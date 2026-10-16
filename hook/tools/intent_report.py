#!/usr/bin/env python3
"""intent_report.py -- what the player was asking for, and what the dodge did instead.

THE INTERIM INSTRUMENT FOR THE DIRECTED-DODGE WORK (HANDOFF-DIRECTED-DODGE.md). It answers the
two questions that motivate that task, from a run directory, with no build:

  * is the recorded `intent` the PLAYER's live input or the PILOT's last write?
    Test: the adapter logs `steer dodge: frame N yielded` on frames where the pilot wrote
    nothing at all. Whatever is in the trace on those frames came from somewhere other than the
    hook -- so if the values are the eight compass directions, the field is the player's.

  * on the frames the dodge actually steered, how far from the player's direction did it go,
    where did its goal come from, and at what magnitude did it move?

WHY IT PARSES THE LOG AND NOT THE TRACE. The per-frame decision exists only in the adapter's
`dodge WOULD MOVE` lines, which are capped at 400 a run. That cap is why the first thing the
handoff asks for is a `--decisions` dump in `SolverHarness`; until that exists, this is the only
view of the decision that is joined to the player's input, and the cap is the caveat.

THAT DUMP NOW EXISTS, AND `tools/decisions_report.py` IS THE INSTRUMENT TO USE. `SolverHarness
--decisions <path>` writes one line per recorded frame, so the same questions this script answers
are answered over the WHOLE recording rather than over the log's 400-line sample -- and answered
as a diff of two files. This script stays because it reads a LIVE run's own log, which the
harness cannot: it is the only view of a session whose trace is not being replayed, and its first
half (whose field `intent` is) has no replacement at all. Prefer the dump when a trace exists.

Usage:  python intent_report.py <run-dir>
"""
import collections
import json
import math
import os
import re
import sys

FRAME_MOVE = re.compile(
    r"dodge WOULD MOVE \| frame (\d+) \| heading (-?[\d.]+)deg scale ([\d.]+) \| (\w+) held (\d) "
    r"\| candidates (\d+)/(\d+) \| durability (\d+)ms clearance (-?[\d.]+) \| goal (-?[\d.]+)deg "
    r"\(course (\d)\)")
FRAME_YIELD = re.compile(r"steer dodge: frame (\d+) yielded")


def load_frames(run_dir):
    frames = {}
    with open(os.path.join(run_dir, "trace.jsonl"), "r", encoding="utf-8") as fh:
        for line in fh:
            if not line.startswith('{"t":"F"'):
                continue
            try:
                f = json.loads(line)
            except Exception:
                continue
            frames[f["f"]] = f
    return frames


def compass_counts(frames):
    c = collections.Counter()
    for f in frames:
        c[round(math.degrees(f["intent"]) % 360.0, 1)] += 1
    return c


def gap_deg(a, b):
    d = (a - b) % 360.0
    return min(d, 360.0 - d)


