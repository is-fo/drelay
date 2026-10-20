// DungeonSolver.cs -- the dungeon's exploration policy: walk the known floor to the goal, and when
// the goal is not on the known floor yet, walk the map a frontier at a time and back out of dead
// ends.
//
// =====================================================================================
// THE TWO ANSWERS A DUNGEON ASKS
// =====================================================================================
//
//   1. IF THE KNOWN FLOOR CONNECTS TO THE GOAL, TAKE THE ROUTE. The boss marker is a point; the
//      map is what decides whether a way exists. Connectivity is answered by a breadth-first walk
//      over FLOOR cells (`DungeonMap.Search`), not by a distance -- a boss thirty units east behind
//      a wall may be eighty cells away along a U, and a distance gradient walks into the wall.
//
//   2. IF IT DOES NOT, EXPLORE. The target is the UNTRIED frontier nearest by path length, with
//      the goal distance only as a tie-break. Nearest-first is what makes a dead end cheap: the
//      stub is entered, found to be dry, spent, and the next choice is generally the junction it
//      came from. That is the backtrack, and `Map.MarkTried` is what makes it a decision rather
//      than a hope.
//
// The choice is COMMITTED. It is not re-derived from the goal's distance every frame -- that
// recomputation is exactly the two-point ping-pong measured in the Infested Cellar
// (`20261020-183555-p37604`: 6 039 frames shuttling between x 507.4 and x 520.6), and it is why
// the operator's "it retreats back into the spawn room" happened. The commitment survives the goal
// moving, because a boss that walks two units does not change which corridor has been explored.
//
// =====================================================================================
// HOW A DEAD END DIES
// =====================================================================================
//
// A frontier is a floor cell with an unpainted neighbour. It is spent in three ways, and each is a
// different measurement:
//
//   * IT RESOLVES ITSELF. A tile arrives on the unknown neighbour while the character stands there,
//     so the cell is no longer a frontier. That is a successful exploration, counted as `explored`.
//   * IT IS WALKED TO AND REVEALS NOTHING. The character stands on it for `DwellFrames` and the map
//     does not grow. It is marked TRIED and counted as a `dead end`. A dungeon does not reveal
//     itself to a character that keeps walking, so standing still beside the unknown is part of the
//     algorithm, not a stall.
//   * IT CANNOT BE REACHED. Walking to it took longer than `ExploreTimeoutFrames`; it is marked
//     TRIED and counted as a `timeout`. The dungeon's floor is unchanged; the character was simply
//     not going to get there.
//
// Marking TRIED is the whole backtrack. From the end of a dead end the nearest untried frontier is
// behind the character, `Search` finds the route back through the junction, and the module publishes
// it. A room with one entrance therefore costs one visit, whether the dead end is a stub corridor
// or a whole room.
//
// =====================================================================================
// IT IS A GOAL SOURCE, NOT A CONTROLLER
// =====================================================================================
//
// Like `Farmer`, `RouteRuntime` and the module it replaces, this class writes ONE point
// (`TryWaypoint`) and nothing else. The planner, admission, the score, the hysteresis and the
// escape floor are untouched: a dungeon narrows WHERE the character is walking, never whether a
// step is safe. That is also why it can be tested offline: the whole class is plain arrays,
// integers and a queue, compiled into `SolverHarness` with no game in sight.
//
// STATE IS INSTANCE STATE. There is no static cache, no global mode flag and no path shared between
// maps. `Bind`/`Reset` are the only transitions that touch the map, and every counter belongs to the
// instance. This is the part of the rewrite the old `MapRoute` got wrong: its map, its route and its
// exploration memory were static, so a test could not run two maps without resetting the world.

using System;

namespace Drelay.Bootstrap
{
    /// <summary>
    /// The dungeon walk: a committed frontier exploration over <see cref="DungeonMap"/>, with the
    /// route to a reachable goal taking priority. See the file header.
    /// </summary>
    public sealed class DungeonSolver
    {
        // --- The geometry, in world units -----------------------------------------------------

        /// <summary>Nodes the breadth-first walk may expand in one plan. Bounds the frame cost.</summary>
        public const int PlanNodeBudget = 40000;

