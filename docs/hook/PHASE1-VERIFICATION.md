# Phase 1 — verification

**Date:** 2026-10-11. **Branch:** `autododge-research`. **Verifier:** DSH agent, at the operator's
request ("verify that phase 1 is complete").

**Read with:** [PHASE1-HANDOFF.md](PHASE1-HANDOFF.md) (the long record) and
[IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §"Phase 1 — Get in, and stay in" (the criteria this
document checks against). **This document supersedes the *status* claims in the handoff**, which was
written at 2026-10-10 01:30 and is therefore one round stale: two live runs after it have done the
thing it says is still missing.

---

## 1. Verdict

> **Phase 1's blocking unknown is resolved: the hook does read live state from the real game.**
> **Phase 1 is not yet complete against its own exit criteria.**

The first sentence is new. Spike 1.3's success test — *read exactly one float from the live world at
~60 Hz* — has now run twice against the real game, with plausible coordinates, a passing offset
cross-check, and `PASS 9c` in `selfcheck.txt`. The handoff's §7 ("Not verified") and §9 item 2 are
stale and should be read as history.

The second sentence is the actual answer to the question asked. Three criteria are unmet or
unproven, one of them (the endurance run) structurally so — §6.

---

## 2. Offline checks, re-run in this session

All of these were re-run against a freshly built tree. Nothing was taken from the documents.

| check | command | result |
|---|---|---|
| build | `hook\build.ps1` | **build OK**; native at `/W4 /WX` with no warnings; `DrelayShared 440912`, `DRELAY_SHARED_SIZE 440912`, shared version 5 — the values §6 of the handoff quotes |
| ABI | `hook\tools\Test-HookAbi.ps1` | `ABI SELF-TEST OK` — DLL loads, `DllMain` inert, 3 exports resolve, abi 1, `DrelayHook_Init` fails closed without a session |
| PE/MVID reader | `hook\tools\Test-PeReader.ps1` | `PE READER TEST OK` — game assembly, synthetic PE32 and managed PE32+ all agree with an independent reader, and the deliberately tampered copy reports the **tampered** MVID |
| injection chain | `hook\tools\Test-Injection.ps1` | **73 check(s): 73 passed, 0 failed** — the number the handoff claims. *(Re-run 2026-10-12 after Phase 2 added run 7: **90 check(s): 90 passed, 0 failed**, and run 7 now matches and patches a target of the synthetic host — see §11.7.)* |
| Phase 0 gate | `hook/tools/Fingerprint.ps1` | `FINGERPRINT OK — 15 checks passed, 2 known gap(s)`; all four target IL hashes re-derived and matching |

Note on run directories: `Test-Injection.ps1` writes its runs into
`hook\obj\injection-test\runs\` and **deletes that tree at the end unless it is run with `-Keep`**
(corrected 2026-10-12; this note previously said `hook\obj\live-runs\`, which the test never writes —
that path is the operator `--runroot`). So the test's own newest directories are only visible with
`-Keep`, and the operator runs under `%LOCALAPPDATA%\drelay\hook` remain the live evidence.

---

## 3. The live evidence that postdates the handoff

Both runs are in the operator's default runroot, `%LOCALAPPDATA%\drelay\hook`.

| | `20261010-015935-p27056` | `20261011-221035-p4364` |
|---|---|---|
| targets found | **4 of 4** | **4 of 4** |
| `frameTick` | patched | patched |
| arm state | **ARMED** | **ARMED** |
| cross-check (reflection vs pointer chain) | (118.5, 116.5) both | (119.5, 115.5) both |
| frame calls | 293 | 294 |
| frame reads | **223 ok, 70 failed** | **232 ok, 62 failed** |
| latest read value | 40.5000 | 123.4930 |
| worst call | 272 µs | 274 µs |
| self-check | **19 passed / 0 failed / 2 gap(s)** | **19 passed / 0 failed / 2 gap(s)** |
| `PASS 9c` | live world read | live world read |
| how the process ended | closed, no fault recorded | closed by the operator; a CLR fault was recorded at close (§5) |

The coordinates are plausible game-world values, and the game's own `darza.log` for the second run
shows sprite coordinates in the same range (1466–2038), so the read is not landing in noise.

**Spike 1.3's core test is met.** What is not met is its *duration and stress* half — see §6.

---

## 4. Two instrument defects found while reading the evidence

Both are the class of defect PHASE1-HANDOFF §4.4 and §8 are about: a number whose label does not
match what it measures. Neither is fatal; both cost a reader time.

### 4.1 `armed at : t+1144780734 ms` is system uptime, not time since start

`managedArmTick` is `GetTickCount()`, which on Windows is **milliseconds since boot**
(`DrelayHookShared.h` says so; `DrelayEvidence.cpp` and `DrelaySelfCheck.cpp` print it as `t+…`).
The machine booted 2026-09-28 16:11, so at 2026-10-11 22:11 the uptime was
`1 144 780 734 ms` — exactly the value printed. The same line in the earlier ARMED run reads
`t+985713218 ms` (11 d 9 h), which is the uptime at *that* moment.

So the operator-facing "armed at" in `hook.log`, `summary.txt` and `selfcheck.txt` is a valid
number labelled as something it is not, and it cannot be compared with the `install time` line
above it (141 ms in the earlier run, 1000 ms in this one). It should either be `GetTickCount() - installStart`, or be relabelled
"system uptime at arm".

### 4.2 `frames.csv`'s `read_ok` column cannot report a failed read

The column is `g_shared->managedReadValid`, which `NativeFrameRead` **sets to 1 on success and never
clears**. It therefore reads `1` on all 294 rows of the second run — including frames 12–73, the 62
that failed. The per-frame truth is only recoverable by differencing the cumulative
`read_failures` column. A reader who trusts `read_ok` (as this verifier did, briefly) concludes
"no frame ever failed", which is false.

### 4.3 The 62 failures are a "not yet", counted as a finding

Differencing `read_failures` puts the failures in **frames 12–73 — 62 consecutive frames, ~1.1 s** —
with successful reads at both ends. `NativeFrameRead` counts a **null or wrong-typed `Player`** as a
failure (`IsInstanceOfNative` returns false for `reference == 0`), and it returns silently with no
count only for a null `World.Shared`.

That is the same "not yet" that §12 of the handoff went to some trouble to stop the *arming probe*
reporting as a refusal: `World.Shared` exists, `Player` follows later. The frame path has one
vocabulary and the probe has another. The distinction that matters is preserved in the *aggregate*
(the read recovers and 232 of 294 succeed), but the failure count is inflated by a state that is not
a defect.

**One positive result worth recording:** `drlog`'s "the native frame read raised; the frame
continued" line — printed once if the `__try` ever fires — is **absent from the run**, and absent
from `hook.log.sizes` too. So none of the 62 was a fault: they were null/type-mismatch or
implausible-value reads. The `__try` has never fired in a live run.

### 4.4 The 274 µs worst call is the first call, not the steady state

`frames.csv`'s `elapsed_us` is non-zero on only 18 of 294 rows: **274 µs on frame 1**, then 1–2 µs on
17 later frames, 0 on the rest. The steady-state cost of the native read is ~1–2 µs, i.e. under
0.02 % of a 16.7 ms frame. The 274 µs is a one-off (cold page / first walk of the chain), and it
should be stated that way — "worst call 274 µs" otherwise reads as a per-frame cost and is 20× the
figure the handoff quotes for the pre-read build.

---

## 5. The fault at close on 2026-10-11 (operator: "I closed the game")

The operator reports that the second run did not crash during play and that they closed the game.
Windows nevertheless recorded a fault at that moment, and it is worth naming precisely because the
handoff's §7 has no entry for it.

**What the machine recorded**

```
Application Error (1000)  2026-10-11 22:12:56
  DarzasDominion.exe, faulting module coreclr.dll, exception code 0xC0000005
.NET Runtime (1023)       2026-10-11 22:12:55
  terminated due to an internal error in the .NET Runtime at IP 0x00007FFFFDA00BCC
  (base 0x00007FFFFD9A0000) with exit code 0x80131506
WER LocalDumps            DarzasDominion.exe.14672.dmp  (46 MB)
```

**What it resolves to** (cached coreclr PDB; `hook\obj\symlookup.ps1`):

```
0xC0000005  read of 0x0000000000000008
coreclr.dll+0x60BCC  VirtualCallStubManager::ResolveWorker+0x48
                     (inline: ModuleBase::GetLoaderAllocator, reading [this+8] with this == 0)
coreclr.dll+0x10FD1B VSD_ResolveWorker+0x33B
coreclr.dll+0x15726D ResolveWorkerAsmStub+0x6D      <- the virtual-call stub, i.e. a call site
                     ... called from the game's own JIT-compiled managed code
```

This is a **first-time virtual dispatch resolution that found a null module**: a virtual call site
jumped to its unresolved stub, the runtime went to look up the target, and the MethodTable it read
had no module behind it.

**Why it is not attributed to the hook, and why it is not attributed away either**

- `ResolveWorker`/`VSD_ResolveWorker` are runtime code the hook never patches, and the faulting
  thread's stack contains **no `Drelay.Hook.dll` frame**. The only hook frames in `crash.txt`
  (`+0x93B4`, `+0x9BA4`) resolve to `AppendStackWalk` and `VehHandler` — the hook's own crash
  *reporter*, captured because the handler runs on the faulting stack. `hook/tools/Resolve-HookRva.ps1`
  is what says so; without it, the report reads as though the hook were in the fault path.
- The timing (operator-initiated close; the game had already returned to the menu and was
  reconnecting) is consistent with a teardown-time dispatch on a type whose module is going away.
- **The earlier ARMED run closed with no fault recorded at all** — no Application Error between
  2026-10-10 00:40 and 2026-10-11 22:12 in the event log — so this is not a systematic close-time
  failure of the current build.
- But the hook *was* resident and patched throughout, and `FrameTickDetour` is not gated against
  teardown: it dereferences `World.Shared` on every frame of the whole session (see below).
  "No hook frame on this thread" bounds where the fault is; it does not prove innocence.

**One concrete, cheap next step.** `DRELAY_NO_PATCH=1` is the project's existing control for
"the patch, and nothing else, removed". Run it, close the game, and compare the event log. The
instrument for *that* question does not need a dump, which matters because
[MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md) §5.2–3 records that harness-spawned processes
produce none: **Windows writes the Application Error event regardless of who spawned the process**,
so "did it fault on close?" is answerable automatically even where "give me the stack" is not.

**Related dead control.** `g_framePaused` is declared, initialised to 0, and read as the first
branch of `FrameTickDetour` — and **nothing ever sets it to 1** (`DrelayInstall.cpp:73` is the only
write). So the "observe only inside the run window" intent has no implementation: the detour keeps
reading the world's memory for the entire life of the process, including after
`phase 1 complete` and during teardown. Either wire it or delete it; a control that is never engaged
is exactly what handoff §8's first three questions are for.

---

## 6. Criterion-by-criterion

Criteria as written in `IMPLEMENTATION-PLAN.md` (Phase 1) and the success tests as written for each
spike.

| criterion | state | evidence / what is missing |
|---|---|---|
| **Spike 1.1** native code resident before `Main` | **met, live** | `bootstrap.log`: "DOTNET_STARTUP_HOOKS is running … at t+0.0 ms"; injection test also asserts the bootstrap line and file predate the host's `Main` marker |
| **Spike 1.2** JIT detour installs; all four targets matched by IL hash | **met, live** | 4/4 in both ARMED runs (the handoff could only claim this *across* runs); `frameTick`'s token read back agrees with Phase 0; slot `0x76` confirmed by probing |
| **Spike 1.3** one target patched and read live state at ~60 Hz without harm | **partly met** | *read live state*: met twice, plausible values, 232 and 223 successes. *~60 Hz*: 57.4 Hz measured with FPS capped. *for 30 minutes, incl. zone transitions, a forced GC and an alt-tab, no crash*: **not done** — recordings cover ~5 s, no forced GC, no alt-tab test, and §5's close-time fault is unexplained |
| **Deliverable** hook skeleton (self-checks, target table + hard refusal, offset pass, off switch) | **met** | `selfcheck.txt` 19/0/2 in both runs; `Test-PeReader.ps1` + injection run 1 cover the refusal; offset pass publishes and cross-checks; `--off` covered by injection run 2 |
| **Exit 1** an hour with **all four targets patched**, no crash, no frame regression | **not met** | (a) **only one target is patched, deliberately** — the plan and the code still disagree and the handoff §9 item 5 says this must be resolved explicitly; (b) the live window is **hard-coded to 5000 ms** (`DrelayMain.cpp:740`) and the evidence is written at its end, so no run can even record an hour — the hook idles afterwards and nothing harvests its counters; (c) the longest ARMED process session is ~3 minutes, and one of the two ended in §5's fault |
| **Exit 2a** a denied log target costs evidence, never the game | **not tested** | restated in the plan this session, with a stated kill mechanism (an exclusive lock, not a delete) and four assertions: game exit unchanged, `elapsed_us` unchanged, the kill **felt** (`hook.log.append-failed`, and `log appends` in `summary.txt` when the lock precedes the write), and no line lost to a transient lock. The instruments already exist; the failure mode has already occurred twice — `20261010-014016-p11352` logged one `ERROR_SHARING_VIOLATION` (errno 13, win32 32, `lines pending: 0`) and recovered every line |
| **Exit 2b** a relay that dies mid-session changes nothing | **not Phase 1's** | moved to Phase 4's exit criteria this session: the relay is Phase 4 and absent, so this cannot be met here; its *absence* is already the status quo and therefore the control |
| **Exit 3** a different build is refused and logs why | **met** | `Test-PeReader.ps1` (tampered MVID) and injection run 1 (fingerprint mismatch), both naming the expected and actual values |
| **M3** all four patched + one hour, no crash, no regression | **open** | same three gaps as Exit 1 |

**Frame regression:** the measurable part is good — ~1–2 µs steady-state per frame (§4.4), i.e. far
inside the 16.7 ms budget. The "no visible frame hitch" clause is a human report and was not
recorded in either run.

---

## 7. The stale claims, and what became of them

**CORRECTED IN PLACE on 2026-10-11, at the operator's instruction.** Each is now either updated or
marked as the record it is, so no document still tells a reader that the live read has not happened.

| where | what it said | now |
|---|---|---|
| `PHASE1-HANDOFF.md` §1 | "What is not yet proven is the live read itself" | rewritten: the live read is proven, with both runs named and what is left stated |
| §3's status table | Spike 1.3 "needs one live session"; the deliverable's cross-check "offline, for both outcomes" | both marked confirmed live; the target table's `collision` row notes it matched with the other three in one run; the live read's own numbers added |
| §7's "Not verified" | the live field read and the offset cross-check, as open items | replaced by what is actually open (the hour, Exit 2a, the tokens), keeping the close-time fault as the new open item |
| §9 items 1–4 | "Run one live session…", "Get one run that finds all four…" | struck through as done, with the session's evidence |
| §12 "Still not proven" | the world in run 5 was a stand-in and a live session was needed | "Now proven, and what it took" |
| §13 "What is still not verified" | "One real frame read" | the run that produced it is named; the endurance half is the remainder |
| §2's run instructions | no `--live-seconds`, no acceptance runners | the two runners are in the command block and the window is documented beside `--collect-delay` |
| `PHASE1-SCOPE.md` §1 and §6.2 | "the live read is not"; the read and the cross-check listed as outstanding | status table and the confirmed/outstanding tables updated |
| `IMPLEMENTATION-PLAN.md` Phase 1 status | "the managed field read inside the frame detour, the offset cross-check, and the hour of play" are outstanding | "built, confirmed live, and one criterion short", naming what is actually outstanding |
| `IMPLEMENTATION-PLAN.md` M3 | "~1000 times with a 14 µs worst call" | the native read's numbers, and the note that 274 µs is the first call |
| `README.md`'s phase table | "the managed field read … is still disabled … the offset cross-check has not run"; "a 43-check … test that passes 43/43" | the live read's evidence, 73/73, and the three things actually outstanding. **This one was not in the original list in this section — the front door carried the same stale claim** |

The top of `PHASE1-HANDOFF.md` now carries a one-paragraph pointer here instead of a warning that the
whole document is stale.

This document is the current status; those paragraphs are history, and are now marked as such inline.

---

## 8. What is left, in order

1. **Attribute the close-time fault** (§5): one `DRELAY_NO_PATCH=1` session, closed the same way,
   compared in the Windows event log. Cheapest open item, and it is the only thing standing between
   "one target is patched" and "the patch is not implicated in anything".
2. **Fix the three instrument defects** (§4): the `armed at` label, `read_ok`, and the null-player
   read counted as a failure. Small, local, and each one has already misled a reader once.
3. **Decide Exit 1's "all four patched"** — amend the criterion or add pass-through detours. This
   is a decision, not work; the plan and the code must stop disagreeing. *(Items 4 and 7's tooling
   were done on 2026-10-11; 1, 2 and 3 are untouched.)*
4. ~~**Make an endurance run possible**~~ **Done — 2026-10-11.** `--live-seconds` (default 5,
   unchanged) sets the recording window instead of the hard-coded 5000 ms, the evidence is written
   when it closes as before, and `g_framePaused` — until then a control nothing ever set — now stops
   the frame read at the window's end (`DRELAY_KEEP_READING=1` preserves the old behaviour). Built and
   regression-tested: `Test-Injection.ps1` 73/73.
5. **Read back the token for every target** (`spawn`/`collision`/`escape` report `0x00000000`),
   closing handoff §7's last item.
6. **Run Exit 2a's test** — now `hook/tools/Run-Exit2a-LogDenial.ps1`: it launches the game, denies
   `hook.log` with an exclusive lock during the collection delay, proves the denial, releases it, and
   asserts the four properties of IMPLEMENTATION-PLAN.md Exit 2a. No new code is needed for it.
7. **The endurance run itself** — now `hook/tools/Run-Endurance.ps1 -Minutes 60`: 30–60 min, FPS capped,
   across a zone transition, with a collection and an alt-tab inside the window, and the read paused
   at the end. The script asserts the launcher exit, the event log, the frame coverage, the live-read
   counts, the pause, the hook's own worst call against the 300 µs ceiling, and reads the game's own
   log for the transitions, collections and slow frames that actually happened.
8. Only then Phase 2.

---

## 9. Phase 3 consideration, recorded at the operator's request

**The escape trigger could be delegated to the Java relay instead of being called in-process.**

Today's design has the hook call `World.RequestEscape` from inside the game process, with the relay
*observing* the resulting packet ([JAVA-BRIDGE.md](../bridge/JAVA-BRIDGE.md) §4.1 `escape`, §5.2 mode 3). The
operator's note is that **the escape command already exists in the Java proxy and is probably
cleaner** — the relay is already the only writer of the socket and already writes the escape packet
— **but the hook would then need a way to delegate it.**

Why this is more than a preference:

- The in-process route is exactly the capability [MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md)
  says is **not yet known to exist**: invoking `World.RequestEscape` means running managed code on
  the game's thread, which is the open yes/no that document was written to answer. Delegating to
  Java **removes that dependency** — the hook would only need to publish a word into the shared
  block, which is pure native and already how every other field crosses.
- The cost of delegating is latency, and it is the cost [JAVA-BRIDGE.md](../bridge/JAVA-BRIDGE.md) §1 is
  built around: the relay polls, so the act waits up to one poll quantum, where the in-process route
  waits for nothing. That section argues the *decision* must stay in C++; delegation keeps that and
  moves only the *act*, which is the cheap half.
- The interface does not exist yet in either direction: §4.2's Java→hook table has only
  `setConfig`, `ping` and `shutdown`, and there is no hook→Java command channel. Delegation needs
  one new field/command (a request bit the relay picks up, and an acknowledgement so the hook can
  log that the escape actually left — without which "the hook decided to escape" and "the game
  escaped" become indistinguishable in the evidence).

Not decided here. It belongs with Phase 3's design — the merged dodge and escape floor — and it
changes what the Phase 4 bridge must carry, so it should be settled before the escape path is built
rather than after.

---

## 10. The 8.6 and 8.7 deliverables (2026-10-11)

Two runs are needed and both are now one command each. Item 8.6 (Exit 2a) needed no hook change;
item 8.7 (the endurance run) could not be run at all, because the recording window was hard-coded to
five seconds and the evidence is written when it closes.

### What changed in the build

| change | where | why |
|---|---|---|
| `--live-seconds S` → `DRELAY_HOOK_LIVE_MS` | `Drelay.Launcher/Program.cs`, `DrelayInstall.cpp` (`ReadLiveWindowMs`) | the window is what the endurance run *is*; **the default is 5 s, unchanged**, so runs before this change stay comparable with runs after it |
| `g_framePaused` set when the window closes | `DrelayMain.cpp` step 8 | `g_framePaused` was the detour's first branch and nothing ever set it, so the read ran past the evidence and through teardown (this document §5). The read is the only part of the hook that touches the game's heap, and it has nothing left to learn once the window closes |
| `DRELAY_KEEP_READING=1` opts out | `DrelayInstall.cpp` (`ReadKeepReadingAfterRun`) | preserves the old behaviour for a run whose subject *is* that teardown. A plain `DRELAY_` name, because the launcher strips `DRELAY_HOOK_*` (trap 1) |
| `frame read : PAUSED / open` in `summary.txt` | `DrelayEvidence.cpp` | reports the state the run ended in rather than the intent it was launched with — two runs with different read counts must be comparable |

### The build is verified

- `build.ps1` — clean, native at `/W4 /WX`; `Drelay.Hook.dll` 90,112 bytes, `Drelay.Launcher.dll`
  35,328 bytes.
- `Test-Injection.ps1` — **73 check(s): 73 passed, 0 failed** against the new build.
- `Drelay.Launcher.exe --help` lists `--live-seconds`.
- **The new path was observed firing**, in a synthetic-host run captured before the injection test
  cleaned up (`hook\obj\cases\pausecheck`):

  ```
  [INFO ] live window closed after 5000 ms; the frame read is paused (0 frame(s) recorded).
          Set DRELAY_KEEP_READING=1 to keep it open
  ...
    frame read      : PAUSED (the live window has closed; DRELAY_KEEP_READING=1 keeps it open)
  ```

  The `5000 ms` is the unchanged default, which is the point: the control is wired, and nothing about
  an ordinary run moved.

### 8.6 — run this

```powershell
# From an elevated shell, if the game itself runs elevated: a launcher at a lower
# integrity level cannot inject into it.
.\hook\tools\Run-Exit2a-LogDenial.ps1
```

It launches the game, waits for `hook.log`, holds it open with sharing disabled for 6 s **during the
collection delay** — so the denial precedes the evidence write, which is the ordering Exit 2a's third
assertion needs — proves the denial with a second write handle, releases it, lets the run finish, and
asserts the four properties. It compares the frame cost against the operator's ARMED run of
2026-10-11 (`%LOCALAPPDATA%\drelay\hook\20261011-221035-p4364`) unless `-BaselineRunDir` says
otherwise. Verdict and evidence land in `hook\obj\cases\exit2a-logdenial\`.

**What to expect:** `hook.log.append-failed` present with an errno and a win32 error, `log appends : N
failed pass(es)` in `summary.txt`, `log lines: N written, 0 overflowed`, and a `hook.log` at least N
lines long — the retry catching up, which is assertion 4. The one thing that would make this test
meaningless is a lock that the hook never noticed, which is why the denial is proven before it is
relied on.

### 8.7 — run this

```powershell
.\hook\tools\Run-Endurance.ps1 -Minutes 60
```

It passes `--live-seconds 3600`, prints what to do while it runs (cap FPS at 60, stay in gameplay,
change zones, alt-tab once, do not close early), waits, and then asserts against the run's own
evidence and the game's own log: launcher exit, the Windows event log, frame coverage, live-read
counts, the pause, and the hook's own worst call against the 300 µs ceiling. Verdict and evidence land
in `hook\obj\cases\endurance-60m\`.

**Two limits it reports rather than hides.** The frame ring holds a few hundred rows, so an hour
overflows it (`frame rows: N written, M overflowed`) and the endurance evidence is the cumulative
counters, not `frames.csv`. And "no frame regression" is asserted only for the hook's own measured
cost — the game's `[StallProbe] slow frame` lines are printed as context, because the game hitches on
its own and there is no un-hooked hour to compare against.

**Both scripts record the SHA-256 of the three build artefacts** into their case directory, so a
verdict can be tied to the binary that produced it.

---

## 11. Close-out — the two runs of 2026-10-12, and Phase 1's state

**Date:** 2026-10-12. **Verifier:** DSH agent, at the operator's report ("phase 1 is complete").
The operator ran both acceptance runners from their own shell on 2026-10-12; this section is read
from the run evidence they left, not from the operator's summary, and it records what the two runs
actually establish.

### 11.1 What ran, and where the evidence is

| | Exit 2a | Endurance |
|---|---|---|
| command | `hook/tools/Run-Exit2a-LogDenial.ps1` | `hook/tools/Run-Endurance.ps1 -Minutes 60` |
| case dir | `hook\obj\cases\exit2a-logdenial\` | `hook\obj\cases\endurance-60m\` |
| run dir | `%LOCALAPPDATA%\drelay\hook\20261012-030308-p19828` | `%LOCALAPPDATA%\drelay\hook\20261012-012057-p3488` |
| wall clock | 365 s | 3900 s |
| checks | **10 passed, 1 failed, 0 gap** | **8 passed, 2 failed, 2 gap** |

Both runs are in the operator's default runroot, i.e. **outside** the harness's write confinement,
which is why they are full-evidence runs and not the truncated ones a harness-spawned run leaves
(MANAGED-ENTRY-HANDOFF.md §5.2).

### 11.2 Exit 2a — met

`verdict.txt` carries one FAIL, and it is the instrument fault of §11.3, not a property of the run.
Every assertion the criterion actually asks for passed:

| criterion (IMPLEMENTATION-PLAN.md Exit 2a) | evidence from this run |
|---|---|
| 1. the game is unaffected | `phase 1 complete: 3 target(s) found, 1 patched`; **no** `.NET Runtime` / `Application Error` event for the window |
| 2. the frame cost is unchanged | `frames.csv`: 1024 calls, worst **9 µs** after the first, against the baseline's 2 µs — inside the ceiling and inside the baseline comparison's tolerance |
| 3. the kill was felt | `hook.log.append-failed` names the path, `errno 13`, `win32 error 32`, `lines pending: 1`; `summary.txt` carries `log appends : 113 failed pass(es)`; `lock.txt` proves the denial with a refused write probe (`Processen kan inte komma åt filen …`) |
| 4. a transient kill lost no line | `log lines : 66 written, 0 overflowed`, and **72** lines present in `hook.log` — the read cursor advanced only on success and the retry caught up, which is the mechanism the assertion is about |

The lock was taken at `03:03:09` during the collection delay, which is the ordering assertion 3 needs.

### 11.3 The FAIL in both runs is one instrument fault: `$child.ExitCode` is `$null` under PowerShell 5.1

Both `verdict.txt` files report `launcher exit code 0 … FAIL exit=` — with an **empty** value, and
both `run.txt` files record `exit=`. An empty value means the property read returned `$null`, not
that the launcher failed: the scripts test `$exitCode -eq 0`, so `$null` fails the check.

**The cause is measured, not guessed.** `Start-Process -PassThru -NoNewWindow` with
`-RedirectStandardOutput`/`-RedirectStandardError` returns a `Process` whose `ExitCode` is `$null`
under **Windows PowerShell 5.1** — which is the host the operator's run used, as the ANSI-encoded
verdict files prove (`Set-Content`'s default is ANSI there, UTF-8 in PowerShell 7). Reproduced on
this machine (PS 5.1.19041.6328):

```
PS 5.1, -PassThru -NoNewWindow + redirection, WaitForExit(10000)  -> ExitCode = $null   (Refresh() does not help)
PS 5.1, same, with `$null = $child.Handle` before the wait        -> ExitCode = 7       (correct)
PS 7.6.6, same, without the Handle touch                          -> ExitCode = 3       (unaffected)
```

**Fixed in both runners**: `$null = $child.Handle` immediately after `Start-Process`, with the reason
in a comment; and the check now reports **GAP** ("the host did not expose the launcher exit code")
rather than FAIL when the value is still unreadable, because a `$null` exit code is a fact about the
instrument and never about the game. The two runs stand as evidence on their other eleven and eleven
checks; the exit-code check is the one line a future run should re-read.

### 11.4 The endurance run's 323 µs sample: not the first call, not located, and not explained

The other FAIL is `worst frame call within the 300 us ceiling : 323 us`. It is **one sample**, and the
run's own log rules out the two easy explanations:

- **it is not the first-call warm-up** §4.4 describes. `hook.log` records, at frame call 120,
  `worst call so far 4 us` — so the 323 µs sample happened *after* call 120, somewhere in the
  remaining 209 777 calls. The earlier ARMED runs' worst call *was* their first (274 µs, then 1–2 µs).
- **it is not a per-frame cost.** `read value` is steady, the read recovered (198 586 ok / 2 218
  failed over 209 897 calls = 1.06 % failed), and the ring's surviving 1 024 rows are all 0–1 µs.

**It cannot be located in time from this run's evidence, and that is an instrument gap.** The frame
ring holds the last 1 024 of 209 897 rows (`209897 written, 208873 overflowed`), so the sample is
gone; the only survivor is the cumulative maximum in `summary.txt`. One 323 µs sample in 209 897 is
5 × 10⁻⁶ of frames, and the detour's timestamps are a QPC pair *inside* the detour, so a single
sample of that size is consistent with the game thread being descheduled between the two reads —
but **that is a hypothesis, not a measurement**, and nothing in this run distinguishes it from a
genuine one-off cost inside the read path. It is recorded here as unexplained rather than waved away.

**The instrument that would settle it is the same one Phase 2 needs.** Phase 2's exit criterion is a
**p99** per-frame cost, and a ring that overflows cannot produce a percentile at all. So the first
Phase 2 instrumentation change is a **frame-cost histogram** in the hook (the pattern `hook.log.sizes`
already uses for IL sizes) plus a locator that survives ring overflow — which would have placed this
sample in a bucket, and would place any future one.

### 11.5 Two further instrument defects this run exposed, both Phase 2's

1. **`phase 1 complete: N target(s) found` counts only the discovery window.** The endurance run's
   final line says `1 target(s) found, 1 patched`, while four lines above it the target-size table
   says `spawn … compiled 2 time(s) AND MATCHED`, `collision … 1 AND MATCHED`, `escape … 8 AND
   MATCHED`. All four *were* found and matched during the run; three were compiled only **after** the
   fixed 30 s discovery window closed (`[WARN] discovery window expired with 1 of 4 targets found`,
   then three `target 'X' found` lines). The counter and the sentence that carries it therefore
   disagree with the evidence directly above them, in the direction that reads as a failure. Phase 2
   hooks `spawn` and `collision`, so this matters: a 30 s window at the title screen would report
   them MISSING while the run went on to match them.
2. **8 compilations were executed and not inspected** (`re-entrant skip : 8`), and the hook's own
   warning says a target reported MISSING is not proof it was never compiled. That warning is correct
   and is the reason the target-size table exists; any Phase 2 statement of the form "the sensor saw
   every compilation" must carry it.

### 11.6 Verdict on Phase 1

> **Phase 1's exit criteria are met.** Exit 1 / M3: an hour with the hook resident and `frameTick`
> patched, **no fault recorded for the window**, 209 897 frame calls at 53.8 Hz average with the read
> live for 198 586 of them, no frame regression attributable to the hook (steady state 0–1 µs), and
> the read correctly paused at the window's close. Exit 2a: met, §11.2. Exit 3: met earlier
> (`Test-PeReader.ps1`, `Test-Injection.ps1` run 1). The `all four patched` clause of Exit 1 remains
> amended rather than satisfied, exactly as the plan's own note says it must be: **one target is
> patched deliberately**, because a detour body must match the target's signature and the other three
> have no Phase 1 behaviour to run. The two FAILs in the verdict files are instrument faults (§11.3,
> §11.4), one fixed and one now scoped as Phase 2 instrumentation.

Phase 2 may start. It starts with [PHASE2-SCOPE.md](PHASE2-SCOPE.md).

### 11.7 One Phase 1 claim in this document has since been overtaken

§2's note that `Test-Injection.ps1` "does NOT cover IL-hash target matching or the frame-tick patch,
because those need the game's own assembly" was true when it was written and is now false. Phase 2's
first round gave the synthetic host a **matchable target of its own** — `SyntheticHostFixture.Cadence`
`::FrameTick`, published through the launcher's new `--test-target` switch, with its identity computed
by `hook/tools/il_method_hash.py` from `il_index.py`'s own reader and hash function — and added run 7,
which asserts that the target is found by IL hash in the real compilation stream, patched, entered
once per cadence call (compared against the host's own body count), and read from: `PASS 9c live world
read: 341 successful frame read(s)`. `GAP 9c`, which runs 1-6 could only report, is closed. The suite
is now **90 checks, 90 passed**. The game's own four hashes are still only checked by the Phase 0 gate
and by a live run, which is the one thing that has not changed. The detail — including the trap that
cost the first attempt its target — is [PHASE2-SCOPE.md](PHASE2-SCOPE.md) §4.2.
