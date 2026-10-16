// TrajectorySampler.cs -- Deliverable 2.2: call the game's own trajectory, do not
// reimplement it.
//
// THE ONE IDEA THIS FILE IS BUILT AROUND.
//
// `ProjDesc.GetPosition(int time, float sin, float cos, out float x, out float y,
// int gameId)` is virtual, with one override per trajectory family -- straight, wavy,
// parametric, boomerang, orbiting, curving, accelerating, wall-bouncing, homing.  The
// hook CALLS it (DODGE-ALGORITHM.md section 4.1) and the decisive reason is the third
// one that section gives: calling it makes the dangerous-position model and the game's
// own collision test exactly consistent by construction, so a dodge cannot disagree
// with the engine about where a bullet will be.  That is the failure mode that makes
// dodges feel haunted, and reimplementing the families is the research project that
// section rules out.
//
// HOW A HOOK WITH NO COMPILE-TIME REFERENCE TO THE GAME CALLS IT ANYWAY.
//
// Reflection's `MethodInfo.Invoke` would work and is unusable: it boxes the two `out`
// floats, allocates an argument array and re-validates the signature on every one of
// twenty-one samples per bullet per frame.  So the call is emitted ONCE, as a
// `DynamicMethod` that does a single `callvirt` and stores the two results into a
// caller-owned `float[2]`.  That is the same technique StartupHook.cs already uses for
// `ldsflda` and `ldflda`, for the same reason: the runtime can answer a question about a
// type the hook cannot name, but only if it is asked once.
//
// `callvirt`, not `call`.  The method looked up is the base `ProjDesc`'s, and virtual
// dispatch is what reaches the concrete family -- a `call` would run the abstract
// declaration's body, which does not exist, or the base's, which would predict every
// bullet as straight.  That failure is silent and is exactly the one the linearity gate
// exists to catch, so this file reports the fraction of straight bullets it sees rather
// than assuming the dispatch worked.
//
// SAMPLING, AND WHY THE STEP MATTERS MORE THAN THE MODEL.
//
// DODGE-ALGORITHM.md section 4.2: sampling a path at fractions of its lifetime silently
// aliases anything periodic, so the grid is fixed at 30 ms and bounded at a 600 ms
// horizon -- twenty-one points, one of them at the live anchor.  The step is a sampling
// rate and not a game constant, which is why it needs no adaptation.
//
// AND EVERY SAMPLE SET IS ANCHORED ON THE LIVE POSITION.  The predicted polyline is
// translated by `live - predicted(now)` before anything looks at it (section 4.4 rule 1),
// which makes model error relative rather than cumulative: the point at delta zero is
// exact by construction and the error at the end of the horizon is only however wrong
// the model is over 600 ms.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;

namespace Drelay.Bootstrap
{
    public static class TrajectorySampler
    {
        /// <summary>The game's trajectory descriptor base, by the name DODGE-ALGORITHM.md section 4.1 cites.</summary>
        public const string DescTypeName = "DarzaCore.Data.ProjDesc";

        /// <summary>Sampling step, in milliseconds.  Section 4.2's fixed grid.</summary>
        public const int StepMs = 30;

        /// <summary>Horizon, in milliseconds.  Section 4.3's derived working value.</summary>
        public const int HorizonMs = 600;

        /// <summary>Thirty-millisecond steps to a 600 ms horizon, plus the anchor.</summary>
        public const int SampleCount = (HorizonMs / StepMs) + 1;

        private delegate void SampleFn(object desc, int time, float sin, float cos,
                                       float[] outPair, int gameId);

        private static bool _resolved;
        private static string _unresolvedReason = "not attempted";

        private static SampleFn _sample;

        // Emitted accessors.  This is the file whose p99 the boxing showed up in: 21 samples
        // per bullet per frame is where a GetValue per field stops being free.
        private static Func<object, object> _getDesc;
        private static Func<object, float> _getSin;
        private static Func<object, float> _getCos;
        private static Func<object, int> _getAge;
        private static Func<object, int> _getStartTime;
        private static Func<object, float> _getProjectileX;
        private static Func<object, float> _getProjectileY;
        private static Func<object, float> _getRadius;
        private static Func<object> _getWorld;
        private static Func<object, object> _getPlayer;
        private static Func<object, float> _getPlayerX;
        private static Func<object, float> _getPlayerY;
        private static Func<object> _getClock;
        private static Func<object, float> _getLogicTotal;
        private static FieldInfo _descField;
        private static FieldInfo _sinField;
        private static FieldInfo _cosField;
        private static FieldInfo _ageField;
        private static FieldInfo _positionField;
        private static FieldInfo _pointX;
        private static FieldInfo _pointY;
        private static FieldInfo _radiusField;

