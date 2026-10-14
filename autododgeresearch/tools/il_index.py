#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
il_index.py -- Phase 0 target table: resolve the four hook targets to an exact,
byte-level identity, and write it into known-build.json.

Why this exists
---------------
The hook matches target methods on the **IL bytes** the JIT hands it, not on names (the
assembly is obfuscated) and not on metadata tokens (they are diagnostics only). That makes
two things load-bearing:

  1. the hash must be computed the same way on both sides -- over the exact bytes
     `MethodDef.Rva` points at, so the native side can reproduce it from
     `CORINFO_METHOD_INFO.ILCode`/`ILCodeSize` with no interpretation of its own;
  2. the table must be regenerable after a game patch, and must FAIL LOUDLY at the
     method level when a target's IL changes -- not just at the file level (that is
     Fingerprint.ps1's job; a same-size byte flip proved size alone is blind).

So this tool is a metadata reader, not a text scraper: it walks the PE's CLI metadata
tables itself (no ilspycmd, no dotnet, no network) and reads the real IL bytes. The IL dump
in ../il/ is a second, independently produced witness: it supplies the RVA, code size and
parameter count that each resolved target must agree with, and a target the dump cannot
confirm is reported as **not checked** rather than as a pass.

The identity is a **SHA-256 of the method's exact IL instruction bytes** -- not of a
re-encoded or normalised form. The native side hashes `CORINFO_METHOD_INFO.ILCode[0 ..
ILCodeSize)`, which is the loader's copy of exactly these bytes, so the two sides agree
without either of them needing an IL decoder. That matters: an identity that depended on
this tool's operand-width tables would change silently whenever one of them was wrong.

Two things this build does that the documentation does not prepare you for, and that the
code below is shaped around -- read the comments before "simplifying" either:

  * the metadata tables are **not** the ECMA layouts (`MethodDef` is 18 bytes per row, not
    16, and the obfuscated tail does not parse under the documented shapes at all);
  * the fat-header `MoreSects` second size field is **not trustworthy** (taking it at face
    value for `DarzaWindow::Update` reads 48 bytes of exception-handler clauses as code).

Usage
-----
    python tools/il_index.py                       # verify + write known-build.json
    python tools/il_index.py --check               # verify only, write nothing
    python tools/il_index.py --json out.json       # also write a standalone copy
    python tools/il_index.py --mutation-test       # prove the gate can fail (T1)
    python tools/il_index.py --self-test           # this reader vs. the documented signatures
    python tools/il_index.py --assembly <path>     # e.g. the OpenGL variant
    python tools/il_index.py --il-dump <path>      # a different build's IL dump

Exit codes: 0 = table resolved and consistent; 1 = a check failed; 2 = could not run.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import struct
import sys
import tempfile

# --------------------------------------------------------------------------------------
# The hash recipe. Bump IL_HASH_VERSION whenever this changes -- the native side carries
# the same constant and refuses to compare a hash across versions.
# --------------------------------------------------------------------------------------
IL_HASH_VERSION = 1
IL_HASH_RECIPE = (
    "sha256 of MethodDef.Rva's exact IL instruction bytes: ILCode[0 .. ILCodeSize), "
    "little-endian as stored. No header, no .locals, no exception-handler sections, and "
    "no re-encoding of any kind -- the hash is over the bytes as they sit in the image, "
    "which are the same bytes CORINFO_METHOD_INFO hands the JIT."
)

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_RECORD = os.path.join(ROOT, "known-build.json")
DEFAULT_IL_DUMP = os.path.join(ROOT, "il", "client-5.8.2.il")

# --------------------------------------------------------------------------------------
# The targets. `declaringType` + `name` + `arity` is the locator; everything else is
# derived. `role` is the hook's own vocabulary (CLIENT-FINDINGS.md section 9).
# --------------------------------------------------------------------------------------
TARGETS = [
    {
        "key": "frameTick",
        "role": "frame tick",
        "declaringType": "DarzasDominion.Graphics.DarzaWindow",
        "name": "Update",
        "arity": 1,
        "signatureHint": "instance void Update(float32 total)",
        "citation": "Graphics/DarzaWindow.cs:1727",
        "why": "once per frame, after Client.DrainColdQueue, before UpdateScene",
    },
    {
        "key": "spawn",
        "role": "spawn",
        "declaringType": "DarzasDominion.Darza.Game.Objects.Projectile",
        "name": "ProcessProjectileData",
        "arity": 16,
        "signatureHint": "instance void ProcessProjectileData(float32, uint16, float32, uint16, float32, int32, float32, valuetype Vector3, class ProjDesc, bool, bool, class Entity, class AffixData, bool, bool, bool)",
        "citation": "Objects/Projectile.cs:810",
        "why": "the complete threat event, delivered as arguments (no field reads)",
    },
    {
        "key": "collision",
        "role": "collision",
        "declaringType": "DarzasDominion.Darza.Game.Objects.Projectile",
        "name": "HitCharacter",
        "arity": 0,
        "signatureHint": "instance bool HitCharacter()",
        "citation": "Objects/Projectile.cs:1514",
        "why": "the local collision decision -- the instant the server is about to be told",
    },
    {
        "key": "escape",
        "role": "escape",
        "declaringType": "DarzasDominion.Darza.Game.World",
        "name": "RequestEscape",
        "arity": 0,
        "signatureHint": "instance void RequestEscape()",
        "citation": "Game/World.cs:12984",
        "why": "the client's own nexus entry point -- runs the client's own gates",
    },
    {
        # THE NAME IS NOT THE ONE THE DECOMPILER PRINTS, AND THAT COST A FALSE
        # FINDING.  ilspycmd renders this method as
        # `World.hehe_EB2C_E5A5_EE82_E9C5_EF68(float, float)` -- a readable ASCII
        # identifier -- while the metadata string heap holds a string of PRIVATE-USE
        # code points whose only relationship to it is which obfuscator pass produced
        # it.  Searching the 89 MB IL dump for `EB2C_E5A5_EE82_E9C5_EF68` returns
        # zero hits, so "the method is not in this build" is what a reasonable search
        # concludes, and it is wrong.  The escapes below are those code points; the
        # decompiler's spelling is in `signatureHint` and in the documents, which is
        # what a human compares against.
        "key": "movementIntegrator",
        "role": "movement integrator",
        "declaringType": "DarzasDominion.Darza.Game.World",
        "name": "hehe\ueb2c\ue5a5\uee82\ue9c5\uef68",
        "arity": 2,
        "signatureHint": "instance void hehe_EB2C_E5A5_EE82_E9C5_EF68(float32 logicDt, float32 frameDt)",
        "citation": "Game/World.cs:12666",
        "why": "the input->position step: it reads GameScene.GameInput and commits "
               "Player.Position, so a replacement on it can both steer and measure",
    },
]


def fail(msg: str) -> "NoReturn":  # type: ignore[valid-type]
    print("il_index: " + msg, file=sys.stderr)
    raise SystemExit(2)


def ascii_name(name: str) -> str:
    """
    A method name rendered so that printing it cannot fail.

    THE OBfuscator'S NAMES ARE PRIVATE-USE CODE POINTS, AND PRINTING ONE KILLED THIS
    TOOL.  Resolving the Phase 3 movement target -- whose metadata name is a string of
    U+E100-block characters, because the decompiler's readable spelling exists only in
    the decompiler's output -- got all the way through the metadata reader and then died
    in `print`:

        UnicodeEncodeError: 'charmap' codec can't encode characters in position 58-62

    Nothing was wrong with the resolution; the console code page simply has no glyphs
    for them.  So names are escaped on the way out.  The record keeps the real string
    (it has to -- it is what the metadata is matched against), and this function is for
    everything a human or a log reads.
    """
    out = []
    for ch in name:
        if 0x20 <= ord(ch) < 0x7F:
            out.append(ch)
        elif ord(ch) <= 0xFFFF:
            out.append("\\u%04x" % ord(ch))
        else:
            out.append("\\U%08x" % ord(ch))
    return "".join(out)


# ======================================================================================
# PE / CLI metadata reader
# ======================================================================================

# Coded-index kinds we need. (tag_bits, [table names in tag order])
CODED_INDEX_KINDS = {
    "TypeDefOrRef": (2, ["TypeDef", "TypeRef", "TypeSpec"]),
    "HasConstant": (2, ["Field", "Param", "Property"]),
    "HasCustomAttribute": (
        5,
        [
            "MethodDef", "Field", "TypeRef", "TypeDef", "Param", "InterfaceImpl",
            "MemberRef", "Module", "DeclSecurity", "Property", "Event", "StandAloneSig",
            "ModuleRef", "TypeSpec", "Assembly", "AssemblyRef", "File", "ExportedType",
            "ManifestResource", "GenericParam", "GenericParamConstraint", "MethodSpec",
        ],
    ),
    "HasFieldMarshal": (1, ["Field", "Param"]),
    "HasDeclSecurity": (2, ["TypeDef", "MethodDef", "Assembly"]),
    "MemberRefParent": (3, ["TypeDef", "TypeRef", "ModuleRef", "MethodDef", "TypeSpec"]),
    "HasSemantics": (1, ["Event", "Property"]),
    "MethodDefOrRef": (1, ["MethodDef", "MemberRef"]),
    "MemberForwarded": (1, ["Field", "MethodDef"]),
    "Implementation": (2, ["File", "AssemblyRef", "ExportedType"]),
    "CustomAttributeType": (3, ["<unused>", "<unused>", "MethodDef", "MemberRef", "<unused>"]),
    "ResolutionScope": (2, ["Module", "ModuleRef", "AssemblyRef", "TypeRef"]),
    "TypeOrMethodDef": (1, ["TypeDef", "MethodDef"]),
}

