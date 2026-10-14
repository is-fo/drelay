# Phase 3 — scope, entry plan, and the measurement that reshapes the spike order

**Date opened:** 2026-10-14. **Branch:** `autododge-research`.
**Read with:** [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §"Phase 3 — Dodge, with the escape as
its floor" (the criteria this document executes), [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §5–§7 (the
solver this phase builds), [RISKS.md](RISKS.md) §2a (the movement question) and §3d (the
two-controller hazard), [VERIFICATION.md](VERIFICATION.md) §5 (how each is proved),
[PHASE2-SCOPE.md](PHASE2-SCOPE.md) §7 (the state Phase 2 closed at, with its two waivers), and
[MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md) §0.2 (the mechanism that makes managed code on
the game's thread legal).

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

1. every phase reports `moved on N frame(s)` with `N` comparable to the phase's length — the input
   landed and the integrator acted on it;
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
```

With the relay running for both, so the wire evidence exists:

```
java -cp drelay.jar networking.Launcher ...      # the operator's usual relay invocation
```

**What to send back:** the run directory (`%LOCALAPPDATA%\drelay\hook\<run>`), the relay session log,
and nothing else. The analysis is:

```
python autododgeresearch\tools\steering_report.py <run-dir> [relay-session.log]
```

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
  session ends, which the switch's own help text says out loud.

---

## 6. What comes next, in order

1. **Spike 3.1's live run** (§5). This is M7 and it gates everything below: if movement cannot be
   actuated, the dodge does not exist and the honest outcome is a stop
   ([IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md), Phase 3 "If this fails").
2. **The budget, from the client's own law.** `DODGE-ALGORITHM.md` §5.2's `b = playerSpeed ×
   frameSeconds` needs `playerSpeed`, which is `PlayerFunctions.TilesPerSecond` **called** with the
   live tiers. The spike's displacement log gives the realised per-frame distance directly, which is
   the same number from the other end: **measure the law rather than resolve its constants**, because
   a resolved constant that disagrees with the live tier is exactly the bug
   [CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §5.3 warns about.
3. **The escape floor's mechanism, verified before it is needed.** `World.RequestEscape` was reported
   `MISSING` in the 2026-10-13 run and the reason is now known: **nothing called it** — the operator
   never pressed the nexus key — and its "same-sized neighbour" warnings are other 335-byte methods.
   Its replacement does not need the game to call it; it needs a callable address, and the plan
   already records the two ways to get one
   ([MANAGED-ENTRY-HANDOFF.md](MANAGED-ENTRY-HANDOFF.md) §0.2, [HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md)
   §5.2). **A `--escape-test` arm of the same live run proves the path end to end** and is worth
   doing while the operator is already in a session — the escape is the floor under everything else,
   and a floor that has never been entered is a floor nobody should stand on.
4. **The cost work Phase 2 waived**, with the solver that shares the frame: cull before sampling
   ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §8), the sampler's `p99` back under its slice, and the
   histogram that places an outlier instead of counting it.
5. **Spike 3.2 — the solver offline.** The decision is a pure function of a plain-data snapshot
   *by design*, so the invariants T5, T6 and T10 are asserted in a harness against recorded traces
   and every weight is tuned there, never in a live run.
6. **Deliverable 3.3/3.4 — the live dodge and the escape floor**, behind their own switch, dry run
   by default, with the paired trial ([VERIFICATION.md](VERIFICATION.md) §5) accounting for every
   death in the two buckets §2 makes unavoidable.

---

## 7. Exit criteria, restated against what now exists

From [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) Phase 3, with the instruments this document
has built:

| criterion | state | instrument |
|---|---|---|
| **after Spike 3.1, the movement question has an answer with evidence** | **built, not yet measured**: the actuator, the target, the switches, the logging and the block are in place and the build is green | `--move-steer sweep`, then `tools\steering_report.py` |
| the budget is measured against the client's own law rather than derived and assumed | not started; §6 item 2 | the spike's per-frame displacement |
| the dodge is *observably* dodging (decision log shows moves that avoid predicted collisions) | not started | the solver, Spike 3.2 |
| the frame budget is respected at p99 | **carried from Phase 2's waiver**; the spike's cost is the floor | `frames.csv`, the cost histogram |
| the death rate in the claim-bearing cases is measurably lower than a deliberate baseline run | not started; needs a paired trial | `TIMING.md` §8 item 4's requirement |
| the escape fires only when no admissible move avoids lethal projected damage, ack-confirmed | **the escalation target is not compiled at all today** — `escape` was `MISSING` on 2026-10-13 because nothing called it | §6 item 3, then the relay's `EscapeAck` join |
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
