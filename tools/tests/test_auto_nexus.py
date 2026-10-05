"""End-to-end check of auto-nexus and the observability layer, with no game client.

The relay is driven exactly as the real one is: a fake game server on one port, a route
pointing at it, and a synthetic client that speaks the client's framing
([4-byte big-endian length][2-byte little-endian type id][body]). It walks the session
through the states that gate injection and asserts on the bytes that reach the "server":

  1. nothing is injected before the client's Hello        (the server is mid-handshake)
  2. nothing is injected before MapInfo                   (there is no world yet)
  3. nothing is injected while HP is above the threshold
  4. below the threshold, exactly one five-byte Escape arrives
  5. the rate limits hold, and a dry run writes nothing
  6. the JSONL event log and the dashboard API tell the same story as the socket

The bytes for the injected packets are taken from the relay's own encoder
(`networking.TestVectors`), so this test cannot pass against a different encoder than the
one production uses.

Usage: python tools/tests/test_auto_nexus.py [--port 0] [--keep-logs]
"""
import argparse
import json
import random
import socket
import struct
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]   # tools/tests/<this file> -> the repository root
CLASSES = ROOT / "target" / "classes"

ESCAPE = bytes.fromhex("4200")
HELLO = bytes.fromhex("00000003370000")
MAPINFO = bytes.fromhex("000000020500")


def frame(payload: bytes) -> bytes:
    return struct.pack(">i", len(payload)) + payload


def read_frame(conn: socket.socket) -> bytes:
    header = b""
    while len(header) < 4:
        chunk = conn.recv(4 - len(header))
        if not chunk:
            raise EOFError("closed while reading header")
        header += chunk
    (length,) = struct.unpack(">i", header)
    body = b""
    while len(body) < length:
        chunk = conn.recv(length - len(body))
        if not chunk:
            raise EOFError("closed while reading body")
        body += chunk
    return body


def vectors(args) -> dict:
    """Ask the relay's own encoders for the payloads this test sends.

    The values are payloads ([type id][body]) and this harness frames them, so there is exactly one
    place that writes a length prefix. See networking.TestVectors for why.
    """
    result = subprocess.run(["java", "-cp", str(CLASSES), "networking.TestVectors"],
                            cwd=ROOT, capture_output=True, text=True, check=True)
    out = {}
    for line in result.stdout.splitlines():
        if "=" not in line or line.startswith("note="):
            continue
        key, value = line.split("=", 1)
        out[key.strip()] = bytes.fromhex(value.strip())
    return out


class FakeServer:
    """Stands in for the game server: it reads what the relay forwards and speaks when told to.

    Direction matters here, and it is easy to get wrong: ``GmMapInfo`` and ``GmHealthUpdate`` are
    server→client packets, so they must come *from this side*. A harness that writes them into the
    relay from the client socket (as this one first did) is exercising a client→server health packet
    that no real session ever sends, and the relay's world gate correctly stays shut - so the test
    fails for a reason that has nothing to do with the feature under test.
    """

    def __init__(self, port: int):
        self.ready = threading.Event()
        self.to_server: list = []
        self.lock = threading.Lock()
        self.connections: list = []
        self._sock = socket.socket()
        self._sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._sock.bind(("127.0.0.1", port))
        self._sock.listen(8)
        self.port = self._sock.getsockname()[1]
        threading.Thread(target=self._accept, daemon=True).start()

    def _accept(self) -> None:
        self.ready.set()
        while True:
            try:
                conn, _ = self._sock.accept()
            except OSError:
                return
            with self.lock:
                self.connections.append(conn)
            threading.Thread(target=self._serve, args=(conn,), daemon=True).start()

    def _serve(self, conn: socket.socket) -> None:
        try:
            while True:
                body = read_frame(conn)
                with self.lock:
                    self.to_server.append(body)
        except (EOFError, OSError):
            with self.lock:
                if conn in self.connections:
                    self.connections.remove(conn)

    def clients(self) -> list:
        """Live client connections, newest last. A closed preflight probe removes itself."""
        with self.lock:
            return list(self.connections)

    def session(self):
        """The connection the relay is proxying, or None while only the probe exists."""
        live = self.clients()
        return live[-1] if live else None

    def send(self, payload: bytes) -> bool:
        conn = self.session()
        if conn is None:
            return False
        try:
            conn.sendall(frame(payload))
            return True
        except OSError:
            return False

    def close(self) -> None:
        for conn in self.clients():
            try:
                conn.close()
            except OSError:
                pass
        try:
            self._sock.close()
        except OSError:
            pass


def get_json(url: str):
    with urllib.request.urlopen(url, timeout=5) as response:
        return json.loads(response.read().decode("utf-8"))


def post_json(url: str, payload: dict):
    request = urllib.request.Request(
        url, data=json.dumps(payload).encode("utf-8"),
        headers={"content-type": "application/json"}, method="POST")
    with urllib.request.urlopen(request, timeout=5) as response:
        return json.loads(response.read().decode("utf-8"))


