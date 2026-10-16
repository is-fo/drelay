"""Build a synthetic PASSING Phase 3 run, so Run-Phase3Session.ps1's success path is exercised.

Kept as a tool rather than a throwaway because the runner's own verdict is the thing an operator
reads, and a runner whose success path has never been taken is a runner nobody should trust. The
lines are the real log shapes (bare addresses; `[INFO ]` from the native side, `[INFO] boot:` from
the managed side) -- the same lesson the steering-report fixture learned the hard way.
"""

import os
import sys

RUN_LOG = """[INFO ] target table: 5 entries from the bootstrap
[INFO ] managed movement replacement published at 00007FFF9DC9B2D0; the native side jumps straight into it only when --move-steer asked for it
[INFO ] PATCHED 'movementIntegrator' at 00007FFF9EFB3100 (trampoline 00007FFF9D300F80)
[INFO ] 5 of 5 target(s) patched
[INFO ] phase 1 complete
"""

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

RELAY = "".join(
    "[Game#2 C->S] 02:31:%02d.%03d len=22 id=0x4A Move 4A0000%s0000E542A29402010000E742336CE142\n"
    % (i, i * 7, "0000E742")
    for i in range(40)
)


def main(dest):
    os.makedirs(dest, exist_ok=True)
    boot = ["[INFO] boot: resolve[7] offsets resolved; publishing the count\n",
            "[INFO] boot: the steering pilot is ARMED: mode 'sweep:240', 8 phase(s) of 240 frame(s)\n",
            "[INFO] boot: steering plan: 8 phase(s), 240 frame(s) each, target frame the client's own input angle\n"]
    for n, (name, target, observed) in enumerate(PHASES, start=1):
        wrote = target * 3.14159265358979 / 180.0
        boot.append("steer frame %d phase %d cmd %.1fdeg wrote %.4frad read-back %.4frad rot 0.0deg "
                    "pos (100.000,100.000) d (0.0300,0.0300) |d| 0.0424\n"
                    % (n * 240, n, target, wrote, wrote))
        boot.append("steering phase %d (%s): target %.1fdeg, moved on 238 frame(s), observed heading "
                    "%.1fdeg, mean |delta| 0.0424 world units, mean cos(observed - last commanded) 0.970\n"
                    % (n, name, target, observed % 360.0))
    boot.append("STEERING SUMMARY | frames 1920, frames the player moved 1904, "
                "skipped: no-player 0, dead-or-safe 16 | mode 'sweep:240'\n")

    with open(os.path.join(dest, "hook.log"), "w", encoding="utf-8") as fh:
        fh.write(RUN_LOG)
    with open(os.path.join(dest, "bootstrap.log"), "w", encoding="utf-8") as fh:
        fh.write("".join(boot))
    with open(os.path.join(dest, "relay.log"), "w", encoding="utf-8") as fh:
        fh.write(RELAY)
    print(dest)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "passing-run"))
