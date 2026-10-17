# HANDOFF — the `TS_Hijacked` crash: on this machine the hook kills the game

**Written:** 2026-10-17, after a live bisection session on the operator's Windows 11 desktop.
**Branch this work belongs on:** `fix/ts-hijack-crash` (not merged — see §9).
**Read this first if you are picking the work up cold.** Everything below is measured on
this machine unless it says otherwise. The point of the document is that the next session
does not re-run the twelve experiments this one ran, and does not misread `crash.txt` again.

---

## 0. The brief

The 0.1.1 release runs on the developer's machine and **not on this one**. Here the game is
killed by the .NET runtime four to fifteen seconds after launch, on roughly **four launches
in ten**, while the game alone is completely stable. The failure is a CLR internal
assertion — `pThread->HasThreadState(Thread::TS_Hijacked)` — followed by a fail-fast
(`0xC0000602`) out of `coreclr.dll`.

It is **not** the target patch, **not** the `compileMethod` detour, **not** auto-aim, **not**
the config, and **not** the relay. It is the **managed bootstrap being resident in the
process**: with `--off` (nothing managed injected) the game is stable 3/3; with the
bootstrap loaded it dies about half the time, whatever else is switched off.

**Done means:** a build on this machine survives the startup/loading-screen window in the
default configuration (auto-aim + dodge on) on **five consecutive launches** and reaches a
world with the frame read armed, and the reason it used to fail is written down in the
documents (§8). Then push the branch (§9) and stop.

---

## 1. The failure, exactly

**Symptom.** The launcher installs the hook, logs `PATCHED 'frameTick'`, and a few seconds
later the game is gone. A `crash.txt` appears in the run directory. `hook.log` stops
mid-run — no `phase 1 complete`, no `summary.txt`, no `offsets.csv`.

**The truth is in the launcher's console** (it echoes the hook log live and captures the
child's stderr):

```
CLR: Assert failure(PID 13440 [0x00003480], Thread: 9864 [0x2688]):
     pThread->HasThreadState(Thread::TS_Hijacked)
    File: D:\a\_work\1\s\src\coreclr\vm\excep.cpp:6524
```

and in Event Viewer → Windows Logs → Application:

```
Application Error, DarzasDominion.exe
Faulting module: coreclr.dll, version 9.0.2026.41315
Exception code: 0xC0000602
Faulting offset: 0x0000000000321531
```

`excep.cpp:6524` is the runtime's own sanity check on the **return-address hijack** path —
the mechanism it uses to stop a thread that is in cooperative GC mode. `0xC0000602` is
`STATUS_FAIL_FAST_EXCEPTION`: an `_ASSERTE_ALL_BUILDS`, so it is fatal even in a release
runtime.

**`crash.txt` is not this death.** It records `exception 0x80000033 unclassified` at an
address in `System.Private.Xml.dll`. `0x80000033` is `STATUS_RETURN_ADDRESS_HIJACK_ATTEMPT`
— the runtime's own signal that a hijacked thread reached a safe point. A vectored handler
sees it and records it; it is a normal step, not a fault. The runtime terminates through
`TerminateProcess`, so the handler cannot see the real death at all. This trap has now cost
this project time in three separate rounds; `TROUBLESHOOTING.md` §2 is the short version.

**The failing thread is a CLR threadpool worker in cooperative GC mode**, allocating, on the
loading screen (`DarzasDominion.Data.DataManager.ParseCachedXmls` → `Parallel.For` →
`XElement.Load`). Verified from a minidump (`clrthreads` → `MTA (Threadpool Worker)`,
`Cooperative`, non-empty GC alloc context).

---

## 2. What has been measured

Every trial below is a fresh launch of `DarzasDominion.exe` through
`Drelay.Launcher.exe`, observed for **25–30 s** and then killed. "Survived" = still running
at the end of the window. Steam was running; one game process at a time; ~2 s between
trials. Two control rows at the top are the only ones that matter for reading the table.

