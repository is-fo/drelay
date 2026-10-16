# Plan — enemies as threats, so the path stops walking over them

**Item 1 of the auto-farmer work.** Written 2026-10-17. **Steps 1–4 are built; §8 is the record of
what was actually delivered and where it departed from this plan. Step 5 (the live confirmation) is
the only part that still needs an operator.** The body of the document below is left as written — it
is the specification the implementation was measured against, and §8 is the diff.

> **§8.8 IS A CORRECTION AND IT SUPERSEDES THE HEADLINE OF §8.** The first delivery — commit
> `d4e8f46` — resolved the reader, filled `DodgeSnapshot.Bodies`, passed every test §5 asks for, and
> **changed no decision on any frame whose only hazard was an enemy body.** The operator greenlit it
> on a live session and then reported that enemies were still not treated as dangerous, which is
> correct: the commit's own `body` fixture plans a route around the mob and then decides `NoThreat`
> on all 600 of its frames, so the route was never followed and no move was ever emitted. Four
> separate defects produced that, and §8.8 is what they were, how each one was measured, and what
> the fix is. Read §8.8 before §8.1–§8.7; the earlier sections are the record of the first delivery
> and several of their conclusions are wrong.

Read with: [`DODGE-ALGORITHM.md`](DODGE-ALGORITHM.md) §5.3 rule 7 (the enemy-body rule),
[`HANDOFF-DIRECTED-DODGE.md`](HANDOFF-DIRECTED-DODGE.md) §4 (the path over the client's grid),
[`PLAN-AUTOFARM.md`](PLAN-AUTOFARM.md) (the rest of the farmer).
[`PathPlanner.cs`](../../hook/managed/Drelay.Bootstrap/PathPlanner.cs),
[`DodgeAdapter.cs`](../../hook/managed/Drelay.Bootstrap/DodgeAdapter.cs).

---

## 1. The operator's request, restated exactly

> *"autododge should treat enemies as projectiles with the simple rationale that an enemy shoots
> projectiles, so pathing over an enemy might look safe until it spawns projectiles."*

That sentence contains two claims, and they are not the same claim:

1. **A path can be routed across an enemy body, because nothing in the solver knows the body is
   there.** This is verifiable in the code today and it is true.
2. **Therefore an enemy should be modelled as a hazard, the way a bullet is.** This is a design
   choice, and it has a failure mode that the reference engine's own audit documents (a dodge that
   fences itself in — `hook/work/auto-dodge-feasibility.md` §6.5, *"ghost lanes fencing the player in
   for the better part of a second"*).

The plan separates them. Claim 1 is a defect with a small, safe fix. Claim 2 is a tuning problem and
must not be allowed to become a *hard* rule.

---

## 2. What is true in the code today

### 2.1 The body list exists and is never filled

`DodgeSnapshot.Bodies` is a real, declared, cleared-every-frame list of `EntityBody { X, Y, Radius }`
(`hook/managed/Drelay.Bootstrap/DodgeSnapshot.cs:240-245`, `:417-418`, `:428`).

`DodgeSolver.Admit` already implements rule 7 against it, including the "already inside this body is
not a refusal" exception that exists so a mob walking onto the player cannot trap them
(`DodgeSolver.cs:2140-2159`, `AdmissionRule.EnemyBody = 7` at `:127`).

**Nothing ever adds to it.** `DodgeAdapter` says so itself:

> `hook/managed/Drelay.Bootstrap/DodgeAdapter.cs:56-58` — *"**RULE 7 (ENEMY BODIES) IS VACUOUS.**
> `DodgeSnapshot.Bodies` is left empty — 'empty is legal', says the type — because no accessor for
> the live entity list has been resolved. The counter `bodies=0` in every report is the honest mark
> of it."*

and the frame path repeats it at `DodgeAdapter.cs:836` (`// Rule 7 is vacuous on the live path`).

So the only thing standing between the solver and rule 7 is a resolved `World.GetEnemies()`.

### 2.2 The path planner cannot see an enemy either

`PathPlanner.BuildDangerEnvelope` builds its envelope from `snapshot.Threats` only
(`PathPlanner.cs:771-802`). `DangerAt` — the cost function for every A* edge, and the exported field
that `DodgeSolver.ProjectedDanger` reads — iterates `_threatCount` trajectories and nothing else
(`PathPlanner.cs:1011-1078`).

The path's cost is `distance + PathStepCostWeight · danger` (`PathPlanner.cs:903`). With bullets
being the only danger, **an enemy standing in the open is free space as far as the search is
concerned**, and the shortest route to a goal beyond it goes through it. On the frames where the
enemy is not currently shooting — which for a ranged mob is most of them — the path deliberately
walks into its muzzle.

This is precisely the operator's sentence, and it is a real defect in the *existing* dodge (not only
in a future farmer): the goal fan and the path both steer the player toward wherever they were
already going, and a mob on that line is invisible.

### 2.3 The enemy list is already resolved, once, by the auto-aim module

`AutoAim.Resolve` resolves `World.GetEnemies()` as a raw managed function pointer (`AutoAim.cs:205-221`),
plus `Position.X/Y`, `Dead`, `Npc`, `Health`, `MaxHealth` and `Effects` on the returned element type
(`:219-244`), and `Effects.Unhittable()` (`:246-253`). `AutoAim.Choose` already applies the four
filters and a distance loop over that array (`:529-595`). **No new game knowledge is needed for this
plan** — every field the body list wants has been resolved and exercised live for a week.

