// DodgeSnapshot.cs -- Spike 3.2: the plain-data contract between the sensor and the solver.
//
// WHY THIS FILE EXISTS AT ALL, AND IT IS NOT TIDINESS.
//
// DODGE-ALGORITHM.md section 2 makes one structural claim that the whole phase rests on:
//
//     "Steps 1-3 are observations. Step 4 is the only decision, and it is a PURE FUNCTION
//      of a plain-data snapshot -- no game pointers, no allocation."
//
// That property is what lets the dodge's quality be decided WITHOUT THE GAME, which is the
// only place it can be decided cheaply: IMPLEMENTATION-PLAN.md's Spike 3.2 tunes every weight
// "in the harness, never in a live run". A pure function of plain data is a function that can
// be run against a recorded trace on the floor of an office, once per second, instead of
// against a live character once per evening.
//
// It also imposes a discipline that is easy to state and easy to break: **nothing in this file
// may name a game type.** No `FieldInfo`, no `object`, no `DarzaCore.*`. Everything here is a
// float, an int, a bool or an array of them, so that `DodgeSolver` compiles into a console
// program with no game in sight. If a future change needs a game accessor to describe a
// snapshot field, the field does not belong in the snapshot.
//
// THE UNITS ARE THE GAME'S OWN, AND THAT IS DELIBERATE. Positions are world units (which
// `World` truncates to integers to index its collision grid -- `conv.i4` in
// `World::'hehe'(float, float)`, so the conversion is a C cast and not a floor). Times are
// milliseconds where the field is named `*Ms` and seconds where it is named `*Seconds`; the
// names carry the unit because this project has already paid for a unit confusion once.
//
// WHY THE SETTINGS ARE READ FROM THE ENVIRONMENT HERE RATHER THAN FROM A CONFIG OBJECT.
// The hook has no configuration file and no settings UI of its own; the established surface is
// a `DRELAY_*` variable (PHASE1-HANDOFF.md section 4 trap 1: plain `DRELAY_` names, because
// the launcher strips every inherited `DRELAY_HOOK_*` name). The offline harness has no
// environment contract at all -- it reads a command line -- so the knobs live in a plain
// mutable struct with defaults, and only the *hook* fills them from the environment. That
// keeps the defaults in one place, which is where the tuning happens.

using System;
using System.Globalization;

namespace Drelay.Bootstrap
{
    /// <summary>What the solver decided to do this frame.</summary>
    public enum DodgeAction
    {
        /// <summary>Nothing to do: no threat, immobile, or the player's own steering is already safe.</summary>
        None = 0,

        /// <summary>Steer to <see cref="DodgeDecision.HeadingRadians"/> at <see cref="DodgeDecision.SpeedScale"/>.</summary>
        Move = 1,

        /// <summary>Gate 6 of section 7: lethal and unavoidable. The caller calls `World.RequestEscape()`.</summary>
        Escape = 2,

        /// <summary>The solver refused to answer; the caller must do nothing and keep the escape floor armed.</summary>
        Degraded = 3,
    }

    /// <summary>Why the solver answered the way it did. A closed set, so a log can be counted.</summary>
    public enum DodgeReason
    {
        /// <summary>Not evaluated -- the switch is off or the snapshot was unusable.</summary>
        NotEvaluated = 0,

        /// <summary>No live threat intersects the horizon; the common case.</summary>
        NoThreat = 1,

        /// <summary>`Moving` is false: the game reads no input at all this frame.</summary>
        PlayerNotMoving = 2,

        /// <summary>The speed law gives zero (paralysed, or a mode that zeroes it). Section 11.</summary>
        Immobile = 3,

        /// <summary>No admissible candidate at all: section 5.3 refused every move.</summary>
        NoAdmissible = 4,

        /// <summary>Following the player's own intent; nothing better on the menu.</summary>
        IntentSafe = 5,

        /// <summary>Overriding intent: the intent heading is not durably safe.</summary>
        OverrideUnsafe = 6,

        /// <summary>Staying where we are; durably safe, so the best dodge is no dodge.</summary>
        StandSafe = 7,

        /// <summary>The hysteresis held the previous heading.</summary>
        Hysteresis = 8,

        /// <summary>Nothing is admissible; this is the least-bad move, chosen for time-to-impact.</summary>
        Desperation = 9,

        /// <summary>Section 7 gate 6: escape.</summary>
        EscapeLethalUnavoidable = 10,

        /// <summary>
        /// `stand` is durably safe AND the player is holding a direction AND at least one MOVE
        /// is admissible -- so the solver emits nothing and the player walks on.
        ///
        /// WHY THIS IS A REASON OF ITS OWN RATHER THAN `StandSafe`. This is the halt defect:
        /// `StandSafe` with `StandStops` on publishes `DodgeCommand.Stop`, which clears
        /// `GameInput.Moving`, so a frame it wins is a frame the player's hands are ignored.
        /// Measured on the three acting sessions, that happens on 18.0 %, 23.5 % and 31.4 % of
        /// the frames the player was holding a direction on -- and on 100 % of those frames an
        /// admissible move existed, so "nothing could be done" is not the explanation.
        ///
        /// A reader cannot tell the two apart from the outside, and the difference is the whole
        /// of the operator's verdict: `StandSafe` means "the dodge stopped a walking player",
        /// this means "the dodge left them alone". Sharing one reason would make the fix
        /// invisible in exactly the instrument built to see it -- HANDOFF-DIRECTED-DODGE.md
        /// section 8 trap 11.
        ///
        /// WHAT IT DOES NOT MEAN. It is not `NoAdmissible`: a move WAS admissible, and the
        /// solver is declining to overrule the player's own hands with it. It is also not the
        /// old halt with a new name -- nothing is published for it, so the pilot leaves
        /// `GameInput` exactly as the player left it.
        /// </summary>
        StandWalkOn = 11,
    }

    /// <summary>
    /// One live bullet's predicted path, in the client's own coordinates, anchored on the live
    /// position at delta zero (DODGE-ALGORITHM.md section 4.4 rule 1).
    ///
    /// THE FIXED ARRAYS ARE THE POINT AND NOT AN OPTIMISATION. This type is filled once per
    /// bullet per frame on the game's thread, and a `List&lt;float&gt;` per bullet per frame would
    /// allocate 40 times a frame forever. `Count` is the number of samples that were taken,
    /// which is `DodgeSnapshot.SampleCount`; a shorter path is one the bullet's own trajectory
    /// family declared finished, and it is frozen at its last sample rather than extrapolated
    /// (section 4.4's "freeze past the trace, never extrapolate").
    /// </summary>
    public sealed class Trajectory
    {
        /// <summary>The game's own cap on samples; `Candidates` and the harness rely on it being a constant.</summary>
        public const int MaxSamples = 21;

        /// <summary>
        /// The identity of the bullet this path belongs to, as packed `(GameId, startTime)`.
        ///
        /// IT IS NOT READ FROM THE OBJECT, AND IT IS NOT A `GameId` FIELD. Identity lives on the
        /// sensor's side — `GameObject.GameId` plus `Projectile._startTime` — and the sampler that
        /// fills this struct resolves neither, on purpose (see `TrajectorySampler.FillTrajectory`).
        ///
        /// THIS FIELD REPLACED TWO THAT WERE WORSE THAN USELESS. The struct used to carry `GameId`
        /// and `StartTime` as separate ints, which the sampler left at zero; the trace recorder then
        /// wrote `t.GameId` — **zero for every bullet in a live 1289-frame recording** — and `(0,
        /// startTime)` collides across bullets and matches nothing the game ever resolved. An
        /// entire recorded session read as "11 predictions, all wrong, and two collisions the model
        /// never saw". A pair of fields nothing fills is an invitation to write them anyway; a
        /// single field the caller must set is not.
        /// </summary>
        public long Key;

        /// <summary>The live object's start time, when the sampler could read it. Diagnostic only.</summary>
        public int StartTime;

        /// <summary>Live position when the sample set was taken, for the anchor check.</summary>
        public float LiveX;
        public float LiveY;

        /// <summary>Projectile radius, which the game's own hit test adds to the player's. Read live.</summary>
        public float Radius;

        /// <summary>Interpolation step of the sample grid, in milliseconds.</summary>
        public float StepMs;

        /// <summary>Raw damage payload, carried for section 7's projection. Zero when not read.</summary>
        public float Damage;
        public float DamagePercent;
        public int ManaDamage;

        /// <summary>True when this bullet's trajectory may be projected; `LinearityGate`'s verdict.</summary>
        public bool Projectable;

        /// <summary>
        /// The client's own answer to "can this projectile ever hit a player-side entity"
        /// (`Projectile.HitsPlayer`, `Projectile.cs:131`).
        ///
        /// IT IS NOT ADVISORY. `Projectile.Hit()` is `if (HitsPlayer) return HitCharacter(); return
        /// HitEnemy();` (`:1505`), and the spawn path sets it to `this is Enemy` (`Entity.cs:1600`),
        /// so **every projectile the local player fires has `HitsPlayer == false` and the client's
        /// player-collision code is unreachable for it.** A threat set that includes them is a dodge
        /// reacting to its own muzzle.
        /// </summary>
        public bool HitsPlayer;

        /// <summary>
        /// True only for projectiles the local player fired (`Projectile.LocalSeedDerived`,
        /// `Projectile.cs:115`). Redundant with `HitsPlayer` on the client's main spawn path and
        /// carried as the second, independent statement of the same exclusion.
        /// </summary>
        public bool LocalSeedDerived;

        public int Count;
        public readonly float[] Xs = new float[MaxSamples];
        public readonly float[] Ys = new float[MaxSamples];

        /// <summary>
        /// The game's own `GetPosition` return, per sample: false means the family has declared
        /// the shot finished at or before this sample.
        ///
        /// IT IS STORED BECAUSE THE FIRST VERSION OF THE SAMPLER THREW IT AWAY. The real
        /// signature is `bool (int,float,float,out float,out float,int)` and the value is the
        /// honest end-of-shot signal -- the same "explicit shot-ends-here flag" the reference
        /// engine's sampling discipline carries. A bullet frozen past the end of its own life
        /// paints danger where there is none, and the temporal test in section 5.5 would then
        /// refuse a move that is perfectly safe.
        /// </summary>
        public readonly bool[] Alive = new bool[MaxSamples];

        /// <summary>The position at sample <paramref name="i"/>, clamped to the samples that exist.</summary>
        public float XAt(int i) => Xs[i < 0 ? 0 : (i >= Count ? Count - 1 : i)];
        public float YAt(int i) => Ys[i < 0 ? 0 : (i >= Count ? Count - 1 : i)];

        /// <summary>
        /// The last sample this trajectory is trusted at, given a confidence cap.
        ///
        /// THE TRUST PREFIX (section 5.5). Everything past this index is a blind tail: either the
        /// horizon we asked for but no longer believe, or the sample at which the game itself
        /// said the shot had ended. Durability is never credited beyond it, which is exactly the
        /// property that stops a blind tail from scoring as "clear for 600 ms".
        /// </summary>
        public int TrustedCount(float trustSeconds)
        {
            int byTime = (int)((trustSeconds * 1000.0f) / StepMs) + 1;
            int byAlive = Count;
            for (int i = 0; i < Count; i++)
            {
                if (!Alive[i])
                {
                    byAlive = i + 1;   // the sample AT which the game said it ended is still a position
                    break;
                }
            }
            int n = byTime < byAlive ? byTime : byAlive;
            if (n < 1) { n = 1; }
            if (n > Count) { n = Count; }
            return n;
        }
    }

    /// <summary>An enemy body, as a disc. Rule 7 of section 5.3.</summary>
    public struct EntityBody
    {
        public float X;
        public float Y;
        public float Radius;

        /// <summary>
        /// The radius the EMITTER uses in the danger field: <c>Radius + player radius +
        /// cfg.EnemyDangerPadUnits</c>, computed once by the reader.
        ///
        /// IT IS A SECOND RADIUS BECAUSE THE TWO DO DIFFERENT JOBS, and the split is the
        /// load-bearing decision of `PLAN-ENEMY-THREATS.md` section 3.1. `Radius` is the body rule
        /// 7 refuses a candidate against, and it must stay unpadded: an inflated body has the same
        /// "encloses the player" failure mode the already-inside exception exists for, without the
        /// exception's justification, so a crowd of mobs would refuse every candidate on the frame
        /// -- the `NoAdmissible` defect measured at 27.7 % of frames. `DangerRadius` reaches the
        /// danger field only, where it is a COST: the path and the projected-danger term react to
        /// it and it cannot remove a candidate.
        ///
        /// ZERO MEANS "NO EMITTER", and it is what the harness's recorded frames and any
        /// hand-built body carry unless the caller sets it. A zero-radius emitter puts nothing in
        /// the danger field, so a body is rule 7 alone.
        /// </summary>
        public float DangerRadius;
    }

