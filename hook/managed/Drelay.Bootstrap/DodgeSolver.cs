// DodgeSolver.cs -- Spike 3.2: the decision, as a pure function of a plain-data snapshot.
//
// =====================================================================================
// WHAT THIS FILE IS
// =====================================================================================
//
// This is DODGE-ALGORITHM.md section 5, in the order that section specifies, and nothing
// else. It reads a `DodgeSnapshot` and returns a `DodgeDecision`. It touches no game type, it
// allocates nothing on the decision path, and it can therefore be run -- and tuned, and
// falsified -- in a console program with no game and no world. That property is the whole
// reason IMPLEMENTATION-PLAN.md's Spike 3.2 exists:
//
//     "The decision is a pure function of a plain-data snapshot. That property exists so that
//      the solver can be tested without the game."
//
// The same source file is compiled into the hook and into `tools/SolverHarness`, so the thing
// being tuned offline **is** the thing that will steer. A re-implementation of the solver in
// Python, which is the obvious alternative, is a second solver that agrees with the first until
// it does not.
//
// =====================================================================================
// THE ORDER OF THE STAGES, AND WHY EACH ONE IS WHERE IT IS
// =====================================================================================
//
//   1. THE INSTANTANEOUS FLOOR (section 5.1) is a hard constraint and is never relaxed. It is
//      the game's own collision predicate -- `DistanceSquared < r_eff^2`, with `r_eff` already
//      containing the player's half -- so the solver cannot disagree with the engine about
//      whether a position is inside a bullet.
//
//   2. CANDIDATES (section 5.2): stand + 24 headings x {0.35, 0.7, 1.0} b + intent + egress
//      = 77. `b = playerSpeed * frameSeconds` is the distance the player can legally cover this
//      frame, so every candidate is a move the server has already accepted thousands of times
//      (PHASE3-HANDOFF.md section 1.4).
//
//   3. ADMISSION (section 5.3), HARD, IN ORDER: in bounds, walkable, path clear, not inside a
//      disc at arrival, not inside one along the way, temporally clear, not inside an enemy
//      body. Admission is the safety boundary, so **nothing here is ever relaxed** -- under
//      budget pressure the candidate set shrinks instead (section 5.2's degradation rule).
//
//   4. `stand` IS A REAL CANDIDATE (section 5.4) and the preferred answer when it is durably
//      safe. "The best dodge is usually no dodge" is a design position, not a fallback.
//
//   5. THE TEMPORAL TEST (section 5.5) is the core algorithm: the player's swept path against
//      the bullet's predicted path, segment-vs-segment **with compatible times**, never beyond
//      the trust prefix.
//
//   6. SCORING (section 5.6) runs OVER THE ALREADY-ADMISSIBLE SET ONLY. Safety is a hard
//      constraint and preference is a score; that ordering is what makes every later feature
//      (a goal, an orbit, a formation step) safe by construction, because it can only rank
//      options that already passed stage 3.
//
//   7. THE ESCAPE GATE (section 7) is gate 6 of the SAME decision and not a second controller.
//
// =====================================================================================
// THE TWO ASYMMETRIES THAT ARE EASY TO GET WRONG, AND ARE NOT TUNING PARAMETERS
// =====================================================================================
//
//   * A MOVING CANDIDATE IS TESTED FOR ITS OWN FRAME; `stand` IS TESTED FOR THE HORIZON.
//     This is not an optimisation. A move is executed for one frame and then re-decided, so
//     demanding a full horizon of clearance from it would refuse the only correct immediate
//     move whenever the far side of the horizon is bad -- and in a dense volley *every* far
//     horizon is bad. `stand`, by contrast, is the one choice where "safe now" and "safe later"
//     are the same question, which is precisely why section 5.4 gives it the full-horizon test.
//
//   * `r_eff` ALREADY CONTAINS THE PLAYER'S HALF. Section 5.1 says this twice because the
//     reference engine's convention is the opposite: its `hitHalf` excludes the player and
//     requires the caller to add it. Ours is `player.Radius + bullet.Radius` and adding the
//     player's half again would make the dodge refuse moves it should take -- a dodge that
//     stands still and dies, from one duplicated term.

using System;
using System.Collections.Generic;

namespace Drelay.Bootstrap
{
    /// <summary>
    /// One candidate's evaluation, kept so the harness can re-derive every number the solver
    /// used and so the chosen index is meaningful.
    /// </summary>
    public struct CandidateResult
    {
        /// <summary>1 = stand, 2 = the ring, 3 = intent, 4 = egress. Only for the report.</summary>
        public int Kind;

        public float HeadingRadians;

        /// <summary>Distance from the player, world units.</summary>
        public float Distance;

        public float X;
        public float Y;

        public bool Admissible;

        /// <summary>Index into `AdmissionRule` of the first rule that refused it; 0 when admitted.</summary>
        public int RefusedBy;

        /// <summary>Seconds until the first modelled danger; the tested window when none.</summary>
        public float DurabilitySeconds;

        public float Clearance;

        /// <summary>
        /// Fraction of the frame's relevant threats whose corridor this candidate is clear of.
        ///
        /// THE SAFETY TERM THE SCORE WAS MISSING. `Clearance` says how far the nearest corridor is;
        /// this says how many of the corridors aimed at the player were actually escaped. They come
        /// apart on exactly the frames that matter -- a candidate can be three units from the
        /// nearest bullet's path and still inside the corridor of the one arriving in 200 ms -- and
        /// the second one is what "dodged" means.
        /// </summary>
        public float Resolve;

        public float Score;
    }

    /// <summary>The admission rules of section 5.3, as a closed set, so a refusal is countable.</summary>
    public enum AdmissionRule
    {
        Admitted = 0,
        InBounds = 1,
        Walkable = 2,
        PathClear = 3,
        DiscAtArrival = 4,
        DiscAlongTheWay = 5,
        TemporallyClear = 6,
        EnemyBody = 7,
        NoBudget = 8,
    }

    /// <summary>
    /// What can be done about ONE threat, decided once per frame and before any candidate exists.
    ///
    /// THIS IS THE QUESTION "SHOULD THIS BULLET BE DODGED AT ALL", AND THE SOLVER USED TO ASK IT
    /// NOWHERE. Every threat was fed to the same three hard rules, so a bullet that had already
    /// touched the player and a bullet arriving faster than the player can move both refused every
    /// candidate on the frame -- including the candidates that were busy avoiding a *different*,
    /// perfectly dodgeable bullet. On the live session of `20261015-021911-p20152` that produced
    /// **1733 NoAdmissible frames out of 6267 (27.7 %)**: more than a quarter of the fight spent
    /// frozen, with the solver reporting that nothing was safe, on frames where something was.
    ///
    /// The classification is deliberately about *feasibility* and not about danger. "Dangerous"
    /// is what the admission rules already answer; "is there any point testing candidates against
    /// this" is the question they cannot ask, because by the time a candidate exists the threat
    /// has already been assumed to be avoidable.
    /// </summary>
    public enum ThreatVerdict
    {
        /// <summary>
        /// The threat's predicted path never enters the player's collision disc. Nothing to dodge.
        ///
        /// IT IS NOT A LICENCE TO SKIP THE THREAT. A candidate MOVES the player, and the bullet's
        /// path may be exactly where the player is going -- so a `Clear` threat is still tested by
        /// every hard rule. What it is not is a reason to have evaluated the frame at all.
        /// </summary>
        Clear = 0,

        /// <summary>
        /// The threat arrives, and the player can be out of its corridor in time. The hard rules
        /// apply in full: this is the only class the admission rules were ever written for.
        /// </summary>
        Dodgeable = 1,

        /// <summary>
        /// The threat is already touching the player -- the disc contains their centre now.
        ///
        /// THE DAMAGE FOR THIS FRAME IS ALREADY COMMITTED, whatever the solver does, so refusing
        /// candidates on its account cannot prevent anything. What it CAN do is refuse the move
        /// that leaves, which is defect 3.4 of SPIKE3.2-OFFLINE-SOLVER.md arriving in rule 6 --
        /// rules 4 and 5 got the "already inside" exception and the temporal test never did.
        /// </summary>
        Inside = 2,

        /// <summary>
        /// The threat reaches the player's corridor sooner than they can walk out of it.
        ///
        /// `t_impact &lt; (collisionR - current_miss) / playerSpeed`: the closest approach happens
        /// before the player could cover the lateral distance needed to be clear of it. This is the
        /// "moving too fast" case -- a bullet that is simply faster than the dodge, not a bullet the
        /// solver failed to avoid.
        /// </summary>
        TooFast = 3,
    }

    /// <summary>
    /// WHERE THE FORWARD GOAL CAME FROM, RECORDED BY THE SOLVER RATHER THAN RE-DERIVED BY A READER.
    ///
    /// This is one field and it is the whole of the defect the directed-dodge work is about:
    /// `SPIKE3.2-OFFLINE-SOLVER.md` section 11.3 builds the goal from the measured **course** --
    /// the direction the body is already travelling -- and while the dodge is steering, that is the
    /// direction the dodge itself chose. The goal then feeds back into the score (`WeightGoal` 0.9)
    /// and the dodge commits to its own deflection. Measured on `20261015-153805-p26156`: the goal
    /// came from the course on **97.8 %** of the moves the adapter logged, and the written heading
    /// equalled that goal on 76.5 % of them.
    ///
    /// A COUNT OF THE SOURCE IS THE ONLY INSTRUMENT THAT CAN TELL "the goal is now the player" FROM
    /// "the field was missing and nothing changed" -- the two are identical in every other number
    /// the harness prints. It is reported here, carried into the `--decisions` dump, and would be
    /// carried into the hook's own report if a live session needed it.
    /// </summary>
    public enum GoalSource
    {
        /// <summary>Not an evaluated frame, or neither the course nor the intent was measured.</summary>
        None = 0,

        /// <summary>
        /// The goal is the measured course -- the world heading of the displacement the client
        /// produced. This is `SPIKE3.2-OFFLINE-SOLVER.md` section 11.3's rule and the one the
        /// operator's report is about.
        /// </summary>
        Course = 1,

        /// <summary>The goal is the player's own steering, world-converted, as the fallback.</summary>
        Intent = 2,

        /// <summary>
        /// The goal is the player's own steering because they are WALKING -- `GameInput.Moving` is
        /// set and the intent is live. `HANDOFF-DIRECTED-DODGE.md` section 5.1; this is the value
        /// whose share over a session is the change's own success measure.
        /// </summary>
        PlayerIntent = 3,
    }

    /// <summary>
    /// The hysteresis state machine of section 5.7, alone, so invariant T10 can be asserted
    /// against it directly rather than inferred from a played-back heading trace.
    ///
    /// THE PROPERTY IT EXISTS TO PROTECT, IN THE SPECIFICATION'S OWN WORDS: "a reversal that
    /// safety requires is never smoothed away. The hysteresis must not be able to lock the
    /// player into a heading that has become unsafe." That is the way anti-jitter turns into a
    /// death, and it is the failure an over-eager margin produces first -- so the margin's whole
    /// job is to be *surmountable*, and the admissibility re-check on every frame is what makes
    /// a locked heading impossible. `Decide` takes admissibility as an argument rather than
    /// re-deriving it, so there is exactly one place where that can be got wrong.
    /// </summary>
    public sealed class Hysteresis
    {
        private float _heading;
        private float _takenMs;
        private float _score;
        private bool _holding;

        /// <summary>True when a heading is being held.</summary>
        public bool Holding => _holding;

        /// <summary>The held heading, radians (the ring heading that was chosen).</summary>
        public float Heading => _heading;

        /// <summary>The score that heading was chosen with. Re-derived each frame by the caller.</summary>
        public float Score => _score;

        /// <summary>Milliseconds at which the held heading was taken.</summary>
        public float TakenMs => _takenMs;

        /// <summary>Drops the held heading -- a zone change, or a disabling of the dodge.</summary>
        public void Reset()
        {
            _holding = false;
            _score = 0.0f;
        }

        /// <summary>
        /// Whether to keep the held heading this frame.
        /// </summary>
        /// <param name="clockMs">The snapshot's clock.</param>
        /// <param name="previousScore">
        /// The held heading's own score **re-derived from this frame's candidate set**, or
        /// `float.NaN` when no candidate carries that heading. Re-deriving it rather than
        /// remembering it is what makes the comparison honest: the world has changed since the
        /// heading was taken, and a remembered score is a score for a world that no longer
        /// exists.
        /// </param>
        /// <param name="previousAdmissible">Whether that candidate was admitted this frame.</param>
        /// <param name="bestScore">The best admissible candidate's score.</param>
        /// <param name="margin">How much better the alternative must be.</param>
        /// <param name="dwellSeconds">The minimum dwell.</param>
        public bool Keep(float clockMs, float previousScore, bool previousAdmissible,
                         float bestScore, float margin, float dwellSeconds)
        {
            if (!_holding)
            {
                return false;
            }

            // THE SAFETY CLAUSE COMES FIRST, AND NEITHER THE DWELL NOR THE MARGIN CAN OVERRIDE
            // IT. An admissible alternative exists and the held heading is no longer admitted:
            // the reversal is taken now. This is the property section 5.7 calls out as the one
            // "an over-eager hysteresis breaks first", and it is the difference between a
            // vibration and a death.
            if (!previousAdmissible)
            {
                return false;
            }

            if (clockMs - _takenMs < dwellSeconds * 1000.0f)
            {
                return true;   // inside the dwell: keep it, whatever the score says
            }

            if (float.IsNaN(previousScore))
            {
                // No candidate carries this heading, which happens when the ring is small or the
                // heading was forced by the egress path. Without a comparison there is no
                // evidence the hold is still the better move, so it is not extended.
                return false;
            }

            return !(bestScore > previousScore + margin);
        }

        /// <summary>Takes a heading, chosen fresh.</summary>
        public void Take(float clockMs, float heading, float score)
        {
            _heading = heading;
            _takenMs = clockMs;
            _score = score;
            _holding = true;
        }
    }

    /// <summary>
    /// The solver. One instance per run: it holds the hysteresis and the escape budget, and it
    /// owns every buffer it needs, so a frame allocates nothing.
    /// </summary>
    public sealed class DodgeSolver
    {
        private DodgeConfig _config;
        private readonly Hysteresis _hysteresis = new Hysteresis();

        /// <summary>
        /// The frame's path, and the only opinion in this class about the NEXT step.
        ///
        /// IT IS ONE INSTANCE AND IT IS CALLED ONCE PER FRAME, from `Decide`, before any candidate is
        /// scored. See `PathPlanner`'s header: the path never reaches the input, it produces a
        /// waypoint, and `ScoreAll` ranks the already-admissible candidates by their alignment with
        /// it. Nothing here can admit, refuse or move a candidate.
        ///
        /// IT HOLDS THE CONFIG RATHER THAN A COPY OF IT. `Configure` replaces the frame's numbers --
        /// the hook re-reads them when the world changes -- and a planner holding the values it was
        /// built with would search the previous world's grid on the frame the map changed.
        /// </summary>
        private readonly PathPlanner _planner;

        // Reused per frame. Sized once to the maximum candidate count so nothing grows on the
        // frame path.
        private CandidateResult[] _results = new CandidateResult[128];
        private int _resultCount;

        private float[] _sweepX = new float[64];
        private float[] _sweepY = new float[64];
        private float[] _sweepT = new float[64];
        private int _sweepCount;

        // The per-frame envelope of the threat set: the trust prefix and the axis-aligned box of
        // the threat's own samples, both computed ONCE per frame instead of once per candidate.
        //
        // THE COST WORK IN SPIKE3.2-OFFLINE-SOLVER.md SECTION 4.3 IS WHAT PUT THESE HERE. The
        // first version rebuilt the threat's bounding box inside the candidate loop, so a frame
        // with 74 candidates and 9 nearby threats walked 74 x 9 x 21 samples to answer a question
        // that does not mention the candidate at all -- and the measured `decide()` p99 came out at
        // three times the whole 300 us frame budget. Nothing here changes a verdict: the envelope
        // is only ever used to SKIP a test that cannot succeed (see `Admit`), and it is computed
        // from the same samples, in the same order, over the same knot range as before.
        private int[] _admitTrusted = new int[0];
        private float[] _admitMinX = new float[0];
        private float[] _admitMaxX = new float[0];
        private float[] _admitMinY = new float[0];
        private float[] _admitMaxY = new float[0];
        private int _admitCount;

        // The per-frame threat CLASSIFICATION: one verdict and one path distance per threat, also
        // computed once per frame rather than once per candidate. `_verdictPath2` is how far the
        // PLAYER's current position is from the threat's predicted path -- the baseline that the
        // one-sided rules compare a candidate against, and the number that makes "moving away"
        // expressible at all.
        private int[] _verdict = new int[0];
        private float[] _verdictPath2 = new float[0];
        private int _verdictCount;
        private int _verdictRelevant;

        // THE RULE 6 DECISION WINDOW, ONE PER THREAT, COMPUTED ONCE PER FRAME.
        //
        // IT IS NOT A MICRO-OPTIMISATION. `WindowMsFor` measures a speed from the threat's samples
        // and takes a square root, and `PlanRefusal` runs once per threat per candidate -- 77
        // candidates against up to 48 threats -- so computing it at the point of use puts the same
        // two operations on the frame path 3700 times when the answer is a property of the threat
        // and the frame. That is exactly the shape `BuildAdmitEnvelope` and `BuildThreatVerdicts`
        // were introduced for (SPIKE3.2-OFFLINE-SOLVER.md section 4.4), and measuring it was the
        // reminder: moving the window into `PlanRefusal` took `decide()` p99 from 111 to 136 us on
        // `p26156` at `--repeat 50`, and caching it per threat took it back.
        private float[] _windowMs = new float[0];
        private int _windowCount;

        private float _previousHeading;
        private bool _hasPreviousHeading;

        // --- The clock ------------------------------------------------------------------

        private float _nowMs;
        private float _frameSeconds;

        // THE PLAN'S TWO PER-FRAME NUMBERS, RESOLVED ONCE IN `Decide` AND READ BY EVERY CANDIDATE.
        // `_planLookaheadMs` is the window the bodies-against-bodies question is asked over;
        // `_goalRadians` is the world heading of the imaginary point in front of the character --
        // the measured course when the character is moving, and the player's own steering when they
        // are not. The two are separate because they answer different questions: one is how long we
        // care for, the other is which way we are trying to go.
        private float _planLookaheadMs;
        private float _goalRadians;
        private bool _goalValid;

        /// <summary>
        /// How long `stand` stays safe on the frame just decided, in seconds, when `stand` was
        /// both ADMITTED and DURABLY SAFE; zero otherwise. See <see cref="StandDurabilitySeconds"/>.
        /// </summary>
        private float _standDurabilitySeconds;

        /// <summary>True when <see cref="_standDurabilitySeconds"/> is a real measurement.</summary>
        private bool _standDurableSafe;

        /// <summary>Where <see cref="_goalRadians"/> came from on the last decision. See `GoalSource`.</summary>
        private GoalSource _goalSource;

        /// <summary>The widest rule 6 decision window the last decision computed. See `DecisionWindowMs`.</summary>
        private float _decisionWindowMs;

        // --- THE STAGE CLOCK (SPIKE3.2-OFFLINE-SOLVER.md section 15.11) -------------------
        //
        // WHY A CLOCK INSIDE `Decide` AND NOT A PROFILER. The harness measures the WHOLE decision per
        // frame, and section 15.9.3b found the thing that measurement cannot answer: evaluating frames
        // on the field report's dry run read p99 **719 µs against a 300 µs budget**, the p50 is 87 µs,
        // the pathfinder's own self-clock is 31 µs either way and `axial=0` is 692 µs -- so the tail
        // is in the admission or the scoring walk and nothing says which. Every cost figure this
        // project has quoted has been a total, and a total cannot name a stage.
        //
        // FOUR MARKS AND FIVE BUCKETS, ONCE PER FRAME. `Stopwatch.GetTimestamp` is a `rdtsc`
        // underneath, so the whole apparatus is a handful of reads on a path that is already doing
        // tens of thousands of floating-point operations; the cost of measuring is not the question
        // here, and the buckets are fields so nothing allocates.
        //
        // THE BUCKETS ARE CUMULATIVE WITHIN A FRAME AND FLUSHED AT THE END: `_stageLast` is the
        // timestamp of the previous mark, so each mark books the time since the last one into its own
        // bucket. A frame that returns early books what it spent and stops.
        private const int StageSetup = 0;
        private const int StagePath = 1;
        private const int StageGates = 2;
        private const int StageAdmit = 3;
        private const int StageScore = 4;
        private const int StageEscape = 5;
        private const int StageCount = 6;

