// EnemyReader.cs -- the live entity list, as `DodgeSnapshot.Bodies`.
//
// =====================================================================================
// WHAT THIS IS, AND THE ONE DEFECT IT CLOSES
// =====================================================================================
//
// `DodgeSnapshot.Bodies` has existed since the solver was written, `DodgeSolver.Admit` has
// implemented rule 7 against it ("a candidate that sweeps an enemy body is refused, unless the
// player is already inside that body"), and nothing has ever put an entry in it. `DodgeAdapter`'s
// own header used to say so in as many words -- *"RULE 7 (ENEMY BODIES) IS VACUOUS"* -- and the
// `bodies=0` in every report was the honest mark of it.
//
// The consequence was not theoretical. `PathPlanner.DangerAt` iterates `snapshot.Threats`, which is
// bullets, so **an enemy standing in the open was free space as far as the path search was
// concerned**: the shortest route to a goal beyond it went through it, and on the frames a ranged
// mob was not currently shooting -- which is most of them -- the path deliberately walked into its
// muzzle. That is the operator's sentence, and it is a defect in the existing dodge rather than in
// a future farmer. See `PLAN-ENEMY-THREATS.md` section 2.
//
// =====================================================================================
// IT IS ITS OWN CLASS RATHER THAN A SHARED HELPER WITH `AutoAim`
// =====================================================================================
//
// Two reasons, and the first is substantive rather than architectural:
//
//   1. **THE FILTERS ARE DIFFERENT QUESTIONS.** `AutoAim` excludes `Dead`, `Npc` and
//      `Effects.Unhittable()` because it is asking "can I damage this?". The dodge is asking "can
//      this damage me?", and the answers differ for the same enemy: an `Invulnerable` or
//      `Invincible` enemy cannot be hit and can usually still hit back, while a `Cutscene` one can
//      do neither. So this reader filters on `Dead` and `Npc` ONLY and deliberately does not call
//      `Unhittable()`. Reusing `AutoAim`'s predicate would make the dodge refuse to walk past an
//      enemy it cannot shoot -- which is backwards, and would be a live regression the moment the
//      auto-aim module was switched off.
//   2. **COUPLING.** `AutoAim`'s state and its resolve path are that module's business, and it
//      resolves `GetEnemies` only when a mode is selected. A second consumer reaching into it would
//      make the auto-aim mode a switch that changes the dodge.
//
// =====================================================================================
// FAILURE DISCIPLINE: IT IS THE HOUSE RULE, AND HERE IT IS ALSO THE SAFETY PROPERTY
// =====================================================================================
//
// `Resolve` returns false with a NAMED reason, `Fill` does nothing, the report prints
// `enemies=unresolved`, and every existing behaviour is byte-identical to a hook that was never
// asked to read enemies. **A hook that cannot read enemies must not dodge differently from one that
// was never asked to.** The reader is therefore optional in exactly the way the collision grid and
// the player's health are: missing it degrades rule 7 back to its old vacuous self and nothing else.
//
// Nothing here allocates per frame. The delegate the resolver builds is an emitted `ldfld`, the
// enemy array is the world's own cached array (see `Fill`), and the only per-frame work is a loop
// over that array with a squared-distance compare.

using System;
using System.Globalization;
using System.Reflection;

namespace Drelay.Bootstrap
{
    public static class EnemyReader
    {
        private const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        /// <summary>Set by the operator to switch the whole reader off. On by default.</summary>
        private const string EnvEnemies = "DRELAY_DODGE_ENEMIES";

        // --- Resolved once ------------------------------------------------------------------

        private static bool _resolved;
        private static string _unresolvedReason = "not attempted";
        private static string _rejectionReason;   // non-null only when the feature was switched OFF

        /// <summary>`Enemy[] World.GetEnemies()`, as a raw managed function pointer.</summary>
        private static IntPtr _getEnemies;

        /// <summary>
        /// `World.Shared`, resolved separately from the enemy list. See <see cref="ResolveWorld"/>.
        /// </summary>
        private static Func<object> _getWorld;

        private static Func<object, float> _getX;
        private static Func<object, float> _getY;
        private static Func<object, bool> _getDead;
        private static Func<object, bool> _getNpc;
        private static Func<object, float> _getRadius;

