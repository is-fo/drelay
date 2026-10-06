# Risks, limits and what this cannot do

Written in the evidence-first style of the rest of this directory: every risk has a **likelihood**, a
**consequence**, the **evidence** that establishes it, and a **mitigation or an honest concession**.
Where a risk cannot be mitigated, it says so rather than proposing a mechanism that would not work.

The scope statement comes first because it bounds everything else.

---

## 0. Scope, and the honest framing

This is a **single-player automation tool, running on the operator's own machine, at the operator's
own privilege level, in a game the operator owns.** It is not a cheat for a competitive game, it does
not interact with other players' clients, and it runs entirely in user mode.

What follows from that:

- **The technical risk is destabilising the operator's own game**, not evasion. That is why so much of
  this plan is about failing loudly and doing nothing rather than about being clever.
- **The moral hazard worth naming** is that an auto-nexus and an auto-dodge change what the game is.
  The existing `drelay` documentation is explicit that the auto-nexus ships **off by default and in dry
  run**, and that flipping the master switch is a deliberate act (`docs/AUTONEXUS.md` §4.2). The hook
  must inherit that posture: off by default, dry run by default, and every decision logged.
- **No anti-cheat was observed.** The install is a plain self-contained .NET 9 apphost: no kernel
  driver, no EasyAntiCheat/BattlEye/Denuvo DLL, no integrity self-check over the game assembly, and no
  anti-debug flag in the PE header. What *is* present is Steam (achievements and stats, unaffected by
  a hook) and the game's own diagnostics.

---

## 1. Risk register

| # | risk | likelihood | consequence | evidence |
|---|---|---|---|---|
| **1** | the server validates movement and rejects hook-steered moves | **medium — unknown** | Phase 4 cancelled | none; unmeasured |
| **1b** | the server trusts the client's `GmHit` hit claim rather than re-simulating | **settled: it does not trust it alone** | an entire alternative architecture (suppress the claim instead of dodging) is ruled out — which is a *result*, not a loss | the client does send the claim (`GmHit`, id 102, 161/580 with the local player as `ObjectId`), but **74 % of falls have no claim**, so the server resolves most damage itself |
| **2** | the damage that matters never touches client code | **high — it is the majority case** | scope of benefit is smaller than hoped | measured: **571 of 775 HP falls (74 %) have no client hit claim** |
| **3** | a game patch changes the target | **certain over time** | hook refuses to install | two apphosts ship *different* builds |
| **4** | the hook stalls the game's main thread | medium | visible freeze; the game's own watchdog reports it | `UiThreadWatchdog`/`StallProbe` exist; tolerance unmeasured |
| **5** | a bad read crashes the game | medium | crash, lost run | GC and pooling hazards are real (`HOOK-ARCHITECTURE.md` §5) |
| **6** | the dodge fights the escape | **high if unhandled — verified** | an in-progress nexus cast is cancelled, in the same frame | `World.cs:13043-13047` |
| **7** | prediction disagrees with the game's collision | unknown until measured | the dodge is confidently wrong | no measurement exists yet; Phase 2 exists to produce one |
| **8** | terrain awareness unavailable | medium | dodge aims through walls | the walkability accessor is unresolved |
| **8b** | **AoE / bomb damage is not modelled at all** | **certain — it is not built yet** | a whole damage channel stays un-dodged; the plan's threat model covers `Projectile` only | `Entity.HitBy(AoeData)`, `AoeBomb` and `Objects/Aoes*` were not read in this pass; bombs detonate on a timer, so a collision sweep cannot see them |
| **9** | the runtime's assumptions break | low today | whole approach needs rework | tiered compilation is off and verified; a future patch could flip it |
| **10** | the tool gets blamed for a game bug | medium | time lost, trust lost | unavoidable; mitigation is logging |

Each is expanded below.

---

## 2. The risks that can change the plan

### 2a. Server-side movement validation — the decisive unknown

**This is the single biggest unknown in the project, and it is the one that decides whether a dodge
exists at all.**

The reference engine's actuation works because it calls the game's own `MoveTo`, which is
*"NOT speed-clamped, collision-checked, packet-emitting"* — i.e. the server accepts it because it
arrives through the legitimate path, and the reference explicitly warns that raw position writes
*"bypass the rigidbody sync and cause server snap-backs"*. Our client's equivalent is the input path:
writing `GameScene.GameInput.MoveAngle`/`Moving` makes **the client itself** generate the position,
apply its own speed law and collision, and emit its own timestamped move records
(`World.cs:12690-12692`, `:12710`, `:12714`, `:12780-12783`). That should be legitimate by
construction.

But *should* is not *is*, and the client's own code shows what the server can check against:
`GmMove` carries `History` + `Position` + `Time`, and `CLIENT-MAP.md` §6.5 notes that a per-step jump
larger than one resolved constant *"will look like a speed hack"*. The distance constants are now
recoverable (`tools/poolresolve.py`), so at least the question is well posed.

