# -*- coding: utf-8 -*-
import io
p = r"G:\coding\javamapp\drelay\autododgeresearch\CLIENT-MAP.md"
s = io.open(p, encoding="utf-8").read()

old = """As literally written, the subtraction is `x - x = 0`, so `LogicElapsedSec` would always be 0 - yet
`World.Update:9565` gates *all local movement* on `time.LogicElapsedSec > hehe(5)` and `Player`
attack timers decrement by it (`Player.cs:4835`, `Entity.cs:824,845`). Since the game demonstrably
moves, **this is a decompiler artifact, not the real arithmetic**: the real code must read the
stopwatch *before* writing `LogicTotal` (i.e. `LogicElapsedSec = (now - previousLogicTotal)/1000`).
`ClientTime = (int)time.LogicTotal` (`World.cs:9505`) is used as a millisecond clock for projectile
life (`Projectile.cs:670`, `:1109`). **A hook should treat `time.LogicTotal` as "monotonic ms clock"
and `time.ElapsedSec` as "frame dt in seconds"; do not reproduce the literal expression.**"""

new = """As literally written the subtraction is `x - x`, which would make `LogicElapsedSec` zero or negative.
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
  `LogicElapsedSec`.**"""
assert old in s
s = s.replace(old, new)
io.open(p, "w", encoding="utf-8").write(s)
print("fix2 ok ->", len(s))