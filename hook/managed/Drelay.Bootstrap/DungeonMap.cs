// DungeonMap.cs -- the dungeon's own map, built from the tiles the client streams in.
//
// =====================================================================================
// WHAT THIS IS FOR, AND WHY IT IS A SEPARATE CLASS
// =====================================================================================
//
// `PathPlanner` searches a 128-unit window around the player. That is the right horizon for a
// dodge and the wrong one for a dungeon: a corridor can leave the room at a right angle, a boss
// room can be thirty units away along a route that is eighty cells long, and `GmTiles` delivers
// the map a piece at a time. The local planner cannot answer "which way out of this room" and it
// is not supposed to.
//
// This class is the memory that can answer it. It stores one state byte per map cell, filled from
// the SAME window `DodgeAdapter.RefreshGrid` already reads (collision bytes plus `Tilemap.GetTile`),
// and offers one breadth-first search over what is known. It contains no policy: it does not decide
// where to walk, it answers three geometry questions:
//
//   1. is the goal reachable over known floor? If so, give me the chain of cells;
//   2. if not, where is the nearest reachable floor cell that touches the unknown?
//   3. how far is the reachable floor from the goal, so the caller can tell "a wall away" from
//      "another room away"?
//
// The policy -- commit to one frontier, spend a dead end, backtrack -- belongs to `DungeonSolver`.
// Keeping the two apart is the point of this rewrite: the old module mixed the cache's storage,
// the search and the commitment flags in one static class, and the state was impossible to reset
// without also resetting the map.
//
// =====================================================================================
// FOUR CELL STATES, AND THE THIRD ONE IS THE DUNGEON
// =====================================================================================
//
//   0  UNKNOWN          nothing has been painted here yet.
//   1  FLOOR            a tile exists and it is walkable. The only cell a route may use.
//   2  WALL             a nonzero collision byte, or outside the map. Permanent: the server does
//                       not turn a collision cell into floor.
//   3  PROVISIONAL WALL a `NoWalk` tile. A wall, but a PLACEHOLDER while a map streams in.
//
// The third state exists because of a measurement, not a theory. In River Maze one boss-room cell
// read `NoWalk` for seventy frames and then became the boss's floor (`20261020-181445-p4196`). A
// cache that treats that placeholder as permanent closes a room that is still arriving; a cache
// that treats it as unknown turns a real wall into a frontier and the character presses it for the
// rest of the session. So a later FLOOR observation overrides it, and a hard wall never does.
//
// Absent tile is not a wall. `Tilemap` does not arrive with the world, and `GetTile` returns null
// for a cell the streamer has not reached. That is UNKNOWN, and the collision byte is the only
// honest answer: zero bytes are what the server sends for space it has not painted. Reading null
// as a wall is what sealed every dungeon in the 2026-10-20 sessions.
//
// =====================================================================================
// STATE IS PER MAP, AND IT IS INSTANCE STATE
// =====================================================================================
//
// Everything here hangs off a `DungeonMap` instance: dimensions, the cells, the exploration memory
// (`tried`), the search scratch, and the map's identity. `Bind` is the only way to change the map,
// and it reallocates ONLY when the grid array reference or its dimensions change -- the same signal
// the window uses. A world change therefore cannot inherit another map's walls, and a map too large
// to cache is refused by name rather than allocating a gigabyte off a varint.

using System;

namespace Drelay.Bootstrap
{
    /// <summary>
    /// One dungeon's walkability map, filled from the frames the adapter already reads, plus the
    /// breadth-first search over it. No policy and no goal: see the file header.
    /// </summary>
    public sealed class DungeonMap
    {
        // --- The source codes, shared with the trace recorder -------------------------------
        //
        // `DodgeAdapter.RefreshGrid` classifies each cell once and hands the same byte to this map
        // and to `TraceRecorder`. Keeping the vocabulary in one place is what lets an offline replay
        // feed a recording into this class and trust the answer: the replay is not interpreting the
        // grid, it is replaying the adapter's own classification.

        public const byte SourceUnknown = 0;
        public const byte SourceCollision = 1;
        public const byte SourceNoWalk = 2;
        public const byte SourceFloor = 3;
        public const byte SourceOutOfMap = 4;

        // --- The four cell states ------------------------------------------------------------

