"""Build a Mihomo profile on the PC; never installs it or changes router settings.

Inputs are data, parsed with SafeLoader. Subscription credentials are never logged.
"""
from __future__ import annotations

import argparse
import copy
from datetime import datetime, timezone
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import tarfile
from typing import Any
from urllib.parse import unquote, urlparse

import requests
import yaml

ROOT = Path(__file__).resolve().parent
ASIA = r"(?i)(香港|港|HK|Hong Kong|HongKong|日本|东京|大阪|JP|Japan|Tokyo|Osaka|台湾|台|台北|TW|Taiwan|Taipei|新加坡|狮城|SG|Singapore)"
ALIASES = {"DIRECT": "DIRECT", "REJECT": "REJECT", "PROXY": "默认代理", "AIRPORT": "机场入口"}
RESERVED = {"DIRECT", "REJECT", "REJECT-DROP", "PASS", "COMPATIBLE", "GLOBAL"}


class BuildError(Exception):
    """A deliberately redacted, user-facing error."""


class UniqueLoader(yaml.SafeLoader):
    pass


def unique_mapping(loader, node, deep=False):
    explicit = set()
    for key_node, value_node in node.value:
        if key_node.tag == "tag:yaml.org,2002:merge":
            continue
        key = loader.construct_object(key_node, deep=deep)
        try:
            if key in explicit:
                raise BuildError(f"YAML 有重复键，行 {key_node.start_mark.line + 1}")
            explicit.add(key)
        except TypeError:
            raise BuildError("YAML 的映射键必须是简单值") from None
    # YAML merge keys allow explicit fields to override anchor defaults.
    return yaml.SafeLoader.construct_mapping(loader, node, deep=deep)


UniqueLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, unique_mapping)


def load_yaml(raw: bytes | str):
    try:
        return yaml.load(raw, Loader=UniqueLoader)
    except BuildError:
        raise
    except (yaml.YAMLError, UnicodeError, ValueError):
        raise BuildError("不是有效的 YAML/JSON；若返回 Base64/URI 列表，请使用 Clash YAML 链接或设置 UA") from None


def mapping(value, label):
    if not isinstance(value, dict):
        raise BuildError(f"{label} 应为 YAML 映射")
    return value


def seq(value, label):
    if not isinstance(value, list):
        raise BuildError(f"{label} 应为列表")
    return value


def name_ok(value, label):
    if not isinstance(value, str) or not value.strip() or any(c in value for c in "\r\n,"):
        raise BuildError(f"{label} 需要非空名称，不能有逗号或换行")
    return value


def has_placeholder(value):
    if isinstance(value, str):
        return "FILL_" in value or "脱敏" in value or "YOUR_" in value
    if isinstance(value, dict):
        return any(has_placeholder(v) for v in value.values())
    if isinstance(value, list):
        return any(has_placeholder(v) for v in value)
    return False


def env_value(value):
    if isinstance(value, dict):
        return {k: env_value(v) for k, v in value.items()}
    if isinstance(value, list):
        return [env_value(v) for v in value]
    if isinstance(value, str) and re.fullmatch(r"\$\{[A-Za-z_][A-Za-z0-9_]*\}", value):
        key = value[2:-1]
        if key not in os.environ:
            raise BuildError(f"缺少环境变量 {key}")
        return os.environ[key]
    return value


def read_yaml_file(path):
    try:
        return load_yaml(Path(path).read_bytes())
    except OSError:
        raise BuildError("本地输入文件无法读取，请检查配置中的路径") from None


def atomic_write(path: Path, data: bytes):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".building-", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(data)
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)


def write_output_bundle(outputs):
    """Stage every file before replacing any, and roll back a failed replacement."""
    staged, previous, replaced = {}, {}, []
    try:
        for path, data in outputs:
            if path in staged:
                raise BuildError("生成文件的输出路径不能相同")
            path.parent.mkdir(parents=True, exist_ok=True)
            previous[path] = path.read_bytes() if path.exists() else None
            fd, tmp = tempfile.mkstemp(prefix=".building-", dir=path.parent)
            staged[path] = tmp
            with os.fdopen(fd, "wb") as stream:
                stream.write(data)
        for path, _ in outputs:
            os.replace(staged[path], path)
            replaced.append(path)
    except OSError:
        restored = True
        for path in reversed(replaced):
            try:
                if previous[path] is None:
                    path.unlink(missing_ok=True)
                else:
                    atomic_write(path, previous[path])
            except OSError:
                restored = False
        if restored:
            raise BuildError("输出文件写入失败；已保留原有主配置、DNS 文件和报告") from None
        raise BuildError("输出文件写入及回滚未全部成功；请核对主配置、DNS 文件和报告后再导入") from None
    finally:
        for tmp in staged.values():
            if os.path.exists(tmp):
                os.unlink(tmp)


def node_document(raw):
    doc = load_yaml(raw)
    if isinstance(doc, list) and all(isinstance(x, dict) for x in doc):
        doc = {"proxies": doc}
    if isinstance(doc, dict):
        if "proxies" in doc or "proxy-providers" in doc:
            if "proxies" in doc:
                for node in seq(doc["proxies"], "proxies"):
                    mapping(node, "节点")
                    name_ok(node.get("name"), "节点")
                    if not isinstance(node.get("type"), str) or not node["type"]:
                        raise BuildError("节点缺少 type")
            if "proxy-providers" in doc:
                mapping(doc["proxy-providers"], "proxy-providers")
            return doc
        if isinstance(doc.get("payload"), list) and all(isinstance(x, dict) for x in doc["payload"]):
            return node_document(yaml.safe_dump({"proxies": doc["payload"]}))
    raise BuildError("未找到节点列表；支持完整 Clash/Mihomo YAML、proxies/payload 节点文件或节点列表")


class Fetcher:
    def __init__(self, root, options, offline=False):
        self.root = root
        self.options = options
        self.offline = offline
        self.cache = Path(options.get("cache_dir", root / ".cache"))
        self.events = []
        self.session = requests.Session()
        self.session.trust_env = bool(options.get("trust_env", True))
        if options.get("proxy"):
            self.session.proxies.update({"http": options["proxy"], "https": options["proxy"]})

    def resolve(self, path, base=None):
        candidate = Path(str(path)).expanduser()
        return candidate.resolve() if candidate.is_absolute() else ((base or self.root) / candidate).resolve()

    def read(self, spec, parser, label, base=None):
        if bool(spec.get("url")) == bool(spec.get("file")):
            raise BuildError(f"{label} 必须且只能指定 url 或 file 之一")
        if spec.get("file"):
            path = self.resolve(spec["file"], base)
            try:
                raw = path.read_bytes()
            except OSError:
                raise BuildError(f"{label} 的本地文件不可读") from None
            value = parser(raw)
            self.events.append({"item": label, "status": "local", "bytes": len(raw)})
            return value, path.parent
        url = spec["url"]
        if not isinstance(url, str) or urlparse(url).scheme not in {"http", "https"}:
            raise BuildError(f"{label} 的 URL 协议必须为 HTTP(S)")
        headers = {"User-Agent": spec.get("user_agent", self.options.get("user_agent", "clash-verge/v2.4.7"))}
        for key, val in mapping(spec.get("headers", {}), "headers").items():
            headers[str(key)] = ", ".join(map(str, val)) if isinstance(val, list) else str(val)
        key = hashlib.sha256(json.dumps([url, headers], sort_keys=True).encode()).hexdigest()
        cache_file = self.cache / f"{key}.data"
        reason = "offline"
        if self.options.get("prefer_cache") and cache_file.exists():
            try:
                value = parser(cache_file.read_bytes())
                self.events.append({"item": label, "status": "cache"})
                return value, None
            except (BuildError, OSError):
                pass
        if not self.offline:
            try:
                max_bytes = int(self.options.get("max_bytes", 16 * 1024 * 1024))
                with self.session.get(url, headers=headers, stream=True,
                                      timeout=(8, int(self.options.get("timeout", 30)))) as response:
                    if not response.ok:
                        raise BuildError(f"HTTP {response.status_code}")
                    chunks, total = [], 0
                    for block in response.iter_content(65536):
                        total += len(block)
                        if total > max_bytes:
                            raise BuildError("响应超过大小限制")
                        chunks.append(block)
                raw = b"".join(chunks)
                value = parser(raw)  # A malformed download must not poison a working cache.
                atomic_write(cache_file, raw)
                self.events.append({"item": label, "status": "downloaded", "bytes": len(raw)})
                return value, None
            except BuildError as exc:
                reason = str(exc)
            except requests.RequestException:
                reason = "网络连接失败"
        if self.offline or spec.get("allow_stale", self.options.get("allow_stale", True)):
            if cache_file.exists():
                try:
                    value = parser(cache_file.read_bytes())
                    stamp = datetime.fromtimestamp(cache_file.stat().st_mtime, timezone.utc).isoformat()
                    self.events.append({"item": label, "status": "cache", "reason": reason, "cached_at": stamp})
                    return value, None
                except (BuildError, OSError):
                    pass
            if spec.get("fallback_file"):
                path = self.resolve(spec["fallback_file"], base)
                try:
                    value = parser(path.read_bytes())
                except (OSError, BuildError):
                    raise BuildError(f"{label} 下载失败，备用文件也不可用") from None
                self.events.append({"item": label, "status": "fallback_file", "reason": reason})
                return value, path.parent
        raise BuildError(f"{label} 无法读取且没有可用缓存（{reason}）")