        /// <summary>
        /// Nodes the optimistic A* may expand. Halved from the exact search's budget because every
        /// node also pays a heap operation; 20 000 is more than a dungeon's reachable ring needs and
        /// keeps a plan inside the frame budget when the map is large.
        /// </summary>
        public const int OptimisticNodeBudget = 20000;

        /// <summary>How far along the route the published waypoint sits, in world units.</summary>
        private const float WaypointAhead = 6.0f;

        /// <summary>A route node this close behind the character is dropped as walked past.</summary>
        private const float AdvanceRadius = 2.5f;

        /// <summary>Step off the route by more than this and it is replanned rather than followed.</summary>
        private const float DeviationRadius = 6.0f;

        /// <summary>Frames between replans even while the held route is still valid.</summary>
        private const int ReplanFrames = 30;

        /// <summary>The goal moving more than this invalidates a held goal route.</summary>
        private const float GoalMoveRadius = 6.0f;

        /// <summary>
        /// The goal is treated as reached when a reachable floor cell is this close to it, in world
        /// units. A marker can sit on a wall, a rock or an object's own occupied tile; the route is
        /// to the floor beside it, and the existing arrival policy does the last few units.
        /// </summary>
        private const float GoalNearUnits = 3.0f;

        /// <summary>
        /// Frames the character stands on a frontier before it is declared dry. One second at the
        /// measured frame rate: a dungeon cell's tile arrives tens of frames after the character
        /// came near it, and half a second was too short to tell "still arriving" from "dead end".
        /// </summary>
        private const int DwellFrames = 60;

        /// <summary>
        /// Frames the character stands on a BOUNDARY probe. One and a quarter seconds, not half:
        /// the measured streamer delay for a placeholder that becomes floor is up to seventy frames
        /// (`PLAN-AUTOFARM.md` section 14.12), and the 30-frame probe was leaving before the tile
        /// arrived. A failed probe is then spent with its neighbours; see `BoundarySpendRadius`.
        /// </summary>
        private const int BoundaryDwellFrames = 75;

        /// <summary>
        /// Boundary cells this close to a failed probe are spent with it. The streamer paints a
        /// neighbourhood, so if standing at one cell for a full dwell did not open the wall, probing
        /// the cell next to it is the same experiment again. This is what keeps a room's whole wall
        /// from costing one probe per cell.
        /// </summary>
        private const int BoundarySpendRadius = 2;

        /// <summary>
        /// Frames a frontier may be walked toward before it is abandoned as unreachable. Five
        /// seconds is several rooms; the counter exists so a target the character cannot physically
        /// reach costs five seconds and not the session.
        /// </summary>
        private const int ExploreTimeoutFrames = 300;

        // --- Modes ---------------------------------------------------------------------------

        private const byte ModeNone = 0;
        private const byte ModeGoal = 1;
        private const byte ModeExplore = 2;

        // --- State ---------------------------------------------------------------------------

        /// <summary>This dungeon's map. The only knowledge this solver has.</summary>
        public readonly DungeonMap Map = new DungeonMap();

        private byte _mode;
        private int[] _path = new int[0];
        private int _pathCount;
        private int _pathIndex;
        private long _planFrame;
        private long _frames;

        /// <summary>The node the held route ends at: the goal cell or the committed frontier.</summary>
        private int _target = -1;

        /// <summary>The committed frontier, or -1 when the route is a goal route.</summary>
        private int _committed = -1;

        /// <summary>
        /// True when the committed target is a BOUNDARY probe -- a floor cell beside a provisional
        /// `NoWalk` wall -- rather than a hard frontier. A sealed room has no hard frontiers at all,
        /// so this is the only thing that gets the character out of one; see the live measurement in
        /// `DUNGEON-SOLVER.md` section 8.
        /// </summary>
        private bool _committedBoundary;

        /// <summary>
        /// The last boundary probe that was chosen. It anchors the next probe to the same wall, so a
        /// room is swept along its perimeter instead of re-crossed: the second Infested Cellar
        /// (`20261020-230932-p36548`) spent roughly 400 frames per probe, mostly walking between
        /// boundary cells on opposite sides of a large component. The first probe of a sequence is
        /// still goal-directed; after that, locality wins and the goal is the tie-break.
        /// </summary>
        private int _lastProbe = -1;

