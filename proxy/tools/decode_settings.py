"""Decode Darza's Dominion settings.dat.

The file is a flat key/value blob obfuscated with a +1 byte shift: every
printable byte is stored one higher than its real value, so 'https' is stored as
'httpu'. Bytes outside the printable range are left alone, which is why separators
show up as '.' and '!'.

Usage:  python tools/decode_settings.py [path] [--write path/to/settings.dat]
"""
import argparse
import re
import sys
from pathlib import Path

DEFAULT = Path(r"C:\Users\isvac\AppData\Local\RippleStudio\Darza\settings.dat")


def decode(data: bytes) -> str:
    out = []
    for b in data:
        if 32 <= b <= 126:
            out.append(chr(b - 1))
        elif b in (9, 10, 13):
            out.append(chr(b))
        else:
            out.append(".")
    return "".join(out)


def encode(text: str) -> bytes:
    out = bytearray()
    for ch in text:
        code = ord(ch)
        if 32 <= code <= 126:
            out.append((code + 1) & 0xFF)
        else:
            out.append(code)
    return bytes(out)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("path", nargs="?", default=str(DEFAULT))
    ap.add_argument("--write", help="re-encode the decoded text back to a settings.dat")
    ap.add_argument("--replace", nargs=2, action="append", metavar=("OLD", "NEW"),
                    help="string replacement applied before writing (repeatable)")
    args = ap.parse_args()

    raw = Path(args.path).read_bytes()
    text = decode(raw)

    out = Path("work/settings-decoded.txt")
    out.parent.mkdir(exist_ok=True)
    out.write_text(text, encoding="utf-8")
    print(f"decoded {len(raw)} bytes -> {out.resolve()}")

    print("\nkey/value pairs discovered:")
    for m in re.finditer(r"([a-z_]{4,})\.+([ -~]{0,60})", text):
        key, value = m.group(1), m.group(2).rstrip(". ")
        print(f"   {key:<26} = {value}")

    if args.write:
        for old, new in (args.replace or []):
            if old not in text:
                print(f"WARNING: '{old}' not found; nothing replaced")
            text = text.replace(old, new)
        Path(args.write).write_bytes(encode(text))
        print(f"\nwrote re-encoded settings -> {args.write}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
