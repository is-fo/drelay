# Handoff — the dodge stops a walking player, and gets stuck near a slow projectile

**Written 2026-10-16, for a session with no context.** This is the third version of this document.
**The first two tasks are done** — the directed dodge (§13/§14 of
[SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md)) and the outrun it replaced (§15, closed by the
operator on 2026-10-16) — and §0 below is the one-paragraph summary of each that a new session needs.
The work this document now asks for is the operator's verdict on §15, which named the next defect in
the same breath as it closed the last one:

> *"They seem pretty similar, keep the code as is right now. I think the real problem is that the
> player's intent is ignored when it's trying to dodge something; this sometimes results in the player
> being stuck next to a slow-moving projectile, not being able to move in any direction. What should
> happen is either 1) pathfind around the projectile or 2) let the player walk away from it. The latest
> run was pretty good though — I did a quite challenging boss no-hit, so the dodger works well."*

**Read §3 before anything else: the defect is already measured, and part of it is measured wrongly.**
The dodge halts a player who is holding a direction on **18–31 % of their walking frames**, and **the
replay harness cannot see it** — replaying the same trace with `standStops` on and off produces
byte-identical numbers, because a replay feeds the solver recorded positions and `stand`'s cost is
displacement. That trap is the first thing to understand in this document.

---

## 0. What is already done

> **The outrun objective: CLOSED BY THE OPERATOR on 2026-10-16.** The path (§15.1–§15.4) plus the axial
> penalty (§15.9) put **SIDESTEP above OUTRUN on all five recordings** — `p20152` 44.0/48.1, `p5824`
> 34.9/53.0, `p26684` 43.8/47.0, `p5424` 36.7/54.5, `p26156` 24.8/49.9 as OUTRUN/SIDESTEP — with six
> scenarios green, every asserted invariant holding, and `decide()` p99 147 µs against a 300 µs budget.
> **The operator's verdict: *"the latest run was pretty good, I did a quite challenging boss no-hit, so
> the dodger works well."*** `tools/Test-DodgeObjective.ps1` re-checks all of it in one command and
> reports `OBJECTIVE: 18 of 18 check(s) hold`. **The code stands as it is** — `WeightAxial` 1.00, the
> path on, `StandStops` true — because that is the operator's decision and §15.9.3c is the evidence it
> was made on. Four live sessions measure the axial term: it lowers OUTRUN and raises SIDESTEP in all
> four, exceeds OUTRUN in two of three acting sessions against none with it off, and costs 2.2–3.5
> points of `overrides intent` every time. The live magnitude is a third of the replay's, which is
> §15.9.3c's feedback loop.

| | |
|---|---|
| §5.1 the goal is the player's held direction | **shipped.** The goal came from the body's own course on **1612 of 1665** moves of `p26156`; now **1082** of its 1666 moves come from the player's held direction and 568 from the course. Mean deflection **59.3° → 51.6°**, median **45.0° → 30.6°**. |
| §5.5 the `scale 0.35` magnitude | **shipped.** 1102 of 1102 hysteresis-held moves were at `scale 0.35`; after the fix, 65 of 1665 are, and `scale 1.0` went 544 → 1543. The cause was `FindHeading`'s tie-break re-deriving a held heading at the near radius — see §13.2. |
| §6.10 the `--decisions` dump | **shipped.** `SolverHarness --decisions <path>`, read by `tools/decisions_report.py` and `tools/dodge_direction_report.py`. |
| §4's `Moving` test | **holds, re-verified.** On the 249 frames of `p26156` where the pilot provably wrote nothing, `moving` was true on 158 (63.5 %) and the intent takes the eight compass directions. |
| problem 1 (the partial-path window) | **shipped.** `MoveTemporalWindowSeconds` was a constant 50 ms, which is a *distance* of `0.05 * speed` — 1.95× the collision disc on a 16 u/s projectile and 0.53× of it on the 6 u/s ones. It is now `disc=2` disc-crossings, exposed as `--dodge-disc` / `DRELAY_DODGE_DECISION_DISC` / `--config disc=`. See §14. |
| **problem 2 (this document)** | **MET on all five recordings, and judged live.** §4's path (§15) plus the axial penalty (§15.9); SIDESTEP exceeds OUTRUN on every recording and the operator's verdict is in §15.9.3. Outstanding: the axial term's own live A/B, and the hit count. |

