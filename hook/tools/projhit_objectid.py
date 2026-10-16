#!/usr/bin/env python3
"""Who is the ObjectId in a PlayerProj=false hit report?

Two readings of the same packet are possible:
  (a) the client claiming "my enemy projectile hit me", in which case ObjectId
      would be the local player's own object/character id, or a small constant;
  (b) the client reporting a *different* object (an ally or a pet) being hit by a
      projectile it does not own, in which case ObjectId is a real game object id.

The captures can separate them: the relay logs `GmMapInfo` (which carries the
local PlayerId) and the object ids the server addresses.  This script prints the
distribution of ObjectId per session alongside the ids that appear elsewhere for
that session, so the ObjectId can be identified rather than assumed.

Source (read-only): work/logs/events-*.jsonl
"""
from __future__ import annotations

import json
import os
import sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parse_replay import varint_read  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
LOGS = os.path.join(ROOT, "proxy", "work", "logs")
FILES = ["events-20261004-235440-1500.jsonl",
         "events-20261005-012701-2400.jsonl",
         "events-20261005-023129-2000.jsonl"]


def main():
    print("=" * 78)
    print("ObjectId distribution in ProjHit, split by PlayerProj")
    print("=" * 78)
    for name in FILES:
        path = os.path.join(LOGS, name)
        if not os.path.exists(path):
            continue
        hits = defaultdict(list)
        for line in open(path, encoding="utf-8", errors="replace"):
            if '"ProjHit"' not in line:
                continue
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            if e.get("pkt") != "ProjHit":
                continue
            raw = bytes.fromhex(e["hex"])
            pos = 2
            t, pos = varint_read(raw, pos)
            pid, pos = varint_read(raw, pos)
            oid, pos = varint_read(raw, pos)
            pp = raw[pos] if pos < len(raw) else None
            hits[e.get("sess")].append((pp, pid, oid, t))
        print(f"\n--- {name}")
        for sess in sorted(hits):
            rows = hits[sess]
            print(f"  {sess}")
            for pp in (0, 1):
                sub = [r for r in rows if r[0] == pp]
                if not sub:
                    continue
                obj = Counter(r[2] for r in sub)
                prj = Counter(r[1] for r in sub)
                print(f"    PlayerProj={pp!s:5s} n={len(sub):4d}  "
                      f"ObjectId distinct={len(obj)} "
                      f"most common={obj.most_common(6)}")
                print(f"        ProjectileId distinct={len(prj)} "
                      f"min={min(prj)} max={max(prj)} "
                      f"most common={prj.most_common(5)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
