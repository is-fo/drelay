# Auto-nexus — usage guide

The operator's manual for the auto-nexus rule: how to turn it on safely, what dry run does and does not
do, how to pick a threshold, what to watch on the dashboard, and how to read the logs when something
went wrong. It assumes the relay is already carrying a session - see the
[README](../README.md) for that.

The design, the gates and a control-by-control explanation live in
[AUTONEXUS.md](AUTONEXUS.md). Read this one if you just want to use the thing.

---

## 1. The short version

The rule is off by default and starts in dry run when switched on. Arm it for one run from the command
line, then tune it live from the dashboard:

```powershell
# 1. dry run: the rule decides and logs, and writes nothing to the server
java -jar drelay.jar -Ddrelay.nexus.enabled=true -Ddrelay.nexus.dryRun=true -Ddrelay.nexus.percent=35

# 2. play. read http://127.0.0.1:8765/ and work/logs/nexus-*.jsonl
#    then decide whether the threshold is where you want it

# 3. when you are satisfied, arm it for real - either restart without dryRun:
java -jar drelay.jar -Ddrelay.nexus.enabled=true -Ddrelay.nexus.dryRun=false -Ddrelay.nexus.percent=35
#    ... or turn dry run off in the dashboard's auto-nexus panel while the session runs
```

From a source tree, `.\drelay.ps1 -JarArg -Ddrelay.nexus.enabled=true` passes the same
properties through. The launcher does not have `-Nexus` flags of its own any more: every setting is a
`-Ddrelay.nexus.*` property, an environment variable, a route-table key or a dashboard control, which
is one mechanism instead of two.

Nothing is armed by default. `enabled` defaults to false, and even with it on, `dryRun` defaults to
true - the two gates are independent, and the second is the one that keeps the feature honest.

## 2. What dry run is, and what it is not

**Dry run is the whole rule minus the write.** The trigger, the gates, the threshold comparison, the
counters and the log lines all behave exactly as they do live. The single difference is that
`AutoNexus.fire()` is never called, so **no bytes reach the server** - and that one difference has two
knock-on effects, both listed below: the per-world budget is never consumed, and the "delay" clock never
starts.

| | dry run | live |
|---|---|---|
| reads `GmHealthUpdate`, computes the percentage | yes | yes |
| applies every gate (world, health, safe area, budget, threshold, ...) | yes | yes |
| increments `decisions`, `declines`, `dry runs` | yes | yes (`nexused` instead) |
| writes a `nexus` event per decision | yes, `DRY RUN would nexus: ...` | yes, `NEXUS: ...` |
| writes `GmEscape` to the server | **no** | yes |
| consumes the per-world budget | **no** | yes |
| starts the "delay" clock | **no** | yes |
| can be acked or refused by the server | **no ack exists** | yes |

Three consequences that catch people out:

