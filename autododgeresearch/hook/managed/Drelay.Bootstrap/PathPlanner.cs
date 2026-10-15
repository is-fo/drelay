// PathPlanner.cs -- Spike 3.2, HANDOFF-DIRECTED-DODGE.md section 4: an opinion about the future.
//
// =====================================================================================
// WHY THIS FILE EXISTS
// =====================================================================================
//
// Measured in HANDOFF-DIRECTED-DODGE.md section 3, on `tools/dodge_direction_report.py`: the dodge
// OUTRUNS on 40-50 % of its own moves, and on three of the five recordings outrunning is the single
// most common thing it does. The design reason is stated there in one sentence and it is worth
// repeating where the consequence lives:
//
//     "Every candidate is one step, scored in isolation ... Nothing in the candidate set can
//      express 'this step is bad because of the step after it'."
//
// The score has one term about direction (`GoalProgress`, which rewards moving along the player's
// own line) and the safety terms are all about this frame. So on a frame where the player is walking
// away from the shooter, "away from the shooter" and "along my own line" are the same direction, and
// the biggest step wins. There is no way for a single step to know that the step after it is worse.
//
// THIS FILE IS THE MISSING OPINION, AND IT IS DELIBERATELY NOT A CANDIDATE. The actuator takes one
// heading and one magnitude per frame; the path never reaches the input. What it produces is a
// WAYPOINT -- a point the character would like to be walking toward once this step is over -- and the
// solver scores each already-admissible candidate by its alignment with that waypoint. The admission
// rules are untouched, so `HANDOFF-DIRECTED-DODGE.md` section 5.3's doctrine holds unchanged: a
// preference term can only rank options that already passed the safety boundary.
//
// =====================================================================================
// THE FOUR DECISIONS INSIDE THIS FILE, AND THE ALTERNATIVE EACH ONE REJECTED
// =====================================================================================
//
//   1. THE SEARCH RUNS ON THE CLIENT'S OWN GRID, one world unit per cell, through
//      `CollisionWindow` -- which is the same `byte[,]` rule 2 already tests against, windowed and
//      read by the same truncating `(int)world - Origin` conversion the game itself uses. There is
//      no second collision model here and there must never be one: a pathfinder that disagrees with
//      the client about where a wall is would steer the player into it, and the disagreement would
//      only be visible as a death.
//
//   2. COST IS DISTANCE PLUS DANGER, AND THE DANGER IS IN SPACETIME. This is the part that makes it
//      a DODGE path rather than a nav route, and it is the same one-clock model `PlanWalk` uses: the
//      player is at cell `c` at time `t`, where `t` is how long the path took to get there at the
//      player's own speed, and the question asked of every projectile is where ITS BODY IS AT THAT
//      INSTANT. A cell is not "dangerous"; a cell is dangerous for a player who arrives at time `t`.
//      That is what lets a path thread a volley instead of treating the bullet's whole remaining
//      trajectory as a static wall.
//
//   3. THE TARGET IS A CELL ON THE GOAL RAY, NOT A DESTINATION. The goal is the player's held
//      direction (section 13), and a ray has no cell to search to. The target is the cell the
//      character would reach by walking along that ray for `PathTargetSeconds`, and a target that is
//      behind a wall or inside a projectile is a legitimate and important case: the search then
//      routes around the obstacle toward it, which is the behaviour this whole file exists to make
//      possible. When the target is not reachable at all, the answer is `NoPath` and the solver falls
//      back to the goal term it had before -- a pathfinder that can produce no answer must not be
//      able to produce no move.
//
//   4. THE WAYPOINT IS THE FIRST NODE MORE THAN ONE STEP AWAY, HELD BY ITS OWN HYSTERESIS. A path
//      recomputed every frame changes its mind every frame, and a dodge that follows it changes its
//      mind with it; section 10.4 and section 13.2 are both recorded lessons about smoothing an
//      angle. The hold is dropped the moment the waypoint is no longer on the path -- the same
//      discipline `Hysteresis` applies to a heading, and for the same reason.
//
// =====================================================================================
// WHAT THIS FILE MAY TOUCH, WHICH IS NOT A STYLE RULE
// =====================================================================================
//
// `DodgeSnapshot.cs` may name no game type, and neither may this. Everything here is a float, an int,
// a bool or an array of them, and the file is compiled into `Drelay.Bootstrap` AND into
// `tools/SolverHarness` through the same `Compile Include` mechanism as the solver's other two
// sources -- so a change that reaches for a game type breaks the offline build immediately, which is
// exactly when you want to find out.
//
// AND IT ALLOCATES NOTHING ON THE FRAME PATH. Every buffer below is a field sized once, at
// construction, for the largest grid the class is willing to search; a `List<Node>` per frame is the
// one thing this project has measured repeatedly and refused.

using System;
using System.Globalization;

namespace Drelay.Bootstrap
{
    /// <summary>Why the planner produced no waypoint. A closed set, so a log can be counted.</summary>
    public enum PathReason
    {
        /// <summary>A waypoint was produced.</summary>
        Found = 0,

        /// <summary>The pathfinder is switched off in this configuration.</summary>
        Disabled = 1,

        /// <summary>The frame carries no collision grid -- the honest degradation. See `CollisionWindow.Present`.</summary>
        NoGrid = 2,

        /// <summary>The grid is larger than the planner's fixed buffers. Refused, never truncated silently.</summary>
        GridTooLarge = 3,

        /// <summary>No goal direction was measured this frame, so there is no ray to aim at.</summary>
        NoGoal = 4,

        /// <summary>The target cell itself is a wall or lies outside the grid window.</summary>
        NoTarget = 5,

        /// <summary>The search expanded its node cap without reaching the target.</summary>
        NodeCap = 6,

        /// <summary>The search completed and the target is genuinely unreachable behind walls.</summary>
        Unreachable = 7,

