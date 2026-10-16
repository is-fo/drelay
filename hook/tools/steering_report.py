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
# The address shapes are taken from REAL hook.log lines, not from what looks natural, and
# that distinction cost a false pass. The native log prints addresses bare -- `at
# 00007FFF9C112233` -- while this reader's first version required an `0x` prefix, and the
# fixture that "verified" it used hand-written lines that had one. So the reader was green
# against its own fixture and matched nothing in a real run, which is the worst kind of test:
# one that agrees with the author instead of with the artefact. `0x?` accepts both, and the
# fixture now carries the exact lines a run produces.
RE_PUBLISHED = re.compile(
    r"managed movement replacement published at (?:0x)?(?P<addr>[0-9A-F]{6,})")
RE_PATCHED = re.compile(
    r"PATCHED 'movementIntegrator' at (?:0x)?(?P<addr>[0-9A-F]{6,})")
RE_NOTPATCHED = re.compile(
    r"target 'movementIntegrator' verified at (?:0x)?(?P<addr>[0-9A-F]{6,}) and NOT patched")
# Anchored on the error line's own shape, because the *summary* line also names the target
# (`movementIntegrator FOUND ... if it were MISSING`) and a loose pattern would read a
# successful match as a miss.
RE_MISSING = re.compile(r"^\s*\[ERROR\]\s+movementIntegrator\s+MISSING")
RE_REJECTED = re.compile(r"unrecognised DRELAY_MOVE_STEER mode|the steering pilot is ARMED but")

RE_RELAY_MOVE = re.compile(r"C->S\] .*?len=(?P<len>\d+) id=0x4A Move\s+(?P<hex>[0-9A-Fa-f]+)")
RE_RELAY_KICK = re.compile(r"id=0x(?:B9|185)\b.*?Kicked", re.IGNORECASE)
RE_RELAY_KICKED_EVENT = re.compile(r'"kickedReason"\s*:\s*"([^"]*)"')

# THE ESCAPE, WHICH IS PHASE 3'S FLOOR AND A DIFFERENT EXPERIMENT FROM THE STEERING ONE.
# `GmEscape` is game id 66 (`0x42`) and `GmEscapeCastState` is 290 (`0x122`); both are
# client->server. The server's answer is `EscapeAck` (158, `0x9E`), which is what turns
# "the client sent it" into "the server took it" -- docs/AUTONEXUS.md section 5 is the join.
# The ids come from `src/main/java/networking/packets/GmPacketType.java` rather than from a
# document, so a build that renumbers them fails the reader rather than the run.
RE_RELAY_ESCAPE = re.compile(r"C->S\] .*?id=0x(?:42|122) (?:Escape|EscapeCastState)\b", re.IGNORECASE)
RE_RELAY_ESCAPE_ACK = re.compile(r"S->C\] .*?id=0x9E (?:EscapeAck)\b", re.IGNORECASE)

RE_ESCAPE_ARMED = re.compile(r"escape test armed: the world exists")
RE_ESCAPE_CALLED = re.compile(r"escape test: calling World\.RequestEscape\(\) now")
RE_ESCAPE_RETURNED = re.compile(r"escape test: World\.RequestEscape\(\) RETURNED after (\d+) ms")
RE_ESCAPE_THREW = re.compile(r"escape test: World\.RequestEscape\(\) THREW")
RE_ESCAPE_UNAVAILABLE = re.compile(r"escape test is NOT available")

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


