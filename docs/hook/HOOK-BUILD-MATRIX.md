# Hook build matrix — the verified build, and how to re-verify it

## What this file is

This is the human-readable half of [`hook/known-build.json`](../../hook/known-build.json). The JSON is the
authority: [`hook/tools/Fingerprint.ps1`](../../hook/tools/Fingerprint.ps1) reads it, compares the installed game
against every value it records, and exits non-zero on a mismatch, so nothing here overrides it. What
this document adds is the part a machine cannot carry — what each pinned value means, what breaks
when it changes, which of the numbers the gate actually compares and which are only recorded, which
of the counter-intuitive findings in this build were *measured* and must not be "cleaned up", and the
exact command sequence that re-establishes all of it after a game patch. It is Phase 0 deliverable 4
of [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md), and the reason that phase exists is the
reason it deserves care: a hook with a wrong target fingerprint does not print a wrong log line, it
patches the wrong function in a live process.

## The verified build

Recorded **2026-10-07** from `E:\SteamLibrary\steamapps\common\Darza's Dominion`. Every value below is
quoted from `hook/known-build.json`; the last column names the check `Fingerprint.ps1` runs, or says that
the value is recorded rather than compared.

| pinned value | recorded as | what it is | if it changes |
|---|---|---|---|
| `game.version` | `5.8.2` | the client release every citation, RVA and field offset in this directory is relative to | record only — nothing compares it. A change does not fail the gate; it means every document here has to be re-read |
| `game.steamAppId` | `1895130` | the Steam product the recorded install came from | record only — a change means this is not the product the research was done against |
| `assembly.file` | `DarzasDominion.dll` | the assembly the four targets live in; the gate joins it to the game path | every path in the tooling stops resolving |
| `assembly.size` | `50216960` | the root apphost's assembly, in bytes. Two apphosts ship with **different** builds | fails `assembly size`; the size is also how the gate recognises the other apphost's file |
| `assembly.sha256` | `B34181534DAE0114970446985E40755F9559F15711DF9461167BABA1FD88CEF8` | content hash of that file | fails `assembly SHA-256`. This is the check that catches an edit which preserves length: the first version of the gate compared size only, and a one-byte flip passed every check |
| `assembly.sizeOpenGlVariant` | `50831360` | the `OpenGL\DarzasDominion.exe` apphost's own build of the same assembly | the gate reports `which apphost: OpenGL variant -- NOT the analysed build` and compares against the OpenGL pair instead |
| `assembly.sha256OpenGlVariant` | `8158393955A9D07E67B0BA04EDC326201C31025DBB327F02DA54B913ACCA984C` | content hash of the OpenGL build | as above. Only the root apphost's build has been analysed |
| `assembly.managedNativeHeaderRva` | `0` | COR20 `ManagedNativeHeader` RVA; 0 means the image has no ReadyToRun/native header | fails `assembly not ReadyToRun`. If the assembly ever ships ReadyToRun, a `compileMethod` hook stops seeing its methods and the hook design needs rework (HOOK-ARCHITECTURE §1 fact 1, §10) |
| `assembly.mustBeIlOnly` | `true` | the COR20 flags requirement, read as `ILONLY` set | fails `assembly ILONLY set`; without it the loader may run code the JIT hook never sees |
| `assembly.comImageFlags` | `0x00000001` | the recorded flag word itself (`COMIMAGE_FLAGS_ILONLY`) | record of the same fact as the row above; a different value has to be explained before anything is patched |
| `assembly.mvid` | `CBF722C0-56DB-027D-6115-AA7A24CE5307` | the module's identity, read by `il_index.py` from the `Module` table | **not** compared by the gate — it is one of the known gaps and must be re-checked in-process at attach (HOOK-ARCHITECTURE §9 check 5). A change means a different module image is loaded |
| `runtime.tfm` | `net9.0` | target framework in `DarzasDominion.runtimeconfig.json` | fails `target framework` |
| `runtime.version` | `9.0.20` | the .NET 9 servicing line | record only — the gate compares `coreclrBuild`, not this field |
| `runtime.coreclrBuild` | `9.0.2026.41315` | file version of `coreclr.dll` and `clrjit.dll` (the gate normalises `9,0,2026,41315 @Commit: …` to four numeric parts) | fails `coreclr.dll version` / `clrjit.dll version`; a runtime update can move the JIT interface, so §4.1's validation sequence has to be re-run before anything is installed |
| `runtime.tieredCompilation` | `false` | `System.Runtime.TieredCompilation` in the app's own `runtimeconfig.json` | fails `TieredCompilation off`. This is the single assumption the hook rests on: with tiering off each method is compiled once and a detour is permanent. If a patch flips it, either force `DOTNET_TieredCompilation=0` in the launcher environment or the approach is invalid (HOOK-ARCHITECTURE §1 fact 2, §9 check 4) |
| `jit.vtableCodeSlotCount` | `4` | how many leading slots of `g_CILJit`'s vtable point into the image | fails `JIT vtable code slots`; the count is how the gate proves it has the right object before writing anything |
| `jit.compileMethodSlot` | `0` | the slot the detour patches | reported as information. It has been slot 0 on every .NET version |
| `jit.getVersionIdentifierSlot` | `2` | the slot the hook reads a JIT/EE GUID from, to validate the object | **not** compared by the gate — the hook does it at install time. Slot 2 is right on .NET 5+; the widely copied implementations hardcode slot 4, which was correct only up to .NET Core 3.1 (HOOK-ARCHITECTURE §4.1, §9 check 3) |

