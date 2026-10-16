# Build a synthetic Spike 3.1 run and check that steering_report.py reads it.
#
# THIS FIXTURE EXISTS BECAUSE THE REPORT'S FAILURE MODE IS A FALSE PASS.  It reads three
# logs by regex, and a regex that matches nothing produces "no lines of this kind" --
# which, if that were treated as a zero, would print a table of zeros and a verdict about
# a run that never happened.  So the fixture has three cases and asserts all three:
#
#   good      a healthy sweep: read-back equal, movement in every phase, observed headings
#             a quarter-turn apart, no kick.  Every check must PASS.
#   unpatched the same steering log with a hook.log that shows the target matched and NOT
#             patched -- what an operator who forgot --move-steer would produce.  It must
#             fail, and it must fail on that check specifically.
#   kicked    a healthy sweep with a movement kick in the relay log.  Only check D may fail.
#
# Usage: python tools/tests/test_steering_report.py

import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.dirname(HERE)
ROOT = os.path.dirname(TOOLS)
REPORT = os.path.join(TOOLS, "steering_report.py")

RUN_LOG = """[INFO ] target table: 5 entries from the bootstrap
[INFO ] managed movement replacement published at 00007FFF9DC9B2D0; the native side jumps straight into it only when --move-steer asked for it
{patched}
[INFO ] phase 1 complete
"""

PATCHED = "[INFO ] PATCHED 'movementIntegrator' at 00007FFF9EFB3100 (trampoline 00007FFF9D300F80)"
NOT_PATCHED = ("[INFO ] target 'movementIntegrator' verified at 00007FFF9EFB14A0 and NOT patched: "
               "its detour body is Phase 2's (this spike patches one target on purpose)")

BOOT_HEAD = """[INFO] boot: resolve[7] offsets resolved; publishing the count
[INFO] boot: the steering pilot is ARMED: mode 'sweep:240', 8 phase(s) of 240 frame(s)
"""

# Eight phases, one quarter-turn apart, each with a healthy cosine and movement on most frames.
PHASES = [
    ("input 0deg", 0.0, 45.0),
    ("input 45deg", 45.0, 90.0),
    ("input 90deg", 90.0, 135.0),
    ("input 135deg", 135.0, 180.0),
    ("input 180deg", 180.0, 225.0),
    ("input 225deg", 225.0, 270.0),
    ("input 270deg", 270.0, 315.0),
    ("input 315deg", 315.0, 360.0),
]


def build_boot(good=True):
    out = [BOOT_HEAD]
    for n, (name, target, observed) in enumerate(PHASES, start=1):
        cmd = target
        wrote = cmd * 3.14159265358979 / 180.0
        out.append(
            "steer frame %d phase %d cmd %.1fdeg wrote %.4frad read-back %.4frad rot 0.0deg "
            "pos (100.000,100.000) d (0.0300,0.0300) |d| 0.0424\n"
            % (n * 240, n, cmd, wrote, wrote))
        observed = observed % 360.0
        mean_cos = 0.97 if good else 0.10
        out.append(
            "steering phase %d (%s): target %.1fdeg, moved on 238 frame(s), observed heading %.1fdeg, "
            "mean |delta| 0.0424 world units, mean cos(observed - last commanded) %.3f\n"
            % (n, name, target, observed, mean_cos))
    out.append("STEERING SUMMARY | frames 1920, frames the player moved 1904, "
               "skipped: no-player 0, dead-or-safe 16 | mode 'sweep:240'\n")
    return "".join(out)


RELAY_GOOD = "".join(
    "[Game#2 C->S] 02:31:54.%03d len=14 id=0x4A Move                   4A00%s0000E542BF8D0200\n"
    % (i, "0000E742")
    for i in range(0, 20)
)

RELAY_KICK = RELAY_GOOD + (
    "[Game#2 S->C] 02:31:55.100 len=32 id=0xB9 Kicked  B9000D4D6F76656D656E74206861636B\n"
    '{"kind":"packet","id":185,"decoded":{"kickedReason":"Movement hack detected"}}\n'
)

