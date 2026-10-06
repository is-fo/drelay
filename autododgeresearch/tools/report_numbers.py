#!/usr/bin/env python3
"""One-shot consolidated numbers for autododgeresearch/TIMING.md.

Prints every figure the report quotes, each labelled with the file it came from,
so the report's numbers can be re-derived by re-running this file.

Usage:  python autododgeresearch/tools/report_numbers.py
"""
from __future__ import annotations

import glob
import json
import os
import statistics
import sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parse_replay import iter_events, parse_replay, pct, varint_read  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
WORK = os.path.join(ROOT, "work")
LOGS = os.path.join(WORK, "logs")

REAL = [("235440", "events-20261004-235440-1500.jsonl"),
        ("012701", "events-20261005-012701-2400.jsonl"),
        ("023129", "events-20261005-023129-2000.jsonl")]


def line(label, values, unit="ms"):
    if not values:
        print(f"{label:<58} none")
        return
    s = sorted(values)
    print(f"{label:<58} n={len(s):<6} min={s[0]:<8.1f} p10={pct(s,0.1):<8.1f} "
          f"p25={pct(s,0.25):<8.1f} med={statistics.median(s):<8.1f} "
          f"p75={pct(s,0.75):<8.1f} p90={pct(s,0.9):<8.1f} max={s[-1]:<9.1f} "
          f"{unit}")


def load(name):
    health, worlds, hits, cp, cpa, ping, pong, upd, esc, ack = (
        defaultdict(list), defaultdict(list), defaultdict(list),
        defaultdict(list), defaultdict(list), defaultdict(list),
        defaultdict(list), defaultdict(list), defaultdict(list),
        defaultdict(list))
    for e in iter_events(os.path.join(LOGS, name)):
        if e.get("kind") != "packet":
            continue
        sess, pkt, dr, ms = e.get("sess"), e.get("pkt"), e.get("dir"), e.get("ms")
        if sess and sess.startswith("Test"):
            continue
        if pkt == "HealthUpdate":
            d = e.get("data") or {}
            if d.get("health") is not None and d.get("maxHealth") is not None:
                health[sess].append((ms, d["maxHealth"], d["health"]))
        elif pkt == "MapInfo":
            worlds[sess].append(ms)
        elif pkt == "ProjHit":
            raw = bytes.fromhex(e["hex"])
            pos = 2
            _, pos = varint_read(raw, pos)
            _, pos = varint_read(raw, pos)
            _, pos = varint_read(raw, pos)
            hits[sess].append((ms, raw[pos] if pos < len(raw) else -1))
        elif pkt == "CheckPing":
            cp[sess].append(ms)
        elif pkt == "CheckPingAck":
            cpa[sess].append(ms)
        elif pkt == "Ping" and dr == "S->C":
            ping[sess].append(ms)
        elif pkt == "Pong" and dr == "C->S":
            pong[sess].append(ms)
        elif pkt == "Update" and dr == "S->C":
            upd[sess].append(ms)
        elif pkt == "Escape" and dr == "C->S":
            esc[sess].append(ms)
        elif pkt == "EscapeAck":
            ack[sess].append(ms)
    return health, worlds, hits, cp, cpa, ping, pong, upd, esc, ack


def pair(req, resp, limit=1500):
    r, s = sorted(req), sorted(resp)
    used, out = set(), []
    for t in r:
        c = [u for u in s if t <= u < t + limit and u not in used]
        if c:
            used.add(c[0])
            out.append(c[0] - t)
    return out


