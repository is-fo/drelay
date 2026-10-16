// EscapeTest.cs -- Phase 3's floor, made enterable before it is needed.
//
// =====================================================================================
// WHY THIS EXISTS, AND WHY IT IS NOT PART OF THE DODGE
// =====================================================================================
//
// Phase 3's deliverable is a dodge, and the client's own `World.RequestEscape()` is its
// **floor**: the one thing reached when the projected damage over the horizon is lethal and
// no admissible move avoids it (`DODGE-ALGORITHM.md` section 7). A floor that has never
// been stood on is not a floor, and the 2026-10-13 live run is the receipt:
//
//     [ERROR]   escape       MISSING  never compiled in the discovery window.
//     [INFO ] target-size escape IL size 335: 6 same-size method(s) compiled,
//             none of them this target
//
// The reason is now known and is not a defect in the pinned identity: **nothing called it**.
// The operator never pressed the nexus key and no zone change called it either, so
// `RequestEscape` was simply never JITted -- and a target that is never compiled is a target
// the detour never sees, which the size table reports as the two-case middle ground it is
// (`docs/HOOK-BUILD-MATRIX.md`).
//
// So the question this file answers is narrower than the dodge's and comes before it:
// **can the hook make the client escape at all?** That is one call with no decision logic
// behind it -- no threat set, no solver, no snapshot -- and it is worth answering on its own
// because everything about the floor depends on it:
//
//   * the plan says the call is "indistinguishable from the player pressing the nexus key",
//     which is an argument from the source (it runs the client's own gates and sends the
//     client's own packet) and has never been *measured*;
//   * `MANAGED-ENTRY-HANDOFF.md` section 6 notes that the managed-entry question was left
//     open for exactly this call, and section 0.2 answers it for replacements -- this is the
//     first time that answer is used to reach a method the game itself has not called;
//   * the escalation's own gates are the client's, so an operator who has never seen
//     `GmEscape` leave the machine while the hook asked for it cannot tell a working floor
//     from a refused one.
//
// It does **not** replace Deliverable 3.4. Deliverable 3.4 is the decision -- project the
// undodgeable damage, call `Entity.GetDamageTaken`, and escape only when no admissible move
// avoids it. This is the actuator underneath that decision, tested alone, on purpose: if the
// call cannot be made, nothing about gate 6 matters.
//
// =====================================================================================
// WHAT IT DOES, EXACTLY, AND WHAT IT REFUSES TO DO
// =====================================================================================
//
// `--escape-test SECONDS` (launcher) -> `DRELAY_ESCAPE_TEST_SEC` -> this class. Once the
// world exists, it waits `SECONDS` and then calls `World.RequestEscape()` **once**. The
// delay is what keeps the test out of the way of the run it rides on: it is measured from
// the first frame with a live `World`, so it does not depend on how long the operator took
// to get into a zone.
//
// Five things are deliberate and each one is a way this could have been a bad tool:
//
//   1. **it fires once.** A repeating call would be a nexus key held down: in the ordinary
//      path that is a `GmEscape` every cooldown, and with the `Impermanence` totem it is a
//      cast that re-arms for ever. One call, then a latched flag, and the reason is logged.
//   2. **it does not require a world to be non-safe.** `RequestEscape` is a legitimate
//      no-op in a safe area -- the plan says so and `VERIFICATION.md` section 5.1 makes the
//      safe-area variant a case the two-controller test has to exercise. Firing there is
//      therefore *information*, not a mistake, and the log says which branch was taken.
//   3. **it never decides anything.** It reads no health, no threat, no damage. Its whole
//      input is "has the world existed for N seconds".
//   4. **it is off unless asked for.** No environment variable, no call, no behaviour: the
//      frame replacement publishes and calls through exactly as it does today.
//   5. **a failure is named, not swallowed.** The frame replacement's outer catch would
//      otherwise turn "the reflection call threw" into "the test never fired", which is the
//      same absent-evidence shape PHASE2-SCOPE.md section 4.12 and 4.15 both record.
//
// The call goes through reflection rather than a cached `MethodInfo` delegate, which is the
// one place this is not the cheapest thing available. It happens **once per run**, so the
// cost is irrelevant, and the reflection form means the hook fails at the call rather than
// at resolve if the method is not invocable -- the failure lands where the log line is.

