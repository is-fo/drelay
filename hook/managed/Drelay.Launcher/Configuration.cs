// Configuration.cs -- the one place the four sources are merged.
//
// HANDOFF-LAUNCH-CONFIG.md section 4:
//
//   1. command line             --game, --autoaim, ...        (highest)
//   2. config file              drelay.config.json
//   3. auto-discovery           Steam / recordedFrom / cwd
//   4. compiled defaults        the numbers in Options        (lowest)
//
// FIELD BY FIELD, NOT SOURCE BY SOURCE (section 4.1).  `--autoaim highesthp` on the command
// line must not discard a `GamePath` that only the config file knows, and a config file that
// sets two fields must not discard the compiled defaults for the rest.
//
// WHY THIS IS ITS OWN FILE.  The merge is the single most error-prone part of the feature --
// its failure mode is a setting that silently does not apply -- and section 13 C1 is a named
// invariant about it.  Keeping it out of `Program.Main` is what lets the offline harness run
// the *shipped* merge rather than a copy of it, with a fake discovery environment standing in
// for the registry.

using System.Collections.Generic;

namespace Drelay.Launcher
{
    internal enum ResolveOutcome
    {
        /// <summary>Every source was merged and every path resolved; `Options` is usable.</summary>
        Ready,

        /// <summary>No source named the game and discovery did not find it. The caller reports.</summary>
        NoGamePath,

        /// <summary>A source supplied a contradictory or unusable value; `Error` says which.</summary>
        Invalid,
    }

    internal sealed class ResolveResult
    {
        public ResolveOutcome Outcome;
        public Options Options;
        public DiscoveryResult Discovery;
        public string Error;

        /// <summary>
        /// Things the merge decided that the operator did not literally ask for and would not
        /// otherwise see.  Reported by the caller rather than printed here, because the offline
        /// harness runs this same function and a resolve that writes to the console cannot be
        /// checked.
        /// </summary>
        public List<string> Notices = new List<string>();
    }

    internal static class Configuration
    {
        public static ResolveResult Resolve(
            Settings config, Settings cli, List<string> gameArguments,
            IDiscoveryEnv discoveryEnv, PinnedBuild pinned, string launcherDirectory)
        {
            var result = new ResolveResult();

            var options = new Options();

            // 1. the compiled defaults are `Options`' own field initialisers, so the config
            //    file is the first overlay and a field it does not mention is untouched.
            options.Apply(config);

            //    THE FILE'S ACTING REQUEST IS REMEMBERED BEFORE THE COMMAND LINE IS APPLIED,
            //    because the command line can replace the *actuator* without touching the field
            //    that asked for it.  See the note at the resolution below.
            bool actingRequestedByFile = options.DodgeAct;

            // 2. the command line, which wins over the config file for every field it sets.
            options.Apply(cli);

            options.GameArguments = gameArguments ?? new List<string>();

            // 2a. THE COMMAND LINE'S ACTUATOR CHOICE BEATS THE FILE'S, AND ONLY WHEN IT MADE ONE.
            //
            // `Dodge.Act` and `--move-steer` both say "this is what steers the player", so they
            // contradict each other, and per-field precedence cannot express that on its own: the
            // two live in different fields, so the file's `Act` would survive a command line that
            // asked for a different pilot.  Two spells of the same contradiction therefore resolve
            // differently, and the difference is who is doing the deciding:
            //
            //   * `--dodge-act --move-steer X`, both typed by hand, is a mistake and is REFUSED
            //     (`Options.Validate`) -- a run that quietly moved the player the way one of two
            //     contradictory flags asked is the surprise `--move-steer`'s design exists to
            //     prevent;
            //   * `"Act": true` in the file with `--move-steer X` on the command line is a RUN,
            //     because the command line is the more specific statement: it wins, `Act` is
            //     cleared for this run, and the notice below says so.  Without this, the release
            //     default itself would break `Run-Phase3Session.ps1 -Experiment Sweep/Hold`, which
            //     passes `--move-steer` and cannot turn a config field off.
            //
            // The file's `Dodge.Enabled` is NOT cleared: solving and logging do not contradict
            // "somebody else steers", and a field the operator set is not discarded silently.
            if (actingRequestedByFile && !cli.DodgeAct.HasValue && !string.IsNullOrEmpty(cli.MoveSteer))
            {
                options.DodgeAct = false;
                result.Notices.Add(
                    "--move-steer '" + cli.MoveSteer + "' chose the actuator, so \"Dodge\": "
                    + "{\"Act\": true} from the configuration file is off for this run. The solver "
                    + "still runs and logs; set \"Dodge\": {\"Enabled\": false} as well to stop it.");
            }

            // 3. auto-discovery, and ONLY for a GamePath no other source supplied.
            //
            //    Section 4.1's diagram draws discovery between the config file and the command
            //    line, which would make it beat the config file; section 4's table says the
            //    config file beats auto-discovery, and section 6 says discovery runs "if GamePath
            //    is still unknown".  Those agree on one behaviour and this is it: discovery may
            //    fill a GamePath, never overwrite one.  The order of the two overlays is therefore
            //    unobservable, and the command line is applied first so that a `--game` never even
            //    pays for the probe.
            if (string.IsNullOrEmpty(options.GamePath))
            {
                result.Discovery = Discovery.Find(discoveryEnv, pinned);
                if (result.Discovery.Found)
                {
                    options.GamePath = result.Discovery.GameDirectory;
                    options.DiscoveredFrom = result.Discovery.GameDirectory;
                }
            }

            if (string.IsNullOrEmpty(options.GamePath))
            {
                result.Outcome = ResolveOutcome.NoGamePath;
                result.Options = options;
                return result;
            }

            // 4. the contradictions, checked BEFORE the implications, because the implications are
            //    what make `--dodge-act` and `--move-steer` agree and a check that ran after them
            //    could never fire.
            if (!options.Validate(out string error))
            {
                result.Outcome = ResolveOutcome.Invalid;
                result.Error = error;
                result.Options = options;
                return result;
            }

            // 5. the flags that imply other flags, once, for both sources (section 10.6).
            options.ApplyImplications();

            // 6. absolute paths, and the three that default to "beside the launcher".
            options.ResolvePaths(launcherDirectory, out error);
            if (error != null)
            {
                result.Outcome = ResolveOutcome.Invalid;
                result.Error = error;
                result.Options = options;
                return result;
            }

            result.Outcome = ResolveOutcome.Ready;
            result.Options = options;
            return result;
        }
    }
}