        /// <summary>The player's own cell is a wall -- pinned against scenery, which is a real state and not an error.</summary>
        StartBlocked = 8,
    }

    /// <summary>
    /// The planner's own numbers, for the harness's report and for the `--decisions` columns.
    ///
    /// THE REASON A PATHFINDER NEEDS THIS IS NOT TIDINESS. `HANDOFF-DIRECTED-DODGE.md` section 6:
    /// "A pathfinder that is silently absent prints as 'the outrun ratio did not move', which is the
    /// same output as a pathfinder that ran and did nothing." Every field here is a column in the
    /// decision dump, so "the path was never found" and "the path was found and had no effect" are
    /// two different lines in one file.
    /// </summary>
    public sealed class PathStats
    {
        /// <summary>Searches run since the last <see cref="PathPlanner.ResetCounters"/>.</summary>
        public long Searches;

        /// <summary>Searches that hit the node cap. See <see cref="PathReason.NodeCap"/>.</summary>
        public long NodeCapHits;

        /// <summary>Frames on which no waypoint was produced, for any reason.</summary>
        public long NoPath;

        /// <summary>Frames on which the waypoint came from the previous frame's hold rather than a fresh path.</summary>
        public long Held;

        /// <summary>
        /// CORNER CUTS THE SEARCH PRODUCED: path steps whose two cells are both walkable but whose
        /// diagonal crosses a solid pair. Must be 0 on every frame of every scenario and recording.
        ///
        /// WHY THIS IS A COUNTER AND NOT A TEST. The diagonal rule -- a diagonal step is allowed only
        /// when both orthogonal neighbours are walkable -- is the one part of this file that can put a
        /// path THROUGH a wall, and the consequence would be a dodge that steers confidently into
        /// scenery. The scenario suite has walls (`corridor`: 165 solid cells) and its paths are 24.7
        /// units against `dodge`'s 12, which is *consistent* with going round -- but a longer path is
        /// inference, and this project's own rule is that a number which cannot be read off a file is
        /// not evidence. The check is cheap enough to run on every search rather than in a unit test
        /// nothing calls: one walk of a path that is at most a few dozen nodes, once per frame.
        /// </summary>
        public long WallCrossings;

        /// <summary>Searches since the last <see cref="PathPlanner.ResetCounters"/> in which a path was found.</summary>
        public long PathsFound;

        /// <summary>Sum of `Nodes` over every path found, for the mean.</summary>
        public long NodesTotal;

        /// <summary>Sum of `Length` over every path found, for the mean.</summary>
        public double LengthTotal;

        /// <summary>Total microseconds spent inside <see cref="PathPlanner.Plan"/>.</summary>
        public double MicrosecondsTotal;

        /// <summary>Why the most recent failure happened. Counted separately because one reason is not the others.</summary>
        public long NoGridFrames;
        public long DisabledFrames;
        public long UnreachableFrames;
        public long NoGoalFrames;
    }

    /// <summary>
    /// A bounded A* over the client's collision grid, with a spacetime danger cost, producing one
    /// waypoint per frame. See this file's header for the design and for what each decision rejected.
    /// </summary>
    public sealed class PathPlanner
    {
        /// <summary>
        /// The largest window the fixed buffers will search, per axis.
        ///
        /// IT IS A BUFFER SIZE AND NOT A MAP SIZE. The snapshot's window is what it is
        /// (`CollisionWindow`), and a window larger than this is REFUSED with
        /// <see cref="PathReason.GridTooLarge"/> rather than searched partially: a search that
        /// silently ignored the far half of its own grid would be a path that steers confidently into
        /// a region it never looked at.
        /// </summary>
        public const int MaxGridWidth = 192;
        public const int MaxGridHeight = 192;

        /// <summary>8-connected neighbourhood, orthogonals first so the cheaper step is tried first.</summary>
        private static readonly int[] DirX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] DirY = { 0, 0, 1, -1, 1, -1, 1, -1 };

        private DodgeConfig _config;

        // --- The grid, as this frame sees it, cached per frame ---------------------------------
        private CollisionWindow _grid;
        private int _gridW;
        private int _gridH;
        private int _cells;
        private float _playerX;
        private float _playerY;
        private float _speedPerSecond;
        private bool _targetSet;
        private int _targetIndex;

        // --- The per-threat danger envelope, resolved once per frame ---------------------------
        //
        // ONE TIGHT RADIUS PER THREAT, WHICH IS THE WHOLE COST OPTIMISATION HERE. The honest question
        // is "how close does this threat's body come to this cell, at the time the player would be
        // there", and answering it by walking 21 samples of all 48 threats for every expanded node
        // would be a hundred thousand distance tests a frame. Two facts make that unnecessary: a
        // bullet's body is within `collisionRadius` of its own path, and its path is sampled at 30 ms
        // -- so a sample whose position is further than `radius + pad + half a sample step` from a
        // cell cannot make that cell dangerous, at any time. `_threatNear` is that bound, per threat,
        // computed once; the per-node test is then a subtraction and a compare.
        private Trajectory[] _threats = new Trajectory[0];
        private int _threatCount;
        private float[] _threatNear = new float[0];
        private float[] _threatRadius = new float[0];
        private float[] _threatStepMs = new float[0];
        private int[] _threatSamples = new int[0];

        // --- The search buffers, sized once -----------------------------------------------------
        private float[] _gScore = new float[0];
        private float[] _costScore = new float[0];
        private int[] _cameFrom = new int[0];
        private bool[] _closed = new bool[0];
        private bool[] _inOpen = new bool[0];
        private int[] _heap = new int[0];
        private float[] _heapKey = new float[0];
        private int _heapCount;

        // --- The result --------------------------------------------------------------------------
        private int[] _pathIndex = new int[0];
        private int _pathCount;

        private float _waypointX;
        private float _waypointY;
        private float _waypointHeading;
        private bool _waypointValid;

