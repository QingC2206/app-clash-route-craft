from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch, MagicMock
import requests
import build
from test_builder import fixture, dump


class ProjectTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.project = self.root / "config" / "SampleOne"
        self.project.mkdir(parents=True)
        fixture(self.project)
        (self.project / "subscrip").mkdir()
        for name in ["A", "B"]:
            (self.project / f"{name}.yaml").replace(self.project / "subscrip" / f"{name}.yaml")
        self.settings = {"sources": [{"name": "A", "url": "https://example.invalid/a"},
                                     {"name": "B", "url": "https://example.invalid/b"}],
                         "validation": {}}
        self.save()

    def save(self):
        dump(self.project / "settings.yaml", self.settings)

    def tearDown(self):
        self.tmp.cleanup()

    def run_project(self, force=False):
        return build.build_project(self.project, force, self.root)

    def test_default_local_and_project_output(self):
        with patch.object(requests.Session, "get", side_effect=AssertionError("network forbidden")):
            target, report = self.run_project()
        self.assertEqual(target, self.root / "output/SampleOne/HomeChain.yaml")
        self.assertEqual(report["priority_order"], [1, 2])
        self.assertEqual([s["source"] for s in report["sources"]], ["A", "B"])

    def test_missing_snapshot_does_not_download(self):
        (self.project / "subscrip/A.yaml").unlink()
        with patch.object(requests.Session, "get", side_effect=AssertionError("network forbidden")):
            with self.assertRaisesRegex(build.BuildError, "-f"):
                self.run_project()

    def test_force_failure_preserves_snapshots_and_outputs(self):
        target, _ = self.run_project()
        before = target.read_bytes()
        a = self.project / "subscrip/A.yaml"
        old = a.read_bytes()
        with patch.object(requests.Session, "get", side_effect=requests.ConnectionError("secret URL")):
            with self.assertRaises(build.BuildError) as caught:
                self.run_project(True)
        self.assertNotIn("secret", str(caught.exception))
        self.assertEqual(a.read_bytes(), old)
        self.assertEqual(target.read_bytes(), before)

    def response(self, payload):
        res = MagicMock()
        res.__enter__.return_value = res
        res.ok = True
        res.iter_content.return_value = [payload]
        return res

    def test_force_success_and_default_reuse(self):
        payload = build.yaml_bytes({"proxies": [{"name": "香港新节点", "type": "http", "server": "127.0.0.1", "port": 8080}]})
        with patch.object(requests.Session, "get", side_effect=lambda *a, **k: self.response(payload)) as get:
            _, report = self.run_project(True)
            self.assertEqual(get.call_count, 2)
        self.assertEqual(report["proxy_count"], 3)
        with patch.object(requests.Session, "get", side_effect=AssertionError("network forbidden")):
            self.run_project()
        self.assertIn("香港新节点", (self.project / "subscrip/A.yaml").read_text(encoding="utf-8"))

    def test_partial_refresh_never_commits(self):
        target, _ = self.run_project()
        before = target.read_bytes()
        old = (self.project / "subscrip/A.yaml").read_bytes()
        with patch.object(requests.Session, "get", side_effect=[self.response(old), requests.ConnectionError()]):
            with self.assertRaises(build.BuildError):
                self.run_project(True)
        self.assertEqual(old, (self.project / "subscrip/A.yaml").read_bytes())
        self.assertEqual(before, target.read_bytes())

    def test_bad_names_rejected(self):
        for name in ["../escape", "CON", "a/b", "A."]:
            self.settings["sources"][0]["name"] = name
            self.save()
            with self.assertRaises(build.BuildError):
                self.run_project()

    def test_projects_isolated(self):
        import shutil
        second = self.root / "config/SampleTwo"
        shutil.copytree(self.project, second)
        first, _ = self.run_project()
        other, _ = build.build_project(second, False, self.root)
        self.assertNotEqual(first, other)
        self.assertTrue(first.is_file() and other.is_file())

    def test_discovery_skips_examples_even_with_settings(self):
        example = self.root / "config" / "Examples"
        example.mkdir()
        (example / "settings.yaml").write_text("invalid example", encoding="utf-8")
        self.assertEqual(build.discover_projects(self.root), [self.project])

    def test_provider_payload_cannot_hide_failed_refresh(self):
        doc = build.yaml_bytes({"proxy-providers": {"remote": {"type": "http", "url": "https://example.invalid/provider", "payload": [{"name": "香港", "type": "http", "server": "localhost", "port": 80}]}}})
        with patch.object(requests.Session, "get", side_effect=[self.response(doc), requests.ConnectionError()]):
            with self.assertRaises(build.BuildError):
                self.run_project(True)