The gate is read-only: it opens the game's files for reading and writes nothing except its report. It
exits **0** when every check passes, **1** on any mismatch (naming the component and the
expected/actual pair), and **2** when it cannot run at all. Two of the values above are deliberately
not script-checked — the MVID and the GUID slot — because both are properties of the *running*
process rather than of a file on disk.

## The six targets

Quoted from the `targetMethods` block. `ilHashVersion` is `1` for all six.

| role | declaring type | method | arity | metadata token | RVA | IL size | IL hash | citation |
|---|---|---|---|---|---|---|---|---|
| frame tick | `DarzasDominion.Graphics.DarzaWindow` | `Update` | 1 | `0x06001FC5` | `0xD54A8` | 1286 | `CF8779CF0191990E9D6365940CC6A59EEBC990BB20E6428648BF99E69241629D` | `Graphics/DarzaWindow.cs:1727` |
| spawn | `DarzasDominion.Darza.Game.Objects.Projectile` | `ProcessProjectileData` | 16 | `0x06005214` | `0x3E4D9C` | 2014 | `8A7928BDA06EDE4F76FFE66E4C80978864E37654603D1CE3ADC83D79719DA55A` | `Objects/Projectile.cs:810` |
| collision | `DarzasDominion.Darza.Game.Objects.Projectile` | `HitCharacter` | 0 | `0x0600521F` | `0x3E64FC` | 989 | `D6F353B8CF67A24344899064EBC13D292095508E6C1C13F36A65081F0ED108B2` | `Objects/Projectile.cs:1514` |
| escape | `DarzasDominion.Darza.Game.World` | `RequestEscape` | 0 | `0x06002964` | `0x162E4C` | 335 | `6E3B9F4CD77B5E8AE6B060B85233B20B00E88F054DFFC4AB380C04458C030A2D` | `Game/World.cs:12984` |
| movement integrator | `DarzasDominion.Darza.Game.World` | `hehe\ueb2c\ue5a5\uee82\ue9c5\uef68` | 2 | `0x06002961` | `0x1621B8` | 2574 | `1E41504101BED2A28539A69A46C2384E4FB24956F04EBDD199A1ED4C72737028` | `Game/World.cs:12666` |
| aim target selection | `DarzasDominion.Darza.Game.AimbotSystem` | `FindTarget` | 2 | `0x06002837` | `0x14C9EC` | 1580 | `B8E646CD86A27DB5DA2B830B40AA85C0E5BD1BF4AB5B936CB2360BE8869C9124` | `Game/AimbotSystem.cs:179` |

The line in the IL dump that each target was found at is recorded as `ilDumpLine`: 500932 (frame
tick), 1978316 (spawn), 1981122 (collision), 780182 (escape), 778772 (movement integrator), 726472
(aim target selection).

### The movement target's name, and why it is written that way

**The name in that row is not the name the decompiler prints, and the two cannot be searched for each
other.** ilspycmd renders this method as `World.hehe_EB2C_E5A5_EE82_E9C5_EF68(float, float)`. The
metadata string heap holds a *different* string of nine private-use code points (`U+EB2C`, `U+E5A5`,
`U+EE82`, `U+E9C5`, `U+EF68` and the `hehe` prefix). A whole-file search of the 89 MB IL dump for
`EB2C_E5A5_EE82_E9C5_EF68` returns **zero hits** — so the natural move reports "this method is not in
this build" and is wrong.

Three consequences, all of them in the tooling:

