# Phase 1 handoff

> **STATUS LIVES IN [PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md).** This document is the long
> record of how Phase 1 got where it is, and it is accurate about everything except the live read —
> which it described as missing until 2026-10-11, when two live runs produced it. Those paragraphs
> have since been corrected in place and marked; the verification document is the current status and
> the list of what is left.

**Read this first, then [PHASE1-SCOPE.md](PHASE1-SCOPE.md) §6.** The scope document is the
long-form record; this is the short version — what exists, what is proven, what is broken, and
what to do next.

Branch: `autododge-research`. Working directory: `G:\coding\javamapp\drelay`.

---

## 1. The state in one paragraph

Phase 1 is **built, and the chain works end to end against the real game.** All three artefacts
build clean; the ABI check, the PE/MVID reader check (including a deliberately tampered image) and
the 73-check end-to-end injection test all pass. Against the real game the whole chain is
*observed* to work: the bootstrap runs before `Main`, the runtime is validated
(`coreclr 9.0.2026.41315`, tiering off), the JIT/EE GUID is read, the loaded MVID is verified
against the file on disk, the collection delay and its window are honoured, the evidence files are
written, the `compileMethod` detour sees the compilation stream, **all four pinned targets have
been matched by IL hash against the live runtime**, and **the frame tick is patched and entered
~1000 times in a run with a 14 µs worst call and no crash.**

Getting there required finding two faults, and both are worth reading before touching this code
(§4). The first was a leaked per-thread re-entrancy counter in the JIT detour — it silently
discarded every compilation on a thread after the first nested one, which is why five live runs
reported `0 of 4 targets matched` while the operator demonstrably played the game. The second was
a calling-convention error in the frame-tick detour that crashed the process on the first frame
after the patch; it is the same class of defect as §4.6, which now has three entries.

**The load-bearing assumption of Phase 1 is unchanged and is now demonstrated rather than
assumed: a JIT detour can be installed in this client, it observes the compilation stream, its
target hashes agree with the pinned build record, and a patched target runs the game's own code
without harm.**

**The live read is proven too, and that is the change since this document was written.**
Spike 1.3's success test — *reading live state from inside that detour* — did not execute against the
game while this was written, because the managed frame callback used to be armed (or disabled) **at
attach**, and `World.Shared` is always null at attach: the world does not exist until a scene loads.
Arming became **a decision made later** — a managed probe the watcher thread calls at every wait that
it performs, which waits for a world, proves the measured offsets against it by comparing the pointer
chain with reflection, publishes the verdict, and only then opens the gate — and the read itself
became **native**, because entering managed code from the frame detour is an illegal reverse P/Invoke
(§13). Since then two live runs have read the real game: `20261010-015935-p27056` and
`20261011-221035-p4364` both report `arm state : ARMED`, both coordinate pairs agreeing, 223 and 232
successful frame reads with plausible values, and `PASS 9c` in `selfcheck.txt`.

**What is left is duration and stress, not capability**: the hour of play, the Exit 2a log-denial
test, the token read-back for the three targets whose token is still `0x00000000`, and the
instrument defects
[PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md) §4 lists. That document is the current status.

---

## 2. How to build, run and check

```powershell
# Build everything into hook\hook\out\
.\hook\build.ps1

# Offline ABI check: loads the DLL in a throwaway process, checks its exports and that
# DrelayHook_Init fails closed without a session. Needs no game.
.\hook\tools\Test-HookAbi.ps1

# End-to-end injection test against a synthetic .NET host staged from the game's own
# runtime. Needs the game installed (for its runtime files) but does NOT run the game.
# 73 checks, all passing (the original 43, plus 27 for the arming gate and three for reporting guards).
.\hook\tools\Test-Injection.ps1

# Offline PE/MVID reader check, including the refusal path against a tampered copy.
# Needs no game and no injection.
.\hook\tools\Test-PeReader.ps1

# Against the real game (needs an operator; the game reaches the title screen without
# a login, which is enough for CoreCLR + JIT to load). The hook waits 40 s after
# install before it starts watching the compilation stream, so that time is for
# getting past the menu and into gameplay -- see `--collect-delay` below.
.\hook\out\Drelay.Launcher.exe `
  --game "E:\SteamLibrary\steamapps\common\Darza's Dominion" `
  --runroot "G:\coding\javamapp\drelay\hook\obj\live-runs" `
  --collect-delay 40000 --verbose

