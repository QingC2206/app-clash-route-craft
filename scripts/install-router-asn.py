#!/usr/bin/env python3
"""Install only the ASN database over SSH; never alter routing, DNS or services."""
from __future__ import annotations

import argparse
import base64
import getpass
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shlex
import subprocess
import sys
import time
import urllib.request
import uuid

RELEASE = "https://api.github.com/repos/MetaCubeX/meta-rules-dat/releases/latest"
ASSET = "GeoLite2-ASN.mmdb"
LIMIT = 64 * 1024 * 1024
LOGIN_FILE = Path(__file__).resolve().with_name("router-login.json")
KNOWN_HOSTS_FILE = Path(__file__).resolve().with_name("router-known_hosts")
q = shlex.quote


class InstallError(Exception):
    pass


class HostKeyMismatch(InstallError):
    def __init__(self, record, pinned=False):
        super().__init__(f"SSH fingerprint mismatch; no credentials sent. Record: {record}")
        self.record = str(record)
        self.pinned = pinned


def can_encode(stream, text):
    try:
        text.encode(getattr(stream, "encoding", None) or "utf-8")
        return True
    except (UnicodeError, LookupError):
        return False


def terminal_color(stream):
    if not stream.isatty() or os.environ.get("TERM") == "dumb" or "NO_COLOR" in os.environ:
        return False
    if os.name != "nt":
        return True
    try:
        import ctypes
        import msvcrt
        from ctypes import wintypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.GetConsoleMode.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        kernel.SetConsoleMode.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        mode = wintypes.DWORD()
        handle = msvcrt.get_osfhandle(stream.fileno())
        return bool(kernel.GetConsoleMode(handle, ctypes.byref(mode)) and kernel.SetConsoleMode(handle, mode.value | 4))
    except (OSError, ValueError, AttributeError):
        return False


def status_line(ok, message, english, plain=False, stream=None):
    stream = stream if stream is not None else sys.stdout
    label = "[OK]" if ok else "[FAIL]"
    icon = "✅" if ok else "❌"
    decorated = not plain and stream.isatty() and os.environ.get("TERM") != "dumb"
    prefix = (icon + " " if decorated and can_encode(stream, icon) else "") + label
    if decorated and terminal_color(stream):
        prefix = ("\033[32m" if ok else "\033[31m") + prefix + "\033[0m"
    print(prefix + " " + (message if can_encode(stream, message) else english), file=stream, flush=True)


def show_result(result, plain=False):
    existing = result.get("status") == "already_installed"
    status_line(True, "验证成功：路由器已安装相同的 ASN 数据库，无需重复安装。" if existing else "安装成功：ASN 数据库已安装并通过内核验证。",
                "SUCCESS: ASN is already installed and verified; no reinstall needed." if existing else "SUCCESS: ASN installed and core validation passed.", plain)
    status_line(True, "SHA-256 校验通过，路由器 Mihomo 内核验证通过。", "SHA-256 and router Mihomo core validation passed.", plain)
    for text, english in (("文件位置：" + result.get("path", ""), "Database: " + result.get("path", "")),
                          ("MC2 未重启，现有规则和 DNS 未修改。", "MC2 was not restarted. Existing rules and DNS were not modified.")):
        print(text if can_encode(sys.stdout, text) else english)
    if result.get("backup"):
        print("Backup: " + result["backup"])


def show_host_key_mismatch(error, plain=False):
    status_line(False, "SSH 指纹不匹配，已停止连接（尚未发送密码）。",
                "SSH fingerprint mismatch. Connection stopped before sending the password.", plain, sys.stderr)
    lines = [
        ("请先确认：当前网络、路由器地址和项目是否对应。", "Check that the network, router address and project match."),
        ("若已换机或刷机：核实新设备身份后，更新 -HostKeySha256 / --host-key-sha256 参数，再重试。",
         "If the router was replaced or reflashed, verify its identity before updating -HostKeySha256 / --host-key-sha256 and retrying.") if error.pinned else
        ("若已换机或刷机：核实新设备身份后，备份并移除下列文件中该地址的旧记录，再重试确认指纹。",
         "If the router was replaced or reflashed, verify its identity, back up the file below, remove only this address's old entry, then retry and confirm the fingerprint."),
        ("主机记录：" + error.record, "Host records: " + error.record),
    ]
    for chinese, english in lines:
        text = chinese if can_encode(sys.stderr, chinese) else english
        encoding = getattr(sys.stderr, "encoding", None) or "utf-8"
        print(text.encode(encoding, errors="replace").decode(encoding), file=sys.stderr)


