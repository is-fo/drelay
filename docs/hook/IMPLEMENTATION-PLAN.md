# Implementation plan

This is the deliverable: **what to build, in what order, and how to know each step worked.**

The supporting research is [CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) (the client, verified),
[TIMING.md](TIMING.md) (the measured window), [CLIENT-MAP.md](CLIENT-MAP.md) (the exhaustive
reverse-engineering map), [HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) (how the hook is installed),
[DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) (what it computes), [JAVA-BRIDGE.md](../bridge/JAVA-BRIDGE.md) (how it
reports), [VERIFICATION.md](VERIFICATION.md) (how each phase is proved, and how it can fail) and
[RISKS.md](RISKS.md).

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
client-side hit claim at all.** The client only ever claims hits it resolves as its own collision —
and the only claim packets in the whole assembly are `GmHit` (projectiles), `GmAllyHit`,
`GmSweepHit`/`GmPlayerSweepHits` (sweeps) and `GmLaserHit`/`GmPlayerLaserHit` (beams). Damage from
**AoE and bombs produces no claim at all** (`Entity.HitBy(AoeData)`, `Entity.cs:2203`, contains no
`SendPacket`), because it resolves on a timer server-side. So the hook helps exactly where the client
is the first to know — projectiles with a client-visible approach — and does nothing for AoE, bomb
detonations, damage-over-time ticks, or point-blank spawns.

