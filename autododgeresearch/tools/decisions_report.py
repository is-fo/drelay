#!/usr/bin/env python3
"""decisions_report.py -- diff two `SolverHarness --decisions` dumps.

THE INSTRUMENT FOR THE DIRECTED-DODGE WORK (HANDOFF-DIRECTED-DODGE.md). `intent_report.py`
is the interim view; it parses the adapter's `dodge WOULD MOVE` lines, which are capped at
400 a session, so every number it prints is a mean over a sample. This reads the per-frame
dump instead -- one line per recorded frame, every frame -- so the same five questions are
answered over the whole recording and answered as a *diff* rather than as two means:

  1. where did the goal come from -- the player's held direction, or the body's own course?
     (the defect: 97.8 % of the baseline session's logged moves had a course goal)
  2. how far is the written heading from the direction the player was holding, on the frames
     they were holding one? (the baseline: mean 47.4 deg, and 48 % of moves past 45 deg)
  3. at what magnitude does it move? (the baseline: 297 of 400 logged moves at scale 0.35)
  4. does the deflection decay -- within a run of consecutive moves, does the angle back to
     the player's direction shrink rather than hold? (the "returns to the line" claim, which
     a mean cannot make)
  5. what reasons, how often is the hold kept, and what does the cost look like.

Usage:
    python decisions_report.py <decisions.txt> [<decisions-before.txt>]

With one file it reports that file. With two it reports the second as "before" and the
first as "after" and prints the deltas, which is the shape every experiment here wants:
the same recording, the same solver, one line of source changed.
"""
import collections
import math
import os
import re
import sys

COLUMNS = ("frame action reason held heading scale goal goalsrc hintent intent course moving "
           "evaluated admissible durabilityMs clearance resolve score runnerup costUs windowMs").split()


def gap_deg(a, b):
    d = (a - b) % 360.0
    return min(d, 360.0 - d)


def signed_deg(a, b):
    """Signed a-b in (-180, 180], which is what a "deflection" has to be to average."""
    d = (a - b + 180.0) % 360.0 - 180.0
    return d


