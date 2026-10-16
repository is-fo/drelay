#!/usr/bin/env python3
"""g.py -- bounded grep-with-context helper for reading the drelay decompile.

Why this exists: World.cs is 24381 lines / 827 KB and the task forbids reading it
whole.  This prints ONLY matching lines (with line numbers) or a bounded
match+context block, so a search never dumps an unbounded region into tool output.

Usage:
    python g.py <file> <regex> [--ctx N] [--max M] [--range A B] [--sig] [--lit]

  --ctx N     print up to N lines of context after each match (default 0)
  --max M     stop after M matches (default 40)
  --range A B restrict to lines A..B (1-based, inclusive)
  --sig       only print lines that look like a declaration / method signature
  --lit       treat the pattern as a literal substring, not a regex

Examples:
    python g.py work/decomp/DarzasDominion.Darza.Game/World.cs "public.*Update" --ctx 2
    python g.py work/decomp/DarzasDominion.Darza.Game.Objects/Projectile.cs "HitsPlayer" --max 20
"""
import re
import sys

SIGPAT = re.compile(
    r"\b(class|struct|enum|interface|delegate|public|private|protected|internal|"
    r"static|virtual|override|sealed|readonly|const|event)\b")


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    path = argv[1]
    raw = argv[2]
    ctx = 0
    mx = 40
    lo, hi = 1, 10 ** 9
    sig = False
    lit = False
    i = 3
    while i < len(argv):
        a = argv[i]
        if a == "--ctx":
            ctx = int(argv[i + 1]); i += 2
        elif a == "--max":
            mx = int(argv[i + 1]); i += 2
        elif a == "--range":
            lo, hi = int(argv[i + 1]), int(argv[i + 2]); i += 3
        elif a == "--sig":
            sig = True; i += 1
        elif a == "--lit":
            lit = True; i += 1
        else:
            print("unknown arg " + a, file=sys.stderr)
            return 2

    pat = re.compile(re.escape(raw)) if lit else re.compile(raw)

    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        lines = fh.read().split("\n")

    n = 0
    for idx in range(lo - 1, min(hi, len(lines))):
        line = lines[idx]
        if not pat.search(line):
            continue
        if sig and not SIGPAT.search(line):
            continue
        n += 1
        if ctx:
            print("=== %s:%d ===" % (path, idx + 1))
            for j in range(idx, min(len(lines), idx + 1 + ctx)):
                print("%6d| %s" % (j + 1, lines[j]))
            print()
        else:
            print("%6d| %s" % (idx + 1, line.rstrip()))
        if n >= mx:
            print("... (stopped at --max %d)" % mx)
            break
    if n == 0:
        print("(no match for %r in %s)" % (raw, path))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))