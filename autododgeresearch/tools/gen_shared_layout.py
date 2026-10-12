#!/usr/bin/env python3
"""Compute the native DrelayShared layout and generate the managed view of it.

WHY THIS EXISTS, AND WHAT IT REPLACES
-------------------------------------
The shared block is the contract between MSVC and the C# compiler, and the first
two attempts to state it twice were both wrong:

  * a hand-written `LayoutKind.Sequential` C# struct, whose sizes were asserted
    against literals derived from the same premise being tested;
  * hand-written `[FieldOffset]` values, which were wrong in three places at once,
    including the ring bases -- nowhere near where the arithmetic suggested.
    Nothing about those numbers is guessable, which is the whole finding.

NO SPECIFIC SIZE IS QUOTED HERE, DELIBERATELY.  This docstring used to name
`DrelayFieldOffset` as 88 and `DrelayTargetHash` as 112 and the ring bases as
141,952 and 731,792.  Those were true of a struct revision that has since changed,
and a measured number written into prose goes stale silently.  The authority is the
compiler: `native/SizeProbe.c` is compiled and run by `build.ps1`, which diffs its
output against this generator's prediction on every build.

So the layout is computed from the native header by mirroring the C rules (size,
alignment, padding) in one place, and the managed struct is *generated* from the
result.  There is then one definition, and the generated file carries the offsets
as data.

The residual risk -- that this script's model of MSVC's packing disagrees with
MSVC -- is not left to trust: the native side reports its own
`sizeof(DrelayShared)` in the block header at attach, and the managed side
compares it with the number this script computed.  A disagreement is a refusal
with both numbers printed.  `kitchen sink` that check is
`DrelayHookShared.h`'s own `static_assert`, which makes the native side fail to
*compile* if its layout stops matching the constant recorded here.

Usage:
    python gen_shared_layout.py                 # write the generated C#
    python gen_shared_layout.py --check         # exit 1 when stale
    python gen_shared_layout.py --print         # just dump the layout

Exit codes: 0 = written/matching, 1 = stale or a parse problem, 2 = usage.
"""

import argparse
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_HEADER = os.path.abspath(
    os.path.join(HERE, "..", "hook", "native", "DrelayHookShared.h"))
DEFAULT_OUT = os.path.abspath(
    os.path.join(HERE, "..", "hook", "managed", "Drelay.Bootstrap",
                 "GeneratedSharedLayout.cs"))

# C scalar sizes and alignments on x64 Windows.  Written down rather than
# imported, because the point of this file is to be the authority on them.
SCALARS = {
    "char": (1, 1),
    "int": (4, 4),
    "float": (4, 4),
    "double": (8, 8),
    "uint8_t": (1, 1),
    "uint16_t": (2, 2),
    "uint32_t": (4, 4),
    "uint64_t": (8, 8),
    "int64_t": (8, 8),
}

STRUCTS = [
    ("DrelayTargetHash", "DrelayTargetHash", "TargetStride"),
    ("DrelayFieldOffset", "DrelayFieldOffset", "OffsetStride"),
    ("DrelayReadStep", "DrelayReadStep", "StepStride"),
    ("DrelayReadSpec", "DrelayReadSpec", "ReadSpecStride"),
    ("DrelayLogLine", "DrelayLogLine", "LogLineStride"),
    ("DrelayTraceRow", "DrelayTraceRow", "TraceRowStride"),
    ("DrelayFrameRow", "DrelayFrameRow", "FrameRowStride"),
]


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def parse_defines(text):
    """`#define NAME (value)` and `#define NAME value`, as ints.

    The identifier is `[A-Za-z_][A-Za-z0-9_]*` and the value is separated by
    whitespace, rather than `(DRELAY_\\w+)\\s+([0-9]+)` with `\\w+` left to find the
    boundary by backtracking.  That version silently skipped
    `#define DRELAY_MAX_TARGETS 8` while matching `DRELAY_MAX_STEPS 8`: with no
    separator required, the engine can split the name at the last underscore and
    read the tail as the value's first "digits" for some names and not others,
    depending on how the backtracking lands.  A missing constant is a bad way to
    find that out.
    """
    out = {}
    for m in re.finditer(r"#define\s+([A-Za-z_][A-Za-z0-9_]*)\s+\(?\s*([0-9]+)\s*\)?", text):
        out.setdefault(m.group(1), int(m.group(2)))
    return out


def struct_body(text, name):
    m = re.search(r"typedef struct\s+" + name + r"\s*\{(.*?)\}\s*" + name + r"\s*;",
                  text, re.S)
    if not m:
        raise SystemExit("struct %s not found in the header" % name)
    return m.group(1)


