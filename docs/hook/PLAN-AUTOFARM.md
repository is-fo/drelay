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

### 12.1 Row 2 (§4) is in — `a38de9d`, "the farmer walks"

`Farmer.cs` (new), plus `DodgeSnapshot`, `DodgeSolver`, `PathPlanner`, `EnemyReader` and
`DodgeAdapter`. `SteeringPilot.cs` is untouched, exactly as §10.1's file-ownership table predicted,
because the farm writes no input: the client's own auto-fire and the existing auto-aim module do the
shooting (§3).

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

### 12.5 Row 3 (§6) is in — `68233d7`, "the world log"

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



