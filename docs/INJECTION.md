# drelay — writing packets, and watching a session

This document is about the half of the relay that **acts**: injecting a packet into a live session, and
the observability that makes a session analysable after the fact. It is the reference for auto-nexus,
which is the first feature built on it.

Where a statement is a measurement, the artefact that measured it is named. Where something is not
known, it says so.

---

## 1. Injecting a packet without breaking the session

There are three separate orderings to get right. They are independent, and getting any one wrong
produces a different failure.

### 1.1 Field order (little-endian, `Write`'s order)

The payload is `[2-byte little-endian type id][body]`. The body is written by the packet's `Write`
method, in the order that method emits it, with every multi-byte primitive **little-endian** (the
client writes through .NET's `BinaryWriter`).

| field | wire |
|---|---|
| `Write(int/uint/short/ushort/long/float/double)` | little-endian |
| `WriteString8/16/32` | 1/2/4-byte length, then UTF-8 bytes |
| `WriteVarint` | 6 value bits + sign (bit 6) + continuation (bit 7); then 7-bit groups |
| `Write(bool)` | one byte |

**Use `Write`'s order, never `Read`'s.** Three separate reversals in this project found `Read` and
`Write` disagreeing - `GmActivateObject`, `GmSwap` and `Item` - and in each case `Read` was the wrong
one. The decompiled reader is not a reliable field list: it drops fields the writer emits, so a
decoder transcribed from it consumes fewer bytes than the packet holds and silently desynchronises
everything after it. The test is the same in every case - a decode must consume a captured packet to
its exact last byte.

### 1.2 Frame order (big-endian length, counting the type id)

```
[4-byte big-endian length][payload]      game / game_slave
```

`GmPacket.GetData()` writes the type id **first** and only then the body, and Telepathy frames whatever
it produced. The length therefore **includes the two type bytes**. An empty `GmEscape` is:

```
00 00 00 02 42 00
└ BE len=2┘ └ LE id ┘
```

`00 00 00 00 42 00` is a *different message*: a zero-length packet followed by a packet with id 66, one
frame later, and the peer would then read the next real packet's length from the wrong offset. That
single byte is the difference between a working injection and a desynchronised session.

The queue service frames identically except that the length is **little-endian** and the type is one
byte. The relay detects the order per session from the first header and passes the decision in; see
`docs/PROTOCOL.md` §4.

### 1.3 State order — the part a captured packet cannot tell you

A packet is only legal in the right *state*. The relay's gate is `Session.injectionReady()`, and it
opens only when both of these have been observed on that connection:

1. **the client's `Hello` (id 55) going up** — before it, the server is still reading the handshake's
   fields, so extra bytes are consumed as part of `Hello` and corrupt the login;
2. **the server's `MapInfo` (id 5) coming down** — before it there is no world, so an escape has
   nothing to leave.

`MapInfo` is the same boundary the client itself uses: it resets its escape-cast state and its
safe-area flag there. Every new `MapInfo` **re-arms** the gate (a realm transition is a new
conversation) and resets the health reading, the per-world injection budget and the rule's arming
state together.

### 1.4 Write order — atomicity with forwarding

Both pumps serialize their writes on the `GameWriter` they own. An injection happens on the
*downstream* thread but must write to the *upstream* socket, so `Session.inject` requires the caller to
hold that writer's monitor:

```java
synchronized (upstream) {          // the very object the upstream pump synchronizes on
    session.inject(upstream, "Escape", Injection.escape(session.frameBigEndian()), "auto-nexus at 20% hp");
}
```

`Session.inject` asserts `Thread.holdsLock` and *refuses* (with an error event) if it is not held. That
refusal exists because the alternative - a length prefix written, then a forwarded packet interleaved
between it and its payload - corrupts the session in a way that looks like a server bug. The assertion
turns it into one loud log line at the one place responsible.

**The injection is recorded before it is written, not after.** The intuitive order is the reverse, and
that is what this did until `tools/tests/test_relay_stress.py` caught the consequence: under load the server
received *more* escapes than the log recorded. The gap is the window between the write and the log
line, and a process that stops in it leaves a packet on the wire the log does not mention. For a safety
feature that is the dangerous direction - a reader asking "did auto-nexus fire when it should not have"
would find fewer firings than happened. Emitting first makes the log a **superset** of the wire, and a
write that then fails is reported as an additional `error` event naming the same injection, so
"attempted and failed" is distinguishable from "attempted and succeeded".

The frame's length order comes from the session (`Session.frameBigEndian()`), never from a constant:
every forwarded frame in a session uses the order its `SessionState` detected, and an injected frame
that disagreed would make the server read an absurd length and desync the stream.

**Failure containment.** The decode/log/retarget work for one packet is wrapped per packet, so an
unexpected exception there costs a log line and the packet is still forwarded unchanged. That is
deliberate: those steps are observation, not policy, and a mis-decode must never be able to end a live
session. The stream-level catch remains for real stream failures (a torn header, a closed socket).

### 1.5 What the relay can therefore send

| packet | id | bytes | notes |
|---|---|---|---|
| `GmEscape` | 66 | `00 00 00 02 42 00` | the whole of an instant nexus; byte-identical to what the client sends |
| `GmEscapeCastState{true}` | 290 | `00 00 00 03 22 01 01` | only to imitate the channelled escape |
| `GmEscapeCastState{false}` | 290 | `00 00 00 03 22 01 00` | cancels a cast |

Build them with `networking.packets.Injection`, not by hand. `java -cp target/classes
networking.TestVectors` prints them, and the offline harness consumes that output rather than
hard-coding the bytes, so a test cannot pass against a different encoder than production uses.

### 1.6 Verification

```powershell
java -cp target/classes networking.InjectionTests     # byte literals, the gate, the rule, the write lock
python tools/tests/test_auto_nexus.py                       # the whole chain against a fake game server
```

`InjectionTests` asserts against **literal byte strings** rather than round-trips. A round-trip passes
even when both directions share the same wrong idea of the format, which is exactly the failure that
matters here.

---

## 2. The varint, and why its grouping was wrong

`writeVarint`/`readVarint` are not protobuf varints. The first byte carries 6 value bits, the sign at
bit 6 and the continuation flag at bit 7. The question the decompiled source could not answer is where
the **first continuation byte** starts, because its bit masks are entries in an encrypted constant pool.

Two candidates are self-consistent:

| grouping | `83 0B` decodes to | `8B 64` decodes to |
|---|---|---|
| 6, 7, 7, ... (the original code) | 1411 | 12811 |
| 6, 6, 7, ... (correct) | **707** | **6411** |

The wire settles it. Every captured `GmHealthUpdate` reading was compared against the
same character's HP as a fixed-width `StatsType.Hp` int16 in a `GmUpdate` packet within 600 ms:

```
votes (the health value appears among the fixed-width Hp stats of the same moment):
  shift7 (original decoder):  0 of 26
  shift6 (corrected):         9 of 26
```

All 23 distinct health values in that window appear as fixed-width stats only under the 6, 6, 7
grouping, and it is the **only** formula in the search space that also reproduces `8B 64` = 6411 (the
measured `GmReconnect` port) and `43` = -3.

The bug was invisible in the way that matters: a wrong shift produces a wrong number of the right
magnitude, and the packet's byte length is unchanged, so nothing downstream notices. The captured
`46 00 83 0B 83 0B 00 00` is now pinned in `PrimitiveTests` as 707/707/0/0.

> An earlier reading of the client recorded the grouping as 6/7 and cited `BA 03` for 250.
> `BA 03` is 250 only under 6, **6**, 7. The two readings agree on short values and diverge above 13
> bits, which is why a spot check on 250 or 6411 looked right either way.

---

## 3. Observability

### 3.1 Why a structured log rather than more `println`

A play session cannot be watched live and cannot be reproduced. The interesting moment is discovered
*afterwards* - usually as one specific question: *what was my HP in the 400 ms before the escape went
out, and did the server acknowledge it.* Answering that from free text means regexes that break
whenever a format string changes; answering it from JSON is a `jq` one-liner.

### 3.2 What is written, and where

Every run gets its own files under `work/logs/` (configurable with `logDirectory`), named by run id:

| file | contents | read it when |
|---|---|---|
| `events-<run>.jsonl` | **every** event, one JSON object per line, rotated by size | you need the full packet stream |
| `nexus-<run>.jsonl` | the escape story only: health readings, decisions, injections, acks, world entries | something went wrong with auto-nexus |
| `relay-<run>.log` | the human-readable line, in the exact format the existing Python tools parse | you want to eyeball it |

The same per-packet line still goes to **stdout**, because that is where every existing verification
script captures the relay's output from.

Rotation keeps the newest 4 generations of `events-*.jsonl` at 32 MB each by default
(`logMaxBytes`). `events-*.jsonl` is appended to a per-run file, never to a shared one, so "the session
I just played" is one file.

### 3.3 The event model

One line per observation:

```json
{"seq":7,"t":"23:40:50.662","ms":653,"kind":"packet","sess":"Game#1","dir":"S->C",
 "pkt":"HealthUpdate","id":70,"len":8,"hex":"4600830B830B0000",
 "data":{"maxHealth":707,"health":707,"shield":0,"barrier":0,"hpPercent":100,"effectivePercent":100}}
```

| field | meaning |
|---|---|
| `seq` | global, monotonic, and assigned **inside the log's write lock**, so the file's line order is the sequence order |
| `t` / `ms` | wall clock (matches the game's own log) and monotonic milliseconds (use this for intervals) |
| `kind` | `session`, `packet`, `inject`, `nexus`, `world`, `note`, `error` |
| `sess` | `Game#3`, `Game_Slave#5`, `Queue#1`, or `relay` |
| `dir` | `C->S` or `S->C` |
| `pkt`, `id`, `len`, `hex` | the packet, its id, its payload length and its bytes (truncated at 4 KB with a `..(N more)` marker) |
| `data` | decoded fields, and every extra field the emitter attached |
| `note` | human-facing one-liner |

Two ordering facts worth knowing before reading a log:

1. **A packet's own event is emitted after the effects it triggered.** A `HealthUpdate` that causes an
   injection produces the `inject` event *first*, then the `nexus` decision, then the `HealthUpdate`
   event - so the triggering packet carries the highest sequence number of the group. Reading strictly
   upwards through `seq` therefore shows cause and effect in reverse for that one packet. The
   `inject` and `nexus` events each carry the health reading that justified them, so the group is
   self-describing at any position; use `t`/`ms` for wall-clock order and `data.injectionIndex` for the
   firing order.
2. **The log is a superset of the wire.** An injection is recorded before its bytes are written (see
   §1.4), so a process that stops mid-injection leaves a record of having tried. A recorded injection
   therefore means "attempted"; an immediately following `error` event naming its sequence number means
   the write did not complete.

### 3.4 Answering the usual questions

```powershell
# the whole escape story of the last session, in order
Get-Content work\logs\nexus-*.jsonl | Select-Object -Last 40

# every injection, with the health reading that caused it
jq -c 'select(.kind=="inject")' work\logs\events-*.jsonl

# the HP trace before and after each nexus decision
jq -c 'select(.kind=="nexus" or .pkt=="HealthUpdate") | {seq,t,pkt,note,hp:.data.health,max:.data.maxHealth}' work\logs\nexus-*.jsonl

# did the server accept it, and how fast
jq -c 'select(.pkt=="EscapeAck")' work\logs\events-*.jsonl

# what the client itself sent that the relay did not
jq -c 'select(.kind=="packet" and .dir=="C->S" and .data.clientOwnEscape==true)' work\logs\events-*.jsonl
```

### 3.5 The dashboard

Started by default on `http://127.0.0.1:8765/` (loopback only; `-Ddrelay.web.port=0` disables it,
`-1` takes any free port). If the configured port is busy the next four are tried, and the chosen URL
is printed.

| endpoint | purpose |
|---|---|
| `GET /` | the page: one HTML string, no build step, no CDN |
| `GET /api/state` | sessions, headline HP, the nexus and strip modules, counters, the filter list, the log paths |
| `GET /api/events?after=N&limit=M[&raw=1]` | events newer than sequence `N`, filtered unless `raw=1` |
| `POST /api/nexus` | sparse overrides, e.g. `{"enabled":true,"threshold_percent":40}` |
| `GET`/`POST /api/strip` | the status-effect strip, e.g. `{"enabled":true,"effects":[11,7,6],"min_votes":3}` |
| `GET`/`POST /api/filters` | the filter list |
| `GET /api/packets` | packet ids and names, for writing a filter |
| `GET /api/log?lines=N` | a tail of the nexus JSONL, for what happened before the page was opened |

**Settings are saved, not just applied.** Both `POST` endpoints write the two blocks the dashboard owns
(`autoNexus`, `strip`) back into the route table this run was started with, and the response says
whether that write succeeded - the page shows it under the controls. The rest of the document is
passed through untouched: the comment block, the routes and the addresses the launcher just resolved
are read and written back byte-for-byte, because a settings writer that dropped a key it did not
recognise would corrupt the file the relay needs to start from. `networking.ConfigWriter` does that
write; `Routes` carries the two blocks forward when the launcher regenerates the file on the next run,
which is what makes a change survive a restart.

**Filters are the point of the live view.** The log records everything; the dashboard shows what is
being reasoned about. A rule matches on `kinds`, `packets` or `sessions`, any of which may use a
trailing `*`. An event is shown when any *enabled* rule matches. The default rule is **character hp**,
and it keeps more than `HealthUpdate` on purpose:

- `HealthUpdate` - the reading itself;
- `Update` - the same HP as a fixed-width `StatsType.Hp` stat, the cross-check on the varint decoder;
- `Escape`, `EscapeCastState`, `EscapeAck`, `ForcedEscape` - the escape conversation;
- `Kicked` - the server's own reason for ending the session, which is the fastest explanation of a
  failed experiment there is;
- `SafeAreaState` - whether an escape is even meaningful here;
- `MapInfo` - world entry, which is what arms injection;
- `Reconnect` - the one packet that moves a session;
- and every action the relay itself took (`nexus`, `inject`, `world`, `error`).

Without the last group the view would show the symptom and hide the cause. `nexus only` and
`everything` are provided but off by default.

---

## 4. Auto-nexus

### 4.1 What it is, and what it is not

It is **reactive**: it reads the server's own `GmHealthUpdate` (id 70, ~10 Hz) and injects
`GmEscape` when the fraction falls below a threshold. The decision is taken on the same thread that
read the packet and the bytes are written before that packet is forwarded, so the escape leaves within
microseconds of the reading arriving - logged as `decisionLatencyMs`.

It is **not** a damage predictor. The technique usually used for this (compute the incoming hit, apply
it to a local HP estimate, escape before it lands) requires the client to acknowledge damage so the
acknowledgement can be suppressed. RotMG does; Darza does not - the capture shows no damage ack, and
`ProjectilesAck` is positional rather than a damage report. So a burst that removes the rest of the bar
between two health packets **cannot** be escaped, and no amount of proxy logic changes that.

### 4.2 Settings

| key (config, `-Ddrelay.nexus.*`, dashboard) | default | meaning |
|---|---|---|
| `enabled` | **false** | master switch |
| `dryRun` | **true** | decide, log and report, write nothing |
| `thresholdPercent` | 35 | trip below this percentage |
| `useEffectiveHp` | false | compare `health + shield + barrier` against `maxHealth` instead of `health` |
| `skipInSafeArea` | true | decline where the server says the area is safe |
| `minIntervalMillis` | 1200 | minimum gap between two injected escapes **in the same world**; the dashboard mislabels this one as "delay ms", see [AUTONEXUS.md](AUTONEXUS.md#8-the-delay-option-and-why-it-looks-like-it-does-nothing) |
| `maxPerWorld` | 3 | hard budget per world entry |
| `rearmPercent` | 0 | optional: require HP to recover to this level before firing again |
| `useCastChannel` | false | imitate the channelled escape (`EscapeCastState{true}` first). Not on the dashboard; config or `-D` only |
| `castMillis` | 2000 | wait between that packet and the escape |

Defaults describe an installed but inert feature: `enabled=false`. Flipping the master switch is a
deliberate act, and `dryRun=true` is the state to tune a threshold from.

The gates, in the order the rule checks them: enabled, injection armed, a health reading exists in
this world, a positive maximum, HP above zero, not a safe area, budget left, interval elapsed,
re-arming satisfied, threshold above zero, percentage below the threshold.

The full control-by-control description, the reason each gate exists and what is deliberately not done
is in [AUTONEXUS.md](AUTONEXUS.md); the step-by-step operator procedure, dry run included, is in
[AUTONEXUS-GUIDE.md](AUTONEXUS-GUIDE.md).

### 4.3 Turning it on

```powershell
# one run, no file edits
.\drelay.ps1 -JarArg -Ddrelay.nexus.enabled=true -Ddrelay.nexus.dryRun=true -Ddrelay.nexus.percent=35

# or straight at the jar, which is the same thing on any install:
java -jar drelay.jar -Ddrelay.nexus.enabled=true -Ddrelay.nexus.dryRun=true -Ddrelay.nexus.percent=35
```

or in `relay-routes.json`:

```json
"autoNexus": { "enabled": true, "dryRun": false, "thresholdPercent": 40, "maxPerWorld": 2 }
```

or live, from the dashboard or with one call:

```powershell
curl.exe -s -X POST http://127.0.0.1:8765/api/nexus `
  -H "content-type: application/json" -d '{\"enabled\":true,\"dry_run\":false,\"threshold_percent\":40}'
```

Every change is recorded as a `note` event with the request, what was applied, and the resulting
config, so a session's log names the settings that produced it. An unknown key is reported back rather
than ignored, because a typo would otherwise look like a setting that simply had no effect.

### 4.4 The one thing to watch on a first live run

The relay cannot tell whether the server accepts an injected escape until it answers. The server's
verdict is `GmEscapeAck` (id 158) - which the stock client **discards**, so the relay is the only place
it can be seen. Every ack is recorded, matched to the injection it answers:

```powershell
jq -c 'select(.pkt=="EscapeAck") | {t,note:.data}' work\logs\events-*.jsonl
```

`server refused the escape` in the nexus log means the instant form was not accepted, and the
`useCastChannel` setting is the next thing to try. `GmForcedEscape` (id 184) carries the server's own
reason string.

### 4.5 What is deliberately not done

- **No suppression of the client's own escape.** The client sends `GmEscape` itself whenever the player
  presses the nexus key, and the server tolerates repeats (the capture shows the client sending a
  second request 140 ms after the first when the first appeared to do nothing). Suppressing the
  client's packet would mean dropping traffic, which is a worse failure mode than a duplicate escape.
- **No interaction with the client's cast state.** The relay sends the instant form unless asked
  otherwise; it never cancels a cast the client started.
- **No prediction.** See §4.1.

---

## 5. Changing a packet the server sent

Everything above *adds* bytes to the client→server stream. This section is the feature that changes
bytes the server wrote, and it is now a module of its own: a dashboard panel, a saved setting and a
set of effects rather than one.

### 5.1 Why it exists

`StatusEffect.Confused` — "Scrambles core movement controls" — is applied **client-side only**. The
server states the effect; the client is the one that swaps the movement axes; and nothing in the
client expires the effect on a timer. So the only way for a session not to be affected by it is for
the client never to learn about it.

It matters. In the 2026-10-05 session the character died at 10% HP with `Confused` and `Slowed`
active, under a boss that re-applied the debuff every few hundred milliseconds for the last 1.5
seconds of the fight.

### 5.2 Where an effect is on the wire, and which ordinals

`StatsType.StatusEffects` (78) inside a `GmUpdate` (id 1), with `DataType 8`:

```
u8 count, then count × 9 bytes:  [i32 Effect][u8 Tier][f32 Duration]   (all little-endian)
```

The ordinal is the declaration order of `DarzaCore.Data.StatusEffect`, which is not obfuscated. The
four the dashboard offers are:

| ordinal | effect | what it does | on the local player, 2026-10-05 capture |
|---|---|---|---|
| 6 | `Paralyzed` | holds the character in place | 4 entries, 3 objects, 0.2–2.5 s |
| 7 | `Slowed` | reduces movement speed | 69 entries, 15 objects, 0.1–4 s |
| 11 | `Confused` | scrambles core movement controls | 5 entries, 2 objects, 0.15–0.85 s |
| 16 | `Hallucinating` | swaps the character's sprites | 20 entries, 2 objects, 0.2–10 s |

Those counts are measured, not assumed: the capture's 26,278 fully-decoded `GmUpdate` packets contain
125,320 status entries, of which 293 are ordinal 6, 3,508 are ordinal 7 and 152 are ordinal 11. The
per-player counts come from replaying the capture through `PlayerLocator`'s own health-vote
derivation — 68 (session, world) pairs, every one with a clear margin — and the last of those
resolutions is object 1930, the same object as the test fixture, which carries `Confused` and `Slowed`
in the final 1.5 seconds before that character died.

Two things follow for the counts. Confused and Slowed genuinely land on the player, so stripping them
is worth having; Paralyzed managed four entries in four hours, so arming it is close to a no-op. And
all four also land on *other* players and on entities, which is the failure mode §5.4 bounds. Which of
them are actually armed, though, is decided by the next section — not by how often they arrive.

The legacy `StatsType.Confused` (68) byte stat is **never** used in that capture, so the stat-78 list
is the only channel.

#### Which effects are safe to remove, and which are a trap

The question that decides it is not "is this a debuff" but **"does the client derive something from
the effect list that it then puts on the wire?"** The client's effect-driven laws are movement
speed, attack rate, regen/HP display, sprite selection and the input remap, and of those only the
first reaches the server as something it re-simulates. `PlayerFunctions.TilesPerSecond` is the whole
of the first law, and its parameters are the effect flags:

```
if (paralyzed || cutscene) return 0;                  // frozen
if (fearOfTheBull && speed > 40) speed = 40;          // speed capped
if (grounded && !onSafeTile(tileType)) speed /= ...;  // base speed reduced
speed *= GetValue(Slowed, slowTier) * GetValue(Speedy, speedyTier)
```

The client computes its position stream from that number and the server runs the same law. Remove an
entry the server's copy still carries and the client moves at 1.0× while the server's model allows
0.85/0.70/0.55/0.45/0.35× — a speed check, and a kick. That is one family: **`Slowed` (7),
`Paralyzed` (6), `Cutscene` (24), `Grounded` (29) and `FearOfTheBull` (23)**. Cutscene is the worst
of them because the law returns exactly `0`, and the capture shows it re-sent six times over ten
seconds with a sentinel duration.

Everything with a mechanical consequence is either **server-computed** — damage, damage-taken
modifiers, `Dispel`, `Minify`/`Gigantify`, the boss-side immunities: the client never sends damage,
the server resolves hitboxes and removes buffs, so deleting an icon changes nothing and there is
nothing to detect — or **inert**: `Bleeding`, `ArmorBroken`, `Burning`, `Vulnerable`, `Weak` feed
local prediction and the bar, and the next server packet corrects both.

`Hallucinating` (16) is the one genuinely safe effect. Its entire client-side consequence is
`World.SetHallucinating` → `GameObject.ToggleHallucinating` → `LoadSprites(...)`: the server sends a
flag and the client picks random sprites locally. Nothing else reads it — not movement, not input,
not combat, not a packet — so there is nothing on the wire for the server to compare and nothing to
kick over. That is confirmed, so it is armed by default alongside Confused. It is a cosmetic gain
rather than a mechanical one — a quieter screen, not a faster character — but it is the only addition
to the default set that costs nothing in exposure.

**These two are the default set** — Confused (11) and Hallucinating (16) — and the include-list is
deliberate. Every other effect is either server-computed (so arming it is inert) or in the movement
law (so arming it risks the run), which means there is no third category to add. A "strip all debuffs"
mode is the shape of change that turns this module into a speed-check generator, which is why the
panel offers names one at a time and why the registry's `knownKeys` is an explicit list.

### 5.3 Why not just shorten the duration

Because it does nothing, and it looks like it should work. The client's only status model is
`Entity.Effects.EffectData`, which every server list **replaces wholesale**
(`StatusEffects.UpdateEffects`: `EffectData = statusEffects`). "Do I have this effect" is answered by
`ContainsEffect`, which scans the array comparing `Effect == effect` and never reads `Duration`.
`StatusEffectInstance`'s equality is a hash of `Effect`, `Tier` and `Id` — not `Duration` — and the
client's own ticking status model (`DarzaGameNet.Packets.Status`) is never instantiated anywhere in
the shipped client, so nothing decrements it locally. An entry of zero seconds and an entry of five
seconds are the same entry.

### 5.4 How the strip works, and what it refuses to do

`networking.packets.UpdateScan` walks the payload and reports the *offsets* of each status list;
`networking.packets.StatusStrip` cuts the nine bytes of each armed effect out and rebuilds the
payload, and `networking.Strip` owns the settings, the counters and the log line for it. `Relay.pump`
re-frames the result with the session's own detected length byte order. Three properties are
load-bearing:

- **the walk must land on the exact last byte.** A walk that "mostly" worked yields offsets that are
  wrong by a few bytes, and a rewrite built on them desynchronises the session — a failure that looks
  like a server problem hours later, with nothing in the log pointing at the cause. `UpdateScan.walk`
  returns `false` for anything it cannot account for, and `StatusStrip` refuses a payload it cannot
  re-read after rewriting.
- **only one object is touched.** Whose list gets rewritten is `networking.PlayerLocator`, which
  derives the local player's object id from `GmHealthUpdate`: the object whose `Hp`/`Health` stat
  equals a reading that just arrived on this connection is the player. The exact marker
  (`StatsType.OwnCharacterId`, 183) exists but is sent only on the full dumps at world entry and at
  the death transition — nine times in four hours — which is too late to be useful on its own, so it
  is used as a confirmation when it appears. A world that cannot be resolved is left alone, and every
  resolution is logged.
- **the log keeps the original.** The packet event is emitted before the rewrite, so `events-*.jsonl`
  still shows what the server actually said; the rewrite is a separate `note` event carrying the
  object id, the effect ordinals, the byte counts and both payloads.

### 5.5 Turning it on, off, and up

**On by default**, with Confused (11) and Hallucinating (16) armed and nothing else. The reasoning is
in §5.2: both are applied client-side, so the only way not to suffer them is not to be told about
them — and both are safe because neither feeds a law the server re-simulates. It is also the most
visible kind of change the relay makes, so every start prints what is armed:

```
strip: removing status effect(s) 11 (confused), 16 (hallucinating) from the local player's lists (needs 3 matching health readings)
strip: disabled (server->client payloads are forwarded verbatim)
```

Three ways to change it, all equivalent:

```jsonc
// the route table (see the "strip" block in relay-routes.json)
"strip": { "enabled": true, "effects": [11, 16], "minVotes": 3 }
```

```powershell
# one run, no file edits
java -jar drelay.jar -Ddrelay.strip.enabled=false          # forward everything verbatim
java -jar drelay.jar -Ddrelay.strip.effects=11,16          # the shipped default, spelled out
java -jar drelay.jar -Ddrelay.strip.effects=11,16,7        # add Slowed - see the warning below
java -jar drelay.jar -Ddrelay.strip.hallucinating=false     # disarm the cosmetic one only
java -jar drelay.jar -Ddrelay.strip.minVotes=5             # demand more evidence before acting
```

```powershell
# live, from the dashboard's "status effect strip" panel, or with one call
curl.exe -s -X POST http://127.0.0.1:8765/api/strip `
  -H "content-type: application/json" -d '{\"enabled\":true,\"effects\":[11,16],\"min_votes\":3}'
```

`effects` is the set of `StatusEffect` ordinals to remove; `effect` (singular) is the pre-module form
and still works, as do the named switches `confused`, `paralyzed`, `slowed` and `hallucinating`, each
of which arms or disarms one effect by name. Note that `effect`/`effects` **replace** the set while a
named switch adjusts it, so an existing route table that spells out `"effects": [11]` keeps exactly
what it says — it is not retro-fitted with the new default. `minVotes` is how many
health readings an object must match before the relay believes it is the local player — raising it
makes a mis-identification less likely and the feature slower to arm. A change made in the dashboard is
written back into the route table the relay was started with (§3.5), so it survives a restart; that is
also why the panel shows whether the write succeeded.

Three things to know before a live run:

- **the server keeps sending it.** The client's copy is replaced by every list the server sends, so
  the strip must fire on every occurrence, and it will. What it cannot do is stop the server applying
  the effect to its own model; if anything server-side ever depends on the player being confused or
  slowed, this is where that would show up.
- **a crowded world can mis-identify the player.** In a fifteen-player fight, several characters can
  be at the same health as the operator at the same moment. Replaying the capture resolves the right
  object in every session and every world checked against the `OwnCharacterId` marker, but one busy
  world in that capture stayed ambiguous. The blast radius is bounded — one other object loses the
  armed effects it was carrying — and the log names the object it acted on, so it is visible rather
  than silent. Arming more effects widens that blast radius, which is the other reason Paralyzed and
  Slowed are off until someone asks for them: the default set is kept to the two effects that cost
  nothing.
- **the server says why it kicked you, so read that first.** `GmKicked` (id 185) carries a string16
  reason, and the relay decodes it into `data.reason` on the packet event, repeats it on that event's
  `note`, and latches it onto the session-close event as `kickedReason`. An armed effect that the
  server validates — Slowed is the proven one — fails as a kick within a second of the strip, and the
  reason names it instead of leaving a bare `disconnected`. A candidate that is genuinely
  unvalidated shows the opposite shape: repeated strips in the log and no kick at all. That is how
  Hallucinating was cleared.

### 5.6 Verification

`tools/tests/test_status_strip.py` drives a real relay against a fake server in five configurations and
asserts each: with the module switched off the bytes are identical; with no `strip` block at all it is
on and removes exactly the two defaults, Confused and Hallucinating; with Confused armed *alone* only
the local player's Confused entry goes, the packet is exactly nine bytes shorter and another object's
`Slowed` in the same packet is untouched; with Paralyzed and Slowed armed as well all three go; and
with Hallucinating armed *alone* only its entry goes. The fixture carries all four named effects in
every case, so the two single-effect cases are what make the defaults independently attributable
rather than only ever removed together. Every case
also checks that the framing survives a following packet and that the log names the object and the
ordinals. `tools/tests/test_dashboard.py` checks the panel and the persistence: the named list is
exactly `confused, paralyzed, slowed, hallucinating`, the armed flags are exactly
`true, false, false, true`, `POST /api/strip`
applies, is written into the route table, and leaves the comment block, the routes and unknown keys
intact.

The offline suites pin the parts a live run cannot: `networking.UpdateScanTests` walks every
`DataType`, refuses truncated payloads, strips several effects at once, and includes one real captured
packet — `seq 331827` of the 2026-10-05 session, the `GmUpdate` that carried `Confused`, `Slowed` and
`Barrier` on object 1930 100 ms after that character died. `networking.SettingsTests` covers the
config's spellings (including the legacy `confused`/`effect` form and `hallucinating` by name), the
default armed set being exactly Confused + Hallucinating in that order and never containing an effect
from the movement law, the writer's refusal to lose a key, and the launcher
carrying dashboard settings across a regeneration. `networking.InjectionTests` decodes a `GmKicked`
fixture and follows its reason all the way into both log events — the packet's `data.reason` and the
session-close event's `kickedReason` — because a reason that decodes but is never emitted is the same
as no reason at all. That last path is also checked end to end on disk: `tools/tests/test_reconnect_log.py`
sends a real `GmKicked` through a real relay process and asserts the forwarded bytes are unchanged,
the text log marks it `Kicked (codec)`, and `events-*.jsonl` carries the reason on both events.

`work/` holds the capture-level tooling used to establish all of this: `live_stat_ids.py` (every stat
id in a run, with a complete walk), `scan_events_status.py` (effect ordinals and durations),
`live_player_id.py` (player identification margins per world), `make_replay.py` plus
`networking.CaptureReplay` (replays a capture through the real Java walker, locator and strip; its
fourth argument is the armed effect set, so "would arming Slowed have touched anyone else" is a
one-line experiment).

---

## 6. Running the verification

```powershell
# build (javac and jar, no Maven needed) and run the Java checks with it
.\build.ps1

# the offline suites (no game, no elevation)
java -cp "target\classes;target\test-classes" networking.PrimitiveTests     # primitives, codecs, ids, varints
java -cp "target\classes;target\test-classes" networking.FrameTests         # framing, both byte orders
java -cp "target\classes;target\test-classes" networking.InjectionTests     # injected bytes, the state gate, the rule, the write lock
java -cp "target\classes;target\test-classes" networking.ClientPacketsTests # the client's clock field in Move/Shoot/ActivateObject
java -cp "target\classes;target\test-classes" networking.UpdateScanTests      # the GmUpdate walk, the strip, the player locator
java -cp "target\classes;target\test-classes" networking.SettingsTests        # settings parsing, the route-table write, the filter round trip
python tools\tests\test_relay.py                     # byte-exact forwarding
python tools\tests\test_relay_little_endian.py       # both length orders end to end
python tools\tests\test_reconnect_log.py             # a retarget is decoded and learned, and a kick reason reaches the log
python tools\tests\test_auto_nexus.py                # the whole auto-nexus chain against a fake server
python tools\tests\test_status_strip.py              # a server->client rewrite: on by default, narrow, re-framed
python tools\tests\test_dashboard.py                 # the page parses; every endpoint it reads has the right shape, and settings persist
python tools\tests\test_relay_stress.py              # both directions under concurrent load: no torn frames,
                                                     # monotonic event log, ring cursor skips nothing
python tools\verify_packet_ids.py                    # ids vs the client enums (needs DRELAY_DECOMP)
```

The Java checks live in `src/test/java` and are standalone main classes rather than a JUnit suite, so
they are run with `java -cp` (as above) instead of through Surefire — which is also why the jar does
not contain them. `tools\tests\test_relay_live.py` needs the real server to be up and the launcher
needs elevation; neither is part of the offline set.

### A layout discrepancy worth recording

`ClientPacketsTests` decodes a real captured `Move` and pins the offset of the client's clock, and in
doing so it turned up something that does not add up: the captured `Move` body is

```
0000E942 0000F142 B9F301 00
└─ X ──┘ └─ Y ──┘ └time┘ └ one byte ┘
```

whereas the decompiled `GmMove` declares `Position`, `Time`, then `HistoryCount` (a byte) followed by a
`HistoryCount`-long array of points. That trailing array has no room in the payload. The capture is
build 5.8.2 and the decompiled source is that same build, so either the field list is richer than the
source suggests or its order differs - and `Move` is one of the client's most frequent packets, so its
declared size and the captured one disagreeing is a real gap rather than a rounding error.

It does not affect anything shipped: the relay reads `Time` and stops, and `Time` is before the
unexplained field in both readings. It is recorded because the same assumption - that the decompiled
field list is complete - is the one that produced three wrong layouts elsewhere in this project
(see §1.1). Anyone extending `ClientPackets` to read further into a `Move` should decode a real
packet to its exact last byte first.
