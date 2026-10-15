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
        'evaluated admissible durabilityMs clearance resolve score runnerup costUs windowMs').split()


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


def main(run_dir, dec_path):
    dec = load_decisions(dec_path)
    frames = load_frames(run_dir)
    name = os.path.basename(run_dir.rstrip('\\/'))
    buckets = collections.Counter()
    held = collections.Counter()
    fresh = collections.Counter()
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
    total = sum(v for k, v in buckets.items() if k in ('OUTRUN', 'SIDESTEP', 'TOWARD'))
    print('== %s: %d dodge moves, %d with a nearby threat' % (name, len(dec), total))
    if total:
        for k in ('OUTRUN', 'SIDESTEP', 'TOWARD'):
            print('   %-9s %5d (%5.1f%%)   held %4d  fresh %4d'
                  % (k, buckets[k], 100.0 * buckets[k] / total, held[k], fresh[k]))
    print()


if __name__ == '__main__':
    base = os.path.join(os.environ['LOCALAPPDATA'], 'drelay', 'hook')
    work = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'work')
    runs = sys.argv[1:] or [
        ('20261015-021911-p20152', 'p20152'), ('20261015-024717-p5824', 'p5824'),
        ('20261015-032549-p26684', 'p26684'), ('20261015-151232-p5424', 'p5424'),
        ('20261015-153805-p26156', 'p26156')]
    if runs and isinstance(runs[0], str) and len(runs) == 2:
        main(os.path.join(base, runs[0]), runs[1])
    else:
        for d, tag in runs:
            main(os.path.join(base, d), os.path.join(work, 'dd-win', 'dec-%s.txt' % tag))
