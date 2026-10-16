# Plan — the hook control channel: live toggles from the dashboard, and starting the hook from the relay

**Items 3a and 3b of the farmer work.** Written 2026-10-17. Implementation plan, not a record;
nothing here is built.

Read with: [`JAVA-BRIDGE.md`](JAVA-BRIDGE.md) (the transport research and the one architectural rule),
[`BRIDGE-RESEARCH.md`](BRIDGE-RESEARCH.md) (the measured numbers behind it),
[`HANDOFF-LAUNCH-CONFIG.md`](../hook/HANDOFF-LAUNCH-CONFIG.md) §8, §9.2, §10.2 (what
was deferred, and the elevation ordering problem), [`PLAN-AUTOFARM.md`](../hook/PLAN-AUTOFARM.md)
(the consumer), and for the proxy's side of the house style,
[`AUTONEXUS.md`](../proxy/AUTONEXUS.md) §4.2 and [`INJECTION.md`](../proxy/INJECTION.md).

---

## 1. The findings that decide the design

`JAVA-BRIDGE.md` specifies a pagefile-backed named section, `Local\DrelayHookShm`, with two
sequence-stamped rings. **It has never been built** — the name appears only in documents. Three facts
found in the tree change what should be built first, and all three push in the same direction.

### 1.1 A shared block already exists, and it is a file

`hook/managed/Drelay.Launcher/SharedBlock.cs:35-113` creates `shared.block` **in the run directory**
and the same file is mapped by three processes: the launcher, the managed bootstrap and the native
hook (`DrelayMain.cpp:242-286`). The reason is measured and recorded:

> *"A named pagefile-backed section is charged against the system commit limit when it is created, and
> on this machine `CreateFileMapping(INVALID_HANDLE_VALUE, ..., "Local\name")` for 431 KB fails with
> error 8 (ERROR_NOT_ENOUGH_MEMORY) from inside the launcher while the identical call from another
> process succeeds."* — `SharedBlock.cs:50-64`

So the transport `JAVA-BRIDGE.md` §3.2 chose was **tried, measured, and replaced by a file**. The
file-backed mapping is strictly better for this project's purposes: no commit charge, readable after
a crash, discoverable by path, and — decisively — **mappable from Java with no dependency at all**
(`BRIDGE-RESEARCH.md` §2.2, option 2: `FileChannel.map(MapMode.READ_WRITE, 0, size, Arena.ofShared())`
is not a restricted method and needs no `--enable-native-access`).

### 1.2 The native side already reads a flag out of that block on the frame path

`g_shared->cfgFlags` is read per frame at `hook/native/DrelayFrame.cpp:446`. The header's own contract
is a **single writer per region**: *"bootstrap writes `cfg`, native writes `st`"*
(`native/DrelayHookShared.h:323-325`), and it documents that a field which fits in existing padding
does **not** require a version bump while a layout change **must** bump `DRELAY_SHARED_VERSION`
(`:22-27`, `:109-116`). The structure this plan needs is therefore not new: it is one more region in a
block that already has the shape, the discipline and a generator
(`hook/tools/gen_shared_layout.py` → `GeneratedSharedLayout.cs`, with a `static_assert` on the size).

### 1.3 The managed side's settings are all read once, at resolve

Every switch and every knob is an environment variable read exactly once
(`DodgeAdapter.cs:426-435`, `DodgeConfig.FromEnvironment` at `DodgeSnapshot.cs:1121-1184`,
`SteeringPilot.cs:341`, `AutoAim.cs:167`), and there is no re-read anywhere. But the plumbing to
*apply* a new configuration already exists and is unused: **`DodgeSolver.Configure(DodgeConfig)`
(`DodgeSolver.cs:644-647`) forwards to `PathPlanner.Configure` (`PathPlanner.cs:262-265`), and no call
site exists in the tree.** That is the hook point, and its absence is why nothing is live-tunable
today.

### 1.4 The one open decision this plan does not take

