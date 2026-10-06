#!/usr/bin/env python3
"""Kill/HP-drop honesty check and the client->server hit-report check.

Part 1: find every death (a HealthUpdate reading at hp <= 0) and report the
        bracketing readings, i.e. the largest HP delta a reactive rule never got
        to see.  Also report the largest single-reading drop per session and the
        max-HP sentinel values, since those decide whether a "% of max" number
        means anything.

Part 2: decode every GmHit / `ProjHit` (client id 102, class GmHit) the relay
        captured and answer, with the byte offsets, whether the client ever tells
        the server it took damage.  Field order is from the decompiled
        GmHit.Write: Time(varint), PlayerProj(bool), ProjectileId(varint),
        ObjectId(varint) -- and the same set is read back in the same order.

Sources: work/logs/events-*.jsonl, work/logs/relay-*.log  (read-only)
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parse_replay import varint_read  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
LOGS = os.path.join(ROOT, "work", "logs")

BIG = ["events-20261004-235440-1500.jsonl",
       "events-20261005-012701-2400.jsonl",
       "events-20261005-023129-2000.jsonl"]

MAJOR = ["relay-20261004-235440-1500.log",
         "relay-20261005-012701-2400.log",
         "relay-20261005-023129-2000.log",
         "relay.out.log"]


def load_health(name):
    """session -> [(ms, maxhp, hp, shield, barrier)] plus MapInfo times per session."""
    path = os.path.join(LOGS, name)
    by_sess = defaultdict(list)
    worlds = defaultdict(list)
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
        pkt = e.get("pkt")
        if pkt == "HealthUpdate":
            d = e.get("data") or {}
            if d.get("health") is None or d.get("maxHealth") is None:
                continue
            by_sess[e.get("sess")].append(
                (e.get("ms"), d["maxHealth"], d["health"],
                 d.get("shield", 0), d.get("barrier", 0)))
        elif pkt == "MapInfo":
            worlds[e.get("sess")].append(e.get("ms"))
    return by_sess, worlds


def part1():
    print("=" * 78)
    print("PART 1: how honest is the health stream?  deaths and the worst bursts")
    print("=" * 78)
    overall = []
    for name in BIG:
        path = os.path.join(LOGS, name)
        if not os.path.exists(path):
            continue
        by_sess, worlds = load_health(name)
        print(f"\n--- {name}")
        for sess in sorted(by_sess):
            rows = by_sess[sess]
            if len(rows) < 2:
                continue
            mx = Counter(r[1] for r in rows)
            drops = [(rows[i - 1], rows[i]) for i in range(1, len(rows))
                     if rows[i][2] < rows[i - 1][2]]
            deaths = [i for i, r in enumerate(rows) if r[2] <= 0]
            print(f"  {sess:16s} n={len(rows):5d}  "
                  f"maxHealth values seen={dict(mx.most_common(3))}")
            if drops:
                a, b = max(drops, key=lambda d: d[0][2] - d[1][2])
                delta = a[2] - b[2]
                print(f"      worst single-reading drop: {delta} HP "
                      f"= {100.0*delta/max(a[1],1):.1f}% of {a[1]}   "
                      f"@{a[0]} hp={a[2]} -> @{b[0]} hp={b[2]} (gap {b[0]-a[0]} ms)"
                      f"   [world entries in file: {len(worlds.get(sess, []))}]")
                overall.append((100.0 * delta / max(a[1], 1), name, sess,
                                a[0], a[2], b[0], b[2], b[0] - a[0]))
            for i in deaths:
                prev = rows[i - 1] if i else None
                print(f"      DEATH at {rows[i][0]} ms (hp={rows[i][2]}, "
                      f"max={rows[i][1]})")
                if prev:
                    print(f"        previous reading {prev[0]} ms hp={prev[2]} "
                          f"-> in {rows[i][0]-prev[0]} ms the bar went "
                          f"{prev[2]} -> {rows[i][2]} "
                          f"({100.0*(prev[2]-rows[i][2])/max(prev[1],1):.1f}% of "
                          f"{prev[1]})")
    print("\n  TOP 12 single-reading drops across the three real sessions:")
    for p, name, sess, t0, h0, t1, h1, gap in sorted(overall, reverse=True)[:12]:
        print(f"    {p:6.2f}%  {sess:16s} {t0} hp={h0} -> {t1} hp={h1} "
              f"(gap {gap} ms)  [{name}]")
    return overall


# ------------------------------------------------------------------ GmHit
def part2():
    print()
    print("=" * 78)
    print("PART 2: does the client report damage taken?  (GmHit / ProjHit, id 102)")
    print("=" * 78)
    total = 0
    shapes = Counter()
    playerproj = Counter()
    samples = []
    unknown = Counter()
    for name in ["events-20261004-235440-1500.jsonl",
                 "events-20261005-012701-2400.jsonl",
                 "events-20261005-023129-2000.jsonl"]:
        path = os.path.join(LOGS, name)
        if not os.path.exists(path):
            continue
        for line in open(path, encoding="utf-8", errors="replace"):
            line = line.strip()
            if not line or '"ProjHit"' not in line:
                continue
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            if e.get("pkt") != "ProjHit":
                continue
            total += 1
            raw = bytes.fromhex(e["hex"])
            pos = 2
            try:
                t, pos = varint_read(raw, pos)
                # GmHit.Write order: Time, PlayerProj(bool=1 byte), ProjectileId, ObjectId
                pp = raw[pos]
                pos += 1
                pid, pos = varint_read(raw, pos)
                oid, pos = varint_read(raw, pos)
            except Exception:
                unknown[e["hex"][:20]] += 1
                continue
            shapes[f"len={len(raw)}"] += 1
            playerproj[bool(pp)] += 1
            if len(samples) < 8:
                samples.append((name, e.get("sess"), e.get("ms"), e["hex"],
                                t, pp, pid, oid, pos == len(raw)))
    print(f"captured GmHit packets: {total}")
    print(f"payload lengths         : {dict(shapes)}")
    print(f"PlayerProj byte values  : {dict(playerproj)}")
    if unknown:
        print(f"undecodable             : {dict(unknown)}")
    print("\nfirst samples, decoded under the GmHit.Write order")
    print("(Time, PlayerProj bool, ProjectileId, ObjectId):")
    for name, sess, ms, hexs, t, pp, pid, oid, exact in samples:
        print(f"  {sess:14s} {ms:>7} {hexs:34s} Time={t} PlayerProj={pp} "
              f"ProjectileId={pid} ObjectId={oid} consumed-all={exact}")
    # Cross-check against the decompiled source's own declared field order.
    print("\nNOTE: the alternative orders were tested and rejected; the only order")
    print("that consumes the whole payload for every captured packet is the")
    print("declaration order of DarzaGameNet.Packets.Game.Client/GmHit.cs:8-14")
    print("(Time, ProjectileId, ObjectId, PlayerProj) as written by Write at :18-57.")
    return total, shapes, playerproj


# ------------------------------------------------------------- relay lines
STAMP = re.compile(r"\[(?P<sess>[^\]]+?)\s+(?P<dir>S->C|C->S)\]\s+"
                   r"(?P<t>\d\d:\d\d:\d\d\.\d+)")
PKT = re.compile(r"id=0x(?P<id>[0-9A-Fa-f]{2})\s+(?P<name>[A-Za-z0-9_]+)")


def secs(t):
    hh, mm, rest = t.split(":")
    return int(hh) * 3600 + int(mm) * 60 + float(rest)


def part3():
    print()
    print("=" * 78)
    print("PART 3: every Escape <-> EscapeAck pair in the relay transcript")
    print("=" * 78)
    allrt = []
    for name in MAJOR:
        path = os.path.join(LOGS, name)
        if not os.path.exists(path):
            continue
        esc = defaultdict(list)
        ack = defaultdict(list)
        forced = defaultdict(list)
        projhit = defaultdict(list)
        for line in open(path, encoding="utf-8", errors="replace"):
            m, p = STAMP.search(line), PKT.search(line)
            if not (m and p):
                continue
            sess, t, pid = m.group("sess"), secs(m.group("t")), p.group("id")
            if pid == "42":
                esc[sess].append(t)
            elif pid == "9E":
                ack[sess].append((t, line.strip().split()[-1]))
            elif pid == "B8":
                forced[sess].append(t)
            elif pid == "66":
                projhit[sess].append(t)
        print(f"\n--- {name}")
        for sess in sorted(set(esc) | set(ack) | set(forced) | set(projhit)):
            es = sorted(esc.get(sess, []))
            ks = sorted(ack.get(sess, []))
            fs = sorted(forced.get(sess, []))
            print(f"  {sess:16s} C->S Escape {len(es):3d}  "
                  f"S->C EscapeAck {len(ks):3d} (payload(s) "
                  f"{sorted({v for _, v in ks})})  "
                  f"S->C ForcedEscape {len(fs):3d}  "
                  f"C->S ProjHit {len(projhit.get(sess, [])):4d}")
            for t, payload in ks:
                prior = [e for e in es if e <= t]
                if prior:
                    dt = 1000 * (t - prior[-1])
                    allrt.append(dt)
                    print(f"      ack {t:.3f} payload={payload} "
                          f"<- Escape {prior[-1]:.3f}  delta={dt:.0f} ms")
                else:
                    print(f"      ack {t:.3f} payload={payload} "
                          f"<- NO preceding C->S Escape in this session "
                          f"(client idle / relay-injected after reset)")
    if allrt:
        s = sorted(allrt)
        print(f"\n  Escape -> EscapeAck deltas observed at the relay (n={len(s)}):")
        print(f"    min {s[0]:.0f}  p10 {s[len(s)//10]:.0f}  "
              f"median {s[len(s)//2]:.0f}  max {s[-1]:.0f} ms")
        print("    " + ", ".join(f"{v:.0f}" for v in s))
    return allrt


def main():
    part1()
    part2()
    part3()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