        /// <summary>
        /// `Health` and `MaxHealth`, read for the WORLD LOG and for nothing else.
        ///
        /// THEY ARE OPTIONAL AND THEY ARE A DIFFERENT QUESTION FROM RULE 7'S. The dodge has never
        /// needed an enemy's health -- it reasons about geometry -- and `PLAN-ENEMY-THREATS.md`'s
        /// reader deliberately resolves the smallest set that answers "can this damage me". The world
        /// log (`PLAN-AUTOFARM.md` section 6) wants health because "the 8 nearest" is how the
        /// operator's session names a boss later, and a boss is a health number. So they are resolved
        /// here, where the enumeration already is, and a build where they moved costs a column.
        ///
        /// THE NAMES ARE `AutoAim`'s, WHICH HAS READ BOTH LIVE: `Health` and `MaxHealth` on the
        /// element type. See `AutoAim.Resolve`.
        /// </summary>
        private static Func<object, float> _getHealth;
        private static Func<object, float> _getMaxHealth;

        /// <summary>
        /// `GameObject.GameId`, read for the FARMER's target list and not for the body list.
        ///
        /// IT IS OPTIONAL FOR THE BODIES AND REQUIRED FOR THE FARMER, and that asymmetry is the same
        /// `FieldLookup` lesson the header of that file records: `GameId` is declared on
        /// `GameObject`, four levels up, so a lookup restricted to the element type would not find
        /// it. Rule 7 has no use for an identity -- it compares geometry every frame -- so a build
        /// where the field moved must not lose the body list over it. The farmer's stickiness rule
        /// IS an identity ("keep the current target while it is alive and in range"), and a list of
        /// targets all carrying id zero would make every enemy the same target, so the farm half is
        /// switched off by name instead of silently collapsing.
        /// </summary>
        private static Func<object, int> _getGameId;

        private static string _gameIdName;

        /// <summary>
        /// `GameObject.Data`, then `GameObjectData.Name` and `.Class` -- THE CRITTER FILTER'S INPUT.
        ///
        /// THE OPERATOR'S FIRST LIVE FARM SESSION IS WHY THIS EXISTS: the character walked at the
        /// realm's ambient critters, which die when it walks over them. A critter is an ordinary
        /// `Enemy` to `GetEnemies()`, an ordinary body to rule 7, and a valid target to every rule
        /// the farmer had, so no geometric test separates it from a mob. The client's own name for
        /// it does, and the name is two field reads away: `Enemy : Entity : GameObject` carries
        /// `Data`, and `GameObjectData` carries `Name` and `Class` as public fields.
        ///
        /// THEY ARE OPTIONAL LIKE EVERY OTHER ACCESSOR HERE. A build where `Data` moved loses the
        /// name (and the ignore list becomes a no-op) but keeps the body list and the farmer, which
        /// is the same discipline `Health` and `GameId` follow.
        /// </summary>
        private static Func<object, object> _getData;
        private static Func<object, object> _getName;
        private static Func<object, object> _getClass;
        private static Func<object, bool> _getNoDie;
        private static string _nameSource;

        /// <summary>
        /// `Entity.Effects` and `StatusEffects.Unhittable()` -- the client's own "cannot be damaged"
        /// predicate, and the CRITTER FILTER's second half. See `FarmTarget.Unhittable`.
        ///
        /// IT IS THE SAME PAIR `AutoAim` RESOLVES (its `_getEffects`/`_unhittable`), and the same
        /// argument applies here: a target that cannot be damaged is not something to farm. The
        /// dodge deliberately does NOT use it (an invulnerable enemy can still shoot at the player,
        /// so rule 7 must still avoid it), which is why it belongs to the farm half only.
        /// </summary>
        private static Func<object, object> _getEffects;
        private static IntPtr _unhittable;
        private static string _unhittableSource = "not resolved";

        /// <summary>The element type's own name, for the farm half's failure line.</summary>
        private static string _enemyTypeName;

        /// <summary>True when the farm half of the reader has everything it needs.</summary>
        private static bool _farmUsable;