        /// <summary>Stage names, in bucket order. See <see cref="StageMicroseconds"/>.</summary>
        public static readonly string[] StageNames =
        {
            "setup", "path", "gates", "admit", "score", "escape",
        };

        private readonly double[] _stageUs = new double[StageCount];
        private readonly double[] _stageFrameUs = new double[StageCount];
        private long _stageLast;
        private bool _stageRunning;

        private void StageStart()
        {
            for (int i = 0; i < StageCount; i++)
            {
                _stageFrameUs[i] = 0.0;
            }
            _stageLast = System.Diagnostics.Stopwatch.GetTimestamp();
            _stageRunning = true;
        }

        /// <summary>Books the time since the last mark into <paramref name="stage"/> and re-arms the clock.</summary>
        private void StageMark(int stage)
        {
            if (!_stageRunning)
            {
                return;
            }
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double us = (now - _stageLast) * (1000000.0 / System.Diagnostics.Stopwatch.Frequency);
            _stageUs[stage] += us;
            _stageFrameUs[stage] += us;
            _stageLast = now;
        }

        /// <summary>
        /// Microseconds spent in one stage over every frame decided since <see cref="ResetStageClock"/>.
        /// <see cref="StageNames"/> is the index order.
        /// </summary>
        public double StageMicroseconds(int stage)
        {
            return stage >= 0 && stage < StageCount ? _stageUs[stage] : 0.0;
        }

        /// <summary>
        /// Frames whose <see cref="StageMicroseconds"/> totals are meaningful -- every frame that
        /// entered the solver, including the ones that returned early.
        /// </summary>
        public long StageFrames { get; private set; }

        /// <summary>Zeroes the stage clock. The harness calls it after its warm-up.</summary>
        public void ResetStageClock()
        {
            for (int i = 0; i < StageCount; i++)
            {
                _stageUs[i] = 0.0;
                _stageFrameUs[i] = 0.0;
            }
            StageFrames = 0;
            _stageRunning = false;
        }

        /// <summary>
        /// THIS frame's microseconds in one stage, live while the decision runs and after it
        /// returns. See <see cref="AttachStageClocks"/> for how it reaches the caller.
        /// </summary>
        public double StageFrameMicroseconds(int stage)
        {
            return stage >= 0 && stage < StageCount ? _stageFrameUs[stage] : 0.0;
        }

        /// <summary>
        /// Copies this frame's stage clocks into a decision, so a caller that keeps decisions can
        /// attribute a TAIL FRAME rather than an average. Called once per frame, at the point the
        /// decision is returned.
        /// </summary>
        public DodgeDecision AttachStageClocks(DodgeDecision decision)
        {
            decision.StageSetupUs = (int)_stageFrameUs[StageSetup];
            decision.StagePathUs = (int)_stageFrameUs[StagePath];
            decision.StageGatesUs = (int)_stageFrameUs[StageGates];
            decision.StageAdmitUs = (int)_stageFrameUs[StageAdmit];
            decision.StageScoreUs = (int)_stageFrameUs[StageScore];
            decision.StageEscapeUs = (int)_stageFrameUs[StageEscape];
            return decision;
        }

        /// <summary>
        /// The frame's own player and budget, kept so <see cref="DescribeChosen"/> can re-derive a
        /// term from the same numbers the score used.
        ///
        /// THEY ARE KEPT RATHER THAN PASSED because the caller that wants the breakdown -- the
        /// harness, printing one frame -- has the snapshot but not the resolved budget, and a reader
        /// that recomputed `speed * frameSeconds` itself would be a second implementation of the one
        /// number every candidate's scale is measured against.
        /// </summary>
        private PlayerView _lastPlayer;
        private float _lastBudget;
        private bool _lastValid;

        /// <summary>
        /// Candidates the last decision refused because they were inside the rule 6 decision window.
        ///
        /// THIS IS THE COUNT THAT SAYS WHETHER THE WINDOW IS LOAD-BEARING AT ALL, and without it
        /// the change is unfalsifiable: the window can be made a hundred times larger and the
        /// aggregate move count will not move, because rule 6's TWO clauses are not separable from
        /// outside. `dangerMs &lt;= windowMs` is the unconditional refusal and the one-sided
        /// comparison beyond it is the other, and a frame where the first never fires is a frame
        /// where the window is decoration. It is reported by `SolverHarness` as `window refusals`,
        /// and the number it is read against is the same run's total refusals.
        /// </summary>
        private long _windowRefusals;

        public DodgeSolver(DodgeConfig config)
        {
            _config = config;
            _planner = new PathPlanner(config);
            EnsureCapacity(MaxCandidates(config));
        }

        /// <summary>
        /// The lookahead in milliseconds, with the configuration as the fallback.
        ///
        /// THE FALLBACK IS FOR THE HARNESS'S OWN ENTRY POINTS, AND IT IS NOT DECORATION.
        /// `AdmissionOf` and `IsDurablySafeAt` call `Admit` directly, without `Decide`, and `Decide`
        /// is what resolves `_planLookaheadMs` for the frame. Called before any decision -- which is
        /// exactly what a harness does when it asks "would the solver have admitted this" on a frame
        /// it built by hand -- the field is still zero, the walk would stop at the first knot, and
        /// the answer would be a weaker test than the solver itself applies. A fallback makes the
        /// direct entry points answer the same question the solver does.
        /// </summary>
        private float PlanLookaheadMs
        {
            get
            {
                if (_planLookaheadMs > 0.0f)
                {
                    return _planLookaheadMs;
                }
                float configured = _config.PlanLookaheadSeconds > 0.0f
                    ? _config.PlanLookaheadSeconds
                    : _config.ImpactWindowSeconds;
                return configured > 0.0f ? configured * 1000.0f : 350.0f;
            }
        }

        public DodgeConfig Config => _config;
        public Hysteresis HysteresisState => _hysteresis;

        /// <summary>The world heading of the forward goal from the last decision. See `_goalRadians`.</summary>
        public float GoalRadians => _goalRadians;

        /// <summary>True when the last decision had a goal -- which is every evaluated frame.</summary>
        public bool GoalValid => _goalValid;

        /// <summary>Which field the last decision's goal was built from. See <see cref="GoalSource"/>.</summary>
        public GoalSource GoalSource => _goalSource;

        /// <summary>
        /// The frame's path, for the harness and for the <c>--decisions</c> dump.
        ///
        /// IT IS EXPOSED RATHER THAN COPIED INTO THE DECISION, for the reason `GoalSource` is: the
        /// path is a per-frame object with seven numbers and a lifetime, and a struct field per
        /// number in `DodgeDecision` would duplicate the planner's own state in a place that can
        /// disagree with it. A reader asks the solver that made the decision.
        /// </summary>
        public PathPlanner Path => _planner;

        /// <summary>
        /// The widest rule 6 decision window this frame computed, in milliseconds -- the value
        /// `PlanRefusal` used for the most slowly-approaching threat in the set.
        ///
        /// IT IS THE ONE NUMBER THAT MAKES THE WINDOW LEGIBLE. The window stopped being a constant
        /// (see <see cref="WindowMsFor"/>), and a per-threat quantity that nothing reports cannot be
        /// told from a constant that nothing reports -- which is this project's own recurring failure
        /// mode. A frame with a slow projectile reads a window in the hundreds of milliseconds; a
        /// frame with a fast one reads tens; and `disc=0,window=&lt;floor&gt;` reads the floor on every
        /// frame, which is how the change is A/B'd against the recording it replaced.
        ///
        /// It is the MAXIMUM rather than the minimum or the mean because the maximum is the one that
        /// decides whether the rule can fire at all: rule 6 refuses when `dangerMs &lt;= window`, so a
        /// frame's effective threshold is its largest window.
        /// </summary>
        public float DecisionWindowMs => _decisionWindowMs;

        /// <summary>
        /// Candidates the last decision refused for being inside the rule 6 decision window.
        /// See <see cref="_windowRefusals"/>.
        /// </summary>
        public long WindowRefusals => _windowRefusals;

        /// <summary>Replaces the configuration; the hook re-reads it when the world changes.</summary>
        public void Configure(DodgeConfig config)
        {
            _config = config;
            _planner.Configure(config);
            EnsureCapacity(MaxCandidates(config));
        }

        private static int MaxCandidates(DodgeConfig c)
        {
            // stand + ring + intent + egress, and then THE GOAL FAN: the exact goal heading plus
            // `GoalFanSteps` ring steps either side of it. The fan exists because the ring's 15-degree
            // headings quantise away "carry on in the direction I am already going", which is the
            // whole answer on the frames where nothing is actually in the way.
            int fan = c.GoalFanSteps < 0 ? 0 : c.GoalFanSteps;
            if (fan > 24)
            {
                fan = 24;
            }
            int n = 2 + (((c.Headings < 1 ? 24 : c.Headings)) * 3) + 2 + 1 + (2 * fan);
            return n < 8 ? 8 : n;
        }

        private void EnsureCapacity(int n)
        {
            if (_results.Length < n)
            {
                _results = new CandidateResult[n];
            }
        }

        /// <summary>Drops the held heading and the clock (a zone change).</summary>
        public void Reset()
        {
            _hysteresis.Reset();
            _planner.Reset();
            _hasPreviousHeading = false;
        }

        /// <summary>
        /// How many seconds `stand` -- staying exactly where the character is -- stays safe on the
        /// frame just decided, and ONLY when `stand` was admitted and durably safe. See
        /// <see cref="StandDurabilityKnown"/>.
        ///
        /// WHY THE SOLVER PUBLISHES THIS RATHER THAN A READER RE-DERIVING IT. The halt fix makes a
        /// frame that used to return from `stand` fall through to the ordinary ladder, so the
        /// decision on such a frame is "a move, taken while standing still was durably safe". The
        /// invariant that has to survive that (T5) is a COMPARISON -- the replacement must not be
        /// less durable than the stand it replaced -- and a comparison against a number the test
        /// computed its own way is a comparison between two implementations. `Admit` already
        /// computed this number for the stand candidate; this is that same number, carried.
        ///
        /// IT IS THE FIRST TIME THE PLAN IS VIOLATED, in seconds, and the horizon when nothing
        /// violates it -- the same quantity `CandidateResult.DurabilitySeconds` holds for any other
        /// candidate, asked about the zero-length candidate.
        /// </summary>
        public float StandDurabilitySeconds => _standDurabilitySeconds;

        /// <summary>True when <see cref="StandDurabilitySeconds"/> is a real measurement.</summary>
        public bool StandDurabilityKnown => _standDurableSafe;

        // ===================================================================================
        // The decision
        // ===================================================================================

        /// <summary>
        /// THE WHOLE DECISION FOR ONE FRAME, and the public entry: it calls <see cref="DecideCore"/>
        /// and stamps the frame's stage clocks onto whatever it returned.
        ///
        /// WHY A WRAPPER RATHER THAN MARKS AT EVERY EXIT. `DecideCore` returns from eleven places --
        /// the stale guard, the dead/safe guard, the immobile guard, the no-threat gate, `stand`,
        /// the intent early-out, the nothing-admissible gate and the escape -- and a clock that
        /// has to be remembered at each of them is a clock that will be wrong at one. One wrapper
        /// catches all eleven by construction, which is the same reasoning `Filled` gives for
        /// returning a decision rather than writing through a parameter.
        ///</summary>
        public DodgeDecision Decide(DodgeSnapshot snapshot)
        {
            return AttachStageClocks(DecideCore(snapshot));
        }

