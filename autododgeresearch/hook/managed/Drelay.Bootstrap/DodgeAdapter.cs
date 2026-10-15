// DodgeAdapter.cs -- Deliverable 3.3, the half that was missing: the solver, on live frames.
//
// =====================================================================================
// WHAT THIS IS
// =====================================================================================
//
// SPIKE3.2-OFFLINE-SOLVER.md built the decision and proved it moves on synthetic scenarios, and
// `PHASE3-HANDOFF.md` section 3.4 recorded the gap that remained: **the sensor and the solver have
// never shared a frame.** `DodgeSolver.Decide` is a pure function of a `DodgeSnapshot`, and until
// this file existed *nothing built one from the game*. There was a trace recorder that wrote the
// snapshot's inputs to JSON and an offline harness that read them back, so the solver had been run
// against recorded play -- but only in a console program, a session later, with the game closed.
//
// This file is the assembler. It reads the live world at the frame tick, fills the plain-data
// snapshot the solver was built to consume, calls `Decide`, and does one of two things with the
// answer:
//
//   * **dry run** -- logs what it would have done, every time it would have done it. This is the
//     default, and the plan's own requirement ("ship it behind its own switch, defaulting to a mode
//     that logs decisions without acting");
//   * **live** -- publishes a heading and a magnitude into a slot `SteeringPilot` consumes at the
//     movement integrator's entry, which is the published slot `PHASE3-HANDOFF.md` section 4
//     describes and is the only thing in this project that moves the player on the solver's word.
//
// =====================================================================================
// WHERE IT RUNS, AND WHY THE TWO HALVES ARE IN DIFFERENT HOOKS
// =====================================================================================
//
// `PHASE3-HANDOFF.md` section 4 settles this and it is load-bearing:
//
//   * the DECISION belongs at the FRAME TICK (`DarzaWindow.Update`), because that is where the
//     client has drained this frame's packets and has not yet advanced the world -- the freshest
//     read there is. `SensorRead.OnFrame` already runs there, and the threat set it builds is this
//     file's input;
//   * the WRITE belongs at the MOVEMENT INTEGRATOR's entry, because that is the last writer before
//     the client reads the input, and the write and the read-back are the same call.
//
// So the two are one frame apart in the same tick, and they are joined by a slot rather than by a
// second decision path. This file owns the deciding end of that slot.
//
// =====================================================================================
// THE THREE THINGS THIS FILE DELIBERATELY DOES NOT DO
// =====================================================================================
//
// Each is written here rather than left to be discovered, because each is a place where a reader
// would otherwise assume the live path is as complete as the offline one.
//
// 1. **NO DAMAGE IS READ, SO THE ESCAPE GATE CANNOT FIRE.** `Trajectory.Damage`, `.DamagePercent`
//    and `.ManaDamage` are left at zero, and `DodgeSolver`'s gate 6 tests
//    `ProjectedUnavoidableDamage > Health * LethalFraction`. With damage zero that is `0 > h`, which
//    is false, so `DodgeAction.Escape` is never returned and `World.RequestEscape()` is never
//    called. That is Deliverable 3.4 and it is deliberately *not* part of this one -- a live run
//    that started casting nexus on its own would be a much worse surprise than a gate that does not
//    fire. `UnavoidableThreats` IS computed and logged, so the gate's input is visible.
//
// 2. **RULE 7 (ENEMY BODIES) IS VACUOUS.** `DodgeSnapshot.Bodies` is left empty -- "empty is
//    legal", says the type -- because no accessor for the live entity list has been resolved. The
//    counter `bodies=0` in every report is the honest mark of it.
//
// 3. **THE PLAYER'S SPEED IS MEASURED, NOT DERIVED FROM THE LAW.** `PlayerFunctions.TilesPerSecond`
//    takes eight arguments that include a tile type, a `PlayerStat` value, two effect tiers and four
//    `HasEffect` lookups, every one of them reached through an obfuscated constant -- and a wrong
//    argument is a *plausible* speed, which is the worst kind of wrong. The displacement the client
//    itself produced between two frame ticks divided by the frame's own delta is a direct
//    measurement of the quantity `b = speed * frameSeconds` is made of, and it is what the offline
//    harness already uses (`SolverHarness.Program.ResolveSpeed`). The law's value at 60 Hz -- 8.2
//    world units/s, `PHASE3-HANDOFF.md` section 1.4 -- is the prior for frames where the player did
//    not move, which includes the frames where the player is pinned, and for those the *maximum*
//    budget is the correct one to test admission against.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Drelay.Bootstrap
{
    /// <summary>What the adapter wants the pilot to do with the input this frame.</summary>
    internal enum DodgeCommand
    {
        /// <summary>Nothing: leave the client's own input exactly as the player's hands left it.</summary>
        None = 0,

        /// <summary>Write <see cref="DodgeAdapter.TakeCommand"/>'s heading at its magnitude.</summary>
        Move = 1,

        /// <summary>The solver's `stand` answer: stop the player, do not carry their old heading on.</summary>
        Stop = 2,
    }

    /// <summary>
    /// The live half of the solver: snapshot assembly, the decision, the dry-run log and the slot.
    /// </summary>
    public static class DodgeAdapter
    {
        // --- The switches ------------------------------------------------------------------

        /// <summary>Set by the operator to run the decision at all. Off by default.</summary>
        private const string EnvDodge = "DRELAY_DODGE";

        /// <summary>Set to `1` to let the decision reach the actuator. Anything else is a dry run.</summary>
        private const string EnvAct = "DRELAY_DODGE_ACT";

        // TWO SWITCHES AND BOTH ARE NEEDED, WHICH IS DELIBERATE. `DRELAY_DODGE` decides whether the
        // solver runs at all; `DRELAY_DODGE_ACT` decides whether its answer reaches the input. And
        // even with both set, the pilot only consumes the slot in the `dodge` steering mode -- so a
        // run moves the player on the solver's word only when the operator asked for three separate
        // things. The failure this prevents is not hypothetical: every previous "the hook moved the
        // player when I did not ask" in this project's history has been one switch.

        // --- Resolution --------------------------------------------------------------------

        private static bool _resolved;
        private static bool _enabled;
        private static bool _act;
        private static string _unresolvedReason = "not attempted";
        private static string _statusLine = "not attempted";

        private static DodgeConfig _config;
        private static DodgeSolver _solver;
        private static readonly DodgeSnapshot Snapshot = new DodgeSnapshot();

        private static Func<object> _getWorld;
        private static Func<object, object> _getPlayer;
        private static Func<object, float> _getPlayerX;
        private static Func<object, float> _getPlayerY;
        private static Func<object, float> _getPlayerRadius;
        private static Func<object, double> _getPlayerHealth;
        private static Func<object, bool> _getPlayerDead;
        private static Func<object, bool> _getSafeWorld;
        private static Func<float> _getWorldRotation;
        private static Func<object, object> _getGameScene;
        private static Func<object, object> _getInput;
        private static Func<object, bool> _getMoving;
        private static Func<object, float> _getMoveAngleRadians;
        private static Func<object> _getClock;
        private static Func<object, float> _getLogicTotal;

        // --- Precedence, which is part of the deliverable and not a later fix ----------------
        //
        // `World.RequestEscape()` has two paths and only one of them is hurt by a dodge. The normal
        // path sends `GmEscape` immediately (`World.cs:13011`) and movement cannot cancel it. The
        // OTHER path -- reached only when `Player.Totems.Has(CharacterTotem.Impermanence)`, i.e. in a
        // challenge run -- starts a CAST, and that cast is cancelled by movement on the very next
        // frame (`World.cs:13043-13047: if (GameScene.GameInput.Moving) { CancelEscapeCast(); }`).
        // So a dodge that steers while the client is casting a nexus cancels a run-saving escape.
        //
        // THE CAST FLAG ITSELF IS NOT READABLE, AND THAT IS WHY THIS IS A TOTEM CHECK. The flag is
        // `World.hehe_E331_E9CE_F5E3_EBF5_EAF7`, an obfuscated private field, and resolving an
        // obfuscated bool on `World` by structure is not possible -- there are dozens. The condition
        // that *reaches* the cast branch is readable (`Player.Totems.Has(Impermanence)`, the same
        // test the client makes), and the rule "while the totem is equipped, the dodge does not
        // steer" is strictly safer than "yield for the duration of the cast": it cannot be wrong
        // about when the cast started, because it never starts one.
        //
        // ITS COST IS NAMED RATHER THAN HIDDEN: with the totem equipped the live dodge is off. That
        // is a real limitation for challenge runs and it is the conservative direction -- the plan's
        // own text says the failure mode when this is got wrong is "a cancelled, run-saving escape".
        private static Func<object, object> _getTotems;
        private static MethodInfo _totemHas;
        private static object _impermanence;
        private static object _precedencePlayer;
        private static int _precedence;          // 0 unresolved, 1 normal escape, 2 channelled
        private static long _framesYieldedToEscape;
        private static bool _precedenceLogged;

        // The collision grid: `private byte[,] World.<grid>`, found as the only `byte[,]` on World
        // for the reason `TraceRecorder.ResolveGrid` records -- the name is an obfuscated code
        // point, so a structural property is the only honest lookup.
        private static Func<object, object> _getGridArray;
        private static Func<object, int, int, byte> _gridAt;
        private static bool _gridResolved;

        // --- The grid window ---------------------------------------------------------------
        //
        // 64 tiles each way, AND IT IS NOT THE HORIZON'S NUMBER ANY MORE. 24 was -- "the region a
        // 600 ms horizon plus a one-frame budget can touch" -- and that was right while every test
        // was a one-step candidate. The pathfinder (HANDOFF-DIRECTED-DODGE.md section 4) searches
        // this grid for a route around a corridor of fire, so the window is now sized by what a PATH
        // can reach rather than by what a step can: 128 units across, which is the same window
        // `TraceRecorder` writes and therefore the same world the offline harness searches. A window
        // that did not match the recorder's would make the tuned decision a decision about a
        // different grid.
        //
        // Re-read only when the player's tile changes, so a wider window is also a cheaper one: the
        // copy happens once per tile crossed rather than once per frame.
        private const int GridRadius = 64;

        private static readonly byte[] GridCells = new byte[(2 * GridRadius) * (2 * GridRadius)];
        private static int _gridOriginX;
        private static int _gridOriginY;
        private static bool _gridWindowValid;

        // --- The published slot ------------------------------------------------------------

        private static DodgeCommand _pending = DodgeCommand.None;
        private static float _pendingHeading;
        private static float _pendingScale;

        /// <summary>
        /// False once the integrator has taken this frame's command, so a frame that somehow calls
        /// the integrator twice cannot act on one decision twice. A decision is a frame's answer.
        /// </summary>
        private static bool _pendingFresh;

        /// <summary>
        /// The magnitude the PILOT actually wrote into the input, so the speed measurement can undo
        /// it. Reported by `SteeringPilot.WriteInput` rather than remembered here, and that is not
        /// bookkeeping pedantry: the adapter asks, the pilot decides whether to write, and the
        /// displacement being measured is the client's response to whatever the pilot actually did.
        /// On a run where `DRELAY_DODGE_ACT=1` but the pilot is in `hold` mode the two disagree, and
        /// a divisor taken from the request would silently mis-scale the budget for the whole run.
        /// </summary>
        private static float _lastWrittenScale = 1.0f;

        internal static void NoteWrittenScale(float scale)
        {
            _lastWrittenScale = scale > 0.05f ? scale : 1.0f;
        }

        // --- The speed estimate ------------------------------------------------------------

        /// <summary>
        /// The resolved speed law at 60 Hz: `(3.5 + 5.0*(SPD+10)/75)` at the SPD the live sweep
        /// implies, and the same prior `SolverHarness` uses. Only ever used before anything has
        /// moved and on frames where nothing did.
        /// </summary>
        private const float NominalSpeed = 8.2f;

        /// <summary>Below this the frame's displacement is noise (or a collision refusal), not a speed.</summary>
        private const float MinStepForSpeed = 0.02f;

        /// <summary>A teleport is a zone change, not a speed. `PHASE3-HANDOFF.md`: the step is ~0.13.</summary>
        private const float MaxStepForSpeed = 5.0f;

        private const float MinSpeed = 0.5f;
        private const float MaxSpeed = 40.0f;

        private static float _speedPerSecond = NominalSpeed;
        private static float _speedLastX = float.NaN;
        private static float _speedLastY = float.NaN;

        // --- The course, which is the direction the BODY is going ---------------------------

        /// <summary>
        /// The world heading the character is actually moving along, and whether it was measured.
        ///
        /// THIS IS THE `PlayerView.CourseRadians` THE FORWARD GOAL IS BUILT FROM, and it is measured
        /// from the same displacement the speed estimate uses -- one subtraction, one `Atan2`, on a
        /// frame that already did the subtraction. Nothing here reads the input, and that is the
        /// point: the input says what was ASKED FOR, and on a frame the dodge overrides, the answer
        /// to "where am I going" is the displacement and not the request.
        /// </summary>
        private static float _courseRadians;
        private static bool _courseValid;

        /// <summary>Frames a course survives after the character stops moving.</summary>
        private const int CourseHoldFrames = 6;

        private static int _courseStaleFrames = CourseHoldFrames + 1;

        /// <summary>
        /// Frames on which the forward goal came from a measured course rather than from the
        /// player's steering. Reported, because it is the one number that says whether the goal is
        /// the character's own motion or the fallback -- and a run where it is zero is a run in
        /// which the plan's anchor was never exercised.
        /// </summary>
        private static long _framesWithCourse;

        /// <summary>
        /// Frames on which the forward goal came from THE PLAYER'S HELD DIRECTION rather than from
        /// the measured course.
        ///
        /// THIS IS THE CHANGE'S OWN LIVE INSTRUMENT, AND IT IS THE ONE NUMBER THAT MAKES THE
        /// OTHERWISE INVISIBLE VISIBLE. Before it, the goal was the course on 96.8 % of the moves
        /// of `20261015-153805-p26156` -- the direction the body was already going, which while the
        /// dodge is steering is the dodge's own last command. The defect was a feedback loop and
        /// the only way to see it in a live run is to count where the goal came from; `course 137
        /// of 7169` next to `goal 4803 of 7169` is that count, and a run where `goal` is zero is a
        /// run in which `GameInput.Moving` never reached the solver and the change did nothing.
        /// </summary>
        private static long _framesWithPlayerGoal;

        // --- The counters the report is made of --------------------------------------------

        private static readonly long[] ReasonCounts = new long[16];
        private static readonly CostHistogram DecisionCost = new CostHistogram();

        /// <summary>Per-threat classification, over the whole run. See `DodgeSolver.ThreatVerdict`.</summary>
        private static readonly long[] VerdictCounts = new long[8];
        private static long _verdictTotal;
        private static long _framesWithRelevantThreats;
        private static long _framesWithADodgeableThreat;
        private static long _framesNothingToDoAbout;

        private static long _frames;        private static long _framesNoWorld;
        private static long _framesEvaluated;
        private static long _framesMoved;
        private static long _framesWouldEscape;
        private static long _framesNoAdmissible;
        private static long _framesStandSafe;
        private static long _framesIntentSafe;
        private static long _framesHeld;
        private static long _threatsConsidered;
        private static long _threatsCulledByCap;
        private static long _candidatesAdmissible;
        private static long _candidatesEvaluated;
        private static long _candidateFrames;
        private static long _speedObservedFrames;
        private static long _speedBlockedFrames;
        private static long _logLines;
        private static int _failureLogged;
        private static bool _firstDecisionLogged;

        /// <summary>
        /// The first report arrives at 300 frames and every 900 after that.
        ///
        /// 300 RATHER THAN 900, AND A PROBE RUN IS WHY. Five seconds of a run is enough to answer
        /// "does this thing run at all", and a report that first appears at 15 seconds is a report
        /// that a short session -- and the synthetic probe, which is a short session by construction
        /// -- never produces. That is defect 3.8's shape one step further out: not an empty file
        /// believed, but a *missing* line that reads as an adapter that never ran. The steady-state
        /// cadence stays at 900, which is what the log's own volume wants.
        /// </summary>
        private static long _nextReportFrame = 300;

        // --- Public surface ----------------------------------------------------------------

        /// <summary>
        /// Whether the adapter is switched on and has everything it needs.
        /// </summary>
        public static bool Enabled => _enabled && _resolved;

        /// <summary>
        /// JIT every method on the frame path, before the patch goes live.
        ///
        /// A METHOD COMPILED ON ITS FIRST CALL IS COMPILED FROM WHEREVER THAT CALL COMES FROM, and
        /// here that is inside a patched game frame. `SPIKE3.2-OFFLINE-SOLVER.md` section 4.4
        /// measured what that costs offline: the first `Decide` was 5683 us against a p99 of 448 --
        /// the JIT, reported as a max. The same stall in a live frame is a dropped frame, and the
        /// whole point of the envelope work in that section was to keep the decision inside 300 us.
        ///
        /// `PrepareMethod` covers the method named and nothing it calls, so the callees are named.
        /// The solver's are INSTANCE methods, which is why this is not `HookEntry.PrepareStatic`.
        /// </summary>
        public static void Warm()
        {
            Prepare(typeof(DodgeAdapter), "OnFrame", BindingFlags.Public | BindingFlags.Static);
            Prepare(typeof(DodgeAdapter), "FillThreats", BindingFlags.NonPublic | BindingFlags.Static);
            Prepare(typeof(DodgeAdapter), "CouldReach", BindingFlags.NonPublic | BindingFlags.Static);
            Prepare(typeof(DodgeAdapter), "RefreshGrid", BindingFlags.NonPublic | BindingFlags.Static);
            Prepare(typeof(DodgeAdapter), "UpdateSpeed", BindingFlags.NonPublic | BindingFlags.Static);
            Prepare(typeof(DodgeAdapter), "Record", BindingFlags.NonPublic | BindingFlags.Static);
            Prepare(typeof(DodgeAdapter), "Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Prepare(typeof(DodgeAdapter), "MaybeReport", BindingFlags.NonPublic | BindingFlags.Static);
            Prepare(typeof(DodgeAdapter), "DecisionLine", BindingFlags.NonPublic | BindingFlags.Static);
            Prepare(typeof(DodgeAdapter), "CheckPrecedence", BindingFlags.NonPublic | BindingFlags.Static);

            Prepare(typeof(DodgeSolver), "Decide", BindingFlags.Public | BindingFlags.Instance);
            Prepare(typeof(DodgeSolver), "BuildAndAdmit", BindingFlags.NonPublic | BindingFlags.Instance);
            Prepare(typeof(DodgeSolver), "BuildAdmitEnvelope", BindingFlags.NonPublic | BindingFlags.Instance);
            Prepare(typeof(DodgeSolver), "Admit", BindingFlags.NonPublic | BindingFlags.Instance);
            Prepare(typeof(DodgeSolver), "ScoreAll", BindingFlags.NonPublic | BindingFlags.Instance);
            Prepare(typeof(DodgeSolver), "CountUnavoidable", BindingFlags.NonPublic | BindingFlags.Instance);
            Prepare(typeof(DodgeSolver), "StaticallyDurable", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        private static void Prepare(Type owner, string name, BindingFlags flags)
        {
            MethodInfo method = owner.GetMethod(name, flags);
            if (method == null)
            {
                return;
            }
            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
            }
            catch (Exception)
            {
                // A method the runtime will not take a handle for is skipped; the rest of the path
                // is still warmed, and the failure is named in the publish log that follows.
            }
        }

        /// <summary>Whether the decision reaches the actuator. False is the dry run.</summary>
        public static bool Acts => _enabled && _resolved && _act;

        /// <summary>Why not, when not.</summary>
        public static string UnresolvedReason => _unresolvedReason;

        /// <summary>The one line the log gets about this file's own configuration.</summary>
        public static string Status()
        {
            return _statusLine;
        }

        // ===================================================================================
        // Resolution
        // ===================================================================================

        /// <summary>
        /// Reads the switches and resolves every accessor the live snapshot needs.
        ///
        /// A FAILURE IS NOT FATAL AND MUST NOT BE. The sensor, the trace, the actuator and the whole
        /// of Phase 1 work without this file; a dodge that cannot resolve one field is a dodge that
        /// does not run, not a hook that does not load. What it must not do is *half* run: the
        /// player's position and radius are required, because a snapshot without them is a snapshot
        /// full of zeroes and the solver would answer it confidently.
        /// </summary>
        public static bool Resolve(Assembly game)
        {
            string switchValue = Environment.GetEnvironmentVariable(EnvDodge);
            if (string.IsNullOrEmpty(switchValue) || switchValue == "0")
            {
                _unresolvedReason = EnvDodge + " is not set";
                _statusLine = "dodge idle: " + _unresolvedReason;
                return false;
            }

            _enabled = true;
            _act = Environment.GetEnvironmentVariable(EnvAct) == "1";

            try
            {
                const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static;

                Type worldType = game.GetType(ManagedArmingProbe.WorldTypeName);
                Type playerType = game.GetType(ManagedArmingProbe.PlayerTypeName);
                if (worldType == null || playerType == null)
                {
                    _unresolvedReason = "types not found: World=" + (worldType != null)
                                        + " Player=" + (playerType != null);
                    _statusLine = "dodge DISARMED: " + _unresolvedReason;
                    return false;
                }

                FieldInfo sharedField = worldType.GetField("Shared", Any);
                FieldInfo playerField = worldType.GetField("Player", Any);
                FieldInfo positionField = playerType.GetField("Position", Any);
                FieldInfo positionX = positionField?.FieldType.GetField("X");
                FieldInfo positionY = positionField?.FieldType.GetField("Y");

                if (sharedField == null || playerField == null
                    || positionX == null || positionY == null)
                {
                    _unresolvedReason = "the player path did not resolve: World.Shared="
                                        + (sharedField != null) + " World.Player="
                                        + (playerField != null) + " Player.Position.XY="
                                        + (positionX != null && positionY != null);
                    _statusLine = "dodge DISARMED: " + _unresolvedReason;
                    return false;
                }

                _getWorld = FieldAccessor.Static(sharedField);
                _getPlayer = FieldAccessor.Reference(playerField);
                _getPlayerX = FieldAccessor.NestedFloat(positionField, positionX);
                _getPlayerY = FieldAccessor.NestedFloat(positionField, positionY);

                // The rest are optional and each one degrades a named thing rather than the run.
                FieldInfo radiusField = FindRadius(playerType);
                if (radiusField != null)
                {
                    _getPlayerRadius = FieldAccessor.Float(radiusField);
                }
                FieldInfo healthField = playerType.GetField("Health", Any);
                if (healthField != null)
                {
                    _getPlayerHealth = FieldAccessor.Double(healthField);
                }
                FieldInfo deadField = worldType.GetField("PlayerDead", Any);
                if (deadField != null)
                {
                    _getPlayerDead = FieldAccessor.Bool(deadField);
                }
                FieldInfo safeField = worldType.GetField("SafeWorld", Any);
                if (safeField != null)
                {
                    _getSafeWorld = FieldAccessor.Bool(safeField);
                }
                FieldInfo rotationField = worldType.GetField("WorldRotation",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (rotationField != null)
                {
                    _getWorldRotation = FieldAccessor.StaticFloat(rotationField);
                }

                FieldInfo sceneField = worldType.GetField("GameScene", Any);
                FieldInfo inputField = sceneField?.FieldType.GetField("GameInput", Any);
                if (inputField != null)
                {
                    _getGameScene = FieldAccessor.Reference(sceneField);
                    _getInput = FieldAccessor.Reference(inputField);
                    FieldInfo movingField = inputField.FieldType.GetField("Moving", Any);
                    if (movingField != null)
                    {
                        _getMoving = FieldAccessor.Bool(movingField);
                    }
                    FieldInfo angleField = inputField.FieldType.GetField("MoveAngle", Any);
                    FieldInfo radiansField = angleField?.FieldType.GetField("Radians", Any);
                    if (angleField != null && radiansField != null)
                    {
                        _getMoveAngleRadians = FieldAccessor.NestedFloat(angleField, radiansField);
                    }
                }

                Type clockType = game.GetType("DarzasDominion.Graphics.ClientTime");
                FieldInfo staticClock = game.GetType("DarzasDominion.Graphics.DarzaWindow")
                    ?.GetField("Time", Any);
                FieldInfo logicTotal = clockType?.GetField("LogicTotal", Any);
                if (staticClock != null && logicTotal != null)
                {
                    _getClock = FieldAccessor.Static(staticClock);
                    _getLogicTotal = FieldAccessor.Float(logicTotal);
                }

                ResolveGrid(worldType, Any);
                ResolvePrecedence(playerType, Any);

                _config = DodgeConfig.FromEnvironment();
                // `DryRun` is recorded from the switch rather than trusted from the environment
                // alone, so one number describes the run: the adapter's own answer to "would this
                // move anything".
                _config.DryRun = !_act;
                _solver = new DodgeSolver(_config);

                _resolved = true;
                // SHORT, BECAUSE THE LOG TRUNCATES AT 256 CHARACTERS AND DOES IT SILENTLY.
                // `DrelayLog.cpp` formats with `_vsnprintf_s(..., _TRUNCATE, ...)`, so an
                // over-long line loses its tail with no marker -- and the tail is where the
                // reasons histogram and the cost percentiles live. The configuration is therefore
                // its own line rather than appended to this one.
                _statusLine = string.Format(CultureInfo.InvariantCulture,
                    "dodge ARMED: mode={0} | preset {1} | stand {2} | grid {3} | intent {4} | health {5}",
                    _act ? "LIVE" : "DRY RUN",
                    _config.PresetName,
                    _config.StandStops ? "STOPS the player" : "yields to the player",
                    _gridResolved ? "World.byte[,]" : "MISSING (walls ignored!)",
                    _getMoveAngleRadians != null ? "world-compensated" : "MISSING (0.0)",
                    _getPlayerHealth != null ? "Player.Health" : "MISSING (0.0)");
                HookEntry.AppendLog("INFO", _statusLine);
                // TWO LINES, AND THE SPLIT IS FORCED BY THE SAME 256-BYTE BUFFER. The plan model
                // added four numbers to the configuration and the single line went past the limit,
                // where it would have been cut with no mark -- and the tail it would have lost is
                // the weights. See `DodgeConfig.DescribePlan`.
                HookEntry.AppendLog("INFO", "dodge config | " + _config.DescribePlan());
                HookEntry.AppendLog("INFO", "dodge config | " + _config.DescribeWeights());
                if (!_gridResolved)
                {
                    // SHORT, for the truncation reason `Resolve` records.
                    HookEntry.AppendLog("WARN", "the dodge's collision grid did NOT resolve: "
                        + "admission rules 1-3 are vacuous and every candidate is admitted through "
                        + "walls. The grid is found as the only byte[,] on World.");
                }
                if (!_act)
                {
                    HookEntry.AppendLog("INFO", "the dodge is in DRY RUN: it will log every decision "
                        + "it would have acted on, and it will not write the input. Set "
                        + EnvAct + "=1 (and run the pilot in `dodge` mode) for it to move anything.");
                }
                HookEntry.FlushLog();
                return true;
            }
            catch (Exception ex)
            {
                _unresolvedReason = ex.GetType().Name + ": " + ex.Message;
                _statusLine = "dodge DISARMED: " + _unresolvedReason;
                _resolved = false;
                return false;
            }
        }

        private static void ResolvePrecedence(Type playerType, BindingFlags any)
        {
            FieldInfo totemsField = playerType.GetField("Totems", any);
            if (totemsField == null)
            {
                return;
            }
            try
            {
                Type totemType = totemsField.FieldType;
                MethodInfo[] methods = totemType.GetMethods(any);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m.Name != "Has" || m.ReturnType != typeof(bool))
                    {
                        continue;
                    }
                    ParameterInfo[] parameters = m.GetParameters();
                    if (parameters.Length != 1 || !parameters[0].ParameterType.IsEnum)
                    {
                        continue;
                    }
                    _totemHas = m;
                    // `CharacterTotem.Impermanence == 0x20 == 32`, resolved from the pool and
                    // recorded at CLIENT-MAP.md section 7.1 and section 10.
                    _impermanence = Enum.ToObject(parameters[0].ParameterType, 32);
                    _getTotems = FieldAccessor.Reference(totemsField);
                    return;
                }
            }
            catch (Exception)
            {
                _totemHas = null;
                _getTotems = null;
            }
        }

        /// <summary>
        /// Which escape path this character is on, asked once per player object.
        ///
        /// ONCE PER PLAYER OBJECT, NOT ONCE PER FRAME, and the reason is the cost rather than the
        /// answer: the totems are a property of the character and only change in town, while this
        /// check goes through `MethodInfo.Invoke` -- a boxed argument and a reflection call, which
        /// is exactly the pattern `FieldAccessor` exists to keep off the frame path.
        /// </summary>
        private static void CheckPrecedence(object player)
        {
            if (ReferenceEquals(player, _precedencePlayer))
            {
                return;
            }
            _precedencePlayer = player;

            if (_getTotems == null || _totemHas == null || _impermanence == null)
            {
                _precedence = 0;
                return;
            }

            try
            {
                object totems = _getTotems(player);
                bool has = totems != null
                           && (bool)_totemHas.Invoke(totems, new[] { _impermanence });
                _precedence = has ? 2 : 1;
            }
            catch (Exception)
            {
                _precedence = 0;
            }

            if (_precedence == 2 && !_precedenceLogged)
            {
                _precedenceLogged = true;
                HookEntry.AppendLog("WARN", "PRECEDENCE: Impermanence is equipped, so "
                    + "World.RequestEscape() takes its CHANNELLED branch -- a cast the client "
                    + "cancels on movement (World.cs:13043). The live dodge is DISABLED for this "
                    + "character; the decision still runs and is still logged.");
                HookEntry.FlushLog();
            }
        }

        private static FieldInfo FindRadius(Type playerType)
        {
            Type t = playerType;
            while (t != null && t != typeof(object))
            {
                FieldInfo f = t.GetField("Radius",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null)
                {
                    return f;
                }
                t = t.BaseType;
            }
            return null;
        }

        private static void ResolveGrid(Type worldType, BindingFlags any)
        {
            FieldInfo candidate = null;
            int count = 0;
            FieldInfo[] fields = worldType.GetFields(any);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType == typeof(byte[,]))
                {
                    candidate = fields[i];
                    count++;
                }
            }
            if (candidate == null || count != 1)
            {
                _gridResolved = false;
                return;
            }
            try
            {
                _getGridArray = FieldAccessor.Reference(candidate);
                _gridAt = FieldAccessor.ByteArray2D(candidate.FieldType);
                _gridResolved = true;
            }
            catch (Exception)
            {
                _gridResolved = false;
            }
        }

        // ===================================================================================
        // The per-frame entry, called from the sensor path at the frame tick
        // ===================================================================================

        /// <summary>
        /// One frame: assemble the snapshot, decide, and publish or log.
        ///
        /// IT RUNS AFTER `SensorRead.OnFrame` AND BORROWS THAT FRAME'S PROJECTILE LIST, for the
        /// reason the trace recorder gives for the same choice: the walk of the game's drawing layer
        /// is the expensive part, and a run whose whole purpose is to be an ordinary run must not
        /// pay for it twice.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void OnFrame(List<object> liveProjectiles, List<ThreatKey> liveKeys, int frame)
        {
            if (!_enabled || !_resolved)
            {
                return;
            }

            // THE SLOT IS CLEARED WHATEVER HAPPENS. A command that survives a frame in which the
            // adapter did not run is a command built for a world that no longer exists, and the
            // integrator would consume it as though it were current.
            _pending = DodgeCommand.None;
            _pendingFresh = false;

            try
            {
                _frames++;

                object world = _getWorld();
                object player = world == null ? null : _getPlayer(world);
                if (world == null || player == null)
                {
                    _framesNoWorld++;
                    return;
                }

                float px = _getPlayerX(player);
                float py = _getPlayerY(player);
                CheckPrecedence(player);
                float radius = _getPlayerRadius != null ? _getPlayerRadius(player) : _config.DefaultPlayerRadius;
                if (radius <= 0.0f)
                {
                    radius = _config.DefaultPlayerRadius;
                }
                float dt = TrajectorySampler.LastFrameSeconds();

                // The measurement comes FIRST, because it describes the frame that has just ended:
                // the displacement between this tick and the last one was produced by the input
                // written one integrator call ago, at the magnitude published one frame ago.
                UpdateSpeed(px, py, dt);

                float rotation = _getWorldRotation != null ? _getWorldRotation() : 0.0f;
                float clock = ReadClock();

                DodgeSnapshot s = Snapshot;
                s.Clear();
                s.Frame = frame;
                s.ClockMs = clock;
                s.FrameSeconds = dt;
                s.Player.X = px;
                s.Player.Y = py;
                s.Player.Radius = radius;
                s.Player.Health = _getPlayerHealth != null ? _getPlayerHealth(player) : 0.0;
                s.Player.SpeedPerSecond = _speedPerSecond;
                s.Player.Dead = _getPlayerDead != null && _getPlayerDead(world);
                s.Player.SafeWorld = _getSafeWorld != null && _getSafeWorld(world);

                bool moving = false;
                float intentInput = 0.0f;
                object scene = _getGameScene != null ? _getGameScene(world) : null;
                object input = scene != null && _getInput != null ? _getInput(scene) : null;
                if (input != null)
                {
                    if (_getMoving != null)
                    {
                        moving = _getMoving(input);
                    }
                    if (_getMoveAngleRadians != null)
                    {
                        intentInput = _getMoveAngleRadians(input);
                    }
                }
                s.Player.Moving = moving;

                // THE INTENT IS CONVERTED TO A WORLD HEADING, AND THE OFFLINE HARNESS DOES NOT DO
                // THIS. `World.cs:12692` computes `angle = MoveAngle + (-WorldRotation + aimOffset)`,
                // so `MoveAngle` is an INPUT-frame number and the solver's snapshot is made of world
                // positions -- every candidate heading it produces is a world heading, and the
                // intent has to be one too or the "is the player's own steering already safe" test
                // compares two different frames. `SolverHarness` reads `intent` raw and its
                // `Rotation` field is never used, so on a session with a rotating camera the
                // harness's intent test is slightly wrong. It is recorded here rather than silently
                // changed there: the harness's numbers are the spike's published record, and a
                // correction that moved them would need its own run to be evidence of anything.
                //
                // `aimOffset` is `hehe(278)` for `Player.HasEffect(160)` and is unresolved, so it is
                // zero -- the same assumption `SteeringPilot.PhaseAngle` makes, and it is recorded
                // as an assumption in both places rather than in one.
                s.Player.IntentRadians = Normalize(intentInput - rotation);
                // AND WHETHER THAT ANGLE WAS ACTUALLY READ. `intentInput` is 0.0 when the accessor
                // did not resolve, and 0.0 radians is a legal heading -- due east -- so without this
                // the solver would obey, and project a goal along, a direction the player never
                // asked for. The status line already says `intent MISSING (0.0)`; this is the same
                // fact carried to where it changes a decision.
                s.Player.IntentValid = _getMoveAngleRadians != null;

                // AND THE COURSE, WHICH IS THE WORLD-FRAME FACT AND NEEDS NO CONVERSION. It was
                // measured from a world-space displacement this same frame (see `UpdateSpeed`), so
                // the camera's rotation is already out of it -- which is the whole reason the
                // forward goal is built from the course rather than from the input angle. At a
                // camera rotation of 13.7 degrees the two differ by exactly 13.7 degrees, and a goal
                // built from the unconverted one points the dodge 13.7 degrees off the direction the
                // character is travelling.
                s.Player.CourseRadians = _courseRadians;
                s.Player.CourseValid = _courseValid;

                s.Grid = RefreshGrid(world, px, py);

                // Rule 7 is vacuous on the live path; the count in the report is the mark of it.
                FillThreats(s, liveProjectiles, liveKeys, px, py, radius);

                long t0 = Stopwatch.GetTimestamp();
                DodgeDecision d = _solver.Decide(s);
                long elapsed = Stopwatch.GetTimestamp() - t0;
                d.CostMicroseconds = (int)((elapsed * 1000000L) / Stopwatch.Frequency);

                Record(s, d);
                Publish(d);
                MaybeReport();

                // THE POSITIVE WITNESS, AND IT EXISTS BECAUSE "NO REPORT" HAS FOUR CAUSES. The
                // adapter may never have been called, may have been called with no world, may have
                // decided and not yet reached its first report, or may have decided and thrown --
                // and only the last of those leaves a line of its own. One line at the first
                // decision separates all four, exactly as `MANAGED REPLACEMENT entered (call 1)`
                // does for the frame tick and for the same reason: a hook that never took effect
                // leaves every other counter agreeing just as happily.
                if (!_firstDecisionLogged)
                {
                    _firstDecisionLogged = true;
                    HookEntry.AppendLog("INFO", string.Format(CultureInfo.InvariantCulture,
                        "dodge: first decision on frame {0}: {1} over {2} threat(s), "
                        + "{3} candidate(s), speed {4:0.00} u/s, {5}us",
                        frame, d.Reason, s.Threats.Count, d.EvaluatedCount,
                        s.Player.SpeedPerSecond, d.CostMicroseconds));
                    HookEntry.FlushLog();
                }

                if (d.Action == DodgeAction.Move && _logLines < MaxDecisionLines)
                {
                    _logLines++;
                    DecisionLine("WOULD MOVE", frame, s, d);
                }
                else if (d.Action == DodgeAction.Escape && _logLines < MaxDecisionLines)
                {
                    _logLines++;
                    DecisionLine("WOULD ESCAPE", frame, s, d);
                }
            }
            catch (Exception ex)
            {
                // ONCE PER RUN, for the reason the frame path's own catch is written that way: an
                // exception thrown every frame would fill the log ring and hide everything else.
                if (System.Threading.Interlocked.Exchange(ref _failureLogged, 1) == 0)
                {
                    HookEntry.AppendLog("ERROR", "the dodge adapter threw; the game is unaffected "
                                                 + "and the solver simply does not run on this frame: "
                                                 + ex.GetType().Name + ": " + ex.Message + " | "
                                                 + HookEntry.FirstFrames(ex));
                    HookEntry.FlushLog();
                }
            }
        }

        private static float ReadClock()
        {
            if (_getClock == null || _getLogicTotal == null)
            {
                return 0.0f;
            }
            object c = _getClock();
            return c == null ? 0.0f : _getLogicTotal(c);
        }

        // ===================================================================================
        // The threat set
        // ===================================================================================

        /// <summary>
        /// Fills the snapshot's threat list from this frame's live projectiles.
        ///
        /// THE CULL IS THE RECORDER'S OWN, AND IT IS THE SAME PREDICATE FOR A REASON. A bullet whose
        /// sampled path cannot reach the player's disc cannot change the decision, and on a busy
        /// frame the sensor tracks well over a hundred bullets of which a handful matter --
        /// `SPIKE3.2-OFFLINE-SOLVER.md` section 6 item 2 names exactly this cull as the work the
        /// budget needs. Using the recorder's predicate rather than a second one also means the
        /// live decision and the recorded trace are decisions about the SAME set: a divergence there
        /// would be invisible and would quietly invalidate every offline comparison.
        /// </summary>
        private static void FillThreats(DodgeSnapshot s, List<object> projectiles, List<ThreatKey> keys,
                                        float px, float py, float playerRadius)
        {
            if (projectiles == null || keys == null)
            {
                return;
            }

            int count = projectiles.Count < keys.Count ? projectiles.Count : keys.Count;
            long declinedBefore = TrajectorySampler.Declined;
            for (int i = 0; i < count; i++)
            {
                if (s.Threats.Count >= MaxThreatsPerFrame)
                {
                    _threatsCulledByCap += count - i;
                    break;
                }
                if (!TrajectorySampler.FillTrajectory(projectiles[i], out Trajectory t))
                {
                    continue;
                }
                // THE IDENTITY IS THE SENSOR'S, AND WRITING ANYTHING ELSE IS DEFECT 3.9. The
                // sampler leaves `Key` untouched on purpose; the sensor resolved it and handed it
                // over in lockstep with the object list. `(0, startTime)` for every bullet is what a
                // recorded session looked like when this was got wrong.
                t.Key = keys[i].Packed;
                if (!CouldReach(t, px, py, playerRadius))
                {
                    continue;
                }
                s.Threats.Add(t);
                _threatsConsidered++;
            }
            _projectilesNotAHittableThreat += TrajectorySampler.Declined - declinedBefore;
        }

        /// <summary>Bullets considered per frame, at most. The same cap, and the same reason, as the recorder's.</summary>
        private const int MaxThreatsPerFrame = 48;

        /// <summary>
        /// Projectiles the sampler declined because the client says they can never hit a player.
        ///
        /// REPORTED BECAUSE IT IS THE ONLY WAY TO TELL THE FIX FROM ITS ABSENCE. A run in which the
        /// player fires and this reads zero is a run whose threat set is full of the player's own
        /// shots -- which is what every run before `20261015-151232-p5424` was, at 51.8 % of the
        /// threat records. See `TrajectorySampler.CannotHitPlayer`.
        /// </summary>
        private static long _projectilesNotAHittableThreat;

        /// <summary>
        /// Whether a bullet could reach the player's disc inside the horizon.
        ///
        /// THE SAME TEST `TraceRecorder.CouldReach` MAKES, DELIBERATELY DUPLICATED RATHER THAN
        /// SHARED. The two are the live decision's and the offline record's views of one question,
        /// and if they ever disagree the disagreement is the finding -- `SPIKE3.2-OFFLINE-SOLVER.md`
        /// section 4.6 is a session that cost an afternoon precisely because one predicate was
        /// written in two places and the two were never compared.
        /// </summary>
        private static bool CouldReach(Trajectory t, float px, float py, float playerRadius)
        {
            float r = playerRadius + t.Radius;
            float r2 = r * r;
            for (int sample = 0; sample < t.Count; sample++)
            {
                float dx = t.Xs[sample] - px;
                float dy = t.Ys[sample] - py;
                if ((dx * dx) + (dy * dy) <= r2)
                {
                    return true;
                }
            }
            float cx = t.Xs[0] - px;
            float cy = t.Ys[0] - py;
            float near = r + 2.0f;
            return (cx * cx) + (cy * cy) <= (near * near);
        }

        // ===================================================================================
        // The collision grid
        // ===================================================================================

        /// <summary>
        /// The client's own grid, windowed on the player, refreshed when their tile changes.
        ///
        /// THE INDEXING IS THE GAME'S AND GETTING IT WRONG LOOKS LIKE A WORKING DODGE. `World`
        /// indexes its grid with a C cast of the float position (`conv.i4`, truncation toward zero)
        /// after bounds-checking, and `FieldAccessor.ByteArray2D` already answers "not a wall" for a
        /// coordinate outside the map -- the same answer the game's own query gives. The window's
        /// row-major layout is `[x, y]`, which is what `CollisionWindow.At` reads.
        /// </summary>
        private static CollisionWindow RefreshGrid(object world, float px, float py)
        {
            CollisionWindow window = default(CollisionWindow);
            if (!_gridResolved || _getGridArray == null || _gridAt == null || world == null)
            {
                return window;
            }

            int originX = (int)px - GridRadius;
            int originY = (int)py - GridRadius;
            int size = 2 * GridRadius;

            if (!_gridWindowValid || originX != _gridOriginX || originY != _gridOriginY)
            {
                object array = _getGridArray(world);
                if (array == null)
                {
                    return window;
                }
                for (int gy = 0; gy < size; gy++)
                {
                    for (int gx = 0; gx < size; gx++)
                    {
                        GridCells[(gy * size) + gx] = _gridAt(array, originX + gx, originY + gy);
                    }
                }
                _gridOriginX = originX;
                _gridOriginY = originY;
                _gridWindowValid = true;
            }

            window.Present = true;
            window.OriginX = originX;
            window.OriginY = originY;
            window.Width = size;
            window.Height = size;
            window.Cells = GridCells;
            return window;
        }

        // ===================================================================================
        // The speed estimate
        // ===================================================================================

        /// <summary>
        /// The player's speed, measured from the client's own displacement.
        ///
        /// WHY A MEASUREMENT AND NOT THE LAW: see this file's header, note 3. The one subtlety is
        /// that in LIVE mode the displacement being measured was produced by the magnitude THIS file
        /// asked for, so it has to be divided back out -- otherwise a half-speed dodge would halve
        /// its own budget every frame and collapse to nothing in a handful of frames. The divisor is
        /// floored because `SpeedScale` is one of `{0.35, 0.7, 1.0}` by construction and dividing a
        /// noisy small number by an even smaller one is how a speed estimate runs away.
        /// </summary>
        private static void UpdateSpeed(float px, float py, float dt)
        {
            if (!float.IsNaN(_speedLastX) && dt > 0.0001f)
            {
                float dx = px - _speedLastX;
                float dy = py - _speedLastY;
                float moved = (float)Math.Sqrt((dx * dx) + (dy * dy));
                if (moved > MinStepForSpeed && moved < MaxStepForSpeed)
                {
                    // THE DIVISOR IS FLOORED, AND THE FLOOR IS NOT COSMETIC. `SpeedScale` is one of
                    // `{0.35, 0.7, 1.0}` by construction, so dividing by the exact value recovers
                    // `b`; dividing a noisy small displacement by an even smaller scale is how a
                    // speed estimate runs away, and a runaway speed is a candidate set the player
                    // cannot reach.
                    float divisor = _lastWrittenScale > 0.34f ? _lastWrittenScale : 1.0f;
                    float estimate = moved / dt / divisor;
                    if (estimate < MinSpeed)
                    {
                        estimate = MinSpeed;
                    }
                    if (estimate > MaxSpeed)
                    {
                        estimate = MaxSpeed;
                    }
                    _speedPerSecond = estimate;
                    _speedObservedFrames++;
                }
                else if (moved <= MinStepForSpeed)
                {
                    // A pinned frame, a hitching frame, or a frame the client refused. The previous
                    // estimate stands: reading zero here would make the solver report `Immobile` and
                    // stop dodging exactly when the player is wedged against terrain, which is
                    // PHASE3-HANDOFF.md section 3.3's failure with a different cause.
                    _speedBlockedFrames++;
                }

                // THE COURSE IS MEASURED FROM THE SAME DISPLACEMENT, AND IT OUTLIVES A STOPPED
                // FRAME BY A FEW FRAMES. A character who is pinned for one frame has not changed
                // direction, and dropping the goal for that frame would make the dodge stand up and
                // re-derive its direction on the next one -- which is the flinch the goal exists to
                // remove. Six frames is a tenth of a second: long enough to ride out a blocked step,
                // short enough that a genuine stop becomes a genuine "no course".
                if (moved > MinStepForSpeed && moved < MaxStepForSpeed)
                {
                    _courseRadians = (float)Math.Atan2(dy, dx);
                    _courseValid = true;
                    _courseStaleFrames = 0;
                }
                else
                {
                    _courseStaleFrames++;
                    if (_courseStaleFrames > CourseHoldFrames)
                    {
                        _courseValid = false;
                    }
                }
            }
            _speedLastX = px;
            _speedLastY = py;
        }

        // ===================================================================================
        // The slot the pilot consumes
        // ===================================================================================

        /// <summary>
        /// The command for this frame, taken once.
        ///
        /// `Stop` IS NOT THE SAME AS `None`, AND THE DISTINCTION IS THE WHOLE OF SECTION 5.4.
        /// `StandSafe` is the specification's preferred answer and it means "do not move" -- so the
        /// pilot must clear the input, because leaving the player's own heading in place is not
        /// standing still, it is walking on. Every other `None` -- `NoThreat`, `NoAdmissible`,
        /// `Immobile`, `Degraded` -- means the player's own steering is untouched, per section 5.7
        /// and defect 3.7. Conflating the two is a dodge that either refuses to stand or refuses to
        /// yield, and both look like working code.
        /// </summary>
        internal static DodgeCommand TakeCommand(out float worldHeadingRadians, out float speedScale)
        {
            worldHeadingRadians = _pendingHeading;
            speedScale = _pendingScale;
            if (!_pendingFresh)
            {
                return DodgeCommand.None;
            }
            _pendingFresh = false;
            return _pending;
        }

        private static void Publish(DodgeDecision d)
        {
            if (!_act)
            {
                // The dry run's whole contract: the decision is made, counted and logged, and
                // nothing reaches the input. `_lastWrittenScale` is the pilot's to report and is
                // untouched here, because in a dry run the pilot is either not steering at all or
                // steering its own plan -- and the speed measurement wants the truth either way.
                return;
            }

            // PRECEDENCE, AND IT IS CHECKED HERE RATHER THAN AT THE START OF THE FRAME. An escape
            // the hook has already committed to is authoritative; this is the conservative standing
            // version of that rule, applied to every frame of a run in which the client could take
            // the channelled path at all. See `CheckPrecedence`.
            if (_precedence == 2)
            {
                _framesYieldedToEscape++;
                return;
            }

            switch (d.Action)
            {
                case DodgeAction.Move:
                    _pending = DodgeCommand.Move;
                    _pendingHeading = d.HeadingRadians;
                    _pendingScale = d.SpeedScale;
                    _pendingFresh = true;
                    break;

                case DodgeAction.Escape:
                    // DELIVERABLE 3.4, AND DELIBERATELY NOT HERE. The adapter computes the gate's
                    // input and records it; calling `World.RequestEscape()` from a run that only
                    // asked for a dodge would be a live cast the operator did not request, and the
                    // precedence rule around the Impermanence totem has to be settled first
                    // (CLIENT-MAP.md section 6.5). The frame is counted in `Record` and named in the
                    // report.
                    break;

                case DodgeAction.None:
                    if (d.Reason == DodgeReason.StandSafe && _config.StandStops)
                    {
                        _pending = DodgeCommand.Stop;
                        _pendingHeading = d.HeadingRadians;
                        _pendingScale = 0.0f;
                        _pendingFresh = true;
                    }
                    // AND WHEN `StandStops` IS OFF, `stand` PUBLISHES NOTHING -- which is the whole
                    // of the "assist" policy. The pilot then leaves `GameInput` exactly as the
                    // player's own hands left it, so a `stand` verdict becomes a no-op instead of a
                    // command that stops a walking character. See `DodgeConfig.StandStops` for the
                    // measurement that made this a switch rather than a constant.
                    break;
            }
        }

        // ===================================================================================
        // The counters and the report
        // ===================================================================================

        private static void Record(DodgeSnapshot s, DodgeDecision d)
        {
            int reason = (int)d.Reason;
            if (reason >= 0 && reason < ReasonCounts.Length)
            {
                ReasonCounts[reason]++;
            }

            DecisionCost.Add(d.CostMicroseconds);

            if (s.Threats.Count > 0)
            {
                _framesEvaluated++;
            }
            if (d.EvaluatedCount > 0)
            {
                _candidateFrames++;
                _candidatesAdmissible += d.AdmissibleCount;
                _candidatesEvaluated += d.EvaluatedCount;
            }
            switch (d.Action)
            {
                case DodgeAction.Move:
                    _framesMoved++;
                    break;
                case DodgeAction.Escape:
                    _framesWouldEscape++;
                    break;
            }
            switch (d.Reason)
            {
                case DodgeReason.NoAdmissible:
                    _framesNoAdmissible++;
                    break;
                case DodgeReason.StandSafe:
                    _framesStandSafe++;
                    break;
                case DodgeReason.IntentSafe:
                    _framesIntentSafe++;
                    break;
            }
            if (d.Held)
            {
                _framesHeld++;
            }
            if (s.Player.CourseValid)
            {
                _framesWithCourse++;
            }
            if (_solver.GoalSource == GoalSource.PlayerIntent)
            {
                _framesWithPlayerGoal++;
            }

            // THE CLASSIFICATION, COUNTED -- "should this bullet be dodged at all" is the question
            // the admission rules cannot ask, and it is the one the operator most needs answered:
            // "it did not dodge that" and "that was not dodgeable" look identical from the outside
            // and are completely different findings.
            int relevant = 0;
            int dodgeable = 0;
            for (int i = 0; i < s.Threats.Count; i++)
            {
                ThreatVerdict v = _solver.VerdictOf(i);
                int index = (int)v;
                if (index >= 0 && index < VerdictCounts.Length)
                {
                    VerdictCounts[index]++;
                }
                _verdictTotal++;
                if (v != ThreatVerdict.Clear)
                {
                    relevant++;
                }
                if (v == ThreatVerdict.Dodgeable)
                {
                    dodgeable++;
                }
            }
            if (relevant > 0)
            {
                _framesWithRelevantThreats++;
                if (dodgeable > 0)
                {
                    _framesWithADodgeableThreat++;
                }
                else
                {
                    _framesNothingToDoAbout++;
                }
            }
        }

        /// <summary>
        /// A move the solver would have made, on one line, with the numbers that produced it.
        ///
        /// THIS IS THE DRY RUN'S PRODUCT AND IT IS THE POINT OF THE MODE. `IMPLEMENTATION-PLAN.md`'s
        /// paired trial wants "the moment it would have moved and the moment it would have escaped",
        /// on the same timeline as the `HealthUpdate` trace and the death, so that every death is
        /// accounted for in one of the two buckets. A summary count cannot be lined up against a
        /// death; a line with a frame number can.
        /// </summary>
        private static void DecisionLine(string what, int frame, DodgeSnapshot s, DodgeDecision d)
        {
            // UNDER 256 CHARACTERS, AND THAT IS A CONSTRAINT RATHER THAN A STYLE. See `Resolve`.
            HookEntry.AppendLog("INFO", string.Format(CultureInfo.InvariantCulture,
                "dodge {0} | frame {1} | heading {2:0.0}deg scale {3:0.00} | {4} held {5} | "
                + "candidates {6}/{7} | durability {8:0}ms clearance {9:0.00} | "
                + "goal {10:0.0}deg (course {11}) | threats {12} unavoidable {13} | cost {14}us",
                what, frame,
                d.HeadingRadians * 180.0 / Math.PI, d.SpeedScale, d.Reason,
                d.Held ? 1 : 0, d.AdmissibleCount, d.EvaluatedCount,
                d.DurabilitySeconds * 1000.0, d.Clearance,
                _solver.GoalRadians * 180.0 / Math.PI, s.Player.CourseValid ? 1 : 0,
                s.Threats.Count, d.UnavoidableThreats,
                d.CostMicroseconds));
            HookEntry.FlushLog();
        }

        /// <summary>Lines of per-decision detail a run will write. Past this, the summary is the record.</summary>
        private const int MaxDecisionLines = 400;

        private static void MaybeReport()
        {
            if (_frames < _nextReportFrame)
            {
                return;
            }
            _nextReportFrame = _frames + 900;
            Log();
        }

        /// <summary>
        /// The dodge's own numbers, in the shape `PHASE 2 NUMBERS` established.
        ///
        /// THREE LINES, AND THE SPLIT IS FORCED RATHER THAN CHOSEN. `DrelayLog.cpp` formats a line
        /// with `_vsnprintf_s(..., _TRUNCATE, ...)` into a `DRELAY_LOG_LINE_LEN`-byte buffer -- 256
        /// -- and a truncated line carries no mark saying so. The first version of this was two
        /// lines and the second was already about 330 characters, so the reasons histogram and the
        /// threat classification -- the two things a reader reaches for first -- were being silently
        /// cut off. The first line is the verdict, the second is the threat set, the third is the
        /// detail; all three are inside the buffer, and `PHASE 3 DODGE NUMBERS` is the one the
        /// runner's own check reads.
        ///
        /// THE MODE WORD IS IN THE FIRST LINE because the same counters mean two different things
        /// depending on it, and a reader who has to know which switch was set to read a number has
        /// been given a number they cannot use.
        /// </summary>
        public static void Log()
        {
            if (!_enabled || !_resolved)
            {
                return;
            }

            HookEntry.AppendLog("INFO", string.Format(CultureInfo.InvariantCulture,
                "PHASE 3 DODGE NUMBERS | mode {0} | frames {1} ({2} with a threat, {3} no world) | "
                + "{4} move(s), {5} stand(s), {6} intent-safe, {7} held | {8} NoAdmissible, "
                + "{9} would-escape | {10}",
                _act ? "LIVE" : "DRY RUN",
                _frames, _framesEvaluated, _framesNoWorld,
                _framesMoved, _framesStandSafe, _framesIntentSafe, _framesHeld,
                _framesNoAdmissible, _framesWouldEscape,
                DecisionCost.Describe()));

            // THE LINE THE OPERATOR ASKS FOR FIRST, because "it did not dodge that" and "that was
            // not dodgeable" are the same picture and different findings. `dodgeable` is the count
            // of threats the decision could actually have answered; `nothing-to-do` is the count of
            // frames where something mattered and NOTHING could be done about it -- the player
            // already being touched, or the bullet faster than the player can move.
            HookEntry.AppendLog("INFO", string.Format(CultureInfo.InvariantCulture,
                "PHASE 3 DODGE THREATS | dodgeable {0}, too fast {1}, touching {2}, clear {3} of {4} | "
                + "{5} frame(s) mattered: {6} answerable, {7} not | precedence {8} ({9} yielded)",
                VerdictCounts[(int)ThreatVerdict.Dodgeable],
                VerdictCounts[(int)ThreatVerdict.TooFast],
                VerdictCounts[(int)ThreatVerdict.Inside],
                VerdictCounts[(int)ThreatVerdict.Clear],
                _verdictTotal,
                _framesWithRelevantThreats, _framesWithADodgeableThreat, _framesNothingToDoAbout,
                PrecedenceName(), _framesYieldedToEscape));

            double meanAdmissible = _candidateFrames > 0
                ? (double)_candidatesAdmissible / _candidateFrames
                : 0.0;
            double meanEvaluated = _candidateFrames > 0
                ? (double)_candidatesEvaluated / _candidateFrames
                : 0.0;

            // `goal N of M` IS THE DIRECTED-DODGE CHANGE REPORTING ON ITSELF, and it is placed here
            // rather than on a new line because the line has room. Before the change the forward
            // goal was the measured course -- the direction the body was already travelling, which
            // while the dodge steers is its own last command -- and the defect was invisible in
            // every aggregate. Now `goal` counts the frames the goal was the PLAYER's held
            // direction and `course` the frames it was the body's; a live run where `goal` is zero
            // on a session where the operator was walking is a run where `GameInput.Moving` never
            // reached the solver, and no other line in this report can say so.
            HookEntry.AppendLog("INFO", string.Format(CultureInfo.InvariantCulture,
                "PHASE 3 DODGE DETAIL | candidates {0:0.0} of {1:0.0} over {2} frame(s) | threats {3} "
                + "considered, {4} dropped by the 48 cap | speed {5:0.00} u/s ({6} observed, {7} blocked) "
                + "| course {8} of {9} | goal {10} of {9} | no-hit {11}",
                meanAdmissible, meanEvaluated, _candidateFrames,
                _threatsConsidered, _threatsCulledByCap,
                _speedPerSecond, _speedObservedFrames, _speedBlockedFrames,
                _framesWithCourse, _frames, _framesWithPlayerGoal,
                _projectilesNotAHittableThreat));

            // THE REASONS GET THEIR OWN LINE. They are the longest variable part of the report -- a
            // busy run names seven of them -- and sharing a line with anything else pushed that line
            // past the 256-byte buffer, which truncates silently.
            HookEntry.AppendLog("INFO", "PHASE 3 DODGE REASONS | " + ReasonHistogram());
            HookEntry.FlushLog();
        }

        private static string PrecedenceName()
        {
            switch (_precedence)
            {
                case 1: return "normal (instant escape)";
                case 2: return "IMPERMANENCE CHANNELLED -- dodge disabled";
                default: return "UNRESOLVED (totem check failed; assuming the normal path)";
            }
        }

        private static string ReasonHistogram()
        {
            // Only the reasons that occurred, in enum order, so the line stays short on the common
            // run -- where 90 % of frames are one reason -- and still names every one that did.
            System.Text.StringBuilder text = new System.Text.StringBuilder(96);
            for (int i = 1; i < ReasonCounts.Length; i++)
            {
                if (ReasonCounts[i] == 0)
                {
                    continue;
                }
                if (text.Length > 0)
                {
                    text.Append(' ');
                }
                text.Append((DodgeReason)i).Append('=').Append(ReasonCounts[i]);
            }
            return text.Length == 0 ? "(none)" : text.ToString();
        }

        /// <summary>Folds an angle into [-pi, pi], which is what the intent comparison needs.</summary>
        private static float Normalize(float radians)
        {
            while (radians > Math.PI)
            {
                radians -= (float)(2.0 * Math.PI);
            }
            while (radians < -Math.PI)
            {
                radians += (float)(2.0 * Math.PI);
            }
            return radians;
        }
    }
}
