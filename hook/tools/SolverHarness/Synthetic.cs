// Synthetic.cs -- traces that can be asserted against when no live run exists.
//
// WHY THIS IS NOT "JUST A UNIT TEST FIXTURE".
//
// The invariants in DODGE-ALGORITHM.md section 10 are statements about *decisions over time*:
// "stand is never rejected while durably safe", "an admissible candidate is never inside a
// bullet's disc at arrival", "the chosen heading never changes while it stays admissible".
// None of them can be checked on a single call. They need a sequence of frames whose geometry
// the author knows exactly, so that a failure says which term is wrong rather than that
// something moved.
//
// A synthetic trace is also the only way to give the harness teeth TODAY. The live trace that
// the recorder writes is the ground truth for the trajectory model (invariant T7), and that one
// genuinely needs a played session. The other three do not: they are properties of the solver's
// own geometry, and a bullet that flies straight at a stationary player is a strictly harder
// case than anything the model has to get right in a live run.
//
// THE SCENARIOS BELOW ARE CHOSEN TO BE FALSIFIABLE, NOT TO BE REALISTIC. Each one has a known
// correct answer, so a solver that regresses on it fails loudly:
//
//   peaceful       nothing on a collision course. The solver must do nothing, every frame.
//   headon         one bullet straight at a stationary player. The solver must move, and the
//                  move must be perpendicular rather than along the bullet's line.
//   volley         four bullets from four directions with a gap in one of them. Anything that
//                  only reacts to the nearest bullet fails here.
//   corridor       a walled lane with one bullet coming down it. The dodge may not solve this
//                  by walking through a wall, and with no wall-aware admission it would.
//   passing-by     bullets that cross the player's line but never come within a disc. The
//                  solver must not flinch -- false positives are the failure the operator sees.
//   body           one enemy body on the goal ray, five units out. The path must route AROUND
//                  it rather than through it, and no planned node may sit in its emitter disc.
//   body-close     one enemy body within a frame's step of the player. Rule 7 must refuse the
//                  straight-ahead candidate, and the emitted move must not sweep the body.
//   body-fence     eight enemy bodies ringing the player at 0.55 units. The emitter discs cover
//                  every candidate, so this is the scenario that proves the pad cannot refuse.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Drelay.Bootstrap;

namespace Drelay.SolverHarness
{
    /// <summary>The scenarios this generator knows, and what each one is for.</summary>
    public enum Scenario
    {
        Peaceful,
        HeadOn,
        Volley,
        Corridor,
        PassingBy,

        /// <summary>
        /// The one scenario whose assertion is that the solver MOVES: a bullet on a line that comes
        /// close enough to matter and far enough that a single frame's step clears it.
        ///
        /// THE WINDOW IS NARROW AND THAT IS THE POINT OF HAVING IT. A player at 8.2 units/s covers
        /// 0.137 units in a frame, so a bullet whose closest approach is `m` is escapable this frame
        /// only when `m + 0.137` exceeds the 0.45 collision radius -- `m` above about 0.31. Below
        /// that no candidate can be admitted and the solver reports `NoAdmissible`, which is the
        /// honest answer and not a dodge. `Dodge` sits at 0.34, inside the window by a hair, so it
        /// fails loudly if any of the admission or durability rules becomes one notch too strict.
        /// </summary>
        Dodge,

        /// <summary>
        /// ONE ENEMY BODY ON THE GOAL RAY, FAR ENOUGH OUT THAT THE PATH CAN GO ROUND IT.
        ///
        /// This is the scenario `PLAN-ENEMY-THREATS.md`'s E3 asks for: with the emitter on and
        /// `WeightEnemyDanger` (or the path's own `StepCostWeight`) non-zero, the route to the goal
        /// must DETOUR -- `pathlen` larger than the straight line, `pathcost` larger than `pathlen`,
        /// and above all `PathStats.BodyZoneNodes` at zero, which is the direct claim rather than the
        /// two inferences. See `Harness.CheckBodyRouting`.
        ///
        /// THE BODY SITS AT FIVE UNITS AND NOT AT ONE. A body within the emitter's radius of the
        /// player's own cell makes EVERY path start inside a body zone, so the counter could never be
        /// zero and the scenario would assert nothing but its own geometry. Five units leaves the
        /// start clear, leaves the detour affordable, and still puts the body squarely on the ray.
        /// </summary>
        Body,

        /// <summary>
        /// One enemy body within a frame's step of the player, directly to the east.
        ///
        /// This is E2's scenario: the body's collision radius (`0.25 + 0.25 = 0.5`) is inside the
        /// player's eastward frame budget (`0.137`), so the straight-ahead candidates are REFUSED by
        /// rule 7 -- and the body is NOT a trap, because only the headings aimed at it are refused.
        /// </summary>
        BodyClose,

        /// <summary>
        /// THE PLAYER RINGED BY EIGHT ENEMY BODIES AT 0.55 UNITS -- E5's scenario, and its geometry
        /// is the whole test.
        ///
        /// 0.55 IS CHOSEN AGAINST THE RULE, NOT THE PICTURE. Rule 7 refuses a swept path only when
        /// the sweep enters `body.Radius + player.Radius` (0.5), and one frame's step is `budget`
        /// (0.137), so a body at 0.55 is inside every candidate's reach and refused by it -- while a
        /// body at 1.0 would be refused by nothing at all and the ring would test nothing. That is
        /// what makes this the scenario where "the emitter is a preference and the body is the only
        /// thing that refuses" has content: EIGHT emitter discs of radius 1.25 cover every candidate
        /// on every frame, so if the pad were an admission rule nothing would ever be admissible --
        /// and the recorded failure the plan cites is exactly that, `NoAdmissible` on 1733 of 6267
        /// frames (27.7 %).
        /// </summary>
        BodyFence,

