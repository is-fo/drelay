// ThreatSet.cs -- the Phase 2 sensor's bookkeeping, and the gate that decides whether a
// trajectory may be projected.
//
// WHY THIS IS A SEPARATE FILE, AND WHY IT HAS A SELF-TEST.
//
// DODGE-ALGORITHM.md sections 3 and 4 specify two things that must be right no matter
// where the data comes from: how a bullet is retired when it stops being observed, and
// when a sampled trajectory may be extended past its last trustworthy point.  Both are
// pure functions of plain data -- no game pointers, no offsets, no managed reads -- so
// both can be exercised on their own, and that is what `SensorSelfTest` does on every
// run, in the bootstrap, before the hook patches anything.
//
// That matters more here than usual.  The tests they correspond to, T3 and T4 in
// IMPLEMENTATION-PLAN.md's list, are about failures that are invisible in a live run:
// a retired-but-live bullet shows up as a dodge that hesitates, and a ghost bullet that
// never dies shows up as a dodge that moves for no reason.  Neither looks like a bug in
// the log.  A self-test is the only place they are cheap to see.
//
// The rules, restated from DODGE-ALGORITHM.md section 3.3 so the code can be read
// against them:
//
//     present                        -> missStreak = 0, keep
//     absent and age < minAgeFrames  -> keep, and do NOT accrue a miss
//     absent otherwise               -> ++missStreak; retire once missStreak >= 2
//     a failed enumeration           -> reconcile nothing at all
//
// and the last of those is not a detail: "a failed read is not evidence of absence", so
// the caller that could not enumerate must not call `Reconcile`.

using System;
using System.Collections.Generic;

namespace Drelay.Bootstrap
{
    /// <summary>
    /// The identity of a live bullet: `(GameId, startTime)`, per DODGE-ALGORITHM.md
    /// section 3.2.
    ///
    /// THE PAIR IS THE POINT, NOT THE POINTER.  `Projectile` is pooled, and a re-rented
    /// instance gets a new `GameId`; state keyed on an object's address would silently
    /// inherit a dead bullet's history, which presents as "the dodge reacts to bullets
    /// that do not exist" (HOOK-ARCHITECTURE.md section 5.4).  Keying on the pair makes
    /// that impossible by construction, and `ThreatSet.Invalidate` makes it impossible
    /// again by observation.
    /// </summary>
    public readonly struct ThreatKey : IEquatable<ThreatKey>
    {
        public readonly int GameId;
        public readonly int StartTime;

        public ThreatKey(int gameId, int startTime)
        {
            GameId = gameId;
            StartTime = startTime;
        }

        /// <summary>Packed for a dictionary key; the two are 32 bits each by construction.</summary>
        public long Packed => ((long)GameId << 32) ^ (uint)StartTime;

        public bool Equals(ThreatKey other) => GameId == other.GameId && StartTime == other.StartTime;
        public override bool Equals(object obj) => obj is ThreatKey other && Equals(other);
        public override int GetHashCode() => Packed.GetHashCode();
        public override string ToString() => "(" + GameId + "," + StartTime + ")";
    }

    /// <summary>What is known about one bullet that is believed to be live.</summary>
    public struct ThreatRecord
    {
        public ThreatKey Key;
        public int FirstSeenFrame;
        public int LastSeenFrame;

        /// <summary>
        /// Consecutive frames the bullet was absent.  Only ever advanced by a frame in
        /// which the enumeration SUCCEEDED.
        /// </summary>
        public int MissStreak;

        /// <summary>
        /// Whether the sensor saw this bullet on a collision course with the player
        /// inside its horizon, and when.  This is Deliverable 2.3's prediction half: the
        /// warning a hit gets is the distance between this and the moment the game's own
        /// collision decision fires.
        /// </summary>
        public bool Predicted;
        public int FirstPredictedFrame;
        public long FirstPredictedTick;

        /// <summary>Set when the game's collision decision was observed for this bullet.</summary>
        public bool HitObserved;

        /// <summary>Frames since it was first seen; the age the min-age rule tests.</summary>
        public int Age(int frame) => frame - FirstSeenFrame;
    }

    /// <summary>
    /// The live-bullet set: one record per key, retired by evidence rather than by
    /// quota.
    /// </summary>
    public sealed class ThreatSet
    {
        /// <summary>
        /// A bullet observed at spawn may legitimately not appear in the live view for a
        /// few frames -- the object exists before whatever the frame path enumerates has
        /// caught up -- and charging a miss for that would retire live bullets.  Three
        /// frames is the grace the reference engine's reconcile also carries; it is a
        /// count of frames and not of milliseconds because the sensor works in frames.
        /// </summary>
        public int MinAgeFrames = 3;

        /// <summary>
        /// Two strikes: the smallest value that survives a single incomplete read.  One
        /// would retire a live bullet on any hiccup; three would let a ghost persist for
        /// frames the dodge is spending on it.
        /// </summary>
        public int RetireAfterMisses = 2;

