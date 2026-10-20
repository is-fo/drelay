// MapRoute.cs -- the route that is longer than the planner's window: the dungeon's own map, built
// from the windows the adapter already reads, walked a frontier at a time.
//
// =====================================================================================
// THE PROBLEM THIS FILE EXISTS FOR, MEASURED
// =====================================================================================
//
// `PathPlanner` searches a 128-unit window around the player. That is the right size for a dodge and
// the wrong size for a dungeon: `20261020-181445-p4196` (River Maze) has the boss 32 units east of
// the character, behind a wall, and the page of the map that reaches it goes SOUTH, then east, then
// back north -- a 79-cell route whose two ends are 32 units apart. Over the known floor of that run,
// a breadth-first walk from the character's cell reaches a cell 1 unit from the boss in **78 steps**.
// The local planner cannot see that: at the frames where it was asked it answered `unreachable`, and
// on the frames before that it spent its 1 200-node budget without finishing (`nodecap` 1 050 of
// 2 355 frames). The character then wandered, which is the operator's "walked up, got stuck in a
// room the boss room was to the east of".
//
// =====================================================================================
// THE SECOND MEASUREMENT: `20261020-183555-p37604`, INFESTED CELLAR
// =====================================================================================
//
// The first version of this file kept only floor cells and routed to "the reachable cell nearest the
// goal". It worked in River Maze and it did not work in Infested Cellar, and the trace says exactly
// why. The character entered the dungeon at (512.5, 511.5) on frame 7 251 and the session ended on
// frame 13 290 -- **6 039 frames, 100 seconds, in the spawn room** -- while `PHASE 3 DODGE MAPROUTE`
// reported the cache growing 718 -> 2 389 -> 999 -> 1 054 floor cells and then *stopping*, plans
// climbing to 1 344 and refusals to 1 205.
//
// Over the floor that session discovered (1 054 cells), replaying the old rule frame by frame gives a
// two-cell cycle. The published waypoint alternates between (521.5, 510.5) -- the room's north-east
// corner, the reachable cell nearest the boss at (563, 506) -- and (519.5, 516.5), the frontier the
// "go and look" fallback picked when the character was already standing on the first. The recorded
// position does the same thing: for the last 4 000 frames the character shuttles between
// x 507.4 and x 520.6 at y 511.6, never further south than y 536. The exit is a corridor that opens
// at (513..515, 553..555) -- **known floor, never visited**, because every rule in the old file
// preferred the goal's own direction.
//
// WHY THE FRONTIER NEVER RETIRED. The cache stored only "floor" and "not floor", so a wall was
// indistinguishable from unexplored space and every cell of the room's wall was a frontier for ever.
// The nearest-to-the-goal one was the east wall, so "explore" and "walk to the boss" were the same
// instruction, and the character pressed the wall, drifted to the next-nearest frontier, was pulled
// back east, and repeated that six thousand times.
//
// SO THE CACHE HAS THREE STATES NOW, and the third one is the fix:
//
//     0  unknown      nothing has been painted here yet -- a frontier neighbour
//     1  open         a tile exists and it is walkable
//     2  wall         the collision byte, or the map border -- a hard wall
//     3  wall (tile)  a `NoWalk` tile. A wall, but a PLACEHOLDER while the map streams (§14.12):
//                     the tile that arrives over it later is still allowed to turn it into 1.
//
// A frontier is therefore "an open cell with an unknown neighbour" and nothing else. `NoWalk` rock
// stops being a frontier, the room's wall stops attracting the route, and the only frontier left in
// the Infested Cellar is the south corridor's mouth. That is a map, not a gradient.
//
// =====================================================================================
// AND IT IS A WALK OF THE MAP, NOT A BEELINE: commit, then back off a dead end
// =====================================================================================
//
// The operator's words are the specification: "It needs to build an internal map inside dungeons to
// map them out. It needs to be able to backtrack if it hits a dead end. All dungeons are always
// solvable, but there might be paths that are dead ends."
//
// So exploration is a committed frontier walk:
//
//   * the route to the quest goal is taken whenever the known floor CONNECTS to it -- that is the
//     question a dungeon asks, and it is answered by a breadth-first walk from the character, not by
//     a distance;
//   * otherwise a frontier cell is chosen by PATH LENGTH (breadth-first depth), nearest first, with
//     distance to the goal only as a tie-break. Nearest-first is what makes a dead end cheap: it is
//     walked, spent, and the next frontier is generally the junction it came from;
//   * the choice is COMMITTED. It is not recomputed from the goal distance every frame -- that
//     recomputation is the ping-pong above -- and it survives the goal moving, because a boss that
//     walks two units does not change which corridor has been explored;
//   * a frontier is spent when the character has stood on it and the map did not grow for
//     `DwellFrames`, or when walking to it took longer than `ExploreTimeoutFrames`. It is marked
//     TRIED and never chosen again, which is the backtrack: the next choice is the nearest untried
//     frontier, and from a dead end that is the way back;
//   * when no untried frontier is reachable the dungeon's connected floor has been mapped and the
//     route says so (`Status()` reads "mapped"); `false` leaves the caller its own goal instead of
//     inventing a waypoint.
//
// The one thing the old rule did that this keeps: while the character is standing on the chosen
// frontier waiting out the dwell it is published THAT CELL, so the character stands still beside the
// unknown for a moment and lets the streamer paint. A dungeon does not reveal itself to a character
// that keeps walking.
//
// =====================================================================================
// IT IS A GOAL SOURCE, NOT A CONTROLLER
// =====================================================================================
//
// Like `Farmer` and `RouteRuntime`, this module writes ONE point (`GoalX`/`GoalY`) and nothing else.
// The planner, admission, the score, the hysteresis and the escape floor are untouched by it: a
// route narrows WHERE the character is walking, never whether a step is safe.