        /// <summary>
        /// `PLAN-AUTOFARM.md` SECTION 4.4's W2: player at A, enemy at B, a wall between.
        ///
        /// The enemy is placed six units north of the player and a solid wall is laid across the
        /// straight line between them, with a gap at the eastern end so a route exists. The check is
        /// the plan's: the path length exceeds the straight line and `WallCrossings == 0` -- a farmer
        /// that walks into scenery because it was told to walk at a mob is the defect this fixture
        /// exists for.
        ///
        /// THE PLAYER DOES NOT WALK UNDER THEIR OWN INPUT HERE. `PlayerWalking` is false for every
        /// `farm*` scenario, because the farmer is the thing being tested and a player walking a
        /// steady circle would move the goal every frame for reasons that have nothing to do with it.
        /// </summary>
        Farmer,

        /// <summary>
        /// W3: the enemy six units out, with the shipped three-unit `FarmStopRadius` -- so the
        /// character has distance to close before it may stand.
        ///
        /// THE STOP RADIUS IS THE FIXTURE'S PARAMETER, NOT A CONSTANT, and W3 is a statement about
        /// closing AND settling. With the shipped three units the character closes three and stands;
        /// at `stop=0` the plan's literal arrival rule ("keep closing until rule 7 refuses") is in
        /// force, and what that does is the `farmerorbit` measurement rather than this one.
        /// </summary>
        FarmerClose,

        /// <summary>
        /// THE PLAN'S ARRIVAL CLAIM, MEASURED. `PLAN-AUTOFARM.md` section 4.1 item 5: "the character
        /// walks up to the mob and stops at its edge, because the only admissible candidates are
        /// outside the disc".
        ///
        /// This scenario is `FarmerClose`'s geometry with `stop=0` so the claim is exercised rather
        /// than assumed, and the harness reports the number of distinct headings the character
        /// commands over the run. A character that stops has few; a character that ORBITS has many.
        /// See `FarmStopRadius` for why the shipped default is not zero.
        /// </summary>
        FarmerOrbit,

        /// <summary>
        /// W1's stickiness fixture: three enemies in a line, the nearest drifting so that it becomes
        /// the middle one and then the far one.
        ///
        /// THE POINT IS THAT THE FARMER MUST NOT THRASH. Two enemies whose distances cross within
        /// `FarmSwitchMargin` must produce ONE hold and no switch, and a third that arrives a clear
        /// margin closer must produce a switch and not a forced release. Those are three different
        /// counters (`Switches`, `ForcedReleases`, `FramesHeld`) and this is the fixture that tells
        /// them apart.
        /// </summary>
        FarmerPair,

        /// <summary>
        /// THE CRITTER FILTER: two enemies in range, the NEARER one named like the realm's ambient
        /// creatures. The farmer must ignore the near one and walk at the far one.
        ///
        /// THE OPERATOR'S FIRST LIVE SESSION IS THE FIXTURE'S REASON: *"it paths to critters, they
        /// die by walking over them but should be ignored"*. No geometric rule separates a critter
        /// from a mob, so the filter is on the client's own `Data.Name`/`.Class`
        /// (`DodgeConfig.FarmIgnore`), and this is the only place it can be checked without the game.
        /// </summary>
        FarmerIgnore,

        /// <summary>
        /// THE TRAVEL FALLBACK: an EMPTY world. No enemies, no portals, no route -- the state the
        /// operator reported as "when there's no enemies on screen ... it has no where to pathfind".
        /// The farmer must publish roam waypoints and keep the character moving, with `stand`
        /// suppressed exactly as a route suppresses it.
        ///
        /// IT IS ALSO THE SPAWN-HAVEN FIXTURE IN MINIATURE: "when entering the world it's stuck
        /// inside a small safe haven, it has to try and breach out of there" is an empty world with
        /// walls around the character, and the waypoint rotation is what turns "stuck" into "walking
        /// until a target appears".
        /// </summary>
        FarmerTravel,

        /// <summary>
        /// W5: the farmer and a live bullet on the same frame. See `PlanSpawns`'s case for the
        /// geometry -- it is `Scenario.Dodge`'s bullet with a farm target added, so the dodge has
        /// real work to do while the farmer is asking to walk north.
        /// </summary>
        FarmerDodge,
    }

    /// <summary>
    /// Builds a synthetic trace. Deterministic for a given seed, so a failure can be re-run and a
    /// weight can be re-tuned against exactly the same frames.
    /// </summary>
    public sealed class SyntheticTrace
    {
        public Scenario Kind;
        public int Seed = 1;
        public int Frames = 600;
        public float Dt = 1.0f / 60.0f;
        public float PlayerSpeed = 8.2f;       // world units/s, the resolved law at a middling SPD
        public float PlayerRadius = 0.25f;
        public float BulletRadius = 0.2f;
        public float BulletSpeed = 24.0f;      // world units/s: fast enough that a dodge matters

