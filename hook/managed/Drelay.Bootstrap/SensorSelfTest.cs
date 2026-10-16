// SensorSelfTest.cs -- T3 and T4, run on every launch, in the bootstrap.
//
// WHY A SELF-TEST AND NOT A UNIT TEST PROJECT.
//
// The hook ships as a startup hook and a native DLL; there is no test runner in the
// process and adding one would mean a second build configuration that nobody would run.
// The project already has the pattern that fits: `selfcheck.txt` is the hook's own
// acceptance test, made of checks that run where the hook runs (HOOK-ARCHITECTURE.md
// section 9).  These are the same kind of check, for the two pieces of Phase 2 that are
// pure functions of plain data.
//
// WHAT THEY CATCH, AND WHY IT IS WORTH THE CODE.
//
// Both failures are invisible in a live run.  A bullet retired while it is still alive
// makes the dodge hesitate; a bullet that never retires makes it move for no reason.
// Neither looks like a defect in a log -- they look like a badly tuned dodge, which is
// the failure mode IMPLEMENTATION-PLAN.md section 2 says offline tests exist to prevent.
// The rules are four lines each and every one of them has a wrong version that is one
// character away.
//
// The results go into the bootstrap's log, and the injection suite asserts that the run
// reports zero failures.  A self-test that is only run by hand is a self-test that is
// only run once.

using System;
using System.Collections.Generic;

namespace Drelay.Bootstrap
{
    public static class SensorSelfTest
    {
        /// <summary>One assertion's outcome, kept for the summary line.</summary>
        private sealed class Result
        {
            public string Name;
            public bool Ok;
            public string Detail;
        }

        /// <summary>
        /// A set built with this run's configured rules.
        ///
        /// THE RULES ARE READ FROM THE ENVIRONMENT SO THE SELF-TEST CAN BE SHOWN TO FAIL.
        /// A self-test whose assertions always pass because the code under test agrees
        /// with itself is worth nothing -- the project already learned that from
        /// `tools/Test-FingerprintGate.ps1`, which exists to prove the fingerprint gate
        /// refuses a mutated build.  Setting `DRELAY_SENSOR_RETIRE_MISSES=1` configures a
        /// rule that is knowingly wrong (one strike retires a live bullet on any
        /// hiccup), and the assertion that catches it -- T4b -- must then fail.  The
        /// injection suite runs exactly that, and would fail if this self-test ever
        /// stopped having teeth.
        /// </summary>
        private static ThreatSet NewSet()
        {
            return new ThreatSet
            {
                MinAgeFrames = Knob("DRELAY_SENSOR_MIN_AGE", 3),
                RetireAfterMisses = Knob("DRELAY_SENSOR_RETIRE_MISSES", 2)
            };
        }

        /// <summary>A positive integer from the environment, or the fallback.  Plain `DRELAY_` names, per PHASE1-HANDOFF.md section 4 trap 1.</summary>
        private static int Knob(string name, int fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            int parsed;
            if (!string.IsNullOrEmpty(value) && int.TryParse(value, out parsed) && parsed > 0)
            {
                return parsed;
            }
            return fallback;
        }

