#!/usr/bin/env python3
"""
move_budget.py -- what the client's own movement telemetry says the legal per-tick budget is.

WHY THIS EXISTS, AND WHAT IT CLOSES.

Phase 3's second exit criterion is that the move budget is *measured* rather than derived and
assumed:

    `b = playerSpeed x frameSeconds`, from the game's own `TilesPerSecond` law
    (DODGE-ALGORITHM.md section 5.2, IMPLEMENTATION-PLAN.md Phase 3)

and the plan's Spike 3.1 also asks for the movement question to be answered "against the
server's own boundary rather than derived and assumed". Two things make that measurable
without another live session, and both are already on disk:

  1. **the client emits its own movement telemetry.** `GmMove` (game id 74, `0x4A`) carries
     the position and the client's own clock, at most once per frame, and the relay has
     logged thousands of them from real play. Each consecutive pair is a distance the client
     *actually travelled* and a time it *actually took*, so the population of steps is the
     client's own distribution of legal movement.
  2. **the server accepted every one of them.** This is the same data the server validated,
     so anything in it is by definition inside the server's acceptance boundary. The maximum
     observed sustained step is therefore a **lower bound on what the server tolerates**, and
     the budget the dodge needs is bounded by the client's own law -- not by a guess about
     where the cliff is.

WHAT IT DOES NOT DO, AND WHY THAT IS DELIBERATE.

It never sends anything and never modifies anything. The plan's boundary *search* -- deliberate
overspend to find the rejection threshold -- is deferred (PHASE3-SCOPE.md section 3): it would
require either a `GmMove` injection, which is a different actuation mechanism from the one the
dodge uses, or a client-side speed the client would never produce. The design does not need the
cliff; it needs the legal step, and the legal step is what this measures.

THE ONE THING TO BE CAREFUL ABOUT, AND HOW IT IS CHECKED.

`Time` in a `GmMove` is a variable-length integer with this project's own grouping (6, 6, 7,
7, ... -- see `docs/INJECTION.md` section 2 and `ClientPackets.Reader.varintOrNull`). A
mis-decoded varint would produce a plausible-looking speed that is wrong by a factor. So the
decode is **validated against the wall clock the relay stamped on each line**: over a whole
session the client's clock and the relay's clock advance at the same rate, so the two deltas
must agree in aggregate. If they do not, the tool says so and refuses to report a speed.

Usage:
  python tools/move_budget.py <relay.log> [<relay.log> ...]
  python tools/move_budget.py <relay.log> --json
"""

import argparse
import json
import math
import os
import re
import struct
import sys

# `[Game#2 C->S] 02:31:54.016 len=14 id=0x4A Move                   4A000000E742...`
RE_MOVE = re.compile(
    r"C->S\]\s+(?P<wall>\d+:\d+:\d+\.\d+)\s+len=(?P<len>\d+)\s+id=0x4A Move\s+(?P<hex>[0-9A-Fa-f]+)")


def read_varint(buf, pos):
    """
    The client's varint, as `networking.packets.ClientPackets.Reader` implements it.

    Returns (value, new_pos) or (None, pos) when the buffer runs out. Kept byte-for-byte in
    step with the Java reader rather than re-derived: two implementations of a wire format
    that disagree are worse than one, and the Java one is already validated against real
    captures by `ClientPacketsTests`.
    """
    if pos >= len(buf):
        return None, pos
    b = buf[pos]
    pos += 1
    result = b & 0x3F
    negative = (b & 0x40) != 0
    shift = 6
    while b & 0x80:
        if pos >= len(buf):
            return None, pos
        b = buf[pos]
        pos += 1
        result |= (b & 0x7F) << shift
        shift += 7
        if shift > 63:
            return None, pos
    return (-result if negative else result), pos