- [`hook/tools/il_targets.py`](../../hook/tools/il_targets.py) lists candidates on a type **without** relying on
  the name: declaring type, parameter count, return and parameter types from the signature blob, IL
  size, and the IL dump's RVA line. It never writes the record — `il_index.py` stays the one
  authority — so target selection gained a tool without gaining a second source of truth.
- every build log prints obfuscated names escaped (`il_index.ascii_name`), because printing the raw
  string killed the tool with `UnicodeEncodeError: 'charmap' codec can't encode characters in
  position 58-62`. The console code page has no glyphs for private-use code points; nothing was wrong
  with the resolution.
- `il_index.py --check` now compares **`hook/known-build.json` against the generated table** and exits
  non-zero on a mismatch. `apply_to_record` used to name the four Phase 0 keys literally, so adding
  this target printed "5 targets" and wrote four — and the build script reads that record to build
  the hook's compiled table. A silent truncation with every line of output correct.

The row's `arity` is `2` and the signature is confirmed as `void (float, float)` from the metadata
itself, which is what distinguishes it from the three other arity-2 methods on `World` that also take
two `float`s; the IL dump's own body confirms the rest (code size 2574 = `0xa0e`, and locals
`Character`, `bool`, `float32`, `Angle`, `Vector3` matching `World.cs:12666`).

The five hashes are **distinct**, and `il_index.py` refuses a table in which two targets share one —
identical hashes would mean the matching is broken.

**The IL hash is the identity. The metadata token, the RVA and the dump line are diagnostics.** Three
reasons, all of them recorded in HOOK-ARCHITECTURE §4.3 and in `il_index.py`:

- the assembly is obfuscated, so **names are not an identity**. `declaringType` + `name` + `arity` is
  a *locator* — `il_index.py` uses it to find the one `MethodDef` row and fails if it is not unique —
  not a guarantee;
- **tokens move when metadata changes.** Adding or removing a method renumbers `MethodDef` rows, so a
  token can match while pointing at something else entirely;
- the **hash is over the exact bytes `CORINFO_METHOD_INFO` hands the JIT** (`ILCode[0 ..
  ILCodeSize)`), which is what the hook actually has in hand when `compileMethod` is called. Matching
  on it is name-independent, token-independent, and build-specific in the way that matters: if a
  method's IL changes, the hash stops matching and the hook refuses to install instead of patching
  the wrong function.

Two further properties of the table are load-bearing. The five hashes are **distinct**, and
`il_index.py` refuses a table in which two targets share one — identical hashes would mean the
matching is broken. And the record's `role` and `citation` fields, together with the `signatureHint`
and one-line `why` carried in `il_index.py`'s `TARGETS` list, are the human anchors of *intent*: the
frame tick is "once per frame, after `Client.DrainColdQueue`, before `UpdateScene`"; spawn is "the
complete threat event, delivered as arguments (no field reads)"; collision is "the local collision
decision — the instant the server is about to be told"; escape is "the client's own nexus entry
point — runs the client's own gates"; and the movement integrator is "the input→position step: it
reads `GameScene.GameInput` and commits `Player.Position`, so a replacement on it can both steer and
measure". That is what a reviewer checks after a patch (see below), not the hashes.

## The hash recipe

Quoted from `targetMethods._recipe` in `hook/known-build.json`; the identical string is `IL_HASH_RECIPE`
in [`hook/tools/il_index.py`](../../hook/tools/il_index.py):

> sha256 of MethodDef.Rva's exact IL instruction bytes: ILCode[0 .. ILCodeSize), little-endian as
> stored. No header, no .locals, no exception-handler sections, and no re-encoding of any kind -- the
> hash is over the bytes as they sit in the image, which are the same bytes CORINFO_METHOD_INFO hands
> the JIT.

Hashing the **raw** bytes rather than a re-encoded or normalised instruction stream is what lets the
two sides agree without either of them owning an IL decoder: the native side hashes the loader's copy
of exactly these bytes, and `il_index.py` hashes the same bytes out of the image, so neither can
disagree with the other about operand widths. That is the whole point of the choice. If the identity
depended on `il_index.py`'s interpretation of the opcodes, a bug in one operand-width table entry
would silently change the identity of a target: the table would regenerate with a different hash,
every file-level check would still pass, and nothing would look wrong. (An earlier draft did
normalise short opcodes to their long forms to survive a re-encoding. It was dropped: the CLI does not
re-encode IL the loader validated, and a patch that genuinely rewrote a method's encoding is exactly
the event this gate exists to catch.)