using System;
using System.Globalization;
using System.Reflection;

namespace Drelay.Bootstrap
{
    public static class EscapeTest
    {
        private static bool _resolved;
        private static bool _enabled;
        private static string _statusLine = "not attempted";
        private static string _unresolvedReason = "not attempted";

        private static FieldInfo _worldSharedField;
        private static Func<object> _getWorld;
        private static MethodInfo _requestEscape;

        /// <summary>Seconds to wait after the world first exists. Zero means "the next frame".</summary>
        private static double _delaySeconds;

        private static bool _armed;
        private static bool _fired;
        private static bool _firedLogged;
        private static long _armedTicks;
        private static long _framesWaited;
        private static double _elapsedAtCallMs;
        private static int _failureLogged;

        public static bool Resolved => _resolved;
        public static bool Enabled => _enabled;
        public static bool Fired => _fired;
        public static string UnresolvedReason => _unresolvedReason;
        public static string Status() => _statusLine;

        /// <summary>
        /// Resolve the world handle and the method, and read the switch.
        ///
        /// Called from the same resolve callback the rest of the hook uses, so the failure
        /// path is uniform: return false with a reason, and the caller logs it and carries
        /// on. Nothing here is fatal to anything else.
        /// </summary>
        public static bool Resolve(Assembly game)
        {
            try
            {
                string raw = (Environment.GetEnvironmentVariable("DRELAY_ESCAPE_TEST_SEC") ?? "").Trim();
                _enabled = raw.Length > 0;
                if (!_enabled)
                {
                    _statusLine = "the escape test is IDLE (DRELAY_ESCAPE_TEST_SEC unset); "
                                  + "RequestEscape is never called";
                    _resolved = true;
                    return true;
                }

                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture,
                                     out _delaySeconds) || _delaySeconds < 0.0)
                {
                    _unresolvedReason = "DRELAY_ESCAPE_TEST_SEC is not a non-negative number of "
                                        + "seconds: '" + raw + "'";
                    _enabled = false;
                    _statusLine = "the escape test is NOT armed: " + _unresolvedReason;
                    _resolved = true;
                    return false;
                }

                Type worldType = game.GetType(ManagedArmingProbe.WorldTypeName);
                if (worldType == null)
                {
                    _unresolvedReason = "type " + ManagedArmingProbe.WorldTypeName + " not found";
                    _statusLine = "the escape test is NOT available: " + _unresolvedReason;
                    return false;
                }

                _worldSharedField = worldType.GetField("Shared",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (_worldSharedField == null)
                {
                    _unresolvedReason = "World.Shared not found";
                    _statusLine = "the escape test is NOT available: " + _unresolvedReason;
                    return false;
                }

                // THE ONE TARGET THE HOOK DOES NOT NEED THE GAME TO COMPILE.  `RequestEscape`
                // is a target in `known-build.json` because it is worth patching, but this
                // call does not depend on the patch at all: it needs a `MethodInfo`, and a
                // method the JIT has never looked at has one. That is the whole reason the
                // floor can be tested on a run where the target is reported MISSING.
                _requestEscape = worldType.GetMethod("RequestEscape",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (_requestEscape == null)
                {
                    _unresolvedReason = "World.RequestEscape() was not found on this build";
                    _statusLine = "the escape test is NOT available: " + _unresolvedReason;
                    return false;
                }
                if (_requestEscape.ReturnType != typeof(void) || _requestEscape.IsStatic)
                {
                    _unresolvedReason = "World.RequestEscape is not a void instance method "
                                        + "(returns " + _requestEscape.ReturnType.Name
                                        + (_requestEscape.IsStatic ? ", static" : ", instance") + ")";
                    _statusLine = "the escape test is NOT available: " + _unresolvedReason;
                    return false;
                }

                _getWorld = FieldAccessor.Static(_worldSharedField);
                _resolved = true;
                _statusLine = string.Format(CultureInfo.InvariantCulture,
                    "the escape test is ARMED: World.RequestEscape() will be called ONCE, "
                    + "{0:0.###} s after the world first exists", _delaySeconds);
                return true;
            }
            catch (Exception ex)
            {
                _unresolvedReason = ex.GetType().Name + ": " + ex.Message;
                _statusLine = "the escape test is NOT available: " + _unresolvedReason;
                return false;
            }
        }

