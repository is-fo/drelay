# Phase 2 — scope, entry plan, and the prerequisite that decides its shape

**Date opened:** 2026-10-12. **Branch:** `autododge-research`.
**Read with:** [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §"Phase 2 — Observe, and find out
whether the model is true" (the criteria this document executes), [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md)
§2–§4 (the pipeline, the threat set, the trajectory), [HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §5
(what is safe to read from the game's thread), [MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md)
(the open mechanism question), and [PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md) §11 (the state
Phase 1 was closed at).

---

## 1. Where Phase 2 starts

Phase 1 is closed ([PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md) §11.6). The build Phase 2 starts
from is the one the two acceptance runs of 2026-10-12 were produced by, whose SHA-256s are recorded
in `hook\obj\cases\exit2a-logdenial\build-identity.txt` and `hook\obj\cases\endurance-60m\`.

What that build gives Phase 2, as facts rather than as plans:

- native code is resident before `Main`, and the runtime, the assembly and the JIT/EE GUID are
  validated on every launch;
- the JIT detour (`compileMethod`) sees the compilation stream, matches targets **by IL hash**, and
  the target table already contains all four targets Phase 2 needs — `frameTick`, `spawn`
  (`Projectile.ProcessProjectileData`), `collision` (`Projectile.HitCharacter`) and `escape`
  (`World.RequestEscape`);
- `frameTick` is patched, and the detour **reads live state from the real game** — 198 586 successful
  reads in the endurance hour at a steady-state cost of 0–1 µs per frame;
- the managed bootstrap resolves field offsets **by reflection at startup** and publishes them into
  the shared block (three offsets today: `World.Shared`, `World.Player.Position` and the `Player`
  type handle), and it can call into the native side, and the native side can call a managed
  callback on an unmanaged thread it owns.

What it does **not** give, and what Phase 2 needs:

- a way to run *our own logic* on the game's thread at frame time. Phase 1's frame detour only
  **reads**; everything it does is a dereference of a raw pointer, which is legal precisely because
  no managed code runs (HOOK-ARCHITECTURE.md §5.2 rule 3);
- a per-frame cost distribution — Phase 1 kept a cumulative maximum, so its own 323 µs outlier
  cannot be placed in time or in a percentile (§6.1).

## 2. Goal, and the four numbers

Phase 2 is **read-only**. It changes nothing about the game. Its goal is to answer the question that
decides whether a dodge is worth building:

> **Does our prediction of a collision agree with the game's?**

and to publish the four numbers the plan names ([IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md)
§"Deliverable 2.3"):

| number | what it decides | how it is produced |
|---|---|---|
| **warning distribution** | whether an escape can be dispatched in time; Phase 3's thresholds | per predicted-then-real collision, the ms between the first frame we predicted it and the frame the game resolved it |
| **false negatives** | whether the model is complete enough to rely on | collisions the game made that our sensor never predicted, each with a named cause |
| **false positives** | whether a dodge would be over-cautious (the `#ghost` rate) | bullets we predicted would hit that never did |
| **per-frame cost p50/p99** | whether the Phase 4 solver fits the budget at all | a **histogram**, not a ring (§6.1) |

Plus the fifth measurement the plan calls out separately: **what fraction of real bullets are
straight**, from the linearity gate's own log.

**"A collision the game made" means `HitCharacter()` returned `true`, and saying so is not
pedantry.** The method is the client's per-frame collision *predicate* — `Projectile.Update` calls
`Hit()` for every live projectile every frame — so a hook on it sees a question per bullet per frame,
and only the `true` answers are decisions. An instrument that counts entries instead publishes a
false-negative rate inflated by however many frames each bullet spends near the player. That is not
hypothetical: the first live session with the comparison installed counted 418 328 entries against 48
resolved hits, and §4.15 is the accounting.

## 3. The prerequisite: how our logic runs on the game's thread

This is the decision that shapes the whole phase, and the shipped build has already partly answered
it — in the opposite direction from where the handoff left it.

**What is established.** A **reverse P/Invoke** from the game's frame thread into an
`[UnmanagedCallersOnly]` stub is refused: the thread is already inside managed code, so
`JIT_ReversePInvokeEnterRare` calls `ReversePInvokeBadTransition` and the runtime fail-fasts with
`COR_E_EXECUTIONENGINE` (0x80131506) via `TerminateProcess`, at `coreclr+0x244839`, six dumps over
three builds, with `managedFrameWaits == 0` proving the managed body never started
([MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md) §2). That measurement stands and nothing here
contradicts it.

**But the conclusion drawn from it — "entering managed code from that thread is the thing that is
refused" — is too broad, and the shipped build falsifies it.** `FrameTickDetour`
(`native\DrelayFrame.cpp:280`) ends with

```cpp
if (g_frameTickTrampoline != nullptr) {
    g_frameTickTrampoline(self, total);      // native\DrelayFrame.cpp:360-361
}
```

and `g_frameTickTrampoline` is MinHook's **trampoline**: a copy of `DarzaWindow.Update`'s overwritten
prologue followed by a jump back into its JIT-compiled body (`DrelayFrame.cpp:485`, `:504-506`). So
every frame of the **hour-long acceptance run of 2026-10-12** was: game's `call` → `jmp` into native
`FrameTickDetour` → native `call` into the trampoline → `jmp` into **JIT-compiled managed code**,
with the managed body's return address sitting in `Drelay.Hook.dll`. That happened **209 897 times in
one hour**, across ~24 collections recorded in the game's own log, and the game kept playing.

So the distinction is not "native vs managed" — it is **which entry path**:

| route into managed code, from the game's thread | state |
|---|---|
| a **plain** call into JIT-compiled code (the trampoline's `jmp` into the body) | **measured working**: 209 897 calls, one hour, no fault |
| an `[UnmanagedCallersOnly]` / `Marshal.GetFunctionPointerForDelegate` **transition stub** (a reverse P/Invoke) | **refused, fail-fast** — six dumps |

The difference is that the second goes through a prologue that asserts the caller is unmanaged. The
first does not: the callee simply runs, in the cooperative mode that running managed code requires.

**What this changes for Phase 2.** The mechanism the sensor needs is *the route already in
production*, not the jump-detour research the handoff nominates. The shape is:

1. the bootstrap JITs a managed helper of **our own design** and publishes
   `RuntimeMethodHandle.GetFunctionPointer()` for it into the shared block — a new publication, since
   the old frame-callback field and `DRELAY_CFG_MANAGED_READ` were deliberately deleted
   (`StartupHook.cs:820-841`, `DrelayHookShared.h:132-137`) and must not be resurrected under their
   old names;
2. the native detour calls that pointer directly (the trampoline's route), passing only arguments;
3. the helper runs on the game's thread with a real managed stack, so `ProjDesc.GetPosition`, the
   pooled projectile set and the managed argument types of `spawn` and `collision` are all ordinary
   C# — problems 1, 2 and 3 of §3.1 below disappear together.

**The one difference from the measured case, and therefore the thing to test.** The route is proven
for *calling the game's own method*. What is **not** measured is a helper of ours that **allocates**,
**calls other managed methods**, and **triggers collections while it is on the stack** with a native
return address beneath it. Reasoning about it: when the GC walks that stack it finds the helper's
JIT info from its IP, unwinds to a return address in `Drelay.Hook.dll` that belongs to no managed
code range, finds no transition `Frame` there, and — by the runtime's own rule — **stops**. Every
managed frame *outside* our native detour is then not scanned. The trampoline case has the same shape
and survived an hour of real play, but "it survived an hour" is not a proof, and the failure it would
hide is a collected object that a caller-frame local still points at. That is exactly what a forced
collection with a deep stack and a caller-held object catches, and it is what Spike 2.0 measures
(§4).

**Why not the jump detour into a managed replacement.** It remains a valid second candidate — it is
what Harmony and MonoMod do, and it has the attractive property that the runtime decodes the
sixteen arguments of `ProcessProjectileData` for us. But it is *more* machinery than the route the
build already exercises, and its continuation form (the managed replacement calling the trampoline
through an unmanaged function pointer, i.e. managed → native → managed) is the shape
[MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md) §3 warns about. It is kept as the fallback if
Spike 2.0 shows the plain-call route cannot allocate safely, not as the first thing to build.

### 3.1 Why Phase 2 cannot simply stay native

Reading is native-safe (no transition), but Phase 2 needs three things a read cannot give:

1. `ProjDesc.GetPosition(...)` — a **virtual managed call**, per bullet per frame. This is the whole
   reason the plan says "call the game, do not reimplement it" ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §4.1),
   and reimplementing the trajectory family set is the research project that section rules out;
2. **enumerating the live projectile set** each frame — a pooled managed collection, a pointer walk
   whose layout, ownership and liveness rules would all have to be re-derived by hand;
3. the **signatures of the spawn and collision hooks**. `ProcessProjectileData` has arity 16. A native
   detour body can only decode those arguments by declaring them; a managed helper declares them and
   the runtime does the decoding. This project has three ABI faults on record, every one of them from
   reading a signature instead of measuring it (PHASE1-HANDOFF.md §4.6), and this is the hardest such
   reading in the whole design — so the mechanism that removes it entirely is worth more than the
   mechanism that merely makes it expressible.

**A second, independent reason to settle this now.** Phase 3 needs the same mechanism to call
`World.RequestEscape`, and [PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md) §9 records the operator's
alternative — delegating the *act* to the Java relay — which only helps if the hook can still
*publish* the decision. Either way the mechanism question is upstream of Phases 3 and 4, and it is
cheaper to answer it once, on a test host, than twice in a live run.

## 4. Spike 2.0 — the mechanism test (synthetic host, offline)

Falsifiable, and no game required. The harness to extend is `hook\tools\Test-Injection.ps1` with
`hook\tests\SyntheticHost`, which already stages a .NET 9 host with tiering off and drives the hook
through numbered offline runs.

The run to add — **the plain-call route, with the three things the measured case does not cover**:

1. the host calls a **cadence method** on a fixed interval — a stand-in frame tick, an instance
   method taking a `float` plus a `ref`/`out` parameter, so a broken ABI shows up as a wrong value
   rather than as no call at all;
2. that method's entry is patched with the same MinHook detour the build already uses, dispatching to
   a **native** detour body that calls a **managed helper** published by the host's own bootstrap —
   a plain static method, *not* `[UnmanagedCallersOnly]`, whose address comes from
   `RuntimeHelpers.PrepareMethod` + `RuntimeMethodHandle.GetFunctionPointer()`;
3. the helper **allocates** (so a collection can happen inside it), **calls another managed method**
   (so the helper is not a leaf), and returns a value the native side logs;
4. the host counts its own body executions and prints them;
5. assertions: the helper was entered once per call, its arguments arrived intact, the host's body ran
   exactly once per call, and the host's cadence did not break;
6. **the falsification variant, which is the point of the spike**: the host's *calling* method holds a
   uniquely-referenced object — reachable from no static, no other frame — **across** the call to the
   patched method, and a second thread allocates continuously so collections happen while the patched
   method and our helper are on the stack. After the call the host verifies the object's field is
   unchanged. If the runtime's walk stops at our native return address and skips the caller's frame,
   that object is collected and the field is corrupted or the process dies — which is exactly the
   hypothesis §3 states and the measurement that decides it. A variant that survives without a `GC`
   having occurred has measured nothing, so the run asserts the collection count is non-zero.

If §4.6 passes, Phase 2 is built the managed-helper way. If it fails, the failure is written down with
its evidence, and the fallback is the jump detour of §3 (managed replacement + managed continuation),
tested by the same spike with the same forced-collection variant.

### 4.1 What the harness does **not** have yet, and the plumbing the spike therefore needs

Reconnaissance of `hook\tools\Test-Injection.ps1` (839 lines, six runs, all inline blocks) found that
the spike cannot be written as "just another run", because **there is no way to give the synthetic
host a target of its own**. Three separate gaps, each with a file:

1. **The target table is baked in at build time and only ever describes the game.**
   `known-build.json` → `tools\gen_target_table.py` → `GeneratedBuildTable.cs` → `PublishTargets()`
   (`StartupHook.cs:1127-1172`) → the shared block → `LoadTargetsFromShared()`
   (`DrelayInstall.cpp:253-277`). There is **no env var, no file and no launcher option** that adds an
   entry; `--assembly` only chooses which assembly has its MVID checked and is reflected over. So
   `MANAGED-ENTRY-HANDOFF.md` §4 step 1's claim that a target entry "can be added to the host's own
   build record, which is what the existing runs already do" is **wrong** — the existing runs add
   nothing, and no run has ever patched a synthetic-host method. Adding the host's method to
   `known-build.json` is not the fix either: that file is the Phase 0 gate's pinned record, and
   `Fingerprint.ps1` checks exactly four target hashes (`tools\il_index.py` regenerates them from the
   game assembly and would drop any extra key).
   **The plumbing**: a launcher option → `DRELAY_HOOK_TEST_TARGETS` (the launcher deletes inherited
   `DRELAY_HOOK_*` and sets its own, `Program.cs:448-458`) → appended by `PublishTargets()` in the
   bootstrap, clearly marked test-only and gated on the variable being present. `DRELAY_MAX_TARGETS`
   is 8 and only 4 are used, so there are slots to spare.
2. **The native patch dispatch is a bare string compare** — `if (strcmp(t->name, "frameTick") == 0)`
   (`DrelayFrame.cpp:468-471`); every other name logs "verified … and NOT patched". Naming the host's
   cadence method `frameTick` reuses the existing body *and* the existing signature
   (`void(void*, float)`), which is why the spike should do exactly that rather than add a second
   dispatch: it exercises the real install path instead of a test-only one.
3. **The host has no cadence method and no IL hash.** `SyntheticHost.cs` has `Main`, one thread
   lambda, and the three stand-in types; nothing is called repeatedly. The method the spike adds must
   be `NoInlining`, take a `float` (plus a `ref`/`out` parameter), count its own body executions, and
   have its IL hash computed for the target entry — by the test, from the built host DLL, using the
   same recipe as `il_index.py` (`sha256` of `MethodDef.Rva`'s exact IL bytes; no header, no
   re-encoding).

Also worth having in hand for the same run: `Test-Injection.ps1` never keeps its evidence by default
(it writes under `hook\obj\injection-test\` and **deletes the tree** unless `-Keep`), so a run 7 that
matters must be invoked with `-Keep` or copy its evidence out — and `PHASE1-VERIFICATION.md:41-43`'s
statement that the test writes into `hook\obj\live-runs\` is wrong on the same point.

### 4.2 The three things run 7 has already established, and what step 2 adds

Run 7 exists and passes: `Test-Injection.ps1 -Keep` reports **90 check(s): 90 passed, 0 failed** on
2026-10-12 (73 before this round). It publishes the host's stand-in frame tick
(`SyntheticHostFixture.Cadence::FrameTick`, IL size 23) through the launcher's `--test-target`, and
the run of 2026-10-12 shows:

```
target table: 5 entries from the bootstrap
target 'frameTick' found: IL 23 bytes, native 10 bytes at 00007FFF99C9AC40 (token 0x06000002, 1/5)
PATCHED 'frameTick' at 00007FFF99C9AC40 (trampoline 00007FFF99B30FC0)
PASS 9  frame tick entered 341 times, worst call 117 us
PASS 9b the JIT-reported address is an executing address (the detour is being entered)
PASS 9c live world read: 341 successful frame read(s), 0 failed, latest Player.Position.X = 12.5000
self_observed_calls=1491  patched_body_calls=1491  witness_intact=1  allocations=207042
```

So the target table, IL-hash matching, the patch, the trampoline continuation, the native frame read
against a live (host) world, and the caller-frame witness all work — and the `GAP 9c` that runs 1-6
could only report is closed.

**One thing that run had to be taught, and it is a trap for Phase 2 rather than a detail of the
fixture.** Installing the detour and *matching* targets are two different states: the native side
waits `--collect-delay` ms after the bootstrap releases `Main` before opening its watching window,
and a method compiled in between is, in the hook's own words, "not lost, only unwatched". With
tiering off a method is JITted exactly once, so a host that calls its stand-in frame tick
immediately compiles it inside the unwatched interval and can never be patched — which is what run 7
did the first time it was written, reporting `0 of 5 target(s) patched` while its cadence loop ran
1 488 times around the miss. `DRELAY_TEST_CADENCE_DELAY_MS` is the fix here. **The same trap applies
to the game**: a bullet's `ProcessProjectileData` compiled during the collection delay would be
unwatchable for the rest of the session, and Phase 2 is the phase that must notice rather than
mislabel it as "the game never ran that code" — see §6.2.

**Step 2, which is what actually settles §3.** Add, in three places:

1. **a shared-block field** holding the managed helper's entry address. Zero-size-impact route: add
   `volatile uint64_t managedFrameHelper;` immediately before `reserved[DRELAY_SHARED_RESERVE]` and
   reduce that `#define` by 8, which is what the reserve is for; `sizeof(DrelayShared)` and every
   existing offset stay put, so `DRELAY_SHARED_VERSION` and the five drift checks of
   `DrelayHookShared.h:553-557`, `build.ps1`'s `SizeProbe.c` cross-check, `SharedLayout.VerifyOffsets`,
   the bootstrap's map-time check and `DrelayMain.cpp:335-341` all keep passing unchanged;
2. **a plain managed helper in the bootstrap** — a `static` method with an all-primitive signature,
   **not** `[UnmanagedCallersOnly]`, whose address is taken with
   `RuntimeHelpers.PrepareMethod(...)` + `RuntimeMethodHandle.GetFunctionPointer()` and published
   beside `managedResolveHook`. The helper must **allocate** (so it, and not some other thread, is
   what triggers the collection) and must **call another managed method** (so it is not a leaf);
3. **a call to it from `FrameTickDetour`**, guarded by the same arm state the read uses, with the
   return value recorded somewhere the evidence writer can print.

The assertions that matter are the ones run 7 already has, plus: the helper was entered once per frame
call, the value it returns matches what the native side passed, and — the whole point — the host's
caller-frame witness is still intact **with the helper allocating on the stack**. Run 7's witness
check passes today, but the detour allocates nothing, so today it measures the trampoline route and
not the helper route. Step 2 is what gives that assertion teeth.

### 4.3 Step 2 ran. The route works, under two rules — and they were found by breaking them

Step 2 is built: `--frame-helper` on the launcher, `DRELAY_HOOK_FRAME_HELPER` to the hook,
`managedFrameHelper` in the shared block, `StartupHook.cs::FrameHelperProbe` published with
`PrepareMethod` + `GetFunctionPointer`, called from `FrameTickDetour` every frame, with the return
value compared against what the native side predicts from the arguments it passed. The verification
of run 7's target matching is [§4.2](#42-the-three-things-run-7-has-already-established-and-what-step-2-adds);
this section is what the mechanism itself turned out to be.

**Every run below is the synthetic host with that target patched, a caller-frame witness held across
every call by the cadence loop, and a second thread allocating continuously.** "Forced" means that
thread also called `GC.Collect(2, Forced, blocking: true, compacting: true)` every 64 ms. Evidence for
all of them is in `hook\obj\cases\phase2-mechanism\`.

| run | helper on the detour | call path pre-compiled | allocates or logs | forced full collections | outcome |
|---|---|---|---|---|---|
| **G** | none | – | – | **647** | clean |
| **A** | yes | no | yes | none (ordinary only) | clean |
| **H** | yes | no | **no** | **647** | clean |
| **B** | yes | no | yes, and forcing its own | forced | **crash** — `gc_heap::plan_phase` |
| **F** | yes | no | yes | **647** | **crash** — `gc_heap::drain_mark_queue` |
| **8a** | yes, with a virtual call | **no** | no | 405 | **corrupted** the host's own objects, on the first patched frame |
| **W** | yes, with a virtual call | **yes** | no | 647, 647, 647, then one more | **clean three times, crashed on the fourth** — `gc_heap::drain_mark_queue` |
| **X × 2** | yes | **yes** | **yes** | 647, 647 | **corrupted** once, **crashed** once |

**Read the W row first, because it is the one that decides the question.** The configuration that had
been clean three times in a row crashed on its fourth run, at the same address as run F, with nothing
changed: warmed, non-allocating, the same 647 forced full compacting collections. **This route is not
safe and it is not reliably unsafe — it is flaky**, and that is a worse property than either, because
it is the one that produces a hook which works in testing and corrupts a live session once an hour.
Everything below is what the controls established along the way, and it is worth reading as the
anatomy of a mechanism that looked sound at every step.

**Rule 1 — every managed method on the detour's path must already be compiled.** Run 8a and runs W
differ in exactly one thing: whether `PublishFrameHelper` pre-compiles the call path (and warms the
virtual dispatch once) before the patch goes live. 8a corrupted the host's `Cadence` object and its
witness on the *first* patched frame — `patched_body_calls` read back as `2024506658377`, a value no
arithmetic in that loop can produce — and three warmed runs of the same scenario were clean. The
mechanism is the obvious one once the evidence is in front of you: a method that has never run is
compiled the first time it is called, and compilation allocates, takes the runtime's JIT lock and can
collect. Phase 1 had already written this lesson down — its own comment on the patch path records
that the detour "needed both a pre-compile and an explicit probe call here" — and this is the same
lesson found again from the other end. `PrepareMethod` is not enough on its own: it covers the method
named, not the methods that method calls, so the rehearsal's dispatch and checksum are prepared and
exercised explicitly. The rule is real; **it is just not sufficient** — the fourth W run obeyed it and
still died.

**Rule 2 — nothing on that path may allocate.** Runs W and X differ in exactly one thing:
`DRELAY_FRAME_HELPER_NO_ALLOC`, which drops the helper's 64 KB per-frame array *and* its
`AppendLog`/`FlushLog` progress line. Before the fourth W run, the split was clean: W three times
clean under 647 full compacting collections, X failing both times it ran — once by corrupting the
caller's objects and once by dying in the runtime. The honest reading is the one the switch can
support: **the difference is "allocating or logging" versus "neither"**, and those two have not been
separated. That distinction decides how wide the prohibition is: a rule about the 64 KB array is a
rule about one method, whereas a rule about logging is a rule about every convenient diagnostic
anyone will ever want to add here — and logging allocates strings, takes a lock and writes a file, all
from the detour's stack.

**The consequence is heavier than it looks, so it is worth stating plainly.** "Allocates nothing" is
not a property a reader can check by looking: a boxing conversion, a `foreach` over a `List<T>`, a
string interpolation, a lambda capture and a `catch` block that constructs an exception are each one
line away and each allocates. It also means the path must be **throw-free**, since throwing
allocates. And even then the fourth W run shows the discipline is not sufficient.

**The verdict.**

- The plain-call route **worked** for a virtual dispatch with two `out` parameters — `ProjDesc.GetPosition`'s
  shape — three times under 647 full blocking compacting collections, and then failed on the fourth
  with nothing changed. **A mechanism that fails one run in four under a stress the game will reach on
  its own is not a foundation**, and Phase 2 does not get to rerun a live session until it is green.
- The crash is always in the GC's mark phase, and the reproduced corruption is always of objects
  reachable only from frames older than the detour. That is consistent with the runtime being unable
  to unwind a managed stack that a native frame interrupts without a transition marker — which is the
  original diagnosis, and which no amount of discipline *inside* the helper can fix, because the
  problem is the frame the helper's caller does not have.
- **And it is not confined to the forced-collection regime.** The suite run of 2026-10-12 had run 7
  launch with `--frame-helper` — ordinary collections only, the configuration that had passed a dozen
  times — and the host's witness came back corrupted, which killed the run before the hook wrote its
  evidence and cost eight assertions. The helper was therefore removed from run 7 (see the note in
  `hook\tools\Test-Injection.ps1`), because a test whose subject is a coin flip teaches its reader to
  rerun it.
- **The jump detour into a managed replacement is therefore the design, not the fallback**
  ([MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md) §3). There the replacement is entered as a
  managed call from the game's own managed caller, so no native frame interrupts the managed chain and
  neither rule has to hold. It is more work, and the continuation into the original has its own
  hazard, but it is the only one of the two candidates whose stack the runtime can actually walk.

**The gate, and why there is not one.** This was written as run 8 of `hook\tools\Test-Injection.ps1`,
asserting that the W configuration survives and the X configuration does not. It was **removed again**
after the fourth W run crashed, and the reason is worth keeping: a gate that is flaky against a real
fault is worse than no gate, because it teaches its reader to rerun until it is green. What survives
is the instrument — `--frame-helper`, `DRELAY_FRAME_HELPER_NO_ALLOC`, `DRELAY_FRAME_HELPER_VIRTUAL`,
`DRELAY_FRAME_HELPER_VIRTUAL_REPEAT`, `DRELAY_TEST_FORCE_GC_MS` — and the case directory
`hook\obj\cases\phase2-mechanism\`, so the regime can be re-run by hand the moment it is needed again.

**The first experiments the next round should run**, in the order they matter: (1) the managed
replacement, which is now the critical path for the whole phase; (2) if it is wanted for the record,
separate "the 64 KB array" from "logging" inside the X configuration, because it says how much of this
route's failure was avoidable — it no longer changes the decision.

### 4.4 Step 3: the managed replacement works, and it is the mechanism

§4.3 sent the plain-call route to the bench. §3's jump-detour candidate was built instead, and the
measurements are the mirror image of the ones above.

**The shape.** MinHook's `jmp` at `frameTick`'s entry is aimed at a **managed method**,
`HookEntry::FrameTickReplacement(IntPtr self, float total)`, instead of at the native detour. On x64
the argument registers line up with the instance method's own ABI — `this` in RCX, the float in XMM1
— so the game's own `call` arrives with the arguments where the replacement expects them, and
**nothing native is in the managed chain**. The replacement then continues into the real method by
calling MinHook's trampoline through a **managed** function pointer:

```csharp
((delegate* managed<IntPtr, float, void>)(IntPtr)trampoline)(self, total);
```

`delegate* unmanaged` there would be a managed-to-native transition, which would put a transition
frame and a preemptive thread state under the original body — reintroducing exactly the interruption
this design removes. `delegate* managed` is emitted as a plain `call` with no transition, and because
the trampoline is the original prologue followed by a jump into the body, the stack at the body's
first instruction is indistinguishable from a direct managed call. Two shared-block fields carry it:
`managedFrameReplacement` (published by the bootstrap on the resolve callback) and
`managedFrameTrampoline` (published by the native side after `MH_CreateHook` and **before**
`MH_EnableHook` — the first `call` arrives as soon as the hook is enabled, and a replacement that read
a zero trampoline would have to skip the original, which stops the game's frame update).

**The result.** Seven runs on the synthetic host, all with the target patched, the caller-frame witness
held across every call, and **650–651 forced full blocking compacting collections** — the identical
stress under which the helper route failed:

| runs | allocation on the patched path | outcome |
|---|---|---|
| `repl-first`, `repl-A1`, `repl-A2`, `repl-A3` | no | **clean**, `patched_body_calls == self_observed_calls` (≈1 500), witness intact |
| `repl-B1`, `repl-B2` | **yes** — 64 KB per frame | **clean**, same counts, witness intact, no `crash.txt` |

All seven are in `hook\obj\cases\phase2-mechanism\` as `repl-*`, and the first of them was entered
from the host's own call (`MANAGED REPLACEMENT entered (call 1): total=26117.137`) — which matters,
because a hook that never fired would leave the counters agreeing just as happily. That is the control
the helper route could not pass: it died about one run in four under this stress, and never survived it
while allocating.

**Why the two results differ, in one sentence.** The helper route's managed frame had a native caller,
so the runtime could not unwind past it; the replacement's managed frames are entered and left by
managed calls only, so there is nothing to unwind past. The remaining native frame — MinHook's
trampoline — is never a *caller* of managed code in the walkable direction: the runtime sees the
original body's return address inside the replacement.

**What this does not yet cover, and Phase 2 must.** The replacement replaces the native detour, so the
native frame read and its ring are not in the path any more: a run in this mode reports
`0 frame(s)` and the self-check's read checks are not made. That is not a defect to fix in the
replacement — it is the work Phase 2 does next: 2.1–2.3 become ordinary C# on the game's thread, with
`ProjDesc.GetPosition`, the pooled projectile set and the collision log all reached as managed calls
from inside the replacement. The reliability figure is also n=7: it is enough to choose the mechanism
over one that fails one run in four, and not enough to stop measuring — run 8 of
`hook\tools\Test-Injection.ps1` re-runs this configuration on every invocation, with allocation ON,
so a regression shows up as a failing suite rather than as a live crash.

**Both instruments stay.** `--frame-helper` and its switches are still in the tree, still off by
default, and still the thing to run if a future question needs a managed call *underneath* a native
frame; `--frame-replacement` is the route the sensor is built on.

### 4.5 The sensor's rules are code and are tested on every launch; the live set is not a list

With the mechanism settled, the next work is 2.1's bookkeeping and 2.2's gate — the two parts of the
sensor that are pure functions of plain data, and therefore the two that can be proven before anything
reads the game.

**Built, in `hook\managed\Drelay.Bootstrap\`:**

- **`ThreatSet.cs`** — records keyed on `(GameId, startTime)`, the two-strike retirement with a
  min-age grace, an explicit `Invalidate` for an observed re-rent, and `InvalidateAll` for a zone
  change. The four rules of [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §3.3 are implemented with the
  asymmetry that matters most: **a frame whose enumeration failed reconciles nothing**, so a read that
  did not happen charges no miss and cannot retire a live bullet. Plus `LinearityGate.IsLinear` — the
  only place a trajectory may be projected past its trace, with the reference engine's three
  conditions: at least three samples, non-zero span, and every interior sample within tolerance of the
  endpoint chord.
- **`SensorSelfTest.cs`** — **fifteen assertions**, run on every launch on the resolve callback:
  T4a–T4g for the retirement rules (two strikes, the grace, the reset on return, and the failed-read
  rule), T3a–T3c for the pooling hazard (a re-rented object cannot inherit, an unknown key is a no-op),
  and five for the gate including the tolerance boundary and the two-point case. Each runs inside its
  own guard, so a self-test cannot take the hook down.

**And it is a test, not a rubber stamp — which is checked.** The rules are read from
`DRELAY_SENSOR_RETIRE_MISSES` / `DRELAY_SENSOR_MIN_AGE`, so the suite can configure a knowingly wrong
one and require the self-test to notice. Run 7 asserts the default reports `15 assertion(s), 15
passed, 0 failed`; **run 9 launches with `DRELAY_SENSOR_RETIRE_MISSES=1`** — one strike instead of
two, so a single incomplete read retires a live bullet — and asserts the self-test names *that*
assertion and reports the failures in its summary. Measured on 2026-10-12: with the broken rule, five
of the fifteen fail, and `T4b one absence does not retire a live bullet` is one of them. The same
reasoning as `tools\Test-FingerprintGate.ps1`: prove the gate can refuse, not that it can accept.

**One design correction came out of the same round, and it changes 2.1's read path.**
[CLIENT-MAP.md](CLIENT-MAP.md) §5.2 is explicit: **there is no `List<Projectile>` and no projectile
dictionary on `World`.** A projectile is an ordinary world node — `AddObject` puts it in
`World.Shared.Objects` (a `Dictionary<int, GameObject>`) and into a layer — so the live set is
`Objects`, filtered by `is Projectile`, and the recommended paths are cheapest-first: `Player` (one
dereference), `GetEnemies()` (a cached array), then `Objects` with a managed type check per entry.
**`Allocations.Projectiles` must not be enumerated**: it is a `Pool<Projectile>` free list of recycled
instances, and reading it as "live projectiles" would produce a threat set of dead objects.

> **CORRECTION (§4.14, 2026-10-12 evening).** The middle claim above is **wrong and this section is
> kept as written so the correction has something to point at.** A projectile is not put into
> `World.Objects`: it is rented and handed to `World.GameObjectLayer.AddChild`. The live set is
> `World.GameObjectLayer.Children`, filtered by `is Projectile`, and a sensor reading the dictionary
> reads zero bullets — which is what the first session to reach a zone measured, over 54 600 frames.
> Everything below about the *dictionary walk* applies to the layer walk, which is what the code now
> does.

That means the frame path's work is a **dictionary walk with a per-entry type test**, and the type
test is the same technique the native frame read already uses for `Player`: compare the object's
MethodTable against a type handle the bootstrap publishes by reflection, masking the low three mark
bits. What has to be resolved and published next is therefore: `World.Objects`, the dictionary's
entries array and count, the entry's value slot, and the `Projectile` type handle plus its
`GameId`/`StartTime`/`Position`/`Radius` offsets — all of which is one more pass of the reflection the
bootstrap already does for `World.Shared` and `Player.Position`.

### 4.6 The sensor reads the world, and the read is measured

`SensorRead.cs` is the frame path's view of the world: it walks the game's own live-object
dictionary once per frame, picks out the projectiles, and feeds their identities to the `ThreatSet`
from §4.5. Run 10 of `hook\tools\Test-Injection.ps1` exercises it against the synthetic host, whose own
counters are the independent witness — it spawns, retires and re-rents occupants and reports what it
holds, so the hook's numbers are checked against something that is not the hook.

```
[INFO] boot: sensor read path resolved: World.Objects is Dictionary`2, projectiles are DarzasDominion.Darza.Game.Objects.Projectile
[INFO] boot: sensor: frame 150: live=20 other=2 tracked=20 retired=15 unreconciled=1 frames_read=149 cost=7us   max=1127us
[INFO] boot: sensor: frame 600: live=20 other=2 tracked=20 retired=59 unreconciled=2 frames_read=598 cost=11us  max=1115us
host, at exit: live_projectiles=20  live_others=2  spawned=163  retired=143  reused=30
```

`live=20` against the host's `live_projectiles=20`; **`other=2` against `live_others=2`**, which is the
assertion that matters most, because `Objects` is shared with static and 3d objects and a filter that
does not filter would report everything as a bullet; `tracked=20` covers `live`; `retired` climbs
15 → 59 over the window while the host is retiring 143 objects and re-renting 30, so retirement is
running against a set that actually changes. `unreconciled` is 1 or 2 — the frames before the world and
the character existed — which is the "a read that did not happen is not an absence" path doing its job.

**Three things about this are worth stating rather than leaving in the code.**

**The cost is 7–16 µs per frame over 22 occupants**, against a frame-tick budget of ≤150 µs
(HOOK-ARCHITECTURE.md §7). That is the reflection-based read, chosen deliberately for the first
version: Phase 2's job is to produce four numbers, one of which is this one, and a reflection walk
produces a correct warning distribution, correct false-negative and false-positive rates and an honest
cost figure without first betting the phase on a hand-computed offset for a private nested struct
inside `Dictionary<int, GameObject>`. The number above is what would justify replacing it with a raw
walk, and it currently says there is no need.

**The fixture had to be corrected to match the game, not the other way round.** The host's first
`Projectile` was declared in `...Game.Objects.Entities`, next to `Player`; the run failed with
`types not found: World=True Projectile=False`, because `known-build.json` pins the collision and spawn
targets as `DarzasDominion.Darza.Game.Objects.Projectile`. A host in a tidier namespace would have
tested a hook that cannot find the real one, so the namespace is now part of the fixture and says so.

**A run with the sensor is a replacement run**, so the native frame read and its ring are not in the
path — the sensor is the thing reading the world. The two are separate runs in the suite for that
reason, and neither is a superset of the other.

What remains for 2.1 is the *spawn* half: `DODGE-ALGORITHM.md` §3.2 wants a record created at the
spawn hook, reading arguments only, so a bullet is known before the frame path's enumeration can see
it. It is worth noting that the frame-tick read above already supplies everything the sampler needs —
identity, live position, radius, and the descriptor — so the spawn hook's value is earlier arrival
rather than a missing input, and 2.2 and 2.3 can be built on what exists.

**And "earlier arrival" turns out to be worth less than it sounds, so this is where the half is
deliberately left — with the reasoning written down rather than implied.**

`ProcessProjectileData` is the handler that *creates* the projectile, and the object enters
`World.Objects` in that same call. The frame tick runs later in the same frame and enumerates
`World.Objects`, so it sees the same bullet in the same frame. The spawn hook's advantage is therefore
**at most one frame — about 17 ms — and only when the frame tick happens to run before the spawn in a
given frame's ordering.** Against that:

* **A 16-argument native ABI is the highest-risk thing in this phase.** Every argument's managed type
  has to be exactly right, and a wrong one is not an exception, it is a corrupted stack. The
  `GetPosition` signature check earned its place by catching a `bool`-versus-`void` difference on a
  **six**-argument method against the shipped client; sixteen arguments is that risk with more than
  double the surface, for a frame.
* **The frame path is also the one that cannot go stale.** It re-reads live values every frame, and
  the threat set's two-strike retirement already tolerates a missed frame by design (§4.5). A spawn
  record is a second source of the same truth, and two sources disagreeing is a new failure mode, not
  a stronger one.

**What would change the decision**, stated so a later reader can tell whether it has: a live run in
which the collision log records a hit whose bullet the frame path never saw (a false negative with no
matching observation), or a Phase 3 requirement that the escape be dispatched inside the same frame the
bullet spawns. Neither has happened, and the second is a Phase 3 measurement, not a Phase 2 guess.

**And there is a harder reason it is not built yet: it could not be verified.** A 16-argument hook
needs a witness that calls it with the real argument shapes, and there are only two candidates. The
synthetic host does not have a `ProcessProjectileData`; giving it one means inventing sixteen
parameters, and a fixture built from my own assumption about a signature tests the assumption rather
than the signature — precisely the failure mode the host's `HitCharacter` was written to avoid, where
the fixture mirrors `known-build.json` instead of my guesses. The other candidate is the client itself,
and the client cannot be taken into a zone without a person. **So the spawn half is not deferred for
effort; it is deferred because there is nothing to check it against**, and building an unverifiable
native-ABI hook is the opposite of what the rest of this phase does.



The plan's Phase 2 deliverables, restated against whichever mechanism §3 selects. Nothing here
changes their content; it changes where the code lives.

| plan deliverable | managed mechanism | native fallback |
|---|---|---|
| **2.1 threat set** — spawn record, per-frame re-read, two-strike retirement, pool-reuse invalidation (T3) | managed replacement on `spawn` and on `frameTick`; the record is plain C# state | native ABI decode of `ProcessProjectileData`'s 16 arguments; raw-offset walk of the pooled set |
| **2.2 trajectory sampler** — resolve `ProjDesc.GetPosition`'s concrete slot once, 30 ms grid to the horizon, anchored on the live position, linearity gate | `ProjDesc.GetPosition` is an ordinary virtual call | **degraded**: a linear model with its curvature error measured and the degradation stated in the log, not hidden |
| **2.3 ground-truth comparison** — the collision log | managed replacement on `collision`; the comparison is written where it is made | native detour body reading `this` and the player position |

The order stays as the plan has it, with one insertion: **§6's instrumentation first**, because 2.3's
output is only interpretable if the cost numbers have a distribution.

### 4.7 The sampler calls the game's own trajectory, and the straight fraction is measured

`TrajectorySampler.cs` is Deliverable 2.2. It calls
`ProjDesc.GetPosition(int, float, float, out float, out float, int)` — virtual, one override per
trajectory family — twenty-one times per bullet per frame, and runs each polyline through the
linearity gate.

**How a hook with no compile-time reference to the game calls a virtual method.** `MethodInfo.Invoke`
would work and is unusable: it boxes both `out` floats, allocates an argument array and re-validates
the signature on every one of twenty-one samples per bullet. So the call is emitted **once**, as a
`DynamicMethod` that does a single `callvirt` and stores the two results into a caller-owned `float[2]`
— the same technique `StartupHook.cs` already uses for `ldsflda`/`ldflda`, for the same reason: the
runtime can answer a question about a type the hook cannot name, but only if it is asked once.
`callvirt` and not `call`, because the method looked up is the base `ProjDesc`'s and virtual dispatch
is what reaches the concrete family. **The signature is checked rather than assumed** — six
parameters, the right types, `out float` by-ref, `void` return — because a `callvirt` emitted against a
signature that has moved would not fail loudly; it would read the wrong slots and produce plausible
coordinates.

**Sampling** follows §4.2 and §4.4: a fixed 30 ms step to a 600 ms horizon (21 points, one at the live
anchor), and **every sample set translated by `live − predicted(now)`** before anything looks at it, so
model error is relative rather than cumulative.

**And the gate's answer is the measurement.** Run 10's configuration, from the 2026-10-12 run:

```
host, at exit:  live_projectiles=20   live_straight=14   live_wavy=6     -> 0.700
sampler: frame 600: sampled=20 straight=14 curved=6 | run: 11860 bullet(s), straight fraction 0.683, cost=17us
```

The host decides each projectile's family by construction, so it knows its own mix and the hook only
measures it. **0.683 against 0.700** — and the assertion is the one with teeth, because a sampler that
was not really dispatching (one running a base body and predicting every bullet as straight) would
report 1.0 on a set that is a third weaves. That is the silent failure `callvirt` exists to avoid, and
this line is where it would show.

**One instrument defect was found by this work, and fixed in it.** The first version timed the read and
the sampler as a single window and reported the read's cost as 300–1200 µs — against 7–16 µs for the
read alone. What that number actually contained was the read, plus the sampler, plus the gen0
collections the sampler's own boxing triggers, all attributed to the read. The two windows are now
separate, and so are the numbers: **read 7–14 µs, sampler 13–17 µs** per frame, about 20–31 µs together
against the 150 µs frame-tick budget. Two costs reported separately are measurements; one number
containing both is a number nobody can act on.

**The cost driver is the reflection, and it is now named rather than guessed**: `FieldInfo.GetValue`
boxes a `float` or a `GamePoint` on every call, so a 20-bullet frame allocates about eighty small
objects and the collections they trigger are real frame time. That is the evidence that would justify
replacing the field reads with the measured raw walk — and the numbers above say there is no need yet.

### 4.9 The four numbers, published — and one correction that stops them being the *live* numbers yet

**The report.** `Phase2Report.cs` prints the phase's four decision numbers on one line, every few
hundred frames, from bucket histograms rather than a worst case:

```
PHASE 2 NUMBERS (synthetic host; a live game run is what these are for)
  | warnings: N predicted hit(s), mean X ms
  | false negatives: N (hit, not predicted)
  | false positives: N (predicted, retired without a hit)
  | cost: read n=… p50<=…us p99<=…us, sampler n=… p50<=…us p99<=…us
```

Three things about that are deliberate. **They are printed together**, because a reader who has to
assemble the answer from four places in a log will assemble it wrongly — the lesson Phase 1 wrote down
after `logOverflow` and `logAppendFailures` were briefly the same counter. **The cost is a percentile
from a histogram**, because a p99 cannot be recovered from a maximum: Phase 1's endurance run kept a
cumulative worst case, its ring overflowed, and the single 323 µs sample it recorded could not be
placed afterwards (PHASE1-VERIFICATION.md §11.4). And **a bucket percentile is reported as an upper
bound** — "p99 ≤ 20 µs" is what the structure can support, and "p99 = 17 µs" would be a number it
cannot.

**And the correction, which is the more important half of this section — including a correction to
this document.** The sensor resolves its fields from the loaded assembly *by name*, and the names it
used were the synthetic host's. Read out of the IL dump for build 5.8.2, the real
`Projectile` is `DarzasDominion.Darza.Game.Objects.Projectile : ...Game.Objects.GameObject :
DarzasDominion.Nodes.Node`, and the fields the sensor needs are spread across all three:

| what | where | name and type |
|---|---|---|
| identity | `GameObject` | `public int32 GameId` |
| start time | `Projectile` | `private int32 _startTime` |
| direction | `Projectile` | `private float32 _angleSin`, `_angleCos` (and `public float32 Angle`) |
| trajectory | `Projectile` | `public DarzaCore.Data.ProjDesc Desc` |
| position | `Nodes.Node` | `public Vector3 Position` (also `ExactPosition`) |
| radius | `GameObject` | `public float32 Radius` |

**An earlier version of this section said "there is no `GameId` field on `Projectile`, and none
anywhere in the assembly", and that was wrong.** `GameId` is a public field on the base
`GameObject`; the search that missed it was restricted to the `Projectile` class's own line range, and
the follow-up whole-file grep used a range that did not include `GameObject`'s declaration four
thousand lines earlier. `Type.GetField` has the same trap from the other side — it walks base classes
for public instance fields but not for private ones — so `FieldLookup.cs` now walks the hierarchy
explicitly and **reports which type and name each field was found under**:

```
sensor read path resolved: Objects is Dictionary`2, projectiles are ...Projectile (Projectile : GameObject),
                           identity Projectile.GameId + Projectile.StartTime
trajectory sampler resolved: fields Projectile.Desc, Projectile.Sin, Projectile.Cos, Projectile.AgeMs,
                             Projectile.Position(GamePoint.X/GamePoint.Y), Projectile.Radius
```

That line is the answer to "why did the sensor not come up on the real client": it names every field,
in the form the lookup found it, so a missing one is visible as a missing *name* rather than as a
silent absence. The candidate lists cover both spellings (`_startTime` / `StartTime`, `_angleSin` /
`Sin`, `Position` / `ExactPosition`, `Radius` / `TouchRadius`, and so on), so the same build resolves
against the host *and* against the client, and the log says which it found.

**What is genuinely still open, and it is now one specific thing rather than a wall.** The sampler
needs *milliseconds since this bullet spawned* to call `GetPosition(time, ...)`, and no such field
exists on the client: `_startTime` is a game-clock value, and the client's own age computation is
`time.LogicTotal − _startTime` (CLIENT-FINDINGS.md §4, `Projectile.cs:670`). The host has a synthetic
`AgeMs` for exactly this reason. So a live run additionally needs the world's logic clock resolved and
the age derived from it — one more field lookup and one subtraction, but it is a real piece of work and
it is the next one.

**What that means for the state of Phase 2** is unchanged by the correction and worth restating:
everything measured in §4.5–§4.8 is measured against the synthetic host, which verifies the
*mechanism* and the *rules* — retirement, sampling, the gate, the comparison and their self-tests do not
care which assembly the objects came from — and is **not** yet a verification against the client. The
numbers in the report above are the instrument working, not the phase's decision numbers.

### 4.10 Against the real client: it resolves, and the live run is now the only thing missing

The resolution work above was checked the only way it can be, by running the hook against the shipped
game. The game was launched through the launcher with `--runroot` inside the repository (a
harness-spawned child cannot create its run directory under `%LOCALAPPDATA%`), left at the title screen,
and killed; the evidence is in `hook\obj\live-runs\20261012-065927-p21968\`.

**What resolved — every field, by the hierarchy walk:**

```
sensor read path resolved: World.Objects is Dictionary`2, projectiles are
    DarzasDominion.Darza.Game.Objects.Projectile (Projectile : GameObject : Node),
    identity GameObject.GameId + Projectile._startTime
trajectory sampler resolved: DarzaCore.Data.ProjDesc.GetPosition called through a virtual dynamic
    method, 21 sample(s) every 30 ms to a 600 ms horizon; fields Projectile.Desc,
    Projectile._angleSin, Projectile._angleCos,
    DERIVED (DarzaWindow.Time.ClientTime.LogicTotal - Projectile._startTime),
    Node.Position(Vector3.X/Vector3.Y), GameObject.Radius
collision observation resolved
```

**And the replacement ran in the real process**: `MANAGED REPLACEMENT entered (call 1):
total=52016.047`. `frameTick` — IL 1286, token `0x06001FC5`, agreeing with Phase 0 — was matched and
patched with the **managed replacement**, so managed code of ours is executing on the real game's frame
thread. That is the prerequisite Phases 2, 3 and 4 all rest on, now confirmed outside the fixture.

**Two live findings, and both were found because the code refuses rather than guesses.**

1. **The real `ProjDesc.GetPosition` returns `bool`, not `void`.** DODGE-ALGORITHM.md §4.1 records the
   signature without saying; the shipped build is
   `Boolean (Int32,Single,Single,Single&,Single&,Int32)`. The signature check refused the call with both
   shapes named — which is exactly its job, because a `callvirt` emitted against the assumed shape
   would leave the return value on the evaluation stack, and an unbalanced stack does not fault at the
   call site; it faults somewhere else entirely, later. The emitted method now pops the boolean.
2. **The age check defeated its own fallback.** The run before this one reported the sampler
   unavailable with `Desc=True Sin=True Cos=True AgeMs=False Position=True` — every field but one, with
   the clock derivation written twelve lines above the check that rejected it. The check now accepts a
   missing age field when the derivation assembled.

**What is left is not code.** `sensor: frame 150: live=0 other=0 tracked=0 unreconciled=150` is the
correct answer at the title screen: there is no world yet, so the enumeration fails and — by the rule
§4.5 states — **no frame is reconciled and no miss is charged**. The next run needs a character in a
zone, which needs an operator. Everything the code can decide on its own has been decided.

### 4.11 The session runner, and the first criterion the instrument actually fails

`hook\tools\Run-Phase2Session.ps1` is the phase's session, in the shape Phase 1's acceptance runs
established: it launches the game with the sensor attached (`--frame-replacement`, with
`DRELAY_FRAME_SENSOR=1` — a plain `DRELAY_` name so the launcher's environment filter cannot eat it),
prints what to do, waits, closes the game, and then **reads the four numbers out of the evidence and
says whether they meet the criteria**. `-AnalyseOnly <runDir>` re-reads a session that has already been
played, which is also how the analysis is verified without a game.

**The first thing it did was expose an instrument that could not answer its own question.** The cost
histogram's edges were `{5, 10, 20, 50, 100, 200, 500, 1000, 2000}` and the criterion is the 150 µs
frame-tick slice, so the sampler's `p99 <= 200 us` was reported as a failure when the truth was that
the bucket spans the answer. **The budget points are now edges** — `{5, 10, 20, 50, 100, 150, 300, 500,
1000, 2000}`, the slice and the ceiling — and the criterion became decidable. That is the same lesson
as Phase 1's unplaceable 323 µs sample, one level up: a histogram whose buckets do not contain the
threshold answers a different question than the one being asked.

**And with it decidable, the sampler fails.** From the run of 2026-10-12:

```
read    n=2101 p50<=20us  p99<=100us     inside the slice
sampler n=2099 p50<=20us  p99<=300us     NOT inside the 150 us slice
```

The read is fine. The sampler's **p99 is in the 150–300 µs bucket**, against a criterion that asks for
a p99 inside the frame-tick slice with room for the solver. The cause is already named and measured: it
is the reflection's boxing (a `FieldInfo.GetValue` per field per bullet per frame boxes a `float` or a
`GamePoint`, so a 20-bullet frame allocates about eighty small objects) plus the 420 virtual calls a
frame. The fix is the one §4.7 anticipated — read the hot fields through offsets rather than
reflection, or cache the accessors — and it is now justified by a number rather than by a suspicion.

This is the phase working as designed. IMPLEMENTATION-PLAN.md's whole reason for putting Phase 2 before
Phase 4 is that a sensor which cannot predict collisions cheaply enough should stop the project
*before* the solver is built, and the session runner is what turns that from a sentiment into a verdict
line.

### 4.12 The tail is fixed, and the session now passes every check

The sampler's p99 was in the 150–300 µs bucket because of the reflection's boxing, and §4.11 named it.
`FieldAccessor.cs` removes it: for each field the resolver already found, a delegate is emitted **once**
on the resolve callback — `ldarg.0; castclass; ldfld; ret` — so the frame path reads a `float` or an
`int` with no `object` in between and no allocation. Nested value-type fields get their own shape
(`ldflda` for the inline struct, then `ldfld` for the member), which is what a position needs: reading
`Vector3` to get at its `X` is exactly the box being removed.

**A delegate rather than a measured offset, and the reason is the project's own rule.** An offset plus
an unsafe pointer read is smaller on paper, and HOOK-ARCHITECTURE.md §5.5 says offsets are CoreCLR's to
choose and must be measured per type per run — so an offset is a number that has to be re-derived, and a
wrong one is a wild read rather than an exception. The emitted `ldfld` has the JIT compute the offset
from the field token, exactly as a hand-written `obj.field` would.

**The result, on the same configuration that failed:**

```
before:  read p50<=20us p99<=100us   sampler p50<=20us p99<=300us    FAIL
after:   read p50<=10us p99<=50us    sampler p50<=20us p99<=50us     PASS
```

and the session runner's verdict went from `INCOMPLETE -- 9 passed, 1 failed` to
**`PHASE 2 SESSION OK -- 10 passed, 0 failed, 0 gap(s)`**, with the warning distribution (`9` predicted
hits, min 598.5 / p50 614.3 / p90 629.2 / max 632.0 ms), zero false negatives and zero false positives.

**Two instrument defects were found getting there, and both are the project's recurring lesson.**

1. **The one-time exception log was keyed on the wrong condition.** It fired only while
   `_framesUnreconciled == 0` — and frame one is *always* unreconciled, because the world does not
   exist yet. So every subsequent frame that threw was silent, and the only symptom was
   `live=0 unreconciled=N` with no reason attached. An instrument whose blind spot is exactly the
   failure it is consulted about, for the fourth time in this project. There are now two counters,
   `unreconciled` and `threw`, on every sensor line, because "there is nothing to read yet" and "the
   read threw" are different problems with different owners.
2. **The accessor delegates were emitted with two parameters instead of one** — my own bug, and it was
   found only because the exception log was changed to carry the **stack** rather than the exception
   type. `NullReferenceException` out of a dozen nested calls says nothing; `at
   DrelayField_Objects(World, Object)` says everything. A diagnostic that names its site is worth the
   characters.

And a fixture artifact was removed alongside them: the host's churn ran on its own thread and mutated
`World.Objects` under the hook's enumerator, making `MoveNext` throw `InvalidOperationException` — a
failure the real client cannot have, because there the dictionary is only ever touched by the frame
thread, which is where the hook is. The fixture now publishes a fresh dictionary per tick, so an
in-flight enumerator finishes on the old one.

### 4.13 The handoff: one command, and why it needs a person

Everything the code can decide on its own is decided. The phase's numbers come from **one run**:

```powershell
.\autododgeresearch\hook\tools\Run-Phase2Session.ps1 -Minutes 60
```

The script launches the game with the sensor attached, prints what to do, waits, closes the game, and
writes `hook\obj\cases\phase2-session\verdict.txt` with the four numbers and the criteria alongside
them. `-AnalyseOnly <runDir>` re-reads a session afterwards.

**It needs a person because the client's window cannot be seen, let alone driven.** That is measured,
not assumed: the client area renders **entirely black** to both `BitBlt` and `PrintWindow` with
`PW_RENDERFULLCONTENT` (the DirectX-capable path), so the menu's layout is not recoverable from the
outside — and clicking blind inside somebody's live game account is not a thing to do on a hunch. The
capture attempt is kept in `hook\tools\capture-window.ps1` so the next person does not repeat it, and
the window it produced is `hook\obj\cases\phase2-session\game-window.png`.

**What a session run looks like when it works**, from the menu-state run of 2026-10-12
(`hook\obj\live-runs\20261012-074644-p29644`, analysed into
`hook\obj\cases\phase2-session-live\verdict.txt`):

```
PASS  the read path resolved against this assembly
PASS  the trajectory sampler resolved          (fields ..., DERIVED (DarzaWindow.Time.ClientTime.LogicTotal - Projectile._startTime), ...)
PASS  the collision observation resolved
PASS  the managed replacement was entered by the game     MANAGED REPLACEMENT entered (call 1): total=51844.504
FAIL  collisions were resolved by the client   0 collision decision(s) observed
GAP   the sampler cost                         no sampler percentiles -- nothing was sampled, because there was no world
PASS  the read cost is inside the frame-tick slice  p50 <= 5 us, p99 <= 5 us over 3001 frame(s)
```

`PHASE 2 SESSION INCOMPLETE -- 6 passed, 1 failed, 1 gap(s)` at the menu is the **correct** verdict: the
sensor is up, the replacement is running inside the real client, and there is nothing to read yet
because `World.Shared` is null until a zone loads. The one run that has not happened is the one with a
character in it.

**Two attempts were made to reach that run without a person, and both are recorded because both are
answers rather than failures to try.**

*Driving the menu blind.* The client's frame loop is alive at the menu (3001 frames of the replacement
entering, and a live `total` clock), so the game is at a working UI rather than hung — which made
keystroke automation look plausible. `hook\tools\drive-window.ps1` takes the foreground properly (plain
`SetForegroundWindow` is refused for a process that is not already foreground; the working form attaches
the input queues first) and sends Enter. **It did not reach a zone**: `unreconciled` stayed at the frame
count, so `World.Shared` never became non-null. Mouse clicks at guessed coordinates were deliberately
not attempted — a blind click in somebody's live account can land anywhere, and there is no way to check
before it does.

*Telling the two empty sessions apart.* That attempt exposed a real gap in the analysis: "0 collisions
observed" is the correct verdict both for a session that fought for an hour and never got hit and for
one that never left the menu, and those need different responses. The session runner now reads the
sensor's own counters and says which it was — `frames_read=0` with `unreconciled` equal to the frame
count is the menu, and the verdict states it in those words, including why it cannot be driven from
outside. A session that was in the game has `frames_read` in the thousands.

**So the handoff is unchanged, and it is one command and a person:**

```powershell
.\autododgeresearch\hook\tools\Run-Phase2Session.ps1 -Minutes 60
```

### 4.14 The first session that reached a zone read nothing, and the reason was a wrong container

**The run.** `20261012-181843-p39540`, fifteen minutes of real play by the operator on the
evening of 2026-10-12, launched by `Run-Phase2Session.ps1`. It is the first session that
got past the menu: the sensor read a world on **54 599 of 54 600 frames**, and the game's
own log (`%LOCALAPPDATA%\RippleStudio\Darza\logs\darza.log`, 18:18–18:35) shows what the
player was doing — `Village`, then `LacunaCombat`, then `Overworld-2 | Overworld-3`, beams,
projectile lights, `Spawned 1 pending projectile(s) for entity 411542 (Rhinoceros)`, and a
`[HitchDigest] … 5.0 min in world, 17657 frames` line. **This was not an idle menu.**

**And the sensor saw nothing.** `live=0` on every frame, `tracked=0`, `retired=0`, zero
collision decisions, no sampler sample, and therefore zeros for all four of the phase's
numbers. The verdict the operator saw was
`PHASE 2 SESSION INCOMPLETE -- 7 passed, 1 failed, 1 gap(s)`, whose one FAIL was *"collisions
were resolved by the client during the session: 0 collision decision(s) observed"* and whose
one GAP was the sampler's cost, which had nothing to sample. **The instrument was behaving
correctly on the data it had; the data was the problem.**

Re-read by the corrected analyser — `hook\obj\cases\phase2-session-181843-reanalysed` — the
same evidence now reads `PHASE 2 SESSION INCOMPLETE -- 7 passed, 2 failed, 3 gap(s)`, and its
second FAIL is the one that names the actual defect:

```
FAIL  the sensor read projectiles from the world
      0 projectile(s) on the last sensor frame after 54599 frame(s) with a world. ...
```

The three GAPs are the fields this run's build did not yet publish (the report had no
`projectiles:` or `linearity:` fields), and they are reported as gaps rather than as passes
for the usual reason: a check that cannot be made is not a check that passed.

**The cause is a wrong container, and it is a correction to this project's own research.**
CLIENT-MAP.md §5.2 said that `World.AddObject` files a projectile in `World.Objects`, and
the sensor read exactly that dictionary and filtered it by `is Projectile`. The decompiled
client says otherwise, and the live run agrees with the decompile:

- `Entity.cs:1521,1550,1589` — a fired projectile is `Projectile.Rent(gameObjectData)` →
  `World.GameObjectLayer.AddChild(projectile)` → `projectile.World = World`. `AddObject`
  and `AddSortObject` are never called for it;
- the only method that writes `Objects[obj.GameId]` is
  `World.hehe_EB2C_E5A5_EE82_E9C5_EF78` (`World.cs:14553-14577`), and its five call sites
  are the map loader, the local player, the entity spawn paths and `SpawnDebugCharacter`;
- so `World.Objects` holds the map's static objects and the entities — which is exactly
  what the sensor counted as `other=200…400` — and **the live projectiles are in
  `World.GameObjectLayer.Children`**.

That also closes CLIENT-MAP.md §10.6, which asked for precisely this experiment, and it
answers it the opposite way from the guess recorded there ("which layer a projectile lands
in is `UNKNOWN - needs live measurement`").

**The fixture had the same wrong shape, which is why 90 checks passed.** `SyntheticHost`
put its projectiles in `World.Objects` too, so the hook and the host shared the assumption
and agreed with each other; the only `PHASE 2 SESSION OK` verdict in this phase is a
verdict about a world the game does not have. **A fixture that is wrong in the same way as
the hook is not evidence, and no amount of it is.** The host now has a `GameObjectLayer`,
its projectiles live there, `World.Objects` holds only the two static objects, and run 10
of the injection suite fails if the sensor goes back to reading the dictionary alone.

**And the new read path was checked against the shipped assembly before it was trusted.**
The sensor resolves its containers by name, so the correction is only as good as the names:
`hook\obj\cases\phase2-sensor-correction\check-layer-fields.py` asks the real
`DarzasDominion.dll` (MVID `CBF722C0-56DB-027D-6115-AA7A24CE5307`) the same question with
the project's own metadata reader, walking base types exactly as `FieldLookup.Find` does:

```
DarzasDominion.Darza.Game.World                     ... GameObjectLayer ...
DarzasDominion.Darza.Game.Objects.GameObjectLayer   <GrayScale>k__BackingField, _drawOnly, _staticList, _dynamicList ...
DarzasDominion.Nodes.Node                           Float_Tolerance, _children, _behindChildren ...
  -> resolved: DarzasDominion.Darza.Game.World::GameObjectLayer
  -> resolved: DarzasDominion.Nodes.Node::_children
LAYER FIELD RESOLVES: True  PROJECTILE FIELDS RESOLVE: True
```

So the next live run's `sensor read path resolved: ... the projectile source is
World.GameObjectLayer.Node._children` line is expected rather than hoped for — and if it
says instead that the layer did not resolve, the sensor says so in the same line and falls
back to the dictionary with a WARN.

**Three defects in the instrument came out of the same run**, and all three are the
project's recurring shape — an instrument whose blind spot is exactly the failure it is
consulted about:

1. **The four numbers stopped being published after 901 frames of 54 600, and the cause
   was in the report itself.** The report fired on `(_frameReplacementCalls % 300) == 0`
   and printed three lines (frames 300, 601, 901), then nothing, while the sensor's own
   lines — a `>=` against a counter nothing else touches — kept coming every 150 frames to
   the end. It happened at the moment the read's maximum jumped from 459 µs to **4 756 µs**,
   and that is the whole explanation: `CostHistogram.Describe()` wrote
   `Edges[Edges.Length]` where it meant `Edges[Edges.Length - 1]`, so the overflow
   annotation — the branch that only runs **when a sample lands above the last edge** —
   threw `IndexOutOfRangeException`, and the frame path's deliberate `catch` swallowed it
   silently. **The report died exactly on the runs that had an outlier worth reporting, and
   the session's verdict then quoted the stale 901-frame histogram (`p99 <= 100 us`) as the
   phase's per-frame cost.** It is fixed (the index, plus a self-test assertion that fills
   the overflow bucket on purpose so the guard cannot share the blind spot), the cadence is
   a countdown that cannot silently stop, and a report that does throw logs the exception
   and its stack once instead of vanishing. This is the third instance of the project's
   recurring shape — an instrument whose blind spot is the failure it is consulted about —
   and the first one where the instrument's own failure was the thing being looked for.
2. **`FAIL 9` — "the frame tick is patched but has never been entered" — fires on every
   managed-replacement run by construction.** In that mode the native detour is not
   installed, so its counter is zero however well the run went; the same run logged
   `MANAGED REPLACEMENT entered (call 1): total=48934.164` 54 599 times. The self-check now
   asks which path the frame read is and reports a **GAP with the reason**, not a failure
   about a detour that does not exist.
3. **"The client produced a world" passes on a session that read no bullets.** A world
   exists in the Nexus, and `World.Shared != null` is not evidence that the sensor has
   anything to look at. There is now a separate check — *"the sensor read projectiles from
   the world"* — that fails a session which had a world and never saw a bullet, because
   that session cannot produce a warning distribution, a false-negative rate or a
   false-positive rate no matter how long it ran.

**And the phase's fifth number moved to where it is read.** The report line now carries
the source split (`projectiles: N observation(s) from the layer's M node(s), K from
World.Objects`), the collision observation's own decision count, and the linearity counts
and fraction, so `warnings: 0` can no longer be read without the numbers that say whether
there was anything to warn about. The `LogSummary` methods on the three sensor classes were
dead code — the operator kills the game, so there is no orderly shutdown to call them from
— and they were **deleted** rather than left as a trap; every number they held is on the
periodic line.

**The cost of reading the layer, as far as the fixture can say.** CLIENT-MAP.md §10.6's
original worry was that `World.Objects` is O(all objects) per frame and that the layer might
be worse. Against the synthetic host — 20 bullets, 22 nodes in the layer — the read is
`p50 <= 20 us` with the p99 moving between the `<=150 us` and the `<=300 us` bucket from run
to run, and the sampler is `p50 <= 20 us, p99 <= 100 us` in every run measured. The spread
is not the walk: the sensor's own last-frame `cost=` is 14–35 µs throughout, and the slow
samples are single frames at 150 µs to 1.8 ms — collections, in a fixture that allocates
freely. **The fixture's per-frame work is about 20 µs and its p99 is set by the collector;
a real zone's layer (hundreds of nodes, measured below) is what decides the phase's
number.** §4.15 has it.

**What this changes about the phase's state.** Nothing in §4.5–§4.13's *mechanism* results
is retracted: the managed replacement runs on the game's thread, the read and sampler
resolve against the real assembly, and the threat-set rules are unaffected by which
container the objects come from. What this section retracts is every *number* — and §4.15
is the session that replaced them.

### 4.15 The first live threat set, the numbers it produced, and the one that was counted wrong

**The run.** `20261012-204212-p8824`, the same one command, an operator in a real zone:
**19 349 frames read the world**, `objects_live=0` on every one of them, and `layer_live`
between 12 and 28 projectiles on a layer of 210–265 nodes. The container correction is
therefore confirmed by the client itself, not by the fixture:

```
sensor read path resolved: World.Objects is Dictionary`2, projectiles are
    ...Projectile (Projectile : GameObject : Node), identity GameObject.GameId + Projectile._startTime;
    the projectile source is World.GameObjectLayer.Node._children (GameObjectLayer : Node, List`1)
sensor: frame 19350: live=28 other=218 tracked=28 retired=9477 unreconciled=1 threw=0
    frames_read=19349 cost=49us max=3721us layer_nodes=265 layer_live=28 layer_other=237 objects_live=0
```

**The report ran 64 times**, which is the countdown working (the defect in §4.14's item 1
killed it after three), and the last line is the run:

```
PHASE 2 NUMBERS | warnings: 48 predicted hit(s), mean 1.0ms | false negatives: 4258 (hit, not predicted)
 | false positives: 169 (predicted, retired without a hit) | cost: read n=19201 p50<=50us
 p99<=100us (OVERFLOW: 11 above 2000us), sampler n=15840 p50<=100us p99<=500us
 | projectiles: 584688 observation(s) from the layer's 3798248 node(s), 0 from World.Objects
 | linearity: 406077 linear, 178611 not, straight fraction 0.695
```

**And the false-negative number is wrong, in a way worth writing down.** `HitCharacter()` is
a per-frame **predicate**: `Projectile.Update` calls `Hit()` for every live projectile every
frame, and the answer decides whether the hit resolves (`CLIENT-FINDINGS.md` §7 now says so
in the place a hook author will look). The replacement observed **every entry** and called it
a collision decision. This run's own log gives both numbers side by side:

```
collision: key=(1355947,48736) tracked=yes predicted=NO warning=0.0ms ... | run: 0 predicted, 1 not, of 1 decision(s)
...
collision: key=(37836,383417) tracked=yes predicted=yes warning=1.8ms ... | run: 50 predicted, 4300 not, of 418328 decision(s)
```

418 328 entries, 4 354 of them first-time-per-bullet, against **48 resolved hits**. Every
frame a bullet spent near the player was another "false negative", which is why the mean
warning is 1.0 ms while the horizon is 600 ms, and why the run's verdict called
`false negatives: 4304` a PASS — it was reporting a number that does not mean what the plan
means by it.

**The fix, and the fixture that could not have caught it.** The replacement now calls
through first and observes only a `true` answer (`CollisionObservation.cs`), which also
removes any window in which an observation could sit in front of the game's own hit
resolution. The synthetic host had the same blind spot from the other side — its
`HitCharacter` returned a bare `true` and was called only on the tick a bullet was already
touching the player, so `evaluations == decisions` and a hook that counted entries looked
perfect. It now computes the answer from the bullet's position and the player's, and the
driver asks every live projectile every tick exactly as the client does:

```
PASS  the predicate was evaluated more often than it decided   evaluations=52159, decisions=22
PASS  the hook did not see more collisions than the host made  observed=10 host=22
PASS  the sensor had PREDICTED those hits                      predicted=10 of 10
PASS  the warning is positive and plausible                    min warning 617.7ms over 10 hit(s)
127 check(s): 127 passed, 0 failed
```

So the mistake is now testable offline, and the fixture's own `hit_character_evaluations` /
`host_hits` pair is what makes it testable.

**What this run proves, and what it does not.**

- **Proves**: the sensor reads the real client's live projectile set — the container, the
  type, the identity fields — on 19 349 frames of real play; the trajectory sampler calls the
  game's own `ProjDesc.GetPosition` on real descriptors and gates 585 000 sample sets; the
  collision replacement is entered on real collisions and compares them against the threat
  set; the read cost is `p50 <= 50 µs, p99 <= 100 µs` with eleven frames above 2 ms in
  19 201; and the straight fraction — the fifth measurement the plan names — is **0.695**
  over 406 077 linear and 178 611 curved sample sets.
- **Does not prove**: the four numbers as `IMPLEMENTATION-PLAN.md` defines them. The
  false-negative count above is an over-count, the false positives and the warning
  distribution were computed on the same wrong event set, and the corrected instrument has
  not been on a live session yet. **One more session settles it**, and this time the number
  to sanity-check is not `live` (it is 12–28) but the ratio of `collisions:` to the
  false-negative count on the report line.
- **Two caveats on the fifth number.** It is a *time-weighted* fraction, not a per-bullet
  one — the sampler samples every live bullet every frame, so a bullet that lives for two
  seconds contributes about a hundred times more than one that lives for one frame. And it
  is a fraction of the trajectories the sampler *could* sample, which is the live set the
  sensor could see, not every bullet the game ever made.

**Performance.** The sampler's p99 is in the 150–500 µs bucket against a 150 µs slice, and
the run's verdict records it as a FAIL. **The operator has waived the cost criteria for this
phase close, and that decision is recorded here rather than argued with**: the mechanism is
verified, and the cost work belongs with the solver that has to fit in the same frame
(Phase 4). §7 restates the criteria with their actuals so the waiver is visible rather than
implied.

### 4.8 The ground-truth comparison, and the warning distribution

`CollisionObservation.cs` is Deliverable 2.3 — "the most valuable artefact in the whole project", by
IMPLEMENTATION-PLAN.md's own description of it. It patches the game's own collision decision with a
second managed replacement and, at every decision, asks the threat set three questions: was this bullet
known, had the sensor already predicted it would hit, and — if so — how long ago.

**The decision it patches is the right one, and the event is the `true`.** `Projectile.HitCharacter()`
is `private bool HitCharacter()`: the instant the client resolves a bullet as hitting the local player
and is about to tell the server, which CLIENT-FINDINGS.md §4 calls "the exact moment the server is
about to be told". **It is also the client's per-frame predicate**, called for every live projectile
every frame, so most of its entries answer "no" (CLIENT-FINDINGS.md §7, and §4.15 for what that cost
when this section's first version read "at every decision" and the code read "at every call"). The
replacement takes the receiver as `object` rather than `IntPtr` — a managed reference is a pointer
in RCX either way, and the typed form means the fields can be read through reflection instead of
reinterpreting an address — and continues into the original through a **managed** function pointer,
`delegate* managed<object, bool>`, for the same reason the frame tick's does. The original is called
on every path, including the ones where the observation failed: a replacement that returned without
calling through would stop the client claiming damage. **It is now called first, and the observation
happens only on its `true`.**

**What a run of it produces**, from the fixture run of 2026-10-12 — ten decisions inside the patched
window:

```
collision: key=(39,13440) tracked=yes predicted=yes warning=619.4ms bullet=(11.6,-4.2) r=6.0
...
10 observed: tracked=yes predicted=yes in all 10;  min warning 617.7 ms over 10 decision(s)
```

Every observed decision was a bullet the threat set knew and had predicted, and the warning is the
horizon plus a frame — which is exactly right for a bullet aimed dead at the player: the sensor first
sees it on the frame it enters the 600 ms horizon. **The false-negative count is
`decisions − predicted`** (zero here), and the **false-positive count is
`ThreatSet.PredictedButNeverHit`**, incremented when a bullet the sensor predicted is retired without
the game ever deciding it hit. Two of the four numbers Phase 2 exists to produce now have a
distribution behind them — **and the first live session showed that the fixture could not tell a
decision from an entry, which is why the host now asks the predicate for every live projectile every
tick and counts both** (§4.15).

**Four fixture defects were found by building this, and three of them were bugs in the fixture's own
geometry rather than in the hook.** They are recorded because each produced a comparison with nothing
in it, and every one of them would have looked like "the sensor works" if the assertions had been
written to check only that the code ran:

1. **The aim used the opposite angular convention to the descriptor.** The families compute
   `x = sin·v·t, y = cos·v·t`, so travel direction is `(sin a, cos a)` and not the usual `(cos a,
   sin a)`. The first version placed the spawn on a `(cos, sin)` offset and handed the desc `a`, so
   every "incoming" bullet set off perpendicular to its own aim. Result: `host_hits=0`.
2. **The bullets were retired before they could arrive.** A 250-unit flight at 120 units/s takes
   2.1 s; the retirement cadence was 1.5 s. Also `host_hits=0`.
3. **The first collision happened before the hook was patched.** The discovery window is 30 s and the
   patch lands after it, so a fixture that waits for the patch before colliding never has the method
   compiled *inside* the window — and a method that is never compiled is never matched. The host now
   collides early *and* keeps colliding, and the run is long enough for the patched window to contain
   most of them; the two unobserved decisions are the pre-patch window and are reported rather than
   asserted away.
4. **`HitCharacter` was inlined**, and this one is not the fixture's — it is `CLIENT-FINDINGS.md` §3's
   flagged risk for the *real* method ("it is `private` and called from one place; if inlining is
   observed, fall back to hooking `Hit()`"), reproduced. Fifteen IL bytes ending in `return true` is
   what the JIT inlines, and the run reported `collision MISSING — never compiled in the discovery
   window` while the host was calling it fifteen times. `[MethodImpl(NoInlining)]` fixes the fixture;
   a live game would need the documented fallback, and this is the evidence that the risk is real
   rather than theoretical.

### 4.16 The run that looked like a regression, and the patch that arrived too early to be useful

**What it looked like.** The session of 2026-10-12 21:22 published
`collisions: 0 decision(s), 0 known, 0 unknown` on every report line. The collision counter had been
changed a few hours earlier — from counting predicate entries to counting decisions — so the obvious
reading was that the change had broken it.

**What it actually was.** The hook decides what to patch in **one pass at the close of the
thirty-second discovery window**, and `Projectile.HitCharacter` is compiled the first time a
projectile is fired at the player. The game's own log puts the player in `GameScene` at 21:23:10 and
the first zone music at 21:23:16; the window closed at 21:23:16. The target was then matched and
reported as found — *after* the pass that would have hooked it:

```
[ERROR]   collision    MISSING  never compiled in the discovery window ... This target is NOT patched.
[INFO ] 1 of 4 target(s) patched
[INFO ] target 'collision' found: IL 989 bytes, native 1298 bytes at 0x7FFF8F4FE720 (3/4)
```

So `collisions: 0 decision(s)` meant **"nothing was hooked"**, not "nothing collided" — and the
sensor around it was healthy: 19 349 frames read, 11 projectiles live on the last frame, the report
published 64 times, straight fraction 0.891. **The counter change is not implicated**; the
replacement was never entered, because the method was never patched.

**The fix is §6.2 one level down: a window's count standing in for the run's.** The live window now
runs a late patch pass once a second (`DrelayPatchNewTargets`), which hooks any target that became
`found` after the window closed, skips the ones already hooked, and leaves the record list the
uninstall walks untouched — the same body as the first pass, shared rather than copied, because the
detour selection and the trampoline-before-enable order are exactly the parts that must not differ
between a patch at t+5 s and one at t+55 s. Run 12 of the injection suite is the regression test: the
host's first call to the predicate is delayed past the window, so the method *cannot* be compiled
inside it, and the run has to show `collision MISSING` at the window's close, `PATCHED 'collision'`
afterwards, and observed collisions that follow.

**What it changes about this run.** Only that it has no collision comparison, and therefore no §7
first-criterion datapoint. Everything else it measured stands, and it is recorded rather than
deleted: a run whose empty comparison has a named cause is worth more than a run with no empty
comparison at all.

**And the same session caught a second line making a claim it cannot support.** `escape` printed

```
target-size escape       IL size 335: compiled 6 time(s) but did not match -- the pinned IL differs from this build's
```

on a build whose pinned IL is correct: six *other* methods of 335 bytes were compiled and
`RequestEscape` was never reached, which is what "the player did not press escape" looks like. The
line is a statement about which same-size candidates were compiled, so it now says that, and points
at `hook.log.observed` — which lists every same-size hash that was seen — instead of choosing between
"never compiled" and "a game patch" for the reader. The README already tells operators to read these
lines first; a line that sends them looking for a patch that does not exist is worse than no line.

## 6. Instrumentation this phase needed before it could report

Both of these were the tail of Phase 1's own verification, and both are now done.

### 6.1 A frame-cost histogram, and a locator that survives ring overflow — **done**

Phase 2's exit criterion is a **p99** per-frame cost. The frame ring holds 1 024 rows and an hour
writes 209 897, so `frames.csv` cannot produce a percentile — and Phase 1's own 323 µs outlier was
therefore lost the moment it happened ([PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md) §11.4). The
pattern already exists in this codebase: `hook.log.sizes` is a bucketed histogram of IL sizes, and
`tools\analyze_size_histogram.py` is the reader for it. `CostHistogram` is the same shape, with the
budget points as edges, and the read and the sampler have one each. **Two lessons came out of using
it, both in §4.14 and §4.15**: the report that prints it has to keep printing (its cadence was a
modulo on a counter another path resets), and the overflow branch has to be *tested* — a bucket
above the last edge crashed the very line that existed to report it.

### 6.2 The `N target(s) found` line must count the run, not the discovery window — **done**

The endurance run's final line read `1 target(s) found` while the target-size table four lines above
it said all four were `compiled … AND MATCHED` — three of them after the fixed 30 s discovery window
closed ([PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md) §11.5). Phase 2 hooks `spawn` and
`collision`, which are exactly the methods that a title-screen window does not compile. The summary
now reports **both** counts — matched inside the window, and matched by the end of the run. On the
2026-10-12 20:42 session the old line read `3 target(s) found` about a run whose own table said all
four were found, because `escape` is not called until a zone loads.

## 7. Exit criteria (from the plan, restated with their instruments)

Restated with the actuals from §4.15, so the state of each one is visible rather than implied.

| criterion | state |
|---|---|
| prediction agrees with the game's own collision decision on the great majority of hits, and **every disagreement has a named cause** | **not yet measured.** The instrument works and the session produced disagreements, but the false-negative count of that session is an over-count (§4.15) and the corrected counter has not been on a live run. This is the one criterion still open |
| the sensor's **p99** per-frame cost is inside the 300 µs ceiling with room for the solver (§6.1) | **read: yes** — `p50 <= 50 µs, p99 <= 100 µs` over 19 201 frames, with 11 frames above 2 ms. **sampler: no** — `p50 <= 100 µs, p99 <= 500 µs`. **The operator has waived the cost criteria for this phase close** (recorded in §4.15); the cost work belongs with the solver that shares the frame, in Phase 4 |
| the **linearity gate** has told us what fraction of real bullets are straight | **yes** — **0.695**, over 406 077 linear and 178 611 curved sample sets, from the live session. Time-weighted rather than per-bullet, which §4.15 flags |

## 8. What would stop Phase 2

Written down before the work, so that stopping is a decision and not a drift. **None of the three
below happened**, and the phase is closing with §7's first criterion still open rather than with a
stop: what the sessions established is that the mechanism works end to end on the real client
(§4.15), which is the question Phase 2 was gated on.

- **the mechanism test fails and the native fallback cannot sample trajectories without a curvature
  error large enough to make the FP/FN numbers meaningless.** Then the honest output is the
  measurement of that error and a stop at "Phase 3 only, from collision events alone" — the sensor
  would still be able to say *that* a bullet is about to hit, and Phase 3's escape needs no
  trajectory at all.
- **`ProjDesc.GetPosition` turns out to be inlined everywhere it matters**, so there is no separate
  body to call. The escape hatch is the one [HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §2.1 names:
  `RequestReJIT` + `SetILFunctionBody` through a profiler.
- **the false-negative rate is dominated by causes we cannot model** (AoE, bombs, server-resolved
  damage). That is not new — it is the 74 % the research already measures — but Phase 2 is where it
  becomes a number attached to *our* sensor rather than to the packet stream, and if it swamps the
  signal the dodge should not be built.
