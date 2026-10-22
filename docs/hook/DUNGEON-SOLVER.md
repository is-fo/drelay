# The dungeon solver — a map built from `GmTiles`, a committed frontier walk, and a dead end that is spent

**Status: built and offline-verified. Not run in the live client.** Written 2026-10-20 on branch
`feat/dungeon-solver`, replacing the earlier `MapRoute.cs` attempt. This is the record of the
rewrite: what the old module got wrong, what the new one is, and which checks hold it.

Read with: [`PLAN-AUTOFARM.md`](PLAN-AUTOFARM.md) §8 (the dungeon work in the farmer plan) and
§14.6–14.13 (the measurements that forced each rule). The operator's specification is in §14.13:

> The pathfinder needs to build an internal map inside dungeons to map them out. It needs to be able
> to backtrack if it hits a dead end. All dungeons are always solveable, but there might be paths
> that are dead ends.

---

## 0. The answer in one screen

```
DodgeAdapter.RefreshGrid (every 30 frames, and on a grid/map change)
    classify each window cell: out-of-map | collision | NoWalk tile | floor tile | no tile
    └─ DungeonMap.Observe(x, y, source)                  <- the only place knowledge enters

DodgeAdapter.FillSnapshot (every frame, farm run, quest goal, planner blind)
    └─ DungeonSolver.TryWaypoint(player, goal, out waypoint)
         ├─ goal is reachable over known floor?  -> route to it
         ├─ a reachable floor cell is within 3   -> route to that (marker on a wall/occupied tile)
         ├─ optimistic A*: cross NoWalk placeholders toward the goal -> walk to the first wall
         │     └─ tile converts? continue : mark that wall blocked and recompute
         ├─ a hard frontier (floor beside unknown) exists -> committed frontier walk
         └─ otherwise                            -> boundary probe (floor beside a NoWalk wall)
    └─ snapshot.GoalX/Y = waypoint; GoalKeepWalking = true
```

The solver writes **one point and nothing else**. The planner, admission, the score, the hysteresis
and the escape floor are untouched: a dungeon narrows *where* the character walks, never *whether* a
step is safe. That is the same contract `Farmer`, `RouteRuntime` and the old `MapRoute` had, and it
is the reason the safety invariants did not need to change.

---

## 1. Why the old module was thrown away

`MapRoute.cs` was one static class holding the map, the route, the commitment flags, the search
scratch and the counters. Three defects came out of the field:

1. **State was global.** Two maps could not coexist in one process or one test; a harness run that
   fed several maps merged them into one cache (measured: the "3 228 floor cells, nineteen
   waypoints, arrived" line was the union of eight maps). The old trace replay had to invent a
   per-map epoch to work around it.
2. **Walls and holes were the same byte.** The first cache stored floor / not-floor, so every wall
   cell in a room was a frontier for ever, and the room's east wall — nearest the boss — was the
   target. That is the Infested Cellar's 6 039-frame pace between two points.
3. **The target was re-derived from the goal distance.** Even after a frontier was chosen, the next
   frame could choose another one, because the commitment was not a state transition. The result
   was two answers alternating frame by frame.

The rewrite draws the line where the plan should have drawn it: **`DungeonMap` is knowledge and
geometry; `DungeonSolver` is policy and memory.** Both are instance classes, both are pure of any
game accessor, and both compile into `SolverHarness` with no client in sight.

---

## 2. `DungeonMap` — four states, and the third one is the dungeon

| state | meaning | may it be walked on? |
|---|---|---|
| `Unknown` (0) | nothing has been painted here | no; it is what a frontier is made of |
| `Floor` (1) | a tile exists and is walkable | yes, and the only state a route uses |
| `Wall` (2) | nonzero collision byte, or outside the map | no, permanently |
| `ProvisionalWall` (3) | a `NoWalk` tile | no, but a later floor tile **may** override it |

The third state is a measurement, not a guess. In River Maze one boss-room cell read `NoWalk` for
seventy frames and then became the boss's floor (`20261020-181445-p4196`). A cache that treats
`NoWalk` as permanent closes a room that is still arriving; a cache that treats it as unknown turns
a real wall into a frontier and the character presses it. So:

- **floor is final** — the streamer does not turn a walkable tile into a wall;
- **a hard wall is final** — the server's nonzero collision byte does not become floor;
- **`NoWalk` is provisional** — a later floor observation overrides it;
- **an absent tile is no information** — `GetTile` returns null for anything the streamer has not
  reached, and reading that null as a wall is what sealed every dungeon in the 2026-10-20 sessions.

The source codes are the trace recorder's own (`0` unpainted, `1` collision, `2` `NoWalk`, `3`
floor, `4` outside). That is deliberate: `SolverHarness` feeds a recorded `src` window into the same
`Observe` the adapter calls, so the replay is the live classification and not an interpretation of
it. The alphabet lives in `TraceRecorder`'s comment and in `DungeonMap`'s constants; a drift between
them is caught by `D1`.