        /// <summary>
        /// An enemy body's own radius in the body scenarios. The game's own value for a mob is
        /// roughly this; what matters for the tests is the SUM with `PlayerRadius`, because that is
        /// the collision radius rule 7 tests and the geometry of `BodyClose` is chosen against it.
        /// </summary>
        public float BodyRadius = 0.5f;

        /// <summary>
        /// The body's own radius in the CLOSE scenario, where the test is the sum against one
        /// frame's step rather than against the path's width.
        /// </summary>
        public float CloseBodyRadius = 0.25f;

        /// <summary>The ring's distance in the fence scenario. See `Scenario.BodyFence`.</summary>
        public float FenceDistance = 0.55f;

        /// <summary>
        /// A FARM MOB'S OWN RADIUS. 0.25, the game's own default for a character body and the same
        /// value `CloseBodyRadius` uses -- so a farm mob's collision disc is `0.25 + 0.25 = 0.5`, the
        /// number rule 7 refuses against and the number the stop radius has to clear.
        /// </summary>
        public float FarmBodyRadius = 0.25f;

        public float StartX = 100.0f;
        public float StartY = 100.0f;
        public int GridSize = 64;
        public int GridOrigin = 68;            // 100 - 32, so the player starts mid-window

        /// <summary>
        /// Whether the player is supposed to be walking under their own input.
        ///
        /// THE SCENARIOS THAT EXIST TO MAKE THE SOLVER MOVE HAVE THE PLAYER STILL, AND THAT IS NOT
        /// A CONVENIENCE. A recorded trajectory is anchored on the bullet's live position at the
        /// moment it was sampled, so it describes where the bullet will be if the world -- the
        /// *player* included -- does not change. A synthetic trace whose player walks while the
        /// scripted bullets fly at the position it started from is not a trace of anything; the
        /// bullets and the player are moving in different coordinate systems, and the naive reading
        /// ("no threat") is correct.
        ///
        /// The walking scenarios are the ones whose point is that the solver should NOT move, and
        /// for those the walk is harmless because the bullets never come close anyway.
        /// </summary>
        public bool PlayerWalking = true;

        /// <summary>Walls, as a predicate over grid cells in the window's own indices.</summary>
        private readonly List<int[]> _walls = new List<int[]>();

        private readonly Random _random;

        public SyntheticTrace(Scenario kind, int seed)
        {
            Kind = kind;
            Seed = seed;
            _random = new Random(seed);
        }

