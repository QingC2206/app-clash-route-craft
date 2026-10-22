from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import build
from test_builder import dump, fixture


class Mc2CompatibilityTests(unittest.TestCase):
    def test_asn_preserves_action_order_and_no_resolve(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'asn.list').write_text('1.2.3.0/24\n2001:db8::/32\n1.2.3.0/24\n')
            cfg = {'rules': ['DOMAIN,example.com,DIRECT', 'IP-ASN,20473,REJECT,no-resolve', 'MATCH,DIRECT']}
            summary = build.embed_asn_sources(cfg, {20473: {'file': 'asn.list'}}, build.Fetcher(root, {}, True), root)
            self.assertEqual(cfg['rules'], ['DOMAIN,example.com,DIRECT',
                             'RULE-SET,HomeChain-ASN-20473,REJECT,no-resolve', 'MATCH,DIRECT'])
            self.assertEqual(cfg['rule-providers']['HomeChain-ASN-20473']['payload'], ['1.2.3.0/24', '2001:db8::/32'])
            self.assertEqual(summary[0]['entries'], 2)

    def test_embedded_geosite_keeps_position_and_actions_without_router_download(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            settings = fixture(root)
            policy = build.read_yaml_file(root / 'policy.yaml')
            policy['domestic_direct'] = True
            policy['geo_mode'] = 'mc2-inline'
            policy['geosite_sources'] = {'geolocation-!cn': {'file': 'noncn.list'}}
            (root / 'noncn.list').write_text('DOMAIN-SUFFIX,foreign.example\nDOMAIN-SUFFIX,foreign.example\nDOMAIN,other.example\n')
            dump(root / 'policy.yaml', policy)
            cfg, report = build.compile_profile(settings, root, offline=True)
            self.assertEqual(cfg['rules'][-4:], [
                'RULE-SET,HomeChain-GeoSite-geolocation-!cn,默认代理',
                'GEOSITE,CN,DIRECT', 'GEOIP,CN,DIRECT', 'MATCH,默认代理'])
            provider = cfg['rule-providers']['HomeChain-GeoSite-geolocation-!cn']
            self.assertEqual(provider, {'type': 'inline', 'behavior': 'classical', 'payload': [
                'DOMAIN-SUFFIX,foreign.example', 'DOMAIN,other.example']})
            self.assertEqual(report['embedded_geosite'][0]['entries'], 2)

    def test_missing_or_invalid_list_aborts_instead_of_silently_removing_rule(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            fetch = build.Fetcher(root, {}, offline=True)
            cfg = {'rules': ['GEOSITE,geolocation-!cn,DIRECT', 'MATCH,REJECT']}
            spec = {'geolocation-!cn': {'file': 'list.txt'}}
            with self.assertRaises(build.BuildError):
                build.embed_geosite_sources(cfg, spec, fetch, root)
            (root / 'list.txt').write_text('IP-CIDR,1.1.1.1/32\n')
            with self.assertRaisesRegex(build.BuildError, '域名'):
                build.embed_geosite_sources(cfg, spec, fetch, root)
            self.assertEqual(cfg['rules'][0], 'GEOSITE,geolocation-!cn,DIRECT')


if __name__ == '__main__':
    unittest.main()