        /// <summary>
        /// The first provisional wall on the optimistic route to the goal, or -1. The character
        /// walks to <see cref="_committed"/> (the floor cell before it) and waits: a floor
        /// observation clears the pending state and the route continues; a dry dwell marks the wall
        /// blocked and the optimistic route is recomputed around it.
        /// </summary>
        private int _pendingWall = -1;

        /// <summary>
        /// The breadth-first parent of the committed frontier: the cell the route came through.
        /// When the frontier stops being a frontier, a FLOOR neighbour other than this one means the
        /// map OPENED there; only this one means the corridor was painted closed. That distinction
        /// is what the old module could not make, because it stored walls and holes as the same
        /// byte and counted every closed stub as "resolved".
        /// </summary>
        private int _committedParent = -1;

        private long _exploreStartFrame;
        private long _dwellStartFrame;
        private long _dwellVersion;
        private long _dwellFloorCells;

        /// <summary>The last frontier spent as a dead end, for the backtrack counter.</summary>
        private int _spentNode = -1;

        private float _routeGoalX;
        private float _routeGoalY;
        private bool _mapped;

        // --- Counters, for the report and the harness ----------------------------------------

        private long _plans;
        private long _goalPlans;
        private long _waypoints;
        private long _refusals;
        private long _deadEnds;
        private long _timeouts;
        private long _backtracks;
        private long _explored;
        private long _boundaryProbes;

        public bool Available => Map.Available;
        public bool Mapped => _mapped;
        public long FloorCells => Map.FloorCells;
        public long Plans => _plans;
        public long GoalPlans => _goalPlans;
        public long Waypoints => _waypoints;
        public long Refusals => _refusals;
        public long DeadEnds => _deadEnds;
        public long Timeouts => _timeouts;
        public long Backtracks => _backtracks;
        public long Explored => _explored;
        public long BoundaryProbes => _boundaryProbes;
        public long Frames => _frames;

        /// <summary>The mode, for a harness assertion: "goal", "explore" or "none".</summary>
        public string ModeName =>
            _mode == ModeGoal ? "goal" : (_mode == ModeExplore ? "explore" : "none");

        /// <summary>One log line. Budgeted against the native log's 256-byte truncation.</summary>
        public string Status()
        {
            if (!Map.Available)
            {
                return "dungeon map OFF (" + Map.Reason + ")";
            }
            return "dungeon map ON (" + Map.Width + "x" + Map.Height
                   + ", " + Map.FloorCells + " floor, " + Map.WallCells + " wall, "
                   + Map.ProvisionalCells + " provisional, " + Map.FrontierCount() + " frontier, "
                   + Map.EdgeCount() + " edge"
                   + (_mapped ? " (mapped)" : "")
                   + ", " + _plans + " plan (" + _goalPlans + " goal), " + _waypoints + " waypoint, "
                   + _refusals + " refused, " + _deadEnds + " dead end, " + _timeouts + " timeout, "
                   + _backtracks + " backtrack, " + _explored + " explored)";
        }

        // ===================================================================================
        // Binding and observation
        // ===================================================================================

        /// <summary>
        /// Binds the map to this world. Forwarded to <see cref="DungeonMap.Bind"/>; a change of map
        /// forgets the route but not the counters, so a report spanning a world change still says
        /// what the previous map's walk did.
        /// </summary>
        public bool Bind(object mapKey, int width, int height)
        {
            bool bound = Map.Bind(mapKey, width, height);
            if (bound)
            {
                ForgetRoute();
                _mapped = false;
            }
            else if (!Map.Available)
            {
                ForgetRoute();
            }
            return bound;
        }

        /// <summary>One classified cell from the adapter's window. See <see cref="DungeonMap.Observe"/>.</summary>
        public void Observe(int x, int y, byte source)
        {
            long version = Map.Version;
            Map.Observe(x, y, source);
            if (Map.Version != version && _mapped)
            {
                // The map grew after it was declared fully mapped: it is worth exploring again.
                _mapped = false;
            }
        }

        /// <summary>
        /// Forgets the held route and the commitment, but keeps the map and the counters. Called on
        /// a world change and by `Reset`.
        /// </summary>
        public void ForgetRoute()
        {
            _mode = ModeNone;
            _pathCount = 0;
            _pathIndex = 0;
            _target = -1;
            _committed = -1;
            _committedParent = -1;
            _committedBoundary = false;
            _lastProbe = -1;
            _pendingWall = -1;
            _exploreStartFrame = 0;
            _dwellStartFrame = 0;
            _dwellVersion = 0;
            _dwellFloorCells = 0;
            _spentNode = -1;
        }