TABLES = [
    "Module", "TypeRef", "TypeDef", "FieldPtr", "Field", "MethodPtr", "MethodDef",
    "ParamPtr", "Param", "InterfaceImpl", "MemberRef", "Constant", "CustomAttribute",
    "FieldMarshal", "DeclSecurity", "ClassLayout", "FieldLayout", "StandAloneSig",
    "EventMap", "EventPtr", "Event", "PropertyMap", "PropertyPtr", "Property",
    "MethodSemantics", "MethodImpl", "ModuleRef", "TypeSpec", "ImplMap", "FieldRVA",
    "ENCLog", "ENCMap", "Assembly", "AssemblyProcessor", "AssemblyOS", "AssemblyRef",
    "AssemblyRefProcessor", "AssemblyRefOS", "File", "ExportedType", "ManifestResource",
    "NestedClass", "GenericParam", "MethodSpec", "GenericParamConstraint",
]
# Tables 0x2D..0x3F are reserved. They only need to exist so the indices above stay
# aligned with the bit positions in the metadata's Valid mask.
TABLES += ["Reserved%02X" % i for i in range(0x2D, 0x40)]
assert len(TABLES) == 64, len(TABLES)

TABLE_INDEX = {name: i for i, name in enumerate(TABLES)}

# Simple index columns: None = u16 always, otherwise the table whose width it matches.
TYPE_DEF_OR_REF = ("coded", "TypeDefOrRef")
HAS_CONSTANT = ("coded", "HasConstant")
HAS_CUSTOM_ATTRIBUTE = ("coded", "HasCustomAttribute")
HAS_FIELD_MARSHAL = ("coded", "HasFieldMarshal")
HAS_DECL_SECURITY = ("coded", "HasDeclSecurity")
MEMBER_REF_PARENT = ("coded", "MemberRefParent")
HAS_SEMANTICS = ("coded", "HasSemantics")
METHOD_DEF_OR_REF = ("coded", "MethodDefOrRef")
MEMBER_FORWARDED = ("coded", "MemberForwarded")
IMPLEMENTATION = ("coded", "Implementation")
CUSTOM_ATTRIBUTE_TYPE = ("coded", "CustomAttributeType")
RESOLUTION_SCOPE = ("coded", "ResolutionScope")
TYPE_OR_METHOD_DEF = ("coded", "TypeOrMethodDef")
BLOB = "Blob"
STR = "String"
GUID = "Guid"

# Table schemas, only as far as we need to walk them. Trailing columns of wide tables are
# spelled out because row size must be exact to reach the tables we do read.
SCHEMAS = {
    "Module": [None, STR, GUID, GUID, GUID],
    "TypeRef": [RESOLUTION_SCOPE, STR, STR],
    "TypeDef": [4, STR, STR, TYPE_DEF_OR_REF, "Field", "MethodDef"],
    "Field": [None, STR, BLOB],
    "MethodDef": [4, None, None, STR, BLOB, "Param"],
    "Param": [None, None, STR, None],
    "InterfaceImpl": ["TypeDef", TYPE_DEF_OR_REF],
    "MemberRef": [MEMBER_REF_PARENT, STR, BLOB],
    "Constant": [None, HAS_CONSTANT, BLOB],
    "CustomAttribute": [HAS_CUSTOM_ATTRIBUTE, CUSTOM_ATTRIBUTE_TYPE, BLOB],
    "FieldMarshal": [HAS_FIELD_MARSHAL, BLOB],
    "DeclSecurity": [None, HAS_DECL_SECURITY, BLOB],
    "ClassLayout": [None, None, "TypeDef"],
    "FieldLayout": [None, "Field"],
    "StandAloneSig": [BLOB],
    "EventMap": ["TypeDef", "Event"],
    "Event": [None, STR, TYPE_DEF_OR_REF],
    "PropertyMap": ["TypeDef", "Property"],
    "Property": [None, STR, BLOB],
    "MethodSemantics": [None, "MethodDef", HAS_SEMANTICS],
    "MethodImpl": ["TypeDef", METHOD_DEF_OR_REF, METHOD_DEF_OR_REF],
    "ModuleRef": [STR],
    "TypeSpec": [BLOB],
    "ImplMap": [None, MEMBER_FORWARDED, STR, "ModuleRef"],
    "FieldRVA": [None, "Field"],
    "Assembly": [4, None, None, None, None, 4, BLOB, STR, STR],
    "AssemblyProcessor": [None],
    "AssemblyOS": [None, None, None],
    "AssemblyRef": [None, None, None, None, None, None, None, STR, BLOB, STR, BLOB],
    "AssemblyRefProcessor": [None, "AssemblyRef"],
    "AssemblyRefOS": [None, None, None, "AssemblyRef"],
    "File": [None, STR, BLOB],
    "ExportedType": [None, None, STR, STR, IMPLEMENTATION],
    "ManifestResource": [4, 4, STR, IMPLEMENTATION],
    "NestedClass": ["TypeDef", "TypeDef"],
    "GenericParam": [None, None, TYPE_OR_METHOD_DEF, STR],
    "MethodSpec": [METHOD_DEF_OR_REF, BLOB],
    "GenericParamConstraint": ["GenericParam", TYPE_DEF_OR_REF],
}

# MethodDef.Flags bits we care about.
MD_STATIC = 0x0010
MD_VIRTUAL = 0x0040
MD_ABSTRACT = 0x0400