        /// <summary>Fills a trace object with the scenario's frames.</summary>
        public DodgeTrace Build()
        {
            var trace = new DodgeTrace();
            trace.Source = "synthetic:" + Kind + ":" + Seed;
            trace.Synthetic = true;
            trace.DodgeConfig = "synthetic";
            trace.GridSource = "synthetic";

            var grid = new GridWindow();
            grid.Generation = 1;
            grid.OriginX = GridOrigin;
            grid.OriginY = GridOrigin;
            grid.Width = GridSize;
            grid.Height = GridSize;
            grid.Cells = new byte[GridSize * GridSize];
            trace.Grids.Add(grid);

            PlanWalls();
            foreach (int[] wall in _walls)
            {
                int cx = wall[0] - grid.OriginX;
                int cy = wall[1] - grid.OriginY;
                if (cx >= 0 && cy >= 0 && cx < grid.Width && cy < grid.Height)
                {
                    grid.Cells[(cy * grid.Width) + cx] = 1;
                }
            }

            // The bullets, as spawn descriptions. Each is a straight line at a constant speed,
            // sampled on the same 30 ms grid the hook samples on, so the solver sees exactly the
            // shape it sees live.
            List<Spawn> spawns = PlanSpawns();
            List<EntityBody> bodies = PlanBodies();

            float x = StartX;
            float y = StartY;
            float heading = 0.0f;
            float clock = 0.0f;
            for (int f = 0; f < Frames; f++)
            {
                var frame = new RecordedFrame();
                frame.Frame = f;
                frame.Dt = Dt;
                frame.ClockMs = clock;
                frame.PlayerX = x;
                frame.PlayerY = y;
                frame.PlayerRadius = PlayerRadius;
                frame.Health = 100.0f;
                // THE FARMER'S FIXTURES RUN WITH THE OPERATOR'S HANDS OFF. `Moving` is the client's
                // own `GameInput.Moving`, and a farm session is one where the farmer is walking the
                // character rather than the operator -- which is also what makes the arrival policy
                // (`PLAN-AUTOFARM.md` section 4.3) the thing under test instead of the halt fix.
                frame.Moving = !IsFarm(Kind);
                if (IsFarm(Kind))
                {
                    // A FIXED SPEED, BECAUSE THE CLOSED LOOP MAKES THE MEASUREMENT A FEEDBACK PATH.
                    // The harness normally measures the speed from the displacement between frames,
                    // and on a closed-loop scenario that displacement is the solver's own last
                    // command -- so the measured speed would be the thing under test rather than an
                    // input to it. 8.2 u/s is the resolved law at a middling SPD, the same constant
                    // `ResolveSpeed` falls back to.
                    frame.SpeedOverride = PlayerSpeed;
                }
                frame.Intent = heading;
                frame.Rotation = 0.0f;
                frame.Grid = grid;

                // THE FARMER'S TARGETS, WHICH ARE NOT THE BODIES. See `RecordedFrame.FarmTargets`.
                // A target may move -- `farmerpair` needs one that does, or the stickiness rule's
                // switch could never fire -- so the list is planned per frame.
                foreach (FarmTarget target in PlanFarmTargets(f))
                {
                    frame.FarmTargets.Add(target);
                }

                // THE ENEMY BODIES, AT THEIR OWN POSITIONS FOR THIS FRAME. `DangerRadius` is left at
                // zero here on purpose: it is `radius + playerRadius + pad`, the pad is a CONFIG
                // value, and the harness's `BuildSnapshot` computes it from the config the run was
                // given -- which is what makes `enemypad=N` an A/B of the emitter alone.
                //
                // THE FARMER'S FIXTURES PLAN THEM PER FRAME rather than once per run, because
                // `farmerpair`'s second mob walks. See `PlanFarmBodies`.
                List<EntityBody> frameBodies = IsFarm(Kind) ? PlanFarmBodies(f) : bodies;
                foreach (EntityBody body in frameBodies)
                {
                    frame.Bodies.Add(new EntityBody
                    {
                        X = body.X,
                        Y = body.Y,
                        Radius = body.Radius,
                        DangerRadius = 0.0f,
                    });
                }

                foreach (Spawn spawn in spawns)
                {
                    float spawnTime = spawn.StartFrame * Dt;
                    float now = f * Dt;
                    if (now < spawnTime)
                    {
                        continue;
                    }
                    float ageMs = (now - spawnTime) * 1000.0f;
                    if (ageMs > spawn.LifetimeMs)
                    {
                        continue;
                    }

                    var threat = new RecordedThreat();
                    threat.GameId = spawn.GameId;
                    threat.StartTime = (int)(spawnTime * 1000.0f);
                    threat.Radius = BulletRadius;
                    threat.Damage = spawn.Damage;
                    threat.Projectable = true;
                    threat.Count = Trajectory.MaxSamples;
                    threat.Xs = new float[threat.Count];
                    threat.Ys = new float[threat.Count];
                    threat.Alive = new bool[threat.Count];

                    for (int s = 0; s < threat.Count; s++)
                    {
                        float ahead = ageMs + (s * 30.0f);
                        threat.Xs[s] = spawn.X + (spawn.Vx * ahead / 1000.0f);
                        threat.Ys[s] = spawn.Y + (spawn.Vy * ahead / 1000.0f);
                        threat.Alive[s] = ahead <= spawn.LifetimeMs;
                    }
                    threat.X = threat.Xs[0];
                    threat.Y = threat.Ys[0];
                    frame.Threats.Add(threat);
                }

                // The player walks along the intent heading, which for every scenario here is a
                // steady circle except `corridor`, where it is a straight line. See `PlayerWalking`
                // for why the scenarios that need the solver to react keep the player still.
                if (!PlayerWalking)
                {
                    heading = 0.0f;
                }
                else if (Kind == Scenario.Corridor)
                {
                    heading = (float)(Math.PI / 2.0);   // due +y, straight down the lane
                }
                else
                {
                    heading += 0.004f;
                }
                if (PlayerWalking)
                {
                    x += (float)Math.Cos(heading) * PlayerSpeed * Dt;
                    y += (float)Math.Sin(heading) * PlayerSpeed * Dt;
                }

                trace.Frames.Add(frame);
                clock += Dt * 1000.0f;
            }

            return trace;
        }