        /// <summary>
        /// True when the game's own radius could not be read, so `DefaultPlayerRadius` is standing in
        /// for it.
        ///
        /// IT IS COUNTED BECAUSE A SILENT ZERO RADIUS IS A BODY YOU CAN WALK THROUGH. A body with no
        /// radius is a point, and a point is a body rule 7 almost never refuses -- which is the exact
        /// defect this file exists to close, wearing the reader's own name. `PLAN-ENEMY-THREATS.md`
        /// section 4.1: "if `Radius` does not resolve, use `DodgeConfig.DefaultPlayerRadius` and
        /// COUNT THE FALLBACK".
        /// </summary>
        private static bool _radiusFallback;

        // --- Per-run counters, for the report ------------------------------------------------

        private static long _framesFilled;
        private static long _bodiesCulled;
        private static long _bodiesDropped;      // null, dead or NPC
        private static long _bodiesRadiusFallback;
        private static long _skippedFrames;

        /// <summary>Enemies that reached the FARMER's list, summed over the run.</summary>
        private static long _farmTargetsAdded;

        /// <summary>Where the reader stands, for the run's own log.</summary>
        public static string Status()
        {
            if (_rejectionReason != null)
            {
                return "enemy bodies: off (" + _rejectionReason + ")";
            }
            if (!_resolved)
            {
                return "enemy bodies: NOT available -- " + _unresolvedReason;
            }
            return "enemy bodies: World.GetEnemies()"
                   + (_radiusFallback
                        ? ", Radius did NOT resolve -- DefaultPlayerRadius stands in and the"
                          + " fallback is COUNTED (a silent zero radius is a body to walk through)"
                        : "")
                   + (_farmUsable
                        ? ", farm targets use " + (_gameIdName ?? "GameId")
                        : ", FARM TARGETS UNAVAILABLE: no GameId field on " + (_enemyTypeName ?? "?")
                          + " or its bases");
        }

        /// <summary>Why the reader is not available; null when it is.</summary>
        public static string UnresolvedReason
        {
            get { return _rejectionReason ?? _unresolvedReason; }
        }

        /// <summary>
        /// The farm half's filter, on its own line: which members the name filter and the
        /// cannot-be-damaged rule resolved against. `PLAN-AUTOFARM.md` section 14.5.
        ///
        /// IT IS A SEPARATE LINE BECAUSE `Status()` IS ALREADY AT THE 256-BYTE EDGE, and because the
        /// two halves of the filter can resolve independently: a build where `Data` moved still has
        /// the `Unhittable()` rule, and the log has to be able to say which one is active.
        /// </summary>
        public static string FarmFilterStatus()
        {
            return "farm filter: name " + _nameSource + " | cannot-be-damaged " + _unhittableSource;
        }

        // ===================================================================================
        // Resolve
        // ===================================================================================