| configuration | trials | survived |
|---|---|---|
| **game alone, no hook at all** | 7 | **7** |
| **`--off`** (native DLL injected, nothing managed, nothing patched) | 3 | **3** |
| stock 0.1.1, default config (`AutoAim: closest`, `Dodge.Act: true`) | 3 | 0 |
| stock 0.1.1 + `DRELAY_NO_PATCH=1` (JIT detour installed, nothing patched) | 1 | 0 |
| stock 0.1.1 + `DRELAY_FORWARD_ONLY=1` (detour reduced to a pure forwarder) | 3 | 1 |
| stock 0.1.1 + `DRELAY_SKIP_JIT_DETOUR=1` (no JIT detour at all) | 3 | 2 |
| stock 0.1.1 + `DRELAY_SKIP_RESOLVE_CALLBACK=1` | 3 | 1 |
| local build + `DRELAY_NO_ARM_PROBE=1` (no reverse P/Invoke from the watcher) | 3 | 1 |
| stock 0.1.1 + `AutoAim: null`, `Dodge.Enabled: false` | 3 | 2 |
| stock 0.1.1 + `DOTNET_gcConcurrent=0` | 7 | 5 |
| local build with the §7 hardening, default config | 5 | 2 |

Pooled over every configuration that has the managed bootstrap loaded: **16 of 36 launches
survived** (≈44 %). Every individual difference above is inside the noise of that rate —
which is the actual result of the bisection: **no single switchable feature owns this.**

### Why the numbers are phrased that way

The failure is a **race**, so a single clean 30 s run proves nothing. Three of this session's
"fixes" looked good on one trial and were dead on the next:

- `DRELAY_FORWARD_ONLY=1` survived 35 s on the first trial → died 12 s and 11 s on the next two;
- `DRELAY_NO_TOKEN_PROBE=1` survived 45 s once → never re-tested above n=1;
- `DOTNET_gcConcurrent=0` survived 3/3, then 2/4 → pooled 5/7, i.e. noise.

**Minimum useful evidence is 5 trials of the same configuration, and a change is only
interesting if it goes 5/5 where the baseline is ~2/5.** Do the baseline first, in the same
sitting, so the machine's mood is held constant.

---

## 3. What that rules out

Ruled out by measurement, and not worth re-testing:

| hypothesis | killed by |
|---|---|
| the `frameTick` / target patch corrupts the game | `DRELAY_NO_PATCH=1` still dies |
| the `compileMethod` detour is fatal | `DRELAY_SKIP_JIT_DETOUR=1` still dies |
| the detour *body* (hashing, logging, token probe) is fatal | `DRELAY_FORWARD_ONLY=1` still dies |
| the repeated reverse P/Invoke of the arming probe | `DRELAY_NO_ARM_PROBE=1` still dies |
| the resolve callback's managed work on the watcher thread | `DRELAY_SKIP_RESOLVE_CALLBACK=1` still dies |
| auto-aim / dodge work | config with both off still dies |
| background GC | `DOTNET_gcConcurrent=0` pooled 5/7 ≈ baseline |
| the release binaries are stale or mismatched | a local build from `master` reproduces identically |
| the game is broken on this machine | game alone 7/7, and it is the same build that runs on the other machine |

One thing this **does** incidentally prove: `known-build.json` is right for this install.
The pinned `frameTick` matches by IL hash on every run (`token 0x06001FC5`), including with
the rewritten SHA-256 in §7 — so the identity layer is not implicated and the digest is
still FIPS-correct.

**What it leaves:** the crash appears when the managed bootstrap runs at startup, and is
independent of everything the bootstrap *does* that can be switched off from outside. Two
candidate shapes remain:

- **(A) the startup-hook phase itself** — the work `StartupHook.Initialize()` performs
  before `Main` (assembly load + reflection over a 50 MB assembly, emitted `DynamicMethod`
  accessors, `RuntimeHelpers.PrepareMethod`, the 16-assertion sensor self-test, the
  `SteeringPilot` warm-up);
- **(B) the mere presence of a startup-hook assembly in the process** — in which case the
  fix is architectural, not a code path.

§4 splits (A) from (B) in one experiment.

---

## 4. The next experiment, in order

### E1 — make `StartupHook.Initialize()` do almost nothing (decisive)

`hook/managed/Drelay.Bootstrap/StartupHook.cs`, as of the commit this document is on:

| member | line | what it is |
|---|---|---|
| `Initialize()` | ~718 | the startup-hook entry point; thin wrapper over `Run()` |
| `Run()` | ~735 | **everything the startup phase does** |
| `OnProbe()` | ~162 | `[UnmanagedCallersOnly]`, called by the native watcher |
| `OnResolve()` | ~508 | `[UnmanagedCallersOnly]`, called by the native watcher ~100 ms in |
| `ResolveOffsetsAfterStartup()` | ~967 | the reflection/offset work, already deferred to the watcher |
| `OpenSharedBlock()` / `PublishTargets()` / `GetOrCreateBootEvent()` | ~1960 / ~2026 / ~1947 | the minimum the native side needs to proceed |