`ilHashVersion` must match on both sides. `il_index.py` carries `IL_HASH_VERSION` and says of it that
"the native side carries the same constant and refuses to compare a hash across versions" — the
native side must *refuse*, not merely prefer, because a v1 hash and a v2 hash of the same method are
different numbers and comparing them can only produce a false mismatch or a false match. Changing the
recipe means bumping the constant and regenerating the table; a table regenerated under a new version
is a recipe change, not a changed build, and must not be read as one.

## Re-verifying after a game patch

Run these in order. Steps 1–5 re-verify the build that is installed; steps 6–8 are the ones that
apply when the game legitimately updated.

1. **The static gate, first.**
   ```powershell
   .\hook\tools\Fingerprint.ps1
   ```
   Exit 0 is the only acceptable result before installing anything. A failure prints
   `FINGERPRINT FAILED -- N of M checks failed` and names each component with its expected and actual
   value; exit 2 means it could not run (install missing, record unreadable). Pass
   `-GamePath "D:\Games\Darza's Dominion"` if the install moved.

2. **Check which apphost the run is looking at.** If the file's size matched
   `sizeOpenGlVariant`, the gate says so — `which apphost: OpenGL variant -- NOT the analysed build`
   — and compares against the OpenGL pair instead. Only the root apphost's build has been analysed;
   a green OpenGL run is green about the wrong file.

3. **Re-derive the target table without writing it.**
   ```powershell
   python .\hook\tools\il_index.py --check
   ```
   This re-reads the PE's CLI metadata directly (no `ilspycmd`, no `dotnet`, no network), re-hashes
   each target's IL bytes, cross-checks every target against `hook/il/client-5.8.2.il` on RVA, code size
   and parameter count, and exits 1 with `TARGET TABLE NOT TRUSTWORTHY` followed by one line per
   problem. `--check` writes nothing. The tool also has a `--self-test` mode, which checks the reader
   against signatures the documents state independently of the metadata (all four targets' arities
   among them) and reads neither `hook/known-build.json` nor the IL dump.

4. **Prove the method-level gate can still fail.**
   ```powershell
   python .\hook\tools\il_index.py --mutation-test
   ```
   It copies the assembly to a temporary directory, flips one byte in the middle of `collision`'s
   instruction stream (not its header), and requires exactly that target's hash to move while the
   file size is unchanged. `MUTATION TEST PASSED` is the evidence; anything else means the gate is
   broken, not that the build is fine.