using System;

namespace Drelay.Bootstrap
{
    /// <summary>
    /// The map-scale route: a walkability map filled by the frames the adapter already reads, and the
    /// next waypoint along the best-known route to a goal -- or, when the map does not reach the goal
    /// yet, the next frontier of the dungeon.
    /// </summary>
    public static class MapRoute
    {
        // --- The cache ---------------------------------------------------------------------
        //
        // `_state[i]`: see the header for the four values. `_tried[i]` is the exploration memory: a
        // frontier that has been walked to and produced nothing is not chosen again.

        public const byte CellUnknown = 0;
        public const byte CellCollision = 1;
        public const byte CellNoWalkTile = 2;
        public const byte CellFloor = 3;
        public const byte CellOutOfMap = 4;

        private const byte Unknown = 0;
        private const byte Open = 1;
        private const byte Wall = 2;
        private const byte WallTile = 3;

        private static byte[] _state;
        private static byte[] _tried;
        private static int _w;
        private static int _h;
        private static object _mapKey;
        private static bool _available;
        private static string _status = "not attempted";

        /// <summary>
        /// The largest map this will keep a cache for, in cells. A dungeon is 1 024 x 1 024 (1.05 M,
        /// about 16 MB with the search arrays); a realm is 2 698 x 2 634 (7.1 M, 90 MB), and the realm
        /// is where the operator says the quest walk already works, so it is refused rather than paid
        /// for. The refusal is in `Status()`, so a run can tell it from "the cache is empty".
        /// </summary>
        private const int MaxMapCells = 2000000;

        /// <summary>Nodes the breadth-first walk may expand in one plan. Bounds the frame cost.</summary>
        private const int PlanNodeBudget = 40000;

        /// <summary>How far along the route the published waypoint sits, in world units.</summary>
        private const float WaypointAhead = 6.0f;

        /// <summary>A route node this close behind the character is dropped as walked past.</summary>
        private const float AdvanceRadius = 2.5f;

        /// <summary>Step off the route by more than this and it is replanned rather than followed.</summary>
        private const float DeviationRadius = 6.0f;

        /// <summary>Frames between replans even while the held route is still valid: the map reveals.</summary>
        private const int ReplanFrames = 40;

