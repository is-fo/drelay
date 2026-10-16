# Hook architecture — injecting a C++ DLL into the .NET 9 client

This document specifies **how bytes get into the game process and how the hook stays there**.
It is the mechanical companion to [CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) (what the client looks
like), [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) (what the hook computes),
[JAVA-BRIDGE.md](../bridge/JAVA-BRIDGE.md) (how it reports) and
[IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) (the order of work).

Every claim marked **[verified]** was checked against the shipped binaries on this machine; the
commands that check them are in `hook/tools/`.

---

## 1. The five facts this design rests on

| # | fact | consequence |
|---|---|---|
| 1 | `DarzasDominion.dll` is **pure IL** — `COMIMAGE_FLAGS = 0x1`, `ManagedNativeHeader RVA = 0` **[verified]** | every game method is JIT-compiled, so a JIT-level hook sees all of it |
| 2 | `System.Runtime.TieredCompilation` is **`false`** in the app's own `runtimeconfig.json` **[verified]** | each method is compiled **once**, fully optimised, and never recompiled: a patch is permanent, and there is no tier-promotion hazard |
| 3 | `clrjit.dll` exports `getJit`; `g_CILJit`'s vtable has exactly four slots, with `compileMethod` at **slot 0** and `getVersionIdentifier` at **slot 2** **[verified by disassembly of the shipped DLL]** | we can detour the JIT's vtable legally and *validate* which runtime we are in before touching anything |
| 4 | `System.Private.CoreLib.dll` **is** ReadyToRun **[verified]** | CoreLib methods are never JIT-compiled and can never be seen by a `compileMethod` hook. Design around it; do not hook CoreLib |
| 5 | the game ships **two apphosts** (`DarzasDominion.exe` and `OpenGL\DarzasDominion.exe`) carrying **different** `DarzasDominion.dll` builds (50,216,960 vs 50,831,360 bytes) **[verified]** | the hook must **fingerprint the build at attach** and refuse to run against an unknown one |

Fact 2 is the one that makes this project tractable. A JIT-hooking design normally has to fight
tier-0 → tier-1 promotion and re-patch on every recompile; here the runtime is configured not to do
that, so **one detour per method, installed once, for the life of the process.**

> **The one way to lose fact 2.** In .NET 9 environment variables take precedence over
> `runtimeconfig.json`. A stray `DOTNET_TieredCompilation=1` inherited from a launcher's
> environment would silently override the app's own setting and reintroduce the entire
> promotion problem. The hook must **read back** the effective setting (see §9) and refuse to
> install if tiering is on.

---

## 2. Two decisions to take before writing code

### 2.1 Detour the JIT, or load a profiler?

Two credible mechanisms exist. They are not equivalent, and the choice shapes the whole project.

**(a) JIT vtable detour (recommended).** Patch `ICorJitCompiler::compileMethod` (vtable slot 0 of
the singleton returned by `clrjit!getJit`). Every JIT compilation in the process then flows through
our function, which records `(MethodDesc, metadata token, IL size, IL hash, native entry, native
size)` and then patches the finished native code with an inline detour.

- *For*: no configuration, no launch-time environment, works on a process that is already running;
  gives a name-independent method identity (IL hash) which is exactly what an obfuscated assembly
  needs; gives us the **native address and the runtime's own field-offset oracle** for free.
- *Against*: `compileMethod` runs on whichever thread is JITting, so the hook must be re-entrancy
  safe; and because the EE copies the JIT's read/write buffer to executable memory *after*
  `compileMethod` returns, the `nativeEntry` we capture is the **final RX address**, which is what
  we want to patch but means we must patch it from a point where the copy has happened.

**(b) CLR profiler (`ICorProfilerCallback`).** `CORECLR_ENABLE_PROFILING` +
`CORECLR_PROFILER_PATH` + `CORECLR_PROFILER={CLSID}`, or attach at runtime through the diagnostics
IPC with `Microsoft.Diagnostics.NETCore.Client`'s `AttachProfiler`, then
`ICorProfilerInfo::SetEventMask(COR_PRF_MONITOR_JIT_COMPILATION)` and
`JITCompilationFinished` / `GetCodeInfo2`, or `SetEnterLeaveFunctionHooks` for managed-entry hooks,
or `RequestReJIT` + `SetILFunctionBody` to rewrite IL.

