// SteeringPilot.cs -- Phase 3 Spike 3.1: can the hook move the player?
//
// =====================================================================================
// WHAT THIS IS, AND WHAT IT DELIBERATELY IS NOT
// =====================================================================================
//
// This is the movement question's experiment, and it contains **no dodge logic at all**.
// It answers one thing: can the hook make the client move the local player, through the
// client's own input path, in a way the client and the server both accept?
//
// The plan (IMPLEMENTATION-PLAN.md, Spike 3.1) names three candidate mechanisms and
// ranks them. This implements the first one:
//
//     GameScene.GameInput.MoveAngle / .Moving / .MoveSpeed
//
// and it implements it for a reason that is not convenience. `World.cs:12690-12692`
// reads exactly those three fields one line before it computes the frame's position
// delta (`:12710`), applies the client's own speed law (`PlayerFunctions.TilesPerSecond`,
// `:12697`), its own collision integrator (`:12714`) and its own timestamped move
// records (`:12769-12783`). Writing them means **the client itself** produces the
// movement, the `GmMove` packet and the history the server validates against -- so the
// outbound stream is legitimate by construction rather than by imitation. That is the
// property the whole phase rests on, and `RISKS.md` section 2a is the argument for it.
//
// It is NOT:
//
//   * a position write (`Player.Position = ...`), which `World.cs:12779-12780` shows the
//     integrator overwriting next frame and which desynchronises the history the
//     outbound `GmMove` is built from -- the same failure the reference engine warns
//     about as server snap-back;
//   * a `GmMove` injection, which would be the relay's business and would have to
//     invent the history array;
//   * the solver. Nothing here decides anything about bullets.
//
// =====================================================================================
// WHERE IT RUNS, AND WHY THAT PLACE
// =====================================================================================
//
// `World.hehe_EB2C_E5A5_EE82_E9C5_EF68(float logicDt, float frameDt)` is the movement
// integrator -- the input->position step (`World.cs:12666`). A **managed replacement**
// is installed on its entry, exactly as `FrameTickReplacement` and
// `CollisionObservation` are on theirs, so the replacement runs on the game's own
// thread with the game's own `call` beneath it (MANAGED-ENTRY-HANDOFF.md section 3).
//
// Why that method rather than the frame tick, which is already patched:
//
//   1. the write and the observation are the *same call*. The replacement writes the
//      input, calls through, and then reads `Player.Position` -- so the displacement it
//      measures is the displacement the client computed from the input it just wrote,
//      with no other writer in between and no frame boundary to blur it;
//   2. nothing else can rewrite the input between the write and the read. The frame
//      tick runs before `World.Update`, which is where this method is called from; a
//      write there would be a frame early and would be overwritten by the real input
//      poll in `PreUpdate`;
//   3. it is the entry point the Phase 3 solver needs anyway, so the plumbing built for
//      the spike is the plumbing the dodge uses.
//
// =====================================================================================
// WHAT IT MEASURES, AND WHY AN IN-PROCESS TEST IS NOT ENOUGH
// =====================================================================================
//
// "The player moved" is not the measurement. Two questions matter and they are
// different:
//
//   * **Did the client act on our input?** Measured as the angle between the commanded
//     direction and the realised per-frame displacement, in the *world* frame. A
//     healthy actuator gives a cosine near 1; a client that ignored us gives noise
//     around 0. The mean cosine per phase is the number, and it is computed offline
//     from the logged pairs as well as live.
//   * **Did the server accept it?** That is not answerable in-process: the client's
//     position is authoritative to itself. It is answered by the relay -- a `GmKicked`
//     (id 185) with a movement reason, or a snap-back in the position trace -- which is
//     why the spike's success criterion names the kick and why
//     `tools/steering_report.py` reads the session log for it.
//
// The commanded angle is recorded **along with the world rotation it was mixed with**,
// because `World.cs:12692` computes
//
//     angle = MoveAngle + (-WorldRotation + aimOffset)
//
// and a reader who has only the commanded number cannot tell a pilot that aimed wrong
// from a camera that was rotated. The aim offset is `0` normally and a resolved constant
// when the player has the effect at `Player.HasEffect(160)`; in `sweep` mode it is not
// assumed at all -- the phases walk eight evenly spaced input angles and the *observed*
// heading is what is logged, so the relation can be read off the table whether or not
// the offset is known.

