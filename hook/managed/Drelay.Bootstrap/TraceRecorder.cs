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

        /// <summary>
        /// Half-width of the grid window, in tiles, centred on the player.
        ///
        /// 64 RATHER THAN THE 24 IT WAS, AND THE PATHFINDER IS THE REASON. 24 was exactly the region
        /// the 600 ms admission horizon could touch -- the comment here used to say so -- and a
        /// decision that never looks further than one step does not need more. A path does: it is a
        /// route around a corridor of fire toward a point on the player's own line, and with a 24-cell
        /// radius the search would be aiming at a target it cannot see the far side of.
        ///
        /// At one world unit per cell this is 128 units across, and the recorder copies it when the
        /// player's tile changes and every `GridRefreshFrames` -- the live window's own cadence, for
        /// the reason stated there: a stationary character in a dungeon sees its map stream in, and
        /// a recorder keyed only on movement would miss it. It is written run-length encoded (see
        /// `WriteGridWindow`), and a window is mostly one value.
        ///
        /// IT MUST STAY EQUAL TO `DodgeAdapter`'s OWN, because the harness reproduces the live
        /// decision from this recording: a trace recorded with a smaller window than the live solver
        /// reads is an offline result for a different world.
        /// </summary>
        private const int GridRadius = 64;

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

        /// <summary>
        /// PER CELL, WHERE ITS VALUE CAME FROM. `0` unpainted, `1` collision byte, `2` `NoWalk`
        /// tile, `3` floor (a tile that is walkable), `4` outside the map.
        ///
        /// IT IS WRITTEN INTO THE `G` RECORD AS ITS OWN RLE because the merged bytes cannot answer
        /// the question a dungeon asks: a room can be enclosed by the server's collision, by
        /// `NoWalk` terrain, or by cells that have not streamed in, and the three are the same `1`
        /// in the merged window. The 2026-10-20 Frost Cavern session needed exactly this -- the
        /// window had 1 204 reachable cells, 2 546 `NoWalk` cells, 563 collision cells and 11 945
        /// unpainted ones, and which of them formed the ring around the room could only be inferred.
        /// </summary>
        private static byte[] _gridSource = new byte[(2 * GridRadius) * (2 * GridRadius)];
        private static bool _tileResolved;
        private static Func<object, object> _getTilemap;
        private static Func<object, bool> _getTileNoWalk;
        private static Func<object, int, int, object> _getTileData;

        /// <summary>The tile id, for the `G` record's `wallType`/`floorType`. Optional.</summary>
        private static Func<object, int> _getTileType;
        private static int _gridWidth;
        private static int _gridHeight;
        private static int _gridOriginX;
        private static int _gridOriginY;
        private static bool _gridResolved;
        private static bool _gridWindowValid;
        private static bool _gridDirty;
        private static long _gridGeneration;

        /// <summary>The grid array the cached window was read from, so a map change re-reads it.</summary>
        private static object _gridArrayLast;

        /// <summary>Calls to `RefreshGrid`, and the call the window was last built on. See `GridRefreshFrames`.</summary>
        private static long _gridClock;
        private static long _gridReadClock;

        /// <summary>Whether the cached window was built WITH tile data. See `GridRefreshFrames`.</summary>
        private static bool _gridTileReady;

        /// <summary>
        /// How often the window is re-read even without a tile change, and it must stay equal to
        /// `DodgeAdapter.GridRefreshFrames` for the reason `GridRadius` must stay equal: a trace is
        /// the live window or it is a different world. Half a second covers `GmTiles` arriving while
        /// the character stands still, which is exactly a dungeon entry.
        /// </summary>
        private const int GridRefreshFrames = 30;

        /// <summary>Distinct terrain ids tracked per class in a `G` record's `wallType`/`floorType`.</summary>
        private const int ModeSlots = 8;

        // The `src` RLE's alphabet. See `_gridSource`.
        //
        // THE VALUES ARE `MapRoute.CellUnknown`..`CellOutOfMap` AND MUST STAY SO. The map-scale route
        // takes these numbers directly--`SolverHarness` feeds a recorded `src` array whole into
        // `MapRoute.Observe`--so a recording whose alphabet drifted would be a cache built from
        // another vocabulary: floor would be read as unpainted and every dungeon would look empty.
        private const byte SourceUnpainted = 0;
        private const byte SourceCollision = 1;
        private const byte SourceNoWalkTile = 2;
        private const byte SourceFloor = 3;
        private const byte SourceOutOfMap = 4;

        /// <summary>Appends one run-length encoded byte array, `(value, run)` pairs, as `[..]`.</summary>
        private static void AppendRle(StringBuilder line, byte[] cells, int count)
        {
            line.Append('[');
            int i = 0;
            bool first = true;
            while (i < count)
            {
                byte value = cells[i];
                int run = 1;
                while (i + run < count && cells[i + run] == value)
                {
                    run++;
                }
                if (!first)
                {
                    line.Append(',');
                }
                first = false;
                line.Append(value).Append(',').Append(run);
                i += run;
            }
            line.Append(']');
        }

        /// <summary>
        /// The most common id in a tiny fixed histogram, updated in place.
        ///
        /// A COUNTED MODE AND NOT "THE FIRST TILE SEEN", because a window contains decorative tiles
        /// as well as the terrain in question and one stray id would name the wrong thing. The table
        /// fills after `ModeSlots` distinct ids; cells past that still count in the totals, only the
        /// mode stops being exact for the rarest kinds.
        /// </summary>
        private static int ModeOf(int[] types, int[] counts, int type, int current)
        {
            for (int i = 0; i < ModeSlots; i++)
            {
                if (counts[i] == 0 || types[i] == type)
                {
                    types[i] = type;
                    counts[i]++;
                    break;
                }
            }
            int best = current;
            int bestCount = 0;
            for (int i = 0; i < ModeSlots; i++)
            {
                if (counts[i] > bestCount)
                {
                    bestCount = counts[i];
                    best = types[i];
                }
            }
            return best;
        }

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

            // THE RECORDING HAS TO MATCH WHAT THE LIVE PLANNER SEES, or the offline replay is a
            // decision about a different world. `DodgeAdapter.RefreshGrid` merges the tile half
            // (`GetTile`/`NoWalk`) into the collision bytes; this does the same thing for the same
            // reason.
            ResolveTilemap(worldType, any);
        }

        /// <summary>
        /// The tile half of walkability (`GetTile`/`NoWalk`) and the lava fields on `TileData`, for
        /// the recording. Mirrors `DodgeAdapter.ResolveTilemap`; a failure records the collision
        /// grid alone, which is what every older trace carried.
        /// </summary>
        private static void ResolveTilemap(Type worldType, BindingFlags any)
        {
            try
            {
                FieldInfo tilemapField = FieldLookup.Find(worldType, out _, "Tilemap");
                if (tilemapField == null)
                {
                    _tileResolved = false;
                    return;
                }
                _getTilemap = FieldAccessor.Reference(tilemapField);
                Type tilemapType = tilemapField.FieldType;

                MethodInfo getTile = tilemapType.GetMethod(
                    "GetTile", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(int), typeof(int) }, null);
                Type tileDataType = getTile != null ? getTile.ReturnType : null;
                if (tileDataType != null)
                {
                    try
                    {
                        // EMITTED, NOT BOUND -- see `FieldAccessor.MethodObject2Int`.
                        _getTileData = FieldAccessor.MethodObject2Int(getTile);
                    }
                    catch (Exception) { _getTileData = null; }
                    FieldInfo noWalk = FieldLookup.Find(tileDataType, out _, "NoWalk");
                    if (noWalk != null && noWalk.FieldType == typeof(bool))
                    {
                        _getTileNoWalk = FieldAccessor.Bool(noWalk);
                    }
                    // THE TILE'S ID, FOR ONE QUESTION ONLY: which terrain makes the ring around a
                    // dungeon room that the player cannot walk through. `TileData` derives from
                    // `GameObjectData`, where `Type` is the id the map names; see the `G` record's
                    // `wallType`/`floorType`. `Int` converts a `short` for the same reason it does
                    // everywhere else in this file.
                    FieldInfo tileType = FieldLookup.Find(tileDataType, out _, "Type");
                    if (tileType != null)
                    {
                        try { _getTileType = FieldAccessor.Int(tileType); }
                        catch (Exception) { _getTileType = null; }
                    }
                }
                _tileResolved = _getTilemap != null;
            }
            catch (Exception)
            {
                _tileResolved = false;
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

            // THE GRID RECORD IS WRITTEN BEFORE THE FRAME THAT REFERENCES IT. It is rewritten when
            // the window has moved, when the grid array or the tile map changes, and every
            // `GridRefreshFrames` -- the live window's own cadence. Writing it every frame would
            // multiply the file for no information, and writing it *after* the frame would mean the
            // reader sees a dangling id; writing it only when the WINDOW MOVED was the third
            // mistake, because a dungeon's map arrives while the character stands still.
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
        /// Re-reads the window when the player's tile has moved, when the grid array is a new one,
        /// when the tile map first arrives and every <see cref="GridRefreshFrames"/>, and writes the
        /// `G` record.
        ///
        /// THE WINDOW'S ORIGIN IS THE PLAYER'S TILE MINUS THE RADIUS, AND THE ARITHMETIC IS THE
        /// GAME'S. `World` indexes its grid with a C cast of the float position (`conv.i4`,
        /// truncation toward zero), so this does the same and the offline harness can therefore
        /// reproduce the game's own walkability answer exactly -- which is the whole point of
        /// recording the grid rather than recording a boolean.
        ///
        /// THE THREE REASONS ARE `DodgeAdapter.RefreshGrid`'S, AND THAT IS NOT TIDINESS: a trace
        /// whose window is older than the live one is an offline replay of a different map. The
        /// stationary case is the one that matters for a dungeon -- the map STREAMS IN
        /// (`GmTiles`), so a character standing still in a room sees its floor and walls arrive
        /// while its position does not change, and a recorder keyed only on the player's tile
        /// writes the first, mostly-unpainted window and never corrects it. The live planner had a
        /// 30-frame refresh for exactly that reason; without it here, the trace of the session that
        /// proves the dungeon fix would not contain the fixing data.
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

            object array = _getGridArray(world);
            if (array == null)
            {
                return;
            }
            object tilemap = _tileResolved && _getTilemap != null ? _getTilemap(world) : null;
            _gridClock++;
            bool rebound = !ReferenceEquals(array, _gridArrayLast);
            bool due = _gridWindowValid && _gridClock - _gridReadClock >= GridRefreshFrames;
            bool tileArrived = tilemap != null && !_gridTileReady;
            if (rebound || due || tileArrived)
            {
                _gridDirty = true;
            }

            if (!_gridDirty)
            {
                return;
            }

            int w = 2 * GridRadius;
            int h = 2 * GridRadius;
            if (_gridCells.Length < w * h)
            {
                _gridCells = new byte[w * h];
            }
            if (_gridSource.Length < w * h)
            {
                _gridSource = new byte[w * h];
            }

            Array dimensions = array as Array;
            int mapW = dimensions != null && dimensions.Rank == 2 ? dimensions.GetLength(0) : 0;
            int mapH = dimensions != null && dimensions.Rank == 2 ? dimensions.GetLength(1) : 0;

            // THE THREE SOURCES OF A SOLID CELL, COUNTED IN THE `G` RECORD. They are the answer to
            // the one question the merged bytes cannot answer by themselves -- "is this wall the
            // server's collision, a `NoWalk` tile, or a cell that has not arrived yet" -- and the
            // dungeon diagnosis in `PLAN-AUTOFARM.md` section 14.10 needed exactly that. A trace
            // whose `noTile` count falls while its `tileWall` count rises is a map streaming in; a
            // trace whose room is solid with `noTile == 0` is a room that is really sealed.
            int noTile = 0;
            int tileWall = 0;
            int collisionWall = 0;
            int floorCells = 0;
            int wallType = 0;
            int floorType = 0;
            // A FIXED, TINY HISTOGRAM RATHER THAN A DICTIONARY, because this runs on the frame path
            // (every `GridRefreshFrames`) and a map has a handful of terrain kinds, not hundreds.
            // When the tables fill, the cells past them still count -- only `wallType`/`floorType`
            // stop being exact for the rarest kinds, which is not what the field is for.
            int[] wallTypes = new int[ModeSlots];
            int[] wallCounts = new int[ModeSlots];
            int[] floorTypes = new int[ModeSlots];
            int[] floorCounts = new int[ModeSlots];

            for (int gy = 0; gy < h; gy++)
            {
                for (int gx = 0; gx < w; gx++)
                {
                    int wx = originX + gx;
                    int wy = originY + gy;
                    int index = (gy * w) + gx;
                    byte c = 0;
                    try
                    {
                        c = _gridAt(array, wx, wy);
                    }
                    catch (Exception)
                    {
                        // Out of the map's bounds. The game's own query bounds-checks and answers
                        // "not solid"; here the reader is told the same thing, and the solver's
                        // own `InWindow` test is what refuses to rely on it for a candidate it
                        // cannot see.
                        c = 0;
                    }
                    // OUTSIDE THE MAP IS NOT WALKABLE -- the same rule `DodgeAdapter` applies, so
                    // the recording and the live decision agree about the map's border.
                    if (mapW > 0 && mapH > 0 && (wx < 0 || wy < 0 || wx >= mapW || wy >= mapH))
                    {
                        c = 1;
                        collisionWall++;
                        _gridCells[index] = c;
                        _gridSource[index] = SourceOutOfMap;
                        continue;
                    }
                    if (c != 0)
                    {
                        collisionWall++;
                        _gridSource[index] = SourceCollision;
                    }
                    else if (tilemap != null && _getTileData != null)
                    {
                        // `World`'s move test is `bounds && Tilemap.CanWalk && collision == 0`, and
                        // the window carries both -- through the SAME rule the live window uses
                        // (`CollisionWindow.MergeCell`), because a trace that disagrees with the live
                        // grid is an offline result for a different world. A tile that has not
                        // streamed in yet is no information, not a wall: that distinction is what
                        // keeps a dungeon navigable (see the rule's own comment).
                        object tileData = _getTileData(tilemap, wx, wy);
                        if (tileData == null)
                        {
                            noTile++;
                            _gridSource[index] = SourceUnpainted;
                        }
                        else
                        {
                            bool noWalk = _getTileNoWalk != null && _getTileNoWalk(tileData);
                            int type = _getTileType != null ? _getTileType(tileData) : 0;
                            if (noWalk)
                            {
                                tileWall++;
                                wallType = ModeOf(wallTypes, wallCounts, type, wallType);
                                _gridSource[index] = SourceNoWalkTile;
                            }
                            else
                            {
                                // A WALKABLE TILE IS FLOOR, AND IT IS COUNTED EVEN WHEN THE PLAYER
                                // CANNOT REACH IT. `floorCells` against the player's own component is
                                // the measurement that says "this map contains other rooms": the
                                // 2026-10-20 Frost Cavern window had **1 204 reachable cells and 126
                                // walkable tiles outside that component**, i.e. the dungeon's other
                                // rooms are painted and separated from the player's by a wall of
                                // `NoWalk` terrain -- no doorway in the 128-unit window at all.
                                floorCells++;
                                floorType = ModeOf(floorTypes, floorCounts, type, floorType);
                                _gridSource[index] = SourceFloor;
                            }
                            c = CollisionWindow.MergeCell(c, true, noWalk);
                        }
                    }
                    _gridCells[index] = c;
                }
            }

            _gridWidth = w;
            _gridHeight = h;
            _gridWindowValid = true;
            _gridDirty = false;
            _gridGeneration++;
            _gridArrayLast = array;
            _gridReadClock = _gridClock;
            _gridTileReady = tilemap != null;

            // RLE over the linear cell order, which is the encoding the game's own `GmTiles`
            // uses for the same field: (value, runLength) pairs. A window is mostly one value, so
            // this is a few hundred bytes instead of two thousand.
            Line.Length = 0;
            Line.Append("{\"t\":\"G\",\"g\":").Append(_gridGeneration);
            Line.Append(",\"ox\":").Append(originX);
            Line.Append(",\"oy\":").Append(originY);
            Line.Append(",\"w\":").Append(w);
            Line.Append(",\"h\":").Append(h);
            Line.Append(",\"rle\":");
            AppendRle(Line, _gridCells, w * h);
            // AND ITS OWN RLE FOR WHERE EACH CELL CAME FROM, because the merged bytes answer "solid"
            // for three different things -- the server's collision, a `NoWalk` tile, and a cell that
            // has not arrived -- and a dungeon room's ring has to be told apart from a streamed map's
            // hole. See `_gridSource`; a reader that does not know the field ignores it.
            Line.Append(",\"src\":");
            AppendRle(Line, _gridSource, w * h);
            // AND WHERE THE SOLID CELLS CAME FROM, so a reader can tell a wall from a hole in the
            // streamed map without a live run. See the counters above; `colWall` includes the cells
            // outside the map, which the border rule also makes solid.
            Line.Append(",\"colWall\":").Append(collisionWall);
            Line.Append(",\"tileWall\":").Append(tileWall);
            Line.Append(",\"noTile\":").Append(noTile);
            // AND WHICH TERRAIN THEY ARE. A ring of `NoWalk` around a room is a wall if it is rock
            // and could be a fog/void filler if it is the same id as everything else unexplored; the
            // two are one run apart with these two numbers.
            Line.Append(",\"floorCells\":").Append(floorCells);
            Line.Append(",\"wallType\":").Append(wallType);
            Line.Append(",\"floorType\":").Append(floorType);
            Line.Append("}\n");
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
            // THE TILE HALF IS NAMED IN THE HEADER, because "World.byte[,]" is no longer the whole
            // walkability answer the recording carries. See `ResolveTilemap`. The clause says the
            // rule and not just the source: `NoWalk` tiles are walls and a missing tile is NOT
            // (dungeon maps stream in), so a reader of the trace can tell which merge produced its
            // `G` records.
            Line.Append(",\"gridSource\":\"")
                .Append(_gridResolved
                    ? (_tileResolved
                        ? "World.byte[,]+Tilemap.GetTile/NoWalk (a missing tile is not a wall)"
                        : "World.byte[,]")
                    : "none")
                .Append('"');
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
            // THE CONTROL CHARACTERS ARE ESCAPED, AND FOR A WHILE THEY WERE NOT. This handled the
            // backslash and the quote and nothing else, on the reasoning that a settings string has no
            // exotica in it -- and then `DodgeConfig.Describe` grew a multi-line form, and an
            // UNESCAPED NEWLINE went into a JSON string. The result was not a bad value but a BROKEN
            // FILE: line 3 of `trace.jsonl` read `path enable=True target=1.5s ...`, which every
            // line-by-line reader reports as a corrupt recording, and the blame lands on the trace
            // rather than on the writer. A JSON writer that does not escape control characters is a
            // writer that is one string away from this, so it escapes them.
            return text.Replace("\\", "\\\\")
                       .Replace("\"", "\\\"")
                       .Replace("\r", "\\r")
                       .Replace("\n", "\\n")
                       .Replace("\t", "\\t");
        }
    }
}
