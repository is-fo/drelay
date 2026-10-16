#!/usr/bin/env python3
"""Print the Phase 0 identity of one method, for a test target.

`il_index.py` is the authority on how a target's identity is computed -- the
hash is SHA-256 over `MethodDef.Rva`'s exact instruction bytes, no header and no
re-encoding -- but it resolves targets out of `known-build.json` and cross-checks
each one against the 85 MB IL dump of the game.  A synthetic host has neither a
record entry nor a dump, and this project's rule is that the *recipe* must not be
restated: a second implementation of it is a second thing that can drift.

So this tool imports `il_index` and uses its reader, its `method_body` and its
`il_hash` -- the same functions the gate uses -- and adds only the lookup and the
printing.  It exists because `Test-Injection.ps1` needs to hand the hook a target
for a method of `SyntheticHost`, and a hash computed any other way would not be
the hash the JIT detour compares against.

    python tools/il_method_hash.py --assembly <path.dll> \
        --type SyntheticHostFixture.Cadence --name FrameTick --arity 1

Prints, one per line:

    type=<declaring type>
    method=<name>
    arity=<n>
    rva=0x...
    ilSize=<n>
    ilHash=<64 hex chars>

and exits 0.  Exits 1 with a message on stderr for anything else.
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import il_index  # noqa: E402  (the path insert above is what makes this importable)


def find_method(pe, want_type: str, want_name: str, want_arity: int):
    """The single MethodDef row matching (type, name, arity), or None."""
    tdef = il_index.type_def_index(pe, want_type)
    if tdef == 0:
        return None, "declaring type %s is not in %s's metadata" % (want_type, pe.path)

    first, end = il_index.type_method_range(pe, tdef)
    n_methods = pe.row_counts["MethodDef"]
    matches = []
    for mi in range(first, end + 1):
        if mi < 1 or mi > n_methods:
            continue
        row = pe.row("MethodDef", mi)
        if pe.string(row[3]) != want_name:
            continue
        if il_index.method_arity(pe, mi) == want_arity:
            matches.append(mi)

    if not matches:
        return None, ("%s::%s/%d is not in %s's metadata"
                      % (want_type, want_name, want_arity, pe.path))
    if len(matches) > 1:
        return None, ("%s::%s/%d is not unique in the metadata (%d candidates)"
                      % (want_type, want_name, want_arity, len(matches)))
    return matches[0], None


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--assembly", required=True, help="the PE to read")
    ap.add_argument("--type", required=True, help="fully qualified declaring type")
    ap.add_argument("--name", required=True, help="method name")
    ap.add_argument("--arity", type=int, required=True, help="parameter count")
    args = ap.parse_args(argv)

    try:
        pe = il_index.PE(args.assembly)
    except Exception as exc:                      # noqa: BLE001 -- reported, not raised
        print("cannot read %s: %s" % (args.assembly, exc), file=sys.stderr)
        return 1

    mi, problem = find_method(pe, args.type, args.name, args.arity)
    if mi is None:
        print(problem, file=sys.stderr)
        return 1

    row = pe.row("MethodDef", mi)
    rva = row[0]
    if rva == 0:
        # An abstract or extern method has no body, so there is nothing for the
        # JIT to hand the detour and no hash to compare.  Say so rather than
        # printing a hash of zero bytes.
        print("%s::%s/%d has no method body (RVA 0)" % (args.type, args.name, args.arity),
              file=sys.stderr)
        return 1

    code, _off, _header, _flags, _extra = il_index.method_body(pe, rva)
    print("type=%s" % args.type)
    print("method=%s" % args.name)
    print("arity=%d" % args.arity)
    print("rva=0x%X" % rva)
    print("ilSize=%d" % len(code))
    print("ilHash=%s" % il_index.il_hash(code))
    return 0


if __name__ == "__main__":
    sys.exit(main())