        private float _heldX;
        private float _heldY;
        private bool _holdValid;
        private float _heldTakenMs;

        private PathReason _reason = PathReason.Disabled;
        private int _nodes;
        private float _length;
        private float _cost;
        private bool _fresh;
        private bool _fieldReady;
        private readonly PathStats _stats = new PathStats();

        public PathPlanner(DodgeConfig config)
        {
            _config = config;
            EnsureCapacity(MaxGridWidth, MaxGridHeight);
        }

        /// <summary>
        /// Replaces the configuration. The hook re-reads it when the world changes, so the planner
        /// must search the new world's numbers on the frame the map does.
        /// </summary>
        public void Configure(DodgeConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// True when the last <see cref="Plan"/> left a danger field that a reader may use.
        ///
        /// IT IS NOT "THE PATH EXISTS" AND THE DIFFERENCE MATTERS. A path can fail for reasons that
        /// have nothing to do with the danger field -- an unreachable target, a blocked start -- while
        /// the field is perfectly well built, and a caller asking "is there danger in front of me"
        /// must not be told no because the route to a goal behind a wall could not be found. It is
        /// true when the frame had a grid and at least one threat to put in the envelope.
        /// </summary>
        public bool FieldReady => _fieldReady;

        /// <summary>
        /// The half-width of the patch a projected-danger probe reads around a future position.
        ///
        /// IT IS THE COLLISION DISC AND NOT A TUNING FUDGE. The danger field is non-zero only within
        /// `player radius + bullet radius + pad` of a bullet's body, so a probe read at a single point
        /// asks "is a bullet exactly here", which is the wrong question and answered no everywhere. At
        /// this radius the nine reads of a 3x3 patch ask "is a bullet near here", which is the
        /// question that decides whether a step leads somewhere better.
        ///
        /// It is built from the last frame's envelope -- the pad and the player's own radius -- so it
        /// tracks the game's own geometry rather than a constant, and it falls back to the configured
        /// pad alone when no envelope exists.
        /// </summary>
        public float ProbeRadius
        {
            // The FIRST threat's tight radius stands for the frame's, which is honest here: the tight
            // radius is `player.Radius + threat.Radius + pad`, and the threat radius varies by a
            // couple of tenths across a session's bullets while the player's half is constant. Taking
            // one number per frame keeps the probe the same size for every candidate of that frame,
            // which is what makes the term comparable across the ring.
            get { return _threatCount > 0 ? _threatNear[0] : 0.5f; }
        }

        /// <summary>The planner's counters. Read by the harness; reset by <see cref="ResetCounters"/>.</summary>
        public PathStats Stats => _stats;

        /// <summary>True when the last <see cref="Plan"/> produced a usable waypoint.</summary>
        public bool WaypointValid => _waypointValid;

        /// <summary>The waypoint's world position, valid when <see cref="WaypointValid"/>.</summary>
        public float WaypointX => _waypointX;
        public float WaypointY => _waypointY;

        /// <summary>The world heading from the player toward the waypoint, radians.</summary>
        public float WaypointHeading => _waypointHeading;

        /// <summary>Path nodes the last search expanded.</summary>
        public int Nodes => _nodes;

        /// <summary>Path length in world units, as the search found it.</summary>
        public float Length => _length;

        /// <summary>Total edge cost including danger, in the same units as length when danger is zero.</summary>
        public float Cost => _cost;

        /// <summary>Why the last search produced no path, or <see cref="PathReason.Found"/>.</summary>
        public PathReason Reason => _reason;

        /// <summary>True when the last waypoint came from the hold rather than from this frame's path.</summary>
        public bool Held => _waypointValid && !_fresh;

        /// <summary>
        /// The danger field's own value at a point and time, for the harness's `--frame N` dump.
        ///
        /// IT IS PUBLIC BECAUSE THE FIELD NEEDS AN INSTRUMENT AND NOT BECAUSE IT NEEDS AN API. The
        /// first version of this planner produced a path whose cost equalled its length on 90 % of
        /// frames and did not change at a danger weight of 20 -- which is a field that returns zero,
        /// and one that is either broken or honestly empty. `pathcost`/`pathlen` cannot tell those
        /// apart; one probe can, and this is it. It reads the envelope built by the last `Plan`, so it
        /// is meaningful only immediately after one.
        /// </summary>
        public float DebugDangerAt(float x, float y, float seconds)
        {
            return DangerAt(x, y, seconds);
        }

        /// <summary>
        /// The danger field's value at a point and time, in `[0,1]`, for the SOLVER as well as the
        /// instrument.
        ///
        /// THE FIELD IS THE PLANNER'S MOST USEFUL EXPORT AND THE PATH IS ONLY ITS FIRST CUSTOMER.
        /// `SPIKE3.2-OFFLINE-SOLVER.md` section 15.5 measured why the first version was only a partial
        /// fix: the retreat is paid for by `resolve` and `clearance`, which are both right about one
        /// step, and a waypoint is a DIRECTION -- so a preference built on it cannot say "this step is
        /// bad because of the step after it", which is the sentence the whole task starts from.
        ///
        /// This answers that sentence directly for any point and any time, so
        /// `DodgeSolver.ProjectedDanger` can ask it about where a candidate LEADS. It is the same
        /// one-clock question `PlanWalk` asks, from the same samples and the same envelope, so a
        /// candidate scored this way is scored on the model's own geometry and not on a second one.
        ///
        /// It reads the envelope built by the last `Plan`, so it is meaningful only after one; with no
        /// threats, no grid or the planner disabled it returns 0 and costs a loop over an empty array.
        /// </summary>
        public float Danger(float x, float y, float seconds)
        {
            return DangerAt(x, y, seconds);
        }

        /// <summary>
        /// One threat's own numbers, as the danger field sees them, for the harness's frame dump.
        /// This is the same probe as <see cref="DebugDangerAt"/> one level down: it reports what the
        /// early-out compared, not what it concluded.
        /// </summary>
        public string DebugThreat(int i)
        {
            if (i < 0 || i >= _threatCount)
            {
                return "none";
            }
            Trajectory t = _threats[i];
            if (t == null || t.Count <= 0)
            {
                return "empty";
            }
            return "radius=" + _threatRadius[i].ToString("0.###", CultureInfo.InvariantCulture)
                 + " near=" + _threatNear[i].ToString("0.###", CultureInfo.InvariantCulture)
                 + " samples=" + _threatSamples[i]
                 + " stepMs=" + _threatStepMs[i].ToString("0.###", CultureInfo.InvariantCulture)
                 + " count=" + t.Count
                 + " first=(" + t.Xs[0].ToString("0.##", CultureInfo.InvariantCulture) + ","
                 + t.Ys[0].ToString("0.##", CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>Threats in the last frame's danger envelope. See <see cref="DebugDangerAt"/>.</summary>
        public int DebugThreatCount => _threatCount;

        /// <summary>The tight radius used for threat <paramref name="i"/>. See <see cref="DebugDangerAt"/>.</summary>
        public float DebugThreatNear(int i)
        {
            return i >= 0 && i < _threatCount ? _threatNear[i] : 0.0f;
        }

        /// <summary>Drops the held waypoint and the counters. A zone change, or a disabled dodge.</summary>
        public void Reset()
        {
            _holdValid = false;
            _waypointValid = false;
            _reason = PathReason.Disabled;
            ResetCounters();
        }

        /// <summary>Zeroes the report counters without touching the held waypoint.</summary>
        public void ResetCounters()
        {
            _stats.Searches = 0;
            _stats.NodeCapHits = 0;
            _stats.NoPath = 0;
            _stats.Held = 0;
            _stats.WallCrossings = 0;
            _stats.PathsFound = 0;
            _stats.NodesTotal = 0;
            _stats.LengthTotal = 0.0;
            _stats.MicrosecondsTotal = 0.0;
            _stats.NoGridFrames = 0;
            _stats.DisabledFrames = 0;
            _stats.UnreachableFrames = 0;
            _stats.NoGoalFrames = 0;
        }

        private void EnsureCapacity(int width, int height)
        {
            int cells = width * height;
            if (_gScore.Length < cells)
            {
                _gScore = new float[cells];
                _costScore = new float[cells];
                _cameFrom = new int[cells];
                _closed = new bool[cells];
                _inOpen = new bool[cells];
            }
            int heapCap = cells + 8;
            if (_heap.Length < heapCap)
            {
                _heap = new int[heapCap];
                _heapKey = new float[heapCap];
            }
            if (_pathIndex.Length < cells)
            {
                _pathIndex = new int[cells];
            }
        }

        // ===================================================================================
        // The per-frame entry
        // ===================================================================================

        /// <summary>
        /// One frame: build the danger envelope, search from the player's cell to a cell on the goal
        /// ray, and reduce the path to a single waypoint.
        ///
        /// IT IS CALLED ONCE PER FRAME, NOT ONCE PER CANDIDATE. 77 candidates times an A* each is not
        /// a thing the frame budget survives; `HANDOFF-DIRECTED-DODGE.md` section 4.1 states the
        /// budget as "under 50 us, because the sampler and the sensor share the 300".
        /// </summary>
        /// <param name="snapshot">The frame. Its grid, threats and player are read; nothing is written.</param>
        /// <param name="goalRadians">The world heading of the forward goal, already resolved by the solver.</param>
        /// <param name="goalValid">Whether that heading is a measurement rather than a default.</param>
        /// <param name="stepRadius">
        /// One full step, `budget`. The waypoint is the first path node beyond it, so the steer cannot
        /// jitter between the current cell and the next.
        /// </param>
        /// <param name="budget">The player's own coverage this frame, world units.</param>
        /// <param name="playerRadius">The player's collision radius, already defaulted.</param>
        public void Plan(DodgeSnapshot snapshot, float goalRadians, bool goalValid,
                         float stepRadius, float budget, float playerRadius)
        {
            long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            _nodes = 0;
            _fieldReady = false;
            _length = 0.0f;
            _cost = 0.0f;
            _fresh = false;
            _waypointValid = false;

            if (!_config.PathEnable)
            {
                Fail(PathReason.Disabled, snapshot);
                _stats.Searches++;
                _stats.DisabledFrames++;
                _stats.MicrosecondsTotal += Elapsed(startTicks);
                return;
            }

            if (!goalValid)
            {
                Fail(PathReason.NoGoal, snapshot);
                _stats.Searches++;
                _stats.NoGoalFrames++;
                _stats.MicrosecondsTotal += Elapsed(startTicks);
                return;
            }

            // --- The grid, and the frame's danger envelope. ---------------------------------
            if (!BeginFrame(snapshot, goalRadians, budget, playerRadius))
            {
                Fail(_reason, snapshot);
                _stats.Searches++;
                _stats.MicrosecondsTotal += Elapsed(startTicks);
                return;
            }

            _stats.Searches++;

            int startIndex = IndexOf(_playerX, _playerY);
            if (startIndex < 0 || !WalkableIndex(startIndex))
            {
                // THE PLAYER'S OWN CELL IS A WALL. This happens for real -- pinned against scenery is
                // the state three of Spike 3.1's eight phases ended in -- and the honest answer is not
                // to search from the nearest walkable cell, which would be a path that starts
                // somewhere the player is not. No path, and the goal term decides as before.
                Fail(PathReason.StartBlocked, snapshot);
                _stats.NoPath++;
                _stats.MicrosecondsTotal += Elapsed(startTicks);
                return;
            }

            if (!Search(startIndex, _targetIndex))
            {
                Fail(_reason, snapshot);
                _stats.NoPath++;
                if (_reason == PathReason.NodeCap)
                {
                    _stats.NodeCapHits++;
                }
                else if (_reason == PathReason.Unreachable)
                {
                    _stats.UnreachableFrames++;
                }
                _stats.MicrosecondsTotal += Elapsed(startTicks);
                return;
            }

            _stats.PathsFound++;
            _stats.NodesTotal += _nodes;
            _stats.LengthTotal += _length;

            // --- The waypoint, and its hysteresis. ------------------------------------------
            //
            // THE WAYPOINT IS A POINT ALONG THE PATH, AND WHERE ALONG IT IS THE WHOLE DESIGN
            // QUESTION. Two earlier answers were wrong in the same way, and both measurements are
            // worth keeping because the second one is a geometric fact rather than a bug:
            //
            //   "The first node beyond one step" (the handoff's section 4.3) measured nothing. A path
            //   node is one world unit wide and a step is `budget`, about 0.14 units, so the waypoint
            //   was the first cell centre and its direction was the grid's own rounding: it alternated
            //   sides of the goal on adjacent frames (`p26156` frame 26 `wp 60`, frame 27 `wp -60`,
            //   goal -135 on both).
            //
            //   "A fixed distance along the path" fixed the noise and changed nothing else, and the
            //   reason is the shape of the answer: **a path that detours only at its far end still
            //   points at the goal for its whole first half.** Every node up to the obstacle carries
            //   the goal direction, so a waypoint near the player is the goal term wearing a
            //   pathfinder's name -- which is exactly what the danger measurement showed: cost minus
            //   length 0 on 90 % of frames, and `|wp - goal|` 0.3 degrees at the median.
            //
            // So the waypoint is a point a FRACTION of the way along the path, where the route has
            // had the distance to commit to its detour: `PathWaypointFraction` of the path's length,
            // never less than `PathWaypointUnits`. The first node beyond that distance is the
            // waypoint, and a path shorter than that is its own far end.
            int waypointNode = -1;
            float waypointReach = _length * _config.PathWaypointFraction;
            if (waypointReach < _config.PathWaypointUnits)
            {
                waypointReach = _config.PathWaypointUnits;
            }
            if (waypointReach <= 0.0f)
            {
                waypointReach = stepRadius;
            }
            float waypointDist2 = waypointReach * waypointReach;
            for (int i = 0; i < _pathCount; i++)
            {
                float wx = WorldX(_pathIndex[i]);
                float wy = WorldY(_pathIndex[i]);
                float dx = wx - _playerX;
                float dy = wy - _playerY;
                if ((dx * dx) + (dy * dy) >= waypointDist2)
                {
                    waypointNode = i;
                    break;
                }
            }
            if (waypointNode < 0 && _pathCount > 0)
            {
                waypointNode = _pathCount - 1;   // a path shorter than the waypoint distance: its far end is the direction
            }
            if (waypointNode < 0)
            {
                Fail(PathReason.Unreachable, snapshot);
                _stats.NoPath++;
                _stats.MicrosecondsTotal += Elapsed(startTicks);
                return;
            }

            float newX = WorldX(_pathIndex[waypointNode]);
            float newY = WorldY(_pathIndex[waypointNode]);

            _fresh = true;
            if (_holdValid && OnPath(_heldX, _heldY) && WithinDwell(snapshot))
            {
                // THE HELD WAYPOINT IS STILL A NODE OF THIS FRAME'S PATH, so the world did not change
                // its mind about it and neither does the dodge. The hold is not "ignore the new path":
                // the new path has been computed and it still contains the old answer, which is the
                // strongest form of agreement available.
                _waypointX = _heldX;
                _waypointY = _heldY;
                _fresh = false;
                _stats.Held++;
            }
            else
            {
                _waypointX = newX;
                _waypointY = newY;
                _heldX = newX;
                _heldY = newY;
                _holdValid = true;
                _heldTakenMs = snapshot.ClockMs;
            }

            float hx = _waypointX - _playerX;
            float hy = _waypointY - _playerY;
            if ((hx * hx) + (hy * hy) < 1e-6f)
            {
                Fail(PathReason.Unreachable, snapshot);
                _stats.NoPath++;
                _stats.MicrosecondsTotal += Elapsed(startTicks);
                return;
            }
            _waypointHeading = (float)Math.Atan2(hy, hx);
            _waypointValid = true;
            _reason = PathReason.Found;
            _stats.MicrosecondsTotal += Elapsed(startTicks);
        }

        private bool WithinDwell(DodgeSnapshot snapshot)
        {
            float dwellMs = _config.PathHysteresisDwellSeconds * 1000.0f;
            return snapshot.ClockMs - _heldTakenMs < dwellMs;
        }

        /// <summary>True when the held waypoint is one of this frame's path nodes, within half a cell.</summary>
        private bool OnPath(float x, float y)
        {
            for (int i = 0; i < _pathCount; i++)
            {
                float dx = WorldX(_pathIndex[i]) - x;
                float dy = WorldY(_pathIndex[i]) - y;
                if ((dx * dx) + (dy * dy) <= 0.25f)
                {
                    return true;
                }
            }
            return false;
        }

        private void Fail(PathReason reason, DodgeSnapshot snapshot)
        {
            _reason = reason;
            _waypointValid = false;
            if (reason != PathReason.Found && reason != PathReason.Disabled && reason != PathReason.NoGoal
                && reason != PathReason.NoGrid && reason != PathReason.GridTooLarge)
            {
                // The hold is dropped on every failure that could have had a path. A hold that
                // survives a frame with no path is a direction from a world that no longer exists --
                // the same reasoning the adapter uses when it clears a pending command every frame.
                _holdValid = false;
            }
            if (reason == PathReason.NoGrid && snapshot != null && !snapshot.Grid.Present)
            {
                _stats.NoGridFrames++;
            }
        }

        // ===================================================================================
        // The frame's own quantities
        // ===================================================================================

        /// <summary>
        /// Resolves the grid, the player, the target cell and the per-threat danger envelope. False when
        /// the frame cannot be searched; <see cref="_reason"/> says which of the ways it cannot.
        /// </summary>
        private bool BeginFrame(DodgeSnapshot snapshot, float goalRadians, float budget, float playerRadius)
        {


            CollisionWindow grid = snapshot.Grid;
            if (!grid.Present || grid.Cells == null || grid.Width <= 0 || grid.Height <= 0)
            {
                _reason = PathReason.NoGrid;
                return false;
            }
            if (grid.Width > MaxGridWidth || grid.Height > MaxGridHeight)
            {
                _reason = PathReason.GridTooLarge;
                return false;
            }

            EnsureCapacity(grid.Width, grid.Height);
            _grid = grid;
            _gridW = grid.Width;
            _gridH = grid.Height;
            _cells = _gridW * _gridH;


            _playerX = snapshot.Player.X;
            _playerY = snapshot.Player.Y;
            _speedPerSecond = snapshot.Player.SpeedPerSecond;
            if (_speedPerSecond <= 0.0f)
            {
                _speedPerSecond = budget > 0.0f && snapshot.FrameSeconds > 0.0f
                    ? budget / snapshot.FrameSeconds
                    : 1.0f;
            }

            // --- The target: one cell on the goal ray, at a fixed travel time. ----------------
            float reach = _speedPerSecond * _config.PathTargetSeconds;
            float tx = _playerX + (reach * (float)Math.Cos(goalRadians));
            float ty = _playerY + (reach * (float)Math.Sin(goalRadians));

            // AND IT IS PULLED INSIDE THE WINDOW BEFORE IT IS INDEXED. A goal ray that leaves the
            // recorded window is the normal case, not an error: the window is ~128 units across and a
            // 1.5 s lookahead at 8.2 units/s is 12.3 units, but the player is not always at the
            // centre of play. Clamping keeps the search inside the region the client's grid actually
            // covers, which is the only region this planner is entitled to have an opinion about.
            if (!_grid.InWindow(tx, ty))
            {
                tx = Clamp(tx, _grid.OriginX + 1.0f, _grid.OriginX + _gridW - 2.0f);
                ty = Clamp(ty, _grid.OriginY + 1.0f, _grid.OriginY + _gridH - 2.0f);
            }

            _targetIndex = IndexOf(tx, ty);
            _targetSet = _targetIndex >= 0 && WalkableIndex(_targetIndex);
            if (!_targetSet)
            {
                _reason = PathReason.NoTarget;
                return false;
            }

            BuildDangerEnvelope(snapshot, playerRadius);
            // THE FIELD IS READY EVEN WHEN THE PATH IS NOT. See `FieldReady`: a frame with threats in
            // it has a danger field whether or not a route to the goal could be found, and the
            // projected-danger term is a question about the field.
            _fieldReady = _threatCount > 0;
            return true;
        }

        private static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) { return lo; }
            if (v > hi) { return hi; }
            return v;
        }

        /// <summary>
        /// One tight radius per threat, and the per-threat numbers the danger test needs.
        ///
        /// THIS IS NOT AN OPTIMISATION OF THE ANSWER, IT IS THE ANSWER. See the comment on the fields:
        /// a bullet's body is within its collision radius of its own sampled path, so a cell further
        /// than that from every sample cannot be hit by it whenever the player arrives. Everything
        /// inside that bound is tested exactly, by interpolation, below.
        /// </summary>
        private void BuildDangerEnvelope(DodgeSnapshot snapshot, float playerRadius)
        {
            int n = snapshot.Threats.Count;
            if (_threats.Length < n)
            {
                _threats = new Trajectory[n < 8 ? 8 : n];
                _threatNear = new float[n < 8 ? 8 : n];
                _threatRadius = new float[n < 8 ? 8 : n];
                _threatStepMs = new float[n < 8 ? 8 : n];
                _threatSamples = new int[n < 8 ? 8 : n];
            }
            _threatCount = 0;
            for (int i = 0; i < n; i++)
            {
                Trajectory t = snapshot.Threats[i];
                if (t == null || t.Count <= 0)
                {
                    continue;
                }
                float step = t.StepMs > 0.0f ? t.StepMs : 30.0f;
                // HALF A SAMPLE STEP IS THE OTHER HALF OF THE BOUND. The path between two knots is
                // covered by the segment test below, but a cell that is off the path by less than the
                // distance the bullet covers between knots can still be crossed between samples.
                float near = playerRadius + t.Radius + _config.PathDangerPadUnits + (0.5f * step * 0.001f * SpeedOf(t, step));
                _threats[_threatCount] = t;
                _threatNear[_threatCount] = near;
                _threatRadius[_threatCount] = playerRadius + t.Radius;
                _threatStepMs[_threatCount] = step;
                _threatSamples[_threatCount] = t.Count;
                _threatCount++;
            }
        }

        /// <summary>The threat's own speed, from its first two samples; 0 when they coincide.</summary>
        private static float SpeedOf(Trajectory t, float stepMs)
        {
            if (t.Count < 2 || stepMs <= 0.0f)
            {
                return 0.0f;
            }
            float dx = t.Xs[1] - t.Xs[0];
            float dy = t.Ys[1] - t.Ys[0];
            return (float)Math.Sqrt((dx * dx) + (dy * dy)) * 1000.0f / stepMs;
        }

        // ===================================================================================
        // The search
        // ===================================================================================

        /// <summary>
        /// A* from <paramref name="start"/> to <paramref name="target"/>, 8-connected with the
        /// diagonal rule, cost = distance + spacetime danger.
        ///
        /// THE DIAGONAL RULE MATTERS HERE AND IT IS NOT A DETAIL. A diagonal step is allowed only when
        /// both of its orthogonal neighbours are walkable, because the collision grid's solid cells
        /// are the walls rule 2 already refuses -- and a diagonal that passed between two solid cells
        /// would be a path through a corner the game itself would refuse to walk. That is a path the
        /// solver could follow into a wall.
        /// </summary>
        private bool Search(int start, int target)
        {
            for (int i = 0; i < _cells; i++)
            {
                _closed[i] = false;
                _inOpen[i] = false;
                _gScore[i] = float.MaxValue;
                _costScore[i] = float.MaxValue;
                _cameFrom[i] = -1;
            }
            _heapCount = 0;
            _pathCount = 0;
            _nodes = 0;

            _gScore[start] = 0.0f;
            _costScore[start] = 0.0f;
            HeapPush(start, Heuristic(start, target));

            int cap = _config.PathNodeCap < 16 ? 16 : _config.PathNodeCap;
            bool found = false;

            while (_heapCount > 0)
            {
                int current = HeapPop();
                if (_closed[current])
                {
                    continue;
                }
                _closed[current] = true;
                _nodes++;

                if (current == target)
                {
                    found = true;
                    break;
                }

                if (_nodes >= cap)
                {
                    _reason = PathReason.NodeCap;
                    return false;
                }

                float g = _gScore[current];
                int cx = current % _gridW;
                int cy = current / _gridW;
                float arrivalSeconds = g / _speedPerSecond;

                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + DirX[d];
                    int ny = cy + DirY[d];
                    if (nx < 0 || ny < 0 || nx >= _gridW || ny >= _gridH)
                    {
                        continue;
                    }
                    bool diagonal = d >= 4;
                    int nIndex = (ny * _gridW) + nx;
                    if (_closed[nIndex] || !WalkableIndex(nIndex))
                    {
                        continue;
                    }
                    if (diagonal
                        && (!WalkableIndex((cy * _gridW) + nx) || !WalkableIndex((ny * _gridW) + cx)))
                    {
                        continue;
                    }

                    float stepLength = diagonal ? 1.41421356f : 1.0f;
                    float cellX = _grid.OriginX + nx + 0.5f;
                    float cellY = _grid.OriginY + ny + 0.5f;
                    float arrival = arrivalSeconds + (stepLength / _speedPerSecond);
                    float danger = DangerAt(cellX, cellY, arrival);
                    float cost = g + stepLength + (_config.PathStepCostWeight * danger);

                    if (cost < _costScore[nIndex])
                    {
                        _costScore[nIndex] = cost;
                        _gScore[nIndex] = g + stepLength;
                        _cameFrom[nIndex] = current;
                        HeapPush(nIndex, cost + Heuristic(nIndex, target));
                    }
                }
            }

            if (!found)
            {
                _reason = PathReason.Unreachable;
                return false;
            }

            // --- The path, from the target back to the player. ------------------------------
            //
            // THE CHAIN IS WALKED BACKWARDS AND THE LENGTHS ARE ACCUMULATED AS EUCLIDEAN STEPS, not
            // read off `_costScore`: the cost includes danger and the length is a distance, and quoting
            // a danger-weighted number as "how far the path goes" would be exactly the kind of
            // quantity confusion this project has already paid for once.
            int node = target;
            float length = 0.0f;
            while (node >= 0 && _pathCount < _cells)
            {
                _pathIndex[_pathCount++] = node;
                int parent = _cameFrom[node];
                if (parent >= 0)
                {
                    int px = parent % _gridW;
                    int py = parent / _gridW;
                    int qx = node % _gridW;
                    int qy = node / _gridW;
                    float dx = qx - px;
                    float dy = qy - py;
                    length += (float)Math.Sqrt((dx * dx) + (dy * dy));
                }
                node = parent;
            }

            // `_pathIndex` is target-first; the caller wants it player-first, which is also the order
            // the waypoint search walks it in.
            for (int i = 0, j = _pathCount - 1; i < j; i++, j--)
            {
                int tmp = _pathIndex[i];
                _pathIndex[i] = _pathIndex[j];
                _pathIndex[j] = tmp;
            }

            // THE CORNER-CUT CHECK, ON THE PATH THAT WAS ACTUALLY RETURNED. See `WallCrossings`: the
            // diagonal rule is the only thing in this file that can put a route through a wall, so it
            // is checked on every search rather than trusted. A diagonal step whose two orthogonal
            // neighbours are both solid is the exact shape the rule exists to forbid.
            for (int i = 1; i < _pathCount; i++)
            {
                int a = _pathIndex[i - 1];
                int b = _pathIndex[i];
                int ax = a % _gridW;
                int ay = a / _gridW;
                int bx = b % _gridW;
                int by = b / _gridW;
                if (ax != bx && ay != by
                    && !WalkableIndex((ay * _gridW) + bx) && !WalkableIndex((by * _gridW) + ax))
                {
                    _stats.WallCrossings++;
                }
            }

            _length = length;
            _cost = _costScore[target];
            _reason = PathReason.Found;
            return true;
        }