        /// <summary>
        /// Finds every accessor the body list needs. Nothing new is required of the game: the same
        /// call and the same four fields `AutoAim` has exercised live since `AUTOAIM.md`'s session.
        ///
        /// THE ARRAY ELEMENT TYPE IS TAKEN FROM THE METHOD'S OWN RETURN TYPE, not from a name. That
        /// is the one place this reader is better than a hand-written list of field names would be:
        /// `GetEnemies` returns the game's element type, so the fields are looked up on the type the
        /// method actually returns rather than on a type name that would have to be right.
        /// </summary>
        public static bool Resolve(Assembly game)
        {
            string switchValue = Environment.GetEnvironmentVariable(EnvEnemies);
            if (switchValue == "0")
            {
                _rejectionReason = EnvEnemies + "=0";
                return false;
            }

            try
            {
                if (game == null)
                {
                    _unresolvedReason = "the game assembly is not loaded";
                    return false;
                }

                Type worldType = game.GetType(ManagedArmingProbe.WorldTypeName);
                if (worldType == null)
                {
                    _unresolvedReason = ManagedArmingProbe.WorldTypeName + " not found";
                    return false;
                }

                // `GetEnemies` is the same call the client's own selection makes and the same one
                // `AutoAim.Resolve` resolves, so this reader sees exactly the set of enemies the
                // client does -- there is no second notion of "what is in the world" to drift.
                MethodInfo getEnemies = worldType.GetMethod(
                    "GetEnemies", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getEnemies == null)
                {
                    _unresolvedReason = "World.GetEnemies() not found";
                    return false;
                }

                Type enemyType = getEnemies.ReturnType.GetElementType();
                if (enemyType == null)
                {
                    _unresolvedReason = "World.GetEnemies() does not return an array";
                    return false;
                }

                FieldInfo positionField = FieldLookup.Find(enemyType, out _, "Position");
                FieldInfo positionX = positionField != null ? positionField.FieldType.GetField("X") : null;
                FieldInfo positionY = positionField != null ? positionField.FieldType.GetField("Y") : null;
                FieldInfo deadField = FieldLookup.Find(enemyType, out _, "Dead");
                FieldInfo npcField = FieldLookup.Find(enemyType, out _, "Npc");

                string missing = "";
                if (positionField == null || positionX == null || positionY == null)
                {
                    missing += " Position.X/Y";
                }
                if (deadField == null) { missing += " Dead"; }
                if (npcField == null) { missing += " Npc"; }
                if (missing.Length != 0)
                {
                    _unresolvedReason = "field(s) not found on " + enemyType.Name + ":" + missing;
                    return false;
                }

                // `GameObject.Radius` -- the same field the dodge already reads for the player and
                // the same one the game's own hit test adds to a bullet's. It is OPTIONAL rather than
                // required, and the fallback is counted rather than silent: see `_radiusFallback`.
                FieldInfo radiusField = FindRadius(enemyType);
                _radiusFallback = radiusField == null;

                // THE DELEGATES, NOT `FieldInfo.GetValue`. A reflective read on the frame path boxes,
                // and this project has paid for that once already (`FieldAccessor`'s own header). The
                // `GetEnemies` call is a raw pointer because a delegate cannot take an `object`
                // receiver for a method declared on a game type the hook cannot name.
                _getX = FieldAccessor.NestedFloat(positionField, positionX);
                _getY = FieldAccessor.NestedFloat(positionField, positionY);
                _getDead = FieldAccessor.Bool(deadField);
                _getNpc = FieldAccessor.Bool(npcField);
                _getRadius = radiusField != null ? FieldAccessor.Float(radiusField) : null;

                // THE WORLD LOG'S TWO OPTIONAL FIELDS. See `_getHealth`: a missing one costs the log
                // a column and nothing else, which is the same discipline every other accessor here
                // follows.
                FieldInfo healthField = FieldLookup.Find(enemyType, out _, "Health");
                FieldInfo maxHealthField = FieldLookup.Find(enemyType, out _, "MaxHealth");
                _getHealth = healthField != null ? FieldAccessor.Float(healthField) : null;
                _getMaxHealth = maxHealthField != null ? FieldAccessor.Float(maxHealthField) : null;

                // THE FARMER'S IDENTITY FIELD. Optional for the bodies, required for the farm
                // half -- see `_getGameId`.
                FieldInfo gameIdField = FieldLookup.Find(enemyType, out _gameIdName, "GameId", "_gameId", "ObjectId");
                _getGameId = gameIdField != null ? FieldAccessor.Int(gameIdField) : null;
                _farmUsable = gameIdField != null;
                _enemyTypeName = enemyType.Name;

                // THE CRITTER FILTER'S TWO STRINGS, on the same optional terms. `Data` is on
                // `GameObject`, so it needs the hierarchy walk `GameId` and `Radius` do.
                FieldInfo dataField = FieldLookup.Find(enemyType, out _, "Data");
                if (dataField != null)
                {
                    _getData = FieldAccessor.Reference(dataField);
                    Type dataType = dataField.FieldType;
                    FieldInfo nameField = dataType.GetField("Name", Any);
                    FieldInfo classField = dataType.GetField("Class", Any);
                    _getName = nameField != null ? FieldAccessor.Reference(nameField) : null;
                    _getClass = classField != null ? FieldAccessor.Reference(classField) : null;
                    // `NoDie` IS ON `EntityData`, A BASE OF THE DATA TYPE, so it needs the hierarchy
                    // walk `FieldLookup` exists for -- a `declaredOnly` lookup on `EnemyData` finds
                    // nothing. It is the second half of the critter rule; see `FarmTarget.NoDie`.
                    FieldInfo noDieField = FieldLookup.Find(dataType, out _, "NoDie");
                    _getNoDie = noDieField != null ? FieldAccessor.Bool(noDieField) : null;
                    _nameSource = (nameField != null ? dataType.Name + ".Name" : "no Name")
                                  + (classField != null ? " / " + dataType.Name + ".Class" : " / no Class")
                                  + (noDieField != null ? " / " + noDieField.DeclaringType.Name + ".NoDie"
                                                        : " / no NoDie");
                }
                else
                {
                    _nameSource = "no GameObject.Data";
                }

                // THE CRITTER FILTER'S RULE, from the client's own predicate. See `FarmTarget.Unhittable`.
                FieldInfo effectsField = FieldLookup.Find(enemyType, out _, "Effects");
                if (effectsField != null && effectsField.FieldType != null)
                {
                    MethodInfo unhittable = effectsField.FieldType.GetMethod(
                        "Unhittable", BindingFlags.Public | BindingFlags.Instance, null,
                        Type.EmptyTypes, null);
                    if (unhittable != null)
                    {
                        _getEffects = FieldAccessor.Reference(effectsField);
                        _unhittable = unhittable.MethodHandle.GetFunctionPointer();
                        _unhittableSource = effectsField.FieldType.Name + ".Unhittable()";
                    }
                    else
                    {
                        _unhittableSource = effectsField.FieldType.Name + ".Unhittable() not found";
                    }
                }
                else
                {
                    _unhittableSource = "no Entity.Effects";
                }

                _getEnemies = getEnemies.MethodHandle.GetFunctionPointer();

                _resolved = true;
                return true;
            }
            catch (Exception ex)
            {
                _unresolvedReason = ex.GetType().Name + ": " + ex.Message;
                _resolved = false;
                _getEnemies = IntPtr.Zero;
                return false;
            }
        }

