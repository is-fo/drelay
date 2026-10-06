#!/usr/bin/env python3
"""The counter-test: are there HP falls with NO preceding client hit claim?

If every single HP fall is preceded within a server tick by a
`ProjHit`(PlayerProj=false), the server plausibly consumes the client's claim and
a hook that suppresses that packet has leverage.

If some falls occur with no such claim -- or if falls occur in sessions where the
client sent no PlayerProj=false packet at all (AoE, beams, contact, DoT) -- the
server resolves damage on its own and no packet timing removes it.

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

WINDOW_MS = 400     # how far back to look for a claim; one server tick is 200 ms


def load(name):
    hits = defaultdict(list)
    health = defaultdict(list)
    aoe = defaultdict(list)
    for line in open(os.path.join(LOGS, name), encoding="utf-8",
                     errors="replace"):
        line = line.strip()
        if not line:
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
            _, pos = varint_read(raw, pos)
            hits[sess].append((e.get("ms"), raw[pos] if pos < len(raw) else None))
        elif pkt == "HealthUpdate":
            d = e.get("data") or {}
            if d.get("health") is None:
                continue
            health[sess].append((e.get("ms"), d.get("maxHealth", 0),
                                 d.get("health", 0)))
        elif pkt in ("Aoes", "Aoes2"):
            aoe[sess].append(e.get("ms"))
    return hits, health, aoe


def main():
    print("=" * 78)
    print("Counter-test: HP falls with no preceding client hit claim")
    print("=" * 78)
    overall = Counter()
    for name in FILES:
        if not os.path.exists(os.path.join(LOGS, name)):
            continue
        hits, health, aoe = load(name)
        print(f"\n--- {name}")
        for sess in sorted(health):
            readings = health[sess]
            if len(readings) < 3:
                continue
            claims = sorted(ms for ms, pp in hits.get(sess, []) if pp == 0)
            falls = []
            for i in range(1, len(readings)):
                a, b = readings[i - 1], readings[i]
                if b[2] < a[2]:
                    falls.append((b[0], a[2] - b[2], a[1]))
            if not falls:
                continue
            no_claim = 0
            no_claim_after_first = 0
            for t, drop, mx in falls:
                near = [c for c in claims if t - WINDOW_MS <= c <= t]
                if not near:
                    no_claim += 1
                    if claims:          # a claim existed earlier in the session
                        no_claim_after_first += 1
            overall["falls"] += len(falls)
            overall["no_claim"] += no_claim
            overall["no_claim_late"] += no_claim_after_first
            print(f"  {sess:16s} falls={len(falls):5d}  "
                  f"PlayerProj=false claims={len(claims):4d}  "
                  f"falls with no claim within {WINDOW_MS} ms={no_claim:5d} "
                  f"({100.0*no_claim/len(falls):.0f}%)")
    print("\n" + "=" * 78)
    print(f"all sessions: {overall['falls']} HP falls, "
          f"{overall['no_claim']} with no PlayerProj=false claim within "
          f"{WINDOW_MS} ms "
          f"({100.0*overall['no_claim']/max(overall['falls'],1):.0f}%)")
    print("=" * 78)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
