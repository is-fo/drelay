#!/usr/bin/env python3
"""Identify the ObjectId carried by a PlayerProj=false hit report.

Hypothesis: that ObjectId is the LOCAL PLAYER's object id -- i.e. the packet is
the client telling the server "an enemy projectile hit me".

Method: the same ObjectId is cross-referenced against the per-object stats the
server streams in GmUpdate (id 1) for the same session/world.  The relay stores
those as raw payload hex, and the StatsType.Hp stat is an int16 (StatsType 2,
DataType 1) inside an object block whose leading varint is the object id.  If the
ObjectId in the hit report tracks the same Hp values as the GmHealthUpdate
readings for that session (within one server tick), it is the local player.

Because the object-stat walk is delicate, this script ALSO reports the id
distribution so the weaker claim can be made from the distribution alone: in
sessions where only one ObjectId ever appears in PlayerProj=false reports, that
id behaves like "me", whatever its numeric value.

Sources (read-only): work/logs/events-*.jsonl
"""
from __future__ import annotations

import json
import os
import struct
import sys
from bisect import bisect_left
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parse_replay import varint_read  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
LOGS = os.path.join(ROOT, "work", "logs")
FILES = ["events-20261004-235440-1500.jsonl",
         "events-20261005-012701-2400.jsonl",
         "events-20261005-023129-2000.jsonl"]

DATA_SHORT = 1


def hp_stats_in_update(raw):
    """Best-effort: yield (objectId, hp) for StatsType.Hp short values.

    GmUpdate layout (work/report-handlers.md B.1): UpdateId, RemovedCount+ids,
    KilledCount+ids, ObjectStatsCount, then per object: ObjectId varint,
    NewObject bool (+ int16 Type if new), StatsCount byte, Stats..., then Dt.
    """
    out = []
    pos = 2
    try:
        _, pos = varint_read(raw, pos)                   # UpdateId
        n, pos = varint_read(raw, pos)                   # removed count
        for _ in range(n):
            _, pos = varint_read(raw, pos)
        n, pos = varint_read(raw, pos)                   # killed count
        for _ in range(n):
            _, pos = varint_read(raw, pos)
        n, pos = varint_read(raw, pos)                   # object stats count
        for _ in range(n):
            oid, pos = varint_read(raw, pos)
            newobj = raw[pos]
            pos += 1
            if newobj:
                pos += 2                                 # int16 type
            cnt = raw[pos]
            pos += 1
            hp = None
            for _ in range(cnt):
                sid = raw[pos]
                dt = raw[pos + 1]
                pos += 2
                if dt == DATA_SHORT:
                    val = struct.unpack_from("<h", raw, pos)[0]
                    pos += 2
                elif dt == 0:
                    pos += 1
                elif dt == 2:
                    pos += 4
                elif dt == 3:
                    pos += 8
                elif dt == 9:
                    pos += 4
                elif dt == 6:
                    _, pos = varint_read(raw, pos)
                    _, pos = varint_read(raw, pos)
                else:
                    return out                           # unsupported: give up
                if sid in (2, 80) and dt == DATA_SHORT:
                    hp = val
            if hp is not None:
                out.append((oid, hp))
    except (IndexError, Exception):
        return out
    return out


def main():
    print("=" * 78)
    print("Is the PlayerProj=false ObjectId the local player?")
    print("=" * 78)
    for name in FILES:
        path = os.path.join(LOGS, name)
        if not os.path.exists(path):
            continue
        reports = defaultdict(list)      # sess -> [(ms, oid)]
        hp_series = defaultdict(list)    # sess -> [(ms, hp)] from HealthUpdate
        updates = defaultdict(list)      # sess -> [(ms, [(oid, hp)])]
        for line in open(path, encoding="utf-8", errors="replace"):
            if '"ProjHit"' not in line and '"HealthUpdate"' not in line \
                    and '"Update"' not in line:
                continue
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            if e.get("kind") != "packet":
                continue
            sess, pkt = e.get("sess"), e.get("pkt")
            if pkt == "ProjHit":
                raw = bytes.fromhex(e["hex"])
                pos = 2
                _, pos = varint_read(raw, pos)
                _, pos = varint_read(raw, pos)
                oid, pos = varint_read(raw, pos)
                if pos < len(raw) and raw[pos] == 0:
                    reports[sess].append((e.get("ms"), oid))
            elif pkt == "HealthUpdate":
                d = e.get("data") or {}
                if d.get("health") is not None:
                    hp_series[sess].append((e.get("ms"), d["health"]))
            elif pkt == "Update" and e.get("hex"):
                try:
                    raw = bytes.fromhex(e["hex"])
                except ValueError:
                    continue
                pairs = hp_stats_in_update(raw)
                if pairs:
                    updates[sess].append((e.get("ms"), pairs))
        if not reports:
            continue
        print(f"\n--- {name}")
        for sess in sorted(reports):
            reps = reports[sess]
            ids = Counter(o for _, o in reps)
            hp = hp_series.get(sess, [])
            print(f"  {sess:16s} {len(reps):4d} 'I was hit' reports, "
                  f"ObjectId dist={dict(ids)}")
            if not hp:
                continue
            for oid in ids:
                # do the Update packets carry that object with an Hp stat?
                near = 0
                hits = 0
                for ms, pairs in updates.get(sess, []):
                    for o, v in pairs:
                        if o == oid:
                            near += 1
                            idx = bisect_left([h[0] for h in hp], ms)
                            for k in range(max(0, idx - 2), min(len(hp), idx + 2)):
                                if abs(hp[k][1] - v) <= 2:
                                    hits += 1
                                    break
                print(f"      ObjectId {oid}: appeared as an object with an "
                      f"Hp stat in {near} Update packets; of those, {hits} "
                      f"matched a HealthUpdate reading within 2 HP")
    print("\nReading: an ObjectId that (a) is the ONLY one ever seen in "
          "'I was hit' reports\nfor its session and (b) carries the same Hp "
          "values as the session's HealthUpdate\nreadings is the local player. "
          "If the match count is zero, the id is something else\nand the "
          "distribution argument alone is what remains.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