    /// <summary>
    /// One enemy the FARMER may walk to, as the sensor saw it. `PLAN-AUTOFARM.md` section 4.2.
    ///
    /// IT IS A SECOND, SEPARATE LIST FROM <see cref="DodgeSnapshot.Bodies"/>, AND THE SEPARATION IS
    /// NOT TIDINESS. The two lists answer different questions and are culled by different radii:
    /// the body list is rule 7's input and is cut at `EnemyCullRadius` (12 units, about twice the
    /// longest horizon the planner reasons over), while the farmer's list is cut at
    /// `FarmEngageRadius` -- how far away an enemy is still worth walking to. Sharing one list
    /// would make `DRELAY_DODGE_ENEMIES=0` silently disable the farmer and would make the body
    /// cull a hard limit on the farmer's reach; and because `EnemyBodies` is the switch whose whole
    /// job is to restore today's decisions byte for byte (the T9 doctrine), nothing the farmer
    /// needs may be smuggled into it.
    ///
    /// IT CARRIES A `GameId` AND THE BODY LIST DOES NOT, because the farmer's first requirement is
    /// *stickiness*: "keep the current target while it is alive and in range" is a statement about
    /// an IDENTITY, and a target remembered by position is a target that is lost the moment it
    /// moves. `World.GetEnemies()` may return the array in any order, so an index is not an
    /// identity either. `GameId` is the same field the sampler's own `ThreatKey` uses, resolved the
    /// same way (`FieldLookup.Find`, which walks the hierarchy because it lives on `GameObject`).
    /// </summary>
    public struct FarmTarget
    {
        /// <summary>`GameObject.GameId` -- the identity the stickiness rule holds on to.</summary>
        public int GameId;

        public float X;
        public float Y;
    }

    /// <summary>
    /// The client's own collision grid, windowed to what a 600 ms horizon can reach.
    ///
    /// THE WINDOW IS AN OPTIMISATION WITH A PRECISE MEANING. A real map's grid is
    /// `[MapInfo.Width, MapInfo.Height]` and a live position is in the thousands, so copying the
    /// whole grid per frame is unthinkable and recording it per trace is worse. But a player can
    /// move at most a few units inside the horizon and an admission test never looks further, so
    /// a window centred on the player is exactly the region the decision can touch -- and it is
    /// the region the recorder writes, which is why an offline trace is a few megabytes instead
    /// of hundreds.
    ///
    /// THE INDEXING IS THE GAME'S OWN, AND GETTING IT WRONG LOOKS LIKE A WORKING DODGE. `World`
    /// indexes this array with a C cast of the float position (`conv.i4`, truncation toward
    /// zero) after bounds-checking against `Tilemap.Width`/`Height`. So a position of 1285.8
    /// reads cell 1285, and the window's origin is also truncated. `GridFromWorld` is the one
    /// place that conversion happens.
    /// </summary>
    public struct CollisionWindow
    {
        public bool Present;

        /// <summary>The world-coordinate (truncated) index of cell [0,0].</summary>
        public int OriginX;
        public int OriginY;

        public int Width;
        public int Height;

        /// <summary>Row-major, `Width * Height` bytes, from a `byte[x, y]` -- see `WriteGridWindow`.</summary>
        public byte[] Cells;

        /// <summary>The cell containing a world position, in the game's own truncating convention.</summary>
        public int GridFromWorld(float world) => (int)world;

        /// <summary>True when the world position is inside the recorded window.</summary>
        public bool InWindow(float x, float y)
        {
            if (!Present)
            {
                return true;   // no grid: the honest answer is "no evidence of a blocker"
            }
            int cx = (int)x - OriginX;
            int cy = (int)y - OriginY;
            return cx >= 0 && cy >= 0 && cx < Width && cy < Height;
        }

        /// <summary>
        /// The collision byte at a world position, or 0 when there is no grid or the position is
        /// outside the window. Zero means "no blocker", which is the safe direction for a
        /// *refusal* (a candidate outside the window is not admitted on the strength of a grid
        /// that does not cover it -- see `DodgeSolver.Admit`, which refuses out-of-window moves
        /// rather than reading this as clear).
        /// </summary>
        public byte At(float x, float y)
        {
            if (!Present)
            {
                return 0;
            }
            int cx = (int)x - OriginX;
            int cy = (int)y - OriginY;
            if (cx < 0 || cy < 0 || cx >= Width || cy >= Height)
            {
                return 0;
            }
            return Cells[(cy * Width) + cx];
        }

        /// <summary>True when a world position is walkable, and only when the window covers it.</summary>
        public bool Walkable(float x, float y)
        {
            if (!Present)
            {
                return true;   // rule 2 degrades away when the offset could not be resolved
            }
            if (!InWindow(x, y))
            {
                return false;  // out of the recorded window is not evidence of walkability
            }
            return At(x, y) == 0;
        }
    }

    /// <summary>The player, as the decision sees them. Plain floats; nothing here is a pointer.</summary>
    public struct PlayerView
    {
        public float X;
        public float Y;

        /// <summary>`GameObject.Radius`. The game's hit test adds this to the bullet's, so it is never added twice.</summary>
        public float Radius;

        /// <summary>Effective health for section 7's gate. The game's own number, not a recomputation.</summary>
        public double Health;

        /// <summary>Current speed in world units per second, from the client's own speed law.</summary>
        public float SpeedPerSecond;

        /// <summary>`GameInput.Moving`.</summary>
        public bool Moving;

        /// <summary>The heading the player's own input asks for, radians. Intent preservation reads this.</summary>
        public float IntentRadians;

        /// <summary>
        /// The world heading the character is ACTUALLY MOVING ALONG, radians, measured from the
        /// displacement the client itself produced between the last two frame ticks.
        ///
        /// THE FORWARD GOAL IS BUILT FROM THIS, AND IT IS NOT THE SAME NUMBER AS
        /// <see cref="IntentRadians"/>. Intent is what the player's hands are asking for; the course
        /// is what the body is doing, and while the dodge is steering the two differ by exactly the
        /// amount the dodge is overriding. "The imaginary point in front of the character" is on the
        /// course -- the direction they will actually carry on in if nothing changes -- and using
        /// intent for it produces a goal the character is not travelling toward.
        ///
        /// IT IS VALID ONLY WHEN THE CHARACTER MOVED. A pinned frame, a stand frame or a world that
        /// teleported the body produce no usable course, and the solver falls back to intent. The
        /// flag exists rather than a sentinel angle because every angle is a legal heading.
        /// </summary>
        public float CourseRadians;

        /// <summary>True when <see cref="CourseRadians"/> was measured from real movement.</summary>
        public bool CourseValid;

        /// <summary>
        /// True when <see cref="IntentRadians"/> was actually read from the client this frame.
        ///
        /// IT EXISTS BECAUSE ZERO IS A LEGAL HEADING AND A MISSING FIELD IS NOT. `IntentRadians`
        /// defaults to 0.0, which is due east, so a run in which `GameInput.MoveAngle` never resolved
        /// would otherwise hand the solver an intent pointing east on every frame -- and the whole
        /// design leans on the intent twice: the `IntentSafe` early-out emits it as a steering
        /// command, and it is the forward goal's fallback. A plausible constant is the worst kind of
        /// wrong, and the adapter already knows the answer (`_getMoveAngleRadians != null`); this is
        /// that answer, carried rather than inferred.
        /// </summary>
        public bool IntentValid;

        /// <summary>`World.PlayerDead`.</summary>
        public bool Dead;

        /// <summary>`World.SafeWorld`, which makes the escape a no-op.</summary>
        public bool SafeWorld;
    }

    /// <summary>
    /// One frame's decision input, and the whole of the solver's world.
    ///
    /// IT IS A CLASS, NOT A STRUCT, AND THE REASON IS THE THREAT LIST. The list is reused
    /// between frames -- allocation on the frame path is the one thing this project has measured
    /// repeatedly and refuses -- so the snapshot is a long-lived mutable object whose contents
    /// are rewritten each frame, not a value passed by copy.
    /// </summary>
    public sealed class DodgeSnapshot
    {
        /// <summary>Frame counter, for the trace and for the hysteresis dwell.</summary>
        public long Frame;

        /// <summary>`ClientTime.LogicTotal`, milliseconds. The clock the game itself uses.</summary>
        public float ClockMs;

        /// <summary>The frame's own delta, seconds. `b = SpeedPerSecond * FrameSeconds`.</summary>
        public float FrameSeconds;

        public PlayerView Player;

        /// <summary>The live threats, reused: `Threats.Count` is what is valid.</summary>
        public readonly System.Collections.Generic.List<Trajectory> Threats =
            new System.Collections.Generic.List<Trajectory>();

        /// <summary>Enemy bodies near the player. Rule 7 of section 5.3; empty is legal.</summary>
        public readonly System.Collections.Generic.List<EntityBody> Bodies =
            new System.Collections.Generic.List<EntityBody>();

        /// <summary>
        /// The enemies the farmer may walk to. Empty is legal and is the switch's off position.
        /// See <see cref="FarmTarget"/> for why this is not the body list.
        /// </summary>
        public readonly System.Collections.Generic.List<FarmTarget> FarmTargets =
            new System.Collections.Generic.List<FarmTarget>();

        /// <summary>
        /// THE FARMER'S GOAL AS A POINT, WHICH IS THE WHOLE OF WHAT THE FARMER ADDS TO THE SOLVER.
        ///
        /// THE EXISTING GOAL IS A RAY AND A RAY CANNOT EXPRESS ARRIVAL. `GoalSource` is resolved from
        /// `PlayerView.IntentRadians`/`CourseRadians` -- a heading, with no notion of "and stop when
        /// you get there" -- so "walk to that enemy" is not a question the ray form can ask.
        /// `PLAN-AUTOFARM.md` section 4.1 makes this the smallest change that produces the walk.
        ///
        /// IT IS SET BY THE FARMER AND READ BY THE SOLVER AND THE PLANNER. The solver turns it into
        /// `_goalRadians` (so every existing goal term, fan and path target works unchanged), and
        /// `PathPlanner.BeginFrame` uses the point's own cell as the search target when the point is
        /// inside the grid window. `GoalHasPoint` is cleared every frame by <see cref="Clear"/>: a
        /// point left over from the previous frame is a goal built for a world that no longer exists,
        /// which is the same reasoning the adapter uses when it clears a pending command each frame.
        /// </summary>
        public bool GoalHasPoint;

        public float GoalX;
        public float GoalY;

        /// <summary>
        /// WHICH MODULE PUBLISHED THE POINT, which the solver needs and cannot infer.
        ///
        /// THE TWO PUBLISHERS WANT DIFFERENT ARRIVAL BEHAVIOUR AND EVERYTHING ELSE IS SHARED. A
        /// farmer walks at a mob and then STOPS, because standing in weapon range and letting the
        /// client's auto-fire work is the point of the farmer; a route is a WALK, so its follower
        /// advances to the next waypoint and the character must keep going. Both are "a point goal"
        /// to the planner and to every score term, and they differ in exactly one place -- the
        /// arrival policy of `PLAN-AUTOFARM.md` section 4.3 -- so the discriminator is one boolean
        /// rather than a second snapshot field carrying a second point.
        ///
        /// THE FARMER WINS WHEN BOTH COULD PUBLISH, because `RouteRuntime.ApplyGoal` returns without
        /// touching the snapshot when a point is already there -- see section 5.3's "if target: walk
        /// at it; else: travel".
        /// </summary>
        public bool GoalFromRoute;

        public CollisionWindow Grid;

        /// <summary>True when the sensor could not produce a usable picture this frame.</summary>
        public bool Stale;

        public void Clear()
        {
            Threats.Clear();
            Bodies.Clear();
            FarmTargets.Clear();
            GoalHasPoint = false;
            GoalX = 0.0f;
            GoalY = 0.0f;
            GoalFromRoute = false;
        }
    }

    /// <summary>
    /// Everything the solver may be tuned by, in one place, with the specification's own
    /// starting values.
    ///
    /// EVERY NUMBER HERE IS A STARTING VALUE AND NONE IS INHERITED (section 9). The three that
    /// are *resolved* rather than guessed are recorded in the comments and should not be
    /// re-derived: the speed law, the 200 ms server tick, and `b = speed * frameSeconds`.
    /// </summary>
    public struct DodgeConfig
    {
        // --- Geometry ------------------------------------------------------------------