**Identity and bounds.** `Bind(array, width, height)` is keyed on the collision grid's **array
reference plus its dimensions**. A new `byte[,]` is a new world (`World.ClearWorld` allocates one),
so a route from the old map cannot survive a portal. A map over `MaxCells` (2 000 000) is refused by
name rather than allocated from a varint; a 2 698 × 2 634 realm (7.1 M cells) is the measured case
that must be refused, and a 1 024 × 1 024 dungeon (1.05 M) fits.

**Search.** One breadth-first walk over `Floor` cells answers three questions in a single pass:

- is the goal's own cell reachable? Then the route is the parent chain to it;
- if not, which untried frontier is nearest **by path length**? The frontier list is collected
  during the walk, so no second scan is needed;
- which reachable floor cell is nearest the goal? The solver uses this only to decide that a marker
  sitting on a wall or an occupied tile is "a wall away" rather than "another room away".

A frontier is *a floor cell with a 4-neighbour that has never been painted*. A `NoWalk` wall is not
a frontier. That single definition is what retired the Infested Cellar's east wall. The node budget
is 40 000; the budget is a frame-cost bound, and the best frontier found so far is still a useful
direction when it runs out.

**Boundary probes are the other half.** A live dungeon defeats the frontier rule completely: the
streamer paints the whole window `NoWalk` before the real tiles arrive, so a room can have **zero
unknown neighbours** and still have two doorways. A boundary is *a floor cell with a 4-neighbour that
is a `ProvisionalWall`*. The search collects these too, and when no hard frontier is reachable the
solver probes them. The **first** probe is goal-directed, with the exit-corridor signal (a
disconnected floor patch within eight cells) as its filter; after that the sequence **sweeps the
wall**: the next probe is the nearest unspent boundary cell to the player, with the goal as the
tie-break. That is what turns a room's boundary from N independent cross-room walks into one
perimeter pass. §8 records the live measurements behind the order.

**Optimistic planning is the mouse's move.** Before any frontier or boundary walk, the solver runs
A* over `Floor` plus `ProvisionalWall`, with a provisional cell costing `ProvisionalCost` (40)
instead of being impassable. The search returns the first provisional wall on the shortest route to
the goal and the floor cell just before it. The character walks to that cell and waits: if the tile
converts, the route opens; if not, that wall cell is marked **blocked** and the search runs again
around it. Hard walls and unknown cells are never traversed. This is what makes the boss known
position actually drive the route, instead of the solver sweeping every wall in the room; see §8.3
for the live run that forced it.

---

## 3. `DungeonSolver` — the state machine

### 3.1 Modes

| mode | entered when | route ends at |
|---|---|---|
| `Goal` | the goal, or a floor cell within 3 units of it, is reachable over known floor | the goal cell |
| `Explore` | the goal is not connected yet | the committed frontier, or the committed boundary probe |
| `None` | no route is held, or the map is fully explored | — |

`Mapped` is a flag, not a mode: it is set when a search finds no untried frontier and the goal is
not on the known floor. `TryWaypoint` then returns `false`, and the caller keeps its own goal
instead of the solver inventing a waypoint on top of the character.

### 3.2 The commitment, and why it is a real state transition

When `Explore` starts, one frontier is chosen and stored. It is not re-chosen while:

- the character is walking toward it;
- the goal moves (a boss that walks two units does not change which corridor is unexplored);
- the map grows somewhere else.

