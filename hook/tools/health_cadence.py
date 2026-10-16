#!/usr/bin/env python3
"""GmHealthUpdate cadence + honesty, from the recorded captures in work/.

Sources (read-only):
  work/replay.txt                     raw per-packet replay of a live session
  work/logs/events-*.jsonl            structured relay event logs
  work/logs/relay-*.log, relay.out.log  human-readable relay transcript
  work/live-digest.jsonl              bulk GmUpdate digest

Everything printed here is measured from those files; nothing is assumed.

Usage:  python hook/tools/health_cadence.py [--verbose]
"""
from __future__ import annotations

import glob
import json
import os
import re
import statistics
import sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parse_replay import decode_health, iter_events, parse_replay, pct  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
WORK = os.path.join(ROOT, "proxy", "work")
VERBOSE = "--verbose" in sys.argv


def dist(label, gaps, unit="ms"):
    if not gaps:
        print(f"{label}: no samples")
        return
    s = sorted(gaps)
    print(f"{label}  (n={len(s)})")
    print(f"    min    {s[0]:.1f} {unit}")
    print(f"    p10    {pct(s, 0.10):.1f} {unit}")
    print(f"    p25    {pct(s, 0.25):.1f} {unit}")
    print(f"    median {statistics.median(s):.1f} {unit}")
    print(f"    p75    {pct(s, 0.75):.1f} {unit}")
    print(f"    p90    {pct(s, 0.90):.1f} {unit}")
    print(f"    p99    {pct(s, 0.99):.1f} {unit}")
    print(f"    max    {s[-1]:.1f} {unit}")
    print(f"    mean   {statistics.fmean(s):.1f} {unit}")
    print(f"    stdev  {statistics.pstdev(s):.1f} {unit}")


# ---------------------------------------------------------------- replay.txt
def analyse_replay():
    path = os.path.join(WORK, "replay.txt")
    print("=" * 78)
    print(f"SOURCE A: {os.path.relpath(path, ROOT)}")
    print("=" * 78)
    kinds = Counter()
    sessions = Counter()
    per_session = defaultdict(list)   # sess -> [(wall, hp)]
    world_marks = defaultdict(list)
    missing = 0
    for kind, sess, wall, payload in parse_replay(path):
        kinds[kind] += 1
        sessions[sess] += 1
        if kind == "H":
            # make_replay.py writes `H <session> <ms> <health>`; the value is already decoded
            # by the relay, so there is no payload to re-decode here.
            if isinstance(payload, int):
                per_session[sess].append((wall, payload))
            else:
                missing += 1
        elif kind == "W":
            world_marks[sess].append(wall)
    print(f"record kinds: {dict(kinds)}")
    print(f"sessions: {dict(sessions)}")
    if missing:
        print(f"HealthUpdate records with no numeric health value: {missing}")
    firsts = []
    for sess in sorted(per_session):
        firsts.append(f"{sess}:{per_session[sess][0][1]}")
    print(f"sanity: first health reading per session = {', '.join(firsts[:12])}")

    grand_gaps = []
    combined_drops = []
    for sess in sorted(per_session):
        rows = per_session[sess]
        if len(rows) < 3:
            continue
        walls = [r[0] for r in rows]
        wm = sorted(world_marks.get(sess, []))
        gaps, in_world = [], []
        for a, b in zip(walls, walls[1:]):
            gaps.append(b - a)
            if not any(a < w <= b for w in wm):
                in_world.append(b - a)
        if not in_world:
            continue
        print(f"\n-- session {sess}: {len(rows)} readings "
              f"{walls[0]}..{walls[-1]} ({walls[-1]-walls[0]} ms span), "
              f"{len(wm)} world marks")
        dist("   inter-arrival (all, incl. world changes)", gaps)
        dist("   inter-arrival (within one world)", in_world)
        grand_gaps.extend(in_world)

        mx = max(max(r[1] for r in rows), 1)
        drops = [(rows[i - 1], rows[i], rows[i - 1][1] - rows[i][1])
                 for i in range(1, len(rows)) if rows[i][1] < rows[i - 1][1]]
        if drops:
            biggest = max(drops, key=lambda d: d[2])
            a, b, delta = biggest
            print(f"   drops: {len(drops)}; largest single-reading drop "
                  f"{delta} HP = {100.0*delta/mx:.2f}% of max {mx}")
            print(f"     from reading @{a[0]} ms hp={a[1]} "
                  f"-> @{b[0]} ms hp={b[1]} (gap {b[0]-a[0]} ms)")
            combined_drops.extend(
                (100.0 * d[2] / mx, sess, d[0][0], d[0][1], d[1][0], d[1][1])
                for d in drops)
        zeros = [r for r in rows if r[1] <= 0]
        print(f"   readings at hp<=0: {len(zeros)}")
    if grand_gaps:
        dist("\nALL SESSIONS COMBINED, within-world inter-arrival", grand_gaps)
    if combined_drops:
        pcts = sorted(d[0] for d in combined_drops)
        dist("DROP SIZES as % of max HP (all sessions, all drops)", pcts, unit="%")
        print("\n  10 largest single-reading drops anywhere in replay.txt:")
        for p, sess, t0, h0, t1, h1 in sorted(combined_drops, reverse=True)[:10]:
            print(f"    {p:6.2f}%  {sess:14s} {t0} hp={h0} -> {t1} hp={h1} "
                  f"(gap {t1-t0} ms)")
    return per_session, grand_gaps, world_marks