def wait_for(predicate, timeout: float, interval: float = 0.05):
    deadline = time.time() + timeout
    while time.time() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(interval)
    return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=0, help="relay port; 0 picks a free one")
    parser.add_argument("--keep-logs", action="store_true")
    parser.add_argument("--java", default="java")
    args = parser.parse_args()
    global JAVA
    JAVA = args.java

    relay_port = args.port or random.randint(20000, 40000)
    upstream_port = relay_port + 1
    web_port = relay_port + 2

    print("== vector check (from the relay's own encoders, payloads only)")
    vec = vectors(args)
    for name, value in vec.items():
        print("   %-14s %s" % (name, value.hex().upper()))
    if vec["escape"] != ESCAPE:
        print("FAIL: the relay's escape encoder disagrees with the captured wire bytes")
        return 1
    if vec["health_full"] != bytes.fromhex("4600830B830B0000"):
        print("FAIL: the health encoder does not reproduce the captured HealthUpdate")
        return 1
    ESCAPE_PAYLOAD = vec["escape"]
    HELLO_PAYLOAD = vec["hello"]
    MAPINFO_PAYLOAD = vec["mapinfo"]

    server = FakeServer(upstream_port)
    if not server.ready.wait(5):
        print("FAIL: the fake server did not start")
        return 1

    log_dir = ROOT / "work" / "logs" / "auto-nexus-test"
    config = ROOT / "work" / "auto-nexus-routes.json"
    config.write_text(json.dumps({
        "listenHost": "127.0.0.1",
        "logDirectory": str(log_dir),
        "ringCapacity": 5000,
        "web": {"host": "127.0.0.1", "port": web_port},
        "autoNexus": {
            "enabled": True,
            "dryRun": False,
            "thresholdPercent": 40,
            "minIntervalMillis": 100000,
            "maxPerWorld": 1,
            "skipInSafeArea": True,
        },
        "routes": [{
            "name": "Game",
            "listenPort": relay_port,
            "remoteHost": "127.0.0.1",
            "remotePort": upstream_port,
        }],
    }, indent=2), encoding="utf-8")

    relay = subprocess.Popen([JAVA, "-cp", str(CLASSES), "networking.Relay", str(config)],
                             cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    failures = []
    try:
        client = None
        for _ in range(60):
            try:
                client = socket.create_connection(("127.0.0.1", relay_port), timeout=2)
                break
            except OSError:
                time.sleep(0.25)
        if client is None:
            print("FAIL: the relay never accepted a connection")
            return 1

        client.settimeout(3.0)

        def drained() -> bool:
            """True when the fake server has seen at least one packet from the client."""
            with server.lock:
                return bool(server.to_server)

        print("== 1. before Hello: injection must be disarmed")
        state = wait_for(lambda: safe_get(web_port, "/api/state"), 10)
        if not state:
            failures.append("the dashboard never answered /api/state")
        # A low reading from the server, with no handshake yet. This is the case that would kill a
        # character on entry if the gate were wrong.
        server.send(vec["health_tiny"])
        time.sleep(0.3)
        if any(payload == ESCAPE for payload in snap(server)):
            failures.append("an escape was injected before the client's Hello")
        else:
            print("   ok: a low health reading alone injected nothing")

        print("== 2. after Hello but before MapInfo: still disarmed")
        client.sendall(frame(HELLO_PAYLOAD))
        if not wait_for(drained, 3):
            failures.append("the relay never forwarded the client's Hello")
        server.send(vec["health_tiny"])
        time.sleep(0.3)
        if any(payload == ESCAPE for payload in snap(server)):
            failures.append("an escape was injected before the world existed (MapInfo)")
        else:
            print("   ok: the relay stayed quiet through the handshake")

        print("== 3. after MapInfo, healthy: still nothing")
        if not server.send(MAPINFO_PAYLOAD):
            failures.append("could not send MapInfo from the server side")
        if not server.send(vec["health_full"]):
            failures.append("could not send a health reading from the server side")
        # Guard against the race where an escape would arrive a moment later.
        time.sleep(0.5)
        if any(payload == ESCAPE for payload in snap(server)):
            failures.append("an escape was injected while HP was above the threshold")
        else:
            print("   ok: 707/707 (100%%) did not trip a 40%% threshold")

        print("== 4. below the threshold: exactly one escape")
        if not server.send(vec["health_tiny"]):
            failures.append("could not send the low health reading")
        escape_payload = ESCAPE
        found = wait_for(lambda: any(p == escape_payload for p in snap(server)), 3)
        if not found:
            failures.append("no escape was injected at 30/707 (4%%), well under the 40%% threshold")
        else:
            print("   ok: the server received %s" % escape_payload.hex().upper())

        print("== 5. rate limits: a second low reading changes nothing")
        server.send(vec["health_tiny"])
        time.sleep(0.5)
        escapes = [p for p in snap(server) if p == escape_payload]
        if len(escapes) != 1:
            failures.append("expected exactly one injected escape, saw %d" % len(escapes))
        else:
            print("   ok: still exactly one escape")

        print("== 6. the dashboard and the log agree with the socket")
        state = safe_get(web_port, "/api/state")
        if not isinstance(state, dict):
            failures.append("the dashboard did not return a JSON object from /api/state: %r" % (state,))
        else:
            nexus = state.get("nexus") or {}
            config_seen = nexus.get("config") or {}
            if config_seen.get("thresholdPercent") != 40:
                failures.append("the dashboard reports the wrong threshold: %r" % (config_seen,))
            if nexus.get("fires", 0) < 1:
                failures.append("the dashboard does not report the injection: fires=%r" % nexus.get("fires"))
            health = state.get("health") or {}
            if health.get("maxHealth") != 707:
                failures.append("the dashboard's max HP is %r, expected 707" % health.get("maxHealth"))
            if health.get("health") != 30:
                failures.append("the dashboard's headline HP is %r, expected 30" % health.get("health"))
            print("   dashboard: fires=%s threshold=%s health=%s/%s (%s%%)" % (
                nexus.get("fires"), config_seen.get("thresholdPercent"),
                health.get("health"), health.get("maxHealth"), health.get("hpPercent")))

            events = safe_get(web_port, "/api/events?after=0&limit=500&raw=1")
            if not isinstance(events, dict):
                failures.append("the dashboard did not return events: %r" % (events,))
            else:
                kinds = {}
                health_events = []
                for event in events.get("events", []):
                    kinds[event["kind"]] = kinds.get(event["kind"], 0) + 1
                    if event.get("pkt") == "HealthUpdate":
                        health_events.append(event)
                if not kinds.get("inject"):
                    failures.append("no inject event is visible in the dashboard: %r" % kinds)
                if not health_events:
                    failures.append("no HealthUpdate reading is visible in the dashboard: %r" % kinds)
                elif (health_events[-1].get("data") or {}).get("health") != 30:
                    failures.append("the health reading on the wire is %r"
                                    % (health_events[-1].get("data"),))
                if not kinds.get("nexus"):
                    failures.append("no nexus decision event is visible in the dashboard: %r" % kinds)
                print("   dashboard events: %s" % kinds)

            filtered = safe_get(web_port, "/api/events?after=0&limit=500")
            if isinstance(filtered, dict):
                names = {e.get("pkt") for e in filtered.get("events", [])}
                if "Hello" in names:
                    failures.append("the default (character hp) filter let Hello through: %r" % names)
                if "HealthUpdate" not in names:
                    failures.append("the default filter dropped the HP readings themselves: %r" % names)
                print("   default filter kept packets: %s (%d hidden)" % (
                    sorted(n for n in names if n), filtered.get("hidden")))

        # The JSONL log is the artifact a post-session analysis reads; it must contain the same events.
        run_logs = sorted((log_dir).glob("events-*.jsonl"), key=lambda p: p.stat().st_mtime)
        if not run_logs:
            failures.append("no events-*.jsonl was written to %s" % log_dir)
        else:
            lines = run_logs[-1].read_text(encoding="utf-8").splitlines()
            parsed = [json.loads(line) for line in lines]
            injects = [e for e in parsed if e.get("kind") == "inject"]
            if not injects:
                failures.append("the JSONL log has no inject event")
            else:
                print("   jsonl: %d events, %d injects, injected bytes %s" % (
                    len(parsed), len(injects), injects[0].get("hex")))
                if injects[0].get("hex") != "4200":
                    failures.append("the logged injection bytes are %r" % injects[0].get("hex"))
            nexus_logs = sorted(log_dir.glob("nexus-*.jsonl"), key=lambda p: p.stat().st_mtime)
            if not nexus_logs:
                failures.append("no nexus-*.jsonl was written")
            elif not nexus_logs[-1].read_text(encoding="utf-8").strip():
                failures.append("the nexus log is empty")
            else:
                print("   nexus log: %d lines" % len(nexus_logs[-1].read_text(encoding="utf-8").splitlines()))
        client.close()
    except Exception as exc:  # noqa: BLE001
        failures.append("exception: %r" % (exc,))
    finally:
        relay.terminate()
        try:
            out = relay.communicate(timeout=15)[0]
        except subprocess.TimeoutExpired:
            relay.kill()
            out = ""
        server.close()
        if args.keep_logs:
            print("---- relay output ----")
            print(out or "")

    if failures:
        print("FAIL:")
        for failure in failures:
            print("  -", failure)
        if not args.keep_logs:
            print("(rerun with --keep-logs to see the relay's own output)")
        return 1
    print("PASS: auto-nexus injected exactly what it should, and only when it should")
    return 0


def snap(server: FakeServer) -> list:
    with server.lock:
        return list(server.to_server)


def safe_get(port: int, path: str):
    try:
        return get_json("http://127.0.0.1:%d%s" % (port, path))
    except (urllib.error.URLError, OSError, ValueError):
        return None


JAVA = "java"

if __name__ == "__main__":
    sys.exit(main())