        public const byte Unknown = 0;
        public const byte Floor = 1;
        public const byte Wall = 2;
        public const byte ProvisionalWall = 3;

        /// <summary>
        /// The largest map this will keep a cache for, in cells. A dungeon is 1 024 x 1 024 (1.05 M,
        /// about 16 MB with the search arrays); a realm is 2 698 x 2 634 (7.1 M, 90 MB) and the realm
        /// walk does not need this module. The refusal is in <see cref="Reason"/>, so a run can tell
        /// it from "the map is empty".
        /// </summary>
        public const int MaxCells = 2000000;

        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>True when a valid cache is bound and the search arrays exist.</summary>
        public bool Available { get; private set; }

        /// <summary>Why the cache is unavailable, or "ready". One short line for the report.</summary>
        public string Reason { get; private set; } = "not bound";

        /// <summary>
        /// Bumped on every observation that changes a cell. It is the "the map grew" signal: the
        /// solver uses it to tell a frontier that revealed something from one that did not, without
        /// diffing a million cells.
        /// </summary>
        public long Version { get; private set; }

        public long FloorCells { get; private set; }
        public long WallCells { get; private set; }
        public long ProvisionalCells { get; private set; }

        private object _key;
        private byte[] _cells;
        private byte[] _tried;
        private byte[] _seen;
        private byte _seenGeneration;
        private int[] _parent;
        private int[] _depth;
        private int[] _queue;
        private int[] _path = new int[0];

        /// <summary>Nodes in <see cref="_path"/> after the last <see cref="BuildPath"/>, player first.</summary>
        public int PathCount { get; private set; }

        // ===================================================================================
        // Binding
        // ===================================================================================

        /// <summary>
        /// Binds the cache to a map. The identity is the collision grid's ARRAY REFERENCE plus its
        /// dimensions, exactly what `DodgeAdapter` uses to decide that the window is stale: a new
        /// `byte[,]` is a new world (`World.ClearWorld` allocates one), and a route from the old
        /// world would be a route through another map's walls.
        ///
        /// Returns true when a cache was (re)allocated. False means either "already this map" or
        /// "refused"; <see cref="Available"/> and <see cref="Reason"/> name which.
        /// </summary>
        public bool Bind(object key, int width, int height)
        {
            if (key == null || width <= 0 || height <= 0)
            {
                Refuse("no collision grid");
                return false;
            }
            if (Available && ReferenceEquals(key, _key) && width == Width && height == Height)
            {
                return false;
            }

            long area = (long)width * height;
            if (area > MaxCells || area > int.MaxValue)
            {
                Refuse(width + "x" + height + " is " + area + " cells, above the " + MaxCells
                       + "-cell cap");
                return false;
            }

            Width = width;
            Height = height;
            _key = key;
            int cells = (int)area;
            _cells = new byte[cells];
            _tried = new byte[cells];
            _seen = new byte[cells];
            _parent = new int[cells];
            _depth = new int[cells];
            // The queue is bounded by the node budget, not by the map: a search that expands more
            // than the budget stops with the best frontier it found, and a queue the size of the
            // map would be another 8 MB at the cap for no benefit.
            _queue = new int[Math.Min(cells, DungeonSolver.PlanNodeBudget)];
            _seenGeneration = 0;
            _path = new int[0];
            PathCount = 0;
            FloorCells = 0;
            WallCells = 0;
            ProvisionalCells = 0;
            Version = 1;
            Available = true;
            Reason = "ready";
            return true;
        }

        /// <summary>Forgets all knowledge but keeps the allocation. Used on a restart or in a test.</summary>
        public void ClearKnowledge()
        {
            if (!Available)
            {
                return;
            }
            Array.Clear(_cells, 0, _cells.Length);
            Array.Clear(_tried, 0, _tried.Length);
            PathCount = 0;
            FloorCells = 0;
            WallCells = 0;
            ProvisionalCells = 0;
            Version++;
        }

        /// <summary>Drops the map completely. The next <see cref="Bind"/> reallocates.</summary>
        public void Reset()
        {
            Available = false;
            _key = null;
            _cells = null;
            _tried = null;
            _seen = null;
            _parent = null;
            _depth = null;
            _queue = null;
            _path = new int[0];
            PathCount = 0;
            Width = 0;
            Height = 0;
            FloorCells = 0;
            WallCells = 0;
            ProvisionalCells = 0;
            Version = 0;
            Reason = "not bound";
        }