        // THE TWO FLAGS THAT DECIDE WHETHER A PROJECTILE IS A THREAT TO THE PLAYER AT ALL.
        // `Projectile.HitsPlayer` is the client's own dispatch condition -- `Hit()` sends a
        // `HitsPlayer == false` projectile to `HitEnemy()` and never to `HitCharacter()` -- and the
        // client's spawn path sets it to `this is Enemy` (`Entity.cs:1600`). So a projectile the
        // local player fired can never reach the player, and neither can an ally's. Both are
        // otherwise indistinguishable from an enemy bullet here: same type, same radius, same
        // position, and they are born at the muzzle, which is inside the player's collision disc.
        //
        // THE MEASUREMENT THAT PUT THEM HERE. On `20261015-151232-p5424`, 932 of 1210 tracked
        // bullets first appear within 0.3 units of the player and fly away -- a three-shot spread
        // every 8 frames -- and they were **51.8 % of every threat record in the session**, 959 of
        // its 971 "already touching" verdicts, and the cause of 255 of its 1140 moves. A player who
        // was merely shooting had the dodge taking the wheel around its own muzzle.
        private static FieldInfo _hitsPlayerField;
        private static FieldInfo _localSeedField;
        private static Func<object, bool> _getHitsPlayer;
        private static Func<object, bool> _getLocalSeed;

        /// <summary>What the resolution log calls the two exclusion flags, found or MISSING.</summary>
        private static string _cannotHitNames = "not attempted";

        /// <summary>
        /// Projectiles the sampler declined because the client says they cannot hit a player.
        ///
        /// IT COUNTS OBSERVATIONS, NOT PROJECTILES, AND EVERY CONSUMER TAKES A DELTA. Three callers
        /// walk the same frame's projectile list -- the sensor's own predictor, the adapter and the
        /// recorder -- so one own shot is declined three times here and this number is roughly three
        /// times the number of own shots fired. That is harmless and intended, because no consumer
        /// reads it as a total: each wraps its own loop and reports the difference, which is that
        /// caller's own count for that frame.
        /// </summary>
        public static long Declined;

        /// <summary>
        /// True when the exclusion is actually in force. False means `HitsPlayer` did not resolve and
        /// the threat set still contains the player's own shots -- a degraded run, and one the
        /// reports say so on rather than leaving to be inferred from a count.
        /// </summary>
        public static bool CannotHitFilterResolved => _getHitsPlayer != null || _getLocalSeed != null;

        // THE TIME BASE, WHICH IS THE ONE THING THE CLIENT DOES NOT STORE.  The host carries
        // an AgeMs per projectile; the client does not, and computes the age itself as
        // 	ime.LogicTotal - _startTime (CLIENT-FINDINGS.md section 4, Projectile.cs:670).
        // So when there is no age field, the age is DERIVED from the game's own clock:
        // DarzasDominion.Graphics.DarzaWindow.Time is a public static ClientTime, and
        // ClientTime.LogicTotal is a public float in milliseconds.
        private static FieldInfo _startTimeField;
        private static FieldInfo _clientTimeField;
        private static FieldInfo _logicTotalField;
        private static bool _deriveAgeFromClock;

        // The player, for the intersection test that turns a sampled polyline into a
        // prediction Deliverable 2.3 can compare against.
        private static FieldInfo _worldSharedField;
        private static FieldInfo _worldPlayerField;
        private static FieldInfo _playerPositionField;
        private static FieldInfo _playerX;
        private static FieldInfo _playerY;
        private static FieldInfo _playerRadiusField;

        /// <summary>Reads `World.Player.Radius`. Null when the field was not found.</summary>
        private static Func<object, float> _getPlayerRadius;

        private static readonly float[] Xs = new float[SampleCount];
        private static readonly float[] Ys = new float[SampleCount];
        private static readonly float[] Pair = new float[2];

        private static long _framesSampled;
        private static long _bulletsSampled;
        private static long _straight;
        private static long _curved;
        private static long _lastCostUs;
        private static long _maxCostUs;
        private static long _nextLogFrame = 150;

        /// <summary>Bucketed per-frame sampler cost, kept apart from the read's.</summary>
        public static readonly CostHistogram Cost = new CostHistogram();

        public static bool Resolved => _resolved;
        public static string UnresolvedReason => _unresolvedReason;

        /// <summary>Fraction of sampled bullets whose trajectory may be projected. Zero when nothing has been sampled.</summary>
        public static double StraightFraction
        {
            get
            {
                long total = _straight + _curved;
                return total == 0 ? 0.0 : (double)_straight / total;
            }
        }

        /// <summary>
        /// The linearity counts, published so the phase's fifth number can ride on the
        /// same line as the other four.
        ///
        /// WHY THEY MOVED.  `LogSummary` held these and was never called -- the sensor has
        /// no orderly shutdown to call it from, because the operator kills the game rather
        /// than letting it exit -- so the straight fraction, which is one of the numbers
        /// Phase 2 exists to produce, was reachable only by finding the sampler's own
        /// periodic line.  A number that lives in a different place than the numbers it is
        /// read with gets read wrongly; that is the same lesson PHASE2-SCOPE.md section 4.9
        /// records for the four.
        /// </summary>
        public static long StraightCount => _straight;

        /// <summary>Bullets the gate refused to project; see <see cref="StraightCount"/>.</summary>
        public static long CurvedCount => _curved;

        /// <summary>Bullet sample sets taken so far.</summary>
        public static long BulletsSampled => _bulletsSampled;