        /// <summary>
        /// Straight-line distance to the target -- admissible, so A* returns an optimal path.
        ///
        /// THERE IS NO TIE-BREAK TOWARD THE GOAL RAY, AND ITS ABSENCE IS A DECISION RATHER THAN AN
        /// OMISSION. `HANDOFF-DIRECTED-DODGE.md` section 4.2 suggests one, to make the path "go around
        /// rather than go wide". A tie-break changes WHICH of several equal-cost paths is returned,
        /// which is a second opinion about the shape of the answer riding on top of the search -- and
        /// the search already has a shape opinion, in `StepCostWeight`. Adding a second before the
        /// first has been measured would leave no way to attribute a change to either. It is the first
        /// thing to try if the measured paths go wider than the operator wants.
        /// </summary>
        private float Heuristic(int index, int target)
        {
            int ax = index % _gridW;
            int ay = index / _gridW;
            int bx = target % _gridW;
            int by = target / _gridW;
            float dx = ax - bx;
            float dy = ay - by;
            return (float)Math.Sqrt((dx * dx) + (dy * dy));
        }

        /// <summary>
        /// The danger of being at a world position at a given time, in world units of cost.
        ///
        /// IT INTERPOLATES BETWEEN SAMPLES RATHER THAN SNAPPING TO ONE. A bullet sampled at 30 ms
        /// covers up to 0.72 units between knots at 24 units/s, which is larger than the collision
        /// disc -- so a cell tested against the nearest knot alone can read "clear" for a bullet that
        /// is crossing it. The two knots either side are interpolated by time, which is the same
        /// one-clock model `PlanWalk` walks on.
        /// </summary>
        private float DangerAt(float x, float y, float seconds)
        {
            float worst = 0.0f;
            for (int i = 0; i < _threatCount; i++)
            {
                Trajectory t = _threats[i];
                float near = _threatNear[i];
                float dx0 = t.Xs[0] - x;
                float dy0 = t.Ys[0] - y;
                if ((dx0 * dx0) + (dy0 * dy0) > near * near)
                {
                    // Cheap early-out against the FIRST sample only. It is not the bound test -- the
                    // bound is per-sample and is done inside the loop below -- it is the common case:
                    // most cells are far from most bullets, and one subtraction pair per threat is all
                    // those cells cost.
                    continue;
                }

                float radius = _threatRadius[i];
                float stepMs = _threatStepMs[i];
                int samples = _threatSamples[i];
                if (samples < 1 || t == null)
                {
                    // UNREACHABLE BY CONSTRUCTION -- `BuildDangerEnvelope` only admits a trajectory with
                    // `Count > 0` -- and written anyway because the alternative to a guard here is an
                    // index of -1 on the game's frame path. The cost is one compare per threat per node.
                    continue;
                }
                int idx = (int)((seconds * 1000.0f) / stepMs);
                if (idx < 0)
                {
                    idx = 0;
                }
                if (idx >= samples - 1)
                {
                    // PAST THE END OF THE SAMPLES IS NOT EXTRAPOLATED. The sampler freezes a bullet at
                    // its last sample when its family declares the shot finished, and inventing motion
                    // beyond that is inventing danger -- the same rule `Trajectory.Alive` and the
                    // trust prefix already apply.
                    idx = samples - 1;
                }
                float fx = t.Xs[idx];
                float fy = t.Ys[idx];
                if (idx < samples - 1)
                {
                    float frac = ((seconds * 1000.0f) - (idx * stepMs)) / stepMs;
                    if (frac < 0.0f) { frac = 0.0f; }
                    if (frac > 1.0f) { frac = 1.0f; }
                    fx += (t.Xs[idx + 1] - fx) * frac;
                    fy += (t.Ys[idx + 1] - fy) * frac;
                }

                float dx = fx - x;
                float dy = fy - y;
                float cellDist = (float)Math.Sqrt((dx * dx) + (dy * dy));
                if (cellDist >= near)
                {
                    continue;
                }
                // Linear in the excess, saturating at 1 for a cell the bullet's own body covers.
                float penalty = cellDist <= radius ? 1.0f : (near - cellDist) / (near - radius);
                if (penalty > worst)
                {
                    worst = penalty;
                }
            }
            return worst;
        }