- **The "delay ms" control does nothing in dry run**, because the clock it measures only starts on a
  real injection. That is not a bug; see [AUTONEXUS.md §8](AUTONEXUS.md#8-the-delay-option-and-why-it-looks-like-it-does-nothing).
- **`max/world` is never consumed in dry run**, so a dry run can report far more would-be nexuses per
  world than a live run would ever fire. That is deliberate: the point of dry run is to see how often
  the threshold is crossed, not to simulate the budget.
- **A dry run proves nothing about the server's verdict.** The only way to learn whether an injected
  escape is accepted is to send one and read `EscapeAck`, which the stock client discards. That is what
  step 3 above is for, and it is why the first live run should be a short one.

**A dry run cannot hurt the character.** It writes nothing at all on the client→server path - the
relay's only other writes are the client's own packets, forwarded unchanged.

## 3. Turning it on, three ways

Settings can come from any of the three surfaces, and the later ones win:

1. **`relay-routes.json`** - the checked-in defaults:

   ```jsonc
   "autoNexus": {
     "enabled": true, "dryRun": true, "thresholdPercent": 35,
     "useEffectiveHp": false, "skipInSafeArea": true,
     "minIntervalMillis": 1200, "maxPerWorld": 3, "rearmPercent": 0
   }
   ```

2. **System properties or environment variables** - one run, no file edit, and they override the file:

   | property | environment | meaning |
   |---|---|---|
   | `-Ddrelay.nexus.enabled` | `DRELAY_NEXUS_ENABLED` | master switch |
   | `-Ddrelay.nexus.dryRun` | `DRELAY_NEXUS_DRY_RUN` | decide only |
   | `-Ddrelay.nexus.percent` | `DRELAY_NEXUS_PERCENT` | threshold % |
   | `-Ddrelay.nexus.effective` | `DRELAY_NEXUS_EFFECTIVE` | count shield+barrier |
   | `-Ddrelay.nexus.delayMs` | `DRELAY_NEXUS_DELAY_MS` | minimum gap between injected escapes |
   | `-Ddrelay.nexus.maxPerWorld` | `DRELAY_NEXUS_MAX_PER_WORLD` | budget per world |
   | `-Ddrelay.nexus.skipSafeArea` | `DRELAY_NEXUS_SKIP_SAFE_AREA` | decline in town |
   | `-Ddrelay.nexus.rearm` | `DRELAY_NEXUS_REARM` | re-arm level % |
   | `-Ddrelay.nexus.castChannel` | `DRELAY_NEXUS_CAST_CHANNEL` | channelled escape (not on the dashboard) |

3. **The dashboard, live, mid-session** - the **auto nexus** panel. Press **apply**; the change takes
   effect on the next health reading. Every change is recorded as a `note` event carrying the request,
   what was applied, and the whole resulting config, so the log always says which settings produced a
   run.

Keys are forgiving: `threshold_percent`, `thresholdPercent` and `threshold-percent` are the same key,
and an unrecognised one is reported back as `unknown:<key>` rather than silently ignored.

## 4. Reading the dashboard

`http://127.0.0.1:8765/` (`-Ddrelay.web.port=0` disables it, `-1` takes any free port; if the configured
port is busy the next four are tried). The URL is printed at startup.

The header, left to right, tells you whether anything can happen at all:

| pill | what it means | if it is not what you expect |
|---|---|---|
| `connecting…` / `live` / `disconnected` | the page's link to the relay's API | the relay is not running, or the page was opened before it started |
| `no session` / `<route>#<n> · <phase>` | the **newest** session and its phase (`NEW`, `CONNECTED`, `HELLO`, `IN_WORLD`, `CLOSED`) | you are still in the lobby, or the newest session is not the one you are playing |
| `injection disarmed` / `injection armed` | the world gate: has the client's `Hello` **and** a `MapInfo` been seen | this is the usual reason nothing fires. `IN_WORLD` and `injection armed` go together |
| `auto-nexus off` / `auto-nexus dry run` / `AUTO-NEXUS LIVE` | the master switch and dry run | note that LIVE only means `enabled && !dryRun`; a threshold of 0 can never fire while it still says LIVE |

Then:

- **character hp** - the newest reading: raw percent, effective percent, how stale it is, and the
  shield/barrier/sample/world/safe-area/cast values behind it. The bar turns red at or below the
  threshold. If it never shows a number, no `HealthUpdate` has arrived in this world yet.
- **auto nexus** - the controls (§5) plus the counters. `decisions` is every reading the rule
  evaluated, `nexused` is escapes written, `dry runs` is would-haves, `declines` is everything else.
  In a healthy live run below the threshold you should see `nexused` increase by one and `declines`
  by a handful around it. The `last ack` line is the server's verdict.
- **sessions** - every session, its phase, whether injection is armed, and its own health.
- **filters** / **packets** - the live stream. The default filter, *character hp*, keeps the health
  readings, the whole escape conversation, world entry, and every action the relay took. If you change
  nothing else, leave it: without the relay's own `nexus`/`inject` events the view shows the symptom and
  hides the cause.

## 5. Choosing a threshold

The threshold is the only setting that needs a real decision, and the dry run is how you make it.

1. **Set the threshold low and dry-run a session you would call normal.** 35% is a reasonable start.
2. **Look at what a dry run reports.** What matters is not the count of would-be nexuses but *where in
   the HP curve* they land, and whether they were single readings or the start of a slide.
3. **Read the shape of the ramp, not just the crossings.** In the recorded session the rule saw the
   character fall from ~97% to 33% over about twenty seconds, with the threshold crossed on the way
   down and never again. That is a good threshold: it fires on a real slide with hundreds of
   milliseconds of margin, not on a spike.
4. **If it fires constantly at 35%, the threshold is doing its job** - the character is genuinely
   living below it. Either the character needs better gear for the content, or you want a lower number
   and accept that the rule will only catch the last part of a slide.
5. **If it never fires, check `declines` before moving the threshold.** The dashboard's `last ack` and
   the nexus log both name the reason (see §7). "the server says this is a safe area" and "injection is
   not armed" are not threshold problems.

A useful cross-check while tuning: the HP panel shows both the raw percentage and the effective one.
Leave **hp+shield+barrier** off until you have seen the two diverge; turn it on when your gear gives you
a shield worth counting, and expect the rule to fire later (a shielded character's raw HP is lower than
its real pool).

## 6. Choosing the other settings

| setting | guidance |
|---|---|
| **delay ms** | leave it at 1200 unless you have a reason. It is a duplicate suppressor covering the gap between an escape and the world change; it cannot fire before the threshold is crossed, and it does nothing at all in dry run. [AUTONEXUS.md §8](AUTONEXUS.md#8-the-delay-option-and-why-it-looks-like-it-does-nothing) has the evidence. |
| **max/world** | 3 is generous. A world where the rule fires three times is a world where something already went wrong; the budget exists so a stuck-below-threshold character cannot be nexused on every reading. |
| **re-arm %** | leave at 0 unless you have seen repeated firing in one world. Setting it (say 70) makes the rule require a recovery before it will fire again, which is a preference rather than a safety property - and it can leave the rule disarmed when you want it. |
| **skip safe area** | leave it on. An escape in town achieves nothing. |
| **hp+shield+barrier** | see §5. On if your build carries a shield or barrier worth counting. |
| **cast channel** | not on the dashboard. Leave it off unless the server refuses the instant escape; it stalls both directions of the session for the whole wait, which freezes the client visibly. |

## 7. Troubleshooting: symptom → cause → fix

Every one of these is a line in the nexus log or a `declines` tick on the dashboard. Nothing in this
feature fails silently, which is the point.

| symptom | the log says | what it means, and what to do |
|---|---|---|
| the pill says disarmed and nothing happens | `injection is not armed (no world yet, or a fresh handshake)` | you are in the handshake or the lobby. Nothing to fix; wait for world entry. |
| the HP panel is empty, decisions all decline | `no HealthUpdate seen in this world` | the world just changed and the server has not sent a reading yet, or this is not a game session at all (the queue route is never evaluated). |
| declines say safe area | `the server says this is a safe area` | you are in town. `skip safe area` is on by default; leaving it on is correct. |
| declines say budget | `world budget spent (n/max)` | the rule has already fired `max` times in this world. A new `MapInfo` resets it. If this happens often, raise `max/world` deliberately or look at why the character keeps coming back below the threshold. |
| declines say interval | `only N ms since the last injection` | the duplicate suppressor working as intended, between an escape and the world change. Not a problem. |
| declines say re-arm | `waiting to re-arm (needs X%, at Y%)` | `re-arm %` is set and HP has not recovered. Lower it, or set it to 0. |
| everything says the threshold was not crossed | `N% is not below the M% threshold` | expected while healthy. Compare `N` with `M` before changing `M`. |
| it fires but nothing happens in game | `NEXUS: ...` with no `EscapeAck` after it | the escape went out and the server never answered. Check the acks below. |
| the server refused it | `injected escape #n REFUSED after X ms` | the instant form was not accepted. This is the case the channelled escape exists for; expect a visible freeze. |
| the ack is attributed to the client | `an escape was accepted (no injection pending in this session: the client sent it)` | you pressed the nexus key yourself. Not an injection; nothing was fired by the relay. |
| the pill says dry run and you expected live | nothing - dry run does not announce itself per event | `dry run` is still checked. Uncheck it and press **apply**. |
| nothing fires *and* `decisions` is 0 | - | `enabled` is off. The master switch returns before counting anything. |
| it fires but the client stutters for two seconds | `injected EscapeCastState` in the log | the channelled escape is on. Turn `useCastChannel` off in the config. |

## 8. Reading the logs afterwards

Two files, both under `work/logs/`, named by run:

- **`events-<run>.jsonl`** - everything, one JSON object per line: every packet with its raw payload
  and its decoded fields, plus every decision the relay made.
- **`nexus-<run>.jsonl`** - the subset worth reading first: the health readings, the whole escape
  conversation, world entry, and every `nexus`/`inject` event. Read it top to bottom and you have the
  story of the session in a minute.

```powershell
# the whole escape story, in order
Get-Content work\logs\nexus-*.jsonl | Select-String -Pattern '"pkt":"Escape'

# every decision the rule reached, with the numbers behind it
Get-Content work\logs\nexus-*.jsonl | Select-String -Pattern '"kind":"nexus"'

# did the server accept the escapes the relay sent?
Get-Content work\logs\nexus-*.jsonl | Select-String -Pattern '"pkt":"EscapeAck"'

# what the client sent on its own, that the relay did not
Get-Content work\logs\events-*.jsonl | Select-String -Pattern 'clientOwnEscape":true'
```

Three things to check, in this order:

1. **Was there an `EscapeAck` for every injected escape**, and did it carry `answersInjectionSeq`
   matching the injection's `seq`? That is the only proof the server accepted the relay's escape.
2. **What was `decisionLatencyMs`?** It should be 0-1 ms. If it is large, the relay thread was blocked
   by something else and the escape left late.
3. **How long was the window between the injection and the next world entry?** If it is much longer
   than the health cadence, the delay is doing real work in it; if the world changes immediately, there
   was nothing for it to suppress.

## 9. Turning it off

- **Right now, mid-session**: uncheck `enabled` in the dashboard and press **apply**.
- **For a run**: leave the `-Ddrelay.nexus.enabled` property off. The rule is off by default, so doing
  nothing is also a valid answer.
- **To make an accidental live run impossible**, set `"enabled": false` in the `autoNexus` block of your
  route table and do not pass the property. Dry run is not a substitute for off if what you want is for
  the relay never to write anything of its own.

## 10. Checking it without playing

None of these need the game, elevation, or a server:

```powershell
java -cp "target\classes;target\test-classes" networking.InjectionTests      # the gates, the budget, the interval, dry run, re-arming
python tools\tests\test_auto_nexus.py                        # the whole chain against a fake game server
python tools\tests\test_dashboard.py                         # the page parses, the endpoints have the right shape
```

`test_auto_nexus.py` drives a real relay against a fake server and asserts on the bytes that arrive:
nothing before the handshake, nothing before `MapInfo`, nothing while healthy, exactly one escape when
the threshold is crossed, and nothing more from a repeat reading. It is the closest thing to a
rehearsal of a live run.
