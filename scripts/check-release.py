"""Verify release inventory/checksums and run each EXE after ZIP extraction."""
from pathlib import Path, PurePosixPath
import hashlib
import json
import re
import subprocess
import tempfile
import zipfile
import importlib.util

root = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('release_guard', root / 'scripts/release-guard.py')
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)
manifest = json.loads((root / "releases/release-manifest.json").read_text(encoding="utf-8-sig"))
assert {entry["name"] for entry in manifest} == {"ClashRouteCraft-win-x64"}
assert len(manifest) == 1 and len({entry["version"] for entry in manifest}) == 1
for release in manifest:
    archive = Path(release["zip"])
    assert archive == root / "releases" / (release["name"] + ".zip")
    assert Path(release["directory"]) == root / "releases" / release["name"]
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == release["sha256"]
    guard.archive(archive, root)
    with tempfile.TemporaryDirectory(prefix="homechain release check ") as temporary:
        # Windows hosted runners may spell TEMP with an 8.3 username alias.
        destination = Path(temporary).resolve()
        with zipfile.ZipFile(archive) as package:
            for member in package.namelist():
                parts = PurePosixPath(member).parts
                assert ".." not in parts and not member.startswith("/") and ":" not in member
                assert not any(p.lower() in {"config", "subscrip", "output", ".cache", ".project-trash", "settings", "resources", "node_modules"} for p in parts)
                assert not member.lower().endswith((".py", ".log", ".data", ".pdb", ".nupkg"))
            package.extractall(destination)
        directory = destination / release["name"]
        # Offline README language links, screenshots and referenced guides must resolve.
        expected_images = {"overview.png", "subscriptions.png", "residential.png", "routing.png", "custom-rules.png", "dns-privacy.png", "output.png"}
        assert {p.name for p in (directory / "docs/screenshots").glob("*.png")} == expected_images
        for relative in ["README.md", "README.en.md", "docs/usage.md", "docs/validation.md", "docs/router-asn.md", "docs/releasing.md", "docs/screenshots/README.md", *["docs/screenshots/" + name for name in expected_images]]:
            assert (directory / relative).read_bytes() == (root / relative).read_bytes(), relative
        for document in directory.rglob("*.md"):
            for target in re.findall(r'!?\[[^\]]*\]\(([^)]+)\)', document.read_text(encoding="utf-8-sig")):
                if target.startswith(("https://", "http://", "#")): continue
                linked = (document.parent / target.split("#", 1)[0]).resolve()
                assert linked.is_relative_to(directory) and linked.is_file(), f"Broken packaged link: {document.name}: {target}"
        sums = (directory / "SHA256SUMS.txt").read_text(encoding="utf-8").splitlines()
        for entry in sums:
            checksum, relative = entry.split("  ", 1)
            assert hashlib.sha256((directory / relative).read_bytes()).hexdigest() == checksum
            assert hashlib.sha256((Path(release["directory"]) / relative).read_bytes()).hexdigest() == checksum
        assert len([p for p in directory.rglob("*") if p.is_file()]) == release["files"]
        response = destination / "native smoke result.json"
        process = subprocess.run([str(directory / "ClashRouteCraft.exe"), "--self-check", "--result", str(response)], capture_output=True, timeout=45)
        result = json.loads(response.read_text(encoding="utf-8"))
        assert result.get("residence_proxy_probe") is True and result.get("rule_semantics") is True and result.get("rule_semantics_v2") is True and result.get("domain_list_auto_detection") is True
        assert process.returncode == 0 and result["ok"] and result["pages"] == 8 and result["project_crud"] and result["ordering"] and result["restore_and_subscription_identity"] and result["classified_rules"] and result["readonly_example"] and result["parameter_remote_urls"] and result["standalone_subscription_pull"] and result["forced_subscription_cache_fallback"]
        assert release["full"] is False and not (directory / "resources").exists()
        initialized = destination / "fresh workspace"
        init_result = destination / "initialize.json"
        init = subprocess.run([str(directory / "ClashRouteCraft.exe"), "--initialize", "--workspace", str(initialized), "--result", str(init_result)], capture_output=True, timeout=20)
        data = json.loads(init_result.read_text(encoding="utf-8"))
        assert init.returncode == 0 and data["ok"] and len(data["restored"]) == 4 and len(data["missing_resources"]) == 5
        policy = initialized / "config/Example/policy.yaml"
        policy.unlink()
        custom = initialized / "settings/rule-defaults.yaml"
        custom.write_text("direct: ['DOMAIN,custom.example']\n", encoding="utf-8")
        before = custom.read_bytes()
        init = subprocess.run([str(directory / "ClashRouteCraft.exe"), "--initialize", "--workspace", str(initialized), "--result", str(init_result)], capture_output=True, timeout=20)
        assert init.returncode == 0 and len(json.loads(init_result.read_text(encoding="utf-8"))["restored"]) == 1 and custom.read_bytes() == before
        assert not (initialized / "resources").exists()
        print(f"PASS {release['name']}: bilingual README, 7 screenshots, offline links, inventory, SHA-256 and native smoke check after extraction")
print("RESULT portable distribution verified")
