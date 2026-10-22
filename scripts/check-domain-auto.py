"""Compare URL-only domain lists with native Mihomo using loopback HTTP only."""
from pathlib import Path
import argparse
import hashlib
import http.server
import importlib.util
import json
import subprocess
import threading
import yaml

ROOT = Path(__file__).resolve().parents[1]
arguments = argparse.ArgumentParser()
arguments.add_argument("--executable", type=Path, default=ROOT / "releases/ClashRouteCraft-win-x64/ClashRouteCraft.exe")
options = arguments.parse_args()
OUT = ROOT / "artifacts/domain-auto-fix/runtime"
OUT.mkdir(parents=True, exist_ok=True)
spec = importlib.util.spec_from_file_location("semantics", ROOT / "scripts/check-rule-semantics-v2.py")
semantics = importlib.util.module_from_spec(spec)
spec.loader.exec_module(semantics)
semantics.OUT = OUT
EXE = options.executable.resolve()
WORK = OUT / "workspace"
raw = (ROOT / "tests/fixtures/wechat-domain.yaml").read_bytes()
payload = yaml.safe_load(raw)["payload"]
results = {"source": "https://raw.githubusercontent.com/Lanlan13-14/Rules/main/rules/Domain/WeChat.yaml",
           "snapshot_sha256": hashlib.sha256(raw).hexdigest(), "entries": len(payload),
           "wildcards": sum(entry.startswith("*.") for entry in payload), "builds": {}}


class RuleSource(http.server.BaseHTTPRequestHandler):
    requests = 0

    def do_GET(self):
        RuleSource.requests += 1
        body = "\n".join(payload).encode() if self.path == "/actually-text.yaml" else raw
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


def build_case(case, offline):
    result = OUT / f"{case}-{'offline' if offline else 'online'}-result.json"
    cmd = [str(EXE), "--build", "--workspace", str(WORK), "--project", case, "--result", str(result)]
    if offline:
        cmd.append("--offline")
    proc = subprocess.run(cmd, capture_output=True, timeout=40, creationflags=subprocess.CREATE_NO_WINDOW)
    if proc.returncode:
        raise RuntimeError(result.read_text(encoding="utf-8") if result.exists() else proc.stdout + proc.stderr)
    rules = yaml.safe_load((WORK / "output" / case / "HomeChain.yaml").read_text(encoding="utf-8"))["rules"]
    report = json.loads((WORK / "output" / case / "HomeChain.report.json").read_text(encoding="utf-8"))
    assert report["core_validation"] == "passed" and report["rule_compiler_version"] >= 3
    return {"rules": rules, "core_validation": report["core_validation"], "rule_compiler_version": report["rule_compiler_version"]}


source_server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), RuleSource)
threading.Thread(target=source_server.serve_forever, daemon=True).start()
origin = http.server.ThreadingHTTPServer(("127.0.0.1", 0), semantics.Origin)
threading.Thread(target=origin.serve_forever, daemon=True).start()
try:
    source_url = f"http://127.0.0.1:{source_server.server_port}"
    for case in ["ExplicitDomain", "UrlOnlyYaml", "UrlOnlyText", "UrlShorthand"]:
        project = WORK / "config" / case
        (project / "subscrip").mkdir(parents=True, exist_ok=True)
        remote = {"name": "WeChat", "action": "REJECT"}
        if case == "ExplicitDomain":
            remote.update(file="WeChat.yaml", behavior="domain")
        else:
            remote["url"] = source_url + ("/actually-text.yaml" if case == "UrlOnlyText" else "/download?list=wechat")
        if case == "UrlShorthand":
            remote = {"reject": [remote["url"]]}
        documents = {
            "settings.yaml": {
                "target": "clash", "sources": [{"name": "Local", "enabled": True}],
                "selection": {"include": ".*"}, "http": {"trust_env": False, "prefer_cache": False},
                "validation": {"mihomo": str(semantics.CORE), "required": True},
            },
            "residential.yaml": {"proxies": [{"name": "SyntheticResidence", "type": "socks5", "server": "127.0.0.1", "port": 1}]},
            "subscrip/Local.yaml": {"proxies": [{"name": "SyntheticEntry", "type": "http", "server": "127.0.0.1", "port": 1}]},
            "policy.yaml": {"remote_rules": [remote], "domestic_direct": False, "final": "DIRECT"},
        }
        for name, document in documents.items():
            (project / name).write_text(yaml.safe_dump(document), encoding="utf-8")
        (project / "WeChat.yaml").write_bytes(raw)
        previous_requests = RuleSource.requests
        results["builds"][case] = build_case(case, offline=case == "ExplicitDomain")
        if case != "ExplicitDomain":
            assert RuleSource.requests > previous_requests, "URL-only build did not fetch the fixture"
            previous_requests = RuleSource.requests
            assert build_case(case, offline=True) == results["builds"][case], "Cached parsing changed the rules"
            assert RuleSource.requests == previous_requests, "Offline build made an HTTP request"
        print(f"{case}: generated {len(results['builds'][case]['rules']) - 1} entries, core validation passed", flush=True)

    hosts = {"unrelated.invalid"}
    for entry in payload:
        base = entry[2:] if entry.startswith("*.") else entry
        hosts.update([base, "audit." + base, "deep.audit." + base, "audit" + base])
    hosts = sorted(hosts)
    runs = {}
    for mode in ["Native", *results["builds"]]:
        cfg = {"mode": "rule", "dns": {"enable": False}, "hosts": {host: "127.0.0.1" for host in hosts}}
        if mode == "Native":
            cfg["rule-providers"] = {"wechat": {"type": "inline", "behavior": "domain", "payload": payload}}
            cfg["rules"] = ["RULE-SET,wechat,REJECT", "MATCH,DIRECT"]
        else:
            cfg["rules"] = results["builds"][mode]["rules"]
        with semantics.kernel(cfg, "runtime-" + mode.lower()) as proxy_port:
            runs[mode] = {host: semantics.request(proxy_port, host, origin.server_port) for host in hosts}
        print(f"{mode}: completed {len(hosts)} loopback requests", flush=True)
    results["requests"] = runs
    results["host_count"] = len(hosts)
    results["mismatches"] = {
        mode: [{"host": host, "native": runs["Native"][host], "generated": runs[mode][host]}
               for host in hosts if runs[mode][host] != runs["Native"][host]]
        for mode in results["builds"]
    }
    assert all(value["status"] in [200, 502] for run in runs.values() for value in run.values())
    assert runs["Native"]["audit.wechat.com"]["origin_reached"] is False
    assert runs["Native"]["wechat.com"]["origin_reached"] is True
    assert runs["Native"]["deep.audit.wechat.com"]["origin_reached"] is True
    assert runs["Native"]["btrace.qq.com"]["origin_reached"] is False
    assert runs["Native"]["audit.btrace.qq.com"]["origin_reached"] is True
    results["ok"] = not any(results["mismatches"].values())
finally:
    source_server.shutdown()
    source_server.server_close()
    origin.shutdown()
    origin.server_close()
(OUT / "results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
assert results["ok"], results["mismatches"]
print(f"RESULT {len(hosts)} hosts x {len(results['builds'])} generated configurations match native domain-provider behavior; URL-only and offline cache builds passed")
