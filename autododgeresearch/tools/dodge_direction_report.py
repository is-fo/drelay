"""Was the DODGE's own chosen direction an outrun or a sidestep?

`outrun_report.py` measures every frame the player moved, which is mostly the player's own input on
these recordings. This joins the harness's per-frame decision dump to the trace so that the
attribution is the solver's: on the moves where `held == 0` the displacement IS the dodge's command,
and on all of them the dumped `heading` is the direction the dodge asked for.

Against the nearest threatening projectile's own muzzle, a chosen heading is:
  OUTRUN   -- along the line away from the muzzle (cos > 0.5)
  SIDESTEP -- across it (|cos| <= 0.5)
  TOWARD   -- into it (cos < -0.5)
"""
import json
import math
import os
import sys
import collections

COLS = ('frame action reason held heading scale goal goalsrc hintent intent course moving '
        'evaluated admissible durabilityMs clearance resolve score runnerup costUs windowMs '
        'wp wpdist pathn pathlen pathcost pathwhy '
        'stSetup stPath stGates stAdmit stScore stEscape').split()


def load_decisions(path):
    rows = []
    for line in open(path, encoding='utf-8', errors='ignore'):
        if line.startswith('#') or not line.strip():
            continue
        parts = [p.strip() for p in line.split('|')]
        if len(parts) != len(COLS):
            continue
        r = dict(zip(COLS, parts))
        if r['action'] != 'Move':
            continue
        r['frame'] = int(r['frame'])
        r['heading'] = float(r['heading'])
        r['held'] = int(r['held'])
        r['moving'] = int(r['moving'])
        r['scale'] = float(r['scale'])
        rows.append(r)
    return {r['frame']: r for r in rows}


def load_frames(run_dir):
    frames = {}
    for line in open(os.path.join(run_dir, 'trace.jsonl'), encoding='utf-8', errors='ignore'):
        if line.startswith('{"t":"F'):
            r = json.loads(line)
            frames[r['f']] = r
    return frames


def nearest_muzzle(frame):
    px, py, pr = frame['px'], frame['py'], frame.get('r') or 0.25
    best = None
    for t in frame.get('threats') or []:
        xs, ys = t.get('xs') or [], t.get('ys') or []
        n = min(len(xs), len(ys), t.get('n', 0))
        if n < 2:
            continue
        r = pr + t.get('rad', 0.0)
        d = min(math.hypot(xs[i] - px, ys[i] - py) for i in range(n))
        if d > 4.0:
            continue
        clearance = d - r
        if best is None or clearance < best[0]:
            best = (clearance, xs[0], ys[0])
    return best


def chosen_clearance(frame, heading_deg, scale, reach):
    """How close the CHOSEN step's own line comes to a threat's predicted path.

    WHY THIS IS BESIDE THE CLASSIFICATION AND NOT INSTEAD OF IT. OUTRUN/SIDESTEP/TOWARD says which way
    the dodge went relative to the shooter; it does not say whether the move it chose is SAFE, and a
    change that trades outrunning for walking into a bullet would improve the first number while
    making the dodge worse. This walks the step the dodge actually emitted -- from the player along
    the chosen heading for `scale` of one frame's reach -- against the threats the player is NOT
    already inside, and returns the smallest clearance, negative when the step's own line enters a
    disc.

    THE "NOT ALREADY INSIDE" CLAUSE IS THE WHOLE OF THE FILTER'S CORRECTNESS. A bullet whose disc
    contains the player now is already committed to damage and every candidate in the ring reads
    negative against it, which is why the first version of this reported that 100 % of chosen steps
    entered a disc -- it was measuring the bullet the player was standing in, on every frame, for
    every heading. It is the same distinction the solver's own `Clearance` term draws with its
    `alreadyInside` exception, and drawing it here from the trace rather than from the decision keeps
    this number independent of the solver's opinion of itself.
    """
    px, py = frame['px'], frame['py']
    pr = frame.get('r') or 0.25
    hx, hy = math.cos(math.radians(heading_deg)), math.sin(math.radians(heading_deg))
    ex, ey = px + (hx * reach * scale), py + (hy * reach * scale)
    best = None
    for t in frame.get('threats') or []:
        xs, ys = t.get('xs') or [], t.get('ys') or []
        n = min(len(xs), len(ys), t.get('n', 0))
        if n < 1:
            continue
        r = pr + t.get('rad', 0.0)
        if math.hypot(xs[0] - px, ys[0] - py) < r:
            continue          # already inside: its damage is committed whatever the dodge does
        for i in range(n):
            d = point_segment(xs[i], ys[i], px, py, ex, ey)
            c = d - r
            if best is None or c < best:
                best = c
    return best


def ring_best_clearance(frame, scale, reach, headings, step_deg):
    """The best clearance any heading on the dodge's own ring would have achieved this frame.

    THE COMPARISON THAT MAKES `chosen clearance` MEAN SOMETHING. A number standing alone cannot say
    whether the dodge chose badly -- on a frame where every direction is inside a corridor, a
    negative clearance is the best available answer and not a defect. This evaluates the same ring the
    solver evaluates (24 headings by default) at the same radius and returns the maximum, so the
    report can print the frames where the CHOSEN step is meaningfully worse than the ring's own best.
    """
    best = None
    for k in range(headings):
        c = chosen_clearance(frame, k * step_deg, scale, reach)
        if c is None:
            continue
        if best is None or c > best:
            best = c
    return best


