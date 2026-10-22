// CommandLine.cs -- the command line, as one of the four configuration sources.
//
// WHAT CHANGED WHEN THE CONFIG FILE ARRIVED (HANDOFF-LAUNCH-CONFIG.md section 4.1)
// ------------------------------------------------------------------------------
// This used to fill `Options` directly, defaults and all.  It now fills a `Settings`, in
// which every field is nullable: `--autoaim closest` records "the command line has an
// opinion about AutoAim", not "AutoAim is closest, and nothing else may touch GamePath".
// `Options.Apply` overlays those opinions in precedence order, after the configuration
// file has had its turn.
//
// THE IMPLICATIONS MOVED OUT OF HERE (section 10.6).  `--autoaim` used to set
// `options.FrameReplacement` inside the parser, which is the one place a configuration
// file cannot reach; `Options.ApplyImplications` now does it once, for both sources.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Drelay.Launcher
{
    internal static class CommandLine
    {
        /// <summary>
        /// The command line as a `Settings`.
        ///
        /// The out-parameters are the two things a `Settings` cannot carry: whether `--help`
        /// was asked for at all (which is not a setting), and the arguments that are not
        /// switches and belong to the game.
        /// </summary>
        public static bool TryParse(string[] args, out Settings settings,
                                    out List<string> gameArguments, out bool help, out string error)
        {
            settings = new Settings();
            gameArguments = new List<string>();
            help = false;
            error = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--game": settings.GamePath = Next(args, ref i); break;
                    case "--exe": settings.ExecutablePath = Next(args, ref i); break;
                    case "--assembly": settings.AssemblyPath = Next(args, ref i); break;
                    case "--hook": settings.HookDll = Next(args, ref i); break;
                    case "--bootstrap": settings.BootstrapDll = Next(args, ref i); break;
                    case "--runroot": settings.RunRoot = Next(args, ref i); break;
                    case "--timeout":
                        if (!ParseMilliseconds(args, ref i, "--timeout", out int timeout, out error)) { return false; }
                        settings.TimeoutMs = timeout;
                        break;
                    case "--collect-delay":
                        if (!ParseMilliseconds(args, ref i, "--collect-delay", out int delay, out error)) { return false; }
                        settings.CollectDelayMs = delay;
                        break;
                    case "--discovery-ms":
                        if (!ParseMilliseconds(args, ref i, "--discovery-ms", out int window, out error)) { return false; }
                        settings.DiscoveryWindowMs = window;
                        break;
                    case "--resolve-delay":
                        if (!ParseMilliseconds(args, ref i, "--resolve-delay", out int resolve, out error)) { return false; }
                        settings.ResolveDelayMs = resolve;
                        break;
                    case "--live-seconds":
                        if (!ParseMilliseconds(args, ref i, "--live-seconds", out int live, out error)) { return false; }
                        settings.LiveSeconds = live;
                        break;
                    case "--cores":
                    {
                        string raw = Next(args, ref i);
                        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                          out int cores) || cores <= 0)
                        {
                            error = "--cores needs a positive number of processors, got '" + raw + "'";
                            return false;
                        }
                        settings.Cores = cores;
                        break;
                    }
                    case "--off": settings.Off = true; break;
                    case "--act": settings.Act = true; break;
                    case "--full-trace": settings.FullTrace = true; break;
                    case "--verbose": settings.Verbose = true; break;
                    case "--no-wait": settings.NoWait = true; break;
                    case "--pause-after-run": settings.PauseAfterRun = true; break;
                    case "--dbg-dump": settings.DbgDump = true; break;
                    case "--test-target": settings.TestTargets = Next(args, ref i); break;
                    case "--frame-helper": settings.FrameHelper = true; break;
                    case "--frame-replacement": settings.FrameReplacement = true; break;
                    case "--move-steer": settings.MoveSteer = Next(args, ref i); break;
                    case "--trace": settings.Trace = true; break;
                    case "--world-log":
                        // ONLY WHAT WAS TYPED. The frame replacement, the frame sensor and the
                        // `DRELAY_WORLDLOG` name are the flag's three dependencies, and all three
                        // are applied in `Options.ApplyImplications`/`EnvironmentBlock` -- the one
                        // place a configuration file would go through too, if it could ask for this.
                        settings.WorldLog = true;
                        break;
                    case "--world-log-every":
                    {
                        string raw = Next(args, ref i);
                        if (!ParseFrameCadence("--world-log-every", raw, out int every, ref error))
                        {
                            return false;
                        }
                        settings.WorldLogEvery = every;
                        break;
                    }
                    case "--dodge": settings.Dodge = true; break;
                    case "--dodge-act":
                        // ONLY WHAT WAS TYPED.  `--dodge-act` implies the dodge, the movement
                        // replacement and the pilot's `dodge` mode, and all three are applied in
                        // `Options.ApplyImplications` -- the one place the config file's
                        // `Dodge.Act` goes through too (section 10.6).
                        settings.DodgeAct = true;
                        break;
                    case "--autofarm":
                        // SAME RULE, ONE MORE IMPLICATION: `--autofarm` is an acting request, and
                        // what it implies (the dodge, its live actuator and a default aim mode) is
                        // applied in `Options.ApplyImplications` so the config file's
                        // `Farm.Enabled` produces exactly the same run. See `Options.AutoFarm`.
                        settings.AutoFarm = true;
                        break;
                    case "--autoaim":
                    {
                        string raw = Next(args, ref i);
                        if (!Options.TryParseAutoAim(raw, out string autoAim, out string why))
                        {
                            error = "--autoaim " + why;
                            return false;
                        }
                        settings.AutoAim = autoAim;
                        break;
                    }
                    case "--dodge-disc":
                    {
                        string raw = Next(args, ref i);
                        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture,
                                             out double disc) || disc < 0.0)
                        {
                            error = "--dodge-disc needs a non-negative multiple of the projectile's"
                                    + " collision-disc crossing time, got '" + raw + "'";
                            return false;
                        }
                        settings.DodgeDisc = disc;
                        break;
                    }
                    case "--dodge-window":
                    {
                        string raw = Next(args, ref i);
                        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture,
                                             out double floor) || floor < 0.0)
                        {
                            error = "--dodge-window needs a non-negative number of seconds, got '" + raw + "'";
                            return false;
                        }
                        settings.DodgeWindowSeconds = floor;
                        break;
                    }
                    case "--escape-test":
                    {
                        string raw = Next(args, ref i);
                        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture,
                                             out double seconds) || seconds < 0.0)
                        {
                            error = "--escape-test needs a non-negative number of seconds, got '" + raw + "'";
                            return false;
                        }
                        settings.EscapeTestSeconds = seconds;
                        break;
                    }
                    case "--help":
                    case "-h":
                        help = true;
                        return true;
                    default:
                    {
                        if (!a.StartsWith("--", StringComparison.Ordinal))
                        {
                            // Anything not starting with -- is passed to the game.
                            gameArguments.Add(a);
                            break;
                        }
                        if (!TryParseEqualsForm(a, settings, ref error)) { return false; }
                        if (!IsKnownEqualsForm(a))
                        {
                            error = "unknown option: " + a;
                            return false;
                        }
                        break;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// The `--flag=value` spelling, which is the one a script is most likely to build.
        ///
        /// It returns false only for a malformed value, and reports "not an equals form at all"
        /// through <paramref name="error"/> being left null -- the caller's two other cases are
        /// a game argument and an unknown option, and only the caller knows which of those a
        /// bare word is.
        /// </summary>
        private static bool TryParseEqualsForm(string a, Settings settings, ref string error)
        {
            int eq = a.IndexOf('=');
            if (!a.StartsWith("--", StringComparison.Ordinal) || eq < 0)
            {
                return true;
            }
            string name = a.Substring(0, eq);
            string value = a.Substring(eq + 1);
            switch (name)
            {
                case "--game": settings.GamePath = value; return true;
                case "--exe": settings.ExecutablePath = value; return true;
                case "--assembly": settings.AssemblyPath = value; return true;
                case "--hook": settings.HookDll = value; return true;
                case "--bootstrap": settings.BootstrapDll = value; return true;
                case "--runroot": settings.RunRoot = value; return true;
                case "--move-steer": settings.MoveSteer = value; return true;
                case "--test-target": settings.TestTargets = value; return true;
                case "--timeout": return ParseMilliseconds(name, value, out settings.TimeoutMs, ref error);
                case "--collect-delay": return ParseMilliseconds(name, value, out settings.CollectDelayMs, ref error);
                case "--discovery-ms": return ParseMilliseconds(name, value, out settings.DiscoveryWindowMs, ref error);
                case "--resolve-delay": return ParseMilliseconds(name, value, out settings.ResolveDelayMs, ref error);
                case "--live-seconds": return ParseMilliseconds(name, value, out settings.LiveSeconds, ref error);
                case "--cores":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                      out int cores) || cores <= 0)
                    {
                        error = "--cores needs a positive number of processors, got '" + value + "'";
                        return false;
                    }
                    settings.Cores = cores;
                    return true;
                case "--autoaim":
                    if (!Options.TryParseAutoAim(value, out string autoAim, out string why))
                    {
                        error = "--autoaim " + why;
                        return false;
                    }
                    settings.AutoAim = autoAim;
                    return true;
                case "--escape-test":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                                         out double seconds) || seconds < 0.0)
                    {
                        error = "--escape-test needs a non-negative number of seconds, got '" + value + "'";
                        return false;
                    }
                    settings.EscapeTestSeconds = seconds;
                    return true;
                case "--dodge-disc":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                                         out double disc) || disc < 0.0)
                    {
                        error = "--dodge-disc needs a non-negative multiple of the projectile's"
                                + " collision-disc crossing time, got '" + value + "'";
                        return false;
                    }
                    settings.DodgeDisc = disc;
                    return true;
                case "--dodge-window":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                                         out double floor) || floor < 0.0)
                    {
                        error = "--dodge-window needs a non-negative number of seconds, got '" + value + "'";
                        return false;
                    }
                    settings.DodgeWindowSeconds = floor;
                    return true;
                case "--world-log-every":
                    if (!ParseFrameCadence(name, value, out int every, ref error)) { return false; }
                    settings.WorldLogEvery = every;
                    return true;
                default:
                    return true;
            }
        }

        private static bool IsKnownEqualsForm(string a)
        {
            int eq = a.IndexOf('=');
            string name = eq < 0 ? a : a.Substring(0, eq);
            switch (name)
            {
                case "--game":
                case "--exe":
                case "--assembly":
                case "--hook":
                case "--bootstrap":
                case "--runroot":
                case "--move-steer":
                case "--test-target":
                case "--timeout":
                case "--collect-delay":
                case "--discovery-ms":
                case "--resolve-delay":
                case "--live-seconds":
                case "--autoaim":
                case "--escape-test":
                case "--dodge-disc":
                case "--dodge-window":
                case "--world-log-every":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A millisecond knob, refused with its own name rather than by an exception.
        ///
        /// THE OLD PARSER USED `int.Parse` HERE, so `--timeout abc` threw out of `Main` -- outside
        /// the try/catch that was supposed to turn a bad launch into a message -- and the operator
        /// got a stack trace for a typo.  Every one of these is a number a user can get wrong.
        /// </summary>
        private static bool ParseMilliseconds(string[] args, ref int i, string flag,
                                              out int value, out string error)
        {
            error = null;
            return ParseMilliseconds(flag, Next(args, ref i), out value, ref error);
        }

        private static bool ParseMilliseconds(string flag, string raw, out int value, ref string error)
        {
            value = 0;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                || parsed < 0)
            {
                error = flag + " needs a non-negative number, got '" + raw + "'";
                return false;
            }
            value = parsed;
            return true;
        }

        private static bool ParseMilliseconds(string flag, string raw, out int? value, ref string error)
        {
            bool ok = ParseMilliseconds(flag, raw, out int parsed, ref error);
            value = ok ? parsed : (int?)null;
            return ok;
        }

        /// <summary>
        /// A frame cadence: a whole number of frames, and at least one.
        ///
        /// ZERO IS REFUSED RATHER THAN READ AS "THE DEFAULT", because `WorldLog.cs` reads a
        /// non-positive `DRELAY_WORLDLOG_EVERY` as "not set" and falls back to 30. An operator who
        /// typed `--world-log-every 0` asked for a cadence; silently writing 30 while the command
        /// line says 0 is the kind of disagreement this parser refuses by name everywhere else.
        /// </summary>
        private static bool ParseFrameCadence(string flag, string raw, out int value, ref string error)
        {
            value = 0;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                || parsed < 1)
            {
                error = flag + " needs a positive number of frames, got '" + raw + "'";
                return false;
            }
            value = parsed;
            return true;
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException("option " + args[i] + " needs a value");
            }
            return args[++i];
        }
    }
}
