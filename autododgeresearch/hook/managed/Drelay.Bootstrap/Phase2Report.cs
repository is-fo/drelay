// Phase2Report.cs -- the four numbers, on one line, and the histogram that makes one of
// them possible.
//
// WHY A HISTOGRAM AND NOT A MAXIMUM.
//
// The phase's exit criterion is a p99 per-frame cost, and a maximum cannot produce one.
// Phase 1 already learned this the hard way: its endurance run kept a cumulative worst
// case, its frame ring overflowed, and the single 323 microsecond sample it recorded could
// not be placed in time or in a distribution afterwards (PHASE1-VERIFICATION.md section
// 11.4).  A histogram is the smallest structure that answers "how often", and it costs a
// bucket increment on the hot path.
//
// THE EDGES ARE MICROSECONDS AND THEY ARE CHOSEN AROUND THE BUDGET.  HOOK-ARCHITECTURE.md
// section 7 gives the frame tick 150 us inside a 16.7 ms frame, so the buckets crowd below
// that and stay coarse above it: what matters is knowing whether the sensor is at 10 us or
// 120, and a run that lands in the overflow bucket has already failed.
//
// A BUCKET PERCENTILE IS AN INTERVAL, NOT A POINT, AND IT IS REPORTED AS ONE.  "p99 is at
// most 20 us" is true and useful; "p99 is 17 us" would be a number the histogram cannot
// support.

using System;
using System.Globalization;

namespace Drelay.Bootstrap
{
    /// <summary>Bucketed per-frame costs, in microseconds.</summary>
    public sealed class CostHistogram
    {
        /// <summary>
        /// Upper edges, in microseconds; the last bucket is everything above.
        ///
        /// THE BUDGET POINTS ARE EDGES, AND THAT IS THE WHOLE REASON THIS ARRAY LOOKS THE
        /// WAY IT DOES.  The phase's criterion is that the per-frame cost fits the
        /// frame-tick slice, and HOOK-ARCHITECTURE.md section 7 puts that slice at 150 us
        /// inside a 16.7 ms frame with a 300 us ceiling overall.  A histogram whose edges
        /// were 100 and 200 could only say "somewhere between", which is not an answer a
        /// criterion can be checked against -- and the first run of the Phase 2 session
        /// analyser produced exactly that: `sampler p99 <= 200 us`, reported as a failure
        /// when the truth was that the instrument could not decide.
        /// </summary>
        public static readonly int[] Edges = { 5, 10, 20, 50, 100, 150, 300, 500, 1000, 2000 };

        private readonly long[] _counts = new long[Edges.Length + 1];

        public long Total { get; private set; }

        public void Add(long microseconds)
        {
            Total++;
            for (int i = 0; i < Edges.Length; i++)
            {
                if (microseconds <= Edges[i])
                {
                    _counts[i]++;
                    return;
                }
            }
            _counts[Edges.Length]++;
        }

        /// <summary>
        /// The smallest edge at or below which <paramref name="percentile"/> of the samples
        /// fall -- i.e. an upper bound on the percentile, never a point estimate.
        /// </summary>
        public int UpperBoundAt(double percentile)
        {
            if (Total == 0)
            {
                return 0;
            }
            long want = (long)Math.Ceiling(Total * percentile);
            long seen = 0;
            for (int i = 0; i < _counts.Length; i++)
            {
                seen += _counts[i];
                if (seen >= want)
                {
                    return i < Edges.Length ? Edges[i] : Edges[Edges.Length - 1];
                }
            }
            return Edges[Edges.Length - 1];
        }

        /// <summary>True when the tail ran past the last edge, which is a failure and not a number.</summary>
        public bool Overflowed => _counts[Edges.Length] > 0;

        // `Edges[Edges.Length]` STOOD IN `Describe()` BELOW UNTIL 2026-10-12, AND IT
        // CRASHED THE REPORT EXACTLY WHEN THE REPORT HAD SOMETHING TO SAY.  The edges array
        // has `Edges.Length` entries, the bucket array has one more, and that expression
        // wanted the LAST EDGE while indexing one past it -- so `Describe()` threw
        // `IndexOutOfRangeException` the first time a sample landed above 2000 us, and not
        // before, because the other arm of the ternary never runs.  It was measured twice:
        //
        //   * on the live session of 2026-10-12 the report stopped after three lines
        //     (frames 300, 601 and 901 of 54 600) at the moment the read's maximum jumped
        //     from 459 us to 4 756 us, and the session's verdict then quoted the stale
        //     901-frame histogram as the phase's per-frame cost;
        //   * on the synthetic host it was reproduced in a single run, and the exception's
        //     own stack named this line -- which is why the frame path now logs its
        //     exceptions once instead of swallowing them in silence.
        //
        // An instrument that fails only on the runs that have an outlier to report is this
        // project's recurring shape, and this is its third instance.  `UpperBoundAt` above
        // had it right from the start, which is what made the mistake invisible in review.

