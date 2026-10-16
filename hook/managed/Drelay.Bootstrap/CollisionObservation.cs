// CollisionObservation.cs -- Deliverable 2.3: the ground-truth comparison.
//
// THE MOST VALUABLE ARTEFACT IN THE WHOLE PROJECT, and IMPLEMENTATION-PLAN.md says so in
// those words.  A dodge's quality cannot be established by unit tests -- the reference
// engine's own audit admits that "live gameplay remains unverified" for much of its
// newest code -- but the *model's* correctness can be, and this is the only thing that
// can tell us whether the prediction is right.
//
// WHAT IT COMPARES.  `Projectile.HitCharacter()` is the instant the client itself
// resolves a bullet as hitting the local player and is about to tell the server
// (CLIENT-FINDINGS.md section 4: "the exact moment the server is about to be told").  It
// is therefore the game's own collision decision, and the sensor's claim to be useful
// rests on agreeing with it.  At that instant this asks three questions:
//
//   * did the threat set know this bullet at all?
//   * had the sensor already predicted that it would hit -- and if so, how long ago?
//   * and when it did not, is that a false negative?
//
// The mirror question is answered by the threat set itself: a bullet the sensor predicted
// and that was retired without ever hitting is a false positive, counted in
// ThreatSet.Reconcile.  Those two rates and the warning distribution are three of the
// four numbers Phase 2 exists to produce.
//
// HOW IT IS ENTERED.  The native side points MinHook's `jmp` at `HitCharacterReplacement`
// instead of at a native detour, exactly as it does for the frame tick, so the game's own
// `call` is a managed-to-managed call and nothing native interrupts the managed stack
// (PHASE2-SCOPE.md section 4.4).  The replacement takes the receiver as `object` rather
// than `IntPtr`: a managed reference is a pointer in RCX either way, and taking it typed
// means the fields can be read through reflection instead of reinterpreting an address.
//
// THE ORIGINAL IS ALWAYS CALLED.  A replacement that observed and returned would tell the
// server nothing, which is the most destructive thing this file could do -- so the
// call-through happens on every path, including the ones where the observation failed.

