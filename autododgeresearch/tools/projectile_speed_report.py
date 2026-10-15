"""How fast are the recorded projectiles, and how far does each move in the 50 ms decision window?

The question HANDOFF-DIRECTED-DODGE's follow-up asks: the decision window is a FIXED 50 ms, so the
distance it protects is `50ms * bullet_speed` -- 0.13 units for a slow bullet, 1.2 for a 24 u/s one
-- against a collision disc of `player_radius + bullet_radius`. This prints both, per distinct
projectile in a trace, so the over-refusal can be sized rather than argued about.
"""
import json
import math
import os
import sys
import collections

WINDOW_MS = 50.0


def projectiles(run_dir):
    """One entry per (id, st), from the FIRST frame it appears in: speed, radius, sample step."""
    seen = {}
    with open(os.path.join(run_dir, "trace.jsonl"), "r", encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            if not line.startswith('{"t":"F"'):
                continue
            try:
                f = json.loads(line)
            except Exception:
                continue
            pr = f.get("r") or 0.25
            for t in f.get("threats", []):
                key = (t["id"], t["st"])
                if key in seen:
                    continue
                xs, ys, alive = t.get("xs") or [], t.get("ys") or [], t.get("alive") or []
                n = min(len(xs), len(ys), t.get("n", 0))
                if n < 3:
                    continue
                # the fastest inter-knot speed over the alive prefix: a projectile that decelerates
                # is fastest at birth, and the decision window is about the near field
                steps = []
                for i in range(1, n):
                    if alive[i] and alive[i - 1]:
                        steps.append(math.hypot(xs[i] - xs[i - 1], ys[i] - ys[i - 1]))
                if not steps:
                    continue
                step_ms = 30.0
                v = max(steps) / (step_ms / 1000.0)
                seen[key] = (v, t.get("rad", 0.0), pr)
    return seen


def main(run_dirs):
    allv = []
    for d in run_dirs:
        name = os.path.basename(d.rstrip("\\/"))
        proj = projectiles(d)
        if not proj:
            continue
        moving = {k: t for k, t in proj.items() if t[0] > 1e-3}
        stationary = len(proj) - len(moving)
        speeds = sorted(v for v, _, _ in moving.values())
        allv += speeds
        # over-refusal factor: how much more of the path is refused than the disc itself
        factors = []
        for v, rad, pr in moving.values():
            disc = rad + pr
            protected = v * (WINDOW_MS / 1000.0)
            if disc > 1e-6:
                factors.append(protected / disc)
        factors.sort()
        cross = sorted((rad + pr) / v * 1000.0 for v, rad, pr in moving.values())
        print("== %s: %d projectile(s), %d stationary" % (name, len(proj), stationary))
        print("   speed u/s   : min %.1f p10 %.1f p50 %.1f p90 %.1f max %.1f"
              % (speeds[0], speeds[len(speeds) // 10], speeds[len(speeds) // 2],
                 speeds[9 * len(speeds) // 10], speeds[-1]))
        print("   disc units  : p50 %.2f" % sorted(rad + pr for _, rad, pr in moving.values())[len(moving) // 2])
        print("   in 50 ms    : p10 %.2f p50 %.2f p90 %.2f units"
              % (speeds[len(speeds) // 10] * 0.05, speeds[len(speeds) // 2] * 0.05,
                 speeds[9 * len(speeds) // 10] * 0.05))
        print("   protected/disc (over-refusal factor): min %.2f p10 %.2f p50 %.2f p90 %.2f max %.2f"
              % (factors[0], factors[len(factors) // 10], factors[len(factors) // 2],
                 factors[9 * len(factors) // 10], factors[-1]))
        print("   disc-crossing time: p10 %.0f p50 %.0f p90 %.0f ms   (the window is %d ms)"
              % (cross[len(cross) // 10], cross[len(cross) // 2], cross[9 * len(cross) // 10],
                 int(WINDOW_MS)))
        print()
    if allv:
        allv.sort()
        print("ALL: %d projectile(s), speed p10 %.1f p50 %.1f p90 %.1f"
              % (len(allv), allv[len(allv) // 10], allv[len(allv) // 2], allv[9 * len(allv) // 10]))


if __name__ == "__main__":
    base = os.path.join(os.environ["LOCALAPPDATA"], "drelay", "hook")
    dirs = sys.argv[1:] or [os.path.join(base, d) for d in (
        "20261015-021911-p20152", "20261015-024717-p5824", "20261015-032549-p26684",
        "20261015-151232-p5424", "20261015-153805-p26156")]
    main(dirs)
