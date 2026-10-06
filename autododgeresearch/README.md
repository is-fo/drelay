# autododge research — an in-process hook for Darza's Dominion

This directory is the research and implementation plan for replacing `drelay`'s **reactive**
auto-nexus with a hook that runs **inside the game process** and can act before the damage lands.

It is research plus a plan. No hook code has been written and nothing in the game has been modified.

Branch: `autododge-research`. The proxy stays Java; the hook is C++.

---

## What "proactive" means here, precisely

The word does a lot of work in this project, so it is worth pinning down before anything else, because
the honest answer is narrower than it sounds.

**Reactive** is the current rule: it reads the server's `GmHealthUpdate`, which reports damage *after*
the server has applied it, and escapes. Its decision point is downstream of the damage.

**Proactive** here means exactly one thing: the decision point moves **upstream of the damage**, to the
instant the client itself resolves a hit — which is measurably one server round trip *before* the
server applies it. It does **not** mean the damage is prevented. The hook's two tools are, in order:
**move out of the way** (dodge), and **leave before it lands** (preventive escape). Both are actions
taken *before* the damage, which is what makes them proactive; neither makes damage impossible.

And the scope of the improvement is bounded by measurement, not by hope: **74 % of measured HP falls
have no client-side hit claim at all**, so for most damage there is no earlier decision point to move
to. The hook is proactive wherever the client is the first to know, and helpless wherever the server
is. [RISKS.md](RISKS.md) §2b and §5 list those cases individually.

---

## The answer in one page

### The frustration was real, and the reason is now measured

The current auto-nexus reads the server's `GmHealthUpdate` and escapes when HP is low. That packet is
a **notification of damage already applied**, not a warning — so the rule can never act before the
damage, and no amount of proxy logic changes that. Measured over the recorded sessions:

| quantity | value |
|---|---|
| `GmHealthUpdate` reflecting a hit, after the client resolved that hit | **+55.5 ms median, +98.1 ms p90** |
| one-way client→server leg (inferred from RTT) | **31 ms** fast cluster / **113 ms** slow cluster |
| server tick period | **200 ms** |
| worst burst inside one 200 ms window (measured over a lookahead window) | **493 HP = 81.1 % of max, ending in death** |
| `Escape` → `EscapeAck` at the relay | **290 ms median** |

> **A correction to an earlier reading of this project.** It was previously stated that the client
> sends no damage acknowledgement, and therefore that suppression was not an option. That is **half
> wrong**: the client *does* declare projectile hits, in `GmHit`/`ProjHit` (id 102), whose `ObjectId`
> is the local player when `PlayerProj = 0` — verified on 161 of 580 captured packets. What remains
> true is that it is not a general escape hatch: **74 % of HP falls (571 of 775) have no such claim
> within 400 ms**, and there is no equivalent for AoE, beam, contact or tick damage. The earliest
> client-side knowledge is still the collision test, not the health packet, because the client's own
> per-hit path is purely presentational (`TIMING.md` §4.3).

### The hook buys time, not clairvoyance

The client decides a bullet is hitting the player in `Projectile.HitCharacter()`
(`Objects/Projectile.cs:1514`), computes the exact damage, uses it **only to draw a number**, and then
tells the server — which applies the damage *afterwards*. Nothing in the client ever writes
`Player.Health`; only `GmHealthUpdate` does. So at the instant of that collision decision, the damage
is still in the server's future.

That is the window. It is worth **≈55–98 ms** over the current rule, plus one frame, and the hook is on
the right side of it by construction rather than by tuning.

Note what the burst measurement says about the alternative. The server emits **one reading per hit,
back to back**, not one summary per tick — so a burst is visible but only as a stream of readings
inside one or two milliseconds, and the first reading of the stream is already damage being applied
(`Game_Slave#11`: 439 HP of 608 gone in eight readings spanning 2 ms, dead one reading later). A hook
cannot act earlier than the first reading either, **unless it predicts the hit**. That is the whole
argument for the collision-anchored design rather than a threshold at the reading.

### But it does not make the nexus unnecessary in general — and this is the honest part

**74 % of measured HP falls (571 of 775) have no client-side hit claim at all.** The client only
claims hits it resolves itself — and the only claim packets in the assembly are for projectiles,
sweeps and beams. **AoE and bomb damage produces no claim whatsoever** (`Entity.HitBy(AoeData)`
contains no `SendPacket`; it resolves on a timer server-side). So for AoE, bomb detonations,
damage-over-time ticks and point-blank spawns the server is the only witness, and no in-process design
sees them coming.

