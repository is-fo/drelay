# Build synthetic relay logs and check that move_budget.py measures what it claims.
#
# THE CHECK THAT MATTERS MOST IS THE CLOCK VALIDATION, because it is the only thing standing
# between a mis-decoded varint and a confident wrong speed. So the fixture's third case
# deliberately encodes a `Time` field that is off by a factor of two and asserts that the
# tool refuses to report a speed rather than reporting one.
#
# The other cases are the measurement itself: a log with known walking steps must give those
# steps back, and a log with a teleport in it must exclude the teleport (the tool's own
# teleport cut is what keeps a zone change from doubling the budget).
#
# Usage: python tools/tests/test_move_budget.py

import math
import os
import struct
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.dirname(HERE)
BUDGET = os.path.join(TOOLS, "move_budget.py")


def varint(value):
    """The client's varint encoder -- the inverse of ClientPackets.Reader.varintOrNull."""
    negative = value < 0
    value = abs(value)
    first = value & 0x3F
    if negative:
        first |= 0x40
    rest = value >> 6
    out = bytearray()
    if rest:
        first |= 0x80
    out.append(first)
    shift = 6
    while rest:
        chunk = rest & 0x7F
        rest >>= 7
        if rest:
            chunk |= 0x80
        out.append(chunk)
    return bytes(out)


def move_line(wall, x, y, t, history=0):
    body = struct.pack("<ff", x, y) + varint(t) + bytes([history])
    body += b"\x00" * (history * 8)
    payload = b"\x4a\x00" + body
    return "[Game#2 C->S] %s len=%d id=0x4A Move %s\n" % (wall, len(payload), payload.hex().upper())


def clock(seconds):
    h = int(seconds // 3600) % 24
    m = int((seconds % 3600) // 60)
    s = seconds % 60
    return "%02d:%02d:%06.3f" % (h, m, s)


def walking_log(path, steps, step_len=1.4, tick_ms=200, t_scale=1.0):
    """
    A straight walk: each packet moves `step_len` along +x, `tick_ms` of client time apart.
    `t_scale` multiplies the ENCODED time so a case can deliberately mis-encode it.
    """
    x = 100.0
    t = 1000
    wall = 3600.0
    lines = []
    for _ in range(steps):
        lines.append(move_line(clock(wall), x, 100.0, int(t * t_scale)))
        x += step_len
        t += tick_ms
        wall += tick_ms / 1000.0
    with open(path, "w", encoding="utf-8") as fh:
        fh.writelines(lines)
    return step_len, tick_ms


def build_budget_argv(path, extra=()):
    return [sys.executable, BUDGET, path] + list(extra)


def run(path, extra=()):
    proc = subprocess.run(build_budget_argv(path, extra), capture_output=True, text=True)
    return proc.returncode, proc.stdout + proc.stderr


def main():
    root = tempfile.mkdtemp(prefix="move_budget_fixture_")
    failures = []

    def check(what, ok, detail=""):
        if isinstance(detail, (list, tuple)):
            detail = " | ".join(str(x) for x in detail)
        print("%s %s%s" % ("PASS" if ok else "FAIL", what, ("  -- " + detail) if detail else ""))
        if not ok:
            failures.append(what)

    try:
        # 1. A plain walk of a known step length.
        p = os.path.join(root, "walk.log")
        step_len, tick_ms = walking_log(p, 300, step_len=1.4, tick_ms=200)
        code, out = run(p)
        check("walk: exits 0", code == 0, "exit %d" % code)
        check("walk: the clock validates", "clock agree on a typical pair: 1.0000" in out)
        # The measured mode should be the step we wrote. A tolerance rather than equality
        # because the first pair has no predecessor and the percentile is interpolated.
        check("walk: the step is recovered",
              "typical moving step       : 1.4000" in out,
              [l.strip() for l in out.splitlines() if "typical moving step" in l][:1])
        check("walk: the tick is recovered", "tick length (median)      : 200 ms" in out)
        check("walk: a speed is reported", "while MOVING: p50" in out)

        # 2. A walk with a zone change in the middle: the jump must be excluded, or the
        #    budget doubles.
        p = os.path.join(root, "jump.log")
        x = 100.0
        t = 1000
        wall = 3600.0
        x, t, wall = 100.0, 1000, 3600.0
        lines = []
        for i in range(200):
            lines.append(move_line(clock(wall), x, 100.0, t))
            x += 1.4
            t += 200
            wall += 0.2
        lines.append(move_line(clock(wall), x + 900.0, 100.0, t))   # the teleport
        t += 200
        wall += 0.2
        x += 900.0
        for i in range(200):
            lines.append(move_line(clock(wall), x, 100.0, t))
            x += 1.4
            t += 200
            wall += 0.2
        with open(p, "w", encoding="utf-8") as fh:
            fh.writelines(lines)
        code, out = run(p)
        check("jump: exits 0", code == 0, "exit %d" % code)
        check("jump: the teleport is excluded", "1 excluded as teleports" in out,
              [l.strip() for l in out.splitlines() if "excluded as teleports" in l][:1])
        check("jump: the walking step is still recovered",
              "typical moving step       : 1.4000" in out)

        # 3. A mis-encoded clock: the tool must REFUSE, not report a wrong speed.
        p = os.path.join(root, "misclock.log")
        walking_log(p, 300, step_len=1.4, tick_ms=200, t_scale=2.0)
        code, out = run(p)
        check("misclock: exits non-zero", code != 0, "exit %d" % code)
        check("misclock: NO speed is reported", "while MOVING: p50" not in out)
        check("misclock: it says why", "mis-decoded" in out or "SPEED WITHHELD" in out)

        # 4. A log with nothing in it must not be read as a zero-speed player.
        p = os.path.join(root, "empty.log")
        open(p, "w", encoding="utf-8").write("[Game#2 S->C] 02:31:53 id=0x46 HealthUpdate 4600\n")
        code, out = run(p)
        check("empty: exits non-zero", code != 0, "exit %d" % code)
        check("empty: says there is no movement", "no usable movement" in out)
    finally:
        import shutil
        shutil.rmtree(root, ignore_errors=True)

    print("")
    if failures:
        print("FIXTURE FAILED -- %d check(s)" % len(failures))
        return 1
    print("FIXTURE OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