- *For*: the only **supported** instrumentation surface; enter/leave hooks are GC-correct and
  exception-correct for free; `RequestReJIT` can rewrite IL, which is the most powerful option.
- *Against*: needs launch-time configuration or a diagnostics attach (both of which a future game
  patch could disable via `DOTNET_EnableDiagnostics*`); only **one** profiler may be active; a full
  IL rewriter is a large amount of work; and `SetILFunctionBody` requires the profiler to have been
  present at method load (or a ReJIT cycle), which complicates third-party tooling coexistence.

**Recommendation.** Build (a). It is self-contained, it needs nothing from the environment, and its
only real cost — re-entrancy discipline in one function — is a dozen lines. Keep (b) documented as
the escape hatch for the specific case where a target turns out to be inlined and cannot be
detoured at all, because `RequestReJIT`/`SetILFunctionBody` is the only mechanism that can
instrument a method with no separate body.

### 2.2 Native detours, or managed hooks?

Since we already have a managed code path available (see §3), the hooks themselves could be
installed three ways:

| | mechanism | verdict |
|---|---|---|
| **A** | native inline detour on the JITted code (MinHook, or a hand-rolled 5-byte jump) | **recommended.** Total control of the frame budget, no managed allocation inside the hook, no dependence on the obfuscated names being resolvable by a managed patcher |
| **B** | managed patcher (Harmony / `MonoMod.RuntimeDetour`) called from the bootstrap | attractive because hook bodies get GC-correct frames and managed field access for free — but both libraries resolve targets by *name*, and this assembly's names are mangled. Viable if driven by metadata token rather than name; worth a spike (§7, Spike 4) |
| **C** | profiler enter/leave hooks | highest ceremony, best correctness guarantees; keep as fallback |

The reference engine reached the same conclusion from the other direction: its actuation is a
native call through a resolved method pointer, and its *reads* are raw native offsets, in an IL2CPP
process where managed hooks were not an option at all. We have an easier target and can afford to
be more conservative: **native detours for control, and deliberately tiny hook bodies that do no
allocation and take no locks.**

---

## 3. Bootstrap: getting the DLL in early enough

The hook must be resident **before the game's first frame**, and ideally before the bulk of the
assembly is JITted, so that `compileMethod` observes the targets. Four mechanisms, in order of
preference.

### 3.1 App-directory proxy DLL + startup hook (recommended)

The deployment is self-contained, so `hostfxr.dll` and `coreclr.dll` are loaded **from the
application's own directory**, which is item 7 of the standard DLL search order. Planting a
forwarding proxy there gets native code into the process before CoreCLR initialises, without
`CreateRemoteThread`, without `SeDebugPrivilege`, and without a second launcher. This is the same
technique UnityDoorstop uses for Unity games.

Pair it with `DOTNET_STARTUP_HOOKS=<path>` in the process environment, which the host runs **before
the app's entry point**, to get a managed bootstrap as well. The two together give the ideal
combination: native code before the runtime, and managed code before the game.

*Caveat*: this modifies the game installation directory, which the existing `drelay` project
explicitly does not do (`README.md`: *"changes nothing on disk in the game's installation"*). If
that principle is to be preserved, use §3.2 instead. This is a product decision, not a technical
one, and it is called out here so it is made deliberately.

### 3.2 Launcher with a controlled environment

`drelay` already elevates and launches the game for its address-claiming trick, so it can equally
launch it with `CreateProcess(..., CREATE_SUSPENDED, …)` and a hand-built environment block,
inject, then `ResumeThread`. This:

- guarantees the environment (blocks the §1 tiering override, sets `DOTNET_STARTUP_HOOKS`);
- allows injection **before any managed code runs**;
- needs no writes to the game directory;
- fits the project's existing architecture.

*Cost*: the game must be started by us. Steam launch options can point at our launcher, so the user
experience is preserved.

