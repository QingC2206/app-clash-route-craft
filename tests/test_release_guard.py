"""Offline adversarial fixtures for the public release boundary."""
import hashlib
import importlib.util
import os
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


guard = load('guard', 'release-guard.py')
publisher = load('publisher', 'publish-release.py')


class ReleaseGuardTests(unittest.TestCase):
    def make_zip(self, directory, extra=None, changed=None, missing=None):
        files = {p: b'public fixture' for p in guard.EXPECTED - {'SHA256SUMS.txt'}}
        pe = bytearray(512)
        pe[:2] = b'MZ'
        struct.pack_into('<I', pe, 60, 128)
        pe[128:132] = b'PE\0\0'
        struct.pack_into('<H', pe, 132, 0x8664)
        struct.pack_into('<H', pe, 152, 0x20B)
        files['ClashRouteCraft.exe'] = bytes(pe)
        files.update(changed or {})
        files['SHA256SUMS.txt'] = ''.join(f'{hashlib.sha256(b).hexdigest()}  {p}\n' for p, b in files.items()).encode()
        if missing:
            del files[missing]
        path = Path(directory) / 'fixture.zip'
        with zipfile.ZipFile(path, 'w') as archive:
            for name, data in files.items():
                archive.writestr(f'{guard.NAME}/{name}', data)
            if extra:
                archive.writestr(extra, b'private fixture')
        return path

    def test_exact_public_archive(self):
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(len(guard.archive(self.make_zip(directory))), 64)

    def test_rejects_private_and_unknown_files(self):
        for extra in ('config/private.yaml', 'licenses/router-login.json', 'docs/personal.txt', '../escape', '._README.md'):
            with self.subTest(extra=extra), tempfile.TemporaryDirectory() as directory:
                with self.assertRaises(ValueError):
                    guard.archive(self.make_zip(directory, extra=f'{guard.NAME}/{extra}'))

    def test_rejects_missing_screenshot(self):
        with tempfile.TemporaryDirectory() as directory, self.assertRaises(ValueError):
            guard.archive(self.make_zip(directory, missing='docs/screenshots/overview.png'))

    def test_rejects_secret_without_printing_value(self):
        secret = b'ghp_' + b'a' * 36
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError) as result:
                guard.archive(self.make_zip(directory, changed={'README.md': secret}))
            self.assertNotIn(secret.decode(), str(result.exception))

    def test_rejects_checksum_tamper(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.make_zip(directory)
            replacement = Path(directory) / 'bad.zip'
            with zipfile.ZipFile(path) as original, zipfile.ZipFile(replacement, 'w') as altered:
                for name in original.namelist():
                    altered.writestr(name, b'altered' if name.endswith('/README.md') else original.read(name))
            with self.assertRaisesRegex(ValueError, 'Checksum mismatch'):
                guard.archive(replacement)

    def test_rejects_managed_pe(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.make_zip(directory)
            with zipfile.ZipFile(path) as archive:
                pe = bytearray(archive.read(f'{guard.NAME}/ClashRouteCraft.exe'))
            struct.pack_into('<II', pe, 152+112+14*8, 4096, 72)
            with self.assertRaisesRegex(ValueError, 'not Native AOT'):
                guard.check_native_exe(pe)

    def test_version_tag_must_match(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = root / 'src/ClashRouteCraft.Desktop/ClashRouteCraft.Desktop.csproj'
            project.parent.mkdir(parents=True)
            project.write_text('<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>')
            self.assertEqual(guard.version(root, 'v1.2.3'), '1.2.3')
            for tag in ('v1.2.4', '1.2.3', 'v1.2.3-extra'):
                with self.assertRaises(ValueError):
                    guard.version(root, tag)

    def test_manual_run_cannot_publish(self):
        with patch.dict(os.environ, {'GITHUB_EVENT_NAME': 'workflow_dispatch'}), patch.object(publisher, 'gh') as gh:
            with self.assertRaisesRegex(ValueError, 'requires a pushed'):
                publisher.main()
            gh.assert_not_called()

    def test_public_release_cannot_be_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'releases').mkdir()
            (root / 'releases/SHA256SUMS.txt').write_text(f'digest  {guard.NAME}.zip\n')
            env = {'GITHUB_EVENT_NAME': 'push', 'GITHUB_REF_TYPE': 'tag', 'GITHUB_REF_NAME': 'v1.2.3',
                   'GITHUB_REPOSITORY': 'example/repo'}
            with patch.dict(os.environ, env), patch.object(publisher, 'ROOT', root), \
                    patch.object(publisher.guard, 'version'), patch.object(publisher.guard, 'archive', return_value='digest'), \
                    patch.object(publisher.subprocess, 'check_output', return_value='commit'), \
                    patch.object(publisher, 'gh', side_effect=['commit', '[[{"tag_name":"v1.2.3","draft":false}]]']) as gh:
                with self.assertRaisesRegex(ValueError, 'not overwritten'):
                    publisher.main()
                self.assertTrue(all(call.args[0] == 'api' for call in gh.call_args_list))

    def test_moved_tag_cannot_publish(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'releases').mkdir()
            (root / 'releases/SHA256SUMS.txt').write_text(f'digest  {guard.NAME}.zip\n')
            env = {'GITHUB_EVENT_NAME': 'push', 'GITHUB_REF_TYPE': 'tag', 'GITHUB_REF_NAME': 'v1.2.3',
                   'GITHUB_REPOSITORY': 'example/repo'}
            with patch.dict(os.environ, env), patch.object(publisher, 'ROOT', root), \
                    patch.object(publisher.guard, 'version'), patch.object(publisher.guard, 'archive', return_value='digest'), \
                    patch.object(publisher.subprocess, 'check_output', return_value='old-commit'), \
                    patch.object(publisher, 'gh', return_value='new-commit') as gh:
                with self.assertRaisesRegex(ValueError, 'tag moved'):
                    publisher.main()
                self.assertEqual(gh.call_count, 1)


if __name__ == '__main__':
    unittest.main()
