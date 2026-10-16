# Auto-aim — the hook's target selection

The hook can aim the player at an enemy through a full 360 degrees, with two modes:
**closest** and **highest hp**. Invulnerable enemies are never chosen.

```
.\hook\out\Drelay.Launcher.exe --game "<game dir>" --autoaim closest
.\hook\out\Drelay.Launcher.exe --game "<game dir>" --autoaim highesthp
```

Nothing happens without the switch: the replacement is published and verified on every
run, but the native side only aims MinHook's `jmp` at it when `--autoaim` is passed.

---

## 1. What the game already has, and the two gates in front of it

The client ships a complete aimbot, and **almost none of it is reimplemented here**.

| what the client has | where | used by this module |
|---|---|---|
| target selection | `AimbotSystem.FindTarget(Player, World)`, `Game/AimbotSystem.cs:179` | **replaced** — that is the whole change |
| projectile leading, wands, curves, orbit, homing, multi-shot spread | `AimbotSystem.CalculateAimAngle`, `:745` | called, unchanged |
| the weapon's reach | `AimbotSystem.CalculateWeaponRange`, `:150` | called, unchanged |
| the use of the chosen angle | `Player.Shoot`, `Objects/Entities/Player.cs:2970` | unchanged |
| "this enemy cannot be damaged" | `Entity.Effects.Unhittable()`, `Objects/Components/StatusEffects.cs:150` | called, unchanged |
| the per-frame call that drives it | `AimbotSystem.UpdateTarget`, from `World.Update` (`Game/World.cs:9564`) | unchanged |

Two gates make the shipped feature unusable on a desktop install:

**The cone.** `FindTarget` keeps only the candidates within

```csharp
abs(angleToEnemy - aimDirection) <= min(player.AimAssist * pi / 20, pi)   // AimbotSystem.cs:293
```

`player.AimAssist` is rebuilt from the equipped gear (`Player.RecalculateAimAssist`,
`Player.cs:7113`). With no aim-assist affix it is zero, the cone is zero, and the
shipped aimbot finds **nothing at all** — which is the "gear enchantment that only
works when the mouse is already close" the module exists to replace. With a large
enough affix the cone reaches `pi` and the selection is already 360 degrees, which is
where the `* pi / 20` is read from: **the client's own formula says the cone is meant to
widen to a full circle**, it just needs an aim-assist value no ordinary roll provides.

**The flag.** `Prefs.enable_aimbot` gates both `AimbotSystem.UpdateTarget` (which calls
`FindTarget`) and `Player.Shoot` (which uses its answer). With it clear, no amount of
target selection has any effect, because nothing ever asks for a target. And the flag
cannot simply be written at startup: `Prefs.enable_aimbot` is `GameSettings`-backed and
the game loads that store from disk **after** the hook's resolve callback has run — in
`DarzaWindow`'s constructor (`Graphics/DarzaWindow.cs:759`,
`GameSettings.Load(FileVersions.SettingsPath)`), while the callback fires 500 ms after
the startup hook signals (`DrelayInstall.cpp:1161`), before the entry point reaches it.
A write made there is overwritten by the file.

That ordering is why **`--autoaim` implies `--frame-replacement`**. The frame
replacement is the only managed entry point on the game's own thread that a run is
guaranteed to have, so it is where the flag is armed (every frame, before
`World.Update` — which is where `UpdateTarget` sits). The alternative, writing from the
watcher thread, is not available: `GameSettings.setBool` is a `Dictionary` indexer write
and a concurrent `TryGetValue` from the game thread is a way to hang the process.

So the replacement does three things and nothing else:

1. it answers `FindTarget` itself, with no cone,
2. it turns `Prefs.enable_aimbot` on and keeps it on from the game's own thread, and
3. it keeps that flag armed if anything clears it.

## 2. The target that is hooked, and why that one

`AimbotSystem.FindTarget(Player, World)` is pinned in `hook/known-build.json` as `aimTarget`
by IL hash, like every other detour target
([HOOK-BUILD-MATRIX.md](HOOK-BUILD-MATRIX.md)). Three properties made it the right
one:

* **it is big enough to have an entry point.** 1580 bytes of IL; the JIT has no budget
  to inline that, so the detour sees it compiled as a method of its own. The two
  obvious alternatives do not have that property — `AimbotSystem.UpdateTarget` is 216
  bytes and the `Prefs.enable_aimbot` accessor is smaller still, and a method the JIT
  inlines has no entry point to patch at all.