        /// <summary>The goal moving more than this invalidates the held goal route.</summary>
        private const float GoalMoveRadius = 6.0f;

        /// <summary>
        /// Frames the character stands on a frontier before it is declared dry. One second at the
        /// measured frame rate: the Infested Cellar's own streaming measurements put a cell's arrival
        /// at tens of frames after the character came near it (§14.12(a)), and half a second was too
        /// short to tell "still arriving" from "dead end".
        /// </summary>
        private const int DwellFrames = 60;

        /// <summary>
        /// Frames a frontier may be walked toward before it is abandoned as unreachable. Five seconds
        /// is several times the length of any room measured so far, and the counter exists so a
        /// target the character cannot physically reach costs five seconds and not the session.
        /// </summary>
        private const int ExploreTimeoutFrames = 300;

        // --- The search state, all allocated once per map ------------------------------------

        private static byte[] _seen;
        private static byte _seenGeneration;
        private static int[] _parent;
        private static int[] _depth;
        private static int[] _queue;

        // --- The held route ------------------------------------------------------------------

        /// <summary>1 = walking to the goal, 2 = walking to a frontier. `_mode` only gates the replan rules.</summary>
        private const byte ModeNone = 0;
        private const byte ModeGoal = 1;
        private const byte ModeExplore = 2;
        private static byte _mode;

        private static int[] _path = new int[0];
        private static int _pathCount;
        private static int _pathIndex;
        private static float _routeGoalX;
        private static float _routeGoalY;
        private static long _planFrame;
        private static long _frames;

        // --- The committed frontier ----------------------------------------------------------
        //
        // Whether the next waypoint is a waypoint or an exploration target is decided by `_mode`;
        // this is the cell an exploration route is walking to, and the boundary between "still
        // arriving" and "dead end" is measured against it.

        private static int _exploreTarget = -1;
        private static long _exploreStartFrame;
        private static long _exploreArriveFrame;
        private static int _exploreKnownAtArrival;

        // --- Counters, for the report --------------------------------------------------------

        private static long _observations;
        private static long _plans;
        private static long _goalPlans;
        private static long _waypoints;
        private static long _refusals;
        private static long _known;
        private static int _walls;
        private static long _deadEnds;
        private static long _timeouts;
        private static long _explored;

        /// <summary>True when a cache exists and routes may be asked for.</summary>
        public static bool Available => _available;

        /// <summary>Floor cells learned so far.</summary>
        public static long KnownCells => _known;

        /// <summary>Frontiers abandoned after being walked to and producing nothing.</summary>
        public static long DeadEnds => _deadEnds;

        /// <summary>
        /// The cache's size, its knowledge and the plan counters, for one log line. The frontier
        /// count is a scan and is only paid once per report, which is what makes it honest: it is
        /// the number of cells this module would be willing to walk to next.
        /// </summary>
        public static string Status()
        {
            if (!_available)
            {
                return "map route OFF (" + _status + ")";
            }
            int frontiers = 0;
            if (_state != null)
            {
                for (int i = 0; i < _state.Length; i++)
                {
                    if (_state[i] == Open && _tried[i] == 0 && HasUnknownNeighbour(i))
                    {
                        frontiers++;
                    }
                }
            }
            // THE LABELS ARE SHORT BECAUSE THE LOG LINE IS. `DrelayLog.cpp` formats a line into a
            // 256-byte buffer with `_vsnprintf_s(..., _TRUNCATE)` and a truncated line carries no
            // mark saying so, so this string is budgeted against that buffer and not against taste:
            // the whole `PHASE 3 DODGE MAPROUTE | ... | goal from the route N frame(s)` line is 211
            // characters with five-digit counters, which leaves room for the numbers to grow.
            return "map route ON (" + _w + "x" + _h + ", " + _known + " floor cell(s), " + _walls
                   + " wall, " + frontiers + " frontier left, " + _plans + " plan (" + _goalPlans
                   + " goal), " + _waypoints + " waypoint, " + _refusals + " refused, " + _deadEnds
                   + " dead end, " + _timeouts + " timeout, " + _explored + " explored)";
        }

