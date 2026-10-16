# Client findings — verified against build 5.8.2

This is the **evidence base** for everything else in `docs/hook/`. Every claim here was
read out of the shipped client, and each one carries the `file:line` it came from. Nothing in this
document is inferred from the proxy work, and nothing is assumed: where a value could not be
recovered statically it is marked `UNKNOWN — needs live measurement`.

The companion documents are [README.md](../../hook/README.md) (the index),
[HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) (how the hook is installed),
[DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) (what it computes),
[JAVA-BRIDGE.md](../bridge/JAVA-BRIDGE.md) (how it talks to the Java relay),
[IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) (the phased work), and
[RISKS.md](RISKS.md).

---

## 0. The runtime: what kind of thing are we hooking?

These five facts decide the entire architecture, and all five were measured, not assumed. The probe
is [`hook/work/pe_probe.py`](../../hook/work/pe_probe.py) and [`hook/work/md_probe.py`](../../hook/work/md_probe.py).

| fact | value | how it was established |
|---|---|---|
| Target | `DarzasDominion.exe` 1.0.0, Steam appid 1895130, x64 | install dir `E:\SteamLibrary\steamapps\common\Darza's Dominion`; `steam_appid.txt` |
| Runtime | **.NET 9.0.20, self-contained**, CoreCLR | `DarzasDominion.runtimeconfig.json`: `"tfm":"net9.0"`, `includedFrameworks` Microsoft.NETCore.App 9.0.20; `coreclr.dll`, `clrjit.dll` beside the app |
| Game code | **one 50 MB pure-IL assembly**, `DarzasDominion.dll` | `pe_probe.py`: `COMIMAGE_FLAGS = 0x00000001` (ILONLY set), `ManagedNativeHeader RVA = 0x0` — **no ReadyToRun block, no native code** |
| Compilation | **JIT, and tiered compilation is OFF** | `runtimeconfig.json`: `"System.Runtime.TieredCompilation": false`; the game's own log says `jit 280 methods, 329.2 ms` |
| Obfuscation | **symbol names and constants only** — the IL is intact | metadata parses cleanly with 23,743 `MethodDef` and 25,105 `Field` rows; the decompile preserves statements, branches and field order |

### 0.1 Why "tiered compilation off" is the single most useful fact

A method that is JIT-compiled once, at one tier, keeps **one native entry point for the whole
process lifetime**. There is no tier-0 → tier-1 promotion, so a patch or detour installed on a
method's native code can never be silently discarded by a recompile. The two hardest problems in
runtime JIT hooking — catching every compilation and surviving promotion — both disappear.

The residual risk is small and specific: a method that the JIT *inlines* into its caller has no
entry point of its own to patch. `DarzaWindow.Update(float)` and `Projectile.LateUpdate` are far
too large to inline, and the hook list in §9 is chosen with this in mind.

### 0.2 What is obfuscated, and what that costs us

Type and member **names** are mangled (`hehe_XXXX_XXXX_...`, `_E311_EE2E_F8F8_E14F_E1D6`), and
every literal is an index into an encrypted constant table
(`hehe_F244_F670_F803_F5DD_EB9E(N)`; the table is a manifest resource XORed with `0x6C0BA624`).
The consequences, in order of importance:

1. **Constants are not in the file.** `player.Radius`, the `TilesPerSecond` coefficients and every
   other literal must be recovered by **decrypting the pool** or by **reading the live value out of
   the process**. This is one bounded, well-defined job, not an open-ended one — see §10.
2. **Field order is intact.** The decompiler emits fields in declaration order, and `Radius`,
   `Damage`, `Desc`, `HitsPlayer` etc. all appear with real names. For IL2CPP this would have been
   fatal; for us it is close to ideal, because it means offsets are *derivable* rather than guessed.
3. **Method bodies are intact.** The control flow is preserved as flattened state machines with
   `switch (num ^ opaque)` dispatchers, which is ugly but semantically complete. Crucially we do
   **not** have to rewrite any of it — see §9, where the design calls the game's own methods rather
   than reimplementing them.

---

## 1. The frame loop

### 1.1 The entry point

```
WindowsGame : Microsoft.Xna.Framework.Game          Windows/WindowsGame.cs:18
  protected override void Update(GameTime gameTime) Windows/WindowsGame.cs:206
      → DarzaWindow?.Update((float)gameTime.TotalGameTime.TotalMilliseconds)   :260
  protected override void Draw(GameTime gameTime)   Windows/WindowsGame.cs:263
      → DarzaWindow?.Draw(gameTime)                                             :266
```

`WindowsGame.Update` also implements a spin-wait frame limiter bounded by
`base.TargetElapsedTime` (`:206-256`), so the game is **frame-rate limited**, not free-running.

### 1.2 The per-frame body

`DarzaWindow.Update(float total)` — `Graphics/DarzaWindow.cs:1727` — is the one method that runs
exactly once per frame, on the game's main thread, before any world state is advanced. In
execution order it does:

| # | call | line | note |
|---|---|---|---|
| 1 | `DynamicFont.WarmAtlasStep()` | `:1729` | |
| 2 | `hehe_F7CF_ED33_F133_E5EB_E683(total)` | `:1730` | time bookkeeping |
| 3 | `UiThreadWatchdog.Heartbeat()` | `:1733` | names this thread the UI thread |
| 4 | `World shared = World.Shared;` | `:1735` | **the static root of the live game state** |
| 5 | `Time.Elapsed = Math.Min(total - Time.Total, …)` | `:1775` | frame delta, clamped |
| 6 | `Client.DrainColdQueue();` | `:1763` | **the network tick**: applies queued server packets on this thread |
| 7 | `Broker.Update(Time);` | `:1771` | |
| 8 | `StallProbe.FrameBoundary();` | `:1779` | once per frame, diagnostics only |
| 9 | scene transition, if one is pending | `:1820-1835` | |
| 10 | `CurrentScene?.PreUpdate();` | `:1839` | |
| 11 | **`CurrentScene?.UpdateScene(Time);`** | `:1841` | **the world tick** |
| 12 | `CurrentScene?.LateUpdate();` | `:1843` | |
| 13 | drain the UI action queues | `:1845-1863` | |
| 14 | `RemoteImageCache.PumpUploads(5);` | `:1865` | |

(The `switch (num ^ …)` dispatcher makes the textual order in the listing misleading; the order
above is the one `switch` order that visits each case exactly once and terminates. `UpdateScene`
is the world tick because `Scene.UpdateScene` is `virtual` (`Nodes/Scene.cs:830`) and
`GameScene.UpdateScene` overrides it (`Scenes/GameScene.cs:1336`).)

### 1.3 The consequence for us

**`DarzaWindow.Update(float)` is the ideal once-per-frame hook.** It is `public`, non-virtual,
non-generic, has a single `float` parameter, is far too large to inline, and it runs **after**
`DrainColdQueue` has applied this frame's server packets and **before** `UpdateScene` advances the
world — which is exactly the vantage point a planner wants: the freshest possible world state, and
a full frame of budget in which to act on it.

### 1.4 The network boundary

`Client.DrainColdQueue()` (`DarzasDominion.Darza.Networking/Client.cs:502`) drains a static queue
and dispatches by an opcode enum (`:546-554`). This is the same boundary the existing `drelay`
proxy reasons about from outside: packets arrive on a network thread and are **applied on the main
thread here**. Nothing about the hook needs this, but it does explain why the hook sees post-packet
state at step 6 and not earlier.

`World.Shared` is declared `public static World Shared;` at `DarzasDominion.Darza.Game/World.cs:4216`.
It is the single static root, and resolving its address once gives the hook access to the entire
live object graph (§4).