* **it is the only place the candidate set is chosen**, so replacing it replaces the
  cone along with everything else, and leaves the client's own highlight of the chosen
  target (the glow `UpdateTarget` paints) working as feedback.
* **it is called once per `World.Update`**, so a target is available on the frame the
  player fires, not one shot late.

The replacement is a **managed replacement**, the mechanism Phase 2 measured and Phase 3
reuses: MinHook's `jmp` lands directly on a managed method with the target's own
signature, so the game's `call` is a managed-to-managed call and no native frame
interrupts the managed stack. The original is reached through MinHook's trampoline as a
**managed** function pointer. See `HOOK-ARCHITECTURE.md` and `PHASE2-SCOPE.md` §4.3–4.4
for why the native-helper route was abandoned.

Because the hook cannot name the game's types at compile time, the replacement takes the
two references as `object` and returns `object` — the same registers as
`Enemy FindTarget(Player, World)` on x64, and the same route
`CollisionObservation.HitCharacterReplacement` takes for its receiver.

## 3. The selection

```
for every enemy the client itself reports (World.GetEnemies()):
    skip if null, if Dead, if Npc
    skip if Effects.Unhittable()          <- Invincible, Invulnerable, Cutscene
    skip if further than the equipped weapon's own range
    closest    -> smallest distance
    highesthp  -> largest Entity.Health, ties broken by distance
```

Three deliberate choices inside that:

* **`Unhittable()` is the client's predicate, not a list of effect ids.** It is what the
  client itself asks when it decides a health bar is drawn as invulnerable, and it keeps
  being right if the game adds a fourth effect. `HasEffect(StatusEffect.Invulnerable)`
  alone would miss `Invincible` and `Cutscene`.
* **The range is the client's own function.** `CalculateWeaponRange` samples every
  projectile's position over its lifetime, so it already knows about wavy, orbiting,
  boomerang and accelerating weapons; the module calls it and applies the same
  `clamp(range * 1.15, 1.5, 10)` the client's own selection does
  (`AimbotSystem.cs:293-294`). A constant would either stop a melee player attacking the
  enemy beside them or let a long staff aim at nothing.
* **The client's enemy-priority table is deliberately NOT used.** `GetEnemyPriority`
  ranks enemies by their `Exp` string and returns zero for anything it does not
  recognise, which would silently drop enemies the operator asked to target. "Closest"
  and "highest hp" mean what they say.

`highesthp` is **current health**, not maximum health. Both fields exist
(`Entity.Health` and `Entity.MaxHealth`, both `double`) and the first-choice log line
prints `health/maxHealth` so the difference is visible in the evidence; the tie-break is
distance.

## 4. The one side effect, stated plainly

**The module turns the game's own aimbot setting on, and the game persists it.**

`Prefs.enable_aimbot` is backed by `GameSettings` (`DarzasDominion.Data.Prefs`), i.e. the
store behind the in-game options menu. There is no way around it: `Player.Shoot` reads
that property before it ever asks for a target, so with it clear the module is
unreachable by construction. The hook sets it at resolve and re-asserts it whenever it
finds it clear, and `bootstrap.log` records both the value it found and every
re-assertion. There is no shutdown callback in the bootstrap, so the value is **not**
restored — after a session with `--autoaim`, the in-game toggle is on.

That is not nothing, and it is not hidden. It is also not harmful on its own: on a
desktop install with no aim-assist affix the client's own selection returns nothing, so
the setting by itself changes no behaviour.

## 5. Reading a run

Everything the module says goes to `<run dir>\bootstrap.log`:

| line | means |
|---|---|
| `auto-aim plan: auto-aim: closest over every damageable enemy ...` | resolved; this is the mode that is live |
| `auto-aim: NOT available -- ...` | resolution failed and the reason is named; the replacement still calls through, so the game is unaffected |
| `managed auto-aim replacement published at 0x...` | the bootstrap published the entry point |
| `auto-aim: the in-game aimbot setting was OFF when this module first ran; ...` | the settings change, named at the moment it happens |
| `auto-aim: re-armed the in-game aimbot setting, which something in the client had cleared` | the setting was re-asserted |
| `auto-aim: the replacement answered its first call in closest mode; the first target is at 143/600 health, 3.2,-1.4` | **the module is live and choosing** — the only line that proves a target was read rather than merely that the method was entered |
| `auto-aim: the replacement was entered, but the module did not resolve (...)` | the hook is patched but the build moved under it; a different fact from not being patched |