        private readonly Dictionary<long, ThreatRecord> _records = new Dictionary<long, ThreatRecord>();
        private readonly HashSet<long> _seenThisFrame = new HashSet<long>();
        private readonly List<long> _retireNow = new List<long>();

        /// <summary>Records believed live.</summary>
        public int Count => _records.Count;

        /// <summary>Frames in which a reconcile retired at least one bullet.</summary>
        public int RetiredTotal { get; private set; }

        /// <summary>Frames whose enumeration failed and were therefore not reconciled.</summary>
        public int UnreconciledFrames { get; private set; }

        /// <summary>
        /// Bullets the sensor predicted would hit and that were retired without the game
        /// ever deciding they did.  This is the false-positive count, and it is the rate
        /// that decides whether a dodge would be over-cautious.
        /// </summary>
        public long PredictedButNeverHit { get; private set; }

        /// <summary>Bullets the game decided were hitting and the sensor had predicted.</summary>
        public long PredictedAndHit { get; private set; }

        /// <summary>Bullets the game decided were hitting and the sensor had not predicted.  The false negatives.</summary>
        public long HitUnpredicted { get; private set; }

        /// <summary>Warning, in milliseconds, accumulated over the predicted hits.</summary>
        public double WarningTotalMs { get; private set; }

        /// <summary>The single record for a key, if it is tracked.</summary>
        public bool TryGet(ThreatKey key, out ThreatRecord record)
        {
            return _records.TryGetValue(key.Packed, out record);
        }

        /// <summary>
        /// Marks that the sensor saw this bullet on a collision course inside its horizon.
        /// Only the FIRST such frame is kept, because the warning a hit is credited with
        /// is the warning the sensor actually had -- not the most recent glance at it.
        /// </summary>
        public bool MarkPredicted(ThreatKey key, int frame, long tick)
        {
            ThreatRecord record;
            if (!_records.TryGetValue(key.Packed, out record) || record.Predicted)
            {
                return false;
            }
            record.Predicted = true;
            record.FirstPredictedFrame = frame;
            record.FirstPredictedTick = tick;
            _records[key.Packed] = record;
            return true;
        }

        /// <summary>
        /// The game's collision decision, for a bullet the threat set knows about.
        ///
        /// RETURNS TRUE THE FIRST TIME ONLY, so the caller counts each hit once, and
        /// reports the warning the sensor actually had: the distance between the frame it
        /// first saw this bullet on a collision course and this instant.  A hit the sensor
        /// never predicted is a false negative and is counted as one -- that number is
        /// half of what decides whether the model is complete enough to rely on.
        /// </summary>
        public bool MarkHit(ThreatKey key, long tickNow, out double warningMs)
        {
            warningMs = 0.0;
            ThreatRecord record;
            if (!_records.TryGetValue(key.Packed, out record) || record.HitObserved)
            {
                return false;
            }
            record.HitObserved = true;
            _records[key.Packed] = record;

            if (record.Predicted)
            {
                PredictedAndHit++;
                warningMs = TicksToMs(tickNow - record.FirstPredictedTick);
                if (warningMs >= 0.0)
                {
                    WarningTotalMs += warningMs;
                }
                else
                {
                    warningMs = 0.0;
                }
            }
            else
            {
                HitUnpredicted++;
            }
            return true;
        }

