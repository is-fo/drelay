# Auto-nexus — what it does, and how it is implemented

A report on the rule in `src/main/java/networking/AutoNexus.java`: the signal it uses, every gate it
applies, where it sits in the relay's threads, and what each control on the dashboard's **auto nexus**
panel actually changes. The companion document is [AUTONEXUS-GUIDE.md](AUTONEXUS-GUIDE.md), which is
the operator's manual (how to turn it on, how to tune it, how to read the logs).

Everything below is written against the code as it stands, and the measurements quoted are taken from
the runs recorded in `work/logs/`.

---

## 1. What it is, in one paragraph

The server sends `GmHealthUpdate` (game id 70, `0x46`) roughly ten times a second with the character's
`maxHealth`, `health`, `shield` and `barrier`. Auto-nexus reads that packet, converts it to a
percentage, and if the percentage is below a threshold it writes `GmEscape` (id 66, `0x42`) into the
client→server stream. The decision is taken on the same virtual thread that read the health packet and
the escape is written **before that packet is forwarded on to the client**, so the escape leaves the
relay within a millisecond of the reading arriving. It is off by default, and even when it is switched
on it starts in **dry run**, which decides and logs but writes nothing.

## 2. What it is not: there is no prediction

This is the single most important thing to understand about the feature, and it is a property of the
game rather than a shortcut in this implementation.

The technique people usually mean by "auto-nexus" (as in RotMG proxies) is **predictive**: it
intercepts the incoming hit, computes the damage it would do, applies it to a local estimate of HP,
and escapes *before* the damage lands. That needs a hook on the damage path. In RotMG that hook is the
client's damage acknowledgement, which a proxy can suppress. Darza's client does not send one - the
closest packets, `ProjectilesAck` and `AllyHit`/`ProjHit`, are positional or sparse, not a damage
report - so a proxy has nothing to intercept, and inventing a local damage model would be guessing.

So the only truthful signal is the server's own `HealthUpdate`, at ~10 Hz. Two consequences follow
directly, and both are worth stating plainly:

- **A burst that removes the rest of the bar between two health packets cannot be escaped.** There is
  no reading to react to, and no proxy logic changes that. Observed in the recorded runs: the cadence
  is a median of **52-61 ms** between readings (p10 ≈ 40-47 ms, p90 ≈ 101 ms), and a single hit that
  takes a character from 100% to 0% inside one of those gaps is invisible until it is over.
- **What the rule optimises is latency after a reading, not knowledge of the future.** The measured
  decision latency is **0-1 ms** (`decisionLatencyMs` in the log). The remaining time is the network:
  in the same run the server's verdict came back **245-305 ms** after each instant escape.

Note also that the rule only ever sees the **game** service. `Session.transition` returns immediately
for queue sessions, so nothing on port 6412 is ever evaluated.

## 3. The signal, the bytes, and where the write happens

| step | where | what |
|---|---|---|
| read | `Relay.pump` (downstream thread) | one length-prefixed message is read from the server socket |
| decode | `Session.onPacket` → `transition` | id 70 → `onHealthUpdate` stores the reading and emits the packet event |
| decide | `Session.onHealthUpdate` → `AutoNexus.onHealth` → `decide` | pure function, no I/O; produces a `Decision` with the reason string |
| act | `AutoNexus.fire` | takes the **upstream** writer's monitor and calls `Session.inject` |
| forward | `Relay.pump` | the health packet is then written to the client, under the **downstream** writer's monitor |

The injected message is five payload bytes framed with a four-byte big-endian length:

```
00 00 00 02 42 00        BE length 2, LE type id 0x0042 = GmEscape
```

Three properties of that write are load-bearing:

- **`Session.inject` refuses to write unless the caller already holds the stream's monitor**
  (`Thread.holdsLock`) and returns `-1` if it does not. Both pumps serialize their own writes on their
  `GameWriter`, so holding the upstream writer's monitor is what guarantees an injected frame lands
  whole and between two forwarded messages rather than inside one.
- **The event is logged before the bytes are written.** The intuitive order is the reverse, and it was
  the reverse until a stress test caught the consequence: a process that stops in the window between
  writing and logging leaves a packet on the wire that the log does not mention. For a safety feature
  that is the dangerous direction - an audit asking "did auto-nexus ever fire unexpectedly" would
  undercount. The log is therefore a **superset** of the wire, and a write that then fails is reported
  as a second `error` event naming the same injection sequence number.
