"""End-to-end check that the relay decodes and logs a `GmReconnect` retarget, and a `GmKicked` reason.

`GmReconnect` (game id 36) is the only packet that tells the client to move to another game
server, and under an address claim it is the only way a session can leave the proxy unnoticed.
`networking.Relay` models it and prints the target. This test proves that end to end, without the
game: the fake peer sits on the server side and sends the packet, and the assertions are that the
bytes still arrive unchanged (decoding must never alter forwarding) and that the log names the
target host and port.

The same run then sends `GmKicked` (id 185), the server's own reason for ending the session. It is
the same shape of check - decode a server->client packet and prove the field reaches the log - and
it is the fastest explanation of a failed server->client rewrite, so it is asserted in the same
place, on disk in `events-*.jsonl`: `data.reason` on the packet event, and `kickedReason` on the
session-close event that the teardown produces a moment later.

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

KICKED_ID = 185
KICK_REASON = "speed hack detected"

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


def kicked_payload() -> bytes:
    """`GmKicked`: the type id, then the reason as a string16 (2-byte LE length + UTF-8).

    `GmKicked.Read` is a single `ReadString16` of `Reason`, exactly like `GmForcedEscape` (184) one
    id earlier, so the payload is built the same way - and the UTF-8 length is a byte count, not a
    character count.
    """
    raw = KICK_REASON.encode("utf-8")
    return struct.pack("<H", KICKED_ID) + struct.pack("<H", len(raw)) + raw


def fake_server(port: int, ready: threading.Event, payloads) -> None:
    def serve(conn: socket.socket) -> None:
        with conn:
            try:
                for payload in payloads:
                    conn.sendall(frame(payload))
                    time.sleep(0.25)
                # Then hang up, the way a server does after a kick. That teardown is what produces the
                # session-close event the reason has to survive into.
                time.sleep(0.3)
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


def newest_events(log_dir: Path):
    """The newest `events-*.jsonl` under ``log_dir``, parsed into events, or [] if there is none.

    A dedicated directory per test run keeps this unambiguous: the relay's run id is a timestamp, so
    "newest by mtime" is this run's log and never a previous one's.
    """
    logs = sorted(log_dir.glob("events-*.jsonl"), key=lambda p: p.stat().st_mtime)
    if not logs:
        return []
    events = []
    for line in logs[-1].read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except ValueError:
                pass
    return events


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--relay-port", type=int, default=6520)
    ap.add_argument("--upstream-port", type=int, default=6620)
    args = ap.parse_args()

    payload = reconnect_payload()
    kicked = kicked_payload()

    ready = threading.Event()
    threading.Thread(target=fake_server, args=(args.upstream_port, ready, [payload, kicked]),
                     daemon=True).start()
    if not ready.wait(5):
        print("FAIL: fake server did not start")
        return 1

    config = ROOT / "work" / "reconnect-routes.json"
    log_dir = ROOT / "work" / "logs" / "reconnect-test"
    config.write_text(json.dumps({
        "listenHost": "127.0.0.1",
        "logDirectory": str(log_dir),
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
    echoed_kick = None
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
            echoed_kick = read_frame(client)
        # Both directions then close (the client above, the fake peer a moment later), which is what
        # lets the relay finish the session and write the close event. Give that write time to land
        # before the process is taken down.
        time.sleep(1.2)
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
    if echoed_kick != kicked:
        failures.append(f"forwarding changed the GmKicked packet (got {echoed_kick!r})")

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

    if "Kicked (codec)" not in log:
        failures.append("relay did not mark the packet as a decoded Kicked")

    # On disk, which is where a failed run is actually analysed: the reason on the packet event, and
    # again on the session-close event, so a disconnect is never cause-less.
    events = newest_events(log_dir)
    if not events:
        failures.append(f"no events-*.jsonl under {log_dir}, so nothing can be checked on disk")
    else:
        kicks = [e for e in events if e.get("pkt") == "Kicked"]
        if not kicks:
            failures.append("no Kicked packet event in events-*.jsonl")
        else:
            kick = kicks[-1]
            print(f"   kicked event: id={kick.get('id')} data={kick.get('data')}")
            if kick.get("id") != KICKED_ID:
                failures.append(f"the Kicked event carries id {kick.get('id')!r}")
            if (kick.get("data") or {}).get("reason") != KICK_REASON:
                failures.append("the packet event's data.reason is %r, expected %r"
                                % ((kick.get("data") or {}).get("reason"), KICK_REASON))
            if KICK_REASON not in (kick.get("note") or ""):
                failures.append(f"the packet event's note does not name the reason: {kick.get('note')!r}")
        closes = [e for e in events
                  if e.get("kind") == "session" and "closed" in (e.get("note") or "")]
        if not closes:
            failures.append("no session-close event in events-*.jsonl")
        else:
            close = closes[-1]
            print(f"   close event: note={close.get('note')!r}")
            if (close.get("data") or {}).get("kickedReason") != KICK_REASON:
                failures.append("the close event lost kickedReason: %r" % (close.get("data"),))
            if KICK_REASON not in (close.get("note") or ""):
                failures.append(f"the close event's note lost the reason: {close.get('note')!r}")

    if failures:
        print("FAIL:")
        for f in failures:
            print("  -", f)
        return 1
    print("PASS: Reconnect and Kicked were forwarded byte-exact, the relay logged the retarget target, "
          "and the kick reason reached both log events")
    return 0


if __name__ == "__main__":
    sys.exit(main())