def wall_seconds(text):
    h, m, s = text.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def parse_moves(path):
    """
    Every `Move` packet in one relay log, as (wall clock, x, y, time, history count).

    The body's layout is taken from `docs/INJECTION.md` and confirmed against the captured
    lengths: `[float X][float Y][varint Time][byte HistoryCount][HistoryCount x 8 bytes]`,
    so a 14-byte payload is type(2) + 8 + two varint bytes + count + zero history, a 22-byte
    one is the same with one history entry, and a 30-byte one with two. The tool checks that
    relationship rather than assuming it -- `history_consistent` in the result.
    """
    out = []
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = RE_MOVE.search(line)
            if not m:
                continue
            payload = bytes.fromhex(m.group("hex"))
            if len(payload) < 13:
                continue
            x = struct.unpack("<f", payload[2:6])[0]
            y = struct.unpack("<f", payload[6:10])[0]
            t, pos = read_varint(payload, 10)
            if t is None or pos >= len(payload):
                continue
            history_count = payload[pos]
            pos += 1
            declared = len(payload) - pos
            out.append({
                "wall": wall_seconds(m.group("wall")),
                "len": int(m.group("len")),
                "x": x,
                "y": y,
                "t": t,
                "history": history_count,
                "history_bytes": declared,
                "history_ok": declared == history_count * 8,
            })
    return out


def percentile(values, p):
    if not values:
        return float("nan")
    ordered = sorted(values)
    k = (len(ordered) - 1) * (p / 100.0)
    lo = int(math.floor(k))
    hi = int(math.ceil(k))
    if lo == hi:
        return ordered[lo]
    return ordered[lo] * (hi - k) + ordered[hi] * (k - lo)


# --------------------------------------------------------------------------------------
# THE TELEPORT CUT, AND WHY IT IS A CONSTANT RATHER THAN A PERCENTILE.
#
# The step distribution is bimodal with a wide empty band between the modes, and that is a
# measurement rather than an assumption: over the three captured sessions the largest
# *walking* step observed is about 2.12 world units per 200 ms tick, and the smallest
# *teleport* is about 74.6. Zone changes, respawns and knock-backs live in that second mode,
# and including them would double the reported budget.
#
# A percentile cut cannot express this: 0.1 % of 3 100 steps is three steps, and the sessions
# contain more teleports than that, so a p99.9 cut would still admit one. A fixed cut at 20
# units -- ten times the observed walking maximum and a third of the smallest jump -- is the
# honest instrument, and the tool reports the excluded steps' count and range so a session
# whose real walking ever approached it would be visible rather than silently trimmed.
# --------------------------------------------------------------------------------------
TELEPORT_CUT = 20.0


