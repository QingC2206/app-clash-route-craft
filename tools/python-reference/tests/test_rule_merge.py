from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import json
import sys
import tempfile
import threading
import unittest

import yaml

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import build
from test_builder import dump, fixture


def candidates(*rules):
    return [{"rule": rule, "source": f"list-{n}", "stage": "remote"}
            for n, rule in enumerate(rules, 1)]


class RuleMergeTests(unittest.TestCase):
    def test_same_condition_same_action_deduplicates(self):
        rules, report = build.merge_rules(candidates(
            "DOMAIN-SUFFIX,Anthropic.COM,REJECT", "DOMAIN-SUFFIX,anthropic.com,REJECT"))
        self.assertEqual(rules, ["DOMAIN-SUFFIX,anthropic.com,REJECT"])
        self.assertEqual(report["duplicates_removed"], 1)
        self.assertEqual(report["conflicts_resolved"], 0)

    def test_conflicting_actions_follow_order_including_direct_first(self):
        for first, second in [("REJECT", "DIRECT"), ("DIRECT", "REJECT"),
                              ("默认代理", "DIRECT"), ("DIRECT", "默认代理")]:
            with self.subTest(first=first, second=second):
                rules, report = build.merge_rules(candidates(
                    f"DOMAIN-SUFFIX,claude.ai,{first}", f"DOMAIN-SUFFIX,claude.ai,{second}"))
                self.assertEqual(rules, [f"DOMAIN-SUFFIX,claude.ai,{first}", f"DOMAIN-SUFFIX,claude.ai,{second}"])
                self.assertEqual(report["conflicts_resolved"], 0)
                self.assertEqual(report["conflicts_preserved"], 1)
                self.assertEqual(report["conflicts"][0]["earlier"]["source"], "list-1")

    def test_specific_exception_and_broader_rule_are_kept(self):
        original = ["DOMAIN,api.example.com,DIRECT", "DOMAIN-SUFFIX,example.com,REJECT",
                    "DOMAIN-SUFFIX,other.example,默认代理"]
        rules, report = build.merge_rules(candidates(*original))
        self.assertEqual(rules, original)
        self.assertEqual(report["overlap_notices"], 1)
        self.assertEqual(report["conflicts_resolved"], 0)
        reversed_rules, reversed_report = build.merge_rules(candidates(*original[::-1]))
        self.assertEqual(reversed_rules, original[::-1])
        self.assertEqual(reversed_report["overlap_notices"], 1)

    def test_domain_suffix_boundary_and_exact_domains(self):
        _, report = build.merge_rules(candidates(
            "DOMAIN-SUFFIX,example.com,REJECT", "DOMAIN,notexample.com,DIRECT",
            "DOMAIN,api.example.com,DIRECT", "DOMAIN-SUFFIX,sub.example.com,默认代理",
            "DOMAIN,sub.api.example.com,默认代理"))
        self.assertEqual(report["overlap_notices"], 3)
        _, exact = build.merge_rules(candidates("DOMAIN,example.com,REJECT", "DOMAIN,api.example.com,DIRECT"))
        self.assertEqual(exact["overlap_notices"], 0)

    def test_ipv4_ipv6_normalization_and_flags(self):
        rules, report = build.merge_rules(candidates(
            "IP-CIDR,10.1.2.3/8,DIRECT,no-resolve", "IP-CIDR,10.0.0.0/8,DIRECT,no-resolve",
            "IP-CIDR,10.0.0.0/8,REJECT", "IP-CIDR,10.2.0.0/16,REJECT,no-resolve",
            "IP-CIDR,2001:db8::1/32,DIRECT", "IP-CIDR6,2001:0db8::/32,REJECT"))
        self.assertEqual(report["duplicates_removed"], 0)
        self.assertEqual(report["duplicates_preserved"], 1)
        self.assertEqual(report["conflicts_preserved"], 1)
        self.assertEqual(report["overlap_notices"], 1)
        self.assertIn("IP-CIDR,10.0.0.0/8,REJECT", rules)
        self.assertIn("IP-CIDR6,2001:db8::/32,DIRECT", rules)

    def test_same_action_overlaps_preserve_specific_rules(self):
        original = ["DOMAIN-SUFFIX,example.com,DIRECT", "DOMAIN,api.example.com,DIRECT"]
        rules, report = build.merge_rules(candidates(*original))
        self.assertEqual(rules, original)
        self.assertEqual(report["duplicates_removed"], 0)

    def test_complex_expressions_are_not_guessed(self):
        original = ["DOMAIN-KEYWORD,claude,REJECT", "DOMAIN-SUFFIX,claude.ai,DIRECT",
                    "AND,((NETWORK,UDP),(DST-PORT,443)),REJECT", "MATCH,默认代理"]
        rules, report = build.merge_rules(candidates(*original))
        self.assertEqual(rules, original)
        self.assertEqual(report["overlap_notices"], 0)

    def test_formats_are_detected_from_content(self):
        parser = build.remote_rule_parser({})
        expected = ["DOMAIN-SUFFIX,claude.ai,DIRECT"]
        for content in [b"# classical list\r\nDOMAIN-SUFFIX,claude.ai\r\n",
                        b"# yaml\npayload:\n  - DOMAIN-SUFFIX,claude.ai\n",
                        b'{"payload": ["DOMAIN-SUFFIX,claude.ai"]}',
                        b"\xef\xbb\xbfpayload: ['DOMAIN-SUFFIX,claude.ai']"]:
            with self.subTest(content=content):
                self.assertEqual(parser(content), expected)
        with self.assertRaises(build.BuildError):
            parser(b"<html>error</html>")

    def test_ordered_yaml_accepts_repeated_actions_without_duplicate_keys(self):
        doc = build.load_yaml("remote_rules:\n- reject: https://example.com/a\n- direct: https://example.com/b\n- reject: https://example.com/c\n")
        specs = list(build.remote_rule_specs(doc["remote_rules"]))
        self.assertEqual([s["action"] for s in specs], ["REJECT", "DIRECT", "REJECT"])
        self.assertEqual(list(build.remote_rule_specs(None)), [])
        with self.assertRaises(build.BuildError):
            list(build.remote_rule_specs([{"reject": "https://example.com/a", "direct": "https://example.com/b"}]))
        with self.assertRaises(build.BuildError):
            list(build.remote_rule_specs([{"url": "https://example.com/a", "priority": 1}]))

    def test_manual_exception_still_wins_and_report_is_bounded(self):
        rules, report = build.merge_rules([
            {"rule": "DOMAIN-SUFFIX,claude.ai,DIRECT", "stage": "first", "source": "first[1]"},
            *candidates(*(f"DOMAIN-SUFFIX,claude.ai,Proxy-{i}" for i in range(110)))])
        self.assertEqual(rules[0], "DOMAIN-SUFFIX,claude.ai,DIRECT")
        self.assertEqual(len(rules), 111)
        self.assertEqual(report["conflicts_preserved"], 110)
        self.assertEqual(len(report["conflicts"]), 100)
        self.assertTrue(report["details_truncated"])

    def test_url_shorthand_end_to_end_in_both_orders(self):
        documents = {
            "/first": b"payload: ['DOMAIN-SUFFIX,anthropic.com', 'DOMAIN-SUFFIX,claude.ai', 'DOMAIN,cdn.usefathom.com']",
            "/second": b"# not YAML\nDOMAIN-SUFFIX,anthropic.com\nDOMAIN-SUFFIX,claude.ai\nDOMAIN-SUFFIX,claude.com\n",
        }

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.end_headers()
                self.wfile.write(documents[self.path.split("?")[0]])

            def log_message(self, *_):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                settings = fixture(root)
                settings["http"] = {"trust_env": False}
                base = f"http://127.0.0.1:{server.server_port}"
                policy_path = root / "policy.yaml"
                policy = build.read_yaml_file(policy_path)
                policy["remote_rules"] = [
                    {"reject": base + "/first?token=do-not-log-this"}, {"direct": base + "/second"}]
                dump(policy_path, policy)
                cfg, report = build.compile_profile(settings, root)
                self.assertIn("DOMAIN-SUFFIX,claude.ai,REJECT", cfg["rules"])
                self.assertLess(cfg["rules"].index("DOMAIN-SUFFIX,claude.ai,REJECT"), cfg["rules"].index("DOMAIN-SUFFIX,claude.ai,DIRECT"))
                self.assertIn("DOMAIN-SUFFIX,claude.com,DIRECT", cfg["rules"])
                self.assertEqual(report["rule_merge"]["conflicts_preserved"], 2)
                self.assertNotIn("do-not-log-this", json.dumps(report))
                policy["remote_rules"].reverse()
                dump(policy_path, policy)
                cfg, report = build.compile_profile(settings, root, offline=True)
                self.assertIn("DOMAIN-SUFFIX,claude.ai,DIRECT", cfg["rules"])
                self.assertLess(cfg["rules"].index("DOMAIN-SUFFIX,claude.ai,DIRECT"), cfg["rules"].index("DOMAIN-SUFFIX,claude.ai,REJECT"))
                self.assertIn("DOMAIN,cdn.usefathom.com,REJECT", cfg["rules"])
        finally:
            server.shutdown()
            server.server_close()

    def test_legacy_settings_rule_location_is_not_silently_ignored(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            settings = fixture(root)
            settings["remote_rules"] = [{"direct": "https://example.com/rules.list"}]
            with self.assertRaisesRegex(build.BuildError, "policy.yaml"):
                build.compile_profile(settings, root, offline=True)

    def test_policy_relative_rule_files_and_fallback(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            settings = fixture(root)
            folder = root / "policies"
            folder.mkdir()
            (folder / "local.list").write_text("DOMAIN-SUFFIX,local.example\n", encoding="utf-8")
            (folder / "fallback.list").write_text("DOMAIN-SUFFIX,fallback.example\n", encoding="utf-8")
            dump(folder / "policy.yaml", {"domestic_direct": False, "remote_rules": [
                {"file": "local.list", "action": "DIRECT"},
                {"url": "https://example.com/rules", "fallback_file": "fallback.list", "action": "REJECT"},
            ]})
            settings["policy_file"] = "policies/policy.yaml"
            cfg, report = build.compile_profile(settings, root, offline=True)
            self.assertEqual(cfg["rules"], ["DOMAIN-SUFFIX,local.example,DIRECT",
                                            "DOMAIN-SUFFIX,fallback.example,REJECT", "MATCH,默认代理"])
            self.assertEqual(report["fetches"][-1]["status"], "fallback_file")


if __name__ == "__main__":
    unittest.main()