        /// <summary>Sampling step of the bullet grid, ms. Section 9: a sampling rate, not a game constant.</summary>
        public float SampleStepMs;

        /// <summary>Samples per trajectory, fixed by the sampler.</summary>
        public int SampleCount;

        /// <summary>The horizon the decision reasons over, ms. Ours, to be re-derived; 600 is the working value.</summary>
        public float HorizonMs;

        /// <summary>
        ///How far into the horizon a sampled trajectory may be *believed*, seconds.
        ///
        /// THIS IS THE TRUST PREFIX AND IT IS NOT THE HORIZON. Section 5.5: durability is never
        /// credited beyond the point where the prediction is trustworthy, "which is what stops a
        /// blind tail from scoring as clear for 600 ms". Setting it equal to the horizon is the
        /// mistake this field exists to make impossible.
        /// </summary>
        public float TrustSeconds;

        /// <summary>Headings in the candidate ring. 24 gives 15 degrees.</summary>
        public int Headings;

        /// <summary>Candidate radii as fractions of the per-frame budget `b`.</summary>
        public float RadiusNear;
        public float RadiusMid;
        public float RadiusFar;

        /// <summary>Distance between samples along the player's own swept path, world units.</summary>
        public float SweepStep;

        /// <summary>Player radius assumed when the game's own is zero (game default is 0.25).</summary>
        public float DefaultPlayerRadius;

        // --- Admission -----------------------------------------------------------------

        /// <summary>
        /// Extra clearance along the swept-segment tests (rules 3, 4, 5 of section 5.3), as
        /// **squared** world units. Zero means the test is exactly the game's own circle test.
        ///
        /// A small pad was considered and deliberately left at zero: the game's own hit test is
        /// the authority, and a pad is a tuning decision that belongs in the harness, not in the
        /// geometry. The field exists so it can be tuned without editing the test.
        /// </summary>
        public float SweepPadSquared;

        /// <summary>
        /// How much further than the collision radius the **warning** pre-test looks, in world
        /// units.
        ///
        /// THE TWO RADII ARE DIFFERENT QUESTIONS AND CONFLATING THEM BREAKS THE SOLVER. A warning
        /// is about the path -- "this bullet's predicted path comes near the line I am on" -- and
        /// a collision is about the arrival -- "this bullet touches me". If the collision test is
        /// the stricter of the two, the solver is asked to avoid a bullet it cannot avoid, refuses
        /// every candidate including `stand`, and dies where it stands.
        ///
        /// The pad also covers the sampling gap, which is the reason the reference engine carries
        /// an arrival margin at all: at 30 ms between knots a 24 unit/s bullet moves 0.72 units,
        /// so a path can cross the collision disc between two knots without either knot being
        /// inside it. This is what makes the pre-test see that case.
        /// </summary>
        public float WarningRadiusPad;

        /// <summary>
        /// How much of section 5.3's rule 6 to apply to a *moving* candidate, in seconds.
        ///
        /// The rules are asymmetric on purpose: a moving candidate is only being tested for the
        /// frame it is executed in, and the next frame re-decides. Testing every move against the
        /// full horizon would refuse a move that is correct now and wrong in 500 ms, which the
        /// next frame can fix, and it would make the dodge refuse to move at all in a dense
        /// volley. So the move's own temporal test is the frame window; `stand` alone gets the
        /// full-horizon durability test (section 5.4), because standing is the choice where
        /// "safe now" and "safe later" are the same question.
        ///
        /// IT IS NOW A FLOOR RATHER THAN THE WINDOW. It used to BE the window, and a fixed 50 ms is
        /// a DISTANCE of `0.05 * bullet_speed` -- 0.10 units for the 2 u/s projectiles of
        /// `20261015-153805-p26156` and 1.19 for a 24 u/s one, against a collision disc of 0.41.
        /// One constant cannot be the right lookahead for both: at the slow end it does not cover
        /// the disc, at the fast end it covers three times it. See
        /// `DecisionDiscMultiplier` for what replaced it. This remains, defaulted to zero and
        /// non-binding, as the absolute floor an operator can put under the whole rule.
        /// </summary>
        public float MoveTemporalWindowSeconds;

        /// <summary>
        /// The rule 6 decision window, as a MULTIPLE OF THE TIME THIS PROJECTILE TAKES TO CROSS THE
        /// COLLISION DISC. Unitless, and this is the number the operator asked to be able to pass
        /// at startup.
        ///
        /// THE DEFECT IT FIXES IS THAT THE WINDOW WAS A CONSTANT TIME AND THEREFORE A
        /// SPEED-DEPENDENT DISTANCE. `DODGE-ALGORITHM.md` section 5.3's temporal refusal is "inside
        /// the decision window the move is refused outright", and the window was 50 ms for every
        /// projectile in the game. What that MEANS is that the dodge protects a stretch of the
        /// bullet's path `0.05 * v` units long, which is not a fact about the bullet that matters --
        /// the fact that matters is the collision disc, `player_radius + bullet_radius`, and how
        /// long the bullet takes to cross it, `disc / v`. Measured over the five recordings
        /// (`tools/projectile_speed_report.py`), on 15360 projectiles:
        ///
        /// | run | speed p50 | disc | protected in 50 ms | protected / disc |
        /// |---|---|---|---|---|
        /// | `p20152` | 16.0 u/s | 0.41 | 0.80 | **1.95x** |
        /// | `p5824` | 16.0 u/s | 0.41 | 0.80 | **1.95x** |
        /// | `p26684` | 16.0 u/s | 0.41 | 0.80 | **1.95x** |
        /// | `p5424` | 16.0 u/s (max 29.7) | 0.41 | 0.80 | **1.95x** (up to 2.52x) |
        /// | `p26156` | **6.0 u/s** | 0.51 | **0.30** | **0.53x** |
        ///
        /// So the fixed window is nearly TWICE the disc in the fast sessions and barely HALF of it
        /// in the slow one -- over-refusing on the first four and under-refusing on the fifth, which
        /// is the one every other number in this document is measured against. The window is now
        /// `DecisionDiscMultiplier * disc / v` seconds, or `MoveTemporalWindowSeconds` if that is
        /// larger, and the quantity is then the same kind of thing for every projectile in the game.
        ///
        /// WHAT THIS IS NOT. The `disc / v` scaling already accounts for speed, so the multiplier is
        /// a pure preference about how much of the path either side of the disc to refuse rather
        /// than a second speed term. The one-sided clause beyond the window, and the
        /// already-inside/bypassed exceptions around it, are untouched: this moves a boundary, it
        /// does not add or remove a rule.
        ///
        /// AND THE WINDOW IS CLAMPED TO `PlanLookaheadSeconds`, which is a correctness fix rather
        /// than a preference. A nearly-stationary projectile has an enormous crossing time -- the
        /// first version of this measured a **30720 ms** window from a 0.5 u/s bullet on `p5824` --
        /// and the walk only ever looks one lookahead ahead, so a window longer than that is
        /// `dangerMs &lt;= infinity`: it refuses unconditionally every candidate that touches the
        /// disc, which is the pre-one-sided behaviour coming back on exactly the frames where a
        /// parked projectile is nearby.
        /// </summary>
        public float DecisionDiscMultiplier;

        /// <summary>
        /// How far ahead a bullet's arrival counts as "now, or nearly now", in seconds.
        ///
        /// THIS IS THE THRESHOLD THAT MAKES THE DODGE ABLE TO MOVE AT ALL, and it is a different
        /// quantity from the decision window above. A bullet that will reach the candidate in
        /// 40 ms is the player's problem this frame; one that will reach it in 400 ms is a problem
        /// for the next twenty-four frames, each of which re-decides with better information.
        /// Refusing a candidate for danger that far out refuses every candidate against every
        /// approaching bullet -- including `stand` -- which is exactly the paralysis the first
        /// version of the temporal rule produced.
        ///
        /// It is derived rather than copied: a player at 8.2 units/s covers 0.137 units in a frame,
        /// so at 0.5 s they can be 4.1 units away from where they are now. Any bullet that arrives
        /// inside that window will still be arriving when the player has moved several times.
        /// </summary>
        public float ImpactWindowSeconds;

        /// <summary>
        /// How far ahead the player's PLAN is projected and tested against the bullets, in seconds.
        ///
        /// THE WHOLE POINT OF THE PLAN MODEL, AND THE NUMBER THAT SEPARATES IT FROM THE PATH MODEL.
        /// A candidate is no longer "a point, and a distance from that point to the bullet's whole
        /// remaining polyline" -- which treats a bullet's entire predicted path as a wall, whether
        /// the bullet is there in 600 ms or was there 600 ms ago. A candidate is a DIRECTION THE
        /// CHARACTER WALKS IN, and the question is where the bullet's body will be WHEN THE
        /// CHARACTER IS THERE. Two bodies, one clock.
        ///
        /// The lookahead is how long that question is asked for. Past it, the frame has been
        /// re-decided many times and the answer is not evidence; inside it, a violation is. It is
        /// bounded by the impact window by default, because that is the window in which a violation
        /// is this frame's business at all.
        /// </summary>
        public float PlanLookaheadSeconds;

        /// <summary>
        /// How far in front of the character the imaginary goal point sits, in seconds of travel.
        ///
        /// The goal is `player + speed * GoalLookaheadSeconds * (cos course, sin course)`, so it is
        /// a point the character can actually reach by carrying on the way they are already going.
        /// It is the anchor the dodge steers around obstacles toward rather than away from, and the
        /// reason a dodge that is not really threatened now has a direction instead of a tie.
        /// </summary>
        public float GoalLookaheadSeconds;

        /// <summary>
        /// Extra candidates either side of the goal heading, in ring steps.
        ///
        /// A one-step fan is what lets the dodge leave the goal line by the smallest amount that
        /// clears the obstacle instead of snapping to the nearest 15-degree ring heading, which is
        /// how a direction-preserving dodge is supposed to behave.
        /// </summary>
        public int GoalFanSteps;

        // --- Scoring (section 5.6) ------------------------------------------------------

        /// <summary>
        /// Whether the move actually got the player out of the corridors aimed at them.
        ///
        /// THE SAFETY TERM THE SCORE WAS MISSING, AND THE ONE THAT STOPS A DODGE FROM DRIBBLING.
        /// Every other preference term is about preference; this one is about whether the move
        /// achieved anything at all. The live session of `20261015-021911-p20152` shows what the
        /// score does without it: on frame 332 the player had a bullet arriving in 600 ms, every
        /// candidate reported `clearance 7.16` (the term saturated -- it measured the distance to
        /// where the bullet was, not to where it was going) and every candidate reported
        /// `durability 570 ms` (also saturated, capped at 1.0 by a 50 ms denominator). Nothing in
        /// the score distinguished leaving the corridor from walking into it, so the winner was
        /// decided by the move penalty -- a penalty on DISTANCE -- and the solver took the smallest
        /// step available, every frame. Six frames of `scale 0.35` covers the ground of one frame
        /// of `scale 1.00`, taken six times slower, while the bullet closes.
        /// </summary>
        public float WeightResolve;

        /// <summary>Raw geometry; small because clearance is already a hard constraint.</summary>
        public float WeightClearance;

        /// <summary>Normalised time-to-first-danger. The anti-jitter term that is not heading continuity.</summary>
        public float WeightDurability;

        /// <summary>The player's own steering is the best goal we have (section 5.7).</summary>
        public float WeightIntent;

        /// <summary>Dot with the previous chosen heading. The reference engine's largest weight.</summary>
        public float WeightHeading;

        /// <summary>Sidestepping keeps options open; fleeing radially can run into more of the volley.</summary>
        public float WeightLateral;

        /// <summary>Do not travel further than necessary.</summary>
        public float WeightMove;

        /// <summary>A small nudge toward doing nothing, deliberately below durability.</summary>
        public float WeightStandBias;

        /// <summary>
        /// What the danger of the state a move LEADS TO costs, per unit of the danger field's own
        /// `[0,1]` scale.
        ///
        /// THE TERM `SPIKE3.2-OFFLINE-SOLVER.md` SECTION 15.6 NAMES AS THE NEXT STEP, and the reason
        /// the pathfinder alone was only a partial fix. Section 15.5's term breakdown measured the
        /// retreat as paid for by `resolve` and `clearance` -- both of which are RIGHT about one step,
        /// because a retreat really does increase the distance to a bullet's corridor and really does
        /// clear corridors. A waypoint is a direction, so it cannot outvote them; a term that asks
        /// about where the candidate LEADS can. See `DodgeSolver.ProjectedDanger`.
        /// </summary>
        public float WeightProjectedDanger;