def regex(pattern, label):
    try:
        return re.compile(pattern or ".*")
    except (re.error, TypeError):
        raise BuildError(f"{label} 的筛选正则无效（使用 Python 正则语法）") from None


def collect_nodes(doc, fetch, base, label, depth=0, ancestry=()):
    if depth > 4:
        raise BuildError(f"{label} 的 providers 嵌套过深或存在循环")
    result = copy.deepcopy(seq(doc.get("proxies", []), "proxies"))
    for provider_name, provider in mapping(doc.get("proxy-providers", {}), "proxy-providers").items():
        provider = mapping(provider, "provider")
        child_label = f"{label}/provider-{len(result)}"
        typ = provider.get("type", "http" if provider.get("url") else "file")
        child_doc, child_base = None, None
        if typ == "inline":
            child_doc = {"proxies": seq(provider.get("payload", []), "provider payload")}
        else:
            if typ == "http":
                resource = {"url": provider.get("url"), "headers": provider.get("header", {})}
            elif typ == "file" and base is not None:
                target = fetch.resolve(provider.get("path", ""), base)
                if not target.is_relative_to(base.resolve()):
                    raise BuildError(f"{label} 的 file provider 超出所在配置目录")
                resource = {"file": str(target)}
            elif typ == "file":
                raise BuildError(f"{label} 的远程 YAML 引用了本地 file provider；请提供对应本地文件")
            else:
                raise BuildError(f"{label} 的 provider 类型不支持")
            resource_id = json.dumps(resource, sort_keys=True)
            if resource_id in ancestry:
                raise BuildError(f"{label} 的 providers 有循环引用")
            try:
                child_doc, child_base = fetch.read(resource, node_document, child_label, base)
            except BuildError:
                if provider.get("payload") and not fetch.options.get("strict_subscriptions"):
                    child_doc = {"proxies": seq(provider["payload"], "provider payload")}
                    fetch.events.append({"item": child_label, "status": "inline_fallback"})
                else:
                    raise
            ancestry_next = ancestry + (resource_id,)
        if typ == "inline":
            ancestry_next = ancestry
        children = collect_nodes(child_doc, fetch, child_base, child_label, depth + 1, ancestry_next)
        include = regex(provider.get("filter", ".*"), label)
        exclude = regex(provider["exclude-filter"], label) if provider.get("exclude-filter") else None
        override = mapping(provider.get("override", {}), "provider override")
        permitted = {"additional-prefix", "additional-suffix", "udp", "tfo", "mptcp", "skip-cert-verify", "dialer-proxy", "ip-version"}
        if set(override) - permitted:
            raise BuildError(f"{label} 使用了尚未支持的 provider override 字段，不能静默忽略")
        excluded_types = set(str(provider.get("exclude-type", "")).lower().split("|"))
        for node in children:
            node = copy.deepcopy(mapping(node, "node"))
            original = name_ok(node.get("name"), "节点")
            if not include.search(original) or (exclude and exclude.search(original)) or str(node.get("type", "")).lower() in excluded_types:
                continue
            node["name"] = str(override.get("additional-prefix", "")) + original + str(override.get("additional-suffix", ""))
            node.update({k: v for k, v in override.items() if k not in {"additional-prefix", "additional-suffix"}})
            result.append(node)
    if len(result) > 20000:
        raise BuildError(f"{label} 节点数超过 20000")
    return result


def namespace_nodes(nodes, source_name):
    result, used, originals, fingerprints = [], set(), {}, set()
    def notice_last(raw):
        # Some subscriptions reuse a real endpoint for expiry/update notices.
        # Prefer the real route's name when deduplicating identical endpoints.
        # A unique notice endpoint is retained, not dropped by this heuristic.
        return (isinstance(raw, dict) and isinstance(raw.get("name"), str)
                and re.search(r"(?:套餐到期日期|套餐重置日期|订阅获取时间)\s*[:：]", raw["name"]) is not None)

    for raw in sorted(nodes, key=notice_last):
        node = copy.deepcopy(mapping(raw, "节点"))
        original = name_ok(node.get("name"), "节点")
        if not isinstance(node.get("type"), str):
            raise BuildError(f"{source_name} 存在没有 type 的节点")
        signature = hashlib.sha256(json.dumps({k: v for k, v in node.items() if k != "name"}, sort_keys=True).encode()).hexdigest()
        if signature in fingerprints:
            continue
        fingerprints.add(signature)
        stem = f"[{source_name}] {original}"
        renamed, suffix = stem, 2
        while renamed in used:
            renamed = f"{stem} ({suffix})"
            suffix += 1
        used.add(renamed)
        originals.setdefault(original, []).append(renamed)
        node["name"] = renamed
        result.append(node)
    for node in result:
        dialer = node.get("dialer-proxy")
        if dialer and dialer not in RESERVED:
            refs = originals.get(dialer, [])
            if len(refs) != 1:
                raise BuildError(f"{source_name} 节点的 dialer-proxy 引用了机场分组或不唯一的节点；需先整理依赖")
            node["dialer-proxy"] = refs[0]
    return result


def _regex_class_end(text, start):
    i = start + 1
    if i < len(text) and text[i] == "^":
        i += 1
    if i < len(text) and text[i] == "]":
        i += 1
    while i < len(text):
        if text[i] == "\\" and i + 1 < len(text):
            i += 2
            continue
        if text[i:i + 2] == "[:":
            end = text.find(":]", i + 2)
            if end >= 0:
                i = end + 2
                continue
        if text[i] == "]":
            return i
        i += 1
    raise BuildError("正则表达式字符组未闭合")


def split_rule(text):
    parts, depth, start, token_start, regex_depth = [], 0, 0, 0, 0
    regex_payload, i = False, 0
    while i < len(text):
        char = text[i]
        if regex_payload:
            if char == "\\" and i + 1 < len(text):
                if text[i + 1] == "Q":
                    end = text.find(r"\E", i + 2)
                    if end < 0:
                        raise BuildError(r"正则表达式的 \Q 必须用 \E 闭合，避免规则动作进入表达式")
                    i = end + 2
                else:
                    i += 2
                continue
            if char == "[":
                i = _regex_class_end(text, i) + 1
                continue
            if char == "{":
                end = i + 1
                while end < len(text) and "0" <= text[end] <= "9":
                    end += 1
                if end > i + 1:
                    if end < len(text) and text[end] == ",":
                        end += 1
                        while end < len(text) and "0" <= text[end] <= "9":
                            end += 1
                    if end < len(text) and text[end] == "}":
                        i = end + 1
                        continue
            if char == "(":
                regex_depth += 1
                i += 1
                continue
            if char == ")" and regex_depth > 0:
                regex_depth -= 1
                i += 1
                continue
            if char == "," and regex_depth == 0:
                if depth == 0:
                    parts.append(text[start:i].strip())
                    start = i + 1
                i += 1
                continue
            if char != ")" or regex_depth > 0:
                i += 1
                continue
            regex_payload = False
        if char == "(":
            depth += 1
            token_start = i + 1
        elif char == ")":
            depth -= 1
            token_start = i + 1
        elif char == ",":
            regex_payload = text[token_start:i].strip().upper() in {"DOMAIN-REGEX", "PROCESS-NAME-REGEX", "PROCESS-PATH-REGEX"}
            regex_depth, token_start = 0, i + 1
            if depth == 0:
                parts.append(text[start:i].strip())
                start = i + 1
        if depth < 0:
            raise BuildError("规则括号不匹配")
        i += 1
    if depth:
        raise BuildError("规则括号不匹配")
    parts.append(text[start:].strip())
    return parts


