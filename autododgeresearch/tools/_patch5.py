# -*- coding: utf-8 -*-
import io
p = r"G:\coding\javamapp\drelay\autododgeresearch\CLIENT-MAP.md"
s = io.open(p, encoding="utf-8").read()

def block(start_marker, end_marker):
    i = s.index(start_marker)
    j = s.index(end_marker, i)
    return s[i:j]

# ---- 10.1
b = block("### 10.1 Blocking", "### 10.2 Blocking")
new = """### 10.1 ~~Blocking: is the server authoritative over hits, or does it trust the client's claim?~~
**ANSWERED [live]: the client's Game_Hit report drives the server's damage, with a ~56-99 ms reconciliation lag.**

Source: `autododgeresearch/tools/_projhit_raw.txt` and `tools/projhit_damage_report.py` (live captured
game traffic, three runs).

* **580 `GmHit` (id 102) packets** captured: **419 with `PlayerProj = true`** (our shots) and
  **161 with `PlayerProj = false`** (we were hit).
* **105 of the 160 analysable `PlayerProj = false` reports are followed by an HP fall** by the next
  health reading. Fall size as % of max HP: **median 6.7, p90 10.5, max 54.6**.
* **Report -> reflecting HP reading lag: min 1 / p10 11 / median 56 / p90 99 / max 1478 ms** (n=160).
* Health is cadenced in a **double-mode ~50 ms / ~100 ms** pattern (`tools/_numbers.txt`: 6087
  inter-arrival samples, histogram peaks at **46-55 ms = 41.7 %** and **96-105 ms = 42.7 %**), with
  measured `CheckPing` RTTs of **40-117 ms, median ~62 ms** on the tested servers (`tools/_rtt_raw.txt`).
* Worked example, `Game_Slave#14`: reports at t=1516905 (projectile 235221) and t=1516950
  (projectile 235223), same `ObjectId` 460542, both resolve to the reading at t=1516967,
  **707 -> 680**. **Two ~28-damage reports aggregate into one 27-HP drop** - one health reading can
  carry several hits, which is why raw per-report deltas do not line up 1:1.

**Consequence for the design.** Suppressing the local `HitCharacter()` damage also suppresses the
`GmHit` at `Projectile.cs:1581` (they are the same code path), and the server demonstrably *applies*
what the client reports - a purely server-side re-simulation would not produce a 56 ms
report->drop correlation at all.

**Remaining experiment (the reduced form of this gap).** Patch `Client.SendPacket` to drop exactly one
outbound `GmHit` and confirm no HP fall follows within ~2 s. Absence confirms the client report is the
*sole* trigger.

**Latency budget.** Every autododge timing budget must include the **56-99 ms reconciliation lag** and
the **~50-100 ms health cadence** on top of the RTT - a fact no static read could have produced.

"""
s = s.replace(b, new)

# ---- 10.2
b = block("### 10.2 Blocking", "### 10.3 Blocking")
new = """### 10.2 ~~Blocking: the five opaque armour coefficients~~ **CLOSED - resolved from the decrypted pool**

The values are **0.7** (`hehe(74)`), **0.007** (`hehe(215)`), **100.0** (`hehe(113)`), **15.0**
(`hehe(69)`) and **25.0** (`hehe(101)`), all from the decrypted *double* pool. See Sec 3.7 and Sec 9.4,
and `autododgeresearch/CLIENT-FINDINGS.md` Sec 2.9.

*Historical note, kept for honesty:* the task statement anticipated `0.74 / 0.215` for these
coefficients. That was the right **form** and the wrong **numbers** - the real pair is `0.7 / 0.007`
with a **cap of 100** armour points.

### 10.2b Low: the remaining unlooked-up literal indices

The four pools are decrypted (`work/_pool.bin`, `work/_pooldecode.py`) and
`autododgeresearch/tools/poolresolve.py` resolves any index by pairing `ldc.i4 N` with the consuming
`call <ty> ...(int32)`. Still unread and still wanted, but all obtainable with **no live run**:

| index | what it is | site |
|---|---|---|
| `ProjDesc.cs:1208` divisor | **boomerang half-range** | if 2, peak = `Lifetime*Speed/2` |
| `hehe(16)` | the radius halving (0.5?) | `GameObject.cs:1570` |
| `hehe(110)` | deferred projectile-spawn TTL (ms) | `World.cs:11706` |
| `hehe(119)` | frame dt clamp (ms) | `DarzaWindow.cs:1775` |
| `hehe(45)` | move-history step threshold | `World.cs:12864` |
| `hehe(752)` | homing steer dead-zone squared | `Projectile.cs:542` |
| `hehe(5)`, `hehe(6)`, `hehe(7)` | the three sentinels used for zero/one comparisons | throughout |
| the `StatusEffect` ids | `hehe(22)`, `hehe(42)`, `hehe(44)`, `hehe(75)`, `hehe(77)`, `hehe(78)`, `hehe(91)`, `hehe(115)`, `hehe(231)`, `hehe(40)`, `hehe(84)`, `hehe(107)`, `hehe(160)`, `hehe(278)` | damage + movement gates |
| `hehe(21)`, `hehe(69)` (int pool) | escape cast duration / cooldown (seconds) | `World.cs:12995,13020` |

"""
s = s.replace(b, new)

# ---- 10.3
b = block("### 10.3 Blocking", "### 10.4 High")
new = """### 10.3 ~~Blocking: the movement constants~~ **CLOSED - resolved from the decrypted pool**

`TilesPerSecond` is exactly **`(3.5 + 5.0 * (SPD + 10.0) / 75.0) * slowMult * speedyMult`**, i.e.
**3.5 tiles/s at SPD 0 and 8.5 tiles/s at the SPD 75 cap**, clamped to SPD 40 under `FearOfTheBull`,
halved when `Grounded` off a safe tile, and 0 while paralysed or in a cutscene (Sec 6.2, Sec 9.4).

Any escape budget derived from the reference game's `4.0 + 5.6*(SPD/75)` (which the pre-existing
`work/auto-dodge-feasibility.md` used) is **~13 % too generous**.

**Residual:** the **units-per-tile ratio** (world tile -> screen pixels) is still only the runtime
camera zoom `World.BaseNodeScale` (Sec 4.3). It does not affect prediction, which should stay in tiles;
it only affects any budget quoted in pixels.

"""
s = s.replace(b, new)
io.open(p, "w", encoding="utf-8").write(s)
print("ok ->", len(s))