        /// <summary>
        /// What moving ALONG the line to the nearest threat costs, squared and unitless. The
        /// penalty twin of `WeightLateral`, and the term `HANDOFF-DIRECTED-DODGE.md` section 3 is
        /// about: OUTRUN and TOWARD are the same direction to `Lateralness` and it scores both
        /// zero, while the safety terms pay for the retreat and the goal pays for walking at the
        /// shooter. This removes the axial band from the menu symmetrically. See
        /// `DodgeSolver.Axialness`.
        /// </summary>
        public float WeightAxial;

        /// <summary>
        /// How far ahead a candidate's own velocity is projected when the state it leads to is scored,
        /// in seconds.
        ///
        /// Eight tenths of a second is about fourteen frames of re-decision at 60 Hz: long enough that
        /// a retreat into a closing bullet is visible in the term, short enough that the projection is
        /// still a statement about the move being made rather than about a world nobody can see.
        /// </summary>
        public float ProjectedSeconds;

        /// <summary>
        /// Samples along that projection; the danger field is read at each and the MEAN is the term.
        ///
        /// The mean rather than the maximum on purpose: a maximum saturates at 1 for any direction
        /// that crosses any corridor at any point, which is most of the ring on a busy frame, and a
        /// term that reads 0 or 1 for everything ranks nothing. The mean keeps the gradient.
        /// </summary>
        public int ProjectedSamples;

        /// <summary>
        /// How much of the move carries the character toward the PATH's waypoint rather than along
        /// the goal ray. See `HANDOFF-DIRECTED-DODGE.md` section 4.
        ///
        /// THE TERM THE DODGE WAS MISSING, AND THE ONE DEFECT 3.16 OF
        /// `SPIKE3.2-OFFLINE-SOLVER.md` SECTION 15 IS ABOUT. `goal` rewards motion along the
        /// player's own line and `lateral` rewards motion across the nearest threat, and on a frame
        /// where the player is walking AWAY from the shooter those two are the same direction -- so
        /// the biggest step wins and the dodge outruns. Measured over the five recordings with
        /// `tools/dodge_direction_report.py`: OUTRUN is 40-50 % of the dodge's own moves, and on
        /// three of the five it is the single most common thing the dodge does.
        ///
        /// It is a PREFERENCE and it goes in `ScoreAll` beside `GoalProgress`, never in `Admit`. The
        /// admissible set is the safety boundary; a path term that admits is a path term that can
        /// walk the player into a bullet, which is the one thing section 5.3's doctrine forbids.
        /// </summary>
        public float WeightPath;

        // --- The pathfinder (HANDOFF-DIRECTED-DODGE.md section 4) ------------------------

        /// <summary>
        /// Whether the frame gets a path at all. False restores the pre-path behaviour exactly, so
        /// the change has an off-switch and an A/B that is one environment variable wide.
        /// </summary>
        public bool PathEnable;

        /// <summary>
        /// How far along the goal ray the search aims, in seconds of the player's own travel.
        ///
        /// The goal is a RAY -- the player's held direction, which has no cell to search to -- so the
        /// target is the cell the character would reach by walking that way for this long. 1.5 s is
        /// twelve units at 8.2 u/s and about four seconds of game time at 60 Hz, which is long enough
        /// to route around a corridor of fire and short enough that a straight-line guess about where
        /// the player is going is still honest.
        /// </summary>
        public float PathTargetSeconds;

        /// <summary>
        /// Expansions the search may make before giving up. The fallback is the goal term, so the cap
        /// trades dodge quality for frame time and never for safety.
        /// </summary>
        public int PathNodeCap;

        /// <summary>
        /// What one unit of danger is worth in units of distance. A cell the bullet's body crosses
        /// costs the step plus this much again, so a square of corridor is worth going around while a
        /// whole detour is not -- which is the "sidestep rather than outrun" preference expressed as a
        /// cost rather than as a weight on the final score.
        /// </summary>
        public float PathStepCostWeight;

        /// <summary>
        /// Extra world units added to the collision disc when the path's danger field is built. It
        /// covers the sampling gap between a bullet's knots; it is a CLAMP on the danger radius and
        /// not a pad on the safety test, which stays exactly the game's own.
        /// </summary>
        public float PathDangerPadUnits;

        /// <summary>
        /// How far along the path the waypoint sits, in world units.
        ///
        /// IT IS NOT THE PATH HORIZON AND IT IS NOT THE STEP. The waypoint is a DIRECTION the score
        /// terms are dotted against, so it has to be far enough out to be a direction at all: a
        /// one-unit-wide grid's first cell centre is half a unit away, and which of the eight
        /// neighbours it is depends on the path's own quantisation. Measured on `p26156` at half a
        /// unit, the waypoint alternated sides on adjacent frames and the path term moved the
        /// direction histogram by three points with a weight of 1.0. At two and a half units it is
        /// stable, and it still sits well inside a path that averages ten.
        /// </summary>
        public float PathWaypointUnits;

        /// <summary>
        /// How far along the path the waypoint sits, as a FRACTION OF THE PATH'S OWN LENGTH.
        ///
        /// THE FRACTION IS THE POINT AND THE ABSOLUTE DISTANCE IS ONLY A FLOOR. A path's first
        /// half carries the goal direction -- the detour is at its far end -- so a waypoint's
        /// direction is a fact about where along the path it was taken, not about the path.
        /// </summary>
        public float PathWaypointFraction;

        /// <summary>
        /// Minimum dwell before a held waypoint may be replaced by a fresh one, seconds. The path term
        /// gets the same anti-jitter discipline as the heading, for the same reason: a path that
        /// changes its mind every frame is a dodge that changes its mind every frame.
        /// </summary>
        public float PathHysteresisDwellSeconds;

        // --- The enemy bodies (PLAN-ENEMY-THREATS.md) ------------------------------------
        //
        // TWO ROLES AND TWO SWITCHES, AND THE SPLIT IS THE POINT. An enemy is a BODY that rule 7
        // refuses a candidate against, and an EMITTER that the danger field costs -- "it will shoot"
        // -- and only the body may refuse. `EnemyBodies` and `EnemyEmitter` are separate so the two
        // can be A/B'd against each other rather than as one change.

        /// <summary>
        /// Whether the enemy body list is filled at all, and therefore whether rule 7 applies.
        ///
        /// `false` RESTORES TODAY'S BEHAVIOUR EXACTLY, which is what makes the change measurable
        /// against every existing recording: rule 7 is vacuous when `Bodies` is empty, so a run with
        /// this off must be byte-identical to one before the reader existed.
        /// </summary>
        public bool EnemyBodies;

        /// <summary>
        /// Whether a body's padded radius reaches the danger field (the path's edge cost and the
        /// `WeightEnemyDanger` score term). **It is never an admission rule.**
        ///
        /// A PREFERENCE AND NOT A GATE, for the reason `PLAN-ENEMY-THREATS.md` section 3.1 records:
        /// a padded radius inside the admission rules would make a candidate "unsafe" merely for
        /// passing within the pad of a mob, and in a crowd of mobs it would refuse every candidate
        /// on the frame -- the `NoAdmissible` defect `ThreatVerdict` was written to fix.
        /// </summary>
        public bool EnemyEmitter;

        /// <summary>
        /// The emitter's pad beyond `enemy radius + player radius`, world units. ~3/4 of a tile.
        ///
        /// IT IS SMALL ON PURPOSE, AND THE RATIONALE "IT WILL SHOOT" IS THE REASON. A ranged mob is
        /// dangerous at whatever its weapon's reach is, which the hook does not know -- so a large
        /// pad is a model with no range in it, and a melee player must stand next to enemies to hit
        /// them. The pad buys the fraction of a second between "the enemy is here" and "the bullet
        /// exists", and the bullet itself is sampled exactly, one frame later, by the existing
        /// trajectory model. `0.75` is a starting value; the harness is where it is settled
        /// (`SPIKE3.2-OFFLINE-SOLVER.md`'s doctrine: tune offline, never in a live run).
        /// </summary>
        public float EnemyDangerPadUnits;

        /// <summary>
        /// Bodies further than this from the player are not added to the list at all, world units.
        ///
        /// WHAT KEEPS THE FRAME COST BOUNDED. A full realm can hold hundreds of enemies and the
        /// solver only ever reasons about the few units it can reach; 12 is about twice the longest
        /// existing horizon (`PathTargetSeconds` at the measured speed), so it removes everything
        /// the planner could ever cost and nothing it could not.
        /// </summary>
        public float EnemyCullRadius;

        /// <summary>
        /// Weight of the new score term: how much a candidate that ends inside an enemy's EMITTER
        /// disc is penalised.
        ///
        /// ZERO IS THE SHIPPING VALUE AND THAT IS A MEASUREMENT POSTURE, NOT AN OVERSIGHT. It is the
        /// same precedent `WeightProjectedDanger` set: a term nobody turns on is a term nobody
        /// measures, so the idea ships with the implementation and the weight starts at zero until
        /// the harness says otherwise. What it answers that the path's edge cost cannot is the step
        /// AFTER this one -- `PlanWalk`/`Plan` sets the route, and this re-ranks candidates that
        /// already passed admission on where they leave the character.
        /// </summary>
        public float WeightEnemyDanger;

        // --- The farmer (PLAN-AUTOFARM.md section 4) -------------------------------------
        //
        // THE FARMER IS A NEW SOURCE OF THE GOAL AND NOTHING ELSE. It does not admit, refuse, steer,
        // aim or fire: it publishes a POINT (`DodgeSnapshot.GoalHasPoint`) and the solver's goal,
        // fan, path target and score terms all work off it unchanged. That placement is the whole
        // safety argument of the plan -- the farmer cannot walk into a bullet because the candidate
        // set it steers through is still the admissible set, and it cannot disable the dodge because
        // it touches no admission rule. `PLAN-AUTOFARM.md` section 2.
        //
        // EVERY NUMBER HERE IS A STARTING VALUE FOR THE OFFLINE HARNESS AND NONE MAY BE TUNED LIVE
        // -- the project's own rule, applied since Phase 3.

        /// <summary>
        /// Whether the farmer walks the character at an enemy. Off by default, and off restores
        /// today's decisions byte for byte -- the T9 doctrine, asserted by the harness with farm
        /// mode off over every scenario.
        ///
        /// IT IS ITS OWN SWITCH AND NOT A RIDER ON `EnemyBodies`. The farmer and rule 7 read two
        /// different lists (see <see cref="FarmTarget"/>), so either can be A/B'd without the other;
        /// `DRELAY_DODGE_ENEMIES=0` must keep restoring the vacuous rule 7 without turning the farmer
        /// into a no-op, and `FarmMode=1` must not resurrect rule 7.
        /// </summary>
        public bool FarmMode;

        /// <summary>
        /// How far away an enemy is still worth walking to, world units -- the farmer's ACQUISITION
        /// radius. `PLAN-AUTOFARM.md` section 4.2: "distance &lt;= FarmEngageRadius (default: the
        /// weapon range, from the same client call AutoAim uses)".
        ///
        /// `8.0` IS THAT DEFAULT STATED AS A NUMBER, AND THE NUMBER IS DEFENSIBLE RATHER THAN
        /// INVENTED: `AutoAim.WeaponRange` clamps the client's own `CalculateWeaponRange` into
        /// `[1.5, 10.0]`, so 8 sits inside the client's own selection band. The live value should
        /// come from `AimbotSystem.CalculateWeaponRange` for the equipped weapon, exactly as
        /// `AutoAim` does; doing that here would couple the solver to a game type, which the
        /// pure-data contract forbids, so the hook resolves it and passes it in when it can.
        /// </summary>
        public float FarmEngageRadius;

