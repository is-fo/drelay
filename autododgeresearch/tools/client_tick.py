#!/usr/bin/env python3
"""Client logic-frame cadence, measured from the wire.

`GmMove` (id 74) is `[float X][float Y][varint Time]` and `Time` is
`World.ClientTime = (int)ClientTime.LogicTotal` (work/report-handlers.md C.7) --
the client's accumulated LOGIC time.  The difference between consecutive Move
packets on one session is therefore the client's logic frame period, which is the
number that decides how long an in-process hook can take to react.

Source (read-only): work/logs/events-*.jsonl
"""
from __future__ import annotations

import json
import os
import statistics
import struct
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parse_replay import varint_read  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
LOGS = os.path.join(ROOT, "work", "logs")
FILES = ["events-20261004-235440-1500.jsonl",
         "events-20261005-012701-2400.jsonl",
         "events-20261005-023129-2000.jsonl"]


def main():
    deltas = Counter()
    wall = Counter()
    for name in FILES:
        path = os.path.join(LOGS, name)
        if not os.path.exists(path):
            continue
        prev = {}
        prev_wall = {}
        for line in open(path, encoding="utf-8", errors="replace"):
            if '"Move"' not in line:
                continue
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            if e.get("pkt") != "Move" or not e.get("hex"):
                continue
            raw = bytes.fromhex(e["hex"])
            if len(raw) < 12:
                continue
            try:
                _, _ = struct.unpack_from("<ff", raw, 2)
                t, _ = varint_read(raw, 10)
            except Exception:
                continue
            s = e.get("sess")
            if s in prev:
                d = t - prev[s]
                if 0 < d < 500:
                    deltas[d] += 1
                w = e.get("ms") - prev_wall[s]
                if 0 < w < 2000:
                    wall[w] += 1
            prev[s] = t
            prev_wall[s] = e.get("ms")
    print("client logic-tick delta between consecutive Move packets (ms):")
    for k, v in sorted(deltas.items()):
        print(f"   {k:5d} ms : {v:6d}")
    vals = [k for k, v in deltas.items() for _ in range(v)]
    if vals:
        print(f"\n  n={len(vals)}  median={statistics.median(vals)}  "
              f"mean={statistics.fmean(vals):.1f}  "
              f"p90={sorted(vals)[int(len(vals)*0.9)]}")
    print("\nwall-clock delta between the same consecutive Move packets (ms):")
    for k, v in sorted(wall.items())[:40]:
        print(f"   {k:5d} ms : {v:6d}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