def dependency():
    try:
        import paramiko
        if (3, 5) <= tuple(int(n) for n in paramiko.__version__.split('.')[:2]) < (5, 0):
            return paramiko
    except ImportError:
        pass
    base = Path(os.environ.get("LOCALAPPDATA", Path.home() / ".local/share")) / "ClashRouteCraft/router-tools"
    executable = base / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
    if Path(sys.prefix).resolve() == base.resolve():
        raise InstallError("SSH dependency unavailable. Remove the router-tools virtual environment and retry.")
    probe = "import paramiko; assert (3,5) <= tuple(map(int,paramiko.__version__.split('.')[:2])) < (5,0)"
    if executable.exists() and subprocess.run([str(executable), "-c", probe], stdin=subprocess.DEVNULL,
                                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30).returncode == 0:
        raise SystemExit(subprocess.call([str(executable), "-X", "utf8", str(Path(__file__).resolve()), *sys.argv[1:]]))
    print("Preparing isolated SSH dependency (Paramiko); no router connection yet.", flush=True)
    if not executable.exists():
        subprocess.run([sys.executable, "-m", "venv", str(base)], check=True, stdin=subprocess.DEVNULL)
    subprocess.run([str(executable), "-m", "pip", "install", "--no-input", "--index-url", "https://pypi.org/simple",
                    "paramiko>=3.5,<5"], check=True, stdin=subprocess.DEVNULL, timeout=240)
    raise SystemExit(subprocess.call([str(executable), "-X", "utf8", str(Path(__file__).resolve()), *sys.argv[1:]]))


def fetch(url, maximum):
    accept = "application/vnd.github+json" if url.startswith("https://api.github.com/") else "application/octet-stream"
    req = urllib.request.Request(url, headers={"User-Agent": "ClashRouteCraft-ASN-Installer", "Accept": accept})
    with urllib.request.urlopen(req, timeout=45) as response:
        data = response.read(maximum + 1)
    if len(data) > maximum:
        raise InstallError("Download exceeded size limit.")
    return data


def database(path=None):
    if path:
        file = Path(path).expanduser()
        if file.stat().st_size > LIMIT:
            raise InstallError("Database exceeded size limit.")
        data = file.read_bytes()
    else:
        print("Downloading ASN database and release SHA-256 from MetaCubeX...", flush=True)
        release = json.loads(fetch(RELEASE, 8 * 1024 * 1024))
        asset = next((a for a in release.get("assets", []) if a.get("name") == ASSET), None)
        if not asset or not re.fullmatch(r"sha256:[0-9a-fA-F]{64}", asset.get("digest", "")):
            raise InstallError("Release does not provide the ASN asset and its SHA-256; nothing installed.")
        url = asset["browser_download_url"]
        if not url.startswith("https://github.com/MetaCubeX/meta-rules-dat/releases/download/"):
            raise InstallError("Unexpected release asset URL.")
        data = fetch(url, LIMIT)
        if hashlib.sha256(data).hexdigest() != asset["digest"][7:].lower():
            raise InstallError("Release SHA-256 mismatch; nothing installed.")
    metadata = data[-131072:].split(b"\xab\xcd\xefMaxMind.com")
    if len(data) < 1024 or len(metadata) < 2 or b"ASN" not in metadata[-1]:
        raise InstallError("File is not an ASN MMDB database.")
    return data


def remote_path(value):
    if not value.startswith("/") or str(PurePosixPath(value)) in ("/", "//") or ".." in value.split("/") or any(ord(c) < 32 or ord(c) == 127 for c in value):
        raise InstallError("Remote paths must be absolute, non-root paths without '..' or control characters.")
    return str(PurePosixPath(value))


def read_login(path, required=False):
    file = Path(path).expanduser()
    if not file.exists() and not required:
        return {}
    try:
        if file.stat().st_size > 65536:
            raise InstallError("Login file exceeds 64 KiB.")
        value = json.loads(file.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError, UnicodeError):
        raise InstallError("Cannot read login file. Use a UTF-8 JSON object; see router-login.example.json.") from None
    if not isinstance(value, dict) or set(value) - {"host", "port", "username", "password"}:
        raise InstallError("Login file accepts only host, port, username and password fields.")
    if any(not isinstance(value.get(k, ""), str) for k in ("host", "username", "password")):
        raise InstallError("Login host, username and password must be JSON strings.")
    if "port" in value and (type(value["port"]) is not int or not 1 <= value["port"] <= 65535):
        raise InstallError("Login port must be an integer between 1 and 65535.")
    return value