        /// <summary>
        /// HOW CLOSE THE FARMER WANTS TO BE, world units -- the radius inside which standing still
        /// and letting the client's auto-fire shoot the target is the correct behaviour.
        ///
        /// THIS IS A SECOND RADIUS AND `PLAN-AUTOFARM.md` DOES NOT HAVE IT. The plan uses one number
        /// for both jobs -- "an enemy within `FarmEngageRadius` is a target" and "`stand` is admitted
        /// only if the character is already within weapon range of its target" -- and those two
        /// sentences cannot both be about one radius: a target is BY CONSTRUCTION inside the
        /// acquisition radius, so a single number makes `stand` admissible the moment a target is
        /// chosen and the farmer never closes the distance at all.
        ///
        /// AND THE PLAN'S ARRIVAL CLAIM (section 4.1 item 5, "the character walks up to the mob and
        /// stops at its edge, because the only admissible candidates are outside the disc") IS
        /// WRONG, measured rather than argued. Rule 7 REFUSES a candidate that sweeps the body; it
        /// does not stop the character. At the body's edge every forward candidate is refused and the
        /// tangential ones are admissible, so the goal term keeps re-aiming at a direction that is
        /// unavailable and the character ORBITS the mob -- `--scenario farmerorbit` is that
        /// measurement, and it is why a stop radius exists. The farmer must stop BEFORE rule 7's
        /// boundary, not on it.
        ///
        /// ZERO MEANS "KEEP CLOSING UNTIL SOMETHING REFUSES", and the harness's `farmerorbit`
        /// fixture is the measurement of what that does -- see below.
        ///
        /// IT MUST CLEAR THE ENEMY'S EMITTER DISC, AND THE SHIPPED 1.5 IS THAT NUMBER RATHER THAN A
        /// ROUND ONE. `PLAN-ENEMY-THREATS.md` gives every enemy body a padded danger radius,
        /// `enemy.Radius + player.Radius + EnemyDangerPadUnits` -- 1.25 at the shipped values -- and
        /// `WeightEnemyDanger` (0.5) penalises a candidate that ends inside it. A stop radius INSIDE
        /// that disc makes the farmer fight its own preference: the goal says "come closer", the
        /// emitter says "get out", and the measured result is a character that circles at the
        /// disc's edge and never settles. `--scenario farmer` at `farmstop=1` does exactly that --
        /// 262 moving frames, 338 with no target, the mob lost -- while `farmstop=1.5` closes in 83
        /// frames and stands for the remaining 517. 1.5 is 1.25 plus a quarter of a unit of slack.
        ///
        /// A DEPLOYMENT WITH A LARGER `EnemyDangerPadUnits` HAS TO RAISE THIS WITH IT, and that is a
        /// real coupling rather than a hidden one: the two numbers are in the same config struct and
        /// the settings line prints both.
        /// </summary>
        public float FarmStopRadius;

        /// <summary>
        /// How much closer a different enemy must be before the farmer abandons the one it is
        /// walking at, world units. `PLAN-AUTOFARM.md` section 4.2.
        ///
        /// THE STICKINESS RULE IS NOT A NICETY AND THIS IS THE NUMBER THAT MAKES IT ONE. Tab-target
        /// thrash between two equidistant mobs is the classic farmer defect: the character oscillates
        /// between two goals and reaches neither. The rule is the same shape as the solver's own
        /// heading hysteresis -- a hold with a margin, plus a FORCED release when the held target
        /// dies or leaves the acquisition radius. It is a hold on the GOAL and not on the heading,
        /// which is what keeps T10 ("hysteresis cannot hold an unsafe heading") untouched.
        /// </summary>
        public float FarmSwitchMargin;

        // --- The routes (PLAN-AUTOFARM.md section 7) --------------------------------------
        //
        // A ROUTE IS A THIRD SOURCE OF THE GOAL, and it is the same kind of thing the farmer is: a
        // POINT that the solver's existing goal machinery consumes. It is used when the farmer has
        // NO target, which is section 5.3's `TRAVEL` state -- "no target in radius; follow the route
        // or the goal". Everything downstream is row 2's code, unchanged.
        //
        // EVERY NUMBER HERE IS A STARTING VALUE FOR THE OFFLINE HARNESS. `PLAN-AUTOFARM.md` section
        // 11 says so for all of them and repeats the project's rule: none may be tuned in a live run.

        /// <summary>Whether a recorded route may publish the frame's goal.</summary>
        public bool RouteMode;

        /// <summary>
        /// The route's name -- the file stem, `<run>/routes/<name>.json`.
        ///
        /// IT IS THE `DRELAY_DODGE_ROUTE` VALUE AND NOT A SECOND SETTING, so one variable names the
        /// route and turns the mode on; an empty name is the off position, which is the same shape
        /// `DRELAY_TRACE` uses.
        /// </summary>
        public string RouteName;

        /// <summary>
        /// The deviation's maximum magnitude, world units -- `PLAN-AUTOFARM.md` section 7.3's
        /// "bounded". At most a third of `RouteSpacingUnits`, so the deviation can never move the
        /// goal past the next waypoint and make the follower skip one.
        /// </summary>
        public float RouteDeviationUnits;

        /// <summary>
        /// The minimum spacing the recorder resamples to, world units. Section 7.2: "so the follower
        /// has a fixed step to count on". The follower's whole loop is "am I at waypoint i; if so
        /// advance", and a route whose points alternate between a hundredth of a unit apart and four
        /// units apart is a route whose reached-radius means different things in different places.
        /// </summary>
        public float RouteSpacingUnits;

        /// <summary>
        /// Douglas-Peucker's tolerance at record time, world units. Section 7.2 item 1: "the general
        /// shape, not the exact path". A hand-drawn curve through an open room becomes three points.
        /// </summary>
        public float RouteToleranceUnits;

        /// <summary>
        /// How close counts as "at the waypoint", world units. Section 7.3's `RadiusReached = 1.0`.
        /// An exact reach is a waypoint nobody ever arrives at, because the character moves in steps.
        /// </summary>
        public float RouteReachUnits;

        /// <summary>
        /// Whether EITHER point-goal module is on -- the farmer or the routes.
        ///
        /// IT IS ONE PROPERTY RATHER THAN TWO CHECKS IN TWO FILES. The solver and the planner both ask
        /// "may the snapshot's point be the goal", and the answer is the same question: a point goal
        /// is a point goal whoever published it. Two separate `FarmMode || RouteMode` expressions
        /// would be two places to forget the second module, and the failure would be a route that
        /// loads, validates, publishes a goal -- and is then ignored by the planner, with every
        /// counter agreeing that the route was followed.
        /// </summary>
        public bool PointGoal => FarmMode || RouteMode;

        /// <summary>
        /// How much of the move carries the character toward the imaginary point in front of them.
        ///
        /// THIS IS THE TERM THE DODGE WAS MISSING, AND IT IS NOT `WeightIntent` WEARING A DIFFERENT
        /// NAME. `intent` scores the direction the player's HANDS asked for, and it is the right
        /// term for "do not fight the operator". `goal` scores the direction the BODY is already
        /// going, and it is the right term for "keep going where you were going": it is what makes
        /// the dodge a dodge rather than a flinch, because sidestepping one bullet does not cost the
        /// character their line. On the frames where every corridor is already entered -- the regime
        /// that dominates a live session -- nothing else in the score has a gradient, and the third
        /// recording shows what that costs: `chosen durability p50 17 ms`, so the winners were
        /// decided by the heading and move terms, and 41 % of moves disagreed with the operator by
        /// more than 90 degrees.
        /// </summary>
        public float WeightGoal;

        /// <summary>Clearance is capped here before weighting, so a distant bullet is not a magnet.</summary>
        public float ClearComfort;

        // --- Hysteresis (section 5.7) ---------------------------------------------------

        /// <summary>How much better an alternative must be to break a held heading.</summary>
        public float HysteresisMargin;

        /// <summary>Minimum dwell before a held heading may be replaced, seconds.</summary>
        public float HysteresisDwellSeconds;

        // --- Section 7's gates ----------------------------------------------------------

        /// <summary>Escape arm budget per world; 0 disables the escape entirely.</summary>
        public int EscapeBudgetPerWorld;

        /// <summary>Seconds after an escape before another may be dispatched.</summary>
        public float EscapeRearmSeconds;

        /// <summary>Fraction of current health at which projected damage is called lethal. 1.0 = "would kill".</summary>
        public float LethalFraction;

        /// <summary>`dryRun`: decide and log everything, act on nothing. Section 7.3's default.</summary>
        public bool DryRun;

        /// <summary>
        /// Whether the solver's `stand` answer CLEARS the input (`true`) or merely YIELDS to it.
        ///
        /// THIS IS THE POLICY KNOB, AND IT IS A POLICY QUESTION RATHER THAN A MEASUREMENT ONE.
        /// Section 5.4 calls `stand` the preferred answer -- "the best dodge is usually no dodge" --
        /// and that is right for a dodge that owns the character's movement. The hook does not: the
        /// operator is holding a direction, and clearing `GameInput.Moving` takes the character away
        /// from them for as long as the stand condition holds.
        ///
        /// Measured on the live session of `20261015-024717-p5824`: **1586 frames of 10139 (15.6 %)
        /// ended in `stand`, every one of them a command that stopped a walking player**, and the
        /// solver agreed with the player's own heading only 63 times in the whole run. Whether that
        /// is the dodge doing its job or the dodge fighting its owner depends on what the operator
        /// wants, so it is a switch and not a constant.
        ///
        ///   `true`  (default) -- the specification's behaviour: `stand` stops the player.
        ///   `false`           -- `stand` writes nothing, so the player's own input stands and the
        ///                        dodge only ever intervenes to steer.
        /// </summary>
        public bool StandStops;

        /// <summary>The preset this configuration came from, or "(custom)". For the log.</summary>
        public string PresetName;