        /// <summary>
        /// `Radius`, up the hierarchy, the same walk `DodgeAdapter.FindRadius` makes for the player.
        /// It is declared on `GameObject` on the real client, so a lookup restricted to the element
        /// type would not find it -- the exact mistake `FieldLookup`'s header records being made
        /// about `GameId`.
        /// </summary>
        private static FieldInfo FindRadius(Type enemyType)
        {
            for (Type t = enemyType; t != null && t != typeof(object); t = t.BaseType)
            {
                FieldInfo f = t.GetField("Radius", Any | BindingFlags.DeclaredOnly);
                if (f != null)
                {
                    return f;
                }
            }
            return null;
        }

        // ===================================================================================
        // Fill
        // ===================================================================================

        /// <summary>
        /// Adds this frame's enemy bodies to <paramref name="s"/>, with the emitter radius already
        /// computed.
        ///
        /// IT DOES NOTHING AT ALL WHEN THE READER DID NOT RESOLVE OR THE FEATURE IS OFF, which is the
        /// failure discipline in one line: the snapshot's body list is what `Clear()` left it as --
        /// empty -- and rule 7 is exactly as vacuous as it was before this file existed.
        ///
        /// THE PLAYER'S OWN RADIUS IS AN INPUT RATHER THAN A READ. The adapter has it (it reads
        /// `Player.Radius` every frame and defaults it), and the emitter radius is
        /// `enemy.Radius + player.Radius + pad` -- so passing it keeps the arithmetic in one place
        /// and keeps this reader from needing a `DodgeConfig` at all.
        ///
        /// THE CULL IS THE POINT OF THE ENTRY LOOP AND IT IS A DISTANCE, NOT A COUNT. A full realm
        /// holds hundreds of enemies; `EnemyCullRadius` (12 units, about twice the longest horizon
        /// the planner reasons over) removes everything the solver could ever cost and nothing it
        /// could not. It is measured from the PLAYER and not from the goal, because the question is
        /// "can this enemy reach the decision", and the decision is where the player is.
        /// </summary>
        public static void Fill(DodgeSnapshot s, DodgeConfig config, float px, float py, float playerRadius)
        {
            // ONE ENTRY LOOP, TWO GATED OUTPUTS. `PLAN-AUTOFARM.md` section 4.2 asks for the
            // enumeration to be shared rather than duplicated, so this is the same walk of the same
            // `World.GetEnemies()` array; what differs is which list each enemy lands in and which
            // radius cut it survived.
            bool forBodies = config.EnemyBodies;
            bool forFarm = config.FarmMode && _farmUsable;

            if (!_resolved || _getEnemies == IntPtr.Zero || s == null || (!forBodies && !forFarm))
            {
                if (_resolved)
                {
                    _skippedFrames++;
                }
                return;
            }

            object raw;
            unsafe
            {
                // `World` caches the array and rebuilds it only when the enemy dictionary changes, so
                // this is the same allocation the client's own selection makes once per frame and not
                // a second one. See `AutoAim.Choose`'s identical call.
                raw = ((delegate* managed<object, object>)(IntPtr)_getEnemies)(WorldObject());
            }

            object[] enemies = raw as object[];
            if (enemies == null || enemies.Length == 0)
            {
                _framesFilled++;
                return;
            }

            float cull = config.EnemyCullRadius;
            float cull2 = cull * cull;
            // THE FARMER'S OWN CUT, AND IT IS A DIFFERENT NUMBER ON PURPOSE. See `FarmTarget` for why
            // the two lists cannot share a radius: the body cut is a frame-cost bound on what rule 7
            // and the planner may reason about, and the farm cut is "how far away is an enemy still
            // worth walking to". A single number would make `DRELAY_DODGE_ENEMY_CULL` silently limit
            // the farmer's reach, or make a long farm range inflate rule 7's list.
            float farm = config.FarmEngageRadius;
            float farm2 = farm * farm;
            _framesFilled++;

            for (int i = 0; i < enemies.Length; i++)
            {
                object enemy = enemies[i];
                if (enemy == null) { _bodiesDropped++; continue; }
                if (_getDead(enemy)) { _bodiesDropped++; continue; }
                if (_getNpc(enemy)) { _bodiesDropped++; continue; }

                float ex = _getX(enemy);
                float ey = _getY(enemy);
                float dx = ex - px;
                float dy = ey - py;
                float d2 = (dx * dx) + (dy * dy);
                // Written so a NaN coordinate fails the test instead of passing it -- the same shape
                // `AutoAim.Choose` uses for the range test, and for the same reason.
                bool inBodyRange = forBodies && d2 <= cull2;
                bool inFarmRange = forFarm && d2 <= farm2;
                if (!inBodyRange && !inFarmRange)
                {
                    _bodiesCulled++;
                    continue;
                }

                if (inFarmRange)
                {
                    // THE IDENTITY IS READ FOR THE FARMER AND NOT FOR THE BODY, and it is read here
                    // rather than in the farmer so the enumeration stays in one place. The farmer's
                    // filter is this reader's own (`Dead`, `Npc`) plus the range cut; what it adds is
                    // the stickiness rule, which is a fact about the farmer's own state.
                    //
                    // THE NAME AND CLASS ARE THE CRITTER FILTER'S INPUT -- see `FarmTarget.Name`.
                    // They are read even when no ignore list is configured, because the farmer's
                    // line reports the name of what it is walking at and that is how an operator
                    // finds a name to ignore. `Data` is null-guarded because a missing data object
                    // would otherwise be a null dereference inside a patched frame.
                    string name = null;
                    string className = null;
                    bool noDie = false;
                    if (_getData != null && (_getName != null || _getClass != null || _getNoDie != null))
                    {
                        object data = _getData(enemy);
                        if (data != null)
                        {
                            if (_getName != null) { name = _getName(data) as string; }
                            if (_getClass != null) { className = _getClass(data) as string; }
                            if (_getNoDie != null) { noDie = _getNoDie(data); }
                        }
                    }
                    // THE CANNOT-BE-DAMAGED PREDICATE. Null effects are treated as hittable, the
                    // same rule `AutoAim.Unhittable` follows: a null here means an object being torn
                    // down, not an invulnerable one.
                    bool unhittable = false;
                    if (_getEffects != null && _unhittable != IntPtr.Zero)
                    {
                        object effects = _getEffects(enemy);
                        if (effects != null)
                        {
                            unsafe
                            {
                                unhittable = ((delegate* managed<object, bool>)(IntPtr)_unhittable)(effects);
                            }
                        }
                    }
                    s.FarmTargets.Add(new FarmTarget
                    {
                        GameId = _getGameId(enemy),
                        X = ex,
                        Y = ey,
                        Name = name,
                        Class = className,
                        Unhittable = unhittable,
                        NoDie = noDie,
                    });
                    _farmTargetsAdded++;
                }

                if (!inBodyRange)
                {
                    continue;
                }

                float radius;
                if (_getRadius != null)
                {
                    radius = _getRadius(enemy);
                }
                else
                {
                    radius = config.DefaultPlayerRadius;
                    _bodiesRadiusFallback++;
                }
                if (radius <= 0.0f)
                {
                    // A ZERO RADIUS IS NOT "NO BODY", IT IS A BODY WITH NO SIZE. The game's own
                    // default is 0.25 and a zero here means the field did not carry a value; the
                    // fallback is the same one the player's own radius uses.
                    radius = config.DefaultPlayerRadius;
                    _bodiesRadiusFallback++;
                }

                s.Bodies.Add(new EntityBody
                {
                    X = ex,
                    Y = ey,
                    Radius = radius,
                    // THE EMITTER RADIUS, COMPUTED ONCE HERE RATHER THAN IN THE PLANNER, so the
                    // body carries both of its own radii and neither consumer re-derives one from
                    // the other's inputs. See `EntityBody.DangerRadius`.
                    DangerRadius = radius + playerRadius + config.EnemyDangerPadUnits,
                });
            }
        }