- **Framing follows the session.** `Session.frameBigEndian()` carries the byte order the relay detected
  for that session, so an injected frame is byte-compatible with every forwarded one beside it. A
  session that resolved little-endian would otherwise be fed a big-endian length and desync instantly.

## 4. The gates, in the order the rule applies them

`decide` is a pure function over `(Session, Reading)` plus the rule's per-session scratch. Each gate
has its own message, and the message is what appears in the log and on the dashboard:

| # | condition to decline | message |
|---|---|---|
| 0 | `enabled` is false | *(returns before deciding; not even counted as a decision)* |
| 1 | no world yet, or a fresh handshake | `injection is not armed (no world yet, or a fresh handshake)` |
| 2 | no `HealthUpdate` yet **in this world** | `no HealthUpdate seen in this world` |
| 3 | `maxHealth <= 0` | `the server has not reported a maximum HP` |
| 4 | `health <= 0` | `HP is zero (dead or spirit form)` |
| 5 | `skipInSafeArea` and the server says safe | `the server says this is a safe area` |
| 6 | per-world budget spent | `world budget spent (n/max)` |
| 7 | too soon after the last injection | `only N ms since the last injection` |
| 8 | `rearmPercent > 0` and not yet re-armed | `waiting to re-arm (needs X%, at Y%)` |
| 9 | `thresholdPercent <= 0` | `threshold is 0, so the rule is off` |
| 10 | health not measurable | `health is not measurable yet` |
| 11 | percentage not below the threshold | `N% is not below the M% threshold` |
| — | **all of the above pass** | fires: `N% HP is below the M% threshold` |

Details that matter when reading a log:

- **Gates 6 and 7 come before 8-11**, so a spent budget or a short interval is reported even when the
  reading is not below the threshold at all. The first reason that matches wins; the order above is
  the order in the code.
- **Gate 1 is the whole state machine.** `injectionReady` is set by the client's `Hello` (id 55)
  followed by the server's `MapInfo` (id 5), and is *deliberately re-armed to false* on every new
  `Hello` - a realm transition reuses the same TCP connection with a fresh handshake, so anything
  injected during it could land in the middle of the server reading login fields.
- **`MapInfo` clears the world state**: the health reading, the safe-area flag, the cast flag, **and**
  the rule's per-world scratch (budget and interval clock). See §5.
- **Gate 5 defaults to on and is the reason `Nexus`/town is safe.** Across the recorded runs it declined
  four times while the character was below the threshold in a safe area - once during a dry run and
  three times in the live tail of the same session.
- **Percentage maths** (`Reading.percent`): with `useEffectiveHp` off, `health / maxHealth`; with it on,
  `(health + shield + barrier) / maxHealth`, with negative shield/barrier clamped to 0. `maxHealth <= 0`
  yields `-1`, which declines at gate 10 rather than being treated as "dead".

## 5. Per-world state, and why the acknowledgement queue is per session

Two pieces of mutable state sit behind the pure decision:

- **`Scratch`**, one per session id: `injectionsThisWorld`, `lastInjectionMono`, `needsRearm`, and the
  last decline reason (used purely to keep the log readable). It is reset on **every world entry**, by
  `AutoNexus.onWorldEntry`, which is called from `Session.onWorldEntry` when `MapInfo` arrives. This is
  what makes the rule usable across a session: a new world means a new budget.
- **`pending`**, a queue of injections awaiting a server verdict - and it is keyed **by session**, which
  fixes a real bug rather than being tidy. The realm flow injects an escape whose ack never arrives, the
  server retargets the client to port 6411, and a fresh session opens. With one relay-wide queue the new
  session's first ack would be matched to the old session's injection, reporting the wrong sequence
  number, the wrong latency and the wrong verdict - corrupting exactly the evidence a reader uses to
  decide whether the instant escape works. `forget(session)` releases both on session close.