        /// <summary>
        /// Binds the cache to this map: called from `DodgeAdapter.RefreshGrid` on every refresh, and
        /// it reallocates only when the grid ARRAY identity or its dimensions change, which is a map
        /// change (`World.ClearWorld` allocates a new `byte[,]`).
        /// </summary>
        public static void EnsureMap(object arrayKey, int width, int height)
        {
            if (arrayKey == null || width <= 0 || height <= 0)
            {
                _available = false;
                _status = "no collision grid";
                return;
            }
            if (ReferenceEquals(arrayKey, _mapKey) && width == _w && height == _h && _state != null)
            {
                return;
            }
            _mapKey = arrayKey;
            _w = width;
            _h = height;
            _path = new int[0];
            _pathCount = 0;
            _pathIndex = 0;
            _mode = ModeNone;
            _exploreTarget = -1;
            _routeGoalX = 0.0f;
            _routeGoalY = 0.0f;

            long area = (long)width * height;
            if (area > MaxMapCells)
            {
                _available = false;
                _state = null;
                _tried = null;
                _seen = null;
                _parent = null;
                _depth = null;
                _queue = null;
                _status = width + "x" + height + " is " + area + " cells, above the " + MaxMapCells
                          + "-cell cap";
                return;
            }
            _state = new byte[area];
            _tried = new byte[area];
            _seen = new byte[area];
            _parent = new int[area];
            _depth = new int[area];
            _queue = new int[area < PlanNodeBudget ? area : PlanNodeBudget];
            _seenGeneration = 0;
            _known = 0;
            _walls = 0;
            _available = true;
            _status = "ready";
        }

        /// <summary>
        /// Records one cell the adapter has just classified, in the TRACE'S OWN SOURCE CODES
        /// (`0` unpainted, `1` the server's collision byte, `2` a `NoWalk` tile, `3` floor, `4`
        /// outside the map). The codes are shared with `TraceRecorder` deliberately: the offline
        /// replay feeds these numbers straight from the recording, so one vocabulary has to exist.
        ///
        /// `NoWalk` IS A WALL HERE AND THAT IS NOT A CONTRADICTION OF §14.12. That section's 70-frame
        /// `NoWalk` placeholder matters because it is the frontier of a map still arriving; the state
        /// it maps to is `WallTile`, which a later floor observation overrides. A hard wall (state 2)
        /// never becomes walkable, a placeholder (state 3) does, and the route only ever walks state 1.
        /// </summary>
        public static void Observe(int x, int y, byte source)
        {
            if (!_available || _state == null)
            {
                return;
            }
            if (x < 0 || y < 0 || x >= _w || y >= _h)
            {
                return;
            }
            int index = (y * _w) + x;
            byte prior = _state[index];
            if (prior == Open)
            {
                // A FLOOR CELL IS NEVER UN-KNOWN. The streamer does not turn a walkable tile into a
                // wall, and a knockback that reads gaps in the window must not erase the map.
                return;
            }
            byte next;
            switch (source)
            {
                case CellFloor:
                    next = Open;
                    break;
                case CellCollision:
                case CellOutOfMap:
                    next = Wall;
                    break;
                case CellNoWalkTile:
                    next = WallTile;
                    break;
                default:
                    // UNPAINTED IS NO INFORMATION: neither walkable nor a wall. That is the cell a
                    // frontier is made of; see the header.
                    return;
            }
            if (prior == next)
            {
                return;
            }
            _state[index] = next;
            if (next == Open)
            {
                _known++;
                _observations++;
                if (prior == Wall || prior == WallTile)
                {
                    _walls--;
                }
            }
            else if (prior == Unknown)
            {
                _walls++;
            }
        }

        /// <summary>Forgets the held route without forgetting the map. Called on a world change.</summary>
        public static void ForgetRoute()
        {
            _pathCount = 0;
            _pathIndex = 0;
            _mode = ModeNone;
            _exploreTarget = -1;
            _routeGoalX = 0.0f;
            _routeGoalY = 0.0f;
        }