In `hook.log`, the native side reports the plumbing:

```
'aimTarget' will be patched with the MANAGED REPLACEMENT at ...; its way back into the
original is the trampoline at ..., published before the hook is enabled
PATCHED 'aimTarget' at ... (trampoline ...)
```

**A target compiled late is still patched.** `FindTarget` is not compiled until a world
exists *and* the aimbot flag is set, which for a session that starts at a login screen is
minutes after the hook's evidence window has closed. The watcher's idle loop polls the
late patch pass every two seconds for as long as the hook is resident, so the module
comes alive when the operator reaches a zone rather than at a moment decided by
`--live-seconds`.

## 6. What this does not do, and what is not proven

* **No line-of-sight test.** Neither the shipped selection nor this one checks whether a
  wall is between the player and the target; projectiles stop at walls in this game. The
  client's own selection has the same property, so this is not a regression, but a
  target behind a wall is still a target.
* **No manual override.** The chosen target wins for as long as it is in range and
  damageable; the module does not yield to the mouse.
* **The server has not been asked.** Aiming is a client-side angle, and the angle that
  leaves the machine is one a player could have produced with a mouse, so nothing about
  the shape of the traffic changes — but no session with `--autoaim` has been run against
  the live server yet, and `GmKicked` is therefore **unverified for this module**. Run it
  with the relay attached and read `proxy/work/logs/events-*.jsonl`, the same way the steering
  spike does.
* **The selection's correctness is not unit-tested.** The offline suite covers the
  plumbing (published, patched, entered, called through) against a synthetic host; the
  three filters and the two scores are not exercised by it, because that host has no
  `AimbotSystem`.
* **An `--autoaim` run does not carry Phase 1's frame evidence.** Implying
  `--frame-replacement` takes the native frame read out of the path, so that run writes no
  `frames.csv` and no frame-ring rows, and it also brings Phase 2's collision observer up,
  because the native dispatch gates both on the same variable. Nothing about aiming needs
  either, and `--trace` and `--dodge` already work the same way, but a run doing both jobs
  at once is not possible.
* **The whole pipeline has not been run against the live game.** Every offline check
  passes and the target is pinned and gate-verified; what is missing is one session with a
  character in a zone, which needs a person at the keyboard and is the only thing that can
  produce the `auto-aim: the replacement answered its first call` line against the real
  client.

## 7. Verifying the build

```powershell
.\hook\build.ps1                        # native + managed, regenerates both tables
.\hook\tools\Fingerprint.ps1                 # the Phase 0 gate, six targets
.\hook\tools\check_opcode_table.py           # the walk against the IL dump (see HOOK-BUILD-MATRIX)
.\hook\tools\Test-HookAbi.ps1           # offline ABI check
.\hook\tools\Test-Injection.ps1         # end to end; run 13 is this module's
```

Run 13 of the injection suite is this module's own test: it publishes a fixture with the
`(object, object) -> object` shape through `--test-target`, runs with `--autoaim closest`,
and requires the replacement to have been published, aimed at by name, **patched by the
idle pass after the live window had closed**, entered, and called through to the original
exactly once per host call — with no crash. What it cannot check is the selection itself,
for the reason in §6.

As last run, on 2026-10-15:

```
run 13: the auto-aim replacement, patched late from the idle pass
  the host has a target selection with a Phase 0 identity to publish PASS  ilSize=15
  the auto-aim replacement was published for the native side PASS
  the module said it could not resolve against this host     PASS
  the native side aimed the hook at the managed replacement by target name PASS
  the patch came from the LATE pass, after the live window had closed PASS
  the replacement reported being ENTERED and unable to act   PASS
  every patched entry reached the original through the managed trampoline PASS
      aim_host_calls=2278 aim_body_calls=2278
  the run did not die                                        PASS  no crash.txt
```

The suite as a whole reports **138 of 140 checks passed**. The two failures are run 10's
trajectory-sampler counters (`sampled=12 live=20`, `hook 0,339 vs host 0,600`), and they
are **pre-existing and unrelated**: the same two checks fail with the same two numbers on
a pristine worktree at the commit this branch starts from, with nothing of this module in
it. They are a timing-sensitive pair of assertions about a churning world, not a symptom
of anything here — but they are not clean, and calling them clean would be worse than
leaving them named.
