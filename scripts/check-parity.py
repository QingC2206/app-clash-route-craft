"""Compare the independent C# implementation against the Python behavior oracle.

Only generated fixtures are used here. No private project is modified or downloaded.
"""
from pathlib import Path
import copy
import json
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
REFERENCE = ROOT / "tools/python-reference"
sys.path[:0] = [str(REFERENCE), str(REFERENCE / "tests")]
import build
from test_builder import fixture, dump

BRIDGE = ROOT / "tests/ClashRouteCraft.Tests/bin/Release/net10.0/ClashRouteCraft.Tests.dll"
passed = 0

def call(request, directory):
    request_path, response_path = directory / "request.json", directory / "response.json"
    request_path.write_text(json.dumps(request, ensure_ascii=False), encoding="utf-8")
    subprocess.run(["dotnet", str(BRIDGE), "--bridge", str(request_path), str(response_path)], check=True, capture_output=True)
    response = json.loads(response_path.read_text(encoding="utf-8"))
    if not response["ok"]:
        raise AssertionError("C# operation failed: " + response.get("type", "unknown"))
    return response["result"]

def clean_report(report):
    report = copy.deepcopy(report)
    report.pop("built_at", None)
    # JSON object keys are strings; the ASN metadata is normalized numerically.
    for entry in report.get("embedded_asn", []):
        entry["asn"] = int(entry["asn"])
    return report

def first_difference(left, right, path="root"):
    if type(left) is not type(right):
        return path + " (type)"
    if isinstance(left, dict):
        if set(left) != set(right): return path + " (keys)"
        return next((d for key in left if (d := first_difference(left[key], right[key], path + "." + key))), None)
    if isinstance(left, list):
        if len(left) != len(right): return path + " (length)"
        return next((d for i, (a, b) in enumerate(zip(left, right)) if (d := first_difference(a, b, path + f"[{i}]"))), None)
    return None if left == right else path + " (value)"

def compare(name, expected, actual):
    global passed
    difference = first_difference(expected, actual)
    if difference: raise AssertionError(name + ": difference at " + difference)
    passed += 1
    print("PASS " + name)

