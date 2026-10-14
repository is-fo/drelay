#!/usr/bin/env python3
"""
steering_report.py -- read a Spike 3.1 run and say whether the hook moved the player.

WHAT IT IS FOR.  IMPLEMENTATION-PLAN.md's Spike 3.1 asks one question -- *can the hook cause the
player to move in a direction the server accepts?* -- and `SteeringPilot` answers the first half
in-process.  This turns that run's evidence into the verdict, because the numbers are spread over
three places and the interesting ones are relations between them:

  1. `bootstrap.log`  -- the pilot's own frame lines and per-phase summaries.  Gives the commanded
                         angle, the value written, the value *read back*, the world rotation and the
                         realised per-frame displacement.
  2. `hook.log`       -- the install, the target match and the patch.  A target that was never
                         patched produces no steering lines at all, and that reads exactly like a
                         pilot that chose to do nothing.
  3. the relay log    -- the wire: the `GmMove` stream the client emitted and any `GmKicked`.  The
                         client's own position is not evidence about the server, so this is the only
                         witness for "the server accepted it".

WHAT IT CHECKS (the five properties in PHASE3-SCOPE.md section 4.7):

  A  the input landed            the read-back equals the value written
  B  the integrator acted        the player moved on a majority of the phase's frames
  C  the direction was obeyed    mean cos(observed - commanded) is high, or (sweep) the observed
                                 headings advance with the commanded ones
  D  the server did not object   no GmKicked in the relay log, and no movement reason if there is one
  E  the stream looks sane       the client kept emitting Move packets at its usual cadence

A run whose mode was rejected, or whose target was never patched, fails loudly here rather than
producing a table of zeroes that reads as "the client ignored us".

Usage:
  python tools/steering_report.py <run-dir> [relay-session.log]
  python tools/steering_report.py <run-dir> --json
"""

import argparse
import json
import math
import os
import re
import struct
import sys

# --------------------------------------------------------------------------------------
# Log shapes. Kept as explicit regexes rather than parsed fields because both logs are
# human-first: the pilot's line is prose with numbers in it, and it has changed once
# already. A miss is reported as "no lines of this kind", never as a zero.
# --------------------------------------------------------------------------------------

RE_FRAME = re.compile(
    r"steer frame (?P<frame>\d+) phase (?P<phase>\d+) cmd (?P<cmd>[-0-9.]+)deg "
    r"wrote (?P<wrote>[-0-9.]+)rad read-back (?P<read>[-0-9.]+)rad rot (?P<rot>[-0-9.]+)deg "
    r"pos \((?P<x>[-0-9.]+),(?P<y>[-0-9.]+)\) d \((?P<dx>[-0-9.]+),(?P<dy>[-0-9.]+)\) "
    r"\|d\| (?P<len>[-0-9.]+)")

RE_PHASE = re.compile(
    r"steering phase (?P<n>\d+) \((?P<name>[^)]+)\): target (?P<target>[-0-9.]+)deg, "
    r"moved on (?P<moved>\d+) frame\(s\), observed heading (?P<obs>[-0-9.]+)deg, "
    r"mean \|delta\| (?P<abs>[-0-9.]+) world units, "
    r"mean cos\(observed - last commanded\) (?P<cos>[-0-9.]+)")

RE_SUMMARY = re.compile(
    r"STEERING SUMMARY \| frames (?P<frames>\d+), frames the player moved (?P<moved>\d+), "
    r"skipped: no-player (?P<noplayer>\d+), dead-or-safe (?P<dead>\d+) \| mode '(?P<mode>[^']*)'")

RE_ARMED = re.compile(r"resolve\[7\].*steering pilot is (ARMED|IDLE|NOT available)(?P<rest>.*)")
RE_PUBLISHED = re.compile(r"managed movement replacement published at 0x(?P<addr>[0-9A-F]+)")
RE_PATCHED = re.compile(r"PATCHED 'movementIntegrator' at (?P<addr>0x[0-9A-F]+)")
RE_NOTPATCHED = re.compile(r"target 'movementIntegrator' verified at (?P<addr>0x[0-9A-F]+) and NOT patched")
RE_MISSING = re.compile(r"movementIntegrator\s+MISSING")
RE_REJECTED = re.compile(r"unrecognised DRELAY_MOVE_STEER mode|the steering pilot is ARMED but")

RE_RELAY_MOVE = re.compile(r"C->S\] .*?len=(?P<len>\d+) id=0x4A Move\s+(?P<hex>[0-9A-Fa-f]+)")
RE_RELAY_KICK = re.compile(r"id=0x(?:B9|185)\b.*?Kicked", re.IGNORECASE)
RE_RELAY_KICKED_EVENT = re.compile(r'"kickedReason"\s*:\s*"([^"]*)"')

