# Spike 3.2 handoff — the solver exists, and the ten defects it took to make it move

**Date written:** 2026-10-14. **Branch:** `autododge-research`.
**Read with:** [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §5 (the solver this document reports: every
stage it names now exists), §5.3–§5.7 and §10 (the rules and invariants as implemented and as
asserted), [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §"Spike 3.2 — the solver, offline" (the
criteria this document reports against), [PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) (the actuator this
solver steers through, and §5.1's build order, which this follows exactly),
[PHASE3-SCOPE.md](PHASE3-SCOPE.md) §6 (where the phase stood), and
[CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §5.4 (the collision grid the terrain rules read).

---

> **READ §11 WITH THIS DOCUMENT.** Every section below §10 reports the solver as it stood before
> 2026-10-15's second change: **the path model**, in which a candidate was a point and its distance to
> a bullet's whole predicted polyline. §11 replaces that geometry with a plan walked in spacetime —
> two bodies, one clock — and adds the forward goal the operator asked for. The numbers below are the
> *before* column of §11's tables and have not been rewritten; §11 states which of them moved and by
> how much, and §11.3 records a defect in the harness that means any figure below produced through
> `SolverHarness --config` was computed with the default weights whatever the command line said.
>
> **§12 is the first live session after §11, and it found something older and larger than either: more
> than half of that session's threat set was the player's own projectiles**, because the sensor never
> asked the client's `HitsPlayer` question. It also settles the camera-rotation question the operator
> raised, and corrects one claim in §9.5 about a line-length limit that does not bind the file those
> lines are written to.

## 0. The state, in one paragraph

**The solver exists, it is a pure function of a plain-data snapshot, and it is asserted against
five synthetic scenarios by an offline harness that fails on any of the four invariants the plan
put here.** It steers when a bullet is dodgeable and it does not when one is not, which is the whole
of what an offline harness can establish. It has now also been recorded against the game three
times, and the answer is a split one: **T7 — "predicted and actual collision agree" — passed on the
first recording (9 of 9) and fails on the two since** (2 of 18 in §4.6, 15 of 68 in §4.7). What those
two recordings bought is not a better miss rate but a better instrument: the harness and the live
observer now read the same geometry — asserted, not eyeballed, as invariant `T7P` — so a miss is
the trajectory model's own rather than a disagreement between two implementations that were never
compared. §4.7 is the verification, and §6 is where the residual lives.

**Ten defects stood between "the solver is written" and "the solver moves", and all ten produced
no error, no crash and no log line.** They are §3, and they are the most useful thing in this
document: seven were found by tests *designed to fail*, and the last three — an empty recording that
the harness called a pass, a trace that recorded every bullet as the same bullet, and an input gate
that disabled the solver on every frame of a live session — were found by live sessions, and are the
most expensive of the ten.

**Added after §4.7, and it is the twelfth of the same species, with a twist: it produced an error,
a stack trace and a log line, and was still invisible.** §3.11 is the overload that silently cost
the project its actuator — found by a probe written to test something else, and would have broken
every steering run rather than only the dodge. It is recorded with the other eleven because the
lesson is the same one: **what a clean build and unchanged numbers do not tell you.**

**Added after §4.3, and it is the eleventh of the same species: the cost number was wrong.** The
harness timed one `Decide` per frame while allocating a snapshot inside the same window, so it
reported 890 µs of garbage collection as the solver's cost; the real p99 is **145 µs** over the
frames that evaluate. §4.4 is the correction, the measurement discipline that produces it, the
per-frame threat envelope that was the genuine missing optimisation, and a second optimisation that
was tried, measured *slower* and removed. **A wrong cost number is a defect of the same kind as the
other ten: it produces no error, no crash and no log line, and it sends the next reader to the wrong
piece of code.**

---

## 1. What exists now, against the algorithm's pipeline

[DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §2 is the per-frame pipeline. Against the code as it
stands:

| pipeline stage | state | where |
|---|---|---|
| 0. health/budget refresh | exists | `DodgeSnapshot.PlayerView`, `TrajectorySampler.LastFrameSeconds` (the game's own clock, not wall time) |
| 1. threat set | exists, Phase 2 | `ThreatSet.cs`, `SensorRead.cs` |
| 2. trajectory sampling | exists, Phase 2, **extended** | `TrajectorySampler.FillSamples` now keeps `GetPosition`'s own return per sample and freezes past it |
| 3. egress test | **exists** | `DodgeSolver.TryEgressDirection`, used only when the player is already inside a disc |
| 4. decision | **exists** | `DodgeSolver.Decide` — floor, 77 candidates, seven admission rules, temporal test, scoring, hysteresis |
| 5. act | exists and certified, **and now wired** | `SteeringPilot`; **the wiring from stage 4 to stage 5 landed as Deliverable 3.3 — see §9** |
| 6. the escape gate | **exists as a decision** | `DodgeSolver.CountUnavoidable` produces the projection; the `RequestEscape` call is Deliverable 3.4, and §9 says why the adapter deliberately does not make it |

**The files, and what each one is for:**

- [`hook/managed/Drelay.Bootstrap/DodgeSnapshot.cs`](hook/managed/Drelay.Bootstrap/DodgeSnapshot.cs)
  — the contract. `PlayerView`, `Trajectory`, `CollisionWindow`, `EntityBody`, `DodgeSnapshot`,
  `DodgeConfig`, `DodgeDecision`, `DodgeAction`, `DodgeReason`, `AdmissionRule`. **Nothing in this
  file may name a game type**, and that is what makes the next file compile in a console program.
- [`hook/managed/Drelay.Bootstrap/DodgeSolver.cs`](hook/managed/Drelay.Bootstrap/DodgeSolver.cs)
  — the decision, in §5's own order, allocating nothing.
- [`hook/managed/Drelay.Bootstrap/DodgeAdapter.cs`](hook/managed/Drelay.Bootstrap/DodgeAdapter.cs)
  — **Deliverable 3.3, and the file that closes the gap `PHASE3-HANDOFF.md` §3.4 named.** It is the
  half that was missing: the thing that reads the live world, fills the snapshot the solver was built
  to consume, decides, and either logs what it would have done (the dry run, the default) or
  publishes a heading and a magnitude into the slot `SteeringPilot` writes from. §9 is its report.
- [`hook/managed/Drelay.Bootstrap/TraceRecorder.cs`](hook/managed/Drelay.Bootstrap/TraceRecorder.cs)
  — the recording half. Off unless `DRELAY_TRACE=1`; writes JSON lines.
- [`hook/tools/SolverHarness/`](hook/tools/SolverHarness/) — the offline half. A console program
  that **compiles the solver's two source files** rather than referencing the hook's DLL, so the
  thing being tuned offline is the thing that will steer. `Synthetic.cs` generates the five
  scenarios; `TraceFile.cs` reads a recording and the `collision:` lines beside it.
- [`hook/tools/Probe-DodgeDryRun.ps1`](hook/tools/Probe-DodgeDryRun.ps1) — the wiring probe. Runs
  the adapter against the synthetic host and checks that it arms, decides and reports; §3.11 is what
  it found on its first run. **It is currently INCONCLUSIVE on this machine** — see §9.4.

---

## 2. How to run it

### 2.1 Offline, with no game

```powershell
cd G:\coding\javamapp\drelay\autododgeresearch
hook\tools\SolverHarness\bin\Release\SolverHarness.exe --scenario dodge
```

Six scenarios: `peaceful`, `headon`, `dodge`, `volley`, `corridor`, `passingby`. Each is chosen to
be falsifiable rather than realistic — `peaceful` fails if the solver flinches, `dodge` fails if it
does not move, `corridor` fails if it walks through a wall. Four invariants plus one move assertion
are reported, and the exit code is the verdict.

**`dodge` is the only scenario whose assertion is "the solver moved", and it exists because three
of the four invariants are satisfied by a solver that never moves at all.** Without it, a suite
that "passed" could be a suite that did nothing.

Weights are tunable from the command line, which is what "tune in the harness, never in a live run"
has to mean concretely:

```powershell
SolverHarness.exe --scenario dodge --config durable=0.5,margin=0.2,lateral=0.8
```

`--frame N` prints one frame in full: the player, every threat, every candidate with the admission
rule that refused it. **That flag found five of the seven defects in §3**, and it is the first thing
to reach for when a scenario fails.

`--repeat N` runs the decision N times per frame and keeps the fastest, which is the only way to read
the cost section honestly — a single-shot `decide()` measures the harness's own allocations (§4.4).
Use `--repeat 200` before believing a p99.

### 2.2 On the wire, for T7

```powershell
# one recording session. The character will walk if the experiment steers; -Trace does not move it.
hook\tools\Run-Phase3Session.ps1 -Experiment Hold -Seconds 300 -Trace

# then, offline, with the game closed
hook\tools\SolverHarness\bin\Release\SolverHarness.exe `
    --trace <run-dir>\trace.jsonl --collisions <run-dir>\bootstrap.log
```

**Check the file grew before doing anything else with it:**

```powershell
(Get-Item <run-dir>\trace.jsonl).Length    # a few hundred bytes means the header and nothing else
```

`-Trace` passes the launcher's `--trace`, which sets **all three** variables a recording needs:
`DRELAY_TRACE` (the recorder), `DRELAY_HOOK_FRAME_REPLACEMENT` (the recorder is called from the
managed frame replacement, not the native detour) and `DRELAY_FRAME_SENSOR` (the trace's input is
that sensor's output). §3.8 is what happens when one of the three is missing — a valid, empty file
that the harness used to call a pass.

**The cost, so it is not a surprise:** about 265 KB per 900 frames with two bullets alive, so a
minute of a busy fight is tens of megabytes. That is why it is off by default.

---

## 3. The ten defects, and what each one teaches

They are here rather than in a changelog because **six of them produced no error, no crash and no
log line**: they produced a dodge that stood still, or moved for no reason, or moved somewhere it
had already refused. A reader who inherits this work should know which of them the aggregate
numbers could not see.

### 3.1 `Add(1, heading, 0.0f)` refused the `stand` candidate, and the preferred answer was dead code

**Symptom:** the solver evaluated 74 candidates, found 71 of them admissible, and then did nothing.
Later, after other fixes, it moved on frames where the harness could prove standing was safe.

**What it was:** the stand candidate's distance is zero by definition, and `Add` carries
`if (distance <= 0.0f) return;` — which is right for every other candidate and wrong for this one.
The call did nothing, so `_results[0]` stayed the previous frame's zeroed struct, `Kind` was 0,
`FindStand` scanned for a `Kind` of 1 and never found one, and **the whole stand branch — the
specification's preferred answer, "the best dodge is usually no dodge" — was unreachable.** The
call site then patched up `_results[0]` by hand, which is what made the defect look like working
code: two of the three fields were being written, just not the one the lookup used.

**The lesson:** a lookup keyed on a field that a write refused to set is a lookup that always
returns "not present", and "not present" is indistinguishable from "not applicable". The stand
candidate is now written directly and its `Kind` is set in the same statement.

### 3.2 `DodgeDecision` was passed to `Fill` by value, and some writes were lost

**Symptom:** `Fill` set `Reason = StandSafe`; the caller received `NotEvaluated`. A flag set by the
*caller* immediately after the call survived, while the fields the call wrote did not.

**What it was:** `Fill(DodgeDecision decision, ...)` took the struct by value. Whether the writes are
visible to the caller is then a question about how the JIT chose to keep the struct, and the
measured answer was "some of them". That is not a subtle bug to be tolerated; it is a function whose
effect depends on the JIT's mood.

**The fix, and the shape of it:** `Fill` became `Filled`, which **returns** the completed decision,
and every call site assigns it. The data flow is now explicit and unmissable in review.

**The lesson, which is worth more than the fix:** the symptom looked exactly like "the solver chose
something else", and an afternoon went into re-deriving the solver's logic before anyone questioned
the *calling convention*. When a function's output disagrees with the function's own body, the body
is the second thing to read.

### 3.3 The detection radius and the collision radius were the same number

**Symptom:** the solver admitted its own durability test (nothing can reach the player) and then
refused every candidate including `stand`. 0 of 74 admitted, on every frame of a head-on approach.
The player stood still and died without the dodge ever moving.

**What it was:** one radius doing two jobs. The **warning** question is "does this bullet's
predicted path come near the line I am on" and the **collision** question is "does this bullet touch
me"; when the collision test uses the warning's (wider) radius, the solver is asked to avoid a
bullet it *cannot* avoid — a bullet whose 600 ms path crosses a line 0.65 units away but whose
nearest approach is 0.55. Every candidate fails, because the player cannot move 0.1 units in one
frame.

**The fix:** `collisionR` for rules 4, 5 and 6, `collisionR + WarningRadiusPad` for the pre-test
only. The two are named in the code and the reason is written where they are used.

**The lesson:** a single number serving two questions will be right for whichever question was
asked first, and the failure is a solver that looks *broken* rather than *confused*.

### 3.4 The "already inside a disc" exception applied to enemy bodies and not to bullets

**Symptom:** with a bullet already inside the player's disc, every candidate was refused —
including the one that left the disc.

**What it was:** rules 4 and 5 refuse a candidate that puts the player in a disc or drags them
through one. A player who is *already* inside satisfies both at the instant they start moving: the
sweep begins inside the volume, so the whole candidate set is refused and the solver dies where it
stands. [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §5.3 documents this exact exception for rule 7's
enemy bodies — "without it, every move out of an overlapping circle sweeps it, which would trap the
player under a mob that walked onto them" — and the same reasoning applies to a bullet.

**The fix:** a threat the player is already inside is not grounds for refusal unless the candidate
fails to *increase* the distance to its centre. Stepping further in is refused; stepping out is
admitted.

**The lesson:** the design already contained the reasoning for this case; it had been written down
for one hazard type and not carried across to the other. That is what a §-numbered specification is
for, and it only works if the section is read while writing the analogous rule.

### 3.5 The temporal test refused candidates for danger that was not arriving

**Symptom:** after 3.3, the failure moved rather than disappearing: the stand candidate was admitted,
and *every other* candidate was refused. The solver stood still.

**What it was:** rule 6 answered one question — "does this candidate's swept path pass within the
collision radius of the bullet's predicted path" — and treated every yes as a refusal. But that yes
covers three quite different situations: the path comes no closer than the radius (admitted
quietly); it comes closer, but only *after* this frame's decision window (recorded as the
candidate's durability, and the next frame re-decides with better information); or it comes closer
*during* this frame and the two are closing at that moment (refused). Treating all three as
refusals refuses every candidate against every approaching bullet.

**The fix:** `TemporalRefusal` returns one of the three answers, with `ImpactWindowSeconds` (0.5 s,
derived from the player's own reach) separating "nearly now" from "later".

**The lesson:** this is the one place where the specification's own sentence — "the player's swept
path must not intersect the bullet's predicted path within the horizon" — is too coarse to
implement, because *within the horizon* is not the same as *this frame*. §5.4 already makes the
distinction for `stand` ("standing is the one choice where safe now and safe later are the same
question"); it needed making for moves too.

### 3.6 The trust prefix was shorter than the sampling horizon, and it hid the closest approach

**Symptom:** the solver refused to stand because its own durability test found a bullet on the
player's position; the harness, re-deriving "durably safe" from the same recorded samples, found the
bullet 0.45 units away and said the position was clear. Four disagreements in four hundred frames,
all at the exact boundary.

**What it was:** `TrustSeconds = 0.45` is sample 15 of 21, so the trust prefix threw away the last
five samples — which for a head-on approach are the samples where the path comes **closest**. The
trusted minimum was 0.492; the true minimum was 0.45. And 0.45 is exactly the collision radius, so
one predicate said "inside" and the other said "outside".

**The fix:** `TrustSeconds = 0.65`, just inside the 600 ms the sampler was asked for, which keeps
the one-sample guard without discarding the tail.

**The lesson:** the trust prefix is a real and necessary idea, and its **length is a measurement,
not a constant to be picked**. A prefix either side of the closest approach changes the answer, and
the direction of the error is not obvious: a shorter prefix is the *optimistic* choice here, not the
conservative one.

### 3.7 The "nothing is safe" fallback emitted a move admission had refused

**Symptom:** four frames of the `dodge` scenario where the emitted candidate was one the solver's
own `Admit` had refused. Every one of them inside the incoming bullet's arrival window.

**What it was:** the step-6 fallback ("nothing is safe, take the option that maximises
time-to-impact") scanned the whole candidate array — **including the entries admission had
refused** — and emitted the best of them. The reasoning ("a desperate move beats no move") is wrong
for the reason the whole of §5.3 rests on: the admissible set is the safety boundary, so a refused
candidate is one that ends inside a disc or through a wall. Emitting it is not the least bad move;
it is the solver deliberately walking into a bullet it had already detected.

**The fix:** no admissible move means **no move**: report `NoAdmissible`, leave the player's own
steering untouched, keep the escape floor armed, and let the escape gate decide. `BestByDurability`
is deleted, and the place where it stood now says why.

**The lesson, and it is the sharpest one here:** an invariant asserted on *what was emitted* rather
than on *what the solver believed* caught this. The solver's own bookkeeping was self-consistent;
only the comparison against the admission result showed the contradiction.

### 3.8 The empty trace, and the harness that called it a pass

**This one is from a live session, not from the harness, and it is the most expensive of the eight.**

**Symptom:** the operator ran the recorded session (`20261014-164314-p12060`), joined a realm, walked
into a tree, took hits from low-damage projectiles — and the harness reported **"VERDICT: every
asserted invariant held"** over a trace containing **zero frames**. The file was 355 bytes: the
header, and nothing else.

**What it was, and it was two defects stacked:**

1. **The recorder is called from the MANAGED frame replacement, not from the native frame detour.**
   The run took the native path (`the frame read is native and needs no managed entry point`), so
   `TraceRecorder.OnFrame` was never called. `Run-Phase2Session.ps1` had passed `--frame-replacement`
   from the beginning, with the comment *"the sensor is a managed replacement, not a native
   detour"*; Phase 3's runner never had, because until this spike nothing in a Phase 3 run needed
   the sensor.
2. **Every invariant is of the form "for every frame, …", and a report over zero frames satisfies
   all of them vacuously.** The harness's own assertion design made an empty recording look like the
   best possible result.

**The fix, in three places:** `--trace` on the launcher now sets *all three* of `DRELAY_TRACE`,
`DRELAY_HOOK_FRAME_REPLACEMENT` and `DRELAY_FRAME_SENSOR`, because any one of them missing produces
a valid empty file; the runner passes `--trace` rather than setting an environment variable and
hoping; and the harness grew assertion **F** — "the recording contains frames" — which fails, with
the cause named, before the four invariants are reported.

**The lesson, and it is the one worth carrying forward:** *a test suite that passes on no data is
worse than one that fails, because it is believed.* The correctness of every individual assertion
was never in question; what was missing was an assertion that there was anything to assert about.
Any harness that reads an artefact should first check the artefact is not empty.

### 3.9 Every threat was recorded with `GameId = 0`, so the trace could not match the game

**Symptom (second recording, `20261014-165437-p19396`):** `predicted by model: 0 (0.0 %)` against two
collisions the game resolved — and, in the same report, **11 predictions that supposedly never
happened**, with closest approaches of 0.028, 0.041, 0.061 and 0.069 units against radii of about
0.4. Bullets do not pass through a player at 0.03 units and miss.

**What it was:** `Trajectory` carried `GameId` and `StartTime` as separate ints, which
`TrajectorySampler.FillTrajectory` deliberately left at **zero** — identity is the *sensor's* to
resolve, and that file says so in its own comment. `TraceRecorder.AppendThreat` then wrote
`t.GameId`, which is that zero. Every bullet in a 1289-frame live recording was therefore recorded as
`(0, startTime)`: a key that collides across bullets and matches nothing the game ever resolved.

**The fix, and it is structural rather than a one-line correction:** the two fields became a single
`Trajectory.Key` (`long`, packed `(GameId, startTime)`), set by the recorder from the `ThreatKey` the
sensor already hands it. **A pair of fields that nothing fills is an invitation to write them
anyway; a single field the caller must set is not.**

**The lesson:** the split identity was documented, deliberate and correct — and the write site
ignored the documentation because the *type* still offered the field. The same hazard as
`FieldAccessor`'s nested-versus-boxed accessors ([PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) §2.2), with
the same remedy: make the wrong thing impossible to express rather than merely documented.

### 3.10 The `Moving` gate disabled the solver on every frame of a live session

**Symptom:** the same recording read `reason: PlayerNotMoving=1289` — **every single frame**. The
solver never evaluated one candidate, while the pilot's own log for that session says the player
moved on 427 of 631 frames.

**What it was:** `Decide` returned immediately when `GameInput.Moving` was false, reasoning that the
client was not reading movement input that frame. The live data says `Moving` was false on all 1289
recorded frames — because **the pilot sets it inside the movement integrator, and the recorder runs
in the frame tick, which is a different point in the frame with a different view of the same field.**

This is precisely the trap `SteeringPilot` documents for the *write*: *"a write from the frame tick
would be consumed a frame later and overwritten by the real input poll in `PreUpdate` in between."*
The same sentence is true of the **read**, and nobody had written it down for the read.

**The fix:** `Moving` is no longer a gate. The gates that survive are properties of the player rather
than of one frame's input object — dead, safe area, and a zero speed law. `Moving` is still recorded,
where it is evidence rather than a decision.

**The lesson:** an input field is a *view of a frame*, not a fact about the world. Anything the client
re-polls every frame is meaningless at the moment another subsystem is about to write it, and reading
it from a different hook than the one that writes it is reading a different frame.

### 3.11 An overload silently cost the project its actuator, and only a probe could see it

**Symptom:** none. The build was clean, the offline scenarios reported the same move counts to the
digit, and every log line that mentioned steering said the pilot was armed. The synthetic dry-run
probe (`hook\tools\Probe-DodgeDryRun.ps1`) was written to check that the *adapter* runs, and what it
actually caught was in the file it borrowed a helper from:

```
publishing the movement replacement threw; the movement target will not be patched and the game is
unaffected: System.Reflection.AmbiguousMatchException: Ambiguous match found for
'Drelay.Bootstrap.SteeringPilot Void WriteInput(System.Object, Single)'.
   at Drelay.Bootstrap.SteeringPilot.PreparePilotPath() ... SteeringPilot.cs:line 1435
   at Drelay.Bootstrap.SteeringPilot.Publish() ... SteeringPilot.cs:line 1398
```

**What it was:** Deliverable 3.3 added a second `WriteInput` overload -- one that takes the solver's
magnitude beside the heading -- and `PreparePilotPath` warmed the pilot's frame path with
`typeof(SteeringPilot).GetMethod("WriteInput", flags)`. **`Type.GetMethod` throws on an ambiguous
name**, the throw was on the frame-path warm-up, and it took the whole `Publish` call with it: the
managed movement replacement was never published, so the movement target was never patched, so **the
actuator was gone on every steering run, dodge or not.**

**The fix:** the warm-up now iterates `GetMethods` and prepares every method whose name is in its
list. A name with overloads cannot be ambiguous that way, and no future overload can repeat this.

**The lesson, and it is a new one for this document's collection:** the other eleven defects
produced no error, no crash and no log line. **This one produced an error, a stack trace and an
error line -- and it was still invisible**, because the log line it produced was about a subsystem
the reader was not looking at ("publishing the movement replacement threw") on a run whose subject
was something else. The first nine defects were found by tests *designed to fail*; this one was found
by **a probe designed to test a different thing**, which is the argument for having one probe per
subsystem rather than one per claim: the instrument that finds the defect you did not predict cannot
be the instrument aimed at the defect you did.

**And it is the second time in this document that an offline instrument caught something the live
evidence never could have.** §4.6's lesson was that a disagreement between two measurements is the
finding; this one is that a *clean build and unchanged numbers* are not evidence that a runtime
lookup resolved.

---

## 4. The harness, and what its numbers are worth

### 4.1 The five assertions, and how each is made

| assertion | how it is checked | whose predicate |
|---|---|---|
| **F** the recording contains frames | the trace has at least one frame. **Runs first, and only when `--trace` was given** | the harness's |
| **T5** `stand` is never rejected while durably safe | moved + durably safe at the current position + not already inside a disc + `stand` admitted by the solver's own `Admit` ⇒ FAIL | the solver's, asked twice via `IsDurablySafeAt` / `AdmissionOf` |
| **T6** no emitted move is inside a disc at arrival | the emitted candidate is re-tested against every threat's recorded disc with the game's own `DistanceSquared < r_eff²` | the harness's, from the trace |
| **T10** hysteresis cannot hold a heading that has become unsafe | the emitted candidate's own admissibility, and `decision.Held` | the solver's |
| **T7** predicted and actual collision agree | each `collision:` line from `CollisionObservation` matched against the first frame the model predicted that bullet's path would enter the disc | both, from two files written by two code paths |
| **MOVE** the solver steers when a bullet is clearly dodgeable | the `dodge` scenario must produce at least one `Move` | the harness's |

**T5 and T6 are deliberately asked of different predicates, and that is the design.** T5 asks the
solver's own durability test, because a harness re-derivation produces "failures" that are really
the difference between two nearly identical predicates — which is exactly what happened, twice, in
the course of building this. T6 asks the *trace*, because "the move I emitted is not inside a
bullet" is a claim about geometry and not about the solver's opinion.

**Assertion F exists because of §3.8, and the shape of it is the point.** T5, T6, T7 and T10 are all
of the form "for every frame, …", so a trace with no frames satisfies every one of them. F is the
assertion that there is anything to assert about, it runs before the others, and its failure message
names the cause. **Any harness that reads an artefact should check the artefact is not empty.**

### 4.2 What the numbers say, and what they do not

Measured over the synthetic scenarios, 900 frames each:

| scenario | moved | verdict |
|---|---|---|
| `peaceful` | 0 % | all invariants held |
| `headon` | 0 % | all invariants held (the bullet misses by more than the warning corridor) |
| `dodge` | 5.8 % | all invariants held; **the move assertion passes** |
| `volley` | 0 % | all invariants held |
| `corridor` | 0 % | all invariants held (the lane's walls leave no admissible escape) |
| `passingby` | 0.8 % | all invariants held |

**Three things these numbers do not establish, and they matter more than the table:**

1. **Whether the trajectory model is right.** That is T7, and it needs §6's run. The synthetic
   scenarios exercise the solver's *geometry*; they say nothing about whether the game's own
   `GetPosition` predicts what the game's own collision test then finds.
2. **Whether the weights are any good.** Every weight in §5.6 is still at the specification's
   starting value. The harness can rank them; nobody has yet sat down and tuned them, because the
   thing they should be tuned against is recorded play. `--config` exists and has never been used in
   anger.
3. **What a real candidate count looks like.** The synthetic scenarios have 0–4 live threats; the
   live sensor sees 12–28 per frame, and **the decision's cost against that set has never been
   measured**. The harness prints a `decide()` p50/p99 over synthetic frames and those numbers are
   not evidence about the live case. `PHASE3-SCOPE.md` §6 item 4 — the cost work Phase 2 waived —
   is still open.

**One measurement worth recording because it looks wrong and is not:** `chosen durability` reports
p50 = 650 ms in `dodge`, i.e. the trust prefix, for most moving frames. That is honest rather than
degenerate — the durability term answers "how long until this specific candidate's path is
violated", and for a candidate that is clear it is clear for the whole horizon, so the score
collapses towards "safe" versus "not". §5.6's second-heaviest weight therefore does less ranking
than the specification suggests; **that is a tuning question and it belongs with the first real
trace, not in a synthetic scenario.**

### 4.3 The first live numbers

Two recordings, and the second is the one that counts — the first is where §3.8, §3.9 and §3.10 were
found, and its T7 numbers are void because the identities in the file were corrupt.

#### The T7 result, which passes

From `20261014-170146-p30952` — 1229 frames, 20.9 s, **66 bullets tracked, 79 grid windows**, and
**9 collisions resolved by the game**:

| measurement | value |
|---|---|
| collisions the game resolved | **9** |
| of those, predicted by the model | **9 (100 %)** |
| **false negatives** (a hit the model never saw coming) | **0 (0 %)** |
| mean notice, first prediction → collision | **275 ms** |
| bullets predicted to hit that did not | 26 of 52 (50 %) |

**Zero false negatives is the result this spike existed to produce, and it is the load-bearing one.**
The model saw every collision coming, with a quarter of a second of notice. The failure mode that
kills — standing still while a bullet arrives unnoticed — did not occur once in nine collisions.

#### The false positives, decomposed

The 50 % over-prediction rate was decomposed by a second pass that asks, for each prediction, what
was actually true at the frame the impact was predicted for:

| what happened | count | who is at fault |
|---|---|---|
| the bullet was gone before it was due | **13** | the projection outlived the bullet |
| the bullet was elsewhere on arrival | **26** | the model |
| still arriving where the player had moved to | 4 | nobody: the player left |

**The 13 are a known gap with a known fix.** `TrustSeconds = 0.65` treats every trajectory as
believable for 650 ms, but `Trajectory.Alive` already records the sample at which the game's own
`GetPosition` said the shot had ended — the end-of-shot signal, captured for exactly this reason and
**not yet used to bound the trust prefix.** A bullet that dies at 200 ms should not be projected to
650. That is a contained change in `Trajectory.TrustedCount`.

**The 26 are the tuning question.** A bullet whose recorded path demonstrably passes within 0.03
units of the player, at a recorded frame, is a bullet the model was right about — so the residual
error is either the model projecting a path the bullet then deviated from, or the fair test itself
(which compares the recorded path against the player's recorded position, both sampled on their own
grids). **Distinguishing those two is the next piece of real work**, and it needs a third recording:
the trace does not carry enough to separate "the bullet moved differently" from "the comparison is
imprecise".

**The direction of the error is the reassuring part, and it is worth stating plainly.** Over-prediction
makes a dodge flinch at bullets that were never going to hit. Under-prediction makes it stand still
and die. The model has **0 %** of the second and **50 %** of the first, which is the correct side to
be wrong on — and it is also the side that costs the operator walking they did not ask for, so it
still needs fixing before the dodge is wired live.

#### The solver's own behaviour

| measurement | value | what it means |
|---|---|---|
| decisions evaluated | **1229 of 1229** | after §3.10's fix. Before it: 0, and the reason given was a lie |
| frames that moved | 36 (2.9 %) | `OverrideUnsafe` 22, `Hysteresis` 8, `IntentSafe` 6 |
| `StandSafe` | 28 | the specification's preferred answer, taken when it was true |
| `NoAdmissible` | **109 (8.9 %)** | no move was safe at all. The escape gate is armed for exactly these |
| candidates, mean admissible | 15.9 of 74 | the terrain and the bullets refuse four fifths of the ring |
| `decide()` p99 | **890 µs** | **three times the entire 300 µs frame budget** |
| `decide()` max | 5683 µs | the first call, i.e. JIT |

> **BOTH OF THOSE COST NUMBERS ARE WITHDRAWN. Read §4.4 before quoting either.** A single-shot
> `Stopwatch` around one `Decide` in this harness measures the garbage collector as much as the
> solver, and the harness was allocating a fresh snapshot per frame inside the timed window. The
> corrected reading is **p99 145 µs over the frames that evaluate**, and the defect that produced
> 890 µs was in the measurement, not in the solver.

**The cost is now the binding constraint.** [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §8 allots
candidate admission 100 µs; the measured p99 is 890 µs, and the previous recording measured 408 µs
against a smaller threat set. The solver alone overruns the budget before the sampler contributes
anything, and [PHASE3-SCOPE.md](PHASE3-SCOPE.md) §6 item 4 — the cost work Phase 2 waived — has
moved from "tidy-up" to "prerequisite". The mitigation §8 names first is not implemented: **the
solver tests every bullet against all 74 candidates, with no cull by closing speed and no use of the
projectability flag.** That is the next piece of work, and it changes nothing about what T7 measures.

> **§4.4 corrects this paragraph, and the correction runs the other way.** The cull that was missing
> was neither of the two named here: it was the **per-frame threat envelope** — one `TrustedCount`
> and one sample bounding box per threat per frame, instead of per threat **per candidate**. With it,
> the decision's p99 is 145 µs against the 300 µs budget, with 42 µs at p50. The closing-speed cull
> and the `Projectable` flag are still unused.

**The 109 `NoAdmissible` frames (8.9 %) are the honest half of the result.** Some are the wedge —
the player pinned against a tree, `walkable` and `path clear` refusing every candidate, the terrain
rules doing their job — and the rest are genuinely closed positions. **Those frames are where the
escape gate will fire**, and 8.9 % is high enough that its threshold had better be right before the
dodge is wired to the actuator.

### 4.4 The cost number was measuring the collector, and the cull that mattered was not the one §8 names

**Written 2026-10-14, after §4.3, against the same recording
(`20261014-170146-p30952`) and the same six synthetic scenarios. Every behaviour number in §4.3
still reproduces exactly; only the cost numbers change, and they change because the measurement was
wrong.**

#### What was wrong with `decide()` p99 = 890 µs

The harness allocated a fresh `DodgeSnapshot` — a `DodgeSnapshot` object, a `List<Trajectory>` and a
`Trajectory` per live threat, each with three 21-element arrays — **for every frame, inside the same
loop that timed `Decide`**. A single-shot `Stopwatch` around one call therefore reports the solver
plus whichever collection happened to land in that window, and the tell is that the number did not
track the algorithm:

| what changed | reported single-shot p99 |
|---|---|
| §4.3's run | 890 µs |
| the same run, `DOTNET_gcServer=1` | **516 µs** |
| removing work from `TemporalRefusal`'s standing window | 737 µs |
| removing the per-candidate threat bounding box | 746 µs |
| eight-frame JIT warm-up added | 688 µs |

**A number that halves when you change the collector and barely moves when you delete work is not a
measurement of the work.** The harness now measures it properly, and two of its own defects were
part of the problem: it never warmed the JIT, so the first frame's compilation was reported as
`max` (5683 µs), and it measured every frame including the 1056 that return at `NoThreat` without
building a candidate, which flattens p50 to zero and makes p99 the tail of the *cheap* frames.

#### The fix, in the harness

- **`--repeat N`** runs the decision N times on the same snapshot and keeps the **fastest**. The
  decision is a pure function of the snapshot except for its own bounded hysteresis state, so the
  fastest of N is the run with the least interference. The decision reported, the four invariants
  and the T7 tracking are unchanged: they all still use the first run.
- **An eight-frame JIT warm-up** before the timed loop.
- **Two cost lines**, not one: over all frames, and over the frames that actually evaluated a
  candidate set. On this recording that is **173 of 1229**.

#### The real number

`SolverHarness.exe --trace <run>\trace.jsonl --collisions <run>\bootstrap.log --repeat 200`:

| measurement | value | note |
|---|---|---|
| `decide()`, all frames | p50 **0 µs**, p99 **116 µs**, max 592 µs | 1056 of 1229 frames are `NoThreat` and cost nothing |
| `decide()`, evaluating frames | p50 **42 µs**, p99 **145 µs**, max **448 µs** | over 173 frames, where the work actually is |
| `decide()`, single-shot, for the record | p99 688 µs, max 1342 µs | **the artefact, for comparison** |

**The budget is met, and the honest statement of it is "typical is easy, the worst frames are not".**
p99 at 145 µs leaves half of [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §8's 300 µs unspent, and
`Decide` is the largest single consumer in that table only on the frames that evaluate. The max of
448 µs does overrun it, and it lands on the three densest frames of the recording (frames 931–943,
thirteen threats in the *already culled* set, against a mean of 0.8). A handful of such frames a
session is survivable and is worth knowing about rather than smoothing over.

**What the tail is, precisely.** The p99 over 173 samples is the third-slowest frame, so it moves
between runs (139–223 µs over the four runs behind the table) while p50 does not (42–43 µs). Treat
the distribution as "40 µs typical, the high hundreds on the densest frames", not as a bound.

#### The change that did it: `BuildAdmitEnvelope`

`Admit` rebuilt two things **per candidate** that do not mention the candidate at all: the threat's
`TrustedCount`, and the axis-aligned box of the threat's own 21 samples. With 74 candidates and 13
threats that is 74 × 13 × 21 float comparisons per frame, every frame, to answer a question about
the threat set. Both are now computed **once per frame** in `DodgeSolver.BuildAdmitEnvelope` and
read by index.

**It is a pure hoist, and the proof is in the behaviour, not the reasoning:** the recorded trace
reports the identical decision histogram to the digit (`NoAdmissible=109 OverrideUnsafe=22
IntentSafe=6 StandSafe=28 Hysteresis=8 NoThreat=1056`, 36 moves, mean 15.942 admissible), all six
synthetic scenarios report the same move counts as §4.2 (`dodge` 52 of 900, `passingby` 7 of 900,
the rest 0), and all four invariants hold.

#### The change that was tried and measured **worse**, and is not in the code

`TemporalRefusal`'s standing window walked all 21 samples of the trust prefix for every admitted
candidate, to answer a question the decision window settles in the first 50 ms. Bounding that walk
by `max(MoveTemporalWindow, ImpactWindow)` looks exact — every value it computes is read through
`nearestMs <= windowMs`, `nearestMs > impactWindowMs` or `IsClosingAt`, all of which are settled
past that time — and it was implemented on that reasoning.

**It measured 350–434 µs p99 against 145–221 µs for the unmodified loop.** The reasoning had a hole
in it: `IsClosingAt` rounds `nearestMs` back to a knot and reads `knot - 1`, so a shorter walk
settles on an *earlier* nearest knot, and more candidates then take the closing-time path through an
extra `PointSegmentDistanceSquared` than the longer walk resolved through a cheap early return. The
loop is byte-for-byte as it was, and the comment where it lives now says why. **"Provably
equivalent" and "measurably faster" are different claims and only the second one is worth a
diff.**

#### §4.3's "13 of 43 false positives are a known gap with a contained fix" — that fix is already in, and does nothing here

§6 item 1 asks for the trust prefix to be bounded by the bullet's own end-of-shot. **It already is**:
`Trajectory.TrustedCount` takes `min(byTime, byAlive)` and has since the file was written. What the
recording shows is that the bound **never fires**: across the 1011 threat records in this session,
**not one contains a sample with `Alive[s] == false`**. `GetPosition` returned true for all 21
samples of every threat, every frame. A bullet whose shot ends inside the 600 ms horizon would be
caught by that bound; the thirteen false positives whose bullet "was gone before it was due" are
tracked in frames beyond the prediction's impact, so this bound cannot be what explains them. The
remaining guess is the other end of the same idea — the **650 ms trust prefix outliving the bullet**
— which is a different bound in a different place.

**So item 1 of §6 is closed as already-implemented, and it is evidence rather than a fix:** the
`Alive` array is populated, recorded and trusted, and on this session it was simply always true.

### 4.5 The second live session: T7 stronger, the cost confirmed, and a truncated last record

**Run `20261014-192048-p15744`, 2171 frames, 36.9 s, `-Experiment Sweep -Trace`, camera rotated by
hand throughout. Every invariant held. The recording is the best one yet and it exposed one more
harness defect and one steering-pilot defect that matters more.**

| measurement | this run | §4.3's run |
|---|---|---|
| frames | 2171 | 1229 |
| **collisions the game resolved** | **24** | 9 |
| **of those, predicted by the model** | **24 (100 %)** | 9 (100 %) |
| **false negatives** | **0 (0 %)** | 0 (0 %) |
| predicted, no hit | 13 of 45 (28.9 %) | 26 of 52 (50 %) |
| mean warning to the hit | **421 ms** | 275 ms |
| frames that moved | 116 (5.3 %) | 36 (2.9 %) |
| `NoAdmissible` | 177 (8.2 %) | 109 (8.9 %) |
| `decide()`, evaluating | p50 49 µs, p99 **217 µs**, max 230 µs | p50 42 µs, p99 145 µs, max 448 µs |
| evaluating frames | 330 of 2171 | 173 of 1229 |

**T7 is now the strongest result in the document, and it is the same result twice.** 33 collisions
across two sessions, one of them with a rotating camera, and **every one was predicted**. The
over-prediction rate also fell from 50 % to 28.9 %. The mean warning rising from 275 ms to 421 ms is
not obviously an improvement or a regression — it is the model noticing collisions earlier in a
denser fight — and it is recorded rather than interpreted.

**The cost claim from §4.4 holds at scale.** 330 evaluating frames instead of 173, a threat set of
the same shape, and the tail is *tighter* than the first recording's: p99 217 µs and max 230 µs
against 145 µs p99 and a 448 µs max. `Decide` is inside the 300 µs budget on the worst frame of a
36-second combat session. The `max` no longer sits near an order of magnitude above the p99, which is
what a real cost distribution looks like and what §4.4 suspected the 448 µs was not.

#### The eleventh-hour harness defect: a half-written last line made 2171 good frames unanalysable

The reader threw `FormatException` on the file's **last** line — cut inside a coordinate,
`...,1211.228,1211.079,1210.923,1210.76,1210` — and the harness exited 2 having analysed nothing.
The cause is ordinary: the recorder appends a line per frame and flushes every 30, and the operator
ends the session by closing the game by hand, so the process can die inside a `Write`. A complete
file always ends in a newline; this one did not. All 2473 lines before it were complete.

**This is §3.8's lesson arriving a third time, in the reader instead of the assertions.** The fix is
two-part and both parts are the point:

- the reader tolerates a malformed line **only** when it is the last line of a file that does not end
  in a newline. A malformed line anywhere else still throws, because that means the writer is broken
  rather than the session being interrupted.
- `DodgeTrace.Warnings` **was collected and never printed by any caller.** Truncation, unknown record
  types, all of it — written to a list nobody read. The report now prints every warning next to the
  frame count it is short of, and `--strict` turns them into a failure for a CI-shaped run.

#### The steering-pilot defect this run exposed, which is worse than the truncation

**`sweep` mode deliberately skips the world-rotation correction** — `SteeringPilot.PhaseAngle` is
`_sweepMode ? target : target + worldRotation` — because the sweep's whole purpose is to *measure* the
mapping by writing a known input and reading the world heading back. That is sound **only if the
camera is still**, and the session instruction was to rotate the camera throughout. Observed:
`mean cos(observed − last commanded)` of 0.829, **−0.493, −0.619**, 0.672, 0.694, and worse in the
later phases. Those numbers are not a failed dodge; they are a measurement taken with a moving
reference.

**And the rotation is finally real: `World.WorldRotation` was non-zero on 1871 of 2172 frames, with
353 distinct values reaching 359.5°.** [PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) §3.2's blocker — "0.0°
on every logged frame, so the transform has never been exercised" — is now false. But §3.2 is **not
closed**, and it would be wrong to report it as closed: the per-phase table cannot confirm
`observed = target + WorldRotation`, because each phase's rotation is itself *averaged over a
rotating angle* and the circular mean of a value that sweeps 180° is meaningless. The phase means
land near the range they should, and that is all that can honestly be said.

**What the next run needs, concretely:**

1. **`sweep` must apply the correction** (`target + worldRotation`, the same as `hold` does), so the
   observed heading is comparable to the commanded one while the camera moves. The mapping is then
   measured by the residual, not by the raw heading.
2. **The phase summary must log `World.WorldRotation` at the moment the observed heading is
   sampled** — one number per phase, not a mean over the phase. Without it the residual cannot be
   attributed to the rotation rather than to the pilot.

**What the next run needs — IMPLEMENTED, so the next run is the measurement rather than the
preparation:**

1. ~~`sweep` must apply the correction~~ — **done.** `PhaseAngle` is now `target + worldRotation` in
   **every** mode, so the commanded *world* heading is the phase target whether or not the camera
   moves. `_sweepMode` is gone: with the correction unconditional it no longer discriminated
   anything, and the compiler was right to say so (`CS0414`).
2. ~~The phase summary must log `World.WorldRotation` at the moment the observed heading is
   sampled~~ — **done, and the pairing was the subtle part.** The displacement measured at call *N*
   was produced by the input written at call *N−1*, executed with the rotation live at call *N−1*,
   so the accumulator now reads `_prevRotation` and `_prevCommandedRadians` and `EndCall` ages both.
   The summary prints `mean WorldRotation` as a circular mean beside the cosine.

**What the next run therefore answers, and it is two things at once:** a `hold` run with the camera
turned gives the transform's residual directly (constant commanded world heading, every deviation is
the transform), and the same session carries the dry-run decision stream if the adapter lands first.

Until that run, the world-to-input conversion for the dodge remains a resolved formula whose residual
is unmeasured — but the assumption that `WorldRotation` is *always zero* is dead, and the correction
is now exercised on every frame of every mode rather than only when the camera happens to be still.

**One more observation, recorded because it will confuse the next reader:** several false positives
in this run carry small `GameId`s — `(9,93809)`, `(10,93809)`, `(13,93945)` — while every other
bullet is in the 175000–386000 range. Either the game recycles ids into a low range for a second
projectile family, or those are a different entity class being tracked as projectiles. Nothing in
this run depends on it; a future T7 decomposition will.

### 4.6 The first genuine false negatives: a real live-sensor bug, found by a disagreement

**Run `20261014-193728-p4508`, 13169 frames, 233 s, `-Experiment Hold -HoldDegrees 90 -HoldFrames
5000`, camera rotated continuously. Every invariant but T7 held. T7 reported 2 collisions the model
never predicted — the first genuine false negatives in the spike's recorded evidence — and the hunt
for them found **a real defect in the live prediction path that the offline harness could not have
seen, because the harness does not contain that code.**

#### The two misses, and why the harness is right about them

| bullet | trace `rad` | disc the harness used | closest the recorded path came | the game's hit disc |
|---|---|---|---|---|
| `(213419,62289)` | 0.16 | 0.41 | **0.423** | 0.45 |
| `(213714,68996)` | 0.23 | 0.48 | **0.532** | 0.45 |

Both are genuinely outside: the sampler's own predicted path never entered the disc on any of the
twelve frames each bullet was recorded for. **The harness is not the defective half.**

#### What the live sensor was doing instead

`TrajectorySampler.OnFrame` decided "this bullet is on a collision course" with

```csharp
float radius = _getRadius(projectile);
float radiusSquared = radius * radius;
if ((dx * dx) + (dy * dy) <= radiusSquared) { ... MarkPredicted(...); }
```

The client's predicate is `DistanceSquared(World.Player.Position) < (World.Player.Radius +
Radius)^2` (`CLIENT-MAP.md`, `Projectile.cs:1573`). **The player's half was missing.** For a
0.2-radius bullet the live check asked "is any sample within 0.2 of the player" where the client
asks 0.45 — a disc 2.25× too small in radius, or five times too small in area. That is the whole of
the 2-versus-14 disagreement: the live observer under-predicted, and every hit whose closest
approach fell between the two discs was counted a false negative that the harness rightly called
predicted. *(Corrected in §4.7: the live observer's final count on this run is 14, not the 11 quoted
below; 11 is the value of its running checkpoint for most of the session.)*

**Two things are worth more than the fix:**

1. **The comment above the code stated the wrong predicate.** It read *"the game's own test is
   `DistanceSquared(player.Position) < r*r`"* — a misquotation that made the wrong comparison look
   deliberate, and that a reader would have had to distrust the comment to disbelieve. **A comment
   that misquotes the client is worse than no comment: it is a licence to keep the bug.** It now
   quotes the real predicate and names the file and line.
2. **No offline test could have caught this, and that is structural rather than bad luck.** The
   harness never runs `TrajectorySampler.OnFrame`; it re-derives the prediction from the recorded
   paths with its own (correct) radius. The two implementations agreeing was never checked, and the
   only thing that surfaced it was **two counters on the same run disagreeing by 9**. That is the
   general lesson: when two independent measurements of the same quantity disagree, the disagreement
   is the finding, and it is worth more than either number.

#### The fix, and what it costs

`_getPlayerRadius` is resolved from `Player.Radius` at sampler resolution (falling back to `0.25`,
the documented default `TraceRecorder` already uses for the same quantity) and read **once per
frame** beside the player's position — not per bullet, because this code runs once per live bullet
and the frame budget is `DODGE-ALGORITHM.md` §8's concern. The resolution log now names which field
it found, or says `MISSING (the prediction falls back to 0.25)`, so the next reader does not have to
re-derive whether the correction is active.

#### What this does and does not establish

- **The live false-negative count should now agree with the harness's.** That is testable on the next
  live run, and it is the specific prediction of this fix: 11 becomes 2. **Tested, and it is half
  right — see §4.7.** The agreement is total (68 of 68 on `20261014-205622-p18428`) and is now
  asserted as invariant `T7P`; the count did not become 2, because this fix never governed the
  count. §4.7 also corrects the 11 to 14.
- **The two genuine misses stand.** 213419 and 213714 were real under-predictions by the trajectory
  model — the recorded path stayed 0.423 and 0.532 away while the game resolved hits at 0.45. They
  are the model's, not the bookkeeping's, and they are the residual to work on.
- **The offline numbers in §4.3, §4.5 and here are unaffected**, because the harness never contained
  the defect. Every T7 count in this document was computed with the correct disc.

#### The instrument this added

`SolverHarness --debug-threat <GameId>` dumps every recorded observation of one bullet with the
distance from its samples to the player's position **on that frame**, and reports whether the
recorded path was *ever* inside the disc. It exists because two earlier attempts to answer this
question compared a path against the player's position at the **recording** frame, and the player
moves ~8 world units over a 600 ms horizon — so the metric was measuring the comparison, not the
model. The flag prints the per-frame minimums so that mistake cannot be repeated silently.


#### Also new in this run

- **`mean |delta|` disagrees with the trace: the pilot reports 0.2880 world units per call, the
  trace's own consecutive positions give 0.1326 — a factor of 2.17 over the same 1821/1822 moved
  frames.** The trace's figure matches the resolved speed law (`7.94 units/s` at 16.7 ms), so the
  trace is the trustworthy one and the pilot's accumulator is counting something double. It is a
  reporting defect rather than a control defect — §1.4's `mean |delta|` confirmation rests on the
  pilot's number — and it is unexplained.
- **The transform itself is now confirmed, which is what this run was for.** Independent
  recomputation from the trace's own consecutive positions, over 1821 moved frames, gives
  `mean cos(observed − 90°) = 1.0000`, bucketed flat across the whole 0–360° of camera rotation
  (twelve 30° buckets, 98–212 moves each, every one 1.0000). The pilot's own per-phase number is
  0.997 at `mean WorldRotation −34.2°`. See [PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) §3.2.

### 4.7 The parity check, and the one thing §4.6 overstated

**Run `20261014-205622-p18428`, 8803 recorded frames, 156 s, `-Experiment Hold -HoldDegrees 90`,
68 resolved collisions.** This is the run §4.6 asked for: its prediction was that with
`TrajectorySampler.OnFrame` fixed, the live observer's false-negative count would stop disagreeing
with the harness's.

**It does. On all 68 resolved collisions the two now say the same thing — 68 of 68, no
disagreements in either direction.** §4.6's fix is confirmed, and the confirmable part of its
prediction held exactly.

**What did not hold is the number, and the difference matters.** §4.6 predicted "11 becomes 2". On
this run both counters say **15**: the live observer's flag and the harness's re-derivation agree
that the model never predicted 15 of 68 collisions, so **T7 still fails**. The prediction confused
two different claims. The fix was about *agreement* — the two implementations reading the same
geometry the same way — and agreement is now total. The *count* is a property of the recording, not
of the fix: `p4508` saw 18 tracked collisions in 233 s, this run saw 68 in 156 s, and the harness's
own prediction code is identical in both. A run with more bullets arriving will have more misses no
matter how well the two observers agree.

#### The correction to §4.6: the disagreement was 12, not 9

§4.6 quotes the live observer's count as **11**. Against the artifact it names, the final count is
**14** — `bootstrap.log`'s own last `PHASE 2 NUMBERS` says `false negatives: 14 (hit, not
predicted)`, and its 18 `collision:` lines read 4 `predicted=yes` and 14 `predicted=NO`. The "11" is
one of the running checkpoints: the counter reads 11 from read `n=1501` to `n=2701`, and then
climbs to 14 by `n=4501` as three more hits arrive. **A live counter read mid-session is a snapshot
of a session that has not finished, and every one of those checkpoint lines was in the log.** So
the disagreement §4.6 is built on is **12 misses against 2**, not 9 against 2 — which strengthens
its conclusion rather than weakening it, and it is the same species of error §4.6 itself is about:
the number was in the file, and the report was written from an earlier reading of it.

#### The guard, so it cannot drift silently again

`SolverHarness` now compares, per resolved collision, the live observer's own `predicted=` flag
(read off the `collision:` line) against its own re-derivation, and **fails** on any disagreement —
invariant `T7P`. On `p4508` it fires exactly as it should, which is what makes it worth having:

| run | collisions | harness false negatives | live false negatives | T7P |
|---|---|---|---|---|
| `20261014-193728-p4508` (before the fix) | 18 | 2 | 14 | **FAIL — 12 differ, all "the live observer missed it"** |
| `20261014-205622-p18428` (after) | 68 | 15 | 15 | **PASS — 68 of 68 agree** |

The `p4508` column is the point: the instrument reproduces the defect it was written for, in the
right direction, on the run that contained it. A guard that has never failed is a guard nobody has
tested.

**One honest qualification, which the report carries rather than hides.** Of the 68 agreements, **11
are collisions whose bullet the recorder never wrote down at all.** The recorder culls with
`TraceRecorder.CouldReach`, which applies the model's own disc test to the model's own samples, so
"no path recorded" is implicit evidence that the model never put that bullet in the disc — the same
answer, arrived at one step removed. On those 11 the harness is not a second reading of the
geometry; the live observer's flag is the only direct witness, and the cull is the corroboration.
`T7P` counts them separately for exactly that reason. All 11 are also T7 false negatives, and T7's
count stands on the live flag for them.

#### What the 15 misses look like, and what is still open

Four of the 15 are bullets the trace *did* record, and they are the same class as §4.6's two:
samples that grazed the disc and stopped a few hundredths of a unit outside it (closest recorded
approach 0.46 against r_eff 0.45, 0.61 against 0.55, 0.65 against 0.55, 0.61 against 0.55). They are
sub-sample boundary misses — the model's sampled path misses a disc the game's own frame-boundary
test enters.

The other 11 are the ones with no recorded path, and they are a genuinely open question, because the
cull's own comment says it *cannot* drop a bullet whose sampled path crosses the player: "this is a
bounding-box test against the very samples the solver would have used." Two things are true of them
and only one is measured. Measured: they are absent from the trace entirely — not under a different
key, not under `GameId` 0 (the §3.9 defect is not back) — 0 occurrences of each `GameId` in 11.7 MB
of JSON. Measured: the cull also keeps any bullet whose *current* position is within `r + 2.0`, so a
frame where such a bullet was even two units from the player would have recorded it. Open: how a
bullet reaches a resolved hit without ever being recorded at either test. A frame-boundary crossing
by a very fast bullet is the shape that fits, and so is a projectile that spawns already inside the
disc and is removed before the recorder next runs; **the trace cannot tell the two apart, and no
instrument in this spike currently can.** It is item 4 of §6's work list, and it is worth a session
because 11 of the run's 15 misses live there.

---

## 5. The configuration surface, as it stands

`DodgeConfig.Default()` holds every number, in one place, with the specification's own starting
values. `DodgeConfig.FromEnvironment()` reads plain `DRELAY_` names for the hook (the launcher
strips every inherited `DRELAY_HOOK_*`, so the plain namespace is the one that works —
[PHASE1-HANDOFF.md](PHASE1-HANDOFF.md) §4 trap 1). The harness sets the same fields from
`--config`.

Three knobs are worth knowing about because they are the ones that are not the specification's:

- `DRELAY_DODGE_TRUST_S` (**0.65**) — the trust prefix, §3.6.
- `DRELAY_DODGE_IMPACT_WINDOW_S` (**0.5**) — "arriving nearly now" versus "arriving later", §3.5.
- `DRELAY_DODGE_WARNING_PAD` (**0.15**) — the warning corridor's width beyond the collision radius,
  §3.3.

**§11 added four more, and they are the plan model's geometry and its one new preference:**

- `DRELAY_DODGE_PLAN_LOOKAHEAD_S` (**0.5**) — how long the plan is walked against the bullets. This
  is the number that replaced "distance to the bullet's whole remaining polyline".
- `DRELAY_DODGE_GOAL_LOOKAHEAD_S` (**0.3**) — how far in front of the character the goal point sits,
  in seconds of travel.
- `DRELAY_DODGE_GOAL_FAN` (**1**) — the `±j` ring steps around the goal heading that get their own
  candidate, so the dodge can leave the goal line by the smallest amount that clears an obstacle
  rather than snapping to a 15° heading.
- `DRELAY_DODGE_W_GOAL` (**0.9**) — the weight on "make progress toward the point in front of you".
  **This is the one to move if the dodge feels like it is fighting you**; §11.7 is the trade and
  `--preset assist` is the position at the other end of it.

The dodge itself is **now wired to the actuator**, as a dry run by default and behind two switches
that must both be set before anything moves. §9 is the report; the one-line version is that
`SteeringPilot` learned a `dodge` mode in which the heading it writes is `DodgeAdapter`'s decision,
and a frame the adapter publishes no command is a frame the pilot does not write at all.

Deliverable 3.4 — the `World.RequestEscape()` call — is **still not done**, and §9.3 says why the
adapter deliberately does not make it.

---

## 6. The next step: one recorded session

**It is one command and it is the only thing standing between "the solver is written" and "the
solver agrees with the game".**

```powershell
cd G:\coding\javamapp\autododgeresearch

# The operator's own shell. The harness's write confinement stops the game reaching the title
# screen (PHASE3-HANDOFF.md section 7), so a harness-launched run needs -RunRoot in the repository.
.\hook\tools\Run-Phase3Session.ps1 -Experiment Hold -Seconds 300 -Trace

# Then, with the game closed:
.\hook\tools\SolverHarness\bin\Release\SolverHarness.exe `
    --trace <run-dir>\trace.jsonl --collisions <run-dir>\bootstrap.log
```

**Choose the experiment by what you want the frames to contain.** `Hold` walks in one direction and
is the shape the solver's output takes; `Sweep` walks eight directions and covers more ground. For
T7 what matters is **being shot at**: a session with bullets arriving from several angles and a few
hundred resolved `collision:` lines is worth more than an hour of walking. A recording with no
collisions is a valid file that proves nothing, and the harness says so rather than reporting a
pass.

**What the run answers, in order of value:**

1. **T7** — do the model's predicted first-impact times agree with the collisions the game actually
   resolved? **Answered on three recordings, and the answer is not one number.** §4.3's first session
   passed outright (9 of 9 collisions predicted, 0 false negatives); `p4508` (§4.6) and `p18428`
   (§4.7) both **fail** it, at 2 of 18 and 15 of 68. What is established is that the harness and the
   live observer now read the same geometry — 68 of 68 agreed on `p18428`, asserted as `T7P` — and
   what is not is the miss rate, which is the residual to work on rather than a bookkeeping question.
2. **The real candidate count and `decide()`'s cost against it** — **answered, and it is in budget**
   (§4.4): p99 **145 µs** over the 173 frames that evaluate, p50 42 µs, max 448 µs on the three
   densest frames. The 890 µs this item used to quote was the harness measuring its own garbage
   collector. The per-frame threat envelope that produced the real number is in the code; the
   closing-speed cull and the `Projectable` flag are still unused.
3. **Whether the terrain rules fire** — the trace carries the collision grid window, and the
   recordings show 44–47 solid cells in the first 48×48 window, with `NoAdmissible` frames where the
   player was pinned.

**The next pieces of work, in order:**

1. **~~Bound the trust prefix by the bullet's own end-of-shot~~ — already implemented, and it never
   fires on this data** (§4.4). `Trajectory.TrustedCount` has taken `min(byTime, byAlive)` all along,
   and not one of the session's 1011 threat records contains an `Alive[s] == false` sample. What is
   left of the idea is the other end of it: the **650 ms trust prefix outliving the bullet**, which is
   the same gap with a different bound.
2. **Cull the threat set before it becomes candidates** — by closing speed, and by the
   `Projectable` flag the sampler already computes and the solver ignores. *Partially done:* the
   threat's trust prefix and sample box are now computed once per frame rather than once per candidate
   (74 × 13 × 21 comparisons a frame, removed), which is what brought the decision inside the budget.
   Closing speed and `Projectable` are still unused.
3. **Decompose the residual 26.** That is what a third recording buys.
4. **Find out how 11 bullets reached a hit without ever being recorded** (§4.7). It is the largest
   single class of miss in the newest recording, and it is the one place where the harness has a
   negative rather than a measurement: the recorder's cull is the model's own predicate, so the
   absence means the model never put those bullets in the disc at any frame boundary — and the cull's
   comment claims that cannot happen to a bullet whose sampled path crosses the player. Either it
   can, or those paths never crossed and the disc is being missed between boundaries. The recorder
   cannot tell the two apart today; an instrument that logs a `HitCharacter` for a bullet the
   recorder has never written, with that bullet's last known position and age, would.

**§11 MOVED THIS LIST, AND IT IS THE FIRST ITEM THAT CHANGED SHAPE.** The thing this section's
number 1 used to be about — the dodge flinching at a bullet's entire path — is what §11 removed, and
the residual is now a *preference* question rather than a geometry one: `keeps its course` against
`overrides intent` (§11.7), which only a live session can settle. The T7 residual (items 1 and 4 here)
is untouched by §11 and still stands.

**Two things to check before the run is worth analysing**, both of which cost a session to learn:

```powershell
(Get-Item <run-dir>\trace.jsonl).Length                                          # a few hundred bytes = empty (§3.8)
Select-String -Path <run-dir>\trace.jsonl -Pattern '"id":0,' | Measure-Object    # any hit = §3.9 is back
```

Both are now impossible from the code, and both were live defects within one session of each other.
**Check the artefact before trusting the report.**

The run leaves a `crash.txt` when the operator closes the game. That is expected and is not a
result — [PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) §3.6.

---

## 7. What this run does **not** do

- **It does not move the player unless it is asked to, twice.** `--dodge` runs the decision and logs
  it; `--dodge-act` additionally sets `DRELAY_DODGE_ACT=1`, patches the movement replacement and runs
  the pilot in `dodge` mode. §9.1 is the switch surface.
- **It does not call `World.RequestEscape()`.** `CountUnavoidable` produces the projection that gate
  6 needs and the call itself is Deliverable 3.4. The decision can return `Escape`; nothing acts on
  it, and because the adapter reads no damage, with the live snapshot it cannot even be returned —
  §9.3.
- **It does not populate rule 7's enemy bodies.** No accessor for the live entity list has been
  resolved, so `DodgeSnapshot.Bodies` is empty and the rule is vacuous. The adapter says so.
- **It does not close the cost question.** The decision has now been timed against recorded play
  (§4.4), but the recorded threat set is the *culled* one — 0.8 nearby threats per frame on average
  and 13 at the worst — while the sensor tracked up to 142 live bullets in the same session. The cost
  of `Decide` against a large *nearby* set is still unmeasured, and the `--repeat` mode is the
  instrument for it.
- **It does not validate the rotation term.** [PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) §3.2 is still
  open: `World.WorldRotation` was 0.0° on every logged frame of the sweep, so the world-to-input
  conversion remains a resolved formula with an untested correction.

---

## 8. Where every number in this document came from

| claim | source |
|---|---|
| the six-scenario table, 900 frames each | `SolverHarness.exe --scenario <name> --frames 900` |
| `dodge`: 52 of 900 frames moved (5.8 %) | same |
| the 900-frame trace is 265 010 bytes | `SolverHarness.exe --scenario dodge --frames 900 --export <path>` |
| the eight defects and their symptoms | the harness's own failure output, plus the live run of `20261014-164314-p12060`, recorded in §3 as they were found |
| `Add(1, ..., 0.0f)` returning early | `DodgeSolver.Add`'s `distance <= 0.0f` guard, and the `--frame` dump showing `kind0=0` |
| the detection/collision radius conflation | `--frame 58` of `headon`, showing `admissible=0` with `stand` durability false |
| the trust-prefix boundary | `--frame 58` of `headon`, `minD2=0.242` over 16 trusted samples against a true 0.2025 over 21 |
| the emitted-but-refused moves | the T6 assertion's `frame 56: emitted a candidate the solver itself refused (rule TemporallyClear, reason Desperation, kind 2)` |
| the empty trace, and the native frame path | `20261014-164314-p12060`: `trace.jsonl` 355 bytes, `hook.log`'s `the frame read is native and needs no managed entry point` and no `MANAGED REPLACEMENT entered` line in `bootstrap.log` |
| the live sensor's 12–28 bullets per frame | [PHASE3-SCOPE.md](PHASE3-SCOPE.md) §1, from the Phase 2 live session |
| the resolved speed law and the per-frame step | [PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) §1.4 |
| the collision grid's indexing convention | the IL dump, `World::'hehe...'(float, float)` → `conv.i4` → the same method's int overload |
| §4.4's `decide()` p99 145 µs / p50 42 µs over 173 frames | `SolverHarness.exe --trace 20261014-170146-p30952\trace.jsonl --collisions <same run>\bootstrap.log --repeat 200`, four runs, 139–223 µs p99 and 42–43 µs p50 |
| §4.4's 890 → 516 µs under server GC | the same command with `--repeat 1`, and with `DOTNET_gcServer=1` |
| §4.4's "not one of 1011 threat records has `Alive[s] == false`" | a regex over the raw `trace.jsonl`, counting `"alive":[...]` arrays containing a `0` — 0 of 1011 |
| §4.4's threat population, 0.8 mean and 13 max per frame | `"id":` occurrences per `{"t":"F"...}` line: 895 frames with none, one frame with 13 |
| §4.4's 173 evaluating frames of 1229 | the harness's own `decide() evaluating ... over N of M frame(s)` line |
| §4.4's "the time bound measured slower" | the bound was implemented, built and run at `--repeat 200`: p99 350–434 µs with it, 145–221 µs without, then reverted |
| §4.7's `p18428` numbers (8803 frames, 156 s, 68 collisions, 53 predicted, 15 not) | `SolverHarness.exe --trace 20261014-205622-p18428\trace.jsonl --collisions <same run>\bootstrap.log` |
| §4.7's `p4508` parity column (18 collisions, 2 harness / 14 live missed, 12 differ) | the same command against `20261014-193728-p4508` |
| §4.7's "the live counter reads 11 from `n=1501` to `n=2701`, then 14 by `n=4501`" | `Select-String -Path 20261014-193728-p4508\bootstrap.log -Pattern 'false negatives: (\d+)'`, unique on `read n=` |
| §4.7's "0 occurrences of each `GameId` in 11.7 MB of JSON" | a regex over the raw `trace.jsonl` for `"id":<n>,"st":<m>` for each of the 15 misses' keys: 4 present, 11 absent |
| §4.7's "on 11 of the 68 agreements the recorder wrote no path" | `T7P`'s own unwitnessed counter, which is `!_firstFrameSeen.ContainsKey(key)` on an agreeing live-`NO` collision |
| §4.7's "all recorded collisions are within 0.42–0.99 of the player at the collision" | each `collision:` line's `bullet=(x,y)` matched to the nearest recorded sample for that key, against that frame's `px`/`py` |
| §4.7's "the recorder's cap of 48 threats a frame never binds" | the most threats written on any frame of `p18428` is 24 (`"id":` count per `{"t":"F"...}` line); `TraceRecorder.MaxThreatsPerFrame` is 48 |

---

## 10. The first live dodge, and the question "should this bullet be dodged at all"

**Run `20261015-021911-p20152`, 6267 frames, 110 s, `-Experiment Dodge`, the solver steering.**
This is the first session in which the decision and the game shared a frame, and it answers §9's
open question. It also produced a better solver, because it produced the first number that could say
what the dodge was doing wrong.

```
-- decisions, before and after the work in this section, same recording -------------
                            2026-10-15 (first live)   after
  NoAdmissible              1733  (27.7 % of frames)  729  (11.6 %)
  mean admissible of 74      19.3                      34.7
  frames that moved           349                      1353
  hysteresis holds           31 % of moving frames     58 %
  decide() p99 (evaluating)  548 us                    155 us
  T5 / T6 / T10              pass                      pass
  the six scenarios          0/0/52/0/0/7 moves        0/6/54/0/2/7, every invariant holds
```

### 10.1 The question the admission rules cannot ask

> *"Identify projectiles that should be dodgeable (i.e didn't spawn inside character or moving too
> fast)."* — the operator, after the first live run

**Every threat was fed to the same three hard rules, and there was no earlier stage that asked
whether there was any point.** A bullet already touching the player and a bullet arriving faster than
the player can move both refused *every* candidate on the frame -- including the candidates that were
busy avoiding a different, perfectly dodgeable bullet -- and the solver reported `NoAdmissible` and
did nothing. That was **27.7 % of the first live session**.

`DodgeSolver.ThreatVerdict` is the stage that asks. It runs **once per frame, before the candidate
ring exists**, and it is deliberately about *feasibility* and not about danger:

| verdict | the test | what the rules do with it |
|---|---|---|
| `Clear` | the path never enters the player's disc | unchanged -- a candidate can still walk *into* it, so every rule still applies |
| `Dodgeable` | arrives, and the player can be clear in time | the full hard rules; this is what they were written for |
| `Inside` | the disc contains the player's centre now | one-sided: refuse only a move that does not increase the distance |
| `TooFast` | `t_impact < (collisionR - current_miss) / playerSpeed` | one-sided: it forbids walking deeper, and refuses nothing else |

**One-sided is not the same as absent**, and the distinction is what keeps T6 passing: a threat that
cannot be avoided still refuses a candidate that ends up closer to it. What it no longer does is
refuse the candidate that is escaping, or refuse the whole frame on behalf of a bullet no move can
answer.

**What it found on the live session, which is the answer to the operator's question:**

```
DODGEABLE        1705 of 27022 threat records ( 6.3 %)   -- arrived, and could be answered
TOO FAST           37 of 27022                  ( 0.1 %)   -- faster than the player can clear
ALREADY TOUCHING 3005 of 27022                  (11.1 %)   -- the disc contained the player
CLEAR           22275 of 27022                  (82.4 %)   -- never came near

frames with a threat that mattered : 2082
  of those, 1273 had one the dodge could ANSWER, and 809 (38.9 %) had nothing it could do
```

**Nearly forty per cent of the frames where something mattered, nothing could be done** -- the player
was already being touched, or the bullet was faster than the dodge. Before this, the solver spent all
of them evaluating 74 candidates against a threat it could not beat and then reporting that the world
was closed.

### 10.2 Defect 3.12, which T6 found and which had been dead code since it was written

```csharp
if (InsideCircle(0.0f, 0.0f, collisionR2, cx, cy))     // rule 4, the arrival-inside-disc test
```

`InsideCircle(cx0, cy0, r2, x, y)` computes `(x-cx0)² + (y-cy0)² < r2`. With `(0, 0)` as the centre
this tests **the candidate's distance from the WORLD ORIGIN** against a 0.41-unit radius, on a map
whose coordinates are in the thousands. **The test could never be true, so rule 4 was dead code.**

It was harmless for as long as it existed, and that is the finding: rule 5 refuses a candidate any of
whose swept points is inside the same disc, and the arrival point *is* the last swept point -- so
rule 5 silently did rule 4's job for every player who was not already inside a disc. It stopped being
harmless the moment §10.1 extended the already-inside exception to rule 6: that left an
already-inside threat with **no** rule checking where the move ended, and the measurement was
**585 emitted moves finishing inside a disc the player was not in, and 585 moves that had not even
improved the distance to a disc they were already in.** T6 is what found it, in the first run after
the change.

### 10.3 Two scoring terms that were constants, and the dribble they caused

The live dump of frame 332 is the receipt. One bullet, 7.5 units away, predicted to close to 0.401
against an `r_eff` of 0.41 at the end of the horizon -- a real threat, 600 ms out. The solver's answer
was `Move / OverrideUnsafe`, **scale 0.35, heading 75°, clearance 7.16, durability 570 ms** -- and it
said the same thing on the next five frames.

- **`clearance` measured the distance to where the bullet IS.** `ClearanceAt` took `Xs[0], Ys[0]` --
  the bullet's position at this instant -- so for a bullet flying at the player the number is LARGEST
  when the bullet is still far away. Every candidate scored `7.16`, the cap at `ClearComfort` made
  them all `1.0`, and the term was a constant on exactly the frames the dodge exists for. It is now
  the distance from the candidate's arrival point to the bullet's **predicted path**, which is what
  "am I out of the way when this arrives" means and which has a gradient pointing away from the line.
- **`durability` divided by 50 ms.** `Normalize` used `MoveTemporalWindowSeconds`, so anything clear
  for more than one frame scored 1.0 -- the second-heaviest weight in §5.6 ranked nothing. It now
  divides by `TrustSeconds`, the horizon the solver actually believes, so 650 ms scores 1 and 130 ms
  scores 0.2. §4.2 measured this and called it a tuning question; it was a units bug.
- **and so the winner was decided by `WeightMove`, which is a penalty on DISTANCE.** The smallest step
  always won. Six frames of `scale 0.35` covers the ground of one frame of `scale 1.00`, taken six
  times slower, while the bullet closes.

`WeightResolve` is the term that was missing: **the fraction of this frame's relevant threats whose
corridor the candidate's arrival point is actually outside.** Clearance says how far the nearest
corridor is; resolve says how many of the corridors aimed at the player were escaped. They come apart
on the frames that matter, and the second one is what "dodged" means. The weights were re-tuned
against this recording, and every change is a consequence of the three findings above.

**And the clearance clamp had to go with them.** `if (clearance < 0) clearance = 0;` was justified by
"an admissible candidate that is inside a disc cannot exist" -- true when rules 4 and 5 refused every
such candidate, false the moment the already-inside exception was applied to all three. On the frames
the dodge exists for, the only term that could tell "toward the bullet" from "away" was going flat.

### 10.4 What is still crude, and it is measured rather than felt

**The dodge still reverses its heading too often: 389 of 1353 moving frames (29 %) turn more than 90
degrees on the very next frame, mean 134 degrees.** The reversal examples show a period of exactly
eight frames in one burst, and the frame dump says why: in the regime that dominates the session --
the player standing inside a cluster of bullets -- **every** candidate has `resolve = 0` (nothing
clears a corridor you are already inside), the clearance term is negative and nearly flat across the
ring (`-0.201` to `-0.257`), and the top six candidates are separated by `0.026`.

**Strengthening the hysteresis was tried and measured and did not help.** Margin 0.45 and dwell
0.25 s took adjacent-frame reversals from 384 to **419** and the mean turn from 133.5 to 137.8,
while `Hysteresis` holds rose from 789 frames to 976 and `OverrideUnsafe` fell from 511 to 324. The
hold does suppress fresh choices; the reversals went up anyway, which says the flips are not "a new
candidate narrowly won" -- they are **the hold itself being reinstated from a heading older than the
last one commanded**, because §5.7's hysteresis assumes the held heading was the last emitted one and
the `stand` and intent early-outs return before the hold is consulted. **Reverted rather than
shipped**, and recorded here because an unsupported change to a damping term would have made the next
reader's job harder. The knob is not the fix; the state machine is.

**The instrument for that work now exists:** `--frame N` prints the top of the candidate menu **by
score** (six rows with `resolve`, `clearance`, `durability` and `score`), which is what turned "it
flipped again" into the two paragraphs above. The list used to be in *candidate* order, so it showed
`stand` and the first few ring headings and said nothing about a winner forty entries down.

### 10.5 What the six scenarios say

| scenario | moves | verdict |
|---|---|---|
| `peaceful` | 0 | all invariants held |
| `headon` | 6 (was 0) | all invariants held |
| `dodge` | 54 (was 52) | all invariants held; the move assertion passes |
| `volley` | 0 | all invariants held |
| `corridor` | 2 (was 0) | all invariants held |
| `passingby` | 7 (was 7) | all invariants held |

`headon` and `corridor` moving is the classification and the rule-4 fix showing up where the synthetic
scenarios can see them; `dodge` moving slightly more is the resolve term. **Every invariant holds on
every scenario, and T5, T6 and T10 hold on the live recording** -- the three that a solver change of
this size is most likely to break. T7 still fails at 4 of 21 collisions, unchanged, and that is §6's
residual and not this section's.


---

## 9. Deliverable 3.3 — the dodge, on live frames

**Written 2026-10-15.** §1's table said stage 5 was "not done", and `PHASE3-HANDOFF.md` §3.4
recorded the consequence in one sentence: **the sensor and the solver have never shared a frame.**
Every decision this document reports was made either by a console program a session later or by a
synthetic scenario that never had a game attached. This section reports the file that closes it.

**What it is, in one line:** [`DodgeAdapter.cs`](hook/managed/Drelay.Bootstrap/DodgeAdapter.cs)
reads the live world at the frame tick, fills the plain-data snapshot `DodgeSolver.Decide` was built
to consume, decides, and then either **logs what it would have done** or **publishes a world heading
and a magnitude** into the slot the pilot writes from.

### 9.1 The switch surface, and why it takes three of them

```powershell
cd G:\coding\javamapp\autododgeresearch

# THE DRY RUN.  The solver decides on every frame and logs every move it would have made.
# Nothing is written to the input.  This is the paired-trial evidence the plan asks for,
# and it cannot move anybody.
.\hook\tools\Run-Phase3Session.ps1 -Experiment Hold -Seconds 180 -Dodge

# THE LIVE DODGE.  The solver steers.  Somewhere it does not matter.
.\hook\tools\Run-Phase3Session.ps1 -Experiment Dodge -Seconds 180 -RelayLog <relay-session.log>
```

| switch | sets | effect |
|---|---|---|
| `--dodge` | `DRELAY_DODGE=1`, plus the frame replacement and the frame sensor | the decision runs and is logged; **the input is never written** |
| `--dodge-act` | the above, plus `DRELAY_DODGE_ACT=1`, `DRELAY_HOOK_MOVE_REPLACEMENT=1`, `DRELAY_MOVE_STEER=dodge` | the decision reaches the actuator |
| `--move-steer dodge` | `DRELAY_MOVE_STEER` alone | the pilot consumes the slot, but a slot nothing publishes is a frame it does not write |

**THREE SWITCHES IS NOT OVERKILL; IT IS THE ONLY SHAPE THAT MAKES THE DEFAULT SAFE.** The pilot in
`dodge` mode writes nothing on any frame the adapter publishes no command, so a pilot left in `dodge`
mode with no adapter is a pilot that does nothing at all. And the adapter with `DRELAY_DODGE_ACT`
unset never publishes, so `--dodge` on its own is a decision stream and nothing else. The two failure
directions are therefore both quiet, and both require a switch that says what it wants.
`--dodge-act` and `--move-steer` are **mutually exclusive and the launcher refuses the
combination**, because preferring one silently is a run that walks where the operator did not ask.

**The dependencies are set by the launcher rather than left to the operator**, for the reason
`--trace` gives: the adapter runs on the sensor's frame path and its input is the sensor's projectile
list, so `DRELAY_DODGE=1` with the sensor off is a solver that decides about an empty world, reports
`NoThreat` on every frame, and looks exactly like a session in which no bullet came near. That is
defect §3.8's shape, one subsystem over.

### 9.2 What the adapter has to get right, and the choices it made

- **The threat keys are the sensor's.** `Trajectory.Key` is written from `ThreatKey.Packed` and from
  nothing else — defect §3.9, made impossible to repeat rather than documented.
- **The cull is the recorder's own predicate**, deliberately duplicated rather than shared. If the
  live decision and the recorded trace ever disagree about which bullets matter, that disagreement is
  the finding, and §4.6 is the session that says so.
- **The intent is converted to a world heading and the offline harness does not convert it.**
  `World.cs:12692` computes `angle = MoveAngle + (−WorldRotation + aimOffset)`, so `MoveAngle` is an
  input-frame number while the solver's whole geometry is world positions.
  `SolverHarness.TraceFile` reads `intent` raw and carries `Rotation` without ever using it, so on a
  session with a rotating camera — which is every T7 session since `p4508` — the harness's
  "is the player's own steering already safe" test is comparing two different frames. **That is a
  live correction to an offline inaccuracy, and it is recorded rather than applied to the harness**:
  the harness's numbers are this document's published record, and a correction that moved them would
  need its own run to be evidence of anything.
- **The speed is measured, not re-derived from the law.** `PlayerFunctions.TilesPerSecond` takes
  eight arguments, four of them `HasEffect` lookups on obfuscated constants, and a wrong argument
  produces a *plausible* speed — the worst kind of wrong. The displacement the client itself produced
  between two frame ticks, divided by the frame's own delta, is a direct measurement of the quantity
  `b = speed · frameSeconds` is made of, and it is what `SolverHarness.ResolveSpeed` already uses.
  The resolved law at 60 Hz (8.2 u/s) is the prior for frames where nothing moved, which includes the
  frames where the player is pinned — and for those the *maximum* budget is the correct one to test
  admission against. The pilot reports the magnitude it actually wrote, so a half-speed dodge does not
  halve its own budget every frame and collapse.
- **`stand` is a write, not an omission.** Section 5.4's preferred answer means *do not move*, so the
  pilot clears the input. Every other `None` — `NoThreat`, `NoAdmissible`, `Immobile`, `Degraded` —
  means the player's own steering is untouched, per §5.7 and defect §3.7. Conflating the two is a
  dodge that either refuses to stand or refuses to yield, and both look like working code.

### 9.3 Precedence, and the two things this deliberately does not do

**The rule from `IMPLEMENTATION-PLAN.md` is implemented, and it is a totem check rather than a cast
check.** `World.RequestEscape()`'s channelled branch — reached only with the `Impermanence` totem —
starts a cast that the client cancels on movement (`World.cs:13043-13047`), so a dodge that steers
can cancel a run-saving escape. The cast flag itself is an obfuscated private bool on `World` and is
not readable; the *condition that reaches the cast branch* is (`Player.Totems.Has(Impermanence)`, the
client's own test). **While that totem is equipped the live dodge is disabled**, the decision still
runs and is still logged, and the log says so once. That is strictly safer than yielding for the
duration of a cast, because it cannot be wrong about when the cast started. Its cost is named rather
than hidden: **no live dodging in a challenge run**, which is a real limitation.

**`World.RequestEscape()` is not called, and the adapter reads no damage, so the gate cannot even
fire.** `Trajectory.Damage`, `.DamagePercent` and `.ManaDamage` are left at zero; gate 6 tests
`ProjectedUnavoidableDamage > Health · LethalFraction`, which is `0 > h` and false. Deliverable 3.4
is that call and it is not this one — a live run that started casting nexus on its own would be a far
worse surprise than a gate that does not fire. `UnavoidableThreats` is computed and logged, so the
gate's *input* is visible even though its output cannot be reached.

**Rule 7 is vacuous.** No accessor for the live entity list has been resolved, so
`DodgeSnapshot.Bodies` is empty and the report's `bodies=0` is the mark of it.

### 9.4 What is verified, and what is not

**Verified, offline and reproducible:**

- the managed and launcher halves build clean (`hook\build.ps1 -Managed`), with `TreatWarningsAsErrors`
  on;
- **the six synthetic scenarios report the same move counts as §4.2 to the digit** — `dodge` 52 of
  900, `passingby` 7 of 900, the other four 0 — and every invariant still holds. Nothing in this
  deliverable touched `DodgeSolver`, and this is the check that says so;
- the launcher's `--dodge`/`--dodge-act`/`--move-steer` surface parses, prints, and **refuses the
  contradiction** with a named error and exit 2;
- against the synthetic host, the adapter **resolves, arms and reports its own configuration** —
  `dodge ARMED: mode=DRY RUN | ...`, the config line, and the degraded paths saying so out loud
  (`grid MISSING (walls ignored!)`, `intent MISSING (0.0)`);
- and §3.11, which is the thing the probe was for.

**Not verified, and it is the whole point of the deliverable:** that the adapter decides on live
frames and that the pilot acts on its answer. **That needs a run with the game attached**, and it is
the user's to make.

**And the offline probe is currently INCONCLUSIVE on this machine, for a reason that is not the
adapter.** `hook\tools\Probe-DodgeDryRun.ps1` drives the whole chain against the synthetic host and
exits 3 with a named reason: **the host's frame tick is never matched and patched.**

```
[WARN ] IL size 23 matches target 'frameTick' but the hash does not -- a same-sized neighbour,
        or a changed method
```

The evidence, so the next reader does not have to re-derive it:

| observation | value |
|---|---|
| `il_method_hash.py` on `SyntheticHostFixture.Cadence::FrameTick` | RVA 0x24DC, tiny header, 23 bytes, `84FE237D…` |
| what the native side hashed at run time (2 distinct size-23 methods, 4 runs) | `FBA04F4E…`, `14DA9655…` |
| is either observed digest any 23-byte window anywhere in the host DLL? | **no** — brute-forced over all 14 825 offsets |
| the host's own call count | `cadence ran 1455 call(s)` |
| `hook.log` | no `PATCHED 'frameTick'` line, in any of the four runs |

So a 23-byte method is compiled and hashed, its digest is not in the assembly file, the method the
tool names is never patched, and the probe therefore never reaches `DodgeAdapter.OnFrame` for a
single real frame. The adapter is called exactly once — by the resolve-time warm-up call
`PublishFrameReplacement` makes — and that call runs before the host has a world, so it correctly
counts one frame with no world.

**This is a test-target problem and not a dodge problem**, it is the subject of
`Test-Injection.ps1` runs 7 and 8, and it is **open**. The probe's job is to say which of the two it
is hitting; it does, in its own words, and it exits 3 rather than reporting a row of adapter
failures that were never given a chance to happen.

### 9.5 The reading of the first live session, when it comes

```powershell
# The dry run first.  Get shot at.  Then, with the game closed:
Select-String -Path <run>\bootstrap.log -Pattern 'dodge |PHASE 3 DODGE'
```

Five things to read off it, in order:

1. `dodge ARMED: mode=DRY RUN | ... grid World.byte[,] | ...` — **if `grid` says MISSING, stop**:
   admission rules 1–3 are vacuous and every candidate is admitted through walls, so the run's
   `moved` count is meaningless.
2. `dodge: first decision on frame N: <reason> over <n> threat(s) ...` — the positive witness. Its
   absence with `dodge ARMED` present means the frame path never reached the adapter.
3. `PHASE 3 DODGE NUMBERS | mode DRY RUN | frames N ...` — **`frames 0` is a failure**, and it is
   §3.8's lesson applied to this file. The line arrives at 300 frames and every 900 after, so a short
   session still gets one.
4. `dodge WOULD MOVE | frame N | heading … scale … | <reason> …` — one per move, at most 400, and the
   only thing that can be lined up against a `HealthUpdate` trace and a death. `Reason` is the field
   to read: `StandSafe` frames are the specification's preferred answer being taken,
   `NoAdmissible` frames are where the escape gate will fire, and `OverrideUnsafe` frames are the
   dodge taking the wheel.
5. `PHASE 3 DODGE DETAIL | ... precedence normal (instant escape) | reasons ...` — the candidate
   count against the live threat set (the number §6 item 2 has never had), and the precedence answer.

**Both log lines are bounded to stay inside the native buffer.** `DrelayLog.cpp` formats with
`_vsnprintf_s(..., _TRUNCATE, ...)` into 256 bytes and **a truncated line carries no mark**, so the
report is two lines and the longest dodge line measured in the probe run was 193 characters. The
first version of the report was one line of about 400 — the reasons histogram and the cost
percentiles would have been silently cut off on every run, which is this document's recurring failure
mode wearing one more hat.


---

## 11. The forward plan — two bodies, one clock

**Written 2026-10-15, after §10 and against the three live recordings.** §10's dodge worked and felt
wrong, and the operator named why in one sentence:

> *"It considers a projectile's entire path dangerous, when the only dangerous part is the projectile
> (and since the character has to move out of the way, a part of the projectile's path needs to be
> considered dangerous). The dodger would feel better if it considered which direction the character
> is moving. It could do this creating an imaginary point in front of the character and trying to
> pathfind to it while avoiding any projectiles in the way. The hook moves the character independent
> of camera rotation, but the player might be angled say 13.7 degrees so hook needs to be able to
> handle that."*

That is the specification for this section, and it is three changes and one correction: the **plan
model** (§11.1–11.2), the **forward goal** (§11.2), and the **harness's intent conversion** (§11.4),
which the goal made load-bearing. §11.3 is a defect found while trying to measure whether any of it
mattered.

### 11.1 The defect, stated exactly

A candidate was **a point**, and its danger was the distance from that point to the bullet's whole
predicted polyline:

- rule 6 compared the candidate's one-frame swept segment against every bullet's path segment with
  matching times, then measured the candidate's **arrival point** against the rest of the polyline;
- `clearance` was that same distance, minus the collision radius;
- `resolve` was the fraction of "threats whose path enters my disc" for which the arrival point was
  outside the polyline.

**A polyline has no clock in it.** For a player who is standing still, distance-to-path and
distance-to-body-at-the-same-time happen to agree, because the bullet walks its path monotonically.
For a player who is *moving* they do not: the arrival point is where the character is for one frame,
and the rest of the polyline describes where the bullet will be over the next six hundred
milliseconds — most of which the character will have spent somewhere else. So every candidate was
scored as though it were a wall the character would lean against, and the dodge flinched at bullets
it had already passed and at bullets it was walking away from.

**The measured signature is a collapsed term.** On `20261015-032549-p26684` — the third and longest
live dodge — the winner's `chosen durability` was **p50 17 ms**. A score whose second-heaviest term
reads one frame on half its moving frames is not ranking anything; the winner was decided by
`heading` and the move penalty, which is what §10.4 called "the state machine" and what §10.3 had
already found once.

### 11.2 The plan model

**A candidate is no longer a point. It is a direction the character walks in, for a lookahead.** The
question is *where will the bullet's body be when my body is there*, and the answer is computed by
subtracting the two motions:

```
player(t)   = P + v * t * (cos h, sin h)          v = budget / frameMs, times the candidate's radius
relative(t) = bullet(t) - player(t)               both linear in t between two sample knots
closest     = distance from the origin to the relative segment
```

`DodgeSolver.PlanWalk` is that walk. It returns three numbers per threat per candidate: the closest
the two bodies ever come (`nearestD2`, and when), the **first** moment the plan is inside the
collision radius (`dangerMs`), and the interval that moment falls in.

**Why a relative segment and not segment-to-segment.** Both bodies are linear in time on a sample
interval, so their difference is linear too, and the closest approach of two objects on linear paths
*is* the distance from the origin to the relative segment. It is exact, it is one
`PointSegmentDistanceSquared` per knot, and the time-compatibility machinery the two-polyline form
needed — `TemporalRefusal`'s knot-window matching, `IsClosingAt`'s "is it still shrinking" — is gone,
because the time correlation is built into the subtraction. It is also **stricter than the samples**,
which matters for the reason §3.3 gives: at 24 units/s a bullet moves 0.72 units between knots, wider
than the 0.41-unit disc, so a sample-only test can walk a bullet clean through the player.

**The refusal is the same two clauses as before, translated.** Inside the decision window (50 ms) is
refused outright — that is what `hitDuringTheFrame` was. Further out, the candidate is refused only
when it gets **closer to that bullet than standing still would**, which is the one-sided doctrine
rules 4 and 5 already applied to a disc the player is standing in, carried to the rule that never had
it.

**And a threat the player is already inside contributes nothing to the score.** The rule doctrine's
own sentence — *"the damage for this frame is already committed, whatever the solver does"* — is just
as true of the weights as of the rules. §10's recording is 11 % "already touching" records, and for
those the plan walk's closest approach is attained at `t = 0`, for **every** candidate, in **every**
direction: durability read 0 ms, clearance read one negative constant, resolve could never reach 1.0.
Three of six terms became constants at once. They still govern admission (rules 4, 5 and 6 remain
one-sided against them) and `CountUnavoidable` still counts them, because a bullet the player is
standing in genuinely is unavoidable damage. The score does not get a vote on it.

### 11.3 The forward goal, and the fan

**The imaginary point in front of the character** is `P + speed * GoalLookaheadSeconds * (cos θ,
sin θ)`, where θ is:

- the **measured course** — the world heading of the displacement the client itself produced between
  the last two frame ticks — when the character is moving; and
- the player's own steering (intent, converted to the world frame) when they are not.

**The course and not the input angle, because the input is a request and the course is a fact.** On a
frame the dodge is overriding, `GameInput.MoveAngle` belongs to the operator's hands; the thing the
character is actually doing is the displacement. The operator's own "13.7 degrees" is the case in
point: `MoveAngle` is an **input-frame** number — `World.cs:12692` computes
`angle = MoveAngle + (-WorldRotation + aimOffset)` — and the course was never in that frame at all, so
the goal is the one heading in this design that needs no conversion. §11.4 is what happens when the
harness forgets it.

**The goal is a candidate, and so are its neighbours.** The ring's 24 headings are `k * 15°`, so the
exact course is representable only by accident — and 13.7° is not a multiple of 15. `BuildAndAdmit`
now adds kind-5 candidates at `goal`, and at `goal ± j * (2π / Headings)` for `j` up to
`GoalFanSteps`. The fan is what lets the dodge leave the goal line by the **smallest** amount that
clears an obstacle rather than snapping to the nearest ring heading. It is 3 more candidates at the
default fan of 1: 77 evaluated instead of 74.

**Zero radians is a legal heading, and a missing field is not, so `PlayerView` grew `IntentValid`.**
The adapter sets it from `_getMoveAngleRadians != null`; the harness sets it because a trace always
carries the field. Without it, a run in which `GameInput.MoveAngle` never resolved would hand the
solver an intent pointing due east on every frame — and the design leans on the intent twice: the
`IntentSafe` early-out **emits it as a steering command**, and it is the goal's fallback. The status
line has said `intent MISSING (0.0)` since §9; this is the same fact carried to where it changes a
decision. The goal is valid only when the course **or** the intent was actually measured, and a
missing intent scores zero on both terms rather than pointing at the horizon.

**The goal term is scaled by distance, and that is the difference from `WeightIntent`.**

```
goal = max(0, dot(heading, goal)) * (candidateDistance / budget)
```

`intent` asks *is this the direction the hands asked for*; a slow step in the right direction
preserves intent exactly as well as a fast one, which is why the move penalty used to decide the
magnitude. `goal` asks *does this make progress toward where the character is going*, and a third of
a step makes a third of the progress — so distance along the goal heading is a gain and the move
penalty is a smaller cost against it. This is the term that stops the dribble §10.3 fixed by hand.

**Where the goal does its work is worth stating, because it is not the frame the safety terms are
about.** When the only threats are ones the player is standing in, `resolve`, `clearance` and
durability are all constants (for the reason in §11.2) and safety has, correctly, nothing to say. The
goal picks the escape direction — the way the character was already going — which is exactly the
behaviour the operator asked for.

### 11.4 Defect 3.13: `--config` was a struct passed by value, and had never worked

Found by trying to measure whether the goal term mattered: two runs of
`--config goal=0,fan=0` produced **byte-identical decision histograms** down to
`NoAdmissible=6` and `durability p50 228.213 ms`. A switch that neither throws nor works is this
document's recurring failure mode, and it was the harness:

```csharp
private static void ApplyConfig(string text, DodgeConfig config)   // DodgeConfig is a struct
```

Every assignment landed in a copy and was discarded on return. The harness printed the preset's
numbers as though they were the ones it had been asked for, `Describe()` and all. It is **defect 3.2
in the instrument rather than in the solver** — a struct by value whose writes are lost — and the
lesson is that section's, unchanged: when a function's output disagrees with its own body, the body
is the second thing to read.

**The consequence for every number in this document is not small.** §2.1, §5 and §10 all present
`--config` as the tuning surface, and §10's own "before and after, same recording" table describes
weights moved against a recording. Any figure anywhere in this document that was produced through
`SolverHarness --config` was produced with whatever the preset said, not with what the command line
said. `ApplyConfig` now takes `ref`, and the fix is one keyword.

### 11.5 The correction the goal made load-bearing: the harness never rotated the intent

`SolverHarness` read the trace's `intent` field raw and carried `rotation` without using it. §9.2
recorded that as a known offline inaccuracy and deliberately left it, on the reasoning that the
harness's numbers were this document's published record. **The goal cannot be built on it.** The
intent is the fallback goal, so an unconverted intent is a goal pointing up to 359° away from where
the player is asking to go — and on the third recording the camera rotation is non-zero on **65.5 %
of frames, mean |rotation| 138°, max 359.9°**, so the fallback would have been near-random.

The harness now does what `DodgeAdapter` has always done — `IntentRadians = intent - rotation` — and
measures the course from consecutive recorded positions with the same threshold and the same
six-frame hold as `DodgeAdapter.UpdateSpeed`, deliberately the same shape, because two
implementations of "where is the character going" that agree until they do not are two different
dodges.

**So `overrides intent` is not comparable across this change.** Before, it counted disagreement with
an angle that was wrong by the camera's rotation; now it counts disagreement with the angle the
operator was actually holding. The figures below carry a footnote rather than a correction, because
the old number is not recoverable and pretending otherwise would be worse.

### 11.6 The numbers

Every run below is the same recording replayed through the same solver, before and after. The *before*
column is the shipped path model measured on the pre-change binary; the *after* column is the plan
model. The three recordings are `p20152` (§10's first live dodge), `p5824` (run 2) and `p26684` (run 3,
the longest).

#### `20261015-032549-p26684` — 21 443 frames, 365.6 s, 33 resolved collisions

| measurement | before | after |
|---|---|---|
| frames that moved | 3908 (18.2 %) | **5041 (23.5 %)** |
| `NoAdmissible` | **1148** | **6** |
| mean admissible | 46.8 of 74.3 | 51.7 of 77.3 |
| hysteresis holds | 2441 (62.5 % of moves) | 2573 (51.0 %) |
| stutter (>90° on an adjacent move) | 35 of 1900 (1.8 %), mean turn 10.8° | 52 of 3102 (1.7 %), mean turn **8.1°** |
| overrides intent | 1617 (41.4 %) † | 2777 (55.1 %) |
| **keeps its course** (within 90° of the goal) | — | **3792 (75.2 %)** |
| **chosen durability p50** | **17 ms** | **500 ms** |
| `decide()` p99, evaluating (`--repeat 50`) | 145–217 µs (§4.4, §4.5, other runs) | **119 µs**, max 230 µs |
| T5 / T6 / T10 | pass | pass |
| T7 | 29 of 33 predicted, 4 missed | **29 of 33 predicted, 4 missed — identical** |

#### `20261015-024717-p5824` — 10 139 frames, 172.3 s, 24 resolved collisions

| measurement | before | after |
|---|---|---|
| frames that moved | 1652 (16.3 %) | 2489 (24.5 %) |
| `NoAdmissible` | 843 | **5** |
| mean admissible | 42.4 | 52.5 |
| hysteresis holds | 59.0 % | 48.9 % |
| stutter | 16 of 675 (2.4 %), 11.2° | 30 of 1548 (1.9 %), 8.7° |
| overrides intent | 693 (41.9 %) † | 701 (28.2 %) |
| keeps its course | — | 1962 (78.8 %) |
| chosen durability p50 | 17 ms | 500 ms |
| T5 / T6 / T10 | pass | pass |

#### `20261015-021911-p20152` — 6 267 frames, §10's recording

§10's own "after" column is the shipped state before this change: `NoAdmissible 729 (11.6 %)`, mean
admissible 34.7, 1353 moves, hysteresis 58 %, p99 155 µs.

| measurement | §10 "after" | plan model |
|---|---|---|
| frames that moved | 1353 (21.6 %) | 2073 (33.1 %) |
| `NoAdmissible` | **729** | **4** |
| mean admissible | 34.7 | 47.0 |
| hysteresis holds | 58 % | 52.5 % |
| stutter | — | 23 of 1509 (1.5 %), mean turn 8.0° |
| overrides intent | — | 656 (31.6 %) |
| keeps its course | — | 1499 (72.3 %) |
| chosen durability p50 | (not recorded) | 500 ms |
| T5 / T6 / T10 | pass | pass |
| T7 | 4 of 21 missed | 4 of 21 missed |

#### The six synthetic scenarios

| scenario | §10 (shipped) | plan model |
|---|---|---|
| `peaceful` | 0 | 0 |
| `headon` | 6 | 52 |
| `dodge` | 54 | 28 |
| `volley` | 0 | 0 |
| `corridor` | 2 | 2 |
| `passingby` | 7 | 20 |

**Every invariant holds on every scenario, and the move assertion still passes.** `headon` moving
five times as often and `dodge` moving half as often is the geometry change showing up where the
synthetic scenarios can see it; these six exist to be *falsifiable*, not to be a tuning target, and
the evidence for the change is the three recordings, not this table. It is recorded rather than
explained because "the synthetic move counts changed" is a thing the next reader will want to know
before they trust the recordings.

#### The decomposition

The three changes were measured separately on `p26684`:

| variant | moved | hysteresis | stutter | mean turn | keeps course | durability p50 | `NoAdmissible` |
|---|---|---|---|---|---|---|---|
| path model (before) | 18.2 % | 62.5 % | 1.8 % | 10.8° | — | 17 ms | 1148 |
| plan model, `--config goal=0,fan=0` | 23.5 % | 64.1 % | 1.2 % | 7.8° | 66.3 % | 262 ms | 6 |
| plan model + goal (shipped) | 23.5 % | 51.0 % | 1.7 % | 8.1° | 75.2 % | 500 ms | 6 |

**The plan geometry is what fixed the collapse** — `NoAdmissible` from 1148 to single digits,
durability from 17 ms to a real distribution, the mean turn from 10.8° to 7.8°. **The goal term is
what makes the dodge keep its line** — "keeps its course" 66.3 % → 75.2 %, and it breaks the
hysteresis hold more often (64.1 % → 51.0 %) because it gives the solver a reason to move that is not
safety. It costs a tenth of a percent of stutter, which is the trade it is: a dodge that prefers its
own line will occasionally change its mind about that line.

### 11.7 What the operator has to decide, and it is one trade

**`keeps its course` and `overrides intent` move in opposite directions, and the size of that is the
one open question this section cannot settle offline.** On `p26684` the dodge now disagrees with the
operator's held steering on 55.1 % of its moving frames against 41.4 % before (†, and see §11.5 — the
*before* figure was measured against an unrotated angle, so even that comparison is soft).

There are two readings and the recordings cannot choose between them:

- **The dodge is right.** The operator is being shot at from every direction; the hands are often
  pointed at a bullet, and the goal is the direction the body was already carrying, which is the
  direction that has been *surviving*. Overriding more often is the dodge doing more of its job.
- **The dodge is fighting the operator.** `DODGE-ALGORITHM.md` §5.7 makes intent preservation a hard
  requirement, not a preference, because AoE and bomb damage are deliberately left to the player. "It
  kept going the way it was going" and "it ignored me" are the same measurement from two sides.

**The knob for it exists and is the preset surface, not a new invention:**

```powershell
# the goal term at its shipped weight
SolverHarness.exe --trace <run>\trace.jsonl --collisions <run>\bootstrap.log

# the goal term alone removed: the plan geometry, no direction preference
SolverHarness.exe --trace <run>\trace.jsonl --collisions <run>\bootstrap.log --config goal=0,fan=0

# the operator-in-charge position: goal up, intent up, `stand` yields
SolverHarness.exe --trace <run>\trace.jsonl --collisions <run>\bootstrap.log --preset assist
```

`DRELAY_DODGE_W_GOAL` moves the one number, on the hook, in a live run. **The answer is the
operator's to give, and the next live session is where they give it** — which is the same shape §10
ended in, one level further along: the sentence that produced this section came from playing it, not
from reading it.

### 11.8 What this does not establish

- **Nothing about whether the plan model is *safer*.** T5, T6 and T10 hold, and T7 is unchanged to the
  collision, because the trajectory model and the collision observation are untouched — but the
  recordings are replays of positions the **path model** produced. A replay cannot tell you what
  happens when the dodge that is being measured is the one steering. §11.7's trade is exactly the kind
  of question only a live run answers.
- **Nothing about the goal lookahead's value.** `GoalLookaheadSeconds` is 0.30 s and
  `PlanLookaheadSeconds` is 0.50 s, both first guesses with a reason written beside them in
  `DodgeConfig.Default()`. Neither has been swept.
- **The fan is untuned.** `GoalFanSteps` is 1. It is the smallest fan that lets the dodge leave the
  goal line without snapping to a ring heading, and no measurement says 1 is better than 2.
- **Rule 7 is still vacuous and the escape gate still cannot fire.** Unchanged from §7 and §9.3.
- **§4.4's closing-speed cull and the `Projectable` flag are still unused.** The plan model made the
  decision *cheaper* (§11.6) rather than needing them.

### 11.9 Where every number in this section came from

| claim | source |
|---|---|
| the "before" columns for `p5824` and `p26684` | `SolverHarness.exe --trace <run>\trace.jsonl --collisions <run>\bootstrap.log`, run 2026-10-15 before the plan model, against the pre-change binary |
| every "after" column | the same command after it |
| `decide()` p99 119 µs, max 230 µs | the same command with `--repeat 50` |
| the 17 ms → 500 ms durability move | `chosen durability` line of each report |
| `keeps its course` | `overrides intent`'s sibling line, added by this section |
| the decomposition table | `--config goal=0,fan=0`, which is only meaningful because §11.4 was fixed first |
| run 3's rotation, 65.5 % non-zero, mean 138°, max 359.9° | a regex over the raw `trace.jsonl` for `"rot":` |
| the six-scenario table | `SolverHarness.exe --scenario <name> --frames 900`, every invariant held |
| the `headon` frame-58 dump | `--scenario headon --frames 900 --frame 58` |
| the 256-byte line budget for the new report fields | the worst-case string lengths of the three changed lines, 234 / 224 / 146 against `DRELAY_LOG_LINE_LEN` |
| defect 3.13 | two `--config` runs producing identical histograms, and `ApplyConfig`'s signature |

### 11.10 The live run this hands to the operator

**The offline half is done and reproducible; the live half is one command and it is yours.** Nothing
below was run against the game by this change — the adapter, the pilot and the world reads compile,
the wiring probe still arms the adapter on the synthetic host, and the decision itself has been
replayed over 37 849 recorded frames across three sessions. What has not happened is the solver and
the game sharing a frame with the plan model in it.

```powershell
cd G:\coding\javamapp\drelay\autododgeresearch

# 1. THE DRY RUN FIRST, and it is not a formality. The solver decides on every frame and logs
#    every move it would have made; nothing reaches the input.
#    Get shot at from more than one direction, and walk while you do it -- the goal is built from
#    the course, so a session standing still exercises only its fallback.
.\hook\tools\Run-Phase3Session.ps1 -Experiment Hold -Seconds 180 -Dodge

# 2. Read it, with the game closed.
Select-String -Path <run>\bootstrap.log -Pattern 'dodge |PHASE 3 DODGE'
```

**Five things to read, in order, and the first two are pass/fail:**

1. `dodge ARMED: ... | grid World.byte[,] | intent world-compensated | health Player.Health` — **if
   `grid` says MISSING, stop.** Admission rules 1–3 are vacuous and every candidate walks through
   walls; nothing else in the run means anything. If `intent` says `MISSING (0.0)`, the goal has no
   fallback and §11.3's `IntentValid` will suppress it — which is correct, and is a run to fix rather
   than to read.
2. `dodge config | preset=... plan=0.5s goal=0.3s fan=1 |` and the weights line beside it. **Two lines
   now, because the plan model pushed the single line past the 256-byte buffer.** If the *weights*
   line is absent, the geometry line was truncated and the numbers on it are not the numbers that ran.
3. `PHASE 3 DODGE DETAIL | ... | course N of M` — **this is the new number.** `course` counts the
   frames on which the forward goal came from a measured course rather than from your steering. If it
   is near zero on a session where you were walking, the displacement measurement is not reaching the
   adapter and the goal is the fallback on every frame, which is a different dodge from the one §11
   measured.
4. `dodge WOULD MOVE | frame N | heading ... | goal ...deg (course 1)` — one per move, at most 400.
   **Read `goal` against the direction you were holding.** With the camera turned, the goal must
   follow the character's motion and not the camera; if the two move together, §11.4's conversion is
   wrong somewhere and the run is a dataset about a bug rather than about the dodge.
5. `PHASE 3 DODGE NUMBERS | ...` and `PHASE 3 DODGE REASONS | ...` — the aggregate. `NoAdmissible` is
   the number this change moved most (1148 → 6 offline on `p26684`); a live run that reports hundreds
   of them is a run where the plan walk is refusing everything, which would be a different finding
   from the offline one.

**AND ONE LINE THAT §12 ADDED, WHICH IS NOW PART OF THE SAME READING:** `PHASE 3 DODGE DETAIL | ... |
no-hit N`, and `trajectory sampler resolved: ... cannot-hit-the-player filter ON`. The first live
session after §11 showed that **51.8 % of its threat records were the player's own projectiles**, and
these two fields are how the exclusion that removes them reports itself. §12.6 is the full read order
for a session where the operator is shooting; it supersedes nothing above, it adds the two checks that
used to be invisible.

**Then the acting run, if the dry run reads right:**

```powershell
.\hook\tools\Run-Phase3Session.ps1 -Experiment Dodge -Seconds 180 -RelayLog <session.log>
```

**And the two questions this run exists to answer, which no replay can:**

- **Does it feel like it keeps your line?** §11.7's trade — `keeps its course` 75.2 % against
  `overrides intent` 55.1 %. If it feels like it is fighting you, `DRELAY_DODGE_W_GOAL` (or
  `--preset assist`) is the knob, and it is one number.
- **Does the goal follow the body or the camera?** Point 4 above, on a run with the camera turned.
  13.7 degrees of error is small enough to feel like "slightly wrong" rather than broken, which is
  exactly the kind of defect that survives a session.

**Two things that are still true and are not this change's fault.** `-Experiment Dodge` runs no
phases, so the steering report's `PASS B`/`PASS C` checks have nothing to read and the runner
correctly does not raise them; `PASS A` used to fail on a 2π wrap — the client's `MoveAngle` setter
normalises to `[0, 2π)` while the pilot logs the raw value — and `tools/steering_report.py` now
compares angles as angles, so a run whose write landed reads as one.

---

## 12. The threat set contained the player's own shots

**Written 2026-10-15, after §11's plan model was played live for the first time.** The operator's
report is two sentences long and the second one is the defect:

> *"Massive improvement. There might have been a mistake on what camera rotation/world angle is
> though. From past tests, camera rotation doesn't affect movement e.g. if the camera is rotated, the
> hook would still move the player in the global direction (compared to a real player holding up and
> rotating, they would walk in a circle). This isn't a problem, I'm just clarifying how it works.*
>
> *Right now the dodges are almost perfect, but when the character shoots in a direction the hook
> moves the player around, this made normal movement jittery."*

§12.5 is the rotation question, and the answer is that nothing was wrong. §12.1 is the other one, and
**more than half of that run's threat set was the player's own projectiles.**

### 12.1 What was in the threat set

The run is `20261015-151232-p5424`, 7926 recorded frames, and it is the first session with §11's plan
model in it. Parsed from the trace alone:

| measurement | value |
|---|---|
| bullets tracked | 1210 |
| **of those, bullets whose FIRST recorded sample is within 0.3 units of the player** | **932** |
| and those fly AWAY from the player over their own horizon | 932 of 932 |
| their cadence | three at a time, every 8 frames — a three-shot spread at ~7.5 shots/s |
| **share of all threat records** | **8158 of 15756 = 51.8 %** |
| **"already touching" verdicts that were these** | **959 of 971** |
| **moves caused by them** | **255 of 1140 = 22 %** |
| frames forced to evaluate a candidate set by them | 433 |

**They are the player's own bullets.** Three of them leave the muzzle together every eight frames,
they are born inside the player's collision disc — which is what gets them past every cull this
project has — and they fly outward for 9.8 units.

**And the removal experiment is the proof.** A copy of the trace with those bullets deleted, replayed
through the same solver:

| | with the own shots | without |
|---|---|---|
| frames that moved | 1140 (14.4 %) | **885 (11.2 %)** |
| `NoThreat` (the decision never started) | 5979 | **6412** |
| `StandSafe` | 805 | 628 |
| **ALREADY TOUCHING** | **971 of 15756 (6.2 %)** | **12 of 7598 (0.2 %)** |
| frames with a threat that mattered | 1144 | 888 |
| ...of those, nothing the dodge could do | 326 (28.5 %) | 75 (8.4 %) |

**The mechanism is `standInsideDisc`.** `Decide` takes its `stand` early-out — the specification's
preferred answer, "the best dodge is usually no dodge" — only when the player is **not** inside a
disc. A projectile born at the muzzle is inside the player's disc on the frame it spawns, so every
frame the player fired, `stand` was refused and the solver went on to pick a replacement heading and
steer. The dodge was escaping a bullet the operator had just fired. `TryEgressDirection` fired for the
same reason: "get out of the thing I am standing in" — the thing being the muzzle.

That is the whole of *"when the character shoots the hook moves the player around"*, and the jitter is
its shadow: on a frame where the player is walking and an own shot is in the set, the dodge overrides
with a heading of its own for a few frames and then yields again.

**And it is not only the dodge.** The same bullets were in the live predictor's input, which marked
them "predicted to hit the player" on the frame they spawned — the player's own shot, born touching
them. The run's own `PHASE 2 NUMBERS` line reads **false positives: 1005**, and a large part of that
column is this; §4.5's mystery low-`GameId` bullets (`(708,59016)`, `(709,59016)`, ... "predicted at
frame 5, closest 0.199 against r_eff 0.41", eight frames apart, three at a time) are the same thing
in an older recording, and that section's guess — "the game recycles ids into a low range for a
second projectile family, or those are a different entity class being tracked as projectiles" — has
its answer here: they were the player's own.

### 12.2 The client's own answer

The project's rule for a question the game already answers is *call the game, do not reimplement it*
(`DODGE-ALGORITHM.md` §4.1), and the client answers this one with a single boolean.

```csharp
// Projectile.Hit()                                    Projectile.cs:1505
private bool Hit()
{
    if (HitsPlayer) { return HitCharacter(); }
    return HitEnemy();
}
```

A projectile with `HitsPlayer == false` **is never tested against a player at all** — `HitCharacter()`
is unreachable for it. And the spawn path sets the flag:

```csharp
// Entity.cs:1600, the one funnel every non-parked server projectile goes through
projectile.ProcessProjectileData(num4, <damage>, ..., position, projDesc,
                                 this is Enemy,     // hitsPlayer
                                 this is Player,    // playerOwned
                                 this, affix, item.Shiny, criticalHit, isAbility);
```

**`hitsPlayer = this is Enemy`.** Every projectile the local player fires has `HitsPlayer == false` by
construction. `Projectile.LocalSeedDerived` (`:115`) is the client's second, independent statement of
the same thing — *"true only for projectiles the local player fired"* — and is carried as well as a
belt-and-braces clause for the one other spawn path (`Entity.cs:1935`, the mirrored-projectile case,
where `hitsPlayer` is computed rather than derived from the shooter).

### 12.3 The fix, and why it lives in the sampler

`TrajectorySampler` resolves both fields, and `FillTrajectory` declines a projectile the client will
never collide with the player **before it takes a single sample**:

```csharp
if (CannotHitPlayer(projectile)) { return false; }
```

**One line, and it fixes the live decision and the recorded trace together** — which is the property
the two call sites have been made to share on purpose (`DodgeAdapter.FillThreats` and
`TraceRecorder.AppendThreat` both call this method and both already handle `false` by skipping). A
second implementation in the adapter would have been a second answer to "which bullets matter", and
this document has a section (§4.6) about what happens when two implementations of one predicate are
never compared.

**It also makes the sensor cheaper rather than more expensive**, because the decline happens before
the 21 `GetPosition` calls: on a shooting player that is half the sampling work gone.

**It fails open, and it says so.** An unresolved field, or a read that throws, means "this might be a
threat" — so the worst case of a broken resolution is the behaviour every run before this one had,
never a threat silently dropped. The resolution line names the outcome on every run:

```
trajectory sampler resolved: ... , cannot-hit-the-player filter ON (Projectile.HitsPlayer/Projectile.LocalSeedDerived)
```

or `MISSING (MISSING/MISSING) -- the threat set WILL contain the player's own shots`. And the count is
recorded:

- the trace's frame line carries **`"nohit":N`** — the projectiles declined on that frame, so a
  recording can be interrogated a session later;
- `SolverHarness` prints `declined, cannot hit: N projectile(s) over M frame(s)`, and says
  `NOT RECORDED` for a trace written before the field existed rather than printing a zero that looks
  like a clean run;
- the hook's own report ends with `PHASE 3 DODGE DETAIL | ... | no-hit N`.

**A count is the only instrument that can tell "there were none" from "the exclusion did nothing"**,
and those two are identical in the threat array. This is §3.8's lesson in a new place: a filter whose
absence is invisible is a filter that will be absent and believed.

**The synthetic host now models both flags** — one projectile in three is a local shot, born on the
player and aimed away — because a branch no fixture can reach is a branch nothing tests, and this
file's own doctrine says so ("a filter that never rejects anything cannot pass"). The probe run reads
`cannot-hit-the-player filter ON (Projectile.HitsPlayer/Projectile.LocalSeedDerived)`, which is the
resolution path verified end to end even though the probe itself is still inconclusive for §9.4's
unrelated reason.

### 12.4 What this does and does not change

- **Every number in §11 and §4 is unchanged.** They were computed from recorded traces played back
  through the solver, and the harness reads the threat list that is *in the file*; the filter is in
  the sampler, which the harness never runs. All four recordings replay to the same histograms to the
  digit after this change, and all six synthetic scenarios still hold every invariant. **The
  recordings were the input to the fix; they are not evidence for it.**
- **The evidence for it is the removal experiment (§12.1)** and the next live session (§12.6).
- **T7's live column should improve and its harness column should not move.** The live observer's
  `predicted=` flag is set in `TrajectorySampler.OnFrame`, which now applies the same exclusion, so
  the ~1000 false positives it reports per session should fall hard. The harness re-derives from the
  recorded paths and never sees the field, so its own counts stay where they are — which is the
  correct behaviour and also means `T7P` (the parity guard) is now comparing two implementations that
  differ by exactly this exclusion. **If `T7P` starts failing on the next run, that is why**, and the
  fix would be to carry the flag into the trace rather than to widen the guard.

### 12.5 The rotation question, settled by measurement

**Nothing was wrong, and the operator's own description is exactly what is implemented.** The claim
to check was: does the character's displacement follow the raw `GameInput.MoveAngle`, or the
world-converted `MoveAngle - WorldRotation`? Over the 3182 moving frame pairs of
`20261015-151232-p5424`, comparing the frame-to-frame displacement direction against each:

| the displacement is within 14° of... | count | share |
|---|---|---|
| **`intent - WorldRotation`** (the converted, world heading) | **2388** | **75.0 %** |
| `intent` raw (the input-frame angle) | 1094 | 34.4 % |

`intent - rot` is closer than raw on 1604 pairs; raw is closer on 385. The 25 % that are neither is
mostly the frames the dodge itself was steering — on those the displacement is the pilot's heading,
not the player's — plus the frames where the angle is genuinely changing.

**And the two halves of the operator's sentence are both right:**

- a **real player** holding a direction while the camera turns walks in a circle, because the client
  computes `angle = MoveAngle + (-WorldRotation + aimOffset)` (`World.cs:12692`) and their input is
  camera-relative — which is the 75 % row above, measured on frames the dodge did not touch;
- the **hook** moves in the global direction regardless of the camera, because `SteeringPilot` writes
  `worldHeading + WorldRotation` and the client's own subtraction takes it straight back out.

So the conversion in `DodgeAdapter` and in `SolverHarness` is correct, and the 13.7° the operator
named is handled: the goal is built from the **course**, which was measured from a world-space
displacement and never needed converting at all (§11.3).

### 12.6 What the next run must show

The instrument is already in the output. Run a session with the player **shooting a lot and walking**,
then read, in order:

1. `trajectory sampler resolved: ... cannot-hit-the-player filter ON (...)`. **If it says MISSING,
   stop** — nothing below it is about the fix.
2. `PHASE 3 DODGE DETAIL | ... | no-hit N`. **`N` must be large** — the previous session's own-shot
   rate was ~1 projectile per frame. `no-hit 0` on a session where the player was shooting means the
   exclusion is not in force, and the resolution line in step 1 says which field was missing.
3. `ALREADY TOUCHING` in `SolverHarness`'s classification, replayed from the new trace. It was
   **6.2 %** of threat records with the own shots in and **0.2 %** without; the new recording should
   read near the second.
4. **The feel.** The dodge should stop taking the wheel while the operator is firing, and the 22 % of
   moves that were the muzzle should be gone. That is the thing this section was written for and the
   thing only the operator can read off.

### 12.7 One correction to §9.5, found while bounding these lines

§9.5 states that the dodge report is split across lines because `DrelayLog.cpp` truncates at
`DRELAY_LOG_LINE_LEN` (256) with no mark. **That constraint does not apply to these lines.**
`HookEntry.AppendLog` buffers into managed memory and `FlushLog` writes `bootstrap.log` with a
`StreamWriter`; the native ring is a different file. Measured on `20261015-151232-p5424`, which is a
normal run: **`bootstrap.log` has 1151 lines and 29 of them are longer than 256 bytes, the longest at
483** — complete, not truncated.

The split report is harmless and stays, but a future reader should not spend an afternoon on a limit
that is not binding. **What is still true and still matters:** a line written through the *native*
logger is silently truncated at 256, and truncated lines carry no mark.

### 12.8 Where every number in this section came from

| claim | source |
|---|---|
| 932 of 1210 tracked bullets first appear within 0.3 units and fly away | a parse of `trace.jsonl`'s per-frame threat arrays, first observation per `(id, st)` |
| 8158 of 15756 threat records are those bullets | the same parse |
| the removal experiment's two columns | the same trace with those `(id, st)` keys deleted, run through `SolverHarness --trace` for both |
| 971 → 12 `ALREADY TOUCHING` | the `-- the threats, by whether they should be dodged at all` section of those two reports |
| 255 of 1140 moves | `frames that moved` in those two reports |
| the mechanism, `standInsideDisc` | `DodgeSolver.Decide`'s `stand` branch, and `--frame` dumps showing `stand admitted as DiscAtArrival` |
| `hitsPlayer = this is Enemy` | `work/decomp/DarzasDominion.Darza.Game.Objects/Entities/Entity.cs:1600`, argument-aligned against `Projectile.ProcessProjectileData`'s own 15-parameter signature (`Projectile.cs:810`) |
| `Hit()` dispatches on `HitsPlayer` | `Projectile.cs:1505` |
| the live observer's 1005 false positives | `20261015-151232-p5424/bootstrap.log`, its last `PHASE 2 NUMBERS` line |
| the 75 % / 34.4 % conversion table | a parse of 3182 consecutive moving frame pairs, comparing the displacement bearing against `intent` and `intent - rot` |
| 1151 lines, 29 over 256, longest 483 | a length histogram of `20261015-151232-p5424/bootstrap.log` |
| the filter resolving end to end | `Probe-DodgeDryRun.ps1`'s run, its `trajectory sampler resolved` line |

---

## 13. The goal was the body's own motion, and the hysteresis scored the held heading at the wrong radius

**Written 2026-10-15, after §12's recording was played back with an instrument.** The operator's
sentence is one line and it names the defect:

> *"The only thing holding this back is that the hook takes over movement completely when it's
> dodging; it would be better if it still tried to pathfind in the direction the player is trying
> to walk."*

§11.3 built the forward goal from the measured **course** — the world heading of the displacement
the client itself produced — on the reasoning that *"the course and not the input angle, because
the input is a request and the course is a fact."* That reasoning is wrong in exactly one place:
**while the dodge is steering, the course is the dodge's own last command.** The goal was the
solver's previous choice, fed back through `WeightGoal` (0.9) on top of `WeightHeading` (1.0), which
points at the same previous choice again, against `WeightIntent` (0.4) for what the player actually
asked for. On `20261015-153805-p26156` the goal came from the course on **1612 of the 1665 moves
(96.8 %)**, and on the 1081 moves where the player was walking the written heading was a mean
**59.3 deg** from the direction they were holding.

And the second defect is not a tuning question and was not visible in any aggregate: **of the 1114
hysteresis-held moves in that session, all 1114 were at `scale 0.35`.** The dodge held its heading
for the dwell and moved at a third of a step, over and over — §1.3's *"a dodge that has stopped
walking"*, with a cause rather than a symptom.

### 13.1 Defect 3.14: the forward goal pointed at the dodge's own last command

`_goalRadians` is resolved once per frame in `DodgeSolver.Decide`. It was:

```csharp
_goalRadians = player.CourseValid ? player.CourseRadians : player.IntentRadians;
_goalValid   = player.CourseValid || player.IntentValid;
```

`PlayerView` already carried the fact that distinguishes a live request from a stale one —
`Moving`, which is `GameInput.Moving`, filled by the adapter and by the harness. It is now:

```csharp
bool playerWalking = player.Moving && player.IntentValid;
if (playerWalking)      { _goalRadians = player.IntentRadians;  _goalSource = GoalSource.PlayerIntent; }
else if (CourseValid)   { _goalRadians = player.CourseRadians;  _goalSource = GoalSource.Course; }
else                    { _goalRadians = player.IntentRadians;  _goalSource = GoalSource.Intent; }
```

**The gate is narrow on purpose and it is not §3.10's gate.** That section removed `Moving` as a
*whole-frame* gate, because a false value there disabled the solver for every frame of a 1289-frame
session. This asks a different question — not "may the solver run", but "is there a direction worth
preserving" — and a `Moving` that is wrong here costs one goal direction and nothing else. It was
measured before being used, on the 249 frames of `p26156` where the adapter's own log says the pilot
wrote **nothing**: `moving` was still true on 158 of them (63.5 %), and the recorded `intent` on
those frames takes the eight compass directions. It carries the player's own state, not a leftover
write. The comment at the line says so, because §3.10's comment says the opposite about the same
field and the next reader will find the two disagreeing.

**It is safe by construction.** The goal only *ranks* candidates that already passed admission; the
admissible set is the safety boundary (§5.3's doctrine) and nothing in this change touches it. A
player holding a direction into a bullet gets the best admissible move toward that direction, which
is the intent-preserving behaviour `DODGE-ALGORITHM.md` §5.7 makes a hard requirement. T5, T6 and
T10 hold on all five recordings and all six scenarios, unchanged.

### 13.2 Defect 3.15: the held heading was re-derived at the near radius, so the hold broke for a reason that was not direction

`Hysteresis.Keep` re-derives the held heading's score from the current frame's candidate set and
drops the hold when the frame's best beats it by `HysteresisMargin` (0.12). `FindHeading` supplied
that score, and it broke ties with `>`:

```csharp
float d = Dot(_results[i].HeadingRadians, heading);
if (d > bestDot) { bestDot = d; best = i; }
```

**The ring holds three radii at each of its 24 headings** (`RadiusNear` 0.35, `RadiusMid` 0.7,
`RadiusFar` 1.0), so every direction is three candidates with the same `HeadingRadians` and
therefore the same dot product. `>` keeps the first maximum, the loop runs near → mid → far, and so
**a held heading was always re-derived at the near radius — the lowest-scoring of its three.**

That number was then compared against the frame's best candidate. Instrumenting the comparison
itself, on `p26156`:

| measurement | value |
|---|---|
| frames that reached the comparison with an admissible held candidate | 1329 |
| ...where the held heading and the best candidate's heading were **the same one** | **395** |
| ...and the comparison still saw a penalty (median / p90) | **0.88 / 2.65** |
| hysteresis-held moves, all of them | 1114 |
| hysteresis-held moves at `scale 0.35` | **1114 (100 %)** |

The penalty is larger than the 0.12 margin, so the hold broke on a difference that was not a
disagreement about direction at all: the best was the far candidate at that heading and the hold
was being scored as the near one. The next frame re-took the same heading at 0.35.

`FindHeading` now returns the held heading's **best-scoring admissible** candidate, preferring an
admissible one over a refusal at the same direction so that `Keep`'s safety clause is not tripped by
a refusal the heading has not actually lost. **This does not strengthen the hysteresis, which §10.4
tried and reverted** — the margin, the dwell and the directional rule are untouched. It makes the
number the margin is compared against the same kind of number the margin is compared to.

### 13.3 The instrument, because an instrument that cannot see the change cannot validate it

**The per-frame decision existed only in the adapter's `dodge WOULD MOVE` log, capped at 400 lines a
session**, so every question this section asks could previously be answered only as a mean over a
sample of 1665 moves. Two things were added before any measurement:

- **`SolverHarness --decisions <path>`** writes one line per recorded frame — frame, action, reason,
  held, heading, scale, goal, **`goalsrc`**, held-direction, intent, course, moving, evaluated,
  admissible, durability, clearance, resolve, score, runner-up, cost — so an experiment is a diff of
  two files. `goalsrc` is read from the solver (`GoalSource`) rather than re-derived by the reader,
  because two implementations of one rule agree until they do not.
- **`tools/decisions_report.py`** reads them and prints the before/after columns. It replaces
  `tools/intent_report.py` for anything with a trace; `intent_report.py` stays because its first
  half — whose field `intent` is — has no replacement, and it reads a *live* run's log, which the
  harness cannot.

**And the change reports on itself live: `PHASE 3 DODGE DETAIL` now carries `goal N of M`** beside
`course N of M`, counted from `DodgeSolver.GoalSource`. A live run where `goal` is zero on a session
where the operator was walking is a run where `GameInput.Moving` never reached the solver, and no
other line in the report can say so.

### 13.4 The numbers

Every column is the same five recordings replayed through the same solver. **`before`** is the
shipped state; **`goal`** is §5.1 alone; **`both`** is §5.1 and §13.2. The decomposition is here
rather than in a separate table because the two changes are separable and were measured separately
— §11.6's form.

#### `20261015-153805-p26156` — 7169 frames, §12's recording

| measurement | before | goal | **both** |
|---|---|---|---|
| the goal came from the **player's held direction** | 0 (0.0 %) | 1081 (64.9 %) | **1081 (64.9 %)** |
| the goal came from the measured **course** | 1612 (96.8 %) | 568 (34.1 %) | **568 (34.1 %)** |
| the heading **equalled its own goal** | 825 (49.5 %) | 419 (25.2 %) | **419 (25.2 %)** |
| mean \|heading − the player's direction\|, on moves while walking | 59.3° | 49.9° | **51.0°** |
| median of the same | 45.0° | **30.0°** | **30.0°** |
| ...disagreeing by more than 45° | 548 (50.7 %) | 391 (36.2 %) | **398 (36.8 %)** |
| ...disagreeing by more than 90° | 218 (20.2 %) | 183 (16.9 %) | **208 (19.2 %)** |
| within 15° of the player's direction | 174 (16.1 %) | 182 (16.8 %) | **191 (17.7 %)** |
| **scales: 1.00 / 0.70 / 0.35** | 544 / 5 / 1115 | 541 / 15 / 1108 | **1543 / 56 / 65** |
| hysteresis holds | 1114 (66.9 %) | 1102 (66.2 %) | 1398 (84.0 %) |
| **of the held moves, those at `scale 0.35`** | **1114 (100 %)** | **1102 (100 %)** | **60 (4.3 %)** |
| overrides intent (in the harness's own terms) | 274 (16.5 %) | 208 (12.5 %) | 231 (13.9 %) |
| stutter (>90° on an adjacent move) | 4 of 1568 (0.3 %), 3.1° | 6 of 1568 (0.4 %), 3.8° | 9 of 1568 (0.6 %), 3.7° |
| changed heading at all, adjacent | 24.8 % | 19.8 % | **12.8 %** |
| chosen durability p50 / p10 | 500 / 231 ms | 500 / 226 ms | 500 / **500 ms** |
| `decide()`, evaluating, `--repeat 50` | p50 33, p99 110, max 250 µs | — | **p50 34, p99 111, max 247 µs** |
| T5 / T6 / T10 | pass | pass | pass |

**The decay is visible per run, not only in the mean.** Over the 52 consecutive-move runs of three
frames or more, the deflection on the first move of a run against the last:

| | before | goal | **both** |
|---|---|---|---|
| first → last | 50.0° → 46.2° | 47.6° → 38.9° | **47.5° → 40.6°** |

§1's requirement 2 asked for the returning-to-the-line behaviour to be *visible in the log rather
than inferred from a mean*, and this is that line: each run starts further off the line than it
finishes, and the gap widens from 3.8° to 7.0–8.7°.

#### All five recordings, `both` against `before`

| run | frames | moves | goal=player | goal=course | mean deflection | >45° | >90° | `scale 1.0` |
|---|---|---|---|---|---|---|---|---|
| `p20152` | 6267 | 2073 | 0 → **2044** | 2052 → **20** | 65.8° → **54.8°** | 49.1 → **42.6 %** | 31.5 → **23.1 %** | 805 → **1604** |
| `p5824` | 10139 | 2489 | 0 → **2080** | 2168 → **161** | 66.8° → **59.6°** | 49.8 → 45.0 % | 30.5 → **25.4 %** | 1087 → **1916** |
| `p26684` | 21443 | 5041 | 0 → **3754** | 4302 → **706** | 79.6° → **70.9°** | 67.7 → **61.2 %** | 41.8 → **35.5 %** | 2073 → **3633** |
| `p5424` | 7926 | 1140 | 0 → **746** | 954 → **223** | 87.1° → 82.5° | 69.0 → **63.1 %** | 50.5 → **46.5 %** | 514 → **903** |
| `p26156` | 7169 | 1665 | 0 → **1081** | 1612 → **568** | 59.3° → **51.0°** | 50.7 → **36.8 %** | 20.2 → 19.2 % | 544 → **1543** |

**The `goal` column is the clean result and it is positive on every recording.** §5.1 alone reduces
the mean deflection on all five (59.3→49.9, 65.8→56.9, 66.8→60.7, 79.6→71.7, 87.1→76.7), the
over-90° share on all five, and the over-45° share on all five, while moving the goal's source from
the body to the player on 64.9–98.6 % of moves. **It does not touch the magnitude**, which is what
§13.2 is for — 541 of 544 full-scale moves before, 541 of 541 after.

**The `both` column carries one trade, and it is the reason this section is not simply `goal`.** On
three recordings `both` is better than `goal` on the mean deflection and the tails (`p20152`
56.9→54.8, `p5824` 60.7→59.6, `p26684` 71.7→70.9) and on **`p26156` and `p5424` it is worse**
(49.9→51.0, 76.7→82.5), while being much better on magnitude everywhere and better on the *median*
on all — median 30.0° on `p26156` both and `goal` alike, 45.0°→30.0° on three of the others.

**What that trade is worth is not decidable offline, and §11.6 says why**: the recordings are
replays of positions the *old* dodge produced, and the old dodge moved at a third of a step, so its
world is the world of a character who never took a full step. A full-magnitude step in a deflected
direction travels three times as far off the line as a third of one — which is what a larger
measured deflection on a replay means and is not the same statement as *worse*. The magnitude result
is unambiguous; the deflection result under §13.2 is *the consequence of the magnitude result* and
is the operator's to judge live, which is §13.6.

#### The six synthetic scenarios

| scenario | §11 (shipped) | `both` |
|---|---|---|
| `peaceful` | 0 | 0 |
| `headon` | 52 | 52 |
| `dodge` | 28 | 28 |
| `volley` | 0 | 0 |
| `corridor` | 2 | 2 |
| `passingby` | 20 | 20 |

**Every invariant holds on every scenario, and every move count is identical to §11's.** These six
exist to be falsified, not to be tuned to (§6.5), and the fact that a change to the goal's
provenance moves none of them is the expected result: their threat geometry is what they measure.

### 13.5 One report line was renamed, and one was added

§7 asked for `keeps its course` to be *"renamed or joined by a `follows the player's direction`
line"*, because after this change "its course" is the wrong question. Both were done — the old line
stays so its history remains comparable, and the new one sits beside it:

```
  keeps its course    : 1426 move(s) (85.6% of moving frames) stayed within 90deg of the forward goal
  follows the player  : 1426 move(s) (85.6% of moving frames) stayed within 90deg of the direction the player was holding
```

They are now the same number, which is itself the result: with the goal exogenous, "stayed within
90° of the goal" and "stayed within 90° of the player" are the same statement on 64.9 % of moves.

### 13.6 What this does not establish

- **Nothing about the §13.2 magnitude trade by feel.** §13.4's `both` column trades deflection for
  magnitude on `p26156` and `p5424`, and a replay cannot say which the operator prefers. The live
  run is the conclusion (§6.4), and `DRELAY_DODGE_W_GOAL`, `--preset assist` and a one-function
  revert of `FindHeading` (13.2, the `>` form is quoted there) are the three knobs.
- **Nothing about whether full-magnitude moves are safer.** T5, T6 and T10 hold and the durability
  distribution improves (p10 231 → 500 ms on `p26156`), but T7 is unchanged and the recordings are
  replays. A character who travels three times as far per frame meets a different world.
- **Nothing about closure.** §6.4: the recording is *"what the new solver would have done in the
  old solver's world"*, enough to rank two versions and not enough to conclude.
- **The goal lookahead and the fan are still untuned** (0.30 s and 1), unchanged from §11.8.
- **`stand` is still 1745 frames of `p26156` (24.3 %)** and still clears the input when it fires.
  `HANDOFF-DIRECTED-DODGE.md` §5.5 is explicit that this is a second, independent way the dodge
  takes over movement and must be run as its own experiment (`--preset assist`); it was not folded
  into this one.

### 13.7 Where every number in this section came from

| claim | source |
|---|---|
| every "before" column | `hook/tools/Capture-DodgeDecisions.ps1 -Out work\dd-before`, against the pre-change solver, kept in `work/dd-before/` |
| every "goal" column | the same script with §5.1 and §13.2 reverted, `work/dd-goalonly/` |
| every "both" column | the same script on the shipped source, `work/dd-final/` |
| the per-frame numbers behind all three | `work/dd-{before,goalonly,final}/dec-<run>.txt`, read by `tools/decisions_report.py` |
| 1612 of 1665 moves had a course goal; 0 of 1665 after | the `goalsrc` column of those dumps, and `DodgeSolver.GoalSource` from which the harness reads it |
| 1114 of 1114 held moves at `scale 0.35`; 60 of 1398 after | the `held` and `scale` columns of the same dumps |
| 1329 comparisons, 395 at the same heading, penalty 0.88 / 2.65 | a temporary `HYSTDBG` trace on the `Hysteresis.Keep` call site, printing `prevHd`/`bestHd`/`prevScore`/`bestScore`/`takenMs`/`nowMs` for every frame that reached it; removed after the measurement, `work/dd-hystdbg.txt` for the run it produced |
| the hysteresis dwell and the `>` tie-break | `DodgeSolver.Hysteresis.Keep`, `DodgeSolver.FindHeading` (the old body is quoted in 13.2) |
| `moving` true on 158 of the 249 pilot-silent frames | `python tools\intent_report.py "$env:LOCALAPPDATA\drelay\hook\20261015-153805-p26156"`, whose section 1b is that test |
| 49.9 → 51.0 deg etc. | the mean over the moves of each dump with `moving` set, computed by `decisions_report.py` |
| the first→last deflection per run | the same script's consecutive-move-run analysis |
| `decide()` p99 111 µs, max 247 µs | `--repeat 50`, `cost-p26156-repeat50.txt` |
| the 255-byte worst case for the new report field | the format string in `DodgeAdapter.Publish` measured against `DRELAY_LOG_LINE_LEN` (272, §12.7): 224 chars raw, 217 at the recorded runs' own field widths, 255 at implausible ones — and the `[level] ` prefix of §12.7's probe check is 7 more, so a real run is ~224 and the bound is the one to watch |
| the probe still INCONCLUSIVE | `Probe-DodgeDryRun.ps1`, exit 3, the host frame tick of §9.4 — unchanged and still open |

### 13.8 The live run this hands to the operator

**The offline half is done and reproducible; the live half is one command and it is yours.** The
adapter, the pilot and the world reads compile; the wiring probe still arms the adapter on the
synthetic host; and the decision has been replayed over 52 944 recorded frames across five sessions,
with the dodge's own numbers read from every one of them rather than from a 400-line sample. What
has not happened is the solver and the game sharing a frame with an exogenous goal in it.

```powershell
cd G:\coding\javamapp\drelay\autododgeresearch

# 1. THE DRY RUN FIRST. The solver decides on every frame and logs every move it would have made.
#    WALK WHILE YOU SHOOT -- the goal is now the direction you are HOLDING, so a session standing
#    still exercises only its fallback and reads `goal 0 of N`.
.\hook\tools\Run-Phase3Session.ps1 -Experiment Hold -Seconds 180 -Dodge

# 2. Read it, with the game closed.
python tools\intent_report.py "$env:LOCALAPPDATA\drelay\hook\<run-dir>"
```

**Four things to read, in order, and the first is pass/fail:**

1. `dodge ARMED: ... | intent MISSING` — **if `intent` says MISSING, stop.** §5.1's gate is
   `Moving && IntentValid`, so a missing intent means the goal is the course on every frame and the
   run is a dataset about the old dodge.
2. `PHASE 3 DODGE DETAIL | ... | course N of M | goal K of M` — **`K` is the new number and it must
   be large.** It counts the frames the goal was the direction you were holding. `goal 0` on a
   session where you were walking means `GameInput.Moving` never reached the solver; `course` near
   `M` means it did not gate anything.
3. `dodge WOULD MOVE | ... | heading ... | goal ...deg (course 1)` — the per-move line. **The goal
   must now follow YOUR hands and not the character's motion.** With the camera turned the goal
   should track the compass direction you are holding; if it tracks the body, §5.1's gate is not
   reaching the adapter.
4. **The feel, which is the only acceptance test that counts.** The dodge should steer *around*
   projectiles on the way to where you are walking, not take the wheel. And watch the magnitude:
   before this change three-quarters of its moves were a third of a step, and the `scale` on the
   `WOULD MOVE` lines should now read 1.00 on most of them.

**And the two questions no replay can answer**, in the operator's own terms:

- **Does it go where you are walking?** `follows the player` against `overrides intent` on the
  replay of the new trace, and then by feel.
- **Is a full step too much?** §13.4's `both` column is the trade: magnitude recovered, deflection
  up on `p26156` and `p5424`. If it feels like it is being flung, the fix is `FindHeading` — the
  one-function revert is quoted in §13.2 — and the question is settled by playing it, not by
  arguing about a mean.

---

## 14. The decision window was a constant number of milliseconds, and therefore a speed-dependent distance

**Written 2026-10-15, on the operator's report after playing §13.** Two problems were named, and this
section is the first one:

> *"A prior change was making only part of the projectile path dangerous, I think this was arbitrarily
> set to just some 'safe' distance in front of it. This distance is oftentimes way too safe, it needs
> to scale with projectile speed."*

That is `MoveTemporalWindowSeconds`, and the diagnosis is exactly right: it was **0.05 s, one constant
for every projectile in the game**, which makes it a distance of `0.05 * bullet_speed` — and §14.2 is
the measurement of how far apart the two ends of that are.

### 14.1 Where the partial path was, and what set its length

`DodgeSolver.PlanRefusal` is rule 6 (the temporal rule) translated to the plan model. It walks the
plan and the bullet together and then refuses a candidate under two clauses:

```csharp
float windowMs = _config.MoveTemporalWindowSeconds * 1000.0f;   // the defect: 50.0, always
...
if (dangerMs <= windowMs) { return (int)AdmissionRule.TemporallyClear; }   // inside the window
// beyond it: one-sided -- refuse only if this move gets CLOSER to the bullet than standing still
```

The first clause is the "arbitrarily safe distance in front of it": **inside the decision window the
candidate is refused outright, whatever else is true.** §11.2 introduced that form and said why —
*"Inside the decision window (50 ms) is refused outright — that is what `hitDuringTheFrame` was"* —
and 50 ms came from §3.5, where the question was whether a bullet arriving **this frame** was
distinguishable from one arriving in 500 ms. As a frame-sized constant that was defensible. What was
never examined is that the same 50 ms is also a *distance*, and the distance is different for every
projectile in the game.

### 14.2 The measurement: one constant, two wrong answers

`tools/projectile_speed_report.py` (new) reads every projectile in a trace and reports its near-field
speed and its collision disc. Over the five recordings, **15 360 projectiles**:

| run | speed p50 | disc (`player + bullet`) | in 50 ms | **protected / disc** |
|---|---|---|---|---|
| `p20152` | 16.0 u/s | 0.41 | 0.80 | **1.95x** |
| `p5824` | 16.0 u/s | 0.41 | 0.80 | **1.95x** |
| `p26684` | 16.0 u/s | 0.41 | 0.80 | **1.95x** |
| `p5424` | 16.0 u/s (max 29.7) | 0.41 | 0.80 | **1.95x**, up to 2.52x |
| **`p26156`** | **6.0 u/s** | **0.51** | **0.30** | **0.53x** |

**The fixed window is nearly twice the collision disc on four recordings and barely half of it on the
fifth** — and the fifth is `p26156`, the session §13 and this section are measured against, and the
one whose `scale 0.35` habit §13.2 called *"a dodge that has stopped walking."* A window that does not
cover the disc under-refuses on exactly the frames where the safest move is available; one that
protects twice the disc over-refuses, which is the operator's "way too safe". **The operator's
sentence is right in the direction that matters and, on the recordings available, the fixed 50 ms was
also too SHORT on the slow session** — which is the larger of the two errors, because it is the one
that lets the player be hit.

### 14.3 The fix: the window is a multiple of the disc-crossing time

The quantity that is the same kind of thing for every projectile is not a duration and not a distance
— it is **how long this bullet is a threat to this player's body**, `disc / v`. The window is now:

```
windowMs = max( DecisionDiscMultiplier * (disc / speed) * 1000,
                MoveTemporalWindowSeconds * 1000 )
windowMs = min( windowMs, PlanLookaheadMs )        // see 14.4
```

- **`DecisionDiscMultiplier`** is the new knob, **default 2.0** — two disc-crossings. It is unitless,
  so it means the same thing against every projectile in the game.
- **`MoveTemporalWindowSeconds`** (0.05 → **0.0**) survives as an absolute floor under it and is
  non-binding by default. It is the off-switch as well as a knob: `disc=0, window=0.05` is the old
  behaviour **exactly**, which is how the change is A/B'd against the recordings below.
- **The speed is measured, not assumed.** A `Trajectory` carries no velocity field; the speed is the
  distance between the first two `Alive` knots over the sample step, in the same spirit as the course
  and the player's own speed. It is the near-field speed, not an average — a shot that decelerates
  into its tail would drag a whole-path average down.
- **It fails to the old behaviour.** No samples, one sample, a dead first knot, an unread radius or a
  zero speed all fall back to the floor. A missing measurement must not be able to make the window
  the whole lookahead.

### 14.4 The clamp, which the measurement found rather than the design

The first version had no ceiling, and `--decisions` caught it immediately: **`p5824` produced a
30 720 ms window** from one 0.5 u/s projectile. `PlanWalk` only ever looks `PlanLookaheadMs` (500 ms)
ahead, so a window longer than that is `dangerMs <= infinity`: the clause stops being a threshold and
becomes an unconditional refusal of every candidate that touches that disc — **the pre-one-sided
behaviour coming back, on exactly the frames where a parked projectile is nearby.** The window is now
clamped to the lookahead, which is the largest value at which the clause still means something.

The clamp is not free of consequence and it is worth stating: on `p26156` the disc-crossing time is
~95 ms, so `disc=2` reaches 190 ms for a median projectile and **clips at 500 ms for the slowest half
of them** (p50 500 ms). Past the clamp, a larger multiplier cannot relax anything further. Anyone
tuning for the slow end of the projectile set should revisit `PlanLookaheadSeconds`, not this knob.

### 14.5 The numbers

Five recordings, the same solver, `--config` only. **`old`** is `disc=0,window=0.05` — the shipped
fixed window, reproduced on the new binary. **`new`** is the shipped `disc=2`.

| run | rule 6 window p50 | window refusals / all refusals | moves | mean deflection | median | admissible (mean) |
|---|---|---|---|---|---|---|
| `p20152` | 50 → **127.8 ms** | 9142 → **24893** of 161 805 | 2073 → 2074 | 54.8° → 54.9° | 45.0° → 45.0° | 46.9 → 46.0 |
| `p5824` | 50 → **100.1 ms** | 15656 → **33317** of 120 459 | 2489 → 2487 | 59.6° → 59.3° | 45.0° → 45.0° | 52.3 → 51.7 |
| `p26684` | 50 → **125.3 ms** | 29943 → **87136** of 318 955 | 5041 → 5040 | 70.9° → 71.0° | 60.0° → 60.0° | 51.4 → 51.0 |
| `p5424` | 50 → **157.3 ms** | 9026 → **21511** of 110 624 | 1140 → 1141 | 82.5° → 83.9° | 79.3° → 80.2° | 50.3 → 49.0 |
| **`p26156`** | **50 → 500 ms** | **18803 → 70574** of 163 644 | 1665 → 1666 | 51.0° → **51.6°** | 30.0° → 39.6° | 48.6 → 46.3 |

**Every pre-existing invariant holds on all five recordings and all six scenarios, and the six
scenarios' move counts are identical to §13's.** `decide()` at `--repeat 50` on `p26156`: **p50 34 µs,
p99 119 µs, max 247 µs** — the p99 went 111 → 136 µs when the window was computed per candidate and
came back when it was cached per threat, which is §4.4's lesson applied before it cost a session.

**The honest reading is that the replay barely moves, and the reason is the metric rather than the
fix.** The window clause fires on 6–13 % of candidate refusals, and `disc=2` raises its count by 2–4x
— on `p26156` by **3.8x**, which is the 4x the window actually grew for a 6 u/s projectile. But rule 6
has two clauses and the decision only sees their union, so **a rule that refuses more candidates does
not change the winner unless it refuses the winner**; move counts move by 1–2 frames out of thousands.
What the change establishes is that the threshold is now the same *kind* of quantity at every speed,
and that its share of the refusals is a number rather than an assumption.

**`p5424` and `p26156` are the two that worsen** (mean deflection +1.4 and +0.6 degrees) and both are
within §13.4's existing trade — §13.2's full-magnitude steps travel further off the line, and the
window lets marginally more of them through. Nothing here is separable from that by replay.

### 14.6 The instrument, so the window cannot go back to being invisible

A per-threat quantity nothing reports is a per-threat quantity that will be believed wrong. Three
things now print it:

- **`SolverHarness --decisions`** gained a **`windowMs`** column, and `tools/decisions_report.py`
  reports it as `rule 6 window, evaluating: p10 / p50 / p90 / max`. A file with an older header is
  still read — the dump's own header is the contract, not the reader's field list.
- **`SolverHarness`** prints **`rule 6 window : N candidate refusal(s) were the unconditional 'inside
  the decision window' clause, of M refusal(s) in total`**, and says so in words when the clause
  never fired at all. This is `_windowRefusals`, counted **per candidate** so that it is in the same
  unit as the total it is printed against; the first version counted per (candidate, threat) and
  reported 248 590 refusals out of 159 207, which is the kind of number that discredits everything
  printed beside it.
- **`DodgeConfig.DescribePlan()`** carries `disc=Kx floor=SECs`, so a live run's own log says which
  window it ran. The line is 147 characters typical, 179 worst case, against §12.7's 256-byte native
  limit.

### 14.7 The startup surface, which is what the operator asked for

`--dodge-disc K` and `--dodge-window SEC` on the launcher, `DRELAY_DODGE_DECISION_DISC` and
`DRELAY_DODGE_WINDOW_S` on the hook, and `--config disc=K,window=SEC` on the harness. All three are
the same two numbers and all three are documented at their point of use; the launcher does not
duplicate the default (`-1` means "not set") because a default living in two assemblies is a default
that will disagree with itself.

```powershell
# the shipped behaviour
.\hook\tools\Run-Phase3Session.ps1 -Experiment Hold -Seconds 180 -Dodge

# one disc-crossing instead of two -- a tighter near-field refusal
hook\out\Drelay.Launcher.exe --dodge --dodge-disc 1

# exactly the old fixed 50 ms, for an A/B on a live session
hook\out\Drelay.Launcher.exe --dodge --dodge-disc 0 --dodge-window 0.05
```

### 14.8 What this does not establish

- **Nothing about whether 2 is the right multiplier by feel.** The sweep (`disc` 0, 0.5, 1, 2, 4, 8)
  is monotonic and bounded and no value breaks an invariant, but 2 is chosen as **one disc-crossing of
  path either side of the disc** and defended on those grounds rather than measured as best.
- **Nothing about safety.** The recordings are replays of positions the old dodge produced, so a
  change to which candidates are admissible is measured in a world that change did not make. T5, T6
  and T10 hold; T7 is unchanged (it could not be otherwise — the trajectory model and the collision
  observation are untouched).
- **Nothing about whether the operator's "too safe" is what this fixes.** §14.2 measures the fixed
  window as `1.95x` the disc on four sessions and `0.53x` on the fifth, so it was too *loose* on four
  and too *tight* on one. The change is right for both, but a session on which the feel was "too safe"
  should be re-measured after this before the complaint is called answered.
- **Nothing about the clamp's cost.** Clipping at `PlanLookaheadMs` means the tuning range for the
  slowest projectiles is gone — on `p26156` `disc=2` and `disc=8` produce the same p50 window. The
  lookahead is now part of this knob's effective range and has not been swept.


## 15. The dodge outran because nothing could see past one step

**The defect, in one sentence:** every candidate was one step scored in isolation, so on the frames
where the player is walking away from the shooter "away from the shooter" and "along my own line" are
the same direction, the safety terms pay for the distance, and the dodge retreats — on **40–50 %** of
its own moves on four of the five recordings (`HANDOFF-DIRECTED-DODGE.md` §3).

**The change:** a bounded `A*` over the client's own collision grid, once per frame, with a spacetime
danger cost (`PathPlanner.cs`). It produces a **waypoint**, and each already-admissible candidate is
scored by its alignment with that waypoint (`WeightPath`, 1.00). It never admits, never refuses and
never reaches the input — the admission rules and their doctrine are untouched. `DRELAY_DODGE_PATH=0`
restores the old score exactly.

**The result is partial and is reported as such.** §15.3 is the honest table: on `p26156` — the slow-
projectile session every other number in this document is measured against — OUTRUN falls **30.5 % →
26.4 %** and SIDESTEP rises **48.8 % → 49.1 %**, which is the handoff's acceptance test met on the
recording it names. On the other four the change is a **wash, and on two of them slightly negative**.
The path is kept because it is bounded, cheap, invariant-clean, off-switchable, and correct on the
session that has a usable measurement — **not** because the objective is met. §15.6 says what the
objective actually needs, and the term breakdown in §15.5 is why.

### 15.1 What was built, and the two design errors it took to make it work

| piece | what it is |
|---|---|
| `PathPlanner.cs` | the search: 8-connected A* on the client's grid, distance + spacetime danger, node cap 1200, all buffers pre-sized |
| `DodgeConfig` | `WeightPath` plus seven path knobs, all on `DRELAY_DODGE_PATH_*` |
| `DodgeAdapter` / `TraceRecorder` | the grid window grew from **24 to 64 cells each way**, so a path has somewhere to go and so the harness searches the same world the hook does |
| `SolverHarness` | six `--decisions` columns (`wp`, `wpdist`, `pathn`, `pathlen`, `pathcost`, `pathwhy`), a `--config` key per knob, a `--frames-from` set, and a per-frame `chosen:` line with every score term |
| `tools/dodge_direction_report.py` | now takes the decision directory as an argument (it was hardcoded to a directory no capture writes) and reports the clearance the chosen step gave up against the best heading available |
| `tools/dodge_term_report.py`, `hook/tools/Capture-DodgeTerms.ps1` | the term breakdown: which of the ten score terms chose the direction the dodge took |

**Two errors, both found by measurement rather than by review, and both worth recording:**

1. **The waypoint was the first path node beyond one step — which is the grid's rounding error.** The
   handoff's §4.3 suggests exactly that, and a path node is one world unit wide while a step is
   `budget` ≈ 0.14 units, so the waypoint was the first cell centre and alternated sides of the goal on
   adjacent frames (`p26156` frame 26 `wp 60`, frame 27 `wp −60`, both against goal −135). **The
   instrument that caught it was `wpdist` in the new column** — 0.5 units, when the path is 10 units
   long — and the fix was to take the waypoint a *fraction of the path's own length* out.

2. **The path's danger cost was zero on 90 % of frames, and the reason is geometric.** `pathcost −
   pathlen` is the danger the route paid, and it was 0 at p50 and p90 even at a danger weight of 20.
   A path that detours only at its far end **points at the goal for its whole first half**, so a
   waypoint near the player is the goal term wearing a pathfinder's name. This is why the fraction
   (0.75 of the path) is the knob that matters and the absolute floor (2.5 units) is only a floor.

**And one configuration was measured and thrown away.** The first version cut `WeightGoal` from 0.90 to
0.30 whenever the path was on, on the reasoning that the two terms would agree wherever the goal was
walkable. It made the dodge outrun **more** (33.6 % against 30.5 % on `p26156`), and the goal term is
put back at 0.90 in the shipped defaults. The comment recording this is at the assignment in
`DodgeConfig.Default`.

### 15.2 The headline: the dodge's own direction, per move

`tools/dodge_direction_report.py`, all five recordings, the dodge's own chosen heading classified
against the line from the nearest threatening muzzle to the player. **Before** = `path=0` on the
current build, which reproduces §3's table exactly — that reproduction is the control, and it is why
the before/after columns are trustworthy.

| run | | OUTRUN | SIDESTEP | TOWARD | gave up >0.10 u of clearance |
|---|---|---|---|---|---|
| `p20152` | before | 552 (46.3 %) | 560 (47.0 %) | 80 (6.7 %) | 198 (16.6 %) |
| | **after** | **595 (49.9 %)** | **511 (42.9 %)** | 86 (7.2 %) | 194 (16.3 %) |
| `p5824` | before | 702 (47.9 %) | 654 (44.6 %) | 110 (7.5 %) | 190 (13.0 %) |
| | **after** | **687 (46.9 %)** | **617 (42.1 %)** | 162 (11.1 %) | 235 (16.0 %) |
| `p26684` | before | 1449 (49.7 %) | 1226 (42.0 %) | 242 (8.3 %) | 384 (13.2 %) |
| | **after** | **1382 (47.4 %)** | **1206 (41.3 %)** | 329 (11.3 %) | 446 (15.3 %) |
| `p5424` | before | 351 (40.6 %) | 469 (54.3 %) | 44 (5.1 %) | 118 (13.7 %) |
| | **after** | 352 (40.7 %) | **402 (46.5 %)** | 110 (12.7 %) | 166 (19.3 %) |
| **`p26156`** | before | 506 (30.5 %) | 809 (48.8 %) | 343 (20.7 %) | 169 (10.2 %) |
| | **after** | **438 (26.4 %)** | **814 (49.1 %)** | 406 (24.5 %) | 195 (11.8 %) |

Read it without the emphasis and it says: **the inversion the task asked for is achieved on one
recording of five.** OUTRUN exceeds SIDESTEP on three of the four older ones before and after, and the
change moves it in the right direction on two of those (`p5824`, `p26684`) and the wrong direction on
one (`p20152`). `p5424`'s SIDESTEP falls seven points while its OUTRUN is unchanged, which is the
worst single row in the table and is not explained by the other columns.

### 15.3 `p26156`, where the change does what it was meant to do

| | OUTRUN | SIDESTEP | TOWARD |
|---|---|---|---|
| the goal cut to 0.30, path on | 33.6 % | 46.3 % | 20.1 % |
| path off, goal 0.90 (baseline) | 30.5 % | 48.8 % | 20.7 % |
| **path on, goal 0.90 (shipped)** | **26.4 %** | **49.1 %** | 24.5 % |
| path on, `wpath=2` | 32.3 % | 40.5 % | 27.2 % |
| path on, `wpath=3` | 34.4 % | 38.7 % | 27.0 % |

The path term earns its weight here **(−4.1 points of OUTRUN)** and over-weighting it destroys the
effect, which is the shape of a preference that is doing something specific rather than a knob that
trades one defect for another. TOWARD rises from 20.7 % to 24.5 % — the goal term pulling the dodge
back toward the shooter's line — and the clearance-regret column rises from 10.2 % to 11.8 %: **26 more
frames of the 1658 choose a heading that gives up more than a tenth of a unit of clearance against the
best heading available.** That is the price, and it is small but it is not zero.

### 15.4 What it does not break

- **All six synthetic scenarios: green**, and **every move count identical to the baseline** except
  none — `peaceful` 0, `headon` 52, `dodge` 28, `volley` 0, `corridor` 2, `passingby` 20, which are the
  pre-change numbers to the frame. The scenarios carry no grid, so the planner reports `nogrid` on all
  900 frames of each and the solver behaves exactly as it did. **That is the degradation path being
  exercised by every scenario run rather than by an argument.**
- **T5, T6, T10 and F hold on all five recordings.** T7 fails on all five exactly as it did before the
  change (§7's baseline: the same false positives, `p26156` with zero resolved collisions). No T7
  number moved.
- **The frame budget is fine.** `decide()` p99 **104 µs** at `--repeat 50` on `p26156`, against 119 µs
  before the path and a 300 µs budget shared with the sampler and the sensor. The path's own share is
  **6.8 µs per frame inside `Plan`**, every one of 7169 frames producing a path, mean 13.9 nodes
  expanded and 9.8 units long, **node cap hit on 0 frames**.

### 15.5 Why it is only partial, measured rather than guessed

`tools/dodge_term_report.py` aggregates the chosen move's own score terms by the direction it chose.
On `p5424`, 841 frames with a chosen move and a nearby threat, mean weighted contribution:

| term | OUTRUN | SIDESTEP | TOWARD | who it pays |
|---|---|---|---|---|
| resolve | 1.339 | 1.282 | 1.139 | **OUTRUN +0.057** |
| clearance | 0.330 | **0.479** | 0.288 | SIDESTEP +0.149 |
| durability | 0.541 | 0.542 | 0.494 | nobody |
| intent | 0.305 | 0.307 | 0.315 | nobody |
| goal | 0.597 | **0.578** | 0.638 | OUTRUN +0.019 — the goal is axial too |
| path | 0.674 | 0.638 | 0.695 | OUTRUN +0.036 |
| lateral | 0.556 | 0.568 | 0.583 | SIDESTEP +0.012 |
| move | −0.245 | −0.240 | −0.237 | nobody |
| frames | 135 | 392 | 314 | |

**The retreat is paid for by `resolve` and `clearance`, and both of them are right.** A retreat does
increase the distance to a bullet's corridor and does clear corridors the player is inside; that is
what those two terms are *for*. The path term cannot outvote them because on a frame whose goal ray is
walkable and clear, the path is the goal ray — which is why the same table shows `path` preferring
OUTRUN by 0.036.

**And one thing this table deliberately does not claim.** The obvious next move is to move those two
weights, and it was tried: `resolve=1.0,clear=1.8` makes `p5424` **worse** (OUTRUN 40.7 % → 42.1 %),
`resolve=0.8,clear=2.0` worse still (43.2 %), while `resolve=1.2,clear=2.0` is marginally better
(41.1 % against 40.7 %) with SIDESTEP 48.3 % against 46.5 %. Those means are aggregates over frames
with different threat geometry, and the first two rows are the proof that the aggregate is not a
causal model: **raising the weight of the term that favours SIDESTEP in the mean made OUTRUN more
common.** No weight change ships on that evidence.

### 15.6 What this does not establish

- **§15.2's objective was not met by the path alone, and §15.9 is what met it.** The section above is
  kept as written because it is the record of the path being insufficient, and because its diagnosis —
  "the retreat is paid for by `resolve` and `clearance`, and both of them are right" — is what pointed
  at the fix. Read §15.9 for the shipped behaviour; read this section for why the obvious version of
  the fix is not the one that worked.
- **The recordings without a `nohit` key are not comparable on behaviour** (handoff §8 trap 9): their
  threat sets contain the player's own shots, so their OUTRUN/SIDESTEP split is inflated. That does not
  rescue the table — `p26684` and `p5824` move in the right direction and `p20152` does not, on the
  same instrument — but it is why `p26156` is the row to decide on and the others are rows to watch.
- **The wall half of the design is now checked, and the claim in the first version of this section
  was wrong.** It said `corridor` carries no recorded grid, so the wall-avoidance path was untested.
  **It does carry one** — `Synthetic.cs` builds a 64×64 window with `PlanWalls` and `corridor`'s has
  **165 solid cells** — and §15.10 is the check. What that claim was reaching for is real and remains:
  no test asserts that the route is the SHORTEST way round, only that it does not go through.
- **Nothing here says the retreat is wrong on the frames it happens.** A retreat is the safest
  available move on a great many of them — the term table says the safety terms are what pay for it —
  and the case for changing it is the operator's, not the model's. The dodge is *supposed* to run when
  running is the only way out; what §3 measured is that it runs when it is not.
- **The projected-danger term ships at weight 0 and is not a result.** §15.9.2 records what it cost to
  find that out.

### 15.7 The live run this hands to the operator

```powershell
# 1. THE DRY RUN FIRST. The solver decides on every frame and logs every move it would have made.
.\hook\tools\Run-Phase3Session.ps1 -Experiment Hold -Seconds 180 -Dodge

# 2. Read it, with the game closed.
python tools\intent_report.py "$env:LOCALAPPDATA\drelay\hook\<run-dir>"

# 3. AND THE A/B, WHICH IS NOW TWO ENVIRONMENT VARIABLES. Same session, one term off:
#    $env:DRELAY_DODGE_AXIAL = '0'   the axial penalty off -- the §15 behaviour
#    $env:DRELAY_DODGE_PATH  = '0'   the pathfinder off -- the §14 behaviour
```

The `WOULD MOVE` lines now carry the waypoint and the path's own numbers, so "it ran around the
volley" is read off a file. **`bootstrap.log` carries the settings line, which now ends with
`path enable=... target=... cap=... danger=... pad=... wp=.../... dwell=...s`** — a run that does not
print it is a run whose path cannot be identified.

### 15.8 Where every number in this section came from

| number | where |
|---|---|
| every before/after column in §15.2 | `work/dd-after` (path on) and `work/off` (path off), both captured on the current build; `python tools\dodge_direction_report.py <dir>` |
| the control (`path=0` reproduces §3) | `tools\AB-Dodge.ps1` or the one-liner above; the before column of §15.2 is that run |
| the `p26156` sweep rows in §15.3 | `--config path=...,wpath=...,goal=...` on `20261015-153805-p26156`, dumps under `work/ab3` |
| the term table in §15.5 | `hook\tools\Capture-DodgeTerms.ps1 -Recording 20261015-151232-p5424 -Out work\terms-p5424`, read by `tools\dodge_term_report.py` |
| the weight sweep that is reported as a null result | `--config resolve=...,clear=...` on `p5424`, dumps under `work/w` |
| the cost and the path counters | `SolverHarness --repeat 50`, `p26156` |
| the scenario move counts | `SolverHarness --scenario <s> --frames 900`, six of them |
| §15.9's before/after columns | `work/dd-final-axial` captured on the shipped build, and `work/dd-ax1`/`work/dd-axial` for the sweep values |
| the live session's own numbers | `python tools\dodge_direction_report.py --run <run-dir> <dump>`, dumps under `work/live-29780` |


## 15.9 The axial penalty, and the first live session

**The defect §15.5's breakdown pointed at, in one line:** OUTRUN and TOWARD are *the same direction* to
`Lateralness` — it scores both zero — so the dodge fell back on the two terms that can tell them apart,
the safety terms (which pay for the retreat) and the goal (which pays for walking at the shooter), and
it oscillated between two mistakes that are one mistake.

**The change:** `Axialness`, the missing twin of `Lateralness` — the squared cosine between the candidate
heading and the line to the nearest threat — subtracted at `WeightAxial` 1.00. It is **symmetric in the
two axial directions**, and that is the whole of the design: it cannot trade OUTRUN for TOWARD, which is
exactly what raising `WeightGoal` does (§15.3: OUTRUN −6 points while TOWARD +9). It removes the axial
band from the menu and leaves the ring's sixty degrees either side to the goal and the safety terms.

### 15.9.1 The result, on the same five recordings

`tools/dodge_direction_report.py`, shipped defaults, `work/dd-final-axial`. The *before* column is §3's
baseline, which `path=0,axial=0` still reproduces exactly.

| run | | OUTRUN | SIDESTEP | TOWARD | gave up >0.10 u of clearance |
|---|---|---|---|---|---|
| `p20152` | before | 552 (46.3 %) | 560 (47.0 %) | 80 (6.7 %) | 198 (16.6 %) |
| | **after** | **524 (44.0 %)** | **573 (48.1 %)** | 95 (8.0 %) | 200 (16.8 %) |
| `p5824` | before | 702 (47.9 %) | 654 (44.6 %) | 110 (7.5 %) | 190 (13.0 %) |
| | **after** | **511 (34.9 %)** | **777 (53.0 %)** | 178 (12.1 %) | 243 (16.6 %) |
| `p26684` | before | 1449 (49.7 %) | 1226 (42.0 %) | 242 (8.3 %) | 384 (13.2 %) |
| | **after** | **1278 (43.8 %)** | **1372 (47.0 %)** | 267 (9.2 %) | 431 (14.8 %) |
| `p5424` | before | 351 (40.6 %) | 469 (54.3 %) | 44 (5.1 %) | 118 (13.7 %) |
| | **after** | **317 (36.7 %)** | **471 (54.5 %)** | 76 (8.8 %) | 156 (18.1 %) |
| **`p26156`** | before | 506 (30.5 %) | 809 (48.8 %) | 343 (20.7 %) | 169 (10.2 %) |
| | **after** | **411 (24.8 %)** | **827 (49.9 %)** | 420 (25.3 %) | 199 (12.0 %) |

**SIDESTEP exceeds OUTRUN on all five recordings**, which is §1 of the handoff's acceptance test and the
thing §15.2 could not do. OUTRUN falls on every one of them, by between 2.3 and 13.0 points; the
outrun-to-sidestep ratio goes from 0.55–1.18 to 0.50–0.84. TOWARD rises on `p5824` and `p26156` while
falling on `p5424` — the axial penalty removed the retreat and the goal term then has more say over which
*safe* direction remains, which is what §15.3 recorded happening whenever the retreat band closes.

**And it costs less clearance than the path alone did.** Against §15.2's path-only column the
clearance-regret figure improves on three of the five (`p26684` 15.3 % → 14.8 %, `p5824` 16.0 % →
16.6 % is worse, `p5424` 19.3 % → 18.1 %, `p20152` 16.3 % → 16.8 % is worse, `p26156` 11.8 % → 12.0 %
is worse) and against the *baseline* it is worse on all five. The dodge is choosing moves that give up
a little more distance-to-corridor in exchange for not retreating, and that is the trade the operator
asked for; it is recorded here so the next session does not have to rediscover it.

**All six scenarios green with move counts identical to the baseline** — `peaceful` 0, `headon` 52,
`dodge` 28, `volley` 0, `corridor` 2, `passingby` 20 — and T5, T6, T10 and F green on all five
recordings, T7 failing on all five exactly as §7's baseline has it.

### 15.9.2 The projected-danger term, which ships at zero because it was measured

§15.5's own conclusion was that the retreat is chosen by the safety terms and that a direction
preference cannot outvote them, so the term to build was one about **the state a candidate leads to**:
project the candidate's velocity over `ProjectedSeconds` and read the planner's spacetime danger field
along it. It is implemented (`DodgeSolver.ProjectedDanger`), it is in the settings line, and
`DRELAY_DODGE_W_PROJECTED` ships it at **0**, for a reason worth recording: **on these recordings the
retreat genuinely does escape**. The bullets that produce the outrun are the ones the character is
already at the edge of, so by the time the projected path reaches a point, the bullet has passed
through it — the field reads zero for the retreat *and* for the sidestep, and the term changes nothing.

Its first version also had a defect the sweep caught in the way this project keeps catching them: the
field is non-zero only within about 0.9 units of a bullet's body, so a probe at a single point read
zero everywhere and the sweep over the weight (0, 0.9, 1.5, 2.5) produced **four byte-identical
direction histograms**. The fix was to probe a 3×3 patch at the collision radius, which is the honest
form of the question. The term then moves a handful of moves and not the histogram, which is the
measurement that says the *idea* is the problem and not the implementation.

### 15.9.3 The operator's live session — the acceptance test

`C:\Users\isvac\AppData\Local\drelay\hook\20261015-211538-p29780`, **Live**, `dryRun=False`, 11113
frames, 59 resolved collisions, and the operator's verdict in their own words:

> *"The feel of the dodger is way better, it feels like it fights you less. Slightly more hits this
> iteration, but that should be fine, can be finetuned later."*

**Read the build that session ran, because it bounds what it proves.** Its settings line reads
`weights resolve=1.5 clear=1.2 durable=0.6 intent=0.4 goal=0.9 path=1 heading=1 lateral=0.8 move=0.25
stand=0.15 hyst=0.12/0.1s dryRun=False` — **no `axial=` field**, because the running DLL predates it.
So the session validates **§15's path**, not §15.9's axial term; the axial term's own live verdict is
still owed. That is the same trap §3.9 and §3.11 record about identities taken for granted, and it is
why the settings line prints every weight.

**What the session does establish, measured on its own trace** (`path=1` replayed, 1451 moves of 11869
frames, 1421 with a nearby threat):

| | OUTRUN | SIDESTEP | TOWARD |
|---|---|---|---|
| the session's own trace, path on | 557 (39.2 %) | 599 (42.2 %) | 265 (18.6 %) |
| the same trace, path off | — | — | — |
| the same trace, **axial 1.0** | **512 (36.0 %)** | **681 (47.9 %)** | 228 (16.0 %) |

and the intent-agreement side, which is what "fights you less" is about: `overrides intent` **11.6 %**
of moving frames at axial 1.0 against 9.4 % at axial 0, `follows the player` **87.9 %** against 90.6 %,
`stutter` 1.6 % against 1.2 %. The axial penalty makes the dodge intervene slightly *more* often and
slightly further from the held direction — which is the same trade §15.3 measured, and the operator's
verdict on the path-only build is the evidence that the trade is the right one.

**And the frame budget, measured properly.** A single-shot harness run of this trace reports
`decide() p99 534 µs`, which is **the harness's own garbage collector and not the solver**: the harness
allocates a snapshot per frame. At `--repeat 20`, best-of-20, the same trace reads **p99 155 µs all
frames and 211 µs on evaluating frames**, against 111/167 µs with the path off — so the pathfinder's
own share is **36.8 µs per frame**, and the whole decision is inside the 300 µs the sensor and the
sampler share. The live run's own log agrees: `p50 <= 50 µs, p99 <= 300 µs`. **This is the 128×128
grid with real walls**, where the mean search expands 67 nodes and runs 15.3 units (against 14 nodes and
9.8 units on the recordings), which is why the path costs five times more live than offline.

### 15.9.3b The axial build's own live dry run — and its cost is the real caveat

`20261015-212602-p440`, **DRY RUN**, 2219 frames, 2159 paths, and its settings line is the first live
one to carry the term: `... goal=0.9 path=1 axial=1 projected=0 heading=1 ... lookahead=0.8s x3`. A dry
run decides on every frame and moves nobody, so — the handoff's §6 rule — it is evidence about the
CHOICE and not about the displacement. Measured on its trace:

| | OUTRUN | SIDESTEP | TOWARD | overrides intent | follows the player |
|---|---|---|---|---|---|
| **axial 1.0** | **189 (40.8 %)** | **235 (50.8 %)** | 39 (8.4 %) | 9.5 % | 90.1 % |
| axial 0 | 223 (48.2 %) | 194 (41.9 %) | 46 (9.9 %) | 4.8 % | 95.0 % |

**The inversion holds on a live session's own geometry** — 48.2/41.9 with the term off, 40.8/50.8 with
it on — and the cost of it is visible in the same row: `overrides intent` **doubles**, 4.8 % → 9.5 %.
On this session the dodge intervenes against the held direction on one moving frame in ten instead of
one in twenty, which is the trade §15.3 and §15.9 name and the thing to judge by feel rather than by a
ratio. `corner cuts through a wall: 0 over 2159 path(s)`, and F, T5, T6, T10 and PATH all pass; T7 fails
on its 20 resolved collisions as every session does.

**AND THE FRAME BUDGET IS THE THING TO FIX NEXT, not a footnote.** At `--repeat 20`, best-of-20:

| trace | `decide()` all frames p99 | evaluating p99 | evaluating p50 | Plan share |
|---|---|---|---|---|
| this dry run, 128×128 grid, 27 solid cells | 244 µs | **719 µs** | 87 µs | 31.1 µs |
| the acting session's trace, §15.9.3 | 155 µs | 211 µs | — | 36.8 µs |
| `p26156` (48×48 grid, no walls) | 131 µs | 147 µs | 50 µs | 6.7 µs |

**The p99 of the evaluating frames is 719 µs against a 300 µs budget**, and it is NOT the pathfinder:
`axial=0` reads 692 µs on the same trace and the `Plan` share is 31 µs either way. The p50 is 87 µs, so
this is the tail and not the typical frame — a few frames where the candidate set is at its fullest and
each of 77 candidates runs the plan walk against every threat in the set. It is inside the 16.7 ms frame
by a factor of twenty so nothing is dropping frames, but the 300 µs this document has been quoting is
the budget *solver plus sensor plus sampler share*, and this trace shows the solver alone exceeding it
on one evaluating frame in a hundred. **§15.11 is the task that leaves — and §15.11 measures it and
withdraws the caveat, because the tail is the harness's own collector and the solver's mean decision on
this trace is 55.5 µs.** Read §15.11 before acting on this paragraph.

### 15.9.4 What §15.9 does not establish

- **Nothing about the axial term live.** The operator's session predates it. The next command is the
  same dry run with `DRELAY_DODGE_AXIAL=0` and `=1` on two sessions and the direction report over both.
- **Nothing about the extra hits.** The operator reports "slightly more hits"; this trace has 59
  collisions and 49 collisions the model did not predict (its `false negatives` line), and the replay
  cannot attribute either to the dodge, because the positions in the trace were produced by the build
  that ran. A hit count is a paired-trial question and `TIMING.md` §8 item 4 is where it is defined.
- **Nothing about the `node cap hit` count going up.** On the live trace `pathcap=400` hits its cap on
  5.7 % of frames and buys 4 µs; the cap ships at 1200 where it never fires. The knob is swept, not
  resolved.
- **The scenarios carry walls and the path goes round them.** See §15.10; the earlier claim here that
  they carry no grid was wrong.
- **What §15.10 does not establish.** It asserts the route never cuts a corner through a wall; it does
  not assert the route is the SHORTEST way round, and nothing checks the path against the *client's*
  walkability for a route longer than the 64-cell window it was searched in.
- **Nothing about whether 1.00 is the axial term's best weight.** It was swept on two recordings
  (0, 0.5, 0.8, 1.0, 1.5, 2.0, 3.0): `p5424` and `p20152` both improve monotonically to 2–3, while
  `p26156` is *best* at 1.0 and degrades above it (`wpath`/`axial` interaction). 1.0 is the value that
  inverts the split on all five, which is the objective, and not the value that minimises OUTRUN on any
  one of them.


## 15.10 The path goes round walls, and that is now a checked number

**The claim this section exists to stop being an inference.** `PathPlanner` searches the client's own
collision grid with A*, 8-connected, and the diagonal rule — a diagonal step is allowed only when both
its orthogonal neighbours are walkable — is the single part of that file which can put a route *through*
a wall. Nothing asserted it. What existed was a shape in the numbers: the `corridor` scenario's paths
expand 52 nodes and run **24.7 units**, against `dodge`'s 13 nodes and **12.0 units** on the same 64×64
window. That is *consistent* with going round and it is not evidence of it — and this document's own
recurring lesson is that a number nobody can read off a file is a number nobody can trust.

**The instrument:** `PathStats.WallCrossings`, incremented on the path a search actually returned, for
every step whose two cells differ on both axes while **both** of its orthogonal neighbours are solid.
It is asserted as an invariant — `PATH no planned route cuts a corner through a wall` — beside T6,
because it is the same kind of claim: T6 holds the *emitted move* to the game's own geometry, and this
holds the *route the move was chosen from* to it.

| input | paths found | corner cuts |
|---|---|---|
| `corridor` (165 solid cells) | 900 | **0** |
| `peaceful`, `passingby` | 305 each | **0** |
| `dodge`, `volley`, `headon` | 900 each | **0** |
| `p20152` / `p5824` / `p26684` / `p5424` / `p26156` | 6126 / 9895 / 21144 / 7923 / 7083 | **0** |

**52 171 paths on the recordings and 3 910 on the scenarios, and not one corner cut.** The assertion
passes on every input the project has; a regression that lets a diagonal through a corner would fail it
on `corridor` first, which is the scenario that has walls in it.

**And the honest half.** This asserts a route never goes through a wall. It does **not** assert the
route is the shortest way round, it does not compare the planner's route against the client's own
pathfinder, and the search is bounded by a 64-cell window on the scenarios and 128 on a live map — a
route that would need to leave that window is not found and is not tested. `corridor`'s 24.7-unit paths
versus `dodge`'s 12 is the shape of a detour; "it is the detour the client would have taken" is not
claimed anywhere and would need a live comparison.


## 15.11 The decision's tail, attributed — and the 300 µs caveat withdrawn

**The question §15.9.3b left open:** the field report's dry run reads `decide()` p99 **719 µs on
evaluating frames against a 300 µs budget** with a p50 of 87 µs, the pathfinder exonerated both by its
own self-clock (31 µs) and by `axial=0` (692 µs), and nothing in the project able to say which of the
admission and the scoring walk the tail was in. **The instrument is now `DodgeSolver`'s stage clock** —
six buckets (`setup`, `path`, `gates`, `admit`, `score`, `escape`) stamped by a wrapper around
`DecideCore`, so no exit path can escape them — reported cumulatively by the harness, written per frame
into the `--decisions` dump as six columns, and read by `tools/dodge_stage_report.py`.

**The answer, on that same trace at `--repeat 20`, worst 1 % of evaluating frames against the rest:**

| stage | worst 1 % | the rest | ratio |
|---|---|---|---|
| `admit` | **1187.8 µs** | 54.6 µs | **21.8×** |
| `score` | 407.4 µs | 23.9 µs | 17.1× |
| `escape` | 116.6 µs | 4.9 µs | 23.8× |
| `path` | 88.4 µs | 36.3 µs | **2.4×** |
| `gates` / `setup` | 0.0 µs | 0.0 µs | — |

**It is admission and it is not the pathfinder.** The path search is 2.4× on the slow frames against
admission's 21.8×, and on the very worst frame of the trace (`frame 90`, 2982 µs) the path bucket is
**48 µs and the admission bucket is 2934 µs**. Two runs of the instrument agree on every ordering, and
the slowest frames are the contiguous one-third of a second around `frame 90-111`, which is a burst
rather than a compile.

**AND THE CAVEAT IN §15.9.3b IS WITHDRAWN, because the instrument shows it was a measurement artefact
and not a solver cost.** The per-frame stage totals, over all 2219 frames: the worst single decision is
**775 µs** (649 of it in `admit`, 138 in `score`) and the mean is **55.5 µs/frame**. The harness's
best-of-N estimator reports a p99 of 716 µs and a max of 812 µs on the same run, and its `decide()`
maxima **cannot contain** the 2.6 ms a collection would cost — so the multi-millisecond samples are the
harness's own collector landing inside one repetition of twenty, which is the same artefact §15.4
recorded for the single-shot `p534 µs` on the acting session's trace.

**The per-frame mean, which is the number to hold against the budget:**

| trace | `path` | `admit` | `score` | `escape` | total, every decision |
|---|---|---|---|---|---|
| the field dry run, 128×128 grid | 32.4 µs | 15.5 µs | 6.3 µs | 1.3 µs | **55.5 µs** |
| `p26156` (48×48 grid) | 6.7 µs | — | — | — | — |

**So the honest statement of the frame budget is this**, and it replaces the one §15.9.3b wrote:
the solver's mean decision is **55.5 µs on the most demanding trace the project has**, of which the
pathfinder is 32.4; the worst individual frame is 775 µs; and the p99 the harness prints is an
estimator whose tail is dominated by the harness's own allocation, not by the solver. Against 16.7 ms
of frame the solver is 0.3 % of the budget and its worst frame is 4.6 %. **The 300 µs figure is not
exceeded by anything that was measured here, and §15.9.3b's "one evaluating frame in a hundred" was a
statement about the instrument.**

**What is still worth fixing, and it is now cheap to state.** The worst frame's 649 µs is in `admit`,
which is `BuildAndAdmit` — 77 candidates against the frame's threats, with the per-threat envelope
caching only ever used to *skip* a test and never to bound the work. A burst of frames where the
candidate set is full and the threat set is large is where that lands. It is a real tail and it is not
close to the budget; the next session should decide whether a 775 µs worst frame is worth a cache,
rather than inheriting a caveat that was never true.







