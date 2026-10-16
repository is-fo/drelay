// RouteRuntime.cs -- the live half of `PLAN-AUTOFARM.md` section 7: record, load, and publish.
//
// =====================================================================================
// WHAT IS HERE AND WHAT IS NOT
// =====================================================================================
//
// Everything with an opinion in it lives in `Route.cs` and `RouteFollower.cs`, which are PURE and
// compile with no game in sight: the simplifier, the resampler, the snap, the deviation, the file
// format. This file is the part that has to touch the client -- resolve `World.Shared`, read the
// player's position, read `MapInfo`, open the routes directory -- and it does nothing else. That
// split is `DodgeSnapshot.cs`'s contract applied to a third module, and it is what lets the route's
// arithmetic be measured offline.
//
// =====================================================================================
// THE TWO SWITCHES, AND WHY THEY ARE TWO
// =====================================================================================
//
//   `DRELAY_ROUTE_RECORD=<name>`   record the operator's walk into `routes/<name>.json`
//   `DRELAY_DODGE_ROUTE=<name>`    follow `routes/<name>.json` when the farmer has no target
//
// THEY ARE SEPARATE BECAUSE THEY ARE SEPARATE SESSIONS. Recording is something the operator does
// once, deliberately, walking the route they want; following is what a farm run does afterwards. A
// single switch would make every recording run also a following run -- the character would be
// dragged at waypoints it has just laid down -- and section 7.1's whole point is that the recording
// is a plain walk with the operator's hands on it.
//
// =====================================================================================
// THE FILE IS REWRITTEN PERIODICALLY AND THAT IS NOT LAZINESS
// =====================================================================================
//
// `TraceRecorder.Close` is never called: the replacement does not know when the recording window
// closes, because the native side owns that, and a process that is killed leaves no shutdown. A
// route written only at close would therefore be a route that never exists. So the file is rewritten
// every `RewriteEverySamples` samples -- about every two seconds at the plan's 10 Hz -- and a session
// that ends abruptly leaves the walk up to that moment. The route is a few kilobytes; the cost is a
// file write per two seconds on a run whose entire purpose is that one file.