5. **Prove the file-level gate can still fail.**
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\hook\tools\Test-FingerprintGate.ps1
   ```
   This copies the assembly, its `runtimeconfig.json` and the two runtime DLLs into a sandbox, flips
   one byte inside `collision`'s IL with the file length preserved, runs `Fingerprint.ps1 -GamePath
   <sandbox>` and requires a non-zero exit. It is the test that closed the hole the first mutation
   test found, where a same-size edit passed a size-only check.

6. **If the game legitimately updated, regenerate the record** — without `--check`:
   ```powershell
   python .\hook\tools\il_index.py
   ```
   On success this rewrites the `targetMethods` block and prints `hook/known-build.json updated:
   targetMethods + assembly.mvid` (it writes `assembly.mvid` when the record does not have one yet).
   Two cautions:
   - it **refuses to write while any problem is outstanding**, and a changed assembly SHA-256 is a
     problem, so the new file identity has to be recorded first (`assembly.size` and
     `assembly.sha256`, and the OpenGL pair if that apphost changed too). `hook/known-build.json` says of
     itself: "Regenerate after every game or update, and record what changed."
   - do not hand-edit the `targetMethods` values; the block says so itself.

7. **The reviewer's job is intent, not hashes.** Accepting the new hashes is not verification. For
   each of the four targets, confirm that the method with that name and arity in that declaring type
   is still **the same hook point**: the frame tick is still the per-frame `Update` that runs after
   `Client.DrainColdQueue` and before `UpdateScene`; spawn is still the 16-argument projectile-spawn
   payload that carries the whole threat event as arguments; collision is still the local collision
   decision that precedes the claim to the server; escape is still the client's own nexus entry
   point. `il_index.py`'s `TARGETS` list carries the `why` and `signatureHint` for exactly this
   reading. A name-and-arity match whose `why` no longer describes the method is a **changed hook
   point**, not a regenerated hash. Tokens and RVAs moving between builds is expected and harmless; a
   role moving is not.

8. **Close the loop.** Re-run the gate and both mutation tests against the regenerated record, and
   confirm `ilHashVersion` is still `1` on both sides.

## What the gate cannot check

These are recorded by `Fingerprint.ps1` itself as known gaps — the run ends with `FINGERPRINT OK --
N checks passed, M known gap(s)` — and a gap is not a pass.

| gap | why, and what answers it |
|---|---|
| **the effective runtime environment** | the gate's own line is `effective runtime environment: cannot be read from a script (check DOTNET_TieredCompilation is unset in the launcher)`. In .NET 9 an environment variable overrides `runtimeconfig.json`, so a stray `DOTNET_TieredCompilation=1` inherited from a launcher defeats the whole design while the file still says `false`. HOOK-ARCHITECTURE §1 records the hazard; §9 check 4 reads the *effective* value in-process and refuses to install if tiering is on |
| **the loaded assembly's MVID** | `the loaded assembly MVID: must be re-checked in-process at attach`. The gate hashes a file on disk; it cannot see which module the running process loaded (HOOK-ARCHITECTURE §9 check 5) |
| **which of the two shipped apphosts is running** | the gate resolves one path and, when the size matches, reports *which file* it read as information. Which apphost is actually running is a property of the process, not of a directory, which is the second reason the assembly fingerprint is re-checked at attach |
| **whether a target has been inlined into its caller** | a method the JIT never reports was either never called or inlined into its caller, and only the JIT compilation stream can tell those apart: HOOK-ARCHITECTURE §9 check 6 requires every target's IL hash to appear in the compilation stream within N seconds and logs which target was missed. No static check of the file can answer it |
| **the target-method IL hashes, when the tooling is unavailable** | if `python` or `il_index.py` is missing, the gate records a gap rather than a pass: the recorded hashes are then checked physically from the file but not re-derived from the metadata. IMPLEMENTATION-PLAN's Phase 0 lists the same set of gaps, and requires the last two to be re-checked in-process at attach (HOOK-ARCHITECTURE §9) |

## Build-specific findings that a maintainer must not "fix"

Each of these was established by measurement, each contradicts either the documentation or the naive
expectation, and each is "obviously wrong" enough that a future reader will want to correct it. All
of them live in [`hook/tools/il_index.py`](../../hook/tools/il_index.py).

### `MoreSects` second size field is not trustworthy in this build

**Finding.** A fat method header with the `MoreSects` flag is documented to carry a second size field
at +12 that covers the header *and* the extra data sections, and the documented rule is to use it.
**This build disagrees.** For `DarzaWindow::Update` the `FlagsAndSize` field declares a 12-byte
header (3 dwords) and the code really is at +12, while the +12 field says 60 — and the 48 bytes it
would skip decode as a plausible but wrong instruction stream, which is exactly the trap: a wrong
offset does not crash, it yields a wrong hash. Across the **1,941 fat+`MoreSects` methods in this
assembly, +12 decodes cleanly 1,202 times against +60's 742, and 739 decode either way**, so the
field cannot be trusted on its own.

**Evidence.** The `MoreSects` note in `method_body()`, which carries those counts; and the
independent witness in `hook/il/client-5.8.2.il`, which records `Header size: 12` and `IL_0000: call void
DarzasDominion.Graphics.DynamicFont::WarmAtlasStep()` for that method (dump line 500932ff), i.e. the
code begins 12 bytes in, not 60.

**Instruction: leave it alone.** `method_body()` resolves the offset by evidence instead of by rule:
when the declared header and the documented alternative disagree, it takes whichever candidate produces
a stream that decodes exactly to `CodeSize`, preferring the **declared** header when both do; a method
that decodes under neither is handed to the caller to be reported rather than guessed at. The
preference is not arbitrary — the declared size is the standard fat-header layout (the header ends
where `CodeSize` ends), and the +12 field is the one that disagrees with it. This is a
**hash-correctness** issue, not cosmetics: the wrong candidate hashes the same number of bytes from the
wrong start, so the digest belongs to no method at all under the right target's name, and every
size-based check still passes. Restoring the documented rule, or hardcoding `+12` because "it works
here", removes the only thing that notices when it does not.

### The metadata table layouts are not the documented ones

**Finding.** `il_index.py` walks the CLI metadata tables itself, and this assembly's row layouts are not
the ones a reader that follows the obvious rules arrives at. What actually had to be fixed, from
`SCHEMAS` and the reader's width rules:

| table | row size | what the reader walks | why the naive reading is wrong |
|---|---|---|---|
| `MethodDef` | **18** | `Rva` **4** + `ImplFlags` 2 + `Flags` 2 + `Name` 4 + `Signature` 4 + `ParamList` 2 | every column is the standard one. The original reader had `Rva` at 2 bytes, which makes the row 16 and puts every subsequent `Name`, `Signature` and `ParamList` two bytes early. The columns that then land on `Name` still resolve to *plausible* strings for most rows, so the misparse was invisible from the table itself and showed up only as a downstream symptom |
| `TypeDef` | **18** | `Flags` 4 + `TypeName` 4 + `TypeNamespace` 4 + `Extends` 2 + `FieldList` 2 + `MethodList` 2 | standard; listed because it is the other table the reader is anchored on |
| `Field` | **10** | `Flags` 2 + `Name` 4 + `Signature` 4 | standard |
| `Param` | **10** | `Flags` 2 + `Sequence` 2 + `Name` 4, **plus one further 2-byte column** | `SCHEMAS["Param"] = [None, None, STR, None]`. This is the one layout here that is a **measurement rather than a reading**: nothing in the code states it, and the documented 8-byte shape is what the reader first used |
| `Assembly`, and the tail of the stream | not byte-exact | `Assembly` does not parse under the documented columns at all, and the tables after `MethodDef` are not byte-exact under the documented layouts | this is why `Assembly.Name` is unreadable (next subsection) and why the MVID comes from `Module` |

The `Param` row was pinned down by requiring the tables *after* it to parse coherently, not by finding a
description of it. With the documented 8-byte row, `Param` ends two bytes per row early and drags every
later table out of step; with 10 bytes, the walk up to `MemberRef` accounts for every byte and then ends
45,994 bytes short of the `#~` stream's end (the appended private data described below). The stride is
what makes `MethodDef.ParamList` land on the right rows, and therefore what makes the arity check work —
so it is load-bearing, and it is corroborated by the arity agreeing with six signatures the documents
state independently.