        /// <summary>Clears everything, including counters. For a test harness or a restart.</summary>
        public static void Reset()
        {
            _state = null;
            _tried = null;
            _seen = null;
            _parent = null;
            _depth = null;
            _queue = null;
            _mapKey = null;
            _w = 0;
            _h = 0;
            _available = false;
            _status = "not attempted";
            _path = new int[0];
            _pathCount = 0;
            _pathIndex = 0;
            _mode = ModeNone;
            _exploreTarget = -1;
            _exploreArriveFrame = 0;
            _exploreKnownAtArrival = 0;
            _frames = 0;
            _observations = 0;
            _plans = 0;
            _goalPlans = 0;
            _waypoints = 0;
            _refusals = 0;
            _known = 0;
            _walls = 0;
            _deadEnds = 0;
            _timeouts = 0;
            _explored = 0;
        }

        /// <summary>
        /// The next point to walk at. Before any map is known it is the next waypoint of the route to
        /// the goal; when the known floor does not reach the goal it is the waypoint of the committed
        /// frontier walk. False when there is nothing useful to publish -- no cache, no floor, or
        /// every frontier already explored -- and the caller keeps its own goal.
        /// </summary>
        public static bool TryWaypoint(float px, float py, float gx, float gy,
                                       out float waypointX, out float waypointY)
        {
            waypointX = 0.0f;
            waypointY = 0.0f;
            _frames++;
            if (!_available || _state == null)
            {
                return false;
            }
            if (float.IsNaN(gx) || float.IsNaN(gy) || float.IsInfinity(gx) || float.IsInfinity(gy))
            {
                return false;
            }

            int pxCell = (int)px;
            int pyCell = (int)py;
            if (pxCell < 0 || pyCell < 0 || pxCell >= _w || pyCell >= _h)
            {
                return false;
            }

            UpdateExploration(px, py);

            bool usable = _pathCount > 0 && _pathIndex < _pathCount
                          && _frames - _planFrame < ReplanFrames;
            if (usable && _mode == ModeGoal)
            {
                usable = Math.Abs(gx - _routeGoalX) <= GoalMoveRadius
                         && Math.Abs(gy - _routeGoalY) <= GoalMoveRadius;
            }
            if (usable && _mode == ModeExplore)
            {
                // THE COMMITTED FRONTIER IS NOT RE-CHOSEN WHEN THE GOAL MOVES. A boss that walks a
                // few units does not change which corridor of the dungeon is unexplored.
                usable = _exploreTarget >= 0;
            }
            if (usable)
            {
                // A ROUTE THE CHARACTER HAS STEPPED OFF IS NOT A ROUTE. The node it should be heading
                // for is the first one still ahead of it; if that is further than the deviation radius
                // the held route is stale (a knockback, a teleport, a wall the map revealed after the
                // plan) and it is replanned rather than followed.
                int next = NextAhead(px, py);
                if (next < 0)
                {
                    usable = false;
                }
                else
                {
                    float dx = CellX(_path[next]) - px;
                    float dy = CellY(_path[next]) - py;
                    if ((dx * dx) + (dy * dy) > DeviationRadius * DeviationRadius)
                    {
                        usable = false;
                    }
                }
            }

            if (!usable)
            {
                // STANDING ON THE FRONTIER: PUBLISH IT. Every node of the route is within
                // `AdvanceRadius`, and the route's job for the next second is to stand there and let
                // the map stream in rather than to walk away from the one place that can reveal it.
                if (_mode == ModeExplore && _exploreTarget >= 0)
                {
                    int t = _exploreTarget;
                    float dx = CellX(t) - px;
                    float dy = CellY(t) - py;
                    if ((dx * dx) + (dy * dy) <= AdvanceRadius * AdvanceRadius)
                    {
                        waypointX = CellX(t);
                        waypointY = CellY(t);
                        _waypoints++;
                        return true;
                    }
                }
                if (!Plan(pxCell, pyCell, gx, gy))
                {
                    _refusals++;
                    return false;
                }
            }

            int index = NextAhead(px, py);
            if (index < 0)
            {
                // The route was replanned above but is already behind the character: every node is
                // inside `AdvanceRadius`. On an exploration route that is the dwell at the frontier
                // and is published as such; anything else has nothing useful to say.
                if (_mode == ModeExplore && _exploreTarget >= 0)
                {
                    waypointX = CellX(_exploreTarget);
                    waypointY = CellY(_exploreTarget);
                    _waypoints++;
                    return true;
                }
                _refusals++;
                return false;
            }
            // WALK FORWARD ALONG THE ROUTE UNTIL THE WAYPOINT IS `WaypointAhead` AWAY, so the point
            // published is a direction along the route and not the route's next cell. A 1-cell
            // waypoint would make the character creep and the dodge re-plan on every step.
            int chosen = index;
            for (int i = index; i < _pathCount; i++)
            {
                chosen = i;
                float dx = CellX(_path[i]) - px;
                float dy = CellY(_path[i]) - py;
                if ((dx * dx) + (dy * dy) >= WaypointAhead * WaypointAhead)
                {
                    break;
                }
            }
            waypointX = CellX(_path[chosen]);
            waypointY = CellY(_path[chosen]);
            _waypoints++;
            return true;
        }

