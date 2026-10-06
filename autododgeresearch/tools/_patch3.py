# -*- coding: utf-8 -*-
import io
p = r"G:\coding\javamapp\drelay\autododgeresearch\CLIENT-MAP.md"
s = io.open(p, encoding="utf-8").read()

# ---- FIX 3: the literal table was wrong because hehe(N) is per-TYPE, not one pool.
old = """The mapping is globally consistent, which is how some values were already pinned in the pre-existing
`work/report-nexus.md`:"""
new = """**Critical correction: `hehe(N)` is not one table.** The obfuscator emits *four* parallel pools, and
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

Other values already pinned by cross-reference in `work/report-nexus.md`:"""
assert old in s
s = s.replace(old, new)
io.open(p, "w", encoding="utf-8").write(s)
print("fix3 ok ->", len(s))