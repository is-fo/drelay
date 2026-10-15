// Program.cs -- Spike 3.2's offline harness.
//
// WHAT THIS PROGRAM IS FOR, IN ONE SENTENCE: it runs the shipped solver against recorded or
// synthetic frames and tries to prove it wrong.
//
// The four things it asserts are DODGE-ALGORITHM.md section 10's invariants 1, 2, 3 and 4 --
// T5, T6, T10 and T7 in IMPLEMENTATION-PLAN.md's table -- because those are the four the plan
// says belong here and nowhere else:
//
//   T5    `stand` is never rejected while durably safe        "the dodge moving for no reason"
//   T6    no emitted move is inside a bullet's disc at arrival  "the core safety invariant"
//   T10   hysteresis cannot lock a heading that became unsafe   "anti-jitter turning into a death"
//   T7    predicted and actual collision agree                  "the model being wrong"
//
// T7 IS THE ONE THAT NEEDED A LIVE RUN, AND IT IS WHY TraceRecorder EXISTS. The other three are
// properties of the solver's own geometry and are asserted against synthetic frames; T7 is a
// statement about the world and can only be checked against frames somebody actually played.
//
// WHAT THIS PROGRAM DELIBERATELY DOES NOT DO. It does not re-implement the solver, it does not
// approximate the temporal test, and it does not "fix up" a frame it does not understand. Every
// number it prints comes from `DodgeSolver.Decide` and from the trace; a harness with its own
// idea of what the answer should be is a second solver, and the project has one.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Drelay.Bootstrap;