The route to it is recomputed when the plan is stale (30 frames), when the character steps more than
6 units off it, or when the map resolves the frontier. **The choice is the state; the path is a
derived value.** That split is the fix for the two-point ping-pong.

The frontier is chosen **nearest first by BFS depth**, with distance to the goal only as a
tie-break. Nearest-first is what makes a dead end cheap: a stub is entered, spent, and the next
choice is generally the junction it came from. A goal-biased choice would pick the east wall again.

### 3.3 How a dead end is spent (this is the backtrack)

A frontier leaves the committed state in exactly four ways, and each is counted separately:

| way | signal | counter |
|---|---|---|
| the map **opened** at it | when it stops being a frontier, a floor neighbour other than the way in | `explored` |
| the map **closed** at it | it stops being a frontier, but the only floor neighbour is the way in | `dead end` |
| the character stood on it and nothing changed | `DwellFrames` (60) with no version growth | `dead end` |
| a boundary probe revealed nothing | `BoundaryDwellFrames` (75) at a `NoWalk` edge with no growth; its nearby boundary cells are spent with it | `dead end` |
| the character could not reach it | `ExploreTimeoutFrames` (300) | `timeout` |
| an optimistic wall did not convert | 75 frames at the floor cell before it; the wall cell is `MarkBlocked` | `dead end` |

A blocked wall is not a `Tried` cell: it is a wall the mouse has bumped into. A later floor
observation clears the block, so a slow streamer can still turn it into a doorway.

The second row is the distinction the old cache could not make: a stub whose end is painted `NoWalk`
is *closed*, not *explored*. It is marked `Tried`, and `Tried` cells are never chosen as a frontier
again. The next plan therefore chooses the nearest untried frontier, which, from the end of a dead
end, is behind the character; the BFS route goes back through the junction. That is the backtrack,
and it needs no special case in the follower.

`dead end` and `timeout` are deliberately separate: the first says the map ended there, the second
says the character did not get there. They look identical in `refused`, and they need different
answers.

### 3.4 Standing still is part of the algorithm

While the character is on the committed frontier and it has not resolved, the solver publishes that
same cell. The existing arrival policy stands there, and the streamer paints. A dungeon does not
reveal itself to a character that keeps walking; `DwellFrames` is the measured one second that gives
a cell time to arrive. Publishing the frontier itself (rather than "wait") is what keeps the whole
contract one point.

### 3.5 The goal

The goal point is the client's own quest/boss marker, resolved by `QuestReader` (§14.9 of the
plan): `World.Quest`, `World.EventQuests`, or an object whose `EntityData.Quest` /
`EntityData.EventQuest` flag is set. `QuestReader` already refuses a `(0,0)` marker, a non-finite
position, and a marker that is not in `World.Objects` (a `World.Quest` that survived a map change).
The solver does not re-resolve the marker and does not need to know whether it is a boss, an NPC or
a board: it is a point, and the map answers whether a way to it exists.

A marker whose own cell is `Wall` or `ProvisionalWall` is not a dead end. If a reachable floor cell
is within 3 units of it, the solver routes there and the existing arrival policy covers the last few
units; this is the same rule the planner's point-goal snap uses.

---

## 4. Integration

- `DodgeAdapter.RefreshGrid` binds and feeds the map only on a farm run (`_config.FarmMode`). The
  binding identity is the same `byte[,]` and dimensions the window uses.
- `DodgeAdapter.FillSnapshot` asks for a waypoint only when the frame carries a **quest goal**
  (`s.GoalIsQuest`), the goal is more than 10 units away, the solver has a map, and either the last
  planner answer was blind (`NoTarget` / `Unreachable` / `NodeCap`) or the goal is beyond 80 units.
  A 45-frame hold stops the two goal sources alternating (the waypoint has a path; the boss's own
  position may not).
- The published goal sets `GoalKeepWalking = true`, so the farm arrival policy does not stop at
  each waypoint.
- **Enemy goals are deliberately excluded.** A farmer stops three units from a mob and lets
  auto-fire work; a chain of waypoints would walk the character through the mob it was meant to
  shoot.
