# How much time does a hook actually have?

**Answering one question:** between "a hit against the local player is decided
client-side" and "the server's damage has already landed", how long is the window,
and where inside it can a proxy or an in-process hook still act?

Everything below is measured from the captures already in `work/`, or quoted from
the decompile with file:line. Where a number cannot be measured, the text says so
and says what to record. No number is invented.

---

## 0. Bottom line

The window is real and it is **not** measured in server ticks. The client decides
its own hit *before* the server has applied the damage, and the whole window is
one client→server network leg plus the server's tick quantisation:

| quantity | measured | source |
|---|---|---|
| client decides "that projectile hit me", sends it | **t = 0** | `Projectile.cs:1573-1587` |
| server's HP reading that reflects it arrives at the proxy | **+55.5 ms median** (p10 10.9, p90 98.1, max 1478) | §2.2, 160 captured reports |
| server tick period (the quantum that delay sits inside) | **200 ms** (median; p25 199, p75 201, n=6087) | §1.1 |
| one-way client→server leg implied by the RTT | **31 ms** (fast cluster) / **113 ms** (slow cluster) | §3 |
| Escape → EscapeAck, both seen at the relay | **290 ms median** (n=8, range 60-413) | §3.4 |
| worst burst a reactive rule must survive | **493 HP = 81.1% of max in 150 ms**, ending in death | §2.4 |

**Verdict in one sentence.** The hook's deadline advantage does not come from the
health stream — that stream is *notification of damage already applied* and no
amount of hooking makes it earlier; it comes from sitting at the instant the client
itself declares the hit (`Projectile.cs:1573`), which is a full client→server
network leg (**31-113 ms**) plus the client's remaining frame (**≈10-17 ms**, not
measurable from these captures) *before* the server reads that claim, and before
any packet-only proxy that is not co-located with the client can even see the
claim leave.

---

## 1. Method and sources

| file | what it is | used for |
|---|---|---|
| `work/logs/events-20261004-235440-1500.jsonl` | relay event log, 6.65 MB, 2 915 `HealthUpdate` readings | §1, §2, §3 |
| `work/logs/events-20261005-012701-2400.jsonl` | relay event log, 10.1 MB, 2 775 readings | §1, §2, §3 |
| `work/logs/events-20261005-023129-2000.jsonl` | relay event log, 2.88 MB, 459 readings | §1, §2, §3 |
| `work/logs/relay-*.log` | the same three sessions in transcript form | cross-check |
| `work/logs/nexus-*.jsonl` | the auto-nexus decision lines | §2.2 worked example |
| `work/decomp/**` | decompiled .NET client | §4, §5 |
| `work/live-digest.jsonl` (23 MB) | bulk `GmUpdate` digest, no `HealthUpdate` values | object-stat cross-check only |
| `work/replay.txt` (23 MB) | 17 870 `H` records + 26 278 `U` payloads | see caveat below |

**Caveat on `work/replay.txt`, and it matters.** Its `H <sess> <ms> <health>`
records do **not** reproduce the `HealthUpdate` readings in the checked-in event
logs for the same session names. For `Game_Slave#3`, the log file
`events-20261004-235440-1500.jsonl` has 293 readings starting at `ms=6899` with
`health=707` (`work/logs/relay-20261004-235440-1500.log:2` reads
`4600830B830B0000`), while `replay.txt` has 710 readings for the same session name
starting at `ms=48201` with `health=375`. `work/make_replay.py` reads
`G:\drelay\work\logs\events-*.jsonl` (a path that exists), so `replay.txt` was
generated from a log set that is not the one checked in. **No number in this
report is taken from `replay.txt`.** All cadence and drop figures come from the
`events-*.jsonl` files, which are internally consistent with the `relay-*.log`
transcripts byte for byte.

**Which files are real sessions.** There are ~105 `events-*.jsonl` files. Only the
three above contain a world with `HealthUpdate` readings. The rest are, by their
session names: 45 files with only `Queue#1`/`Queue#2` (the queue service, never a
world), 24 synthetic loopback tests (`Test#1`), and 21 with a `Game#1` session that
never left the lobby. All of those were examined and excluded on the presence or
absence of `HealthUpdate`, not on filename.

**Scripts.** Every number is reproducible:

```
python autododgeresearch/tools/report_numbers.py        # all of §1, §2, §3
python autododgeresearch/tools/hitreport_and_deaths.py  # deaths + GmHit capture
python autododgeresearch/tools/health_cadence.py        # cadence detail + histogram
python autododgeresearch/tools/rtt_and_tick.py          # §3 RTT + tick, per session
python autododgeresearch/tools/projhit_damage_report.py # §2.2 report -> reading
python autododgeresearch/tools/damage_causality.py      # §2.3 causal test
python autododgeresearch/tools/falls_without_claim.py   # §2.3 counter-test
python autododgeresearch/tools/burst_window.py          # §2.4 burst spans
python autododgeresearch/tools/field_order_solver.py    # §5 wire order of GmHit
python autododgeresearch/tools/projhit_identity.py      # §5 who the report names
python autododgeresearch/tools/validate_varint.py       # decoder proof, 6149/6149
python autododgeresearch/tools/client_tick.py           # §1.4 outbound cadence
```

`parse_replay.py` holds the shared varint and log readers. The varint decoder is
transcribed from the relay's own `src/main/java/networking/GameReader.java:138-153`
("6, 6, 7, 7, …") and is proved by re-decoding all **6 149** `HealthUpdate` payloads
recorded in the event logs and comparing against the relay's own decoded
`maxHealth`/`health`/`shield`/`barrier`: **6149/6149 exact, 0 mismatches.**

---

## 2. The health-update cadence and its honesty

### 2.1 Inter-arrival distribution of `GmHealthUpdate` (id 70, `0x46`)

Wire layout: `[LE ushort id=70][varint MaxHealth][varint Health][varint Shield]
[varint Barrier]`; 4 signed varints, no object id — the packet is always about the
local player. Measured over all three real sessions, keeping only gaps that do not
straddle a `MapInfo` (a new world re-sends a full reading):

| scope | n | p10 | p25 | median | p75 | p90 | max |
|---|---|---|---|---|---|---|---|
| 235440, all sessions | 2 894 | 47.0 | 50.0 | **63.5** | 100.0 | 101.0 | 55 550 |
| 012701, all sessions | 2 752 | 40.2 | 49.0 | **52.0** | 100.0 | 101.0 | 42 201 |
| 023129, all sessions | 441 | 3.0 | 49.0 | **97.0** | 100.0 | 100.0 | 15 048 |
| **all three runs** | **6 087** | **46.0** | **50.0** | **53.0** | **100.0** | **101.0** | **55 550** |