# ------------------------------------------------------- events-*.jsonl
def analyse_events():
    print()
    print("=" * 78)
    print("SOURCE B: work/logs/events-*.jsonl  (relay-recorded sessions)")
    print("=" * 78)
    for path in sorted(glob.glob(os.path.join(WORK, "logs", "events-*.jsonl"))):
        name = os.path.basename(path)
        by_sess = defaultdict(list)
        worlds = defaultdict(list)
        injections = defaultdict(list)
        acks = defaultdict(list)
        pings = defaultdict(list)
        pongs = defaultdict(list)
        cps = defaultdict(list)
        cpa = defaultdict(list)
        n_health = 0
        for ev in iter_events(path):
            if ev.get("kind") == "packet":
                pkt = ev.get("pkt")
                if pkt == "HealthUpdate":
                    d = ev.get("data") or {}
                    if "health" in d and "maxHealth" in d:
                        by_sess[ev.get("sess")].append(
                            (ev.get("ms"), d["maxHealth"], d["health"],
                             d.get("shield", 0), d.get("barrier", 0), ev.get("hex")))
                        n_health += 1
                elif pkt == "MapInfo":
                    worlds[ev.get("sess")].append(ev.get("ms"))
                elif pkt == "Ping" and ev.get("dir") == "S->C":
                    pings[ev.get("sess")].append(ev.get("ms"))
                elif pkt == "Pong" and ev.get("dir") == "C->S":
                    pongs[ev.get("sess")].append(ev.get("ms"))
                elif pkt == "CheckPing":
                    cps[ev.get("sess")].append(ev.get("ms"))
                elif pkt == "CheckPingAck":
                    cpa[ev.get("sess")].append(ev.get("ms"))
            elif ev.get("kind") == "inject":
                injections[ev.get("sess")].append((ev.get("ms"), ev.get("pkt"),
                                                   ev.get("note")))
            elif ev.get("kind") in ("nexus", "world") and "latencyMs" in (ev.get("data") or {}):
                acks[ev.get("sess")].append((ev.get("ms"), ev.get("data")["latencyMs"],
                                             ev.get("note")))
        print(f"\n--- {name} : {n_health} HealthUpdate readings, "
              f"{sum(len(v) for v in injections.values())} injections")

        all_gaps = []
        for sess in sorted(by_sess):
            rows = by_sess[sess]
            if len(rows) < 3:
                continue
            ms = [r[0] for r in rows]
            gaps = [b - a for a, b in zip(ms, ms[1:])]
            wm = sorted(worlds.get(sess, []))
            in_world = [b - a for a, b in zip(ms, ms[1:])
                        if not any(a < w <= b for w in wm)]
            print(f"\n  session {sess}: {len(rows)} readings, "
                  f"{ms[0]}..{ms[-1]} ms, {len(wm)} world entries")
            dist("     inter-arrival (all)", gaps)
            if len(in_world) != len(gaps):
                dist("     inter-arrival (within a world)", in_world)
            all_gaps.extend(in_world)
            mx = max(max(r[1] for r in rows), 1)
            drops = [(rows[i - 1], rows[i], rows[i - 1][2] - rows[i][2])
                     for i in range(1, len(rows)) if rows[i][2] < rows[i - 1][2]]
            if drops:
                biggest = max(drops, key=lambda d: d[2])
                a, b, delta = biggest
                print(f"     drops: {len(drops)}; largest single-reading drop "
                      f"{delta} HP = {100.0*delta/mx:.1f}% of max {mx}")
                print(f"       from {a[0]} ms hp={a[2]} -> {b[0]} ms hp={b[2]} "
                      f"(gap {b[0]-a[0]} ms)")
            # kill check: any pair whose drop >= previous reading's hp
            fatal = [(a, b) for a, b in zip(rows, rows[1:])
                     if a[2] > 0 and b[2] <= 0 and a[2] - b[2] >= a[2]]
            zero_reads = [r for r in rows if r[2] <= 0]
            print(f"     readings at hp<=0 (death): {len(zero_reads)}"
                  + (f" first at {zero_reads[0][0]} ms" if zero_reads else ""))
        if all_gaps:
            dist("\n  ALL SESSIONS COMBINED, within-world inter-arrival", all_gaps)
        for sess in sorted(injections):
            for ms, pkt, note in injections[sess]:
                print(f"  INJECT {sess} @{ms} ms  {pkt}  {note}")
        for sess in sorted(acks):
            for ms, lat, note in acks[sess]:
                print(f"  ACK    {sess} @{ms} ms  latencyMs={lat}  {note}")
        # ping/pong and checkping RTT
        for label, req, resp, keep in (("CheckPing->CheckPingAck", cps, cpa, True),
                                       ("Ping->Pong", pings, pongs, True)):
            all_rtt = []
            for sess in sorted(req):
                r, s = sorted(req[sess]), sorted(resp[sess])
                used = set()
                rtts = []
                for t in r:
                    cand = [u for u in s if u >= t and u - t < 1000 and u not in used]
                    if cand:
                        u = cand[0]
                        used.add(u)
                        rtts.append(u - t)
                if rtts:
                    print(f"  {label} session {sess}: n={len(rtts)}")
                    dist(f"     RTT ({label})", rtts)
                    all_rtt.extend(rtts)
            if all_rtt:
                dist(f"  {label} RTT, ALL SESSIONS", all_rtt)
    return