# A RELAY LOG WITH THE ESCAPE ON IT. `GmEscape` is `0x42` and the server's answer is
# `EscapeAck`, `0x9E`; both ids come from `GmPacketType.java`. The empty `GmEscape` at login
# is included on purpose, because it is the reason a login-time escape is not evidence --
# the fixture has to look like a real log for the count to mean anything. The Move stream
# is carried too: a client that is escaping still walks, and a log without it would fail the
# cadence check for a reason that has nothing to do with the escape.
RELAY_ESCAPE = (
    "[Game#2 C->S] 02:31:52.900 len=2 id=0x42 Escape                 4200\n"
    "[Game#2 S->C] 02:31:53.100 len=2 id=0x9E EscapeAck              9E00\n"
) + RELAY_GOOD + (
    "[Game#2 C->S] 02:34:10.500 len=2 id=0x42 Escape                 4200\n"
    "[Game#2 S->C] 02:34:10.740 len=2 id=0x9E EscapeAck              9E00\n"
)

# AN ESCAPE-ONLY RUN: no `--move-steer`, so no pilot line and no phases.  The fixture must
# not carry the pilot's "ARMED" status here, because a run that asked for the escape and not
# for steering is exactly the case where reporting a failed steering test would be wrong.
BOOT_ESCAPE = (
    "[INFO] boot: resolve[7] offsets resolved; publishing the count\n"
    "[INFO] boot: escape test is ARMED: World.RequestEscape() will be called ONCE, "
    "25 s after the world first exists\n"
    "[INFO] boot: escape test armed: the world exists; calling World.RequestEscape() once in 25 s\n"
    "[INFO] boot: escape test: calling World.RequestEscape() now (world for 25031 ms, 1502 frame(s)) "
    "-- this line precedes the call so that a fault inside it still leaves the attempt on record\n"
    "[INFO] boot: escape test: World.RequestEscape() RETURNED after 25031 ms. The client's own "
    "gates ran; whether it sent GmEscape, started an Impermanence cast or did nothing is a wire "
    "fact -- read the relay log for id 0x56 (GmEscape) or the escape-cast-state pair\n"
)

# The delay had not elapsed when the run ended: armed, never called.
BOOT_ESCAPE_TOO_SOON = (
    "[INFO] boot: resolve[7] offsets resolved; publishing the count\n"
    "[INFO] boot: escape test is ARMED: World.RequestEscape() will be called ONCE, "
    "900 s after the world first exists\n"
    "[INFO] boot: escape test armed: the world exists; calling World.RequestEscape() once in 900 s\n"
)


def write_case(root, name, boot, hook, relay):
    d = os.path.join(root, name)
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, "bootstrap.log"), "w", encoding="utf-8") as fh:
        fh.write(boot)
    with open(os.path.join(d, "hook.log"), "w", encoding="utf-8") as fh:
        fh.write(hook)
    relay_path = os.path.join(d, "relay.log")
    with open(relay_path, "w", encoding="utf-8") as fh:
        fh.write(relay)
    return d, relay_path


def run_report(d, relay=None):
    argv = [sys.executable, REPORT, d]
    if relay:
        argv.append(relay)
    proc = subprocess.run(argv, capture_output=True, text=True)
    if os.environ.get("STEERING_FIXTURE_VERBOSE") == "1":
        print("---- %s (exit %d) ----" % (d, proc.returncode))
        print(proc.stdout)
        print(proc.stderr)
        print("---- end ----")
    return proc.returncode, proc.stdout + proc.stderr