**The decisive mitigation is cheap and should be done first: test it from the proxy, with no hook at
all.** `drelay` is already the man in the middle. Injecting a plausible `Move` and watching for
`GmKicked` (id 185, whose reason string the relay already decodes — `docs/INJECTION.md` §5.5) answers
the question in an afternoon, before a line of C++ is written.

**If it fails**: Phase 4 is cancelled and Phases 0–3 stand alone. That is a real possibility and the
plan is deliberately shaped so that the outcome is still a success.

### 2b. Most damage does not pass through client code

**Measured, not feared: 571 of 775 HP falls (74 %) have no client-side hit claim.** The client's
collision test is not the arbiter for most damage; the server resolves it independently (`TIMING.md`
§2.3, §7.2).

The consequence is a hard scope bound. The hook improves the cases where **the client is the first to
know**:

| case | hook helps? |
|---|---|
| slow projectile with a visible approach | **yes, comfortably** |
| fast projectile, predicted on a previous frame | **yes — 55–98 ms of extra warning** |
| fast projectile, acted on only at the collision | marginally, one frame |
| telegraphed AoE with a non-zero delay | **yes** — and note the packet stream already carries the telegraph, so this is the one row a proxy could also do |
| first tick of a beam/DoT | partly |
| point-blank spawn | **no** — the collision is t = 0 |
| melee/contact | **no** — no client involvement |
| every subsequent DoT tick | **no** |
| server-resolved damage | **no** — by definition |

**The mitigation is honesty in the reporting, not a mechanism.** The Phase 2/3 trial must account for
**every** death in two buckets — "no warning existed" and "warning existed and the decision would have
been in time" — because that accounting is the only honest measure of the feature, and it is the
number the operator needs in order to know what they are running.

### 2c. Game updates

**Certain over time.** Two apphosts already ship different builds of the game assembly, so even
"which file" is not fixed.

