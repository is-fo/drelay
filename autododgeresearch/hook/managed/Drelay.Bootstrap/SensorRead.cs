// SensorRead.cs -- the frame path's view of the world, and Phase 2's first real read.
//
// WHAT THIS IS, AND WHAT IT IS NOT YET.
//
// This walks the game's own live-object dictionary once per frame, picks out the
// projectiles, and feeds their identities to the ThreatSet.  It is the "threat set"
// half of Deliverable 2.1: the records exist, they are retired by evidence, and the
// pooling hazard is handled.  It deliberately does NOT yet sample trajectories or
// compare against the collision decision -- that is 2.2 and 2.3, and they are built on
// this, so this is the thing that has to be right first.
//
// WHERE THE LIVE PROJECTILES COME FROM, AND THE CORRECTION THIS FILE NOW CARRIES.
//
// There is no `List<Projectile>` and no projectile dictionary on `World`, and that much
// is unchanged: a projectile is an ordinary world node.  WHAT WAS WRONG IS WHERE THE
// NODE LANDS.  This file originally read `World.Objects` (a
// `Dictionary<int, GameObject>`) and filtered it by type, on CLIENT-MAP.md section
// 5.2's claim that `World.AddObject` puts a projectile there.  **It does not, and the
// 2026-10-12 evening session measured the consequence:** fifteen minutes of real play,
// 54 600 frames, a world present on every one of them, and `live=0` throughout --
// while the game's own log recorded beams, projectile lights and
// `Spawned 1 pending projectile(s) for entity 411542 (Rhinoceros)`.
//
// The static reason, from the decompiled build: `Entity.cs:1521,1550,1589` fires a
// projectile with `Projectile.Rent(...)` -> `World.GameObjectLayer.AddChild(projectile)`
// -> `projectile.World = World`, and never calls `World.AddObject`/`AddSortObject`.  Only
// `World.hehe_EB2C_E5A5_EE82_E9C5_EF78` ever writes `Objects[obj.GameId]`, and its five
// call sites are the map loader, the player, the entity spawn packets and the world's own
// spawn path -- no projectile among them.  So `World.Objects` holds the map's static
// objects plus entities, and **the live projectile set is
// `World.GameObjectLayer.Children`** (`Node._children`, which the layer's overridden
// `AddChild` reaches through `base.AddChild`).
//
// BOTH SOURCES ARE READ, AND BOTH ARE COUNTED.  This is not belt and braces: the counts
// are what closed CLIENT-MAP.md section 10.6's open experiment ("which layer live
// projectiles occupy"), and a hook that silently switched sources on the strength of a
// decompile would have replaced one unmeasured assumption with another.  The sensor line
// carries `objects_live=` and `layer_live=` separately, so the next run says which
// collection carried the bullets rather than asserting it.
//
// `Allocations.Projectiles` is still a `Pool<Projectile>` free list of recycled instances
// and must never be enumerated as "the live ones": doing so yields a threat set of dead
// objects, which presents as a dodge that reacts to bullets that are not there.
//
// BOTH SOURCES ARE SHARED WITH OTHER THINGS, so the filter is a real filter: the
// dictionary holds static and 3d objects, and the layer holds everything the world draws
// -- the player, enemies, effects, lasers and sweeps.  The synthetic host now puts
// non-projectiles in both, so a filter that never rejects anything cannot pass.
//
// WHY REFLECTION, FOR NOW, AND WHY THAT IS NOT LAZINESS.
//
// Reading a field of a type the hook cannot reference at compile time has two honest
// implementations: reflect, or resolve the field's offset once and read raw memory.  The
// project's own rule (HOOK-ARCHITECTURE.md section 5.5) is that offsets must be
// *measured*, never derived from declaration order, and the measurement machinery for
// reference fields and inline value types exists in StartupHook.cs already.
//
// This version reflects, and it does so on purpose: Phase 2's job is to produce four
// numbers, one of which is the per-frame cost.  A reflection-based read produces a
// correct warning distribution, correct false-negative and false-positive rates, and an
// honest -- probably bad -- cost number, and it does that without first betting the
// phase on a hand-computed offset for a private nested struct inside
// `Dictionary<int, GameObject>`.  If the cost is the problem, this file's own log says
// so, in microseconds, on every run; that is the evidence that would justify replacing
// it with the raw walk, and not a guess made before the first measurement.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace Drelay.Bootstrap
{
    public static class SensorRead
    {
        /// <summary>The game's live-object dictionary type is generic; this is the field on `World`.</summary>
        public const string ProjectileTypeName = "DarzasDominion.Darza.Game.Objects.Projectile";

        private static bool _resolved;
        private static string _unresolvedReason = "not attempted";

        private static FieldInfo _sharedField;
        private static FieldInfo _objectsField;
        private static FieldInfo _layerField;
        private static FieldInfo _childrenField;
        private static FieldInfo _gameIdField;
        private static FieldInfo _startTimeField;

        // EMITTED ACCESSORS, NOT GetValue: a GetValue per field per object per frame
        // boxes every value type it returns, and the collections that causes were measured
        // in the sampler's p99 (PHASE2-SCOPE.md section 4.11).  Built once, on resolve.
        private static Func<object> _getShared;
        private static Func<object, object> _getObjects;
        private static Func<object, object> _getLayer;
        private static Func<object, object> _getChildren;
        private static Func<object, int> _getGameId;
        private static Func<object, int> _getStartTime;
        private static Type _projectileType;
        private static Type _layerType;

        internal static readonly ThreatSet Threats = new ThreatSet();

        /// <summary>
        /// The live projectile objects this frame, reused every frame rather than
        /// reallocated, and handed to the sampler.
        ///
        /// IT HOLDS REFERENCES, AND THAT IS DELIBERATE.  A strong reference keeps a
        /// projectile alive for the length of the frame in which it was read, which is
        /// the frame the sampler is about to look at it in -- the alternative is a
        /// collection between the read and the sample, which would hand the sampler a
        /// bullet that has already been recycled.
        /// </summary>
        private static readonly List<object> LiveObjects = new List<object>();

        /// <summary>The identity of each live projectile, in lockstep with LiveObjects, so the sampler can mark a prediction on the right record.</summary>
        private static readonly List<ThreatKey> LiveKeys = new List<ThreatKey>();

        /// <summary>
        /// Which keys have already been taken from a source this frame, so the two sources
        /// cannot count the same bullet twice.  Reused, never reallocated.
        /// </summary>
        private static readonly HashSet<long> SeenKeys = new HashSet<long>();

        private static int _frame;
        private static long _framesRead;
        private static long _framesUnreconciled;

        // PER SOURCE, BECAUSE THE SPLIT IS THE MEASUREMENT.  `objects_live` and
        // `layer_live` on the sensor line are what says which collection carries the
        // bullets on this build; their sum is `live`.
        private static long _objectsLiveTotal;
        private static long _layerLiveTotal;
        private static long _layerNodesTotal;

        /// <summary>Frames whose read threw, kept apart from frames with nothing to read.</summary>
        private static long _readExceptions;
        private static long _retiredByReconcile;
        private static long _lastCostUs;
        private static long _maxCostUs;
        private static long _nextLogFrame = 150;

        /// <summary>Bucketed per-frame read cost, for the p99 the phase has to publish.</summary>
        public static readonly CostHistogram Cost = new CostHistogram();

        public static bool Resolved => _resolved;

        /// <summary>The frame counter the collision observation turns into a warning.</summary>
        public static int Frame => _frame;

        /// <summary>Identities currently believed live. Only meaningful after a frame has run.</summary>
        public static int TrackedCount => Threats.Count;

        /// <summary>
        /// Resolves the four things the read path needs, once, on the resolve callback.
        ///
        /// A FAILURE HERE IS NOT FATAL AND MUST NOT BE.  The sensor is Phase 2's; the
        /// frame read, the patch and the evidence all belong to Phase 1 and keep working
        /// without it.  So this returns false with a reason, the reason is logged, and
        /// the frame path simply does not run the sensor.
        /// </summary>
        public static bool Resolve(Assembly game)
        {
            try
            {
                Type worldType = game.GetType(ManagedArmingProbe.WorldTypeName);
                _projectileType = game.GetType(ProjectileTypeName);
                if (worldType == null || _projectileType == null)
                {
                    _unresolvedReason = "types not found: World=" + (worldType != null)
                                        + " Projectile=" + (_projectileType != null);
                    return false;
                }

                // THE NAMES ARE CANDIDATES, AND THE ONE THAT MATCHED IS LOGGED.  `GameId`
                // is declared on the base `GameObject` and `_startTime` privately on
                // `Projectile`, so a lookup that asked only one type would find half of
                // what it needs and report the other half as missing.
                string sharedName;
                string objectsName;
                string gameIdName;
                string startName;
                _sharedField = FieldLookup.Find(worldType, out sharedName, "Shared", "_shared");
                _objectsField = FieldLookup.Find(worldType, out objectsName, "Objects", "_objects");
                _gameIdField = FieldLookup.Find(_projectileType, out gameIdName,
                                                "GameId", "_gameId", "ObjectId");
                _startTimeField = FieldLookup.Find(_projectileType, out startName,
                                                   "_startTime", "StartTime", "startTime");

                if (_sharedField == null || _objectsField == null || _gameIdField == null
                    || _startTimeField == null)
                {
                    _unresolvedReason = "fields not found: World.Shared=" + (_sharedField != null)
                                        + " World.Objects=" + (_objectsField != null)
                                        + " Projectile.GameId=" + (_gameIdField != null)
                                        + " Projectile.StartTime=" + (_startTimeField != null);
                    return false;
                }

                // The dictionary must be enumerable as a non-generic IDictionary, which
                // is what lets this file walk a collection whose element type it cannot
                // name.  `Dictionary<TKey,TValue>` implements it, so this is a check on
                // the game's shape rather than on ours.
                Type objectsType = _objectsField.FieldType;
                if (!typeof(IDictionary).IsAssignableFrom(objectsType))
                {
                    _unresolvedReason = "World.Objects is " + objectsType.FullName
                                        + ", which is not an IDictionary";
                    return false;
                }

                _getShared = FieldAccessor.Static(_sharedField);
                _getObjects = FieldAccessor.Reference(_objectsField);
                _getGameId = FieldAccessor.Int(_gameIdField);
                _getStartTime = FieldAccessor.Int(_startTimeField);

                // THE SECOND SOURCE, AND THE ONE THAT ACTUALLY CARRIES THE BULLETS.
                // `World.GameObjectLayer` is a field whose name equals its type name, and
                // the child list hangs off `Nodes.Node`, so `_children` is found by the
                // same hierarchy walk as everything else -- `Children` is a property, and
                // `GetField` does not see properties.
                //
                // NOT FATAL IF IT DOES NOT RESOLVE, AND DELIBERATELY SO: refusing here
                // would throw away the read, the patch and the whole run over a field
                // name, and the sensor line's `layer_nodes=0 layer_live=0` says exactly
                // what happened.  What it must not do is stay quiet, so a missing layer
                // is a WARN with the reason on it.
                string layerName = "(none)";
                string childrenName = "(none)";
                _layerField = FieldLookup.Find(worldType, out layerName, "GameObjectLayer", "_gameObjectLayer");
                if (_layerField != null)
                {
                    _layerType = _layerField.FieldType;
                    _childrenField = FieldLookup.Find(_layerType, out childrenName,
                                                      "_children", "children", "Children");
                }

                if (_childrenField != null && typeof(IList).IsAssignableFrom(_childrenField.FieldType))
                {
                    _getLayer = FieldAccessor.Reference(_layerField);
                    _getChildren = FieldAccessor.Reference(_childrenField);
                }
                else
                {
                    HookEntry.AppendLog("WARN", "the sensor could not resolve the projectile layer: "
                                                + layerName + " field=" + (_layerField != null)
                                                + ", child list=" + childrenName
                                                + (childrenName != "(none)" && !typeof(IList).IsAssignableFrom(_childrenField.FieldType)
                                                    ? " (" + _childrenField.FieldType.FullName + ", not an IList)" : "")
                                                + "; projectiles will be read from World.Objects only, "
                                                + "which the client does not populate -- expect live=0");
                    HookEntry.FlushLog();
                }

                _resolved = true;
                HookEntry.AppendLog("INFO", "sensor read path resolved: " + objectsName
                                            + " is " + objectsType.Name + ", projectiles are "
                                            + _projectileType.FullName + " (" + FieldLookup.Hierarchy(_projectileType)
                                            + "), identity " + gameIdName + " + " + startName
                                            + "; the projectile source is "
                                            + (_getChildren != null
                                                ? layerName + "." + childrenName + " ("
                                                  + FieldLookup.Hierarchy(_layerType) + ", "
                                                  + _childrenField.FieldType.Name + ")"
                                                : "World.Objects ONLY -- the layer did not resolve"));
                HookEntry.FlushLog();
                return true;
            }
            catch (Exception ex)
            {
                _unresolvedReason = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Why the read path is not available, for the log.</summary>
        public static string UnresolvedReason => _unresolvedReason;

        /// <summary>
        /// One frame's read.  Called from the managed replacement, so its caller is
        /// managed and allocating here is safe -- it is only expensive.
        ///
        /// THE ASYMMETRY IS THE IMPORTANT PART.  If anything goes wrong -- no world, no
        /// dictionary, an exception mid-walk -- the frame is NOT reconciled.  A read that
        /// did not happen is not evidence that a bullet is gone, and reconciling anyway
        /// is how one hiccup retires every live bullet at once.
        /// </summary>
        public static void OnFrame()
        {
            if (!_resolved)
            {
                return;
            }

            _frame++;
            long t0 = Stopwatch.GetTimestamp();

            Threats.BeginFrame();
            LiveObjects.Clear();
            LiveKeys.Clear();
            bool enumerationOk = false;
            int objectsLive = 0;
            int others = 0;
            int layerLive = 0;
            int layerNodes = 0;
            int layerOthers = 0;
            SeenKeys.Clear();

            try
            {
                object world = _getShared();
                if (world != null)
                {
                    // ---------------------------------------------------------------
                    // SOURCE 1: `World.Objects`.  KEPT, COUNTED, AND NOT THE PROJECTILE
                    // SET.  It is where the map's static objects and the entities live,
                    // and reading it costs a walk of a few hundred entries per frame;
                    // dropping it would throw away the measurement this correction rests
                    // on.  `objects_live` on the sensor line is what it contributed.
                    // ---------------------------------------------------------------
                    object objects = _getObjects(world);
                    IDictionary dictionary = objects as IDictionary;
                    if (dictionary != null)
                    {
                        foreach (DictionaryEntry entry in dictionary)
                        {
                            object value = entry.Value;
                            if (value == null)
                            {
                                continue;
                            }
                            if (!_projectileType.IsInstanceOfType(value))
                            {
                                others++;
                                continue;
                            }
                            if (Track(value))
                            {
                                objectsLive++;
                            }
                        }
                        enumerationOk = true;
                    }
                    else if (_framesUnreconciled == 0)
                    {
                        // WHY AN UNRECONCILED FRAME NEEDS A REASON.  The first version
                        // reported `unreconciled=N` and nothing else, and when the sensor
                        // came back with every frame unreconciled there was nothing to read:
                        // "no world yet", "a world with no object dictionary" and "the
                        // dictionary is not an IDictionary" are three different problems
                        // with three different owners, and a bare count cannot tell them
                        // apart.  This cost one diagnostic round.
                        HookEntry.AppendLog("WARN", "the sensor cannot enumerate: "
                                                    + "World.Shared is non-null but its Objects field is "
                                                    + (objects == null ? "null" : "not an IDictionary")
                                                    + " (" + (objects == null ? "null"
                                                              : objects.GetType().FullName) + ")");
                        HookEntry.FlushLog();
                    }

                    // ---------------------------------------------------------------
                    // SOURCE 2: `World.GameObjectLayer.Children`, WHICH IS WHERE THE
                    // CLIENT'S PROJECTILES ACTUALLY ARE (see the file header, and the
                    // measurement this correction comes from).
                    //
                    // INDEXED RATHER THAN ENUMERATED, AND THE COUNT IS READ ONCE.  A
                    // `foreach` over a `List<Node>` throws the moment anything is added
                    // to it, and the world is mutated on this very thread every frame --
                    // the layer is the collection the game adds bullets to.  Reading
                    // `Count` once and indexing keeps the walk bounded and cannot throw
                    // mid-enumeration.
                    //
                    // THE WALK IS O(EVERYTHING THE WORLD DRAWS) -- static objects,
                    // entities, effects, lasers and sweeps as well as bullets -- which
                    // CLIENT-MAP.md section 10.6 predicted would be the cost of reading a
                    // layer instead of a list.  The histogram on this file's own report
                    // line is where that prediction gets a number; if the p99 leaves the
                    // 150 us frame-tick slice, the next step is `_dynamicList`, which the
                    // layer keeps for exactly the nodes that move.
                    // ---------------------------------------------------------------
                    if (_getChildren != null)
                    {
                        object layer = _getLayer(world);
                        if (layer != null)
                        {
                            IList children = _getChildren(layer) as IList;
                            if (children != null)
                            {
                                int count = children.Count;
                                for (int i = 0; i < count; i++)
                                {
                                    object node = children[i];
                                    if (node == null)
                                    {
                                        continue;
                                    }
                                    layerNodes++;
                                    if (!_projectileType.IsInstanceOfType(node))
                                    {
                                        layerOthers++;
                                        continue;
                                    }
                                    if (Track(node))
                                    {
                                        layerLive++;
                                    }
                                }
                                enumerationOk = true;
                            }
                            else if (_framesUnreconciled == 0)
                            {
                                HookEntry.AppendLog("WARN", "the sensor cannot walk the projectile layer: "
                                                            + "its child field is "
                                                            + (_getChildren(layer) == null ? "null" : "not an IList"));
                                HookEntry.FlushLog();
                            }
                        }
                        else if (_framesUnreconciled == 0)
                        {
                            HookEntry.AppendLog("INFO", "the sensor has a world but no GameObjectLayer yet, "
                                                        + "so only World.Objects was read this frame");
                            HookEntry.FlushLog();
                        }
                    }
                }
                else if (_framesUnreconciled == 0)
                {
                    HookEntry.AppendLog("INFO", "the sensor has no world yet: World.Shared is null, so "
                                                + "no frame is reconciled (a read that did not happen is "
                                                + "not an absence)");
                    HookEntry.FlushLog();
                }
            }
            catch (Exception ex)
            {
                // THE FIRST EXCEPTION IS ALWAYS LOGGED, AND KEYING THAT ON THE UNRECONCILED
                // COUNT WAS A BLIND SPOT.  The frame before the world exists is unreconciled
                // for an ordinary reason -- there is nothing to read yet -- and it sets the
                // count to one; every frame after it that threw was then silent, and the
                // only symptom was `live=0 unreconciled=N` with no reason attached.  It cost
                // a diagnostic round.  Two counters, because "the world is not there yet"
                // and "the read threw" are different problems.
                _readExceptions++;
                if (_readExceptions == 1)
                {
                    // THE STACK, NOT ONLY THE TYPE.  "NullReferenceException" out of a dozen
                    // nested calls says nothing about which one; the first line of the trace
                    // says everything, and this fires once per run so it can afford the
                    // characters.
                    HookEntry.AppendLog("ERROR", "the sensor's frame read threw; no frame is "
                                                 + "reconciled while this lasts: " + ex.GetType().Name
                                                 + ": " + ex.Message + " | " + HookEntry.FirstFrames(ex));
                    HookEntry.FlushLog();
                }
            }

            // THE READ'S COST IS MEASURED AROUND THE READ, AND THE SAMPLER'S AROUND THE
            // SAMPLER.  The first version of this timed them as one window, and the
            // number that came out -- 300 to 1200 microseconds, against 7 to 16 for the
            // read alone -- was not the read's cost at all: it was the read plus the
            // sampler plus the gen0 collections the sampler's own boxing triggers, all
            // attributed to the read.  Two costs that are reported separately are
            // measurements; one number that contains both is a number nobody can act on.
            long t1 = Stopwatch.GetTimestamp();

            if (enumerationOk)
            {
                _retiredByReconcile += Threats.Reconcile(_frame);
                _framesRead++;
                _objectsLiveTotal += objectsLive;
                _layerLiveTotal += layerLive;
                _layerNodesTotal += layerNodes;

                // Deliverable 2.2, on the set that was just read.  Only when the
                // enumeration succeeded: sampling a partial set would report a straight
                // fraction for a frame the sensor did not really see.
                TrajectorySampler.OnFrame(LiveObjects, LiveKeys, _frame);
            }
            else
            {
                Threats.NoteUnreconciledFrame();
                _framesUnreconciled++;
            }

            long frequency = Stopwatch.Frequency;
            _lastCostUs = frequency > 0 ? ((t1 - t0) * 1000000L) / frequency : 0L;
            Cost.Add(_lastCostUs);
            if (_lastCostUs > _maxCostUs)
            {
                _maxCostUs = _lastCostUs;
            }

            if (_frame >= _nextLogFrame)
            {
                _nextLogFrame += 150;
                // THE SPLIT IS APPENDED, NOT INSERTED, AND THAT IS DELIBERATE: the field
                // order before it is what the injection suite and the session analyser
                // already parse, and a line whose meaning moves cannot be compared with
                // the runs it is being compared against.
                HookEntry.AppendLog("INFO", "sensor: frame " + _frame + ": live=" + (objectsLive + layerLive)
                                            + " other=" + others + " tracked=" + Threats.Count
                                            + " retired=" + _retiredByReconcile
                                            + " unreconciled=" + _framesUnreconciled + " threw=" + _readExceptions
                                            + " frames_read=" + _framesRead
                                            + " cost=" + _lastCostUs + "us max=" + _maxCostUs + "us"
                                            + " layer_nodes=" + layerNodes + " layer_live=" + layerLive
                                            + " layer_other=" + layerOthers + " objects_live=" + objectsLive);
                HookEntry.FlushLog();
            }
        }

        /// <summary>
        /// Records one projectile and returns whether it was new this frame.
        ///
        /// THE SEEN-SET IS WHY THIS EXISTS.  Two sources are walked, and a bullet that
        /// appeared in both would otherwise be counted twice, sampled twice, and would
        /// make `sampled` exceed `live` -- a comparison the injection suite uses as its
        /// witness.  `Threats.Observe` is idempotent for the record, but the lists the
        /// sampler reads are not.
        /// </summary>
        private static bool Track(object projectile)
        {
            int gameId = _getGameId(projectile);
            int startTime = _getStartTime(projectile);
            ThreatKey key = new ThreatKey(gameId, startTime);
            if (!SeenKeys.Add(key.Packed))
            {
                return false;
            }
            LiveObjects.Add(projectile);
            LiveKeys.Add(key);
            Threats.Observe(key, _frame);
            return true;
        }

        /// <summary>Bullets read from `World.Objects` so far -- the source that carries none on the client.</summary>
        public static long ObjectsLiveTotal => _objectsLiveTotal;

        /// <summary>Bullets read from the world's drawing layer so far -- the source that carries them.</summary>
        public static long LayerLiveTotal => _layerLiveTotal;

        /// <summary>Nodes walked in the layer so far, bullets or not: the cost this correction buys.</summary>
        public static long LayerNodesTotal => _layerNodesTotal;

        /// <summary>True when the layer source resolved. False means `live=` is Objects-only and will read 0 on the client.</summary>
        public static bool LayerSourceResolved => _getChildren != null;
    }
}







