#!/usr/bin/env python3
"""Parser for drelay's raw packet replay format (work/replay.txt) and for the
structured event logs (work/logs/events-*.jsonl).

replay.txt line grammar (from work/make_replay.py, verified by inspection):
    H <sess> <wallms> <seq>            HealthUpdate (GmHealthUpdate, id 70) reading
    W <sess> <wallms>                  world/map boundary
    U <sess> <wallms> <hex>            GmUpdate (id 1) - raw payload incl. 2-byte id
    P <sess> <wallms> <hex>            other inbound packet
    C <sess> <wallms> <hex>            outbound packet
    I <sess> <wallms> <hex>            relay injection
    N .../ E ...                       notes / errors
The reader here is deliberately tolerant: unknown record letters are counted and
skipped rather than crashing, so a format drift shows up as a counter, not a lie.
"""
from __future__ import annotations

import json
import re
import sys
from collections import Counter, defaultdict

HEX = re.compile(r"^[0-9A-Fa-f]+$")


def parse_replay(path):
    """Yield (kind, sess, wallms, payload_or_None) for every replay line."""
    unknown = Counter()
    n = 0
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.rstrip("\n")
            if not line:
                continue
            n += 1
            parts = line.split(" ", 3)
            kind = parts[0]
            if kind == "H" and len(parts) >= 4:
                yield ("H", parts[1], int(parts[2]), int(parts[3]))
            elif kind == "W" and len(parts) >= 3:
                yield ("W", parts[1], int(parts[2]), None)
            elif kind in ("U", "P", "C", "I") and len(parts) >= 4:
                yield (kind, parts[1], int(parts[2]), parts[3])
            else:
                unknown[kind] += 1
    if unknown:
        print(f"# parse_replay: {n} lines, unrecognised record kinds: {dict(unknown)}",
              file=sys.stderr)


def varint_read(buf, pos):
    """Darza's custom varint, transcribed from the relay's own decoder.

    src/main/java/networking/GameReader.java:138-153:
        result = b & 0x3F; negative = b & 0x40; shift = 6
        while (b & 0x80): b = next; result |= (b & 0x7F) << shift; shift += 7
    So byte 0 carries 6 value bits, the sign in bit 6 and the continuation flag
    in bit 7; the first continuation byte contributes at bit 6 and each
    subsequent one steps by 7 -> the documented "6, 6, 7, 7, ..." grouping.
    `83 0B` is 707 (pinned in networking.PrimitiveTests and work/varint_align.py).
    """
    b = buf[pos]
    pos += 1
    neg = bool(b & 0x40)
    val = b & 0x3F
    shift = 6
    while b & 0x80:
        b = buf[pos]
        pos += 1
        val |= (b & 0x7F) << shift
        shift += 7
    return (-val if neg else val), pos


def decode_health(payload_hex):
    """payload = '<4 hex id>' + 4 varints MaxHealth, Health, Shield, Barrier."""
    raw = bytes.fromhex(payload_hex)
    pos = 2  # skip little-endian ushort id
    vals = []
    for _ in range(4):
        v, pos = varint_read(raw, pos)
        vals.append(v)
    return vals


def iter_events(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                yield json.loads(line)
            except json.JSONDecodeError:
                continue


def pct(sorted_vals, q):
    if not sorted_vals:
        return None
    if len(sorted_vals) == 1:
        return sorted_vals[0]
    idx = q * (len(sorted_vals) - 1)
    lo = int(idx)
    hi = min(lo + 1, len(sorted_vals) - 1)
    frac = idx - lo
    return sorted_vals[lo] * (1 - frac) + sorted_vals[hi] * frac