        private void Refuse(string reason)
        {
            Available = false;
            _key = null;
            _cells = null;
            _tried = null;
            _seen = null;
            _parent = null;
            _depth = null;
            _queue = null;
            _path = new int[0];
            PathCount = 0;
            FloorCells = 0;
            WallCells = 0;
            ProvisionalCells = 0;
            Reason = reason;
        }

        // ===================================================================================
        // Observation
        // ===================================================================================

        public bool Inside(int x, int y)
        {
            return x >= 0 && y >= 0 && x < Width && y < Height;
        }

        /// <summary>The cell's state, or <see cref="Unknown"/> outside the map.</summary>
        public byte KindAt(int x, int y)
        {
            if (!Available || x < 0 || y < 0 || x >= Width || y >= Height)
            {
                return Unknown;
            }
            return _cells[(y * Width) + x];
        }

        public byte KindAt(int index)
        {
            if (!Available || index < 0 || index >= _cells.Length)
            {
                return Unknown;
            }
            return _cells[index];
        }

        public bool IsFloor(int x, int y)
        {
            return KindAt(x, y) == Floor;
        }

        /// <summary>
        /// One cell the adapter has just classified. The precedence is the whole dungeon rule:
        ///
        ///   * FLOOR is final. The streamer does not turn a walkable tile into a wall, and a
        ///     knockback that reads a gap in the window must not erase the map;
        ///   * a nonzero collision byte (or outside the map) is a PERMANENT wall, and it upgrades a
        ///     provisional `NoWalk` wall because it is the server's own answer;
        ///   * a `NoWalk` tile is a PROVISIONAL wall: it can be overridden by a later floor tile,
        ///     which is the 70-frame placeholder measured in River Maze;
        ///   * an absent tile (`SourceUnknown`) is NO INFORMATION: it never changes a cell. It is
        ///     what a frontier is made of, not what a wall is made of.
        /// </summary>
        public void Observe(int x, int y, byte source)
        {
            if (!Available || x < 0 || y < 0 || x >= Width || y >= Height)
            {
                return;
            }
            int index = (y * Width) + x;
            byte prior = _cells[index];
            if (prior == Floor || prior == Wall)
            {
                // FLOOR IS FINAL AND SO IS A HARD WALL. The streamer does not turn a walkable tile
                // into a wall, and the server's nonzero collision byte does not turn back into
                // floor. Only the provisional `NoWalk` state may be upgraded by a later tile.
                return;
            }

            byte next;
            switch (source)
            {
                case SourceFloor:
                    next = Floor;
                    break;
                case SourceCollision:
                case SourceOutOfMap:
                    next = Wall;
                    break;
                case SourceNoWalk:
                    next = prior == Wall ? Wall : ProvisionalWall;
                    break;
                default:
                    return;
            }
            if (next == prior)
            {
                return;
            }

            AdjustCounts(prior, next);
            _cells[index] = next;
            Version++;
        }

        private void AdjustCounts(byte prior, byte next)
        {
            if (prior == Floor) { FloorCells--; }
            else if (prior == Wall) { WallCells--; }
            else if (prior == ProvisionalWall) { ProvisionalCells--; }

            if (next == Floor) { FloorCells++; }
            else if (next == Wall) { WallCells++; }
            else if (next == ProvisionalWall) { ProvisionalCells++; }
        }

        /// <summary>
        /// Marks a frontier as walked and dry. This is the exploration memory and the whole reason a
        /// dead end costs one visit: a tried frontier is never chosen again, so the next choice is
        /// the nearest untried one -- which, from a dead end, is the way back to the junction.
        /// </summary>
        public void MarkTried(int index)
        {
            if (!Available || index < 0 || index >= _tried.Length)
            {
                return;
            }
            _tried[index] = 1;
        }

        public bool IsTried(int index)
        {
            return !Available || index < 0 || index >= _tried.Length || _tried[index] != 0;
        }