- The report line is `PHASE 3 DODGE DUNGEON | <Status()> | goal from the dungeon <n> frame(s)`.
  `floor` must grow while the character is moving; `frontier` is what says whether there is still
  somewhere to go; `dead end` and `backtrack` are the operator's requested behaviour, counted.
- Arm-time warming covers `TryWaypoint`, `Observe`, `Bind`, `Status` and `DungeonMap.Observe`, so a
  first call cannot be JIT-compiled inside a patched game frame.

---

## 5. What the offline checks assert

They run on **every** `SolverHarness` invocation, including the short run in
`Test-HookOffline.ps1`, so they cannot be forgotten.

| # | check | result |
|---|---|---|
| D1 | the four cell states; floor is final, a hard wall is final, `NoWalk` upgrades, an absent tile is unknown; the same array keeps the map, a new array clears it, an over-cap map is refused by name. | PASS |
| D2 | a U-corridor whose goal is behind the wall is walked the long way round; the first waypoint goes east along the U, not south into the wall; the walk arrives. | PASS: 7 goal plans, 166 steps |
| D3 | a blind stub toward the goal is entered, found **closed**, marked a dead end, backtracked out of, and the goal beyond it reached; the most-visited cell is bound (5). | PASS: 1 dead end, 1 backtrack, goal in 86 frames, max 5 visits |
| D4 | a room with one entrance is left through that entrance and the goal beyond its wall is reached. | PASS: left the room, goal in 88 frames |
| D5 | state is per map: a new array starts empty, an empty map refuses rather than inventing a route, counters survive a rebind, `Reset` clears everything. | PASS |
| D6 | a room whose entire boundary is a `NoWalk` placeholder, with two doorways left as placeholders, is probed at the boundary, the door converts, and the goal beyond is reached. | PASS: boundary probe and arrival |
| D7 | a REAL `NoWalk` wall on the direct goal line is planned through optimistically, bumped, blocked, and the route recomputed around it through a placeholder door. | PASS: all 10 wall rows blocked, goal reached, first waypoint east |

The trace replay (`--trace <run>/trace.jsonl --goal X,Y [--map N]`) now feeds recorded `src` windows
into `DungeonMap` and walks the result. It prints the measurement (`dungeon (trace, map N): ...`) and
is not a pass/fail check: whether a route exists is a property of how much map the session saw.

**What the checks do not say.** They say the solver maps, spends and backtracks over a synthetic
streamer. They do not say the live `World.Tilemap` is readable at the frame tick, that the map's
dimensions resolve, or that a real dungeon's boss marker is placed where `QuestReader` reads it.
Those are the next session's `PHASE 3 DODGE DUNGEON` line.

---

## 6. Limits, stated rather than hidden

- **The map is only what the window has read.** `GmTiles` streams; `DungeonMap` can route only over
  cells that have arrived as floor. A map that streams slower than 60 frames per cell costs one
  dwell per cell.
- **The search is a 4-neighbour BFS.** That is the map's own move model; diagonal moves are the
  planner's job, and this module only publishes a point.
- **The budget is finite.** A dungeon larger than 40 000 nodes per plan returns the best frontier it
  found; the next plan continues. The budget is per plan, not per session.
