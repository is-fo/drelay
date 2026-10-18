# Handoff — recording a route from the proxy↔hook bridge

**Why this is a handoff rather than code.** The operator's request after the second live farm
session: *"I want to be able to record a route, but put that in a handoff, the best time to implement
that is when the PROXY↔HOOK bridge exists."* This document is that handoff. It records what already
exists, what the bridge changes, and what is left to build — so the work can start the day the bridge
lands without re-deriving any of it.

Read with: [`PLAN-AUTOFARM.md`](PLAN-AUTOFARM.md) §7 (the routes, row 6, built), §14 (the two live
sessions and the roam fallback that stands in for a route today),
[`PLAN-HOOK-CONTROL.md`](../bridge/PLAN-HOOK-CONTROL.md) (the control channel this depends on),
[`JAVA-BRIDGE.md`](../bridge/JAVA-BRIDGE.md).

---

## 1. The problem a route solves, in the operator's words

- *"when entering the world it's stuck inside a small safe haven, it has to try and breach out of
  there"* — the realm's spawn is an enclosure, and the farmer's first goal is "an enemy within 8
  units", of which there are none inside it;
- *"when there's no enemies on screen (which happens often since it kills them), it has no where to
  pathfind"* — `TRAVEL`, with no answer until a route says where to go next;
- so the real fix is a recorded walk: spawn → bottom of the realm → loop through the packs, replayed
  with the deviation and the obstacle avoidance §7.2–§7.4 already implement.

**What ships today is a stopgap and is named as one.** When no target is in range the farmer
publishes a roam waypoint (`DodgeSnapshot.GoalKeepWalking`, `FarmTravelRadius` 25 units) and rotates
its direction — south first, then the cardinals and diagonals — when it is reached or not reached in
five seconds. It gets the character out of the haven and keeps it moving between packs. It is not a
route: it does not know where the farming is, it cannot be recorded, and it cannot be reproduced.

---

## 2. What already exists (row 6, `b0734e9` / the routes commit)

| piece | file | state |
|---|---|---|
| the file format, the map validity block, Douglas–Peucker, the resample, the snap | `managed/Drelay.Bootstrap/Route.cs` | built, pure, R1/R2 green |
| the follower, the bounded smooth deviation, the lap seed | `managed/Drelay.Bootstrap/RouteFollower.cs` | built, pure, R3/R4/R5 green |
| the live half — `World.Shared`, `Player.Position`, `MapInfo`, the recorder's sampling | `managed/Drelay.Bootstrap/RouteRuntime.cs` | built |
| the switches | `DRELAY_ROUTE_RECORD=<name>`, `DRELAY_DODGE_ROUTE=<name>` | built, launcher-unaware |
| the fixture suite | `hook/tools/SolverHarness` R1–R5 | green on every run |

Two things about that are worth restating because they constrain the bridge work:

1. **The recorder writes every twenty samples, not at close.** `TraceRecorder.Close` is never called —
   the native side owns the recording window — so a route written only at close is a route that never
   exists. Twenty samples at 10 Hz is two seconds; an abruptly-ended session keeps the walk up to that
   moment. A bridge-started recording inherits this and does not need a new flush rule.
2. **A route is a PREFERENCE path, never an admission filter.** The follower publishes a point goal,
   the solver still chooses from the admissible set, so a route that walks through a bullet is not
   followed into the bullet. Nothing in the bridge work may change that — a "play route" command must
   not become a mode that overrides the dodge.

The one thing the routes do **not** have is a way to start and stop a recording in a live session.
Today it is an environment variable read at launch, so naming and re-recording a route means
relaunching the game. That is the gap the bridge fills.

---

## 3. What the bridge changes

`PLAN-HOOK-CONTROL.md` (items 3a/3b) is the design: a command channel from the relay to the hook,
plus the dashboard panel that writes to it. The route work needs three verbs and one query, in the
same shape as the plan's existing settings commands:

| command | effect | notes |
|---|---|---|
| `route.record.start { "name": "realm-loop" }` | begin sampling into `routes/<name>.json` | refuses a name that already exists unless `"overwrite": true`; the file is written every twenty samples as today |
| `route.record.stop` | finish the current recording | the last partial file is valid; no close handshake is needed for correctness |
| `route.play { "name": "realm-loop" }` | load and follow, exactly as `DRELAY_DODGE_ROUTE` does | refused by name when the map block does not match the current map (R2's rule) |
| `route.stop` | stop following; the farmer returns to target-or-roam | |
| `route.list` | the route files and their map blocks | for the dashboard |

**Every one of them has a launch-time default already** (`DRELAY_ROUTE_RECORD`,
`DRELAY_DODGE_ROUTE`), which is `PLAN-AUTOFARM.md` §9's "the bridge is not load-bearing" invariant:
the relay can be killed mid-session and the route the operator started keeps recording.

**Where the files live** is the one open decision. Today: `%LOCALAPPDATA%\drelay\hook\routes\`. The
bridge makes two other placements plausible — the run directory (so a route is evidence beside
`worlds.jsonl`), or the relay's own storage (so the dashboard can show it and a route can be shared
between sessions). The recommendation is: **keep the hook's location as the recording target**
(the hook wrote the file and can validate the map block without a round trip), and let the relay
*read* it for display. Moving the write to the relay would make the bridge load-bearing for a
feature that already works without it.

**Nothing new is needed on the frame path.** The recorder is already sampled from the managed frame
replacement's sensor path; starting and stopping it is a flag flip and a file open/close, both of
which may happen on the frame the command is drained. Those are rare, so they may allocate — the
no-allocation rule applies to per-frame work, not to a command.

---

## 4. What the bridge must have before this work starts

- **3a: the command channel.** A way for the relay to put a small command into the shared block and
  for the hook to drain it once per frame, with the state machine `PLAN-HOOK-CONTROL.md` describes.
  Routes need no new transport; they need the transport that plan is building.
- **3b: the launch surface for the bridge itself** (`--config`/`--bridge`), so a session that will be
  driven from the dashboard is started deliberately rather than by an inherited variable.
- the dashboard panel, so `route.record.start` has a button. Without it the verbs are still usable
  from the relay console, which is enough to test them.

Until 3a lands, the launch-time variables remain the only surface, and the operator's workflow is:
record in one session, replay in the next. That is usable today and is why the route feature is not
blocked on the bridge — it is only *awkward* without it.

---

## 5. The order to build it in, and what to check

1. **A command handler in the hook** that understands the four verbs and calls the existing
   `RouteRuntime` entry points. No new route logic: `Record(name)`, `StopRecording()`,
   `Follow(name)`, `StopFollowing()` already exist or are one-line wrappers around what
   `RouteRuntime` does from the environment.
2. **`LauncherHarness` C11** — the same shape as C10 for `--autofarm`: the command is a name, the
   implication is the recorder, and nothing is written when it is not asked for.
3. **A bridge-level test** in the relay's harness: a command put into the block is drained once, and
   a malformed one is refused by name rather than dropped.
4. **The replay half needs no change** and should not get one. `DRELAY_DODGE_ROUTE` already proves
   the follower; the bridge only changes who says the name.
5. **A live session**: record the spawn→farming-area walk, check the file's map block, replay it in a
   fresh session, and confirm the character leaves the haven without the roam fallback. That is the
   one measurement that says the route feature is done.

**The open measurement** the routes still owe, and the bridge makes cheap: **what the recorder
should do with a teleport.** The 2026-10-18 session measured a 1 219-unit jump on a `CanTeleport`
realm, and a recorder that treats every pair of samples as a step records the teleport as one. Today
the resampler would emit a single 1 219-unit segment and the follower would walk it. The bridge does
not fix that; a distance guard in the recorder would (drop a sample whose step exceeds, say, 30
units, and start a new segment). It is worth doing in the same change, and it is the kind of thing
that should be measured on a recorded teleport rather than assumed.