        /// <summary>
        /// Resolves the fields and emits the call.  Failure is not fatal: the frame read,
        /// the patch and the threat set all keep working, and the reason is logged.
        /// </summary>
        public static bool Resolve(Assembly game)
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static;
            try
            {
                Type projectileType = game.GetType(SensorRead.ProjectileTypeName);
                Type descType = game.GetType(DescTypeName);
                if (projectileType == null || descType == null)
                {
                    _unresolvedReason = "types not found: Projectile=" + (projectileType != null)
                                        + " " + DescTypeName + "=" + (descType != null);
                    return false;
                }

                string descName;
                string sinName;
                string cosName;
                string ageName;
                string positionName;
                string radiusName;
                _descField = FieldLookup.Find(projectileType, out descName, "Desc", "_desc");
                _sinField = FieldLookup.Find(projectileType, out sinName, "_angleSin", "Sin", "sin");
                _cosField = FieldLookup.Find(projectileType, out cosName, "_angleCos", "Cos", "cos");
                _ageField = FieldLookup.Find(projectileType, out ageName, "AgeMs", "_ageMs", "Age");

                // THE GAME'S CLOCK IS RESOLVED WHETHER OR NOT THE AGE NEEDS IT, and that is a
                // correction rather than tidiness. The clock has two jobs here: it is the fallback
                // for the age (which the real `Projectile` needs, because it stores none), and it
                // is the source of the frame delta the solver's move budget is built from. The
                // first version assembled it only when the age field was missing, so on a build
                // that has an age field the move budget would silently have fallen back to 60 Hz
                // -- right up to the frame where it was not 60 Hz, which is the frame the budget
                // is supposed to stay legal through.
                {
                    string startName = null;
                    string timeName = null;
                    string logicName = null;
                    _startTimeField = FieldLookup.Find(projectileType, out startName,
                                                       "_startTime", "StartTime", "startTime");
                    Type clockType = game.GetType("DarzasDominion.Graphics.ClientTime");
                    _clientTimeField = FieldLookup.Find(game.GetType("DarzasDominion.Graphics.DarzaWindow"),
                                                        out timeName, "Time");
                    if (clockType != null && _clientTimeField != null)
                    {
                        _logicTotalField = FieldLookup.Find(clockType, out logicName, "LogicTotal");
                    }
                    _deriveAgeFromClock = _startTimeField != null && _clientTimeField != null
                                          && _logicTotalField != null;
                    if (_ageField == null)
                    {
                        ageName = _deriveAgeFromClock
                            ? "DERIVED (" + timeName + "." + logicName + " - " + startName + ")"
                            : "MISSING";
                    }
                    else
                    {
                        ageName = ageName + (_deriveAgeFromClock ? " (clock also resolved)" : "");
                    }
                }
                _positionField = FieldLookup.Find(projectileType, out positionName,
                                                  "Position", "ExactPosition", "_position");
                _radiusField = FieldLookup.Find(projectileType, out radiusName,
                                                "Radius", "_radius", "TouchRadius");
                // THE EXCLUSION'S OWN TWO FIELDS. Neither is required: a build without them runs the
                // way every run before this one did, and says so (`hittable-filter MISSING`). What is
                // not allowed is resolving them and then not using them.
                {
                    string hitsName;
                    string localName;
                    _hitsPlayerField = FieldLookup.Find(projectileType, out hitsName, "HitsPlayer");
                    _localSeedField = FieldLookup.Find(projectileType, out localName, "LocalSeedDerived");
                    _getHitsPlayer = _hitsPlayerField != null ? FieldAccessor.Bool(_hitsPlayerField) : null;
                    _getLocalSeed = _localSeedField != null ? FieldAccessor.Bool(_localSeedField) : null;
                    _cannotHitNames = (hitsName ?? "MISSING") + "/" + (localName ?? "MISSING");
                }
                // `_ageField` MAY LEGITIMATELY BE NULL, and the live client is why.  The real
                // `Projectile` stores no age at all -- the client derives it from its own
                // clock -- so an age field is required only when the clock derivation could
                // not be assembled.  The first version of this demanded the field outright,
                // and the run against the real game reported the sampler unavailable with
                // `Desc=True Sin=True Cos=True AgeMs=False Position=True`: every field it
                // needed but one, with the fallback that covers that one sitting unused
                // twelve lines above the check that rejected it.
                if (_descField == null || _sinField == null || _cosField == null
                    || _positionField == null
                    || (_ageField == null && !_deriveAgeFromClock))
                {
                    _unresolvedReason = "Projectile fields not found: Desc=" + (_descField != null)
                                        + " Sin=" + (_sinField != null) + " Cos=" + (_cosField != null)
                                        + " Age=" + (_ageField != null || _deriveAgeFromClock)
                                        + " Position=" + (_positionField != null);
                    return false;
                }
                string pointXName;
                string pointYName;
                _pointX = FieldLookup.Find(_positionField.FieldType, out pointXName, "X");
                _pointY = FieldLookup.Find(_positionField.FieldType, out pointYName, "Y");
                if (_pointX == null || _pointY == null)
                {
                    _unresolvedReason = "the position type has no X/Y";
                    return false;
                }

                // The player's own position AND RADIUS, for the intersection test.  Deliverable 2.3
                // asks whether the sensor had PREDICTED a hit, and a prediction is exactly this: a
                // sampled point inside the hit disc, on a frame before the game's own collision
                // decision fires.
                //
                // THE DISC IS `World.Player.Radius + Radius`, AND THE COMMENT HERE USED TO SAY IT
                // WAS THE PROJECTILE'S RADIUS ALONE, quoting "the game's own test is
                // `DistanceSquared(player.Position) < r*r`". That quotation is wrong -- the client's
                // predicate is `DistanceSquared(World.Player.Position) < (World.Player.Radius +
                // Radius)^2` (CLIENT-MAP.md, `Projectile.cs:1573`) -- and the comment is what made
                // the wrong code beside it look deliberate. **A comment that misquotes the client is
                // worse than no comment: it is a licence to keep the bug.** The radius is resolved
                // here, unconditionally, so that the name is in scope for the resolution log too.
                Type worldType = game.GetType(ManagedArmingProbe.WorldTypeName);
                Type playerType = game.GetType(ManagedArmingProbe.PlayerTypeName);
                string playerRadiusName = null;
                if (worldType != null && playerType != null)
                {
                    string sharedName2;
                    string playerName;
                    string playerPositionName;
                    string playerXName;
                    string playerYName;
                    _playerRadiusField = FieldLookup.Find(playerType, out playerRadiusName,
                                                          "Radius", "_radius", "TouchRadius");
                    _worldSharedField = FieldLookup.Find(worldType, out sharedName2, "Shared", "_shared");
                    _worldPlayerField = FieldLookup.Find(worldType, out playerName, "Player", "_player");
                    _playerPositionField = FieldLookup.Find(playerType, out playerPositionName,
                                                            "Position", "ExactPosition", "_position");
                    if (_playerPositionField != null)
                    {
                        _playerX = FieldLookup.Find(_playerPositionField.FieldType, out playerXName, "X");
                        _playerY = FieldLookup.Find(_playerPositionField.FieldType, out playerYName, "Y");
                    }
                }
                if (_radiusField == null || _worldSharedField == null || _worldPlayerField == null
                    || _playerX == null || _playerY == null)
                {
                    _unresolvedReason = "the intersection test's fields are not all present: Radius="
                                        + (_radiusField != null) + " World.Shared="
                                        + (_worldSharedField != null) + " World.Player="
                                        + (_worldPlayerField != null) + " Player.XY="
                                        + (_playerX != null && _playerY != null);
                    return false;
                }

                MethodInfo getPosition = descType.GetMethod("GetPosition", Any);
                if (getPosition == null)
                {
                    _unresolvedReason = DescTypeName + " has no GetPosition";
                    return false;
                }

                // THE SIGNATURE IS CHECKED, NOT ASSUMED, AND THE CHECK IS THE POINT.
                // This project has three ABI faults on record, every one of them from
                // reading a signature instead of measuring it.  A `callvirt` emitted
                // against a signature that has moved would not fail loudly here -- it
                // would read the wrong slots and produce plausible coordinates, which is
                // the worst possible outcome for a dodge.
                ParameterInfo[] parameters = getPosition.GetParameters();
                if (parameters.Length != 6
                    || parameters[0].ParameterType != typeof(int)
                    || parameters[1].ParameterType != typeof(float)
                    || parameters[2].ParameterType != typeof(float)
                    || parameters[3].ParameterType != typeof(float).MakeByRefType()
                    || parameters[4].ParameterType != typeof(float).MakeByRefType()
                    || parameters[5].ParameterType != typeof(int)
                    || (getPosition.ReturnType != typeof(void) && getPosition.ReturnType != typeof(bool)))
                {
                    _unresolvedReason = "GetPosition's signature is neither "
                                        + "(int,float,float,out float,out float,int)->void nor ->bool; it is "
                                        + Describe(getPosition);
                    return false;
                }

                _getDesc = FieldAccessor.Reference(_descField);
                _getSin = FieldAccessor.Float(_sinField);
                _getCos = FieldAccessor.Float(_cosField);
                _getProjectileX = FieldAccessor.NestedFloat(_positionField, _pointX);
                _getProjectileY = FieldAccessor.NestedFloat(_positionField, _pointY);
                _getRadius = FieldAccessor.Float(_radiusField);
                if (_ageField != null)
                {
                    _getAge = FieldAccessor.Int(_ageField);
                }
                if (_deriveAgeFromClock)
                {
                    _getStartTime = FieldAccessor.Int(_startTimeField);
                    _getClock = FieldAccessor.Static(_clientTimeField);
                    _getLogicTotal = FieldAccessor.Float(_logicTotalField);
                }
                if (_worldSharedField != null)
                {
                    _getWorld = FieldAccessor.Static(_worldSharedField);
                    _getPlayer = FieldAccessor.Reference(_worldPlayerField);
                    _getPlayerX = FieldAccessor.NestedFloat(_playerPositionField, _playerX);
                    _getPlayerY = FieldAccessor.NestedFloat(_playerPositionField, _playerY);
                    if (_playerRadiusField != null)
                    {
                        _getPlayerRadius = FieldAccessor.Float(_playerRadiusField);
                    }
                }
                _sample = EmitSample(getPosition, descType, getPosition.ReturnType == typeof(bool));
                _resolved = true;
                HookEntry.AppendLog("INFO", "trajectory sampler resolved: " + DescTypeName
                                            + ".GetPosition called through a virtual dynamic method, "
                                            + SampleCount + " sample(s) every " + StepMs
                                            + " ms to a " + HorizonMs + " ms horizon; fields "
                                            + descName + ", " + sinName + ", " + cosName + ", "
                                            + ageName + ", " + positionName + "(" + pointXName + "/"
                                            + pointYName + "), " + radiusName
                                            + ", player radius "
                                            + (_getPlayerRadius != null
                                               ? playerRadiusName
                                               : "MISSING (the prediction falls back to 0.25)")
                                            + ", cannot-hit-the-player filter "
                                            + (CannotHitFilterResolved
                                               ? "ON (" + _cannotHitNames + ")"
                                               : "MISSING (" + _cannotHitNames
                                                 + ") -- the threat set WILL contain the player's"
                                                 + " own shots"));
                HookEntry.FlushLog();
                return true;
            }
            catch (Exception ex)
            {
                _unresolvedReason = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static string Describe(MethodInfo method)
        {
            string text = method.ReturnType.Name + " (";
            ParameterInfo[] parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                {
                    text += ",";
                }
                text += parameters[i].ParameterType.Name;
            }
            return text + ")";
        }