        /// <summary>
        /// The route is only ever published when the local planner cannot do the job, which is a
        /// decision for the caller: this is the predicate it uses, kept here so the two halves of the
        /// module's contract sit together. See `DodgeAdapter`'s call site for the argument.
        /// </summary>
        public static bool ShouldTakeOver(bool plannerBlind, float goalDistance)
        {
            return _available && (plannerBlind || goalDistance > 80.0f);
        }

        // ===================================================================================
        // The committed frontier walk
        // ===================================================================================

        /// <summary>
        /// Spends the committed frontier when it has been resolved, walked to and left dry, or held
        /// for too long. This is the module's whole memory: `_tried` is what makes a dead end cost
        /// one visit, and what makes the NEXT choice a step back toward the junction.
        /// </summary>
        private static void UpdateExploration(float px, float py)
        {
            if (_exploreTarget < 0)
            {
                return;
            }
            int t = _exploreTarget;
            if (_state[t] != Open)
            {
                FinishExplore(true);
                return;
            }
            if (!HasUnknownNeighbour(t))
            {
                // THE FRONTIER RESOLVED ITSELF: a tile arrived on one of its unknown neighbours. That
                // is a successful exploration, not a dead end, and it needs no dwell.
                FinishExplore(true);
                return;
            }
            float dx = CellX(t) - px;
            float dy = CellY(t) - py;
            if ((dx * dx) + (dy * dy) <= AdvanceRadius * AdvanceRadius)
            {
                if (_exploreArriveFrame == 0)
                {
                    _exploreArriveFrame = _frames;
                    _exploreKnownAtArrival = (int)_known;
                }
                else if (_frames - _exploreArriveFrame >= DwellFrames)
                {
                    if (_known > _exploreKnownAtArrival)
                    {
                        FinishExplore(true);
                    }
                    else
                    {
                        _tried[t] = 1;
                        _deadEnds++;
                        FinishExplore(false);
                    }
                }
            }
            else if (_frames - _exploreStartFrame >= ExploreTimeoutFrames)
            {
                _tried[t] = 1;
                _timeouts++;
                FinishExplore(false);
            }
        }

        private static void FinishExplore(bool explored)
        {
            if (explored)
            {
                _explored++;
            }
            _exploreTarget = -1;
            _exploreArriveFrame = 0;
            _mode = ModeNone;
            _pathCount = 0;
            _pathIndex = 0;
        }

        // ===================================================================================
        // The search
        // ===================================================================================

