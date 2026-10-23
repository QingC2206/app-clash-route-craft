"""Fail closed on private paths, unexpected package files and version mismatches.

This is a publishing guard, not a substitute for reviewing source and screenshots.
Only filenames (never matched secret values) are included in error messages.
"""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import json
import os
import re
import struct
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
NAME = "ClashRouteCraft-win-x64"
PROGRAM = {"ClashRouteCraft.exe", "av_libglesv2.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll"}
DOCUMENTS = {"LICENSE", "THIRD-PARTY-NOTICES.md", "README.md", "README.en.md",
             "docs/usage.md", "docs/validation.md", "docs/router-asn.md", "docs/releasing.md",
             "docs/screenshots/README.md"}
DOCUMENTS |= {f"docs/screenshots/{name}.png" for name in
              ("overview", "subscriptions", "residential", "routing", "custom-rules", "dns-privacy", "output")}
LICENSES = {f"licenses/{name}" for name in (
    "Avalonia-LICENSE.txt", "AvaloniaEdit-LICENSE.txt", "YamlDotNet-LICENSE.txt",
    "dotnet-runtime-LICENSE.txt", "SkiaSharp-LICENSE.txt", "ANGLE-LICENSE.txt",
    "HarfBuzz-LICENSE.txt", "Mihomo-LICENSE.txt", "dotnet-LICENSE.TXT", "dotnet-THIRD-PARTY-NOTICES.TXT")}
EXPECTED = PROGRAM | DOCUMENTS | LICENSES | {"SHA256SUMS.txt"}
PRIVATE_PARTS = {"config", "settings", "output", "subscrip", ".cache", ".project-trash", "artifacts", "releases", "__macosx"}
SECRET = re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|https?://[^\s/:]+:[^\s/@]+@|[?&](?:token|access_token|key)=[A-Za-z0-9_-]{24,}", re.I)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def check_path(path):
    parts = PurePosixPath(path).parts
    require(parts and ".." not in parts and not path.startswith("/") and "\\" not in path and ":" not in path,
            "Unsafe file path")
    require(not any(p.lower() in PRIVATE_PARTS or p.lower().startswith(("router-login", "router-known_hosts", "._"))
                    or p.lower() in {".ds_store", ".env"} for p in parts), f"Private path: {path}")


def check_text(path, data):
    require(not SECRET.search(data), f"Possible credential in {path}; review locally (value redacted)")


def version(root=ROOT, tag=None):
    value = ET.parse(root / "src/ClashRouteCraft.Desktop/ClashRouteCraft.Desktop.csproj").findtext(".//Version")
    require(bool(re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?", value or "")), "Invalid project version")
    if tag is not None:
        require(tag == "v" + value, "Version tag must equal v + the desktop project Version")
    return value


def source():
    tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode().split("\0")
    ignored = subprocess.check_output(["git", "ls-files", "-ci", "--exclude-standard", "-z"], cwd=ROOT)
    require(not ignored, "Tracked files match .gitignore; remove private/build files from the index before release")
    for name in filter(None, tracked):
        path = ROOT / name
        if name == 'scripts/router-login.example.json':
            require(not json.loads(path.read_text(encoding='utf-8-sig')).get('password'), 'Login example must not contain a password')
        else:
            check_path(name)
        require(not path.is_symlink(), f"Tracked symlink requires review: {name}")
        if path.suffix.lower() not in {".png", ".ico"}:
            check_text(name, path.read_bytes())
    tag = os.environ.get("GITHUB_REF_NAME") if os.environ.get("GITHUB_REF_TYPE") == "tag" else None
    return version(tag=tag)


def check_native_exe(data):
    require(data[:2] == b"MZ" and len(data) > 256, "Missing Windows executable")
    pe = struct.unpack_from("<I", data, 60)[0]
    require(data[pe:pe+4] == b"PE\0\0", "Invalid PE header")
    require(struct.unpack_from("<H", data, pe+4)[0] == 0x8664, "Expected Windows x64 executable")
    optional = pe+24
    require(struct.unpack_from("<H", data, optional)[0] == 0x20B, "Expected PE32+")
    require(struct.unpack_from("<II", data, optional+112+14*8) == (0, 0), "Managed CLR executable is not Native AOT")


def archive(path, source_root=None):
    with zipfile.ZipFile(path) as package:
        entries = [e for e in package.infolist() if not e.is_dir()]
        names = [e.filename for e in entries]
        require(len(names) == len(set(names)), "Duplicate archive entries")
        for entry in package.infolist():
            check_path(entry.filename)
            require((entry.external_attr >> 16) & 0o170000 != 0o120000, "Archive symlink rejected")
        require(set(names) == {f"{NAME}/{p}" for p in EXPECTED}, "Archive inventory differs from the public allowlist")
        sums = {}
        for line in package.read(f"{NAME}/SHA256SUMS.txt").decode("utf-8-sig").splitlines():
            digest, relative = line.split("  ", 1)
            require(relative not in sums and re.fullmatch("[0-9a-f]{64}", digest), "Invalid checksum inventory")
            sums[relative] = digest
        require(set(sums) == EXPECTED - {"SHA256SUMS.txt"}, "Checksum inventory is incomplete")
        for relative in EXPECTED - {"SHA256SUMS.txt"}:
            data = package.read(f"{NAME}/{relative}")
            require(hashlib.sha256(data).hexdigest() == sums[relative], f"Checksum mismatch: {relative}")
            if relative.endswith((".md", ".txt", ".TXT")):
                check_text(relative, data)
            if source_root and relative in DOCUMENTS:
                require(data == (source_root / relative).read_bytes(), f"Outdated document: {relative}")
        check_native_exe(package.read(f"{NAME}/ClashRouteCraft.exe"))
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["source", "archive", "verify"])
    parser.add_argument("--github-output", action="store_true")
    args = parser.parse_args()
    if args.mode == "source":
        result = source()
        if args.github_output:
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
                output.write(f"version={result}\n")
        print(f"PASS source privacy paths and version {result}")
    else:
        digest = archive(ROOT / f"releases/{NAME}.zip", ROOT)
        checksum = ROOT / "releases/SHA256SUMS.txt"
        expected = f"{digest}  {NAME}.zip\n"
        if args.mode == 'verify':
            require(checksum.read_text(encoding='utf-8') == expected, 'Transferred archive checksum mismatch')
        else:
            checksum.write_text(expected, encoding="utf-8")
        print("PASS single AOT archive, public inventory, documentation and checksums")
