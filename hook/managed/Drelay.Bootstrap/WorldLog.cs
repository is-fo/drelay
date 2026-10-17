// WorldLog.cs -- the collection instrument of PLAN-AUTOFARM.md section 6.
//
// =====================================================================================
// WHAT THIS IS FOR, IN THE PLAN'S OWN WORDS
// =====================================================================================
//
// Two of the farmer's work items are BLOCKED ON MEASURED DATA rather than on code: there is no
// realm, dungeon or portal DESTINATION identifier anywhere client-side (`PLAN-AUTOFARM.md` section
// 1), so "walk into that realm" and "whitelist that dungeon" cannot be written until somebody has
// stood in the lobby, walked to the portals and written down what the client can see. The
// operator offered exactly that session, and section 6's requirement is that it be MECHANICAL:
// the hook logs everything into one file and the operator only has to walk around.
//
// This is that file. One JSON object per line, `worlds.jsonl`, in the run directory.
//
// =====================================================================================
// IT IS EVIDENCE AND IT IS NEVER ON THE DECISION PATH
// =====================================================================================
//
// `TraceRecorder` is the shape this is copied from -- `Resolve` once, `OnFrame` on the frame tick,
// its own switch, and nothing at all when the switch is off -- and the discipline is that same
// one: **nothing here may change a dodge.** It writes a file. It reads the world for the file. It
// is called after the decision, it returns immediately when it has no switch, and a frame it
// throws on costs the log a line rather than the run a frame.
//
// =====================================================================================
// WHY IT DOES NOT DECODE THE MAP, AND WHY IT DOES NOT CARRY THE WHOLE ENEMY LIST
// =====================================================================================
//
// Section 6.1 says both and gives the reason: the map data the hook can already read is the
// collision grid, so this logs WHETHER that grid resolved and HOW BIG it is -- "if the whole map is
// needed, that is section 7's full-grid mirror and it should be built then, not now". And the
// enemy list is capped at the eight nearest, because eight rows is how a session's operator says
// "that one, the big one" afterwards without the file growing with a realm's whole population.
//
// =====================================================================================
// THE OPERATOR'S SWITCH, AND THE PLAN'S `--world-log`
// =====================================================================================
//
// The plan calls it `--world-log`; the hook has no command line, so it is `DRELAY_WORLDLOG=1`,
// which is the same surface `DRELAY_TRACE=1` uses and the same surface the launcher's environment
// block carries (`PHASE1-HANDOFF.md` section 4 trap 1: plain `DRELAY_` names, because the launcher
// strips every inherited `DRELAY_HOOK_*` name). `DRELAY_WORLDLOG_EVERY=<frames>` moves the periodic
// cadence; the default is 30 frames, about twice a second at 60 Hz.