# --------------------------------------------------- relay-*.log / relay.out.log
STAMP = re.compile(r"\[(?P<sess>[^\]]+?)\s+(?P<dir>S->C|C->S)\]\s+(?P<t>\d\d:\d\d:\d\d\.\d+)")
PKT = re.compile(r"id=0x(?P<id>[0-9A-F]{2})\s+(?P<name>[A-Za-z0-9]+)")
HEXRE = re.compile(r"\s(?P<hex>[0-9A-F]{6,})\s*$")


def secs(t):
    hh, mm, rest = t.split(":")
    return int(hh) * 3600 + int(mm) * 60 + float(rest)


def analyse_relay_logs():
    print()
    print("=" * 78)
    print("SOURCE C: work/logs/relay-*.log  (relay transcript, HH:MM:SS.mmm)")
    print("=" * 78)
    for path in sorted(glob.glob(os.path.join(WORK, "logs", "relay-*.log"))):
        name = os.path.basename(path)
        health = defaultdict(list)
        escapes = defaultdict(list)     # C->S id 0x42
        ack = defaultdict(list)         # S->C id 0x9E
        forced = defaultdict(list)      # S->C id 0xB8
        world = defaultdict(list)
        for line in open(path, encoding="utf-8", errors="replace"):
            m, p = STAMP.search(line), PKT.search(line)
            if not (m and p):
                continue
            sess, t = m.group("sess"), secs(m.group("t"))
            pid = p.group("id")
            if pid == "46":
                h = HEXRE.search(line.rstrip())
                if h:
                    try:
                        vals = decode_health(h.group("hex"))
                    except Exception:
                        continue
                    health[sess].append((t, vals))
            elif pid == "42":
                escapes[sess].append(t)
            elif pid == "9E":
                ack[sess].append((t, line.strip().split()[-1]))
            elif pid == "B8":
                forced[sess].append(t)
            elif pid == "24":
                world[sess].append(t)
        if not health:
            continue
        print(f"\n--- {name}")
        allg = []
        for sess in sorted(health):
            rows = health[sess]
            ts = [r[0] for r in rows]
            gaps = [b - a for a, b in zip(ts, ts[1:])]
            inw = [b - a for a, b in zip(ts, ts[1:])
                   if not any(a < w <= b for w in world.get(sess, []))]
            print(f"  {sess}: {len(rows)} readings over {ts[-1]-ts[0]:.1f} s")
            if gaps:
                dist("     inter-arrival (all)", [g * 1000 for g in gaps])
            if inw and len(inw) != len(gaps):
                dist("     inter-arrival (within a world)", [g * 1000 for g in inw])
            allg.extend(inw if inw else gaps)
            drops = [(rows[i - 1], rows[i], rows[i - 1][1][1] - rows[i][1][1])
                     for i in range(1, len(rows)) if rows[i][1][1] < rows[i - 1][1][1]]
            if drops:
                a, b, d = max(drops, key=lambda x: x[2])
                mx = max(r[1][0] for r in rows) or 1
                print(f"     {len(drops)} drops; largest {d} HP "
                      f"= {100.0*d/mx:.1f}% of max {mx} "
                      f"({a[0]:.3f} hp={a[1][1]} -> {b[0]:.3f} hp={b[1][1]}, "
                      f"gap {1000*(b[0]-a[0]):.0f} ms)")
        if allg:
            dist("  ALL SESSIONS, inter-arrival", [g * 1000 for g in allg])
        # escape -> ack, both directions observed at the relay
        for sess in sorted(set(escapes) | set(ack)):
            print(f"  {sess}: C->S Escape {len(escapes.get(sess, []))}, "
                  f"S->C EscapeAck {len(ack.get(sess, []))}, "
                  f"S->C ForcedEscape {len(forced.get(sess, []))}")
            es = sorted(escapes.get(sess, []))
            for t, payload in sorted(ack.get(sess, [])):
                prior = [e for e in es if e <= t]
                if prior:
                    print(f"     ack @{t:.3f} payload={payload} "
                          f"<- last Escape @{prior[-1]:.3f} "
                          f"(+{1000*(t-prior[-1]):.0f} ms)")