        /// <summary>Drops the map and every counter. For a test harness or a fresh session.</summary>
        public void Reset()
        {
            Map.Reset();
            _path = new int[0];
            _mode = ModeNone;
            _pathCount = 0;
            _pathIndex = 0;
            _target = -1;
            _committed = -1;
            _committedParent = -1;
            _committedBoundary = false;
            _lastProbe = -1;
            _pendingWall = -1;
            _planFrame = 0;
            _frames = 0;
            _exploreStartFrame = 0;
            _dwellStartFrame = 0;
            _dwellVersion = 0;
            _dwellFloorCells = 0;
            _spentNode = -1;
            _routeGoalX = 0.0f;
            _routeGoalY = 0.0f;
            _mapped = false;
            _plans = 0;
            _goalPlans = 0;
            _waypoints = 0;
            _refusals = 0;
            _deadEnds = 0;
            _timeouts = 0;
            _backtracks = 0;
            _explored = 0;
            _boundaryProbes = 0;
        }

        /// <summary>
        /// Only a goal the local planner cannot aim at is worth this module's map-scale answer.
        /// The decision is the caller's; this is the same predicate kept beside the module's
        /// contract so the two halves do not drift.
        /// </summary>
        public bool ShouldTakeOver(bool plannerBlind, float goalDistance)
        {
            return Map.Available && (plannerBlind || goalDistance > 80.0f);
        }

        // ===================================================================================
        // The waypoint
        // ===================================================================================

        /// <summary>
        /// The next point to walk at, or false when this module has nothing useful to say. False is
        /// an answer: the caller keeps its own goal rather than this module inventing a waypoint.
        /// </summary>
        public bool TryWaypoint(float playerX, float playerY, float goalX, float goalY,
                                out float waypointX, out float waypointY)
        {
            waypointX = 0.0f;
            waypointY = 0.0f;
            _frames++;

            if (!Map.Available)
            {
                _refusals++;
                return false;
            }
            if (float.IsNaN(goalX) || float.IsNaN(goalY)
                || float.IsInfinity(goalX) || float.IsInfinity(goalY)
                || float.IsNaN(playerX) || float.IsNaN(playerY))
            {
                _refusals++;
                return false;
            }
            int playerCellX = (int)playerX;
            int playerCellY = (int)playerY;
            if (!Map.Inside(playerCellX, playerCellY))
            {
                _refusals++;
                return false;
            }

            // THE COMMITTED FRONTIER IS SPENT BEFORE THE ROUTE IS TRUSTED. A frontier that resolved
            // itself, or one the character has stood on while the map did not grow, changes the
            // question this frame is answering.
            UpdateCommitted(playerX, playerY);

            bool usable = _pathCount > 0 && _pathIndex < _pathCount
                          && _frames - _planFrame < ReplanFrames;
            if (usable && _mode == ModeGoal)
            {
                usable = Math.Abs(goalX - _routeGoalX) <= GoalMoveRadius
                         && Math.Abs(goalY - _routeGoalY) <= GoalMoveRadius;
            }
            if (usable && _mode == ModeExplore)
            {
                usable = _committed >= 0;
            }
            if (usable)
            {
                // A ROUTE THE CHARACTER HAS STEPPED OFF IS NOT A ROUTE. A knockback, a teleport or a
                // wall the map revealed after the plan all look the same from here: the next node is
                // farther than the deviation radius, so the route is replanned rather than followed.
                int next = NextAhead(playerX, playerY);
                if (next < 0)
                {
                    usable = false;
                }
                else if (Map.DistanceSquared(_path[next], playerX, playerY)
                         > DeviationRadius * DeviationRadius)
                {
                    usable = false;
                }
            }

            if (!usable)
            {
                // STANDING ON THE FRONTIER: PUBLISH IT. The character waits beside the unknown for
                // the streamer to paint, which is what `UpdateCommitted` is counting. Publishing the
                // cell itself is what makes the existing arrival policy stand there.
                if (_mode == ModeExplore && _committed >= 0
                    && Map.Distance(_committed, playerX, playerY) <= AdvanceRadius)
                {
                    waypointX = Map.CellX(_committed);
                    waypointY = Map.CellY(_committed);
                    _waypoints++;
                    return true;
                }
                if (!Plan(playerCellX, playerCellY, goalX, goalY))
                {
                    _refusals++;
                    return false;
                }
            }

            int index = NextAhead(playerX, playerY);
            if (index < 0)
            {
                if (_mode == ModeExplore && _committed >= 0)
                {
                    // Every node of the route is under the character: this is the dwell.
                    waypointX = Map.CellX(_committed);
                    waypointY = Map.CellY(_committed);
                    _waypoints++;
                    return true;
                }
                if (_mode == ModeGoal && _target >= 0
                    && Map.Distance(_target, playerX, playerY) <= AdvanceRadius)
                {
                    waypointX = Map.CellX(_target);
                    waypointY = Map.CellY(_target);
                    _waypoints++;
                    return true;
                }
                _refusals++;
                return false;
            }

            // WALK FORWARD ALONG THE ROUTE UNTIL THE WAYPOINT IS `WaypointAhead` AWAY, so the point
            // published is a direction along the route and not the route's next cell. A 1-cell
            // waypoint makes the character creep and the dodge replan on every step.
            int chosen = index;
            for (int i = index; i < _pathCount; i++)
            {
                chosen = i;
                if (Map.DistanceSquared(_path[i], playerX, playerY) >= WaypointAhead * WaypointAhead)
                {
                    break;
                }
            }
            waypointX = Map.CellX(_path[chosen]);
            waypointY = Map.CellY(_path[chosen]);
            _waypoints++;
            return true;
        }