        /// <summary>The specification's own starting values, and where each one comes from.</summary>
        public static DodgeConfig Default()
        {
            DodgeConfig c = default(DodgeConfig);
            c.SampleStepMs = 30.0f;          // section 9: sampling rate, portable
            c.SampleCount = Trajectory.MaxSamples;
            c.HorizonMs = 600.0f;            // section 9: ours, to be re-derived from TIMING.md
            // STRICTLY INSIDE THE HORIZON, WITH A REASON, AND 0.45 WAS TOO SHORT.
            //
            // 0.45 s is sample 15 of 21, so the trust prefix threw away the last five samples --
            // the deceleration tail of a bullet's life, and for the head-on case the very samples
            // where its path comes closest. The measured consequence: the closest approach was
            // 0.492 by the trusted samples and 0.45 by the full set, so the solver refused to
            // stand for a threat it judged *absent* one frame later, and the harness disagreed
            // with it on four frames out of four hundred.
            //
            // The horizon the sampler was asked for is the honest limit: 600 ms is 21 samples and
            // the model has declined to say anything past it. So the prefix sits just inside that,
            // which leaves the one-sample guard the reference engine's "trust prefix" is for
            // (never credit clearance to the very last knot) without discarding the tail.
            c.TrustSeconds = 0.65f;
            c.Headings = 24;
            c.RadiusNear = 0.35f;
            c.RadiusMid = 0.70f;
            c.RadiusFar = 1.00f;
            c.SweepStep = 0.05f;
            c.DefaultPlayerRadius = 0.25f;
            c.SweepPadSquared = 0.0f;
            c.WarningRadiusPad = 0.15f;
            // THE DECISION WINDOW IS THE DISC-CROSSING TIME, NOT A CONSTANT NUMBER OF MILLISECONDS.
            // See `DecisionDiscMultiplier`: the old 0.05 was a distance of `0.05 * v` -- 1.95x the
            // collision disc on a 16 u/s projectile and 0.53x of it on a 6 u/s one -- so it could
            // not be right for both ends of the same game. The floor is zero and therefore
            // non-binding out of the box; it exists so an operator can put an absolute minimum
            // under the rule without also scaling it.
            c.MoveTemporalWindowSeconds = 0.0f;
            c.DecisionDiscMultiplier = 2.0f;
            c.ImpactWindowSeconds = 0.5f;
            // THE PLAN MODEL'S TWO HORIZONS, AND WHY THEY ARE TWO NUMBERS.
            //
            // `PlanLookaheadSeconds` is how long the bodies-against-bodies question is asked for.
            // It starts equal to the impact window -- past that, a violation is not this frame's
            // business -- and it is the quantity that replaced "the distance from this point to the
            // bullet's whole remaining path".
            //
            // `GoalLookaheadSeconds` is much shorter on purpose. The goal is a DIRECTION, and a
            // 300 ms projection of it is long enough to have a clear ordering between the ring's
            // headings and short enough that a straight line is still an honest guess about where
            // the character will be. Projecting the goal over the whole lookahead would make it a
            // second safety horizon instead of a preference.
            c.PlanLookaheadSeconds = 0.5f;
            c.GoalLookaheadSeconds = 0.30f;
            c.GoalFanSteps = 1;

            // THE SCORING WEIGHTS WERE RE-TUNED ON 2026-10-15 AGAINST A LIVE RECORDING, and the
            // changes are all consequences of one finding: three of the six terms were degenerate in
            // practice. `clearance` saturated because it measured distance to where a bullet IS;
            // `durability` saturated because `Normalize` divided by 50 ms; and `heading` plus the
            // move penalty therefore decided every frame, which produced a dodge that took the
            // smallest available step in a direction that flipped every second frame.
            //
            //   resolve    new, and the heaviest preference after `heading`: it is the only term
            //              that says whether the move ACHIEVED anything.
            //   clearance  raised from 0.25, because it now measures distance to the bullet's path
            //              and therefore has a gradient where the old one had a constant.
            //   durability raised from 0.30, because it now has range instead of being 1.0 or 1.0.
            //   move       cut hard from 1.20: the move penalty is a penalty on DISTANCE, and while
            //              the safety terms were saturated it was the only term with a gradient --
            //              so it chose a third of a step, on every frame, as a bullet closed.
            //   lateral    lowered from 1.20, because path clearance now expresses "sidestep"
            //              geometrically rather than by dotting against the nearest bullet.
            c.WeightResolve = 1.50f;
            c.WeightClearance = 1.20f;
            c.WeightDurability = 0.60f;
            c.WeightIntent = 0.40f;
            c.WeightHeading = 1.00f;
            c.WeightLateral = 0.80f;
            c.WeightMove = 0.25f;
            c.WeightStandBias = 0.15f;
            // THE GOAL TERM, AND WHY IT SITS WHERE IT DOES. Below `resolve`, so that clearing a
            // corridor always beats keeping the line; above `intent`, because the direction the
            // character is already carrying is better evidence about where they are going than the
            // direction their hands are holding -- while the dodge is steering, the hands are not
            // the thing moving the body. Section 10 records the measurements the whole set was
            // re-tuned against.
            c.WeightGoal = 0.90f;
            c.ClearComfort = 1.0f;

            // --- THE PROJECTED-DANGER TERM (SPIKE3.2-OFFLINE-SOLVER.md section 15.6) ---------
            //
            // ZERO WOULD BE THE SAFE STARTING VALUE AND IT IS NOT THE ONE THAT SHIPS. A term nobody
            // turns on is a term nobody measures, and section 15.5's breakdown is the measurement that
            // says where the retreat has to be answered: `resolve` and `clearance` pay for it and both
            // are right about one step, so the term that answers it has to be about the step after.
            //
            // 0.9 SITS BELOW `resolve` (1.5) ON PURPOSE -- clearing a corridor still beats keeping a
            // line, which is the same ordering `WeightGoal` was given for the same reason -- and above
            // `move`, so that a step which walks into the next bullet is not preferred for being
            // short. `DRELAY_DODGE_W_PROJECTED=0` restores the section 15 behaviour exactly.
            c.WeightAxial = 1.00f;
            c.WeightProjectedDanger = 0.00f;
            c.ProjectedSeconds = 0.8f;
            c.ProjectedSamples = 3;

            // --- THE PATH (HANDOFF-DIRECTED-DODGE.md section 4) -----------------------------
            //
            // AND THE GOAL TERM FALLS WHEN THE PATH IS ON, WHICH IS THE WHOLE POINT OF THE PAIR. The
            // path's waypoint IS the goal's own ray whenever the ray is walkable and safe, so the two
            // terms agree and `WeightPath` sets the scale; when the ray is blocked or dangerous the
            // waypoint moves and the two DISAGREE -- and the disagreement has to be resolvable. Left
            // at 0.90 the goal term would outvote a lateral waypoint by 0.9 to 1.0 on the very frames
            // the change exists for, so it is cut to 0.30 when the path is on and kept at 0.90 when it
            // is off, which is what makes `PathEnable` an A/B of one thing.
            c.WeightPath = 1.00f;
            c.PathEnable = true;
            c.PathTargetSeconds = 1.5f;
            c.PathNodeCap = 1200;
            c.PathStepCostWeight = 1.60f;
            c.PathDangerPadUnits = 0.25f;
            c.PathWaypointUnits = 2.5f;
            c.PathWaypointFraction = 0.75f;
            c.PathHysteresisDwellSeconds = 0.15f;
            // THE ENEMY BODIES. Rule 7 has existed and been vacuous since the solver was written --
            // `DodgeSnapshot.Bodies` was never filled -- so these ship ON. `DRELAY_DODGE_ENEMIES=0`
            // restores the vacuous rule 7 exactly. See `PLAN-ENEMY-THREATS.md` sections 3.1 and 3.2.
            c.EnemyBodies = true;
            c.EnemyEmitter = true;
            c.EnemyDangerPadUnits = 0.75f;
            c.EnemyCullRadius = 12.0f;
            // NON-ZERO, AND THAT IS A CHANGE FROM WHAT WAS DELIVERED FIRST. The weight shipped at
            // zero, on the `WeightProjectedDanger` precedent: implement, measure, do not guess. The
            // measurement then said the guess was not the problem -- the TERM never ran. It read the
            // candidate's arrival point, one frame's step away, where a mob six units down that
            // heading contributes nothing, so `wenemy=0` and `wenemy=5` produced byte-identical
            // scores and the same heading straight into the mob. With the term fixed to span the
            // planner's horizon, the smallest weight that moves `--scenario body` off the straight
            // line is 0.04; 0.5 is chosen as a mid-range preference, against `WeightPath` 1.0 and
            // `WeightGoal` 0.9, and it costs nothing when the frame has no emitter field.
            //
            // IT IS A PREFERENCE AND NOT A RULE, which is why a non-zero default is defensible at
            // all: it re-ranks the admissible set and cannot admit, refuse or pin a candidate. The
            // harness cannot settle it further yet -- no live recording carries bodies, so there is
            // no real mob geometry to sweep against. `DRELAY_DODGE_W_ENEMY` moves it in one run.
            c.WeightEnemyDanger = 0.50f;
            // THE FARMER, OFF, WITH ITS STARTING VALUES. `FarmMode` ships false because the farmer is
            // a new behaviour and every existing number in this project was measured without it.
            // `--scenario farmer*` in the SolverHarness is where these are exercised.
            c.FarmMode = false;
            c.FarmEngageRadius = 8.0f;
            // 1.5 AND NOT 1.0: see `FarmStopRadius` -- a stop radius inside the enemy's emitter disc
            // (1.25 at these pad values) makes the farmer fight `WeightEnemyDanger` and never settle.
            // This is the one number in this block that is derived from another rather than chosen.
            c.FarmStopRadius = 1.5f;
            c.FarmSwitchMargin = 1.0f;
            // THE ROUTES, OFF, WITH SECTION 7'S OWN STARTING VALUES. `RouteMode` ships false and
            // `RouteName` ships empty, which is the same switch: one variable names the route and
            // turns the mode on. The deviation is a THIRD of the spacing (1.0 against 3.0), which is
            // section 7.3's own limit and the reason the follower cannot skip a waypoint.
            c.RouteMode = false;
            c.RouteName = "";
            c.RouteDeviationUnits = 1.0f;
            c.RouteSpacingUnits = 3.0f;
            c.RouteToleranceUnits = 1.5f;
            c.RouteReachUnits = 1.0f;
            // THE GOAL TERM KEEPS ITS OWN VALUE, AND THAT IS A MEASUREMENT RATHER THAN AN OVERSIGHT.
            // The first version of this cut `WeightGoal` from 0.90 to 0.30 whenever the path was on,
            // on the reasoning that the path's waypoint IS the goal whenever the goal is walkable and
            // safe, so the two terms would agree and only `WeightPath` needed to set the scale -- and
            // on the frames where they disagreed the disagreement had to be resolvable. The reasoning
            // is sound and the numbers refused it: `p26156`, `tools/dodge_direction_report.py`, the
            // dodge's own moves --
            //
            //   path off, goal 0.90 (the baseline)  OUTRUN 30.5 %  SIDESTEP 48.8 %  TOWARD 20.7 %
            //   path on,  goal 0.30                OUTRUN 33.6 %  SIDESTEP 46.3 %  TOWARD 20.1 %
            //   path on,  goal 0.90                OUTRUN 26.4 %  SIDESTEP 49.3 %  TOWARD 24.2 %
            //
            // Cutting the goal made the dodge outrun MORE, which is the opposite of the intent, and
            // the third row is why the cut is not here. The goal term is what reduces OUTRUN -- see
            // SPIKE3.2-OFFLINE-SOLVER.md section 15 -- because it is the only term that prefers the
            // axial direction the player asked for over the axial direction that is away from the
            // shooter. The path is an addition, not a replacement.
            c.WeightGoal = 0.90f;
            // THE HYSTERESIS IS THE SPECIFICATION'S STARTING VALUE BECAUSE STRENGTHENING IT WAS
            // TRIED, MEASURED AND DID NOT HELP. The reasoning was sound and the numbers refused it.
            //
            // The dodge reverses its heading by more than 90 degrees on 384 of its 1353 moving
            // frames on `20261015-021911-p20152` -- 28 %, and the reversal examples show a period of
            // exactly eight frames, which is the dwell expiring. The frame dump says why the choice
            // changes at all: in the regime that dominates the session, the player standing inside a
            // cluster of bullets, EVERY candidate has `resolve = 0` (nothing clears a corridor you
            // are already inside), the clearance term is negative and nearly flat across the ring
            // (-0.201 to -0.257), and the top six candidates are separated by 0.026 -- far inside a
            // 0.12 margin, so the hold should have damped it.
            //
            // Raising the margin to 0.45 and the dwell to 0.25 s, on that reasoning, made it
            // slightly WORSE: 419 adjacent-frame reversals against 384, mean turn 137.8 against
            // 133.5. The hold does suppress fresh choices -- `Hysteresis` rose from 789 frames to
            // 976 and `OverrideUnsafe` fell from 511 to 324 -- and the reversals went up anyway,
            // which says the flips are not "a new candidate narrowly won": they are the HOLD ITSELF
            // being taken from a different heading than the last one commanded. Section 5.7's
            // hysteresis assumes the held heading was the last emitted one; here the `stand` and
            // intent early-outs return before the hold is consulted, so the held heading can be
            // several frames older than the last move, and `Keep` reinstates it.
            //
            // That is the next piece of work and it needs the instrument rather than a knob --
            // `--frame N` now prints the top of the menu by score, which is what this was diagnosed
            // with. Reverted rather than shipped: an unsupported change to a term that is supposed
            // to be dampening would have made the next reader's job harder, not easier.
            c.HysteresisMargin = 0.12f;
            c.HysteresisDwellSeconds = 0.10f;

            c.EscapeBudgetPerWorld = 1;
            c.EscapeRearmSeconds = 30.0f;
            c.LethalFraction = 1.0f;
            c.DryRun = true;
            c.StandStops = true;
            c.PresetName = "balanced";
            return c;
        }

        /// <summary>
        /// A named, coherent tuning, so three characters can be compared instead of one guess.
        ///
        /// WHY PRESETS RATHER THAN MORE KNOBS. The weights interact: raising `resolve` changes which
        /// candidates are admissible, which changes what the hysteresis holds, which changes the
        /// step sizes the move penalty sees. A reader handed eight numbers has no way to know which
        /// of them produced a behaviour, and the two live sessions so far produced exactly one
        /// finding each with a single change. A preset is a position someone can hold and argue
        /// with; the numbers below are the argument.
        ///
        /// All three share the geometry (`horizon`, `trust`, `headings`, the three radii, the two
        /// temporal windows). Those are measurements of the sampler and the client, not preferences,
        /// and changing them would make the three incomparable.
        /// </summary>
        public static DodgeConfig Preset(string name)
        {
            DodgeConfig c = Default();
            string key = name == null ? "" : name.Trim().ToLowerInvariant();
            switch (key)
            {
                case "":
                case "balanced":
                    // The shipped behaviour: the specification's weights, the specification's stand.
                    c.PresetName = "balanced";
                    return c;

                case "assist":
                    // INTERVENE ONLY TO STEER, AND PREFER WHERE THE PLAYER IS ALREADY GOING.
                    //
                    // For an operator who wants a safety net rather than a pilot. `stand` never
                    // stops anybody; the intent weight nearly triples so that when the dodge does
                    // take the wheel it takes it toward the direction the player asked for; the
                    // heading weight rises so that once committed it stays committed; and the move
                    // penalty is small because a bigger step that keeps the player's line is better
                    // than a small step that does not.
                    c.PresetName = "assist";
                    c.StandStops = false;
                    c.WeightResolve = 1.20f;
                    c.WeightClearance = 1.00f;
                    c.WeightDurability = 0.60f;
                    c.WeightIntent = 0.90f;
                    c.WeightGoal = 1.60f;
                    c.WeightHeading = 1.60f;
                    c.WeightLateral = 1.00f;
                    c.WeightMove = 0.15f;
                    c.WeightStandBias = 0.00f;
                    c.HysteresisMargin = 0.20f;
                    c.HysteresisDwellSeconds = 0.15f;
                    return c;

                case "evasive":
                    // SAFETY FIRST, AND WILLING TO FIGHT THE PLAYER FOR IT.
                    //
                    // `resolve` nearly doubles so that a move which leaves the corridors aimed at
                    // the player dominates everything else; the intent weight falls, so the dodge
                    // will go the other way when the other way is safer; the heading weight falls
                    // so a genuinely better direction is taken immediately rather than after the
                    // dwell. This is the one to compare against `assist` when the question is
                    // "does it actually dodge".
                    c.PresetName = "evasive";
                    c.StandStops = true;
                    c.WeightResolve = 2.20f;
                    c.WeightClearance = 1.50f;
                    c.WeightDurability = 0.80f;
                    c.WeightIntent = 0.20f;
                    c.WeightGoal = 0.45f;
                    c.WeightHeading = 0.60f;
                    c.WeightLateral = 0.60f;
                    c.WeightMove = 0.15f;
                    c.WeightStandBias = 0.00f;
                    c.HysteresisMargin = 0.10f;
                    c.HysteresisDwellSeconds = 0.08f;
                    return c;

                default:
                    c.PresetName = "(custom: unrecognised preset '" + name + "')";
                    return c;
            }
        }

