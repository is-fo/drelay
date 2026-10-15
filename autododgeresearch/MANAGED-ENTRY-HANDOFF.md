# Running managed code on the game's thread — research handoff

**For:** the agent picking up the mechanism question left open by the native-frame-read round.
**Read first:** [PHASE1-HANDOFF.md](PHASE1-HANDOFF.md) §13, `work/crash-debug/FINDINGS.md` in the
repository root's `work` directory, and [HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §5.2 rule 3.

An earlier attempt at this problem is already resolved and **must not be re-derived**: calling a
managed method from the frame detour through an `[UnmanagedCallersOnly]` transition stub is illegal,
and the runtime kills the process for it. The full evidence is in §2. What is *not* resolved is
whether any mechanism exists at all. That is the task.

---

## 0. Amendment, 2026-10-12 — the question is narrower than this document assumed

**Read this before §1 and §3; it corrects their framing, and the correction is measured.**

This document says (§1) "For anything that must *run* managed logic on the game's thread … there is
no working mechanism yet. **Entering managed code from that thread is the thing that is refused**".
That is **too broad**, and the shipped, verified build falsifies it.

`FrameTickDetour` ends with `g_frameTickTrampoline(self, total)` (`native\DrelayFrame.cpp:360-361`),
and that trampoline is MinHook's copy of `DarzaWindow.Update`'s overwritten prologue followed by a
`jmp` back into its **JIT-compiled managed body** (`DrelayFrame.cpp:485`, `:504-506`; the contract is
stated at `:264-269`). So every frame of the hour-long acceptance run of 2026-10-12 was a **native
`call` into managed code from the game's own thread**, with the managed body's return address in
`Drelay.Hook.dll` — **209 897 times**, across the ~24 collections the game's own log records, with no
fault. Managed code runs on that thread, entered from native, and the game keeps playing.

What is actually refused is a *specific entry path*, not the concept:

| route into managed code, from the game's thread | state |
|---|---|
| a **plain** call into JIT-compiled code — the trampoline's `jmp` into the body | **measured working**: 209 897 calls in one hour |
| an `[UnmanagedCallersOnly]` / `Marshal.GetFunctionPointerForDelegate` **transition stub** | **refused, fail-fast**, six dumps, `coreclr+0x244839` |

The difference is that the second is a reverse P/Invoke: its prologue asserts the caller is
unmanaged, and the frame thread is not. The first makes no such assertion — the callee simply runs,
in the cooperative mode that running managed code requires. §2's measurements stand exactly as
recorded; only the conclusion drawn from them changes.

**Consequences for this document.**

- §3's jump-detour candidate is **no longer the first thing to try**. It is still a valid fallback,
  but the cheaper candidate is: the bootstrap JITs a helper of *our own design* (a plain static
  method, **not** `[UnmanagedCallersOnly]`), publishes `RuntimeMethodHandle.GetFunctionPointer()` for
  it into the shared block, and the native detour calls that address — the same route the trampoline
  call already uses, with a signature we chose rather than one we had to decode.
- The **unmeasured** part is narrower and testable: the measured case calls the **game's own method**
  as a leaf-ish frame; it does not prove that a helper of ours which **allocates**, **calls other
  managed methods** and therefore **triggers collections while it is on the stack** is safe. When the
  GC walks that stack it finds the helper's JIT info from its IP, unwinds to a return address in
  `Drelay.Hook.dll` that is in no managed code range, finds no transition `Frame`, and by the
  runtime's rule **stops** — so every managed frame outside our native detour goes unscanned. That is
  a real hypothesis with a real failure mode, and it is what the Phase 2 spike's forced-collection
  variant measures ([PHASE2-SCOPE.md](PHASE2-SCOPE.md) §4.6).
- §5 item 6's warning ("a managed replacement's address is not the same thing as an
  `[UnmanagedCallersOnly]` method's address… measure it, do not document it") becomes the central
  measurement rather than a side note.
- §4 step 1's parenthetical — a target entry "can be added to its own build record, **which is what
  the existing runs already do** for the target table" — is **wrong**. `hook\tools\Test-Injection.ps1`
  has six runs and none of them adds a target; there is no host build record, no `DRELAY_TARGETS`, and
  no launcher option for one. The target table is generated at build time from `known-build.json`
  (`tools\gen_target_table.py` → `GeneratedBuildTable.cs` → `PublishTargets()`,
  `StartupHook.cs:1127-1172`) and nothing else can contribute to it. The plumbing a host-target run
  needs is written out in [PHASE2-SCOPE.md](PHASE2-SCOPE.md) §4.1.

The deliverable §6 asks for is unchanged; the mechanism it is asked about is now a smaller and more
specific question.