with tempfile.TemporaryDirectory(prefix="homechain-parity-") as temporary:
    directory = Path(temporary)
    # Node normalization, alias selection and source-local references.
    nodes = [
        {"name": "香港", "type": "http", "server": "localhost", "port": 80},
        {"name": "香港", "type": "http", "server": "localhost", "port": 81},
        {"name": "重复别名", "type": "http", "server": "localhost", "port": 81},
    ]
    compare("node namespace", build.namespace_nodes(nodes, "A"), call({"op": "namespace", "nodes": nodes}, directory))
    base = {"type": "http", "server": "localhost", "port": 80}
    nodes = [{**base, "name": "套餐到期日期：2027"}, {**base, "name": "香港01"}]
    compare("notice alias selection", build.namespace_nodes(nodes, "A"), call({"op": "namespace", "nodes": nodes}, directory))

    rule_cases = ["example.cn", "+.example.net", "*.example.com", "1.2.3.4/24", "2001:db8::1/32",
                  "DOMAIN,example.com", "IP-CIDR,10.1.2.3/8,OLD,no-resolve", "IP-ASN,20473,OLD,no-resolve",
                  "AND,((NETWORK,UDP),(DST-PORT,443))", "OR,((DOMAIN,a.example),(DOMAIN,b.example)),OLD",
                  r"DOMAIN-REGEX,^api[0-9]{1,3}\.example$", {"rule": r"DOMAIN-REGEX,^api[,)]?\.example$,OLD"},
                  r"PROCESS-PATH-REGEX,^C:\\Apps\\[a-z]{1,3}\.exe$",
                  r"AND,((DOMAIN-REGEX,^api[0-9]{1,3}\.example$),(NETWORK,TCP))",
                  {"match": r"DOMAIN-REGEX,^foo,bar$|^blocked\.example$"},
                  {"rule": r"DOMAIN-REGEX,^foo,bar$|^blocked\.example$,Old Group"},
                  "IN-PORT,7890", "PROCESS-NAME-WILDCARD,*chrome*", r"PROCESS-PATH-WILDCARD,C:\Apps\*",
                  r"AND,((DOMAIN-REGEX,^foo,bar$|^blocked\.example$),(NETWORK,TCP))"]
    for i, rule in enumerate(rule_cases):
        compare(f"rule normalization {i+1}", build.rule_with_action(rule, "REJECT"), call({"op": "rule", "rule": rule, "action": "REJECT"}, directory))

    merge_cases = [
        ["DOMAIN-SUFFIX,Example.COM,DIRECT", "DOMAIN-SUFFIX,example.com,REJECT"],
        ["DOMAIN,api.example.com,DIRECT", "DOMAIN-SUFFIX,example.com,REJECT"],
        ["IP-CIDR,10.1.2.3/8,DIRECT,no-resolve", "IP-CIDR,10.0.0.0/8,DIRECT,no-resolve", "IP-CIDR,10.2.0.0/16,REJECT,no-resolve"],
        ["IP-CIDR,2001:db8::1/32,DIRECT", "IP-CIDR6,2001:0db8::/32,REJECT"],
        ["DOMAIN-KEYWORD,claude,REJECT", "DOMAIN-SUFFIX,claude.ai,DIRECT", "MATCH,默认代理"],
        ["DOMAIN,test.example,PASS", "DOMAIN,test.example,REJECT", "DOMAIN,test.example,PASS", "MATCH,DIRECT"],
        ["DOMAIN,test.example,TCP_ONLY", "DOMAIN,test.example,DIRECT", "MATCH,REJECT"],
        ["IP-CIDR,127.0.0.0/8,REJECT,no-resolve", "IP-CIDR,192.0.2.0/24,REJECT", "IP-CIDR,127.0.0.0/8,REJECT,no-resolve", "MATCH,DIRECT"],
        ["DOMAIN,test.example,DynamicGroup", "DOMAIN,test.example,DynamicGroup", "MATCH,REJECT"],
    ]
    for i, rules in enumerate(merge_cases):
        candidates = [{"rule": r, "source": f"list-{n}", "stage": "remote"} for n, r in enumerate(rules, 1)]
        merged, report = build.merge_rules(candidates)
        compare(f"rule merge report {i+1}", {"rules": merged, "report": report}, call({"op": "merge", "candidates": candidates}, directory))

    for i, (text, spec) in enumerate([
        ("# list\nDOMAIN-SUFFIX,claude.ai\n", {}),
        ("payload: ['DOMAIN-SUFFIX,claude.ai']", {}),
        ('{"payload":["DOMAIN-SUFFIX,claude.ai"]}', {}),
        ("payload: [example.com, '+.example.net']", {"behavior": "domain"}),
        ("1.2.3.4/24\n2001:db8::1/32\n", {"behavior": "ipcidr"}),
        (r"DOMAIN-REGEX,^api[0-9]{1,3}\.example$", {}),
        (r"DOMAIN-REGEX,^foo,bar$|^blocked\.example$", {"rule_format": "match"}),
        (r"DOMAIN-REGEX,^foo,bar$|^blocked\.example$,Old Group", {"rule_format": "rule"}),
        ("*.example.com\n.example.com\n+.*.example.com\na.*.example.com\n", {"behavior": "domain"}),
        ("1.2.3.4/24\n2001:db8::1/32\n", {"behavior": "ipcidr", "no-resolve": True}),
        ("payload: ['*.wechat.com', 'btrace.qq.com', '+.example.org']", {"url": "https://example.com/download?list=wechat"}),
        ("*.wechat.com\nbtrace.qq.com\n.example.net\na.*.example.com", {"behavior": "auto"}),
        ("['*.wechat.com', 'btrace.qq.com']", {}),
        ("\ufeff# header\n*.wechat.com\nbtrace.qq.com\n; end\n// end", {}),
        ("*.wechat.com\nbtrace.qq.com", {"behavior": "classical"}),
        ("example.com\nDOMAIN,exact.example", {}),
        ("DOMAIN,exact.example\nexample.com", {}),
        ("127.0.0.1\n2001:db8::/32", {}),
        ("example.com\n127.0.0.0/8", {}),
    ]):
        compare(f"remote parser {i+1}", build.remote_rule_parser(spec)(text.encode()), call({"op": "remote", "text": text, "spec": spec}, directory))

    for variant in range(11):
        root = directory / f"variant-{variant}"; root.mkdir()
        settings = fixture(root)
        policy = build.read_yaml_file(root / "policy.yaml")
        if variant == 1: settings["sources"][1]["priority"] = 10
        if variant == 2: settings["sources"][1]["enabled"] = False
        if variant == 3:
            policy["dns"] = build.read_yaml_file(REFERENCE / "tests/fixtures/dns.yaml")["dns"]
            policy["first"] = [{"match": "DOMAIN-SUFFIX,claude.ai", "action": "DIRECT"}]
        if variant == 4:
            (root / "local.list").write_text("DOMAIN-SUFFIX,example.cn\nDOMAIN,api.example.net\n")
            policy["remote_rules"] = [{"name": "test", "file": "local.list", "action": "REJECT"}]
        if variant in (5, 8):
            policy["domestic_direct"] = True
            policy["geosite_sources"] = {"geolocation-!cn": {"file": "noncn.list"}}
            (root / "noncn.list").write_text("DOMAIN-SUFFIX,foreign.example\nDOMAIN,other.example\n")
        if variant in (6, 9):
            policy["proxy"] = ["IP-ASN,20473,OLD,no-resolve"]
            policy["asn_sources"] = {20473: {"file": "asn.list"}}
            (root / "asn.list").write_text("1.2.3.0/24\n2001:db8::/32\n1.2.3.0/24\n")
        if variant == 7:
            dump(root / "A.yaml", {"proxy-providers": {"inline": {"type": "inline", "payload": [{"name": "香港", "type": "http", "server": "127.0.0.1", "port": 1081}]}, "file": {"type": "file", "path": "B.yaml", "filter": "香港", "override": {"additional-prefix": "供应-", "udp": True}}}})
        if variant in (8, 9): policy["geo_mode"] = "mc2-inline"
        if variant == 10:
            policy["rule_sets"] = [{"id": "generic", "name": "Base", "enabled": True, "include_fallback": False, "mappings": {"Group": "PROXY", "DIRECT": "DIRECT", "PASS": "PASS"}, "rules": [{"match": "DOMAIN,import.example", "source_action": "PASS"}, {"match": "DOMAIN,import.example", "source_action": "Group"}, {"match": "GEOIP,CN", "source_action": "DIRECT"}, {"match": "MATCH", "source_action": "Group"}]}]
            policy["supplemental_rules"] = [{"match": "DOMAIN,extra.example", "action": "PROXY"}, {"match": "DOMAIN-SUFFIX,extra.example", "action": "DIRECT"}]
            policy["dns_follow_rules"] = True
            policy["dns"] = {"enable": True, "nameserver": ["1.1.1.1"], "direct-nameserver": ["223.5.5.5"], "nameserver-policy": {"geosite:cn": ["223.5.5.5"]}}
            policy["first"] = [{"match": "DOMAIN-SUFFIX,example.cn", "action": "PROXY"}, {"match": "DOMAIN,child.example.cn", "action": "DIRECT"}, {"match": "DOMAIN,pass.example", "action": "PASS"}, {"match": "AND,((DOMAIN,x.example),(NETWORK,UDP))", "action": "DIRECT"}, {"match": r"DOMAIN-REGEX,^api[0-9]{1,3}\.example$", "action": "DIRECT"}]
        dump(root / "policy.yaml", policy)
        configuration, report = build.compile_profile(settings, root, True)
        actual = call({"op": "compile", "root": str(root), "settings": settings, "offline": True}, directory)
        compare(f"compiled configuration {variant}", configuration, actual["configuration"])
        compare(f"compiled report {variant}", clean_report(report), clean_report(actual["report"]))

print(f"RESULT {passed} semantic parity checks passed")