using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Drelay.Bootstrap
{
    /// <summary>
    /// THE WORLD-CHANGE LOG. `PLAN-AUTOFARM.md` section 6.
    ///
    /// THE PIVOT OF THE WHOLE INSTRUMENT IS `Resolve`'S TRANSITION STATE, because the plan's section
    /// 5.1 makes world-change detection the pivot of the farmer's state machine and says in as many
    /// words that "this has to be measured, not assumed". So this records WHICH SIGNAL FIRED on
    /// every change it sees -- the `MapInfo` object reference, its `Name`, `SafeWorld`, and the
    /// collision grid's dimensions -- as four independent bits on a transition line. A transition
    /// that fires on the wrong signal is then visible in the file rather than mysterious in a
    /// session, and the four are the same candidates `HANDOFF-DIRECTED-DODGE.md` section 5.2 names.
    /// </summary>
    public static class WorldLog
    {
        private const string EnvWorldLog = "DRELAY_WORLDLOG";
        private const string EnvEvery = "DRELAY_WORLDLOG_EVERY";
        private const string FileName = "worlds.jsonl";

        /// <summary>How many enemies the frame line carries. See the header.</summary>
        private const int NearEnemies = 8;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly StringBuilder Line = new StringBuilder(4096);

        private static bool _resolved;
        private static bool _enabled;
        private static string _unresolvedReason = "not attempted";
        private static string _path;
        private static StreamWriter _writer;
        private static int _everyFrames = 30;
        private static int _sinceLastLine;

        // --- The accessors, resolved once. Every one is optional. ---------------------------

        private static Func<object> _getWorld;
        private static Func<object, object> _getPlayer;

        private static Func<object, object> _getMapInfo;
        private static Func<object, string> _mapName;
        private static Func<object, string> _mapAsset;
        private static Func<object, int> _mapWidth;
        private static Func<object, int> _mapHeight;
        private static Func<object, int> _mapPlayerId;
        private static Func<object, int> _mapDifficulty;
        private static Func<object, bool> _mapSolo;
        private static Func<object, bool> _mapOpenedByKey;
        private static Func<object, bool> _mapCanTeleport;
        private static Func<object, string> _mapLevels;

        private static Func<object, bool> _worldDead;
        private static Func<object, bool> _worldSafe;
        private static Func<object, bool> _worldVault;
        private static Func<object, bool> _worldSpace;
        private static Func<object, object> _getActivatables;
        private static Type _portalType;

        private static Func<object, float> _playerX;
        private static Func<object, float> _playerY;
        private static Func<object, float> _playerRadius;
        private static Func<object, double> _playerHealth;
        private static Func<object, double> _playerMaxHealth;

        private static Func<object, float> _portalX;
        private static Func<object, float> _portalY;
        private static Func<object, int> _portalGameId;
        private static Func<object, string> _portalTitle;
        private static Func<object, string> _portalTag;
        private static Func<object, bool> _portalCalled;
        private static Func<object, bool> _portalLocked;

        private static Func<object, object> _getGridArray;
        private static bool _gridResolved;

        // --- The world-change state ---------------------------------------------------------

        private static object _lastMapInfo;
        private static string _lastMapName;
        private static bool _lastSafe;
        private static bool _lastVault;
        private static int _lastGridWidth = -1;
        private static int _lastGridHeight = -1;
        private static bool _haveBaseline;

        // --- The per-frame measurements -----------------------------------------------------

        private static float _lastX = float.NaN;
        private static float _lastY = float.NaN;
        private static float _lastClockMs = float.NaN;
        private static float _playerSpeed;

        private static readonly System.Collections.Generic.List<EnemyReader.EnemySample> Enemies =
            new System.Collections.Generic.List<EnemyReader.EnemySample>();

        // --- The report's counters ----------------------------------------------------------

        private static long _framesSeen;
        private static long _linesWritten;
        private static long _transitions;
        private static long _lastTransitionFrame = -1;
        private static string _lastTransitionSignals = "";

        /// <summary>Whether the log is switched on and has a file.</summary>
        public static bool Enabled => _resolved && _enabled;

        /// <summary>Why not, when not.</summary>
        public static string UnresolvedReason => _unresolvedReason;

        /// <summary>The file being written, or null.</summary>
        public static string Path => _path;

        // ===================================================================================
        // Resolution
        // ===================================================================================

        /// <summary>
        /// Reads the switch, opens the file, and resolves every accessor the log wants.
        ///
        /// EVERY ACCESSOR IS OPTIONAL AND NONE IS FATAL, which is `TraceRecorder.Resolve`'s rule and
        /// here it is stronger: this instrument exists to find out what the client can see, so a
        /// build where a field has moved must produce a file with that column missing and a header
        /// that NAMES it -- not no file. The header is that list.
        /// </summary>
        public static bool Resolve(Assembly game, string runDir)
        {
            string switchValue = Environment.GetEnvironmentVariable(EnvWorldLog);
            if (string.IsNullOrEmpty(switchValue) || switchValue == "0")
            {
                _unresolvedReason = EnvWorldLog + " is not set";
                return false;
            }

            try
            {
                const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static;

                if (game == null)
                {
                    _unresolvedReason = "the game assembly is not loaded";
                    return false;
                }
                if (string.IsNullOrEmpty(runDir))
                {
                    _unresolvedReason = "no run directory (DRELAY_HOOK_RUNDIR is unset)";
                    return false;
                }
                Directory.CreateDirectory(runDir);
                _path = System.IO.Path.Combine(runDir, FileName);

                Type worldType = game.GetType(ManagedArmingProbe.WorldTypeName);
                Type playerType = game.GetType(ManagedArmingProbe.PlayerTypeName);
                if (worldType == null)
                {
                    _unresolvedReason = ManagedArmingProbe.WorldTypeName + " not found";
                    return false;
                }

                FieldInfo sharedField = worldType.GetField("Shared", Any);
                FieldInfo playerField = worldType.GetField("Player", Any);
                if (sharedField == null)
                {
                    _unresolvedReason = "World.Shared not found";
                    return false;
                }
                _getWorld = FieldAccessor.Static(sharedField);
                _getPlayer = playerField != null ? FieldAccessor.Reference(playerField) : null;

                // THE ENEMY ROWS MUST NOT DEPEND ON THE DODGE BEING ON, AND THEY DID.
                //
                // `EnemyReader` is resolved today by `DodgeAdapter.Resolve`, which is the only
                // module that has needed it -- and that method returns at its first line when
                // `DRELAY_DODGE` is unset. The world log runs after it, so a session that collected
                // map and portal data with the dodge turned off would have written a well-formed file
                // with no enemy rows at all, while `PLAN-AUTOFARM.md` section 6.2 item 4 asks the
                // operator where the enemies are densest. A reader that is already resolved is left
                // alone (the two calls above are the adapter's); otherwise this instrument resolves
                // it for itself, with the same two inputs the adapter hands over.
                if (!EnemyReader.Available)
                {
                    EnemyReader.ResolveWorld(sharedField);
                    EnemyReader.Resolve(game);
                }

                // THE MAP. `World.MapInfo` is a `GmMapInfo` (`World.cs:4486`), and every field this
                // log reads is declared on that packet type -- see `PLAN-AUTOFARM.md` section 6.1's
                // list. The type is taken FROM THE FIELD rather than by name, for the reason
                // `EnemyReader.Resolve` takes its element type from the method's return type: a
                // name that has to be right is a name that can be wrong.
                FieldInfo mapInfoField = worldType.GetField("MapInfo", Any);
                if (mapInfoField != null)
                {
                    Type mapType = mapInfoField.FieldType;
                    _getMapInfo = FieldAccessor.Reference(mapInfoField);
                    _mapName = String(mapType, "Name");
                    _mapAsset = String(mapType, "AssetName");
                    _mapWidth = Number(mapType, "Width");
                    _mapHeight = Number(mapType, "Height");
                    _mapPlayerId = Number(mapType, "PlayerId");
                    _mapDifficulty = Number(mapType, "Difficulty");
                    _mapSolo = Flag(mapType, "IsSoloRun");
                    _mapOpenedByKey = Flag(mapType, "OpenedByKey");
                    _mapCanTeleport = Flag(mapType, "CanTeleport");
                    _mapLevels = String(mapType, "Levels");
                }

                _worldDead = Flag(worldType, "PlayerDead");
                _worldSafe = Flag(worldType, "SafeWorld");
                _worldVault = Flag(worldType, "VaultWorld");
                _worldSpace = Flag(worldType, "Space");

                FieldInfo activatablesField = worldType.GetField("Activatables", Any);
                if (activatablesField != null)
                {
                    _getActivatables = FieldAccessor.Reference(activatablesField);
                    // THE ELEMENT TYPE IS NOT THE TYPE THAT CARRIES THE PORTAL'S OWN FIELDS, AND
                    // THE FIRST VERSION OF THIS GOT IT WRONG. `World.Activatables` is declared
                    // `List<ActivatableObject>` (`World.cs:4745`), so the generic argument is the
                    // BASE class -- and `Title`, `Tag`, `Called` and `locked` are declared on
                    // `Portal`. Resolving them on the element type found none of them: the log wrote
                    // `null` titles and `false` locks against a world whose portal was titled and
                    // locked, with nothing in the file to say a column was missing. The offline
                    // verifier caught it because it asserts the private `locked` field is READ, not
                    // that the row exists.
                    //
                    // SO THE SUBCLASS IS FOUND RATHER THAN NAMED. `PLAN-AUTOFARM.md` section 5.2
                    // says to "filter by the portal type", and the honest way to find that type
                    // without hard-coding a namespace is: the first type in the game assembly that
                    // derives from the collection's own element type and is called `Portal`. The
                    // name is a filter and not a pin -- a build where the class moved breaks the
                    // resolution and names it, which is the house rule for every accessor here.
                    Type elementType = activatablesField.FieldType.IsGenericType
                        ? activatablesField.FieldType.GetGenericArguments()[0]
                        : null;
                    _portalType = elementType != null ? FindPortalType(game, elementType) : null;
                    if (_portalType != null)
                    {
                        Type portalType = _portalType;
                        // See the note on the tree: `GameId` is on `GameObject` and `X`/`Y` on
                        // `Nodes.Node`, so both walks are hierarchy walks and a lookup restricted to
                        // `Portal` would find neither.
                        _portalGameId = FindIntByHierarchy(portalType, "GameId", "_gameId", "ObjectId");
                        _portalX = FindFloatByHierarchy(portalType, "X", "_x");
                        _portalY = FindFloatByHierarchy(portalType, "Y", "_y");
                        _portalTitle = Property(portalType, "Title");
                        _portalTag = Property(portalType, "Tag");
                        _portalCalled = FlagByHierarchy(portalType, "Called");
                        _portalLocked = FlagByHierarchy(portalType, "locked", "_locked", "Locked");
                    }
                }

                // THE GRID, FOUND STRUCTURALLY AND NOT BY NAME. `DodgeAdapter.ResolveGrid` explains
                // why: "the field has no stable name, so a structural property is the only honest
                // lookup" -- and this log only wants to know WHETHER it resolved and HOW BIG it is,
                // which is exactly what section 6.1 asks for and what the size guard of section 8.1
                // will need.
                FieldInfo[] worldFields = worldType.GetFields(Any);
                int byteGrids = 0;
                for (int i = 0; i < worldFields.Length; i++)
                {
                    if (worldFields[i].FieldType == typeof(byte[,]))
                    {
                        _getGridArray = FieldAccessor.Reference(worldFields[i]);
                        byteGrids++;
                    }
                }
                _gridResolved = byteGrids == 1;

                if (playerType != null)
                {
                    FieldInfo positionField = playerType.GetField("Position", Any);
                    FieldInfo px = positionField?.FieldType.GetField("X");
                    FieldInfo py = positionField?.FieldType.GetField("Y");
                    if (positionField != null && px != null && py != null)
                    {
                        _playerX = FieldAccessor.NestedFloat(positionField, px);
                        _playerY = FieldAccessor.NestedFloat(positionField, py);
                    }
                    _playerRadius = FindFloatByHierarchy(playerType, "Radius");
                    _playerHealth = FindDoubleByHierarchy(playerType, "Health");
                    _playerMaxHealth = FindDoubleByHierarchy(playerType, "MaxHealth");
                }

                int every;
                if (int.TryParse(Environment.GetEnvironmentVariable(EnvEvery),
                                 NumberStyles.Integer, Inv, out every) && every > 0)
                {
                    _everyFrames = every;
                }

                _writer = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write),
                                           new UTF8Encoding(false));

                // FLUSHED PER LINE, WHICH IS PERIODIC RATHER THAN PER FRAME.
                //
                // `Close` is never called: the replacement does not know when the operator closes the
                // window, and both `TraceRecorder.Close` and this one are dead code for that reason.
                // With a buffered writer the evidence would therefore end at whatever the last
                // buffer boundary was -- and, worse for the session this instrument exists for, the
                // file would not grow for the first several seconds, so the "did it record anything"
                // check would read as "header only" while the log was working. Lines are written
                // every `_everyFrames` frames (30 by default, about twice a second), so this is one
                // small append per half-second on a module that is off unless the operator asks.
                _writer.AutoFlush = true;

                WriteHeader();

                _resolved = true;
                _enabled = true;
                return true;
            }
            catch (Exception ex)
            {
                _unresolvedReason = ex.GetType().Name + ": " + ex.Message;
                _resolved = false;
                _enabled = false;
                return false;
            }
        }

        /// <summary>
        /// The first line: what this build could and could not resolve.
        ///
        /// IT IS THE MOST IMPORTANT LINE IN THE FILE, because the operator's session is the ONLY
        /// source of the data sections 5 and 8 are blocked on, and a session logged by a hook whose
        /// `MapInfo` accessor silently failed is a session that has to be run again. `missing` names
        /// every one of them.
        /// </summary>
        private static void WriteHeader()
        {
            Line.Length = 0;
            Line.Append("{\"t\":\"H\",\"v\":1,\"source\":\"PLAN-AUTOFARM.md section 6 (--world-log)\"")
                .Append(",\"every\":").Append(_everyFrames)
                .Append(",\"grid\":").Append(_gridResolved ? "true" : "false")
                .Append(",\"missing\":[");
            bool first = true;
            AppendMissing(ref first, "MapInfo", _getMapInfo);
            AppendMissing(ref first, "MapInfo.Name", _mapName);
            AppendMissing(ref first, "MapInfo.AssetName", _mapAsset);
            AppendMissing(ref first, "MapInfo.Width", _mapWidth);
            AppendMissing(ref first, "MapInfo.Height", _mapHeight);
            AppendMissing(ref first, "MapInfo.PlayerId", _mapPlayerId);
            AppendMissing(ref first, "MapInfo.Difficulty", _mapDifficulty);
            AppendMissing(ref first, "MapInfo.IsSoloRun", _mapSolo);
            AppendMissing(ref first, "MapInfo.OpenedByKey", _mapOpenedByKey);
            AppendMissing(ref first, "MapInfo.CanTeleport", _mapCanTeleport);
            AppendMissing(ref first, "MapInfo.Levels", _mapLevels);
            AppendMissing(ref first, "World.PlayerDead", _worldDead);
            AppendMissing(ref first, "World.SafeWorld", _worldSafe);
            AppendMissing(ref first, "World.VaultWorld", _worldVault);
            AppendMissing(ref first, "World.Space", _worldSpace);
            AppendMissing(ref first, "World.Activatables", _getActivatables);
            AppendMissing(ref first, "Portal.GameId", _portalGameId);
            AppendMissing(ref first, "Portal.Title", _portalTitle);
            AppendMissing(ref first, "Portal.Tag", _portalTag);
            AppendMissing(ref first, "Portal.Called", _portalCalled);
            AppendMissing(ref first, "Portal.locked", _portalLocked);
            AppendMissing(ref first, "Player.Position.X/Y", _playerX);
            AppendMissing(ref first, "Player.Radius", _playerRadius);
            AppendMissing(ref first, "Player.Health", _playerHealth);
            AppendMissing(ref first, "Player.MaxHealth", _playerMaxHealth);
            // THE ENEMY LIST IS `EnemyReader`'S AND ITS ABSENCE HAS ITS OWN NAME IN THE HOOK LOG
            // (`EnemyReader.Status`), so this row only says whether the log can ask it at all.
            AppendMissing(ref first, "World.GetEnemies()",
                          EnemyReader.Available ? typeof(WorldLog) : null);
            Line.Append("],\"note\":\"a name in `missing` is a column absent from every line below."
                        + " Enemies are sampled through EnemyReader, so their absence is named in the"
                        + " hook log rather than here.\"}");
            _writer.WriteLine(Line.ToString());
        }

        /// <summary>
        /// The `Portal` class, as the only type in the game assembly that derives from the element
        /// type of `World.Activatables` and is called `Portal`. See the resolve for why the element
        /// type itself is the wrong type to read.
        ///
        /// THE NAME IS A FILTER AND NOT A PIN. `World.Activatables` holds every
        /// `StaticObject`-derived `ActivatableObject` (`World.cs:14605`), so a chest and a portal are
        /// the same list; the operator's checklist is about PORTALS, and section 5.2 says to filter
        /// by the portal type. The name is how the filter is spelled, and a build where the class
        /// moved leaves this null -- which the header then reports by name rather than logging blank
        /// titles for a whole session.
        /// </summary>
        private static Type FindPortalType(Assembly game, Type elementType)
        {
            try
            {
                Type[] types = game.GetTypes();
                for (int i = 0; i < types.Length; i++)
                {
                    if (types[i].Name == "Portal" && elementType.IsAssignableFrom(types[i]))
                    {
                        return types[i];
                    }
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                // A partial type list is still worth searching: `Types` carries the ones that loaded.
                Type[] loaded = ex.Types;
                if (loaded != null)
                {
                    for (int i = 0; i < loaded.Length; i++)
                    {
                        if (loaded[i] != null && loaded[i].Name == "Portal"
                            && elementType.IsAssignableFrom(loaded[i]))
                        {
                            return loaded[i];
                        }
                    }
                }
            }
            catch (Exception)
            {
                // fall through to null
            }
            return null;
        }

        private static void AppendMissing(ref bool first, string name, object accessor)
        {
            if (accessor != null)
            {
                return;
            }
            if (!first)
            {
                Line.Append(',');
            }
            first = false;
            Line.Append('"').Append(name).Append('"');
        }

        // ===================================================================================
        // The frame
        // ===================================================================================

        /// <summary>
        /// One frame. Writes a periodic line, and a transition line whenever the world changed.
        ///
        /// IT IS CALLED AFTER THE DECISION AND TOUCHES NOTHING THE DECISION READS. See the header:
        /// this instrument is evidence, and the whole of its contract is that switching it on cannot
        /// change a dodge.
        /// </summary>
        public static void OnFrame(int frame, float clockMs)
        {
            if (!_enabled || _writer == null)
            {
                return;
            }

            try
            {
                _framesSeen++;
                object world = _getWorld();
                object player = world != null && _getPlayer != null ? _getPlayer(world) : null;
                if (world == null)
                {
                    return;
                }

                object mapInfo = _getMapInfo != null ? _getMapInfo(world) : null;
                string mapName = mapInfo != null && _mapName != null ? _mapName(mapInfo) : null;
                bool safe = _worldSafe != null && _worldSafe(world);
                bool vault = _worldVault != null && _worldVault(world);

                int gridW = 0;
                int gridH = 0;
                object grid = _getGridArray != null ? _getGridArray(world) : null;
                Array gridArray = grid as Array;
                if (gridArray != null)
                {
                    gridW = gridArray.GetLength(0);
                    gridH = gridArray.GetLength(1);
                }

                // THE FOUR SIGNALS, COMPUTED EVERY FRAME AND ACTED ON WHEN ANY OF THEM MOVES. See
                // the class comment: this is `PLAN-AUTOFARM.md` section 5.1's measurement, and the
                // point of recording all four is that the file says WHICH fired rather than that
                // "something" did.
                bool byReference = !ReferenceEquals(mapInfo, _lastMapInfo);
                bool byName = !string.Equals(mapName, _lastMapName, StringComparison.Ordinal);
                bool bySafe = safe != _lastSafe;
                bool byGrid = gridW != _lastGridWidth || gridH != _lastGridHeight;
                bool transition = _haveBaseline
                                  && (byReference || byName || bySafe || byGrid
                                      || vault != _lastVault);

                // The player's own speed, MEASURED here rather than read: the adapter's estimate is
                // its own and is not exposed, and two subtractions per frame is the whole cost. It
                // is the same quantity `DodgeAdapter.UpdateSpeed` measures and by the same argument
                // -- the displacement the client produced is the truth, and the speed law is a
                // model of it.
                float px = 0.0f;
                float py = 0.0f;
                bool havePlayer = player != null && _playerX != null && _playerY != null;
                if (havePlayer)
                {
                    px = _playerX(player);
                    py = _playerY(player);
                    if (!float.IsNaN(_lastX) && clockMs > _lastClockMs)
                    {
                        float dx = px - _lastX;
                        float dy = py - _lastY;
                        float seconds = (clockMs - _lastClockMs) / 1000.0f;
                        if (seconds > 0.0f)
                        {
                            _playerSpeed = (float)Math.Sqrt((dx * dx) + (dy * dy)) / seconds;
                        }
                    }
                    _lastX = px;
                    _lastY = py;
                    _lastClockMs = clockMs;
                }

                if (transition)
                {
                    _transitions++;
                    _lastTransitionFrame = frame;
                    _lastTransitionSignals = Signals(byReference, byName, bySafe, byGrid,
                                                     vault != _lastVault);
                    WriteTransition(frame, clockMs, mapInfo, mapName, safe, vault,
                                    gridW, gridH, byReference, byName, bySafe, byGrid);
                }

                _lastMapInfo = mapInfo;
                _lastMapName = mapName;
                _lastSafe = safe;
                _lastVault = vault;
                _lastGridWidth = gridW;
                _lastGridHeight = gridH;
                _haveBaseline = true;

                if (--_sinceLastLine > 0)
                {
                    return;
                }
                _sinceLastLine = _everyFrames;

                WriteFrame(frame, clockMs, world, player, mapInfo, mapName, safe, vault,
                           gridW, gridH, px, py, havePlayer);
                _linesWritten++;
            }
            catch (Exception)
            {
                // A FRAME THE LOG CANNOT WRITE IS A LOST LINE AND NOT A LOST RUN, which is the same
                // rule the trace recorder follows. `Close` reports the counters, so a log that
                // stopped half way through is visible rather than silent.
            }
        }

        private static string Signals(bool byReference, bool byName, bool bySafe, bool byGrid,
                                      bool byVault)
        {
            StringBuilder text = new StringBuilder(48);
            if (byReference) { text.Append("mapInfoRef "); }
            if (byName) { text.Append("mapName "); }
            if (bySafe) { text.Append("safeWorld "); }
            if (byVault) { text.Append("vaultWorld "); }
            if (byGrid) { text.Append("gridSize"); }
            return text.Length == 0 ? "(none)" : text.ToString().TrimEnd();
        }

        private static void WriteTransition(int frame, float clockMs, object mapInfo, string mapName,
                                            bool safe, bool vault, int gridW, int gridH,
                                            bool byReference, bool byName, bool bySafe, bool byGrid)
        {
            Line.Length = 0;
            Line.Append("{\"t\":\"T\",\"f\":").Append(frame)
                .Append(",\"clock\":").Append(Num(clockMs))
                .Append(",\"signals\":").Append(Json(_lastTransitionSignals))
                .Append(",\"mapInfoRef\":").Append(FlagBit(byReference))
                .Append(",\"mapName\":").Append(FlagBit(byName))
                .Append(",\"safeWorld\":").Append(FlagBit(bySafe))
                .Append(",\"grid\":").Append(FlagBit(byGrid))
                .Append(",\"to\":{\"name\":").Append(Json(mapName))
                .Append(",\"safe\":").Append(FlagBit(safe))
                .Append(",\"vault\":").Append(FlagBit(vault))
                .Append(",\"gw\":").Append(gridW)
                .Append(",\"gh\":").Append(gridH)
                .Append('}')
                .Append('}');
            _writer.WriteLine(Line.ToString());
            _linesWritten++;
        }

        private static void WriteFrame(int frame, float clockMs, object world, object player,
                                       object mapInfo, string mapName, bool safe, bool vault,
                                       int gridW, int gridH, float px, float py, bool havePlayer)
        {
            Line.Length = 0;
            Line.Append("{\"t\":\"F\",\"f\":").Append(frame)
                .Append(",\"clock\":").Append(Num(clockMs));

            // --- map -------------------------------------------------------------------------
            //
            // THE MAP BLOCK IS THE REASON THIS INSTRUMENT EXISTS. `PLAN-AUTOFARM.md` section 5.1
            // makes world identity the pivot of the farmer's state machine and section 8.2 makes
            // `MapInfo.Name`/`AssetName` the primary dungeon key, and neither can be designed until
            // the operator's session says what those strings actually are and whether they are
            // stable. So every field is written raw.
            Line.Append(",\"map\":{");
            Line.Append("\"name\":").Append(Json(mapName));
            Line.Append(",\"asset\":").Append(Json(Call(_mapAsset, mapInfo)));
            Line.Append(",\"w\":").Append(Int(CallInt(_mapWidth, mapInfo)));
            Line.Append(",\"h\":").Append(Int(CallInt(_mapHeight, mapInfo)));
            Line.Append(",\"pid\":").Append(Int(CallInt(_mapPlayerId, mapInfo)));
            Line.Append(",\"diff\":").Append(Int(CallInt(_mapDifficulty, mapInfo)));
            Line.Append(",\"solo\":").Append(FlagBit(CallBool(_mapSolo, mapInfo)));
            Line.Append(",\"key\":").Append(FlagBit(CallBool(_mapOpenedByKey, mapInfo)));
            Line.Append(",\"tp\":").Append(FlagBit(CallBool(_mapCanTeleport, mapInfo)));
            Line.Append(",\"levels\":").Append(Json(Call(_mapLevels, mapInfo)));
            Line.Append('}');

            // --- world -----------------------------------------------------------------------
            Line.Append(",\"world\":{");
            Line.Append("\"safe\":").Append(FlagBit(safe));
            Line.Append(",\"vault\":").Append(FlagBit(vault));
            Line.Append(",\"space\":").Append(FlagBit(CallBool(_worldSpace, world)));
            Line.Append(",\"dead\":").Append(FlagBit(CallBool(_worldDead, world)));
            Line.Append(",\"entries\":").Append(_transitions);
            Line.Append('}');

            // --- player ----------------------------------------------------------------------
            Line.Append(",\"player\":{");
            Line.Append("\"x\":").Append(Num(px));
            Line.Append(",\"y\":").Append(Num(py));
            Line.Append(",\"r\":").Append(Num(havePlayer ? CallFloat(_playerRadius, player) : 0.0f));
            Line.Append(",\"hp\":").Append(Num(havePlayer ? CallDouble(_playerHealth, player) : 0.0f));
            Line.Append(",\"maxhp\":").Append(Num(havePlayer
                ? CallDouble(_playerMaxHealth, player) : 0.0f));
            Line.Append(",\"spd\":").Append(Num(_playerSpeed));
            Line.Append('}');

            // --- portals ---------------------------------------------------------------------
            //
            // THE OPERATOR'S SESSION IS WHY THIS LIST IS HERE AT ALL, and section 5.2 says exactly
            // what it buys: with no destination identifier client-side, the portal's own free-text
            // `Title`/`Tag` is the only candidate for an entry gate that does not require entering
            // first. Which of them is stable is the measurement.
            Line.Append(",\"portals\":[");
            System.Collections.IEnumerable actives = null;
            if (_getActivatables != null)
            {
                actives = _getActivatables(world) as System.Collections.IEnumerable;
            }
            bool firstPortal = true;
            int otherActivatables = 0;
            if (actives != null)
            {
                foreach (object portal in actives)
                {
                    if (portal == null)
                    {
                        continue;
                    }
                    // THE FILTER OF SECTION 5.2, AND THE COUNT IS WHAT MAKES IT HONEST. A list that
                    // silently drops the chests would read as a lobby with nothing in it; `other` is
                    // the number of activatables that were not portals, so a session's file says
                    // both what was found and what was passed over.
                    if (_portalType != null && !_portalType.IsInstanceOfType(portal))
                    {
                        otherActivatables++;
                        continue;
                    }
                    if (!firstPortal) { Line.Append(','); }
                    firstPortal = false;
                    Line.Append("{\"id\":").Append(Int(CallInt(_portalGameId, portal)))
                        .Append(",\"x\":").Append(Num(CallFloat(_portalX, portal)))
                        .Append(",\"y\":").Append(Num(CallFloat(_portalY, portal)))
                        .Append(",\"title\":").Append(Json(Call(_portalTitle, portal)))
                        .Append(",\"tag\":").Append(Json(Call(_portalTag, portal)))
                        .Append(",\"locked\":").Append(FlagBit(CallBool(_portalLocked, portal)))
                        .Append(",\"called\":").Append(FlagBit(CallBool(_portalCalled, portal)))
                        .Append('}');
                }
            }
            Line.Append("],\"portalsOther\":").Append(otherActivatables);

            // --- enemies ---------------------------------------------------------------------
            //
            // THE EIGHT NEAREST AND A COUNT, NOT THE WHOLE LIST. Section 6.1's words: "the 8 nearest
            // is enough to name a boss later", and a realm's whole population would make the file
            // large for no question it can answer. The sampler is `EnemyReader`'s, so the log and
            // rule 7 read the same enumeration and cannot disagree about what "the enemy list" is.
            int enemyTotal;
            EnemyReader.SampleEnemies(Enemies, NearEnemies, px, py, out enemyTotal);
            Line.Append(",\"enemies\":{\"n\":").Append(enemyTotal).Append(",\"near\":[");
            for (int i = 0; i < Enemies.Count; i++)
            {
                if (i > 0) { Line.Append(','); }
                EnemyReader.EnemySample e = Enemies[i];
                Line.Append("{\"id\":").Append(e.GameId)
                    .Append(",\"x\":").Append(Num(e.X))
                    .Append(",\"y\":").Append(Num(e.Y))
                    .Append(",\"hp\":").Append(Num(e.Health))
                    .Append(",\"maxhp\":").Append(Num(e.MaxHealth))
                    .Append(",\"npc\":").Append(FlagBit(e.Npc))
                    .Append(",\"dead\":").Append(FlagBit(e.Dead))
                    .Append('}');
            }
            Line.Append("]}");

            // --- grid ------------------------------------------------------------------------
            //
            // "WHETHER THE COLLISION byte[,] RESOLVED, ITS GetLength(0), GetLength(1)" -- and
            // nothing else. Section 6.1 refuses to decode the map here for the reason it gives: the
            // grid's CONTENTS are section 7's full-grid mirror and its size guard, and building that
            // now would be building the wrong thing at the wrong time.
            Line.Append(",\"grid\":{\"ok\":").Append(_gridResolved ? "true" : "false")
                .Append(",\"gw\":").Append(gridW)
                .Append(",\"gh\":").Append(gridH)
                .Append("}}");

            _writer.WriteLine(Line.ToString());
        }

        // ===================================================================================
        // Small readers and writers
        // ===================================================================================

        private static string Call(Func<object, string> read, object target)
        {
            if (read == null || target == null) { return null; }
            try { return read(target); } catch (Exception) { return null; }
        }

        private static int CallInt(Func<object, int> read, object target)
        {
            if (read == null || target == null) { return 0; }
            try { return read(target); } catch (Exception) { return 0; }
        }

        private static float CallFloat(Func<object, float> read, object target)
        {
            if (read == null || target == null) { return 0.0f; }
            try { return read(target); } catch (Exception) { return 0.0f; }
        }

        private static double CallDouble(Func<object, double> read, object target)
        {
            if (read == null || target == null) { return 0.0; }
            try { return read(target); } catch (Exception) { return 0.0; }
        }

        private static bool CallBool(Func<object, bool> read, object target)
        {
            if (read == null || target == null) { return false; }
            try { return read(target); } catch (Exception) { return false; }
        }

        /// <summary>
        /// A JSON boolean, as the two literal words.
        ///
        /// IT IS NOT `1`/`0`, AND THE READER IS WHY: `worlds.jsonl` is parsed by whatever the
        /// operator has to hand -- a Python one-liner, `jq`, a notebook -- and `"safe": 1` is a
        /// number that a truthiness test accepts and a schema does not. The plan's own checklist
        /// (section 6.2) has labels written by a human next to machine rows, so the file has to be
        /// boring to read.
        /// </summary>
        private static string FlagBit(bool value)
        {
            return value ? "true" : "false";
        }

        private static string Int(int value)
        {
            return value.ToString(Inv);
        }

        private static string Num(float value)
        {
            return value.ToString("0.###", Inv);
        }

        private static string Num(double value)
        {
            return value.ToString("0.###", Inv);
        }

        /// <summary>
        /// A JSON string, ESCAPED, and this is the one place in this file where being careful is not
        /// optional. `TraceRecorder`'s hand-rolled writer was caught by an unescaped newline inside
        /// the settings string and it turned a healthy recording into a file every reader reported as
        /// CORRUPT. Portal titles and tags are USER-VISIBLE FREE TEXT -- they come off the wire -- so
        /// a quote in one of them is not hypothetical, and a bare quote ends the string and makes
        /// every following line unparseable.
        /// </summary>
        private static string Json(string value)
        {
            if (value == null)
            {
                return "null";
            }
            StringBuilder text = new StringBuilder(value.Length + 2);
            text.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            text.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        }
                        else
                        {
                            text.Append(c);
                        }
                        break;
                }
            }
            text.Append('"');
            return text.ToString();
        }

        // ===================================================================================
        // The accessor helpers, one per shape
        // ===================================================================================

        private static Func<object, string> String(Type owner, string name)
        {
            FieldInfo field = FieldLookup.Find(owner, out _, name);
            if (field != null && field.FieldType == typeof(string))
            {
                Func<object, object> read = FieldAccessor.Reference(field);
                return target => read(target) as string;
            }
            return Property(owner, name);
        }

        private static Func<object, int> Number(Type owner, string name)
        {
            FieldInfo field = FieldLookup.Find(owner, out _, name);
            return field == null ? null : FieldAccessor.Int(field);
        }

        private static Func<object, bool> Flag(Type owner, string name)
        {
            FieldInfo field = FieldLookup.Find(owner, out _, name);
            return field == null ? null : FieldAccessor.Bool(field);
        }

        private static Func<object, string> Property(Type owner, string name)
        {
            PropertyInfo property = owner.GetProperty(name, BindingFlags.Public
                                                            | BindingFlags.NonPublic
                                                            | BindingFlags.Instance);
            return property == null || property.PropertyType != typeof(string)
                ? null
                : (Func<object, string>)(target => property.GetValue(target, null) as string);
        }

        private static Func<object, bool> FlagByHierarchy(Type owner, params string[] names)
        {
            FieldInfo field = FieldLookup.Find(owner, out _, names);
            return field == null ? null : FieldAccessor.Bool(field);
        }

        private static Func<object, int> FindIntByHierarchy(Type owner, params string[] names)
        {
            FieldInfo field = FieldLookup.Find(owner, out _, names);
            return field == null ? null : FieldAccessor.Int(field);
        }

        private static Func<object, float> FindFloatByHierarchy(Type owner, params string[] names)
        {
            FieldInfo field = FieldLookup.Find(owner, out _, names);
            return field == null ? null : FieldAccessor.Float(field);
        }

        private static Func<object, double> FindDoubleByHierarchy(Type owner, params string[] names)
        {
            FieldInfo field = FieldLookup.Find(owner, out _, names);
            return field == null ? null : FieldAccessor.Double(field);
        }

        // ===================================================================================
        // The report
        // ===================================================================================

        /// <summary>One line for the hook log, so a run says whether the collection file exists.</summary>
        public static string Describe()
        {
            if (!_enabled)
            {
                return "world log: off (" + _unresolvedReason + ")";
            }
            return "world log: " + _linesWritten + " line(s), " + _transitions + " world change(s)"
                   + (_transitions > 0
                       ? ", last at frame " + _lastTransitionFrame + " on " + _lastTransitionSignals
                       : "")
                   + ", " + _framesSeen + " frame(s) seen -> " + _path;
        }

        /// <summary>Flushes and closes. Called where the other recorders are closed.</summary>
        public static void Close()
        {
            if (_writer == null)
            {
                _enabled = false;
                return;
            }
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (Exception)
            {
                // nothing useful to do while the process is going down
            }
            _writer = null;
            _enabled = false;
        }
    }
}
