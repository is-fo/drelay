// Route.cs -- a recorded walk, turned into a coordinate list a follower can repeat.
//
// =====================================================================================
// WHAT THIS IS, IN THE OPERATOR'S OWN SPECIFICATION
// =====================================================================================
//
// `PLAN-AUTOFARM.md` section 7's restatement: *"record a path, convert it to a coordinate list that
// can be repeated, capture the general shape rather than the exact path, add deviation so it looks
// natural, and pathfind around anything blocking it."* This file is the first three clauses; the
// fourth is free, because the follower publishes a POINT GOAL and the point goal already goes
// through the planner built for item 1's enemy bodies (section 7.4).
//
// =====================================================================================
// IT IS PURE DATA AND IT COMPILES WITH NO GAME IN SIGHT, LIKE THE SOLVER
// =====================================================================================
//
// Everything here is `float`, `int`, `string` and the solver's own `CollisionWindow` -- no
// `FieldInfo`, no `object`, no `DarzaCore.*`. That is the same contract `DodgeSnapshot.cs` states,
// and it is what lets the simplifier, the resampler and the deviation be measured on the floor of an
// office rather than against a live character (`SolverHarness.csproj` compiles this file).
//
// =====================================================================================
// THE MAP BLOCK IS THE ROUTE'S VALIDITY CONDITION AND NOT METADATA
// =====================================================================================
//
// Section 7.1: *"a route recorded in one realm is not replayed in another, and replaying it into a
// different map is a bug, not a fallback."* So `MatchesMap` refuses by NAME rather than degrading,
// which is the same "refuse and name it" discipline the fingerprint gate uses. A route whose map
// block does not match is not a route that nearly works; it is a route through scenery that is not
// there.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Drelay.Bootstrap
{
    /// <summary>One sample of a walk: a position and the milliseconds since the recording started.</summary>
    public struct RoutePoint
    {
        public float X;
        public float Y;

        /// <summary>Milliseconds since the first sample. Carried for the file, not used to replay.</summary>
        public int Ms;
    }

    /// <summary>
    /// The sampling rule of `PLAN-AUTOFARM.md` section 7.1: *a fixed time step AND a minimum distance*
    /// filter.
    ///
    /// BOTH CLAUSES, AND THE DISTANCE ONE IS NOT DECORATION. Section 7.1 says why in as many words:
    /// *"the distance filter is what keeps a stationary player from filling the file"*. A 10 Hz
    /// sample of a character standing in the lobby for two minutes is twelve hundred identical
    /// points, and after simplification they become two -- so the time step alone would not corrupt
    /// the route, but it would make the FILE the wrong size to read and the simplification the only
    /// thing standing between the operator and a wall of noise.
    ///
    /// IT IS PURE AND IT IS HERE RATHER THAN IN THE HOOK, so the rule is testable without the game:
    /// `RouteRecorder` reads the player's position and hands it to this, and the harness drives this
    /// directly (R1).
    /// </summary>
    public sealed class RouteSampler
    {
        /// <summary>The samples taken so far, in order.</summary>
        public readonly List<RoutePoint> Samples = new List<RoutePoint>();

        private float _intervalMs = 100.0f;
        private float _minDistance = 1.0f;
        private float _startMs;
        private float _lastMs = float.NaN;
        private float _lastX;
        private float _lastY;

        /// <summary>How many times `Observe` was called and did not take a sample.</summary>
        public long Suppressed { get; private set; }

        /// <summary>Starts a recording. `intervalMs` is the time step, 100 for the plan's 10 Hz.</summary>
        public void Reset(float intervalMs, float minDistance)
        {
            Samples.Clear();
            _intervalMs = intervalMs > 0.0f ? intervalMs : 100.0f;
            _minDistance = minDistance > 0.0f ? minDistance : 0.0f;
            _startMs = 0.0f;
            _lastMs = float.NaN;
            Suppressed = 0;
        }

        /// <summary>
        /// Offers one frame. True when the sample was taken.
        ///
        /// THE FIRST SAMPLE IS ALWAYS TAKEN, whatever the filters say, because a route with no start
        /// is not a route. After that a sample needs BOTH: enough time since the last one, and enough
        /// distance from it.
        /// </summary>
        public bool Observe(float x, float y, float clockMs)
        {
            if (float.IsNaN(_lastMs))
            {
                _startMs = clockMs;
                Take(x, y, clockMs);
                return true;
            }
            if (clockMs - _lastMs < _intervalMs)
            {
                Suppressed++;
                return false;
            }
            float dx = x - _lastX;
            float dy = y - _lastY;
            if ((dx * dx) + (dy * dy) < _minDistance * _minDistance)
            {
                Suppressed++;
                return false;
            }
            Take(x, y, clockMs);
            return true;
        }

        private void Take(float x, float y, float clockMs)
        {
            Samples.Add(new RoutePoint
            {
                X = x,
                Y = y,
                // THE TIME IS MILLISECONDS SINCE THE RECORDING STARTED and not a wall clock, so a
                // file is comparable with another file from another session without knowing when
                // either was taken.
                Ms = (int)(clockMs - _startMs),
            });
            _lastX = x;
            _lastY = y;
            _lastMs = clockMs;
        }
    }

    /// <summary>
    /// A recorded walk, simplified and resampled, plus the map it belongs to.
    ///
    /// THE POINTS ARE THE ROUTE AND THE RAW SAMPLES ARE NOT, which is section 7.2's whole point: the
    /// simplification and the resample happen AT RECORD TIME so that the file is the route. A file
    /// that carried the raw 10 Hz samples would make every reader re-derive the shape, and two
    /// readers that did it slightly differently would follow two different paths through one file.
    /// </summary>
    public sealed class Route
    {
        /// <summary>The file format's version, so a future change can refuse an old file by name.</summary>
        public const int FormatVersion = 1;

        public string Name = "";

        /// <summary>The map the walk was recorded in -- the validity condition. See the header.</summary>
        public string MapName = "";
        public string MapAsset = "";
        public int MapWidth;
        public int MapHeight;

        /// <summary>Free text: where and when the recording was taken. Evidence, never read back.</summary>
        public string RecordedFrom = "";

        public readonly List<RoutePoint> Points = new List<RoutePoint>();

        /// <summary>How many raw samples the walk had, before simplification. Evidence for R1.</summary>
        public int RawSamples;

        /// <summary>How much the route moved between its first and last sample, world units.</summary>
        public float RawLength;

        /// <summary>True when the route has enough points to be worth following.</summary>
        public bool Usable => Points.Count >= 2;

        /// <summary>
        /// Whether this route belongs to the map the client is currently in. See the header: a
        /// mismatch is a REFUSAL and not a degradation.
        ///
        /// THE COMPARISON IS ON THE NAME, WITH THE ASSET AS A SECOND CHANCE, and that is a deliberate
        /// split rather than a loose one. `MapInfo.Name` is the human-facing string the operator's own
        /// log will carry ("Plains of Peril") and is the primary key section 8.2 proposes for the
        /// dungeon whitelist; `AssetName` is the client's own resource name and is the fallback for a
        /// build where `Name` turned out to be localised or empty. Either matching is enough, and
        /// both being empty on the RECORDING is a refusal, because a route with no map block is a
        /// route nobody can say the world of.
        /// </summary>
        public bool MatchesMap(string name, string asset, int width, int height)
        {
            bool haveName = !string.IsNullOrEmpty(MapName);
            bool haveAsset = !string.IsNullOrEmpty(MapAsset);
            if (!haveName && !haveAsset)
            {
                return false;
            }
            bool nameOk = haveName && string.Equals(MapName, name, StringComparison.Ordinal);
            bool assetOk = haveAsset && string.Equals(MapAsset, asset, StringComparison.Ordinal);
            if (!nameOk && !assetOk)
            {
                return false;
            }
            // THE DIMENSIONS ARE A THIRD, INDEPENDENT CHECK AND NOT A REPLACEMENT FOR THE TWO ABOVE.
            // A map reissued with the same name and different bounds is a map whose scenery moved, so
            // a route recorded in the old one is as wrong there as it is in another realm. Zero on
            // either side means "not recorded" and the check is skipped rather than failed.
            if (MapWidth > 0 && width > 0 && MapWidth != width)
            {
                return false;
            }
            if (MapHeight > 0 && height > 0 && MapHeight != height)
            {
                return false;
            }
            return true;
        }

        /// <summary>Why <see cref="MatchesMap"/> said no, for the log.</summary>
        public string MapMismatch(string name, string asset, int width, int height)
        {
            return "route '" + Name + "' was recorded in " + DescribeMap() + " and this world is "
                   + (string.IsNullOrEmpty(name) ? "(no name)" : name)
                   + (string.IsNullOrEmpty(asset) ? "" : "/" + asset)
                   + " " + width + "x" + height;
        }

        private string DescribeMap()
        {
            return (string.IsNullOrEmpty(MapName) ? "(no name)" : MapName)
                   + (string.IsNullOrEmpty(MapAsset) ? "" : "/" + MapAsset)
                   + " " + MapWidth + "x" + MapHeight;
        }

        // ===================================================================================
        // Build: simplify, resample, snap
        // ===================================================================================

        /// <summary>
        /// Turns a raw walk into a route: Douglas-Peucker, then resample, then snap.
        ///
        /// THE ORDER IS SECTION 7.2'S AND IT IS NOT ARBITRARY. Simplify FIRST, so the resampler walks
        /// the shape rather than the noise; resample SECOND, so the follower has a fixed step to count
        /// on; snap LAST, so that snapping cannot move a point far enough to change the shape the
        /// simplification agreed on.
        /// </summary>
        public static Route Build(string name, string mapName, string mapAsset, int mapWidth,
                                  int mapHeight, string recordedFrom, IList<RoutePoint> raw,
                                  float tolerance, float spacing, CollisionWindow grid)
        {
            var route = new Route();
            route.Name = name ?? "";
            route.MapName = mapName ?? "";
            route.MapAsset = mapAsset ?? "";
            route.MapWidth = mapWidth;
            route.MapHeight = mapHeight;
            route.RecordedFrom = recordedFrom ?? "";
            route.RawSamples = raw == null ? 0 : raw.Count;
            if (raw == null || raw.Count == 0)
            {
                return route;
            }
            route.RawLength = Length(raw);

            var kept = new List<RoutePoint>(raw);
            if (tolerance > 0.0f && kept.Count > 2)
            {
                kept = Simplify(kept, tolerance);
            }
            List<RoutePoint> spaced = Resample(kept, spacing);
            for (int i = 0; i < spaced.Count; i++)
            {
                RoutePoint p = spaced[i];
                if (grid.Present)
                {
                    SnapToWalkable(ref p, spaced, i, grid);
                }
                route.Points.Add(p);
            }
            return route;
        }

        /// <summary>
        /// Douglas-Peucker, iteratively.
        ///
        /// THIS IS LITERALLY "THE GENERAL SHAPE, NOT THE EXACT PATH" (section 7.2 item 1). A
        /// hand-drawn curve through an open room becomes three points. The implementation is the
        /// stack-based form rather than the recursive one because a 10 Hz walk over ten minutes is
        /// six thousand samples and a recursion depth proportional to the walk is a stack the frame
        /// path should not be gambling with -- this runs at record time, but the same function is
        /// what the harness runs on fixtures and a test that can overflow is a test that lies.
        /// </summary>
        public static List<RoutePoint> Simplify(IList<RoutePoint> points, float tolerance)
        {
            int n = points.Count;
            var keep = new bool[n];
            keep[0] = true;
            keep[n - 1] = true;

            var stack = new Stack<int>();
            stack.Push(0);
            stack.Push(n - 1);
            float tolerance2 = tolerance * tolerance;

            while (stack.Count >= 2)
            {
                int last = stack.Pop();
                int first = stack.Pop();
                if (last <= first + 1)
                {
                    continue;
                }
                int worst = -1;
                float worstD2 = 0.0f;
                for (int i = first + 1; i < last; i++)
                {
                    float d2 = PerpendicularDistanceSquared(points[i], points[first], points[last]);
                    if (d2 > worstD2)
                    {
                        worstD2 = d2;
                        worst = i;
                    }
                }
                if (worst >= 0 && worstD2 > tolerance2)
                {
                    keep[worst] = true;
                    stack.Push(first);
                    stack.Push(worst);
                    stack.Push(worst);
                    stack.Push(last);
                }
            }

            var result = new List<RoutePoint>(n);
            for (int i = 0; i < n; i++)
            {
                if (keep[i])
                {
                    result.Add(points[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// The squared distance from `p` to the SEGMENT `a`-`b`, which is the quantity Douglas-Peucker
        /// is defined against.
        ///
        /// IT IS THE SEGMENT AND NOT THE INFINITE LINE, and the difference is not pedantry: a walk
        /// that doubles back has points whose perpendicular distance to the *line* through two far
        /// samples is near zero while their distance to the *segment* is metres. The line form would
        /// discard the whole doubling-back as "straight".
        /// </summary>
        private static float PerpendicularDistanceSquared(RoutePoint p, RoutePoint a, RoutePoint b)
        {
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float length2 = (dx * dx) + (dy * dy);
            if (length2 <= 1e-12f)
            {
                float px0 = p.X - a.X;
                float py0 = p.Y - a.Y;
                return (px0 * px0) + (py0 * py0);
            }
            float t = (((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / length2;
            if (t < 0.0f) { t = 0.0f; }
            if (t > 1.0f) { t = 1.0f; }
            float qx = a.X + (t * dx);
            float qy = a.Y + (t * dy);
            float ex = p.X - qx;
            float ey = p.Y - qy;
            return (ex * ex) + (ey * ey);
        }

        /// <summary>
        /// Resamples a polyline to a MINIMUM spacing, keeping the endpoints and the shape.
        ///
        /// WHAT IT IS FOR, IN SECTION 7.2'S WORDS: "so the follower has a fixed step to count on".
        /// The follower's whole loop is "am I at waypoint i; if so, i = i + 1", and a route whose
        /// two consecutive points are a hundredth of a unit apart and then four units apart is a
        /// route whose reached-radius means different things at different places.
        ///
        /// IT INSERTS AND IT DOES NOT REMOVE, EXCEPT BY MERGING. A segment longer than the spacing is
        /// subdivided; a segment shorter than it is dropped only when the previous emitted point is
        /// already within the spacing, so the polyline's own corners are never lost. The last point is
        /// always emitted, because a route that stops short of where it was walked to is a route that
        /// changed the walk.
        /// </summary>
        public static List<RoutePoint> Resample(IList<RoutePoint> points, float spacing)
        {
            var result = new List<RoutePoint>();
            if (points == null || points.Count == 0)
            {
                return result;
            }
            result.Add(points[0]);
            if (points.Count == 1)
            {
                return result;
            }
            if (!(spacing > 0.0f))
            {
                for (int i = 1; i < points.Count; i++)
                {
                    result.Add(points[i]);
                }
                return result;
            }

            for (int i = 1; i < points.Count; i++)
            {
                RoutePoint from = result[result.Count - 1];
                RoutePoint to = points[i];
                float dx = to.X - from.X;
                float dy = to.Y - from.Y;
                float distance = (float)Math.Sqrt((dx * dx) + (dy * dy));
                if (distance <= spacing)
                {
                    // TOO CLOSE TO BE A STEP, BUT THE LAST POINT STILL HAS TO LAND. A dropped final
                    // point is the route's own end being edited by its spacing.
                    if (i == points.Count - 1)
                    {
                        result[result.Count - 1] = to;
                    }
                    continue;
                }
                int steps = (int)(distance / spacing);
                if (steps < 1) { steps = 1; }
                // AND THE SUBDIVISION IS EVEN RATHER THAN spacing-EXACT, so the emitted points sit ON
                // the recorded polyline. A spacing-exact walk accumulates a fractional offset at every
                // segment and drifts off the shape the simplification agreed on.
                for (int s = 1; s <= steps; s++)
                {
                    float t = (float)s / steps;
                    result.Add(new RoutePoint
                    {
                        X = from.X + (dx * t),
                        Y = from.Y + (dy * t),
                        // THE TIME IS INTERPOLATED TOO, so the file's `ms` column stays a monotone
                        // clock rather than a sequence with jumps where points were inserted.
                        Ms = from.Ms + (int)((to.Ms - from.Ms) * t),
                    });
                }
                if (i == points.Count - 1)
                {
                    result[result.Count - 1] = to;
                }
            }
            return result;
        }

        /// <summary>
        /// Pulls a route point out of a wall, along the direction of travel.
        ///
        /// SECTION 7.2 ITEM 2: *"a waypoint that lands in a wall (because the recorder sampled
        /// mid-corner) is pulled out along the direction of travel rather than left to trap the
        /// follower"*. Walking outward from the direction of travel and then to either side, rather
        /// than radially, is what makes the pulled point a continuation of the route instead of a
        /// point beside it -- a radial pull on a corner drags the route backwards.
        ///
        /// IT DOES NOTHING WITHOUT A GRID, AND IT DOES NOTHING WHEN THE GRID DOES NOT COVER THE POINT.
        /// `CollisionWindow.Walkable` answers false for an out-of-window position, and "outside the
        /// recorded window" is not evidence of a wall. Moving a point on the strength of a grid that
        /// does not reach it would be the same class of error as admitting a candidate on one.
        /// </summary>
        private static void SnapToWalkable(ref RoutePoint p, IList<RoutePoint> points, int index,
                                           CollisionWindow grid)
        {
            if (!grid.Present || !grid.InWindow(p.X, p.Y))
            {
                return;
            }
            if (grid.Walkable(p.X, p.Y))
            {
                return;
            }

            // The direction of travel at this point, from its neighbours.
            float dx = 0.0f;
            float dy = 0.0f;
            int next = index + 1 < points.Count ? index + 1 : index;
            int previous = index > 0 ? index - 1 : index;
            dx = points[next].X - points[previous].X;
            dy = points[next].Y - points[previous].Y;
            float length = (float)Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= 1e-6f)
            {
                dx = 1.0f;
                dy = 0.0f;
                length = 1.0f;
            }
            dx /= length;
            dy /= length;
            // Perpendicular, for the side steps.
            float px = -dy;
            float py = dx;

            for (int step = 1; step <= SnapSearchCells; step++)
            {
                // FORWARD FIRST, THEN EITHER SIDE, THEN BACKWARD, at each distance. The order is the
                // preference: the nearest point that keeps the character going the way it was going.
                if (TrySnap(ref p, grid, dx * step, dy * step)) { return; }
                if (TrySnap(ref p, grid, (dx * step) + (px * step), (dy * step) + (py * step))) { return; }
                if (TrySnap(ref p, grid, (dx * step) - (px * step), (dy * step) - (py * step))) { return; }
                if (TrySnap(ref p, grid, -dx * step, -dy * step)) { return; }
            }
            // NOTHING FOUND: the point stays where it is. That is the honest answer -- the follower
            // will plan around it or refuse the step, and either is better than a point invented at
            // an arbitrary distance.
        }

        /// <summary>How far the snap search looks, in cells. Three is enough for a corner sample.</summary>
        private const int SnapSearchCells = 3;

        private static bool TrySnap(ref RoutePoint p, CollisionWindow grid, float dx, float dy)
        {
            float x = p.X + dx;
            float y = p.Y + dy;
            if (!grid.Walkable(x, y))
            {
                return false;
            }
            p.X = x;
            p.Y = y;
            return true;
        }

        private static float Length(IList<RoutePoint> points)
        {
            float total = 0.0f;
            for (int i = 1; i < points.Count; i++)
            {
                float dx = points[i].X - points[i - 1].X;
                float dy = points[i].Y - points[i - 1].Y;
                total += (float)Math.Sqrt((dx * dx) + (dy * dy));
            }
            return total;
        }

        /// <summary>The route's own length, for the harness's R1 comparison.</summary>
        public float Length()
        {
            return Length(Points);
        }

        // ===================================================================================
        // The file
        // ===================================================================================

        /// <summary>
        /// The route as section 7.1's document: `{ name, map: {name, assetName, width, height},
        /// recordedFrom, points: [ {x, y, ms} ] }`.
        ///
        /// THE WRITER ESCAPES, AND THE READER IS WHAT MAKES THAT WORTH DOING. `RecordedFrom` and the
        /// map names are free text a human typed or the client sent; `TraceRecorder`'s hand-rolled
        /// writer already turned one unescaped newline into a file every reader called CORRUPT, and
        /// that was a settings string rather than an operator's note.
        /// </summary>
        public string ToJson()
        {
            var text = new StringBuilder(1024 + (Points.Count * 32));
            text.Append("{\"v\":").Append(FormatVersion)
                .Append(",\"name\":").Append(Json(Name))
                .Append(",\"map\":{\"name\":").Append(Json(MapName))
                .Append(",\"assetName\":").Append(Json(MapAsset))
                .Append(",\"width\":").Append(MapWidth.ToString(CultureInfo.InvariantCulture))
                .Append(",\"height\":").Append(MapHeight.ToString(CultureInfo.InvariantCulture))
                .Append('}')
                .Append(",\"recordedFrom\":").Append(Json(RecordedFrom))
                .Append(",\"samples\":").Append(RawSamples.ToString(CultureInfo.InvariantCulture))
                .Append(",\"rawLength\":").Append(Num(RawLength))
                .Append(",\"points\":[");
            for (int i = 0; i < Points.Count; i++)
            {
                if (i > 0) { text.Append(','); }
                text.Append("{\"x\":").Append(Num(Points[i].X))
                    .Append(",\"y\":").Append(Num(Points[i].Y))
                    .Append(",\"ms\":").Append(Points[i].Ms.ToString(CultureInfo.InvariantCulture))
                    .Append('}');
            }
            text.Append("]}");
            return text.ToString();
        }

        private static string Num(float value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static string Json(string value)
        {
            if (value == null)
            {
                return "null";
            }
            var text = new StringBuilder(value.Length + 2);
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
                        if (c < ' ') { text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); }
                        else { text.Append(c); }
                        break;
                }
            }
            text.Append('"');
            return text.ToString();
        }

        /// <summary>
        /// Reads a route file. False with a NAMED reason, which is the house rule and here it is also
        /// the operator's only feedback: a route that will not load is a session that says why.
        ///
        /// THE PARSER IS STRICT AND SMALL, AND IT IS DELIBERATELY NOT A GENERAL JSON READER. This file
        /// has one schema and this project writes it; a general parser is a second thing to be wrong,
        /// and the shape it would accept is wider than the shape that exists. Numbers, strings with
        /// the escapes the writer emits, and the three containers the schema uses.
        /// </summary>
        public static bool TryParse(string text, out Route route, out string why)
        {
            route = null;
            why = null;
            if (string.IsNullOrEmpty(text))
            {
                why = "the file is empty";
                return false;
            }
            try
            {
                var reader = new RouteJson(text);
                reader.Expect('{');
                var result = new Route();
                while (true)
                {
                    string key = reader.String();
                    reader.Expect(':');
                    switch (key)
                    {
                        case "v":
                            int version = (int)reader.Number();
                            if (version != FormatVersion)
                            {
                                why = "route format v" + version + " but this build writes v"
                                      + FormatVersion;
                                return false;
                            }
                            break;
                        case "name": result.Name = reader.String(); break;
                        case "recordedFrom": result.RecordedFrom = reader.String(); break;
                        case "samples": result.RawSamples = (int)reader.Number(); break;
                        case "rawLength": result.RawLength = (float)reader.Number(); break;
                        case "map": ReadMap(reader, result); break;
                        case "points": ReadPoints(reader, result); break;
                        default:
                            reader.SkipValue();
                            break;
                    }
                    char next = reader.Peek();
                    if (next == ',') { reader.Take(); continue; }
                    reader.Expect('}');
                    break;
                }
                if (!result.Usable)
                {
                    why = "the route carries " + result.Points.Count + " point(s); two are needed"
                          + " (a recording shorter than two samples is not a walk)";
                    return false;
                }
                route = result;
                return true;
            }
            catch (Exception ex)
            {
                why = ex.Message;
                return false;
            }
        }

        private static void ReadMap(RouteJson reader, Route route)
        {
            reader.Expect('{');
            while (true)
            {
                string key = reader.String();
                reader.Expect(':');
                switch (key)
                {
                    case "name": route.MapName = reader.String(); break;
                    case "assetName": route.MapAsset = reader.String(); break;
                    case "width": route.MapWidth = (int)reader.Number(); break;
                    case "height": route.MapHeight = (int)reader.Number(); break;
                    default: reader.SkipValue(); break;
                }
                char next = reader.Peek();
                if (next == ',') { reader.Take(); continue; }
                reader.Expect('}');
                return;
            }
        }

        private static void ReadPoints(RouteJson reader, Route route)
        {
            reader.Expect('[');
            if (reader.Peek() == ']')
            {
                reader.Take();
                return;
            }
            while (true)
            {
                reader.Expect('{');
                var point = new RoutePoint();
                while (true)
                {
                    string key = reader.String();
                    reader.Expect(':');
                    switch (key)
                    {
                        case "x": point.X = (float)reader.Number(); break;
                        case "y": point.Y = (float)reader.Number(); break;
                        case "ms": point.Ms = (int)reader.Number(); break;
                        default: reader.SkipValue(); break;
                    }
                    char inner = reader.Peek();
                    if (inner == ',') { reader.Take(); continue; }
                    reader.Expect('}');
                    break;
                }
                route.Points.Add(point);
                char next = reader.Peek();
                if (next == ',') { reader.Take(); continue; }
                reader.Expect(']');
                return;
            }
        }

        /// <summary>
        /// A minimal reader for exactly the document <see cref="ToJson"/> writes. See `TryParse` for
        /// why it is not a general one.
        /// </summary>
        private sealed class RouteJson
        {
            private readonly string _text;
            private int _i;

            public RouteJson(string text)
            {
                _text = text;
            }

            public char Peek()
            {
                SkipWhitespace();
                return _i < _text.Length ? _text[_i] : '\0';
            }

            public void Take()
            {
                SkipWhitespace();
                if (_i < _text.Length) { _i++; }
            }

            public void Expect(char c)
            {
                SkipWhitespace();
                if (_i >= _text.Length || _text[_i] != c)
                {
                    throw new FormatException("expected '" + c + "' at offset " + _i);
                }
                _i++;
            }

            public string String()
            {
                SkipWhitespace();
                if (_i < _text.Length && _text[_i] == 'n'
                    && _i + 4 <= _text.Length && _text.Substring(_i, 4) == "null")
                {
                    _i += 4;
                    return null;
                }
                Expect('"');
                var text = new StringBuilder();
                while (_i < _text.Length)
                {
                    char c = _text[_i++];
                    if (c == '"')
                    {
                        return text.ToString();
                    }
                    if (c != '\\')
                    {
                        text.Append(c);
                        continue;
                    }
                    if (_i >= _text.Length) { break; }
                    char escape = _text[_i++];
                    switch (escape)
                    {
                        case '"': text.Append('"'); break;
                        case '\\': text.Append('\\'); break;
                        case '/': text.Append('/'); break;
                        case 'n': text.Append('\n'); break;
                        case 'r': text.Append('\r'); break;
                        case 't': text.Append('\t'); break;
                        case 'b': text.Append('\b'); break;
                        case 'f': text.Append('\f'); break;
                        case 'u':
                            if (_i + 4 > _text.Length) { throw new FormatException("short \\u escape"); }
                            text.Append((char)int.Parse(_text.Substring(_i, 4),
                                                        NumberStyles.HexNumber,
                                                        CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw new FormatException("bad escape '\\" + escape + "'");
                    }
                }
                throw new FormatException("unterminated string");
            }

            public double Number()
            {
                SkipWhitespace();
                int start = _i;
                while (_i < _text.Length && (_text[_i] == '-' || _text[_i] == '+' || _text[_i] == '.'
                                             || _text[_i] == 'e' || _text[_i] == 'E'
                                             || (_text[_i] >= '0' && _text[_i] <= '9')))
                {
                    _i++;
                }
                if (_i == start)
                {
                    throw new FormatException("expected a number at offset " + start);
                }
                return double.Parse(_text.Substring(start, _i - start),
                                    NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            /// <summary>Steps over one value of any shape, so an unknown key does not fail the load.</summary>
            public void SkipValue()
            {
                SkipWhitespace();
                if (_i >= _text.Length) { return; }
                char c = _text[_i];
                if (c == '"') { String(); return; }
                if (c == '{')
                {
                    int depth = 0;
                    do
                    {
                        if (_text[_i] == '"') { String(); continue; }
                        if (_text[_i] == '{' || _text[_i] == '[') { depth++; }
                        if (_text[_i] == '}' || _text[_i] == ']') { depth--; }
                        _i++;
                    }
                    while (_i < _text.Length && depth > 0);
                    return;
                }
                if (c == '[')
                {
                    int depth = 0;
                    do
                    {
                        if (_text[_i] == '"') { String(); continue; }
                        if (_text[_i] == '{' || _text[_i] == '[') { depth++; }
                        if (_text[_i] == '}' || _text[_i] == ']') { depth--; }
                        _i++;
                    }
                    while (_i < _text.Length && depth > 0);
                    return;
                }
                while (_i < _text.Length && _text[_i] != ',' && _text[_i] != '}'
                       && _text[_i] != ']')
                {
                    _i++;
                }
            }

            private void SkipWhitespace()
            {
                while (_i < _text.Length && (_text[_i] == ' ' || _text[_i] == '\t'
                                             || _text[_i] == '\n' || _text[_i] == '\r'))
                {
                    _i++;
                }
            }
        }
    }
}