### 3.3 `CreateRemoteThread` + `LoadLibrary` into a running process

Workable and is what a "attach to the game I already have open" workflow needs. Two rules:

1. **`DllMain` does nothing.** The loader lock is held during `DllMain`, CoreCLR registers its own
   `DllMain`, and the documented never-do list includes `LoadLibrary`, `CreateThread`, and
   synchronising with other threads. `DllMain` should call `DisableThreadLibraryCalls` and return.
2. **Do the work from an exported `Init()`**, called by the injector's own remote thread *after*
   `LoadLibraryW` returns. That keeps `DllMain` an empty stub, which is what Microsoft recommends.

The cost is that `Init` runs on a native, non-CLR thread, so anything that touches managed state
must go through a proper transition — which is another reason §5 does its managed reads from a
managed callback rather than from raw native.

### 3.4 Diagnostics IPC (`ApplyStartupHook`)

`Microsoft.Diagnostics.NETCore.Client` can attach a startup hook to an already-running process over
the diagnostics pipe, with no injection API at all. It is the cheapest spike and the most fragile
shipping mechanism, because it depends on diagnostics not being disabled. **Use it to de-risk, not
to ship.**

### 3.5 Recommended combination

```
launcher (drelay, existing elevation path)
  └─ CreateProcess(DarzasDominion.exe, CREATE_SUSPENDED, controlled env block)
       ├─ DOTNET_STARTUP_HOOKS = <abs>\Drelay.Bootstrap.dll
       └─ (no DOTNET_TieredCompilation, no DOTNET_ReadyToRun)
     inject Drelay.Hook.dll → call Init()
     ResumeThread
         ↓
     CoreCLR starts → bootstrap runs before game Main
         ↓
     bootstrap: resolve targets by metadata token, PrepareMethod them,
                measure field offsets, publish a GcHandle for World.Shared,
                call DrelayHook_Install(config)
         ↓
     Drelay.Hook.dll: validate runtime → detour compileMethod →
                      capture targets → MinHook-patch them
```

---

## 4. Installing the JIT detour

### 4.1 The exact sequence

```
1. GetModuleHandleW(L"clrjit.dll")                      // [verified] this DLL is in the install dir
2. getJit = GetProcAddress(jit, "getJit")               // [verified] exported
3. pJit = getJit()
4. vtbl = *(void***)pJit                                // CILJit has no data members, so vptr is at +0
5. VALIDATE, in this order, and abort loudly on any failure:
   a. vtbl[0..3] all lie inside clrjit.dll's .text      // the only four code pointers in the table
   b. read the GUID via vtbl[2] and compare to the expected 9.0 JITEE identifier
   c. compare coreclr.dll's / clrjit.dll's file version to the pinned value
6. VirtualProtect(&vtbl[0], sizeof(void*), PAGE_READWRITE, &old)
7. g_realCompileMethod = vtbl[0]; vtbl[0] = &HookCompileMethod;
8. VirtualProtect(&vtbl[0], sizeof(void*), old, &old)
```

Step 5 is the part that is *not* optional, and the reason is concrete: the community implementations
everyone copies have a **stale constant**. MonoMod and xoofx/ManagedJit both hardcode
`getVersionIdentifier` at vtable index **4**, which was correct for .NET Core 3.1; in .NET 5 the
interface lost `clearCache`/`isCacheCleanupRequired`, and the index became **2**. `compileMethod`
has always been slot 0. On the shipped `clrjit.dll` the four slots resolve to `compileMethod`,
`ProcessShutdownWork`, `getVersionIdentifier`, `setTargetOS` — and slot 3 is an empty stub, which
matches the runtime source (its body is inside a `TARGET_OS_RUNTIMEDETERMINED` guard that Windows
does not define).

**Validate by behaviour, not by version number.** Reading slot 2 and getting the JIT/EE GUID back is
a positive proof that we have the right object and the right slot; if it returns garbage, we abort
before writing anything. This single check turns "the game crashes on launch after a game update"
into "the hook printed one line and did nothing", which is the difference between a debugging
session and a bug report we cannot act on.

### 4.2 The detour body