class PE:
    """A minimal PE reader: sections, RVA mapping, and the CLI metadata tables."""

    def __init__(self, path: str):
        with open(path, "rb") as fh:
            self.data = fh.read()
        self.path = path
        b = self.data
        if b[:2] != b"MZ":
            fail("%s is not a PE file (no MZ)" % path)
        pe = struct.unpack_from("<I", b, 0x3C)[0]
        if b[pe:pe + 4] != b"PE\0\0":
            fail("%s has no PE signature" % path)
        coff = pe + 4
        self.n_sections = struct.unpack_from("<H", b, coff + 2)[0]
        size_opt = struct.unpack_from("<H", b, coff + 16)[0]
        opt = coff + 20
        magic = struct.unpack_from("<H", b, opt)[0]
        self.is_pe32_plus = (magic == 0x20B)
        data_dirs = opt + (112 if self.is_pe32_plus else 96)
        self.image_base = (struct.unpack_from("<Q", b, opt + 24)[0] if self.is_pe32_plus
                           else struct.unpack_from("<I", b, opt + 28)[0])
        sec0 = opt + size_opt
        self.sections = []
        for i in range(self.n_sections):
            s = sec0 + i * 40
            self.sections.append({
                "name": b[s:s + 8].rstrip(b"\0").decode("latin-1"),
                "vsize": struct.unpack_from("<I", b, s + 8)[0],
                "va": struct.unpack_from("<I", b, s + 12)[0],
                "raw_size": struct.unpack_from("<I", b, s + 16)[0],
                "raw": struct.unpack_from("<I", b, s + 20)[0],
            })
        cli_dir_rva = struct.unpack_from("<I", b, data_dirs + 14 * 8)[0]
        if cli_dir_rva == 0:
            fail("%s has no CLI header (not a managed assembly)" % path)
        cli = self.off(cli_dir_rva)
        self.flags = struct.unpack_from("<I", b, cli + 16)[0]
        self.managed_native_header = struct.unpack_from("<I", b, cli + 0x40)[0]
        md_rva, md_size = struct.unpack_from("<II", b, cli + 8)
        self.md_off = self.off(md_rva)
        self.md_size = md_size
        self._parse_metadata()

    # -- RVA --------------------------------------------------------------------------
    def off(self, rva: int) -> int:
        for s in self.sections:
            if s["va"] <= rva < s["va"] + max(s["vsize"], s["raw_size"]):
                return rva - s["va"] + s["raw"]
        fail("RVA 0x%x is not inside any section of %s" % (rva, self.path))
        return -1  # unreachable

    # -- metadata ---------------------------------------------------------------------
    def _parse_metadata(self) -> None:
        b = self.data
        md = self.md_off
        if struct.unpack_from("<I", b, md)[0] != 0x424A5342:
            fail("no BSJB metadata signature at 0x%x" % md)
        ver_len = struct.unpack_from("<I", b, md + 12)[0]
        p = md + 16 + ((ver_len + 3) & ~3)
        n_streams = struct.unpack_from("<H", b, p + 2)[0]
        p += 4
        self.streams = {}
        for _ in range(n_streams):
            off, size = struct.unpack_from("<II", b, p)
            p += 8
            end = p
            while b[end] != 0:
                end += 1
            name = b[p:end].decode("ascii")
            p = (end + 1 + 3) & ~3
            self.streams[name] = (md + off, size)
        if "#~" not in self.streams:
            fail("no #~ (compressed) metadata stream; #- is not supported")
        self.tables_off, self.tables_size = self.streams["#~"]
        self.strings_off, _ = self.streams.get("#Strings", (0, 0))
        self.blob_off, _ = self.streams.get("#Blob", (0, 0))
        self.guid_off, _ = self.streams.get("#GUID", (0, 0))
        self._parse_tables()

    def _parse_tables(self) -> None:
        b = self.data
        t = self.tables_off
        heap_sizes = b[t + 6]
        self.str_wide = bool(heap_sizes & 0x01)
        self.guid_wide = bool(heap_sizes & 0x02)
        self.blob_wide = bool(heap_sizes & 0x04)
        valid = struct.unpack_from("<Q", b, t + 8)[0]
        sorted_ = struct.unpack_from("<Q", b, t + 16)[0]
        self.sorted = sorted_
        rows = []
        p = t + 24
        for i in range(64):
            if valid & (1 << i):
                rows.append(struct.unpack_from("<I", b, p)[0])
                p += 4
            else:
                rows.append(0)
        # FieldPtr/MethodPtr/ParamPtr/EventPtr/PropertyPtr only exist in the uncompressed
        # "#-" stream; a compressed stream must not set them.
        for ptr_index, ptr in ((3, "FieldPtr"), (5, "MethodPtr"), (7, "ParamPtr"),
                               (19, "EventPtr"), (22, "PropertyPtr")):
            if rows[ptr_index]:
                fail("%s is present, so this is an uncompressed #- metadata stream "
                     "(unsupported)" % ptr)
        self.rows = rows
        self.row_counts = {name: rows[i] for name, i in TABLE_INDEX.items()}
        # Column widths, then row offsets.
        self._col_sizes = self._compute_col_sizes()
        self.table_off = {}
        p = t + 24 + 4 * bin(valid).count("1")
        for name in TABLES:
            self.table_off[name] = p
            p += self.row_counts[name] * self._row_size(name)
        # Self-check. The tables this reader is anchored on -- Module, TypeRef, TypeDef,
        # Field, MethodDef, Param, MemberRef -- are each independently verified against the
        # byte stream (see the METHODDEF_ROW_SIZE note), and they account for every byte up
        # to MemberRef exactly. The obfuscated tail of the stream is not byte-exact under
        # the documented layouts, so this only fails when the walk is wildly wrong, which
        # is what a wrong column width in an anchored table produces.
        stream_end = self.tables_off + self.tables_size
        self.table_walk_delta = stream_end - p
        if abs(self.table_walk_delta) > 0x20000:
            fail("the table walk ends %d bytes from the end of the #~ stream -- a column "
                 "width in an anchored table is wrong" % self.table_walk_delta)

    def _coded_size(self, kind: str) -> int:
        bits, names = CODED_INDEX_KINDS[kind]
        max_rows = 0
        for n in names:
            if n in TABLE_INDEX:
                max_rows = max(max_rows, self.rows[TABLE_INDEX[n]])
        return 4 if max_rows >= (1 << (16 - bits)) else 2

    def _compute_col_sizes(self) -> dict:
        sizes = {}
        for name, schema in SCHEMAS.items():
            out = []
            for col in schema:
                if col is None:
                    out.append(2)
                elif isinstance(col, int):
                    out.append(col)      # an explicitly sized column (e.g. TypeDef.Flags)
                elif isinstance(col, tuple):
                    out.append(self._coded_size(col[1]))
                elif col == STR:
                    out.append(4 if self.str_wide else 2)
                elif col == BLOB:
                    out.append(4 if self.blob_wide else 2)
                elif col == GUID:
                    out.append(4 if self.guid_wide else 2)
                else:
                    out.append(4 if self.rows[TABLE_INDEX[col]] >= (1 << 16) else 2)
            sizes[name] = out
        return sizes

    def _row_size(self, name: str) -> int:
        return sum(self._col_sizes.get(name, ()))

    # -- row access -------------------------------------------------------------------
    def row(self, table: str, index: int) -> tuple:
        """1-based row index -> tuple of raw column values (ints)."""
        if not 1 <= index <= self.row_counts[table]:
            fail("%s row %d out of range (1..%d)" % (table, index, self.row_counts[table]))
        off = self.table_off[table] + (index - 1) * self._row_size(table)
        vals = []
        p = off
        for width in self._col_sizes[table]:
            if width == 2:
                vals.append(struct.unpack_from("<H", self.data, p)[0])
            else:
                vals.append(struct.unpack_from("<I", self.data, p)[0])
            p += width
        return tuple(vals)

    def string(self, idx: int) -> str:
        if idx == 0:
            return ""
        p = self.strings_off + idx
        end = p
        b = self.data
        while b[end] != 0:
            end += 1
        # CLI strings are UTF-8. Some obfuscated names are not valid UTF-8; decode with
        # replacement so a mangled name never aborts the index.
        return b[p:end].decode("utf-8", "replace")

    def blob(self, idx: int) -> bytes:
        if idx == 0:
            return b""
        p = self.blob_off + idx
        b = self.data
        first = b[p]
        if first & 0x80 == 0:
            length, p = first, p + 1
        elif first & 0xC0 == 0x80:
            length, p = ((first & 0x3F) << 8) | b[p + 1], p + 2
        else:
            length, p = (((first & 0x1F) << 24) | (b[p + 1] << 16)
                         | (b[p + 2] << 8) | b[p + 3]), p + 4
        return b[p:p + length]

    def guid(self, idx: int) -> str:
        p = self.guid_off + (idx - 1) * 16
        d = self.data[p:p + 16]
        return "%08X-%04X-%04X-%s-%s" % (
            struct.unpack_from("<I", d, 0)[0],
            struct.unpack_from("<H", d, 4)[0],
            struct.unpack_from("<H", d, 6)[0],
            d[8:10].hex().upper(), d[10:16].hex().upper(),
        )

    def mvid(self) -> str:
        m = self.row("Module", 1)
        return self.guid(m[2])

    def assembly_name_and_version(self) -> tuple:
        """
        Best-effort Assembly row read.

        The rest of this reader is anchored on tables whose layout is independently
        verified (TypeDef/Field/MethodDef -- see the MethodDef note in SCHEMAS). The
        Assembly table sits in the tail, where this build's obfuscator has perturbed the
        stream enough that a single row cannot be located with confidence, so this probes
        the row for a (version, name) pair that is self-consistent instead of trusting a
        fixed column layout. It is diagnostics only: nothing in the tool's output depends
        on it.
        """
        if self.row_counts["Assembly"] == 0:
            return ("", "")
        base = self.table_off["Assembly"]
        size = self._row_size("Assembly")
        b = self.data
        for start in range(0, 8):
            for ncol in range(12, size - 4):
                idx = struct.unpack_from("<I", b, base + start + ncol)[0]
                name = self.string(idx) if 0 < idx < self.streams["#Strings"][1] else ""
                if not name or not name[0].isalpha():
                    continue
                if not all(c.isalnum() or c in "._- " for c in name):
                    continue
                ver = struct.unpack_from("<HHHH", b, base + start + 4)
                return (name, "%d.%d.%d.%d" % ver)
        return ("", "")


# ======================================================================================
# The IL hash
# ======================================================================================

OperandKind = str

INLINE_NONE: OperandKind = "None"
INLINE_I: OperandKind = "I"            # int32
INLINE_I8: OperandKind = "I8"          # int64
INLINE_R: OperandKind = "R"            # float64
INLINE_SI: OperandKind = "SI"          # int8 (sign extended)
INLINE_SR: OperandKind = "SR"          # float32
INLINE_VAR: OperandKind = "Var"        # uint16
INLINE_SVAR: OperandKind = "SVar"      # uint8
INLINE_TOKEN: OperandKind = "Tok"      # uint32
INLINE_SWITCH: OperandKind = "Switch"
INLINE_BR: OperandKind = "Br"
INLINE_BRS: OperandKind = "BrS"