RULE_TYPES = {
    "DOMAIN", "DOMAIN-SUFFIX", "DOMAIN-KEYWORD", "DOMAIN-REGEX", "DOMAIN-WILDCARD",
    "GEOSITE", "GEOIP", "IP-CIDR", "IP-CIDR6", "IP-ASN", "IP-SUFFIX",
    "SRC-GEOIP", "SRC-IP-ASN", "SRC-IP-CIDR", "SRC-IP-SUFFIX", "SRC-PORT", "DST-PORT", "IN-PORT",
    "PROCESS-NAME", "PROCESS-PATH", "PROCESS-NAME-REGEX", "PROCESS-PATH-REGEX",
    "PROCESS-NAME-WILDCARD", "PROCESS-PATH-WILDCARD", "REMATCH-NAME",
    "NETWORK", "DSCP", "IN-TYPE", "IN-USER", "IN-NAME", "AND", "OR", "NOT", "UID",
}
REGEX_RULE_TYPES = {"DOMAIN-REGEX", "PROCESS-NAME-REGEX", "PROCESS-PATH-REGEX"}


def split_full_rule(text):
    first = text.find(",")
    if first >= 0 and text[:first].strip().upper() in REGEX_RULE_TYPES:
        last = text.rfind(",")
        if last == first or last == len(text) - 1:
            raise BuildError("规则缺少目标动作")
        return [text[:first].strip().upper(), text[first + 1:last].strip(), text[last + 1:].strip()]
    return split_rule(text)


def rule_input_format(spec, fallback="auto"):
    value = spec.get("rule_format", fallback)
    if value not in {"auto", "match", "rule"}:
        raise BuildError("rule_format 只支持 auto、match 或 rule")
    return value


def rule_with_action(raw, action, bare_domain_suffix=True, input_format="auto"):
    if isinstance(raw, dict):
        if len(raw) != 1 or not set(raw) <= {"match", "rule"}:
            raise BuildError("规则对象应只包含 match（匹配条件）或 rule（带动作的完整规则）")
        input_format, raw = next(iter(raw.items()))
    if not isinstance(raw, str) or not raw.strip() or "\n" in raw or "\r" in raw:
        raise BuildError("规则必须是非空单行字符串")
    text = raw.strip()
    if input_format not in {"auto", "match", "rule"}:
        raise BuildError("规则输入格式无效")
    if "," not in text:
        try:
            net = ipaddress.ip_network(text, strict=False)
            return f"{'IP-CIDR6' if net.version == 6 else 'IP-CIDR'},{net},{action},no-resolve"
        except ValueError:
            kind = "DOMAIN-SUFFIX" if bare_domain_suffix or text.startswith(("+.", ".")) else "DOMAIN"
            if text.startswith("*."):
                kind = "DOMAIN-WILDCARD"
            domain = text[2:] if text.startswith("+.") else text.lstrip(".")
            if not domain or re.search(r"[\s/:]", domain):
                raise BuildError("域名规则格式不正确")
            return f"{kind},{domain},{action}"
    typ = text.split(",", 1)[0].strip().upper()
    if typ not in RULE_TYPES:
        raise BuildError("不支持的规则类型；名单不接受 MATCH/FINAL/RULE-SET，避免意外接管全部流量")
    if typ in REGEX_RULE_TYPES:
        if input_format == "auto" and len(split_rule(text)) > 2:
            raise BuildError("正则含顶层逗号，无法区分表达式与动作；请使用 {match: '匹配条件'} 或 {rule: '完整规则'}，远程名单可指定 rule_format")
        payload = split_full_rule(text)[1] if input_format == "rule" else text.split(",", 1)[1].strip()
        if not payload:
            raise BuildError("规则缺少匹配条件")
        return ",".join([typ, payload, action])
    parts = split_rule(text)
    if len(parts) < 2 or not parts[1]:
        raise BuildError("规则缺少匹配条件")
    extra = parts[2:]
    flags = [x for x in extra if x in {"no-resolve", "src"}]
    policies = [x for x in extra if x not in {"no-resolve", "src"}]
    if len(policies) > 1:
        raise BuildError("规则字段过多；请检查逗号或条件格式")
    return ",".join([typ, parts[1], action] + flags)


def domain_provider_rule(text):
    labels = text.lower().split(".")
    suffix, subdomains = labels[0] == "+", labels[0] == ""
    body = labels[1:] if suffix or subdomains else labels
    if not body or any(not x or x != "*" and not re.fullmatch(r"[\w-]+", x) for x in body):
        raise BuildError("domain 名单格式无效；* 必须独占一个域名层级，+ 或空前缀只能位于开头")
    if "*" not in body and not subdomains:
        return f"{'DOMAIN-SUFFIX' if suffix else 'DOMAIN'},{'.'.join(body)},DIRECT"
    def escape(value):
        return "".join("\\" + c if c in r"\.+*?()|[]{}^$" else c for c in value)
    prefix = r"(?:[^.]+\.)*" if suffix else r"(?:[^.]+\.)+" if subdomains else ""
    pattern = "(?i)^" + prefix + r"\.".join("[^.]+" if x == "*" else escape(x) for x in body) + "$"
    return "DOMAIN-REGEX," + pattern + ",DIRECT"


def remote_rule_parser(spec):
    def parse(raw):
        input_format = rule_input_format(spec)
        fmt = spec.get("format", "auto")
        if fmt == "auto":
            # Inspect content, not the URL suffix (URLs may contain query strings).
            try:
                decoded = raw.decode("utf-8-sig") if isinstance(raw, bytes) else raw
            except UnicodeError:
                raise BuildError("远程规则不是 UTF-8 文本") from None
            significant = next((x.strip() for x in decoded.splitlines()
                                if x.strip() and not x.lstrip().startswith(("#", "//", ";"))), "")
            if significant.startswith("<"):
                raise BuildError("远程规则返回了 HTML/XML，未取得规则名单")
            fmt = "yaml" if re.match(r"^(payload\s*:|rules\s*:|---(?:\s|$)|[\[{]|-\s)", significant) else "text"
        if fmt == "yaml":
            doc = load_yaml(raw)
            lines = doc.get("payload", doc.get("rules")) if isinstance(doc, dict) else doc
            lines = seq(lines, "远程规则 payload")
            if "rule_format" not in spec:
                input_format = "rule" if isinstance(doc, dict) and "payload" not in doc and "rules" in doc else "match"
        elif fmt == "text":
            try:
                lines = raw.decode("utf-8-sig").splitlines()
            except UnicodeError:
                raise BuildError("远程规则不是 UTF-8 文本") from None
        else:
            raise BuildError("电脑端内嵌规则支持 yaml/text，不直接解码 mrs")
        behavior = spec.get("behavior", "auto")
        if behavior not in {"auto", "classical", "domain", "ipcidr"}:
            raise BuildError("远程规则 behavior 只支持 auto、classical、domain 或 ipcidr")
        entries = []
        for line in lines:
            if not isinstance(line, str):
                raise BuildError("远程规则包含非字符串项")
            line = line.strip()
            if line and not line.startswith(("#", "//", ";")):
                entries.append(line)
        if not entries:
            raise BuildError("远程规则集为空")
        if behavior == "auto":
            # Inspect the whole list. Explicit behavior takes precedence, and
            # typed rules or IP/mixed lists retain classical handling.
            def domain_entry(line):
                if "," in line:
                    return False
                try:
                    ipaddress.ip_network(line, strict=False)
                    return False
                except ValueError:
                    return True
            behavior = "domain" if all(domain_entry(line) for line in entries) else "classical"
        result = []
        for line in entries:
            if behavior == "ipcidr":
                try:
                    network = ipaddress.ip_network(line, strict=False)
                except ValueError:
                    raise BuildError("ipcidr 规则包含非 IP 网段") from None
            if behavior == "domain" and "," in line:
                raise BuildError("domain 规则应只包含域名")
            # Validate before caching. The placeholder action is replaced by the caller.
            if behavior == "domain":
                result.append(domain_provider_rule(line))
            elif behavior == "ipcidr":
                no_resolve = spec.get("no-resolve", False)
                if type(no_resolve) is not bool:
                    raise BuildError("no-resolve 必须为布尔值")
                result.append(f"{'IP-CIDR6' if network.version == 6 else 'IP-CIDR'},{network},DIRECT" + (",no-resolve" if no_resolve else ""))
            else:
                result.append(rule_with_action(line, "DIRECT", input_format=input_format))
        return result
    return parse