def angle_gap_rad(a, b):
    """How far apart two radians are, as an angle: a full turn is zero, not 2*pi.

    WHY THIS EXISTS, AND IT COST A RUN ITS VERDICT. Check A compares the angle the pilot
    wrote with the angle it read back from the same field, and the client's setter NORMALISES
    what it is given: `20261015-032549-p26684` logged `wrote -2.3562rad read-back 3.9270rad`
    -- a difference of exactly 2*pi, on a frame where the write had landed perfectly. The
    check reported `worst |written - read back| = 6.2832000` and FAILED the run, so a session
    whose actuator worked end to end read as a broken one. A difference of 2*pi in an angle is
    a difference of zero; only a reader that forgets angles wrap can disagree.
    """
    d = (a - b) % (2.0 * math.pi)
    return min(d, (2.0 * math.pi) - d)


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
        "escape": {
            "armed": False,
            "called": False,
            "returned_ms": None,
            "threw": False,
            "unavailable": False,
            "requested": False,
        },
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
            if RE_REJECTED.search(line):
                out["rejected"] = True

    # ---------------------------------------------------------------------------------
    # hook.log -- and the pilot's own status line, which lives HERE and not in the
    # bootstrap log.
    #
    # THAT SPLIT WAS FOUND BY THE FIXTURE, AND IT WOULD HAVE MADE THIS REPORT USELESS.
    # `SteeringPilot.LogPlan` is called from the resolve callback and writes through
    # `HookEntry.AppendLog`, which is the NATIVE log -- so `the steering pilot is ARMED`
    # lands in `hook.log`, while the escape test's status (logged from the same place) also
    # lands there. A reader that only opened `bootstrap.log` would find no pilot line at all
    # and fail every steering run for the wrong reason. Both files are read, and hook.log
    # wins because it is where these lines actually are.
    # ---------------------------------------------------------------------------------
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

    # The status lines, from whichever file carries them. hook.log first; the bootstrap log
    # is scanned only if hook.log did not answer, so a run whose native log is truncated
    # still resolves.
    #
    # THE PUBLISH LINE IS READ HERE AS WELL AS BY hook.log's OWN LOOP, and the duplication is
    # the fix for a bug that hid behind a green fixture. `managed movement replacement
    # published at ...` is written by `SteeringPilot.Publish`, which runs on the resolve
    # callback and therefore logs through `HookEntry` with the `[INFO] boot:` prefix -- so it
    # lands in BOOTSTRAP.log, and a reader that looked for it only in hook.log reported
    # `published None` for every real run while its fixture, which put the line in hook.log,
    # passed. Guarded with `is None` so a native-side occurrence still wins.
    for text in (read_text(hook) if os.path.isfile(hook) else "",
                 read_text(boot) if os.path.isfile(boot) else ""):
        for line in text.splitlines():
            if out["published"] is None:
                m = RE_PUBLISHED.search(line)
                if m:
                    out["published"] = m.group("addr")
            if out["armed"] is None and "steering pilot is" in line:
                out["armed_line"] = line.strip()
                if "NOT available" in line:
                    out["armed"] = "unavailable"
                elif "ARMED" in line:
                    out["armed"] = "armed"
                elif "IDLE" in line:
                    out["armed"] = "idle"
            if RE_ESCAPE_ARMED.search(line):
                out["escape"]["armed"] = True
                out["escape"]["requested"] = True
            if RE_ESCAPE_CALLED.search(line):
                out["escape"]["called"] = True
            m = RE_ESCAPE_RETURNED.search(line)
            if m:
                out["escape"]["returned_ms"] = int(m.group(1))
            if RE_ESCAPE_THREW.search(line):
                out["escape"]["threw"] = True
            if RE_ESCAPE_UNAVAILABLE.search(line):
                out["escape"]["unavailable"] = True
                out["escape"]["requested"] = True

    # AN EMPTY LOG SET IS NOT "A RUN THAT ASKED FOR NOTHING". Both files absent or empty means
    # there is no evidence at all, and `parse_run` must be able to say which of those it is --
    # a run that legitimately asked for neither experiment still has a hook.log full of
    # install lines and a bootstrap.log full of resolve lines.
    hook_text = read_text(hook) if os.path.isfile(hook) else ""
    boot_text = read_text(boot) if os.path.isfile(boot) else ""
    out["unreadable"] = (len(hook_text.strip()) == 0 and len(boot_text.strip()) == 0)
    return out


