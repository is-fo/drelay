// AutoAim.cs -- the hook's auto-aim module, and the replacement that decides what the
// player shoots at.
//
// WHAT THE CLIENT ALREADY HAS, AND WHY IT IS NOT ENOUGH.
//
// The game ships a complete aimbot.  `AimbotSystem.FindTarget(Player, World)`
// (`Game/AimbotSystem.cs:179`) collects every live, non-NPC enemy inside the equipped
// weapon's own range, keeps the ones within a cone of the local aim direction, and picks
// one according to `Prefs.aimbot_priority_mode`; `AimbotSystem.CalculateAimAngle` then
// leads the target for projectile travel, wands, curves, orbits and homing; and
// `Player.Shoot` (`Objects/Entities/Player.cs:2970`) uses that angle instead of the
// mouse's when the whole path is enabled.  None of that is reproduced here.  This module
// **replaces the selection and nothing else**, so the client's own aiming arithmetic, its
// own projectile model and its own target highlight are all still the ones doing the work
// -- the same "call it, do not reimplement it" rule the dodge's damage and trajectory
// paths follow.
//
// What is missing on a desktop install is not the machinery but the two gates in front
// of it:
//
//   1. THE CONE.  The candidate list is filtered by
//      `abs(angleToEnemy - aimDirection) <= min(player.AimAssist * pi / 20, pi)`
//      (`Game/AimbotSystem.cs:293`).  `player.AimAssist` is rebuilt from the equipped
//      gear (`Player.RecalculateAimAssist`, `Objects/Entities/Player.cs:7113`), so a
//      player with no aim-assist affix has a cone of exactly zero and the shipped aimbot
//      finds nothing at all -- the gear enchantment is what makes it appear to work, and
//      only for the enemies the mouse is already near.  A replacement that returns the
//      target itself answers to no cone, which is the full 360 degrees this module is
//      for.
//
//   2. THE FLAG.  `Prefs.enable_aimbot` gates `AimbotSystem.UpdateTarget` -- which is
//      what calls this method -- and `Player.Shoot`, which is what uses its answer.  The
//      property is backed by `GameSettings`, i.e. the game's own settings store, so
//      arming it is a real settings change and it is treated as one below.
//
// THE THREE DECISIONS THIS MODULE ADDS, AND NOTHING ELSE:
//
//   * target the CLOSEST damageable enemy in range (`--autoaim closest`);
//   * target the enemy with the MOST HEALTH in range (`--autoaim highesthp`);
//   * never target something that cannot be damaged.
//
// THE INVULNERABILITY RULE IS THE CLIENT'S OWN AND NOT A LIST OF MAGIC NUMBERS.
// `Entity.Effects.Unhittable()` (`Objects/Components/StatusEffects.cs:150`) is the
// predicate the client itself uses when it decides a health bar is drawn as
// invulnerable, and it answers true for `Invincible`, `Invulnerable` and `Cutscene`.
// Asking it means the module cannot disagree with the client about what "cannot be
// damaged" means, and it keeps being right if the game ever adds a fourth.
//
// WHAT IT DELIBERATELY DOES NOT DO.
//
//   * It does not use the client's enemy-priority table (`GetEnemyPriority`).  That
//     table ranks enemies by their `Exp` string and returns zero for anything it does
//     not recognise, which would silently drop enemies the operator asked to target.
//     The two modes here are the literal rules the operator asked for.
//   * It does not aim through its own geometry or lead its own projectiles -- see above.
//   * It does not run at all unless the launcher passed `--autoaim`.  The replacement is
//     published on every run so the plumbing is exercised, but the native side only aims
//     MinHook's `jmp` at it when the switch is set.
//
// FAILURE IS SILENT TO THE GAME AND LOUD IN THE LOG.  Every path that cannot reach the
// original calls through instead, anything thrown is caught and logged once, and the
// worst case is the client's own cone-limited selection -- never a suppressed call and
// never an exception escaping into a frame.

