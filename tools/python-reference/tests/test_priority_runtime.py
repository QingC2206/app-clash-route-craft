"""Verify actual Mihomo priority, failover and recovery using local mock proxies."""
from pathlib import Path
import json
import os
import socket
import socketserver
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from urllib.parse import quote

import requests
import yaml

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import build
from test_builder import fixture

EXE = Path(os.environ.get("MIHOMO_PATH", str(Path(__file__).resolve().parents[3] / "resources/validation/mihomo.exe")))


class MockProxy(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        try:
            if not self.server.alive:
                return
            self.request.settimeout(3)
            data = b""
            while b"\r\n\r\n" not in data:
                block = self.request.recv(8192)
                if not block:
                    return
                data += block
            if data.startswith(b"CONNECT "):
                self.request.sendall(b"HTTP/1.1 200 Connection Established\r\n\r\n")
                data = b""
                while b"\r\n\r\n" not in data:
                    block = self.request.recv(8192)
                    if not block:
                        return
                    data += block
            time.sleep(self.server.delay)
            self.request.sendall(b"HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
        except OSError:
            pass


@unittest.skipUnless(EXE.is_file() and os.name == "nt", "requires the locally verified Windows Mihomo")
class PriorityRuntime(unittest.TestCase):
    def test_higher_priority_slower_then_failure_then_recovery(self):
        servers = []
        proc = None
        temp = tempfile.TemporaryDirectory()
        root = Path(temp.name)
        try:
            for delay in [0.20, 0.005]:
                server = MockProxy(("127.0.0.1", 0), Handler)
                server.alive, server.delay = True, delay
                threading.Thread(target=server.serve_forever, daemon=True).start()
                servers.append(server)
            settings = fixture(root, tuple(s.server_address[1] for s in servers))
            test_url = "http://127.0.0.1:9/generate_204"
            settings["selection"]["test_url"] = test_url
            config, _ = build.compile_profile(settings, root, True)
            with socket.socket() as sock:
                sock.bind(("127.0.0.1", 0))
                port = sock.getsockname()[1]
            config.update({"mixed-port": 0, "external-controller": f"127.0.0.1:{port}", "secret": "test-only",
                           "dns": {"enable": False}, "tun": {"enable": False}})
            path = root / "config.yaml"
            path.write_text(yaml.safe_dump(config, allow_unicode=True, sort_keys=False), encoding="utf-8")
            log = (root / "core.log").open("wb")
            proc = subprocess.Popen([str(EXE), "-d", str(root), "-f", str(path)], stdout=log, stderr=subprocess.STDOUT,
                                    creationflags=subprocess.CREATE_NO_WINDOW)
            session = requests.Session()
            session.trust_env = False
            session.headers["Authorization"] = "Bearer test-only"
            api = f"http://127.0.0.1:{port}"
            for _ in range(60):
                try:
                    if session.get(api + "/version", timeout=0.2).ok:
                        break
                except requests.RequestException:
                    pass
                time.sleep(0.1)
            else:
                self.fail("Local test core did not start")

            def probe(name):
                return session.get(api + "/proxies/" + quote(name, safe="") + "/delay",
                                   params={"url": test_url, "timeout": 1000}, timeout=3)

            def now():
                response = session.get(api + "/proxies/" + quote("订阅优先", safe=""), timeout=1)
                response.raise_for_status()
                return response.json()["now"]

            a = probe("[A] 香港同名")
            b = probe("[B] 香港同名")
            self.assertTrue(a.ok and b.ok, (a.status_code, b.status_code))
            self.assertGreater(a.json()["delay"], b.json()["delay"])
            self.assertTrue(probe("优先级·10").ok)
            self.assertTrue(probe("优先级·20").ok)
            self.assertEqual(now(), "优先级·10")
            servers[0].alive = False
            self.assertFalse(probe("[A] 香港同名").ok)
            self.assertFalse(probe("优先级·10").ok)
            self.assertEqual(now(), "优先级·20")
            servers[0].alive = True
            self.assertTrue(probe("[A] 香港同名").ok)
            self.assertTrue(probe("优先级·10").ok)
            self.assertEqual(now(), "优先级·10")
            self.evidence = {"A_slower_but_preferred": True, "A_down_switches_to_B": True,
                             "A_recovers_returns_to_A": True, "network": "loopback only"}
        finally:
            if proc is not None:
                proc.terminate()
                try:
                    proc.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    proc.kill()
                    proc.wait(timeout=5)
                log.close()
            for server in servers:
                server.shutdown()
                server.server_close()
            temp.cleanup()


if __name__ == "__main__":
    unittest.main()
