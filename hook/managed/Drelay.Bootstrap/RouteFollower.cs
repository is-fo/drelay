// RouteFollower.cs -- the other half of `PLAN-AUTOFARM.md` section 7: replay a recorded route.
//
// =====================================================================================
// THE WHOLE LOOP, IN THE PLAN'S OWN WORDS (section 7.3)
// =====================================================================================
//
//     RouteFollower, per frame:
//         if reached(waypoint[i], RadiusReached = 1.0):
//             i = (i + 1) % count
//         goal = waypoint[i] + deviation(i, lap)
//         publish goal point                       (section 4's snapshot goal)
//
// That last line is the entire integration, and it is why this file is small: the follower publishes
// THE SAME POINT GOAL THE FARMER PUBLISHES, so everything downstream -- the planner, the admission
// rules, the score terms, the hysteresis -- is the code built for item 1 and row 2, unchanged. A
// route is a different answer to "where should the goal be", and nothing else.
//
// =====================================================================================
// THE DEVIATION IS THE DESIGN, NOT A FLOURISH
// =====================================================================================
//
// Section 7.3 gives its four properties and each one is a decision:
//
//   * **BOUNDED**, magnitude <= `RouteDeviationUnits`, default 1.0 unit and at most a third of the
//     resample spacing -- so the deviation can never move the goal past the next waypoint, which is
//     what would make the follower skip one;
//   * **SMOOTH IN TIME, NOT PER FRAME** -- a slow function of `(i, lap, clock)`, so the character
//     DRIFTS rather than jitters. Per-frame random offsets would ADD stutter to a follower whose
//     whole point is to look unhurried, and the project has already measured what angle jitter costs
//     (`SPIKE3.2-OFFLINE-SOLVER.md` section 13.2);
//   * **RE-DRAWN PER LAP** with a lap seed, so the second lap is not the first;
//   * **SNAPPED TO WALKABLE**, so a deviation cannot push the goal into a wall.
//
// The function is a sum of two sines, which allocates nothing, has an exact bound, and has a
// second derivative small enough that "smooth" is a number the harness can assert rather than an
// impression (R4).
//
// =====================================================================================
// OBSTACLE AVOIDANCE IS NOT HERE, AND THAT IS SECTION 7.4'S POINT
// =====================================================================================
//
// The follower's goal is a POINT, so it goes through `PathPlanner`: A* on the client's own collision
// grid, 8-connected with the diagonal rule, danger-aware, `WallCrossings` asserted at 0. A building
// between the player and the next waypoint is a path around it, and that is the `corridor`
// scenario's existing job. **A route that walks through a bullet is not followed into the bullet**,
// because the route only ever ranks candidates that already passed admission.

using System;
using System.Globalization;

namespace Drelay.Bootstrap
{
    /// <summary>
    /// Walks a <see cref="Route"/> and publishes its current waypoint, with deviation, as the frame's
    /// point goal.
    /// </summary>
    public sealed class RouteFollower
    {
        /// <summary>The route being followed, or null.</summary>
        public Route Route { get; private set; }

        /// <summary>The waypoint index the follower is walking at.</summary>
        public int Index { get; private set; }

        /// <summary>How many times the follower has wrapped to the first waypoint.</summary>
        public int Lap { get; private set; }

        /// <summary>Waypoint advances over the run. `PLAN-AUTOFARM.md` section 7.5's R3 reads it.</summary>
        public long Advances { get; private set; }

        /// <summary>Laps completed.</summary>
        public long Laps { get; private set; }

        /// <summary>The last published goal's deviation magnitude, world units. For the log.</summary>
        public float LastDeviation { get; private set; }

        /// <summary>
        /// Times a deviation landed in a wall and was pulled out. `PLAN-AUTOFARM.md` section 7.3's
        /// fourth property is "snapped to walkable"; this is the count that says it ever had to be.
        /// A zero count on a run with a narrow corridor is not a pass, it is a run where the
        /// deviation happened to stay on the floor -- which is why the number is reported rather than
        /// asserted.
        /// </summary>
        public long DeviationSnaps { get; private set; }