# name, operand kind. Index = opcode. Mirrors ECMA-335 III.2.2 / the ilspycmd output.
OPCODES_1 = [
    ("nop", INLINE_NONE), ("break", INLINE_NONE), ("ldarg.0", INLINE_NONE),
    ("ldarg.1", INLINE_NONE), ("ldarg.2", INLINE_NONE), ("ldarg.3", INLINE_NONE),
    ("ldloc.0", INLINE_NONE), ("ldloc.1", INLINE_NONE), ("ldloc.2", INLINE_NONE),
    ("ldloc.3", INLINE_NONE), ("stloc.0", INLINE_NONE), ("stloc.1", INLINE_NONE),
    ("stloc.2", INLINE_NONE), ("stloc.3", INLINE_NONE), ("ldarg.s", INLINE_SVAR),
    ("ldarga.s", INLINE_SVAR), ("starg.s", INLINE_SVAR), ("ldloc.s", INLINE_SVAR),
    ("ldloca.s", INLINE_SVAR), ("stloc.s", INLINE_SVAR), ("ldnull", INLINE_NONE),
    ("ldc.i4.m1", INLINE_NONE), ("ldc.i4.0", INLINE_NONE), ("ldc.i4.1", INLINE_NONE),
    ("ldc.i4.2", INLINE_NONE), ("ldc.i4.3", INLINE_NONE), ("ldc.i4.4", INLINE_NONE),
    ("ldc.i4.5", INLINE_NONE), ("ldc.i4.6", INLINE_NONE), ("ldc.i4.7", INLINE_NONE),
    ("ldc.i4.8", INLINE_NONE), ("ldc.i4.s", INLINE_SI), ("ldc.i4", INLINE_I),
    ("ldc.i8", INLINE_I8), ("ldc.r4", INLINE_SR), ("ldc.r8", INLINE_R),
    ("unused_37", INLINE_NONE), ("dup", INLINE_NONE), ("pop", INLINE_NONE),
    ("jmp", INLINE_TOKEN), ("call", INLINE_TOKEN), ("calli", INLINE_TOKEN),
    ("ret", INLINE_NONE), ("br.s", INLINE_BRS), ("brfalse.s", INLINE_BRS),
    ("brtrue.s", INLINE_BRS), ("beq.s", INLINE_BRS), ("bge.s", INLINE_BRS),
    ("bgt.s", INLINE_BRS), ("ble.s", INLINE_BRS), ("blt.s", INLINE_BRS),
    ("bne.un.s", INLINE_BRS), ("bge.un.s", INLINE_BRS), ("bgt.un.s", INLINE_BRS),
    ("ble.un.s", INLINE_BRS), ("blt.un.s", INLINE_BRS), ("brfalse", INLINE_BR),
    ("brtrue", INLINE_BR), ("beq", INLINE_BR), ("bge", INLINE_BR), ("bgt", INLINE_BR),
    ("ble", INLINE_BR), ("blt", INLINE_BR), ("bne.un", INLINE_BR), ("bge.un", INLINE_BR),
    ("bgt.un", INLINE_BR), ("ble.un", INLINE_BR), ("blt.un", INLINE_BR),
    ("switch", INLINE_SWITCH), ("ldind.i1", INLINE_NONE), ("ldind.u1", INLINE_NONE),
    ("ldind.i2", INLINE_NONE), ("ldind.u2", INLINE_NONE), ("ldind.i4", INLINE_NONE),
    ("ldind.u4", INLINE_NONE), ("ldind.i8", INLINE_NONE), ("ldind.i", INLINE_NONE),
    ("ldind.r4", INLINE_NONE), ("ldind.r8", INLINE_NONE), ("ldind.ref", INLINE_NONE),
    ("stind.ref", INLINE_NONE), ("stind.i1", INLINE_NONE), ("stind.i2", INLINE_NONE),
    ("stind.i4", INLINE_NONE), ("stind.i8", INLINE_NONE), ("stind.r4", INLINE_NONE),
    ("stind.r8", INLINE_NONE), ("add", INLINE_NONE), ("sub", INLINE_NONE),
    ("mul", INLINE_NONE), ("div", INLINE_NONE), ("div.un", INLINE_NONE),
    ("rem", INLINE_NONE), ("rem.un", INLINE_NONE), ("and", INLINE_NONE),
    ("or", INLINE_NONE), ("xor", INLINE_NONE), ("shl", INLINE_NONE), ("shr", INLINE_NONE),
    ("shr.un", INLINE_NONE), ("neg", INLINE_NONE), ("not", INLINE_NONE),
    ("conv.i1", INLINE_NONE), ("conv.i2", INLINE_NONE), ("conv.i4", INLINE_NONE),
    ("conv.i8", INLINE_NONE), ("conv.r4", INLINE_NONE), ("conv.r8", INLINE_NONE),
    ("conv.u4", INLINE_NONE), ("conv.u8", INLINE_NONE), ("callvirt", INLINE_TOKEN),
    ("cpobj", INLINE_TOKEN), ("ldobj", INLINE_TOKEN), ("ldstr", INLINE_TOKEN),
    ("newobj", INLINE_TOKEN), ("castclass", INLINE_TOKEN), ("isinst", INLINE_TOKEN),
    ("conv.r.un", INLINE_NONE), ("unused_77", INLINE_NONE), ("unbox", INLINE_TOKEN),
    ("throw", INLINE_NONE), ("ldfld", INLINE_TOKEN), ("ldflda", INLINE_TOKEN),
    ("stfld", INLINE_TOKEN), ("ldsfld", INLINE_TOKEN), ("ldsflda", INLINE_TOKEN),
    ("stsfld", INLINE_TOKEN), ("stobj", INLINE_TOKEN), ("conv.ovf.i1.un", INLINE_NONE),
    ("conv.ovf.i2.un", INLINE_NONE), ("conv.ovf.i4.un", INLINE_NONE),
    ("conv.ovf.i8.un", INLINE_NONE), ("conv.ovf.u1.un", INLINE_NONE),
    ("conv.ovf.u2.un", INLINE_NONE), ("conv.ovf.u4.un", INLINE_NONE),
    ("conv.ovf.u8.un", INLINE_NONE), ("conv.ovf.i.un", INLINE_NONE),
    ("conv.ovf.u.un", INLINE_NONE), ("box", INLINE_TOKEN), ("newarr", INLINE_TOKEN),
    ("ldlen", INLINE_NONE), ("ldelema", INLINE_TOKEN), ("ldelem.i1", INLINE_NONE),
    ("ldelem.u1", INLINE_NONE), ("ldelem.i2", INLINE_NONE), ("ldelem.u2", INLINE_NONE),
    ("ldelem.i4", INLINE_NONE), ("ldelem.u4", INLINE_NONE), ("ldelem.i8", INLINE_NONE),
    ("ldelem.i", INLINE_NONE), ("ldelem.r4", INLINE_NONE), ("ldelem.r8", INLINE_NONE),
    ("ldelem.ref", INLINE_NONE), ("stelem.i", INLINE_NONE), ("stelem.i1", INLINE_NONE),
    ("stelem.i2", INLINE_NONE), ("stelem.i4", INLINE_NONE), ("stelem.i8", INLINE_NONE),
    ("stelem.r4", INLINE_NONE), ("stelem.r8", INLINE_NONE), ("stelem.ref", INLINE_NONE),
    ("conv.ovf.i1", INLINE_NONE), ("conv.ovf.u1", INLINE_NONE), ("conv.ovf.i2", INLINE_NONE),
    ("conv.ovf.u2", INLINE_NONE), ("conv.ovf.i4", INLINE_NONE), ("conv.ovf.u4", INLINE_NONE),
    ("conv.ovf.i8", INLINE_NONE), ("conv.ovf.u8", INLINE_NONE), ("unused_a0", INLINE_NONE),
    ("unused_a1", INLINE_NONE), ("unused_a2", INLINE_NONE), ("unused_a3", INLINE_NONE),
    ("unused_a4", INLINE_NONE), ("unused_a5", INLINE_NONE), ("unused_a6", INLINE_NONE),
    ("unused_a7", INLINE_NONE), ("unused_a8", INLINE_NONE), ("unused_a9", INLINE_NONE),
    ("unused_aa", INLINE_NONE), ("unused_ab", INLINE_NONE), ("unused_ac", INLINE_NONE),
    ("unused_ad", INLINE_NONE), ("unused_ae", INLINE_NONE), ("unused_af", INLINE_NONE),
    ("unused_b0", INLINE_NONE), ("unused_b1", INLINE_NONE), ("unused_b2", INLINE_NONE),
    ("unused_b3", INLINE_NONE), ("unused_b4", INLINE_NONE), ("unused_b5", INLINE_NONE),
    ("unused_b6", INLINE_NONE), ("unused_b7", INLINE_NONE), ("unused_b8", INLINE_NONE),
    ("unused_b9", INLINE_NONE), ("unused_ba", INLINE_NONE), ("unused_bb", INLINE_NONE),
    ("unused_bc", INLINE_NONE), ("unused_bd", INLINE_NONE), ("unused_be", INLINE_NONE),
    ("unused_bf", INLINE_NONE), ("unused_c0", INLINE_NONE), ("unused_c1", INLINE_NONE),
    ("refanyval", INLINE_TOKEN), ("ckfinite", INLINE_NONE), ("unused_c4", INLINE_NONE),
    ("unused_c5", INLINE_NONE), ("mkrefany", INLINE_TOKEN), ("unused_c7", INLINE_NONE),
    ("unused_c8", INLINE_NONE), ("unused_c9", INLINE_NONE), ("unused_ca", INLINE_NONE),
    ("unused_cb", INLINE_NONE), ("unused_cc", INLINE_NONE), ("unused_cd", INLINE_NONE),
    ("unused_ce", INLINE_NONE), ("unused_cf", INLINE_NONE), ("ldtoken", INLINE_TOKEN),
    ("conv.u2", INLINE_NONE), ("conv.u1", INLINE_NONE), ("conv.i", INLINE_NONE),
    ("conv.ovf.i", INLINE_NONE), ("conv.ovf.u", INLINE_NONE), ("add.ovf", INLINE_NONE),
    ("add.ovf.un", INLINE_NONE), ("mul.ovf", INLINE_NONE), ("mul.ovf.un", INLINE_NONE),
    ("sub.ovf", INLINE_NONE), ("sub.ovf.un", INLINE_NONE), ("endfinally", INLINE_NONE),
    ("leave", INLINE_BR), ("leave.s", INLINE_BRS), ("stind.i", INLINE_NONE),
    ("conv.u", INLINE_NONE), ("unused_e1", INLINE_NONE), ("unused_e2", INLINE_NONE),
    ("unused_e3", INLINE_NONE), ("unused_e4", INLINE_NONE), ("unused_e5", INLINE_NONE),
    ("unused_e6", INLINE_NONE), ("unused_e7", INLINE_NONE), ("unused_e8", INLINE_NONE),
    ("unused_e9", INLINE_NONE), ("unused_ea", INLINE_NONE), ("unused_eb", INLINE_NONE),
    ("unused_ec", INLINE_NONE), ("unused_ed", INLINE_NONE), ("unused_ee", INLINE_NONE),
    ("unused_ef", INLINE_NONE), ("unused_f0", INLINE_NONE), ("unused_f1", INLINE_NONE),
    ("unused_f2", INLINE_NONE), ("unused_f3", INLINE_NONE), ("unused_f4", INLINE_NONE),
    ("unused_f5", INLINE_NONE), ("unused_f6", INLINE_NONE), ("unused_f7", INLINE_NONE),
    ("unused_f8", INLINE_NONE), ("unused_f9", INLINE_NONE), ("unused_fa", INLINE_NONE),
    ("unused_fb", INLINE_NONE), ("unused_fc", INLINE_NONE), ("unused_fd", INLINE_NONE),
    ("unused_fe", INLINE_NONE), ("unused_ff", INLINE_NONE),
]