        /// <summary>
        /// The whole decision for one frame. Pure with respect to the snapshot: it mutates only
        /// this solver's own state (the hysteresis and the previous heading), which is the state
        /// the specification gives the solver.
        /// </summary>
        private DodgeDecision DecideCore(DodgeSnapshot snapshot)
        {
            DodgeDecision decision = default(DodgeDecision);
            decision.Action = DodgeAction.None;
            decision.Reason = DodgeReason.NotEvaluated;

            // THE STAGE CLOCK STARTS HERE, BEFORE ANY WORK, so a frame that returns at the first
            // guard still books what it spent. See `StageStart`.
            StageFrames++;
            StageStart();

            // The envelope belongs to the frame that built it. `AdmissionOf` -- the harness's
            // entry point -- calls `Admit` directly and may be handed a different snapshot, so the
            // count is invalidated here: the guard in `Admit` then takes the honest fallback rather
            // than reading a box computed for another world.
            _admitCount = 0;
            // The same invalidation for the classification: `AdmissionOf` calls `Admit` directly and
            // may be handed a different snapshot, so the verdicts belong to the frame that built
            // them and not to the one being asked about. With the count at zero every threat takes
            // the honest fallback (`Clear`) and the rules behave exactly as they did before the
            // classifier existed.
            _verdictCount = 0;
            // And the same for the goal's provenance: it is a fact about the frame just decided, so
            // a reader that asks after an early return must not be handed the previous frame's
            // answer. It is resolved below for every frame that gets that far.
            _goalSource = GoalSource.None;
            // And the same for the decision window, for the same reason.
            _decisionWindowMs = 0.0f;
            // And the same for the `stand` verdict's own durability: it is a fact about the frame
            // just decided, and a reader that asks after an early return must not be handed the
            // previous frame's answer -- which for a comparison against THIS frame's chosen move
            // would be a number taken from a different world.
            _standDurabilitySeconds = 0.0f;
            _standDurableSafe = false;

            if (snapshot == null || snapshot.Stale)
            {
                decision.Action = DodgeAction.Degraded;
                return AttachStageClocks(decision);
            }

            _nowMs = snapshot.ClockMs;
            _frameSeconds = snapshot.FrameSeconds > 0.0f ? snapshot.FrameSeconds : (1.0f / 60.0f);

            PlayerView player = snapshot.Player;
            float playerRadius = player.Radius > 0.0f ? player.Radius : _config.DefaultPlayerRadius;

            // --- The plan's two numbers, resolved once for the whole frame. ------------------
            //
            // THE LOOKAHEAD IS CLAMPED TO THE TRUST PREFIX, and the clamp is not decoration: past
            // the prefix the sampler has declined to say anything, and a lookahead longer than the
            // model's own honesty would test the plan against samples `TrustedCount` has already
            // refused to credit.
            float planLookahead = _config.PlanLookaheadSeconds > 0.0f
                ? _config.PlanLookaheadSeconds
                : _config.ImpactWindowSeconds;
            float trustSeconds = _config.TrustSeconds > 0.0f ? _config.TrustSeconds : 0.65f;
            if (planLookahead > trustSeconds)
            {
                planLookahead = trustSeconds;
            }
            _planLookaheadMs = planLookahead * 1000.0f;

            // THE IMAGINARY POINT IN FRONT OF THE CHARACTER IS WHERE THE PLAYER IS WALKING, AND IT
            // IS THE COURSE ONLY WHEN THEY ARE NOT WALKING. The course is a MEASUREMENT, and that is
            // exactly what made it the wrong first choice:
            //
            //   THE COURSE IS A FEEDBACK LOOP. It is the world heading of the displacement the client
            //   just produced -- and while the dodge is steering, that displacement is the dodge's
            //   own last command. So the goal was the dodge's previous choice, fed back through
            //   `WeightGoal` (0.9) on top of `WeightHeading` (1.0), which points at the same previous
            //   choice again, against `WeightIntent` (0.4) for what the player actually asked for.
            //   Measured on `20261015-153805-p26156`: on the 400 moves the adapter logged, the player
            //   was holding a direction on 342 of them and the goal came from the course on
            //   **391 (97.8 %)** -- a mean deflection of **47.4 deg**, with 297 of the 400 held at
            //   `scale 0.35` by hysteresis. That is the operator's *"the hook takes over movement
            //   completely when it's dodging"*, measured. Replayed over the whole recording rather
            //   than the log's 400-line sample, it is worse, not better: the goal came from the
            //   course on **568 of the 1665 moves (34.1 %)**, and on the 1081 moves where the player
            //   was walking the written heading was a mean **59.3 deg** from the direction they were
            //   holding.
            //
            // So the question is not "where is the body going" but "where is the player asking to
            // go", and `GameInput.Moving` is the fact that distinguishes a live request from a stale
            // one. The intent goes stale the moment the player lets go -- 93.2 % of the non-moving
            // frames of `p26156` repeat the previous intent -- so an ungated intent would walk the
            // player in a direction they had stopped asking for, which is worse than the defect.
            //
            // THE GATE IS NARROW ON PURPOSE AND IT IS NOT SECTION 3.10's GATE. See the comment on
            // `Moving` further down: that section removed `Moving` as a WHOLE-FRAME gate, because a
            // false value there disabled the solver for every frame of a 1289-frame session. This
            // asks a different question -- not "may the solver run", but "is there a direction worth
            // preserving" -- and a `Moving` that is wrong here costs one goal direction and nothing
            // else. It was measured before being used: on the 249 frames of `p26156` where the pilot
            // provably wrote nothing at all, `moving` was still true on 158 (63.5 %), and the intent
            // on those frames takes the eight compass directions. It carries the player's own state,
            // not a leftover write. `tools/intent_report.py` is the test; re-run it on a fresh
            // recording before trusting a change to this line.
            //
            // BOTH ARE WORLD HEADINGS AND BOTH ARE CONVERTED ON THE WAY IN. `IntentRadians` is
            // `MoveAngle - WorldRotation` because the client ADDS the rotation back
            // (`World.cs:12692`), and the course was never in the input frame to begin with. A goal
            // built from an unconverted input angle points the dodge off by the camera's rotation --
            // 13.7 degrees in the operator's own example, and up to 359 in the recorded sessions.
            //
            // IT IS SAFE BY CONSTRUCTION, and the reason is worth stating where the line is: the goal
            // only RANKS candidates that already passed admission. The admissible set is the safety
            // boundary and nothing here touches it, so a player holding a direction into a bullet
            // gets the best admissible move toward that direction rather than a move into it --
            // which is the intent-preserving behaviour `DODGE-ALGORITHM.md` section 5.7 makes a hard
            // requirement.
            bool playerWalking = player.Moving && player.IntentValid;
            if (playerWalking)
            {
                _goalRadians = player.IntentRadians;
                _goalSource = GoalSource.PlayerIntent;
            }
            else if (player.CourseValid)
            {
                _goalRadians = player.CourseRadians;
                _goalSource = GoalSource.Course;
            }
            else
            {
                _goalRadians = player.IntentRadians;
                _goalSource = GoalSource.Intent;
            }
            // AND THE GOAL IS ONLY A GOAL WHEN SOMETHING WAS ACTUALLY MEASURED. With no course
            // and no intent the direction is not unknown-but-guessable, it is unknown; `GoalProgress`
            // returns zero for every candidate and the decision falls back to safety and the other
            // preferences. See `PlayerView.IntentValid` for the plausible constant this avoids.
            _goalValid = playerWalking || player.CourseValid || player.IntentValid;
            if (!_goalValid)
            {
                _goalSource = GoalSource.None;
            }

            // --- THE PATH: ONE SEARCH, ONCE PER FRAME, BEFORE MAYBE-EVALUATING THE FRAME. -----
            //
            // IT IS RESOLVED HERE RATHER THAN AFTER THE THREAT GATE, AND THE REASON IS THE
            // INSTRUMENT. A run where the planner only ran on the 3410 frames that evaluate would
            // report "no path" for the other 3759, and `HANDOFF-DIRECTED-DODGE.md` section 6:
            // "a pathfinder that is silently absent prints as 'the outrun ratio did not move', which
            // is the same output as a pathfinder that ran and did nothing". Resolving it on every
            // frame makes the dump's own columns say which of the two happened. The cost is one grid
            // read of 16 cells and, on the frames with no threat, no search worth measuring -- the
            // path's cost is reported per frame by the harness, not assumed.
            //
            // AND IT IS A PREFERENCE, NOT A GATE. Nothing below this line is conditional on the path
            // existing: a frame with no grid, no goal or an unreachable target scores exactly as it
            // did before `WeightPath` existed (the term contributes nothing when the waypoint is
            // invalid). "A pathfinder that can produce no answer must not be able to produce no
            // move" -- section 4.1.
            float pathStepRadius = player.SpeedPerSecond * _frameSeconds;
            // `gates` CLOSES WITH THE GOAL RESOLUTION, WHICH IS WHERE IT STARTED. The bucket means
            // "the frame's own quantities and hazards -- the goal, the plan's numbers, the threat
            // pre-test" and not "the code near the word gate": an operator reading a stage table
            // wants to know what the TIME was for, and the two guards below are two compares.
            StageMark(StageGates);
            if (pathStepRadius > 0.0f)
            {
                _planner.Plan(snapshot, _goalRadians, _goalValid, pathStepRadius,
                              pathStepRadius, playerRadius);
            }
            StageMark(StagePath);

            // --- Stage 0: the gates that are not about bullets. -----------------------------
            //
            // These come before any geometry because they are the ladder's steps 1 and 2
            // (section 5.7), and because a dead, safe or immobile player must not pay for a
            // candidate set. They are also the honest answer in the common case: most frames
            // have no threat on a collision course at all.
            if (player.Dead || player.SafeWorld)
            {
                decision.Reason = DodgeReason.PlayerNotMoving;
                return AttachStageClocks(decision);
            }

            float budget = player.SpeedPerSecond * _frameSeconds;
            if (budget <= 0.0f)
            {
                decision.Reason = DodgeReason.Immobile;
                return AttachStageClocks(decision);
            }
            // KEPT FOR THE FRAME, WHATEVER BRANCH THE DECISION LEAVES BY. See `_lastPlayer`.
            _lastPlayer = player;
            _lastBudget = budget;
            _lastValid = true;

            // `GameInput.Moving` IS DELIBERATELY NOT A GATE HERE, AND THE RECORDED RUN IS THE
            // REASON. It used to return immediately when it was false, on the reasoning that the
            // client is not reading movement input this frame. The live recording
            // `20261014-165437-p19396` shows what that reasoning is worth: the pilot moved the
            // player on 427 of 631 frames, and **`Moving` was false on every one of the 1289
            // recorded frames** -- because the pilot sets it inside the movement integrator, and
            // the recorder runs in the frame tick, which is a different point in the frame with a
            // different view of the same field. That session decided `PlayerNotMoving` 1289 times
            // out of 1289 and the solver never evaluated a single candidate.
            //
            // It is the same trap `SteeringPilot` documents for the angle write -- "a write from the
            // frame tick would be consumed a frame later and overwritten by the real input poll in
            // `PreUpdate` in between" -- and it applies to reading as well as writing. The honest
            // gates are properties of the player rather than of one frame's input object: dead,
            // safe area, and a zero speed law. `Moving` stays in the trace, where it is evidence
            // rather than a decision.
            if (!AnyThreatNearPlayer(snapshot, player.X, player.Y, playerRadius,
                                     _config.WarningRadiusPad, budget))
            {
                decision.Reason = DodgeReason.NoThreat;
                return AttachStageClocks(decision);
            }

            // --- Stage 2 and 3: candidates and admission. -----------------------------------
            _resultCount = 0;
            BuildAndAdmit(snapshot, player, playerRadius, budget);
            decision.EvaluatedCount = _resultCount;
            StageMark(StageAdmit);
            int standIndex = FindStand();
            bool standAdmissible = standIndex >= 0 && _results[standIndex].Admissible;

            int admissible = 0;
            for (int i = 0; i < _resultCount; i++)
            {
                if (_results[i].Admissible)
                {
                    admissible++;
                }
            }
            decision.AdmissibleCount = admissible;

            // --- Stage 4: `stand` is a real candidate, and the preferred one. ---------------
            //
            // Section 5.4: "If `stand` is durable and the player is not already inside a disc,
            // the dodge does nothing. That is a feature: the best dodge is usually no dodge."
            bool standInsideDisc = InsideAnyDisc(snapshot, player.X, player.Y, playerRadius);
            bool standDurable = StaticallyDurable(snapshot, player, player.X, player.Y, playerRadius);
            bool standSafe = standAdmissible && !standInsideDisc && standDurable;
            if (standSafe)
            {
                // THE NUMBER T5 COMPARES AGAINST, RECORDED WHERE IT IS TRUE. `standSafe` is the
                // conjunction the `stand` gate itself uses, so a frame this does not fire on has no
                // stand-durability claim at all rather than a zero that reads as "unsafe".
                _standDurabilitySeconds = _results[standIndex].DurabilitySeconds;
                _standDurableSafe = true;
                // --- THE HALT DEFECT, AND WHY THIS BRANCH IS SPLIT IN TWO. ------------------
                //
                // `stand` IS ONLY "DO NOTHING" FOR A PLAYER WHO IS ALREADY DOING NOTHING.
                // `Publish` turns `StandSafe` into `DodgeCommand.Stop` when `StandStops` is on,
                // and `StopInput` clears `GameInput.Moving` -- so for a player who IS holding a
                // direction the verdict does not mean "stand", it means "the dodge takes the
                // wheel and parks you". Measured on the three acting sessions of
                // SPIKE3.2-OFFLINE-SOLVER.md section 15.9.3c, on the frames the player was
                // holding a direction: 1123 of 6231 (18.0 %), 1982 of 8435 (23.5 %), 1689 of
                // 5374 (31.4 %) -- and on EVERY ONE of those frames at least one MOVE was
                // admissible, so "there was nothing else to do" is not what happened.
                //
                // The operator's verdict names both halves of the fix:
                //
                //   "either 1) pathfind around the projectile or 2) let the player walk away
                //    from it ... this sometimes results in the player being stuck next to a
                //    slow-moving projectile, not being able to move in any direction."
                //
                // So: when the player is walking, `stand` no longer halts. If an admissible
                // MOVE exists the branch is not a `stand` at all and falls through to the
                // ordinary scoring (which is where "walk around it" is decided, by the goal,
                // the path and the safety terms together); if no admissible MOVE exists the
                // solver emits NOTHING and the player's own hands carry on -- option 2, and the
                // `StandWalkOn` reason says so on the frame it happens.
                //
                // THE SAFETY BOUNDARY IS NOT TOUCHED. The fall-through admits nothing: the
                // admissible set is whatever `Admit` already made it, and every frame that now
                // moves instead of standing was scored by `ScoreAll` subject to the same rules.
                // The other branch EMITS NOTHING AT ALL, which is strictly weaker than the stop
                // it replaces. And `stand` stays the preferred answer for a player who is not
                // walking, unchanged, which is section 5.4's whole doctrine.
                if (!playerWalking)
                {
                    return AttachStageClocks(Filled(decision, standIndex, DodgeAction.None, DodgeReason.StandSafe, budget, player));
                }
                int standWalkOnMove = FirstAdmissibleMove();
                if (standWalkOnMove < 0)
                {
                    // Option 2: stand is safe, nothing better is offerable, so the dodge does
                    // not stop the player -- it yields, and the frame is counted.
                    decision.Reason = DodgeReason.StandWalkOn;
                    decision.Action = DodgeAction.None;
                    decision.CandidateIndex = -1;
                    decision.HeadingRadians = player.IntentRadians;
                    decision.SpeedScale = 0.0f;
                    decision.DurabilitySeconds = _results[standIndex].DurabilitySeconds;
                    decision.Clearance = _results[standIndex].Clearance;
                    return AttachStageClocks(decision);
                }
                // Otherwise fall through: `stand` was safe, but the player is walking and
                // something admissible can carry them, so the normal ladder decides which.
            }

            // Why the stand branch was not taken, as an `AdmissionRule`, for the offline harness.
            // Captured HERE rather than recomputed below, so it describes the instant the gate
            // looked at -- see `DodgeDecision.StandRefusal`.
            decision.StandRefusal = standAdmissible
                ? (int)AdmissionRule.DiscAtArrival
                : (standIndex >= 0 ? _results[standIndex].RefusedBy : (int)AdmissionRule.NoBudget);

            // --- Stage 5: scoring, over the admissible set only. ---------------------------
            ScoreAll(snapshot, player, playerRadius, budget);
            StageMark(StageScore);
            int best = BestAdmissible();

            // Step 3 of the ladder, and the one early-out the whole common case rests on: the
            // player's own steering is already the answer, so nothing is overridden and the
            // ownership they keep over the damage channels this dodge does not cover is untouched
            // (section 5.7).
            //
            // IT IS COMPOSED FROM THE SAME TWO PRIMITIVES THE CANDIDATE SET USES, AND THAT IS NOT
            // A STYLE CHOICE. The first version read `_results[intentIndex].Admissible`, and that
            // field does not exist yet at this point -- the intent point is not one of the ring
            // candidates and is only admitted later, if at all. A defaulted `false` therefore
            // silently disabled the whole branch, and the observable effect was a dodge that
            // evaluated 74 candidates, found 74 of them admissible, and then chose to do nothing.
            // Composing the test here means there is nothing to forget to fill in.
            //
            // AND IT IS ONLY ASKED WHEN THERE IS AN INTENT TO OBEY. Emitting `IntentRadians` is
            // emitting a steering command, and `IntentRadians` defaults to due east -- so an
            // unresolved `GameInput.MoveAngle` would have this branch walk the player east on every
            // frame it found clear, with the log reporting `IntentSafe` as though the operator had
            // asked for it. See `PlayerView.IntentValid`.
            float intentX = player.X + (budget * (float)Math.Cos(player.IntentRadians));
            float intentY = player.Y + (budget * (float)Math.Sin(player.IntentRadians));
            if (player.IntentValid
                && StaticallyDurable(snapshot, player, intentX, intentY, playerRadius)
                && !InsideAnyDisc(snapshot, intentX, intentY, playerRadius)
                && Admit(snapshot, player, playerRadius, budget, intentX, intentY,
                         out float intentDurability, out float intentClearance,
                         out float _) == (int)AdmissionRule.Admitted)
            {
                decision.Action = DodgeAction.Move;
                decision.Reason = DodgeReason.IntentSafe;
                decision.HeadingRadians = player.IntentRadians;
                decision.SpeedScale = 1.0f;
                decision.Distance = budget;
                decision.CandidateIndex = -1;
                decision.DurabilitySeconds = intentDurability;
                decision.Clearance = intentClearance;
                _hysteresis.Take(_nowMs, player.IntentRadians, 0.0f);
                _previousHeading = player.IntentRadians;
                _hasPreviousHeading = true;
                return AttachStageClocks(decision);
            }

            // --- The intent ladder (section 5.7, steps 3 to 6). ----------------------------
            if (best < 0)
            {
                // NOTHING IS ADMISSIBLE, SO NOTHING IS EMITTED -- and the first version of this
                // branch got that wrong in a way worth recording. It picked the candidate that
                // maximised time-to-impact and emitted it, on the reasoning that a desperate move
                // beats no move. The reasoning is wrong, and it is wrong for the reason the whole
                // of section 5.3 rests on: **the admissible set is the safety boundary**, so a move
                // the admission rules refused is a move that ends inside a disc or through a wall.
                // Emitting it is not "the least bad move"; it is the solver deliberately walking
                // into a bullet it had already detected, on exactly the frames where the player is
                // in the most danger. It failed T6 on four frames of the dodge scenario, all of them
                // inside the incoming bullet's arrival window.
                //
                // The honest answer is what step 6 actually asks for, expressed the way the design
                // expresses it: report that no move is safe, keep the escape floor armed, and let
                // the gate below decide whether this is a nexus or a death. The player's own
                // steering is untouched -- which also means the manual dodge they own for the AoE
                // channel this design does not cover still works.
                decision.Reason = DodgeReason.NoAdmissible;
                decision.Action = DodgeAction.None;
                decision.UnavoidableThreats = CountUnavoidable(snapshot, player, playerRadius, budget, decision);
                _hysteresis.Reset();
                return AttachStageClocks(decision);
            }

            bool held = false;
            int chosen = best;

            // Hysteresis: hold the previous heading while it is still admitted and the new best
            // does not beat it by the margin. The held heading's own score is RE-DERIVED from
            // this frame's candidates rather than remembered -- the world has moved on since the
            // heading was taken, and a remembered score describes a frame that no longer exists.
            if (_hysteresis.Holding && _hasPreviousHeading)
            {
                int previous = FindHeading(_hysteresis.Heading);
                bool previousAdmissible = previous >= 0 && _results[previous].Admissible;
                float previousScore = previousAdmissible ? _results[previous].Score : float.NaN;
                if (_hysteresis.Keep(_nowMs, previousScore, previousAdmissible,
                                     _results[best].Score, _config.HysteresisMargin,
                                     _config.HysteresisDwellSeconds))
                {
                    chosen = previous;
                    held = true;
                }
                // Otherwise the hold is dropped by falling through to `best`: a reversal that
                // safety requires is never smoothed away.
            }

            decision.Held = held;
            decision.RunnerUpScore = RunnerUp(chosen);

            // A CHOSEN MOVE OF ZERO LENGTH IS A `stand` WEARING A `Move`'s LABEL, AND IT HALTS.
            // `Filled` derives `SpeedScale` from the candidate's distance, and `Publish` turns a
            // zero scale into a write of `Moving = false` -- so a zero-distance `Move` stops a
            // walking player exactly as `stand` did, while reporting a different reason and
            // escaping the halt counter. Measured after the first version of this section shipped:
            // 157 zero-scale `Move` frames over the nine inputs, 112 of them on a frame the player
            // was walking -- 0.95 % of the 11759 frames this section claims to have fixed. The size
            // is not the point: the point is that the instrument this whole section exists for would
            // have read "0 halts" while the dodge was still stopping the player.
            //
            // It is the same distinction `FirstAdmissibleMove` draws, for the same reason: a
            // candidate is a direction the character WALKS IN, and one that walks nowhere is not a
            // move. So it becomes what it actually is -- the walk-on, which emits nothing at all
            // and leaves the player's own hands in charge.
            if (_results[chosen].Distance <= 1e-6f && playerWalking)
            {
                decision.Reason = DodgeReason.StandWalkOn;
                decision.Action = DodgeAction.None;
                decision.CandidateIndex = -1;
                decision.HeadingRadians = player.IntentRadians;
                decision.SpeedScale = 0.0f;
                decision.DurabilitySeconds = _results[chosen].DurabilitySeconds;
                decision.Clearance = _results[chosen].Clearance;
                return AttachStageClocks(decision);
            }

            decision = Filled(decision, chosen, DodgeAction.Move,
                 held ? DodgeReason.Hysteresis : DodgeReason.OverrideUnsafe, budget, player);

            if (!held)
            {
                _hysteresis.Take(_nowMs, _results[chosen].HeadingRadians, _results[chosen].Score);
            }
            _previousHeading = _results[chosen].HeadingRadians;
            _hasPreviousHeading = true;

            // --- Stage 7: the escape gate, as gate 6 of this same decision. ----------------
            decision.UnavoidableThreats = CountUnavoidable(snapshot, player, playerRadius, budget, decision);
            StageMark(StageEscape);
            if (decision.UnavoidableThreats > 0
                && decision.ProjectedUnavoidableDamage > player.Health * _config.LethalFraction
                && _config.EscapeBudgetPerWorld > 0)
            {
                DodgeDecision escape = decision;
                escape.Action = DodgeAction.Escape;
                escape.Reason = DodgeReason.EscapeLethalUnavoidable;
            return AttachStageClocks(escape);
            }

            return AttachStageClocks(decision);
        }

        /// <summary>
        /// Completes a decision from a candidate, and RETURNS it rather than writing through a
        /// by-value parameter.
        ///
        /// THE BY-VALUE PARAMETER IS THE BUG, AND IT COST AN AFTERNOON. `Fill(decision, ...)` takes
        /// the struct by value; whether the writes are visible to the caller is then a question about
        /// how the JIT chose to keep the struct, and the measured answer was that SOME writes
        /// landed and some did not -- `Reason` and `Action` reverted to the caller's defaults while a
        /// flag set by the caller itself, immediately after the call, survived. A function whose
        /// effect depends on which of its writes the JIT keeps is not a function. Returning the
        /// value makes the data flow explicit and is unmissable in review.
        /// </summary>
        private DodgeDecision Filled(DodgeDecision decision, int index, DodgeAction action,
                                     DodgeReason reason, float budget, PlayerView player)
        {
            CandidateResult r = _results[index];
            decision.Action = action;
            decision.Reason = reason;
            decision.HeadingRadians = r.HeadingRadians;
            decision.SpeedScale = budget > 0.0f ? r.Distance / budget : 0.0f;
            if (decision.SpeedScale > 1.0f)
            {
                decision.SpeedScale = 1.0f;
            }
            decision.DurabilitySeconds = r.DurabilitySeconds;
            decision.Distance = r.Distance;
            decision.CandidateIndex = index;
            decision.Score = r.Score;
            decision.Clearance = r.Clearance;
            if (action == DodgeAction.None)
            {
                decision.HeadingRadians = player.IntentRadians;
                decision.SpeedScale = 0.0f;
            }
            return decision;
        }

        // ===================================================================================
        // Stage 1 -- the instantaneous floor (section 5.1)
        // ===================================================================================

        /// <summary>
        /// `dangerous(bullet, point) <=> (point - bullet.position)^2 < r_eff^2`, with
        /// `r_eff = player.Radius + bullet.Radius` and nothing added twice.
        /// </summary>
        private static bool InsideDisc(float pointX, float pointY, float playerRadius,
                                       Trajectory threat, float x, float y)
        {
            float dx = x - pointX;
            float dy = y - pointY;
            float r = playerRadius + threat.Radius;
            return (dx * dx) + (dy * dy) < (r * r);
        }