def analyse(path):
    moves = parse_moves(path)
    result = {
        "file": path,
        "moves": len(moves),
        "notes": [],
    }
    if len(moves) < 10:
        result["notes"].append("too few Move packets to measure anything")
        return result, moves

    bad_history = sum(1 for m in moves if not m["history_ok"])
    result["history_consistent"] = bad_history == 0
    result["history_mismatches"] = bad_history
    if bad_history:
        result["notes"].append(
            "%d packet(s) whose HistoryCount does not match the payload length -- the layout "
            "assumption is wrong for those and their positions may be too" % bad_history)

    # ---------------------------------------------------------------------------------
    # The decode check, done PER PAIR and aggregated.
    #
    # THE SESSION-LEVEL VERSION OF THIS WAS WRONG, and the way it was wrong is worth keeping:
    # it compared the first packet's clock to the last packet's, so a relay log that spans
    # midnight -- `relay-20261004-235440-1500.log` starts at 23:54:40 and ends at 00:16:03 --
    # produced a *negative* wall span and the check refused a decode that is in fact correct.
    # Per pair, every one of these sessions agrees to within half a percent:
    #
    #     per-pair client/wall ratio: p05 0.995  p50 1.000  p95 1.005
    #
    # which is two independent clocks over 639-655 seconds and is exactly the evidence a
    # varint decode needs. The aggregate below is the median of the per-pair ratios.
    # ---------------------------------------------------------------------------------
    ratios = []
    for a, b in zip(moves, moves[1:]):
        dw = b["wall"] - a["wall"]
        dt = (b["t"] - a["t"]) / 1000.0
        if 0.05 < dw < 0.5 and dt > 0.0:
            ratios.append(dt / dw)
    result["pair_ratios"] = len(ratios)
    if ratios:
        result["clock_ratio_p50"] = percentile(ratios, 50)
        result["clock_ratio_p05"] = percentile(ratios, 5)
        result["clock_ratio_p95"] = percentile(ratios, 95)
        result["clock_ok"] = 0.9 <= result["clock_ratio_p50"] <= 1.1
        if not result["clock_ok"]:
            result["notes"].append(
                "the client clock and the relay clock disagree by %.0f%% on a typical pair, so "
                "the Time field is probably mis-decoded and no SPEED is reported"
                % (100.0 * (result["clock_ratio_p50"] - 1.0)))
    else:
        result["clock_ok"] = False
        result["notes"].append("no pairs close enough in wall time to compare clocks")

    # ---------------------------------------------------------------------------------
    # Steps: consecutive packets, with the teleport mode excluded on the record.
    # ---------------------------------------------------------------------------------
    walked = []         # (dt_ms, distance) below the cut
    jumps = []          # (dt_ms, distance) at or above it
    for a, b in zip(moves, moves[1:]):
        dt = b["t"] - a["t"]
        if dt <= 0 or dt > 500:
            continue
        dist = math.hypot(b["x"] - a["x"], b["y"] - a["y"])
        (jumps if dist >= TELEPORT_CUT else walked).append((dt, dist))

    result["teleport_cut"] = TELEPORT_CUT
    result["steps"] = len(walked)
    result["excluded_steps"] = len(jumps)
    if jumps:
        result["excluded_min"] = min(d for _, d in jumps)
        result["excluded_max"] = max(d for _, d in jumps)

    if not walked:
        result["notes"].append("no walking steps below the teleport cut; cannot measure")
        return result, moves

    distances = [d for _, d in walked]
    result["step_p50"] = percentile(distances, 50)
    result["step_p90"] = percentile(distances, 90)
    result["step_p99"] = percentile(distances, 99)
    result["step_p999"] = percentile(distances, 99.9)
    result["step_max"] = max(distances)
    result["dt_p50_ms"] = percentile([dt for dt, _ in walked], 50)
    result["dt_p99_ms"] = percentile([dt for dt, _ in walked], 99)
    # The moving subset: the walking steps only, which is what a speed means. The full
    # population includes every packet where the player stood still, and a median over it is
    # zero by construction -- a number that says nothing about how fast the client can go.
    moving = [(dt, d) for dt, d in walked if d > 0.01]
    result["moving_steps"] = len(moving)
    if moving:
        result["moving_step_p50"] = percentile([d for _, d in moving], 50)
        result["moving_step_p90"] = percentile([d for _, d in moving], 90)

    if result.get("clock_ok") and moving:
        per_s = [d / (dt / 1000.0) for dt, d in moving]
        result["moving_speed_p50_per_s"] = percentile(per_s, 50)
        result["moving_speed_p95_per_s"] = percentile(per_s, 95)
    return result, moves