        /// <summary>True when a route is loaded and usable.</summary>
        public bool Active => Route != null && Route.Usable;

        /// <summary>
        /// Loads a route. False with a named reason -- the route is refused, not degraded, which is
        /// section 7.1's own discipline for the map check.
        /// </summary>
        public bool SetRoute(Route route, out string why)
        {
            why = null;
            if (route == null)
            {
                why = "no route";
                return false;
            }
            if (!route.Usable)
            {
                why = "route '" + route.Name + "' carries " + route.Points.Count
                      + " point(s); two are needed";
                return false;
            }
            Route = route;
            Index = 0;
            Lap = 0;
            Advances = 0;
            Laps = 0;
            LastDeviation = 0.0f;
            DeviationSnaps = 0;
            return true;
        }

        /// <summary>Drops the route. Called on a world change, where a route stops being valid.</summary>
        public void Clear()
        {
            Route = null;
            Index = 0;
            Lap = 0;
        }

        /// <summary>Zeroes the run's counters without dropping the route.</summary>
        public void ResetCounters()
        {
            Advances = 0;
            Laps = 0;
            DeviationSnaps = 0;
        }

        /// <summary>
        /// One frame: advance if the current waypoint is reached, then publish the goal.
        ///
        /// THE REACHED TEST IS A RADIUS AND NOT AN EXACT HIT, because a waypoint is a coordinate and
        /// the character moves in steps: an exact reach is a waypoint nobody ever arrives at, and the
        /// follower would stall on it. `RouteReachUnits` is the radius, and the plan's own starting
        /// value is 1.0.
        ///
        /// IT ADVANCES BY AT MOST ONE WAYPOINT PER FRAME, WHICH IS DELIBERATE. A character that
        /// teleports -- a portal, a knockback, a lag spike -- lands past several waypoints at once,
        /// and a follower that skipped them all would take the route's shortcut through whatever the
        /// waypoints were going around. One step per frame re-walks the route from where the character
        /// now is, and the plan's "loop" is exactly that.
        /// </summary>
        public bool Step(float px, float py, DodgeConfig config, float clockMs, CollisionWindow grid,
                         out float goalX, out float goalY)
        {
            goalX = 0.0f;
            goalY = 0.0f;
            if (!Active)
            {
                return false;
            }

            RoutePoint current = Route.Points[Index];
            float deviation = Deviation(Route.Name, Index, Lap, clockMs, config.RouteDeviationUnits);

            // THE ADVANCE TEST ALLOWS FOR THE DEVIATION, AND R3 IS WHY IT HAS TO.
            //
            // Section 7.3's loop is `if reached(waypoint[i], 1.0): i = i + 1`, and the goal it then
            // publishes is `waypoint[i] + deviation`. Those two lines disagree whenever the deviation
            // is not zero: the character walks to the DEVIATED point, arrives there, and is by
            // construction `|deviation|` away from the waypoint it is being tested against. With the
            // plan's own starting values -- a 1.0-unit deviation and a 1.0-unit reach -- the loop is
            // exactly marginal, and the harness measured what that means: `--scenario`'s R3 fixture
            // stalled on waypoint 5 of 11 and completed ZERO laps in six thousand frames, with the
            // planner reporting a route on every one of them.
            //
            // So the radius the test uses is `RouteReachUnits + |deviation|`: "within reach of the
            // waypoint, allowing for the offset we asked you to walk with". It is bounded by the
            // plan's own limit on the deviation (a third of a waypoint's spacing), so it cannot let
            // the follower advance from a point that is not near the route -- which is the property
            // the exact test existed for.
            float reach = (config.RouteReachUnits > 0.0f ? config.RouteReachUnits : 1.0f)
                          + Math.Abs(deviation);
            float dx = current.X - px;
            float dy = current.Y - py;
            if (((dx * dx) + (dy * dy)) <= reach * reach)
            {
                Index++;
                Advances++;
                if (Index >= Route.Points.Count)
                {
                    // A LAP IS A WRAP, AND THE SEED CHANGES WITH IT -- see the header: "re-drawn per
                    // lap with a lap seed, so the second lap is not the first".
                    Index = 0;
                    Lap++;
                    Laps++;
                }
                current = Route.Points[Index];
                deviation = Deviation(Route.Name, Index, Lap, clockMs, config.RouteDeviationUnits);
            }

            LastDeviation = Math.Abs(deviation);

            // THE OFFSET IS ALONG THE ROUTE'S PERPENDICULAR AT THIS WAYPOINT, not along a fixed axis
            // and not radially from the route. A radial offset on a straight leg does nothing (the
            // point is already on the line) and a fixed-axis offset drags the character sideways on a
            // leg that runs along that axis. The perpendicular is the only direction that is
            // "beside the route" wherever the route happens to be going.
            RoutePerpendicular(Index, out float nx, out float ny);
            goalX = current.X + (nx * deviation);
            goalY = current.Y + (ny * deviation);

            // SNAPPED TO WALKABLE, the fourth property. The same rule as `Route.Build`'s waypoint
            // snap and for the same reason: a goal inside a wall is a body the planner cannot route
            // to, so the frame would report `NoTarget` and the route term would contribute nothing.
            // The search here is a short perpendicular walk, because a deviation is at most a third
            // of the waypoint spacing and cannot be far from the route.
            if (grid.Present && grid.InWindow(goalX, goalY) && !grid.Walkable(goalX, goalY))
            {
                float magnitude = Math.Abs(deviation);
                bool found = false;
                for (int step = 1; step <= 3 && !found; step++)
                {
                    float sign = deviation >= 0.0f ? -1.0f : 1.0f;
                    float candidate = (magnitude * sign) * ((3 - step) / 3.0f);
                    float cx = current.X + (nx * candidate);
                    float cy = current.Y + (ny * candidate);
                    if (grid.Walkable(cx, cy))
                    {
                        goalX = cx;
                        goalY = cy;
                        found = true;
                    }
                }
                if (found)
                {
                    DeviationSnaps++;
                    LastDeviation = Distance(current.X, current.Y, goalX, goalY);
                }
                else
                {
                    // NOTHING WALKABLE BESIDE THE ROUTE: the goal is the WAYPOINT ITSELF. A deviation
                    // that cannot be spent is not spent -- the alternative is a goal inside scenery.
                    goalX = current.X;
                    goalY = current.Y;
                    LastDeviation = 0.0f;
                }
            }
            return true;
        }