        /// <summary>
        /// One breadth-first walk from the character over the map's OPEN cells, which answers the two
        /// questions this module has in one pass:
        ///
        ///   * is the goal connected to the character over known floor? If it is, the route is the
        ///     chain to it -- the dungeon's actual question, answered by connectivity and not by a
        ///     distance;
        ///   * if it is not, which frontier is nearest along the floor? Depth is the metric, the goal
        ///     distance only a tie-break, because a dead end is cheap when it is reached first and
        ///     expensive when it is reached last.
        ///
        /// A NODE BUDGET, NOT A DISTANCE CAP. The budget bounds the frame cost; when it is reached the
        /// best frontier found so far is still a useful direction to walk.
        /// </summary>
        private static bool Plan(int startX, int startY, float gx, float gy)
        {
            if (_state == null || _seen == null || _parent == null || _depth == null || _queue == null)
            {
                return false;
            }
            int start = (startY * _w) + startX;
            if (_state[start] != Open)
            {
                // THE CHARACTER IS NOT ON KNOWN FLOOR: the first frame of a map, or a cell whose tile
                // has not arrived. Nothing can be planned from here; the caller keeps its own goal.
                return false;
            }

            _seenGeneration++;
            if (_seenGeneration == 0)
            {
                Array.Clear(_seen, 0, _seen.Length);
                _seenGeneration = 1;
            }
            _plans++;

            int goalCellX = (int)gx;
            int goalCellY = (int)gy;
            bool goalKnown = goalCellX >= 0 && goalCellY >= 0
                             && goalCellX < _w && goalCellY < _h
                             && _state[(goalCellY * _w) + goalCellX] == Open;
            int goalIndex = goalKnown ? (goalCellY * _w) + goalCellX : -1;

            int head = 0;
            int tail = 0;
            _queue[tail++] = start;
            _seen[start] = _seenGeneration;
            _parent[start] = -1;
            _depth[start] = 0;

            // The frontier candidate: nearest by path, tie-broken by distance to the goal.
            int best = -1;
            int bestDepth = int.MaxValue;
            float bestGoal2 = float.MaxValue;
            int reachedGoal = -1;
            int expanded = 0;

            while (head < tail && expanded < PlanNodeBudget)
            {
                int current = _queue[head++];
                expanded++;
                if (current == goalIndex)
                {
                    reachedGoal = current;
                    break;
                }
                if (_tried[current] == 0 && current != start && HasUnknownNeighbour(current))
                {
                    int depth = _depth[current];
                    float d2 = DistanceSquared(current, gx, gy);
                    if (depth < bestDepth || (depth == bestDepth && d2 < bestGoal2))
                    {
                        bestDepth = depth;
                        bestGoal2 = d2;
                        best = current;
                    }
                }
                int cx = current % _w;
                int cy = current / _w;
                int nextDepth = _depth[current] + 1;
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + (d == 0 ? 1 : (d == 1 ? -1 : 0));
                    int ny = cy + (d == 2 ? 1 : (d == 3 ? -1 : 0));
                    if (nx < 0 || ny < 0 || nx >= _w || ny >= _h)
                    {
                        continue;
                    }
                    int n = (ny * _w) + nx;
                    if (_seen[n] == _seenGeneration || _state[n] != Open)
                    {
                        continue;
                    }
                    _seen[n] = _seenGeneration;
                    _parent[n] = current;
                    _depth[n] = nextDepth;
                    if (tail < _queue.Length)
                    {
                        _queue[tail++] = n;
                    }
                }
            }