def main():
    root = tempfile.mkdtemp(prefix="steering_fixture_")
    failures = []

    def check(what, ok, detail=""):
        print("%s %s%s" % ("PASS" if ok else "FAIL", what, ("  -- " + detail) if detail else ""))
        if not ok:
            failures.append(what)

    try:
        # ---------------------------------------------------------------------------------
        # FIRST, THE REAL LINES. Golden lines captured verbatim from the operator's run of
        # 2026-10-14 (`tools/tests/golden/hook-log-lines.txt`), because the fixture's own
        # hand-written lines once had an `0x` prefix the native log does not print -- so the
        # reader was green here and matched nothing in a real run. A fixture is only as good
        # as its resemblance to the artefact, and hand-writing that resemblance is how it
        # drifts. Lines prefixed `PILOT:` are outside the golden set (they are the pilot's
        # own lines from a run that reached phase 1, appended here so the golden case can
        # exercise the steering checks too) and are dropped for the patch-only assertion.
        # ---------------------------------------------------------------------------------
        golden_path = os.path.join(HERE, "golden", "hook-log-lines.txt")
        golden_all = open(golden_path, encoding="utf-8").read()
        golden = "\n".join(line for line in golden_all.splitlines()
                           if not line.startswith("PILOT:")).strip() + "\n"
        d = os.path.join(root, "golden")
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, "hook.log"), "w", encoding="utf-8") as fh:
            fh.write(golden)
        with open(os.path.join(d, "bootstrap.log"), "w", encoding="utf-8") as fh:
            fh.write("")
        code, out = run_report(d, None)
        # The assertion is that the ADDRESSES WERE READ, not that a steering plan ran: the
        # golden lines are a patch line and a publish line, and a report that cannot parse
        # them is a report that would call any real run "never patched". The address is
        # printed in the parse summary, so its presence is the check.
        check("golden: the patch address is read",
              "patched 00007FFF9EFB3100" in out,
              "the golden lines are the operator's own (%d line(s)); a reader that cannot see "
              "them cannot see any run" % len(golden.strip().splitlines()))

        d, r = write_case(root, "good", build_boot(True), RUN_LOG.format(patched=PATCHED), RELAY_GOOD)
        code, out = run_report(d, r)
        check("good: exits 0", code == 0, "exit %d" % code)
        check("good: every check passes", "VERDICT: the movement question is answered YES" in out)
        check("good: the patch was seen", "PASS the movement target was PATCHED" in out)
        check("good: read-back agreed", "PASS A the input landed" in out)
        check("good: the server did not object", "PASS D the server did not object" in out)

        d, r = write_case(root, "unpatched", build_boot(True),
                          RUN_LOG.format(patched=NOT_PATCHED), RELAY_GOOD)
        code, out = run_report(d, r)
        check("unpatched: exits non-zero", code != 0, "exit %d" % code)
        check("unpatched: fails on the patch check",
              "FAIL the movement target was PATCHED" in out)
        check("unpatched: still reads the phase table", "238" in out)

        d, r = write_case(root, "kicked", build_boot(True), RUN_LOG.format(patched=PATCHED), RELAY_KICK)
        code, out = run_report(d, r)
        check("kicked: exits non-zero", code != 0, "exit %d" % code)
        check("kicked: the kick is named with its reason",
              "Movement hack detected" in out)
        check("kicked: only the wire check fails",
              out.count("FAIL ") == 1, "FAIL count = %d" % out.count("FAIL "))

        # A run with no evidence at all must not read as a healthy one.
        d, _ = write_case(root, "empty", "", "", "")
        code, out = run_report(d, os.path.join(d, "relay.log"))
        check("empty: exits non-zero", code != 0, "exit %d" % code)
        check("empty: says there is nothing to read",
              "logs are readable" in out and "nothing can be concluded" in out)

        # ---------------------------------------------------------------------------------
        # The escape floor: a run whose only experiment was `--escape-test`.
        # ---------------------------------------------------------------------------------
        d, r = write_case(root, "escape", BOOT_ESCAPE, RUN_LOG.format(patched=PATCHED), RELAY_ESCAPE)
        code, out = run_report(d, r)
        check("escape: exits 0", code == 0, "exit %d" % code)
        check("escape: the call was reached and returned",
              "PASS F the escape call was MADE and RETURNED" in out)
        check("escape: it reached the wire",
              "PASS F'' the escape reached the wire" in out)
        check("escape: the ack is counted", "2 escape ack(s)" in out)
        check("escape: more than one request is flagged for attribution",
              "more than one escape request" in out)

        # Armed but the delay never elapsed -- a different result from "never reached it",
        # and the report has to say which one it is.
        d, r = write_case(root, "escape-too-soon", BOOT_ESCAPE_TOO_SOON,
                          RUN_LOG.format(patched=PATCHED), RELAY_ESCAPE)
        code, out = run_report(d, r)
        check("escape-too-soon: exits non-zero", code != 0, "exit %d" % code)
        check("escape-too-soon: says the delay had not elapsed",
              "the delay had not elapsed" in out)
        check("escape-too-soon: does not claim the call happened",
              "PASS F the escape call was MADE and RETURNED" not in out)

        # A steering-only run must not be failed for not escaping.
        d, r = write_case(root, "no-escape-asked", build_boot(True),
                          RUN_LOG.format(patched=PATCHED), RELAY_GOOD)
        code, out = run_report(d, r)
        check("a steering run is not failed for not escaping",
              "PASS F the escape floor (not requested this run)" in out)
        check("a steering run still exits 0", code == 0, "exit %d" % code)
    finally:
        shutil.rmtree(root, ignore_errors=True)

    print("")
    if failures:
        print("FIXTURE FAILED -- %d check(s)" % len(failures))
        return 1
    print("FIXTURE OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
