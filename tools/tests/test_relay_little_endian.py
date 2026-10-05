"""Prove the relay handles the queue's little-endian framing end to end.

The live queue session failed with "implausible payload length 134217728" because a
little-endian length header (08 00 00 00 = 8) was read as big-endian (0x08000000). The relay now
detects the order per session. No real queue server is needed to exercise that path: this stands
up a little-endian peer, points a relay route at it, and drives a client through the relay.

Also checks the opposite case, so the detection cannot regress into "always little-endian".

Usage: python tools/tests/test_relay_little_endian.py
Exit codes: 0 pass, 1 fail.
"""
import json
import socket
import struct
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]   # tools/tests/<this file> -> the repository root
RELAY_PORT = 6597
PEER_PORT = 6697


def read_frame(sock: socket.socket, order: str, timeout: float = 15.0) -> bytes:
    sock.settimeout(timeout)
    header = b""
    while len(header) < 4:
        chunk = sock.recv(4 - len(header))
        if not chunk:
            raise EOFError("closed while reading header")
        header += chunk
    (length,) = struct.unpack("<i" if order == "little" else ">i", header)
    body = b""
    while len(body) < length:
        chunk = sock.recv(length - len(body))
        if not chunk:
            raise EOFError("closed while reading body")
        body += chunk
    return body


def fake_peer(port: int, order: str, ready: threading.Event, seen: list, reply: bytes) -> None:
    with socket.socket() as srv:
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("127.0.0.1", port))
        srv.listen(5)
        srv.settimeout(30)
        ready.set()
        # The readiness probe connects without sending, so keep accepting until a real
        # request arrives rather than treating the first connection as the client.
        while True:
            try:
                conn, _ = srv.accept()
            except OSError:
                return
            with conn:
                try:
                    frame = read_frame(conn, order, timeout=8)
                except (EOFError, OSError):
                    continue
                seen.append(frame)
                prefix = struct.pack("<i" if order == "little" else ">i", len(reply))
                conn.sendall(prefix + reply)
                time.sleep(1)
                return


def run_case(order: str, relay_port: int, peer_port: int) -> list[str]:
    failures: list[str] = []
    ready = threading.Event()
    seen: list = []
    # a queue Hello-shaped reply: id 1, then a string8
    reply = bytes([0x01, 0x06]) + b"Zenith"
    threading.Thread(target=fake_peer, args=(peer_port, order, ready, seen, reply), daemon=True).start()
    if not ready.wait(5):
        return [f"{order}: fake peer did not start"]

    config = ROOT / "work" / f"le-routes-{order}.json"
    config.parent.mkdir(exist_ok=True)
    config.write_text(json.dumps({
        "listenHost": "127.0.0.1",
        "routes": [{"name": "Queue", "listenPort": relay_port,
                    "remoteHost": "127.0.0.1", "remotePort": peer_port}],
    }), encoding="utf-8")

    relay = subprocess.Popen(["java", "-cp", "target\\classes", "networking.Relay", str(config)],
                             cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    try:
        deadline = time.time() + 20
        while time.time() < deadline:
            try:
                socket.create_connection(("127.0.0.1", relay_port), timeout=1).close()
                break
            except OSError:
                time.sleep(0.3)
        else:
            return [f"{order}: relay never listened"]

        # client sends a queue Join-shaped request: id 3 + string8 token
        request = bytes([0x03, 0x03]) + b"abc"
        with socket.create_connection(("127.0.0.1", relay_port), timeout=10) as client:
            client.sendall(struct.pack("<i" if order == "little" else ">i", len(request)) + request)
            got = read_frame(client, order)

        if got != reply:
            failures.append(f"{order}: reply mismatch: expected {reply.hex()}, got {got.hex()}")
        if not seen:
            failures.append(f"{order}: peer never received the request")
        elif seen[0] != request:
            failures.append(f"{order}: peer got {seen[0].hex()}, expected {request.hex()}")
    except Exception as exc:  # noqa: BLE001
        failures.append(f"{order}: {type(exc).__name__}: {exc}")
    finally:
        relay.terminate()
        try:
            out = relay.communicate(timeout=10)[0]
        except subprocess.TimeoutExpired:
            relay.kill()
            out = ""
        if out:
            for line in out.splitlines():
                if "detected" in line or "stream error" in line:
                    print(f"   [{order}] {line.strip()}")
    return failures


def main() -> int:
    failures = []
    failures += run_case("little", RELAY_PORT, PEER_PORT)
    failures += run_case("big", RELAY_PORT + 1, PEER_PORT + 1)

    if failures:
        print("FAIL:")
        for failure in failures:
            print("  -", failure)
        return 1
    print("PASS: relay handled both little-endian and big-endian framing end to end")
    return 0


if __name__ == "__main__":
    sys.exit(main())
