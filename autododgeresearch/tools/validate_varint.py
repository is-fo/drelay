#!/usr/bin/env python3
"""Validate the varint decoder in parse_replay.py against the relay's OWN
decoded HealthUpdate values recorded in work/logs/events-*.jsonl.

If our independent decode of the same `hex` reproduces the relay's
maxHealth/health/shield/barrier for every sample, the decoder is pinned.
"""
import glob
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parse_replay import decode_health, iter_events  # noqa: E402

LOGS = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                    "..", "..", "work", "logs")

total = 0
ok = 0
bad = []
for path in sorted(glob.glob(os.path.join(LOGS, "events-*.jsonl"))):
    for ev in iter_events(path):
        if ev.get("pkt") != "HealthUpdate":
            continue
        d = ev.get("data") or {}
        if "health" not in d:
            continue
        total += 1
        got = decode_health(ev["hex"])
        want = [d.get("maxHealth"), d.get("health"), d.get("shield"), d.get("barrier")]
        if got == want:
            ok += 1
        elif len(bad) < 20:
            bad.append((ev["hex"], got, want))

print(f"samples compared : {total}")
print(f"exact match      : {ok}")
print(f"mismatch         : {total - ok}")
for h, g, w in bad:
    print(f"  MISMATCH {h}: decoded={g} relay={w}")
sys.exit(0 if (total and ok == total) else 1)