`max` is not a cadence figure — it is an idle stretch. The largest single gap in the
corpus is **55 550 ms** in `Game_Slave#5` (run 235440), between two readings in the
*same* world (the session has one `MapInfo`, at `ms=46501`; the gap runs from
`707 hp` to `697 hp` at the other end of a 94-second world). The per-session `max`
values in the table are all of this kind. The distribution is **strongly bimodal**,
and that bimodality is the whole story:

| bucket | count | share |
|---|---|---|
| 0 ms (two readings in the same relay millisecond) | 138 | 2.3% |
| 1-10 ms | 380 | 6.2% |
| 11-45 ms | 81 | 1.3% |
| **46-55 ms** | **2 539** | **41.7%** |
| 56-70 ms | 58 | 1.0% |
| 71-95 ms | 103 | 1.7% |
| **96-105 ms** | **2 602** | **42.7%** |
| 106-150 ms | 84 | 1.4% |
| 151-210 ms | 54 | 0.9% |
| 211-400 ms | 6 | 0.1% |
| >400 ms | 42 | 0.7% |

So readings come at ~50 ms **or** ~100 ms, almost nothing in between: `HealthUpdate`
is **event-driven on the server's 200 ms tick**, at two per tick or one per tick
(the 0/1-10 ms buckets are pairs delivered back to back inside one millisecond).
Per-session figures for at least one full session are in
`autododgeresearch/tools/_numbers.txt` §1; `Game_Slave#9` in run 012701 is the
largest single session at **2 043 readings** (median 52 ms, p90 101 ms).

For an independent second opinion on the *same* wire, `work/capture-analysis/_timing.txt`
(an earlier capture in this repo, decoded by a different script) reports
`Game_Slave#3 S->C` p50 = 98 ms / p90 = 101 ms over 43 readings and
`Game_Slave#5 S->C` p50 = 100 ms / p90 = 100 ms over 54 readings. Same 50/100 ms
structure, different run. The claim in `docs/AUTONEXUS.md:40` of "a median of
**52-61 ms** (p10 ≈ 40-47 ms, p90 ≈ 101 ms)" is correct for the runs it used; the
combined median over all three real sessions here is 53 ms.

### 2.2 How soon after a damage event the HP reading reflects it

There is **no damage event in the stream** to timestamp against — that is the
finding of §5 — so the honest measurement uses the one client-authored damage
signal that does exist: the `ProjHit` (`GmHit`, id 102) report with
`PlayerProj = false`, which is the client telling the server "an enemy projectile
hit me" (§5 proves the field order and the meaning). The captures contain **160**
such reports. The delay from that report to the **next** `HealthUpdate` reading:

| statistic | value |
|---|---|
| n | 160 |
| min | 1 ms |
| p10 | 10.9 ms |
| p25 | 34.0 ms |
| **median** | **55.5 ms** |
| p75 | 92.0 ms |
| p90 | 98.1 ms |
| max | 1478 ms |

Control (`PlayerProj = true`, i.e. our shot hitting an enemy, n=419, which has no
causal relation to our HP): median **89 ms**, p75 **1831 ms**, p90 **3568 ms**. The
control's long tail is the signature of "the next reading happened to arrive when it
arrived"; the false-report distribution is *capped by the cadence*, which is what
you see when the report is what caused the reading to be emitted.

Worked example, `Game_Slave#14` (`events-20261004-235440-1500.jsonl`), consecutive
reads with 27 HP coming off per hit:

```
report @1516905 ms  6600BFD02395DB1CBE9B3800   hp 707 -> 680  (reading @1516967, +62 ms)
report @1516950 ms  6600ACD12397DB1CBE9B3800   hp 707 -> 680  (same reading, +17 ms)
report @1517030 ms  6600BCD22399DB1CBE9B3800   hp 643 -> 598  (reading @1517117, +87 ms)
report @1518586 ms  660090EB2380DD1CBE9B3800   hp 590 -> 563  (reading @1518667, +81 ms)
```

Sanity check on the 20 ms gap: with the client's hit at t and a fresh reading sent
up to 100 ms later, the **server has already applied the damage by the time the
proxy can see that it happened**. That is the core of the answer to the question.

### 2.3 Was the damage caused by the claim, or applied independently?

This decides whether any inbound-packet trickery can *prevent* damage, so it gets
two measurements.

**Test A (causality).** For `PlayerProj=false` reports, the immediately following
reading showed a fall **105/158 (66%)**; for the `PlayerProj=true` control,
**204/418 (49%)**. The 17-point gap over 576 events, together with a median
report→reading delay of **68 ms** against **1581 ms** for the control, is evidence
the server consumes the claim. It is not proof of trust — 34% of claims produce no
immediate fall.

**Test B (counter-test).** Do HP falls ever happen with **no** claim? Across all
three sessions, of **775** HP falls, **571 (74%)** have no `PlayerProj=false`
report within 400 ms. Whole sessions have 100% claim-free falls — `Game_Slave#11`
in run 235440: 31 falls, **0** `PlayerProj=false` reports; `Game_Slave#5` in run
023129: 25 falls, **0** reports. Those sessions' damage is AoE / beam / contact /
DoT and never passes through `GmHit`.

**Therefore:** the server clearly resolves a large share of damage on its own, so
suppressing client hit reports is not a general escape hatch. What the 66%-vs-49%
split supports — and all it supports — is that for **projectile** hits the server's
damage step is at least coupled to the client's claim, and always later than it.

### 2.4 The largest single-reading HP drop — and why that metric alone misleads

