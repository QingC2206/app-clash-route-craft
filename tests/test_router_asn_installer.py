"""Offline regression tests. Executes shell commands only in disposable local fixtures."""
from pathlib import Path
import argparse
import builtins
import base64
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch, MagicMock

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("installer", ROOT / "scripts/install-router-asn.py")
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
SHELL = os.environ.get("CRC_TEST_BASH") or (r"C:\Program Files\Git\bin\bash.exe" if os.name == "nt" else shutil.which("sh"))
DATA = b"fixture-asn-database" * 200 + b"\xab\xcd\xefMaxMind.comdatabase_type GeoLite2-ASN"


def unix(path):
    text = Path(path).resolve().as_posix()
    return "/" + text[0].lower() + text[2:] if os.name == "nt" else text


class LocalRemote(installer.Remote):
    """Use a disposable filesystem as the router, exercising the real shell commands."""
    def __init__(self, fault=None):
        self.fault = fault
        self.validations = 0
        self.hashes = 0

    def run(self, command, data=None, timeout=90):
        if "df -Pk " in command:
            # Git Bash's df can probe disconnected Windows volumes; emulate router df.
            return "0" if self.fault == "disk_full" else "10485760"
        if " -t -d " in command:
            self.validations += 1
            if self.fault == "invalid_database" or (self.fault == "after_replace" and self.validations == 2):
                raise installer.InstallError("fixture core rejected database")
        result = subprocess.run([SHELL, "-c", command], input=data, capture_output=True, timeout=timeout)
        if result.returncode:
            raise installer.InstallError(result.stdout.decode(errors="replace") + result.stderr.decode(errors="replace"))
        return result.stdout.decode(errors="replace").strip()

    def hash(self, path):
        if self.fault == "upload_mismatch" and path.endswith(".new"):
            return "0" * 64
        return super().hash(path)


