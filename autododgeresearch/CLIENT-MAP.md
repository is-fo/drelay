# CLIENT-MAP.md - reverse-engineering map of the Darza's Dominion client

**Purpose.** This is the evidence base for an in-process C++ hook (autododge + predictive auto-nexus)
against `DarzasDominion.exe`. It answers, with `file:line` citations, *where* the client's state lives,
*when* it changes per frame, and *which methods* a native detour must intercept.

**Source root.** `G:\coding\javamapp\drelay\work\decomp\` - ILSpy-style decompilation of
`DarzasDominion.dll` (pure IL, .NET 9 self-contained, `TieredCompilation: false`, no ReadyToRun,
23 743 methods, no observed anti-cheat). Quoted line numbers are into the decompiled `.cs` files.

> **STATUS BANNER (kept current).** Three findings that were open while this document was first
> drafted are now settled, and this document has been updated in place rather than left stale:
>
> | gap | status | where |
> |---|---|---|
> | §10.2 the five armour coefficients | **CLOSED** — `0.7 / 0.007 / 100.0 / 15.0 / 25.0` | §3.7, §9.4, and [`CLIENT-FINDINGS.md`](CLIENT-FINDINGS.md) §2.9 |
> | §10.3 the movement constants | **CLOSED** — `tilesPerSecond = (3.5 + 5.0·(SPD+10)/75) · slow · speedy`, i.e. **3.5 / 8.5 tiles/s at SPD 0 / 75** | §6.2, §9.4 |
> | §10.1 whether the server trusts the client's hit report | **ANSWERED [live]** — yes, with a **median 56 ms / p90 99 ms** reconciliation lag onto a **~50/100 ms** health cadence | §3.5, §10.1 |
>
> The mechanism for the first two is decoding the obfuscator's **four** constant pools and resolving
> each IL call site's return type; see [`tools/poolresolve.py`](tools/poolresolve.py) and
> §9.4. **`hehe(N)` is pool-scoped, not global** — that correction is important and is explained in
> §9.4.
>
> **What is still genuinely open** (and is the short list worth acting on): the *inverse* half of
> §10.1 (does the server also simulate?); §10.6 (which layer live projectiles occupy — decides scan
> cost); §10.4 / §10.5 (boomerang half-range and the LCG, both prediction-accuracy); §10.11
> (`WallBouncePath` — bouncing projectiles are not yet predictable); and the AoE/bomb damage channel,
> which this document explicitly did **not** cover (§10.16). Appendix B ranks them.

**How to read the quotes.** The decompiler flattened control flow into state machines
(`switch (num ^ literal) { case ... }`) with `goto` labels, and folded every literal into a constant-pool
lookup `_E311_EE2E_F8F8_E14F_E1D6.hehe_F244_F670_F803_F5DD_EB9E(N)`. Two consequences:
1. **Statement order inside a flattened method is not reliable**; the *set* of operations is. I report
   an order only when assignments/data dependencies pin it down, and I flag the one place where I could
   not (`Sec 1.4`).
2. **Every literal is opaque.** `hehe(N)` values are read from an encrypted manifest resource at type
   initialisation and are not present in the source (see `Sec 9.4`). So a line like
   `SpeedWaveFreq != hehe(7)` means "!= *the zero constant*", which is inferable from usage but not
   readable. Where a literal's *value* matters I say so explicitly and mark it. (This paragraph
   describes the *first* draft of this document; several of those values have since been read out of
   the decrypted pools and the IL dump - see the status banner above and Sec 9.4. The paragraphs that
   still say "UNKNOWN" are the ones that are still genuinely unknown, and Appendix B ranks them.)

Anything the decompile does not settle is written `UNKNOWN - needs live measurement`, never guessed.

**Table of contents**

| Sec | Topic |
|---|---|
| 1 | The frame loop |
| 2 | The network tick |
| 3 | The projectile model (motion, collision, damage, `ProjDesc`) |
| 4 | Player and world state |
| 5 | Projectile / entity registry, pooling |
| 6 | Input and movement |
| 7 | The escape path |
| 8 | Candidate hook list (ranked) |
| 9 | What is obfuscated and what is not |
| 10 | Gaps: what a hook still needs measured (Appendix B ranks them) |
| A/B/C | Recovered `LateUpdate` control flow; open-question shortlist; file inventory |

**Tooling.** Two helper scripts in `autododgeresearch/tools/` were written for this document:

* `g.py` - bounded grep-with-context: `python g.py <file> <regex> [--ctx N] [--max M] [--range A B] [--sig]`.
  It exists because `World.cs` is 24 381 lines / 827 KB and cannot be read whole; every `World.cs`
  citation below is a targeted region located with it.
* `il_constants.py` - resolves obfuscated literal *values* from the real IL dump at
  `autododgeresearch/il/client-5.8.2.il` (89 MB, produced by another agent in this workspace).
  The decompiled C# folds every literal into `hehe(int32 index)` and shows only the index; the IL shows
  the instruction sequence around the call, which is where the value is usable. This is the single most
  effective way to *read* a constant, whether or not the pool has been decrypted (see Sec 9.4).

**Cross-source note.** This workspace also contains live-measurement artifacts produced by sibling
agents in `autododgeresearch/tools/` (`_numbers.txt`, `_projhit_raw.txt`, `_rtt_raw.txt`,
`_cadence_raw.txt`, `_deaths_raw.txt`, and the scripts that generate them). Where those answer an open
question from Sec 10 they are cited as **[live]** and the corresponding gap is closed, not merely
listed.

---

## 1. THE FRAME LOOP

### 1.1 The real entry point is not MonoGame's `Game.Update`

`Windows.Program.Main` constructs `WindowsGame` and runs it:

```csharp
// work/decomp/Windows/Program.cs:135
hehe_E046_EC3C_EA3A_E199_E742 = new WindowsGame();
// work/decomp/Windows/Program.cs:136
hehe_E046_EC3C_EA3A_E199_E742.Run();
```

`WindowsGame : Game` (`work/decomp/Windows/WindowsGame.cs:18`) is the MonoGame host. Its `Update`
override does a spin-wait frame limiter and then hands the whole frame to the game's own window class:

```csharp
// work/decomp/Windows/WindowsGame.cs:206
protected override void Update(GameTime gameTime)
// work/decomp/Windows/WindowsGame.cs:260
DarzaWindow?.Update((float)gameTime.TotalGameTime.TotalMilliseconds);
```

```csharp
// work/decomp/Windows/WindowsGame.cs:263
protected override void Draw(GameTime gameTime)
// work/decomp/Windows/WindowsGame.cs:265
hehe_F417_EBB7_E2EE_EF98_F8D8.GraphicsDevice.Clear(Microsoft.Xna.Framework.Color.Black);
// work/decomp/Windows/WindowsGame.cs:266
DarzaWindow?.Draw(gameTime);
```

Note the argument: **the game's clock is `GameTime.TotalGameTime.TotalMilliseconds`, a monotonic
millisecond counter - not `ElapsedGameTime`.** `DarzaWindow.Update` receives absolute milliseconds
since game start and differences it itself.

`WindowsGame.Draw` is overridden (`:263`), `WindowsGame.Update` is overridden (`:206`), and
`WindowsGame.Initialize`/`LoadContent` are overridden (`:130`, `:200`). All are `protected override`.
`WindowsGame` itself is `public class WindowsGame : Game` and is not sealed.

### 1.2 `DarzaWindow.Update(float total)` - the one true frame function

```csharp
// work/decomp/DarzasDominion.Graphics/DarzaWindow.cs:1727
public void Update(float total)
```

`DarzaWindow` is `public class DarzaWindow` (`work/decomp/DarzasDominion.Graphics/DarzaWindow.cs:34`),
2720 lines, **not** a MonoGame `Game` and **not** virtual, **not** sealed. `Update` is a plain
`public void` instance method - which makes it an easy detour target but also means there is no
vtable slot to swap; it needs an IL-body patch or a JIT-compiled-code patch.

The body's order is partly encoded in a flattened `switch`, but five assignments pin the prologue
unambiguously because each consumes the previous one's value. In execution order:

| # | what | line |
|---|---|---|
| 1 | `DynamicFont.WarmAtlasStep();` | `:1729` |
| 2 | `hehe_F7CF_ED33_F133_E5EB_E683(total);` - **returns immediately unless `IsMobile`** | `:1730` |
| 3 | `UiThreadWatchdog.Heartbeat()` / `SetPhase` | `:1733-1734` |
| 4 | `World shared = World.Shared;` | `:1735` |
| 5 | `StallProbe.SetFrameContext(... shared?.Characters.Count ...)` | `:1742` |
| 6 | `Time.LogicTotal = LogicStopwatch.ElapsedMilliseconds;` | `:1783` |
| 7 | `Time.Total = total;` | `:1767` |
| 8 | `Time.Elapsed = Math.Min(total - Time.Total, hehe(119));` (clamped dt) | `:1775` |
| 9 | `Time.ElapsedSec = Time.Elapsed / hehe(70);` (`/1000`) | `:1755` |
| 10 | `Time.LogicElapsedSec = ((float)LogicStopwatch.ElapsedMilliseconds - Time.LogicTotal) / hehe(70);` | `:1787` |
| 11 | `MainThreadId = Environment.CurrentManagedThreadId;` | `:1759` |
| 12 | `Client.DrainColdQueue();` | `:1763` |
| 13 | `Broker.Update(Time);` | `:1771` |
| 14 | `StallProbe.FrameBoundary();` | `:1779` |
| 15 | day/night block (only when `CurrentScene is GameScene`) | `:1801-1807` |
| 16 | scene-swap block (`hehe_F67F_E140_E557_EFC9_E5CA`) | `:1820-1835` |
| 17 | `CurrentScene?.PreUpdate();` | `:1839` |
| 18 | `CurrentScene?.UpdateScene(Time);` | `:1841` |
| 19 | `CurrentScene?.LateUpdate();` | `:1843` |
| 20 | run queued `Action`s (`hehe_F67F_E140_E557_EFC9_E5C8.TakeAll()`) | `:1845-1849` |
| 21 | run queued `(Action, TaskCompletionSource)` pairs | `:1850-1863` |
| 22 | `RemoteImageCache.PumpUploads(hehe(5));` | `:1865` |
| 23 | `SoundManager.Update(Time.ElapsedSec);` | `:1886` |

The key lines, verbatim:

```csharp
// DarzaWindow.cs:1783
Time.LogicTotal = LogicStopwatch.ElapsedMilliseconds;
// DarzaWindow.cs:1755
Time.ElapsedSec = Time.Elapsed / _E311_EE2E_F8F8_E14F_E1D6.hehe_F244_F670_F803_F5DD_EB9E(70);
// DarzaWindow.cs:1775
Time.Elapsed = Math.Min(total - Time.Total, _E311_EE2E_F8F8_E14F_E1D6.hehe_F244_F670_F803_F5DD_EB9E(119));
```

```csharp
// DarzaWindow.cs:1763
Client.DrainColdQueue();
```

```csharp
// DarzaWindow.cs:1839-1843
CurrentScene?.PreUpdate();
CurrentScene?.UpdateScene(Time);
CurrentScene?.LateUpdate();
```

`hehe(70)` is 1000 (it is the divisor applied to milliseconds everywhere: `_startTime = (int)
DarzaWindow.Time.LogicTotal;` in `Projectile.cs:670` and `time.LogicTotal` is milliseconds).
`hehe(119)` is the dt clamp (a maximum frame delta in ms; value unresolved).

### 1.3 `Scene.UpdateScene` - the per-scene backbone, and where the world ticks

```csharp
// work/decomp/DarzasDominion.Nodes/Scene.cs:830
public virtual void UpdateScene(ClientTime time)
```

`Scene` is `public class Scene : Node` (`work/decomp/DarzasDominion.Nodes/Scene.cs:30`).
`UpdateScene` is **`public virtual`** - overridable, and `GameScene` does override it:

```csharp
// work/decomp/DarzasDominion.Darza.Scenes/GameScene.cs:1336
public override void UpdateScene(ClientTime time)
// work/decomp/DarzasDominion.Darza.Scenes/GameScene.cs:1338
base.UpdateScene(time);
```

Inside `Scene.UpdateScene`, in order:

| # | operation | line |
|---|---|---|
| 1 | hardware fullscreen toggle handling | `:856-877` |
| 2 | `ControllerInputState.Tick(time.Total);` | `:878` |
| 3 | `NodeAction` list update with `time.ElapsedSec` | `:880-888` |
| 4 | touch-cursor synthesis from mouse | `:911-945` |
| 5 | hit-test `Alerts` -> `Screens` -> `TooltipLayer` -> `UI` -> scene, cursor swap | `:946-1003` |
| 6 | **`Update(time);`** - `Node.Update`, walks the tree | `:1005` |
| 7 | `Alerts.Update(time);` `Screens.Update(time);` `TooltipLayer.Update(time);` `UI.Update(time);` | `:1015-1031` |
| 8 | **`LateUpdate(time);`** `UI.LateUpdate(time);` `TooltipLayer.LateUpdate(time);` `Screens.LateUpdate(time);` `Alerts.LateUpdate(time);` | `:1043-1048` |
| 9 | gamepad text input, key repeat, etc. | `:1053-1060+` |

So the tree order is: **`PreUpdate` (input poll) -> `Update` (logic) -> `LateUpdate` (post-logic)**, and
`World` is a `Node` in that tree (`public class World : Node`, `work/decomp/DarzasDominion.Darza.Game/World.cs:76`).

`Node.Update` and `Node.LateUpdate` are **`public virtual`**:

```csharp
// work/decomp/DarzasDominion.Nodes/Node.cs:2290
public virtual void Update(ClientTime time)
// work/decomp/DarzasDominion.Nodes/Node.cs:2389
public virtual void LateUpdate(ClientTime time)
```

`Node.Update` recurses into children; `World.Update` and `World.LateUpdate` are overrides:

```csharp
// work/decomp/DarzasDominion.Darza.Game/World.cs:9473
public override void Update(ClientTime time)
// work/decomp/DarzasDominion.Darza.Game/World.cs:6356
public override void LateUpdate(ClientTime time)
```

### 1.4 Inside `World.Update` - the actual gameplay tick order

`World.Update` (`World.cs:9473`) is far more legible than most, because its operations are one linear
body. The ones that matter, in source order:

```csharp
// World.cs:9505
ClientTime = (int)time.LogicTotal;
// World.cs:9519
FrameClientTime = (int)time.LogicTotal;
```
```csharp
// World.cs:9515
MusicEmitters.Update(this, time.ElapsedSec);
// World.cs:9525
MusicCombat.Update(this, time.ElapsedSec);
```
```csharp
// World.cs:9543
hehe_EB2C_E5A5_EE82_E9C5_EF5E();
// World.cs:9544
Slot.UpdateSwaps();
// World.cs:9547-9548
hehe_EB2C_E5A5_EE82_E9C5_EF52(time);
hehe_EB2C_E5A5_EE82_E9C5_EF7D();
// World.cs:9550
hehe_EB2C_E5A5_EE82_E9C5_EF7B(time);
```

Then the two hinges:

```csharp
// World.cs:9552
base.Update(time);
```
```csharp
// World.cs:9561-9568
if (Player != null && 0 == 0)
{
    hehe_EB2C_E5A5_EE82_E9C5_EFCC();
    AimbotSystem.UpdateTarget(Player, this);
    if (time.LogicElapsedSec > _E311_EE2E_F8F8_E14F_E1D6.hehe_F244_F670_F803_F5DD_EB9E(5))
    {
        hehe_EB2C_E5A5_EE82_E9C5_EF68(time.LogicElapsedSec, time.ElapsedSec);
    }
```

* **`base.Update(time)` (`:9552`) is `Node.Update`** - this is where every child node (including the
  layers holding characters and projectiles) runs its own `Update`.
* **`hehe_EB2C_E5A5_EE82_E9C5_EF68(...)` is the local-player movement + attack + escape function**
  (Sec 6 and Sec 7). It is the only caller of `Player.Shoot(...)` in the movement path.
* There is also a built-in **`AimbotSystem`** (`World.cs:9564`). That is game-shipped code, not a
  third-party add-on; worth reading before writing a targeting layer of our own.

Continue: camera target selection (`:9569-9631`), a packet drain block (`:9650-9777`, Sec 2.3), then
the rest of the frame (health-bar/UI/fog work). `World.LateUpdate` (`:6356`) is **not** entity update
- it is camera pan, scale update and layer view-bounds/culling (`:6363-6472`).

**Honest ordering caveat.** The exact interleaving of (a) `base.Update(time)` at `:9552`, (b) the
packet drain block at `:9650+`, and (c) character/projectile motion inside `base.Update` cannot be
read off with certainty, because the decompiler hoisted the phase dispatch into a `switch` whose
`goto` targets sit inside the same body (`:9507-9650`). Two statements *are* certain from their data
dependencies: `ClientTime = (int)time.LogicTotal` happens at the top, and the drain block uses
`hehe_E331_E9CE_F5E3_EBF5_EA31`, which is populated by the network thread. Whether a projectile
spawned by a `GmUpdate` received in *this* call is stepped in *this* call or the next is therefore
`UNKNOWN - needs live measurement` (a one-frame question; the experiment is in Sec 10).

**`LogicElapsedSec` anomaly (flagged, not guessed).** The decompiled `DarzaWindow.Update` computes:

```csharp
// DarzaWindow.cs:1783
Time.LogicTotal = LogicStopwatch.ElapsedMilliseconds;
// DarzaWindow.cs:1787
Time.LogicElapsedSec = ((float)LogicStopwatch.ElapsedMilliseconds - Time.LogicTotal) / hehe(70);
```

As literally written the subtraction is `x - x`, which would make `LogicElapsedSec` zero or negative.
I resolved this against the real IL rather than guessing, and **the decompiler is faithful** - the
sequence really is a subtract of the just-stored `LogicTotal`:

```
// autododgeresearch/il/client-5.8.2.il:501140-501146   (DarzaWindow.Update)
IL_01d0: ldsfld class DarzasDominion.Graphics.ClientTime DarzasDominion.Graphics.DarzaWindow::Time
IL_01d5: ldsfld class [System.Runtime]System.Diagnostics.Stopwatch DarzasDominion.Graphics.DarzaWindow::LogicStopwatch
IL_01da: callvirt instance int64 [System.Runtime]System.Diagnostics.Stopwatch::get_ElapsedMilliseconds()
IL_01df: conv.r4
IL_01e0: stfld float32 DarzasDominion.Graphics.ClientTime::LogicTotal

// autododgeresearch/il/client-5.8.2.il:501151-501158
IL_01ed: ldsfld class DarzasDominion.Graphics.ClientTime DarzasDominion.Graphics.DarzaWindow::Time
IL_01f2: ldsfld class [System.Runtime]System.Diagnostics.Stopwatch DarzasDominion.Graphics.DarzaWindow::LogicStopwatch
IL_01f7: callvirt instance int64 [System.Runtime]System.Diagnostics.Stopwatch::get_ElapsedMilliseconds()
IL_01fc: conv.r4
IL_01fd: ldsfld class DarzasDominion.Graphics.ClientTime DarzasDominion.Graphics.DarzaWindow::Time
IL_0202: ldfld float32 DarzasDominion.Graphics.ClientTime::LogicTotal
IL_0207: sub
IL_0208: ldc.i4.s 70
IL_020a: call float32 '...'::'hehe...'(int32)
IL_020f: div
IL_0210: stfld float32 DarzasDominion.Graphics.ClientTime::LogicElapsedSec
```

So `LogicElapsedSec = (Stopwatch.ElapsedMilliseconds_now - Time.LogicTotal) / hehe(70)` where
`Time.LogicTotal` was written microseconds earlier in the same frame. **The value is therefore a tiny
sub-millisecond residual, effectively `0.000x`**, and the movement gate is satisfied only because
`hehe(5)` (the thing it is compared against at `World.cs:9565`) is **not** 0 - it must be a negative
sentinel. That is consistent with this obfuscator's habit of using `-1`-style sentinels for
"unset/always-true" comparisons.

Two consequences a hook must absorb:

* **Do not use `LogicElapsedSec` as a frame dt.** It is a residual, not a delta. The usable per-frame
  dt is `time.ElapsedSec` (`DarzaWindow.cs:1755`, `Elapsed / 1000`); the usable monotonic clock is
  `time.LogicTotal` (`(int)time.LogicTotal` is the world's `ClientTime`, `World.cs:9505`, used as the
  ms age clock for every projectile, `Projectile.cs:670`, `:1109`).
* **Attack/movement timers that decrement by `LogicElapsedSec`** (`Entity.cs:824,845`,
  `Player.cs:4835,4841,4845`) therefore decrement by ~0 - i.e. those specific timers are effectively
  driven by something else, or are dead. `Entity._attackDelay` decrementing by a residual would never
  expire, yet attacks do respect cooldowns, so the real gate is `RateOfFire` / the server. Flagged in
  Sec 10.8 with a measurement that settles it. **A hook should never time anything off
  `LogicElapsedSec`.**

### 1.5 Per frame vs per network tick

* **Per frame (variable dt).** Everything above. `time.Elapsed` is `min(TotalGameTime delta, hehe(119))`
  (`DarzaWindow.cs:1775`) and `time.ElapsedSec = Elapsed/1000` (`:1755`). Projectile motion, character
  motion, collision and damage all advance by `time.ElapsedSec` (or by the ms clock) **inside the
  frame function**. There is **no fixed-timestep accumulator and no logic decoupling**: this is a
  directly-dt'd client.
* **Per network tick.** Only the *application of server packets*, and only in the sense that
  `GmUpdate` carries a monotonic `UpdateId`, and the applier rejects out-of-order packets:
  ```csharp
  // World.cs:10105
  if (_ECF0_F129_ED83_ECC8_F588.UpdateId - hehe(1) == UpdateId)
  ```
  ```csharp
  // World.cs:10301
  LastUpdateId = _ECF0_F129_ED83_ECC8_F588.UpdateId;
  ```
  `World.UpdateId` and `World.LastUpdateId` are plain `int` fields (`World.cs:4522` area).
* **The `ClientTime` struct is the clock object**, and it is tiny:
  ```csharp
  // work/decomp/DarzasDominion.Graphics/ClientTime.cs:3
  public class ClientTime
  // :5  public static ClientTime Time;
  // :7  public float Elapsed;
  // :9  public float ElapsedSec;
  // :11 public FrameData FrameData;
  // :13 public float Total;
  // :15 public float LogicTotal;
  // :17 public float LogicElapsedSec;
  // :19 public long TotalFrames;
  ```
  Its constructor stores the instance into its own static: `Time = this;` (`ClientTime.cs:23`), so
  `ClientTime.Time` is a static handle to the live clock. `DarzaWindow.Time` is a *separate* static
  (`public static ClientTime Time = new ClientTime();`, `DarzaWindow.cs:444`). `World` uses
  `(int)time.LogicTotal` from the parameter, while `Projectile.cs:670` uses
  `DarzaWindow.Time.LogicTotal` - assume they are the same object but verify live.

### 1.6 The single best per-frame hook

**`World.LateUpdate(ClientTime)`** (`work/decomp/DarzasDominion.Darza.Game/World.cs:6356`,
`public override void LateUpdate(ClientTime time)`).

Justification:

1. It is called once per frame from `Scene.UpdateScene` **after** `Update(time)` (`Scene.cs:1005`)
   and therefore after `World.Update` (`World.cs:9473`) and therefore after `Node.Update` through the
   whole tree (`World.cs:9552`) - i.e. after every character's and projectile's this-frame update.
2. It is an `override` of a `public virtual` base method (`Node.LateUpdate`, `Node.cs:2389`), so it is
   a normal virtual call - a managed method that can be detoured by patching its JIT'd entry from
   `MethodDesc`, or by hooking the vtable slot of `World` if we can reach the object.
3. It is on `World`, not on a per-object method, so the hook is **once per frame** rather than
   once per projectile (which matters: 100+ projectiles/frame is normal, and a detour that takes a
   lock or allocates will show up in the frame budget).
4. `World.Shared` is reachable statically (`public static World Shared;`, `World.cs:4216`), so from
   the hook we do not even need the `this` pointer - we can read the singleton.

**Second-best (and better in one respect): `World.Update` (`:9473`) itself.** Hooking the *entry* of
`World.Update` gives a per-frame tick that runs **before** the objects move this frame (good for
deciding "where should I move", since the answer is consumed by the movement call at `:9567` later in
the same function); hooking the *exit* gives the same state as `World.LateUpdate`. If the detour is a
raw entry patch, the cleanest architecture is: entry hook -> snapshot and run the predictive model ->
let the original proceed, so the client's own state is untouched and our decision is applied through
the client's own input/escape API (Sec 7) rather than by writing state.

**A caveat for both:** if `World.Update`/`World.LateUpdate` were inlined by the JIT into the caller,
there is no entry point to patch. `World.Update` is a large `override` called through a virtual
dispatch from `Node.Update` over a `List<Node>`, which in practice is **not** inlined; but
`TieredCompilation: false` means the method is compiled once at first call and stays, so a detour must
be applied before the first call (or force a recompile). Code patching of the entry point is the
strategy for both.

**Do not hook `Node.Update`/`Node.LateUpdate`** - they are the single hottest methods in the tree
(every UI node, every projectile, every label) and would fire thousands of times per frame.
---

## 2. THE NETWORK TICK

### 2.1 The handler table

`DarzasDominion.Darza.Networking.Client` is `public static class Client`
(`work/decomp/DarzasDominion.Darza.Networking/Client.cs:21`). The dispatch table is:

```csharp
// Client.cs:140
private static readonly Dictionary<GmPacketType, List<Func<GmPacket, bool>>> hehe_E5EC_EDDD_E866_F7B6_EF09;
```

i.e. **packet id -> an ordered list of handlers**, each returning `bool` (whether it consumed the
packet). Registration:

```csharp
// Client.cs:805
public static void AddHandler(GmPacketType type, Func<GmPacket, bool> handler)
// Client.cs:807
	Dictionary<GmPacketType, List<Func<GmPacket, bool>>> obj = hehe_E5EC_EDDD_E866_F7B6_EF09;
// Client.cs:816-822
	if (hehe_E5EC_EDDD_E866_F7B6_EF09.ContainsKey(type) && 0 == 0)
	{
		hehe_E5EC_EDDD_E866_F7B6_EF09[type].Add(handler);
		return;
	}
	List<Func<GmPacket, bool>> value = new List<Func<GmPacket, bool>> { handler };
	hehe_E5EC_EDDD_E866_F7B6_EF09[type] = value;
```

`AddHandler`/`RemoveHandler` (`:833`) are `public static`; the dictionary and a scratch
`Func<GmPacket, bool>[]` (`hehe_E5EC_EDDD_E866_F7B6_EF10`, `:880`) are `private static`. Invocation
copies the handler list under `Monitor` into the scratch array and then calls each handler outside the
lock (`:883-909`) - so **handlers run on whatever thread invoked the dispatch, without the lock held**.

`World` registers its own handlers exactly once, in `World.OnAddToScene`:

```csharp
// World.cs:6976
protected override void OnAddToScene(Scene scene)
// World.cs:7045
Shared = this;
// World.cs:7194
Client.AddHandler((GmPacketType)hehe(1), hehe_EB2C_E5A5_EE82_E9C5_EF81);
```

`hehe(1) == GmPacketType.GmUpdate == 1` (id from the task; the lookup itself is opaque). `Shared = this`
at `:7045` is where the static root is set - valuable: `World.Shared` is non-null from the moment the
world joins a scene.

### 2.2 Which thread does what

The receive path is **on the Telepathy network thread**:

```csharp
// Client.cs:299
telepathyClient.OnData = action2;
```
```csharp
// Client.cs:423
private static void hehe_EFF0_ED69_F541_E66F_F1F9(ArraySegment<byte> _ECF0_F129_ED83_ECC8_F588)
// Client.cs:469
gmPacket = GmPacket.Parse(_ECF0_F129_ED83_ECC8_F588);
```

Then a **warm/cold split**, driven by a per-packet-type bool array:

```csharp
// Client.cs:152
private static readonly bool[] hehe_E5EC_EDDD_E866_F7B6_EF0F;
```
```csharp
// Client.cs:475-491
typeId = gmPacket.TypeId;
if ((uint)typeId < (uint)hehe_E5EC_EDDD_E866_F7B6_EF0F.Length)
{
	if (hehe_E5EC_EDDD_E866_F7B6_EF0F[typeId])
	{
		// -> handled immediately, on the network thread
	}
}
// Client.cs:461        (warm path, network thread)
hehe_EFF0_ED69_F541_E66F_F1F9(gmPacket);
// Client.cs:466        (cold path)
hehe_E5EC_EDDD_E866_F7B6_EF0E.Enqueue(new hehe_F563_EF52_E77C_F1B6_EDF2(gmPacket));
```

The cold queue is a `ConcurrentQueue<...>` drained on the game thread at the top of the frame:

```csharp
// Client.cs:502
public static void DrainColdQueue()
// Client.cs:506
while (hehe_E5EC_EDDD_E866_F7B6_EF0E.TryDequeue(out result))
// Client.cs:518
hehe_EFF0_ED69_F541_E66F_F1F9(result.hehe_E4A7_E4A9_F6C0_ED6C_E272);
```
called from `DarzaWindow.Update`:
```csharp
// DarzaWindow.cs:1763
Client.DrainColdQueue();
```

**So both are possible, per packet type.** Cold packets are applied on the **game thread** (early in
the frame, before `CurrentScene.UpdateScene`). Warm packets are applied on the **network thread** and
touch shared state concurrently. Whether `GmUpdate` (id 1) is warm or cold is decided by the
`hehe_E5EC_EDDD_E866_F7B6_EF0F` array, which is **built at runtime** by
`hehe_EFF0_ED69_F541_E66F_F1F9()` and reset on disconnect:

```csharp
// Client.cs:948
hehe_E5EC_EDDD_E866_F7B6_EF0F = hehe_EFF0_ED69_F541_E66F_F1F9();
// Client.cs:963
hehe_E5EC_EDDD_E866_F7B6_EF0E = new ConcurrentQueue<hehe_F563_EF52_E77C_F1B6_EDF2>();
// Client.cs:953
hehe_E5EC_EDDD_E866_F7B6_EF09 = new Dictionary<GmPacketType, List<Func<GmPacket, bool>>>();
```
**The contents of that whitelist are `UNKNOWN - needs live measurement`** (the builder method was not
enumerated). This matters for thread-safety reasoning, but note that it does **not** change where
`GmUpdate` is *applied*: the handler only enqueues (below), so the application is serialised on the
game thread either way. That part is certain from `World.cs:15934`.

### 2.3 How `GmUpdate` (id 1) is applied

Handler:
```csharp
// World.cs:15910
private bool hehe_EB2C_E5A5_EE82_E9C5_EF81(GmPacket _ECF0_F129_ED83_ECC8_F588)
// World.cs:15912
FrameProfiler.NoteServerUpdate();
// World.cs:15934
hehe_E331_E9CE_F5E3_EBF5_EA31.Enqueue((GmUpdate)_ECF0_F129_ED83_ECC8_F588);
// World.cs:15938
Interlocked.Increment(ref hehe_E331_E9CE_F5E3_EBF5_EA34);
```

Queue:
```csharp
// World.cs:4272
private readonly ConcurrentQueue<GmUpdate> hehe_E331_E9CE_F5E3_EBF5_EA31 = new ConcurrentQueue<GmUpdate>();
```

Drain + apply, inside `World.Update`, on the game thread:
```csharp
// World.cs:9681
if (!hehe_E331_E9CE_F5E3_EBF5_EA31.IsEmpty || 1 == 0)
// World.cs:9683-9693
	if (SendPacket && 0 == 0)
	{
		hehe_EB2C_E5A5_EE82_E9C5_EF51();
	}
	GmUpdate result3;
	while (hehe_E331_E9CE_F5E3_EBF5_EA31.TryDequeue(out result3))
	{
		hehe_EB2C_E5A5_EE82_E9C5_EF53(result3);
		hehe_E331_E9CE_F5E3_EBF5_EA35 += hehe(1);
		hehe_EB2C_E5A5_EE82_E9C5_EF8C();
	}
```

Applier: `World.hehe_EB2C_E5A5_EE82_E9C5_EF53(GmUpdate)` at `World.cs:10103`. It is large and handles,
in order: `UpdateId` sequencing (`:10105`), removed objects (`:10109-10138`), per-object stats
including **creating the local `Player`**:

```csharp
// World.cs:10142
if (reference.ObjectId == MapInfo.PlayerId)
// World.cs:10154
Player = new Player(reference.ObjectId, DataManager.Data.Objects[reference.Type]);
```

then enemy/character spawn/despawn, and finally `LastUpdateId = ...UpdateId` (`:10301`).

**Conclusion for the hook.** `GmUpdate` application is **serialised on the game thread**, inside
`World.Update`. There is no separate game-thread "tick boundary" beyond that: the client does not run
a fixed logic step, so the closest thing to a tick boundary is "the drain at `World.cs:9681-9694`
completed". The server's own tick identity is `GmUpdate.UpdateId` (a monotonically increasing counter,
compared as `UpdateId - 1 == World.UpdateId` at `:10105`).

### 2.4 Sibling spawn channels (needed by Sec 3)

The projectile batch has its own queue and its own drain, in the same block:

```csharp
// World.cs:4252
private readonly ConcurrentQueue<GmProjectiles> hehe_E331_E9CE_F5E3_EBF5_EA27 = new ConcurrentQueue<GmProjectiles>();
```
```csharp
// World.cs:9712-9716
if (num12 <= hehe(0) || !hehe_E331_E9CE_F5E3_EBF5_EA27.TryDequeue(out var result5))
{
	break;
}
hehe_EB2C_E5A5_EE82_E9C5_EF56(result5);
```

Similarly `GmLasers` -> `:9723-9727`, `GmLaserRetarget` -> `:9735`, `GmAllyLasers` -> `:9743`,
`GmSweeps` -> `:9748`, `GmAllySweeps` -> `:9753`. So a hook that wants "all entity/projectile state
has caught up to the server for frame N" must sit at/after `World.Update`'s drain block. Each of these
drains is bounded (a literal cap, e.g. the `num12 <= hehe(0)` guard), so a burst larger than the cap
is spread across frames - which is itself a measurable property worth knowing (Sec 10).

The projectile-batch applier itself:

```csharp
// World.cs:10975
private void hehe_EB2C_E5A5_EE82_E9C5_EF56(GmProjectiles _ECF0_F129_ED83_ECC8_F588)
// World.cs:10977-10980
if (Player == null) { return; }
// World.cs:10989-10994   acknowledge the batch with our position+time
Client.SendPacket(new GmProjectilesAck
{
	ProjectileId = _ECF0_F129_ED83_ECC8_F588.ProjectilesId,
	Time = ClientTime,
	Position = Player.GamePoint
});
// World.cs:11025-11050   per projectile in the batch
for (; i < projectiles.Length; i += 1)
{
	GameProjectile gameProjectile = projectiles[i];
	if (!Objects.TryGetValue(gameProjectile.OwnerId, out var value2) || !(value2 is Entity entity))
	{
		// World.cs:11030-11039  owner unknown yet -> park with a TTL
	}
	else if (!(entity is Character character) || character is Player || !hehe_EB2C_E5A5_EE82_E9C5_F000(character))
	{
		entity.ShootProjectile(gameProjectile, alpha);
	}
}
// World.cs:11051-11061   AoE bombs in the same batch
aoes = _ECF0_F129_ED83_ECC8_F588.Aoes;
for (; i < aoes.Length; i += 1)
{
	AoeProjectile aoe = aoes[i];
	if (Objects.TryGetValue(aoe.OwnerId, out var value3) && value3 is Entity entity2 && (!(entity2 is Player) || aoe.HasBombData))
	{
		entity2.ShootAoe(aoe);
	}
}
```

Note `World.cs:11028`: **spawns are resolved through `Objects[OwnerId]`** - the owner must be a live
object, otherwise the spawn is parked. And `World.cs:11048` (`entity.ShootProjectile(...)`) is the
single funnel through which every non-parked server projectile becomes a live `Projectile` object.
---

## 3. THE PROJECTILE MODEL

`Projectile` lives at `work/decomp/DarzasDominion.Darza.Game.Objects/Projectile.cs`, is
`public class Projectile : GameObject` (`:26`), 2337 lines, **not sealed**.

### 3.1 Declared fields that matter for a threat model

Position/rotation are **not** declared on `Projectile` - they come from `Node`, at the top of the
hierarchy. Full chain: `Projectile : GameObject : Node`.

| what | declaring type | member | type | line |
|---|---|---|---|---|
| position | `Node` | `Position` | `Vector3` | `work/decomp/DarzasDominion.Nodes/Node.cs:175` |
| exact (post-transform) position | `Node` | `ExactPosition` | `Vector3` | `Node.cs:131` |
| rotation (radians) | `Node` | `Rotation` | `float` | `Node.cs:179` |
| scale | `Node` | `Scale` | `Vector3` | `Node.cs:183` |
| collision radius | `GameObject` | `Radius` | `float` | `GameObject.cs:271` |
| object id | `GameObject` | `GameId` | `int` | `GameObject.cs:175` |
| data record | `GameObject` | `Data` | `GameObjectData` | `GameObject.cs:167` |
| live flag | `GameObject` | `Initialized` | `bool` | `GameObject.cs:207` |
| smoothed velocity | `GameObject` | `CurrentVelocity => _smoothedVelocity` | `Vector3` | `GameObject.cs:377` |
| tick position history | `GameObject` | `_tickPosition`, `_posAtTick`, `_tickPosOneAgo`..`_tickPosFourAgo` | `Vector3` | `GameObject.cs:311,315,317,319,321,323` |

`Projectile`'s own declared fields (all in `Projectile.cs`):

| field | type | line | notes |
|---|---|---|---|
| `Angle` | `float` | `:111` | spawn heading in radians; the threat model's `sin`/`cos` seed |
| `Damage` | `int` | `:113` | raw damage for *this* projectile |
| `LocalSeedDerived` | `bool` | `:115` | **true only for projectiles the local player fired** (3.5) |
| `DamagePercent` | `float` | `:117` | fraction-of-target-max-health damage |
| `ManaDamage` | `int` | `:119` | |
| `ManaDamagePercent` | `float` | `:121` | |
| `BarrierDamage` | `int` | `:123` | |
| `BarrierDamagePercent` | `float` | `:125` | |
| `CriticalHit` | `bool` | `:127` | |
| `Desc` | `ProjDesc` | `:129` | **the descriptor; the whole motion model hangs off this** |
| `HitsPlayer` | `bool` | `:131` | true = this projectile can hit player-side entities |
| `PlayerOwned` | `bool` | `:135` | true = fired by a player (not a monster) |
| `IsAbility` | `bool` | `:137` | |
| `Affix` | `AffixData` | `:139` | per-instance size/damage modifier |
| `QualitySize` | `float` | `:141` | default `hehe(2)` (= 1f), multiplies size |
| `HomingWireTargetId` | `int` | `:93` | wire-supplied homing target id |
| `_owner` | `Entity` | `:75` | live owner reference |
| `_ownerId` | `int` | `:89` | owner's `GameId` (assigned `owner?.GameId ?? 0`, `:841`) |
| `_startPosition` | `Vector3` | `:83` | spawn anchor |
| `_startTime` | `int` | `:85` | `(int)DarzaWindow.Time.LogicTotal` (`:670`) |
| `_angleSin` / `_angleCos` | `float` | `:63` / `:59` | cached spawn heading |
| `_lastAnchor` | `Vector3` | `:87` | refreshed each frame for `Anchored` projectiles |
| `_dead` | `bool` | `:65` | |
| `hits` | `HashSet<int>` | `:53` | game ids already hit |
| `_lastHitAt` | `Dictionary<int,int>` | `:55` | game id -> last hit ms (rehit delay) |
| `_entityHitCount` | `int` | `:57` | for `Pierces` |
| `_homing` family | `bool/float/Vector3/int` | `:95-109` | homing state machine (`_homingInit`, `_homingLocked`, `_homingTarget`, `_homingTargetId`, `_homingHeading`, `_homingSteer`, `_homingPos`, `_homingLastMs`) |
| `_rotationSpeed`, `_spin` | `float` | `:77`, `:79` | cosmetic spin |
| `lockRotation` | `bool` | `:133` | |
| spawn/death anim state | `float/bool` | `:143-161` | `_animTime`, `_growFrom`, `_growing`, `_deathScaleFrom`, `_deathAlphaFrom`, `_deathFadeSeconds`, `_shadowShrinkSeconds`, `_shadowScaleFrom` |

Notably **absent**: there is no per-projectile speed field. Speed comes from `Desc.Speed` via
`Desc.GetVelocityAt(t)` (3.6.3).

### 3.2 The per-frame motion update

```csharp
// Projectile.cs:1091
public override void LateUpdate(ClientTime time)
```

`public override` - it overrides `GameObject.LateUpdate` (`GameObject.cs:2542`), which overrides
`Node.LateUpdate` (`Node.cs:2389`), which is `public virtual`. So this is a virtual call and a valid
detour target (per-projectile, so it fires N times per frame - good for a per-projectile "about to
move" hook, bad for a global tick).

Its structure, with the age clock first:

```csharp
// Projectile.cs:1109
int num = World.ClientTime - _startTime;
```
so `num` is **age in milliseconds**, derived from the world's monotonic ms clock.

Then size/alpha over time (`:1110-1138`), then the death branch, then the live branch:

```csharp
// Projectile.cs:1141-1149
if (_dead && 0 == 0)
{
	if (!StepDeath(time.ElapsedSec) && !_homingInit)
	{
		int time2 = ((Desc.GetVelocityAt((float)Desc.Lifetime) < hehe(5)) ? _deathTimeAlive : num);
		Desc.GetPosition(time2, _angleSin, _angleCos, out x, out y, GameId);
		Position = AnchorBase() + new Vector3(x, y, hehe(5));
	}
	return;
}
```

The three-way motion dispatcher, which is the heart of it:

```csharp
// Projectile.cs:1151
if (((HomingDrives(num) ? true : false) ? StepHoming(num, out x, out y) : ((Desc.WallBounce > hehe(0)) ? StepWallBounce(num, out x, out y) : Desc.GetPosition(num, _angleSin, _angleCos, out x, out y, GameId))) && 0 == 0)
```

and the commit:

```csharp
// Projectile.cs:1173-1174
Vector3 position = Position;
Position = AnchorBase() + new Vector3(x, y, hehe(5));
```

So the invariant is: **`x`,`y` are an offset from an anchor; `Position = AnchorBase() + (x,y,0)`.**

```csharp
// Projectile.cs:325-344
private Vector3 AnchorBase()
{
	if (Desc != null)
	{
		if (... !Desc.Anchored ...) { goto IL_002a; }
		if (_owner != null && _owner.Scene != null && _owner.GameId == _ownerId)
		{
			_lastAnchor = new Vector3(_owner.Position.X, _owner.Position.Y, hehe(5));
		}
		return _lastAnchor;
	}
	IL_002a:
	return _startPosition;
}
```

i.e. an `Anchored` projectile follows a live owner (`_lastAnchor` refreshed each call when
`Desc.Anchored` and `_owner.GameId == _ownerId`), otherwise the offset is relative to a **fixed**
`_startPosition`. This distinction is essential for a predictive model: anchored attacks move with
the caster.

Rotation is cosmetic-lagged:

```csharp
// Projectile.cs:1175-1203
if ((!lockRotation || 1 == 0) && (!Utils.Equals(Position.X, position.X) || !Utils.Equals(Position.Y, position.Y) || 1 == 0))
{
	float to = MathF.Atan2(Position.Y - position.Y, Position.X - position.X) + new Angle((float)((ProjectileData)Data).AngleFix, false).Radians;
	float t = hehe(2) - MathF.Exp(hehe(131) * time.ElapsedSec);   // 1 - exp(-FacingLerpRate*dt)
	_angleRotation = LerpAngle(_angleRotation, to, t);
	Rotation = _angleRotation;
}
```
with `private const float FacingLerpRate = 8f;` (`Projectile.cs:81`) and a `LerpAngle` helper
(`:2152`). `hehe(2)` is 1f and `hehe(131)` is `-8f` (so `1 - e^{-8 dt}`).

The size/radius block, which is where the collision radius actually changes during flight:

```csharp
// Projectile.cs:1114-1124
if (Desc != null && 0 == 0 && Desc.SizeGain != hehe(7))
{
	float num2 = Desc.SizeAt((float)num / hehe(70));
	if (Affix != null && 0 == 0) { num2 *= Affix.ProjectileSize; }
	num2 *= QualitySize;
	SetSize(num2, false);
	Radius = num2 * hehe(1);
}
```

**The actual position formula for the common (non-homing, non-bouncing) case lives in `ProjDesc`,
not in `Projectile`** - see 3.6.

### 3.3 Lifetime / expiry, and leaving the map

Lifetime is decided **inside** `ProjDesc.GetPosition`: the method returns `true` when the projectile
has expired, and the `if (...)` at `Projectile.cs:1151` treats a `true` return as "died":

```csharp
// ProjDesc.cs:1280
return ((!((double)time >= Lifetime * hehe(24))) ? 1 : 0) == hehe(0);
```
and identically in the sine variant:
```csharp
// ProjSinDesc.cs:217
return ((!((double)time >= Lifetime * hehe(24))) ? 1 : 0) == hehe(0);
```
`hehe(24)` is 1000, so the predicate is `!(timeMs >= Lifetime_seconds * 1000)` - i.e. **return `true`
(expired) once `age_ms >= Lifetime * 1000`**. `Lifetime` is a `double` in **seconds**
(`ProjDesc.cs:138`).

On expiry (`Projectile.cs:1153-1171`):
```csharp
// Projectile.cs:1153-1158
_dead = true;
_deathTimeAlive = num;
if (!Prefs.projectile_grow || 1 == 0)
{
	Remove();
	return;
}
```
So with the `projectile_grow` preference off, the projectile is removed immediately; with it on, it
plays a shrink/fade (`StepDeath`, `:1260`) driven by constants
`SpawnGrowSeconds = 0.1f` (`:143`) and `DeathShrinkSeconds = 0.09f` (`:145`).

**Wall/terrain collision terminates the projectile** (3.4) via `Remove()`. `Remove()` (`:1055`) is:
```csharp
// Projectile.cs:1057, 1079, 1083
RemoveFromParent();
World = null;
RemovedFromWorld(World);
```
and `OnRemoveFromScene` (`:1019`) clears the light children and calls `Remove()`.

**Note what `Remove()` does NOT do: it never calls `Allocations.Projectiles.Return(this)`.** Grepping
the whole tree for `.Projectiles.Return` / `Projectiles?.Return` returns nothing. So ordinary expiry
detaches the object and it becomes garbage; the pool is not refilled by that path. Practically:
**do not assume a `Projectile*` you cached last frame is still live and still describes the same
projectile** - check scene membership (Sec 5.3).
### 3.4 Collision - the exact predicate

`LateUpdate` tests **terrain first, then entities**:

```csharp
// Projectile.cs:1226-1250
bool hit3d;
StaticObject staticObject = World.ProjectileCollides((int)Position.X, (int)Position.Y, out hit3d);
if (staticObject != null && 0 == 0)
{
	if ((hit3d ? true : false) || ((!Desc.IgnoreTerrain || 1 == 0) && Desc.WallBounce <= hehe(0)))
	{
		if (HitsPlayer && 0 == 0)
		{
			Client.SendPacket(new GmAllyHit { Time = World.ClientTime, ProjectileId = GameId, ObjectId = staticObject.GameId });
		}
		World?.CreateHitEffect(staticObject, this);
		Remove();
		return;
	}
}
else if ((Desc.Telegraph <= hehe(7) || (double)num >= Desc.Telegraph * hehe(24)) && Hit() && 0 == 0)
{
	Remove();
	return;
}
```

* `World.ProjectileCollides(int x, int y, out bool hit3d)` is at `World.cs:13411`.
* A terrain hit sends `GmAllyHit` **when `HitsPlayer`** and then removes the projectile (unless
  `IgnoreTerrain` and it is allowed to bounce).
* Entity collision only runs when the projectile is **not** inside a solid tile, and only after the
  telegraph window: `num >= Desc.Telegraph * 1000`.
* `Hit()` (`:1505`) dispatches on `HitsPlayer`:
  ```csharp
  // Projectile.cs:1505-1512
  private bool Hit()
  {
  	if (HitsPlayer && 0 == 0)
  	{
  		return HitCharacter();
  	}
  	return HitEnemy();
  }
  ```

**`HitCharacter()` - the moment a projectile is decided to hit the local player.** The most important
predicate in the whole document:

```csharp
// Projectile.cs:1514
private bool HitCharacter()
// Projectile.cs:1516
int clientTime = World.ClientTime;
// Projectile.cs:1521
if (World.Player != null && 0 == 0 && (!World.Player.KnockedUp || 1 == 0) && (!World.Player.SpiritMode || 1 == 0))
```
```csharp
// Projectile.cs:1597        (the plain, non-homing radius case)
num2 = World.Player.Radius + Radius;
```
```csharp
// Projectile.cs:1553-1556   (proximity case)
if (0 == 0 && (!PlayerOwned || 1 == 0))
{
	num2 = MathF.Max(num2, (float)Desc.Proximity);
}
```
```csharp
// Projectile.cs:1573-1592   (THE collision test + hit + local damage + wire claim)
if (DistanceSquared(World.Player.Position) < num2 * num2 && 0 == 0 && CanHitEntity(World.Player.GameId, clientTime) && 0 == 0)
{
	bool num3 = RegisterEntityHit(World.Player.GameId, clientTime);
	World.Player.HitBy(this);
	if (Prefs.close_menus_on_damage && 0 == 0)
	{
		base.Scene?.OnDamage();
	}
	Client.SendPacket(new GmHit
	{
		Time = clientTime,
		ProjectileId = GameId,
		ObjectId = World.Player.GameId,
		PlayerProj = PlayerOwned
	});
	if (num3 && 0 == 0)
	{
		return true;
	}
}
```

So the **exact hit predicate against the local player** is:

```
DistanceSquared(World.Player.Position) < (World.Player.Radius + Projectile.Radius)^2
  AND CanHitEntity(World.Player.GameId, World.ClientTime)
```
with `num2` first raised to `max(num2, Desc.Proximity)` when `Desc.Proximity > 0` and the projectile is
not player-owned. `DistanceSquared` is a plain XY squared distance:
```csharp
// GameObject.cs:2088-2092
public float DistanceSquared(Vector3 pos)
{
	float num = Position.X - pos.X;
	float num2 = Position.Y - pos.Y;
	return num * num + num2 * num2;
}
```

**Two escape hatches a dodging model must respect:**

```csharp
// Projectile.cs:1521
if (World.Player != null && 0 == 0 && (!World.Player.KnockedUp || 1 == 0) && (!World.Player.SpiritMode || 1 == 0))
```
i.e. **while `Player.KnockedUp` or `Player.SpiritMode`, the local player cannot be hit at all** - the
predicate short-circuits and `HitCharacter()` falls through to `return false` at `:1604`. The same
guard exists on the other side in `Entity.HitBy(Projectile)` (`Entity.cs:2465`).

`CanHitEntity` / `RegisterEntityHit` implement pierce/rehit bookkeeping:

```csharp
// Projectile.cs:231
private bool CanHitEntity(int gameId, int now)
// Projectile.cs:234
if (_entityHitCount < MaxHits)
// Projectile.cs:240
return (hits.Contains(gameId) ? 1 : 0) == hehe(0);
// Projectile.cs:279
private bool RegisterEntityHit(int gameId, int now)
```
with
```csharp
// Projectile.cs:179-195
private int MaxHits
{
	...
	if (Desc.Pierces > hehe(0)) { return Desc.Pierces; }
	if (!Desc.IgnoreEntity) { ... }
	...
}
// Projectile.cs:214-222
private int RehitDelayMs
{
	...
	if (Desc == null || ... || !(Desc.RehitDelay >= hehe(7))) { return -1; }
	return (int)(Desc.RehitDelay * hehe(24));
}
```

`HitCharacter` also has a **second loop** over every *other* non-NPC character
(`World.GetCharactersNoNpc()`, `:1606`), including allies; that loop sends `GmAllyHit` (`:1628`) and
skips `KnockedUp`/`SpiritMode` characters:

```csharp
// Projectile.cs:1614
bool flag2 = (byte)((!(entity is Character character) || false || ((!character.KnockedUp || 1 == 0) && !character.SpiritMode)) ? hehe(0) : hehe(1)) != 0;
// Projectile.cs:1619-1620
float num4 = entity.Radius + Radius;
if (!(DistanceSquared(entity.Position) < num4 * num4) || false || !CanHitEntity(entity.GameId, clientTime))
```
and the local player is tested separately first at `:1573`, so **the local player is not double-hit by
the loop** (the loop body starts after the local test).

`HitEnemy()` (`:1643`) is the mirror for `HitsPlayer == false`: it iterates `World.GetEnemies()`
(`:1669`), skips `Npc` enemies (`:1676`), uses the same radius test (`:1680-1681`), calls
`enemy.HitBy(this)` (`:1684`) and sends `GmHit` **only when `PlayerOwned`** (`:1685-1694`).

### 3.5 Who decides damage: the client claims, and the server reconciles

This changes the whole design, so it is worth stating plainly.

**The client is the one that decides a hit happened and tells the server.** The entire server->client
packet directory (`work/decomp/DarzaGameNet.Packets.Game.GameServer/`, 150+ files, enumerated) contains
**no "damage taken" / "you were hit" packet**. Health arrives only as an absolute snapshot:

```csharp
// work/decomp/DarzaGameNet.Packets.Game.GameServer/GmHealthUpdate.cs:9-15
public short MaxHealth; public short Health; public short Shield; public short Barrier;
```

Meanwhile the outbound hit claims are explicit and client-authored:

```csharp
// Projectile.cs:1581-1587   (claim: this projectile hit me)
Client.SendPacket(new GmHit { Time = clientTime, ProjectileId = GameId, ObjectId = World.Player.GameId, PlayerProj = PlayerOwned });
// Projectile.cs:1234-1239   (claim: this projectile hit terrain)
Client.SendPacket(new GmAllyHit { Time = World.ClientTime, ProjectileId = GameId, ObjectId = staticObject.GameId });
// Projectile.cs:1628-1633   (claim: this projectile hit another character)
Client.SendPacket(new GmAllyHit { Time = clientTime, ProjectileId = GameId, ObjectId = entity.GameId });
// Projectile.cs:1687-1693   (claim: my projectile hit an enemy)
Client.SendPacket(new GmHit { Time = clientTime, ProjectileId = GameId, ObjectId = enemy.GameId, PlayerProj = PlayerOwned });
```

And there is a **handshake**: the server sends a projectile batch, the client acknowledges it with its
own current position and time (`World.cs:10989-10994`, quoted in 2.4). `GmProjectilesAck` fields:
`int ProjectileId`, `int Time`, `GamePoint Position`
(`work/decomp/DarzaGameNet.Packets.Game.Client/GmProjectilesAck.cs:9-13`).

**What this means for autododge/auto-nexus:** the HP *change* is computed locally by
`World.Player.HitBy(this)` (`Projectile.cs:1576`) and then reconciled against the server's absolute
values by `GmHealthUpdate`. So a hook can (a) intercept the hit before it is applied, (b) suppress it,
and (c) still be corrected by the server on the next `HealthUpdate`.

**This has since been measured on live traffic - see Sec 10.1 [live].** In short: the outbound
`GmHit` (`PlayerProj = false`, i.e. "I was hit") is followed by an HP fall in 105 of 160 analysable
cases, with a **median 56 ms / p90 99 ms** lag to the reflecting `GmHealthUpdate`, and the health
channel is cadenced at **~50 ms / ~100 ms**. So the client's report demonstrably drives the server.
Whether the server *also* independently simulates (and would apply damage we never reported) is the
one reduced question still open; the experiment is at the end of Sec 10.1.
### 3.6 `ProjDesc` and the motion formulas

`ProjDesc` is `work/decomp/DarzaCore.Data/ProjDesc.cs`, 1463 decompiled lines. Subclasses:
`ProjSinDesc` (`work/decomp/DarzaCore.Data/ProjSinDesc.cs`), `ProjBombDesc`
(`work/decomp/DarzaCore.Data/ProjBombDesc.cs:13`), `ProjCsinDesc` (`work/decomp/DarzaCore.Data/ProjCsinDesc.cs:5`),
plus `WallBouncePath.cs` (9220 bytes) and the `ProjectilePath` enum (`ProjectilePath.cs`).

#### 3.6.1 Full field list (base `ProjDesc`)

All are `public` fields unless marked. Types are exact.

| field | type | line | threat-model relevance |
|---|---|---|---|
| `DegreesToRadians` | `static double` | `:102` | unit constant on every angular field |
| `AngleGap` | `double` | `:104` | fan spread between `NumProj` projectiles |
| `Boomerang` | `bool` | `:106` | motion reverses at half range |
| `BreaksArmor` | `bool` | `:108` | derived in `Parse` from `Effects` (`:661-667`) |
| `Curve` | `double` | `:110` | deg/s angular drift |
| `CurveBreak` | `double` | `:112` | curve stops after this long |
| `CurveDelay` | `double` | `:114` | curve starts after this long |
| `NewCurve` | `double` | `:116` | second (constant-curvature) mode |
| `Effects` | `EffectDesc[]` | `:118` | status application |
| `Gain` | `double` | `:120` | **speed acceleration** |
| `GainDelay` | `double` | `:122` | |
| `Accelerations` | `Acceleration[]` | `:124` | **piecewise speed profile (takes priority over `Gain`)** |
| `Id` | `string` | `:126` | XML identity; key into `DataManager.Data.ObjectsId` |
| `IgnoreArmor` | `bool` | `:128` | |
| `IgnoreArmorPercentage` | `double` | `:130` | **armor pierce** |
| `IgnoreEntity` | `bool` | `:132` | |
| `IgnoreTerrain` | `bool` | `:134` | |
| `Friendly` | `bool` | `:136` | |
| `Lifetime` | `double` | `:138` | **seconds** |
| `MaxDamage` | `short` | `:140` | |
| `MaxSpeed` | `double` | `:142` | cap when `Gain != 0` |
| `MinDamage` | `short` | `:144` | |
| `NumProj` | `int` | `:146` | **count in the fan** |
| `Orbit` | `double` | `:148` | |
| `OrbitBreak` | `double` | `:150` | |
| `OrbitDelay` | `double` | `:152` | |
| `Pierces` | `int` | `:154` | max entity hits |
| `RateOfFire` | `double` | `:156` | |
| `StartOffset` | `double` | `:158` | |
| `Variance` | `double` | `:160` | |
| `RehitDelay` | `double` | `:162` | default `hehe(17)`; clamped up to >= `hehe(32)` in `Parse` (`:694-697`) |
| `Size` | `double` | `:164` | default `hehe(6)`; collision radius derives from it |
| `SizeGain` | `double` | `:166` | radius growth over time |
| `AlternateCurve` | `bool` | `:168` | |
| `HasOnExpire` / `OnExpireIndex` / `OnExpireCount` / `OnExpireAngleGap` / `OnExpireInherit` / `OnExpireOnWall` | `bool/byte/int/double/bool/bool` | `:170-180` | child spawn on expiry |
| `HasEmit` / `EmitIndex` / `EmitCount` / `EmitAngleGap` / `EmitInterval` / `EmitInherit` | `bool/byte/int/double/double/bool` | `:182-192` | child spawn while flying |
| `Max_Emits_Per_Projectile` | `const int 16` | `:194` | hard cap |
| `Telegraph` | `double` | `:196` | **seconds of non-colliding windup** |
| `SpeedWaveFreq` | `double` | `:198` | speed oscillation |
| `SpeedWaveAmp` | `double` | `:200` | |
| `Proximity` | `double` | `:202` | **extra hit radius floor for non-player-owned projectiles** |
| `WallBounce` | `int` | `:204` | |
| `Anchored` | `bool` | `:206` | moves with owner |
| `Speed` | `double` | `:208` | **the primary motion constant** |
| `TetherDuration` | `double` | `:210` | default `hehe(17)` |
| `TetherHealDamageFraction` | `const double 0.03` | `:212` | |
| `TetherHeal` | `double` | `:214` | default `hehe(36)` |
| `Offset` | `string` | `:216` | selects pre/post-rotation offset application |
| `XOffset`/`XOffsetMax`/`YOffset`/`YOffsetMax`/`AngleOffset` | `double` x5 | `:218-226` | LCG-randomised spawn placement |
| `Knockback` family (`Knockback`, `KnockbackStrength`, `KnockbackSpeed`, `KnockbackIncStrength`, `KnockbackIncSpeed`, `KnockbackIncrement`) | `bool/double*4/int` | `:228-238` | |
| `KnockUp` | `KnockUpDesc` | `:240` | drives `Character.KnockedUp` |
| `Trails` | `TrailDesc[]` | `:242` | |
| `DamageHealthPercent` | `double` | `:244` | fraction-of-max-HP damage |
| `ManaDamage` / `DamageManaPercent` | `short` / `double` | `:246`, `:248` | |
| `BarrierDamage` / `DamageBarrierPercent` | `short` / `double` | `:250`, `:252` | |
| `DamageIncrement` / `IncDamage` | `int` / `short` | `:254`, `:256` | |
| `Lifesteal`/`LifestealPoints`, `Manasteal`/`ManastealPoints` | `bool/double` | `:258-264` | |
| `CanCriticalHit` | `bool` | `:266` | |
| `CriticalChance` | `double` | `:268` | |
| `CriticalDamage` | `double` | `:270` | used as the crit **multiplier** (3.7) |
| `NoFaceDirection` | `bool` | `:272` | |
| `NoHitNoCount` | `bool` | `:274` | |
| `Ricochet` / `RicochetAll` | `int` / `bool` | `:276`, `:278` | |
| `HomingTurnRate` | `double` | `:280` | **degrees; homing only when != 0** |
| `HomingDelay` / `HomingBreak` / `HomingRange` | `double` | `:282`, `:284`, `:286` | seconds / seconds / tiles; range default `hehe(15)` |
| `HomingSteerDeadZone` | `const float 1.25f` | `:288` | |
| `HomingPathProbeMs` | `const int 16` | `:290` | homing sub-step (ms) |
| `ChannelRate` | `double` | `:292` | |
| `_rangeVariants` / `_affixVariants` | `Dictionary<int,ProjDesc>` / `Dictionary<long,ProjDesc>` | `:294`, `:296` | cache for the variant factories |
| `IsHoming` | `bool => HomingTurnRate != 0` | `:298` | |
| `Path` | `virtual ProjectilePath` (base returns 0) | `:330` | **selects the subclass** |

Subclass `ProjSinDesc` adds (the "wavy" projectile):
```csharp
// ProjSinDesc.cs:14-24
public double Amplitude;
public double Frequency;
public double FreqGain;
public double AmpGain;
public bool Wavy;
public bool WavyRandom;
```

`ProjectileData` (the visual/data record, not the descriptor) adds:
```csharp
// work/decomp/DarzaCore.Data/ProjectileData.cs:34-42
public double AngleFix;
public double SpinSpeed;
public bool LockRotation;
public short[] AdditionalSprites;
public short[] AdditionalSpritesShiny;
```
and is `public class ProjectileData : GameObjectData` (`:11`).

**Where the loader is.** `ProjDesc.Parse(XElement)` (`ProjDesc.cs:632`) reads every field from XML
attributes/elements via `GameObjectData.ElementValue(...)` / `AttributeValue(...)`. The polymorphic
factory is:

```csharp
// ProjDesc.cs:1124
public static ProjDesc HandleProjectile(XElement element)
// ProjDesc.cs:1141
ProjectilePath key = ((text == null) ? ((ProjectilePath)hehe(0)) : ((ProjectilePath)Enum.Parse(typeof(ProjectilePath), text)));
// ProjDesc.cs:1144
ProjDesc obj2 = (ProjDesc)Activator.CreateInstance(_projectileHandlers.Value[key]);
// ProjDesc.cs:1145
obj2.Parse(element);
```
with the handler map a `ThreadLocal<Dictionary<ProjectilePath, Type>>`:
```csharp
// ProjDesc.cs:82
private static readonly ThreadLocal<Dictionary<ProjectilePath, Type>> _projectileHandlers = new ThreadLocal<Dictionary<ProjectilePath, Type>>(delegate
```
`EntityData` wires the XML sequence to that factory:
```csharp
// work/decomp/DarzaCore.Data.Entities/EntityData.cs:16
public static Func<XElement, ProjDesc> _003C0_003E__HandleProjectile;
// EntityData.cs:382-385
Func<XElement, ProjDesc> func = _003C_003EO._003C0_003E__HandleProjectile;
...
func = (_003C_003EO._003C0_003E__HandleProjectile = ProjDesc.HandleProjectile);
```

**Where the table lives at runtime.** There is **no global `Dictionary<int, ProjDesc>` registry of all
projectile descriptors.** Descriptors hang off *owners*:

```csharp
// work/decomp/DarzaCore.Data.Entities/EntityData.cs:91
public ProjDesc[] Projectiles;
// EntityData.cs:128
projectile = Projectiles[index];
```
and off equipment: every consumer reads `equip.Projectiles` / `equipmentData.Projectiles`
(`work/decomp/DarzaCore.Data.ItemPower/AffixPointMath.cs:207,1047,1298,1414,1499`;
`work/decomp/DarzaCore.Data.ItemPower.Analysis/Analyzers.cs:1171,1496,2260,2473`).

**The critical consequence for a hook:** a live `Projectile` gives you its `Desc` **directly**
(`public ProjDesc Desc;`, `Projectile.cs:129`), assigned in `ProcessProjectileData`
(`Projectile.cs:892 Desc = desc;`). So **you never need to look up a descriptor by id** - read
`projectile->Desc` and dereference the fields. Only if you want the descriptor *before* the projectile
object exists (e.g. from `ShootProjectile`) do you need
`TryGetProjectile(objectType, index, slotIndex, out ProjDesc)` (`Entity.cs:2184`, `protected virtual`)
or `DataManager.Data.ObjectsId[desc.Id]` (`Entity.cs:1873`). The exact runtime shape of
`DataManager.Data` (`Objects`, `ObjectsId`, `Items`, `Affixes`, `LightIds`) is
**UNKNOWN - needs live measurement** for field offsets; the classes are
`work/decomp/DarzaCore.Data/GameData.cs` (121 KB) and `GameObjectData.cs` (18 KB) and were not
enumerated field by field. The accessors are visible in use, e.g.
`GameData.Shared.LightIds[lightDesc.Id]` (`Projectile.cs:824`), `GameData.Shared.Affixes` and
`DataManager.Data.Items` (`Entity.cs:1795,1808`), `DataManager.Data.Objects` (`World.cs:10148,10154`).
#### 3.6.2 The base motion formula (`ProjDesc.GetPosition`)

```csharp
// ProjDesc.cs:1149
public virtual bool GetPosition(int time, float sin, float cos, out float x, out float y, int gameId = 0)
```

Reconstructed arithmetic (dropping the flattened `switch` scaffolding and the always-false guards):

```
if (Telegraph > 0)
    time = max(0, time - (int)(Telegraph * 1000));          // :1159
dt        = time / 1000f;                                    // :1161
speed     = (float)Speed;                                    // :1162
heading   = atan2(sin, cos);                                 // :1163
curDt     = (OrbitDelay >= 0) ? min(dt, OrbitDelay) : dt;    // :1164
lifeTime  = (OrbitDelay >= 0) ? min(Lifetime, OrbitDelay) : Lifetime;  // :1165
dist      = curDt * speed;                                   // :1166
if (Accelerations != null && Accelerations.Length != 0)
    dist = ComputeAccelerationDistance(dt);                  // :1169
else if (Gain != 0 && dt >= GainDelay)
    dist = GetDistance(-1f);                                 // :1173
if (SpeedWaveAmp != 0 && SpeedWaveFreq != 0 && Gain == 0 && (Accelerations == null || Accelerations.Length == 0))
    dist += (float)(speed * SpeedWaveAmp / SpeedWaveFreq * (1 - cos(SpeedWaveFreq * curDt)));   // :1177
```

Then angular drift is added to the heading (`:1179-1204`):

```
newCurveActive = IsNewCurveActive(dt) && !Boomerang;         // :1179
sign = CurveSign(gameId);                                    // :1180
delta = 0;
if (!newCurveActive && Curve != 0 && dt - CurveDelay > 0) {  // :1182
    t = dt - CurveDelay;
    if (CurveBreak > 0) t = min(dt - CurveDelay, CurveBreak + CurveDelay);
    delta = DegreesToRadians * Curve * t * sign;             // :1189
}
if (!newCurveActive && Orbit != 0 && dt - OrbitDelay >= 0) { // :1191
    t = dt - OrbitDelay;
    if (OrbitBreak > 0) t = min(dt - OrbitDelay, OrbitBreak + OrbitDelay);
    delta = DegreesToRadians * Orbit * t * sign;             // :1198
}
if (delta != 0) { cos = cos(heading + delta); sin = sin(heading + delta); }   // :1202-1203
```

Boomerang overrides the distance (`:1205-1213`):

```
if (Boomerang) {
    dist = (time / 1000f) * Speed;                           // :1207
    peak = (Lifetime * Speed) / hehe(7);                     // :1208
    if (dist > peak) dist = peak - (dist - peak);             // :1211
}
```
**Caveat:** the divisor literal at `:1208` is `hehe(7)`, which is the *zero* sentinel everywhere else
(`Speed != hehe(7)`, `Telegraph > hehe(7)`). Taken literally, `peak = Lifetime*Speed/0` is nonsense;
the sane reading is `peak = Lifetime * Speed / 2`. **The divisor's value is UNKNOWN - needs live
measurement**; it materially affects every boomerang projectile's range. Experiment in Sec 10.4.

Then the local offset is built (`:1214-1222`):

```
if (newCurveActive) ComputeNewCurveLocal(dt, dist, out x, out y, sign);
else { x = dist; y = 0; }
```

`Rotate(sin, cos, ref x, ref y)` (`ProjDesc.cs:1347`) is a pure 2-D rotation (recovered statement
order from the assignments' data dependencies):

```csharp
// ProjDesc.cs:1349 + :1374 + :1378 + :1384
float num = x;      // x
float num3 = y;     // y
x = num * cos - num3 * sin;
y = num * sin + num3 * cos;
```

LCG-based spawn jitter is applied around it (`:1223-1272`):

```
LCG.Value.Initialize((uint)gameId);                                   // :1223
if (XOffsetMax != 0) xOff = (float)LCG.Value.NextDouble(XOffset, XOffsetMax);   // :1248
if (YOffsetMax != 0) yOff = (float)LCG.Value.NextDouble(YOffset, YOffsetMax);   // :1260
if (Offset == <first string>) { x += yOff; y += xOff; }               // :1264-1265
Rotate(sin, cos, ref x, ref y);                                       // :1267
if (Offset == <second string>) { x += xOff; y += yOff; }              // :1270-1271
```

and the expiry return at the end (`:1280`). Note the jitter is **deterministic in `gameId`** - the
LCG is re-seeded from the projectile id - so a predictive model *can* reproduce it exactly, provided
it knows the LCG. The LCG is `LinearCongruentialGenerator` in `DarzaCore.Tools`, held in a
`ThreadLocal` (`ProjDesc.cs:100`, `ProjSinDesc.cs:12`); the `Initialize`/`NextDouble` arithmetic is
**UNKNOWN - needs live measurement** (Sec 10.5).

`GetDistance` (the local function for the `Gain` path, `:1281-1344`) is:

```
num15 = 0;                     // start (distance travelled at max speed)
if (MaxSpeed != 0) {                                     // :1296
    num14 = MaxSpeed;
    num18 = Math.Abs(num14 - Speed) / Math.Abs(Gain);     // :1307  time to reach MaxSpeed
    if (dt - GainDelay - num18 > 0) {                     // :1315
        num18 = Math.Min(curDt - GainDelay, num18);       // :1322
        num15 = curDt - GainDelay - num18;                // :1311
    }
} else {
    num18 = lifeTime - GainDelay;                         // :1337
}
num17 = num15 * num14;                                    // :1339  distance at max speed
num18sq = num18 * num18 * hehe(9) * (float)Gain;           // :1340  hehe(9) == 0.5
num19 = num18 * speed;                                    // :1341
return GainDelay * speed + num19 + num18sq + num17;        // :1343
```
i.e. **`distance = GainDelay*Speed + t_gain*Speed + 0.5*Gain*t_gain^2 + t_max*MaxSpeed`** - the usual
piecewise-constant-acceleration kinematics, with `hehe(9) == 0.5`.

#### 3.6.3 `GetVelocityAt`

```csharp
// ProjDesc.cs:956
public float GetVelocityAt(float dt)
```
Two branches:

* **Accelerations present** (`:958-1035`): walks the `Acceleration[]` list, takes the segment whose
  `Wait` has passed, and returns
  ```csharp
  // ProjDesc.cs:1024
  num = num4 + num5 * num6;
  ```
  where `num4` is the segment's starting speed (the first segment uses `acceleration.StartSpeed`,
  `:1010`), `num5 = (float)acceleration.Amount`, and `num6` is elapsed time inside the segment, clipped
  by the segment's `Clamp`:
  ```csharp
  // ProjDesc.cs:1021-1022
  float num7 = (float)((acceleration.Clamp - (double)num4) / (double)num5);
  num6 = ((num7 <= hehe(5)) ? hehe(5) : Math.Min(num2, num7));
  ```
  So it is a **piecewise-linear speed ramp**: `v = v_seg_start + Amount * (elapsed - Delay)`.
* **Otherwise** (`:1037-1045`):
  ```csharp
  // ProjDesc.cs:1037-1043
  float num8 = (float)Speed;
  if (Gain != hehe(7) && (double)dt >= GainDelay)
  {
  	num8 += (float)(Gain * ((double)dt - GainDelay));
  	if (!Utils.Equals(MaxSpeed, hehe(17)) || 1 == 0)
  	{
  		num8 = ((Gain > hehe(7)) ? Math.Min(num8, (float)MaxSpeed) : Math.Max(num8, (float)MaxSpeed));
  	}
  }
  ```
  i.e. **`v(t) = Speed + Gain*(t - GainDelay)`, clamped by `MaxSpeed`** (min when accelerating, max
  when decelerating). `hehe(17)` is the "MaxSpeed unset" sentinel.
* Related helpers: `ComputeAccelerationDistance(float dt)` (`:868`), `MaxTravelDistance()` (`:1051`),
  `SampleMaxPathRange()` (`:1492`), `ComputeNewCurveLocal` (`:1390`), `IsNewCurveActive` (`:1465`),
  `CurveSign(int gameId)` (`:1474`), `SizeAt(float dt)` (`:1483`), `PathHeadingAt(int,float,float,int,float)`
  (`:396`, which is `GetPosition` at `timeMs` followed by an angle estimate).
#### 3.6.4 The wavy/sine formula (`ProjSinDesc.GetPosition`)

```csharp
// ProjSinDesc.cs:82
public override bool GetPosition(int time, float sin, float cos, out float x, out float y, int gameId = 0)
```

Identical preamble to the base (telegraph, `dt`, `speed`, `curDt`, `lifeTime`, `dist`, speed-wave,
curve/orbit) and then the part that matters - the lateral sine offset:

```csharp
// ProjSinDesc.cs:147-157
float num9 = hehe(5);
if (Wavy && 0 == 0)
{
	LCG.Value.Initialize((uint)gameId);
	num9 = ((LCG.Value.NextDouble() < hehe(12)) ? hehe(5) : hehe(6));
}
else
{
	num9 = ((gameId % hehe(5) == 0) ? hehe(5) : hehe(6));
}
float num10 = (float)(Math.Sin((double)num9 + (double)dt * Frequency + hehe(12) * FreqGain * (double)dt * (double)dt) * (Amplitude + AmpGain * (double)dt));
```

i.e.

```
phase0  = Wavy ? (LCG(gameId).NextDouble() < p ? 0 : PI) : ((gameId % M == 0) ? 0 : PI)
lateral(dt) = sin( phase0 + dt*Frequency + 0.5*FreqGain*dt^2 ) * (Amplitude + AmpGain*dt)
```

with `hehe(12) == 0.5` (same 0.5 as `hehe(9)`), `hehe(5) == 0`, `hehe(6) == 1`. **Honest reading of
`gameId % hehe(5)`:** a modulus of 0 is a divide-by-zero, and a modulus of 1 makes the branch
degenerate, so the literal there must be 2 (a parity coin flip) - which means the *same* lookup index
`hehe(5)` cannot be both 0 and 2. **Flagged: the modulus base needs live confirmation** (Sec 10.5).

Then:

```csharp
// ProjSinDesc.cs:158-190
if (num3 != 0 && 0 == 0)
{
	ComputeNewCurveLocal(dt, num2, out var x2, out var y2, num4);
	// x = x2;  y = y2 + num10;
}
else
{
	x = num2;
	y = num10;
}
```
So for the plain sine case: **`x` = along-track distance, `y` = lateral sine offset**, then the same
`Rotate(sin, cos, ...)` and LCG offsets as the base (`:191-215`). This is the exact formula for the
"wavy" projectile family and is fully reproducible offline.

#### 3.6.5 The constant-curvature arc (`ComputeNewCurveLocal`)

```csharp
// ProjDesc.cs:1390
protected void ComputeNewCurveLocal(float dt, float dist, out float x, out float y, double curveSign = 1.0)
```
```csharp
// ProjDesc.cs:1392-1437
float num  = dt - (float)CurveDelay;                                  // time since curve start
float num2 = ((CurveBreak > hehe(7)) ? Math.Min(num, (float)CurveBreak) : num);   // :1398
num10 = dist * (float)CurveDelay / dt;                                // :1413  pre-curve straight part
num6  = num - num2;                                                   // :1429
num9  = dist * num6 / dt;                                             // :1417  post-curve straight part
num8  = (float)(DegreesToRadians * NewCurve * curveSign) * num2;      // :1421  total arc angle
num4  = dist * num2 / dt;                                             // :1437  arc chord budget
// :1445-1451
if (Math.Abs(num8) < hehe(74)) { /* straight-line limit */ }
num5 = num4 * (float)(Math.Sin(num8) / (double)num8);
num7 = num4 * (float)((1 - Math.Cos(num8)) / (double)num8);
// :1454 + :1462
x = num10 + num5 + num9 * (float)Math.Cos(num8);
y = num7 + num9 * (float)Math.Sin(num8);
```
This is the classic **constant-curvature arc** parameterisation: with
`phi = NewCurve(rad) * sign * min(t, CurveBreak)`, the along/across components are `sin(phi)/phi` and
`(1-cos(phi))/phi`. Reproducible exactly. Note the `Math.Abs(num8) < hehe(74)` guard is the
small-angle branch - and `hehe(74)` is *also* the linear armour coefficient, which is a coincidence of
literal interning, **not** evidence that they are equal.

#### 3.6.6 The homing path (`Projectile.StepHoming`)

```csharp
// Projectile.cs:437
private bool StepHoming(int timeAlive, out float x, out float y)
```
Key arithmetic:

```csharp
// Projectile.cs:531
float num9 = (float)(Desc.HomingTurnRate * ProjDesc.DegreesToRadians) / hehe(70);
// Projectile.cs:534-537
int num10 = Math.Min(hehe(116), timeAlive - _homingLastMs);
float velocityAt = Desc.GetVelocityAt((float)_homingLastMs / hehe(70));
float num11 = Desc.PathHeadingAt(_homingLastMs, _angleSin, _angleCos, GameId, MathF.Atan2(_angleSin, _angleCos));
_homingHeading = num11 + _homingSteer;
// Projectile.cs:540-554
float num12 = _homingTarget.Position.X - _homingPos.X;
float num13 = _homingTarget.Position.Y - _homingPos.Y;
if (num12 * num12 + num13 * num13 >= hehe(752))
{
	float num14;
	for (num14 = MathF.Atan2(num13, num12) - _homingHeading; num14 > hehe(6); num14 -= hehe(40)) { }
	for (; num14 < hehe(71); num14 += hehe(40)) { }
	float num15 = num9 * (float)num10;
	_homingSteer += Math.Clamp(num14, 0f - num15, num15);
	_homingHeading = num11 + _homingSteer;
}
// Projectile.cs:556-558
_homingPos.X += MathF.Cos(_homingHeading) * velocityAt * ((float)num10 / hehe(70));
_homingPos.Y += MathF.Sin(_homingHeading) * velocityAt * ((float)num10 / hehe(70));
_homingLastMs += num10;
// Projectile.cs:560-561
x = _homingPos.X - _startPosition.X;
y = _homingPos.Y - _startPosition.Y;
```

where:
* `hehe(116)` is a **16 ms sub-step cap**, matching `public const int HomingPathProbeMs = 16;`
  (`ProjDesc.cs:290`).
* `hehe(70) == 1000`.
* `hehe(752)` is a squared minimum-distance threshold (the `HomingSteerDeadZone = 1.25f` constant
  implies `1.25^2 = 1.5625`, which is **not** 752 in any obvious scaling - **UNKNOWN**, and it is the
  radius below which homing stops steering; experiment in Sec 10.5).
* `hehe(6) == 1` is the `> 1` wrap threshold, `hehe(71) == -1` the `< -1` threshold, and `hehe(40)` is
  `2*PI` - so the two `for` loops are the standard shortest-angle normalisation of `num14` into
  `(-PI, PI]`. **This is exactly reproducible** and is a good sanity check on the recovered literals.

So homing is a **replicated 16 ms-capped Euler integration with a clamped turn rate**:
`steer += clamp(angleError, -turnRate*dtMs/1000, +turnRate*dtMs/1000)`, then
`pos += unit(heading+steer) * v(t) * dtMs/1000`.

The homing window and gate:
```csharp
// Projectile.cs:530
int num8 = ((Desc.HomingBreak > hehe(7)) ? ((int)((Desc.HomingDelay + Desc.HomingBreak) * hehe(24))) : hehe(112));
// Projectile.cs:347-372
private bool HomingDrives(int timeAlive)
// :349  if (Desc.HomingTurnRate != hehe(7))          -> i.e. != 0
// :357  if (HomingWireTargetId == 0) { ... }
// :371  return !((double)timeAlive >= Desc.HomingDelay * hehe(24));
```
`hehe(112)` is the "no HomingBreak" sentinel (a large ms value).

Homing target selection:
```csharp
// Projectile.cs:495-525
if ((!_homingLocked || 1 == 0) && World != null && 0 == 0)
{
	if (HitsPlayer && 0 == 0)
	{
		_homingLocked = true;
		_homingTarget = ResolveHomingWireTarget();
	}
	else
	{
		float num3 = (float)Desc.HomingRange;
		float num4 = num3 * num3;
		Enemy[] enemies = World.GetEnemies();
		for (int i = 0; i < enemies.Length; i += 1)
		{
			Enemy enemy = enemies[i];
			if (enemy != null && (!enemy.Npc || 1 == 0) && (!hits.Contains(enemy.GameId) || 1 == 0)
			    && (!enemy.HasEffect(hehe(78)) || 1 == 0) && (!enemy.HasEffect(hehe(115)) || 1 == 0))
			{
				float num5 = enemy.Position.X - _homingPos.X;
				float num6 = enemy.Position.Y - _homingPos.Y;
				float num7 = num5 * num5 + num6 * num6;
				if (num7 < num4) { num4 = num7; _homingTarget = enemy; }
			}
		}
		_homingLocked = _homingTarget != null;
	}
	_homingTargetId = _homingTarget?.GameId ?? hehe(0);
}
```
`World.GetEnemies()` is cheap (cached array, Sec 5.1). `hehe(78)`/`hehe(115)` are the
invisibility/invulnerability pair used consistently as "cannot be targeted" (cf. `GetDamageTaken` at
`Entity.cs:584` and `HitEnemy` at `:1681`). Their enum names are in `work/report-status-effects.md`;
I did not re-derive them here.

`ResolveHomingWireTarget()` (`:374-435`) prefers `World.Player` when
`World.Player.GameId == HomingWireTargetId`, otherwise scans `World.GetCharactersNoNpc()`.

#### 3.6.7 Wall bounce

```csharp
// Projectile.cs:1822
private bool StepWallBounce(int timeAlive, out float x, out float y)
```
Selected only when `Desc.WallBounce > 0` (`:1151`). The bounce logic lives in
`work/decomp/DarzaCore.Data/WallBouncePath.cs` (9220 bytes). I did **not** read its arithmetic; for a
threat model the consequence is that a bouncing projectile's future path is **not** a closed-form
function of `(t, angle, speed)` and needs either its formulas or a simulation harness. Flagged in
Sec 10.11.
### 3.7 The complete damage arithmetic for an incoming projectile

Entry point: `Entity.HitBy(Projectile proj)` (`work/decomp/DarzasDominion.Darza.Game.Objects.Entities/Entity.cs:2459`).

**Step 0 - hard immunity / early outs.**

```csharp
// Entity.cs:2461-2468
Character character = this as Character;
...
if (character != null && 0 == 0 && ((character.SpiritMode ? true : false) || character.KnockedUp))
{
	return;
}
```

**Step 1 - crit.**

```csharp
// Entity.cs:2469
int baseDamage = ((proj.CriticalHit ? true : false) ? ((int)((double)proj.Damage * proj.Desc.CriticalDamage)) : proj.Damage);
```
so **`baseDamage = CriticalHit ? Damage * Desc.CriticalDamage : Damage`**. `CriticalDamage` is used
directly as a **multiplier**, not a bonus.

The percent-damage channel is computed separately:
```csharp
// Entity.cs:2484
val2 = (int)(proj.DamagePercent * hehe(138));
// Entity.cs:2506
num2 = (int)((float)(int)Health * proj.DamagePercent);
```
`hehe(138)` is 100 (the percent scale), so `val2` is a percent floor and `num2` is
`floor(Health * DamagePercent)`. The **larger of the two is used**:
```csharp
// Entity.cs:2544-2547
if (proj.DamagePercent > hehe(5))
{
	num2 = GetDamageTaken(Math.Max(num2, val2), num4, proj.CriticalHit, out var _);
}
```
Mana mirror at `:2484`/`:2516`/`:2548`/`:2549-2552` using `player.Mana * proj.ManaDamagePercent` with
`hehe(29)` as the mana percent scale. Barrier channels are read but **deliberately not applied** here:
```csharp
// Entity.cs:2521-2524
_ = proj.BarrierDamage;
_ = Barrier;
_ = proj.BarrierDamagePercent;
_ = proj.BarrierDamagePercent;
```
(dead loads; barrier is handled generically through `CalculateBarrier` inside `GetDamageTaken`.)

**Step 2 - armor pierce.**

```csharp
// Entity.cs:2538
double num4 = proj.Desc.IgnoreArmorPercentage;
// Entity.cs:2539-2542
if (proj.LocalSeedDerived && 0 == 0 && World?.Player != null && 0 == 0)
{
	num4 = Math.Min(hehe(6), num4 + (double)World.Player.ArmorPiercing);
}
```
So: **`ignoreArmor = Desc.IgnoreArmorPercentage` (+ `World.Player.ArmorPiercing` only if this
projectile was locally spawned), clamped to at most 1** (`hehe(6) == 1`). The `LocalSeedDerived` clause
is the client's local prediction of the *player's own* armor piercing. For an **incoming monster
projectile, `num4 = Desc.IgnoreArmorPercentage` exactly.**

**Step 3 - `GetDamageTaken(baseDamage, armorPierce, criticalHit, out Color)`** (`Entity.cs:568`):

```csharp
// Entity.cs:570-598
color = Color.Red;
if (Npc) { return hehe(0); }                                        // :575-578
if (baseDamage < hehe(0)) { color = Color.Green; return baseDamage; }   // :579-583  negative = healing
if (HasEffect(hehe(22), hehe(78), hehe(115))) { return hehe(0); }   // :584-587  invulnerable
CalculateProtected(ref baseDamage);                                 // :588
num = CalculateBarrier(baseDamage);                                 // :589
if (num <= hehe(0)) { return num; }                                 // :590-593
if (criticalHit) { color = Color.Yellow; }                          // :594-597
value = GetArmor();                                                 // :598
```

then the order, recovered from the state machine's data dependencies (`:602-640`):

```
CalculateArmored(ref value);                // :603   applied to the ARMOR value
value = (int)(value * (1 - armorPierce));   // :618   hehe(6) == 1
CalculateArmorBroken(ref value);            // :622
value2 = CalculateDamage(num, value);       // :630
CalculateVulnerable(ref value2);            // :626
return Math.Max(value2, hehe(1));           // :636   hehe(1) == 1 -> minimum 1 damage
```

So the recovered order is **armored -> pierce -> armor-broken -> armour formula -> vulnerable**, with
a **minimum of 1 damage** on any non-zero, non-immune hit.

The individual steps:

```csharp
// Entity.cs:643-659   CalculateProtected
if (!HasEffect(hehe(91), out var tier)) { return; }
value2 = StatusEffectValues.GetValue(hehe(91), tier);
value = (int)((float)value * value2);
```
```csharp
// Entity.cs:661-706   CalculateBarrier
num = Math.Min(baseDamage, (int)Barrier);       // :698
if (Barrier > hehe(7)) { Barrier -= num; }      // :691, :688
val = baseDamage - num;                         // :702
return Math.Max(hehe(0), val);                  // :686
```
**Barrier absorbs `min(damage, Barrier)` and is decremented by exactly that.**
```csharp
// Entity.cs:708-724   CalculateArmored
if (!HasEffect(hehe(44), out var tier)) { return; }
num = ((this is Player) ? StatusEffectValues.GetValue(hehe(44), tier)
                        : StatusEffectValues.GetValueEnemy(hehe(44), tier));   // :721
value = (int)((float)value * num);
```
```csharp
// Entity.cs:726-731   CalculateDamage  -- THE ARMOR FORMULA
private int CalculateDamage(int baseDamage, int armor)
{
	int val = (int)((double)baseDamage * (hehe(74) - hehe(215) * (double)Math.Min(armor, hehe(113))));
	double num = hehe(69) * (double)baseDamage / (hehe(101) / Math.Max(baseDamage, hehe(6)) * (double)armor + hehe(69));
	return Math.Max(val, (int)num);
}
```

**This is the formula the task asks for, and the literals are opaque.** Reading the structure:
* line 1 is a *linear* armour reduction, `baseDamage * (A - B*min(armor, CAP))` - the `0.74 / 0.215`
  shape the task anticipated, i.e. `A ~ 0.74`, `B` some per-armour-point coefficient, `CAP` an armour
  cap.
* line 2 is a *hyperbolic* reduction, `K * baseDamage / (C/Max(baseDamage,1) * armor + K)` - the
  `K/(K + armor*C/damage)` shape, a diminishing-returns curve that only bites at low damage.
* `Math.Max(val, num)` takes the **more generous of the two reductions for the attacker**, i.e. the
  worse case for the defender.

**These five literals have since been RESOLVED** (Sec 9.4 and
`autododgeresearch/CLIENT-FINDINGS.md` Sec 2.9, from the decrypted double pool):

| index | value |
|---|---|
| `hehe(74)` | **0.7** |
| `hehe(215)` | **0.007** |
| `hehe(113)` | **100.0** |
| `hehe(69)` | **15.0** |
| `hehe(101)` | **25.0** |

so the formula is **exactly**:

```csharp
int val = (int)(baseDamage * (0.7 - 0.007 * Math.Min(armor, 100.0)));
double n = 15.0 * baseDamage / (25.0 / Math.Max(baseDamage, 1.0) * armor + 15.0);
return Math.Max(val, (int)n);      // GetDamageTaken then floors the result at 1
```

**A 0.7 base multiplier, a 0.7 %-per-armour-point linear term capped at 100 armour points, and a
hyperbolic fallback (`15·dmg / (25·armour/max(dmg,1) + 15)`), taking whichever is better for the
defender.** Sec 10.2 is therefore **closed** - and note that the literal *indices* are pool-specific:
`hehe(6) == 1.0` in the double pool (the pierce clamp) while `hehe(6)` is `3` in the int-like
`ControllerActions` mapping (`work/report-nexus.md`). Do not carry an index interpretation across
pools.

```csharp
// Entity.cs:733-749   CalculateArmorBroken
if (!HasEffect(hehe(231), out var tier)) { return; }
value2 = StatusEffectValues.GetValue(hehe(231), tier);
value = (int)((double)value * (hehe(6) - (double)value2));      // hehe(6) == 1
```
```csharp
// Entity.cs:751-767   CalculateVulnerable
if (!HasEffect(hehe(77), out var tier)) { return; }
value2 = StatusEffectValues.GetValue(hehe(77), tier);
value = (int)((double)value * (hehe(6) + (double)value2));      // hehe(6) == 1
```

`StatusEffectValues.GetValue(StatusEffect, byte tier)` and `GetValueEnemy(...)`
(`work/decomp/DarzaCore.Data/StatusEffectValues.cs`, 9982 bytes) are the tier -> multiplier tables;
the coefficients are data, not code.

**Step 4 - statuses / knockback / knock-up.** After the damage, `HitBy(Projectile)` applies the
projectile's statuses (`proj.Desc.Effects`, checked at `:2553` and `:2557`) and the knockback family.
`KnockUpDesc KnockUp` (`ProjDesc.cs:240`) drives the `Character` knock-up state
(`Character.KnockedUp` / `KnockUpStart` / `KnockUpLanding`, `Character.cs:226,111,113`), which - per
Step 0 - makes the player **unhittable** while airborne.

**Step 5 - mana shield split, display only.**
```csharp
// Entity.cs:2641-2662
private void SplitManaShieldForDisplay(ref int health, ref int mana)
// :2654
int num = ArmorTraits.ManaShieldSplit(health, player.Mana, ArmorTraits.KitManaShieldShare(player.Items), out manaSpent);
```
called at `:2566`, i.e. **after** the damage has been decided - it only moves the number between the
health and mana damage labels.

#### 3.7.1 What must be read from the live object graph to evaluate the formula

To evaluate incoming damage for the local player at frame N, a hook must read:

| value | path |
|---|---|
| raw damage for this projectile | `Projectile.Damage` (`Projectile.cs:113`) |
| crit flag | `Projectile.CriticalHit` (`:127`) |
| crit multiplier | `Projectile.Desc.CriticalDamage` (`ProjDesc.cs:270`) |
| percent-of-max-HP channel | `Projectile.DamagePercent` (`:117`) |
| armor pierce | `Projectile.Desc.IgnoreArmorPercentage` (`ProjDesc.cs:130`) |
| **armor** | `Entity.Armor` (`Entity.cs:70`) via `GetArmor()` (`Entity.cs:2334`) |
| **barrier** | `Entity.Barrier` (`Entity.cs:110`), `Entity.MaxBarrier` (`:108`) |
| **health / max health** | `Entity.Health` (`:84`), `Entity.MaxHealth` (`:98`) - both `double` |
| mana / max mana | `Entity.Mana` (`:100`), `Entity.MaxMana` (`:102`) |
| **status effect ids + tiers** | `Entity.Effects` (`Entity.cs:78`, type `StatusEffects`) |
| `protected` tier | `HasEffect(hehe(91), out tier)` -> `StatusEffectValues.GetValue(hehe(91), tier)` |
| `armored` tier | `HasEffect(hehe(44), out tier)` -> `GetValue`/`GetValueEnemy(hehe(44), tier)` |
| `armor broken` tier | `HasEffect(hehe(231), out tier)` -> `GetValue(hehe(231), tier)` |
| `vulnerable` tier | `HasEffect(hehe(77), out tier)` -> `GetValue(hehe(77), tier)` |
| immunity set | `HasEffect(hehe(22), hehe(78), hehe(115))` (`Entity.cs:584`) |
| spirit / knock-up | `Character.SpiritMode` (`Character.cs:87`), `Character.KnockedUp` (`:226`) |
| shield | `Character.ShieldTier` (`:77`), `Character.ShieldType` (`:79`) |

The **status-effect collection is a `StatusEffects : Node`**
(`work/decomp/DarzasDominion.Darza.Game.Objects.Components/StatusEffects.cs:19`) holding
`public StatusPacketData EffectData` (`:44`), where `StatusPacketData` is a **struct** with
`StatusEffectInstance[] Statuses` (`work/decomp/DarzaGameNet.Packets/StatusPacketData.cs:15`). So the
tier lookup is an array scan, not a dictionary walk - cheap. `Entity.HasEffect(StatusEffect, out byte)`
(`Entity.cs:801`) delegates to `StatusEffects.HasEffect` (`StatusEffects.cs:512`); the concrete
`ContainsEffect` implementations are at `StatusEffects.cs:67/84/100`.

### 3.8 Does the client ever receive an *updated* projectile position?

**No. Projectiles are spawn-event-only; the client re-simulates.** Evidence:

1. **The packet set has no motion packet.** The complete server->client list
   (`Get-ChildItem work/decomp/DarzaGameNet.Packets.Game.GameServer`) contains exactly these
   projectile-related packets: `GmProjectiles`, `GmAoes`, `GmAoes2`, `GmLasers`, `GmLaserRetarget`,
   `GmLasersEnd`, `GmAllyLasers`, `GmSweeps`, `GmAllySweeps`, `GmShockwave`, `GmPlayEffect`. None
   carries a per-projectile position update after the fact. `GmProjectiles` carries a **batch**
   (`GameProjectile[] Projectiles`, `GmProjectiles.cs:11`) and a batch id (`int ProjectilesId`, `:9`).
2. **The spawn struct is a spawn struct.** `GameProjectile`
   (`work/decomp/DarzaGameNet.Packets.Stats/GameProjectile.cs:8`) carries `GamePoint Position` (`:10`),
   `int OwnerId` (`:12`), `int StartId` (`:14`), `byte Index` (`:16`), `float Angle` (`:18`), the damage
   fields (`:20-31`), `byte Amount` (`:32`), `float AngleGap` (`:34`), `byte SlotIndex` (`:36`),
   `short AffixType` (`:38`), `byte TextureIndex` (`:40`), `short OwnerType` (`:42`), `SpriteIndex`
   (`:44`), `CriticalHit`/`CriticalDamage` (`:46`,`:48`), `float AngleOffset` (`:50`),
   `byte PierceCount` (`:52`), proc data (`:54-66`), `IsRicochet` (`:68`),
   `HasTargetPosition`/`TargetPosition` (`:70`,`:72`), `HasHomingTarget`/`HomingTargetId` (`:74`,`:76`).
   There is **no** velocity, no speed, no per-tick position, no delta. Position appears exactly once.
3. **The object side recomputes from `Desc` every frame with no network input.**
   `Projectile.LateUpdate` (`:1091`) computes `int num = World.ClientTime - _startTime;` (`:1109`)
   from the local clock and then `Desc.GetPosition(num, ...)` (`:1146`, `:1151`). The only network value
   it ever consults is `HomingWireTargetId` (`:1520`, `:1527`, `:357`), a target *id*, not a position.
   Compare `GmLaserRetarget`, which **does** exist for lasers (`World.cs:9735-9740`,
   `value.Retarget(result8.Angle, result8.RotateSpeed, ...)`) - projectiles have no equivalent, which
   makes the absence intentional rather than an omission in the packet list.
4. **Even the deferred-spawn queue is time-bounded, not position-synced.** Spawns whose owner is not
   yet known are parked per owner with a TTL:
   ```csharp
   // World.cs:4296
   private readonly Dictionary<int, List<hehe_E311_EE2E_F8F8_E14F_E16E>> hehe_E331_E9CE_F5E3_EBF5_EA3D = new Dictionary<int, List<hehe_E311_EE2E_F8F8_E14F_E16E>>();
   // World.cs:11030-11039
   hehe_E331_E9CE_F5E3_EBF5_EA3D[gameProjectile.OwnerId]...Add(new ... {
       hehe_EEEA_F612_F379_EF74_E3E7 = gameProjectile,
       hehe_EEEA_F612_F379_EF74_E3E8 = hehe(2),
       hehe_EEEA_F612_F379_EF74_E3E9 = ClientTime });
   // World.cs:11706
   if (clientTime - value[i].hehe_EEEA_F612_F379_EF74_E3E9 <= hehe(110)) { ... }
   ```
   `hehe(110)` is that parking TTL in ms.

**Conclusion: the authoritative state on the client for a live projectile's position is
`Projectile.Position`, which is a locally-integrated quantity. Two clients with different frame timing
will disagree slightly.** The direct, important consequence for autododge: **our predictive model and
the client's own collision test see the same positions**, because both are functions of the same
`(startPosition, angle, gameId, ClientTime, Desc)`. That is why `Desc.GetPosition` is the single most
valuable reproduction target in this document. It also means the server's only positional ground truth
for hit validation is `GmProjectilesAck` (client reports "at `Time` I was at `Position`") plus the
client's `GmHit` claim - see 3.5 and the open question in 10.1.
---

## 4. PLAYER AND WORLD STATE

### 4.1 The access chain from a static root

```
DarzasDominion.Darza.Game.World.Shared        (static World,  World.cs:4216)
  +-> .Player                                 (Player,        World.cs:4494)
        +-> .Position / .Health / .Effects / ... (inherited)
```

`ldsfld`-able statics:

| member | declaring type | type | line |
|---|---|---|---|
| `Shared` | `World` | `static World` | `World.cs:4216` |
| `Time` | `DarzaWindow` | `static ClientTime` | `DarzaWindow.cs:444` |
| `Time` | `ClientTime` | `static ClientTime` (set in ctor) | `ClientTime.cs:5` |
| `CurrentScene` | `DarzaWindow` | `static Scene` | `DarzaWindow.cs:448` |
| `MainThreadId` | `DarzaWindow` | `static int` | `DarzaWindow.cs:498` |
| `Projectiles` | `Allocations` | `static Pool<Projectile>` | `Allocations.cs:257` |
| `GameInput` | `GameScene` | instance field `GameInput` | `GameScene.cs:307` |

```csharp
// World.cs:4216
public static World Shared;
// World.cs:4494
public Player Player;
```
`World.Shared` is assigned in `World.OnAddToScene` (`World.cs:7045 Shared = this;`) and `Player = null`
in `World.ClearWorld` (`World.cs:13960`).

**Beware:** `World.Player` is `null` until the server's `GmUpdate` identifies the local object:
```csharp
// World.cs:10142
if (reference.ObjectId == MapInfo.PlayerId)
// World.cs:10154
Player = new Player(reference.ObjectId, DataManager.Data.Objects[reference.Type]);
```
Any hook must null-check `World.Shared` **and** `World.Shared.Player` every frame. There is **one**
`Player` field - no separate `LocalPlayer`. Other players are `Character`s in `World.Characters`.
The local player's own id is `World.Player.GameId` (`GameObject.cs:175`) and equals `MapInfo.PlayerId`
(`GmMapInfo.cs:18`).

### 4.2 The field list a threat model needs, with declaration sites

Most state is **inherited** from `Entity`/`Character`, which is why `Player.cs` looks sparse.

| what | declaring type | member | type | line |
|---|---|---|---|---|
| move vector (clamped) | `Player` | `SlideVector` | `Vector2` | `Player.cs:1461` |
| move vector (max) | `Player` | `MaxSlideVector` | `Vector2` | `Player.cs:1377` |
| scripted-move target / origin | `Player` | `MovingTowardsPosition` / `MovingTowardsStart` | `GamePoint` | `Player.cs:1465` / `:1467` |
| scripted-move speed / remaining / total / active | `Player` | `MovingTowardsSpeed` / `MovingTowardsTime` / `MovingTowardsStartTime` / `MovingTowards` | `float/int/int/bool` | `Player.cs:1469/1471/1473/1475` |
| player-stat increases | `Character` | `_statIncreases` (protected) | `Dictionary<PlayerStat,float>` | `Character.cs:101` |
| items (equipment) | `Character` | `Items` | `Item[]` | `Character.cs:97` |
| spirit form | `Character` | `SpiritMode` | `bool` | `Character.cs:87` |
| knock-up active | `Character` | `KnockedUp` (getter) | `bool` | `Character.cs:226` |
| knock-up points | `Character` | `KnockUpStart` / `KnockUpLanding` | `GamePoint` | `Character.cs:111` / `:113` |
| shield | `Character` | `ShieldTier` / `ShieldType` | `byte` / `short` | `Character.cs:77` / `:79` |
| **health** | `Entity` | `Health` | `double` | `Entity.cs:84` |
| **max health** | `Entity` | `MaxHealth` | `double` | `Entity.cs:98` |
| **barrier** | `Entity` | `Barrier` | `double` | `Entity.cs:110` |
| max barrier | `Entity` | `MaxBarrier` | `double` | `Entity.cs:108` |
| mana / max mana | `Entity` | `Mana` / `MaxMana` | `double` | `Entity.cs:100` / `:102` |
| **armor** | `Entity` | `Armor` | `int` | `Entity.cs:70` |
| **status effects** | `Entity` | `Effects` | `StatusEffects` | `Entity.cs:78` |
| last position (prev frame) | `Entity` | `LastPosition` | `Vector3` | `Entity.cs:92` |
| dead | `Entity` | `Dead` | `bool` | `Entity.cs:118` |
| npc flag | `Entity` | `Npc` | `bool` | `Entity.cs:114` |
| attack delay per slot | `Entity` | `_attackDelay` | `Dictionary<int,float>` | `Entity.cs:60` |
| last facing | `Entity` | `_lastFacingVector` | `Vector3` | `Entity.cs:64` |
| hp/mp/barrier percent (UI) | `Entity` | `HpPercent`/`MpPercent`/`BarrierPercent` | `float` | `Entity.cs:96/104/106` |
| current tile | `Entity` | `_currentTile` (protected) | `TileData` | `Entity.cs:62` |
| **position** | `Node` | `Position` | `Vector3` | `Node.cs:175` |
| exact/scene position | `Node` | `ExactPosition` | `Vector3` | `Node.cs:131` |
| rotation / scale | `Node` | `Rotation` / `Scale` | `float` / `Vector3` | `Node.cs:179` / `:183` |
| **game id** | `GameObject` | `GameId` | `int` | `GameObject.cs:175` |
| **collision radius** | `GameObject` | `Radius` | `float` | `GameObject.cs:271` |
| tick position | `GameObject` | `_tickPosition` | `Vector3` | `GameObject.cs:315` |
| smoothed velocity | `GameObject` | `_smoothedVelocity` | `Vector3` | `GameObject.cs:307` |
| visible velocity | `GameObject` | `CurrentVelocity` (getter) | `Vector3` | `GameObject.cs:377` |

**There is no `TilesPerSecond` field on the player.** Speed is computed per frame (Sec 6.2) from
`Player.GetStat(PlayerStat.Speed)` (`World.cs:12697`); the enum is
`work/decomp/DarzaCore.Data/PlayerStat.cs`. The *realised* speed is observable as
`GameObject.CurrentVelocity` (`GameObject.cs:377`) or from the `TPS` history
(`Player.TPS.Add(value, 2, ClientTime)`, `World.cs:12783`).

**Armor is virtual:** `protected virtual int GetArmor() { return Armor; }` (`Entity.cs:2334`). A
targeted search found no `Player` override (only `StatsType.Armor`/`PlayerStat.Armor` enum usages at
`Player.cs:4932,5157,6449,6486`), so **player armour is read through `GetArmor()` -> `Entity.Armor`**,
subject to the `_statIncreases` dictionary (`Character.cs:101`).

**Access modifiers matter for a native hook, and they are favourable:** `Position`, `Rotation`,
`Scale`, `GameId`, `Radius`, `Health`, `MaxHealth`, `Barrier`, `Mana`, `Armor`, `Effects`, `Dead`,
`Npc`, `LastPosition`, `SpiritMode`, `KnockedUp`, `Items` are all `public`. Only `_statIncreases`,
`_knockUp*`, `_attackDelay` (public on `Entity`), and the `Character` spirit-anim temporaries are
`protected`/`private`. So **almost everything the damage model needs is `public`** - nothing forces a
private-field offset calculation.

### 4.3 Units and scale

**`Position` is in TILES, not pixels.** Evidence:

* The tile map is indexed by integer tile coordinates: `Tilemap.GetTile(gmMove.Position)`
  (`World.cs:9998`), `Tilemap.GetTile(result2.X, result2.Y)` (`:9669`), and `World.AddObject` indexes
  its collision grids with `[(int)obj.Position.X, (int)obj.Position.Y]` (`World.cs:14598,14602`).
* Rendering multiplies by the camera zoom to get pixels:
  ```csharp
  // Tilemap.cs:1632
  position = focusBase + Node.RotateXYOrigin(new Vector3((float)(p.X - focus.X) * _exactScale.X, (float)(p.Y - focus.Y) * _exactScale.Y, hehe(5)), _sin, _cos);
  ```
  ```csharp
  // World.cs:6374-6375
  float num  = (float)DarzaWindow.Width  / (hehe(7) * BaseNodeScale);
  float num2 = (float)DarzaWindow.Height / (hehe(7) * BaseNodeScale);
  ```
  So screen-pixels = tiles * `_exactScale` = tiles * `BaseNodeScale`, and `BaseNodeScale` is the
  runtime zoom (`World.UpdateScale`, `World.cs:6144`; assigned at `:6150-6156` from
  `Prefs.auto_scale_world` / `ForceScale`).
* `Node.RotateXYOrigin(v, sin, cos)` is a pure 2-D rotation of `v`, used identically to
  `ProjDesc.Rotate`.
* Collision is in tiles: `DistanceSquared` compares tile-space positions against `Radius` sums, and a
  radius of `0.5` (the inferred `hehe(16)`, Sec 9.4) is a plausible half-tile body.

**There is no fixed "tile size in pixels" constant in the game.** The tile->pixel factor *is* the
runtime camera zoom. `Tilemap.TileUvSize = 1f / 64f`
(`work/decomp/DarzasDominion.Darza.Game.Objects/Tilemap.cs:143`) is a **texture-atlas UV** constant
(64 tiles per atlas row/column), not a pixel size. For a hook this is a non-issue: all of the game's
own collision, movement and projectile math is in tiles, so the threat model should stay in tiles.

### 4.4 Map dimensions

```csharp
// work/decomp/DarzasDominion.Darza.Game.Objects/Tilemap.cs:101
public int Height;
// Tilemap.cs:103
public int Width;
// Tilemap.cs:208
Width = width;
```
mirroring the server's map info:
```csharp
// work/decomp/DarzaGameNet.Packets.Game.GameServer/GmMapInfo.cs:14 / :16 / :18
public int Width;
public int Height;
public int PlayerId;
```
`World.Tilemap` is the live instance (`Tilemap.GetTile(...)`, `Tilemap.GetFocus()`, `World.cs:9669,9998,6400`).
Origin is `(0,0)` at the map corner; positions are clamped to `[0,Width) x [0,Height)`. `Tilemap` keeps
`FocusX`/`FocusY` as statics (`World.cs:9640-9641`) with a `GetFocus()` accessor.

Other map state a hook may want (`GmMapInfo.cs`): `CanTeleport` (`:20`), `Difficulty` (`:32`),
`GameTime` (`:50`), `IsSoloRun` (`:56`), `DamageSeed` (`:58`), `SwapWindowSeconds` (`:60`).
`DamageSeed` is notable - damage rolls are seeded - but the roll site was not located; see Sec 10.10.
---

## 5. PROJECTILE / ENTITY REGISTRY

### 5.1 The three containers on `World`

```csharp
// World.cs:4739  public Dictionary<int, GameObject> Objects;
// World.cs:4741  public Dictionary<int, Entity> Characters;
// World.cs:4743  public Dictionary<int, Enemy> Enemies;
// World.cs:4745  public List<ActivatableObject> Activatables;
// World.cs:4747  public List<IContainer> Containers;
// World.cs:4749  public List<GameObject> ContainerPreviewObjects;
// World.cs:4508  public List<GameObject> EventQuests = new List<GameObject>();
```
**All are public instance fields keyed by `GameId`.** Registration, in `World.AddObject`:

```csharp
// World.cs:14329  public void AddObject(GameObject obj)
// World.cs:14577  Objects[obj.GameId] = obj;
// World.cs:14617  Characters[enemy.GameId] = enemy;     // when enemy.Npc
// World.cs:14621  Enemies[enemy.GameId] = enemy;        // when !enemy.Npc
// World.cs:14626  Characters[character.GameId] = character;
```

**How an enemy is distinguished from an ally/player - exactly:**

```csharp
// World.cs:14578-14622
if (!(obj is Player) || 1 == 0)
{
	if (!(obj is Character character) || 1 == 0)
	{
		if (!(obj is Enemy enemy) || 1 == 0) { ... }
		else if (enemy.Npc && 0 == 0)
		{
			Characters[enemy.GameId] = enemy;
		}
		else
		{
			Enemies[enemy.GameId] = enemy;
		}
	}
	else
	{
		Characters[character.GameId] = character;
	}
}
```

The discriminator is **runtime type plus, for `Enemy`, the `Npc` flag** (`Entity.Npc`,
`Entity.cs:114`):

| runtime type | `Npc` | lands in | meaning |
|---|---|---|---|
| `Player` | - | `Objects` only | the local player (exactly one) |
| `Character` (not `Player`) | - | `Objects` + `Characters` | other players / allies |
| `Enemy` | `true` | `Objects` + `Characters` | friendly/neutral NPC |
| `Enemy` | `false` | `Objects` + `Enemies` | **hostile monster** |

Cached array accessors (each memoises into a private field, invalidated per frame at `:9483-9488` and
by `ClearWorld` at `:13938-13942`):

```csharp
// World.cs:15230  public Entity[] GetCharacters()        -> Characters.Values.ToArray()  (hehe_..._EA42)
// World.cs:15245  public Entity[] GetCharactersNoNpc()   -> filters Npc               (hehe_..._EA43)
// World.cs:15298  public Entity[] GetNpcs()
// World.cs:15383  public Enemy[]  GetEnemies()           -> Enemies.Values.ToArray()   (hehe_..._EA4F)
// World.cs:15398  public GameObject GetGameObject(int id)  -> Objects.TryGetValue
// World.cs:15407  public GameObject[] GetGameObjects()   -> Objects.Values.ToArray()    (hehe_..._EA63)
```
```csharp
// World.cs:9483-9488   (per-frame invalidation)
hehe_E331_E9CE_F5E3_EBF5_EA42 = null;
hehe_E331_E9CE_F5E3_EBF5_EA43 = null;
hehe_E331_E9CE_F5E3_EBF5_EA62 = null;
hehe_E331_E9CE_F5E3_EBF5_EA4F = null;
hehe_E331_E9CE_F5E3_EBF5_EA63 = null;
hehe_E331_E9CE_F5E3_EBF5_EA44 = false;
```
**These accessors are safe and cheap to call from a per-frame hook**, and they are what the game's own
collision code uses (`Projectile.cs:1606`, `:1211`, `:394`, `:506`).

### 5.2 Where the live projectiles are

**There is no `List<Projectile>` and no projectile dictionary on `World`.** A projectile is an ordinary
node: `AddObject` puts it in `Objects` and into a **layer**:

```csharp
// World.cs:14331-14346
if (obj is StaticObjectFlat) { ... }
if (obj is EnemyFlat && 0 == 0) { ... }
...
GameObjectLayer.AddChild(obj);
// World.cs:14382
FlatGroundLayer.AddChild(obj);
```
Lasers and sweeps are also put there explicitly: `GameObjectLayer.AddChild(laser2)`
(`World.cs:11135`, `:11195`, `:11283`), `GameObjectLayer.AddChild(sweep)` (`:11364`, `:11443`, `:11497`).

So the **live projectile set is `World.Objects` (filtered by `is Projectile`) or
`World.GameObjectLayer.Children` (filtered)**, and the game itself iterates `Objects.Values`:

```csharp
// World.cs:6698
using (Dictionary<int, GameObject>.ValueCollection.Enumerator enumerator3 = Objects.Values.GetEnumerator())
// World.cs:8649
using Dictionary<int, GameObject>.ValueCollection.Enumerator enumerator = Objects.Values.GetEnumerator();
// World.cs:13946
using (Dictionary<int, GameObject>.ValueCollection.Enumerator enumerator = Objects.Values.GetEnumerator())
```

**Recommended read path for a hook, cheapest first:**
1. `World.Shared.Player` - one dereference.
2. `World.Shared.GetEnemies()` - cached array (for `HitEnemy`-side reasoning and homing targets).
3. For **incoming projectiles**: `World.Shared.Objects` and test `obj is Projectile`. Cost is
   O(objects) with a managed type check per entry. **Which layer a projectile lands in is
   `UNKNOWN - needs live measurement`** (the `AddObject` branch at `:14331-14382` was not fully
   decoded); if projectiles all live in `GameObjectLayer`, iterating that list is cheaper. Sec 10.6.
4. `Allocations.Projectiles` is **not** a live list - it is a free-list `Pool<Projectile>`
   (`Allocations.cs:257`), i.e. recycled instances only. **Do not enumerate it as "live projectiles".**

`Object3d`, `StaticObject` and `StaticObjectFlat` share `Objects` with projectiles, so a
`Projectile`-only filter is required; there is no `ObjectsByType`. `World.RemoveObject` (removes from
`Enemies` at `:14838`) and `World.ClearWorld` (`:13928`) are the two events that invalidate everything;
`ClearWorld` clears `Objects`, `Characters`, `Enemies`, `FlatGroundLayer`, `GameObjectLayer`,
`AoeObjectLayer`, ... and nulls `Player` at `:13960`.

### 5.3 Pooling - does it affect a hook?

`PoolSharp` is referenced from `Projectile.cs:22`. The decompiled concrete type is
`Telepathy.Pool<T>` (`work/decomp/Telepathy/Pool.cs:6`), a **LIFO stack wrapper**:

```csharp
// Pool.cs:8    private readonly Stack<T> hehe_E92A_EEF8_E406_E7E7_EEA7 = new Stack<T>();
// Pool.cs:17-24
public T Take()
{
	if (hehe_E92A_EEF8_E406_E7E7_EEA7.Count <= hehe(0)) { return hehe_E92A_EEF8_E406_E7E7_EEA8(); }  // allocate
	return hehe_E92A_EEF8_E406_E7E7_EEA7.Pop();                                                     // reuse
}
// Pool.cs:26-29
public void Return(T item) { hehe_E92A_EEF8_E406_E7E7_EEA7.Push(item); }
```

The pools are `Allocations` statics created in its static ctor:
```csharp
// Allocations.cs:253-259
public static Pool<StaticObject> StaticObjects;
public static Pool<Enemy> Enemies;
public static Pool<Projectile> Projectiles;
public static Pool<Character> Characters;
public static Pool<Object3d> Object3ds;
// Allocations.cs:357
Projectiles = new Pool<Projectile>(hehe_ED88_E910_F4CE_F2A4_E0D5(func3, num3, action3));
```
The generator/action pair scrubs the instance on take/return:
```csharp
// Allocations.cs:102-109   (the Projectile generator)
internal Projectile hehe_F010_EDF4_E83A_F53C_E5A5(IPool<Projectile> _ECF0_F129_ED83_ECC8_F588)
{
	...
	prj.Reset();
```
and `Projectile.Reset()` (`Projectile.cs:676`) clears the state:
```csharp
// Projectile.cs:690-698
Initialized = false;
_owner = null;
_ownerId = hehe(0);
_deathTimeAlive = hehe(0);
_homingTarget = null;
_homingTargetId = hehe(0);
_homingSteer = hehe(5);
_homingInit = false;
_homingLocked = false;
HomingWireTargetId = hehe(0);
```
`Projectile.Rent(GameObjectData data)` (`:574`) is the pool entry point:
```csharp
// Projectile.cs:576-600
Pool<Projectile> projectiles = Allocations.Projectiles;
...
obj = projectiles.Take();
...
if (obj == null || 1 == 0) { obj = new Projectile(); }
((GameObject)obj).Set(data);
```
(`Projectile.cs:796 Allocations.Projectiles?.Add(this);` is a `PoolSharp.IPool<T>` overload artefact -
do not over-read it.)

**What this means for a hook:**

1. **A `Projectile` instance can in principle be resurrected and reused**, so a raw pointer captured on
   frame N may be a *different* projectile later. **Always re-validate**, in this order:
   `p != null` -> `p.World != null` -> `p.Initialized` -> `Objects.TryGetValue(p.GameId) == p`. The
   `World` check is cheapest and is exactly what the client relies on (`Remove()` sets `World = null`,
   `Projectile.cs:1079`). `Initialized` is `public bool` on `GameObject` (`GameObject.cs:207`).
2. **Ordinary despawn does not return to the pool.** `Projectile.Remove()` (`:1055-1089`) does
   `RemoveFromParent()` (`:1057`), `World = null` (`:1079`), `RemovedFromWorld(World)` (`:1083`), and a
   whole-tree grep finds **no `Allocations.Projectiles.Return(...)` call site at all**. So the pool is
   only drained by `Rent()` and never refilled on expiry, meaning `Take()` almost always allocates
   fresh. **The reuse hazard is therefore theoretical for projectiles - but do not rely on it**, since
   `Characters`/`Enemies`/`StaticObjects` are fed by the same `Allocations` statics and *are* recycled
   elsewhere. Treat every entity pointer as frame-local.
---

## 6. INPUT AND MOVEMENT

### 6.1 The input state

`GameInput` is `public abstract class GameInput : Node`
(`work/decomp/DarzasDominion.Darza.Game.UI/GameInput.cs:8`) with a tiny, fully readable field list:

```csharp
// GameInput.cs:10-26
public bool Shooting;
public bool AutoShooting;
public bool Moving;
public bool Escape;
public bool Activate;
public bool ShowBags;
public Angle ShootAngle;
public Angle MoveAngle;
public float MoveSpeed;
```

The desktop implementation is `work/decomp/DarzasDominion.Darza.Game.UI.Desktop/DesktopInput.cs`
(1103 lines). Direction comes from four held keys summed into an `IntPoint`, or from the left stick:

```csharp
// DesktopInput.cs:362
IntPoint intPoint = new IntPoint(hehe(0), hehe(0));
// DesktopInput.cs:365-378   (each held key adds/subtracts 1 on an axis)
intPoint.Y += hehe(1);   // up
intPoint.Y -= hehe(1);   // down
intPoint.X -= hehe(1);   // left
intPoint.X += hehe(1);   // right
// DesktopInput.cs:379
Moving = (((intPoint.X != 0) ? true : false) ? true : ((uint)intPoint.Y > (uint)hehe(0)));
// DesktopInput.cs:382-383
MoveAngle = new Angle(intPoint);
MoveSpeed = ((hehe_E331_E9CE_F5E3_EBF5_F416.Held(base.Scene) ? true : false) ? Prefs.crouch_speed : hehe(2));
// DesktopInput.cs:387-398   (controller)
Vector2 vector2 = ControllerInputState.LeftStick(Prefs.LoadOption(hehe(59)).GetValue<float>());
if (vector2.LengthSquared() > hehe(5))
{
	Moving = true;
	MoveAngle = new Angle(new GamePoint(vector2.X, vector2.Y));
	float num = vector2.Length();
	if (num > hehe(2)) { num = hehe(2); }
	MoveSpeed = num;
}
// DesktopInput.cs:405-408   (escape)
if ((hehe_E331_E9CE_F5E3_EBF5_E407.Down(base.Scene) ? true : false) || hehe_E18C_F72D_EE64_F88F_F5B7((ControllerActions)hehe(6)))
{
	Escape = true;
}
```
`hehe(2) == 1` (the "full speed" multiplier) and `hehe(5) == 0` (the dead-zone threshold). Note
`hehe(6)` is `ControllerActions.Nexus == 3` per the pre-existing `work/report-nexus.md` mapping - so
the gamepad escape binding is the same code path as a keyboard escape.

**So the input->direction mapping is: a `GamePoint` direction in `GameInput.MoveAngle`, a scalar
throttle in `GameInput.MoveSpeed` (1.0 for keyboard, stick magnitude for gamepad), and the booleans
`Moving`/`Shooting`/`AutoShooting`/`Escape`.** A hook that wants to steer the player does **not** need
to fake input: it can write `GameInput.Moving = true; GameInput.MoveAngle = new Angle(dir);
GameInput.MoveSpeed = 1f;` and the client's own integrator consumes it next frame. That is the **least
invasive** steering method available, and it goes through the same code path as a human, so it
produces identical `GmMove` packets.

`GameInput` is reached via `GameScene.GameInput` (`work/decomp/DarzasDominion.Darza.Scenes/GameScene.cs:307
public GameInput GameInput;`), and the client itself uses exactly that path from `World`:

```csharp
// World.cs:12690-12692
bool moving = GameScene.GameInput.Moving;
float moveSpeed = GameScene.GameInput.MoveSpeed;
Angle angle = ((moving ? true : false) ? GameScene.GameInput.MoveAngle.AddRadians(0f - WorldRotation + num) : new Angle(new GamePoint(Player.SlideVector.X, Player.SlideVector.Y)));
```

### 6.2 `PlayerFunctions.TilesPerSecond` - the whole law

```csharp
// work/decomp/DarzaGameNet/PlayerFunctions.cs:459
public static double TilesPerSecond(short tileType, double speed, byte slow, byte speedy, bool paralyzed, bool fearOfTheBull, bool grounded, bool cutscene)
```

Recovered body, in order:

```csharp
// PlayerFunctions.cs:461-471
if (paralyzed || cutscene) { return hehe(7); }                             // 0
// :472-475
if (fearOfTheBull && 0 == 0 && speed > hehe(18)) { speed = hehe(18); }     // clamp to the fear cap
// :476-479
if (grounded && 0 == 0 && Array.IndexOf(GroundedSafeTiles, tileType) == -1) { speed /= hehe(14); }
// :480
speed += hehe(19);
// :492 / :498
value2 = StatusEffectValues.GetValue((StatusEffect)hehe(75), speedy);      // speedy tier
value  = StatusEffectValues.GetValue((StatusEffect)hehe(42), slow);        // slow tier
// :502
num2 = hehe(20) + hehe(21) * (speed / hehe(16));
// :510-523
if (slow   > hehe(0)) { num2 *= (double)value; }
if (speedy > hehe(0)) { num2 *= (double)value2; }
// :532
return num2;
```

i.e. **`tilesPerSecond = (A + B * ((speed + C) / D)) * slowMult(tier) * speedyMult(tier)`**, with an
early `return 0` while paralysed or in a cutscene, an optional clamp on `speed` for `fearOfTheBull`,
and a `grounded` penalty outside `GroundedSafeTiles`. The literals `A = hehe(20)`, `B = hehe(21)`,
`C = hehe(19)`, `D = hehe(16)`, plus the fear cap `hehe(18)` and the grounded divisor `hehe(14)`, are
**the movement constants and they are opaque**. `hehe(16)` is repeatedly the same literal index used
for the `0.5` radius factor, which is a hint it is *not* 0.5 here (a divisor of 0.5 would double
`speed`) - but this is **UNKNOWN - needs live measurement** (Sec 10.3).

### 6.3 The per-frame movement integration

The method is private on `World` and its name is a decompiler hash:

```csharp
// World.cs:12666
private void hehe_EB2C_E5A5_EE82_E9C5_EF68(float _ECF0_F129_ED83_ECC8_F588, float _ECF0_F129_ED83_ECC8_F588)
```
Two `float` parameters, both named identically by the obfuscator. Called once with `(logicDt, frameDt)`:
```csharp
// World.cs:9565-9567
if (time.LogicElapsedSec > hehe(5))
{
	hehe_EB2C_E5A5_EE82_E9C5_EF68(time.LogicElapsedSec, time.ElapsedSec);
}
```

The body, in order:

```csharp
// World.cs:12668
Character player = Player;
// World.cs:12671-12682   (gate: dead / spirit-transfer / spirit with 0 hp -> stop all input)
bool flag  = player?.SpiritMode ?? false;
bool flag2 = hehe_EB2C_E5A5_EE82_E9C5_EF76();
if (PlayerDead || PreparingSpiritTransferToBeyond || ((!flag || 1 == 0) && Player.Health <= hehe(0) && (!flag2 || 1 == 0)))
{
	GameScene.GameInput.Moving = false;
	GameScene.GameInput.Shooting = false;
	GameScene.GameInput.AutoShooting = false;
	Player.Attacking = false;
	Player.JustStartedAttacking = false;
	Player.StopMovingTowards();
	Player.UpdateMoveVector(Vector3.Zero, _ECF0_F129_ED83_ECC8_F588);
}
// World.cs:12683-12686
else if (Player.KnockedUp && 0 == 0) { hehe_EB2C_E5A5_EE82_E9C5_EF6A(_ECF0_F129_ED83_ECC8_F588); }
// World.cs:12687   the normal branch
else if ((GameScene.GameInput.Moving ? true : false) || (Player.Sliding() ? true : false) || Player.MovingTowards)
```

The core, with the speed law applied:

```csharp
// World.cs:12689
float num = ((Player.HasEffect(hehe(160)) ? true : false) ? hehe(278) : hehe(5));   // aim/strafe offset
// World.cs:12693-12697
float num2 = (float)(hehe_E331_E9CE_F5E3_EBF5_EA4B?.Speed ?? hehe(6));     // current tile speed multiplier
int tileType = ((int?)hehe_E331_E9CE_F5E3_EBF5_EA4B?.Type) ?? hehe(0);
Player.Effects.HasEffect((StatusEffect)hehe(42), out var tier);             // slow tier
Player.Effects.HasEffect((StatusEffect)hehe(75), out var tier2);            // speedy tier
float num3 = (float)PlayerFunctions.TilesPerSecond((short)tileType, Player.GetStat((PlayerStat)hehe(0)), tier, tier2,
    Player.HasEffect(hehe(40)), Player.HasEffect(hehe(107)), Player.HasEffect(hehe(84)), Player.HasEffect(hehe(115))) * num2;
```
So the eight `TilesPerSecond` arguments are, in order: `(short)tileType`, `GetStat(Speed)`, `slowTier`,
`speedyTier`, `HasEffect(paralyzed)`, `HasEffect(fearOfTheBull)`, `HasEffect(grounded)`,
`HasEffect(cutscene)`.

```csharp
// World.cs:12710
gamePoint = angle.Position * num3 * _ECF0_F129_ED83_ECC8_F588 * moveSpeed;
// World.cs:12714
vector = Player.GetMoveVector(new Vector3(gamePoint.X, gamePoint.Y, hehe(5)), moving, _ECF0_F129_ED83_ECC8_F588);
```
i.e. **`delta = unit(angle) * tilesPerSecond * dt * moveSpeed`** - a pure `p += v*dt`, with collision
resolution hidden inside `Player.GetMoveVector` (whose slide accumulation is at
`Player.cs:4698-4765`). Then, for scripted moves:

```csharp
// World.cs:12733-12751
if (Player.MovingTowards && 0 == 0)
{
	float num5 = NodeAction.ModifyStep((float)Player.MovingTowardsTime / (float)Player.MovingTowardsStartTime, (StepModifier)hehe(5)) * Player.MovingTowardsSpeed;
	GamePoint gamePoint2 = new Angle(Player.MovingTowardsPosition - Player.MovingTowardsStart).Position * num5 * _ECF0_F129_ED83_ECC8_F588;
	vector = Player.GetMoveVector(new Vector3(gamePoint2.X, gamePoint2.Y, hehe(5)), false, _ECF0_F129_ED83_ECC8_F588);
	float length = (Player.MovingTowardsPosition - Player.GamePoint).Length;
	float num6 = vector.Length();
	if (num6 > length) { vector *= length / num6; }
	Player.MovingTowardsTime -= (int)(_ECF0_F129_ED83_ECC8_F588 * hehe(70));
	if (Player.MovingTowardsTime <= hehe(0))
	{
		Player.MovingTowards = false;
		Player.WasMoving = true;
		flag3 = true;
	}
}
```

### 6.4 Where a new position is committed, and where the outbound packet is emitted

**Commit:**
```csharp
// World.cs:12769
hehe_EB2C_E5A5_EE82_E9C5_EF69(new GamePoint(vector2.X, vector2.Y));
// World.cs:12770
Player.UpdateMoveVector(vector, _ECF0_F129_ED83_ECC8_F588);
// World.cs:12771-12781
Vector3 position;
if (Manipulating != null && 0 == 0)
{
	position = Manipulating.Position;
	Manipulating.Position = vector2;
}
else
{
	position = Player.Position;
	Player.Position = vector2;
}
// World.cs:12782-12783
float value = XNAUtils.VectorLengthIgnoreZ(vector2 - position);
Player.TPS.Add(value, hehe(2), ClientTime);
```
So **`Player.Position = vector2` at `World.cs:12780` is the single place the local player's position is
written this frame.** `vector2` comes from `hehe_EB2C_E5A5_EE82_E9C5_EF73(...)` (`:12756`), the
collision-resolving integrator, and `Player.GetMoveVector` (`Player.cs:4572-4765`) handles sliding.
`hehe_EB2C_E5A5_EE82_E9C5_EF69(...)` (`:12857`) is the **position-history sampler**: it accumulates
moved distance into `hehe_E331_E9CE_F5E3_EBF5_EA71` and, once a step threshold (`hehe(45)`) is
exceeded, appends to the history list:
```csharp
// World.cs:12859-12887
GamePoint gamePoint = _ECF0_F129_ED83_ECC8_F588 - hehe_E331_E9CE_F5E3_EBF5_EA71;
...
if (!(length >= hehe(45))) { return; }
float num = length / hehe(45);
...
hehe_E331_E9CE_F5E3_EBF5_EA71 += gamePoint2;
...
hehe_E331_E9CE_F5E3_EBF5_EA24.Add(hehe_E331_E9CE_F5E3_EBF5_EA71);
```

**Outbound:**
```csharp
// World.cs:9969
private void hehe_EB2C_E5A5_EE82_E9C5_EF51()
// World.cs:9982-9993
GamePoint gamePoint = Player.GamePoint;
if (Manipulating != null) { gamePoint = Manipulating.GamePoint; }
gmMove = new GmMove
{
	Time = ClientTime,
	Position = gamePoint,
	History = hehe_EB2C_E5A5_EE82_E9C5_EF50(),
	HistoryCount = hehe_E331_E9CE_F5E3_EBF5_EA24.Count
};
// World.cs:9998
hehe_E331_E9CE_F5E3_EBF5_EA4B = Tilemap.GetTile(gmMove.Position);
// World.cs:10007
hehe_E331_E9CE_F5E3_EBF5_EA71 = gmMove.Position;
// World.cs:10011
hehe_E331_E9CE_F5E3_EBF5_EA24.Clear();
// World.cs:10015
Client.SendPacket(gmMove);
```

**So the outbound position is `GmMove`, sent from `World.cs:10015` inside
`World.hehe_EB2C_E5A5_EE82_E9C5_EF51()`, at most once per frame**, and it is sent from the packet-drain
block of `World.Update`:
```csharp
// World.cs:9683-9686
if (SendPacket && 0 == 0)
{
	hehe_EB2C_E5A5_EE82_E9C5_EF51();
}
```
i.e. **`World.Update` emits `GmMove` before applying this frame's `GmUpdate`s.** The packet carries the
position, the accumulated `History`, and `Time = World.ClientTime`.

### 6.5 Could a hook write position/direction directly, and what fights it?

| write | effect | what fights it |
|---|---|---|
| `GameInput.MoveAngle` / `MoveSpeed` / `Moving` | **cleanest.** Consumed next frame at `World.cs:12692,12710`; identical to human input | nothing - it *is* the input path. But it is one-frame-ahead: the write must land **before** `World.Update`'s call at `:9567` |
| `Player.Position = v` | instant teleport for this frame's collision and rendering | the next frame's integrator recomputes `vector2` from `Player.Position` (`:12779 position = Player.Position;` then `:12780 Player.Position = vector2;`), so a teleport persists only until the next movement branch runs - and only if it runs at all (`:12687` requires `Moving \|\| Sliding() \|\| MovingTowards`). It also desyncs `Player.Position` from the history list `hehe_E331_E9CE_F5E3_EBF5_EA24`, while the server validates against `GmMove.History` + `Position` + `Time`; a per-step jump larger than `hehe(45)` will look like a speed hack |
| `Player.SlideVector` / `MaxSlideVector` | changes the direction the "not moving" branch uses (`:12692`) - a legitimate-looking way to keep drifting | overwritten by `GetMoveVector` accumulation (`Player.cs:4698-4765`) |
| `Player.MovingTowards*` + `MovingTowards = true` | the **scripted-move** path (`:12733-12751`): the client walks toward a point at a controlled speed with arrival clamping | needs 6 fields set consistently; the branch is gated on `Player.Sliding() \|\| MovingTowards` at `:12687` and self-cancels at `:12745-12749`. This is the right path for a *dodge step* if you want the client to do the pathing |
| calling the movement method directly | immediate move | it is `private` (`World.cs:12666`) and takes `(logicDt, frameDt)`; callable only via a resolved `MethodDesc` |

**Remaining fights to design around:**
* `Player.StopMovingTowards()` (`Player.cs:4630`) is called from the dead/spirit gate (`:12680`) and on
  arrival (`:12786`), so a scripted move can be cancelled by state the hook did not observe.
* **`World.RequestEscape()`'s cast branch cancels on movement:**
  ```csharp
  // World.cs:13043-13047
  if (GameScene.GameInput.Moving && 0 == 0) { CancelEscapeCast(); return; }
  ```
  So an autododge that sets `GameInput.Moving = true` **will cancel an in-progress nexus cast**, in the
  same frame or the next. This is a real interaction bug to design around.

  **SCOPE CORRECTION (operator-confirmed).** This branch is **not normal escape**. Ordinary escape
  takes the instant path (the `GmEscape` send at `:13011`) and is **never** cancelled by movement. The
  channelled path is reached only when the character has the **`Impermanence` totem**, i.e. in a
  challenge run — the same `Player.Totems.Has(...)` condition the method itself tests at `:13009`. So
  the hazard is real but conditional, and the hook can read the same condition to know which mode it
  is in. See [`CLIENT-FINDINGS.md`](CLIENT-FINDINGS.md) §5.3 and [`RISKS.md`](RISKS.md) §3d.
* While `Player.KnockedUp`, the whole normal movement branch is bypassed in favour of
  `hehe_EB2C_E5A5_EE82_E9C5_EF6A(...)` (`:12683-12686`) - a hook cannot steer during knock-up.
---

## 7. THE ESCAPE PATH

### 7.1 `World.RequestEscape()`

```csharp
// World.cs:12984
public void RequestEscape()
```
`public void`, on `World`, **no parameters, no return** - the single easiest thing in this document to
call directly from a native hook (given a `World*`).

Its logic in order:

```csharp
// World.cs:12987
if (hehe_E331_E9CE_F5E3_EBF5_EAF6 != DateTime.MinValue)
// World.cs:12995
num = ((((!((DateTime.UtcNow - hehe_E331_E9CE_F5E3_EBF5_EAF6).TotalSeconds <= hehe(69))) ? 1 : 0) == hehe(0)) ? 1 : 0);
// World.cs:13008
bool flag = (byte)num != 0;
// World.cs:13009
if (((((uint)((Player == null || false || (PlayerDead ? true : false)) ? hehe(1) : (SafeWorld ? 1 : 0)) | (flag ? 1u : 0u)) != 0) ? true : false) || (hehe_E331_E9CE_F5E3_EBF5_EA4A ? true : false) || !Player.Totems.Has((CharacterTotem)hehe(95)) || 1 == 0)
{
	// World.cs:13011   THE ESCAPE EMISSION
	Client.SendPacket(new GmEscape());
	return;
}
// otherwise: a cast-time branch (Impermanence totem)
// World.cs:13014-13021
if (hehe_E331_E9CE_F5E3_EBF5_EAF7 && 0 == 0) { CancelEscapeCast(); return; }
hehe_E331_E9CE_F5E3_EBF5_EAF7 = true;
hehe_E331_E9CE_F5E3_EBF5_EAF8 = DateTime.UtcNow.AddSeconds(hehe(21));
hehe_EB2C_E5A5_EE82_E9C5_EF6C()?.ShowEscapeCast(hehe(64), hehe(64));
goto IL_0137;
// World.cs:13002-13005   IL_0137
Client.SendPacket(new GmEscapeCastState { Casting = true });
```

**The exact emission point is `World.cs:13011`: `Client.SendPacket(new GmEscape());`** - reached when
any of:
* `Player == null`, `PlayerDead`, or `SafeWorld` (the inverted-looking `hehe(1)` branch means "send
  now"), or
* a cooldown has elapsed (`flag`), or
* the safe-area flag `hehe_E331_E9CE_F5E3_EBF5_EA4A` is set (written from `GmSafeAreaState`:
  `World.cs:20201 hehe_E331_E9CE_F5E3_EBF5_EA4A = ((GmSafeAreaState)...).Safe;`), or
* **the player does not have the Impermanence totem** (`!Player.Totems.Has((CharacterTotem)hehe(95))`;
  `hehe(95) == CharacterTotem.Impermanence == 0x20 == 32` per the pre-existing `work/report-nexus.md`).

Otherwise the client enters a **cast** (`hehe_E331_E9CE_F5E3_EBF5_EAF7 = true`, deadline
`DateTime.UtcNow.AddSeconds(hehe(21))`) and sends `GmEscapeCastState { Casting = true }` while it waits,
which is then cancelled or completed in `hehe_EB2C_E5A5_EE82_E9C5_EF6B()` (`World.cs:13025`) when the
timer elapses. The cooldown guard at `:12995` uses `hehe(69)`, and the cast duration `hehe(21)`; both
values are **UNKNOWN - needs live measurement**, though their roles are clear (Sec 10.2/10.15).

### 7.2 The `GameInput.Escape` check and the frame-loop point

Inside the big movement/attack function, near its end:

```csharp
// World.cs:12837
hehe_EB2C_E5A5_EE82_E9C5_EF6B();
// World.cs:12838-12842
if (!GameScene.GameInput.Escape)
{
	return;
}
GameScene.GameInput.Escape = false;
// World.cs:12843-12846
if (!DeathMenu.Opened)
{
	goto IL_0a07;
}
// World.cs:12854   IL_0a07
RequestEscape();
```

So the full chain is:

```
DesktopInput (per frame, in GameScene.UpdateScene before World.Update)
  sets GameInput.Escape = true                                    DesktopInput.cs:407
  -> World.Update                                                 World.cs:9473
     -> hehe_EB2C_E5A5_EE82_E9C5_EF68(logicDt, frameDt)           World.cs:9567
        -> if (GameInput.Escape) { GameInput.Escape = false; RequestEscape(); }   World.cs:12838-12854
           -> Client.SendPacket(new GmEscape())                   World.cs:13011
              -> Client.hehe_EFF0_ED69_F541_E66F_F1F9(packet)     Client.cs:621 / :624
                 -> Telepathy?.Send(dataScratch)                  Client.cs:640
                    -> TelepathyClient.Send(ArraySegment<byte>)   Telepathy/TelepathyClient.cs:292
                       -> sendPipe.Enqueue(message)               TelepathyClient.cs:318
                          + sendPending.Set()                     TelepathyClient.cs:314
                             -> [send thread] MagnificentSendPipe.DequeueAndSerializeAll(...)
                                                                  MagnificentSendPipe.cs:131
                                -> Utils.IntToBytesBigEndianNonAlloc(count, payload, num)
                                                                  MagnificentSendPipe.cs:179
                                   -> ThreadFunctions.SendMessagesBlocking(stream, payload, packetSize)
                                                                  ThreadFunctions.cs:12
                                      -> stream.Write(payload, 0, packetSize)
                                                                  ThreadFunctions.cs:16
```

The two hop details, verbatim:

```csharp
// Client.cs:637-640
dataScratch = _ECF0_F129_ED83_ECC8_F588.GetDataScratch();
Interlocked.Add(ref BytesOut, dataScratch.Count);
...
Telepathy?.Send(dataScratch);
```
```csharp
// Telepathy/TelepathyClient.cs:304 / :318 / :314
if (hehe_E124_E90D_F072_E3C4_F07D.sendPipe.Count < SendQueueLimit)
...
hehe_E124_E90D_F072_E3C4_F07D.sendPipe.Enqueue(message);
hehe_E124_E90D_F072_E3C4_F07D.sendPending.Set();
```

**Two hook choices, both clean:**

1. **Call `World.RequestEscape()` directly** (`World.cs:12984`, `public void`, no args). The
   highest-level, most future-proof option: it respects the cooldown, the safe-area check, the totem
   check, and the cast semantics. The native signature is trivial: instance method, `this` in `rcx`,
   nothing else.
2. **Emit the packet yourself.** `GmEscape` is the smallest packet in the game:
   ```csharp
   // work/decomp/DarzaGameNet.Packets.Game.Client/GmEscape.cs:5
   public class GmEscape : GmPacket
   // :7
   public override GmPacketType Type => (GmPacketType)hehe(199);
   // :9-15   Write/Read are both empty
   public override void Write(ByteWriter w) { }
   public override void Read(ByteReader r) { }
   ```
   So its payload is **empty** and the wire bytes are exactly the 2-byte little-endian type id
   (id 66 per `work/report-nexus.md`): `42 00`. With Telepathy's 4-byte big-endian length prefix:
   `00 00 00 02 42 00`. Emitting it raw bypasses the cooldown and all client-side gates - powerful,
   and correspondingly risky. `Client.SendPacket(GmPacket, Action callback = null)` is `public static`
   (`Client.cs:619`) and `Telepathy` is `public static TelepathyClient` (`Client.cs:134`), so both are
   reachable from a detour.

**Draw-side note:** `DarzaWindow.Draw(GameTime)` (`DarzaWindow.cs:1889`) increments
`Time.TotalFrames` (`:1898`) and renders. Nothing in the escape path touches `Draw`, so escape emission
is decided entirely within `World.Update`.

---

## 8. THE CANDIDATE HOOK LIST

Legend: `this` denotes an instance method (a native detour takes the object in `rcx`). Ranked by value
for the stated goal. "What breaks" covers inlining and shared/private-generic hazards.

| # | class | method | signature | line | static/virtual/sealed/private | gives us | why good | what breaks |
|---|---|---|---|---|---|---|---|---|
| **1** | `World` | `LateUpdate` | `public override void LateUpdate(ClientTime time)` | `World.cs:6356` | instance, override of `public virtual`, not sealed | **per-frame tick, post-logic** | once per frame; runs after `Scene.UpdateScene`'s `Update(time)` (`Scene.cs:1005`) so all entity/projectile updates have run; `World.Shared` reachable statically | inlining (unlikely - called via `List<Node>` virtual dispatch); needs a JIT-entry patch. It is a *late* tick, so a decision made here is consumed next frame |
| **2** | `World` | `Update` | `public override void Update(ClientTime time)` | `World.cs:9473` | instance, override of `public virtual`, not sealed | **per-frame tick, pre-movement** | an entry hook lands before `base.Update` (`:9552`) and before the movement call (`:9567`); a decision written here is consumed the *same* frame | runs before this frame's packet drain, so the world may be one server tick stale at entry |
| **3** | `Projectile` | `LateUpdate` | `public override void LateUpdate(ClientTime time)` | `Projectile.cs:1091` | instance, override of `public virtual`, not sealed | **per-projectile motion + collision + damage decision** | this *is* where a projectile's position advances and where `Hit()`/`HitCharacter()` runs (`:1246`); detouring gives the exact predicate inputs | fires N times/frame (100+ projectiles); needs per-call cost discipline. `this` is the projectile |
| **4** | `Projectile` | `HitCharacter` | `private bool HitCharacter()` | `Projectile.cs:1514` | **private**, instance, not virtual | **the moment a projectile's collision with the local player is decided** | contains the exact predicate `DistanceSquared(World.Player.Position) < (World.Player.Radius + Radius)^2 && CanHitEntity(...)` (`:1573`), the `World.Player.HitBy(this)` call (`:1576`) and the `GmHit` claim (`:1581`) | `private` -> no vtable slot; needs a `MethodDesc` lookup or an entry patch. Runs at most once per projectile per frame |
| **5** | `Entity` | `HitBy(Projectile)` | `public void HitBy(Projectile proj)` | `Entity.cs:2459` | instance, not virtual, not sealed | **the moment damage is computed** | the entire damage arithmetic (crit, pierce, `GetDamageTaken`) is here; gives exact pre/post damage and the victim | runs for *every* entity hit, not only the local player - must filter `this == World.Shared.Player` |
| **6** | `Entity` | `GetDamageTaken` | `public int GetDamageTaken(int baseDamage, double armorPierce, bool criticalHit, out Color color)` | `Entity.cs:568` | instance, not virtual | **the damage-about-to-land number, post-mitigation** | the single reduction of the whole pipeline; small method | called 4x per `HitBy` (health, health%, mana, mana%); the `out Color` complicates the detour ABI (4-byte out pointer) |
| **7** | `World` | `RequestEscape` | `public void RequestEscape()` | `World.cs:12984` | instance, not virtual, not sealed, `public` | **escape emission (callable)** | trivially callable from a detour or by `MethodDesc`; honours cooldown/safe-area/totem/cast | none structurally; risk is behavioural (it *casts* rather than escapes when Impermanence is owned, and the cast is cancelled by movement - Sec 6.5) |
| **8** | `Client` | `SendPacket` | `public static void SendPacket(GmPacket packet, Action callback = null)` | `Client.cs:619` | **static**, not virtual | **every outbound packet (observation or injection)** | the single choke point for all client->server traffic; static means a fixed raw detour signature | very hot (movement every frame). It forwards to the private overload, so patch that one to see the pre-`GetDataScratch` state |
| **9** | `Client` | private send impl | `private static void hehe_EFF0_ED69_F541_E66F_F1F9(GmPacket, Action = null)` | `Client.cs:624` | **private static**, not virtual | **the real outbound send** | `Telepathy?.Send(dataScratch)` at `:640` is the actual handoff; the scratch bytes are available | private; hot; the `ArraySegment<byte>` is pooled - do not retain it |
| **10** | `World` | move emit | `private void hehe_EB2C_E5A5_EE82_E9C5_EF51()` | `World.cs:9969` | **private**, instance | **local player intent, outbound (`GmMove`)** | the one place the committed position + history is serialised and sent (`:9987-10015`) | private; obfuscated name may shift between builds |
| **11** | `World` | movement integrator | `private void hehe_EB2C_E5A5_EE82_E9C5_EF68(float logicDt, float frameDt)` | `World.cs:12666` | **private**, instance | **local player intent, inbound (input -> position)** | contains `Player.Position = vector2` (`:12780`) and the whole input->movement mapping; an entry hook can rewrite `GameInput` before it is read | private; two `float` args (obfuscator gave both the same name); called only when `LogicElapsedSec > 0` (`:9565`) |
| **12** | `Projectile` | `ProcessProjectileData` | `public void ProcessProjectileData(float angle, ushort damage, float damagePercent, ushort manaDamage, float manaDamagePercent, int barrierDamage, float barrierDamagePercent, Vector3 position, ProjDesc desc, bool hitsPlayer, bool playerOwned, Entity owner, AffixData affix, bool shiny, bool criticalHit, bool isAbility = false)` | `Projectile.cs:810` | instance, `public`, not virtual | **projectile spawn with full parameters** | 15 parameters including `desc` and `owner` (`:840-841`, `Desc = desc` at `:892`); the richest spawn observation point | 15-arg call plus `this` - the detour must model the x64 ABI for a large argument set. Also it is not where the projectile enters the world (`AddedToWorld`, `:2045`) |
| **13** | `Entity` | `ShootProjectile` | `public void ShootProjectile(GameProjectile projectile, float alpha = 1f)` | `Entity.cs:1783` | instance, `public`, not virtual | **projectile spawn, wire -> object** | this is the per-projectile hop in the `World` handler loop (`World.cs:11048`); gives the whole wire struct plus the owner `this` | runs per projectile in a batch; `GameProjectile` is a large struct (34 fields) passed by value, so the ABI is a hidden copy |
| **14** | `World` | apply `GmUpdate` | `private void hehe_EB2C_E5A5_EE82_E9C5_EF53(GmUpdate)` | `World.cs:10103` | **private**, instance | **server-tick boundary + object creation/removal** | the `GmUpdate` applier: `UpdateId` sequencing (`:10105`), object removal (`:10109`), local `Player` creation (`:10154`), `LastUpdateId` (`:10301`) | private; very large; runs on the game thread inside the drain loop (`:9690`) |
| **15** | `NetworkStreamExtensions` | `ReadSafely` | `public static int ReadSafely(this NetworkStream stream, byte[] buffer, int offset, int size)` | `work/decomp/Telepathy/NetworkStreamExtensions.cs:9` | **static** extension method | **raw inbound bytes, transport level** | lowest legitimate observation point for inbound traffic; sees Telepathy length-prefixed frames | `NetworkStream` is a BCL type - its methods may be precompiled differently; `stream.Read` at `:13` is the actual call. No game semantics here |

### 8.1 The single best target for each of the three requested jobs

**(a) A per-frame tick, after the world state is current.**
**`World.LateUpdate(ClientTime)`** (`World.cs:6356`), rank 1. A `public override` of a `public virtual`
method on the singleton `World`; runs once per frame after all object updates (`Scene.cs:1005` precedes
`Scene.cs:1043`); `World.Shared` (`World.cs:4216`) is a static, so the hook does not need to capture
`this`. Secondary: the **entry** of `World.Update` (`:9473`), the right place to *write* a movement
decision that must be consumed this frame.

**(b) The moment a projectile's collision with the local player is decided.**
**`Projectile.HitCharacter()`** (`Projectile.cs:1514`), rank 4. The deciding line is
```csharp
// Projectile.cs:1573
if (DistanceSquared(World.Player.Position) < num2 * num2 && 0 == 0 && CanHitEntity(World.Player.GameId, clientTime) && 0 == 0)
```
with `num2 = World.Player.Radius + Radius` (`:1597`), possibly raised to `Desc.Proximity` (`:1555`).
It is **private**, so the detour needs the compiled address via metadata rather than a vtable slot. If a
public method is preferred, `Projectile.LateUpdate` (rank 3) is the caller and the predicate is a few
instructions away, or `Entity.HitBy(Projectile)` (rank 5, `public`) fires for every projectile that
beats the predicate - a filter on `this == World.Shared.Player` gives the same information one step
later.

**(c) The moment the local player's damage is computed.**
**`Entity.HitBy(Projectile)`** (`Entity.cs:2459`) for the whole pipeline, or
**`Entity.GetDamageTaken(int, double, bool, out Color)`** (`Entity.cs:568`) for the final number
(ranks 5 and 6). `HitBy` is `public` and gives the projectile and the entity; the damage is produced at
```csharp
// Entity.cs:2543
baseDamage = GetDamageTaken(baseDamage, num4, proj.CriticalHit, out var color);
```
Detouring `HitBy` allows both observation and **suppression** (return before its body runs, or return
early inside `HitCharacter` before `World.Player.HitBy(this)` at `:1576`). Suppressing at
`HitCharacter` also skips the `GmHit` claim at `:1581` - but the server may then disagree (Sec 3.5 and
Sec 10.1).
---

## 9. WHAT IS OBFUSCATED AND WHAT IS NOT

### 9.1 Names: mostly readable, with two obfuscation layers

**Readable, real English names** for every domain concept. Verbatim examples from the sources cited
above:

* Fields: `Damage`, `DamagePercent`, `CriticalHit`, `HitsPlayer`, `PlayerOwned`, `LocalSeedDerived`,
  `Desc`, `Angle`, `Speed`, `Lifetime`, `Amplitude`, `Frequency`, `Wavy`, `HomingTurnRate`, `Health`,
  `MaxHealth`, `Barrier`, `Armor`, `SpiritMode`, `KnockedUp`, `GameId`, `Radius`, `Position`,
  `Rotation`, `MovingTowards`, `SlideVector`, `TilesPerSecond`, `RequestEscape`, `Escape`.
* Types: `Projectile`, `ProjDesc`, `ProjSinDesc`, `ProjBombDesc`, `GameProjectile`, `Player`,
  `Character`, `Entity`, `Enemy`, `World`, `Scene`, `Node`, `GameObject`, `Client`, `GameInput`,
  `DesktopInput`, `StatusEffects`, `StatusPacketData`, `DamageSeed`, `ProjDesc.HandleProjectile`.
* Methods: `GetPosition`, `GetVelocityAt`, `SizeAt`, `PathHeadingAt`, `ComputeAccelerationDistance`,
  `MaxTravelDistance`, `CalculateDamage`, `CalculateArmorBroken`, `CalculateVulnerable`,
  `CalculateProtected`, `CalculateBarrier`, `CalculateArmored`, `GetDamageTaken`, `HitBy`,
  `HitByProjectile`, `HitCharacter`, `HitEnemy`, `AddHandler`, `RemoveHandler`, `SendPacket`,
  `AddObject`, `GetEnemies`, `GetCharacters`, `GetGameObjects`, `ProjectileCollides`,
  `ShootProjectile`, `UpdateMoveVector`, `StopMovingTowards`, `SplitManaShieldForDisplay`.

**Mangled names** are confined to (a) compiler-generated closure/state-machine types (`_003C_003Ec`,
`hehe_EB30_F404_E8F3_F141_F182`), (b) private helper methods the obfuscator renamed wholesale, and
(c) private fields it renamed. In `World.cs` an entire family of private methods carries sequential
hex suffixes:

```csharp
// World.cs:12666   local movement + attack + escape
private void hehe_EB2C_E5A5_EE82_E9C5_EF68(float _ECF0_F129_ED83_ECC8_F588, float _ECF0_F129_ED83_ECC8_F588)
// World.cs:12984   escape emission - READABLE, because public API was kept
public void RequestEscape()
// World.cs:15910   GmUpdate handler
private bool hehe_EB2C_E5A5_EE82_E9C5_EF81(GmPacket _ECF0_F129_ED83_ECC8_F588)
// World.cs:10103   GmUpdate applier
private void hehe_EB2C_E5A5_EE82_E9C5_EF53(GmUpdate _ECF0_F129_ED83_ECC8_F588)
```
The same pattern appears in `Projectile.cs` (`hehe_E38F_EA5A_F3C4_F733_F055`,
`hehe_F718_F3B9_F092_F709_E3BC`), which are calls into *other* obfuscated statics used purely as
constant-lookup shims.

**Also obfuscated: parameter names.** Note
`private void hehe_...(float _ECF0_F129_ED83_ECC8_F588, float _ECF0_F129_ED83_ECC8_F588)` - **both
parameters have the same name**, because the obfuscator collapsed distinct names onto one token. This
is a real hazard: parameter *order* is correct, but names cannot tell you which `float` is which. In
`World.cs:12666` I resolved them from the call site (`World.cs:9567` passes
`(time.LogicElapsedSec, time.ElapsedSec)`), i.e. `(logicDt, frameDt)`.

**Readable access modifiers are preserved.** `public`/`private`/`protected`/`virtual`/`override`/
`sealed`/`static` all survive, which is what makes Sec 8 meaningful: `RequestEscape` is `public`
(`World.cs:12984`), `HitCharacter` is `private` (`Projectile.cs:1514`), `GetArmor` is
`protected virtual` (`Entity.cs:2334`), `SendPacket` is `public static` (`Client.cs:619`),
`ClientTime.Time` is `public static` (`ClientTime.cs:5`), `GetCharacters` is `public`
(`World.cs:15230`).

### 9.2 Metadata: field names are retained

The fact that the decompilation shows real field names (`Damage`, `HitsPlayer`, `_ownerId`, ...) **is**
the proof that the metadata still carries them - a decompiler has no other source for a field name.
`DarzasDominion.dll` is pure IL with no R2R, and a tree-wide grep for `ObfuscationAttribute` /
`SuppressIldasm` found none, so the **metadata tables (`Field`, `Method`, `TypeDef`) are intact and can
be read at runtime** through the CLR's own metadata APIs, or offline with
`System.Reflection.Metadata`.

Consequence: **the C++ side does not have to hard-code offsets if it does not want to.** At injection
time it can walk the loaded assembly's metadata (via the profiling API's `IMetaDataImport`, or by
following the in-memory `MethodTable`/`FieldDesc` chain of a type reached from a known object) and
resolve every field it needs by name. Strictly more robust than hard-coding, at the cost of a one-time
resolution pass.

### 9.3 Class layout and offsets

**No `[StructLayout]` / `LayoutKind.Explicit` / `[FieldOffset]` / `Pack=` was found on any of the game
types cited here.** Checked: `Projectile`, `GameObject`, `Node`, `Entity`, `Character`, `Player`,
`World`, `ProjDesc`, `ProjectileData`, `GameProjectile`, `StatusPacketData`, `ClientTime`, `GameInput`.
Every one is therefore `LayoutKind.Auto`, and for a **reference type** with only reference/primitive
fields the CLR's default `Auto` layout for that shape is effectively "declaration order with
pointer-size alignment" - which is what the decompiler's field order reflects.

**But `Auto` is a permission, not a promise.** A C++ side that hard-codes `Position` at offset `0xNN`
of `Node` is trusting (a) that the CLR laid the fields out in declaration order, (b) that
`TieredCompilation: false` plus a stable runtime means the layout never changes between runs, and
(c) that a future game build does not reorder. (a) and (b) are very likely true for this build; (c) is
a maintenance risk, not a correctness risk for a pinned binary.

**Two concrete layout hazards that do exist and were observed:**

1. **`GameProjectile` is a `struct`** (`work/decomp/DarzaGameNet.Packets.Stats/GameProjectile.cs:8`)
   with 34 fields including two `GamePoint` structs and five `bool`s. A `struct` with `Auto` layout and
   mixed field types **is** subject to CLR packing decisions, and a native detour that receives it *by
   value* (as `Entity.ShootProjectile(GameProjectile, float)` does, `Entity.cs:1783`) depends on the
   ABI copy layout, not just field offsets. **Do not hard-code `GameProjectile`'s layout.**
2. **Generic base classes shift offsets.** `Player` derives `Character` -> `Entity` -> `GameObject` ->
   `Node`, and none of those are generic - good. But `StatusEffects`, `Pool<Projectile>`
   (`Telepathy/Pool.cs:6`), and the `ThreadLocal<>` tables (`ProjDesc.cs:82,100`) are generic, and a
   generic instantiation gets its own `MethodTable` and its own field layout *per instantiation*. Any
   offset derived for `Pool<Projectile>` is valid only for that instantiation.

**Verdict:** resolve offsets at runtime by field name from metadata (9.2) rather than hard-coding, and
treat any `struct` passed by value as opaque. If hard-coding is chosen for speed, hard-code only the
`public` fields on the `Node`/`GameObject`/`Entity`/`Character`/`Player`/`Projectile` chain.

### 9.4 The constant pool: the one thing that is genuinely opaque

Every literal in the assembly is routed through two lookups:

```csharp
// work/decomp/-----.cs:5272
public static int hehe_F244_F670_F803_F5DD_EB9E(int _ECF0_F129_ED83_ECC8_F588)
// work/decomp/-----.cs:5274
return _E2CE_F7E5_F8CD_E553_F1A2.hehe_E99D_E9E0_E1B2_F128_E8B3.hehe_E99D_E9E0_E1B2_F128_E8BB[_ECF0_F129_ED83_ECC8_F588];
// work/decomp/-----.cs:2589
return hehe_E99D_E9E0_E1B2_F128_E8BB[_ECF0_F129_ED83_ECC8_F588];
```
and the array is filled from an **encrypted manifest resource** at type-init:

```csharp
// work/decomp/-----.cs:5006
Stream stream = _E311_EE2E_F8F8_E14F_E1EC.hehe_F244_F670_F803_F5DD_EB9E(Assembly.GetExecutingAssembly().GetManifestResourceStream(hehe_E99D_E9E0_E1B2_F128_E8B2()));
// work/decomp/-----.cs:5033
num2 = binaryReader.ReadInt32() ^ -(-173014974 ^ 0x29C78948);
// work/decomp/-----.cs:5040
hehe_E99D_E9E0_E1B2_F128_E8BB[num2] = binaryReader.ReadInt32() ^ 0x6C0BA624;
// work/decomp/-----.cs:5045
num2 = binaryReader.ReadInt32() ^ 0xA6FD2B4;
// work/decomp/-----.cs:5167
num2 = binaryReader.ReadInt32() ^ 0x6F5022A5;
```
`hehe_E99D_E9E0_E1B2_F128_E8BB` is `internal int[]` (`-----.cs:4956`). So **all the numeric constants I
could not read - the armour coefficients in `CalculateDamage`, the `TilesPerSecond` movement
constants, `hehe(16)==0.5f`, `hehe(110)` TTL, `hehe(119)` dt clamp, the boomerang halving divisor - are
one XOR away from being recovered, but not from the source.**

**A hook does not need to decrypt them.** Every such value is either (a) observable from the live
object graph (`Armor` is `Entity.Armor`; speed is `GameObject.CurrentVelocity`; `Desc.Size`/`Speed`/
`Lifetime` are readable fields), or (b) obtainable by reading the *already-initialised* `int[]` out of
the loaded process at a known static address. That second route is the recommended one: find the
`_E2CE_F7E5_F8CD_E553_F1A2` type's static field holding the array, read it, and you have the whole
constant table without decrypting anything.
**Critical correction: `hehe(N)` is not one table.** The obfuscator emits *four* parallel pools, and
which one a call site reads is decided by the **return type of the call in the IL** - a fact the
decompiled C# hides, because every call renders identically as
`_E311_EE2E_F8F8_E14F_E1D6.hehe_F244_F670_F803_F5DD_EB9E(N)`. The pool is
**1035 ints + 16 longs + 787 floats + 229 doubles** (`work/report-nexus.md` Sec 0 and the decryption
in `work/_pooldecode.py` / `work/_pool.bin`; the decode is verified against eight independently known
packet ids). **Therefore the same index N can be 0 in the int pool and non-zero in the float pool** -
which is exactly what tripped up an earlier draft of this document, where the `float` zero and the
`double` zero were assumed to be the same index.

The two tools that resolve this are `autododgeresearch/tools/poolresolve.py` (pairs each `ldc.i4 N`
with the consuming `call <ty> ...(int32)` and prints the resolved value) and
`autododgeresearch/tools/il_constants.py` (this document's helper, same idea with a `--find` mode).
A sibling agent in this workspace has already run the decryption; the values quoted below are
**resolved, not inferred**, and each is annotated with its pool.

Resolved values from the decrypted pools (see `autododgeresearch/CLIENT-FINDINGS.md` Sec 2.9):

| site | pool / index | resolved value |
|---|---|---|
| `Entity.CalculateDamage` linear term | double pool[74] | **0.7** |
| `Entity.CalculateDamage` linear term | double pool[215] | **0.007** |
| `Entity.CalculateDamage` armour cap | double pool[113] | **100.0** |
| `Entity.CalculateDamage` hyperbolic numerator | double pool[69] | **15.0** |
| `Entity.CalculateDamage` hyperbolic denominator | double pool[101] | **25.0** |
| `Entity.cs:2541` pierce clamp | double pool[6] | **1.0** |
| `PlayerFunctions.TilesPerSecond` fear cap | double pool[18] | **40.0** |
| `PlayerFunctions.TilesPerSecond` grounded divisor | double pool[14] | **2.0** |
| `PlayerFunctions.TilesPerSecond` additive term | double pool[19] | **10.0** |
| `PlayerFunctions.TilesPerSecond` curve base | double pool[20] | **3.5** |
| `PlayerFunctions.TilesPerSecond` curve slope | double pool[21] | **5.0** |
| `PlayerFunctions.TilesPerSecond` speed divisor | double pool[16] | **75.0** |
| `ProjDesc` time divisor | float pool[70] | **1000.0** |

So **`CalculateDamage` is exactly**:

```csharp
// Entity.cs:726-731 with the pools resolved
int val = (int)(baseDamage * (0.7 - 0.007 * Math.Min(armor, 100.0)));
double n = 15.0 * baseDamage / (25.0 / Math.Max(baseDamage, 1.0) * armor + 15.0);
return Math.Max(val, (int)n);      // then floored at 1 by GetDamageTaken
```
i.e. **a 0.7 base multiplier, a 0.7 %-per-armour-point linear term capped at 100 armour points, and a
hyperbolic fallback, taking whichever is better for the defender, floored at 1.** The task's
anticipated `0.74 / 0.215` shape was the right *form* and the wrong *numbers* - the real pair is
**0.7 / 0.007 with a cap of 100**.

And **`TilesPerSecond` is exactly**:

```csharp
// PlayerFunctions.cs:459-533 with the pools resolved
if (paralyzed || cutscene) return 0.0;
if (fearOfTheBull && speed > 40.0) speed = 40.0;
if (grounded && !GroundedSafeTiles.Contains(tileType)) speed /= 2.0;
speed += 10.0;
double tps = 3.5 + 5.0 * (speed / 75.0);
tps *= StatusEffectValues.GetValue(Slowed, slowTier);
tps *= StatusEffectValues.GetValue(Speedy, speedyTier);
```
so **`tilesPerSecond = (3.5 + 5.0 * (SPD + 10.0) / 75.0) * slowMult * speedyMult`**, i.e. **3.5
tiles/s at SPD 0 and 8.5 tiles/s at the cap of SPD 75**. Any escape budget built on the reference
game's `4.0 + 5.6*(SPD/75)` (which the pre-existing `work/auto-dodge-feasibility.md` used) is
**~13 % too generous** - a real, quantified error this map removes.

Other values already pinned by cross-reference in `work/report-nexus.md`:

| lookup | value | anchor |
|---|---|---|
| `hehe(0)`, `hehe(1)` | 0, 1 | boolean idioms throughout (`== hehe(0)`, `!= 0`) |
| `hehe(95)` | 32 | `(char)hehe(95)` is the space character; `CharacterTotem.Impermanence == 0x20` |
| `hehe(22)` | 5 | `GmMapInfo.Type`; `GmPacketType.MapInfo == 5` |
| `hehe(237)` | 64 | the app-interruption nexus `Options` value |
| `hehe(6)` | 3 | `ControllerActions.Nexus == 3` (`DesktopInput.cs:405`) |

This document adds three more with independent anchors:

| lookup | value | anchor |
|---|---|---|
| `hehe(6)` | 1 | `value * (hehe(6) - armorPierce)` (`Entity.cs:618`) must be `1 - pierce`; `value * (hehe(6) + vulnerable)` (`:766`) must be `1 + vulnerable`; `Math.Min(hehe(6), num4)` is a **clamp to 1** (`:2541`) |
| `hehe(2)` | 1 | `QualitySize = hehe(2)` (`Projectile.cs:141`) is the neutral size multiplier; `MoveSpeed = ... : hehe(2)` (`DesktopInput.cs:383`) is a 1.0 throttle; `if (num > hehe(2)) { num = hehe(2); }` is a **clamp to 1** (`DesktopInput.cs:393-395`) |
| `hehe(70)` | 1000 | the ms -> s divisor, used in `DarzaWindow.cs:1755`, `Projectile.cs:1116/1127/1145/1159`, `ProjDesc.cs:1161/1207/1280`, `World.cs:12744` |
| `hehe(24)` | 1000 | `Lifetime * hehe(24)` and `Telegraph * hehe(24)` convert seconds to ms (`ProjDesc.cs:1280`, `Projectile.cs:1112`) |

and two strongly inferred but **not** independently anchored:

| lookup | inferred | why |
|---|---|---|
| `hehe(16)` | 0.5 | used as `Radius = ObjSize * hehe(16)` (`GameObject.cs:1570`); a half-tile hitbox radius is the only self-consistent reading, and `Projectile.UpdateRadius` (`Projectile.cs:1864`) uses `hehe(1)` (=1) where the base uses `hehe(16)`, so the projectile override drops the halving - which only makes sense if `hehe(16) == 0.5` |
| `hehe(9)` / `hehe(12)` | 0.5 | the kinematic `1/2 a t^2` coefficient (`ProjDesc.cs:1340`) and the `1/2 * FreqGain * t^2` phase coefficient (`ProjSinDesc.cs:157`) |

Two zeros coexist: `hehe(7)` is the zero sentinel in `double`/`int` comparisons (`Speed != hehe(7)`,
`Orbit != hehe(7)`, `Telegraph > hehe(7)`, `RehitDelay >= hehe(7)`), while `hehe(5)` is the zero used in
`float`-arithmetic contexts (`_homingSteer = hehe(5)`, `num9 = hehe(5)`, `y = hehe(5)`). The obfuscator
interns literals per type/context, which is exactly why the boomerang divisor at `ProjDesc.cs:1208` is
ambiguous (Sec 3.6.2) and why `gameId % hehe(5)` is suspicious (Sec 3.6.4).

---

## 10. GAPS

Everything below is something a hook needs that **this static pass could not determine**. Each is
phrased as a measurement. Ordered by how much it blocks design.

### 10.1 ~~Blocking: is the server authoritative over hits, or does it trust the client's claim?~~
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

### 10.2 ~~Blocking: the five opaque armour coefficients~~ **CLOSED - resolved from the decrypted pool**

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

### 10.3 ~~Blocking: the movement constants~~ **CLOSED - resolved from the decrypted pool**

`TilesPerSecond` is exactly **`(3.5 + 5.0 * (SPD + 10.0) / 75.0) * slowMult * speedyMult`**, i.e.
**3.5 tiles/s at SPD 0 and 8.5 tiles/s at the SPD 75 cap**, clamped to SPD 40 under `FearOfTheBull`,
halved when `Grounded` off a safe tile, and 0 while paralysed or in a cutscene (Sec 6.2, Sec 9.4).

Any escape budget derived from the reference game's `4.0 + 5.6*(SPD/75)` (which the pre-existing
`work/auto-dodge-feasibility.md` used) is **~13 % too generous**.

**Residual:** the **units-per-tile ratio** (world tile -> screen pixels) is still only the runtime
camera zoom `World.BaseNodeScale` (Sec 4.3). It does not affect prediction, which should stay in tiles;
it only affects any budget quoted in pixels.

### 10.4 High: the boomerang half-range divisor

**Why it matters.** `ProjDesc.cs:1208` is `(float)(Lifetime * Speed) / hehe(7)` while `hehe(7)` is the
zero sentinel elsewhere - either the divisor is 2 (sane) or the expression is degenerate. Boomerang
projectiles are common and their whole threat envelope depends on it.

**Experiment.** Spawn/know a boomerang (`Desc.Boomerang == true`), log `Projectile.Position` every
frame, find the turnaround frame, compare its distance to `Lifetime * Speed / 2`. Also 10.2(a).

### 10.5 High: `LinearCongruentialGenerator`, and the two suspicious modulus/phase literals

**Why it matters.** `ProjDesc`/`ProjSinDesc` use `LCG.Value.NextDouble()` seeded from `GameId` to place
spawn offsets (`ProjDesc.cs:1223-1260`) and, for `Wavy`, the sine phase (`ProjSinDesc.cs:150-151`).
Without the LCG's arithmetic and its `Initialize`/`NextDouble` semantics, the **exact** spawn position
of a fan or a wavy projectile cannot be reproduced - the prediction inherits a phase error.

**Experiment.** Read `work/decomp/DarzaCore.Tools/LinearCongruentialGenerator.cs` (small - it was not
read in this pass). Also confirm whether `gameId % hehe(5) == 0` (`ProjSinDesc.cs:155`) is a parity
test, and calibrate `hehe(752)` (`Projectile.cs:542`, the homing steer dead-zone squared) against the
`HomingSteerDeadZone = 1.25f` constant. Note `LCG` and `_projectileHandlers` are `ThreadLocal`
(`ProjDesc.cs:82,100`; `ProjSinDesc.cs:12`) - the hook must run on the simulation thread or re-seed
identically.

### 10.6 High: which layer live projectiles occupy, and the exact `AddObject` branch

**Why it matters.** Enumerating `World.Objects` and type-testing every entry is O(all game objects) per
frame. If projectiles all land in one `GameObjectLayer`, the enumeration is much cheaper.

**Experiment.** At a frame tick, count `World.Objects.Count`, count entries `is Projectile`, and count
`World.GameObjectLayer.Children` / `World.FlatGroundLayer.Children` entries `is Projectile`. Also dump
the `AddObject` decision (`World.cs:14331-14382`) per type in a live session.

### 10.7 Low: `hehe(16)`, the radius halving - statically resolvable, not yet looked up

**This is one `poolresolve.py` call, not a live experiment** (a sibling agent's status note claims it
is closed; I have not seen the value, so I record it as *resolvable but unread* rather than asserting
it).

**Why it matters.** It is half of the collision predicate's radius term. The chain is:
`GameObject.UpdateRadius` does `Radius = ObjSize * hehe(16)` (`GameObject.cs:1570`), and
`Projectile.UpdateRadius` overrides it with `Radius = (float)Desc.Size * hehe(1)` before applying
`Affix.ProjectileSize` and `QualitySize` (`Projectile.cs:1864-1873`). For the two to be consistent,
`hehe(16)` must be `0.5` in the float pool.

**Experiment (static).** `python tools/poolresolve.py` restricted to the `GameObject.cs:1570` call
site. **Confirmation (live, 1 minute):** read `Projectile.Radius` and `Projectile.Desc.Size` for a
known projectile; the ratio is `hehe(16) * Affix.ProjectileSize * QualitySize`. Also read
`World.Player.Radius`.

### 10.8 Medium: what actually times attack cadence, if not `LogicElapsedSec`

The `LogicElapsedSec` arithmetic is now **settled** (Sec 1.4, read from the real IL): it is
`(Stopwatch.ElapsedMilliseconds_now - Time.LogicTotal) / 1000` where `Time.LogicTotal` was written
moments earlier in the same frame, so it is a residual ~0, and the movement gate passes only because
the sentinel it is compared against (`hehe(5)` at `World.cs:9565`) is negative.

**That raises a real, still-open question:** several timers decrement by this residual -
`Entity.cs:824` (`_attackDelay`), `Entity.cs:845` (`_moveDelay`), `Player.cs:4835` (a timer),
`Player.cs:4841` (`_laserAttackDelay`), `Player.cs:4845` (`_sweepAttackDelay`). A residual decrement
would never expire, yet attacks *do* respect cooldowns in game.

**Why it matters.** If a hook wants to know when the next shot is allowed (e.g. to time a
shoot-then-dodge), it needs the real gate.

**Experiment.** Log `Time.LogicElapsedSec` alongside `Entity._attackDelay` values across a real
attack cycle and see whether the dictionary entries actually fall, and by how much. If they do not,
the gate is `ProjDesc.RateOfFire` plus the server's `GmProcCooldown` (`work/decomp/.../GmProcCooldown.cs`)
rather than a client-side timer. **Also worth checking (cheap, static): read `hehe(5)` and `hehe(7)`
from the decrypted *double* pool** (Sec 10.2b) - that confirms the sentinel and closes half of this.

### 10.9 Medium: warm vs cold packet classification

**Why it matters.** Warm packets are handled on the network thread; if any projectile-affecting packet
is warm, its handler can run concurrently with the game thread, and a hook reading the object graph
without synchronisation can tear. (`World`'s own handlers only enqueue, which suggests the designers
were aware - but the whitelist was not enumerated.)

**Experiment.** Read `Client.hehe_E5EC_EDDD_E866_F7B6_EF0F` at runtime and print which `GmPacketType`
ids are `true`. For each projectile-relevant id, check whether the handler mutates shared state or only
enqueues.

### 10.10 Medium: `DamageSeed` and the damage roll

**Why it matters.** `GmMapInfo.DamageSeed` (`GmMapInfo.cs:58`) names a seeded roll, and
`ProjDesc.MinDamage`/`MaxDamage` (`:144`,`:140`) imply a damage range. The roll site was not located;
every `Projectile.Damage` observed in the wire struct may already be the rolled value (nothing to
reproduce) or may be the max with the roll applied server-side.

**Experiment.** Record `GameProjectile.Damage`, `Desc.MinDamage`, `Desc.MaxDamage` and the observed
Health delta for many hits; decide whether `Damage` is already rolled. If it is, this gap closes for
free.

### 10.11 Medium: `WallBouncePath` arithmetic

**Why it matters.** A bouncing projectile's path is not a closed-form function of `(t, angle, speed)`;
without the formulas (or a simulation harness) such projectiles cannot be predicted.

**Experiment.** Read `work/decomp/DarzaCore.Data/WallBouncePath.cs` (9220 bytes) - not read in this
pass - and reproduce `Projectile.StepWallBounce` (`Projectile.cs:1822`); that needs
`World.ProjectileCollides` (`World.cs:13411`) and the tile collision byte array
`hehe_E331_E9CE_F5E3_EBF5_EA47` (`World.cs:9674-9677`).

### 10.12 Medium: per-tile speed multipliers

**Why it matters.** Movement is multiplied by the current tile's `Speed` (`World.cs:12693`), and that
field (`hehe_E331_E9CE_F5E3_EBF5_EA4B`) is written once per move-packet emit from
`Tilemap.GetTile(gmMove.Position)` (`World.cs:9998`). A movement model needs the per-tile table.

**Experiment.** Dump the tile type -> `Speed`/`Type` mapping by walking the tilemap, or read `TileData`
(the live tile is `protected TileData _currentTile`, `Entity.cs:62`).

### 10.13 Low: exact element order of the projectile spawn packet

**Why it matters.** Only if the hook wants to read raw bytes rather than the object graph.

**Experiment.** `GmProjectiles.Read`/`Write` (`GmProjectiles.cs:77`/`:17`) and
`GameProjectile.Read`/`Write` (`GameProjectile.cs:225`/`:78`) are flattened state machines; the order
can be recovered by the XOR-recurrence method used in `work/report-nexus.md` Sec 0, or measured by
framing a capture against a known spawn.

### 10.14 Low: `Prefs` (behaviour switches that change the model)

Several switches alter what the client does, so a hook must read them: `Prefs.projectile_grow`
(`Projectile.cs:1155`), `Prefs.close_menus_on_damage` (`:1577`), `Prefs.auto_scale_world`
(`World.cs:6151`), `Prefs.crouch_speed` (`DesktopInput.cs:383`), `Prefs.hide_players`
(`World.cs:14627`), `Prefs.cursor`. Also `GameObject.CachedHidePlayersNearProjectiles`
(`GameObject.cs:249`) gates the proximity block at `Projectile.cs:1206-1225`, which calls
`entity.MarkNearEnemyProjectile(total)` - a **secondary consumer of projectile positions** that can
misbehave if positions are wrong. The full `Prefs` surface was not enumerated.

### 10.15 Low: `hehe(110)`, `hehe(119)` and the escape timings - all resolvable statically

`World.cs:11706` drops deferred projectile spawns older than `hehe(110)` ms; if a hook wants to
predict "a spawn is coming", that TTL is the window. `hehe(119)` caps the frame dt
(`DarzaWindow.cs:1775`), bounding how far a projectile can move in one frame. `hehe(69)` is the escape
cooldown seconds (`World.cs:12995`) and `hehe(21)` the cast duration (`World.cs:13020`).

**All four are single `poolresolve.py` lookups** (Sec 10.2b) - no live measurement required. Only
`hehe(119)` ideally wants confirmation against a real frame-delta histogram, because it bounds the
prediction horizon.

### 10.16 Explicitly not investigated in this pass

* `work/decomp/DarzasDominion.Darza.Game.Objects/GameObject.cs` (4681 lines), `AoeObjectLayer.cs`,
  `Tilemap.cs` (79 KB), `Laser.cs` (51 KB) and `Sweep.cs` were read only where cited. Their per-frame
  `LateUpdate` ordering relative to `Projectile.LateUpdate` is assumed to be sibling order within the
  same layer and was **not verified**.
* `work/decomp/DarzasDominion.Darza.Game.Objects.Components/AoeBomb.cs` and the `Objects/Aoes*` trees:
  **not read**. Area effects are a separate damage channel (`Entity.HitBy(AoeData)`, `Entity.cs:2203`;
  `GmShockwave`; `AoeObjectLayer`) and are a **real gap** for a complete autododge, since a bomb
  detonates on a timer rather than by collision. **Recommended next read.**
* `work/decomp/-----.cs` / `hehe-----.cs` (773 KB) were consulted **only** for the constant-pool
  mechanism (Sec 9.4). Any main-loop or watchdog logic living there was not examined.
* `work/decomp/DarzasDominion.Darza.Game.UI*/` beyond `GameInput.cs` and `DesktopInput.cs`: not read.
* `Windows.Crash/CrashReporter`, `DarzasDominion.Darza.Diagnostics/StallProbe`, `UiThreadWatchdog` and
  `FrameProfiler` are all called from the frame loop (`DarzaWindow.cs:1733`, `:1779`, `:1800+`) but
  were not examined. `CrashReporter.Send` (`Program.cs:212`) is the only outbound call outside the game
  socket and may itself be worth knowing about.
---

## Appendix A: the recovered `Projectile.LateUpdate` control flow in one block

The whole per-frame projectile step with the decompiler scaffolding removed. `num` is age in ms; `x`,`y`
are the anchor-relative offset produced by the motion model.

```csharp
// Projectile.LateUpdate(ClientTime time)                        Projectile.cs:1091
base.LateUpdate(time);                                 // :1093  GameObject.LateUpdate
// spawn grow-in animation                              :1096-1108
int num = World.ClientTime - _startTime;               // :1109  age, ms
// alpha / telegraph                                    :1110-1113
// SizeGain -> SetSize + Radius                         :1114-1124
// avg-light fade                                       :1125-1138
if (_dead) {                                           // :1141
    if (!StepDeath(time.ElapsedSec) && !_homingInit) { // :1143
        int t = (Desc.GetVelocityAt((float)Desc.Lifetime) < 0f) ? _deathTimeAlive : num;
        Desc.GetPosition(t, _angleSin, _angleCos, out x, out y, GameId);
        Position = AnchorBase() + new Vector3(x, y, 0); // :1147
    }
    return;                                            // :1149
}
bool expired =
      HomingDrives(num)      ? StepHoming(num, out x, out y)               // :437
    : (Desc.WallBounce > 0)  ? StepWallBounce(num, out x, out y)           // :1822
    :                          Desc.GetPosition(num, _angleSin, _angleCos, out x, out y, GameId);  // :1149
if (expired) {                                         // :1151
    _dead = true; _deathTimeAlive = num;               // :1153-1154
    if (!Prefs.projectile_grow) { Remove(); return; }  // :1155-1158
    RemoveAllActions();                                 // shrink-out, :1160-1170
    Position = AnchorBase() + new Vector3(x, y, 0);
    return;
}
Vector3 position = Position;                           // :1173
Position = AnchorBase() + new Vector3(x, y, 0);        // :1174
// facing lerp -> Rotation                              :1175-1204
StepTrails(position, time.ElapsedSec, num);            // :1205
// proximity-fade marks for OTHER characters (<= ~3 tiles squared)   :1206-1225
StaticObject so = World.ProjectileCollides((int)Position.X, (int)Position.Y, out bool hit3d); // :1227
if (so != null) {                                      // :1228
    if (hit3d || (!Desc.IgnoreTerrain && Desc.WallBounce <= 0)) {          // :1230
        if (HitsPlayer)
            Client.SendPacket(new GmAllyHit { Time = World.ClientTime, ProjectileId = GameId, ObjectId = so.GameId }); // :1234
        World?.CreateHitEffect(so, this);              // :1241
        Remove(); return;                              // :1242-1243
    }
} else if ((Desc.Telegraph <= 0 || num >= Desc.Telegraph * 1000) && Hit()) {   // :1246
    Remove(); return;                                  // :1248-1249
}
// spin                                                :1251-1257
```

And the two collision leaves, reduced:

```csharp
// Projectile.Hit()                                             Projectile.cs:1505
private bool Hit()
{
    if (HitsPlayer) { return HitCharacter(); }
    return HitEnemy();
}

// Projectile.HitCharacter()  -- the local-player branch only   Projectile.cs:1514
private bool HitCharacter()
{
    int clientTime = World.ClientTime;                                          // :1516
    if (World.Player == null || World.Player.KnockedUp || World.Player.SpiritMode)
        { /* falls through to the other-characters loop */ }                    // :1521
    float r = World.Player.Radius + Radius;                                     // :1597
    if (Desc.Proximity > 0 && !PlayerOwned) { r = MathF.Max(r, (float)Desc.Proximity); }  // :1553-1556
    if (DistanceSquared(World.Player.Position) < r * r
        && CanHitEntity(World.Player.GameId, clientTime))                       // :1573
    {
        bool pierced = RegisterEntityHit(World.Player.GameId, clientTime);      // :1575
        World.Player.HitBy(this);                                              // :1576
        Client.SendPacket(new GmHit { Time = clientTime, ProjectileId = GameId,
                                      ObjectId = World.Player.GameId, PlayerProj = PlayerOwned });  // :1581
        if (pierced) { return true; }                                          // :1588-1591
    }
    // :1605+ loop over World.GetCharactersNoNpc(), skipping KnockedUp/SpiritMode,
    //       same radius test, entity.HitBy(this), GmAllyHit claim
    return false;                                                              // :1604
}
```

---

## Appendix B: open-question shortlist, in experiment order

**Already done** (do not redo): the constant pools are decrypted and the armour coefficient and
movement-speed questions are closed (Sec 3.7, Sec 6.2, Sec 9.4); the server-trusts-the-client
question is answered on live traffic (Sec 10.1).

Ranked list of what is actually still open:

1. **The inverse of 10.1 - does the server *also* simulate?** Patch `Client.SendPacket` to drop
   exactly one outbound `GmHit` (`PlayerProj = false`) and confirm no HP fall follows within ~2 s.
   This is the only remaining architectural question, and it decides whether hit-report suppression is
   a viable actuation primitive at all.
2. **Which layer live projectiles occupy** (10.6). Count `World.Objects` entries `is Projectile` versus
   `World.GameObjectLayer.Children` / `FlatGroundLayer.Children` at a frame tick. Decides whether the
   per-frame threat scan is O(projectiles) or O(all objects) - i.e. whether the hook's budget is fine
   or the whole design needs a spatial index.
3. **AoE / bomb damage** (10.16). Currently *not read at all*: `AoeBomb.cs`, `Objects/Aoes*`,
   `Entity.HitBy(AoeData)` (`Entity.cs:2203`). Bombs detonate on a timer rather than by collision, so
   the collision-based threat model in Sec 3.4 misses a whole damage channel. **This is the largest
   actual coverage hole in this document.**
4. **`WallBouncePath`** (10.11). Without it, bouncing projectiles are not predictable. Read
   `work/decomp/DarzaCore.Data/WallBouncePath.cs` and reproduce `Projectile.StepWallBounce`
   (`Projectile.cs:1822`).
5. **The LCG and the boomerang divisor** (10.4, 10.5). Both are prediction-*accuracy* items: the LCG
   seeds spawn offsets and the `Wavy` phase, and the boomerang divisor sets the turnaround range.
   Partly static (`poolresolve.py` for the divisor, read `LinearCongruentialGenerator.cs` for the LCG).
6. **Confirm the radius halving** (10.7). One `poolresolve.py` call, or one live read of
   `Projectile.Radius / Desc.Size`.
7. **The remaining literal indices** (10.2b) - the escape timings, `hehe(110)`, `hehe(119)`,
   `hehe(45)`, `hehe(752)`, and the `StatusEffect` id -> name map. All static, all cheap.
8. **What actually times attack cadence** (10.8), now that `LogicElapsedSec` is known to be a residual.
9. **Units-per-tile** (Sec 4.3 residual) - only needed if a budget is quoted in pixels; the prediction
   itself should stay in tiles.

## Appendix C: files in this deliverable

| file | what it is |
|---|---|
| `autododgeresearch/CLIENT-MAP.md` | this document |
| `autododgeresearch/tools/g.py` | bounded grep-with-context helper used to locate every targeted region of `World.cs` and the other large files. Usage: `python g.py <file> <regex> [--ctx N] [--max M] [--range A B] [--sig] [--lit]` |
| `autododgeresearch/tools/il_constants.py` | resolves obfuscated literal *values* from `il/client-5.8.2.il`. Usage: `python il_constants.py <il_file> <index> [--after K]` or `python il_constants.py <il_file> --find "<substring>" --window N` |
| `autododgeresearch/il/client-5.8.2.il` | 89 MB `ilspycmd -il` dump of Darza's Dominion 5.8.2, produced by a sibling agent in this workspace. **This is the file that lets a constant be read without decrypting the pool**, because the IL shows the instruction sequence around each `hehe(int32)` call. |
| `autododgeresearch/tools/poolresolve.py`, `work/_pool.bin`, `work/_pooldecode.py` | pre-existing (sibling/parent work): the decrypted constant pools and the resolver that maps a call site to its pool. Cited in Sec 9.4. |
| `autododgeresearch/tools/_projhit_raw.txt`, `_numbers.txt`, `_rtt_raw.txt`, `_cadence_raw.txt`, `_deaths_raw.txt` | live captured-traffic measurement outputs produced by sibling agents. Cited as **[live]** in Sec 3.5 and Sec 10.1. |

`CLIENT-MAP.md` and the two scripts under `tools/` are the only files this task created. Everything
else in `autododgeresearch/` listed above was produced by other agents working in the same workspace;
it is cited, not owned.