        private static bool InsideAnyDisc(DodgeSnapshot snapshot, float x, float y, float playerRadius)
        {
            for (int i = 0; i < snapshot.Threats.Count; i++)
            {
                Trajectory t = snapshot.Threats[i];
                if (t.Count > 0 && InsideDisc(x, y, playerRadius, t, t.Xs[0], t.Ys[0]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Whether any live threat's predicted path comes within `pad` of the player's disc inside
        /// the horizon.
        ///
        /// This is the cheap pre-test that turns "there are 40 bullets on the screen" into "there
        /// are two bullets that matter", which is section 8's first mitigation (cull before
        /// sampling) applied to the decision rather than to the sampler. It uses the WARNING
        /// radius and not the collision radius -- see `WarningRadiusPad` on why the two must stay
        /// apart.
        /// </summary>
        private bool AnyThreatNearPlayer(DodgeSnapshot snapshot, float px, float py,
                                         float playerRadius, float pad, float budget)
        {
            for (int i = 0; i < snapshot.Threats.Count; i++)
            {
                Trajectory t = snapshot.Threats[i];
                if (t.Count <= 0)
                {
                    continue;
                }
                float r = playerRadius + t.Radius + pad;
                float r2 = r * r;
                int trusted = t.TrustedCount(_config.TrustSeconds);
                for (int s = 0; s < trusted; s++)
                {
                    float dx = t.Xs[s] - px;
                    float dy = t.Ys[s] - py;
                    if ((dx * dx) + (dy * dy) <= r2)
                    {
                        return true;
                    }
                }
            }

            // AND THE ENEMY BODIES, OR RULE 7 NEVER RUNS ON A FRAME WITH NO BULLET IN IT.
            //
            // This gate is the first thing `DecideCore` does, and a frame that returns here never
            // builds a candidate set -- so nothing is tested, nothing is refused and the planner is
            // never asked for a path. Without the loop below, the whole body feature would be
            // invisible on exactly the frames `PLAN-ENEMY-THREATS.md` exists for: a ranged mob
            // standing in the open between the player and where they are going, not currently
            // shooting. The operator's sentence is "pathing over an enemy might look safe until it
            // spawns projectiles", and those are the frames it describes.
            //
            // IT IS NOT THE EMITTER BECOMING A GATE. What this decides is whether the frame is
            // EVALUATED, not what the evaluation may do: a body still cannot admit or move anything,
            // so the only hard word it has is rule 7's refusal against its own unpadded radius.
            //
            // THE REACH IS THE BODY'S COLLISION RADIUS PLUS ONE FRAME'S STEP, AND NOT THE PAD. A body
            // is interesting to rule 7 when a candidate of THIS FRAME could sweep into it -- the
            // collision radius alone would miss every body in the one window where the refusal
            // matters (`body.Radius + player.Radius < distance <= that + budget`), which is exactly
            // the case `bodyclose` builds. The step is `budget`, which is the same quantity the
            // candidate ring is scaled by, so the gate and the ring cannot disagree about how far
            // the player can go.
            //
            // IT IS WHAT MAKES THE PAD SWEEPABLE. E4 asserts that `EnemyDangerPadUnits` has no effect
            // on admission, and a gate that opened only for a positive pad would break that claim in
            // the most confusing possible way: every candidate would read "not admissible" at pad 0
            // because the frame was never evaluated, which is indistinguishable from the emitter
            // refusing everything. Rule 7's reach does not move when the pad does.
            //
            // AND IT IS ONLY EVER TRUE FOR A BODY THAT IS THERE. `EnemyBodies=0` leaves the list
            // empty, so this loop cannot open a frame that today's gate would have closed -- which is
            // E9, the requirement that switching the module off changes nothing.
            float stepReach = budget;
            for (int b = 0; b < snapshot.Bodies.Count; b++)
            {
                if (snapshot.Player.Dead || snapshot.Player.SafeWorld)
                {
                    break;      // the two guards this gate sits behind; see `DecideCore`
                }
                EntityBody body = snapshot.Bodies[b];
                float reach = body.Radius + playerRadius + stepReach;
                float dx = body.X - px;
                float dy = body.Y - py;
                if ((dx * dx) + (dy * dy) <= reach * reach)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The minimum squared distance from a point to a threat's predicted polyline, over the
        /// EVEN samples and nothing in between. The caller's threshold turns this into "inside the
        /// collision disc" or "near the path".
        ///
        /// WHY SAMPLES AND NOT SEGMENTS, IN THE PLACE WHERE THE ANSWER DECIDES WHETHER THE PLAYER
        /// STANDS STILL. Section 5.4's durability test is "does anything threaten this position for
        /// the whole horizon", and the honest reading of a sampled polyline is the samples. A
        /// point-to-segment version would also fire for a bullet whose 600 ms path merely crosses
        /// near the player at some moment -- which is the warning question, answered by
        /// `WarningRadiusPad`, and NOT the collision question, which is whether the bullet's
        /// centre is ever close enough to touch. Using the collision radius on the samples is the
        /// strict test; using it on the segments would be the loose one, and loose here means
        /// standing still inside a bullet's path.
        /// </summary>
        /// <summary>
        /// Section 5.4's durability, applied to any fixed point: is it clear of every threat's
        /// predicted path, at the game's own collision radius, for the whole horizon?
        ///
        /// IT IS THE SAME TEST FOR `stand` AND FOR THE INTENT EARLY-OUT, and it is also the test
        /// the candidate `stand` is admitted by -- see <see cref="ThreatThreatensFixedPoint"/>.
        /// That is not an accident of code sharing but the point of it: two implementations of
        /// "is this position safe" that are *nearly* the same produce a solver that stands when it
        /// should move and moves when it should stand, and the failure is invisible in the
        /// aggregate numbers because both halves look reasonable in isolation.
        /// </summary>
        private bool StaticallyDurable(DodgeSnapshot snapshot, PlayerView player, float x, float y,
                                       float playerRadius)
        {
            for (int i = 0; i < snapshot.Threats.Count; i++)
            {
                Trajectory t = snapshot.Threats[i];
                if (t.Count <= 0)
                {
                    continue;
                }
                float r = playerRadius + t.Radius;
                if (ThreatThreatensFixedPoint(t, x, y, r * r, out float _, out float _))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Whether a threat's predicted path ever comes within `r2` of a FIXED point, and when.
        ///
        /// THE SAMPLES, NOT THE SEGMENTS, AND THAT IS THE STRICT READING ON PURPOSE. This answers
        /// "does the bullet's centre knowably land where I am", which is the question the
        /// candidate `stand` and the intent early-out both turn on. It is deliberately *not* the
        /// segment version: a path that merely crosses near the point at some moment is the
        /// warning question, and answering it here would refuse `stand` for bullets that never
        /// touch the player -- the over-caution the operator sees as a dodge that flinches.
        ///
        /// The plan model lives in <see cref="PlanRefusal"/>, where the candidate is moving and
        /// "where will the bullet be when I am there" is the actual question.
        /// </summary>
        private bool ThreatThreatensFixedPoint(Trajectory threat, float x, float y, float r2,
                                               out float firstMs, out float closestD2)
        {
            firstMs = float.MaxValue;
            closestD2 = float.MaxValue;
            float stepMs = threat.StepMs > 0.0f ? threat.StepMs : _config.SampleStepMs;
            int trusted = threat.TrustedCount(_config.TrustSeconds);
            for (int s = 0; s < trusted && s < threat.Count; s++)
            {
                float dx = threat.Xs[s] - x;
                float dy = threat.Ys[s] - y;
                float d2 = (dx * dx) + (dy * dy);
                if (d2 < closestD2)
                {
                    closestD2 = d2;
                }
                if (d2 < r2 && firstMs == float.MaxValue)
                {
                    firstMs = s * stepMs;
                }
            }
            return firstMs != float.MaxValue;
        }

        /// <summary>
        /// Checks the threats nearest the fixed point first, in a small fixed window, and stops as
        /// soon as one of them comes within the collision radius.
        ///
        /// WHY A GUARD AND NOT A LOOP OVER EVERY THREAT, WHEN THE FIXED POINT IS THE PLAYER'S OWN
        /// POSITION AND THE ANSWER IS THEREFORE STATIC. Because the pre-test that decides whether
        /// to evaluate a frame at all (<see cref="AnyThreatNearPlayer"/>) already uses the WARNING
        /// radius, and the durability test uses the COLLISION radius -- a strictly smaller
        /// corridor. A threat that fails the pre-test is one the decision has already, correctly,
        /// declined to act on; re-admitting it here through a wider radius would make `stand`
        /// unsafe for bullets the solver is not allowed to be afraid of, and the two predicates
        /// would disagree on exactly the frames where the guard was tighter than the collision
        /// test. That disagreement is measured: it produced a "T5 stand is never rejected while
        /// durably safe" failure on five frames of the passing-by scenario, where the bullets are
        /// 1.5 units away and the solver moved anyway.
        /// </summary>
        private bool AnyThreatThreatensPoint(DodgeSnapshot snapshot, float x, float y,
                                             float playerRadius)
        {
            for (int i = 0; i < snapshot.Threats.Count; i++)
            {
                Trajectory t = snapshot.Threats[i];
                if (t.Count <= 0)
                {
                    continue;
                }
                float r = playerRadius + t.Radius;
                if (ThreatThreatensFixedPoint(t, x, y, r * r, out float _, out float _))
                {
                    return true;
                }
            }
            return false;
        }

        // `FindIntent` STOOD HERE, AND IT IS DELETED RATHER THAN KEPT FOR THE HARNESS. It scanned
        // the candidate array for a `Kind` of 3, and nothing had called it since the intent
        // early-out stopped reading `_results` -- which is the fix recorded in `Decide`, where a
        // defaulted `Admissible` field silently disabled that branch. A lookup that no longer has a
        // caller is a lookup the next reader will assume has one.

        // ===================================================================================
        // Stage 2 and 3 -- the candidate set and admission
        // ===================================================================================

        private void Add(int kind, float heading, float distance)
        {
            if (_resultCount >= _results.Length || distance <= 0.0f)
            {
                return;
            }
            CandidateResult r = default(CandidateResult);
            r.Kind = kind;
            r.HeadingRadians = heading;
            r.Distance = distance;
            _results[_resultCount] = r;
            _resultCount++;
        }

        private void BuildAndAdmit(DodgeSnapshot snapshot, PlayerView player, float playerRadius, float budget)
        {
            // `stand` first, so its index is always 0 and `FindStand` is a scan that cannot be
            // confused by a ring radius of zero.
            //
            // IT IS WRITTEN DIRECTLY RATHER THAN THROUGH `Add`, AND THAT IS NOT A STYLE CHOICE.
            // `Add` refuses a non-positive distance -- which is right for every other candidate and
            // wrong for this one, because `stand`'s distance IS zero by definition. The first
            // version called `Add(1, heading, 0.0f)` and then patched up `_results[0]` by hand; the
            // call did nothing at all, so `_results[0]` was a defaulted struct whose `Kind` was 0,
            // `FindStand` scanned for a `Kind` of 1 and never found one, and the whole stand branch
            // -- the specification's preferred answer, "the best dodge is usually no dodge" -- was
            // dead code. The observable symptom was a dodge that moved on frames where it was
            // provably safe to stand still, in a scenario where the bullets were 1.5 units away.
            CandidateResult stand = default(CandidateResult);
            stand.Kind = 1;
            stand.HeadingRadians = player.IntentRadians;
            stand.Distance = 0.0f;
            stand.X = player.X;
            stand.Y = player.Y;
            _results[0] = stand;
            _resultCount = 1;

            // The ring. Radii are named and kept distinct on purpose: a configuration that sets
            // two of them equal would otherwise evaluate the same point three times and report a
            // candidate count the report cannot explain.
            float[] radii = { _config.RadiusNear, _config.RadiusMid, _config.RadiusFar };
            for (int ri = 0; ri < radii.Length; ri++)
            {
                float distance = radii[ri] * budget;
                if (distance <= 0.0f)
                {
                    continue;
                }
                bool duplicate = false;
                for (int rj = 0; rj < ri; rj++)
                {
                    if (Math.Abs(radii[rj] - radii[ri]) < 1e-6f)
                    {
                        duplicate = true;
                    }
                }
                if (duplicate)
                {
                    continue;
                }
                for (int h = 0; h < _config.Headings; h++)
                {
                    float heading = (float)((2.0 * Math.PI * h) / _config.Headings);
                    Add(2, heading, distance);
                }
            }

            // The intent point at full magnitude: "keep doing what the player asked" is a
            // direction the 15-degree ring quantises away, and it is frequently the right answer.
            //
            // AND IT IS ONLY ADDED WHEN THERE IS AN INTENT. See `PlayerView.IntentValid`: with the
            // accessor unresolved this heading is 0.0 radians, which is a ring heading like any
            // other, so an ungated candidate would be a duplicate of east with a bogus label.
            if (player.IntentValid)
            {
                Add(3, player.IntentRadians, budget);
            }

            // THE GOAL FAN: THE IMAGINARY POINT IN FRONT OF THE CHARACTER, AND THE SMALL DEVIATIONS
            // FROM IT. This is the candidate set the operator asked for -- "creating an imaginary
            // point in front of the character and trying to pathfind to it while avoiding any
            // projectiles in the way" -- expressed in the one-step form the actuator can execute.
            //
            // THE EXACT HEADING MATTERS AS MUCH AS THE FAN. The ring's headings are k*15 degrees;
            // the course is whatever angle the character is actually travelling at, and the
            // operator's own example is 13.7 degrees. A set that can only offer 15-degree multiples
            // of world north is a dodge that cannot carry on in the direction it is already going,
            // which is the whole behaviour being asked for.
            //
            // The fan's radii are full magnitude and nothing else: a fan at 0.35 b would be a third
            // of a step toward the goal, and the goal term already scores distance separately.
            Add(5, _goalRadians, budget);
            int fan = _config.GoalFanSteps < 0 ? 0 : _config.GoalFanSteps;
            if (fan > 0 && _config.Headings > 0)
            {
                float step = (float)((2.0 * Math.PI) / _config.Headings);
                for (int j = 1; j <= fan; j++)
                {
                    Add(5, _goalRadians + (j * step), budget);
                    Add(5, _goalRadians - (j * step), budget);
                }
            }

            // The egress point, and ONLY when we are already inside a disc: "get out of the thing
            // I am standing in" has a direction that the ring does not contain, and being inside
            // a disc makes it the only candidate that is certainly an improvement.
            if (InsideAnyDisc(snapshot, player.X, player.Y, playerRadius))
            {
                if (TryEgressDirection(snapshot, player, playerRadius, out float ex, out float ey))
                {
                    float dx = ex - player.X;
                    float dy = ey - player.Y;
                    float distance = (float)Math.Sqrt((dx * dx) + (dy * dy));
                    if (distance > 1e-6f)
                    {
                        Add(4, (float)Math.Atan2(dy, dx), budget);
                    }
                }
            }

            // Admission, and the two phases inside it: geometry first (cheap, and it refuses most
            // candidates before any bullet is looked at), then the bullet tests.
            //
            // THE PER-FRAME ENVELOPE IS BUILT HERE, ONCE, BEFORE THE FIRST CANDIDATE IS ADMITTED.
            // Everything in it is a property of the threat set and not of a candidate: the trust
            // prefix, and the box the threat's own samples occupy. `Admit` reads them by threat
            // index. See the fields' comment for why this exists and why it cannot change a verdict.
            BuildAdmitEnvelope(snapshot);

            // THE CLASSIFICATION COMES AFTER THE ENVELOPE AND BEFORE THE FIRST CANDIDATE, because
            // it reads the trust prefix the envelope just computed and because every candidate's
            // verdict depends on it. See `BuildThreatVerdicts`.
            BuildThreatVerdicts(snapshot, player, playerRadius, budget);
            BuildDecisionWindows(snapshot, playerRadius);

            for (int i = 0; i < _resultCount; i++)
            {
                CandidateResult r = _results[i];
                float cx = player.X + (r.Distance * (float)Math.Cos(r.HeadingRadians));
                float cy = player.Y + (r.Distance * (float)Math.Sin(r.HeadingRadians));
                r.X = cx;
                r.Y = cy;

                int refused = Admit(snapshot, player, playerRadius, budget, cx, cy, out float durability,
                                    out float clearance, out float resolve);
                r.Admissible = refused == (int)AdmissionRule.Admitted;
                r.RefusedBy = refused;
                r.DurabilitySeconds = durability;
                r.Clearance = clearance;
                r.Resolve = resolve;
                _results[i] = r;
            }
        }

        /// <summary>
        /// Fills the per-frame threat envelope: `TrustedCount` and the sample bounding box for
        /// every live threat, so the candidate loop does not recompute either one 74 times.
        /// </summary>
        private void BuildAdmitEnvelope(DodgeSnapshot snapshot)
        {
            int n = snapshot.Threats.Count;
            if (_admitTrusted.Length < n)
            {
                _admitTrusted = new int[n];
                _admitMinX = new float[n];
                _admitMaxX = new float[n];
                _admitMinY = new float[n];
                _admitMaxY = new float[n];
            }
            _admitCount = n;

            for (int t = 0; t < n; t++)
            {
                Trajectory threat = snapshot.Threats[t];
                if (threat.Count <= 0)
                {
                    // An empty slot is not a threat: mark the envelope empty so every candidate
                    // rejects it at the bounding-box test, exactly as the `Count <= 0` guard did.
                    _admitTrusted[t] = 0;
                    _admitMinX[t] = float.MaxValue;
                    _admitMaxX[t] = float.MinValue;
                    _admitMinY[t] = float.MaxValue;
                    _admitMaxY[t] = float.MinValue;
                    continue;
                }

                int trusted = threat.TrustedCount(_config.TrustSeconds);
                _admitTrusted[t] = trusted;

                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                for (int k = 0; k < threat.Count; k++)
                {
                    float x = threat.Xs[k];
                    float y = threat.Ys[k];
                    if (x < minX) { minX = x; }
                    if (x > maxX) { maxX = x; }
                    if (y < minY) { minY = y; }
                    if (y > maxY) { maxY = y; }
                }
                _admitMinX[t] = minX;
                _admitMaxX[t] = maxX;
                _admitMinY[t] = minY;
                _admitMaxY[t] = maxY;
            }
        }

        /// <summary>
        /// Classifies every threat for this frame: is there any point testing candidates against it?
        ///
        /// IT RUNS ONCE PER FRAME, BEFORE THE CANDIDATE RING EXISTS, and that ordering is the whole
        /// design. The three hard rules answer "is this candidate safe" and they answer it per
        /// candidate per threat; the question here is about the threat alone, and asking it 74 times
        /// would be both wasteful and -- more importantly -- unanswerable, because at candidate time
        /// the threat has already been treated as avoidable.
        ///
        /// THE THREE TESTS, IN ORDER, EACH CHEAPER THAN THE NEXT:
        ///
        ///   1. is the disc already on the player            -> `Inside`
        ///   2. does the path ever enter the disc at all     -> `Clear` if not
        ///   3. can the player walk out of the corridor in time -> `TooFast` if not
        ///
        /// Step 2 is the player's CURRENT position against the threat's whole predicted path, so it
        /// is the same bodies-in-spacetime question `PlanWalk` asks with a zero velocity -- not the
        /// sample-at-a-knot version `StaticallyDurable` asks, which is deliberately the strict
        /// reading for the two fixed-point gates and would classify a bullet whose body crosses the
        /// disc between two knots as invisible.
        /// </summary>
        private void BuildThreatVerdicts(DodgeSnapshot snapshot, PlayerView player,
                                         float playerRadius, float budget)
        {
            int n = snapshot.Threats.Count;
            if (_verdict.Length < n)
            {
                _verdict = new int[n];
                _verdictPath2 = new float[n];
            }
            _verdictCount = n;
            _verdictRelevant = 0;

            // `budget` is the distance the player may cover this frame, so the speed is that over
            // the frame's own delta -- the same quantity the snapshot carries, re-derived here
            // because `budget` is what the caller actually sized the ring with.
            float playerSpeed = _frameSeconds > 0.0f ? budget / _frameSeconds : 0.0f;

            for (int t = 0; t < n; t++)
            {
                Trajectory threat = snapshot.Threats[t];
                if (threat.Count <= 0)
                {
                    _verdict[t] = (int)ThreatVerdict.Clear;
                    _verdictPath2[t] = float.MaxValue;
                    continue;
                }

                float collisionR = playerRadius + threat.Radius;
                float collisionR2 = collisionR * collisionR;

                int trusted = t < _admitCount ? _admitTrusted[t]
                                              : threat.TrustedCount(_config.TrustSeconds);

                // THE PLAYER PARKED WHERE THEY ARE, WALKED IN SPACETIME. `vxPerMs`/`vyPerMs` are
                // zero, so this is the same question the fixed-point durability test asks -- "does
                // this bullet's BODY ever come within the collision radius of mine, at the time the
                // two of us are there" -- but over the whole sampled interval rather than at the
                // knots, which is why it catches the bullet that crosses the disc between two of
                // them. `pathMs` is now the time of the closest approach rather than the time of the
                // closest knot, and it is what the `TooFast` verdict compares against the time the
                // player would need to clear the corridor.
                PlanWalk(threat, player.X, player.Y, 0.0f, 0.0f, collisionR2, trusted,
                         _config.TrustSeconds * 1000.0f,
                         out float path2, out float pathMs, out float _);
                _verdictPath2[t] = path2;

                float dx = threat.Xs[0] - player.X;
                float dy = threat.Ys[0] - player.Y;
                if ((dx * dx) + (dy * dy) < collisionR2)
                {
                    _verdict[t] = (int)ThreatVerdict.Inside;
                    _verdictRelevant++;
                    continue;
                }

                if (path2 >= collisionR2)
                {
                    _verdict[t] = (int)ThreatVerdict.Clear;
                    continue;
                }

                // CAN THE PLAYER BE OUT OF THE WAY IN TIME? The lateral distance that has to be
                // covered is `collisionR - miss`, and the time available is the moment of closest
                // approach. A player who is slower than that is not failing to dodge; they are
                // being shot by something the dodge cannot answer, and pretending otherwise makes
                // the frame's decision about a bullet that is not a decision.
                float miss = (float)Math.Sqrt(path2);
                float need = collisionR - miss;
                float escapeMs = playerSpeed > 0.01f ? (need / playerSpeed) * 1000.0f : float.MaxValue;

                _verdict[t] = pathMs < escapeMs ? (int)ThreatVerdict.TooFast
                                                : (int)ThreatVerdict.Dodgeable;
                _verdictRelevant++;
            }
        }

        // `PathDistanceSquared` STOOD HERE -- the distance from a fixed point to the threat's
        // whole predicted polyline -- and it is deleted rather than left unused, because it is
        // the exact shape of the defect this work exists to remove. A point-to-polyline distance
        // has no clock in it: it says a bullet's path is a wall whether the bullet is there now,
        // in 600 ms, or was there 600 ms ago. `PlanWalk` with a zero velocity is the same question
        // asked in spacetime, and it lives in one place.

        /// <summary>The verdict of threat <paramref name="index"/> for the frame just decided.</summary>
        public ThreatVerdict VerdictOf(int index)
        {
            if (index < 0 || index >= _verdictCount)
            {
                return ThreatVerdict.Clear;
            }
            return (ThreatVerdict)_verdict[index];
        }

        /// <summary>How many threats this frame were not `Clear` -- the ones the decision is about.</summary>
        public int RelevantThreats => _verdictRelevant;

        /// <summary>How far the player's current position is from threat <paramref name="index"/>'s path.</summary>
        public float PathDistanceOf(int index)
        {
            if (index < 0 || index >= _verdictCount)
            {
                return float.MaxValue;
            }
            return (float)Math.Sqrt(_verdictPath2[index]);
        }

        /// <summary>
        /// Section 5.3, in order. Returns <see cref="AdmissionRule.Admitted"/> or the first rule
        /// that refused. Never relaxed, and never reordered: the cheap tests are first, and the
        /// temporal test -- which is the expensive one -- only runs for candidates that survived
        /// everything else.
        /// </summary>
        private int Admit(DodgeSnapshot snapshot, PlayerView player, float playerRadius, float budget,
                          float cx, float cy, out float durability, out float clearance,
                          out float resolve)
        {
            durability = _config.MoveTemporalWindowSeconds;
            clearance = float.MaxValue;
            resolve = 0.0f;
            int relevantThreats = 0;
            int resolvedThreats = 0;

            // THE PLAN'S VELOCITY, IN WORLD UNITS PER MILLISECOND, DERIVED FROM THE CANDIDATE
            // ITSELF AND NOT FROM THE CONFIGURATION. The candidate's displacement over one frame is
            // `(cx, cy) - player`, and a candidate is a direction carried for the whole lookahead,
            // so that displacement per frame IS the plan's speed. Radius 0.35 means "walk at a
            // third of the pace", which is what `SpeedScale` writes and therefore what the client
            // does; `stand` is the zero vector and the plan is the player parked where they are.
            float planDx = cx - player.X;
            float planDy = cy - player.Y;
            float frameMs = _frameSeconds * 1000.0f;
            float vxPerMs = 0.0f;
            float vyPerMs = 0.0f;
            if (frameMs > 0.0001f)
            {
                vxPerMs = planDx / frameMs;
                vyPerMs = planDy / frameMs;
            }

            CollisionWindow grid = snapshot.Grid;

            // Rule 1: in bounds. The map's own bounds ARE the grid's bounds, which is why an
            // absent grid makes this rule vacuous rather than false.
            if (grid.Present && !grid.InWindow(cx, cy))
            {
                return (int)AdmissionRule.InBounds;
            }

            // Rule 2: walkable. A blocked destination is a move the client's own integrator will
            // refuse, which presents in the live run as the player standing still while a bullet
            // arrives (PHASE3-HANDOFF.md section 3.3: three of eight sweep phases ended pinned
            // against scenery, with the command landing and the read-back matching).
            if (!grid.Walkable(cx, cy))
            {
                return (int)AdmissionRule.Walkable;
            }

            BuildSweep(player, cx, cy);

            // Rule 3: path clear -- the swept segment does not cross a blocker. Endpoint-only
            // checks let a candidate through a thin wall, and a step here is a meaningful
            // fraction of a tile.
            if (grid.Present)
            {
                for (int s = 0; s < _sweepCount; s++)
                {
                    if (!grid.Walkable(_sweepX[s], _sweepY[s]))
                    {
                        return (int)AdmissionRule.PathClear;
                    }
                }
            }

            // Rules 4 and 5, and the temporal test -- one pass over the threats, because all
            // three are cheaper together than three separate loops over the same set.
            bool insideAtArrival = false;
            bool insideAlongTheWay = false;
            bool temporallyClear = true;
            int temporalRule = (int)AdmissionRule.TemporallyClear;
            // Whether THIS CANDIDATE has already been refused by rule 6's unconditional clause. It
            // is per candidate, not per (candidate, threat), because the counter is reported against
            // a total that is also per candidate -- see `_windowRefusals`.
            bool refusedByWindow = false;
            float firstDangerMs = float.MaxValue;

            for (int t = 0; t < snapshot.Threats.Count; t++)
            {
                Trajectory threat = snapshot.Threats[t];
                if (threat.Count <= 0)
                {
                    continue;
                }

                // TWO RADII, AND KEEPING THEM APART IS THE WHOLE OF THIS PARAGRAPH.
                //
                //   `collisionR`  player.Radius + bullet.Radius -- the game's own hit test, the
                //                 number that decides whether a bullet actually touches the player.
                //                 Rules 4, 5 and 6 turn on THIS and nothing else.
                //   `warningR`    `collisionR` plus `WarningRadiusPad` -- the radius the CHEAP
                //                 pre-test uses to decide whether to evaluate anything at all.
                //
                // THE FIRST VERSION USED THE WARNING RADIUS FOR BOTH, AND THE RESULT WAS A DODGE
                // THAT COULD NOT EXIST. A warning is about the path: "this bullet's predicted path
                // comes near the line I am on". A collision is about the arrival: "this bullet
                // touches me". Making the collision test the stricter of the two means the solver
                // is asked to avoid something it cannot avoid -- a bullet whose 600 ms path
                // crosses a line 0.65 units away but whose actual nearest approach is 0.55 -- and
                // the honest answer to that is paralysis: every candidate is refused, including
                // `stand`, and the player dies standing still without the dodge ever moving.
                //
                // The pad is also what keeps the temporal test from aliasing. Sampling every 30 ms
                // means a 24 unit/s bullet jumps 0.72 units between samples, so a path can cross
                // the disc between two knots without either knot being inside it. The warning
                // radius is what makes the pre-test catch that case; the collision radius is what
                // makes the verdict honest.
                float collisionR = playerRadius + threat.Radius;
                float collisionR2 = collisionR * collisionR;
                float warningR = collisionR + _config.WarningRadiusPad;
                float warningR2 = (warningR * warningR) + _config.SweepPadSquared;

                // THE PRE-FILTER: if the threat's whole predicted path stays further away than
                // the sweep's own extent plus `warningR`, no test below can fail. One bounding-box
                // comparison per threat replaces thousands of distance tests -- section 8's
                // "early-out bounding-box rejection per bullet before any segment test".
                //
                // THE BOX AND THE TRUST PREFIX COME FROM THE ENVELOPE BUILT ONCE PER FRAME. They
                // are the same numbers this call used to recompute for every candidate; the call
                // now takes them as arguments instead of walking the threat's samples again.
                int trusted = t < _admitCount ? _admitTrusted[t] : threat.TrustedCount(_config.TrustSeconds);
                if (ThreatBoundingBoxFar(_sweepX, _sweepY, _sweepCount, warningR,
                                         _admitMinX[t], _admitMaxX[t], _admitMinY[t], _admitMaxY[t]))
                {
                    continue;
                }

                // THE ALREADY-INSIDE EXCEPTION, WHICH APPLIES TO BULLETS AND NOT ONLY TO ENEMY
                // BODIES, AND WHICH THE FIRST VERSION OF THIS FILE GOT WRONG.
                //
                // Rules 4 and 5 refuse a candidate that puts the player inside a disc or drags
                // them through one. But a player who is ALREADY inside a disc satisfies both of
                // those conditions at the moment they start moving: the sweep begins inside the
                // volume, so every candidate -- including the one that leaves it -- is refused.
                // The measured consequence is total paralysis exactly when the decision matters
                // most.
                //
                // The rule is therefore conditional, in the same shape as rule 7's documented
                // exception for enemy bodies: **a threat the player is already inside is not
                // grounds for refusal, unless the candidate does not increase the distance to its
                // centre.** Stepping further in is refused; stepping out is the one move that is
                // certainly an improvement, and it must be admitted or the dodge dies where it
                // stands.
                bool alreadyInside = InsideCircle(threat.Xs[0], threat.Ys[0], collisionR2,
                                                 player.X, player.Y);

                // RULE 4 -- AND THE CENTRE HERE IS THE BULLET'S, WHICH IT WAS NOT.
                //
                // This line read `InsideCircle(0.0f, 0.0f, collisionR2, cx, cy)` until 2026-10-15,
                // and `InsideCircle(cx0, cy0, r2, x, y)` computes `(x-cx0)^2 + (y-cy0)^2 < r2`. With
                // `(0, 0)` as the centre it was testing **the candidate's distance from the WORLD
                // ORIGIN** against a 0.41-unit radius, on a map whose coordinates are in the
                // thousands. The test could never be true, so `insideAtArrival` was never set and
                // rule 4 was dead code.
                //
                // It was harmless for as long as it existed, and that is the interesting part:
                // rule 5 below refuses a candidate any of whose swept points is inside the same
                // disc, and the arrival point IS the last swept point -- so rule 5 silently did rule
                // 4's job for every player who was not already inside a disc. It stopped being
                // harmless the moment the already-inside exception was extended to rule 6, because
                // that left an already-inside threat with NO rule that checked where the move ended:
                // measured on `20261015-021911-p20152`, **585 emitted moves finished inside a disc
                // the player was not in, and 585 of the moves that did not escape a disc they were
                // already in had not even improved the distance.** T6 is what found it.
                if (InsideCircle(threat.Xs[0], threat.Ys[0], collisionR2, cx, cy))
                {
                    if (!alreadyInside)
                    {
                        insideAtArrival = true;
                    }
                    else
                    {
                        float nowDx = threat.Xs[0] - player.X;
                        float nowDy = threat.Ys[0] - player.Y;
                        float nowD2 = (nowDx * nowDx) + (nowDy * nowDy);
                        float thenDx = threat.Xs[0] - cx;
                        float thenDy = threat.Ys[0] - cy;
                        float thenD2 = (thenDx * thenDx) + (thenDy * thenDy);
                        if (thenD2 <= nowD2)
                        {
                            insideAtArrival = true;
                        }
                    }
                }

                // Rule 5: along the way against the bullet's CURRENT disc -- the disc that exists
                // at the only time those swept points exist. Skipped when the player is already
                // inside that disc, for the reason above: every point of a path leaving a disc is
                // closer to the exit than the point they started from.
                if (!insideAlongTheWay && !alreadyInside)
                {
                    for (int s = 0; s < _sweepCount; s++)
                    {
                        if (InsideCircle(threat.Xs[0], threat.Ys[0], collisionR2,
                                         _sweepX[s], _sweepY[s]))
                        {
                            insideAlongTheWay = true;
                            break;
                        }
                    }
                }

                // Rule 6: THE PLAN TEST, AND IT IS NO LONGER A PATH-AGAINST-PATH TEST.
                //
                // WHAT IT USED TO ASK, AND WHY THAT WAS WRONG. The old rule compared the candidate's
                // one-frame swept segment against the bullet's predicted POLYLINE, and then -- for
                // the rest of the horizon -- measured the distance from the candidate's arrival
                // point to that polyline. Both halves treat the bullet's whole remaining path as a
                // wall, whether the bullet is there in 600 ms or was there 600 ms ago, and the
                // operator's own words for the result are the reason this work exists: "it considers
                // a projectile's entire path dangerous, when the only dangerous part is the
                // projectile".
                //
                // WHAT IT ASKS NOW. A candidate is a DIRECTION THE CHARACTER WALKS IN, and the only
                // question is where the bullet's body is when the character's body is there. Two
                // bodies, one clock. `PlanWalk` walks the plan and the threat together over the
                // lookahead and returns the closest the two bodies EVER come, the time of that
                // approach, and the exact time the plan first enters the bullet -- all of it from a
                // relative-motion segment, which is exact because both bodies are linear in time
                // between two sample knots.
                //
                // THE REFUSAL IS ONE-SIDED, EXACTLY AS SECTION 5.3's DOCTRINE REQUIRES, and the two
                // clauses are the old rule's two clauses translated.
                int refusal = PlanRefusal(threat, player.X, player.Y, vxPerMs, vyPerMs, collisionR2,
                                          trusted, t, out float nearestD2,
                                out float nearestMs,
                                          out float dangerMs,
                                          out float threatWindowMs);

                // AND RULE 6 DOES NOT FIRE FOR A THREAT THAT CANNOT BE AVOIDED AT ALL.
                //
                // This is defect 3.4 arriving in the third rule, and it is the single change that
                // produced the largest quality difference on live data. Rules 4 and 5 got the
                // "already inside" exception when the defect was found; the temporal test never
                // did, so on a frame where the player was already standing in a bullet every
                // candidate -- including every candidate that walked OUT -- was refused by rule 6,
                // and the solver reported `NoAdmissible` and did nothing. A threat the player is
                // already inside, or one arriving faster than they can move, is not a threat the
                // candidate set can answer; letting it refuse the whole set does not make the
                // player safer, it makes the decision about a different bullet impossible.
                //
                // WHAT REPLACES IT IS ONE-SIDED, NOT ABSENT. The candidate is still refused if its
                // arrival point ends up CLOSER to the threat's predicted path than the player is
                // now -- so a dodge can never be talked into walking deeper into a bullet it
                // cannot avoid -- but it is never refused for a danger no move can improve.
                int verdict = t < _verdictCount ? _verdict[t] : (int)ThreatVerdict.Clear;
                if (alreadyInside)
                {
                    // Rules 4 and 5 have already applied exactly this one-sided test against the
                    // disc the player is standing in; rule 6 has nothing to add to it.
                }
                else if (verdict == (int)ThreatVerdict.TooFast)
                {
                    if (nearestD2 < _verdictPath2[t])
                    {
                        temporallyClear = false;
                        temporalRule = (int)AdmissionRule.TemporallyClear;
                    }
                }
                else if (refusal > (int)AdmissionRule.Admitted)
                {
                    temporallyClear = false;
                    temporalRule = refusal;
                    if (refusal == (int)AdmissionRule.TemporallyClear && dangerMs <= threatWindowMs)
                    {
                        // `dangerMs` is inside THIS THREAT's own window, which is the comparison
                        // `PlanRefusal` itself made. It is reported per candidate below, so it is
                        // counted in the same unit as the refusal total it is printed against.
                        refusedByWindow = true;
                    }
                }

                // THE REPORTED DURABILITY IS THE TIME UNTIL THIS PLAN'S PATH COMES WITHIN THE
                // COLLISION RADIUS OF THE BULLET'S BODY -- the same question rule 6 answers, asked
                // as a *time* instead of as a verdict, because section 5.6 needs a number it can
                // rank and not a boolean.
                //
                // IT IS NOT THE FIXED-POINT TEST, AND THE FIRST VERSION'S MISTAKE IS INSTRUCTIVE.
                // Using `ThreatThreatensFixedPoint` here -- the samples against the arrival point --
                // looks equivalent and is not: for a standing player and a nearby candidate both,
                // a bullet is either landing on the point within this frame or not landing on it at
                // all, so the number came out as exactly the frame time or exactly the trust prefix
                // and nothing in between. The distribution in the harness said so immediately (p50
                // = p10 = min = 650 ms), which is what a degenerate score term looks like.
                //
                // The plan form has the range the term needs: a bullet a quarter of a second away
                // and a bullet arriving next frame are different numbers, which is the difference
                // between "stay" and "leave now".
                //
                // AND IT IS THE FIRST VIOLATION, NOT THE TRUE MINIMUM. Durability answers "how long
                // until this plan is violated", and a closest approach that stays outside the
                // collision radius is not a violation -- it is the horizon. Feeding the true minimum
                // in here was measured: the chosen durability collapsed to p50 17 ms (one frame) on
                // every recording, because a bullet that is merely *near* sets the true minimum.
                //
                // AND A THREAT THE PLAYER IS ALREADY INSIDE CONTRIBUTES NOTHING TO ANY OF THE THREE
                // SCORE TERMS, WHICH IS THE SAME DOCTRINE THE RULES ABOVE ALREADY APPLY.
                //
                // "The damage for this frame is already committed, whatever the solver does." That
                // sentence is what gives rules 4, 5 and 6 their one-sided form, and it is just as
                // true of the score. Its measured cost when the score ignored it: on
                // `20261015-021911-p20152` -- a recording in which 11 % of all threat records are
                // already touching the player -- the plan walk's closest approach is attained AT
                // t = 0 for such a threat, for EVERY candidate, in every direction. So durability
                // read 0 ms, clearance read the same negative number, and resolve could never reach
                // 1.0, all of it identical across the ring. Three of the six terms became constants
                // scaled down, and `chosen durability p50 0 ms` is that degeneracy in one number.
                //
                // What still governs an already-inside threat is where each of them belongs: rules 4
                // and 5 refuse any move that does not increase the distance to its centre, and
                // `CountUnavoidable` still counts it, because a bullet the player is standing in
                // genuinely is unavoidable damage. The score does not get a vote on it.
                if (!alreadyInside)
                {
                    if (dangerMs < firstDangerMs)
                    {
                        firstDangerMs = dangerMs;
                    }

                    // CLEARANCE IS MEASURED TO WHERE THE BULLET'S BODY WILL BE, NOT TO WHERE IT IS
                    // AND NOT TO THE LINE IT IS ON. The first version returned the distance to
                    // `Xs[0], Ys[0]` -- the bullet's position at this instant -- which is LARGEST
                    // when the bullet is far away, precisely when the dodge does not need to move.
                    // Live, it saturated: on `20261015-021911-p20152` frame 332 the player had a
                    // bullet arriving in 600 ms and every candidate reported `clearance 7.16`,
                    // because the bullet's current position was 7.5 units off.
                    //
                    // The second version returned the distance to the bullet's whole remaining
                    // polyline, which has a gradient but treats a path the bullet has not flown yet
                    // as though it were already occupied. This is the third: the closest the two
                    // BODIES ever come, on one clock, over the lookahead.
                    float pathClearance = (float)Math.Sqrt(nearestD2) - collisionR;
                    if (pathClearance < clearance)
                    {
                        clearance = pathClearance;
                    }
                }

                // `resolve` IS THE NEW SAFETY TERM, and it is the one the scoring was missing.
                //
                // Clearance rewards being far from the nearest corridor; this rewards CLEARING the
                // corridors that are actually aimed at the player. They come apart on the frame
                // that matters: a candidate can be 3 units from the nearest bullet's path and still
                // inside the corridor of the one arriving in 200 ms. Counting resolved threats
                // gives the score a gradient toward "out of the way", which is the only thing a
                // dodge is ever trying to achieve, and it is what stops the dribble -- see the note
                // on `WeightResolve`.
                if (!alreadyInside && verdict != (int)ThreatVerdict.Clear)
                {
                    relevantThreats++;
                    if (nearestD2 >= collisionR2)
                    {
                        resolvedThreats++;
                    }
                }
            }

            // A PLAN THAT IS NEVER VIOLATED INSIDE THE LOOKAHEAD IS CLEAR FOR THE WHOLE OF IT, which
            // is the horizon value the term is normalized against. The previous shape only wrote
            // this when a threat had survived the bounding-box pre-test and then stayed outside the
            // radius, so a frame with no nearby threat at all left durability at its initializer --
            // the decision window -- and scored 0.1 for a plan nothing was coming for.
            durability = firstDangerMs < float.MaxValue
                ? firstDangerMs / 1000.0f
                : PlanLookaheadMs / 1000.0f;

            // A FRAME WHOSE ONLY THREATS ARE ONES THE PLAYER IS STANDING IN HAS NO CLEARANCE TO
            // REPORT, AND `float.MaxValue` IS NOT AN ANSWER -- it is an uninitialized term that
            // happens to compare correctly in the score and prints as 3.4e38 in the decision log.
            // "Comfortable" is the honest value: there is nothing left to be far from.
            if (clearance == float.MaxValue)
            {
                clearance = _config.ClearComfort;
            }

            if (insideAtArrival)
            {
                return (int)AdmissionRule.DiscAtArrival;
            }
            if (insideAlongTheWay)
            {
                return (int)AdmissionRule.DiscAlongTheWay;
            }
            if (!temporallyClear)
            {
                if (refusedByWindow)
                {
                    _windowRefusals++;
                }
                return temporalRule;
            }

            // Rule 7: not inside an enemy body, with the "already inside" exception. Without the
            // exception every move out of an overlapping circle sweeps it, which traps the player
            // under a mob that walked onto them.
            for (int b = 0; b < snapshot.Bodies.Count; b++)
            {
                EntityBody body = snapshot.Bodies[b];
                float br = body.Radius + playerRadius;
                float br2 = br * br;
                if (InsideCircle(body.X, body.Y, br2, player.X, player.Y))
                {
                    continue;   // already inside this body: the test is dropped for it
                }
                for (int s = 0; s < _sweepCount; s++)
                {
                    if (InsideCircle(body.X, body.Y, br2, _sweepX[s], _sweepY[s]))
                    {
                        return (int)AdmissionRule.EnemyBody;
                    }
                }
            }

            // The fraction of this frame's RELEVANT threats -- the ones that are not `Clear` -- whose
            // corridor this candidate's arrival point is outside. 1.0 means the move leaves the
            // player clear of everything that was aimed at them.
            if (relevantThreats > 0)
            {
                resolve = (float)resolvedThreats / relevantThreats;
            }
            return (int)AdmissionRule.Admitted;
        }

        private static bool InsideCircle(float cx, float cy, float r2, float x, float y)
        {
            float dx = x - cx;
            float dy = y - cy;
            return (dx * dx) + (dy * dy) < r2;
        }

        /// <summary>
        /// The player's swept path for one candidate: `MoveSteps + 1` points on a straight line
        /// from where they are to where the candidate puts them, with their times.
        ///
        /// THE PATH IS SAMPLED AND NOT ASSUMED INSTANTANEOUS, because a fast player covers a real
        /// fraction of a tile per frame (0.133 to 0.146 world units measured live,
        /// PHASE3-HANDOFF.md section 1.4) and a point-against-segment test would miss the moment
        /// the player passes through a bullet. The times matter as much as the positions: the
        /// temporal test is a segment-vs-segment test "with compatible times", so every sweep
        /// point carries the millisecond it is occupied at.
        /// </summary>
        private void BuildSweep(PlayerView player, float cx, float cy)
        {
            float dx = cx - player.X;
            float dy = cy - player.Y;
            float distance = (float)Math.Sqrt((dx * dx) + (dy * dy));

            int steps = distance <= 1e-6f ? 1 : (int)Math.Ceiling(distance / _config.SweepStep);
            if (steps < 1)
            {
                steps = 1;
            }
            if (steps > 64)
            {
                steps = 64;   // cannot happen for a legal budget, but a bound beats a surprise
            }
            if (_sweepX.Length < steps + 1)
            {
                _sweepX = new float[steps + 1];
                _sweepY = new float[steps + 1];
                _sweepT = new float[steps + 1];
            }

            // The move takes the frame it is executed in; the sweep is that segment. `MoveT` is
            // in milliseconds from now.
            float moveMs = _frameSeconds * 1000.0f;
            for (int s = 0; s <= steps; s++)
            {
                float u = (float)s / steps;
                _sweepX[s] = player.X + (dx * u);
                _sweepY[s] = player.Y + (dy * u);
                _sweepT[s] = moveMs * u;
            }
            _sweepCount = steps + 1;
        }

        /// <summary>
        /// Section 5.3 rule 6, the temporal test, as a three-way answer instead of a boolean -- and
        /// now asked of a PLAN in SPACETIME rather than of a path against a path.
        ///
        /// THE ONE SENTENCE THAT DESCRIBES THE CHANGE: a candidate used to be a POINT with a
        /// distance to the bullet's entire remaining polyline, and it is now a DIRECTION the
        /// character walks in, tested against wherever the bullet's body will be WHEN THE CHARACTER
        /// IS THERE. Two bodies, one clock.
        ///
        /// TWO DISTANCES COME OUT OF THE WALK AND CONFUSING THEM IS A DEFECT WORTH NAMING.
        ///
        ///   * `nearestD2`/`nearestMs` are the TRUE closest approach of the two bodies over the
        ///     lookahead, wherever it is. They are what "am I out of the way when this arrives"
        ///     means, and they are the inputs to `clearance`, `resolve` and the durability term.
        ///   * `dangerMs` is the FIRST moment the plan's body and the bullet's body are inside the
        ///     collision radius -- the only event the verdict is about. `float.MaxValue` means the
        ///     lookahead is clear, which is the durability term's horizon value.
        ///
        /// A third output -- the closest approach *inside* the radius -- was carried by the path
        /// model and is gone: it existed to answer "is the arrival point worse than the player's
        /// position now", and the spacetime form answers that with `nearestD2` against the standing
        /// plan's own `nearestD2`. An output nothing reads is a claim nothing tests.
        ///
        /// The one-sided clause is what keeps rule 6 from refusing the whole candidate set on behalf
        /// of a bullet no move can answer: outside the decision window, a candidate is refused only
        /// when it gets CLOSER to this bullet than standing still would. That is the same doctrine
        /// rules 4 and 5 apply to a disc the player is already inside -- stepping further in is
        /// refused, stepping out is admitted -- carried to the rule that never had it.
        ///
        /// `alreadyInside` is NOT a parameter here because the caller already skips this rule for a
        /// threat whose disc contains the player: rules 4 and 5 have applied exactly this one-sided
        /// test against that disc, and applying it twice is how a move that escapes gets refused by
        /// the rule that was supposed to admit it.
        /// </summary>
        private int PlanRefusal(Trajectory threat, float px, float py, float vxPerMs, float vyPerMs,
                                float r2, int trusted, int threatIndex,
                                out float nearestD2Out, out float nearestMsOut,
                                out float dangerMsOut, out float windowMsOut)
        {
            // THE THREAT'S OWN WINDOW, NOT THE FRAME'S WIDEST ONE. `_windowMs[t]` is this projectile's
            // own disc-crossing time scaled by the multiplier; `_decisionWindowMs` is the maximum over
            // the frame and is only a report. Comparing against the frame maximum here would refuse a
            // candidate for a bullet it clears, on behalf of a slower bullet elsewhere in the set.
            float windowMs = threatIndex >= 0 && threatIndex < _windowCount
                ? _windowMs[threatIndex]
                : WindowMsFor(threat, trusted, r2);
            windowMsOut = windowMs;

            // THE WALK ALWAYS RUNS, even when the verdict is already settled, because clearance,
            // resolve and durability are read off it by the caller. That was true of the path walk
            // it replaces and it is why `TemporalRefusal` never returned early either.
            PlanWalk(threat, px, py, vxPerMs, vyPerMs, r2, trusted, PlanLookaheadMs,
                     out float nearestD2, out float nearestMs, out float dangerMs);

            nearestD2Out = nearestD2;
            nearestMsOut = nearestMs;
            dangerMsOut = dangerMs;

            // Never inside the collision radius anywhere in the lookahead: nothing to answer.
            if (dangerMs == float.MaxValue)
            {
                return (int)AdmissionRule.Admitted;
            }

            // Inside the decision window: this is the frame the player is spending, and no version
            // of walking into a bullet now is safe. This is the clause `hitDuringTheFrame` used to
            // be, and it is deliberately the one unconditional refusal left in the rule.
            if (dangerMs <= windowMs)
            {
                return (int)AdmissionRule.TemporallyClear;
            }

            // Further out: one-sided, against the standard the player already has -- standing still.
            float standD2 = threatIndex >= 0 && threatIndex < _verdictCount
                ? _verdictPath2[threatIndex]
                : float.MaxValue;
            if (nearestD2 < standD2)
            {
                return (int)AdmissionRule.TemporallyClear;
            }
            return (int)AdmissionRule.Admitted;
        }

        /// <summary>
        /// The rule 6 decision window for every threat in the frame, once, into <see cref="_windowMs"/>.
        /// See <see cref="_windowMs"/> for why it is not computed at the point of use.
        ///
        /// IT ALSO PUBLISHES THE FRAME'S WIDEST WINDOW, which is what `DecisionWindowMs` reports: the
        /// largest of these is the frame's effective threshold, because a candidate is refused when
        /// ANY threat's window reaches it.
        /// </summary>
        private void BuildDecisionWindows(DodgeSnapshot snapshot, float playerRadius)
        {
            int n = snapshot.Threats.Count;
            if (_windowMs.Length < n)
            {
                _windowMs = new float[n];
            }
            _windowCount = n;
            _decisionWindowMs = 0.0f;

            for (int t = 0; t < n; t++)
            {
                Trajectory threat = snapshot.Threats[t];
                if (threat.Count <= 0)
                {
                    _windowMs[t] = 0.0f;
                    continue;
                }
                int trusted = t < _admitCount ? _admitTrusted[t] : threat.Count;
                float disc = playerRadius + threat.Radius;
                float windowMs = WindowMsFor(threat, trusted, disc * disc);
                _windowMs[t] = windowMs;
                if (windowMs > _decisionWindowMs)
                {
                    _decisionWindowMs = windowMs;
                }
            }
        }

        /// <summary>
        /// The rule 6 decision window for ONE threat, in milliseconds: how much of this bullet's
        /// path counts as "the frame the player is spending" rather than as something the next frame
        /// re-decides.
        ///
        /// IT IS A MULTIPLE OF THE TIME THIS PROJECTILE TAKES TO CROSS THE COLLISION DISC, and that
        /// is the whole of the fix. The window used to be `MoveTemporalWindowSeconds`, one constant
        /// for every projectile in the game -- which makes it a DISTANCE of `window * v`, and a
        /// distance that varies by an order of magnitude across the projectiles a player actually
        /// meets. Measured over the five recordings (`tools/projectile_speed_report.py`, 15360
        /// projectiles): the fixed 50 ms protected **1.95x the collision disc** on a 16 u/s
        /// projectile and **0.53x of it** on the 6 u/s projectiles of `p26156` -- the session every
        /// other number in this document is measured against. It over-refused on four recordings and
        /// under-refused on the fifth, and it could not be right for both ends.
        ///
        /// `disc / v` is the quantity that IS the same kind of thing everywhere: it is how long the
        /// bullet is a threat TO THIS PLAYER'S BODY, which is the interval the refusal is about. The
        /// multiplier is then a pure preference -- how much path either side of the disc to refuse --
        /// and it is the knob exposed as `disc=` on the command line and
        /// `DRELAY_DODGE_DECISION_DISC` on the hook. `MoveTemporalWindowSeconds` is an absolute
        /// floor under it, zero out of the box.
        ///
        /// THE SPEED IS MEASURED, NOT ASSUMED, and this is the same discipline as the course and the
        /// player's speed: the trace carries no velocity field, and a speed law re-derived here would
        /// be a second answer to a question the samples already answer. It is the distance between
        /// the first two ALIVE knots over the sample step, which is the bullet's own near-field speed
        /// at the moment of the decision -- the interval the window is about.
        ///
        /// IT FAILS OPEN, AND IT FAILS TO THE OLD BEHAVIOUR. No samples, one sample, a dead beat, or
        /// a projectile whose radius is unread leave the speed at zero, and a zero speed yields an
        /// infinite crossing time -- so the window would run away. Every one of those cases therefore
        /// returns the floor (`MoveTemporalWindowSeconds`), which is the fixed-window behaviour this
        /// replaced. A missing measurement must not be able to turn the decision window into the
        /// whole lookahead.
        /// </summary>
        private float WindowMsFor(Trajectory threat, int trusted, float collisionR2)
        {
            float floorMs = _config.MoveTemporalWindowSeconds * 1000.0f;
            float multiplier = _config.DecisionDiscMultiplier;
            if (multiplier <= 0.0f)
            {
                // `disc=0` means "do not scale": the rule is the floor and nothing else.
                return floorMs;
            }

            float v = ThreatSpeedPerMs(threat, trusted);
            if (v <= 1e-6f)
            {
                return floorMs;
            }

            // `collisionR2` is `(playerRadius + bulletRadius)^2`, so its square root is the disc's
            // radius -- the same disc the whole rule is about, and the same one `PlanWalk` tests
            // against. Using the radius the caller already computed keeps the two from disagreeing.
            float disc = (float)Math.Sqrt(collisionR2 > 0.0f ? collisionR2 : 0.0f);
            if (disc <= 0.0f)
            {
                return floorMs;
            }

            float scaledMs = multiplier * (disc / v);
            if (scaledMs > floorMs)
            {
                floorMs = scaledMs;
            }
            // AND THE WINDOW CANNOT EXCEED THE HORIZON IT IS ASKED OVER, WHICH IS NOT A TIDINESS
            // CLAMP. A nearly-stationary projectile has an enormous disc-crossing time -- the first
            // measurement of this on `p5824` produced a **30720 ms** window from a 0.5 u/s bullet,
            // and `PlanWalk` only ever looks `PlanLookaheadMs` (500 ms) ahead -- so past the
            // lookahead the window clause stops being a threshold and becomes `dangerMs <= infinity`,
            // which refuses every candidate that touches that disc within the lookahead. That is not
            // the speed scaling doing its job; it is the rule collapsing back into the unconditional
            // form it had before the one-sided clause existed, on exactly the frames where a parked
            // projectile is nearby. Clamping at the lookahead keeps the clause meaningful: inside it,
            // a violation is refused; outside it, the walk has nothing to say and the one-sided
            // comparison decides.
            float horizonMs = PlanLookaheadMs;
            if (horizonMs > 0.0f && floorMs > horizonMs)
            {
                return horizonMs;
            }
            return floorMs;
        }

        /// <summary>
        /// The threat's own speed in world units per millisecond, from its sample grid.
        ///
        /// THE NEAR-FIELD SPEED, NOT AN AVERAGE OVER THE HORIZON. Samples are 30 ms apart and a
        /// projectile's speed law is not constant -- `Trajectory` records the deceleration tail
        /// (`Alive`, and the reference engine's "shot finished" signal), and a shot that slows to
        /// nothing at the end of its life would drag a whole-path average down. The decision window
        /// is about the next few tens of milliseconds, so it reads the first interval.
        ///
        /// ZERO IS RETURNED FOR "COULD NOT MEASURE", and the caller falls back to the floor for it.
        /// A stationary projectile is genuinely a zero here, and it is also a case where the old
        /// behaviour (a fixed window) is the safe answer, so the two do not need to be told apart.
        /// </summary>
        private float ThreatSpeedPerMs(Trajectory threat, int trusted)
        {
            int n = threat.Count;
            if (trusted > 0 && trusted < n)
            {
                n = trusted;
            }
            if (n < 2)
            {
                return 0.0f;
            }
            if (!threat.Alive[0] || !threat.Alive[1])
            {
                return 0.0f;
            }
            float stepMs = threat.StepMs > 0.0f ? threat.StepMs : _config.SampleStepMs;
            if (stepMs <= 0.0f)
            {
                return 0.0f;
            }
            float dx = threat.Xs[1] - threat.Xs[0];
            float dy = threat.Ys[1] - threat.Ys[0];
            return (float)Math.Sqrt((dx * dx) + (dy * dy)) / stepMs;
        }

        /// <summary>
        /// The player's plan and one threat, walked TOGETHER over the lookahead, and the closest the
        /// two BODIES ever come.
        ///
        /// WHY A RELATIVE SEGMENT AND NOT A SEGMENT-TO-SEGMENT TEST. The bullet is interpolated
        /// linearly between two sample knots; the plan is a straight line at constant speed by
        /// construction. Both are therefore linear in time, so the DIFFERENCE between them is
        /// linear in time too -- and the closest approach of two objects on linear paths is the
        /// distance from the origin to that relative segment. It is exact, it costs one
        /// `PointSegmentDistanceSquared` per knot, and it needs none of the time-compatibility
        /// machinery the two-polyline form needed, because the time correlation is built into the
        /// subtraction.
        ///
        /// IT IS ALSO STRICTER THAN THE SAMPLES AND THAT MATTERS. At 24 units/s a bullet moves 0.72
        /// units between knots (SPIKE3.2-OFFLINE-SOLVER.md section 3.3), which is larger than a
        /// 0.41-unit collision disc, so a sample-only test can walk a bullet straight through the
        /// player between two knots. Testing the whole interval cannot.
        ///
        /// THE WALK STOPS AT THE LOOKAHEAD AND MAY OVERRUN IT BY ONE KNOT, which is deliberate: the
        /// cost of shortening the last interval to a partial segment is more arithmetic than the
        /// 30 ms it buys, and every consumer of these numbers treats them as evidence about a window
        /// rather than as a boundary condition.
        /// </summary>
        private void PlanWalk(Trajectory threat, float px, float py, float vxPerMs, float vyPerMs,
                              float r2, int trusted, float lookaheadMs,
                              out float nearestD2, out float nearestMs, out float dangerMs)
        {
            nearestD2 = float.MaxValue;
            nearestMs = 0.0f;
            dangerMs = float.MaxValue;

            if (trusted > threat.Count)
            {
                trusted = threat.Count;
            }
            if (trusted <= 0)
            {
                return;
            }

            float stepMs = threat.StepMs > 0.0f ? threat.StepMs : _config.SampleStepMs;

            // Knot 0 is at t = 0, where the plan has not moved yet: the relative position is just
            // the bullet's offset from the player.
            float relX = threat.Xs[0] - px;
            float relY = threat.Ys[0] - py;
            float relD2 = (relX * relX) + (relY * relY);
            nearestD2 = relD2;
            nearestMs = 0.0f;
            if (relD2 < r2)
            {
                dangerMs = 0.0f;
            }
            if (trusted <= 1)
            {
                return;
            }

            for (int k = 0; k + 1 < trusted; k++)
            {
                float t0 = k * stepMs;
                if (t0 >= lookaheadMs)
                {
                    break;
                }
                float t1 = t0 + stepMs;

                // The relative position at the far knot: the bullet's position there, less where
                // the plan puts the player at that same instant.
                float nextX = threat.Xs[k + 1] - px - (vxPerMs * t1);
                float nextY = threat.Ys[k + 1] - py - (vyPerMs * t1);
                float dx = nextX - relX;
                float dy = nextY - relY;

                float d2 = RelativeClosest(relX, relY, dx, dy, out float u);
                if (d2 < nearestD2)
                {
                    nearestD2 = d2;
                    nearestMs = t0 + (u * stepMs);
                }

                // The FIRST entry, not the deepest one: durability is "how long until this plan is
                // violated", and a later, closer approach does not make the first one later.
                if (dangerMs == float.MaxValue)
                {
                    float entry = RelativeEntry(relX, relY, dx, dy, r2);
                    if (entry >= 0.0f)
                    {
                        dangerMs = t0 + (entry * stepMs);
                    }
                }

                relX = nextX;
                relY = nextY;
            }
        }

        /// <summary>
        /// The closest approach of the relative path `(wx, wy) + u * (dx, dy)` to the origin, over
        /// `u` in [0, 1], and the `u` at which it happens.
        /// </summary>
        private static float RelativeClosest(float wx, float wy, float dx, float dy, out float u)
        {
            float len2 = (dx * dx) + (dy * dy);
            float t = len2 <= 1e-12f ? 0.0f : -(((wx * dx) + (wy * dy)) / len2);
            if (t < 0.0f) { t = 0.0f; }
            if (t > 1.0f) { t = 1.0f; }
            u = t;
            // THE DISTANCE COMES FROM THE ONE GEOMETRY PRIMITIVE IN THIS FILE, so that the
            // point-to-segment formula exists in exactly one place. `t` is therefore computed twice
            // -- once here for the TIME, once inside for the DISTANCE -- and that is a handful of
            // flops against a loop that runs 77 x threats x 21 times a frame. One formula is worth
            // more than the flops, and a second copy is a second thing that can disagree.
            return PointSegmentDistanceSquared(0.0f, 0.0f, wx, wy, wx + dx, wy + dy);
        }

        /// <summary>
        /// The `u` in [0, 1] at which the relative path first comes within `sqrt(r2)` of the origin,
        /// or -1 when it does not. The smaller root of `|w + u d|^2 = r2`, which is the entry.
        /// </summary>
        private static float RelativeEntry(float wx, float wy, float dx, float dy, float r2)
        {
            float c = (wx * wx) + (wy * wy) - r2;
            if (c < 0.0f)
            {
                return 0.0f;      // already inside at the interval's start
            }
            float a = (dx * dx) + (dy * dy);
            if (a <= 1e-12f)
            {
                return -1.0f;     // stationary and outside: it never enters
            }
            float b = 2.0f * ((wx * dx) + (wy * dy));
            float disc = (b * b) - (4.0f * a * c);
            if (disc < 0.0f)
            {
                return -1.0f;
            }
            float u = (-b - (float)Math.Sqrt(disc)) / (2.0f * a);
            return (u >= 0.0f && u <= 1.0f) ? u : -1.0f;
        }

        // `TemporalDanger` STOOD HERE, AND IT IS DELETED RATHER THAN LEFT UNUSED.
        //
        // It answered "when does the player's one-frame sweep first cross the bullet's predicted
        // path", by matching knot windows -- the two-polyline form of section 5.5. It was already
        // uncalled when the plan model landed, and leaving it would have been a second, older
        // answer to the question `PlanRefusal` now owns: a reader who found it would have had no
        // way to tell which one the solver used. The time correlation it implemented by hand is
        // built into `PlanWalk`'s relative-motion subtraction, where it cannot drift.

        // `ClearanceAt` stood here -- the distance from a point to the threat's CURRENT disc -- and it
        // was removed rather than left unused, because leaving it would invite its reinstatement.
        // It is the wrong question for a score term: for a bullet flying at the player it is largest
        // exactly when the bullet is still far away, and it says nothing about where the bullet is
        // going. `Admit` now reports clearance as the distance from the candidate point to the
        // threat's predicted PATH, which is what "am I out of the way when this arrives" means.

        /// <summary>
        /// Whether a threat's whole trusted path is further from the sweep's bounding box than
        /// `r`. Conservative by construction: it only ever skips tests that cannot succeed.
        ///
        /// THE THREAT'S OWN BOX IS A PARAMETER AND NOT RECOMPUTED HERE. It is a property of the
        /// threat, not of the candidate, and the first version rebuilt it for every candidate --
        /// 74 times a frame over the same 21 samples. That was invisible until the decision was
        /// measured against recorded play (SPIKE3.2-OFFLINE-SOLVER.md section 4.3: p99 three times
        /// the whole frame budget), because a repeated computation costs nothing to read.
        ///
        /// THE KNOT RANGE IS UNCHANGED. `BuildAdmitEnvelope` builds the box over `threat.Count`
        /// knots -- the same range this function used to walk -- so the skip is the same skip and
        /// the admitted set is the same set.
        /// </summary>
        private static bool ThreatBoundingBoxFar(float[] xs, float[] ys, int count, float r,
                                                 float tMinX, float tMaxX, float tMinY, float tMaxY)
        {
            if (tMinX > tMaxX || tMinY > tMaxY)
            {
                return true;    // an empty threat: nothing to be near
            }

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int s = 0; s < count; s++)
            {
                if (xs[s] < minX) { minX = xs[s]; }
                if (xs[s] > maxX) { maxX = xs[s]; }
                if (ys[s] < minY) { minY = ys[s]; }
                if (ys[s] > maxY) { maxY = ys[s]; }
            }

            float dx = 0.0f, dy = 0.0f;
            if (tMaxX < minX - r) { dx = (minX - r) - tMaxX; }
            else if (tMinX > maxX + r) { dx = tMinX - (maxX + r); }
            if (tMaxY < minY - r) { dy = (minY - r) - tMaxY; }
            else if (tMinY > maxY + r) { dy = tMinY - (maxY + r); }
            return (dx * dx) + (dy * dy) > 0.0f;
        }

        /// <summary>The egress direction: away from the nearest disc the player is inside.</summary>
        private bool TryEgressDirection(DodgeSnapshot snapshot, PlayerView player, float playerRadius,
                                        out float x, out float y)
        {
            float bestDistance = float.MaxValue;
            float bestX = player.X;
            float bestY = player.Y;
            bool found = false;
            for (int i = 0; i < snapshot.Threats.Count; i++)
            {
                Trajectory t = snapshot.Threats[i];
                if (t.Count <= 0)
                {
                    continue;
                }
                if (!InsideDisc(t.Xs[0], t.Ys[0], playerRadius, t, player.X, player.Y))
                {
                    continue;
                }
                float dx = player.X - t.Xs[0];
                float dy = player.Y - t.Ys[0];
                float d = (float)Math.Sqrt((dx * dx) + (dy * dy));
                if (d < bestDistance)
                {
                    bestDistance = d;
                    // Just outside the disc, along the line from its centre through the player.
                    float need = playerRadius + t.Radius + _config.SweepStep;
                    if (d < 1e-5f)
                    {
                        // Dead centre: there is no "away" direction from the centre, so pick one
                        // deterministically rather than dividing by zero.
                        bestX = player.X + need;
                        bestY = player.Y;
                    }
                    else
                    {
                        bestX = t.Xs[0] + ((dx / d) * need);
                        bestY = t.Ys[0] + ((dy / d) * need);
                    }
                    found = true;
                }
            }
            x = bestX;
            y = bestY;
            return found;
        }

        // ===================================================================================
        // Stage 6 -- scoring (section 5.6)
        // ===================================================================================

        private void ScoreAll(DodgeSnapshot snapshot, PlayerView player, float playerRadius, float budget)
        {
            // The best (smallest) distance to any *non-ignorable* threat, for the lateral term.
            for (int i = 0; i < _resultCount; i++)
            {
                CandidateResult r = _results[i];
                if (!r.Admissible)
                {
                    r.Score = float.NegativeInfinity;
                    _results[i] = r;
                    continue;
                }

                float durability = Normalize(r.DurabilitySeconds);
                // A MISSING INTENT SCORES ZERO RATHER THAN DUE EAST. The term is a preference, and a
                // preference for a direction nobody asked for is worse than no preference at all --
                // which is the same reasoning `_goalValid` applies to the goal.
                float intent = player.IntentValid
                    ? IntentAlignment(r.HeadingRadians, player.IntentRadians, r.Distance)
                    : 0.0f;
                float goal = GoalProgress(r.HeadingRadians, r.Distance, budget);
                float path = PathProgress(r.HeadingRadians, r.Distance, budget);
                float heading = _hasPreviousHeading ? Dot(r.HeadingRadians, _previousHeading) : 0.0f;
                float lateral = Lateralness(snapshot, player, r);
                float move = r.Distance / budget;
                float stand = r.Distance <= 1e-6f ? 1.0f : 0.0f;
                float clearance = r.Clearance;
                if (clearance > _config.ClearComfort)
                {
                    clearance = _config.ClearComfort;
                }
                // NEGATIVE CLEARANCE IS KEPT, AND CLAMPING IT AT ZERO WAS THE CAUSE OF THE DODGE'S
                // HEADING OSCILLATION.
                //
                // The clamp's justification was "an admissible candidate that is inside a disc
                // cannot exist", which was true when rules 4 and 5 refused every candidate that
                // ended in one. It stopped being true the moment the already-inside exception was
                // applied to all three rules: escaping a disc you are standing in means emitting a
                // candidate that IS still inside it, and for every one of those the clearance term
                // was `0.0`. So on exactly the frames the dodge exists for -- bullets closing,
                // player inside a corridor -- the only term that could tell "toward the bullet"
                // from "away from it" went flat, and the direction was decided by the latency
                // between the lateral term's two equal perpendiculars. Measured on
                // `20261015-021911-p20152`: **617 of 1353 moves (46 %) turned more than 90 degrees,
                // mean 133** -- the operator's "crude", in one number.
                //
                // Signed and bounded on both sides, the term has a gradient wherever the player is:
                // a candidate that ends deeper inside scores negative, one that ends further out
                // scores positive, and the reversal term is no longer the only thing with an
                // opinion.
                if (clearance < -_config.ClearComfort)
                {
                    clearance = -_config.ClearComfort;
                }
                float resolve = r.Resolve;
                // THE STATE THIS STEP LEADS TO, WHICH IS THE THING THE SCORE COULD NOT SEE.
                // See `ProjectedDanger` and SPIKE3.2-OFFLINE-SOLVER.md section 15.6.
                float projected = ProjectedDanger(player, r, budget);

                r.Score =
                    (_config.WeightResolve * resolve)
                    + (_config.WeightClearance * clearance)
                    + (_config.WeightDurability * durability)
                    + (_config.WeightIntent * intent)
                    + (_config.WeightGoal * goal)
                    + (_config.WeightPath * path)
                    + (_config.WeightHeading * heading)
                    + (_config.WeightLateral * lateral)
                    - (_config.WeightAxial * Axialness(snapshot, player, r))
                    - (_config.WeightProjectedDanger * projected)
                    - (_config.WeightEnemyDanger * EnemyDanger(player, r, budget))
                    - (_config.WeightMove * move)
                    + (_config.WeightStandBias * stand);
                _results[i] = r;
            }
        }

        /// <summary>
        /// Each score term's own contribution to the chosen candidate, as a weighted value.
        ///
        /// WHY THIS EXISTS AND WHY IT IS NOT LOGGING. Every measurement this project has made of the
        /// score so far has been a DISTRIBUTION OVER OUTCOMES -- how many moves were outruns, how many
        /// degrees of deflection -- and a distribution cannot say WHICH TERM chose them. The
        /// outrun/sidestep question is exactly that kind of question: `tools/dodge_direction_report.py`
        /// says the dodge retreated on 46 % of its moves on `p20152`, and nothing anywhere says
        /// whether that was the safety terms paying for distance, the goal term failing to have an
        /// opinion, or the move penalty preferring a shorter step. This is the instrument that
        /// answers it, and it is the reason `HANDOFF-DIRECTED-DODGE.md` section 8 trap 11 puts the
        /// instrument before the change.
        ///
        /// The order is the order of the sum in `ScoreAll`, and each entry is `weight * term` -- the
        /// number that actually entered the score -- so a reader can add them up and get `Score`
        /// back. `path` is new with the pathfinder; the rest are the terms that predate it.
        /// </summary>
        public void DescribeChosen(DodgeSnapshot snapshot, int index, float[] into)
        {
            if (into == null || into.Length < 14)
            {
                return;
            }
            for (int i = 0; i < 14; i++)
            {
                into[i] = 0.0f;
            }
            if (!_lastValid || index < 0 || index >= _resultCount)
            {
                return;
            }
            CandidateResult r = _results[index];
            float budget = _lastBudget;
            float goal = GoalProgress(r.HeadingRadians, r.Distance, budget);
            float path = PathProgress(r.HeadingRadians, r.Distance, budget);
            float heading = _hasPreviousHeading ? Dot(r.HeadingRadians, _previousHeading) : 0.0f;
            float lateral = snapshot != null ? Lateralness(snapshot, _lastPlayer, r) : 0.0f;
            float axial = snapshot != null ? Axialness(snapshot, _lastPlayer, r) : 0.0f;
            float move = budget > 0.0f ? r.Distance / budget : 0.0f;
            float stand = r.Distance <= 1e-6f ? 1.0f : 0.0f;
            float clearance = r.Clearance;
            if (clearance > _config.ClearComfort) { clearance = _config.ClearComfort; }
            if (clearance < -_config.ClearComfort) { clearance = -_config.ClearComfort; }
            float intent = _lastPlayer.IntentValid
                ? IntentAlignment(r.HeadingRadians, _lastPlayer.IntentRadians, r.Distance)
                : 0.0f;

            into[0] = _config.WeightResolve * r.Resolve;
            into[1] = _config.WeightClearance * clearance;
            into[2] = _config.WeightDurability * Normalize(r.DurabilitySeconds);
            into[3] = _config.WeightIntent * intent;
            into[4] = _config.WeightGoal * goal;
            into[5] = _config.WeightPath * path;
            into[6] = _config.WeightHeading * heading;
            into[7] = _config.WeightLateral * lateral;
            into[8] = -(_config.WeightMove * move);
            into[9] = _config.WeightStandBias * stand;
            into[10] = -(_config.WeightAxial * axial);
            into[11] = -(_config.WeightProjectedDanger * ProjectedDanger(_lastPlayer, r, budget));
            into[12] = -(_config.WeightEnemyDanger * EnemyDanger(_lastPlayer, r, budget));
            into[13] = r.Score;
        }

        /// <summary>The candidate names of <see cref="DescribeChosen"/>'s terms, in its own order.</summary>
        public static readonly string[] ScoreTermNames =
        {
            "resolve", "clearance", "durability", "intent", "goal", "path", "heading",
            "lateral", "move", "stand", "axial", "projected", "enemy", "score",
        };

        /// <summary>
        /// Durability as a fraction of the horizon the solver actually believes, capped at 1.
        ///
        /// IT USED TO DIVIDE BY `MoveTemporalWindowSeconds` -- 50 ms -- AND THAT MADE THE TERM A
        /// CONSTANT. Any candidate whose path was clear for more than one frame scored 1.0, which is
        /// almost every candidate on almost every frame, so `WeightDurability` -- the second-heaviest
        /// weight in section 5.6 -- ranked nothing at all. SPIKE3.2-OFFLINE-SOLVER.md section 4.2
        /// measured exactly this and called it honestly ("the score collapses towards safe versus
        /// not"), and left it as a tuning question.
        ///
        /// The horizon is the honest denominator: durability is "how long until this candidate's
        /// path is violated", the sampler was asked for 650 ms, and the solver does not believe
        /// anything past it (`Trajectory.TrustedCount`). A candidate that is clear for 650 ms scores
        /// 1; one that is violated in 130 ms scores 0.2. That is a ranking, which is what a score
        /// term is for.
        /// </summary>
        private float Normalize(float durabilitySeconds)
        {
            float window = PlanLookaheadMs / 1000.0f;
            if (window <= 0.0f)
            {
                return 0.0f;
            }
            float v = durabilitySeconds / window;
            return v > 1.0f ? 1.0f : v;
        }

        /// <summary>
        /// How dangerous the state a candidate LEADS TO is, on the planner's own spacetime danger
        /// field. In `[0,1]`, and 0 whenever the field has nothing to say.
        ///
        /// WHY THIS IS THE TERM THE TASK ACTUALLY NEEDED, AND WHY THE WAYPOINT WAS NOT ENOUGH.
        /// `SPIKE3.2-OFFLINE-SOLVER.md` section 15.5 measured the shipped path term and found it only
        /// a partial fix, with the reason in the terms themselves: the retreat is paid for by `resolve`
        /// and `clearance`, and BOTH OF THEM ARE RIGHT about one step -- a retreat really does
        /// increase the distance to a bullet's corridor, and really does clear corridors. A waypoint
        /// is a DIRECTION, so a preference built on it cannot outvote them on a frame where the goal
        /// ray is itself walkable and clear, which is most of them.
        ///
        /// The sentence this answers is the one `HANDOFF-DIRECTED-DODGE.md` opens with: "nothing in the
        /// candidate set can express 'this step is bad because of the step after it'." So this asks the
        /// question about the step after: take the candidate's own velocity -- the player's
        /// displacement to the candidate, carried on -- and read the danger field along the next
        /// `ProjectedSeconds` of it, at the times the character would be there. A retreat that carries
        /// the character into the second bullet now costs; a sidestep into the clear does not.
        ///
        /// IT IS A PREFERENCE AND NOT A GATE, exactly like `WeightPath`: the admissible set is the
        /// safety boundary and nothing here can admit, refuse or move a candidate. The field it reads
        /// is the same envelope the search uses, built once per frame, so this is a second consumer of
        /// one measurement rather than a second model.
        /// </summary>
        private float ProjectedDanger(PlayerView player, CandidateResult r, float budget)
        {
            int samples = _config.ProjectedSamples;
            float seconds = _config.ProjectedSeconds;
            if (samples < 1 || seconds <= 0.0f || r.Distance <= 1e-6f || budget <= 0.0f)
            {
                return 0.0f;   // `stand` has no direction to carry, which is what `stand` is for
            }
            if (!_planner.FieldReady)
            {
                return 0.0f;   // no grid or no violence this frame: no field, and so no opinion
            }

            float speed = budget / (_frameSeconds > 0.0f ? _frameSeconds : (1.0f / 60.0f));
            float dx = (r.X - player.X) / r.Distance;
            float dy = (r.Y - player.Y) / r.Distance;
            float total = 0.0f;
            for (int s = 1; s <= samples; s++)
            {
                float t = (seconds * s) / samples;
                float distance = speed * t;
                float x = player.X + (dx * distance);
                float y = player.Y + (dy * distance);
                // THE REGION AND NOT THE POINT, AND THE FIRST VERSION OF THIS GOT IT WRONG IN A WAY
                // THE INSTRUMENT CAUGHT. `Danger` is non-zero only within about `player radius +
                // bullet radius + pad` of a bullet's BODY -- 0.9 units on these recordings -- and a
                // point eight units ahead is nowhere near any bullet's body at the instant the
                // character is there. So the term read 0 for every candidate of every frame, and the
                // sweep over its weight (0, 0.9, 1.5, 2.5) produced four byte-identical direction
                // histograms -- the exact signature this project keeps recording: a term that is
                // absent and a term that does nothing print the same thing.
                //
                // The question is "is there a bullet NEAR where I will be", and "near" in a world of
                // 0.4-unit discs is the collision radius. The probe is a 3x3 patch at that radius.
                float reach = _planner.ProbeRadius;
                float worst = 0.0f;
                for (int ox = -1; ox <= 1; ox++)
                {
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        float d = _planner.Danger(x + (ox * reach), y + (oy * reach), t);
                        if (d > worst)
                        {
                            worst = d;
                        }
                    }
                }
                total += worst;
            }
            return total / samples;
        }

        /// <summary>
        /// How close a candidate leaves the character to an enemy's EMITTER disc. In `[0,1]`, and 0
        /// whenever the frame has no enemy bodies or the emitter is off.
        ///
        /// IT IS THE SAME FIELD `ProjectedDanger` READS, OVER THE OTHER HALF OF THE ENVELOPE. See
        /// `PathPlanner.DangerOverBodies`: bodies are folded into the planner's one envelope as
        /// one-sample stationary entries, so this is a second question asked of one measurement --
        /// "does this candidate end up on a mob's doorstep" -- rather than a second model of where
        /// the mobs are.
        ///
        /// IT READS THE CANDIDATE'S ARRIVAL POINT, NOT ITS PROJECTED PATH, AND THAT IS THE DIVISION OF
        /// LABOUR RATHER THAN AN OMISSION. The PATH is what answers "will the route go through the
        /// enemy's pad" -- the edge cost in `PathPlanner.Search` -- and it answers it for the whole
        /// horizon at once. This term exists for the frames the path declined (no grid, no goal,
        /// unreachable) and for the ranking among admissible candidates: it can only re-rank what
        /// already passed admission, exactly like `WeightPath`, and it can never admit, refuse or
        /// move a candidate.
        ///
        /// ZERO WHEN `stand` IS THE CANDIDATE, for the same reason `ProjectedDanger` returns zero
        /// there: standing has no direction and no projected position, which is what standing is.
        ///
        /// THE DEFAULT WEIGHT IS ZERO. This ships implemented and switched off, the
        /// `WeightProjectedDanger` precedent: the harness sweeps it, a live run reads it, and nobody
        /// has to guess whether the term is absent or merely silent.
        /// </summary>
        private float EnemyDanger(PlayerView player, CandidateResult r, float budget)
        {
            if (_config.WeightEnemyDanger <= 0.0f)
            {
                return 0.0f;   // the off-switch, and it costs one compare on every frame
            }
            if (r.Distance <= 1e-6f || budget <= 0.0f)
            {
                return 0.0f;   // `stand` has no arrival point that is any different from the player's
            }
            if (!_planner.FieldReady)
            {
                return 0.0f;   // no grid or no violence this frame: no field, and so no opinion
            }

            // THE POINT, NOT THE REGION, AND THAT IS THE DESIGN RATHER THAN A SHORTCUT. The body field
            // is a set of CELLS whose centres are inside an emitter disc, so "is this arrival point in
            // a mob's doorstep" is a single cell lookup -- the same lookup the search's edge cost made
            // for the same cell. A 3x3 probe here would ask about cells the candidate is not in.
            return _planner.DangerOverBodies(r.X, r.Y);
        }

        /// <summary>
        /// How much of the move carries the character toward the PATH's waypoint.
        ///
        /// IT IS SHAPED LIKE <see cref="GoalProgress"/> AND IT REPLACES IT ON THE FRAMES THE PATH HAS
        /// AN OPINION. Same `forward * (distance / budget)`: a step that points at the waypoint
        /// scores by how much of a full step it takes, and a retreat scores zero rather than
        /// negative. The two terms are separate so that the A/B is a weight and not a rewrite --
        /// `DRELAY_DODGE_PATH=0` restores `WeightGoal` at its old value and this term at zero.
        ///
        /// ZERO WHEN THERE IS NO WAYPOINT, which is the whole degradation story: no grid, no goal, an
        /// unreachable target or a search that hit its cap all leave `WaypointValid` false and the
        /// candidate set scored exactly as it was before this term existed.
        /// </summary>
        private float PathProgress(float heading, float distance, float budget)
        {
            if (!_planner.WaypointValid || distance <= 1e-6f || budget <= 0.0f)
            {
                return 0.0f;
            }
            float forward = Dot(heading, _planner.WaypointHeading);
            if (forward <= 0.0f)
            {
                return 0.0f;
            }
            return forward * (distance / budget);
        }

        /// <summary>
        /// How much of the goal the move achieves: the fraction of the step that carries the
        /// character toward the imaginary point in front of them.
        ///
        /// IT IS SCALED BY DISTANCE AND THAT IS THE DIFFERENCE FROM <see cref="IntentAlignment"/>.
        /// `intent` asks "is this the direction the player's hands asked for", and a slow step in
        /// the right direction preserves intent exactly as well as a fast one -- which is true, and
        /// is why `WeightMove` alone used to decide the magnitude. `goal` asks "does this make
        /// progress toward where the character is already going", and a third of a step makes a
        /// third of the progress. That is what puts the dodge back on its line instead of dribbling:
        /// with the goal term present, distance along the goal heading is a *gain*, and the move
        /// penalty is a smaller cost against it.
        ///
        /// Zero for a heading that is not forward at all, so a retreat is never scored as progress.
        /// </summary>
        private float GoalProgress(float heading, float distance, float budget)
        {
            if (!_goalValid || distance <= 1e-6f || budget <= 0.0f)
            {
                return 0.0f;
            }
            float forward = Dot(heading, _goalRadians);
            if (forward <= 0.0f)
            {
                return 0.0f;
            }
            return forward * (distance / budget);
        }

        /// <summary>
        /// How much of the player's intent survives. A full-magnitude move along the intent
        /// heading scores 1 and one in the opposite direction scores 0; a zero-length move has no
        /// direction and so cannot be said to preserve intent either way.
        /// </summary>
        private static float IntentAlignment(float heading, float intent, float distance)
        {
            if (distance <= 1e-6f)
            {
                return 0.0f;
            }
            return (Dot(heading, intent) + 1.0f) * 0.5f;
        }

        /// <summary>
        /// `dot(heading, perpendicular to the line to the nearest threat)`.
        ///
        /// WHY SIDESTEPPING IS PREFERRED, which is section 5.6's reason and not an aesthetic one:
        /// moving along the line towards a shooter takes the player into the rest of its volley,
        /// while moving across it keeps the same distance from the bullets already in the air and
        /// leaves every option open.
        /// </summary>
        private static float Lateralness(DodgeSnapshot snapshot, PlayerView player, CandidateResult r)
        {            if (r.Distance <= 1e-6f)
            {
                return 0.0f;
            }
            float nearest = float.MaxValue;
            float toX = 0.0f, toY = 0.0f;
            bool found = false;
            for (int i = 0; i < snapshot.Threats.Count; i++)
            {
                Trajectory t = snapshot.Threats[i];
                if (t.Count <= 0)
                {
                    continue;
                }
                float dx = t.Xs[0] - player.X;
                float dy = t.Ys[0] - player.Y;
                float d2 = (dx * dx) + (dy * dy);
                if (d2 < nearest)
                {
                    nearest = d2;
                    toX = dx;
                    toY = dy;
                    found = true;
                }
            }
            if (!found)
            {
                return 0.0f;
            }
            float len = (float)Math.Sqrt(nearest);
            if (len < 1e-6f)
            {
                return 0.0f;
            }
            toX /= len;
            toY /= len;
            // The two perpendiculars; take the stronger alignment of the two so the term is
            // symmetric -- sidestepping left and right are equally lateral.
            float hx = (float)Math.Cos(r.HeadingRadians);
            float hy = (float)Math.Sin(r.HeadingRadians);
            float a = Math.Abs((hx * -toY) + (hy * toX));
            return a * a;   // squared so that a nearly-radial move scores near zero
        }

        /// <summary>
        /// `dot(heading, the line to the nearest threat)` squared -- how AXIAL the move is. The
        /// missing twin of <see cref="Lateralness"/>, and the one the task is actually about.
        ///
        /// WHY A PENALTY ON AXIS AND NOT A BONUS ON SIDESTEP. `Lateralness` already rewards moving
        /// across the nearest threat, and it distributes over the ring: a move that is perfectly
        /// lateral scores 1, an axial one scores 0. On its own that should be enough, and it is not,
        /// because of what the axial directions are. `HANDOFF-DIRECTED-DODGE.md` section 3 names both:
        /// OUTRUN is along the line away from the muzzle and TOWARD is along it into the muzzle, and
        /// **they are the same direction to `Lateralness`, which scores them both zero.** Worse, the
        /// model does not treat them the same: `resolve` and `clearance` pay for the retreat, because
        /// a retreat really does increase the distance to a bullet's corridor, while a move toward the
        /// muzzle clears nothing. So the retreat wins on safety and the walk-into-the-shooter wins on
        /// the goal, and the dodge oscillates between two mistakes that are one mistake.
        ///
        /// THIS TERM IS SYMMETRIC IN THE TWO AXIAL DIRECTIONS AND THAT IS THE POINT. It cannot prefer
        /// "toward" over "away" or the reverse, so it cannot trade one defect for the other -- which is
        /// exactly what raising `WeightGoal` does (section 15.3: OUTRUN 30.5 % -> 24.4 % while TOWARD
        /// rises 20.7 % -> 29.8 %). It removes the axial band from the menu and leaves the ring's
        /// sixty degrees of sidestep either side to be decided by the goal and the safety terms.
        /// </summary>
        private static float Axialness(DodgeSnapshot snapshot, PlayerView player, CandidateResult r)
        {
            if (r.Distance <= 1e-6f)
            {
                return 0.0f;   // `stand` has no axis, which is what `stand` is for
            }
            float nearest = float.MaxValue;
            float toX = 0.0f, toY = 0.0f;
            bool found = false;
            for (int i = 0; i < snapshot.Threats.Count; i++)
            {
                Trajectory t = snapshot.Threats[i];
                if (t.Count <= 0)
                {
                    continue;
                }
                float dx = t.Xs[0] - player.X;
                float dy = t.Ys[0] - player.Y;
                float d2 = (dx * dx) + (dy * dy);
                if (d2 < nearest)
                {
                    nearest = d2;
                    toX = dx;
                    toY = dy;
                    found = true;
                }
            }
            if (!found)
            {
                return 0.0f;
            }
            float len = (float)Math.Sqrt(nearest);
            if (len < 1e-6f)
            {
                return 0.0f;   // standing on the muzzle: there is no line, so there is no axis
            }
            toX /= len;
            toY /= len;
            float hx = (float)Math.Cos(r.HeadingRadians);
            float hy = (float)Math.Sin(r.HeadingRadians);
            float a = (hx * toX) + (hy * toY);
            return a * a;   // squared, so the squaring matches `Lateralness`'s and the two sum to 1
        }

        private static float Dot(float aRadians, float bRadians)
        {
            return ((float)Math.Cos(aRadians) * (float)Math.Cos(bRadians))
                 + ((float)Math.Sin(aRadians) * (float)Math.Sin(bRadians));
        }

        private int FindStand()
        {
            for (int i = 0; i < _resultCount; i++)
            {
                if (_results[i].Kind == 1)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// The first admissible candidate that actually MOVES the character, or -1 when the only
        /// admissible answers are zero-length ones.
        ///
        /// IT ASKS ABOUT `Distance`, NOT ABOUT `Kind`. The candidate set is built from `Add(kind,
        /// heading, distance)`, and the zero-length answers come in several kinds -- `stand` and
        /// an intent point that happens to coincide with the player's position both land at
        /// distance zero. A test on `Kind != 1` would call that second one a move and would let
        /// this branch report "something can carry them" on a frame where nothing can, which is
        /// exactly the class of mistake this whole document is about: a quantity that reads as
        /// present and is not.
        ///
        /// The answer is used twice, and both uses are why it returns an index rather than a
        /// boolean: the caller needs to know WHETHER an admissible move exists (to choose between
        /// the walk-on and the ordinary ladder), and the reader of the dump benefits from the
        /// same question being asked in one place. The scan is over `_resultCount` (77 at most)
        /// and runs only on frames that reached the `stand` branch, which is where the old code
        /// returned immediately.
        /// </summary>
        private int FirstAdmissibleMove()
        {
            for (int i = 0; i < _resultCount; i++)
            {
                if (_results[i].Admissible && _results[i].Distance > 1e-6f)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// The candidate that carries the held heading, for the hysteresis to re-derive its score
        /// from -- the BEST-scoring admissible one, not the first that points that way.
        ///
        /// THE TIE-BREAK WAS `>` AND IT WAS THE WHOLE OF THE `scale 0.35` PROBLEM. The ring holds
        /// THREE radii (`RadiusNear` 0.35, `RadiusMid` 0.7, `RadiusFar` 1.0) at each of the 24
        /// headings, so every direction is three candidates with the same `HeadingRadians` and
        /// therefore the same dot product. `>` kept the first maximum, and the loop is ordered
        /// near -> mid -> far, so a held heading was ALWAYS re-derived at the near radius -- the
        /// lowest-scoring of its three.
        ///
        /// That is not a stylistic wart, because the hysteresis compares that re-derived score
        /// against the frame's best. Measured on `20261015-153805-p26156`, by instrumenting the
        /// comparison itself: **on 395 of the 1329 frames that reached it with an admissible held
        /// candidate, the held heading and the best candidate's heading were THE SAME ONE** -- and
        /// the comparison still saw a penalty (median 0.88, p90 2.65) purely because the best was
        /// the far candidate at that heading while the hold was scored as the near one. That is
        /// larger than the 0.12 margin, so the hold broke on a difference that was not a
        /// disagreement about direction at all.
        ///
        /// The measured result was total: of the 1102 hysteresis-held moves in that session,
        /// **1102 were at `scale 0.35`** -- the dodge held for its dwell at a third of a step,
        /// repeatedly, which is `HANDOFF-DIRECTED-DODGE.md` section 1.3's "a dodge that has
        /// stopped walking". The heading hysteresis was never the `scale 0.35` problem; the
        /// re-derivation was.
        ///
        /// IT DOES NOT STRENGTHEN THE HYSTERESIS, WHICH SECTION 10.4 MEASURED AND REVERTED. The
        /// margin, the dwell and the directional rule are untouched: this only makes the number the
        /// margin is compared against the same kind of number the margin is compared to. A hold is
        /// still dropped the moment a real alternative beats this heading, and still dropped
        /// immediately when this heading stops being admissible -- which is why admissibility is
        /// preferred here and `Hysteresis.Keep`'s safety clause still fires on the deflated
        /// fallback when nothing at this heading was admitted.
        /// </summary>
        private int FindHeading(float heading)
        {
            int best = -1;
            float bestDot = -2.0f;
            bool bestAdmissible = false;
            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < _resultCount; i++)
            {
                if (_results[i].Kind != 2)
                {
                    continue;
                }
                float d = Dot(_results[i].HeadingRadians, heading);
                if (d < bestDot)
                {
                    continue;
                }

                bool admissible = _results[i].Admissible;
                float score = _results[i].Score;
                bool better;
                if (d > bestDot)
                {
                    better = true;
                }
                else if (admissible != bestAdmissible)
                {
                    // Same direction, and one of the two is admissible: prefer the admissible
                    // one. A refusal here would trip `Keep`'s safety clause and drop a hold this
                    // heading has not actually lost.
                    better = admissible;
                }
                else
                {
                    better = admissible && score > bestScore;
                }

                if (better)
                {
                    bestDot = d;
                    best = i;
                    bestAdmissible = admissible;
                    bestScore = score;
                }
            }
            return best;
        }

        private int BestAdmissible()
        {
            int best = -1;
            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < _resultCount; i++)
            {
                if (!_results[i].Admissible)
                {
                    continue;
                }
                if (_results[i].Score > bestScore)
                {
                    bestScore = _results[i].Score;
                    best = i;
                }
            }
            return best;
        }

        private float RunnerUp(int chosen)
        {
            float best = float.NegativeInfinity;
            for (int i = 0; i < _resultCount; i++)
            {
                if (i == chosen || !_results[i].Admissible)
                {
                    continue;
                }
                if (_results[i].Score > best)
                {
                    best = _results[i].Score;
                }
            }
            return best;
        }

        // `BestByDurability` USED TO SIT HERE, and its removal is the record of a defect rather
        // than a tidy-up. It existed to answer section 5.7 step 6 -- "nothing is safe, take the
        // option that maximises time-to-impact" -- by scanning the whole candidate array, including
        // the entries the admission rules had REFUSED, and emitting the best of them. That is the
        // one thing the design forbids: the admissible set is the safety boundary, and a refused
        // candidate is one that ends inside a disc or through a wall. It produced four T6 failures
        // in the dodge scenario, every one of them inside the incoming bullet's arrival window.
        // The step-6 answer is now "no admissible move: report it and keep the escape armed".

        // ===================================================================================
        // Stage 7 -- the escape gate (section 7.2, gate 6)
        // ===================================================================================

        /// <summary>
        /// The threats that intersect EVERY admissible candidate's swept path: **damage that
        /// cannot be dodged**.
        ///
        /// THE DEFINITION IS THE FEATURE. It is not "a lot of damage is coming" -- it is "there
        /// is no move on the menu that avoids this one", which is exactly the asymmetry section
        /// 7.3 weighs: escaping when it was not needed costs a nexus scroll and a run, failing to
        /// escape when it was needed costs the character. Only the second is fatal, so the gate
        /// is deliberately narrow.
        /// </summary>
        private int CountUnavoidable(DodgeSnapshot snapshot, PlayerView player, float playerRadius,
                                     float budget, DodgeDecision decision)
        {
            int unavoidable = 0;
            double damage = 0.0;

            for (int t = 0; t < snapshot.Threats.Count; t++)
            {
                Trajectory threat = snapshot.Threats[t];
                if (threat.Count <= 0)
                {
                    continue;
                }

                bool everyCandidateHit = true;
                bool anyCandidate = false;
                float r = playerRadius + threat.Radius;
                float r2 = r * r;

                // The candidates that were admitted, plus the one currently chosen. A threat that
                // hits all of them is unavoidable *given the admissible set*, which is the
                // honest formulation: a threat that hits everything except a move the terrain
                // refused cannot be dodged either.
                //
                // AND IT IS THE PLAN WALK, OVER THE FULL TRUST PREFIX. This is the one place the
                // lookahead is the whole horizon rather than the impact window, and that is the
                // gate's own definition: "no move on the menu avoids this one" has to mean for as
                // long as the model is trusted, not for as long as this frame is spending. The
                // sweep that used to be rebuilt here per candidate is gone with the path model.
                for (int i = 0; i < _resultCount; i++)
                {
                    if (!_results[i].Admissible)
                    {
                        continue;
                    }
                    anyCandidate = true;
                    float danger = PlanDangerFull(threat, player, _results[i].X, _results[i].Y, r2);
                    if (danger < float.MaxValue)
                    {
                        continue;
                    }
                    everyCandidateHit = false;
                    break;
                }

                if (anyCandidate && everyCandidateHit)
                {
                    unavoidable++;
                    damage += threat.Damage
                              + (player.Health * threat.DamagePercent)
                              + threat.ManaDamage;
                }
            }

            decision.ProjectedUnavoidableDamage = damage;
            return unavoidable;
        }

        /// <summary>The plan walk over the FULL trust prefix, for the escape projection.</summary>
        private float PlanDangerFull(Trajectory threat, PlayerView player, float cx, float cy,
                                     float r2)
        {
            float frameMs = _frameSeconds * 1000.0f;
            float vxPerMs = frameMs > 0.0001f ? (cx - player.X) / frameMs : 0.0f;
            float vyPerMs = frameMs > 0.0001f ? (cy - player.Y) / frameMs : 0.0f;
            int trusted = threat.TrustedCount(_config.TrustSeconds);
            PlanWalk(threat, player.X, player.Y, vxPerMs, vyPerMs, r2, trusted,
                     _config.TrustSeconds * 1000.0f,
                     out float _, out float _, out float dangerMs);
            return dangerMs;
        }

        // ===================================================================================
        // Geometry
        // ===================================================================================

        /// <summary>Squared distance from a point to a segment. The workhorse of section 5.5.</summary>
        public static float PointSegmentDistanceSquared(float px, float py,
                                                        float ax, float ay, float bx, float by)
        {
            float vx = bx - ax;
            float vy = by - ay;
            float wx = px - ax;
            float wy = py - ay;
            float len2 = (vx * vx) + (vy * vy);
            if (len2 <= 1e-12f)
            {
                return (wx * wx) + (wy * wy);
            }
            float t = ((wx * vx) + (wy * vy)) / len2;
            if (t < 0.0f) { t = 0.0f; }
            if (t > 1.0f) { t = 1.0f; }
            float dx = wx - (t * vx);
            float dy = wy - (t * vy);
            return (dx * dx) + (dy * dy);
        }

        // `SegmentDistanceSquared` AND ITS `SegmentsIntersect`/`Cross` HELPERS STOOD HERE, AND THEY
        // ARE DELETED RATHER THAN LEFT UNUSED. They were the path-model geometry: the
        // segment-against-segment test that section 5.5 describes, which the solver no longer asks
        // because two polylines were the wrong shape for the question (SPIKE3.2-OFFLINE-SOLVER.md
        // section 11.1). Leaving them would have left a second, older answer to "when do these two
        // collide" in the same file as the one the solver uses -- and the whole finding of section
        // 11 is that the older answer was wrong in a way that looked reasonable.

        /// <summary>For the harness: every candidate from the last decision, copied out.</summary>
        public void CopyLastCandidates(List<CandidateResult> into)
        {
            into.Clear();
            for (int i = 0; i < _resultCount; i++)
            {
                into.Add(_results[i]);
            }
        }

        /// <summary>
        /// For the harness and the self-test: the first admission rule that refuses a move to
        /// `(x, y)` this frame, or <see cref="AdmissionRule.Admitted"/>.
        ///
        /// IT EXISTS SO THAT A TEST CAN ASK THE SOLVER THE QUESTION THE SOLVER ASKS ITSELF. T5 --
        /// "`stand` is never rejected while durably safe" -- is a statement about the solver's own
        /// admission of the zero-length candidate, and the first version of the harness re-derived
        /// it with its own copy of the predicate. Two nearly identical predicates did what two
        /// nearly identical predicates always do: they disagreed on the frames where the answer was
        /// close, and the disagreement was reported as a solver failure. The remedy is not a
        /// tolerance; it is one implementation, asked twice.
        /// </summary>
        public int AdmissionOf(DodgeSnapshot snapshot, float x, float y)
        {
            if (snapshot == null || snapshot.Stale)
            {
                return (int)AdmissionRule.NoBudget;
            }
            float playerRadius = snapshot.Player.Radius > 0.0f
                ? snapshot.Player.Radius
                : _config.DefaultPlayerRadius;
            float budget = snapshot.Player.SpeedPerSecond * snapshot.FrameSeconds;
            _nowMs = snapshot.ClockMs;
            _frameSeconds = snapshot.FrameSeconds > 0.0f ? snapshot.FrameSeconds : (1.0f / 60.0f);
            return Admit(snapshot, snapshot.Player, playerRadius, budget, x, y,
                         out float _, out float _, out float _);
        }

        /// <summary>
        /// For the harness: whether the solver's own fixed-point durability test is satisfied at
        /// `(x, y)` -- the predicate behind "the best dodge is usually no dodge".
        /// </summary>
        public bool IsDurablySafeAt(DodgeSnapshot snapshot, float x, float y)
        {
            if (snapshot == null)
            {
                return false;
            }
            float playerRadius = snapshot.Player.Radius > 0.0f
                ? snapshot.Player.Radius
                : _config.DefaultPlayerRadius;
            return StaticallyDurable(snapshot, snapshot.Player, x, y, playerRadius);
        }
    }
}