        /// <summary>The unit vector perpendicular to the route at a waypoint.</summary>
        private void RoutePerpendicular(int index, out float nx, out float ny)
        {
            int count = Route.Points.Count;
            int next = index + 1 < count ? index + 1 : 0;
            int previous = index > 0 ? index - 1 : count - 1;
            float dx = Route.Points[next].X - Route.Points[previous].X;
            float dy = Route.Points[next].Y - Route.Points[previous].Y;
            float length = (float)Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= 1e-6f)
            {
                // A DEGENERATE WAYPOINT HAS NO PERPENDICULAR, so the deviation is spent on +x. It is
                // reachable -- two coincident points survive a resample when the route doubles back
                // exactly -- and the alternative is a NaN heading for the whole frame.
                nx = 0.0f;
                ny = 1.0f;
                return;
            }
            nx = -dy / length;
            ny = dx / length;
        }

        /// <summary>
        /// The deviation, in world units along the route's perpendicular. See the header for the four
        /// properties this function is built to have.
        ///
        /// **IT IS A PURE FUNCTION OF ITS ARGUMENTS.** Nothing here reads a clock, a random generator
        /// or any state, so the same (route, index, lap, time) always produces the same offset --
        /// which is what makes R5's "twice with the same lap seed produces identical goals" a
        /// statement about the shipped code rather than about a seed a test happened to set.
        ///
        /// **THE BOUND IS EXACT AND THAT IS WHY IT IS TWO SINES AND NOT ONE PLUS A CLAMP.** `sin` is
        /// in [-1, 1] and the second contributes half, so the sum is in [-1.5, 1.5]; dividing by 1.5
        /// gives exactly [-1, 1]. A single sine plus a random walk would need clamping, and a clamped
        /// noise function has a flat spot at every excursion -- the deviation would visibly stop
        /// moving at its extremes, which is the one thing a drift must not do.
        ///
        /// **THE INDEX IS IN THE PHASE, AND ITS SIZE IS CHOSEN AGAINST THE SPACING.** A phase offset
        /// of `index * IndexPhaseStep` makes consecutive waypoints deviate differently, which is what
        /// the plan's `deviation(i, lap, clock)` asks for. The step must not be so large that
        /// advancing a waypoint JUMPS the goal sideways: the phase derivative of the sum is at most
        /// `(1 + 0.5 * 1.7) / 1.5` per radian, so 0.35 rad moves the goal by at most 0.44 of
        /// `RouteDeviationUnits`. At the shipped values that is 0.44 units against a 3-unit waypoint
        /// spacing, so the plan's "the deviation can never make the follower skip a waypoint" (a third
        /// of the spacing, 1.0 unit) still holds with room to spare.
        /// </summary>
        public static float Deviation(string routeName, int index, int lap, float clockMs,
                                      float deviationUnits)
        {
            if (!(deviationUnits > 0.0f))
            {
                return 0.0f;
            }
            float phase = LapPhase(routeName, lap);
            float seconds = clockMs * 0.001f;
            // TWO LOW FREQUENCIES, AND "LOW" IS THE PROPERTY THAT MATTERS. 0.6 and 1.7 rad/s are
            // periods of about ten and four seconds: slow enough that the character is seen to drift
            // over a walk rather than to vibrate, and fast enough that a lap does not repeat the same
            // shape. The project's own measurement of what fast noise costs is
            // SPIKE3.2-OFFLINE-SOLVER.md section 13.2.
            float a = (float)Math.Sin((seconds * 0.6f) + phase + (index * IndexPhaseStep));
            float b = (float)Math.Sin((seconds * 1.7f) + (phase * 1.7f) + (index * IndexPhaseStep));
            return ((a + (0.5f * b)) / 1.5f) * deviationUnits;
        }

