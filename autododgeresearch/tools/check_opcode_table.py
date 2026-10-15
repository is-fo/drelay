#!/usr/bin/env python3
"""
check_opcode_table.py -- does `il_index.OPCODES_1` describe this assembly?

WHY THIS EXISTS.

`il_index.py`'s instruction walk is a *check*, not the identity: the target hash is
SHA-256 over the raw IL bytes, so no entry in the opcode table can move a target's
fingerprint.  What the table feeds is exactly two things --

  * `decode_length()`, which decides whether a body was located correctly and is
    what turns a mislocated method into a reported problem rather than a silently
    different hash; and
  * the `MoreSects` offset choice inside `method_body()`, which *does* choose which
    bytes get hashed.

-- and that second one is why a wrong operand width is not harmless.  A wrong width
can change the chosen body of a target, and the gate's own output would still say
everything agreed.

So the table needs a witness that is not the table.  That witness is the IL dump: it
was produced by a different tool from the same assembly and carries, for every method,
the RVA, the declared code size and the offset of every instruction.  This tool walks
each body with the table `il_index.py` actually ships and compares.

    python tools/check_opcode_table.py                 # the recorded build
    python tools/check_opcode_table.py --json table.json
    python tools/check_opcode_table.py --list 20       # the first 20 bad methods

Two numbers come out, and they are different questions:

    all-offsets-match   every offset the walk visited is an instruction offset the
                        dump lists.  This is the one a wrong *width* breaks.
    ends-exactly        the walk consumed the body with no overrun and no shortfall.
                        This is the one a mislocated *body* breaks, and it is what
                        `decode_length()` is used for.

Exit code 0 when the table consumes at least `--min-exact` (default 99 %) of the bodies
it can read exactly and matches at least `--min-offsets` (default 90 %) of them; 1
otherwise, with the offending opcodes named.
"""

import argparse
import json
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import il_index as core  # noqa: E402


def walk(code, table, opcodes2, short_fixed, short_to_long):
    """The same walk `il_index.decode_length` performs, returning its offsets too."""
    p = core.code_entry(code)
    n = len(code)
    offs = []
    while p < n:
        offs.append(p)
        op = code[p]
        p += 1
        if op == 0xFE:
            spec = opcodes2.get(code[p])
            if spec is None:
                return offs, -1
            kind = spec[1]
            p += 1
        else:
            kind = table[op][1]
            if op in short_fixed or op in short_to_long:
                p += 1 if op in short_to_long else 0
                continue
        if kind == core.INLINE_NONE:
            pass
        elif kind in (core.INLINE_I, core.INLINE_TOKEN, core.INLINE_SR, core.INLINE_BR):
            p += 4
        elif kind in (core.INLINE_I8, core.INLINE_R):
            p += 8
        elif kind in (core.INLINE_SI, core.INLINE_SVAR, core.INLINE_BRS, "Align"):
            p += 1
        elif kind == core.INLINE_VAR:
            p += 2
        elif kind == core.INLINE_SWITCH:
            if p + 4 > n:
                return offs, -1
            count = struct.unpack_from("<I", code, p)[0]
            if count > 100000 or p + 4 + 4 * count > n:
                return offs, -1
            p += 4 + 4 * count
        else:
            return offs, -1
        if p > n:
            return offs, -1
    return offs, p


def parse_dump(path):
    """
    rva -> (code size, set of instruction offsets), straight out of the IL dump.

    THE DUMP IS THE WITNESS AND NOT A SECOND IMPLEMENTATION OF THE READER.  It is
    ilspycmd's own output from the shipped assembly, so agreement between it and this
    reader is agreement between two tools, which is the only kind that means anything.
    """
    methods = {}
    current = None
    rva_re = re.compile(r"// Method begins at RVA 0x([0-9a-fA-F]+)")
    size_re = re.compile(r"// Code size:\s*(\d+)")
    off_re = re.compile(r"^\s*IL_([0-9A-Fa-f]{4}):")
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            match = rva_re.search(line)
            if match:
                current = {"rva": int(match.group(1), 16), "size": None, "offs": set()}
                methods[current["rva"]] = current
                continue
            if current is None:
                continue
            match = size_re.search(line)
            if match:
                current["size"] = int(match.group(1))
                continue
            match = off_re.match(line)
            if match:
                current["offs"].add(int(match.group(1), 16))
    return methods


