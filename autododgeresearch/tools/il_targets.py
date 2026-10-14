#!/usr/bin/env python3
"""
il_targets.py -- list candidate methods on a type, so a new hook target can be pinned.

WHY THIS EXISTS, AND WHY IT IS NOT A SECOND il_index.py.

`tools/il_index.py` resolves a target that is already *named* in `known-build.json`
by `(declaringType, name, arity)`.  That works for the four Phase 0 targets, whose
metadata names are the readable ones (`Update`, `ProcessProjectileData`,
`HitCharacter`, `RequestEscape`).

It does not work for the methods Phase 3 needs.  The client's obfuscator renames
the interesting ones to mangled identifiers, and -- this is the trap -- the name the
*decompiler* prints and the name that is actually in the metadata string heap are
**not the same string**:

    decompiled:  World.hehe_EB2C_E5A5_EE82_E9C5_EF68(float, float)
    metadata:    World.<a string of private-use code points>
    IL dump:     World.'<the same private-use string>'

A whole-file search of the 89 MB IL dump for `EB2C_E5A5_EE82_E9C5_EF68` returns
**zero** hits, which is the measurement that makes this file necessary: the
expression that a hook author would naturally reach for cannot find the method at
all, and the failure is silent (it looks like "the method is not in this build").

So the selection is made on evidence that does not depend on the name:

  1. the declaring type, which is readable;
  2. the parameter count, which the metadata reader gets from the Param table;
  3. the IL size, which is the number the *decompiled* method's line span predicts;
  4. the return type and parameter types, read from the signature blob;
  5. the decompiled method's own call sites and line range, for the final tie-break.

An operator picks the row whose shape matches the decompiled method, then adds the
`declaringType` / `name` / `arity` triple to `known-build.json` and lets
`il_index.py` do the pinning -- this tool never writes the record, so there is
still exactly one authority for it.

Usage:
  python tools/il_targets.py --type DarzasDominion.Darza.Game.World
  python tools/il_targets.py --type ... --arity 2 --returns void --params float,float
  python tools/il_targets.py --type ... --rva 0x162E4C
  python tools/il_targets.py --type ... --csv > candidates.csv
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import il_index as core  # noqa: E402

def _recorded_assembly():
    """
    Where the verified build lives, per `known-build.json`.

    `recordedFrom` is a *directory* (the install root), which is the opposite of
    what `il_index.py --assembly` takes.  Getting that wrong is a confusing
    "file not found" on a path that exists, so the join happens here once.
    """
    try:
        rec = core.load_record(core.DEFAULT_RECORD)
    except SystemExit:
        return None
    where = rec.get("recordedFrom")
    if not where:
        return None
    if os.path.isdir(where):
        return os.path.join(where, rec.get("assembly", {}).get("file", "DarzasDominion.dll"))
    return where


DEFAULT_ASSEMBLY = _recorded_assembly()


def signature_of(pe, mi):
    """
    (return type text, parameter type texts) from the method's signature blob.

    A deliberately shallow reader: it walks the element type and the parameter
    list, and it does not resolve every ELEMENT_TYPE_* exhaustively.  It exists to
    tell `void (float, float)` from `void (float, float, int)` and to name a
    reference parameter as `ref`, which is the whole of what target selection
    needs.  A type it cannot name is reported as a hex kind rather than guessed at.
    """
    row = pe.row("MethodDef", mi)
    blob = pe.blob(row[4])
    pos = 0

    def read_compressed():
        nonlocal pos
        b = blob[pos]
        if b & 0x80 == 0:
            pos += 1
            return b
        if b & 0xC0 == 0x80:
            v = ((b & 0x3F) << 8) | blob[pos + 1]
            pos += 2
            return v
        v = ((b & 0x1F) << 24) | (blob[pos + 1] << 16) | (blob[pos + 2] << 8) | blob[pos + 3]
        pos += 4
        return v

    def type_name(kind):
        """
        Name one type and advance `pos` past exactly the bytes it consumed.
        EVERY BRANCH MUST ADVANCE, AND THAT IS THE SECOND BUG THIS FUNCTION HAD.  A
        nested call that leaves `pos` where it started re-reads the same byte for the
        next parameter, so `GetDamageTaken(int, double, bool, out Color)` came back as
        four `int`s -- the correct count with the wrong types, which is the kind of
        error that survives a glance at the table.
        """
        nonlocal pos
        simple = {
            0x01: "void", 0x02: "bool", 0x03: "char", 0x04: "sbyte", 0x05: "byte",
            0x06: "short", 0x07: "ushort", 0x08: "int", 0x09: "uint", 0x0A: "long",
            0x0B: "ulong", 0x0C: "float", 0x0D: "double", 0x0E: "string",
            0x18: "IntPtr", 0x19: "UIntPtr", 0x1C: "object",
        }
        if kind in simple:
            pos += 1
            return simple[kind]
        if kind in (0x11, 0x12):          # VALUETYPE / CLASS, followed by a TypeDefOrRef
            pos += 1
            token = read_compressed()
            return type_token_name(pe, token) or ("kind 0x%02X" % kind)
        if kind == 0x1D:                  # SZARRAY
            pos += 1
            return type_name(blob[pos]) + "[]"
        if kind == 0x10:                  # BYREF -- counted as one parameter, see il_index
            pos += 1
            return "ref " + type_name(blob[pos])
        if kind == 0x1F or kind == 0x20:  # generic instance / var
            read_compressed()
            return "generic"
        if kind == 0x0F:                  # PTR
            pos += 1
            return type_name(blob[pos]) + "*"
        pos += 1
        return "kind 0x%02X" % kind

    # THE CALLING CONVENTION BYTE IS NOT THE PARAMETER COUNT, AND READING IT AS ONE
    # WAS THE FIRST VERSION OF THIS FUNCTION.  A `MethodDefSig` is
    # `[calling convention][generic param count if GENERIC][param count][ret][params...]`,
    # so the first byte carries DEFAULT/VARARG/HASTHIS plus the GENERIC flag in bit 4.
    # Skipping the count and reading the convention byte instead produced arity-2 methods
    # with thirty-two parameters, all of them decoded from what was actually the return
    # type and the parameter list -- a plausible-looking table with every row wrong.
    # The fix is one byte of protocol, and the symptom is why this note is here: nothing
    # errored, and the parameter column was the only thing that disagreed with the
    # decompiled source.
    convention = blob[pos]
    pos += 1
    if convention & 0x10:                 # GENERIC: a compressed generic parameter count
        read_compressed()
    param_count = read_compressed()
    ret = type_name(blob[pos])
    params = []
    for _ in range(param_count):
        params.append(type_name(blob[pos]))
    return ret, params


def type_token_name(pe, token):
    """TypeDefOrRef coded token -> a name, for the evidence column."""
    if token == 0:
        return None
    tag = token & 0x3
    index = token >> 2
    try:
        if tag == 0:
            nested = core.nested_class_map(pe)
            return core.declaring_type_of(pe, index, nested)
        if tag == 1:
            row = pe.row("TypeRef", index)
            ns = pe.string(row[2])
            nm = pe.string(row[1])
            return (ns + "." + nm) if ns else nm
    except Exception:                     # a malformed token is reported, not raised
        return None
    return None


def self_test(pe):
    """
    Check this tool's signature reader against the four documented signatures.

    WHY THIS EXISTS.  The reader had two bugs on its first run, and neither of them
    raised anything: the calling convention byte was read as the parameter count, and
    one branch of the type walker forgot to advance.  Both produced a *plausible table*
    -- correct arity, wrong types -- and the only reason they were caught is that a
    `--params float,float` filter that should have matched the movement integrator
    matched nothing at all.

    `il_index.py` already states four of these signatures independently of any reader
    (`SELFTEST_SIGNATURES`), and it uses them to check its own arity model.  This is the
    same list used for the other half of the signature, so the check is against the
    documents rather than against this code.
    """
    expected = {
        "Update": ("void", ["float"]),
        "ProcessProjectileData": None,            # 16 parameters; only the count is documented
        "HitCharacter": ("bool", []),
        "RequestEscape": ("void", []),
        "HitByProjectile": ("void", ["ref Proj..."]),
        "GetDamageTaken": ("int", ["int", "double", "bool", "ref ..."]),
    }
    checks = []
    for tname, mname, arity, why in core.SELFTEST_SIGNATURES:
        tdef = core.type_def_index(pe, tname)
        if tdef == 0:
            checks.append(("%s::%s" % (tname, mname), False, "declaring type not found"))
            continue
        first, last = core.type_method_range(pe, tdef)
        found = None
        for mi in range(first, last + 1):
            if pe.string(pe.row("MethodDef", mi)[3]) == mname:
                found = mi
                break
        if found is None:
            checks.append(("%s::%s" % (tname, mname), False, "method not found"))
            continue
        ret, params = signature_of(pe, found)
        want = expected.get(mname)
        if want is None:
            checks.append(("%s::%s parameter count" % (tname.rsplit(".", 1)[-1], mname),
                           len(params) == arity,
                           "expected %d, got %d (%s)   [%s]"
                           % (arity, len(params), ",".join(params), why)))
            continue
        rexp, pexp = want
        ok = len(params) == len(pexp) and ret == rexp and all(
            p == e or e.endswith("...") or (e == "ref ..." and p.startswith("ref "))
            for p, e in zip(params, pexp))
        checks.append(("%s::%s signature" % (tname.rsplit(".", 1)[-1], mname), ok,
                       "expected %s(%s), read %s(%s)   [%s]"
                       % (rexp, ",".join(pexp), ret, ",".join(params), why)))
    return checks


def main(argv=None):
    ap = argparse.ArgumentParser(description="list candidate hook targets on one type")
    ap.add_argument("--assembly", default=DEFAULT_ASSEMBLY,
                    help="the game assembly (default: known-build.json's recordedFrom)")
    ap.add_argument("--type", required=False, help="fully qualified declaring type")
    ap.add_argument("--arity", type=int, default=None)
    ap.add_argument("--returns", default=None, help="substring match on the return type")
    ap.add_argument("--params", default=None, help="comma-separated types, exact list")
    ap.add_argument("--min-il", type=int, default=0)
    ap.add_argument("--max-il", type=int, default=1 << 30)
    ap.add_argument("--rva", default=None, help="show only the method at this RVA")
    ap.add_argument("--hashes", action="store_true",
                    help="compute each candidate's IL hash (slower)")
    ap.add_argument("--csv", action="store_true")
    ap.add_argument("--self-test", action="store_true",
                    help="check the signature reader against the documented signatures")
    args = ap.parse_args(argv)

    if args.self_test:
        pe = core.PE(args.assembly)
        checks = self_test(pe)
        bad = 0
        for what, ok, detail in checks:
            print("%s %s: %s" % ("PASS" if ok else "FAIL", what, detail))
            bad += 0 if ok else 1
        print("%d check(s): %d passed, %d failed" % (len(checks), len(checks) - bad, bad))
        return 1 if bad else 0

    if not args.type:
        print("--type is required unless --self-test is given")
        return 2

    pe = core.PE(args.assembly)
    tdef = core.type_def_index(pe, args.type)
    if tdef == 0:
        print("type %s is not in %s's metadata" % (args.type, pe.path))
        return 1
    first, last = core.type_method_range(pe, tdef)
    want_rva = int(args.rva, 0) if args.rva else None
    want_params = args.params.split(",") if args.params else None

    rows = []
    for mi in range(first, last + 1):
        if mi < 1 or mi > pe.row_counts["MethodDef"]:
            continue
        row = pe.row("MethodDef", mi)
        rva = row[0]
        if want_rva is not None and rva != want_rva:
            continue
        if not (0x200 <= rva < len(pe.data)):
            continue                          # no body (abstract/pinvoke) -- not a target
        try:
            code, code_off, header_size, flags, extra = core.method_body(pe, rva)
        except SystemExit:
            continue
        arity = core.method_arity(pe, mi)
        if args.arity is not None and arity != args.arity:
            continue
        if not (args.min_il <= len(code) <= args.max_il):
            continue
        ret, params = signature_of(pe, mi)
        if args.returns and args.returns.lower() not in ret.lower():
            continue
        if want_params is not None and params != want_params:
            continue
        ilhash = core.il_hash(code) if args.hashes else ""
        rows.append({
            "token": "0x06%06X" % mi,
            "row": mi,
            "name": pe.string(row[3]),
            "arity": arity,
            "returns": ret,
            "params": ",".join(params),
            "rva": "0x%X" % rva,
            "ilSize": len(code),
            "flags": ("static " if row[2] & core.MD_STATIC else "")
                     + ("virtual " if row[2] & core.MD_VIRTUAL else "")
                     + ("abstract" if row[2] & core.MD_ABSTRACT else ""),
            "ilHash": ilhash,
        })

    rows.sort(key=lambda r: r["ilSize"])
    if args.csv:
        print("token,name,arity,returns,params,rva,ilSize,flags,ilHash")
        for r in rows:
            print(",".join('"%s"' % str(r[k]) for k in
                           ("token", "name", "arity", "returns", "params", "rva",
                            "ilSize", "flags", "ilHash")))
        return 0

    print("%s on %s -- %d candidate(s) with a body" % (pe.path, args.type, len(rows)))
    for r in rows:
        nm = r["name"]
        # The obfuscator's names are strings of private-use code points, which are
        # unreadable in a console and copy badly; the code-point count identifies
        # them where the glyphs cannot.
        printable = all(0x20 <= ord(c) < 0x7F for c in nm) and nm
        label = nm if printable else "<private-use name, %d code point(s)>" % len(nm)
        print("  %s  row %-5d arity %-2d %-14s (%s)  rva %-9s IL %-6d %s"
              % (r["token"], r["row"], r["arity"], label, r["params"], r["rva"],
                 r["ilSize"], r["flags"]))
        if r["ilHash"]:
            print("        ilHash %s" % r["ilHash"])
    return 0


if __name__ == "__main__":
    sys.exit(main())
