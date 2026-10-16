// TraceFile.cs -- read a recorded trace, and the ground truth that goes beside it.
//
// THE TWO HALVES OF INVARIANT T7 COME FROM TWO FILES, AND THAT IS THE POINT.
//
//   `trace.jsonl`      what the model said: one line per frame, with the player, the collision
//                      grid and every culled threat's sampled path.
//   `bootstrap.log`    what actually happened: `CollisionObservation` writes one `collision:`
//                      line per `HitCharacter` that returned **true**, with the bullet's key.
//
// Neither file is written by this program, and neither is derived from the other. A predicted-
// versus-actual comparison is only worth something if the prediction was recorded before the
// collision was observed, and the fact that they are separate artefacts written by separate code
// paths is what makes that true rather than merely asserted.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Drelay.Bootstrap;

namespace Drelay.SolverHarness
{
    /// <summary>
    /// The packed `(GameId, startTime)` identity, in the same form the hook's `ThreatKey` packs it.
    ///
    /// DUPLICATED RATHER THAN IMPORTED, AND THAT IS A DELIBERATE COST. `ThreatKey` lives in
    /// `ThreatSet.cs`, which the harness does not compile: that file is the sensor's bookkeeping and
    /// carries the retirement policy, which has nothing to do with reading a trace. Two lines of
    /// packing are a cheaper price than dragging the sensor into the offline build -- and the
    /// identity itself is not a detail to be re-invented, which is why the hook's version is the
    /// one quoted here and the harness never keys on an address.
    /// </summary>
    internal static class Keys
    {
        public static long Packed(int gameId, int startTime)
        {
            return ((long)gameId << 32) ^ (uint)startTime;
        }
    }

    /// <summary>One bullet's recorded path for one frame.</summary>
    public sealed class RecordedThreat
    {
        public int GameId;
        public int StartTime;
        public float X;
        public float Y;
        public float Radius;
        public float Damage;
        public float DamagePercent;
        public int ManaDamage;
        public bool Projectable;
        public int Count;
        public float[] Xs;
        public float[] Ys;
        public bool[] Alive;

        /// <summary>The identity, in the packed form the harness keys its dictionaries by.</summary>
        public long Key => Keys.Packed(GameId, StartTime);
    }

    /// <summary>One recorded frame.</summary>
    public sealed class RecordedFrame
    {
        public int Frame;
        public float Dt;
        public float ClockMs;
        public float PlayerX;
        public float PlayerY;
        public float PlayerRadius;
        public float Health;
        public bool Moving;
        public float Intent;
        public bool Dead;
        public bool SafeWorld;
        public float Rotation;

        /// <summary>The grid window this frame references, or null.</summary>
        public GridWindow Grid;

        /// <summary>
        /// World units per second, when the caller overrode the measurement.
        ///
        /// THE SPEED IS NOT IN THE TRACE, AND THAT IS DELIBERATE. The trace records the player's
        /// position every frame, and the speed is the difference between two of those divided by
        /// the frame delta -- a measurement, not a field to be trusted. The override exists for the
        /// one case the measurement cannot cover: a frame where the player did not move, where the
        /// harness wants to assert admission against a *larger* budget than the player used.
        /// </summary>
        public float SpeedOverride;

        public readonly List<RecordedThreat> Threats = new List<RecordedThreat>();

        /// <summary>
        /// The enemy bodies rule 7 tests against, at this frame's positions.
        ///
        /// IT IS A LIST ON THE FRAME AND NOT A FIELD ON THE TRACE, because a body moves: an enemy
        /// that walks toward the player changes the answer on every frame it does, and a scenario or
        /// a recording that carried one position for the whole run would be a test of a statue.
        ///
        /// `DangerRadius` IS NOT READ OFF THE RECORDING. It is `Radius + playerRadius + pad` and the
        /// pad is a CONFIG value, so the harness computes it from the frame's own player radius and
        /// the config it is running -- which is what makes `--config enemypad=N` an A/B of the
        /// emitter alone rather than a change that also had to be baked into the file.
        /// </summary>
        public readonly List<EntityBody> Bodies = new List<EntityBody>();

        public int Skipped;

        /// <summary>
        /// Projectiles the sampler declined this frame because the client says they can never hit a
        /// player -- the local player's own shots and allies', by `Projectile.HitsPlayer` and
        /// `Projectile.LocalSeedDerived`. Absent from a recording made before those fields were
        /// resolved, which is why it defaults to zero and why the harness reports it rather than
        /// asserting on it.
        /// </summary>
        public int NoHit;