**Change:** leave in `Run()` only what the native side cannot start without —

1. `OpenSharedBlock()`,
2. `PublishTargets()` (this reads the *generated* table, not the game assembly),
3. the boot event signal,
4. `PublishFrameReplacement()` / `PublishFrameHelper()` **only if** the native side reads
   those addresses before `OnResolve` — check `DrelayMain.cpp`; if it does, publishing the
   addresses is cheap (they are already-emitted `[UnmanagedCallersOnly]` stubs) and can stay,

and move **everything else** into `ResolveOffsetsAfterStartup()`, which the native side
already calls from the watcher thread once the runtime is fully up: the runtime-config
check, the game-assembly identity check, the offset resolution, the accessor emission, the
sensor self-test, and `SteeringPilot.Warm()`.

**Why this is the right split, not just the cheapest one.** The project's own record says the
watcher thread is the safe place to enter managed code from ("a genuine unmanaged thread —
so its reverse P/Invoke is exactly what the runtime expects", `StartupHook.cs` ~L42-48), and
`ResolveOffsetsAfterStartup` already exists precisely because the startup-hook thread could
not be blocked. If (A) is the answer, this change *is* the fix. If (B) is the answer, the
experiment costs one build and one round of trials and rules the whole class out.

**Gate it behind an environment switch while you measure** — `DRELAY_MINIMAL_STARTUP=1` (a
plain `DRELAY_` name: the launcher deletes every `DRELAY_HOOK_*` from the child's
environment, which is a trap this project has hit three times). Then both shapes can be
compared on one binary, in one sitting, alternating, which is the only way to keep the race
honest.

**Read it as:**

- 5/5 survive with the switch and ~2/5 without → **(A)**, and the switch's shape is the fix.
  Land it as the default, keep the old path behind `DRELAY_FULL_STARTUP=1` for one release.
- still ~2/5 with the switch → **(B)**. Do not keep digging in the bootstrap. Go to E2.

### E2 — if E1 says (B): find the smallest thing that still triggers it

Only if E1 fails. The point is to find *what* about having the bootstrap loaded matters, not
to keep bisecting features:

1. `DOTNET_STARTUP_HOOKS` pointing at a **trivial** assembly (one that only opens the shared
   block and signals the boot event) — does the game survive? This splits "a startup hook at
   all" from "this startup hook".
2. If a trivial hook is also fatal, the problem is the startup-hook mechanism on this
   runtime/OS, and the architectural options are: load the bootstrap from the native side
   after `Main` starts (the runtime is fully up by then, and `Drelay.Hook.dll` already runs
   before `Main`), or accept the limitation and document it. **Do not** start a third
   mechanism without measuring (1) first.
3. If a trivial hook is fine, re-add the `Run()` steps back one at a time, in this order
   (most suspicious first, because each one JITs or emits code during startup):
   `SensorSelfTest` → `SteeringPilot.Warm` → accessor emission (`FieldAccessor`) →
   `ResolveOffsets` reflection → `CheckGameAssembly` (reads a 50 MB file) →
   `CheckRuntimeConfig`.

### E3 — confirm the machine split (cheap, and it decides what the fix *is*)

Run the **unmodified 0.1.1 release** on the other machine, 5 launches, 60 s each. If it is
5/5 there, the deliverable for this machine is a defensive fix (E1) and the documents must
say so; if it also fails there, this is a plain regression and the bisection above is the
whole map.

### E4 — the mechanism, if you want it (optional, parallel)

The managed stack of the fail-fast thread is empty in the two dumps available: the thread
unwinds before the assert. To get a stack, hook it earlier rather than later:

- `DOTNET_DbgEnableMiniDump=1`, `DOTNET_DbgMiniDumpType=4` (full memory),
  `DOTNET_DbgMiniDumpName=<run dir>\clrdump.dmp` in the launcher's environment, or the
  launcher's own `--dbg-dump`;
- then `dotnet-dump analyze <dmp>` and `clrthreads` → the thread with `Cooperative` + a
  live alloc context. **`setthread` takes the `DBG` column, not the OSID** — this cost an
  hour in the session that wrote this;
- the hook's own VEH already sees the `0x80000033` record; `DrelayCrash.cpp`'s raw stack
  scan stops at the first unreadable slot, so widening it (and re-snapshotting modules in
  the handler) will show more of the managed frames than the current report does.

---

## 5. How to measure

The harness this session used is outside the repo, at `C:\Users\isvac\drelaysuite\work\`:

| file | what it does |
|---|---|
| `run-hook-test.ps1` | one launch with an env-var set and/or launcher args; reports survived/died and the event-log entry |
| `trials.ps1` | N repeats of the above, prints a summary line |
| `mdinfo.py` | minimal minidump reader — exception code, tid, module, raw stack scan |
| `dumps\` | the three dumps from this session plus `latest.dmp` |

They are scratch quality. If you keep them, move them into `hook/tools/` and make the paths
relative; if you would rather not, a single trial is:

```powershell
# one trial: env for the child, then watch. Steam must already be running.
$env:DRELAY_MINIMAL_STARTUP = '1'      # anything NOT starting with DRELAY_HOOK_ reaches the game
Get-Process DarzasDominion -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$t0 = Get-Date
$p = Start-Process 'C:\Users\isvac\drelaysuite\drelay-hook-0.1.1\Drelay.Launcher.exe' -PassThru -NoNewWindow
Start-Sleep 25
"survived: $(-not $p.HasExited)"
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$t0} |
  Where-Object { $_.Message -match 'DarzasDominion' -and $_.Id -eq 1000 } |
  ForEach-Object { ($_.Message -split "`n" | Select-String 'Undantagskod|Exception code') }