# The two Phase 1 acceptance runs that need an operator, each of which launches the
# game itself and writes its evidence, its verdict and its event-log window into a
# case directory. Run them from an elevated shell if the game itself runs elevated:
# a launcher at a lower integrity level cannot inject into it.
.\hook\tools\Run-Exit2a-LogDenial.ps1          # Exit 2a: a denied log target
.\hook\tools\Run-Endurance.ps1 -Minutes 60    # Exit 1 / M3: the hour of play
```

**`--runroot` is only needed when the launcher is started from the DSH agent harness** (§11).
From the operator's own shell the default `%LOCALAPPDATA%\drelay\hook` works.

**Design frame rate: 60 Hz.** The game runs its frame loop **uncapped** — measured at roughly
200 Hz (~4.9 ms per frame) at the title screen — but Phase 1's budget and its success test are
written against 60 Hz (16.7 ms/frame), the operator caps FPS in the game's own settings, and the
high-rate limit test is deferred. A 60 Hz design point is the conservative one; the uncapped
figure is recorded in `hook.log`'s `frame tick at call 120` line and in `frames.csv`'s
`arg_total_ms`/`frame_delta_ms` columns so the limit test has a baseline when it is run.

**The collection delay, and why it exists.** `--collect-delay MS` (default 30000, `0` starts
immediately) is how long the hook waits after the detour is installed before it opens its
discovery window, so the operator can clear the menu. **Nothing is lost during the delay**: the
detour is already installed and `PublishMatch` records a match whenever it sees one, and the
per-target size counters cover the whole run. The delay decides when the watcher stops *waiting*,
not when it starts *looking*. Every post-fix run has in fact found `frameTick` *during* the
delay — which is the design working.

The knob `DRELAY_HOOK_COLLECT_DELAY_MS` must be set **by the launcher**, not exported from the
operator's shell: the launcher strips every `DRELAY_HOOK_*` variable from the child's environment
before adding its own (§4.4). `--collect-delay` is the supported way in.

**The recording window, and why it stopped being five seconds.** `--live-seconds S` (default 5) is
how long the hook records after it patches the frame tick, and the evidence files are written when
that window closes — so the window *is* the run as far as the evidence is concerned. Five seconds was
hard-coded and was the right length for proving the detour is entered; it cannot record the hour of
play Exit 1 asks for, which is why `Run-Endurance.ps1` passes 3600. **When the window closes the
frame read is paused** (`g_framePaused`, the detour's first branch, which until then was a control
nothing ever set), so a session no longer keeps dereferencing the game's heap through teardown;
`DRELAY_KEEP_READING=1` — a plain `DRELAY_` name, for the reason §4.5 gives — preserves the old
behaviour. `summary.txt` reports which state the run ended in.

Evidence for a run lands in the run directory: `bootstrap.log`, `hook.log`, `hook.log.sizes`,
`hook.log.observed`, `selfcheck.txt`, `summary.txt`, `events.jsonl`, `offsets.csv`, `frames.csv`,
`crash.txt`, and `shared.block`.

**Read the `target-size` lines and the `detour accounting` line first.** They are the two lines
that decide how every other verdict in the run should be read:

```
[INFO ] detour accounting: 10135 invocation(s), 10127 instrumented, 8 skipped as re-entrant
[INFO ] target-size frameTick    IL size 1286: compiled 1 time(s) AND MATCHED -- the target is found
[ERROR] target-size collision    IL size 989: ZERO compiled -- this method never reached the JIT
```

`ZERO compiled` is a statement about the game **only** when `invocations == instrumented` (or the
8-ish re-entrant skips are themself explained). A run where the detour was entered more often than
it inspected cannot distinguish "the game never compiled this" from "the hook looked away", and it
now says so in `hook.log` and in `summary.txt` rather than leaving it to be inferred.

The build regenerates two files from other files — do not hand-edit them:
`GeneratedBuildTable.cs` (from `hook/known-build.json`) and `GeneratedSharedLayout.cs` (from
`native/DrelayHookShared.h`). See §6.

---

## 3. What Phase 1 set out to prove

| | must prove | state |
|---|---|---|
| **Spike 1.1** | native code is resident before the game's `Main`, and says so in a log that predates the game's own | **confirmed live** |
| **Spike 1.2** | the JIT detour installs, and all four targets appear in the compilation stream matched by IL hash | **confirmed live** |
| **Spike 1.3** | one target can be patched and read live state at ~60 Hz for a long session without harm | **patch confirmed live; live read confirmed live (232 and 223 reads, `ARMED`, `PASS 9c`); the long session is not** |
| **Deliverable** | the hook skeleton: self-checks, the Phase 0 target table with a hard refusal, the offset-resolution pass, and an off switch | **built; the offset cross-check runs live against the game's own `World`/`Player` and its verdict is published, for both outcomes** |

Measured, against the installed build (game 5.8.2, `DarzasDominion.dll` MVID
`CBF722C0-56DB-027D-6115-AA7A24CE5307`, 50,216,960 bytes):

| target | IL size | metadata token | matched live |
|---|---|---|---|
| `frameTick` — `DarzaWindow.Update` | 1286 | `0x06001FC5` | yes, every post-fix run |
| `spawn` — `Projectile.ProcessProjectileData` | 2014 | `0x06005214` | yes |
| `collision` — `Projectile.HitCharacter` | 989 | `0x0600521F` | yes, and with the other three in one run (`20261010-015935-p27056` and `20261011-221035-p4364`) |
| `escape` — `World.RequestEscape` | 335 | `0x06002964` | yes |

The live read's own evidence, both runs: `arm state: ARMED`, the pointer chain and reflection
agreeing on `Player.Position` (`(118.5, 116.5)` and `(119.5, 115.5)`), 223 and 232 successful frame
reads against 70 and 62 failures, and a `read value` in the game's own coordinate range (40.5 and
123.493). The failures are a contiguous window *inside* each run, not a warm-up: in
`20261011-221035-p4364` they are frames 12–73 with successful reads on both sides of them, and no
SEH fault was logged for any of them — the read path counts a null or wrong-typed `Player` as a
failure, which is the state §12 taught the *arming probe* to wait on instead. See
[PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md) §4.2–4.3.

`frameTick`'s token is read back from the live runtime and **agrees with Phase 0's**; the
`getMethodDefFromMethod` slot the runtime actually uses is confirmed as **`0x76`** (§5 trap 8).

---

## 4. The faults that blocked it, and what each taught

These cost rounds. They are recorded because each is a *class* of mistake this code invites, not a
one-off.

### 4.1 The leaked re-entrancy counter — why five runs said "0 of 4"

`HookCompileMethod` guards against re-entering itself with a per-thread TLS depth:

```cpp
if (EnterDetour() > 0) {                     // "we are already inside on this thread"
    return g_realCompile(...);               // <-- no LeaveDetour() on this path
}
```

`EnterDetour()` incremented the depth **unconditionally** and returned the *previous* value. The
skip path therefore incremented and never decremented, so the depth grew by one for every nested
compilation and **never came back to zero**. From then on every compilation on that thread hit
`EnterDetour() > 0` and was discarded: not counted, not hashed, not size-matched, not in the size
histogram. A permanent, per-thread blind spot, and its signature is exactly "some methods are
seen, some are not, and the missing ones are reported as never compiled".

Fixed by making a nested entry leave the depth alone — the body is still skipped, but the thread
returns to zero when the outer call finishes. `g_detourInvocations` (counted *before* the guard)
and `g_reentrantSkips` (counted on the skip path) now make the guard's work visible on every run.

**The lesson, and it is the one to carry forward:** the two instruments that "independently"
confirmed `ZERO compiled` — the size histogram and the `OnCompiled` hash comparison — both sat
*downstream of the same guard*. Two instruments that share a code path are one instrument. An
instrument can only be trusted to the extent its own blind spot is measured, which is why
`invocations` is now counted before the guard that used to hide them. See §8.

### 4.2 The frame-tick detour dropped `this` — why the patch crashed the game

`DarzaWindow.Update` is an **instance** method: on Win64 the callee receives `this` in `RCX` and
`total` in `XMM0`. The detour and its trampoline were declared `void(float)`, which reads `XMM0`
correctly and drops `RCX` entirely — and because the trampoline *is* the method's own prologue
followed by a jump back into its body, that body then ran with whatever `RCX` the detour's own
code happened to leave behind.

Measured, twice, on two builds and two pids:

| run | `frameTick` nativeEntry | faulting instruction | access |
|---|---|---|---|
| `20261009-030324-p28196` | `0x00007FFF4E4AB3A0` | `0x00007FFF4E4AB457` (+0xB7) | read of `0x3F` |
| `20261009-030501-p29456` | `0x00007FFF53017A20` | `0x00007FFF53017AD7` (+0xB7) | read of `0x3F` |

A fixed offset in the method body and a fixed bogus base of **zero** is a deterministic wrong
`this`, not a torn instruction or a race. Fixed by carrying the receiver explicitly:

```cpp
typedef void (__cdecl *FrameTickFn)(void* self, float total);
void __cdecl FrameTickDetour(void* self, float total) { ... g_frameTickTrampoline(self, total); }
```

**This is the same class of defect as §4.6** — a hook whose declared signature does not match the
callee's ABI — and it is worth noting how differently the two presented. The earlier one corrupted
a vtable pointer and produced a crash at *shutdown* with a stack that blamed an unrelated frame;
this one crashed at a **fixed offset inside the callee on the very first call**, which is what made
it findable from the crash report alone. When a hook faults, read the fault offset relative to the
patched entry before reading anything else.

### 4.3 A third fault: the histogram was lossy, and it was the instrument of record

`hook.log.sizes` is built from a 512-slot open-addressed table keyed by `size % 512` with a
**64-probe cap**. The comment asserted "512 distinct IL sizes is far more than a session produces
(a few hundred at most)". A real session exceeds that: the run that first found a target filled
**all 512 slots** and reported `61 compilation(s) could not be recorded`, and the preceding run
dropped **101**. The per-target verdict was *derived from that table*, so it could report
`ZERO compiled` about a method that had been compiled.

It did not in fact do so — the probe chain from each target's slot reached a free slot well within
the cap, which is why the earlier finding survived — but a verdict that *can* be produced by an
instrument fault is worse than no verdict. The per-target count is now a direct comparison against
each target's IL size (four comparisons, no hashing, no eviction), the probe covers the whole
table, and the table is 4096 slots. `hook/tools/analyze_size_histogram.py` replays the probe-chain
argument against a saved `hook.log.sizes` and says, per target, whether its absence is **proven**
or **unfalsifiable**.

### 4.4 An instrument that was wrong about what it was measuring

`frames.csv` was headed `arg_total_s,implied_hz`, treating the detour's argument as a frame *delta
in seconds* and printing `1/arg` as a frequency. It is neither: `DarzaWindow.Update`'s one
parameter is `GameTime.TotalGameTime.TotalMilliseconds` — a monotonically increasing **total in
milliseconds**. Printed as seconds it reads as an absurd 27,824-second frame and 0.00 Hz, and a
reader had no way to tell that the instrument was fine. It now reports `arg_total_ms` and the
derived `frame_delta_ms`, which is the number that actually proves the detour is on a periodic
frame loop (~4.9 ms uncapped) rather than a same-shaped neighbour.

The same round fixed a reporting line that had become actively misleading: `target-size` printed
`compiled 1 time(s) but did not match` about a target it had just matched, patched and driven
~1000 times. That wording was accurate only while no target had ever matched.

### 4.5 Operator switches named `DRELAY_HOOK_*` cannot be set — three times now

The launcher builds the child's environment by **deleting every variable whose name begins with
`DRELAY_HOOK_`**. Three diagnostic switches were written with that prefix and were therefore
unreachable through the only supported way to start the game:

| switch | was | now |
|---|---|---|
| the no-patch control | `DRELAY_HOOK_NO_PATCH` | `DRELAY_NO_PATCH` |
| the detour forward-only control | `DRELAY_HOOK_FORWARD_ONLY` | `DRELAY_FORWARD_ONLY` |
| the null frame-callback control | `DRELAY_HOOK_NO_FRAME_CB` | `DRELAY_NO_FRAME_CB` |

The convention is now a rule, not a preference: **`DRELAY_HOOK_*` is the launcher's own namespace
and nothing the operator sets may live in it.** `--collect-delay` is the supported way to set the
one knob that legitimately lives there.

### 4.6 ABI mismatches are this project's recurring failure, and there are now three

**`getVersionIdentifier` (fixed earlier).** `DrelayRuntimeValidate` called it with a *by-value*
16-byte return type where the runtime's own header declares `void getVersionIdentifier(GUID*)`.
The by-value aggregate made MSVC pass a hidden return pointer in `RCX`, pushing the real arguments
along by one register, so the callee wrote 16 bytes of GUID over the first 16 bytes of the
`CILJit` singleton and destroyed its vtable pointer. Two faults resolved against Microsoft symbols
were literally the vtable-slot load — `UnsafeJitFunction+0x59D` and
`CEEInfo::JitProcessShutdownWork+0x26`, both `read of 0xFFFFFFFFFFFFFFFF`.

**`CORINFO_METHOD_INFO`'s shape (fixed earlier).** It had `args` first and `ftn` last, with a
`CORINFO_SIG_INFO` that computed to 96 bytes against the runtime's 112 — putting `ILCode` at 104
instead of 16 and `ILCodeSize` at 112 instead of 24, so the detour hashed an `mdToken`-shaped wild
pointer whenever the size pre-filter happened to match. Both structs are now copied from
`corinfo.h` at `release/9.0` with `static_assert`s on every offset the hook uses.

**`FrameTickDetour` (this round).** §4.2.

The transferable rule: **when a hook's signature is written from documentation rather than from the
callee's own declaration, the failure is not a wrong number — it is a process death that blames
something else.** Every signature this code passes across an ABI boundary is now either copied from
the runtime's header or *measured*.

---

## 5. Traps that will cost hours if you do not know them

**1. The launcher deletes every environment variable beginning with `DRELAY_HOOK_`.** See §4.5.
Name operator switches `DRELAY_*`, and always assert that a control was *engaged* (the run contains
the warning line it was supposed to print), not merely that the behaviour differed.

**2. A startup hook cannot JIT-compile anything.** This is the root cause of five apparently
unrelated crashes and is documented in full in PHASE1-SCOPE.md §6.1. Any call that forces a method
to be compiled for the first time faults the process, and the stack blames the startup hook rather
than the compiler. `ThreadPool.QueueUserWorkItem`, `new Thread().Start()`,
`RuntimeHelpers.PrepareMethod` and the first P/Invoke marshalling stub all died this way. The
bootstrap therefore does only what is already compiled: marshal bytes, set an event, return.
Everything that needs compiling runs behind the **resolve callback**.

**3. A log that only survives success is not a log.** A hard-killed process never reaches `fclose`.
`DrLogFlushNow()` is called immediately after each decisive line, `fflush` precedes `fclose`, and
`DrelayCloseDiscovery` now flushes too — the run that first found a target was patched and died on
the first frame, and its discovery-window and detour-accounting lines were still sitting in the
ring. **When adding a decision point, flush at the point of interest, not at the end.**

**4. Read `shared.block`, not just the log.** It is the only evidence that exists when no log does.
Ad-hoc sentinels written into unused block fields are a legitimate debugging technique here.

**5. `Marshal.OffsetOf` does not work on managed classes.** It throws for any type with object
references — i.e. every interesting type in this game. Offsets are measured with `ldflda` through a
`DynamicMethod` (`FieldOffsetOf`, `StaticFieldAddress`).

**6. Never dereference the mapped block from managed code.** It is a raw dereference of memory
another process writes; the runtime cannot validate it and the failure mode is the whole process.
All access goes through `Marshal` at the generator's offsets.

**7. Two writers on one log file destroy each other's output.** The native side writes `hook.log`,
the managed side `bootstrap.log`; the native side always appends. Separately, **the launcher's live
log tail must open and close its handle per call** — a held handle makes the hook's appends fail
with `ERROR_SHARING_VIOLATION` for the whole session, which is measured and regression-tested.

**8. `getMethodDefFromMethod`'s slot is determined at runtime, and any hardcoded value for it is a
guess — including the one this project previously recorded as a finding.** The hook probes
candidate slots and keeps the one whose answer is a real MethodDef token for a method it has
*already* identified by IL hash. Against the live game, slot `0x7A` returns token `0` and **slot
`0x76` returns `0x06001FC5`, matching Phase 0.** Calling a vtable slot with the wrong signature
does not fail; it calls an unrelated function.

**9. The launcher's `Usage()` writes to stdout, so `Drelay.Launcher.exe --help > --game` creates a
file literally named `--game`.** If you add shell redirection, check for it.

**10. The build script needs `cl.exe` invocations on ONE physical line with `< nul`.** A batch file
does not continue an argument list across a line break, and `cl.exe` with no input file waits on
stdin — so a malformed invocation *hangs* rather than failing. Also: `/Fo"<dir>\"` escapes the
closing quote; use a forward slash before it.