def parse_fields(body):
    """(type, name, dimension) for each member of a struct body."""
    fields = []
    for decl in body.split(";"):
        decl = decl.strip()
        if not decl:
            continue
        # Drop a leading `volatile`/`const` into the type string so the size
        # lookup can find it after normalisation.
        m = re.match(r"^(.*?)\b(\w+)\s*(?:\[(.*?)\])?$", decl, re.S)
        if not m:
            raise SystemExit("cannot parse field declaration: %r" % decl)
        typ = " ".join(m.group(1).split())
        fields.append((typ, m.group(2), m.group(3)))
    return fields


def size_align(typ, defines, known, dims):
    key = typ.replace("volatile ", "")
    if key in SCALARS:
        return SCALARS[key]
    if key in known:
        return known[key]
    raise SystemExit("unknown type %r" % typ)


def resolve_count(dim, defines, dims):
    dim = dim.strip()
    if dim in defines:
        return defines[dim]
    if dim in dims:
        return dims[dim]
    if "*" in dim:
        product = 1
        for part in dim.split("*"):
            product *= resolve_count(part, defines, dims)
        return product
    return int(dim)


def compute_layout(text, name, known, dims):
    """Offsets, sizes and the struct's alignment, per the C rules.

    The alignment comes back as well as the size, and that matters: an embedded
    struct aligns to *its own* alignment, not to eight.  `DrelayFieldOffset` is
    88 bytes with alignment 4 (its largest member is a `uint32_t`), and the first
    version of this model assumed 8 for every embedded struct -- which put every
    field from `offsets` onward eight bytes out and made the generator's total
    disagree with the compiler's by 128 bytes.  The compiled size probe
    (native/SizeProbe.c) is what caught it, and is why the model is checked rather
    than trusted.
    """
    defines = parse_defines(text)
    body = struct_body(text, name)
    offset = 0
    max_align = 1
    rows = []
    for typ, fname, dim in parse_fields(body):
        size, align = size_align(typ, defines, known, dims)
        if dim is not None:
            # An array aligns to its ELEMENT type, never to its total size.  The
            # first version of this model promoted any array of eight bytes or
            # more to 8-byte alignment, which is true for `uint64_t x[8]` and
            # false for `uint8_t mvid[16]` and `DrelayReadStep steps[8]`; it moved
            # four fields and inflated the total by 128 bytes.  The compiled probe
            # reported the discrepancy; this line is the fix.
            size *= resolve_count(dim, defines, dims)
        offset = (offset + align - 1) // align * align
        rows.append({"offset": offset, "type": typ, "name": fname, "size": size})
        offset += size
        max_align = max(max_align, align)
    total = (offset + max_align - 1) // max_align * max_align
    return rows, total, max_align


CS_TYPES = {
    "uint8_t": "byte",
    "uint16_t": "ushort",
    "uint32_t": "uint",
    "uint64_t": "ulong",
    "int32_t": "int",
    "int64_t": "long",
    "float": "float",
    "double": "double",
}

# Length constants the C# struct needs, in the generated file, by name.
DIM_CONSTANTS = [
    "DRELAY_MAX_TARGETS",
    "DRELAY_MAX_STEPS",
    "DRELAY_MAX_READS",
    "DRELAY_TARGET_NAME_LEN",
    "DRELAY_FIELD_NAME_LEN",
    "DRELAY_LOG_LINE_LEN",
    "DRELAY_LOG_RING_LINES",
    "DRELAY_TRACE_RING_ROWS",
    "DRELAY_FRAME_RING_ROWS",
]


def cs_type(typ):
    key = typ.replace("volatile ", "")
    if key in CS_TYPES:
        return CS_TYPES[key]
    if key == "char":
        return "byte"
    return None


