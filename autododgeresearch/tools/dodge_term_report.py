"""Which score term chooses OUTRUN, and which chooses SIDESTEP?

`dodge_direction_report.py` measures WHAT the dodge did -- outrun, sidestep or move toward the shooter,
against the nearest threatening muzzle. This says WHY, by joining the same two files and reading the
chosen move's own score terms, which `SolverHarness --frame N` prints and `DodgeSolver.DescribeChosen`
produces.

WHY A SEPARATE REPORT AND NOT A COLUMN IN THE DECISION DUMP. The decision dump is per frame and every
one of its columns is one number about the frame; a term breakdown is ten numbers about the CHOSEN
CANDIDATE, and ten more columns would make the dump unreadable while still not aggregating. This reads
the dump's `--frame` output instead -- see `Capture-DodgeTerms.ps1` -- and aggregates the terms by the
classification the other report computes.

The question it answers, in one line: on the frames the dodge retreats, is it retreating because the
safety terms paid for the distance, because the goal term had no opinion, or because the move penalty
preferred a shorter step? Those three have three different fixes and no distribution over outcomes can
tell them apart.
"""
import collections
import math
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dodge_direction_report import chosen_clearance, load_frames, nearest_muzzle  # noqa: E402

TERM_RE = re.compile(r'(\w+)=(-?[\d.]+|nan|-inf)')
CHOSEN_RE = re.compile(r'^\s*chosen\s*:\s*(.+)$')
FRAME_RE = re.compile(r'^== frame (\d+) ==')


def load_terms(path):
    """frame -> {term: value}, from a harness `--frame` capture."""
    out = {}
    frame = None
    for line in open(path, encoding='utf-8', errors='ignore'):
        m = FRAME_RE.match(line)
        if m:
            frame = int(m.group(1))
            continue
        m = CHOSEN_RE.match(line)
        if m and frame is not None:
            terms = {}
            for name, value in TERM_RE.findall(m.group(1)):
                try:
                    terms[name] = float(value)
                except ValueError:
                    terms[name] = 0.0
            out[frame] = terms
    return out


def main(run_dir, terms_path, frame_reach=0.15):
    frames = load_frames(run_dir)
    terms = load_terms(terms_path)
    sums = collections.defaultdict(lambda: collections.Counter())
    counts = collections.Counter()
    for f, t in terms.items():
        a = frames.get(f)
        if a is None:
            continue
        nt = nearest_muzzle(a)
        if nt is None:
            continue
        _, mx, my = nt
        heading = t.get('heading')
        # The dump's `chosen` line does not carry the heading; the classification comes from the
        # clearance of the step's own line, which is the one thing the terms file and the trace can
        # both be asked about. See the note in `main` of dodge_direction_report.py.
        ux, uy = a['px'] - mx, a['py'] - my
        un = math.hypot(ux, uy)
        if un < 0.5:
            continue
        # Recover the heading from the chosen line's own `path`/`goal` terms is not possible, so the
        # heading is read from the parallel decision dump when one was passed.
        kind = None
        if heading is not None:
            hx, hy = math.cos(math.radians(heading)), math.sin(math.radians(heading))
            cos = (hx * (ux / un)) + (hy * (uy / un))
            kind = 'OUTRUN' if cos > 0.5 else ('TOWARD' if cos < -0.5 else 'SIDESTEP')
        counts[kind] += 1
        for name, value in t.items():
            if name in ('score', 'heading', 'scale'):
                continue
            sums[kind][name] += value
    total = sum(counts.values())
    name = os.path.basename(run_dir.rstrip('\\/'))
    print('== %s: %d frame(s) with a chosen move and a nearby threat' % (name, total))
    if total == 0:
        print()
        return
    order = ['resolve', 'clearance', 'durability', 'intent', 'goal', 'path', 'heading',
             'lateral', 'move', 'stand']
    print('   %-14s %8s %8s %8s' % ('term (mean)', 'OUTRUN', 'SIDESTEP', 'TOWARD'))
    for term in order:
        row = []
        for kind in ('OUTRUN', 'SIDESTEP', 'TOWARD'):
            n = counts[kind]
            row.append(sums[kind][term] / n if n else 0.0)
        print('   %-14s %8.3f %8.3f %8.3f' % (term, row[0], row[1], row[2]))
    print('   %-14s %8d %8d %8d' % ('frames', counts['OUTRUN'], counts['SIDESTEP'], counts['TOWARD']))
    print()


if __name__ == '__main__':
    base = os.path.join(os.environ['LOCALAPPDATA'], 'drelay', 'hook')
    if len(sys.argv) >= 3:
        main(sys.argv[1], sys.argv[2])
    else:
        print(__doc__)
        sys.exit(2)
