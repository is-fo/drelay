// QuestReader.cs -- the active quest markers, as the farmer's input, and the client's own
// `ActivatableObject.Activate(0)` as its output.
//
// =====================================================================================
// WHAT THE CLIENT ACTUALLY HAS, AND WHY THIS IS NOT A SECOND ENEMY LIST
// =====================================================================================
//
// The client tracks quests as OBJECTS, not as text:
//
//   * `World.Quest` -- the one quest the client's arrow is tracking (`World.cs:4506`), set from
//     `GmQuest.ObjectId` when the packet's `Event` flag is false;
//   * `World.EventQuests` -- every active event marker (`World.cs:4508`), appended from the same
//     packet when `Event` is true.
//
// Both are `GameObject` references, and the client's own minimap draws each at `Position` and
// hides the ordinary enemy sprite behind a quest arrow (`Minimap.cs:816`, `:981`). The minimap
// container's centre is `World.Quest.Position` when there is one (`MinimapContainer.cs:217-219`).
// So "walk to the quest" is a POINT, exactly like the farmer's enemy target -- it is not a
// rectangle, a zone, or a second copy of `World.GetEnemies()`.
//
// The object behind a marker can be an enemy, an NPC, a shop, a quest board or a static quest
// object. Nothing in the packet says which: `GmQuest` carries only `ObjectId` and `Event`. The
// type of the LIVE object is the only discriminator, and the useful split is
// `ActivatableObject`: those are entered with `Activate(0)` (the client's own input path), while
// an enemy is walked at and fought by the existing auto-fire. This reader exposes both facts and
// takes no decision about which one to walk to.
//
// =====================================================================================
// IT IS A READER, NOT A POLICY
// =====================================================================================
//
// Like `EnemyReader` and `PortalReader`, this file finds the accessors, samples the frame's
// markers into the snapshot and says what it could not resolve. WHICH marker to walk to, when to
// walk and when to activate are the private farmer module's decisions
// (`PLAN-AUTOFARM.md` section 13), so this file contains no policy and publishes no goal.
//
// =====================================================================================
// THE TWO CALLS THAT MATTER
// =====================================================================================
//
// `ActivatableObject.Activate(int)` is the virtual method `ObjectActivator` calls when the player
// stands on an activatable and presses interact. Calling it on a quest NPC/shop/board runs the
// client's own gates instead of reimplementing them -- the same doctrine `PortalReader` follows
// for `Portal.Activate(0)` and `World.RequestEscape` follows for the nexus. It is resolved on
// `ActivatableObject`, not on `Portal`, because the quest marker may be any of the client's forty
// activatable subclasses.
//
// IT IS WARMED AT ARM TIME. `MethodInfo.Invoke` on a method the JIT has not seen compiles it,
// and that compilation would otherwise happen inside a patched frame -- the argument
// `PortalReader` and `DodgeAdapter.Prepare` both make.