        /// <summary>
        /// One `callvirt` and two array stores.  Emitted once, called twenty-one times per
        /// bullet per frame, and the only place this assembly touches a trajectory.
        /// </summary>
        private static SampleFn EmitSample(MethodInfo getPosition, Type descType, bool returnsBool)
        {
            var method = new DynamicMethod(
                "DrelaySampleProjectilePosition",
                typeof(void),
                new[] { typeof(object), typeof(int), typeof(float), typeof(float),
                        typeof(float[]), typeof(int) },
                typeof(TrajectorySampler).Module,
                skipVisibility: true);

            ILGenerator il = method.GetILGenerator();
            LocalBuilder x = il.DeclareLocal(typeof(float));
            LocalBuilder y = il.DeclareLocal(typeof(float));

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, descType);   // the cast is what makes callvirt verifiable
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Ldloca, x);
            il.Emit(OpCodes.Ldloca, y);
            il.Emit(OpCodes.Ldarg, 5);
            il.Emit(OpCodes.Callvirt, getPosition);
            if (returnsBool)
            {
                // THE REAL CLIENT'S GetPosition RETURNS bool, AND THIS IS WHY THE SIGNATURE IS
                // CHECKED RATHER THAN ASSUMED.  DODGE-ALGORITHM.md section 4.1 records the
                // signature as returning void; against the shipped build it is
                // Boolean (Int32,Single,Single,Single&,Single&,Int32), and a callvirt emitted
                // against the assumed shape would leave the return value on the stack -- an
                // unbalanced evaluation stack, which the verifier or the JIT turns into a
                // fault somewhere else entirely.  The value is discarded here because the
                // sampler wants the two coordinates; whether the family considers the shot
                // still alive is not a question this file asks.
                il.Emit(OpCodes.Pop);
            }

