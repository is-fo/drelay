# autododge research — an in-process hook for Darza's Dominion

This directory is the research and implementation plan for replacing `drelay`'s **reactive**
auto-nexus with a hook that runs **inside the game process**: it dodges incoming damage before it
lands, and falls back to the client's own nexus only when no move can avoid it.

It is research plus a plan, **and Phases 0, 1 and 2 are built and closed**: the fingerprint gate and
the target table that pin the hook's four detour points by IL hash; the injection and hook skeleton
that matches all four against the live game; and the read-only sensor that predicts collisions and
compares them against the game's own decision ([`PHASE2-SCOPE.md`](../docs/hook/PHASE2-SCOPE.md), closed by
operator decision on 2026-10-13). No game file has been modified; the hook attaches at runtime.

Branch: `autododge-research`. The proxy stays Java; the hook is C++.

---

## Where the work stands

| phase | state |
|---|---|
| **0** — ground truth, and a build gate | **built.** The gate passes 15 checks against the installed build and refuses a mutated one at the *method* level; **six** targets are pinned by IL hash, cross-checked against the disassembly, and recorded in [`known-build.json`](known-build.json) (four Phase 0/2 targets, Phase 3's movement integrator, and the auto-aim target `AimbotSystem.FindTarget`, added by the auto-aim module below). The procedure is [`docs/hook/HOOK-BUILD-MATRIX.md`](../docs/hook/HOOK-BUILD-MATRIX.md). One item is open: the controlled `GmHit`-drop trial, which needs a live session and does not gate the hook |
| **1** — get in, and stay in | **built, confirmed live, and closed on 2026-10-12.** Three artefacts build clean — `Drelay.Hook.dll` (native), `Drelay.Bootstrap.dll` (the startup hook) and `Drelay.Launcher.exe` — and every offline check passes: ABI, the PE/MVID reader (including a deliberately tampered image) and a **127-check end-to-end injection test that passes 127/127** (90 when Phase 1 closed; Phase 2 added the managed replacement under forced full compacting collections, the sensor's read of the live set, the collision comparison and the predicate-semantics checks those last two need). Against the real game the whole chain works: injection before `Main`, the handshake, runtime validation, the JIT/EE GUID, the loaded-vs-file MVID check, the collection delay and its window, and the `compileMethod` detour seeing the compilation stream. **All four targets are matched by IL hash against the live game** — `frameTick` (IL 1286, token `0x06001FC5`, agreeing with Phase 0), `spawn` (2014), `collision` (989) and `escape` (335) — and `frameTick` is patched, with the hook reading live state from the real game: `arm state: ARMED`, the pointer chain and reflection agreeing on `Player.Position`, a plausible coordinate in `frames.csv`, and in the **hour-long acceptance run of 2026-10-12** 198 586 successful reads out of 209 897 frame calls at 53.8 Hz with no fault recorded and the read correctly paused at the window's close. Exit 2a (a denied log target costs evidence, never the game) passed all four of its assertions in the same round. Two verdict lines read FAIL and both are **instrument faults**, not run failures: the two runners read a `$null` exit code under Windows PowerShell 5.1 (fixed in both scripts), and the endurance run's single 323 µs sample sits above the 300 µs ceiling with no way to place it in time — the ring had already overflowed, which is why a cost histogram is Phase 2's first instrumentation change. The full accounting is [`PHASE1-VERIFICATION.md`](../docs/hook/PHASE1-VERIFICATION.md) §11; [`PHASE1-HANDOFF.md`](../docs/hook/PHASE1-HANDOFF.md) is the long record of how it got there — its earlier sections describe fixed faults and are historical, and the long-form record is [`PHASE1-SCOPE.md`](../docs/hook/PHASE1-SCOPE.md) §6 |
| **2** — observe, and find out whether the model is true | **closed by operator decision on 2026-10-13**, with two recorded waivers rather than every number clean: the sampler's p99 cost is deferred to the solver that has to share the frame (now Phase 3), and the false-negative count is an over-count from asking the collision predicate on every entry instead of every `true` ([`PHASE2-SCOPE.md`](../docs/hook/PHASE2-SCOPE.md) §4.15, §7). Its mechanism question was answered by measurement. Read-only. The goal is one question — does our prediction of a collision agree with the game's? — and four numbers: the warning distribution, the false-negative and false-positive rates, the per-frame cost p50/p99, and the fraction of real bullets that are straight. **How the sensor runs on the game's thread was resolved by measurement, and it cost the first two candidates.** A native detour that calls into managed code is flaky — about one run in four died inside the GC's mark phase even with every method pre-compiled and nothing allocating — because the runtime cannot unwind a managed stack that a native frame interrupts without a transition marker. What works is a **managed replacement**: MinHook's `jmp` lands directly in a managed method with the target's signature, so the game's own `call` is a managed-to-managed call, and it continues into the original through a **managed** function pointer to MinHook's trampoline. Seven runs survived 650 forced full blocking compacting collections, including with allocation, and run 8 of the injection suite re-runs that configuration on every invocation. Scope, the run tables and both verdicts: [`PHASE2-SCOPE.md`](../docs/hook/PHASE2-SCOPE.md) §4.3–§4.4. **And the first session that reached a zone found the sensor reading the wrong container:** fifteen minutes of real play, 54 600 frames with a world on all but one of them, a zone full of bullets in the game's own log — and `live=0` throughout, because the client keeps its projectiles in `World.GameObjectLayer.Children` and not in `World.Objects`, which is what CLIENT-MAP.md §5.2 had claimed and what the hook read. The synthetic host had modelled the same wrong shape, which is why every offline check passed. Both are corrected and §10.6 is closed by the measurement. **The next session read the real client's bullets** — 19 349 frames with a world, 12–28 live projectiles per frame on a layer of 210–265 nodes, a straight fraction of **0.695** over 585 000 sampled trajectories, and a read cost of `p50 <= 50 µs, p99 <= 100 µs` — and it also showed that the collision counter had been counting the game's per-frame *predicate questions* rather than its decisions: 418 328 entries against 48 resolved hits, which inflated the false-negative rate by however long each bullet spent near the player. The counter now gates on the game's own `true`, the fixture models the predicate and would catch a regression, and **one more live session produces the four numbers as the plan defines them**. The operator has waived the cost criteria for this close. [`PHASE2-SCOPE.md`](../docs/hook/PHASE2-SCOPE.md) §4.14–§4.15 |
| **3** — dodge, with the escape as its floor | **entered 2026-10-14; Spike 3.1 ANSWERED YES on 2026-10-14.** The movement question has a mechanism: a **fifth pinned target**, `World`'s movement integrator, with a **managed replacement** on its entry — the same route Phase 2 proved — so the hook writes `GameScene.GameInput.MoveAngle` / `.Moving` / `.MoveSpeed` and the *client* produces the position, the speed law, the collision and the history the outbound `GmMove` is built from. It is opt-in (`Drelay.Launcher.exe --move-steer sweep` or `hold:90:600`), bounded by frame counts, guarded by the game's own dead/safe/spirit gates, and it releases the input when the plan ends. **The live sweep of `20261014-040859-p22748` moved the player in eight commanded directions: the value read back from `MoveAngle` equalled the value written on every logged frame (worst `0.0000000 rad`), the heading followed the command to within 2°, and the per-frame displacement agrees with the resolved speed law and with the offline budget.** The one witness still outstanding is the wire's — run the sweep with `-RelayLog` to see `GmKicked` absent rather than assume it. The run reports through [`tools/steering_report.py`](tools/steering_report.py), which reads the pilot's own frame log, the install log and the **relay's wire log** — because the client's position cannot witness whether the server accepted the movement. Entry plan and the corrected spike order: [`PHASE3-SCOPE.md`](../docs/hook/PHASE3-SCOPE.md); **the measurement, the defects behind it and the next step — the solver, offline — are in [`PHASE3-HANDOFF.md`](../docs/hook/PHASE3-HANDOFF.md)**. **Spike 3.2 then answered the offline half of the phase on the same day:** the solver is a pure function of a plain-data snapshot (`DodgeSnapshot.cs` + `DodgeSolver.cs`, no game types, no allocation on the decision path), a trace recorder writes one JSON line per frame with the player, the collision grid and every culled threat's sampled path (`DRELAY_TRACE=1`), and an offline harness (`hook\tools\SolverHarness`) **compiles those same two source files** rather than a copy — so what is tuned offline is what will steer. Four invariants (T5, T6, T10, T7) plus a "must move" assertion, over five synthetic scenarios with known correct answers: green, and they found **seven defects on the day the solver was written** — including a `stand` candidate whose lookup field was never written, so the specification's preferred answer ("the best dodge is usually no dodge") was dead code, and a "nothing is safe" fallback that emitted moves admission had already refused. **T7 — predicted versus actual collision, the only test that can tell us whether the trajectory model is right — is wired and has never been run**: it needs a recorded session. What exists, what it cost, and the one command outstanding: [`SPIKE3.2-OFFLINE-SOLVER.md`](../docs/hook/SPIKE3.2-OFFLINE-SOLVER.md). **T7 has since been run against six recorded sessions** -- 0 % false negatives on the first, and a residual of 4-15 misses per session on the rest (that document, sections 4.3-4.7). **And the operator's own reading of the dodge -- *"it considers a projectile's entire path dangerous"* -- produced the plan model on 2026-10-15:** a candidate is no longer a point scored against a bullet's whole polyline, it is a direction the character walks in, tested against where the bullet's body will be at the same instant, plus an imaginary goal point projected along the measured course. `NoAdmissible` on the third recording falls from 1148 frames to 6 and the decision gets *cheaper*, with T5/T6/T10 holding and T7 unchanged to the collision. The trade the change leaves open -- `keeps its course` against `overrides intent` -- is the operator's to settle live: [`SPIKE3.2-OFFLINE-SOLVER.md`](../docs/hook/SPIKE3.2-OFFLINE-SOLVER.md) section 11. **The first live session after it then found something older and larger: 51.8 % of that session's threat records were the player's own projectiles**, because the sensor filtered by type and never asked the client's `HitsPlayer` question -- the same flag `DODGE-ALGORITHM.md` section 3.1 had named as "the threat filter" since the beginning. They were 959 of its 971 "already touching" verdicts and 255 of its 1140 moves, and they made the dodge take the wheel around the operator's own muzzle every time they fired. The sampler now declines them before sampling, and both the trace and the report carry the count: [`SPIKE3.2-OFFLINE-SOLVER.md`](../docs/hook/SPIKE3.2-OFFLINE-SOLVER.md) section 12 |
| **4** — the bridge | not started. Phase 3 does not depend on it |

### Building and checking Phase 1

```powershell
.\\hook\build.ps1                 # all three artefacts -> hook\out\
.\\hook\tools\Test-HookAbi.ps1    # offline ABI check, no game needed
.\\hook\tools\Test-PeReader.ps1   # PE/MVID reader, incl. a tampered image
.\\hook\tools\Test-Injection.ps1  # end-to-end, against a synthetic host (127 checks)
.\\hook\tools\Test-HookOffline.ps1  # everything that needs NO game: the ABI self-test, the two generated tables,
                                    # both report fixtures, and the launcher's configuration
                                    # invariants (C1-C9: the four sources, their per-field
                                    # precedence, the discovery walk, the environment the child
                                    # is given and the world log's launch surface). The metadata
                                    # reader's self-test joins them only
                                    # when the game is installed -- it reads the game's own PE --
                                    # and is named as not run when it is not.
```

If a native build fails with `LNK1104: cannot open file ...Drelay.Hook.dll`, a leftover game
process is holding it — kill any `DarzasDominion` or `Drelay.Launcher` first. It is not the
documented retry-once quirk.

### Running auto-aim

The hook's target selection: full 360 degrees, two modes, invulnerable enemies never
chosen. [`AUTOAIM.md`](../docs/hook/AUTOAIM.md) is the manual; the short version is one switch.

```powershell
.\\hook\out\Drelay.Launcher.exe --game "E:\SteamLibrary\steamapps\common\Darza's Dominion" --autoaim closest
.\\hook\out\Drelay.Launcher.exe --game "E:\SteamLibrary\steamapps\common\Darza's Dominion" --autoaim highesthp
```

`closest` is the nearest damageable enemy in the equipped weapon's range; `highesthp` is
the one with the most current health. Both replace the game's own
`AimbotSystem.FindTarget` — the shipped desktop aimbot is the same mechanism behind a
cone of `player.AimAssist * pi / 20`, which is zero without the gear affix, so this is
the difference between "nothing" and "everything in range". Everything downstream of the
choice — the projectile leading, the shot geometry, the highlight on the target — is the
client's own code, unchanged.

It sets the game's own aimbot setting on (`Prefs.enable_aimbot`), and the game persists
it; that is named in the log when it happens and in [`AUTOAIM.md`](../docs/hook/AUTOAIM.md) §4. It also
implies `--frame-replacement`, for the ordering reason in [`AUTOAIM.md`](../docs/hook/AUTOAIM.md) §1 —
the flag has to be armed from the game's own thread before the target is ever called.
The target is compiled only once a world exists, so the run's own log is in
`<run dir>\bootstrap.log` — look for `auto-aim: the replacement answered its first call`.

### Recording the world (`--world-log`, the farmer's collection instrument)

This is [`PLAN-AUTOFARM.md`](../docs/hook/PLAN-AUTOFARM.md) §6: the operator walks around and the hook
writes everything the farmer cannot be written without — the map names, the lobby's portals and their
titles, and which world-change signal actually fires. One switch:

```powershell
.\\hook\out\Drelay.Launcher.exe --world-log
# the launcher prints "run directory : ..." at the top; the evidence is
# <run dir>\worlds.jsonl   (plus hook.log and bootstrap.log beside it)
```

`--game PATH` is only needed when neither the configuration file nor discovery names the install.
`--world-log-every 60` moves the periodic line from its 30-frame default (about twice a second at
60 Hz), and implies `--world-log`.

The file is JSON Lines: a header naming every accessor this build could not resolve, a frame line
every N frames carrying the map block (`Name`, `AssetName`, `Width`, `Height`, `PlayerId`,
`Difficulty`, `IsSoloRun`, `OpenedByKey`, `CanTeleport`, `Levels`), the world's flags (`SafeWorld`,
`VaultWorld`, `Space`, `PlayerDead`), the player's position, radius and health, every object in
`World.Activatables` with the portal's title, tag, lock and called state, the eight nearest enemies,
and whether the collision grid resolved and how big it is; and one `transition` line per world change
naming which signal fired (`MapInfo` reference, its name, `SafeWorld`, the grid's dimensions).

It is **evidence**: it writes a file, changes no decision, and is never on the dodge's path. The one
thing to check before trusting a session is that the file grew — `(Get-Item <run-dir>\worlds.jsonl).Length`
of a few hundred bytes means the header and nothing else. The flag sets the managed frame replacement,
the frame sensor and `DRELAY_WORLDLOG=1` together for exactly that reason: the writer is called from
the sensor's frame path, so the variable on its own produces a header and no frames. The switch is
command-line-only and not a configuration key, because a double-click should not start recording
evidence. [`PLAN-AUTOFARM.md`](../docs/hook/PLAN-AUTOFARM.md) §6.2 is the checklist of what to walk
and note while it records.

**The farmer itself is not in this repository.** The auto-farmer's decision code is a private module
(`PLAN-AUTOFARM.md` §13): the tree carries a `Farmer` stub with the same surface and no behaviour, and
a build made from a checkout cannot farm. To use it, put the module at `hook/private/Farmer.cs` and
rebuild, or copy a privately built `Drelay.Bootstrap.dll` over the one in `out\`. `build.ps1` and
`out\VERSION.txt` both say which build you have, and the release workflow refuses to publish one that
contains the module.

### Starting it with no arguments (the configuration file)

`Drelay.Launcher.exe` with **no arguments at all** is a supported way to start it, and it is what a
double-click does. It reads one file:

```text
%LOCALAPPDATA%\drelay\hook\drelay.config.json
```

and, if that file does not say where the game is, it looks for it: the Steam registry
(`HKCU\Software\Valve\Steam` → `SteamPath`, then `steamapps\libraryfolders.vdf` and every library in
it), then the `recordedFrom` path in [`known-build.json`](known-build.json), then the launcher's own
directory (which covers "unpacked into the game folder"). First hit wins.

**Discovery produces a candidate; the gate decides.** A directory is only accepted when the assembly
beside the apphost is the pinned build, so the `OpenGL\` apphost — which carries a *different*
assembly — is refused and named, rather than silently skipped for the next candidate.

Precedence is **per field**: command line > config file > discovery > compiled defaults. A config
that sets `GamePath` and two other fields keeps all three when you pass `--autoaim highesthp`, and a
flag the config does not mention keeps the launcher's default. There is deliberately no
environment-variable layer: the launcher deletes every `DRELAY_HOOK_*` name from the child's
environment, so a setting read from that namespace could never reach the hook.

**A first no-argument run writes that file if it is missing, and then runs with it** — so a
double-click is: the file appears, and the session aims at the closest damageable enemy and dodges,
which is what the written file says. `%LOCALAPPDATA%\drelay\hook` then holds what to change:

```jsonc
{
  // Required unless the game is discovered (§ above) or --game is given.
  "GamePath": "E:\\SteamLibrary\\steamapps\\common\\Darza's Dominion",

  "AutoAim": "closest",          // "closest" | "highesthp" | null (off)
  "Dodge": {
    "Enabled": true,             // run the solver every frame and log decisions
    "Act": true,                 // ...and let the decision steer the player.
                                 // false still solves and logs; it just never moves you
    "DecisionDisc": null,        // --dodge-disc; null means the solver's own default
    "WindowSeconds": null        // --dodge-window
  },

  "CollectDelayMs": 0,
  "DiscoveryWindowMs": 10000,
  "LiveSeconds": 5,
  "PauseAfterRun": false
}
```

The file is written by a first run and never overwritten, so editing it is how a session is
described from then on; setting `"AutoAim"` to `null` and `"Dodge"."Act"` to `false` leaves the hook
attached, reading the frame and running the solver's dry run, and moving nothing.

Every key is optional, `null` means "not set", unknown keys are ignored, and a *value* that is wrong
(a misspelled `AutoAim`, a number where a boolean belongs) refuses at startup naming the field — a
typo that silently resolved to "off" would cost a session spent watching the game not aim. The
test-harness switches (`--test-target`, `--frame-helper`, `--frame-replacement`, `--off`,
`--full-trace`, `--trace`, `--world-log`, `--world-log-every`, `--move-steer`, `--escape-test`,
`--assembly`, `--exe`) are **not** in the
schema and not in the template; a config that offers `FrameHelper` would be offering a switch that
allocates 64 KB per frame on purpose.

`"Dodge": {"Act": true}` and `--move-steer` both say *this is what steers the player*: typed together
on the command line they are refused, but `--move-steer` beside the file's `Act` wins for that run,
with a `note :` line saying so, so `Run-Phase3Session.ps1 -Experiment Sweep|Hold` still works. The
solver keeps running and logging; add `"Enabled": false` to the file for a calibration run that does
not pay for it.

If discovery fails, a no-argument run prints the reason, points at the file to fill in, and waits for
a keypress — **only** from a double-click. A run whose standard input is redirected never blocks, and
a refusal the user cannot fix by editing a file (a missing DLL, an OpenGL install) is printed and
exits without pausing. The design, the decisions behind it and the ones it rejected are in
[`HANDOFF-LAUNCH-CONFIG.md`](../docs/hook/HANDOFF-LAUNCH-CONFIG.md).

### Running a Phase 3 session

One command, and it needs a person at the keyboard — the character has to be in a world, and the
client's menus cannot be driven from outside. **The character will walk on its own** during the
steering experiments, so run it somewhere it does not matter.

```powershell
# the whole spike in one sitting: calibration sweep, then a held heading, then the nexus floor
.\\hook\tools\Run-Phase3Session.ps1 -Experiment All -Seconds 240 -RelayLog <relay.log>

# or one experiment at a time
.\\hook\tools\Run-Phase3Session.ps1 -Experiment Sweep  -Seconds 240
.\\hook\tools\Run-Phase3Session.ps1 -Experiment Hold   -Seconds 180
.\\hook\tools\Run-Phase3Session.ps1 -Experiment Escape -Seconds 120

# re-read a run that has already been played
.\\hook\tools\Run-Phase3Session.ps1 -AnalyseOnly <run-dir> -RelayLog <relay.log>
```

It writes the checks and the raw report to `hook\obj\cases\phase3-session\verdict.txt` and exits
non-zero if any check failed. **Let the run last as long as the phases need** — `sweep:240` is about
35 s of gameplay for its eight phases and `hold` 600 frames is about 10 s; a run closed early reports
"no phase summary" and proves nothing. The protocol, the escape test and what each outcome means are
in [`PHASE3-SCOPE.md`](../docs/hook/PHASE3-SCOPE.md) §5.

### Recording a trace, and running the offline solver (Spike 3.2)

The dodge's solver is a pure function of a plain-data snapshot, so it runs — and is tuned — with no
game at all. Its input is either a synthetic scenario or a recorded session.

```powershell
# 1. With no game: five scenarios, each with a known correct answer.
.\\hook\tools\SolverHarness\bin\Release\SolverHarness.exe --scenario dodge

# 2. Record a session.  The character walks under the experiment's control; -Trace only records.
.\\hook\tools\Run-Phase3Session.ps1 -Experiment Hold -Seconds 300 -Trace

# 3. Then, with the game closed, run the solver over what was played.  THIS is the only way to
#    check invariant T7 -- whether the model's predicted collisions are the ones that happened.
.\\hook\tools\SolverHarness\bin\Release\SolverHarness.exe `
    --trace <run-dir>\trace.jsonl --collisions <run-dir>\bootstrap.log
```

`--frame N` prints one frame in full — every candidate and the admission rule that refused it — and
it is the first thing to reach for when a scenario fails. `--config w=...,w=...` overrides the
weights, which is what "tune in the harness, never in a live run" means concretely.

**Check the file grew before trusting it:** `(Get-Item <run-dir>\trace.jsonl).Length` — a few hundred
bytes means the header and nothing else. `--trace` sets all three variables a recording needs (the
recorder, the managed frame replacement it is called from, and the Phase 2 sensor whose threat set is
its input), because a run missing any one of them writes a valid, empty file that is
indistinguishable from "no bullet came near". The harness now fails on an empty recording — assertion
**F** — rather than reporting every invariant vacuously held.

**The recording is expensive** — about 265 KB per 900 frames with two bullets alive — so it is off by
default. `-Trace` is the switch.
[`SPIKE3.2-OFFLINE-SOLVER.md`](../docs/hook/SPIKE3.2-OFFLINE-SOLVER.md) is the handoff: what exists, the eight
defects it took to make the solver move, and the one run still outstanding.

### Running the Phase 2 session

One command, and it needs a person at the keyboard: the sensor only has anything to read once a
character is in a zone, and the client's window renders black to every capture path tried, so its menus
cannot be driven from outside.

```powershell
.\\hook\tools\Run-Phase2Session.ps1 -Minutes 60
```

It launches the game with the sensor attached (`--frame-replacement`, `DRELAY_FRAME_SENSOR=1`), waits,
closes the game, and writes the four decision numbers and the criteria they are checked against into
`hook\obj\cases\phase2-session\verdict.txt`. `-AnalyseOnly <runDir>` re-reads a session that has
already been played. What the verdict means, and a menu-state run analysed as a worked example, are in
[`PHASE2-SCOPE.md`](../docs/hook/PHASE2-SCOPE.md) §4.13.

Running it against the game is one command; the launcher streams the hook's log live and
prints the evidence when the game exits. From an unpacked release zip in the game's folder, that
command is **just the program**, because the configuration file and auto-discovery supply the path:

```powershell
.\\hook\out\Drelay.Launcher.exe
```

or, when the game is somewhere the discovery walk does not look:

```powershell
.\\hook\out\Drelay.Launcher.exe --game "E:\SteamLibrary\steamapps\common\Darza's Dominion"
```

**One environment note.** Launching the game from the DSH agent harness fails to create its run
directory under `%LOCALAPPDATA%` — child processes from that harness are confined to the workspace —
so a harness-launched run needs `--runroot` pointing inside the repository:

```powershell
.\\hook\out\Drelay.Launcher.exe --game "E:\SteamLibrary\steamapps\common\Darza's Dominion" `
    --runroot "G:\coding\javamapp\drelay\hook\obj\live-runs"
```

From the operator's own shell the default is fine.

`--live-seconds S` (default 5) is how long the hook records after the *discovery window* closes, and
the evidence files are written when that window ends — so it *is* the run as far as the evidence is
concerned. **The frame read stays open when it closes**, because that read is the sensor the dodge
runs on; `--pause-after-run` (`DRELAY_KEEP_READING=0`) stops it, which is what every earlier release
did. The hook no longer waits for the discovery window to be useful: a target is patched about half a
second after the game first compiles it, so the dodge and auto-aim are live within a second or two of
launch. `--collect-delay` (default 0, was 30000) and `--discovery-ms` (default 10000, was 30000) still
exist, and they now bound the *reporting* — which targets this build produced while anyone was
watching — rather than the patch. The two acceptance runs that need an operator are scripts,
because both have assertions rather than just an outcome — run them from an elevated shell if the
game itself runs elevated, since a launcher at a lower integrity level cannot inject into it:

```powershell
.\\hook\tools\Run-Exit2a-LogDenial.ps1        # Exit 2a: a denied log target
.\\hook\tools\Run-Endurance.ps1 -Minutes 60   # Exit 1 / M3: the hour of play
```

**Read the `target-size` lines first.** They say, per target, whether a method of that IL
size was compiled *at all* during the run — which distinguishes "the monitored process never
ran that code" from "it ran and its IL differs from `known-build.json`". Those two have
different owners and the middle ground between them cost three rounds.

**Two findings from Phase 1 are worth reading before anything else**, because both correct something
this research previously asserted, and both are in [`PHASE1-SCOPE.md`](../docs/hook/PHASE1-SCOPE.md) §5:

- **the shared block's layout is not hand-computable.** The first attempt at `[FieldOffset]` values was
  wrong in three places at once — and the answer is not a better guess. The layout is now *generated*
  from the native header (`tools/gen_shared_layout.py`), and the generator's model is itself checked
  by a compiled probe (`native/SizeProbe.c`) that the build runs and diffs. The generator's model was
  wrong twice, and the probe is what said so both times;
- **the `getMethodDefFromMethod` slot is determined by probing candidates at runtime, not by a
  hardcoded constant.** An earlier reading held that the slot was `0x7A` and that `0x76` was
  `runWithErrorTrap` on .NET 9 — **the live run falsifies that**: against the real game slot `0x7A`
  returned token `0x00000000`, and slot **`0x76` returned the correct `0x06001FC5`**, matching the
  token Phase 0 pinned for `frameTick`. So `0x76` *is* the correct slot on this runtime, and it is the
  runtime probe — not a written-down constant — that makes relying on it safe.

The gate is the thing that makes the rest safe rather than brave: it is what turns "the hook
crashed the game after an update" into "the hook printed one line and did nothing"
([HOOK-ARCHITECTURE.md](../docs/hook/HOOK-ARCHITECTURE.md) §6).

---

## What "proactive" means here, precisely

The word does a lot of work in this project, so it is worth pinning down before anything else, because
the honest answer is narrower than it sounds.

**Reactive** is the current rule: it reads the server's `GmHealthUpdate`, which reports damage *after*
the server has applied it, and escapes. Its decision point is downstream of the damage.

**Proactive** here means exactly one thing: the decision point moves **upstream of the damage**, to the
instant the client itself resolves a hit — which is measurably one server round trip *before* the
server applies it. It does **not** mean the damage is prevented. The hook's two tools are, in order:
**move out of the way** (dodge), and **leave before it lands** (preventive escape). Both are actions
taken *before* the damage, which is what makes them proactive; neither makes damage impossible.

And the scope of the improvement is bounded by measurement, not by hope: **74 % of measured HP falls
have no client-side hit claim at all**, so for most damage there is no earlier decision point to move
to. The hook is proactive wherever the client is the first to know, and helpless wherever the server
is. [RISKS.md](../docs/hook/RISKS.md) §2b and §5 list those cases individually.

---

## The answer in one page

### The frustration was real, and the reason is now measured

The current auto-nexus reads the server's `GmHealthUpdate` and escapes when HP is low. That packet is
a **notification of damage already applied**, not a warning — so the rule can never act before the
damage, and no amount of proxy logic changes that. Measured over the recorded sessions:

| quantity | value |
|---|---|
| `GmHealthUpdate` reflecting a hit, after the client resolved that hit | **+55.5 ms median, +98.1 ms p90** |
| one-way client→server leg (inferred from RTT) | **31 ms** fast cluster / **113 ms** slow cluster |
| server tick period | **200 ms** |
| worst burst inside one 200 ms window (measured over a lookahead window) | **493 HP = 81.1 % of max, ending in death** |
| `Escape` → `EscapeAck` at the relay | **290 ms median** |

> **A correction to an earlier reading of this project.** It was previously stated that the client
> sends no damage acknowledgement, and therefore that suppression was not an option. That is **half
> wrong**: the client *does* declare projectile hits, in `GmHit`/`ProjHit` (id 102), whose `ObjectId`
> is the local player when `PlayerProj = 0` — verified on 161 of 580 captured packets. What remains
> true is that it is not a general escape hatch: **74 % of HP falls (571 of 775) have no such claim
> within 400 ms**, and there is no equivalent for AoE, beam, contact or tick damage. The earliest
> client-side knowledge is still the collision test, not the health packet, because the client's own
> per-hit path is purely presentational (`TIMING.md` §4.3).

### The hook buys time, not clairvoyance

The client decides a bullet is hitting the player in `Projectile.HitCharacter()`
(`Objects/Projectile.cs:1514`), computes the exact damage, uses it **only to draw a number**, and then
tells the server — which applies the damage *afterwards*. Nothing in the client ever writes
`Player.Health`; only `GmHealthUpdate` does. So at the instant of that collision decision, the damage
is still in the server's future.

That is the window. It is worth **≈55–98 ms** over the current rule, plus one frame, and the hook is on
the right side of it by construction rather than by tuning.

Note what the burst measurement says about the alternative. The server emits **one reading per hit,
back to back**, not one summary per tick — so a burst is visible but only as a stream of readings
inside one or two milliseconds, and the first reading of the stream is already damage being applied
(`Game_Slave#11`: 439 HP of 608 gone in eight readings spanning 2 ms, dead one reading later). A hook
cannot act earlier than the first reading either, **unless it predicts the hit**. That is the whole
argument for the collision-anchored design rather than a threshold at the reading.

### But it does not make the nexus unnecessary in general — and this is the honest part

**74 % of measured HP falls (571 of 775) have no client-side hit claim at all.** The client only
claims hits it resolves itself — and the only claim packets in the assembly are for projectiles,
sweeps and beams. **AoE and bomb damage produces no claim whatsoever** (`Entity.HitBy(AoeData)`
contains no `SendPacket`; it resolves on a timer server-side). So for AoE, bomb detonations,
damage-over-time ticks and point-blank spawns the server is the only witness, and no in-process design
sees them coming.

So "the auto-nexus would never need to be triggered" is **half right**. A hook makes the nexus
*better and rarer* — it escapes in cases the reactive rule cannot — and it enables a real dodge for the
projectiles the client does see. It does not make damage preventable in general, and anyone promising
that is not reading the measurements.

### Why this was worth doing anyway

Three measured facts make the hook decisively better than the current rule, independent of dodging:

1. the current rule **can act on stale HP** — the recorded case is an escape injected on a 1 % reading
   **151 ms after HP had already reached zero**;
2. the current rule **cannot distinguish "a hit landed" from "a hit is coming"**, so it must be tuned
   conservatively in the direction that costs runs;
3. a dodge is genuinely achievable here, and was **not** achievable through packets: the stream never
   re-broadcasts a projectile's position, so a packet-only dodge is dead reckoning with unbounded
   error. In-process, the true position is one field read away.

### Why it is tractable

Five facts, all verified against the shipped binaries:

| | |
|---|---|
| `DarzasDominion.dll` is **pure IL** — no ReadyToRun, no native code | every game method is JIT-compiled, so a JIT-level hook sees all of it |
| `System.Runtime.TieredCompilation` is **`false`** | each method is compiled **once**; a patch is permanent, and the whole tier-promotion hazard does not exist |
| the JIT's vtable is **four slots, `compileMethod` at 0, `getVersionIdentifier` at 2** | verified by disassembling the shipped `clrjit.dll` — note the community tools hardcode slot **4**, which was right for .NET Core 3.1 |
| the client **never applies damage to itself** | so the collision decision is genuinely ahead of the server's damage |
| no anti-cheat, no integrity check, no driver | a plain self-contained .NET 9 apphost |

And the two things that would normally be the hard part are avoidable:

- **No trajectory reimplementation.** `ProjDesc.GetPosition` handles straight, wavy, parametric,
  boomerang, orbiting, curving, accelerating and bouncing families. The hook **calls it** instead of
  reproducing it — which also makes prediction exactly consistent with collision by construction. The
  reference engine reached the same conclusion independently.
- **No damage reimplementation.** Every damage constant has been recovered from the decrypted constant
  pool, so it *could* be recomputed — but the hook **calls `Entity.GetDamageTaken`**, because eight
  mitigation steps read live state and being subtly wrong means escaping one hit too late.

---

## The documents

Read in this order for the argument; the entries whose *what it is* column starts with **plan** are
the forward-looking ones — two of them (the auto-farmer and the hook control channel) are written but
not built, and the third (enemy threats) is built and has been corrected once: read
[`PLAN-ENEMY-THREATS.md`](../docs/hook/PLAN-ENEMY-THREATS.md) §8.8 before §8.1–§8.7, because the
first delivery passed every test it had and changed no decision.

| document | what it is | read it when |
|---|---|---|
| **[CLIENT-FINDINGS.md](../docs/hook/CLIENT-FINDINGS.md)** | the verified client, with `file:line` for every claim — the frame loop, the projectile model, the collision predicate, the damage arithmetic, the movement law, and the four best hook points | you want to know what is actually true and how we know |
| **[TIMING.md](../docs/hook/TIMING.md)** | the measured window: health cadence, RTT, server tick, the largest invisible burst, and a scenario-by-scenario deadline table | you want the numbers that justify the whole approach |
| **[CLIENT-MAP.md](../docs/hook/CLIENT-MAP.md)** | the exhaustive reverse-engineering map (3,389 lines): every field, every accessor, the candidate hook list, and ten ranked unknowns each paired with the experiment that settles it | you are implementing and need a specific answer |
| **[HOOK-ARCHITECTURE.md](../docs/hook/HOOK-ARCHITECTURE.md)** | how the hook gets in and stays in: JIT detour vs profiler, injection, identity by IL hash, GC and pooling safety, version tagging, the frame budget, and the startup self-checks | you are building the hook |
| **[DODGE-ALGORITHM.md](../docs/hook/DODGE-ALGORITHM.md)** | what it computes: the threat set, trajectory sampling, the solver, the temporal test, the intent ladder, damage estimation, and the preventive-escape decision. §12 is the borrow/don't-borrow ledger against the reference engine | you are building the dodge |
| **[JAVA-BRIDGE.md](../docs/bridge/JAVA-BRIDGE.md)** | keeping the proxy in Java: why the decision cannot cross the bridge, whether JNA is needed (it is, minimally), sequence-stamped shared memory, and failure detection | you are wiring the hook to the relay |
| **[BRIDGE-RESEARCH.md](../docs/bridge/BRIDGE-RESEARCH.md)** | the source-cited evidence behind the bridge design: JNA / JNI / FFM benchmarks, Windows named-pipe and shared-memory limitations verified against the OpenJDK and Microsoft sources, and the measured loopback/pipe numbers | you want to check a bridge claim or revisit the transport choice |
| **[IMPLEMENTATION-PLAN.md](../docs/hook/IMPLEMENTATION-PLAN.md)** | **the deliverable**: five phases with spikes, deliverables, exit criteria, a test list, milestones and the decision point | you want to know what to do on Monday |
| **[PHASE3-SCOPE.md](../docs/hook/PHASE3-SCOPE.md)** | Phase 3's entry plan: what Phase 2 left, the run that reshaped the spike order (88 client-resolved collisions, 100 % tracked, 74 % with no warning at all), why the relay cannot answer the movement question, what was built for Spike 3.1, the escape floor's own test, the live protocol, and — §5.1 — **how to read a run's `crash.txt`, which is not a crash report about the run** | you are starting Phase 3, running the steering or escape experiments, or looking at a `crash.txt` |
| **[PHASE3-HANDOFF.md](../docs/hook/PHASE3-HANDOFF.md)** | **The movement question, answered, and the next step.** The live sweep's numbers (write landed on every frame, 1428/1951 frames moved, ≤ 2° heading residual), the four defects between "the actuator is built" and "the actuator works" — three of them misdiagnosed first — what the run does *not* establish (the wire's `GmKicked`, the unmeasured rotation term, terrain wedging, the sensor and actuator never sharing a frame), the pipeline mapped to the code that exists, and **the build order for the solver, offline** | you are about to write the dodge, or you are reading any earlier document's account of why the game would not start |
| **[AUTOAIM.md](../docs/hook/AUTOAIM.md)** | **the auto-aim module**: the two modes, the target it replaces and why that one, what it borrows from the client instead of reimplementing, the invulnerability and range rules, the one settings side effect it has, how to read its log lines, and what it does *not* prove | you are running, reading or changing the target selection |
| **[`docs/hook/HOOK-BUILD-MATRIX.md`](../docs/hook/HOOK-BUILD-MATRIX.md)** | the verified build, the exact procedure to re-verify the hook after a game patch, what the gate cannot check, and the build-specific findings a maintainer must not "simplify" away | you are installing, updating or re-verifying the hook |
| **[HANDOFF-DIRECTED-DODGE.md](../docs/hook/HANDOFF-DIRECTED-DODGE.md)** | **the open task, written for a session with no context**: the dodge takes over movement when it dodges, and the operator wants it to pathfind toward the direction they are walking instead. The defect measured (on 85.5 % of the frames it steered the player was holding a direction, and on 97.8 % of those the goal was the body's own course — mean deflection 47.4°, three-quarters of the moves held at a third of a step), the one field whose meaning has to be verified first, the design in the order to try it, ten traps, the baseline table for all five recordings and the exact commands | you are implementing the next change to the dodge, or you are wondering why the dodge feels like it takes the wheel |
| **[VERIFICATION.md](../docs/hook/VERIFICATION.md)** | the playbook: five layers from the static gate to the on-the-wire checks, what each can and cannot establish, and the falsification test that must pass before the model may be trusted | you are about to run something live |
| **[RISKS.md](../docs/hook/RISKS.md)** | the risk register, the verified dodge-vs-escape interaction bug, and an explicit list of what this cannot do | you are deciding whether to start |
| **[PLAN-ENEMY-THREATS.md](../docs/hook/PLAN-ENEMY-THREATS.md)** | **built, then corrected** — read §8.8 before §8.1–§8.7. Enemies enter the dodge's danger field as stationary threats, so the path stops routing across a mob. `EnemyReader` fills `DodgeSnapshot.Bodies` from `World.GetEnemies()` (rule 7 stopped being vacuous), the body radius is the only radius that may refuse a candidate, and the emitter pad is a cost. **The first delivery passed every test it had and decided `NoThreat` on every frame whose only hazard was a body** — the no-threat gate tested the body against admission's reach and not the planner's, the emitter term read a point one frame away instead of a direction, and the intent early-out obeyed a heading that walked into a muzzle. §8.8 has the four defects, the `E10` invariant that now fails when a decision ignores a body on the route, and the re-run numbers | you are doing the smallest useful dodge change, or you are starting the farmer (the enemy-body rule is what gives the farmer its arrival behaviour) |
| **[PLAN-AUTOFARM.md](../docs/hook/PLAN-AUTOFARM.md)** | **plan, not built**: the auto-farmer — walking to the closest enemy (a point goal for the existing path planner), the lobby state machine and portal entry (`GmActivateObject`, not the dead `EnterPortal`), the recorded route with bounded smooth deviation, and the dungeon whitelist with clear-and-advance. §3 records why firing is **not** hook work at all (auto-fire is a client input option and auto-aim is already built); §6 is the operator's data-collection checklist; §10 is the build order and §10.1 what may run in parallel | you are building the farmer, or you want to know exactly which of it is blocked on live data |
| **[PLAN-HOOK-CONTROL.md](../docs/bridge/PLAN-HOOK-CONTROL.md)** | **plan, not built**: live toggles from the dashboard and starting the hook from the relay. The transport is the **existing `shared.block` file** (already mapped by three processes and already read on the native frame path) with a new control region, not the pagefile section `JAVA-BRIDGE.md` proposed — that one was tried and replaced by a file. Plus `--config <path>`, the launch button, and the elevation ordering problem | you are building items 3a/3b, or you are about to add any new setting and want to know whether it can be live |

Supporting evidence lives in [`il/`](il) (the IL dump of the exact build), and the tools that produced
every number are in [`tools/`](tools) — see below.

---

## The plan, in five lines

| phase | question | risk | estimate |
|---|---|---|---|
| **0** | what are we targeting, and can we prove it has not changed? | very low | 1–2 days |
| **1** | can native code get into this process, early, and survive? | **high** | 3–5 days |
| **2** | can we observe the truth, and does our model match it? | medium | 1–2 weeks |
| **3** | **can we move the player out of the way — and escape only when we cannot?** | **highest** | 3–6 weeks |
| **4** | does the relay see the hook, and is that useful? | low | 3–5 days |

**The dodge and the escape are one phase, and the escape is its floor** (merged 2026-10-13). The
client's `World.RequestEscape()` is reached at exactly one point in the decision — when the projected
damage over the horizon is lethal **and no admissible move avoids it** — so the deliverable is a dodge
that never needs the nexus where dodging is possible, rather than a predictive nexus that still ends
the run. The relay bridge, formerly phase 5, is now phase 4 and is additive to both.

**Phase 3 carries the whole risk.** Its risk is not the algorithm; it is whether the server accepts
hook-steered movement, which is the one question in this design with no precedent in the reference
engine and no measurement in the captures. That question is answered by a spike that uses **no dodge
logic at all**, and it can be probed **from the existing proxy, with no hook at all** — see
[RISKS.md](../docs/hook/RISKS.md) §2a.

**Phase 2 was the decision point, and it has been taken: the project proceeds to Phase 3.** Its four
numbers — how much warning prediction gives, the false-negative and false-positive rates, and the
per-frame cost — are what license the build. Had they been bad, the honest move would have been to
stop there and publish the measurement; with the escape folded in as a floor there is no longer a
consolation deliverable at that point, which is why the numbers were worth reading before Phase 3
starts.

---

## Four things worth knowing before starting

**1. The most valuable artefact is not the dodge.** It is the Phase 2 log that compares every predicted
collision against every collision the game actually made. A dodge's quality cannot be established by
unit tests — the reference engine's own audit says *"live gameplay remains unverified"* for much of its
newest code — but the *model's* correctness can be, and that log is the only thing that can tell us
whether the prediction is right. Build it before the solver.

**2. There is a verified interaction hazard waiting, scoped to challenge runs.**
`World.RequestEscape()`'s **channelled** branch cancels on movement:

```csharp
// World.cs:13043-13047
if (GameScene.GameInput.Moving) { CancelEscapeCast(); return; }
```

A dodge that steers **cancels an in-progress nexus cast in the same frame**. Precedence must be
designed before the dodge is enabled live, not patched after. **Scope it correctly, though:** that
branch is reached only with the `Impermanence` totem — ordinary escape is instant and is never
cancelled by movement. An earlier draft of this research stated that as a general hazard; it is not,
and the correction is recorded in [CLIENT-FINDINGS.md](../docs/hook/CLIENT-FINDINGS.md) §5.3. The practical
consequence is that a test suite which never equips the totem will pass while the real bug sits
untouched.

**3. Three things are deliberately out of scope, and one of them constrains the design.** AoE and bomb
damage is left to the player; point-blank spawns are treated as a positioning problem rather than a
reaction problem (and are mostly avoidable); and there is **no melee or contact damage** at all, which
removes a row rather than adding one.

The AoE decision is the one that costs something, because it makes **intent preservation a
requirement rather than a preference**: if the dodge fights the direction the player is steering, it
has taken away the tool they need for the channel it does not cover. The mechanism that keeps the
trade safe is a single rule — **an AoE volume may affect a candidate's score, but it must never remove
a candidate** ([DODGE-ALGORITHM.md](../docs/hook/DODGE-ALGORITHM.md) §5.7, [VERIFICATION.md](../docs/hook/VERIFICATION.md) §3
invariant I11).

**4. The reference repo was worth reading, but almost none of it should be copied.**
[Evergreen-Techworks/realm-engine-client](https://github.com/Evergreen-Techworks/realm-engine-client)
is a mature dodge engine for a different game, and what transfers is its **reasoning**, not its
numbers: a two-strike projectile retirement policy, fixed-step trajectory sampling, anchoring on the
live position every frame, freezing past the end of a trace rather than extrapolating, the
time-parametrised polyline as the sensor/solver interface, safety as a hard constraint with preference
as a score over the safe set, and degrade-the-candidate-set-never-the-tests under budget pressure.

What must **not** be copied: its time constants (calibrated to a 5 Hz server), its hit geometry (a
per-axis square, where our client's test is verified to be `DistanceSquared < r²` on circles), its
player half-extent (its player is a point; ours has a real radius the game adds for us), and its entire
IL2CPP offset layer. [DODGE-ALGORITHM.md](../docs/hook/DODGE-ALGORITHM.md) §12 has the full list, both directions.

---

## Tools and evidence in this directory

Everything is reproducible. Nothing here is hand-waved from a blog post.

**Deterministic, runnable now:**

| tool | does |
|---|---|
| **[`tools/Fingerprint.ps1`](tools/Fingerprint.ps1)** | **the Phase 0 gate, and the first thing to run before any session.** Fifteen static checks — tiered compilation, IL-only status, the assembly's size *and SHA-256*, the `clrjit` vtable's code-slot count, both runtime DLL versions, and **every pinned target's IL hash** — with a non-zero exit on any mismatch. It resolves `getJit` from the export directory itself and re-hashes each target's instruction stream straight out of the assembly, then requires `il_index.py` to regenerate the same table, so it needs no other tooling. Mutation-tested at both levels: a same-size byte flip anywhere in the assembly fails loudly, and a single flipped byte *inside a target's IL* is named as that target. Its expected values live in [`known-build.json`](known-build.json) |
| **[`tools/il_index.py`](tools/il_index.py)** | **builds the target table, and is the authority on it.** A self-contained PE/CLI metadata reader — no `ilspycmd`, no `dotnet`, no network — that resolves the hook's **six** detour targets to `(declaring type, name, arity, metadata token, RVA, IL size, SHA-256 of the exact IL bytes)` and writes them into `known-build.json`. Cross-checks every target against the IL dump on RVA, code size and parameter count; `--check` also compares the record against the table and exits non-zero on a mismatch (a hardcoded key list used to drop a target silently); `--mutation-test` proves a one-byte IL edit moves exactly that target's hash |
| **[`tools/check_opcode_table.py`](tools/check_opcode_table.py)** | **the opcode table's own witness.** Walks every method body the IL dump describes with the `OPCODES_1` that `il_index.py` actually ships and compares the visited offsets against the dump's. It exists because the table was one entry short (`br`, `0x38`) for its whole life — which is what refused the auto-aim target as `TARGET TABLE NOT TRUSTWORTHY` — and because the `MoreSects` offset choice inside `method_body()` reads that table, so a wrong width can move which bytes get hashed. Exits non-zero below either threshold; the numbers are in [`docs/hook/HOOK-BUILD-MATRIX.md`](../docs/hook/HOOK-BUILD-MATRIX.md) |
| **[`tools/il_targets.py`](tools/il_targets.py)** | lists candidate methods on a type so a new target can be pinned **without depending on its name** — declaring type, arity, and the return and parameter types decoded from the signature blob, plus IL size and the IL dump's RVA line. It exists because the client's obfuscator gives the movement integrator a metadata name of private-use code points that **cannot be found by searching for the name the decompiler prints** (zero hits in the 89 MB dump). It never writes the record, so `il_index.py` stays the one authority. `--self-test` checks the reader against the four signatures the documents state independently |
| **[`tools/steering_report.py`](tools/steering_report.py)** | reads a Spike 3.1 steering run and says whether the hook moved the player: the pilot's read-back (did the write land), the realised displacement (did the integrator act), the per-phase commanded-vs-observed headings (was the direction obeyed), the relay log's `GmKicked` (did the server object) and the `GmMove` cadence. It also reads an `--escape-test` run — whether `World.RequestEscape()` was reached, returned or threw, and whether a `GmEscape`/`EscapeAck` left the machine — and it keeps the two experiments apart, so a run that asked for only one is not failed for the other. It fails loudly on a run whose target was never patched, because that otherwise reads exactly like a pilot that chose to do nothing, and its own fixture (`tools/tests/test_steering_report.py`) asserts the good, unpatched, kicked, escape, escape-too-soon and empty cases |
| **[`tools/Test-FingerprintGate.ps1`](tools/Test-FingerprintGate.ps1)** | the gate's own falsification test, at the level that matters: it copies the install to a sandbox, flips one byte **inside a target's IL** with the file length unchanged, and asserts the gate exits non-zero naming *that* target while the other three still pass. A gate that cannot be shown to fail is not a gate |
| [`tools/Test-JitVtable.ps1`](tools/Test-JitVtable.ps1) | locates `clrjit!getJit`, resolves `&g_CILJit`, and dumps the `ICorJitCompiler` vtable. **This is Phase 0's most important instrument** — it proves the four-slot layout and which slot holds `compileMethod` on the exact runtime build |
| [`tools/Test-ReadyToRun.ps1`](tools/Test-ReadyToRun.ps1) | reads the COR20 `ManagedNativeHeader` and reports IL-only vs ReadyToRun. Verified on this install: `DarzasDominion.dll` and `MonoGame.Framework.dll` are **IL-only**; `System.Private.CoreLib.dll` **is R2R** |
| [`tools/Get-PeExports.ps1`](tools/Get-PeExports.ps1) | PE export lister; used to confirm `coreclr.dll` exports `GetCLRRuntimeHost` and `g_dacTable`, i.e. that in-process DAC reads are on the table |
| [`tools/poolresolve.py`](tools/poolresolve.py) | resolves `hehe(N)` constant lookups in the IL to real values, by pairing each index with the return type of the call that consumes it. This is what recovered the movement law and the damage law |
| [`tools/il_constants.py`](tools/il_constants.py) | resolves obfuscated literal *values* straight out of the IL dump |
| [`tools/g.py`](tools/g.py) | bounded grep-with-context, written because `World.cs` is 24,381 lines and cannot be read whole |
| [`pe_probe.py`](work/pe_probe.py) | PE/CLI/R2R probe — proves the assembly is pure IL with no ReadyToRun block |
| [`md_probe.py`](work/md_probe.py) | .NET metadata table census — 23,743 methods, 25,105 fields |
| [`_pooldecode.py`](work/_pooldecode.py) | decrypts the obfuscator's constant pool (1035 ints / 16 longs / 787 floats / 229 doubles), verified against eight independently-known packet ids |
| [`tools/analyze_size_histogram.py`](tools/analyze_size_histogram.py) | decides whether a `hook.log.sizes` "ZERO compiled" verdict is a fact about the game or an artifact of the fixed-size histogram table |
| [`tools/pe_probe.py`](tools/pe_probe.py) | minimal PE inspector: subsystem, COM descriptor, imported DLLs and functions |

```powershell
# the one command to run before any session
.\\tools\Fingerprint.ps1

# the target table behind it, and the proof that the gate can fail
python .\\tools\il_index.py --check
python .\\tools\il_index.py --mutation-test
powershell -NoProfile -ExecutionPolicy Bypass -File .\\tools\Test-FingerprintGate.ps1
```

The procedure these fit into — what to run after a game patch, what each result licenses, and
what the gate cannot check — is [`docs/hook/HOOK-BUILD-MATRIX.md`](../docs/hook/HOOK-BUILD-MATRIX.md).


**Produced during this research** (the measurement scripts behind [TIMING.md](../docs/hook/TIMING.md) and
[CLIENT-MAP.md](../docs/hook/CLIENT-MAP.md)): `health_cadence.py`, `rtt_and_tick.py`, `report_numbers.py`,
`projhit_damage_report.py`, `damage_causality.py`, `hitreport_and_deaths.py`,
`field_order_solver.py`, `validate_varint.py`, `client_tick.py`, `report_numbers.py`, and others,
plus their raw outputs as `_*_raw.txt` / `_numbers.txt`.

**Evidence files:**

| file | what |
|---|---|
| [`il/client-5.8.2.il`](il) | the full IL dump of `DarzasDominion.dll` for build 5.8.2 (85 MB, 2,218,925 lines) — the input to `poolresolve.py`, and the independent cross-check `il_index.py` verifies every target against (RVA, code size and parameter count) |
| [`known-build.json`](known-build.json) | the verified build record: the pinned values `Fingerprint.ps1` compares against, and the `targetMethods` table `il_index.py` generates |
| `_numbers.txt`, `_cadence_raw.txt`, `_rtt_raw.txt`, `_projhit_raw.txt`, `_deaths_raw.txt` | the raw measurements [TIMING.md](../docs/hook/TIMING.md) is built from |

---

## What is deliberately not in this directory

- **The hook is a Phase 1 skeleton plus the Phase 2 sensor.** Phase 0 of the plan said the first
  thing to write was the fingerprint gate, not the hook — and that gate is here. Phase 1's
  `Drelay.Hook.dll`, `Drelay.Bootstrap.dll` and `Drelay.Launcher.exe` (in [`out/`](out)) are built,
  have been run against the live game, and are closed; Phase 2 built the sensor on top of them and
  closed on 2026-10-13 ([`PHASE2-SCOPE.md`](../docs/hook/PHASE2-SCOPE.md)).
- **No changes to the Java relay.** The bridge design in [JAVA-BRIDGE.md](../docs/bridge/JAVA-BRIDGE.md) is
  additive by construction: the relay runs identically with and without the hook.
- **No game files were modified**, and none need to be on the recommended injection path. The gate
  and the indexer are read-only; the one test that writes anything writes to a temporary sandbox copy.
- **No pinned constants file.** The movement law, the damage law and the trajectory model were all
  recovered, but they are **called** rather than frozen — see [IMPLEMENTATION-PLAN.md](../docs/hook/IMPLEMENTATION-PLAN.md)
  Phase 0 item 3 for why a constants file would be a hazard rather than a convenience.
- **No claims without a `file:line` or a measurement.** Where something is genuinely unknown it is
  marked as such and paired with the experiment that would settle it — [CLIENT-MAP.md](../docs/hook/CLIENT-MAP.md)
  §10 lists them all, and two of the three that were "blocking" have since been resolved by the
  constant-pool decode.
