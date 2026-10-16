#!/usr/bin/env python3
"""Round-trip time and server-tick measurements, re-derived cleanly.

Three independent clocks are available in the captures, and they answer
different questions:

  1. `CheckPing` (id 106, client->server, empty) -> `CheckPingAck` (id 107,
     server->client, empty).  Both are tiny and the server's answer is a direct
     reply, so the observed delta is close to a true application round trip.
  2. `Ping` (id 92, server->client) -> `Pong` (id 72, client->server).  The S->C
     ping carries a timestamp; the C->S pong follows within a millisecond, so
     this pair measures the RELAY's own observation skew, not the network.
  3. `GmUpdate` (id 1) arrival cadence on the server->client stream, which is the
     server tick rate as seen from the wire.  `GmUpdateAck.Time` is
     `LastClientTime`, i.e. the PREVIOUS tick (work/report-handlers.md:406).

Everything is measured at the relay, so every number includes the relay's own
observation point -- which is exactly the point a packet proxy occupies.

Source (read-only): work/logs/events-*.jsonl
"""
from __future__ import annotations

import json
import os
import statistics
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parse_replay import pct  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
LOGS = os.path.join(ROOT, "proxy", "work", "logs")

# The three real live sessions.  Everything else in work/logs is either an empty
# start-up file or a synthetic loopback test (sessions named Test#N).
REAL = ["events-20261004-235440-1500.jsonl",
        "events-20261005-012701-2400.jsonl",
        "events-20261005-023129-2000.jsonl"]


def is_real(sess):
    return sess and not sess.startswith("Test")


def rtts(req, resp, limit_ms=1500):
    """Greedy nearest-in-time pairing, each response consumed once."""
    r, s = sorted(req), sorted(resp)
    used = set()
    out = []
    for t in r:
        cand = [u for u in s if u >= t and u - t < limit_ms and u not in used]
        if cand:
            used.add(cand[0])
            out.append(cand[0] - t)
    return out


def stat_line(label, vals):
    if not vals:
        print(f"{label}: none")
        return
    s = sorted(vals)
    print(f"{label}  n={len(s)}")
    print(f"    min {s[0]}  p10 {pct(s,0.1):.0f}  p25 {pct(s,0.25):.0f}  "
          f"median {statistics.median(s):.0f}  p75 {pct(s,0.75):.0f}  "
          f"p90 {pct(s,0.9):.0f}  max {s[-1]}  mean "
          f"{statistics.fmean(s):.0f}")


def main():
    per_file = {}
    for name in REAL:
        path = os.path.join(LOGS, name)
        if not os.path.exists(path):
            continue
        d = {
            "cp": defaultdict(list), "cpa": defaultdict(list),
            "ping": defaultdict(list), "pong": defaultdict(list),
            "update": defaultdict(list), "updack": defaultdict(list),
            "health": defaultdict(list), "esc": defaultdict(list),
            "ack": defaultdict(list),
        }
        for line in open(path, encoding="utf-8", errors="replace"):
            line = line.strip()
            if not line:
                continue
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            if e.get("kind") != "packet":
                continue
            sess, pkt, dr = e.get("sess"), e.get("pkt"), e.get("dir")
            if not is_real(sess):
                continue
            ms = e.get("ms")
            if pkt == "CheckPing":
                d["cp"][sess].append(ms)
            elif pkt == "CheckPingAck":
                d["cpa"][sess].append(ms)
            elif pkt == "Ping" and dr == "S->C":
                d["ping"][sess].append(ms)
            elif pkt == "Pong" and dr == "C->S":
                d["pong"][sess].append(ms)
            elif pkt == "Update" and dr == "S->C":
                d["update"][sess].append(ms)
            elif pkt == "UpdateAck":
                d["updack"][sess].append(ms)
            elif pkt == "HealthUpdate":
                dd = e.get("data") or {}
                if dd.get("health") is not None:
                    d["health"][sess].append((ms, dd["health"]))
            elif pkt == "Escape" and dr == "C->S":
                d["esc"][sess].append(ms)
            elif pkt == "EscapeAck":
                d["ack"][sess].append(ms)
        per_file[name] = d

    print("=" * 78)
    print("1. CheckPing -> CheckPingAck  (empty request, direct reply)")
    print("=" * 78)
    for name, d in per_file.items():
        allv = []
        print(f"\n--- {name}")
        for sess in sorted(d["cp"]):
            v = rtts(d["cp"][sess], d["cpa"][sess])
            if v:
                print(f"  {sess:16s}")
                stat_line("    RTT", v)
                allv += v
        stat_line("  ALL SESSIONS in this file", allv)

    print()
    print("=" * 78)
    print("2. Ping -> Pong  (relay observation skew, NOT network RTT)")
    print("=" * 78)
    for name, d in per_file.items():
        allv = []
        for sess in sorted(d["ping"]):
            allv += rtts(d["ping"][sess], d["pong"][sess], 100)
        stat_line(f"  {name}", allv)

    print()
    print("=" * 78)
    print("3. GmUpdate arrival cadence = the server tick rate on the wire")
    print("=" * 78)
    for name, d in per_file.items():
        allv = []
        print(f"\n--- {name}")
        for sess in sorted(d["update"]):
            ts = sorted(d["update"][sess])
            if len(ts) < 10:
                continue
            gaps = [b - a for a, b in zip(ts, ts[1:])]
            small = [g for g in gaps if g < 400]
            print(f"  {sess:16s} n={len(ts):6d}")
            stat_line("    tick gap (all <400 ms)", small)
            allv += small
        if allv:
            stat_line("  ALL", allv)

    print()
    print("=" * 78)
    print("4. Escape -> EscapeAck as seen entirely at the relay")
    print("=" * 78)
    for name, d in per_file.items():
        for sess in sorted(set(d["esc"]) | set(d["ack"])):
            es, ks = sorted(d["esc"].get(sess, [])), sorted(d["ack"].get(sess, []))
            if not es and not ks:
                continue
            print(f"  {name} {sess}: Escape {len(es)}, EscapeAck {len(ks)}")
            for t in ks:
                prior = [e for e in es if e <= t]
                if prior:
                    print(f"     ack @{t} <- Escape @{prior[-1]}  "
                          f"delta {t-prior[-1]} ms")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