def login_options(args):
    saved = read_login(args.login_file or LOGIN_FILE, required=bool(args.login_file))
    # Explicit CLI settings take precedence; never silently reuse another target's password.
    same_target = all(not explicit or not stored or explicit == stored for explicit, stored in
                      ((args.host, saved.get("host")), (args.user, saved.get("username")), (args.port, saved.get("port"))))
    args.host = args.host or saved.get("host", "").strip()
    args.user = args.user or saved.get("username", "").strip()
    args.port = args.port if args.port is not None else saved.get("port", 22)
    if saved:
        print("Loaded local login settings.", flush=True)
    if not same_target and saved.get("password"):
        print("Command-line target differs from the login file; enter its password separately.", flush=True)
    return saved.get("password", "") if same_target else ""


class Remote:
    def __init__(self, client):
        self.client = client

    def run(self, command, data=None, timeout=90):
        channel = self.client.get_transport().open_session(timeout=20)
        try:
            channel.settimeout(30)
            channel.set_combine_stderr(True)
            channel.exec_command(command)
            if data is not None:
                for pos in range(0, len(data), 65536):
                    channel.sendall(data[pos:pos + 65536])
            channel.shutdown_write()
            deadline = time.monotonic() + timeout
            output = bytearray()
            while True:
                while channel.recv_ready():
                    output.extend(channel.recv(32768))
                    if len(output) > 256 * 1024:
                        raise InstallError("Remote command output exceeded limit.")
                if channel.exit_status_ready() and not channel.recv_ready():
                    break
                if time.monotonic() > deadline:
                    raise InstallError("Remote command timed out.")
                time.sleep(0.02)
            text = output.decode("utf-8", errors="replace").strip()
            if channel.recv_exit_status() != 0:
                raise InstallError("Router command failed: " + text)
            return text
        finally:
            channel.close()

    def hash(self, path):
        # BusyBox may expose a command whose applet is not compiled in. Check actual
        # execution and digest output, not just whether `command -v` finds a name.
        for backend in ("sha256sum", "openssl dgst -sha256", "busybox sha256sum"):
            command = f"if [ ! -e {q(path)} ]; then echo MISSING; else {backend} {q(path)}; fi"
            try:
                result = self.run(command)
            except InstallError:
                continue
            if result == "MISSING":
                return None
            match = re.search(r"^\\?([0-9a-fA-F]{64})\s|=\s*([0-9a-fA-F]{64})\s*$", result)
            if match:
                return (match[1] or match[2]).lower()
        raise InstallError("Cannot calculate router SHA-256: sha256sum, OpenSSL and BusyBox all failed or returned an invalid digest. Check file readability and OpenSSL SHA-256 support; installation cannot continue without verification.")

    def upload(self, path, data):
        self.run(f"umask 077; cat > {q(path)}", data=data)


def validate(remote, core, source, temporary):
    config = """mode: rule
log-level: info
geo-auto-update: false
geox-url:
  asn: 'http://127.0.0.1:0/disabled'
rules:
  - IP-ASN,20473,DIRECT
  - MATCH,DIRECT
"""
    remote.run(f"ln -sf {q(source)} {q(temporary + '/ASN.mmdb')}")
    remote.upload(temporary + "/config.yaml", config.encode())
    # A watchdog bounds a hung core even if the SSH client disconnects.
    command = f"""{q(core)} -t -d {q(temporary)} -f {q(temporary + '/config.yaml')} > {q(temporary + '/validation.log')} 2>&1 &
validator=$!
(sleep 45; if tr '\\000' ' ' < /proc/"$validator"/cmdline 2>/dev/null | grep -F {q(temporary + '/config.yaml')} >/dev/null; then kill -9 "$validator" 2>/dev/null; fi) </dev/null >/dev/null 2>&1 &
watchdog=$!
wait "$validator"; result=$?
kill "$watchdog" 2>/dev/null
cat {q(temporary + '/validation.log')}
exit $result
"""
    output = remote.run(command, timeout=60)
    if "test is successful" not in output:
        raise InstallError("Core did not confirm a successful ASN configuration test: " + output)
    return output