def main():
    print("=" * 78)
    print("1. GmHealthUpdate (id 70) inter-arrival, within one world")
    print("=" * 78)
    combined = []
    for tag, name in REAL:
        health, worlds, *_ = load(name)
        allg = []
        for sess in sorted(health):
            rows = health[sess]
            if len(rows) < 3:
                continue
            ts = [r[0] for r in rows]
            wm = sorted(worlds.get(sess, []))
            g = [b - a for a, b in zip(ts, ts[1:])
                 if not any(a < w <= b for w in wm)]
            if g:
                line(f"  {tag} {sess}", g)
            allg += g
        line(f"  === {tag} ALL SESSIONS", allg)
        combined += allg
        print()
    line("=== ALL THREE RUNS, ALL SESSIONS", combined)

    print()
    print("  bucket histogram (all three runs, within one world):")
    b = Counter()
    for g in combined:
        if g <= 0:
            b["0"] += 1
        elif g <= 10:
            b["1-10"] += 1
        elif g <= 45:
            b["11-45"] += 1
        elif g <= 55:
            b["46-55"] += 1
        elif g <= 70:
            b["56-70"] += 1
        elif g <= 95:
            b["71-95"] += 1
        elif g <= 105:
            b["96-105"] += 1
        elif g <= 150:
            b["106-150"] += 1
        elif g <= 210:
            b["151-210"] += 1
        elif g <= 400:
            b["211-400"] += 1
        else:
            b[">400"] += 1
    tot = sum(b.values()) or 1
    for k in ["0", "1-10", "11-45", "46-55", "56-70", "71-95", "96-105",
              "106-150", "151-210", "211-400", ">400"]:
        if b[k]:
            print(f"    {k:>8} ms : {b[k]:6d}  {100.0*b[k]/tot:5.1f}%")
    print(f"    total       : {sum(b.values())}")

    print()
    print("=" * 78)
    print("2. Biggest single-reading HP drops and the delayed reflection")
    print("=" * 78)
    tops = []
    for tag, name in REAL:
        health, *_ = load(name)
        for sess in sorted(health):
            rows = health[sess]
            if len(rows) < 2:
                continue
            mx = max(r[1] for r in rows)
            drops = [(rows[i - 1], rows[i]) for i in range(1, len(rows))
                     if rows[i][2] < rows[i - 1][2]]
            if not drops:
                continue
            a, c = max(drops, key=lambda d: d[0][2] - d[1][2])
            tops.append((100.0 * (a[2] - c[2]) / mx, tag, sess, a[0], a[2],
                         c[0], c[2], c[0] - a[0], len(drops)))
    print(f"{'% of max':>9} {'run':>7} {'session':<16} "
          f"{'from':>9} {'hp':>5} {'to':>9} {'hp':>5} {'gap':>7} drops")
    for p, tag, sess, t0, h0, t1, h1, gap, nd in sorted(tops, reverse=True):
        print(f"{p:9.2f} {tag:>7} {sess:<16} {t0:>9} {h0:>5} {t1:>9} {h1:>5} "
              f"{gap:>7} {nd}")
    pv = sorted(t[0] for t in tops)
    line("drop sizes as % of max (one entry per session)", pv, "%")

    print()
    print("=" * 78)
    print("3. ProjHit (GmHit id 102): the client's own hit reports")
    print("=" * 78)
    tot_t = tot_f = 0
    d_false = []
    d_true = []
    for tag, name in REAL:
        health, worlds, hits, *_ = load(name)
        for sess in sorted(hits):
            raw = hits[sess]
            f = [x for x in raw if x[1] == 0]
            t = [x for x in raw if x[1] == 1]
            tot_t += len(t)
            tot_f += len(f)
            if f or t:
                print(f"  {tag} {sess:<16} PlayerProj=true {len(t):4d}   "
                      f"PlayerProj=false {len(f):4d}")
            rows = health.get(sess, [])
            for ms, pp in raw:
                nxt = [r for r in rows if r[0] > ms]
                if not nxt:
                    continue
                (d_false if pp == 0 else d_true).append(nxt[0][0] - ms)
    print(f"\n  total ProjHit captured: {tot_t + tot_f}   "
          f"PlayerProj=true {tot_t}   PlayerProj=false {tot_f}")
    line("  report -> next HealthUpdate, PlayerProj=false", d_false)
    line("  report -> next HealthUpdate, PlayerProj=true ", d_true)

    print()
    print("=" * 78)
    print("4. Round-trip time")
    print("=" * 78)
    cluster_fast, cluster_slow = [], []
    for tag, name in REAL:
        *_, cp, cpa, ping, pong, upd, esc, ack = load(name)
        v = []
        for sess in sorted(cp):
            v += pair(cp[sess], cpa[sess])
        for sess in sorted(cp):
            w = pair(cp[sess], cpa[sess])
            if w:
                print(f"  {tag} {sess:<16}", end=" ")
                line("", w)
        line(f"  === {tag} ALL", v)
        for sess in sorted(cp):
            w = pair(cp[sess], cpa[sess])
            (cluster_fast if sess.startswith("Game_Slave") else
             cluster_slow).extend(w)
        pv = []
        for sess in sorted(ping):
            pv += pair(ping[sess], pong[sess], 100)
        line(f"  {tag} Ping->Pong (relay skew, not RTT)", pv)
    line("=== CheckPing->CheckPingAck on Game_Slave#NN", cluster_fast)
    line("=== CheckPing->CheckPingAck on Game#NN", cluster_slow)

    print()
    print("=" * 78)
    print("5. Server tick (GmUpdate arrival cadence) and EscapeAck latency")
    print("=" * 78)
    for tag, name in REAL:
        *_, ping, pong, upd, esc, ack = load(name)
        g = []
        for sess in sorted(upd):
            ts = sorted(upd[sess])
            g += [b - a for a, b in zip(ts, ts[1:]) if 0 < b - a < 400]
        line(f"  {tag} GmUpdate tick gap", g)
        d = []
        for sess in sorted(set(esc) | set(ack)):
            es, ks = sorted(esc.get(sess, [])), sorted(ack.get(sess, []))
            for t in ks:
                prior = [e for e in es if e <= t]
                if prior:
                    d.append(t - prior[-1])
                    print(f"  {tag} {sess:<16} Escape @{prior[-1]} -> "
                          f"EscapeAck @{t}  = {t-prior[-1]} ms")
        line(f"  {tag} Escape -> EscapeAck (relay-observed)", d)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