**Evidence.** The tables the reader is *anchored* on — Module, TypeRef, TypeDef, Field, MethodDef,
Param, MemberRef — agree with the independent census of this same assembly: **23,743 `MethodDef`
rows and 25,105 `Field` rows**, as recorded in [README.md](../../hook/README.md) and
[CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §0.1 (produced by `hook/work/md_probe.py`). The reader's own
comment is that those tables "account for every byte up to `MemberRef` exactly"; a wrong column width
in one of them would put `MemberRef` somewhere else. The walk is additionally asserted against the
metadata stream: if it ends more than `0x20000` bytes from the end of the `#~` stream, the reader
fails loudly and says a column width is wrong.

The figures, as measured on this build (`il_index.py --self-test` and the walk audit it prints):

| quantity | value |
|---|---|
| `MethodDef` rows / `Field` rows / `TypeDef` rows | 23,743 / 25,105 / 3,584 |
| fat method bodies whose walk consumes the body exactly | 10,738 |
| fat method bodies whose walk does **not** (reported per-target as `ilDecodesCleanly=false`) | 371 |
| tiny method bodies (no walk needed; the code is one length byte) | 12,161 |
| residual bytes between the end of the known tables and the end of `#~` | 45,994 |

The 371 are the honest bound on this reader's instruction walk. They are not a target-side problem: none
of the four targets is among them, and the hash does not depend on the walk — but a patch that moved a
target *into* that set would be reported, which is the point of checking.

**Instruction: leave the sizes alone**, and do not extend the byte-exact walk into the tail. The
layout that matters for the four targets is anchored and independently corroborated; the tail is
explicitly treated as *not* byte-exact, which is why the reader checks its own walk rather than
trusting it. The check is a bound, not a proof: if the walk ends more than `0x20000` bytes from the end
of the `#~` stream the reader fails loudly, and this build's walk ends **45,994 bytes (44.9 KB) short**
of the end — the tables it knows about account for every byte up to `MemberRef`, and the shortfall is
what an obfuscator's private data appended after the tables looks like. It is tolerated and recorded,
rather than papered over by inventing a table to absorb it.

### `Assembly.Name` cannot be read from this build's metadata

**Finding.** Because the `Assembly` table sits in the perturbed tail, the assembly name and version
`il_index.py` prints may be blank, and the MVID does not come from the `Assembly` table at all: it is
read from the `Module` table (row 1, its `Mvid` GUID column), which is how `hook/known-build.json`'s
`assembly.mvid` was produced.

**Evidence.** `assembly_name_and_version()`'s own docstring: the assembly row "sits in the tail,
where this build's obfuscator has perturbed the stream enough that a single row cannot be located
with confidence", so it *probes* the row for a self-consistent `(version, name)` pair instead of
trusting a fixed column layout — and it is "diagnostics only: nothing in the tool's output depends on
it".

**Instruction: leave it alone, and do not chase a blank.** Nothing depends on the name or version:
the gate never reads them, the target table is keyed on the MVID and the assembly SHA-256, and a
blank field in a log line is not a symptom of anything.

### The IL dump's textual nesting cannot be reconstructed reliably

**Finding.** `il_index.py` deliberately does **not** attribute methods to declaring types from the IL
dump. ilspycmd's block layout does not let a class stack be reconstructed reliably: a `.class`
declaration can be followed by `.field`/`.custom` lines before its `{`, and `.property`/`.event`
blocks contain their own `.get`/`.addon` members. The dump is therefore used only for what it states
unambiguously about a method body — its RVA, its code size and its parameter count — while the
declaring type comes from the metadata.

**Evidence.** `scan_il_dump()`'s docstring states the hazard and the deliberate limitation, and
`resolve_target()` states the split: the metadata knows the type and the name, the dump is the
independent witness that the RVA, code size and arity agree.

**Instruction: keep the split.** Attributing a method to the wrong type would be worse than not
answering: a brace-counting reconstruction produces a confident but wrong answer with nothing to
contradict it, whereas the current design reports a target the dump does not contain as *unchecked*
and the caller turns that into a problem. A disagreement between the two sides is reported as a
failure, never smoothed over.

### The instruction walk is a check, never the identity

**Finding.** `code_entry()` exists because some methods carry a jump indirection at the start of
their stream — `br.s +3`, a zero byte, then a second `br.s +0`, with the real body at +5 — and it
matches that whole five-byte shape (`2B <delta> 00 2B 00`) exactly rather than testing for "starts
with `br.s`". The near-miss is real in this build: the frame tick's stream contains
`IL_0030: br.s IL_0035`, `IL_0032: ldc.i4.0`, `IL_0033: br.s IL_0035` — bytes `2B 03 16 2B 00` — which
is the ordinary shape a C# conditional leaves behind (one side `ldc.i4.1` + `br.s`, the other
`ldc.i4.0` + `br.s`, both meeting at `brtrue.s`), not an indirection. Separately, the opcode table
`decode_length()` walks with used to be **one entry short** — `br` (`0x38`) had no row, so everything
after it sat one index low — and the walk could not consume every body; that is fixed, and its own
section below records the measurement that found it.

