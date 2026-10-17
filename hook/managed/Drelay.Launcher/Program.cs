// Program.cs -- Drelay.Launcher.
//
// WHAT THIS PROGRAM IS FOR
// ------------------------
// Its whole job is to make one ordering true, and then to prove it happened:
//
//   1. the game process exists but has run no code            (CreateProcess, CREATE_SUSPENDED)
//   2. the hook DLL is resident in it before CoreCLR exists   (inject, call Init)
//   3. the environment cannot defeat the design               (a hand-built block, not inheritance)
//   4. the game runs, and the hook was in place first         (resume, then compare the logs)
//
// It is the preferred injection mechanism from HOOK-ARCHITECTURE.md section 3.2,
// chosen over the app-directory proxy DLL (section 3.1) because that one requires
// writing into the game's installation, and the drelay project's stated principle
// is that it does not.  Everything this program touches lives under the run
// directory in %LOCALAPPDATA%.
//
// WHERE THE CONFIGURATION COMES FROM (HANDOFF-LAUNCH-CONFIG.md)
// ------------------------------------------------------------
// Four sources, merged FIELD BY FIELD in this order, first one wins:
//
//   1. the command line                       (Settings from CommandLine.cs)
//   2. %LOCALAPPDATA%\drelay\hook\drelay.config.json   (Settings from ConfigFile.cs)
//   3. auto-discovery: Steam, the pinned record, the launcher's own directory
//                                             (Discovery.cs)
//   4. the compiled defaults                  (Options in Settings.cs)
//
// There is deliberately NO environment-variable layer, and the reason is in this file:
// `EnvironmentBlock` deletes every `DRELAY_HOOK_*` name from the child's environment, so a
// configuration read from that namespace would be silently deleted (section 4).
//
// A DOUBLE-CLICK IS A SUPPORTED WAY TO START THIS PROGRAM.  With no arguments at all the
// launcher creates the configuration file if it is missing, loads it, discovers the game if the
// file does not say where it is, and simply runs -- no prompt, no pause, no console output the
// user has to dismiss.  The pause exists for exactly one failure mode, a console window vanishing
// before a human can read why, and it is applied to exactly that (section 6).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Drelay.Launcher
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string launcherDirectory = AppContext.BaseDirectory;
            string configPath = ConfigFile.DefaultPath(null);

            // --- 1. the command line -------------------------------------------
            Settings cli;
            List<string> gameArguments;
            bool help;
            string error;
            bool parsed;
            try
            {
                parsed = CommandLine.TryParse(args, out cli, out gameArguments, out help, out error);
            }
            catch (ArgumentException ex)
            {
                // A flag with no value.  The parser throws because the alternative -- threading a
                // ref error through every `Next` -- buries the flag names; catching it here keeps
                // the message and stops a typo from becoming an unhandled exception.
                parsed = false;
                help = false;
                gameArguments = new List<string>();
                cli = new Settings();
                error = ex.Message;
            }
            if (!parsed)
            {
                Console.Error.WriteLine("error: " + error);
                Usage();
                return 2;
            }
            if (help)
            {
                Usage();
                return 0;
            }

            // --- 2. the configuration file -------------------------------------
            //
            // READ BEFORE ANYTHING ELSE IS DECIDED, because "where is the game" is answered by
            // this file first and the file's own absence is a legal state (rule 1 of section 5).
            //
            // AND ON A FIRST NO-ARGUMENT RUN, WRITTEN FIRST.  This is the one deviation from
            // section 6's flow and it is deliberate: section 6 writes a template only when
            // discovery fails, so the operator whose game *was* found -- every Steam user, which is
            // the case section 7 exists for -- never sees the file and can only change what the
            // hook does by knowing the schema by heart.  Writing it costs nothing, blocks nothing,
            // pauses nothing, and is what turns "launched it and nothing happened" into "here is
            // the file, turn the dodge on in it".
            if (FirstRun.ShouldWriteTemplate(args.Length == 0, File.Exists(configPath)))
            {
                if (ConfigFile.WriteTemplate(configPath, out string templateError))
                {
                    Console.WriteLine("configuration : " + configPath + "   (new; edit it to change what runs)");
                }
                else
                {
                    // A read-only profile is not a reason to refuse to start: the compiled defaults
                    // are exactly what the template would have said.
                    Console.Error.WriteLine("warning: could not write " + templateError);
                }
            }

            if (!ConfigFile.TryRead(configPath, out Settings config, out _, out error))
            {
                Console.Error.WriteLine("error: " + error);
                return 2;
            }

            // --- 3. merge, with discovery in the middle ------------------------
            //
            // All four sources and the order they win in live in `Configuration.Resolve`, which is
            // also what the offline harness exercises; this method decides only what to SAY when
            // there is no game to start.
            IDiscoveryEnv explore = OperatingSystem.IsWindows()
                ? new WindowsDiscoveryEnv(launcherDirectory)
                : null;
            ResolveResult resolved = Configuration.Resolve(
                config, cli, gameArguments, explore, PinnedBuildRecord(), launcherDirectory);

            switch (resolved.Outcome)
            {
                case ResolveOutcome.NoGamePath:
                    return NoGamePath(args.Length == 0, configPath, resolved.Discovery);
                case ResolveOutcome.Invalid:
                    Console.Error.WriteLine("error: " + resolved.Error);
                    return 2;
            }

            Options options = resolved.Options;

            // WHAT THE MERGE DECIDED THAT THE OPERATOR DID NOT LITERALLY ASK FOR.  Printed before
            // the run rather than buried in a log, because each of these is a case where a
            // configuration file and a command line disagreed and the outcome is not the one a
            // reader of the command line alone would predict.
            foreach (string notice in resolved.Notices)
            {
                Console.WriteLine("note          : " + notice);
            }

            try
            {
                return Run(options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAILED: " + ex.Message);
                if (options.Verbose)
                {
                    Console.Error.WriteLine(ex);
                }
                return 1;
            }
        }

        /// <summary>
        /// The no-game refusal, and the first-run experience.
        ///
        /// TWO BRANCHES, AND THE DIFFERENCE BETWEEN THEM IS WHO IS WATCHING.  A double-click
        /// (`args.Length == 0`) with no redirected input gets a written template, the exact path,
        /// the field to fill in, and a keypress to hold the window open.  A scripted run, which
        /// always passes at least one argument, gets the same explanation on stderr and its exit
        /// code -- it must never block on a keypress, and it never writes a file it did not ask
        /// for.  The decision itself is `FirstRun.ShouldPause`, which is the function section 13
        /// C5 is about.
        /// </summary>
        private static int NoGamePath(bool noArguments, string configPath, DiscoveryResult discovery)
        {
            string why = discovery != null && discovery.Failure != null
                ? discovery.Failure
                : "and neither the command line nor the configuration file said where it is";

            if (!noArguments)
            {
                Console.Error.WriteLine("error: no game path: " + why);
                Console.Error.WriteLine("Pass --game PATH, or set \"GamePath\" in " + configPath + ".");
                Usage();
                return 2;
            }

            Console.Error.WriteLine("The game could not be found: " + why);
            Console.Error.WriteLine();

            // THE WRITING IS NOT DONE HERE ANY MORE.  `Main` writes the template when a
            // no-argument run finds it missing, before anything is resolved, so by the time this
            // runs the file is either there or could not be created at all.  One place writes it;
            // this place says what to put in it.
            if (File.Exists(configPath))
            {
                Console.Error.WriteLine("Open it and fill in \"GamePath\" with the folder that holds");
                Console.Error.WriteLine("  " + Drelay.Bootstrap.GeneratedBuildTable.ExecutableFileName);
                Console.Error.WriteLine("(on Steam that is usually");
                Console.Error.WriteLine("  <library>\\steamapps\\common\\Darza's Dominion):");
                Console.Error.WriteLine("  " + configPath);
            }
            else
            {
                Console.Error.WriteLine("Pass --game PATH, or create a file with a \"GamePath\" in it at:");
                Console.Error.WriteLine("  " + configPath);
            }
            Console.Error.WriteLine();

            // A REFUSAL THE USER CANNOT FIX BY EDITING THE FILE IS NOT PAUSED ON (section 6):
            // the OpenGL mismatch is printed and the window is allowed to close, because the
            // answer is not in a file this program can point at.
            bool userFixable = discovery == null || !discovery.OpenGlVariant;
            if (FirstRun.ShouldPause(noArguments, Console.IsInputRedirected, userFixable))
            {
                Console.Error.WriteLine("Press any key to close this window.");
                try
                {
                    Console.ReadKey(intercept: true);
                }
                catch (InvalidOperationException)
                {
                    // No console to read from after all; nothing to hold open.
                }
            }
            return 2;
        }

        /// <summary>
        /// The pinned build, as discovery needs it.
        ///
        /// READ FROM `GeneratedBuildTable` RATHER THAN WRITTEN DOWN AGAIN, which is the same rule
        /// every other pinned number in this repository follows: `known-build.json` is the record,
        /// the generator turns it into the table, and a launcher with its own copy of the app id
        /// or the assembly size would be a second source of truth for a question Phase 0 already
        /// answered once.
        /// </summary>
        private static PinnedBuild PinnedBuildRecord()
        {
            return new PinnedBuild
            {
                RecordedFrom = Drelay.Bootstrap.GeneratedBuildTable.RecordedFrom,
                ExecutableFileName = Drelay.Bootstrap.GeneratedBuildTable.ExecutableFileName,
                AssemblyFileName = Drelay.Bootstrap.GeneratedBuildTable.AssemblyFileName,
                AssemblySize = Drelay.Bootstrap.GeneratedBuildTable.AssemblySize,
                AssemblySizeOpenGlVariant = Drelay.Bootstrap.GeneratedBuildTable.AssemblySizeOpenGlVariant,
                SteamAppId = Drelay.Bootstrap.GeneratedBuildTable.SteamAppId,
            };
        }

        private static int Run(Options options)
        {
            string gameExe = ResolveGameExecutable(options.GamePath, options.ExecutablePath);
            string gameDir = Path.GetDirectoryName(gameExe);
            string bootstrap = Path.GetFullPath(options.BootstrapDll);
            string hook = Path.GetFullPath(options.HookDll);

            foreach (string required in new[] { gameExe, bootstrap, hook })
            {
                if (!File.Exists(required))
                {
                    Console.Error.WriteLine("FAILED: not found: " + required);
                    return 1;
                }
            }

            uint session = (uint)(Process.GetCurrentProcess().Id & 0xFFFF);
            options.RunDirectory = Path.Combine(
                options.RunRoot,
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                "-p" + session.ToString(CultureInfo.InvariantCulture));

            Directory.CreateDirectory(options.RunDirectory);
            Console.WriteLine("run directory : " + options.RunDirectory);
            Console.WriteLine("game          : " + gameExe);
            if (options.DiscoveredFrom != null)
            {
                // ONE LINE, AND ONLY WHEN NOBODY SAID WHERE THE GAME WAS.  A discovered path is a
                // fact the operator should be able to see in the transcript of the run -- it is
                // the one input the launcher chose for itself -- and it is not a prompt.
                Console.WriteLine("game (discovered from auto-discovery): " + options.DiscoveredFrom);
            }

            // The environment is built, not inherited.  This is the one place the
            // DOTNET_TieredCompilation hazard from HOOK-ARCHITECTURE.md section 1
            // can be closed, and closing it here is why the launcher exists at all
            // rather than a shell script that sets a variable and starts the game.
            var environmentVariables = EnvironmentBlock.Build(
                options, gameDir, bootstrap, session, options.RunDirectory,
                InheritedEnvironment(), Console.WriteLine);
            IntPtr environment = Marshal.StringToHGlobalUni(
                EnvironmentBlock.ToBlockText(environmentVariables));

            using (var shared = SharedBlock.Create(session, options.RunDirectory))
            {
                shared.WriteHeader();
                shared.SetConfigReady();

                // --- 1. create, suspended ------------------------------------
                //
                // THE CHILD'S STANDARD HANDLES ARE CONNECTED TO THIS PROCESS'S, AND
                // THAT IS EVIDENCE PLUMBING RATHER THAN POLITENESS.
                //
                // `bInheritHandles` used to be false with no `STARTF_USESTDHANDLES`, so
                // the game's stdout and stderr went wherever a console-less child's go:
                // nowhere.  PHASE1-HANDOFF.md §9 item 3 asked for the console to be
                // captured because `Fatal error. Internal CLR error. (0x80131506)` is
                // printed to stderr -- and through the only supported way to start the
                // game that capture was impossible.  A crash that reported itself only on
                // stderr was therefore indistinguishable from one that reported nothing.
                //
                // Only the three duplicated standard handles are inheritable; the block
                // mapping and the events are created with null security attributes and so
                // are private, which is what keeps `bInheritHandles: true` from leaking
                // the launcher's own objects into the game.
                var startup = new NativeMethods.STARTUPINFOW();
                startup.cb = Marshal.SizeOf<NativeMethods.STARTUPINFOW>();
                IntPtr childIn = NativeMethods.DuplicateStdHandle(NativeMethods.STD_INPUT_HANDLE);
                IntPtr childOut = NativeMethods.DuplicateStdHandle(NativeMethods.STD_OUTPUT_HANDLE);
                IntPtr childErr = NativeMethods.DuplicateStdHandle(NativeMethods.STD_ERROR_HANDLE);
                startup.dwFlags = (int)NativeMethods.STARTF_USESTDHANDLES;
                startup.hStdInput = childIn;
                startup.hStdOutput = childOut;
                startup.hStdError = childErr;
                NativeMethods.PROCESS_INFORMATION pi;

                var commandLine = new StringBuilder(Quote(gameExe));
                foreach (string extra in options.GameArguments)
                {
                    commandLine.Append(' ').Append(Quote(extra));
                }

                bool created;
                try
                {
                    created = NativeMethods.CreateProcess(
                        gameExe, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                        NativeMethods.CREATE_SUSPENDED | NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                        environment, gameDir, ref startup, out pi);
                }
                finally
                {
                    // Closed as soon as the child has them: holding a duplicate of a
                    // pipe for the whole session would keep the write end alive after
                    // the reader has gone.
                    if (childIn != IntPtr.Zero) { NativeMethods.CloseHandle(childIn); }
                    if (childOut != IntPtr.Zero) { NativeMethods.CloseHandle(childOut); }
                    if (childErr != IntPtr.Zero) { NativeMethods.CloseHandle(childErr); }
                }
                if (!created)
                {
                    Console.Error.WriteLine("FAILED: CreateProcess: " + NativeMethods.LastErrorText());
                    return 1;
                }

                Console.WriteLine("game pid      : " + pi.dwProcessId);
                Console.WriteLine("hook          : " + hook + (options.Off ? "   (OFF: inject and report only)" : string.Empty));

                try
                {
                    // --- 2. inject, while no managed code has run -------------
                    // One stub, three steps (LoadLibraryW, GetProcAddress,
                    // DrelayHook_Init) -- see NativeMethods for why a two-call
                    // sequence cannot work and what the stub does instead.
                    uint initResult = NativeMethods.InjectAndInitialize(pi.hProcess, hook, options.Verbose);
                    if (initResult == 0)
                    {
                        Console.Error.WriteLine(
                            "FAILED: the hook DLL loaded but DrelayHook_Init returned 0. The game " +
                            "will run unhooked; hook.log and OutputDebugString explain why. " +
                            "Terminating the suspended process.");
                        NativeMethods.TerminateProcess(pi.hProcess, 1);
                        return 1;
                    }

                    // --- 3. resume -------------------------------------------
                    if (NativeMethods.ResumeThread(pi.hThread) == unchecked((uint)-1))
                    {
                        Console.Error.WriteLine("FAILED: ResumeThread: " + NativeMethods.LastErrorText());
                        NativeMethods.TerminateProcess(pi.hProcess, 1);
                        return 1;
                    }

                    // --- 4. wait for the hook, then for the bootstrap --------
                    bool nativeReady = shared.WaitForNative(options.TimeoutMs);
                    Console.WriteLine("native ready  : " + (nativeReady ? "yes" : "NO (timed out)"));

                    bool bootReady = shared.WaitForBootstrap(5000);
                    if (nativeReady)
                    {
                        shared.Report(Console.WriteLine, options.Verbose);
                    }

                    if (options.NoWait)
                    {
                        Console.WriteLine("--no-wait: the game is running; not waiting for it to exit.");
                        return nativeReady ? 0 : 3;
                    }

                    Console.WriteLine();
                    Console.WriteLine("The game is running. Close it (or press Ctrl+C here) when done.");
                    Console.WriteLine("Evidence will be in: " + options.RunDirectory);
                    Console.WriteLine();

                    // Wait for the game, REPORTING THE HOOK'S PROGRESS AS IT ARRIVES.
                    //
                    // The comment here always claimed this, and the code did not do it:
                    // `WaitForSingleObject` sat in a loop and printed nothing, so every
                    // operational message the hook emits -- `waiting 30000 ms before
                    // collection starts`, `collection started`, the per-target verdicts --
                    // reached the operator only after the game closed, if at all.  The
                    // launcher printed a snapshot of the shared block at the end and a
                    // line saying where the evidence would be, which is a report, not a
                    // transcript.
                    //
                    // An operator cannot use a collection window they cannot see: the
                    // first three live runs after the delay went in all produced
                    // `0 of 4 targets found`, and the one datum needed to understand that
                    // -- whether collection had started and when -- was in a file nobody
                    // had told them to watch.
                    //
                    // The hook's log is append-only by construction (see `DrLogFlushTo`:
                    // the mode is always "ab", because truncating destroyed the
                    // bootstrap's lines), and it flushes after each decisive line, so
                    // tailing the file is exactly following the run.
                    var exitWatch = Stopwatch.StartNew();
                    int exitCode = int.MinValue;
                    using (var tail = new LogTail(Path.Combine(options.RunDirectory, "hook.log")))
                    {
                        Console.WriteLine("---- hook log (live) ----");
                        while (exitWatch.Elapsed.TotalSeconds < 6 * 3600)
                        {
                            uint waited = NativeMethods.WaitForSingleObject(pi.hProcess, 2000);
                            tail.Drain(line => Console.WriteLine("  " + line));
                            if (waited == NativeMethods.WAIT_OBJECT_0)
                            {
                                NativeMethods.GetExitCodeProcess(pi.hProcess, out exitCode);
                                break;
                            }
                        }
                        // The hook may flush its last lines as the process dies, so one
                        // final drain after the wait is what keeps `phase 1 complete` off
                        // the cutting-room floor -- and a final held-line flush is what
                        // keeps the line after it, which has no trailing newline to be
                        // matched on, from being dropped.
                        tail.Drain(line => Console.WriteLine("  " + line));
                        tail.FlushHeldLine(line => Console.WriteLine("  " + line));
                        Console.WriteLine("---- end of hook log ----");
                    }
                    if (exitCode == int.MinValue)
                    {
                        Console.WriteLine("the game is still running after six hours; giving up on waiting.");
                    }
                    else
                    {
                        Console.WriteLine("game exited with code " + exitCode);
                    }

                    shared.Report(Console.WriteLine, verbose: true);
                    ReportEvidence(options, gameDir, bootReady, nativeReady);
                    return nativeReady ? 0 : 3;
                }
                finally
                {
                    NativeMethods.CloseHandle(pi.hThread);
                    NativeMethods.CloseHandle(pi.hProcess);
                }
            }
        }

        /// <summary>
        /// This process's own environment, as pairs.
        ///
        /// A separate method so that `EnvironmentBlock.Build` can be given a fake one: the
        /// filter, the "only what was supplied" rule and the ordering are all decided there, and
        /// a test of them must not depend on what the test runner happens to have in its shell.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, string>> InheritedEnvironment()
        {
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                pairs.Add(new KeyValuePair<string, string>((string)entry.Key, (string)entry.Value));
            }
            return pairs;
        }

        private static void ReportEvidence(Options options, string gameDir, bool bootReady, bool nativeReady)
        {
            string logPath = Path.Combine(options.RunDirectory, "hook.log");
            Console.WriteLine();
            Console.WriteLine("---- evidence ----");
            Console.WriteLine("hook.log      : " + (File.Exists(logPath) ? "present" : "MISSING"));

            // THE BOOTSTRAP LINE IS IN `bootstrap.log`, NOT IN `hook.log`.
            //
            // This check looked only in `hook.log` and therefore reported
            // "the bootstrap line is MISSING" on every run where it was in fact
            // present -- which is every run.  The two files are separate on purpose:
            // two writers on one path, opened by two runtimes with different sharing
            // modes, produced a file containing one writer's lines and not the
            // other's, so the native side writes `hook.log` and the managed bootstrap
            // writes `bootstrap.log`.  A report that contradicts the design it is
            // reporting on is worse than no report: a reader of a live run would
            // conclude Spike 1.1 had failed.
            string bootLogPath = Path.Combine(options.RunDirectory, "bootstrap.log");
            string bootLine = FindLine(logPath, "DOTNET_STARTUP_HOOKS is running");
            string bootLineWhere = "hook.log";
            if (bootLine == null)
            {
                bootLine = FindLine(bootLogPath, "DOTNET_STARTUP_HOOKS is running");
                bootLineWhere = "bootstrap.log";
            }
            Console.WriteLine("spike 1.1     : " + (bootLine != null
                ? "the bootstrap ran; its first line is in " + bootLineWhere
                : "the bootstrap line is MISSING from both hook.log and bootstrap.log"));

            Console.WriteLine("bootstrap     : " + (bootReady ? "signalled readiness" : "did NOT signal"));
            Console.WriteLine("native        : " + (nativeReady ? "answered" : "did NOT answer"));
            // A DISCOVERED INSTALL THAT THE HOOK THEN REFUSED IS THE ONE CASE SECTION 7.1 WANTS
            // POINTED AT, because the release zip does not carry `Fingerprint.ps1`: the hook's own
            // identity check wrote the answer into the run directory, and saying so is the whole
            // of the fix.
            string selfCheck = Path.Combine(options.RunDirectory, "selfcheck.txt");
            if (File.Exists(selfCheck))
            {
                Console.WriteLine("selfcheck     : " + selfCheck);
            }
            Console.WriteLine();
            Console.WriteLine("Next: compare the bootstrap line's position in bootstrap.log against the");
            Console.WriteLine("game's own log, and check selfcheck.txt and offsets.csv in the run directory.");
            if (Directory.Exists(gameDir))
            {
                Console.WriteLine("Game log directory (if any): " + Path.Combine(gameDir, "logs"));
            }
        }

        /// <summary>
        /// The first line of <paramref name="path"/> containing <paramref name="needle"/>,
        /// or null when the file does not exist or does not contain it.  A missing file
        /// is a null result rather than an exception: this runs at the end of a run that
        /// may well have died before writing anything, which is exactly when a report is
        /// most wanted.
        /// </summary>
        private static string FindLine(string path, string needle)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    if (line.Contains(needle))
                    {
                        return line;
                    }
                }
            }
            catch (IOException)
            {
            }
            return null;
        }

        private static string ResolveGameExecutable(string path, string explicitExe)
        {
            if (!string.IsNullOrEmpty(explicitExe))
            {
                return explicitExe;
            }
            if (Directory.Exists(path))
            {
                // The apphost's name, derived from the pinned build record rather
                // than stated here a second time.  Nothing else in the hook
                // hard-codes it, and a launcher that did would be the one place a
                // rename broke silently.
                return Path.Combine(path, Drelay.Bootstrap.GeneratedBuildTable.ExecutableFileName);
            }
            return path;
        }

        private static string Quote(string value)
        {
            if (value.IndexOf(' ') < 0 && value.IndexOf('"') < 0)
            {
                return value;
            }
            return "\"" + value.Replace("\\\"", "\\\"") + "\"";
        }

        private static void Usage()
        {
            Console.WriteLine(@"Drelay.Launcher -- start the game suspended, inject the hook, resume it.

  With no arguments at all it reads
    %LOCALAPPDATA%\drelay\hook\drelay.config.json
  and creates that file when it is not there yet, then runs with it. The
  written file is the release default -- auto-aim at the closest damageable
  enemy, and the live dodge -- and it is where either is turned off. If the
  file does not say where the game is, the game is found (Steam, the recorded
  install, then this program's own directory) and started. A double-click is
  a supported way to run this program.

  --game PATH          the game directory or the executable itself. Required
                       unless the configuration file sets GamePath or the game
                       is discovered.
  --exe PATH           the executable to start, when it is not
                       <game dir>\DarzasDominion.exe. Used by the injection test.
  --assembly PATH      the managed assembly the hook should identify, when it is
                       not <game dir>\DarzasDominion.dll. Its directory becomes the
                       game directory the hook probes. Used by the injection test.
  --hook PATH          Drelay.Hook.dll  (default: beside this program)
  --bootstrap PATH     Drelay.Bootstrap.dll (default: beside this program)
  --runroot PATH       where run directories are created
                       (default: %LOCALAPPDATA%\drelay\hook)
  --timeout MS         how long to wait for the hook to answer (default 60000)
  --collect-delay MS   how long the hook waits before it opens the discovery
                       window (default 0; the window used to open 30 s in).
                       The patch no longer waits for that window, so this only
                       affects when the per-target verdicts are measured.
  --discovery-ms MS    how long the discovery window stays open (default 10000).
                       It closes early the moment every target is in. This is a
                       reporting bound, not a functional one: a target compiled
                       after it closes is still matched and still patched.
  --resolve-delay MS   how long the hook waits for the runtime to leave the
                       startup-hook phase before it calls into managed code
                       (default 100; 0 asks the question 'does the margin
                       matter at all'). A margin, not a signal: a startup hook
                       cannot JIT-compile anything, so it cannot execute a
                       single statement after it returns.
  --live-seconds S     how long the hook records after the DISCOVERY WINDOW ends,
                       in seconds (default 5). The evidence files are written when
                       that window ends, so an endurance run is
                       `--live-seconds 3600`. The frame read STAYS OPEN when the
                       window closes -- it is the sensor the dodge runs on.
  --cores N            tell the game's runtime it has N processors
                       (DOTNET_PROCESSOR_COUNT). The game's startup data load runs
                       a Parallel.For, and the CLR's thread-suspension race gets
                       worse the more threads it has; a machine where the hook dies
                       during loading can cap this without changing the game. Not set
                       by default, so the runtime's own count wins.
  --pause-after-run    stop reading the world when the evidence window closes,
                       which is what the hook used to do by default
                       (DRELAY_KEEP_READING=0).
  --off                off switch: inject nothing managed, patch nothing
  --full-trace         log every JIT compilation, not only target hits
  --act                dry run off (the hook may act; Phase 3+ only)
  --no-wait            do not wait for the game to exit
  --dbg-dump           ask the CLR to write <run dir>\crashdump.dmp if it dies.
                       Off by default; needed for automated runs, because WER
                       LocalDumps fires for an operator's shell and not for one
                       spawned from the agent harness.
  --test-target SPEC   publish one extra target for a test host, in addition to
                       the four pinned game targets. SPEC is
                       `name|declaringType|methodName|ilSize|ilHash`, and more
                       than one may be joined with `;`. ONLY for
                       Test-Injection.ps1, whose host is not the game and so has
                       no target in known-build.json; the hook logs every test
                       target it appends. Never used for a real session.
  --frame-helper       call a plain managed helper from the frame detour every
                       frame, so Phase 2 can measure whether managed code that
                       allocates and triggers collections is safe on the game's
                       frame thread. The helper allocates 64 KB per frame on
                       purpose, so this is OFF by default and is only for
                       Test-Injection.ps1's run 7.
  --frame-replacement  patch the frame tick with a MANAGED method instead of the
                       native detour, so the game's own call becomes a
                       managed-to-managed call and no native frame interrupts the
                       managed stack. Replaces the detour: with this on, the native
                       frame read and its ring are not in the path. Off by default;
                       Phase 2's mechanism test.
  --trace              SPIKE 3.2: record trace.jsonl in the run directory. Sets the frame
                       replacement, the frame sensor and the recorder together, because any of
                       the three missing produces a valid, empty file.
  --move-steer VALUE   PHASE 3 SPIKE 3.1: let the hook steer the player, using
                       the hook's own actuator. VALUE selects the experiment and is
                       documented in PHASE3-SCOPE.md; without this switch the
                       movement replacement is still published and verified, and
                       only calls through. Examples:
                         --move-steer hold:90:8000     hold 90 deg for 8 s
                         --move-steer sweep:1200       cycle the cardinal headings
                                                      every 1200 frames and log the
                                                      commanded-vs-observed pairs
                       The player WILL move. Do not use it in a run whose result
                       matters, and never while a nexus cast is in progress.
  --autoaim MODE       AUTO-AIM: replace the game's own target selection so the player
                       always aims at a chosen enemy, through a full 360 degrees rather
                       than the narrow cone the shipped desktop aimbot uses (that cone is
                       `player.AimAssist * pi / 20`, rebuilt from the equipped gear's
                       aim-assist affix, so with no affix the shipped one finds nothing at
                       all). MODE is:
                         closest     the nearest damageable enemy in range
                         highesthp   the damageable enemy with the most health
                       `in range` is the client's own weapon range -- the same function the
                       shipped selection calls -- and `damageable` is the client's own
                       predicate, `Effects.Unhittable()`, which is true for Invincible,
                       Invulnerable and Cutscene; those are never chosen. Dead enemies and
                       NPCs are skipped, and the client's own projectile leading, shot
                       geometry and target highlight are unchanged.
                       IT ARMS A REAL SETTING: Prefs.enable_aimbot is set ON, because
                       Player.Shoot checks it before it ever asks for a target, and the game
                       persists that setting. Without this switch the replacement is still
                       published and verified, and only calls through.
                       It implies --frame-replacement, and that is not a preference: the
                       flag must be armed from the game's own thread before the target is
                       ever called (nothing compiles it otherwise), and the frame
                       replacement is the only game-thread entry point a run has.
  --dodge              DELIVERABLE 3.3, DRY RUN: run the solver on live frames and LOG what
                       it would have done -- the heading, the magnitude, the reason, the
                       candidate count, the cost -- without writing the input at all. Sets
                       the frame replacement and the frame sensor, because the decision runs
                       on the sensor's frame path and an empty threat set decides `NoThreat`
                       on every frame. This is the default shape of the dodge and the one to
                       run first: it is the paired-trial evidence, and it cannot move anybody.
  --dodge-act          THE LIVE DODGE, AND THE ONLY SWITCH IN THIS PROJECT THAT MOVES THE
                       PLAYER ON THE SOLVER'S WORD. Implies --dodge and additionally sets
                       DRELAY_DODGE_ACT=1, patches the movement replacement and runs the pilot
                       in `dodge` mode. On any frame the adapter publishes no command the
                       pilot writes nothing, so the player's own input stands. Mutually
                       exclusive with --move-steer. The player WILL move; the log says where.
  --dodge-disc K       THE RULE 6 DECISION WINDOW, as a multiple of the time a projectile takes
                       to cross the collision disc (player radius + projectile radius). The
                       window is how much of a bullet's path counts as the frame being spent and is refused
                       outright; past it the candidate is only refused if it gets closer to the
                       bullet than standing still would. It was a constant 50 ms, which is a
                       DISTANCE of `0.05 * speed` -- about 1.95x the collision disc on a 16 u/s
                       projectile and 0.53x of it on a 6 u/s one -- so one number could not be
                       right for both. K is unitless and now does the same job at every speed.
                       Default 2 (two disc-crossings). K=0 turns the scaling off and runs the
                       rule on --dodge-window alone, which with 0.05 is the old behaviour exactly.
                       Sets DRELAY_DODGE_DECISION_DISC; the run logs it as `disc=Kx`.
  --dodge-window SEC   An absolute floor in seconds under that window; 0 (the default) means the
                       speed-scaled value always wins. Sets DRELAY_DODGE_WINDOW_S; logged as
                       `floor=SECs`.
  --escape-test SEC    PHASE 3'S FLOOR TEST: call World.RequestEscape() once, from
                       inside the game, SEC seconds after the world first exists.
                       This is the client's own nexus entry point, so it runs the
                       client's own gates -- in a safe area it is a legitimate no-op,
                       and whether it sent GmEscape is a wire fact to read from the
                       relay log. Implies --frame-replacement, because that is where
                       the call is made from. It fires ONCE and then stops.
                       Example: --escape-test 25   (a normal run, then a nexus)
  --verbose            print the environment that was removed, and diagnostics
  --help               this text

Any other argument is passed through to the game.");
        }
    }

    /// <summary>
    /// Follows an append-only file and yields each new complete line once.
    ///
    /// WHY A TAIL RATHER THAN WATCHING THE SHARED BLOCK.  Both are available: the
    /// launcher maps the block and can read `compileCalls`, `targetsFound` and the
    /// status bits at any time.  The block is the right source for a *state* -- how
    /// many targets, which bits are set -- and it is the wrong source for a
    /// *narrative*: it cannot say "waiting 30000 ms before collection starts", it has
    /// no notion of ordering between the counter updates, and several of the messages
    /// an operator needs (the delay, the window opening, each target's verdict) exist
    /// only as log lines.  The file is the record of what happened; this follows it.
    ///
    /// Two properties of the hook's writer make this simple, and both are relied on
    /// rather than assumed:
    ///
    ///   * the file is append-only.  `DrLogFlushTo` always opens with "ab" because
    ///     truncating on a first pass destroyed the bootstrap's lines -- so a byte
    ///     offset is a valid cursor and no line can be rewritten behind us;
    ///   * it is flushed after every decisive line, so a tail sees those lines while
    ///     the process is still running rather than at exit.
    ///
    /// A partial line at the end of a read is held over rather than printed: the
    /// writer flushes per line, but a read can still land mid-write, and printing half
    /// a line and then the whole of it is worse than printing it once.
    /// </summary>
    internal sealed class LogTail : IDisposable
    {
        private readonly string _path;
        private long _offset;
        private readonly StringBuilder _partial = new StringBuilder();

        public LogTail(string path)
        {
            _path = path;
        }

        /// <summary>
        /// Prints every complete line written since the last call.  Safe to call when
        /// the file does not exist yet (the hook may not have opened it) and after it
        /// has been closed by its writer.
        ///
        /// THE HANDLE IS OPENED AND CLOSED ON EVERY CALL, AND THAT IS THE WHOLE FIX.
        ///
        /// This used to hold one `FileStream` open for the entire session, with the
        /// reasoning that the hook appends with the C runtime's `_SH_DENYNO` and so the
        /// two should coexist.  They do not.  Measured, in isolation, with the exact
        /// parameters both sides use:
        ///
        ///   append with no other handle             -> ok
        ///   reader held open, then append           -> FAILED, errno 13, Win32 32
        ///                                                (ERROR_SHARING_VIOLATION)
        ///   append handle held open, then reader    -> FAILED, same error
        ///   two appends in a row                    -> ok
        ///
        /// So the second opener always loses, on both sides.  A tail that holds its
        /// handle for the session therefore makes the hook's flusher lose almost every
        /// append: live runs recorded 134, 261, 371 and 537 failed passes, which is
        /// roughly four a second -- the flusher's entire cadence.  The lines were safe in
        /// the ring the whole time (`0 overflowed`), but they never reached the file, so
        /// the transcript the operator reads stopped at the last line written before the
        /// tail opened.
        ///
        /// Opening per call costs one `CreateFile` every two seconds and leaves the
        /// handle held for microseconds, so the flusher's 250 ms retries always find the
        /// file free.  A reader that has to reopen is a reader that cannot starve the
        /// writer -- and the reverse of that is exactly the bug this replaces: the
        /// launcher's convenience broke the hook's evidence, which is a strictly worse
        /// trade than a slightly more expensive tail.
        /// </summary>
        public void Drain(Action<string> write)
        {
            if (!File.Exists(_path)) { return; }

            string text;
            try
            {
                // FileShare.ReadWrite, because the hook may hold the file open for
                // appending at the moment this opens, and this end of the pair is the
                // one that can afford to be refused and retry.
                using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                {
                    long length = stream.Length;

                    // A shrunk or replaced file would make the offset meaningless.  It
                    // cannot happen with an append-only writer, but a stale offset would
                    // silently print nothing at all, and silence is indistinguishable
                    // from "the hook said nothing".
                    if (length < _offset)
                    {
                        _offset = 0;
                        _partial.Clear();
                    }
                    if (length == _offset) { return; }

                    stream.Seek(_offset, SeekOrigin.Begin);
                    var buffer = new byte[Math.Min(length - _offset, 64 * 1024)];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    _offset += read;
                    text = Encoding.UTF8.GetString(buffer, 0, read);
                }
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }

            string all = _partial + text;
            _partial.Clear();
            int start = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != '\n') { continue; }
                write(all.Substring(start, i - start).TrimEnd('\r'));
                start = i + 1;
            }
            // Whatever follows the last newline is an incomplete line; keep it.
            if (start < all.Length) { _partial.Append(all, start, all.Length - start); }
        }

        /// <summary>
        /// Prints any line still held back, because the writer is not going to finish it.
        /// Call this once, after the process under observation has exited.
        ///
        /// WHY THIS EXISTS: the last thing the hook writes before it stops is almost
        /// always followed by no further output, so its final newline may arrive after
        /// the last drain -- or, when the writer is killed, never.  Holding that line
        /// for a newline that will not come is how a run appears to end one line early,
        /// which is exactly what the first version of this did: `collection started`
        /// appeared in `hook.log` and never on the operator's console.
        ///
        /// It is safe at the end and NOT safe mid-run.  A partial line mid-run is a line
        /// still being appended to, and printing it would mean printing a truncated
        /// message and then the whole of it; at the end there is nothing left to append.
        /// </summary>
        public void FlushHeldLine(Action<string> write)
        {
            if (_partial.Length == 0) { return; }
            write(_partial.ToString().TrimEnd('\r'));
            _partial.Clear();
        }

        public void Dispose()
        {
            // Nothing is held between calls, so there is nothing to release.  Kept so
            // the `using` at the call site stays honest about the lifetime.
        }
    }
}
