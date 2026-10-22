"""Verify the installed AOT generator using isolated, synthetic projects."""
from pathlib import Path
import json
import subprocess
import yaml

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts/rule-semantics-v2"
OUT.mkdir(parents=True, exist_ok=True)
EXE = ROOT / "releases/ClashRouteCraft-win-x64/ClashRouteCraft.exe"
CORE = EXE.parent / "resources/validation/mihomo.exe"
WORK = OUT / "synthetic-workspace"

cases = {
    "RegexLiteral": {
        "reject": [{"match": r"DOMAIN-REGEX,^foo,bar$|^blocked\.example$"}, r"DOMAIN-REGEX,^api[0-9]{1,3}\.example$",
                   "DOMAIN,pass.example"],
        "first": [{"match": "DOMAIN,pass.example", "action": "PASS"}],
    },
    "DnsState": {
        "first": [{"match": rule, "action": "REJECT"} for rule in
                  ["IP-CIDR,127.0.0.0/8,no-resolve", "IP-CIDR,192.0.2.0/24", "IP-CIDR,127.0.0.0/8,no-resolve"]],
    },
    "DomainProvider": {
        "remote_rules": [{"name": "domain-list", "file": "domains.txt", "behavior": "domain", "action": "REJECT"}],
    },
    "IpProvider": {
        "remote_rules": [{"name": "ip-list", "file": "cidrs.txt", "behavior": "ipcidr", "action": "REJECT"}],
    },
}
results = {}
for name, policy in cases.items():
    project = WORK / "config" / name
    (project / "subscrip").mkdir(parents=True, exist_ok=True)
    settings = {
        "target": "clash", "sources": [{"name": "Local", "enabled": True}],
        "selection": {"include": ".*"}, "http": {"trust_env": False},
        "validation": {"mihomo": str(CORE), "required": True},
    }
    documents = {
        "settings.yaml": settings,
        "residential.yaml": {"proxies": [{"name": "SyntheticResidence", "type": "socks5", "server": "127.0.0.1", "port": 1}]},
        "subscrip/Local.yaml": {"proxies": [{"name": "SyntheticEntry", "type": "http", "server": "127.0.0.1", "port": 1}]},
        "policy.yaml": dict(policy, domestic_direct=False, final="DIRECT"),
    }
    for filename, document in documents.items():
        (project / filename).write_text(yaml.safe_dump(document, allow_unicode=True), encoding="utf-8")
    (project / "domains.txt").write_text("*.example.com\n.example.org\n", encoding="utf-8")
    (project / "cidrs.txt").write_text("127.0.0.0/8\n", encoding="utf-8")
    result = OUT / (name + "-release-result.json")
    proc = subprocess.run([str(EXE), "--build", "--workspace", str(WORK), "--project", name,
                           "--offline", "--result", str(result)], capture_output=True,
                          timeout=40, creationflags=subprocess.CREATE_NO_WINDOW)
    if proc.returncode:
        raise RuntimeError(result.read_text(encoding="utf-8") if result.exists() else proc.stdout + proc.stderr)
    generated = yaml.safe_load((WORK / "output" / name / "HomeChain.yaml").read_text(encoding="utf-8"))
    report = json.loads((WORK / "output" / name / "HomeChain.report.json").read_text(encoding="utf-8"))
    results[name] = {"exit_code": proc.returncode, "rules": generated["rules"],
                     "core_validation": report["core_validation"], "rule_compiler_version": report["rule_compiler_version"]}

assert r"DOMAIN-REGEX,^api[0-9]{1,3}\.example$,REJECT" in results["RegexLiteral"]["rules"]
assert "DOMAIN,pass.example,PASS" in results["RegexLiteral"]["rules"]
assert "DOMAIN,pass.example,REJECT" in results["RegexLiteral"]["rules"]
assert r"DOMAIN-REGEX,^foo,bar$|^blocked\.example$,REJECT" in results["RegexLiteral"]["rules"]
assert results["DnsState"]["rules"].count("IP-CIDR,127.0.0.0/8,REJECT,no-resolve") == 2
assert r"DOMAIN-REGEX,(?i)^[^.]+\.example\.com$,REJECT" in results["DomainProvider"]["rules"]
assert r"DOMAIN-REGEX,(?i)^(?:[^.]+\.)+example\.org$,REJECT" in results["DomainProvider"]["rules"]
assert "IP-CIDR,127.0.0.0/8,REJECT" in results["IpProvider"]["rules"]
(OUT / "release-generation-results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
assert all(r["rule_compiler_version"] >= 2 and r["core_validation"] == "passed" for r in results.values())
print("RESULT all published generator rule-semantics-v2 fixtures passed")