namespace Drelay.SolverHarness
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAILED: " + ex.GetType().Name + ": " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return 2;
            }
        }

        private static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                Usage();
                return 2;
            }

            string tracePath = null;
            string collisionLog = null;
            string scenarioName = null;
            string exportPath = null;
            string decisionsPath = null;
            string playerSpeedText = null;
            int seed = 1;
            int frames = 600;
            bool quiet = false;
            string configArgs = null;
            string presetName = null;
            int traceFrame = -1;
            int repeat = 1;
            bool strict = false;
            int debugThreatId = -1;
            System.Collections.Generic.HashSet<int> framesToExplain = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--trace": tracePath = Next(args, ref i); break;
                    case "--collisions": collisionLog = Next(args, ref i); break;
                    case "--scenario": scenarioName = Next(args, ref i); break;
                    case "--seed": seed = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--frames": frames = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--export": exportPath = Next(args, ref i); break;
                    case "--decisions": decisionsPath = Next(args, ref i); break;
                    case "--player-speed": playerSpeedText = Next(args, ref i); break;
                    case "--config": configArgs = Next(args, ref i); break;
                    case "--preset": presetName = Next(args, ref i); break;
                    case "--frame": traceFrame = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--frames-from":
                    {
                        // One frame number per line, `#` comments and blanks skipped. See
                        // `FramesToExplain` for why a set of frames needs a switch of its own.
                        string listPath = Next(args, ref i);
                        framesToExplain = new System.Collections.Generic.HashSet<int>();
                        foreach (string raw in System.IO.File.ReadAllLines(listPath))
                        {
                            string trimmed = raw.Trim();
                            if (trimmed.Length == 0 || trimmed[0] == '#')
                            {
                                continue;
                            }
                            int parsed;
                            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                             out parsed))
                            {
                                framesToExplain.Add(parsed);
                            }
                        }
                        break;
                    }
                    case "--repeat": repeat = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--strict": strict = true; break;
                    case "--debug-threat": debugThreatId = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--quiet": quiet = true; break;
                    case "-h":
                    case "--help":
                        Usage();
                        return 0;
                    default:
                        if (a.StartsWith("--scenario=", StringComparison.Ordinal))
                        {
                            scenarioName = a.Substring("--scenario=".Length);
                        }
                        else
                        {
                            Console.Error.WriteLine("unknown argument '" + a + "'");
                            Usage();
                            return 2;
                        }
                        break;
                }
            }

            // The preset first, then `--config`, for the same reason the hook applies them in that
            // order: a `--config` line is meant to move ONE number from a named starting point.
            DodgeConfig config = DodgeConfig.Preset(presetName);
            if (!string.IsNullOrEmpty(configArgs))
            {
                ApplyConfig(configArgs, ref config);
            }

            DodgeTrace trace;
            if (!string.IsNullOrEmpty(tracePath))
            {
                trace = DodgeTrace.Load(tracePath, collisionLog);
                if (!quiet)
                {
                    Console.WriteLine("trace    : " + tracePath
                                      + " (" + trace.Frames.Count + " frame(s), "
                                      + trace.Grids.Count + " grid window(s))");
                    if (!string.IsNullOrEmpty(trace.DodgeConfig))
                    {
                        Console.WriteLine("recorded : the hook's dodge settings were " + trace.DodgeConfig);
                    }
                }
            }
            else if (!string.IsNullOrEmpty(scenarioName))
            {
                Scenario kind = ParseScenario(scenarioName);
                var synthetic = new SyntheticTrace(kind, seed)
                {
                    Frames = frames,
                    // The scenarios whose point is that the solver must react to a bullet keep the
                    // player still; the ones whose point is that it must NOT react let them walk.
                    PlayerWalking = kind != Scenario.HeadOn && kind != Scenario.Dodge
                                    && kind != Scenario.Volley && kind != Scenario.Corridor,
                };
                trace = synthetic.Build();
                if (!quiet)
                {
                    Console.WriteLine("scenario : " + kind + " (seed " + seed + ", "
                                      + trace.Frames.Count + " frame(s))");
                }
                if (!string.IsNullOrEmpty(exportPath))
                {
                    synthetic.WriteJsonLines(trace, exportPath);
                    if (!quiet)
                    {
                        Console.WriteLine("exported : " + exportPath);
                    }
                }
            }
            else
            {
                Console.Error.WriteLine("either --trace or --scenario is required");
                Usage();
                return 2;
            }

            if (!string.IsNullOrEmpty(playerSpeedText))
            {
                float speed = float.Parse(playerSpeedText, CultureInfo.InvariantCulture);
                foreach (RecordedFrame f in trace.Frames)
                {
                    f.SpeedOverride = speed;
                }
            }

            var harness = new Harness(trace, config);
            harness.FrameToExplain = traceFrame;
            harness.FramesToExplain = framesToExplain;
            harness.ExpectMove = string.Equals(scenarioName, "dodge", StringComparison.OrdinalIgnoreCase);
            harness.ExpectFrames = !string.IsNullOrEmpty(tracePath);
            harness.Repeat = repeat;
            harness.Strict = strict;
            harness.DebugThreatId = debugThreatId;
            harness.DecisionsPath = decisionsPath;
            harness.Run(!quiet);
            return harness.Failures == 0 ? 0 : 1;
        }

        private static string Next(string[] args, ref int i)
        {
            i++;
            if (i >= args.Length)
            {
                throw new ArgumentException("missing value after " + args[i - 1]);
            }
            return args[i];
        }

        private static Scenario ParseScenario(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "peaceful": return Scenario.Peaceful;
                case "headon":
                case "head-on": return Scenario.HeadOn;
                case "dodge": return Scenario.Dodge;
                case "volley": return Scenario.Volley;
                case "corridor": return Scenario.Corridor;
                case "passingby":
                case "passing-by": return Scenario.PassingBy;
                default:
                    throw new ArgumentException("unknown scenario '" + name + "'");
            }
        }

        /// <summary>
        /// `name=value,name=value` over the tuning weights, so a weight can be swept from the
        /// command line without editing the source -- which is what "tune in the harness" has to
        /// mean concretely.
        /// </summary>
        /// <summary>
        /// Applies `--config` on top of the preset.
        ///
        /// `ref`, AND THE `ref` IS THE WHOLE OF A DEFECT THAT SURVIVED FROM THE DAY THIS METHOD WAS
        /// WRITTEN. `DodgeConfig` is a struct, so the parameter without `ref` was a COPY: every
        /// assignment below landed in a temporary that was discarded on return, `--config` silently
        /// did nothing, and the harness printed the preset's numbers as though they were the ones it
        /// had just been asked for. It was found by trying to measure whether one new term mattered:
        /// two runs of `--config goal=0,fan=0` produced byte-identical decision histograms, which is
        /// what a switch that does not throw and does not work looks like. It is the same defect as
        /// SPIKE3.2-OFFLINE-SOLVER.md section 3.2 -- a struct passed by value whose writes are lost --
        /// in the instrument rather than in the solver, and it is the reason that section's lesson
        /// is about calling conventions and not about arithmetic.
        /// </summary>
        private static void ApplyConfig(string text, ref DodgeConfig config)
        {
            foreach (string pair in text.Split(','))
            {
                if (pair.Length == 0)
                {
                    continue;
                }
                string[] kv = pair.Split('=');
                if (kv.Length != 2)
                {
                    throw new ArgumentException("--config wants name=value pairs; got '" + pair + "'");
                }
                float v = float.Parse(kv[1], CultureInfo.InvariantCulture);
                switch (kv[0].ToLowerInvariant())
                {
                    case "clear": config.WeightClearance = v; break;
                    case "resolve": config.WeightResolve = v; break;
                    case "standstops": config.StandStops = v != 0.0f; break;
                    case "durable": config.WeightDurability = v; break;
                    case "intent": config.WeightIntent = v; break;
                    case "goal": config.WeightGoal = v; break;
                    case "plan": config.PlanLookaheadSeconds = v; break;
                    case "front": config.GoalLookaheadSeconds = v; break;
                    case "fan": config.GoalFanSteps = (int)v; break;
                    // THE DECISION WINDOW, IN THE TWO FORMS `DodgeConfig` CARRIES. `disc` is the
                    // multiplier on this projectile's own disc-crossing time and is the knob that
                    // matters; `window` is the absolute floor under it. `disc=0` is legal and means
                    // "no speed scaling", which is how the change is A/B'd against the recording --
                    // `--config disc=0,window=0.05` is exactly the old fixed window.
                    case "disc": config.DecisionDiscMultiplier = v; break;
                    case "window": config.MoveTemporalWindowSeconds = v; break;
                    case "heading": config.WeightHeading = v; break;
                    case "lateral": config.WeightLateral = v; break;
                    case "move": config.WeightMove = v; break;
                    case "stand": config.WeightStandBias = v; break;
                    case "margin": config.HysteresisMargin = v; break;
                    case "dwell": config.HysteresisDwellSeconds = v; break;
                    // THE PATHFINDER, AND `path=0` IS THE A/B THE WHOLE CHANGE RESTS ON. With it off
                    // the score is the pre-path one and the goal term has to be put back by hand
                    // (`--config path=0,goal=0.9`), because `DodgeConfig.Default` cuts `goal` when the
                    // path is on and a comparison against the old behaviour has to restore both
                    // halves of the same change.
                    case "path": config.PathEnable = v != 0.0f; break;
                    case "wpath": config.WeightPath = v; break;
                    case "pathdel": config.PathStepCostWeight = v; break;
                    case "pathcap": config.PathNodeCap = (int)v; break;
                    case "pathpad": config.PathDangerPadUnits = v; break;
                    case "pathtarget": config.PathTargetSeconds = v; break;
                    case "pathdwell": config.PathHysteresisDwellSeconds = v; break;
                    case "pathwp": config.PathWaypointUnits = v; break;
                    // THE PROJECTED-DANGER TERM. `projected=0` is the off-switch and restores the
                    // section 15 behaviour exactly, which is the A/B the next measurement needs.
                    case "projected": config.WeightProjectedDanger = v; break;
                    // THE AXIAL PENALTY -- the term this task is about. `axial=0` is the off-switch.
                    case "axial": config.WeightAxial = v; break;
                    case "projseconds": config.ProjectedSeconds = v; break;
                    case "projsamples": config.ProjectedSamples = (int)v; break;
                    case "wpfraction": config.PathWaypointFraction = v; break;
                    case "horizon": config.HorizonMs = v; break;
                    case "trust": config.TrustSeconds = v; break;
                    case "legalern": config.LethalFraction = v; break;
                    default:
                        throw new ArgumentException("unknown config key '" + kv[0] + "'");
                }
            }
        }

        private static void Usage()
        {
            Console.Error.WriteLine(@"SolverHarness -- Spike 3.2: run the dodge solver over recorded frames.

  --trace PATH        a trace.jsonl written by DRELAY_TRACE=1
  --collisions PATH   the run's bootstrap.log, for the predicted-vs-actual half of T7
  --scenario NAME     synthetic frames instead: peaceful | headon | volley | corridor | passingby
  --seed N            synthetic scenario seed (default 1)
  --frames N          synthetic frame count (default 600)
  --export PATH       write the synthetic trace in the recorder's format, for the reader's sake
  --player-speed V    world units/s, overriding the measured and derived estimates
  --preset NAME       a named, coherent tuning. Three, and they are meant to be compared:
                        balanced  the specification's weights and the specification's `stand`
                        assist    never stops the player; steers toward where they were going
                        evasive   safety first, willing to fight the player for it
                      Applied BEFORE --config, so `--preset evasive --config move=0.4` moves one
                      number from the preset rather than starting again from the default.
  --config K=V,...    override tuning weights and the plan geometry:
                      resolve,clear,durable,intent,goal,heading,lateral,move,stand,margin,dwell,
                      horizon,trust,lethalfraction,standstops,plan,front,fan,disc,window
                      disc   the rule 6 decision window as a multiple of the projectile's own
                             collision-disc crossing time (default 2.0). disc=0 turns the speed
                             scaling off and runs the rule on `window` alone.
                      window an absolute floor in seconds under that window (default 0).
  --quiet             suppress the per-part headings
  --repeat N          run the decision N times per frame and keep the FASTEST, for the cost
                      number. Single-shot `decide()` measures the collector as much as the
                      solver; use --repeat 50 before believing a p99.
  --decisions PATH    write one line per frame: the frame, the decision, the player's held
                      direction, the goal, and where the goal came from. This is the instrument
                      HANDOFF-DIRECTED-DODGE.md section 6.10 asks for -- without it the only view
                      of a per-frame decision is the adapter's 400-line `WOULD MOVE` log, which is
                      capped at 400 lines a session, so every experiment is a mean over a sample
                      rather than a diff of two files. See tools/decisions_report.py.
  --strict            treat the trace reader's warnings (a half-written last line, an unknown
                      record type) as a failure instead of a report line.
  --debug-threat ID   dump every recorded observation of the bullet with this GameId, with the
                      distance from its samples to the player on each frame. For T7 disagreements.

Exit codes: 0 = every invariant held, 1 = an invariant failed, 2 = usage or input problem.");
        }
    }

    /// <summary>
    /// The replay. One solver instance across the whole trace, because the hysteresis and the
    /// previous heading are state the solver is *supposed* to carry between frames -- running a
    /// fresh solver per frame would test T5/T6 and quietly not test T10.
    /// </summary>
    internal sealed class Harness
    {
        private readonly DodgeTrace _trace;
        private readonly DodgeConfig _config;
        private readonly DodgeSolver _solver;

        public int Failures;

        /// <summary>
        /// When set, the report asserts that the solver moved, and fails if it did not.
        ///
        /// WHY THIS IS A FLAG AND NOT A PROPERTY OF EVERY RUN. Three of the four invariants are
        /// satisfied by a solver that never moves, so a suite made only of them cannot tell a
        /// working dodge from a disabled one -- and "it passed" would then mean "it did nothing".
        /// The flag is set for the one scenario whose geometry guarantees that not moving is the
        /// wrong answer, and the failing message says so.
        /// </summary>
        public bool ExpectMove;

        /// <summary>
        /// When set, the report fails if the trace contained no frames at all.
        ///
        /// THIS IS THE ASSERTION THAT WOULD HAVE SAVED A RUN, AND ITS ABSENCE IS WHY A BROKEN
        /// RECORDING LOOKED LIKE A CLEAN ONE. Every invariant is a statement of the form "for every
        /// frame, ...", and a report over zero frames satisfies all of them vacuously -- so the
        /// harness printed "every asserted invariant held" over a trace whose only content was its
        /// header. The run of `20261014-164314-p12060` did exactly that: the recorder was armed but
        /// the game's frame path took the native detour instead of the managed replacement, so the
        /// recorder was never called.
        ///
        /// **A test suite that passes on no data is worse than one that fails**, because it is
        /// believed. This flag makes an empty recording a failure with a named cause.
        /// </summary>
        public bool ExpectFrames;

        /// <summary>
        /// When set, one frame's whole candidate evaluation is printed.
        ///
        /// THIS EXISTS BECAUSE A SOLVER THAT REFUSES EVERYTHING LOOKS LIKE A SOLVER THAT FOUND NO
        /// THREAT. The aggregate report says "0 admissible" and nothing about which of the seven
        /// rules did the refusing, and the difference between "the geometry is wrong" and "the
        /// geometry is right and the world is genuinely closed" is not visible from a count. One
        /// frame printed in full is worth an hour of reading the code.
        /// </summary>
        public int FrameToExplain = -1;

        /// <summary>
        /// A SET of frames to explain, from `--frames-from`, for the term breakdown.
        ///
        /// `--frame N` EXPLAINS ONE FRAME AND A MEASUREMENT NEEDS THOUSANDS. `tools/dodge_term_report.py`
        /// aggregates the chosen move's score terms by the direction it chose, and the dodge moves on
        /// 1 100 to 5 000 frames of each recording -- so the alternative to this is one process per
        /// frame, each one re-parsing a 7 MB trace, which is a measurement nobody runs twice.
        /// </summary>
        public System.Collections.Generic.HashSet<int> FramesToExplain;

        /// <summary>
        /// Where to write one line per frame -- the whole decision, joined to the player's input.
        ///
        /// THIS IS THE INSTRUMENT, AND ITS ABSENCE WAS THE REASON EVERY EXPERIMENT IN THIS TASK
        /// WAS A MEAN RATHER THAN A DIFF. The per-frame decision (heading, scale, reason, goal, and
        /// where the goal came from) exists on a live run only in `DodgeAdapter`'s `dodge WOULD
        /// MOVE` log, which is **capped at 400 lines a session**; the replay that reads the whole
        /// recording reports only histograms. So "did the goal move to the player's direction, and
        /// did the deflection decay" could be answered as "the mean fell from 47.4 deg to X" and
        /// not as "here are the 1665 frames, before and after, side by side" -- and a mean over a
        /// 400-line sample of 1665 moves is the weakest form of the evidence this project accepts.
        ///
        /// The fields are chosen so that the two questions the handoff asks are answerable from the
        /// file alone: `held`/`hintent` are the player's own request (so the deflection is
        /// `signed(heading - hintent)` and needs nothing else), and `goalsrc` says where the goal
        /// came from on that frame -- which is the one fact that makes "the goal is the course" and
        /// "the goal is the player" distinguishable after the change.
        /// </summary>
        public string DecisionsPath;

        /// <summary>
        /// How many times `Decide` is run per frame for the cost measurement. 1 is the honest
        /// single-shot number, and it is not the number to tune against.
        ///
        /// WHY REPEATING A PURE FUNCTION IS THE ONLY WAY TO MEASURE IT HERE. The harness builds a
        /// fresh `DodgeSnapshot` for every frame -- correctly, because the game hands the solver
        /// one plain-data view per frame and the solver must not depend on object identity -- and
        /// that is hundreds of small allocations a frame. A single `Stopwatch` around one `Decide`
        /// therefore measures the solver *plus whichever garbage collection happens to land in
        /// that window*, and the measurement shows it: the reported p99 moved from 890 us to 746 to
        /// 737 as work was REMOVED from the solver, and forcing server GC moved it to 516 on its
        /// own. A number that responds to the collector and not to the algorithm is not a
        /// measurement of the algorithm.
        ///
        /// With `--repeat N` the decision is run N times on the same snapshot and the MINIMUM is
        /// kept: the solver is a pure function of the snapshot except for its own hysteresis, which
        /// is bounded state, so the fastest of N runs is the one with the least interference and
        /// the best available estimate of the true cost. Nothing downstream changes -- the
        /// decision reported, the invariants and the T7 tracking all still use the first run.
        /// </summary>
        public int Repeat = 1;

        /// <summary>
        /// Makes the reader's warnings fatal. Off by default, and the default is a judgement: a
        /// half-written last line is what closing the game by hand produces, not a defect in the
        /// recording, and refusing to analyse 2173 good frames over 40 lost bytes would be the
        /// wrong trade. On for a CI-shaped run, where "the file is not quite what was promised"
        /// should stop the line rather than print a paragraph nobody reads.
        /// </summary>
        public bool Strict;

        /// <summary>
        /// A bullet key (`GameId`) to dump in full: for every frame the trace records it, the
        /// sampler's own radius, the samples' distances to the player's recorded position on that
        /// frame, and whether any sample was ever inside. Added to settle why
        /// `TrackPredictions` records no prediction for a bullet the game resolved as a hit.
        /// </summary>
        public int DebugThreatId = -1;

        private readonly List<string> _debugThreatLines = new List<string>();
        private float _debugThreatBest = float.MaxValue;
        private int _debugThreatBestFrame = -1;
        private bool _debugThreatEverInside;
        private float _debugThreatRadius = float.NaN;

        // --- Accumulators ---------------------------------------------------------------

        private readonly Dictionary<DodgeAction, int> _actions = new Dictionary<DodgeAction, int>();
        private readonly Dictionary<DodgeReason, int> _reasons = new Dictionary<DodgeReason, int>();
        private long _frames;
        private long _evaluatedTotal;
        private long _admissibleTotal;
        private long _framesWithCandidates;
        private long _hysteresisHolds;
        /// <summary>
        /// The dodge's OWN heading history -- the stutter measure. Contrast `_overridesIntent`.
        /// </summary>
        private long _adjacentMoves;
        private long _adjacentHeadingChanges;
        private double _turnDegrees;
        private float _lastChosenHeading = float.NaN;
        private int _lastChosenFrame = int.MinValue;

        /// <summary>Moving frames whose heading disagreed with the player's input by more than 90 deg.</summary>
        private long _overridesIntent;

        /// <summary>Moving frames whose heading pointed within 90 deg of the forward goal.</summary>
        private long _goalAligned;
        private long _goalConsidered;

        /// <summary>
        /// Every candidate refusal the run made, over every evaluated frame. The denominator for
        /// `_solver.WindowRefusals`, which counts only the ones rule 6's unconditional clause caused.
        /// </summary>
        private long _refusalsTotal;

        /// <summary>
        /// Moving frames whose heading pointed within 90 deg of the direction the player's hands
        /// were holding.
        ///
        /// THE MEASUREMENT THE OPERATOR'S SENTENCE ASKS FOR. `keeps its course` was the right
        /// question while the goal WAS the course; once the goal is the player's held direction
        /// (HANDOFF-DIRECTED-DODGE.md section 5.1) "its course" names the thing the change removed,
        /// and the two lines move in opposite directions by construction -- so the handoff asks for
        /// this one beside it rather than instead of it. It is `1 - overrides intent` restricted to
        /// the frames a goal existed on, and it is deliberately the same 90-degree test so the two
        /// numbers can be read against each other.
        /// </summary>
        private long _playerAligned;

        /// <summary>Reversals on the NEXT frame -- the ones an operator sees as a stutter.</summary>
        private long _adjacentReversals;
        private List<string> _adjacentReversalExamples = new List<string>();
        private long _moveFrames;

        /// <summary>Per-threat classification over the whole recording.</summary>
        private readonly Dictionary<ThreatVerdict, int> _verdicts =
            new Dictionary<ThreatVerdict, int>();
        private long _framesWithRelevantThreats;
        private long _framesWithADodgeableThreat;
        private long _framesNothingToDoAbout;

        private readonly List<float> _durabilityMs = new List<float>();
        private readonly List<float> _costUs = new List<float>();

        /// <summary>
        /// The same costs, for the frames that actually built and admitted a candidate set.
        ///
        /// WHY THE SPLIT IS NECESSARY TO READ THE BUDGET. Most frames are not a decision at all:
        /// `AnyThreatNearPlayer` answers no and the whole stage returns before a candidate exists.
        /// Those frames cost nothing, and averaging them in makes any solver look cheap -- while a
        /// p99 over all frames reports the *tail of the cheap frames* whenever the no-threat case
        /// is the majority. The recorded 1229-frame session is 1056 `NoThreat` frames and 173 that
        /// evaluate. `Decide`'s real cost is a property of the second set, so both are printed.
        /// </summary>
        private readonly List<float> _activeCostUs = new List<float>();

        // T5
        private int _standRejectedWhileDurable;
        private int _standRefusedByRules;
        private List<int> _standRejectedExample = new List<int>();

        // T6
        private int _movesInsideDiscAtArrival;
        private int _movesNotAdmissible;

        /// <summary>Moves that ended inside a disc the player was ALREADY in -- rule 4's exception.</summary>
        private int _movesEscapeIncomplete;

        private List<string> _moveViolationExamples = new List<string>();
        private List<string> _moveEscapeExamples = new List<string>();

        // T10
        private int _unsafeHold;
        private List<string> _holdViolationExamples = new List<string>();

        // T7
        private sealed class Prediction
        {
            public int Frame;
            public float PlayerX;
            public float PlayerY;
            public float PlayerRadius;
            public float Radius;
            public float ImpactMs;

            /// <summary>Trace frame index at which the predicted impact falls. Used for the fair test.</summary>
            public int ImpactFrame;
            public float MinDistance;

            /// <summary>
            /// The smallest distance between this bullet's path and the player over every frame in
            /// which the bullet was tracked, which is the number that decides whether a prediction
            /// that never became a hit was a false positive.
            /// </summary>
            public float ClosestEver;

            /// <summary>
            /// Whether the bullet's own recorded path still entered the player's disc at the frame
            /// the impact was predicted for, judged against **where the player actually was** -- not
            /// against where they were standing when the prediction was made.
            ///
            /// THIS IS THE FAIR TEST OF A PREDICTION, AND THE DIFFERENCE IS NOT A DETAIL. The model
            /// projects the bullet forward from the player's position at decision time and holds the
            /// player still; the player then walks for the 274 ms of notice the model gave. Judging
            /// the prediction against the original position asks "would this have hit a statue",
            /// which is a question nobody cares about. Judging it against the position the player
            /// actually reached asks the question that matters: **by the time the bullet arrived,
            /// was it still going to hit?** A prediction that only fails because the player left is
            /// a prediction the dodge would have acted on correctly.
            /// </summary>
            public bool HitAtImpactTime;

            /// <summary>
            /// The bullet was no longer being tracked at the frame it was predicted to arrive in,
            /// so it never got there. The most common shape of a false positive, and not the
            /// player's doing.
            /// </summary>
            public bool BulletGoneAtImpact;
        }

        private readonly Dictionary<long, Prediction> _firstPrediction = new Dictionary<long, Prediction>();
        private readonly Dictionary<long, int> _lastFrameSeen = new Dictionary<long, int>();
        private readonly Dictionary<long, int> _firstFrameSeen = new Dictionary<long, int>();
        private int _degradedFrames;

        public Harness(DodgeTrace trace, DodgeConfig config)
        {
            _trace = trace;
            _config = config;
            _solver = new DodgeSolver(config);
        }

        public void Run(bool verbose)
        {
            Console.WriteLine();
            Console.WriteLine("== Spike 3.2 offline harness ==");
            Console.WriteLine("solver   : " + _config.Describe());
            Console.WriteLine();

            float previousX = float.NaN;
            float previousY = float.NaN;
            float previousIntent = float.NaN;

            var candidates = new List<CandidateResult>();

            System.IO.StreamWriter decisions = null;
            if (!string.IsNullOrEmpty(DecisionsPath))
            {
                decisions = new System.IO.StreamWriter(DecisionsPath, false);
                decisions.WriteLine("# SolverHarness --decisions -- one line per recorded frame, in order.");
                // THE `# ` IS ADDED HERE RATHER THAN BY THE DESCRIBE, AND EVERY LINE OF IT. `Describe`
                // returns its lines without a comment marker because the hook's own log does not use
                // one -- the marker belongs to this file's format, and the version that assumed the
                // string came pre-marked wrote a bare config line into the middle of the dump, which
                // a reader that skips `#` lines then tried to parse as a frame with 26 fields.
                decisions.WriteLine("# " + _config.Describe().TrimEnd('\r', '\n').Replace("\n", "\n# "));
                decisions.WriteLine("# frame | action | reason | held | heading | scale | goal | goalsrc | "
                                  + "hintent | intent | course | moving | evaluated | admissible | "
                                  + "durabilityMs | clearance | resolve | score | runnerup | costUs | "
                                  + "windowMs | wp | wpdist | pathn | pathlen | pathcost | pathwhy | "
                                  + "stSetup | stPath | stGates | stAdmit | stScore | stEscape");
            }

            // JIT warm-up. The first `Decide` of a process compiles every method the decision
            // touches and is measured in milliseconds -- the harness reports it as `max`, and the
            // recorded session's 5683 us is that and not the solver. Those frames are still
            // analysed below with their real measurements; this loop only makes the *first*
            // measured frame stop being a compilation event.
            int warmupFrames = Math.Min(8, _trace.Frames.Count);
            for (int w = 0; w < warmupFrames; w++)
            {
                _solver.Decide(BuildSnapshot(_trace.Frames[w],
                                             ResolveSpeed(_trace.Frames[w], float.NaN, float.NaN),
                                             0.0f, false));
            }
            // THE WARM-UP'S OWN DECISIONS ARE NOT THE RUN'S. They are compiled and measured by the
            // cost report below, but they are not frames of the recording, so every counter the
            // report prints -- including the pathfinder's -- starts from here rather than including
            // eight frames nobody asked about.
            _solver.Path.ResetCounters();
            // AND THE STAGE CLOCK, for the same reason: eight warm-up frames are compilation, not
            // decision. See `DodgeSolver.StageMicroseconds`.
            _solver.ResetStageClock();

            foreach (RecordedFrame frame in _trace.Frames)
            {
                float speed = ResolveSpeed(frame, previousX, previousY);
                ResolveCourse(frame, previousX, previousY);
                previousX = frame.PlayerX;
                previousY = frame.PlayerY;

                DodgeSnapshot snapshot = BuildSnapshot(frame, speed, _courseRadians, _courseValid);
                long t0 = Stopwatch.GetTimestamp();
                DodgeDecision decision = _solver.Decide(snapshot);
                long t1 = Stopwatch.GetTimestamp();
                for (int r = 1; r < Repeat; r++)
                {
                    long r0 = Stopwatch.GetTimestamp();
                    _solver.Decide(snapshot);
                    long r1 = Stopwatch.GetTimestamp();
                    if (r1 - r0 < t1 - t0)
                    {
                        t0 = r0;
                        t1 = r1;
                    }
                }
                decision.CostMicroseconds = (int)(((t1 - t0) * 1000000L) / Stopwatch.Frequency);
                _costUs.Add(decision.CostMicroseconds);
                if (decision.EvaluatedCount > 0)
                {
                    _activeCostUs.Add(decision.CostMicroseconds);
                }

                _solver.CopyLastCandidates(candidates);
                // The refusal total, counted where the candidates are: `Admissible` is the solver's
                // own answer per candidate, so this is the same set `WindowRefusals` is a subset of.
                for (int ci = 0; ci < candidates.Count; ci++)
                {
                    if (!candidates[ci].Admissible)
                    {
                        _refusalsTotal++;
                    }
                }

                _frames++;
                Count(_actions, decision.Action);
                Count(_reasons, decision.Reason);
                if (decision.EvaluatedCount > 0)
                {
                    _framesWithCandidates++;
                    _evaluatedTotal += decision.EvaluatedCount;
                    _admissibleTotal += decision.AdmissibleCount;
                }
                if (decision.Action == DodgeAction.Move)
                {
                    _moveFrames++;
                    _durabilityMs.Add(decision.DurabilitySeconds * 1000.0f);
                }
                if (decision.Held)
                {
                    _hysteresisHolds++;
                }

                // THE CLASSIFICATION, COUNTED -- because "should this bullet be dodged at all" is a
                // question about the threat set and not about the decision, and the aggregate the
                // decision reports cannot answer it. See `DodgeSolver.ThreatVerdict`.
                {
                    int relevant = 0;
                    int dodgeable = 0;
                    for (int ti = 0; ti < snapshot.Threats.Count; ti++)
                    {
                        ThreatVerdict v = _solver.VerdictOf(ti);
                        Count(_verdicts, v);
                        if (v != ThreatVerdict.Clear)
                        {
                            relevant++;
                        }
                        if (v == ThreatVerdict.Dodgeable)
                        {
                            dodgeable++;
                        }
                    }
                    if (relevant > 0)
                    {
                        _framesWithRelevantThreats++;
                    }
                    if (dodgeable > 0)
                    {
                        _framesWithADodgeableThreat++;
                    }
                    else if (relevant > 0)
                    {
                        _framesNothingToDoAbout++;
                    }
                }

                CheckT5(frame, snapshot, decision, candidates);
                CheckT6(frame, snapshot, decision, candidates);
                CheckT10(frame, snapshot, decision, previousIntent);
                TrackPredictions(frame, snapshot, decision);
                if (DebugThreatId >= 0)
                {
                    DebugThreat(frame, snapshot);
                }

                if (frame.Frame == FrameToExplain
                    || (FramesToExplain != null && FramesToExplain.Contains(frame.Frame)))
                {
                    Explain(frame, snapshot, decision, candidates, speed);
                }

                if (decision.Action == DodgeAction.Degraded)
                {
                    _degradedFrames++;
                }
                if (decisions != null)
                {
                    WriteDecisionLine(decisions, frame, snapshot, decision, candidates);
                }
                previousIntent = frame.Intent;
            }

            CheckT7();

            if (verbose)
            {
                ReportWorld();
                ReportDebugThreat();
                ReportDecisions();
                ReportInvariants();
                ReportT7();
                ReportCost();
            }
            else
            {
                ReportInvariants();
                ReportT7();
            }

            if (decisions != null)
            {
                decisions.Flush();
                decisions.Dispose();
                decisions = null;
                if (verbose)
                {
                    Console.WriteLine("  decisions written   : " + DecisionsPath);
                }
            }

            Console.WriteLine();
            if (_warningsFatal)
            {
                Failures++;
            }
            if (Failures == 0)
            {
                Console.WriteLine("VERDICT: every asserted invariant held.");
            }
            else
            {
                Console.WriteLine("VERDICT: " + Failures + " invariant failure(s).");
            }
        }

        // ===================================================================================
        // Building the snapshot
        // ===================================================================================

        /// <summary>
        /// Prints everything the solver saw and everything it decided about one frame.
        ///
        /// The geometry is printed raw -- positions, radii, the radius sum squared, and the
        /// squared distance from each candidate's arrival point to each threat -- because the
        /// admission rules are all one comparison of two numbers and a report that omits them
        /// cannot distinguish "correctly refused" from "compared the wrong pair".
        /// </summary>
        private void Explain(RecordedFrame frame, DodgeSnapshot snapshot, DodgeDecision decision,
                             List<CandidateResult> candidates, float speed)
        {
            Console.WriteLine();
            Console.WriteLine("== frame " + frame.Frame + " ==");
            Console.WriteLine("  player  : (" + F(frame.PlayerX) + "," + F(frame.PlayerY) + ") r="
                              + F(snapshot.Player.Radius) + " speed=" + F(speed)
                              + " dt=" + F(frame.Dt) + " moving=" + frame.Moving
                              + " intent=" + F(frame.Intent));
            Console.WriteLine("  budget b: " + F(speed * frame.Dt));
            Console.WriteLine("  grid    : " + (snapshot.Grid.Present
                              ? snapshot.Grid.Width + "x" + snapshot.Grid.Height + " at ("
                                + snapshot.Grid.OriginX + "," + snapshot.Grid.OriginY + ")"
                              : "absent"));
            foreach (Trajectory t in snapshot.Threats)
            {
                float r = snapshot.Player.Radius + t.Radius;
                float dx = t.Xs[0] - frame.PlayerX;
                float dy = t.Ys[0] - frame.PlayerY;
                Console.WriteLine("  threat  : (" + (int)(t.Key >> 32) + "," + (int)t.Key + ")"
                                  + " @" + t.StartTime
                                  + " now (" + F(t.Xs[0]) + "," + F(t.Ys[0]) + ") r=" + F(t.Radius)
                                  + " count=" + t.Count + " trusted=" + t.TrustedCount(_config.TrustSeconds)
                                  + " alive0=" + (t.Count > 0 && t.Alive[0])
                                  + " | player offset (" + F(dx) + "," + F(dy) + ") d2="
                                  + F((dx * dx) + (dy * dy)) + " r_eff2=" + F(r * r)
                                  + " inside=" + ((dx * dx) + (dy * dy) < r * r));
            }
            Console.WriteLine("  decision: " + decision.Action + " / " + decision.Reason
                              + " index=" + decision.CandidateIndex
                              + " admissible=" + decision.AdmissibleCount
                              + " evaluated=" + decision.EvaluatedCount
                              + " durability=" + F(decision.DurabilitySeconds * 1000.0f) + "ms");
            // THE PATH, AND A PROBE OF ITS DANGER FIELD. `pathcost` in the decision dump says what the
            // route cost; when it equals the length on every frame the field may be honestly empty or
            // it may be broken, and these three lines are what tells those apart: a `d` of zero at the
            // player's own position with threats on the envelope means the field is not reading.
            PathPlanner planner = _solver.Path;
            Console.WriteLine("  path    : " + planner.Reason
                              + " wp=" + (planner.WaypointValid ? F(Deg(planner.WaypointHeading)) : "nan")
                              + " at (" + F(planner.WaypointX) + "," + F(planner.WaypointY) + ")"
                              + " nodes=" + planner.Nodes + " len=" + F(planner.Length)
                              + " cost=" + F(planner.Cost) + " held=" + planner.Held
                              + " envelope=" + planner.DebugThreatCount + " threat(s)");
            if (planner.DebugThreatCount > 0)
            {
                Console.WriteLine("    danger at the player, t=0.00s : "
                                  + F(planner.DebugDangerAt(frame.PlayerX, frame.PlayerY, 0.0f))
                                  + "   t=0.25s : "
                                  + F(planner.DebugDangerAt(frame.PlayerX, frame.PlayerY, 0.25f))
                                  + "   tight radius of threat 0 : "
                                  + F(planner.DebugThreatNear(0)));
                for (int ti = 0; ti < planner.DebugThreatCount; ti++)
                {
                    Console.WriteLine("    envelope[" + ti + "] : " + planner.DebugThreat(ti));
                }
            }
            Console.WriteLine("  solver's own view: durablySafeAtCurrent="
                              + _solver.IsDurablySafeAt(snapshot, frame.PlayerX, frame.PlayerY)
                              + ", insideAnyDisc="
                              + InsideAnyDisc(snapshot, frame.PlayerX, frame.PlayerY)
                              + ", stand admitted as "
                              + (AdmissionRule)_solver.AdmissionOf(snapshot, frame.PlayerX,
                                                                   frame.PlayerY)
                              + ", StandRefusal=" + (AdmissionRule)decision.StandRefusal);
            int shown = 0;
            // THE CHOSEN MOVE'S OWN TERMS, BEFORE THE MENU. `tools/dodge_direction_report.py` says the
            // dodge retreated; this says WHICH TERM PAID FOR IT, which is the difference between a
            // finding and a diagnosis. `HANDOFF-DIRECTED-DODGE.md` section 8 trap 11 is the reason it
            // comes before any further change: a distribution over outcomes cannot name a cause.
            if (decision.Action == DodgeAction.Move && decision.CandidateIndex >= 0)
            {
                float[] terms = new float[13];
                _solver.DescribeChosen(snapshot, decision.CandidateIndex, terms);
                var parts = new System.Collections.Generic.List<string>();
                for (int ti = 0; ti < terms.Length; ti++)
                {
                    parts.Add(DodgeSolver.ScoreTermNames[ti] + "=" + F(terms[ti]));
                }
                // THE HEADING IS ON THIS LINE AND NOT ONLY IN THE MENU BELOW, because the reader of
                // this line is a script that aggregates terms BY DIRECTION (see
                // `tools/dodge_term_report.py`). A breakdown that cannot be grouped by the thing the
                // dodge chose is a breakdown of a frame, not of a decision.
                Console.WriteLine("  chosen  : heading=" + F(Deg(decision.HeadingRadians))
                                  + " scale=" + F(decision.SpeedScale) + " "
                                  + string.Join(" ", parts)
                                  + " | fieldReady=" + _solver.Path.FieldReady
                                  + " danger(t=0.4) at player " 
                                  + F(_solver.Path.Danger(frame.PlayerX, frame.PlayerY, 0.4f))
                                  + " at +2u " 
                                  + F(_solver.Path.Danger(frame.PlayerX + 2.0f, frame.PlayerY, 0.4f))
                                  + " ON-BULLET(t=0) " 
                                  + F(snapshot.Threats.Count > 0
                                      ? _solver.Path.Danger(snapshot.Threats[0].Xs[0], snapshot.Threats[0].Ys[0], 0.0f)
                                      : -1.0f)
                                  + " ON-BULLET(t=0.6) " 
                                  + F(snapshot.Threats.Count > 0
                                      ? _solver.Path.Danger(snapshot.Threats[0].XAt(20), snapshot.Threats[0].YAt(20), 0.6f)
                                      : -1.0f));
            }
            foreach (CandidateResult c in candidates)
            {
                if (!c.Admissible && shown > 8)
                {
                    continue;
                }
                Console.WriteLine("    cand kind=" + c.Kind + " hd=" + F(c.HeadingRadians)
                                  + " d=" + F(c.Distance) + " at (" + F(c.X) + "," + F(c.Y) + ")"
                                  + " admissible=" + c.Admissible + " refusedBy="
                                  + (AdmissionRule)c.RefusedBy + " clearance=" + F(c.Clearance)
                                  + " durability=" + F(c.DurabilitySeconds * 1000.0f) + "ms"
                                  + " resolve=" + F(c.Resolve) + " score=" + F(c.Score));
                shown++;
            }
            // THE TOP OF THE MENU, BY SCORE, WHICH IS THE ONLY VIEW THAT EXPLAINS A CHOICE.
            // The list above is in CANDIDATE order, so it shows `stand` and the ring's first few
            // headings -- and when the winner is a heading 40 entries down, the dump says nothing
            // about why. Six rows sorted by score is what turns "it flipped again" into a number.
            Console.WriteLine("    -- top by score --");
            var ranked = new System.Collections.Generic.List<CandidateResult>();
            foreach (CandidateResult c in candidates)
            {
                if (c.Admissible)
                {
                    ranked.Add(c);
                }
            }
            ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
            int top = 0;
            foreach (CandidateResult c in ranked)
            {
                if (top++ >= 6)
                {
                    break;
                }
                Console.WriteLine("      kind=" + c.Kind + " hd=" + F(c.HeadingRadians)
                                  + " d=" + F(c.Distance)
                                  + " resolve=" + F(c.Resolve) + " clearance=" + F(c.Clearance)
                                  + " durability=" + F(c.DurabilitySeconds * 1000.0f) + "ms"
                                  + " score=" + F(c.Score));
            }
            Console.WriteLine();
        }

        /// <summary>
        /// One decision, one line, every frame. See <see cref="DecisionsPath"/> for why.
        ///
        /// EIGHT NUMBERS, AND EACH ONE IS A QUESTION THE AGGREGATE REPORT CANNOT ANSWER.
        ///
        ///   * `heading` and `hintent` -- the dodge's choice against the direction the player was
        ///     holding, per frame, so the deflection itself is the difference rather than a mean;
        ///   * `goal` and `goalsrc` -- the goal, and **which field it was built from**. `goalsrc` is
        ///     `player`/`course`/`none`, and it is the single field that makes the change under test
        ///     legible: the defect is that the goal was the body's own course (97.8 % of logged
        ///     moves), and "was it the player this time" is otherwise only visible inside `Decide`;
        ///   * `held` -- whether the player was walking at all, the gate the goal now uses, recorded
        ///     beside the decision it gated rather than in a separate file;
        ///   * `intent`/`course`/`moving` -- the raw evidence, so a reader who distrusts one field
        ///     can re-derive the goal from the other two;
        ///   * `scale` -- the magnitude, which is the other half of the operator's complaint: 297 of
        ///     400 logged moves in the baseline session were held at a third of a step.
        ///
        /// The chosen candidate's `resolve` and `clearance` are read back through `CandidateIndex`,
        /// because a heading with no safety numbers beside it cannot be told from a coin flip -- the
        /// same reason `Explain` prints the top of the menu by score.
        /// </summary>
        private void WriteDecisionLine(System.IO.StreamWriter w, RecordedFrame frame,
                                       DodgeSnapshot snapshot, DodgeDecision decision,
                                       List<CandidateResult> candidates)
        {
            float hintent = Normalize(frame.Intent - frame.Rotation);
            // WHERE THE GOAL CAME FROM, READ FROM THE SOLVER RATHER THAN RE-DERIVED HERE.
            //
            // The first version of this field re-ran the goal's own precedence in the harness, and
            // that is the one thing an instrument must never do: two implementations of one rule
            // agree until they do not, and the reader then believes a number about the harness
            // instead of a number about the solver. The solver records its decision (`_goalSource`)
            // and this prints it, so a change to the rule shows up here without an edit.
            string goalSrc = GoalSourceName(_solver.GoalSource);

            float resolve = float.NaN;
            float clearance = decision.Clearance;
            if (decision.CandidateIndex >= 0 && decision.CandidateIndex < candidates.Count)
            {
                CandidateResult chosen = candidates[decision.CandidateIndex];
                resolve = chosen.Resolve;
                clearance = chosen.Clearance;
            }

            w.Write(frame.Frame);                       w.Write(" | ");
            w.Write(decision.Action);                   w.Write(" | ");
            w.Write(decision.Reason);                   w.Write(" | ");
            w.Write(decision.Held ? 1 : 0);             w.Write(" | ");
            w.Write(F(Deg(decision.HeadingRadians)));   w.Write(" | ");
            w.Write(F(decision.SpeedScale));            w.Write(" | ");
            w.Write(F(Deg(_solver.GoalRadians)));       w.Write(" | ");
            w.Write(goalSrc);                           w.Write(" | ");
            w.Write(F(Deg(hintent)));                   w.Write(" | ");
            w.Write(F(Deg(snapshot.Player.IntentRadians))); w.Write(" | ");
            w.Write(F(Deg(snapshot.Player.CourseRadians))); w.Write(" | ");
            w.Write(snapshot.Player.Moving ? 1 : 0);    w.Write(" | ");
            w.Write(decision.EvaluatedCount);           w.Write(" | ");
            w.Write(decision.AdmissibleCount);          w.Write(" | ");
            w.Write(F(decision.DurabilitySeconds * 1000.0f)); w.Write(" | ");
            w.Write(F(clearance));                      w.Write(" | ");
            w.Write(F(resolve));                        w.Write(" | ");
            w.Write(float.IsNegativeInfinity(decision.Score) ? "-inf" : F(decision.Score));
            w.Write(" | ");
            w.Write(float.IsNegativeInfinity(decision.RunnerUpScore) ? "-inf" : F(decision.RunnerUpScore));
            w.Write(" | ");
            w.Write(decision.CostMicroseconds);
            w.Write(" | ");
            // THE RULE 6 DECISION WINDOW THIS FRAME USED. It stopped being a constant, and a
            // per-threat quantity nothing reports cannot be told from a constant nothing reports --
            // which is exactly the failure this column exists to prevent. `disc=0,window=<floor>`
            // writes the floor here on every frame, so the change and its own off-switch are one
            // grep apart.
            w.Write(F(_solver.DecisionWindowMs));
            w.Write(" | ");
            // THE PATH, WHICH IS THE NEW OBJECT AND THEREFORE THE ONE THAT MOST NEEDS A COLUMN.
            // HANDOFF-DIRECTED-DODGE.md section 6 states the rule this set of columns exists for:
            // "a pathfinder that is silently absent prints as 'the outrun ratio did not move', which
            // is the same output as a pathfinder that ran and did nothing". Six numbers, and each
            // answers a different half of that: `pathwhy` says whether there was a path at all and
            // why not, `pathn`/`pathlen` say how much of a search produced it, `pathcost` says how
            // much of that route was spent in danger, `wpdist` says how far away the thing the dodge
            // is steering at actually is, and `wp` is the direction it is steering at -- the column
            // that makes "it sidestepped" readable per frame instead of inferred from a mean.
            PathPlanner path = _solver.Path;
            w.Write(path.WaypointValid ? F(Deg(Ring(path.WaypointHeading))) : "nan"); w.Write(" | ");
            w.Write(path.WaypointValid ? F(Distance(snapshot.Player.X, snapshot.Player.Y,
                                                    path.WaypointX, path.WaypointY)) : "nan");
            w.Write(" | ");
            w.Write(path.Reason == PathReason.Found ? path.Nodes : -1);          w.Write(" | ");
            w.Write(path.Reason == PathReason.Found ? F(path.Length) : "nan");   w.Write(" | ");
            w.Write(path.Reason == PathReason.Found ? F(path.Cost) : "nan");     w.Write(" | ");
            w.Write(PathReasonName(path.Reason));
            // WHERE THIS FRAME'S OWN TIME WENT, per stage, so a TAIL frame can be attributed and
            // not just an average -- SPIKE3.2-OFFLINE-SOLVER.md section 15.11. Six columns because
            // a mean cannot answer "which stage made this frame slow" when the slow frames are one
            // in a hundred.
            for (int stage = 0; stage < DodgeSolver.StageNames.Length; stage++)
            {
                w.Write(" | ");
                w.Write(decision.StageUs(stage));
            }
            w.Write(Environment.NewLine);
        }

        private static float Distance(float ax, float ay, float bx, float by)
        {
            float dx = bx - ax;
            float dy = by - ay;
            return (float)Math.Sqrt((dx * dx) + (dy * dy));
        }

        /// <summary>
        /// The path's heading, folded onto the same ring the candidate set uses.
        ///
        /// THE WAYPOINT IS A CONTINUOUS ANGLE AND EVERY OTHER HEADING IN THIS FILE IS A RING HEADING.
        /// A reader comparing `wp` against `heading` column-by-column is comparing a number the search
        /// produced against a number the ring produced, and those two are up to 7.5 degrees apart even
        /// when they agree -- which is exactly why `GoalFanSteps` exists. Folding the waypoint onto
        /// the same 15-degree ring makes "the dodge did what the path said" a comparison of two equal
        /// quantities rather than of two that are merely close.
        /// </summary>
        private float Ring(float radians)
        {
            int headings = _config.Headings < 1 ? 24 : _config.Headings;
            float step = (float)(2.0 * Math.PI) / headings;
            return (float)Math.Round(radians / step) * step;
        }

        /// <summary>The `--decisions` spelling of a path failure. See `PathReason`.</summary>
        private static string PathReasonName(PathReason reason)
        {
            switch (reason)
            {
                case PathReason.Found: return "found";
                case PathReason.Disabled: return "off";
                case PathReason.NoGrid: return "nogrid";
                case PathReason.GridTooLarge: return "toolarge";
                case PathReason.NoGoal: return "nogoal";
                case PathReason.NoTarget: return "notarget";
                case PathReason.NodeCap: return "nodecap";
                case PathReason.Unreachable: return "unreachable";
                case PathReason.StartBlocked: return "startblocked";
                default: return "?";
            }
        }

        private static float Deg(float radians)
        {
            return radians * 180.0f / (float)Math.PI;
        }

        /// <summary>The `--decisions` spelling of a goal's provenance. See `DodgeSolver.GoalSource`.</summary>
        private static string GoalSourceName(GoalSource source)
        {
            switch (source)
            {
                case GoalSource.PlayerIntent: return "player";
                case GoalSource.Course: return "course";
                case GoalSource.Intent: return "intent";
                default: return "none";
            }
        }


        /// THE SPEED IS MEASURED WHEREVER THE FRAME CAN BE MEASURED, AND THE RESOLVED LAW IS THE
        /// FALLBACK. This matters more than it looks. The law
        /// `3.5 + 5.0*(SPD+10)/75` needs an SPD stat the trace does not carry, so any use of it
        /// offline is an assumption dressed as a number; the displacement between two consecutive
        /// recorded frames is a measurement of exactly the quantity `b` is made of. The law is
        /// therefore used only as a prior for a frame where the player did not move -- which,
        /// note, includes the frames where the player is pinned against terrain, and for those the
        /// *maximum* budget is the correct one to test admission against.
        /// </summary>
        /// <summary>The measured course, and how many frames it has survived without movement.</summary>
        private float _courseRadians;
        private bool _courseValid;
        private int _courseStaleFrames = 7;

        /// <summary>Frames a course survives after the character stops moving. Same as the hook's.</summary>
        private const int CourseHoldFrames = 6;

        /// <summary>
        /// The direction the character is actually moving, measured from the recorded positions of
        /// two consecutive frames.
        ///
        /// THE OFFLINE TWIN OF `DodgeAdapter.UpdateSpeed`'S MEASUREMENT, DELIBERATELY THE SAME SHAPE:
        /// same threshold, same six-frame hold, same "a blocked frame does not change the course"
        /// rule. If the two disagreed, the offline numbers would describe a dodge nobody will ever
        /// run -- which is the whole reason this harness compiles the solver's own source instead of
        /// reimplementing it.
        ///
        /// The threshold is `ResolveSpeed`'s own: below 0.001 units the frame is noise or a refusal,
        /// not a direction.
        /// </summary>
        private void ResolveCourse(RecordedFrame frame, float previousX, float previousY)
        {
            if (float.IsNaN(previousX) || frame.Dt <= 0.0f)
            {
                return;
            }
            float dx = frame.PlayerX - previousX;
            float dy = frame.PlayerY - previousY;
            float moved = (float)Math.Sqrt((dx * dx) + (dy * dy));
            if (moved > 0.001f && moved < 5.0f)
            {
                _courseRadians = (float)Math.Atan2(dy, dx);
                _courseValid = true;
                _courseStaleFrames = 0;
                return;
            }
            _courseStaleFrames++;
            if (_courseStaleFrames > CourseHoldFrames)
            {
                _courseValid = false;
            }
        }

        private float ResolveSpeed(RecordedFrame frame, float previousX, float previousY)
        {
            if (frame.SpeedOverride > 0.0f)
            {
                return frame.SpeedOverride;
            }
            if (!float.IsNaN(previousX) && frame.Dt > 0.0f)
            {
                float dx = frame.PlayerX - previousX;
                float dy = frame.PlayerY - previousY;
                float moved = (float)Math.Sqrt((dx * dx) + (dy * dy));
                if (moved > 0.001f)
                {
                    return moved / frame.Dt;
                }
            }
            // The prior: the resolved law at a middling SPD, which is very close to the per-frame
            // displacement the live sweep measured (0.1369 units in 16.680 ms = 8.21 units/s).
            return 8.2f;
        }

        private DodgeSnapshot BuildSnapshot(RecordedFrame frame, float speed, float courseRadians,
                                            bool courseValid)
        {
            var snapshot = new DodgeSnapshot();
            snapshot.Frame = frame.Frame;
            snapshot.ClockMs = frame.ClockMs;
            snapshot.FrameSeconds = frame.Dt;

            snapshot.Player.X = frame.PlayerX;
            snapshot.Player.Y = frame.PlayerY;
            snapshot.Player.Radius = frame.PlayerRadius > 0.0f ? frame.PlayerRadius : 0.25f;
            snapshot.Player.Health = frame.Health;
            snapshot.Player.SpeedPerSecond = speed;
            snapshot.Player.Moving = frame.Moving;

            // THE INTENT IS CONVERTED TO THE WORLD FRAME HERE, AND IT USED TO NOT BE. The trace
            // records `MoveAngle` as the client held it -- an INPUT-frame number, because
            // `World.cs:12692` computes `angle = MoveAngle + (-WorldRotation + aimOffset)` -- and
            // the solver's whole geometry is world positions. Reading it raw made the offline
            // intent test compare two different frames, which `SPIKE3.2-OFFLINE-SOLVER.md` section
            // 9.2 recorded as a known inaccuracy and left alone because the harness's numbers were
            // published. It cannot be left alone any more: the forward goal is the COURSE when the
            // character is moving and the INTENT when they are not, so an unconverted intent is a
            // goal pointing up to 359 degrees away from where the player is asking to go.
            snapshot.Player.IntentRadians = Normalize(frame.Intent - frame.Rotation);
            // The trace always carries the field -- a recording with no intent is a recording with no
            // `"intent":` key, which the reader would not have parsed -- so the harness's answer is
            // yes by construction. It is set explicitly rather than defaulted because the default is
            // `false`, and a solver fed a snapshot whose intent is "not valid" behaves differently
            // on purpose (see `DodgeSolver.Decide`).
            snapshot.Player.IntentValid = true;

            // THE COURSE, WHICH NEEDS NO CONVERSION: it was measured from a world-space
            // displacement, so the camera is already out of it. This is the offline twin of
            // `DodgeAdapter.UpdateSpeed`'s measurement, and the two must agree or the two paths are
            // deciding about different worlds.
            snapshot.Player.CourseRadians = courseRadians;
            snapshot.Player.CourseValid = courseValid;
            snapshot.Player.Dead = frame.Dead;
            snapshot.Player.SafeWorld = frame.SafeWorld;

            if (frame.Grid != null)
            {
                snapshot.Grid = frame.Grid.ToSolverWindow();
            }

            foreach (RecordedThreat recorded in frame.Threats)
            {
                var trajectory = new Trajectory();
                trajectory.Key = recorded.Key;
                trajectory.StartTime = recorded.StartTime;
                trajectory.Radius = recorded.Radius;
                trajectory.LiveX = recorded.X;
                trajectory.LiveY = recorded.Y;
                trajectory.StepMs = _config.SampleStepMs;
                trajectory.Projectable = recorded.Projectable;
                trajectory.Damage = recorded.Damage;
                trajectory.DamagePercent = recorded.DamagePercent;
                trajectory.ManaDamage = recorded.ManaDamage;
                int n = Math.Min(recorded.Count, Trajectory.MaxSamples);
                trajectory.Count = n;
                for (int s = 0; s < n; s++)
                {
                    trajectory.Xs[s] = recorded.Xs[s];
                    trajectory.Ys[s] = recorded.Ys[s];
                    trajectory.Alive[s] = recorded.Alive[s];
                }
                snapshot.Threats.Add(trajectory);
            }
            return snapshot;
        }

        // ===================================================================================
        // T5 -- `stand` is never rejected while durably safe
        // ===================================================================================

        /// <summary>
        /// THE INVARIANT, PRECISELY: if `stand` is admitted by the solver's own admission rules
        /// AND the position is durably safe AND the player is not already inside a disc, then the
        /// solver must not be steering them anywhere.
        ///
        /// WHY ALL THREE TERMS. The specification's sentence is "`stand` is never rejected while
        /// durably safe", and each word of it is doing work:
        ///
        ///   * "admitted" -- the solver's own `Admit` on the zero-length candidate. Without it the
        ///     test would fail legitimately for a position that is durable but unreachable, which
        ///     is the case when the terrain has the player pinned;
        ///   * "durably safe" -- the solver's own horizon predicate, asked of the solver
        ///     (`IsDurablySafeAt`) rather than re-derived here, because two nearly identical
        ///     predicates that disagree on the close frames produce a "failure" that is really the
        ///     difference between them;
        ///   * "not already inside a disc" -- a player standing in a bullet is not safe by any
        ///     reading, and moving is the correct answer there.
        /// </summary>
        private void CheckT5(RecordedFrame frame, DodgeSnapshot snapshot, DodgeDecision decision,
                             List<CandidateResult> candidates)
        {
            if (decision.Action != DodgeAction.Move)
            {
                return;
            }
            if (!_solver.IsDurablySafeAt(snapshot, frame.PlayerX, frame.PlayerY))
            {
                return;
            }
            if (InsideAnyDisc(snapshot, frame.PlayerX, frame.PlayerY))
            {
                return;
            }

            // At this point the position is durably safe and not inside a disc, so the ONLY thing
            // that may legitimately stop `stand` is the admission rules -- and if they admit it,
            // the solver has refused a move it should have taken.
            AdmissionRule stand = (AdmissionRule)_solver.AdmissionOf(snapshot, frame.PlayerX,
                                                                     frame.PlayerY);
            if (stand != AdmissionRule.Admitted)
            {
                _standRefusedByRules++;
                return;
            }

            _standRejectedWhileDurable++;
            if (_standRejectedExample.Count < 5)
            {
                _standRejectedExample.Add(frame.Frame);
                Console.WriteLine("  T5 FAIL at frame " + frame.Frame
                                  + ": stand is admitted and durably safe, yet the solver moved"
                                  + " (reason " + decision.Reason + ", heading "
                                  + F(decision.HeadingRadians) + ", admitted="
                                  + decision.AdmissibleCount + " of " + decision.EvaluatedCount + ")");
            }
        }

        /// <summary>The game's own hit test against every threat's current disc.</summary>
        private static bool InsideAnyDisc(DodgeSnapshot snapshot, float x, float y)
        {
            float playerRadius = snapshot.Player.Radius > 0.0f ? snapshot.Player.Radius : 0.25f;
            foreach (Trajectory t in snapshot.Threats)
            {
                if (t.Count <= 0)
                {
                    continue;
                }
                float r = playerRadius + t.Radius;
                float dx = x - t.Xs[0];
                float dy = y - t.Ys[0];
                if ((dx * dx) + (dy * dy) < (r * r))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The harness's own reading of "how close does this threat's path come".</summary>
        private float MinDistanceSquared(Trajectory t, float px, float py)
        {
            int trusted = t.TrustedCount(_config.TrustSeconds);
            float best = float.MaxValue;
            for (int s = 0; s < trusted && s < t.Count; s++)
            {
                float dx = t.Xs[s] - px;
                float dy = t.Ys[s] - py;
                float d2 = (dx * dx) + (dy * dy);
                if (d2 < best)
                {
                    best = d2;
                }
            }
            return best;
        }

        /// <summary>
        /// Whether the player's current position is safe over the whole horizon, computed from
        /// the trace alone.
        ///
        /// THE THRESHOLD IS THE SOLVER'S, TO THE BIT: `DistanceSquared < r_eff²`, against the
        /// bullet's live radius, over the samples inside the solver's own trust prefix. A harness
        /// that chose its own comparison or its own sample count would not be testing the solver;
        /// it would be testing the difference between two nearly identical predicates, and it
        /// would report that difference as a solver failure. The trust prefix is the one that
        /// caught exactly that: at 0.45 s the harness saw 16 samples and the solver 21, and the
        /// five extra samples held the true closest approach.
        /// </summary>
        private bool DurablySafe(RecordedFrame frame, DodgeSnapshot snapshot)
        {
            float px = frame.PlayerX;
            float py = frame.PlayerY;
            float playerRadius = frame.PlayerRadius > 0.0f ? frame.PlayerRadius : 0.25f;

            foreach (Trajectory t in snapshot.Threats)
            {
                if (t.Count <= 0)
                {
                    continue;
                }
                float r = playerRadius + t.Radius;
                float r2 = r * r;
                int trusted = t.TrustedCount(_config.TrustSeconds);
                for (int s = 0; s < trusted && s < t.Count; s++)
                {
                    float dx = t.Xs[s] - px;
                    float dy = t.Ys[s] - py;
                    if ((dx * dx) + (dy * dy) < r2)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        // ===================================================================================
        // T6 -- no emitted move is inside a bullet's disc at its arrival time
        // ===================================================================================

        /// <summary>
        /// THE CORE SAFETY INVARIANT, ASSERTED ON WHAT WAS EMITTED RATHER THAN ON WHAT THE SOLVER
        /// BELIEVES IT EMITTED. The candidate's own position is taken from the solver's result
        /// struct, the discs are taken from the trace, and the test is the game's own predicate --
        /// `DistanceSquared < r_eff^2` with `r_eff` already containing the player's half.
        /// </summary>
        private void CheckT6(RecordedFrame frame, DodgeSnapshot snapshot, DodgeDecision decision,
                             List<CandidateResult> candidates)
        {
            if (decision.Action != DodgeAction.Move)
            {
                return;
            }
            if (decision.CandidateIndex < 0 || decision.CandidateIndex >= candidates.Count)
            {
                return;
            }
            CandidateResult chosen = candidates[decision.CandidateIndex];
            if (!chosen.Admissible)
            {
                _movesNotAdmissible++;
                if (_moveViolationExamples.Count < 5)
                {
                    _moveViolationExamples.Add("frame " + frame.Frame + ": emitted a candidate the"
                                               + " solver itself refused (rule "
                                               + (AdmissionRule)chosen.RefusedBy + ", reason "
                                               + decision.Reason + ", kind " + chosen.Kind + ")");
                }
                return;
            }

            float px = frame.PlayerX;
            float py = frame.PlayerY;
            float playerRadius = frame.PlayerRadius > 0.0f ? frame.PlayerRadius : 0.25f;

            foreach (Trajectory t in snapshot.Threats)
            {
                if (t.Count <= 0)
                {
                    continue;
                }
                float r = playerRadius + t.Radius;
                float dx = chosen.X - t.Xs[0];
                float dy = chosen.Y - t.Ys[0];
                if ((dx * dx) + (dy * dy) < (r * r))
                {
                    // WALKED IN, OR FAILED TO WALK OUT? THE TWO ARE NOT THE SAME FINDING, AND
                    // TREATING THEM AS ONE IS HOW A DOCUMENTED EXCEPTION LOOKS LIKE A REGRESSION.
                    //
                    // Rule 4 refuses a candidate whose arrival point is inside a disc -- with one
                    // documented exception (SPIKE3.2-OFFLINE-SOLVER.md section 3.4): a player who is
                    // ALREADY inside a disc cannot step out of it in one frame, and refusing every
                    // such candidate is what made the early solver stand still and die. That
                    // exception admits a move that is still inside a disc it started in, and T6 --
                    // which was written before the exception was ever exercised -- counts it as a
                    // violation. The distinction below is the honest one:
                    //
                    //   * the player was NOT inside this disc and the move ended inside it: the
                    //     solver walked into a bullet. That is a violation, full stop.
                    //   * the player WAS inside it and the move did not get clear: the exception
                    //     working as designed, and only a violation if the move did not improve.
                    float pdx = frame.PlayerX - t.Xs[0];
                    float pdy = frame.PlayerY - t.Ys[0];
                    float nowD2 = (pdx * pdx) + (pdy * pdy);
                    bool wasInside = nowD2 < (r * r);
                    if (!wasInside)
                    {
                        _movesInsideDiscAtArrival++;
                        if (_moveViolationExamples.Count < 5)
                        {
                            _moveViolationExamples.Add("frame " + frame.Frame + ": arrival ("
                                                       + F(chosen.X) + "," + F(chosen.Y) + ") WALKED INTO "
                                                       + t.Key + "'s disc at ("
                                                       + F(t.Xs[0]) + "," + F(t.Ys[0]) + ") r=" + F(r));
                        }
                        return;
                    }
                    _movesEscapeIncomplete++;
                    if (_moveEscapeExamples.Count < 5)
                    {
                        _moveEscapeExamples.Add("frame " + frame.Frame + ": arrival ("
                                                + F(chosen.X) + "," + F(chosen.Y) + ") is still inside "
                                                + t.Key + "'s disc (was " + F((float)Math.Sqrt(nowD2))
                                                + " from its centre, now " + F((float)Math.Sqrt((dx * dx) + (dy * dy)))
                                                + "), r=" + F(r));
                    }
                    // `<=` because "improved" means the arrival is FURTHER from the centre than the
                    // player is now. The first version of this line read `>=` and counted every
                    // improving escape as a violation -- which is the same class of mistake as the
                    // one it exists to find, and it reported 819 of them.
                    if ((dx * dx) + (dy * dy) <= nowD2)
                    {
                        // Not even an improvement: this IS the old defect, unchanged.
                        _movesInsideDiscAtArrival++;
                    }
                    return;
                }
            }
        }

        // ===================================================================================
        // T10 -- hysteresis cannot lock a heading that has become unsafe
        // ===================================================================================

        /// <summary>
        /// A HELD HEADING THAT IS NO LONGER ADMITTED IS THE FAILURE THE INVARIANT EXISTS TO
        /// CATCH, and it is checked against the candidate the solver itself marked as the held
        /// one -- so a hysteresis that keeps steering into a bullet fails here even if its own
        /// bookkeeping says everything is fine.
        ///
        /// HEADING-CHANGE RATE IS REPORTED RATHER THAN ASSERTED. Jitter is a tuning property (a
        /// rate over a run) and safety is a property of each frame; only the second can be an
        /// invariant, and conflating them is how a "test" that is really a preference starts
        /// failing for the wrong reason.
        ///
        /// **IT MEASURES THE DECISION AGAINST ITS OWN PREVIOUS DECISION, AND IT DID NOT.**
        /// `previousIntent` is the trace's `intent` field, which is `GameInput.MoveAngle` read at
        /// the FRAME TICK -- before the pilot writes, and after the client's own poll -- so it is
        /// the PLAYER'S input heading, not the dodge's last command. Comparing the two gives a
        /// perfectly good number ("how often does the dodge disagree with the hands") and it is not
        /// the number this section is about: "the dodge flipped its own heading" is a stutter, and
        /// "the dodge overrode the player" is the dodge doing its job. The two were reported as one
        /// figure for two sessions. `_lastChosenHeading` is the dodge's own history.
        /// </summary>
        private void CheckT10(RecordedFrame frame, DodgeSnapshot snapshot, DodgeDecision decision,
                              float previousIntent)
        {
            if (decision.Action != DodgeAction.Move)
            {
                return;
            }

            // The dodge against its own last command, on an adjacent frame: the stutter measure.
            if (!float.IsNaN(_lastChosenHeading))
            {
                float turn = Math.Abs(Normalize(decision.HeadingRadians - _lastChosenHeading));
                bool adjacent = (frame.Frame - _lastChosenFrame) <= 2;
                if (adjacent)
                {
                    _adjacentMoves++;
                    _turnDegrees += turn * 180.0f / (float)Math.PI;
                    if (turn > 0.0005f)
                    {
                        _adjacentHeadingChanges++;
                    }
                    if (turn > 0.5f * (float)Math.PI)
                    {
                        _adjacentReversals++;
                        if (_adjacentReversalExamples.Count < 6)
                        {
                            _adjacentReversalExamples.Add("frame " + frame.Frame + ": "
                                + (_lastChosenHeading * 180.0 / Math.PI).ToString("0.0", CultureInfo.InvariantCulture)
                                + "deg -> "
                                + (decision.HeadingRadians * 180.0 / Math.PI).ToString("0.0", CultureInfo.InvariantCulture)
                                + "deg, reason " + decision.Reason
                                + ", score " + decision.Score.ToString("0.00", CultureInfo.InvariantCulture));
                        }
                    }
                }
            }
            _lastChosenHeading = decision.HeadingRadians;
            _lastChosenFrame = frame.Frame;

            if (!float.IsNaN(previousIntent))
            {
                // HOW OFTEN THE DODGE OVERRIDES THE PLAYER, which is the other half of the picture
                // and is NOT jitter. Section 5.7's intent preservation exists so that a dodge which
                // is not needed leaves the operator in control; a run where this is high is a run
                // where the player was being steered a lot, whatever the heading history looks like.
                float delta = Math.Abs(Normalize(decision.HeadingRadians - previousIntent));
                if (delta > 0.5f * (float)Math.PI)
                {
                    _overridesIntent++;
                }
            }

            // THE GOAL MEASURE, AND IT IS THE ONE THIS WORK EXISTS TO MOVE. "Does the dodge carry
            // the character toward the imaginary point in front of them, or away from it?" is the
            // question an operator answers by feel -- "it feels better" -- and this is that question
            // as a count. `overrides intent` is not it: overriding the hands is correct whenever the
            // hands are pointed at a bullet, and the goal is where the BODY was already going.
            if (_solver.GoalValid)
            {
                float toGoal = Math.Abs(Normalize(decision.HeadingRadians - _solver.GoalRadians));
                _goalConsidered++;
                if (toGoal <= 0.5f * (float)Math.PI)
                {
                    _goalAligned++;
                }
                if (snapshot.Player.IntentValid)
                {
                    float toPlayer = Math.Abs(Normalize(decision.HeadingRadians
                                                        - snapshot.Player.IntentRadians));
                    if (toPlayer <= 0.5f * (float)Math.PI)
                    {
                        _playerAligned++;
                    }
                }
            }

            if (!decision.Held)
            {
                return;
            }

            // The held heading's own candidate: the one the solver chose, which for a held
            // decision is the previous heading. If it is not admissible, the hold was unsafe.
            if (decision.Score == float.NegativeInfinity)
            {
                _unsafeHold++;
                if (_holdViolationExamples.Count < 5)
                {
                    _holdViolationExamples.Add("frame " + frame.Frame
                                               + ": held a heading with no admissible candidate");
                }
            }
        }

        // ===================================================================================
        // T7 -- predicted and actual collision agree
        // ===================================================================================

        /// <summary>
        /// Records, for each tracked bullet, the FIRST frame at which the model said its path
        /// would enter the player's disc -- the same predicate `TrajectorySampler` uses, applied
        /// to the recorded samples so that the harness's reading and the hook's are the same test.
        ///
        /// "THE SAME PREDICATE" IS THE WHOLE VALUE OF THIS NUMBER. If the harness predicted with
        /// its own geometry it would be measuring itself; the sampler's own test is
        /// `DistanceSquared &lt;= r*r` against the sampled points with the *bullet's* radius, and
        /// the player's half is added here because the recorded trace carries the player's radius
        /// as a separate field.
        /// </summary>
        private void TrackPredictions(RecordedFrame frame, DodgeSnapshot snapshot, DodgeDecision decision)
        {
            long key = 0;
            foreach (Trajectory t in snapshot.Threats)
            {
                key = t.Key;
                if (!_firstFrameSeen.ContainsKey(key))
                {
                    _firstFrameSeen[key] = frame.Frame;
                }
                _lastFrameSeen[key] = frame.Frame;

                if (_firstPrediction.TryGetValue(key, out Prediction recorded))
                {
                    // THE CLOSEST APPROACH IS ACCUMULATED OVER EVERY LATER FRAME, NOT READ OFF THE
                    // FRAME THE PREDICTION WAS RECORDED ON. The trace's polylines are re-anchored on
                    // the bullet's live position every frame, so the closest approach visible on the
                    // first frame that predicted a hit is the approach *as it looked then* -- and it
                    // is systematically larger than the truth, because the bullet has not arrived
                    // yet. Reading it there made every genuine near miss look like a
                    // predicted-and-never-happened bullet, which is precisely the false-positive
                    // count T7 reports.
                    float rr = frame.PlayerRadius + t.Radius;
                    for (int s = 0; s < t.Count; s++)
                    {
                        // ONLY THE SAMPLES THE BULLET WAS STILL FLYING FOR. Past its own
                        // end-of-shot the recorder freezes the path at the last position, so a
                        // frozen tail would be counted as "the bullet was still there" and would
                        // supply a closest approach that never happened.
                        if (!t.Alive[s])
                        {
                            break;
                        }
                        float dx = t.Xs[s] - frame.PlayerX;
                        float dy = t.Ys[s] - frame.PlayerY;
                        float distance = (float)Math.Sqrt((dx * dx) + (dy * dy));
                        if (distance < recorded.ClosestEver)
                        {
                            recorded.ClosestEver = distance;
                        }
                    }
                    continue;
                }

                float r = frame.PlayerRadius + t.Radius;
                float r2 = r * r;
                bool inside = false;
                float closest = float.MaxValue;
                int closestIndex = 0;
                // THE EARLIEST sample inside the disc, not the nearest. Impact time is when the
                // bullet first arrives, and the nearest sample can be later -- an earlier sample
                // that grazes the edge is still the moment the hit happens.
                int impactIndex = -1;
                for (int s = 0; s < t.Count; s++)
                {
                    float dx = t.Xs[s] - frame.PlayerX;
                    float dy = t.Ys[s] - frame.PlayerY;
                    float d2 = (dx * dx) + (dy * dy);
                    if (d2 < closest)
                    {
                        closest = d2;
                        closestIndex = s;
                    }
                    if (d2 <= r2)
                    {
                        inside = true;
                        if (impactIndex < 0)
                        {
                            impactIndex = s;
                        }
                    }
                }
                if (inside && impactIndex < 0)
                {
                    impactIndex = closestIndex;
                }
                if (inside)
                {
                    var p = new Prediction();
                    p.Frame = frame.Frame;
                    p.PlayerX = frame.PlayerX;
                    p.PlayerY = frame.PlayerY;
                    p.PlayerRadius = frame.PlayerRadius;
                    p.Radius = t.Radius;
                    p.ImpactMs = impactIndex * _config.SampleStepMs;
                    p.MinDistance = (float)Math.Sqrt(closest);
                    p.ClosestEver = p.MinDistance;
                    p.ImpactFrame = frame.Frame
                                    + (int)Math.Round(p.ImpactMs / (frame.Dt * 1000.0f));
                    _firstPrediction[key] = p;
                }
            }
        }

        /// <summary>
        /// The comparison. For every collision the game resolved, did the model's own sampler say
        /// this bullet was on a collision course before it arrived?
        ///
        /// THE DIRECTION OF THE ERROR IS THE REPORT. A model that over-predicts makes a dodge that
        /// flinches at bullets that were never going to hit; a model that under-predicts makes one
        /// that stands still and dies. Both rates are printed, and neither is a pass/fail on its
        /// own -- what fails T7 is a hit whose bullet was *never* predicted, because that is the
        /// case where the model is simply wrong.
        /// </summary>
        private void CheckT7()
        {
            // A SYNTHETIC TRACE CANNOT SETTLE T7, AND SAYING SO IS THE HONEST REPORT.
            //
            // This test asks whether the model's collision prediction agrees with the game's. On a
            // synthetic trace the second half of that comparison does not exist: there is no game,
            // no `HitCharacter`, and the generator's own sample grid is a different quantisation
            // from the frame grid the game's collision test runs on. A bullet whose sampled path
            // grazes the player while no frame-boundary position does is a *correct* prediction and
            // an artefact of the generator, and the strict test cannot tell the two apart. So it is
            // not run, and the report says which of the two situations the reader is looking at.
            if (_trace.Synthetic)
            {
                _t7 = new T7Report
                {
                    Hits = 0,
                    Matched = 0,
                    Unpredicted = 0,
                    PredictedNeverHit = 0,
                    PredictedInsideDisc = 0,
                    TrackedBullets = _firstFrameSeen.Count,
                    PredictedBullets = _firstPrediction.Count,
                    MeanWarningMs = 0.0f,
                    NotApplicable = true,
                };
                return;
            }

            long matched = 0;
            long unpredicted = 0;
            var warnings = new List<float>();
            float frameDeltaMs = _trace.Frames.Count > 1 ? _trace.Frames[0].Dt * 1000.0f : 16.667f;

            // THE FAIR TEST, BEFORE ANYTHING ELSE. For every prediction, ask whether the bullet was
            // still going to reach the player at the frame the impact was predicted for -- using
            // the position the player ACTUALLY occupied then. A prediction that fails this is a
            // model error; a prediction that passes it but never became a hit is the player having
            // left, which is the dodge working.
            foreach (KeyValuePair<long, Prediction> entry in _firstPrediction)
            {
                Prediction p = entry.Value;
                if (p.ImpactFrame < 0 || p.ImpactFrame >= _trace.Frames.Count)
                {
                    continue;
                }
                RecordedFrame impactFrame = _trace.Frames[p.ImpactFrame];
                RecordedThreat atImpact = null;
                foreach (RecordedThreat t in impactFrame.Threats)
                {
                    if (t.Key == entry.Key)
                    {
                        atImpact = t;
                        break;
                    }
                }
                if (atImpact == null)
                {
                    // THE BULLET WAS GONE BY THE TIME IT WAS DUE. The prediction said "this arrives
                    // in N ms"; N ms later the sensor was no longer tracking it, so it never
                    // arrived. That is a real property of the prediction -- the bullet died, hit a
                    // wall, or was removed -- and it is what a false positive usually is: a
                    // trajectory the model projected further than the bullet's own life. It is NOT
                    // the player having dodged.
                    p.BulletGoneAtImpact = true;
                    continue;
                }
                float rr = impactFrame.PlayerRadius + atImpact.Radius;
                float rr2 = rr * rr;
                // The bullet is somewhere along its own recorded path at the impact frame; it can
                // have been sampled at any point relative to that frame's start, so the whole path
                // is searched rather than the knot whose index the prediction named.
                for (int s = 0; s < atImpact.Count; s++)
                {
                    float dx = atImpact.Xs[s] - impactFrame.PlayerX;
                    float dy = atImpact.Ys[s] - impactFrame.PlayerY;
                    if ((dx * dx) + (dy * dy) <= rr2)
                    {
                        p.HitAtImpactTime = true;
                        break;
                    }
                }
            }

            foreach (ObservedHit hit in _trace.Hits)
            {
                long key = Key(hit.GameId, hit.StartTime);

                // THE ONE CHECK THAT COMPARES THE TWO IMPLEMENTATIONS RATHER THAN THE MODEL.
                //
                // Two independent halves produce a "this bullet was predicted" flag for the same
                // collision: `TrajectorySampler.OnFrame` marks it live, in the hook, and
                // `TrackPredictions` re-derives it here from the recorded samples. Nothing in the
                // harness runs the sampler, so until this comparison existed the two were never
                // checked against each other -- and on run `20261014-193728-p4508` they disagreed
                // by 14 versus 2 false negatives because the live disc was missing the player's
                // half (SPIKE3.2-OFFLINE-SOLVER.md section 4.6, corrected in 4.7). The counters on
                // that run were the only instrument that caught it, and they only caught it because
                // a human read both. A disagreement here therefore means one of the two is wrong
                // about the geometry, and it is worth failing on rather than reporting.
                if (hit.HasPredictedFlag)
                {
                    bool harnessSays = _firstPrediction.ContainsKey(key);
                    _parityCompared++;
                    if (harnessSays == hit.Predicted)
                    {
                        _parityAgree++;
                        // NOT INDEPENDENTLY RE-DERIVED, AND THE DISTINCTION IS WORTH KEEPING.
                        //
                        // The recorder culls with `TraceRecorder.CouldReach`, which applies the
                        // model's own disc test to the model's own samples -- so a bullet that was
                        // never written is implicit evidence that the model never put it inside
                        // the disc, which is the same answer `_firstPrediction` would have given
                        // had the path been written down. That is corroboration, but it is
                        // corroboration one step removed: it is the *reader's* absence of a
                        // trajectory, not a second reading of the geometry. Counting these apart
                        // keeps "two readings agreed" from quietly meaning "one reading, and one
                        // thing that was never read".
                        if (!hit.Predicted && !_firstFrameSeen.ContainsKey(key))
                        {
                            _parityUnwitnessed++;
                        }
                    }
                    else if (hit.Predicted)
                    {
                        _parityLiveOnly++;
                        if (_parityMismatch.Count < 10)
                        {
                            _parityMismatch.Add(hit + " at log line " + hit.LineNumber
                                                + ": the live observer predicted it and this reader"
                                                + " did not (the re-derived path never entered the"
                                                + " disc)");
                        }
                    }
                    else
                    {
                        _parityHarnessOnly++;
                        if (_parityMismatch.Count < 10)
                        {
                            _parityMismatch.Add(hit + " at log line " + hit.LineNumber
                                                + ": the live observer did NOT predict it and this"
                                                + " reader did -- the direction of the 2-versus-14"
                                                + " defect, an under-predicting live disc");
                        }
                    }
                }

                if (!_firstPrediction.TryGetValue(key, out Prediction p))
                {
                    unpredicted++;
                    if (_t7Unpredicted.Count < 10)
                    {
                        _t7Unpredicted.Add(hit + " at log line " + hit.LineNumber
                                           + (hit.Tracked ? " (tracked)" : " (never tracked)"));
                    }
                    continue;
                }
                matched++;
                // THE WARNING IS MEASURED IN FRAMES BETWEEN THE PREDICTION AND THE COLLISION, NOT
                // FROM `ImpactMs`. `ImpactMs` is the bullet's own time-to-impact *as recorded on the
                // frame the prediction was made*, so it is the model's forward view at that instant
                // and says nothing about how much notice the decision had. Averaging it produced
                // "mean model warning 446 ms" -- a number that looked like a warning and was
                // actually the model's remaining horizon. A number whose name promises something
                // its definition does not deliver is worse than no number.
                if (_lastFrameSeen.TryGetValue(key, out int lastFrame))
                {
                    warnings.Add((lastFrame - p.Frame) * frameDeltaMs);
                }
            }

            // Predicted and never hit: bullets whose recorded path came INSIDE the collision radius
            // of the player's recorded position, and that the game never resolved as a hit.
            //
            // ONLY THOSE COUNT, AND THE RESTRICTION IS THE DIFFERENCE BETWEEN A MEASUREMENT AND A
            // NAG. The solver's pre-test deliberately warns about a slightly wider corridor than
            // the collision radius (see `WarningRadiusPad`), so a bullet that merely enters that
            // wider corridor is a bullet the model was *right* to look at. Counting it as a false
            // positive would make the number a function of the pad, not of the model.
            long falsePositives = 0;
            long predictedInsideDisc = 0;
            foreach (KeyValuePair<long, Prediction> entry in _firstPrediction)
            {
                bool hit = false;
                foreach (ObservedHit h in _trace.Hits)
                {
                    if (Keys.Packed(h.GameId, h.StartTime) == entry.Key)
                    {
                        hit = true;
                        break;
                    }
                }
                if (hit)
                {
                    continue;
                }
                Prediction p = entry.Value;
                float r = p.PlayerRadius + p.Radius;
                predictedInsideDisc++;
                // THE TOLERANCE IS THE DIFFERENCE BETWEEN A RECORDED TRACE AND A SYNTHETIC ONE, and
                // it is not a fudge factor. On a recorded trace the test is strict: any bullet whose
                // sampled path entered the hit disc and that the game never resolved is a false
                // positive. On a synthetic trace the generator's own sample grid is the confound
                // (see DodgeTrace.Synthetic), so the test asks only whether the model predicted
                // something that came nowhere near.
                float tolerance = _trace.Synthetic ? (r * 0.5f) : 1e-4f;
                bool arriving = p.ClosestEver < r - tolerance;
                if (!arriving)
                {
                    continue;
                }
                if (p.HitAtImpactTime)
                {
                    // STILL ARRIVING WHERE THE PLAYER HAD MOVED TO, and the game resolved no hit.
                    // Counted apart from a model error: either the game's per-frame test disagreed
                    // with the sampled path, or the bullet was removed before it got there.
                    _stillArrivingNoHit++;
                    continue;
                }
                if (p.BulletGoneAtImpact)
                {
                    // The bullet ceased to exist before it was due. The projection was too long,
                    // not wrong about the geometry.
                    _bulletDiedFirst++;
                    continue;
                }
                falsePositives++;
                _modelErrors++;
                if (_t7FalsePositive.Count < 10)
                {
                    _t7FalsePositive.Add("(" + (entry.Key >> 32) + "," + (int)entry.Key + ")"
                                         + " predicted at frame " + p.Frame
                                         + ", closest " + F(p.ClosestEver)
                                         + " against r_eff " + F(r));
                }
            }

            _t7 = new T7Report
            {
                Hits = _trace.Hits.Count,
                Matched = matched,
                Unpredicted = unpredicted,
                PredictedNeverHit = falsePositives,
                PredictedInsideDisc = predictedInsideDisc,
                TrackedBullets = _firstFrameSeen.Count,
                PredictedBullets = _firstPrediction.Count,
                MeanWarningMs = Mean(warnings),
                ParityCompared = _parityCompared,
                ParityAgree = _parityAgree,
                ParityLiveOnly = _parityLiveOnly,
                ParityHarnessOnly = _parityHarnessOnly,
                ParityUnwitnessed = _parityUnwitnessed,
            };
        }

        private sealed class T7Report
        {
            public long Hits;
            public long Matched;
            public long Unpredicted;
            public long PredictedNeverHit;
            public long PredictedInsideDisc;
            public long TrackedBullets;
            public long PredictedBullets;
            public float MeanWarningMs;

            /// <summary>Collision lines that carried a live `predicted=` flag, and so could be
            /// compared. Zero on a log written before the field existed, or on synthetic frames.</summary>
            public long ParityCompared;

            /// <summary>Collision lines where the live observer and this reader agreed.</summary>
            public long ParityAgree;

            /// <summary>
            /// Of `ParityAgree`, the ones where the bullet was never written to the trace at all.
            /// Both sides say "not predicted", but this reader had no geometry to say it from: the
            /// recorder culled the bullet, so the only witness is the live observer's flag.
            /// </summary>
            public long ParityUnwitnessed;

            /// <summary>The live observer predicted it and this reader did not.</summary>
            public long ParityLiveOnly;

            /// <summary>The live observer did not predict it and this reader did. The direction of
            /// the `p4508` defect: an under-predicting live disc.</summary>
            public long ParityHarnessOnly;

            /// <summary>True when the trace was synthetic and T7 therefore could not be evaluated.</summary>
            public bool NotApplicable;
        }

        private T7Report _t7;
        private long _modelErrors;
        private long _stillArrivingNoHit;
        private long _bulletDiedFirst;
        private readonly List<string> _t7Unpredicted = new List<string>();
        private readonly List<string> _t7FalsePositive = new List<string>();
        private readonly List<string> _parityMismatch = new List<string>();
        private long _parityCompared;
        private long _parityAgree;
        private long _parityLiveOnly;
        private long _parityHarnessOnly;
        private long _parityUnwitnessed;

        // ===================================================================================
        // The report
        // ===================================================================================

        private void ReportWorld()
        {
            Console.WriteLine("-- the trace -----------------------------------------------------");
            Console.WriteLine("  frames              : " + _frames);
            if (_trace.Frames.Count > 0)
            {
                RecordedFrame first = _trace.Frames[0];
                RecordedFrame last = _trace.Frames[_trace.Frames.Count - 1];
                float seconds = (last.ClockMs - first.ClockMs) / 1000.0f;
                Console.WriteLine("  duration            : " + F(seconds) + " s (clock)");
                Console.WriteLine("  frame delta         : " + F(first.Dt * 1000.0f) + " ms");
            }
            Console.WriteLine("  tracked bullets     : " + _firstFrameSeen.Count);
            // THE PLAYER'S OWN SHOTS, COUNTED BY THE CLIENT'S OWN PREDICATE. `nohit` is written by
            // the recorder per frame: the projectiles `TrajectorySampler` declined because
            // `Projectile.HitsPlayer` was false or `LocalSeedDerived` was true, i.e. the ones the
            // client's `Hit()` can never route to `HitCharacter()`. On `20261015-151232-p5424` --
            // recorded before the exclusion existed -- 932 of 1210 tracked bullets were the player's
            // own, and they were 51.8 % of every threat record and 255 of its 1140 moves. A live run
            // of a shooting player should show a substantial number here; **zero means the exclusion
            // is not in force**, and `bootstrap.log`'s sampler resolution line says which field was
            // missing. Zero is also correct for a recording made before the field was resolved, which
            // is why this is reported rather than asserted.
            long noHit = 0;
            long framesWithNoHit = 0;
            long framesCarryingTheField = 0;
            foreach (RecordedFrame f in _trace.Frames)
            {
                noHit += f.NoHit;
                if (f.NoHit > 0)
                {
                    framesWithNoHit++;
                }
                if (f.NoHitRecorded)
                {
                    framesCarryingTheField++;
                }
            }
            if (framesCarryingTheField == 0)
            {
                Console.WriteLine("  declined, cannot hit: NOT RECORDED -- this trace predates the"
                                  + " `HitsPlayer`/`LocalSeedDerived` exclusion, so its threat set"
                                  + " contains the player's own shots and every count below is"
                                  + " inflated by them");
            }
            else
            {
                Console.WriteLine("  declined, cannot hit: " + noHit + " projectile(s) over "
                                  + framesWithNoHit + " of " + framesCarryingTheField + " frame(s) --"
                                  + " `HitsPlayer` false or `LocalSeedDerived` true, so the client"
                                  + " can never collide them with the player. Zero here on a session"
                                  + " where the player was shooting means the exclusion is not in"
                                  + " force; see bootstrap.log's `cannot-hit-the-player filter`");
            }
            Console.WriteLine("  resolved collisions : " + _trace.Hits.Count
                              + " (from the bootstrap log)");
            if (_trace.Grids.Count > 0)
            {
                GridWindow g = _trace.Grids[0];
                int solid = 0;
                foreach (byte cell in g.Cells)
                {
                    if (cell != 0) { solid++; }
                }
                Console.WriteLine("  collision grid      : " + _trace.Grids.Count + " window(s), first "
                                  + g.Width + "x" + g.Height + " at (" + g.OriginX + "," + g.OriginY
                                  + "), " + solid + " solid cell(s)");
            }
            // THE READER'S OWN WARNINGS, WHICH USED TO GO NOWHERE. `DodgeTrace.Warnings` collected
            // truncated lines and unknown record types and no caller ever printed the list -- the
            // same shape as every other defect in this spike: the information existed and nothing
            // looked at it. A recording that is missing its last record should say so in the
            // report, next to the frame count it is short of.
            for (int i = 0; i < _trace.Warnings.Count; i++)
            {
                Console.WriteLine("  [WARN] " + _trace.Warnings[i]);
            }
            if (Strict && _trace.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  --strict: " + _trace.Warnings.Count
                                  + " reader warning(s) treated as a failure.");
                _warningsFatal = true;
            }
            Console.WriteLine();
        }

        private bool _warningsFatal;

        private void ReportDecisions()
        {
            Console.WriteLine("-- decisions -----------------------------------------------------");
            Console.WriteLine("  action              : " + Describe(_actions));
            Console.WriteLine("  reason              : " + Describe(_reasons));
            if (_framesWithCandidates > 0)
            {
                Console.WriteLine("  candidates          : mean evaluated "
                                  + F((float)_evaluatedTotal / _framesWithCandidates)
                                  + ", mean admissible "
                                  + F((float)_admissibleTotal / _framesWithCandidates));
            }
            Console.WriteLine("  frames that moved   : " + _moveFrames + " of " + _frames
                              + " (" + Percent(_moveFrames, _frames) + ")");
            Console.WriteLine("  hysteresis holds    : " + _hysteresisHolds + " ("
                              + Percent(_hysteresisHolds, _moveFrames == 0 ? 1 : _moveFrames)
                              + " of moving frames)");
            // TWO DIFFERENT QUESTIONS, TWO DIFFERENT NUMBERS, AND THEY WERE ONE NUMBER FOR TWO
            // SESSIONS. `stutter` is the dodge against its OWN previous command on an adjacent
            // frame -- the thing the operator sees as the character jerking sideways. `overrides`
            // is the dodge against the PLAYER'S input heading -- the dodge taking the wheel, which
            // is its job. Reporting one figure for both made the dodge look jittery when it was
            // overriding and vice versa.
            Console.WriteLine("  stutter             : " + _adjacentReversals + " of " + _adjacentMoves
                              + " adjacent move(s) (" + Percent(_adjacentReversals, _adjacentMoves)
                              + ") turned past 90deg; mean turn "
                              + (_adjacentMoves > 0
                                  ? (_turnDegrees / _adjacentMoves).ToString("0.0", CultureInfo.InvariantCulture)
                                  : "0")
                              + "deg, and " + Percent(_adjacentHeadingChanges, _adjacentMoves)
                              + " changed heading at all");
            Console.WriteLine("  overrides intent    : " + _overridesIntent + " move(s) ("
                              + Percent(_overridesIntent, _moveFrames)
                              + " of moving frames) disagreed with the player's own heading by >90deg");
            // THE GOAL MEASURE, AND IT IS THE ONE THIS WORK EXISTS TO MOVE. `overrides intent` counts
            // the dodge taking the wheel, which is its job whenever the hands are pointed at a
            // bullet; this one counts the dodge carrying the character toward the point in front of
            // them rather than away from it, which is what "it feels better" means and what the plan
            // model was built to move. ON THE RECORDINGS --- NOT IN A LIVE RUN, WHERE THIS ONE
            // MOVES THE OTHER WAY. `lead` is the number to read from now on, and the two are printed
            // together because their disagreement IS the result: making the goal the player's own
            // held direction (HANDOFF-DIRECTED-DODGE.md section 5.1) drops `keeps its course` from
            // 97.1 % to 85.6 % on `p26156` while raising `follows the player's direction` from 0 %
            // to 64.9 %, and a reader who saw only the first line would call that a regression.
            if (_goalConsidered > 0)
            {
                Console.WriteLine("  keeps its course    : " + _goalAligned + " move(s) ("
                                  + Percent(_goalAligned, _goalConsidered)
                                  + " of moving frames) stayed within 90deg of the forward goal");
            }
            if (_goalConsidered > 0)
            {
                Console.WriteLine("  follows the player  : " + _playerAligned + " move(s) ("
                                  + Percent(_playerAligned, _goalConsidered)
                                  + " of moving frames) stayed within 90deg of the direction the"
                                  + " player was holding");
            }
            // THE NUMBER THE OPERATOR FEELS. A `stand` verdict with `StandStops` on is a frame on
            // which the pilot clears `GameInput.Moving` -- the character is held still while the
            // player is holding a direction. With it off the verdict is a no-op, and this line is
            // how the difference between the two presets is read.
            if (_config.StandStops)
            {
                long stands = 0;
                int standCount;
                if (_reasons.TryGetValue(DodgeReason.StandSafe, out standCount))
                {
                    stands = standCount;
                }
                Console.WriteLine("  stops the player    : " + stands + " frame(s) ("
                                  + Percent(stands, _frames)
                                  + ") -- `stand` clears the input; preset " + _config.PresetName);
            }
            else
            {
                Console.WriteLine("  stops the player    : 0 -- `stand` yields to the player's own"
                                  + " input; preset " + _config.PresetName);
            }
            if (_durabilityMs.Count > 0)
            {
                _durabilityMs.Sort();
                // THE WINDOW'S OWN COUNT, AND WITHOUT IT THE CHANGE IS UNFALSIFIABLE. Rule 6 has two
            // clauses -- the unconditional `dangerMs <= window` refusal and the one-sided comparison
            // beyond it -- and from the outside they are one rule, so a window scaled by a hundred
            // can leave every aggregate on this page identical. This is the share of refusals that
            // the window itself caused, and it is the number that says whether scaling it did
            // anything at all. Read it against `disc=0,window=<old>` on the same recording: if the
            // two are equal the window is not load-bearing on that session and the change is neither
            // proved nor disproved by it.
            Console.WriteLine("  rule 6 window       : "
                              + (_solver.WindowRefusals > 0
                                  ? _solver.WindowRefusals + " candidate refusal(s) were the unconditional"
                                    + " `inside the decision window` clause, of "
                                    + _refusalsTotal + " refusal(s) in total"
                                  : "the unconditional clause never fired -- rule 6 was decided entirely"
                                    + " by its one-sided comparison"));
            Console.WriteLine("  chosen durability   : p50 " + F(P(_durabilityMs, 0.50))
                                  + " ms, p10 " + F(P(_durabilityMs, 0.10))
                                  + " ms, min " + F(_durabilityMs[0]) + " ms");
            }
            Console.WriteLine();
            Console.WriteLine("-- the threats, by whether they should be dodged at all -----------");
            long tracked = 0;
            foreach (var pair in _verdicts)
            {
                tracked += pair.Value;
            }
            foreach (ThreatVerdict v in new[]
                     { ThreatVerdict.Dodgeable, ThreatVerdict.TooFast, ThreatVerdict.Inside, ThreatVerdict.Clear })
            {
                int n;
                _verdicts.TryGetValue(v, out n);
                Console.WriteLine("  " + VerdictName(v) + " : " + n + " of " + tracked
                                  + " threat record(s) (" + Percent(n, tracked) + ")");
            }
            Console.WriteLine("  frames with a threat that matters : " + _framesWithRelevantThreats
                              + "; of those " + _framesWithADodgeableThreat
                              + " had one the dodge could answer, and "
                              + _framesNothingToDoAbout + " (" + Percent(_framesNothingToDoAbout, _framesWithRelevantThreats)
                              + ") had nothing it could do");
            Console.WriteLine();
            ReportPath();
        }

        /// <summary>
        /// The pathfinder's own numbers, and they exist for one sentence in the handoff.
        ///
        /// `HANDOFF-DIRECTED-DODGE.md` section 6: "A pathfinder that is silently absent prints as
        /// 'the outrun ratio did not move', which is the same output as a pathfinder that ran and did
        /// nothing." So this block separates the two. `searches with a path` against `searches` says
        /// whether the planner ran at all; the reason histogram says, for every frame it did not,
        /// WHICH of the ways it declined -- and the four are not the same finding: `nogrid` on a
        /// recording is a recorder that did not carry the grid, `unreachable` is a real map saying
        /// the target is behind a wall, `nodecap` is a budget being spent, and `nogoal` is a frame
        /// where the player was not walking and the course was not measured either.
        ///
        /// AND THE COST IS PRINTED HERE RATHER THAN INGESTED INTO `ReportCost`, because it is measured
        /// on the solver's own clock inside `Plan` -- the harness's `decide()` cost already contains
        /// it, and adding the two would count it twice.
        /// </summary>
        private void ReportPath()
        {
            PathStats stats = _solver.Path.Stats;
            if (stats.Searches == 0)
            {
                Console.WriteLine("-- the path ------------------------------------------------------");
                Console.WriteLine("  THE PLANNER NEVER RAN: no frame reached it. See `PathPlanner.Plan`'s");
                Console.WriteLine("  call site in `DodgeSolver.Decide`; this line is the one that keeps a");
                Console.WriteLine("  disabled pathfinder from reading as an ineffective one.");
                Console.WriteLine();
                return;
            }

            long found = stats.Searches - stats.NoPath;
            Console.WriteLine("-- the path ------------------------------------------------------");
            Console.WriteLine("  searches            : " + stats.Searches
                              + " frame(s); " + found + " produced a path ("
                              + Percent(found, stats.Searches) + "), " + stats.NoPath
                              + " did not (" + Percent(stats.NoPath, stats.Searches) + ")");
            if (found > 0)
            {
                Console.WriteLine("  mean path           : " + F((float)(stats.NodesTotal / (double)found))
                                  + " node(s) expanded, " + F((float)(stats.LengthTotal / found))
                                  + " units long, and " + stats.Held + " frame(s) held the previous"
                                  + " waypoint");
            }
            Console.WriteLine("  node cap hit        : " + stats.NodeCapHits + " ("
                              + Percent(stats.NodeCapHits, stats.Searches)
                              + ") -- searches that spent " + _config.PathNodeCap
                              + " expansions without reaching the target");
            // THE ONE CLAIM ABOUT THE PATH THAT CAN BE FALSE WITHOUT ANYTHING ELSE MOVING: that it goes
            // AROUND walls rather than through them. The diagonal rule is the only thing in the planner
            // that can put a route through a wall, and the corridor scenario is the 165-solid-cell map
            // where it would show. A zero here and a path 24.7 units long against an open map
            // 12 is the evidence; either number alone is not.
            Console.WriteLine("  corner cuts through a wall : " + stats.WallCrossings
                              + " over " + stats.PathsFound + " path(s) found -- a diagonal step"
                              + " whose two orthogonal neighbours are BOTH solid; must be 0, and the"
                              + " diagonal rule exists to make it 0");
            Console.WriteLine("  declined, by reason : nogrid=" + stats.NoGridFrames
                              + " off=" + stats.DisabledFrames
                              + " nogoal=" + stats.NoGoalFrames
                              + " unreachable=" + stats.UnreachableFrames
                              + " (the rest are notarget/startblocked/toolarge)");
            Console.WriteLine("  cost                : " + F((float)(stats.MicrosecondsTotal / stats.Searches))
                              + " us per frame inside Plan, over " + stats.Searches
                              + " search(es) -- a SEPARATE clock from `decide()` above, which"
                              + " already contains it");
            Console.WriteLine();
        }

        private static string VerdictName(ThreatVerdict v)
        {
            switch (v)
            {
                case ThreatVerdict.Dodgeable: return "DODGEABLE     (arrives, and the player can be clear in time)";
                case ThreatVerdict.TooFast: return "TOO FAST      (arrives sooner than the player can clear it)";
                case ThreatVerdict.Inside: return "ALREADY TOUCHING (its disc contains the player now)";
                default: return "CLEAR         (its path never enters the player's disc)";
            }
        }

        private void ReportInvariants()
        {
            Console.WriteLine("-- invariants ----------------------------------------------------");
            if (ExpectFrames)
            {
                // FIRST, BECAUSE IT INVALIDATES EVERYTHING BELOW IT. An empty recording satisfies
                // every "for every frame" invariant vacuously, so reporting them as passes over no
                // frames is the harness lying in the most confident available tone.
                Report("F   the recording contains frames",
                       _frames > 0,
                       _frames == 0
                           ? "the trace has NO FRAMES -- only its header. The recorder was armed but "
                             + "never called, which happens when the game's frame path takes the "
                             + "native detour: the recorder runs from the MANAGED frame replacement, "
                             + "so `-Trace` must also pass `--frame-replacement`. Every invariant "
                             + "below is vacuously true over zero frames."
                           : _frames + " frame(s)");
            }
            Report("T5  stand is never rejected while durably safe",
                   _standRejectedWhileDurable == 0,
                   _standRejectedWhileDurable + " frame(s) moved while `stand` was admitted and"
                   + " durably safe; " + _standRefusedByRules + " frame(s) moved while durably safe"
                   + " but with `stand` refused by the admission rules (legitimate: pinned");
            Report("T6  no emitted move is inside a disc at arrival",
                   _movesInsideDiscAtArrival == 0 && _movesNotAdmissible == 0,
                   _movesInsideDiscAtArrival + " move(s) INSIDE a disc the player was not in, "
                   + _movesNotAdmissible + " move(s) the solver had refused, "
                   + _movesEscapeIncomplete + " move(s) still inside a disc the player started in"
                   + " (rule 4's documented exception: an improvement, not a violation)");
            Report("T10 hysteresis cannot hold an unsafe heading",
                   _unsafeHold == 0,
                   _unsafeHold + " held heading(s) with no admissible candidate");
            // THE PATH'S OWN SAFETY CLAIM, ASSERTED RATHER THAN INFERRED. A path is a route through
            // the client's collision grid, and the one way it can be wrong about a wall is a diagonal
            // corner cut -- a step whose two orthogonal neighbours are both solid, which is the exact
            // shape the diagonal rule forbids. The `corridor` scenario is 165 solid cells and its
            // paths run 24.7 units against an open map's 12, which is consistent with going round and
            // is not evidence of it; this is. It is asserted on the paths the planner RETURNED, which
            // is the same standard T6 holds the emitted moves to.
            Report("PATH no planned route cuts a corner through a wall",
                   _solver.Path.Stats.WallCrossings == 0,
                   _solver.Path.Stats.WallCrossings + " corner cut(s) over "
                   + _solver.Path.Stats.PathsFound + " path(s) found -- a diagonal step whose two"
                   + " orthogonal neighbours are BOTH solid. A non-zero count means the planner can"
                   + " route a dodge into scenery on a map with walls in it.");
            if (_trace.Hits.Count == 0)
            {
                if (_t7 != null && _t7.NotApplicable)
                {
                    Report("T7  predicted and actual collision agree", true,
                           "NOT EVALUATED: these are synthetic frames, so there is no game's own"
                           + " collision decision to agree with. " + _firstPrediction.Count
                           + " bullet(s) would have been predicted. T7 needs a recorded trace"
                           + " (--trace <run>/trace.jsonl --collisions <run>/bootstrap.log).");
                }
                else
                {
                    // A trace with no collisions cannot prove agreement, so the only thing that can
                    // fail here is a prediction the model should not have made.
                    bool ok = _t7 == null || _t7.PredictedNeverHit == 0;
                    Report("T7  predicted and actual collision agree", ok,
                           "no collisions in this recorded trace; " + _firstPrediction.Count
                           + " bullet(s) predicted, of which "
                           + (_t7 == null ? 0 : _t7.PredictedNeverHit)
                           + " entered the hit disc without the game resolving a hit"
                           + (_firstPrediction.Count == 0 ? " -- nothing was proved either way" : ""));
                    if (!ok)
                    {
                        foreach (string example in _t7FalsePositive)
                        {
                            Console.WriteLine("        false positive: " + example);
                        }
                    }
                }
            }
            else
            {
                Report("T7  predicted and actual collision agree",
                       _t7 != null && _t7.Unpredicted == 0,
                       _t7 == null ? "not computed"
                       : _t7.Unpredicted + " collision(s) the model never predicted");
            }

            // THE GUARD THE `p4508` DEFECT ASKED FOR. T7 above asks whether the model is right; this
            // asks whether the two things that both claim to be the model are the same thing. They
            // are separate failures and they need separate lines: a trace can pass T7 while the two
            // observers disagree (that is exactly what `p4508` did -- the harness was right and the
            // live observer was not), and a trace can fail T7 while both observers agree.
            if (_t7 != null && _t7.ParityCompared > 0)
            {
                long mismatches = _t7.ParityLiveOnly + _t7.ParityHarnessOnly;
                Report("T7P the live observer and the harness agree on every collision",
                       mismatches == 0,
                       mismatches == 0
                           ? _t7.ParityAgree + " of " + _t7.ParityCompared
                             + " resolved collision(s) agreed; on " + _t7.ParityUnwitnessed
                             + " of those the recorder wrote no path, so that half of the agreement"
                             + " is the cull's own negative rather than a second reading of the"
                             + " geometry"
                           : mismatches + " of " + _t7.ParityCompared
                             + " resolved collision(s) DIFFER: " + _t7.ParityHarnessOnly
                             + " the live observer missed, " + _t7.ParityLiveOnly
                             + " the live observer saw and this reader did not");
                if (mismatches != 0)
                {
                    foreach (string example in _parityMismatch)
                    {
                        Console.WriteLine("        " + example);
                    }
                }
            }
            else if (_trace.Hits.Count > 0)
            {
                Report("T7P the live observer and the harness agree on every collision", true,
                       "NOT COMPARABLE: none of the " + _trace.Hits.Count + " collision line(s)"
                       + " carried a `predicted=` field, so the live observer's own answer was not"
                       + " recorded. This is a silence, not an agreement -- re-run the session with"
                       + " a hook that logs it.");
            }

            // THE ASSERTION THAT HAS TEETH IN THE OTHER DIRECTION, AND THE ONLY ONE THAT CAN FAIL
            // FOR A DODGE THAT IS TOO TIMID. T5, T6 and T10 are all satisfied by a solver that
            // never moves at all -- "stand is never rejected" is trivially true if the solver
            // always stands -- so the scenarios whose whole purpose is to produce motion need a
            // check that fails when the motion does not happen. `Dodge` is that scenario, and this
            // is that check.
            if (ExpectMove)
            {
                Report("MOVE the solver steers when a bullet is clearly dodgeable",
                       _moveFrames > 0,
                       _moveFrames + " frame(s) of " + _frames + " moved; the scenario places a"
                       + " bullet whose closest approach is inside the hit disc and outside the"
                       + " dodge's one-frame reach, so a solver that never moves is too timid");
            }
            foreach (string example in _moveViolationExamples)
            {
                Console.WriteLine("        " + example);
            }
            if (_movesEscapeIncomplete > 0)
            {
                Console.WriteLine("        and " + _movesEscapeIncomplete + " move(s) that had to"
                                  + " escape a disc the player was already standing in:");
                foreach (string example in _moveEscapeExamples)
                {
                    Console.WriteLine("        " + example);
                }
            }
            foreach (string example in _adjacentReversalExamples)
            {
                Console.WriteLine("        reversal " + example);
            }
            foreach (string example in _holdViolationExamples)
            {
                Console.WriteLine("        " + example);
            }
            Console.WriteLine();
        }

        private void ReportT7()
        {
            Console.WriteLine("-- T7: the model against the game --------------------------------");
            if (_trace.Hits.Count == 0)
            {
                Console.WriteLine("  no collision lines were read. If the run had collisions and a");
                Console.WriteLine("  bootstrap.log was not passed with --collisions, this is silence,");
                Console.WriteLine("  not agreement.");
                Console.WriteLine();
                return;
            }
            Console.WriteLine("  collisions resolved : " + _t7.Hits);
            Console.WriteLine("  predicted by model  : " + _t7.Matched + " ("
                              + Percent(_t7.Matched, _t7.Hits) + ")");
            Console.WriteLine("  collision(s) NOT predicted : " + _t7.Unpredicted + " ("
                              + Percent(_t7.Unpredicted, _t7.Hits) + ") -- the false negatives");
            Console.WriteLine("  predicted, no hit          : " + _t7.PredictedNeverHit + " ("
                              + Percent(_t7.PredictedNeverHit, _t7.PredictedBullets) + ")");
            Console.WriteLine("     still arriving where the player had moved to : "
                              + _stillArrivingNoHit + "  <- the player left; not a model error");
            Console.WriteLine("     the bullet was gone before it was due          : "
                              + _bulletDiedFirst + "  <- the projection outlived the bullet");
            Console.WriteLine("     the bullet was elsewhere on arrival             : "
                              + _modelErrors + "  <- the model put it where it was not");
            Console.WriteLine("  mean warning to the hit : " + F(_t7.MeanWarningMs)
                              + " ms (frames from the first prediction to the collision)");
            foreach (string example in _t7Unpredicted)
            {
                Console.WriteLine("        unpredicted: " + example);
            }
            foreach (string example in _t7FalsePositive)
            {
                Console.WriteLine("        false positive: " + example);
            }
            if (_t7.ParityCompared > 0)
            {
                Console.WriteLine("  predictor parity    : " + _t7.ParityAgree + " agree, "
                                  + _t7.ParityHarnessOnly + " the live observer missed, "
                                  + _t7.ParityLiveOnly + " the live observer alone predicted, over "
                                  + _t7.ParityCompared + " collision(s) carrying a live"
                                  + " `predicted=` flag");
                Console.WriteLine("     of the agreements, " + _t7.ParityUnwitnessed
                                  + " were bullets the recorder wrote no path for, so the harness"
                                  + " matched them on the cull's own negative rather than on a"
                                  + " recorded path");
            }
            Console.WriteLine();
        }

        private void ReportCost()
        {
            if (_costUs.Count == 0)
            {
                return;
            }
            _costUs.Sort();
            _activeCostUs.Sort();
            Console.WriteLine("-- cost (the decision alone, no sensor, no sampling) --------------");
            Console.WriteLine("  decide() all frames : p50 " + F(P(_costUs, 0.50)) + " us, p99 "
                              + F(P(_costUs, 0.99)) + " us, max " + F(_costUs[_costUs.Count - 1])
                              + " us"
                              + (Repeat > 1 ? ("   (best of " + Repeat + ")") : ""));
            if (_activeCostUs.Count > 0)
            {
                Console.WriteLine("  decide() evaluating  : p50 " + F(P(_activeCostUs, 0.50)) + " us, p99 "
                                  + F(P(_activeCostUs, 0.99)) + " us, max "
                                  + F(_activeCostUs[_activeCostUs.Count - 1]) + " us, over "
                                  + _activeCostUs.Count + " of " + _costUs.Count + " frame(s)");
            }
            Console.WriteLine("  the 300 us frame budget is shared with the sampler and the sensor;");
            Console.WriteLine("  this number is only the solver's slice of it.");
            // WHERE THE TIME WENT, WHICH IS THE QUESTION A TOTAL CANNOT ANSWER. SPIKE3.2 section
            // 15.9.3b found p99 719 us on evaluating frames with the pathfinder exonerated both by its
            // own self-clock and by `axial=0` -- leaving the admission and the scoring walk, and
            // nothing in the project able to separate them. This is that separation: cumulative
            // microseconds per stage and each one's share of the whole decision.
            long stageFrames = _solver.StageFrames;
            if (stageFrames > 0)
            {
                double stageTotal = 0.0;
                for (int stage = 0; stage < DodgeSolver.StageNames.Length; stage++)
                {
                    stageTotal += _solver.StageMicroseconds(stage);
                }
                // THE STAGE CLOCKS ARE THE PER-FRAME ESTIMATOR, NOT A BEST-OF-N. Every repetition
                // runs `Decide` again and the stage buckets accumulate all of them, so dividing by
                // `StageFrames` gives the mean of every decision taken rather than the best one.
                // That is what makes it the number to compare against the frame budget: the
                // `decide()` p99 above is a best-of-N and therefore cannot see a collection that
                // landed in one repetition out of twenty.
                Console.WriteLine("  stage split (" + F((float)(stageTotal / stageFrames))
                                  + " us/frame over " + stageFrames + " decision(s), every"
                                  + " repetition counted):");
                for (int stage = 0; stage < DodgeSolver.StageNames.Length; stage++)
                {
                    double us = _solver.StageMicroseconds(stage);
                    Console.WriteLine("    " + DodgeSolver.StageNames[stage].PadRight(8)
                                      + " : " + F((float)(us / stageFrames)).PadLeft(8)
                                      + " us/frame, " + Percent((long)Math.Round(us), (long)Math.Round(stageTotal))
                                      + " of it");
                }
            }
            if (Repeat == 1)
            {
                Console.WriteLine("  A SINGLE-SHOT p99 IS NOT THE SOLVER'S COST: the harness allocates a"
                                  + " snapshot per frame,");
                Console.WriteLine("  so this number includes whichever collection landed inside the"
                                  + " timed window.");
                Console.WriteLine("  Re-run with --repeat 50 for the number to tune against.");
            }
            Console.WriteLine();
        }

        /// <summary>
        /// Dumps every recorded observation of one bullet, with the distance from its samples to
        /// the player's recorded position ON THAT FRAME.
        ///
        /// WHY THIS IS THE RIGHT FORM OF THE QUESTION. The obvious check -- "did this bullet's path
        /// ever come within r_eff of the player" -- is the one that produced a wrong finding twice,
        /// because the player moves ~8 world units over a 600 ms horizon and a path recorded at
        /// frame N describes positions the player has long left by frame N+36. The sampler's samples
        /// carry absolute positions and the bullet's own 30 ms step; the only comparison that means
        /// anything holds the player still for the length of ONE sample step, not one horizon.
        /// Printing the minimum over the whole run beside the minimum per frame makes the
        /// difference visible instead of implicit.
        /// </summary>
        private void DebugThreat(RecordedFrame frame, DodgeSnapshot snapshot)
        {
            foreach (Trajectory t in snapshot.Threats)
            {
                if ((int)(t.Key >> 32) != DebugThreatId)
                {
                    continue;
                }
                _debugThreatRadius = t.Radius;
                float r = frame.PlayerRadius + t.Radius;
                float best = float.MaxValue;
                int bestSample = -1;
                for (int s = 0; s < t.Count; s++)
                {
                    float dx = t.Xs[s] - frame.PlayerX;
                    float dy = t.Ys[s] - frame.PlayerY;
                    float d = (float)Math.Sqrt((dx * dx) + (dy * dy));
                    if (d < best)
                    {
                        best = d;
                        bestSample = s;
                    }
                }
                bool inside = best <= r;
                if (inside)
                {
                    _debugThreatEverInside = true;
                }
                if (best < _debugThreatBest)
                {
                    _debugThreatBest = best;
                    _debugThreatBestFrame = frame.Frame;
                }
                _debugThreatLines.Add("  frame " + frame.Frame
                                      + "  player (" + F(frame.PlayerX) + "," + F(frame.PlayerY) + ")"
                                      + "  rad " + F(t.Radius) + "  r_eff " + F(r)
                                      + "  samples " + t.Count
                                      + "  minDist " + F(best) + " @sample " + bestSample
                                      + " (" + F(bestSample * 30.0f) + " ms)"
                                      + "  inside=" + inside);
            }
        }

        /// <summary>Prints the `--debug-threat` dump and what it establishes.</summary>
        private void ReportDebugThreat()
        {
            if (DebugThreatId < 0)
            {
                return;
            }
            Console.WriteLine("-- debug threat " + DebugThreatId + " --------------------------------");
            if (_debugThreatLines.Count == 0)
            {
                Console.WriteLine("  NOT RECORDED in any frame -- the recorder's cull dropped it"
                                  + " (CouldReach) or it was never in the sensor's set.");
                Console.WriteLine();
                return;
            }
            for (int i = 0; i < _debugThreatLines.Count; i++)
            {
                Console.WriteLine(_debugThreatLines[i]);
            }
            Console.WriteLine("  observations        : " + _debugThreatLines.Count);
            Console.WriteLine("  radius (from trace) : " + F(_debugThreatRadius));
            Console.WriteLine("  closest to player   : " + F(_debugThreatBest)
                              + " at frame " + _debugThreatBestFrame);
            Console.WriteLine("  EVER inside r_eff   : " + _debugThreatEverInside
                              + "   <- if false, `TrackPredictions` correctly recorded no"
                              + " prediction and the model's path is the suspect");
            bool predicted = false;
            foreach (KeyValuePair<long, Prediction> entry in _firstPrediction)
            {
                if ((int)(entry.Key >> 32) == DebugThreatId)
                {
                    predicted = true;
                    Console.WriteLine("  prediction recorded : frame " + entry.Value.Frame
                                      + ", impact frame " + entry.Value.ImpactFrame
                                      + " (trace has " + _trace.Frames.Count + " frames)"
                                      + ", impactMs " + F(entry.Value.ImpactMs));
                }
            }
            if (!predicted)
            {
                Console.WriteLine("  prediction recorded : NONE");
            }
            Console.WriteLine();
        }

        private void Report(string name, bool ok, string detail)
        {
            Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + name);
            Console.WriteLine("         " + detail);
            if (!ok)
            {
                Failures++;
            }
        }

        // ===================================================================================
        // Small helpers
        // ===================================================================================

        private static long Key(int gameId, int startTime)
        {
            return ((long)gameId << 32) ^ (uint)startTime;
        }

        private static void Count<T>(Dictionary<T, int> map, T key)
        {
            map.TryGetValue(key, out int n);
            map[key] = n + 1;
        }

        private static string Describe<T>(Dictionary<T, int> map)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<T, int> entry in map)
            {
                parts.Add(entry.Key + "=" + entry.Value);
            }
            return parts.Count == 0 ? "none" : string.Join(" ", parts);
        }

        private static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Percent(long part, long whole)
        {
            if (whole == 0)
            {
                return "n/a";
            }
            return ((100.0 * part) / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        private static float Mean(List<float> values)
        {
            if (values.Count == 0)
            {
                return 0.0f;
            }
            double total = 0.0;
            foreach (float v in values)
            {
                total += v;
            }
            return (float)(total / values.Count);
        }

        /// <summary>The sorted list's p-th quantile, by nearest rank.</summary>
        private static float P(List<float> sorted, double p)
        {
            if (sorted.Count == 0)
            {
                return 0.0f;
            }
            int index = (int)Math.Round(p * (sorted.Count - 1));
            if (index < 0) { index = 0; }
            if (index >= sorted.Count) { index = sorted.Count - 1; }
            return sorted[index];
        }

        private static float Normalize(float radians)
        {
            while (radians > Math.PI) { radians -= (float)(2.0 * Math.PI); }
            while (radians < -Math.PI) { radians += (float)(2.0 * Math.PI); }
            return radians;
        }
    }
}
