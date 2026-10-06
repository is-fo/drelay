# Implementation plan

This is the deliverable: **what to build, in what order, and how to know each step worked.**

The supporting research is [CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) (the client, verified),
[TIMING.md](TIMING.md) (the measured window), [CLIENT-MAP.md](CLIENT-MAP.md) (the exhaustive
reverse-engineering map), [HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) (how the hook is installed),
[DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) (what it computes), [JAVA-BRIDGE.md](JAVA-BRIDGE.md) (how it
reports) and [RISKS.md](RISKS.md).

---

## 0. The shape of the answer, up front

The premise the work started from was *"what if the auto-nexus never needed to be triggered?"* The
research says that premise is **half right, and the useful half is not the half it looks like.**

**What is true.** A hook placed at the client's own collision decision occupies a window the reactive
rule structurally cannot. Measured: the server's `GmHealthUpdate` that reflects a hit arrives
**+55.5 ms median (+98.1 ms p90)** after the client resolved that hit, because the reading is a
*notification of damage already applied*, not a warning. The hook's advantage over the reactive rule
is that whole interval plus one frame — **not** the escape round trip, which both designs pay.

**What is not true.** "Never needing to be triggered" implies preventing damage generally. That is
not achievable, and the measurement says why: **74 % of measured HP falls (571 of 775) have no
client-side hit claim at all.** For those the server is the only witness, no client-side hook can see
them coming, and no amount of in-process code changes that. The hook helps exactly where the client
is the first to know — projectiles with a client-visible approach — and does nothing at all for
point-blank spawns, melee contact and server-resolved damage.

**So the plan is shaped to deliver the reliable half first.** A *predictive nexus* (Phase 3) is
worth most of the benefit, is far less risky than movement injection, and answers the reported
frustration directly. A *dodge* (Phase 4) is the larger prize and the larger risk, and it is built
on the same sensor. This ordering is a deliberate choice, not a convenience: Phase 3 is worth
shipping on its own, and if Phase 4's movement question (RISKS §2a) turns out badly, Phase 3 is
unaffected.

### 0.1 Why the premise is nonetheless worth building on

Three measured facts make the hook decisively better than the current rule even before any dodging:

1. **The current rule can act on stale HP.** The recorded case: an escape injected on a 1 % reading
   **151 ms after HP had already reached zero** (`nexus-20261005-023129-2000.jsonl`). The hook is
   seeded from the same `Player.Health` but advanced by its own predictions, so it cannot be in that
   state.
2. **The current rule cannot distinguish "a hit landed" from "a hit is coming".** With one number it
   must be conservative in the direction that costs runs. The hook knows the bullet, so it can
   distinguish them — which is what makes a *lower* effective threshold safe.
3. **The dodge is genuinely achievable here.** It was not achievable through packets: the packet
   stream never re-broadcasts a projectile's position, so a packet-only dodge is dead reckoning with
   unbounded error (`work/auto-dodge-feasibility.md` §2). In-process, the true position is one field
   read away, and the reference engine's own architecture confirms that every good part of its dodge
   sits downstream of exactly that read.

---

## 1. Workstreams and phases

Six phases. Each has a **spike** (does the mechanism work at all?) and a **deliverable** (is it
built?), and each is gated on the previous one's exit criteria. The estimates are order-of-magnitude
for a single developer working with the existing `drelay` codebase.

| phase | question it answers | risk of the unknown | estimate |
|---|---|---|---|
| **0** | what exactly are we targeting, and can we prove it has not changed? | very low | 1–2 days |
| **1** | can native C++ code get into this process, early, and survive? | **high** | 3–5 days |
| **2** | can we observe the truth, and does our model of it match reality? | medium | 1–2 weeks |
| **3** | can a predictive nexus beat the reactive one? | low, given 2 | 1 week |
| **4** | can we move the player legitimately? | **highest** | 2–4 weeks |
| **5** | does the relay see the hook, and is that useful? | low | 3–5 days |

**Phases 0–3 are the recommended commitment.** They deliver the headline benefit, they are
independently useful, and they do not depend on the movement question. Phase 4 should be entered only
after Phase 3 is running live and has been measured against the reactive rule.

---

## Phase 0 — Ground truth, and a build gate

**Goal.** Turn the research into an executable, repeatable check that the target is what we think it
is. This phase produces no hook; it produces the thing that stops the hook from being dangerous later.