        // ===================================================================================
        // Exploration memory
        // ===================================================================================

        /// <summary>
        /// Spends the committed frontier when it has resolved, been walked to and left dry, or been
        /// held too long. This is the module's memory: `MarkTried` is what makes a dead end cost one
        /// visit, and what makes the next choice a step back toward the junction.
        /// </summary>
        private void UpdateCommitted(float playerX, float playerY)
        {
            if (_committed < 0)
            {
                return;
            }
            int target = _committed;
            if (_pendingWall >= 0 && Map.KindAt(_pendingWall) == DungeonMap.Floor)
            {
                // THE WALL WAS A DOORWAY. The tile arrived while the character approached; drop the
                // pending probe and let the next plan route through the new floor.
                _pendingWall = -1;
                FinishExplore(true);
                return;
            }
            if (Map.KindAt(target) != DungeonMap.Floor)
            {
                // A floor cell is never downgraded, so this is only reachable if the map was rebound
                // between frames. Releasing the commitment is safer than routing from another map.
                FinishExplore(false);
                return;
            }
            if (!Map.HasUnknownNeighbour(target) && !Map.HasProvisionalNeighbour(target))
            {
                // THE FRONTIER IS NO LONGER A FRONTIER. Two things can have happened, and they are
                // the operator's two outcomes: the map OPENED here (a floor neighbour other than
                // the way in), which is a successful exploration; or the placeholder was painted
                // closed, which is a DEAD END. The old cache could not tell them apart because a
                // wall and a hole were one byte, and it counted every closed stub as explored.
                if (OpenedAround(target, _committedParent))
                {
                    FinishExplore(true);
                }
                else
                {
                    _deadEnds++;
                    _spentNode = target;
                    FinishExplore(false);
                }
                return;
            }
            float distance = Map.Distance(target, playerX, playerY);
            if (distance <= AdvanceRadius)
            {
                if (_dwellStartFrame == 0)
                {
                    _dwellStartFrame = _frames;
                    _dwellVersion = Map.Version;
                    _dwellFloorCells = Map.FloorCells;
                }
                else if (_frames - _dwellStartFrame
                         >= (_committedBoundary ? BoundaryDwellFrames : DwellFrames))
                {
                    if (Map.Version != _dwellVersion || Map.FloorCells > _dwellFloorCells)
                    {
                        FinishExplore(true);
                    }
                    else
                    {
                        if (_pendingWall >= 0)
                        {
                            Map.MarkBlocked(_pendingWall);
                            _pendingWall = -1;
                        }
                        SpendBoundary(target);
                        _deadEnds++;
                        _spentNode = target;
                        FinishExplore(false);
                    }
                }
            }
            else if (_frames - _exploreStartFrame >= ExploreTimeoutFrames)
            {
                if (_pendingWall >= 0)
                {
                    Map.MarkBlocked(_pendingWall);
                    _pendingWall = -1;
                }
                SpendBoundary(target);
                _timeouts++;
                _spentNode = target;
                FinishExplore(false);
            }
        }