---

## 6. The two generated files, and why they matter

| generated | from | by |
|---|---|---|
| `managed/Drelay.Bootstrap/GeneratedBuildTable.cs` | `hook/known-build.json` | `hook/tools/gen_target_table.py` |
| `managed/Drelay.Bootstrap/GeneratedSharedLayout.cs` | `native/DrelayHookShared.h` | `hook/tools/gen_shared_layout.py` |

The second exists because **the shared block's layout is not hand-computable.** The first attempt
at `[FieldOffset]` values was wrong in three places at once, and the answer is not a better guess.
The layout is generated from the native header, and the generator's model is itself checked by a
compiled probe (`native/SizeProbe.c`) that `build.ps1` runs and diffs against the generator's
prediction, plus a `static_assert` on `DRELAY_SHARED_SIZE`. The generator's model was wrong twice —
embedded structs align to their own alignment, arrays to their element type, not their total size —
and the probe is what said so both times.

**Current measured values**, from `build.ps1`'s own probe output; any document quoting different
numbers for these is stale:

```
DrelayTargetHash 104      DrelayFieldOffset 84      DrelayShared 440912
DRELAY_SHARED_SIZE 440912
```

**If you change a struct in `DrelayHookShared.h`: bump `DRELAY_SHARED_VERSION`, re-run the
generator, and let the probe tell you if the model disagrees.**

