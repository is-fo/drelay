"""Which stage makes the SLOW frames slow?

`SolverHarness` reports `decide()` cost as p50/p99 over frames and, since SPIKE3.2-OFFLINE-SOLVER.md
section 15.11, a cumulative per-stage split. Neither answers the question section 15.9.3b left open:
the field report's dry run reads `decide()` p99 **719 us on evaluating frames against a 300 us
budget** while the p50 is 87 us -- so the tail is one frame in a hundred and a MEAN over the other
ninety-nine cannot name it.

This reads the per-frame stage columns the decision dump now carries and answers it three ways:

  * the frames ranked by total stage time, so the worst frames can be looked at directly;
  * each stage's mean over the worst 1 % against its mean over the rest, which is what "it is the
    tail" means as a number;
  * the same split for the pathfinder's own self-clock, as the control -- if the slow frames are not
    slow in `stPath`, the path search is not the cause whatever the totals say.

Usage:
    python tools\\dodge_stage_report.py <decision-dump> [--top 12]
"""
import sys


COLS = ('frame action reason held heading scale goal goalsrc hintent intent course moving '
        'evaluated admissible durabilityMs clearance resolve score runnerup costUs windowMs '
        'wp wpdist pathn pathlen pathcost pathwhy '
        'stSetup stPath stGates stAdmit stScore stEscape standdur').split()

STAGES = ('stSetup', 'stPath', 'stGates', 'stAdmit', 'stScore', 'stEscape')


def _columns(path):
    """The column names, from the file's own `# frame | ...` header. See the note in
    `dodge_direction_report.py`: a hard-coded list here does not fail loudly, it reads zero rows and
    reports a zero, which is indistinguishable from a run that made no decisions."""
    comment = []
    with open(path, encoding='utf-8', errors='ignore') as fh:
        for line in fh:
            if not line.startswith('#'):
                break
            comment.append(line.lstrip('#').strip())
    for i, line in enumerate(comment):
        if line.startswith('frame') and '|' in line:
            return [c.strip() for c in ' '.join(comment[i:]).split('|')]
    return COLS


def load(path):
    cols = _columns(path)
    rows = []
    total = 0
    for line in open(path, encoding='utf-8', errors='ignore'):
        if line.startswith('#') or not line.strip():
            continue
        total += 1
        parts = [p.strip() for p in line.split('|')]
        if len(parts) != len(cols):
            continue
        r = dict(zip(cols, parts))
        try:
            r['frame'] = int(r['frame'])
            for c in STAGES:
                r[c] = int(r[c] or 0)
            r['costUs'] = int(r['costUs'] or 0)
            r['evaluated'] = int(r['evaluated'] or 0)
        except ValueError:
            continue
        r['stageTotal'] = sum(r[c] for c in STAGES)
        rows.append(r)
    if total > 0 and not rows:
        print('!! %s: %d data line(s) and NONE parsed -- the dump\'s column list and this reader'
              ' disagree. "no parseable rows" below is then a statement about this script and not'
              ' about the run; see `dodge_direction_report.py` for the same trap.' % (path, total))
    return rows


def main(path, top):
    rows = load(path)
    if not rows:
        print('no parseable rows in %s' % path)
        return
    evaluated = [r for r in rows if r['evaluated'] > 0]
    print('== %s' % path)
    print('   %d frame(s), %d of them evaluating' % (len(rows), len(evaluated)))
    if not evaluated:
        return

    ranked = sorted(evaluated, key=lambda r: r['stageTotal'], reverse=True)
    worst_n = max(1, len(evaluated) // 100)
    worst = ranked[:worst_n]
    rest = ranked[worst_n:]

    print()
    print('   the worst %d evaluating frame(s) by stage time (%.1f %% of %d):'
          % (worst_n, 100.0 * worst_n / len(evaluated), len(evaluated)))
    print('     %-7s %9s %9s %9s %9s %9s %9s %9s'
          % ('frame', 'total', 'stSetup', 'stPath', 'stGates', 'stAdmit', 'stScore', 'stEscape'))
    for r in ranked[:top]:
        print('     %-7d %9d %9d %9d %9d %9d %9d %9d'
              % (r['frame'], r['stageTotal'], r['stSetup'], r['stPath'], r['stGates'],
                 r['stAdmit'], r['stScore'], r['stEscape']))

    def mean(rs, c):
        return sum(r[c] for r in rs) / len(rs) if rs else 0.0

    print()
    print('   mean per stage, worst 1 % against the rest -- this is the answer to "is it the tail":')
    print('     %-9s %10s %10s %10s' % ('stage', 'worst 1%', 'the rest', 'ratio'))
    for c in STAGES:
        w, b = mean(worst, c), mean(rest, c)
        ratio = ('%.1fx' % (w / b)) if b > 0.5 else ('n/a' if w == 0 else 'inf')
        print('     %-9s %10.1f %10.1f %10s' % (c, w, b, ratio))
    print('     %-9s %10.1f %10.1f' % ('TOTAL', mean(worst, 'stageTotal'), mean(rest, 'stageTotal')))


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    top = 12
    args = sys.argv[1:]
    if '--top' in args:
        i = args.index('--top')
        top = int(args[i + 1])
        del args[i:i + 2]
    main(args[0], top)