        /// <summary>
        /// Spends a failed probe. A boundary probe is a neighbourhood experiment, so its nearby
        /// boundary cells are spent with it; a hard frontier is a single specific hole and is spent
        /// alone.
        /// </summary>
        private void SpendBoundary(int target)
        {
            if (_committedBoundary)
            {
                Map.MarkTriedAround(target, BoundarySpendRadius);
            }
            else
            {
                Map.MarkTried(target);
            }
        }

        private void FinishExplore(bool explored)
        {
            if (explored)
            {
                _explored++;
            }
            _committed = -1;
            _committedParent = -1;
            _committedBoundary = false;
            _pendingWall = -1;
            _target = -1;
            _mode = ModeNone;
            _pathCount = 0;
            _pathIndex = 0;
            _dwellStartFrame = 0;
            _dwellVersion = 0;
            _dwellFloorCells = 0;
        }

        /// <summary>
        /// True when the frontier that just stopped being one has a FLOOR neighbour other than the
        /// cell the route came through. That is the map opening at the frontier rather than a stub
        /// being painted closed; see <see cref="_committedParent"/>.
        /// </summary>
        private bool OpenedAround(int index, int parent)
        {
            int x = index % Map.Width;
            int y = index / Map.Width;
            if (x + 1 < Map.Width && (index + 1) != parent && Map.KindAt(index + 1) == DungeonMap.Floor)
            {
                return true;
            }
            if (x > 0 && (index - 1) != parent && Map.KindAt(index - 1) == DungeonMap.Floor)
            {
                return true;
            }
            if (y + 1 < Map.Height && (index + Map.Width) != parent
                && Map.KindAt(index + Map.Width) == DungeonMap.Floor)
            {
                return true;
            }
            if (y > 0 && (index - Map.Width) != parent
                && Map.KindAt(index - Map.Width) == DungeonMap.Floor)
            {
                return true;
            }
            return false;
        }

        // ===================================================================================
        // Planning
        // ===================================================================================

        private bool Plan(int playerCellX, int playerCellY, float goalX, float goalY)
        {
            DungeonMap.SearchResult search = Map.Search(playerCellX, playerCellY, goalX, goalY,
                                                         PlanNodeBudget);
            _plans++;
            if (!search.Started)
            {
                // THE CHARACTER IS NOT ON KNOWN FLOOR: the first frames of a map, or a cell whose
                // tile has not arrived. Nothing can be planned from here; the caller keeps its goal.
                return false;
            }

            if (search.GoalNode >= 0)
            {
                return StartGoalRoute(search.GoalNode, goalX, goalY, playerCellX, playerCellY);
            }
            if (search.NearestGoalNode >= 0
                && search.NearestGoalSquared <= GoalNearUnits * GoalNearUnits)
            {
                // THE GOAL IS BESIDE KNOWN FLOOR. A marker can sit on a wall or on its own occupied
                // tile; the route is to the floor within reach of it, and the existing arrival policy
                // covers the last few units.
                return StartGoalRoute(search.NearestGoalNode, goalX, goalY, playerCellX, playerCellY);
            }

            // THE MOUSE'S MOVE: PLAN THROUGH THE PLACEHOLDER TOWARD THE GOAL. The exact search has
            // said there is no floor route; this search treats `NoWalk` as a possible doorway and
            // returns the first one on the shortest route. The character walks to the floor cell
            // before it and waits. A conversion opens the route; a dry dwell blocks that wall and
            // the next plan goes around it.
            if (_committed < 0)
            {
                int approach;
                int wall;
                if (Map.SearchOptimistic(playerCellX, playerCellY, goalX, goalY,
                                         OptimisticNodeBudget, out approach, out wall))
                {
                    _committed = approach;
                    _pendingWall = wall;
                    _committedBoundary = true;
                    _lastProbe = approach;
                    _exploreStartFrame = _frames;
                    _dwellStartFrame = 0;
                    _boundaryProbes++;
                    _mode = ModeExplore;
                    _target = approach;
                    _mapped = false;
                    if (!SetPath(approach))
                    {
                        FinishExplore(false);
                        return false;
                    }
                    _committedParent = Map.ParentOf(approach);
                    return true;
                }
            }

            // THE GOAL IS NOT CONNECTED TO THE KNOWN FLOOR. Explore, committing to a frontier.
            int target;
            if (_committed >= 0)
            {
                target = _committed;
            }
            else
            {
                target = search.FrontierNode;
                _committedBoundary = false;
                if (target < 0)
                {
                    // NO HARD FRONTIER: EVERY EDGE OF THE KNOWN FLOOR HAS BEEN PAINTED. That is a
                    // sealed room, and in a streaming dungeon the seal is usually a `NoWalk`
                    // placeholder over the doorway. Probe the boundary instead; see
                    // `ChooseBoundary` and the live River Maze/Chasm of Dread measurement.
                    target = ChooseBoundary(playerCellX, playerCellY, goalX, goalY);
                    _committedBoundary = target >= 0;
                }
                if (target < 0)
                {
                    // THE DUNGEON'S CONNECTED FLOOR IS MAPPED: no untried frontier or boundary is
                    // left and the goal is not on this floor. False is the honest answer; the
                    // caller keeps its own goal rather than this module inventing a waypoint on top
                    // of the character.
                    _mapped = true;
                    FinishExplore(false);
                    return false;
                }
                _committed = target;
                _exploreStartFrame = _frames;
                _dwellStartFrame = 0;
                if (_committedBoundary)
                {
                    _lastProbe = target;
                    _boundaryProbes++;
                }
                else
                {
                    _lastProbe = -1;
                }
            }

            _mode = ModeExplore;
            _target = target;
            if (!SetPath(target))
            {
                // The frontier was not reached by this search (the node budget stopped before it, or
                // the map changed under the search). Release it and let the next frame choose again.
                _mapped = false;
                FinishExplore(false);
                return false;
            }
            _committedParent = Map.ParentOf(target);
            CountBacktrack(playerCellX, playerCellY);
            return true;
        }