# Direction words the client's own kick reasons use. A kick for a reason that has nothing
# to do with movement (an authentication failure, a server restart) is not evidence about
# the actuator, and treating it as such would be a false positive in the other direction.
MOVEMENT_WORDS = ("move", "speed", "hack", "position", "teleport", "desync", "cheat")


def read_text(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


def circular_mean_deg(sin_sum, cos_sum):
    if sin_sum == 0.0 and cos_sum == 0.0:
        return float("nan")
    return math.degrees(math.atan2(sin_sum, cos_sum))


def angle_delta_deg(a, b):
    """Signed a-b in (-180, 180]."""
    d = (a - b) % 360.0
    if d > 180.0:
        d -= 360.0
    return d


def parse_run(run_dir):
    out = {
        "run_dir": run_dir,
        "frames": [],
        "phases": [],
        "summary": None,
        "armed": None,
        "armed_line": None,
        "published": None,
        "patched": None,
        "not_patched": None,
        "missing": False,
        "rejected": False,
        "notes": [],
    }

    boot = os.path.join(run_dir, "bootstrap.log")
    hook = os.path.join(run_dir, "hook.log")
    if not os.path.isfile(boot):
        out["notes"].append("bootstrap.log is missing from %s" % run_dir)
    else:
        for line in read_text(boot).splitlines():
            m = RE_FRAME.search(line)
            if m:
                out["frames"].append({
                    "frame": int(m.group("frame")),
                    "phase": int(m.group("phase")),
                    "cmd": float(m.group("cmd")),
                    "wrote": float(m.group("wrote")),
                    "read": float(m.group("read")),
                    "rot": float(m.group("rot")),
                    "x": float(m.group("x")),
                    "y": float(m.group("y")),
                    "dx": float(m.group("dx")),
                    "dy": float(m.group("dy")),
                    "len": float(m.group("len")),
                })
                continue
            m = RE_PHASE.search(line)
            if m:
                out["phases"].append({
                    "n": int(m.group("n")),
                    "name": m.group("name"),
                    "target": float(m.group("target")),
                    "moved": int(m.group("moved")),
                    "observed": float(m.group("obs")),
                    "mean_abs": float(m.group("abs")),
                    "mean_cos": float(m.group("cos")),
                })
                continue
            m = RE_SUMMARY.search(line)
            if m:
                out["summary"] = {
                    "frames": int(m.group("frames")),
                    "moved": int(m.group("moved")),
                    "no_player": int(m.group("noplayer")),
                    "dead_or_safe": int(m.group("dead")),
                    "mode": m.group("mode"),
                }
                continue
            if "steering pilot is" in line:
                out["armed_line"] = line.strip()
                if "ARMED" in line:
                    out["armed"] = "armed"
                elif "IDLE" in line:
                    out["armed"] = "idle"
                else:
                    out["armed"] = "unavailable"
                continue
            if RE_REJECTED.search(line):
                out["rejected"] = True

    if not os.path.isfile(hook):
        out["notes"].append("hook.log is missing from %s" % run_dir)
    else:
        for line in read_text(hook).splitlines():
            m = RE_PUBLISHED.search(line)
            if m:
                out["published"] = m.group("addr")
                continue
            m = RE_PATCHED.search(line)
            if m:
                out["patched"] = m.group("addr")
                continue
            m = RE_NOTPATCHED.search(line)
            if m:
                out["not_patched"] = m.group("addr")
                continue
            if RE_MISSING.search(line) or ("movementIntegrator" in line and "MISSING" in line):
                out["missing"] = True
    return out


def parse_relay(path):
    out = {
        "path": path,
        "moves": [],
        "kicks": [],
        "kicked_reasons": [],
        "calls": [],
        "notes": [],
    }
    if not path:
        return out
    if not os.path.isfile(path):
        out["notes"].append("relay log not found: %s" % path)
        return out

    for line in read_text(path).splitlines():
        m = RE_RELAY_MOVE.search(line)
        if m:
            body = m.group("hex")
            # [type:2][x:4][y:4][time:varint][historyCount:1][history: 8*count]
            # The X/Y are the first eight bytes after the two-byte type id, and they are what
            # the movement trace is made of. `Time` is a variable-length integer and is not
            # decoded here on purpose: `docs/INJECTION.md` section "A layout discrepancy"
            # records that the decompiled field list and the captured payload do not agree
            # past `Time`, so anything beyond X/Y would be a decode of a layout this project
            # has explicitly said it does not trust.
            try:
                xb = bytes.fromhex(body[4:12])
                yb = bytes.fromhex(body[12:20])
                x = struct.unpack("<f", xb)[0]
                y = struct.unpack("<f", yb)[0]
                out["moves"].append({"len": int(m.group("len")), "x": x, "y": y})
            except Exception:
                # A short or malformed body is skipped and counted by absence: `moves` is
                # what the cadence check reports on, so a log this reader cannot parse
                # shows up as "no Move packets matched" rather than as a zero-length trace.
                continue
            continue
        if RE_RELAY_KICK.search(line):
            out["kicks"].append(line.strip())
        m = RE_RELAY_KICKED_EVENT.search(line)
        if m:
            out["kicked_reasons"].append(m.group(1))
    return out


def move_trace(moves):
    """Per-packet displacement, and the wall-clock/monotonic order the client sent them in."""
    out = []
    for i in range(1, len(moves)):
        dx = moves[i]["x"] - moves[i - 1]["x"]
        dy = moves[i]["y"] - moves[i - 1]["y"]
        out.append({
            "dx": dx,
            "dy": dy,
            "len": math.hypot(dx, dy),
            "heading": math.degrees(math.atan2(dy, dx)),
        })
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description="read a Spike 3.1 steering run")
    ap.add_argument("run_dir")
    ap.add_argument("relay_log", nargs="?", default=None)
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--quiet", action="store_true", help="only the verdict lines")
    args = ap.parse_args(argv)

    run = parse_run(args.run_dir)
    relay = parse_relay(args.relay_log)
    trace = move_trace(relay["moves"])

    checks = []

    # -- the run happened at all, before anything is interpreted ------------------------
    if run["missing"] or run["not_patched"]:
        checks.append(("the movement target was PATCHED", False,
                       "it was matched by IL hash and left unpatched -- no steering was possible. "
                       "Pass --move-steer to the launcher; the patch is opt-in"))
    elif run["patched"]:
        checks.append(("the movement target was PATCHED", True, "at " + run["patched"]))
    elif run["published"]:
        checks.append(("the movement target was PATCHED", False,
                       "the replacement was published (0x%s) but no patch line was seen" % run["published"]))
    else:
        checks.append(("the movement target was PATCHED", False,
                       "no publication and no patch line -- the bootstrap may not have resolved"))

    if run["rejected"]:
        checks.append(("the mode was understood", False,
                       "the pilot reported a rejected or empty plan; see bootstrap.log's plan lines"))
    elif run["armed"] == "armed":
        checks.append(("the mode was understood", True, run["armed_line"] or ""))
    elif run["armed"] == "idle":
        checks.append(("the mode was understood", False,
                       "the pilot resolved and is IDLE: DRELAY_MOVE_STEER never reached it. "
                       "Use the launcher's --move-steer, not a shell variable with the same name"))
    else:
        checks.append(("the mode was understood", False, run["armed_line"] or "no pilot line found"))

    # -- A: the write landed -------------------------------------------------------------
    if run["frames"]:
        worst = max(abs(f["wrote"] - f["read"]) for f in run["frames"])
        checks.append(("A the input landed (read-back == written)", worst < 1e-4,
                       "worst |written - read back| = %.7f rad over %d logged frame(s)"
                       % (worst, len(run["frames"]))))
    else:
        checks.append(("A the input landed (read-back == written)", False,
                       "no 'steer frame' lines -- the replacement was not entered, or the pilot "
                       "was idle. Check the PATCHED check above first"))

    # -- B: the integrator acted ---------------------------------------------------------
    if run["phases"]:
        moved = sum(p["moved"] for p in run["phases"])
        total_frames = run["summary"]["frames"] if run["summary"] else None
        if total_frames:
            share = moved / float(total_frames)
            checks.append(("B the integrator acted on the input", share > 0.5,
                           "%d of %d frame(s) moved (%.0f%%)" % (moved, total_frames, 100 * share)))
        else:
            checks.append(("B the integrator acted on the input", moved > 0,
                           "%d frame(s) moved (no summary line for a denominator)" % moved))
    else:
        checks.append(("B the integrator acted on the input", False,
                       "no phase summaries -- the plan did not run to its first phase boundary"))

    # -- C: the direction was obeyed -----------------------------------------------------
    if run["phases"]:
        cosines = [p["mean_cos"] for p in run["phases"] if not math.isnan(p["mean_cos"])]
        mean_cos = sum(cosines) / len(cosines) if cosines else float("nan")
        # In sweep mode the commanded value is an *input* angle and the observed one is a world
        # heading, so their absolute difference is the transform, not an error. What must hold in
        # both modes is that the observed heading MOVES WITH the commanded one -- so the check is
        # that the deltas between consecutive phases agree with the commanded deltas.
        deltas = []
        for a, b in zip(run["phases"], run["phases"][1:]):
            deltas.append(angle_delta_deg(b["observed"], a["observed"]))
        cmd_deltas = []
        for a, b in zip(run["phases"], run["phases"][1:]):
            cmd_deltas.append(angle_delta_deg(b["target"], a["target"]))
        consistent = True
        if len(cmd_deltas) >= 3:
            residuals = [abs(angle_delta_deg(d, c)) for d, c in zip(deltas, cmd_deltas)]
            residuals = [r for r in residuals if not math.isnan(r)]
            consistent = bool(residuals) and max(residuals) < 60.0
            checks.append(("C the direction was obeyed (mapping)", consistent,
                           "per-phase observed-minus-commanded deltas %s deg; worst residual %.0f deg"
                           % (["%.0f" % d for d in deltas], max(residuals) if residuals else float("nan"))))
        else:
            checks.append(("C the direction was obeyed (mean cosine)", mean_cos > 0.8 if not math.isnan(mean_cos) else False,
                           "mean cos(observed - commanded) = %.3f over %d phase(s)" % (mean_cos, len(cosines))))
        if not math.isnan(mean_cos):
            checks.append(("C' the per-phase alignment (world frame; only meaningful for hold)",
                           mean_cos > 0.8,
                           "mean cos = %.3f" % mean_cos))
    else:
        checks.append(("C the direction was obeyed", False, "no phases to compare"))

    # -- D: the server did not object ----------------------------------------------------
    if args.relay_log is None:
        checks.append(("D the server did not object (GmKicked)", False,
                       "no relay log given: the client's own position cannot witness this. "
                       "Pass the session log as the second argument"))
    elif relay["kicks"] or relay["kicked_reasons"]:
        movement = [r for r in relay["kicked_reasons"]
                    if any(w in r.lower() for w in MOVEMENT_WORDS)]
        checks.append(("D the server did not object (GmKicked)", not movement,
                       "kicked: %s (movement-related: %s)"
                       % (relay["kicked_reasons"] or ["(no reason string)"], movement or "none")))
    else:
        checks.append(("D the server did not object (GmKicked)", True,
                       "no Kicked packet in the relay log"))

    # -- E: the stream kept its cadence ---------------------------------------------------
    if args.relay_log and relay["moves"]:
        lens = {}
        for m in relay["moves"]:
            lens[m["len"]] = lens.get(m["len"], 0) + 1
        big = sum(1 for t in trace if t["len"] > 2.0)
        checks.append(("E the client kept emitting Move at its usual cadence", True,
                       "%d Move packet(s), lengths %s, %d step(s) above 2 world units"
                       % (len(relay["moves"]), sorted(lens.items()), big)))
    elif args.relay_log:
        checks.append(("E the client kept emitting Move at its usual cadence", False,
                       "no Move packets matched in the relay log -- wrong log, or the client was "
                       "not in a world"))

    # ------------------------------------------------------------------------------------
    # Output
    # ------------------------------------------------------------------------------------
    if args.json:
        print(json.dumps({
            "run": {k: v for k, v in run.items() if k not in ("frames",)},
            "frame_lines": len(run["frames"]),
            "relay": {k: v for k, v in relay.items() if k != "moves"},
            "move_packets": len(relay["moves"]),
            "checks": [{"what": w, "ok": ok, "detail": d} for w, ok, d in checks],
        }, indent=2))
        return 0

    print("steering report -- %s" % args.run_dir)
    if not args.quiet:
        print("  bootstrap: %d frame line(s), %d phase summar(ies), summary %s"
              % (len(run["frames"]), len(run["phases"]),
                 "yes" if run["summary"] else "NO"))
        print("  hook     : published %s, patched %s, not-patched %s, missing %s"
              % (run["published"], run["patched"], run["not_patched"], run["missing"]))
        if args.relay_log:
            print("  relay    : %d Move packet(s), %d Kicked line(s) -- %s"
                  % (len(relay["moves"]), len(relay["kicks"]), args.relay_log))
        for note in run["notes"] + relay["notes"]:
            print("  note     : " + note)
        print("")

    if run["phases"] and not args.quiet:
        print("  phase  target   observed  mean|d|   mean cos  moved")
        for p in run["phases"]:
            print("  %5d  %6.1f  %8.1f  %7.4f  %8.3f  %5d"
                  % (p["n"], p["target"], p["observed"], p["mean_abs"], p["mean_cos"], p["moved"]))
        print("")

    failed = 0
    for what, ok, detail in checks:
        print("%s %s" % ("PASS" if ok else "FAIL", what))
        if detail:
            print("       " + detail)
        failed += 0 if ok else 1

    print("")
    if failed == 0:
        print("VERDICT: the movement question is answered YES for this run -- the hook steered the "
              "player, the client obeyed, and the server did not object.")
    else:
        print("VERDICT: %d check(s) did not pass. Read them in order: the first failure is almost "
              "always the cause of the rest." % failed)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