using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Drelay.Bootstrap
{
    public static class AutoAim
    {
        // ------------------------------------------------------------------
        // The two modes, and the spellings the operator may use.
        // ------------------------------------------------------------------

        internal const int ModeOff = 0;
        internal const int ModeClosest = 1;
        internal const int ModeHighestHealth = 2;

        /// <summary>
        /// The target `FindTarget` returns nothing for is chosen with no radius at all: this
        /// is the value `AimbotSystem.CalculateWeaponRange` itself returns for a blank or
        /// unreadable weapon (`Game/AimbotSystem.cs:172`), and the client's own range for
        /// that case is this, scaled and clamped by the same two constants below.
        /// </summary>
        private const float DefaultWeaponRange = 5.0f;

        /// <summary>The two constants in `AimbotSystem`'s own selection range (`:293-294`).</summary>
        private const float RangeScale = 1.15f;
        private const float RangeMin = 1.5f;
        private const float RangeMax = 10.0f;

        private const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        // ------------------------------------------------------------------
        // Configuration and state.
        // ------------------------------------------------------------------

        private static int _mode = ModeOff;
        private static string _modeText = "";
        private static bool _resolved;
        private static string _unresolvedReason;
        private static string _statusLine = "auto-aim: not resolved";

        private static int _loggedEntry;
        private static int _loggedUnresolvedEntry;
        private static int _loggedTrouble;
        private static int _loggedNoTrampoline;
        private static int _loggedFlagForced;
        private static bool _loggedFlagOriginal;

        // ------------------------------------------------------------------
        // The game's own accessors, resolved once on the resolve callback.
        // ------------------------------------------------------------------

        private static IntPtr _getEnemies;        // Enemy[] World.GetEnemies()
        private static Func<object, float> _getX;
        private static Func<object, float> _getY;
        private static Func<object, bool> _getDead;
        private static Func<object, bool> _getNpc;
        private static Func<object, double> _getHealth;
        private static Func<object, double> _getMaxHealth;
        private static Func<object, object> _getEffects;
        private static Func<object, object> _getItems;
        private static IntPtr _unhittable;        // bool StatusEffects.Unhittable()
        private static IntPtr _getItemData;       // ItemData Item.get_Data()
        private static IntPtr _getItemBlank;      // bool Item.get_IsBlank()
        private static IntPtr _weaponRange;       // float AimbotSystem.CalculateWeaponRange(EquipmentData)
        private static Type _equipmentDataType;
        private static IntPtr _getAimbotFlag;     // bool Prefs.get_enable_aimbot()
        private static IntPtr _setAimbotFlag;     // void Prefs.set_enable_aimbot(bool)

        /// <summary>Where the module stands, for the run's own log.</summary>
        public static string Status()
        {
            return _statusLine;
        }

        /// <summary>Why the module is not available; null when it is.</summary>
        public static string UnresolvedReason
        {
            get { return _unresolvedReason; }
        }

        // ==================================================================
        // Resolve
        // ==================================================================

        /// <summary>
        /// Parse the mode and find every accessor the selection needs.
        ///
        /// RETURNS FALSE WITH A REASON RATHER THAN THROWING, for the same reason the
        /// steering pilot does: this is one module inside a hook that has other work to do,
        /// and nothing about it is allowed to cost the frame read, the patch or the game.
        /// The replacement is published either way -- a module that resolved nothing calls
        /// through on every frame, which is exactly what not being installed looks like.
        /// </summary>
        public static bool Resolve(Assembly game)
        {
            try
            {
                _modeText = (Environment.GetEnvironmentVariable("DRELAY_AIM_MODE") ?? "").Trim();
                _mode = ParseMode(_modeText);

                if (_mode == ModeOff)
                {
                    _resolved = true;
                    _statusLine = "auto-aim: off"
                                  + (_modeText.Length == 0 ? " (DRELAY_AIM_MODE unset)"
                                                           : " ('" + _modeText + "' is not a mode)");
                    return true;
                }

                if (game == null)
                {
                    _unresolvedReason = "the game assembly is not loaded";
                    _statusLine = "auto-aim: NOT available -- " + _unresolvedReason;
                    return false;
                }

                Type worldType = game.GetType(ManagedArmingProbe.WorldTypeName);
                Type playerType = game.GetType(ManagedArmingProbe.PlayerTypeName);
                Type aimbotType = game.GetType("DarzasDominion.Darza.Game.AimbotSystem");
                Type prefsType = game.GetType("DarzasDominion.Data.Prefs");
                _equipmentDataType = game.GetType("DarzaCore.Data.Items.EquipmentData");

                if (worldType == null || playerType == null || aimbotType == null
                    || prefsType == null || _equipmentDataType == null)
                {
                    _unresolvedReason = "type(s) not found:"
                        + (worldType == null ? " World" : "")
                        + (playerType == null ? " Player" : "")
                        + (aimbotType == null ? " AimbotSystem" : "")
                        + (prefsType == null ? " Prefs" : "")
                        + (_equipmentDataType == null ? " EquipmentData" : "");
                    _statusLine = "auto-aim: NOT available -- " + _unresolvedReason;
                    return false;
                }

                // `GetEnemies` is the same call the client's own selection makes, so the
                // module sees exactly the set of enemies the client does -- no second
                // notion of "what is in the world" to drift from it.
                MethodInfo getEnemies = worldType.GetMethod(
                    "GetEnemies", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getEnemies == null)
                {
                    _unresolvedReason = "World.GetEnemies() not found";
                    _statusLine = "auto-aim: NOT available -- " + _unresolvedReason;
                    return false;
                }
                Type enemyType = getEnemies.ReturnType.GetElementType();
                _getEnemies = getEnemies.MethodHandle.GetFunctionPointer();

                FieldInfo positionField = FieldLookup.Find(enemyType, out _, "Position");
                FieldInfo positionX = positionField != null ? positionField.FieldType.GetField("X") : null;
                FieldInfo positionY = positionField != null ? positionField.FieldType.GetField("Y") : null;
                FieldInfo deadField = FieldLookup.Find(enemyType, out _, "Dead");
                FieldInfo npcField = FieldLookup.Find(enemyType, out _, "Npc");
                FieldInfo healthField = FieldLookup.Find(enemyType, out _, "Health");
                FieldInfo maxHealthField = FieldLookup.Find(enemyType, out _, "MaxHealth");
                FieldInfo effectsField = FieldLookup.Find(enemyType, out _, "Effects");

                string missing = "";
                if (positionField == null || positionX == null || positionY == null)
                {
                    missing += " Position.X/Y";
                }
                if (deadField == null) { missing += " Dead"; }
                if (npcField == null) { missing += " Npc"; }
                if (healthField == null) { missing += " Health"; }
                // `MaxHealth` is read for the evidence only -- the selection is on current
                // health -- so its absence is a gap in the log line and not a refusal.
                if (effectsField == null) { missing += " Effects"; }
                if (missing.Length != 0)
                {
                    _unresolvedReason = "field(s) not found on " + enemyType.Name + ":" + missing;
                    _statusLine = "auto-aim: NOT available -- " + _unresolvedReason;
                    return false;
                }

                MethodInfo unhittable = effectsField.FieldType.GetMethod(
                    "Unhittable", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (unhittable == null)
                {
                    _unresolvedReason = effectsField.FieldType.Name + ".Unhittable() not found";
                    _statusLine = "auto-aim: NOT available -- " + _unresolvedReason;
                    return false;
                }

                // THE WEAPON, FOR THE ONE THING THE CLIENT'S OWN RANGE DOES THAT A
                // CONSTANT CANNOT: a melee weapon and a long staff have ranges an order of
                // magnitude apart, so aiming at a target the equipped weapon cannot reach
                // would stop the player attacking the one beside them. `Player.Items` is a
                // field on `Character`, and `Item.Data`/`Item.IsBlank` are properties --
                // the same three lookups `AimbotSystem`'s own range helper makes.
                FieldInfo itemsField = FieldLookup.Find(playerType, out _, "Items");
                if (itemsField == null)
                {
                    _unresolvedReason = "Player.Items not found";
                    _statusLine = "auto-aim: NOT available -- " + _unresolvedReason;
                    return false;
                }
                Type itemType = itemsField.FieldType.GetElementType();
                PropertyInfo itemData = itemType != null ? itemType.GetProperty("Data", Any) : null;
                PropertyInfo itemBlank = itemType != null ? itemType.GetProperty("IsBlank", Any) : null;
                MethodInfo weaponRange = aimbotType.GetMethod(
                    "CalculateWeaponRange", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { _equipmentDataType }, null);
                if (itemType == null || itemData == null || itemBlank == null || weaponRange == null)
                {
                    _unresolvedReason = "the weapon chain is incomplete (Item."
                        + (itemData == null ? "Data" : "")
                        + (itemBlank == null ? (itemData == null ? " and " : "") + "IsBlank" : "")
                        + (weaponRange == null ? (itemData == null || itemBlank == null ? " and " : "")
                                               + "AimbotSystem.CalculateWeaponRange" : "")
                        + " not found)";
                    _statusLine = "auto-aim: NOT available -- " + _unresolvedReason;
                    return false;
                }

                PropertyInfo aimbotFlag = prefsType.GetProperty("enable_aimbot", Any);
                MethodInfo aimbotFlagGet = aimbotFlag != null ? aimbotFlag.GetGetMethod(true) : null;
                MethodInfo aimbotFlagSet = aimbotFlag != null ? aimbotFlag.GetSetMethod(true) : null;
                if (aimbotFlagGet == null || aimbotFlagSet == null)
                {
                    _unresolvedReason = "Prefs.enable_aimbot is not a readable/writable property";
                    _statusLine = "auto-aim: NOT available -- " + _unresolvedReason;
                    return false;
                }

                // THE DELEGATES, NOT `FieldInfo.GetValue`. A reflective read on the frame
                // path boxes, and Phase 2 measured what that costs; `FieldAccessor` emits
                // one `ldfld`-shaped delegate per accessor instead. The method handles below
                // are raw calls because a delegate cannot take an `object` receiver for a
                // method declared on a game type the hook cannot name -- the same reason
                // `CollisionObservation` takes its receiver as `object`.
                _getX = FieldAccessor.NestedFloat(positionField, positionX);
                _getY = FieldAccessor.NestedFloat(positionField, positionY);
                _getDead = FieldAccessor.Bool(deadField);
                _getNpc = FieldAccessor.Bool(npcField);
                _getHealth = FieldAccessor.Double(healthField);
                _getMaxHealth = maxHealthField != null ? FieldAccessor.Double(maxHealthField) : null;
                _getEffects = FieldAccessor.Reference(effectsField);
                _getItems = FieldAccessor.Reference(itemsField);
                _unhittable = unhittable.MethodHandle.GetFunctionPointer();
                _getItemData = itemData.GetGetMethod(true).MethodHandle.GetFunctionPointer();
                _getItemBlank = itemBlank.GetGetMethod(true).MethodHandle.GetFunctionPointer();
                _weaponRange = weaponRange.MethodHandle.GetFunctionPointer();
                _getAimbotFlag = aimbotFlagGet.MethodHandle.GetFunctionPointer();
                _setAimbotFlag = aimbotFlagSet.MethodHandle.GetFunctionPointer();

                _resolved = true;
                _statusLine = "auto-aim: " + ModeName(_mode)
                              + " over every damageable enemy within the equipped weapon's range"
                              + " (Player.AimAssist is not consulted; that is the cone this replaces)";
                return true;
            }
            catch (Exception ex)
            {
                _unresolvedReason = "resolving the module threw: " + HookEntry.FirstFrames(ex);
                _statusLine = "auto-aim: NOT available -- " + _unresolvedReason;
                return false;
            }
        }

        private static int ParseMode(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return ModeOff;
            }
            switch (text.Trim().ToLowerInvariant())
            {
                case "off":
                case "none":
                    return ModeOff;
                case "closest":
                case "close":
                case "nearest":
                    return ModeClosest;
                case "highesthp":
                case "highest-hp":
                case "highest":
                case "maxhp":
                case "max-hp":
                    return ModeHighestHealth;
                default:
                    return ModeOff;
            }
        }

        private static string ModeName(int mode)
        {
            switch (mode)
            {
                case ModeClosest: return "closest";
                case ModeHighestHealth: return "highest-hp";
                default: return "off";
            }
        }

        /// <summary>
        /// Log what was resolved, once, on the resolve callback. The mode alone is not
        /// enough: "highest-hp" reads identically whether the range came from the equipped
        /// weapon or from the fallback, and those two aim at different things.
        /// </summary>
        public static void LogPlan()
        {
            HookEntry.AppendLog("INFO", "auto-aim plan: " + _statusLine);
            if (_mode != ModeOff && _resolved)
            {
                HookEntry.AppendLog("INFO", "auto-aim: the game's own AimbotSystem.UpdateTarget calls "
                                           + "AimbotSystem.FindTarget once per World.Update; this "
                                           + "replacement answers that call, so the client's own "
                                           + "leading, projectile model and target highlight are "
                                           + "unchanged");
            }
            HookEntry.FlushLog();
        }

        // ==================================================================
        // The managed replacement
        // ==================================================================

        /// <summary>
        /// `AimbotSystem.FindTarget(Player, World)`, replaced.
        ///
        /// THE SIGNATURE IS THE ABI AND NOT A STYLE CHOICE. The target is `public static` and
        /// takes two managed references and returns one, so on x64 the arguments arrive in
        /// RCX and RDX and the answer leaves in RAX. Taking them as `object` is the same
        /// registers with a cast the JIT would have done anyway, and it is what lets this
        /// method read the player and the enemies through managed accessors instead of
        /// reinterpreting addresses -- the route `CollisionObservation.HitCharacterReplacement`
        /// already takes for its receiver.
        ///
        /// `NoInlining` because the published entry point is this method's own code: a JIT
        /// that inlined it into its caller would leave `GetFunctionPointer` pointing at a
        /// method nobody enters.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object FindTargetReplacement(object player, object world)
        {
            if (_mode == ModeOff)
            {
                return CallOriginal(player, world);
            }

            if (!_resolved)
            {
                // ENTERED AND CANNOT ACT, WHICH IS A DIFFERENT FACT FROM NOT BEING ENTERED.
                // The launcher refuses an unknown mode, so on a real session this means the
                // *build* moved under the hook: a field renamed, a type gone. It is also the
                // line the injection suite's own run 13 asserts on, because the synthetic
                // host is not the game and resolves none of it -- which is what makes
                // "patched and entered" measurable without an AimbotSystem to select from.
                // The reason is named either way, so a session that aims at nothing is not
                // left looking like a module that decided there was no target.
                if (Interlocked.Exchange(ref _loggedUnresolvedEntry, 1) == 0)
                {
                    HookEntry.AppendLog("ERROR", "auto-aim: the replacement was entered, but the "
                                                + "module did not resolve (" + (_unresolvedReason
                                                ?? "no reason recorded")
                                                + "); the client's own selection runs instead");
                    HookEntry.FlushLog();
                }
                return CallOriginal(player, world);
            }

            try
            {
                // THE FLAG IS RE-ASSERTED HERE AND NOT ONLY AT RESOLVE. It is the game's own
                // setting, and the options menu writes it back from its own control, so a
                // session that opens that menu while the client's stored value was off would
                // clear it -- and with it the only call that ever reaches this method. The
                // check is one call and the write happens only when it is actually off.
                EnsureAimbotFlag();

                object chosen = Choose(player, world);
                if (Interlocked.Exchange(ref _loggedEntry, 1) == 0)
                {
                    // ONE LINE, ONCE, AND IT CARRIES THE FIRST CHOICE RATHER THAN ONLY THE
                    // FACT THAT THE REPLACEMENT RAN. "entered" alone cannot be told apart
                    // from "entered and chose nothing every frame", and the first choice's
                    // health is the number that says the module is reading the enemy it
                    // thinks it is. This is also the only place `MaxHealth` is read: the
                    // selection itself is on current health, so a target line that shows
                    // `143/143` rather than `143/600` is what tells those two apart.
                    string detail;
                    if (chosen == null)
                    {
                        detail = "no damageable enemy was inside the weapon's range on that frame";
                    }
                    else
                    {
                        double health = _getHealth(chosen);
                        string max = _getMaxHealth == null
                            ? "?"
                            : _getMaxHealth(chosen).ToString("0.#", CultureInfo.InvariantCulture);
                        detail = "the first target is at " + health.ToString("0.#", CultureInfo.InvariantCulture)
                                 + "/" + max + " health, "
                                 + _getX(chosen).ToString("0.##", CultureInfo.InvariantCulture) + ","
                                 + _getY(chosen).ToString("0.##", CultureInfo.InvariantCulture);
                    }
                    HookEntry.AppendLog("INFO", "auto-aim: the replacement answered its first call "
                                               + "in " + ModeName(_mode) + " mode; " + detail);
                    HookEntry.FlushLog();
                }
                return chosen;
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _loggedTrouble, 1) == 0)
                {
                    HookEntry.AppendLog("ERROR", "auto-aim: selecting a target threw; the client's "
                                                + "own selection runs instead from here on -- "
                                                + HookEntry.FirstFrames(ex));
                    HookEntry.FlushLog();
                }
                return CallOriginal(player, world);
            }
        }

        /// <summary>
        /// Continue into the real method, through MinHook's trampoline.
        ///
        /// A ZERO TRAMPOLINE MEANS THE HOOK WAS ENABLED WITHOUT THE BOOTSTRAP'S ADDRESS BEING
        /// PUBLISHED FIRST, which the native side orders explicitly. Returning null rather
        /// than calling nothing is the honest answer: "no target" is a state the game already
        /// handles on every frame with nothing in range.
        /// </summary>
        private static object CallOriginal(object player, object world)
        {
            long trampoline = Marshal.ReadInt64(HookEntry.SharedBlockPointer,
                                                GeneratedLayout.Off_managedAimTrampoline);
            if (trampoline == 0)
            {
                if (Interlocked.Exchange(ref _loggedNoTrampoline, 1) == 0)
                {
                    HookEntry.AppendLog("ERROR", "auto-aim: the replacement ran with no trampoline "
                                                + "published; the client's selection cannot run, so "
                                                + "no target is chosen on this frame");
                    HookEntry.FlushLog();
                }
                return null;
            }
            unsafe
            {
                return ((delegate* managed<object, object, object>)(IntPtr)trampoline)(player, world);
            }
        }

        // ==================================================================
        // The selection
        // ==================================================================

        /// <summary>
        /// The rule, applied to the client's own enemy list.
        ///
        /// NO CONE, NO PRIORITY TABLE, NO SHOT GEOMETRY -- the filters are the four that
        /// follow from "target something that can be damaged and can be reached":
        /// not null, not dead, not an NPC, not un-hittable, and within the equipped weapon's
        /// range. Everything else is the score.
        /// </summary>
        private static object Choose(object player, object world)
        {
            if (player == null || world == null || _getEnemies == IntPtr.Zero)
            {
                return null;
            }

            // `GetEnemies` caches its array on the world and rebuilds it only when the
            // enemy dictionary changes, so this is the same allocation the client's own
            // selection makes once per frame and not a second one.
            object raw;
            unsafe
            {
                raw = ((delegate* managed<object, object>)(IntPtr)_getEnemies)(world);
            }
            object[] enemies = raw as object[];
            if (enemies == null || enemies.Length == 0)
            {
                return null;
            }

            float px = _getX(player);
            float py = _getY(player);
            float range = WeaponRange(player);
            float rangeSq = range * range;

            object best = null;
            float bestDistanceSq = float.MaxValue;
            double bestHealth = double.MinValue;

            for (int i = 0; i < enemies.Length; i++)
            {
                object enemy = enemies[i];
                if (enemy == null) { continue; }
                if (_getDead(enemy)) { continue; }
                if (_getNpc(enemy)) { continue; }
                if (Unhittable(enemy)) { continue; }

                float dx = _getX(enemy) - px;
                float dy = _getY(enemy) - py;
                float d2 = dx * dx + dy * dy;
                // Written this way so a NaN coordinate -- which no frame has produced yet --
                // fails the test instead of passing it.
                if (!(d2 <= rangeSq)) { continue; }

                if (_mode == ModeClosest)
                {
                    if (d2 < bestDistanceSq)
                    {
                        bestDistanceSq = d2;
                        best = enemy;
                    }
                }
                else
                {
                    double hp = _getHealth(enemy);
                    if (hp > bestHealth || (hp == bestHealth && d2 < bestDistanceSq))
                    {
                        bestHealth = hp;
                        bestDistanceSq = d2;
                        best = enemy;
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// `Effects.Unhittable()` -- the client's own "this cannot be damaged" predicate.
        ///
        /// A MISSING EFFECTS OBJECT IS TREATED AS HITTABLE. `Entity` creates its
        /// `StatusEffects` in its constructor, so a null one means an object that is being
        /// torn down; refusing to aim at it and refusing to aim at everything would be the
        /// same choice made twice, and the cheap one is to let the other filters decide.
        /// </summary>
        private static bool Unhittable(object enemy)
        {
            object effects = _getEffects(enemy);
            if (effects == null)
            {
                return false;
            }
            unsafe
            {
                return ((delegate* managed<object, bool>)(IntPtr)_unhittable)(effects);
            }
        }

        /// <summary>
        /// The client's own selection radius: `clamp(CalculateWeaponRange(weapon) * 1.15,
        /// 1.5, 10)` (`Game/AimbotSystem.cs:293-294`, and `:392` for the same three numbers
        /// around the same call).
        ///
        /// THE SAME FUNCTION IS CALLED RATHER THAN ITS RESULT RE-DERIVED. It samples every
        /// projectile's position over its lifetime, so it already knows about wavy, orbiting,
        /// boomerang and accelerating weapons -- and reimplementing that here is exactly how
        /// this module would come to disagree with the client about whether a target is
        /// reachable.
        /// </summary>
        private static float WeaponRange(object player)
        {
            float raw = DefaultWeaponRange;
            try
            {
                object[] items = _getItems(player) as object[];
                object weapon = (items != null && items.Length > 0) ? items[0] : null;
                if (weapon != null)
                {
                    bool blank;
                    unsafe
                    {
                        blank = ((delegate* managed<object, bool>)(IntPtr)_getItemBlank)(weapon);
                    }
                    if (!blank)
                    {
                        object data;
                        unsafe
                        {
                            data = ((delegate* managed<object, object>)(IntPtr)_getItemData)(weapon);
                        }
                        if (data != null && _equipmentDataType.IsInstanceOfType(data))
                        {
                            unsafe
                            {
                                raw = ((delegate* managed<object, float>)(IntPtr)_weaponRange)(data);
                            }
                        }
                    }
                }
            }
            catch
            {
                // A weapon that cannot be read costs the range, not the frame: the fallback
                // is the client's own answer for "no weapon", and the log line below says
                // when it was used.
                raw = DefaultWeaponRange;
            }

            float range = raw * RangeScale;
            if (!(range > RangeMin)) { range = RangeMin; }
            if (range > RangeMax) { range = RangeMax; }
            return range;
        }

        /// <summary>
        /// Keep `Prefs.enable_aimbot` set while the module is on.
        ///
        /// THIS IS A REAL SETTING CHANGE AND IT IS NOT HIDDEN. The property is
        /// `GameSettings`-backed (`DarzasDominion.Data.Prefs`), so it is what the game's own
        /// options menu shows and what it persists -- a session that runs auto-aim leaves the
        /// in-game toggle on. There is no way around it: `Player.Shoot` checks that property
        /// before it ever asks for a target, so with it clear the module is unreachable by
        /// construction. The original value is read and logged at resolve, and the write
        /// happens only when the value is off, so the setting is not rewritten every frame.
        /// </summary>
        private static void EnsureAimbotFlag()
        {
            bool current;
            unsafe
            {
                current = ((delegate* managed<bool>)(IntPtr)_getAimbotFlag)();
            }
            if (!_loggedFlagOriginal)
            {
                _loggedFlagOriginal = true;
                HookEntry.AppendLog("INFO", "auto-aim: the in-game aimbot setting was "
                                           + (current ? "ON" : "OFF")
                                           + " when this module first ran; it is set ON while "
                                           + "auto-aim is enabled, and the game persists that "
                                           + "setting (there is no shutdown callback to restore it)");
                HookEntry.FlushLog();
            }
            if (current)
            {
                return;
            }
            unsafe
            {
                ((delegate* managed<bool, void>)(IntPtr)_setAimbotFlag)(true);
            }
            if (Interlocked.Exchange(ref _loggedFlagForced, 1) == 0)
            {
                HookEntry.AppendLog("INFO", "auto-aim: re-armed the in-game aimbot setting, which "
                                           + "something in the client had cleared; the module is "
                                           + "unreachable while it is off");
                HookEntry.FlushLog();
            }
        }

        /// <summary>
        /// One frame's worth, called from the managed frame replacement.
        ///
        /// WHY THE MODULE NEEDS A FRAME PATH AT ALL, AND WHY `--autoaim` IMPLIES ONE.
        ///
        /// `EnsureAimbotFlag` runs here because the write it makes at *resolve* time does
        /// not survive, and that is a fact about the client rather than a precaution.
        /// `Prefs.enable_aimbot` is `GameSettings`-backed, and the game loads that store
        /// from disk once, in `DarzaWindow`'s constructor (`Graphics/DarzaWindow.cs:759`,
        /// `GameSettings.Load(FileVersions.SettingsPath)`), loading every key over whatever
        /// was there. The resolve callback runs **500 ms after the startup hook signals**
        /// (`DrelayInstall.cpp:1161`), i.e. before the entry point has even reached that
        /// constructor -- so a write made there is on the right store and is then replaced
        /// by the file's value.
        ///
        /// The consequence of getting this wrong is the quiet kind: with the flag clear,
        /// `AimbotSystem.UpdateTarget` never calls `FindTarget`, so the method is never
        /// compiled, so the detour is never matched, so nothing in the log says anything was
        /// wrong. A module that waits for a call that only happens once the flag is set has
        /// to set the flag from somewhere that does not.
        ///
        /// The frame replacement is that somewhere: it is on the game's own thread, at the
        /// top of every frame, before `World.Update` -- which is where `UpdateTarget` sits --
        /// runs. Writing from there cannot race the game's own reads of the same store, which
        /// is why the watcher thread is not allowed to do it: `GameSettings.setBool` is a
        /// `Dictionary` indexer write, and a concurrent `TryGetValue` on the game thread is
        /// the classic way to hang a process that shares no lock.
        /// </summary>
        internal static void OnFrame()
        {
            if (_mode == ModeOff || !_resolved)
            {
                return;
            }
            try
            {
                EnsureAimbotFlag();
            }
            catch
            {
                // The frame path must not throw for this. `EnsureAimbotFlag` reads and
                // writes two game members; anything it can do wrong is a refused write,
                // never the frame.
            }
        }

        // ==================================================================
        // Publishing
        // ==================================================================

        /// <summary>
        /// Publish the replacement's entry point, and warm everything it calls.
        ///
        /// CALLED ON EVERY RUN, WHETHER OR NOT THE OPERATOR ASKED FOR AUTO-AIM -- the same
        /// discipline as the steering pilot's, and for the same reason: what the switch
        /// decides is whether MinHook's `jmp` is aimed here at all, and publishing on every
        /// run means the shared slot, the native dispatch and the trampoline handshake are
        /// exercised by runs that have nothing to lose by it.
        ///
        /// THE WARM-UP IS THE POINT. A method first compiled from inside a patched game
        /// method is a JIT in the middle of a frame, so every method on the selection path
        /// is prepared before the patch can go live.
        /// </summary>
        public static void Publish()
        {
            MethodInfo method = typeof(AutoAim).GetMethod(
                nameof(FindTargetReplacement), BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                HookEntry.AppendLog("ERROR", "auto-aim: the replacement was not found by name; "
                                            + "nothing published and the target will not be patched");
                HookEntry.FlushLog();
                return;
            }

            PrepareSelectionPath();
            RuntimeHelpers.PrepareMethod(method.MethodHandle);

            IntPtr entry = method.MethodHandle.GetFunctionPointer();
            Marshal.WriteInt64(HookEntry.SharedBlockPointer,
                               GeneratedLayout.Off_managedAimReplacement, entry.ToInt64());
            HookEntry.AppendLog("INFO", "managed auto-aim replacement published at 0x"
                                        + entry.ToInt64().ToString("X")
                                        + "; the native side aims MinHook's jmp at it only when "
                                        + "--autoaim asked for it");
            HookEntry.FlushLog();
        }

        /// <summary>
        /// `PrepareMethod` on every method the selection path calls.
        ///
        /// MATCHED BY NAME ACROSS ALL OVERLOADS, deliberately: `Type.GetMethod(name, flags)`
        /// throws `AmbiguousMatchException` the moment a second overload appears, and
        /// `SteeringPilot` lost every steering run to exactly that when a `WriteInput`
        /// overload was added. Iterating and comparing names cannot be ambiguous. The
        /// accessor delegates are absent from this list because `FieldAccessor` compiles each
        /// of them with `CreateDelegate` at resolve, where it is already too late to be a
        /// first-time cost inside a frame.
        /// </summary>
        private static void PrepareSelectionPath()
        {
            string[] names =
            {
                "FindTargetReplacement", "CallOriginal", "Choose", "Unhittable", "WeaponRange",
                "EnsureAimbotFlag", "ParseMode", "ModeName", "Status", "Resolve", "Publish",
                "OnFrame",
            };
            MethodInfo[] methods = typeof(AutoAim).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                bool wanted = false;
                for (int n = 0; n < names.Length; n++)
                {
                    if (names[n] == methods[i].Name)
                    {
                        wanted = true;
                        break;
                    }
                }
                if (!wanted)
                {
                    continue;
                }
                try
                {
                    RuntimeHelpers.PrepareMethod(methods[i].MethodHandle);
                }
                catch
                {
                    // A callee that is generic or otherwise unprepared is skipped; the
                    // publish log that follows is what says the path was warmed at all.
                }
            }
        }
    }
}