def remote_rule_specs(entries):
    """Accept ordered YAML shorthand and the existing detailed entry format."""
    shortcuts = {"direct": "DIRECT", "reject": "REJECT", "proxy": "PROXY"}
    if entries is None:
        return
    for number, raw in enumerate(seq(entries, "remote_rules（使用带 - 的 YAML 列表）"), 1):
        entry = mapping(raw, "remote rule")
        keys = set(entry) & set(shortcuts)
        if keys:
            if len(entry) != 1:
                raise BuildError("每个远程规则列表项只能有一个 direct/reject/proxy 动作；多个动作请分行加 -")
            key = next(iter(keys))
            urls = entry[key] if isinstance(entry[key], list) else [entry[key]]
            for offset, url in enumerate(urls, 1):
                if not isinstance(url, str) or urlparse(url).scheme not in {"http", "https"}:
                    raise BuildError(f"remote_rules 第 {number} 项应填写 HTTP(S) 规则 URL")
                yield {"name": f"remote[{number}].{key}[{offset}]", "url": url,
                       "action": shortcuts[key], "enabled": True}
        else:
            if "priority" in entry:
                raise BuildError("远程规则不使用 priority：请调整 remote_rules 中的上下顺序")
            yield {"name": f"remote[{number}]", **entry}


def canonical_rule(rule):
    """Normalize only well-defined equivalences; flags remain part of identity."""
    parts = split_full_rule(rule)
    if parts[0] == "MATCH":
        return rule, ("MATCH",), parts[1]
    kind, value, target = parts[:3]
    if kind in {"DOMAIN", "DOMAIN-SUFFIX"}:
        value = value.lower()
    elif kind in {"IP-CIDR", "IP-CIDR6", "SRC-IP-CIDR"}:
        try:
            network = ipaddress.ip_network(value, strict=False)
        except ValueError:
            raise BuildError("IP 网段规则格式不正确") from None
        value = str(network)
        if kind != "SRC-IP-CIDR":
            kind = "IP-CIDR6" if network.version == 6 else "IP-CIDR"
    flags = sorted(set(parts[3:]))
    return ",".join([kind, value, target] + flags), (kind, value, *flags), target


class RuleOverlapIndex:
    """Prefix tries: earliest rule per action, avoiding an all-pairs scan."""
    def __init__(self):
        self.roots = {}

    @staticmethod
    def descriptor(key):
        if len(key) < 2:
            return None
        kind, value, *flags = key
        if kind in {"DOMAIN", "DOMAIN-SUFFIX"}:
            return ("domain", tuple(flags)), value.split(".")[::-1], kind == "DOMAIN-SUFFIX"
        if kind in {"IP-CIDR", "IP-CIDR6", "SRC-IP-CIDR"}:
            network = ipaddress.ip_network(value)
            bits = format(int(network.network_address), f"0{network.max_prefixlen}b")[:network.prefixlen]
            return ("ip", network.version, kind == "SRC-IP-CIDR", tuple(flags)), bits, True
        return None

    @staticmethod
    def node():
        return {"children": {}, "cover": {}, "exact": {}, "subtree": {}}

    @staticmethod
    def collect(found, entries):
        for target, entry in entries.items():
            if target not in found or entry["position"] < found[target]["position"]:
                found[target] = entry

    def overlaps(self, key):
        desc = self.descriptor(key)
        if desc is None:
            return []
        family, path, broad = desc
        node, found = self.roots.get(family), {}
        if node is None:
            return []
        self.collect(found, node["cover"])
        for part in path:
            node = node["children"].get(part)
            if node is None:
                return list(found.values())
            self.collect(found, node["cover"])
        self.collect(found, node["subtree"] if broad else node["exact"])
        return list(found.values())

    def add(self, key, entry):
        desc = self.descriptor(key)
        if desc is None:
            return
        family, path, broad = desc
        node = self.roots.setdefault(family, self.node())
        node["subtree"].setdefault(entry["action"], entry)
        for part in path:
            node = node["children"].setdefault(part, self.node())
            node["subtree"].setdefault(entry["action"], entry)
        node["cover" if broad else "exact"].setdefault(entry["action"], entry)


def merge_rules(candidates):
    """Deduplicate only fixed domain predicates with fixed built-in actions."""
    report = {"precedence": "first_listed", "input_count": len(candidates),
              "duplicates_removed": 0, "duplicates_preserved": 0, "conflicts_resolved": 0, "conflicts_preserved": 0, "overlap_notices": 0,
              "duplicates": [], "preserved_duplicates": [], "conflicts": [], "overlaps": [], "detail_limit": 100,
              "overlap_detection": "DOMAIN/DOMAIN-SUFFIX and CIDR with identical flags; other expressions are not expanded"}
    seen, output, index = {}, [], RuleOverlapIndex()

    def brief(entry):
        return {k: entry[k] for k in ("source", "stage", "rule", "position")}

    def note(kind, counter, earlier, later, resolution):
        report[counter] += 1
        if len(report[kind]) < report["detail_limit"]:
            report[kind].append({"earlier": brief(earlier), "later": brief(later), "resolution": resolution})

    for position, raw in enumerate(candidates, 1):
        rule, key, target = canonical_rule(raw["rule"])
        entry = {**raw, "rule": rule, "action": target, "position": position}
        actions = seen.setdefault(key, {})
        previous = actions.get(target)
        if previous is not None:
            stable = key[0] in {"DOMAIN", "DOMAIN-SUFFIX", "DOMAIN-KEYWORD", "DOMAIN-REGEX", "DOMAIN-WILDCARD", "GEOSITE"} and target in {"DIRECT", "REJECT", "REJECT-DROP", "PASS"}
            if stable:
                note("duplicates", "duplicates_removed", previous, entry, "same_condition_and_action; keep_earlier")
                continue
            note("preserved_duplicates", "duplicates_preserved", previous, entry, "matching_or_action_may_change; keep_both")
        elif actions:
            note("conflicts", "conflicts_preserved", next(iter(actions.values())), entry, "same_condition; keep_both; core_decides")
        for other in index.overlaps(key):
            if other["action"] != target and canonical_rule(other["rule"])[1] != key:
                note("overlaps", "overlap_notices", other, entry, "keep_both; earlier_rule_first_on_overlap")
        index.add(key, entry)
        actions[target] = entry
        output.append(rule)
    report["output_count"] = len(output)
    report["details_truncated"] = any(report[count] > len(report[kind]) for count, kind in (
        ("duplicates_removed", "duplicates"), ("duplicates_preserved", "preserved_duplicates"), ("conflicts_preserved", "conflicts"), ("overlap_notices", "overlaps")))
    return output, report


def check_graph(config):
    elements = config["proxies"] + config["proxy-groups"]
    names = [x["name"] for x in elements]
    if len(names) != len(set(names)) or set(names) & RESERVED:
        raise BuildError("生成结果中节点或分组重名，或使用了保留名称")
    graph = {name: [] for name in names}
    for obj in elements:
        refs = obj.get("proxies", []) + ([obj["dialer-proxy"]] if obj.get("dialer-proxy") else [])
        for ref in refs:
            if ref in graph:
                graph[obj["name"]].append(ref)
            elif ref not in RESERVED:
                raise BuildError("生成配置存在未知的节点/分组引用")
    visited, active = set(), set()

    def walk(node):
        if node in active:
            raise BuildError("生成配置存在代理链循环")
        if node in visited:
            return
        active.add(node)
        for target in graph[node]:
            walk(target)
        active.remove(node)
        visited.add(node)

    for name in names:
        walk(name)