@unittest.skipUnless(SHELL and Path(SHELL).exists(), "POSIX shell required; set CRC_TEST_BASH on Windows")
class InstallationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="crc-asn-test-")
        self.root = Path(self.temp.name)
        self.folder = self.root / "data with spaces"
        self.folder.mkdir()
        self.core = self.root / "fake-core.sh"
        self.core.write_text('#!/bin/sh\n[ "$1" = "-t" ] || exit 2\n[ -s "$3/ASN.mmdb" ] || exit 3\nprintf "configuration file test is successful\\n"\n', encoding="utf-8", newline="\n")
        self.core.chmod(0o755)
        self.target = self.folder / "ASN.mmdb"
        self.stdout = contextlib.redirect_stdout(io.StringIO())
        self.stderr = contextlib.redirect_stderr(io.StringIO())
        self.stdout.__enter__(); self.stderr.__enter__()

    def tearDown(self):
        self.stdout.__exit__(None, None, None); self.stderr.__exit__(None, None, None)
        self.temp.cleanup()

    def run_install(self, fault=None):
        return installer.install(LocalRemote(fault), DATA, unix(self.folder), unix(self.core))

    def test_new_install_and_idempotent_retry(self):
        result = self.run_install()
        self.assertEqual(result["status"], "installed")
        self.assertIsNone(result["backup"])
        self.assertFalse(result["service_restarted"])
        self.assertEqual(self.target.read_bytes(), DATA)
        self.assertEqual(self.run_install()["status"], "already_installed")
        self.assertEqual([p.name for p in self.folder.iterdir()], ["ASN.mmdb"])

    def test_replacement_keeps_verified_backup(self):
        self.target.write_bytes(b"original database")
        result = self.run_install()
        backups = list(self.folder.glob(".ASN.mmdb.backup-*"))
        self.assertEqual(len(backups), 1)
        self.assertEqual(backups[0].read_bytes(), b"original database")
        self.assertEqual(result["backup"], unix(backups[0]))
        self.assertEqual(self.target.read_bytes(), DATA)

    def test_precommit_failures_preserve_original(self):
        for fault in ("upload_mismatch", "invalid_database", "disk_full"):
            with self.subTest(fault=fault):
                self.target.write_bytes(b"original database")
                with self.assertRaises(installer.InstallError): self.run_install(fault)
                self.assertEqual(self.target.read_bytes(), b"original database")
                self.assertEqual([p.name for p in self.folder.iterdir()], ["ASN.mmdb"])

    def test_postcommit_failure_restores_original(self):
        self.target.write_bytes(b"original database")
        with self.assertRaises(installer.InstallError): self.run_install("after_replace")
        self.assertEqual(self.target.read_bytes(), b"original database")

    def test_postcommit_failure_without_original_removes_new_file(self):
        with self.assertRaises(installer.InstallError): self.run_install("after_replace")
        self.assertFalse(self.target.exists())

    def test_competing_installer_lock_is_preserved(self):
        lock = self.folder / ".crc-asn-install.lock"
        lock.mkdir()
        with self.assertRaises(installer.InstallError): self.run_install()
        self.assertTrue(lock.exists())
        self.assertFalse(self.target.exists())

    def test_openssl_only_checksum(self):
        self.target.write_bytes(DATA)
        class OpenSslRemote(LocalRemote):
            def run(self, command, **kwargs):
                command = "sha256sum() { echo 'sha256sum: applet not found' >&2; return 127; }; " + command
                return super().run(command, **kwargs)
        self.assertEqual(OpenSslRemote().hash(unix(self.target)), hashlib.sha256(DATA).hexdigest())

    def test_invalid_digest_also_falls_back_to_openssl(self):
        self.target.write_bytes(DATA)
        class InvalidDigestRemote(LocalRemote):
            def run(self, command, **kwargs):
                return super().run("sha256sum() { echo 'unsupported'; return 0; }; " + command, **kwargs)
        self.assertEqual(InvalidDigestRemote().hash(unix(self.target)), hashlib.sha256(DATA).hexdigest())

    def test_busybox_fallback_after_two_failed_backends(self):
        self.target.write_bytes(DATA)
        class BusyBoxRemote(LocalRemote):
            def run(self, command, **kwargs):
                prefix = 'sha256sum() { return 127; }; openssl() { return 127; }; busybox() { command sha256sum "$2"; }; '
                return super().run(prefix + command, **kwargs)
        self.assertEqual(BusyBoxRemote().hash(unix(self.target)), hashlib.sha256(DATA).hexdigest())

    def test_all_hash_backends_fail_preserves_original_and_cleans_lock(self):
        self.target.write_bytes(b"original database")
        class NoHashRemote(LocalRemote):
            def run(self, command, **kwargs):
                prefix = 'sha256sum() { return 127; }; openssl() { return 127; }; busybox() { return 127; }; '
                return super().run(prefix + command, **kwargs)
        with self.assertRaisesRegex(installer.InstallError,"Cannot calculate router SHA-256"):
            installer.install(NoHashRemote(),DATA,unix(self.folder),unix(self.core))
        self.assertEqual(self.target.read_bytes(),b"original database")
        self.assertEqual([p.name for p in self.folder.iterdir()],["ASN.mmdb"])

    def test_complete_install_with_broken_busybox_sha256sum(self):
        class OpenSslRemote(LocalRemote):
            def run(self, command, **kwargs):
                prefix = "sha256sum() { echo 'sha256sum: applet not found' >&2; return 127; }; "
                return super().run(prefix + command, **kwargs)
        self.target.write_bytes(b"original database")
        result = installer.install(OpenSslRemote(),DATA,unix(self.folder),unix(self.core))
        self.assertEqual(result["status"],"installed")
        self.assertEqual(self.target.read_bytes(),DATA)