using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Drelay.Bootstrap
{
    /// <summary>
    /// The route recorder and follower, as the frame path sees them.
    /// </summary>
    public static class RouteRuntime
    {
        private const string EnvRecord = "DRELAY_ROUTE_RECORD";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static;

        /// <summary>
        /// How many samples between rewrites of the file. Twenty at the plan's 10 Hz is two seconds.
        /// See the header for why the file is not written once at close.
        /// </summary>
        private const int RewriteEverySamples = 20;

        private static bool _resolved;
        private static string _unresolvedReason = "not attempted";

        private static Func<object> _getWorld;
        private static Func<object, object> _getPlayer;
        private static Func<object, float> _playerX;
        private static Func<object, float> _playerY;

        private static Func<object, object> _getMapInfo;
        private static Func<object, string> _mapName;
        private static Func<object, string> _mapAsset;
        private static Func<object, int> _mapWidth;
        private static Func<object, int> _mapHeight;

        // --- recording -----------------------------------------------------------------------

        private static bool _recording;
        private static string _recordName;
        private static string _recordPath;
        private static readonly RouteSampler Sampler = new RouteSampler();
        private static int _samplesSinceWrite;
        private static long _writes;
        private static long _samplesWritten;
        private static string _recordFailure;

        // --- following -----------------------------------------------------------------------

        /// <summary>
        /// The follower. Read by the adapter, which asks it for a goal when the farmer has none.
        ///
        /// IT IS PUBLIC AND THE ADAPTER READS IT rather than the adapter owning it, because the two
        /// halves of section 7 share the route's identity: `ApplyGoal` has to know whether the world
        /// still matches the route's map block, and that question belongs to the module that loaded
        /// the file.
        /// </summary>
        public static readonly RouteFollower Follower = new RouteFollower();

        private static bool _followRequested;
        private static string _followName;
        private static string _followRefusal;

        /// <summary>True when a route is loaded and being followed.</summary>
        public static bool Following => Follower.Active;

        /// <summary>Why a requested route is not being followed; null when it is, or when none was asked for.</summary>
        public static string FollowRefusal => _followRefusal;

        /// <summary>Why the runtime is not available; null when it is.</summary>
        public static string UnresolvedReason => _unresolvedReason;

        // ===================================================================================
        // Resolution
        // ===================================================================================

        /// <summary>
        /// Reads both switches and, when a route is to be followed, loads and validates its file.
        ///
        /// IT IS CALLED AFTER `DodgeAdapter.Resolve`, for the reason `WorldLog`'s resolve records:
        /// the config it needs is the adapter's own, and the adapter does not exist until then.
        /// </summary>
        public static bool Resolve(Assembly game, DodgeConfig config, string runDir)
        {
            if (game == null)
            {
                _unresolvedReason = "the game assembly is not loaded";
                return false;
            }
            Type worldType = game.GetType(ManagedArmingProbe.WorldTypeName);
            Type playerType = game.GetType(ManagedArmingProbe.PlayerTypeName);
            if (worldType == null)
            {
                _unresolvedReason = ManagedArmingProbe.WorldTypeName + " not found";
                return false;
            }

            FieldInfo sharedField = worldType.GetField("Shared", Any);
            if (sharedField == null)
            {
                _unresolvedReason = "World.Shared not found";
                return false;
            }
            _getWorld = FieldAccessor.Static(sharedField);

            FieldInfo playerField = worldType.GetField("Player", Any);
            FieldInfo positionField = playerType?.GetField("Position", Any);
            FieldInfo px = positionField?.FieldType.GetField("X");
            FieldInfo py = positionField?.FieldType.GetField("Y");
            if (playerField != null && positionField != null && px != null && py != null)
            {
                _getPlayer = FieldAccessor.Reference(playerField);
                _playerX = FieldAccessor.NestedFloat(positionField, px);
                _playerY = FieldAccessor.NestedFloat(positionField, py);
            }

            // THE MAP BLOCK IS THE ROUTE'S VALIDITY CONDITION, so the accessors that name it are
            // resolved for the FOLLOWER even when nothing is being recorded. A build where they do
            // not resolve cannot validate a route, and then the honest answer is to refuse every
            // route rather than to follow one that might be for another realm.
            FieldInfo mapInfoField = worldType.GetField("MapInfo", Any);
            if (mapInfoField != null)
            {
                Type mapType = mapInfoField.FieldType;
                _getMapInfo = FieldAccessor.Reference(mapInfoField);
                _mapName = StringField(mapType, "Name");
                _mapAsset = StringField(mapType, "AssetName");
                _mapWidth = IntField(mapType, "Width");
                _mapHeight = IntField(mapType, "Height");
            }

            string recordName = Environment.GetEnvironmentVariable(EnvRecord);
            if (!string.IsNullOrEmpty(recordName) && recordName != "0")
            {
                if (string.IsNullOrEmpty(runDir))
                {
                    _unresolvedReason = "recording asked for but DRELAY_HOOK_RUNDIR is unset";
                    return false;
                }
                if (_getPlayer == null)
                {
                    _unresolvedReason = "recording asked for but Player.Position did not resolve";
                    return false;
                }
                string directory = Path.Combine(runDir, "routes");
                Directory.CreateDirectory(directory);
                _recordName = recordName;
                _recordPath = Path.Combine(directory, recordName + ".json");
                Sampler.Reset(100.0f, 1.0f);
                _recording = true;
                _samplesSinceWrite = 0;
            }

            if (config.RouteMode)
            {
                _followRequested = true;
                _followName = config.RouteName;
                if (string.IsNullOrEmpty(runDir))
                {
                    _followRefusal = "DRELAY_DODGE_ROUTE is set and DRELAY_HOOK_RUNDIR is not, so"
                                     + " there is no routes directory to read";
                }
                else
                {
                    LoadRoute(Path.Combine(Path.Combine(runDir, "routes"), config.RouteName + ".json"));
                }
            }

            _resolved = true;
            return true;
        }

        private static void LoadRoute(string path)
        {
            string text;
            try
            {
                if (!File.Exists(path))
                {
                    _followRefusal = "no route file at " + path;
                    return;
                }
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                _followRefusal = "could not read " + path + ": " + ex.Message;
                return;
            }

            Route route;
            string why;
            if (!Route.TryParse(text, out route, out why))
            {
                _followRefusal = path + " is not a route: " + why;
                return;
            }
            // THE NAME ON THE SWITCH WINS OVER THE NAME IN THE FILE, and the mismatch is reported
            // rather than silently accepted: a file copied to the wrong name is a file whose map
            // block nobody re-checked, and the name is what the log says the run followed.
            route.Name = route.Name == null || route.Name.Length == 0 ? Path.GetFileNameWithoutExtension(path) : route.Name;
            string setWhy;
            if (!Follower.SetRoute(route, out setWhy))
            {
                _followRefusal = path + " is not usable: " + setWhy;
                return;
            }
            _followRefusal = null;
        }

        // ===================================================================================
        // The frame
        // ===================================================================================

        /// <summary>
        /// One frame of RECORDING. Reads the player's position and offers it to the sampler, and
        /// rewrites the file every `RewriteEverySamples` samples.
        ///
        /// IT RETURNS IMMEDIATELY WHEN NOTHING IS BEING RECORDED, which is every run but the
        /// operator's collection walk -- one boolean, the same accounting `TraceRecorder` makes.
        /// </summary>
        public static void OnFrame(DodgeConfig config, int frame, float clockMs, CollisionWindow grid)
        {
            if (!_recording)
            {
                return;
            }
            try
            {
                object world = _getWorld();
                object player = world == null || _getPlayer == null ? null : _getPlayer(world);
                if (player == null)
                {
                    return;
                }
                if (!Sampler.Observe(_playerX(player), _playerY(player), clockMs))
                {
                    return;
                }
                _samplesSinceWrite++;
                if (_samplesSinceWrite < RewriteEverySamples)
                {
                    return;
                }
                _samplesSinceWrite = 0;
                WriteRoute(config, world, grid);
            }
            catch (Exception ex)
            {
                // ONE LINE, NOT ONE PER FRAME: a recording that cannot write is a session the
                // operator needs to know about, and a thousand copies of the message is not knowing.
                if (_recordFailure == null)
                {
                    _recordFailure = ex.GetType().Name + ": " + ex.Message;
                }
            }
        }

        /// <summary>
        /// Builds and writes the route. Called on the rewrite cadence and, once, by `Flush`.
        ///
        /// THE SIMPLIFICATION, THE RESAMPLE AND THE SNAP HAPPEN HERE AND NOT INCREMENTALLY, because
        /// all three are properties of the WHOLE walk: a Douglas-Peucker pass over a prefix is not a
        /// prefix of the pass over the walk. That is also why the file is rewritten rather than
        /// appended to.
        /// </summary>
        private static void WriteRoute(DodgeConfig config, object world, CollisionWindow grid)
        {
            string mapName = null;
            string asset = null;
            int width = 0;
            int height = 0;
            object mapInfo = _getMapInfo != null && world != null ? _getMapInfo(world) : null;
            if (mapInfo != null)
            {
                if (_mapName != null) { mapName = _mapName(mapInfo); }
                if (_mapAsset != null) { asset = _mapAsset(mapInfo); }
                if (_mapWidth != null) { width = _mapWidth(mapInfo); }
                if (_mapHeight != null) { height = _mapHeight(mapInfo); }
            }

            // THE MAP BLOCK GOES IN EVEN WHEN IT IS EMPTY, and an empty one makes the route
            // unusable: `MatchesMap` refuses a route that cannot say what world it came from. A file
            // that is written anyway is still evidence for the operator, and it is better than no
            // file -- but it will not be followed, which is the honest reading.
            Route route = Route.Build(_recordName, mapName, asset, width, height,
                                      RecordedFrom(), Sampler.Samples, config.RouteToleranceUnits,
                                      config.RouteSpacingUnits, grid);
            // THE SIMPLIFICATION, THE RESAMPLE AND THE SNAP HAPPEN HERE AND NOT INCREMENTALLY,
            // because all three are properties of the WHOLE walk: a Douglas-Peucker pass over a
            // prefix is not a prefix of the pass over the walk. That is also why the file is
            // rewritten rather than appended to.
            File.WriteAllText(_recordPath, route.ToJson());
            _writes++;
            _samplesWritten = Sampler.Samples.Count;
        }

        /// <summary>
        /// Where and when the walk was taken, as `Route.RecordedFrom`.
        ///
        /// IT IS EVIDENCE AND IT IS NEVER READ BACK -- `RouteFollower` does not look at it -- but the
        /// operator's session produces several files and a directory of routes that cannot say which
        /// walk produced them is a directory nobody can prune. It is the hook's own clock and not the
        /// client's, so it does not need an accessor the run may not have resolved.
        /// </summary>
        private static string RecordedFrom()
        {
            return "recorded " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                   + " UTC by the drelay hook";
        }

        /// <summary>
        /// Offers the route's waypoint as this frame's point goal, when nothing better has one.
        ///
        /// **A TARGET BEATS A ROUTE AND THAT ORDER IS SECTION 5.3'S.** The farmer runs first; when it
        /// has something to fight the route says nothing, and when it does not the route is the
        /// `TRAVEL` answer. The caller does not have to know that -- it calls the farmer and then
        /// calls this, and this returns without touching the snapshot when a point is already there.
        ///
        /// IT WRITES `GoalHasPoint`/`GoalX`/`GoalY` AND NOTHING ELSE, which is the same contract the
        /// farmer keeps: a route is a third answer to "where should the goal be", and everything
        /// downstream -- the planner, admission, the score, the hysteresis, the escape floor -- is
        /// unchanged by it.
        /// </summary>
        public static bool ApplyGoal(object world, DodgeConfig config, DodgeSnapshot s)
        {
            if (!_followRequested || s == null)
            {
                return false;
            }
            if (s.GoalHasPoint)
            {
                // The farmer already answered. See the method comment for why this is not an error.
                return false;
            }
            if (!Follower.Active)
            {
                return false;
            }

            // THE MAP BLOCK, CHECKED ON A CHANGE OF MAP RATHER THAN EVERY FRAME. Reading the map
            // identity is four delegate calls and a string compare; asking only when the `MapInfo`
            // object reference moves is the same signal section 5.1 makes primary, and it is the
            // cheap one.
            object mapInfo = _getMapInfo != null && world != null ? _getMapInfo(world) : null;
            if (!ReferenceEquals(mapInfo, _lastValidatedMapInfo))
            {
                _lastValidatedMapInfo = mapInfo;
                string name = _mapName != null && mapInfo != null ? _mapName(mapInfo) : null;
                string asset = _mapAsset != null && mapInfo != null ? _mapAsset(mapInfo) : null;
                int width = _mapWidth != null && mapInfo != null ? _mapWidth(mapInfo) : 0;
                int height = _mapHeight != null && mapInfo != null ? _mapHeight(mapInfo) : 0;
                if (!Follower.Route.MatchesMap(name, asset, width, height))
                {
                    // REFUSED BY NAME, which is section 7.1's rule: "replaying it into a different
                    // map is a bug, not a fallback". The follower is dropped rather than degraded, so
                    // the goal falls back to the player's own intent -- the behaviour of a run with no
                    // route at all.
                    _followRefusal = Follower.Route.MapMismatch(name, asset, width, height);
                    Follower.Clear();
                    return false;
                }
            }

            float gx;
            float gy;
            if (!Follower.Step(s.Player.X, s.Player.Y, config, s.ClockMs, s.Grid, out gx, out gy))
            {
                return false;
            }
            s.GoalHasPoint = true;
            s.GoalX = gx;
            s.GoalY = gy;
            // AND WHICH MODULE PUBLISHED IT. The solver needs this and cannot infer it: a farmer
            // stops once it is inside `FarmStopRadius` and a route never stops, and the arrival
            // policy is the only place the two differ. See `DodgeSnapshot.GoalFromRoute`.
            s.GoalFromRoute = true;
            return true;
        }

        private static object _lastValidatedMapInfo;

        /// <summary>Called where the world changes. A route belongs to a map; a new map re-checks it.</summary>
        public static void OnWorldChanged()
        {
            _lastValidatedMapInfo = null;
        }

        /// <summary>Flushes the recording, building the route from everything sampled so far.</summary>
        public static void Flush(DodgeConfig config, CollisionWindow grid)
        {
            if (!_recording || Sampler.Samples.Count < 2)
            {
                return;
            }
            try
            {
                WriteRoute(config, _getWorld(), grid);
            }
            catch (Exception ex)
            {
                if (_recordFailure == null)
                {
                    _recordFailure = ex.GetType().Name + ": " + ex.Message;
                }
            }
        }

        // ===================================================================================
        // The report
        // ===================================================================================

        /// <summary>One line for the hook log, so a run says what the routes are doing.</summary>
        public static string Describe()
        {
            if (!_resolved)
            {
                return "routes: not available (" + _unresolvedReason + ")";
            }
            string text = "";
            if (_recording)
            {
                text = "recording '" + _recordName + "' to " + _recordPath + ": "
                       + Sampler.Samples.Count + " sample(s), " + Sampler.Suppressed
                       + " suppressed by the time/distance filter, " + _writes + " write(s)"
                       + (_samplesWritten > 0 ? ", last with " + _samplesWritten + " sample(s)" : "");
                if (_recordFailure != null)
                {
                    text += " -- FAILED: " + _recordFailure;
                }
            }
            if (_followRequested)
            {
                if (text.Length > 0) { text += " | "; }
                text += Follower.Active
                    ? Follower.Describe()
                    : "route '" + _followName + "' refused: " + (_followRefusal ?? "(no reason recorded)");
            }
            return text.Length == 0 ? "routes: idle (neither switch set)" : text;
        }

        // ===================================================================================
        // The accessor helpers
        // ===================================================================================

        private static Func<object, string> StringField(Type owner, string name)
        {
            FieldInfo field = FieldLookup.Find(owner, out _, name);
            if (field == null || field.FieldType != typeof(string))
            {
                return null;
            }
            Func<object, object> read = FieldAccessor.Reference(field);
            return target => read(target) as string;
        }

        private static Func<object, int> IntField(Type owner, string name)
        {
            FieldInfo field = FieldLookup.Find(owner, out _, name);
            return field == null ? null : FieldAccessor.Int(field);
        }
    }
}