        /// <summary>
        /// Writes the trace out in the recorder's own format, so that the reader, the writer and
        /// the solver are all exercised on the same bytes. A synthetic trace that is never
        /// serialised would leave the reader untested until the operator's first live run.
        /// </summary>
        public void WriteJsonLines(DodgeTrace trace, string path)
        {
            using (var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write),
                                                 new UTF8Encoding(false)))
            {
                var line = new StringBuilder(8192);
                line.Append("{\"t\":\"H\",\"v\":1,\"dodge\":\"synthetic\",\"player\":\"Player\",")
                    .Append("\"projectile\":\"Projectile\",\"samples\":21,\"gridSource\":\"synthetic\",")
                    .Append("\"synthetic\":true}");
                writer.WriteLine(line.ToString());

                foreach (GridWindow g in trace.Grids)
                {
                    line.Length = 0;
                    line.Append("{\"t\":\"G\",\"g\":").Append(g.Generation)
                        .Append(",\"ox\":").Append(g.OriginX)
                        .Append(",\"oy\":").Append(g.OriginY)
                        .Append(",\"w\":").Append(g.Width)
                        .Append(",\"h\":").Append(g.Height)
                        .Append(",\"rle\":[");
                    int i = 0;
                    bool first = true;
                    while (i < g.Cells.Length)
                    {
                        byte value = g.Cells[i];
                        int run = 1;
                        while (i + run < g.Cells.Length && g.Cells[i + run] == value)
                        {
                            run++;
                        }
                        if (!first)
                        {
                            line.Append(',');
                        }
                        first = false;
                        line.Append(value).Append(',').Append(run);
                        i += run;
                    }
                    line.Append("]}");
                    writer.WriteLine(line.ToString());
                }

                foreach (RecordedFrame f in trace.Frames)
                {
                    line.Length = 0;
                    line.Append("{\"t\":\"F\",\"f\":").Append(f.Frame)
                        .Append(",\"dt\":").Append(Num(f.Dt))
                        .Append(",\"clock\":").Append(Num(f.ClockMs))
                        .Append(",\"px\":").Append(Num(f.PlayerX))
                        .Append(",\"py\":").Append(Num(f.PlayerY))
                        .Append(",\"r\":").Append(Num(f.PlayerRadius))
                        .Append(",\"hp\":").Append(Num(f.Health))
                        .Append(",\"moving\":").Append(f.Moving ? "true" : "false")
                        .Append(",\"intent\":").Append(Num(f.Intent))
                        .Append(",\"dead\":false,\"safe\":false,\"rot\":0")
                        .Append(",\"grid\":").Append(f.Grid != null ? f.Grid.Generation : 0)
                        .Append(",\"threats\":[");
                    for (int t = 0; t < f.Threats.Count; t++)
                    {
                        if (t > 0)
                        {
                            line.Append(',');
                        }
                        RecordedThreat th = f.Threats[t];
                        line.Append("{\"id\":").Append(th.GameId)
                            .Append(",\"st\":").Append(th.StartTime)
                            .Append(",\"x\":").Append(Num(th.X))
                            .Append(",\"y\":").Append(Num(th.Y))
                            .Append(",\"rad\":").Append(Num(th.Radius))
                            .Append(",\"lin\":1")
                            .Append(",\"dmg\":").Append(Num(th.Damage))
                            .Append(",\"dmgp\":0,\"mdmg\":0")
                            .Append(",\"n\":").Append(th.Count)
                            .Append(",\"xs\":[");
                        for (int s = 0; s < th.Count; s++)
                        {
                            if (s > 0) { line.Append(','); }
                            line.Append(Num(th.Xs[s]));
                        }
                        line.Append("],\"ys\":[");
                        for (int s = 0; s < th.Count; s++)
                        {
                            if (s > 0) { line.Append(','); }
                            line.Append(Num(th.Ys[s]));
                        }
                        line.Append("],\"alive\":[");
                        for (int s = 0; s < th.Count; s++)
                        {
                            if (s > 0) { line.Append(','); }
                            line.Append(th.Alive[s] ? '1' : '0');
                        }
                        line.Append("]}");
                    }
                    line.Append("],\"bodies\":[");
                    for (int b = 0; b < f.Bodies.Count; b++)
                    {
                        if (b > 0)
                        {
                            line.Append(',');
                        }
                        EntityBody body = f.Bodies[b];
                        line.Append("{\"x\":").Append(Num(body.X))
                            .Append(",\"y\":").Append(Num(body.Y))
                            .Append(",\"rad\":").Append(Num(body.Radius))
                            .Append('}');
                    }
                    // THE FARMER'S TARGETS GO IN THE FILE TOO, AND THE REASON IS THE ONE
                    // `PLAN-ENEMY-THREATS.md` SECTION 8.8.7 RECORDS AS A COST: the recorder never wrote
                    // a `bodies` key, so the live session that would have settled the emitter's weight
                    // could not be replayed. This writer is the one the hook's own recorder is modelled
                    // on, and writing the key here is what makes `TraceRecorder` growing one a
                    // compatible change rather than a format break. `TraceFile` reads it back.
                    line.Append("],\"farm\":[");
                    for (int b = 0; b < f.FarmTargets.Count; b++)
                    {
                        if (b > 0)
                        {
                            line.Append(',');
                        }
                        FarmTarget target = f.FarmTargets[b];
                        line.Append("{\"id\":").Append(target.GameId)
                            .Append(",\"x\":").Append(Num(target.X))
                            .Append(",\"y\":").Append(Num(target.Y))
                            .Append('}');
                    }
                    line.Append("],\"skipped\":0}");
                    writer.WriteLine(line.ToString());                }
            }
        }

        // --- Scenarios -------------------------------------------------------------------

        private struct Spawn
        {
            public int GameId;
            public int StartFrame;
            public float X;
            public float Y;
            public float Vx;
            public float Vy;
            public float LifetimeMs;
            public float Damage;
        }

        private void PlanWalls()
        {
            if (Kind == Scenario.Farmer)
            {
                // THE WALL W2 ASKS FOR: a solid line across the straight route to the enemy, with a
                // gap at its eastern end so a route exists. The enemy is six units north at
                // (StartX, StartY+6); the wall sits at y = StartY+3 from x = StartX-4 to StartX+2,
                // and the gap at StartX+3..StartX+4 is where the route goes. Without the gap the
                // target would be unreachable and the fixture would assert `Unreachable` rather than
                // a detour -- a different claim wearing the same name.
                int wallY = (int)StartY + 3;
                for (int x = (int)StartX - 4; x <= (int)StartX + 2; x++)
                {
                    _walls.Add(new[] { x, wallY });
                }
                return;
            }
            if (Kind != Scenario.Corridor)
            {
                return;
            }
            // Two solid rows either side of the start position, leaving a two-tile lane running
            // along +y. A dodge that ignores walkability will happily step into the wall.
            int laneX = (int)StartX;
            for (int dy = -8; dy <= 24; dy++)
            {
                for (int dx = -3; dx <= 3; dx++)
                {
                    if (dx == 0 || dx == 1)
                    {
                        continue;   // the lane itself
                    }
                    _walls.Add(new[] { laneX + dx, (int)StartY + dy });
                }
            }
        }

        /// <summary>
        /// The enemy bodies this scenario places, in world coordinates, stationary for the run.
        ///
        /// STATIONARY IS A SCENARIO CHOICE RATHER THAN A MODEL LIMIT. `DodgeSnapshot.Bodies` is
        /// refilled every frame from the live entity list, so a moving enemy is just a different list
        /// per frame; the property these three scenarios test -- that rule 7 refuses a body, that the
        /// path routes around one, that an emitter disc cannot fence the player in -- is about
        /// geometry and not about motion, and a statue is the smallest fixture that has it.
        /// </summary>
        private List<EntityBody> PlanBodies()
        {
            var bodies = new List<EntityBody>();
            switch (Kind)
            {
                case Scenario.Body:
                    // ON THE RAY, FIVE UNITS OUT. See `Scenario.Body` for why five and not one.
                    bodies.Add(Body(StartX + 6.0f, StartY, BodyRadius));
                    break;

                case Scenario.BodyClose:
                    // ONE FRAME'S STEP PLUS THE COLLISION RADIUS IS 0.637; 0.55 IS INSIDE THE
                    // REFUSAL BUT OUTSIDE "THE PLAYER IS ALREADY IN IT", which is the only window
                    // where rule 7's refusal and its exception can be told apart.
                    bodies.Add(Body(StartX + 0.55f, StartY, CloseBodyRadius));
                    break;

                case Scenario.BodyFence:
                    for (int k = 0; k < 8; k++)
                    {
                        double angle = k * Math.PI / 4.0;
                        bodies.Add(Body(StartX + ((float)Math.Cos(angle) * FenceDistance),
                                        StartY + ((float)Math.Sin(angle) * FenceDistance),
                                        CloseBodyRadius));
                    }
                    break;
            }
            return bodies;
        }

        private static EntityBody Body(float x, float y, float radius)
        {
            return new EntityBody { X = x, Y = y, Radius = radius, DangerRadius = 0.0f };
        }

        /// <summary>True for the five fixtures `PLAN-AUTOFARM.md` section 4 adds.</summary>
        public static bool IsFarm(Scenario kind)
        {
            return kind == Scenario.Farmer || kind == Scenario.FarmerClose
                   || kind == Scenario.FarmerOrbit || kind == Scenario.FarmerPair
                   || kind == Scenario.FarmerIgnore || kind == Scenario.FarmerTravel
                   || kind == Scenario.FarmerDodge;
        }

        /// <summary>
        /// The enemy the farmer walks at, at its own position for this frame.
        ///
        /// IT IS ALSO A BODY, AND THAT IS NOT A DUPLICATION. The live reader puts the same enemy in
        /// both lists (`EnemyReader.Fill`), and for the fixture it is load-bearing twice over: rule 7
        /// is what stops the character walking THROUGH the mob, and `AnyBodyAcrossTheRoute` is the
        /// gate that decides the frame is worth evaluating at all -- an enemy that is only a farm
        /// target would leave the dodge's own body machinery untested on the very fixtures that
        /// depend on it.
        /// </summary>
        private List<EntityBody> PlanFarmBodies(int frame)
        {
            var bodies = new List<EntityBody>();
            switch (Kind)
            {
                case Scenario.Farmer:
                    bodies.Add(Body(StartX, StartY + 6.0f, FarmBodyRadius));
                    break;
                case Scenario.FarmerClose:
                    bodies.Add(Body(StartX, StartY + 6.0f, FarmBodyRadius));
                    break;
                case Scenario.FarmerOrbit:
                    bodies.Add(Body(StartX, StartY + 3.0f, FarmBodyRadius));
                    break;
                case Scenario.FarmerPair:
                    // THE FARMER'S OWN STOP RADIUS IS WHAT MAKES A SWITCH POSSIBLE, AND SEEING WHY
                    // IS THE FIXTURE. The character closes on the nearest mob and STOPS within
                    // `FarmStopRadius` of it; nothing can then overtake a mob that is a unit and a
                    // half away, so with the shipped 3.0 the nearest would never change and the
                    // stickiness rule would never be asked a question. So the fixture sets a WIDE
                    // stop radius (it is on the closed-loop side, so the character halts at once)
                    // and lets the second mob walk in from the east until it is a clear margin
                    // closer. That is the crossing the rule exists for.
                    bodies.Add(Body(StartX, StartY + 5.0f, FarmBodyRadius));
                    bodies.Add(FarmPairSecond(frame));
                    break;
                case Scenario.FarmerIgnore:
                    // Two ground creatures to the east and one ordinary mob to the north. All three
                    // are bodies, because the live reader puts the same enemies in both lists.
                    bodies.Add(Body(StartX + 4.0f, StartY, FarmBodyRadius));
                    bodies.Add(Body(StartX + 5.0f, StartY + 1.0f, FarmBodyRadius));
                    bodies.Add(Body(StartX, StartY + 5.0f, FarmBodyRadius));
                    break;
                case Scenario.FarmerDodge:
                    bodies.Add(Body(StartX, StartY + 3.0f, FarmBodyRadius));
                    break;
            }
            return bodies;
        }

        /// <summary>
        /// The farmer's own target list, which carries the `GameId` the body list has no use for.
        ///
        /// PER FRAME RATHER THAN PER RUN, because `farmerpair` needs one of its mobs to MOVE: the
        /// stickiness rule switches on a change in relative distance, and with everything static the
        /// nearest is the nearest forever and the rule is never asked a question. A moving enemy is
        /// also simply what the live reader sees -- `World.GetEnemies()` is rebuilt every frame.
        /// </summary>
        private List<FarmTarget> PlanFarmTargets(int frame)
        {
            var targets = new List<FarmTarget>();
            switch (Kind)
            {
                case Scenario.Farmer:
                    targets.Add(Target(1, StartX, StartY + 6.0f));
                    break;
                case Scenario.FarmerClose:
                    targets.Add(Target(1, StartX, StartY + 6.0f));
                    break;
                case Scenario.FarmerOrbit:
                    targets.Add(Target(1, StartX, StartY + 3.0f));
                    break;
                case Scenario.FarmerPair:
                    // MOB 1 IS THE HELD TARGET AT FIVE UNITS DUE NORTH; MOB 2 WALKS IN ALONG THE X
                    // AXIS EIGHT UNITS EAST AND CROSSES THE MARGIN SOMEWHERE AROUND FRAME 135. See
                    // `PlanFarmBodies` for why the fixture needs a wide stop radius for this to be a
                    // question at all.
                    targets.Add(Target(1, StartX, StartY + 5.0f));
                    EntityBody second = FarmPairSecond(frame);
                    targets.Add(new FarmTarget { GameId = 2, X = second.X, Y = second.Y });
                    break;
                case Scenario.FarmerIgnore:
                    // THE CRITTER RULE IS THE FIXTURE, and the values are the client's own after the
                    // 2026-10-18 research: `GameObjectData.Class` is the .NET TYPE name of the data
                    // class, and `Enemy` has exactly one subclass -- `EnemyFlat`, the ground-layer
                    // creature you walk on. `NoDie` is the server-side flag the client parses and
                    // never reads. Two ignored kinds, both structural; the far one is an ordinary
                    // mob and must be the one chosen.
                    targets.Add(new FarmTarget
                    {
                        GameId = 1,
                        X = StartX + 4.0f,
                        Y = StartY,
                        Name = "Crab",
                        Class = "EnemyFlat",
                    });
                    targets.Add(new FarmTarget
                    {
                        GameId = 2,
                        X = StartX + 5.0f,
                        Y = StartY + 1.0f,
                        Name = "Duck",
                        Class = "Enemy",
                        NoDie = true,
                    });
                    targets.Add(new FarmTarget
                    {
                        GameId = 3,
                        X = StartX,
                        Y = StartY + 5.0f,
                        Name = "Slime",
                        Class = "Enemy",
                    });
                    break;
                case Scenario.FarmerDodge:
                    targets.Add(Target(1, StartX, StartY + 3.0f));
                    break;
            }
            return targets;
        }

        /// <summary>
        /// Mob 2 of `farmerpair`, as a function of the frame: it starts eight units due east of the
        /// player and walks west at 0.03 units per frame, so it crosses inside the switch margin
        /// somewhere in the first few hundred frames and stays in the acquisition radius throughout.
        ///
        /// IT IS A FUNCTION AND NOT A STATUE, BECAUSE THE RULE CANNOT BE EXERCISED BY A STATUE. See
        /// `PlanFarmTargets`.
        /// </summary>
        private EntityBody FarmPairSecond(int frame)
        {
            return Body(StartX + 8.0f - (0.03f * frame), StartY, FarmBodyRadius);
        }

        private static FarmTarget Target(int id, float x, float y)
        {
            return new FarmTarget { GameId = id, X = x, Y = y };
        }

        private List<Spawn> PlanSpawns()
        {            var spawns = new List<Spawn>();
            switch (Kind)
            {
                case Scenario.Peaceful:
                    // Two bullets crossing far away, so the trace is not simply empty.
                    spawns.Add(Far(1, 60, -12.0f, 0.0f, 0.0f, 0.0f));
                    spawns.Add(Far(2, 120, 0.0f, 12.0f, 0.0f, 0.0f));
                    break;

                case Scenario.HeadOn:
                    // A bullet that crosses the player's line 0.5 units to one side and reaches
                    // that point inside the horizon, so there is something to dodge and somewhere
                    // to dodge to.
                    //
                    // THE GEOMETRY OF "A BULLET THAT MATTERS" IS NARROWER THAN IT LOOKS, AND THE
                    // FIRST TWO VERSIONS OF THIS SCENARIO GOT IT WRONG. The decision only sees
                    // bullets whose predicted path enters the hit disc *inside the horizon* --
                    // 0.45 s of a 24 unit/s bullet is 10.8 units -- so a bullet aimed at where the
                    // player is standing, arriving in two seconds, is not a threat the model is
                    // looking for and the correct answer is to do nothing. A scenario that is
                    // meant to produce motion has to put its bullet in the window:
                    //
                    //     fired from (89, 100.5) at 24 units/s along +x
                    //     crosses x = 100 at t = 0.458, and by the horizon it is at x = 99.8:
                    //     closest approach 0.5 in y, against a 0.45 hit radius.
                    //
                    // 0.5 against 0.45 is deliberate: it is close enough to threat, and far
                    // enough that a 0.137-unit sidestep clears it. `stand` is refused (0.5 > 0.45,
                    // so standing is *not* safe forever, but it is safe inside the horizon -- the
                    // distinction the durability test is about).
                    spawns.Add(Offset(1, 30, StartX - 11.0f, StartY + 0.30f, 1.0f, 0.0f, 0f, 0f));
                    spawns.Add(Offset(2, 210, StartX - 11.0f, StartY + 0.30f, 1.0f, 0.0f, 0f, 0f));
                    break;

                case Scenario.Dodge:
                    spawns.Add(Offset(1, 30, StartX - 11.0f, StartY + 0.43f, 1.0f, 0.0f, 0f, 0f));
                    spawns.Add(Offset(2, 210, StartX - 11.0f, StartY + 0.43f, 1.0f, 0.0f, 0f, 0f));
                    break;

                case Scenario.Volley:
                    // Four bullets closing on the start position from four diagonals, each passing
                    // 0.5 units off it and each starting 10 units out -- outside the disc, so the
                    // player has an immediate choice to make. It is a dense test of the candidate
                    // set: the gap is real but narrow, and a solver that only reacts to the
                    // nearest bullet fails here.
                    for (int k = 0; k < 4; k++)
                    {
                        double angle = Math.PI / 4.0 + (k * Math.PI / 2.0);
                        float ox = (float)(-Math.Sin(angle) * 0.45);
                        float oy = (float)(Math.Cos(angle) * 0.45);
                        spawns.Add(Aimed(10 + k, 40, 10.0f, (float)angle,
                                         StartX + ox, StartY + oy));
                    }
                    break;

                case Scenario.Corridor:
                    // Down the lane, from ahead, passing 0.5 units to one side. The lane's walls are
                    // what make this a terrain test: a dodge that steps sideways into the wall is
                    // a dodge that has stopped.
                    spawns.Add(Aimed(1, 40, 10.0f, (float)(Math.PI / 2.0),
                                     StartX + 0.45f, StartY + 10.0f));
                    spawns.Add(Aimed(2, 240, 10.0f, (float)(Math.PI / 2.0),
                                     StartX + 0.45f, StartY + 10.0f));
                    break;

                case Scenario.PassingBy:
                    // Through the neighbourhood, never within a disc: perpendicular passes that
                    // miss by 1.5 units, well outside the 0.45 unit hit radius.
                    spawns.Add(Offset(1, 30, StartX - 14.0f, StartY + 1.5f, 1.0f, 0.0f, 0f, 0f));
                    spawns.Add(Offset(2, 150, StartX + 14.0f, StartY - 1.5f, -1.0f, 0.0f, 0f, 0f));
                    break;

                case Scenario.FarmerDodge:
                    // W5'S FIXTURE: THE `dodge` BULLET WITH A FARM TARGET ON THE SAME FRAME. The
                    // geometry is `Scenario.Dodge`'s exactly -- a bullet crossing 0.43 units from the
                    // start position, which is inside the window where one frame's step clears it --
                    // so the dodge has real work to do while the farmer is asking to walk north. That
                    // is the collision the fixture exists for, and T6 is the check that the dodge
                    // still wins it.
                    //
                    // THE BULLET'S PATH IS ANCHORED ON THE PLAYER'S START POSITION, and on a
                    // closed-loop scenario the player then walks. That is a real limitation and it is
                    // the right one: the fixture asks whether the two inputs coexist at all, and the
                    // first thirty frames are where the threat is. A bullet re-aimed at a moving
                    // player would be a re-simulation of the game, not a fixture.
                    spawns.Add(Offset(1, 30, StartX - 11.0f, StartY + 0.43f, 1.0f, 0.0f, 0f, 0f));
                    spawns.Add(Offset(2, 210, StartX - 11.0f, StartY + 0.43f, 1.0f, 0.0f, 0f, 0f));
                    break;
            }
            return spawns;
        }

        /// <summary>
        /// A bullet fired from `from` along `(dirX, dirY)`.
        ///
        /// THE DIRECTION IS ALWAYS PASSED EXPLICITLY. An earlier version inferred it from a
        /// "through" point and the inference silently produced a stationary direction for the
        /// scenarios that passed a zero vector -- a bullet that never arrives looks exactly like a
        /// bullet that was successfully dodged, which is the worst possible way for a fixture to be
        /// wrong.
        /// </summary>
        private Spawn Offset(int id, int startFrame, float fromX, float fromY,
                             float dirX, float dirY, float unusedX, float unusedY)
        {
            float vx = dirX;
            float vy = dirY;
            float len = (float)Math.Sqrt((vx * vx) + (vy * vy));
            if (len < 1e-6f)
            {
                throw new ArgumentException("a synthetic bullet needs a direction");
            }
            return new Spawn
            {
                GameId = id,
                StartFrame = startFrame,
                X = fromX,
                Y = fromY,
                Vx = (vx / len) * BulletSpeed,
                Vy = (vy / len) * BulletSpeed,
                LifetimeMs = 2000.0f,
                Damage = 30.0f,
            };
        }

        /// <summary>A bullet at `from` whose line passes through the player's start position.</summary>
        private Spawn Aimed(int id, int startFrame, float distance, float angle,
                            float passX, float passY)
        {
            float fromX = passX - ((float)Math.Cos(angle) * distance);
            float fromY = passY - ((float)Math.Sin(angle) * distance);
            return new Spawn
            {
                GameId = id,
                StartFrame = startFrame,
                X = fromX,
                Y = fromY,
                Vx = (float)Math.Cos(angle) * BulletSpeed,
                Vy = (float)Math.Sin(angle) * BulletSpeed,
                LifetimeMs = 1400.0f,
                Damage = 30.0f,
            };
        }

        private Spawn Far(int id, int startFrame, float vx, float vy, float ox, float oy)
        {
            return new Spawn
            {
                GameId = id,
                StartFrame = startFrame,
                X = StartX - 14.0f + ox,
                Y = StartY - 14.0f + oy,
                Vx = vx,
                Vy = vy,
                LifetimeMs = 1400.0f,
                Damage = 10.0f,
            };
        }

        private static string Num(float value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}