def main(run_dir):
    frames = load_frames(run_dir)
    by_frame = frames
    print("frames in the trace: %d" % len(frames))

    moved = []
    yielded = []
    with open(os.path.join(run_dir, "bootstrap.log"), "r", encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            m = FRAME_MOVE.search(line)
            if m:
                moved.append(dict(frame=int(m.group(1)), heading=float(m.group(2)),
                                  scale=float(m.group(3)), reason=m.group(4),
                                  held=int(m.group(5)), cand=int(m.group(6)),
                                  evaluated=int(m.group(7)), durability=int(m.group(8)),
                                  clearance=float(m.group(9)), goal=float(m.group(10)),
                                  course=int(m.group(11))))
                continue
            m = FRAME_YIELD.search(line)
            if m:
                yielded.append(int(m.group(1)))

    # -- 1. whose field is `intent`? ------------------------------------------------
    print()
    print("== 1. is `intent` the player's input, or the pilot's last write? ==")
    ys = [by_frame[f] for f in yielded if f in by_frame]
    if not ys:
        print("   no `steer dodge ... yielded` lines -- cannot answer from this run")
    else:
        c = compass_counts(ys)
        main8 = [v for v in c if min(v % 45, 45 - (v % 45)) < 0.6]
        print("   frames the pilot wrote NOTHING on : %d" % len(ys))
        print("   distinct intent values there      : %d" % len(c))
        print("   ...of which compass directions    : %d  %s"
              % (len(main8), sorted(round(v) for v in main8)))
        print("   the odd few are a compass direction plus the camera's own rounding.")
        print("   -> the field is the PLAYER's live eight-way input." if len(main8) >= 6 else
              "   -> NOT clearly the player's input; treat this field with suspicion.")

    # -- 1b. does `moving` mean "the player is holding a direction"? -----------------
    #
    # THE FLAG THE DESIGN HANGS ON. `intent` goes stale the moment the player lets go, so the
    # goal may only use it while the player is actually asking to walk. `bootstrap.log` section
    # 3.10 records a session where `Moving` was false on every recorded frame while the pilot was
    # driving, which reads as "the pilot clobbers it" -- but if it is still true on frames the
    # pilot demonstrably did not touch, it is the player's own flag surviving into the frame tick.
    print()
    print("== 1b. does `moving` mean the player is holding a direction? ==")
    ordered = sorted(frames.values(), key=lambda f: f["f"])
    held = sum(1 for f in ordered if f.get("moving"))
    print("   moving=true over the run          : %d of %d (%.1f%%)"
          % (held, len(ordered), 100.0 * held / max(len(ordered), 1)))
    if ys:
        yh = sum(1 for f in ys if f.get("moving"))
        print("   moving=true on the PILOT-SILENT frames : %d of %d (%.1f%%)   <- the decisive one"
              % (yh, len(ys), 100.0 * yh / len(ys)))
    stale = 0
    n = 0
    prev = None
    for f in ordered:
        if prev is not None and not f.get("moving"):
            n += 1
            if round(f["intent"], 6) == round(prev["intent"], 6):
                stale += 1
        prev = f
    print("   moving=false frames whose intent did NOT change : %d of %d (%.1f%%)"
          % (stale, n, 100.0 * stale / max(n, 1)))
    print("   -> the intent goes stale when the player stops; gate it on `moving`.")

    # -- 2. what did the dodge do to it? --------------------------------------------
    print()
    print("== 2. on the frames the dodge steered ==")
    if not moved:
        print("   no `dodge WOULD MOVE` lines in this run")
        return
    joined = [r for r in moved if r["frame"] in by_frame]
    print("   logged moves (the log caps at 400) : %d" % len(joined))
    if not joined:
        return
    held = sum(1 for r in joined if by_frame[r["frame"]].get("moving"))
    from_course = sum(1 for r in joined if r["course"])
    same_as_goal = sum(1 for r in joined
                       if gap_deg(r["heading"] % 360.0, r["goal"] % 360.0) < 1.0)
    deltas = [gap_deg(r["heading"] % 360.0,
                      (math.degrees(by_frame[r["frame"]]["intent"])
                       - math.degrees(by_frame[r["frame"]]["rot"])) % 360.0) for r in joined]
    print("   the player was holding a direction : %d (%.1f%%)"
          % (held, 100.0 * held / len(joined)))
    print("   the goal came from the measured course : %d (%.1f%%)"
          % (from_course, 100.0 * from_course / len(joined)))
    print("   the heading EQUALLED its own goal  : %d (%.1f%%)"
          % (same_as_goal, 100.0 * same_as_goal / len(joined)))
    print("   mean |heading - the player's direction| : %.1f deg"
          % (sum(deltas) / len(deltas)))
    print("   angle to the player's direction:")
    for lo, hi in ((0, 15), (15, 45), (45, 90), (90, 135), (135, 181)):
        n = sum(1 for d in deltas if lo <= d < hi)
        print("      %3d-%3d deg : %4d (%.1f%%)" % (lo, hi, n, 100.0 * n / len(deltas)))
    print("   scales written : %s" % dict(collections.Counter(round(r["scale"], 2) for r in joined)))
    print("   reasons        : %s" % dict(collections.Counter(r["reason"] for r in joined)))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    main(sys.argv[1])