        /// <summary>
        /// One enemy, as the WORLD LOG wants it. `PLAN-AUTOFARM.md` section 6.1.
        ///
        /// IT IS ITS OWN TYPE RATHER THAN `EntityBody`, and the difference is the point: `EntityBody`
        /// is geometry rule 7 tests, and this is a row of evidence for a file. Sharing one type would
        /// make every future addition to the log a change to the solver's input.
        /// </summary>
        public struct EnemySample
        {
            public int GameId;
            public float X;
            public float Y;
            public float Radius;
            public float Health;
            public float MaxHealth;
            public bool Npc;
            public bool Dead;
        }

        /// <summary>
        /// Copies up to <paramref name="max"/> enemies into <paramref name="into"/>, nearest first.
        /// Returns the number written; zero when the reader did not resolve.
        ///
        /// IT IS THE SAME ENUMERATION AND A DIFFERENT FILTER, which is `PLAN-AUTOFARM.md` section
        /// 4.2's instruction applied to the log: "factor the enumeration into the `EnemyReader` ...
        /// and have both modules read it, keeping each module's own filter". The bodies drop dead and
        /// NPC enemies and cull at `EnemyCullRadius`, because rule 7 must not reason about a corpse;
        /// **the log drops nothing**, because "there were four corpses in view" is exactly the kind of
        /// thing the operator's session is for. It sorts by distance and keeps `max`.
        ///
        /// IT IS CALLED FROM THE DIAGNOSTIC PATH ONLY. When the world log is off this method is never
        /// entered, so its `List` sort costs the frame nothing -- the same accounting
        /// `PLAN-ENEMY-THREATS.md` made for `EnemyReader.Fill` itself.
        /// </summary>
        public static int SampleEnemies(System.Collections.Generic.List<EnemySample> into, int max,
                                        float px, float py, out int total)
        {
            total = 0;
            if (into == null)
            {
                return 0;
            }
            into.Clear();
            if (!_resolved || _getEnemies == IntPtr.Zero || max <= 0)
            {
                return 0;
            }

            object raw;
            unsafe
            {
                raw = ((delegate* managed<object, object>)(IntPtr)_getEnemies)(WorldObject());
            }
            object[] enemies = raw as object[];
            if (enemies == null || enemies.Length == 0)
            {
                return 0;
            }

            for (int i = 0; i < enemies.Length; i++)
            {
                object enemy = enemies[i];
                if (enemy == null)
                {
                    continue;
                }
                into.Add(new EnemySample
                {
                    GameId = _getGameId != null ? _getGameId(enemy) : 0,
                    X = _getX(enemy),
                    Y = _getY(enemy),
                    Radius = _getRadius != null ? _getRadius(enemy) : 0.0f,
                    Health = _getHealth != null ? _getHealth(enemy) : 0.0f,
                    MaxHealth = _getMaxHealth != null ? _getMaxHealth(enemy) : 0.0f,
                    Npc = _getNpc(enemy),
                    Dead = _getDead(enemy),
                });
            }

            // NEAREST FIRST, AND THE SORT IS THE WHOLE OF "the 8 nearest". A log that wrote the
            // first eight the world happened to return would be a sample of the array's order, which
            // nothing guarantees -- `World` rebuilds it from a dictionary. The comparer is a closure
            // over the player's position and it allocates once per call; the log runs at a few hertz
            // on a diagnostic path, so that is the right trade for not carrying an index array.
            //
            // `total` IS THE WHOLE POPULATION AND NOT THE SAMPLE, because `PLAN-AUTOFARM.md` section
            // 6.1 asks for "count, and the 8 nearest" as two different facts: how many mobs are in
            // the realm is what says whether the realm is worth farming, and the eight rows are what
            // names them.
            total = into.Count;
            if (into.Count > 1)
            {
                into.Sort((a, b) =>
                {
                    float adx = a.X - px;
                    float ady = a.Y - py;
                    float bdx = b.X - px;
                    float bdy = b.Y - py;
                    return ((adx * adx) + (ady * ady)).CompareTo((bdx * bdx) + (bdy * bdy));
                });
            }
            if (into.Count > max)
            {
                into.RemoveRange(max, into.Count - max);
            }
            return into.Count;
        }