        /// <summary>True when this floor cell has a 4-neighbour that has never been painted.</summary>
        public bool HasUnknownNeighbour(int index)
        {
            if (!Available || index < 0 || index >= _cells.Length || _cells[index] != Floor)
            {
                return false;
            }
            int x = index % Width;
            int y = index / Width;
            if (x + 1 < Width && _cells[index + 1] == Unknown) { return true; }
            if (x > 0 && _cells[index - 1] == Unknown) { return true; }
            if (y + 1 < Height && _cells[index + Width] == Unknown) { return true; }
            if (y > 0 && _cells[index - Width] == Unknown) { return true; }
            return false;
        }

        /// <summary>
        /// The number of cells this map would be willing to walk to next: a floor cell with an
        /// unpainted neighbour that has not already been spent. It is a scan, paid once per report.
        /// </summary>
        public long FrontierCount()
        {
            if (!Available)
            {
                return 0;
            }
            long count = 0;
            for (int i = 0; i < _cells.Length; i++)
            {
                if (_tried[i] == 0 && HasUnknownNeighbour(i))
                {
                    count++;
                }
            }
            return count;
        }

        // ===================================================================================
        // Search
        // ===================================================================================

        /// <summary>What one breadth-first walk found. All indices are into the map's arrays.</summary>
        public struct SearchResult
        {
            /// <summary>True when the search actually started (the player's cell is known floor).</summary>
            public bool Started;

            /// <summary>The goal's own cell, when it is known floor and the walk reached it; else -1.</summary>
            public int GoalNode;

            /// <summary>The untried frontier nearest by path length; -1 when there is none.</summary>
            public int FrontierNode;

            /// <summary>Path length in cells to <see cref="FrontierNode"/>.</summary>
            public int FrontierDepth;

            /// <summary>The reachable floor cell nearest the goal, whether or not it is a frontier.</summary>
            public int NearestGoalNode;

            /// <summary>Squared distance in cells from <see cref="NearestGoalNode"/> to the goal point.</summary>
            public float NearestGoalSquared;

            /// <summary>Floor cells this walk expanded.</summary>
            public int Visited;

            /// <summary>True when <see cref="DungeonSolver.PlanNodeBudget"/> stopped the walk early.</summary>
            public bool BudgetHit;
        }