---

## 7. Verified, and not

**Verified offline, reproducible:**

- the whole tree builds with no warnings; native at `/W4 /WX`
- the compiler's `sizeof`/`offsetof` agree with the generated managed layout, field by field
- the DLL loads, its `DllMain` is inert, its three exports resolve, ABI version 1, and
  `DrelayHook_Init` fails closed without a session (`Test-HookAbi.ps1`)
- `--off` runs the host cleanly with nothing installed
- the end-to-end chain against a synthetic host: injection before `Main`, the handshake, runtime
  validation, the `compileMethod` detour installing, and the lifecycle checks (`Test-Injection.ps1`,
  **73 checks: the original 43, plus 27 for the arming gate and three for reporting guards**;
  see §12)
- the refusal path against a deliberately tampered image (`Test-PeReader.ps1`)
- the probe-chain argument that says whether a target's `ZERO compiled` is proven
  (`hook/tools/analyze_size_histogram.py`)

**Verified on the real game:**

- the bootstrap runs before `Main`: `DOTNET_STARTUP_HOOKS is running … at t+0,0 ms`, signalling
  the native side 78–141 ms into the process
- the loaded assembly's MVID is `CBF722C0-56DB-027D-6115-AA7A24CE5307` — **matches
  `hook/known-build.json` exactly**, read from the runtime rather than the file, and verified against
  the file on disk (self-check 5a)
- `TieredCompilation` off; runtime `.NET 9.0.20, X64`; JIT/EE GUID verified
- the collection delay is honoured, with heartbeats, and the window opens and closes on time
- `compileMethod` is detoured and observes the stream — 3,556 instrumented compilations in a short
  session, 10,127 in a gameplay session, with `invocations - instrumented == 8` (re-entrant skips)
  in both, and zero unaccounted entries
- **all four targets matched by IL hash**, with `frameTick`'s token read back from the live runtime
  agreeing with Phase 0
- the `getMethodDefFromMethod` slot the runtime uses, confirmed as **`0x76`** by probing
- **`frameTick` patched, its detour entered 999–1002 times, worst call 14 µs, no crash**, with
  `frames.csv` populated