        /// <summary>
        /// The hook's own knobs, from plain `DRELAY_` names. Every one falls back to the default
        /// above on a missing or unparseable value, because a typo in an environment variable
        /// must not silently become a different dodge -- it must become the default dodge.
        /// </summary>
        public static DodgeConfig FromEnvironment()
        {
            // THE PRESET IS APPLIED FIRST AND THE KNOBS OVERRIDE IT, so an operator can run
            // `DRELAY_DODGE_PRESET=evasive` and then move ONE number without re-stating the other
            // seven. The other order would make every experiment start from the specification's
            // values again and quietly discard the preset it was meant to refine.
            DodgeConfig c = Preset(Environment.GetEnvironmentVariable("DRELAY_DODGE_PRESET"));
            c.SampleStepMs = Float("DRELAY_DODGE_SAMPLE_MS", c.SampleStepMs);
            c.HorizonMs = Float("DRELAY_DODGE_HORIZON_MS", c.HorizonMs);
            c.TrustSeconds = Float("DRELAY_DODGE_TRUST_S", c.TrustSeconds);
            c.Headings = Int("DRELAY_DODGE_HEADINGS", c.Headings);
            c.RadiusNear = Float("DRELAY_DODGE_R_NEAR", c.RadiusNear);
            c.RadiusMid = Float("DRELAY_DODGE_R_MID", c.RadiusMid);
            c.RadiusFar = Float("DRELAY_DODGE_R_FAR", c.RadiusFar);
            c.WeightClearance = Float("DRELAY_DODGE_W_CLEAR", c.WeightClearance);
            c.WeightResolve = Float("DRELAY_DODGE_W_RESOLVE", c.WeightResolve);
            c.WeightDurability = Float("DRELAY_DODGE_W_DURABLE", c.WeightDurability);
            c.WeightIntent = Float("DRELAY_DODGE_W_INTENT", c.WeightIntent);
            c.WeightHeading = Float("DRELAY_DODGE_W_HEADING", c.WeightHeading);
            c.WeightLateral = Float("DRELAY_DODGE_W_LATERAL", c.WeightLateral);
            c.WeightMove = Float("DRELAY_DODGE_W_MOVE", c.WeightMove);
            c.WeightStandBias = Float("DRELAY_DODGE_W_STAND", c.WeightStandBias);
            c.WeightGoal = Float("DRELAY_DODGE_W_GOAL", c.WeightGoal);
            // THE PROJECTED-DANGER TERM'S THREE KNOBS. Zero is legal for the weight -- it is the
            // off-switch -- and it is the only one of the three that may be zero.
            c.WeightAxial = FloatAllowZero("DRELAY_DODGE_W_AXIAL", c.WeightAxial);
            c.WeightProjectedDanger = FloatAllowZero("DRELAY_DODGE_W_PROJECTED", c.WeightProjectedDanger);
            c.ProjectedSeconds = Float("DRELAY_DODGE_PROJECTED_S", c.ProjectedSeconds);
            c.ProjectedSamples = Int("DRELAY_DODGE_PROJECTED_SAMPLES", c.ProjectedSamples);
            // THE PATH'S OWN KNOBS. `DRELAY_DODGE_PATH=0` is the off-switch and it restores the
            // pre-path score exactly, which is what makes the change A/B-able on a live session
            // rather than only on a replayed recording.
            c.PathEnable = Environment.GetEnvironmentVariable("DRELAY_DODGE_PATH") != "0";
            c.WeightPath = FloatAllowZero("DRELAY_DODGE_W_PATH", c.WeightPath);
            c.PathTargetSeconds = Float("DRELAY_DODGE_PATH_TARGET_S", c.PathTargetSeconds);
            c.PathNodeCap = Int("DRELAY_DODGE_PATH_CAP", c.PathNodeCap);
            c.PathStepCostWeight = FloatAllowZero("DRELAY_DODGE_PATH_DANGER", c.PathStepCostWeight);
            c.PathDangerPadUnits = FloatAllowZero("DRELAY_DODGE_PATH_PAD", c.PathDangerPadUnits);
            c.PathWaypointUnits = Float("DRELAY_DODGE_PATH_WAYPOINT", c.PathWaypointUnits);
            c.PathWaypointFraction = FloatAllowZero("DRELAY_DODGE_PATH_WPFRACTION", c.PathWaypointFraction);
            c.PathHysteresisDwellSeconds = FloatAllowZero("DRELAY_DODGE_PATH_DWELL_S", c.PathHysteresisDwellSeconds);
            // THE ENEMY KNOBS. `DRELAY_DODGE_ENEMIES=0` is the off-switch for the whole feature and
            // it is the one that must restore today's decisions byte for byte (the T9 doctrine);
            // `DRELAY_DODGE_ENEMY_PAD` moves the emitter and must never change admission, which the
            // harness's E4 asserts rather than assumes.
            c.EnemyBodies = Environment.GetEnvironmentVariable("DRELAY_DODGE_ENEMIES") != "0";
            c.EnemyEmitter = Environment.GetEnvironmentVariable("DRELAY_DODGE_EMITTER") != "0";
            c.EnemyDangerPadUnits = Float("DRELAY_DODGE_ENEMY_PAD", c.EnemyDangerPadUnits);
            c.EnemyCullRadius = Float("DRELAY_DODGE_ENEMY_CULL", c.EnemyCullRadius);
            c.WeightEnemyDanger = FloatAllowZero("DRELAY_DODGE_W_ENEMY", c.WeightEnemyDanger);
            // THE FARMER'S KNOBS. `DRELAY_DODGE_FARM=1` is the switch and it is OFF by default, for
            // the reason the default above records. `DRELAY_DODGE_FARM_STOP=0` is legal and is the
            // plan's literal arrival rule -- see `FarmStopRadius` for what it measures.
            c.FarmMode = Environment.GetEnvironmentVariable("DRELAY_DODGE_FARM") == "1";
            c.FarmEngageRadius = Float("DRELAY_DODGE_FARM_RANGE", c.FarmEngageRadius);
            c.FarmStopRadius = FloatAllowZero("DRELAY_DODGE_FARM_STOP", c.FarmStopRadius);
            c.FarmSwitchMargin = FloatAllowZero("DRELAY_DODGE_FARM_MARGIN", c.FarmSwitchMargin);
            // THE ROUTE'S KNOBS. `DRELAY_DODGE_ROUTE=<name>` is both the switch and the name, for the
            // reason the field records; the four numbers move one at a time from the command line.
            string routeName = Environment.GetEnvironmentVariable("DRELAY_DODGE_ROUTE");
            c.RouteName = routeName ?? "";
            c.RouteMode = !string.IsNullOrEmpty(routeName);
            c.RouteDeviationUnits = FloatAllowZero("DRELAY_DODGE_ROUTE_DEV", c.RouteDeviationUnits);
            c.RouteSpacingUnits = Float("DRELAY_DODGE_ROUTE_SPACING", c.RouteSpacingUnits);
            c.RouteToleranceUnits = FloatAllowZero("DRELAY_DODGE_ROUTE_TOL", c.RouteToleranceUnits);
            c.RouteReachUnits = Float("DRELAY_DODGE_ROUTE_REACH", c.RouteReachUnits);
            c.PlanLookaheadSeconds = Float("DRELAY_DODGE_PLAN_LOOKAHEAD_S", c.PlanLookaheadSeconds);
            c.GoalLookaheadSeconds = Float("DRELAY_DODGE_GOAL_LOOKAHEAD_S", c.GoalLookaheadSeconds);
            c.GoalFanSteps = Int("DRELAY_DODGE_GOAL_FAN", c.GoalFanSteps);
            // THE DECISION WINDOW, AND THE TWO WAYS TO SET IT. `DRELAY_DODGE_DECISION_DISC` is the
            // multiplier on the projectile's own disc-crossing time and is the knob to use;
            // `DRELAY_DODGE_WINDOW_S` is the absolute floor under it and is 0 out of the box.
            //
            // `disc=0` is legal and means "no speed scaling" -- the rule then runs on the floor
            // alone, which is exactly the old fixed-window behaviour when the floor is 0.05. That
            // is how the change is A/B'd against the recording, so it must not be rejected as an
            // invalid value the way a missing one is.
            c.DecisionDiscMultiplier = FloatAllowZero("DRELAY_DODGE_DECISION_DISC", c.DecisionDiscMultiplier);
            c.MoveTemporalWindowSeconds = FloatAllowZero("DRELAY_DODGE_WINDOW_S", c.MoveTemporalWindowSeconds);
            c.HysteresisMargin = Float("DRELAY_DODGE_HYST_MARGIN", c.HysteresisMargin);
            c.HysteresisDwellSeconds = Float("DRELAY_DODGE_HYST_DWELL_S", c.HysteresisDwellSeconds);
            c.EscapeBudgetPerWorld = Int("DRELAY_DODGE_ESCAPE_BUDGET", c.EscapeBudgetPerWorld);
            c.EscapeRearmSeconds = Float("DRELAY_DODGE_ESCAPE_REARM_S", c.EscapeRearmSeconds);
            c.LethalFraction = Float("DRELAY_DODGE_LETHAL_FRACTION", c.LethalFraction);
            c.DryRun = Environment.GetEnvironmentVariable("DRELAY_DODGE_DRYRUN") != "0";
            // `DRELAY_DODGE_STAND_STOPS=0` makes `stand` yield instead of stopping the player.
            c.StandStops = Environment.GetEnvironmentVariable("DRELAY_DODGE_STAND_STOPS") != "0";
            return c;
        }

        private static float Float(string name, float fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            float parsed;
            if (!string.IsNullOrEmpty(raw)
                && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                && parsed > 0.0f)
            {
                return parsed;
            }
            return fallback;
        }

        /// <summary>
        /// The same parse as <see cref="Float"/>, except that zero is a legal value.
        ///
        /// IT EXISTS FOR THE TWO DECISION-WINDOW KNOBS AND FOR THE SAME REASON `Int` ACCEPTS ZERO.
        /// `Float` rejects zero because every value it reads is a positive quantity where zero is a
        /// typo that would silently disable a term. The decision multiplier is the opposite case:
        /// zero is the meaningful "no speed scaling" setting, and it is the setting the old fixed
        /// window is reproduced with. A knob whose off position is rejected cannot be turned off.
        /// </summary>
        private static float FloatAllowZero(string name, float fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            float parsed;
            if (!string.IsNullOrEmpty(raw)
                && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                && parsed >= 0.0f)
            {
                return parsed;
            }
            return fallback;
        }