def emit_field(indent, row, known):
    """One C# field declaration for a computed layout row.

    The mapping is decided by the field's *total size* against its element size,
    not by its declared name, so a new array field in the native header is
    handled without touching this generator:

      * one byte                 -> a plain `byte`
      * N bytes, element 1 byte  -> a `fixed byte name[N]`
      * N bytes, element k bytes -> a `name_First` element plus a `name_Count`,
                                    because C# has no fixed-size buffer for
                                    elements wider than a byte
      * an embedded struct       -> that struct's generated type
    """
    base = row["type"].replace("volatile ", "")
    out = []
    if base in known:
        # An embedded struct.
        elem_size, _ = known[base]
        if row["size"] == elem_size:
            out.append("%s[FieldOffset(%d)] public %s %s;"
                       % (indent, row["offset"], base, row["name"]))
            return out
        # An array of structs.  Each element needs its own explicitly-offset
        # field: C# cannot express `T name[N]` except for single-byte elements,
        # and `[MarshalAs]` arrays are not permitted in an Explicit-layout struct
        # at all.  The generated names are `<singular>0..N-1`, and the singular
        # form is derived by stripping a trailing `s` -- `targets` becomes
        # `t0..t7`.  A generator that emitted `targets_First` plus a count instead
        # compiled fine and was useless, because the accessor that maps an index
        # onto a field had nothing to switch on.
        singular = row["name"][:-1] if row["name"].endswith("s") else row["name"]
        count = row["size"] // elem_size
        for i in range(count):
            out.append("%s[FieldOffset(%d)] public %s %s%d;"
                       % (indent, row["offset"] + (i * elem_size), base, singular, i))
        out.append("%spublic const int %s_Count = %d;" % (indent, row["name"], count))
        return out

    elem_size, _ = SCALARS[base]
    ct = cs_type(row["type"])
    if ct is None:
        raise SystemExit("no C# mapping for field %s of type %s" % (row["name"], row["type"]))

    if row["size"] == elem_size:
        out.append("%s[FieldOffset(%d)] public %s %s;" % (indent, row["offset"], ct, row["name"]))
        return out

    if row["size"] % elem_size != 0:
        # A value type with padding that is not a whole number of elements.
        out.append("%s// %s: %s occupies %d bytes, emitted as a raw block"
                   % (indent, row["name"], base, row["size"]))
        out.append("%s[FieldOffset(%d)] public unsafe fixed byte %s[%d];"
                   % (indent, row["offset"], row["name"], row["size"]))
        return out

    count = row["size"] // elem_size
    if elem_size == 1:
        out.append("%s[FieldOffset(%d)] public unsafe fixed byte %s[%d];"
                   % (indent, row["offset"], row["name"], count))
        out.append("%spublic const int %s_Count = %d;" % (indent, row["name"], count))
        return out

    # Wider elements: expose the first and the count, and let callers index with
    # raw offsets.  Declaring a fixed buffer of ushort/uint/ulong is not
    # expressible in C# without unsafe generics, and a `[MarshalAs]` array member
    # would not be permitted in an Explicit-layout struct at all.
    out.append("%s[FieldOffset(%d)] public %s %s_First;" % (indent, row["offset"], ct, row["name"]))
    out.append("%spublic const int %s_Count = %d;" % (indent, row["name"], count))
    return out