        public string Describe()
        {
            if (Total == 0)
            {
                return "no sample";
            }
            return "n=" + Total + " p50<=" + UpperBoundAt(0.50) + "us p99<=" + UpperBoundAt(0.99)
                   + "us" + (Overflowed ? " (OVERFLOW: " + _counts[Edges.Length] + " above "
                                          + Edges[Edges.Length - 1] + "us)" : "");
        }
    }

    /// <summary>
    /// The phase's four decision numbers, in one place.
    ///
    /// IMPLEMENTATION-PLAN.md names them, and each decides something different: the
    /// warning distribution decides whether an escape can be dispatched in time (Phase 3's
    /// thresholds), the false negatives decide whether the model is complete enough to
    /// rely on at all, the false positives decide whether a dodge would be over-cautious,
    /// and the cost decides whether the Phase 4 solver fits the frame at all.
    ///
    /// They are printed together, on one line, on purpose: a reader who has to assemble
    /// the answer from four places in a log will assemble it wrongly, and a number that
    /// cannot be read is worse than no number -- the lesson Phase 1 wrote down after
    /// `logOverflow` and `logAppendFailures` were briefly the same counter.
    /// </summary>
    public static class Phase2Report
    {
        public static void Log()
        {
            double mean = SensorRead.Threats.MeanWarningMs;
            // NO PARENTHETICAL ABOUT WHICH PROCESS THIS IS.  The first version said
            // "(synthetic host; a live game run is what these are for)", which was true
            // while the host was the only thing it had ever run in and became a lie the
            // moment the hook resolved against the real client.  The bootstrap cannot tell
            // the two apart -- it resolves whatever assembly the launcher names -- so the
            // label belongs to whoever reads the evidence, and `Run-Phase2Session.ps1`
            // supplies it.
            HookEntry.AppendLog("INFO", "PHASE 2 NUMBERS"
                                        + " | warnings: " + SensorRead.Threats.PredictedAndHit
                                        + " predicted hit(s), mean "
                                        + mean.ToString("F1", CultureInfo.InvariantCulture) + "ms"
                                        + " | false negatives: " + SensorRead.Threats.HitUnpredicted
                                        + " (hit, not predicted)"
                                        + " | false positives: " + SensorRead.Threats.PredictedButNeverHit
                                        + " (predicted, retired without a hit)"
                                        + " | cost: read " + SensorRead.Cost.Describe()
                                        + ", sampler " + TrajectorySampler.Cost.Describe()
                                        // THE FIFTH NUMBER, AND THE SOURCE SPLIT, ON THE SAME LINE.
                                        // PHASE2-SCOPE.md section 2 names the straight fraction
                                        // separately from the four, and the 2026-10-12 evening session
                                        // is why the split is here too: `warnings: 0` was true and
                                        // useless, and the question it could not answer -- "did the
                                        // sensor see any bullets at all?" -- is answered by
                                        // `projectiles:` below it.  A reader must not have to open a
                                        // second line to find out that the first number had nothing to
                                        // count.
                                        // THE DECISION COUNT AND ITS PROVENANCE.  The first two
                                        // numbers are the threats set's view; this is the collision
                                        // observation's own, so a reader can see how many decisions
                                        // were counted and how many of them were for a bullet the
                                        // sensor had never seen (a coverage hole, which is a
                                        // different finding from a missed prediction).
                                        + " | collisions: " + CollisionObservation.Entered
                                        + " decision(s), " + CollisionObservation.Known
                                        + " known, " + CollisionObservation.Unknown + " unknown"
                                        + " | projectiles: " + SensorRead.LayerLiveTotal
                                        + " observation(s) from the layer's " + SensorRead.LayerNodesTotal
                                        + " node(s), " + SensorRead.ObjectsLiveTotal
                                        + " from World.Objects" + (SensorRead.LayerSourceResolved
                                            ? "" : " (LAYER SOURCE DID NOT RESOLVE)")
                                        + " | linearity: " + TrajectorySampler.StraightCount
                                        + " linear, " + TrajectorySampler.CurvedCount + " not, straight fraction "
                                        + TrajectorySampler.StraightFraction.ToString("F3", CultureInfo.InvariantCulture));
            HookEntry.FlushLog();
        }
    }
}