### Deliverables

1. **`tools/fingerprint.py`** (or a small PS1) that, given the game install path, prints and can
   `--verify`:
   - the loaded-assembly identity: which apphost, `DarzasDominion.dll` size and MVID
     (**the two apphosts carry different builds** — 50,216,960 vs 50,831,360 bytes);
   - `runtimeconfig.json` flags, specifically `System.Runtime.TieredCompilation` (**must be false**);
   - the IL-only assertion (`COMIMAGE_FLAGS`, `ManagedNativeHeader`);
   - `coreclr.dll` / `clrjit.dll` file versions;
   - the JIT/EE GUID read from `clrjit.dll`'s `g_CILJit` vtable slot **2**;
   - the runtime-function count of the vtable (must be four code pointers).
2. **`tools/il_index.py`** that builds the target table from the IL: for each target method,
   `(declaring type, name, metadata token, IL size, IL hash)`. This is the identity the hook matches
   on ([HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §4.3) and it must be regenerated per game build.
   *Note*: `ilspycmd -il` output is a usable input, and `autododgeresearch/il/client-5.8.2.il` is
   already a dump of this exact build.
3. **A pinned constants file** generated by `tools/poolresolve.py` — the resolved values that matter
   (`TilesPerSecond` coefficients, the damage law, `GameObject.Radius`, `ProjDesc.Size`). Recorded
   for understanding and for cross-checks; **not** baked into logic, because §"call don't compute".
4. **`docs/HOOK-BUILD-MATRIX.md`**: which game build the hook has been verified against, and the
   exact procedure to re-verify after a patch.
5. **The server-authority question — now partly answered, and worth one cheap experiment.** The
   research left this explicitly blocking: *does the server re-simulate hits, or does it trust the
   client's `GmHit` claim?* (`CLIENT-MAP.md` §10.1). Subsequent measurement settled the important half:
   the client **does** send the claim (`GmHit`, id 102, with the local player as `ObjectId` in 161 of
   580 captured packets), **but 74 % of HP falls have no claim at all** — so the server resolves most
   damage independently. That rules out "suppress the claim" as an architecture, which is a result.

   What it does not settle is whether a claim the server *does* receive is *necessary* for the damage
   it accompanies. That distinction no longer changes the design (we anchor on the collision either
   way), so it drops from blocking to interesting. **The experiment is still worth one afternoon**,
   because the answer is useful to RISKS §2b's scope accounting and because it needs no hook: the
   relay is already the man in the middle, so **drop the outbound `GmHit` for exactly one hit and
   observe whether the server still decrements Health.**


### Exit criteria

- `tools/fingerprint.py --verify` passes on the installed build and prints one line per component.
- The target table lists all four primary targets with distinct IL hashes.
- Deliberately corrupting a copy of the assembly (one byte in a target's IL) makes the table
  regeneration **fail to match**, proving the gate works.
- The server-authority question has a **definitive answer from a controlled `GmHit`-drop trial**,
  recorded with the session log that proves it.

### Why this is Phase 0 and not an afterthought

The existing `drelay` documentation makes the argument better than this plan can: three separate
reversals in that project found the decompiled field list disagreeing with the wire, and the lesson
recorded there is *"a decode must consume a captured packet to its exact last byte"*
(`docs/INJECTION.md` §1.1). The hook's equivalent is that a method fingerprint must match the
loaded assembly exactly. Getting this wrong in a hook does not produce a wrong log line; it produces
a patch on the wrong function in a live process.

---

## Phase 1 — Get in, and stay in

**Goal.** Prove the injection and early-load mechanism before designing anything that depends on it.

**Risk: high.** Everything downstream assumes native code can be resident before the first frame and
before the target methods are JITted. If that fails, the whole approach needs re-planning.

### Spike 1.1 — injection, with no hooking

- Bootstrap that runs **before the game's `Main`** and writes a line recording: its own path,
  the resolved game directory, the `DarzDominion.dll` MVID, and `getJit()`'s address.
- Two candidate mechanisms to try, in the order given in [HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §3:
  a controlled-environment launcher with `CREATE_SUSPENDED` (preferred; needs no writes to the game
  directory), and app-directory proxy DLL + `DOTNET_STARTUP_HOOKS` (needs a write to the install).
- **Success**: the line appears in the game's log directory *before* the game's own first log line,
  and the MVID matches the expected build.

### Spike 1.2 — the JIT detour, observing only

- Detour `compileMethod` per [HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §4, with all four
  disciplines (call real first, re-entrancy guard, last-error save/restore, `__try`).
- Log every compilation as `(token?, IL size, IL hash, native entry, native size)` to a CSV.
- **This spike validates three unverified assumptions at once**, and each is worth stating:
  1. does the vtable-slot-2 GUID check work on this build?
  2. does `getMethodDefFromMethod` actually live at the documented slot, or at the older one?
  3. is the `nativeEntry` we capture the address that will actually execute (i.e. does the EE copy
     after we return)?
- **Success**: the CSV contains all four target methods, matched by IL hash, with stable addresses
  across two runs. (Stable addresses are expected because tiering is off — an *unstable* result would
  mean the `TieredCompilation: false` assumption is wrong, which is the single most important thing
  this spike can discover.)

### Spike 1.3 — one detour, one field

- MinHook-patch **one** target (the frame tick) with a detour that reads exactly one float from the
  world and appends it to a file.
- **Success**: plausible values at ~60 Hz for 30 minutes of play, including zone transitions, a
  forced GC, and an alt-tab. No crash, no visible frame hitch.

### Deliverable — the hook skeleton

`Drelay.Hook.dll` (C++/x64) + `Drelay.Bootstrap` (managed), with:
- the startup self-check list from [HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §9, implemented as
  code and run on every start;
- the target table consumed from Phase 0, with a hard refusal on mismatch;
- the field-offset resolution pass, run every launch, logging every resolved offset;
- an off switch that is honoured before any patch is applied.

### Exit criteria

- The game runs for an hour with the hook loaded and all four targets patched, with no crash and no
  measured frame-time regression above the budget.
- Killing the hook's log target, or the relay, changes nothing.
- Starting the hook against a deliberately different build **refuses and logs why**, and the game
  runs normally.

---

## Phase 2 — Observe, and find out whether the model is true

**Goal.** Build the sensor, and use it to answer the question that decides whether the dodge is worth
building: **does our prediction of a collision agree with the game's?**

**This phase changes nothing about the game.** It is read-only, it is the largest single de-risking
step in the plan, and it produces the ground truth that every later phase is tuned against.

### Deliverable 2.1 — the threat set

- Spawn hook: `Projectile.ProcessProjectileData` → add a record keyed on `(GameId, startTime)`,
  reading **only arguments** ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §3.2).
- Frame tick: re-read each live bullet's position; apply the two-strike retirement with a min-age
  grace; invalidate on pool reuse.
- **Test T3** (`RISKS.md` §Test list): force a pool reuse and assert no record inherits.

### Deliverable 2.2 — the trajectory sampler

- Resolve `ProjDesc.GetPosition`'s concrete slot once; call it for a fixed 30 ms grid out to the
  horizon ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §4.2).
- Anchor every sample set on the live position each frame.
- Implement the linearity gate, and log when it says a trajectory is **not** linear — that log is the
  direct evidence for how often curved shots occur in real play, which currently is unknown.

### Deliverable 2.3 — the ground-truth comparison

**The most valuable artefact in the whole project.**

- Hook the collision decision (`Projectile.HitCharacter`) and, for every call, log:
  the bullet identity, the frame, `World.ClientTime`, the player position, the bullet position, the
  computed `r_eff`, and whether our own sensor had **predicted** an intersection with this bullet, and
  if so **how many milliseconds of warning** it gave.
- Also log bullets that hit that we never predicted, and bullets we predicted that never hit.

This gives four numbers that decide everything:

| measurement | what it decides |
|---|---|
| **how much warning we had** (distribution) | whether an escape can be dispatched in time — this is the decision input for Phase 3's thresholds |
| **false negatives** (hit, not predicted) | whether the model is complete enough to be relied on |
| **false positives** (predicted, no hit) | whether the dodge will be over-cautious; directly the `#ghost` rate |
| **the per-frame cost** (p50/p99) | whether the Phase 4 solver fits the budget at all |

### Exit criteria

- Over several hours of real play, the prediction agrees with the game's own collision decision on
  the great majority of hits, and every disagreement is explicable by a named cause (a curved
  trajectory, a homing re-target, a pool-reuse race, a >horizon flight).
- The sensor's p99 per-frame cost is inside the budget with room for the solver.
- **The linearity-gate log has told us what fraction of real bullets are straight**, which is the
  number that decides how good a dodge can be.

### Why this phase is not optional

Phase 2 is where the project can fail *cheaply*. If the sensor cannot predict collisions, no solver
will help, and the honest answer is to stop at Phase 3 (nexus only) rather than to build a dodge on a
model that does not match reality. Building the solver first would hide a sensor problem inside a
tuning problem, which is exactly the failure mode that makes dodges feel haunted.

---

## Phase 3 — Predictive nexus (the headline deliverable)

**Goal.** Replace the reactive rule with one that acts on knowledge of an incoming lethal hit,
**without moving the player at all.**

**Why this is a separate phase.** It needs no movement question, no server movement validation, and
no solver. It is the smallest change that captures most of the measured benefit, and it is the direct
answer to the reported frustration. If the project stopped here it would still be a success.

### Deliverable

The decision from [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §7:

- project, across every live threat, the damage that **cannot be dodged** (initially: that will land
  regardless of where the player is, which for Phase 3 means "the bullet is on a collision course and
  there is no time to leave");
- compute the exact damage by **calling `Entity.GetDamageTaken`** on the live player, never by
  recomputing it ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §6);
- if that projected damage is lethal, call **`World.RequestEscape()`** — the client's own nexus entry
  point, which runs the client's own gates and is indistinguishable from the player pressing the key;
- gates and asymmetry exactly as specified: `dryRun` default on, safe-area skip, per-world budget,
  re-arm, and the two error costs weighed deliberately (§7.3).

### Configuration surface

The existing `AutoNexus` config block, the dashboard panel and the `-Ddrelay.nexus.*` properties are
the established interfaces (`docs/AUTONEXUS.md` §4.2). The hook's settings should present **through
the same surfaces**, so an operator has one place to look. New keys are needed for the predictive
mode (horizon, aggression, minimum warning) and they must be reported-back-or-rejected exactly as the
existing settings are, because "a typo would otherwise look like a setting that simply had no effect"
(`docs/AUTONEXUS.md` §7).

### Verification

- **Offline replay is not possible for the decision** (it needs live game state), so verification is:
  the Phase 2 log plus a **deliberate paired trial**. The existing relay already timestamps both the
  injected escape and the `ProjHit` claim; what is missing is a deliberate trial that pairs them
  (`TIMING.md` §8 item 4). Run it: for a session, have the hook decide in dry run and log the exact
  moment it *would* have escaped, then compare against the `HealthUpdate` trace and the death, if any.
- **Success criterion**: for every death in the trial, either the hook had no warning (a claim-free
  fall — expected for ~74 % of falls) or it had warning and its dry-run decision would have escaped in
  time. **The two buckets must account for every death**, and that accounting is the honest measure of
  the feature.

### Exit criteria

- Live with `dryRun=false`, the hook escapes in the cases the reactive rule could not and does not
  escape when the reactive rule would not have.
- The relay's `EscapeAck` join confirms the escape was accepted (`docs/AUTONEXUS.md` §5).
- No escape is ever dispatched against stale HP.

---

## Phase 4 — Dodge (the larger prize, entered only after Phase 3 is live)

**Goal.** Not needing the nexus, in the cases where that is possible.

**Risk: highest.** Not the algorithm — the algorithm is well specified and the reference engine
proves the shape works — but the **movement question**, which is the one thing in this whole design
with no precedent in the reference and no measurement in the captures.

### Phase 4 prerequisite — close the AoE gap

**This is a task, not a risk item, and it must be done before the solver is written.** The threat model
in [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §3 is built entirely on `Projectile` objects. **AoE and
bomb damage is a separate channel that was not read in this research pass**: `Entity.HitBy(AoeData)`
(`Entity.cs:2203`), `AoeBomb.cs` and `Objects/Aoes*`. Bombs detonate on a timer, so a collision sweep
does not see them at all, which makes this another 74 %-class blind spot if it is left unaddressed.

Read it, and note the one thing that makes it *easier* than projectiles: the AoE telegraph **is** in the
packet stream (`GmAoes`/`GmAoes2` carry `Target`, `Value`, `Radius`, `Delay` — `work/report-handlers.md`
§C.3), so this is the one damage channel a packet-only proxy could also handle. The hook's advantage
here is live tile/occupancy state, not earlier knowledge.

### Spike 4.1 — can we move at all? (do this before writing a solver)

This spike uses **no dodge logic whatsoever**. It answers one question: *can the hook cause the
player to move in a direction the server accepts?*

Three mechanisms, in order of legitimacy ([CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §5.2):

| mechanism | how | what to watch for |
|---|---|---|
| **input steering** (preferred) | write `GameScene.GameInput.MoveAngle` / `.Moving`, which `World.cs:12690-12692` consumes one line later | the client generates the position, its own speed law, its own collision and its own timestamped move records — so the outbound stream is legitimate **by construction**. Watch for `GmKicked` (id 185) with a movement reason |
| **`MovingTowards`** | the client's own server-sanctioned move-to-a-point facility, `World.cs:12733-12750` | is it usable without a `GmMoveTowards` from the server? Unknown; a one-line experiment |
| **position write** | **do not** | corrupts `Player.TPS` and the move history the outbound packets are built from (`World.cs:12756-12783`); the reference engine's warning about raw writes causing server snap-backs is the same failure in another engine |

**The existing `drelay` proxy can help here, with no hook at all.** It is already the man in the
middle; injecting a plausible `Move` and watching for a kick is a test the proxy can run today, and
it is the cheapest possible way to learn whether the server validates movement. **Do this first.**

**Success criterion**: the player moves under hook control for a sustained period, the server does
not kick, no snap-backs, and the dodge device's own movement accounting (`Player.TPS`) stays sane.

**If this fails**: Phase 4 does not happen. Steel yourself for that outcome before starting, because
it is a real possibility and the honest response is to stop rather than to escalate to more invasive
techniques.

### Spike 4.2 — the solver, offline

The decision is a pure function of a plain-data snapshot ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md)
§2). That property exists **so that the solver can be tested without the game.** Build the snapshot
from a recorded trace in Phase 2's log format, and run the solver against it in a plain test harness:

- assert the invariants of [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §10;
- tune every weight in §5.6 in the harness, **never in a live run**;
- measure cost against the candidate counts, and pick the degradation threshold from data.

This is where the dodge's quality is actually determined, and it is the cheapest place to iterate.

### Deliverable 4.3 — the live dodge

- Wire the solver's chosen heading to the actuation mechanism Spike 4.1 proved.
- Ship it **behind its own switch**, defaulting to a mode that logs decisions without acting.
- Keep the Phase 3 escape running underneath as the safety floor, exactly as the reference engine
  keeps an immediate reflex under its planner: if the solver is degraded, stale or has not decided,
  the escape still works.

### Exit criteria

- Over a sustained live session: the dodge is *observably* dodging (the decision log shows moves that
  avoid predicted collisions), the frame budget is respected at p99, and the death rate in the
  claim-bearing cases (Phase 2's ~26 %) is measurably lower than the baseline recorded in Phase 3's
  trial.
- **A deliberate baseline run** with the dodge off, so the comparison is between two measured
  sessions rather than between a session and an impression.

---

## Phase 5 — The bridge

**Goal.** Join the hook's decisions to the relay's wire observations.

Specified in [JAVA-BRIDGE.md](JAVA-BRIDGE.md). Two things make it worth doing and neither is
optional if it is done at all:

1. **The escape-latency join.** The hook's `escape` event and the relay's `EscapeAck` are the two
   halves of one story; joining them on `World.ClientTime` measures the end-to-end escape path, which
   neither side can produce alone. That measurement is what makes the Phase 3 thresholds tunable
   rather than guessed.
2. **The dashboard.** The existing panels are how an operator reasons about a live session
   (`docs/AUTONEXUS.md` §7). The hook's state — armed, degraded, last warning, prediction accuracy
   from Phase 2 — belongs in the same place.

### Deliverable

- The shared-memory section, sequence-stamped rings and heartbeat of
  [JAVA-BRIDGE.md](JAVA-BRIDGE.md) §3–§5.
- **The dependency-free spike first** ([JAVA-BRIDGE.md](JAVA-BRIDGE.md) §7), because the shared-memory
  round-trip latency on this platform is the one number nobody could find a measurement for.
- New dashboard panel: hook state, the Phase 2 accuracy counters, the escape-latency join.
- Settings round-trip through the existing config-write path, with the same
  report-unknown-keys behaviour.

### Exit criteria

- The relay runs identically with and without the hook (`JAVA-BRIDGE.md` §5.3, mode 1 vs mode 3).
- Hook death produces a "stalled" report within 250 ms and no crash and no spin.
- Hook restart with a bumped epoch resyncs cleanly.
- The escape-latency join produces a number for a live escape.

---

## 2. Test list

The reference engine's audit is candid that *"live gameplay remains unverified"* for much of its
newest code, which is a warning worth heeding: a dodge's quality cannot be established by unit tests.
But a dodge's *correctness properties* can, and offline tests are the only cheap place to iterate.

| # | test | phase | what it catches |
|---|---|---|---|
| T1 | fingerprint gate refuses a mutated assembly | 0 | patching the wrong method after a game update |
| T2 | IL-hash table regenerates identically for the same build | 0 | nondeterminism in the identity |
| T3 | a re-rented `Projectile` never inherits state | 2 | the pooling hazard — presents as "reacting to bullets that do not exist" |
| T4 | retirement needs **two** consecutive verified absences, and a failed read is not an absence | 2 | ghost bullets, and the opposite: retiring live bullets |
| T5 | `stand` is never rejected while durably safe | 4 | the dodge moving for no reason |
| T6 | no emitted move is inside a bullet's disc at its arrival time | 4 | the core safety invariant |
| T7 | predicted-vs-actual collision agreement, over a real log | 2 | the model being wrong |
| T8 | hook cost p99 within budget for a long session | 1, 4 | the watchdog and the crash risk |
| T9 | with the dodge disabled the hook changes nothing observable | 3, 4 | the tool harming the game |
| T10 | hysteresis cannot lock a heading that has become unsafe | 4 | the specific way anti-jitter turns into a death |
| T11 | escape is never dispatched on stale HP | 3 | the measured 151 ms-after-death bug class |
| T12 | the escape-latency join produces a number | 5 | the bridge being decorative |

**T7 is the one to build first**, because it is the only test that can tell us whether the trajectory
model is right, and it needs no live play — Phase 2's collision log is enough.

---

## 3. What could make this plan wrong

Stated here rather than buried, because each has a specific early warning sign:

| if this is true | then | the early sign |
|---|---|---|
| movement cannot be actuated legitimately (RISKS §2a) | Phase 4 is cancelled; Phases 0–3 stand alone | Spike 4.1 fails, or the proxy-side move test gets kicked |
| terrain walkability cannot be called from the hook | the dodge loses its wall awareness; it will sometimes aim through walls | the offset resolution pass cannot find the accessor; the hook logs a capability loss at startup |
| the trajectory model disagrees with reality beyond tolerance | the dodge is unsafe, and only Phase 2's T7 can see it | false negatives and false positives in the collision comparison |
| the game's frame period is far tighter than assumed (144 fps+) | the solver budget shrinks; degrade to the outer ring sooner | Phase 1 Spike 1.3's frame timings |
| a future game patch adds anti-tamper | the whole hook approach may become unacceptable | the install is stable and has none today; the fingerprint gate makes a silent change impossible |

---

## 4. Milestones, as a checklist

- [ ] **M0** — fingerprint tooling green; target table generated; build-matrix documented
- [ ] **M1** — bootstrap runs before `Main`; UTC-off; `DarzDominion.dll` MVID recorded
- [ ] **M2** — JIT detour installed and validated; target methods found by IL hash with stable addresses
- [ ] **M3** — all four targets patched; one hour of play with no crash and no frame regression
- [ ] **M4** — threat set maintained correctly, including pool reuse and two-strike retirement
- [ ] **M5** — trajectory sampling working; the linearity-gate log quantifying curved-shot frequency
- [ ] **M6** — **prediction-vs-reality measurement published**: warning distribution, false positives, false negatives, per-frame cost
- [ ] **M7** — predictive nexus in dry run, with a paired trial accounting for every death
- [ ] **M8** — predictive nexus live, ack-confirmed
- [ ] **M9** — movement question answered, one way or the other, with evidence
- [ ] **M10** — solver tuned offline; invariants T5, T6, T10 asserted
- [ ] **M11** — dodge live behind a switch, with the escape as its safety floor
- [ ] **M12** — bridge connected; escape-latency join producing numbers; dashboard panel live

**M6 is the decision point.** Everything before it is de-risking; everything after it is building.
If M6's numbers are bad, the honest move is to stop at M8 and document why — which is a better
outcome than a dodge that cannot be trusted, and a much better outcome than one that is trusted and
wrong.