        /// <summary>Radians of phase per waypoint. See `Deviation` for how it is bounded.</summary>
        private const float IndexPhaseStep = 0.35f;

        /// <summary>
        /// The per-lap phase, from the route's own name and the lap number.
        ///
        /// IT IS A HASH AND NOT `new Random(lap)`, for two reasons: this is on the frame path at a few
        /// waypoint advances a second and an allocation per frame is the one thing this project
        /// refuses, and a `Random` seeded by lap alone would give the same phase to every route's
        /// second lap. FNV-1a over the name and the lap gives a phase that is stable for a file, new
        /// for a lap, and free.
        /// </summary>
        private static float LapPhase(string routeName, int lap)
        {
            uint hash = 2166136261u;
            if (routeName != null)
            {
                for (int i = 0; i < routeName.Length; i++)
                {
                    hash = (hash ^ routeName[i]) * 16777619u;
                }
            }
            hash = (hash ^ (uint)lap) * 16777619u;
            return (hash % 10000u) * ((float)(2.0 * Math.PI) / 10000.0f);
        }

        private static float Distance(float ax, float ay, float bx, float by)
        {
            float dx = bx - ax;
            float dy = by - ay;
            return (float)Math.Sqrt((dx * dx) + (dy * dy));
        }

        /// <summary>The follower's own numbers, for the run's log.</summary>
        public string Describe()
        {
            if (!Active)
            {
                return "route: none";
            }
            return string.Format(CultureInfo.InvariantCulture,
                "route '{0}': {1} point(s), at {2}/{1}, lap {3}, {4} advance(s), deviation {5:0.00}",
                Route.Name, Route.Points.Count, Index, Lap, Advances, LastDeviation);
        }
    }
}