        /// <summary>
        /// One breadth-first walk from the player over known FLOOR cells. It answers the three
        /// questions in the file header in a single pass, because a dungeon asks all three every
        /// time the map grows and a second pass would double the frame cost.
        ///
        /// It is a breadth-first walk and not an A*: the map is uniform-cost, the search is bounded
        /// by a node budget, and the frontier's answer is "nearest by path length", which is the
        /// depth BFS gives for free. A tie between two frontiers is broken by distance to the goal,
        /// so exploring toward the boss costs nothing when the distances are equal.
        /// </summary>
        public SearchResult Search(int startX, int startY, float goalX, float goalY, int nodeBudget)
        {
            SearchResult result = default(SearchResult);
            result.GoalNode = -1;
            result.FrontierNode = -1;
            result.NearestGoalNode = -1;
            result.NearestGoalSquared = float.MaxValue;
            if (!Available || _cells == null || _queue == null)
            {
                return result;
            }
            if (startX < 0 || startY < 0 || startX >= Width || startY >= Height)
            {
                return result;
            }
            int start = (startY * Width) + startX;
            if (_cells[start] != Floor)
            {
                return result;
            }
            result.Started = true;

            _seenGeneration++;
            if (_seenGeneration == 0)
            {
                Array.Clear(_seen, 0, _seen.Length);
                _seenGeneration = 1;
            }

            int goalCellX = (int)goalX;
            int goalCellY = (int)goalY;
            int goalIndex = -1;
            if (goalCellX >= 0 && goalCellY >= 0 && goalCellX < Width && goalCellY < Height)
            {
                int candidate = (goalCellY * Width) + goalCellX;
                if (_cells[candidate] == Floor)
                {
                    goalIndex = candidate;
                }
            }

            int head = 0;
            int tail = 0;
            _queue[tail++] = start;
            _seen[start] = _seenGeneration;
            _parent[start] = -1;
            _depth[start] = 0;

            int bestDepth = int.MaxValue;
            float bestGoal2 = float.MaxValue;
            int budget = nodeBudget < 0 ? 0 : nodeBudget;

            while (head < tail)
            {
                if (result.Visited >= budget)
                {
                    result.BudgetHit = true;
                    break;
                }
                int current = _queue[head++];
                result.Visited++;

                if (current == goalIndex)
                {
                    result.GoalNode = current;
                    break;
                }

                float d2 = DistanceSquared(current, goalX, goalY);
                if (d2 < result.NearestGoalSquared)
                {
                    result.NearestGoalSquared = d2;
                    result.NearestGoalNode = current;
                }

                if (_tried[current] == 0 && HasUnknownNeighbour(current))
                {
                    int depth = _depth[current];
                    if (depth < bestDepth || (depth == bestDepth && d2 < bestGoal2))
                    {
                        bestDepth = depth;
                        bestGoal2 = d2;
                        result.FrontierNode = current;
                        result.FrontierDepth = depth;
                    }
                }

                int cx = current % Width;
                int cy = current / Width;
                int nextDepth = _depth[current] + 1;
                for (int d = 0; d < 4; d++)
                {
                    int nx;
                    int ny;
                    if (d == 0) { nx = cx + 1; ny = cy; }
                    else if (d == 1) { nx = cx - 1; ny = cy; }
                    else if (d == 2) { nx = cx; ny = cy + 1; }
                    else { nx = cx; ny = cy - 1; }
                    if (nx < 0 || ny < 0 || nx >= Width || ny >= Height)
                    {
                        continue;
                    }
                    int n = (ny * Width) + nx;
                    if (_seen[n] == _seenGeneration || _cells[n] != Floor)
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
            return result;
        }

        /// <summary>
        /// The parent chain to <paramref name="target"/>, player first, in the map's own reusable
        /// path buffer. Returns the node count, or 0 when the target was not reached by the last
        /// search or the chain is not a finite parent walk.
        ///
        /// THE BOUND IS THE MAP'S OWN SIZE. A parent chain in a breadth-first tree cannot loop, but
        /// `_parent` is a plain array and a stale entry from another generation is a plausible
        /// number; the bound turns a corrupt chain into "no path" rather than a spun frame.
        /// </summary>
        public int BuildPath(int target)
        {
            PathCount = 0;
            if (!Available || _cells == null || target < 0 || target >= _cells.Length)
            {
                return 0;
            }
            if (_seen[target] != _seenGeneration)
            {
                // THE TARGET WAS NOT REACHED BY THE LAST SEARCH. `_parent` is a plain array and a
                // stale entry from an earlier generation is a plausible number; building a route
                // from it would walk a path that was never computed.
                return 0;
            }
            int count = 0;
            for (int node = target; node >= 0 && count <= _cells.Length; node = _parent[node])
            {
                count++;
            }
            if (count <= 0 || count > _cells.Length)
            {
                return 0;
            }
            if (_path.Length < count)
            {
                // Grow once to the chain length; a route is re-used for its lifetime.
                _path = new int[count < 16 ? 16 : count];
            }
            int index = count - 1;
            for (int node = target; node >= 0 && index >= 0; node = _parent[node])
            {
                _path[index--] = node;
            }
            PathCount = count;
            return count;
        }

        /// <summary>One node of the path built by <see cref="BuildPath"/>, player first.</summary>
        public int PathNodeAt(int index)
        {
            if (index < 0 || index >= PathCount)
            {
                return -1;
            }
            return _path[index];
        }

        /// <summary>
        /// The breadth-first parent of a node from the last search, or -1 for the start. It is the
        /// "way in" to a committed frontier, which is what tells a frontier that OPENED (a floor
        /// neighbour other than the way in) from one that closed (only the way back).
        /// </summary>
        public int ParentOf(int index)
        {
            if (!Available || index < 0 || index >= _parent.Length || _seen[index] != _seenGeneration)
            {
                return -1;
            }
            return _parent[index];
        }

        // ===================================================================================
        // Geometry
        // ===================================================================================

        public float CellX(int index) => (index % Width) + 0.5f;

        public float CellY(int index) => (index / Width) + 0.5f;

        public float DistanceSquared(int index, float x, float y)
        {
            float dx = CellX(index) - x;
            float dy = CellY(index) - y;
            return (dx * dx) + (dy * dy);
        }

        public float Distance(int index, float x, float y)
        {
            return (float)Math.Sqrt(DistanceSquared(index, x, y));
        }
    }
}