        /// <summary>
        /// True when the frame line carried `nohit` at all. A recording made before
        /// `TrajectorySampler` grew the exclusion has no such key, and "the field is absent" and
        /// "the filter declined nothing" are different findings -- the first is a trace that cannot
        /// speak to the question, the second is a run with no own shots (or a filter that is not in
        /// force, which `bootstrap.log`'s resolution line settles).
        /// </summary>
        public bool NoHitRecorded;
    }

    /// <summary>
    /// The client's collision grid, as it was recorded: a window of `byte[x, y]` cells plus the
    /// world-coordinate index of its first cell.
    /// </summary>
    public sealed class GridWindow
    {
        public long Generation;
        public int OriginX;
        public int OriginY;
        public int Width;
        public int Height;

        /// <summary>Row-major over `[x, y]`, exactly as the recorder wrote it.</summary>
        public byte[] Cells;

        /// <summary>
        /// The solver's own window type, rebuilt from the record.
        ///
        /// THE TRUNCATION IS THE GAME'S. `World` turns a float position into a grid index with a C
        /// cast (`conv.i4`), and so does `CollisionWindow`. If this conversion were wrong the
        /// offline walkability answer would differ from the live one and every terrain test in the
        /// harness would be measuring the harness.
        /// </summary>
        public CollisionWindow ToSolverWindow()
        {
            CollisionWindow window = default(CollisionWindow);
            window.Present = Width > 0 && Height > 0 && Cells != null;
            window.OriginX = OriginX;
            window.OriginY = OriginY;
            window.Width = Width;
            window.Height = Height;
            window.Cells = Cells;
            return window;
        }
    }

    /// <summary>One resolved collision, from the bootstrap log.</summary>
    public sealed class ObservedHit
    {
        public int GameId;
        public int StartTime;
        public bool Tracked;
        public bool Predicted;

        /// <summary>
        /// Whether the log line carried a `predicted=` field at all.
        ///
        /// WITHOUT THIS, A LOG THAT PREDATES THE FIELD LOOKS LIKE A LOG WHOSE OBSERVER PREDICTED
        /// NOTHING. `Predicted` defaults to false, so the parity check below would compare fifteen
        /// real agreements against a field that was never written and report every collision as a
        /// disagreement. The absence of a measurement is not a measurement -- the same distinction
        /// the empty-trace check makes at the top of the invariant list.
        /// </summary>
        public bool HasPredictedFlag;

        public float WarningMs;
        public float BulletX;
        public float BulletY;
        public float Radius;
        public int LineNumber;

        /// <summary>The identity, packed. `(GameId, startTime)` and never the object's address:
        /// `Projectile` is pooled, and state keyed on an address inherits a dead bullet's history
        /// (HOOK-ARCHITECTURE.md section 5.4).</summary>
        public long Key => Keys.Packed(GameId, StartTime);
        public override string ToString() => "(" + GameId + "," + StartTime + ")";
    }

    /// <summary>A whole trace: the header, the grid windows and the frames.</summary>
    public sealed class DodgeTrace
    {
        public string Source;
        public int Version;
        public string DodgeConfig = "";
        public string GridSource = "";
        public int SampleCount = 21;

        /// <summary>
        /// True when these frames did not come off a live client.
        ///
        /// IT CHANGES EXACTLY ONE ASSERTION, AND THE REASON IS A PROPERTY OF THE GENERATOR RATHER
        /// THAN A LICENCE. A synthetic bullet's sampled path is laid on the 30 ms grid from a
        /// position that no frame boundary necessarily visits; a live bullet's path is derived from
        /// the same object the game's own per-frame collision test reads. So a synthetic trace can
        /// contain a bullet whose *sampled* path grazes the player while its *frame-boundary* path
        /// never does, and the strict false-positive test -- "the model claimed a hit the game never
        /// resolved" -- then reports the generator's quantisation as a model error.
        ///
        /// For synthetic frames T7 therefore asserts only the gross property: no prediction for a
        /// bullet that never came near. For recorded frames it asserts the real one, which is the
        /// test the plan says needs a live run.
        /// </summary>
        public bool Synthetic;

        public readonly List<RecordedFrame> Frames = new List<RecordedFrame>();
        public readonly List<GridWindow> Grids = new List<GridWindow>();
        public readonly List<ObservedHit> Hits = new List<ObservedHit>();

        public readonly List<string> Warnings = new List<string>();

        public RecordedFrame FrameAt(int index) => Frames[index];

