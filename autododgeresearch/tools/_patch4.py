# -*- coding: utf-8 -*-
import io
p = r"G:\coding\javamapp\drelay\autododgeresearch\CLIENT-MAP.md"
s = io.open(p, encoding="utf-8").read()

old = """**I cannot resolve `hehe(74)`, `hehe(215)`, `hehe(113)`, `hehe(69)`, `hehe(101)` from the decompile.**
They are exactly the armour coefficients, and they live in the encrypted manifest resource. The task's
`0.74 / 0.215 / ...` figures are **consistent with this structure** (`A = 0.74`, `B = 0.00215` per
armour point, `K = 100`, `C = 50`, `CAP` ~ 45) but **that is an inference, not a reading**. The
experiment to pin them is in Sec 10.2 and is cheap: read `Armor`, read the incoming `Damage`, read the
resulting `Health` delta, and fit."""

new = """**These five literals have since been RESOLVED** (Sec 9.4 and
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
pools."""
assert old in s
s = s.replace(old, new)

# ---------------------------------------------------- FIX 5: Appendix B refresh
old2 = """1. **Dump the constant array** (10.2(a)). One read unblocks the armour coefficients, the movement
   constants, every `StatusEffect` id, the boomerang divisor, the radius halving, the dt clamp, the
   history step, the homing dead-zone, the escape cooldown and cast duration."""
new2 = """1. ~~**Dump the constant array** (10.2(a)).~~ **DONE** by a sibling agent: the four pools are
   decrypted and verified against eight known packet ids (`work/_pool.bin`, `work/_pooldecode.py`,
   `autododgeresearch/tools/poolresolve.py`). The armour coefficients and the movement law are now
   exact (Sec 3.7, Sec 6.2, Sec 9.4). What remains from this bucket is only the handful of values not
   yet looked up: the boomerang divisor, `hehe(16)` (the radius halving), `hehe(110)`, `hehe(119)`,
   `hehe(45)`, `hehe(752)`, and the `StatusEffect` id -> name map. Re-run `poolresolve.py` restricted
   to those indices."""
assert old2 in s
s = s.replace(old2, new2)
io.open(p, "w", encoding="utf-8").write(s)
print("fix4/5 ok ->", len(s))