`JAVA-BRIDGE.md:228-236` leaves **escape ownership** open: the hook calls `World.RequestEscape()`
in-process, or the relay injects `GmEscape` as the only writer of the socket. Nothing in this plan
depends on the answer — control is configuration, not decisions — and the current implementation is
already the in-process one (`EscapeTest.cs:162`, `:256-293`; `DodgeAdapter` deliberately does not call
it, `:1179-1186`). Recorded as still open; **not** a prerequisite for 3a or 3b.

---

## 2. Transport, decided

| option | verdict |
|---|---|
| **The existing `shared.block` plus a control region** | **chosen.** Already mapped by three processes, already read on the native frame path, already has a single-writer contract and a layout generator, needs no JNA and no new transport, and is discoverable by path. |
| pagefile `Local\DrelayHookShm` (`JAVA-BRIDGE.md` §3.2) | **not for control.** The measured failure in §1.1 is the reason it was never built. Keep the *name* in reserve for a high-rate event ring (§8) if telemetry ever needs one. |
| loopback TCP (`JAVA-BRIDGE.md` §3.1) | a good **fallback**, and the only one that can carry a control channel when the block cannot be found or when the relay is not the parent process. Costs a connect/reconnect state machine and needs its own ack. Not first. |
| named pipe, message mode | rejected on the repo's own measurement (`HANDOFF-LAUNCH-CONFIG.md` §9.2). |
| config file rewritten + `FileSystemWatcher` | rejected for the reason `HANDOFF-LAUNCH-CONFIG.md:375-378` gives: file locking plus a watcher's coalescing makes *"did the hook apply my change"* unanswerable without an acknowledgement channel — which is precisely what the block gives us for free. |

### 2.1 Discovering the block from Java

The path is deterministic in shape and not in name:

```
<runroot>\<yyyyMMdd-HHmmss>-p<pid16>\shared.block
        -- Program.cs:271-277, runroot defaults to %LOCALAPPDATA%\drelay\hook
```

so the relay's discovery is:

1. resolve the run root (`drelay.hook.runroot`, else `%LOCALAPPDATA%\drelay\hook`);
2. list `*-p*` directories, newest first by directory timestamp;
3. open `shared.block` read/write with **shared** access (the launcher creates it
   `FileShare.ReadWrite`, `SharedBlock.cs:69-73`, and Java's Windows open path already passes
   `FILE_SHARE_READ|WRITE|DELETE`);
4. validate `magic`, `version` and `structSize` against its own constants **before interpreting a
   single field** — the header's own rule: *"A mismatch between this and the native side's value is a
   refusal, never an interpretation"* (`SharedProtocol.cs:23-27`);
5. confirm liveness from `nativePid`/`bootPid` and the run directory's pid suffix, so a crashed run's
   block in an old directory is not mistaken for the live one;