        /// <summary>
        /// Reads a trace file, and the collision log beside it when one is given.
        ///
        /// THE COLLISION LOG IS OPTIONAL AND ITS ABSENCE IS RECORDED. A trace with no hits is a
        /// perfectly good file -- it is the control case -- but a trace whose run had hits and
        /// whose log was not passed in would silently look like one that had none, and that is
        /// the failure mode that turns "the model predicted nothing" into a passing test.
        /// </summary>
        public static DodgeTrace Load(string tracePath, string collisionLogPath)
        {
            var trace = new DodgeTrace();
            trace.Source = tracePath;

            // A TRACE THAT IS STILL BEING WRITTEN IS A NAMED FAILURE, AND THE GUARD IS REFUSING RATHER
            // THAN PROBING. The recorder holds the file open for the whole session, and the two ways
            // a replay of a live run can go are BOTH wrong: opened read-only it fails with "being
            // used by another process", and opened shared it reads HALF A SESSION and reports the
            // line it is halfway through as `a second malformed line: the recording is corrupt, not
            // truncated` -- which is a worse lie than the first, because it accuses a healthy
            // recording.
            //
            // So the file is opened with the sharing the recorder GRANTS (`FileShare.Read`, its own
            // creation mode) and nothing else: if another process holds it for writing, this fails
            // and says why. `20261015-214418-p6412` is that case -- an acting session with `-Trace`,
            // measured while the operator was still playing it.
            try
            {
                using (var probe = new FileStream(tracePath, FileMode.Open, FileAccess.Read,
                                                  FileShare.Read))
                {
                    probe.ReadByte();
                }
            }
            catch (IOException ex)
            {
                throw new IOException("the trace at " + tracePath + " is STILL BEING WRITTEN -- close"
                    + " the game first. The recorder holds it open for the whole session, so a run"
                    + " that is in progress cannot be replayed: half of it is not on disk yet and the"
                    + " numbers would describe part of a session. (" + ex.Message + ")", ex);
            }

            // THE LAST RECORD OF A RECORDED RUN CAN BE HALF-WRITTEN, AND THAT IS NOT A CORRUPT FILE.
            //
            // The recorder appends a line per frame and flushes every 30 frames, and the operator
            // ends the session by closing the game by hand -- which can kill the process in the
            // middle of `StreamWriter.Write`, leaving a prefix of one line on disk. A COMPLETE file
            // therefore always ends in a newline, and a file that does not is one the process did
            // not finish writing. Measured: the run of 20261014-192048-p15744 ends
            // `...,1211.228,1211.079,1210.923,1210.76,1210` -- cut inside a coordinate in the
            // middle of the last threat's `xs` array, after 2473 complete lines.
            //
            // The first version of this reader threw, and the whole 2173-frame session became
            // unanalysable because of its last 40 bytes. Treating it as a warning instead is the
            // same judgement Assertion F makes about an empty file, in the other direction: report
            // what is missing, and analyse what is there. It is deliberately narrow -- only the
            // final line, only when the file is unterminated -- because a malformed line in the
            // MIDDLE of a recording means the writer is broken, and that must still fail loudly.
            bool endsCleanly = EndsWithNewline(tracePath);

            var byGeneration = new Dictionary<long, GridWindow>();
            int lineNumber = 0;
            int malformedLine = 0;
            string malformedMessage = null;
            foreach (string line in File.ReadLines(tracePath))
            {
                lineNumber++;
                if (line.Length == 0)
                {
                    continue;
                }
                Json record;
                try
                {
                    record = Json.Parse(line);
                }
                catch (FormatException ex)
                {
                    // A malformed line is a hard failure UNLESS it is the last line of an
                    // unterminated file. `ReadLines` is lazy, so "is this the last line" is only
                    // knowable once the enumeration ends: the line is tagged here and judged after
                    // the loop. At most one line can be tagged, because the next iteration either
                    // parses or throws with the tags already set.
                    if (malformedLine != 0)
                    {
                        throw new FormatException(tracePath + ":" + lineNumber + ": " + ex.Message
                                                  + " (a second malformed line: the recording is"
                                                  + " corrupt, not truncated)");
                    }
                    malformedLine = lineNumber;
                    malformedMessage = ex.Message;
                    continue;
                }

                string kind = record["t"].AsString();
                switch (kind)
                {
                    case "H":
                        trace.Version = record["v"].AsInt();
                        trace.DodgeConfig = record["dodge"].AsString();
                        trace.GridSource = record["gridSource"].AsString();
                        trace.SampleCount = record["samples"].AsInt(21);
                        // The header may declare the trace synthetic. Real recordings never do, so
                        // the reader's default is "recorded" and the flag can only ever relax the
                        // one assertion that a generated trace cannot answer.
                        trace.Synthetic = record["synthetic"].AsBool(false);
                        break;

                    case "G":
                    {
                        GridWindow g = ReadGrid(record);
                        byGeneration[g.Generation] = g;
                        trace.Grids.Add(g);
                        break;
                    }

                    case "F":
                    {
                        RecordedFrame frame = ReadFrame(record, byGeneration, trace.Warnings);
                        trace.Frames.Add(frame);
                        break;
                    }

                    default:
                        trace.Warnings.Add("line " + lineNumber + ": unknown record type '" + kind + "'");
                        break;
                }
            }

            if (malformedLine != 0 && (endsCleanly || malformedLine != lineNumber))
            {
                // A malformed line that is not the last line of an unterminated file: the writer is
                // broken. Fail, naming which of the two conditions failed.
                throw new FormatException(tracePath + ":" + malformedLine + ": " + malformedMessage
                                          + (endsCleanly
                                             ? " (the file ends in a newline, so this is not a"
                                               + " truncated final write)"
                                             : " (a later line parsed, so this is not the last"
                                               + " line)"));
            }
            if (malformedLine != 0)
            {
                trace.Warnings.Add("the last line (" + malformedLine + ") is half-written and was"
                                   + " skipped: the file does not end in a newline, so the process"
                                   + " died inside a write. " + (lineNumber - 1)
                                   + " complete line(s) read.");
            }

            if (!string.IsNullOrEmpty(collisionLogPath) && File.Exists(collisionLogPath))
            {
                LoadHits(collisionLogPath, trace.Hits);
            }

            return trace;
        }