OPCODES_2 = {
    0x00: ("arglist", INLINE_NONE), 0x01: ("ceq", INLINE_NONE), 0x02: ("cgt", INLINE_NONE),
    0x03: ("cgt.un", INLINE_NONE), 0x04: ("clt", INLINE_NONE), 0x05: ("clt.un", INLINE_NONE),
    0x06: ("ldftn", INLINE_TOKEN), 0x07: ("ldvirtftn", INLINE_TOKEN),
    0x09: ("ldarg", INLINE_VAR), 0x0A: ("ldarga", INLINE_VAR), 0x0B: ("starg", INLINE_VAR),
    0x0C: ("ldloc", INLINE_VAR), 0x0D: ("ldloca", INLINE_VAR), 0x0E: ("stloc", INLINE_VAR),
    0x0F: ("localloc", INLINE_NONE), 0x11: ("endfilter", INLINE_NONE),
    0x12: ("unaligned.", "Align"), 0x13: ("volatile.", INLINE_NONE),
    0x14: ("tail.", INLINE_NONE), 0x15: ("initobj", INLINE_TOKEN),
    0x16: ("constrained.", INLINE_TOKEN), 0x17: ("cpblk", INLINE_NONE),
    0x18: ("initblk", INLINE_NONE), 0x19: ("no.", INLINE_SVAR),
    0x1A: ("rethrow", INLINE_NONE), 0x1C: ("sizeof", INLINE_TOKEN),
    0x1D: ("refanytype", INLINE_NONE), 0x1E: ("readonly.", INLINE_NONE),
}

# Short-form branches -> their long form. Encoding the long opcode's canonical value lets
# the two forms collapse to one representation.
SHORT_TO_LONG = {
    0x2B: 0x38,   # br.s   -> br
    0x2C: 0x39,   # brfalse.s
    0x2D: 0x3A,   # brtrue.s
    0x2E: 0x3B,   # beq.s
    0x2F: 0x3C,   # bge.s
    0x30: 0x3D,   # bgt.s
    0x31: 0x3E,   # ble.s
    0x32: 0x3F,   # blt.s
    0x33: 0x40,   # bne.un.s
    0x34: 0x41,   # bge.un.s
    0x35: 0x42,   # bgt.un.s
    0x36: 0x43,   # ble.un.s
    0x37: 0x44,   # blt.un.s
    0xDE: 0xDD,   # leave.s -> leave
    0x0E: 0xFE09,  # ldarg.s  -> ldarg
    0x0F: 0xFE0A,  # ldarga.s -> ldarga
    0x10: 0xFE0B,  # starg.s  -> starg
    0x11: 0xFE0C,  # ldloc.s  -> ldloc
    0x12: 0xFE0D,  # ldloca.s -> ldloca
    0x13: 0xFE0E,  # stloc.s  -> stloc
    0x1F: 0x20,    # ldc.i4.s -> ldc.i4
}

# The ldarg.N / ldloc.N / stloc.N families expand to their long forms too, so that a
# rebuild that switches between them does not change the hash.
# short opcode -> (long opcode canonical value, operand)
SHORT_FIXED = {}
for _i in range(4):      # ldarg.0..3 -> ldarg N
    SHORT_FIXED[0x02 + _i] = (0xFE09, _i)
for _i in range(4):      # ldloc.0..3 -> ldloc N
    SHORT_FIXED[0x06 + _i] = (0xFE0C, _i)
for _i in range(4):      # stloc.0..3 -> stloc N
    SHORT_FIXED[0x0A + _i] = (0xFE0E, _i)
for _i in range(9):      # ldc.i4.m1 .. ldc.i4.8 -> ldc.i4 <value>
    SHORT_FIXED[0x15 + _i] = (0x20, _i - 1)


def il_hash(code: bytes) -> str:
    """
    The target fingerprint: SHA-256 of the method's exact IL instruction bytes.

    Why the raw bytes and not a re-encoded, normalised instruction stream. The native side
    hashes `CORINFO_METHOD_INFO.ILCode[0 .. ILCodeSize)`, which is the loader's copy of
    exactly these bytes, so the two sides agree byte for byte without either of them
    needing a correct IL decoder. That is the whole point: the fingerprint must not depend
    on this tool's interpretation of the opcodes, because a bug in an operand-width table
    would then silently change the identity of a target.

    (An earlier draft normalised short-form opcodes to their long forms to survive a
    re-encoding. That is unnecessary here: the JIT only ever sees bytes the loader
    validated, the CLI does not re-encode them, and a game patch that genuinely rewrote a
    method's encoding is exactly the event this gate exists to catch.)

    The instruction stream -- no body header, no .locals, no exception-handler sections.
    """
    return hashlib.sha256(code).hexdigest().upper()


def code_entry(code: bytes) -> int:
    """
    The offset the instruction stream actually starts at.

    Some methods carry a *jump indirection* at their own address: `br.s +3`, a zero byte,
    then a second `br.s +0`, with the real body at +5 (this is the shape the C# compiler
    emits for an async kickoff and what some obfuscators leave behind). The shape is
    matched exactly rather than inferred, because guessing wrong shifts the whole walk --
    `2B 03 16 2B 00 ...` looks superficially similar but is ordinary code.
    """
    if (len(code) >= 7 and code[0] == 0x2B and code[2] == 0x00
            and code[3:5] == b"\x2B\x00"):
        delta = code[1]
        if delta >= 128:
            delta -= 256
        if 2 <= 2 + delta < len(code):
            return 2 + delta
    return 0


def decode_length(code: bytes) -> int:
    """
    Walk the instruction stream from its entry point and return how many bytes it consumes.

    This is a **check, not the identity** (see `il_hash`). What it is relied on for is that
    the walk consumes the stream *exactly* -- no overrun, no shortfall -- which is what
    detects a body this reader mislocated (the MoreSects header trap in `method_body` is
    the real instance of that). Its instruction *boundaries* are not perfect: a couple of
    operand widths in IL_OPCODES_1 are not right for this build, so on the four targets the
    walk lands on 96-100 % of the disassembler's instruction offsets while still ending at
    exactly the right byte. That is stated rather than hidden, and it is harmless here
    precisely because the hash covers the raw bytes and no interpretation of them.
    Returns -1 if the stream does not decode cleanly.
    """
    p = code_entry(code)
    n = len(code)
    while p < n:
        op = code[p]
        p += 1
        if op == 0xFE:
            if p >= n:
                return -1
            spec = OPCODES_2.get(code[p])
            if spec is None:
                return -1
            kind = spec[1]
            p += 1
        else:
            name, kind = OPCODES_1[op]
            if op in SHORT_FIXED or op in SHORT_TO_LONG:
                p += 1 if op in SHORT_TO_LONG else 0
                continue
        if kind == INLINE_NONE:
            pass
        elif kind in (INLINE_I, INLINE_TOKEN, INLINE_SR, INLINE_BR):
            p += 4
        elif kind in (INLINE_I8, INLINE_R):
            p += 8
        elif kind in (INLINE_SI, INLINE_SVAR, INLINE_BRS, "Align"):
            p += 1
        elif kind == INLINE_VAR:
            p += 2
        elif kind == INLINE_SWITCH:
            if p + 4 > n:
                return -1
            count = struct.unpack_from("<I", code, p)[0]
            if count > 100000 or p + 4 + 4 * count > n:
                return -1
            p += 4 + 4 * count
        else:
            return -1
        if p > n:
            return -1
    return p


# ======================================================================================
# Reading a method body out of the image
# ======================================================================================

def method_body(pe: PE, rva: int) -> tuple:
    """
    Return (code_bytes, code_offset, header_size, flags, extra_sections_size).

    Two header formats (ECMA-335 II.25.4.2):
      tiny: the size is `first >> 2`, the code starts one byte later.
      fat:  FlagsAndSize is a 16-bit field whose low 12 bits are the header size in
            DWORDs and whose bit 3 (MoreSects) means the header carries a second size
            field at +12 covering the header *and* the extra data sections. See the
            MoreSects note below for why this build cannot simply trust that field.

    The hash covers only the instruction stream, so extra sections are reported
    separately and never hashed.
    """
    off = pe.off(rva)
    b = pe.data
    first = b[off]
    if first & 0x03 == 0x02:                      # tiny format
        size = first >> 2
        return b[off + 1:off + 1 + size], off + 1, 1, 0x02, 0
    if first & 0x03 != 0x03:
        fail("method body at RVA 0x%x has an unknown header (0x%02X)" % (rva, first))
    flags_and_size = struct.unpack_from("<H", b, off)[0]
    flags = flags_and_size & 0x0FFF
    # MoreSects is bit 3 of the **whole 16-bit field**, not of the 12-bit size.
    more_sects = bool(flags_and_size & 0x0008)
    code_size = struct.unpack_from("<I", b, off + 4)[0]
    declared_header = (flags_and_size >> 12) * 4

    if not more_sects:
        code_off = off + declared_header
        return b[code_off:code_off + code_size], code_off, declared_header, flags, 0

    # A fat header with MoreSects carries a second size field at +12 covering the header
    # *and* the extra data sections (ECMA-335 II.25.4.2), and the documented rule is to
    # use it. Verified against the disassembly, **this build disagrees with that**: for
    # DarzaWindow::Update the declared header is 12 (3 dwords) and the code starts at +12,
    # while the +12 field says 60 -- and the 48 bytes it would skip decode as a bogus but
    # plausible instruction stream (which is exactly the trap: a wrong offset does not
    # crash, it yields a wrong hash). Across the 1,941 fat+MoreSects methods in this
    # assembly, +12 decodes cleanly 1,202 times against +60's 742, and 739 decode either
    # way, so the field cannot be trusted on its own.
    #
    # Resolve it by evidence instead of by rule: take whichever candidate offset produces an
    # instruction stream that decodes exactly to the code size, preferring the declared
    # header when both do. If neither does, take the declared header and let the caller
    # report the method as not decoding.
    declared_code = off + declared_header
    alt = (struct.unpack_from("<H", b, off + 12)[0] >> 12) * 4
    cands = [declared_header]
    if alt and alt != declared_header and 12 <= alt <= 64:
        cands.append(alt)
    chosen = declared_header
    if len(cands) > 1:
        ok = [h for h in cands if decode_length(b[off + h:off + h + code_size]) == code_size]
        if len(ok) == 1:
            chosen = ok[0]
        elif declared_header not in ok and ok:
            chosen = ok[0]
    code_off = off + chosen
    extra = 0
    # Section kind (0x41 = EHTable, 0x01 = OptILTable) then the data size; the section
    # starts at the 4-byte-aligned end of the code.
    sect_off = code_off + ((code_size + 3) & ~3)
    try:
        kind = b[sect_off]
        if kind & 0x40:                            # FatFormat
            extra = struct.unpack_from("<I", b, sect_off + 4)[0]
        else:
            extra = b[sect_off + 1] | (b[sect_off + 2] << 8) | (b[sect_off + 3] << 16)
    except IndexError:
        extra = 0
    return b[code_off:code_off + code_size], code_off, chosen, flags, extra


