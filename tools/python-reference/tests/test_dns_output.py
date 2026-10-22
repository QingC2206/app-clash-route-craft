from pathlib import Path
import copy
import json
import os
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import build
from test_builder import dump, fixture


class DnsOutputTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.settings = fixture(self.root)
        self.policy = build.read_yaml_file(self.root / "policy.yaml")
        self.policy["dns"] = copy.deepcopy(build.read_yaml_file(build.ROOT / "tests/fixtures/dns.yaml")["dns"])
        self.settings_path = self.root / "settings.yaml"
        self.target = self.root / "output/HomeChain.yaml"
        self.dns_target = self.target.with_name("HomeChain.MC2DNS.yaml")
        self.report_target = self.target.with_suffix(".report.json")
        self.save()

    def tearDown(self):
        self.temp.cleanup()

    def save(self):
        dump(self.root / "policy.yaml", self.policy)
        dump(self.settings_path, self.settings)

    def old_outputs(self):
        self.target.parent.mkdir(parents=True, exist_ok=True)
        previous = {}
        for path in (self.target, self.dns_target, self.report_target):
            previous[path] = ("old-" + path.name).encode()
            path.write_bytes(previous[path])
        return previous

    def assert_unchanged(self, previous):
        self.assertEqual({p: p.read_bytes() for p in previous}, previous)

    def test_original_dns_policy_and_new_group_reference(self):
        cfg, report = build.compile_profile(self.settings, self.root, offline=True)
        dns = cfg["dns"]
        self.assertEqual(dns["nameserver"], ["https://1.1.1.1/dns-query#住宅出口"])
        self.assertEqual(dns["direct-nameserver"], dns["nameserver"])
        self.assertEqual(dns["fallback"], ["https://8.8.8.8/dns-query#住宅出口"])
        self.assertEqual(dns["default-nameserver"], ["https://223.5.5.5/dns-query"])
        self.assertEqual(dns["proxy-server-nameserver"], ["https://223.5.5.5/dns-query#DIRECT"])
        self.assertFalse(dns["respect-rules"])
        self.assertFalse(dns["direct-nameserver-follow-policy"])
        self.assertFalse(dns["ipv6"])
        for inherited in ("nameserver-policy", "proxy-server-nameserver-policy", "fallback-filter"):
            self.assertNotIn(inherited, dns)
        self.assertEqual(report["dns"]["proxy_references"], ["DIRECT", "住宅出口"])

    def test_paired_files_and_report_share_one_dns_policy(self):
        target, report = build.generate(self.settings_path, offline=True)
        self.assertEqual(target, self.target)
        full = build.read_yaml_file(target)
        mc2 = build.read_yaml_file(self.dns_target)
        self.assertEqual(set(mc2), {"dns"})
        expected = {**full["dns"], "listen": "0.0.0.0:23453", "enhanced-mode": "redir-host"}
        self.assertEqual(mc2["dns"], expected)
        # MC2 uses awk -F: '/listen/{print $3}' | xargs, not a YAML parser.
        ports = [line.split(":")[2].strip() for line in self.dns_target.read_text(encoding="utf-8").splitlines() if "listen" in line]
        self.assertEqual(ports, ["23453"])
        self.assertEqual(full["dns"]["listen"], "127.0.0.1:1053")
        self.assertEqual(report["dns"]["mc2_file"], str(self.dns_target))
        self.assertNotIn("p#a:ss", self.dns_target.read_text(encoding="utf-8"))
        self.assertEqual(json.loads(self.report_target.read_text(encoding="utf-8"))["dns"], report["dns"])
        self.policy["dns"]["nameserver"] = ["https://9.9.9.9/dns-query#住宅出口"]
        self.save()
        build.generate(self.settings_path, offline=True)
        self.assertEqual(build.read_yaml_file(target)["dns"]["nameserver"], self.policy["dns"]["nameserver"])
        self.assertEqual(build.read_yaml_file(self.dns_target)["dns"]["nameserver"], self.policy["dns"]["nameserver"])

    def test_stale_dns_proxy_reference_does_not_replace_either_file(self):
        previous = self.old_outputs()
        self.policy["dns"]["nameserver"] = ["https://1.1.1.1/dns-query#美国静态"]
        self.save()
        with self.assertRaisesRegex(build.BuildError, "美国静态"):
            build.generate(self.settings_path, offline=True)
        self.assert_unchanged(previous)

    def test_bootstrap_resolver_required_for_proxied_dns(self):
        self.policy["dns"].pop("proxy-server-nameserver")
        self.save()
        with self.assertRaisesRegex(build.BuildError, "proxy-server-nameserver"):
            build.compile_profile(self.settings, self.root, offline=True)

    def test_runtime_dns_cannot_silently_override_policy_dns(self):
        self.settings["runtime"] = {"dns": {"enable": False}}
        with self.assertRaisesRegex(build.BuildError, "policy.yaml"):
            build.compile_profile(self.settings, self.root, offline=True)

    def test_dns_parameters_and_policy_endpoints_are_checked(self):
        self.policy["dns"]["nameserver-policy"] = {
            "+.example.com": "https://1.1.1.1/dns-query#%E4%BD%8F%E5%AE%85%E5%87%BA%E5%8F%A3&h3=false"
        }
        self.save()
        _, report = build.compile_profile(self.settings, self.root, offline=True)
        self.assertEqual(report["dns"]["proxy_references"], ["DIRECT", "住宅出口"])
        self.policy["dns"]["nameserver-policy"]["+.example.com"] = "https://1.1.1.1/dns-query#missing"
        self.save()
        with self.assertRaisesRegex(build.BuildError, "missing"):
            build.compile_profile(self.settings, self.root, offline=True)

    def test_bundle_replace_failure_restores_all_old_outputs(self):
        previous = self.old_outputs()
        real_replace = os.replace
        failed = False

        def fail_once(src, dst):
            nonlocal failed
            if Path(dst) == self.target and not failed:
                failed = True
                raise OSError("simulated main file lock")
            return real_replace(src, dst)

        with patch.object(build.os, "replace", side_effect=fail_once):
            with self.assertRaisesRegex(build.BuildError, "已保留"):
                build.generate(self.settings_path, offline=True)
        self.assertTrue(failed)
        self.assert_unchanged(previous)
        self.assertEqual(list(self.target.parent.glob(".building-*")), [])

    def test_mc2_core_check_failure_preserves_both_outputs(self):
        previous = self.old_outputs()
        exe = self.root / "mock-core.exe"
        exe.touch()
        self.settings["validation"] = {"mihomo": str(exe), "required": True}
        self.save()
        with patch.object(build, "validate_with_core", side_effect=[None, build.BuildError("MC2 check failed")]) as check:
            with self.assertRaisesRegex(build.BuildError, "MC2 check failed"):
                build.generate(self.settings_path, offline=True)
        self.assertEqual(check.call_count, 2)
        self.assertEqual(check.call_args_list[0].args[0]["dns"]["listen"], "127.0.0.1:1053")
        self.assertEqual(check.call_args_list[1].args[0]["dns"]["listen"], "0.0.0.0:23453")
        self.assert_unchanged(previous)


if __name__ == "__main__":
    unittest.main()
