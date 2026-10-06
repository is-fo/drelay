# Verification playbook

How to prove the hook works, and — more importantly — how to find out when it does not.

The companion documents are [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §2 (the test list) and
[RISKS.md](RISKS.md) §4 (the two-controller integration test, which is the one test that belongs to
neither the offline suites nor the wire checks and is reproduced in §5.1 below). Where that material
defines the suites, this document is the **procedure**: what to run, in what order, and what a pass
actually licenses you to believe.

The design principle throughout is the one the existing `drelay` project already established for its
packet work: **a test must be able to fail in the direction that matters.** `docs/INJECTION.md` §1.6
explains why the injection tests assert against literal bytes rather than round-trips — a round-trip
passes even when both directions share the same wrong idea of the format. The hook has the same
failure mode available to it: a prediction harness that compares the model against itself will pass
forever.

So every test below names **the wrong answer it can produce**, not just the check it performs.

---

## 1. The layered strategy, and what each layer can and cannot establish

A dodge's *quality* cannot be proven by tests — the reference engine's own safety audit says
*"live gameplay remains unverified"* for much of its newest code. What testing can establish is
narrower and still valuable: that the sensor tells the truth, that the solver obeys its own safety
rules, and that the hook does not harm the game.

| layer | runs where | establishes | **cannot** establish |
|---|---|---|---|
| **L0 — static gate** | offline, seconds | the target is the build we built for | anything about runtime behaviour |
| **L1 — offline unit/invariant** | offline, milliseconds | the solver's safety invariants hold over recorded and synthetic snapshots | that the snapshot resembles a real fight |
| **L2 — model agreement** | live, read-only | the predictor agrees with the game's own collision test | that agreeing implies good dodging |
| **L3 — in-game A/B** | live, acting | the hook beats the baseline on a measured metric | that the metric generalises past the sample |
| **L4 — end-to-end wire** | live, both processes | the hook's decisions reached the server | anything the server does not report |

The layers are ordered by cost and by how much they can mislead. **L2 is the pivot**: it is the only
layer that can tell us whether the model is right, and everything after it is tuning. Build L2 before
the solver.

---

## 2. L0 — the static gate

Run before every session. It is seconds of work and it is what turns "the hook crashed the game" into
"the hook refused to install and said why".

```powershell
$g = "E:\SteamLibrary\steamapps\common\Darza's Dominion"

# 1. Is the runtime the one we validated?
.\autododgeresearch\tools\Test-JitVtable.ps1  -Path "$g\clrjit.dll"      # expect: 4 code slots
.\autododgeresearch\tools\Test-ReadyToRun.ps1 -Path "$g\DarzasDominion.dll"  # expect: IL-only
.\autododgeresearch\tools\Get-PeExports.ps1   -Path "$g\coreclr.dll"     # expect: g_dacTable etc.

# 2. Is the assembly the build the target table was made from?
python autododgeresearch\tools\il_constants.py    # resolves the literals we depend on

# 3. Is tiered compilation still off?
Get-Content "$g\DarzasDominion.runtimeconfig.json" | Select-String TieredCompilation
```

**Expected output, recorded from the verified build** (5.8.2, .NET 9.0.20, clrjit 9.0.2026.41315):

| check | expected | why it matters |
|---|---|---|
| `getJit` → `g_CILJit` vtable | exactly **four** code slots, then data | `compileMethod` is slot 0; anything else means the interface moved |
| slot 2 GUID | the .NET 9 `JITEEVersionIdentifier` | **positive proof** we have the right object — and note the community libraries hardcode slot 4, correct only up to .NET Core 3.1 |
| `DarzasDominion.dll` | `ManagedNativeHeader=0` → IL-only | if it ever becomes R2R, a `compileMethod` hook stops seeing its methods |
| `System.Private.CoreLib.dll` | `RTR` → ReadyToRun | documents the intentional limitation: CoreLib is unhookable and unused |
| `DarzasDominion.runtimeconfig.json` | `TieredCompilation: false` | **the single assumption the whole design rests on** |
| assembly size | 50,216,960 **or** 50,831,360 | the two shipped apphosts carry *different* builds |

**Falsification test for L0 itself:** corrupt one byte inside a target method's IL in a *copy* of the
assembly and confirm the target table fails to match. If it still matches, the fingerprint is not
actually fingerprinting anything and every downstream guarantee is worthless.

**What L0 cannot tell you:** whether the enum those bytes came from is the one the running process
loaded. That is why the hook re-checks the loaded assembly's MVID at attach
([HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §9).

---

## 3. L1 — the solver's invariants, offline

The solver's decision is a **pure function of a plain-data snapshot** ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md)
§2). That property exists specifically so this layer is possible. Feed it:

- synthetic snapshots (hand-built volleys with known answers);
- **replayed snapshots from L2's logs**, which is where the value is, because they are real fights.

### The invariants to assert

Each of these is a property that can be checked mechanically, and each names the failure it catches.

| # | invariant | the wrong answer it catches |
|---|---|---|
| I1 | `stand` is never rejected while the player is durably safe for the full horizon | the dodge moving for no reason, which is how a dodge gets a player killed |
| I2 | no admissible candidate is inside a bullet's disc at its own arrival time | the primary safety violation |
| I3 | the chosen heading does not change while it stays admissible and no alternative beats it by the hysteresis margin | 60 Hz oscillation — the player vibrating instead of moving |
| I4 | **hysteresis cannot lock a heading that has become unsafe** | an over-eager anti-jitter turning into a death — the failure I3's fix introduces |
| I5 | a candidate is never emitted with a magnitude above the per-frame legal budget `b` | a move the server can reject |
| I6 | temporal clearance is never credited past the point the prediction is trusted | a blind tail scoring as "clear for 600 ms" |
| I7 | a re-rented `Projectile` never inherits a dead bullet's record | "the dodge reacts to bullets that do not exist" |
| I8 | the solver's cost stays inside its slice as candidate count scales | the frame budget |

I3 and I4 are deliberately in tension and must both hold. Testing only I3 produces a dodge that
commits to a heading that has become lethal; testing only I4 produces one that jitters.

**What L1 cannot tell you:** whether the snapshot is *representative*. A solver can satisfy every
invariant on synthetic data and behave badly in a real 40-bullet fight. That is what L2 is for.

---

## 4. L2 — the model-agreement measurement (the pivot)

**This is the most important test in the project, and it changes nothing about the game.** It is
read-only, and it produces the four numbers that decide whether any of the rest is worth building.

### The procedure

1. Hook the frame tick and the collision decision. **Do not hook the spawn yet** — first prove the
   simplest version works.
2. Every frame, run the sensor over the live threat set and record, per bullet, the earliest
   predicted intersection time with the player's swept path.
3. On every `Projectile.HitCharacter` call, record: the bullet identity, the frame, `World.ClientTime`,
   the player position, the bullet position, the computed `r_eff`, and **whether the sensor had
   predicted this bullet, and with how much warning**.
4. Also record the bullets the sensor predicted that **never** hit.
5. Play for several hours across varied content. The log is the deliverable.

### What to compute from it

| metric | definition | decides |
|---|---|---|
| **warning distribution** | for each hit the sensor predicted, `predictedImpactTime − firstPredictionTime` | whether an escape can be dispatched in time — this is the input to Phase 3's thresholds |
| **false-negative rate** | hits that happened but were never predicted, per hit | whether the model is complete enough to rely on |
| **false-positive rate** | predictions that never became hits, per prediction | how over-cautious the dodge will be; this *is* the ghost rate |
| **cost** | p50/p99 per-frame microseconds | whether the Phase 4 solver fits the budget at all |
| **linearity census** | the fraction of live bullets whose sampled path passes the linearity gate | how good a dodge can possibly be |

### Interpreting it honestly

| result | meaning | action |
|---|---|---|
| low false negatives, useful warning, cost inside budget | the model is sound | proceed to Phase 3 |
| high false negatives | the sensor is **missing threats**, which is the dangerous direction | stop; find the cause before building anything |
| high false positives | over-cautious; will refuse safe cells | tune the horizon and the trust prefix; acceptable to ship only if the false positives are explicable |
| warning ≈ 0 | **the whole premise fails** for this content | stop at nothing; document and re-plan |
| cost over budget | the solver cannot run | reduce candidate count, or cull harder before sampling |

**The falsification test that matters:** take the false negatives and explain every one by a *named
cause* — a curved trajectory, a homing re-target, a pool-reuse race, a flight longer than the horizon,
a bullet spawned inside the player. **If any false negative has no named cause, the model is not
understood and must not be trusted.** That sentence is the acceptance criterion for L2.

**What L2 cannot tell you:** whether *acting* on the prediction improves outcomes. A perfect
predictor with a bad solver, or with movement the server rejects, changes nothing. That is L3.

---

## 5. L3 — the in-game A/B

### The baseline must be a measured session, not an impression

Record a baseline run with the hook's acting disabled, then a run with it enabled, on comparable
content. Compare **death rate in claim-bearing cases** — that is, deaths where L2's log shows the
client declared the hit — because including claim-free deaths dilutes the signal with cases the hook
structurally cannot affect ([RISKS.md](RISKS.md) §2b).

### The Phase 3 trial specifically

The predictive nexus is verified differently from the dodge, because its decision needs live state
and cannot be replayed. The procedure is a **deliberate paired trial**, and the existing relay already
has both halves of it (`TIMING.md` §8 item 4):

1. run in dry run; log the exact moment the hook *would* have escaped, and why;
2. pair that against the relay's `HealthUpdate` trace and against the death, if there was one;
3. join the hook's `escape` event with the relay's `EscapeAck` on `World.ClientTime`
   ([JAVA-BRIDGE.md](JAVA-BRIDGE.md) §5.3), which measures the end-to-end escape latency that neither
   side can produce alone.

**The acceptance criterion is an accounting, not a rate:** every death in the trial must fall into
exactly one of two buckets — *"no warning existed"* (a claim-free fall, expected for ~74 %) or
*"warning existed and the dry-run decision would have escaped in time"*. **A death in neither bucket
is a bug**, and a third bucket labelled "unclear" is a failed trial.

### The anti-regression test

With the dodge and the escape both disabled, the hook must change **nothing observable** — the same
packets, the same timing, the same behaviour. This is the test that protects the operator's game from
the tool, and it should be run after every change to the hook infrastructure, not just at the end.

### 5.1 The two-controller test (the one that matters most for safety)

The dodge and the escape are two controllers fighting over one player, and the conflict is **located,
not hypothetical**: `World.RequestEscape()`'s channelled branch cancels on movement
(`World.cs:13043-13047`), so a dodge that steers will cancel an in-progress nexus cast in the same
frame ([RISKS.md](RISKS.md) §3d).

**The test:** with the dodge live and the escape armed, force both to want to act in the same frame and
assert the documented precedence — the escape wins, the dodge yields, the cast is not cancelled by the
hook, and the session is not kicked.

Variants, because each exercises a different branch of `RequestEscape` that a single test would miss:

| variant | picks up |
|---|---|
| escape while knocked up | the normal movement branch is bypassed entirely during knock-up, so the dodge cannot steer and must not pretend to |
| escape while a `MovingTowards` is active | a scripted move can be cancelled by state the hook did not observe (`Player.StopMovingTowards`) |
| escape in a safe area | `RequestEscape` is a legitimate **no-op** there — the dodge must not mistake that for success |
| escape inside the world-entry grace window | the instant path is taken rather than the cast |
| escape in a totem world | the **channelled** path is taken and cancels on the first movement — the variant most likely to be discovered the hard way, because it only happens to characters with a particular run modifier |

---

## 6. L4 — end-to-end on the wire

The relay is the only place several of these facts are visible at all.

| what to check | how | why only the relay can |
|---|---|---|
| the injected escape was **accepted** | `jq -c 'select(.pkt=="EscapeAck")' work/logs/events-*.jsonl` | the stock client **discards** `GmEscapeAck` (`docs/AUTONEXUS.md` §5) |
| the server's **reason** if it refused | `GmForcedEscape` (id 184) carries a string16 | same |
| whether the server **kicked** us | `GmKicked` (id 185), reason decoded into `data.reason` and latched as `kickedReason` | the fastest explanation of a failed experiment there is |
| the escape **latency**, end to end | join the hook's `escape` event with the `EscapeAck` on `World.ClientTime` | spans both processes |
| the hook's own view of a decision | the new dashboard panel | there is no other window into the hook |

### The named-failure checks

Each of these is a specific, previously-observed failure with a specific log signature. Running them
is how a session is diagnosed rather than guessed at.

| symptom | check | likely cause |
|---|---|---|
| kicks shortly after the hook acts | `data.reason` on the `Kicked` event | **movement validation** — the decisive Phase 4 risk ([RISKS.md](RISKS.md) §2a) |
| the dodge moves and the character snaps back | the relay's `Move` records vs the server's next correction | a position write instead of the input path |
| the nexus cast is cancelled when the dodge acts | the client's own `EscapeCastState` pair in the log | the verified interaction bug ([RISKS.md](RISKS.md) §3d) |
| "no events from the hook" | `heartbeatSeq` staleness, then `ProcessHandle` liveness | hook dead vs game hung — the mapping cannot tell you which ([JAVA-BRIDGE.md](JAVA-BRIDGE.md) §5.2) |
| the hook works but nothing appears in the dashboard | `magic`/`version`/`epoch` in the section header | a layout change, or a stale section from a previous hook instance |

---

## 7. Pre-flight checklist, in order, per session

```
[ ] L0 fingerprint green (script above)
[ ] hook installs; startup self-checks all pass (HOOK-ARCHITECTURE.md §9)
[ ] all four targets found by IL hash      <- a miss means an inlined target; use the documented fallback
[ ] field offsets resolved and logged
[ ] dry run ON
[ ] bridge: hook state visible in the dashboard, heartbeat fresh
[ ] anti-regression: with everything disabled, the game behaves normally
[ ] two-controller precedence exercised: escape vs dodge in one frame (§5.1)
--- play ---
[ ] no `Kicked` with a movement reason
[ ] no escape dispatched on stale HP
[ ] the hook's frame cost p99 inside budget
[ ] decisions logged with reasons
--- after ---
[ ] L2 metrics recomputed over the session
[ ] every death accounted for in a named bucket
[ ] any new false negative named, or the model is not trusted
```

---

## 8. What this playbook deliberately does not claim

- **It does not prove dodge quality.** No offline test can. The reference engine's audit is explicit
  that its own tests pin internal invariants rather than gameplay quality, and that *"live gameplay
  remains unverified"* for its newest stages. The layer-2 measurement is the closest thing to a quality
  signal available, and it measures the *sensor*, not the dodge.
- **It does not establish server acceptance.** Only a live run does, and only for the behaviours
  actually exercised — which is why the movement question is answered from the proxy first
  ([RISKS.md](RISKS.md) §2a).
- **It cannot distinguish "the dodge is bad" from "the content is undodgeable."** The ~74 % claim-free
  damage is undodgeable by construction; the accounting in §5 is what keeps those cases from being
  mistaken for failures.
- **It has no regression suite for the game itself.** If the hook destabilises the game, the game's own
  `StallProbe`/`UiThreadWatchdog` logs are the detector, not this document.