def parse_relay(path):
    out = {
        "path": path,
        "moves": [],
        "kicks": [],
        "kicked_reasons": [],
        "calls": [],
        "escape_request": 0,
        "escape_ack": 0,
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
        if RE_RELAY_ESCAPE.search(line):
            out["escape_request"] += 1
        if RE_RELAY_ESCAPE_ACK.search(line):
            out["escape_ack"] += 1
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

    # ------------------------------------------------------------------------------------
    # THE TWO EXPERIMENTS ARE INDEPENDENT, AND THE REPORT HAS TO SAY SO.
    #
    # `--move-steer` and `--escape-test` are separate switches on purpose (see
    # PHASE3-SCOPE.md sections 4.6 and 6.3): one steers the player and one calls the client's
    # own nexus entry point. A run that asked for only one of them must not be failed for not
    # doing the other -- and the failure mode that creates is not cosmetic, because it would
    # report a perfectly good escape-floor result as a failed steering run. So the steering
    # block runs only when a steering plan was actually armed, and the escape block only when
    # an escape test was actually requested.
    # ------------------------------------------------------------------------------------
    steering_requested = run["armed"] == "armed"
    if not steering_requested:
        if run["unreadable"]:
            checks.append(("the run's logs are readable", False,
                           "hook.log and bootstrap.log are both empty or missing in %s -- "
                           "nothing can be concluded from this run" % args.run_dir))
        elif run["armed"] is None:
            # No pilot status line, but the logs exist: this is what a run without
            # `--move-steer` looks like, and it is not a failure. The movement target is still
            # published and verified on every run; only the patch is opt-in.
            checks.append(("this run did not ask for steering", True,
                           "no pilot status line, which is what a run without --move-steer "
                           "looks like"))
        else:
            checks.append(("this run did not ask for steering", True,
                           run["armed_line"] or "pilot is idle"))
    else:
        # -- the run happened at all, before anything is interpreted ----------------------
        if run["not_patched"]:
            checks.append(("the movement target was PATCHED", False,
                           "it was matched by IL hash and left unpatched -- no steering was possible"))
        elif run["missing"]:
            checks.append(("the movement target was PATCHED", False,
                           "the movement target was never compiled in this run, so nothing could "
                           "have been patched; the pilot cannot have written anything"))
        elif run["patched"]:
            checks.append(("the movement target was PATCHED", True, "at " + run["patched"]))
        elif run["published"]:
            checks.append(("the movement target was PATCHED", False,
                           "the replacement was published (0x%s) but no patch line was seen"
                           % run["published"]))
        else:
            checks.append(("the movement target was PATCHED", False,
                           "no publication and no patch line -- the bootstrap may not have resolved"))

        if run["rejected"]:
            checks.append(("the mode was understood", False,
                           "the pilot reported a rejected or empty plan; see the plan lines"))
        else:
            checks.append(("the mode was understood", True, run["armed_line"] or ""))

    # -- A: the write landed -------------------------------------------------------------
    if steering_requested:
        if run["frames"]:
            worst = max(angle_gap_rad(f["wrote"], f["read"]) for f in run["frames"])
            # THE TOLERANCE IS THE LOG'S OWN RESOLUTION, NOT A NUMBER PICKED TO PASS. Both angles
            # are printed to four decimals, so two numbers that ARE the same angle may differ by up
            # to 1e-4 after rounding -- measured on `20261015-032549-p26684`, where the worst gap is
            # 8.53e-05 on `wrote 3.0252 read 9.3083`: 9.3083 - 3.0252 = 6.2831 against a true 2*pi
            # of 6.28319, and the residual is the printout. A threshold of 1e-4 sits exactly on that
            # rounding and would fail this run or pass it depending on which frames were logged.
            # 5e-4 is one order above the instrument and four below any real disagreement: a write
            # that did not land is out by a whole angle, not by a fifth of a degree.
            checks.append(("A the input landed (read-back == written, mod 2pi)", worst < 5e-4,
                           "worst |written - read back| = %.7f rad over %d logged frame(s) "
                           "(an angle: a full turn counts as zero; the log's own resolution is 1e-4)"
                           % (worst, len(run["frames"]))))
        else:
            checks.append(("A the input landed (read-back == written)", False,
                           "no 'steer frame' lines -- the replacement was not entered. Check the "
                           "PATCHED check above first"))

    # -- B: the integrator acted ---------------------------------------------------------
    if steering_requested:
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
    if steering_requested:
        if run["phases"]:
            cosines = [p["mean_cos"] for p in run["phases"] if not math.isnan(p["mean_cos"])]
            mean_cos = sum(cosines) / len(cosines) if cosines else float("nan")
            # In sweep mode the commanded value is an *input* angle and the observed one is a
            # world heading, so their absolute difference is the transform, not an error. What
            # must hold in both modes is that the observed heading MOVES WITH the commanded one
            # -- so the check is that the deltas between consecutive phases agree with the
            # commanded deltas.
            deltas = []
            for a, b in zip(run["phases"], run["phases"][1:]):
                deltas.append(angle_delta_deg(b["observed"], a["observed"]))
            cmd_deltas = []
            for a, b in zip(run["phases"], run["phases"][1:]):
                cmd_deltas.append(angle_delta_deg(b["target"], a["target"]))
            if len(cmd_deltas) >= 3:
                residuals = [abs(angle_delta_deg(d, c)) for d, c in zip(deltas, cmd_deltas)]
                residuals = [r for r in residuals if not math.isnan(r)]
                consistent = bool(residuals) and max(residuals) < 60.0
                checks.append(("C the direction was obeyed (mapping)", consistent,
                               "per-phase observed-minus-commanded deltas %s deg; worst residual "
                               "%.0f deg"
                               % (["%.0f" % d for d in deltas],
                                  max(residuals) if residuals else float("nan"))))
            else:
                checks.append(("C the direction was obeyed (mean cosine)",
                               mean_cos > 0.8 if not math.isnan(mean_cos) else False,
                               "mean cos(observed - commanded) = %.3f over %d phase(s)"
                               % (mean_cos, len(cosines))))
            if not math.isnan(mean_cos):
                checks.append(("C' the per-phase alignment (world frame; only meaningful for hold)",
                               mean_cos > 0.8,
                               "mean cos = %.3f" % mean_cos))
        else:
            checks.append(("C the direction was obeyed", False, "no phases to compare"))

    # -- D and E: the wire, which both experiments share -----------------------------------
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

    # -- F: the escape floor, when an operator asked for it --------------------------------
    #
    # THIS IS A DIFFERENT EXPERIMENT FROM THE STEERING ONE, and it is allowed to be the only
    # thing a run did. `--escape-test` calls the client's own `World.RequestEscape()` once,
    # which is Phase 3's floor (DODGE-ALGORITHM.md section 7). What makes it worth a check of
    # its own is that the call has never been made: the 2026-10-13 run reported `escape
    # MISSING never compiled`, because nothing ever called it. So the four states below are
    # four different results, and collapsing them would lose the whole point:
    #
    #   never armed       the world never existed for the delay, or the frame replacement
    #                     was not installed -- the test did not run
    #   armed, not called the clock started but the delay had not elapsed when the run ended
    #   called, returned  the call was made and the client's own gates ran; whether it SENT
    #                     `GmEscape` is the wire check below
    #   called, threw     the call was made and the client refused it -- still not "never
    #                     reached it", which is why EscapeTest latches and logs before the call
    esc = run["escape"]
    if not esc["requested"]:
        checks.append(("F the escape floor (not requested this run)", True,
                       "no --escape-test: World.RequestEscape was not called by the hook"))
    else:
        if esc["unavailable"]:
            checks.append(("F the escape request was understood", False,
                           "the hook could not resolve World.RequestEscape on this build; see "
                           "bootstrap.log for the reason"))
        elif esc["called"]:
            checks.append(("F the escape call was MADE and RETURNED", esc["returned_ms"] is not None,
                           "World.RequestEscape() returned after %s ms"
                           % (esc["returned_ms"] if esc["returned_ms"] is not None
                              else "? -- the call was reached but its return line is missing")))
        elif esc["armed"]:
            checks.append(("F the escape call was MADE and RETURNED", False,
                           "the test armed and the delay had not elapsed when the run ended; "
                           "raise --live-seconds or lower --escape-test"))
        else:
            checks.append(("F the escape call was MADE and RETURNED", False,
                           "the test never armed -- no world existed, or the frame replacement "
                           "was not installed (--escape-test implies --frame-replacement)"))

        if esc["threw"]:
            checks.append(("F' the client accepted the call", False,
                           "World.RequestEscape() threw -- see bootstrap.log"))
        # The wire half. Only meaningful when the call happened and a relay log was given,
        # because the client sending nothing is a legitimate outcome in a safe area.
        if args.relay_log is None:
            checks.append(("F'' the escape reached the wire", False,
                           "no relay log given: whether the client sent GmEscape, started a "
                           "cast or did nothing is a wire fact and cannot be read in-process. "
                           "Pass the session log as the second argument"))
        elif esc["called"]:
            checks.append(("F'' the escape reached the wire (GmEscape / cast state)",
                           relay["escape_request"],
                           "GmEscape (0x42) or GmEscapeCastState (0x122) seen %d time(s); "
                           "EscapeAck (0x9E) seen %d time(s)"
                           % (relay["escape_request"], relay["escape_ack"])))
        else:
            checks.append(("F'' the escape reached the wire", False,
                           "the call never happened, so nothing can have reached the wire"))

    # ------------------------------------------------------------------------------------
    # Output
    # ------------------------------------------------------------------------------------
    if args.json:
        print(json.dumps({
            "run": {k: v for k, v in run.items() if k not in ("frames",)},
            "frame_lines": len(run["frames"]),
            "relay": {k: v for k, v in relay.items() if k != "moves"},
            "move_packets": len(relay["moves"]),
            "escape": run["escape"],
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
            print("  relay    : %d Move packet(s), %d escape request(s), %d escape ack(s), "
                  "%d Kicked line(s) -- %s"
                  % (len(relay["moves"]), relay["escape_request"], relay["escape_ack"],
                     len(relay["kicks"]), args.relay_log))
            if relay["escape_request"] > 1:
                # THE HANDSHAKE ALSO ASKS TO ESCAPE, ONCE, WITH AN EMPTY `GmEscape` at
                # login. So a count above one is not automatically a defect -- but it is
                # the first thing to look at, because "the hook escaped twice" and "the
                # login handshake plus the hook" are different results with the same total.
                print("             note: more than one escape request; the empty `GmEscape` at "
                      "login is one of them, so attribute the extra one before concluding")
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

    # The two experiments are independent, so a run that answered one of them is not a failed
    # run. Saying so explicitly stops the escape checks' failures from reading as a verdict on
    # the steering, and the other way round.
    if run["escape"]["requested"] and run["escape"]["called"] and not run["escape"]["threw"]:
        print("       the ESCAPE test was reached and returned -- see the F'' line for whether "
              "it left the machine")
    if run["phases"]:
        print("       the STEERING test produced %d phase(s)" % len(run["phases"]))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
