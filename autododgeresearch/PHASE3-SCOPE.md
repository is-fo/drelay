# Phase 3 — scope, entry plan, and the measurement that reshapes the spike order

**Date opened:** 2026-10-14. **Branch:** `autododge-research`.
**Read with:** [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §"Phase 3 — Dodge, with the escape as
its floor" (the criteria this document executes), [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §5–§7 (the
solver this phase builds), [RISKS.md](RISKS.md) §2a (the movement question) and §3d (the
two-controller hazard), [VERIFICATION.md](VERIFICATION.md) §5 (how each is proved),
[PHASE2-SCOPE.md](PHASE2-SCOPE.md) §7 (the state Phase 2 closed at, with its two waivers), and
[MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md) §0.2 (the mechanism that makes managed code on
the game's thread legal).

> **Status 2026-10-14: Spike 3.1 is ANSWERED YES** — see [PHASE3-HANDOFF.md](PHASE3-HANDOFF.md),
> which carries the measurement (`20261014-040859-p22748`), the four defects that stood between a
> built actuator and a working one, what the run does *not* establish, and the next step: the solver,
> offline. **§5.1's account of the startup crash below has been corrected there and here — it was
> never the `PrepareMethod` pass.**

---

## 1. Where Phase 3 starts

Phase 2 is closed by operator decision on 2026-10-13, with two recorded waivers and one measurement
carried forward ([PHASE2-SCOPE.md](PHASE2-SCOPE.md) §7). What the closed phase leaves behind, as
facts rather than as plans:

- **the sensor reads the real client.** 19 349 frames of live play walked
  `World.GameObjectLayer.Node._children` and tracked 12–28 live projectiles per frame; 585 000
  trajectory sample sets were gated by the linearity rule; per-frame read cost `p50 <= 50 µs,
  p99 <= 100 µs` with eleven frames above 2 ms in 19 201;
- **the collision comparison is installed and correct at the event level.** `HitCharacter` is
  observed only on its `true`, the mistake that produced Phase 2's over-counted false-negative number
  is fixed in code and has an offline fixture that fails if it regresses;
- **the reference-mechanism question is answered.** A managed replacement entered by MinHook's `jmp`
  at a target's entry — the game's own `call` becoming a managed-to-managed call, with the original
  reached through a `delegate* managed` trampoline — ran clean under 650 forced compacting
  collections, seven times ([MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md) §0.2). This is what
  makes Phase 3's two actuators possible at all;
- **the straight fraction is measured: 0.695** time-weighted, over 406 077 linear and 178 611 curved
  sample sets.

What it does **not** leave behind:

- a per-frame cost that fits the solver's budget. The sampler's `p99` is in the 150–500 µs bucket
  against a 150 µs slice, and the operator waived the cost criteria for the phase close
  ([PHASE2-SCOPE.md](PHASE2-SCOPE.md) §4.15). **The cost work is Phase 3's**, and it belongs with the
  solver that has to share the frame;
- a live session with the corrected collision counter. The corrected instrument has not been on a
  live run, which is why Phase 2's false-negative number is carried forward rather than closed.

And one item Phase 0 has left open since the plan was written: the `GmHit`-drop trial, which needs a
live session and does not gate the hook ([IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md), Phase 0
item 5). It stays open, and §5 records why it is now cheap to fold into a Phase 3 session.

---

## 2. **The measurement that reshapes this phase**

**The operator's run of 2026-10-13 — the last session before this document — changes what Phase 3's
first move should be, and the change comes from a number the plan did not have.**

That run's collision log contains **88 `HitCharacter` decisions**:

| | count | share |
|---|---|---|
| **tracked by the sensor** | 88 | **100 %** |
| **predicted before it happened** | 23 | **26 %** |
| **tracked, and not predicted** | 65 | **74 %** |
| tracked by the sensor and unknown to it | **0** | 0 % |

Every un-predicted collision is `tracked=yes predicted=NO warning=0.0ms`, and the shape of the
positions says why: the bullets arrive at **the player's own coordinates** —
`r=0.2` at `(1204.9,1991.9)` while the player is at `(1204,1990)` — which is a point-blank spawn, not
a model failure. The predicted ones are the ones with a visible approach, and their warnings run from
**18.4 ms to 619.4 ms** with a mean of 88.2 ms.

Two things follow, and both are load-bearing.

**First: the sensor's coverage is not the problem; the warning window is.** Phase 2's first criterion
("prediction agrees with the game's own collision decision on the great majority of hits") is met in
exactly the sense that matters for a dodge: **zero collisions happened on a bullet the sensor did not
know about.** What it missed was *timing* — 74 % of client-resolved collisions had no warning at all
because there was nothing to warn about. That is not a defect to fix; it is the same 74 % the
research already measured from the packet side ([RISKS.md](RISKS.md) §2b), now reproduced
independently from inside the process. **The dodge's addressable surface is the 26 % with warning,
and the honest measure of the feature is what it does inside that 26 %.**

**Second: the plan's spike order is wrong, and its own text half-says so.** Spike 3.1's first
suggested test is:

> **The existing `drelay` proxy can help here, with no hook at all.** … injecting a plausible `Move`
> and watching for a kick is a test the proxy can run today, and it is the cheapest possible way to
> learn whether the server validates movement. **Do this first.**

**That is not the cheapest way, and it does not answer the question the phase is gated on.** §3 is
the argument, and it is one paragraph long:

---

## 3. Why the proxy cannot answer Spike 3.1, and what it can still do

**The question Spike 3.1 exists to answer is not "does the server validate movement".** The operator
already settled that — it validates *distance per game tick*, which makes the constraint an
arithmetic one this design already respects ([RISKS.md](RISKS.md) §2a). The open question is the one
that decides whether the dodge exists:

> **Can the hook cause the client to move the player, through the client's own input path, and have
> the result accepted?**

Three reasons the relay cannot answer it, in increasing order of severity:

1. **The relay has no access to the one actuator the design chose.** `drelay` is a TCP man in the
   middle. It can write a `GmMove` byte-for-byte, and that is a *different mechanism* from writing
   `GameScene.GameInput.MoveAngle` — the one the plan ranks first, the one the reference engine's
   "authoritative-move principle" points at, and the one whose safety argument is that the client
   generates the position, the speed law, the collision and the history itself. A relay test proves
   something about `GmMove`; it proves nothing about the mechanism the dodge uses.
2. **A relay `GmMove` has to invent the packet's own history, and the wire format is not fully
   decoded.** The captured `Move` body is `[float X][float Y][varint Time][byte HistoryCount]
   [HistoryCount × 8 bytes]`; the relay reads `Time` and stops, and `docs/INJECTION.md` §"A layout
   discrepancy" records that the decompiled field list and the captured payload do not agree. A test
   built on a partly-understood packet produces a result whose interpretation is tangled with the
   packet's own correctness — the exact failure mode that document's §1.1 warns about.
3. **It moves the character without the client's knowledge, which is the mechanism the design
   rejected.** An injected `GmMove` desynchronises the client's position from the server's. Whatever
   the server does next — accept, snap back, kick — the operator's *client* is now somewhere else
   than the server thinks, and the next real `GmMove` (with the client's own position, `Time` and
   history) is what follows it. That is a state mismatch with an unknown resolution, tested on a live
   character, to learn something the design does not need to do.

**So Spike 3.1 is answered in-process, and §4 is what was built for it.** The proxy keeps one job
that is genuinely its own and is now scheduled as part of Spike 3.1's *second* half: **it is the only
witness of whether the server accepted the movement**, because the kick (`GmKicked`, id 185) and the
`GmMove` stream are wire facts and the client's own position is not evidence about them.

**A deliberate consequence:** the movement-boundary search in
[VERIFICATION.md](VERIFICATION.md) §5.2 — *"record the largest magnitude that is still accepted"* —
is **not** run by deliberately overspending. Overspending means either a `GmMove` injection (the
mechanism above) or a client-side speed that the client itself would never produce, and both cost a
live character to answer a question the *budget* already answers: `b = TilesPerSecond × frameSeconds`
is a legal move by construction, so the design never needs to know where the cliff is ([RISKS.md](RISKS.md)
§2a). What the phase does need is the **figure the client's own law produces**, which §6 builds and
the live run measures zero-risk. The boundary search stays available as an explicit, opt-in
experiment for an operator who wants the number, and §8 records it as deferred rather than dropped.

---

## 4. Spike 3.1 — the movement question, built

### 4.1 The mechanism, and why it is this one

**Input steering: write `GameScene.GameInput.MoveAngle` / `.Moving` / `.MoveSpeed`.**

`World.cs:12690-12692` reads exactly those three fields one line before it computes the frame's
position delta (`:12710`), applies the client's own speed law (`:12697`), its own collision integrator
(`:12714`) and its own history accounting (`:12769-12783`). Writing them means the client produces the
movement, the `GmMove` and the history — so the outbound stream is legitimate **by construction**.
The plan's table ranks this first and the ranking is right; what is new is that it is now *built*
rather than *proposed*.

### 4.2 The five targets, and the one that was added

`known-build.json` now pins **five** targets. The fifth is the movement integrator:

| key | declaring type | name | token | IL | role |
|---|---|---|---|---|---|
| `movementIntegrator` | `DarzasDominion.Darza.Game.World` | private-use string (see below) | `0x06002961` | 2574 | the input→position step (`World.cs:12666`) |

**Its metadata name is not the name the decompiler prints, and that is a trap worth recording.** The
decompiled source calls it `hehe_EB2C_E5A5_EE82_E9C5_EF68(float, float)`. The metadata string heap
holds a *different* string — nine private-use code points. **A whole-file search of the 89 MB IL dump
for the decompiler's spelling returns zero hits**, so the natural way to find this method reports
"not in this build" and is wrong. `tools/il_targets.py` exists for that reason (§4.3), the record
keeps the real string, and every build log prints it escaped.

Why a **managed replacement on that method** rather than a write from the frame tick, which is
already patched:

1. **the write and the observation are the same call.** The replacement writes the input, calls
   through, and the next frame reads the position the client computed from it — with no other writer
   in between and no frame boundary to blur it;
2. **nothing can re-write the input between the write and the read.** The frame tick runs before
   `World.Update`, which is where this method is called from; a write there would be consumed a frame
   later and overwritten by the real input poll in `PreUpdate` in between;
3. **it is the entry point the solver needs anyway.** The plumbing built for the spike is the
   plumbing the dodge steers through, so nothing here is throwaway.

### 4.3 `tools/il_targets.py` — selecting a target whose name cannot be searched

The four Phase 0 targets are found by `(declaringType, name, arity)` because their metadata names are
readable. The movement integrator's is not. So selection is made on evidence that does not depend on
the name:

- the declaring type (readable);
- the parameter count, from the `Param` table;
- **the return and parameter types**, decoded from the method's signature blob — this is what
  distinguishes the four arity-2 methods on `World` that take two `float`s;
- the IL size, which the decompiled method's line span predicts;
- the `Method begins at RVA 0x…` line in the IL dump, whose body's locals
  (`Character`, `bool`, `float32`, `Angle`, `Vector3`) and code size (2574 = `0xa0e`) match
  `World.cs:12666`'s body exactly.

The tool **never writes the record**; it lists candidates with their tokens, RVAs and IL hashes, and
`il_index.py` does the pinning. There is still one authority for the table.

**Two bugs in it were found by using it, and both are recorded because both were silent:**

1. it read the signature's **calling-convention byte as the parameter count**, producing arity-2
   methods with thirty-two parameters and every row wrong;
2. one branch of its type walker **forgot to advance the cursor**, so
   `GetDamageTaken(int, double, bool, out Color)` came back as four `int`s — correct count, wrong
   types.

Neither raised an exception; both produced a *plausible table*. They were caught because
`--params float,float` — a filter that should have matched the movement integrator — matched nothing.
`--self-test` now checks the reader against the four signatures the project documents independently,
and it is the same list `il_index.py` already used for its own arity model.

### 4.4 Two plumbing defects found while wiring the target

**A hardcoded key list silently truncated the record.** `apply_to_record` named
`("frameTick", "spawn", "collision", "escape")` literally. Adding the movement target made every line
of output correct — resolved, cross-checked, "TARGET TABLE OK — 5 targets" — and then wrote **four**
into `known-build.json`. The build script turns that record into the hook's compiled table, so the
hook would have shipped without the target while the tool that produced it said otherwise. The loop
now derives its keys from the table, and **`il_index.py --check` compares the record against the
table** and exits non-zero on a mismatch. That check did not exist, and its absence is why the
truncation was invisible.

**Printing an obfuscated name killed the tool.** The resolution succeeded and then died in `print`
with `UnicodeEncodeError: 'charmap' codec can't encode characters in position 58-62`: the console code
page has no glyphs for private-use code points. Names are escaped on the way out
(`ascii_name`), while the record keeps the real string — it is what the metadata is matched against.

### 4.5 What the code does, per frame

`SteeringPilot` (`hook\managed\Drelay.Bootstrap\SteeringPilot.cs`) is the actuator and the
measurement, and it contains **no dodge logic at all**:

```
movementIntegrator replacement (managed, entered by MinHook's jmp)
  ├─ the game's own guards: dead / safe world / no player / spirit mode → write nothing
  ├─ read Player.Position  → measure last frame's displacement against last frame's command
  ├─ advance the phase if its frame budget is spent
  ├─ write GameScene.GameInput.MoveAngle (radians), .Moving = true, .MoveSpeed = 1
  └─ call the original through the trampoline  (ALWAYS, on every path)
```

Two modes, chosen by `DRELAY_MOVE_STEER` (set by `Drelay.Launcher.exe --move-steer`):

| mode | what it does | why |
|---|---|---|
| `sweep[:frames]` | walks **eight evenly spaced input angles**, 45° apart, `frames` each (240 default, 120 for `sweepfast`) | the independent variable has to be the thing written — `MoveAngle` — and the observed heading is the dependent one, so the transform `MoveAngle + (-WorldRotation + aimOffset)` is *measured* rather than assumed. A sweep in world headings would presuppose the very transform it is there to measure, and would hide a wrong rotation sign behind a plausible table |
| `hold:degrees[:frames]` | holds one **world** heading, corrected by the live `World.WorldRotation` | this is the shape the solver's output will take |

**What is measured, and what is deliberately not.** "The player moved" is not the measurement. Two
questions are, and they are different:

- **did the client act on our input?** the angle between the commanded direction and the realised
  per-frame displacement. A healthy actuator gives a cosine near 1; a client that ignored us gives
  noise around 0;
- **did the server accept it?** *not answerable in-process* — the client's position is authoritative
  to itself. It is answered by the relay: `GmKicked` (id 185) with a movement reason, or a snap-back
  in the position trace. That is §5.4, and it is why the proxy still has a job.

Every frame logs the commanded angle, the value **read back** from the field, the world rotation, the
position and the displacement, so the write can be seen to have landed and the movement can be seen
to follow it.

### 4.6 The switches, and the reason there are two names

`--move-steer VALUE` is the only switch in the project that moves the player. The launcher passes it
as **`DRELAY_MOVE_STEER`** (the operator's value) and sets **`DRELAY_HOOK_MOVE_REPLACEMENT=1`** (the
launcher's own namespace, which the launcher deletes inherited values from). The split is deliberate:
the hook's decision *whether to patch at all* must not be reachable from a stale shell variable, and
the experiment's *parameters* must be. With `--move-steer` absent, the replacement is still published
and still calls through — so the shared slot, the native dispatch and the trampoline handshake are
exercised by **every** run, rather than first being exercised by the run whose mistake would move the
player.

### 4.7 Success criterion

**The player moves under hook control, in the commanded direction, the server does not kick, and the
client's own movement accounting stays sane.** Concretely, from a `sweep` run:

1. **every phase reports `moved on N frame(s)` with `N` comparable to the phase's length** — the
   input landed and the integrator acted on it. **The run has to last long enough for a phase to
   end**: `sweep:240` needs 240 frames per phase (about four seconds each, eight phases, so a little
   over half a minute), and `hold:90:600` needs 600 (ten seconds). The first two live attempts were
   closed about two seconds after the patch went live, which is why they show a patched target, an
   armed plan, and no phase summary: the plan had not reached its first boundary;
2. the per-phase `observed heading` advances with the commanded angle, one quarter-turn at a time;
3. the **read-back** in the frame lines equals the value written — the write reached the field the
   client reads, rather than a copy;
4. `hook.log` contains no `GmKicked` and the relay log contains no `Kicked` event for the window
   ([VERIFICATION.md](VERIFICATION.md) §5's list);
5. the frame cost stays inside the budget — this phase carries Phase 2's waived cost work, and a
   3-field write plus two reads is the floor of what the solver will cost.

**What is deliberately *not* gated offline, and the reason matters.** The pilot itself is exercised
only live: the synthetic host has none of the fields it resolves (`World.GameScene`, `GameInput`,
`Player.Health`), and teaching that host a fake input path would test a fixture rather than the game.
What *is* gated offline is everything around it — the target's identity by IL hash, the target table
against the assembly, the shared-block layout, the publish-and-dispatch plumbing on every run, the
report's own reading of the logs (four cases, including the unpatched and kicked ones), and the whole
existing 132-check injection suite, which now runs green with the fifth target in the table. **The
live run is the only thing that can answer the question, which is why §5 exists rather than an
offline substitute.**

---

## 5. The live protocol (operator)

Two runs, in this order. **The character will walk on its own. Do it somewhere it does not matter,
and be ready to stop.** Nothing here touches what Phase 2 already proved.

**Build first** (`hook\build.ps1`), then:

```
# 1. SWEEP -- calibrate. ~40 s of movement over eight headings.
Drelay.Launcher.exe --game "<install>" --live-seconds 180 --move-steer sweep:240

# 2. HOLD -- the shape the solver uses, one world heading, bounded.
Drelay.Launcher.exe --game "<install>" --live-seconds 120 --move-steer hold:90:600

# 3. ESCAPE -- the floor itself, called once under hook control (§6 item 3).
Drelay.Launcher.exe --game "<install>" --live-seconds 60 --escape-test 25
```

With the relay running for each, so the wire evidence exists:

```
java -cp drelay.jar networking.Launcher ...      # the operator's usual relay invocation
```

**What to send back:** the run directory (`%LOCALAPPDATA%\drelay\hook\<run>`), the relay session log,
and nothing else. The analysis is:

```
python autododgeresearch\tools\steering_report.py <run-dir> [relay-session.log]
```

**Or run the whole thing as one command**, which is what the runner is for:

```powershell
.\autododgeresearch\hook\tools\Run-Phase3Session.ps1 -Experiment Sweep -Seconds 240
.\autododgeresearch\hook\tools\Run-Phase3Session.ps1 -Experiment All   -Seconds 240 -RelayLog <relay.log>
.\autododgeresearch\hook\tools\Run-Phase3Session.ps1 -AnalyseOnly <run-dir> -RelayLog <relay.log>
```

`Run-Phase3Session.ps1` launches the experiment, **stays attached** (the launcher waits for the game
and streams the hook's log live), reads the report for each run, writes the checks and the raw report
to `hook\obj\cases\phase3-session\verdict.txt`, exits non-zero if any check failed, and repeats the
one thing a reader most needs not to misread: **a `crash.txt` and an event-log `0x80131506` at the
moment the game is closed are expected and are not this run's result** (§5.1).

**It worked badly at first, and both faults are worth keeping visible.**

1. **It could die silently, which is the worst thing a diagnostic can do.** The first version ran
   with `$ErrorActionPreference = 'Stop'` and invoked the launcher directly; the launcher writes its
   own errors and returns non-zero, and a non-zero native exit under `Stop` is a terminating error --
   so the script stopped on the launch line, printed nothing about why, and never analysed. That is
   what "prints stuff but exits very quickly" was. Failures are now collected and reported, the
   launcher's own lines are echoed, and its exit code is a check of its own.
2. **One inverted test grew checks out of nothing.** `$escapeAsked` read
   `$joined -notmatch '...not requested this run'`, so an empty report matched nothing and was taken
   as "the escape was requested", adding two escape checks that could only fail. A negative test
   against an empty set is not a negative result; it is no result. It is now gated on there having
   been a run.
3. **A run root the launcher cannot write to looks like nothing at all.** In a confined shell
   `Directory.CreateDirectory` throws inside the launcher, which prints one `FAILED:` line and exits
   1 before the game starts. There is a write probe at the start now, and it names the fix.

**And the reason the game seemed not to open at all, which was neither of the above.** This section
first recorded it as the `RuntimeHelpers.PrepareMethod` pass that `SteeringPilot.Warm` had grown.
**That attribution was wrong, and the correction is left here rather than swept up because the wrong
cause was "fixed" and the game went on dying** ([PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) §2.1 is the
full account). The pass was removed and the startup access violation was unchanged, because the crash
was `castclass` on a **value type**: `FieldAccessor` emitted `castclass field.DeclaringType` for every
accessor, which is correct for a reference type and has no meaning for a `struct` -- and Phase 3's
actuator is the first code in this project to touch one (`GameInput.MoveAngle` is
`DarzaCore.Geometry.Angle`, a `sealed` value type). The launch's own stderr carries the receipt:

```
Fatal error. System.AccessViolationException: Attempted to read or write protected memory.
   at System.Runtime.CompilerServices.CastHelpers.ChkCastClassSpecial(Void*, System.Object)
   at DynamicClass.DrelayField_MoveAngle.Radians(System.Object)
   at Drelay.Bootstrap.SteeringPilot.Warm()
```

It is a genuine fault and not a catchable `InvalidCastException`, which is why a warm-up defect
became a process death. `FieldAccessor` now emits `unbox` for a value-typed declaring type. **A
second defect sat behind it, and only a later run could expose it:** the warm-up read the pilot's box
back through the nested `GameInput`-shaped accessor, which faults on a boxed struct for the same
reason. Both are fixed and both are documented at the accessors themselves.

**And a third, which the first *successful* sweep found** ([PHASE3-HANDOFF.md](PHASE3-HANDOFF.md)
§2.3): `WriteInput` wrote the angle with `FloatSetter(Angle.Radians)`, whose receiver is an `Angle` --
so the write landed in the pilot's own box, every frame, and the input the client reads kept the
client's own value. `Moving` and `MoveSpeed` landed correctly, so the player did move: due south, at
the client's own speed, whatever the sweep commanded, because the read-back used the *nested*
accessor while the write did not and the log compared two different objects. **A round trip that
writes and reads through a different pair than the actuator uses is not a check**; `ProveInputWrite`
now runs the actuator's own pair on the first frame with a live input, and the sweep of
`20261014-040859-p22748` is the first run in which check A passes.

The report handles both experiments and tells them apart: a run that asked for only one of them is
not failed for the other, which matters because a good escape result reported as a failed steering
run would be worse than no report at all.

**About run 3.** `--escape-test 25` calls `World.RequestEscape()` once, 25 seconds after the world
first exists — the client's own nexus entry point, so the client's own gates run. **In a safe area it
is a legitimate no-op**, and the report says which of the four outcomes happened rather than guessing:
never armed, armed but the delay had not elapsed, called and returned, or called and threw. Whether it
*left the machine* is the relay's answer — `GmEscape` (id 66 / `0x42`) or the escape-cast-state pair
(290 / `0x122`), with `EscapeAck` (158 / `0x9E`) as the server's acceptance. **Expect one escape
request at login**, so a count of two means one of them is the hook's and one is the handshake; the
report flags that rather than calling it a defect.

Run 3 is **independent of runs 1–2** and can be taken on its own. It needs the character in a world
for 25 seconds; a town is fine (the no-op is a result), a run in progress is better.

**Optional, and now cheap:** the Phase 0 `GmHit`-drop trial (drop the outbound `GmHit` for exactly
one hit and see whether the server still decrements Health) needs no hook and can ride along with
either run. It does not gate anything; it closes an open Phase 0 item.

**Safety properties the code guarantees, so the operator does not have to:**

- the pilot writes **nothing** while the player is dead, in spirit mode, in a safe world, or with no
  player — it waits and counts;
- after the plan's last phase the input is **released** (`Moving = false`, `MoveSpeed = 0`).
  Without that, a dropped input would leave the client integrating a heading forever;
- the original movement tick is called on **every** path, including every failure path, so the worst
  case is a player who does not move rather than a player who cannot;
- `sweep` and `hold` are **bounded** by frame counts, and `hold` without a count runs until the
  session ends, which the switch's own help text says out loud;
- the escape test fires **once** and then latches, so it cannot become a nexus key held down — which
  in the ordinary path is an escape every cooldown and, with the `Impermanence` totem, a cast that
  re-arms for ever. It logs *before* it calls, so a fault inside the call still leaves the attempt on
  record rather than reading as "the test never armed".

---

## 5.1 Reading a run's `crash.txt` — which is not a crash report about the run

**The first three Phase 3 live runs each left a `crash.txt`, and the operator's own reading of them
was right and this document's first reading was wrong: the game was closed by hand, and that is when
the file appears.**

The reason is in the reporter's own header (`hook/native/DrelayCrash.cpp`), and it is the same
distinction `PHASE1-VERIFICATION.md` §5 and `PHASE1-HANDOFF.md` §"Two things in crash.txt" already
record for earlier rounds:

- a `crash.txt` is written by a **vectored exception handler**, so it only records exceptions that
  reach it. A **`COR_E_EXECUTIONENGINE` (`0x80131506`)** is not one: coreclr terminates the process
  through `TerminateProcess`, which raises nothing, so *the handler cannot see it* and the file that
  exists is **not** evidence about it;
- the authoritative record of a fatal CLR error is therefore the **Windows event log** — `.NET
  Runtime` id 1023, *"the process was terminated due to an internal error in the .NET Runtime at IP
  0x… with exit code 0x80131506"*, plus `Application Error` id 1000 naming `coreclr.dll`. That is
  what the three runs of 2026-10-14 have:
  `02:33:50`, `02:35:52` and `02:46:26`, all `coreclr.dll`, all `0x80131506`;
- **the third of those is the control that settles it.** That run had steering switched off —
  `DRELAY_MOVE_STEER` unset, the pilot logging *"resolved all 15 field(s) and is IDLE"* — and
  `movementIntegrator` **was never patched** (`1 of 5 target(s) patched`, `frameTick` only). It still
  wrote a complete evidence set: 295 successful frame reads, `phase 1 complete`, `summary.txt`,
  `selfcheck.txt`, `offsets.csv`, `frames.csv`. **So the close-time fatal error is not the pilot, not
  the movement replacement, and not this phase's work** — it is the hook's long-standing
  close/shutdown behaviour, which `PHASE1-VERIFICATION.md` §5 describes as a teardown-time dispatch
  on a module that is going away, with no `Drelay.Hook.dll` frame on the faulting stack.

**Two consequences for reading Phase 3's runs, and both matter more than the fault itself:**

1. **a `crash.txt` in a run directory says nothing about whether that run succeeded.** Read
   `hook.log`'s own ending (`phase 1 complete`, `N target(s) patched`), the pilot's phase summaries,
   and the relay log. This is the same rule the project already applies to `crash.txt` in the Phase 1
   and Phase 2 rounds;
2. **the steering run's first two attempts were not failures of the steering.** Their `hook.log`s
   show the movement target patched and the plan's first phase begun; the fatal error belongs to the
   close. What was *not* obtained is the measured evidence, because the operator closed the game
   before the phases completed — so §5's protocol stands, with the runs given enough time.

**The one open question this leaves**, and the cheap way to close it: whether the close-time fatal
error is the hook's doing at all. `PHASE1-VERIFICATION.md` §5 asks for exactly this control —
`DRELAY_NO_PATCH=1`, close the game, compare the event log — and it is still the right test. **If a
run with the hook present but nothing patched, and a run with the hook absent entirely, both produce
the same event-log entry on close, then the entry is a property of the game and its runtime, and this
document's only obligation is to stop calling it a Phase 3 result.** Until that is measured, it is
recorded here as *not attributable to this phase*, which is as far as the evidence goes.


---

## 5.2 The budget, measured from the client's own telemetry — **done, offline**

Phase 3's second exit criterion is that the budget is *measured rather than derived and assumed*.
It is now measured, from data that was already on disk, and the tool is
[`tools/move_budget.py`](../tools/move_budget.py) with a fixture
([`tools/tests/test_move_budget.py`](../tools/tests/test_move_budget.py), 13 checks).

**The idea, in one sentence.** `GmMove` (id 74 / `0x4A`) carries the position and the client's own
clock, the relay has logged thousands of them from real play, and **every one of them is a packet the
server accepted** — so the population of consecutive-packet displacements is the client's own
distribution of *legal* movement, and its p99 is a legal step by the server's own admission.

**Three captures, 7 611 packets, 7 531 walking steps.** The clock check is the thing that makes the
numbers trustworthy, and it is a check rather than a hope: the client's `Time` field and the relay's
wall clock advance at **ratio 1.0000 on a typical pair, over ~7 500 pairs**, which is two independent
clocks agreeing for ten minutes. A mis-decoded varint cannot hide in that, and the fixture proves the
tool refuses to report a speed when one is fed to it.

| measurement | value |
|---|---|
| tick between Move packets (median) | **200 ms** |
| walking step, typical while moving | **1.44 world units per tick** |
| walking step, p99 | **1.96 world units per tick** |
| walking step, p99.9 | 2.18–3.13 (session-dependent) |
| teleports excluded (≥ 20 units) | 24 of 7 555 steps |

**And it agrees with the resolved speed law, which is a check on both.** The law is
`3.5 + 5.0·(SPD+10)/75` tiles/s — 3.5 at SPD 0, 8.5 at the cap ([CLIENT-FINDINGS.md](CLIENT-FINDINGS.md)
§2.9). Over a 200 ms tick that is **0.70 to 1.70 units per tick if a world unit is a tile**, and the
measured mode is 1.44 with a p99 of 1.96. The population sits exactly where the law says it should:
inside the cap at p99, with the p95 *speeds* overshooting it explained by the `Speedy` tier multiplier
the law applies and by pairs whose tick ran short. **Two independent routes to the same number.**

**What that means for the dodge, concretely.** `DODGE-ALGORITHM.md` §5.2's budget
`b = playerSpeed · frameSeconds` is a call to the client's own law (§6's rule), which returns client
units; the measured legal step is in the same units; so the candidate radii `{0.35, 0.7, 1.0}·b` are
bounded by a number the server has already accepted thousands of times. **The dodge does not need to
know where the server's cliff is** — only where the legal ground is, and this is that.

**Two honest limits, both recorded rather than smoothed over.**

1. **This is a lower bound on the server's tolerance, not the cliff.** Finding the cliff needs
   deliberate overspend, which §3 defers and gives the reasons for.
2. **The tile scale is still not proven.** The agreement above holds *if* a world unit is a tile; it
   is consistent with that and nothing more. It does not matter to the solver, because the budget, the
   positions, the radii and the speed law are all in client units and the rule is to call the law
   rather than trust a resolved constant.

**One measurement this incidentally produced, and it is worth keeping:** the client's `Move` packets
are **200 ms apart, not one per frame**, which contradicts `CLIENT-MAP.md` §6.4's "at most once per
frame" in the direction of *fewer*. The client's emission is tied to the server's update cadence, so
this is a second, independent reading of the server tick — and it is 200 ms, which is the same number
`DODGE-ALGORITHM.md` §4.3 refuses to import from the reference engine's 5 Hz assumption. **The tick is
5 Hz here too, and that is now measured rather than inherited.** It does not change the design (the
budget is per frame, and the frame is what `--move-steer`'s displacement log measures), but it is
recorded because it corrects a document.

---

## 6. What comes next, in order

1. ✅ **Spike 3.1's live run** (§5 runs 1–2). This is M7 and it gated everything below.
   **Answered YES on 2026-10-14** (`20261014-040859-p22748`): the hook steered the live player
   through the client's own input path in eight commanded directions, the write landed on every
   logged frame, and the movement followed to within 2°. Two follow-ups are outstanding and neither
   gates the solver: the wire's `GmKicked` witness (one sweep with `-RelayLog`), and the rotation
   term, which this run could not measure because `WorldRotation` was 0.0° throughout. The terrain
   wedging the run exposed (three of eight phases pinned against scenery) is the live motivation for
   admission rules 2 and 3 of [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §5.3.
   **[PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) is the measurement and the next step.**
2. ✅ **The budget, measured from the client's own accepted telemetry** (§5.2): `tools/move_budget.py`,
   7 611 real `GmMove` packets, 3.5–8.5 tiles/s resolved law against 1.44 units per 200 ms measured
   mode, and a clock check that validates the decode at ratio 1.0000 over ~7 500 pairs. **What is
   left is the per-frame confirmation** — the live sweep's displacement log — which is the same
   quantity read from the other end.
3. ✅ **The escape floor's mechanism, built and waiting for its run** (§5 run 3, §9).
4. **The cost work Phase 2 waived**, with the solver that shares the frame: cull before sampling
   ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §8), the sampler's `p99` back under its slice, and the
   histogram that places an outlier instead of counting it.
5. **Spike 3.2 — the solver offline.** The decision is a pure function of a plain-data snapshot
   *by design*, so the invariants T5, T6 and T10 are asserted in a harness against recorded traces
   and every weight is tuned there, never in a live run.
6. **Deliverable 3.3/3.4 — the live dodge and the escape floor**, behind their own switch, dry run
   by default, with the paired trial ([VERIFICATION.md](VERIFICATION.md) §5) accounting for every
   death in the two buckets §2 makes unavoidable.

### 9. The escape floor's mechanism, and why it is a run of its own

**The problem the last live run exposed.** `escape` was reported `MISSING` on 2026-10-13 — *"never
compiled in the discovery window"* — and the reason is now known and is not a defect in the pinned
identity: **nothing called it.** The operator never pressed the nexus key, and no zone change called
it either, so the JIT never compiled `World.RequestEscape` and the detour never saw it. The
"same-sized neighbour" warning the run printed is other 335-byte methods, which is the two-case
middle ground `docs/HOOK-BUILD-MATRIX.md` describes.

That has a consequence the plan did not draw out. **The floor of Phase 3 is the one part of the
deliverable that can be reached without the decision that eventually calls it**, because
`RequestEscape` is a `public void` instance method: it needs a `MethodInfo` and a `World`, not a
compiled body and not a patch. So it can be tested *now*, before any solver exists to need it, and
that is what `--escape-test SEC` does:

- the call is made from the **frame replacement**, which is a managed method on the game's thread
  ([MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md) §0.2), and it goes through reflection so a
  failure lands at the call rather than at resolve;
- it fires **once**, `SEC` seconds after the world first exists — wall clock from "the world exists"
  rather than from run start, so the delay does not depend on how long the operator took to get into
  a zone;
- it **does not require a non-safe area.** `RequestEscape` is a legitimate no-op in a safe area
  ([VERIFICATION.md](VERIFICATION.md) §5.1 makes that a case the two-controller test has to exercise),
  so firing there is information rather than a mistake, and the report names which outcome happened;
- four outcomes are distinguished rather than collapsed: **never armed**, **armed but the delay had
  not elapsed**, **called and returned**, **called and threw**. Only the first is a failed test.

**What it establishes, and what it does not.** It establishes that the hook can *reach* the client's
nexus entry point and that the client's own gates run — and, with the relay, whether a
`GmEscape` (or the cast-state pair) actually left the machine and whether the server acknowledged it.
It does **not** establish that escaping was the right decision, and it is not Deliverable 3.4: gate 6
of [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §7 — project the undodgeable damage, call
`Entity.GetDamageTaken`, escape only when no admissible move avoids it — is still to be built. What
it removes is the possibility of discovering, at the end of the dodge work, that the floor cannot be
called at all.

---

## 7. Exit criteria, restated against what now exists

From [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) Phase 3, with the instruments this document
has built:

| criterion | state | instrument |
|---|---|---|
| **after Spike 3.1, the movement question has an answer with evidence** | **ANSWERED YES, 2026-10-14** (`20261014-040859-p22748`): the write landed on every logged frame (worst `0.0000000 rad`), the player moved on 1428 of 1951 frames, the heading followed the command to within 2° at eight quarter-turns, mean cos 0.995. The server's acceptance is the one witness still outstanding — run it with `-RelayLog`. [PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) §1, §3.1 | `--move-steer sweep`, then `tools\steering_report.py` |
| the budget is measured against the client's own law rather than derived and assumed | **measured both ways now**: 7 611 real `GmMove` packets give a 200 ms tick and a 1.44-unit typical step (§5.2), and the live sweep's own per-frame displacement is 0.133–0.146 units at 16.680 ms — 8.0–8.7 units/s against the resolved law's 8.5 tiles/s at the cap ([PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) §1.4). The tile scale is still not *proven*; the agreement is consistent with it and nothing more | `tools/move_budget.py` (with `tools/tests/test_move_budget.py`) |
| the dodge is *observably* dodging (decision log shows moves that avoid predicted collisions) | not started | the solver, Spike 3.2 |
| the frame budget is respected at p99 | **carried from Phase 2's waiver**; the spike's cost is the floor | `frames.csv`, the cost histogram |
| the death rate in the claim-bearing cases is measurably lower than a deliberate baseline run | not started; needs a paired trial | `TIMING.md` §8 item 4's requirement |
| the escape fires only when no admissible move avoids lethal projected damage, ack-confirmed | **the escalation target is not compiled at all today** — `escape` was `MISSING` on 2026-10-13 because nothing called it. **The actuator is built and tested in isolation** (§9) and awaits §5 run 3; the *decision* (gate 6, Deliverable 3.4) is not started | `--escape-test`, then the relay's `GmEscape`/`EscapeAck` join (`tools/steering_report.py` checks F and F'') |
| the two-controller precedence holds live: no cast cancelled, no movement `GmKicked` | not started | [VERIFICATION.md](VERIFICATION.md) §5.1, with and without the `Impermanence` totem |

---

## 8. What would stop Phase 3

Written down before the work, so that stopping is a decision and not a drift.

- **Spike 3.1 fails**: the player does not move under hook control, or the server kicks. Then the
  dodge does not exist, and the plan's own words apply — *"the escape floor of Deliverable 3.4 is the
  only behaviour left"*, escape-only was judged of questionable value on its own by the 2026-10-13
  merge, so **the default is to stop and document** rather than to escalate to position writes or
  packet injection.
- **The actuation works but the timing does not.** §2's 26 %/74 % split is the constraint: if the
  claim-bearing cases are dominated by point-blank arrivals, a 600 ms-horizon dodge can only help
  where the warning exists, and the honest deliverable may be a much narrower feature than the plan
  imagines. **This is not a stop — it is a scope reduction that has to be measured and stated**, and
  the paired trial is what measures it.
- **The cost cannot be brought inside the budget** once the solver shares the frame. Phase 2 waived
  this once; waiving it twice would mean shipping a dodge that costs frames, and a frame cost is a
  change to the game the operator plays.

Deliberately **not** stopping conditions, and the reason: a curved-trajectory rate of 30 % (it is
0.695 straight, which the freeze-and-anchor discipline is built for), the 74 % of damage with no
client claim (accepted scope — [RISKS.md](RISKS.md) §2b), and the escape path's own gates (they are
the client's, and calling the client's function is the point).