```cpp
CorJitResult HookCompileMethod(ICorJitCompiler* self, ICorJitInfo* info,
                               CORINFO_METHOD_INFO* methodInfo, unsigned flags,
                               uint8_t** nativeEntry, uint32_t* nativeSize) {
    static thread_local int entrancy = 0;
    if (entrancy > 0) return g_realCompileMethod(self, info, methodInfo, flags,
                                                 nativeEntry, nativeSize);
    ++entrancy;
    DWORD lastError = GetLastError();          // the runtime owns this slot; do not clobber it
    CorJitResult result;
    __try {
        result = g_realCompileMethod(self, info, methodInfo, flags, nativeEntry, nativeSize);
        if (result == CORJIT_OK) OnCompiled(self, info, methodInfo, *nativeEntry, *nativeSize);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        result = CORJIT_INTERNALERROR;         // never let an SEH exception escape into the JIT
    }
    SetLastError(lastError);
    --entrancy;
    return result;
}
```

Four disciplines are load-bearing, and each one exists because a real implementation was bitten by
its absence:

| discipline | why |
|---|---|
| **Call the real JIT first** | our own hook body gets JIT-compiled; if we filter before delegating, we deadlock or recurse |
| **re-entrancy guard** | the same; MonoMod keeps a thread-static depth counter for exactly this |
| **save/restore the last-error slot** | the runtime stores P/Invoke state there — a hook that P/Invokes and clobbers it has caused real `FileStream` crashes |
| **`__try`/`__except` around everything** | a native exception unwinding into the JIT is unrecoverable |

### 4.3 Identifying the target methods

The assembly is obfuscated, so **names are not an identity**. Three identifiers are available, in
increasing robustness:

1. **`CORINFO_METHOD_INFO.ftn`** — a `MethodDesc*`, fine as a dictionary key, but not stable across
   a restart and not meaningful across a build.
2. **the metadata token**, via `ICorJitInfo::getMethodDefFromMethod`. *Caveat*: the vtable index
   for this (and for the module→assembly accessor) is **not** the value most samples hardcode; the
   current .NET 9 index for `getMethodDefFromMethod` is documented by MonoMod's generated header as
   `0x76`, while ManagedJit uses `105`. Treat both as unverified until the spike confirms the token
   against a method we already know by IL. **This is a spike item, not an assumption.**
3. **the IL bytes** — `ILCode`/`ILCodeSize` are handed to us directly. Hashing them and matching
   against an offline table extracted from `DarzasDominion.dll` is **name-independent, token-
   independent and build-specific in exactly the way we want**: if the game updates and a method's
   IL changes, the hash stops matching and we refuse to install rather than patching the wrong
   function.

**Design decision: match on IL hash, and carry the token and the `MethodDesc` as diagnostics.** The
IL hash is what makes the hook safe across game updates, and we already have the IL
(`hook/il/client-5.8.2.il`) to build the table from.

### 4.4 Patching the finished code

`OnCompiled` must patch *after* the EE has copied the JIT's writable buffer into executable memory,
because that copy happens after `compileMethod` returns. Two consequences:

- the address we should patch is the one the EE will *execute*, not the RW buffer, so we patch
  lazily — either on the first observed call into the target, or from a short-lived deferred queue —
  rather than writing to `nativeEntry` inside the detour;
- on .NET 8+ there is an optional W^X mode (`System.Runtime.CompilerServices.EnableWriteXorExecute`)
  which changes how code pages are double-mapped. The app does not set it, so we get the default,
  but the patch helper should still `VirtualProtect` the address it is about to execute rather than
  assuming a particular mapping.

