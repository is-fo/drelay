"""Is the dodge outrunning bullets, and is that pushing the player away from the shooter?

The operator's second problem: "some projectiles are dodged by outrunning, this makes the character
pushed away from the boss, instead of simply taking a step to the side or running around the
projectile."

Two questions, from a trace:
  1. On the frames the dodge moved, was the move ALONG the line away from the nearest threatening
     projectile (outrunning) or ACROSS it (sidestepping)?
  2. Over the whole session, does the player's net displacement carry them away from the projectiles'
     common origin?

The projectile's origin is its own first sample: every shot in these traces is sampled from birth, so
the first knot is the muzzle, which is the shooter. Nothing here needs the boss's position.
"""
import json
import math
import os
import sys
import collections


def load(run_dir):
    grids = {}
    frames = []
    for line in open(os.path.join(run_dir, 'trace.jsonl'), encoding='utf-8', errors='ignore'):
        if line.startswith('{"t":"G'):
            r = json.loads(line)
            grids[r['g']] = r
        elif line.startswith('{"t":"F'):
            frames.append(json.loads(line))
    return frames


def nearest_threat(frame):
    """The threat whose disc the player is closest to entering, and its muzzle."""
    px, py, pr = frame['px'], frame['py'], frame.get('r') or 0.25
    best = None
    for t in frame.get('threats') or []:
        xs, ys = t.get('xs') or [], t.get('ys') or []
        n = min(len(xs), len(ys), t.get('n', 0))
        if n < 2:
            continue
        r = pr + t.get('rad', 0.0)
        # closest the bullet's sampled path comes to the player, over the trusted prefix
        d = min(math.hypot(xs[i] - px, ys[i] - py) for i in range(n))
        if d > 4.0:
            continue
        clearance = d - r
        if best is None or clearance < best[0]:
            # the muzzle: the first sample of the path
            best = (clearance, xs[0], ys[0], xs, ys, n)
    return best


def main(run_dir):
    frames = load(run_dir)
    if len(frames) < 3:
        print('not enough frames')
        return
    name = os.path.basename(run_dir.rstrip('\\/'))
    print('== %s: %d frames' % (name, len(frames)))

    # The player's move between consecutive frames, against the line from the nearest threatening
    # projectile's muzzle to the player.
    along = 0      # moved AWAY from the muzzle (outrunning)
    across = 0     # moved perpendicular (sidestepping)
    toward = 0     # moved TOWARD the muzzle
    no_threat = 0
    along_samples = []
    across_samples = []
    for i in range(1, len(frames)):
        a, b = frames[i - 1], frames[i]
        dx, dy = b['px'] - a['px'], b['py'] - a['py']
        dist = math.hypot(dx, dy)
        if dist < 1e-3:
            continue
        nt = nearest_threat(a)
        if nt is None:
            no_threat += 1
            continue
        _, mx, my = nt[0], nt[1], nt[2]
        # unit vector from the muzzle to the player: "away from the shooter"
        ux, uy = a['px'] - mx, a['py'] - my
        un = math.hypot(ux, uy)
        if un < 0.5:
            continue
        ux, uy = ux / un, uy / un
        # cosine between the move and "away from the shooter"
        cos = (dx * ux + dy * uy) / dist
        if cos > 0.5:
            along += 1
            along_samples.append(dist)
        elif cos < -0.5:
            toward += 1
        else:
            across += 1
            across_samples.append(dist)
    total = along + across + toward
    if total == 0:
        print('   no frames with a nearby threat')
        return
    print('   frames with a threat within 4 units and a measured move: %d' % total)
    print('   moved AWAY from the muzzle (outrunning)  : %d (%.1f%%)'
          % (along, 100.0 * along / total))
    print('   moved ACROSS it (sidestepping)           : %d (%.1f%%)'
          % (across, 100.0 * across / total))
    print('   moved TOWARD it                          : %d (%.1f%%)'
          % (toward, 100.0 * toward / total))
    if along_samples and across_samples:
        print('   mean step when outrunning: %.3f units, when sidestepping: %.3f units'
              % (sum(along_samples) / len(along_samples),
                 sum(across_samples) / len(across_samples)))

    # Every muzzle the session saw, and where the player ended up relative to them.
    muzzles = []
    for f in frames[::10]:
        for t in f.get('threats') or []:
            xs = t.get('xs') or []
            ys = t.get('ys') or []
            if len(xs) > 1:
                muzzles.append((xs[0], ys[0]))
    if muzzles:
        cx = sum(m[0] for m in muzzles) / len(muzzles)
        cy = sum(m[1] for m in muzzles) / len(muzzles)
        d0 = math.hypot(frames[0]['px'] - cx, frames[0]['py'] - cy)
        d1 = math.hypot(frames[-1]['px'] - cx, frames[-1]['py'] - cy)
        dmin = min(math.hypot(f['px'] - cx, f['py'] - cy) for f in frames)
        dmax = max(math.hypot(f['px'] - cx, f['py'] - cy) for f in frames)
        print('   mean muzzle at (%.1f, %.1f) over %d shot(s)' % (cx, cy, len(muzzles)))
        print('   player distance from it: start %.1f, end %.1f, min %.1f, max %.1f'
              % (d0, d1, dmin, dmax))


if __name__ == '__main__':
    base = os.path.join(os.environ['LOCALAPPDATA'], 'drelay', 'hook')
    dirs = sys.argv[1:] or [os.path.join(base, d) for d in (
        '20261015-021911-p20152', '20261015-024717-p5824', '20261015-032549-p26684',
        '20261015-151232-p5424', '20261015-153805-p26156')]
    for d in dirs:
        main(d)
        print()
