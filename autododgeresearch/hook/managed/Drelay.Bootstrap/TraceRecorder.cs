// TraceRecorder.cs -- the live half of Spike 3.2: record what the solver would decide against.
//
// =====================================================================================
// WHY A RECORDER AT ALL, WHEN THE SENSOR ALREADY EXISTS
// =====================================================================================
//
// The Phase 2 sensor observes the world and keeps counts. It does not keep *traces*: the
// per-bullet sample set it builds every frame is handed to the linearity gate, folded into a
// fraction, and dropped. So the four numbers Phase 2 produces describe the sensor, and none of
// them can be replayed -- there is no file that says "at frame 412 the player was here and these
// two bullets were on these paths".
//
// That is exactly what Spike 3.2 needs, because its first and most important invariant is T7:
//
//     "PREDICTED AND ACTUAL COLLISION AGREE. ... log every predicted first-impact time and every
//      `HitCharacter` call that returned `true` ... and assert the model predicted the ones that
//      happened. This is the hook's ground truth ... It is the one to build first. It is the only
//      test that can tell us whether the trajectory model is right."
//
// "The ones that happened" already exist: `CollisionObservation` writes one `collision:` line per
// resolved hit, with the bullet's key and the warning the sensor had. What does not exist is the
// other half -- what the model said, frame by frame, about every bullet it was tracking. This
// file writes it.
//
// =====================================================================================
// WHAT IT WRITES, AND WHY EACH FIELD IS THERE
// =====================================================================================
//
//   H  a header: the run, the grid window, the configuration the solver would have used
//   G  the collision grid window, once per change, run-length encoded
//   F  one frame: the clock, the player, and every live bullet's sampled path
//
// ONE FILE, THREE RECORD KINDS, JSON LINES. The format is deliberately boring: an offline
// harness has to read it, a human has to be able to look at it when the harness disagrees, and
// a format that needs its own parser is a format that will be read only by the code that wrote
// it -- which is how a fixture stops being evidence.
//
// THE PLAYER IS RECORDED EVEN WITH NO BULLETS ANYWHERE. A trace of a peaceful walk is the
// control case for T7: zero predicted impacts against zero resolved hits is the only way to show
// that the recorder is not inventing danger.
//
// =====================================================================================
// THE COST, WHICH IS THE REASON THIS IS OFF BY DEFAULT
// =====================================================================================
//
// Recording every frame at 60 Hz costs one file append and one string build per frame, plus
// about a kilobyte per bullet per frame. That is far too much to leave on for a normal session
// and it is why the switch is `DRELAY_TRACE` and not a default. The mitigations are the ones
// section 8 names for the sampler: cull bullets that cannot reach the player inside the horizon
// before recording them, and cap the number recorded per frame. What is recorded is therefore
// "the bullets that could matter", which is also the set the solver would have evaluated.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Drelay.Bootstrap
{
    /// <summary>
    /// The one place in the hook that writes a replayable trace of the decision's own input.
    /// </summary>
    public static class TraceRecorder
    {
        private const string EnvTrace = "DRELAY_TRACE";
        private const string FileName = "trace.jsonl";

        /// <summary>Bullets recorded per frame, at most. The rest are counted, not written.</summary>
        private const int MaxThreatsPerFrame = 48;

        /// <summary>Half-width of the grid window, in tiles, centred on the player.</summary>
        private const int GridRadius = 24;

        private static bool _enabled;
        private static bool _resolved;
        private static string _unresolvedReason = "not attempted";
        private static string _path;
        private static StreamWriter _writer;

        private static readonly StringBuilder Line = new StringBuilder(4096);
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // The grid window, reused between frames. The layout is row-major over [x, y] so that a
        // reader can index it the way `World` indexes its own `byte[x, y]`.
        private static byte[] _gridCells = new byte[(2 * GridRadius) * (2 * GridRadius)];
        private static int _gridWidth;
        private static int _gridHeight;
        private static int _gridOriginX;
        private static int _gridOriginY;
        private static bool _gridResolved;
        private static bool _gridWindowValid;
        private static bool _gridDirty;
        private static long _gridGeneration;

        // Accessors, resolved once. All optional: a missing one degrades the trace, never the run.
        private static Func<object> _getWorld;
        private static Func<object, object> _getPlayer;
        private static Func<object, float> _getPlayerX;
        private static Func<object, float> _getPlayerY;
        private static Func<object, float> _getPlayerRadius;
        private static Func<object, double> _getPlayerHealth;
        private static Func<object, object> _getGameScene;
        private static Func<object, object> _getInput;
        private static Func<object, bool> _getMoving;
        private static Func<object, float> _getMoveAngleRadians;
        private static Func<object, bool> _getPlayerDead;
        private static Func<object, bool> _getSafeWorld;
        private static Func<float> _getWorldRotation;
        private static Func<object, float> _getLogicTotal;
        private static Func<object> _getClock;
        private static Func<object, object> _getGridArray;
        private static Func<object, int, int, byte> _gridAt;

        private static long _framesRecorded;
        private static long _threatsRecorded;
        private static long _threatsSkipped;
        private static int _failureLogged;
        private static int _framesUntilLog = 600;

        /// <summary>Whether the recorder is switched on and has a file.</summary>
        public static bool Enabled => _resolved && _enabled;

        /// <summary>Why not, when not.</summary>
        public static string UnresolvedReason => _unresolvedReason;

        // ===================================================================================
        // Resolution
        // ===================================================================================

        /// <summary>
        /// Reads the switch, opens the file, and resolves every accessor the trace needs.
        ///
        /// EVERY ACCESSOR IS OPTIONAL AND A MISSING ONE IS NOT FATAL. The trace's job is to make
        /// the offline harness possible; a trace with no health reading is still a trace that can
        /// test T7, and refusing to record because one field moved would turn "the model cannot be
        /// checked" into "the model cannot be checked and the reason was a rename".
        /// </summary>
        public static bool Resolve(Assembly game, string runDir)
        {
            string switchValue = Environment.GetEnvironmentVariable(EnvTrace);
            if (string.IsNullOrEmpty(switchValue) || switchValue == "0")
            {
                _unresolvedReason = "DRELAY_TRACE is not set";
                return false;
            }

            try
            {
                const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static;

                Type worldType = game.GetType(ManagedArmingProbe.WorldTypeName);
                Type playerType = game.GetType(ManagedArmingProbe.PlayerTypeName);
                if (worldType == null)
                {
                    _unresolvedReason = ManagedArmingProbe.WorldTypeName + " not found";
                    return false;
                }

                FieldInfo sharedField = worldType.GetField("Shared", Any);
                FieldInfo playerField = worldType.GetField("Player", Any);
                FieldInfo playerPositionField = playerType?.GetField("Position", Any);
                FieldInfo positionXField = playerPositionField?.FieldType.GetField("X");
                FieldInfo positionYField = playerPositionField?.FieldType.GetField("Y");
                FieldInfo radiusField = FindRadius(playerType);
                FieldInfo healthField = playerType?.GetField("Health", Any);
                FieldInfo deadField = worldType.GetField("PlayerDead", Any);
                FieldInfo safeField = worldType.GetField("SafeWorld", Any);
                FieldInfo sceneField = worldType.GetField("GameScene", Any);
                FieldInfo inputField = sceneField?.FieldType.GetField("GameInput", Any);
                FieldInfo movingField = inputField?.FieldType.GetField("Moving", Any);
                FieldInfo moveAngleField = inputField?.FieldType.GetField("MoveAngle", Any);
                FieldInfo angleRadiansField = moveAngleField?.FieldType.GetField("Radians", Any);
                FieldInfo rotationField = worldType.GetField("WorldRotation",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (sharedField == null || playerField == null || positionXField == null
                    || positionYField == null)
                {
                    _unresolvedReason = "the player path did not resolve: World.Shared="
                                        + (sharedField != null) + " World.Player=" + (playerField != null)
                                        + " Player.Position.XY="
                                        + (positionXField != null && positionYField != null);
                    return false;
                }

                _getWorld = FieldAccessor.Static(sharedField);
                _getPlayer = FieldAccessor.Reference(playerField);
                _getPlayerX = FieldAccessor.NestedFloat(playerPositionField, positionXField);
                _getPlayerY = FieldAccessor.NestedFloat(playerPositionField, positionYField);
                if (radiusField != null)
                {
                    _getPlayerRadius = FieldAccessor.Float(radiusField);
                }
                if (healthField != null)
                {
                    _getPlayerHealth = FieldAccessor.Double(healthField);
                }
                if (deadField != null)
                {
                    _getPlayerDead = FieldAccessor.Bool(deadField);
                }
                if (safeField != null)
                {
                    _getSafeWorld = FieldAccessor.Bool(safeField);
                }
                if (sceneField != null && inputField != null)
                {
                    _getGameScene = FieldAccessor.Reference(sceneField);
                    _getInput = FieldAccessor.Reference(inputField);
                    if (movingField != null)
                    {
                        _getMoving = FieldAccessor.Bool(movingField);
                    }
                    if (moveAngleField != null && angleRadiansField != null)
                    {
                        _getMoveAngleRadians = FieldAccessor.NestedFloat(moveAngleField, angleRadiansField);
                    }
                }
                if (rotationField != null)
                {
                    _getWorldRotation = FieldAccessor.StaticFloat(rotationField);
                }

                // The game's own clock, for the `LogicTotal` the sampler already depends on.
                Type clockType = game.GetType("DarzasDominion.Graphics.ClientTime");
                FieldInfo staticClock = game.GetType("DarzasDominion.Graphics.DarzaWindow")?
                    .GetField("Time", Any);
                FieldInfo logicTotal = clockType?.GetField("LogicTotal", Any);
                if (staticClock != null && logicTotal != null)
                {
                    _getClock = FieldAccessor.Static(staticClock);
                    _getLogicTotal = FieldAccessor.Float(logicTotal);
                }

                ResolveGrid(worldType, Any);

                string dir = !string.IsNullOrEmpty(runDir)
                    ? runDir
                    : Environment.GetEnvironmentVariable("DRELAY_HOOK_RUNDIR");
                if (string.IsNullOrEmpty(dir))
                {
                    _unresolvedReason = "no run directory (DRELAY_HOOK_RUNDIR is unset)";
                    return false;
                }
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, FileName);
                _writer = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write,
                                                          FileShare.Read, 1 << 16));
                _writer.AutoFlush = false;
                _enabled = true;
                _resolved = true;

                WriteHeader();
                HookEntry.AppendLog("INFO", "trace recorder ARMED: writing " + _path
                                            + " -- player, grid and every culled threat's sampled path, "
                                            + "JSON lines, one frame per line. The cost is real; this "
                                            + "switch is off by default for that reason.");
                HookEntry.FlushLog();
                return true;
            }
            catch (Exception ex)
            {
                _unresolvedReason = ex.GetType().Name + ": " + ex.Message;
                _enabled = false;
                return false;
            }
        }

        private static FieldInfo FindRadius(Type playerType)
        {
            if (playerType == null)
            {
                return null;
            }
            FieldInfo f = playerType.GetField("Radius",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                return f;
            }
            // `GameObject.Radius` is on the base, not on `Player`.
            Type t = playerType.BaseType;
            while (t != null && t != typeof(object))
            {
                f = t.GetField("Radius", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null)
                {
                    return f;
                }
                t = t.BaseType;
            }
            return null;
        }

        /// <summary>
        /// The client's collision grid: `private byte[,] World.&lt;grid&gt;`, the one `World` itself
        /// indexes with a truncated float position.
        ///
        /// THE NAMES ARE PRIVATE-USE CODE POINTS AND THAT IS NOT A PROBLEM HERE. The field is
        /// obfuscated, so it cannot be found by name -- it is found as *the only `byte[,]` on
        /// `World`*, which is a structural property rather than a spelling, and this project has
        /// been bitten twice by trusts placed in spellings (PHASE3-SCOPE.md section 4.3).
        /// </summary>
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
        // The per-frame entry
        // ===================================================================================

        /// <summary>
        /// One frame. Called from the frame replacement, after the sensor, on a frame the sensor
        /// says it read successfully.
        ///
        /// THE ORDER MATTERS AND IS NOT ARBITRARY. It runs after `SensorRead.OnFrame` because it
        /// borrows that frame's projectile list rather than walking the layer a second time --
        /// the walk is the expensive part and doing it twice would double the frame cost of a
        /// run whose whole purpose is to be a normal run.
        /// </summary>
        public static void OnFrame(List<object> liveProjectiles, List<ThreatKey> liveKeys,
                                   int frame, float frameSeconds)
        {
            if (!_enabled || _writer == null)
            {
                return;
            }

            try
            {
                object world = _getWorld();
                object player = world == null ? null : _getPlayer(world);
                if (player == null)
                {
                    return;   // no character yet: nothing to record and nothing to decide on
                }

                float px = _getPlayerX(player);
                float py = _getPlayerY(player);

                WriteFrame(frame, frameSeconds, world, player, px, py, liveProjectiles, liveKeys);
            }
            catch (Exception ex)
            {
                // ONCE PER RUN, for the reason the frame path's own catch is written that way: an
                // exception thrown every frame would fill the log ring and hide everything else.
                if (System.Threading.Interlocked.Exchange(ref _failureLogged, 1) == 0)
                {
                    HookEntry.AppendLog("ERROR", "the trace recorder threw; the trace is truncated "
                                                 + "from this frame and the game is unaffected: "
                                                 + ex.GetType().Name + ": " + ex.Message + " | "
                                                 + HookEntry.FirstFrames(ex));
                    HookEntry.FlushLog();
                }
            }
        }

        private static void WriteFrame(int frame, float frameSeconds, object world, object player,
                                       float px, float py, List<object> projectiles, List<ThreatKey> keys)
        {
            float radius = _getPlayerRadius != null ? _getPlayerRadius(player) : 0.25f;
            double health = _getPlayerHealth != null ? _getPlayerHealth(player) : 0.0;
            bool dead = _getPlayerDead != null && _getPlayerDead(world);
            bool safe = _getSafeWorld != null && _getSafeWorld(world);
            float rotation = _getWorldRotation != null ? _getWorldRotation() : 0.0f;
            float clock = 0.0f;
            if (_getClock != null && _getLogicTotal != null)
            {
                object c = _getClock();
                if (c != null)
                {
                    clock = _getLogicTotal(c);
                }
            }

            float intent = 0.0f;
            bool moving = false;
            if (_getInput != null && _getGameScene != null)
            {
                object scene = _getGameScene(world);
                object input = scene == null ? null : _getInput(scene);
                if (input != null)
                {
                    if (_getMoving != null)
                    {
                        moving = _getMoving(input);
                    }
                    if (_getMoveAngleRadians != null)
                    {
                        intent = _getMoveAngleRadians(input);
                    }
                }
            }

            // THE GRID RECORD IS WRITTEN BEFORE THE FRAME THAT REFERENCES IT, AND ONLY WHEN THE
            // WINDOW HAS MOVED. The window is a property of the map and of which tile the player
            // is standing on; writing it every frame would multiply the file for no information,
            // and writing it *after* the frame would mean the reader sees a dangling id.
            RefreshGrid(world, px, py);

            Line.Length = 0;
            Line.Append("{\"t\":\"F\",\"f\":").Append(frame);
            Line.Append(",\"dt\":").Append(Num(frameSeconds));
            Line.Append(",\"clock\":").Append(Num(clock));
            Line.Append(",\"px\":").Append(Num(px));
            Line.Append(",\"py\":").Append(Num(py));
            Line.Append(",\"r\":").Append(Num(radius));
            Line.Append(",\"hp\":").Append(Num(health));
            Line.Append(",\"moving\":").Append(moving ? "true" : "false");
            Line.Append(",\"intent\":").Append(Num(intent));
            Line.Append(",\"dead\":").Append(dead ? "true" : "false");
            Line.Append(",\"safe\":").Append(safe ? "true" : "false");
            Line.Append(",\"rot\":").Append(Num(rotation));
            Line.Append(",\"grid\":").Append(_gridWindowValid ? _gridGeneration : 0).Append(',');

            // Cull first, record second (section 8's mitigation 1). A bullet that cannot reach
            // the player inside the horizon has no bearing on the decision the trace exists to
            // check, and recording it would make the file mostly noise.
            Line.Append("\"threats\":[");
            int written = 0;
            int skipped = 0;
            long declinedBefore = TrajectorySampler.Declined;
            if (projectiles != null && keys != null)
            {
                for (int i = 0; i < projectiles.Count && i < keys.Count; i++)
                {
                    if (written >= MaxThreatsPerFrame)
                    {
                        skipped++;
                        continue;
                    }
                    if (!TrajectorySampler.FillTrajectory(projectiles[i], out Trajectory t))
                    {
                        continue;
                    }
                    t.Key = keys[i].Packed;
                    if (!CouldReach(t, px, py, radius))
                    {
                        skipped++;
                        continue;
                    }
                    if (written > 0)
                    {
                        Line.Append(',');
                    }
                    AppendThreat(t, keys[i]);
                    written++;
                }
            }
            Line.Append(']');
            Line.Append(",\"skipped\":").Append(skipped);

            // AND THE ONES THE CLIENT SAYS CANNOT HIT THE PLAYER, COUNTED RATHER THAN MERELY DROPPED.
            //
            // THIS NUMBER IS THE INSTRUMENT FOR THE FIX, NOT DECORATION. `FillTrajectory` declines
            // a projectile whose `HitsPlayer` is false or whose `LocalSeedDerived` is true
            // (SPIKE3.2-OFFLINE-SOLVER.md section 12), which on the run that produced this line was
            // **51.8 % of every threat record** -- a player's own shots, born inside their collision
            // disc. A frame line with `nohit=0` on a session where the player was shooting means the
            // exclusion is not in force, and `bootstrap.log`'s resolution line says why. A count is
            // the only way to tell "there were none" from "the filter did nothing", and the two look
            // identical in the threat array.
            Line.Append(",\"nohit\":").Append(TrajectorySampler.Declined - declinedBefore);
            Line.Append('}');
            Line.Append('\n');

            _writer.Write(Line.ToString());
            _framesRecorded++;
            _threatsRecorded += written;
            _threatsSkipped += skipped;

            // Flushed periodically rather than per frame: the operator closes the game by hand and
            // a buffer that dies with the process takes the trace with it. Every 30 frames is half
            // a second of exposure, which is the same trade `HookEntry.FlushLog` makes.
            if ((frame % 30) == 0)
            {
                _writer.Flush();
            }

            if (--_framesUntilLog <= 0)
            {
                _framesUntilLog = 600;
                HookEntry.AppendLog("INFO", "trace: " + _framesRecorded + " frame(s), "
                                            + _threatsRecorded + " threat record(s), "
                                            + _threatsSkipped + " culled, "
                                            + (_writer.BaseStream.Length / 1024) + " KB");
                HookEntry.FlushLog();
            }
        }

        /// <summary>
        /// Whether a bullet could reach the player's disc inside the trust prefix.
        ///
        /// A CULL, AND THE DIRECTION OF ITS ERROR MATTERS. It may drop a bullet that would have
        /// mattered only if the sampled path is a bad model of the true one -- and if the model is
        /// that bad, the trace is not evidence about the model anyway. What it must never do is
        /// drop a bullet whose own sampled path crosses the player, which it cannot: this is a
        /// bounding-box test against the very samples the solver would have used.
        /// </summary>
        private static bool CouldReach(Trajectory t, float px, float py, float playerRadius)
        {
            float r = playerRadius + t.Radius;
            float r2 = r * r;
            for (int s = 0; s < t.Count; s++)
            {
                float dx = t.Xs[s] - px;
                float dy = t.Ys[s] - py;
                if ((dx * dx) + (dy * dy) <= r2)
                {
                    return true;
                }
            }
            // Also keep a bullet whose *current* disc is within a few radii, which is the case
            // where the model is least certain and the evidence most valuable.
            float cx = t.Xs[0] - px;
            float cy = t.Ys[0] - py;
            float near = r + 2.0f;
            return (cx * cx) + (cy * cy) <= (near * near);
        }

        private static void AppendThreat(Trajectory t, ThreatKey key)
        {
            // THE IDENTITY IS THE SENSOR'S, NOT THE SAMPLER'S, AND GETTING THAT WRONG MADE THE
            // WHOLE TRACE USELESS FOR T7. `Trajectory` carries a `GameId` field and
            // `TrajectorySampler.FillTrajectory` leaves it at zero on purpose -- identity is not
            // that file's to read (see its comment) and the sensor is the one that resolves it, as
            // `(GameId, startTime)`. The first version of this method wrote `t.GameId` instead of
            // the key it was handed, so **every bullet in the trace was recorded as
            // `GameId = 0`** -- and `(0, startTime)` collides across bullets and matches nothing
            // the game ever resolved. The recorded run read as: 11 predictions, all false
            // positives, and two real collisions the model "never predicted".
            Line.Append("{\"id\":").Append(key.GameId);
            Line.Append(",\"st\":").Append(key.StartTime);
            Line.Append(",\"x\":").Append(Num(t.Xs[0]));
            Line.Append(",\"y\":").Append(Num(t.Ys[0]));
            Line.Append(",\"rad\":").Append(Num(t.Radius));
            Line.Append(",\"lin\":").Append(t.Projectable ? "1" : "0");
            Line.Append(",\"dmg\":").Append(Num(t.Damage));
            Line.Append(",\"dmgp\":").Append(Num(t.DamagePercent));
            Line.Append(",\"mdmg\":").Append(t.ManaDamage);
            Line.Append(",\"n\":").Append(t.Count);
            Line.Append(",\"xs\":[");
            for (int s = 0; s < t.Count; s++)
            {
                if (s > 0)
                {
                    Line.Append(',');
                }
                Line.Append(Num(t.Xs[s]));
            }
            Line.Append("],\"ys\":[");
            for (int s = 0; s < t.Count; s++)
            {
                if (s > 0)
                {
                    Line.Append(',');
                }
                Line.Append(Num(t.Ys[s]));
            }
            Line.Append("],\"alive\":[");
            for (int s = 0; s < t.Count; s++)
            {
                if (s > 0)
                {
                    Line.Append(',');
                }
                Line.Append(t.Alive[s] ? '1' : '0');
            }
            Line.Append("]}");
        }

        // ===================================================================================
        // The collision grid
        // ===================================================================================

        /// <summary>
        /// Re-reads the window when the player's tile has moved, and writes the `G` record.
        ///
        /// THE WINDOW'S ORIGIN IS THE PLAYER'S TILE MINUS THE RADIUS, AND THE ARITHMETIC IS THE
        /// GAME'S. `World` indexes its grid with a C cast of the float position (`conv.i4`,
        /// truncation toward zero), so this does the same and the offline harness can therefore
        /// reproduce the game's own walkability answer exactly -- which is the whole point of
        /// recording the grid rather than recording a boolean.
        /// </summary>
        private static void RefreshGrid(object world, float px, float py)
        {
            if (!_gridResolved || _getGridArray == null || _gridAt == null || world == null)
            {
                return;
            }

            int originX = (int)px - GridRadius;
            int originY = (int)py - GridRadius;
            if (!_gridWindowValid || originX != _gridOriginX || originY != _gridOriginY)
            {
                _gridOriginX = originX;
                _gridOriginY = originY;
                _gridDirty = true;
            }

            if (!_gridDirty)
            {
                return;
            }

            object array = _getGridArray(world);
            if (array == null)
            {
                return;
            }

            int w = 2 * GridRadius;
            int h = 2 * GridRadius;
            if (_gridCells.Length < w * h)
            {
                _gridCells = new byte[w * h];
            }

            for (int gy = 0; gy < h; gy++)
            {
                for (int gx = 0; gx < w; gx++)
                {
                    byte c = 0;
                    try
                    {
                        c = _gridAt(array, originX + gx, originY + gy);
                    }
                    catch (Exception)
                    {
                        // Out of the map's bounds. The game's own query bounds-checks and answers
                        // "not solid"; here the reader is told the same thing, and the solver's
                        // own `InWindow` test is what refuses to rely on it for a candidate it
                        // cannot see.
                        c = 0;
                    }
                    _gridCells[(gy * w) + gx] = c;
                }
            }

            _gridWidth = w;
            _gridHeight = h;
            _gridWindowValid = true;
            _gridDirty = false;
            _gridGeneration++;

            // RLE over the linear cell order, which is the encoding the game's own `GmTiles`
            // uses for the same field: (value, runLength) pairs. A window is mostly one value, so
            // this is a few hundred bytes instead of two thousand.
            Line.Length = 0;
            Line.Append("{\"t\":\"G\",\"g\":").Append(_gridGeneration);
            Line.Append(",\"ox\":").Append(originX);
            Line.Append(",\"oy\":").Append(originY);
            Line.Append(",\"w\":").Append(w);
            Line.Append(",\"h\":").Append(h);
            Line.Append(",\"rle\":[");
            int i = 0;
            bool first = true;
            while (i < _gridCells.Length)
            {
                byte value = _gridCells[i];
                int run = 1;
                while (i + run < _gridCells.Length && _gridCells[i + run] == value)
                {
                    run++;
                }
                if (!first)
                {
                    Line.Append(',');
                }
                first = false;
                Line.Append(value).Append(',').Append(run);
                i += run;
            }
            Line.Append("]}\n");
            _writer.Write(Line.ToString());
        }

        // ===================================================================================
        // Header, close, and the frame-seconds source
        // ===================================================================================

        private static void WriteHeader()
        {
            Line.Length = 0;
            Line.Append("{\"t\":\"H\",\"v\":1");
            Line.Append(",\"dodge\":\"").Append(EscapeJson(ConfigLine())).Append('"');
            Line.Append(",\"player\":\"").Append(EscapeJson(ManagedArmingProbe.PlayerTypeName)).Append('"');
            Line.Append(",\"projectile\":\"").Append(EscapeJson(SensorRead.ProjectileTypeName)).Append('"');
            Line.Append(",\"samples\":").Append(Trajectory.MaxSamples);
            Line.Append(",\"gridSource\":\"").Append(_gridResolved ? "World.byte[,]" : "none").Append('"');
            Line.Append('}');
            Line.Append('\n');
            _writer.Write(Line.ToString());
            _writer.Flush();
        }

        /// <summary>The configuration the solver would run with, recorded so a trace explains itself.</summary>
        private static string ConfigLine()
        {
            try
            {
                return DodgeConfig.FromEnvironment().Describe();
            }
            catch (Exception)
            {
                return "unavailable";
            }
        }

        /// <summary>Flushes and closes. Safe to call more than once; the process may die without it.</summary>
        public static void Close()
        {
            if (_writer == null)
            {
                return;
            }
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (Exception)
            {
                // Nothing useful can be done here, and throwing from a shutdown path is worse.
            }
            _writer = null;
            _enabled = false;
        }

        /// <summary>The counters, for the run summary.</summary>
        public static string Summary()
        {
            if (_path == null)
            {
                return "trace: not enabled (" + _unresolvedReason + ")";
            }
            return "trace: " + _framesRecorded + " frame(s), " + _threatsRecorded
                   + " threat record(s), " + _threatsSkipped + " culled, path " + _path;
        }

        // ===================================================================================
        // Formatting
        // ===================================================================================

        /// <summary>
        /// A float, invariantly formatted and short.
        ///
        /// `R`-style round-tripping is not used: the trace's job is to be read by a float parser,
        /// and four decimals is well inside the tolerance of every quantity here (world units are
        /// metres-of-a-tile and milliseconds are integers). It halves the file, which for a
        /// 30-second recording of 40 bullets is the difference between 7 MB and 14.
        /// </summary>
        private static string Num(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return "0";
            }
            return value.ToString("0.####", Inv);
        }

        private static string Num(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return "0";
            }
            return value.ToString("0.####", Inv);
        }

        private static string EscapeJson(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            return text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
