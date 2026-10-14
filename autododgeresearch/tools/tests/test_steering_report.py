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
[INFO ] managed movement replacement published at 0x7FFF9ADD8C90; the native side jumps straight into it only when --move-steer asked for it
{patched}
[INFO ] phase 1 complete
"""

PATCHED = "[INFO ] PATCHED 'movementIntegrator' at 0x7FFF9C112233 (trampoline 0x7FFF9A850F40)"
NOT_PATCHED = ("[INFO ] target 'movementIntegrator' verified at 0x7FFF9C112233 and NOT patched: "
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


def run_report(d, relay):
    proc = subprocess.run([sys.executable, REPORT, d, relay],
                          capture_output=True, text=True)
    return proc.returncode, proc.stdout + proc.stderr


def main():
    root = tempfile.mkdtemp(prefix="steering_fixture_")
    failures = []

    def check(what, ok, detail=""):
        print("%s %s%s" % ("PASS" if ok else "FAIL", what, ("  -- " + detail) if detail else ""))
        if not ok:
            failures.append(what)

    try:
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

        # A run with no steering at all must not read as a healthy one.
        d, _ = write_case(root, "empty", "", "", "")
        code, out = run_report(d, os.path.join(d, "relay.log"))
        check("empty: exits non-zero", code != 0, "exit %d" % code)
        check("empty: says the replacement was never entered",
              "no 'steer frame' lines" in out)
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