def validate_dns(config):
    """Catch a stale proxy name before Mihomo treats it as an interface name."""
    if "dns" not in config:
        return []
    dns = mapping(config["dns"], "policy.dns")
    endpoints = []
    for key in ("default-nameserver", "proxy-server-nameserver", "nameserver", "fallback", "direct-nameserver"):
        if key in dns:
            endpoints.extend(seq(dns[key], f"policy.dns.{key}"))
    for key in ("nameserver-policy", "proxy-server-nameserver-policy"):
        for values in mapping(dns.get(key, {}), f"policy.dns.{key}").values():
            endpoints.extend([values] if isinstance(values, str) else seq(values, f"policy.dns.{key}"))
    names = {p["name"] for p in config["proxies"] + config["proxy-groups"]} | RESERVED
    references = set()
    for endpoint in endpoints:
        if not isinstance(endpoint, str) or not endpoint.strip():
            raise BuildError("DNS 服务器地址必须是非空字符串")
        for option in urlparse(endpoint).fragment.split("&"):
            option = unquote(option)
            if (not option or "=" in option or option in {"h3", "skip-cert-verify", "ecs-override", "disable-ipv4", "disable-ipv6"}
                    or re.fullmatch(r"disable-qtype-\d+", option)):
                continue
            if option not in names and option != "RULES":
                raise BuildError(f"DNS 引用了不存在的代理/分组：{option}；请核对 policy.yaml 中 # 后的名称")
            references.add(option)
    if dns.get("enable", False) and (references - {"DIRECT"} or dns.get("respect-rules", False)):
        if not dns.get("proxy-server-nameserver"):
            raise BuildError("DNS 经代理发送时，需要配置 proxy-server-nameserver 以解析代理服务器域名")
    return sorted(references)


def apply_dns_routing(config, policy, candidates):
    enabled = policy.get("dns_follow_rules", False)
    if not isinstance(enabled, bool):
        raise BuildError("dns_follow_rules 必须为布尔值")
    if not enabled:
        return None
    dns = config.get("dns", {})
    if not dns.get("enable", False):
        return {"enabled": False, "reason": "dns_disabled"}
    direct = dns.get("direct-nameserver", [])
    proxy = dns.get("nameserver", []) + dns.get("fallback", [])
    if not direct or not proxy:
        raise BuildError("dns_follow_rules 需要 direct-nameserver 和 nameserver/fallback，分别指定直连与代理域名的 DNS")
    prefix = "HomeChain-DNS-"
    providers = config.get("rule-providers", {})
    if any(k.startswith(prefix) for k in providers):
        raise BuildError("rule-providers 的 HomeChain-DNS- 前缀由 DNS 规则生成器保留")

    def domain_only(condition):
        parts = split_rule(condition)
        if parts[0] in {"DOMAIN", "DOMAIN-SUFFIX", "DOMAIN-KEYWORD", "DOMAIN-REGEX", "DOMAIN-WILDCARD", "GEOSITE"}:
            return True
        if parts[0] not in {"AND", "OR", "NOT"} or len(parts) != 2:
            return False
        body = parts[1]
        if not (body.startswith("(") and body.endswith(")")):
            return False
        children = split_rule(body[1:-1])
        return bool(children) and all(c.startswith("(") and c.endswith(")") and domain_only(c[1:-1]) for c in children)

    ordered, skipped = {}, []
    previous, payload = "", None
    groups = projected = passed = 0
    for entry in candidates:
        if entry["stage"] in {"baseline", "final"}:
            continue
        parts = split_full_rule(entry["rule"])
        action = parts[1] if parts[0] == "MATCH" else parts[2]
        if action == "PASS":
            passed += 1
            continue
        condition = "DOMAIN-REGEX,^.*$" if parts[0] == "MATCH" else ",".join(parts[:2])
        if not domain_only(condition):
            skipped.append({"rule": entry["rule"], "source": entry["source"], "reason": "requires_connection_context"})
            continue
        category = "direct" if action == "DIRECT" else "proxy"
        if category != previous:
            groups += 1
            name = f"{prefix}{groups:04d}-{category}"
            payload = []
            providers[name] = {"type": "inline", "behavior": "classical", "payload": payload}
            ordered["rule-set:" + name] = copy.deepcopy(direct if category == "direct" else proxy)
            previous = category
        payload.append(condition)
        projected += 1
    for key, value in dns.get("nameserver-policy", {}).items():
        if key.startswith("rule-set:" + prefix):
            raise BuildError("nameserver-policy 的 HomeChain-DNS- 引用由生成器管理，请勿复制生成结果回输入")
        ordered[key] = copy.deepcopy(value)
    if groups:
        config["rule-providers"] = providers
    dns["nameserver-policy"] = ordered
    return {"enabled": True, "precedence": "user_domain_rules_then_dns_policy_then_default", "projected_rules": projected,
            "provider_count": groups, "pass_skipped": passed, "connection_rules_skipped": skipped,
            "scope": "Domain-only conditions in first/manual/remote/imported/supplemental order. Connection-dependent rules stay in traffic routing; DIRECT re-resolves via direct-nameserver."}


def mc2_dns_document(config):
    if "dns" not in config:
        return None
    dns = copy.deepcopy(config["dns"])
    # These are required by the installed MC2 1.2.2 Redir-Host integration.
    # MC2 dnsmasq.postconf extracts the third colon-separated field via xargs.
    # Keep this address plain in emitted YAML; quoted addresses break that script.
    dns["listen"] = "0.0.0.0:23453"
    dns["enhanced-mode"] = "redir-host"
    return {"dns": dns}


def yaml_bytes(document, comment=""):
    return (comment + yaml.safe_dump(document, allow_unicode=True, sort_keys=False, width=120)).encode("utf-8")


def validate_with_core(config, exe, workdir, logpath):
    fd, pending = tempfile.mkstemp(prefix="config-", suffix=".yaml", dir=workdir)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(yaml_bytes(config))
        kwargs = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {}
        run = subprocess.run([str(exe.resolve()), "-t", "-d", str(workdir), "-f", pending],
                             capture_output=True, timeout=90, **kwargs)
        atomic_write(logpath, run.stdout + run.stderr)
        if run.returncode:
            raise BuildError(f"Mihomo 校验失败，旧输出已保留；详细日志在 .cache/{logpath.name}（不要直接公开日志）")
    except (OSError, subprocess.TimeoutExpired):
        raise BuildError("Mihomo 校验未完成，旧输出已保留") from None
    finally:
        if os.path.exists(pending):
            os.unlink(pending)


def embed_geosite_sources(config, sources, fetch, base):
    """Replace selected top-level GEOSITE rules in place with self-contained lists."""
    summaries = []
    for tag, spec in mapping(sources, "policy.geosite_sources").items():
        name_ok(tag, "geosite tag")
        spec = {"url": spec} if isinstance(spec, str) else mapping(spec, "geosite source")
        positions = [i for i, rule in enumerate(config["rules"])
                     if rule.startswith("GEOSITE,") and split_rule(rule)[1].lower() == tag.lower()]
        if not positions:
            continue
        entries, _ = fetch.read(spec, remote_rule_parser(spec), f"GeoSite·{tag}", base=base)
        payload = []
        for entry in entries:
            parts = split_full_rule(entry)
            if parts[0] not in {"DOMAIN", "DOMAIN-SUFFIX", "DOMAIN-KEYWORD", "DOMAIN-REGEX", "DOMAIN-WILDCARD"}:
                raise BuildError("geosite_sources 只接受域名类规则，不能改变 GeoSite 的匹配范围")
            payload.append(",".join(parts[:2]))
        payload = list(dict.fromkeys(payload))
        name = "HomeChain-GeoSite-" + tag
        config.setdefault("rule-providers", {})[name] = {
            "type": "inline", "behavior": "classical", "payload": payload}
        for i in positions:
            parts = split_rule(config["rules"][i])
            config["rules"][i] = ",".join(["RULE-SET", name, *parts[2:]])
        summaries.append({"tag": tag, "provider": name, "entries": len(payload), "positions": positions})
    return summaries