        /// <summary>Prints the plan once, at resolve time, like the pilot's does.</summary>
        public static void LogPlan()
        {
            HookEntry.AppendLog("INFO", Status());
            HookEntry.FlushLog();
        }

        /// <summary>
        /// One frame's worth. Called from the frame replacement, after the sensor, and
        /// **outside** the sensor's own switch: this is an actuator test, not a read, and
        /// gating it behind `DRELAY_FRAME_SENSOR` would mean the escape test could only be
        /// run by paying for the read path as well.
        /// </summary>
        public static void OnFrame()
        {
            if (!_resolved || !_enabled || _fired)
            {
                return;
            }

            try
            {
                object world = _getWorld();
                if (world == null)
                {
                    return;   // no world yet: the clock has not started
                }

                if (!_armed)
                {
                    _armed = true;
                    _armedTicks = Environment.TickCount64;
                    HookEntry.AppendLog("INFO", string.Format(CultureInfo.InvariantCulture,
                        "escape test armed: the world exists; calling World.RequestEscape() once "
                        + "in {0:0.###} s", _delaySeconds));
                    HookEntry.FlushLog();
                }

                _framesWaited++;
                double elapsedMs = Environment.TickCount64 - _armedTicks;
                if (elapsedMs < _delaySeconds * 1000.0)
                {
                    return;
                }

                _elapsedAtCallMs = elapsedMs;
                Invoke(world);
            }
            catch (Exception ex)
            {
                if (System.Threading.Interlocked.Exchange(ref _failureLogged, 1) == 0)
                {
                    HookEntry.AppendLog("ERROR", "the escape test threw; the game is unaffected "
                                                 + "and the frame update continues untouched: "
                                                 + ex.GetType().Name + ": " + ex.Message);
                    HookEntry.FlushLog();
                }
            }
        }

        /// <summary>
        /// `World.RequestEscape()`, once, with the outcome written down either way.
        ///
        /// THE LOG LINE BEFORE THE CALL IS THE IMPORTANT ONE. If the call itself is what kills
        /// the process -- which is the shape a wrong managed-entry assumption takes -- then a
        /// log written only afterwards leaves no evidence that the call was reached, and the
        /// run reads as "the test never armed". Two lines, then the verdict line: a reader can
        /// tell "reached it", "it returned" and "it threw" apart without a debugger.
        /// </summary>
        private static void Invoke(object world)
        {
            _fired = true;   // latched BEFORE the call, so a throw cannot re-arm it

            if (!_firedLogged)
            {
                _firedLogged = true;
                HookEntry.AppendLog("INFO", string.Format(CultureInfo.InvariantCulture,
                    "escape test: calling World.RequestEscape() now (world for {0:0} ms, "
                    + "{1} frame(s)) -- this line precedes the call so that a fault inside it "
                    + "still leaves the attempt on record",
                    _elapsedAtCallMs, _framesWaited));
                HookEntry.FlushLog();
            }

            try
            {
                _requestEscape.Invoke(world, null);
                HookEntry.AppendLog("INFO", string.Format(CultureInfo.InvariantCulture,
                    "escape test: World.RequestEscape() RETURNED after {0:0} ms. The client's own "
                    + "gates ran; whether it sent GmEscape, started an Impermanence cast or did "
                    + "nothing is a wire fact -- read the relay log for id 0x56 (GmEscape) or "
                    + "the escape-cast-state pair", _elapsedAtCallMs));
                HookEntry.FlushLog();
            }
            catch (TargetInvocationException tie)
            {
                // The game's own method threw. Reported with the inner exception, because the
                // wrapper is an artefact of calling through reflection and says nothing.
                HookEntry.AppendLog("ERROR", "escape test: World.RequestEscape() THREW "
                                             + (tie.InnerException != null
                                                ? tie.InnerException.GetType().Name + ": "
                                                  + tie.InnerException.Message
                                                : tie.Message)
                                             + " -- the call was made and the client refused it, "
                                             + "which is a different result from never reaching it");
                HookEntry.FlushLog();
            }
        }
    }
}