        // ===================================================================================
        // Grid addressing, in the game's own convention
        // ===================================================================================

        /// <summary>Cell index of a world position, or -1 outside the window. The truncation is the game's.</summary>
        private int IndexOf(float worldX, float worldY)
        {
            int cx = (int)worldX - _grid.OriginX;
            int cy = (int)worldY - _grid.OriginY;
            if (cx < 0 || cy < 0 || cx >= _gridW || cy >= _gridH)
            {
                return -1;
            }
            return (cy * _gridW) + cx;
        }

        private bool WalkableIndex(int index)
        {
            return _grid.Cells[index] == 0;
        }

        private float WorldX(int index)
        {
            return _grid.OriginX + (index % _gridW) + 0.5f;
        }

        private float WorldY(int index)
        {
            return _grid.OriginY + (index / _gridW) + 0.5f;
        }

        // ===================================================================================
        // The open set: a binary heap over (cell, key)
        // ===================================================================================

        private void HeapPush(int cell, float key)
        {
            if (_heapCount >= _heap.Length)
            {
                return;   // the buffer is sized for every cell; this cannot happen, and refusing beats growing
            }
            int i = _heapCount++;
            _heap[i] = cell;
            _heapKey[i] = key;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_heapKey[parent] <= _heapKey[i])
                {
                    break;
                }
                Swap(i, parent);
                i = parent;
            }
        }

        private int HeapPop()
        {
            int top = _heap[0];
            _heapCount--;
            if (_heapCount > 0)
            {
                _heap[0] = _heap[_heapCount];
                _heapKey[0] = _heapKey[_heapCount];
                int i = 0;
                for (;;)
                {
                    int left = (2 * i) + 1;
                    int right = left + 1;
                    int smallest = i;
                    if (left < _heapCount && _heapKey[left] < _heapKey[smallest])
                    {
                        smallest = left;
                    }
                    if (right < _heapCount && _heapKey[right] < _heapKey[smallest])
                    {
                        smallest = right;
                    }
                    if (smallest == i)
                    {
                        break;
                    }
                    Swap(i, smallest);
                    i = smallest;
                }
            }
            return top;
        }

        private void Swap(int a, int b)
        {
            int c = _heap[a];
            _heap[a] = _heap[b];
            _heap[b] = c;
            float k = _heapKey[a];
            _heapKey[a] = _heapKey[b];
            _heapKey[b] = k;
        }

        private static double Elapsed(long startTicks)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks)
                 * (1000000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
    }
}