        /// <summary>True when the reader resolved and the world log may ask it for rows.</summary>
        public static bool Available => _resolved && _getEnemies != IntPtr.Zero;

        /// <summary>
        /// The world, for the `GetEnemies` call.
        ///
        /// READ THROUGH THE SAME STATIC FIELD EVERY OTHER PART OF THE HOOK USES (`World.Shared`),
        /// resolved here rather than passed in, so this reader is self-contained: the adapter hands
        /// it the frame's numbers and nothing else.
        /// </summary>
        private static object WorldObject()
        {
            return _getWorld == null ? null : _getWorld();
        }

        /// <summary>
        /// The `World.Shared` accessor, resolved by the caller that already has the type.
        ///
        /// IT IS HERE RATHER THAN IN `Resolve` BECAUSE THE TWO ARE DIFFERENT QUESTIONS: `Resolve`
        /// finds the enemy list on the world TYPE, and this finds the world INSTANCE. Keeping them
        /// apart means a hook whose `World.Shared` never resolves still reports the reader's own
        /// status honestly instead of failing for a reason that belongs to the caller.
        /// </summary>
        public static void ResolveWorld(FieldInfo sharedField)
        {
            _getWorld = sharedField != null ? FieldAccessor.Static(sharedField) : null;
        }

        // ===================================================================================
        // The report
        // ===================================================================================

