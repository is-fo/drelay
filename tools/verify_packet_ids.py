"""Compare the Java packet-id tables against the decompiled client enums, name by name.

Independent check of a hand-transcribed 321-entry table. Two facts make the comparison sound:
the client enums declare no explicit values (so the id is the ordinal) and their entry counts
match the Java tables. That leaves only transcription errors, which is exactly what this catches.

Usage: python tools/verify_packet_ids.py
       DRELAY_DECOMP=<dir> overrides where the decompiled client enums are read from.

Exit codes: 0 match, 1 mismatch, 2 skipped (no decompiled client available).
"""
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DECOMP = Path(os.environ.get("DRELAY_DECOMP", ROOT / "work" / "decomp"))

PAIRS = [
    (
        DECOMP / "DarzaGameNet.Packets" / "GmPacketType.cs",
        ROOT / "src" / "main" / "java" / "networking" / "packets" / "GmPacketType.java",
    ),
    (
        DECOMP / "DarzaGameNet.Packets" / "QPacketType.cs",
        ROOT / "src" / "main" / "java" / "networking" / "packets" / "QPacketType.java",
    ),
]

ENTRY = re.compile(r"^\s*([A-Za-z_]\w*)\s*,?\s*$")


def csharp_entries(path: Path) -> list[str]:
    entries = []
    inside = False
    for line in path.read_text(encoding="utf-8").splitlines():
        if "enum" in line:
            inside = True
            continue
        if not inside:
            continue
        if line.strip().startswith("}"):
            break
        match = ENTRY.match(line)
        if match:
            entries.append(match.group(1))
    return entries


def java_array_entries(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8")
    # only the NAMES array, not the constants declared later in the file
    start = text.index("NAMES = {")
    end = text.index("};", start)
    block = text[start:end]
    return re.findall(r'"([A-Za-z0-9_]+)"', block)


def main() -> int:
    missing = [cs for cs, _ in PAIRS if not cs.exists()]
    if missing:
        # The client enums come from a decompilation of the installed game, which this repository
        # does not ship: its binaries are third-party code. The check therefore reports that it was
        # skipped rather than failing, and says how to point it at your own decompilation.
        print("SKIP: no decompiled client to compare against.")
        for path in missing:
            print(f"   missing: {path}")
        print("   decompile the client with ILSpy and set DRELAY_DECOMP to the output directory")
        print(r"   example:  $env:DRELAY_DECOMP = 'C:\src\decomp'; python tools\verify_packet_ids.py")
        return 2

    differences = 0
    for cs_path, java_path in PAIRS:
        cs = csharp_entries(cs_path)
        java = java_array_entries(java_path)
        print(f"== {cs_path.name}: client={len(cs)} java={len(java)}")

        if len(cs) != len(java):
            print(f"   LENGTH MISMATCH: client {len(cs)} vs java {len(java)}")
            differences += 1

        for index, (a, b) in enumerate(zip(cs, java)):
            if a != b:
                print(f"   id {index}: client={a!r} java={b!r}")
                differences += 1

        if differences == 0:
            print("   all names match at every index")
        print()

    if differences:
        print(f"FAIL: {differences} difference(s)")
        return 1
    print("PASS: packet-id tables match the client enums exactly")
    return 0


if __name__ == "__main__":
    sys.exit(main())
