// EnvironmentBlock.cs -- the controlled environment the game is started with.
//
// THE CHILD'S ENVIRONMENT IS BUILT, NOT INHERITED, and this file is the whole of that
// decision.  Every `DOTNET_*` and every `DRELAY_HOOK_*` variable the operator has in their own
// shell is deleted before the launcher adds its own, because the hazard is the namespace rather
// than one known variable -- `DOTNET_TieredCompilation` is only the member of it that is known
// to break this design today (HOOK-ARCHITECTURE.md section 1, PHASE1-HANDOFF.md section 4
// trap 1).
//
// IT IS ALSO WHERE THE CONFIG FILE BECOMES REAL, and where the config file's *absence* has to
// mean something.  HANDOFF-LAUNCH-CONFIG.md section 13 C2: "an unset field is absent from the
// child's environment, not set to the default".  A field the user never mentioned is a field
// the hook should decide, and the hook's own defaults are the same numbers the launcher would
// have written (`DrelayInstall.cpp`: kDefaultCollectDelayMs 0, kDefaultDiscoveryWindowMs 10000,
// kDefaultResolveDelayMs 100; `DrelayMain.cpp`: 60000 ms) -- so writing only what was supplied
// costs nothing and keeps one authority for each number.
//
// THE NAMES THAT ARE ALWAYS WRITTEN ARE THE LAUNCHER'S OWN, and each one is always written
// because its absence is not a neutral state: the session id, the run directory, the game
// directory, the block file, and `DRELAY_HOOK_DRYRUN` -- for which an *absent* variable means
// "act" (`StartupHook.cs`: `dryRun = GetEnvironmentVariable(EnvDryRun) == "1"`), which is the
// opposite of the safe default.
//
// `inherited` IS A PARAMETER rather than a call to `Environment.GetEnvironmentVariables()` so
// that the whole block is a pure function of its inputs, which is what makes C2 a check rather
// than an inspection.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Drelay.Launcher
{
    internal static class EnvironmentBlock
    {
        public static List<KeyValuePair<string, string>> Build(
            Options options, string gameDir, string bootstrap, uint session, string runDir,
            IEnumerable<KeyValuePair<string, string>> inherited, Action<string> report)
        {
            var variables = new List<KeyValuePair<string, string>>();
            var removed = new List<string>();

            foreach (var entry in inherited)
            {
                string key = entry.Key;
                if (key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("DRELAY_HOOK_", StringComparison.OrdinalIgnoreCase))
                {
                    removed.Add(key);
                    continue;
                }
                // A NAME THIS PROCESS IS ABOUT TO SET ITSELF IS DROPPED RATHER THAN
                // INHERITED, so the environment block never carries the same variable twice.
                // `DRELAY_KEEP_READING` is outside the `DRELAY_HOOK_` namespace (it has to
                // be: the operator may set it in their own shell), so the prefix filter
                // above does not catch it, and `--pause-after-run` writes `0`.  Two entries
                // for one name is a block whose meaning depends on how the child's runtime
                // happens to resolve duplicates -- which is not a contract to rely on.
                if (options.PauseAfterRun &&
                    key.Equals("DRELAY_KEEP_READING", StringComparison.OrdinalIgnoreCase))
                {
                    removed.Add(key);
                    continue;
                }
                variables.Add(new KeyValuePair<string, string>(key, entry.Value));
            }

            // The bootstrap is the only DOTNET_* variable the design needs, and it
            // is what makes the managed half run before the game's Main.
            variables.Add(new KeyValuePair<string, string>(
                "DOTNET_STARTUP_HOOKS", bootstrap));

            // THE ONE CASE WHERE A `DOTNET_*` VARIABLE IS ADDED BACK DELIBERATELY.
            //
            // The filter above exists so that a stray `DOTNET_TieredCompilation` in the
            // operator's shell cannot silently change the JIT's behaviour underneath the
            // hook.  Crash-dump configuration is the opposite kind of thing: it cannot
            // change how the process runs, it only decides whether a death leaves a body
            // behind, and the launcher is the only place it can be set -- the filter is
            // exactly what stops the operator from setting it themselves.
            if (options.DbgDump)
            {
                variables.Add(new KeyValuePair<string, string>("DOTNET_DbgEnableMiniDump", "1"));
                // 2 = with heap.  A mini dump without the heap has the stacks, which is
                // usually enough; the heap is what makes `clrstack`/SOS usable, and this
                // is the case where the alternative is no evidence at all.
                variables.Add(new KeyValuePair<string, string>("DOTNET_DbgMiniDumpType", "2"));
                variables.Add(new KeyValuePair<string, string>(
                    "DOTNET_DbgMiniDumpName", System.IO.Path.Combine(runDir, "crashdump.dmp")));
                variables.Add(new KeyValuePair<string, string>("DOTNET_CreateDumpDiagnostics", "1"));
            }

            // THE LAUNCHER'S OWN NAMES, ALWAYS WRITTEN.  See the file header: each of these is a
            // fact about *this* run that no outside source may hold an opinion about.
            variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_SESSION", session.ToString(CultureInfo.InvariantCulture)));
            variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_RUNDIR", runDir));
            variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_GAMEDIR", gameDir));
            if (!string.IsNullOrEmpty(options.AssemblyPath))
            {
                // Named explicitly so the bootstrap does not have to know what the
                // game's assembly is called. Several files derive that name from
                // the pinned build record instead of hard-coding it, and this is
                // the value that lets the same hook be pointed at a test host.
                variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_ASSEMBLY", options.AssemblyPath));
            }
            // The file backing the shared block. Passed as a path rather than as a
            // kernel object name so that the block has no commit charge at creation
            // and so that a crashed run leaves its block on disk.
            variables.Add(new KeyValuePair<string, string>(
                "DRELAY_HOOK_BLOCK", System.IO.Path.Combine(runDir, "shared.block")));
            variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_DRYRUN", options.DryRun ? "1" : "0"));

            // The native side's watchdog timeout, overridable so the timeout path
            // itself is testable.  A watchdog nobody has ever seen fire is a
            // watchdog that does not work, and this one has to: it is what keeps a
            // bootstrap that never runs from being a game that never starts.
            //
            // WRITTEN ONLY WHEN THE USER ASKED FOR IT, because the launcher's default and the
            // native default are the same 60 s (`DrelayMain.cpp`: `uint32_t timeoutMs = 60000`).
            if (options.SetTimeoutMs)
            {
                variables.Add(new KeyValuePair<string, string>(
                    "DRELAY_HOOK_TIMEOUT_MS", options.TimeoutMs.ToString(CultureInfo.InvariantCulture)));
            }
            if (options.SetCollectDelayMs)
            {
                variables.Add(new KeyValuePair<string, string>(
                    "DRELAY_HOOK_COLLECT_DELAY_MS",
                    options.CollectDelayMs.ToString(CultureInfo.InvariantCulture)));
            }
            if (options.SetDiscoveryWindowMs)
            {
                variables.Add(new KeyValuePair<string, string>(
                    "DRELAY_HOOK_DISCOVERY_WINDOW_MS",
                    options.DiscoveryWindowMs.ToString(CultureInfo.InvariantCulture)));
            }
            if (options.SetResolveDelayMs)
            {
                variables.Add(new KeyValuePair<string, string>(
                    "DRELAY_HOOK_RESOLVE_DELAY_MS",
                    options.ResolveDelayMs.ToString(CultureInfo.InvariantCulture)));
            }

            // THE ONE SWITCH WHOSE ABSENCE IS THE FEATURE, SO IT IS ONLY EVER SET TO TURN
            // SOMETHING OFF.  `DRELAY_KEEP_READING` is a plain `DRELAY_` name (the hook
            // reads it before the launcher's filter is relevant, but the operator may also
            // set it in their own shell), and the hook's default is now "keep reading"; an
            // unset variable and a set-to-0 one must therefore not be the same thing.  They
            // are not: the launcher writes the variable only when asked, and the hook reads
            // an empty value as "not set".
            if (options.PauseAfterRun)
            {
                variables.Add(new KeyValuePair<string, string>("DRELAY_KEEP_READING", "0"));
            }
            // How long the hook records after the patch.  Clamped here as well as in
            // the hook so that a typo is refused by the launcher's own message rather
            // than silently reduced to the ceiling inside the process.
            if (options.SetLiveSeconds)
            {
                int liveMs = options.LiveSeconds > 6 * 3600 ? 6 * 3600 : options.LiveSeconds;
                if (liveMs < 0)
                {
                    liveMs = 0;
                }
                variables.Add(new KeyValuePair<string, string>(
                    "DRELAY_HOOK_LIVE_MS",
                    (liveMs * 1000).ToString(CultureInfo.InvariantCulture)));
            }
            if (options.FullTrace)
            {
                variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_FULLTRACE", "1"));
            }
            if (!string.IsNullOrEmpty(options.TestTargets))
            {
                // Test-only, and loud about it: the bootstrap logs every test target
                // it appends, so a run that used this switch cannot later be mistaken
                // for a run of the pinned game build.
                variables.Add(new KeyValuePair<string, string>(
                    "DRELAY_HOOK_TEST_TARGETS", options.TestTargets));
            }
            if (options.FrameHelper)
            {
                variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_FRAME_HELPER", "1"));
            }
            if (options.FrameReplacement)
            {
                variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_FRAME_REPLACEMENT", "1"));
            }
            if (options.Trace)
            {
                // THE THREE THAT TOGETHER MAKE A TRACE NON-EMPTY. See `Options.Trace`.
                variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_FRAME_REPLACEMENT", "1"));
                variables.Add(new KeyValuePair<string, string>("DRELAY_FRAME_SENSOR", "1"));
                variables.Add(new KeyValuePair<string, string>("DRELAY_TRACE", "1"));
            }
            if (!string.IsNullOrEmpty(options.MoveSteer))
            {
                // BOTH NAMES, AND THE SECOND ONE IS THE OPERATOR'S OWN.
                //
                // The hook is loaded into a process the launcher starts, so the value
                // could just be left in the inherited environment -- but the launcher
                // deletes every inherited `DRELAY_HOOK_*` name precisely so that a
                // stale shell variable cannot change what gets patched, and whether
                // the player may be moved is exactly that kind of decision.  So the
                // native side is told through the namespace it does not inherit
                // (`DRELAY_HOOK_MOVE_REPLACEMENT`, which the managed replacement
                // publishes for), and the *value* is passed under its own name for the
                // managed side to read.  A value with no name would be a setting that
                // silently does nothing; a name with no value would be a patch with no
                // behaviour.
                variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_MOVE_REPLACEMENT", "1"));
                variables.Add(new KeyValuePair<string, string>("DRELAY_MOVE_STEER", options.MoveSteer));
            }
            if (!string.IsNullOrEmpty(options.AutoAim))
            {
                // THE SAME TWO-NAME SPLIT AS `--move-steer`, AND FOR THE SAME REASON.
                // `DRELAY_HOOK_AIM_REPLACEMENT` lives in the namespace the launcher owns and
                // deletes inherited values from, so a stale shell variable cannot decide that
                // a patched game method jumps into managed code; the *mode* travels under its
                // own name (`DRELAY_AIM_MODE`) for the managed side, which is where the
                // selection lives, to read. A value with no flag would patch nothing; a flag
                // with no value would patch a method that then does nothing.
                //
                // AND IT TURNS THE FRAME REPLACEMENT ON, WHICH IS NOT OPTIONAL.  The module
                // has to arm `Prefs.enable_aimbot` before `AimbotSystem.FindTarget` is ever
                // called -- nothing else compiles it, so nothing else patches it -- and the
                // only place it may make that write is the game's own thread. The frame
                // replacement is the one game-thread entry point that exists on every run.
                // `AutoAim.OnFrame` carries the whole argument, including why the write at
                // resolve time is lost to `GameSettings.Load` in `DarzaWindow`'s constructor.
                variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_FRAME_REPLACEMENT", "1"));
                variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_AIM_REPLACEMENT", "1"));
                variables.Add(new KeyValuePair<string, string>("DRELAY_AIM_MODE", options.AutoAim));
            }
            if (options.Dodge)
            {
                // THE THREE DEPENDENCIES OF A LIVE DECISION, SET TOGETHER. See `Options.Dodge`.
                variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_FRAME_REPLACEMENT", "1"));
                variables.Add(new KeyValuePair<string, string>("DRELAY_FRAME_SENSOR", "1"));
                variables.Add(new KeyValuePair<string, string>("DRELAY_DODGE", "1"));
                // THE DECISION WINDOW, IF THE OPERATOR ASKED FOR ONE. It travels under its own name
                // for the managed side to read, and it is set even in a plain `--dodge` dry run
                // because the window changes which candidates are admissible -- so a dry run that
                // does not carry it is a dry run of a different dodge from the one that will act.
                if (options.DodgeDisc >= 0.0)
                {
                    variables.Add(new KeyValuePair<string, string>(
                        "DRELAY_DODGE_DECISION_DISC",
                        options.DodgeDisc.ToString("0.####", CultureInfo.InvariantCulture)));
                }
                if (options.DodgeWindowSeconds >= 0.0)
                {
                    variables.Add(new KeyValuePair<string, string>(
                        "DRELAY_DODGE_WINDOW_S",
                        options.DodgeWindowSeconds.ToString("0.####", CultureInfo.InvariantCulture)));
                }
            }
            if (options.DodgeAct)
            {
                // AND THE ACTUATOR'S OWN TWO, WHICH ONLY THIS SWITCH SETS.
                //
                // `DRELAY_DODGE_ACT` is the adapter's gate; `DRELAY_HOOK_MOVE_REPLACEMENT` plus
                // `DRELAY_MOVE_STEER=dodge` is the pilot's. The same name-with-a-value split as
                // `--move-steer`, for the same reason: the *flag* that decides whether a patched
                // game's frame enters managed code lives in the namespace the launcher owns and
                // deletes inherited values from, and the *value* travels under its own name.
                variables.Add(new KeyValuePair<string, string>("DRELAY_DODGE_ACT", "1"));
                variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_MOVE_REPLACEMENT", "1"));
                variables.Add(new KeyValuePair<string, string>("DRELAY_MOVE_STEER", "dodge"));
            }
            if (options.EscapeTestSeconds >= 0.0)
            {
                // Same two-name split as `--move-steer`, and for the same reason: the
                // *flag* that decides whether managed code is entered lives in the
                // namespace the launcher owns and deletes inherited values from
                // (`--escape-test` sets `--frame-replacement`, so that name is already
                // set above), and the *parameter* travels under its own name for the
                // managed side to read.
                variables.Add(new KeyValuePair<string, string>(
                    "DRELAY_ESCAPE_TEST_SEC",
                    options.EscapeTestSeconds.ToString("0.###", CultureInfo.InvariantCulture)));
            }
            if (options.Off)
            {
                // With the off switch set, the startup hook is not installed at
                // all: "off" has to mean the process is untouched, and the
                // strongest form of that is not injecting managed code into it.
                variables.RemoveAll(v => v.Key == "DOTNET_STARTUP_HOOKS");
                variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_OFF", "1"));
            }

            if (report != null && options.Verbose)
            {
                report("environment   : removed " + removed.Count + " variable(s):");
                foreach (string key in removed)
                {
                    report("                  " + key);
                }
            }

            // A Unicode environment block is a NUL-separated, double-NUL-terminated
            // sequence of `name=value` strings, sorted case-insensitively --
            // which is what CREATE_UNICODE_ENVIRONMENT requires.
            variables.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

            return variables;
        }

        /// <summary>
        /// The block as the NUL-separated, double-NUL-terminated string
        /// `CREATE_UNICODE_ENVIRONMENT` requires.  Split from <see cref="Build"/> so that the
        /// ordering and the filter can be asserted as data rather than through a pointer.
        /// </summary>
        public static string ToBlockText(List<KeyValuePair<string, string>> variables)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var pair in variables)
            {
                builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
            }
            builder.Append('\0');
            return builder.ToString();
        }
    }
}