---

## 3. The design, and the rule that keeps it safe

### 3.1 Two radii, and only one of them is allowed to refuse

| role | radius | where it is applied | what it may do |
|---|---|---|---|
| **body** | `enemy.Radius + player.Radius` | rule 7, `Admit` | **refuse** a candidate |
| **emitter** | `enemy.Radius + player.Radius + EnemyDangerPadUnits` | the danger envelope only (path cost, projected-danger score) | **prefer** not to be there |

**The emitter radius must never be an admission rule, and this is the load-bearing decision in the
document.** Rule 7's exception ("already inside → the test is dropped") exists because a body that
encloses the player is otherwise unsurvivable; an *inflated* body has the same failure mode but
without the excuse, and in a crowd of mobs it would refuse every candidate on the frame — the
`NoAdmissible` defect that `ThreatVerdict` was written to fix (`DodgeSolver.cs:131-183`, measured at
**1733 of 6267 frames, 27.7 %**, on `20261015-021911-p20152`). A dodge that will not move because
there is a mob nearby is worse than one that walks past it.

So the emitter radiates into the danger field, which is a **cost**, and the path and the score react
to it. It cannot remove a candidate.

### 3.2 Why the emitter pad is small, and why the weight starts at zero

The rationale "it will shoot" is real but it is *time-unbounded*: a ranged mob is dangerous at
whatever its weapon's reach is, which the hook does not know. Two honest consequences:

- **A melee player must stand next to enemies to hit them.** A large pad makes the dodge orbit away
  from the thing the auto-aim module is aiming at, and — once the farmer exists — away from the
  target it was sent to kill. That is a self-defeating behaviour, not caution.
- **The muzzle is already the sensor.** When the enemy actually fires, `Projectile.ProcessProjectileData`
  produces a real trajectory that the existing sampler predicts exactly. The emitter pad buys the
  time between "the enemy is here" and "the bullet exists" — a fraction of a second — and it buys it
  with a model that has no range in it.

The project's own precedent for exactly this situation is `WeightProjectedDanger`, which ships at
weight **0** with the idea retained and the implementation measured
(`docs/hook/HANDOFF-DIRECTED-DODGE.md` §0, `SPIKE3.2-OFFLINE-SOLVER.md` §15.9.2). Do the same:
`EnemyEmitter` on, `WeightEnemyDanger = 0` until the harness says otherwise.

### 3.3 Filters are not auto-aim's filters

`AutoAim` excludes `Dead`, `Npc` and `Effects.Unhittable()` because it is asking **"can I damage
this?"**. The dodge is asking **"can this damage me?"**, which is a different question with a
different answer for the same enemy. An `Invulnerable` or `Invincible` enemy cannot be hit and can
usually still hit back; a `Cutscene` one can do neither.

So the body reader filters on `Dead` and `Npc` only, and deliberately **does not** reuse
`Unhittable()`. This is one of the two reasons the reader is its own class (§4.1) rather than a
shared helper with `AutoAim`.

*(The other reason is coupling: `AutoAim`'s state is its own module's business, and a second
consumer reaching into it makes the auto-aim mode a switch that changes the dodge.)*

---

## 4. Implementation

### 4.1 A new `EnemyReader.cs`

Read-only, resolve-once, allocates nothing per frame — the same discipline as `AutoAim.Resolve`.

```
Resolve(Assembly game):
    World.GetEnemies() -> IntPtr + element Type           (AutoAim.cs:205-221 is the pattern)
    element.Position{X,Y}, .Dead, .Npc, .Radius           (FieldLookup.Find + FieldAccessor.NestedFloat)
    -> returns false with a named reason; the dodge is unaffected by a failure

Fill(DodgeSnapshot s, DodgeConfig cfg, float px, float py, float radius):
    raw = ((delegate* managed<object, object>)_getEnemies)(world)   // World caches the array
    for each enemy:
        skip null, Dead, Npc
        d2 = (ex-px)^2 + (ey-py)^2
        skip if d2 > EnemyCullRadius^2                              // see §4.4
        s.Bodies.Add(new EntityBody { X, Y, Radius = enemyRadius })
```

`Radius` comes from `GameObject.Radius` — the same field the dodge already reads for the player
(`DodgeAdapter.cs:757`, `PlayerView.Radius`), and which the game's own hit test adds to the bullet's
(`DODGE-ALGORITHM.md` §5.1). If `Radius` does not resolve, use `DodgeConfig.DefaultPlayerRadius` and
**count the fallback** — a silent zero radius is a body you can walk through.

Failure discipline is the house rule: `Resolve` returns false with a reason, `Fill` does nothing, the
report prints `bodies=0 enemies=unresolved`, and every existing behaviour is unchanged. A hook that
cannot read enemies must not dodge differently from one that was never asked to.

### 4.2 `DodgeSnapshot.cs` — three fields and one new struct member

```
EntityBody
    float X, Y, Radius             (exists)
    float DangerRadius             NEW: Radius + playerRadius + cfg.EnemyDangerPadUnits

DodgeConfig
    bool  EnemyBodies              NEW  default true   -- rule 7 uses the list at all
    bool  EnemyEmitter             NEW  default true   -- the pad reaches the danger field
    float EnemyDangerPadUnits      NEW  default 0.75   -- ~3/4 of a tile; harness-tuned
    float EnemyCullRadius          NEW  default 12.0   -- bodies further than this are not added
    float WeightEnemyDanger        NEW  default 0.0    -- see §3.2
```

