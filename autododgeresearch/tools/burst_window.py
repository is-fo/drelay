#!/usr/bin/env python3
"""Bursts: how much HP can leave the bar between decisions?

Two different quantities matter, and only the first was measured initially:

  * the drop between two CONSECUTIVE readings -- what a rule sees if it acts on
    every reading;
  * the total drop inside a short WINDOW that contains several readings -- what a
    rule sees if the readings arrive back to back inside one server tick, or if it
    is rate-limited (the relay's default `minIntervalMillis = 1200`), or if it
    cannot act on each one.

`work/logs/nexus-20261005-023129-2000.jsonl` shows the second kind: a chain of
`HealthUpdate` readings all stamped inside the same 5 ms, ending at hp=0.

Reports, per session: the largest `before - min(after)` for a lookahead window of
`W` ms; how many readings that window contained; and whether hp hit 0.

Sources (read-only): work/logs/events-*.jsonl
"""
from __future__ import annotations

import json
import os
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
LOGS = os.path.join(ROOT, "work", "logs")
FILES = ["events-20261004-235440-1500.jsonl",
         "events-20261005-012701-2400.jsonl",
         "events-20261005-023129-2000.jsonl"]


def load(name):
    per = defaultdict(list)
    for line in open(os.path.join(LOGS, name), encoding="utf-8",
                     errors="replace"):
        if '"HealthUpdate"' not in line:
            continue
        try:
            e = json.loads(line)
        except json.JSONDecodeError:
            continue
        d = e.get("data") or {}
        if d.get("health") is None or d.get("maxHealth") is None:
            continue
        if d["maxHealth"] == 9999:          # sentinel: max unknown, skip
            continue
        per[e.get("sess")].append((e.get("ms"), d["maxHealth"], d["health"]))
    for s in per:
        per[s].sort()
    return per


def main():
    for W in (0, 50, 100, 200):
        print("=" * 78)
        print(f"largest drop over a {W} ms lookahead window"
              + (" (== consecutive readings)" if W == 0 else ""))
        print("=" * 78)
        best_all = []
        for name in FILES:
            per = load(name)
            for sess in sorted(per):
                rows = per[sess]
                if len(rows) < 2:
                    continue
                best = None
                for i, (t, mx, hp) in enumerate(rows):
                    lo = hp
                    n = 1
                    j = i + 1
                    while j < len(rows) and rows[j][0] - t <= W:
                        lo = min(lo, rows[j][2])
                        n += 1
                        j += 1
                    if n > 1 and hp - lo > 0:
                        cand = (hp - lo, hp, lo, mx, t, rows[j - 1][0], n)
                        if best is None or cand[0] > best[0]:
                            best = cand
                if best:
                    d, h0, h1, mx, t0, t1, n = best
                    flag = "  <-- ends at 0" if h1 == 0 else ""
                    print(f"  {name[:14]} {sess:<16} {d:5d} HP "
                          f"= {100.0*d/mx:5.1f}% of {mx}   {h0} -> {h1} "
                          f"over {t1-t0} ms in {n} readings{flag}")
                    best_all.append((100.0 * d / mx, name, sess, h0, h1,
                                     t1 - t0, n))
        if best_all:
            p = max(best_all)
            print(f"  WORST: {p[0]:.1f}%  {p[2]} in {p[1]}  "
                  f"({p[3]} -> {p[4]} over {p[5]} ms, {p[6]} readings)")
        print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