using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Drelay.Bootstrap
{
    public static class CollisionObservation
    {
        private static bool _resolved;
        private static string _unresolvedReason = "not attempted";

        // Emitted accessors rather than GetValue, for the reason in FieldAccessor.cs.
        private static Func<object, int> _getGameId;
        private static Func<object, int> _getStartTime;
        private static Func<object, float> _getX;
        private static Func<object, float> _getY;
        private static Func<object, float> _getRadius;

        private static FieldInfo _gameIdField;
        private static FieldInfo _startTimeField;
        private static FieldInfo _positionField;
        private static FieldInfo _pointX;
        private static FieldInfo _pointY;
        private static FieldInfo _radiusField;

        private static long _entered;
        private static long _known;
        private static long _unknown;
        private static long _predicted;
        private static long _unpredicted;
        private static double _warningTotalMs;
        private static double _warningMinMs = double.MaxValue;
        private static double _warningMaxMs;

        public static bool Resolved => _resolved;
        public static string UnresolvedReason => _unresolvedReason;

        /// <summary>
        /// Collision decisions observed -- the denominator of everything else.
        ///
        /// A DECISION IS A `true` RETURN, NOT AN ENTRY.  `HitCharacter` is the client's
        /// per-frame predicate, so the entries are questions; this counts the answers that
        /// said yes.  See `HitCharacterReplacement`, and PHASE2-SCOPE.md section 4.14 for
        /// what counting the other thing cost.
        /// </summary>
        public static long Entered => _entered;

        /// <summary>Decisions whose bullet the threat set already knew about.</summary>
        public static long Known => _known;

        /// <summary>
        /// Decisions for a bullet the threat set had never seen.  A coverage hole rather
        /// than a missed prediction, and a different owner.
        /// </summary>
        public static long Unknown => _unknown;

        /// <summary>Resolves the projectile fields this needs.  Failure is not fatal: the frame path and the threat set keep working.</summary>
        public static bool Resolve(Assembly game)
        {
            try
            {
                Type projectileType = game.GetType(SensorRead.ProjectileTypeName);
                if (projectileType == null)
                {
                    _unresolvedReason = SensorRead.ProjectileTypeName + " not found";
                    return false;
                }

                string gameIdName;
                string startName;
                string positionName;
                string radiusName;
                // Candidate names, for the reason FieldLookup.cs gives: the identity lives
                // on the base `GameObject` and the start time privately on `Projectile`,
                // and the host and the client spell neither the same way.
                _gameIdField = FieldLookup.Find(projectileType, out gameIdName,
                                                "GameId", "_gameId", "ObjectId");
                _startTimeField = FieldLookup.Find(projectileType, out startName,
                                                   "_startTime", "StartTime", "startTime");
                _positionField = FieldLookup.Find(projectileType, out positionName,
                                                  "Position", "ExactPosition", "_position");
                _radiusField = FieldLookup.Find(projectileType, out radiusName,
                                                "Radius", "_radius", "TouchRadius");
                if (_positionField != null)
                {
                    string pointXName;
                    string pointYName;
                    _pointX = FieldLookup.Find(_positionField.FieldType, out pointXName, "X");
                    _pointY = FieldLookup.Find(_positionField.FieldType, out pointYName, "Y");
                }
                if (_gameIdField == null || _startTimeField == null || _positionField == null
                    || _radiusField == null || _pointX == null || _pointY == null)
                {
                    _unresolvedReason = "Projectile fields not all present: GameId="
                                        + (_gameIdField != null) + " StartTime="
                                        + (_startTimeField != null) + " Position="
                                        + (_positionField != null) + " Radius=" + (_radiusField != null);
                    return false;
                }

                _getGameId = FieldAccessor.Int(_gameIdField);
                _getStartTime = FieldAccessor.Int(_startTimeField);
                _getX = FieldAccessor.NestedFloat(_positionField, _pointX);
                _getY = FieldAccessor.NestedFloat(_positionField, _pointY);
                _getRadius = FieldAccessor.Float(_radiusField);

                _resolved = true;
                HookEntry.AppendLog("INFO", "collision observation resolved: the hook will compare "
                                            + "every HitCharacter decision against the threat set");
                HookEntry.FlushLog();
                return true;
            }
            catch (Exception ex)
            {
                _unresolvedReason = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// THE REPLACEMENT.  `Projectile.HitCharacter()` is `private bool HitCharacter()`,
        /// so the receiver arrives in RCX and the answer leaves in AL; a static method
        /// taking `object` and returning `bool` is the same ABI, with the receiver typed.
        ///
        /// THE ORIGINAL IS CALLED FIRST, AND ONLY A `true` ANSWER IS OBSERVED.  This is a
        /// correction, and it is the difference between two very different numbers.
        /// `HitCharacter` is a per-frame PREDICATE: `Projectile.Update` calls `Hit()` every
        /// frame, `Hit()` calls this when `HitsPlayer` is set, and the answer decides
        /// whether the hit resolves.  Observing every *entry* therefore counts questions,
        /// not decisions -- and on the 2026-10-12 20:42 live session it counted 418 328
        /// entries for 48 resolved hits, which turned one bullet passing near the player
        /// into a false negative on every frame it was near.  The plan's false-negative
        /// rate is "hits the game made that the sensor never predicted"
        /// (IMPLEMENTATION-PLAN.md, Deliverable 2.3), and that is the `true` set.
        ///
        /// THE ORDER IS ALSO THE SAFER ONE.  Calling through first removes the window in
        /// which an observation could delay or disturb the game's own hit resolution, and
        /// the observation is still wrapped, so nothing it does can change the answer the
        /// game gets back.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool HitCharacterReplacement(object self)
        {
            bool decided = CallOriginal(self);
            if (decided)
            {
                try
                {
                    ObserveHit(self);
                }
                catch
                {
                    // Swallowed deliberately: an exception escaping into the game's own hit
                    // path would be far worse than a missed observation, and `catch` costs
                    // nothing until something is thrown.
                }
            }
            return decided;
        }

        /// <summary>
        /// The comparison, at the instant the game decides.  Never throws out.
        /// </summary>
        private static void ObserveHit(object projectile)
        {
            _entered++;

            if (!_resolved || projectile == null)
            {
                return;
            }

            int gameId = _getGameId(projectile);
            int startTime = _getStartTime(projectile);
            var key = new ThreatKey(gameId, startTime);

            long tickNow = System.Diagnostics.Stopwatch.GetTimestamp();
            double warningMs;
            ThreatRecord record;
            bool wasTracked = SensorRead.Threats.TryGet(key, out record);
            bool firstTime = SensorRead.Threats.MarkHit(key, tickNow, out warningMs);

            if (!firstTime)
            {
                return;   // the game's own path can decide the same bullet twice
            }

            if (wasTracked && record.Predicted)
            {
                _predicted++;
                _warningTotalMs += warningMs;
                if (warningMs < _warningMinMs)
                {
                    _warningMinMs = warningMs;
                }
                if (warningMs > _warningMaxMs)
                {
                    _warningMaxMs = warningMs;
                }
            }
            else
            {
                _unpredicted++;
            }

            if (wasTracked)
            {
                _known++;
            }
            else
            {
                _unknown++;
            }

            // ONE LINE PER COLLISION, and collisions are what the run is for.  The line
            // carries the verdict and the warning, so the log *is* the predicted-versus-
            // actual comparison rather than a summary of one.  It also carries the
            // positions, because a disagreement that has to be diagnosed later needs the
            // geometry and not only the verdict.
            float bulletX = _getX(projectile);
            float bulletY = _getY(projectile);
            float radius = _getRadius(projectile);

            HookEntry.AppendLog("INFO", "collision: key=" + key + " tracked=" + (wasTracked ? "yes" : "NO")
                                        + " predicted=" + (wasTracked && record.Predicted ? "yes" : "NO")
                                        + " warning=" + warningMs.ToString("F1", CultureInfo.InvariantCulture)
                                        + "ms bullet=("
                                        + bulletX.ToString("F1", CultureInfo.InvariantCulture) + ","
                                        + bulletY.ToString("F1", CultureInfo.InvariantCulture) + ") r="
                                        + radius.ToString("F1", CultureInfo.InvariantCulture)
                                        + " | run: " + _predicted + " predicted, " + _unpredicted
                                        + " not, of " + _entered + " decision(s)");
            HookEntry.FlushLog();
        }

        /// <summary>
        /// Continue into the real method, through MinHook's trampoline.  A MANAGED function
        /// pointer, for the reason PHASE2-SCOPE.md section 4.4 gives: `delegate* unmanaged`
        /// would be a managed-to-native transition, which puts a transition frame and a
        /// preemptive thread state under the original body and reintroduces exactly the
        /// interruption the managed replacement exists to remove.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool CallOriginal(object self)
        {
            long trampoline = Marshal.ReadInt64(HookEntry.SharedBlockPointer,
                                                GeneratedLayout.Off_managedCollisionTrampoline);
            if (trampoline == 0)
            {
                // Must not happen: the native side publishes the trampoline before it
                // enables the hook.  Returning false would suppress the hit, so this is
                // loud -- the game would otherwise stop claiming damage in silence.
                HookEntry.AppendLog("ERROR", "the collision replacement ran with no trampoline "
                                             + "published; the original decision cannot be made");
                HookEntry.FlushLog();
                return false;
            }

            unsafe
            {
                return ((delegate* managed<object, bool>)(IntPtr)trampoline)(self);
            }
        }

        /// <summary>
        /// Prepare and publish the replacement, and warm everything it calls -- the same
        /// discipline as the frame tick's, for the same reason: a method compiled on its
        /// first call is compiled from inside a patched game frame.
        /// </summary>
        public static void Publish()
        {
            MethodInfo method = typeof(CollisionObservation).GetMethod(
                nameof(HitCharacterReplacement), BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                HookEntry.AppendLog("ERROR", "the collision replacement was not found by name; nothing published");
                HookEntry.FlushLog();
                return;
            }

            MethodInfo callOriginal = typeof(CollisionObservation).GetMethod(
                "CallOriginal", BindingFlags.NonPublic | BindingFlags.Static);
            if (callOriginal != null)
            {
                RuntimeHelpers.PrepareMethod(callOriginal.MethodHandle);
            }
            RuntimeHelpers.PrepareMethod(method.MethodHandle);

            IntPtr entry = method.MethodHandle.GetFunctionPointer();
            Marshal.WriteInt64(HookEntry.SharedBlockPointer,
                               GeneratedLayout.Off_managedCollisionReplacement, entry.ToInt64());
            HookEntry.AppendLog("INFO", "managed collision replacement published at 0x"
                                        + entry.ToInt64().ToString("X")
                                        + "; the native side will jump straight into it");
            HookEntry.FlushLog();
        }
    }
}