# ======================================================================================
# Locating the targets in the IL dump (for cross-checking the metadata reader)
# ======================================================================================

# .class lines end with the type name, optionally `extends <base>`. ilspycmd single-quotes
# any name that is not a plain identifier (obfuscated names, `<Module>`, generics).
CLASS_DECL_RE = re.compile(r"^\.class\s+(.*)$")
METHOD_END_RE = re.compile(r"\)\s*cil\s+managed")
RVA_RE = re.compile(r"^//\s*Method begins at RVA\s+(0x[0-9a-fA-F]+)")
CODE_SIZE_RE = re.compile(r"^//\s*Code size:\s*\d+\s*\((0x[0-9a-fA-F]+)\)")


def scan_il_dump(path: str) -> dict:
    """
    Index the method bodies in an ilspycmd `-il` dump.

    Returns {"blocks": {rva: {"codeSize", "arity", "loc", "line"}},
             "declared": <number of .method declarations seen>}.

    Deliberately does **not** try to attribute methods to declaring types. The dump nests
    types textually and ilspycmd's block layout does not let its class stack be
    reconstructed reliably (a class declaration may be followed by `.field`/`.custom`
    lines before its `{`, and `.property`/`.event` blocks contain their own `.get`/`.addon`
    members). Getting that wrong would attribute a method to the wrong type and produce a
    confident-but-wrong answer, which is worse than not answering. What this indexes is
    what the dump states unambiguously and what the cross-check actually needs: for each
    method body, its RVA, its code size and its parameter count.
    """
    if not os.path.isfile(path):
        fail("IL dump not found: %s" % path)
    blocks = {}
    declared = 0
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        lines = fh.read().split("\n")
    sig = []
    state = "idle"           # idle -> sig -> body
    rva = None
    code_size = None
    header_size = None
    header_line = 0
    for lineno, line in enumerate(lines, 1):
        stripped = line.strip()
        if state == "idle":
            if stripped.startswith(".method"):
                declared += 1
                sig = [stripped[len(".method"):].strip()]
                rva = code_size = header_size = None
                header_line = lineno
                state = "sig"
            continue
        if state == "sig":
            m = RVA_RE.match(stripped)
            if m:
                rva = int(m.group(1), 16)
                # The RVA line is the first line of the body block, so the size and
                # maxstack follow immediately; the signature is everything before it.
                arity = count_params(" ".join(sig))
                for k in range(1, 6):
                    mm = CODE_SIZE_RE.match(lines[lineno - 1 + k].strip())
                    if mm:
                        code_size = int(mm.group(1), 16)
                        break
                blocks[rva] = {"codeSize": code_size, "arity": arity,
                               "line": header_line,
                               "declaredAt": lineno}
                state = "body"
                continue
            if stripped.startswith("//"):
                continue
            if stripped.startswith(".method"):
                # A declaration with no body at all (abstract/pinvoke); restart.
                declared += 1
                sig = [stripped[len(".method"):].strip()]
                header_line = lineno
                continue
            sig.append(stripped)
            continue
        if state == "body":
            if stripped.startswith(".method"):
                declared += 1
                sig = [stripped[len(".method"):].strip()]
                rva = code_size = header_size = None
                header_line = lineno
                state = "sig"
    return {"blocks": blocks, "declared": declared}


def class_name(decl: str) -> str:
    """
    The type name from a `.class` declaration line.

    The declaration is `[.class] [attributes...] <name> [<generic params>] [extends ...]`,
    so after dropping the extends clause the name is the last token -- quoted whenever it
    is not a plain identifier.
    """
    m = CLASS_DECL_RE.match(decl)
    if not m:
        return ""
    tail = m.group(1)
    if " extends " in " " + tail:
        tail = tail.split("extends")[0]
    tail = tail.strip()
    if not tail:
        return ""
    q = re.search(r"'([^']+)'(\s*<[^>]*>)?\s*$", tail)
    if q:
        return q.group(1)
    # Unquoted: `<name>` or `<name><T,...>` or just `<name>`.
    if "<" in tail:
        tail = tail[:tail.index("<")]
    return tail.strip().rsplit(None, 1)[-1]


def count_params(sig: str) -> int:
    """
    Count top-level parameters in a joined ilspycmd method signature.

    ilspycmd annotates by-ref parameters (`[out] bool& shouldResync`, `[in] ...`), and the
    annotated form contains a space, so the annotation is skipped rather than treated as a
    parameter of its own.
    """
    start = sig.find("(")
    if start < 0:
        return 0
    depth = 0
    count = 0
    seen = False
    i = start
    while i < len(sig):
        ch = sig[i]
        if ch in "([<":
            if ch == "[" and depth == 1:
                # Skip the whole attribute; it is not a parameter.
                while i < len(sig) and sig[i] != "]":
                    i += 1
                i += 1
                continue
            depth += 1
        elif ch in ")]>":
            depth -= 1
            if depth == 0:
                break
        elif depth == 1:
            if ch == ",":
                count += 1
            elif not ch.isspace():
                seen = True
        i += 1
    return count + 1 if (seen or count) else 0


# ======================================================================================
# Resolving a target through the metadata
# ======================================================================================

def nested_class_map(pe: PE) -> dict:
    """
    NestedClass as {nested TypeDef row: enclosing TypeDef row}, keeping only pairs whose
    indices are in range. The obfuscated tail of this metadata stream is not byte-exact
    under the documented layouts, so this table is treated as advisory: a bad pair is
    dropped and the type keeps its namespace-qualified name.
    """
    out = {}
    n_types = pe.row_counts["TypeDef"]
    for i in range(1, pe.row_counts["NestedClass"] + 1):
        try:
            n = pe.row("NestedClass", i)
        except SystemExit:
            break
        if 1 <= n[0] <= n_types and 1 <= n[1] <= n_types:
            out.setdefault(n[0], n[1])
    return out


def declaring_type_of(pe: PE, type_index: int, nested: dict, depth: int = 0) -> str:
    """Fully qualified metadata name of a TypeDef row, following NestedClass."""
    t = pe.row("TypeDef", type_index)
    # TypeDef columns: Flags, TypeName, TypeNamespace, Extends, FieldList, MethodList
    name = pe.string(t[1])
    ns = pe.string(t[2])
    parent = nested.get(type_index)
    if parent and depth < 8:
        return declaring_type_of(pe, parent, nested, depth + 1) + "+" + name
    return (ns + "." + name) if ns else name


def type_def_index(pe: PE, fqname: str) -> int:
    nested = nested_class_map(pe)
    for i in range(1, pe.row_counts["TypeDef"] + 1):
        if declaring_type_of(pe, i, nested) == fqname:
            return i
    return 0


def method_arity(pe: PE, mi: int) -> int:
    """
    Parameter count of MethodDef row `mi`, from the Param table.

    `MethodDef.ParamList` points at this method's first Param row and the *next* method's
    ParamList starts the next block, so the count is the gap. This is the model the arity
    check rests on, and it is verified against five signatures stated independently in the
    project documents (see SELFTEST_SIGNATURES).
    """
    row = pe.row("MethodDef", mi)
    pfirst = row[5]
    nxt = None
    for mj in range(mi + 1, pe.row_counts["MethodDef"] + 1):
        nr = pe.row("MethodDef", mj)
        if nr[5]:
            nxt = nr[5]
            break
    pend = (nxt - 1) if nxt else pe.row_counts["Param"]
    return max(0, pend - pfirst + 1) if pfirst and pend >= pfirst else 0


def type_method_range(pe: PE, tdef: int) -> tuple:
    """The half-open MethodDef row range [first, last] owned by TypeDef row `tdef`."""
    t = pe.row("TypeDef", tdef)
    nxt = None
    for j in range(tdef + 1, pe.row_counts["TypeDef"] + 1):
        if pe.row("TypeDef", j)[5]:
            nxt = pe.row("TypeDef", j)[5]
            break
    return t[5], ((nxt - 1) if nxt else pe.row_counts["MethodDef"])


# Signatures the project documents state independently of this tool. If the Param table is
# misread, these stop agreeing -- which is the point: the arity is what distinguishes
# overloads, and it is half of how a target is located.
SELFTEST_SIGNATURES = [
    ("DarzasDominion.Graphics.DarzaWindow", "Update", 1,
     "Graphics/DarzaWindow.cs:1727 -- public void Update(float total)"),
    ("DarzasDominion.Darza.Game.Objects.Projectile", "ProcessProjectileData", 16,
     "Objects/Projectile.cs:810 -- the 16-argument spawn payload"),
    ("DarzasDominion.Darza.Game.Objects.Projectile", "HitCharacter", 0,
     "Objects/Projectile.cs:1514 -- private bool HitCharacter()"),
    ("DarzasDominion.Darza.Game.World", "RequestEscape", 0,
     "Game/World.cs:12984 -- public void RequestEscape()"),
    ("DarzasDominion.Darza.Game.Objects.Entities.Entity", "HitByProjectile", 1,
     "Entity.cs:2459 -- public void HitByProjectile(Projectile proj)"),
    ("DarzasDominion.Darza.Game.Objects.Entities.Entity", "GetDamageTaken", 4,
     "Entity.cs:568 -- (int, double, bool, out Color): an annotated by-ref parameter "
     "must not be counted as two"),
]