def render(header_text, version):
    known = {}
    # Every array dimension and length is read from the header's own #defines.
    # This dict used to hold a second copy of six of them, which is exactly the
    # duplication the generator exists to remove: changing a ring size in the
    # header left the model's copy stale and the totals disagreed.
    dims = {}

    lines = []
    lines.append("// <auto-generated>")
    lines.append("//   Generated by autododgeresearch/tools/gen_shared_layout.py from")
    lines.append("//   autododgeresearch/hook/native/DrelayHookShared.h.  DO NOT EDIT.")
    lines.append("//")
    lines.append("//   Every [FieldOffset] below is computed from the native struct's own")
    lines.append("//   declaration.  Hand-writing them was tried and produced three wrong")
    lines.append("//   strides; see the generator's docstring.")
    lines.append("// </auto-generated>")
    lines.append("")
    lines.append("using System;")
    lines.append("using System.Runtime.InteropServices;")
    lines.append("")
    lines.append("namespace Drelay.Bootstrap")
    lines.append("{")

    # Sub-structs first, so the shared struct can embed them by size.
    #
    # Their field offsets are kept, not just their totals, because a struct whose
    # *offsets* the managed side restates by hand is a struct whose offsets will
    # eventually be wrong.  `PublishTargets` did exactly that with
    # `DrelayTargetHash`: it wrote the 32-byte IL hash at byte 40 of the record
    # when the native header puts it at byte 34, so every target's hash arrived
    # with six zero bytes in front of it and the detour's `memcmp` could never
    # match -- which is precisely the "no target has ever been observed matching"
    # gap Phase 1 recorded as unverified.  It also wrote `found` at 68 instead of
    # 66, landing inside `reserved0`, and read it back as non-zero for every
    # target because the misplaced hash covered that byte.
    struct_offsets = {}
    for cpp_name, cs_name, stride_const in STRUCTS:
        rows, total, alignment = compute_layout(header_text, cpp_name, known, dims)
        # An embedded struct aligns to *its own* alignment, not to eight.  See
        # compute_layout's docstring for what assuming otherwise cost.
        known[cpp_name] = (total, alignment)
        struct_offsets[stride_const] = rows
        lines.append("    /// <summary>%s: %d bytes.</summary>" % (cpp_name, total))
        lines.append("    [StructLayout(LayoutKind.Explicit, Size = %d, CharSet = CharSet.Ansi)]"
                     % total)
        lines.append("    public struct %s" % cs_name)
        lines.append("    {")
        for row in rows:
            lines.extend(emit_field("        ", row, known))
        lines.append("    }")
        lines.append("")

    rows, total, _ = compute_layout(header_text, "DrelayShared", known, dims)

    lines.append("    /// <summary>")
    lines.append("    /// The shared block, field for field as the native header declares it.")
    lines.append("    /// %d bytes including the reserved tail." % total)
    lines.append("    /// </summary>")
    lines.append("    [StructLayout(LayoutKind.Explicit, Size = %d, CharSet = CharSet.Ansi)]" % total)
    lines.append("    public struct DrelayShared")
    lines.append("    {")
    for row in rows:
        lines.extend(emit_field("        ", row, known))
    lines.append("    }")
    lines.append("")

    # The offsets a reader needs, as data, plus the constants the C# side uses.
    lines.append("    /// <summary>Byte offsets and sizes, as computed from the native header.</summary>")
    lines.append("    public static class GeneratedLayout")
    lines.append("    {")
    lines.append("        public const uint SharedVersion = %du;" % version)
    lines.append("        public const int DrelaySharedSize = %d;" % total)
    # Every length constant the header defines, so the managed side never
    # restates one.  Read from the header rather than listed by hand: the first
    # version enumerated them, which meant a constant added to the header was
    # invisible here and produced five CS0117s in SharedProtocol.cs.
    for dim, value in sorted(parse_defines(header_text).items()):
        if dim in ("DRELAY_SHARED_VERSION", "DRELAY_SHARED_SIZE", "DRELAY_ABI_VERSION"):
            continue
        lines.append("        public const int %s = %d;" % (dim, value))
    for dim, value in sorted(dims.items()):
        # Skip anything the header already defines: the first version emitted
        # both spellings and C# rejects a duplicate constant with CS0102.
        if dim in parse_defines(header_text):
            continue
        lines.append("        public const int %s = %d;" % (dim, value))
    for cpp_name, cs_name, stride_const in STRUCTS:
        lines.append("        public const int %s = %d;" % (stride_const, known[cpp_name][0]))
    # One offset constant per field of every sub-struct, so a managed writer can
    # address a record by name instead of by a number copied out of the header.
    # The prefix is the stride constant minus "Stride": `TargetStride` gives
    # `Target_hash`, `Target_found`, and so on.
    for stride_const, sub_rows in sorted(struct_offsets.items()):
        prefix = stride_const[:-len("Stride")] if stride_const.endswith("Stride") else stride_const
        for row in sub_rows:
            lines.append("        public const int %s_%s = %d;"
                         % (prefix, row["name"], row["offset"]))
    # One offset constant per field, named after the field, so a C# accessor can
    # say GeneratedLayout.Off_<field> instead of repeating a number.  Emitted for
    # *every* field, because the previous hard-coded list of names silently
    # emitted nothing at all: the list used PascalCase names and the header uses
    # lowerCamelCase, so every lookup missed.
    for row in rows:
        lines.append("        public const int Off_%s = %d;" % (row["name"], row["offset"]))
    lines.append("    }")
    lines.append("}")
    return "\n".join(lines) + "\n", total


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--header", default=DEFAULT_HEADER)
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--print", dest="dump", action="store_true")
    args = ap.parse_args()

    try:
        header_text = strip_comments(io.open(args.header, encoding="utf-8").read())
    except OSError as exc:
        print("cannot read %s: %s" % (args.header, exc), file=sys.stderr)
        return 1

    version = parse_defines(header_text).get("DRELAY_SHARED_VERSION")
    if version is None:
        print("DRELAY_SHARED_VERSION not found in the header", file=sys.stderr)
        return 1

    text, total = render(header_text, version)

    if args.dump:
        print(text)
        print("# DrelayShared total: %d bytes, version %d" % (total, version))
        return 0

    if args.check:
        try:
            current = io.open(args.out, encoding="utf-8").read()
        except OSError:
            print("%s does not exist; run the generator" % args.out, file=sys.stderr)
            return 1
        if current != text:
            print("%s is STALE with respect to %s" % (args.out, args.header), file=sys.stderr)
            return 1
        print("%s is up to date (%d bytes)" % (args.out, total))
        return 0

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    io.open(args.out, "w", encoding="utf-8", newline="\n").write(text)
    print("wrote %s (DrelayShared = %d bytes, version %d)" % (args.out, total, version))
    return 0


if __name__ == "__main__":
    sys.exit(main())
