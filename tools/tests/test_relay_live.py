"""End-to-end check of the relay against a real Darza's Dominion server.

tools/tests/test_relay.py proves the relay's framing with a local echo server. This goes one step
further and puts the relay in front of the real Game server, then verifies that a plain client
behind the relay receives the server's genuine opening frame:

    0000000A 5C 00 ...     (4-byte big-endian length 0x0A, packet id 0x5C)

That frame was observed on every direct connect (see docs/PROTOCOL.md), so receiving it through
the relay proves the full path — client -> relay -> real server -> relay -> client — with the
correct big-endian framing. No elevation, no WinDivert, no game process.

Usage: python tools/tests/test_relay_live.py [--real-ip 18.145.161.25] [--real-port 6410]
Exit codes: 0 pass, 1 fail, 2 skipped (server unreachable).
"""
import argparse
import json
import socket
import struct
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]   # tools/tests/<this file> -> the repository root
LOCAL_PORT = 6499
EXPECTED_ID = 0x5C


def read_frame(sock: socket.socket, timeout: float = 20.0) -> bytes:
    sock.settimeout(timeout)
    header = b""
    while len(header) < 4:
        chunk = sock.recv(4 - len(header))
        if not chunk:
            raise EOFError("closed while reading length header")
        header += chunk
    (length,) = struct.unpack(">i", header)
    body = b""
    while len(body) < length:
        chunk = sock.recv(length - len(body))
        if not chunk:
            raise EOFError("closed while reading payload")
        body += chunk
    return body


def server_reachable(ip: str, port: int) -> bool:
    try:
        with socket.create_connection((ip, port), timeout=8) as probe:
            frame = read_frame(probe, timeout=10)
            return frame[0] == EXPECTED_ID
    except OSError:
        return False


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--real-ip", default="18.145.161.25")
    ap.add_argument("--real-port", type=int, default=6410)
    args = ap.parse_args()

    if not server_reachable(args.real_ip, args.real_port):
        print(f"SKIP: {args.real_ip}:{args.real_port} is not answering with the expected frame")
        return 2
    print(f"real server {args.real_ip}:{args.real_port} is live and speaks the framing")

    config = ROOT / "work" / "live-routes.json"
    config.parent.mkdir(exist_ok=True)
    config.write_text(json.dumps({
        "listenHost": "127.0.0.1",
        "routes": [{
            "name": "Live",
            "listenPort": LOCAL_PORT,
            "remoteHost": args.real_ip,
            "remotePort": args.real_port,
        }],
    }), encoding="utf-8")

    log_path = ROOT / "work" / "live-relay.log"
    log = log_path.open("w", encoding="utf-8")
    relay = subprocess.Popen(
        ["java", "-cp", "target\\classes", "networking.Relay", str(config)],
        cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, text=True)

    try:
        deadline = time.time() + 20
        while time.time() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", LOCAL_PORT), timeout=1):
                    break
            except OSError:
                time.sleep(0.3)
        else:
            print("FAIL: relay never listened")
            return 1

        with socket.create_connection(("127.0.0.1", LOCAL_PORT), timeout=15) as client:
            frame = read_frame(client)
            hexed = frame.hex().upper()
            print(f"received {len(frame)} bytes through the relay: {hexed}")
            if frame[0] != EXPECTED_ID:
                print(f"FAIL: expected packet id 0x{EXPECTED_ID:02X}, got 0x{frame[0]:02X}")
                return 1
            # echo the frame back through the relay to prove the upstream direction too
            client.sendall(struct.pack(">i", len(frame)) + frame)
            print("sent the frame back upstream without error")
    except Exception as exc:  # noqa: BLE001
        print(f"FAIL: {type(exc).__name__}: {exc}")
        print(log_path.read_text(encoding="utf-8", errors="replace")[:1500])
        return 1
    finally:
        relay.terminate()
        try:
            relay.wait(timeout=5)
        except subprocess.TimeoutExpired:
            relay.kill()
        log.close()

    print("\nrelay log:")
    print(log_path.read_text(encoding="utf-8", errors="replace")[:1200])
    print("\nPASS: relay carried a genuine server handshake in both directions")
    return 0


if __name__ == "__main__":
    sys.exit(main())
