#!/usr/bin/env python3
"""The decisive measurement: does the client tell the server it was hit, and how
long after that report does the server's own health reading move?

Background established by field_order_solver.py:
  * The client->server `ProjHit` (GmHit, id 102) packet is
        Time(varint) -> ProjectileId(varint) -> ObjectId(varint) -> PlayerProj(bool)
    and PlayerProj is NOT always true: the captures contain both values.
    `work/decomp/.../Projectile.cs:1581` builds the packet with
        PlayerProj = PlayerOwned
    when the hit entity is `World.Player` (an enemy shot hitting us -> false),
    and `:1687` builds it with `PlayerProj = PlayerOwned` when the hit entity is
    an enemy (our shot -> true).  So PlayerProj=false IS a client->server
    "I was hit" report -- the damage-taken report the design brief believed did
    not exist.

This script quantifies it:
  1. counts PlayerProj true/false per session;
  2. for every ProjHit with PlayerProj == false, finds the first HealthUpdate
     reading strictly after it and reports (a) whether HP fell, (b) the delta,
     (c) the delay from the report to the reading;
  3. reports the same for true, as a control.

Sources (read-only): work/logs/events-{235440-1500,012701-2400,023129-2000}.jsonl
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


def load(name):
    """-> (per_session ProjHit list, per_session HealthUpdate list)."""
    hits = defaultdict(list)
    health = defaultdict(list)
    path = os.path.join(LOGS, name)
    for line in open(path, encoding="utf-8", errors="replace"):
        line = line.strip()
        if not line:
            continue
        if '"ProjHit"' not in line and '"HealthUpdate"' not in line:
            continue
        try:
            e = json.loads(line)
        except json.JSONDecodeError:
            continue
        if e.get("kind") != "packet":
            continue
        pkt = e.get("pkt")
        sess = e.get("sess")
        if pkt == "ProjHit":
            raw = bytes.fromhex(e["hex"])
            # unambiguously determined order: Time, ProjectileId, ObjectId, PlayerProj
            pos = 2
            t, pos = varint_read(raw, pos)
            pid, pos = varint_read(raw, pos)
            oid, pos = varint_read(raw, pos)
            pp = raw[pos] if pos < len(raw) else None
            hits[sess].append((e.get("ms"), t, pid, oid, pp, e.get("hex")))
        elif pkt == "HealthUpdate":
            d = e.get("data") or {}
            if d.get("health") is None:
                continue
            health[sess].append((e.get("ms"), d.get("maxHealth", 0),
                                 d.get("health", 0)))
    return hits, health


def main():
    grand = Counter()
    true_delays, false_delays = [], []
    false_with_drop = 0
    false_drop_pct = []
    examples = []
    print("=" * 78)
    print("PART 2b: ProjHit (GmHit id 102) as the client->server damage report")
    print("=" * 78)
    for name in FILES:
        path = os.path.join(LOGS, name)
        if not os.path.exists(path):
            continue
        hits, health = load(name)
        print(f"\n--- {name}")
        for sess in sorted(hits):
            hs = hits[sess]
            c = Counter(h[4] for h in hs)
            grand.update(c)
            print(f"  {sess:16s} ProjHit {len(hs):4d}  "
                  f"PlayerProj True={c.get(1, 0):4d} False={c.get(0, 0):4d} "
                  f"None={c.get(None, 0)}")
            readings = health.get(sess, [])
            if not readings:
                continue
            for ms, t, pid, oid, pp, hexs in hs:
                nxt = [r for r in readings if r[0] > ms]
                if not nxt:
                    continue
                r_ms, r_max, r_hp = nxt[0]
                prev = [r for r in readings if r[0] <= ms]
                prev_hp = prev[-1][2] if prev else None
                delay = r_ms - ms
                fell = prev_hp is not None and r_hp < prev_hp
                if pp == 0:
                    false_delays.append(delay)
                    if fell:
                        false_with_drop += 1
                        if r_max:
                            false_drop_pct.append(
                                100.0 * (prev_hp - r_hp) / r_max)
                    if len(examples) < 12:
                        examples.append((name, sess, ms, t, pid, oid, hexs,
                                         prev_hp, r_ms, r_hp, delay, fell))
                elif pp == 1:
                    true_delays.append(delay)
    print("\n" + "=" * 78)
    print("TOTALS")
    print("=" * 78)
    print(f"ProjHit packets                : {sum(grand.values())}")
    print(f"  PlayerProj = true  (our shot): {grand.get(1, 0)}")
    print(f"  PlayerProj = false (we were hit): {grand.get(0, 0)}")

    def stats(v, label):
        if not v:
            print(f"{label}: none")
            return
        s = sorted(v)
        print(f"{label} (n={len(s)}): min {s[0]}  p10 {s[len(s)//10]}  "
              f"median {s[len(s)//2]}  p90 {s[int(len(s)*0.9)]}  max {s[-1]} ms")

    stats(false_delays, "delay report -> next HP reading, PlayerProj=false")
    stats(true_delays, "delay report -> next HP reading, PlayerProj=true")
    if false_drop_pct:
        s = sorted(false_drop_pct)
        print(f"HP fall after a PlayerProj=false report: {false_with_drop} of "
              f"{len(false_delays)} reports had a fall by the next reading; "
              f"fall size as % of max: median {s[len(s)//2]:.1f}%, "
              f"p90 {s[int(len(s)*0.9)]:.1f}%, max {s[-1]:.1f}%")
    print("\nworked examples (PlayerProj=false = 'I was hit' report):")
    print("  session          report@ms  wire Time  projId   objId  payload"
          "                     hpBefore -> hpAtNextReading (@ms, +delay)")
    for (name, sess, ms, t, pid, oid, hexs, before, r_ms, r_hp, delay,
         fell) in examples:
        print(f"  {sess:16s} {ms:>9} {t:>9} {pid:>8} {oid:>7}  {hexs:30s} "
              f"{before} -> {r_hp} (@{r_ms}, +{delay} ms) "
              f"{'DROP' if fell else 'no drop'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
