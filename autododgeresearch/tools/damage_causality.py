#!/usr/bin/env python3
"""Does the server apply damage BECAUSE of the client's ProjHit claim, or on its
own schedule?

The question matters because it decides where a hook's leverage is:
  * if damage is gated on the client->server GmHit(id 102, PlayerProj=false),
    suppressing/delaying that packet prevents or postpones the damage;
  * if the server runs its own collision test, the client's claim is advisory and
    no inbound packet timing helps.

What is measurable from the captures is the *relation* between the two:

  A) For ProjHit(PlayerProj=false) reports -- the client asserting "I was hit" --
     how often does the very next HealthUpdate show an HP fall, and how large?
  B) The same for ProjHit(PlayerProj=true) -- our shot hitting an enemy, which
     should have NO causal relation to our HP.  This is the control: it fixes the
     baseline "the next reading happened to fall anyway" rate.
  C) The delay from a report to the first reading that shows a fall, and to the
     reading that follows it, to see whether the damage lands on the next server
     tick or the one after.

A large gap between (A) and (B) is strong evidence the server consumes the claim.
A gap of zero would mean the health stream is just independent of the reports.

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
LOGS = os.path.join(ROOT, "work", "logs")
FILES = ["events-20261004-235440-1500.jsonl",
         "events-20261005-012701-2400.jsonl",
         "events-20261005-023129-2000.jsonl"]

# Game_Slave#7 in the 012701 run is the one dense combat session: 434 ProjHit
# packets over ~90 s with both PlayerProj values present, plus 2043 readings.


def load(name):
    hits = defaultdict(list)
    health = defaultdict(list)
    for line in open(os.path.join(LOGS, name), encoding="utf-8",
                     errors="replace"):
        line = line.strip()
        if not line or ('"ProjHit"' not in line and '"HealthUpdate"' not in line):
            continue
        try:
            e = json.loads(line)
        except json.JSONDecodeError:
            continue
        if e.get("kind") != "packet":
            continue
        sess = e.get("sess")
        if e.get("pkt") == "ProjHit":
            raw = bytes.fromhex(e["hex"])
            pos = 2
            t, pos = varint_read(raw, pos)
            pid, pos = varint_read(raw, pos)
            oid, pos = varint_read(raw, pos)
            hits[sess].append((e.get("ms"), raw[pos] if pos < len(raw) else None))
        elif e.get("pkt") == "HealthUpdate":
            d = e.get("data") or {}
            if d.get("health") is None:
                continue
            health[sess].append((e.get("ms"), d.get("maxHealth", 0),
                                 d.get("health", 0)))
    return hits, health


def main():
    print("=" * 78)
    print("Is the server's damage gated on the client's hit claim?")
    print("=" * 78)
    agg = defaultdict(lambda: {"n": 0, "fall1": 0, "fall2": 0,
                               "d1": [], "d2": [], "pct1": [], "pct2": []})
    for name in FILES:
        if not os.path.exists(os.path.join(LOGS, name)):
            continue
        hits, health = load(name)
        for sess in sorted(hits):
            readings = health.get(sess, [])
            if len(readings) < 3:
                continue
            for ms, pp in hits[sess]:
                if pp is None:
                    continue
                after = [r for r in readings if r[0] > ms]
                before = [r for r in readings if r[0] <= ms]
                if len(after) < 2 or not before:
                    continue
                h0 = before[-1][2]
                a = agg[pp]
                a["n"] += 1
                # first reading after the report
                if after[0][2] < h0:
                    a["fall1"] += 1
                    a["d1"].append(after[0][0] - ms)
                    if before[-1][1]:
                        a["pct1"].append(
                            100.0 * (h0 - after[0][2]) / before[-1][1])
                # first reading strictly after the report that shows a fall
                fell = [r for r in after if r[2] < h0]
                if fell:
                    r = fell[0]
                    a["fall2"] += 1
                    a["d2"].append(r[0] - ms)
                    if before[-1][1]:
                        a["pct2"].append(
                            100.0 * (h0 - r[2]) / before[-1][1])
    for pp, label in ((False, "PlayerProj = false  (client reports I WAS HIT)"),
                      (True, "PlayerProj = true   (client reports I hit an enemy)")):
        a = agg[pp]
        n = a["n"]
        print(f"\n{label}")
        print(f"  reports evaluated                        : {n}")
        if not n:
            continue
        print(f"  the reading immediately after showed a fall: "
              f"{a['fall1']} = {100.0*a['fall1']/n:.0f}%")
        print(f"  any of the next readings showed a fall     : "
              f"{a['fall2']} = {100.0*a['fall2']/n:.0f}%")
        for key, lab in (("d1", "delay to the immediate next reading"),
                         ("d2", "delay to the first reading showing a fall")):
            v = sorted(a[key])
            if v:
                print(f"  {lab:42s}: n={len(v)} min={v[0]} "
                      f"p10={v[len(v)//10]} median={v[len(v)//2]} "
                      f"p90={v[int(len(v)*0.9)]} max={v[-1]} ms")
        for key, lab in (("pct1", "fall size / max (immediate reading)"),
                         ("pct2", "fall size / max (first falling reading)")):
            v = sorted(a[key])
            if v:
                print(f"  {lab:42s}: n={len(v)} median={v[len(v)//2]:.1f}% "
                      f"p90={v[int(len(v)*0.9)]:.1f}% max={v[-1]:.1f}%")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
