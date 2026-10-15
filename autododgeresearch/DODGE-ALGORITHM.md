# Dodge and preventive-escape algorithm

This is the design of what the hook **computes**. It is written against the verified client facts in
[CLIENT-FINDINGS.md](CLIENT-FINDINGS.md), it borrows deliberately and selectively from the reference
engine's dodge design (research summary in §12), and it derives its own constants rather than
inheriting any.

The measured evidence for the timing that justifies the whole approach lives in [TIMING.md](TIMING.md);
this document is written so that only §7 and §9 depend on it.

---

## 1. The thesis, in one paragraph

The client decides a bullet is hitting the player in `Projectile.HitCharacter()`
(`Objects/Projectile.cs:1514`), and at that moment it (a) computes the exact damage, (b) uses the
number only to draw it, and (c) sends `GmHit` to tell the server about a hit **the server has not
yet applied** — because nothing in the client ever writes `Player.Health`, which only
`GmHealthUpdate` touches ([CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §3). So there is a window that
begins at the collision decision and ends when the server's damage lands, and its length is the
client→server trip. The hook's job is to occupy that window: **first not be there, and only if that
fails, leave.**

That is the difference between the existing reactive auto-nexus and this design. The reactive rule
reads `GmHealthUpdate`, which reports damage that has *already been applied*; it is structurally
one quantum late and cannot ever be earlier. The hook reads the game's own collision decision, which
is structurally one trip early.

## 2. The pipeline, per frame

```
DarzaWindow.Update(float)                       hook #1, once per frame
  │
  ├─ 0. health/budget refresh                     read Player scalars, tick the frame counter
  │
  ├─ 1. THREAT SET                                maintained incrementally, see §3
  │       from hook #2 (spawn): add (GameId, startTime, desc, damage, hitsPlayer, playerOwned)
  │       from hook #3 (collision): mark consumed, cancel any commitment aimed through it
  │       from the frame tick:      retire bullets that no longer appear
  │
  ├─ 2. TRAJECTORY SAMPLING                       for each live threat, see §4
  │       call the game's own ProjDesc.GetPosition(t + Δ) for Δ on a fixed grid
  │       build a time-parametrised polyline, anchored on the live position
  │
  ├─ 3. EGRESS TEST                               see §5
  │       is the player currently inside a threat's hit disc?
  │       if yes: compute the cheapest real egress direction
  │
  ├─ 4. DECISION                                   see §6, §7
  │       a. is the incoming damage over the horizon survivable?  -> §6
  │       b. if not survivable by moving, is escape the right answer?  -> §7
  │       c. otherwise: choose a move  -> §5
  │
  └─ 5. ACT
          move:    publish a desired heading + magnitude to the actuation layer
          escape:  call World.RequestEscape()  (client's own nexus entry point)
          neither: do nothing
```

Steps 1–3 are observations. Step 4 is the only decision, and it is a **pure function** of a
plain-data snapshot — no game pointers, no allocation. Step 5 is the only side effect, and it is
confined to one place.

**Why the tick is the entry point, not a per-bullet hook.** `DarzaWindow.Update` runs after
`Client.DrainColdQueue()` has applied this frame's packets and before `CurrentScene.UpdateScene`
advances the world (`Graphics/DarzaWindow.cs:1763`, `:1841`), so at that instant the world is as
fresh as it will ever be and there is a whole frame of budget left to act. Deciding anywhere else
means deciding on state that is about to change.

---

## 3. The threat set

### 3.1 What qualifies

The client's own predicate is `Projectile.Hit()` (`Projectile.cs:1505`), which branches on
`HitsPlayer`. **That flag is the threat filter**, and it arrives as an argument at the spawn hook.
Nothing needs to be inferred about ownership or alliance.

> **AS IMPLEMENTED 2026-10-15, AND THIS PARAGRAPH HAD BEEN CORRECT BUT UNREAD FOR A WEEK.** The
> filter above was written into this document from the beginning and the code did not apply it: the
> sensor filtered projectiles by **type** and by nothing else, so the player's own shots — same type,
> same radius, and born inside the player's collision disc, which is what got them past every cull —
> went into the threat set. The first live session after the plan model measured the cost:
> **51.8 % of its threat records were the local player's own projectiles**, 959 of its 971
> "already touching" verdicts, and **255 of its 1140 moves**. The dodge was escaping the operator's
> own muzzle, and `stand` — the specification's preferred answer — was refused on every frame they
> fired, because `Decide` takes its `stand` early-out only when the player is not inside a disc.
>
> `TrajectorySampler` now resolves `Projectile.HitsPlayer` (`:131`) and `Projectile.LocalSeedDerived`
> (`:115`) and declines a projectile the client can never collide with the player, before it is
> sampled. The full record, the removal experiment and the two fields are in
> [SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md) §12. **The lesson is this section's own: a
> sentence in a specification is a filter only if the code asks the question, and both of this
> project's threat-set filters that were "obvious" turned out to be missing rather than simple.**

### 3.2 The record

One record per live bullet, keyed on `(GameId, startTime)` — see the pooling hazard in
[HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §5.4. The `startTime` is what makes the key safe across
pool reuse.

| field | source | notes |
|---|---|---|
| `gameId`, `startTime` | spawn hook | the identity |
| `x, y` | spawn hook (spawn position) | the *live* position is re-read every frame from the object |
| `angle` | spawn hook | radians |
| `sin, cos` | derived | the game keeps its own; we recompute to match exactly |
| `desc` | spawn hook | pointer to the trajectory descriptor — **used, never interpreted** |
| `damage`, `damagePercent`, `manaDamage`, `manaDamagePercent`, `barrierDamage*` | spawn hook | the raw damage payload |
| `criticalHit` | spawn hook | |
| `localSeedDerived` | spawn hook | changes the armour-pierce accumulation |
| `radius` | live object each frame | `Projectile.Radius`, which the spawn path sets from `Desc.Size` |
| `lastSeenFrame` | frame tick | for retirement |
| `missStreak` | frame tick | for the two-strike retirement rule |

### 3.3 Retirement — the two-strike rule

Inside the process a bullet that hits a wall is `Remove()`d in the same frame
(`Projectile.cs:1227-1243`), so **ghost bullets do not exist** — the limitation the packet-only
feasibility study identified as fundamental is simply absent here. But a *single* missed read can
still happen, so retirement is by evidence, not by quota:

```
present                        -> missStreak = 0, keep
absent and age < minAgeMs      -> keep, and do NOT accrue a miss
absent otherwise               -> ++missStreak; retire once missStreak >= 2
```

`minAgeMs` exists because a bullet observed at spawn may legitimately not yet appear in whatever
live-collection view is used, and charging a miss for that would retire live bullets. Two strikes is
"the smallest value that survives a single incomplete read". **A failed read is not evidence of
absence** — if the enumeration itself fails, skip reconciliation entirely rather than treating the
result as "nothing is present".

This is the reference engine's `ProjectileRetirePolicy::Reconcile`, and it is the single most
portable piece of their projectile tracker: it was written for exactly the same
"no server packets, only memory" situation we are in.

---

## 4. Trajectory: call the game, do not reimplement it

### 4.1 The decision

`ProjDesc.GetPosition(int time, float sin, float cos, out float x, out float y, int gameId)`
(`DarzaCore.Data/ProjDesc.cs:1149`) is a **virtual** method with a subclass per trajectory family —
straight, wavy, parametric, boomerang, orbiting, curving, accelerating, wall-bouncing, homing. It
resolves `time` as **milliseconds** (`dt = time / 1000.0`, float pool[70] = 1000.0) and `Speed` as
world-units per second ([CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §2.10).

**The hook calls it. It does not reimplement it.** Three reasons, and the third is decisive:

1. The reference engine, faced with the same problem in IL2CPP, also **calls the game's own
   function** for the trajectory and does not carry a family model anywhere in its source. The one
   thing it *does* own is the sampler (§4.2).
2. The family set is large and includes a per-bullet seeded LCG (`LCG.Value.Initialize((uint)gameId)`,
   `ProjDesc.cs:1223`) for parametric jitter. Reproducing that bit-for-bit is a research project with
   no upside.
3. **Calling it makes the prediction exactly consistent with the collision by construction.** The
   game's own hit test is `DistanceSquared(player.Position) < r²` (`Projectile.cs:1573`). If both the
   dangerous-position model and the hit model come from the same function, the dodge cannot disagree
   with the engine about where a bullet will be — which is the failure mode that makes dodges feel
   haunted.

To call it, the hook needs a callable pointer for the *virtual* method. Resolve it once at startup
from the projectiles' own `Desc` objects (the concrete type's slot), cache it, and call it directly.
If resolution fails, fall back to §4.4 and **say so in the log** — the reference engine's own warning
is that a failed resolve means "curved shots are being predicted as straight lines", which is a
silent, dangerous degradation.

### 4.2 Sampling, and why the step matters more than the model

The reference engine's hardest-won trajectory lesson is not about the trajectory at all: sampling a
path at *fractions of its lifetime* silently aliases anything periodic. At a 2 s lifetime a 48-sample
cache gives an 87 ms step and is fine; at 10 s it gives 435 ms, and a ~600 ms wavy period is
reconstructed as its chord — "dangerous where the bullet is not, safe where it is".

So the sampler is fixed-step and bounded:

| parameter | value | derivation |
|---|---|---|
| step | **30 ms** | below the period of any plausible weave; matches the reference's `kTraceStepMs` and needs no adaptation because it is a sampling rate, not a game constant |
| horizon | **600 ms** | our own, see §4.3 |
| samples | `ceil(horizon / step) + 1` = **21** | one point at Δ=0 (the live anchor) |
| last sample | pinned exactly on `min(lifetime, horizon)` | so consumers can distinguish "the bullet dies here" (a fact) from "we ran out of budget" (an unknown) — the `tailAtShotEnd` flag |
| lifetime source | `Desc.Lifetime` | in the same units as `time`; see the normalisation note below |

*Lifetime normalisation.* The reference engine found this necessary and it is worth copying as a
defensive measure: a lifetime below ~250 is ambiguous and is treated as seconds rather than
milliseconds, with a 2000 ms default when the value is unreadable. We can do better — we know
`GetPosition` treats `time` as milliseconds — but the *default*, not the unit, is what protects
against an unread field: **if `Desc.Lifetime` cannot be read, assume 2000 ms rather than 0**, because
a zero lifetime makes a bullet harmless and that is the dangerous direction to be wrong in.

### 4.3 The horizon, derived rather than copied

The reference engine's 800 ms is calibrated to a 5 Hz server (200 ms per tick, so 800 ms = four
ticks). **That reasoning does not transfer**, and copying the number would be copying a coincidence.

Derive ours from our own quantities:

```
horizon  ≈  (the time the player needs to clear the hit disc)
          + (the time an escape takes to leave the client)
          + (a margin for the decision being made one frame early)
```

- clearing the disc: `2 · r_eff / playerSpeed`;
- escape dispatch: one frame, plus whatever the measured relay/injection path costs
  ([TIMING.md](TIMING.md));
- margin: two frames.

With `r_eff` and the tile scale still to be measured, the working value is **600 ms**, and the rule
is that it must be **re-derived from the measurements in [TIMING.md](TIMING.md), not inherited**.
The horizon appears in exactly two places — the sample count and the durability score (§5.5) — so
retuning it is a one-constant change.

### 4.4 Sampling the future, honestly

Three disciplines, each from a documented failure in the reference engine's history:

1. **Anchor on the live position, never on the spawn position.** Re-read the bullet's `Position`
   every frame and translate the whole predicted polyline by `live − predicted(now)`. This makes
   model error *relative* rather than *cumulative*: the sample at Δ=0 is exact by construction, and
   the error at the end of the horizon is only however wrong the model is over 600 ms. The packet-
   only approach had no equivalent, which is why every one of its predictions drifted without bound.
2. **Freeze past the end of the trace, never extrapolate.** If the prediction ends before the
   horizon — because the model is unreliable for that family, or the trace was truncated — hold the
   last trusted point still. Freezing *under*-counts danger; extrapolating paints ghost lanes. When
   danger is under-counted the instantaneous test in §5.1 is still authoritative and will veto the
   move, so the failure is bounded; when it is over-counted the dodge refuses cells that are safe
   and the player dies of hesitation. **The asymmetry says: freeze.**
3. **Only a verifiably linear trajectory may be projected past its trace.** A bullet whose sampled
   points all lie within a small tolerance of the straight line through its endpoints is one we can
   extend with confidence; a wavy or orbiting one is not. The reference engine gates this behind a
   single predicate and it is the only place a lane is ever extended. Copy the gate, including the
   tolerance idea: every interior sample within a small fixed distance of the endpoint chord, times
   strictly increasing, span greater than zero.

---

## 5. The solver

### 5.1 The instantaneous floor (never relaxed)

The game's own predicate, applied as a hard constraint:

```
dangerous(bullet, point) ⇔ (point − bullet.position)² < r_eff²
r_eff = player.Radius + bullet.Radius          // Projectile.cs:1597
        possibly widened by Desc.Proximity when the bullet is not player-owned   // :1555
```

**This is a circle test, verified, not inherited.** The reference engine's model is a per-axis
Chebyshev square for bullet hitboxes and it goes to some length to explain that treating that square
as a circle over-stamps corners by ~21 % — that analysis is inapplicable here: our client's test is
`DistanceSquared < r²` (`Projectile.cs:1573`), so the correct model is a circle and the reference's
central geometric caveat simply does not arise. It is worth stating plainly because it is the kind
of thing that gets copied reflexively.

Note also that `r_eff` already contains the player half, so unlike the reference engine we must not
add a player half again — their `hitHalf` deliberately excludes it and requires the caller to add
it; ours does not.

### 5.2 Candidates

```
1 stand (magnitude 0)
+ 24 headings × 3 radii
+ 1 intent-aligned heading at full magnitude
+ 1 egress-direction point (only when §5.4 says we are inside a disc)
+ 1 goal heading at full magnitude, + GoalFanSteps more either side of it
= 77 (80 with the default one-step fan)
```

- headings at `2π·k/24`, giving 15° resolution — the reference's 24-heading engine (`zDodge`) is the
  closest analogue to our budget, and its 120 candidates is a reasonable upper bound;
- radii as fractions of the per-frame move budget `b`: `{0.35, 0.7, 1.0}`;
- `b = playerSpeed · frameSeconds`, i.e. **exactly the distance the player can legally cover this
  frame**. Using the legal budget as the outer radius is what keeps every candidate a move the
  server will accept;
- the intent and egress points exist because a 15° ring quantises away "keep doing what the player
  asked" and "get out of the thing I am standing in", and both of those are frequently the right
  answer;
- **the goal fan, added 2026-10-15**, is the same argument for the third thing a 15° ring quantises
  away: "carry on in the direction I am already travelling". The ring's headings are multiples of 15°
  from world north and the measured course is whatever angle the character is actually moving at, so
  the exact goal heading needs a candidate of its own — the operator's own example is 13.7°. The
  `± 1` neighbours at full magnitude are what let the dodge leave that line by the smallest amount
  that clears an obstacle instead of snapping to the nearest ring heading. See
  [SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md) §11.3.

**Degradation under budget pressure removes radii, not tests.** If the measured cost exceeds the
slice, drop to the outer radius only (24 candidates). The admission rules below are never relaxed.

### 5.3 Admission (hard, in order)

A candidate is admissible only if all of these hold. Each is cheap, and the cheap ones run first.

1. **in bounds** — the candidate is inside the map;
2. **walkable** — the destination cell does not block the player. *Source*: the client's own
   **collision grid**, `private byte[,] World.<grid>` (`World.cs:4318`), written from
   `MapElement.Collision` at `World.cs:9677` and fed by `GmTiles`. A square byte array indexed by
   tile coordinate, so this is a direct read with one resolved offset
   ([CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §5.4). The `Collision` byte is 0 none / 1 solid /
   2 solid3D;
3. **path clear** — the swept segment from the player to the candidate does not cross a blocker.
   Endpoint-only checks let a candidate "through" a thin wall, and a step here is a meaningful
   fraction of a tile;
4. **not inside a bullet at arrival** — apply §5.1 at the candidate's position;
5. **not inside a bullet along the way** — apply §5.1 to the swept segment against every bullet's
   current disc;
6. **temporally clear** — for every bullet, the plan's body and the bullet's body are never inside the
   collision radius of each other on the same clock, within the lookahead. This is the test that
   makes the dodge look intelligent: it is what allows threading a gap that exists only at a
   particular moment, and it is the single highest-value algorithm in the whole design (§5.5). **As
   implemented it is a spacetime walk rather than a path-against-path test** — see the note in §5.5
   and [SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md) §11;
7. **not inside an enemy body** — a hard exclusion, with one exception: if the player is *already*
   inside the body, the test is dropped for that body. Without the exception every move out of an
   overlapping circle sweeps it, which would trap the player under a mob that walked onto them.

**Rule 6 is one-sided in the same way rules 4 and 5 are**, and the reasoning is §5.3's own: inside the
decision window a violation is refused outright, and beyond it a candidate is refused only when it
gets *closer* to that bullet than standing still would. A bullet the player is already inside, or one
arriving faster than they can move, must not be allowed to refuse the whole candidate set — that is
the mechanism by which a solver reports "nothing is safe" on a frame where something is.

The exception in rule 7 is not a special case invented here; it is the reference engine's documented
reasoning for the same rule, and the same escape applies to AoE volumes.

### 5.4 Standing still is a real candidate

`stand` is admitted only if the player's current position passes the **full-horizon** version of
rule 6 — not the one-frame version. A position is "durably clear" if nothing threatens it for the
whole horizon. This matters because standing is the one choice where "safe now" and "safe later" are
the same question, and it is the answer the dodge should prefer when it is true: a player who does
not move does not walk into anything.

If `stand` is durable and the player is not already inside a disc, the dodge **does nothing**. That
is a feature: the best dodge is usually no dodge.

### 5.5 The temporal test — the core algorithm

For each bullet, we have a sampled polyline of predicted positions with times, anchored at Δ=0 on the
live position (§4). For each candidate, we have the player's swept path over the same horizon.

The test is: **does the player's swept path intersect the bullet's predicted path at a
compatible time?** Concretely, for each pair of (player step segment, bullet path segment), test
segment-vs-segment, then check whether the times can coincide. Two refinements matter:

- **sample the player's own motion, do not treat it as instantaneous.** A fast player covers a real
  fraction of a tile per frame; the intersection test must be between two swept segments over
  overlapping time windows, not between a point and a segment.
- **interpolate the bullet's path, not just its knots.** Between two samples the bullet is somewhere
  on the chord, and at 30 ms steps with a fast bullet the chord is short enough that linear
  interpolation is accurate. This is the reference engine's reasoning for its sample step and it
  holds here.

> **AS IMPLEMENTED 2026-10-15, THIS SECTION IS TRUE BUT STRONGER, AND THE DIFFERENCE IS WORTH THE
> PARAGRAPH.** "Does the player's swept path intersect the bullet's predicted path at a compatible
> time" describes two polylines and asks when they cross. What the solver now asks is *where will the
> bullet's body be when my body is there* — one subtraction, walked over a lookahead — and the two are
> not the same question. They agree for a player who is standing still, which is why the older form
> looked right for so long. They come apart for a player who is **moving**, and the older form is
> wrong in the direction that costs behaviour rather than safety: the candidate's arrival point was
> compared against the bullet's whole remaining polyline, so a bullet's entire path read as a wall
> whether the bullet would be there in 600 ms or had been there 600 ms ago. The relative-motion form
> is exact for two objects that are each linear between knots, needs no time-compatibility machinery,
> and is stricter between samples — at 24 units/s a bullet moves 0.72 units between knots, wider than
> the 0.41-unit disc. See [SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md) §11.1–11.2.

The *durability* of a candidate is then the time until its first temporal intersection, and the
score uses it (§5.6). The reference engine's version of this — an arrival-time march over a bounded
time grid, with a "trust prefix" that refuses to credit clearance beyond the point where the
prediction is actually trustworthy — is the single most valuable algorithm to port, and the trust
prefix is what stops a blind tail from scoring as "clear for 600 ms".

### 5.6 Scoring

Safety is a **hard constraint**; preference is a **score over the already-admissible set**. That
ordering is what makes every later feature safe by construction — a goal, an orbit or a formation
step can never move the player into a bullet because it can only rank options that already passed
§5.3. Adopt it.

```
score =  w_clear      · min(clearance, clearComfort)
       + w_resolve    · fraction of the frame's relevant threats this plan stays clear of
       + w_durable    · durability                     // normalized time-to-first-danger
       + w_intent     · intentAlignment
       + w_goal       · goalProgress                   // toward the point in front of the character
       + w_heading    · headingContinuity              // dot with the previous chosen heading
       + w_lateral    · lateralness                    // preference for sidestepping over fleeing
       − w_move       · (candidateDistance / b)
       + w_standBias  · isStand
```

with initial weights, all to be tuned in the offline harness rather than in a live run:

| term | weight | why it is there |
|---|---|---|
| `clearance` | 1.20 | how close the two bodies ever come over the lookahead; the geometry term |
| `resolve` | 1.50 | how many of the bullets actually aimed at the player this plan stays clear of. Added 2026-10-15; without it nothing in the score said whether a move ACHIEVED anything |
| `durability` | 0.60 | normalized time-to-first-violation. **Time-correlated since 2026-10-15**; it measures the first moment the plan is inside a bullet's disc, and it ignores bullets the player is already standing in, whose damage is committed |
| `intent` | 0.40 | the direction the player's hands asked for; §5.7 |
| `goal` | 0.90 | progress toward the imaginary point in front of the character — the measured course when moving, intent when not. Added 2026-10-15; see §5.7 and [SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md) §11.3 |
| `heading continuity` | 1.00 | anti-jitter; the reference engine's largest weight |
| `lateralness` | 1.20 | sidestepping keeps the player's options open; fleeing radially can run into more of the volley |
| `move cost` | 0.25 | do not travel further than necessary |
| `stand bias` | 0.15 | small nudge toward doing nothing, deliberately below the durability weight |

**`clearance` and `durability` are not the numbers this table first carried, and the reason is one
measurement.** The original `clearance` measured the distance to where the bullet **is** — largest
exactly when the dodge does not need to move — and the original `durability` normalized by the 50 ms
decision window, so anything clear for more than one frame scored 1.0. Both were constants on the
frames the score exists for, and the winner was decided by the move penalty, which is a penalty on
distance: the dodge took the smallest available step, every frame, while the bullet closed. Live, the
chosen candidate's durability was **p50 17 ms** on the third recording. Three of the terms have now
been rewritten and the measurement that says so is in
[SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md) §10.3 and §11.6.

### 5.7 Intent preservation

**This is now a hard requirement rather than a preference**, because AoE and bomb damage are
deliberately **left to the player** ([RISKS.md](RISKS.md) §2b — accepted scope). If the auto-dodge
fights the direction the player is steering, it takes away the one tool the player has for the damage
channel the dodge does not cover. A dodge that is right about bullets and wrong about the player's
intent makes the operator worse off than no dodge at all.

The player's steering is read from `GameScene.GameInput` (`Moving`, `MoveAngle`, `MoveSpeed`) — the
same values `World.cs:12690-12692` consumes one line later to compute the movement delta.

**`MoveAngle` IS AN INPUT-FRAME NUMBER AND THE COURSE IS NOT, AND THAT IS THE WHOLE OF THE CONVERSION.**
`World.cs:12692` computes `angle = MoveAngle + (-WorldRotation + aimOffset)`, so the world heading the
player is asking for is `MoveAngle - WorldRotation`. Everything the solver reasons about is world
positions, so the intent is converted on the way in and the chosen heading is converted back on the
way out (`SteeringPilot` writes `worldHeading + WorldRotation`). A camera at 13.7° makes that a
13.7° error in the goal; the recorded sessions reach 359.9°.

**The goal is the second thing in this section, and it is not intent's replacement — it is its
counterpart.** Intent is what the hands asked for. The **course** is what the body is doing, measured
from the client's own frame-to-frame displacement, and *the imaginary point in front of the character*
is projected along it. When the character is not moving there is no course and intent is the fallback.
The two answer different questions — "where do you want to go" and "where are you going" — and the
score carries both, `w_intent` and `w_goal`, so the trade between obedience and line-keeping is one
number rather than an architecture. [SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md) §11.7 is
the measurement of that trade on the third recording and the operator's call to make.

The policy, adapted from the reference engine's most carefully reasoned ladder (their PJDodge
override ladder, which is the clearest statement of "how much may intent buy" in the whole
reference):

1. **no threat and not in a hazard** → change nothing at all;
2. **immobile** (`speed <= 0` — paralyzed, cutscene, or anything else the speed law zeroes) → do not
   pretend to dodge; report "immobile" and let §7 handle survival;
3. **intent heading is safe** → do not override. `intentAlignment` in the score handles this
   automatically, but the explicit early-out keeps the common case free;
4. **a safe override exists and the threat is not imminent** → prefer the override that stays
   closest to intent;
5. **the threat is imminent** → take the best survival option, ignoring intent;
6. **nothing is safe** → take the option that maximises time-to-impact and does not enter *another*
   hazard.

Two rules on top of the ladder, both from documented reference-engine bugs:

- **hysteresis**: once a heading is chosen, keep it while it remains admissible and the new best does
  not beat it by a margin, with a short dwell. Without this the dodge oscillates at 60 Hz and the
  player vibrates instead of moving. The reference engine uses a ~100 ms dwell for its reactive
  engine and 250 ms for its planning engines; tune in the harness.
- **a reversal that safety requires is never smoothed away.** The hysteresis must not be able to lock
  the player into a heading that has become unsafe. Admissibility is re-checked every frame, so this
  falls out of the design — but it is the property to test, because it is the one an over-eager
  hysteresis breaks first.

**The one exception to intent preservation.** Because the player is responsible for AoE, the dodge
should *not* try to steer them out of an AoE telegraph. Doing so would override the manual dodge and
could push them into a bullet. The rule is: **the dodge reasons about bullets; AoE is the player's,
and the dodge's only obligation is to not fight them.** An AoE volume may be a *score* input — a mild
preference against standing in one — but never an *admission* filter that removes candidates, because
that is the mechanism by which it would start overriding intent.

---

## 6. Damage estimation — call it, do not compute it

For the escape decision (§7) we need "will the incoming damage kill me, or is it survivable by
moving?". The tempting move is to reimplement `Entity.GetDamageTaken`, and it is now *possible*:
every constant has been recovered ([CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §2.9), giving
`max(d·(0.7 − 0.007·min(armor,100)), 15·d/(25·armor/max(d,1) + 15))` floored at 1, on top of a
`CalculateBarrier` step, an `ArmorBroken` step, a `Vulnerable` step, an `Armored` step, a
`Protected` step, and a total-immunity check against status effects 5/15/24.

**Do not.** Call `Entity.GetDamageTaken` on the live player with the live arguments. Reasons:

- the eight mitigation steps read live status-effect tiers, live barrier bookkeeping and live armour;
  a reimplementation must mirror all of them, and being wrong in the *safe-looking* direction
  (under-estimating damage) produces a nexus that fires one hit too late;
- the same argument applies to `HitByProjectile`'s own preamble, including the
  `LocalSeedDerived → min(1.0, pierce + World.Player.ArmorPiercing)` accumulation (`Entity.cs:2541`);
- it is one call, once per decision, not in a hot loop. The cost is irrelevant.

The hook's damage estimate is therefore **the game's own number**, which is also the number the
player will see on screen — so the hook's log and the game's UI can be compared directly, which is a
large testing advantage.

---

## 7. Preventive escape

**This is the last gate of one decision, not a feature with its own trigger.** Since 2026-10-13 the
plan merges the dodge and the escape into a single phase, and §7 is what that phase reaches for when
§5's solver can find no admissible move that avoids lethal projected damage
([IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md), Phase 3). The precedence rule that follows from
that — the escape is authoritative, the dodge yields, and the interaction is a verified hazard rather
than a hypothetical one — is [RISKS.md](RISKS.md) §3d.

### 7.1 Why this replaces the reactive rule

The reactive auto-nexus fires when a `GmHealthUpdate` reports HP below a threshold, i.e. **after** the
damage. The measured evidence for how late that is lives in [TIMING.md](TIMING.md); what matters for
the design is the shape of the improvement: the hook's decision point is one client→server trip
earlier, and the trip is the same one the escape itself must make. In other words, the hook gets a
full round trip of extra warning by construction rather than by tuning.

### 7.2 The decision

Run only when moving cannot save the player, and only in the order below — the cheap and certain
checks first, and the first refusal wins:

| # | condition | action |
|---|---|---|
| 0 | disabled by config | nothing |
| 1 | player is dead, in spirit mode, or the world says safe area | nothing |
| 2 | no live HP reading in this world | nothing |
| 3 | HP is zero | nothing |
| 4 | a durable-safe move exists **and** the projected damage is survivable | move (§5) |
| 5 | a durable-safe move exists **and** the projected damage is not survivable but a move reduces it below lethal | move, urgently |
| 6 | the projected damage over the horizon is **lethal and unavoidable** | **escape** |
| 7 | the escape budget is spent, or inside the re-arm window | nothing, and log why |

Gate 6 is the whole feature. "Lethal and unavoidable" is evaluated as: the maximum total projected
damage across all bullets whose predicted paths intersect every admissible candidate's swept path —
i.e. damage that cannot be dodged — exceeds current effective HP.

### 7.3 Asymmetry of the two errors

This is the most important policy statement in the document.

| error | cost |
|---|---|
| **escape when it was not needed** | a nexus scroll, a ruined run, an angry operator |
| **fail to escape when it was needed** | the character dies |

The reactive rule is tuned to the first error, because with a single `GmHealthUpdate` sample it
cannot distinguish "a big hit landed" from "a big hit is coming". The hook *can*, because it knows
the bullet. That means it can afford a **lower effective threshold and a longer decision window**,
and it should — but the two settings must remain separately tunable, with the escape-off case
(`dryRun`) as the default, exactly as the existing module ships.

### 7.4 Escaping legitimately

Call `World.RequestEscape()` (`World.cs:12984`). It is the client's own nexus entry point, it runs
the client's own gates — `PlayerDead`, `SafeWorld`, the safe-area flag, the world-entry grace window,
`Player.Totems`, the cast state — and it sends either `GmEscape` or the cast-state pair
(`:13002`, `:13011`). Calling it is therefore **indistinguishable from the player pressing the
nexus key at that instant**, which is the one behaviour the server is known to accept.

The alternative — emitting the packet ourselves — is worse in three ways: it skips the client's
gates, it duplicates a writer the Java relay already owns with correct framing and logging
(`docs/INJECTION.md` §1.4), and it forfeits the client's own world-entry and totem logic. Do not
reimplement a gate that already exists and is already correct.

---

## 8. Budget

The frame budget is fixed by [HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §7 at **≤ 300 µs**. Where
it goes:

| stage | budget | note |
|---|---|---|
| threat-set maintenance | 20 µs | O(live bullets); retirement is a scan |
| trajectory sampling | 100 µs | `21 samples × live bullets` calls into `GetPosition` — the dominant cost, and the one to measure first |
| candidate admission | 100 µs | 77 candidates × bullets, with an early-out bounding-box rejection per bullet before any segment test |
| scoring | 10 µs | trivial |
| dispatch | 10 µs | one write, or one call |
| **headroom** | 60 µs | deliberately unspent |

The trajectory sampling cost is the design's main performance risk, because it calls into the game's
own virtual method once per bullet per sample. Three mitigations, in order of preference:

1. **cull before sampling** — only sample bullets whose *current* position and heading could plausibly
   reach the player within the horizon (a radius-and-closing-speed pre-filter). In a typical fight
   most bullets are not threats.
2. **sample lazily** — build a bullet's polyline on the first frame it survives the cull, and rebuild
   only when its live position deviates from the model by more than a tolerance (which is the same
   signal the reference engine uses for its clock calibration). Bullets that fly straight are then
   sampled once.
3. **degrade to the outer ring** — fewer candidates to test against, same sample set.

---

## 9. Constants, and where each one comes from

**Every number below is a starting value to be tuned in the offline harness, and none of them is
inherited from the reference engine.** The right-hand column is the point: this table is the
antidote to copying constants that were calibrated to a different game.

| constant | starting value | derivation / status |
|---|---|---|
| sample step | 30 ms | sampling rate, not a game constant — portable |
| horizon | 600 ms | **ours**; must be re-derived from [TIMING.md](TIMING.md) and the measured tile scale |
| move budget `b` | `playerSpeed · frameSeconds` | from the game's own `TilesPerSecond` law, resolved (row below) |
| headings | 24 | budget-driven |
| radii | `{0.35, 0.7, 1.0}·b` | budget-driven |
| `r_eff` | `player.Radius + bullet.Radius` | **read live**, not assumed; `GameObject.Radius` defaults to 0.25 |
| player movement speed | `(3.5 + 5.0·(SPD+10)/75) · slow · speedy` tiles/s | resolved from the pool: **3.5 tiles/s at SPD 0, 8.5 at the cap**; `FearOfTheBull` clamps SPD to 40. The reference's `4.0 + 5.6·(SPD/75)` would be ~13 % too generous |
| hysteresis dwell | ~100 ms | to be tuned; the reference engine's 100/250 ms are theirs |
| retirement strikes | 2 | from the reference engine's retirement policy, which is portable reasoning, not a tuned number |
| `minAgeMs` for retirement | ~1 frame | ditto |
| linear-motion tolerance | small fraction of a tile | ditto; the reference uses 0.002 tiles in *its* scale, which means "essentially exact" |
| horizon cull distance | a few player-widths | ditto |
| damage-immunity effects | 5, 15, 24 | **resolved from the pool**, verified against `Entity.cs:584` |
| damage constants | 0.7, 0.007, 100, 15, 25 | **resolved from the pool** — recorded for understanding only, since we call the function |
| `TilesPerSecond` constants | 40, 2.0, 10.0, 3.5, 5.0, 75.0 | **resolved from the pool**; supersedes the `4.0 + 5.6·(SPD/75)` guess |

Three deliberate omissions from the table, because they are the reference engine's constants and
would be actively wrong here: its `kServerTickSec = 0.2` (a 5 Hz server), its arrival-margin and
latency-pad tile fractions (calibrated to its collision geometry), and its `kPlayerHalf = 0.2139`
(its player is a point with a threshold; ours has a real radius and the game adds it for us).

---

## 10. Invariants worth testing

> **IMPLEMENTED, AND FOUR OF THEM ARE ASSERTED ON EVERY RUN OF THE HARNESS.** Invariants 1 to 4
> (T5, T6, T10, T7 in IMPLEMENTATION-PLAN.md's table) are checked by
> [`hook/tools/SolverHarness`](hook/tools/SolverHarness). T5, T6 and T10 hold over five synthetic
> scenarios; **T7 is wired and has never been run**, because it needs frames from a played session —
> `DRELAY_TRACE=1` records them and [SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md) §6 is
> the command. Read that document before changing anything in §5: it records seven defects that the
> text below does not anticipate, including one place (§5.5's temporal test) where this document's
> own sentence is too coarse to implement as written.

Each of these is a property that can be asserted offline, which is the only place a dodge can be
tested cheaply:

1. **`stand` is never rejected while the player is durably safe.** Otherwise the dodge moves for no
   reason.
2. **An admissible candidate is never inside a bullet's current disc at its arrival time.** Assert it
   for every emitted move, over a replay.
3. **The chosen heading never changes while it stays admissible and no alternative beats it by the
   hysteresis margin.** Jitter is measurable as a heading-change rate.
4. **Predicted and actual collision agree.** The strongest possible test: log every predicted
   first-impact time and every `HitCharacter` call **that returned `true`** — the method is the
   client's per-frame predicate, so its `false` entries are questions and not collisions — and assert
   the model predicted the ones that happened. This is the hook's ground truth, and it is only
   available *because* we hook the collision.
5. **A re-rented `Projectile` never inherits state.** Key on `(GameId, startTime)`; assert that a
   `Reset` observation clears the record.
6. **The hook never exceeds its frame budget**, asserted as a p99 over a replay.
7. **With the dodge disabled, the hook's presence changes nothing observable** — the same packets,
   the same timing. This is the regression test that protects the user's game from the tool.

Invariant 4 is the one to build first. It is the only test that can tell us whether the trajectory
model is right, and it is available without any live play at all if a recorded position trace
exists.

**How that worked out, for the next reader.** Invariant 4 was built first as specified, and the
recording it needs did not exist — Phase 2 keeps counts, not traces — so `TraceRecorder` was written
to produce one. Invariants 1 to 3 turned out to have teeth without any live play at all: five
synthetic scenarios, each with a known correct answer, found seven defects in the solver on the
day it was written. **Invariant 4 remains the one that needs a session**, and it is the only one
whose failure would mean the trajectory model itself is wrong rather than the solver's use of it.

---

## 11. Explicit limitations

- **It cannot fix an unavoidable volley.** If the projected damage exceeds effective HP no matter
  where the player stands, the only answers are escape and not being there in the first place. The
  dodge's value is in making that case rarer, not in making it survivable.
- **Terrain awareness is only as good as the resolved accessor.** If the client's walkability test
  cannot be called, the dodge loses rules 2 and 3 of §5.3 and will sometimes aim through a wall. It
  logs this and it is a visible capability loss, not a silent one.
- **It does not model other players' bodies** unless they are enemies. In a crowded world that is a
  real omission and it is the same class of gap the reference engine's audit documents about its own
  caps.
- **It cannot dodge what it cannot see.** A damage source with no `Projectile` object is invisible to
  this design and is handled only by §7.
- **AoE and bomb damage is deliberately out of scope, by operator decision.** It is left to the player
  to dodge manually, which is a legitimate trade: it keeps the first version's threat model to one
  well-understood object type. **But it converts intent preservation into a requirement** — see §5.7
  — because a dodge that overrides the player's steering has taken away the tool they need for the
  channel it does not cover. The mechanism that makes the trade safe is the rule that an AoE may be a
  *score* input but never an *admission* filter.
- **Wall-bouncing projectiles are not yet predictable** (`WallBouncePath`), and the parametric
  families draw their spawn phase from a per-bullet seeded LCG. Both are handled correctly when the
  game's own `GetPosition` is called (§4.1) and neither is handled by any reimplementation — one more
  reason not to write one.
- **Immobile means immobile.** `Paralyzed` (and any other effect the speed law zeroes) must shut the
  dodge off entirely rather than produce a dodge the player cannot execute. `Slowed` must scale the
  move budget down with it. Both follow from deriving `b` from the game's own speed law (§9) rather
  than from a nominal speed, and both are worth an explicit test.
- **The escape decision inherits `RequestEscape`'s own gates.** In a safe area it is a no-op; in the
  world-entry grace window it is instant; and **with the `Impermanence` totem** it becomes a channel
  that cancels on movement, which interacts with the dodge and must be tested explicitly
  ([RISKS.md](RISKS.md) §3d, [VERIFICATION.md](VERIFICATION.md) §5.1). The ordinary instant path is
  **not** cancelled by movement.

---

## 12. What was borrowed, and what was deliberately not

The `drelay` project has a prior assessment of the reference engine at
`work/auto-dodge-feasibility.md`, written from the packet-only point of view. That document's central
conclusion — that a packet-only implementation cannot reach a good dodge because every good piece
lives downstream of a per-frame memory read — is still correct, and it is exactly why this design
exists. What changes is that we now *have* the per-frame read.

### Borrowed (the reasoning, not the numbers)

| idea | from | why it transfers |
|---|---|---|
| **two-strike retirement with a min-age grace** | project-retire policy | designed for exactly the memory-only case we are in |
| **fixed-step sampling with a pinned final sample and an explicit "shot ends here" flag** | trajectory cache | pure sampling discipline; the aliasing failure it prevents is scale-independent |
| **anchor on the live position every frame** | lane re-anchoring | removes cumulative drift; we can do it every frame |
| **freeze past the trace, never extrapolate** | conservative freeze | the failure asymmetry is the same |
| **only a verified-linear trajectory may be projected** | linear-motion gate | ditto |
| **the time-parametrised polyline as the sensor/solver interface** | lane threat struct | the right separation of concerns, and what makes the solver pure |
| **arrival-time march + a trust prefix that caps durability** | temporal model | the highest-value algorithm in the reference; prevents a blind tail from scoring as clear |
| **safety as a hard constraint, preference as a score over the safe set** | solver admission/score split | makes every later feature safe by construction |
| **the intent-preservation ladder** | PJDodge override ladder | the clearest existing answer to "how much may intent buy" |
| **"already inside the volume" escape exception** | zone/enemy swept tests | without it, being inside a hazard is unsurvivable |
| **degrade the candidate set, never the admission tests** | frame budget | the correct thing to sacrifice |
| **the worker/handoff discipline: never block the game thread** | async planner | applies directly to our bridge |
| **the authoritative-move principle: call the function the input handler calls** | movement runtime | this is why we use `RequestEscape` and the input angle, not a position write |

### Deliberately not borrowed

| thing | why not |
|---|---|
| **the traffic model** | there is none to borrow — the reference calls the game's function and so do we |
| **Chebyshev / AABB hit geometry and its player half-extent** | their collision is per-axis on a box; ours is verified `DistanceSquared < r²` on circles |
| **every time constant** | calibrated to a 5 Hz server; ours must come from [TIMING.md](TIMING.md) |
| **tile-scale distances and pads** | different units, different collision radii |
| **the memory-offset layer** | 93 KB of IL2CPP offsets for a different game; we resolve our own at startup |
| **the grid Dijkstra / pocket search, the spacetime latest-departure advisor, the nav router** | these answer "where should I be in two seconds", which is a goal-seeking problem. Our first version had no goal beyond "exist". **Updated 2026-10-15 — the condition this row set was met, and the goal arrived, but not in this form.** The candidate solver did not dead-end; it went *stale*, and the operator's reading of it is the one that stuck: a bullet's entire path was being treated as dangerous, and the dodge had no opinion about which way the character was already going. What was added is the spacetime half of this row — a plan walked against the bullets in one clock — plus a single goal point projected along the measured course. See [SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md) §11. The grid Dijkstra and the nav router are still not borrowed, and still for this reason |
| **the A\* / BFS time-sliced lattice** | same reason, and it is the most expensive and least portable part of their code. §11's fan of goal-aligned candidates is a one-step lookahead, not a lattice: it can see that a direction leads into an obstacle, and it cannot see a way round a corner. That limit is named rather than hidden |
| **the WASD-yield that stops planning while a key is held** | we are improving a tool the user runs deliberately; making the dodge stop when they touch the keyboard is a policy choice, not a correctness one, and it belongs in config |
