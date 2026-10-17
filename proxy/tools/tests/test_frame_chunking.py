"""Assert the relay puts ONE frame on the wire as ONE TCP segment.

Why this exists: `pump` used to write the 4-byte length prefix and the payload as two separate
writes. The game server's admission reader treats the first `read()` as a whole frame, so it took
the length (e.g. `00 00 00 83`) as a zero-length packet and then parsed the 131-byte `Hello` as the
next header - a ~9.2e8-byte payload it then waited for forever. The socket stayed open and the
server kept sending `Ping`, so it read as "the proxy connects and then the game hangs on the
loading screen" rather than as a protocol error. The lobby uses the same relay path but its reader
loops, so the queue worked and only the game broke - which is why the symptom looked like a
server-side admission problem.

The client writes `[4-byte BE length][payload]` in a single send, so the relay must too.

Usage: python tools/tests/test_frame_chunking.py [--relay-port 6520] [--upstream-port 6620]
"""
import argparse
import json
import socket
import struct
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]   # tools/tests/<this file> -> the proxy root


def frame(payload: bytes) -> bytes:
    return struct.pack(">i", len(payload)) + payload


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--relay-port", type=int, default=6520)
    ap.add_argument("--upstream-port", type=int, default=6620)
    args = ap.parse_args()

    ready = threading.Event()
    reads: list = []          # (bytes received, total so far) per recv() call
    session: list = []        # the connection that actually carried data

    def serve(conn):
        conn.settimeout(5)
        while True:
            try:
                data = conn.recv(65536)
            except (socket.timeout, OSError):
                break
            if not data:
                break
            reads.append((len(data), data))
            if not session:
                session.append(conn)
            if sum(size for size, _ in reads) >= 4 + 131 + 4 + 604:
                break

    def fake_server():
        with socket.socket() as srv:
            srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            srv.bind(("127.0.0.1", args.upstream_port))
            srv.listen(4)
            ready.set()
            # The relay dials a destination probe at startup; that connection carries no data, so
            # keep accepting instead of serving exactly one session.
            while True:
                conn, _ = srv.accept()
                threading.Thread(target=serve, args=(conn,), daemon=True).start()

    threading.Thread(target=fake_server, daemon=True).start()
    if not ready.wait(5):
        print("FAIL: fake server did not start")
        return 1

    config = ROOT / "work" / "test-routes-chunking.json"
    config.parent.mkdir(exist_ok=True)
    config.write_text(json.dumps({
        "listenHost": "127.0.0.1",
        "routes": [{
            "name": "Test",
            "listenPort": args.relay_port,
            "remoteHost": "127.0.0.1",
            "remotePort": args.upstream_port,
        }],
    }), encoding="utf-8")

    relay = subprocess.Popen(
        ["java", "-cp", "target\\classes", "networking.Relay", str(config)],
        cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
    )

    client = None
    deadline = time.time() + 20
    while time.time() < deadline:
        try:
            client = socket.create_connection(("127.0.0.1", args.relay_port), timeout=2)
            break
        except OSError:
            time.sleep(0.3)
    if client is None:
        relay.kill()
        print("FAIL: relay never accepted a connection")
        print(relay.stdout.read() if relay.stdout else "")
        return 1

    # The two sizes that matter: the client's 131-byte `Hello` and a DailyGold-sized 604-byte one.
    hello = bytes([0x37, 0x00]) + b"H" * 129
    daily = bytes([0x33]) + b"D" * 603
    failures = []
    try:
        with client:
            client.sendall(frame(hello))       # one send, exactly like the game client
            time.sleep(1.0)
            client.sendall(frame(daily))
            time.sleep(1.0)
    except Exception as exc:  # noqa: BLE001
        failures.append(f"exception: {exc}")
    finally:
        relay.terminate()
        try:
            out = relay.communicate(timeout=10)[0]
        except subprocess.TimeoutExpired:
            relay.kill()
            out = ""

    sizes = [size for size, _ in reads]
    if sizes != [4 + 131, 4 + 604]:
        failures.append(f"upstream read sizes were {sizes}, expected [135, 608] "
                        "- the relay split a frame across TCP segments")
    if len(reads) == 2:
        if reads[0][1] != frame(hello):
            failures.append("upstream received the wrong bytes for the Hello frame")
        if reads[1][1] != frame(daily):
            failures.append("upstream received the wrong bytes for the 604-byte frame")

    if failures:
        print(out or "")
        print("FAIL:")
        for f in failures:
            print("  -", f)
        return 1
    print(f"PASS: two frames of 131 and 604 bytes each arrived upstream as ONE read "
          f"({sizes[0]} + {sizes[1]} bytes), byte-exact")
    return 0


if __name__ == "__main__":
    sys.exit(main())
