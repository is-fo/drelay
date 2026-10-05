"""End-to-end check that the relay decodes and logs a `GmReconnect` retarget.

`GmReconnect` (game id 36) is the only packet that tells the client to move to another game
server, and under an address claim it is the only way a session can leave the proxy unnoticed.
`networking.Relay` models it and prints the target. This test proves that end to end, without the
game: the fake peer sits on the server side and sends the packet, and the assertions are that the
bytes still arrive unchanged (decoding must never alter forwarding) and that the log names the
target host and port.

Usage: python tools/tests/test_reconnect_log.py [--relay-port 6520] [--upstream-port 6620]
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

RECONNECT_ID = 36
TARGET_HOST = "18.145.215.213"
TARGET_PORT = 6411
TARGET_CHARACTER = 3774335

# The payload as captured live on 2026-10-02, when the server moved the client to a realm:
#   24 00              type, a 2-byte little-endian ushort (game packets, not the queue's 1 byte)
#   0E 00 00 00        string32 host length (4-byte little-endian)
#   "18.145.215.213"   host
#   8B 64              varint port 6411
#   00                 ToBeyond = false
#   7F 97 39 00 00 00 00 00   CharacterId, little-endian int64 = 3774335
CAPTURED = bytes.fromhex("24000E00000031382E3134352E3231352E3231338B64007F97390000000000")


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


def write_string32(text: str) -> bytes:
    raw = text.encode("utf-8")
    return struct.pack("<i", len(raw)) + raw


def write_varint(value: int) -> bytes:
    # Mirrors networking.GameWriter.writeVarint: 6 value bits + sign in the first byte, 7 after.
    negative = value < 0
    remaining = abs(value)
    first = remaining & 0x3F
    if negative:
        first |= 0x40
    remaining >>= 6
    if remaining == 0:
        return bytes([first])
    out = bytearray([first | 0x80])
    while remaining:
        chunk = remaining & 0x7F
        remaining >>= 7
        out.append(chunk | 0x80 if remaining else chunk)
    return bytes(out)


def reconnect_payload() -> bytes:
    """The payload the real server sends, built field by field from the client's Read order."""
    return (
        struct.pack("<H", RECONNECT_ID)                # game packets begin with a 2-byte LE type
        + write_string32(TARGET_HOST)
        + write_varint(TARGET_PORT)
        + bytes([0])                                   # ToBeyond = false
        + struct.pack("<q", TARGET_CHARACTER)          # CharacterId, little-endian
    )


def fake_server(port: int, ready: threading.Event, payload: bytes) -> None:
    def serve(conn: socket.socket) -> None:
        with conn:
            try:
                conn.sendall(frame(payload))
                time.sleep(0.5)
            except OSError:
                pass

    with socket.socket() as srv:
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("127.0.0.1", port))
        srv.listen(4)
        ready.set()
        # The relay dials its destination once at startup, so keep accepting rather than
        # serving a single session.
        while True:
            conn, _ = srv.accept()
            threading.Thread(target=serve, args=(conn,), daemon=True).start()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--relay-port", type=int, default=6520)
    ap.add_argument("--upstream-port", type=int, default=6620)
    args = ap.parse_args()

    payload = reconnect_payload()

    ready = threading.Event()
    threading.Thread(target=fake_server, args=(args.upstream_port, ready, payload), daemon=True).start()
    if not ready.wait(5):
        print("FAIL: fake server did not start")
        return 1

    config = ROOT / "work" / "reconnect-routes.json"
    config.write_text(json.dumps({
        "listenHost": "127.0.0.1",
        "routes": [{
            "name": "Game",
            "listenPort": args.relay_port,
            "remoteHost": "127.0.0.1",
            "remotePort": args.upstream_port,
        }],
    }), encoding="utf-8")

    relay = subprocess.Popen(
        ["java", "-cp", "target\\classes", "networking.Relay", str(config)],
        cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
    )

    failures = []
    echoed = None
    deadline = time.time() + 20
    while time.time() < deadline:
        try:
            client = socket.create_connection(("127.0.0.1", args.relay_port), timeout=2)
            break
        except OSError:
            time.sleep(0.3)
    else:
        relay.kill()
        print("FAIL: relay never accepted a connection")
        print(relay.stdout.read() if relay.stdout else "")
        return 1

    try:
        with client:
            client.settimeout(5)
            echoed = read_frame(client)
    except Exception as exc:  # noqa: BLE001
        failures.append(f"exception reading the forwarded frame: {exc}")
    finally:
        relay.terminate()
        try:
            out = relay.communicate(timeout=10)[0]
        except subprocess.TimeoutExpired:
            relay.kill()
            out = ""

    print(out or "")

    if payload != CAPTURED:
        failures.append(f"the built payload no longer matches the live capture: {payload.hex()}")
    if echoed != payload:
        failures.append(f"forwarding changed the packet (got {echoed!r})")

    log = out or ""
    expected = f"RETARGET -> {TARGET_HOST}:{TARGET_PORT}  characterId={TARGET_CHARACTER}  toBeyond=false"
    if expected not in log:
        failures.append(f"relay log did not contain: {expected!r}")
    if "Reconnect (codec)" not in log:
        failures.append("relay log did not mark the packet as a decoded Reconnect")
    # Learning matters as much as decoding: without it the follow-up dial is forwarded to the stale
    # route destination instead of the address the server just named.
    if f"port {TARGET_PORT} now forwards to {TARGET_HOST}:{TARGET_PORT}" not in log:
        failures.append("relay did not record the destination learned from the retarget")
    # The claim is best effort: elevated it succeeds, unelevated it must refuse loudly rather than
    # crash or claim the wrong thing.
    claimed = f"claimed {TARGET_HOST} on the loopback interface" in log
    refused = f"could not claim {TARGET_HOST}" in log
    if not (claimed or refused):
        failures.append("relay did not report the outcome of claiming the retarget address")
    else:
        print(f"(claim path taken: {'claimed' if claimed else 'refused (unelevated)'})")

    if failures:
        print("FAIL:")
        for f in failures:
            print("  -", f)
        return 1
    print("PASS: Reconnect was forwarded byte-exact and the relay logged the retarget target")
    return 0


if __name__ == "__main__":
    sys.exit(main())