        /// <summary>
        /// The boundary probe to walk when no hard frontier is reachable: a floor cell beside a
        /// provisional `NoWalk` wall.
        ///
        /// THE FIRST PROBE IS GOAL-DIRECTED, THEN THE WALL IS SWEPT. A disconnected floor patch on
        /// the far side of a placeholder is the strongest doorway signal, so the first probe prefers
        /// it and otherwise heads toward the goal. Once a boundary sequence is running, the next
        /// probe is the nearest unspent boundary cell to the PLAYER: walking to the far side of a
        /// large room for every probe is what made the second Infested Cellar spend roughly 400
        /// frames per probe, and a perimeter sweep is the same experiment in a fraction of the
        /// travel. The goal remains the tie-break, so a fork still prefers the boss side.
        /// </summary>
        private int ChooseBoundary(int playerCellX, int playerCellY, float goalX, float goalY)
        {
            int count = Map.BoundaryCount;
            if (count <= 0)
            {
                return -1;
            }
            int best = -1;
            float bestScore = float.MaxValue;
            float bestGoal = float.MaxValue;
            bool sweep = _lastProbe >= 0;
            for (int i = 0; i < count; i++)
            {
                int index = Map.BoundaryAt(i);
                if (index < 0 || Map.IsTried(index) || Map.KindAt(index) != DungeonMap.Floor)
                {
                    continue;
                }
                float goal2 = Map.DistanceSquared(index, goalX, goalY);
                float outside = NearestDisconnectedFloor(index);
                float score;
                if (sweep)
                {
                    // SWEEP THE WALL, DO NOT CROSS THE ROOM. Once a boundary sequence is running,
                    // the next probe is the nearest unspent boundary cell; the goal is a tiny
                    // tie-break so a fork prefers the boss side. This is what turns a room's wall
                    // from N independent cross-room walks into one perimeter pass.
                    float player2 = Map.DistanceSquared(index, playerCellX + 0.5f, playerCellY + 0.5f);
                    score = player2 + (goal2 * 0.001f) + (outside >= 0.0f ? -0.25f : 0.0f);
                }
                else
                {
                    // THE FIRST PROBE IS GOAL-DIRECTED. A disconnected floor patch on the far side
                    // of the wall is the strongest doorway signal; without one, the goal direction
                    // is the only useful ranking.
                    score = goal2 + (outside >= 0.0f ? 0.0f : 1000000000.0f);
                }
                if (score < bestScore || (score == bestScore && goal2 < bestGoal))
                {
                    bestScore = score;
                    bestGoal = goal2;
                    best = index;
                }
            }
            return best;
        }

