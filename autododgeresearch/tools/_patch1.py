# -*- coding: utf-8 -*-
import io, sys
p = r"G:\coding\javamapp\drelay\autododgeresearch\CLIENT-MAP.md"
s = io.open(p, encoding="utf-8").read()
orig = len(s)

# ---------------------------------------------------------------- FIX 1
old = """**Tooling.** `autododgeresearch/tools/g.py` is a bounded grep-with-context helper written for this
document (`python g.py <file> <regex> [--ctx N] [--max M] [--range A B] [--sig]`). It exists because
`World.cs` is 24 381 lines / 827 KB and cannot be read whole; every `World.cs` citation below is a
targeted region located with it."""
new = """**Tooling.** Two helper scripts in `autododgeresearch/tools/` were written for this document:

* `g.py` - bounded grep-with-context: `python g.py <file> <regex> [--ctx N] [--max M] [--range A B] [--sig]`.
  It exists because `World.cs` is 24 381 lines / 827 KB and cannot be read whole; every `World.cs`
  citation below is a targeted region located with it.
* `il_constants.py` - resolves obfuscated literal *values* from the real IL dump at
  `autododgeresearch/il/client-5.8.2.il` (89 MB, produced by another agent in this workspace).
  The decompiled C# folds every literal into `hehe(int32 index)` and shows only the index; the IL shows
  the instruction sequence around the call, which is where the value is usable. This is the single most
  effective way to *read* a constant without decrypting the resource (see Sec 9.4.1).

**Cross-source note.** This workspace also contains live-measurement artifacts produced by sibling
agents in `autododgeresearch/tools/` (`_numbers.txt`, `_projhit_raw.txt`, `_rtt_raw.txt`,
`_cadence_raw.txt`, `_deaths_raw.txt`, and the scripts that generate them). Where those answer an open
question from Sec 10 they are cited as **[live]** and the corresponding gap is closed, not merely
listed."""
assert old in s
s = s.replace(old, new)
io.open(p, "w", encoding="utf-8").write(s)
print("fix1 ok", orig, "->", len(s))