6. **re-resolve on a new directory.** A new run creates a new file in a new directory; the relay must
   notice (the run root's newest directory changed) and re-attach, and it must report `gone` rather
   than reading a frozen block forever — the same failure `JAVA-BRIDGE.md` §5.2 describes, and the
   reason the block carries a heartbeat.

---

## 3. The control region

### 3.1 Shape

One more region in `DrelayShared`, at a fixed published offset, with the header's one-writer-per-region
rule: **Java writes `ctrl`, the hook writes `hookAck`.**

```
/* control: written by the Java relay, read by the hook */
volatile uint32_t ctrlMagic;        /* 'DHRK' -- a magic of its own, so a garbage block is inert  */
volatile uint32_t ctrlVersion;      /* layout version of THIS region                              */
volatile uint32_t ctrlSeq;          /* bumped with release on every change                        */
volatile uint32_t ctrlHeartbeat;    /* bumped by the relay; staleness => the hook reverts         */
volatile uint32_t ctrlOwnerPid;     /* the relay's pid, for the log line                          */
volatile uint32_t ctrlFlags;        /* one bit per boolean toggle                                 */
/* then a fixed set of numbers, each a named field (never an array indexed by an enum) */

/* acknowledgement: written by the hook, read by the Java relay */
volatile uint32_t hookAckSeq;       /* the ctrlSeq the hook has applied                           */
volatile uint32_t hookAckFlags;     /* what it actually applied after clamping                    */
volatile uint32_t hookAckRefused;   /* a count, and a reason code for the last refusal             */
```

Three properties are deliberate:

- **a magic and a version of its own.** The block's main `version` gates the whole layout; this region
  gets its own, so a relay built against v7 can attach to a hook of the same block version and still
  refuse a control region it does not understand, field by field, without refusing the block itself;
- **the numbers are named fields, not an array indexed by an enum.** An array is how two builds come
  to disagree about what index 7 means, and this project's generator exists to make that impossible;
- **`hookAck*` is the answer to "did it apply".** Without it the whole approach is the
  `FileSystemWatcher` dead end; with it, a dashboard can say *applied* / *clamped* / *refused, because*.

### 3.2 Where it goes, and the version-bump cost

The header documents that a field which fits in existing padding does not force a bump
(`DrelayHookShared.h:109-116`), while any real layout change does
(`:22-27`; `DRELAY_SHARED_VERSION 7u`, `DRELAY_SHARED_SIZE 440944u`, with the compile-time
`static_assert` at `:723-725` enforcing it). **The control region should be treated as a real layout
change: bump the version and the size, regenerate, and let the mismatch refusal do its job.** The
cost is a coordinated release — launcher, bootstrap, native hook and now the relay all have to
understand the same version — and that coordination is the point: a relay that guesses at a hook's
layout is a relay that can mis-set a safety switch.

### 3.3 Ordering and the read cost

- Java: write the payload fields, then `ctrlSeq` with a **release** store. Payload before stamp,
  always.
- Hook: read `ctrlSeq` with an **acquire** load; if it equals the last applied value, do nothing.
  The JDK documents `getAcquire`/`setRelease` as compatible with C++ `memory_order_acquire`/`release`,
  and `BRIDGE-RESEARCH.md` §2.2 and `JAVA-BRIDGE.md` §3.3 both name the specific hazard — *"a plain
  read of a shared index inside a spin loop can be hoisted by the JIT into an infinite loop"*. On the
  managed side this is a `Volatile.Read` on an `int` field of the block
  (`HookEntry` already reads the block through raw pointers, `StartupHook.cs:1945`, `:2900-2918`).
- **Cost**: one acquire load per frame, in `FrameReplacementSensor`, next to `EscapeTest.OnFrame()`
  and before `_frameSensorEnabled`'s branch. A change is copied only when the sequence differs. This
  is the same shape as the existing cached env read (`FrameReplacementAllocates`,
  `StartupHook.cs:1532-1540`), and it is off the sampler's and solver's cost entirely.

---

## 4. What is live-tunable, and what is not

This split is the most important decision in the document, because a live control channel that can
loosen a *safety* property is a live control channel that can kill the character.

### 4.1 Live (reported, applied at a frame boundary)

| group | examples | why it is safe |
|---|---|---|
| **policy** | dodge `Enabled`, `Act`, `DryRun`, `StandStops` | they change whether the tool acts, never whether a move is admissible |
| **scoring** | every `Weight*`, `ClearComfort`, hysteresis margin/dwell, path weights | a score can only re-rank candidates that already passed |
| **goals** | farm mode on/off, farm engage radius, the target-stickiness margin, the active route, route deviation | a goal changes the *direction* of a move, never its legality |
| **attack** | farmer firing on/off, its cadence limits | a client action the operator performs anyway; and it has its own launch-time off |
| **diagnostics** | decision logging verbosity, report cadence | no behaviour |

### 4.2 Restart-only (and the hooks refuse them by name)

| group | why |
|---|---|
| **admission geometry** | `SampleStepMs`, `SampleCount`, `HorizonMs`, `TrustSeconds`, `SweepStep`, radii, `MoveTemporalWindowSeconds`, `DecisionDiscMultiplier`, `ImpactWindowSeconds` — these define the boundary itself. A relay must not be able to widen the disc-at-arrival rule from a web page; that is the one change that converts "safe" into "dead" without any other symptom. |
| **the sampler's identity** | the per-trajectory sample buffers are sized from `SampleCount` at resolve; changing it live is a re-allocation on the frame path |
| **which detours exist** | `--autoaim`, `--frame-replacement`, `--move-steer`, `--escape-test` are decided at JIT time by the native side (`DrelayFrame.cpp:174-235`). A target that was never patched cannot be turned on from a web page. |
| **anything resolved from a game field** | offsets and accessors (`DodgeAdapter.Resolve`, `AutoAim.Resolve`) — a failure there is a restart, not a toggle |
| **the module's own existence** | `DRELAY_FARM`/`DRELAY_DODGE` gate whether the module publishes a replacement at all |

The dashboard must present both groups, with the restart-only ones visibly marked — otherwise the
operator's reasonable expectation ("everything on this page applies") is wrong in exactly the place
where being wrong is expensive.

### 4.3 The rule that keeps it honest

**Every live-tunable setting must also have a launch-time value, and the launch-time value is the
default.** The bridge is not load-bearing (`JAVA-BRIDGE.md` §8): a run with no relay is the status quo,
and a run whose relay dies reverts to launch-time configuration (§6). A farmer mode that exists only
across the bridge would make the bridge a safety dependency, which §8 forbids.

---

## 5. Applying a change

```
FrameReplacementSensor(total):
    Control.Poll()                    <- NEW: acquire ctrlSeq; on change, validate, clamp, apply
    EscapeTest.OnFrame()              <- unchanged
    if (_frameSensorEnabled)
        SensorRead.OnFrame()
        ... TraceRecorder, DodgeAdapter (unchanged)
```

Four rules:

1. **at a frame boundary, before the decision** — never mid-frame. A decision computed against two
   configurations is a decision nobody can reproduce, and this project's whole testing method is
   reproducibility.
2. **validate then clamp, and report what was applied** — the `applied` key list and the whole
   resulting config in the response, exactly as `/api/nexus` does
   (`WebDashboard.java:382-399`: *"the response carries the whole resulting config, not just what
   changed: the dashboard's controls are rebuilt from it, so a value that was clamped … is visible
   immediately"*).
3. **an unknown key is named, not ignored** — `AutoNexus.Config.apply` returns `"unknown:" + key`
   (`AutoNexus.java:188`), and the plan inherits that: *"a typo would otherwise look like a setting
   that simply had no effect"* (`AUTONEXUS.md` §7).
4. **apply through the existing seam** — `DodgeAdapter._config` is a field and `DodgeSolver.Configure`
   already exists for exactly this; adding the call site is the change, and it must reconfigure the
   planner in the same breath (`PathPlanner.Configure`, `PathPlanner.cs:262-265`) so the search and the
   solver cannot hold two different configurations.

---

## 6. Expiry, failure and the operator's override

The bridge's rule in one line: **a missing, stale, lying or hostile control region leaves the hook
exactly as it was launched.**

| situation | hook behaviour |
|---|---|
| no block, no `ctrlMagic` | ignore the region entirely; launch-time configuration stands; one log line |
| `ctrlVersion` mismatch | refuse the region by name; launch-time configuration stands |
| `ctrlHeartbeat` stale (> 2 s) | **revert to launch-time configuration**, log once with the last applied configuration, and re-arm if the heartbeat returns |
| `ctrlSeq` goes backwards (relay restarted) | treat as a fresh sequence, re-apply |
| a value fails validation | apply the clamped value, set `hookAckRefused` with a reason, do **not** apply the invalid field |
| `DRELAY_NO_CONTROL=1` at launch | the region is never read. The operator's "a buggy relay must not be able to disarm the dodge." |

Two properties the implementation must have and the tests must assert: **the hook never blocks on the
control region** (no lock, no wait, no syscall — it is a load and a compare) and **the hook never
crashes on it** (every field is validated before use; a garbage block is inert).

---

## 7. 3b — starting the hook from the relay's dashboard

### 7.1 What exists, and what does not

| exists | does not exist |
|---|---|
| `hook\out\Drelay.Launcher.exe`, whose injection is a process launch with `CREATE_SUSPENDED` + a remote `LoadLibraryW`/`DrelayHook_Init` stub + `ResumeThread` (`PHASE1-SCOPE.md` §2.1, `Program.cs:340-381`) | a `--config <path>` flag. `HANDOFF-LAUNCH-CONFIG.md` §8 recommends exactly this shape for a supervising process *"not adopted now"* |
| `--no-wait`: *"what a supervising process uses: it returns as soon as the hook has answered, and the game keeps running"* (§8) | a proxy-side launcher: `HANDOFF-LAUNCH-CONFIG.md` §1 verdict 9 — *"Not in this release … §10.2 is the ordering problem that has to be solved first"* |
| a per-field merged config file at `%LOCALAPPDATA%\drelay\hook\drelay.config.json`, with every value translated to a `DRELAY_*`/`DRELAY_HOOK_*` environment variable (`EnvironmentBlock.Build`) | a dashboard route or button for any of it |
| the proxy's HTTP API, its `ProcessBuilder` usage (`Launcher.java:303`, `Relay.java:1420`) and its mutable-config house style | — |

### 7.2 The launcher-side step: `--config <path>`

Add it to `CommandLine.cs` and `Configuration.cs` as one more per-field source, ranked **between the
config file and the command line** (or, cleanly: `--config <path>` *replaces* the default config
file's path while keeping its precedence). Reasons this is the right first step and not a side
quest:

- without it, a proxy-launched run either overwrites the operator's saved file (unacceptable: it is
  the operator's settings) or cannot pass any settings at all;
- with it, every launch is reproducible and inspectable after the fact, in the run directory —
  *"a generated config is inspectable after the fact, which stdin is not"* (§8);
- it is testable offline, and the launcher already has a harness with the C1–C8 invariants
  (`tools/LauncherHarness/`) to extend.

**The invariant to add:** a `--config`-supplied field is overridden by a command-line field and
overrides the default config file's field — the same per-field rule as §4.1 of that document, with one
more source. C1 becomes a three-source truth table.

### 7.3 The relay-side step: a launch button that is not a second game launcher

New pieces, all small and all local to the proxy:

```
networking/HookControl.java     -- maps shared.block; polls; validates; writes the control region
networking/HookLauncher.java    -- resolves Drelay.Launcher.exe; writes a per-run config; spawns it
WebDashboard                    -- GET /api/hook  (state)   POST /api/hook/config (toggles)
                                   POST /api/hook/launch  (start the game with the hook)
DashboardPage                   -- one panel: hook state, live toggles, restart-only values greyed
```

Behaviour of `POST /api/hook/launch`:

1. **refuse if a hook is already attached** — a second launcher would start a second game. The refusal
   is by name, the way `FirstRun`'s refusals are.
2. resolve the launcher: an explicit setting first (`drelay.hook.launcher` in the route table), then
   beside the jar, then beside the game, then `%LOCALAPPDATA%\drelay\hook`. A missing launcher is a
   named refusal, and so is a launcher whose `VERSION.txt`/build does not match the game — though that
   second one is the *hook's* refusal to report (see §7.5).
3. write a generated config into the relay's own work directory (per-run, inspectable) carrying the
   game path, the auto-aim/dodge/farmer switches, `NoWait: true`, `RunRoot`, and `CollectDelay: 0`.
4. spawn the launcher, capture its stdout, and read the `run directory : …` line it already prints
   (`Program.cs:278`) — that is how the relay learns the block path without guessing (§2.1).
5. attach to the block, start the poll thread, and report state.

### 7.4 The elevation ordering problem — and the recommendation

`HANDOFF-LAUNCH-CONFIG.md` §10.2 states it exactly:

> *"The proxy elevates (it claims server addresses on the loopback interface, `Launcher.java`), and a
> process cannot inject into one at a higher integrity level. If the proxy launches the game, then
> either the game is elevated (because the proxy is), or the launcher must elevate itself, or the
> launcher must be launched by the proxy before it elevates."*

Three options, and the plan's verdict:

| option | what happens | verdict |
|---|---|---|
| **(a) the relay spawns the launcher** | both the launcher and the game are children of an elevated process, i.e. the same integrity band, so the injection works. **The game runs elevated.** | implement it, and **do not make it the default**: elevating the game is a visible security change and some security software treats an elevated game process differently. |
| **(b) the relay writes the config and hands the operator the exact command** (one click to copy) | the operator runs it from their own unelevated shell; the hook works exactly as it does today | **default.** It solves the operator's actual problem — "find the arguments and type them" — without changing the integrity of the game, and it is the flow `HANDOFF-LAUNCH-CONFIG.md` §6's first-run experience already assumes. |
| **(c) a de-elevated helper** (scheduled task, `explorer.exe` hand-off, `runas /trustlevel`) | the relay spawns the launcher unelevated | real work, real fragility, no new capability. Do not. |

The dashboard must say which mode the button will use, because the difference is not visible
afterwards: a game that was started elevated looks identical from inside the game.

**One more thing the button is not.** Injection is **process launch only** — "attach the hook to a
running game" was considered and rejected (`HOOK-ARCHITECTURE.md` §3.3). The button therefore reads
*"start the game with the hook"*, never *"attach"*, and pressing it while the game is running refuses
rather than injecting.

### 7.5 Failure reporting

| failure | who reports it |
|---|---|
| launcher missing / no game path | the relay, in the response and the event log |
| the game's build does not match the pinned one | **the hook**, in `selfcheck.txt`/`hook.log`; the relay reads the state from the block (`nativeStatus`'s `DRELAY_ST_REFUSED`) and shows it — *"the launcher should prefer (2) and, when discovery succeeds but the hook later refuses, point at the run directory's `selfcheck.txt`"* (`HANDOFF-LAUNCH-CONFIG.md` §7.1) |
| the launcher returns non-zero | the relay, with the launcher's own stdout tail; the launcher already prints the reason |
| the hook starts but never attaches to the block | the relay reports `starting` then `stalled`, not `attached` — the difference between "the game is loading" and "the hook failed" is the block's heartbeat |

---

## 8. The hook → relay direction, and how little of it is needed

`JAVA-BRIDGE.md` §4.1 lists the events. **For 3a and 3b, none of them is required**: control needs
Java→hook plus the ack, and the ack is a few counters already in the block (`nativeStatus`,
`targetsPatched`, the counters the self-check writes). A hook panel that says *attached / armed /
patched / refused*, plus the dodge's own report counters, is a **status region** — a small fixed
snapshot with its own sequence stamp:

```
hookStSeq / hookStFrame / hookStPlayerX/Y / hookStThreats / hookStBodies /
hookStDecisionCounters[reason] / hookStCostUs / hookStEnemies / hookStPortals
```

The full sequence-stamped **event ring** is the right answer if telemetry ever becomes high-rate, and
`JAVA-BRIDGE.md` §7's spike is still the way to prove its latency — but it is not on this path, and
building it first would delay the two items the operator asked for. The one *functional* consumer
worth naming: the farmer's relay-injection fallback for entering a portal needs the portal's `GameId`
(`PLAN-AUTOFARM.md` §5.2), which is a single field in the status region rather than a ring.

**And the one architectural rule, restated because this plan must not weaken it:** the bridge carries
**observation and configuration only**. The dodge, the escape decision and the farmer's target choice
stay in-process, inside the frame (`JAVA-BRIDGE.md` §1). Nothing in this plan moves a decision across
the file.

---

## 9. Verification

| # | check | how |
|---|---|---|
| B1 | the relay maps the block and refuses a bad one | offline fixture: a block file with the right magic and a wrong version, wrong size, and a garbage header — each refused **by name**, none interpreted |
| B2 | a toggle reaches the hook | POST a dodge-off; the hook applies it at the next frame; the change is visible in the dodge report and `hookAckSeq` advances |
| B3 | a toggle is reported back | the response carries the whole resulting configuration; a clamped value is visible; an unknown key is named, per §5 |
| B4 | a restart-only key is refused, not applied | POST an admission-geometry change; expect the named refusal, and assert the hook's boundary is unchanged |
| B5 | **killing the relay changes nothing but the configuration** | paired runs: kill the relay mid-session; assert the hook reverts to launch-time config inside the heartbeat window, keeps dodging, keeps its frame cost, and does not stall, spin or leak |
| B6 | `DRELAY_NO_CONTROL=1` makes the region inert | run with a deliberately lying block; assert zero effect |
| B7 | no torn reads | the `JAVA-BRIDGE.md` §7 criterion: a checksum and a monotonic counter over ≥ 10⁶ toggles, zero torn, p99 < 1 ms |
| B8 | the relay stays dependency-free | no new imports; no JNA; `FileChannel.map` + `VarHandle` only. This is the property `JAVA-BRIDGE.md` §2 was written to preserve and it is worth an explicit check |
| B9 | a relay-started hook attaches and plays | one live run: the button (or the copied command, mode (b)), the run directory read, the block attached, the game playable, the dodge acting |
| B10 | the button cannot start a second game | with the game already running, assert the refusal and that no process was spawned |
| B11 | the per-field precedence still holds with three sources | extend the launcher harness's C1 to a truth table over CLI × `--config` × default file |

B5 and B6 are the two that matter most: together they are the assertion that the relay is a
*convenience*, not a dependency.

---

## 10. Order of work

| # | item | needs the game? | depends on |
|---|---|---|---|
| 1 | the control region in `native/DrelayHookShared.h`, the generator re-run, the version/size bump | no | — |
| 2 | the managed poll: `Control.Poll()` in `FrameReplacementSensor`, validate/clamp/apply, the ack fields; **two toggles only** (dodge `Enabled`, `Act`) | no | 1 |
| 3 | `networking/HookControl.java`: block discovery, mapping, `VarHandle` access, the poll/heartbeat thread, revert-on-stale | no | 1 |
| 4 | `POST /api/hook/config` + `GET /api/hook` + the dashboard panel | no | 3 |
| 5 | the rest of the live-tunable table (§4.1) as the farmer needs it | no | 2, 4 |
| 6 | `--config <path>` in the launcher + the C1 truth table | no | — |
| 7 | `HookLauncher` + `POST /api/hook/launch` + the button, defaulting to mode (b) | **yes, one run** | 6 |
| 8 | the status region (§8), if the panel needs more than the existing counters | no | 1 |

Rows 1–4 are a self-contained deliverable: **a dashboard that turns the dodge on and off in a running
game, and reverts if the relay dies.** That is 3a, and it is what the farmer's later work needs to be
tunable without a relaunch. Row 6–7 is 3b.

---

## 11. What this plan does not do, and the decisions it leaves open

- **Escape ownership** (`JAVA-BRIDGE.md` §4, still open): not needed here; the in-process call remains
  the implementation.
- **The event ring** (`JAVA-BRIDGE.md` §3.2 ring A): not built. The status region covers the dashboard
  and the farmer's one field.
- **Attach to a running game**: not supported, and the button must say so (`HOOK-ARCHITECTURE.md` §3.3).
- **The game's elevation is the operator's decision** (§7.4), and mode (b) is the default precisely so
  that decision is not taken silently.
- **No anti-detection claim.** This plan changes which process writes a settings file, not what leaves
  the machine.
- **Nothing about the pagefile section.** If a high-rate ring is ever wanted, `JAVA-BRIDGE.md` §7's
  spike is still the prerequisite, and the measurement in `SharedBlock.cs:50-64` is the reason to look
  at a file first.
