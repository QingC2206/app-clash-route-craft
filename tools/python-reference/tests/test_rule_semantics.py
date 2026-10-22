from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import build


class RuleSemanticsTests(unittest.TestCase):
    def test_regex_boundaries_are_explicit(self):
        for kind in build.REGEX_RULE_TYPES:
            raw = kind + r",^foo,bar$|^blocked\.example$"
            self.assertEqual(build.rule_with_action({"match": raw}, "REJECT"), raw + ",REJECT")
            self.assertEqual(build.rule_with_action({"rule": raw + ",Old Group"}, "REJECT"), raw + ",REJECT")
            self.assertEqual(build.canonical_rule(raw + ",REJECT")[0], raw + ",REJECT")
            with self.assertRaises(build.BuildError):
                build.rule_with_action(raw, "REJECT")

    def test_remote_formats_preserve_regex(self):
        raw = r"DOMAIN-REGEX,^foo,bar$|^blocked\.example$"
        for content, spec in [(raw, {"rule_format": "match"}), (raw + ",OLD", {"rule_format": "rule"}),
                              ("payload: ['" + raw + "']", {}), ("rules: ['" + raw + ",OLD']", {})]:
            self.assertEqual(build.remote_rule_parser(spec)(content.encode()), [raw + ",DIRECT"])

    def test_dns_sensitive_and_dynamic_duplicates_survive(self):
        for rule in ["IP-CIDR,127.0.0.0/8,REJECT,no-resolve", "DOMAIN,test.example,DynamicGroup",
                     "OR,((IP-CIDR,127.0.0.0/8,no-resolve),(IP-CIDR,192.0.2.0/24)),REJECT"]:
            candidates = [{"rule": rule, "source": "test", "stage": "first"}] * 2
            rules, report = build.merge_rules(candidates)
            self.assertEqual(rules, [rule, rule])
            self.assertEqual(report["duplicates_preserved"], 1)
            self.assertEqual(report["conflicts_preserved"], 0)

    def test_domain_scope_and_ip_resolution(self):
        self.assertEqual(build.remote_rule_parser({"behavior": "domain"})(b"*.example.com\n.example.com"),
                         [r"DOMAIN-REGEX,(?i)^[^.]+\.example\.com$,DIRECT", r"DOMAIN-REGEX,(?i)^(?:[^.]+\.)+example\.com$,DIRECT"])
        self.assertEqual(build.remote_rule_parser({"behavior": "ipcidr"})(b"127.1.2.3/8"), ["IP-CIDR,127.0.0.0/8,DIRECT"])
        self.assertEqual(build.remote_rule_parser({"behavior": "ipcidr", "no-resolve": True})(b"127.0.0.0/8"), ["IP-CIDR,127.0.0.0/8,DIRECT,no-resolve"])

    def test_kernel_rule_types(self):
        for rule in ["IN-PORT,7890", "PROCESS-NAME-WILDCARD,*chrome*", r"PROCESS-PATH-WILDCARD,C:\Apps\*"]:
            self.assertEqual(build.rule_with_action(rule, "REJECT"), rule + ",REJECT")

    def test_auto_domain_formats(self):
        entries = ["*.wechat.com", "btrace.qq.com", "+.example.org", ".example.net", "a.*.example.com"]
        expected = [r"DOMAIN-REGEX,(?i)^[^.]+\.wechat\.com$,DIRECT", "DOMAIN,btrace.qq.com,DIRECT", "DOMAIN-SUFFIX,example.org,DIRECT",
                    r"DOMAIN-REGEX,(?i)^(?:[^.]+\.)+example\.net$,DIRECT", r"DOMAIN-REGEX,(?i)^a\.[^.]+\.example\.com$,DIRECT"]
        import yaml
        import json
        for text in ["\ufeff# header\n\n" + "\n".join(entries) + "\n; end\n// end", yaml.safe_dump({"payload": entries}), yaml.safe_dump(entries), json.dumps({"payload": entries})]:
            for spec in [{"url": "https://example.com/download?id=1"}, {"behavior": "auto"}]:
                self.assertEqual(build.remote_rule_parser(spec)(text.encode()), expected)

    def test_auto_domain_overrides_and_mixed_lists(self):
        self.assertEqual(build.remote_rule_parser({"behavior": "classical"})(b"*.wechat.com\nbtrace.qq.com"),
                         ["DOMAIN-WILDCARD,*.wechat.com,DIRECT", "DOMAIN-SUFFIX,btrace.qq.com,DIRECT"])
        for text in [b"example.com\nDOMAIN,exact.example", b"DOMAIN,exact.example\nexample.com", b"127.0.0.1\n2001:db8::/32", b"example.com\n127.0.0.0/8"]:
            self.assertEqual(build.remote_rule_parser({})(text), build.remote_rule_parser({"behavior": "classical"})(text))
        for text in [b"payload: ['*.example.com', 123]", b"payload: []", b"# empty", b"bad*.example.com", b"*.example.com\nbad*.example.net"]:
            with self.assertRaises(build.BuildError):
                build.remote_rule_parser({})(text)


if __name__ == "__main__":
    unittest.main()