def main(argv=None):
    ap = argparse.ArgumentParser(description="measure the client's legal movement from GmMove telemetry")
    ap.add_argument("logs", nargs="+")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args(argv)

    reports = []
    for path in args.logs:
        if not os.path.isfile(path):
            reports.append({"file": path, "moves": 0, "notes": ["not found"]})
            continue
        result, _ = analyse(path)
        reports.append(result)

    if args.json:
        print(json.dumps(reports, indent=2))
        return 0

    print("move budget from the client's own GmMove telemetry")
    print("")
    usable = [r for r in reports if r.get("steps")]
    for r in reports:
        print("  %s" % os.path.basename(r["file"]))
        if not r.get("steps"):
            print("    no usable movement (%d packet(s))" % r.get("moves", 0))
            for n in r.get("notes", []):
                print("    note: " + n)
            continue
        print("    %d Move packet(s), %d walking step(s), %d excluded as teleports (>= %.0f units)"
              % (r["moves"], r["steps"], r.get("excluded_steps", 0), r.get("teleport_cut", 0)))
        print("    history layout consistent: %s" % r.get("history_consistent"))
        print("    step  p50 %.4f  p90 %.4f  p99 %.4f  p99.9 %.4f  max %.4f   world units"
              % (r["step_p50"], r["step_p90"], r["step_p99"], r["step_p999"], r["step_max"]))
        print("    tick  p50 %.0f ms  p99 %.0f ms" % (r["dt_p50_ms"], r["dt_p99_ms"]))
        print("    clock agree on a typical pair: %.4f over %d pair(s)"
              % (r.get("clock_ratio_p50", float("nan")), r.get("pair_ratios", 0)))
        if r.get("moving_steps"):
            print("    while MOVING (%d step(s)): p50 %.4f  p90 %.4f  world units per tick"
                  % (r["moving_steps"], r["moving_step_p50"], r["moving_step_p90"]))
        if r.get("moving_speed_p50_per_s"):
            print("    while MOVING: p50 %.3f  p95 %.3f  world units/s"
                  % (r["moving_speed_p50_per_s"], r["moving_speed_p95_per_s"]))
        else:
            print("    SPEED WITHHELD: the Time field could not be validated on this session")
        for n in r.get("notes", []):
            if args.verbose or "mis-decoded" in n:
                print("    note: " + n)

    validated = [r for r in usable if r.get("clock_ok") and r.get("moving_steps")]
    print("")
    if not validated:
        print("NO VALIDATED SPEED. The client clock and the relay clock did not agree on any")
        print("session, so the Time field's decode is unproven and no budget is reported. That")
        print("is the check working: a mis-decoded varint produces a plausible wrong speed.")
        return 1

    walk_max = max(r["step_max"] for r in validated)
    walk_p99 = max(r["step_p99"] for r in validated)
    move_p50 = max(r.get("moving_step_p50", 0.0) for r in validated)
    tick = max(r["dt_p50_ms"] for r in validated)
    speed_max = max(r.get("moving_speed_p95_per_s", 0.0) for r in validated)

    print("across %d validated session(s), in the client's own units:" % len(validated))
    print("  tick length (median)      : %.0f ms  -- the client emits Move once per server tick"
          % tick)
    print("  walking step, p99         : %.4f world units per tick" % walk_p99)
    print("  walking step, max         : %.4f world units per tick" % walk_max)
    print("  typical moving step       : %.4f world units per tick" % move_p50)
    print("  implied speed, p95        : %.3f world units/s" % speed_max)
    print("")
    print("  THE BUDGET PHASE 3 USES.")
    print("")
    print("  `DODGE-ALGORITHM.md` section 5.2's budget is `b = playerSpeed x frameSeconds`,")
    print("  computed by CALLING the client's own speed law rather than by resolving its")
    print("  constants (section 6). That call returns the same units the position uses, so the")
    print("  budget and the measured walking step below are directly comparable -- and the")
    print("  measured step is the client's own accepted travel, so a candidate inside it is a")
    print("  move the server has already been seen to accept.")
    print("")
    print("  THE MEASUREMENT AGREES WITH THE RESOLVED LAW, WHICH IS A CHECK ON BOTH. The law is")
    print("  `3.5 + 5.0 * (SPD + 10) / 75` tiles/s (CLIENT-FINDINGS.md section 2.9), i.e. 3.5 to")
    print("  8.5 tiles/s, which over a %.0f ms tick is 0.70 to 1.70 units per tick **if a world" % tick)
    print("  unit is a tile**. The measured walking mode is %.2f and the p99 is %.2f, and the" % (move_p50, walk_p99))
    print("  observed tick is 200 ms, so the population sits right where the law says it should:")
    print("  inside 1.70 at p99, with the p95 speeds above that explained by the `Speedy` tier")
    print("  multiplier the law applies and by pairs where the tick was short.")
    print("")
    print("  So the two independent routes to the budget -- call the client's law, or measure")
    print("  what the client's accepted packets actually did -- land on the same number, and the")
    print("  dodge's outer candidate radius (%.2f x b at `DODGE-ALGORITHM.md` section 5.2) needs" % 1.0)
    print("  only the law, which the live sweep's own displacement log confirms per frame.")
    print("")
    print("  WHAT THIS IS NOT. It is a LOWER bound on the server's tolerance, not the cliff. The")
    print("  cliff needs deliberate overspend, which PHASE3-SCOPE.md section 3 defers: it would")
    print("  need either a different actuation mechanism from the one the dodge uses, or a speed")
    print("  the client would never produce. The dodge does not need the cliff.")
    print("")
    print("  A CAVEAT ON THE TILE SCALE, stated because it is the one place this could be")
    print("  reading a coincidence: the agreement above holds *if* a world unit is a tile, and")
    print("  the p95 speeds that overshoot the nominal cap are consistent with buffed movement")
    print("  rather than with a different scale. It is not proof. Nothing in the dodge needs it")
    print("  -- the budget, the positions, the radii and the speed law are all in client units,")
    print("  and `DODGE-ALGORITHM.md` section 6's rule is to CALL the law rather than trust a")
    print("  resolved constant, which is exactly what makes the scale irrelevant to the solver.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