            int target;
            if (reachedGoal >= 0)
            {
                target = reachedGoal;
                _mode = ModeGoal;
                _goalPlans++;
                _routeGoalX = gx;
                _routeGoalY = gy;
                _exploreTarget = -1;
            }
            else
            {
                // THE COMMITTED FRONTIER SURVIVES A REPLAN. The path to it is recomputed (the map may
                // have grown, or the character may have been knocked off it) but the CHOICE is not
                // re-made: re-making it from the goal distance every frame is the ping-pong in the
                // header, and the whole point of `_tried` is that a choice, once made, is walked.
                if (_exploreTarget < 0)
                {
                    _exploreTarget = best;
                    _exploreStartFrame = _frames;
                    _exploreArriveFrame = 0;
                    _exploreKnownAtArrival = 0;
                }
                else if (_seen[_exploreTarget] != _seenGeneration)
                {
                    // THE BUDGET DID NOT REACH THE COMMITTED FRONTIER THIS PLAN, so its parent chain
                    // belongs to another search. Using it would walk a route that was never computed
                    // -- `_parent` is a plain array and a stale entry is a plausible number. The
                    // best frontier this search DID reach is the honest replacement.
                    if (best < 0)
                    {
                        _pathCount = 0;
                        _pathIndex = 0;
                        _mode = ModeNone;
                        return false;
                    }
                    _exploreTarget = best;
                    _exploreStartFrame = _frames;
                    _exploreArriveFrame = 0;
                    _exploreKnownAtArrival = 0;
                }
                target = _exploreTarget;
                _mode = ModeExplore;
            }
            if (target < 0)
            {
                // THE DUNGEON'S CONNECTED FLOOR IS MAPPED: no frontier is left untried, and the goal
                // is not on this floor. `false` is the honest answer -- the caller keeps its own goal
                // rather than this module inventing a waypoint on top of the character.
                _pathCount = 0;
                _pathIndex = 0;
                _mode = ModeNone;
                return false;
            }

            // THE CHAIN IS WALKED BACK FROM THE TARGET. `_path` is player-first, which is the order the
            // follower advances in. A breadth-first parent chain cannot loop, and the bound is the
            // map's own size so a corrupt parent can never spin the frame.
            int count = 0;
            for (int node = target; node >= 0 && count <= _state.Length; node = _parent[node])
            {
                count++;
            }
            if (count <= 0 || count > _state.Length)
            {
                return false;
            }
            if (_path.Length < count)
            {
                // Grow to the chain length once; a route is re-used for its lifetime.
                _path = new int[count < 16 ? 16 : count];
            }
            int index = count - 1;
            for (int node = target; node >= 0 && index >= 0; node = _parent[node])
            {
                _path[index--] = node;
            }
            _pathCount = count;
            _pathIndex = 0;
            _planFrame = _frames;
            return true;
        }

        /// <summary>
        /// True when any 4-neighbour of this OPEN cell has never been painted. That is a hard
        /// frontier -- the map has not arrived there at all -- and `NoWalk` rock is deliberately not
        /// one: it is a wall, the cache knows it is a wall, and treating it as "unexplored" is what
        /// kept the character against the Infested Cellar's east wall for six thousand frames.
        /// </summary>
        private static bool HasUnknownNeighbour(int index)
        {
            if (_state == null)
            {
                return false;
            }
            int x = index % _w;
            int y = index / _w;
            if (x + 1 < _w && _state[(y * _w) + x + 1] == Unknown) { return true; }
            if (x > 0 && _state[(y * _w) + x - 1] == Unknown) { return true; }
            if (y + 1 < _h && _state[((y + 1) * _w) + x] == Unknown) { return true; }
            if (y > 0 && _state[((y - 1) * _w) + x] == Unknown) { return true; }
            return false;
        }

        /// <summary>
        /// The index of the first route node still ahead of the character, dropping the nodes it has
        /// already walked past, or -1 when the route is exhausted.
        /// </summary>
        private static int NextAhead(float px, float py)
        {
            while (_pathIndex < _pathCount)
            {
                float dx = CellX(_path[_pathIndex]) - px;
                float dy = CellY(_path[_pathIndex]) - py;
                if ((dx * dx) + (dy * dy) > AdvanceRadius * AdvanceRadius)
                {
                    return _pathIndex;
                }
                _pathIndex++;
            }
            return -1;
        }

        private static float CellX(int index) => (index % _w) + 0.5f;

        private static float CellY(int index) => (index / _w) + 0.5f;

        private static float DistanceSquared(int index, float gx, float gy)
        {
            int cx = index % _w;
            int cy = index / _w;
            float dx = (cx + 0.5f) - gx;
            float dy = (cy + 0.5f) - gy;
            return (dx * dx) + (dy * dy);
        }
    }
}