def install(remote, data, data_dir="/koolshare/merlinclash", core="/koolshare/bin/clash"):
    data_dir, core = remote_path(data_dir), remote_path(core)
    identity = uuid.uuid4().hex
    target = data_dir + "/ASN.mmdb"
    stage = data_dir + "/.ASN.mmdb." + identity + ".new"
    backup = data_dir + "/.ASN.mmdb.backup-" + time.strftime("%Y%m%d-%H%M%S") + "-" + identity[:8]
    temporary = "/tmp/crc-asn-" + identity
    lock = data_dir + "/.crc-asn-install.lock"
    expected = hashlib.sha256(data).hexdigest()
    locked = False
    original = None
    backup_ready = False
    replacement_attempted = False
    try:
        remote.run(f"test -d {q(data_dir)} && test -w {q(data_dir)} && test -x {q(core)}")
        remote.run(f"mkdir {q(lock)}")
        locked = True
        remote.run(f"umask 077; mkdir {q(temporary)}")
        # Do not follow an existing final-file symlink or replace a directory.
        remote.run(f"test ! -L {q(target)} && {{ test ! -e {q(target)} || test -f {q(target)}; }}")
        original = remote.hash(target)
        if original == expected:
            validate(remote, core, target, temporary)
            return {"status": "already_installed", "sha256": expected, "path": target, "core_validation": "passed", "service_restarted": False}
        free_kib = int(remote.run(f"df -Pk {q(data_dir)} | awk 'END {{print $4}}'"))
        old_size = int(remote.run(f"wc -c < {q(target)}")) if original else 0
        required = len(data) + old_size + 1024 * 1024
        if free_kib * 1024 < required:
            raise InstallError(f"Insufficient storage: need {required} bytes including staging/backup; original file unchanged.")
        print("Uploading temporary database and verifying router SHA-256...", flush=True)
        remote.upload(stage, data)
        if remote.hash(stage) != expected:
            raise InstallError("Upload SHA-256 mismatch; original file unchanged.")
        print("Validating ASN with the router's core (network download disabled)...", flush=True)
        validate(remote, core, stage, temporary)
        if remote.hash(target) != original:
            raise InstallError("Existing ASN changed during installation; refusing to overwrite it.")
        if original:
            remote.run(f"cp -p {q(target)} {q(backup)}")
            if remote.hash(backup) != original:
                raise InstallError("Backup verification failed; original file unchanged.")
            backup_ready = True
        remote.run(f"chmod 644 {q(stage)}")
        replacement_attempted = True
        remote.run(f"mv -f {q(stage)} {q(target)}")
        if remote.hash(target) != expected:
            raise InstallError("Installed file SHA-256 mismatch.")
        validate(remote, core, target, temporary)
        return {"status": "installed", "sha256": expected, "path": target, "backup": backup if backup_ready else None,
                "core_validation": "passed", "service_restarted": False}
    except BaseException as failure:
        if replacement_attempted:
            try:
                if remote.hash(target) not in (expected, original, None):
                    raise InstallError("Another writer changed ASN; automatic rollback refused.")
                if backup_ready:
                    remote.run(f"mv -f {q(backup)} {q(target)}")
                    if remote.hash(target) != original:
                        raise InstallError("Restored file SHA-256 mismatch.")
                elif original is None:
                    remote.run(f"rm -f {q(target)}")
                print("Installation failed; previous ASN state restored.", file=sys.stderr)
            except Exception:
                raise InstallError(f"Connection/rollback failed. Check {target}; preserved backup: {backup if backup_ready else 'none'}. No service restart was requested.") from failure
        raise
    finally:
        if locked:
            try:
                # Only our unique paths; no recursive deletion of user directories.
                remote.run(f"rm -f {q(stage)} {q(temporary + '/ASN.mmdb')} {q(temporary + '/config.yaml')} {q(temporary + '/validation.log')} {q(temporary + '/cache.db')}; rmdir {q(temporary)} && rmdir {q(lock)}")
            except Exception:
                print(f"Cleanup incomplete; inspect temporary paths {temporary}, {stage}, {lock} before retrying.", file=sys.stderr)