- **A fully mapped component with no route to the marker returns `false`.** The honest answer is
  "this map cannot reach it", and the decision then is to leave (§8.4's nexus), which is not part
  of this module.
- **No live run.** Every claim above is an offline check or a replay of a recording. The next live
  session's dungeon line is the evidence that matters.

---

## 7. Where the old names went

`MapRoute.cs` is deleted. The harness's `ReportMapRoute`/`ReportDungeonWalk` were replaced by
`ReportDungeonMap`, `ReportDungeonRoute`, `ReportDungeonWalk`, `ReportDungeonRoom`,
`ReportDungeonState` and `ReportDungeonSealedRoom`. `DodgeSnapshot`/`QuestReader`/`TraceRecorder`/
`WorldLogVerify` are unchanged: the quest reader, the tile half of walkability and the trace's `src`
instrument are reused because they were measured and correct; what was rewritten is the dungeon's
state.

---

## 8. The first live dungeon run: sealed by placeholders

`20261020-223237-p14412` is the session the operator described as “got stuck walking in circles
inside a room” in **Chasm of Dread**, and “stuck in spawn room walking up and down” in **River Maze**.
The run used this module. The evidence is in `worlds.jsonl`, the `trace.jsonl` `src` windows, and
`PHASE 3 DODGE DUNGEON`.

**What the line said.** At the end of the River Maze segment:

```
dungeon map ON (1024x1024, 518 floor, 87 wall, 2102 provisional, 17 frontier (mapped),
                 6993 plan (0 goal), 485 waypoint, 6950 refused, 1 dead end, 0 timeout,
                 0 backtrack, 39 explored)
```

`(mapped)` was the bug. The solver had **3–17 frontiers**, but every one was outside the player's
component. Plans and refusals climbed while waypoints stayed at 478 → 485: the character was not
being given a goal at all.

**What the map said.** Decoding the last River Maze `G` record and flood-filling from the player:
the component was **424 floor cells**, and its neighbours were **0 unknown, 68 collision, 78
`NoWalk`** — and **0 other floor**. The room was not a dead end. It was a room whose two doorways
were still `NoWalk` placeholders; the real corridor floor was visible on the other side of the
placeholder (94 disconnected floor cells in the same window), but the wall between them had not been
painted yet. The same signature is in Chasm of Dread (2 044 floor, 3 828 provisional, 3 frontiers,
8 plans).

**Why the old rule could not see it.** A hard frontier is “floor beside unknown”. Here there was no
unknown: the streamer had painted the entire window `NoWalk`. The definition that retired the
Infested Cellar's east wall therefore retired the only signal that could find the exit.

**The fix, and why it is not a random wall sweep.** The search now also collects **boundary cells**:
floor cells beside a `ProvisionalWall`. When no hard frontier is reachable, the solver probes one.
The probe is ranked **goal distance first**, with the **disconnected floor** signal as a filter and
tie-break — the corridor on the other side of a doorway placeholder is already floor in the same
window. A probe dwells `BoundaryDwellFrames` (75), then marks itself `Tried`; if the door converts,
`Observe` turns the placeholder into floor, the map connects, and the goal route takes over.

### 8.1 The second live run: the component was large, and the probe order was wrong

`20261020-224816-p12992` is the Infested Cellar run the operator described as “never progresses past
the room with the intersection”. It used the first boundary build (30-frame probe, outside-floor
ranking). The last window:

- player component: **1 108 floor cells**, bounds x 486–539, y 503–567
- neighbours: **0 unknown**, 352 collision, 126 `NoWalk`, **0 other floor**
- status over the segment: floor **994 → 1 139**, `edge` **102 → 79**, plans **171 → 348**,
  dead ends **25**, timeouts **6**, backtracks **15**
- position over 6 239 frames: a cycle between y ≈ 503–516 and y ≈ 530–550, 63 visits to the
  intersection cell — not a dead end, but no progress either

The per-cell transitions over that window are the important part: **601 cells went `2 -> 3`**
(`NoWalk` placeholder → floor) and **zero went `1 -> 3`** (collision → floor). So the doorways are
indeed `NoWalk` placeholders and they do convert; the streamer was working. The solver was not
finishing the experiment: the measured placeholder-to-floor delay is up to **70 frames**, and the
30-frame probe left before the tile arrived.

Two selection defects made it worse:

1. **The frontier rank was nearest-by-path-depth.** In a 1 108-cell component that explores the whole
   south half first, then cycles. It is now **goal distance first**, path depth as the tie-break; the
   commitment and tried set still prevent the old ping-pong.
2. **The boundary rank was outside-floor first.** The strongest outside-floor patches were at the
   spawn end, so every probe walked the character back north. The first probe is now goal-directed;
   after that the rank is **local**: the next probe is the nearest unspent boundary cell to the
   player, with the goal as a tiny tie-break. The second Infested Cellar spent roughly **400 frames
   per probe**, mostly crossing a large room to the next boundary cell; a perimeter sweep is the same
   experiment without the travel.

The probe also spends its **neighbourhood** on failure (`BoundarySpendRadius` = 2): if 75 frames at
one boundary cell did not open the wall, the cell next to it is the same experiment.

The static trace replay of the latest map now walks toward the goal side of the intersection instead
of the spawn-room wall:

```
dungeon (trace, map 11): ... 1 192 floor, 315 wall, 3 383 provisional, 0 frontier, 103 edge,
    1 plan (0 goal), 5 waypoint ... 4 waypoint(s) walked from (512.5,510.5) to (521.5,510.5)
```

The replay is static: the recorded map never gains the door cell, so it stops at the probe. What it
shows is the direction — east/north toward the marker, not back to the spawn. The live streamer is
what turns the probed placeholder into floor.

**Offline fixture.** `D6` is the room made undeniable: all 2 500 cells of the window pre-painted
`NoWalk`, a ten-by-ten floor room, two exits whose door cells are placeholders, and a reveal function
that turns a door into floor when the character stands near it. The goal is south; the east exit is a
decoy. The check passes, and it fails on the pre-boundary build at the first `TryWaypoint`.

**Report line change.** `Status()` now carries `edge` beside `frontier`. `edge` is the number of
unspent boundary cells: a non-zero `edge` on a run that reports `(mapped)` is no longer possible, and
a run whose `floor` and `edge` both stop changing is the one that is genuinely finished. The live
line reads, e.g. `... 0 frontier, 79 edge ...` while the room is still being probed.

**What the next run must show.** `edge` falling while `floor` grows at the doorways; a probe that
converts should open a route to the marker. If `edge` falls to 0 with the boss still unreachable, the
component really is sealed and the answer is the exit decision (§8.4), not another probe. If it still
cycles between the same two ends, the line's `dead end` and `backtrack` counts will say so.

### 8.2 The third live run: both bosses reached, and the spawn room is the remaining cost

`20261020-230932-p36548` has two Infested Cellars in a row; both reached the boss room. The
operator's remaining complaint is speed: the character spends a long time discovering the spawn
room.

The trace agrees. The first visit gets more than 25 units from spawn at frame 7 639 but spends
roughly frames 8 409-10 408 within ~10 units of it. The second leaves briefly at frame 14 546,
returns, and stays within ~10 units until about frame 18 158 -- roughly **3 600 frames** -- before
the route to the boss opens. The `DUNGEON` line shows ~73 `edge` with only 15 dead ends and 11
timeouts over 8 000 frames: about **400 frames per probe**, of which the 75-frame dwell is only a
fifth. Most of it was travelling between boundary cells on opposite sides of a large component,
because the probe rank was goal-first and the goal was far away.

The fix is the **local sweep**: the first probe stays goal-directed, then the next is the nearest
unspent boundary cell to the player, with the goal as a tie-break. The character now walks the
room's perimeter instead of crossing it. With `BoundarySpendRadius` = 2, a wall costs a handful of
probes. The inherent limit remains: **tiles only arrive near the character**, so the exit cannot be
known until the character is near it; it does not need to collide with the wall, only to walk past
it. A perimeter pass is the cheapest way to do that.

**The next run's measurement** is the same `DUNGEON` line: for the same room, the frames per probe
should collapse and `edge` should fall without the position cycling between the room's two ends.

### 8.3 The fourth live run: the optimistic planner

`20261020-233845-p12720` is the run the operator called *"painfully slow"*: one Infested Cellar
from frame 2 885 to 16 535. The status tells the whole story:

- floor grows 1 047 -> 1 682 by about frame 4 002, then sits at **1 682 for roughly 7 000 frames**;
- `edge` falls 99 -> 33, plans rise 18 -> 333, dead ends 18, timeouts 14;
- the first goal plan does not appear until frame **11 602** -- roughly 8 700 frames after entry.

The exact search can only answer "is there a way over floor?"; when the boss is across a doorway
placeholder the answer is no, so every frame went to a frontier or a boundary probe. The boss's
position was known the whole time; **the route was not**, because the map deliberately refuses to
route over `NoWalk`.

The fix is the optimistic A* above. It treats `NoWalk` as a possible doorway at cost 40, finds the
shortest route toward the boss, and returns the first wall on it. The character walks to that wall;
the tile either converts (route continues) or the wall is blocked and the next route goes around.
That is the loop a maze-solving mouse runs, and it uses the boss position as the objective rather
than a tie-break.

`D7` is the offline proof: a real wall straight east of the player and a doorway south. The first
waypoint is **east** (18.5, 13.5), all **10 wall rows are blocked**, the route re-plans through the
south door, and the goal is reached in 1 052 frames. Without optimistic planning this is a perimeter
sweep; without blocking the wall the same cell is retried for ever.

**What the next run must show.** The `DUNGEON` line's `dead end` will rise in bursts as real walls
are bumped, and `floor` should grow shortly after each burst as a doorway converts. The time spent
near the spawn room should no longer be dominated by walking its perimeter. If a boss is behind a
**collision** wall (not a placeholder), no amount of proximity will open it; the line will show
blocked walls with no floor growth, and the answer is the exit decision, not another probe.

### 8.4 The fifth live run: the probe was not being asked

`20261020-235250-p33652` is two Infested Cellars. The first eventually reached the boss (goal plans
start at frame 12 047, after about 8 500 frames); the second was aborted after ~1 500 frames, with
the character sitting in a 3x3 space around **(521, 515)** for over a thousand frames.

The second run's counters are the signature: `dead end` only moved 29 -> 31 and `timeout` 13 -> 14
in those 1 500 frames, while the status showed 43 unspent boundary cells. The solver was not probing
once per 75 frames; it was barely being asked at all.

The cause is in the adapter, not the map. The dungeon solver was called only while the local
planner's answer was one of the blind reasons (`NoTarget`/`Unreachable`/`NodeCap`) or during a
45-frame hold. Once the solver published the **floor cell before a wall**, the local planner had a
perfectly good route to that cell -- so the planner was no longer blind, the hold expired, and the
solver stopped being called mid-dwell. The dwell never reached 75 frames, so the wall was never
converted or blocked, and the character stood at the same spot. The planner was working; the probe
was not being asked to finish.

The fix is `DungeonSolver.HasActiveProbe`: the adapter now keeps asking the dungeon solver while a
probe is committed, even when the local planner has a route. The hold is also raised from 45 to 120
frames, so a published waypoint survives longer than one dwell. A wall that fails its dwell now
blocks its **neighbourhood** (`BlockedRadius` = 2), so a solid wall costs a handful of probes rather
than one per cell.

**The live test is the second-cellar shape**: while the character stands at a wall, `dead end` or
`timeout` must advance at least once per ~75 frames, and a converted tile must show up as floor
growth. A frozen counter beside a stationary position is this integration bug, not a map that cannot
be solved.

### 8.5 The sixth live run: why the spawn room is slow and the rest is fast

`20261021-000135-p35056` is Infested Cellar twice and Frost Cavern. Both Cellars reached the boss.
The operator's observation is exact: the spawn room takes a long time, then the rest of the route is
efficient.

That asymmetry is **information, not pathing**. At entry the map knows the spawn room and almost
nothing else: the boss is outside the room, every connection to it is still a `NoWalk` placeholder,
and there is no floor path. The solver must find a doorway by walking to wall boundaries and letting
the streamer paint; each failed candidate costs `BoundaryDwellFrames` (75) plus the walk. Once a
doorway converts, the next corridor's floor arrives, the map connects, and the remaining route is
mostly known floor -- which is why it then looks like a near-one-shot.

The run also exposed an ordering mistake: the optimistic wall plan was tried **before hard
frontiers**, so a room with real unpainted edges was wall-probed first. Hard frontiers are cheaper
and are the direction the streamer is already revealing; they are now chosen first, and the
optimistic plan is the fallback for a room whose every edge is already painted.

The optimistic search also now prices a provisional wall at `DoorwayCost` (2) when it has
**disconnected floor on its far side** -- the corridor seen through a doorway -- and at
`ProvisionalCost` (40) otherwise. This is the same exit-corridor signal the boundary probe uses, and
it stops a solid wall from being tested ahead of an obvious opening.

**What should change in the next run**: floor and frontier should advance while the character is
still in the spawn room, because real unknown edges are walked first. The unavoidable part remains:
a doorway that has never been streamed cannot be known, so the character still has to walk near the
room's boundary at least once. The aim is one perimeter pass, not a wall-cell-by-wall-cell probe.