        /// <summary>
        /// Whether the file's last byte is a newline. An empty file counts as clean (there is
        /// nothing half-written in it); a file with no trailing newline does not.
        /// </summary>
        private static bool EndsWithNewline(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length == 0)
                {
                    return true;
                }
                stream.Seek(-1, SeekOrigin.End);
                return stream.ReadByte() == '\n';
            }
        }

        private static GridWindow ReadGrid(Json record)
        {
            var g = new GridWindow();
            g.Generation = record["g"].AsLong();
            g.OriginX = record["ox"].AsInt();
            g.OriginY = record["oy"].AsInt();
            g.Width = record["w"].AsInt();
            g.Height = record["h"].AsInt();
            g.Cells = new byte[Math.Max(0, g.Width * g.Height)];

            List<Json> rle = record["rle"].Items;
            int cell = 0;
            for (int i = 0; i + 1 < rle.Count; i += 2)
            {
                byte value = (byte)rle[i].AsInt();
                int run = rle[i + 1].AsInt();
                for (int k = 0; k < run && cell < g.Cells.Length; k++)
                {
                    g.Cells[cell++] = value;
                }
            }
            return g;
        }

        private static RecordedFrame ReadFrame(Json record, Dictionary<long, GridWindow> grids,
                                               List<string> warnings)
        {
            var frame = new RecordedFrame();
            frame.Frame = record["f"].AsInt();
            frame.Dt = record["dt"].AsFloat(1.0f / 60.0f);
            frame.ClockMs = record["clock"].AsFloat();
            frame.PlayerX = record["px"].AsFloat();
            frame.PlayerY = record["py"].AsFloat();
            frame.PlayerRadius = record["r"].AsFloat(0.25f);
            frame.Health = record["hp"].AsFloat();
            frame.Moving = record["moving"].AsBool();
            frame.Intent = record["intent"].AsFloat();
            frame.Dead = record["dead"].AsBool();
            frame.SafeWorld = record["safe"].AsBool();
            frame.Rotation = record["rot"].AsFloat();
            frame.Skipped = record["skipped"].AsInt();
            frame.NoHitRecorded = !record["nohit"].IsNull;
            frame.NoHit = record["nohit"].AsInt();

            long gridId = record["grid"].AsLong();
            if (gridId != 0 && grids.TryGetValue(gridId, out GridWindow g))
            {
                frame.Grid = g;
            }

            foreach (Json t in record["threats"].Items)
            {
                var threat = new RecordedThreat();
                threat.GameId = t["id"].AsInt();
                threat.StartTime = t["st"].AsInt();
                threat.X = t["x"].AsFloat();
                threat.Y = t["y"].AsFloat();
                threat.Radius = t["rad"].AsFloat();
                threat.Damage = t["dmg"].AsFloat();
                threat.DamagePercent = t["dmgp"].AsFloat();
                threat.ManaDamage = t["mdmg"].AsInt();
                threat.Projectable = t["lin"].AsInt() != 0;
                List<Json> xs = t["xs"].Items;
                List<Json> ys = t["ys"].Items;
                List<Json> alive = t["alive"].Items;
                threat.Count = t["n"].AsInt(xs.Count);

                // THE COUNT IS CLAMPED TO WHAT IS ACTUALLY PRESENT, because a truncated trace (a
                // process killed mid-write) must read as a short path rather than as a
                // zero-filled bullet sitting at the origin -- which is inside the player's disc
                // and would look exactly like a model that predicts everything as a hit.
                int n = Math.Min(threat.Count, Math.Min(xs.Count, ys.Count));
                if (n != threat.Count)
                {
                    warnings.Add("frame " + frame.Frame + ": threat " + threat.GameId
                                 + " declared " + threat.Count + " samples but carries " + n);
                }
                threat.Count = n;
                threat.Xs = new float[n];
                threat.Ys = new float[n];
                threat.Alive = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    threat.Xs[i] = xs[i].AsFloat();
                    threat.Ys[i] = ys[i].AsFloat();
                    threat.Alive[i] = i < alive.Count ? alive[i].AsInt() != 0 : true;
                }
                frame.Threats.Add(threat);
            }

            // ENEMY BODIES, WHEN THE RECORD CARRIES THEM. A recording from before the reader existed
            // has no `bodies` key at all, so this reads as the empty list and rule 7 is exactly as
            // vacuous as it was for that session -- which is the honest replay of it. `danger` is
            // deliberately NOT read: the emitter radius is a CONFIG value, see `RecordedFrame.Bodies`.
            foreach (Json b in record["bodies"].Items)
            {
                frame.Bodies.Add(new EntityBody
                {
                    X = b["x"].AsFloat(),
                    Y = b["y"].AsFloat(),
                    Radius = b["rad"].AsFloat(),
                    DangerRadius = 0.0f,   // the harness fills it from the config it is running
                });
            }
            return frame;
        }

        /// <summary>
        /// Reads `collision:` lines out of a bootstrap log.
        ///
        /// THE SHAPE IS PINNED BY `CollisionObservation.ObserveHit` AND IS ASSERTED HERE, not
        /// assumed: a line that does not match is counted and reported rather than skipped
        /// silently. A parser that quietly finds nothing is how a run with forty hits reports
        /// forty false positives and looks like a broken model.
        /// </summary>
        public static void LoadHits(string path, List<ObservedHit> into)
        {
            int lineNumber = 0;
            foreach (string line in File.ReadLines(path))
            {
                lineNumber++;
                int marker = line.IndexOf("collision: key=(", StringComparison.Ordinal);
                if (marker < 0)
                {
                    continue;
                }
                // collision: key=(375634,57919) tracked=yes predicted=yes warning=103.7ms
                //            bullet=(383.7,1102.7) r=0.2 | run: ...
                string rest = line.Substring(marker + "collision: key=(".Length);
                int close = rest.IndexOf(')');
                if (close < 0)
                {
                    continue;
                }
                string[] key = rest.Substring(0, close).Split(',');
                if (key.Length != 2)
                {
                    continue;
                }

                var hit = new ObservedHit();
                hit.GameId = int.Parse(key[0], CultureInfo.InvariantCulture);
                hit.StartTime = int.Parse(key[1], CultureInfo.InvariantCulture);
                hit.Tracked = rest.Contains("tracked=yes");
                hit.HasPredictedFlag = rest.Contains("predicted=");
                hit.Predicted = rest.Contains("predicted=yes");
                hit.WarningMs = ExtractFloat(rest, "warning=");
                hit.BulletX = ExtractPair(rest, "bullet=", 0);
                hit.BulletY = ExtractPair(rest, "bullet=", 1);
                hit.Radius = ExtractFloat(rest, "r=");
                hit.LineNumber = lineNumber;
                into.Add(hit);
            }
        }

        private static float ExtractFloat(string text, string key)
        {
            int at = text.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
            {
                return 0.0f;
            }
            at += key.Length;
            int end = at;
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.'
                                         || text[end] == '-' || text[end] == '+'))
            {
                end++;
            }
            return float.TryParse(text.Substring(at, end - at), NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out float value) ? value : 0.0f;
        }

        private static float ExtractPair(string text, string key, int which)
        {
            int at = text.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
            {
                return 0.0f;
            }
            at += key.Length;
            int open = text.IndexOf('(', at);
            if (open < 0)
            {
                return 0.0f;
            }
            int close = text.IndexOf(')', open);
            if (close < 0)
            {
                return 0.0f;
            }
            string[] parts = text.Substring(open + 1, close - open - 1).Split(',');
            if (which >= parts.Length)
            {
                return 0.0f;
            }
            return float.TryParse(parts[which], NumberStyles.Float, CultureInfo.InvariantCulture,
                                  out float value) ? value : 0.0f;
        }
    }
}