using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Drelay.Bootstrap
{
    /// <summary>
    /// A per-frame actuator for the local player's movement, plus the measurement of
    /// whether the client obeyed it.
    ///
    /// The mode is chosen by `DRELAY_MOVE_STEER`, which the launcher sets from
    /// `--move-steer`; with it unset (the default, and every run that is not Spike 3.1)
    /// <see cref="Resolve"/> still runs and still validates the field lookups, and
    /// <see cref="BeforeOriginal"/> does nothing but return.
    /// </summary>
    public static class SteeringPilot
    {
        // ---------------------------------------------------------------------------
        // Resolved game state. All of it is looked up once, at the resolve callback.
        // ---------------------------------------------------------------------------

        private static bool _resolved;
        private static bool _enabled;
        private static string _unresolvedReason = "not attempted";
        private static string _statusLine = "not attempted";

        private static FieldInfo _worldSharedField;
        private static FieldInfo _worldGameSceneField;
        private static FieldInfo _gameSceneInputField;
        private static FieldInfo _inputMovingField;
        private static FieldInfo _inputMoveSpeedField;
        private static FieldInfo _inputMoveAngleField;
        private static FieldInfo _angleRadiansField;
        private static FieldInfo _worldPlayerField;
        private static FieldInfo _worldPlayerDeadField;
        private static FieldInfo _worldSafeWorldField;
        private static FieldInfo _worldRotationField;
        private static FieldInfo _playerPositionField;
        private static FieldInfo _positionXField;
        private static FieldInfo _positionYField;
        private static FieldInfo _playerHealthField;
        private static FieldInfo _playerSpiritModeField;

        private static Func<object> _getWorld;
        private static Func<object, object> _getGameScene;
        private static Func<object, object> _getInput;
        private static Action<object, bool> _setMoving;
        private static Action<object, float> _setMoveSpeed;
        private static Action<object, float> _setMoveAngleRadians;
        private static Func<object, float> _getMoveAngleRadians;
        private static Func<object, object> _getPlayer;
        private static Func<object, bool> _getPlayerDead;
        private static Func<object, bool> _getSafeWorld;
        private static Func<object, float> _getPositionX;
        private static Func<object, float> _getPositionY;
        private static Func<object, double> _getHealth;
        private static Func<object, bool> _getSpiritMode;

        /// <summary>
        /// `World.WorldRotation` is `public static` (`World.cs:4212`), so its accessor
        /// takes no object. A separate field rather than one more entry in the object-shaped
        /// list above, because a `Func<object, float>` here would let a caller pass any
        /// object at all and still compile.
        /// </summary>
        private static Func<float> _getWorldRotationStatic;

        /// <summary>
        /// One boxed `Angle`, written in place once per frame.
        ///
        /// WHY A BOX, WHICH IS NORMALLY THE THING TO AVOID. `MoveAngle` is an inline
        /// struct field on the input object, so setting it means either calling the
        /// game's `Angle(GamePoint)` constructor through reflection -- which allocates a
        /// `GamePoint`, an `Angle` and an `object[]` per write -- or writing the struct's
        /// only field in place. The box is created once (its type comes from the field,
        /// so it cannot be the wrong struct) and `FieldInfo.SetValue` copies its payload
        /// into the live field, so the steady-state write allocates nothing.
        /// </summary>
        private static object _boxedAngle;
        private static Type _angleType;

        /// <summary>The world-frame radians the pilot last asked for.</summary>
        private static float _commandedRadians = float.NaN;

        // ---------------------------------------------------------------------------
        // Mode and state.
        // ---------------------------------------------------------------------------

        private static string _mode = "off";
        private static float[] _phaseTargets;          // degrees, in the frame the mode says
        private static string[] _phaseNames;
        private static int _phaseFrames = 240;         // frames per phase; 0 means "no limit"
        private static bool _sweepMode;                // target is written straight into the input

        private static long _frame;
        private static int _phase;
        private static bool _finished;
        private static long _phaseStartFrame;
        private static int _startupGrace;
        private static float _lastX = float.NaN;
        private static float _lastY = float.NaN;
        private static int _skippedNoPlayer;
        private static int _skippedNotAlive;

        // Per-phase accumulators for the live summary.
        private static long _phaseMoved;
        private static double _phaseSumCosine;
        private static double _phaseSumAbs;
        private static double _phaseSumSin;
        private static double _phaseSumCos;

        private static long _loggedFrames;
        private static int _failureLogged;
        private static long _totalMoved;

        /// <summary>Whether the field lookups resolved, for the startup log and the summary.</summary>
        public static bool Resolved => _resolved;

        /// <summary>Why not, when they did not.</summary>
        public static string UnresolvedReason => _unresolvedReason;

        /// <summary>Whether a steering experiment is actually being run.</summary>
        public static bool Enabled => _enabled;

        /// <summary>One line per frame is a lot of log; every 60 is about a second at 60 Hz.</summary>
        private const int LogEveryFrames = 60;

        /// <summary>
        /// Frames of live world before a phase may start, so the first phase is not
        /// measured against a player that has not finished entering the zone.
        /// </summary>
        private const int StartupGraceFrames = 30;

        /// <summary>
        /// Displacement below this is not movement and is excluded from the heading
        /// statistics. It is deliberately tiny: "did he move at all" is answered by the
        /// moved-count, and a floor large enough to matter would quietly delete the slow
        /// frames a `Slowed` tier produces.
        /// </summary>
        private const float MovementFloor = 0.0005f;

        // ===================================================================================
        // Resolution
        // ===================================================================================

        /// <summary>
        /// Find every field the pilot touches, and parse the mode.
        ///
        /// Returns false with a reason rather than throwing: the pilot is Phase 3's
        /// experiment, and the frame read, the patch and Phase 2's evidence must all
        /// survive its failure.
        /// </summary>
        public static bool Resolve(Assembly game)
        {
            try
            {
                _mode = (Environment.GetEnvironmentVariable("DRELAY_MOVE_STEER") ?? "").Trim();
                _enabled = _mode.Length > 0 && !string.Equals(_mode, "off", StringComparison.OrdinalIgnoreCase);

                Type worldType = game.GetType(ManagedArmingProbe.WorldTypeName);
                if (worldType == null)
                {
                    _unresolvedReason = "type " + ManagedArmingProbe.WorldTypeName + " not found";
                    _statusLine = "the steering pilot is NOT available: " + _unresolvedReason;
                    return false;
                }

                const BindingFlags Any =
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

                _worldSharedField = worldType.GetField("Shared", Any);
                _worldGameSceneField = worldType.GetField("GameScene", Any);
                _worldPlayerField = worldType.GetField("Player", Any);
                _worldPlayerDeadField = worldType.GetField("PlayerDead", Any);
                _worldSafeWorldField = worldType.GetField("SafeWorld", Any);
                // NOT IN THE REQUIRED LIST. `WorldRotation` is resolved here and checked by
                // `ResolveRotation`, which `StartupHook` calls first; a build that renamed
                // it costs the `hold` mode's aim correction and nothing else, and a required
                // field that fails the whole pilot would turn "one optional accessor moved"
                // into "the experiment cannot run".
                _worldRotationField = worldType.GetField("WorldRotation", Any);

                // THE `GameScene` *FIELD*, NOT `World.Scene` CAST TO A `GameScene`. The
                // client reaches its input through `GameScene.GameInput` (`World.cs:12690`)
                // and `World` holds a `public readonly GameScene GameScene`
                // (`World.cs:4306`) -- a typed field, so one type is found rather than an
                // inheritance walk from the base `Scene` plus a cast.
                Type gameSceneType = _worldGameSceneField?.FieldType;
                _gameSceneInputField = gameSceneType?.GetField("GameInput", Any);
                Type inputType = _gameSceneInputField?.FieldType;

                _inputMovingField = inputType?.GetField("Moving", Any);
                _inputMoveSpeedField = inputType?.GetField("MoveSpeed", Any);
                _inputMoveAngleField = inputType?.GetField("MoveAngle", Any);
                _angleType = _inputMoveAngleField?.FieldType;
                _angleRadiansField = _angleType?.GetField("Radians", Any);

                Type playerType = game.GetType(ManagedArmingProbe.PlayerTypeName);
                _playerPositionField = playerType?.GetField("Position", Any);
                _playerHealthField = playerType?.GetField("Health", Any);
                _playerSpiritModeField = playerType?.GetField("SpiritMode", Any);
                Type positionType = _playerPositionField?.FieldType;
                _positionXField = positionType?.GetField("X");
                _positionYField = positionType?.GetField("Y");

                string missing = Missing();
                if (missing != null)
                {
                    _unresolvedReason = "field(s) not found: " + missing;
                    _statusLine = "the steering pilot is NOT available: " + _unresolvedReason
                                  + " -- the frame read and the patch are unaffected";
                    return false;
                }

                // THE DELEGATES, NOT `FieldInfo.GetValue`. A patrol through the reflection
                // API on the frame path boxes every value it reads, and `FieldAccessor`
                // exists because that cost was measured (PHASE2-SCOPE.md section 4.11).
                // The reads borrow it outright; the writes need setters it does not have
                // yet, emitted here for the same reason.
                _getWorld = FieldAccessor.Static(_worldSharedField);
                _getGameScene = FieldAccessor.Reference(_worldGameSceneField);
                _getInput = FieldAccessor.Reference(_gameSceneInputField);
                _getPlayer = FieldAccessor.Reference(_worldPlayerField);
                _getPlayerDead = FieldAccessor.Bool(_worldPlayerDeadField);
                _getSafeWorld = FieldAccessor.Bool(_worldSafeWorldField);
                _getPositionX = FieldAccessor.NestedFloat(_playerPositionField, _positionXField);
                _getPositionY = FieldAccessor.NestedFloat(_playerPositionField, _positionYField);
                _getHealth = FieldAccessor.Double(_playerHealthField);
                _getSpiritMode = FieldAccessor.Bool(_playerSpiritModeField);
                _getWorldRotationStatic = FieldAccessor.StaticFloat(_worldRotationField);

                _setMoving = FieldAccessor.BoolSetter(_inputMovingField);
                _setMoveSpeed = FieldAccessor.FloatSetter(_inputMoveSpeedField);
                _setMoveAngleRadians = FieldAccessor.FloatSetter(_angleRadiansField);
                _getMoveAngleRadians = FieldAccessor.NestedFloat(_inputMoveAngleField, _angleRadiansField);

                // The box has to be the game's own struct type -- `Angle` has exactly one
                // field (`Radians`, `Angle.cs:18`) but the runtime checks the type on the
                // way into `SetValue`, so a stand-in would fail at the write rather than
                // at the resolve, which is the wrong place to find out.
                _boxedAngle = Activator.CreateInstance(_angleType);

                ParsePlan();
                _resolved = true;
                _statusLine = BuildStatus();
                return true;
            }
            catch (Exception ex)
            {
                _unresolvedReason = ex.GetType().Name + ": " + ex.Message;
                _statusLine = "the steering pilot is NOT available: " + _unresolvedReason
                              + " -- the frame read and the patch are unaffected";
                return false;
            }
        }

        private static string Missing()
        {
            string[] names =
            {
                "World.Shared", "World.GameScene", "World.Player", "World.PlayerDead", "World.SafeWorld",
                "GameScene.GameInput", "GameInput.Moving", "GameInput.MoveSpeed",
                "GameInput.MoveAngle", "Angle.Radians", "Player.Position", "Player.Health",
                "Player.SpiritMode", "position.X", "position.Y",
            };
            FieldInfo[] fields =
            {
                _worldSharedField, _worldGameSceneField, _worldPlayerField, _worldPlayerDeadField,
                _worldSafeWorldField, _gameSceneInputField, _inputMovingField,
                _inputMoveSpeedField, _inputMoveAngleField, _angleRadiansField, _playerPositionField,
                _playerHealthField, _playerSpiritModeField, _positionXField, _positionYField,
            };
            var missing = new System.Collections.Generic.List<string>();
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i] == null)
                {
                    missing.Add(names[i]);
                }
            }
            return missing.Count == 0 ? null : string.Join(", ", missing);
        }

        private static string BuildStatus()
        {
            if (!_enabled)
            {
                return "the steering pilot resolved all 15 field(s) and is IDLE "
                       + "(DRELAY_MOVE_STEER unset); the movement replacement, if patched, only calls through";
            }
            if (_phaseTargets.Length == 0)
            {
                return "the steering pilot is ARMED but its mode was rejected: " + _unresolvedReason;
            }
            return "the steering pilot is ARMED: mode '" + _mode + "', " + _phaseTargets.Length
                   + " phase(s) of " + _phaseFrames + " frame(s)";
        }

        /// <summary>A sentence for the startup log: what was resolved, and what will happen.</summary>
        public static string Status()
        {
            return _statusLine;
        }

        /// <summary>
        /// Resolve `World.WorldRotation` separately, and FIRST, because it is the one field
        /// the pilot can live without.
        ///
        /// `hold` mode aims at a world heading and is wrong without it; `sweep` mode does
        /// not use it at all, because what it measures is the mapping the rotation takes
        /// part in. So a missing rotation degrades the hold and must not disable the sweep
        /// -- which is why it is not in <see cref="Resolve"/>'s mandatory list and why a
        /// failure here is silent rather than fatal. It is a `public static float`
        /// (`World.cs:4212`), so a rename in a future build is a plausible way to lose it.
        /// </summary>
        internal static void ResolveRotation(Assembly game)
        {
            try
            {
                Type worldType = game.GetType(ManagedArmingProbe.WorldTypeName);
                _worldRotationField = worldType?.GetField("WorldRotation",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch
            {
                _worldRotationField = null;
            }
        }

        /// <summary>
        /// Prints the plan to the log, once, at resolve time. A run whose mode was
        /// rejected says so here rather than looking like a pilot that chose to do
        /// nothing.
        /// </summary>
        public static void LogPlan()
        {
            HookEntry.AppendLog("INFO", Status());
            if (_enabled && _phaseTargets.Length > 0)
            {
                HookEntry.AppendLog("INFO", string.Format(CultureInfo.InvariantCulture,
                    "steering plan: {0} phase(s), {1} frame(s) each, target frame {2}",
                    _phaseTargets.Length, _phaseFrames,
                    _sweepMode ? "the client's own input angle" : "world heading + WorldRotation"));
            }
            HookEntry.FlushLog();
        }

        // ===================================================================================
        // The plan: what `DRELAY_MOVE_STEER` means
        // ===================================================================================

        /// <summary>
        /// Parse the mode string. Grammar, in full:
        ///
        ///     off                     do nothing (the default)
        ///     sweep[:frames]          walk 8 input angles 45 degrees apart
        ///     sweepfast[:frames]      the same, 120 frames each unless overridden
        ///     hold:degrees[:frames]   hold one world heading
        ///     hold:degrees            hold until the run ends
        ///
        /// A malformed mode is not fatal: it is logged and the pilot stays idle, because a
        /// typo in an experiment's name must not be a run that silently does something
        /// else. That is the rule the AutoNexus settings follow (docs/AUTONEXUS.md
        /// section 7), and the alternative -- a default direction -- would be a run that
        /// moves the player when the operator asked for something the parser did not
        /// understand.
        /// </summary>
        private static void ParsePlan()
        {
            string[] parts = _mode.Split(':');
            string verb = parts[0].ToLowerInvariant();

            switch (verb)
            {
                case "sweep":
                case "sweepfast":
                {
                    // EIGHT EVENLY SPACED INPUT ANGLES, NOT EIGHT WORLD HEADINGS. The
                    // point of the sweep is to measure the mapping, so the independent
                    // variable has to be the thing written -- `GameInput.MoveAngle` -- and
                    // the dependent variable is where the player actually went. A sweep in
                    // world headings would presuppose the very transform it is there to
                    // measure, and would hide a wrong `WorldRotation` sign behind a table
                    // that looked plausible.
                    _sweepMode = true;
                    _phaseFrames = verb == "sweepfast" ? 120 : 240;
                    if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer,
                                                         CultureInfo.InvariantCulture, out int frames)
                        && frames > 0)
                    {
                        _phaseFrames = frames;
                    }
                    _phaseTargets = new float[8];
                    _phaseNames = new string[8];
                    for (int i = 0; i < 8; i++)
                    {
                        _phaseTargets[i] = i * 45f;
                        _phaseNames[i] = "input " + (i * 45) + "deg";
                    }
                    break;
                }

                case "hold":
                {
                    if (parts.Length < 2 || !float.TryParse(parts[1], NumberStyles.Float,
                                                            CultureInfo.InvariantCulture, out float degrees))
                    {
                        _unresolvedReason = "hold needs a heading in degrees, e.g. hold:90";
                        _enabled = false;
                        _phaseTargets = new float[0];
                        _phaseNames = new string[0];
                        return;
                    }
                    _sweepMode = false;
                    _phaseFrames = 0;
                    if (parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer,
                                                         CultureInfo.InvariantCulture, out int frames)
                        && frames > 0)
                    {
                        _phaseFrames = frames;
                    }
                    _phaseTargets = new[] { degrees };
                    _phaseNames = new[] { "hold " + degrees.ToString("0.#", CultureInfo.InvariantCulture) + "deg" };
                    break;
                }

                default:
                    _unresolvedReason = "unrecognised DRELAY_MOVE_STEER mode '" + _mode + "'";
                    _enabled = false;
                    _phaseTargets = new float[0];
                    _phaseNames = new string[0];
                    break;
            }
        }

        // ===================================================================================
        // The per-frame entry, called from the movement replacement
        // ===================================================================================

        /// <summary>
        /// Called at the TOP of the movement integrator's replacement, before the original
        /// runs, so the write is the input the original reads.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void BeforeOriginal()
        {
            if (!_resolved || !_enabled || _finished)
            {
                return;
            }

            _frame++;

            try
            {
                object world = _getWorld();
                if (world == null)
                {
                    _skippedNoPlayer++;
                    return;
                }

                // THE GAME'S OWN GATES, READ RATHER THAN GUESSED. `World.cs:12673` stops
                // all input when the player is dead or transferring to spirit, and
                // `:12683` bypasses the whole movement branch while knocked up. Writing
                // the input through any of those is at best ignored and at worst a write
                // into a frame the client is about to override; either way it is not
                // evidence, so the pilot waits rather than producing a table of zeroes.
                if (_getPlayerDead(world) || _getSafeWorld(world))
                {
                    _skippedNotAlive++;
                    return;
                }

                object player = _getPlayer(world);
                if (player == null)
                {
                    _skippedNoPlayer++;
                    return;
                }
                if (_getSpiritMode(player) || _getHealth(player) <= 0.0)
                {
                    _skippedNotAlive++;
                    return;
                }

                if (_startupGrace < StartupGraceFrames)
                {
                    _startupGrace++;
                    return;
                }

                object input = _getInput(_getGameScene(world));
                if (input == null)
                {
                    _skippedNoPlayer++;
                    return;
                }

                float x = _getPositionX(player);
                float y = _getPositionY(player);
                float rotation = _getWorldRotationStatic();

                // THE PREVIOUS FRAME'S DISPLACEMENT IS MEASURED AGAINST THE PREVIOUS
                // FRAME'S COMMAND, and that ordering is why the measurement is taken here
                // rather than after the original runs: the call through the trampoline is
                // about to rewrite `Player.Position`, and the delta between that new
                // position and the one read last frame is the client's own response to
                // the input it read last frame -- the number this spike exists to produce.
                float dx = float.NaN;
                float dy = float.NaN;
                if (!float.IsNaN(_lastX) && !float.IsNaN(_lastY))
                {
                    dx = x - _lastX;
                    dy = y - _lastY;
                    Accumulate(dx, dy);
                }
                _lastX = x;
                _lastY = y;

                AdvancePhase();

                float commanded = PhaseAngle(rotation);
                WriteInput(input, commanded);

                LogFrame(input, rotation, x, y, dx, dy, commanded);
            }
            catch (Exception ex)
            {
                // SWALLOWED, COUNTED, AND NAMED ONCE, for the reason the frame
                // replacement's own catch states: an exception escaping into the game's
                // movement tick would be far worse than a missed experiment. The one
                // thing that must not happen is a silent swallow, because that reads as
                // "the pilot ran and chose to do nothing".
                if (System.Threading.Interlocked.Exchange(ref _failureLogged, 1) == 0)
                {
                    HookEntry.AppendLog("ERROR", "the steering pilot threw; the game is unaffected "
                                                 + "and the movement tick continues untouched: "
                                                 + ex.GetType().Name + ": " + ex.Message);
                    HookEntry.FlushLog();
                }
            }
        }

        /// <summary>
        /// Write the steering input. This is the whole actuator: three field writes into
        /// the object the client's own integrator reads one line later.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteInput(object input, float angle)
        {
            _commandedRadians = angle;
            _setMoveAngleRadians(_boxedAngle, angle);
            _inputMoveAngleField.SetValue(input, _boxedAngle);
            _setMoving(input, true);
            _setMoveSpeed(input, 1f);
        }

        /// <summary>
        /// The angle to write this frame, in the client's own input frame.
        ///
        /// `World.cs:12692` computes `MoveAngle.AddRadians(-WorldRotation + aimOffset)`, so
        /// an input of `worldHeading + WorldRotation` produces a world direction of
        /// `worldHeading` when the aim offset is zero. `sweep` mode skips the correction on
        /// purpose -- it is measuring the mapping, and the observed heading is logged
        /// beside the commanded one so the relation can be read off the table.
        /// </summary>
        private static float PhaseAngle(float worldRotation)
        {
            if (_phase >= _phaseTargets.Length)
            {
                return _commandedRadians;
            }
            float target = _phaseTargets[_phase] * ((float)Math.PI / 180f);
            return _sweepMode ? target : target + worldRotation;
        }

        /// <summary>Move to the next phase when the current one has run its frames.</summary>
        private static void AdvancePhase()
        {
            if (_phase >= _phaseTargets.Length)
            {
                return;
            }
            if (_phaseStartFrame == 0)
            {
                _phaseStartFrame = _frame;
                HookEntry.AppendLog("INFO", "steering phase " + (_phase + 1) + "/" + _phaseTargets.Length
                                            + " begins: " + _phaseNames[_phase]);
                HookEntry.FlushLog();
            }
            if (_phaseFrames > 0 && (_frame - _phaseStartFrame) >= _phaseFrames)
            {
                EmitPhaseSummary(_phase);
                _phase++;
                _phaseStartFrame = _frame;
                ResetPhaseAccumulators();
                if (_phase >= _phaseTargets.Length)
                {
                    _finished = true;
                    ReleaseInput();
                    EmitRunSummary();
                    HookEntry.AppendLog("INFO", "steering plan complete after " + _frame
                                                + " frame(s); the input was released and the path "
                                                + "continues to be called, now only calling through.");
                    HookEntry.FlushLog();
                }
                else
                {
                    HookEntry.AppendLog("INFO", "steering phase " + (_phase + 1) + "/"
                                                + _phaseTargets.Length + " begins: " + _phaseNames[_phase]);
                    HookEntry.FlushLog();
                }
            }
        }

        /// <summary>
        /// Stop steering, and put the input back the way a player who is not pressing
        /// anything would have it.
        ///
        /// THE RELEASE EXISTS BECAUSE THE ALTERNATIVE IS A `Moving = true` THAT NEVER
        /// STOPS. The pilot is the last writer before the integrator runs, so a dropped
        /// input would leave the client integrating a heading forever -- the player would
        /// walk until something else cleared the flag. Releasing is not politeness; it is
        /// the difference between a bounded experiment and a run that has to be killed.
        /// </summary>
        private static void ReleaseInput()
        {
            try
            {
                object world = _getWorld();
                if (world == null)
                {
                    return;
                }
                object input = _getInput(_getGameScene(world));
                if (input == null)
                {
                    return;
                }
                _setMoving(input, false);
                _setMoveSpeed(input, 0f);
            }
            catch
            {
                // Nothing to do about it here, and nothing should be printed per frame.
            }
        }

        // ===================================================================================
        // Measurement
        // ===================================================================================

        private static void Accumulate(float dx, float dy)
        {
            double length = Math.Sqrt((double)dx * dx + (double)dy * dy);
            if (length < MovementFloor)
            {
                return;
            }
            _phaseMoved++;
            _totalMoved++;
            _phaseSumAbs += length;

            // The observed heading and the alignment with what was commanded. In sweep
            // mode the commanded angle is an *input* angle rather than a world one, so the
            // cosine is reported per phase and the mapping is read off the observed
            // headings instead; in hold mode the two frames are the same and this is the
            // number that answers "did the client obey".
            double observed = Math.Atan2(dy, dx);
            _phaseSumSin += Math.Sin(observed);
            _phaseSumCos += Math.Cos(observed);
            _phaseSumCosine += Math.Cos(observed - _commandedRadians);
        }

        private static void ResetPhaseAccumulators()
        {
            _phaseMoved = 0;
            _phaseSumCosine = 0;
            _phaseSumAbs = 0;
            _phaseSumSin = 0;
            _phaseSumCos = 0;
        }

        private static void EmitPhaseSummary(int phase)
        {
            if (phase < 0 || phase >= _phaseTargets.Length)
            {
                return;
            }
            double observed = _phaseMoved > 0 ? Math.Atan2(_phaseSumSin, _phaseSumCos) : double.NaN;
            double meanCosine = _phaseMoved > 0 ? _phaseSumCosine / _phaseMoved : double.NaN;
            double meanAbs = _phaseMoved > 0 ? _phaseSumAbs / _phaseMoved : double.NaN;
            HookEntry.AppendLog("INFO", string.Format(CultureInfo.InvariantCulture,
                "steering phase {0} ({1}): target {2:0.#}deg, moved on {3} frame(s), "
                + "observed heading {4:0.0}deg, mean |delta| {5:0.0000} world units, "
                + "mean cos(observed - last commanded) {6:0.000}",
                phase + 1, _phaseNames[phase], _phaseTargets[phase], _phaseMoved,
                observed * 180.0 / Math.PI, meanAbs, meanCosine));
            HookEntry.FlushLog();
        }

        private static void EmitRunSummary()
        {
            HookEntry.AppendLog("INFO", string.Format(CultureInfo.InvariantCulture,
                "STEERING SUMMARY | frames {0}, frames the player moved {1}, "
                + "skipped: no-player {2}, dead-or-safe {3} | mode '{4}'",
                _frame, _totalMoved, _skippedNoPlayer, _skippedNotAlive, _mode));
            HookEntry.FlushLog();
        }

        private static void LogFrame(object input, float rotation, float x, float y,
                                     float dx, float dy, float commanded)
        {
            if ((_loggedFrames++ % LogEveryFrames) != 0)
            {
                return;
            }
            float readBack = _getMoveAngleRadians(input);
            HookEntry.AppendLog("INFO", string.Format(CultureInfo.InvariantCulture,
                "steer frame {0} phase {1} cmd {2:0.0}deg wrote {3:0.0000}rad read-back {4:0.0000}rad "
                + "rot {5:0.0}deg pos ({6:0.000},{7:0.000}) d ({8:0.0000},{9:0.0000}) |d| {10:0.0000}",
                _frame, _phase + 1, commanded * 180.0 / Math.PI, commanded, readBack,
                rotation * 180.0 / Math.PI, x, y, dx, dy,
                Math.Sqrt((double)dx * dx + (double)dy * dy)));
            HookEntry.FlushLog();
        }

        // ===================================================================================
        // The managed replacement on the movement integrator
        // ===================================================================================
        //
        // THIS METHOD IS WHAT MINHOOK'S `jmp` LANDS IN, and that is not an implementation
        // detail: it is the reason the actuator is safe.  `World.hehe_EB2C_E5A5_EE82_E9C5_
        // EF68(float, float)` is an ordinary instance method, so on x64 the receiver is in
        // RCX and the two floats are in XMM1 and XMM2; a static method taking those three
        // explicitly has the same ABI, and the game's own `call` becomes a
        // managed-to-managed call with no native frame anywhere in the chain
        // (MANAGED-ENTRY-HANDOFF.md section 3, and PHASE2-SCOPE.md section 4.4 for the
        // measurement that settled it).
        //
        // THE ORIGINAL IS CALLED ON EVERY PATH.  A replacement that steered and returned
        // would stop the player's movement, collision, escape handling and `GmMove`
        // emission in one line -- the most destructive thing this file could do -- so the
        // call-through happens even when the steering path threw.

        /// <summary>
        /// The replacement: steer, then run the game's own movement tick.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void MovementIntegratorReplacement(IntPtr self, float logicDt, float frameDt)
        {
            try
            {
                BeforeOriginal();
            }
            catch (Exception ex)
            {
                // Swallowed for the reason the frame replacement's catch gives: an
                // exception escaping into the game's movement tick is worse than a missed
                // experiment. `BeforeOriginal` already logs and counts its own failures, so
                // this only catches something in the call itself.
                if (System.Threading.Interlocked.Exchange(ref _failureLogged, 1) == 0)
                {
                    HookEntry.AppendLog("ERROR", "the movement replacement threw before calling "
                                                 + "through; the game's movement tick still runs: "
                                                 + ex.GetType().Name + ": " + ex.Message);
                    HookEntry.FlushLog();
                }
            }

            CallOriginal(self, logicDt, frameDt);
        }

        /// <summary>
        /// Continue into the real method through MinHook's trampoline, as a MANAGED
        /// function pointer -- `delegate* managed`, never `delegate* unmanaged`, because
        /// the unmanaged form is a managed-to-native transition and puts a preemptive
        /// thread state under the original body (PHASE2-SCOPE.md section 4.4).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CallOriginal(IntPtr self, float logicDt, float frameDt)
        {
            long trampoline = Marshal.ReadInt64(HookEntry.SharedBlockPointer,
                                                GeneratedLayout.Off_managedMovementTrampoline);
            if (trampoline == 0)
            {
                // Must not happen: the native side publishes the trampoline before it
                // enables the hook, because the first call arrives immediately. Returning
                // would freeze the player in place, so this is loud.
                if (System.Threading.Interlocked.Exchange(ref _failureLogged, 2) != 2)
                {
                    HookEntry.AppendLog("ERROR", "the movement replacement ran with no trampoline "
                                                 + "published; the game's movement tick cannot run "
                                                 + "and the player will not move");
                    HookEntry.FlushLog();
                }
                return;
            }

            unsafe
            {
                ((delegate* managed<IntPtr, float, float, void>)(IntPtr)trampoline)(self, logicDt, frameDt);
            }
        }

        /// <summary>
        /// Prepare and publish the replacement, and warm everything it calls -- the same
        /// discipline as the frame tick's and the collision hook's, for the same reason: a
        /// method compiled on its first call is compiled from inside a patched game
        /// method, which is a JIT in the middle of the frame.
        ///
        /// CALLED ON EVERY RUN, WHETHER OR NOT THE OPERATOR ASKED FOR STEERING.  Publishing
        /// the address costs one JIT of a method that will never be entered, and it means
        /// the plumbing -- the shared slot, the native dispatch by target name, the
        /// trampoline handshake -- is exercised by every run rather than only by the runs
        /// where a mistake would move the player.  What the switch decides is whether
        /// MinHook's `jmp` is aimed at it at all; the native side reads that.
        /// </summary>
        public static void Publish()
        {
            MethodInfo method = typeof(SteeringPilot).GetMethod(
                nameof(MovementIntegratorReplacement), BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                HookEntry.AppendLog("ERROR", "the movement replacement was not found by name; "
                                             + "nothing published and the target will not be patched");
                HookEntry.FlushLog();
                return;
            }

            MethodInfo callOriginal = typeof(SteeringPilot).GetMethod(
                "CallOriginal", BindingFlags.NonPublic | BindingFlags.Static);
            if (callOriginal != null)
            {
                RuntimeHelpers.PrepareMethod(callOriginal.MethodHandle);
            }
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            PreparePilotPath();

            IntPtr entry = method.MethodHandle.GetFunctionPointer();
            Marshal.WriteInt64(HookEntry.SharedBlockPointer,
                               GeneratedLayout.Off_managedMovementReplacement, entry.ToInt64());
            HookEntry.AppendLog("INFO", "managed movement replacement published at 0x"
                                        + entry.ToInt64().ToString("X")
                                        + "; the native side jumps straight into it only when "
                                        + "--move-steer asked for it");
            HookEntry.FlushLog();
        }

        /// <summary>
        /// JIT everything the pilot's frame path touches, before the patch goes live.
        ///
        /// `PrepareMethod` covers the method named and nothing it calls, so every callee on
        /// this path is named explicitly.  The path also touches the reflection *fields*
        /// rather than their accessor delegates -- `_inputMoveAngleField.SetValue` is a
        /// framework method, and the boxed-`Angle` copy it performs is not something
        /// `PrepareMethod` can reach.
        /// </summary>
        private static void PreparePilotPath()
        {
            string[] names =
            {
                "BeforeOriginal", "WriteInput", "PhaseAngle", "AdvancePhase", "ReleaseInput",
                "Accumulate", "EmitPhaseSummary", "EmitRunSummary", "LogFrame", "ParsePlan",
                "BuildStatus", "Missing", "Resolve",
            };
            for (int i = 0; i < names.Length; i++)
            {
                MethodInfo m = typeof(SteeringPilot).GetMethod(
                    names[i], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m != null)
                {
                    try
                    {
                        RuntimeHelpers.PrepareMethod(m.MethodHandle);
                    }
                    catch
                    {
                        // A callee that is generic or otherwise unprepared is skipped; the
                        // rest of the path is still warmed, and the failure is not silent
                        // because the publish log follows.
                    }
                }
            }
        }
    }
}
