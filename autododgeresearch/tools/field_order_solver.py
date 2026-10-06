#!/usr/bin/env python3
"""Field-order solver for the client->server hit packets.

The decompiler flattens obfuscated methods into a `while(true) switch(num)` state
machine whose case labels are encrypted artifacts; the source order it prints is
therefore not the wire order.  Rather than trust it, this script brute-forces every
permutation of a packet's declared fields against every captured packet and keeps
only the orderings that (a) consume the payload exactly and (b) are unique.

Field *types* come from the class declarations (int => varint, bool => one byte).
The answer is a measurement over 100% of the captured packets, not a reading of
obfuscated code.

Sources: work/logs/events-*.jsonl  (600+ captured client->server packets)
"""
from __future__ import annotations

import glob
import itertools
import json
import os
import sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parse_replay import varint_read  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
LOGS = os.path.join(ROOT, "work", "logs")

FILES = ["events-20261004-235440-1500.jsonl",
         "events-20261005-012701-2400.jsonl",
         "events-20261005-023129-2000.jsonl"]

# field name -> kind, taken from the decompiled class declarations.
# The key is the name the RELAY writes into its event log (`pkt`), not the class name.
SPECS = {
    # GmHit, id 102.  DarzaGameNet.Packets.Game.Client/GmHit.cs:8-14
    "ProjHit": {"Time": "varint", "ProjectileId": "varint",
                "ObjectId": "varint", "PlayerProj": "bool"},
    # GmAllyHit, id 174.
    "AllyHit": {"Time": "varint", "ProjectileId": "varint",
                "ObjectId": "varint"},
    # GmUseItem, id 393.
    "UseItem": {"Time": "varint", "SlotId": "byte", "From": "point",
                "Target": "point", "ProjectileId": "varint",
                "GameId": "varint"},
}


def read_hex(pkt):
    """pkt -> {session: [hex payloads]} for the three big real sessions."""
    out = defaultdict(list)
    for name in FILES:
        path = os.path.join(LOGS, name)
        if not os.path.exists(path):
            continue
        for line in open(path, encoding="utf-8", errors="replace"):
            if f'"{pkt}"' not in line:
                continue
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            if e.get("pkt") == pkt and e.get("hex"):
                out[e.get("sess")].append(e["hex"])
    return out


def consume(raw, order, spec):
    pos = 2  # skip the 2-byte little-endian packet id
    got = {}
    for f in order:
        kind = spec[f]
        if kind == "varint":
            if pos >= len(raw):
                return None
            v, pos = varint_read(raw, pos)
            got[f] = v
        elif kind == "bool" or kind == "byte":
            if pos >= len(raw):
                return None
            got[f] = raw[pos]
            pos += 1
        elif kind == "point":
            if pos + 8 > len(raw):
                return None
            import struct
            got[f] = struct.unpack_from("<ff", raw, pos)
            pos += 8
    return (got, pos == len(raw))


def solve(pkt):
    spec = SPECS[pkt]
    payloads = read_hex(pkt)
    total = sum(len(v) for v in payloads.values())
    print(f"\n--- {pkt}: {total} captured packets")
    if not total:
        print("    no captured packets; cannot measure")
        return
    names = list(spec)
    surviving = []
    for order in itertools.permutations(names):
        ok = 0
        bad = 0
        for sess, hexes in payloads.items():
            for h in hexes:
                try:
                    raw = bytes.fromhex(h)
                except ValueError:
                    continue
                r = consume(raw, order, spec)
                if r and r[1]:
                    ok += 1
                else:
                    bad += 1
        if ok and not bad:
            surviving.append((order, ok))
    if not surviving:
        print("    NO field order consumes every payload exactly "
              "-> the declared field set is incomplete or a type is wrong")
        return
    print(f"    {len(surviving)} order(s) consume all {total} payloads exactly.")
    # A permutation of same-typed fields is byte-indistinguishable, so the tie is
    # broken by CONSECUTIVE-PACKET STRUCTURE: Time must be monotonic within a
    # session and must track the same order of magnitude as the other `Time`
    # fields in the protocol (see work/capture-analysis/_timing.txt).
    print("    structural discrimination on consecutive packets of one session:")
    best = []
    for order, ok in surviving:
        per_sess = defaultdict(list)
        for sess, hexes in payloads.items():
            for h in hexes:
                r = consume(bytes.fromhex(h), order, spec)
                if r:
                    per_sess[sess].append(r[0])
        mono = 0
        tot = 0
        for sess, rows in per_sess.items():
            for a, b in zip(rows, rows[1:]):
                tot += 1
                if b["Time"] >= a["Time"]:
                    mono += 1
        frac = mono / tot if tot else 0
        best.append((frac, order, ok))
    best.sort(reverse=True)
    for frac, order, ok in best[:4]:
        print(f"      {frac*100:5.1f}% non-decreasing Time  "
              f"{' -> '.join(order)}")
    order = best[0][1]
    print(f"    SELECTED: {' -> '.join(order)}")
    vals = defaultdict(list)
    for sess, hexes in payloads.items():
        for h in hexes:
            r = consume(bytes.fromhex(h), order, spec)
            if r:
                for k, v in r[0].items():
                    vals[k].append(v)
    print("    value ranges under that order:")
    for k in order:
        v = vals[k]
        if spec[k] == "varint":
            print(f"      {k:14s} min={min(v)} max={max(v)} "
                  f"distinct={len(set(v))}")
        else:
            c = Counter(map(str, v))
            print(f"      {k:14s} values={dict(c.most_common(5))}")


def main():
    for pkt in SPECS:
        solve(pkt)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
