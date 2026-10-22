"""Independent semantic review. Uses synthetic data and loopback endpoints only."""
from pathlib import Path
from contextlib import contextmanager
import http.client
import http.server
import json
import socket
import struct
import subprocess
import threading
import time
import yaml

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts/rule-semantics-v2"
OUT.mkdir(parents=True, exist_ok=True)
CORE = ROOT / "releases/ClashRouteCraft-win-x64/resources/validation/mihomo.exe"
BRIDGE = ROOT / "tests/ClashRouteCraft.Tests/bin/Release/net10.0/ClashRouteCraft.Tests.dll"


def bridge(request, label):
    source, target = OUT / (label + "-input.json"), OUT / (label + "-output.json")
    source.write_text(json.dumps(request), encoding="utf-8")
    subprocess.run(["dotnet", str(BRIDGE), "--bridge", str(source), str(target)],
                   check=True, capture_output=True, timeout=20)
    return json.loads(target.read_text(encoding="utf-8"))


def port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


@contextmanager
def kernel(config, label):
    folder = OUT / label
    folder.mkdir(exist_ok=True)
    proxy_port = port()
    config = dict(config, **{"mixed-port": proxy_port, "bind-address": "127.0.0.1",
                            "allow-lan": False, "log-level": "debug"})
    path = folder / "config.yaml"
    path.write_text(yaml.safe_dump(config, allow_unicode=True), encoding="utf-8")
    checked = subprocess.run([str(CORE), "-t", "-d", str(folder), "-f", str(path)],
                             capture_output=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
    if checked.returncode:
        raise RuntimeError((checked.stdout + checked.stderr).decode("utf-8", errors="replace"))
    with (folder / "core.log").open("wb") as log:
        proc = subprocess.Popen([str(CORE), "-d", str(folder), "-f", str(path)], stdout=log,
                                stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            for _ in range(100):
                if proc.poll() is not None:
                    raise RuntimeError("kernel exited")
                try:
                    with socket.create_connection(("127.0.0.1", proxy_port), timeout=.1):
                        break
                except OSError:
                    time.sleep(.03)
            else:
                raise RuntimeError("kernel not ready")
            yield proxy_port
        finally:
            proc.terminate()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()


class Origin(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        body = b"audit-local-origin"
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


def request(proxy_port, host, origin_port):
    conn = http.client.HTTPConnection("127.0.0.1", proxy_port, timeout=4)
    try:
        conn.request("GET", f"http://{host}:{origin_port}/")
        response = conn.getresponse()
        return {"status": response.status, "origin_reached": response.read() == b"audit-local-origin"}
    finally:
        conn.close()


class Dns:
    def __init__(self):
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.socket.bind(("127.0.0.1", 0))
        self.socket.settimeout(.1)
        self.port = self.socket.getsockname()[1]
        self.stop = threading.Event()
        self.queries = []
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def run(self):
        while not self.stop.is_set():
            try:
                data, address = self.socket.recvfrom(4096)
                p = 12
                labels = []
                while data[p]:
                    n = data[p]
                    labels.append(data[p + 1:p + n + 1].decode("ascii"))
                    p += n + 1
                qtype = struct.unpack("!H", data[p + 1:p + 3])[0]
                question = data[12:p + 5]
                self.queries.append({"host": ".".join(labels), "type": qtype})
                answer = b""
                if qtype == 1:
                    answer = b"\xc0\x0c" + struct.pack("!HHIH", 1, 1, 10, 4) + socket.inet_aton("127.0.0.1")
                reply = data[:2] + struct.pack("!HHHHH", 0x8180, 1, bool(answer), 0, 0) + question + answer
                self.socket.sendto(reply, address)
            except socket.timeout:
                pass
            except OSError:
                return

    def close(self):
        self.stop.set()
        self.socket.close()
        self.thread.join(timeout=2)


def main():
    results = {}
    origin = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Origin)
    threading.Thread(target=origin.serve_forever, daemon=True).start()
    dns = Dns()
    try:
        raw = r"DOMAIN-REGEX,^foo,bar$|^blocked\.example$"
        normalized = bridge({"op": "rule", "rule": {"match": raw}, "action": "REJECT"}, "literal-comma")
        case = {"raw": raw, "generated": normalized}
        for label, rule in [("original", raw + ",REJECT"), ("generated", normalized["result"])]:
            cfg = {"mode": "rule", "dns": {"enable": False}, "hosts": {"blocked.example": "127.0.0.1"},
                   "rules": [rule, "MATCH,DIRECT"]}
            with kernel(cfg, "literal-comma-" + label) as proxy_port:
                case[label + "_request"] = request(proxy_port, "blocked.example", origin.server_port)
        results["regex_literal_comma"] = case

        rules = ["IP-CIDR,127.0.0.0/8,REJECT,no-resolve", "IP-CIDR,192.0.2.0/24,REJECT",
                 "IP-CIDR,127.0.0.0/8,REJECT,no-resolve", "MATCH,DIRECT"]
        merged = bridge({"op": "merge", "candidates": [{"rule": r, "stage": "first", "source": "synthetic"}
                                                       for r in rules]}, "dns-state-dedup")
        case = {"original_rules": rules, "merge": merged}
        for label, active in [("original", rules), ("generated", merged["result"]["rules"])]:
            cfg = {"mode": "rule", "ipv6": False,
                   "dns": {"enable": True, "ipv6": False, "enhanced-mode": "redir-host", "use-hosts": False,
                           "use-system-hosts": False, "nameserver": [f"udp://127.0.0.1:{dns.port}"]}, "rules": active}
            with kernel(cfg, "dns-state-" + label) as proxy_port:
                case[label + "_request"] = request(proxy_port, "resolve.example", origin.server_port)
        case["dns_queries"] = list(dns.queries)
        results["dns_state_dedup"] = case

        parsed = bridge({"op": "remote", "text": "127.0.0.0/8", "spec": {"behavior": "ipcidr", "format": "text"}}, "ip-provider")
        normalized = bridge({"op": "rule", "rule": parsed["result"][0], "action": "REJECT", "input": "rule"}, "ip-provider-action")
        case = {"entry": "127.0.0.0/8", "generated": normalized}
        for label in ["original", "generated"]:
            cfg = {"mode": "rule", "ipv6": False,
                   "dns": {"enable": True, "ipv6": False, "enhanced-mode": "redir-host", "use-hosts": False,
                           "use-system-hosts": False, "nameserver": [f"udp://127.0.0.1:{dns.port}"]}}
            if label == "original":
                cfg["rule-providers"] = {"ips": {"type": "inline", "behavior": "ipcidr", "payload": ["127.0.0.0/8"]}}
                cfg["rules"] = ["RULE-SET,ips,REJECT", "MATCH,DIRECT"]
            else:
                cfg["rules"] = [normalized["result"], "MATCH,DIRECT"]
            with kernel(cfg, "ip-provider-" + label) as proxy_port:
                case[label + "_request"] = request(proxy_port, "ip-list.example", origin.server_port)
        results["ip_provider_no_resolve"] = case

        for label, entry, host in [("wildcard-deep", "*.example.com", "deep.a.example.com"),
                                   ("wildcard-one", "*.example.com", "a.example.com"),
                                   ("wildcard-apex", "*.example.com", "example.com"),
                                   ("dot-apex", ".example.com", "example.com"),
                                   ("dot-deep", ".example.com", "deep.a.example.com"),
                                   ("plus-apex", "+.example.com", "example.com"),
                                   ("plus-deep", "+.example.com", "deep.a.example.com"),
                                   ("middle-hit", "a.*.example.com", "a.b.example.com"),
                                   ("middle-miss", "a.*.example.com", "a.b.c.example.com"),
                                   ("two-hit", "*.*.example.com", "a.b.example.com"),
                                   ("two-miss", "*.*.example.com", "a.b.c.example.com"),
                                   ("plus-star", "+.*.example.com", "deep.a.example.com"),
                                   ("uppercase", "*.EXAMPLE.COM", "a.example.com")]:
            parsed = bridge({"op": "remote", "text": entry, "spec": {"behavior": "domain", "format": "text"}}, label)
            generated = bridge({"op": "rule", "rule": parsed["result"][0], "action": "REJECT", "input": "rule"}, label + "-action")
            case = {"entry": entry, "host": host, "generated": generated}
            for mode in ["original", "generated"]:
                cfg = {"mode": "rule", "dns": {"enable": False}, "hosts": {host: "127.0.0.1"}}
                if mode == "original":
                    cfg["rule-providers"] = {"domains": {"type": "inline", "behavior": "domain", "payload": [entry]}}
                    cfg["rules"] = ["RULE-SET,domains,REJECT", "MATCH,DIRECT"]
                else:
                    cfg["rules"] = [generated["result"], "MATCH,DIRECT"]
                with kernel(cfg, label + "-" + mode) as proxy_port:
                    case[mode + "_request"] = request(proxy_port, host, origin.server_port)
            results["domain_provider_" + label] = case

        results["supported_types"] = {}
        for rule in ["IN-PORT,7890", "PROCESS-NAME-WILDCARD,*chrome*", "PROCESS-PATH-WILDCARD,C:\\Apps\\*"]:
            label = rule.split(",")[0].lower()
            cfg = {"mode": "rule", "dns": {"enable": False}, "rules": [rule + ",REJECT", "MATCH,DIRECT"]}
            with kernel(cfg, label):
                results["supported_types"][label] = {"core_accepts": True,
                    "generator": bridge({"op": "rule", "rule": rule, "action": "REJECT"}, label)}
    finally:
        dns.close()
        origin.shutdown()
        origin.server_close()
    allowed = {"domain_provider_wildcard-deep", "domain_provider_wildcard-apex", "domain_provider_dot-apex",
               "domain_provider_middle-miss", "domain_provider_two-miss"}
    # Require both known positive/negative outcomes, not just equality between two failures.
    results["ok"] = all(case["original_request"] == case["generated_request"]
                        and case["original_request"]["status"] == (200 if name in allowed else 502)
                        and case["original_request"]["origin_reached"] == (name in allowed)
                        for name, case in results.items() if "original_request" in case) and all(case["generator"]["ok"] for case in results["supported_types"].values())
    (OUT / "results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    assert results["ok"], "Rule semantics differ; inspect artifacts/rule-semantics-v2/results.json"
    print("RESULT all rule-semantics-v2 runtime comparisons passed")


if __name__ == "__main__":
    main()
