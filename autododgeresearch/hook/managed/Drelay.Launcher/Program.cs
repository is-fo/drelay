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
    internal sealed class Options
    {
        public string GamePath;
        public string ExecutablePath;
        public string AssemblyPath;
        public string HookDll;
        public string BootstrapDll;
        public string RunRoot;
        public bool Off;
        public bool DryRun = true;
        public bool FullTrace;
        public bool Verbose;
        public bool NoWait;
        public int TimeoutMs = 60000;

        // Whether the child is asked to write a crash dump of its own.
        //
        // THE LAUNCHER DOES NOT SET THIS BY DEFAULT, AND THE REASON IS WHICH HAND IS
        // DOING THE WRITING.  On this machine WER `LocalDumps` is already enabled
        // machine-wide, so a crash started from an operator's own shell leaves a full
        // dump in `%LOCALAPPDATA%\CrashDumps` without anyone asking.  A process started
        // from the agent harness does NOT -- measured, repeatedly -- and the CLR's own
        // `DOTNET_DbgEnableMiniDump` is the only route left, which the launcher's
        // environment filter deletes.  So the switch exists to give automation the
        // evidence the operator already gets, and it stays off by default so a normal
        // session does not accumulate half-gigabyte dumps on every crash.
        public bool DbgDump;

        // The operator's head start before the hook starts watching the compilation
        // stream, in milliseconds.  Passed to the hook as
        // `DRELAY_HOOK_COLLECT_DELAY_MS`; the hook clamps it and falls back to its own
        // 30 s default if the variable is absent or unparseable, so this is a
        // convenience rather than the only way to set it.
        //
        // It has to be plumbed through the launcher because the launcher *strips every
        // `DRELAY_HOOK_*` variable from the child's environment* before adding its own
        // -- a deliberate safety measure that removes the `DOTNET_TieredCompilation`
        // hazard namespace, and the reason a flag set from the operator's shell in that
        // namespace would never reach the hook.  See PHASE1-HANDOFF.md section 4 trap 1.
        public int CollectDelayMs = 30000;

        // A TARGET FOR A TEST HOST, AND ONLY EVER FOR A TEST HOST.
        //
        // The hook's target table comes from `known-build.json` through
        // `GeneratedBuildTable.cs`, and that is the Phase 0 gate's pinned record of
        // *the game*: `Fingerprint.ps1` checks exactly those four IL hashes, so a
        // fifth entry there would be a lie about the build and `il_index.py` would
        // drop it on the next regeneration anyway.  A test that wants the detour to
        // match a method of `SyntheticHost` therefore cannot use that route at all,
        // and before this option existed no run in `Test-Injection.ps1` could patch a
        // target -- which is why its most ambitious assertion was `GAP 9c the read
        // path is open but no frame has entered the detour yet`.
        //
        // The value is one or more specs separated by `;`, each
        // `name|declaringType|methodName|ilSize|ilHash`.  It travels as
        // `DRELAY_HOOK_TEST_TARGETS`, which the launcher *sets* rather than passes
        // through: the launcher deletes every inherited `DRELAY_HOOK_*` name, so a
        // stray variable in an operator's shell cannot reach the hook, and a normal
        // session -- this is null -- publishes exactly the four game targets.
        public string TestTargets;

        // Phase 2's mechanism test: call a plain managed helper from the frame detour
        // every frame.
        //
        // OFF BY DEFAULT BECAUSE THE HELPER ALLOCATES ON PURPOSE.  The measurement is
        // whether managed code that triggers collections is safe on the game's frame
        // thread -- see PHASE2-SCOPE.md section 3 and native\DrelayFrame.cpp's
        // "THE MANAGED FRAME HELPER" note.  64 KB per frame is the probe, not a
        // feature, so it is opt-in and reaches the hook as
        // `DRELAY_HOOK_FRAME_HELPER=1` (a name the launcher *sets*, so it cannot be
        // inherited by accident).
        public bool FrameHelper;

        // Phase 2's other mechanism test: patch the frame tick with a MANAGED method
        // instead of a native detour, so the game's own `call` becomes a
        // managed-to-managed call.
        //
        // IT REPLACES THE DETOUR RATHER THAN ADDING TO IT, so it is opt-in and mutually
        // exclusive with `--frame-helper` in practice: with this on, the native frame
        // read and the ring are not written by this file any more, because this file is
        // not in the path.  See `managedFrameReplacement` in DrelayHookShared.h.
        public bool FrameReplacement;

        // How long the hook keeps recording after it patches the frame tick, in
        // seconds.  Passed to the hook as `DRELAY_HOOK_LIVE_MS`.
        //
        // FIVE SECONDS WAS HARD-CODED AND IS STILL THE DEFAULT.  It is the right
        // length for proving the detour is entered on a title screen; it cannot
        // record the endurance criterion, which asks for an hour of play with no
        // crash and no frame regression, because the hook writes its evidence when
        // this window closes.  `--live-seconds 3600` is that run.
        //
        // The default is deliberately unchanged so that runs before this switch
        // existed remain comparable with runs after it.
        public int LiveSeconds = 5;

        public List<string> GameArguments = new List<string>();

        public string RunDirectory;

        public static bool TryParse(string[] args, out Options options, out string error)
        {
            options = new Options();
            error = null;
            string scriptDir = AppContext.BaseDirectory;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--game": options.GamePath = Next(args, ref i); break;
                    case "--exe": options.ExecutablePath = Next(args, ref i); break;
                    case "--assembly": options.AssemblyPath = Next(args, ref i); break;
                    case "--hook": options.HookDll = Next(args, ref i); break;
                    case "--bootstrap": options.BootstrapDll = Next(args, ref i); break;
                    case "--runroot": options.RunRoot = Next(args, ref i); break;
                    case "--timeout": options.TimeoutMs = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--collect-delay": options.CollectDelayMs = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--live-seconds": options.LiveSeconds = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--off": options.Off = true; break;
                    case "--act": options.DryRun = false; break;
                    case "--full-trace": options.FullTrace = true; break;
                    case "--verbose": options.Verbose = true; break;
                    case "--no-wait": options.NoWait = true; break;
                    case "--dbg-dump": options.DbgDump = true; break;
                case "--test-target": options.TestTargets = Next(args, ref i); break;
                case "--frame-helper": options.FrameHelper = true; break;
                case "--frame-replacement": options.FrameReplacement = true; break;
                    case "--help":
                    case "-h":
                        error = "help";
                        return false;
                    default:
                        if (a.StartsWith("--game=", StringComparison.Ordinal)) { options.GamePath = a.Substring(7); }
                        else if (a.StartsWith("--exe=", StringComparison.Ordinal)) { options.ExecutablePath = a.Substring(6); }
                        else if (a.StartsWith("--assembly=", StringComparison.Ordinal)) { options.AssemblyPath = a.Substring(11); }
                        else if (a.StartsWith("--hook=", StringComparison.Ordinal)) { options.HookDll = a.Substring(7); }
                        else if (a.StartsWith("--bootstrap=", StringComparison.Ordinal)) { options.BootstrapDll = a.Substring(12); }
                        else if (a.StartsWith("--runroot=", StringComparison.Ordinal)) { options.RunRoot = a.Substring(10); }
                        else if (a.StartsWith("--timeout=", StringComparison.Ordinal)) { options.TimeoutMs = int.Parse(a.Substring(10), CultureInfo.InvariantCulture); }
                        else if (a.StartsWith("--collect-delay=", StringComparison.Ordinal)) { options.CollectDelayMs = int.Parse(a.Substring(16), CultureInfo.InvariantCulture); }
                        else if (a.StartsWith("--live-seconds=", StringComparison.Ordinal)) { options.LiveSeconds = int.Parse(a.Substring(15), CultureInfo.InvariantCulture); }
                        else if (a.StartsWith("--", StringComparison.Ordinal))
                        {
                            error = "unknown option: " + a;
                            return false;
                        }
                        else
                        {
                            // Anything not starting with -- is passed to the game.
                            options.GameArguments.Add(a);
                        }
                        break;
                }
            }

            if (string.IsNullOrEmpty(options.GamePath))
            {
                error = "--game is required.";
                return false;
            }
            // Resolving relative paths against the *current* directory is a way
            // to be surprised when a script runs from elsewhere, so the default
            // is derived from the launcher's own location.
            options.GamePath = Path.GetFullPath(options.GamePath);
            if (!string.IsNullOrEmpty(options.ExecutablePath))
            {
                options.ExecutablePath = Path.GetFullPath(options.ExecutablePath);
            }
            if (!string.IsNullOrEmpty(options.AssemblyPath))
            {
                options.AssemblyPath = Path.GetFullPath(options.AssemblyPath);
                // When an assembly is named explicitly, its own directory is the
                // one the hook should treat as "the game directory": probing a
                // different one would be measuring the wrong tree.  This is what
                // makes the same launcher usable against a test host.
                if (File.Exists(options.AssemblyPath))
                {
                    options.GamePath = Path.GetDirectoryName(options.AssemblyPath);
                }
            }
            if (string.IsNullOrEmpty(options.HookDll))
            {
                options.HookDll = Path.Combine(scriptDir, "Drelay.Hook.dll");
            }
            if (string.IsNullOrEmpty(options.BootstrapDll))
            {
                options.BootstrapDll = Path.Combine(scriptDir, "Drelay.Bootstrap.dll");
            }
            if (string.IsNullOrEmpty(options.RunRoot))
            {
                options.RunRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "drelay", "hook");
            }
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

    internal static class Program
    {
        private static int Main(string[] args)
        {
            Options options;
            string error;
            if (!Options.TryParse(args, out options, out error))
            {
                if (error != "help")
                {
                    Console.Error.WriteLine("error: " + error);
                }
                Usage();
                return 2;
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

            // The environment is built, not inherited.  This is the one place the
            // DOTNET_TieredCompilation hazard from HOOK-ARCHITECTURE.md section 1
            // can be closed, and closing it here is why the launcher exists at all
            // rather than a shell script that sets a variable and starts the game.
            var environment = BuildEnvironment(options, gameDir, bootstrap, session, options.RunDirectory);

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
        /// The controlled environment block.
        ///
        /// Every DOTNET_* variable is removed and then exactly the ones this
        /// design wants are set.  Removing them by prefix rather than by name is
        /// deliberate: the hazard is not one known variable, it is the whole
        /// namespace, and DOTNET_TieredCompilation is only the member of it that
        /// is known to break the design today.
        /// </summary>
        private static IntPtr BuildEnvironment(Options options, string gameDir, string bootstrap,
                                              uint session, string runDir)
        {
            var variables = new List<KeyValuePair<string, string>>();
            var removed = new List<string>();

            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = (string)entry.Key;
                if (key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("DRELAY_HOOK_", StringComparison.OrdinalIgnoreCase))
                {
                    removed.Add(key);
                    continue;
                }
                variables.Add(new KeyValuePair<string, string>(key, (string)entry.Value));
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
                    "DOTNET_DbgMiniDumpName", Path.Combine(runDir, "crashdump.dmp")));
                variables.Add(new KeyValuePair<string, string>("DOTNET_CreateDumpDiagnostics", "1"));
            }

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
                "DRELAY_HOOK_BLOCK", Path.Combine(runDir, "shared.block")));
            variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_DRYRUN", options.DryRun ? "1" : "0"));
            // The native side's watchdog timeout, overridable so the timeout path
            // itself is testable.  A watchdog nobody has ever seen fire is a
            // watchdog that does not work, and this one has to: it is what keeps a
            // bootstrap that never runs from being a game that never starts.
            variables.Add(new KeyValuePair<string, string>(
                "DRELAY_HOOK_TIMEOUT_MS", options.TimeoutMs.ToString(CultureInfo.InvariantCulture)));
            variables.Add(new KeyValuePair<string, string>(
                "DRELAY_HOOK_COLLECT_DELAY_MS",
                options.CollectDelayMs.ToString(CultureInfo.InvariantCulture)));
            // How long the hook records after the patch.  Clamped here as well as in
            // the hook so that a typo is refused by the launcher's own message rather
            // than silently reduced to the ceiling inside the process.
            int liveMs = options.LiveSeconds > 6 * 3600 ? 6 * 3600 : options.LiveSeconds;
            if (liveMs < 0)
            {
                liveMs = 0;
            }
            variables.Add(new KeyValuePair<string, string>(
                "DRELAY_HOOK_LIVE_MS",
                (liveMs * 1000).ToString(CultureInfo.InvariantCulture)));
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
            if (options.Off)
            {
                // With the off switch set, the startup hook is not installed at
                // all: "off" has to mean the process is untouched, and the
                // strongest form of that is not injecting managed code into it.
                variables.RemoveAll(v => v.Key == "DOTNET_STARTUP_HOOKS");
                variables.Add(new KeyValuePair<string, string>("DRELAY_HOOK_OFF", "1"));
            }

            if (options.Verbose)
            {
                Console.WriteLine("environment   : removed " + removed.Count + " variable(s):");
                foreach (string key in removed)
                {
                    Console.WriteLine("                  " + key);
                }
            }

            // A Unicode environment block is a NUL-separated, double-NUL-terminated
            // sequence of `name=value` strings, sorted case-insensitively --
            // which is what CREATE_UNICODE_ENVIRONMENT requires.
            variables.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

            var builder = new StringBuilder();
            foreach (var pair in variables)
            {
                builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
            }
            builder.Append('\0');

            return Marshal.StringToHGlobalUni(builder.ToString());
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

  --game PATH          the game directory or the executable itself (required)
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
  --collect-delay MS   how long the hook waits before it starts watching the
                       compilation stream, so the operator can get past the menu
                       and into gameplay (default 30000; 0 starts immediately)
  --live-seconds S     how long the hook records after it patches the frame tick,
                       in seconds (default 5). The evidence files are written when
                       this window closes, so an endurance run is
                       `--live-seconds 3600`. After the window the frame read is
                       paused; set DRELAY_KEEP_READING=1 in this shell to keep it
                       open, which is the behaviour before this switch existed.
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