**Neither of the four recorded targets needs the indirection, and none of them uses it.** Measured
against the dump, each of their streams begins at `IL_0000` with a real instruction — `call void
DarzasDominion.Graphics.DynamicFont::WarmAtlasStep()` for the frame tick (at +12, its declared header),
and `ldarg.0` for spawn, collision and escape — so `code_entry()` returns 0 for all four. An earlier
draft of this directory's own notes attributed the frame tick's RVA to the `2B 03 16` bytes; that was
wrong, and the dump contradicts it. The rule exists for methods that do carry the indirection, not for
these.

**Evidence.** `code_entry()`'s docstring (the matched shape, and `2B 03 16 2B 00` named explicitly as
something that "looks superficially similar but is ordinary code"); the dump lines 500950–500972 for
the frame tick; `decode_length()`'s docstring for the offset statistic.

**Note for the reader.** None of the four recorded targets needs the indirection. Verified against
the dump, each of their streams begins at `IL_0000` with a real instruction — `call void
DarzasDominion.Graphics.DynamicFont::WarmAtlasStep()` for the frame tick, `ldarg.0` for spawn,
collision and escape — so `code_entry()` returns 0 for all four in this build. The rule exists for
the methods that do carry the indirection; do not go looking for one in these four.

**Instruction: leave `code_entry()` alone, and fix `decode_length()` only against the
disassembler.** Do not relax `code_entry()` to a prefix test — that shifts the walk by three bytes for
ordinary code like the sequence above. And do not "fix" `decode_length()`'s operand widths *towards the
hash*: the hash is over the raw bytes and no interpretation of them, and the walk exists only to catch a
body this reader mislocated (the `MoreSects` trap above is the real instance of that). The one thing
that *is* a legitimate repair is the one that was made: a table entry that the IL dump's own instruction
offsets contradict, fixed against that witness and measured over every method the dump describes.

### The opcode table's missing `br`, and how it was found