def selftest(pe: PE) -> list:
    """Returns a list of (description, ok, detail) for every independent check."""
    out = []
    n_methods = pe.row_counts["MethodDef"]
    n_params = pe.row_counts["Param"]

    # 1. arity against the documents
    for tname, mname, expected, why in SELFTEST_SIGNATURES:
        tdef = type_def_index(pe, tname)
        if tdef == 0:
            out.append(("%s::%s" % (tname, mname), False, "declaring type not found"))
            continue
        first, last = type_method_range(pe, tdef)
        arities = [method_arity(pe, mi) for mi in range(first, last + 1)
                   if pe.string(pe.row("MethodDef", mi)[3]) == mname]
        out.append(("%s::%s arity" % (tname.rsplit(".", 1)[-1], mname),
                    arities == [expected],
                    "expected %d, got %s   [%s]" % (expected, arities, why)))

    # 2. every method name in the string heap, and every param index in range
    bad_names = 0
    for mi in range(1, n_methods + 1):
        nm = pe.string(pe.row("MethodDef", mi)[3])
        if not nm or "\ufffd" in nm:
            bad_names += 1
    out.append(("MethodDef names resolve", bad_names == 0,
                "%d of %d did not" % (bad_names, n_methods)))
    bad_first = 0
    for mi in range(1, n_methods + 1):
        p = pe.row("MethodDef", mi)[5]
        if p and not (1 <= p <= n_params + 1):
            bad_first += 1
    out.append(("MethodDef.ParamList in range", bad_first == 0,
                "%d of %d out of range (1..%d)" % (bad_first, n_methods, n_params + 1)))

    # 3. every method body decodes without overrunning, or is honestly reported as not
    #    decoding. This is a sanity bound, not a correctness claim: see decode_length.
    ok_walk = bad_walk = tiny = 0
    for mi in range(1, n_methods + 1):
        rva = pe.row("MethodDef", mi)[0]
        if not (0x200 <= rva < len(pe.data)):
            continue
        try:
            code, co, hs, flags, extra = method_body(pe, rva)
        except SystemExit:
            continue
        if hs == 1:
            tiny += 1
        if decode_length(code) == len(code):
            ok_walk += 1
        else:
            bad_walk += 1
    out.append(("method bodies walk exactly", True,
                "%d fat bodies decode to the byte, %d do not (reported as "
                "ilDecodesCleanly=false where they matter), %d tiny bodies"
                % (ok_walk, bad_walk, tiny)))
    return out


def resolve_target(pe: PE, dump: dict, target: dict) -> dict:
    """
    Find the one MethodDef row that matches (declaringType, name, arity).

    The identity of the declaring type and the method name come from the metadata reader,
    which is the only side that knows them (the IL dump's obfuscated names are mangled in
    a different way). The IL dump supplies the independent cross-check: the RVA and code
    size this reader computes for the chosen row must be the ones the dump recorded for a
    method with the same parameter count.
    """
    want_type = target["declaringType"]
    want_name = target["name"]
    want_arity = target["arity"]

    # The metadata's view: walk the MethodDef rows of that TypeDef
    tdef = type_def_index(pe, want_type)
    if tdef == 0:
        fail("declaring type %s is not in %s's metadata" % (want_type, pe.path))
    first, end = type_method_range(pe, tdef)
    n_methods = pe.row_counts["MethodDef"]
    candidates = []
    for mi in range(first, end + 1):
        if mi < 1 or mi > n_methods:
            continue
        row = pe.row("MethodDef", mi)
        if pe.string(row[3]) != want_name:
            continue
        if method_arity(pe, mi) == want_arity:
            candidates.append((mi, row, want_arity))

    if len(candidates) != 1:
        fail("%s::%s/%d is not unique in the metadata (%d candidates)"
             % (want_type, ascii_name(want_name), want_arity, len(candidates)))
    mi, row, npar = candidates[0]
    rva = row[0]
    code, code_off, header_size, flags, extra = method_body(pe, rva)
    sig_blob = pe.blob(row[4])
    decoded = decode_length(code)

    # Cross-check against the IL dump. The dump is the independent witness: it was produced
    # by a different tool from a different input, so agreement on RVA, code size and
    # parameter count is real evidence. A target the dump does not contain is reported as
    # unchecked rather than quietly passing -- the caller turns that into a problem.
    dump_view = dump["blocks"].get(rva)
    cross = {"checked": False, "problems": []}
    if dump_view is None:
        cross["problems"].append("RVA 0x%X is not in the IL dump" % rva)
    else:
        cross["checked"] = True
        cross["ilDumpLine"] = dump_view["declaredAt"]
        if dump_view["codeSize"] is not None and dump_view["codeSize"] != len(code):
            cross["problems"].append(
                "code size: dump %d, metadata %d" % (dump_view["codeSize"], len(code)))
        if dump_view["arity"] != npar:
            cross["problems"].append(
                "parameter count: dump %d, metadata %d" % (dump_view["arity"], npar))

    rec = {
        "methodDefRow": mi,
        "metadataToken": "0x06%06X" % mi,
        "declaringType": want_type,
        "name": want_name,
        "arity": npar,
        "flags": ("static " if row[2] & MD_STATIC else "") +
                 ("virtual " if row[2] & MD_VIRTUAL else "") +
                 ("abstract" if row[2] & MD_ABSTRACT else ""),
        "implFlags": "0x%04X" % row[1],
        "rva": "0x%X" % rva,
        "methodHeaderSize": header_size,
        "extraSections": extra,
        "ilSize": len(code),
        "ilHash": il_hash(code),
        "ilDecodesCleanly": decoded == len(code),
        "ilDumpLine": dump_view["declaredAt"] if dump_view else None,
        "crossCheckedAgainstIlDump": cross["checked"],
        "crossCheckProblems": cross["problems"],
        "methodSigBlob": sig_blob.hex().upper(),
        "role": target["role"],
        "citation": target["citation"],
        "why": target["why"],
        "ilHashVersion": IL_HASH_VERSION,
    }
    return rec


# ======================================================================================
# The record
# ======================================================================================

def load_record(path: str) -> dict:
    if not os.path.isfile(path):
        fail("known-build.json not found at %s" % path)
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


def write_record(path: str, rec: dict) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(rec, fh, indent=2, ensure_ascii=False)
        fh.write("\n")


def build_table(assembly: str, dump_path: str, record: dict, verbose: bool = True) -> dict:
    pe = PE(assembly)
    dump = scan_il_dump(dump_path)
    sha = hashlib.sha256(pe.data).hexdigest().upper()
    size = len(pe.data)
    mvid = pe.mvid()
    asm_name, asm_ver = pe.assembly_name_and_version()

    if verbose:
        print("assembly   : %s" % assembly)
        print("             %d bytes  sha256 %s" % (size, sha))
        print("             MVID %s  (%s %s)" % (mvid, asm_name, asm_ver))
        print("             ILONLY=%s ManagedNativeHeader=0x%X"
              % (bool(pe.flags & 1), pe.managed_native_header))
        print("             %d methods, %d types"
              % (pe.row_counts["MethodDef"], pe.row_counts["TypeDef"]))

    # Does the recorded build still describe this file?
    problems = []
    known = record.get("assembly", {})
    if known.get("sha256") and sha != known["sha256"].upper():
        if sha == (known.get("sha256OpenGlVariant") or "").upper():
            if verbose:
                print("note       : this is the OpenGL apphost variant, not the analysed "
                      "build -- the table below describes THIS file")
        else:
            problems.append("assembly SHA-256 does not match known-build.json "
                            "(recorded %s, actual %s)" % (known.get("sha256"), sha))

    targets = {}
    for target in TARGETS:
        r = resolve_target(pe, dump, target)
        targets[target["key"]] = r
        if verbose:
            print("  %-10s %s::%s/%d" % (target["key"], r["declaringType"], ascii_name(r["name"]),
                                         r["arity"]))
            print("             token %s  ilSize %d  ilHash %s"
                  % (r["metadataToken"], r["ilSize"], r["ilHash"]))
            if r["crossCheckedAgainstIlDump"]:
                print("             IL dump line %d: RVA, code size and arity all agree"
                      % r["ilDumpLine"])
        if not r["crossCheckedAgainstIlDump"]:
            problems.append("%s: not cross-checked against the IL dump" % target["key"])
        for p in r["crossCheckProblems"]:
            problems.append("%s: %s" % (target["key"], p))
        if not r["ilDecodesCleanly"]:
            problems.append("%s: the instruction walk does not consume the body exactly "
                            "(the hash is unaffected -- it is over the raw bytes -- but "
                            "this reader may have mislocated the code)" % target["key"])

    # Distinctness: two targets sharing a hash means the matching is broken.
    seen = {}
    for key, r in targets.items():
        if r["ilHash"] in seen:
            problems.append("targets %s and %s have the SAME IL hash" % (seen[r["ilHash"]], key))
        seen[r["ilHash"]] = key

    table = {
        "_comment": ("Phase 0 target table, generated by tools/il_index.py. Regenerate "
                     "after every game update. The hook matches on ilHash and refuses to "
                     "install if a target's IL differs; metadataToken and ilDumpLine are "
                     "diagnostics, not identity."),
        "generated": None,
        "ilHashVersion": IL_HASH_VERSION,
        "ilHashRecipe": IL_HASH_RECIPE,
        "assembly": {
            "file": os.path.basename(assembly),
            "size": size,
            "sha256": sha,
            "mvid": mvid,
            "assemblyName": asm_name,
            "assemblyVersion": asm_ver,
            "ilOnly": bool(pe.flags & 1),
            "managedNativeHeaderRva": pe.managed_native_header,
        },
        "ilDump": {
            "file": os.path.relpath(dump_path, ROOT).replace("\\", "/"),
            "methodBlocksIndexed": len(dump["blocks"]),
            "methodDeclarations": dump["declared"],
        },
        "targets": targets,
        "problems": problems,
    }
    return table


