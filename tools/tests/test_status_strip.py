"""End-to-end check of the server->client status-effect strip module, with no game client.

Everything is driven the way the real relay is driven: a fake game server on one port, a route
pointing at it, and a synthetic client that speaks the game's framing
([4-byte big-endian length][2-byte little-endian type id][body]). The fake server sends a world
entry, health readings and GmUpdate packets; the client asserts on the bytes that come back.

What is checked, and why each case is here:

  1. with the module switched off, every server packet reaches the client byte-identical - the
     feature is inert when it is asked to be, which is the property that makes it safe to ship on
     by default;
  2. with no `strip` block at all, the module is ON and removes exactly Confused (11) and
     Hallucinating (16), which are the defaults. The default is part of the behaviour, so it is
     checked like any other case rather than assumed;
  3. with Confused armed alone, only the local player's Confused entry is removed and the packet is
     exactly nine bytes shorter - which also proves Hallucinating is not removed unless it is armed;
  4. with Paralyzed (6) and Slowed (7) armed as well, all three go, the unarmed Barrier on the same
     list stays, and another object's Slowed in the *same* packet is left alone;
  5. with Hallucinating armed alone, only its entry goes, so the two default effects are independently
     attributable rather than only ever removed together;
  6. the framing stays in sync afterwards - a following packet still arrives whole and unchanged,
     which is the failure this feature could cause and the reason it re-frames the length prefix;
  7. the relay's own event log names the object and the ordinals it removed, so a silent no-op and a
     working strip are distinguishable after the fact.

Usage: python tools/tests/test_status_strip.py [--keep-logs]
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
CLASSES = ROOT / "target" / "classes"
JAVA = "java"

# Payloads, not frames: [LE type id][body]. `frame()` adds the 4-byte big-endian length, and there is
# exactly one place that writes one.
HELLO = bytes.fromhex("370000")        # GmHello (55) plus the one trailing byte the client sends
MAPINFO = bytes.fromhex("0500")        # GmMapInfo (5)

PLAYER_OBJECT = 1930
OTHER_OBJECT = 1924
PARALYZED = 6
SLOWED = 7
CONFUSED = 11
HALLUCINATING = 16
BARRIER = 32


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


# --- the game's own encodings, written out longhand -------------------------------------------

def varint(value: int) -> bytes:
    """The game's custom varint: 6 value bits + sign + continuation, then 7-bit groups.

    Mirrors networking.GameWriter.writeVarint; the first continuation group holds bits 6-12, which
    is the detail that makes ``707 == 83 0B`` and not ``83 16``.
    """
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
        b = remaining & 0x7F
        remaining >>= 7
        out.append(b | 0x80 if remaining else b)
    return bytes(out)


def i16(value: int) -> bytes:
    return struct.pack("<h", value)


def i32(value: int) -> bytes:
    return struct.pack("<i", value)


def f32(value: float) -> bytes:
    return struct.pack("<f", value)


def status_entry(effect: int, tier: int, duration: float) -> bytes:
    return i32(effect) + bytes([tier]) + f32(duration)


def status_list(entries) -> bytes:
    return bytes([len(entries)]) + b"".join(status_entry(*e) for e in entries)


def update(update_id: int, objects) -> bytes:
    """A GmUpdate payload: [LE id][update id][removed][killed][objects][Dt].

    ``objects`` is a list of ``(object_id, [(stat_type, data_type, value_bytes), ...])``.
    """
    body = varint(update_id) + varint(0) + varint(0) + varint(len(objects))
    for object_id, stats in objects:
        body += varint(object_id) + b"\x00" + bytes([len(stats)])
        for stat_type, data_type, value in stats:
            body += bytes([stat_type, data_type]) + value
    body += varint(0)
    return i16(1) + body


def health_update(max_health: int, health: int) -> bytes:
    """GmHealthUpdate (70): four *varints*, not fixed-width shorts.

    Worth stating because it is the trap in this packet: the captured ``46 00 8F 9C 01 ...`` reads as
    9999 only under the game's varint, and a harness that wrote little-endian shorts would produce a
    packet the relay decodes as nonsense - so the health the locator is asked to match would never
    match, and the feature would look broken for a reason unrelated to the feature.
    """
    return i16(70) + varint(max_health) + varint(health) + varint(0) + varint(0)


def statuses_of(payload: bytes):
    """Every object id in a GmUpdate and every status list it carries.

    Returns ``([(object_id, [(effect, tier, duration), ...])], [object_id, ...])``. The object ids
    matter because the rewrite shortens the packet, and an object *after* the shortened list is the
    only place a wrong length shows up.
    """
    index = 2
    index = skip_varint(payload, index)          # update id
    count, index = read_varint(payload, index)
    for _ in range(count):
        index = skip_varint(payload, index)
    count, index = read_varint(payload, index)
    for _ in range(count):
        index = skip_varint(payload, index)
    objects, index = read_varint(payload, index)
    found = []
    object_ids = []
    for _ in range(objects):
        object_id, index = read_varint(payload, index)
        object_ids.append(object_id)
        if payload[index]:
            raise AssertionError("fixture should not use new objects")
        index += 1
        stats = payload[index]
        index += 1
        for _ in range(stats):
            stat_type = payload[index]
            data_type = payload[index + 1]
            index += 2
            if stat_type == 78 and data_type == 8:
                n = payload[index]
                index += 1
                entries = []
                for _ in range(n):
                    entries.append((struct.unpack_from("<i", payload, index)[0],
                                    payload[index + 4],
                                    struct.unpack_from("<f", payload, index + 5)[0]))
                    index += 9
                found.append((object_id, entries))
            else:
                index = skip_value(payload, index, data_type)
    index = skip_varint(payload, index)          # Dt
    if index != len(payload):
        raise AssertionError("the walk ended at %d of %d bytes" % (index, len(payload)))
    return found, object_ids


def skip_value(payload: bytes, index: int, data_type: int) -> int:
    if data_type == 0:
        return index + 1
    if data_type == 1:
        return index + 2
    if data_type == 2:
        return index + 4
    if data_type == 3:
        return index + 8
    if data_type == 4:
        length = payload[index]
        return index + 1 + length
    if data_type == 6:
        return index + 8
    if data_type == 7:
        return index + 4
    if data_type == 9:
        return index + 4
    raise AssertionError("fixture uses an unhandled data type %d" % data_type)


def read_varint(payload: bytes, index: int):
    b = payload[index]
    index += 1
    result = b & 0x3F
    negative = bool(b & 0x40)
    shift = 6
    while b & 0x80:
        b = payload[index]
        index += 1
        result |= (b & 0x7F) << shift
        shift += 7
    return (-result if negative else result), index


def skip_varint(payload: bytes, index: int) -> int:
    return read_varint(payload, index)[1]


def contains_effect(payload: bytes, object_id: int, effect: int) -> bool:
    lists, _ = statuses_of(payload)
    for oid, entries in lists:
        if oid == object_id and any(e[0] == effect for e in entries):
            return True
    return False


def list_of(payload: bytes, object_id: int):
    """The status entries one object carries, or None when it carries no list."""
    lists, _ = statuses_of(payload)
    for oid, entries in lists:
        if oid == object_id:
            return entries
    return None


# --- the fake server ---------------------------------------------------------------------------

class FakeServer:
    """Stands in for the game server, and the only reader of the upstream socket.

    One reader, deliberately: a thread drains the socket and the test pops frames off the queue.
    Two threads calling ``recv`` on the same socket split the stream between them, which looks
    exactly like the relay dropping a packet.
    """

    def __init__(self, port: int):
        self.port = port
        self.ready = threading.Event()
        self.connected = threading.Event()
        self.conn = None
        self.received = []
        self.arrived = threading.Condition()
        self.error = None
        threading.Thread(target=self._run, daemon=True).start()

    def _run(self):
        listener = socket.socket()
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind(("127.0.0.1", self.port))
        listener.listen(4)
        self.port = listener.getsockname()[1]
        self.ready.set()
        try:
            # The relay dials twice: once in its preflight, which it hangs up immediately, and once
            # for the session itself. A server that stopped listening after the first hang-up refuses
            # the real dial, and the test then fails for a reason that has nothing to do with the
            # feature. So keep accepting, and always talk on the newest connection.
            while True:
                conn, _ = listener.accept()
                self.conn = conn
                self.connected.set()
                try:
                    while True:
                        payload = read_frame(conn)
                        with self.arrived:
                            self.received.append(payload)
                            self.arrived.notify_all()
                except Exception:
                    pass                      # preflight hang-up, or the end of the test
        except OSError as e:
            self.error = e
        finally:
            listener.close()

    def next_frame(self, timeout: float = 5.0):
        """The next payload the relay forwarded upstream, or None on timeout."""
        deadline = time.time() + timeout
        with self.arrived:
            while not self.received:
                remaining = deadline - time.time()
                if remaining <= 0:
                    return None
                self.arrived.wait(remaining)
            return self.received.pop(0)

    def send(self, payload: bytes):
        self.conn.sendall(frame(payload))


def write_config(path: Path, log_dir: Path, relay_port: int, upstream_port: int, strip):
    """The route table for one run; ``strip`` is the whole block, or None to leave it out.

    Omitting it is a case in its own right: the module's default is on with Confused armed, and the
    only way to check a default is to not configure it.
    """
    document = {
        "listenHost": "127.0.0.1",
        "logDirectory": str(log_dir),
        "ringCapacity": 5000,
        "web": {"host": "127.0.0.1", "port": 0},
        "routes": [{
            "name": "Game",
            "listenPort": relay_port,
            "remoteHost": "127.0.0.1",
            "remotePort": upstream_port,
        }],
    }
    if strip is not None:
        document["strip"] = strip
    path.write_text(json.dumps(document, indent=2), encoding="utf-8")


def startup_line(effects):
    """The startup text the relay should print for an armed set (or for being off)."""
    if not effects:
        return "strip: disabled"
    names = {PARALYZED: "paralyzed", SLOWED: "slowed", CONFUSED: "confused",
             HALLUCINATING: "hallucinating"}
    return "status effect(s) %s" % ", ".join(
        "%d (%s)" % (effect, names[effect]) for effect in sorted(effects))


# The five cases, each as (label, strip block or None, the effects that must be removed from the
# player). The "default" case has no strip block at all, which is the on-by-default claim: the module
# ships armed with Confused (11) and Hallucinating (16). The fixture carries all four named debuffs in
# every case, so each case also asserts that the effects it did not arm survive untouched - which is
# what keeps the two defaults independently attributable rather than only ever removed together.
CASES = [
    ("off", {"enabled": False, "effects": [CONFUSED, HALLUCINATING], "minVotes": 3}, set()),
    ("default", None, {CONFUSED, HALLUCINATING}),
    ("confused", {"enabled": True, "effects": [CONFUSED], "minVotes": 3}, {CONFUSED}),
    ("multi", {"enabled": True, "effects": [PARALYZED, SLOWED, CONFUSED], "minVotes": 3},
     {PARALYZED, SLOWED, CONFUSED}),
    ("halluc", {"enabled": True, "effects": [HALLUCINATING], "minVotes": 3}, {HALLUCINATING}),
]


def run_once(label: str, strip, expected_removed: set, keep_logs: bool):
    """One relay run: return (failures, log_path)."""
    failures = []
    server = FakeServer(0)
    if not server.ready.wait(5):
        return ["the fake server did not start"], None

    probe = socket.socket()
    probe.bind(("127.0.0.1", 0))
    relay_port = probe.getsockname()[1]
    probe.close()

    log_dir = ROOT / "work" / "logs" / "strip-test"
    config = ROOT / "work" / ("strip-%s-routes.json" % label)
    write_config(config, log_dir, relay_port, server.port, strip)

    relay = subprocess.Popen([JAVA, "-cp", str(CLASSES), "networking.Relay", str(config)],
                             cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    relay_output = []
    threading.Thread(target=lambda: [relay_output.append(line) for line in relay.stdout],
                     daemon=True).start()
    log_path = None
    client = None
    try:
        client = None
        for _ in range(60):
            try:
                client = socket.create_connection(("127.0.0.1", relay_port), timeout=2)
                break
            except OSError:
                time.sleep(0.25)
        if client is None:
            return ["the relay never accepted a connection on port %d" % relay_port], None
        if not server.connected.wait(5):
            return ["the relay never dialled the fake server"], None

        client.sendall(frame(HELLO))
        forwarded = server.next_frame()
        if forwarded != HELLO:
            failures.append("the relay did not forward the client's Hello unchanged (got %r)"
                            % (forwarded,))

        server.send(MAPINFO)
        if read_frame(client) != MAPINFO:
            failures.append("the relay did not forward MapInfo unchanged")

        # Three matching readings: the relay needs evidence before it will name the player object.
        for tick in range(3):
            health = 400 + tick
            server.send(health_update(500, health))
            if read_frame(client) != health_update(500, health):
                failures.append("a HealthUpdate was not forwarded unchanged")
            server.send(update(10 + tick, [(PLAYER_OBJECT, [(2, 1, i16(health))])]))
            if read_frame(client) != update(10 + tick, [(PLAYER_OBJECT, [(2, 1, i16(health))])]):
                failures.append("a plain Update was not forwarded unchanged")

        # The packet under test: four effects on the player, two on somebody else, and a stat after
        # both lists so a rewrite that is short by nine bytes cannot still look correct.
        kept_on_player = {BARRIER} | ({CONFUSED, SLOWED, PARALYZED, HALLUCINATING} - expected_removed)
        marker = (7, 9, f32(1.5))
        packet = update(99, [
            (OTHER_OBJECT, [(78, 8, status_list([(BARRIER, 1, 69420.0), (SLOWED, 1, 0.5)]))]),
            (PLAYER_OBJECT, [(78, 8, status_list([(BARRIER, 1, 69420.0), (CONFUSED, 1, 0.85),
                                                  (SLOWED, 1, 2.5), (PARALYZED, 1, 1.5),
                                                  (HALLUCINATING, 1, 10.0)]))]),
            (555, [marker]),
        ])
        server.send(packet)
        arrived = read_frame(client)

        if expected_removed:
            for effect in expected_removed:
                if contains_effect(arrived, PLAYER_OBJECT, effect):
                    failures.append("effect %d on the player reached the client" % effect)
            for effect in kept_on_player:
                if not contains_effect(arrived, PLAYER_OBJECT, effect):
                    failures.append("arming %s removed the unrelated effect %d from the player"
                                    % (sorted(expected_removed), effect))
            if not contains_effect(arrived, OTHER_OBJECT, SLOWED):
                failures.append("another object's Slowed entry was removed as well")
            if len(arrived) != len(packet) - 9 * len(expected_removed):
                failures.append("the rewritten packet is %d bytes, expected %d"
                                % (len(arrived), len(packet) - 9 * len(expected_removed)))
            entries = list_of(arrived, PLAYER_OBJECT)
            if not entries or len(entries) != 5 - len(expected_removed):
                failures.append("the player's list survived with the wrong number of entries: %r"
                                % (entries,))
            _, object_ids = statuses_of(arrived)
            if not any(oid == 555 for oid in object_ids):
                failures.append("the object after the rewritten list lost its stats")
        else:
            if arrived != packet:
                failures.append("the strip is off but the packet was changed anyway")

        # The framing check: a packet sent after a rewrite must still arrive whole. A length prefix
        # that disagreed with the new payload would desynchronise here and nowhere else.
        tail = health_update(500, 401)
        server.send(tail)
        if read_frame(client) != tail:
            failures.append("the stream lost framing after the rewrite")

        client.close()
        time.sleep(0.4)

        logs = sorted(log_dir.glob("events-*.jsonl"), key=lambda p: p.stat().st_mtime)
        if logs:
            log_path = logs[-1]
            text = log_path.read_text(encoding="utf-8", errors="replace")
            stripped = [json.loads(line) for line in text.splitlines() if '"stripped"' in line]
            if expected_removed and not stripped:
                failures.append("the event log records no strip, so the rewrite is unattributable")
            if expected_removed and stripped:
                data = stripped[-1].get("data", {})
                if data.get("objectId") != PLAYER_OBJECT:
                    failures.append("the logged strip names object %r" % data.get("objectId"))
                if sorted(data.get("effects") or []) != sorted(expected_removed):
                    failures.append("the logged strip names effects %r, expected %r"
                                    % (data.get("effects"), sorted(expected_removed)))
                if data.get("bytesAfter", 0) >= data.get("bytesBefore", 0):
                    failures.append("the logged strip does not record a shorter packet")
            if not expected_removed and stripped:
                failures.append("the log records a strip while the feature was switched off")
    except Exception as e:
        # A closed socket is a failure to report, not a traceback: the interesting part is usually
        # what the relay said on its way out.
        failures.append("the session died: %s: %s" % (type(e).__name__, e))
    finally:
        if client is not None:
            try:
                client.close()
            except OSError:
                pass
        relay.terminate()
        try:
            relay.wait(timeout=5)
        except subprocess.TimeoutExpired:
            relay.kill()
        if failures and relay.stdout is not None:
            interesting = [line for line in relay_output
                           if "error" in line.lower() or "refused" in line.lower()
                           or "could not" in line.lower()]
            for line in interesting[:10]:
                failures.append("relay said: " + line.strip())
        if not keep_logs:
            for stray in (ROOT / "work").glob("strip-*-routes.json"):
                stray.unlink(missing_ok=True)

    # The startup line is the operator's only confirmation that the settings were read: a typo in the
    # route table would otherwise look identical to "the debuff never arrived". Checked on every run,
    # passing or failing, because it is the one thing a live test depends on being true.
    expected = startup_line(expected_removed)
    if not any(expected in line for line in relay_output):
        failures.append("the relay did not report the strip setting at startup (wanted %r)" % expected)

    return failures, log_path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--keep-logs", action="store_true")
    args = parser.parse_args()

    if not CLASSES.exists():
        print("FAIL: target/classes is missing; build first (./build.ps1)")
        return 2

    all_failures = []
    for label, strip, expected_removed in CASES:
        failures, log_path = run_once(label, strip, expected_removed, args.keep_logs)
        if failures:
            all_failures.extend("%s: %s" % (label, f) for f in failures)
        else:
            print("   %-9s: ok%s" % (label, "" if log_path is None else "  (log %s)" % log_path.name))

    if all_failures:
        print("FAIL: %d problem(s)" % len(all_failures))
        for failure in all_failures:
            print("  - " + failure)
        return 1
    print("PASS: the strip is on by default, inert when switched off, removes only the armed effects "
          "from only the local player's list, keeps the framing, and says so in the log")
    return 0


if __name__ == "__main__":
    sys.exit(main())