            il.Emit(OpCodes.Ldarg, 4);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldloc, x);
            il.Emit(OpCodes.Stelem_R4);
            il.Emit(OpCodes.Ldarg, 4);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldloc, y);
            il.Emit(OpCodes.Stelem_R4);
            il.Emit(OpCodes.Ret);

            return (SampleFn)method.CreateDelegate(typeof(SampleFn));
        }

        /// <summary>
        /// Samples one bullet's path onto the fixed grid and anchors it on the live position.
        ///
        /// THE ANCHOR IS THE POINT (section 4.4 rule 1). The game is asked where the bullet will
        /// be at `age + 30s ms` for twenty-one values of `s`, and the resulting polyline is then
        /// translated by `live - predicted(now)` so that the sample at delta zero agrees with the
        /// bullet's live position exactly. Model error therefore stays **relative** rather than
        /// accumulating across frames: whatever the family is wrong about, it is wrong about by
        /// the same amount here and at the horizon, and the decision never inherits yesterday's
        /// mistake as today's starting point.
        ///
        /// THE RETURN OF `GetPosition` IS KEPT (this file's session-long gripe about the sampler
        /// throwing it away). The real client's signature returns `bool`; the existing caller
        /// discards it because the Phase 2 measurement is about positions. For the solver it is
        /// the honest end-of-shot signal, so `Alive[s]` records it per sample and everything past
        /// the first false is **frozen at that position** rather than extrapolated -- section
        /// 4.4's "freeze past the trace, never extrapolate". A bullet frozen past its own death
        /// paints danger where there is none and would refuse a safe move.
        /// </summary>
        private static void FillSamples(object desc, int age, float sin, float cos,
                                        float liveX, float liveY, Trajectory into)
        {
            int n = SampleCount;
            if (n > Trajectory.MaxSamples)
            {
                n = Trajectory.MaxSamples;
            }

            bool ended = false;
            float lastX = liveX;
            float lastY = liveY;
            for (int s = 0; s < n; s++)
            {
                if (ended)
                {
                    // Frozen: the family has already said the shot is over, so the honest
                    // prediction for every later sample is the position it ended at.
                    into.Xs[s] = lastX;
                    into.Ys[s] = lastY;
                    into.Alive[s] = false;
                    continue;
                }
                _sample(desc, age + (s * StepMs), sin, cos, Pair, 0);
                into.Xs[s] = Pair[0];
                into.Ys[s] = Pair[1];
                lastX = Pair[0];
                lastY = Pair[1];
                into.Alive[s] = true;
            }

            float offsetX = liveX - into.Xs[0];
            float offsetY = liveY - into.Ys[0];
            for (int s = 0; s < n; s++)
            {
                into.Xs[s] += offsetX;
                into.Ys[s] += offsetY;
            }

            into.Count = n;
            into.StepMs = StepMs;
            into.LiveX = liveX;
            into.LiveY = liveY;
            into.Projectable = LinearityGate.IsLinear(into.Xs, into.Ys, n, LinearityGate.DefaultTolerance);
        }

        /// <summary>Fills a caller-owned trajectory from a live projectile object.</summary>
        public static bool FillTrajectory(object projectile, out Trajectory into)
        {
            into = null;
            if (!_resolved || projectile == null)
            {
                return false;
            }

            // THE CLIENT'S OWN EXCLUSION COMES FIRST, BEFORE A SINGLE SAMPLE IS TAKEN, and the
            // ordering is both the fix and half its cost. A projectile the client will never test
            // against the player is not a threat, so it must not be sampled, must not be culled,
            // must not be recorded and must not reach the solver -- and skipping it here is what
            // makes that true for the live decision and the recorded trace with one line, rather
            // than for one of them and not the other. See `_hitsPlayerField` for the measurement.
            if (CannotHitPlayer(projectile))
            {
                return false;
            }

            try
            {
                object desc = _getDesc(projectile);
                if (desc == null)
                {
                    return false;
                }
                float sin = _getSin(projectile);
                float cos = _getCos(projectile);
                float clockLogicTotal = 0.0f;
                if (_deriveAgeFromClock)
                {
                    object clock = _getClock();
                    if (clock != null)
                    {
                        clockLogicTotal = _getLogicTotal(clock);
                    }
                }
                int age = _getAge != null
                    ? _getAge(projectile)
                    : (int)clockLogicTotal - _getStartTime(projectile);

                into = RentTrajectory();
                // IDENTITY IS NOT THIS FILE'S TO READ. `GameId` lives on `GameObject` and
                // `_startTime` on `Projectile`; the sensor resolves both and hands the caller a
                // `ThreatKey`, which is what the recorder writes (see `Trajectory.Key`). Resolving
                // them again here would be a second definition of the identity this project has
                // already been bitten by (HOOK-ARCHITECTURE.md section 5.4), so the key is left
                // untouched for the caller and only the start time -- a diagnostic -- is read.
                into.StartTime = _startTimeField != null ? _getStartTime(projectile) : 0;
                into.Radius = _getRadius(projectile);
                into.HitsPlayer = _getHitsPlayer != null && _getHitsPlayer(projectile);
                into.LocalSeedDerived = _getLocalSeed != null && _getLocalSeed(projectile);
                FillSamples(desc, age, sin, cos,
                            _getProjectileX(projectile), _getProjectileY(projectile), into);
                return true;
            }
            catch (Exception)
            {
                into = null;
                return false;
            }
        }

        /// <summary>
        /// Whether the client would refuse to collide this projectile with the player, ever.
        ///
        /// IT FAILS OPEN, AND THAT IS THE ONLY SAFE DIRECTION. If the fields did not resolve, or a
        /// read throws, the answer is "no, it might be a threat" -- so the worst case of a broken
        /// resolution is the behaviour every run before this one had, and never a threat silently
        /// dropped. The counter (`Declined`) and the resolution line are what make the difference
        /// between "there were no own shots" and "the exclusion is not in force" visible.
        /// </summary>
        private static bool CannotHitPlayer(object projectile)
        {
            if (_getHitsPlayer == null && _getLocalSeed == null)
            {
                return false;
            }
            try
            {
                if (_getHitsPlayer != null && !_getHitsPlayer(projectile))
                {
                    Declined++;
                    return true;
                }
                if (_getLocalSeed != null && _getLocalSeed(projectile))
                {
                    Declined++;
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return false;
        }

        // A small free list, so the recorder can fill a trajectory per bullet per frame without
        // allocating one per bullet per frame. The recorder returns each one when it is done;
        // the list only ever grows to the maximum number of bullets recorded in a frame.
        private static readonly List<Trajectory> Pool = new List<Trajectory>();
        private static int _poolUsed;

        private static Trajectory RentTrajectory()
        {
            if (_poolUsed < Pool.Count)
            {
                Trajectory t = Pool[_poolUsed];
                _poolUsed++;
                return t;
            }
            Trajectory created = new Trajectory();
            Pool.Add(created);
            _poolUsed++;
            return created;
        }

        /// <summary>Returns every rented trajectory to the pool. Called once per frame by the recorder.</summary>
        public static void ReleaseTrajectories()
        {
            _poolUsed = 0;
        }

        /// <summary>One reusable trajectory, for the sampler's own per-frame path.</summary>
        private static readonly Trajectory ScratchTrajectory = new Trajectory();

        /// <summary>
        /// The frame's own delta, in seconds, for `b = speed * frameSeconds`.
        ///
        /// THE GAME'S CLOCK, NOT WALL TIME. `ClientTime.LogicTotal` advances by the client's own
        /// logic delta, and the movement integrator receives that same number; deriving the move
        /// budget from wall-clock time would let a frame hitch grant a larger legal step than the
        /// client actually took, which is the one way a legal-by-construction budget stops being
        /// legal. Falls back to 60 Hz when the clock is unavailable, which is the frame rate the
        /// Phase 3 sweep measured (16.680 ms).
        /// </summary>
        public static float LastFrameSeconds()
        {
            return _lastFrameSeconds;
        }

        private static float _lastFrameSeconds = 1.0f / 60.0f;

        /// <summary>Updates the derived frame delta; called once per frame by the sensor path.</summary>
        public static void NoteClock(float logicTotalMs)
        {
            if (_clockMsValid && logicTotalMs > _clockMs)
            {
                float dt = (logicTotalMs - _clockMs) / 1000.0f;
                if (dt > 0.0f && dt < 0.5f)
                {
                    _lastFrameSeconds = dt;
                }
            }
            _clockMs = logicTotalMs;
            _clockMsValid = true;
        }

        private static float _clockMs;
        private static bool _clockMsValid;

        /// <summary>
        /// Samples every live projectile handed over by the read path, and reports what
        /// the linearity gate made of them.
        ///
        /// THE FRACTION IS THE MEASUREMENT, NOT A DIAGNOSTIC.  "What fraction of real
        /// bullets are straight" is one of the numbers Phase 2 exists to produce
        /// (IMPLEMENTATION-PLAN.md, Phase 2 exit criteria): it decides how good a dodge
        /// can be, because a dodge's lanes can only be projected for the straight ones.
        /// It is also the honest check on the virtual dispatch -- if `callvirt` were
        /// silently reaching the wrong body, the fraction would be 1.0 on a game full of
        /// weaves, and this line would say so.
        /// </summary>
        public static void OnFrame(List<object> liveProjectiles, List<ThreatKey> liveKeys, int frame)
        {
            if (!_resolved || liveProjectiles == null || liveProjectiles.Count == 0)
            {
                return;
            }

            long t0 = Stopwatch.GetTimestamp();
            int sampled = 0;
            int straightHere = 0;
            int curvedHere = 0;
            int predictedHere = 0;

            // THE PLAYER, ONCE PER FRAME.  A prediction is a sampled point inside this
            // disc, and the disc is the game's own: `DistanceSquared(player.Position) < r*r`.
            //
            // THE RADIUS IS READ HERE AND NOT PER BULLET, for the reason the whole sampler is
            // shaped this way: the frame budget is a per-frame [DODGE-ALGORITHM.md](DODGE-ALGORITHM.md)
            // section 8 concern and this loop runs once per live bullet. One accessor call per frame
            // instead of one per bullet is the difference between a fix and a regression.
            bool playerKnown = false;
            float playerX = 0.0f;
            float playerY = 0.0f;
            float playerRadius = 0.25f;
            try
            {
                object world = _getWorld == null ? null : _getWorld();
                if (world != null)
                {
                    object player = _getPlayer(world);
                    if (player != null)
                    {
                        object playerPosition = player;
                        if (playerPosition != null)
                        {
                            playerX = _getPlayerX(playerPosition);
                            playerY = _getPlayerY(playerPosition);
                            if (_getPlayerRadius != null)
                            {
                                playerRadius = _getPlayerRadius(player);
                            }
                            playerKnown = true;
                        }
                    }
                }
            }
            catch (Exception)
            {
                playerKnown = false;
            }

            // The game's own clock, once per frame, when the age has to be derived.
            float clockLogicTotal = 0.0f;
            if (_deriveAgeFromClock)
            {
                try
                {
                    object clock = _getClock();
                    if (clock != null)
                    {
                        clockLogicTotal = _getLogicTotal(clock);
                    }
                }
                catch (Exception)
                {
                    clockLogicTotal = 0.0f;
                }
            }

            // THE FRAME DELTA IS DERIVED FROM THE GAME'S OWN CLOCK, and it is derived HERE
            // because here is where the clock is read. `b = speed * frameSeconds` is the solver's
            // move budget and its whole claim to legality (PHASE3-HANDOFF.md section 1.4); a wall
            // clock would grant a larger legal step after a hitch than the client actually took.
            if (clockLogicTotal > 0.0f)
            {
                NoteClock(clockLogicTotal);
            }

            try
            {
                for (int i = 0; i < liveProjectiles.Count; i++)
                {
                    object projectile = liveProjectiles[i];

                    // THE SAME EXCLUSION THE THREAT SET GETS, AND IT IS NOT OPTIONAL HERE EITHER.
                    // This loop is what sets the live observer's `predicted=` flag, which is the
                    // flag `T7P` compares against the harness and the flag every false-positive
                    // rate in SPIKE3.2-OFFLINE-SOLVER.md is built from. A player's own shot is born
                    // inside their collision disc, so leaving them in makes the live predictor claim
                    // a hit on almost every shot fired -- which is exactly the "96 % predicted, no
                    // hit" column those recordings report.
                    if (CannotHitPlayer(projectile))
                    {
                        continue;
                    }

                    object desc = _getDesc(projectile);
                    if (desc == null)
                    {
                        continue;
                    }

                    float sin = _getSin(projectile);
                    float cos = _getCos(projectile);
                    int age = _getAge != null
                        ? _getAge(projectile)
                        : (int)clockLogicTotal - _getStartTime(projectile);
                    object position = projectile;
                    float liveX = _getProjectileX(position);
                    float liveY = _getProjectileY(position);

                    // THE SAMPLE SET, TAKEN BY THE ONE ROUTINE THAT TAKES IT. This used to be
                    // eleven lines inline here and is now a call, because the trace recorder needs
                    // the same eleven lines and a second copy of them would be a second
                    // definition of the anchor rule. See FillTrajectory: the anchoring is the
                    // easy half to get subtly wrong, and the whole point of section 4.4 rule 1 is
                    // that model error stays relative rather than accumulating.
                    Trajectory scratch = ScratchTrajectory;
                    FillSamples(desc, age, sin, cos, liveX, liveY, scratch);
                    for (int s = 0; s < SampleCount; s++)
                    {
                        Xs[s] = scratch.Xs[s];
                        Ys[s] = scratch.Ys[s];
                    }

                    sampled++;
                    if (LinearityGate.IsLinear(Xs, Ys, SampleCount, LinearityGate.DefaultTolerance))
                    {
                        straightHere++;
                    }
                    else
                    {
                        curvedHere++;
                    }

                    // THE PREDICTION ITSELF, and the whole of Deliverable 2.3's first half.
                    // The bullet is on a collision course if any sampled point lies inside
                    // the hit disc; the time to that point is the warning, and it is
                    // recorded on the frame the sensor first sees it so the comparison at
                    // the collision gets the warning the sensor actually had.
                    //
                    // THE DISC IS THE CLIENT'S OWN, AND IT TOOK A RECORDED RUN TO NOTICE IT WAS NOT.
                    // `Projectile.HitCharacter` (CLIENT-MAP.md, `Projectile.cs:1573`) is
                    //
                    //     DistanceSquared(World.Player.Position) < (World.Player.Radius + Radius)^2
                    //
                    // and this check used `radius * radius` -- the BULLET's radius alone, with the
                    // player's half missing. For a 0.2-radius bullet that is a 0.2 disc where the
                    // client uses 0.45, so the live predictor was answering a question the client
                    // never asks, and `ThreatSet.MarkPredicted` was called far less often than the
                    // geometry warranted. The run of `20261014-193728-p4508` is what surfaced it: the
                    // live observer counted **11** false negatives there while the harness -- which
                    // reads the recorded paths off line and uses `PlayerRadius + t.Radius` -- counted
                    // **2**. The harness was right in every case that could be checked, and the
                    // difference is exactly this line.
                    //
                    // The player's radius falls back to 0.25 when the field did not resolve, which is
                    // `GameObject.Radius`'s documented default and the value `TraceRecorder` already
                    // uses for the same quantity (`_getPlayerRadius != null ? ... : 0.25f`).
                    if (playerKnown && liveKeys != null && i < liveKeys.Count)
                    {
                        float radius = _getRadius(projectile);
                        float disc = playerRadius + radius;
                        float radiusSquared = disc * disc;
                        for (int s = 0; s < SampleCount; s++)
                        {
                            float dx = Xs[s] - playerX;
                            float dy = Ys[s] - playerY;
                            if ((dx * dx) + (dy * dy) <= radiusSquared)
                            {
                                if (SensorRead.Threats.MarkPredicted(liveKeys[i], frame, t0))
                                {
                                    predictedHere++;
                                }
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Once per run: a throwing sample would otherwise fill the log ring.
                if (_framesSampled == 0)
                {
                    HookEntry.AppendLog("ERROR", "the trajectory sampler threw; no sample set "
                                                 + "from this point is trustworthy: " + ex.GetType().Name
                                                 + ": " + ex.Message);
                    HookEntry.FlushLog();
                }
            }

            long t1 = Stopwatch.GetTimestamp();
            long frequency = Stopwatch.Frequency;
            _lastCostUs = frequency > 0 ? ((t1 - t0) * 1000000L) / frequency : 0L;
            Cost.Add(_lastCostUs);
            if (_lastCostUs > _maxCostUs)
            {
                _maxCostUs = _lastCostUs;
            }
            if (sampled > 0)
            {
                _framesSampled++;
                _bulletsSampled += sampled;
                _straight += straightHere;
                _curved += curvedHere;
            }

            if (frame >= _nextLogFrame && _framesSampled > 0)
            {
                _nextLogFrame = frame + 150;
                HookEntry.AppendLog("INFO", "sampler: frame " + frame + ": sampled=" + sampled
                                            + " straight=" + straightHere + " curved=" + curvedHere
                                            + " predicted=" + predictedHere
                                            + " | run: " + _bulletsSampled + " bullet(s), "
                                            + "straight fraction " + StraightFraction.ToString("F3", CultureInfo.InvariantCulture)
                                            + ", cost=" + _lastCostUs + "us max=" + _maxCostUs + "us");
                HookEntry.FlushLog();
            }
        }
    }
}