        /// <summary>
        /// Runs every assertion and returns the counts.  Never throws: a self-test that
        /// can take the hook down is worse than the bug it is looking for, so each
        /// assertion is run inside its own guard and a throw is counted as a failure.
        /// </summary>
        public static void Run(out int passed, out int failed)
        {
            var results = new List<Result>();

            Check(results, "T4a a bullet is kept while it is seen every frame", () =>
            {
                ThreatSet set = NewSet();
                for (int frame = 0; frame < 10; frame++)
                {
                    set.BeginFrame();
                    set.Observe(new ThreatKey(7, 100), frame);
                    set.Reconcile(frame);
                }
                return set.Count == 1;
            });

            Check(results, "T4b one absence does not retire a live bullet", () =>
            {
                ThreatSet set = NewSet();
                for (int frame = 0; frame < 5; frame++)
                {
                    set.BeginFrame();
                    set.Observe(new ThreatKey(7, 100), frame);
                    set.Reconcile(frame);
                }
                set.BeginFrame();                       // frame 5: absent once
                set.Reconcile(5);
                bool kept = set.Count == 1;
                set.BeginFrame();                       // frame 6: present again
                set.Observe(new ThreatKey(7, 100), 6);
                set.Reconcile(6);
                return kept && set.Count == 1;
            });

            Check(results, "T4c TWO consecutive absences retire it", () =>
            {
                ThreatSet set = NewSet();
                for (int frame = 0; frame < 5; frame++)
                {
                    set.BeginFrame();
                    set.Observe(new ThreatKey(7, 100), frame);
                    set.Reconcile(frame);
                }
                set.BeginFrame();
                set.Reconcile(5);
                bool afterOne = set.Count == 1;
                set.BeginFrame();
                set.Reconcile(6);
                return afterOne && set.Count == 0;
            });

            Check(results, "T4d a returning bullet resets the miss streak", () =>
            {
                ThreatSet set = NewSet();
                for (int frame = 0; frame < 5; frame++)
                {
                    set.BeginFrame();
                    set.Observe(new ThreatKey(7, 100), frame);
                    set.Reconcile(frame);
                }
                // Absent, present, absent, absent: the streak must restart at the
                // reappearance, so the second absence is still only the first strike.
                set.BeginFrame();
                set.Reconcile(5);
                set.BeginFrame();
                set.Observe(new ThreatKey(7, 100), 6);
                set.Reconcile(6);
                set.BeginFrame();
                set.Reconcile(7);
                bool aliveAfterOneMoreMiss = set.Count == 1;
                set.BeginFrame();
                set.Reconcile(8);
                return aliveAfterOneMoreMiss && set.Count == 0;
            });

            Check(results, "T4e a bullet inside its grace is not charged a miss", () =>
            {
                // Seen once, then absent for the whole grace window: it must survive all
                // of it, because being absent while young is "not yet", not "gone".
                ThreatSet set = NewSet();
                set.BeginFrame();
                set.Observe(new ThreatKey(7, 100), 0);
                set.Reconcile(0);
                for (int frame = 1; frame <= set.MinAgeFrames; frame++)
                {
                    set.BeginFrame();
                    set.Reconcile(frame);
                    if (set.Count != 1)
                    {
                        return false;
                    }
                }
                return true;
            });

            Check(results, "T4f a FAILED enumeration accrues no miss (no reconcile, no strike)", () =>
            {
                // The rule with the most expensive wrong version: if the caller
                // reconciles after a read that did not happen, one hiccup retires a live
                // bullet.  Modelled by simply not reconciling, and then reconciling
                // twice more than the retire threshold would allow if the skipped frames
                // had counted.
                ThreatSet set = NewSet();
                for (int frame = 0; frame < 5; frame++)
                {
                    set.BeginFrame();
                    set.Observe(new ThreatKey(7, 100), frame);
                    set.Reconcile(frame);
                }
                for (int frame = 5; frame < 12; frame++)
                {
                    set.BeginFrame();
                    set.NoteUnreconciledFrame();        // enumeration failed; no Reconcile
                }
                return set.Count == 1;
            });

            Check(results, "T4g a new bullet is admitted on its first observation", () =>
            {
                ThreatSet set = NewSet();
                set.BeginFrame();
                set.Observe(new ThreatKey(11, 200), 0);
                set.Reconcile(0);
                return set.Count == 1;
            });

            Check(results, "T3a invalidating a key removes its record at once", () =>
            {
                ThreatSet set = NewSet();
                set.BeginFrame();
                set.Observe(new ThreatKey(3, 9), 0);
                set.Reconcile(0);
                bool removed = set.Invalidate(new ThreatKey(3, 9));
                return removed && set.Count == 0;
            });

            Check(results, "T3b a RE-RENTED object cannot inherit the old bullet's state", () =>
            {
                // The pooling hazard, as a test.  Same startTime, different GameId -- the
                // shape a re-rent produces -- must not be the same record, so the new
                // bullet is admitted with a fresh age rather than inheriting the old
                // one's history.
                ThreatSet set = NewSet();
                set.BeginFrame();
                set.Observe(new ThreatKey(3, 9), 0);
                set.Reconcile(0);
                for (int frame = 1; frame < 20; frame++)
                {
                    set.BeginFrame();
                    set.Reconcile(frame);
                }
                bool oldGone = set.Count == 0;

                set.BeginFrame();
                set.Observe(new ThreatKey(4, 9), 20);   // re-rented: new GameId, same startTime
                set.Reconcile(20);
                bool fresh = set.Count == 1;

                // And the fresh one is inside its grace, not carrying twenty frames of
                // age: absent for the whole window and still kept.
                for (int frame = 21; frame <= 20 + set.MinAgeFrames; frame++)
                {
                    set.BeginFrame();
                    set.Reconcile(frame);
                    if (set.Count != 1)
                    {
                        return false;
                    }
                }
                return oldGone && fresh;
            });

            Check(results, "T3c invalidating an unknown key is a no-op, not an insert", () =>
            {
                ThreatSet set = NewSet();
                set.Invalidate(new ThreatKey(99, 99));
                return set.Count == 0;
            });

            Check(results, "gate: a straight trace may be projected", () =>
            {
                float[] xs = { 0f, 1f, 2f, 3f, 4f };
                float[] ys = { 0f, 2f, 4f, 6f, 8f };
                return LinearityGate.IsLinear(xs, ys, 5, LinearityGate.DefaultTolerance);
            });

            Check(results, "gate: a wavy trace may NOT be projected", () =>
            {
                float[] xs = { 0f, 1f, 2f, 3f, 4f };
                float[] ys = { 0f, 3f, -3f, 3f, 0f };
                return !LinearityGate.IsLinear(xs, ys, 5, LinearityGate.DefaultTolerance);
            });

            Check(results, "gate: a zero-span trace may NOT be projected", () =>
            {
                float[] xs = { 5f, 5f, 5f, 5f };
                float[] ys = { 5f, 5f, 5f, 5f };
                return !LinearityGate.IsLinear(xs, ys, 4, LinearityGate.DefaultTolerance);
            });

            Check(results, "gate: two points are not a trajectory", () =>
            {
                float[] xs = { 0f, 1f };
                float[] ys = { 0f, 1f };
                return !LinearityGate.IsLinear(xs, ys, 2, LinearityGate.DefaultTolerance);
            });

            Check(results, "gate: a trace just inside the tolerance passes, just outside fails", () =>
            {
                // One interior sample off the chord by a known distance, measured
                // perpendicular to it.  The chord here is the x axis, so the offset is
                // the y value and the check is exact.
                float[] inside = { 0f, 1f, 2f, 3f, 4f };
                float[] insideYs = { 0f, 0.04f, 0f, 0f, 0f };
                float[] outside = { 0f, 1f, 2f, 3f, 4f };
                float[] outsideYs = { 0f, 0.06f, 0f, 0f, 0f };
                return LinearityGate.IsLinear(inside, insideYs, 5, LinearityGate.DefaultTolerance)
                    && !LinearityGate.IsLinear(outside, outsideYs, 5, LinearityGate.DefaultTolerance);
            });

            // THE HISTOGRAM'S OVERFLOW PATH, WHICH IS THE ONLY PATH THAT EVER CRASHED.
            //
            // `Describe()` indexed `Edges[Edges.Length]` -- one past the end -- and the
            // mistake was invisible until a sample landed above the last edge, so every run
            // that had an outlier worth reporting lost its numbers instead of reporting it
            // (PHASE2-SCOPE.md section 4.14, where that cost a live session's cost
            // percentiles).  A self-test that never fills the overflow bucket would have
            // exactly the blind spot of the code it guards, so this one fills it on
            // purpose: if `Describe()` throws, `Check` records the exception by name.
            Check(results, "histogram: an overflowing bucket can still be described", () =>
            {
                CostHistogram histogram = new CostHistogram();
                histogram.Add(3);
                histogram.Add(9000);
                if (!histogram.Overflowed)
                {
                    return false;
                }
                string text = histogram.Describe();
                return text.Contains("OVERFLOW") && text.Contains("above 2000us");
            });

            passed = 0;
            failed = 0;
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].Ok)
                {
                    passed++;
                }
                else
                {
                    failed++;
                    HookEntry.AppendLog("ERROR", "sensor self-test FAILED: " + results[i].Name
                                                 + (string.IsNullOrEmpty(results[i].Detail)
                                                        ? "" : " -- " + results[i].Detail));
                }
            }

            HookEntry.AppendLog(failed == 0 ? "INFO" : "ERROR",
                                "sensor self-test: " + results.Count + " assertion(s), " + passed
                                + " passed, " + failed + " failed (threat set: two-strike retirement "
                                + "with a min-age grace and no strike for a failed read; linearity gate)");
            HookEntry.FlushLog();
        }

        /// <summary>
        /// Runs one assertion so that a throw is a failed check and not a dead hook.
        /// </summary>
        private static void Check(List<Result> results, string name, Func<bool> assertion)
        {
            var result = new Result { Name = name, Ok = false, Detail = "" };
            try
            {
                result.Ok = assertion();
            }
            catch (Exception ex)
            {
                result.Detail = ex.GetType().Name + ": " + ex.Message;
            }
            results.Add(result);
        }
    }
}

