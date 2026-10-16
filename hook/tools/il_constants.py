#!/usr/bin/env python3
"""il_constants.py -- resolve obfuscated literal VALUES from the real IL dump.

The decompiled C# folds every literal into hehe_k(int32 index), so the VALUE is
never visible. The IL dump (hook/il/client-5.8.2.il) DOES show it:
right after the call there is an ldc/branch that reveals the constant, or the
method body shows the arithmetic directly.

Usage:
    python il_constants.py <il_file> <index> [--after K] [--limit M]
    python il_constants.py <il_file> --find "<substring>" [--window N]
"""
import re
import sys


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    path = argv[1]
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        lines = fh.read().split("\n")
    idx = None
    after = 2
    limit = 60
    find = None
    window = 40
    i = 2
    while i < len(argv):
        a = argv[i]
        if a == "--after":
            after = int(argv[i + 1]); i += 2
        elif a == "--limit":
            limit = int(argv[i + 1]); i += 2
        elif a == "--find":
            find = argv[i + 1]; i += 2
        elif a == "--window":
            window = int(argv[i + 1]); i += 2
        else:
            idx = int(a); i += 1

    callre = re.compile(r"call\s+.*::'.*'\(int32\)")
    ldcre = re.compile(r"ldc\.(i4(\.s|\.m1)?|r4|r8)\s+(.+?)\s*$")

    if find is not None:
        hits = 0
        for n, line in enumerate(lines):
            if find in line:
                hits += 1
                print("=== %s:%d ===" % (path, n + 1))
                for j in range(max(0, n - 4), min(len(lines), n + window)):
                    print("%8d| %s" % (j + 1, lines[j]))
                print()
                if hits >= 3:
                    break
        if hits == 0:
            print("(no match for %r)" % find)
        return 0

    n = 0
    for k in range(len(lines) - 3):
        m = ldcre.search(lines[k])
        if not m:
            continue
        imm = m.group(3).strip()
        try:
            val = int(imm, 0)
        except ValueError:
            continue
        if val != idx:
            continue
        if not callre.search(lines[k + 1]):
            continue
        n += 1
        print("=== %s:%d  (index %d) ===" % (path, k + 1, idx))
        for j in range(k, min(len(lines), k + 2 + after)):
            print("%8d| %s" % (j + 1, lines[j]))
        print()
        if n >= limit:
            print("... (stopped at --limit %d)" % limit)
            break
    if n == 0:
        print("(no ldc ... %d ; call ...(int32) pattern found)" % idx)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))