**So the plan is shaped to dodge first and to escape last.** The deliverable is the *dodge*: where the
client is the first to know a bullet is coming, the hook **moves the player out of the way** rather
than ending the run. The client's own `World.RequestEscape()` remains, as the **floor** rather than a
phase of its own — it is reached at exactly one point in the decision
([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §7 gate 6), when the projected damage over the horizon is
lethal **and no admissible move avoids it**.

**That merge is the ordering decision of 2026-10-13, and the reasoning is recorded here because it
reverses the plan's original shape.** A predictive nexus is still a nexus: it ends the run, and the
reported frustration is being forced to leave at all. The escape-only phase that used to sit at 3 was
therefore judged of questionable value on its own, and its decision folded into the dodge as the last
gate — one sensor, one snapshot, one frame budget, one place where the two controllers can disagree.
The relay bridge that used to be Phase 5 is now **Phase 4** and is unchanged by any of this: it is
additive to both, and nothing in Phase 3 waits on it.

What that costs is that the largest risk now sits inside the phase that matters: the dodge rests on
the **movement question** (RISKS §2a) — the one thing in this design with no precedent in the
reference engine and no measurement in the captures. The mitigation is structural rather than
optimistic: the question is answered by a spike that uses **no dodge logic at all**, and it is run
before a solver is written (Spike 3.1). If it fails, the plan's honest stop is documented in §3
rather than improvised at the end.

### 0.1 Why the premise is nonetheless worth building on

Three measured facts make the hook decisively better than the current rule, and they are the ground
the dodge stands on:

1. **The current rule can act on stale HP.** The recorded case: an escape injected on a 1 % reading
   **151 ms after HP had already reached zero** (`nexus-20261005-023129-2000.jsonl`). The hook is
   seeded from the same `Player.Health` but advanced by its own predictions, so it cannot be in that
   state.
2. **The current rule cannot distinguish "a hit landed" from "a hit is coming".** With one number it
   must be conservative in the direction that costs runs. The hook knows the bullet, so it can
   distinguish them — which is what lets the dodge be aimed at a predicted path rather than triggered
   by a number, and what stops the escape from firing on the aftermath.
3. **The dodge is genuinely achievable here.** It was not achievable through packets: the packet
   stream never re-broadcasts a projectile's position, so a packet-only dodge is dead reckoning with
   unbounded error (`hook/work/auto-dodge-feasibility.md` §2). In-process, the true position is one field
   read away, and the reference engine's own architecture confirms that every good part of its dodge
   sits downstream of exactly that read.

---

## 1. Workstreams and phases

Five phases. Each has a **spike** (does the mechanism work at all?) and a **deliverable** (is it
built?), and each is gated on the previous one's exit criteria. The estimates are order-of-magnitude
for a single developer working with the existing `drelay` codebase.

| phase | question it answers | risk of the unknown | estimate |
|---|---|---|---|
| **0** | what exactly are we targeting, and can we prove it has not changed? | very low | 1–2 days |
| **1** | can native C++ code get into this process, early, and survive? | **high** | 3–5 days |
| **2** | can we observe the truth, and does our model of it match reality? | medium | 1–2 weeks |
| **3** | **can we move the player out of the way — and escape only when we cannot?** | **highest** | 3–6 weeks |
| **4** | does the relay see the hook, and is that useful? | low | 3–5 days |

**Five phases, because two of the six merged.** Phases 0–2 are the de-risking half and are done
(Phase 0 but for one live experiment that does not gate the hook); Phase 3 *is* the project — the
dodge, with the escape as its floor — and it is the only phase that carries the movement question.
Phase 4 is additive and can slip indefinitely without touching Phase 3.

**Phase 3 is entered only after Phase 2's numbers have been read**, because that is the M6 decision
point, and it is the one place in the plan where stopping is the honest move (§4). Everything before
it exists to make that stop cheap. Note the change of shape: the old plan could stop at a predictive
nexus and still ship something. With the escape folded in as a floor instead of a phase, **a stop
after Phase 2 ships nothing new** — the reactive auto-nexus remains the status quo — so at that point
the deliverable is the measurement itself, and it has to be good enough to stand alone.

---

## Phase 0 — Ground truth, and a build gate

**Goal.** Turn the research into an executable, repeatable check that the target is what we think it
is. This phase produces no hook; it produces the thing that stops the hook from being dangerous later.

### Deliverables

1. ✅ **`hook/tools/Fingerprint.ps1` + `hook/known-build.json` — BUILT.** Runs nine-plus static checks and
   exits non-zero on any mismatch: the tiered-compilation setting, the assembly's IL-only status and
   content hash, the `clrjit` vtable's code-slot count, and both runtime DLL versions. It resolves
   `getJit` from the export directory, recovers `g_CILJit` from the function prologue, and reads the
   vtable itself — so it needs no external tools and no hardcoded RVAs.

   **It was mutation-tested, and the testing found a real hole.** The first version checked the
   assembly *size*, and flipping a single byte of the assembly left every check passing while the
   file's length was unchanged. That is exactly the failure the gate exists to prevent. A SHA-256
   check was added and the mutation now fails loudly with the expected and actual hashes. The lesson
   is recorded in `hook/known-build.json` itself, because it is the kind of thing a future reader would
   otherwise "simplify" away:

   ```powershell
   .\hook\tools\Fingerprint.ps1                    # exit 0 = safe to install
   .\hook\tools\Fingerprint.ps1 -GamePath "D:\Games\Darza's Dominion"
   ```

   Three things it *cannot* check, reported as known gaps rather than passes: the effective
   `DOTNET_*` environment (a `.NET 9` environment variable overrides `runtimeconfig.json`, so a stray
   `DOTNET_TieredCompilation=1` would defeat the whole design), the loaded assembly's MVID, and the
   target-method IL hashes. The last two must be re-checked **in-process at attach**
   ([HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §9).

2. ✅ **`hook/tools/il_index.py` + the populated `targetMethods` block — BUILT.** Resolves the four
   targets to an exact byte identity and writes it into `hook/known-build.json`: for each target,
   `(declaring type, name, arity, metadata token, RVA, IL size, IL hash)`.

   It is a **metadata reader, not a text scraper**. It walks the PE's CLI metadata tables
   itself — no `ilspycmd`, no `dotnet`, no network — so the hash is computed over the exact
   `MethodDef.Rva` bytes the JIT will hand the hook, and the IL dump is used only as an
   independent cross-check. Three things it establishes beyond "it runs":

   - **The identity is a SHA-256 of the raw IL bytes**, not of a re-encoded instruction
     stream. The native side hashes `CORINFO_METHOD_INFO.ILCode[0..ILCodeSize)` and needs no
     IL decoder at all, so a wrong operand-width table in this tool cannot silently change
     which function the hook patches.
   - **Every target is cross-checked against the IL dump** on three independent values — RVA,
     code size and parameter count — and a target the dump cannot confirm is reported as *not
     checked*, not as a pass.
   - **The table is deterministic**: two runs produce byte-identical output.

   Four findings from building it are load-bearing and are recorded so that a future reader
   does not "simplify" them away (all four are in `docs/hook/HOOK-BUILD-MATRIX.md`): this build's
   fat-header `MoreSects` size field is **not trustworthy** (a wrong choice there yields a
   plausible-looking but wrong hash); the metadata tables are **not the documented layouts**
   (`MethodDef` is 18 bytes per row, not 16); `Assembly.Name` is unreadable and the MVID is
   taken from the `Module` table instead; and the IL dump's textual nesting **cannot** be
   reconstructed reliably, so targets are resolved from metadata and the dump only
   cross-checks.

   ```powershell
   python .\hook\tools\il_index.py --check            # verify, write nothing
   python .\hook\tools\il_index.py                    # verify + update the record
   python .\hook\tools\il_index.py --mutation-test    # prove the gate can fail
   ```

3. **A pinned constants file** generated by `hook/tools/poolresolve.py` — **deliberately not
   produced.** The values are recovered and reproducible (`poolresolve.py` resolves them from
   the IL and the decrypted pool), but the design rule is *call, don't compute*
   ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §6, §9): the speed law is **called**
   (`PlayerFunctions.TilesPerSecond`, with live status tiers), damage is **called**
   (`Entity.GetDamageTaken`), and trajectories are **called** (`ProjDesc.GetPosition`). A
   frozen constants file would be a second source of truth for numbers the hook must never use
   directly, and its most likely use — deriving a move budget from a nominal speed — is the
   specific bug [CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §5.3 warns about. The resolved values
   stay where they are read: in `poolresolve.py`'s output and in the documents.
4. ✅ **`docs/hook/HOOK-BUILD-MATRIX.md` — BUILT.** The human-readable half of `hook/known-build.json`:
   the verified build and what breaks if each pinned value changes, the four targets, the hash
   recipe, the re-verification procedure after a patch, what the gate cannot check, the four
   build-specific findings above, and where every number comes from.
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

   **This is the one Phase 0 item still open, and the only one that needs a live session.**


### Exit criteria

- ✅ `hook/tools/Fingerprint.ps1` exits 0 on the installed build and prints one line per component.
  *met for the 5.8.2 / .NET 9.0.20 build: 15 checks pass, 2 known gaps — and the four target IL
  hashes are now **verified** rather than merely reported. The gate re-hashes each target's
  instruction stream straight out of the assembly and separately requires that `il_index.py`
  regenerate the same table, so the two halves must agree.*
- ✅ **The gate provably can fail — at both levels.** *File level: flipping one byte of the
  assembly (same file size), or flipping `TieredCompilation` to `true`, makes it exit 1 with the
  expected/actual pair named. Method level: `hook/tools/Test-FingerprintGate.ps1` copies the install to
  a sandbox, flips one byte inside a target's IL, and asserts that the gate exits non-zero naming
  **that target's** IL hash while the other three still pass. That distinction is the point: a
  file-level check cannot say which method moved, and "which method moved" is what decides whether
  the hook may install.*
- ✅ The target table lists all four primary targets with **distinct** IL hashes, each
  cross-checked against the IL dump on RVA, code size and parameter count.
- ✅ Deliberately corrupting a copy of the assembly (one byte in a target's IL) makes the table
  regeneration **fail to match**, proving the gate works at the *method* level and not only the
  file level: `il_index.py --mutation-test` reports exactly one target's hash as changed.
- ⬜ The server-authority question has a definitive answer from a controlled `GmHit`-drop trial,
  recorded with the session log that proves it.

### Why this is Phase 0 and not an afterthought

The existing `drelay` documentation makes the argument better than this plan can: three separate
reversals in that project found the decompiled field list disagreeing with the wire, and the lesson
recorded there is *"a decode must consume a captured packet to its exact last byte"*
(`docs/hook/INJECTION.md` §1.1). The hook's equivalent is that a method fingerprint must match the
loaded assembly exactly. Getting this wrong in a hook does not produce a wrong log line; it produces
a patch on the wrong function in a live process.

---

## Phase 1 — Get in, and stay in

**Goal.** Prove the injection and early-load mechanism before designing anything that depends on it.

**Risk: high.** Everything downstream assumes native code can be resident before the first frame and
before the target methods are JITted. If that fails, the whole approach needs re-planning.

> **Status: built, confirmed live, and closed 2026-10-12.** The three artefacts
> (`Drelay.Hook.dll`, `Drelay.Bootstrap.dll`, `Drelay.Launcher.exe`) build clean and every check that
> does not need a running game passes. The live runs have happened too: injection happens before
> `Main`, runtime validation passes, all four targets are matched by IL hash, the frame tick is
> patched, the hook reads live state from the real game, **Exit 2a's log-denial test passed all four
> of its assertions**, and the **hour-long endurance run** completed with no fault recorded and
> 198 586 successful frame reads over 209 897 frame calls. The two verdict lines that read FAIL are
> instrument faults — a `$null` exit code under Windows PowerShell 5.1 (fixed in both runners) and a
> single 323 µs sample that the overflowed frame ring cannot place in time. The accounting is
> [PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md) §11;
> [PHASE1-HANDOFF.md](PHASE1-HANDOFF.md) is the long record of how it got there and
> [PHASE1-SCOPE.md](PHASE1-SCOPE.md) §6 separates what is verified from what is not.

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

- The game runs for an hour with the hook loaded and **all four targets patched**, with no crash and
  no measured frame-time regression above the budget.
  *The design point is **60 Hz (16.7 ms per frame)** — the operator will cap FPS in the game's own
  settings, and the high-rate/uncapped limit test is deferred. Justification: the hook has been
  measured running under an **uncapped ~200 Hz frame loop** at the title screen (~4.9 ms per frame),
  so 60 Hz is the conservative design point, not the observed worst case.*
  *Open decision, not taken here: only `frameTick` is patched today, deliberately — a detour body must
  match the target's signature, and the other three have no Phase 1 behaviour to run
  ([PHASE1-SCOPE.md](PHASE1-SCOPE.md) §2.4). Either this criterion is amended to "the one target with
  Phase 1 behaviour patched", or the other three get pass-through detours.*
  **Closed 2026-10-12 as amended**: the criterion is read as "the one target with Phase 1 behaviour
  patched", which is what `frameTick` is, and the hour ran with it patched and no fault recorded
  ([PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md) §11.6). Phase 2 gives the other three their
  bodies, and its own exit criteria name them.
- **Exit 2a — a destroyed log target costs evidence, never the game.** With the hook running and
  patched, the evidence path is denied to the hook **by a stated mechanism**: a second process holds
  `hook.log` open with sharing disabled, which is the mechanism every observed failure has had (a
  scanner or a reader). *Renaming or deleting the file is not a test while the hook holds its own
  handle — Windows keeps the file alive, or the hook simply recreates it on the next pass.* The run
  must then show all four:
  1. **the game is unaffected**: it runs to completion and exits the way it does in an untouched run;
  2. **the frame cost is unchanged**: `frames.csv`'s `elapsed_us` distribution matches an untouched
     run's — the evidence path is off the frame path and must stay off it;
  3. **the kill was felt**: `hook.log.append-failed` exists, naming the `errno`, the win32 error and
     the path the hook actually tried to open; and, when the lock is applied before the evidence
     files are written, `summary.txt` carries `log appends : N failed pass(es)` with `N ≥ 1`. **A run
     with neither is a run whose control never fired** (handoff §8's third question);
  4. **a transient kill costs no line**: once the lock is released, the lines written before it appear
     in `hook.log` — the read cursor advances only on a successful write, so the retry is the
     mechanism, and the test asserts it rather than assuming it.

  *Not tested. The failure mode is not hypothetical and the instruments already exist: the run
  `hook\obj\live-runs\20261010-014016-p11352` logged one `ERROR_SHARING_VIOLATION` (errno 13, win32
  32, `lines pending: 0`) and recovered every line, and handoff §10 records ~371 failed appends in the
  run whose launcher tail held the handle. What is missing is a test that denies the path
  deliberately and asserts those four properties.*
  **Met 2026-10-12**: `hook/tools/Run-Exit2a-LogDenial.ps1`, run from the operator's shell
  (`hook\obj\cases\exit2a-logdenial\`, run `20261012-030308-p19828`). All four properties hold —
  `phase 1 complete`, no fault event, the cost unchanged (9 µs worst after the first, against the
  baseline's 2 µs), `hook.log.append-failed` with `errno 13 / win32 32`, `log appends : 113 failed
  pass(es)`, and `log lines : 66 written, 0 overflowed` with 72 lines present. The lock was taken
  during the collection delay and its denial proven with a refused write probe. The verdict's one
  FAIL is the `$null` exit code of §11.3. See [PHASE1-VERIFICATION.md](PHASE1-VERIFICATION.md) §11.2.

- **Exit 2b (Phase 4) — a relay that dies mid-session changes nothing.** The bridge is observation:
  a missing, stalled or dead relay must not change the hook's behaviour by one instruction, and the
  hook must never wait on it ([JAVA-BRIDGE.md](../bridge/JAVA-BRIDGE.md) §8, [RISKS.md](RISKS.md) §2d).
  *Moved to Phase 4's exit criteria, because it cannot be tested here and is not Phase 1's: the relay
  is Phase 4 and absent, its **absence** is the status quo — every run so far is a no-relay run — and
  what needs proving is the relay dying **after** attach.*
- Starting the hook against a deliberately different build **refuses and logs why**, and the game
  runs normally.
  *Satisfied: `hook/tools/Test-PeReader.ps1` covers the deliberately tampered MVID, and run 1 of
  `hook/tools/Test-Injection.ps1` covers the fingerprint mismatch — the run is refused with both values
  named.*

---

## Phase 2 — Observe, and find out whether the model is true

**Goal.** Build the sensor, and use it to answer the question that decides whether the dodge is worth
building: **does our prediction of a collision agree with the game's?**

**This phase changes nothing about the game.** It is read-only, it is the largest single de-risking
step in the plan, and it produces the ground truth that every later phase is tuned against.

> **Status: closed by operator decision on 2026-10-13, with one measurement carried forward.** The
> execution plan, the decision rule for the mechanism Phase 1 left open (`ProjDesc.GetPosition` and
> the live projectile set must be touched **on the game's thread**, which a call detour into managed
> code cannot do), the instrumentation this phase needed first, and the fallback if the mechanism
> test failed are all in [PHASE2-SCOPE.md](PHASE2-SCOPE.md). Its §7 restates these criteria against
> their actuals, and the close rests on two explicit waivers rather than on the criteria being met:
> the sampler's p99 cost (**"ignore performance concerns for now"** — the cost work belongs with the
> solver that shares the frame, which is now Phase 3) and the false-negative accounting, which the
> session that produced it over-counted by asking the collision predicate on every entry instead of
> every `true` (§4.15). That last number is the one open measurement, and Phase 3's dry-run trial is
> where it is closed out.

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

- Hook the collision decision (`Projectile.HitCharacter`) and, **for every call that returns
  `true`**, log: the bullet identity, the frame, `World.ClientTime`, the player position, the bullet
  position, the computed `r_eff`, and whether our own sensor had **predicted** an intersection with
  this bullet, and if so **how many milliseconds of warning** it gave.
- **The `true` is not a detail, and getting it wrong cost a session.** `HitCharacter` is a per-frame
  *predicate*: `Projectile.Update` calls `Hit()` on every live projectile every frame, and the answer
  decides whether the hit resolves (`Projectile.cs:1505,1514`; `CLIENT-FINDINGS.md` §4 records that it
  "returns true iff the bullet is being consumed by the hit"). Logging every *entry* counts questions
  instead of decisions — the 2026-10-12 20:42 session logged 418 328 of them against 48 resolved hits,
  and every frame a bullet spent near the player became another false negative. **The event this
  deliverable is about is the `true`; the entries are not evidence.**
- Also log bullets that hit that we never predicted, and bullets we predicted that never hit.

This gives four numbers that decide everything:

| measurement | what it decides |
|---|---|
| **how much warning we had** (distribution) | whether there is time to move — the input to Phase 3's dodge budget and to the timing of its escape floor |
| **false negatives** (hit, not predicted) | whether the model is complete enough to be relied on |
| **false positives** (predicted, no hit) | whether the dodge will be over-cautious; directly the `#ghost` rate |
| **the per-frame cost** (p50/p99) | whether the Phase 3 solver fits the budget at all |

### Exit criteria

- Over several hours of real play, the prediction agrees with the game's own collision decision on
  the great majority of hits, and every disagreement is explicable by a named cause (a curved
  trajectory, a homing re-target, a pool-reuse race, a >horizon flight).
- The sensor's p99 per-frame cost is inside the budget with room for the solver.
- **The linearity-gate log has told us what fraction of real bullets are straight**, which is the
  number that decides how good a dodge can be.

*As closed on 2026-10-13: the third criterion is met — **0.695** of sampled trajectories were straight,
over 406 077 linear and 178 611 curved sample sets — the first is the measurement the phase was gated
on but its false-negative count is an over-count from a wrong event set, and the second is waived for
now and moved into Phase 3 with the solver that shares the frame
([PHASE2-SCOPE.md](PHASE2-SCOPE.md) §4.15, §7).*

### Why this phase is not optional

Phase 2 is where the project can fail *cheaply*. If the sensor cannot predict collisions, no solver
will help, and the honest answer is to **stop before Phase 3** rather than to build a dodge on a model
that does not match reality. Building the solver first would hide a sensor problem inside a tuning
problem, which is exactly the failure mode that makes dodges feel haunted.

**Stopping here now means stopping for real.** Under the old ordering the consolation prize was a
predictive nexus that shipped on its own; the merge removed that, so a stop after Phase 2 leaves the
reactive auto-nexus as the status quo, and the deliverable is the measurement itself — which is why
§4's decision point is stated as a decision and not as a checkpoint.

---

## Phase 3 — Dodge, with the escape as its floor

> **Status: entered 2026-10-14, and Spike 3.1 is ANSWERED YES as of 2026-10-14 04:08.**
> [PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) is the measurement and the next step; the execution plan,
> the corrected spike order and the measurement that reshapes the phase are in
> [PHASE3-SCOPE.md](PHASE3-SCOPE.md). What exists now: a **fifth pinned target** (the movement
> integrator, `World.cs:12666`) with a managed replacement on its entry, a per-frame actuator that
> writes `GameScene.GameInput` (the mechanism this section ranks first), an opt-in and bounded
> launcher switch for it, and `hook/tools/steering_report.py` to read the result against the relay's own
> wire evidence. **The live sweep of run `20261014-040859-p22748` moved the player under hook control
> in eight commanded directions: the value read back from `MoveAngle` equalled the value written on
> every logged frame (worst `0.0000000 rad`, 1428 of 1951 frames moved, heading residual ≤ 2°,
> mean cos 0.995), and the per-frame displacement (0.133–0.146 units at 16.680 ms) agrees with the
> resolved `3.5 + 5.0·(SPD+10)/75` law and with §5.2's offline step distribution.** The one witness
> still outstanding is the wire's: no relay log was attached, so `GmKicked` has not been observed
> *absent* — the operator reports no disconnect, and illegal movement is disconnected quickly. That
> does not gate the dodge; it gates the claim, and it is one command
> ([PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) §3.1, §7). **Two corrections to this section's text are
> load-bearing and are recorded rather than quietly dropped:** (1) the "cheapest possible way to
> learn whether the server validates movement" it names — injecting a plausible `Move` from the
> relay — cannot answer Spike 3.1, because the relay cannot reach the actuator this design chose and
> the question is not whether the server validates movement (the operator settled that) but whether
> the hook can steer through the client's own input path; [PHASE3-SCOPE.md](PHASE3-SCOPE.md) §3 is
> the argument, and the proxy keeps the one job that is genuinely its own. (2) The
> **movement-boundary search** this section asks Spike 3.1 to run is **not** run by deliberate
> overspend, for the reasons in §3 and §"Exit criteria" of [PHASE3-SCOPE.md](PHASE3-SCOPE.md): `b =
> TilesPerSecond × frameSeconds` is a legal move by construction, so the design never needs to know
> where the cliff is, and the per-frame figure it does need is now measured
> ([PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) §1.4). The search stays available as an explicit, opt-in
> experiment.

**Goal.** Not needing the nexus, in the cases where that is possible — and calling it only when
nothing else is.

**This is the merged phase, and the merge is concrete.** The move and the escape are not two features
with two triggers; they are one decision with two outcomes, taken once per frame
([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §5, §7):

| outcome | when |
|---|---|
| **move** | an admissible move exists that avoids lethal projected damage (gates 4–5) |
| **escape** | the projected damage over the horizon is lethal **and no admissible move avoids it** (gate 6) |
| nothing | everything else, logged with the reason it did nothing (gates 0–3, 7) |

**Why it is one phase.** A predictive nexus is still a nexus: it ends the run, and the frustration
that started this work is being forced to leave at all. Escape-only was judged of questionable value
as a standalone phase (operator decision, 2026-10-13), and the two share one sensor, one snapshot and
one frame budget — building them apart would have meant building the same plumbing twice and then
reconciling two controllers in the same frame, which is a **verified** hazard rather than a
hypothetical one ([RISKS.md](RISKS.md) §3d).

**Risk: highest.** Not the algorithm — the algorithm is well specified, and
[DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §12 is explicit about what transfers from the reference
engine and what must not be copied — but the **movement question** ([RISKS.md](RISKS.md) §2a), which
is the one thing in this whole design with no precedent in the reference and no measurement in the
captures. Spike 3.1 exists to answer it before any solver code is written.

### Phase 3 prerequisite — scope decisions, already taken

Two decisions remove work from this phase, and both are recorded because they change what "done"
means:

1. **AoE and bomb damage is out of scope by operator decision.** It is left to the player to dodge
   manually. That closes the coverage gap this plan previously treated as a required task — but it
   **promotes intent preservation to a requirement** ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §5.7):
   a dodge that overrides the player's steering takes away the tool they need for the channel it does
   not cover. An AoE volume may be a *score* input; it must never be an *admission* filter.
2. **Terrain is available, not hypothetical.** The client's own collision grid is
   `private byte[,] World.<grid>` (`World.cs:4318`), fed by `GmTiles` and written at `World.cs:9677`.
   Admission rules 2 and 3 read it directly ([CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §5.4), so the
   "running blind" degradation is a fallback for a failed offset resolution rather than an expected
   state.

**Movement validation is also reframed.** The server does validate movement, but the check is
**distance per game tick** — so the design's per-frame budget, derived from the game's own
`TilesPerSecond × frameSeconds`, is the constraint that matters, and it is an arithmetic question
rather than an architectural one ([RISKS.md](RISKS.md) §2a).

### Spike 3.1 — can we move at all? (do this before writing a solver)

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
Because the validation is a distance-per-tick check, the spike should deliberately probe the
**boundary**: find the largest per-tick magnitude that is still accepted, and confirm the design's
budget sits inside it with margin.

> **Measured 2026-10-14, and the boundary probe is deliberately not run.** The sustained movement
> happened, in eight directions, for 1951 frames; the distance-per-tick question is answered from the
> other end — the client's own accepted `GmMove` telemetry (1.44 units per 200 ms typical, 1.96 at
> p99, [PHASE3-SCOPE.md](PHASE3-SCOPE.md) §5.2) and now the client's own per-frame integrator
> (0.133–0.146 units per 16.680 ms frame, [PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) §1.4), both inside
> the resolved law. **Deliberate overspend is a live-character cost for a number the design does not
> use**: `b = playerSpeed · frameSeconds` is a legal step by construction, so the cliff is not on the
> path. The kick is still unwitnessed on the wire and is the one thing this criterion leaves open
> ([PHASE3-HANDOFF.md](PHASE3-HANDOFF.md) §3.1).

**If this fails**: **the dodge does not exist, and with it the point of the project.** The escape
floor of Deliverable 3.4 is the only behaviour left, and the merge decision above already recorded
that escape-only is of questionable value on its own — so the default is to stop and document, and
shipping the floor by itself would be a deliberate operator choice rather than a fallback this plan
assumes. Steel yourself for that outcome before starting, because it is a real possibility and the
honest response is to stop rather than to escalate to more invasive techniques.

### Spike 3.2 — the solver, offline

> **DONE, 2026-10-14.** The solver, the trace recorder and the offline harness exist and are green on
> five synthetic scenarios; the seven defects the harness found are recorded with their symptoms.
> **[SPIKE3.2-OFFLINE-SOLVER.md](SPIKE3.2-OFFLINE-SOLVER.md)** is the handoff, including the one
> outstanding item — a recorded session, which is the only thing that can settle T7.

The decision is a pure function of a plain-data snapshot ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md)
§2). That property exists **so that the solver can be tested without the game.** Build the snapshot
from a recorded trace in Phase 2's log format, and run the solver against it in a plain test harness:

- assert the invariants of [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §10;
- tune every weight in §5.6 in the harness, **never in a live run**;
- measure cost against the candidate counts, and pick the degradation threshold from data.

This is where the dodge's quality is actually determined, and it is the cheapest place to iterate.

### Deliverable 3.3 — the live dodge

- Wire the solver's chosen heading to the actuation mechanism Spike 3.1 proved.
- Ship it **behind its own switch**, defaulting to a mode that logs decisions without acting.
- Keep the escape floor of Deliverable 3.4 running underneath, exactly as the reference engine keeps
  an immediate reflex under its planner: if the solver is degraded, stale, immobile or has not
  decided, the floor still works.

### Deliverable 3.4 — the escape floor, and the precedence rule

The decision is [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §7, and it is **not** a second controller:
it is gate 6 of the same decision, evaluated in the same frame pass, after the move set has been
tested.

- project, across every live threat, the damage that **cannot be dodged**: the maximum total projected
  damage across all bullets whose predicted paths intersect every admissible candidate's swept path
  ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §7.2);
- compute the exact damage by **calling `Entity.GetDamageTaken`** on the live player, never by
  recomputing it (§6);
- if that projected damage is lethal **and no admissible move avoids it**, call
  **`World.RequestEscape()`** — the client's own nexus entry point, which runs the client's own gates
  and is indistinguishable from the player pressing the key;
- gates and asymmetry exactly as specified: `dryRun` default on, safe-area skip, per-world budget,
  re-arm, and the two error costs weighed deliberately (§7.3).

**The escape is the last gate, not the first.** That is the whole substance of the merge: the floor
fires only when the solver can show that moving cannot help, so a dodge that has legs is never
pre-empted by an escape that merely had a lower threshold.

**Precedence is part of the deliverable, not a later fix**, because the conflict is already located
in the client's own code — `World.RequestEscape()`'s channelled branch cancels on movement
(`World.cs:13043-13047`), so a dodge that steers cancels an in-progress nexus cast in the same frame
([RISKS.md](RISKS.md) §3d). The rule, written before the dodge is enabled live rather than after:

1. the escape is **authoritative** — once the hook has decided to escape, the dodge emits nothing for
   the duration of the cast;
2. the dodge's state machine observes the client's cast flag and yields, rather than racing it;
3. the hook knows which mode it is in from the same `Player.Totems` check the client uses — and while
   the instant path is the normal one, the *rule* must not assume that, because the failure mode when
   it is wrong is a cancelled, run-saving escape.

The hazard is scoped to challenge runs (the `Impermanence` totem), which is exactly why a test suite
that never equips the totem passes while the bug sits untouched.

### Configuration surface

The existing `AutoNexus` config block, the dashboard panel and the `-Ddrelay.nexus.*` properties are
the established interfaces (`docs/hook/AUTONEXUS.md` §4.2). The hook's settings should present **through
the same surfaces**, so an operator has one place to look. New keys are needed for the dodge (horizon,
aggression, candidate budget), for the escape floor (minimum warning), and for the precedence rule;
they must be reported-back-or-rejected exactly as the existing settings are, because "a typo would
otherwise look like a setting that simply had no effect" (`docs/hook/AUTONEXUS.md` §7).

### Verification

- **Offline first.** The decision is a pure function of a plain-data snapshot, so the solver is tuned
  in Spike 3.2's harness and the invariants T5, T6 and T10 are asserted there — never in a live run.
- **Offline replay is not possible for the acting decision** (it needs live game state), so the live
  verification is the Phase 2 log plus a **deliberate paired trial**. The existing relay already
  timestamps both the injected escape and the `ProjHit` claim; what is missing is a deliberate trial
  that pairs them (`TIMING.md` §8 item 4). Run it: for a session, have the hook decide in dry run and
  log the exact moment it *would* have moved and the exact moment it *would* have escaped, then
  compare against the `HealthUpdate` trace and the death, if any.
- **Success criterion**: for every death in the trial, either the hook had no warning (a claim-free
  fall — expected for ~74 % of falls) or it had warning and its dry-run decision would have survived
  the horizon. **The two buckets must account for every death**, and that accounting is the honest
  measure of the feature. This is also where Phase 2's over-counted false-negative number is closed
  out.
- **The two-controller test** ([VERIFICATION.md](VERIFICATION.md) §5.1) forces the escape and the
  dodge to want to act in the same frame — with and without the `Impermanence` totem — and asserts the
  precedence above.
- **The movement boundary search** ([VERIFICATION.md](VERIFICATION.md) §5.2) turns "the server
  validates movement" into a measured per-tick threshold, and asserts the design's budget sits inside
  it at every `Slowed` tier.

### Exit criteria

- After Spike 3.1, the movement question has an answer with evidence — one way or the other — and the
  budget is measured against the server's own boundary rather than derived and assumed.
- Over a sustained live session with the dodge acting: the dodge is *observably* dodging (the decision
  log shows moves that avoid predicted collisions), the frame budget is respected at p99, and the
  death rate in the claim-bearing cases (Phase 2's ~26 %) is measurably lower than a **deliberate
  baseline run** recorded with acting disabled — so the comparison is between two measured sessions
  rather than between a session and an impression.
- The escape fires only when the solver can show that no admissible move avoids lethal projected
  damage, the relay's `EscapeAck` join confirms it was accepted (`docs/hook/AUTONEXUS.md` §5), and no
  escape is ever dispatched against stale HP.
- The two-controller precedence holds in the live session: no cast is cancelled by the hook, and no
  `GmKicked` arrives with a movement reason.

---

## Phase 4 — The bridge

**Goal.** Join the hook's decisions to the relay's wire observations.

Specified in [JAVA-BRIDGE.md](../bridge/JAVA-BRIDGE.md). Two things make it worth doing and neither is
optional if it is done at all:

1. **The escape-latency join.** The hook's `escape` event and the relay's `EscapeAck` are the two
   halves of one story; joining them on `World.ClientTime` measures the end-to-end escape path, which
   neither side can produce alone. That measurement is what makes the Phase 3 escape floor's timing —
   and the precedence rule that goes with it — tunable rather than guessed.
2. **The dashboard.** The existing panels are how an operator reasons about a live session
   (`docs/hook/AUTONEXUS.md` §7). The hook's state — armed, degraded, last warning, prediction accuracy
   from Phase 2 — belongs in the same place.

### Deliverable

- The shared-memory section, sequence-stamped rings and heartbeat of
  [JAVA-BRIDGE.md](../bridge/JAVA-BRIDGE.md) §3–§5.
- **The dependency-free spike first** ([JAVA-BRIDGE.md](../bridge/JAVA-BRIDGE.md) §7), because the shared-memory
  round-trip latency on this platform is the one number nobody could find a measurement for.
- New dashboard panel: hook state, the Phase 2 accuracy counters, the escape-latency join.
- Settings round-trip through the existing config-write path, with the same
  report-unknown-keys behaviour.

### Exit criteria

- The relay runs identically with and without the hook (`JAVA-BRIDGE.md` §5.3, mode 1 vs mode 3).
- **A relay that dies mid-session changes nothing** — moved here from Phase 1's Exit 2, where it could
  not be tested ([JAVA-BRIDGE.md](../bridge/JAVA-BRIDGE.md) §8, [RISKS.md](RISKS.md) §2d). With the hook
  attached and reading live state, kill the relay: the game's exit, the hook's `frame reads` and
  `frames.csv`'s `elapsed_us` distribution must all be unchanged from a paired run without the kill,
  and the hook must never wait on the bridge. *A missing relay is already the status quo — every run
  before Phase 4 is a no-relay run — so the absence case is the control, not the test.*
- Hook death produces a "stalled" report within 250 ms and no crash and no spin.
- Hook restart with a bumped epoch resyncs cleanly.
- The escape-latency join produces a number for a live escape.

---

## 2. Test list

The reference engine's audit is candid that *"live gameplay remains unverified"* for much of its
newest code, which is a warning worth heeding: a dodge's quality cannot be established by unit tests.
But a dodge's *correctness properties* can, and offline tests are the only cheap place to iterate.

**The procedure that runs these — in what order, with what a pass licenses, and with the
falsification test that must pass before the model may be trusted — is
[VERIFICATION.md](VERIFICATION.md).** The table below is the index of what exists; that document is
how to run it.

| # | test | phase | what it catches |
|---|---|---|---|
| T1 | fingerprint gate refuses a mutated assembly | 0 | patching the wrong method after a game update |
| T2 | IL-hash table regenerates identically for the same build | 0 | nondeterminism in the identity |
| T3 | a re-rented `Projectile` never inherits state | 2 | the pooling hazard — presents as "reacting to bullets that do not exist" |
| T4 | retirement needs **two** consecutive verified absences, and a failed read is not an absence | 2 | ghost bullets, and the opposite: retiring live bullets |
| T5 | `stand` is never rejected while durably safe | 3 | the dodge moving for no reason |
| T6 | no emitted move is inside a bullet's disc at its arrival time | 3 | the core safety invariant |
| T7 | predicted-vs-actual collision agreement, over a real log | 2 | the model being wrong |
| T8 | hook cost p99 within budget for a long session | 1, 3 | the watchdog and the crash risk |
| T9 | with the dodge disabled the hook changes nothing observable | 3 | the tool harming the game |
| T10 | hysteresis cannot lock a heading that has become unsafe | 3 | the specific way anti-jitter turns into a death |
| T11 | escape is never dispatched on stale HP | 3 | the measured 151 ms-after-death bug class |
| T12 | the escape-latency join produces a number | 4 | the bridge being decorative |

**T7 is the one to build first**, because it is the only test that can tell us whether the trajectory
model is right, and it needs no live play — Phase 2's collision log is enough.

---

## 3. What could make this plan wrong

Stated here rather than buried, because each has a specific early warning sign:

| if this is true | then | the early sign |
|---|---|---|
| movement cannot be actuated legitimately (RISKS §2a) | Phase 3 stops, and the escape floor is **not** shipped on its own — the project rests on Phase 2's measurement | Spike 3.1 fails, or the proxy-side move test gets kicked |
| terrain walkability cannot be called from the hook | the dodge loses its wall awareness; it will sometimes aim through walls | the offset resolution pass cannot find the accessor; the hook logs a capability loss at startup |
| the trajectory model disagrees with reality beyond tolerance | the dodge is unsafe, and only Phase 2's T7 can see it | false negatives and false positives in the collision comparison |
| the game's frame period is far tighter than assumed (144 fps+) | the solver budget shrinks; degrade to the outer ring sooner | Phase 1 Spike 1.3's frame timings |
| a future game patch adds anti-tamper | the whole hook approach may become unacceptable | the install is stable and has none today; the fingerprint gate makes a silent change impossible |

---

## 4. Milestones, as a checklist

- [x] **M0** — fingerprint tooling green; target table generated; build-matrix documented
      *(the gate passes 15 checks; the four targets are pinned by IL hash and cross-checked
      against the disassembly; `docs/hook/HOOK-BUILD-MATRIX.md` records the procedure. The one
      Phase 0 item still open is the `GmHit`-drop trial, which needs a live session and does
      not gate the hook.)*
- [x] **M1** — bootstrap runs before `Main`; UTC-off; `DarzDominion.dll` MVID recorded
      *(built: the bootstrap is a `DOTNET_STARTUP_HOOKS` hook that records the MVID from the
      *runtime*, refuses on mismatch, and blocks until the native side confirms the detour is
      installed — so `Main` cannot run first. **Now observed in the real process**: the bootstrap
      signals ~78–141 ms into the process, before `Main`, and the loaded assembly's MVID is verified
      against the file on disk.)*
- [x] **M2** — JIT detour installed and validated; target methods found by IL hash with stable addresses
      *(built: validation is vtable-in-`.text` → JIT/EE GUID → tiering → MVID, in that order, and the
      detour is installed only after all four pass. **Now run and confirmed**: all four targets are
      matched by IL hash, the `getMethodDefFromMethod` slot probe resolves to `0x76` and agrees with
      Phase 0's token for `frameTick`, and the JIT-reported address is shown to be an executing
      address because the frame detour is entered.)*
- [x] **M3** — all four targets patched; one hour of play with no crash and no frame regression
      *(closed 2026-10-12, as amended: `frameTick` — the one target with Phase 1 behaviour — is
      patched, and the hour ran with it patched. The endurance run `20261012-012057-p3488` recorded
      209 897 frame calls at 53.8 Hz average, 198 586 successful live reads against 2 218 failures
      (1.06 %), no `Application Error`/`.NET Runtime` event for the window, and the read correctly
      paused at the window's close. All four targets were matched by IL hash during the run — the
      `1 target(s) found` line counts only the fixed 30 s discovery window and is an instrument
      defect, not a result (§11.5). The one number that is not a pass is the single 323 µs sample
      against the 300 µs ceiling, unexplained and unplaceable because the frame ring overflowed;
      Phase 2's first instrumentation change is the cost histogram that fixes that
      ([PHASE2-SCOPE.md](PHASE2-SCOPE.md) §6.1).)*
- [ ] **M4** — threat set maintained correctly, including pool reuse and two-strike retirement
- [ ] **M5** — trajectory sampling working; the linearity-gate log quantifying curved-shot frequency
- [x] **M6** — **prediction-vs-reality measurement published**: warning distribution, false positives, false negatives, per-frame cost
      *(closed by operator decision on 2026-10-13 with two recorded waivers rather than with every
      number clean: the per-frame cost is deferred to the solver that has to share the frame, and the
      false-negative count came from a session that asked the collision predicate on every entry
      instead of every `true`, so it is an over-count. Both are in
      [PHASE2-SCOPE.md](PHASE2-SCOPE.md) §4.15 and restated against the criteria in its §7; the
      corrected counter rides on Phase 3's dry-run trial.)*
- [ ] **M7** — movement question answered, one way or the other, with evidence
      *(**in progress**: the mechanism is built and the build is green — a fifth pinned target,
      a managed replacement on the movement integrator, an opt-in bounded steering switch, and
      `hook/tools/steering_report.py` with a fixture that asserts the good, unpatched, kicked and empty
      cases. What is missing is the live run. [PHASE3-SCOPE.md](PHASE3-SCOPE.md) §5 is the
      protocol.)*
- [ ] **M8** — solver tuned offline; invariants T5, T6, T10 asserted
- [ ] **M9** — dodge in dry run, with the escape floor wired and the paired trial accounting for every death
- [ ] **M10** — dodge acting live behind a switch, escape fired only when no admissible move exists, ack-confirmed
- [ ] **M11** — bridge connected; escape-latency join producing numbers; dashboard panel live

**M6 is the decision point, and it has now been taken: the project proceeds to Phase 3.** Had the
numbers been bad, the honest move would have been to stop here and publish the measurement — a better
outcome than a dodge that cannot be trusted, and a much better outcome than one that is trusted and
wrong. What the merge changed is the cost of that stop: there is no longer a console deliverable at
this point, so a stop at M6 means a stop.