        /// <summary>
        /// Squared distance from a boundary cell to the nearest FLOOR cell the last search did not
        /// reach, searched in a 17x17 box; -1 when there is none in the box. A disconnected floor
        /// patch on the other side of a provisional wall is the strongest available evidence that
        /// the wall is a doorway placeholder and not rock.
        /// </summary>
        private float NearestDisconnectedFloor(int index)
        {
            int x = index % Map.Width;
            int y = index / Map.Width;
            const int Radius = 8;
            float best = float.MaxValue;
            for (int dy = -Radius; dy <= Radius; dy++)
            {
                int ny = y + dy;
                if (ny < 0 || ny >= Map.Height)
                {
                    continue;
                }
                for (int dx = -Radius; dx <= Radius; dx++)
                {
                    int nx = x + dx;
                    if (nx < 0 || nx >= Map.Width)
                    {
                        continue;
                    }
                    int n = (ny * Map.Width) + nx;
                    if (Map.KindAt(n) != DungeonMap.Floor || Map.WasReached(n))
                    {
                        continue;
                    }
                    float d2 = (dx * dx) + (dy * dy);
                    if (d2 < best)
                    {
                        best = d2;
                    }
                }
            }
            return best == float.MaxValue ? -1.0f : best;
        }

        private bool StartGoalRoute(int target, float goalX, float goalY,
                                    int playerCellX, int playerCellY)
        {
            // A GOAL ROUTE IS NOT AN EXPLORATION. The commitment is dropped, so a boss that moves or
            // a tile that arrives cannot leave a stale frontier behind it.
            _mode = ModeGoal;
            _target = target;
            _committed = -1;
            _committedParent = -1;
            _committedBoundary = false;
            _lastProbe = -1;
            _pendingWall = -1;
            _exploreStartFrame = 0;
            _dwellStartFrame = 0;
            _dwellVersion = 0;
            _dwellFloorCells = 0;
            _routeGoalX = goalX;
            _routeGoalY = goalY;
            _mapped = false;
            _goalPlans++;
            if (!SetPath(target))
            {
                return false;
            }
            _spentNode = -1;
            return true;
        }

        private bool SetPath(int target)
        {
            int count = Map.BuildPath(target);
            if (count <= 0)
            {
                return false;
            }
            _pathCount = count;
            _pathIndex = 0;
            _planFrame = _frames;
            if (_path.Length < count)
            {
                // Grow once to the longest route this map has needed; a route is re-used for its
                // lifetime. Allocating a fresh array per plan would put up to 40 000 ints on the
                // frame path every replan, which is a GC pause inside a patched game frame.
                _path = new int[count < 16 ? 16 : count];
            }
            for (int i = 0; i < count; i++)
            {
                _path[i] = Map.PathNodeAt(i);
            }
            return true;
        }

        /// <summary>
        /// Counts the step OUT of a dead end. The spent frontier is compared with the first node of
        /// the next route: if that node is farther from it than the player is, the character is
        /// walking away from the stub, which is the backtrack. This is a measurement for the report,
        /// not a rule -- the route itself comes from the breadth-first walk and is correct either
        /// way.
        /// </summary>
        private void CountBacktrack(int playerCellX, int playerCellY)
        {
            if (_spentNode < 0 || _pathCount < 2)
            {
                _spentNode = -1;
                return;
            }
            float before = Map.Distance(_spentNode, playerCellX + 0.5f, playerCellY + 0.5f);
            float after = Map.Distance(_spentNode, Map.CellX(_path[1]), Map.CellY(_path[1]));
            if (after > before + 0.5f)
            {
                _backtracks++;
            }
            _spentNode = -1;
        }

        private int NextAhead(float playerX, float playerY)
        {
            while (_pathIndex < _pathCount)
            {
                if (Map.DistanceSquared(_path[_pathIndex], playerX, playerY)
                    > AdvanceRadius * AdvanceRadius)
                {
                    return _pathIndex;
                }
                _pathIndex++;
            }
            return -1;
        }
    }
}