def recorded_assembly():
    """The assembly `known-build.json` was pinned from -- the same rule il_targets uses."""
    record = core.load_record(os.path.join(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__))), "known-build.json"))
    root = record.get("recordedFrom")
    if not root:
        core.fail("known-build.json has no recordedFrom")
    name = (record.get("assembly") or {}).get("file") or "DarzasDominion.dll"
    return os.path.join(root, name)


def recorded_dump():
    return os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                        "il", "client-5.8.2.il")


def main():
    parser = argparse.ArgumentParser(description="Validate il_index's opcode table against the IL dump.")
    parser.add_argument("--assembly", default=None)
    parser.add_argument("--il-dump", default=None)
    parser.add_argument("--min-exact", type=float, default=99.0,
                        help="percent of readable bodies the walk must consume exactly (default 99)")
    parser.add_argument("--min-offsets", type=float, default=90.0,
                        help="percent of readable bodies whose every walk offset is in the dump (default 90)")
    parser.add_argument("--list", type=int, default=0, help="print this many failing methods")
    parser.add_argument("--json", default=None)
    args = parser.parse_args()

    assembly = args.assembly or recorded_assembly()
    dump_path = args.il_dump or recorded_dump()
    if not os.path.isfile(assembly):
        core.fail("assembly not found at %s" % assembly)
    if not os.path.isfile(dump_path):
        core.fail("IL dump not found at %s (it is not versioned -- see docs/HOOK-BUILD-MATRIX.md)"
                  % dump_path)

    pe = core.PE(assembly)
    methods = parse_dump(dump_path)
    print("assembly : %s" % assembly)
    print("IL dump  : %s" % dump_path)
    print("methods  : %d in the dump" % len(methods))

    table = core.OPCODES_1
    checked = matched = exact = 0
    failures = []
    for rva, record in methods.items():
        if record["size"] is None or not record["offs"]:
            continue
        try:
            code, _, _, _, _ = core.method_body(pe, rva)
        except Exception:
            continue
        if len(code) != record["size"]:
            continue
        checked += 1
        offsets, end = walk(code, table, core.OPCODES_2, core.SHORT_FIXED, core.SHORT_TO_LONG)
        offsets_ok = all(o in record["offs"] for o in offsets)
        ends_ok = end == len(code)
        if offsets_ok:
            matched += 1
        if ends_ok:
            exact += 1
        if (not offsets_ok or not ends_ok) and len(failures) < max(args.list, 0):
            bad = next((o for o in offsets if o not in record["offs"]), None)
            failures.append({"rva": "0x%X" % rva, "ilSize": len(code),
                             "endsExactly": ends_ok, "firstBadOffset": bad,
                             "opcode": ("0x%02X" % code[bad]) if bad is not None else None})

    if checked == 0:
        core.fail("no method in the dump could be read out of the assembly")

    exact_pct = 100.0 * exact / checked
    offsets_pct = 100.0 * matched / checked
    print("checked  : %d method body/bodies present in both" % checked)
    print("matches  : %d (%.2f%%) -- every offset the walk visited is the dump's"
          % (matched, offsets_pct))
    print("exact    : %d (%.2f%%) -- the walk consumed the body with no overrun or shortfall"
          % (exact, exact_pct))

    for entry in failures:
        print("  %s IL %d endsExactly=%s firstBadOffset=%s opcode=%s"
              % (entry["rva"], entry["ilSize"], entry["endsExactly"],
                 entry["firstBadOffset"], entry["opcode"]))

    if args.json:
        with open(args.json, "w", encoding="utf-8", newline="\n") as handle:
            json.dump({"assembly": assembly, "dump": dump_path, "checked": checked,
                       "allOffsetsMatch": matched, "endsExactly": exact,
                       "failures": failures}, handle, indent=2)
            handle.write("\n")
        print("written  : %s" % args.json)

    ok = exact_pct >= args.min_exact and offsets_pct >= args.min_offsets
    print("")
    if ok:
        print("OPCODE TABLE OK -- the table il_index.py ships describes this assembly")
    else:
        print("OPCODE TABLE SUSPECT -- the walk disagrees with the IL dump:")
        print("  ends-exactly %.2f%% < %.2f%% and/or offsets %.2f%% < %.2f%%"
              % (exact_pct, args.min_exact, offsets_pct, args.min_offsets))
        print("  The digest is unaffected (it is over raw bytes), but the MoreSects offset")
        print("  choice inside method_body() reads this table, so a chosen body could move.")
    return 0 if ok else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except SystemExit:
        raise
    except Exception as exc:  # never a traceback where a one-line refusal will do
        print("check_opcode_table: unexpected failure: %s: %s" % (type(exc).__name__, exc),
              file=sys.stderr)
        sys.exit(2)
