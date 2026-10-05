"""End-to-end check of the drelay relay without the game.

Stands up a fake "game server", points a relay route at it, and drives a client
through the relay with the wire framing the decompiled client uses:
[4-byte big-endian length][payload], payload[0] == packet id.

Asserts that bytes arrive unchanged, in order, and that the relay honours
big-endian lengths (the original proxy wrote little-endian and would fail here).

Usage: python tools/tests/test_relay.py [--relay-port 6510] [--upstream-port 6610]
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

ROOT = Path(__file__).resolve().parents[2]   # tools/tests/<this file> -> the repository root


def frame(payload: bytes) -> bytes:
    return struct.pack(">i", len(payload)) + payload


def read_frame(stream: socket.socket) -> bytes:
    header = b""
    while len(header) < 4:
        chunk = stream.recv(4 - len(header))
        if not chunk:
            raise EOFError("closed while reading header")
        header += chunk
    (length,) = struct.unpack(">i", header)
    body = b""
    while len(body) < length:
        chunk = stream.recv(length - len(body))
        if not chunk:
            raise EOFError("closed while reading body")
        body += chunk
    return body


def fake_server(port: int, ready: threading.Event, received: list) -> None:
    def serve(conn: socket.socket) -> None:
        with conn:
            # echo everything back, so the client can verify both directions
            while True:
                try:
                    body = read_frame(conn)
                except EOFError:
                    break
                received.append(body)
                conn.sendall(frame(b"\x99" + body))

    with socket.socket() as srv:
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("127.0.0.1", port))
        srv.listen(4)
        ready.set()
        # The relay probes its destination once at startup and that probe is a real connection, so
        # this peer has to keep accepting instead of serving exactly one session.
        while True:
            conn, _ = srv.accept()
            threading.Thread(target=serve, args=(conn,), daemon=True).start()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--relay-port", type=int, default=6510)
    ap.add_argument("--upstream-port", type=int, default=6610)
    args = ap.parse_args()

    ready = threading.Event()
    received: list = []
    threading.Thread(target=fake_server, args=(args.upstream_port, ready, received), daemon=True).start()
    if not ready.wait(5):
        print("FAIL: fake server did not start")
        return 1

    config = ROOT / "work" / "test-routes.json"
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

    deadline = time.time() + 20
    connected = False
    while time.time() < deadline:
        try:
            client = socket.create_connection(("127.0.0.1", args.relay_port), timeout=2)
            connected = True
            break
        except OSError:
            time.sleep(0.3)

    if not connected:
        relay.kill()
        print("FAIL: relay never accepted a connection")
        print(relay.stdout.read() if relay.stdout else "")
        return 1

    failures = []
    try:
        payloads = [
            bytes([0x46]) + b"health-update",
            bytes([0x42]),
            bytes([0x01]) + bytes(range(256)),
        ]
        with client:
            for payload in payloads:
                client.sendall(frame(payload))
                echoed = read_frame(client)
                if echoed != b"\x99" + payload:
                    failures.append(f"round trip mismatch for payload id 0x{payload[0]:02X}")
            if received and received[0] != payloads[0]:
                failures.append("upstream received wrong bytes")
    except Exception as exc:  # noqa: BLE001
        failures.append(f"exception: {exc}")
    finally:
        relay.terminate()
        try:
            out = relay.communicate(timeout=10)[0]
        except subprocess.TimeoutExpired:
            relay.kill()
            out = ""

    print(out or "")
    if failures:
        print("FAIL:")
        for f in failures:
            print("  -", f)
        return 1
    print(f"PASS: {len(payloads)} framed packets round-tripped byte-exact through the relay")
    return 0


if __name__ == "__main__":
    sys.exit(main())