        private static double TicksToMs(long ticks)
        {
            return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>Mean warning over the predicted hits, in milliseconds.</summary>
        public double MeanWarningMs => PredictedAndHit == 0 ? 0.0 : WarningTotalMs / PredictedAndHit;

        /// <summary>Starts a frame.  The seen-set is per frame, so a missed reconcile cannot leak into the next one.</summary>
        public void BeginFrame()
        {
            _seenThisFrame.Clear();
        }

        /// <summary>
        /// Records that a bullet was observed this frame.  Called once per live bullet
        /// the frame path could identify, and only with arguments -- see
        /// HOOK-ARCHITECTURE.md section 5.3: the spawn hook reads no managed field at
        /// all.
        /// </summary>
        public void Observe(ThreatKey key, int frame)
        {
            long packed = key.Packed;
            _seenThisFrame.Add(packed);

            ThreatRecord record;
            if (_records.TryGetValue(packed, out record))
            {
                record.MissStreak = 0;
                record.LastSeenFrame = frame;
                _records[packed] = record;
                return;
            }

            record = new ThreatRecord
            {
                Key = key,
                FirstSeenFrame = frame,
                LastSeenFrame = frame,
                MissStreak = 0
            };
            _records[packed] = record;
        }

        /// <summary>
        /// Ends a frame in which the enumeration SUCCEEDED.  Anything not seen accrues a
        /// miss, unless it is still inside its grace, and anything at two strikes is
        /// retired.
        ///
        /// A FAILED ENUMERATION MUST NOT CALL THIS.  Skipping it charges no miss, which
        /// is exactly the intended asymmetry: a read that did not happen is not evidence
        /// that a bullet is gone, and treating it as such is how a live bullet gets
        /// retired by a hiccup.
        /// </summary>
        public int Reconcile(int frame)
        {
            _retireNow.Clear();
            foreach (KeyValuePair<long, ThreatRecord> entry in _records)
            {
                if (_seenThisFrame.Contains(entry.Key))
                {
                    continue;
                }

                ThreatRecord record = entry.Value;
                if (record.Age(frame) < MinAgeFrames)
                {
                    continue;
                }

                record.MissStreak = record.MissStreak + 1;
                if (record.MissStreak >= RetireAfterMisses)
                {
                    _retireNow.Add(entry.Key);
                }                else
                {
                    _records[entry.Key] = record;
                }
            }

            for (int i = 0; i < _retireNow.Count; i++)
            {
                // A PREDICTED BULLET THAT NEVER HIT IS THE FALSE-POSITIVE COUNT, and it is
                // only knowable here: retirement is the moment the evidence says the
                // bullet is gone, and if the game's collision decision never fired for it
                // then the sensor's prediction was one the player was never going to be
                // hit by.  That rate is what decides whether a dodge would be
                // over-cautious.
                ThreatRecord record;
                if (_records.TryGetValue(_retireNow[i], out record) && record.Predicted
                    && !record.HitObserved)
                {
                    PredictedButNeverHit++;
                }
                _records.Remove(_retireNow[i]);
            }
            RetiredTotal += _retireNow.Count;
            return _retireNow.Count;
        }

        /// <summary>Records that this frame's enumeration failed, for the evidence and for the self-test.</summary>
        public void NoteUnreconciledFrame()
        {
            UnreconciledFrames++;
        }

        /// <summary>
        /// Drops a record outright, because the instance behind the key was observed
        /// being re-rented or reset.  Belt and braces next to the keyed identity: the
        /// pair already makes inheritance impossible, and this makes it explicit the
        /// moment the pool hands the object out again.
        /// </summary>
        public bool Invalidate(ThreatKey key)
        {
            return _records.Remove(key.Packed);
        }

        /// <summary>Everything goes -- a zone change, or `World.ClearWorld`.</summary>
        public void InvalidateAll()
        {
            _records.Clear();
            _seenThisFrame.Clear();
        }

        /// <summary>Read-only view, for the sensor's own decision pass.</summary>
        public void CopyRecords(List<ThreatRecord> into)
        {
            into.Clear();
            foreach (KeyValuePair<long, ThreatRecord> entry in _records)
            {
                into.Add(entry.Value);
            }
        }
    }

    /// <summary>
    /// The linearity gate: may a sampled trajectory be projected past its trace?
    ///
    /// DODGE-ALGORITHM.md section 4.4 rule 3.  A bullet whose samples all lie close to
    /// the straight line through its endpoints is one the sensor may extend with
    /// confidence; a wavy, orbiting or bouncing one is not, and extending it would
    /// paint danger where the bullet is not and safety where it is.  The gate is the
    /// only place a lane is ever extended, which is why it is a function with a
    /// self-test rather than three lines inside the sampler.
    /// </summary>
    public static class LinearityGate
    {
        /// <summary>How far an interior sample may sit off the endpoint chord.</summary>
        public const float DefaultTolerance = 0.05f;

        /// <summary>
        /// <paramref name="xs"/>/<paramref name="ys"/> hold <paramref name="count"/>
        /// points, oldest first, on a fixed time grid.
        /// </summary>
        public static bool IsLinear(float[] xs, float[] ys, int count, float tolerance)
        {
            // Fewer than three points is not a trajectory -- there is no interior sample
            // to contradict the chord, so the honest answer is "not established", which
            // is what a false return means here.  The first version of this returned
            // true for two points, which would have let a single frame of motion be
            // extrapolated to the horizon.
            if (count < 3)
            {
                return false;
            }

            float ax = xs[0];
            float ay = ys[0];
            float bx = xs[count - 1];
            float by = ys[count - 1];

            float dx = bx - ax;
            float dy = by - ay;
            float span = (float)Math.Sqrt((dx * dx) + (dy * dy));

            // A stationary trace has no direction to project along.  "Zero span" is the
            // reference engine's own condition and it is the one that matters most: a
            // bullet whose samples all coincide is exactly the case where extrapolating
            // would invent a lane out of nothing.
            if (span <= 0.0f)
            {
                return false;
            }

            for (int i = 1; i < count - 1; i++)
            {
                // Perpendicular distance from the interior point to the chord, which is
                // the quantity the tolerance is about.  Computed as the cross product
                // over the span so it is a distance and not an area.
                float cross = ((xs[i] - ax) * dy) - ((ys[i] - ay) * dx);
                float distance = (float)Math.Abs(cross) / span;
                if (distance > tolerance)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