def required_geodata(config):
    text = json.dumps([config.get(k) for k in ("rules", "sub-rules", "rule-providers")])
    dns = json.dumps(config.get("dns", {})).lower()
    files = set()
    if re.search(r"\bGEOSITE,", text, re.I) or "geosite:" in dns:
        files.add("GeoSite.dat")
    if re.search(r"\b(?:SRC-)?GEOIP,", text, re.I) or config.get("dns", {}).get("fallback-filter", {}).get("geoip", False):
        files.add("GeoIP.dat" if config.get("geodata-mode", False) else "country.mmdb")
    if re.search(r"\b(?:SRC-)?IP-ASN,", text, re.I):
        files.add("ASN.mmdb")
    return sorted(files)


def validate_mc2_package(config, dns_doc, exe, package, logpath, native_data_dir=None):
    """Validate the package header with stock or explicitly declared external databases."""
    if not dns_doc:
        raise BuildError("MC2 安装包校验需要 policy.dns")
    try:
        with tempfile.TemporaryDirectory(prefix="mc2-check-") as folder, tarfile.open(package) as archive:
            workdir = Path(folder)
            assets = {}
            for name in ("GeoIP.dat", "GeoSite.dat"):
                raw = archive.extractfile("merlinclash/clash/" + name).read()
                (workdir / name).write_bytes(raw)
                assets[name] = {"bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}
            merged = mapping(load_yaml(archive.extractfile("merlinclash/yaml_basic/head.yaml").read()), "MC2 head")
            for key in ("proxies", "proxy-providers", "proxy-groups", "rule-providers", "rules", "sub-rules"):
                if key in config:
                    merged[key] = config[key]
            merged.update(dns_doc)
            required_assets = {}
            if native_data_dir is not None:
                for name in required_geodata(merged):
                    source = Path(native_data_dir) / name
                    if not source.is_file():
                        raise BuildError(f"原生 GEO 的 MC2 校验需要 {name}；请补齐校验数据库")
                    raw = source.read_bytes()
                    (workdir / name).write_bytes(raw)
                    required_assets[name] = {"bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}
            validate_with_core(merged, exe, workdir, logpath)
            # A PC download must not hide a router startup dependency.
            downloaded = [p.name for p in workdir.glob("*") if p.name not in assets and p.name not in required_assets]
            if downloaded:
                raise BuildError("MC2 校验触发额外文件下载（" + ", ".join(downloaded) +
                                 "）；请补齐数据库依赖，旧输出已保留")
            return {"status": "passed_with_geodata" if required_assets else "passed",
                    "scope": "MC2 package header + external databases + imported fields + DNS; install/update these databases on the router" if required_assets else "MC2 package header + stock databases + imported fields + DNS",
                    "database_mode": "external" if required_assets else "stock", "required_assets": required_assets,
                    "stock_assets": assets, "additional_core_downloads": downloaded,
                    "router_runtime_tested": False}
    except (OSError, tarfile.TarError, KeyError, AttributeError):
        raise BuildError("MC2 安装包读取失败，请检查 validation.mc2_package") from None


def embed_asn_sources(config, sources, fetch, base):
    summaries = []
    for number, spec in mapping(sources, "policy.asn_sources").items():
        number = str(number)
        if not number.isdigit():
            raise BuildError("asn_sources 的键必须是 ASN 编号")
        positions = [i for i, rule in enumerate(config["rules"])
                     if rule.startswith("IP-ASN," + number + ",")]
        if not positions:
            continue
        spec = {"url": spec} if isinstance(spec, str) else dict(mapping(spec, "ASN source"))
        spec["behavior"] = "ipcidr"
        entries, _ = fetch.read(spec, remote_rule_parser(spec), f"ASN·{number}", base=base)
        payload = list(dict.fromkeys(split_rule(entry)[1] for entry in entries))
        name = "HomeChain-ASN-" + number
        config.setdefault("rule-providers", {})[name] = {
            "type": "inline", "behavior": "ipcidr", "payload": payload}
        for i in positions:
            parts = split_rule(config["rules"][i])
            if "src" in parts[3:]:
                raise BuildError("asn_sources 暂不支持 src 标志，不能改变匹配方向")
            config["rules"][i] = ",".join(["RULE-SET", name, *parts[2:]])
        summaries.append({"asn": number, "provider": name, "entries": len(payload), "positions": positions})
    return summaries


def compile_profile(settings, root, offline=False):
    settings = env_value(mapping(settings, "settings"))
    if "remote_rules" in settings:
        raise BuildError("规则入口已统一到 policy.yaml：请将 settings.yaml 中的 remote_rules 整段移到 policy_file 指定的文件")
    if "dns" in mapping(settings.get("runtime", {}), "runtime"):
        raise BuildError("DNS 策略已统一到 policy.yaml：请将 runtime.dns 移到 policy.yaml 的顶层 dns")
    fetch = Fetcher(root, mapping(settings.get("http", {}), "http"), offline)
    policy_path = fetch.resolve(settings.get("policy_file", "policy.yaml"))
    policy = env_value(mapping(read_yaml_file(policy_path), "policy"))
    geo_mode = policy.get("geo_mode", "native")
    if geo_mode not in ("native", "mc2-inline"):
        raise BuildError("geo_mode 只支持 native 或 mc2-inline")
    # Validate private local inputs before consuming an activated subscription window.
    residence_doc = env_value(read_yaml_file(fetch.resolve(settings.get("residential_file", "residential.yaml"))))
    residences = copy.deepcopy(seq(residence_doc.get("proxies") if isinstance(residence_doc, dict) else residence_doc, "住宅节点"))
    if not residences:
        raise BuildError("住宅列表不能为空")
    for node in residences:
        mapping(node, "住宅节点")
        name_ok(node.get("name"), "住宅节点")
        if has_placeholder(node):
            raise BuildError("住宅节点仍有 FILL_/YOUR_/脱敏占位符，请先在本地填写")
        if not node.get("type") or not isinstance(node.get("server"), str) or not node["server"].strip():
            raise BuildError("住宅节点需要 type 和 server")
        if type(node.get("port")) is not int or not 1 <= node["port"] <= 65535:
            raise BuildError("住宅节点端口必须在 1 至 65535")
        node["dialer-proxy"] = "机场入口"
    selection = mapping(settings.get("selection", {}), "selection")
    sources = seq(settings.get("sources", []), "sources")
    proxies, source_groups, tiers, summaries, all_eligible, seen_sources = [], [], {}, [], [], set()
    interval = int(selection.get("interval", 300))
    timeout = int(selection.get("timeout", 5000))
    tolerance = int(selection.get("tolerance", 50))
    if interval < 1 or timeout < 1 or tolerance < 0:
        raise BuildError("健康检查 interval/timeout 应为正数，tolerance 不能为负数")
    health = {"url": selection.get("test_url", "https://www.gstatic.com/generate_204"),
              "interval": interval, "timeout": timeout, "lazy": False, "expected-status": 204}
    for source in sources:
        source = mapping(source, "source")
        if not source.get("enabled", True):
            continue
        name = name_ok(source.get("name"), "订阅")
        if name in seen_sources:
            raise BuildError("订阅名称不能重复")
        seen_sources.add(name)
        priority = source.get("priority", 100)
        if type(priority) is not int:
            raise BuildError(f"{name} 的 priority 必须为整数，越小越优先")
        try:
            doc, base = fetch.read(source, node_document, name)
            nodes = namespace_nodes(collect_nodes(doc, fetch, base, name), name)
        except BuildError as exc:
            if source.get("on_error", "abort") == "skip":
                summaries.append({"source": name, "status": "skipped", "reason": str(exc)})
                continue
            raise
        include = regex(source.get("include", selection.get("include", ASIA)), name)
        exclude = regex(source.get("exclude", selection.get("exclude", "")), name) if source.get("exclude", selection.get("exclude")) else None
        prefix_len = len(f"[{name}] ")
        eligible = [n["name"] for n in nodes if include.search(n["name"][prefix_len:]) and not (exclude and exclude.search(n["name"][prefix_len:]))]
        if not eligible:
            raise BuildError(f"{name} 没有节点匹配入口筛选；请调整 include/exclude")
        proxies.extend(nodes)
        all_eligible.extend(eligible)
        tiers.setdefault(priority, []).extend(eligible)
        source_groups.append({"name": f"订阅·{name}", "type": "url-test", "proxies": eligible,
                              **health, "tolerance": tolerance})
        summaries.append({"source": name, "priority": priority, "nodes": len(nodes), "eligible": len(eligible)})
    if not proxies:
        raise BuildError("没有可用的已启用订阅")
    tier_groups = [{"name": f"优先级·{level}", "type": "url-test", "proxies": members,
                    **health, "tolerance": tolerance} for level, members in sorted(tiers.items())]
    groups = [
        {"name": "默认代理", "type": "select", "proxies": ["住宅出口", "机场入口", "DIRECT"]},
        {"name": "住宅出口", "type": "select", "proxies": [n["name"] for n in residences]},
        {"name": "机场入口", "type": "select", "proxies": ["订阅优先", "机场手选"] + [g["name"] for g in source_groups]},
        {"name": "订阅优先", "type": "fallback", "proxies": [g["name"] for g in tier_groups], **health},
        {"name": "机场手选", "type": "select", "proxies": all_eligible},
        *tier_groups, *source_groups,
    ]
    valid_actions = {g["name"] for g in groups} | {p["name"] for p in proxies + residences} | RESERVED

    def action(value):
        value = ALIASES.get(value, value)
        if value not in valid_actions:
            raise BuildError("规则 action 引用了不存在的策略")
        return value

    candidates = []

    def add_rule(rule, source, stage):
        candidates.append({"rule": rule, "source": source, "stage": stage})

    for number, entry in enumerate(seq(policy.get("first", []), "policy.first"), 1):
        entry = mapping(entry, "first rule")
        add_rule(rule_with_action(entry.get("match"), action(entry.get("action", "PROXY")), input_format="match"), f"first[{number}]", "first")
    order = policy.get("order", ["direct", "reject", "proxy"])
    if not isinstance(order, list) or len(order) != 3 or set(order) != {"direct", "reject", "proxy"}:
        raise BuildError("policy.order 必须恰好包含 direct、reject、proxy 各一次")
    for group in order:
        target = action({"direct": "DIRECT", "reject": "REJECT", "proxy": "PROXY"}[group])
        for rule in seq(policy.get(group, []), group):
            add_rule(rule_with_action(rule, target), f"manual.{group}", "manual")
    remote_summaries, remote_names = [], set()
    for remote in remote_rule_specs(policy.get("remote_rules", [])):
        if not remote.get("enabled", True):
            continue
        name = name_ok(remote.get("name"), "远程规则集")
        if name in remote_names:
            raise BuildError("启用的远程规则集名称不能重复")
        remote_names.add(name)
        target = action(remote.get("action", "PROXY"))
        entries, _ = fetch.read(remote, remote_rule_parser(remote), f"规则·{name}", base=policy_path.parent)
        for entry in entries:
            add_rule(rule_with_action(entry, target, input_format="rule"), name, "remote")
        remote_summaries.append({"name": name, "rules": len(entries), "action": target})
    imported_ids = set()
    for raw_set in policy.get("rule_sets", []):
        rule_set = mapping(raw_set, "规则集")
        set_id = rule_set.get("id")
        if not isinstance(set_id, str) or not set_id.strip() or set_id in imported_ids:
            raise BuildError("规则集 ID 为空或重复")
        imported_ids.add(set_id)
        if not rule_set.get("enabled", True):
            continue
        for number, raw in enumerate(rule_set.get("rules", []), 1):
            entry = mapping(raw, "导入规则")
            match = entry.get("match", "")
            parts = split_rule(match)
            fallback = match == "MATCH" or len(parts) >= 2 and (parts[0] == "GEOIP" and parts[1].upper() == "CN" or parts[0] == "GEOSITE" and (parts[1].upper() == "CN" or parts[1] == "geolocation-!cn"))
            if fallback and not rule_set.get("include_fallback", False):
                continue
            target = rule_set.get("mappings", {}).get(entry.get("source_action"), "")
            if not target:
                raise BuildError("规则集尚有未映射策略")
            target = action(target)
            rule = "MATCH," + target if match == "MATCH" else rule_with_action(match, target, input_format="match")
            add_rule(rule, f"rule-set:{set_id}[{number}]", "imported")
    for number, raw in enumerate(policy.get("supplemental_rules", []), 1):
        entry = mapping(raw, "补充规则")
        add_rule(rule_with_action(entry.get("match"), action(entry.get("action", "PROXY")), input_format="match"), f"supplemental[{number}]", "supplemental")
    if policy.get("domestic_direct", True):
        for rule in ["GEOSITE,geolocation-!cn," + action("PROXY"), "GEOSITE,CN,DIRECT", "GEOIP,CN,DIRECT"]:
            add_rule(rule, "domestic", "baseline")
    add_rule("MATCH," + action(policy.get("final", "PROXY")), "final", "final")
    rules, merge_report = merge_rules(candidates)
    runtime = copy.deepcopy(mapping(settings.get("runtime", {}), "runtime"))
    forbidden = {"proxies", "proxy-providers", "proxy-groups", "rules", "rule-providers"} & set(runtime)
    if forbidden:
        raise BuildError("runtime 不能覆盖生成器管理的代理、分组和规则字段")
    config = {"mixed-port": 7890, "allow-lan": False, "mode": "rule", "log-level": "info",
              "ipv6": False, "geodata-mode": True, "geodata-loader": "memconservative",
              "profile": {"store-selected": True}, **runtime,
              "proxies": proxies + residences, "proxy-groups": groups, "rules": rules}
    if policy.get("dns") is not None:
        config["dns"] = copy.deepcopy(mapping(policy["dns"], "policy.dns"))
    dns_routing = apply_dns_routing(config, policy, candidates)
    embedded_geosite = embed_geosite_sources(config, policy.get("geosite_sources", {}), fetch, policy_path.parent) if geo_mode == "mc2-inline" else []
    embedded_asn = embed_asn_sources(config, policy.get("asn_sources", {}), fetch, policy_path.parent) if geo_mode == "mc2-inline" else []
    check_graph(config)
    dns_references = validate_dns(config)
    report = {"built_at": datetime.now(timezone.utc).isoformat(), "sources": summaries,
                    "remote_rules": remote_summaries, "fetches": fetch.events, "rule_merge": merge_report, "rule_compiler_version": 3,
                    "geo_mode": geo_mode, "geo_rules_version": 1, "required_geodata": required_geodata(config),
                    "embedded_rule_count": sum(x["entries"] for x in embedded_geosite + embedded_asn),
                    "embedded_geosite": embedded_geosite,
                    "embedded_asn": embedded_asn,
                    "proxy_count": len(config["proxies"]), "rule_count": len(rules),
                    "priority_order": sorted(tiers), "selection": "priority tiers: url-test; tiers: fallback",
                    "contains_subscription_urls": False,
                    "dns": {"configured": "dns" in config, "enabled": config.get("dns", {}).get("enable", False),
                            "source": "policy.yaml", "proxy_references": dns_references}}
    if dns_routing is not None:
        report["dns_routing"] = dns_routing
    return config, report


def generate(settings_path: Path, output=None, offline=False, mihomo=None, require_core=False,
             settings_override=None, extra_outputs=()):
    settings_path = settings_path.resolve()
    settings = mapping(settings_override if settings_override is not None else read_yaml_file(settings_path), "settings")
    root = settings_path.parent
    config, report = compile_profile(settings, root, offline)
    if "_subscription_fetches" in settings:
        report["subscription_fetches"] = settings["_subscription_fetches"]
    target = Path(output) if output else root / settings.get("output", "output/HomeChain.yaml")
    target = target.resolve()
    blob = yaml_bytes(config, "# Generated for Mihomo (Clash Meta). Contains private proxy credentials.\n"
                             "# Edit settings/residential/policy inputs, then rebuild.\n")
    dns_doc = mc2_dns_document(config)
    dns_target = target.with_name(f"{target.stem}.MC2DNS.yaml") if dns_doc else None
    dns_blob = yaml_bytes(dns_doc, "# MC2 1.2.2 Redir-Host DNS fragment, generated from policy.yaml.\n"
                                  "# Paste the ENTIRE file into MC2 DNS settings > Edit Redir-Host.\n"
                                  "# Requires the residential proxy group in the matching main config.\n"
                                  "# Generated DNS rule-set references also require its rule-providers.\n"
                                  "# Not a standalone proxy profile; do not upload as the main YAML.\n") if dns_doc else None
    report["dns"]["mc2_file"] = str(dns_target) if dns_target else None
    # Parse the generated text before invoking a core or replacing an existing output.
    check_graph(load_yaml(blob))
    validation = mapping(settings.get("validation", {}), "validation")
    exe = mihomo or validation.get("mihomo")
    if exe:
        exe = Path(exe)
        if not exe.is_absolute():
            exe = root / exe
        if not exe.is_file():
            if require_core or validation.get("required", False):
                raise BuildError("指定的 Mihomo 校验程序不存在")
            exe = None
    elif require_core or validation.get("required", False):
        raise BuildError("需要指定 Mihomo 校验程序")
    cache_dir = Path(settings.get("http", {}).get("cache_dir", root / ".cache"))
    if exe:
        workdir = (root / validation.get("data_dir", ".cache/core-data")).resolve()
        workdir.mkdir(parents=True, exist_ok=True)
        for name in required_geodata(config):
            if not (workdir / name).is_file():
                raise BuildError(f"内核校验缺少 {name}，请补齐校验数据库")
        validate_with_core(config, exe, workdir, cache_dir / "core-check.log")
        report["core_validation"] = "passed"
        if dns_doc:
            validate_with_core({**config, **dns_doc}, exe, workdir, cache_dir / "mc2-dns-check.log")
            report["dns"]["mc2_core_validation"] = "passed"
        if validation.get("mc2_package"):
            report["mc2_package_validation"] = validate_mc2_package(
                config, dns_doc, exe, (root / validation["mc2_package"]).resolve(), cache_dir / "mc2-package-check.log", workdir if report["geo_mode"] == "native" else None)
    else:
        report["core_validation"] = "not_run; structure checked only"
        if dns_doc:
            report["dns"]["mc2_core_validation"] = "not_run; structure checked only"
    outputs = list(extra_outputs)
    if dns_doc:
        outputs.append((dns_target, dns_blob))
    outputs.append((target.with_suffix(".report.json"), (json.dumps(report, ensure_ascii=False, indent=2) + "\n").encode()))
    outputs.append((target, blob))
    write_output_bundle(outputs)
    return target, report


def project_settings(project, root=ROOT):
    raw = env_value(mapping(read_yaml_file(project / "settings.yaml"), "settings"))
    allowed = {"sources", "selection", "runtime", "http", "validation"}
    if set(raw) - allowed:
        raise BuildError("项目 settings 含旧版或未知字段；只支持 sources/selection/runtime/http/validation")
    settings = copy.deepcopy(raw)
    seen = set()
    for i, source in enumerate(seq(settings.get("sources", []), "sources"), 1):
        mapping(source, "source")
        if set(source) - {"name", "url", "priority", "enabled", "include", "exclude", "user_agent", "headers"}:
            raise BuildError("订阅含旧版或未知字段；不再使用 file/fallback_file/allow_stale/on_error")
        name = name_ok(source.get("name"), "订阅")
        if (re.search(r'[<>:"/\\|?*]', name) or name.endswith((" ", "."))
                or name in {".", ".."} or re.fullmatch(r"(?i)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", name)):
            raise BuildError("订阅名称不能包含文件名非法字符或系统保留名称")
        if name.casefold() in seen:
            raise BuildError("订阅名称不能重复（文件名不区分大小写）")
        seen.add(name.casefold())
        source.setdefault("priority", i)
    settings["http"] = {**settings.get("http", {}),
                        "cache_dir": str(root / ".cache" / project.name)}
    bundled = root / "resources"
    settings.setdefault("validation", {
        "mihomo": str((bundled / "validation/mihomo.exe").resolve()),
        "data_dir": str((bundled / "geodata").resolve()),
        "required": True,
    })
    return settings


def build_project(project, force=False, root=ROOT):
    project, root = Path(project).resolve(), Path(root).resolve()
    settings = project_settings(project, root)
    # Rule lists use cache first normally; -f also requests fresh rule lists.
    # Only subscription refresh is strictly fail-closed, including nested providers.
    settings["http"]["prefer_cache"] = not force
    extra = []
    with tempfile.TemporaryDirectory(prefix="homechain-subs-") as temporary:
        stage = Path(temporary)
        fetch = Fetcher(project, {**settings["http"], "cache_dir": str(stage / "cache"),
                                 "allow_stale": False, "prefer_cache": False, "strict_subscriptions": True})
        for source in settings["sources"]:
            if not source.get("enabled", True):
                continue
            name = source["name"]
            snapshot = project / "subscrip" / f"{name}.yaml"
            if force:
                if not source.get("url"):
                    raise BuildError(f"{name} 缺少 url，不能强制刷新")
                doc, base = fetch.read(source, node_document, name)
                nodes = collect_nodes(doc, fetch, base, name)
                if not nodes:
                    raise BuildError(f"{name} 下载结果没有节点")
                # Materialize providers too, so normal builds never fetch subscriptions.
                payload = yaml_bytes({"proxies": nodes}, "# Local subscription snapshot; contains private credentials.\n")
                staged = stage / f"{name}.yaml"
                atomic_write(staged, payload)
                extra.append((snapshot, payload))
                source["file"] = str(staged)
            else:
                if not snapshot.is_file():
                    raise BuildError(f"{name} 缺少 subscrip/{name}.yaml；请先运行 python build.py -f")
                doc = node_document(snapshot.read_bytes())
                if doc.get("proxy-providers"):
                    raise BuildError(f"{name} 快照仍引用外部 provider；请运行 python build.py -f 生成完整快照")
                source["file"] = str(snapshot)
            source.pop("url", None)
        settings["_subscription_fetches"] = fetch.events if force else [
            {"item": s["name"], "status": "snapshot"} for s in settings["sources"] if s.get("enabled", True)]
        return generate(project / "settings.yaml", root / "output" / project.name / "HomeChain.yaml",
                        settings_override=settings, extra_outputs=extra)


def discover_projects(root=ROOT):
    # Examples are documentation inputs, never runnable projects (including -f).
    excluded = {"examples"}
    return sorted(p.parent for p in (Path(root) / "config").glob("*/settings.yaml")
                  if p.parent.name.casefold() not in excluded)


def main():
    parser = argparse.ArgumentParser(description="生成 config 下所有项目；默认读本地订阅快照")
    parser.add_argument("-f", action="store_true", help="强制刷新订阅；任何订阅下载失败即报错，不回退")
    parser.add_argument("--workspace", type=Path, default=ROOT, help="显式指定包含 config 的工作区；默认仅使用参考实现所在目录")
    args = parser.parse_args()
    try:
        workspace = args.workspace.resolve()
        projects = discover_projects(workspace)
        if not projects:
            raise BuildError("config 下没有项目 settings.yaml")
        for project in projects:
            print(f"正在生成项目：{project.name}", flush=True)
            target, report = build_project(project, args.f, root=workspace)
            print(f"已生成：{target}")
            print(f"节点 {report['proxy_count']}，规则 {report['rule_count']}；内核校验：{report['core_validation']}")
            print("MC2 DNS 文件：" + str(report["dns"].get("mc2_file")))
        print("完成。文件仅保存在电脑；首次导入先启动主 YAML，成功后再设置 MC2 DNS。")
        return 0
    except BuildError as exc:
        print("生成失败：" + str(exc), file=sys.stderr)
        return 1
    except Exception as exc:
        print(f"生成未完成（{type(exc).__name__}）；请检查输入格式。", file=sys.stderr)
        return 1


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    raise SystemExit(main())