All five follow `DodgeConfig.Default()`/`FromEnvironment()` (`DodgeSnapshot.cs:859`, `:1121`) so the
harness can sweep them with `--config` and a live run can set them with `DRELAY_DODGE_*`.

### 4.3 `PathPlanner.BuildDangerEnvelope` — fold the bodies in

After the trajectory loop, walk `snapshot.Bodies` and add each body as a **stationary** entry in the
same per-threat arrays:

```
_threats[i]   = a Trajectory with Count = 1, Xs[0]/Ys[0] = body centre, Radius = body.Radius,
                StepMs = cfg.SampleStepMs, Alive[0] = true
_threatNear[i]   = body.DangerRadius                     // stationary => no half-step term
_threatRadius[i] = body.DangerRadius
_threatSamples[i] = 1
```

**Why `Count = 1` is correct rather than a hack.** `DangerAt` clamps its sample index to
`samples - 1` and interpolates only when `idx < samples - 1` (`PathPlanner.cs:1039-1061`), so a
one-sample trajectory is *always* at its own position, for every `seconds` — which is exactly a body
that does not move. `SpeedOf` returns 0 when `Count < 2` (`:805-814`), so the half-sample term
vanishes on its own. `Trajectory.XAt/YAt` clamp (`DodgeSnapshot.cs:209-210`).

Two consequences to handle explicitly:

- **`DebugThreatCount` / `DebugThreat(i)` change meaning.** They currently describe bullets. The
  frame dump and `--decisions` must say which entries are bodies, or a body in the envelope reads as
  a bullet with a strange speed. Add a `_threatIsBody[]` flag, a `bodies=` column and a
  `DebugThreat` marker.
- **`ProbeRadius` takes `_threatNear[0]`** (`PathPlanner.cs:291-299`) as the frame's representative
  disc. With bullets plus bodies, `[0]` may be a body. Keep the existing rule but define it over
  bullets when at least one exists, and fall back to bodies otherwise — with a comment, because
  "the first entry" silently changing meaning is the class of bug this project has already paid for.

### 4.4 `DodgeAdapter.OnFrame` — one call, one counter

```
DodgeAdapter.cs:836-837
    FillThreats(s, liveProjectiles, liveKeys, px, py, radius);
+   EnemyReader.Fill(s, _config, px, py, radius);        // bodies for rule 7
+   if (_config.EnemyEmitter) { /* the planner reads s.Bodies itself */ }
```
and the report line gains `bodies=N enemies=N culled=N fallbackRadius=N`. Rule 7 stops being vacuous
the moment this call exists; the comment at `:836` must be deleted in the same change, because a
comment that says a feature is absent is how the next reader skips the test.

`EnemyCullRadius` is what keeps the frame cost bounded: a full realm can hold hundreds of enemies and
the solver only ever reasons about the few units it can reach. Culling at 12 units is ~2× the longest
existing horizon and removes everything the planner could ever cost.

### 4.5 Solo answer to "does the emitter pad belong in `PlanWalk` at all?"

**No.** `PlanWalk`/rules 4–6 are the *hard* boundary (`DODGE-ALGORITHM.md` §5.3, and the doctrine
repeated in `HANDOFF-DIRECTED-DODGE.md` §4.3: *"the admission rules remain the safety boundary"*).
A body with a padded radius inside `PlanWalk` would make a candidate "unsafe" merely for passing
within the pad of a mob, which is the fencing-in failure. The emitter reaches only:

1. the path's edge cost (so the route goes around rather than through), and
2. a new score term (`WeightEnemyDanger`), which can only re-rank candidates that already passed.

Rule 7 keeps the unpadded body radius, unchanged, exactly as it is written today.

---

## 5. Tests and instruments

The rule this project applies to every change and which this plan inherits: **an instrument that
cannot see the change cannot validate it** (`HANDOFF-DIRECTED-DODGE.md` §8 trap 11).

| # | what | how | pass |
|---|---|---|---|
| E1 | `bodies=0` becomes `bodies=N` on a live frame with enemies in view | `bootstrap.log`'s dodge report | a nonzero count, and `enemies=unresolved` when resolution failed |
| E2 | rule 7 refuses a candidate that walks into a body | `SolverHarness` gets a new scenario `body` (a stationary `EntityBody` on the line to the goal) | `AdmissionRule.EnemyBody` appears in the refusal counters; the chosen heading is not through the body |
| E3 | the path routes around a body rather than through it | same scenario, with a body placed on the straight line and `WeightEnemyDanger` > 0 | path length exceeds the straight-line distance, and **no path node centre is inside `body.DangerRadius`** — a counter like `WallCrossings`, asserted at 0 |
| E4 | the emitter pad never refuses anything | same scenario with `EnemyDangerPadUnits` swept from 0 to 3 and `WeightEnemyDanger` = 0 | the admissible count is identical at every pad, i.e. the pad has no effect on admission |
| E5 | the pad cannot fence the player in | a `body` scenario with the player surrounded by 8 bodies at 1 unit | at least one admissible candidate exists, so the decision is not `NoAdmissible` |
| E6 | `stand` still wins when it is durably safe | existing T5, all five recordings and six scenarios | unchanged — this is the invariant most likely to break, because a body near the player is exactly what T5's durability test would react to if the body were in `PlanWalk` |
| E7 | the safety boundary holds | existing T6/T10, all recordings and scenarios | unchanged |
| E8 | the cost is bounded | `decide()` p99 against the 300 µs budget, with a body-heavy scenario | inside budget, and the delta from the no-body baseline reported rather than asserted |
| E9 | with the module off nothing changes | `EnemyBodies=false`, replay every recording | byte-identical decisions to today (the T9 doctrine) |