For four detours, a hand-rolled trampoline is tempting; **use MinHook**. It handles the cases that
are tedious to get right — a prologue too short for a 5-byte jump (JITted methods are frequently
tiny; one of `clrjit.dll`'s own vtable entries is three bytes), and non-atomic patching observed by
another core — and its documented pattern is to queue the patches and apply them in one pass that
suspends and resumes all threads once.

---

## 5. Reading managed state safely

This is the section where a hook that works in a spike becomes a hook that does not crash after
twenty minutes of play.

### 5.1 The object layout, stated once

On x64, relative to the **managed reference**:

| offset | contents |
|---|---|
| `−8` | object header (sync block) |
| `0` | MethodTable pointer |
| `+8` | first instance field |

`MIN_OBJECT_SIZE` is 24 bytes. During a GC the MethodTable pointer's low bits are used as mark bits,
so any reader that might sample during a marking phase must mask the low 3 bits before
dereferencing. Reference fields are plain 8-byte pointers.

### 5.2 The three rules that keep us alive

1. **Never cache a raw object pointer across an allocation.** Re-read the root every frame. This one
   rule removes the overwhelming majority of the danger, and it is cheap: the root is one field
   read.
2. **Hold long-lived references in a `GCHandle`, not a raw pointer.** The runtime updates a handle's
   contents when the object moves, so a `GCHandle` stays valid across a compacting GC while a raw
   `Object*` does not. *Not yet implemented:* Phase 1 resolves `World.Shared` by taking the address
   of the static field (`ldsflda` through a `DynamicMethod`) and dereferencing it each frame, which
   is equivalent for a static field of a non-collectible class but is not a handle, and the header
   that publishes it says so. A strong handle resolved by the native side is Phase 2's shape.
3. **Read managed state from native code, on the game's own thread, with no managed call.** This rule
   said the opposite until it was measured, and the measurement killed the process: a **call** detour
   on a managed method runs on a thread that is already *inside* managed code — the detour sits
   between the managed caller and the callee's body — so a reverse P/Invoke from there is illegal.
   coreclr refuses it in `ReversePInvokeBadTransition`, which fail-fasts with
   `COR_E_EXECUTIONENGINE` (0x80131506) through `TerminateProcess`, raising nothing an in-process
   handler can see. Six dumps across three builds, always at `coreclr+0x244839`, always with the
   detour on the faulting stack. `PHASE1-HANDOFF.md` §13 and
   [MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md) carry the evidence and the open question.
   
   What is safe is what the old rule already relied on: that thread **is** in cooperative GC mode, so
   it cannot be suspended mid-read and the GC cannot move the objects a raw read walks. Re-read the
   root, guard the object identity against the type's MethodTable (masking the low three mark bits),
   bound the values, and wrap the dereferences in `__try`. That is what `FrameTickDetour` does; no
   allocation, no transition, no compile, and no ABI boundary.
   
   For anything that must *run* managed logic on the game's thread — calling `RequestEscape`, walking
   a pooled collection — there is no working mechanism yet. Entering managed code from that thread is
   the thing that is refused, so the candidate is a **jump** detour into a managed replacement (which
   the thread enters as a managed call and never leaves cooperative mode), not a call detour into a
   managed callback. It is unresearched, and the preemptive-mode hazard in its obvious form is
   recorded in MANAGED-ENTRY-HANDOFF.md.

### 5.3 Which rule applies to which hook

| hook | where the reads happen | why it is safe |
|---|---|---|
| frame tick (`DarzaWindow.Update`) | dereference the `World.Shared` static field, then read a handful of `Player` floats | native, no managed call; one dereference + pure float reads, no allocation |
| spawn (`Projectile.ProcessProjectileData`) | **arguments only** | the threat arrives in registers and on the stack; no dereference of anything |
| collision (`Projectile.HitCharacter`) | `this` + the player's position | `this` is already on the stack as the first argument |
| escape (`World.RequestEscape`) | call it, do not read | the client's own gates run |

**The spawn hook reading only arguments is the strongest safety property in the design.** It means
the highest-frequency hook in the system touches no managed field at all, so the pooling hazard
(§5.4) and the GC hazard cannot affect it.

### 5.4 The pooling hazard, and why identity matters

`Projectile` is pooled — `Rent`/`Reset`/return — and a re-rented instance gets a **new `GameId`**.
Any planner state keyed on the object's address silently inherits a dead bullet's history. The
symptom is "the dodge occasionally reacts to bullets that do not exist", which is very hard to
diagnose from behaviour and trivial to prevent: **key all per-bullet state on `(GameId, startTime)`,
and treat a `Rent`/`Reset` observation as an explicit invalidation.** This is a test, not a
convention — see [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §T3.

### 5.5 Resolving field offsets

Offsets must be **measured at startup on every run**, never baked in, because a game patch changes
them. Four ways to get them, best first:

1. **Ask the JIT while we are inside it.** `ICorJitInfo` exposes `getClassNumInstanceFields`,
   `getFieldInClass`, `getFieldOffset`, `isFieldStatic`, `getStaticBaseAddress` and more. This is
   the runtime's own answer for the exact type and the exact instantiation, and we are already
   positioned to call it. The `getFieldOffset` slot is documented by MonoMod's .NET 9 header as
   `0x5E` — again, *verify, do not assume*.
2. **Measure from managed code once.** `Marshal.OffsetOf` for blittable layouts, or `Unsafe.ByteOffset`
   against a live instance. Boring, reliable, and it covers the dozen `Player` scalars we need.
3. **Read them from the IL offline** (`ldfld` token → `FieldDef` → declaration order) as a
   *cross-check only*. CoreCLR computes instance layout in the type loader and is permitted to
   reorder; reference fields are grouped so the GC can scan a contiguous prefix. Do not derive an
   offset from declaration order and trust it.
4. **In-process DAC** (`CLRDataCreateInstance` from the shipped `mscordaccore.dll`, whose export is
   [verified] present, as is `coreclr.dll`'s `g_dacTable`). This is the most powerful reader
   available and it is worth a spike — *but* the same class of tooling documents that it normally
   requires a suspended target, so whether it attaches to a live unsuspended process is an open
   question. Treat it as a bonus, not a dependency.

**And the offsets we actually need are few.** Because the threat arrives as arguments at the spawn
hook and as `this` at the collision hook ([CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §9.4), the field
reads reduce to a handful of `Player` scalars — position, the HP family, armour piercing, the status
effect presence — plus `World.Shared` itself.

---

## 6. Version tagging: making a game update fail loudly

Two facts make this a first-class requirement rather than a nicety:

- the two shipped apphosts carry **different `DarzasDominion.dll` builds**;
- any patch can change IL, field offsets and method addresses simultaneously.

The hook therefore refuses to install unless a fingerprint matches:

```
fingerprint = {
    assembly size  (e.g. 50,216,960)
    assembly MVID
    a set of IL hashes for the ~4 target methods
    coreclr.dll / clrjit.dll file version (9.0.2026.41315)
    the JIT/EE GUID read from clrjit vtbl[2]
    the effective TieredCompilation value
}
```

On mismatch: log one line naming which component differed, install nothing, and let the game run
normally. **A hook that does nothing is a far better failure than a hook that patches the wrong
method** — the latter corrupts a live session in a way that looks like a server bug, which is
precisely the failure class the existing `drelay` documentation warns about.

---

## 7. The frame budget: the real constraint

The hook runs **inside the game's frame**. Every microsecond it spends is a microsecond of frame
time the game does not get, and the game's own `UiThreadWatchdog`/`StallProbe` machinery already
watches for exactly this. Three budgets, nested:

| budget | value | source |
|---|---|---|
| the game's frame period | **measured: the game runs its frame loop uncapped at roughly 200 Hz (~4.9 ms per frame) at the title screen**; the design target is 60 Hz (16.7 ms), which is the conservative design point rather than the observed worst case | measured from the frame detour's own argument (`GameTime.TotalGameTime.TotalMilliseconds`, advancing ~4.9 ms per call), via `frames.csv`'s `arg_total_ms`/`frame_delta_ms` columns. The operator caps FPS in the game's settings, and the high-rate/uncapped limit test is deferred ([CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §10 #4) |
| our self-imposed ceiling | **≤ 300 µs**, i.e. under 2 % of a 60 fps frame | design choice; the game's own watchdog tolerance is the input to be measured |
| the per-hook cost split | frame tick ≤ 150 µs; spawn ≤ 5 µs per bullet; collision ≤ 5 µs | derived from the algorithmic budget in [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §8 |

The frame loop is measured running uncapped at roughly 200 Hz, but the operator caps FPS in the
game's own settings and the design target is 60 Hz — so the **≤ 300 µs ceiling stays expressed against
a 60 Hz frame (16.7 ms)**, the frame the hook is actually built to fit inside. The uncapped
measurement is what makes that the conservative design point rather than the observed worst case.

Two features follow directly from the budget being a hard constraint:

**Degrade, do not skip.** If the planner's measured cost exceeds its slice, it drops **candidate
count** (the inner rings), never an admission test. This is the reference engine's discipline and
the reason is sound: a smaller candidate set is a worse dodge, whereas a skipped safety test is an
unsafe one. Never trade correctness for time.

**Never block on the Java relay.** The bridge ([JAVA-BRIDGE.md](../bridge/JAVA-BRIDGE.md)) is for
observation. A missing, stalled or dead relay must not change the hook's behaviour by a single
instruction, and the hook must never wait on it. The reference engine's worker-thread design is the
model: plain-data in, plain-data out, publication dropped on contention, and the game thread's own
reflex remains the safety floor.

---

## 8. Two things the hook must never do

1. **Never write `Player.Position` directly.** `World.cs:12756-12783` computes a position delta,
   feeds it through `Player.UpdateMoveVector`, assigns the position, and then feeds the *realised*
   displacement into the movement tracker that the outbound packets are built from. A raw write
   corrupts that history. The reference engine's warning is the same from the other direction: raw
   writes bypass the game's own movement path and produce server snap-backs. Use the input angle
   (§5.2b of [CLIENT-FINDINGS.md](CLIENT-FINDINGS.md)) or `World.RequestEscape()`.
2. **Never emit a packet directly while the Java relay owns the socket.** The relay already writes
   `GmEscape` with correct framing, atomicity and logging (`docs/hook/INJECTION.md` §1.4). Two writers on
   one stream is the failure the existing design went out of its way to prevent. The hook *decides*;
   the relay *writes*.

---

## 9. Startup self-checks (the hook's own acceptance test)

Run in order; any failure aborts the installation and logs one line.

| # | check | fails when |
|---|---|---|
| 1 | `clrjit.dll` and `coreclr.dll` file versions match the pinned build | the game updated its runtime |
| 2 | `getJit` found; `vtbl[0..3]` inside `clrjit.dll`'s `.text` | the JIT interface changed |
| 3 | GUID read via `vtbl[2]` matches the expected identifier | the vtable moved, or wrong object |
| 4 | effective `TieredCompilation` is off | an environment variable overrode the app; **the whole design depends on this** |
| 5 | the loaded `DarzasDominion.dll` fingerprint matches | wrong apphost, or a game patch |
| 6 | every target's IL hash found in the compilation stream within N seconds | a target was inlined, renamed-by-rebuild, or removed |
| 7 | each target's field offsets resolved to plausible values | layout changed |
| 8 | the bridge handshake succeeded (or was explicitly disabled) | relay not running; **not fatal** |

Check 6 is the subtle one. A method that the JIT never reports was either never called or was
inlined into its caller. The response is to fall back to a documented alternative target
([CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §9.3 already gives a fallback for each), and to **log
which target was missed** — because "the hook installed but three of four detours never fired" is
otherwise indistinguishable from "the dodge is bad".

---

## 10. What this design deliberately does not do

- **No IL rewriting.** The methods are flattened state machines from a decompiler; rewriting their
  IL to inject a call is a large, risky job for no gain over a native detour.
- **No hooking of `System.Private.CoreLib`.** It is ReadyToRun and tiering is off, so those methods
  are never JITted and a `compileMethod` hook cannot see them. Nothing in the design needs them.
- **No manual mapping / reflective loading.** It buys nothing here and costs the PE loader, TLS
  callbacks, exception-directory registration and a module list that disagrees with the runtime's.
- **No driver.** Everything runs in user mode at the user's own privilege level.
- **No modification of the game's files** in the recommended path (§3.2). If §3.1's proxy DLL is
  chosen, that is the one place this changes, and it should be an explicit decision.
