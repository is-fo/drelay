"""Resolve `hehe_F244_F670_F803_F5DD_EB9E(N)` constant lookups in IL to real values.

The client's constants all live in four encrypted arrays (int/long/float/double). Which
array a call site uses is decided by the *return type* the JIT sees, which only the IL
shows -- the decompiled C# cannot tell you, because every call renders identically as
`hehe_F244_F670_F803_F5DD_EB9E(N)`.  This tool reads the IL dump from `ilspycmd -il`,
finds each `ldc.i4 <N>` that is immediately followed by a `call <ty> ...hehe...`,
and prints the resolved value from `work/_pool_*.txt`.

    python autododgeresearch/tools/poolresolve.py <file.il> "<method substring>"

Verified anchors (see work/find_int_pool.py): the int pool is identity-mapped for the small
indices, so `hehe(190)` = 70 = GmHealthUpdate, `hehe(22)` = 5 = GmMapInfo, `hehe(4)` = 4, ...
That is what makes the mapping trustworthy: the resolver reproduces known packet ids.
"""
import re
import sys
from pathlib import Path

WORK = Path(__file__).resolve().parents[2] / 'work'


def load_pool(name):
    out = {}
    p = WORK / f'_pool_{name}.txt'
    for line in p.read_text().splitlines():
        if '=' in line:
            k, v = line.split('=', 1)
            out[int(k)] = v
    return out


POOLS = {p: load_pool(p) for p in ('ints', 'longs', 'floats', 'doubles')}

# A call to the obfuscated lookup renders with a mangled method name, so match on the
# return type and the fact that the callee takes one int32.
CALL = re.compile(r'^\s*IL_[0-9A-Fa-f]+:\s*call\s+(int32|int64|float32|float64)\s+\S+::(\S+)\(int32\)')
# ldc.i4 / ldc.i4.s / ldc.i4.N -- the three encodings of a small int constant.
LDC_I4 = re.compile(r'^\s*IL_[0-9A-Fa-f]+:\s*(ldc\.i4(?:\.s|\.m1)?|ldc\.i4)\s+(-?\d+)')
LDC_I4_SHORT = re.compile(r'^\s*IL_[0-9A-Fa-f]+:\s*ldc\.i4\.(\d)\b')

TY_TO_POOL = {
    'int32': 'ints',
    'int64': 'longs',
    'float32': 'floats',
    'float64': 'doubles',
}


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    path = Path(sys.argv[1])
    needle = sys.argv[2]
    lines = path.read_text(encoding='utf-8', errors='backslashreplace').splitlines()

    # Locate the enclosing method header by searching for the needle at column 0-ish.
    starts = [i for i, l in enumerate(lines)
              if needle in l and not l.strip().startswith('//')]
    if not starts:
        raise SystemExit(f'no line containing {needle!r}')
    start = starts[0]
    print(f'--- {path.name}: showing from line {start + 1} for {needle!r} ---')

    hits = 0
    for i in range(start, min(len(lines), start + 4000)):
        line = lines[i]
        if i > start and re.match(r'^\t\.method\b', line):
            break
        m_short = LDC_I4_SHORT.match(line)
        m_long = LDC_I4.match(line)
        idx = None
        if m_short:
            idx = int(m_short.group(1))
        elif m_long:
            idx = int(m_long.group(2))
        if idx is None:
            continue
        # The lookup call is on the next non-blank, non-comment line.
        j = i + 1
        while j < len(lines) and (not lines[j].strip() or lines[j].strip().startswith('//')):
            j += 1
        if j >= len(lines):
            continue
        m_call = CALL.match(lines[j])
        if not m_call:
            continue
        ret_ty = m_call.group(1)
        pool = POOLS[TY_TO_POOL[ret_ty]]
        val = pool.get(idx, '<index out of range>')
        print(f'  {line.split(":", 1)[0].strip():>10}  hehe({idx:<5}) -> {ret_ty:<8} = {val}')
        hits += 1
    if not hits:
        print('  (no resolved lookups found in the scanned window)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