### 0.1 Amendment 2, 2026-10-12 — the cheap route was built, measured, and is flaky; §3's candidate is the design

The route §0 nominated (a plain managed helper called from the native detour) was built and tested on
the synthetic host. Nine runs, evidence in `hook\obj\cases\phase2-mechanism\`; the table and the
argument are [PHASE2-SCOPE.md](PHASE2-SCOPE.md) §4.3. Two disciplines emerged, each isolated by a
control that changed one thing:

1. **Every managed method on the detour's path must already be compiled.** Calling an uncompiled
   method there means compiling it there — which allocates, takes the JIT lock and can collect. One
   run corrupted the host's own objects on the first patched frame; with the path pre-compiled and
   warmed before the patch, three runs of the same scenario were clean. `PrepareMethod` covers only
   the method named, so callees and the dispatch site must be prepared and exercised explicitly.
1. **Nothing on that path may allocate.** Same path, same 647 forced full compacting collections, same
   witness: the helper failed both times it allocated-or-logged and was clean three times when it did
   neither. The switch that separated them dropped both a 64 KB array and the helper's logging, so
   those two are not separated — and no longer matters.

**And then the fourth run of the disciplined configuration — warmed, non-allocating, the same 647
forced full compacting collections — crashed in `gc_heap::drain_mark_queue`, the same place run F
died.** So the plain-call route is not safe and not reliably unsafe: it is **flaky**, roughly one run
in four under a stress the game reaches on its own. The crash is always in the GC's mark phase and the
reproduced corruption is always of objects reachable only from frames older than the detour, which is
the original diagnosis: the runtime cannot unwind a managed stack that a native frame interrupts
without a transition marker, and no discipline *inside* the helper can supply the frame its caller
does not have.

**§3's jump-detour candidate is therefore the design rather than the fallback**, and the reason is
structural: there the replacement is entered as a managed call from the game's own managed caller, so
no native frame interrupts the managed chain and neither discipline has to hold. Its own open question
— how the replacement continues into the original without reintroducing that frame — is the next thing
this project should measure, and it is now on the critical path for Phases 2, 3 and 4 together.

There is no gate for the helper route in the test suite. It was written as run 8 of
`hook\tools\Test-Injection.ps1` and removed again: a gate that is flaky against a real fault is worse
than no gate, because it teaches its reader to rerun until it is green. The instrument survives
(`--frame-helper` and the `DRELAY_FRAME_HELPER_*` switches) and so does the case directory, so the
regime can be re-run by hand whenever the replacement needs comparing against it.

### 0.2 Amendment 3, 2026-10-12 — §3's candidate was built, and it works

The managed replacement this document nominated in §3 is built and measured. MinHook's `jmp` at the
target's entry is aimed at a managed method with the target's own signature, so the game's `call`
becomes a managed-to-managed call; the replacement continues into the original through a **managed**
function pointer (`delegate* managed`, not `delegate* unmanaged`) to MinHook's trampoline, which the
JIT emits as a plain `call`, so the original body's return address is inside the replacement and the
whole chain stays walkable.

**Seven runs under 650–651 forced full blocking compacting collections — the identical stress that
killed the helper route about one run in four — all clean**, including two with allocation on the
patched path. `patched_body_calls == self_observed_calls` in every one, the caller-held witness
intact in every one, no crash in any. Run 8 of `hook\tools\Test-Injection.ps1` re-runs this on every
invocation with allocation ON.

**The answer to §1's question, then, is yes — with the qualifier that the *route* matters and not the
concept.** Managed code can run on the game's thread, entered directly from the game's own call. What
cannot be depended on is reaching it by `call`ing into managed code from a native detour, because that
leaves a native frame inside the managed stack that the runtime cannot unwind past. The phases that
needed this answer — Phase 2's sampler, and Phase 3's `World.RequestEscape` and movement work, which
are one merged phase since 2026-10-13 — can now be built the managed way, through a replacement
rather than a callback.

Details, the run table and the raw evidence: [PHASE2-SCOPE.md](PHASE2-SCOPE.md) §4.4 and
`hook\obj\cases\phase2-mechanism\` (`repl-*`).

---

## 1. The question

**Can a hook that intercepts a method running on the game's own thread execute managed code?**

Phase 1 does not need the answer any more — the frame read is native (§4) and that closed the
phase's blocking item. Phase 3 does need it: invoking `World.RequestEscape`, walking pooled
`Projectile` instances, or reading anything behind a managed accessor cannot be done by walking raw
offsets, and those actions have to happen on the thread that owns the game's frame.

The deliverable is a **yes/no with a measurement**, not an implementation. If the answer is yes, the
follow-up is a design; if it is no, the follow-up is a re-scoping of every Phase 3 target that
currently assumes it.

---

## 2. What is already established (do not re-derive)

### 2.1 The measured failure

`FrameTickDetour` used to call `ManagedFrameCallback.OnFrame`, an
`[UnmanagedCallersOnly(CallConvs = [CallConvs.Cdecl])]` static method, through its transition stub.
Every run died about a second after the `frameTick` patch went live. The death was
`COR_E_EXECUTIONENGINE` (`0x80131506`), at **`coreclr.dll+0x244839`** in every one of six crash
dumps spanning three builds. Resolved against the runtime's own PDB that address is:

```
coreclr!ReversePInvokeBadTransition + 0x9        (function rva 0x244830, size 0x33)
```

and the faulting thread's stack, from the dump of run `20261009-003209-p31396`, is:

```
coreclr!JIT_ReversePInvokeEnterRare + 0xBE    <- the reverse P/Invoke's entry helper
coreclr!ReversePInvokeBadTransition + 0x9     <- the refusal; the exception address
coreclr!EEPolicy::HandleFatalError + 0x85     <- COR_E_EXECUTIONENGINE, then TerminateProcess
Drelay.Hook.dll+0x4C1F   = FrameTickDetour + 0xDF   <- the detour
DarzasDominion.dll+0x2F54405                        <- the game's own managed frame, still below it
```

`managedFrameWaits` was `0` in the shared block of **every** crashed run, which is the proof that the
managed method body was never entered: the refusal happens in the stub prologue.

### 2.2 Why it is refused, and why that is not negotiable

A call-style detour on a managed method runs on a thread that is already inside managed code — the
detour sits between the managed caller of `DarzaWindow.Update` and its body — so the thread is in
cooperative GC mode with live managed frames below the detour. A reverse P/Invoke asserts the
opposite: that its caller is unmanaged, because the transition frame it pushes is where the GC stops
walking the managed stack. coreclr enforces the assertion rather than tolerating it.

Note the asymmetry that makes `OnProbe` work: the **watcher** thread is a genuine unmanaged thread
created by `CreateThread` inside the injected DLL, never having entered managed code, so its reverse
P/Invoke is exactly what the runtime expects. It has entered managed code thousands of times without
incident. **The problem is the thread, not the attribute.**

### 2.3 The control that isolates it

With `DRELAY_NO_PATCH=1` the process ran 281 s. With the patch installed and the managed callback
suppressed — which needed `DRELAY_NO_FRAME_CB=1` at the time, a switch that no longer exists — the
process ran 115 s and the detour executed **4 963 frames with a 1 µs worst call**. The patch, the
trampoline and the detour body are therefore all sound; only the managed call kills it.

### 2.4 What replaced it

The frame read is native now (`DrelayFrame.cpp`): the detour dereferences the `World.Shared` static
field, walks to `Player`, checks the object's MethodTable against the `Player` type handle the
bootstrap publishes in `managedPlayerTypeHandle`, reads the two `Position` floats, bounds them, and
records a success or a failure — all inside a `__try`. It is the same read the arming cross-check
performs as its production path, and it survived 104 s and 294 frames on the title screen with the
read path correctly closed (`arm state: WAITING`), which is the run that confirmed the fix.

---

## 3. The candidate, and the hazard in its obvious form

Both a call detour and a jump detour get installed with MinHook. The difference is what the entry
jumps to:

* **call detour (dead)** — entry `jmp`s to a *native* function, which `call`s the trampoline and
  `call`s managed code. The managed call is the illegal reverse P/Invoke.
* **jump detour (candidate)** — entry `jmp`s directly into a **managed** replacement. The game's own
  `call DarzaWindow.Update` then *is* a managed-to-managed call; the thread never leaves cooperative
  mode, so there is no transition to refuse and the replacement may do anything.

That much is well-precedented: it is how Harmony and MonoMod patch methods on .NET Core.

**The hazard is what happens next.** Having done its work, the replacement must continue into the
real method body, and the obvious way is to call MinHook's trampoline through a function pointer.
That call is itself a managed→native transition, which puts the thread into **preemptive** mode —
and then the trampoline jumps into the original method's JIT'd body, so managed code runs with the
thread believing it is outside the managed world. The GC would not scan that frame, which is the same
family of defect as §2.1 and is exactly what rule 3 now warns about.

The clean form is to fall into the body with **no native frame at all** — a tail jump after the
replacement's managed code has run — which C# cannot express and which needs a hand-written stub.
Whether that stub is possible without breaking the managed frame chain is the research question.

**Do not assume the hazard is fatal before measuring it, and do not assume the obvious form works
either.** Both are one experiment each.

---

## 4. What to measure, in order

Each step has a falsifiable assertion. Stop at the first one that fails and write down what it
showed; a negative result is the deliverable.

### Step 1 — the synthetic host (no game needed)

`tools/Test-Injection.ps1` already stages a synthetic .NET host from the game's own runtime and
drives the hook through runs 1–6 offline. Extend it with a run that:

1. has a method the host calls on a fixed cadence (a stand-in frame tick), identified by IL hash so
   the target table can match it;
2. has that method patched with a **jump** detour whose destination is a managed replacement;
3. asserts that the replacement is entered, that `self` and the float argument arrive intact, and
   that the host's own method body still runs exactly once per call.

The host does not need the game's IL hashes for this: a target entry for the host's method can be
added to its own build record, which is what the existing runs already do for the target table.

### Step 2 — the mechanism of "continue into the original"

Three variants, each measured separately:

| variant | what it does | what to watch |
|---|---|---|
| call the trampoline from managed | the obvious form | does the process survive a forced GC? does the original body run? does `frames.csv` keep a ~60 Hz cadence? |
| tail-jump stub | no native frame | same, plus: is the managed stack walk still intact (does the host's caller frame survive a GC)? |
| do not call the original at all | observe-only replacement | an upper bound on what is possible; proves the entry works even if continuation does not |

### Step 3 — force GCs

This is the step that decides soundness, and it cannot be replaced by a long run that happens not to
collect. Allocate continuously on a second thread inside the host for the duration, so collections
are frequent and occur while the patched method is on the stack. A variant that survives 5 minutes
without a forced GC has measured nothing.

### Step 4 — the game

Only after steps 1–3 pass. The game's own frame tick, the same three-way comparison against the
native read (which is now the baseline that works), and a session long enough to cross collections.

---

## 5. Things that will waste your time if you do not know them

1. **The launcher strips `DOTNET_*` from the child's environment.** See PHASE1-HANDOFF.md §4.5.
2. **Harness-spawned runs leave less evidence than operator runs.** WER wrote a full 47 MB dump for
   every crash the operator's shell produced, and none for any crash this session reproduced; and a
   plain `Environment.FailFast` from the same shell wrote no dump either. WER `LocalDumps` is enabled
   machine-wide, so this is a property of processes spawned from the agent sandbox — the same
   restriction PHASE1-HANDOFF.md §11 records. Do not read "no `crash.txt`" as "no crash".
3. **`COMPlus_DbgEnableMiniDump` did not produce a dump for this self-contained coreclr**, with or
   without `DbgMiniDumpName`, and did not produce one at the default path either — while the same
   knobs worked for a framework-dependent .NET 8 app. The `DOTNET_` spelling may be the only one
   .NET 9 honours, and the launcher deletes it. Until the launcher is changed, **an automated run
   cannot capture a dump**; the dumps in §2 came from the operator's own shell.
4. **The gate is checked inside the read, not around the detour**, so a run whose cross-check refuses
   still enters the detour. That was true of the managed callback and is true of the native read.
   Nothing in the frame path may assume the world exists.
5. **An instrument's blind spot must not be where the failure is.** `shared.block`'s `frameCalls` used
   to be written *after* the managed call, so every failure inside it reported `frame calls: 0` —
   which reads as "the detour never ran". It is written before the read now. Check the same property
   of anything you add.
6. **A managed replacement's address is not the same thing as an `[UnmanagedCallersOnly]` method's
   address.** The latter is a transition stub; for the former you want the code the game's own
   `call` should land in. What `RuntimeMethodHandle.GetFunctionPointer()` returns for a given method
   is one of those things to *measure*, not to document — this project has three ABI faults on
   record, all from reading a signature instead of measuring it (PHASE1-HANDOFF.md §4.6).
7. **`Drelay.Hook.map` resolves the hook's own RVAs** (`tools/Resolve-HookRva.ps1`), and
   `work/crash-debug/resolve-rva.ps1` resolves a `Module+RVA` against the cached `coreclr.pdb` and
   `clrjit.pdb`. `work/crash-debug/analyze-dump.ps1` reads a WER minidump directly — exception
   record, per-thread RIPs, and a resolved stack scan. Use them; they exist because there is no
   debugger on this machine.

---

## 6. What "done" looks like

Either:

* **a measured yes** — a synthetic host where a managed replacement runs on the thread that called
  the patched method, the original body still runs, and the whole thing survives forced collections,
  with the evidence in a committed test run; or
* **a measured no** — a specific failure of each variant, with the dump or the event-log offset that
  names it, so that every Phase 3 target that needs managed code on the game's thread can be
  re-scoped against a fact rather than a hope.

Write it up as a new section in PHASE1-HANDOFF.md (or a sibling document) with the same discipline as
§12: the measurement, the control, and what the control would have said if the mechanism had not
worked.
