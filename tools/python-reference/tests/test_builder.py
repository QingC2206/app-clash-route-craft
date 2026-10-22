from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import copy
import json
import sys
import tempfile
import threading
import unittest

import yaml

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import build


def dump(path, data):
    Path(path).write_text(yaml.safe_dump(data, allow_unicode=True, sort_keys=False), encoding="utf-8")


def fixture(root, ports=(1081, 1082)):
    for name, port in zip(("A", "B"), ports):
        dump(root / f"{name}.yaml", {"proxies": [
            {"name": "香港同名", "type": "http", "server": "127.0.0.1", "port": port},
            {"name": "美国", "type": "http", "server": "127.0.0.1", "port": port + 100},
        ]})
    dump(root / "residential.yaml", {"proxies": [
        {"name": "住宅", "type": "socks5", "server": "127.0.0.1", "port": 1090,
         "username": "local", "password": "p#a:ss\\word", "udp": True}
    ]})
    dump(root / "policy.yaml", {"direct": ["example.cn"], "reject": ["bad.example"],
                                "proxy": ["DOMAIN-SUFFIX,proxy.example"], "domestic_direct": False})
    return {"sources": [{"name": "A", "priority": 10, "file": "A.yaml"},
                        {"name": "B", "priority": 20, "file": "B.yaml"}],
            "selection": {"include": "香港", "interval": 300, "timeout": 1000, "tolerance": 0},
            "residential_file": "residential.yaml", "policy_file": "policy.yaml"}


class BuilderTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.settings = fixture(self.root)

    def tearDown(self):
        self.temp.cleanup()

    def test_priority_namespace_and_chain(self):
        cfg, report = build.compile_profile(self.settings, self.root, True)
        groups = {g["name"]: g for g in cfg["proxy-groups"]}
        self.assertEqual(groups["订阅优先"]["proxies"], ["优先级·10", "优先级·20"])
        self.assertEqual(groups["机场手选"]["proxies"], ["[A] 香港同名", "[B] 香港同名"])
        self.assertEqual(cfg["proxies"][-1]["dialer-proxy"], "机场入口")
        self.assertEqual(cfg["proxies"][-1]["password"], "p#a:ss\\word")
        self.assertNotIn("proxy-providers", cfg)
        self.assertFalse(report["contains_subscription_urls"])

    def test_equal_priorities_share_latency_pool(self):
        self.settings["sources"][1]["priority"] = 10
        cfg, _ = build.compile_profile(self.settings, self.root, True)
        group = next(g for g in cfg["proxy-groups"] if g["name"] == "优先级·10")
        self.assertEqual(group["type"], "url-test")
        self.assertEqual(len(group["proxies"]), 2)

    def test_anchors_and_duplicate_keys(self):
        raw = "base: &node {type: http, server: 127.0.0.1, port: 80}\nproxies:\n- {<<: *node, name: 香港, port: 81}\n"
        self.assertEqual(build.node_document(raw)["proxies"][0]["port"], 81)
        with self.assertRaises(build.BuildError):
            build.load_yaml("x: 1\nx: 2\n")

    def test_provider_payload_and_local_file(self):
        dump(self.root / "A.yaml", {"proxy-providers": {
            "inline": {"type": "inline", "payload": [{"name": "香港", "type": "http", "server": "127.0.0.1", "port": 1081}]},
            "file": {"type": "file", "path": "B.yaml", "filter": "香港", "override": {"additional-prefix": "供应-", "udp": True}},
        }})
        cfg, _ = build.compile_profile(self.settings, self.root, True)
        self.assertTrue(any(p["name"] == "[A] 供应-香港同名" and p["udp"] for p in cfg["proxies"]))

    def test_remote_file_provider_does_not_read_pc(self):
        fetch = build.Fetcher(self.root, {}, True)
        with self.assertRaises(build.BuildError):
            build.collect_nodes({"proxy-providers": {"x": {"type": "file", "path": "A.yaml"}}}, fetch, None, "remote")

    def test_source_dependencies_and_cycles(self):
        with self.assertRaises(build.BuildError):
            build.namespace_nodes([{"name": "香港", "type": "http", "dialer-proxy": "机场的某个组"}], "A")
        with self.assertRaises(build.BuildError):
            build.check_graph({"proxies": [{"name": "a", "dialer-proxy": "b"}], "proxy-groups": [{"name": "b", "proxies": ["a"]}]})

    def test_duplicate_names_and_dedup(self):
        nodes = [{"name": "香港", "type": "http", "server": "127.0.0.1", "port": 80},
                 {"name": "香港", "type": "http", "server": "127.0.0.1", "port": 81},
                 {"name": "重复别名", "type": "http", "server": "127.0.0.1", "port": 81}]
        result = build.namespace_nodes(nodes, "A")
        self.assertEqual([p["name"] for p in result], ["[A] 香港", "[A] 香港 (2)"])

    def test_notice_alias_does_not_hide_real_route(self):
        base = {"type": "http", "server": "127.0.0.1", "port": 80}
        nodes = [
            {**base, "name": "🇭🇰 套餐到期日期：2027-01-01"},
            {**base, "name": "🇭🇰 套餐重置日期：2026-11-01"},
            {**base, "name": "🇭🇰 订阅获取时间：2026-10-15"},
            {**base, "name": "🇭🇰 香港 01"},
            {**base, "name": "🇭🇰 香港 02", "port": 81},
            {**base, "name": "套餐到期日期：独立节点", "port": 82},
        ]
        original = copy.deepcopy(nodes)
        result = build.namespace_nodes(nodes, "A")
        self.assertEqual([p["name"] for p in result],
                         ["[A] 🇭🇰 香港 01", "[A] 🇭🇰 香港 02", "[A] 套餐到期日期：独立节点"])
        self.assertEqual({k: v for k, v in result[0].items() if k != "name"}, base)
        self.assertEqual(nodes, original)

    def test_notice_alias_keeps_real_route_in_ingress_pool(self):
        base = {"type": "http", "server": "127.0.0.1", "port": 1081}
        dump(self.root / "A.yaml", {"proxies": [
            {**base, "name": "🇭🇰 套餐到期日期：2027-01-01"},
            {**base, "name": "🇭🇰 香港 01"},
        ]})
        cfg, report = build.compile_profile(self.settings, self.root, True)
        group = next(g for g in cfg["proxy-groups"] if g["name"] == "订阅·A")
        self.assertEqual(group["proxies"], ["[A] 🇭🇰 香港 01"])
        self.assertEqual(report["sources"][0]["eligible"], 1)

    def test_rule_actions_flags_and_order(self):
        cfg, _ = build.compile_profile(self.settings, self.root, True)
        self.assertEqual(cfg["rules"], ["DOMAIN-SUFFIX,example.cn,DIRECT", "DOMAIN-SUFFIX,bad.example,REJECT",
                                       "DOMAIN-SUFFIX,proxy.example,默认代理", "MATCH,默认代理"])
        self.assertEqual(build.rule_with_action("IP-CIDR,10.0.0.0/8,OLD,no-resolve", "REJECT"), "IP-CIDR,10.0.0.0/8,REJECT,no-resolve")
        self.assertEqual(build.rule_with_action("AND,((NETWORK,UDP),(DST-PORT,443))", "REJECT"), "AND,((NETWORK,UDP),(DST-PORT,443)),REJECT")

    def test_remote_domains_and_catchall_rejected(self):
        parser = build.remote_rule_parser({"format": "yaml", "behavior": "domain"})
        self.assertEqual(parser(b"payload: [example.com, '+.example.net']"), ["DOMAIN,example.com,DIRECT", "DOMAIN-SUFFIX,example.net,DIRECT"])
        with self.assertRaises(build.BuildError):
            build.remote_rule_parser({})(b"payload: ['MATCH,DIRECT']")
        with self.assertRaises(build.BuildError):
            build.node_document(b"dmxlc3M6Ly9ub3RlLXlhbWw=")

    def test_cache_fallback_and_invalid_download_does_not_replace_cache(self):
        state = {"status": 200, "data": b"proxies: [{name: HK, type: http, server: 127.0.0.1, port: 80}]"}
        received = []

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                received.append(self.headers.get("User-Agent"))
                self.send_response(state["status"])
                self.end_headers()
                self.wfile.write(state["data"])

            def log_message(self, *_):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            fetch = build.Fetcher(self.root, {"trust_env": False})
            spec = {"url": f"http://127.0.0.1:{server.server_port}/?token=never-log-me"}
            first, _ = fetch.read(spec, build.node_document, "A")
            state.update(status=403)
            second, _ = fetch.read(spec, build.node_document, "A")
            self.assertEqual(first, second)
            self.assertEqual(fetch.events[-1]["status"], "cache")
            state.update(status=200, data=b"<html>not yaml</html>")
            third, _ = fetch.read(spec, build.node_document, "A")
            self.assertEqual(first, third)
            state["data"] = b"proxies: [not-a-node]"
            fourth, _ = fetch.read(spec, build.node_document, "A")
            self.assertEqual(first, fourth)
            self.assertNotIn("never-log-me", json.dumps(fetch.events))
            self.assertEqual(set(received), {"clash-verge/v2.4.7"})
        finally:
            server.shutdown()
            server.server_close()

    def test_failure_keeps_previous_output(self):
        target = self.root / "output/HomeChain.yaml"
        target.parent.mkdir()
        target.write_text("old-valid-output", encoding="utf-8")
        cfgpath = self.root / "settings.yaml"
        dump(cfgpath, self.settings)
        dump(self.root / "residential.yaml", [{"name": "住宅", "type": "socks5", "server": "FILL_SERVER", "port": 0}])
        with self.assertRaises(build.BuildError):
            build.generate(cfgpath, offline=True)
        self.assertEqual(target.read_text(), "old-valid-output")

    def test_first_rule_override_and_unknown_action(self):
        policy = {"first": [{"match": "DOMAIN-SUFFIX,claude.ai", "action": "DIRECT"}], "domestic_direct": False}
        dump(self.root / "policy.yaml", policy)
        cfg, _ = build.compile_profile(self.settings, self.root, True)
        self.assertEqual(cfg["rules"][0], "DOMAIN-SUFFIX,claude.ai,DIRECT")
        policy["first"][0]["action"] = "missing-group"
        dump(self.root / "policy.yaml", policy)
        with self.assertRaises(build.BuildError):
            build.compile_profile(self.settings, self.root, True)


if __name__ == "__main__":
    unittest.main()