def histogram(per_session, world_marks, label):
    """Bucket within-world inter-arrival gaps to expose the cadence structure.

    HealthUpdate is pushed into the client's main thread with RunOnMainThread
    (work/decomp/DarzasDominion.Darza.Game/World.cs:16038), so the relay-side gaps
    printed here are the server's send cadence observed on the wire; the client's own
    frame quantisation sits on top of it and is NOT observable through the relay.
    """
    print()
    print("=" * 78)
    print(f"HISTOGRAM: {label}")
    print("=" * 78)
    buckets = Counter()
    for sess, rows in per_session.items():
        ts = [r[0] for r in rows]
        wm = sorted(world_marks.get(sess, []))
        for a, b in zip(ts, ts[1:]):
            if any(a < w <= b for w in wm):
                continue
            g = b - a
            if g <= 0:
                buckets["0"] += 1
            elif g <= 10:
                buckets["1-10"] += 1
            elif g <= 45:
                buckets["11-45"] += 1
            elif g <= 55:
                buckets["46-55"] += 1
            elif g <= 70:
                buckets["56-70"] += 1
            elif g <= 95:
                buckets["71-95"] += 1
            elif g <= 105:
                buckets["96-105"] += 1
            elif g <= 150:
                buckets["106-150"] += 1
            elif g <= 210:
                buckets["151-210"] += 1
            elif g <= 400:
                buckets["211-400"] += 1
            else:
                buckets[">400"] += 1
    total = sum(buckets.values()) or 1
    order = ["0", "1-10", "11-45", "46-55", "56-70", "71-95", "96-105",
             "106-150", "151-210", "211-400", ">400"]
    for k in order:
        if buckets[k]:
            print(f"  {k:>8} ms : {buckets[k]:6d}  {100.0*buckets[k]/total:5.1f}%")
    print(f"  total       : {sum(buckets.values())}")


def main():
    per_session, _, world_marks = analyse_replay()
    histogram(per_session, world_marks, "work/replay.txt, within-world HealthUpdate gaps")
    analyse_events()
    analyse_relay_logs()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