### 1.5 Where each hook sits in the frame, precisely

Worth stating because it bounds the design's latency and because it is easy to get wrong:

```
DarzaWindow.Update(float)                            <- HOOK #1 (frame tick) runs HERE, at the top
  ...
  CurrentScene.UpdateScene(Time)                     DarzaWindow.cs:1841
    └─ World.Update(ClientTime)                      World.cs:9473
         Node.Update(time)                            World.cs:9552   <- children run
           ├─ layer / projectile nodes                <- HOOK #3 (collision) fires inside here
           └─ ...                                     <- HOOK #2 (spawn) fires inside here
         the player movement / attack / escape fn     World.cs:9561-9568
         the packet drain block                       World.cs:9650+
```

Two consequences, and both are properties of the design rather than accidents:

1. **The frame hook runs at the start of the frame, before the world tick.** The planner therefore
   always sees a *completed* previous frame — the freshest self-consistent state there is — and has a
   whole frame of budget to act. That costs one frame of latency, which the budget in
   [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §7 already accounts for, and it is the right trade:
   planning against half-updated state would be worse than planning one frame old.
2. **The spawn and collision hooks fire inside the world tick**, i.e. *after* this frame's frame-hook
   call. Their events are therefore consumed by **next** frame's decision. That is the same
   one-frame latency seen from the other side, and it is why the collision hook still buys ~55 ms:
   the frame it costs is ~10–17 ms against a 55.5 ms median advantage.

> **Do not use `Time.LogicElapsedSec` for hook timing.** The frame computes it as
> `(Stopwatch.ElapsedMilliseconds − Time.LogicTotal) / 1000` where `Time.LogicTotal` was assigned a
> few statements earlier in the same method (`DarzaWindow.cs:1783`, `:1787`), making it a residual
> near zero rather than a frame delta. `Time.ElapsedSec` is the frame delta. The hook measures its
> own frame interval regardless (frame counter plus a monotonic clock), so it depends on neither —
> but the anomaly is recorded because it is exactly the value a hook author would reach for.

---

## 2. The projectile model

### 2.1 Class hierarchy

```
Node                                   Nodes/Node.cs
└── GameObject                         Objects/GameObject.cs         (Radius, Position, World)
    └── Projectile                     Objects/Projectile.cs:26
```

`Projectile`'s own fields are declared at `Projectile.cs:47-177`; inherited ones matter just as
much, so both are listed.

### 2.2 Fields a threat model needs

| field | type | where | meaning |
|---|---|---|---|
| `Position` | `Vector3` | `Nodes/Node.cs:175` | **live position in world units**, updated every frame |
| `Radius` | `float` | `Objects/GameObject.cs:271` | the projectile's hit radius |
| `World` | `World` (ref) | `Objects/GameObject.cs:283` | back-pointer to the world |
| `Angle` | `float` | `Projectile.cs:111` | spawn angle, radians |
| `Damage` | `int` | `Projectile.cs:113` | **raw damage** for this bullet instance |
| `LocalSeedDerived` | `bool` | `Projectile.cs:115` | damage was rolled from a local seed |
| `DamagePercent` | `float` | `Projectile.cs:117` | fraction-of-current-HP damage |
| `ManaDamage` / `ManaDamagePercent` | `int` / `float` | `:119`, `:121` | |
| `BarrierDamage` / `BarrierDamagePercent` | `int` / `float` | `:123`, `:125` | |
| `CriticalHit` | `bool` | `Projectile.cs:127` | |
| `Desc` | `ProjDesc` (ref) | `Projectile.cs:129` | **the trajectory + shape descriptor** |
| `HitsPlayer` | `bool` | `Projectile.cs:131` | **is this a bullet that can hit me** |
| `PlayerOwned` | `bool` | `Projectile.cs:135` | mine (so ignore it) |
| `IsAbility` | `bool` | `Projectile.cs:137` | |
| `Affix` | `AffixData` | `Projectile.cs:139` | scales size and damage |
| `QualitySize` | `float` | `Projectile.cs:141` | |
| `GameId` | `int` | (on `Node`/`GameObject`) | **stable identity for a bullet** |
| `HomingWireTargetId` | `int` | `Projectile.cs:93` | non-zero ⇒ homing, and this is its only target |
| `Owner` | `Entity` | `Projectile.cs:75` (`_owner`) | who fired it |

Two private fields are load-bearing for prediction and would need offset resolution rather than
pure field order: `_startPosition` (`:83`), `_startTime` (`:85`) and `_angleSin`/`_angleCos`
(`:59`, `:63`). The current frame's trajectory input is `num = World.ClientTime - _startTime`
(`:1109`), so a predictor that wants to agree with the game exactly needs `World.ClientTime` and
`_startTime` — both readable, neither free.

### 2.3 Spawn — `Projectile.ProcessProjectileData`

```
Projectile.cs:810
public void ProcessProjectileData(float angle, ushort damage, float damagePercent,
    ushort manaDamage, float manaDamagePercent, int barrierDamage, float barrierDamagePercent,
    Vector3 position, ProjDesc desc, bool hitsPlayer, bool playerOwned, Entity owner,
    AffixData affix, bool shiny, bool criticalHit, bool isAbility = false)
```

This is the **complete damage and geometry payload for one new bullet, delivered as arguments**:
angle, every damage component, the spawn position, the descriptor that defines the trajectory, and
the two flags that matter (`hitsPlayer`, `playerOwned`). The body assigns each one to its field
(`Projectile.cs:840-932`, quoted in §2.2's line list).

For a native detour this is close to ideal: the arguments arrive in registers/stack under the
standard x64 calling convention, so the hook can read the whole event **without touching a single
field offset**.

### 2.4 Motion — the game does it, and we can borrow it

```
Projectile.LateUpdate(ClientTime time)              Objects/Projectile.cs:1091
```

Per frame, per projectile:

```csharp
int num = World.ClientTime - _startTime;                                  // :1109
bool alive = HomingDrives(num) ? StepHoming(num, out x, out y)            // :1151
           : Desc.WallBounce > 0 ? StepWallBounce(num, out x, out y)
           : Desc.GetPosition(num, _angleSin, _angleCos, out x, out y, GameId);
Position = AnchorBase() + new Vector3(x, y, 0f);                          // :1174
```

So **position is a pure function of elapsed time plus the descriptor**, evaluated by the game's own
`ProjDesc.GetPosition`:

```
ProjDesc.GetPosition(int time, float sin, float cos, out float x, out float y, int gameId = 0)
    DarzaCore.Data/ProjDesc.cs:1149   (virtual; overridden per ProjectilePath)
ProjDesc.GetVelocityAt(float dt)      DarzaCore.Data/ProjDesc.cs:956
```

Inside `GetPosition` the trajectory family is selected from descriptor fields, all visible at
`ProjDesc.cs:104-268`:

| feature | fields | line |
|---|---|---|
| base speed | `Speed`, `MaxSpeed` | `:208`, `:142` |
| acceleration stages | `Accelerations[]` (`Wait`, `StartSpeed`, `Amount`, `Delay`) | `:124`, `:966-1015` |
| speed wave | `SpeedWaveAmp`, `SpeedWaveFreq` | `:200`, `:198` |
| curve / orbit | `Curve`, `CurveDelay`, `CurveBreak`, `Orbit`, `OrbitDelay`, `OrbitBreak`, `NewCurve`, `AlternateCurve` | `:110-152`, `:1180-1204` |
| boomerang | `Boomerang` | `:106`, `:1205-1213` |
| parametric | `ComputeNewCurveLocal` path | `:1216` |
| lifetime | `Lifetime`, `Telegraph` | `:138`, `:196` |
| hit shape | `Size`, `SizeGain`, `Proximity`, `Pierces`, `RehitDelay` | `:164`, `:166`, `:202`, `:154`, `:162` |
| damage | `MinDamage`, `MaxDamage`, `DamageHealthPercent`, `DamageManaPercent`, `DamageBarrierPercent`, `DamageIncrement`, `IncDamage`, `CriticalChance`, `CanCriticalHit` | `:144`, `:140`, `:244`, `:248`, `:252`, `:254`, `:256`, `:268`, `:266` |
| flags | `IgnoreTerrain`, `IgnoreEntity`, `IgnoreArmor`, `BreaksArmor`, `Friendly`, `WallBounce`, `Anchored`, `HasEmit`, `HasOnExpire` | `:134`, `:132`, `:128`, `:108`, `:136`, `:204`, `:206`, `:182`, `:170` |

**This is the single most important design conclusion in the whole study: we do not reimplement
`GetPosition`.** It is a virtual method with a `ProjectilePath` subclass per trajectory family, it
calls a per-bullet seeded LCG (`LCG.Value.Initialize((uint)gameId)`, `:1223`) for its parametric
jitter, and getting any of that subtly wrong would produce a dodge that is confidently wrong. The
hook instead **calls the game's own `GetPosition`** to ask "where will bullet *B* be at time
*t*+Δ?", which makes prediction *exactly* consistent with the collision test by construction.

What we do need is the contract, and the contract is two lines of the game's own code:

```
hit ⇔ DistanceSquared(World.Player.Position) < (r_eff)²        Projectile.cs:1573
```

### 2.5 Collision — the predicate, exactly

```
Projectile.Hit()          Projectile.cs:1505   → HitsPlayer ? HitCharacter() : HitEnemy()
Projectile.HitCharacter() Projectile.cs:1514
```

The relevant sequence, quoted verbatim from the decompile:

```csharp
// Projectile.cs:1596-1599
IL_010a:
num2 = World.Player.Radius + Radius;          // default hit radius = player + bullet
// Projectile.cs:1552-1556
if (!PlayerOwned) num2 = MathF.Max(num2, (float)Desc.Proximity);   // Proximity widens it
// Projectile.cs:1572-1592
if (DistanceSquared(World.Player.Position) < num2 * num2
    && CanHitEntity(World.Player.GameId, clientTime))
{
    bool num3 = RegisterEntityHit(World.Player.GameId, clientTime);
    World.Player.HitBy(this);
    if (Prefs.close_menus_on_damage) base.Scene?.OnDamage();
    Client.SendPacket(new GmHit {
        Time = clientTime, ProjectileId = GameId,
        ObjectId = World.Player.GameId, PlayerProj = PlayerOwned });
    if (num3) return true;
}
```

Four facts fall out of this, and each one matters:

1. **Collision is a plain circle test.** `(dx² + dy²) < (playerRadius + bulletRadius)²`. There is no
   AABB, no Chebyshev metric, no separate visual hitbox. The constant pool hides the *value* of
   `player.Radius` but the *shape* is unambiguous, and it is the cheapest possible thing to predict:
   a bullet is dangerous iff its trajectory segment passes within `r_eff` of a point.
2. **`HitsPlayer` is the filter that gives us the threat set for free.** `Hit()`
   (`:1505`) branches on it, so only projectiles with `HitsPlayer == true` can ever call
   `HitCharacter()`. There is no need to infer ally/enemy from ownership.
3. **The remaining preconditions are cheap and readable**: `!KnockedUp` and `!SpiritMode`
   (`:1521`), and `CanHitEntity(gameId, clientTime)` (`:1573`), which is the re-hit guard keyed off
   `_lastHitAt` / `_hitDelay`.
4. **Homing bullets are restricted to their wire target.** `HomingWireTargetId != 0`
   (`:1520`, `:1527`) means the bullet only tests against that one `GameId`. A predictor that
   ignores this will over-estimate danger from homing bullets aimed at someone else.

### 2.6 The ordering fact that makes this whole design work

The client sends `GmHit` — its assertion to the server that a hit occurred — **after** it has
already computed the hit locally, and the client **never applies that damage to its own `Health`**.
The proof is in §3.

Therefore, at the instant `HitCharacter()`'s condition is true, the following is true *on the
server's clock*: the client has not yet told the server about the hit, so the server has not yet
resolved it, so **the damage is still in the future**. That is precisely the window the existing
reactive auto-nexus cannot see, and precisely the window this hook exists to occupy.

### 2.7 Lifetime and removal

`Remove()` is called when a bullet dies (`Projectile.cs:1157`, `:1242`, `:1248`). Three death
causes are visible:

- the trajectory returns false (lifetime expired) — `:1151` returning true;
- **terrain collision** — `World.ProjectileCollides((int)Position.X, (int)Position.Y, out hit3d)`
  (`:1227`), which also emits `GmAllyHit`;
- the character/enemy hit path (`:1246`).

This matters because it is the *live-pool reconciliation* the reference engine had to build
separately and that the existing packet-only `drelay` dodge assessment named as a fundamental
limitation (`hook/work/auto-dodge-feasibility.md` §6.1, "ghost bullets"). **Inside the process, ghost
bullets do not exist**: a bullet that hit a wall is `Remove()`d in the same frame, so any planner
that reads the live pool reads the truth.

### 2.8 Pooling — a real identity hazard

Projectiles are pooled, not allocated:

```
Objects/Projectile.cs:574   public static Projectile Rent(GameObjectData data)
Objects/Projectile.cs:596       obj = new Projectile();
Objects/Projectile.cs:676   public override void Reset()
DarzasDominion/Allocations.cs:104, :346   IPool<Projectile> factory
```

A `Projectile` object is returned to the pool, reset, re-rented and given a **new `GameId`**. Any
planner state keyed on the object's *address* will therefore silently inherit a dead bullet's
history. **Key all per-bullet state on `(GameId, _startTime)`, never on the pointer.** This is a
non-obvious failure mode that would present as "the dodge occasionally reacts to bullets that do
not exist", and it is worth a dedicated test (§9.4).

---

## 3. Who owns the player's health? (the load-bearing negative result)

**The client never decrements the local player's `Health` for damage.** Every assignment to a
`Health` member in the entire assembly, with the display-only ones called out:

| site | verdict |
|---|---|
| `Entity.cs:148`, `:168` | constructor initialisation (`Health = 25`) |
| `Entity.cs:431`, `:435` | `ProcessStat` — server stat packet |
| `Player.cs:4951` | `Health = stat.GetShort();` — `StatsType.Hp`, server stat packet |
| `Player.cs:4953-4961` | the same `ProcessStat` block sets `BaseMaxHealth`, `MaxHealth` |
| `Entity.cs:2362`, `:2543` | **local variable named `health`**, not the field (`int health = damage;` → `health = GetDamageTaken(...)`) |
| `Entity.cs:2285`, `:2348`, `:2416` | same — local variables used only to drive `World?.ShowDamageMessage(...)` and `CreateHitEffect(...)` |
| the `GmHealthUpdate` handler | `World.cs:16055-16100` — **the only path that moves the live HP bar** |

And `Entity.HitByProjectile` computes every mitigation step and then uses the result **only** for
display:

```csharp
// Entity.cs:2543
baseDamage = GetDamageTaken(baseDamage, num4, proj.CriticalHit, out var color);
// Entity.cs:2569
World?.ShowDamageMessage(this, baseDamage.ToString(), color, this is Player, …);
```

The damage model is implemented client-side, faithfully, purely to draw a number. It is inert.

**This is good news twice over.** It confirms the hook's entire thesis — the client's collision is
*ahead of* the server's damage, so acting at that moment acts before the damage lands — and it
means the client contains a **complete, exact damage calculator we can simply call** rather than
reimplement, which is what §6 of [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) exploits.

### 3.1 What the client *does* tell the server, precisely

An earlier reading of this project asserted a flat "the client sends no damage acknowledgement". **That
is half wrong and the correction matters**, so it is recorded here.

The client **does** declare projectile hits it detects, in `GmHit` (id 102, also named `ProjHit`),
whose field order was resolved by brute force over all 580 captured payloads as
`Time → ProjectileId → ObjectId → PlayerProj`. When `PlayerProj = 0` — **161 of those 580** — the
`ObjectId` is the local player, proved by matching that object's `StatsType.Hp` int16 against the
session's `GmHealthUpdate` readings (120/120 and 15/15).

What remains true, and is what the design depends on:

- **it is not a general mechanism.** 74 % of measured HP falls (571 of 775) have **no such claim**
  within 400 ms, and whole sessions have 100 % claim-free falls, so the server plainly resolves much
  damage on its own (`TIMING.md` §2.3, §7.2);
- **but where a claim *is* sent, it demonstrably drives the server's damage.** Of 161 analysable
  `GmHit{PlayerProj=false}` reports, **105 are followed by an HP fall**, with a median **56 ms**
  (p90 **99 ms**) reconciliation lag onto the ~50/100 ms health cadence (`CLIENT-MAP.md` §10.1, from
  the captured traffic in `hook/tools/_projhit_raw.txt`). So the true model is **both**: the server
  simulates independently **and** honours the client's claim. That reconciliation lag must enter every
  latency budget in [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md);
- **there is no equivalent for any other damage source** — no client→server packet carries an HP,
  shield or damage value anywhere in the assembly, and AoE, beam, contact and DoT hits have no claim
  packet at all;
- **the earliest client-side knowledge is therefore still the collision test** (`Projectile.cs:1573`),
  not the health packet and not the claim, because the claim is *emitted by* that same test.

The consequence for design is unchanged: anchor on the collision. But the corrected facts do rule out
one thing an earlier draft of [RISKS.md](RISKS.md) had left open — **suppressing the claim is not an
alternative architecture.** It would blind the server to a subset of hits it currently applies *because
of* that claim, while removing none of the 74 % it resolves on its own.

---

## 4. Player and world state

| what | member | where |
|---|---|---|
| the world root | `public static World Shared` | `Game/World.cs:4216` |
| the local player | `World.Player` (used as `World.Shared?.Player`, `DarzaWindow.cs:1742`) | on `World` |
| player position | `Entity.Position` (`Vector3`, inherited) | `Nodes/Node.cs:175` |
| player velocity/intent | `Player.GetMoveVector(...)`, `Player.UpdateMoveVector(...)` | `World.cs:12714`, `:12770` |
| HP / max / shield / barrier | `Player.Health`, `.MaxHealth`, `.Shield`, `.Barrier` | `Player.cs:1547`, `:1549`, `:1433`, `:1545` |
| HP fraction | `Entity.HpPercent` | `Entity.cs:96`, recomputed `Player.cs:5230-5238` |
| stats | `Player.GetStat(PlayerStat)` | used `World.cs:12697` |
| status effects | `Player.Effects` (`StatusEffects`), `HasEffect(StatusEffect[, out tier])` | `Player.cs`, used `World.cs:12695-12697` |
| own object id | `Player.GameId` | used `Projectile.cs:1527`, `:1573` |
| spirit / knocked-up | `Player.SpiritMode`, `Player.KnockedUp` | `Entity.cs:2208`, `:1521` |
| safe area | `World.hehe_..._EA4A` (from `GmSafeAreaState`) | `World.cs:13009` |

The character list accessors are `World.GetCharacters()` (`World.cs:15230`) and
`World.GetCharactersNoNpc()` (`World.cs:15245`); the projectile-collision predicate used by bullets
is `World.ProjectileCollides(int x, int y, out bool hit3d)` (`World.cs:13411`). The parallel-array
ingest of a server projectile batch is at `World.cs:10982-11032`.

**Units.** `Position` is in **tiles**, not pixels — the client indexes tilemaps with
`[(int)Position.X, (int)Position.Y]` directly, and the tile→pixel factor is the runtime camera zoom
`World.BaseNodeScale`, so there is **no fixed tile-size constant to find** (`CLIENT-MAP.md` §4.3; the
`1f/64f` in `Tilemap` is an atlas UV, not a world scale). That resolves the units question decisively
and in the convenient direction: everything in [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) is already in
the same units the game uses, and no conversion is needed anywhere.

What remains `UNKNOWN — needs live measurement` is only the **map dimensions in tiles**, which bound the
candidate bounds check.

---

## 5. Movement and input

### 2.9 The decrypted constant pools, and why they change the plan

`hook/work/_pooldecode.py` already decrypted the obfuscator's constant blob (`hook/work/_pool.bin`, from
the ConfuserEx-style manifest resource described in `proxy/work/report-nexus.md` §0) into four arrays:
**1035 ints, 16 longs, 787 floats, 229 doubles.** The decode is *verified*, not assumed: it
reproduces all eight independently-known packet ids
(`hehe(190)=70` HealthUpdate, `hehe(22)=5` MapInfo, `hehe(4)=4`, …).

Which pool a call site uses is decided by the **return type in the IL**, and the decompiled C#
cannot tell you that — every call renders identically as `hehe_F244_F670_F803_F5DD_EB9E(N)`.
[`hook/tools/poolresolve.py`](../../hook/tools/poolresolve.py) closes that gap: it reads the `ilspycmd -il` dump,
pairs each `ldc.i4 N` with the `call <ty> …(int32)` that consumes it, and prints the resolved
value. So the client's magic numbers are fully recovered and reproducible.

Two of them matter immediately.

**The speed law.** `PlayerFunctions.TilesPerSecond` (`PlayerFunctions.cs:459`), resolved:

```csharp
if (paralyzed || cutscene) return 0.0;                      // double pool[7]
if (fearOfTheBull && speed > 40.0) speed = 40.0;            // double pool[18]
if (grounded && !GroundedSafeTiles.Contains(tileType)) speed /= 2.0;   // pool[14]
speed += 10.0;                                              // pool[19]
double tps = 3.5 + 5.0 * (speed / 75.0);                    // pool[20], pool[21], pool[16]
tps *= StatusEffectValues.GetValue(Slowed,  slowTier);      // pool[42]
tps *= StatusEffectValues.GetValue(Speedy,  speedyTier);    // pool[75]
```

`3.5 + 5.0·(SPD+10)/75` — **not** the `4.0 + 5.6·(SPD/75)` the old packet-only feasibility note
guessed from the reference game (`hook/work/auto-dodge-feasibility.md` §3, quoting `auto-dodge.js:442`).
The endpoints are **3.5 tiles/s at SPD 0 and 8.5 at the SPD 75 cap**, so every escape budget derived
from the guess was ~13 % too generous. `FearOfTheBull` clamps SPD to 40. This is exactly the kind of
error the hook does not have to make, and it is the reason none of the dodge constants may be
inherited from the reference engine.

**The armour law and the damage formula.** `Entity.CalculateDamage` (`Entity.cs:726`), resolved:

```csharp
int val  = (int)(baseDamage * (0.7 - 0.007 * Math.Min(armor, 100.0)));
double n = 15.0 * baseDamage / (25.0 / Math.Max(baseDamage, 1.0) * armor + 15.0);
return Math.Max(val, (int)n);
```

so incoming damage against armour is
`max(baseDamage · (0.7 − 0.007·min(armour,100)), 15·dmg / (25·armour/max(dmg,1) + 15))`, floored
at 1 — **a 0.7 base multiplier and a 0.7 %-per-armour-point linear term, capped at 100 armour
points.** The two competing forms are the familiar "linear reduction vs. a hyperbolic curve, take
the better for the defender" shape from this genre, and both are now exact.

`Entity.GetDamageTaken` (`Entity.cs:568`) resolves to this order: no-op on `Npc`; negative damage
returns and colours green; **total immunity** if any of status effects `5`, `15`, `24`
(`hehe(22)`, `hehe(78)`, `hehe(115)`) is present; then `CalculateProtected`; then `CalculateBarrier`;
then armour reduction via `CalculateArmorBroken`; then `CalculateVulnerable`; then
`CalculateDamage`; then `CalculateArmored`; floored at 1.

And `Entity.HitByProjectile` (`Entity.cs:2469`) becomes:

```csharp
int baseDamage = proj.CriticalHit ? (int)(proj.Damage * proj.Desc.CriticalDamage) : proj.Damage;
double pierce  = proj.Desc.IgnoreArmorPercentage;
if (proj.LocalSeedDerived && World.Player != null)
    pierce = Math.Min(1.0, pierce + World.Player.ArmorPiercing);   // the 1.0 is pool[6]
baseDamage = GetDamageTaken(baseDamage, pierce, proj.CriticalHit, out color);
```

**The design conclusion this changes.** With the pools decrypted, the incoming damage *is*
computable offline — but the conclusion is still that the hook should **call
`Entity.GetDamageTaken` rather than reimplement it**. Not because the constants are unknown
(they are now known) but because the *order* of the eight mitigation steps, the
`SplitManaShieldForDisplay` interleaving, the barrier bookkeeping and the status-effect tier lookups
are all live runtime state that a reimplementation would have to mirror exactly, and any one of them
being subtly off produces a nexus that fires one hit too late. Calling the game's own function gives
the exact number for free, on the exact object, at the exact instant.

### 2.10 Trajectory units, resolved

`ProjDesc.GetPosition` (`ProjDesc.cs:1149`) resolves to `dt = time / 1000.0f`
(float pool[70] = 1000.0), so:

- **`time` is milliseconds** — this is `World.ClientTime − _startTime`;
- **`dt` is seconds**;
- **`Speed` is world units per second**, and the product `dt · Speed` is a distance in the same
  world units as `Position`.

That single resolved constant is what makes the whole prediction model dimensionally coherent, and
it is the answer to the "world units per tile" open question's sibling: units are internally
consistent everywhere, and the only remaining unknown is the units-per-tile ratio (§10).



`World.hehe_EB2C_E5A5_EE82_E9C5_EF68(float dt, float)` — `World.cs:12666` — is the player
movement tick. In order:

```csharp
// World.cs:12687-12697
else if (GameScene.GameInput.Moving || Player.Sliding() || Player.MovingTowards)
{
    bool moving   = GameScene.GameInput.Moving;
    float moveSpeed = GameScene.GameInput.MoveSpeed;
    Angle angle = moving ? GameScene.GameInput.MoveAngle.AddRadians(0f - WorldRotation + num)
                         : new Angle(new GamePoint(Player.SlideVector.X, Player.SlideVector.Y));
    ...
    float num3 = (float)PlayerFunctions.TilesPerSecond(
        (short)tileType, Player.GetStat((PlayerStat)0), tier, tier2,
        Player.HasEffect(Paralyzed), Player.HasEffect(FearOfTheBull),
        Player.HasEffect(Grounded), Player.HasEffect(Cutscene)) * num2;
    gamePoint = angle.Position * num3 * dt * moveSpeed;                  // :12710
    vector = Player.GetMoveVector(new Vector3(gamePoint.X, gamePoint.Y, 0f), moving, dt);
    ...
    hehe_..._EF69(new GamePoint(vector2.X, vector2.Y));                   // :12769
    Player.UpdateMoveVector(vector, dt);                                  // :12770
    Player.Position = vector2;                                            // :12780
    float value = XNAUtils.VectorLengthIgnoreZ(vector2 - position);
    Player.TPS.Add(value, 2, ClientTime);                                 // :12783
}
```

`PlayerFunctions.TilesPerSecond` (`DarzaGameNet/PlayerFunctions.cs:459`) is the speed law, and its
**structure** is readable even though its constants are opaque:

```csharp
if (paralyzed || cutscene) return 0.0;
if (fearOfTheBull && speed > 40.0) speed = 40.0;
if (grounded && !GroundedSafeTiles.Contains(tileType)) speed /= 2.0;
speed += 10.0;
double tps = 3.5 + 5.0 * (speed / 75.0);                 // the SPD curve
tps *= StatusEffectValues.GetValue(Slowed, slowTier);    // double pool[42]
tps *= StatusEffectValues.GetValue(Speedy, speedyTier);  // double pool[75]
```

Every one of those numbers is a **resolved** constant, not a guess — see §2.9, which also explains
why this is **not** the `4.0 + 5.6·(SPD/75)` the earlier packet-only note assumed.

This is exactly the law the existing `docs/hook/INJECTION.md` §5.2 describes from the proxy side, and it
confirms the note there: **`Slowed`, `Paralyzed`, `Cutscene`, `Grounded` and `FearOfTheBull` feed a
law the server re-simulates**, which is why the relay's status strip refuses to remove them. The
hook must respect the same boundary — see [RISKS.md](RISKS.md) §"server agreement".

### 5.2 What this means for actuation

Three options exist, and they are not equally good:

**(a) Call `World.RequestEscape()`.** `World.cs:12984`. This is the game's own nexus entry point:
it consults the safe-area flag, the world-entry grace window, `Player.Totems`, `PlayerDead` and the
cast state, and then sends `GmEscape` (`:13011`) or starts a cast (`:13002`, `:13019-13021`).
Calling it is **strictly better than injecting `GmEscape` from outside**, because it runs the
client's own gates: the result is identical to the player pressing the nexus key at that instant,
which is the one behaviour the server is known to accept.

**(b) Steer movement by setting the input angle.** `GameScene.GameInput.MoveAngle` / `.Moving` are
read at `World.cs:12690-12692` and used to compute the position delta a line later. Writing the
desired heading there means *the client itself* integrates the position, applies its own speed law,
its own collision, its own `TPS` accounting and its own timestamped move records — i.e. the
outbound stream stays **byte-legitimate by construction**, which is the property that protects
against server-side movement validation.

**(c) Write `Player.Position` directly.** Discouraged. `World.cs:12756-12783` shows the game
computing the position delta, then `Player.UpdateMoveVector(vector, dt)`, then the assignment, then
feeding the realised displacement into `Player.TPS` for the movement tracker. A raw write bypasses
`UpdateMoveVector` and corrupts the very history the outbound packets are built from. The reference
engine's own comment is the same warning from the other direction
(`hook/work/auto-dodge-feasibility.md` §3, quoting `DangerPlanner.h:29-35`: *"Raw writes … bypass ACTk's
rigidbody sync and cause server snap-backs"*).

There is also a **client-native "move to a point" facility** — `Player.MovingTowards`,
`MovingTowardsPosition`, `MovingTowardsStart`, `MovingTowardsTime`, `MovingTowardsSpeed`
(`World.cs:12733-12750`) — driven by the server's `GmMoveTowards`, and the client acknowledges it
with `GmMoveTowardFinished` (`:12787`). It is a real, server-sanctioned movement mode. Whether it is
usable for player-initiated movement without a `GmMoveTowards` is a **spike question**
([IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §Spike 4).

### 5.3 Escape

```
World.RequestEscape()                      World.cs:12984
  instant path → Client.SendPacket(new GmEscape())                    :13011
  channelled  → GmEscapeCastState{Casting=true}, cast end at +C21 s   :13002, :13019-13021
  cancel      → CancelEscapeCast() / GmEscapeCastState{Casting=false} :13016
```

The frame-loop check that turns the key press into a call is
`GameScene.GameInput.Escape` → `RequestEscape()` (`World.cs:12838-12848`), one-shot (cleared at
`:12842`). Both packets are already modelled and byte-verified on the Java side
(`docs/hook/INJECTION.md` §1.5), so the hook and the proxy can agree on the wire bytes.

**The cast branch is a challenge-run special case, not normal escape.** This corrects an earlier
reading of this document, which described the movement cancellation in `RequestEscape` as a general
interaction hazard. It is not. Operator-confirmed semantics:

- **Normal escape is instant and is never cancelled by moving.** The ordinary player path takes the
  `:13011` branch and sends the empty `GmEscape` immediately.
- **The channelled branch is reached only when the character has the `Impermanence` totem** — a
  challenge/run modifier. Only there does `World.cs:13043-13047` cancel the cast on movement.

So the two-controller hazard is **real but scoped to challenge runs**, and it is the one case where
a dodge that steers can cancel the escape the same frame it issues it. The design consequence is the
same precedence rule ([RISKS.md](RISKS.md) §3d), but it is now correctly sized: the hook can assume
the instant path is the normal one, and only needs the yield logic when the totem is present. The
client makes that detectable from the same `Player.Totems` check the game itself performs at
`World.cs:13009`.

**There is no melee or contact damage** (operator-confirmed, and consistent with every damage site
found in the decompile: the paths are `HitByProjectile`, `HitBy(AoeData)` and `HitBy(int, …)`, all of
which require a projectile or an AoE). That removes a whole row from the scenario table in
[TIMING.md](TIMING.md) §6 and it removes the "melee" entry from the non-goals in
[RISKS.md](RISKS.md) §5.

**Movement-stopping status effects, and the one that ends the dodge entirely.** Two effects change
the speed the dodge must budget for, and they are not the same case:

| effect | resolved effect on the speed law | what the dodge must do |
|---|---|---|
| `Paralyzed` | `TilesPerSecond` **returns 0 immediately** (`PlayerFunctions.cs:461-470`) | **do not attempt to dodge at all.** The player cannot move, so every candidate is a lie. The design's `movementLocked` gate covers this, and it must be driven by the *computed* speed rather than by a separate check, so that any future effect with the same consequence is covered for free |
| `Slowed` | multiplies by `StatusEffectValues.GetValue(Slowed, tier)` (`PlayerFunctions.cs:510`) | dodge, but with a **proportionally smaller move budget `b`** and a correspondingly shorter reachable radius |

Both fall out of the design correctly **only because the budget is derived from the game's own
`TilesPerSecond`** ([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §9) rather than from a nominal speed:
the hook computes the speed by calling the game's law with the live status tiers, so a slowed player
gets a smaller `b` automatically. **A hardcoded nominal speed would produce a dodge that spends
budget the player does not have**, which is both a wasted dodge and a movement the server can
reject. This is the concrete reason the speed model must be *called*, not assumed, and it is worth a
dedicated test: assert that with `Slowed` at each tier the emitted move magnitude scales as the
game's own speed does.



---

### 5.4 Terrain: the walkability grid exists and is readable

This closes what was listed as an open capability question (RISKS §3b). The client maintains a
**real per-tile collision grid**, and it is fed by a packet we can name.

### The packet

```
GmTiles                                    DarzaGameNet.Packets.Game.GameServer/GmTiles.cs
  Tiles = new MapElement[r.ReadVarint()]   // count, then per element:
      Type  = (short)r.ReadVarint()        // a tile-type id
      DeltaX += r.ReadVarint()             // run-length X delta
      DeltaY += r.ReadVarint()             // run-length Y delta
  ...then a separate run-length-encoded collision array:
      num4 = r.ReadVarint()                // number of RLE entries
      for j in 0..num4:
          num6 = r.ReadVarint()
          num5 += num6 >> 1                // delta-encoded index -- element (num5)
          Tiles[num5].Collision = ((num6 & 1) != 0) ? 5 : 1
```

and the element:

```
public struct MapElement                     DarzaGameNet.Structures/MapElement.cs
    public const byte CollisionNone   = 0;   // :5
    public const byte CollisionSolid  = 1;   // :7
    public const byte CollisionSolid3D= 2;   // :9
    public short X, Y, Type;
    public byte Width, Height, Collision;
```

So `Collision` is a small enum and the RLE encodes a **1-bit** value per index with the low bit
carrying it — which is why the writer emits `(delta << 1) | (collision == 5 ? 1 : 0)`
(`GmTiles.cs:94`) and the reader masks `num6 & 1` and takes `num6 >> 1` as the delta.

### The client-side grid the hook should read instead

The packet's delta encoding is not something a native hook wants to maintain. It does not have to:

```
private byte[,] World.<grid>               World.cs:4318     // a true 2-D byte array, [x, y]
World.cs:9677   array[result2.X, result2.Y] = result2.Collision;   // the ingest
World.cs:15887  private (byte Terrain, byte Entity) <query>()      // and a combined query
```

**This is the best terrain story available anywhere in the project:** a square `byte[,]` indexed
directly by tile coordinate, holding exactly the collision byte. A native reader needs only

- the field's offset on `World` (resolved at startup like every other offset), and
- the .NET array layout, which is fixed: element count at `+8`, first element at `+16`, and for a
  `byte[,]` the stride is 1 byte with the two lengths stored in the array's MethodTable-adjacent
  bounds.

`Tilemap.GetTile(x, y)?.Type` (`World.cs:9669`) gives the tile *type*, and `DataManager.Data.Tiles`
maps a type to `TileData`, which carries the `NoWalk` flag (`TileData.cs:52`) — so a hook that
prefers a type-based model has that route available too. But the collision grid is the direct one and
is what the game's own movement code consults.

**Consequence for [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §5.3:** the walkability tests (rules 2
and 3) are no longer provisional. They read this grid, and the "terrain awareness unavailable"
degradation is a fallback for a *failed offset resolution*, not for a missing capability.

---

## 6. Damage arithmetic, as the client computes it

Two entry points compute damage; both are callable and both take the data they need as arguments.

```csharp
Entity.HitByProjectile(Projectile proj)      Entity.cs:2459   // the player path
Entity.HitBy(int damage, float damageHealthPercent, int manaDamage,
             float damageManaPercent, float ignoreArmor, bool criticalHit,
             bool heals, bool playerOwned, GamePoint target, ProjDesc desc)   Entity.cs:2339
Entity.HitBy(AoeData aoe)                    Entity.cs:2203
Entity.GetDamageTaken(int baseDamage, double armorPierce, bool criticalHit, out Color color)  Entity.cs:568
```

`HitByProjectile` reduces to:

```csharp
// Entity.cs:2469
int baseDamage = proj.CriticalHit ? (int)(proj.Damage * proj.Desc.CriticalDamage) : proj.Damage;
// Entity.cs:2506, :2516
num2 = (int)((float)Health * proj.DamagePercent);      // fraction-of-current-HP component
num3 = (int)((float)player.Mana * proj.ManaDamagePercent);
// Entity.cs:2538-2542
double num4 = proj.Desc.IgnoreArmorPercentage;
if (proj.LocalSeedDerived && World.Player != null) num4 = Math.Min(1.0, num4 + World.Player.ArmorPiercing);
// Entity.cs:2543
baseDamage = GetDamageTaken(baseDamage, num4, proj.CriticalHit, out color);
// Entity.cs:2546, :2548, :2551   the same for the percent components
```

and `GetDamageTaken` (`Entity.cs:568-641`) applies, in named steps:

| step | method | line |
|---|---|---|
| invulnerable / Npc / negative-damage short-circuits | inline | `:575-587` |
| `CalculateProtected` (a `Protected` status multiplier) | `:643` | |
| `CalculateBarrier` (spends `Barrier` first) | `:661` | |
| critical-hit colour | inline | `:594-597` |
| `GetArmor()` | `:2334` | |
| `armorPierce` scaling | inline | `:618` |
| `CalculateArmorBroken` (`ArmorBroken` effect) | `:733` | |
| `CalculateVulnerable` | `:626` | |
| `CalculateDamage(baseDamage, armor)` | `:726` | |
| `CalculateArmored` (the `Armored` effect) | `:708` | |
| floor at 1 | inline | `:636` |

`CalculateDamage` (`Entity.cs:726-731`) is the armour law, and its **coefficients are all resolved**
(§2.9):

```csharp
int val  = (int)(baseDamage * (0.7 - 0.007 * Math.Min(armor, 100.0)));
double n = 15.0 * baseDamage / (25.0 / Math.Max(baseDamage, 1.0) * armor + 15.0);
return Math.Max(val, (int)n);
```

**Consequence:** evaluating incoming damage *offline* is now possible, since every constant is
known. The recommendation is nevertheless to **call `GetDamageTaken` in-process** rather than
reimplement it — not because the arithmetic is unknown but because eight mitigation steps, the
barrier bookkeeping and the status-effect tier lookups all read live runtime state, and a
reimplementation that is subtly wrong produces a nexus that fires one hit too late. See
[DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §6.

---

## 7. The escape path, and why it is now predictive

Putting §2.6 and §6 together:

```
frame N, Projectile.LateUpdate(bullet B)
   └── Hit() → HitCharacter()
        └── HitByProjectile(player)          computes the exact damage, displays it, changes no health
        └── Client.SendPacket(GmHit …)       tells the server "I hit the player"
                                          ▲
                                          └── THE SERVER APPLIES THE DAMAGE AFTER THIS POINT
```

Everything the reactive `drelay` auto-nexus cannot see — the identity of the incoming bullet, its
exact damage, the moment of the collision decision — is available at this instruction, and the
server's own damage is still downstream of it by one client→server trip. The end-to-end latency
budget that follows from this is measured in [TIMING.md](TIMING.md).

**One clarification, and the hook paid for it on 2026-10-12: `HitCharacter()` is a per-frame
predicate, not an event.** `LateUpdate` calls `Hit()` for every live projectile every frame, and
`HitCharacter()` answers `true` only on the frame the hit resolves — the `return false` at
`Projectile.cs:1604` is the ordinary answer, and `CanHitEntity` suppresses repeats for the same
bullet. So anything patched onto this method sees every *question*; **the collision decisions are
the `true` answers.** The first live session with the collision hook installed counted 418 328
entries and 48 resolved hits, and for a while every one of those entries was treated as a decision
— which inflates the false-negative rate by however many frames a bullet spends near the player.
`IMPLEMENTATION-PLAN.md` Deliverable 2.3, `VERIFICATION.md` §"the procedure" and
[DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §10 invariant 4 all now say the same thing: gate on the
return value. The accounting is [PHASE2-SCOPE.md](PHASE2-SCOPE.md) §4.14.

The hook therefore does not need to *predict the future from outside the process*. It needs to
**arrive at the collision decision earlier than the server does**, which is automatic once it is
running inside the process, and then to spend the remaining window doing something better than
nexusing — namely, not being there.

---

## 8. Anti-cheat and integrity surfaces (the negative results)

Searched and **not found**: any kernel driver, any EasyAntiCheat/BattlEye/Denuvo DLL, any
integrity self-check over `DarzasDominion.dll`, any anti-debug flag in the PE
(`characteristics = 0x0022` — DLL + EXECUTABLE only), any `ReadyToRun` or native obfuscator
section. The install is a plain self-contained .NET 9 apphost.

What *is* present and worth naming:

- **Steam** (`steam_api64.dll`, `Steamworks.NET.dll`, `steam_appid.txt` = 1895130). Achievements
  and stats are Steam-side; a hook changes nothing about them.
- **NLog**, `crashes/`, `logs/darza.NN.log`, and the game's own `StallProbe`/`UiThreadWatchdog`
  frame watchdog. The watchdog is the one to respect: a hook that blocks the main thread will be
  *reported by the game itself*, which is both a crash risk and a gift — the game's own log becomes
  a free profiler for the hook's frame cost.
- **A watchdog heartbeat exists** (`UiThreadWatchdog.Heartbeat()`, `DarzaWindow.cs:1733`; phase
  transitions at `:1734`, `:1838-:1844`). `UNKNOWN — needs measurement`: what interval it tolerates
  and what it does on breach. The hook must be designed to a *strict* budget regardless; see
  [RISKS.md](RISKS.md).

This is a single-player automation tool on the researcher's own machine. The honest framing is that
the technical risk is *destabilising the game for the user*, not evasion.

---

## 9. The candidate hook list, ranked

Signatures are given as declared. The shape of a native detour depends on `static` vs instance
(the instance case prepends a `this` argument, `rcx`) and on the calling convention (x64 fastcall:
`rcx, rdx, r8, r9`, then stack).

| # | target | signature | line | why | given to us |
|---|---|---|---|---|---|
| **1** | `DarzaWindow.Update` | `public void Update(float total)` | `DarzaWindow.cs:1727` | once per frame, after packets, before the world tick; single float arg; far too big to inline | the frame tick and all the budget |
| **2** | `Projectile.ProcessProjectileData` | 16-arg `public void (...)` | `Projectile.cs:810` | fires once per spawned bullet with angle, position, damage, `desc`, `hitsPlayer`, `playerOwned` **as arguments** | the complete threat event with **zero field offsets** |
| **3** | `Projectile.HitCharacter` | `private bool HitCharacter()` | `Projectile.cs:1514` | **the collision decision for the local player**; returns true iff the bullet is being consumed by the hit | the exact moment the server is about to be told |
| 4 | `Entity.HitByProjectile` | `public void HitByProjectile(Projectile proj)` | `Entity.cs:2459` | the damage computation, with `proj` as an argument; the place to read the exact number | precise incoming damage for the "will this kill me" test |
| 5 | `World.RequestEscape` | `public void RequestEscape()` | `World.cs:12984` | the actuator; calling it is the legitimate nexus | escape |
| 6 | `Entity.GetDamageTaken` | `public int GetDamageTaken(int, double, bool, out Color)` | `Entity.cs:568` | optional: call it rather than hook it, once we can invoke managed code | exact mitigation |
| 7 | `World.<player movement tick>` | `private void hehe_EB2C_…_EF68(float, float)` | `World.cs:12666` | per-frame movement application; where steering would be injected | read intent, write heading |
| 8 | `Projectile.LateUpdate` | `public override void LateUpdate(ClientTime)` | `Projectile.cs:1091` | **per-bullet per-frame**; the exhaustive alternative to #2 if a spawn hook proves unreliable (pooling, re-emission, homing re-target) | every bullet, every frame — at a cost of one detour call per bullet per frame |
| 9 | `ProjDesc.GetPosition` | `public virtual bool GetPosition(int, float, float, out float, out float, int)` | `ProjDesc.cs:1149` | for **obtaining a callable pointer** to the game's own trajectory function; a hot target, so call it, do not hook it | exact future positions |
| 10 | `Client.DrainColdQueue` | `public static void DrainColdQueue()` | `Client.cs:502` | the network-apply boundary; a cheaper, earlier frame marker than #1 | a pre-apply vantage |
| 11 | `ProjDesc.GetVelocityAt` | `public float GetVelocityAt(float dt)` | `ProjDesc.cs:956` | speed for a time-to-impact estimate | closure speed |
| 12 | `PlayerFunctions.TilesPerSecond` | `public static double TilesPerSecond(short, double, byte, byte, bool, bool, bool, bool)` | `PlayerFunctions.cs:459` | the speed law as a callable; also the one law the server re-simulates | exact escape budget |

### 9.1 The minimum viable set

**#1 (frame) + #2 (spawn) + #3 (collision) + #5 (escape)** is enough to build a *predictive* nexus
that beats the current one by the whole of the client→server trip. **#9** turns it into a dodge.
Nothing else is required for a first working version, and that is deliberately a **four-detour**
project, not a framework.

### 9.2 Why #2 is the surprise win

The instinct is to hook the per-bullet update (#8) because that is where the data is. The better
target is the **spawn**, because `ProcessProjectileData` (`Projectile.cs:810`) takes the entire
threat as arguments, so the hook reads it **without a single field offset** — the most fragile part
of any managed-memory hook is the part this target eliminates. The trajectory is then reproduced by
*calling* `GetPosition` (#9), not by reading fields.

### 9.3 Inlining risk, per target

| target | risk | mitigation |
|---|---|---|
| `DarzaWindow.Update` | none — huge method, called from one site | — |
| `ProcessProjectileData` | none — 16 parameters, large body | — |
| `HitCharacter` | **low but real** — it is `private` and called from one place (`Hit()`, `:1509`) | if inlining is observed, fall back to hooking `Hit()` (`:1505`) or `LateUpdate` (#8) |
| `Entity.HitByProjectile` | low | same fallback |
| `GetPosition` | **do not hook** — it is `virtual` and hot; **call** it | obtain a pointer; invoke |

Verified as a mitigation: `System.Runtime.TieredCompilation = false` removes the recompile hazard
entirely (§0.1), so the only inlining question is the initial compile.

### 9.4 Field-offset resolution vs argument-passing

The full list of offsets a naive design would need — `World.Shared.Player`, `Player.Position.X/Y`,
`Player.Radius`, `Player.Health/MaxHealth/Shield/Barrier/ArmorPiercing/GameId`, `Projectile.Angle`,
`Projectile.Desc`, `Projectile.Damage`, `Projectile.Radius`, `GameInput.MoveAngle`, and ~40
`ProjDesc` fields — is **not needed**, because:

- the frame tick (#1) is the only place we need `World.Shared`, and it is resolved **once**;
- the threat arrives **as arguments** at #2;
- the collision state arrives **as `this` + arguments** at #3 and #4;
- the trajectory is **called**, not read (#9).

The offsets that remain are a handful of `Player` scalars (position, HP family, armour) read for the
decision. They are resolved once at startup by a resolution pass, and they are the reason
`HOOK-ARCHITECTURE.md` §"resolution" exists as its own phase.

---

## 10. What is still unknown, and how to find it

Each of these is a *measurement*, not a research question, and each has a designated spike in
[IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md).

| # | unknown | why it matters | how to resolve |
|---|---|---|---|
| 1 | ~~value of `Player.Radius`, `GameObject.Radius` default~~ | defines `r_eff` | **RESOLVED by pool decode** — `GameObject.Radius = 0.25` (float pool[16]) unless the spawn path overrides it; confirm live at spike 3 |
| 2 | ~~`Desc.Size` default and the `Size`→`Radius` relation~~ | bullet hit radius | **RESOLVED** — `ProjDesc.Size = 3.0` (float pool[6]); `Projectile.cs:1864` sets `Radius = (float)Desc.Size · C`, so read `Projectile.Radius` live rather than recomputing |
| 3 | ~~world-units-per-tile~~ | — | **RESOLVED** — `Position` is already in **tiles**, and the tile→pixel factor is the runtime camera zoom `World.BaseNodeScale`. No conversion needed; no fixed tile-size constant exists. Only the map dimensions in tiles remain unknown, and they bound the bounds check |
| 4 | `DarzaWindow.Update` frame period and jitter | the hook's budget | `StallProbe` in the game's own log, or hook #1 and timestamp |
| 5 | ~~`World.ClientTime` unit and its relation to the trajectory clock~~ | predicting with the same clock the game uses | **RESOLVED** — `ProjDesc.GetPosition` divides `time` by 1000 (float pool[70]), so `time` is **milliseconds** and `dt` is seconds; `Speed` is world-units-per-second |
| 6 | server tick rate | the horizon constant | measure `GmUpdate` cadence — [TIMING.md](TIMING.md) |
| 7 | whether the server validates movement strongly enough to make steering (§5.2b) safe | decides whether the dodge can act at all | the existing `drelay` proxy can answer this from outside, with no hook: steer and watch for kicks (`docs/hook/INJECTION.md` §5.5) |
| 8 | `UiThreadWatchdog` tolerance | how much frame time the hook may spend | measure, then set the budget at 10 % of it |
| 9 | whether `HitCharacter` is inlined | whether target #3 works | install the detour and count invocations |
| 10 | whether `GetPosition` can be called safely from the main thread with arbitrary `time` | the entire prediction model | call it with the current time and compare against `Position` |
| 11 | **which of the two shipped apphosts is running** | they carry **different** `DarzasDominion.dll` builds (50,216,960 vs 50,831,360 bytes) | fingerprint the loaded assembly (size + MVID + IL hashes) at attach; see [HOOK-ARCHITECTURE.md](HOOK-ARCHITECTURE.md) §"version tagging" |

---

## 11. One-paragraph summary

The client is a self-contained .NET 9 CoreCLR application whose 50 MB game assembly is **pure IL
with tiered compilation disabled**, so every method is JIT-compiled exactly once and its native code
is stable for the life of the process — which makes runtime hooking a solved-shaped problem rather
than an adversarial one. The interesting control flow is completely readable: `DarzaWindow.Update`
is the frame tick, `Projectile.ProcessProjectileData` receives each new bullet's angle, position,
damage, descriptor and `hitsPlayer` flag **as arguments**, `Projectile.HitCharacter` is the exact
instant the client decides a bullet is hitting the local player, and that decision happens **before**
the client tells the server about it with `GmHit`. The client computes damage precisely and then
throws the number away for display — it never writes `Health`, which only `GmHealthUpdate` touches —
so at the moment of the collision decision the real damage is still in the server's future, and the
hook has the whole client→server trip to do something about it. It does not need to reimplement the
game's trajectories or its armour formula, because it can *call* `ProjDesc.GetPosition` and
`Entity.GetDamageTaken`; and it does not need to write movement, because `World.RequestEscape()` is
the client's own nexus entry point and `GameScene.GameInput.MoveAngle` is the client's own steering
input, both of which keep the outbound stream legitimate.