Get-Process DarzasDominion -ErrorAction SilentlyContinue | Stop-Process -Force
if (-not $p.HasExited) { $p.Kill() }
```

Build with `.\hook\build.ps1` (~2 min, both halves). It needs VS2019 (`vswhere` finds it)
and `dotnet` 8.0.402, and restores the net9.0 reference pack from NuGet. Run the launcher
from `hook\out\` to test your build, and from `C:\Users\isvac\drelaysuite\drelay-hook-0.1.1\`
to test the shipped one.

### Acceptance criteria for the fix

1. **Five consecutive launches**, default config (`AutoAim: closest`, `Dodge.Act: true`),
   each alive for **60 s** after the launcher reports `phase 1 complete`, with
   `summary.txt` written — zero `0xC0000602`.
2. **At least one run with the character in a zone**: `spawn` and `collision` found and
   patched, the frame read `ARMED`, `frames.csv` non-trivial.
3. `.\hook\build.ps1` clean, and `Test-HookAbi.ps1` reports `ABI SELF-TEST OK`.
4. **`Test-HookOffline.ps1` is not a usable gate on this machine** — it dies with a
   fail-fast immediately after `ABI SELF-TEST OK`, with the stock DLL *and* with a fresh
   build, so it is pre-existing and not a regression. Fixing it is out of scope; do not
   spend the session on it, but do say in the commit message that it was not run to
   completion and why.

### One more failure mode to expect, so you do not chase it

The 17:04 run — the one run this session got past the loading screen with the stock build —
survived **three and a half minutes** and then died a *different* death: an unhandled
managed `System.ArgumentOutOfRangeException` ("Index was out of range", parameter `index`)
inside `DarzasDominion.Graphics.DarzaWindow.Update` → `hehe(SKBitmap, Rectangle, Int32)`,
i.e. the SkiaSharp glyph blit. `.NET Runtime` event id 1026 at `17:08:05`. That is a
**separate, game-side drawing crash**, it produces no `crash.txt` and no `0xC0000602`, and it
is not what this handoff is about. If your fix works, you will meet it.

---

## 6. Traps

Each of these cost this session real time.

1. **Do not read `crash.txt` as the cause.** `0x80000033` is the runtime's hijack signal.
   The death is `0xC0000602` in `coreclr.dll` and it is only visible in the launcher console
   and the event log. `TROUBLESHOOTING.md` §2.
2. **`jit-internal.txt` with `0x02345678` is not a crash** either — it is the JIT's private
   retry channel and it appears in runs from before this hook existed.
3. **The launcher deletes every `DRELAY_HOOK_*` variable from the child's environment.**
   Operator switches must be named `DRELAY_*`. (This has now bitten the project four times.)
4. **`Start-Process -ArgumentList @()` throws** when the array is empty, and the script then
   reports "the game died in 1 s" — which reads exactly like the bug under investigation. A
   closed-over empty array silently ate two rounds of trials in this session. Use a splat and
   only add `ArgumentList` when it is non-empty.
5. **`dotnet-dump analyze` → `setthread <n>` takes the `DBG` column**, not the OSID.
6. **A single survival is not a result.** See §2. Five trials minimum, baseline first.
7. **`--off` still injects the native DLL**, so it is the right control for "the native half
   is fine". It is not a working hook and must never be presented as one.
8. Kill stray `DarzasDominion` processes before every trial — a leftover process makes the
   next launch look instant-successful, or makes Steam relaunch the old one.
9. The game needs ~2 s between launches, and Steam must be running, or the crash under
   investigation is masked by a different failure.

---

## 7. State of the working tree

Two native files are already modified, and they are **hardening, not the fix**. They were
measured and they do not remove this crash (5 trials, 2 survived, same as baseline). Keep
them or drop them — if you keep them, say in the commit message that they are hardening.

- `hook/native/DrelayLog.cpp` — `Emit` no longer takes an SRW lock: the ring slot is
  reserved with `InterlockedIncrement` and the reader treats a line as readable only when
  `line->seq` is the number it expects. `DrLog` is reachable from inside `compileMethod`
  (first entry, target match, slot probe) and locking there is a real hazard. The near-miss
  line ("IL size N matches target … but the hash does not") is now emitted by
  `DrLogFlushObserved` from the flusher thread instead of from the detour, wording unchanged,
  announced once per newly stored hash.
- `hook/native/DrelayInstall.cpp` — `Sha256` is a self-contained FIPS SHA-256 instead of
  five CryptoAPI calls per hash; `CryptAcquireContextW` can load a provider DLL under the
  loader lock, and this runs on the JIT thread. The digest is unchanged (the pinned
  `frameTick` hash still matches — verify this on the first run after you touch it). The
  detour also no longer calls `DrLogFlushNow` on first entry.

`TROUBLESHOOTING.md` is new at the repo root: the operator-facing symptom → fix document,
including the `crash.txt` trap and the ruled-out table from §3.

Not changed, and still true: `hook\version.txt` is `0.1.1`. **Bump it to `0.1.2`** as part
of closing this, so a build on the other machine can be identified from `VERSION.txt` alone.

---

## 8. Close-out checklist

- [ ] E1 run: 5 trials with the switch, 5 without, same sitting, numbers in the commit message
- [ ] the fix landed as the default (or the "this machine cannot run it" conclusion written
      into `docs/hook/RISKS.md` if E1 says (B) and E2 finds nothing actionable)
- [ ] `hook\version.txt` → `0.1.2`
- [ ] `docs/hook/HOOK-BUILD-MATRIX.md` updated if the startup phase's contents changed
      (it is the reference a maintainer uses after a game patch)
- [ ] `hook/README.md` document table gets a row for this handoff, and for
      `TROUBLESHOOTING.md` if it is not already referenced
- [ ] `TROUBLESHOOTING.md` §1's "relaunch" workaround replaced with the real fix once it is
      known, or kept with the conclusion that it is the only workaround
- [ ] the acceptance criteria in §5 met **on this machine**, with the run directories named
- [ ] `Test-HookOffline.ps1` noted as not-run-to-completion, with its pre-existing fail-fast

---

## 9. The final step: push a branch, do not merge

The operator tests on the other machine, so the result travels as a branch. **Do not merge
into `master` and do not tag a release** — `hook-v0.1.2` only once the other machine has
been tried.

```powershell
cd <repo>
git checkout -b fix/ts-hijack-crash      # already the branch this document is committed on
git add -A
git commit -m "fix(hook): ..."           # one line, then the measurements from §5
git push -u origin fix/ts-hijack-crash
```

Then report, in this order:

1. the branch name and the commit;
2. the before/after trial numbers from §5 (5 trials each, not one);
3. the run directories that met the acceptance criteria;
4. what changed and why, in one paragraph, naming the mechanism if E1/E2 established it;
5. anything the operator must do on the other machine (`git fetch`, `git switch`, rebuild
   with `.\hook\build.ps1`, run from `hook\out\`).

If the session runs out of time before the fix is verified, **still push the branch** with
whatever is established, and say plainly in the report which of the §5 criteria are met and
which are not. A pushed branch with an honest "2 of 5" is worth more than an unpushed one
with a claim.