So "the auto-nexus would never need to be triggered" is **half right**. A hook makes the nexus
*better and rarer* — it escapes in cases the reactive rule cannot — and it enables a real dodge for the
projectiles the client does see. It does not make damage preventable in general, and anyone promising
that is not reading the measurements.

### Why this was worth doing anyway

Three measured facts make the hook decisively better than the current rule, independent of dodging:

1. the current rule **can act on stale HP** — the recorded case is an escape injected on a 1 % reading
   **151 ms after HP had already reached zero**;
2. the current rule **cannot distinguish "a hit landed" from "a hit is coming"**, so it must be tuned
   conservatively in the direction that costs runs;
3. a dodge is genuinely achievable here, and was **not** achievable through packets: the stream never
   re-broadcasts a projectile's position, so a packet-only dodge is dead reckoning with unbounded
   error. In-process, the true position is one field read away.

### Why it is tractable

Five facts, all verified against the shipped binaries:

| | |
|---|---|
| `DarzasDominion.dll` is **pure IL** — no ReadyToRun, no native code | every game method is JIT-compiled, so a JIT-level hook sees all of it |
| `System.Runtime.TieredCompilation` is **`false`** | each method is compiled **once**; a patch is permanent, and the whole tier-promotion hazard does not exist |
| the JIT's vtable is **four slots, `compileMethod` at 0, `getVersionIdentifier` at 2** | verified by disassembling the shipped `clrjit.dll` — note the community tools hardcode slot **4**, which was right for .NET Core 3.1 |
| the client **never applies damage to itself** | so the collision decision is genuinely ahead of the server's damage |
| no anti-cheat, no integrity check, no driver | a plain self-contained .NET 9 apphost |

And the two things that would normally be the hard part are avoidable:

- **No trajectory reimplementation.** `ProjDesc.GetPosition` handles straight, wavy, parametric,
  boomerang, orbiting, curving, accelerating and bouncing families. The hook **calls it** instead of
  reproducing it — which also makes prediction exactly consistent with collision by construction. The
  reference engine reached the same conclusion independently.
- **No damage reimplementation.** Every damage constant has been recovered from the decrypted constant
  pool, so it *could* be recomputed — but the hook **calls `Entity.GetDamageTaken`**, because eight
  mitigation steps read live state and being subtly wrong means escaping one hit too late.

---

## The documents

Read in this order for the argument; the last four are the plan.

| document | what it is | read it when |
|---|---|---|
| **[CLIENT-FINDINGS.md](CLIENT-FINDINGS.md)** | the verified client, with `file:line` for every claim — the frame loop, the projectile model, the collision predicate, the damage arithmetic, the movement law, and the four best hook points | you want to know what is actually true and how we know |
| **[TIMING.md](TIMING.md)** | the measured window: health cadence, RTT, server tick, the largest invisible burst, and a scenario-by-scenario deadline table | you want the numbers that justify the whole approach |
| **[CLIENT-MAP.md](CLIENT-MAP.md)** | the exhaustive reverse-engineering map (3,389 lines): every field, every accessor, the candidate hook list, and ten ranked unknowns each paired with the experiment that settles it | you are implementing and need a specific answer |
| **[HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md)** | how the hook gets in and stays in: JIT detour vs profiler, injection, identity by IL hash, GC and pooling safety, version tagging, the frame budget, and the startup self-checks | you are building the hook |
| **[DODGE-ALGORITHM.md](DODGE-ALGORITHM.md)** | what it computes: the threat set, trajectory sampling, the solver, the temporal test, the intent ladder, damage estimation, and the preventive-escape decision. §12 is the borrow/don't-borrow ledger against the reference engine | you are building the dodge |
| **[JAVA-BRIDGE.md](JAVA-BRIDGE.md)** | keeping the proxy in Java: why the decision cannot cross the bridge, whether JNA is needed (it is, minimally), sequence-stamped shared memory, and failure detection | you are wiring the hook to the relay |
| **[BRIDGE-RESEARCH.md](BRIDGE-RESEARCH.md)** | the source-cited evidence behind the bridge design: JNA / JNI / FFM benchmarks, Windows named-pipe and shared-memory limitations verified against the OpenJDK and Microsoft sources, and the measured loopback/pipe numbers | you want to check a bridge claim or revisit the transport choice |
| **[IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md)** | **the deliverable**: six phases with spikes, deliverables, exit criteria, a test list, milestones and the decision point | you want to know what to do on Monday |
| **[VERIFICATION.md](VERIFICATION.md)** | the playbook: five layers from the static gate to the on-the-wire checks, what each can and cannot establish, and the falsification test that must pass before the model may be trusted | you are about to run something live |
| **[RISKS.md](RISKS.md)** | the risk register, the verified dodge-vs-escape interaction bug, and an explicit list of what this cannot do | you are deciding whether to start |

