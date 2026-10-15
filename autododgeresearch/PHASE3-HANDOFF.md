# Phase 3 handoff — the movement question is answered, and the dodge is the next step

**Date written:** 2026-10-14. **Branch:** `autododge-research`.
**Read with:** [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §"Phase 3 — Dodge, with the escape as
its floor" (the criteria this document reports against), [PHASE3-SCOPE.md](PHASE3-SCOPE.md) §4–§5
(the spike as built and the live protocol), [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §2 and §5–§10
(the solver this document hands over to), [RISKS.md](RISKS.md) §2a (the movement question) and §2b
(the 26 %/74 % split), [VERIFICATION.md](VERIFICATION.md) §5 (how each claim is proved), and
[MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md) §0.2 (why managed code on the game's thread is
legal at all).

---

## 0. The answer, in one paragraph

**Spike 3.1 is answered YES.** The hook caused the live client to move the local player, through the
client's own input path — `GameScene.GameInput.MoveAngle` / `.Moving` / `.MoveSpeed` — for a
sustained run, in eight commanded directions, and the client's own integrator produced the position,
the speed and the collision exactly as designed. The measurement is not "the player moved": it is
that the value **read back** from the client's own field equals the value **written** to it on every
logged frame (`worst |written − read back| = 0.0000000 rad`), and that the heading the player
actually travelled followed the commanded angle to within 2° at every quarter turn (`mean cos = 0.995`
over the run). What is **not** yet established is the one thing that cannot be witnessed in-process:
that the server accepted the movement. No disconnect occurred and the operator's prior testing is
that illegal movement is disconnected quickly, but that is testimony rather than a wire log — see §3.1,
which is the only outstanding witness on the phase's first exit criterion.

The dodge itself **now has solver code**, and its offline half is complete:
[SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md) is that work's handoff — the solver, the
trace recorder, the offline harness, the seven defects it took to make the solver move, and the one
recorded session that is still outstanding. Everything §5 below asks for exists except the live
validation of the trajectory model, which needs a session somebody actually played.

---

## 1. The run, and what it measured

### 1.1 The commands

```powershell
# the operator's own shell (the harness's write confinement stops the game reaching the title
# screen, so a harness-launched run needs -RunRoot inside the repository -- see README.md)
cd G:\coding\javamapp\autododgeresearch
.\hook\tools\Run-Phase3Session.ps1 -Experiment Sweep -Seconds 300
```

which is, from [`hook/tools/Run-Phase3Session.ps1`](hook/tools/Run-Phase3Session.ps1):

```
Drelay.Launcher.exe --game "<install>" --live-seconds 300 \
    --runroot "%LOCALAPPDATA%\drelay\hook" --move-steer sweep:240
```

**Run directory:** `%LOCALAPPDATA%\drelay\hook\20261014-040859-p22748`.
**Read with:** `python autododgeresearch\tools\steering_report.py <run-dir>`.

### 1.2 The numbers

The run lasted **1951 frames**, eight phases of 240, at a measured **16.680 ms** frame delta
(60.0 Hz) — about 32.5 s of live play. The report's own table, verbatim:

| phase | commanded | observed | mean \|Δ\| | mean cos | frames moved |
|---|---|---|---|---|---|
| 1 | 0° | 0.0° | 0.1369 | 1.000 | 240 |
| 2 | 45° | 45.0° | 0.1370 | 1.000 | 240 |
| 3 | 90° | 90.0° | 0.1332 | 1.000 | **25** |
| 4 | 135° | 136.5° | 0.1353 | 0.989 | 240 |
| 5 | 180° | 180.0° | 0.1355 | 1.000 | **188** |
| 6 | 225° | −135.0° | 0.1418 | 0.998 | 240 |
| 7 | 270° | −90.0° | 0.1436 | 1.000 | **15** |
| 8 | 315° | −45.5° | 0.1457 | 0.974 | 240 |

(every phase reports the same heading whether it is written as `225°` or `−135°`; both spellings are
the same angle, and the mean cos is what compares them)

| measurement | value |
|---|---|
| worst \|written − read back\| over 128 logged frames | **0.0000000 rad** |
| frames the player moved | **1428 of 1951 (73 %)** |
| per-phase observed − commanded residuals | 45, 45, 46, 44, 45, 45, 44, 45 deg — **worst 2°** |
| mean cos(observed − commanded) | **0.995** |
| `World.WorldRotation` on every logged frame | **0.0°** |
| worst frame-tick call so far, at call 120 | 346 µs |

### 1.3 The checks, and what each one is worth

From `steering_report.py`, against the criteria in [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md)
§"Exit criteria" and [PHASE3-SCOPE.md](PHASE3-SCOPE.md) §4.7:

| check | state | the evidence |
|---|---|---|
| the movement target was PATCHED | **PASS** | `PATCHED 'movementIntegrator' at 00007FFF9E80D620`; 2 of 5 targets patched (`frameTick` + `movementIntegrator`) |
| the mode was understood | **PASS** | `the steering pilot is ARMED: mode 'sweep:240', 8 phase(s) of 240 frame(s)` |
| **A** the input landed | **PASS** | `worst \|written − read back\| = 0.0000000 rad` |
| **B** the integrator acted | **PASS** | moved on 1428 of 1951 frames, and the displacement appears in `Player.Position` between consecutive logged frames |
| **C/C′** the direction was obeyed | **PASS** | observed − commanded is 45° at every quarter turn; worst residual 2°; mean cos 0.995 |
| **D** the server did not object | **FAIL (no relay log given)** | not witnessable in-process — see §3.1 |
| **F** the escape floor | PASS (not requested this run) | `--escape-test` was not passed |

There is also a check the report cannot make and the log can, and it is the one that makes check A
trustworthy rather than circular:

```
steering input write proved on the first live input: the nested setter wrote 0.25 rad into
MoveAngle and the nested getter read back 0.25
```

The actuator's own instruction pair — `ldflda GameInput.MoveAngle; stfld Angle.Radians`, and the
matching read — was exercised once on a real input object before the first steering write. That is
new in this build and its absence is what let the previous run's defect reach the operator
(§2.3).

### 1.4 The per-frame half of the budget, which closes [PHASE3-SCOPE.md](PHASE3-SCOPE.md) §5.2

§5.2 measured the client's *legal* walking step offline from 7 611 accepted `GmMove` packets:
**1.44 units per 200 ms tick typical, 1.96 at p99**, against a resolved speed law of
`3.5 + 5.0·(SPD+10)/75` tiles/s (3.5 at SPD 0, 8.5 at the cap). It recorded one thing as still
missing: *"the per-frame confirmation — the live sweep's displacement log"*.

That confirmation is now on disk. The per-phase mean displacement runs **0.1332 to 0.1457 world
units per frame** at 16.680 ms, i.e.

```
0.1332 / 0.016680 = 7.99 units/s      (slowest phase, blocked early by terrain)
0.1457 / 0.016680 = 8.74 units/s      (fastest phase)
0.1369 / 0.016680 = 8.21 units/s      (phase 1, a full clean phase)
```

against a law that gives **8.5 tiles/s at the SPD cap**. The two ends of the budget now agree from
both directions: the client's own accepted telemetry (200 ms ticks) and the client's own integrator
(per-frame deltas), with the resolved law in the middle. The `Speedy` tier multiplier the law applies
is the plausible reason the fastest phase sits slightly above the cap, which is the same explanation
§5.2 gives for its own p95 overshoot — and it is the same caveat: **this is a lower bound on the
server's tolerance, not the cliff** (§3.1).

**What this means concretely for the solver:** `b = playerSpeed · frameSeconds` is a legal move by
construction, the measured legal step is in the same units, and the candidate radii `{0.35, 0.7,
1.0}·b` are bounded by a number the server has accepted thousands of times. Nothing in the dodge
needs to know where the cliff is.

---

## 2. The four defects between "the actuator is built" and "the actuator works"

They are recorded here rather than in a changelog because **three of the four were misdiagnosed the
first time**, and each misdiagnosis cost a live run. A reader who inherits this work should know
which sentences in the surrounding documents were wrong and why.

### 2.1 `castclass <valuetype>` is invalid IL, and it stopped the game starting

**Symptom:** the game died about a second after `CreateProcess`, before any window appeared. Every
run. `crash.txt` said `0xC0000005`, `read of 00007FFF3E800010`, faulting inside
`System.Private.CoreLib.dll+0x420D03`, with `Drelay.Hook.dll` frames on the stack.

**What it was blamed on first:** a `RuntimeHelpers.PrepareMethod` pass in `SteeringPilot.Warm`. The
pass was removed; **the crash was unchanged**, because it was never the pass.

**What it was:** `FieldAccessor` emitted `castclass field.DeclaringType` for every accessor. That is
the correct instruction for a reference type and has **no meaning for a value type** — the C#
compiler emits `unbox.any` there, and RyuJIT's handling of the illegal form is not a rejected cast
but undefined behaviour. It was found with the launch's own stderr, which carries the managed stack:

```
Fatal error. System.AccessViolationException: Attempted to read or write protected memory.
   at System.Runtime.CompilerServices.CastHelpers.ChkCastClassSpecial(Void*, System.Object)
   at DynamicClass.DrelayField_MoveAngle.Radians(System.Object)
   at Drelay.Bootstrap.SteeringPilot.Warm()
```

and it is a genuine fault rather than a catchable `InvalidCastException`. Isolated in a standalone
reproduction of the exact IL, one process per case:

| IL | result |
|---|---|
| `castclass Angle; ldarg.1; stfld Radians` | **Fatal error. Internal CLR error. (0x80131506)** |
| `castclass Angle; ldfld Radians` | a garbage `float`, no exception |
| `unbox Angle; ldarg.1; stfld Radians` | 0.25, correct |
| `unbox Angle; ldfld Radians` | 0.25, correct |

Phase 3's actuator is the first code in this project to touch a **value-typed** field — `MoveAngle`
is `DarzaCore.Geometry.Angle`, a `sealed` value type — so it was the first to hit it.
`FieldAccessor.EmitReceiver` now emits `unbox` when the declaring type is a value type and keeps
`castclass` otherwise, so nothing that worked before changes behaviour. **The corrected record is
also written into the code** ([`FieldAccessor.cs`](hook/managed/Drelay.Bootstrap/FieldAccessor.cs),
`EmitReceiver`, and `SteeringPilot._getBoxedAngleRadians`), because
[PHASE3-SCOPE.md](PHASE3-SCOPE.md) §5.1 still attributes this crash to the `PrepareMethod` pass and
that attribution is wrong.

### 2.2 The warm-up read the box back through the wrong accessor

Independently of 2.1, `Warm` round-tripped through `_getMoveAngleRadians`, which is
`NestedFloat(GameInput.MoveAngle, Angle.Radians)` and therefore casts its receiver to `GameInput` —
and the receiver handed to it was the pilot's own boxed `Angle`. `castclass` on a boxed struct faults
in the same helper as 2.1. There is now a box-shaped getter, `_getBoxedAngleRadians`, and the two are
documented as **not interchangeable in either direction**. The actuator's write uses the nested
setter; only the warm-up uses the box pair.

### 2.3 The write went into the pilot's box, not into the input: the first sweep walked due south

**Symptom:** the first successful live sweep. The player moved — 73 % of frames — but always in the
same direction, and the report failed checks A, B and C. The frame log says exactly what happened:

```
steer frame 31 phase 1 cmd 0.0deg wrote 0.0000rad read-back 4.7124rad ...
steering phase 1 (input 0deg): target 0deg, moved on 240 frame(s), observed heading -90.0deg
```

`4.7124 rad` is 270°: the player walked due south at 0.133 units a frame for both phases.

**What it was:** `WriteInput` called `FieldAccessor.FloatSetter(Angle.Radians)`, whose IL casts its
receiver to `Angle` — so the angle was written into the pilot's private box, every frame, while the
input the client reads kept the client's own value. `Moving` and `MoveSpeed` landed correctly because
their receiver *is* the input, which is why the player moved at all and why the failure looked like a
client that ignored the hook.

**The fix:** `FieldAccessor.NestedFloatSetter`, emitting `ldflda GameInput.MoveAngle; stfld
Radians` on the input object. **The lesson worth carrying:** the read-back in this run used the
*nested* accessor while the write did not, so the log compared two different objects and reported the
discrepancy as if it were the client's. A round trip that writes and reads through a *different* pair
than the actuator uses is not a check. That is why `ProveInputWrite` now exists and runs on the first
frame with a live input.

### 2.4 Not a defect: a phase lasts four seconds, and that is what it was asked for

The operator's note that "it walks in a direction for 4 seconds, not 4 frames" is the mode working
as specified: `sweep[:frames]` takes **frames per phase**, and `sweep:240` is 240 frames — 4.0 s at
this run's 16.680 ms. `sweepfast` is the 120-frame variant, and `--move-steer sweep:60` is legal if a
future run wants shorter exposure. Nothing in the design assumes 4 s; it is the calibration run's
choice, and the solver's own cadence is re-decided every frame ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md)
§5.6 hysteresis).

---

## 3. What this run does **not** establish

### 3.1 Server acceptance — the one outstanding witness

**Check D cannot be answered in-process**, and this is by design, not by omission: the client's
position is authoritative to itself, so "the player is at the position we asked for" is not evidence
about the server. The witnesses are wire facts:

- `GmKicked` (id 185) with a movement reason, or
- a snap-back in the position trace of the relay's `GmMove` stream.

**What the run has instead:** no disconnect occurred, and the operator's report is that from prior
testing the server disconnects quickly when movement is illegal. That is a strong indication and it
is *not* a wire log. `Run-Phase3Session.ps1 -RelayLog <relay-session.log>` is the run that closes it,
and it is cheap: the same sweep, with the relay attached, converts check D from FAIL to PASS or
produces the kick.

**It does not gate the dodge.** The design's position is that `b = playerSpeed · frameSeconds` is
legal by construction ([RISKS.md](RISKS.md) §2a), and §1.4 now confirms the measured step agrees with
the client's own law. It gates the *claim*, and the claim belongs in the phase close.

### 3.2 The rotation term is no longer an assumption — but the transform is still unmeasured

> **UPDATED 2026-10-14 by [SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md) §4.5. The
> premise below is dead: `World.WorldRotation` is not always 0.0°, and the pilot now compensates for
> it in every mode. What remains open is the *size of the residual*, not whether the correction is
> being exercised. Read the first paragraph as history.**

`World.WorldRotation` was **0.0° on every logged frame** of this run, so `MoveAngle` and the world
heading were the same number and the sweep could not measure the transform it was built to measure.
The relation the solver will depend on comes from the client's own source — `World.cs:12692`
computes `angle = MoveAngle + (−WorldRotation + aimOffset)` — so a world heading `W` needs
`MoveAngle = W + WorldRotation − aimOffset`, which is what `hold` mode already writes. **With the
camera unrotated, that correction has never been exercised.** A `hold` run (or a sweep with the
camera turned) settles it; until then the dodge's world-to-input conversion is a resolved-formula
assumption with `aimOffset = 0`, and `Player.HasEffect(160)`'s offset is untested.

**What the session of `20261014-192048-p15744` changed, in three steps:**

1. **The premise is false.** The operator rotated the camera through the run, and
   `World.WorldRotation` was non-zero on **1871 of 2172 frames**, with **353 distinct values**
   reaching 359.5°. It is a live, continuously varying field, not a constant zero.
2. **The sweep could not measure it, and its own numbers say so.** `sweep` wrote its target straight
   into `MoveAngle` with no correction — deliberately, to read the transform off a table of written
   input against observed heading. That table requires a still camera: with one rotating, the phase
   cosines came out 0.829, **−0.493, −0.619**, 0.672, 0.694, which measures the mouse as much as the
   transform.
3. **The correction is now applied in every mode**, so the commanded **world heading** is the phase
   target whether or not the camera moves: the observed heading must match it, and the residual is
   the transform's error. The phase summary prints `mean WorldRotation` beside the cosine, so the
   correction's input is a printed number rather than an assumption about the operator's hands.

**What is still open, precisely:** the residual of `observed − commanded world heading` under a
rotating camera. A `hold` run with the camera turned is now the cleanest measurement of it, because
the commanded world heading is constant and every deviation is the transform's. `aimOffset` for
`Player.HasEffect(160)` remains untested, and the `mean |delta|` per-phase figure stays the witness
for whether the client acted at all.

### 3.3 Terrain: the player wedged in three of eight phases

Phases 3, 5 and 7 moved for only **25, 188 and 15 frames**; in the rest the player was pinned. The
log shows the mechanism plainly — the command landed, the read-back matched, and the client's own
collision refused the step:

```
steer frame 541 phase 3 cmd 90.0deg wrote 1.5708rad read-back 1.5708rad pos (1285.844,1471.890) d (0.0000,0.0000)
steer frame 751 phase 4 cmd 135.0deg wrote 2.3562rad read-back 2.3562rad pos (1285.844,1471.890) d (0.0000,0.0000)
```

`d (0.0000,0.0000)` for 210 frames, then movement resumes when the heading changes. The operator
describes it as getting stuck on a tree and otherwise making a circle.

**This is the actuator behaving correctly and the *absence of a solver* showing.** The pilot writes
one heading and keeps writing it; nothing in it knows the step was refused. It is also the cleanest
possible motivation for admission rules 2 and 3 of [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §5.3 —
**walkable destination** and **path clear**, both read from the client's own collision grid
`private byte[,] World.<grid>` (`World.cs:4318`) — because a dodge that picks a blocked candidate is
a dodge that stands still while a bullet arrives.

### 3.4 The sensor and the actuator have never shared a frame

`Run-Phase3Session.ps1` does not set `DRELAY_FRAME_SENSOR=1` (that is `Run-Phase2Session.ps1`'s
switch), so this run exercised the native frame read and the movement integrator, **not** the
trajectory sampler. Phase 2 measured the sampler at `p50 ≤ 50 µs, p99 ≤ 100 µs` standalone; Phase 3's
cost criterion — the one Phase 2 waived and Phase 3 inherits — is the **sum** of the sampler, the
admission test and the write inside one 300 µs slice ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §8).
**That sum has never been measured.** The `worst call so far 346 µs` at call 120 is a single
early-session frame tick, not a p99, and the two must not be confused in either direction.

### 3.5 `escape` still has never been compiled

Reported `MISSING` again, for the reason [PHASE3-SCOPE.md](PHASE3-SCOPE.md) §9 records: nothing called
`World.RequestEscape()`, so the JIT never compiled it. `--escape-test 25` is the run that reaches it,
it is independent of the steering runs, and it needs a world for 25 s. **The floor is still
un-proven**, and it is one command.

### 3.6 What a `crash.txt` in the run directory means

Nothing about the run. This run left one, and the close-time fault is the hook's long-standing
shutdown behaviour, reproduced with steering off and the movement target unpatched. Read the phase
summaries and the report, not the file ([PHASE3-SCOPE.md](PHASE3-SCOPE.md) §5.1).

---

## 4. What exists now, mapped to the algorithm's pipeline

[DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §2 is the per-frame pipeline. Against the code as it stands:

| pipeline stage | state | where |
|---|---|---|
| 0. health/budget refresh | **exists** | the native frame read ([`hook/native/DrelayFrame.cpp`](hook/native/DrelayFrame.cpp)) — `frames.csv`, per-frame cost |
| 1. threat set | **exists, Phase 2** | `ThreatSet.cs` + `SensorRead.cs`; the two-strike retirement and the min-age grace are implemented and self-tested (16 assertions) |
| 2. trajectory sampling | **exists, Phase 2** | `TrajectorySampler.cs` — `ProjDesc.GetPosition` through a virtual dynamic method, 21 samples / 30 ms / 600 ms horizon; 585 000 sample sets measured live in Phase 2 |
| 3. egress test | **missing** | [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §5.1's disc test is specified; no code |
| 4. decision | **missing** | the solver: candidates, admission, temporal test, scoring, escape gate |
| 5. act | **exists and is now certified** | `SteeringPilot` writes `GameInput` from inside the movement integrator's managed replacement; this document is its evidence |

**One structural decision the actuator makes for the solver, and it is load-bearing.** The decision
point and the write point are in **different hooks**:

- the **frame tick** (`DarzaWindow.Update`) runs after the client has drained this frame's packets and
  before `CurrentScene.UpdateScene` advances the world, so it is where the world is freshest and
  where the solver's snapshot belongs ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §2's argument);
- the **movement integrator** entry is where the write belongs, because it is the last writer before
  the client reads the input, nothing can re-write the input in between, and the write and the
  read-back are the same call ([`SteeringPilot.cs`](hook/managed/Drelay.Bootstrap/SteeringPilot.cs)).

So the solver publishes **a heading and a magnitude**, and the pilot consumes them at the integrator
entry. That is one shared slot in the block, not a second decision path — and it keeps Phase 2's
sensor (frame tick) and Phase 3's actuator (movement integrator) on the threads and in the places
they have already been proved on.

---

## 5. The next step — Spike 3.2, the offline solver

> **DONE, AND HANDED OVER.** [SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md) reports the
> build this section specifies, in this order, with the seven defects the harness found on the way.
> What remains from the plan below is the one thing an offline build cannot do: **T7 needs a
> recorded session**, and §6 of that document is the command.

[IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §"Spike 3.2 — the solver, offline" sets the shape:
the decision is a pure function of a plain-data snapshot **by design**, so it is built and tuned
without the game. This is where the dodge's quality is actually determined and it is the cheapest
place to iterate.

### 5.1 Build order

1. **The snapshot type, and nothing else.** Plain data, no game pointers, no allocation: player
   position/radius/health, the frame's `frameSeconds`, the live threat records, and the sampled
   polylines. [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §2's "pure function of a plain-data snapshot"
   exists precisely so that step 4 can run in a console harness.
2. **The trace reader.** Phase 2's live logs are the source. Invariant 4 of
   [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §10 — predicted vs actual collision — is the first thing
   to assert and it needs **no live play**: log every predicted first-impact time and every
   `HitCharacter` call **that returned `true`** (the method is the client's per-frame predicate, so
   its `false` entries are questions, not collisions), and assert the model predicted the ones that
   happened. It is the only test that can tell us whether the trajectory model is right.
3. **The solver as specified**, in this order, because each stage is what the next one filters:
   - §5.1 the instantaneous floor (a circle test — `DistanceSquared < r²`, `Projectile.cs:1573`;
     `r_eff` already contains the player's half, so it must **not** be added again);
   - §5.2 candidates: stand + 24 headings × `{0.35, 0.7, 1.0}·b` + intent + egress = **77**;
   - §5.3 admission, hard, in order — in bounds, **walkable**, **path clear**, not inside a disc at
     arrival, not inside one along the way, **temporally clear**, not inside an enemy body (with the
     already-inside exception). Rules 2 and 3 need the collision grid resolved (§3.3 is the live
     motivation for that work);
   - §5.4 `stand` is a real candidate and the preferred answer when it is durably safe — "the best
     dodge is usually no dodge";
   - §5.5 the temporal test: player swept path vs bullet predicted path, **segment-vs-segment with
     compatible times**, with the bullet interpolated between knots and the reference engine's
     **trust prefix** so a blind tail never scores as "clear for 600 ms";
   - §5.6 scoring **over the already-admissible set only** — safety is a hard constraint, preference
     is a score. That ordering is what makes every later feature safe by construction.
4. **The escape gate as gate 6 of the same decision**, not as a second controller: project the damage
   that cannot be dodged across the admissible set ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §7.2),
   compute it by **calling `Entity.GetDamageTaken`** on the live player and never by recomputing it
   (§6), and escape only when it is lethal and no admissible move avoids it.
5. **Intent preservation** (§5.7) from the first version that moves anything. AoE and bomb damage are
   out of scope by operator decision and are the player's to dodge manually
   ([IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §"Phase 3 prerequisite"), which **promotes** the
   rule: a dodge that overrides the player's steering takes away the tool they need for the channel
   it does not cover.

### 5.2 What to tune offline, and what not to trust

Every constant in [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §9 is a **starting value**, and its
right-hand column is the antidote to copying a tuned number from another engine. The weights of §5.6
are tuned in the harness, never in a live run. Three numbers are already resolved and should be used
rather than re-derived: the speed law (`3.5 + 5.0·(SPD+10)/75` tiles/s, now also confirmed per-frame
by §1.4), the tick (`200 ms`, measured two ways), and the budget `b = playerSpeed · frameSeconds`.

### 5.3 The invariants to assert, from [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §10

`stand` is never rejected while durably safe; no admissible candidate is inside a bullet's disc at
arrival; the chosen heading does not change while it stays admissible and nothing beats it by the
hysteresis margin; **predicted and actual collision agree** (build this first); a re-rented
`Projectile` never inherits state (key on `(GameId, startTime)`); the frame budget holds at p99; and
**with the dodge disabled the hook's presence changes nothing observable** — the regression test that
protects the operator's game from the tool.

---

## 6. Then Deliverable 3.3 and 3.4 — the live dodge and the escape floor

From [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md):

- wire the solver's chosen heading to the actuation mechanism this document proves, through the
  published slot of §4;
- ship it **behind its own switch**, defaulting to a mode that logs decisions without acting;
- keep the escape floor running underneath, exactly as the reference engine keeps an immediate reflex
  under its planner: if the solver is degraded, stale, immobile or has not decided, the floor still
  works;
- **precedence is part of the deliverable and is written before the dodge goes live**: `World.
  RequestEscape()`'s channelled branch cancels on movement (`World.cs:13043-13047`), so a dodge that
  steers cancels an in-progress nexus cast in the same frame. The escape is authoritative; the dodge
  observes the client's cast flag and yields. The hazard is scoped to the `Impermanence` totem, which
  is exactly why a suite that never equips it passes while the bug sits untouched
  ([VERIFICATION.md](VERIFICATION.md) §5.1).

**Verification, in one line each:** offline for the decision and every weight; the **paired trial**
for the acting decision — dry run logs the moment it *would* have moved and the moment it *would*
have escaped, compared against the `HealthUpdate` trace and the death, with **every death accounted
for in one of the two buckets** ([IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §"Verification");
the two-controller test for precedence; and the relay for `GmKicked` and `EscapeAck`.

---

## 7. The next live runs, as a protocol

Each is one command and each is independent. The first two close open items from this document; the
third is the dodge's own.

```powershell
cd G:\coding\javamapp\autododgeresearch

# 1. THE OUTSTANDING WITNESS (closes check D). Same sweep, with the relay attached.
.\hook\tools\Run-Phase3Session.ps1 -Experiment Sweep -Seconds 300 -RelayLog <relay-session.log>

# 2. THE ROTATION TERM (closes 3.2) and THE FLOOR (closes 3.5). One world, 25 s, then a nexus.
.\hook\tools\Run-Phase3Session.ps1 -Experiment Escape -Seconds 120 -RelayLog <relay-session.log>

# 3. THE DODGE, once §5 exists: dry run first -- it decides and logs, and does not move.
.\hook\tools\Run-Phase3Session.ps1 -Experiment Hold -Seconds 180 -RelayLog <relay-session.log>
```

For runs 1–2 the operator is in a zone and does nothing; for run 3 the character will walk under the
solver's control, so it belongs somewhere it does not matter.

**One environmental fact, because it cost this session several runs:** the game reaches the **loading
screen and stops** when it is launched from the DSH agent harness, because its data directory
(`%LOCALAPPDATA%\RippleStudio\Darza`) is outside the harness's write confinement — it writes no
`darza.log` at all, which is the tell. The agent's own shell can write there; a *game process it
spawns* cannot. **Run these from the operator's own shell.** The same confinement is why a
harness-launched run needs `-RunRoot` inside the repository ([README.md](README.md),
[PHASE1-HANDOFF.md](PHASE1-HANDOFF.md) §11).

---

## 8. Where every number in this document came from

| claim | source |
|---|---|
| the eight-phase table, 1428/1951, residuals, mean cos | `steering_report.py` on `%LOCALAPPDATA%\drelay\hook\20261014-040859-p22748` |
| worst \|written − read back\| = 0.0000000 rad | the report's check A, from 128 `steer frame` lines in that run's `bootstrap.log` |
| the nested-round-trip proof | that run's `bootstrap.log`, `steering input write proved on the first live input` |
| 16.680 ms / 60.0 Hz, worst frame call 346 µs | that run's `hook.log` |
| `2 of 5 target(s) patched`, `movementIntegrator` at `00007FFF9E80D620` | that run's `hook.log` |
| the wedge (`d (0.0000,0.0000)` for 210 frames) | that run's `bootstrap.log`, `steer frame 541`…`751` |
| the offline step distribution (1.44 / 1.96 units per 200 ms) | [PHASE3-SCOPE.md](PHASE3-SCOPE.md) §5.2, `tools/move_budget.py` |
| the value-type `castclass` fault and the four-case table | the standalone reproduction in `work/warmrepro/`, and the faulting run's stderr |
| the abstract-receiver refusals | same reproduction, `Activator.CreateInstance` and `RuntimeHelpers.GetUninitializedObject`, both `MemberAccessException` |