# ======================================================================================
# Modes
# ======================================================================================

def apply_to_record(record: dict, table: dict) -> dict:
    """Fold the generated table into known-build.json, keeping the hand-written parts."""
    asm = table["assembly"]
    if asm["sha256"] == (record.get("assembly", {}).get("sha256") or "").upper():
        record["assembly"].setdefault("mvid", asm["mvid"])
    record["targetMethods"] = {
        "_comment": ("The pinned detour targets, by IL hash "
                     "(generated by tools/il_index.py; do not hand-edit). ilHashVersion %d."
                     % table["ilHashVersion"]),
        "_recipe": table["ilHashRecipe"],
        "_generatedFrom": {
            "assemblySha256": asm["sha256"],
            "mvid": asm["mvid"],
            "ilDump": table["ilDump"]["file"],
        },
    }
    # THE KEYS COME FROM THE TABLE, NOT FROM A LIST HERE.  This loop used to name the
    # four Phase 0 targets literally, and nothing connected that tuple to `TARGETS`
    # above: adding Phase 3's movement target resolved it, printed it, cross-checked
    # it against the IL dump, reported "TARGET TABLE OK -- 5 targets" and then wrote
    # FOUR of them into the record.  The generator that builds the hook's compiled
    # table reads the record, so the hook would have shipped with the movement target
    # absent and nothing anywhere saying so.  A hardcoded list beside a data-driven
    # one is a silent truncation, and this is the second copy of that mistake in this
    # file's history -- `PublishTargets` writing into the wrong offsets was the first.
    for key in table["targets"].keys():
        r = table["targets"][key]
        record["targetMethods"][key] = {
            "declaringType": r["declaringType"],
            "name": r["name"],
            "arity": r["arity"],
            "metadataToken": r["metadataToken"],
            "rva": r["rva"],
            "ilSize": r["ilSize"],
            "ilHash": r["ilHash"],
            "ilHashVersion": r["ilHashVersion"],
            "ilDumpLine": r["ilDumpLine"],
            "role": r["role"],
            "citation": r["citation"],
        }
    return record


def mutation_test(assembly: str, dump_path: str, record: dict) -> int:
    """
    The Phase 0 evidence: prove that a one-byte change to a target's IL makes the table
    regeneration disagree -- i.e. the gate works at the METHOD level, not only the file
    level. Returns a process exit code.
    """
    print("mutation test: baseline")
    base = build_table(assembly, dump_path, record, verbose=False)
    if base["problems"]:
        print("  baseline has problems, refusing to mutation-test:")
        for p in base["problems"]:
            print("    - " + p)
        return 1

    tmp = tempfile.mkdtemp(prefix="il_index_mut_")
    try:
        mutated_path = os.path.join(tmp, os.path.basename(assembly))
        shutil.copyfile(assembly, mutated_path)
        pe = PE(mutated_path)
        target_key = "collision"
        r = base["targets"][target_key]
        rva = int(r["rva"], 16)
        body_off = pe.off(rva)
        # Flip a byte in the middle of the instruction stream, not the header.
        flip_at = body_off + r["methodHeaderSize"] + max(0, r["ilSize"] // 2)
        with open(mutated_path, "r+b") as fh:
            fh.seek(flip_at)
            original = fh.read(1)
            fh.seek(flip_at)
            fh.write(bytes([original[0] ^ 0x01]))
        print("  flipped byte 0x%02X -> 0x%02X at file offset 0x%X (%s::%s IL)"
              % (original[0], original[0] ^ 0x01, flip_at, r["declaringType"], ascii_name(r["name"])))

        after = build_table(mutated_path, dump_path, record, verbose=False)
        changed = []
        for key, t in after["targets"].items():
            if t["ilHash"] != base["targets"][key]["ilHash"]:
                changed.append(key)
        size_same = after["assembly"]["size"] == base["assembly"]["size"]

        print("  file size unchanged : %s" % size_same)
        print("  targets whose IL hash changed: %s" % (", ".join(changed) or "(none)"))
        ok = changed == [target_key]
        if ok:
            print("MUTATION TEST PASSED -- a one-byte IL edit in %s is detectable, and "
                  "only that target moved." % target_key)
            return 0
        print("MUTATION TEST FAILED -- expected exactly [%s] to change" % target_key)
        return 1
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def run_selftest(assembly: str) -> int:
    """
    Independent checks of this reader, against facts the project established by other means.

    Nothing here reads `known-build.json` or the IL dump: the point is that a reader bug
    cannot make both sides of a cross-check wrong in the same direction. The signatures are
    the ones the documents state, and the table walk is checked against its own invariants.
    """
    pe = PE(assembly)
    print("il_index self-test on %s" % assembly)
    print("  %d methods, %d types, %d params"
          % (pe.row_counts["MethodDef"], pe.row_counts["TypeDef"], pe.row_counts["Param"]))
    print()
    results = selftest(pe)
    failed = 0
    for name, ok, detail in results:
        print("  %-42s %-4s %s" % (name, "PASS" if ok else "FAIL", detail))
        if not ok:
            failed += 1
    print()
    if failed:
        print("SELF-TEST FAILED -- %d of %d checks" % (failed, len(results)))
        return 1
    print("SELF-TEST OK -- %d checks" % len(results))
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(
        description="Phase 0: build the hook's target table from the shipped IL.")
    ap.add_argument("--assembly", default=None,
                    help="the game assembly (default: known-build.json's recordedFrom)")
    ap.add_argument("--il-dump", default=DEFAULT_IL_DUMP,
                    help="the ilspycmd -il dump used to cross-check the targets")
    ap.add_argument("--record", default=DEFAULT_RECORD, help="known-build.json path")
    ap.add_argument("--json", default=None, help="also write the table here")
    ap.add_argument("--check", action="store_true",
                    help="verify only; do not write known-build.json")
    ap.add_argument("--mutation-test", action="store_true",
                    help="prove the gate detects a one-byte IL edit in a target")
    ap.add_argument("--self-test", action="store_true",
                    help="check this reader against the signatures the documents state")
    args = ap.parse_args(argv)

    record = load_record(args.record)
    assembly = args.assembly
    if not assembly:
        recorded_from = record.get("recordedFrom")
        if not recorded_from:
            fail("known-build.json has no recordedFrom; pass --assembly")
        assembly = os.path.join(recorded_from, record["assembly"]["file"])
    if not os.path.isfile(assembly):
        fail("assembly not found: %s (pass --assembly)" % assembly)

    if args.self_test:
        return run_selftest(assembly)

    if args.mutation_test:
        return mutation_test(assembly, args.il_dump, record)

    table = build_table(assembly, args.il_dump, record)

    # THE RECORD IS CHECKED AGAINST THE TABLE, NOT ONLY THE TABLE AGAINST THE ASSEMBLY.
    #
    # THIS IS THE CHECK THAT WAS MISSING, AND ITS ABSENCE WAS SILENT. `known-build.json`
    # is what the build script turns into the hook's compiled target table, so a target
    # that resolves perfectly and never reaches the record is a target the hook does not
    # have -- while every line this tool prints says the opposite. That happened: Phase
    # 3's movement target resolved, cross-checked and was counted in "5 targets", and the
    # hardcoded key list in `apply_to_record` wrote four. Nobody would have seen it until
    # a run reported the target MISSING against a build that is not missing it.
    record_problems = []
    stored = record.get("targetMethods", {})
    for key, r in table["targets"].items():
        s = stored.get(key)
        if s is None:
            record_problems.append("not in %s: %s" % (os.path.basename(args.record), key))
            continue
        for field, value in (("metadataToken", r["metadataToken"]),
                             ("ilHash", r["ilHash"]),
                             ("ilSize", r["ilSize"])):
            if s.get(field) != value:
                record_problems.append("%s.%s: record %s, table %s"
                                       % (key, field, s.get(field), value))
    for key in stored:
        if not key.startswith("_") and key not in table["targets"]:
            record_problems.append("in %s but not in the assembly: %s"
                                   % (os.path.basename(args.record), key))

    print("")
    if table["problems"]:
        print("TARGET TABLE NOT TRUSTWORTHY:")
        for p in table["problems"]:
            print("  - " + p)
    else:
        n = len(table["targets"])
        print("TARGET TABLE OK -- %d targets, distinct IL hashes, RVA and code size "
              "cross-checked against the IL dump" % n)

    if args.check:
        if record_problems:
            print("%s DOES NOT MATCH THE TABLE:" % os.path.basename(args.record))
            for p in record_problems:
                print("  - " + p)
        else:
            print("%s matches the table" % os.path.basename(args.record))

    if args.json:
        with open(args.json, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(table, fh, indent=2, ensure_ascii=False)
            fh.write("\n")
        print("table written to %s" % args.json)

    if not args.check and not table["problems"]:
        import datetime
        table["generated"] = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        record = apply_to_record(record, table)
        write_record(args.record, record)
        print("known-build.json updated: targetMethods + assembly.mvid")

    return 1 if (table["problems"] or (args.check and record_problems)) else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except SystemExit:
        raise
    except Exception as exc:  # never a traceback where a one-line refusal will do
        print("il_index: unexpected failure: %s: %s" % (type(exc).__name__, exc),
              file=sys.stderr)
        sys.exit(2)