**Mitigation, fully specified in [HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §6 and Phase 0**: an
executable fingerprint (`--verify`) covering the assembly size and MVID, per-target IL hashes, the
runtime DLL versions, the JIT/EE GUID from the JIT vtable, and the effective tiered-compilation
setting. On mismatch the hook **installs nothing and logs one line**, and the game runs normally.

**Concede**: this does not make the hook survive a patch. It makes a patch *fail safely*, which is the
achievable goal. The re-verification procedure after each game update belongs in the build matrix
Phase 0 produces.

### 2d. Stalling the game's main thread

The hook runs inside the frame. The game ships its own watchdog (`UiThreadWatchdog`, `StallProbe`)
and its log already prints slow-frame reports, so a hook that blocks is **reported by the game
itself** — which is simultaneously a crash risk and a gift, because the game's own log becomes a free
profiler for the hook.

**Mitigation**: the nested budget of [HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §7 (≤ 300 µs
self-imposed), the degrade-don't-skip rule (drop candidate rings, never an admission test), and the
rule that **the hook never blocks on the Java relay**. The relay is observation; a stalled or dead
relay must not change the hook's behaviour by one instruction.

**Concede**: the watchdog's actual tolerance is unmeasured. Measure it in Phase 1 Spike 1.3 and set
the budget to a tenth of it.

---

## 3. Risks that are engineering, not architecture

### 3a. Memory safety: the GC and the pool

Two concrete hazards, both with specific mitigations already specified:

- **a raw object pointer is invalidated by a compacting GC.** Mitigation
  ([HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §5): never cache a raw pointer across an allocation;
  re-read the root every frame; hold long-lived references in a `GCHandle`, which the runtime updates
  when the object moves; and do the managed reads from a managed callback so the thread is already in
  cooperative mode with correct stack metadata.
- **`Projectile` is pooled, and a re-rented instance gets a new `GameId`.** A planner keyed on the
  pointer silently inherits a dead bullet's history, presenting as *"the dodge reacts to bullets that
  do not exist"*. Mitigation: key all per-bullet state on `(GameId, startTime)`, invalidate on pool
  reuse, and make it test **T3**.

**The strongest structural mitigation is already in the design**: the spawn hook
(`Projectile.ProcessProjectileData`) takes the entire threat — angle, position, damage, descriptor,
`hitsPlayer` — **as arguments**, so the highest-frequency hook touches no managed field at all. That
removes both hazards from the hot path by construction.

**Concede**: a `.NET` runtime update could change object-layout details. Mitigation: measure every
offset at startup on every launch rather than baking it in, and abort if a resolve looks implausible.

### 3b. Terrain awareness

The dodge's swept-path admission tests need to know what blocks the player. The client has that
information; whether it is reachable as a callable from the hook is unresolved.

**Mitigation**: make it a startup capability probe with three outcomes — resolved, unresolved, and
resolution-failed. If unresolved, the dodge degrades to bounds-only and **logs the capability loss at
startup**, so an operator knows the dodge is running blind rather than discovering it when the player
walks into a wall.

### 3c. The prediction model being wrong

Unmeasured until Phase 2. **This is why Phase 2 exists and why it is a phase rather than a task.**

The mitigation is a measurement, not a mechanism: log every predicted collision and every actual
collision, and publish the warning distribution, the false-negative rate and the false-positive rate.
**The plan has an explicit decision point at M6** where bad numbers mean stopping at the predictive
nexus rather than building a dodge on a model that does not match reality.

**Concede**: a dodge cannot be *proven* safe. The reference engine's own audit says so repeatedly —
*"live gameplay remains unverified"* for much of its newest code, and its tests pin internal
invariants rather than dodge quality. The honest goal is a model whose disagreements are all
explicable, plus an escape floor underneath it.

### 3d. The dodge fighting the escape — verified interaction bug

**Located, and it is not hypothetical.** `World.RequestEscape()`'s channelled branch cancels on
movement:

```csharp
// World.cs:13043-13047
if (GameScene.GameInput.Moving) { CancelEscapeCast(); return; }
```

So a dodge that sets `GameInput.Moving = true` **cancels an in-progress nexus cast** in the same frame
or the next. This is the classic two-controllers-one-player problem, and it is the reason the
reference engine has explicit WASD-yield logic in every one of its engines.

**Mitigation, and it must be designed before the dodge is enabled live** — not patched afterwards:

1. the escape is **authoritative**: once the hook has decided to escape, the dodge stops emitting for
   the duration of the escape (the cast branch's own cancellation is the signal);
2. the dodge's state machine observes the client's cast flag and yields, rather than racing it;
3. the interaction is an explicit test (see §4).

Two further interactions in the same family, both from `CLIENT-MAP.md` §6.5:

- **while `Player.KnockedUp`, the normal movement branch is bypassed entirely**, so a hook cannot
  steer during knock-up. The dodge must detect this and not pretend to dodge.
- **`Player.StopMovingTowards()` is called from the dead/spirit gate and on arrival**, so a scripted
  move can be cancelled by state the hook did not observe. If the `MovingTowards` mechanism is chosen,
  the hook must treat "the move stopped" as information, not as a bug.

---

## 4. The test that matters most for safety

Beyond the unit-level list in [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §2, one integration
test deserves isolation because it is the only one that can catch a whole class of live failure:

> **The two-controller test.** With the dodge live and the escape armed, force both to want to act in
> the same frame and assert the documented precedence: the escape wins, the dodge yields, the cast is
> not cancelled by the hook, and the session is not kicked.

Its variants: escape during knock-up; escape while a `MovingTowards` is active; escape in a safe area
(where `RequestEscape` is a legitimate no-op and the dodge must not mistake it for success); escape
inside the world-entry grace window (where the instant path is taken); and escape in a totem world
(where the channelled path is taken and cancels on the first movement).

That last variant is the one most likely to be discovered the hard way, because it only happens to
characters with a particular run modifier.

---

## 5. Explicit non-goals and conceded limits

Stated plainly so that no one reads more into the design than is there:

- **It cannot make every hit survivable.** 74 % of measured falls never touch client code
  (§2b). No in-process design changes that.
- **It cannot dodge a point-blank spawn or a contact hit.** There is no earlier frame in which the
  information exists.
- **It cannot stop damage already being ticked.** Once the server is applying a beam or DoT, the
  escape must traverse one network leg plus tick quantisation to change a decision already in
  progress.
- **It cannot suppress the client's hit claim usefully.** The client does declare projectile hits
  (`GmHit`, id 102), so this was worth checking rather than assuming. Measured: 34 % of claims produce
  no immediate fall and **74 % of falls have no claim**, and no other damage source has a claim at all.
  Suppressing it would blind the server to some hits and remove none of the ones that matter.
- **It is not a packet-level change.** Everything the existing relay does — the packet path, the log,
  the dashboard, the status strip — is unchanged. The hook is additive.
- **It does not modify the game's files** in the recommended injection path (the controlled-environment
  launcher). The alternative path (an app-directory proxy DLL) does, and that should be a deliberate
  decision rather than a default, because the existing project's README makes
  *"changes nothing on disk in the game's installation"* a stated property.
- **It cannot be verified to be safe.** Only to be *measurably better* and *explicably wrong*. That is
  the honest ceiling and the plan is written to it.

---

## 6. The pre-mortem, in four sentences

If this project fails, the four most likely stories are:

1. **The movement question failed** and Phase 4 was cancelled — in which case the predictive nexus
   still shipped and the project was a partial success, provided that outcome was designed for rather
   than discovered.
2. **The sensor could not predict collisions** and Phases 4–5 were cancelled at the M6 decision point
   — in which case the measurement that proved it is itself the valuable output, and the plan's
   phase ordering is what made it a cheap failure instead of an expensive one.
3. **The hook destabilised the game** — the frame budget, the GC hazards and the pooling hazard are
   the three places to look, and each already has a named mitigation and a named test.
4. **The dodge and the escape fought each other** — which is already located in the client's own code
   (`World.cs:13043-13047`) and therefore belongs in the design, not in the debugging.