        private static int Int(string name, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            int parsed;
            if (!string.IsNullOrEmpty(raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                && parsed >= 0)
            {
                return parsed;
            }
            return fallback;
        }

        /// <summary>
        /// THE SAME SETTINGS ON ONE LINE, for a field that lives inside JSON.
        ///
        /// THE TRACE HEADER IS NOT A LOG LINE AND MUST NOT CARRY A NEWLINE. The `dodge` field of the
        /// `H` record is written by a hand-rolled JSON writer that does not escape control
        /// characters, so `Describe`'s three lines became an unescaped newline inside a JSON string --
        /// and line 3 of `trace.jsonl` then read `path enable=...`, which every line-by-line reader
        /// reported as a CORRUPT RECORDING at line 3. Measured on `20261015-214418-p6412`, whose
        /// trace is otherwise healthy and 11400 frames long.
        /// </summary>
        public string DescribeOneLine()
        {
            string plan = DescribePlan().Replace("\n", " ").Replace("\r", " ").Trim();
            string weights = DescribeWeights().Replace("\n", " ").Replace("\r", " ").Trim();
            string path = DescribePath().Replace("\n", " ").Replace("\r", " ").Trim();
            string enemies = DescribeEnemies().Replace("\n", " ").Replace("\r", " ").Trim();
            string farm = DescribeFarm().Replace("\n", " ").Replace("\r", " ").Trim();
            string route = DescribeRoute().Replace("\n", " ").Replace("\r", " ").Trim();
            return plan + " " + weights + " " + path + " " + enemies + " " + farm + " " + route;
        }

        /// <summary>A one-line description for the log, so a run's own settings are readable from it.</summary>
        public string Describe()
        {
            // THE JOIN IS HERE AND IT IS `\n`, NOT `Environment.NewLine`, AND IT IS NOT COSMETIC.
            // `DescribePlan`, `DescribeWeights` and `DescribePath` each end their own line with `\n`
            // and this concatenation relied on that -- so when the path's line was appended without
            // one, the settings line read `... lookahead=0.8s x3path enable=True ...`. The damage did
            // not stop at the log: the same string is written into the TRACE HEADER as a JSON field,
            // and a raw newline inside a JSON string is not escaped by the recorder's hand-rolled
            // writer, so `trace.jsonl` acquired a line that is a bare `path enable=...` -- and every
            // reader that parses the file line by line reported the recording as CORRUPT at line 3.
            // Joining with an explicit separator makes the three parts independent of each other's
            // trailing bytes.
            string plan = DescribePlan().TrimEnd('\r', '\n');
            string weights = DescribeWeights().TrimEnd('\r', '\n');
            string path = DescribePath().TrimEnd('\r', '\n');
            string enemies = DescribeEnemies().TrimEnd('\r', '\n');
            string farm = DescribeFarm().TrimEnd('\r', '\n');
            string route = DescribeRoute().TrimEnd('\r', '\n');
            return plan + "\n" + weights + "\n" + path + "\n" + enemies + "\n" + farm + "\n" + route + "\n";
        }

        /// <summary>
        /// The geometry half of <see cref="Describe"/>, and it is SEPARATE FOR A REASON THAT COSTS A
        /// SESSION TO LEARN: `DrelayLog.cpp` truncates a line at 256 bytes with no mark saying so,
        /// and the single-line version of this string crossed that limit the moment the plan model
        /// added four numbers to it. A truncated config line is a run whose weights cannot be read
        /// back -- which is the one thing the line exists for.
        /// </summary>
        public string DescribePlan()
        {
            // `disc` IS ON THIS LINE BECAUSE IT IS GEOMETRY AND BECAUSE NOTHING ELSE PRINTS IT.
            // `DescribeWeights` is the only other half and it is a list of preferences; the decision
            // window is a property of the rule, so a run that does not print it prints numbers about
            // a dodge it cannot identify. Measured at 224 chars for this line plus 9 for ` disc={8}`
            // -- see the length note beside `DodgeAdapter`'s own report line.
            return string.Format(CultureInfo.InvariantCulture,
                "preset={0} horizon={1}ms trust={2}s headings={3} radii={4}/{5}/{6} standStops={7} "
                + "plan={8}s goal={9}s fan={10} disc={11}x floor={12}s | ",
                PresetName, HorizonMs, TrustSeconds, Headings, RadiusNear, RadiusMid, RadiusFar,
                StandStops, PlanLookaheadSeconds, GoalLookaheadSeconds, GoalFanSteps,
                DecisionDiscMultiplier, MoveTemporalWindowSeconds);
        }

        /// <summary>The preference half of <see cref="Describe"/>. See <see cref="DescribePlan"/>.</summary>
        public string DescribeWeights()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "weights resolve={0} clear={1} durable={2} intent={3} goal={4} path={5} axial={6} "
                + "projected={7} heading={8} lateral={9} move={10} stand={11} hyst={12}/{13}s "
                // THE NEWLINE IS PART OF THE FORMAT AND IT IS NOT COSMETIC. `Describe` is
                // `DescribePlan() + DescribeWeights() + DescribePath()`, so the three lines exist only
                // because each one ends itself; the version that appended the path's numbers without
                // one produced `x3path enable=True ...` -- a run whose settings line cannot be read
                // back, which is the one thing these lines are for. A bare `\n` rather than
                // `Environment.NewLine` because this string is written into a file that a Python
                // reader parses on whichever platform produced it.
                + "dryRun={14} lookahead={15}s x{16}\n",
                WeightResolve, WeightClearance, WeightDurability, WeightIntent, WeightGoal, WeightPath,
                WeightAxial, WeightProjectedDanger, WeightHeading, WeightLateral, WeightMove,
                WeightStandBias, HysteresisMargin, HysteresisDwellSeconds, DryRun, ProjectedSeconds,
                ProjectedSamples);
        }

        /// <summary>
        /// The pathfinder's own numbers, on their own line.
        ///
        /// A THIRD LINE RATHER THAN THREE MORE NUMBERS ON THE SECOND, and the reason is the one
        /// `DescribePlan` records: `DrelayLog.cpp` truncates at 256 bytes with no mark saying so, and
        /// a truncated settings line is a run whose settings cannot be read back -- which is the only
        /// thing these lines exist for. `HANDOFF-DIRECTED-DODGE.md` section 6 asks for exactly this
        /// and says why: "a live run's own log says which path it ran".
        /// </summary>
        public string DescribePath()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "path enable={0} target={1}s cap={2} danger={3} pad={4} wp={5}/{7} dwell={6}s",
                PathEnable, PathTargetSeconds, PathNodeCap, PathStepCostWeight, PathDangerPadUnits,
                PathWaypointUnits, PathHysteresisDwellSeconds, PathWaypointFraction);
        }

        /// <summary>
        /// The enemy-body numbers, on their own line, for the same 256-byte reason as the other two.
        ///
        /// THE LINE MATTERS AS MUCH AS THE OTHERS, AND FOR A SHARPER REASON: `bodies=0` in the dodge
        /// report is ambiguous on its own -- it is what an unresolved reader prints AND what a quiet
        /// frame prints AND what `DRELAY_DODGE_ENEMIES=0` prints. This line is what tells those three
        /// apart in a log, and it is what `PLAN-ENEMY-THREATS.md`'s E1 reads.
        /// </summary>
        public string DescribeEnemies()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "enemies bodies={0} emitter={1} pad={2} cull={3} wEnemy={4}",
                EnemyBodies, EnemyEmitter, EnemyDangerPadUnits, EnemyCullRadius, WeightEnemyDanger);
        }

        /// <summary>
        /// The farmer's own numbers, on their own line, for the same 256-byte reason as the other
        /// four (`DrelayLog.cpp` truncates with no mark saying so, and a truncated settings line is
        /// a run whose settings cannot be read back).
        ///
        /// IT MATTERS FOR THE SAME REASON `DescribeEnemies` DOES: `FarmMode` off and `FarmMode` on
        /// with no target in range produce the SAME decision on a quiet frame, and only the settings
        /// line tells a run where the farmer was never enabled from one where it had nothing to do.
        /// </summary>
        public string DescribeFarm()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "farm on={0} engage={1} stop={2} margin={3}",
                FarmMode, FarmEngageRadius, FarmStopRadius, FarmSwitchMargin);
        }

        /// <summary>
        /// The route's own numbers, on their own line, for the same 256-byte reason as the other
        /// five (`DrelayLog.cpp` truncates with no mark saying so).
        ///
        /// THE LINE MATTERS FOR THE REASON THE FARMER'S DOES: a run with `RouteMode` off and a run
        /// with it on and the player already at the route's first waypoint produce the same decision
        /// on a quiet frame, and only the settings line tells a run where no route was loaded from
        /// one where the route had nothing to say.
        /// </summary>
        public string DescribeRoute()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "route on={0} name='{1}' dev={2} spacing={3} tol={4} reach={5}",
                RouteMode, RouteName, RouteDeviationUnits, RouteSpacingUnits, RouteToleranceUnits,
                RouteReachUnits);
        }
    }

    /// <summary>
    /// The solver's answer for one frame, with the numbers that produced it.
    ///
    /// THE DIAGNOSTICS ARE PART OF THE CONTRACT, NOT LOGGING. A dodge whose decisions cannot be
    /// explained frame by frame cannot be tuned, and every one of the invariants in section 10 is
    /// asserted *against this struct* offline: T6 needs the chosen candidate's position, T10
    /// needs the heading and whether it was held, and the harness's whole output is a
    /// distribution over these fields.
    /// </summary>
    public struct DodgeDecision
    {
        public DodgeAction Action;
        public DodgeReason Reason;

        /// <summary>Steering intent for `Move`, radians in the client's input frame.</summary>
        public float HeadingRadians;

        /// <summary>Magnitude as a fraction of the per-frame budget, 1.0 for a full step.</summary>
        public float SpeedScale;

        /// <summary>Seconds until the first modelled danger under the chosen action; the horizon when none.</summary>
        public float DurabilitySeconds;

        /// <summary>Distance from the player to the chosen point, world units.</summary>
        public float Distance;

        /// <summary>The chosen candidate's index in the evaluated set, for the harness to re-derive the score.</summary>
        public int CandidateIndex;

        /// <summary>Score of the chosen candidate, and of the best rejected one.</summary>
        public float Score;
        public float RunnerUpScore;

        /// <summary>Admissible candidates of the ones evaluated. Zero means the set was empty or all refused.</summary>
        public int AdmissibleCount;
        public int EvaluatedCount;

        /// <summary>Clearance to the nearest threat at the chosen point; negative inside a disc.</summary>
        public float Clearance;

        /// <summary>How many threats can still reach the player no matter which admissible move is taken.</summary>
        public int UnavoidableThreats;

        /// <summary>Projected unavoidable damage for section 7's gate, in the game's own units.</summary>
        public double ProjectedUnavoidableDamage;

        /// <summary>Whether the chosen heading was held by hysteresis rather than chosen fresh.</summary>
        public bool Held;

        /// <summary>Microseconds the decision took, measured by the caller that runs it in the frame.</summary>
        public int CostMicroseconds;

        /// <summary>
        /// The same decision's own stage clocks, in `DodgeSolver.StageNames` order, microseconds.
        ///
        /// FIXED FIELDS RATHER THAN AN ARRAY, because `DodgeDecision` is a struct that is copied at
        /// several points in `Decide` and must stay allocation-free. Six `int`s is the cost of being
        /// able to say WHICH STAGE the tail frame spent its time in -- `SPIKE3.2-OFFLINE-SOLVER.md`
        /// section 15.11 -- and an aggregate cannot say it, because the tail is a hundredth of the
        /// frames and the mean is the other ninety-nine.
        /// </summary>
        public int StageSetupUs;
        public int StagePathUs;
        public int StageGatesUs;
        public int StageAdmitUs;
        public int StageScoreUs;
        public int StageEscapeUs;

        /// <summary>One stage of <see cref="DodgeDecision"/> by index; 0 when the index is out of range.</summary>
        public int StageUs(int stage)
        {
            switch (stage)
            {
                case 0: return StageSetupUs;
                case 1: return StagePathUs;
                case 2: return StageGatesUs;
                case 3: return StageAdmitUs;
                case 4: return StageScoreUs;
                case 5: return StageEscapeUs;
                default: return 0;
            }
        }

        /// <summary>
        /// Why the zero-length `stand` candidate was not taken, as an <c>AdmissionRule</c>.
        ///
        /// A DIAGNOSTIC WITH AN OFF-LINE READER, AND IT EXISTS BECAUSE THE AGGREGATE NUMBERS COULD
        /// NOT SETTLE A QUESTION. "The solver moved while it was durably safe" was visible in the
        /// harness, and so was "the stand candidate is admissible" -- but not *which* of the three
        /// gates in the stand branch refused, and the three have nothing in common: one is the
        /// admission rule, one is the instantaneous disc test, and one is the horizon durability
        /// test. A number that cannot be attributed is a number that costs a debugging session,
        /// which is what this one cost.
        /// </summary>
        public int StandRefusal;
    }
}