class InputTests(unittest.TestCase):
    def test_release_request_uses_json_accept(self):
        with patch.object(installer.urllib.request,"urlopen",return_value=io.BytesIO(b"{}")) as request:
            self.assertEqual(installer.fetch(installer.RELEASE,100),b"{}")
            self.assertEqual(request.call_args.args[0].get_header("Accept"),"application/vnd.github+json")

    def test_unsafe_remote_paths_rejected(self):
        for path in ("/", "//", "/./", ".", "relative/path", "/a/../b", "/a\nb", "/a\x00", "/a\t"):
            with self.subTest(path=path), self.assertRaises(installer.InstallError): installer.remote_path(path)

    def test_download_hash_mismatch_rejected(self):
        release = {"assets": [{"name": installer.ASSET, "digest": "sha256:" + "0" * 64,
                               "browser_download_url": "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/GeoLite2-ASN.mmdb"}]}
        with patch.object(installer, "fetch", side_effect=[json.dumps(release).encode(), DATA]), self.assertRaises(installer.InstallError):
            installer.database()

    def test_missing_release_digest_rejected(self):
        with patch.object(installer, "fetch", return_value=b'{"assets":[]}'), self.assertRaises(installer.InstallError): installer.database()

    def test_local_non_asn_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "ASN.mmdb"
            path.write_bytes(b"not a database" * 200)
            with self.assertRaises(installer.InstallError): installer.database(path)


class StatusTests(unittest.TestCase):
    class Terminal(io.StringIO):
        @property
        def encoding(self): return "utf-8"
        def isatty(self): return True

    class AsciiTerminal(Terminal):
        @property
        def encoding(self): return "ascii"

    def test_success_and_failure_colors_keep_text_labels(self):
        for ok, icon, code, label in ((True,"✅","32","[OK]"),(False,"❌","31","[FAIL]")):
            with self.subTest(ok=ok):
                stream=self.Terminal()
                with patch.object(installer,"terminal_color",return_value=True), patch.dict(os.environ,{"TERM":"xterm"}):
                    installer.status_line(ok,"中文状态","English status",stream=stream)
                self.assertIn(icon,stream.getvalue());self.assertIn(label,stream.getvalue())
                self.assertIn("\033["+code+"m",stream.getvalue());self.assertIn("\033[0m",stream.getvalue())

    def test_ascii_terminal_falls_back_without_encoding_error(self):
        stream=self.AsciiTerminal()
        with patch.object(installer,"terminal_color",return_value=False):
            installer.status_line(False,"失败","FAILED",stream=stream)
        self.assertEqual(stream.getvalue(),"[FAIL] FAILED\n")
        stream.getvalue().encode("ascii")

    def test_plain_redirected_and_dumb_outputs_have_no_escape_codes_or_icons(self):
        for stream, plain, term in ((self.Terminal(),True,"xterm"),(io.StringIO(),False,"xterm"),(self.Terminal(),False,"dumb")):
            with patch.dict(os.environ,{"TERM":term}):
                installer.status_line(True,"成功","SUCCESS",plain=plain,stream=stream)
            self.assertIn("[OK]",stream.getvalue())
            self.assertNotIn("✅",stream.getvalue());self.assertNotIn("\033",stream.getvalue())

    def test_no_color_environment_disables_colors(self):
        with patch.dict(os.environ,{"NO_COLOR":"1"}):
            self.assertFalse(installer.terminal_color(self.Terminal()))

    def test_installed_and_already_installed_have_distinct_success_messages(self):
        for status, word in (("installed","安装成功"),("already_installed","无需重复安装")):
            stream=io.StringIO()
            with contextlib.redirect_stdout(stream):
                installer.show_result({"status":status,"path":"/fixture/ASN.mmdb"},plain=True)
            self.assertIn(word,stream.getvalue());self.assertIn("MC2 未重启",stream.getvalue())


class LoginFlowTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.login = Path(self.temp.name)/"router-login.json"
        self.login.write_text(json.dumps({"host":"127.0.0.1","port":2222,"username":"fixture-user","password":"fixture-密码"},ensure_ascii=False),encoding="utf-8-sig")

    def tearDown(self): self.temp.cleanup()

    def test_file_login_authenticates_before_download_and_never_prints_password(self):
        events=[]; client=MagicMock()
        def connect(_module,args,password):
            self.assertEqual((args.host,args.port,args.user,password),("127.0.0.1",2222,"fixture-user","fixture-密码"))
            events.append("authenticate"); return client
        def database(_path): events.append("download"); return DATA
        output=io.StringIO()
        with patch.object(installer,"LOGIN_FILE",self.login), patch.object(sys,"argv",["install-router-asn.py"]), \
             patch.object(installer,"dependency",return_value=object()), patch.object(installer,"connect",side_effect=connect), \
             patch.object(installer,"database",side_effect=database), patch.object(installer,"install",return_value={"status":"installed"}), \
             patch.object(installer.getpass,"getpass") as prompt, contextlib.redirect_stdout(output):
            self.assertEqual(installer.main(),0)
        self.assertEqual(events,["authenticate","download"])
        prompt.assert_not_called(); client.close.assert_called_once()
        self.assertNotIn("fixture-密码",output.getvalue())

    def test_authentication_failure_does_not_download(self):
        errors=io.StringIO()
        with patch.object(installer,"LOGIN_FILE",self.login), patch.object(sys,"argv",["install-router-asn.py"]), \
             patch.object(installer,"dependency",return_value=object()), patch.object(installer,"connect",side_effect=installer.InstallError("Authentication failed")), \
             patch.object(installer,"database") as download, contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(errors):
            self.assertEqual(installer.main(),1)
        download.assert_not_called()
        self.assertIn("[FAIL]",errors.getvalue())

    def test_json_mode_preserves_final_machine_result(self):
        output=io.StringIO()
        result={"status":"already_installed","core_validation":"passed"}
        with patch.object(installer,"LOGIN_FILE",self.login), patch.object(sys,"argv",["install-router-asn.py","--json"]), \
             patch.object(installer,"dependency",return_value=object()), patch.object(installer,"connect",return_value=MagicMock()), \
             patch.object(installer,"database",return_value=DATA), patch.object(installer,"install",return_value=result), contextlib.redirect_stdout(output):
            self.assertEqual(installer.main(),0)
        self.assertNotIn("[OK]",output.getvalue())
        self.assertEqual(json.loads(output.getvalue()[output.getvalue().index('{'):]),result)

    def test_download_failure_closes_authenticated_session(self):
        client=MagicMock()
        with patch.object(installer,"LOGIN_FILE",self.login), patch.object(sys,"argv",["install-router-asn.py"]), \
             patch.object(installer,"dependency",return_value=object()), patch.object(installer,"connect",return_value=client), \
             patch.object(installer,"database",side_effect=installer.InstallError("Download failed")), \
             contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(installer.main(),1)
        client.close.assert_called_once()

    def test_cli_override_does_not_reuse_other_targets_password(self):
        args=argparse.Namespace(login_file=str(self.login),host="different.invalid",user=None,port=None)
        with contextlib.redirect_stdout(io.StringIO()): password=installer.login_options(args)
        self.assertEqual(password,"");self.assertEqual(args.host,"different.invalid")

    def test_invalid_login_file_error_does_not_echo_contents(self):
        self.login.write_text('{"password":"fixture-secret",broken}',encoding="utf-8")
        with self.assertRaises(installer.InstallError) as error: installer.read_login(self.login)
        self.assertNotIn("fixture-secret",str(error.exception))

    def test_password_stdin_overrides_saved_password(self):
        client=MagicMock()
        with patch.object(installer,"LOGIN_FILE",self.login), patch.object(sys,"argv",["install-router-asn.py","--password-stdin"]), \
             patch.object(sys,"stdin",io.StringIO("override-secret\n")), patch.object(installer,"dependency",return_value=object()), \
             patch.object(installer,"connect",return_value=client) as connect, patch.object(installer,"database",return_value=DATA), \
             patch.object(installer,"install",return_value={}), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(installer.main(),0)
        self.assertEqual(connect.call_args.args[2],"override-secret")

    def test_ready_isolated_dependency_is_reused_without_pip(self):
        base=Path(self.temp.name)/"ClashRouteCraft/router-tools"
        executable=base/("Scripts/python.exe" if os.name=="nt" else "bin/python")
        executable.parent.mkdir(parents=True); executable.touch()
        original_import=builtins.__import__
        def missing_dependency(name,*args,**kwargs):
            if name=="paramiko": raise ImportError("fixture missing global dependency")
            return original_import(name,*args,**kwargs)
        with patch.dict(os.environ,{"LOCALAPPDATA":self.temp.name}), patch("builtins.__import__",side_effect=missing_dependency), \
             patch.object(installer.subprocess,"run",return_value=argparse.Namespace(returncode=0)) as probe, \
             patch.object(installer.subprocess,"call",return_value=0) as launch, self.assertRaises(SystemExit) as outcome:
            installer.dependency()
        self.assertEqual(outcome.exception.code,0)
        probe.assert_called_once(); self.assertIn("-c",probe.call_args.args[0])
        self.assertNotIn("pip",str(probe.call_args));launch.assert_called_once()