def load(path):
    """Read a dump, taking the column list from the file's own `# frame | ...` header.

    THE HEADER IS THE CONTRACT, NOT THIS MODULE. The dump gained a column (`windowMs`) the moment
    the rule 6 decision window stopped being a constant, and a reader that hard-coded the field list
    would have silently mis-parsed every older file rather than reporting what it could not read.
    A file with no header falls back to the list below.
    """
    cols = None
    rows = []
    bad = 0
    with open(path, "r", encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            line = line.rstrip("\r\n")
            if not line:
                continue
            if line.startswith("#"):
                if "frame" in line and "|" in line:
                    head = line.lstrip("#").strip()
                    if head.startswith("frame"):
                        cols = [c.strip() for c in head.split("|")]
                continue
            parts = [p.strip() for p in line.split("|")]
            if cols is None:
                cols = [c for c in COLUMNS if c in ("frame", "action", "reason", "held", "heading",
                                                    "scale", "goal", "goalsrc", "hintent", "intent",
                                                    "course", "moving", "evaluated", "admissible",
                                                    "durabilityMs", "clearance", "resolve", "score",
                                                    "runnerup", "costUs")]
            if len(parts) != len(cols):
                bad += 1
                continue
            r = dict(zip(cols, parts))
            # the report's own baseline fields must be present or the file is not one of these
            if not all(k in r for k in ("frame", "action", "moving", "heading", "hintent")):
                bad += 1
                continue
            try:
                for k in ("frame", "held", "moving", "evaluated", "admissible", "costUs"):
                    r[k] = int(r[k])
                for k in ("heading", "scale", "goal", "hintent", "intent", "course",
                          "durabilityMs", "clearance", "score", "runnerup"):
                    r[k] = float(r[k])
                r["windowMs"] = float(r["windowMs"]) if "windowMs" in r else float("nan")
            except ValueError:
                bad += 1
                continue
            rows.append(r)
    return rows, bad


def summarize(rows):
    s = {}
    moves = [r for r in rows if r["action"] == "Move"]
    s["frames"] = len(rows)
    s["moves"] = len(moves)
    s["reasons"] = collections.Counter(r["reason"] for r in rows)
    s["held"] = sum(1 for r in moves if r["held"])
    s["goal_player"] = sum(1 for r in moves if r["goalsrc"] == "player")
    s["goal_course"] = sum(1 for r in moves if r["goalsrc"] == "course")
    s["goal_intent"] = sum(1 for r in moves if r["goalsrc"] == "intent")
    s["goal_none"] = sum(1 for r in moves if r["goalsrc"] == "none")
    s["scales"] = collections.Counter(round(r["scale"], 2) for r in moves)

    # The measurement the operator's sentence is about: the dodge's heading against the
    # direction the player was actually holding, on the moves where they were holding one.
    walked = [r for r in moves if r["moving"] and r["goalsrc"] != "none"]
    s["moves_while_holding"] = len(walked)
    s["deltas"] = [gap_deg(r["heading"], r["hintent"]) for r in walked]
    s["signed"] = [signed_deg(r["heading"], r["hintent"]) for r in walked]
    s["over90"] = sum(1 for d in s["deltas"] if d > 90.0)
    s["over45"] = sum(1 for d in s["deltas"] if d > 45.0)
    s["same_as_goal"] = sum(1 for r in moves if gap_deg(r["heading"], r["goal"]) < 1.0)
    s["durability"] = sorted(r["durabilityMs"] for r in moves)
    s["cost"] = sorted(r["costUs"] for r in rows if r["evaluated"] > 0)
    s["evaluated_frames"] = sum(1 for r in rows if r["evaluated"] > 0)
    # The rule 6 decision window, which stopped being a constant. Reported on the frames that
    # actually built a candidate set, because on the others no window was ever computed.
    s["windows"] = sorted(r["windowMs"] for r in rows
                          if r["evaluated"] > 0 and r["windowMs"] == r["windowMs"])

    # 4. Does the deflection decay? For each run of consecutive moving frames, the first and
    # the last deflection -- a "returns to the line" claim is about the last one being smaller,
    # not about a mean over both.
    runs = []
    cur = []
    prev_frame = None
    for r in rows:
        if r["action"] == "Move":
            if prev_frame is not None and r["frame"] - prev_frame > 2 and cur:
                runs.append(cur)
                cur = []
            cur.append(r)
            prev_frame = r["frame"]
    if cur:
        runs.append(cur)
    s["runs"] = len(runs)
    s["runs_ge3"] = [r for r in runs if len(r) >= 3]
    decay_first = 0.0
    decay_last = 0.0
    n = 0
    for run in s["runs_ge3"]:
        first = run[0]
        last = run[-1]
        if not first["moving"] or not last["moving"]:
            continue
        decay_first += gap_deg(first["heading"], first["hintent"])
        decay_last += gap_deg(last["heading"], last["hintent"])
        n += 1
    s["decay_n"] = n
    s["decay_first"] = decay_first / n if n else float("nan")
    s["decay_last"] = decay_last / n if n else float("nan")
    return s


def pct(a, b):
    return "n/a" if not b else "%.1f%%" % (100.0 * a / b)


def mean(xs):
    return sum(xs) / len(xs) if xs else float("nan")


def p(sorted_values, q):
    if not sorted_values:
        return float("nan")
    i = min(len(sorted_values) - 1, max(0, int(round(q * (len(sorted_values) - 1)))))
    return sorted_values[i]


def report(name, s, before=None):
    print("== %s ==" % name)
    print("  frames                      : %d" % s["frames"])
    print("  frames that moved           : %d (%s)" % (s["moves"], pct(s["moves"], s["frames"])))
    print("  ...with the player walking  : %d (%s of moves)"
          % (s["moves_while_holding"], pct(s["moves_while_holding"], s["moves"])))
    print("  the goal came from          : player %d (%s), course %d (%s), intent %d, none %d"
          % (s["goal_player"], pct(s["goal_player"], s["moves"]),
             s["goal_course"], pct(s["goal_course"], s["moves"]),
             s["goal_intent"], s["goal_none"]))
    print("  heading equalled its goal   : %d (%s)"
          % (s["same_as_goal"], pct(s["same_as_goal"], s["moves"])))
    print("  hysteresis holds            : %d (%s of moves)"
          % (s["held"], pct(s["held"], s["moves"])))
    if s["deltas"]:
        print("  |heading - held direction|  : mean %.1f deg, median %.1f, p90 %.1f"
              % (mean(s["deltas"]), p(sorted(s["deltas"]), 0.5), p(sorted(s["deltas"]), 0.9)))
        print("  signed deflection           : mean %+.1f deg (positive = to the player's right)"
              % mean(s["signed"]))
        print("  disagreeing by >45 / >90    : %d (%s) / %d (%s)"
              % (s["over45"], pct(s["over45"], len(s["deltas"])),
                 s["over90"], pct(s["over90"], len(s["deltas"]))))
        print("  angle to the held direction :")
        for lo, hi in ((0, 15), (15, 45), (45, 90), (90, 135), (135, 181)):
            n = sum(1 for d in s["deltas"] if lo <= d < hi)
            print("      %3d-%3d deg : %5d (%s)" % (lo, hi, n, pct(n, len(s["deltas"]))))
    print("  scales written              : %s" % dict(sorted(s["scales"].items())))
    print("  reasons                     : %s"
          % dict(sorted(((str(k), v) for k, v in s["reasons"].items()), key=lambda kv: -kv[1])))
    if s["durability"]:
        print("  chosen durability           : p50 %s ms, p10 %s ms"
              % (("%.1f" % p(s["durability"], 0.5)), ("%.1f" % p(s["durability"], 0.1))))
    if s["windows"]:
        print("  rule 6 window, evaluating   : p10 %.1f ms, p50 %.1f ms, p90 %.1f ms, max %.1f ms"
              % (p(s["windows"], 0.10), p(s["windows"], 0.50),
                 p(s["windows"], 0.90), s["windows"][-1]))
    if s["cost"]:
        print("  decide() cost, evaluating   : p50 %.0f us, p99 %.0f us, max %.0f us over %d frame(s)"
              % (p(s["cost"], 0.5), p(s["cost"], 0.99), s["cost"][-1], s["evaluated_frames"]))
    print("  consecutive-move runs       : %d, of which >=3 frames: %d"
          % (s["runs"], len(s["runs_ge3"])))
    if s["decay_n"]:
        print("  deflection, first -> last   : %.1f deg -> %.1f deg over %d run(s)   <- the decay"
              % (s["decay_first"], s["decay_last"], s["decay_n"]))

    if before is not None:
        print("  -- against the before column --")
        print("  moves       : %d -> %d (%+.1f pt of frames)"
              % (before["moves"], s["moves"],
                 100.0 * (s["moves"] - before["moves"]) / max(s["frames"], 1)))
        print("  goal=player : %s -> %s"
              % (pct(before["goal_player"], before["moves"]), pct(s["goal_player"], s["moves"])))
        print("  goal=course : %s -> %s"
              % (pct(before["goal_course"], before["moves"]), pct(s["goal_course"], s["moves"])))
        print("  hysteresis  : %s -> %s"
              % (pct(before["held"], before["moves"]), pct(s["held"], s["moves"])))
        if before["deltas"] and s["deltas"]:
            print("  deflection  : mean %.1f -> %.1f deg"
                  % (mean(before["deltas"]), mean(s["deltas"])))
            print("  >45 / >90   : %s / %s -> %s / %s"
                  % (pct(before["over45"], len(before["deltas"])),
                     pct(before["over90"], len(before["deltas"])),
                     pct(s["over45"], len(s["deltas"])),
                     pct(s["over90"], len(s["deltas"]))))
        if before["decay_n"] and s["decay_n"]:
            print("  decay       : %.1f -> %.1f deg"
                  % (before["decay_last"], s["decay_last"]))
    print()


def main(argv):
    if len(argv) not in (2, 3):
        print(__doc__)
        return 2
    after, bad_a = load(argv[1])
    print("loaded %s: %d frame(s)%s"
          % (argv[1], len(after), "" if not bad_a else ", %d unreadable line(s)" % bad_a))
    before = None
    if len(argv) == 3:
        before_rows, bad_b = load(argv[2])
        print("loaded %s: %d frame(s)%s"
              % (argv[2], len(before_rows), "" if not bad_b else ", %d unreadable line(s)" % bad_b))
        before = summarize(before_rows)
        after_name = os.path.basename(argv[1]) + " (after)"
    else:
        after_name = os.path.basename(argv[1])
    print()
    report(after_name, summarize(after), before)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