Supporting evidence lives in [`il/`](il) (the IL dump of the exact build), and the tools that produced
every number are in [`tools/`](tools) — see below.

---

## The plan, in six lines

| phase | question | risk | estimate |
|---|---|---|---|
| **0** | what are we targeting, and can we prove it has not changed? | very low | 1–2 days |
| **1** | can native code get into this process, early, and survive? | **high** | 3–5 days |
| **2** | can we observe the truth, and does our model match it? | medium | 1–2 weeks |
| **3** | **can a predictive nexus beat the reactive one?** | low given 2 | 1 week |
| **4** | can we move the player legitimately? | **highest** | 2–4 weeks |
| **5** | does the relay see the hook, and is that useful? | low | 3–5 days |

**Phases 0–3 are the recommended commitment.** They deliver the headline benefit — a nexus that acts
on knowledge of an incoming lethal hit rather than on the aftermath — and they do not depend on the
movement question at all. Phase 3 is worth shipping on its own.

**Phase 4 is entered only after Phase 3 is live and measured.** Its risk is not the algorithm; it is
whether the server accepts hook-steered movement, which is the one question in this design with no
precedent in the reference engine and no measurement in the captures. That question can be answered
**from the existing proxy, with no hook at all**, in an afternoon — see
[RISKS.md](RISKS.md) §2a.

**Phase 2 is the decision point.** It produces the four numbers that decide whether a dodge is worth
building: how much warning prediction gives, its false-negative and false-positive rates, and its
per-frame cost. If those numbers are bad, the honest move is to stop at Phase 3 and document why.

---

## Four things worth knowing before starting

**1. The most valuable artefact is not the dodge.** It is the Phase 2 log that compares every predicted
collision against every collision the game actually made. A dodge's quality cannot be established by
unit tests — the reference engine's own audit says *"live gameplay remains unverified"* for much of its
newest code — but the *model's* correctness can be, and that log is the only thing that can tell us
whether the prediction is right. Build it before the solver.

**2. There is a verified interaction hazard waiting, scoped to challenge runs.**
`World.RequestEscape()`'s **channelled** branch cancels on movement:

```csharp
// World.cs:13043-13047
if (GameScene.GameInput.Moving) { CancelEscapeCast(); return; }
```

A dodge that steers **cancels an in-progress nexus cast in the same frame**. Precedence must be
designed before the dodge is enabled live, not patched after. **Scope it correctly, though:** that
branch is reached only with the `Impermanence` totem — ordinary escape is instant and is never
cancelled by movement. An earlier draft of this research stated that as a general hazard; it is not,
and the correction is recorded in [CLIENT-FINDINGS.md](CLIENT-FINDINGS.md) §5.3. The practical
consequence is that a test suite which never equips the totem will pass while the real bug sits
untouched.

**3. Three things are deliberately out of scope, and one of them constrains the design.** AoE and bomb
damage is left to the player; point-blank spawns are treated as a positioning problem rather than a
reaction problem (and are mostly avoidable); and there is **no melee or contact damage** at all, which
removes a row rather than adding one.

The AoE decision is the one that costs something, because it makes **intent preservation a
requirement rather than a preference**: if the dodge fights the direction the player is steering, it
has taken away the tool they need for the channel it does not cover. The mechanism that keeps the
trade safe is a single rule — **an AoE volume may affect a candidate's score, but it must never remove
a candidate** ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §5.7, [VERIFICATION.md](VERIFICATION.md) §3
invariant I11).