def point_segment(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    length2 = (dx * dx) + (dy * dy)
    if length2 <= 1e-9:
        return math.hypot(px - ax, py - ay)
    t = ((px - ax) * dx + (py - ay) * dy) / length2
    t = max(0.0, min(1.0, t))
    return math.hypot(px - (ax + (t * dx)), py - (ay + (t * dy)))


def main(run_dir, dec_path, frame_reach=0.15):
    dec = load_decisions(dec_path)
    frames = load_frames(run_dir)
    name = os.path.basename(run_dir.rstrip('\\/'))
    buckets = collections.Counter()
    held = collections.Counter()
    fresh = collections.Counter()
    unsafe = collections.Counter()
    unsafe_n = collections.Counter()
    regret = 0
    regret_frames = 0
    for f, d in dec.items():
        a = frames.get(f)
        if a is None:
            continue
        nt = nearest_muzzle(a)
        if nt is None:
            buckets['no nearby threat'] += 1
            continue
        _, mx, my = nt
        hx = math.cos(math.radians(d['heading']))
        hy = math.sin(math.radians(d['heading']))
        ux, uy = a['px'] - mx, a['py'] - my
        un = math.hypot(ux, uy)
        if un < 0.5:
            # the player is standing on the muzzle: there is no "away" direction
            buckets['on the muzzle'] += 1
            continue
        cos = (hx * (ux / un)) + (hy * (uy / un))
        if cos > 0.5:
            kind = 'OUTRUN'
        elif cos < -0.5:
            kind = 'TOWARD'
        else:
            kind = 'SIDESTEP'
        buckets[kind] += 1
        (held if d['held'] else fresh)[kind] += 1
        c = chosen_clearance(a, d['heading'], d['scale'], frame_reach)
        if c is not None:
            unsafe_n[kind] += 1
            if c < 0.0:
                unsafe[kind] += 1
        # HOW MUCH SAFETY DID THE CHOICE GIVE UP, against the best this frame's ring could do? A
        # tenth of a unit is the scale of the game's own collision radius (0.37-0.51), so this counts
        # the frames where the dodge chose something materially worse than something available.
        best = ring_best_clearance(a, d['scale'], frame_reach, 24, 15.0)
        if best is not None and c is not None:
            regret_frames += 1
            if best - c > 0.10:
                regret += 1
    total = sum(v for k, v in buckets.items() if k in ('OUTRUN', 'SIDESTEP', 'TOWARD'))
    print('== %s: %d dodge moves, %d with a nearby threat' % (name, len(dec), total))
    if total:
        for k in ('OUTRUN', 'SIDESTEP', 'TOWARD'):
            print('   %-9s %5d (%5.1f%%)   held %4d  fresh %4d   step enters a disc: %d/%d'
                  % (k, buckets[k], 100.0 * buckets[k] / total, held[k], fresh[k],
                     unsafe[k], unsafe_n[k]))
        print('   gave up >0.10 units of clearance against the best heading available: %d/%d (%.1f%%)'
              % (regret, regret_frames, 100.0 * regret / max(regret_frames, 1)))
    print()


if __name__ == '__main__':
    base = os.path.join(os.environ['LOCALAPPDATA'], 'drelay', 'hook')
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    # THE DECISION DIRECTORY IS AN ARGUMENT BECAUSE IT IS THE WHOLE POINT OF THE REPORT. It used to
    # be hardcoded to `work/dd-win`, which is not a directory any capture writes; a report that reads
    # a path nobody produced is one `FileNotFoundError` away from being silently skipped, and the
    # before/after columns this exists to print are exactly two directories.
    #
    # AND A SECOND ARGUMENT SELECTS THE RUN. The five recordings were the only input for long enough
    # that their names were compiled in, which meant the ONE session that matters most -- the
    # operator's own live one -- could not be measured at all: its run directory is named for the
    # timestamp and its dump is not `dec-<recording>.txt`. `--run <run-dir> <dec-dump>` reports on
    # any trace, and the five-recording table stays the default.
    if len(sys.argv) >= 4 and sys.argv[1] == '--run':
        main(sys.argv[2], sys.argv[3])
    else:
        dec_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, 'work', 'dd-before')
        runs = [('20261015-021911-p20152', 'p20152'), ('20261015-024717-p5824', 'p5824'),
                ('20261015-032549-p26684', 'p26684'), ('20261015-151232-p5424', 'p5424'),
                ('20261015-153805-p26156', 'p26156')]
        for d, tag in runs:
            path = os.path.join(dec_dir, 'dec-%s.txt' % tag)
            if not os.path.exists(path):
                # A MISSING DUMP IS NAMED RATHER THAN CRASHED ON. The five recordings are captured by
                # one script and a reader who pointed this at a half-finished directory needs to know
                # which of the five is absent, not to read a stack trace about the first one.
                print('== %s: no decision dump at %s -- run Capture-DodgeDecisions.ps1\n' % (tag, path))
                continue
            main(os.path.join(base, d), path)