using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Drelay.Bootstrap
{
    public static class QuestReader
    {
        private const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        // --- Resolved once ------------------------------------------------------------------

        private static bool _resolved;
        private static string _unresolvedReason = "not attempted";
        private static string _status = "not attempted";

        private static Func<object> _getWorld;
        private static Func<object, object> _getQuest;
        private static Func<object, object> _getEventQuests;

        /// <summary>
        /// `World.Objects` -- the dictionary of the LIVE objects in this world, keyed by `GameId`.
        ///
        /// IT IS THE STALENESS TEST, AND THE CLIENT'S OWN `ClearWorld` IS WHY IT IS NEEDED.
        /// `World.ClearWorld()` clears `Objects` and `EventQuests` but NOT `World.Quest` (verified in
        /// the 2026-10-19 dump: `Objects.Clear()`, `EventQuests.Clear()`, `GameUI.SetQuest(null)` and
        /// no `Quest = null`). A tracked quest therefore SURVIVES a map change as a reference to the
        /// previous map's object: its `Position` is a point in the map the character just left, and
        /// the farmer walked to it -- 165 frames of a live session walking at a wall in the Tavern
        /// toward a marker at (343.6, 1364.1), which was in the realm it had left. `World.Quest` is
        /// only ever assigned from a key of this dictionary (`World.hehe...EF9D`), so membership is
        /// exactly "this marker is in this world".
        /// </summary>
        private static Func<object, object> _getObjects;

        private static Func<object, float> _getX;
        private static Func<object, float> _getY;

        /// <summary>
        /// `Node.Position` IS THE PREFERRED POSITION FOR A MARKER, and `ExactPosition` is only the
        /// fallback. Every other reader in this hook -- the player, `EnemyReader`, `PortalReader`,
        /// and the collision grid itself -- is in the same unrotated map frame, and the client's
        /// own minimap centres on `World.Quest.Position`. `ExactPosition` is composed through the
        /// world's rotated base node (`BaseNode.Rotation = WorldRotation`), so it is a SCREEN-space
        /// point and is the wrong frame whenever the camera has been turned. See `AddQuest`.
        /// </summary>
        private static Func<object, float> _getExactX;
        private static Func<object, float> _getExactY;

        private static Func<object, int> _getGameId;
        private static Func<object, object> _getData;
        private static Func<object, object> _getName;
        private static Func<object, bool> _getDead;
        private static Type _entityType;
        private static Type _activatableType;
        private static MethodInfo _activate;

        // --- The DATA FLAGS, which are the second and independent source of a quest object ------
        //
        // THE OPERATOR'S 22:36 SESSION IS WHY THIS EXISTS. It walked through the overworld and the
        // Infested Cellar, the client drew a quest indicator for `Athena, Queen of Arachnids`, and
        // `PHASE 3 DODGE PILOT` reported `quest=0/0 qsrc=none` for the whole run -- `World.Quest`
        // was null and `World.EventQuests` empty, so the farmer never had a marker to walk to and
        // spent the session on its roam waypoints. The client marks a quest object in TWO places:
        // the tracked reference (`World.Quest` / `EventQuests`, what the minimap and the arrow
        // read), and the OBJECT'S OWN DATA: `EntityData.Quest` / `EntityData.EventQuest`, which is
        // what the UI tests for a boss before it keeps the arrow alive
        // (`DesktopMMOUI.SetQuest`: `Boss?.Data is EntityData d && d.EventQuest`). A dungeon boss is
        // announced by the boss-health-bar packet and its data carries the flag; the tracked
        // reference is not guaranteed to be set for it.
        //
        // SO THE REGISTRY IS SCANNED TOO. `World.Objects` is every live object in this world, keyed
        // by `GameId`, and a data flag on one of them IS the client saying "this is a quest". The
        // scan runs every `QuestScanFrames` frames and its result is cached, because it costs a
        // dictionary walk and six delegate calls per object while a frame costs microseconds.
        // ONE ENTRY PER DATA CLASS THAT DECLARES THE FLAG, resolved at arm time by a walk over the
        // assembly's types -- NOT by looking for `Quest` on `GameObject.Data`'s STATIC type.
        // `GameObject.Data` is declared `GameObjectData`, while the instance is `EnemyData` /
        // `CharacterData` / ..., and `Quest`/`EventQuest` are declared on `EntityData` BELOW it. A
        // hierarchy walk from the field's type therefore finds nothing, which is the trap this
        // shape exists to avoid; the scan tests each entry's declaring type against the runtime
        // object and reads the flag from the one that matches.
        private static Type[] _questFlagTypes;
        private static Func<object, bool>[] _questFlagGetters;
        private static Type[] _eventFlagTypes;
        private static Func<object, bool>[] _eventFlagGetters;
        private static bool _flagsResolved;

        /// <summary>`World.Player`'s type, so the registry scan can skip the player itself.</summary>
        private static Type _playerType;

        /// <summary>Frames between registry scans. Six a second: a boss's marker is worth half a second of staleness, a frame walk is not.</summary>
        private const int QuestScanFrames = 10;

        private static long _scanClock;
        private static readonly System.Collections.Generic.List<QuestTarget> _scanned =
            new System.Collections.Generic.List<QuestTarget>();
        private static long _dataMarkers;
        private static long _scanObjects;
        private static bool _scanRan;

        // --- Per-run counters ---------------------------------------------------------------

        private static long _markersSeen;
        private static long _markersSkipped;
        private static long _markersStale;
        private static long _activationsAttempted;
        private static long _activationsFailed;

        /// <summary>Where the reader stands, for the arm-time log. One line, under 256 bytes.</summary>
        public static string Status()
        {
            return _status;
        }

        /// <summary>Why the reader is not available; null when it is.</summary>
        public static string UnresolvedReason
        {
            get { return _resolved ? null : _unresolvedReason; }
        }

        /// <summary>True when the reader resolved and the quest walker may ask for markers.</summary>
        public static bool Available => _resolved;

        /// <summary>Markers this run put into the snapshot.</summary>
        public static long MarkersSeen => _markersSeen;

        /// <summary>
        /// Markers refused because their position was the origin, non-finite, or otherwise not a
        /// place. A non-zero count beside `MarkersSeen == 0` is the signature of a resolved
        /// `World.Quest` whose position the client has not populated yet -- the "walks to the top
        /// left" defect -- and it is counted rather than logged per frame.
        /// </summary>
        public static long MarkersSkipped => _markersSkipped;

        /// <summary>
        /// Markers refused because they are NOT OBJECTS IN THIS WORLD: a `World.Quest` that survived
        /// a map change and still points at the map the character left. Counted apart from
        /// <see cref="MarkersSkipped"/> because the two are different diagnoses -- an unplaced marker
        /// and a marker from another realm -- and the second one is measured, not guessed: the
        /// 2026-10-19 session that walked 165 frames at a Tavern wall was walking at (343.6, 1364.1)
        /// with `World.Objects` holding nothing by that id. See `_getObjects`.
        /// </summary>
        public static long MarkersStale => _markersStale;

        /// <summary>Activations attempted and how many the client's call threw or refused.</summary>
        public static long ActivationsAttempted => _activationsAttempted;
        public static long ActivationsFailed => _activationsFailed;

        /// <summary>
        /// Markers found by the registry scan (`Data.Quest` / `Data.EventQuest`), and how many objects
        /// the last scan walked. Non-zero `DataMarkers` with a zero `MarkersSeen` is the "the reader
        /// sees it and the farmer does not" case; a zero `DataMarkers` over a non-zero
        /// `ScannedObjects` is "the client's data does not flag anything here".
        /// </summary>
        public static long DataMarkers => _dataMarkers;

        /// <summary>Objects the last registry scan walked, 0 before it ran.</summary>
        public static long ScannedObjects => _scanObjects;

        /// <summary>True once the registry scan has run at least once.</summary>
        public static bool Scanned => _scanRan;

        /// <summary>
        /// THE RAW STATE, FOR THE REPORT -- written so "did it see a quest?" is answered by the log
        /// and not by inference. Every field is a value the reader itself used: the tracked
        /// reference, the event list, and the registry scan's own count. It is formatted once per
        /// report, so it may format names.
        /// </summary>
        public static string DescribeLive()
        {
            if (!_resolved)
            {
                return "not resolved: " + _unresolvedReason;
            }
            object world = _getWorld != null ? _getWorld() : null;
            if (world == null)
            {
                return "no World.Shared";
            }
            object primary = _getQuest != null ? _getQuest(world) : null;
            object raw = _getEventQuests != null ? _getEventQuests(world) : null;
            IList events = raw as IList;
            return "Quest=" + Describe(primary)
                   + " | events=" + (events != null ? events.Count : -1)
                   + " | scan=" + (_scanRan ? _scanObjects + " obj, " + _scanned.Count + " flagged" : "not run")
                   + " | seen=" + _markersSeen + " skip=" + _markersSkipped + " stale=" + _markersStale;
        }

        /// <summary>One object as the log can carry it: id, name and place, or `none`.</summary>
        private static string Describe(object item)
        {
            if (item == null)
            {
                return "none";
            }
            try
            {
                int id = _getGameId != null ? _getGameId(item) : 0;
                string name = null;
                if (_getData != null && _getName != null)
                {
                    object data = _getData(item);
                    if (data != null)
                    {
                        name = _getName(data) as string;
                    }
                }
                float x;
                float y;
                bool placed = TryReadPlace(item, out x, out y);
                return "id=" + id + " name='" + (name ?? "") + "' at "
                       + (placed ? x.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                                 + "," + y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                                 : "none");
            }
            catch (Exception ex)
            {
                return "unreadable (" + ex.GetType().Name + ")";
            }
        }

        /// <summary>Zeroes the per-run counters.</summary>
        public static void ResetCounters()
        {
            _markersSeen = 0;
            _markersSkipped = 0;
            _markersStale = 0;
            _dataMarkers = 0;
            _scanObjects = 0;
            _scanRan = false;
            _scanned.Clear();
            _activationsAttempted = 0;
            _activationsFailed = 0;
        }

        // ===================================================================================
        // Resolve
        // ===================================================================================

        /// <summary>
        /// Finds `World.Quest`, `World.EventQuests`, a marker's `GameId`/`Position`/`Data.Name`,
        /// `Entity.Dead` and `ActivatableObject.Activate(int)`.
        ///
        /// EVERY LOOKUP IS THE ONE THE CLIENT'S OWN UI MAKES. `Quest` and `EventQuests` are public
        /// FIELDS on `World`; `Position` is `Node.Position` (a `Vector3`, the same layout
        /// `PortalReader` resolves); `Data.Name` is a public field on `GameObjectData`. The one
        /// non-obvious piece is `ActivatableObject`: it is found through `World.Activatables`'
        /// element type, because that is the assembly's own spelling and a name scan over ~1900
        /// types is what `PortalReader` already pays for -- this reader does not pay it again.
        /// </summary>
        public static bool Resolve(Assembly game, string worldTypeName)
        {
            try
            {
                if (game == null)
                {
                    _unresolvedReason = "the game assembly is not loaded";
                    _status = "quests: NOT available -- " + _unresolvedReason;
                    return false;
                }
                Type worldType = game.GetType(worldTypeName);
                if (worldType == null)
                {
                    _unresolvedReason = worldTypeName + " not found";
                    _status = "quests: NOT available -- " + _unresolvedReason;
                    return false;
                }

                FieldInfo questField = worldType.GetField("Quest", Any);
                FieldInfo eventsField = worldType.GetField("EventQuests", Any);
                if (questField == null && eventsField == null)
                {
                    _unresolvedReason = "World.Quest and World.EventQuests not found";
                    _status = "quests: NOT available -- " + _unresolvedReason;
                    return false;
                }

                Type markerType = questField != null
                    ? questField.FieldType
                    : (eventsField.FieldType.IsGenericType
                        ? eventsField.FieldType.GetGenericArguments()[0]
                        : null);
                if (markerType == null)
                {
                    _unresolvedReason = "World.EventQuests is not a list of GameObjects";
                    _status = "quests: NOT available -- " + _unresolvedReason;
                    return false;
                }

                _getQuest = questField != null ? FieldAccessor.Reference(questField) : null;
                _getEventQuests = eventsField != null ? FieldAccessor.Reference(eventsField) : null;

                // THE WORLD'S OWN OBJECT REGISTRY. Best effort: if it does not resolve, the staleness
                // guard cannot run and the reader says so in `Status()` -- it does NOT refuse every
                // marker, which would turn a missing field into a farmer that never quests.
                FieldInfo objectsField = FieldLookup.Find(worldType, out _, "Objects");
                if (objectsField != null && typeof(IDictionary).IsAssignableFrom(objectsField.FieldType))
                {
                    _getObjects = FieldAccessor.Reference(objectsField);
                }
                FieldInfo playerField = FieldLookup.Find(worldType, out _, "Player");
                _playerType = playerField != null ? playerField.FieldType : null;

                // THE POSITION IS A VECTOR ON A BASE TYPE, not a float X on the marker. `Position`
                // is the map-frame point every other reader uses and is sampled first;
                // `ExactPosition` is the rotated-screen-space fallback. See `AddQuest` for the
                // coordinate-frame bug that made the order load-bearing.
                _getX = VectorComponent(markerType, "X", "Position");
                _getY = VectorComponent(markerType, "Y", "Position");
                _getExactX = VectorComponent(markerType, "X", "ExactPosition");
                _getExactY = VectorComponent(markerType, "Y", "ExactPosition");
                bool hasPosition = _getX != null && _getY != null;
                bool hasExact = _getExactX != null && _getExactY != null;
                if (!hasPosition && !hasExact)
                {
                    _unresolvedReason = "a quest marker's Position did not resolve";
                    _status = "quests: NOT available -- " + _unresolvedReason;
                    return false;
                }

                // THE IDENTITY IS OPTIONAL ONLY IN THE SENSE THAT A MISSING ONE STILL WALKS: the
                // farmer holds a marker across frames. A marker without an id is re-chosen every
                // frame, which is fine for a single tracked quest and is reported by name.
                FieldInfo gameIdField = FieldLookup.Find(markerType, out _, "GameId", "_gameId", "ObjectId");
                _getGameId = gameIdField != null ? FieldAccessor.Int(gameIdField) : null;

                FieldInfo dataField = FieldLookup.Find(markerType, out _, "Data");
                if (dataField != null)
                {
                    _getData = FieldAccessor.Reference(dataField);
                    FieldInfo nameField = FieldLookup.Find(dataField.FieldType, out _, "Name");
                    _getName = nameField != null ? FieldAccessor.Reference(nameField) : null;
                }

                // `Dead` IS ON `Entity`, NOT ON `GameObject`, so it is resolved through the type
                // `World.GetEnemies()` already returns and applied only to instances of that type.
                // A non-entity marker (an NPC is an Entity; a board is not) reads false.
                MethodInfo getEnemies = worldType.GetMethod(
                    "GetEnemies", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                Type enemyType = getEnemies != null ? getEnemies.ReturnType.GetElementType() : null;
                FieldInfo deadField = enemyType != null ? FieldLookup.Find(enemyType, out _, "Dead") : null;
                if (deadField != null)
                {
                    _entityType = deadField.DeclaringType;
                    _getDead = FieldAccessor.Bool(deadField);
                }

                // THE DATA FLAGS, resolved at ARM TIME over every data class in the assembly that
                // declares one. See the field block: the flags are declared on `EntityData`, which is
                // BELOW `GameObject.Data`'s static type, so a hierarchy walk from the field finds
                // nothing. A type walk finds exactly the declaring classes, and their accessors are
                // emitted here rather than on the first frame that meets one.
                if (_getData != null)
                {
                    Type dataRoot = FieldLookup.Find(markerType, out _, "Data") != null
                        ? FieldLookup.Find(markerType, out _, "Data").FieldType
                        : null;
                    if (dataRoot != null)
                    {
                        var questTypes = new System.Collections.Generic.List<Type>();
                        var questGetters = new System.Collections.Generic.List<Func<object, bool>>();
                        var eventTypes = new System.Collections.Generic.List<Type>();
                        var eventGetters = new System.Collections.Generic.List<Func<object, bool>>();
                        foreach (Type t in game.GetTypes())
                        {
                            if (t == null || t.IsAbstract || !dataRoot.IsAssignableFrom(t))
                            {
                                continue;
                            }
                            FieldInfo questFlag = t.GetField("Quest", Any | BindingFlags.DeclaredOnly);
                            if (questFlag != null && questFlag.FieldType == typeof(bool))
                            {
                                questTypes.Add(t);
                                questGetters.Add(FieldAccessor.Bool(questFlag));
                            }
                            FieldInfo eventFlag = t.GetField("EventQuest", Any | BindingFlags.DeclaredOnly);
                            if (eventFlag != null && eventFlag.FieldType == typeof(bool))
                            {
                                eventTypes.Add(t);
                                eventGetters.Add(FieldAccessor.Bool(eventFlag));
                            }
                        }
                        _questFlagTypes = questTypes.ToArray();
                        _questFlagGetters = questGetters.ToArray();
                        _eventFlagTypes = eventTypes.ToArray();
                        _eventFlagGetters = eventGetters.ToArray();
                        _flagsResolved = _questFlagTypes.Length > 0 || _eventFlagTypes.Length > 0;
                    }
                }

                // THE ENTRY CALL. `World.Activatables` is a `List<ActivatableObject>`; its element
                // type is the one that declares the virtual `Activate(int)` every portal/NPC/board
                // overrides. A marker that is not an instance of it is an enemy and is fought.
                FieldInfo activatablesField = worldType.GetField("Activatables", Any);
                _activatableType = activatablesField != null && activatablesField.FieldType.IsGenericType
                    ? activatablesField.FieldType.GetGenericArguments()[0]
                    : null;
                if (_activatableType != null)
                {
                    _activate = _activatableType.GetMethod(
                        "Activate", BindingFlags.Public | BindingFlags.Instance, null,
                        new[] { typeof(int) }, null);
                    if (_activate != null)
                    {
                        // WARM IT HERE, NOT INSIDE A PATCHED FRAME. See the file header.
                        try { RuntimeHelpers.PrepareMethod(_activate.MethodHandle); }
                        catch (Exception) { /* a method with a generic context refuses; leave it cold */ }
                    }
                }

                _resolved = true;
                _status = "quests: World.Quest"
                          + (_getQuest != null ? "" : " (missing)")
                          + " + EventQuests"
                          + (_getEventQuests != null ? "" : " (missing)")
                          + " | x/y " + (_getX != null ? "Position" : "no Position")
                          + (_getExactX != null ? " (fallback ExactPosition)" : "")
                          + " | id " + (_getGameId != null ? "GameId" : "MISSING")
                          + " | name " + (_getName != null ? "Data.Name" : "MISSING")
                          + " | world " + (_getObjects != null
                              ? "Objects (stale markers refused)"
                              : "MISSING (a marker from another map will be walked to)")
                          + " | flags " + (_flagsResolved
                              ? "Data.Quest/EventQuest (registry scanned)"
                              : "MISSING (only the tracked quest and event list are read)")
                          + " | enter " + (_activate != null ? "Activate(0)" : "MISSING (walk only)");
                return _resolved;
            }
            catch (Exception ex)
            {
                _unresolvedReason = ex.GetType().Name + ": " + ex.Message;
                _status = "quests: NOT available -- " + _unresolvedReason;
                _resolved = false;
                return false;
            }
        }

        /// <summary>`World.Shared`, resolved by the caller that already has the field.</summary>
        public static void ResolveWorld(FieldInfo sharedField)
        {
            _getWorld = sharedField != null ? FieldAccessor.Static(sharedField) : null;
        }

        // ===================================================================================
        // Fill
        // ===================================================================================

        /// <summary>
        /// Copies this frame's active markers into <paramref name="s"/>, when the farmer and the
        /// quest walker are both on.
        ///
        /// IT DOES NOTHING WHEN THE READER DID NOT RESOLVE OR EITHER SWITCH IS OFF, so a run
        /// without the quest walker is byte-identical to a build without this file: the
        /// snapshot's quest list stays what `Clear()` left it as -- empty.
        ///
        /// THE TRACKED QUEST COMES FIRST, AND IT IS NOT A SORT. `World.Quest` is the marker the
        /// client's own arrow points at; `EventQuests` is everything else the server has
        /// announced. The farmer's hold uses `Primary` to keep the selected quest ahead of a
        /// nearer event marker. A duplicate (the same object in both) is added once.
        /// </summary>
        public static void Fill(DodgeSnapshot s, DodgeConfig config)
        {
            if (!_resolved || s == null || !config.FarmMode || !config.FarmQuest || _getWorld == null)
            {
                return;
            }
            object world = _getWorld();
            if (world == null)
            {
                return;
            }

            object primary = _getQuest != null ? _getQuest(world) : null;
            // THE LIVE-OBJECT REGISTRY, READ ONCE PER FRAME. A `null` here means the field did not
            // resolve (see `Resolve`), in which case the staleness guard is skipped rather than
            // refusing everything -- the reader degrades to its pre-guard behaviour and `Status()`
            // said so at arm time.
            IDictionary objects = _getObjects != null ? _getObjects(world) as IDictionary : null;
            if (primary != null)
            {
                AddQuest(s, primary, true, objects);
            }

            object raw = _getEventQuests != null ? _getEventQuests(world) : null;
            IList events = raw as IList;
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    object item = events[i];
                    if (item == null)
                    {
                        continue;
                    }
                    // Reference equality is enough: the duplicate is the very object the server put
                    // in `World.Quest`, and `GameId` is only the fallback when the id resolved.
                    if (ReferenceEquals(item, primary))
                    {
                        continue;
                    }
                    AddQuest(s, item, false, objects);
                }
            }

            // AND THE OBJECTS THE CLIENT FLAGS IN THEIR OWN DATA. See `_getQuestFlag`: the tracked
            // reference can be empty while the client still knows a quest object, and this is the
            // other half. It runs after the two tracked sources so `Primary` and the event order are
            // unchanged, and its markers are added last because a tracked quest outranks a scanned one.
            ScanRegistry(world, objects);
            if (_scanned.Count > 0)
            {
                AddScanned(s);
            }
        }

        private static void AddQuest(DodgeSnapshot s, object item, bool primary, IDictionary objects)
        {
            int id = _getGameId != null ? _getGameId(item) : 0;
            for (int i = 0; i < s.Quests.Count; i++)
            {
                if ((id != 0 && s.Quests[i].GameId == id) || ReferenceEquals(s.Quests[i].Object, item))
                {
                    return;
                }
            }
            QuestTarget target;
            if (!TryBuildTarget(item, objects, primary, out target))
            {
                return;
            }
            s.Quests.Add(target);
            _markersSeen++;
        }

        /// <summary>
        /// One marker, as the client's own data describes it -- or false when the object is stale,
        /// unplaced, or dead enough to be no place to walk to.
        ///
        /// THE STALENESS GUARD LIVES HERE rather than in `AddQuest`, because both sources feed
        /// through this: the tracked reference and the registry scan. See `_getObjects`.
        /// </summary>
        private static bool TryBuildTarget(object item, IDictionary objects, bool primary,
                                           out QuestTarget target)
        {
            target = default(QuestTarget);
            int id = _getGameId != null ? _getGameId(item) : 0;

            // A MARKER THAT IS NOT AN OBJECT IN THIS WORLD IS NOT A PLACE IN THIS MAP.
            //
            // `World.Quest` is not cleared by `World.ClearWorld()` (see `_getObjects`), so after a
            // portal, a death or a dungeon exit it can still point at the realm the character left.
            // Walking to it is walking to a coordinate that means nothing here: the 2026-10-19 session
            // did exactly that for 165 frames in the Tavern and then reported it as `qsrc=worldquest`,
            // which is why the counter is separate and the log names it.
            //
            // THE TEST IS BY IDENTITY AND BY ID, because a `GameId` may survive a map change while
            // the instance behind it does not. An unresolved `GameId` (0) cannot be tested and is
            // allowed through: the farmer's own hold then re-picks it every frame, which is the
            // documented behaviour of a marker without an id.
            if (objects != null && id != 0)
            {
                if (!objects.Contains(id))
                {
                    _markersStale++;
                    return false;
                }
                object live = objects[id];
                if (live != null && !ReferenceEquals(live, item))
                {
                    _markersStale++;
                    return false;
                }
            }

            string name = null;
            if (_getData != null && _getName != null)
            {
                object data = _getData(item);
                if (data != null)
                {
                    name = _getName(data) as string;
                }
            }

            bool dead = _getDead != null && _entityType != null
                        && _entityType.IsInstanceOfType(item) && _getDead(item);

            float x;
            float y;
            if (!TryReadPlace(item, out x, out y))
            {
                _markersSkipped++;
                return false;
            }

            target.Object = item;
            target.GameId = id;
            target.X = x;
            target.Y = y;
            target.Name = name;
            target.Primary = primary;
            target.Activatable = _activatableType != null && _activatableType.IsInstanceOfType(item);
            target.Dead = dead;
            return true;
        }

        /// <summary>
        /// The marker's own place, in the MAP's frame, or false when it has none.
        ///
        /// `Position` FIRST, AND THIS IS A COORDINATE FIX RATHER THAN A PREFERENCE.
        ///
        /// `Node.ExactPosition` IS NOT IN THE MAP'S COORDINATE FRAME. `Node.SetPosition` builds it
        /// as `parentPosition + RotateXYOrigin(Position, sin, cos) * scale`, and the world's base
        /// node is ROTATED by `WorldRotation` (`World.cs`: `BaseNode.Rotation = WorldRotation`). So
        /// `ExactPosition` is the SCREEN-rotated point, while everything the hook reasons about --
        /// `Player.Position`, every enemy's `Position`, every portal's `Position`, and the collision
        /// grid's own indices -- is in the unrotated map frame. The client's own minimap agrees: it
        /// centres on `World.Quest.Position` and draws every marker from `.Position`.
        ///
        /// THE PREVIOUS ORDER WAS EXACTLY BACKWARDS, and it is the operator's "I think the quest
        /// co-ordinates are being read wrongly": with the camera rotated, a marker was published as
        /// its own position ROTATED about the origin, so the farmer walked at a place that was not
        /// the quest. `ExactPosition` remains the fallback for a marker whose `Position` is
        /// unpopulated, which is the only case where it can be the better answer.
        ///
        /// A MARKER AT (0,0) IS NOT A PLACE. That was the "walks to the top left" defect: a
        /// `World.Quest` whose object had not been placed yet reads zero, and every counter agreed
        /// the farmer had a target while it walked at the map origin.
        /// </summary>
        private static bool TryReadPlace(object item, out float x, out float y)
        {
            x = 0.0f;
            y = 0.0f;
            bool placed = false;
            if (_getX != null && _getY != null)
            {
                x = _getX(item);
                y = _getY(item);
                placed = x != 0.0f || y != 0.0f;
            }
            if (!placed && _getExactX != null && _getExactY != null)
            {
                float exactX = _getExactX(item);
                float exactY = _getExactY(item);
                if (exactX != 0.0f || exactY != 0.0f)
                {
                    x = exactX;
                    y = exactY;
                    placed = true;
                }
            }
            if (!placed
                || float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y)
                || (x == 0.0f && y == 0.0f))
            {
                return false;
            }
            return true;
        }

        // ===================================================================================
        // The registry scan: the DATA FLAGS, which are the second source
        // ===================================================================================

        /// <summary>
        /// Walks `World.Objects` and keeps every object whose DATA says it is a quest object.
        ///
        /// SEE `_getQuestFlag` FOR WHY THIS EXISTS AT ALL: the operator's 22:36 session had a quest
        /// indicator on screen and `World.Quest` null, so the tracked reference is not the only way
        /// the client knows. `EntityData.Quest` / `EntityData.EventQuest` are the client's own
        /// statement about an object, and a boss's Data is where the UI looks before it keeps the
        /// quest arrow alive.
        ///
        /// IT IS CACHED AND SCANNED AT `QuestScanFrames`, not read per frame: the walk is over every
        /// live object and costs about six delegate calls each. The markers it produces are carried
        /// forward between scans, so a hit does not disappear on the nine frames between them.
        /// </summary>
        private static void ScanRegistry(object world, IDictionary objects)
        {
            _scanClock++;
            if (!_flagsResolved)
            {
                return;
            }
            // `1` AND NOT `0`, so the FIRST call scans: the map is populated before the hook's first
            // decision in every session that matters, and a ten-frame hole at the start would be a
            // hole in exactly the report an operator reads first.
            if (objects == null || (_scanClock % QuestScanFrames) != 1L)
            {
                return;
            }
            _scanned.Clear();
            _scanObjects = objects.Count;
            _scanRan = true;
            IDictionaryEnumerator e = objects.GetEnumerator();
            while (e.MoveNext())
            {
                object item = e.Value;
                if (item == null || _getData == null)
                {
                    continue;
                }
                object data;
                try { data = _getData(item); }
                catch (Exception) { continue; }
                if (data == null)
                {
                    continue;
                }
                bool quest;
                bool eventQuest;
                try
                {
                    quest = ReadFlag(data, _questFlagTypes, _questFlagGetters);
                    eventQuest = ReadFlag(data, _eventFlagTypes, _eventFlagGetters);
                }
                catch (Exception)
                {
                    continue;
                }
                if (!quest && !eventQuest)
                {
                    continue;
                }
                // THE PLAYER IS NOT A MARKER, and neither is a dead object: the registry keeps both.
                if (_playerType != null && _playerType.IsInstanceOfType(item))
                {
                    continue;
                }
                QuestTarget target;
                if (!TryBuildTarget(item, objects, false, out target))
                {
                    continue;
                }
                if (target.Dead)
                {
                    continue;
                }
                _scanned.Add(target);
                _dataMarkers += 1;
            }
        }

        /// <summary>One bool field on the data object, via the declaring type that matches it.</summary>
        private static bool ReadFlag(object data, Type[] types, Func<object, bool>[] getters)
        {
            if (types == null || data == null)
            {
                return false;
            }
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i].IsInstanceOfType(data))
                {
                    return getters[i](data);
                }
            }
            return false;
        }

        /// <summary>
        /// The scanned markers, on their own terms: reference-deduped against what `Fill` already
        /// added this frame, so an object that is BOTH the tracked quest and data-flagged is listed
        /// once, and the tracked one keeps `Primary`.
        /// </summary>
        private static void AddScanned(DodgeSnapshot s)
        {
            for (int i = 0; i < _scanned.Count; i++)
            {
                QuestTarget t = _scanned[i];
                bool duplicate = false;
                for (int j = 0; j < s.Quests.Count; j++)
                {
                    if (ReferenceEquals(s.Quests[j].Object, t.Object)
                        || (t.GameId != 0 && s.Quests[j].GameId == t.GameId))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                {
                    t.Scanned = true;
                    s.Quests.Add(t);
                }
            }
        }

        // ===================================================================================
        // Activate
        // ===================================================================================

        /// <summary>
        /// Calls the client's own `ActivatableObject.Activate(0)` -- the virtual method the input
        /// handler calls when the player interacts with the object they are standing on.
        ///
        /// IT RUNS THE CLIENT'S GATES AND THAT IS THE POINT: locked/shop/cooldown/dialog state all
        /// live inside the override, so this cannot open a quest the client would refuse. A
        /// non-activatable marker (an enemy) is refused with a reason rather than called: the
        /// farmer walks at those and lets auto-fire do the work.
        ///
        /// THE THROW IS CAUGHT AND COUNTED, because this runs inside a patched frame and an
        /// exception there is a crash, not a failed attempt.
        /// </summary>
        public static bool TryActivate(object marker, out string reason)
        {
            reason = null;
            if (marker == null) { reason = "no quest marker"; return false; }
            if (_activatableType == null) { reason = "ActivatableObject never resolved"; return false; }
            if (!_activatableType.IsInstanceOfType(marker))
            {
                reason = "the quest marker is not an activatable";
                return false;
            }
            if (_activate == null) { reason = "Activate(int) never resolved"; return false; }

            _activationsAttempted++;
            try
            {
                _activate.Invoke(marker, new object[] { 0 });
                return true;
            }
            catch (Exception ex)
            {
                _activationsFailed++;
                reason = (ex.InnerException != null ? ex.InnerException.GetType().Name
                                                   : ex.GetType().Name)
                         + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
        }

        // ===================================================================================
        // Reflection helpers
        // ===================================================================================

        /// <summary>
        /// One component of the first vector field found, up the hierarchy -- `Position` first, then
        /// `ExactPosition`. This is `PortalReader`'s own helper, repeated rather than shared
        /// because it is nine lines of reflection and a shared home would be a third file every
        /// reader has to compile against.
        /// </summary>
        private static Func<object, float> VectorComponent(Type type, string component,
                                                          params string[] vectorFields)
        {
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                for (int i = 0; i < vectorFields.Length; i++)
                {
                    FieldInfo vector = t.GetField(vectorFields[i], Any | BindingFlags.DeclaredOnly);
                    if (vector == null)
                    {
                        continue;
                    }
                    FieldInfo part = vector.FieldType.GetField(component, Any);
                    if (part != null && part.FieldType == typeof(float))
                    {
                        return FieldAccessor.NestedFloat(vector, part);
                    }
                }
            }
            return null;
        }
    }
}