---

## 1. The objective, and what "done" means

**The dodge must stop taking the wheel away from a player who is walking.** Two separable failures, both
named by the operator and both measured in §3:

1. **The halt.** `stand` is a verdict that CLEARS `GameInput.Moving` (`StandStops` is true in the shipped
   config), so on every frame it wins, a player who is holding a direction is stopped dead. It wins on
   **18–31 %** of their walking frames in the three acting sessions. The requested behaviour is either
   of the operator's two: **pathfind around the projectile, or let the player walk away from it.** What
   must not happen is that the character stands still while the player is asking to move and the
   projectile closes.
2. **The stuck frame.** `NoAdmissible` — all 77 candidates refused — has the solver emit *nothing*,
   which is not the same thing and is not the halt. With no command the player's own input stands, so
   these frames are the operator's *"not being able to move in any direction"* only if the player is
   also refused by the world. Measured at 6 frames of 11725 in `p6412` and 0 in the other two acting
   sessions, all with `admissible=0` and `durability 0 ms` — the player is inside a disc. **Establish
   first whether this is the operator's symptom at all**, because the halt is two orders of magnitude
   more common.

Done means all of:

1. **`stand` no longer halts a walking player on the frames where moving is safe.** The measurable
   version: the share of `StandSafe` frames on which the player was holding a direction goes from
   18–31 % to near zero **without** any admitted move being less durable than the stand it replaced.
   Condition on `AdmissibleCount > 0`, because when nothing is admissible there is nothing to emit.
2. **A walking player is never stopped while an admissible move exists that keeps them moving**, and
   when the only admissible move is a bad one the solver says so in a counter rather than by stopping
   them — the same doctrine every other degradation in this solver follows.
3. **Every invariant holds on all five recordings and all six synthetic scenarios**, and **the decision
   stays inside the frame budget** (measured at 55.5 µs/frame mean, 775 µs worst; 300 µs shared).
   **T6 is the one this task can break**, because "never stop the player" invites emitting a move
   admission refused — which is exactly the defect §4's "nothing is admissible" branch records.
4. **The halt rate is an instrument, not an anecdote.** A column that says, per frame, whether the
   decision stopped a walking player and what it stopped them *for*. The operator's symptom is a
   displacement and §8's first trap is that displacement is the one thing a replay does not model.
5. **A live session, judged by feel.** The operator plays, ideally against something with slow
   projectiles; the dodge should feel like it *lets them move*. This is the only acceptance test that
   counts and it is the operator's.

