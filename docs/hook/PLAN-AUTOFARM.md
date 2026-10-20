# Plan — the auto-farmer: walk to an enemy, farm a realm, follow a route, clear a dungeon

**Items 2a, 2b, 3c and 3d of the farmer work.** Written 2026-10-17. This is an implementation
plan, not a record; nothing here is built.

> **§12 IS THE RECORD AND IT SUPERSEDES PARTS OF §4, §6 AND §7.** Rows 2, 3 and 6 of §10's order of
> work are built on branch `feat/autofarm` — the farmer walks, the `--world-log` collection
> instrument exists, and a recorded route can be recorded and replayed. §12 names the seven places
> the plan's own numbers did not survive contact with the harness, all of them measured rather than
> argued, and the two bugs the world log's offline verifier found before it shipped. **Row 4, the
> operator's collection session, has now been run twice**: §12.8 is the first run — half the
> questions answered, three columns silently zero, the instrument defects behind them fixed — and
> §12.9 is the re-run, in which every column is filled, both dungeons are named *before* entry, the
> realm's entrance is pinned to one portal at (125.5, 109.5) titled with a player's name, and leaving
> a dungeon is measured to return to the lobby rather than to the realm. Rows 5, 7, 8 and 9 are still
> not built; §12.7 says what each is waiting on, and for 5, 7 and 8 the answer is no longer "a
> measurement".

Read with: [`PLAN-ENEMY-THREATS.md`](PLAN-ENEMY-THREATS.md) (item 1, and a prerequisite for the
arrival behaviour in §4), [`PLAN-HOOK-CONTROL.md`](../bridge/PLAN-HOOK-CONTROL.md) (items 3a/3b —
the reason the farmer's switches can be changed without relaunching),
[`DODGE-ALGORITHM.md`](DODGE-ALGORITHM.md), [`HANDOFF-DIRECTED-DODGE.md`](HANDOFF-DIRECTED-DODGE.md)
(the path), [`AUTOAIM.md`](AUTOAIM.md) (the target selection this builds on).

---

## 0. The shape of the answer, up front

The operator's own note on 2b — *"since you can't run the game, this step might need to be split up,
where I go and collect coordinates and other data that is needed"* — is correct, and this plan is
arranged around it. Three findings reorganise the work:

| finding | consequence |
|---|---|
| **Firing needs no work at all: the client already ships auto-fire.** `InputOptions.AutoFire` is an in-game input option, and with it on the client holds its own `Shooting` input; `Player.Shoot` then overrides the angle with `AimbotSystem.CalculateAimAngle(...)` whenever `Prefs.enable_aimbot` is set (`Player.cs:2970-2977`) — which the auto-aim module already forces on. | §3. **Nothing to build.** The attack loop is *auto-fire (a client setting) + the existing auto-aim module*, and the hook writes no firing input at all. It is recorded here because an earlier draft of this plan wrongly treated it as a feature. |
| **A tuned, hazard-aware A\* already exists and is already wired to the actuator's slot** (`PathPlanner.cs`, `DodgeAdapter`'s one-frame `DodgeCommand` slot consumed by `SteeringPilot`). | 2a and 3c are mostly *goal plumbing*, not pathfinding work. §4 and §7. |
| **Portal entry is `GmActivateObject` (id 84) and the client's own entry point is `Portal.Activate(0)`; but there is no realm/dungeon/portal *target* field anywhere in the client, and `docs/` does not mention portals at all.** | 2b and 3d are **blocked on measured data**, not on code. §5 and §8, and the collection instrument in §6. |

The bottom line: **§4 and §7 are buildable now; §5 and §8 need one instrumented session of the
operator's, and that session should be doing §6's collection list. §3 is not work at all.**

---

## 1. What already exists — the asset inventory

| need | what exists | where |
|---|---|---|
| enemy list | `World.GetEnemies()` resolved as a raw function pointer, with `Position.X/Y`, `Dead`, `Npc`, `Health`, `MaxHealth`, `Effects` on the element type | `AutoAim.cs:205-244`, used `:529-595` |
| enemy bodies for the dodge | declared, wired into rule 7, never filled | `DodgeSnapshot.cs:240-245`, `DodgeSolver.cs:2140-2159` |
| pathfinding | bounded A\* over the client's collision grid with a spacetime danger cost, 8-connected with the diagonal rule, buffers preallocated, `WallCrossings` self-check | `PathPlanner.cs` entire |
| path → a preference | `DodgeSolver` scores each admissible candidate by alignment with the planner's waypoint | `DodgeSolver.cs:2973-2982` |
| movement actuation | three writes at the movement integrator's entry: `GameInput.MoveAngle.Radians`, `.Moving`, `.MoveSpeed`, with `WorldRotation` compensation | `SteeringPilot.cs:951-1000`, `:762-818` |
| the decision → actuation slot | `DodgeCommand {None, Move, Stop}` published per frame by the adapter, consumed once by the pilot | `DodgeAdapter.cs:1149-1203`, `SteeringPilot.cs:1137-1147` |
| firing | **a client input option, not hook work**: `InputOptions.AutoFire` (`DarzasDominion.Data/InputOptions.cs:16`) holds `Shooting`; the client's own path is `GameInput.Shooting` → `Player.Attacking` → `Player.Shoot` → the aimbot angle → the weapon gates, cooldown, animation and outbound shot. The hook does not write it — see §3 | `World.cs:12805`, `:12828`; `Player.cs:2938-2977` |
| the collision grid | the only `byte[,]` on `World`, resolved structurally, windowed 128×128 around the player | `DodgeAdapter.cs:686-714`, `:1007-1045` |
| whole-map grid | **not built** — specified | `HANDOFF-DIRECTED-DODGE.md` §5.2 |
| player position, speed, course | measured every frame | `DodgeAdapter.cs:764-767`, `:1061-1120` |
| map identity | `World.MapInfo` object, `World.Name`, `World.AssetName`, `World.SafeWorld`, `World.IsSoloRun` | `World.cs:4486`, `:13778-13817` |
| portals | `World.Activatables` (any `StaticObject`-derived `ActivatableObject`), `Portal` with `Called`, `_dungeonLevel`, `locked`; entry via `Portal.Activate(0)` → `GmActivateObject` | `Portal.cs:22-152`, `ObjectActivator.cs:560`, `World.cs:14605-14608` |

Two absences to name now, because both are load-bearing and neither is a coding problem:

- **No realm id, dungeon id or portal destination exists client-side.** `PortalData` carries only
  `Locked`, `KeyType`, `LockedMessage` (`hook/work/decomp/DarzaCore.Data.Map.Activatable/PortalData.cs:6-12`).
  Where a portal leads is decided by the server; the client learns it only from the next
  `GmReconnect` and the next `GmMapInfo`. A dungeon *whitelist* therefore has to be keyed on what the
  client can see after the fact (`MapInfo.Name`/`AssetName`) or on the portal's free-text `Title`/`Tag`
  stats — and which of those is stable is a measurement (§6).
- **The `WeightProjectedDanger` precedent applies to every new weight here.** This project ships ideas
  at weight 0 until the offline harness says otherwise (`WeightProjectedDanger`,
  `SPIKE3.2-OFFLINE-SOLVER.md` §15.9.2). Every number this plan adds is a starting value for the
  harness, never a live tuning target.

---

## 2. Where the farmer sits in the architecture

The farmer is **not a second controller**. The project has already paid for that lesson: two
controllers arguing in one frame is a *verified* hazard, not a hypothetical one
(`RISKS.md` §3d, `IMPLEMENTATION-PLAN.md` Phase 3 precedence).

So the farmer is **a new source of the goal**, and nothing else:

```
per frame (frame tick, the existing sensor path)
    EnemyReader.Fill(...)                    <- item 1
    Farmer.ChooseTarget(...)                 <- NEW: nearest enemy, or the route's next waypoint
        -> snapshot.Goal { HasPoint, X, Y }   <- NEW: a POINT, where today there is only a ray
    DodgeAdapter.OnFrame(...) -> DodgeSolver.Decide(snapshot)
        the same admission rules, the same path planner, the same score
        the same DodgeCommand slot -> SteeringPilot -> GameInput

and nothing else: the aim is the auto-aim module's, and the fire is the client's own
auto-fire option (§3).  The farmer moves; it does not shoot.
```

Consequences that follow from that placement, and they are the whole of the safety argument:

1. **The farmer cannot walk into a bullet**, because the candidate set it steers through is the
   admissible set, unchanged. §5.3's doctrine holds: a preference can only rank options that passed
   the boundary.
2. **The farmer cannot disable the dodge.** It changes a goal; it does not touch admission, the plan
   walk, the dodge's hysteresis or the escape floor.
3. **The farmer inherits the dodge's degradation behaviour**: no grid, no threats, degraded sensor —
   all the existing fallbacks apply, and every one of them ends in "the goal term decides as before"
   rather than "no move".
4. **It needs a `StandStops`-aware policy of its own** (§4.3): the farmer wants to stop at the enemy,
   and the dodge's `stand` verdict is the mechanism, so the two must agree rather than fight.

---

## 3. Firing: already a client setting — no work here

**There is nothing for the hook to build, and the earlier draft of this plan was wrong to call it a
missing actuator.** The client ships **auto-fire as an input option** (`InputOptions.AutoFire`,
`DarzasDominion.Data/InputOptions.cs:16`), toggled from the game's own input settings, and with it on
`DesktopInput` holds `Shooting` true every frame. The rest is already in place:

```
World.hehe_EB2C_E5A5_EE82_E9C5_EF68(logicDt, frameDt)     // the movement integrator
  :12805   Player.Attacking = GameInput.Shooting && !Player.KnockedUp
  :12813   if (Player.Attacking && !Player.SpiritMode)
  :12828       Player.Shoot(GameInput.ShootAngle.AddRadians(-WorldRotation).Radians, ClientTime)

Player.Shoot (Player.cs:2938)
  :2970   if (Prefs.enable_aimbot)                     // AutoAim already forces this on
  :2975       angle = AimbotSystem.CalculateAimAngle(this, GetCurrentTarget(), equipmentData)
```

So the complete attack loop is **auto-fire (a client setting) + the auto-aim module (already built)**:
auto-aim chooses the target, the client's own `Player.Shoot` overrides the passing angle with
`CalculateAimAngle` — the client's own leading, wand and curve math — and the weapon gates, cooldown,
animation and outbound shot are all the client's. The hook writes nothing.

Three consequences for the rest of this plan:

1. **The hook must not write firing input.** Adding a `Shooting` write would duplicate a client setting
   that already does the job and would take a switch away from the player for no capability. The hook's
   only contribution is the aim, and that is `AUTOAIM.md`'s module, unchanged.
2. **Auto-fire is an operator-facing prerequisite**, on the same footing as `--autoaim`: the farmer
   README and the run checklist say *turn auto-fire on* (`PLAN-HOOK-CONTROL.md` can surface the state
   as a diagnostic, but it is not a toggle the hook owns). A farmer that walks to a mob and does not
   shoot is now a one-line diagnosis rather than a code hunt.
3. **Laser and sweep weapons still take their branch before the aimbot override** (`Player.cs:2960`,
   `:2965`), so for those the shot goes along the mouse angle and auto-aim does not steer it. That is a
   property of the client and not something this plan changes; **ship projectile weapons first and name
   the laser/sweep case as a limitation.**

Nothing else in this document depends on this section; it is here so that the next reader does not
re-derive it.

---

## 4. 2a — walk to the closest enemy

### 4.1 Design: a point goal, and the smallest possible change

Today the solver's goal is a **ray** (`GoalSource.PlayerIntent` / `Course` / `Intent`,
`DodgeSolver.cs:843-863`) and `PathPlanner.BeginFrame` derives its target cell from that ray
(`PathPlanner.cs:724-746`). For the farmer the goal is a **point** — the enemy.

The smallest change that produces the walk:

1. **`DodgeSnapshot` gains a goal point**: `bool GoalHasPoint; float GoalX, GoalY;` — a *point*, not a
   second ray, because "walk to that enemy" and "keep walking the way you are facing" are different
   questions and the ray form cannot express arrival.
2. **`GoalSource.GameplayTarget = 3`** — a new member, so the report can tell "the goal is the
   farmer's target" from "the goal is the player's hands" (the same instrument argument
   `GoalSource` was created for, `DodgeSolver.cs:185-215`).
3. **`DodgeSolver` resolves the point goal ahead of the existing ladder** when `config.FarmMode` is
   set and the snapshot carries a point. The *rest of the frame is untouched*: the path planner
   still runs once per frame, the candidate ring is unchanged, admission is unchanged.
4. **`PathPlanner.BeginFrame`**: when a point goal is present and the point is inside the grid
   window, use the point's cell as the target (clamped to walkable, and refused with `NoTarget` if
   it is a wall — the existing honest degradation). When it is outside the window, **keep today's
   behaviour**: the target is the furthest cell along the direction to it. That is a two-line change
   and it is what makes the first version work for an enemy 60 units away (inside the 128×128 window)
   *and* for one across the map (outside it).
5. **Arrival is free once item 1 lands.** `PLAN-ENEMY-THREATS.md` fills `DodgeSnapshot.Bodies` and
   rule 7 refuses candidates *into* an enemy body. So the character walks up to the mob and stops at
   its edge, because the only admissible candidates are outside the disc — **the arrival behaviour
   falls out of the safety rule rather than needing one of its own.** This is the ordering argument
   for doing item 1 first.

### 4.2 Which enemy to walk to

Reuse `AutoAim`'s selection rule and, crucially, its **scope**: the same `World.GetEnemies()`, the
same `Dead`/`Npc` filters, the same weapon range. But **do not reach into `AutoAim`'s state** — a
second consumer of that module would make the auto-aim mode a switch that changes the farmer. Factor
the enumeration into the `EnemyReader` of `PLAN-ENEMY-THREATS.md` §4.1 and have both modules read it,
keeping each module's own *filter* (auto-aim excludes what it cannot damage; the farmer excludes what
it cannot reach or does not want to fight):

```
target = the enemy minimising (distance to player)
         subject to: not Dead, not Npc,
                     distance <= FarmEngageRadius          (default: the weapon range,
                                                            from the same client call AutoAim uses)
         with a stickiness rule: keep the current target while it is alive and in range,
         and switch only when a different enemy is closer by FarmSwitchMargin (decided below)
```

**The stickiness rule is not a nicety.** Tab-target thrash between two equidistant mobs is the
classic farmer defect: the character oscillates between two goals and reaches neither. The rule is
the same shape as the solver's existing heading hysteresis (`Hysteresis.Keep`/`Take`,
`DODGE-ALGORITHM.md` §5.7) and belongs in the same family — a hold with a margin, plus a *forced*
release when the held target dies or leaves range, and a counter for both.

### 4.3 Making the farmer's goal beat `stand`

The farmer's most common state is "no bullets in the air", which is exactly the frame the dodge
answers `StandSafe` on — and with `StandStops` true that **stops the character**
(`HANDOFF-DIRECTED-DODGE.md` §3.1, measured at 18–31 % of walking frames). A farmer that stands still
whenever nothing is shooting at it farms nothing.

The policy, and it must not weaken the existing fix:

- in farm mode, `stand` is admitted only if the character is **already within weapon range of its
  target**; otherwise the durability test keeps its full force and the goal term decides, which is
  what makes the character close the distance;
- the `StandSafe`-vs-walking-player work of `SPIKE3.2-OFFLINE-SOLVER.md` §15.14 is **not** touched:
  this is a farm-mode condition on the *same* test, reported in its own counter
  (`stand held for range`), so a regression in the operator's original fix is visible as a nonzero
  count on a non-farm run — i.e. never.

### 4.4 Tests