        /// <summary>Zeroes the per-run counters. Called where the report's own are zeroed.</summary>
        public static void ResetCounters()
        {
            _framesFilled = 0;
            _bodiesCulled = 0;
            _bodiesDropped = 0;
            _bodiesRadiusFallback = 0;
            _skippedFrames = 0;
            _farmTargetsAdded = 0;
        }

        /// <summary>
        /// The reader's own numbers, for the dodge report line.
        ///
        /// `PLAN-ENEMY-THREATS.md`'s E1 is exactly this string: `bodies=0` becomes `bodies=N` on a
        /// live frame with enemies in view, and `enemies=unresolved` says by name when resolution
        /// failed, because a zero that cannot say why it is zero is the defect this whole plan
        /// exists to close. `skipped` is the count of frames where the list was never asked for
        /// (`DRELAY_DODGE_ENEMIES=0`), which is the third way this can legitimately read zero.
        /// </summary>
        public static string Describe()
        {
            if (_rejectionReason != null)
            {
                return "enemies=off culled=0 fallbackRadius=0";
            }
            if (!_resolved)
            {
                return "enemies=unresolved culled=0 fallbackRadius=0";
            }
            return string.Format(CultureInfo.InvariantCulture,
                "enemies=live frames={0} culled={1} dropped={2} fallbackRadius={3} skipped={4} "
                + "farmTargets={5}{6}",
                _framesFilled, _bodiesCulled, _bodiesDropped, _bodiesRadiusFallback, _skippedFrames,
                _farmTargetsAdded,
                // THE FARM HALF'S OWN NAMED FAILURE, and it is separate from the body half's because
                // the two can succeed independently: a build where `GameId` moved still has a
                // working rule 7, and a run with `DRELAY_DODGE_ENEMIES=0` still has working farm
                // targets. `culled` counts enemies neither list wanted.
                _farmUsable ? "" : " farm=unavailable(no GameId)");
        }
    }
}