- the live console transcript reaches the operator, and the hook's log survives being tailed
- field offsets measured and published: `World.Player +896`, `Player.Position +328`
- **the live field read** (Spike 1.3's success test): `arm state: ARMED`, reflection and the pointer
  chain agreeing on the real `World`/`Player`'s `Player.Position`, 232 and 223 successful frame reads,
  `PASS 9c`, and a plausible coordinate in `frames.csv`'s `value` column — `20261010-015935-p27056`
  and `20261011-221035-p4364`. Worst frame call 274 µs, and it is the **first** call; 1–2 µs after it
- **all four targets in one session**, with the three unpatched ones verified at their live addresses
- the two process-identity questions a run could get wrong are now answered in the run's own
  evidence: the hooked pid, its image, the foreground window's owner sampled across the discovery
  window, and a census of same-named processes

**Not verified, and each needs live work:**

- **an hour of play with no crash and no frame regression** (exit criterion 1, milestone M3). The
  longest ARMED session is ~3 minutes of process, and its recorded frames cover ~5 s because the live
  window was hard-coded; `--live-seconds` now sets it, which is what makes the hour recordable. One of
  the two ARMED runs also ended in an unattributed CLR fault when the operator closed the game — see
  [PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md) §5.
- **Exit 2a, the denied log target.** Untested, and now restated with a mechanism and four assertions
  in IMPLEMENTATION-PLAN.md; `hook/tools/Run-Exit2a-LogDenial.ps1` is the run. The relay half is Exit 2b,
  Phase 4's, and deliberately absent here.
- **the `token 0x00000000` in the found-log for `spawn`/`collision`/`escape`.** The slot probe
  resolves the token for the *first* match, and the reuse path returns nothing; `frameTick` is the
  only target whose token has been read back live.

**Known gaps, reported as GAPs rather than passes:**

- the bootstrap targets `net8.0`, not `net9.0`: the only SDK here is 8.0.202, which refuses
  `net9.0` (NETSDK1045). Harmless for a startup hook, but it is a guarantee given up.
- in-process MVID is checked by the native side reading the file, not by asking the runtime for the
  loaded image.
- `ICorJitInfo::getFieldOffset` as a second offset source is Phase 2's.
- the behavioural proof that tiering is off (a target compiled twice) — a proof that cannot exist
  unless it happens.

---

## 8. The method note, which is the most transferable thing here

Every wrong conclusion in this phase came from a control or an instrument that was not verified to
be doing what it claimed. The three questions to ask of any control:

1. **Did it run?**
2. **Did it change what it claimed to change?**
3. **Would the log have said so if it had not?**

and the fourth that the two-fault round added:

4. **Is the step you suspect inside the range your control covers — and does your *instrument*
   share a code path with the thing it is measuring?**

The last one is the expensive one. `ZERO compiled` was reported by two instruments that looked
independent — a size histogram and an IL-hash comparison — and both were downstream of the same
re-entrancy guard, so both went blind together and agreed. **An instrument can only be trusted to
the extent its own blind spot is measured.** That is why `g_detourInvocations` is counted *before*
the guard, why the per-target counts no longer read a lossy table, and why the frame detour's own
argument is printed as a monotonically increasing millisecond total rather than a claimed delta:
each of those is an instrument whose failure mode is now itself observable.

Two habits follow, and both have now paid for themselves:

- **A flushed checkpoint after every single step, plus a switch that removes exactly one step.**
- **For anything crossing an ABI boundary, measure the shape; do not document it.** The ABI faults
  in §4.6 were both found by an offset or a value rather than by an argument, and both had been
  "verified" against documentation first.

And the habit that would have saved the most time here, stated plainly because it was available
and unused: **the game's own log is ground truth.** A round was spent arguing that the hooked
process must be sitting at the title screen while the operator played elsewhere, and the answer —
`darza.log` for that exact run, showing `GameScene` and a server connection — was one file read
away the whole time. See §11.

---

## 9. What to do next, in order

1. ~~**Make the managed frame callback and the offset cross-check run *after* the game has a
   world.**~~ **Done — see §12, and see §13 for why the callback itself is gone.** The arming
   decision is made by a managed probe the watcher thread calls at every wait; it checks once a world
   exists, cross-checks the measured offsets against that world, publishes the verdict, and only then
   opens the gate the read goes through. Proven offline both with a world and without one — and the
   read behind that gate is **native**, not managed.
2. ~~**Run one live session, get into gameplay, and read the arm transition.**~~ **Done — twice, on
   2026-10-10 and 2026-10-11** (`20261010-015935-p27056`, `20261011-221035-p4364`). The checklist
   that was written here for that session is worth keeping, because it is what a future run should
   still show, in this order: `frame read path ARMED` in `hook.log` (also on the operator's console),
   `arm state : ARMED` and the two coordinate pairs in `summary.txt`, `PASS 7b` in `selfcheck.txt`,
   `live world read: N successful frame read(s)` in check 9c, and a `frames.csv` whose `value` column
   is a plausible coordinate rather than `0.0000`. Both runs produced all of it. If a run instead
   reports `WAITING`, the world never appeared; if `REFUSED`, the offsets do not describe this build
   and the two coordinate pairs say how.
3. ~~**Name the `0x80131506` death, because it is now the only thing blocking a live read.**~~ **Done
   — see §13.** It was named from the runtime's own PDB and from the crash dumps that were already on
   disk, and the fix is that the frame read is native. Two corrections to the notes that were here,
   because both are traps for the next reader:
   - the death was *not* only in the console. `TerminateProcess` raises nothing the in-process
     reporter can see, but WER `LocalDumps` is enabled on this machine and wrote a **full 47 MB dump
     for every crash**, back to 2026-10-07, in `%LOCALAPPDATA%\CrashDumps`. The exception address and
     the faulting thread's stack are both in there. The console was never needed.
   - `DRELAY_NO_FRAME_CB` no longer exists, and the bisect it would have run has been run (§13).
     There is no managed frame entry point left to suppress.
4. ~~**Get one run that finds all four targets *and* patches `frameTick`.**~~ **Done** —
   `20261010-015935-p27056` and `20261011-221035-p4364` both found 4 of 4 and patched `frameTick`.
   (The earlier "half of this is done" note referred to `20261009-232059-p28164`, which found all
   four in one session but died before the patch could be reported.)
5. **Resolve the "all four targets patched" criterion.** The plan's exit criterion says four; the
   code deliberately patches one, and PHASE1-SCOPE.md §2.4 explains why the other three have no
   Phase 1 behaviour to run. Amend the criterion explicitly or add pass-through detours. Do not
   leave the plan and the code disagreeing.
6. **Read back the token for every target**, not just the first, so §7's last open item closes.
7. **The hour of play** at 60 Hz with FPS capped, no crash and no frame regression — exit criterion
   1 and milestone M3. `--live-seconds` (default 5, unchanged) is what makes it recordable, and
   `hook/tools/Run-Endurance.ps1 -Minutes 60` is the run; the frame read is paused when the window closes.
8. **Then, and only then, Phase 2.** It is read-only, it is the largest single de-risking step in
   the plan, and it produces the ground truth every later phase is tuned against.

Do not start Phase 2 without a live read: it is read-only, it is a sensor, and a sensor that has
never read anything is a design. **That condition is now met**, so what remains before Phase 2 is the
list in [PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md) §8 — the endurance run, the Exit 2a
log-denial test, the token read-back, and the instrument defects — not this list, which is history.
And do not read a `crash.txt` from a run that also has a `jit-internal.txt` as evidence of a crash —
§12 explains which is which.

---

## 10. Defects fixed along the way

Each of these had previously produced a confident wrong answer, and every one was found by
instrumenting rather than by reasoning:

- **the leaked re-entrancy counter** (§4.1) — five runs of `0 of 4` explained
- **the frame-tick detour's dropped `this`** (§4.2) — a reproducible process death on the first
  frame after the patch
- **the lossy size histogram used as the instrument of record** (§4.3)
- **`frames.csv` labelled the frame argument as seconds and printed `1/arg` as Hz** (§4.4)
- **three operator switches named in the launcher's stripped namespace** (§4.5)
- **`CORINFO_METHOD_INFO` and `CORINFO_SIG_INFO` were the wrong shape**, so the detour hashed an
  `mdToken`-shaped wild pointer whenever the size pre-filter happened to match
- **`PublishTargets` wrote each target's IL hash at the wrong offset**, so *every* published hash
  was wrong, and `found` landed inside `reserved0` — which is why the discovery status printed
  `FOUND` for all four targets with zero values
- **`DrPeReadMvid` never read an MVID** — it copied 16 bytes from just past the module row's `Name`
  column, required a PE32+ optional header for an AnyCPU assembly, and skipped the row counts
- **the native MVID comparison ran before the bootstrap had published anything**, so it compared
  zeros against the file and refused to install against the real game
- **the MVID reader failed on every PE32+ image** (it read `NumberOfRvaAndSizes` at the PE32 offset
  for both formats)
- **`ldflda` on a null reference throws on this runtime**, so every field-offset measurement threw
  and published `0 offset(s)`
- **`compileMethod` invocations were counted twice**, so every reported compilation count was
  exactly 2×
- **`selfcheck.txt` reported the identity check as all zeros while passing**, and the strongest
  identity check was never recorded as a check at all (it is 5a now)
- **the launcher's live log tail held its file handle open**, making the hook's appends fail with
  `ERROR_SHARING_VIOLATION` ~371 times a run
- **the MVID log line prints raw memory order**, so the correct GUID appears byte-swapped
  (`C022F7CB-DB56-7D02-…` for `CBF722C0-56DB-027D-…`). Cosmetic — but it cost a round to rule out
  as a wrong-build signal, and it should be formatted as a GUID.
- **`World.Shared`'s static address was truncated to 32 bits** when it was published. On x64 an
  address does not fit the 32-bit `managedOffset` column, so `offsets.csv` and the launcher's report
  printed its low half as though it were a byte offset. Nothing consumed it, so nothing failed —
  which is the quietest kind of wrong, and the reason the fix is a documented two-halves encoding
  rather than a wider field (§12).
- **the `agree` column of `offsets.csv` had one producer and that producer was Phase 2's**, so it
  read `0` in every Phase 1 run and self-check 7 reported three GAPs that no amount of live play
  would have closed. It now has the producer Phase 1 can supply — the pointer chain — and
  `jitValid` names which oracle spoke, because "the JIT said so" and "a live walk said so" are
  different claims (§12).
- **the bootstrap's numbers were culture-formatted, and this machine's culture is `sv-SE`.** The one
  managed participant in the run printed `at t+0,0 ms` and `reflection (12,5000, -7,2500)` while
  `hook.log`, `summary.txt`, `selfcheck.txt` and `offsets.csv` — all native, all `%.1f`/`%.4f` in the
  C locale — printed `0.0` and `12.5000`. **The two halves of the one comparison that proves the
  offsets are real therefore read as different numbers**, and a parser that accepted `12.5000`
  silently failed to see the confirmation: the arming-gate test failed on its first run for exactly
  this reason. The handoff had quoted `at t+0,0 ms` as evidence for months without noticing. Fixed
  by formatting every numeric bootstrap message with `CultureInfo.InvariantCulture`; the process
  culture is deliberately **not** changed, because a hook that reconfigures its host's formatting to
  tidy its own log lines is a hook that changes the thing it is measuring.
- **the launcher's end-of-run report said Spike 1.1 had failed on every run.** It searched
  `hook.log` for `DOTNET_STARTUP_HOOKS is running` and printed
  `spike 1.1 : the bootstrap line is MISSING from hook.log` — but the bootstrap writes
  `bootstrap.log`, deliberately, because two writers on one file destroy each other's output. The one
  report an operator reads at the end of a live run therefore contradicted the design it was
  reporting on. It now searches both files and names the one the line was found in.
- **the arming cross-check refused on a world that had no player in it yet.** Found by the first two
  live runs that reached gameplay, and it disabled the sensor for the whole session. `World.Shared`
  becomes non-null while a scene is still loading; `World.Player` follows seconds later.
  `FieldInfo.GetValue(null)` on the instance field threw `TargetException`, `TryArm` caught it and
  refused — the right response to an exception and the wrong one to "not yet". The readiness test is
  now the production read path (the raw pointer chain, which cannot throw) and requires both objects;
  a world without a player waits, logs once, and is reproduced offline by
  `DRELAY_TEST_PLAYER_DELAY_MS` (§12).
- **`crash.txt` reported a routine JIT retry as a crash.** `0x02345678` is the JIT's
  `FATAL_JIT_EXCEPTION`, raised by `fatal()` and caught by the JIT's own `__JITfilter`, which turns it
  into `reportFatalError(CorJitResult)`. `param[0] 0x80000005` is `CORJIT_RECOVERABLEERROR` — a
  `noway_assert` the runtime recovers from. The reporter's filter list did not know the code, so it
  wrote a crash report whose stack showed clrjit with our `compileMethod` detour one frame below,
  which reads as the hook breaking the compiler. It now goes to `jit-internal.txt` (§12). The same
  investigation found the opposite hole: the run's *actual* death,
  `COR_E_EXECUTIONENGINE (0x80131506)`, terminates through `TerminateProcess` and raises nothing, so
  the reporter cannot see it and wrote no report at all.

---

## 11. Two environment traps specific to this machine

- **A stale game process holds `Drelay.Hook.dll` and every native build fails with
  `LNK1104: cannot open file ...`.** Check for and kill a leftover `DarzasDominion` or
  `Drelay.Launcher` first; it is not the documented retry-once quirk.
- **The DSH agent harness cannot create the launcher's run directory under `%LOCALAPPDATA%`.** A
  `Drelay.Launcher.exe` started from that harness fails at
  `Directory.CreateDirectory(options.RunDirectory)` with `UnauthorizedAccessException`, while the
  same call from the harness's own PowerShell, from python and from a fresh .NET child all succeed —
  it is a property of processes spawned from that sandbox, not of the launcher. **Pass `--runroot`
  pointing inside the repository** (`hook\hook\obj\live-runs`) and it works. The
  operator's own shell is unaffected.

**And one instrument that is not a trap but is worth knowing about:** the game writes its own log to
`%LOCALAPPDATA%\RippleStudio\Darza\logs\darza.log`, timestamped, naming the scene, the focus owner
and its own JIT counts. **It is the ground truth for "which process played, and how far it got."**
Read it before concluding anything about a run's coverage.

---

## 12. The arming round: why the sensor had never run, and what replaced the gate

This is §9 item 1. It is written up because the fix is a *design* change rather than a bug fix, and
because the old design was defensible right up until the moment it was measured.

### What was wrong

> **SUPERSEDED BY §13, IN PART.** The arming design below is still the live design and the cross-check
> is unchanged. What §13 removed is the *managed frame callback* this section treats as the sensor's
> reader: it cannot work at all — entering managed code from the frame detour is an illegal reverse
> P/Invoke — and the read is native now. Read §13 before reading the table below that describes
> `ManagedFrameCallback.OnFrame`.

`Spike 1.3`'s success test is *read exactly one float from the live world*. The reader existed — a
managed `[UnmanagedCallersOnly]` callback the frame detour called once per frame — and it had never
read anything, because **the decision to enable it was made at the wrong moment**. The bootstrap
armed it (or disabled it for the session) at attach, and at attach `World.Shared` is always null: the
world does not exist until the game loads a scene.

The old code did not hide this. It logged

```
[WARN] World.Shared is still null this early in startup, so the pointer chain cannot be
       cross-checked yet; the managed frame callback is disabled for this session
```

on every run, and reported the offsets as **UNVERIFIED rather than wrong** — which is the right way
to report a check that could not be made. The defect was in the consequence, not the message: a check
that could not be made was allowed to switch off the sensor that the whole of Phase 2 is built on.
**A GAP that disables something is a FAIL wearing a GAP's clothes.**

### The design that replaced it

There are now **two managed entry points on two threads**, and the split is the whole change:

| | runs on | does | must not |
|---|---|---|---|
| `ManagedFrameCallback.OnProbe` | the native watcher thread | waits for a world, then *once* cross-checks the measured offsets against it and publishes the verdict | — |
| `ManagedFrameCallback.OnFrame` | the game's own thread, inside the detour | reads one float, once per frame | allocate, block, or throw |

The probe is called by the watcher at **every wait it does** — the collection delay, the discovery
poll, the live-check wait and the final idle loop — so a world that appears in ten seconds and one
that appears in three minutes are caught by the same code. Each call after the decision is one
volatile read and a return.

**Why not the frame detour, which §9 item 1 originally suggested.** Three reasons, and the third is
the one that decided it:

1. the cross-check needs reflection, which allocates and can trigger a GC; the frame callback's
   entire contract is that it does not, and it runs inside the game's frame and a native `__try`;
2. the frame detour's receiver is a `DarzaWindow`, **not** the world — the world reference comes
   from a static field, so the frame thread knows nothing the watcher thread does not. The
   "receiver in hand" argument does not apply to this question;
3. **a gate that only the frame detour can open cannot be tested without the game.** The synthetic
   host cannot match the game's IL hashes, so no frame tick is ever patched offline, so an
   arming path living in `OnFrame` would have been unreachable by every automated test — a fix for
   the phase's blocking item that could only ever be checked by an operator session. The probe is
   driven by the watcher, which the offline harness *does* run, and that is why runs 5 and 6 below
   exist at all.

### The verdict, and the four states

The gate is published in the shared block as `managedArmState`, and **four states is the point**:

| state | means | reported as |
|---|---|---|
| `WAITING` | no world has existed yet; the callback exists and reads nothing | GAP (check 7b), with the probe and frame counts |
| `ARMED` | the pointer chain reproduced reflection; the callback reads | PASS, naming both coordinate pairs |
| `REFUSED` | the chain did **not** reproduce reflection — the offsets do not describe this build | FAIL, naming both coordinate pairs |
| `UNCONFIGURED` | no offsets were ever measured, so there was nothing to check | FAIL — a different owner from `REFUSED` |

`UNCONFIGURED` and `REFUSED` are separate because one is a missing prerequisite and the other is a
finding about the layout. Collapsing them into "not armed" is the mistake §8 is about.

The comparison itself is of **coordinates, not addresses**: an address comparison would test that
two routes reached the same object, which says nothing about whether the bytes at
`Player + positionOffset` are the floats the runtime's own accessor reports.

### What the run now carries

- `hook.log`: `frame callback ARMED (t+… ms, after N probe(s) found no world): reflection and the
  pointer chain agree on Player.Position = (x, y)`, flushed at the transition — so the operator sees
  it on the console while the game is still running, not in a file written minutes later. The
  `REFUSED` and `UNCONFIGURED` transitions are logged the same way.
- `summary.txt`: `arm state`, `armed at`, `cross-check : reflection (…) vs pointer chain (…)`, and
  `waited for world: N probe(s), M frame(s) found none`, printed **above** the read counts, because
  two zeroes under `managed reads` mean something entirely different depending on the arm state.
- `selfcheck.txt`: check **7** now says *which* route confirmed each offset
  (`confirmed by the pointer chain`), **7b** is the cross-check itself, and **9c** is Spike 1.3's
  success test — a PASS only when a read actually succeeded.
- `offsets.csv`: `agree` is now written by the pointer chain (Phase 1's second route) with `jitValid`
  naming the oracle, and a **static** field's column is a 64-bit address printed as
  `0x<high><low>` instead of its truncated low half.
- The shared block is **version 4, 440912 bytes** (`managedProbeHook`, `managedArmState`,
  `managedProbeWaits`, `managedFrameWaits`, `managedArmTick` and the four cross-check floats).

### The two tests, and what each one is for

`Test-Injection.ps1` gained two runs against the same synthetic host, which now declares types with
the game's *shape* — a static `World.Shared`, an instance `Player`, an inline two-float
`GamePoint Position` — so that the offset measurement, the probe and the cross-check all run without
the game:

- **run 5** gives that host a world three seconds after `Main`. It asserts the offsets were measured,
  that **at least one probe looked and found no world** (so the gate demonstrably did not open at
  attach), that the callback armed, that both routes and both coordinate pairs are in the evidence,
  that all three walked offsets carry `agree=1`, that the static address is printed whole, and that
  the live-read check reports *"armed but no frame has entered it yet"* rather than a pass — because
  this host has no frame tick, and a test that claimed a live read here would be inventing evidence.
- **run 6 is the control**: the same run with the world removed. The callback must never be reported
  as armed, the offsets must still be measured, the probe must still be seen looking, and the
  cross-check must be reported as a GAP. Without it, run 5's PASS would be indistinguishable from a
  cross-check that reports PASS unconditionally.

**Run 6 is the control, in the tradition of §8's third question** — *would the log have said so if
the gate had not worked?* One variable removed, and the log required to say so.

### Now proven, and what it took

**THE WORLD IN RUN 5 WAS A STAND-IN, AND THE GAME'S OWN `World`/`Player` HAVE SINCE BEEN THROUGH
IT.** The paragraph that used to be here said a live session was still needed. Two have happened
(2026-10-10 and 2026-10-11): both report `ARMED` against the real types at their real offsets, with
the pointer chain and reflection agreeing, and 232 and 223 successful frame reads. Two details in
the old wording are stale as well and are worth naming, because both were design, not scheduling:
the reader is **not** the managed callback (it is native — §13), and `frames.csv`'s `value` column now
holds a coordinate (40.5000 and 123.4930) rather than `0.0000`. See
[PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md).

### What the live runs found, including the bug this design shipped with

Three live sessions were run. **None of them was a wasted run**, and two of them were paid for in
full:

**`20261009-231054-p25440` — the aborted one.** The game never left its loading screen and was
closed during the discovery window, so no evidence files were written. It still settled four things:

- the offsets measured against the *real* assembly are `World.Player +896` and `Player.Position +328`,
  unchanged — the round did not disturb the measurement;
- `World.Shared`'s address printed whole — `0x000001FD02402DE0` — where the old code printed
  `0x02402DE0` and called it an offset;
- the launcher's report said `frame callback : waiting for a world` for the whole run, so the gate's
  new state is visible from outside the process as well as in the files;
- no world appeared during 45 s of collection delay plus the start of the discovery window, on a game
  that had not yet reached gameplay. That is the reason the probe keeps polling rather than sampling
  once — and it means a live run has to reach an actual scene.

**`20261009-232258-p27856` and `20261009-232059-p28164` — both reached gameplay, both hit the same
bug, and one of them found all four targets.**

The arming probe did its job and then refused:

```
[ERROR] frame callback REFUSED: reflection says (0.0000, 0.0000) and the pointer chain built from
        the measured offsets says (0.0000, 0.0000) -- the offsets do not describe this build
```

and the reason was in `bootstrap.log`, not in that line:

```
[ERROR] boot: the arming cross-check threw, so the frame callback stays off:
        System.Reflection.TargetException: Non-static field requires a target.
   at System.Reflection.FieldAccessor.GetValue(Object obj)
   at Drelay.Bootstrap.ManagedFrameCallback.ArmIfWorldExists() in StartupHook.cs:line 235
```

**`World.Shared` becomes non-null while a scene is still loading — about ten seconds into a run —
and `World.Player` stays null until a character exists.** The cross-check treated a non-null world as
"ready", asked reflection for a player that was not there, and got `TargetException` from
`FieldInfo.GetValue(null)` on an instance field. `TryArm` caught it and refused — the right response
to an exception and the wrong response to this condition — so a state that was merely **not yet**
disabled the sensor for the session.

That is the same defect this round exists to fix, one level down: **a not-yet-ready state reported as
a finding.** The fix is to make the readiness test the *production* read path — the raw pointer
chain, which cannot throw — and to require both `World` and `Player` before reflection is asked
anything. A world that exists with no player in it now logs once, increments the wait counter, and
keeps waiting. `SyntheticHost` reproduces the window offline (`DRELAY_TEST_PLAYER_DELAY_MS`,
default 3000 ms, measured from the world's creation), and run 5 asserts all three properties: the
window was observed, it was logged, and it did not refuse the callback. The control run's GAP text
changed from "no world existed" to "no live player existed", because those are now different facts.

**One of the two runs also found all four targets**: `frameTick`, `spawn`, `collision` — which had
matched only once before — and `escape`, in a single session. That is §9 item 3's "one run that finds
all four", reached on the first run that got far enough into gameplay. Both runs died before the
patch could be reported (see below), so "all four found and `frameTick` patched in one run" is still
open.

### Two things in `crash.txt` that were not crashes

Both live runs left a `crash.txt`, and reading it as a crash report wasted an hour. There were two
distinct records in it and **neither was the process's death**.

**`exception 0x02345678 ... param[0] 0x80000005`** is the .NET JIT's own give-up path, not a fault
at all. `0x02345678` is `FATAL_JIT_EXCEPTION`, raised in exactly one place — `fatal(int errCode)` in
`src/coreclr/jit/error.cpp`:

```cpp
ULONG_PTR exceptArg = errCode;
RaiseException(FATAL_JIT_EXCEPTION, EXCEPTION_NONCONTINUABLE, 1, &exceptArg);
```

and caught by the JIT's own filter in the same file, which turns it into
`jitInfo->reportFatalError((CorJitResult)errCode)` and returns `EXCEPTION_EXECUTE_HANDLER`. So
`param[0]` is a `CorJitResult`, not an address: `0x80000005` is `CORJIT_RECOVERABLEERROR`, i.e. a
`noway_assert`, and the runtime retries the method with MinOpts. The signature is identical in a run
recorded before this round's changes — same code, same parameter, same six `clrjit.dll` offsets —
so it is not the hook, and it is not new.

It was still worth recording: the reporter's filter list knows about managed exceptions, C++
exceptions and thread names, and does not know about this one, so a routine JIT retry produced a file
called `crash.txt` whose stack showed clrjit and our `compileMethod` detour one frame below — which
reads exactly like the hook corrupting the compiler. It now goes to **`jit-internal.txt`**, says in
its own header that the runtime handles it, and names the `CorJitResult`. `crash.txt` is reserved for
faults.

**The actual death is not in `crash.txt` at all.** The Windows event log has it:

```
.NET Runtime, Id 1023: The process was terminated due to an internal error in the .NET Runtime
at IP 0x...244839 with exit code 0x80131506.
Application Error, Id 1000: faulting module coreclr.dll, exception code 0x80131506,
offset 0x244839
```

`0x80131506` is `COR_E_EXECUTIONENGINE` — "Internal CLR error", the same class of failure §4.6 and
PHASE1-SCOPE.md §6.1 record from earlier rounds. It terminates through `TerminateProcess`, which
raises nothing, so **the crash reporter cannot see it and wrote no report.** That is a real hole in
the evidence: the one failure this project most needs a stack for is the one that leaves none. The
console, meanwhile, prints `Fatal error. Internal CLR error. (0x80131506)` with a managed stack, and
that text is the thing to capture on the next run.

**What the timing implicated, and the operator spotted it rather than me.** Both crashes happened at
the end of the collection window, which is `DrelayPatchTargets()`: with the window closed, `frameTick`
is patched, and the very next frame enters `FrameTickDetour`. Neither run's log contains `PATCHED`,
which is only flushed on the 250 ms cadence, so the death was inside a few hundred milliseconds of the
patch going live — about five frames at 200 Hz. That is precisely when the frame detour runs for the
first time, and **this round is what made it able to call managed code at all**: before it, the
bootstrap zeroed the callback address whenever the cross-check was unavailable, so `g_managedFrame`
stayed null and the detour never entered managed code. In both crashed runs the cross-check *refused*,
so the callback was never armed **and therefore never pre-compiled** — the first frame after the patch
had to JIT-compile `OnFrame`, on the game's frame thread, inside a native detour, with the game's own
update on the stack. §6.1 is a catalogue of what compiling in the wrong context costs in this process.

Two changes follow, and the second is what makes the first an ordering rather than a hope:

1. `PrepareFrameCallback()` now runs on the **first probe call**, before any state decision — not in
   the "armed" branch where it started. The detour reaches `OnFrame` whether or not the gate ever
   opens, because the gate is checked *inside* `OnFrame`; a run whose cross-check refuses still
   executes uncompiled managed code inside the game's frame.
2. `DrelayPatchTargets()` calls `DrelayPollArmProbe()` **immediately before the patch loop** and logs
   `the managed frame callback is prepared; patching may go live`. The probe is driven by a polling
   *cadence*, and a run whose waits all ended early could reach the patch without it having run; the
   explicit call removes the cadence from the argument.

This is a mitigation against a measured hazard, not a proven fix: the death leaves no stack, and the
hypothesis is timing plus mechanism rather than a symbol. If the next live run dies the same way, then
the managed call from the frame detour is illegal for a reason other than JIT context — and the prime
candidate is the `[UnmanagedCallersOnly]` **reverse-P/Invoke transition**, which the runtime performs
on the assumption that the caller is unmanaged (preemptive), while a detour injected into managed code
runs with the thread still in cooperative mode. `OnProbe` works because the watcher thread is a plain
native thread; the game's frame thread is not. `DRELAY_NO_FRAME_CB=1` is the control that decides it:
it publishes a null callback address, so the detour never enters managed code and nothing else changes.
A run that survives under it and dies without it is the answer.

---

## 13. The `0x80131506` round: the frame read is native

This is §9 item 3, and it closes the crash rather than mitigating it. It is written up separately
because the round's premise was wrong in a way that is worth reading before touching the frame path.

### What the death was

`coreclr.dll+0x244839` resolves, against the runtime's own cached PDB, to
**`ReversePInvokeBadTransition + 0x9`** — the runtime's deliberate refusal of a reverse P/Invoke made
from a thread it does not consider unmanaged. The faulting thread's stack from the dump of run
`20261009-003209-p31396`:

```
coreclr!JIT_ReversePInvokeEnterRare + 0xBE
coreclr!ReversePInvokeBadTransition + 0x9     <- the exception address, 0x80131506
coreclr!EEPolicy::HandleFatalError + 0x85
Drelay.Hook.dll+0x4C1F   = FrameTickDetour + 0xDF
DarzasDominion.dll+0x2F54405                  <- the game's own managed frame, below the detour
```

The frame detour runs on the game's thread **while that thread is inside managed code** — between the
managed caller of `DarzaWindow.Update` and its body — so the thread is already in cooperative GC mode
with live managed frames below the detour. A reverse P/Invoke asserts that its caller is unmanaged,
because the transition frame it pushes is where the GC stops walking the managed stack. The runtime
refuses it, and `TerminateProcess` means no in-process reporter sees anything.

`managedFrameWaits` was `0` in every crashed run's shared block: the method body was never entered.
The refusal is in the stub prologue, **one level before the compile the previous round was trying to
avoid** — so `PrepareFrameCallback` could not have helped, and neither could anything else inside
`OnFrame`. The previous round's other change, publishing the callback at startup, was already true of
its own build: `20261009-232258`'s `bootstrap.log` says `frame callback 0x7FF7A7760F00`, and that run
is one of the six dumps with this exact exception address.

### The bisect

| run | patched | managed callback | detour entered | outcome |
|---|---|---|---|---|
| baseline ×3 | yes | published | yes | died ~1 s after the patch, `0x80131506` |
| `DRELAY_NO_PATCH=1` | no | published | never | survived 281 s |
| `DRELAY_NO_FRAME_CB=1` | yes | null | yes — 4 963 frames, worst 1 µs | survived 115 s |

The third row is the isolation: same hook, same target address, same trampoline, and only the managed
call removed. That is the second branch §12 predicted, and it also identified the mechanism, which is
that the *thread*, not the attribute, is the problem.

### What changed

* **The frame read is native.** `FrameTickDetour` dereferences the `World.Shared` static field itself,
  walks to `Player`, checks the object's MethodTable against the type handle the bootstrap publishes
  in `managedPlayerTypeHandle` (low three mark bits masked), reads the two `Position` floats, bounds
  them, and records a success or a failure — inside a `__try`, because that is the one thing a native
  read can do that a managed one could not. It is the same chain the arming cross-check already
  exercises as its production path.
* **`OnFrame`, `PrepareFrameCallback`, `GetFrameCallbackAddress` and `DRELAY_NO_FRAME_CB` are gone**,
  deliberately and with the reasoning recorded where they were, so no reader finds a dead mechanism
  that looks live.
* **The shared block is version 5.** `managedFrameHook` became `managedPlayerTypeHandle` at the same
  offset and size, so the block is still 440 912 bytes; the version bumped because the field's meaning
  changed. `DRELAY_CFG_MANAGED_READ` is retired rather than left defined — the bit must not be reused
  without a new name, because a saved block or an older launcher means the old thing by it.
* **The pre-patch preparation is gone**, because there is nothing to prepare: the detour needs no
  compiled managed code, no published address and no ordering against the probe's cadence.
* **`frameCalls` is published before the read.** It used to be written after the managed call, so every
  failure inside that call reported `frame calls: 0` — an instrument whose blind spot was exactly the
  failure it would be consulted about.

### What the round verified

* `Test-Injection.ps1`: **73 of 73**, including three new checks — the frame path reports itself as
  native, no managed frame path remains, and the `Player` MethodTable is published for the read.
* A live run on the title screen with `--collect-delay 0`: patched `frameTick`, **294 frames**, worst
  call 8 µs, `phase 1 complete`, and the process still alive 104 s later when the operator-kill ended
  it. The same configuration previously died about a second after the patch.
* Its `selfcheck.txt` says the read path is closed with `arm state: WAITING` and 139 probes found no
  live player, which is the correct report for a title screen and the evidence that the gate works.

### What was still not verified when this was written, and what it needed

**One real frame read.** The synthetic host cannot match the game's IL hashes, so no frame tick is
patched there, and the title screen has no player. This needed an operator session that reached
gameplay. What to look for, in order: `frame read path ARMED` in `hook.log`, `arm state : ARMED` in
`summary.txt`, `PASS 9c live world read` in `selfcheck.txt`, a plausible coordinate in `frames.csv`'s
`value` column, and `frame reads` rising rather than `frame reads 0 ok / 0 failed`.

**That session happened on 2026-10-11 and produced every one of those lines**
(`%LOCALAPPDATA%\drelay\hook\20261011-221035-p4364`: 232 ok / 62 failed, value 123.4930, `ARMED`).
What is still not verified is the *endurance* half of the same criterion — an hour, at 60 Hz with FPS
capped, across a zone transition, with a forced collection and an alt-tab — and the tooling for it is
`--live-seconds` plus `hook/tools/Run-Endurance.ps1`.

### The question this leaves open

Nothing in the process can run managed code on the game's thread, and Phase 3 will need to. That is a
separate research task with its own handoff: **[MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md)**.
HOOK-ARCHITECTURE.md §5.2 rule 3 said the opposite of what this round measured and has been rewritten.