| # | check | how |
|---|---|---|
| W1 | the goal comes from the enemy | `--decisions` gains `goalsrc=farm` and the target's id/distance; the offline harness's `GoalSource` split must show it |
| W2 | the path avoids scenery on the way | a `farmer` synthetic scenario: player at A, enemy at B, a wall between; assert the path length exceeds the straight line and `WallCrossings == 0` |
| W3 | arrival is stable | a scenario with the enemy at 3 units: assert the character does not oscillate (heading-change rate below the existing stutter threshold) and that rule 7 is what stops it, not a `NoAdmissible` frame |
| W4 | no target → no change | with no enemies, the decision is byte-identical to the non-farm run (T9's doctrine) |
| W5 | the dodge still wins | a scenario with an enemy to walk to *and* a bullet arriving: the emitted move avoids the bullet even though it is not the one the goal prefers — the ordering proof that the farmer is a preference |
| W6 | the safety invariants hold | T5/T6/T10 across all five recordings and all scenarios, with farm mode on and off |

---

## 5. 2b — the auto-farmer, and the lobby

### 5.1 The state machine

The farmer is a small, explicit state machine with an operator-readable current state in the
dashboard and the log. Every transition is a measured fact, not an inference:

```
IDLE            -> nothing enabled
IN_LOBBY        World.SafeWorld && no target available        (the Nexus/"The Tavern")
WALK_TO_PORTAL  a portal object is chosen and being approached
ENTER_PORTAL    Portal.Activate(0) issued; waiting for the world to change
IN_REALM        world changed, not a whitelisted dungeon      (farming)
IN_DUNGEON      world changed and the map matched the whitelist
FIGHT           a target is selected; approach it (auto-fire and auto-aim do the shooting)
TRAVEL          no target in radius; follow the route / the goal
DEAD/ESCAPED    PlayerDead, or an escape was taken -> back to IN_LOBBY
```

**World-change detection is the pivot of the whole machine**, and it must be an identity, not a
guess. `HANDOFF-DIRECTED-DODGE.md` §5.2 names the candidates (the `byte[,]` reference changing, the
`MapInfo` object reference changing, `Width`/`Height` changing, `Tilemap`'s reference) and says
explicitly: *"This has to be measured, not assumed."* Same rule here. The plan's recommendation:
**`MapInfo` object-reference change is the primary signal, `MapInfo.Name` change is the
confirmation, and `SafeWorld` is the lobby bit** — three independent facts, each logged, so a
transition that fires on the wrong one is visible rather than mysterious.

`World.SafeWorld` is the client's own lobby test (`World.cs:13797`,
`SafeWorld = Utils.SafeWorld(Name)`), so the farmer does not need to know the map-name literals to
know it is in a safe area. That is the one piece of lobby detection that needs no new data.

### 5.2 Lobby navigation

The operator's description: *"on escape or start, the player is put into the lobby, if they walk down
a bit they see (usually two) portals they can enter."* So:

1. **Find the portals.** `World.Activatables` holds every `StaticObject`-derived `ActivatableObject`
   (`World.cs:14605-14608`), and a portal is a `Portal`. Resolve the list the same way the sensor
   resolves its own containers (`SensorRead`'s `IDictionary` / `IList` walks are the working
   precedent, `SensorRead.cs:327-349`, `:387-414`), filter by the portal type, and read each one's
   `GameId`, `Position` and the three portal stats that matter (`Title` 105, `Tag` 25, `Locked` 51 —
   `Portal.cs:203-275`).
2. **Choose one.** With no target identifier available (§1), the choice is by *position* and by
   *rank*, not by destination: the closest portal, or the operator's own rule (e.g. "the second
   portal from the left"), or a title/tag substring the operator supplies. Every choice is logged
   with the portal's title so a session's log says which one was used.
3. **Walk to it** — the §4 walk, with the portal's position as the goal point and the *same* path
   planner, so the walk down the lobby and around its scenery needs no new code.
4. **Enter it.** Three options, in order of preference:
   - **(preferred) call the client's own entry point**: resolve `Portal.Activate(int)` and invoke it
     on the chosen object with `0`, from the frame tick. This is the `World.RequestEscape()`
     doctrine — call the function the input handler calls, not a reimplementation of its gate
     (`DODGE-ALGORITHM.md` §7.4) — and because the object is a live game object, the invocation
     route is the one `AutoAim`/`EscapeTest` already use for managed calls
     (`AutoAim.cs:310-315`, `EscapeTest.cs:162`). It runs the client's own locked/cooldown gates.
   - **fallback: write `GameInput.Activate`** and let `ObjectActivator`'s portal branch run
     (`ObjectActivator.cs:560`, `:576-602`). Cheaper to write but **the frame ordering is
     unverified** — `DesktopInput` sets the field from a key each frame (`DesktopInput.cs:650`) and
     `ObjectActivator` clears it after use, so *when* the write must happen is a measurement, not an
     assumption. Do not start here.
   - **fallback for a bridged setup: inject `GmActivateObject` (id 84) from the relay.** The id, the
     field order (`ObjectId` varint, `Value` string32, `Position` 2×f32, `Time` int32 — the **Write**
     order, which is the authoritative one; `Read` disagrees) and the state gate
     (`Session.injectionReady()`) all exist, and the relay is already the only writer of the socket.
     What the relay needs is the portal's `GameId`, which is a value the bridge can carry
     (`PLAN-HOOK-CONTROL.md`). Recorded as the option that removes a second caller of a client
     method; **not** the first choice, because the in-process call is strictly better informed.

   **`EnterPortal` (id 73) must not be sent.** It is a dead enum slot with no implementing class
   (`proxy/work/report-loot.md:368-375`) — a finding that exists in no `docs/` file and is exactly
   the kind of thing a plan has to say out loud.

5. **Confirm.** The world change is the ack. A timeout with no world change means the portal was
   locked, the walk failed, or the click did not land; the farmer returns to `IN_LOBBY`, logs the
   reason, and retries with the next portal, with a bounded retry count.

### 5.3 Farming a realm

With §4 in place and auto-fire on, farming is:

```
repeat:
    target = nearest enemy within FarmEngageRadius        (§4.2, sticky)
    if target: walk at it while the client's auto-fire and the auto-aim module do the shooting
    else:      travel — the operator's start position first, then the route (§7) or a sweep
    bound every loop with: a per-loop time budget, a maximum death/nexus count,
                           and a check that `PlayerDead`/`SafeWorld` has not changed the state
```

The farmer's whole job is *where to walk*. It never fires, never aims and never touches a weapon —
§3 is the reason, and it is also why the loop above has no attack step in it.

The operator's own observation — *"the bottom part of the map is probably the best place to start
farming"* — is a **route or a target point**, not a policy: after entering a realm, the first travel
goal is a coordinate at the bottom of the map (§6 collects it), and from there §7's route takes over.
Making it a coordinate rather than a "walk south" heuristic is what lets the same code serve 3c
without a second notion of "where to go".

**Health, death and escape are deliberately not the farmer's business.** The dodge's escape floor is
gate 6 of the dodge's own decision and stays authoritative (`DODGE-ALGORITHM.md` §7); a farmer that
added its own nexus threshold would be the second controller this plan exists to avoid. What the
farmer *must* do is **observe** the death/nexus and return the machine to `IN_LOBBY`, which is the
one place it needs to be robust: a farmer that does not notice a death farms a corpse.

### 5.4 What 2b cannot be until §6's data exists

- which portal to pick (no destination identifier);
- the lobby's walk coordinates and the map's bottom region;
- whether a realm contains portals (needed for 3d's entry from a realm);
- whether `MapInfo.Name` is stable enough to key a whitelist on.

All four are data, not code.

---

## 6. The collection session — what the operator can gather, and the instrument that gathers it

This is the deliverable the operator offered, and it should be **mechanical**: the hook logs
everything into one file and the operator only has to walk around.

### 6.1 The instrument: `--world-log`

A new diagnostic mode, off by default, that writes `worlds.jsonl` into the run directory. It is
*evidence*, on the evidence path, never on the decision path — the same discipline as `TraceRecorder`
(which is the shape to copy: `TraceRecorder.cs`, `Resolve` + `OnFrame`, its own switch, no cost when
off).

One line per **world change** and one line per N frames, carrying:

```
frame, World.ClientTime,
map:      MapInfo.Name, AssetName, Width, Height, PlayerId, Difficulty, IsSoloRun,
          OpenedByKey, CanTeleport, Levels,
world:    SafeWorld, VaultWorld, Space, PlayerDead, worldEntryCount,
player:   x, y, Radius, Health, MaxHealth, SpeedPerSecond,
portals:  [ { gameId, x, y, title(105), tag(25), locked(51), called(269) } ],   // Activatables
enemies:  count, and the 8 nearest [ { gameId, x, y, health, maxHealth, npc } ],
grid:     whether the collision byte[,] resolved, its GetLength(0), GetLength(1)
```

and, on a world change only, a `transition` line naming **which signal fired** (MapInfo reference,
name change, SafeWorld change, grid reference change) — the §5.1 measurement.

Note the two things this instrument deliberately *does not* read: it does not need the full enemy
list (the 8 nearest is enough to name a boss later) and it does not decode the map. **The map data is
the collision grid the hook already resolves**; if the whole map is needed, that is §7's full-grid
mirror and it should be built then, not now.

**The operator's surface is one flag**, because the writer is called from the managed frame
replacement's sensor path and a switch that does not also turn those on writes a header and no
frames:

```powershell
.\\hook\out\Drelay.Launcher.exe --world-log
```

`--world-log-every <frames>` moves the cadence from its 30-frame default and implies `--world-log`
(§12.5 records the launcher side).

### 6.2 The operator's checklist

Each line is a thing to walk to and note; the hook's log is the data and the operator's notes are the
labels. **The labels are the part that cannot be automated**, so they matter more than the
coordinates.

1. **Lobby (Nexus / "The Tavern").** Stand still; then walk to each portal and stand next to it.
   Note: how many portals, and which one the operator would pick for a realm (left/right, near/far),
   and what each portal's on-screen label says.
2. **The lobby walk.** Walk the intended route from spawn to the chosen portal, at normal pace, once.
3. **Enter a realm.** Note which portal was used.
4. **In the realm.** Walk to the bottom of the map along the intended farming route, and note: where
   the enemies are densest, whether there are portals in the realm, whether the realm has a
   discernible bottom edge, and where the character spawns on entry.
5. **Farm for a few minutes.** Move between packs; the goal is to log enemy positions and confirm the
   course of the intended route.
6. **Leave / die / nexus.** Note how the character got back to the lobby (nexus key, death, portal,
   or a timeout) — the return path is as important as the outbound one.
7. **A dungeon, if the operator wants 3d started.** Enter one specific, easy, whitelistable dungeon;
   walk to the boss; kill it; note **how the exit works** (a portal that appears, a walk back, a
   `GmReconnect` back to the lobby) and what the on-screen name of the dungeon is. Repeat for a
   second dungeon so the whitelist has two entries rather than one.

What the result buys, concretely: the lobby route for §5.2, the realm farming route for §7, the
dungeon names and exit mechanism for §8, and the map-identity measurement §5.1 needs to be safe.

---

## 7. 3c — a recorded route, replayed with deviation and obstacle avoidance

The operator's specification, restated: *record a path, convert it to a coordinate list that can be
repeated, capture the general shape rather than the exact path, add deviation so it looks natural,
and pathfind around anything blocking it.*

Every one of those clauses has an existing mechanism, which is why this item is mostly assembly:

### 7.1 Record

A `RouteRecorder` alongside `TraceRecorder`, switch `DRELAY_ROUTE_RECORD=<name>`:

- samples the player's position — which is **already measured every frame** for the course
  (`DodgeAdapter.UpdateSpeed`, `:1061-1120`) — at a fixed **time** step (default 10 Hz) *and* with a
  **minimum distance** filter (default 1.0 unit). The distance filter is what keeps a stationary
  player from filling the file;
- writes `%LOCALAPPDATA%\drelay\hook\routes\<name>.json`: `{ name, map: {name, assetName, width,
  height}, recordedFrom, points: [ {x, y, ms} ] }`. The map block is the route's **validity
  condition**: a route recorded in one realm is not replayed in another, and replaying it into a
  different map is a bug, not a fallback. If the map block does not match, the route is refused by
  name — the same "refuse and name it" discipline as the fingerprint gate.

### 7.2 Convert to the general shape

Two passes, both standard and both cheap, and both performed **at record time** so the file is the
route:

1. **Douglas–Peucker simplification**, tolerance default 1.5 units — this is literally "the general
   shape, not the exact path". A hand-drawn curve through an open room becomes three points.
2. **Resample to a minimum waypoint spacing** (default 3 units) so the follower has a fixed step to
   count on, and **snap each waypoint to the nearest walkable cell** using the client's own grid. A
   waypoint that lands in a wall (because the recorder sampled mid-corner) is pulled out along the
   direction of travel rather than left to trap the follower.

### 7.3 Replay with deviation

```
RouteFollower, per frame:
    if reached(waypoint[i], RadiusReached = 1.0):
        i = (i + 1) % count
    goal = waypoint[i] + deviation(i, lap)
    publish goal point                       (§4's snapshot goal)
```

`deviation` is what makes it natural, and its properties are the design:

- **bounded** — magnitude ≤ `RouteDeviationUnits` (default 1.0 unit, ≤ 1/3 of the resample spacing,
  so the deviation can never make the follower skip a waypoint);
- **smooth in time**, not per frame — a slow noise function of `(i, lap, clock)` (a low-frequency sum
  of two sines with a per-lap random phase is enough and allocates nothing), so the character drifts
  rather than jitters. Per-frame random offsets would *add* stutter to a follower whose whole point
  is to look unhurried, and the project has already measured what angle jitter costs
  (`SPIKE3.2-OFFLINE-SOLVER.md` §13.2);
- **re-drawn per lap** with a lap seed, so the second lap is not the first;
- **snapped to walkable**, same as the waypoints, so a deviation cannot push the goal into a wall.

### 7.4 Obstacle avoidance is not new work

The follower's goal is a **point**, so it goes through §4.1's path planner: A\* on the client's own
collision grid, 8-connected with the diagonal rule, danger-aware, with `WallCrossings` asserted 0.
A building, a wall or a piece of terrain between the player and the next waypoint is a path around it
— that is the existing `corridor` scenario's job and it is already green.

Two honest limits, named rather than hidden:

- the planner searches the **128×128 window** centred on the player (`GridRadius = 64`,
  `DodgeAdapter.cs:189`). A waypoint further than ~60 units away is outside it, and the current
  behaviour is the "furthest cell along the direction to the goal" fallback — which for a route is
  *correct*, because the next waypoint is by construction ~3 units away. The limit only bites if a
  route is recorded with enormous spacing, which the resampler prevents;
- a route is a *preference path*, so it is followed through the same admissible candidate set as
  everything else. **A route that walks through a bullet is not followed into the bullet** — the
  dodge's admission rules still refuse the step, the character deviates around the threat, and
  returns to the route. That is the desired behaviour and it is also why the route must never be
  allowed to become an admission filter.

### 7.5 Instruments and tests

| # | check | how |
|---|---|---|
| R1 | the recorder writes a route whose shape is the walk | count points before/after simplification on a synthetic walk; assert the file's point count is below the raw sample count and the path's Hausdorff distance to the raw walk is within the tolerance |
| R2 | a route replayed in a different map is refused by name | a unit test on the validity check |
| R3 | the follower reaches the end | a synthetic map: waypoints across a room with one wall; assert the follower's goal visits every waypoint in order and the path never crosses the wall |
| R4 | deviation is bounded and smooth | assert `abs(deviation) <= RouteDeviationUnits` for every (i, lap) over 10 000 samples, and that the second difference stays under a threshold (smoothness is a measurable property, not an impression) |
| R5 | a real recorded route is reproducible | `--decisions` on a route replay, twice with the same lap seed → identical goals; different seeds → different but bounded goals |

---

## 8. 3d — the dungeon auto-solver, and its whitelist

The operator's question: *"can the dungeon map be parsed and the auto-farmer pathfinds to boss, kills
it and then exits the dungeon? this should have a whitelist on what dungeons to enter."*

The honest answer, in four parts, in the order they must be built:

### 8.1 The whole-map grid (prerequisite, and it is already specified)

`HANDOFF-DIRECTED-DODGE.md` §5.2 is the specification and it is unchanged by this plan: the full
`World.<byte[,]>` is `new byte[MapInfo.Width, MapInfo.Height]` (`World.cs:14217`), it is the same
array rule 2 already tests against, and it must be **mirrored once per map** into a managed array
(never read per frame), with:

- a **map identity** to invalidate it — §5.1's measurement, and the same three candidate signals;
- a **size guard** — `MapInfo.Width`/`Height` come off the wire as varints, so a corrupt or hostile
  value must degrade to "no grid" rather than allocate a gigabyte. This is not hypothetical: it is
  the same class of defect as §3.9/§3.11 of the spike, where an identity taken for granted cost a
  session;
- the snapshot carries a **pointer**, not a copy (the existing no-allocation-on-the-frame-path rule).

This is also the item that makes the *realm* walk work over long distances, so it is worth building
before 3c's route is used at scale.

### 8.2 Dungeon identity and the whitelist

There is no dungeon id (§1). So the whitelist has to be keyed on what the client can see, and the
plan is explicit about which and why:

| key | available | verdict |
|---|---|---|
| `MapInfo.Name` / `AssetName` | yes, on the `MapInfo` of the dungeon itself (`World.cs:13805`, `:13813`) | **primary.** It identifies the dungeon *after* entry, which is enough to decide whether to *stay* and to key per-dungeon behaviour. |
| portal `Title` (stat 105) / `Tag` (25) | yes, *before* entry (`Portal.cs:203-275`) | **secondary, and possibly the entry gate.** If §6 shows the title carries a stable dungeon name, the whitelist can gate entry without entering. If it does not, entry gating is impossible and the farmer must enter and leave — which must then be cheap and safe. |
| `MapInfo.Difficulty` | yes (a byte) | difficulty, not identity. Useful as a *condition* on a whitelisted name. |
| `GmReconnect` host:port | on the wire, via the relay | correlates the transition but names no dungeon. |

So: **the whitelist is a list of map names** (from §6), with a per-entry `Difficulty` bound and a
per-entry note (e.g. "has an exit portal", "boss is X"). An unlisted dungeon is never entered, and a
dungeon entered by mistake is left at once — the whitelist is checked on world entry in both
directions.

### 8.3 Clear-and-advance, which is 90 % of the value and needs no boss knowledge

"Find the boss and kill it" needs a boss identifier, and there is none: `Enemy` has no boss flag,
`GmSetBossHealthBar` (id 209) is a **UI** packet (`World.cs:22153-22211`), and `EnemyAnnounce` (183)
has no implementing class. The one client-visible boss signal that might be usable is the boss health
bar packet, and it is **unverified as a hook-observable event** — the hook does not currently detach
the packet layer at all.

So the first version does not look for a boss. It does the thing that is robust without one:

```
IN_DUNGEON:
    repeat:
        if an enemy is within FarmEngageRadius: fight it (the §5.3 loop)
        else: advance -- pathfind toward the next unexplored region:
                  in order: (1) the recorded dungeon route, if one exists (3c),
                            (2) the furthest reachable cell from the entrance,
                            (3) a frontier sweep of the mirrored grid
        stop when: the boss-bar signal is observed (if it turns out to be observable),
                   or every reachable cell has been visited,
                   or the per-dungeon time budget expires
    then: exit (§8.4)
```

Point (2) is deliberate: in most dungeons of this shape the boss room is the far side of the map, and
"walk to the furthest reachable cell, killing everything on the way" reaches it without a boss
identifier, without a boss table, and without any new data beyond the full grid. It is also
**strictly honest about its own failure**: if the boss is not at the far end, the run times out and
says so.

### 8.4 Exiting

Three mechanisms exist and §6 identifies which one a given dungeon uses:

1. **an exit portal** — a `Portal` in `World.Activatables` after the boss dies, entered by §5.2's
   preferred path (call `Portal.Activate(0)`);
2. **a walk to a fixed exit** — the route recorder again, with a short second route;
3. **`World.RequestEscape()`** — the client's own nexus, which in a dungeon is the "leave" action, and
   which is already resolved and already proven callable (`EscapeTest.cs:162`, `:256-293`). This is
   the universal fallback and it needs no per-dungeon knowledge at all, which is exactly why the
   whitelist can start small: **a dungeon the plan cannot finish can still be left.**

### 8.5 Ordering and risk

| step | needs | risk |
|---|---|---|
| 8.1 full-grid mirror | nothing but the identity measurement | low — specified, and the identity is already an open measurement |
| 8.2 whitelist by name | §6's dungeon names | low, but the *entry* gate depends on the title measurement |
| 8.3 clear-and-advance | 8.1 + §4, with auto-fire on | medium — the frontier sweep is the only genuinely new algorithm, and it is a BFS on a byte grid |
| 8.4 exit | §5.2's portal path, or `RequestEscape` | low |
| 8.6 boss-specific behaviour | **does not exist to build** | needs a boss identifier that is not in the client today; the boss health-bar packet is the one lead, and reading it needs a packet-layer observation the hook does not have |

**Recommendation: ship 8.1–8.4 and treat the boss as "whatever is at the far end".** That is the
version the evidence supports. A true boss-hunting version is a *new research item* (how does a
process-side observer see `GmSetBossHealthBar`?), not a step in this plan.

---

## 9. Invariants, and the ones most likely to break

The farmer adds preferences, not rules, so the safety invariants should be untouched. The table names
what to assert, and — more usefully — *which change would break which*.

| invariant | asserted by | what would break it |
|---|---|---|
| **T5** `stand` is never rejected while admitted and durably safe | harness, all recordings + scenarios | §4.3's range condition. Assert T5 with farm mode **off** is byte-identical to today, and that farm mode **on** reports its own `stand held for range` counter. |
| **T6** no emitted move is inside a disc at arrival | harness, on what was *emitted* | any change that lets the goal bypass admission. The farmer must never be in `Admit`. |
| **T10** hysteresis cannot hold an unsafe heading | harness | the target-stickiness rule (§4.2) if it is implemented as a heading hold rather than a *goal* hold. Keep it a goal hold. |
| **T9 / mode-1 equivalence** | paired runs | any module that acts without its own switch. **The farmer writes no input except the movement slot the dodge already owns**, which is what keeps this invariant cheap to hold. |
| **frame budget** 300 µs shared | `decide()` p99 + stage clocks | the full-grid mirror if it is read per frame; the frontier sweep if it is run every frame instead of on demand. |
| **`WallCrossings == 0`** | `PathStats`, every search | the route's deviation or waypoint snapping silently moving a goal into a wall. Snap, and assert. |
| **the bridge is not load-bearing** | kill the relay mid-session | any farmer setting that only exists across the bridge. Every farmer mode must have a launch-time default (`PLAN-HOOK-CONTROL.md` §6). |

---

## 10. Order of work

Ordered so that each row is independently testable and only the last two need the game.

| # | item | needs the game? | depends on |
|---|---|---|---|
| 1 | item 1 (`PLAN-ENEMY-THREATS.md`): fill `Bodies`, fold the emitter into the danger field | no | — |
| 2 | §4 2a: the point goal, the target selection with stickiness, the `stand` range condition, W1–W6 offline | no | 1 |
| 3 | §6 the `--world-log` instrument | no to build, **yes to use** | 1 (it reuses `EnemyReader` for the enemy lines; the portal and map lines are independent) |
| 4 | **the operator's collection session** (§6.2) | **yes** | 3 |
| 5 | §5 2b: the state machine, the portal path, lobby navigation, using §6's data | yes, to confirm | 2, 4 |
| 6 | §7 3c: the recorder, the simplifier, the follower, the deviation | no to build | 2 |
| 7 | §8.1 the full-grid mirror | identity measurement from §6 | 4 |
| 8 | §8.2–8.4 the dungeon whitelist, clear-and-advance, exit | yes | 5, 6, 7 |
| 9 | §8.6 boss-specific behaviour | **blocked on research, not on this plan** | — |
| — | §3 firing | **not a work item** — auto-fire is an in-game input option | — |

Rows 1–2 are the first deliverable and they are entirely offline: **a character that walks at the
nearest mob and stops at its edge, while the client's own auto-fire and the existing auto-aim module do
the shooting — and it still dodges.** Nothing in it needs the game, and it is the thing to build, run
in the harness, and then play once, because it is also the thing that shows whether the farmer's
premise — walk, let the client shoot, dodge — actually works at all.

### 10.1 What may run in parallel, and what may not

These plans can be worked in separate worktrees, but **not all of them at once**, and the binding
constraint is not the code. Three things decide it:

**(a) File ownership.** The plans overlap in five places, and each overlap is a place two branches
would have to agree on semantics rather than merely resolve a hunk:

| shared file | claimed by | note |
|---|---|---|
| `DodgeSolver.cs`, `PathPlanner.cs`, `DodgeSnapshot.cs`, `DodgeAdapter.cs` | item 1, 2a, 3d, 3a | the solver's goal/admission region and the planner's envelope. **Item 1 and 2a are the same region** and must be sequential — 2a is written against item 1's rule 7. |
| `StartupHook.cs` (the frame path) | 2a, 2b, 3a, `--world-log` | one-line call sites each, but four branches editing the same method. Merge order matters more than the conflicts. |
| `native/DrelayHookShared.h` + `GeneratedSharedLayout.cs` | **3a only** | generated file; a second branch touching the header would collide in regenerated code. Keep it in one worktree. |
| the launcher's `CommandLine.cs`/`Settings.cs`/`ConfigFile.cs`/`EnvironmentBlock.cs` | 3b (`--config`), the farmer's own switches | small, and the only branch that touches them is the one adding the launch surface. |
| the relay's `WebDashboard.java`/`DashboardPage.java` | 3a **and** 3b | the dashboard's hook panel. Keep 3a and 3b in one worktree, or do 3b after 3a. |

**Nothing on the hook side is edited by two of these plans at once except `StartupHook.cs`, and there
each edit is a single call site.** That is a consequence of §3 not being work: with the hook writing no
new input, `SteeringPilot.cs` is untouched by this whole plan and the farmer is confined to a new
`Farmer.cs` plus call sites.

So the safe maximum is **two concurrent worktrees**, or three if the third is confined to new files:

1. **the solver/farmer chain** — item 1 → 2a → 2b / 3c / 3d. Sequential in that order; item 1 and 2a are
   the same region and 2a is written against 1's rule 7.
2. **the control channel** — 3a → 3b. Disjoint from (1) except for one call site in `StartupHook.cs`
   and (3a's) `DodgeAdapter.cs` line. It owns the generated layout, which is why it must not share a
   worktree with a header change from elsewhere.
3. *(optional)* **new-file-only work** — `RouteRecorder`/`RouteFollower`/the simplifier and the
   deviation math (3c's §7.1–7.4), or the `--world-log` instrument, built and unit-tested against
   synthetic data. Their *integration* needs 2a, so they merge last, but the files are new and conflict
   with nothing.

**(b) Validation is serial, and it is the real limit.** Two facts make concurrency worth less than it
looks:

- the harness replays **all five recordings and all six scenarios** after every change, and two
  branches that both move `DodgeSolver`/`PathPlanner` each shift those numbers, so the merged result
  needs a full fresh replay regardless of how cleanly it merged;
- **live verification is one resource.** One game install, one pinned build, one hook in the process,
  one `Drelay.Launcher.exe`, and the relay cannot be run twice (the address claim is global —
  `netsh … store=active`). Several of these plans need a live session (2b, 3d) and the operator's
  collection session is itself a prerequisite for two of them. **Test-time parallelism is the thing to
  bound, not authoring-time.**

**(c) Two worktree gotchas that will cost an afternoon each if they are not known.** Both artefacts
the work needs are *gitignored*, so a fresh worktree has neither:

| artefact | where | workaround |
|---|---|---|
| the IL dump, `hook/il/client-5.8.2.il` (~85 MB) | ignored (`hook/.gitignore:30`, only `.gitkeep` is tracked) | `il_index.py` and `check_opcode_table.py` both hard-fail without it; both accept `--il-dump <path>`, so point them at the main checkout, or junction `hook/il` |
| the decompiled client, `hook/work/decomp` (1916 files, 69 MB) | ignored (`hook/.gitignore:36`) | every one of these plans reads it. Junction it (`New-Item -ItemType Junction`), do not copy it |
| the default run root `%LOCALAPPDATA%\drelay\hook` and `drelay.config.json` | **shared by every worktree** | give each worktree its own `--runroot` while testing, or 3a's block discovery (which picks the newest directory) will attach to the other worktree's run |

**Recommendation.** Start **two** worktrees: the solver/farmer chain and the control channel. Do the
`--world-log` instrument early inside the first one, because the operator's collection session is on the
critical path for 2b and 3d and is the only piece of this work that is not code — it can run while the
code work continues. Add a third worktree only for the new-file-only items, and only if the
rebuild-and-replay cost is acceptable.

**And there is a genuinely free head start.** With auto-fire on and `--autoaim closest`, the operator
can farm *today* — walking manually and letting the client shoot — which is worth doing before any of
this is built: it establishes whether walking-at-the-nearest-mob is even the right farming behaviour,
at zero cost and with no code.

---

## 11. What this plan does not establish

- **What has *not* been tested is a client that walks itself while firing.** Neither half is new —
  the dodge's steering is measured server-accepted with no `GmKicked` ever observed, and auto-fire is
  the operator's own setting — but the *combination* is a session nobody has run, and the run
  checklist's live check is exactly that: auto-fire on, the farmer walking, the relay watching for a
  kick. The relay is the witness, and it is a log read.
- **Auto-fire is a prerequisite the plan cannot assert.** It is a client input option (§3); if it is
  off, the farmer walks to mobs and nothing happens, which is a one-line diagnosis and not a bug to
  chase.
- **No anti-detection claim of any kind is made.** The project's stated position is unchanged: the
  traffic shape is what a player could produce with a keyboard and a mouse, the relay and the hook
  are documented, and use is at the operator's own risk (`README.md` disclaimer).
- **No realm/dungeon topology is asserted.** §1's absences are exactly that: `docs/` never mentions
  portals, no target identifier exists client-side, and §6's session is how that changes.
- **No boss-identification mechanism is specified**, because none was found. §8.6 says so and names
  the one lead.
- **Nothing here is measured yet.** Every number in this plan (`FarmEngageRadius`,
  `FarmSwitchMargin`, `RouteDeviationUnits`, the resample spacing, the simplification tolerance) is a
  starting value for the offline harness, and none of them may be tuned in a live run — the project's
  own rule, applied since Phase 3 (`IMPLEMENTATION-PLAN.md` Phase 3, "Spike 3.2 — the solver,
  offline").

---

## 12. What was actually built — rows 2 and 3, and the five places the plan was wrong

Written after the fact, on branch `feat/autofarm`. The body of this document above is left as
written: it is the specification the implementation was measured against, and this section is the
diff. Rows 5, 7, 8 and 9 of §10's table are **not built**; §12.7 says what each is waiting on, and
§12.8 is the row-4 session.

### 12.1 Row 2 (§4) is in — `b72077d`, "the farmer walks"

`Farmer.cs` (new, and since §13 a PRIVATE module outside the repository), plus `DodgeSnapshot`,
`DodgeSolver`, `PathPlanner`, `EnemyReader` and `DodgeAdapter`. `SteeringPilot.cs` is untouched,
exactly as §10.1's file-ownership table predicted, because the farm writes no input: the client's
own auto-fire and the existing auto-aim module do the shooting (§3).

The farmer is what §2 says it is — a source of the goal and nothing else. It publishes
`DodgeSnapshot.GoalHasPoint/GoalX/GoalY`, the solver resolves it into `_goalRadians` ahead of its
existing ladder and records `GoalSource.GameplayTarget`, and the planner aims at the point's own
cell. Nothing in `Admit`, the plan walk, the hysteresis or the escape floor changed.

> **A value is not where §4.1 item 2 says it is.** The plan gives the new member as
> `GoalSource.GameplayTarget = 3`; `PlayerIntent` already holds 3. The new member takes 4, because
> renumbering an enum the trace format carries would change the meaning of every recorded line.

### 12.2 The four places the plan's own numbers did not survive contact

Each was found by a harness fixture and fixed, and each is a place a reader of §4 alone would get it
wrong. All five fixtures are new: `--scenario farmer | farmerclose | farmerorbit | farmerpair |
farmerdodge`.

**(a) `FarmStopRadius` had to become a SECOND radius (§4.1 item 5's arrival claim is wrong).** The
plan uses `FarmEngageRadius` for both acquisition and arrival, and one number cannot do both: a
target is by construction inside the acquisition radius, so a single number makes `stand` admissible
the moment a target is chosen and the character never closes at all.

And the plan's arrival rule — *"the character walks up to the mob and stops at its edge, because the
only admissible candidates are outside the disc"* — does not happen. **Rule 7 REFUSES a candidate; it
does not stop the character.** `--scenario farmerorbit` is that measurement, with `farmstop=0`: no
frame settles, the character walks past the mob, and the mob leaves its acquisition radius
(`1 forced release(s) over 195 closing frame(s), 0 reversal(s)`). The drift is smooth, which is why
the first version of the check — counting heading reversals — passed on it. What the shipped rule is
instead: `stand` is declined while the target is further than `FarmStopRadius`, and admitted once it
is inside, so the character closes and then stands and shoots. `--scenario farmerclose`, at the
shipped values: **15 moving frames, then 585 frames of `StandSafe`.**

**(b) `FarmStopRadius` must also clear the enemy's emitter disc.** `PLAN-ENEMY-THREATS.md` gives
every body a padded danger radius of `enemy.Radius + player.Radius + EnemyDangerPadUnits` — **1.25**
at the shipped values — and `WeightEnemyDanger` (0.5) penalises a candidate that ends inside it. A
stop radius *inside* that disc makes the farmer fight its own preference, and the measured result is
a character that circles at the disc's edge and never settles: `--scenario farmer` at `farmstop=1`
loses the mob (`262 moving frames, 338 with no target`), while `farmstop=1.5` closes in 83 frames and
stands for 517. **The shipped 1.5 is that number plus a quarter of a unit of slack, and a deployment
with a larger pad has to raise it.**

**(c) `WeightIntent` fights the farmer, and the plan does not name it.** §4.3 names the `stand`
early-out; the scoring layer has the mirror image. `WeightIntent` (0.4) rewards motion along the
direction the operator's *hands* last asked for, and in a farm session those hands are idle — so with
the goal due north and the stale intent due east, the ring heading at **60°** beat the goal's own 90°
by 0.68 of score, and the character walked 30° off its target and missed it entirely. The intent term
is now zeroed for the frames the farmer is still closing. `IntentSafe` is suppressed on exactly those
frames too — one flag (`_farmClosing`), so `stand`, `IntentSafe` and the intent term cannot disagree
about whether the farmer is closing.

**(d) The planner's waypoint distance is wrong for a POINT goal.** `PathWaypointFraction` (0.75) is
right for a ray goal, and `HANDOFF-DIRECTED-DODGE.md` §4.3 says why — with a ray goal the detour is
near the *end* of the path. It is wrong for a point goal, where the obstruction is wherever it
happens to be: a wall in front of the character puts the detour at the START and the 75 % waypoint on
the far side of the wall, so `WeightPath` cannot see it and the character slides along the wall
(`--scenario farmer` at `farmstop=1`, before this fix). A point goal now takes its waypoint at
`PathWaypointUnits` instead.

### 12.3 Two consequences the plan's §9 invariant table does not anticipate

- **`BodyZoneNodes` (E3's counter) has to exempt the target's own emitter disc.** A route that ends
  at a mob must end inside that mob's disc; `--scenario farmer` measured a non-zero count on all 162
  frames it had a route, and a permanently red invariant is an ignored one. Every *other* body is
  still counted, including one standing next to the target, so the property the counter exists for is
  intact.
- **`DodgeSolver.Configure` now also drops the heading hold.** A world change invalidates a heading
  taken against bullets that are no longer in flight.

### 12.4 The measurements §4.4 asks for, and two the plan does not

Fourteen scenarios × farm mode on and off, all green. Every claim below is a harness assertion in
`hook/tools/SolverHarness/Program.cs`, printed on every run.

| # | check | result |
|---|---|---|
| W1 | the goal is the farmer's target and the farmer's target is the goal | PASS. The two counts are made by two different objects (the farmer's own bookkeeping and the solver's `GoalSource`), so a farmer whose output never reached the solver cannot pass it |
| W1b | the farmer never holds a target outside its acquisition radius | PASS, 0 of 600 |
| W1c | the stickiness rule holds through a cross and switches on a clear margin | PASS on `farmerpair`: **1 switch, 0 forced releases, 598 held frames of 600** |
| W2 | the route to the farmer's target goes round the wall | PASS on `farmer`; `WallCrossings == 0` is the existing global PATH check, which this scenario now feeds |
| W3 | the arrival is stable: it closes, stops, and does not oscillate | PASS on `farmerclose`: 15 moving frames, 585 settled, 0 reversals, 0 `NoAdmissible` |
| W3b | (new) with no stop radius the farmer misses, not orbits | PASS as a **characterisation** of `farmstop=0` — see §12.2(a). It is asserted on `ForcedReleases >= 1`, which is not tautological: a held target can only leave the acquisition radius if the character walked away from it |
| W4 | with the farmer off, nothing about the dodge changes | PASS, and measured **two ways**: every decision column of all nine pre-existing scenarios is byte-identical against `94f200d` with the switch off (T9), and byte-identical with the switch **on** and no targets in range (clock columns excluded, exactly as `PLAN-ENEMY-THREATS.md` §8.6 excludes them) |
| W5 | the dodge and the farmer are asked at the same time and the dodge wins | PASS on `farmerdodge`, with T6 holding every emitted move on the same frames |
| W6 | the safety invariants hold with farm mode on and off | PASS — this is T5/T6/T10 and the PATH/BODY invariants, run across the whole sweep |

**What is NOT verified, and it is the same gap item 1 has**: no live run. `World.GetEnemies()` is
exercised, `GameId` resolution is new and untested against the shipped build, and whether a farm mob
is really at `Radius 0.25` on the live client is unverified — `PLAN-ENEMY-THREATS.md` §8.7 records
`fallbackRadius=11985` on its own live run, so roughly a quarter of live bodies took the fallback.
`FarmStopRadius`'s 1.5 is derived from a pad that was itself never measured in a realm.

### 12.5 Row 3 (§6) is in — `6f1a2f6`, "the world log"

`WorldLog.cs` (new), `EnemyReader.SampleEnemies`, one call site in `StartupHook`.

> **The plan calls it `--world-log`, and that is now a launch option.** The hook's own surface is a
> `DRELAY_` environment variable and always has been (`PHASE1-HANDOFF.md` §4 trap 1), so the writer
> reads `DRELAY_WORLDLOG=1` and `DRELAY_WORLDLOG_EVERY=<frames>`; but the variable alone is not a
> usable switch, because `WorldLog.OnFrame` is called from the managed frame replacement's sensor
> path and `DRELAY_HOOK_FRAME_REPLACEMENT` is deleted from anything the launcher inherits. So
> `Drelay.Launcher.exe --world-log` sets all three names — the frame replacement, `DRELAY_FRAME_SENSOR`
> and `DRELAY_WORLDLOG` — and `--world-log-every <frames>` moves the cadence and implies the log. A
> run that got only the variable would write a header and no frames, which is exactly the empty-file
> failure `Options.Trace` records.

**The launch surface is the flag** (§6.1's one-line command), and the file lands in the run
directory the launcher names at the top of its transcript: `<run dir>\worlds.jsonl`, beside the usual
`hook.log` and `bootstrap.log`. The three environment names still work for a hand-built launch, and
the launcher's `--world-log` now drops an inherited `DRELAY_WORLDLOG`/`DRELAY_WORLDLOG_EVERY` when it
writes its own, so a stale shell variable cannot change the cadence of a run that asked for none. It
is a command-line switch and not a configuration key, for the same reason `--trace` is not one: a
double-click must not start filling the run directory with evidence nobody asked for.
`LauncherHarness --check c9` asserts that the flag reaches all three names, that it implies the frame
replacement, that the cadence implies the log, and that a run without the flag publishes neither of
the world-log names. Writing the option also closed one latent defect in the environment builder: `--autoaim` used
to publish `DRELAY_HOOK_FRAME_REPLACEMENT` a second time through the block that also writes it for
the implied frame replacement, so the child's block carried one name twice — the builder now collapses
duplicates and `--check c2` asserts one entry per name.

**And the launch surface exposed a second half of the same "half-switched-on" defect, in the writer
itself.** `EnemyReader` was resolved only by `DodgeAdapter.Resolve`, which returns at its first line
when `DRELAY_DODGE` is unset — so a collection run with the dodge turned off wrote a well-formed file
with no enemy rows, while §6.2 item 4 asks the operator where the enemies are densest. `WorldLog.Resolve`
now resolves the reader itself when it is not already available, and `WorldLogVerify` runs with no
`DRELAY_DODGE` set, so the enemy-row assertions are the proof that the instrument stands alone. The
writer also flushes per line now (`AutoFlush`): `Close` is never called, so a buffered writer would
have ended the evidence at the last buffer boundary — and the "did the file grow" check the operator
is told to make would have read "header only" for the first several seconds of a working log.

It writes `worlds.jsonl` into the run directory: a header naming every accessor this build could not
resolve, a frame line every 30 frames with the map block, the world block, the player block, the
portal list, the eight nearest enemies and the grid's dimensions, and a `transition` line on every
world change naming which of the five signals fired (§5.1's measurement). It is evidence, it is never
on the decision path, and it decodes no map — §6.1's rule.

**It is verified offline, and the verification found two real bugs before it shipped.**
`hook/tools/WorldLogVerify` compiles the *real* `WorldLog.cs` and the *real* `EnemyReader.cs` against
a fake game whose field and property names are the client's own, and parses every line the writer
emits. Both would have been invisible in the game for a whole session:

1. the enemy rows were written in `World.GetEnemies()`'s own order and not nearest first, because the
   distance sort sat inside the truncation branch — so "the 8 nearest" was "the first 8" on every
   frame with more than eight mobs;
2. the portal fields were resolved on the **element type** of `World.Activatables`, which is
   `ActivatableObject`, while `Title`, `Tag`, `Called` and `locked` are declared on `Portal`. Every
   portal row would have carried a null title and `locked: false`, with the row present and the file
   well formed — which is a silent wrong answer about the one thing §6 exists to measure.

It is now an offline check in `Test-HookOffline.ps1` (7 checks) and is built by `build.ps1`. The
resolver sits **after** `DodgeAdapter.Resolve` in `StartupHook`, because it samples the enemy list
through `EnemyReader` and that is resolved by the adapter.

**Not run in the game.** What is unverified is the live call: that `World.Shared` is non-null at the
frame tick, and that `Activatables` holds the portals at the moment the operator is in the lobby.

### 12.6 Row 6 (§7) is in — the routes

`Route.cs`, `RouteFollower.cs` and `RouteRuntime.cs` (new), plus the point-goal generalisation below
and one call site in `StartupHook`. `DRELAY_ROUTE_RECORD=<name>` records the operator's walk into
`routes/<name>.json`; `DRELAY_DODGE_ROUTE=<name>` follows it when the farmer has no target — §5.3's
`TRAVEL`. The simplifier, the resampler, the snap, the deviation, the follower and the file format are
PURE and compile into the harness, which is what makes R1–R5 checkable offline; `RouteRuntime.cs` is
the only part that touches the client and it does nothing else.

**The route is a third source of the point goal, and the point goal had to be generalised.** The
planner and the solver both gated the point on `FarmMode`, so a route would have loaded, validated,
published a goal — and been ignored by the planner, with every counter agreeing the route was
followed. The gate is now `DodgeConfig.PointGoal` (`FarmMode || RouteMode`), and one new snapshot
field, `GoalFromRoute`, tells the two apart where they differ: **a farmer stops when it is inside
`FarmStopRadius`; a route never stops**, because its follower advances to the next waypoint the
moment this one is reached. That difference is one flag, and `stand`, `IntentSafe` and the `intent`
score term are all suppressed by the same one.

THREE THINGS THE ROUTE FIXTURES FOUND, and the first two are in code that predates this item.

**(a) §7.3's own loop is inconsistent with its own numbers, and R3 measured it.** The pseudocode is
`if reached(waypoint[i], 1.0): i = i + 1`, then `goal = waypoint[i] + deviation`. The character walks
to the DEVIATED point and is therefore `|deviation|` away from the waypoint it is tested against — so
with the plan's own starting values (a 1.0-unit deviation and a 1.0-unit reach) the loop is exactly
marginal. R3's first run completed **zero laps in six thousand frames**, with the planner returning a
route on every one of them. The test's radius is now `RouteReachUnits + |deviation|`.

**(b) The waypoint hold can name the path's LAST node, which is wrong for a point goal behind an
obstruction.** The first R3 fixture had the walk go straight through where a wall is, and it stalled
for six thousand frames with the character against the wall's face at `(23.97, 32.47)`. The
measurement is `nodes 13, len 6, held 1`: a thirteen-node, six-unit detour whose held waypoint is the
path's final node — the goal cell itself, on the far side of the wall. `OnPath` accepts it (the goal
IS a node of the detour) and the hold is re-taken every dwell, so the steering command is "walk east,
into the wall". **This is the pathfinder's, not the follower's**, and it is recorded rather than fixed
here: every fixture until now had a goal reachable in a straight line, where steering at the path's
last node is correct. Row 2's `farmer` fixture does not hit it — its point goal is six units out with
a nine-unit detour, so the 2.5-unit waypoint is well inside the detour. **It is the next thing the
pathfinder's owner should look at.**

**(c) The route file is rewritten every twenty samples, because nothing closes it.** `TraceRecorder.Close`
is never called — the replacement does not know when the recording window closes, the native side owns
that — so a route written only at close is a route that never exists. Twenty samples at 10 Hz is two
seconds, and a session that ends abruptly leaves the walk up to that moment.

**R1–R5, as measured.** They run on every harness invocation and are now an offline-suite check (8
checks).

| # | check | result |
|---|---|---|
| R1 | the recorded route is shorter than the walk and within the tolerance | PASS: 900 offered samples, 600 suppressed by the time/distance filter, 300 raw → **115 route points**, one-way Hausdorff distance to the raw walk **1.156** against a 1.5 tolerance |
| R2 | a route from another map is refused by name, and the file round-trips | PASS: same map accepted, other realm refused, other size refused, asset-only match accepted, 11 points read back, a truncated file refused |
| R3 | the follower walks the route through a walled room | PASS: 13 waypoints, **0 in a wall**, 13 advances, **1 lap**, 283 planned routes, 0 corner cuts, 0 steps that ended in a wall |
| R4 | the deviation is bounded and smooth | PASS: over 10 000 samples `\|deviation\| ≤ 1.000` against a 1.0 bound, the largest second difference is **0** against the 0.334 the frequencies imply, lap 0 and lap 1 differ by 0.327, and the shipped deviation is exactly spacing/3 |
| R5 | a route replays identically, and a different lap seed stays bounded | PASS: 200 of 200 goals identical between two followers on one route; 200 of 200 different between routes `alpha` and `beta`, largest disagreement 0.706 against a bound of 2.0 |

**What R3 does NOT test, said plainly**: its simulation applies rule 2 and the swept-wall test and
NOT the candidate ring or the score, so it says "a follower plus the planner crosses the room" and not
"the dodge would have emitted this". The `corridor` scenario's existing PATH invariants are what hold
the ray-goal case.

**And no live run.** `RouteRuntime` resolves `World.Shared`, `Player.Position` and `MapInfo`; the
route file's map block is written from whatever those accessors return and a recording with no names
is refused by `MatchesMap` rather than followed. Whether the operator's walk produces a route that
replays is a session, not a build.

### 12.7 The rows that are not built, and what each is waiting on

| row (§10) | state | waiting on |
|---|---|---|
| 4 — the operator's collection session | **run twice** — 2026-10-18 (a failed instrument, §12.8) and its re-run (§12.9). Every column filled; the dungeons named; no dungeon cleared | the boss/exit pass of §6.2 item 7 |
| 5 — §5 2b, the state machine and lobby navigation | **not built** | nothing measured — §12.9 has the lobby's four portals, the realm entrance, and the "stand on its `Position`" rule |
| 6 — §7 3c, the recorded route | **built** — see §12.6 | — |
| 7 — §8.1, the full-grid mirror | **not built, and unblocked** | §12.9's map-identity measurement (name + ref + grid size, all agreeing) — and a `CanTeleport` realm where 1 219-unit jumps occur, which the mirror's invalidation has to tolerate |
| 8 — §8.2–8.4, the dungeon whitelist and clear-and-advance | **not built, and unblocked** | the key (`MapInfo.Name`) and the entry gate (the portal's `Title` == that name) are measured; the exit-without-boss path is measured. **Blocked on nothing** except a session that kills something, which is the boss research of row 9 |
| 9 — §8.6, boss behaviour | **not built** | research that is not in this plan |

The plan's §10.1 ordering has not been tested across worktrees: this branch built rows 2, 3 and 6 in
one worktree, sequentially, and touched `StartupHook.cs` only by adding three statements.

### 12.8 Row 4, run once: what the session answered, and the three columns it could not fill

The run is `%LOCALAPPDATA%\drelay\hook\20261018-011545-p16312`: 302 lines of `worlds.jsonl` (one
header, 292 frame lines, 9 transitions) covering frames 926–9656, about two and a half minutes of the
client's clock. It went Tavern → realm → Tavern → realm → Tavern → The Beyond → Tavern → realm →
Tavern, which is more world changes than §6.2 asked for and exactly what §5.1 needed.

**What it answered.** Four of §5's open questions now have measurements rather than guesses:

- **A realm contains portals.** `Darza's Dominion` reported one and then two `Portal` objects in
  `World.Activatables` (ids 451313 and 451427), with `portalsOther` between 0 and 2. So §8.4's first
  exit mechanism — an exit portal, entered by §5.2's preferred path — exists in the place 3d has to
  leave. §5.4's fourth bullet is answered.
- **Map identity is the easy half of it.** On all nine transitions the `MapInfo` reference, the name
  and the collision grid's dimensions moved *together*; the grid alone separates the three maps
  (Tavern 235×208, The Beyond 81×122, realm 2698×2634). `MapInfo.Name` was byte-identical across
  three separate entries to the realm. So §5.1's "measured, not assumed" recommendation is now
  measured, and any of the three signals would do.
- **`MapInfo.PlayerId` is not an identity, and §6.1's map block should say so.** It changed on every
  entry, including between two entries to the *same* map: the Tavern reported 3335405, 3335496,
  3335688, 3335698, 3335940 and the realm 450589, 451133, 614358. It is a per-world-instance number.
  Nothing keys on it, and nothing should.
- **The realm's geometry is real numbers now.** The character entered at (660.2, 1427.1) of a
  2698×2634 map, and the session's own walk reached y = 2120. That is the "bottom part of the map"
  §5.3 has been waiting for, as a coordinate rather than a heuristic. The full grid §8.1 wants is
  7.1 M cells — worth the size guard, not worth worrying about.

**And three things §5.1 does not anticipate**, each of which would have been a defect in 2b:

- **`SafeWorld` is not settled on the frame the map changes.** Six of the nine transitions fired the
  `safeWorld` bit, and in three of the nine the value on the transition line disagrees with the next
  frame line: The Beyond is `false` then `true` (f 5380 → 5396), the Tavern `false` then `true`
  (5451 → 5456), and the realm — the one that matters — `true` then `false` (5639 → 5666). It goes
  wrong in both directions, so it is a transient and not a lag. **A state machine that reads
  `SafeWorld` on the change frame classifies the realm as the lobby for one frame**, which is
  precisely the frame §5.1 wants to decide `IN_LOBBY` on. Read it a frame later, or key the lobby on
  the map name and use `SafeWorld` as a confirmation.
- **`SafeWorld` is not the same question as "is this the lobby".** *The Beyond* is
  `SafeWorld == true` — `Utils.SafeWorld` says so — while being an 81×122 map with no portals and no
  player-vs-world enemies. §5.1's lobby bit is therefore necessary and not sufficient, and 2b has to
  name the lobby it wants.
- **The lobby contains things `World.GetEnemies()` calls enemies.** Every Tavern sample carries three
  rows — two at 69420/69420 HP and one at 1000000/1000000 — with `npc: false`, `dead: false`. So §5.1's
  `IN_LOBBY = World.SafeWorld && no target available` is false in the room the operator starts in: a
  target *is* available, it just is not one worth walking to. The acquisition rule of §4.2 has to be
  gated by the world state, not by whether the target list came back empty. That is a finding about
  the plan's own state machine, not about the client.

**What it could not fill, and why.** The portal positions were `0`, the portal titles and tags were
`null`, and the player's `hp`/`maxhp` were `0` — on every one of the 292 frame lines. The header named
two of those three columns as missing and said nothing about the third, and the offline verifier was
green on all of it. All three are the same class of defect, and each is now fixed:

| column | why it was empty | what it is now |
|---|---|---|
| `Portal.x`/`.y` | the writer looked for a `float X` (then `_x`) on the portal's hierarchy. There is none: a portal's position is `Node.Position`, a `Vector3`, and the client's own convention is `Position.X`/`.Y` in grid units (`StaticObject.LateUpdate` asks `World.Tilemap.GetTile((int)Position.X, (int)Position.Y)`). Neither accessor resolved, `CallFloat(null, …)` answered 0, and **the column was not in `missing` at all** | read through the same `NestedFloat` the player's position uses, with `ExactPosition` as the fallback |
| `Portal.title`/`.tag` | resolved as *properties on `Portal`*. Both are public **fields on base types** — `ActivatableObject.Title`, `GameObject.Tag` | a field-first, then property, hierarchy walk |
| `player.hp`/`.maxhp` | `Player` declares `public new int Health`, shadowing `Entity`'s `double`. The hierarchy walk found the int, the accessor returned `double`, and the JIT reinterpreted the bits: **42 read as 2.08E-322**. The header did not name it, because it *had* resolved | `FieldAccessor` now converts any numeric width, refuses a field it cannot read with an exception that names the field, and the header gained a `resolved` block saying which member each column came from |

The third one is not only the log's. `DodgeAdapter`, `SteeringPilot` and `TraceRecorder` all built the
same mistyped health accessor, so the dodge's `Player.Health` and the trace's health column have been
2.08E-322 on every live frame. Nothing observable changes today: the escape gate's damage inputs are
zero by construction (`DodgeAdapter`'s header, note 1), so `ProjectedUnavoidableDamage > Health *
LethalFraction` is `0 > ~0`, still false; and `SteeringPilot`'s liveness gate happened to be correct
by accident (a health of 0 reads as 0.0). **The day Deliverable 3.4 arms the damage inputs, the gate
would have fired on the first unavoidable bullet, because the threshold it compares against was 0.**
That is the item this run's evidence bought.

Two smaller things the file now says and did not: a frame with no player writes `"ok":false` rather
than `(0,0)` (every world change produces one, on both sides of the change — the player is torn down
and rebuilt), and the speed column no longer reports a displacement against that placeholder (the run
has exactly one such row, `"spd":1917` at f=3146, on a character that walks at 9.3).

**What the run does not do.** No dungeon was entered, so §8.2's whitelist still has no entries and the
`Title`-as-entry-gate measurement has not been made. The lobby walk was sampled (spawn (117.5, 123.5)
toward (127.6, 106.1)) but the portal it approached has no position in this file, so that leg is not
yet a route. And all of it, including the three fixes, is offline-verified only — `WorldLogVerify`
now asserts each of these columns against a fake game shaped like the client rather than like the
resolver, which is why it will not pass on a re-run that is still zero. **The re-run is the same one
flag**, and it is the only thing between §5.2 and a farmer that walks out of the lobby.

### 12.9 The re-run: every column filled, and what a dungeon actually looks like

`20261018-014055-p14892`. The header is v2, `missing` is empty, and `resolved` names every member:
`Portal.X → Node.Position.X`, `Portal.Title → ActivatableObject.Title`, `Portal.Tag → GameObject.Tag`,
`Player.Health → Player.Health (Int32)`. 365 lines: 358 frame lines and 7 transition lines over frames
550–11260. Every number below is a row of that file.

**It answered the two questions §12.8 could not**, and it answered a third that §8 had given up on.

| question (§8.2 / §5.4) | measurement |
|---|---|
| is `MapInfo.Name` the whitelist key? | **Yes, and `AssetName` cannot be one.** Both dungeons visited exposed it: `Outback Hunt` reports `asset` as `OutbackHunt1.rmap|OutbackHunt1.rmap|OutbackHunt1.rmap` (a pipe-joined triple) and `River Maze` reports `null.null`. Names are clean single tokens: `Outback Hunt`, `River Maze`. |
| can the whitelist gate entry *before* entering? | **Yes.** Every realm portal carries a `Title` of the form `<dungeon name>\nLV <n>`, and the name is *exactly* the `MapInfo.Name` the client reports after entry — `Outback Hunt\nLV 25` → `Outback Hunt`, `River Maze\nLV 25` → `River Maze`. Measured on both dungeons, so it is a rule and not one lucky portal. |
| what is a dungeon, as opposed to a realm? | `MapInfo.Difficulty` is the tier — **0 = lobby, 1 = realm, 2 = dungeon** — and `MapInfo.Levels` is `"Level: 25"` in a dungeon and `""` everywhere else. Either would separate the three worlds; `Name` is still the identity. |

The four worlds, as the file reports them:

| `Name` | `AssetName` | size | `Difficulty` | `Levels` | `SafeWorld` |
|---|---|---|---|---|---|
| The Tavern | `TheTavernRemade_Halloween.bmap` | 235×208 | 0 | `""` | true |
| Darza's Dominion | `NewDominion2048.rmap` | 2698×2634 | 1 | `""` | false |
| Outback Hunt | `OutbackHunt1.rmap|…|…` | 331×339 | 2 | `Level: 25` | false |
| River Maze | `null.null` | 1024×1024 | 2 | `Level: 25` | false |

**Entering is standing on the portal.** Not next to it, not in front of it — *on* its `Position`, and
the file has four walks into three portals:

| portal | `Position` | character's last position before the world changed | distance |
|---|---|---|---|
| `[US] pocquette (45)`, The Tavern | (125.5, 109.5) | (125.468, 109.347) at f 730 → realm at f 771 | 0.16 |
| `[US] pocquette (46)`, The Tavern | (125.5, 109.5) | (125.092, 109.675) at f 2140 → realm at f 2193 | 0.44 |
| `Outback Hunt\nLV 25`, the realm | (1244.058, 301.597) | (1244.271, 302.152) at f 1900 → Outback Hunt at f 1927 | 0.59 |
| `River Maze\nLV 25`, the realm | (463.791, 1200.188) | (463.791, 1200.188) at f 11020 → River Maze at f 11037 | 0.00 |

So §5.2's "walk to it, then enter it" is one goal point — the portal's own `Position` — and the
client's `ActivatableObject.ActivateDistance` (2 units) is the radius that matters, not a click. What
the file records is a *position*, not an activation, so the attribution rests on the coincidence
being 0.16 units twice with no other portal inside two units; that is the strongest reading a log can
give. It also settles §5.2's third question, "which portal leads to the realm", and the answer is the
one nobody would have guessed from the plan: **the lobby's realm entrance is a portal titled with a
region, a player name and a level** — `[US] pocquette (45)` — and it names the *realm instance*, not
the destination, because the destination is always `Darza's Dominion`. §5.2's "with no target
identifier available, the choice is by position and by rank" therefore stands; the title is a filter
(`[EU]`/`[US]`) and a log line, not a destination.

**Titles are not identities.** `[US] pocquette (45)` became `(46)` and then `(47)` during the session
— the host levelling — while the other five titles were byte-identical across every sample. Both
facts matter: a whitelist may key on a dungeon's title, and nothing may key on a lobby portal's.

**`World.Activatables` is server-fed, not a spatial query.** Three realm portals appeared while the
character stood still at (1251, 270): one 16 units away, then one 1 284 units away, then one 1 219
units away. So "find the portals" (§5.2 item 1) is "keep the ones the client has been told about";
a farmer cannot enumerate a map's portals from one place, and a portal it wants may arrive while it
is farming somewhere else. Six realm portals were learned in 10 000 frames, in clusters a thousand
units apart, each with a `Position` that never changed.

**Leaving a dungeon goes to the lobby, not back to the realm**, and not through the portal that names
the destination. *River Maze*'s only portal is titled `Darza's Dominion` and the character stood on it
at f 11080–11140; the world change at f 11177 went to **The Tavern**. Same shape from *Outback Hunt*
(f 1927 → f 2063, The Tavern), which had no portals at all. `hp` never moved off 738/738 and `dead`
was never true, so this is not a death: it is the nexus, §8.4's mechanism 3, and it is the one that
needs no per-dungeon knowledge — which is the whitelist's whole safety argument, now measured once.

**The dungeons were looked at, not cleared.** 136 frames in *Outback Hunt* and 140 in *River Maze*
(~2.3 s each), the character never left the entry point (`spd` 0 on every sample), no enemy was ever
`dead`, and `hp` never changed. §6.2 item 7's "walk to the boss, kill it, note how the exit works" is
still not done, so §8.3's clear-and-advance has no data and §8.6's boss lead is untouched.

**And the enemies are still the thing the lobby cannot be defined by.** 51 distinct ids in the realm;
the same stationary, high-HP, `npc:false` entities in every world — 69420 in the lobby and at both
dungeon entrances, 1000000 in the lobby, 106000 in *River Maze* — with exactly one of them seen
moving (Outback Hunt id 265, 69420). §12.8's finding stands unchanged: `!Dead && !Npc` does not
exclude them, so farm acquisition has to be gated by the world state and not by the target list.
No enemy in 358 lines was ever `dead`, which is consistent with a session that killed nothing.

**One thing the instrument still owed, now paid.** §12.8's only unanswered question was *which* lobby
object enters the realm, and the file said `"portalsOther": 23` — a count, not a name. The client has
**40** direct `ActivatableObject` subclasses, 39 of them not `Portal` (`SoloDungeonStatue`,
`ChainingPortal`, `NpcShop`, `VaultTerminal`, `DungeonChest`, …), so the entrance could have been any
of them. The frame line now
carries an `others` block: the non-`Portal` activatables summarised **by runtime type**, each with its
count, its nearest instance's `Title` and that instance's position. The next session answers the
question for good — and, from this run, the answer is probably "none of them": the entrance was a
`Portal`, it just did not look like one.

Two smaller measurements worth having. The realm reports `CanTeleport = true`, and a 1 219-unit jump
appears between two samples of a character that was farming (f 10990 → 11020) — so positions in that
map are not always walks, and a recorder that treats every pair of samples as a step records a
teleport as one. And the whole lobby is four portals inside a 30-unit square, which makes §5.2's
"walk down a bit" a route of a few seconds rather than a navigation problem.




### 13. The private module: what ships, what does not, and how to drop it in

The operator's constraint, recorded before the next row was built:

> *"A constraint of the autofarmer is that I do not want to share it like everything else. I should be
> able to drop in the class file or .dll and have it, but not pushed to remote or packaged in
> releases."*

This is a distribution decision, not a behaviour change: **the auto-farmer's decision code is not
published.** Everything else in this component is — the solver, the planner, the point-goal plumbing
that the farmer and the routes share, the world log, the harness, and this document. The line is
drawn at the one file that decides which enemy to walk at.

| | public tree | private |
|---|---|---|
| `hook/managed/Drelay.Bootstrap/Farmer.cs` | **the stub**: `Drelay.Bootstrap.Farmer` with the module's whole public surface and no behaviour (`ChooseTarget` publishes nothing, every counter stays zero, `Installed` is `false`) | — |
| `hook/private/Farmer.cs` | — | **the module**: the same class with the target selection, the stickiness rule and the counters, `Installed = true` |
| the point-goal plumbing (`DodgeSnapshot.GoalHasPoint/GoalX/GoalY`, the solver's `GameplayTarget` resolution, the planner's point target, the `stand` range condition) | yes, and it does nothing without a publisher | — |
| the harness's five `farmer*` fixtures and their W1–W6 assertions | yes, and they **skip by name** on a build without the module | — |
| this plan, including §4 and §12 | yes | — |

#### 13.1 The mechanism, in five places

1. **`hook/.gitignore` ignores `private/`.** This is the mechanism, not a convenience: a release is
   built from a checkout, and a checkout contains only tracked files, so a released
   `Drelay.Bootstrap.dll` is the stub build by construction.
2. **`build.ps1` prints which build it made** — `private farm module: absent` or `private farm
   module: N source(s)` with a red *NOT FOR DISTRIBUTION* — and stamps the answer into
   `out\VERSION.txt` as `farm module: public stub` or `farm module: private (NOT FOR DISTRIBUTION)`.
   The stamp is the fact that travels with the artefact.
3. **Both project files apply the same rule** (`Drelay.Bootstrap.csproj` and
   `SolverHarness.csproj`): if `hook/private` exists, `Compile Remove` the stub and compile
   `private/**/*.cs` instead, and define `DRELAY_PRIVATE_FARMER`. They are separate files and could
   drift, which is why the harness is also the thing that checks the result (point 5).
4. **`Farmer.Installed` is a `const`** — `false` in the stub, `true` in the module. It is the only
   fact the two files may disagree on, and being a `const` means no caller can be compiled against
   the other build's answer. It is how the adapter warns at arm time that `DRELAY_DODGE_FARM=1` was
   set in a build that has no farmer, and how the harness decides to skip rather than fail.
5. **`Test-HookOffline.ps1` checks the boundary in three ways**: `git check-ignore` proves
   `hook/private` cannot be committed; `out\VERSION.txt` must name the build that the private
   directory implies; and `SolverHarness --scenario farmer` must *skip* when the module is absent and
   *run* when it is present. The last one is a separate program compiled from the same rule, so it
   catches one project file applying the rule and the other not.

The release workflow adds a fourth guard: `hook-release.yml` refuses to publish if `hook/private`
exists in the checkout or if `out\VERSION.txt` does not say `farm module: public stub`. A release
that contains the farmer cannot be taken back, which is why the check is in the pipeline as well as
in the ignore file.

#### 13.2 Switching it on: `--autofarm`

The farmer is one launch switch, `--autofarm`, or one configuration key, `"Farm": { "Enabled": true }`.
Both go through the launcher's single implication pass, so the two spellings produce the same run:

| what it turns on | why it is a dependency and not a preference |
|---|---|
| the dodge (`DRELAY_DODGE=1`) | the farmer publishes a POINT GOAL; the solver is the only thing that turns it into a heading. A goal nobody reads is a number. |
| the live actuator (`DRELAY_DODGE_ACT=1`, `DRELAY_MOVE_STEER=dodge`) | the decision has to reach the input or the character never walks. This is the *same* actuator `--dodge-act` names — `--autofarm` does not add a second way to move the player. |
| `AutoAim = "closest"`, **only when no other source chose a mode** | `Player.Shoot` aims through the aimbot, so without a mode the client shoots somewhere other than where the character walked. An operator's `--autoaim highesthp` or config `AutoAim` is left alone. |
| `DRELAY_DODGE_FARM=1` | the module's own switch (`DodgeConfig.FarmMode`). Nothing else in the launcher writes it, and a stale value in the operator's shell is dropped when the launcher writes its own. |

**Firing is still not the hook's, and must not be.** `PLAN-AUTOFARM.md` §3: auto-fire is the client's
`InputOptions.AutoFire`, and the hook writes no firing input on any run. The launcher help says so,
because "it walked up and did not shoot" is the one failure `--autofarm` cannot diagnose for you.

Two contradictions are handled rather than discovered: `--autofarm --move-steer X` is **refused** by
name (two pilots, both typed by hand), and `"Farm": {"Enabled": true}` in the file beside
`--move-steer` on the command line is a **run** in which the command line takes the actuator and the
file's request is cleared with a notice — the same rule `Dodge.Act` already followed.

The template a first run writes names the key and leaves it `false`: a double-click is not a request
to farm. `LauncherHarness` checks all of this as `c10`, including that no run without the flag writes
`DRELAY_DODGE_FARM` and that the flag on a public build still parses (it just cannot walk).

#### 13.3 Dropping it in

**The class file.** Put `Farmer.cs` at `hook/private/Farmer.cs` and run `build.ps1`. The build picks
it up, removes the stub, prints the red line, and stamps `out\VERSION.txt` as private. Nothing else
changes; the module's surface is the stub's surface, and a member added to one and not the other
breaks the build loudly in whichever direction it is missing.

**The DLL.** Take the `Drelay.Bootstrap.dll` a private build produced and copy it over the one in
`out\`. The bootstrap is the only file that differs — it is where `Farmer` lives — so there is no
second assembly to load, no plugin directory to configure, and no version skew between the module
and the code it feeds. `out\VERSION.txt` is stale in that hand-copied case; the private build's own
stamp is the record of what was copied.

**Why not a runtime plugin.** A `Drelay.Farmer.dll` loaded with `Assembly.LoadFrom` would make the
module replaceable without a rebuild, and it was considered. It loses on two counts that matter
here: the first call into an un-JITted plugin would happen inside a patched game frame, which is
exactly what `DodgeAdapter.Prepare` exists to prevent; and the plugin's ABI against
`DodgeSnapshot`/`DodgeConfig` would become a thing that can drift from the bootstrap's, where today
it is the same compile. A one-file swap is the smallest mechanism that satisfies the constraint.

#### 13.4 What this does not hide

The plan describes the farmer's design in §4 and records its measurements in §12, and both stay
public on the operator's explicit instruction. What is private is the implementation of the
selection rule, the hold, the counters and the arrival policy's farm-mode clause — enough that the
published binary cannot farm, and not a claim that the technique is secret. The public build is
otherwise the same program: `DRELAY_DODGE_FARM=1` is still parsed, `FarmEngageRadius` and
`FarmStopRadius` are still settings, and the solver still resolves a point goal — because the routes
(§7) publish one too, and they are not private.

**The docs in §12 cite commits that no longer exist in that form.** The farmer's decision code was
committed locally before this constraint was known, so the local branch was rewritten to replace
every historical `Farmer.cs` with the stub; the SHAs in §12 are the post-rewrite ones. The branch is
still not pushed: the module's history is not something a rewrite can put back into the public
record, and it never needed to be there.


---

### 14. The first live farm session: critters, a three-unit distance, and the lobby it never left

`20261018-023243-p21896`, the first run with `--autofarm`. It worked -- the farmer walked to mobs,
held targets, switched seven times over ~5900 frames, and the dodge stayed in charge -- and it did
three things the operator did not want. Each is recorded here with the number that decided the fix.

**14.1 It walked at critters, and the report could not say what they were.** The farmer's line read
`target=418433 at=0.85`; nothing in the run said what 418433 was. The realm's ambient creatures are
ordinary `Enemy` objects to `World.GetEnemies()` -- `Dead` false, `Npc` false, a position, a health
bar -- so every filter the farmer had passed them, and they die when the character walks over them,
so the character spent its session switching between them. **No geometric rule separates a critter
from a mob.** The fix is the client's own name for it:

- `EnemyReader` now reads `GameObject.Data`, then `GameObjectData.Name` and `.Class`, and carries
  them on `FarmTarget` -- optional like every other accessor, so a build where `Data` moved loses
  the filter and keeps the farmer;
- `DodgeConfig.FarmIgnore` is a comma-separated, case-insensitive substring list matched against
  both strings. **It ships `critter`**, because a default that matches nothing costs nothing: if the
  client does not use that word, no enemy is filtered and the behaviour is the old one;
- the farmer's line now carries the held target's **name**, which is how the next session names a
  name to ignore instead of quoting an id;
- `--scenario farmerignore` is the offline fixture: the near enemy is a `Baby Critter`, the far one
  a `Slime`, and W1d asserts the near one was seen and rejected (`Ignored > 0`) and the far one
  chosen.

If `critter` is not the client's word, one run of `--autofarm` shows the name in the farm line and
`DRELAY_DODGE_FARM_IGNORE=<name>` sets it without a rebuild.

**14.2 It stood too close.** `FarmStopRadius` shipped 1.5 -- one and a quarter units of emitter disc
plus a quarter of slack -- and the run ended holding a target at **0.85 units**, inside a mob's own
contact reach. The operator's request is three tiles, and the shipped value is now **3.0**: still
well inside `FarmEngageRadius` (8), still clear of the emitter disc, and the character stops there
instead of walking into the mob. `DRELAY_DODGE_FARM_STOP` moves it. The `farmerclose` fixture moved
its enemy from three units to six so that W3 still measures closing AND settling, and `farmerdodge`'s
target moved onto the stop radius so the bullet still arrives at the same relative position.

**14.3 It never went to a portal, because nothing asked it to.** The state machine of §5.1 is row 5
and is still not built, but the half the operator watched fail is the half a farmer needs after every
death: the character respawns in the Tavern and stands there. That half is now in:

- **`PortalReader`** (public, a reader and not a policy, like `EnemyReader`) resolves
  `World.Activatables`, the `Portal` subclass, `Node.Position.X/Y` and `ActivatableObject.Title`, and
  samples the frame's portals into `DodgeSnapshot.Portals` -- in a safe world only, because that is
  the only place the lobby pilot uses them;
- **the private module's lobby branch** picks the nearest **realm entrance** -- the portal whose
  title starts with `[US]`/`[EU]`, which is what the 2026-10-18 session measured; `Storage Vault` and
  `Guild Hall` do not -- publishes its `Position` as the point goal, and calls
  `PortalReader.TryActivate`, which is `Portal.Activate(0)`: the client's own entry point, warmed at
  arm time and running the client's own locked/cooldown gates. It retries every ~3 s until the world
  changes;
- **`DodgeSnapshot.GoalIsPortal`** is the one thing the arrival policy needs to know: a portal must
  be walked all the way onto (`FarmPortalRadius`, 1.5) rather than stopped three units short, so the
  solver's `stand` gate reads that radius for this goal and `FarmStopRadius` for a mob.

**What this still is not.** The §5.1 machine's other states -- `IN_DUNGEON`, the whitelist, the
clear-and-advance loop, the explicit exit -- are rows 7-9 and remain unbuilt. The lobby branch is
enter-only; the way back out of a dungeon is still the client's own nexus (§8.4), which is the
measured behaviour and needs no code. And **none of §14's three changes has had a live run**: the
portal reader is reflection against a live `World.Activatables` that no offline fixture can hold.
The run's log will say which half resolved -- `portals: World.Activatables -> Portal | x/y Position |
title Title | enter Activate(0)`, or the named failure -- and the farm line carries
`lobby=<attempts>/<activations> portal='<title>'`.

**14.4 The second live session: a portal it would not approach, a haven it would not leave**

`20261018-030319-p26800`, and every one of §14.1–14.3's changes is in its log. Two of the operator's
observations are the same defect seen twice -- **no target means no goal, and no goal means a
character that stands**.

**(a) The portal approach was the portal's own tile.** The farm line proved the pilot ran
(`lobby=5/5`, `portal='[US] Foley (45)'`), and the world log shows the character walking to
(123.6, 105.6) and entering. What it also shows is the character arriving on the portal and then
standing on it for up to 150 frames. The operator's diagnosis was the fix: *"it could maybe be solved
by trying to pathfind to a coordinate near the portal"* -- a portal is an object on a tile, and a
path search whose target cell is occupied refuses the search, which from the outside is a farmer that
will not walk at a portal it can see. **The goal is now a point `FarmPortalRadius` short of the
portal on the player's side, recomputed every frame from the portal's current position** -- which is
the second half of the operator's sentence, *"since they slightly change position every now and
then"*. The activation test stays measured against the portal itself, so arrival and entry agree.

**(b) The safe haven, and the empty screen, are `TRAVEL`.** After entering a realm the character
spawns inside a small enclosure with no enemies in it; between packs the screen is empty because the
farmer killed everything. Both frames published nothing, and the solver's `stand` early-out answered
`StandSafe` -- the correct answer to "nothing to do". The farmer now has a **roam waypoint**:
`FarmTravelRadius` (25 units) away, south first (the operator's own "bottom part of the map is the
best place to start farming"), then the other cardinals and the diagonals, replaced when reached,
when it is not reached in five seconds, or when a teleport says the map changed. It is published with
`DodgeSnapshot.GoalKeepWalking`, which suppresses `stand` exactly as a route's `GoalFromRoute` does,
so the character keeps walking instead of stopping three units short and calling it arrival. The
25-unit step is deliberate: the planner's window is 64 units, so a short waypoint leaves room to
route around the haven's wall rather than aiming straight into it.

`--scenario farmertravel` is the fixture: an empty world, 600 frames, two or more waypoints, and
**no `StandSafe` frame at all** -- the assertion that the goal, not the dodge's early-out, is what
moves the character. W1 was rewidened for it: a farm goal no longer implies a target, so W1 compares
target frames against frames where a target AND the farmer's point agreed, and allows the goal-source
count to exceed them by the travel frames.

**(c) The real answer is a route, and it is a handoff.** The operator's instruction is to record a
route and to defer it until the proxy↔hook bridge exists. `HANDOFF-ROUTE-BRIDGE.md` is that handoff:
what §7 already built, the four commands the bridge needs (`route.record.start/stop`,
`route.play/stop`), why the hook should keep owning the file, and the one open measurement (what a
recorder should do with a teleport). The roam waypoint is the stopgap and is named as one wherever it
appears.

**14.5 The third session: the portal's own tile, a 20-unit range, and critters as a rule**

`20261018-031916-p21156`. Two of §14.4's changes were in it and one of them was wrong.

**(a) The portal approach double-counted its own radius.** §14.4 published a point
`FarmPortalRadius` short of the portal "on the player's side" to give the path search a walkable
target. But the arrival policy then used `FarmPortalRadius` as the distance at which it stops, so the
character stopped `FarmPortalRadius` + `FarmPortalRadius` = 3.0 units short — and the run's world log
has it holding at **2.7 units** from `[US] Orion (37)` for a hundred and fifty frames, entering only
when the operator walked it the rest of the way. The operator's sentence — *"it never walks to a
portal, I did that manually. But it does enter"* — is exactly that arithmetic.

The fix puts each number back where it belongs: **the goal is the portal's own position, and the
`PathPlanner` snaps a POINT goal whose cell is not walkable to the nearest walkable cell within three
cells**, instead of returning `NoTarget`. That is the operator's original suggestion — "pathfind to a
coordinate near the portal" — made generic, and it also helps a route waypoint that lands on a rock.
The goal point itself is untouched, so the score terms, the distance and the arrival policy still see
the portal; only the search's end cell moves.

**(b) The acquisition range was four paces.** `FarmEngageRadius` shipped 8 units on a 2698x2634
realm — the operator's *"the range it paths to enemies are quite small, can it be increased?"*. It is
now **20**, still inside the planner's 64-unit window so the whole approach is visible to the search,
and still a separate number from the client's weapon range (which decides what can be SHOT, not what
is worth walking to). `DRELAY_DODGE_FARM_RANGE` moves it, and `farmerorbit` pins 8 for itself because
its characterisation is "the character leaves the acquisition radius", which is a different walk at
20.

**(c) Critters are a RULE, not a name list.** §14.1's `critter` substring did not catch them. The
operator names crabs, ducks and *"different kinds"*, and the defining fact is mechanical:
*"they're not shootable or damageable as normal enemies, the only way to subdue them is to walk on
top of them"*. A research pass over the decompiled client found the structure behind that sentence,
and one of its findings killed the first guess:

- **`GameObjectData.Class` is not a gameplay category — it is the .NET TYPE NAME of the data class.**
  `GameObject.Create` selects the live type with `_types.TryGetValue(data.Class, ...)` where the keys
  are `Type.Name`, and `Enemy` has exactly one subclass: `EnemyFlat`. So every object in
  `World.GetEnemies()` has `Class` ∈ {`"Enemy"`, `"EnemyFlat"`}, and no substring like `critter` can
  ever match it (it can only match `Data.Name`).
- **`EnemyFlat : Enemy` is the ground creature.** No shadow, `GlowColor = Transparent`, a
  ground-flat `SetPosition`, and it is added to the world's flat-ground layer instead of the y-sorted
  object layer — it is drawn *under* the player, which is what "walk on top of it" looks like. It has
  a dedicated empty data class, `EnemyFlatData : EnemyData`.
- **`EntityData.NoDie` is parsed and never read** anywhere in 1897 decompiled files. That is the
  signature of a flag enforced on the server: projectiles are applied locally and reported with
  `GmHit`, and the server decides whether they count. It is the only per-object bit in the data that
  says "this does not die to damage".
- **`Effects.Unhittable()` is not the same predicate as "not shootable".** The client's projectile
  hit test skips only `Npc` and two status-effect ids, while `Unhittable()` ORs three — so one of its
  three does not stop a bullet. Client-side, any non-NPC non-invulnerable enemy eats bullets and
  shows damage numbers; a critter's immunity *must* be server-authoritative. The aimbot's own
  refusal is a third thing again: `AimbotSystem.GetEnemyPriority(enemy) == 0` for dead/NPC, for a
  non-`EnemyData`, and for an empty or unrecognised `EnemyData.Exp` — which is the likeliest reason
  the operator sees the aimbot ignore them, and it is an *allowlist of twelve `Exp` values*, so it
  would also drop real mobs and is not used.

So the farmer's filter is now two data rules plus the name list:

- `Data.Class == "EnemyFlat"` — structural, covers every kind the client renders as a ground
  creature;
- `Data.NoDie == true` — the server-side flag, inferred rather than proven;
- `Effects.Unhittable()` — kept, because a phase-invulnerable boss is not a farm target while it is
  invulnerable (and the flag is re-read every frame, so it returns when the phase ends);
- the name list (`critter,crab,duck`) stays as a belt for anything damageable and still unwanted.

`EnemyReader` carries `Class`, `NoDie` and `Unhittable` on `FarmTarget`, the farm line prints the
held target's `class='…' nodie=…` and counts the rule separately (`critters=` beside
`unhittable=`), and the arm-time log names every member the two halves resolved:

```
farm filter: name GameObjectData.Name / GameObjectData.Class / EntityData.NoDie | cannot-be-damaged StatusEffects.Unhittable()
```

**`--scenario farmerignore` is the fixture and the two ignored targets are the two rules**: a `Crab`
whose class is `EnemyFlat`, and a `Duck` with `NoDie`; the chosen one is an ordinary `Slime`.
W1d fails if either rule regresses.

**What one live session settles.** The rule is a *category* test, not a proven identity for these
particular creatures (confidence ≈ 0.7 that crabs and ducks are `EnemyFlat`). The farm line is the
measurement: run `--autofarm`, walk over a crab, and `class='…'` says it outright. `EnemyFlat`
confirms it; `Enemy` refutes it and points at `nodie=true`, and if both are false the answer is the
aimbot's `GetEnemyPriority` — which the reader does not resolve today and would need a new accessor.

**And the two lines are separate lines now.** The farm line had reached the 256-byte truncation
edge with the target name and the lobby/travel counters on it, and the tail it would have lost is
exactly the part a session that never left the spawn needs. `PHASE 3 DODGE FARM` keeps the target
numbers; `PHASE 3 DODGE PILOT` carries `lobby=`, `portal=` and `travel=`.

### 14.6 The quest goal, and the wall half of the walk test

Two defects survived every rule added between §14.6 and §14.12 of the previous revision of this
section: the grid did not always know what a wall was, and a distant quest point had no route. The
compensations (a far-window-edge retry, partial paths, a waypoint pull-back, a blind 20-unit quest
step, a lava preference, an unstick rule, and dungeon-boss / quest-flag enemy fallbacks) are removed.
What remains is the smallest statement of each fix.

**(a) The window asks the client's tile map.** The client's move test is
`in bounds && Tilemap.CanWalk(x, y) && collision[x, y] == 0`. `CollisionWindow.Cells` carried only
the third term, so a `NoWalk` tile with a zero collision byte (the black void, and `NoWalk` terrain
the server never sends collision for) read as open ground. `DodgeAdapter.ResolveTilemap` resolves
`World.Tilemap` and `Tilemap.GetTile(x, y)`; `RefreshGrid` marks a cell solid when the tile EXISTS
and `TileData.NoWalk` is true. The arm-time line is `dodge grid | collision World.byte[,] | tile
walls ON (NoWalk; a missing tile reads as open, not as wall)` or `... MISSING`, and a run that says
`MISSING` still has the old defect. `TraceRecorder` applies the same merge -- `CollisionWindow.
MergeCell`, one function for both call sites -- so an offline replay is a decision about the grid the
live planner saw. The window is also re-read when the collision array reference changes and every
`GridRefreshFrames` (30), because `GmTiles` streams in and a realm change allocates a new array; the
recorder uses the same three triggers, because a trace taken while the character stands still in a
dungeon otherwise misses the map arriving.

**A CELL WITH NO TILE IS NOT A WALL, AND THE FIRST VERSION OF (a) SAID IT WAS.** That reads like a
detail of the same fix and it is a different bug with its own session; §14.10 is the measurement.

The live log confirms all of it: `dodge grid | collision World.byte[,] | tile walls ON
(Tilemap.GetTile/NoWalk)`, and the dump's own `gridSource` on the trace is
`World.byte[,]+Tilemap.GetTile/NoWalk`. The dump also settles what the collision bytes mean: the
server sends **1** and **2**, not only 0/1, and both are blocked -- a distance measurement below
puts the client's refusal boundary at exactly **0.400** from a `2`.

**(b) The quest is the client's own marker.** `QuestReader` resolves `World.Quest` and
`World.EventQuests`, samples `Position` (with `ExactPosition` only as the fallback for a marker that
has not been placed) and publishes a `QuestTarget` per marker. A marker that reads `(0,0)` is
refused and counted (`qskip=`). The private farmer walks the tracked marker first, then the nearest
event marker, publishes its real position as the point goal, and calls `ActivatableObject.Activate(0)`
when standing on an activatable one. There is no step, no dungeon-boss guess and no enemy-data
fallback: if the client offers no marker, the farmer falls through to its enemy/travel branches and
the log says `qsrc=none`.

**(b.1) The operator takes the pathing back by moving.** A point goal is resolved only while the
player is NOT holding a movement input (`!playerWalking`, i.e. not `Moving && IntentValid`). With the
keyboard live, the ordinary goal ladder answers from `PlayerIntent` exactly as it does with the
farmer off; the farmer still reports the marker/target it is holding, so a session can tell "the
operator took over" from "the goal was lost". Releasing the input resumes the point goal on the next
frame. This is a goal selection, not an admission rule, so the dodge's safety boundary is unchanged.

**(b.2) A marker from another map is refused, and that was the "quest pathing does not work".**
`World.ClearWorld()` clears `Objects` and `EventQuests` and calls `GameUI.SetQuest(null)`, but it
does **not** clear `World.Quest`. A tracked quest therefore survives a portal, a death and a dungeon
exit as a reference to the previous map's object, and its `Position` is a coordinate in a map that is
no longer loaded. The 2026-10-19 `20261019-170821-p7272` session is the measurement: the tracked
marker was `id=460422 at 343.6,1364.1 primary=True activatable=False name='Aspect of Corruption'`,
the character was in the Tavern at (118,125), and the farmer held that marker for **165 frames**
(`quest=165/1 qsrc=worldquest`) before `World.Quest` was finally replaced -- 165 frames of walking at
a Tavern wall toward a realm. `QuestReader` now resolves `World.Objects` (the live-object registry,
keyed by `GameId`) and refuses a marker whose id is not in it, counted separately as `qstale=` and
named once in the log. `World.Quest` is only ever assigned from a key of that dictionary, so
membership IS "this marker is in this world". A legitimately tracked quest in another realm is then
not a goal: the farmer falls through to the lobby pilot exactly as it does with no marker.

**(c) What is still open.** The planner searches a 128-unit window; a marker farther than that is
reached a frame at a time through the ray fallback, and a target cell the map does not connect to
produces no path (`nodecap`/`unreachable`). That is the honest state: the release below is what stops
the character pressing the wall, and the next fix is still a real map-scale route. The lines to read
are the arm-time `tile walls` clause, `qsrc=`/`qstale=`, and `PHASE 3 DODGE PATH`'s
`unreachable`/`nodecap`/`notarget` counts.

### 14.7 The stuck character: the footprint, a refusal nobody counted, and the release

`20261019-211704-p7732` is the session the operator described as *"having trouble with the character
getting stuck on walls or terrain when autofarming ... when it is locked on an enemy, it will never
try to path around something when it's stuck"*, and it separates that one sentence into three
defects. Each has a measurement and each has a fixture now.

**(a) The walk test has a FOOTPRINT, not a cell.** The client's movement resolver
(`World.hehe...EF73`, which calls `EF74`) does not test the single cell under the destination point:
it tests `(int)(x ± n)` and `(int)(y ± n)` and their diagonals, twice, at two offsets. So the player
has a **square footprint of half-width 0.4** on the axis test and may not come closer than that to a
nonzero collision cell even with its centre in an open one. Measured on the two sessions:
`20261019-211704-p7732` closest approach while moving **0.401** over 252 frames (median 0.487);
`20261019-220335-p16416` **0.403** over 352 frames next to a value-2 cell, and moving at 0.008 next
to *tile-derived* cells -- which is the other half of the finding: the client's move test reads the
server's collision bytes and knows nothing about `Tilemap.NoWalk`, so `EF74` and `Tilemap.CanWalk` are
two different tests for two different questions.

`CollisionWindow.Walkable` now tests the footprint, so rules 2 and 3 agree with the game. The claim
that this was THE cause of the frozen character was WRONG, and §14.8 is the correction: the freeze was
a `stand` winning the score. The footprint is still right -- a centre-only rule admits steps `EF73`
discards -- and the harness's `W7` is what holds it: the first version of this rule admitted seven
such steps on the live trace, which is `NoAdmissible` renamed.

**The escape that IS in the rule.** The sweep in rule 3 starts at the player, so a position already
inside a footprint violation (a knockback, a teleport, a server correction, a sliding tile) makes every
candidate fail the sweep -- including the one that steps out -- and the character is held still by the
rule that exists to keep it out of walls. Rule 3 is therefore not asked when `Walkable(player)` is
already false. Rule 2, which is the client's own test, still refuses every blocked destination, so
nothing the game would discard is admitted: the skip only stops the sweep from being the thing that
keeps the player in. On `20261019-220335-p16416` the frames with no admissible candidate fall from
**938 to 3**.

**(b) A third of the planner's refusals were invisible.** The same report read `868 search: 541
found, 0 none (unreachable 0, nodecap 0, nogrid 0)`: 327 searches produced no path and the line said
`0 none`. `Plan`'s `BeginFrame`-failed branch increments `Searches` and returns without touching
`NoPath`, so `NoTarget` -- the search's end cell is solid -- was counted nowhere. It is now
`PathStats.NoTargetFrames` and is printed (`notarget`, beside `nogoal`), because a refusal the report
does not count is a refusal the report calls a success.

And the `NoTarget` that mattered was the TRAVEL WAYPOINT IN THE VOID. The farmer's roam point is 25
units out in one of eight directions; in the session above, the first one was deep inside the
cave's wall, and the planner's point-goal snap only looked three cells for walkable ground. With no
path the path term is zero and the goal term points at the wall -- the 62-second freeze. The snap now
falls back to a ring search over the WHOLE window, so the search's end cell is the closest walkable
cell toward the waypoint and the route is the part of the way the map allows. The goal POINT is
untouched: the score terms, the distance and the arrival policy still see the real place. The fixture
is `--scenario farmertravelwall` (`W1f`): a block laid where the first waypoint goes, `NoTargetFrames
== 0`, and the character covers 30 units. Reverting the second snap makes that same fixture report
**309 notarget refusals in 600 frames**, which is the falsification.

**(c) A target with no route is released, not pressed.** What is left after (a) and (b) is a mob the
map does not connect to -- a sealed room, a corridor narrower than the player, a walled-off ledge.
`Unreachable` (or the node cap) is the planner's honest answer there, and no local rule can invent a
route. The hold is what makes it permanent. The private farmer now tracks the BEST distance to the
held target since the hold began and drops it after `FarmStuckFrames` (180, ~3 s) without improving by
`FarmStuckUnits` (0.5) while still outside `FarmStopRadius + 0.5`; it then travels for
`FarmDetourFrames` (150, ~2.5 s) rather than re-acquiring the same mob on the next frame, and
re-acquires it from wherever the detour put it. `StuckReleases` counts it, separately from
`ForcedReleases` (the mob did not die or leave the radius -- the character simply never got closer),
and `stuck=` carries it in the pilot line. The fixture is `--scenario farmersealed` (`W1g`): a mob in
a nine-cell box twelve units east, one release, and 34 units walked instead of a character against a
wall.

**(d) The instruments.** `--scenario farmertravelwall`, `--scenario farmersealed` and the harness's
`W7` are the three that fail without these fixes. The live lines to read are the arm-time
`tile walls` clause, `PHASE 3 DODGE PATH`'s `notarget`, `PHASE 3 DODGE DETAIL`'s `blocked` count
(671 of 868 in the frozen session; it must fall), `qstale=` in the pilot line, and `stuck=`. The
harness's closed loop also stopped being an idealisation: it now applies the client's refusal (the
destination footprint, then the two axis slides, then nothing) instead of adding the step
unconditionally, so a fixture can no longer "reach" a target by walking through scenery.

**What (c) does not cover, and why.** A mob that is CLOSER than `FarmStopRadius` but through a wall
is not released: the farmer is meant to stand and shoot at that range, and the one measurement that
would tell "the shot lands" from "the shot hits a wall" is the client's own hit report, which this
module does not read. The live session has no example of it (every freeze was out of range), and
inventing a rule without one is what §14.6's removals were about. The line that would show it is
`PHASE 3 DODGE PILOT`'s `stand held for range` climbing beside a `target=` that never dies.

### 14.8 The `stand` that would not walk, and the waypoint a follower cannot use

`20261019-220335-p16416`, the run after §14.7. The quest resolved correctly (`qsrc=event`, marker
`Vesta, Axis Gatesguard`) and the character still stopped dead for **40 seconds** at (1209.4, 544.6),
then **15 seconds** at (540.4, 983.6), and the operator walked it out by hand both times. The report
said `Move` on those frames and `blocked` counted 65 % of the session. Two defects, and the first one
is the real answer to *"it does not path around walls, trees and so on"*.

**(a) The `stand` candidate was never actually suppressed, and it won the score.**
`PLAN-AUTOFARM.md` §4.3's arrival policy sets `standSafe = false` while the farmer is closing, which
suppresses the `stand` EARLY-OUT. `stand` is also candidate index 0 in the scored set, and with no
bullets in the air it collects the maximum clearance term (+1.2), the maximum durability (+0.6), the
heading-alignment term (+1.0, because it inherits the player's last intent as its heading) and the
stand bias (+0.15) -- while every real MOVE pays `WeightMove` (0.25). On the operator's stuck frame
2000: **`stand` scored 2.95, the best move scored 1.713**. `Filled` then turns the winner into
`Move | SpeedScale = 0` -- a command that writes `MoveSpeed = 0`, stops the character, and is counted
as a move by every aggregate in the report. That is the freeze, and it only ever happens when the
path's direction is momentarily blocked: a wall, a tree, a corner. Which is exactly the state the
operator described as "stuck on walls and never pathing around them".

The fix: while `_pointDriven` is true, the `stand` candidate's score is `-Infinity` before
`BestAdmissible`, so a closing farmer cannot answer "do nothing". Nothing is admitted or refused by
this; the candidate set, the rules and the score are unchanged, and if every move is refused the frame
reports `NoAdmissible` -- the honest answer, and one the log can be read for. The harness's **`W8`**
("no emitted move has zero length") is the fixture: 294 zero-length moves on the stuck trace's replay
before the fix, 0 after.

**(b) The far goal's waypoint was 75 % of the way along the route, so it pointed through walls.**
A farm goal is usually far outside the planner's 128-unit window (the quest marker here is ~1 000
units away), so `_pointTarget` is false and the target is the RAY's end. The waypoint is then
`PathWaypointFraction` (0.75) of the path's length -- a *reporting* choice made when a near waypoint
looked like the goal direction. With a route that turns a corner it is a control error: on frame 2000
the path was 36.9 units long, its first steps went EAST around the wall, and its 0.75 point was
south-west, so `WaypointHeading` was 150 degrees -- into the wall -- while the only free direction was
30 degrees. The path term could never vote for the step that was available.

A point goal outside the window now uses `PathWaypointUnits` (2.5) like a point goal inside it: a
follower needs the LOCAL direction of the route. The fraction stays for a pure ray, where there is no
place to arrive at and the far end is the only information. Same frame after the fix: waypoint at
(1212.5, 544.5), heading 0 degrees (east, along the wall and toward the opening), path term +1.0, and
the chosen move is east at full scale.

**(c) Reading a live farm session offline.** `--goal x,y` on the harness replays a recorded trace with
a FIXED point goal, because a trace does not carry the farmer's goal or its enemy list and a session
that walked to a quest otherwise cannot be replayed as itself. It sets farm mode, publishes the point
after `ChooseTarget`, and marks T7 not-evaluated (the game's hit decisions belong to the operator's
inputs, not to a synthetic goal). The frame-2000 numbers above are that replay: `--trace
<run>/trace.jsonl --goal 340.5,1065.5 --frame 2000`.

**(d) What the operator will see in the next run.** `PHASE 3 DODGE FARM`'s `blocked` share collapses
(the 22:03 run was `(268 observed, 2387 blocked)`), `PHASE 3 DODGE REASONS` gains `NoAdmissible` on
the frames where nothing is genuinely available instead of `OverrideUnsafe` on a zero-scale move, and
the character walks along the wall it used to stand against. The counters that would still mean
"stuck" are `stuck=` in the pilot line (a held target with no progress) and `blocked` climbing beside
`Move`.

### 14.9 The quest the client knew about and `World.Quest` did not

`20261019-223603-p19004`: Tavern -> Darza's Dominion -> **Infested Cellar** -> Tavern, with a quest
indicator on screen for `Athena, Queen of Arachnids`. Every report in the run, the cellar included
(frames 3 812-7 082), read:

```
PHASE 3 DODGE PILOT | ... travel=2519/15 quest=0/0 qname='' qact=0/0 qpos=0,0 qskip=0 qstale=0 qsrc=none
```

So the answer to the operator's question is **no: the farmer never tried to path to a quest, because
it never had a marker**. `QuestFrames` stayed 0, and the "circles" are the travel fallback -- a roam
waypoint 25 units out (`FarmTravelRadius`), rotated when it is reached or after five seconds, which
is exactly `travel=2519/15`, until an enemy came inside `FarmEngageRadius` and the enemy hold took
over. `qskip=0` and `qstale=0` say the refusals never fired either: the reader was not evaluating
markers at all.

`World.Quest` was null and `World.EventQuests` empty for the whole session. The client knows a quest
object in **two** places, and only one of them is those fields:

- the TRACKED reference, `World.Quest` / `World.EventQuests` -- what the minimap draws and the quest
  arrow points at, and what `QuestReader` has always read;
- the OBJECT'S OWN DATA -- `EntityData.Quest` and `EntityData.EventQuest`
  (`DarzaCore.Data.Entities.EntityData`). The client's own UI reads the second one: `DesktopMMOUI`'s
  `SetQuest` keeps the arrow alive for a dungeon boss by testing `Boss?.Data is EntityData d &&
  d.EventQuest`, and `EntityData.EventQuestType` names the arrow sprite. A boss is announced by the
  boss-health-bar packet, and its data flag does not require `World.Quest` to be set.

`QuestReader` now scans `World.Objects` for both flags, every `QuestScanFrames` (10) frames, and
publishes what it finds as markers with `Scanned = true`. The scan resolves the flags by walking the
**assembly's types** for classes below `GameObject.Data`'s declared type that declare `Quest` /
`EventQuest` -- the fields live on `EntityData`, which is BELOW `GameObjectData`, so a hierarchy walk
from `Data`'s static type finds nothing. That was the first version's failure, caught by the new
fixture before a live run.

**The instruments, because this is a source question and not a pathing question.** First, a report
line of its own:

```
PHASE 3 DODGE QUEST | Quest=<none|id=.. name='..' at x,y> | events=<n> | scan=<n> obj, <m> flagged
| seen=<n> skip=<n> stale=<n>
```

Second, the pilot line carries `qseen=` (`MarkersSeen`) and `qdata=` (`DataMarkers`) beside `qskip=`
and `qstale=`, and `qsrc=` now distinguishes `worldquest`, `event` and **`data`**. A session that
still never quests will say which of the three it is:

- `scan=0 obj, 0 flagged` with `events=0` -- the client flags nothing, and the next step is a third
  source, not another pathing fix;
- `flagged>0` with `seen=0` -- the reader sees them and the fill does not publish them (a counter bug);
- `seen>0` with `quest=0` -- published and declined, and `qname`/`qpos` say what was declined.

The fixture is in `WorldLogVerify`: an `Enemy` at (320, 240) called `Athena, Queen of Arachnids`,
whose `EntityData.Quest` is true, present in `World.Objects` and absent from both tracked
collections. `QuestReader.Fill` finds it as a marker, and the check fails without the scan.

### 14.10 The dungeon that read as a sealed box: a tile that has not streamed in is not a wall

`20261020-171352-p40648` is the session with the fix of §14.6(a) in it, and the operator's report on
it was two sentences: *"the quest/boss locator works in the world, it however does not work inside
dungeons ... when entering a dungeon the pathfinder falls back to the circular discovery movement"*,
with the observation that the client presents the boss as a dot and a direction arrow rather than as
a place a route can be drawn to. The dot was not the problem. The grid was.

**The measurement.** *Frost Cavern* (frames 27 604-29 340 of the run; `MapInfo.Name` = `Frost
Cavern`, `Difficulty` 2). The character entered at (513.4, 503.7), next to the exit portal, and spent
the whole segment inside a circular entry room of radius ~19 (1 094 cells). The only enemy and the
tracked quest marker were the same object: `Gradvale, Mage of Winter`, `qsrc=worldquest`,
`qpos=649.5,561.5` -- 130 units east, behind the room's wall. The two reports inside that segment
read `quest=24703/22` and `quest=25603/22` with `qname='Gradvale, Mage of Winter'` unchanged and
`stuck=0`, so the farmer held that marker throughout and the character covered 30 units. The trace's
own grid says why:

| frame | player | walkable cells in the window | player's walkable component |
|---|---|---|---|
| 27 640 | (513.0, 504.4) | 474 | |
| 28 000 | (522.1, 521.6) | 880 | 1 027, does **not** touch the window's edge |
| 28 500 | (527.2, 533.6) | 1 094 | 1 027, same |
| 29 300 | (528.4, 533.4) | 1 094 | 1 027, same |

The window is 128x128 and the room is ~37 units across, so the component is the room: **a closed
island, 1 027 walkable cells, with rock on every side inside the planner's whole horizon.** The
pathfinder answered `Unreachable`/no route on those frames, the path term was zero, the goal term
pointed into the wall, and the character bumped around its room -- the "circular discovery movement".

**And the rock was not rock.** Comparing the same absolute cells between frames 27 640 and 29 300 --
the character moved 30 units inside one room, so this is not a map change:

- **620 cells went `1 -> 0`** (solid -> open floor),
- **364 cells went `1 -> 2`** (solid -> solid, but now with the server's own byte on it),
- 10 203 were unchanged.

`Tilemap` does not arrive with the world. `GmTiles` streams it in, and `Tilemap.GetTile(x, y)` answers
**null** for a cell that has not arrived yet (it answers null for out-of-map too, which the callers
already handle with the map's dimensions). §14.6(a)'s rule was `tileData == null || NoWalk -> solid`,
so every cell whose tile had not streamed in became a wall: the room's own floor was 474 cells on the
entry frame and 1 094 three seconds later, and the 1 027-cell island is exactly the part of the room
the streamer had not reached. A dungeon's map is bigger than its first room and arrives a piece at a
time, so the rule that fixed the overworld's black void sealed every dungeon.

**The rule now, in one function for both call sites.** `CollisionWindow.MergeCell(collision,
hasTile, noWalk)`:

1. a nonzero collision byte is a wall (`1` and `2`; the 0.400 footprint measurement of §14.8 is
   against the `2`);
2. a tile that EXISTS and says `NoWalk` is a wall -- the black void, which is what §14.6(a) was for;
3. **a cell with no tile is no information, so the collision byte answers.** In a dungeon those
   bytes are `0` for everything the server has not painted, which is the intended reading: the
   planner may route through unexplored space and the character discovers it, and the client's
   streamer paints ahead of the character (this session walked 30 units through cells that had read
   solid a second earlier).

The reverse direction is bounded by the client, not by us: `Tilemap.CanWalk` refuses a cell with no
tile, so a mis-predicted step is ONE refused step at a frontier the server is already painting
through, and the 30-frame `GridRefreshFrames` re-read is what turns the arrived tiles into a new plan.
The prediction can be wrong about a wall and cannot be wrong about a collision byte.

**Nothing about the waypoint changed, and that is the finding.** `ChooseQuest` publishes the marker's
real `Position` (`PLAN-AUTOFARM.md` §14.6(b)) and it did so throughout this segment; the writer of the
goal was right and the map the planner read said the goal did not exist. The approach the operator
suspected -- setting the quest waypoint -- is the same code that works in the overworld, which is why
"works in the realm, not in a dungeon" is a statement about the grid and not about the goal.

**The instruments.** `TraceRecorder`'s window now uses the live window's three refresh triggers
(player tile, grid array, `GridRefreshFrames`) instead of the player's tile alone: a character
standing still in a dungeon is precisely the case where the map arrives and the position does not,
and a trace keyed only on movement recorded the first, mostly-unpainted window and never corrected
it -- so the recording that proves this fix could not have contained the fixing data. `SolverHarness`
runs `G1` (the `MergeCell` truth table) and `G2` (a 41x41 room whose tiles have not arrived: 317
walkable cells from the collision bytes, 317 under the rule now, **0** under `tileData == null ->
wall`) on every invocation. The arm-time line reads `tile walls ON (NoWalk; a missing tile reads as
open, not as wall)`, so a run can tell this rule from a run with no tile data at all. And each `G`
record now carries **where its solid cells came from** -- `colWall` (the server's collision bytes plus
the out-of-map border), `tileWall` (an existing `NoWalk` tile) and `noTile` (not arrived) -- which is
the one question the merged bytes cannot answer for themselves: a room that is solid with
`noTile > 0` is a room whose map has not arrived, and one solid with `noTile == 0` is a room that is
really sealed. The next live dungeon session's lines are `PHASE 3 DODGE PATH`'s `unreachable` (it must
fall for a marker across a room) and those three counts in a re-recorded trace's `G` records.

**What is still open, and why it was not guessed at.** The target for a goal outside the window is
still the RAY's end (`PathTargetSeconds`, ~12 units), and that cell is inside the grid's knowledge
only as far as the window reaches. A dungeon corridor that leaves a room at a right angle, more than
~12 units off the line to the boss, is therefore not on the ray: the search can answer `Unreachable`
and the character walks at the room's wall until the streamed map turns the ray into a walkable
straight line again. The obvious next rule -- when the target is unreachable, return the path to the
REACHABLE cell nearest the goal instead of refusing -- is a change to what `Unreachable` means for
every goal shape, including the dodge's own ray, and this file has one measurement of the sealed box
and none of that. It is the first thing to try if the next dungeon session still presses a wall, and
`PHASE 3 DODGE PATH`'s `unreachable` count is the number that would justify it.

**And the next session supplied the first half of that.** §14.11 is `20261020-175713-p15344`, the run
with the rule below in it: `notarget` had moved to the *ray's* end rather than to a streamed box, and
it was 80% of the session. The ray now snaps beside a wall the same way a point does; the remaining
question is a room the map itself encloses, which is where the `src` instrument comes in.

### 14.11 The second Frost Cavern run: the goal ray that ends in a wall, and a room the map calls sealed

`20261020-175713-p15344` is the first session with §14.10's rule in it, and the operator's report is
*"new run, gets stuck in this room, boss location is to the north-west"*. Two findings: one in the
planner, one in the map, and only the first is ours to fix blind.

**What the run says.** Frost Cavern from frame 1 898. The character never leaves a 23x38-unit box
(x 492.6-515.4, y 502.8-541.2) for 7 100 frames while the farmer holds the quest marker on almost
every one of them (`quest=7854/2`, `qsrc=worldquest`, `qname='Gradvale, Mage of Winter'`,
`qpos=426.5,654.5` -- 145 units away, the only enemy in the map). The planner line is

```
PHASE 3 DODGE PATH | 8054 search: 1576 found, 0 none (unreachable 0, nodecap 0, nogrid 0, notarget 6478, nogoal 0)
```

read at the end of the run, and it grows by ~900 `notarget` per 900-frame report from the dungeon
entry onward -- **every frame of the dungeon refused with a target the planner would not aim at**, and
`unreachable` never fires at all because the search never ran.

**(a) A goal beyond the window is a RAY, and its end was landing in the wall.** `BeginFrame` takes a
point goal's own cell only when the point is inside the 128-unit window; a marker in the next room is
outside it, so the target is `PathTargetSeconds` of travel (~12 units) along the direction to it --
which in a dungeon is the wall between the rooms. A solid target answered `PathReason.NoTarget`, so
the path term contributed nothing on exactly the frames a route was needed, and the character had
only the goal term to press the wall with. A point goal has been snapped to the nearest walkable cell
since §14.7(b) ("a point on a solid cell is not `NoTarget`, it is `end beside it`"); the ray had been
left refusing on the argument that a ray has nowhere to route to. That argument is about where the
target IS, not about whether a route can exist.

The ray now snaps within `PathPointSnapCells` (three), and no further, because a ray names a
DIRECTION: snapping it across the window would let it jump behind the player and produce a route with
nothing to do with the goal. Measured on this very trace, with the recorded grid and the boss as a
fixed goal (`SolverHarness --trace <run>/trace.jsonl --goal 426.5,654.5`):

| | path found | `notarget` | `unreachable` |
|---|---|---|---|
| before | 4 700 of 8 385 (56%) | 3 684 (44%) | 0 |
| after | 7 186 of 8 385 (86%) | 1 147 (14%) | 40 |

The remaining 14% are frames whose 12-unit ray end is more than three cells inside the wall, and that
refusal is honest -- a direction that deep into rock says nothing about a way round. The live session's
rate should fall from ~80% to roughly this, which is 5 000 more frames with a path term. The fixture is
`SolverHarness`'s `G3`: a one-cell wall in open ground gives `Found`, and a six-cell wall (the whole
three-ring solid) still gives `NoTarget`.

**(b) The room is sealed in the client's own walkability, and that is the finding.** With §14.10's
merge in place, the window at frame 9 008 contains 13 275 `0` cells of which 11 945 are unpainted, 563
collision cells and 2 546 `NoWalk` tiles -- so **1 330 cells are real floor**. The player's own
reachable component is **1 204 cells and does not touch the window's edge**: the room is an island
surrounded by painted `1`s and `2`s, and the ~126 floor tiles outside the component are other areas of
the map, painted and disconnected. So there is no walkable exit within 128 units of the room, and
`Tilemap.CanWalk` refuses the ring: the character cannot walk out, and no waypoint choice can change
that. It is not the goal selection -- §14.6(b)'s marker writer is doing exactly what it should.

And the room is not featureless: it holds a `LootShrine` at (508.5,518.5) and -- intermittently -- the
dungeon portal. `World.Activatables` reports `Darza's Dominion@511,502` at frames 1 898-1 958, nothing
from 2 138 to 5 047, the portal again at 5 048, nothing again after 5 078. What the game's own way out
of that room is -- a door that opens, a portal that must be activated, a pit, or a room generated as a
dead end -- is not in the client's tile or collision data, and this file has no measurement of it. It
is the next question, and it is a question about the DUNGEON, not about the planner.

**(c) The instrument for that question is now in every `G` record.** The merged bytes cannot tell the
three kinds of solid apart, so each grid record now carries **`src`**, a second run-length encoded
array with one byte per cell: `0` unpainted, `1` the server's collision byte, `2` a `NoWalk` tile, `3`
floor, `4` outside the map. Alongside it: `floorCells` (walkable cells that carry a tile, i.e. floor
including the parts the player cannot reach), `wallType` and `floorType` (the commonest terrain id in
each class). One more dungeon session therefore answers, from the file rather than by inference: where
the painted map ends, what the ring around the room is made of, and whether the other rooms' floors are
`NoWalk` or merely unreachable. `TraceFile` reads all of it (`GridWindow.Source`, `SourceAt`, and a
`-1` for a trace recorded before the field existed).

**What the next session should read.** `PHASE 3 DODGE PATH`'s `notarget` (it must collapse, and
`unreachable` should replace it where a room really is closed), the `G` record's `src` around the stuck
room, and the `others` block in `worlds.jsonl` for whatever object the room's way out turns out to be.

### 14.12 The route longer than the window: River Maze, Frost Cavern, and the map that streams

`20261020-181445-p4196` is the run the `src` instrument from §14.11 was built for, and it settles the
two questions the previous two sections could not. The operator's report is two cases:

> River Maze: walked up, got stuck in a room the boss room was to the east of, but the walkable path
> had to continue walking north, then east, then south to reach it.
> Frost Cavern: Stuck in spawn room.

**(a) An unexplored cell is first painted `NoWalk`, and only later becomes floor.** One boss-room cell
in River Maze, frame by frame, from the new per-cell `src` map:

| frames | source |
|---|---|
| 1 599 - 2 073 | unpainted (`src` 0) |
| 2 079 - 2 147 | a `NoWalk` tile (`src` 2) |
| 2 165 - 3 946 | floor (`src` 3) |

That is the missing fact behind the last three sections. §14.6(a) said a `NoWalk` tile is a wall and
`GetTile == null` was the streaming problem; §14.10 concluded a missing tile is not a wall; §14.11
found rooms that looked sealed. The truth is the row above: **the map is filled with a `NoWalk`
placeholder before the real tiles arrive**, so a `NoWalk` tile is sometimes rock and sometimes the
frontier of a map still loading, and no single reading of the merged window can tell them apart. The
cell above was "inside a wall" for seventy frames and then was the boss's floor.

**(b) River Maze: a 79-cell route between two cells 32 apart.** The boss (`Adzul, River Scourge`) sat
32 units east of the character and the floor that reaches it runs **south, then east, then north** --
exactly the operator's description, in reverse. Over the floor the whole run discovered (3 228 cells),
a breadth-first walk from the character's cell reached a cell **1 unit from the boss in 78 steps**,
and the bounding box of that route (x 514-545, y 561-586) is *inside* the planner's 128-unit window at
the stuck frame. The local planner still could not find it, and the run says why: `nodecap` 1 050 of
2 355 frames (the 1 200-node budget spent before the search finished) and, when given a 20 000-node
budget offline, `unreachable` on 56.6% of frames -- because the route's middle cells were still
`NoWalk` placeholders *at that moment*. The route was not there yet. It arrived as the character moved.

**(c) So the route is over what is KNOWN to be floor, and it is re-planned as the map grows.** New
module `MapRoute.cs`:

* a `byte[]` per map cell -- `1` = a floor tile has been seen here, `0` = nothing is known. A `NoWalk`
  cell, a collision cell and an unpainted cell are all "not known" (the row in (a) is why), so the
  route can never be built through the placeholder the way §14.6 wanted to;
* the adapter already reads every cell of the 128-unit window every 30 frames, so it feeds this cache
  in the same pass (`DodgeAdapter.RefreshGrid`); the cache grows as the character explores and is
  reallocated on a map change, keyed on the grid array's identity exactly as the window is;
* a breadth-first walk (uniform cost, a queue, a 40 000-node budget) from the character to the
  reachable cell **nearest the goal** -- the frontier when the goal is beyond it;
* when the character is *on* that nearest cell already, the route's job becomes "go and look": it
  picks the reachable cell that **touches unknown floor** and is still nearest the goal, because a
  dungeon does not reveal itself to a character that stands still -- the increment in (a) happened
  while the character moved, not while it waited;
* the published goal is a waypoint 6 units along that route, and `GoalKeepWalking` keeps the arrival
  policy from stopping at each one.

**It only takes over a QUEST goal, and only when the local planner could not aim.** The condition is
the previous frame's `PathReason` being `NoTarget`/`Unreachable`/`NodeCap` (or the goal being beyond
80 units), plus a 45-frame hold, because the two goal sources would otherwise alternate every few
frames: the waypoint has a path, the boss's own position does not. An enemy goal is deliberately
excluded -- the farmer stops three units from a mob and lets auto-fire work, and a chain of waypoints
would walk the character through the mob it was meant to shoot. Every safety property is untouched:
this module writes `GoalX`/`GoalY` and nothing else, and the planner, admission, the score and the
escape floor are the same code.

**The verification is on the real run.** `SolverHarness --trace <run> --goal 546.1,563.5` now feeds
the recorded `src` windows into `MapRoute` and walks the result from the recording's first position:

```
map route (trace): map route ON (1400x1400, 3228 floor cell(s), 10 plan(s), 19 waypoint(s), 0 refused);
19 waypoint(s) walked from (513.2,512.0) to (545.5,564.5), 1.166 unit(s) from the goal (arrived)
```

Nineteen waypoints from where the character entered to 1.2 units from `Adzul`, over the floor that
session discovered -- the U the operator described, found by the module. `G4` is the assertion:
a single-width U-corridor with a wall straight between the character and the goal must give a first
waypoint *east along the U*, walking it must arrive, an empty cache must be refused, and a character
standing on the closest known floor must still be given somewhere to go.

**(d) Frost Cavern is the same thing with less map.** The spawn room is where the character stayed
for 1 695 frames and where only **617 floor cells** were ever revealed, the nearest reachable floor to
`Gradvale, Mage of Winter` being **127 units short** of it. The map route will walk it along the
room's frontier and ask for movement, which is the only thing that can reveal more -- whether a
doorway appears is exactly what the next session's `PHASE 3 DODGE MAPROUTE` line will say, together
with `floor cell(s)` (it must grow) and `goal from the route` (it must be non-zero). If `floor cell(s)`
stops growing while the character is moving, the room is genuinely closed and the next answer is not
a route but a decision to leave -- §8.4's nexus, which needs no per-dungeon knowledge.

**The instruments.** `PHASE 3 DODGE MAPROUTE` (once per report): the cache's dimensions, its floor
cells, plans, waypoints and refusals, and the frames the goal came from the route. `Src` and the
per-cell source map in every `G` record (§14.11) are what made (a) visible at all; the trace replay's
`map route (trace)` line is the offline twin.

**§14.13 REPLACES TWO THINGS IN THIS SECTION, AND THE OPERATOR FOUND OUT WHY.** Infested Cellar
(`20261020-183555-p37604`) was stuck in its spawn room for 6 039 frames with the route pacing between
two points: the cache above stored only floor and not-floor, so the room's wall stayed a frontier for
ever, and the target was recomputed from the goal distance every frame. The cache is a three-state
map now (unknown / open / wall, with a `NoWalk` wall revisable), the frontier walk is committed and
spends dead ends, and the trace replay keys its cache per map -- the 3 228-cell line quoted above was
eight maps merged into one cache. See §14.13.

### 14.13 The dungeon that would not be left: a map with walls in it, a committed frontier, and a dead end that is spent

`20261020-183555-p37604` is the run the operator summarised in two sentences:

> Did manage to complete a River Maze although it was a quite easy map (up, right, up).
> Failed in an Infested Cellar, stuck in spawn room, would eventually path up out of spawn room but
> retreat back in. The pathfinder needs to build an internal map inside dungeons to map them out. It
> needs to be able to backtrack if it hits a dead end. All dungeons are always solveable, but there
> might be paths that are dead ends.

**(a) The measurement: 6 039 frames in one room, between two points.** The character enters the
Infested Cellar at `(512.5, 511.5)` on frame 7 251 and the session ends on frame 13 290. Over those
**6 039 frames -- 100 seconds** -- the recorded position stays inside the spawn room and, in the last
4 000 frames, shuttles between **x 507.4 and x 520.6 at y 511.6**. The run's own `PHASE 3 DODGE
MAPROUTE` line reports the cache at **1 054 floor cells** and the plans/refusals climbing 131/79 ->
1 344/1 205. The replay of the module over that recorded map reproduces the cycle exactly: the
published waypoint alternates between `(521.5, 510.5)` -- the room's north-east corner, the reachable
cell nearest the boss at `(563, 506)` -- and `(519.5, 516.5)`, the frontier the old "go and look"
fallback picked once the character was standing on the first.

**The exit was known floor the whole time.** The corridor that leaves the room opens at
`(513..515, 553..555)`. It is in the recorded window from early in the session, and **the character
never went south of y 536**. It did not need to discover anything to leave; it needed a reason to walk
away from the goal.

**(b) Why it retreated: a wall and a hole were the same byte, and a frontier never retired.** The
first cache (§14.12(c)) stored `1` = a floor tile has been seen, `0` = nothing is known. A `NoWalk`
wall and an unpainted cell are both `0`, so **every cell of the spawn room's wall was a frontier for
ever**, and the one nearest the boss was the east wall. "Walk to the boss" and "explore" were the
same instruction, so the character pressed the east wall, drifted to the next-nearest frontier, was
pulled back toward the boss, and repeated that until the session ended. The second half is the goal
gradient itself: the target was recomputed from the goal's distance **every frame**, so nothing the
character did could ever be finished.

**(c) The cache is now a map with three states.** New `MapRoute`:

* `0` **unknown**: nothing has been painted here. Only this is a frontier neighbour;
* `1` **open**: a tile exists and it is walkable. The route is built over these cells and no others;
* `2` **wall**: a nonzero collision byte, or outside the map;
* `3` **wall (tile)**: a `NoWalk` tile -- a wall, but **a placeholder while the map streams**
  (§14.12(a) measured one cell at seventy frames of `NoWalk` before it became the boss's floor). A
  later floor observation overrides it; a floor cell is never overridden back.

The adapter already classifies every cell of its window every refresh (`RefreshGrid`), so it now
hands `MapRoute.Observe` **which case the cell fell into** instead of just "floor or not", in
`TraceRecorder`'s own source codes. A frontier is therefore *an open cell with an unknown
neighbour*, and nothing else: the room's `NoWalk` rock stops being unexplored space. On the
Infested Cellar's recorded map the frontier set collapses from "every wall cell in the window" to
the handful of cells at the corridor's mouth, and the route heads south.

**(d) Commit to a frontier; spend a dead end; backtrack.** Exploration is now a walk of the map:

* the route to the quest goal is taken **whenever the known floor connects to it** -- the question a
  dungeon asks is connectivity, answered by a breadth-first walk from the character, not by a
  distance;
* otherwise a frontier is chosen **by path length** (breadth-first depth), nearest first, with the
  goal's distance only as a tie-break. Nearest-first is what makes a dead end cheap: it is walked,
  spent, and the next choice is generally the junction it came from;
* the choice is **committed**. It is not re-derived from the goal distance every frame -- that is
  the ping-pong in (a) -- and it survives the boss walking, because two units of boss movement do
  not change which corridor has been explored;
* a frontier is **spent** when the character has stood on it and the map did not grow for
  `DwellFrames` (60, one second), or when walking to it took longer than `ExploreTimeoutFrames`
  (300). It is marked tried and never chosen again: the next choice is the nearest untried frontier,
  and from a dead end that **is** the way back. While the character is waiting out the dwell the
  frontier cell itself is published, so it stands still beside the unknown and lets the streamer
  paint -- a dungeon does not reveal itself to a character that keeps walking;
* when no untried frontier is reachable, the component has been mapped and `TryWaypoint` says so
  (`Status()` reads `... frontier left`, `dead end`, `timeout`, `explored`), returning `false` so the
  caller keeps its own goal instead of this module inventing one.

It is still a goal source and nothing else: one point, and the planner, admission, the score, the
hysteresis and the escape floor are untouched.

**(e) The verification, and an instrument that was wrong.** Two new assertions, both on the real
module:

* `G4`'s third block is the Infested Cellar room made undeniable: floor, `NoWalk` walls, the goal
  **east** beyond the wall, and one **unpainted opening to the south**. The first waypoint must be
  south. It is: `13.5,18.5` from `12.5,12.5`. A cache whose non-floor cells are all "unknown" aims
  east at the wall and fails here.
* `G5` is a dungeon walk: `GmTiles`-style reveal around the character, a U with a **blind stub toward
  the goal** at its first junction, and a goal at the far end. The stub must be entered, found
  closed, and abandoned; the goal must be reached; and no cell may be entered more than a handful of
  times. It is: the stub was entered and backtracked, the goal reached in **86 frames**, the
  most-visited cell entered **5** times. The revisit bound is what a per-frame goal-distance retarget
  fails. A dry frontier in an isolated room is also spent (3 dead ends marked, 3 retargets), which
  is the case where standing still would otherwise be for ever.

**AND THE TRACE REPLAY HAD BEEN MEASURING A CHIMERA.** The replay fed every `G` record of a recording
into **one** cache, and a recording holds the lobby, a realm and two or three dungeons at overlapping
coordinates. The line §14.12(c) quoted -- 3 228 floor cells, nineteen waypoints, arrived -- was the
union of eight maps, so some of the floor it routed over belonged to another dungeon. The harness now
keys the cache per map (a teleport of more than 40 units, `--map N` to choose which one) and the
honest numbers are these, both from `20261020-183555-p37604`:

```
map route (trace, map 4): ... 2546 floor cell(s), 4987 wall, 22 frontier left, 10 plan (10 goal),
19 waypoint ... 19 waypoint(s) walked from (511.5,513.5) to (592.5,563.5), 1.503 unit(s) from the
goal (arrived)
map route (trace, map 7): ... 1054 floor cell(s), 2559 wall, 7 frontier left, 4 plan (0 goal),
9 waypoint ... 8 waypoint(s) walked from (512.5,511.5) to (512.5,554.5), 70.235 unit(s) from the
goal (SHORT)
```

The first is the River Maze the operator completed, and **2 546 is exactly the number the live
`PHASE 3 DODGE MAPROUTE` line printed** for that map -- the offline cache and the live one are now
the same cache. The second is the Infested Cellar: from the spawn cell the route walks eight
waypoints **south to the corridor's end at y 554.5** instead of pacing the room at y 511.6, and stops
there because the recorded map has never seen beyond it. In the live game the streamer paints that
corridor as the character walks into it and the frontier moves on; that is what the next session's
line has to show (`floor cell(s)` growing, `dead end(s)` only where the map really ends).

**(f) What is still open.** The route can only spend frontiers it can reach and see; a dungeon that
streams slower than one second per cell costs one dwell per cell. A component with no frontier left
and no route to the marker is a dungeon this module cannot solve, and the answer there is not a route
but the decision to leave (§8.4's nexus) -- deliberately still unwritten. And the only proof that
matters is the next run: one Infested Cellar session whose `MAPROUTE` line shows the floor growing
while the character is moving, and a boss reached.