**And it must not break what shipped.** §13's goal change, §13.2's hysteresis fix, §14's decision
window, §15's path and §15.9's axial penalty all live in the same `Decide`, and **the operator's
instruction is that they stay as they are**: "keep the code as is right now". The admission rules remain
the safety boundary (§5.3's doctrine), and nothing in this task may relax rule 4, 5 or 6.

---

## 2. What is already true — do not re-derive these

- **The solver is a pure function of a plain-data snapshot.** `DodgeSolver.Decide(DodgeSnapshot)`
  returns a `DodgeDecision`, touches no game type, and **allocates nothing on the decision path**
  (every buffer is a field, sized once — `EnsureCapacity`, `_results`, `_verdict`, `_windowMs`). The
  same source file is compiled into the hook *and* into `hook/tools/SolverHarness`, so the thing
  tuned offline is the thing that steers. **A pathfinder that allocates a `List<Node>` per frame
  breaks a property this project has measured repeatedly and refuses.**
- **A candidate is a direction the character walks in, tested in spacetime.** `PlanWalk` walks the plan
  and the bullet together on one clock; `PlanRefusal` is rule 6, whose window is now
  `disc=2` disc-crossings (§14).
- **The forward goal exists and is the player's.** `_goalRadians` / `_goalValid` / `GoalSource`,
  resolved once per frame, with a candidate of its own (`Kind == 5`) and a fan either side. The goal
  is the player's held direction when `Moving && IntentValid` (world-converted), the measured course
  otherwise.
- **The collision grid is resolved and windowed.** `CollisionWindow` in `DodgeSnapshot.cs`:
  `Present`, `OriginX/Y`, `Width`, `Height`, `Cells` (row-major `byte[x, y]`), `InWindow`, `At`,
  `Walkable`. Rule 1 (in bounds) and rule 2 (walkable) already read it. §5.1 is what it is and what it
  is not.
- **The player's own projectiles are excluded** before sampling, by the client's own `HitsPlayer` and
  `LocalSeedDerived` (§12 of the spike). Confirmed working live (`no-hit 6977`,
  `ALREADY TOUCHING 0.0 %`). **Do not touch this.**
- **`SolverHarness --config` works**, and `--decisions` writes one line per frame.
  `tools/decisions_report.py` reads it; `tools/dodge_direction_report.py` joins it to a trace.
- **`intent` in the trace is the player's live input**, measured, not assumed —
  `python tools\intent_report.py <run-dir>`. And **camera rotation is correct and measured**
  (`World.cs:12692`, `angle = MoveAngle + (-WorldRotation + aimOffset)`, so the world intent is
  `intent - rot`; the pilot writes `worldHeading + WorldRotation`). Do not re-open either.

---

## 3. The defect, measured

### 3.1 The current defect: `stand` halts a walking player

**The dodge stops the player while they are holding a direction on 18–31 % of their walking frames.**
`stand` is the solver's "the best dodge is no dodge" verdict, and with `StandStops` true it implements
that by **clearing `GameInput.Moving`** — so a frame it wins is a frame the player's hands are ignored
and the character is held still. Read from the `--decisions` dumps of the three acting sessions (the
`moving` column is the player's own live input, §2):

| session | frames the player was walking | `StandSafe` frames (halts) | share |
|---|---|---|---|
| `20261015-212859-p34740` | 6231 | 1123 | **18.0 %** |
| `20261015-214418-p6412` | 8435 | 1982 | **23.5 %** |
| `20261015-214927-p32752` | 5374 | 1689 | **31.4 %** |

**And the replay harness cannot see the consequence, which is the first thing to understand here.**
Replaying any of these traces with `--config standstops=1` and `standstops=0` produces **byte-identical
direction splits** — 1132 moves and 11.5 % overrides either way on `p34740`, 2079/9.9 % on `p6412`,
1087/17.8 % on `p32752`. Not because the policy has no effect, but because **a replay feeds the solver
recorded positions**: a command that clears the player's input changes nothing about where the trace
says they were. **`stand`'s cost is displacement, and displacement is the one thing a replay does not
model** (trap 1 in §8). Any measurement of a fix has to come from a live session, or from a harness that
steps the world forward from its own decisions — which does not exist, and building it is the honest
prerequisite if a fix is to be judged offline at all.

**The stuck frame is a different, rarer failure.** `NoAdmissible` — every one of 77 candidates refused —
makes the solver emit *nothing*: **6 frames of 11725** in `p6412`, **0** in `p34740` and `p32752`. All six
have `admissible=0`, `evaluated=77` and `durability 0 ms`: the player is inside a disc and every move is
refused. Emitting nothing is **not** the halt — with no command the player's own input stands — so those
frames are "cannot move" only if the world also refuses them. **Establish which symptom the operator
means before building anything for the second**: the halt is two orders of magnitude more common and is
measured; the stuck frame is six frames and is not yet shown to be felt.

### 3.2 The previous defect, for context: the dodge outran

**This is the table §15 of the spike closes, and it is kept because the next task must not reopen it.**
`tools/dodge_direction_report.py` joins each frame of the `--decisions` dump to the trace, takes the
nearest projectile whose path comes within 4 units, and classifies the **dodge's own chosen heading**
against the line from that projectile's muzzle to the player:

- **OUTRUN** — along the line away from the muzzle (`cos > 0.5`);
- **SIDESTEP** — across it (`|cos| ≤ 0.5`);
- **TOWARD** — into it (`cos < -0.5`).

| run | dodge moves with a nearby threat | **OUTRUN** | SIDESTEP | TOWARD |
|---|---|---|---|---|
| `p20152` | 1192 | **552 (46.3 %)** | 560 (47.0 %) | 80 (6.7 %) |
| `p5824` | 1466 | **702 (47.9 %)** | 654 (44.6 %) | 110 (7.5 %) |
| `p26684` | 2917 | **1449 (49.7 %)** | 1226 (42.0 %) | 242 (8.3 %) |
| `p5424` | 864 | 351 (40.6 %) | 469 (54.3 %) | 44 (5.1 %) |
| **`p26156`** | 1658 | **506 (30.5 %)** | **809 (48.8 %)** | **343 (20.7 %)** |

**After §15: OUTRUN 44.0/34.9/43.8/36.7/24.8 against SIDESTEP 48.1/53.0/47.0/54.5/49.9 — inverted on all
five, and closed by the operator.** `tools/Test-DodgeObjective.ps1` is the one-command check
(`OBJECTIVE: 18 of 18`). **Do not re-tune it.**

**Why it was what it was, in one sentence, because the diagnosis is what makes the next task's design
legible.** Every candidate was one step scored in isolation: `Lateralness` prefers motion across the
nearest threat and the goal prefers motion along the player's line, and on a frame where the player is
walking away from the shooter **those two agree and the biggest step wins**. `Axialness` removed the
axial band and the path gave the choice somewhere to be going; neither of them addresses whether the
player was allowed to move at all, which is §3.1.

## 4. The design, in the order to try it

### 4.1 The shape: a path over the grid, steered one step at a time

The actuator takes one heading and a magnitude per frame. So the path is **not** what reaches the
input — it is what gives the one-step decision its opinion about the future:

```
per frame, once:
    path = Pathfind(player, goalRay, threatField)      // §4.2 -- the expensive part
    waypoint = first path node beyond the step radius  // §4.3
per candidate, unchanged:
    score += WeightPath * alignment(candidateHeading, waypoint)
```

**Once per frame, not once per candidate.** 77 candidates × an A\* each is not a thing the budget
survives; §14.5 is the reminder that even two square roots per (candidate, threat) pair cost 25 µs of
p99 when they were not cached. The search runs on the frames that evaluate, which is 3410 of 7169 on
`p26156`, so the budget per search is roughly `(300 − 119) / 1` µs if it is the only new cost — and
the honest target is **under 50 µs**, because the sampler and the sensor share the 300.

**And the path must degrade to what exists today.** No grid, a goal with no reachable node, a search
that hits its node cap or its time cap: the decision must fall back to the current candidate set and
say so, in a counter the harness prints. A pathfinder that can produce *no* answer must not be able to
produce *no move*.

### 4.2 The search: A\* on the client's grid, with a cost field

- **Nodes are grid cells**, one world unit each (§5.1). The window in a trace is 48×48; a full map is
  `MapInfo.Width × MapInfo.Height` and is available live (§5.2), so the search must be **bounded by
  the goal distance and a node cap**, not by the window.
- **Cost is distance plus danger.** The danger field is the part that makes it a *dodge* path rather
  than a nav route: a cell whose centre is inside a projectile's disc at the time the player would be
  there costs more — and the time comes from the path's own length and the player's speed, so this is
  a **spacetime** cost, consistent with `PlanWalk`'s one-clock model rather than a new one.
- **Tie-break toward the goal ray.** The heuristic is the straight line to the goal; the tie-break is
  a small preference for cells nearer the player's own line, which is what makes the path "go around"
  rather than "go wide".
- **The goal is not a room, it is a ray.** §13 made the forward goal the player's held direction, and
  a ray has no cell to search to. The search target is **the furthest cell along the goal ray that is
  reachable within the path horizon** — `GoalLookaheadSeconds` (0.3 s) extended to a path horizon of
  its own (§4.5). A goal that is *behind a wall* is a legitimate and important case: the path then
  hugs the wall toward it, which is the behaviour to look at first.
- **8-connected, with the diagonal rule.** A diagonal step is allowed only when both orthogonal
  neighbours are walkable — the standard rule, and it matters here because the collision grid's solid
  cells are the walls rule 2b already tests against.

### 4.3 From a path to a heading: one waypoint, with hysteresis of its own

- **The waypoint is the first path node that is more than one step away**, so that the steer does not
  jitter between the current cell and the next. `RadiusFar * budget` (one full step) is the scale.
- **The waypoint gets its own hysteresis.** The path is recomputed every frame and a path that
  changes its mind every frame is a dodge that changes its mind every frame; §10.4 and §13.2 are both
  recorded lessons about smoothing an angle, and the same discipline applies — hold the waypoint for a
  dwell, drop it when it is no longer on the path, and count the holds.
- **The path score is a *preference*, so it goes in `ScoreAll` beside `GoalProgress` — never in
  `Admit`.** This is not a style rule. The admissible set is the safety boundary; §13.1's safety
  argument and §DODGE-ALGORITHM.md §5.5's doctrine both rest on it, and a path term that admits is a
  path term that can walk the player into a bullet.

### 4.4 The cheap version first, which may be enough

Before A\*: a **one-step lookahead over the existing candidate set**. For each admissible candidate,
project one more step from it in the best available direction and score the pair. That is
`O(candidates × candidates)` and it can be reduced to `O(candidates)` by projecting each candidate
along the goal ray. It cannot thread a moving gap and it cannot route around a wall, but **it can
tell an outrun from a sidestep on the frames where the player is walking away**, which is §3's
dominant case. **Measure this first.** If it moves the OUTRUN/SIDESTEP ratio, the A\* is a quality
decision rather than a necessity and §4.4's cost budget can be spent on the spacetime cost instead.

### 4.5 What not to do

- **Do not make the path a candidate.** 48×48 cells is not a candidate ring, and a pathfinder whose
  output goes through `Admit` puts a search inside the safety boundary.
- **Do not re-derive the collision grid.** `World`'s `byte[,]` is resolved, is rule 2's source, and
  the client's own answer to "is this tile solid" — §5.2.
- **Do not average the path heading with the goal heading.** Two opposed angles average to a
  direction neither asked for. It is a *scored* choice, like §5.1's goal candidate and its fan.
- **Do not fix the outrun by raising `WeightLateral`.** §3's table is the reason: on the frames where
  the player is walking away from the shooter, lateral and goal agree, so the weight is not the
  variable that decides it. Measure before moving it — §10.4's precedent is a hysteresis change that
  was tried, measured worse, and reverted.
- **Do not change admission, the plan walk, or the projectile exclusion to make the feel better.**

---

## 5. Where the code is, and what is missing

### 5.1 The grid, as it stands

`CollisionWindow` (`hook/managed/Drelay.Bootstrap/DodgeSnapshot.cs`) is a **window** of the client's
`byte[,]`, and its comment is right about why:

> *"A real map's grid is `[MapInfo.Width, MapInfo.Height]` and a live position is in the thousands, so
> copying the whole grid per frame is unthinkable and recording it per trace is worse. But a player
> can move at most a few units inside the horizon and an admission test never looks further, so a
> window centred on the player is exactly the region the decision can touch."*

Measured on `p26156`: **420 distinct windows, every one 48×48, origins stepping by one cell as the
player walks** (`ox=1210, oy=1394` → `1209,1394` → `1208,1394` → `1208,1393`). Cell size is one world
unit, indexing is the game's own truncating `(int)world - Origin`, and a live 600 ms horizon at
~8.2 units/s reaches **4.1 units** — so the window is ~6× more than the *current* lookahead needs, and
**nowhere near enough for a path**. A path that goes around a volley, or toward a boss room, needs tens
of cells and a stable coordinate frame across frames; the window has neither.

### 5.2 What the full grid costs, and how to get it

The full array is `World.<byte[,]>`, `new byte[MapInfo.Width, MapInfo.Height]`
(`World.cs:14217`), with `World.MapInfo.Width` / `.Height` (`GmMapInfo.cs:14/16`) and
`World.MapInfo.PlayerId` (`:18`) available to name it. `DodgeAdapter` already resolves the array by
reflection (it is *the only `byte[,]` on `World`* — see its comment at `DodgeAdapter.cs:169`).

**The thing that is missing is a mirror.** Reading a full map cell-by-cell through a field accessor
every frame is not affordable, so the task needs:

1. **a one-time (per map) copy into a managed array**, `Width * Height` bytes — a few hundred KB at
   most, allocated on a map change and never on the frame path;
2. **a map identity to invalidate it.** Candidates, in the order worth trying: the `byte[,]`
   reference changing (an allocation per world), the `MapInfo` object reference changing,
   `MapInfo.Width`/`Height` changing, or `Tilemap`'s object reference. **This has to be measured, not
   assumed** — §3.9's "every threat was recorded with `GameId = 0`" and §3.11's silent overload are
   both records of an identity that was taken for granted;
3. **a size guard.** `MapInfo.Width` is a varint off the wire (§CLIENT-MAP.md): a hostile or corrupt
   value must degrade the path to "no grid" rather than allocate a gigabyte.

**And the snapshot must carry it without copying.** `DodgeSnapshot` is reused between frames and
allocates nothing; a full-map mirror is a second, longer-lived object that the snapshot *points at*,
exactly as `CollisionWindow.Cells` points into the recorder's window today.

### 5.3 The files

| file | what to look at |
|---|---|
| `hook/managed/Drelay.Bootstrap/DodgeSolver.cs` | `Decide` resolves the frame's numbers; `BuildAndAdmit` builds the candidate set including the goal fan (`Kind == 5`); `ScoreAll` is the score — **the path term goes here**; `GoalProgress` is the goal term; `PlanWalk` / `PlanRefusal` are the spacetime walk and rule 6; `WindowMsFor` / `BuildDecisionWindows` are §14; `FindHeading` is §13.2; `Hysteresis.Keep`/`Take`; `CopyLastCandidates` |
| `hook/managed/Drelay.Bootstrap/DodgeSnapshot.cs` | `PlayerView`, `Trajectory`, `EntityBody`, `CollisionWindow` (**the grid**), `DodgeConfig` (`Default`/`Preset`/`FromEnvironment`/`DescribePlan`/`DescribeWeights`) |
| `hook/managed/Drelay.Bootstrap/DodgeAdapter.cs` | `OnFrame` fills the snapshot; the grid resolution (line ~674) and `FillThreats`; `Publish`; `DecisionLine` is the `WOULD MOVE` log; `Log` is the `PHASE 3 DODGE` report |
| `hook/managed/Drelay.Bootstrap/SteeringPilot.cs` | `dodge` mode: `commanded = heading + rotation`, `WriteInput`/`StopInput` |
| `hook/managed/Drelay.Bootstrap/FieldAccessor.cs` | `ByteArray2D` — how a `byte[,]` is read without naming it |
| `hook/tools/SolverHarness/Program.cs` | the replay, `BuildSnapshot`, the invariants, `--config`, `--decisions`, `--frame` |
| `hook/tools/SolverHarness/Synthetic.cs` | the six scenarios' geometry — **`corridor` is the one with walls** |
| `tools/decisions_report.py` | per-frame decision across the whole recording |
| `tools/dodge_direction_report.py` | **§3's measurement** — OUTRUN / SIDESTEP / TOWARD per move |
| `tools/projectile_speed_report.py` | §14's projectile speeds and disc sizes |
| `tools/intent_report.py` | whose field `intent` is, on a live run |

---

## 6. The measurement plan

**Baseline first, and keep the output.** The five recordings are named in §7's table and the harness
is `hook\tools\SolverHarness\bin\Release\SolverHarness.exe`.

```powershell
cd G:\coding\javamapp\drelay\autododgeresearch

# every recording, with the decision dump and the aggregate report
pwsh -NoProfile -File .\hook\tools\Capture-DodgeDecisions.ps1 -Out .\work\dd-before

# THE NUMBER THIS TASK MOVES: outrun vs sidestep, per move, over the whole recording
python tools\dodge_direction_report.py

# and the two that must not break
python tools\decisions_report.py .\work\dd-before\dec-p26156.txt

# the six scenarios -- green is the bar, the move counts are not
foreach ($s in 'peaceful','headon','dodge','volley','corridor','passingby') {
  & $h --scenario $s --frames 900
}

# cost, which needs the repeat or it measures the collector
& $h --trace "$env:LOCALAPPDATA\drelay\hook\20261015-153805-p26156\trace.jsonl" `
      --collisions "$env:LOCALAPPDATA\drelay\hook\20261015-153805-p26156\bootstrap.log" --repeat 50
```

**The reporting to add alongside `--decisions`**, because the path is a new object and an object
nothing reports cannot be validated:

- **`--decisions` gains the path's own fields**: the waypoint heading, the path length in nodes, the
  path's danger cost, the number of searches, and the fallback reason when there is none. One column
  per number, in the same file as the decision that used it.
- **the harness prints `path` counts**: searches run, searches that hit the node cap, frames with no
  path, mean path length, and the mean cost per search in microseconds. **A pathfinder that is
  silently absent prints as "the outrun ratio did not move", which is the same output as a pathfinder
  that ran and did nothing.**
- **`DodgeConfig.DescribePlan()` carries the path's two or three numbers** so a live run's own log
  says which path it ran. The report line is at ~147 characters of a 256-byte native budget (§14.6),
  so a second line is the pattern to follow, not a longer one.

**And the acceptance test, which is the operator's and not the session's.** The dry run first — it
decides on every frame and moves nobody:

```powershell
.\hook\tools\Run-Phase3Session.ps1 -Experiment Hold -Seconds 180 -Dodge
# then, with the game closed:
python tools\intent_report.py "$env:LOCALAPPDATA\drelay\hook\<run-dir>"
```

**For this task the dry run is nearly as good as the acting one**: the question is what the solver
*chose*, and the dry run logs the same `WOULD MOVE` lines with the same heading, scale, goal and
course flag. The difference is closure — in a dry run the body follows the player's own input, so the
*actual* displacement is not the dodge's. **Read the choice, not the displacement.** §3's measurement
reads the choice for exactly this reason.

---

## 7. The invariants you must not break

Asserted by the harness on every recording and every scenario — unchanged, and **this is the list a
pathfinder is most likely to break**:

- **F** the recording contains frames — an empty trace is a failure, not a pass (§3.8).
- **T5** `stand` is never rejected while it is admitted and durably safe.
- **T6** no emitted move is inside a disc at the player's arrival — *this is the safety boundary and
  it is asserted on what was emitted, not on what the solver believed* (§3.7).
- **T7** predicted and actual collisions agree; **T7P** the live observer and the harness agree.
- **T10** hysteresis cannot hold a heading that has become unsafe.
- **MOVE** the `dodge` scenario must produce at least one move, because three of the four invariants
  are satisfied by a solver that never moves at all.

**T5, T6 and T10 have held through every change so far. A change that breaks one of them is not a
tuning question.** If a scenario fails, `--frame N` prints that frame in full — the player, every
threat, every candidate with the rule that refused it, and the top of the menu by score. It found five
of the seven original defects in §3 and it is the first thing to reach for.

**Current baseline, so a regression is visible:** all five recordings and all six scenarios pass
everything except **T7 on the recordings**, which fails identically before and after every change in
§13 and §14 — `p26156` has **zero resolved collisions**, so its T7 line says nothing (§6.6), and the
other four carry the harness's pre-existing false positives. **Compare a T7 number against the same
run's before-column, never against zero.**

| run | frames | moved | hysteresis | stutter | `decide()` p99 |
|---|---|---|---|---|---|
| `p20152` | 6267 | 2074 (33.1 %) | 73.8 % | 1.6 % / 7.4° | — |
| `p5824` | 10139 | 2487 (24.5 %) | 75.8 % | 1.7 % / 7.7° | — |
| `p26684` | 21443 | 5040 (23.5 %) | 73.5 % | 1.5 % / 7.8° | — |
| `p5424` | 7926 | 1141 (14.4 %) | 78.4 % | 1.7 % / 6.7° | — |
| **`p26156`** | **7169** | **1666 (23.2 %)** | **83.7 %** | **0.6 % / 3.8°** | **119 µs, max 247 µs** |

And the same run's behaviour, from `tools/decisions_report.py`, as the state a pathfinder starts from:
the goal comes from the **player** on 1082 moves and the **course** on 568; mean \|heading − held
direction\| **51.6°**, median **30.6°**; scales `1.0` × 1541, `0.7` × 57, `0.35` × 67.

---

## 8. Traps

1. **A path is not a move.** The actuator takes one heading and one magnitude. The path's job is to
   give the one-step choice an opinion; a design that emits a path node directly has moved the safety
   boundary into the search.
2. **The window is not the map.** `CollisionWindow` is 48×48 centred on the player and its origin moves
   with them (§5.1). A path computed in window coordinates is a path in a frame that changes every
   frame. **A stable origin is part of the task.**
3. **The full grid has to be mirrored once, not read per frame**, and the mirror has to be invalidated
   on a map change (§5.2). An identity taken for granted is §3.9 and §3.11 twice over.
4. **Allocation on the frame path is refused.** Every search buffer is a field sized once. A
   `List<Node>` per frame is the one thing this project has measured and refused repeatedly.
5. **`intent` is stale when the player is not holding a direction** — 93.2 % of non-moving frames
   repeat the previous value. §13 gates the goal on `Moving` for this reason and the path inherits it.
6. **The goal must stay in the world frame.** The pilot adds `WorldRotation` back on the way out;
   nothing in `DodgeSolver` may see an input-frame angle. `intent - WorldRotation` is the conversion
   and `DodgeAdapter.OnFrame` and `SolverHarness.BuildSnapshot` must stay identical.
7. **Replay is not closure.** The recordings are replays of positions the *old* dodge produced, so a
   change measured on them is "what the new solver would have done in the old solver's world". That is
   enough to rank two versions and not enough to conclude. The live session is the conclusion.
8. **The synthetic scenarios are falsification tools, not a tuning target.** `corridor` is the one
   with walls in it, and `headon` and `dodge` move counts changed by 5× when §11 landed and that was
   correct. Keep them green and do not tune to them.
9. **The traces before `p26156` have no `nohit` key** and their threat sets contain the player's own
   shots, so their `ALREADY TOUCHING`, move counts and classification are inflated. Compare a new
   change against `p26156` for the dodge's behaviour, and against the older ones only for invariants.
10. **The 256-byte line limit applies to `hook.log`, not `bootstrap.log`** (§12.7). `bootstrap.log`
    carries complete 483-character lines. Do not split a line for a limit that does not bind.
11. **An instrument that cannot see the change cannot validate it.** §6.10's lesson, and this task adds
    a new object (the path) to the same problem. Add the columns before the search.
12. **Every change is replayed over all five recordings** before it is believed.

---

## 9. Where to write the result

**`SPIKE3.2-OFFLINE-SOLVER.md` §15.14**, in that document's voice: what changed, the measured before and
after — **with §3.1's halt table as the headline**, since the halt rate is the number this task moves —
what the operator's live session said, and **what the change does not establish**. §11, §12, §13, §14 and
§15 are the models to follow: name the defect that motivated the change in one sentence, and put the
number that proves it next to the number that proves it was fixed. §15.13 is the section this task
answers directly: it is the operator's verdict, and its own last line says the two things to build.

**AND THE FIRST PARAGRAPH MUST STATE WHAT WAS MEASURED AND HOW.** This task's defect is a
**displacement** and the replay harness cannot see it (§3.1): the two configurations produce identical
numbers offline. A section that reports only replay numbers for this change would be reporting the one
quantity that is provably blind to it.

If the change is reverted, write it up anyway. §10.4 is the precedent and it is one of the more useful
sections in that document: *"strengthening the hysteresis was tried and measured and did not help …
reverted rather than shipped."* §14.4 is the second precedent — the first version of the window had no
clamp, one recording produced a 30720 ms window from a parked projectile, and the clamp is in the
shipped code because the measurement found it rather than the design. §15.9.2 is the third: the
projected-danger term ships at weight 0 because the sweep said the idea, not the implementation, was the
problem.