try:
    import paramiko
except ImportError:
    paramiko = None


@unittest.skipUnless(paramiko, "Paramiko needed for loopback SSH authentication/transfer tests")
class SshTests(unittest.TestCase):
    """Real SSH transport, no SFTP server; never connects to a router."""
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.auth_attempts = []
        self.received = bytearray()
        self.key = paramiko.RSAKey.generate(2048)
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0)); self.listener.listen(1)
        self.port = self.listener.getsockname()[1]
        self.fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(self.key.asbytes()).digest()).decode().rstrip("=")
        self.args = argparse.Namespace(host="127.0.0.1", user="fixture-admin", port=self.port,
                                      known_hosts=str(Path(self.temp.name)/"known_hosts"), host_key_sha256=self.fingerprint)
        self.server_error = None
        self.transport = None
        attempts, received = self.auth_attempts, self.received
        class Server(paramiko.ServerInterface):
            def check_auth_password(self, username, password):
                attempts.append((username, password))
                return paramiko.AUTH_SUCCESSFUL if password == "fixture-password" else paramiko.AUTH_FAILED
            def check_channel_request(self, kind, chanid): return paramiko.OPEN_SUCCEEDED
            def check_channel_exec_request(self, channel, command):
                def reply():
                    while True:
                        block = channel.recv(65536)
                        if not block: break
                        received.extend(block)
                    channel.sendall(b"received"); channel.send_exit_status(0); channel.close()
                threading.Thread(target=reply, daemon=True).start()
                return True
        def serve():
            try:
                sock, _ = self.listener.accept()
                self.transport = paramiko.Transport(sock)
                self.transport.add_server_key(self.key)
                self.transport.start_server(server=Server())
                channels = []
                while self.transport.is_active():
                    channel = self.transport.accept(timeout=0.2)
                    if channel is not None: channels.append(channel)
            except (EOFError, OSError, paramiko.SSHException): pass
        self.thread = threading.Thread(target=serve, daemon=True); self.thread.start()

    def tearDown(self):
        if self.transport: self.transport.close()
        self.listener.close(); self.thread.join(timeout=3)
        self.temp.cleanup()

    def test_single_password_connection_binary_upload_without_sftp(self):
        client = installer.connect(paramiko, self.args, "fixture-password")
        try:
            payload = bytes(range(256)) * 15000  # larger than SSH's default receive window
            result = installer.Remote(client).run("cat > temporary-file", payload)
            self.assertEqual(result, "received")
            self.assertEqual(self.received, payload)
            self.assertEqual(len(self.auth_attempts), 1)
            self.assertTrue(Path(self.args.known_hosts).is_file())
        finally: client.close()

    def test_fingerprint_mismatch_rejected_before_password_auth(self):
        self.args.host_key_sha256 = "SHA256:wrong"
        with self.assertRaises(installer.InstallError): installer.connect(paramiko, self.args, "fixture-password")
        self.assertEqual(self.auth_attempts, [])

    def test_unknown_noninteractive_host_rejected_before_password_auth(self):
        self.args.host_key_sha256 = None
        with patch("sys.stdin", io.StringIO()), self.assertRaises(installer.InstallError):
            installer.connect(paramiko, self.args, "fixture-password")
        self.assertEqual(self.auth_attempts, [])

    def test_known_host_with_wrong_pin_rejected_before_auth(self):
        hosts = paramiko.HostKeys()
        hosts.add(f"[127.0.0.1]:{self.port}", self.key.get_name(), self.key)
        hosts.save(self.args.known_hosts)
        self.args.host_key_sha256 = "SHA256:wrong"
        with self.assertRaises(installer.InstallError): installer.connect(paramiko, self.args, "fixture-password")
        self.assertEqual(self.auth_attempts, [])


class LauncherTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt", "PowerShell 5.1/7 test on Windows")
    def test_powershell_secure_password_and_argument_boundaries(self):
        for shell in (shutil.which("powershell.exe"), shutil.which("pwsh.exe")):
            if not shell: continue
            with self.subTest(shell=shell), tempfile.TemporaryDirectory() as tmp:
                # Environment keys copied from Windows are uppercase.
                env = {k: v for k, v in os.environ.items() if k.upper() != "PSMODULEPATH"}
                env["CRC_INSTALLER_ARGV_OUT"] = str(Path(tmp)/"argv.json")
                env["CRC_INSTALLER_STDIN_OUT"] = str(Path(tmp)/"stdin.txt")
                command = r'''function global:python {
                    $args | ConvertTo-Json | Set-Content -Encoding utf8 -LiteralPath $env:CRC_INSTALLER_ARGV_OUT
                    $input | Set-Content -Encoding utf8 -LiteralPath $env:CRC_INSTALLER_STDIN_OUT
                    $global:LASTEXITCODE = 0
                }
                $fixtureSecret = ConvertTo-SecureString 'fixture-密码' -AsPlainText -Force
                & './scripts/Install-RouterAsn.ps1' -Router '127.0.0.1' -UserName 'fixture-user' -AsnFile 'C:\path with spaces\ASN.mmdb' -Password $fixtureSecret
                '''
                result = subprocess.run([shell,"-NoProfile","-NonInteractive","-Command",command],cwd=ROOT,env=env,capture_output=True,timeout=30)
                self.assertEqual(result.returncode,0,result.stderr.decode(errors="replace"))
                self.assertEqual(result.stderr,b"",result.stderr.decode(errors="replace"))
                arguments = json.loads(Path(env["CRC_INSTALLER_ARGV_OUT"]).read_text(encoding="utf-8-sig"))
                self.assertIn("--password-stdin",arguments)
                self.assertIn(r"C:\path with spaces\ASN.mmdb",arguments)
                self.assertNotIn("fixture-密码",str(arguments))
                self.assertEqual(Path(env["CRC_INSTALLER_STDIN_OUT"]).read_text(encoding="utf-8-sig").strip(),"fixture-密码")

    @unittest.skipUnless(SHELL and Path(SHELL).exists(), "POSIX shell required")
    def test_posix_launcher_help(self):
        with tempfile.TemporaryDirectory() as tmp:
            python = Path(tmp)/"python3"
            python.write_text("#!/bin/sh\nexec " + installer.q(unix(sys.executable)) + ' "$@"\n',encoding="utf-8",newline="\n")
            python.chmod(0o755)
            command = f"PATH={installer.q(unix(tmp))}:$PATH sh {installer.q(unix(ROOT/'scripts/install-router-asn.sh'))} --help"
            result = subprocess.run([SHELL,"-c",command],capture_output=True,timeout=30)
            self.assertEqual(result.returncode,0,result.stderr.decode(errors="replace"))
            self.assertIn(b"--host-key-sha256",result.stdout)


if __name__ == "__main__":
    unittest.main()