def connect(paramiko, args, password):
    client = paramiko.SSHClient()
    known = Path(args.known_hosts).expanduser()
    # A project identifies its own router, even when other projects use the same IP.
    # Never mix this trust store with system or legacy shared host records.
    if known.is_file():
        client.load_host_keys(str(known))

    class ConfirmKey(paramiko.MissingHostKeyPolicy):
        def missing_host_key(self, client, hostname, key):
            fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip("=")
            if args.host_key_sha256:
                if fingerprint != args.host_key_sha256:
                    raise HostKeyMismatch(known, pinned=True)
            elif sys.stdin.isatty():
                print(f"First connection to {hostname}\nSSH host fingerprint: {fingerprint}")
                if input("Confirm this router fingerprint (type yes): ").strip() != "yes":
                    raise InstallError("SSH host key not accepted.")
            else:
                raise InstallError("Unknown SSH host. First connect interactively or provide --host-key-sha256 from a trusted source.")
            known.parent.mkdir(parents=True, exist_ok=True)
            client.get_host_keys().add(hostname, key.get_name(), key)
            client.save_host_keys(str(known))
            if os.name != "nt":
                known.chmod(0o600)

    client.set_missing_host_key_policy(ConfirmKey())

    class PinnedTransport(paramiko.Transport):
        def start_client(self, *positional, **keyword):
            super().start_client(*positional, **keyword)
            if args.host_key_sha256:
                key = self.get_remote_server_key()
                actual = "SHA256:" + base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip("=")
                if actual != args.host_key_sha256:
                    raise HostKeyMismatch(known, pinned=True)

    try:
        client.connect(args.host, port=args.port, username=args.user, password=password, timeout=20,
                       banner_timeout=20, auth_timeout=20, allow_agent=False, look_for_keys=False,
                       transport_factory=PinnedTransport)
        return client
    except paramiko.BadHostKeyException:
        client.close()
        raise HostKeyMismatch(known) from None
    except paramiko.AuthenticationException:
        client.close()
        raise InstallError("SSH authentication failed. Check the router username/password (including router-login.json) and whether SSH password login is enabled. ASN download has not started.") from None
    except BaseException:
        client.close()
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", help="Router address; prompted if omitted")
    parser.add_argument("--user", help="Router administrator; prompted if omitted")
    parser.add_argument("--port", type=int)
    parser.add_argument("--login-file", help="Login JSON; defaults to router-login.json beside this script")
    parser.add_argument("--asn-file", help="Use a local ASN.mmdb instead of downloading")
    parser.add_argument("--password-stdin", action="store_true", help="Read one password line from stdin, never command arguments")
    parser.add_argument("--host-key-sha256", help="Trusted SSH host fingerprint for unattended first use")
    parser.add_argument("--known-hosts", default=str(KNOWN_HOSTS_FILE), help="Project SSH host records; defaults to router-known_hosts beside this script (no system/shared records)")
    parser.add_argument("--data-dir", default="/koolshare/merlinclash")
    parser.add_argument("--core", default="/koolshare/bin/clash")
    parser.add_argument("--plain", action="store_true", help="Disable emoji and terminal colors; retain [OK]/[FAIL] labels")
    parser.add_argument("--json", action="store_true", help="Print the final result as JSON instead of the human-readable summary")
    args = parser.parse_args()
    try:
        if sys.version_info < (3, 10):
            raise InstallError("Python 3.10+ is required on this computer.")
        paramiko = dependency()
        password = login_options(args)
        if not 1 <= args.port <= 65535:
            raise InstallError("Invalid SSH port.")
        remote_path(args.data_dir); remote_path(args.core)
        if args.password_stdin and (not args.host or not args.user):
            raise InstallError("--password-stdin requires --host and --user.")
        args.host = args.host or input("Router address: ").strip()
        args.user = args.user or input("Router username: ").strip()
        if not args.host or not args.user:
            raise InstallError("Router address and username are required.")
        password = sys.stdin.readline().rstrip("\r\n") if args.password_stdin else password or getpass.getpass("Router password: ")
        if not password:
            raise InstallError("Empty password; nothing installed.")
        client = connect(paramiko, args, password)
        password = None
        try:
            print("SSH authentication successful. Preparing ASN database...", flush=True)
            client.get_transport().set_keepalive(20)
            data = database(args.asn_file)
            print(f"Database ready: {len(data)} bytes, SHA256 {hashlib.sha256(data).hexdigest()}", flush=True)
            result = install(Remote(client), data, args.data_dir, args.core)
            if args.json:
                print(json.dumps(result, ensure_ascii=True, indent=2))
            else:
                show_result(result, args.plain)
        finally:
            client.close()
        return 0
    except KeyboardInterrupt:
        status_line(False, "已取消：未完成本次安装验证。", "CANCELLED: installation verification did not complete.", args.plain, sys.stderr)
        return 130
    except HostKeyMismatch as error:
        show_host_key_mismatch(error, args.plain)
        return 1
    except (InstallError, OSError, ValueError, subprocess.SubprocessError) as error:
        status_line(False, "执行失败：本次未能确认 ASN 安装成功，请查看下方原因。", "FAILED: ASN installation success could not be confirmed. See the reason below.", args.plain, sys.stderr)
        print(str(error).encode(getattr(sys.stderr, "encoding", None) or "utf-8", errors="replace").decode(getattr(sys.stderr, "encoding", None) or "utf-8"), file=sys.stderr)
        return 1
    except Exception as error:
        # Do not dump tracebacks, connection arguments or credentials.
        status_line(False, "执行失败：请检查 SSH 身份、登录信息和连接状态。", "FAILED: check SSH host identity, authentication and connectivity.", args.plain, sys.stderr)
        print(f"SSH operation failed ({type(error).__name__}).", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