**The table was one entry short for its whole life.** `OPCODES_1` had no row for `br` (`0x38`), so every
row after it sat one index low: `switch` at `0x44` instead of `0x45`, `isinst` at `0x74` instead of
`0x75`, and on to the end. It went unnoticed because the entries either side of the shift mostly share an
operand width — `br` and `brfalse` are both four-byte branches — so the walk usually landed correctly
anyway, and the four Phase 0 targets all consumed their bodies exactly.

**What found it was the auto-aim target.** `AimbotSystem.FindTarget` is 1580 bytes, contains `isinst`,
and the shifted table walked it off the end: `il_index.py` refused to write the record with
`aimTarget: the instruction walk does not consume the body exactly`, which is the check doing its job —
a target that could not be verified would otherwise have been pinned anyway, because the IL *hash* is
over raw bytes and never needed the walk.

**Measured against the dump before and after.** For each of the 20 418 methods the dump describes (RVA,
declared code size and instruction offsets all present), the body was read and walked, and the walk was
required to (a) visit only offsets the dump lists and (b) consume the body exactly:

| table | all visited offsets are the dump's | body consumed exactly |
|---|---|---|
| before (`br` missing) | 11 699 (57.30 %) | 19 971 (97.81 %) |
| after | **19 667 (96.32 %)** | **20 413 (99.98 %)** |

`python hook/tools/check_opcode_table.py` re-runs that measurement against the table
`il_index.py` currently ships, and exits non-zero if it falls below either threshold. It exists
because the number in the row above is a claim about a table that can be edited, and a claim that
cannot be re-measured is a claim nobody can check.

**The digest never depended on this, and still does not.** `il_hash` is SHA-256 over the exact IL bytes;
no entry in the opcode table can move a target's identity. What the table feeds is `decode_length()`
(the "is this body where we think it is" check) and the `MoreSects` offset choice inside
`method_body()` — which *can* move a chosen body, so the fix was verified to leave all five
pre-existing target hashes **unchanged** rather than assumed to.

## Where the numbers come from

| source | what it provides |
|---|---|
| [`hook/known-build.json`](../../hook/known-build.json) | the verified record: every value quoted above, and the authority the gate reads |
| [`hook/tools/il_index.py`](../../hook/tools/il_index.py) | the four targets' identity. A self-contained PE/CLI metadata reader — no `ilspycmd`, no `dotnet`, no network — that reads the real IL bytes and hashes them. Modes: default (verify and write), `--check` (verify only, write nothing), `--mutation-test` (a one-byte IL edit must move exactly that target), `--self-test` (the reader against signatures the documents state) |
| [`hook/tools/Fingerprint.ps1`](../../hook/tools/Fingerprint.ps1) | the static gate: tiered compilation, IL-only status, `ManagedNativeHeader`, the assembly's size **and SHA-256**, the `clrjit` vtable's code-slot count, both runtime DLL versions, and each target's IL bytes at its recorded RVA |
| [`hook/tools/Test-FingerprintGate.ps1`](../../hook/tools/Test-FingerprintGate.ps1) | the mutation test of the gate itself: a copy with one byte flipped inside a target's IL, same file length, must be refused |
| [`hook/tools/Test-JitVtable.ps1`](../../hook/tools/Test-JitVtable.ps1) | the instrument behind the `jit` rows: finds `getJit` in the export directory, recovers `g_CILJit`, and dumps the vtable slots. The four-slot layout and the slot names are recorded in HOOK-ARCHITECTURE §4.1 |
| [`hook/tools/Test-ReadyToRun.ps1`](../../hook/tools/Test-ReadyToRun.ps1) | reads the COR20 `ManagedNativeHeader` and reports IL-only versus ReadyToRun — the instrument behind `managedNativeHeaderRva` and `mustBeIlOnly` |
| `hook/work/md_probe.py` (from this directory, `../../work/md_probe.py`) | the .NET metadata table census: 23,743 `MethodDef` rows and 25,105 `Field` rows for this assembly, as recorded in README.md and CLIENT-FINDINGS.md §0.1 |
| `hook/il/client-5.8.2.il` | the evidence file: an `ilspycmd -il` dump of this build, 2,218,925 lines. Its first line is `// IL code: DarzasDominion`; it records no version of the tool that produced it |

**The dump is cross-checked, not trusted.** `il_index.py` uses it only to locate the targets and to
cross-check what the metadata reader found — RVA, code size and parameter count — and reports a
disagreement as a failure rather than smoothing it over. A target the dump does not contain is
reported as unchecked, and the caller turns that into a problem. The identity of a target never comes
from the dump.

Nothing in this document is a substitute for running the commands in "Re-verifying after a game
patch".