The verdict itself is `GmEscapeAck` (id 158), which the stock client **discards** - the relay is the only
place it can be seen. `onEscapeAck` matches it to the oldest pending injection *in that session*, and an
ack with nothing pending is reported as the client's own escape (`no injection pending in this session:
the client sent it`) rather than being blamed on the relay. This is how a run distinguishes "the relay
nexused and the server accepted it" from "the player pressed the key".

## 6. Where the code is

| file | role |
|---|---|
| `src/main/java/networking/AutoNexus.java` | the rule: `Config`, `Reading`, `Decision`, `PendingInjection`, the gate order, the write, the ack matching, the dashboard view |
| `src/main/java/networking/Session.java` | the state machine that arms it (`Hello`, `MapInfo`, `HealthUpdate`, `SafeAreaState`, `EscapeCastState`, `EscapeAck`) and the `inject` write path with its lock check |
| `src/main/java/networking/Relay.java` | loads `autoNexus` from `relay-routes.json`, then `-Ddrelay.nexus.*` / `DRELAY_NEXUS_*` over it; builds the `AutoNexus`; the pump loop that calls `onPacket` before forwarding |
| `src/main/java/networking/web/WebDashboard.java` | `GET /api/state` (the whole rule state) and `POST /api/nexus` (sparse overrides) |
| `src/main/java/networking/web/DashboardPage.java` | the page itself, including the auto nexus panel |
| `src/main/java/networking/log/EventLog.java` | routes `kind=nexus`/`inject`/`world` events into `work/logs/nexus-<run>.jsonl` as well as `events-<run>.jsonl` |
| `src/main/java/networking/InjectionTests.java` | the offline suite: the rule's gates, the budget, the interval, dry run, re-arming, per-session acks |
| `tools/tests/test_auto_nexus.py` | the same rule end to end against a fake game server, through the real relay |

Config keys are accepted in three forms with `_` folded to `-` and case ignored, so
`threshold_percent`, `thresholdPercent` and `threshold-percent` all mean the same thing. An unknown key
is reported back as `unknown:<key>` rather than ignored, because a typo would otherwise look like a
setting that simply had no effect.

## 7. The dashboard's auto nexus panel, control by control

The page polls `/api/state` once a second and rebuilds these controls from the reply, but **not while
one of them has focus** - so a value you are typing is never overwritten mid-keystroke. Nothing is sent
until you press **apply**, which POSTs the whole set to `/api/nexus`; every change is recorded as a
`note` event containing the request, what was applied, and the resulting config.

| control | key | what it does | what it does **not** do |
|---|---|---|---|
| **enabled** | `enabled` | the master switch. Off means `onHealth` returns immediately: no decision, no counter, not even a log line. | Nothing else. It does not arm injection; the world gate does that. |
| **dry run** | `dryRun` | the rule runs, decides, counts and logs, and **writes nothing**. The event says `DRY RUN would nexus: ...`. | It does not suppress the client's own escape, and it does not un-arm anything. |
| **hp+shield+barrier** | `useEffectiveHp` | compares `health + shield + barrier` against `maxHealth` instead of bare `health`, so a shielded character is not nexused on raw HP. | It does not change what the HP panel prints; the panel shows both percentages. |
| **skip safe area** | `skipInSafeArea` | declines while the server's `SafeAreaState` says the area is safe (town/Nexus), where an escape achieves nothing. | It is on by default; turning it off means the rule will try to escape in town. |
| **threshold %** | `thresholdPercent` | the trip level: fire below this percentage. | **0 disables the rule without disabling the switch** - the header pill will still say LIVE. |
| **delay ms** | `minIntervalMillis` | the minimum gap between two escapes *this relay injected* **in the same world**. See §8 - this is the one control whose label misleads. | It is not a delay before firing, not a delay after a reading, and it does not count the player's own escape key. |
| **max/world** | `maxPerWorld` | hard budget of injected escapes per world entry. Reset by every `MapInfo`. | It does not carry over between worlds, and it does not limit the client's own escapes. |
| **re-arm %** | `rearmPercent` | after firing, refuse to fire again until HP has recovered to at least this percentage. | 0 (the default) leaves the rule armed continuously; the budget and interval still apply. |
| **apply** | — | POSTs all of the above. | A value out of range is *clamped*, and the clamped value comes back in the response, so what you typed and what is stored cannot silently differ. |
| **reload** | — | re-reads `/api/state` immediately instead of waiting for the next poll. | — |

Read-only parts of the panel:

| element | meaning |
|---|---|
| header pill `AUTO-NEXUS LIVE` / `auto-nexus dry run` / `auto-nexus off` | derived: `enabled && !dryRun` is LIVE. Note it says LIVE for `enabled=true, dryRun=false` even when `thresholdPercent=0`, which can never fire. |
| header pill `injection armed` / `injection disarmed` | `Session.injectionReady()` for the primary session - the world gate, i.e. whether `Hello` + `MapInfo` have both been seen. This, not `enabled`, is why nothing happens in the lobby. |
| header pill session tag | the primary session is **the newest session id**, not necessarily the one you are playing. |
| character hp panel | the newest reading: raw `%`, effective `%`, how old it is, plus shield, barrier, sample count, world name, safe area and cast state. The bar turns red at or below the threshold - but it compares the **raw** percentage, so with `hp+shield+barrier` on, the bar can be red while the rule declines, or vice versa. |
| `decisions` | readings the rule evaluated (only counted while `enabled`). |
| `nexused` | escapes actually written. |
| `dry runs` | decisions that *would* have nexused and wrote nothing. |
| `declines` | every decision that did not fire, for any reason. |
| `last ack` | the newest `EscapeAck` verdict, and whether it answered an injection. |
| the four ack lines | the last four acks with time, accepted/refused, and the note. |

## 8. The delay option, and why it looks like it does nothing

**What it is.** `minIntervalMillis` is a **rate limiter on repeat injections, scoped to one world**. It
is checked at gate 7 against the time of the last escape *this relay wrote*. It is not a pause before
firing: when a reading is below the threshold and every gate passes, the escape is written immediately,
always.

**Why it is invisible in practice.** Five separate reasons, in descending order of how often they bite:

1. **In dry run it cannot do anything at all.** The clock it measures is only started by a real
   injection (`fire()` sets `lastInjectionMono`), and dry run returns before `fire()`. So the interval
   check always sees "infinitely long ago" and never declines. The code path says so; driving the rule
   directly (the same `Hello`/`MapInfo`/`HealthUpdate` fixture `InjectionTests` uses, with a 5000 ms
   delay) shows a dry-run decision followed immediately by another decision to fire; and the logs agree:
   a recorded dry-run session produced **604** decisions that would have nexused with the interval
   configured at 1200 ms and **0** interval declines. If you tune the delay while in dry run - which is
   the default, and the state the guide tells you to tune in - you will see nothing happen, because
   there is nothing for it to gate.
2. **A successful nexus wipes the clock.** `MapInfo` resets the rule's per-world scratch, including
   `lastInjectionMono`, and a successful nexus is what causes the next `MapInfo`. So the interval can
   only ever cover the window *between the escape and the world change*.
3. **It does not count the player's own escapes.** Pressing the nexus key yourself does not start the
   clock, so the relay can still inject immediately afterwards.
4. **A delay shorter than the health cadence is unobservable.** Readings arrive every ~50 ms (median
   52-61 ms, p10 40-47 ms). The gate can only block a reading that arrives *sooner* than the delay;
   a reading 50 ms later sails through a 12 ms gate every time. A recorded session was run live with
   the delay set to 12 ms and injected four escapes; the interval declined **nothing**, because no
   reading ever came within 12 ms of an injection (the closest was ~82 ms). So even with the rule
   firing, a 12 ms delay is inert.
5. **Its effect only ever appears as a decline line**, and it is merged into the same `declines` counter
   as every other reason, so the dashboard gives no sign that it did anything.

The observable window is therefore narrow and can be stated exactly: the delay does something only when
it is **longer than the gap between two health readings (~40-100 ms) and shorter than the time to the
next world change (~0.9-1.0 s in the recorded runs)**, and only when a sub-threshold reading recurs in
the same world. The default of 1200 ms sits just past the top of that window, which is why it shows up
exactly once per world at most.

**What it actually buys, with the evidence.** In the recorded live session, one world went like this:

```
01:29:18.152  NEXUS: 33% HP is below the 35% threshold        thisWorld=1  decisionLatency=1ms
01:29:18.202  no nexus: only 50 ms since the last injection  thisWorld=1
01:29:18.252  no nexus: only 100 ms since the last injection thisWorld=1
01:29:19.067  injection is not armed (no world yet, or a fresh handshake)   <- MapInfo, new world
01:29:19.067  armed for a new world: ... interval 1200 ms, live             <- scratch reset
```

The escape was written at `.152`, the ack came back accepted at `.429` (277 ms), and the world changed
at `19.067` - 915 ms later. In those 915 ms the server sent two more sub-threshold readings, and the
interval suppressed both. **Without it, those two readings would each have fired**, spending the whole
three-per-world budget in ~100 ms on a nexus that was already on its way. Across that whole session the
interval declined **9** times for **7** injections, and every one of those declines fell in a window
like this one, between an injection and the world change that followed it. That is the real function: a
**duplicate-escape suppressor covering the window between firing and the world change**, matching the
server's demonstrated tolerance for repeats (the stock client itself re-sends an escape ~140-160 ms
after the first when nothing appears to happen - visible in the same logs as two `C->S Escape` events
160 ms apart under one ack).

So it is not broken. It is a real gate with a real effect that is (a) provably inert in dry run, and
(b) named as though it were something else.

**Recommendation.** Rename the control. `delay ms` reads as "wait this long before escaping", which is
the one thing it is not; `min interval ms` or `re-escape gap ms`, with a tooltip such as "minimum gap
between two injected escapes in the same world", would describe what it does. A genuine pre-fire delay
would be a different feature and would be actively harmful here - a delay after a health reading spends
exactly the latency the design exists to avoid. The `-D` property is already named
`-Ddrelay.nexus.delayMs`, and the API accepts `delay`/`delayms` as aliases, so the confusion is baked
into three surfaces rather than one.

Two related notes on the same gate:

- Because a decline is logged only when the reason string changes, and the interval's reason contains
  the millisecond count, every distinct count is a new reason - so a character stuck below the
  threshold in one world with a large delay would log one line per reading (up to 10/s) rather than one
  line. The deduplication helps the threshold and safe-area declines, not this one.
- Gate 6 (the budget) is checked *before* gate 7 (the interval). With `maxPerWorld=0` the rule reports
  `world budget spent (0/0)` for every reading regardless of the interval.

## 9. Counters, and what a "decision" is

`decisions` counts calls to `decide` that got past the master switch. `nexused` counts escapes written,
`dry runs` counts decisions that would have written one, and `declines` counts everything else - so
`decisions == nexused + dry runs + declines` (modulo the write-failure path, which counts a decision and
a fire but writes nothing, and emits an `error` event instead). They are deliberately not reset per
world: they describe the run.

## 10. Known limits and quirks

- **No prediction** (§2). This is the honest ceiling of a proxy-only design here.
- **The primary session is the newest one**, which is not always the one you are playing; the HP panel
  and the `injection armed` pill follow it.
- **The HP bar's colour uses the raw percentage** even when the rule is comparing effective HP.
- **`thresholdPercent = 0` disables the rule while leaving the master switch on**, and the header pill
  will still read LIVE with `dryRun` off. Set `enabled` off instead if you want the state to be legible.
- **Safe-area state is only as fresh as the last `SafeAreaState`.** It is cleared on every `MapInfo`
  (to "not safe"), specifically so that a world that never re-sends the flag cannot silently disable
  the rule for its whole duration.
- **The channelled-escape variant still exists in the config** (`useCastChannel` / `castMillis`) but is
  no longer offered on the dashboard; see §11. It is the fallback for the case where the server refuses
  the instant form.
- **A nexus during a burst is not survivable**, and the interval, budget and re-arm settings do not
  change that. They limit repeat attempts, not incoming damage.

## 11. `useCastChannel`: still supported, no longer on the dashboard

The dashboard's `cast channel` checkbox was removed because it is a fallback for a case that has never
been observed to be necessary, and because it is the setting with the worst failure mode in the panel.
The instant five-byte escape is the default, is what most recorded injections used, and is byte-identical
to what the stock client sends in every non-channelled situation - so it is the form with evidence
behind it. The feature itself is unchanged and still reachable from the two configuration surfaces that
do not put it one click away during a session:

```jsonc
// relay-routes.json
"autoNexus": { "useCastChannel": true, "castMillis": 2000 }
```

```
-Ddrelay.nexus.castChannel=true     DRELAY_NEXUS_CAST_CHANNEL=true
```

It sends `GmEscapeCastState{Casting=true}` first, waits `castMillis`, then sends the escape. Be aware of
what the wait costs: the sleep happens **while the downstream pump holds the upstream writer's
monitor**, and that thread *is* the server→client pump, so for the whole gap **both directions stall**
and the client visibly freezes. Nothing is lost - the escape has already gone out ahead of the pause -
but the instant form is the default for that reason. In the one recorded run that used it and got an
answer, the `EscapeAck` arrived accepted with a reported latency of **2249 ms**. Note what that number
is: the pending injection's clock is started *before* the sleep, so the reported latency includes the
2000 ms wait, and the server's own contribution was the same ~250 ms the instant form took.