E4 and E6 are the two that protect the design: together they assert that the emitter is a preference
and the body is the only thing that refuses.

---

## 6. What this does not do

- **It does not model a muzzle or a firing cadence.** There is no "this enemy is about to shoot"
  prediction; that needs an enemy-type/behaviour table the hook does not have, and it is not needed
  while the real bullet is sampled exactly one frame later.
- **It does not measure the emitter's radius.** `0.75` is a starting value and the harness is where it
  is settled (`SPIKE3.2-OFFLINE-SOLVER.md`'s own doctrine: tune offline, never in a live run).
- **It does not add a ranged-reach model.** A pad is not a range, and pretending otherwise is the
  failure this plan is arranged to avoid.
- **It does not make `Bodies` a threat in the solver's own list.** They are a second, parallel input
  consumed by exactly two places (rule 7 and the planner envelope) and by one new score term.
- **It does not fix the one thing that actually kills players near a mob**, which is the mob's
  projectile — already handled, and handled better, by the existing sampler.

## 7. Estimates and order

| step | files | size | needs the game? |
|---|---|---|---|
| `EnemyReader` resolve + fill + report counters | `EnemyReader.cs` (new), `DodgeAdapter.cs`, `StartupHook.cs` (call site only if needed) | ~200 lines | no (resolve failures visible offline) |
| config fields + defaults + env | `DodgeSnapshot.cs` | ~60 lines | no |
| envelope + `ProbeRadius` + `--decisions` columns | `PathPlanner.cs`, `DodgeAdapter.cs` | ~80 lines | no |
| `body` synthetic scenario, E2–E5, E8 | `hook/tools/SolverHarness/Synthetic.cs`, `Program.cs` | ~250 lines | no |
| live confirmation (E1) and a session with a mob on the line | — | one session | **yes** |

Ordered 1 → 4 with the harness green before any live run, which is the order every previous dodge
change took. **Step 5 is the only thing that needs the operator**, and it is one session: walk toward
a ranged mob in a realm and read whether the path bends around it and whether the character still
closes on it.

Per the operator's own note on this item — *"this might be simple enough where you can just implement
it straight away"* — steps 1–4 are a single sitting and touch no pinned offset, no bridge and no new
game field.

---

## 8. What was actually built, and the four places it departed from the plan

Steps 1–4 are in. This section is the record: what exists, what each of the plan's tests measured, and
the decisions that had to be made where the plan was silent or wrong. It is written in the form the
rest of this project's records take — the departure first, because that is the part a reader of §1–7
cannot infer.

### 8.1 Departure 1: the no-threat gate had to learn about bodies, and that is the change the plan does not mention

**This is the one substantive addition.** `DodgeSolver.DecideCore`'s fourth guard is
`if (!AnyThreatNearPlayer(...)) return NoThreat;` — and it iterated `snapshot.Threats`, which is
bullets. A frame with a mob standing in the open and nothing in flight therefore returned `NoThreat`
before a candidate set existed, before rule 7 was asked anything, and before the planner was consulted.

The consequence is larger than a missing refusal: **without this change the whole feature is invisible
on exactly the frames §1's operator sentence is about.** "Pathing over an enemy might look safe until
it spawns projectiles" describes frames with no projectile in them. So `AnyThreatNearPlayer` now also
asks whether a body is close enough that **this frame's candidate ring could reach it**:

```
reach = body.Radius + player.Radius + budget      // budget = speed * frameSeconds
```

Why that reach and not the emitter's: it is exactly rule 7's own boundary, so the gate opens on the
frames admission has something to say and on no others. An emitter-sized gate would drag the pad into
the question of WHICH frames are evaluated — and §3.1's whole argument is that the pad must not be
able to do that.

**What the gate may not do is restated where it is implemented**, because it is the load-bearing
distinction of the document: this decides whether the frame is EVALUATED. It cannot admit, refuse or
move anything. The body still refuses only against its own unpadded radius, and the emitter is still a
cost.

**And it is what keeps E4 meaningful.** A pad-driven gate would have made `enemypad=0` produce
"every candidate inadmissible" — which reads identically to the emitter refusing everything and would
have hidden the very defect E4 exists to catch. It was found exactly that way: the first E4 sweep
reported `admissible=0` at pad 0 and 46 200 at every positive pad, because the gate had been written
against `DangerRadius`.

> **CORRECTED — see §8.8.1.** The reasoning above is sound about the pad and wrong about the reach.
> "A frame with a mob in it and nothing in flight therefore returned `NoThreat` before … the planner
> was consulted" is exactly right, and the reach chosen to fix it — rule 7's boundary — is *admission's*
> reach and not the frame's. The planner's emitter field reaches `PathTargetSeconds` of travel, about
> nineteen times as far, and a body beyond one frame's step therefore still returned `NoThreat`. The
> gate needed a second half, not a wider first one.

### 8.2 Departure 2: `EnemyReader` resolves `World.Shared` separately from the enemy list

§4.1's sketch has `Fill(DodgeSnapshot, DodgeConfig, px, py, radius)` and no world parameter, so the
reader needs the world instance. It gets it from `World.Shared` — resolved by
`EnemyReader.ResolveWorld(FieldInfo)`, which `DodgeAdapter.Resolve` calls with the field it has
already found for its own use.

The two are separate calls on purpose: `Resolve` finds the enemy list on the world *type*, and
`ResolveWorld` finds the world *instance*. A hook whose `World.Shared` never resolves then reports the
reader's own status honestly instead of failing for a reason that belongs to the caller.

### 8.3 `EntityBody.DangerRadius` is computed by the reader, and zero means "no emitter"

§4.2 adds `DangerRadius = Radius + playerRadius + cfg.EnemyDangerPadUnits` to the struct and has the
planner derive it. It is computed once in `EnemyReader.Fill` instead, so the struct carries both of its
radii and neither consumer re-derives one from the other's inputs. `BuildBodyField` then reads
`EnemyEmitter` to decide whether to spray anything at all, and `EnemyBodies` to decide whether there
is a list to spray — a body is then rule 7 alone, exactly as §3.1 requires.

The harness sets `DangerRadius` from *its own* config rather than reading it off the frame, which is
what makes `--config enemypad=N` a sweep of the emitter alone: positions and body radii come from the
trace, the pad comes from the run.

### 8.4 Departure 3: bodies are a per-cell field, not one-sample trajectories in the envelope

§4.3 folds each body into the planner's per-threat arrays as a stationary one-sample `Trajectory`.
The first implementation did exactly that, and it is correct — `DangerAt` clamps its sample index, so
a one-sample entry is at its own position for every `seconds`, and `SpeedOf` returns 0 for
`Count < 2` so the half-sample term vanishes. It is also **five times more expensive than it needs to
be**, and the harness said so on the first sweep:

| scenario — 8 bodies for `bodyfence`, 1 for `bodyclose` | `decide()` p50 | p99 |
|---|---|---|
| `bodyclose`, bodies as envelope entries (single-shot) | 134 µs | 231 µs |
| `bodyclose`, after the change below (single-shot) | 59 µs | 114 µs |
| `body`, one body far enough out not to cover any cell | 21 µs | 31 µs |

and with the estimator the project actually tunes against (`--repeat 50`, best of 50, Release):

| scenario | p50 | p99 |
|---|---|---|
| `peaceful` (no bodies anywhere) | 17 µs | 23 µs |
| `body` (one body, no cell covered) | 10 µs | 33 µs |
| `bodyclose` (one body, one cell covered) | 22 µs | 56 µs |
| `bodyfence` (eight bodies) | 20 µs | 58 µs |

The cause is not allocation (that was tried and fixed separately — `new Trajectory()` is a class whose
six sample arrays are built in its constructor, ~450 bytes each, and pooling it moved `max` but not
`p50`). The cause is that **every expanded node paid a distance test against every body**, and this
planner expands ~1 400 nodes a frame. Eight bodies is ~17 000 distance tests a frame for an answer
that is a property of the cell.

So the bodies are sprayed once per frame into `PathPlanner._bodyCost` — a `float` per grid cell, `1`
inside a `DangerRadius` disc and `0` outside — and `DangerAt` adds one array read. `DebugBodyCount`
is now the count of cells an emitter covers, which is a more useful number than a body count: "eight
bodies" says nothing about whether the field is doing anything, while "N cells" says exactly how much
of the search's world the emitter owns. At the shipped pad that N is small — a `DangerRadius` of 1.25
usually covers one or two cell centres — so `bodyclose` costs what it does because the ONE covered
cell is the player's own, and the search has to route out of it.

Two consequences worth naming:

- **The body field is a step, not a gradient.** A bullet's danger ramps linearly over its pad because
  a bullet's distance varies continuously; a body is a disc, and inventing a falloff would be
  inventing a range model, which §3.2 refuses. The value is `1` inside and `0` outside, and the
  measured detour is real: on `body` the path is 12.828 units against a 12.0-unit straight line, with
  `pathcost == pathlen` — the route leaves the disc entirely rather than paying to cross it.
- **`ProbeRadius` needs no special case any more**, and neither does `_threatIsBody`. The envelope
  holds bullets, so "the first threat" means what it always meant. §4.3's second consequence is
  answered by the array not containing bodies at all rather than by a flag defending against it.

### 8.5 Departure 4: the E3 counter is not `WallCrossings`

§5's E3 says "a counter like `WallCrossings`, asserted at 0". `WallCrossings` counts corner cuts on
the returned path; the twin is `PathStats.BodyZoneNodes`, which counts **planned path nodes whose
centre is inside an enemy emitter disc**. Same construction and same reason as the corner-cut counter:
`pathlen` and `pathcost` growing are *consistent* with going round a body and are not evidence of it,
because both are also what a pad that costs nothing prints.

It is asserted on the paths the planner returned, over every scenario, and it is **not asserted** on
a trace where an enemy's emitter disc already covers the player — the first node is then inside a disc
whether the edge cost is counted or not, and the count cannot answer the question. `bodyclose` and
`bodyfence` are both that shape by construction, and the harness prints which of the two verdicts it
reached rather than a silent pass (see `Harness.BodiesCoverThePlayer`).

### 8.6 What each of §5's tests measured

Every number below is from `hook\tools\SolverHarness\bin\Release\SolverHarness.exe`, 600 frames, seed
1, `balanced`, the shipped defaults. The Debug build was used for the first four while the code was
being written; the Release build is what every figure here is quoted from.

| # | what | how it was actually run | result |
|---|---|---|---|
| E1 | `bodies=0` becomes `bodies=N` | `bootstrap.log`'s `PHASE 3 DODGE DETAIL` line, then `EnemyReader.Describe()` on the same line | **needs the game.** The report carries `bodies <mean> on <n> of <frames> frame(s)` and then `enemies=live frames= culled= dropped= fallbackRadius= skipped=`; `enemies=unresolved` / `enemies=off` name the other two ways it can read zero |
| E2 | rule 7 refuses a candidate that walks into a body | `--scenario bodyclose` (one body at 0.55 units, east) | **PASS**: 12 000 refusals by `AdmissionRule.EnemyBody` over 600 frames, and 0 emitted moves swept a body |
| E3 | the path routes around a body rather than through it | `--scenario body` (one body 6 units out, on the goal ray) | **PASS AS A CLAIM ABOUT THE PLANNER, AND IT WAS NOT EVIDENCE ABOUT THE DODGE**: `BodyZoneNodes` 0 over 600 paths found, path length 12.83 against the straight line 12.0 — and on the same 600 frames the decision was `NoThreat`/`None`, so no move was emitted and the route was never followed. See §8.8.2 |
| E4 | the emitter pad never refuses anything | `--scenario bodyclose` and `bodyfence`, `--config enemypad=0 / 0.75 / 1.5 / 3` | **PASS**: the admissible total is *identical* at every pad, in both scenarios (34 200 and 15 000) |
| E5 | the pad cannot fence the player in | `--scenario bodyfence` (eight bodies at 0.55 units) | **PASS**: every one of the 600 frames with a candidate set had an admissible MOVING candidate |
| E6 | `stand` still wins when it is durably safe | T5, all nine scenarios | **PASS**, unchanged |
| E7 | the safety boundary holds | T6 and T10, all nine scenarios | **PASS**, unchanged |
| E8 | the cost is bounded | `decide()` p50/p99 on the Release harness with `--repeat 50` (the estimator the project tunes against), body-heavy scenarios against `peaceful` as the no-body baseline | **PASS, with a named cost**: `peaceful` 17/23 µs, `bodyclose` 22/56 µs, `bodyfence` 20/58 µs — so eight enemy bodies in view add ~35 µs to p99. The first implementation of §4.3 added ~200 µs (134/231 single-shot); §8.4 records why. All inside the 300 µs budget, which is shared with the sampler and the sensor |
| E9 | with the module off nothing changes | `--config enemies=0,emitter=0,wenemy=0` against the **pre-change source compiled from `HEAD`**, all nine scenarios | **PASS for the six scenarios that existed before**: byte-identical on every decision field (frame, action, reason, held, heading, scale, goal, goalsrc, hintent, intent, course, moving, evaluated, admissible, durability, clearance, resolve, score, runnerup, window). The three body fixtures cannot be replayed against the old build — it does not know them |
| E10 | a body on the route to the goal opens the frame | added by §8.8.3: every frame with a body whose collision disc lies across the segment from the player to the planner's own target, asserted against `DodgeReason.NoThreat` | **the invariant the first delivery was missing.** It FAILS on the pre-fix build (`600 of 600` frames of `--scenario body`) and passes after §8.8.1/§8.8.3. It is asserted on the DECISION, where E2 asserts on the refusals and E3 on the route |

§8.8.6 re-runs this table against the fixed build with the new defaults; the numbers in the rows
above are the first delivery's.

#### E9's comparison, and why it is not "identical files"

Two groups of columns are expected to differ and were excluded from the comparison:

- `costUs`, `stSetup`…`stEscape` — the harness's own clock, which differs run to run;
- `bodyzone`, `nbody` — instrumentation that exists only because of this change. `bodyzone` is the
  frame's count of planned nodes inside an emitter disc; `nbody` is the cells the frame's emitter
  discs cover. Both are 0 with the emitter off, and that is the honest reading rather than a
  discrepancy: with the emitter off a body is rule 7 alone, and rule 7 puts nothing in a danger field.

With those excluded, **the six scenarios that predate this change are byte-identical against the
pre-change build** — every one of the twenty-one fields that can change a decision, on every one of
3 600 frames. That is the claim T9 makes, measured rather than asserted.

The three body fixtures cannot be compared against the old build — it does not know them. They are
compared against themselves with the switches off, and the result is narrower and still worth having:
the two runs differ on **`wp` and the path's numbers** and on nothing else, which is the feature doing
its job (the emitter bends the route) and not a decision changing. The one decision field that does
move is `score`, on `bodyclose` and `bodyfence`, and only because a candidate's `resolve` — the
fraction of the frame's threats its arrival point is clear of — is a property of the path. The chosen
candidate is identical on every frame: same action, same reason, same heading, same scale, same
durability.

### 8.7 What is not built, and it is §7's step 5

**No live run has been made.** `bootstrap.log` has never printed `bodies=N` from a real realm, the
`GetEnemies()` resolve has never been exercised inside the game, and `Radius`'s resolution on the
enemy element type is unverified against the shipped build — the code falls back to
`DefaultPlayerRadius` and counts it (`fallbackRadius=`), which is the honest failure, but the honest
failure is not a pass.

That is one session, exactly as §7 says: walk toward a ranged mob and read whether **the path bends
around it** and whether **the character still closes on it**. Everything else is now covered offline.

`WeightEnemyDanger` still ships at **0** and no measurement here changes that. The `body` and
`bodyfence` scenarios are the fixtures a sweep should be run against, and the answer will not be one
number: §3.2's argument is that the pad buys the fraction of a second before the bullet exists, so
the term that needs the harness is about the step after, not about standing further away.

> **THIS SECTION WAS WRONG ON BOTH COUNTS, AND §8.8 IS WHY.** A live run *had* been made by the time
> this was read — `20261016-144404-p36200`, 8 106 frames, `bodies 5.28 on 6697 of 8106` — so E1 was
> satisfied and the operator's report was the confirmation that the feature did nothing. And
> `WeightEnemyDanger` was at zero for a reason that turned out not to matter: the term contributed
> zero at *every* weight, because it read a point one frame away instead of a direction (§8.8.2). It
> now ships at **0.5** (§8.8.4).

---

## 8.8 The correction: the first delivery changed no decision

This section supersedes the headline of §8 and the conclusions of §8.1, §8.4 and §8.6. It is written
in the same form as the rest of the record — the measurement first, because every one of these
findings was invisible to the checks that were run.

### 8.8.1 The gate's body reach was admission's reach, and the two answers differ by 19×

§8.1 is right that the no-threat gate had to learn about bodies, and right that the pad must not enter
it. It is wrong about how far the gate must see. The reach it chose is rule 7's:

```
reach = body.Radius + playerRadius + budget        // 0.637 units at the harness's numbers
```

and that is the reach of the *admission* question ("could a candidate of this frame be refused").
Bodies have a second consumer — the planner's emitter field — whose reach is the planner's own target:

```
reach = speed * PathTargetSeconds                  // 12.3 units at 9.23 u/s — and cull is 12
```

Nineteen times further. The plan's §2.2 defect, "an enemy standing in the open is free space as far
as the search is concerned", lives entirely in that gap: a mob six units down the player's own heading
was beyond rule 7's reach, so the gate returned `NoThreat` before a candidate set existed, and the
route the planner had *just built around it* — `Plan` runs before the gate, deliberately — was
discarded unread.

The live run shows the shape of it without needing the body positions: `bodies 5.28 on 6697 of 8106
frame(s)` with `NoThreat=4109` of 6990 decisions. Bodies were in the snapshot on 83 % of frames and
the solver declined to think about them on 59 % of its decisions — every frame where the only hazard
was a body, which is the frame the plan exists for.

### 8.8.2 `WeightEnemyDanger` could not be told from an absent term

The score term read the candidate's arrival point:

```csharp
return _planner.DangerOverBodies(r.X, r.Y);      // one cell, ~0.14 units away
```

A candidate is a *direction*. A mob six units down that direction does not touch a point 0.14 units
ahead, so the term returned zero for every body further than the pad from the player. Measured on
`--scenario body` (one body six units out, pad 0.75, goal straight at it):

| run | chosen heading | score | path waypoint |
|---|---|---|---|
| `wenemy=0` | 0° — straight into the mob | 4.84 | 15° |
| `wenemy=5` | 0° | 4.84 | 15° |
| `enemypad=2,wenemy=5` | 0° | 4.84 | 15° |

Byte-identical scores at weight 0 and weight 5. Multiplying zero by five is still zero; the weight was
not an off-switch, it was a term that never ran.

The same class of error put a second zero in the term's guard: `if (!_planner.FieldReady)` — and
`FieldReady` is `_threatCount > 0`, a statement about the **bullet** envelope (`ProbeRadius` is built
from the first threat's tight radius). A frame with a mob in it and nothing in flight has no bullets
and therefore "no field", so the term was switched off on precisely the frames the pad exists for.
`PathPlanner.BodyFieldReady` is now the body half's own question, fed by a cell count
`BuildBodyField` maintains as it sprays.

### 8.8.3 The invariant that was missing, and why every existing check passed

E2 asserts on `Admit`'s refusal counters. The `PATH`/`BODY` invariant asserts on the route the planner
returned. **Neither reads a decision.** So on `--scenario body` the harness printed, in one run:

```
[PASS] BODY no planned route enters an enemy emitter disc
       0 planned node(s) inside a body's DangerRadius over 600 path(s) found
[PASS] E2  no emitted move sweeps an enemy body
       0 move(s) swept a body's collision disc
-- decisions --
  action  : None=600
  reason  : NoThreat=600
  frames that moved : 0 of 600 (0.0%)
```

The route avoids the disc; nothing was ever emitted; both checks pass. This is §5's own trap 11 ("an
instrument that cannot see the change cannot validate it") applied to the plan itself: the fixture was
named "the path must go round it" and it measured the path.

**E10 is the join**, and it is asserted on the decision:

> On every frame that carries a body whose **collision** disc lies across the segment from the player
> to the point the planner is aiming at, the decision must not be `NoThreat`.

The geometry is re-derived in the harness from `GoalValid`, the player's speed and `PathTargetSeconds`
rather than read out of the solver, for the same reason `CheckT5` asks the solver's own durability
predicate instead of copying it. On the pre-fix build it fails with `600 of 600` and names the frames;
on the fixed build it passes. `--scenario body` is the fixture that exercises it, and a run with no
bodies reports it as vacuously true rather than as a pass over nothing.

### 8.8.4 The other two defects, and the fix

Once the frame is evaluated, two more things had to be true before a mob changes anything, and neither
was:

1. **`IntentSafe` obeyed a heading that walked into a muzzle.** The intent early-out is composed of
   three questions about *bullets* (`StaticallyDurable`, `InsideAnyDisc`, `Admit`), so on a frame with
   a mob and nothing in flight it fired every time: the dodge took the wheel, re-emitted the player's
   own heading, and `WeightPath` — the only term that ever saw the distant body — was never consulted.
   It now also requires that the player's own heading does not run into an emitter disc within the
   planner's horizon. **This is a preference and not an admission rule**: nothing is refused, the
   frame falls through to the ordinary ladder, and the emitted move is still drawn from the admissible
   set. It uses the EMITTER radius (pad included), because a pad that could not be spent on a
   preference would have nothing to spend itself on; the gate uses the collision radius so `enemypad`
   still cannot decide which frames are evaluated.

2. **The weight shipped at zero.** With the term fixed to sample the body field along the candidate's
   own direction over the planner's horizon, the smallest weight that moves `--scenario body` off the
   straight line is **0.04**; it now ships at **0.5**, against `WeightPath` 1.0 and `WeightGoal` 0.9.
   `DRELAY_DODGE_W_ENEMY` moves it in one run.

The gate itself gained a second half, `AnyBodyAcrossTheRoute`, rather than a wider first one: a frame
is evaluated if the bullets are near **or** a body's collision disc is on the route to the goal. The
route test is the straight goal ray and not the planned path, because the planned path has already
bent around the body — asking it "did you touch the body" is reading the answer as its own premise.

### 8.8.5 What the harness says now

`--scenario body`, 600 frames, seed 1, the new defaults:

| | before | after |
|---|---|---|
| action / reason | `None` / `NoThreat` | `Move` / `Hysteresis` |
| frames that moved | 0 of 600 | 600 of 600 |
| chosen heading | 0° — into the mob | 15° — around it, the planner's own waypoint |
| rule 7 refusals | 0 | 0 (the body is six units out; rule 7 is not the mechanism here) |
| `BodyZoneNodes` | 0 | 0 — the route still never enters the disc |
| E10 | **FAIL**, 600 of 600 | PASS |
| `decide()` p99 (`--repeat 50`) | 33 µs | 87 µs |

The heading now agrees with the waypoint the planner produced: `wp=15`, `heading=15`, `bodyzone=0`.

### 8.8.6 The re-run of §8.6, on the fixed build

Every scenario is run at `--frames 600 --seed 1`; cost at `--repeat 50`; E9 against the pre-change
source compiled in a worktree at `ce254be`.

| # | result after the fix |
|---|---|
| E2 | **PASS**, unchanged: 12 000 `AdmissionRule.EnemyBody` refusals on `bodyclose`, 0 moves swept a body |
| E3 | **PASS, and now about a decision**: `body` emits 600 moves at 15°, `bodyzone` 0 over 600 paths found |
| E4 | **PASS**: mean admissible is identical at pad 0 / 0.75 / 1.5 / 3 in `body` (77), `bodyclose` (57) and `bodyfence` (25) — the pad still cannot refuse, and now cannot decide which frames are evaluated either |
| E5 | **PASS** at every pad: `bodyfence` keeps an admissible MOVING candidate on all 600 frames |
| E6/E7 | **PASS**, unchanged: T5, T6, T10 and the corner-cut invariant hold on all eight scenarios |
| E8 | **PASS with a named cost**: `peaceful` 17/23 µs, `bodyclose` 23/59, `bodyfence` 21/60, `body` 30/87 — the last is the scenario that now actually scores 77 candidates against a body field. Inside the 300 µs budget shared with the sampler and the sensor |
| E9 | **PASS**: with `enemies=0,emitter=0,wenemy=0` the build is byte-identical to `ce254be` on every decision column — 3 000 frames over the five pre-existing scenarios (`costUs`, the stage clocks and the new `bodyzone`/`nbody` columns excluded, exactly as §8.6 describes) |
| E10 | **PASS**, and it is the check that FAILED before the fix |

### 8.8.7 What is still not built

**No live run has been made against this correction**, and the reason is now a hard one: the live
recorder never writes the `bodies` key, so `trace.jsonl` from `20261016-144404-p36200` replays with
`enemy bodies: 0 of 7164 frame(s) carried one` and no live frame can be re-run against the new term.
The harness's reader has accepted a `bodies` array since `d4e8f46`; `TraceRecorder.WriteFrame` does not
write one. Until it does, the weight cannot be tuned against real mob geometry and E1 remains a claim
about a `bootstrap.log` line rather than something replayable.

That replay is otherwise a useful negative control: with no bodies it produces the **identical**
decision summary before and after this correction (`None=5112 Move=2052`, and the same eight reason
counts), which is what "the module is inert without a body list" looks like measured rather than
argued. The one invariant it fails is **T7** — `7 collision(s) the model never predicted` — which is
the pre-existing projectile-prediction gap the same run's own log reports as `false negatives: 7 (hit,
not predicted)`. It is not a body finding and this correction neither caused nor touched it.

Two further things are true and worth naming:

- **`fallbackRadius=11985`** on the live run. `EnemyReader.Status()` printed no "Radius did NOT
  resolve" clause, so the field resolved and roughly 28 % of the bodies that reached the radius read
  returned `Radius == 0` and were substituted with `DefaultPlayerRadius` (0.25). Whether that is the
  game's answer for those enemy types or the wrong field is unverified.
- **The `body` fixture is a statue.** A moving mob is just a different list per frame, and nothing here
  exercises a mob that walks onto the player — rule 7's "already inside" exception is unexercised on
  the fixed path.