**4. The reference repo was worth reading, but almost none of it should be copied.**
[Evergreen-Techworks/realm-engine-client](https://github.com/Evergreen-Techworks/realm-engine-client)
is a mature dodge engine for a different game, and what transfers is its **reasoning**, not its
numbers: a two-strike projectile retirement policy, fixed-step trajectory sampling, anchoring on the
live position every frame, freezing past the end of a trace rather than extrapolating, the
time-parametrised polyline as the sensor/solver interface, safety as a hard constraint with preference
as a score over the safe set, and degrade-the-candidate-set-never-the-tests under budget pressure.

What must **not** be copied: its time constants (calibrated to a 5 Hz server), its hit geometry (a
per-axis square, where our client's test is verified to be `DistanceSquared < r²` on circles), its
player half-extent (its player is a point; ours has a real radius the game adds for us), and its entire
IL2CPP offset layer. [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §12 has the full list, both directions.

---

## Tools and evidence in this directory

Everything is reproducible. Nothing here is hand-waved from a blog post.

**Deterministic, runnable now:**

| tool | does |
|---|---|
| **[`tools/Fingerprint.ps1`](tools/Fingerprint.ps1)** | **the Phase 0 gate, and the first thing to run before any session.** Nine-plus static checks — tiered compilation, IL-only status, the assembly's size *and SHA-256*, the `clrjit` vtable's code-slot count, both runtime DLL versions — and a non-zero exit on any mismatch. It resolves `getJit` from the export directory itself, so it needs no other tooling. Mutation-tested: a same-size byte flip in the assembly makes it fail loudly. Its expected values live in [`known-build.json`](known-build.json) |
| [`tools/Test-JitVtable.ps1`](tools/Test-JitVtable.ps1) | locates `clrjit!getJit`, resolves `&g_CILJit`, and dumps the `ICorJitCompiler` vtable. **This is Phase 0's most important instrument** — it proves the four-slot layout and which slot holds `compileMethod` on the exact runtime build |
| [`tools/Test-ReadyToRun.ps1`](tools/Test-ReadyToRun.ps1) | reads the COR20 `ManagedNativeHeader` and reports IL-only vs ReadyToRun. Verified on this install: `DarzasDominion.dll` and `MonoGame.Framework.dll` are **IL-only**; `System.Private.CoreLib.dll` **is R2R** |
| [`tools/Get-PeExports.ps1`](tools/Get-PeExports.ps1) | PE export lister; used to confirm `coreclr.dll` exports `GetCLRRuntimeHost` and `g_dacTable`, i.e. that in-process DAC reads are on the table |
| [`tools/poolresolve.py`](tools/poolresolve.py) | resolves `hehe(N)` constant lookups in the IL to real values, by pairing each index with the return type of the call that consumes it. This is what recovered the movement law and the damage law |
| [`tools/il_constants.py`](tools/il_constants.py) | resolves obfuscated literal *values* straight out of the IL dump |
| [`tools/g.py`](tools/g.py) | bounded grep-with-context, written because `World.cs` is 24,381 lines and cannot be read whole |
| [`pe_probe.py`](../work/pe_probe.py) | PE/CLI/R2R probe — proves the assembly is pure IL with no ReadyToRun block |
| [`md_probe.py`](../work/md_probe.py) | .NET metadata table census — 23,743 methods, 25,105 fields |
| [`_pooldecode.py`](../work/_pooldecode.py) | decrypts the obfuscator's constant pool (1035 ints / 16 longs / 787 floats / 229 doubles), verified against eight independently-known packet ids |

```powershell
# the one command to run before any session
.\autododgeresearch\tools\Fingerprint.ps1
```


**Produced during this research** (the measurement scripts behind [TIMING.md](TIMING.md) and
[CLIENT-MAP.md](CLIENT-MAP.md)): `health_cadence.py`, `rtt_and_tick.py`, `report_numbers.py`,
`projhit_damage_report.py`, `damage_causality.py`, `hitreport_and_deaths.py`,
`field_order_solver.py`, `validate_varint.py`, `client_tick.py`, `report_numbers.py`, and others,
plus their raw outputs as `_*_raw.txt` / `_numbers.txt`.

**Evidence files:**

| file | what |
|---|---|
| [`il/client-5.8.2.il`](il) | the full IL dump of `DarzasDominion.dll` for build 5.8.2 (85 MB) — the input to `poolresolve.py` and the source for the per-target IL hashes the hook will match on |
| `_numbers.txt`, `_cadence_raw.txt`, `_rtt_raw.txt`, `_projhit_raw.txt`, `_deaths_raw.txt` | the raw measurements [TIMING.md](TIMING.md) is built from |

---

## What is deliberately not in this directory

- **No hook code.** This is a plan, and Phase 0 of it says the first thing to write is the fingerprint
  gate, not the hook.
- **No changes to the Java relay.** The bridge design in [JAVA-BRIDGE.md](JAVA-BRIDGE.md) is
  additive by construction: the relay runs identically with and without the hook.
- **No game files were modified**, and none need to be on the recommended injection path.
- **No claims without a `file:line` or a measurement.** Where something is genuinely unknown it is
  marked as such and paired with the experiment that would settle it — [CLIENT-MAP.md](CLIENT-MAP.md)
  §10 lists them all, and two of the three that were "blocking" have since been resolved by the
  constant-pool decode.