Per session (max HP taken from the session's own readings), the worst drop between
two consecutive readings:

| % of max | run | session | from (ms, hp) | to (ms, hp) | gap |
|---|---|---|---|---|---|
| **98.92%** | 023129 | `Game#12` | 358362, 9999 | 367689, 108 | 9 327 ms |
| **24.58%** | 012701 | `Game_Slave#7` | 461267, 887 | 461267, 712 | **0 ms** |
| **23.03%** | 012701 | `Game_Slave#11` | 580370, 288 | 580418, 148 | 48 ms |
| 16.45% | 012701 | `Game_Slave#9` | 564268, 608 | 564269, 508 | 1 ms |
| 14.61% | 012701 | `Game#8` | 504217, 712 | 550578, 608 | 46 361 ms |
| **14.14%** | 235440 | `Game_Slave#9` | 1322466, 807 | 1322466, 707 | **0 ms** |
| 13.98% | 012701 | `Game_Slave#15` | 655966, 608 | 658519, 523 | 2 553 ms |
| 10.03% | 023129 | `Game_Slave#9` | 328084, 476 | 328183, 415 | 99 ms |
| 6.60% | 235440 | `Game_Slave#14` | 1537871, 707 | 1538271, 660 | 400 ms |
| 2.83% | (six sessions) | | | | 47-20 798 ms |

Two of those rows are **artefacts, not damage** and must not be read as bursts:

* `Game#12` 9999 → 108 with a 9 327 ms gap is a `maxHealth` **sentinel**:
  `maxHealth = 9999, health = 9999` occurs exactly **4 times** in the whole corpus,
  once per session, always as that session's first reading
  (`Game_Slave#11` @350833, `Game#12` @358362, `Game_Slave#13` @372385,
  `Game#14` @367689/374 — all run 023129), and the max-health distribution for that
  run is otherwise exactly `608` (442), `108` (13), `9999` (4). Treat 9999 as
  "max not known yet", not as a 98.9% burst.
* `Game_Slave#7` 887 → 712 and `Game_Slave#9` 807 → 707 both have **gap 0 ms** and a
  drop of exactly 175 and exactly 100 with `maxHealth` 712/707: the earlier reading
  used a stale, larger `maxHealth` (887 / 807), so the "drop" is a max-HP
  correction, not damage.

Excluding those, the **worst genuine drop between two consecutive readings is 23.0%
of max (140 HP of 608) in 48 ms** (`Game_Slave#11`, run 012701), then 16.4%
`Game_Slave#9` (012701) and 14.1% `Game_Slave#9` (235440). Median worst-drop per
session is **4.9%**; p75 14.1%, p90 22.4%.

**That metric is not the whole story, and this is the most important correction in
this report.** In run 023129, `Game_Slave#11` loses **439 HP (72.2% of 608) in
seven readings inside 2 ms**, and the character is dead one reading later:

```
350634  439/608   72%   <- the relay's rule sees this as "72%, not below 35%"
350634  385/608   63%
350634  331/608   54%
350634  277/608   46%
350635  223/608   37%
350635  169/608   28%   <- injection fires here
350635  115/608   19%      "only 0 ms since the last injection"
350635   61/608   10%
350635    7/608    1%
350636    0/608    0%   <- dead
```

Each *consecutive* increment is only 54 HP (8.9%), so a consecutive-reading metric
understates the event by 8x. The drop that matters is the **span of the burst**, and
that is what a reactive rule's rate limiter and budget gates actually see. Measured
over a lookahead window (`autododgeresearch/tools/burst_window.py`):

| window | worst burst in the corpus | readings in it | ends at 0? |
|---|---|---|---|
| 50 ms | **439 HP = 72.2% of max** (`Game_Slave#11`, 023129) | 8 | **yes** |
| 50 ms | 236 HP = 38.8% of max (`Game_Slave#7`, 023129) | 5 | no |
| 100 ms | 439 HP = 72.2% (`Game_Slave#11`, 023129) | 8 | **yes** |
| 200 ms | **493 HP = 81.1% of max** (`Game_Slave#11`, 023129) | 12 | **yes** |
| 200 ms | 383 HP = 63.0% (`Game_Slave#15`, 012701) | 9 | no |

The server emits one reading per hit, back to back, not one summary reading per
tick — so a burst is *visible* but only as a stream of readings inside one
millisecond, and the first reading of the stream is already the damage being
applied. A hook cannot act earlier than the first reading of the stream either,
unless it predicts the hits.

### 2.5 Can a burst kill from full?

**Not observed from full, but very nearly observed, and the corpus is not large
enough to say "never".**

* There are exactly **two** real deaths (a reading at `hp <= 0`) in the corpus, both
  in run 023129: `Game_Slave#11` @350636 (previous reading @350635, hp 7 → 0, gap
  1 ms) and `Game_Slave#13` @387084 (previous @387034, hp 57 → 0, gap 50 ms). The
  third `hp <= 0`-looking row is the 9999 sentinel of §2.4 and is not a death.
* The largest *observed* drop that ends in death is **493 HP of 608 (81.1%) in
  150 ms** (`Game_Slave#11`), and **439 HP of 608 (72.2%) in 2 ms**. Neither starts
  from full: the bursts begin at 493/608 and 439/608.
* No adjacent pair has the first reading at full (or ≥90% of max) and the second at
  ≤10%. The closest approach is the `Game_Slave#13` death, **57/108 → 0 in 50 ms**
  (52.8%).
* Reason the corpus cannot settle it: the auto-nexus rule fires before that happens
  in every recorded world — all **18** injections across the three runs happened at
  1%, 24%, 25%, 26%, 28%, 29%, 32%, 33% or 34% HP, and each one ended the world
  shortly after. **NOT MEASURABLE from the available captures — needs a live run**
  (§8 item 2).

What *is* demonstrated, and is the real failure mode, is that the rule can act on a
reading that is already obsolete or already fatal. Two worked cases from
`work/logs/nexus-20261005-023129-2000.jsonl`:

```
--- Game_Slave#11, a burst inside one tick -------------------------------------
350634 ms  HealthUpdate  hp 439/608  (72%) -> "no nexus: 72% is not below the 35% threshold"
350634 ms  HealthUpdate  hp 385/608  (63%) -> "no nexus: 63% ..."
350634 ms  HealthUpdate  hp 331/608  (54%) -> "no nexus: 54% ..."
350634 ms  HealthUpdate  hp 277/608  (46%) -> "no nexus: 46% ..."
350635 ms  HealthUpdate  hp 169/608  (28%) -> inject Escape 4200
350636 ms  HealthUpdate  hp   0/608  ( 0%) -> "no nexus: HP is zero (dead or spirit form)"

--- Game_Slave#13, action on a post-death echo --------------------------------
387034 ms  HealthUpdate  hp  57/108  (53%) -> "no nexus: 53% is not below the 35% threshold"
387084 ms  HealthUpdate  hp   0/108  ( 0%) -> "no nexus: HP is zero (dead or spirit form)"
387235 ms  HealthUpdate  hp   1/108  ( 1%) -> "NEXUS: 1% HP is below the 35% threshold"
387235 ms  inject        Escape 4200
```

In the second case the character died at `.084` and the relay injected at `.235` —
**151 ms after death**, on a 1-HP reading that was echoed after the death message.
In the first case the escape went out 1 ms before the death reading, i.e. into a
tick that had already applied all of the damage.

### 2.6 The client's own outbound cadence (why the hook's frame matters)

Measured from consecutive `GmMove` (id 74) `Time` fields — `Time` is
`World.ClientTime = (int)ClientTime.LogicTotal`, the client's accumulated *logic*
time (`work/report-handlers.md` §C.7):

| delta | count |
|---|---|
| 195 ms | 318 |
| 196 ms | 437 |
| 199 ms | 376 |
| **200 ms** | **2 242** |
| **201 ms** | **2 150** |
| 202 ms | 423 |
| everything else | ~500 |

The client's outbound packet cadence is the **server's 200 ms tick, not its frame
rate** — the relay's view cannot see the render frame at all. The client frame
period is therefore **NOT MEASURABLE from the available captures — needs a live
run**; record the distribution of `ClientTime.LogicTotal` deltas inside one tick
(equivalently, count `World.Update` calls per `GmUpdate`). The value matters: it is
the hook's worst-case latency from "collision would be detected this frame" to
"the hook's own escape write lands".

---

## 3. Round-trip time

### 3.1 What is measured vs inferred

Everything in this section is **observed at the relay**, i.e. at the same
observation point a packet proxy occupies. It is therefore a direct measurement of
what a proxy gets, and only an upper bound on what the client's NIC sees.

Three clocks are present, and they answer different questions:

| pair | what the delta really is | n | median |
|---|---|---|---|
| `CheckPing` (106, C→S, empty) → `CheckPingAck` (107, S→C, empty) | close to a true application round trip: request goes out, server answers directly | 776 | see below |
| `Ping` (92, S→C) → `Pong` (72, C→S) | the **relay's own observation skew**, not the network — the client answers the server's ping within a millisecond | 1 555 | **0 ms** (p90 1 ms, max 5 ms) |
| `Escape` (66, C→S, `4200`) → `EscapeAck` (158, `9E0001`) | one round trip **plus** the server's decision, both endpoints observed here | 9 | **290 ms** |

`Ping`→`Pong` measuring 0 ms is itself the proof that the relay sees both
directions of a session with negligible internal skew, so the other two pairs are
network/server time, not relay time.

### 3.2 `CheckPing` → `CheckPingAck`

The distribution is **bimodal by destination, not noisy**, and the split is stable
across all three runs. Sessions map to destinations via each run's `RETARGET` lines:

* every `Game#NN` → `3.71.176.153:6411` or `18.145.215.213:6411`
* every `Game_Slave#NN` → `18.145.161.25:6410`

that mapping holds without exception in all three runs
(`relay-20261004-235440-1500.log`, `relay-20261005-012701-2400.log`,
`relay-20261005-023129-2000.log`).

| cluster | n | min | p10 | p25 | median | p75 | p90 | max |
|---|---|---|---|---|---|---|---|---|
| `Game_Slave#NN` (6410) | 583 | 29 | 44.0 | 51.5 | **63.0** | 74.0 | 99.6 | 304 |
| `Game#NN` (6411) | 193 | 176 | 187.0 | 198.0 | **226.0** | 277.0 | 320.8 | 380 |

Per-session medians inside the fast cluster: 46-86 ms
(`Game_Slave#17` 46.0, `Game_Slave#13` 50.0, `Game_Slave#11` 58.0,
`Game_Slave#7` 59.0, `Game_Slave#14` 61.5, `Game_Slave#9` 64.0,
`Game_Slave#5` 65.0). Inside the slow cluster: 188-314 ms (`Game#10` 188.0,
`Game#8` 198.5, `Game#4` 219.5, `Game#6` 235.0, `Game#2` 267.0).
**The clearest counter-example is an intra-run inversion.** Both sessions dial the
same destination under this grouping, yet in run 012701 `Game_Slave#3` measures a
**68.5 ms** median (n=4) while `Game_Slave#5` measures **203.5 ms** (n=20) — and in
the other two runs it is the other way round (`Game_Slave#3` also shows 219.0 ms in
235440, while `Game_Slave#5` shows 61.5 / 51.0 ms in 235440 / 023129). So the
fast/slow split is not purely a property of the `6410` vs `6411` route; the same
route varied by run. The relay has no route-level instrumentation to explain it, so
this is recorded as unexplained run-to-run variance, and the two cluster medians
(63.0 ms and 226.0 ms) are quoted as the observed range of the two legs rather than
as fixed constants.

**Inferred, one-way:** halve the RTT, since the two directions are unmeasured
individually. **31 ms** for the fast cluster (31.5 = 63.0/2), **113 ms** for the slow
one (113 = 226.0/2). Stated as an inference, not a measurement: the asymmetry is
unknown, and a relay sitting between client and server cannot separate the two legs.
Given the run-to-run variation just noted, treat 31-57 ms as the plausible one-way
range for a well-connected client and 90-113 ms for the `6411` route.

### 3.3 Server tick

Measured independently as the arrival cadence of `GmUpdate` (id 1) on the
server→client stream:

| run | n | p10 | p25 | median | p75 | p90 |
|---|---|---|---|---|---|---|
| 235440 | 2 636 | 198.0 | 199.0 | **200.0** | 201.0 | 202.0 |
| 012701 | 3 161 | 198.0 | 199.0 | **200.0** | 201.0 | 202.0 |
| 023129 | 1 831 | 199.0 | 200.0 | **200.0** | 200.0 | 201.0 |

Exactly 5 Hz, matching the reference engine's `kServerTickSec = 0.2f`. One further
consequence, from `work/report-handlers.md:406-407`: `GmUpdateAck.Time` is
`LastClientTime`, i.e. the **previous** tick, so the server's round trip and its
tick quantisation are both present in anything the client acks.

### 3.4 `Escape` → `EscapeAck`, and what it implies for the deadline

Every pair where both ends were observed at the relay (9 events; the other injections
in these runs ended a world, so the ack never came):

| run | session | Escape @ | EscapeAck @ | delta |
|---|---|---|---|---|
| 235440 | `Game#4` | 41 861 | 42 140 | 279 ms |
| 235440 | `Game_Slave#20` | 1 821 721 | 1 822 019 | 298 ms |
| 235440 | `Game_Slave#3` | 37 297 | 37 357 | **60 ms** |
| 235440 | `Game_Slave#5` | 140 319 | 140 609 | 290 ms |
| 235440 | `Game_Slave#9` | 1 402 991 | 1 403 404 | 413 ms |
| 012701 | `Game#8` | 531 753 | 532 016 | 263 ms |
| 012701 | `Game_Slave#3` | 52 937 | 53 074 | 137 ms |
| 023129 | `Game_Slave#11` | 357 450 | 357 726 | 276 ms |
| 023129 | `Game_Slave#13` | 404 193 | 404 481 | 288 ms |

median **290 ms**, range 60-413 ms. The 60 ms and 137 ms pairs sit *below* the same
session's RTT cluster, which is consistent with the server answering a client packet
that arrived just before a tick boundary — i.e. the ack is quantised by the 200 ms
tick, so it is an upper bound on the round trip rather than a clean estimator of it.
`docs/AUTONEXUS.md:44` reports "245-305 ms" for the same quantity in its own run;
the 9-pair ensemble here is consistent with that.

**Deadline arithmetic.** Let the server apply damage to the local player at its own
clock time `T`. Anything the client fires at real time `t` is processed by the
server at the first tick boundary at or after `t + one_way`:

```
T_earliest_effect = next_tick_boundary( t + one_way )
necessary condition for arriving before the server's damage step:
    t + one_way  <  T
```

Substituting the measured values, with `one_way ≈ 31 ms` (fast cluster, inferred):

* To influence the tick that applies damage, a packet must leave the client
  **≥ 31 ms before `T`**, and on average **31 + 100 = 131 ms** before `T` because the
  tick grid is unknown from outside and uniform (200 ms grid, mean half-period 100 ms).
* The relay-observed `Escape`→`EscapeAck` of **290 ms** is the full `2 × one_way`
  plus server-side decision and tick quantisation, and is the number to use when
  asking "does the escape beat the next damage": an escape emitted at `t` is
  *answered* ~290 ms later, and the server has had a tick boundary in between.
* The current reactive rule acts **on** a `HealthUpdate`, i.e. at `t ≈ T + one_way`
  (the reading is generated after the damage is applied). It is therefore always
  **one one-way leg + up to one tick too late to prevent anything** — it can only
  react to damage already taken.

---

## 4. The client's own hit timing

### 4.1 When the client decides a hit, and where the code is

`work/decomp/DarzasDominion.Darza.Game.Objects/Projectile.cs`, `HitCharacter()`
(called from `Hit()` at `:1505`, which is called from `LateUpdate` at `:1246`):

```csharp
// Projectile.cs:1514-1517
private bool HitCharacter()
{
    int clientTime = World.ClientTime;       // the client's own logic clock

// Projectile.cs:1573-1587
    if (DistanceSquared(World.Player.Position) < num2 * num2 && 0 == 0
        && CanHitEntity(World.Player.GameId, clientTime) && 0 == 0)
    {
        bool num3 = RegisterEntityHit(World.Player.GameId, clientTime);
        World.Player.HitBy(this);            // cosmetic: damage numbers, flash, sfx
        if (Prefs.close_menus_on_damage && 0 == 0) base.Scene?.OnDamage();
        Client.SendPacket(new GmHit
        {
            Time = clientTime,               // <-- the client's clock
            ProjectileId = GameId,
            ObjectId = World.Player.GameId,  // <-- targets the LOCAL PLAYER
            PlayerProj = PlayerOwned         // <-- false for an enemy shot
        });
        if (num3 && 0 == 0) return ... 1 ... != 0;
    }
```

and the caller:

```csharp
// Projectile.cs:1246
else if ((Desc.Telegraph <= ... 7 || (double)num >= Desc.Telegraph * ... 24)
         && Hit() && 0 == 0)
{
    Remove();
    return;
}
```

`num2` is set at `:1597` (`num2 = World.Player.Radius + Radius;`) or at `:1555`
(`num2 = MathF.Max(num2, (float)Desc.Proximity);`). The test is a circle overlap
against `World.Player.Position` — the client's **locally predicted** position, not
the server's.

### 4.2 Is the collision test cosmetic, or ahead of the server?

**It runs ahead of the server, and it is also cosmetic — both at once, and that is
the whole point.**

* It is a **claim**, not a resolution: it emits `GmHit` to the server (`:1581`,
  `:1687`, and `AoeBomb.cs:287,323`) and never writes the outcome to HP. The client
  derives a *predicted* damage number only for display, inside
  `Entity.HitByProjectile` (`Entity.cs:2459`), which ends in
  `World?.ShowDamageMessage(...)` (`Entity.cs:2566-2580`) and `PlaySfx`, using a
  locally computed `GetDamageTaken(...)`. Nowhere there is `Health` written.
* It therefore runs **at the client's local collision instant**, which precedes the
  server's own resolution by at least the client→server leg (`one_way`, §3), and by
  more if the server re-checks against the authoritative position from its last
  received `Move`.
* `World.ClientTime` — the timestamp the client puts in the claim — is the client's
  own logic clock (`World.cs:9505  ClientTime = (int)time.LogicTotal;`), not a
  server clock, which is why a proxy can use `GmHit.Time` only as a client-side
  ordering key.

### 4.3 Does anything in the client ever decrement `Player.Health`?

**Confirmed: no. Only the `GmHealthUpdate` handler ever sets `Player.Health` to a
server value, and nothing decrements it.** Evidence: an exhaustive search of the
whole decompile for assignments to `Health`/`Player.Health`.

Searches run (via `grep`, over all `*.cs` in `work/decomp`):

```
\.Health\s*(-=|\+=|=)        -> 34 matches, all enumerated below
Health\s*[-+]=               -> 4 matches, all `target.Health += source.Health`
                                (item tooltip stat aggregation, not gameplay)
```

The complete set of writes to a `Health` field in the client:

| file:line | write | gameplay? |
|---|---|---|
| `DarzasDominion.Darza.Game/World.cs:16001-16116` (specifically `:16055`) | `world.Player.Health = gmHealthUpdate.Health;` inside the registered `HealthUpdate` handler | **the only authoritative writer** |
| `World.cs:16052` | `world.Player.Health = 0;` on the `PlayerDead` branch of the same handler | death only |
| `World.cs:7905` | `Player.Health = 0;` in the local `PlayerDead` path | death only |
| `World.cs:481`, `:484` | the decompiler's duplicate closure copy of the same handler | same code |
| `...Objects.Entities/Player.cs:4951` | `Health = stat.GetShort();` in `Player.ProcessStat`, `case StatsType.Hp` | spawn / refresh, **not** per hit |
| `...Objects.Entities/Entity.cs:148`, `:168` | `Health = 25;` in base ctor / `Reset()` | initialisation |
| `...Objects.Entities/Entity.cs:431`, `:435` | `Health = stat.GetLong() / GetInt()` in `Entity.ProcessStat` | spawn / refresh for non-player entities |
| `...UI.Desktop.Tooltips/DesktopItemTooltip.cs:10033`, `...UI.Mobile.Tooltips/MobileItemTooltip.cs:9251` | `target.Health += source.Health;` | tooltip affix aggregation |
| `...Data.Affixes/AffixData.cs`, `AffixPointMath.cs`, `ItemData.cs`, `ItemQuality.cs`, `ArmorTraits.cs`, `Affixes/AffixPoints.cs`, tooltip classes | `Health` | data/UI fields on *other* types |

Neither `Entity.HitBy(Projectile)` (`:2198`), `Entity.HitBy(AoeData)` (`:2203`),
`Entity.HitBy(int, ...)` (`:2339`) nor `Entity.HitByProjectile(Projectile)`
(`:2459`) assigns to `Health`, and neither does `Projectile.HitCharacter`. The
per-hit path is **purely presentational on the client**.

Two consequences, both load-bearing for the design:

1. A hook cannot "see the damage before the client does" in `Player.Health` — the
   value is the server's, delivered already-applied. The earliest client-side
   knowledge is the **collision test** at `Projectile.cs:1573`, not the HP number.
2. Conversely, the hook does not need to guess at HP: `Player.Health` at any moment
   is exactly the last `GmHealthUpdate` value, so a hook's prediction can be seeded
   from the authoritative number and then differ from it only by the damage it has
   not yet seen. That is a much better footing than a proxy's, which must infer HP.

---

## 5. What the client tells the server: `ProjectilesAck` / hit reports

### 5.1 There is no packet whose payload is a "damage taken" value

Exhaustive check. I enumerated every class in
`work/decomp/DarzaGameNet.Packets.Game.Client/` (117 classes) and extracted every
public field plus the `hehe(n)` lookup index from `Type =>`. **No client→server
packet carries an HP, health, damage or shield field.** The complete field lists are
in the tool output of
`python -c "..."` run over that directory (the pick: `GmHit` = `Time, ProjectileId,
ObjectId, PlayerProj`; `GmAllyHit` = `Time, ProjectileId, ObjectId`;
`GmPlayerLaserHit` = `Time, LaserId, ObjectId`; `GmSweepHit` = `Time, SweepId`;
`GmProjectilesAck` = `ProjectileId, Time, Position`). Searched for the words
`Health`, `Hp`, `Damage`, `Shield` across `DarzaGameNet.Packets.Game.Client/*.cs`:
the only hits are enum members and unrelated classes.

`GmHealthUpdate` (id 70) is **server→client only** — there is no client class for
id 70. So the direction is unambiguous: HP travels from server to client and never
the other way.

### 5.2 `ProjectilesAck` (id 64, client→server) — positional/clock, not damage

`DarzaGameNet.Packets.Game.Client/GmProjectilesAck.cs` (`Read` at `:55`,
`Ack => true` at `:17`): fields in order **`ProjectileId`, `Time`, `Position`**.
Exactly one send site in the whole client, `World.cs:10989`, inside the drain of
`GmProjectiles`:

```csharp
// World.cs:10989, quoted in work/report-handlers.md §C.1
Client.SendPacket(new GmProjectilesAck
{
    ProjectileId = _ECF0_F129_ED83_ECC8_F588.ProjectilesId,
    Time = ClientTime,
    Position = Player.GamePoint        // <-- the client's own position
});
```

So every `GmProjectiles` (id 86) from the server is answered with "this is where I
am, at this clock" — which is *why* the server can resolve a projectile's geometry
against the client's position one tick later, and why the client's hit claim
(`GmHit`) is plausible to the server. It carries **no damage**. Captured counts in
the 012701 run: 4 221 `Projectiles` / 4 221 `ProjectilesAck`, i.e. always paired.

### 5.3 `ProjHit` (id 102, class `GmHit`) — the nearest thing, and it does exist

`work/report-handlers.md:374` lists id 102 `ProjHit` among the enum members with
"no packet class at all", then `:377` infers it is `GmHit.cs` (pool index 212, used
by no other class). The captures confirm the inference and fix the field order by
brute force rather than by reading the obfuscated state machine:

`autododgeresearch/tools/field_order_solver.py` tries all 24 permutations of
`{Time, ProjectileId, ObjectId (varint), PlayerProj (bool)}` against all **580**
captured `ProjHit` payloads. Result: exactly **6** orderings consume every payload
exactly (the three varints are byte-indistinguishable from each other), all with
`PlayerProj` in the last position. Time-monotonicity within a session — `Time` must
be non-decreasing — breaks the tie at **99.5%** for `Time` first against **87.2%**
for the alternatives:

```
ProjHit  =  Time(varint) -> ProjectileId(varint) -> ObjectId(varint) -> PlayerProj(bool)
```

This is *not* the order the decompiler prints for `GmHit.Write`
(`Time -> PlayerProj -> ProjectileId -> ObjectId`, `GmHit.cs:20-57`): that method is
a flattened `while(true) switch(num)` state machine and the printed source order is
not the wire order. The measurement wins.

`PlayerProj` is **not** always true, and that is the discovery:

| PlayerProj | count | meaning |
|---|---|---|
| `true` (419) | 72% | the client's own projectile hit an enemy — `Projectile.cs:1687` |
| `false` (161) | 28% | the client's **non-owned** projectile hit the target it names — `Projectile.cs:1581` |

and the target it names is the **local player**:

* In every session where `PlayerProj=false` reports exist at all, the ObjectId is a
  single value: `Game_Slave#14` → 460542 (9x) and 1361 (10x);
  `Game_Slave#11` → 456799 (11x); `Game_Slave#15` → 457237 (16x);
  `Game_Slave#17` → 457492 (13x); `Game_Slave#7` (012701) → 453757 (32x);
  `Game_Slave#9` (012701) → 456719 (9x); `Game_Slave#11` (023129) → 460338 (14x).
* `autododgeresearch/tools/projhit_identity.py` cross-references that id against the
  per-object `StatsType.Hp` (int16) values the server streams in `GmUpdate`:
  `Game_Slave#9` ObjectId **458799** appears as an object with an Hp stat in
  **120** `GmUpdate` packets, and **120/120** of those Hp values match a
  `HealthUpdate` reading within 2 HP; `Game_Slave#9` (023129) ObjectId **459766**:
  **15/15**. That is the local player.
* `GmMapInfo` (id 5) is what carries `PlayerId` (`GmMapInfo.cs:18`, read at `:287`,
  matched at `World.cs:10142`), but the relay does not decode the field into its
  event log, which is why the cross-reference above had to go through the Hp stat.

**So, plainly:** the client *does* tell the server when it has been hit — not with a
damage number, but with a collision claim naming the local player's object id, the
projectile id, and the client's own clock. That packet is `GmHit` / `ProjHit`
(id 102) with `PlayerProj = 0`. The design brief's belief that "there is no
damage-report packet" is **refuted for projectile hits**, and **confirmed for
everything else**: there is no AoE/beam/contact/DoT equivalent, and no packet
anywhere in the client→server direction carries a damage or HP value (§5.1).

### 5.4 The search that establishes the negative

Run over `work/decomp/DarzaGameNet.Packets.Game.Client/*.cs` (117 files) and
`work/decomp/**/*.cs`:

```
pattern: \.Health\s*(-=|\+=|=)          -> 34 matches (all in §4.3's table)
pattern: Health\s*[-+]=                 -> 4 matches, item tooltips only
pattern: new GmHit|GmHit\b|new GmProjHit|GmProjHit
        -> 5 matches: Projectile.cs:1581, :1687, AoeBomb.cs:287, :323, GmHit.cs:6
pattern: class GmProjHit                -> no match (no such class)
enum enumeration of GmPacketType        -> no client class exists for id 70
```

### 5.5 Capture counts for completeness (run 012701, from the relay's own log)

| packet | direction | count |
|---|---|---|
| `Projectiles` (86) / `ProjectilesAck` (64) | S→C / C→S | 4 221 / 4 221 |
| `ProjHit` (102) | C→S | 484 |
| `AllyHit` (174) | C→S | 756 |
| `HealthUpdate` (70) | S→C | 2 775 |
| `Update` (1) / `UpdateAck` (83) | S→C / C→S | 3 184 / 3 182 |
| `Aoes2` (161) / `Aoes2Ack` (?) | S→C / C→S | 252 / 252 |

Note the near-1:1 pairings (`Projectiles`/`ProjectilesAck`, `Update`/`UpdateAck`,
`Aoes2`/`Aoes2Ack`) and the *asymmetric* hit traffic: 484 `ProjHit` against 2 775
health readings, i.e. **83% of HP changes are not accompanied by any client hit
claim** — the same 74% (400 ms window, all three runs) that §2.3 Test B reports.

---

## 6. The deadline arithmetic, as a table

Time is measured from **the first instant the client could know**, which §4 shows is
the local collision test (`Projectile.cs:1573`), not the HP number. `one_way` is the
inferred 31 ms (fast cluster, §3.2); the slow cluster's 113 ms and the measured
`Escape`→`EscapeAck` of 290 ms (§3.4) are quoted where they change the answer.
"Packet-only proxy" means a proxy at the relay's observation point; "in-process hook"
means code inside the client process.

| threat | first client-side knowledge → impact | what a packet-only proxy can do | what an in-process hook can do | can an escape plausibly arrive in time? |
|---|---|---|---|---|
| **Single slow projectile** (visible approach, e.g. 300+ ms of flight) | `GmProjectiles` announces `Position` + `Angle`; speed and lifetime are **not on the wire** and must come from object data. Client collision at impact − 0. | See the shot at spawn; act at spawn + its own processing (~1 ms, §2.4's relay took 0 ms-1 ms decision). Cannot see the bullet's true later position (no re-broadcast). | Evaluate the true swept path against the real player position and hit radius **every frame**, and write the escape the same frame the danger becomes real. | **Yes, comfortably.** Any escape sent ≥ 31 ms + tick offset before impact lands; a 300 ms flight leaves ≥ 200 ms of margin even at 290 ms ack. |
| **Fast projectile** (crosses the player's hitbox in < one frame of travel, e.g. >20 tiles/s) | the client's collision test at impact − 0; the hit report leaves at impact + 0. | Sees `ProjHit` only *after* the client has already been hit; the server reading follows **+55.5 ms median** later (§2.2). Effectively reactive-only. | Detect the *predicted* intersection on the previous frame(s) — the swept-segment test is arithmetic the client already has all the inputs for. Gains the whole report→reading interval, **55.5 ms median / 98.1 ms p90**. | **Yes if the hook predicts** (it buys 55-98 ms plus one frame); **no if it waits for the collision**, because the claim is already in flight and the server has already read it. |
| **Point-blank spawn** (enemy fires from inside the player's hitbox) | t = 0 *at* the collision; there is no earlier frame at which the projectile had a different position. | Nothing before the claim; nothing after. | At best one frame: the hook still learns only when the collision test runs. | **No.** The damage is decided in the same frame the first knowledge exists; **NOT MEASURABLE from the available captures — needs a live run** to confirm the frame period (record `ClientTime.LogicTotal` deltas per tick, §2.6). |
| **AoE that lands on the player's tile** | `GmAoes2`/`GmAoes` announces the AoE; `AoeProjectile` carries `Target`, `Value` (damage), `Radius`, `Delay` (`work/report-handlers.md` §C.3) — **a telegraph in the packet stream**, unlike a bullet. | Best case in the whole protocol for a proxy: exact impact tile, exact damage, and a `Delay`. Relay-inject the escape on arrival of the AoE packet. | Same information, ~1 frame earlier, plus access to the live tile/occupancy state (a proxy must infer blocking). | **Yes, if the Delay is non-zero** — measured AoE delays are not in these captures: **NOT MEASURABLE from the available captures — needs a live run** (record `AoeProjectile.Delay` vs. the `HealthUpdate` fall). AoEs with `Delay = 0` are point-blank rows. |
| **Damage-over-time / beam** (laser, sweep, tick damage) | `GmLaserRetarget` (271) / `GmLaserHit` (364) / `GmLaserRetarget` handler queues; `GmPlayerLaser` (68) carries `Position` + `Angle`. | A proxy can see the beam exists but not its current hit state; and §2.3 Test B shows whole sessions whose damage never produces a `ProjHit` (0 claims for 31 and 25 falls). | Can test beam-vs-player geometry per frame and escape on the first frame of intersection, before the first tick of damage. | **Partly.** The first tick is preventable if the hook is one frame ahead; subsequent ticks are **not** — once the server is ticking damage, no escape beats the next tick. |
| **Melee / contact hit** *(does not exist - operator-confirmed, and no damage site in the decompile requires it; retained only as the "no client involvement" control case)* | t = 0 at the collision; there is no telegraph in the packet stream at all (no `ProjHit` is generated for contact damage in any captured session). | Nothing. The proxy's first signal is the reading, **+55.5 ms median** after the collision. | Same t = 0, but the hook can act in the same frame instead of one network leg later. | **No**, and specifically: the server's own collision test (its `PopulateEnemies` analogue) applies damage with no client involvement, which is exactly the 74% of falls with no claim in §2.3. |

Every row's "can it arrive in time" test uses the same inequality from §3.4:
the escape must leave the client at least `one_way` (31 ms fast / 113 ms slow)
before the server tick whose damage step matters, and the tick grid is uniform
200 ms, so budget the **mean 100 ms of tick quantisation** on top.

---

## 7. The honest conclusion

### 7.1 What the hook actually fixes

**Fixed — the reaction latency after knowledge.** The current reactive rule's
decision path costs ~0-1 ms (`decisionLatencyMs`, seen as 0 in
`work/logs/nexus-20261005-023129-2000.jsonl`), so the rule is already
latency-optimal *given its input*. The input is the problem. A hook replaces
"wait for the server to tell me I was hit" with "I know the instant the client
resolves the hit". Three effects, and they are **not** additive because two of them
are alternatives to the same baseline:

* **vs. the reactive rule: +55.5 ms median (p90 +98.1 ms).** That is the measured
  collision → reflecting-reading interval (§2.2). It is decision time the hook has
  and the reactive rule does not. It is *not* gained again from any network term
  below — the reactive rule already writes its escape 0-1 ms after its reading.
* **vs. a remote (cloud/relay) proxy: +31 ms (fast cluster) / +113 ms (slow
  cluster).** A proxy that is one network leg away has not even *seen* the client's
  hit claim when the hook has already acted on its own collision test. Same frame,
  different place in the network.
* **vs. a co-located proxy that parses outbound traffic: at most one frame,
  ≈0-17 ms, and only by predicting.** This is the hardest comparison and the one
  worth stating plainly, because it is where the packet-only design is strongest.
  Such a proxy can read the `GmHit` claim the moment the client writes it, which is
  essentially the same instant the hook learns of the collision — so the hook's
  *action* gains only the remainder of that frame. Its real advantage is
  **knowledge**, not action: it can run its own swept test on the previous frame and
  be ready *before* the collision exists, which buys the **55.5 ms median / 98.1 ms
  p90** of §2.2 as prediction headroom rather than as notification. A proxy cannot
  do this: it has the spawn packet but not the live projectile position or the live
  player hit radius — `work/auto-dodge-feasibility.md` §2 tabulates both as absent
  from the stream ("Current (later) position: **NO. Never re-sent**"; "Size / hit
  half: **NO (derived)**") — so its swept test would run against dead-reckoned
  geometry.

Sum against the reactive rule, for the fast cluster, taking the maximum rather than
the sum of overlapping terms: **≈55.5 ms median of extra decision budget, ≈98 ms at
p90**, plus one frame if the hook predicts rather than reacts.

**Fixed — acting on knowledge that is true rather than stale.** §2.5's worked
examples are the concrete cases. The relay fired on a 1-HP reading echoed **151 ms
after the character had died**; and in the other death it fired at 28% into a burst
that had already taken 439 HP (72.2%) off the bar in 2 ms. A hook seeded from
`Player.Health` (which §4.3 shows is exactly the last server value) and advanced by
its own collision predictions cannot be in either state.

**Fixed — threats with a client-visible approach.** Slow projectiles, fast
projectiles (by prediction), telegraphed AoEs, and the first tick of a beam. These
are the rows of §6 marked "yes".

### 7.2 What remains physically impossible

Not "hard" — impossible, for the reasons already in the data:

1. **Any burst whose damage does not pass through a client-visible collision.** 74%
   of measured HP falls have no client hit claim at all (§2.3 Test B: 571 of 775).
   For those, the server is the only witness and the `HealthUpdate` is the first
   signal anyone gets. The corpus contains one such burst of **493 HP = 81.1% of
   max inside 150 ms, ending in death** (§2.4).
2. **Point-blank spawn** (and, were it to exist, melee/contact - it does not). t = 0 is the collision; there is no
   frame in which earlier knowledge exists.
3. **Every damage tick after the first tick of a beam/DoT.** Once the server is
   ticking damage, an escape must traverse `one_way` + tick quantisation to change a
   decision that is already being made.
4. **Preventing damage by suppressing the client's hit claim.** §2.3: 34% of claims
   produce no immediate fall and 74% of falls have no claim, so the server plainly
   resolves damage without the claim; suppressing `ProjHit` would blind the server to
   some hits and remove none of the ones that matter.

### 7.3 Quantified improvement over the reactive rule

| metric | reactive rule (measured) | hook (arithmetic from the above) | improvement |
|---|---|---|---|
| time from local knowledge to decision | 0 ms after the *reading*; the reading is itself **+55.5 ms median / +98.1 ms p90** after the collision | ≤ 1 frame after the collision (≈10-17 ms) | **≈55-98 ms**, plus one frame |
| time from local knowledge to packet on the wire | 0-1 ms after the reading (§2.4); **+31 ms of network** before the server can act on it in the fast cluster | same frame as the collision; **+31 ms network** from the client's NIC | **≈31-113 ms** against a non-co-located proxy |
| HP percentage the rule cannot see | between consecutive readings: median worst-drop per session **4.9%**, p75 **14.1%**, p90 **22.4%**, worst **23.0% in 48 ms**. Across a burst: worst **72.2% in 2 ms** and **81.1% in 150 ms**, the latter ending in death | unchanged — this is an input limitation, not a latency one, unless the threat has a client-visible approach | **0%** for claim-free damage; **the whole burst** for predicted projectiles |
| largest observed post-death action | injected `Escape` on a 1% reading **151 ms after `hp` reached 0** (`nexus-20261005-023129-2000.jsonl`, `.084` → `.235`); in the other death, the escape went out 1 ms before the `hp 0` reading, into a tick that had already applied 439 HP (72.2%) of damage | cannot act on stale HP | **≈151 ms** of provably-avoidable error |

The "0%" row is the honest centre of the answer: the hook's advantage is entirely a
*latency* advantage inside the window between the client's collision test and the
server's damage step, and that window exists **only for damage the client is the
first to know about**. Where the server is the first witness, no hook helps, and the
measured cadence — 53 ms median, 100 ms p75, event-driven on a 200 ms tick — is the
floor on how bad that can be.

---

## 8. What to record in the next live run

Each of these is a number this report marked NOT MEASURABLE. All are wire-observable
without new instrumentation beyond what the relay already writes:

1. **Client logic-frame period.** Log `ClientTime.LogicTotal` from two consecutive
   `Update`/`Attack`/`Move` frames *within one server tick* (currently the relay
   records only the last `Time` per packet). What to record: the histogram of
   intra-tick `LogicTotal` deltas; it is the hook's worst-case in-frame latency.
2. **A kill that starts from a full bar.** Log every `HealthUpdate` and, for each
   pair whose earlier reading is ≥90% of max and later reading is 0, the whole packet
   window between them. One occurrence settles §2.5. (The *within-tick* burst case is
   already measured — §2.4 — but the corpus contains no burst that began at full HP.)
3. **`AoeProjectile.Delay` vs. the `HealthUpdate` fall.** Decode `Delay` and `Radius`
   from `GmAoes2` (id 161) and pair it with the first falling reading. That is the
   telegraph budget for the AoE row of §6.
4. **Whether an escape can beat a hit that is already claimed.** Inject an escape
   *and* log the `ProjHit` claim, so the escape's position in the
   claim→measurement window is known rather than inferred. The relay already
   timestamps both; what is missing is a deliberate paired trial.
5. **One-way asymmetry.** A `CheckPing`/`CheckPingAck` pair measures only the sum.
   To separate the legs, compare the relay's timestamp for a known server-origin
   event (`GmPing`'s embedded timestamp) against the client-bound arrival — the
   relay currently discards the `GmPing` time field (`hex 5C00 6E7B02176F22DF08`).
