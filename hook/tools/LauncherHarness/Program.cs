// Program.cs -- the launcher's offline harness.
//
// WHAT THIS PROGRAM IS FOR, IN ONE SENTENCE: it proves the launcher's configuration behaviour --
// the four sources, their precedence, the file, the discovery walk and the environment it builds
// -- without a game, a Steam install, a registry or a console.
//
// THE INVARIANTS IT CHECKS ARE HANDOFF-LAUNCH-CONFIG.md SECTION 13'S TABLE, BY NAME:
//
//   C1  a config field never overrides a value typed on the command line
//   C2  an unset field is absent from the child's environment, not set to the default
//   C3  a typo in AutoAim refuses and names the field
//   C4  discovery never returns a directory containing the OpenGL assembly
//   C5  a zero-argument run with redirected input never pauses
//   C6  the written template contains no test-harness key
//   C7  every config field has a flag, and every user-facing flag a field
//
// plus the boundary cases section 11 asks for by step.  Each check prints one line; a failure
// prints what it expected and what it found, and the process exits 1.
//
// THE CODE UNDER TEST IS THE SHIPPED CODE.  Every source file compiled here is the launcher's own
// (`Compile Include` in the csproj), so this program cannot agree with a copy of the rules while
// disagreeing with the rules.  Two things are read as *source* rather than executed, and both say
// so where they do it: `Program.Main`'s pause rule (C5) and, when given
// `--known-build`, the pinned record the generated table comes from.
//
// Usage:
//   LauncherHarness                       run every check
//   LauncherHarness --check c4            run one
//   LauncherHarness --source PATH         also check Program.cs's pause rule (C5)
//   LauncherHarness --known-build PATH    also check the pinned record has what discovery needs
//
// Exit codes: 0 = every check passed, 1 = a check failed, 2 = usage or an internal error.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Drelay.Launcher;

namespace Drelay.LauncherHarness
{
    internal sealed class CheckException : Exception
    {
        public CheckException(string message) : base(message) { }
    }

    internal static class Program
    {
        private static readonly List<string> Failures = new List<string>();
        private static readonly List<string> Ran = new List<string>();
        private static string _tmp;
        private static string _programSource;
        private static string _knownBuild;

        private static int Main(string[] args)
        {
            string only = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--check":
                        if (i + 1 >= args.Length) { Usage(); return 2; }
                        only = args[++i];
                        break;
                    case "--source":
                        if (i + 1 >= args.Length) { Usage(); return 2; }
                        _programSource = args[++i];
                        break;
                    case "--known-build":
                        if (i + 1 >= args.Length) { Usage(); return 2; }
                        _knownBuild = args[++i];
                        break;
                    case "--print-template":
                        // WHAT A FIRST RUN WILL ACTUALLY WRITE, without starting a game.  The
                        // template is the release's default behaviour (§16.2), so being able to read
                        // it from the build is worth one option.
                        Console.Write(ConfigFile.Template());
                        return 0;
                    case "--help":
                    case "-h":
                        Usage();
                        return 0;
                    default:
                        Console.Error.WriteLine("unknown option: " + args[i]);
                        Usage();
                        return 2;
                }
            }

            try
            {
                _tmp = MakeTemp();
            }
            catch (CheckException ex)
            {
                Console.Error.WriteLine("FAILED: " + ex.Message);
                return 2;
            }

            var checks = new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("c1 per-field precedence", C1),
                new KeyValuePair<string, Action>("c2 the child's environment", C2),
                new KeyValuePair<string, Action>("c3 a typo names its field", C3),
                new KeyValuePair<string, Action>("c4 discovery and the OpenGL gate", C4),
                new KeyValuePair<string, Action>("c5 the pause rule", C5),
                new KeyValuePair<string, Action>("c6 the first-run template", C6),
                new KeyValuePair<string, Action>("c7 config fields and flags, both directions", C7),
                new KeyValuePair<string, Action>("c8 the pinned record discovery needs", C8),
                new KeyValuePair<string, Action>("r1 the config reader's boundaries", R1),
                new KeyValuePair<string, Action>("r2 the command line's boundaries", R2),
            };

            foreach (var check in checks)
            {
                if (only != null && !check.Key.StartsWith(only, StringComparison.OrdinalIgnoreCase)) { continue; }
                Ran.Add(check.Key);
                try
                {
                    check.Value();
                    Console.WriteLine("ok   " + check.Key);
                }
                catch (CheckException ex)
                {
                    Failures.Add(check.Key);
                    Console.WriteLine("FAIL " + check.Key);
                    Console.WriteLine("       " + ex.Message);
                }
                catch (Exception ex)
                {
                    Failures.Add(check.Key);
                    Console.WriteLine("FAIL " + check.Key);
                    Console.WriteLine("       " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            try { Directory.Delete(_tmp, true); } catch (Exception) { }

            Console.WriteLine();
            if (Ran.Count == 0)
            {
                Console.Error.WriteLine("no check matched '" + only + "'");
                return 2;
            }
            if (Failures.Count > 0)
            {
                Console.Error.WriteLine("LAUNCHER CHECKS FAILED: " + string.Join(", ", Failures.ToArray()));
                return 1;
            }
            Console.WriteLine("LAUNCHER CHECKS OK -- " + Ran.Count + " check(s)");
            return 0;
        }

        /// <summary>
        /// A directory this process may write in.
        ///
        /// TWO CANDIDATES, BECAUSE %TEMP% IS NOT ALWAYS WRITABLE.  A process started from the DSH
        /// agent harness is confined to the repository, so `Path.GetTempPath()` -- which is under
        /// `%LOCALAPPDATA%` -- fails with ERROR_ACCESS_DENIED before a single check has run.  That
        /// is a property of where this program is started from rather than of the launcher, and it
        /// is the same quirk the README records for `--runroot`.  The fallback keeps the checks
        /// runnable from either place.
        /// </summary>
        private static string MakeTemp()
        {
            string name = "drelay-launcher-harness-" + Guid.NewGuid().ToString("N");
            var candidates = new List<string>
            {
                Path.Combine(Path.GetTempPath(), name),
                Path.Combine(AppContext.BaseDirectory, "harness-tmp", name),
            };
            foreach (string candidate in candidates)
            {
                try
                {
                    Directory.CreateDirectory(candidate);
                    return candidate;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
            throw new CheckException("no writable directory for the harness's temporary files");
        }

        private static void Usage()
        {            Console.WriteLine(@"LauncherHarness -- the launcher's configuration, offline.

  --check NAME     run only the checks whose name starts with NAME (c1..c8, r1, r2)
  --source PATH    Program.cs, so C5 can assert Main's single pause is behind FirstRun.ShouldPause
  --known-build PATH  hook/known-build.json, so C8 can assert the record carries the app id and
                      the OpenGL assembly size that discovery names the variant with
  --print-template print the configuration file a first run writes, and exit
  --help           this text");
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private static void Expect(bool condition, string what)
        {
            if (!condition) { throw new CheckException(what); }
        }

        private static void ExpectEqual(string expected, string actual, string what)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new CheckException(what + ": expected '" + expected + "', got '" + actual + "'");
            }
        }

        private static void ExpectEqual(int expected, int actual, string what)
        {
            if (expected != actual)
            {
                throw new CheckException(what + ": expected " + expected + ", got " + actual);
            }
        }

        private static void ExpectEqual(double expected, double actual, string what)
        {
            if (Math.Abs(expected - actual) > 1e-9)
            {
                throw new CheckException(what + ": expected " + expected + ", got " + actual);
            }
        }

        private static Settings Empty()
        {
            return new Settings();
        }

        /// <summary>The command line as a `Settings`, refusing to continue if it will not parse.</summary>
        private static Settings Cli(params string[] args)
        {
            List<string> gameArguments;
            return Cli(args, out gameArguments);
        }

        private static Settings Cli(string[] args, out List<string> gameArguments)
        {
            Settings settings;
            bool help;
            string error;
            if (!CommandLine.TryParse(args, out settings, out gameArguments, out help, out error))
            {
                throw new CheckException("the command line [" + string.Join(" ", args) + "] was refused: " + error);
            }
            return settings;
        }

        /// <summary>Write a config file and read it back through the shipped reader.</summary>
        private static Settings Config(string json, out string path, out List<string> recognised)
        {
            path = Path.Combine(_tmp, "config-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            Settings settings;
            string error;
            if (!ConfigFile.TryRead(path, out settings, out recognised, out error))
            {
                throw new CheckException("the config was refused: " + error);
            }
            return settings;
        }

        private static Settings Config(string json)
        {
            string path;
            List<string> recognised;
            return Config(json, out path, out recognised);
        }

        /// <summary>Read a config file and return the refusal, or null when it was accepted.</summary>
        private static string ConfigError(string json, out string path)
        {
            path = Path.Combine(_tmp, "config-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            Settings settings;
            List<string> recognised;
            string error;
            if (ConfigFile.TryRead(path, out settings, out recognised, out error)) { return null; }
            return error;
        }

        private static Options Resolve(Settings config, Settings cli, IDiscoveryEnv env)
        {
            ResolveResult result = Configuration.Resolve(
                config, cli, new List<string>(), env ?? new FakeEnv(), Pinned(), @"C:\app");
            if (result.Outcome != ResolveOutcome.Ready)
            {
                throw new CheckException("expected a usable configuration, got " + result.Outcome
                                         + (result.Error != null ? ": " + result.Error : string.Empty));
            }
            return result.Options;
        }

        private static ResolveResult ResolveRaw(Settings config, Settings cli, IDiscoveryEnv env)
        {
            return Configuration.Resolve(
                config, cli, new List<string>(), env ?? new FakeEnv(), Pinned(), @"C:\app");
        }

        /// <summary>The pinned build the harness tests against -- deliberately not the real one.</summary>
        private static PinnedBuild Pinned()
        {
            return new PinnedBuild
            {
                RecordedFrom = @"E:\SteamLibrary\steamapps\common\Darza's Dominion",
                ExecutableFileName = "DarzasDominion.exe",
                AssemblyFileName = "DarzasDominion.dll",
                AssemblySize = 1000,
                AssemblySizeOpenGlVariant = 2000,
                SteamAppId = 1895130,
            };
        }

        private static List<KeyValuePair<string, string>> Env(Options options, params string[] inherited)
        {
            var parent = new List<KeyValuePair<string, string>>();
            foreach (string pair in inherited)
            {
                int eq = pair.IndexOf('=');
                parent.Add(new KeyValuePair<string, string>(pair.Substring(0, eq), pair.Substring(eq + 1)));
            }
            return EnvironmentBlock.Build(options, @"C:\game", @"C:\app\Drelay.Bootstrap.dll",
                                          4242, @"C:\runs\now", parent, null);
        }

        private static string Get(List<KeyValuePair<string, string>> variables, string name)
        {
            foreach (var pair in variables)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) { return pair.Value; }
            }
            return null;
        }

        private static bool Has(List<KeyValuePair<string, string>> variables, string name)
        {
            return Get(variables, name) != null;
        }

        // ------------------------------------------------------------------
        // the tables
        // ------------------------------------------------------------------

        /// <summary>
        /// Where each config field ends up.  `null` means "the launcher consumes it and the child
        /// sees no variable", which is a decision, not an omission -- and C7 asserts that every
        /// field of the schema appears here, so a new field has to make that decision.
        /// </summary>
        private static readonly (string Key, string Env)[] FieldEnv =
        {
            ("GamePath", null),
            ("AutoAim", "DRELAY_AIM_MODE"),
            ("Dodge.Enabled", "DRELAY_DODGE"),
            ("Dodge.Act", "DRELAY_DODGE_ACT"),
            ("Dodge.DecisionDisc", "DRELAY_DODGE_DECISION_DISC"),
            ("Dodge.WindowSeconds", "DRELAY_DODGE_WINDOW_S"),
            ("CollectDelayMs", "DRELAY_HOOK_COLLECT_DELAY_MS"),
            ("DiscoveryWindowMs", "DRELAY_HOOK_DISCOVERY_WINDOW_MS"),
            ("ResolveDelayMs", "DRELAY_HOOK_RESOLVE_DELAY_MS"),
            ("LiveSeconds", "DRELAY_HOOK_LIVE_MS"),
            ("PauseAfterRun", "DRELAY_KEEP_READING"),
            ("RunRoot", null),
            ("DbgDump", "DOTNET_DbgEnableMiniDump"),
            ("Verbose", null),
            ("NoWait", null),
            ("TimeoutMs", "DRELAY_HOOK_TIMEOUT_MS"),
            ("Hook", null),
            ("Bootstrap", null),
        };

        /// <summary>A value the flag accepts, or `null` for a switch.  Used to prove each flag is real.</summary>
        private static readonly (string Flag, string Sample)[] FlagSample =
        {
            ("--game", @"C:\g"),
            ("--autoaim", "closest"),
            ("--dodge", null),
            ("--dodge-act", null),
            ("--dodge-disc", "2"),
            ("--dodge-window", "1"),
            ("--collect-delay", "1"),
            ("--discovery-ms", "1"),
            ("--resolve-delay", "1"),
            ("--live-seconds", "1"),
            ("--pause-after-run", null),
            ("--runroot", @"C:\r"),
            ("--dbg-dump", null),
            ("--verbose", null),
            ("--no-wait", null),
            ("--timeout", "1"),
            ("--hook", @"C:\h.dll"),
            ("--bootstrap", @"C:\b.dll"),
        };

        /// <summary>Every key of the schema, set, with a value that is not the default.</summary>
        private static string FullConfigJson()
        {
            return @"{
  ""GamePath"": ""C:\\cfg-game"",
  ""AutoAim"": ""closest"",
  ""Dodge"": { ""Enabled"": true, ""Act"": true, ""DecisionDisc"": 2.5, ""WindowSeconds"": 0.25 },
  ""CollectDelayMs"": 111,
  ""DiscoveryWindowMs"": 222,
  ""ResolveDelayMs"": 33,
  ""LiveSeconds"": 7,
  ""PauseAfterRun"": true,
  ""RunRoot"": ""C:\\cfg-runs"",
  ""DbgDump"": true,
  ""Verbose"": true,
  ""NoWait"": true,
  ""TimeoutMs"": 12345,
  ""Hook"": ""C:\\app\\h.dll"",
  ""Bootstrap"": ""C:\\app\\b.dll""
}";
        }

        // ------------------------------------------------------------------
        // C1 -- per-field precedence
        // ------------------------------------------------------------------

        private static void C1()
        {
            var none = Resolve(Empty(), Cli("--game", @"C:\cli-game"), new FakeEnv());

            // Nothing supplied: the compiled defaults are what is left, and the sentinels still
            // mean "not set" rather than "the solver's default is written down here too".
            ExpectEqual(10000, none.DiscoveryWindowMs, "the default discovery window");
            ExpectEqual(100, none.ResolveDelayMs, "the default resolve margin");
            ExpectEqual(5, none.LiveSeconds, "the default evidence window");
            Expect(none.DryRun, "the default is a dry run");
            Expect(none.DodgeDisc < 0.0 && none.DodgeWindowSeconds < 0.0,
                   "an unset dodge window must stay 'not set', not carry a copy of the solver's default");
            Expect(none.CollectDelayMs == 0 && !none.SetCollectDelayMs,
                   "an unset collect delay is still 'not set'");

            string ignored;
            List<string> recognised;
            Settings config = Config(@"{
  ""GamePath"": ""C:\\cfg-game"",
  ""AutoAim"": ""closest"",
  ""Dodge"": { ""Enabled"": true, ""DecisionDisc"": 3.0 },
  ""CollectDelayMs"": 2500,
  ""DiscoveryWindowMs"": 12000
}", out ignored, out recognised);

            // Config only.
            var onlyConfig = Resolve(config, Empty(), new FakeEnv());
            ExpectEqual(@"C:\cfg-game", onlyConfig.GamePath, "GamePath from the config file");
            ExpectEqual("closest", onlyConfig.AutoAim, "AutoAim from the config file");
            Expect(onlyConfig.Dodge, "Dodge.Enabled from the config file");
            ExpectEqual(3.0, onlyConfig.DodgeDisc, "Dodge.DecisionDisc from the config file");
            ExpectEqual(2500, onlyConfig.CollectDelayMs, "CollectDelayMs from the config file");
            ExpectEqual(12000, onlyConfig.DiscoveryWindowMs, "DiscoveryWindowMs from the config file");
            Expect(onlyConfig.FrameReplacement,
                   "an AutoAim from the CONFIG FILE must imply the frame replacement exactly as the flag does (section 10.6)");

            // Command line only.
            var onlyCli = Resolve(Empty(), Cli("--game", @"D:\cli-game", "--autoaim", "highesthp"), new FakeEnv());
            ExpectEqual(@"D:\cli-game", onlyCli.GamePath, "GamePath from the command line");
            ExpectEqual("highesthp", onlyCli.AutoAim, "AutoAim from the command line");

            // Both, and the CLI says one thing only.  This is C1.
            var both = Resolve(config, Cli("--autoaim", "highesthp"), new FakeEnv());
            ExpectEqual("highesthp", both.AutoAim, "the command line wins for the field it sets");
            ExpectEqual(@"C:\cfg-game", both.GamePath,
                        "a command-line value for one field must not discard a config value for another");
            Expect(both.Dodge, "the config's Dodge.Enabled survives a command line that sets AutoAim");
            ExpectEqual(3.0, both.DodgeDisc, "the config's dodge window survives too");
            ExpectEqual(2500, both.CollectDelayMs, "the config's collect delay survives too");
            ExpectEqual(12000, both.DiscoveryWindowMs, "a field neither source set keeps the default");

            // And the other direction: the CLI overrides exactly one field of a config that sets many.
            var overridden = Resolve(config, Cli("--collect-delay", "10"), new FakeEnv());
            ExpectEqual(10, overridden.CollectDelayMs, "the command line overrides the one field it sets");
            ExpectEqual(12000, overridden.DiscoveryWindowMs, "and leaves the neighbouring field alone");
            ExpectEqual("closest", overridden.AutoAim, "and leaves a different type alone");

            // AutoAim null in the config is "off", which is the same statement as absence.
            Settings off = Config(@"{ ""AutoAim"": null }");
            Expect(off.AutoAim == null, "a null AutoAim must mean 'not set', not a mode");
            var withNull = Resolve(Config(@"{ ""GamePath"": ""C:\\g"", ""AutoAim"": null }"), Empty(), new FakeEnv());
            Expect(withNull.AutoAim == null, "a null AutoAim must leave the hook's own default in charge");
            Expect(!withNull.FrameReplacement, "and must not turn the frame replacement on");
        }

        // ------------------------------------------------------------------
        // C2 -- the child's environment
        // ------------------------------------------------------------------

        private static void C2()
        {
            var minimal = Resolve(Empty(), Cli("--game", @"C:\g"), new FakeEnv());
            var vars = Env(minimal, "DOTNET_TieredCompilation=1", "DRELAY_HOOK_EVIL=1", "PATH=C:\\Windows");

            string[] neverWritten =
            {
                "DRELAY_HOOK_COLLECT_DELAY_MS", "DRELAY_HOOK_DISCOVERY_WINDOW_MS",
                "DRELAY_HOOK_RESOLVE_DELAY_MS", "DRELAY_HOOK_LIVE_MS", "DRELAY_HOOK_TIMEOUT_MS",
                "DRELAY_KEEP_READING", "DRELAY_HOOK_FRAME_REPLACEMENT", "DRELAY_HOOK_FRAME_HELPER",
                "DRELAY_HOOK_AIM_REPLACEMENT", "DRELAY_AIM_MODE", "DRELAY_DODGE", "DRELAY_DODGE_ACT",
                "DRELAY_HOOK_MOVE_REPLACEMENT", "DRELAY_MOVE_STEER", "DRELAY_HOOK_TEST_TARGETS",
                "DRELAY_HOOK_FULLTRACE", "DRELAY_TRACE", "DRELAY_FRAME_SENSOR", "DRELAY_HOOK_OFF",
                "DOTNET_DbgEnableMiniDump",
            };
            foreach (string name in neverWritten)
            {
                Expect(!Has(vars, name), "a field nobody set must reach the child as NOTHING: " + name);
            }

            string[] alwaysWritten =
            {
                "DOTNET_STARTUP_HOOKS", "DRELAY_HOOK_SESSION", "DRELAY_HOOK_RUNDIR",
                "DRELAY_HOOK_GAMEDIR", "DRELAY_HOOK_BLOCK", "DRELAY_HOOK_DRYRUN",
            };
            foreach (string name in alwaysWritten)
            {
                Expect(Has(vars, name), "the launcher's own name must always be written: " + name);
            }

            Expect(!Has(vars, "DOTNET_TieredCompilation"),
                   "the inherited DOTNET_ namespace must be deleted, not passed through");
            Expect(!Has(vars, "DRELAY_HOOK_EVIL"),
                   "the inherited DRELAY_HOOK_ namespace must be deleted, not passed through");
            ExpectEqual("1", Get(vars, "DRELAY_HOOK_DRYRUN"),
                        "an unset --act must leave DRELAY_HOOK_DRYRUN=1 (its absence means ACT)");

            // Every field of the schema reaches the variable it claims to.
            var full = Resolve(Config(FullConfigJson()), Empty(), new FakeEnv());
            var fullVars = Env(full);
            foreach (var row in FieldEnv)
            {
                if (row.Env == null) { continue; }
                Expect(Has(fullVars, row.Env),
                       "the config field " + row.Key + " must reach " + row.Env);
            }
            ExpectEqual("closest", Get(fullVars, "DRELAY_AIM_MODE"), "AutoAim reaches DRELAY_AIM_MODE");
            ExpectEqual("1", Get(fullVars, "DRELAY_DODGE"), "Dodge.Enabled reaches DRELAY_DODGE");
            ExpectEqual("111", Get(fullVars, "DRELAY_HOOK_COLLECT_DELAY_MS"), "CollectDelayMs");
            ExpectEqual("222", Get(fullVars, "DRELAY_HOOK_DISCOVERY_WINDOW_MS"), "DiscoveryWindowMs");
            ExpectEqual("33", Get(fullVars, "DRELAY_HOOK_RESOLVE_DELAY_MS"), "ResolveDelayMs");
            ExpectEqual("7000", Get(fullVars, "DRELAY_HOOK_LIVE_MS"), "LiveSeconds is converted to ms");
            ExpectEqual("0", Get(fullVars, "DRELAY_KEEP_READING"), "PauseAfterRun writes the inverse switch");
            ExpectEqual("12345", Get(fullVars, "DRELAY_HOOK_TIMEOUT_MS"), "TimeoutMs");
            ExpectEqual("2.5", Get(fullVars, "DRELAY_DODGE_DECISION_DISC"), "Dodge.DecisionDisc");
            ExpectEqual("0.25", Get(fullVars, "DRELAY_DODGE_WINDOW_S"), "Dodge.WindowSeconds");
            ExpectEqual("1", Get(fullVars, "DOTNET_DbgEnableMiniDump"), "DbgDump");

            // The fields with no variable are still read, and land on the launcher.
            ExpectEqual(@"C:\cfg-runs", full.RunRoot, "RunRoot");
            ExpectEqual(@"C:\app\h.dll", full.HookDll, "Hook");
            ExpectEqual(@"C:\app\b.dll", full.BootstrapDll, "Bootstrap");
            Expect(full.Verbose, "Verbose");
            Expect(full.NoWait, "NoWait");
            ExpectEqual(@"C:\cfg-game", full.GamePath, "GamePath");

            // Section 5's note: RunRoot is in the schema and not in the template, and if it is
            // supplied it is made absolute -- a relative run root is a directory created wherever
            // the launcher happened to start from.
            var relativeRunRoot = Resolve(
                Config(@"{ ""GamePath"": ""C:\\g"", ""RunRoot"": ""relative-runs"" }"), Empty(), new FakeEnv());
            Expect(Path.IsPathRooted(relativeRunRoot.RunRoot),
                   "a relative RunRoot must be resolved to an absolute path, got: " + relativeRunRoot.RunRoot);

            // The dodge's two windows travel only when they were set, and the sentinel is the
            // reason: the solver's own default must not be written down here.
            var dodgeNoWindow = Resolve(
                Config(@"{ ""GamePath"": ""C:\\g"", ""Dodge"": { ""Enabled"": true } }"), Empty(), new FakeEnv());
            var dodgeVars = Env(dodgeNoWindow);
            Expect(Has(dodgeVars, "DRELAY_DODGE"), "Dodge on: the solver runs");
            Expect(!Has(dodgeVars, "DRELAY_DODGE_DECISION_DISC"),
                   "a dodge with no --dodge-disc must not send a disc window at all");
            Expect(!Has(dodgeVars, "DRELAY_DODGE_WINDOW_S"),
                   "a dodge with no --dodge-window must not send a floor at all");
        }

        // ------------------------------------------------------------------
        // C3 -- a typo names its field, in whichever source it was typed
        // ------------------------------------------------------------------

        private static void C3()
        {
            Settings settings;
            List<string> gameArguments;
            bool help;
            string error;

            bool ok = CommandLine.TryParse(new[] { "--autoaim", "closests" }, out settings,
                                           out gameArguments, out help, out error);
            Expect(!ok, "a misspelled --autoaim must be refused");
            Expect(error != null && error.Contains("--autoaim") && error.Contains("closests"),
                   "the refusal must name the flag and the value, got: " + error);

            ok = CommandLine.TryParse(new[] { "--autoaim=closests" }, out settings,
                                      out gameArguments, out help, out error);
            Expect(!ok, "the --flag=value spelling must be refused too");
            Expect(error != null && error.Contains("--autoaim") && error.Contains("closests"),
                   "and must name the flag the same way, got: " + error);

            string path;
            string configError = ConfigError(@"{ ""AutoAim"": ""closests"" }", out path);
            Expect(configError != null, "a misspelled AutoAim in the config file must be refused");
            Expect(configError.Contains("AutoAim"), "the refusal must name the field, got: " + configError);
            Expect(configError.Contains("closests"), "and the value, got: " + configError);
            Expect(configError.Contains(path), "and the file, got: " + configError);
        }

        // ------------------------------------------------------------------
        // C4 -- discovery, and the gate that decides
        // ------------------------------------------------------------------

        private static void C4()
        {
            // 1. No Steam, no pinned path: the launcher's own directory.
            var local = new FakeEnv { LauncherDirectory = @"C:\unpacked\hook" };
            Game(local, @"C:\unpacked\hook", 1000);
            var found = Discovery.Find(local, Pinned());
            ExpectEqual(@"C:\unpacked\hook", found.GameDirectory, "the launcher's own directory is probe 3");

            // 2. Steam, one library, the game where Steam puts it.
            var steam = new FakeEnv { LauncherDirectory = @"C:\app" };
            steam.Registry(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", @"C:\Steam");
            steam.Text(@"C:\Steam\steamapps\libraryfolders.vdf", Vdf(@"C:\Steam"));
            Game(steam, @"C:\Steam\steamapps\common\Darza's Dominion", 1000);
            found = Discovery.Find(steam, Pinned());
            ExpectEqual(@"C:\Steam\steamapps\common\Darza's Dominion", found.GameDirectory,
                        "the Steam library is probe 1, and it is the first one that is right");

            // 3. The game on a SECOND library, named by the app manifest: the app id is used for
            //    the manifest, and the manifest's installdir beats the pinned directory name.
            var second = new FakeEnv { LauncherDirectory = @"C:\app" };
            second.Registry(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", @"C:\Steam");
            second.Text(@"C:\Steam\steamapps\libraryfolders.vdf", Vdf(@"C:\Steam", @"D:\Games\SteamLibrary"));
            second.Text(@"D:\Games\SteamLibrary\steamapps\appmanifest_1895130.acf",
                        "\"AppState\"\n{\n\t\"appid\"\t\t\"1895130\"\n\t\"installdir\"\t\t\"Darza Renamed\"\n}\n");
            Game(second, @"D:\Games\SteamLibrary\steamapps\common\Darza Renamed", 1000);
            found = Discovery.Find(second, Pinned());
            ExpectEqual(@"D:\Games\SteamLibrary\steamapps\common\Darza Renamed", found.GameDirectory,
                        "a game on a second Steam library must be found, by its own manifest's installdir");

            // 4. A Steam install with no libraryfolders.vdf: the registry path is itself a library.
            var noVdf = new FakeEnv { LauncherDirectory = @"C:\app" };
            noVdf.Registry(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", @"C:\Steam");
            Game(noVdf, @"C:\Steam\steamapps\common\Darza's Dominion", 1000);
            found = Discovery.Find(noVdf, Pinned());
            ExpectEqual(@"C:\Steam\steamapps\common\Darza's Dominion", found.GameDirectory,
                        "with no library list, the registry's SteamPath is the library");

            // 5. No Steam at all: the pinned record.
            var pinnedOnly = new FakeEnv { LauncherDirectory = @"C:\app" };
            Game(pinnedOnly, @"E:\SteamLibrary\steamapps\common\Darza's Dominion", 1000);
            found = Discovery.Find(pinnedOnly, Pinned());
            ExpectEqual(@"E:\SteamLibrary\steamapps\common\Darza's Dominion", found.GameDirectory,
                        "the pinned record is probe 2");

            // 6. THE INVARIANT: the OpenGL variant is never returned, even when the right build is
            //    one library further along -- the mismatch is NAMED instead of being skipped.
            var openGl = new FakeEnv { LauncherDirectory = @"C:\app" };
            openGl.Registry(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", @"C:\Steam");
            openGl.Text(@"C:\Steam\steamapps\libraryfolders.vdf", Vdf(@"C:\Steam", @"D:\Games\SteamLibrary"));
            Game(openGl, @"C:\Steam\steamapps\common\Darza's Dominion", 2000);
            Game(openGl, @"D:\Games\SteamLibrary\steamapps\common\Darza's Dominion", 1000);
            var result = Discovery.Find(openGl, Pinned());
            Expect(result.GameDirectory == null, "discovery must NOT return the OpenGL build");
            Expect(result.OpenGlVariant, "the OpenGL variant must be flagged as such");
            Expect(result.Failure != null && result.Failure.Contains("OpenGL"),
                   "and named in the message, got: " + result.Failure);

            // 7. An unknown build is a mismatch too, and it is reported rather than accepted.
            var wrong = new FakeEnv { LauncherDirectory = @"C:\app" };
            Game(wrong, @"E:\SteamLibrary\steamapps\common\Darza's Dominion", 999);
            result = Discovery.Find(wrong, Pinned());
            Expect(result.GameDirectory == null, "a directory whose assembly is not the pinned build is not the game");
            Expect(!result.OpenGlVariant, "and is not claimed to be the OpenGL variant either");
            Expect(result.Failure != null && result.Failure.Contains("999"),
                   "the mismatch must name the size it found, got: " + result.Failure);

            // 8. Nothing anywhere.
            result = Discovery.Find(new FakeEnv { LauncherDirectory = @"C:\app" }, Pinned());
            Expect(result.GameDirectory == null && result.Failure != null,
                   "finding nothing must be a refusal with a reason, not a silent null");

            // 9. The scanner that reads Steam's own file format, against a nested block -- the
            //    shape that breaks a naive key/value pairing.
            List<string> paths = Discovery.ParseQuotedValue(Vdf(@"C:\Steam", @"D:\Games\SteamLibrary"), "path");
            ExpectEqual(2, paths.Count, "both library paths must come out of the VDF");
            ExpectEqual(@"C:\Steam", paths[0], "the first library path, unescaped");
            ExpectEqual(@"D:\Games\SteamLibrary", paths[1], "the second library path, unescaped");
        }

        /// <summary>A libraryfolders.vdf with the nested-block shape Steam writes.</summary>
        private static string Vdf(params string[] libraries)
        {
            var text = new StringBuilder();
            text.Append("\"libraryfolders\"\r\n{\r\n");
            for (int i = 0; i < libraries.Length; i++)
            {
                text.Append("\t\"").Append(i).Append("\"\r\n\t{\r\n");
                text.Append("\t\t\"path\"\t\t\"").Append(libraries[i].Replace("\\", "\\\\")).Append("\"\r\n");
                text.Append("\t\t\"label\"\t\t\"\"\r\n");
                text.Append("\t\t\"apps\"\r\n\t\t{\r\n\t\t\t\"1895130\"\t\t\"123456\"\r\n\t\t}\r\n");
                text.Append("\t}\r\n");
            }
            text.Append("}\r\n");
            return text.ToString();
        }

        /// <summary>A directory holding the pinned apphost and an assembly of the given size.</summary>
        private static void Game(FakeEnv env, string directory, long assemblySize)
        {
            env.Dir(directory);
            env.File(Path.Combine(directory, "DarzasDominion.exe"), 4096);
            env.File(Path.Combine(directory, "DarzasDominion.dll"), assemblySize);
        }

        // ------------------------------------------------------------------
        // C5 -- the pause rule
        // ------------------------------------------------------------------

        private static void C5()
        {
            Expect(FirstRun.ShouldPause(true, false, true),
                   "a double-click with a fixable failure must hold the window open");
            Expect(!FirstRun.ShouldPause(true, true, true),
                   "C5: a zero-argument run with redirected input must NEVER pause");
            Expect(!FirstRun.ShouldPause(false, false, true),
                   "a scripted run (one or more arguments) must never pause");
            Expect(!FirstRun.ShouldPause(true, false, false),
                   "a refusal the user cannot fix by editing a file must not pause");

            // The other first-run rule: a no-argument run that finds no configuration file creates
            // one, because section 6's "write it only when discovery fails" is the one case where
            // the user who needs the file (the one whose game was found) never gets it.
            Expect(FirstRun.ShouldWriteTemplate(true, false),
                   "a first no-argument run must write the configuration template");
            Expect(!FirstRun.ShouldWriteTemplate(true, true),
                   "and must never write over a file that exists -- that file may be the operator's");
            Expect(!FirstRun.ShouldWriteTemplate(false, false),
                   "a scripted run must not create files it did not ask for");

            if (_programSource == null)
            {
                Console.WriteLine("       (Program.cs not given: the 'which line does Main call' half of");
                Console.WriteLine("        C5 is not checked. Pass --source to check it.)");
                return;
            }
            Expect(File.Exists(_programSource), "Program.cs not found at " + _programSource);
            string source = File.ReadAllText(_programSource);

            int readKeys = Count(source, "Console.ReadKey");
            ExpectEqual(1, readKeys,
                        "Main must have exactly one pause; a second one is a path that can block a script");

            int guarded = Count(source, "FirstRun.ShouldPause(");
            ExpectEqual(1, guarded,
                        "the one pause must be decided by a single FirstRun.ShouldPause call");

            int redirectChecks = Count(source, "Console.IsInputRedirected");
            ExpectEqual(1, redirectChecks,
                        "the redirected-input check must live in the one call site, not be re-derived");

            // And the same for the other first-run decision: exactly one call, so a future edit
            // cannot add a second writer (or leave the template unwritten).
            int writers = Count(source, "FirstRun.ShouldWriteTemplate(");
            ExpectEqual(1, writers,
                        "the template must be written from exactly one place, behind FirstRun.ShouldWriteTemplate");
        }

        private static int Count(string haystack, string needle)
        {
            int count = 0;
            int at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }

        // ------------------------------------------------------------------
        // C6 -- the template
        // ------------------------------------------------------------------

        private static void C6()
        {
            string template = ConfigFile.Template();
            Expect(template.TrimStart().StartsWith("{"), "the template must be a JSON object");

            // It must be readable by the reader that wrote it -- comments included.
            string path = Path.Combine(_tmp, "template.json");
            File.WriteAllText(path, template, new UTF8Encoding(false));
            Settings settings;
            List<string> recognised;
            string error;
            Expect(ConfigFile.TryRead(path, out settings, out recognised, out error),
                   "the written template must be readable by the launcher: " + error);

            // C6 itself: no test-harness key, by string search.
            string[] forbidden =
            {
                "FrameHelper", "FrameReplacement", "TestTargets", "MoveSteer", "EscapeTest",
                "FullTrace", "Trace", "Off", "Exe", "Assembly",
            };
            foreach (string key in forbidden)
            {
                Expect(!template.Contains("\"" + key + "\""),
                       "the template must not offer the test-harness key \"" + key + "\"");
            }

            // And the same statement structurally: the template sets exactly the documented keys.
            var expected = new List<string>
            {
                "GamePath", "AutoAim", "Dodge", "Dodge.Enabled", "Dodge.Act", "Dodge.DecisionDisc",
                "Dodge.WindowSeconds", "CollectDelayMs", "DiscoveryWindowMs", "LiveSeconds",
                "PauseAfterRun",
            };
            var actual = new List<string>(recognised);
            expected.Sort(StringComparer.Ordinal);
            actual.Sort(StringComparer.Ordinal);
            ExpectEqual(string.Join(",", expected.ToArray()), string.Join(",", actual.ToArray()),
                        "the template's keys");

            // WHAT THE TEMPLATE SAYS, WHICH IS NOW A DECISION RATHER THAN A RESTATEMENT OF THE
            // COMPILED DEFAULTS (see `ConfigFile.Template`'s own note).  A first no-argument run
            // writes this file AND runs with it, so these three assertions are the shipped default
            // behaviour of a double-click: aim at the closest enemy, solve and steer.
            var defaults = new Options();
            ExpectEqual("closest", settings.AutoAim,
                        "the written template is the release default: auto-aim on, closest");
            Expect(settings.Dodge == true, "and the dodge on");
            Expect(settings.DodgeAct == true, "and acting, not only logging");
            Expect(settings.DodgeDisc == null, "the template must leave the disc window unset");
            Expect(settings.DodgeWindowSeconds == null, "and the floor unset");

            // The timing knobs really are the compiled defaults, from the same fields the launcher
            // uses -- a default changed in Options without changing the template fails here.
            ExpectEqual(defaults.CollectDelayMs, settings.CollectDelayMs ?? -999, "CollectDelayMs");
            ExpectEqual(defaults.DiscoveryWindowMs, settings.DiscoveryWindowMs ?? -999, "DiscoveryWindowMs");
            ExpectEqual(defaults.LiveSeconds, settings.LiveSeconds ?? -999, "LiveSeconds");
            Expect(settings.PauseAfterRun == false, "PauseAfterRun must be false in the template");
            Expect(settings.GamePath == null, "GamePath must be empty, so the first run has something to fill in");
            Expect(settings.TimeoutMs == null, "the template must not pin the timeout");

            // AND THE WHOLE POINT OF THE FILE: resolved through the shipped merge, it produces a run
            // that asks the hook for both modules, rather than the all-off run of 2026-10-16.
            Options resolved = Resolve(settings, Cli("--game", @"C:\g"), new FakeEnv());
            Expect(resolved.FrameReplacement,
                   "the written template must install the managed frame replacement -- it is the entry "
                   + "point both the dodge and auto-aim are driven from");
            var vars = Env(resolved);
            ExpectEqual("closest", Get(vars, "DRELAY_AIM_MODE"), "the template must reach DRELAY_AIM_MODE");
            ExpectEqual("1", Get(vars, "DRELAY_DODGE"), "and DRELAY_DODGE");
            ExpectEqual("1", Get(vars, "DRELAY_DODGE_ACT"), "and DRELAY_DODGE_ACT");
            ExpectEqual("dodge", Get(vars, "DRELAY_MOVE_STEER"), "and the pilot's dodge mode");
            ExpectEqual("1", Get(vars, "DRELAY_FRAME_SENSOR"), "and the sensor the dodge reads");
        }

        // ------------------------------------------------------------------
        // C7 -- the table, in both directions
        // ------------------------------------------------------------------

        private static void C7()
        {
            // Direction 1: every field of the config has a flag, and the flag is real.
            foreach (ConfigField field in ConfigFile.Schema)
            {
                bool known = false;
                foreach (var row in FlagSample)
                {
                    if (row.Flag == field.Flag) { known = true; break; }
                }
                Expect(known, "the schema claims " + field.Key + " maps to " + field.Flag
                              + ", which is not a flag this harness knows how to exercise");
            }
            foreach (var row in FlagSample)
            {
                Settings parsed;
                List<string> gameArguments;
                bool help;
                string error;
                string[] args = row.Sample == null
                    ? new[] { row.Flag, "--game", @"C:\g" }
                    : new[] { row.Flag, row.Sample, "--game", @"C:\g" };
                Expect(CommandLine.TryParse(args, out parsed, out gameArguments, out help, out error),
                       "the flag " + row.Flag + " must be accepted by the command line: " + error);
            }

            // Direction 2: every field the config reader recognises is in the table.
            string path;
            List<string> recognised;
            Config(FullConfigJson(), out path, out recognised);
            var expected = new List<string>();
            foreach (ConfigField field in ConfigFile.Schema)
            {
                expected.Add(field.Key);
                if (field.Key.StartsWith("Dodge.", StringComparison.Ordinal) && !expected.Contains("Dodge"))
                {
                    // `Dodge` itself is recognised too, and it is one key however many children it
                    // has: the reader records the parent once, when it opens the object.
                    expected.Add("Dodge");
                }
            }
            var actual = new List<string>(recognised);
            expected.Sort(StringComparer.Ordinal);
            actual.Sort(StringComparer.Ordinal);
            ExpectEqual(string.Join(",", expected.ToArray()), string.Join(",", actual.ToArray()),
                        "the keys the reader recognised on a document that sets every field");

            // And every field's destination is a decision somebody made.
            foreach (ConfigField field in ConfigFile.Schema)
            {
                bool decided = false;
                foreach (var row in FieldEnv)
                {
                    if (row.Key == field.Key) { decided = true; break; }
                }
                Expect(decided, "the config field " + field.Key
                                + " has no entry in the harness's environment table, so where it goes is undocumented");
            }
            foreach (var row in FieldEnv)
            {
                bool inSchema = false;
                foreach (ConfigField field in ConfigFile.Schema)
                {
                    if (field.Key == row.Key) { inSchema = true; break; }
                }
                Expect(inSchema, "the harness documents " + row.Key + ", which is not in the config schema");
            }
        }

        // ------------------------------------------------------------------
        // C8 -- the pinned record carries what discovery needs
        // ------------------------------------------------------------------

        private static void C8()
        {
            if (_knownBuild == null || !File.Exists(_knownBuild))
            {
                Console.WriteLine("       (known-build.json not given: skipped. Pass --known-build to check it.)");
                return;
            }
            using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(_knownBuild)))
            {
                JsonElement root = document.RootElement;
                JsonElement assembly = root.GetProperty("assembly");
                long size = assembly.GetProperty("size").GetInt64();
                Expect(assembly.TryGetProperty("sizeOpenGlVariant", out JsonElement openGl),
                       "known-build.json must record sizeOpenGlVariant: discovery NAMES the OpenGL "
                       + "install with it, and without it the refusal degrades to 'not the pinned build'");
                long openGlSize = openGl.GetInt64();
                Expect(openGlSize > 0, "sizeOpenGlVariant must be a real size, not 0");
                Expect(openGlSize != size,
                       "sizeOpenGlVariant must differ from size, or the gate cannot tell them apart");

                JsonElement game = root.GetProperty("game");
                Expect(game.TryGetProperty("steamAppId", out JsonElement appId),
                       "known-build.json must record game.steamAppId: discovery reads the app manifest with it");
                Expect(appId.GetInt32() > 0, "steamAppId must be a real app id, not 0");
            }
        }

        // ------------------------------------------------------------------
        // R1 -- the config reader's boundaries (section 11 step 2)
        // ------------------------------------------------------------------

        private static void R1()
        {
            Settings settings;
            List<string> recognised;
            string error;

            // A missing file is "defaults", not an error.
            string missing = Path.Combine(_tmp, "not-there.json");
            Expect(ConfigFile.TryRead(missing, out settings, out recognised, out error),
                   "a missing file must not be an error: " + error);
            Expect(settings.IsEmpty, "a missing file must leave every field unset");

            // An empty file is the same statement.
            string emptyPath = Path.Combine(_tmp, "empty.json");
            File.WriteAllText(emptyPath, "   \r\n");
            Expect(ConfigFile.TryRead(emptyPath, out settings, out recognised, out error),
                   "an empty file must not be an error: " + error);
            Expect(settings.IsEmpty, "an empty file must leave every field unset");

            // An unknown key is ignored, not refused: a config from a later version must still start.
            Settings withUnknown = Config(@"{ ""GamePath"": ""C:\\g"", ""FrameHelper"": true, ""NotAKey"": 1 }");
            ExpectEqual(@"C:\g", withUnknown.GamePath, "a known key beside an unknown one is still read");
            Expect(withUnknown.FrameHelper == null,
                   "a test-harness key must be UNKNOWN to the reader, not a hidden switch");

            // Malformed JSON names the file.
            string path;
            string malformed = ConfigError(@"{ ""GamePath"": }", out path);
            Expect(malformed != null && malformed.Contains(path),
                   "malformed JSON must name the file, got: " + malformed);

            // A wrong type names the field.
            string wrongType = ConfigError(@"{ ""LiveSeconds"": ""5"" }", out path);
            Expect(wrongType != null && wrongType.Contains("LiveSeconds"),
                   "a wrong type must name the field, got: " + wrongType);

            string negative = ConfigError(@"{ ""CollectDelayMs"": -1 }", out path);
            Expect(negative != null && negative.Contains("CollectDelayMs"),
                   "a negative number must name the field, got: " + negative);

            string badDodge = ConfigError(@"{ ""Dodge"": 3 }", out path);
            Expect(badDodge != null && badDodge.Contains("Dodge"),
                   "a Dodge that is not an object must name the field, got: " + badDodge);

            string notAnObject = ConfigError(@"[]", out path);
            Expect(notAnObject != null && notAnObject.Contains(path),
                   "a top-level array must be refused with the file named, got: " + notAnObject);

            // Trailing commas and comments are accepted, because the template has both.
            Settings commented = Config("{\n  // a comment\n  \"GamePath\": \"C:\\\\g\",\n}");
            ExpectEqual(@"C:\g", commented.GamePath, "a comment and a trailing comma must not be a startup failure");

            // The default location is where the documents say, under the given profile.
            string expectedDir = Path.Combine(@"D:\profile", "drelay", "hook");
            ExpectEqual(expectedDir, ConfigFile.Directory(@"D:\profile"), "the config directory");
            ExpectEqual(Path.Combine(expectedDir, "drelay.config.json"),
                        ConfigFile.DefaultPath(@"D:\profile"), "the config file path");

            // Writing the template, then reading it back, is the first-run round trip.
            string written = Path.Combine(_tmp, "written", "drelay.config.json");
            Expect(ConfigFile.WriteTemplate(written, out error), "the template must be writable: " + error);
            Expect(File.Exists(written), "and must exist afterwards");
            Expect(ConfigFile.WriteTemplate(written, out error), "writing it twice must be a no-op");
            File.WriteAllText(written, @"{ ""GamePath"": ""C:\\mine"" }");
            ConfigFile.WriteTemplate(written, out error);
            ExpectEqual(@"{ ""GamePath"": ""C:\\mine"" }", File.ReadAllText(written),
                        "writing the template must NEVER overwrite a file the user has");
        }

        // ------------------------------------------------------------------
        // R2 -- the command line's boundaries
        // ------------------------------------------------------------------

        private static void R2()
        {
            Settings settings;
            List<string> gameArguments;
            bool help;
            string error;

            // An unknown option is refused; a bare word is the game's.
            Expect(!CommandLine.TryParse(new[] { "--nonsense" }, out settings, out gameArguments,
                                         out help, out error),
                   "an unknown option must be refused");
            Expect(CommandLine.TryParse(new[] { "--game", @"C:\g", "-screen-width", "1920" },
                                        out settings, out gameArguments, out help, out error),
                   "an argument the launcher does not own must be passed through: " + error);
            ExpectEqual(2, gameArguments.Count, "both game arguments must be passed through");
            ExpectEqual("-screen-width", gameArguments[0], "in order");

            // The equals spelling, for the flags a script is most likely to build.
            Expect(CommandLine.TryParse(new[] { @"--game=C:\g", "--autoaim=highesthp" },
                                        out settings, out gameArguments, out help, out error),
                   "--flag=value must parse: " + error);
            ExpectEqual(@"C:\g", settings.GamePath, "--game=PATH");
            ExpectEqual("highesthp", settings.AutoAim, "--autoaim=MODE");

            // A number a user can get wrong is refused with its own name, not by an exception.
            Expect(!CommandLine.TryParse(new[] { "--game", @"C:\g", "--timeout", "abc" },
                                         out settings, out gameArguments, out help, out error),
                   "--timeout abc must be refused");
            Expect(error != null && error.Contains("--timeout"),
                   "and must name the flag, got: " + error);
            Expect(!CommandLine.TryParse(new[] { "--game", @"C:\g", "--live-seconds", "-1" },
                                         out settings, out gameArguments, out help, out error),
                   "a negative knob must be refused");

            // A flag with no value is a message, not an unhandled exception -- the harness calls the
            // parser the way `Main` does, and `Main` catches `ArgumentException` for exactly this.
            bool threw = false;
            try
            {
                CommandLine.TryParse(new[] { "--game" }, out settings, out gameArguments, out help, out error);
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Expect(threw, "a flag with no value must be reported as a failure (Main turns it into a message)");

            // The one contradiction that is refused rather than resolved.
            ResolveResult contradiction = ResolveRaw(
                new Settings(),
                Cli(new[] { "--game", @"C:\g", "--dodge-act", "--move-steer", "hold:90:8000" }, out gameArguments),
                new FakeEnv());
            Expect(contradiction.Outcome == ResolveOutcome.Invalid,
                   "--dodge-act with an explicit --move-steer must be refused, not resolved");
            Expect(contradiction.Error != null && contradiction.Error.Contains("--dodge-act"),
                   "and the refusal must name the flag, got: " + contradiction.Error);

            // BUT THE SAME PAIR IS A RUN WHEN THE `Act` CAME FROM THE CONFIGURATION FILE, because
            // the file cannot be told "not this time" and the command line is the more specific
            // statement.  This is the case the shipped template creates: it ships `"Act": true`,
            // and `Run-Phase3Session.ps1 -Experiment Sweep|Hold` passes `--move-steer`.
            ResolveResult fromFile = ResolveRaw(
                Config(@"{ ""GamePath"": ""C:\\g"", ""Dodge"": { ""Enabled"": true, ""Act"": true } }"),
                Cli(new[] { "--move-steer", "hold:90:8000" }, out gameArguments),
                new FakeEnv());
            Expect(fromFile.Outcome == ResolveOutcome.Ready,
                   "a command-line --move-steer must override the FILE's Dodge.Act rather than be refused: "
                   + fromFile.Error);
            Expect(!fromFile.Options.DodgeAct, "the command line's actuator choice wins");
            ExpectEqual("hold:90:8000", fromFile.Options.MoveSteer, "and the pilot runs the experiment asked for");
            Expect(fromFile.Options.Dodge,
                   "the file's Dodge.Enabled is NOT discarded: solving and logging do not contradict "
                   + "somebody else steering");
            Expect(fromFile.Notices.Count == 1,
                   "and the override must be reported, not silent");
            Expect(fromFile.Notices[0].Contains("--move-steer") && fromFile.Notices[0].Contains("Act"),
                   "with a notice naming both, got: " + fromFile.Notices[0]);
            // The adapter reads `DRELAY_DODGE_ACT == "1"`, so NOT writing the name is the statement
            // "do not act" -- which is also C2's rule for a field the operator did not ask for.
            Expect(!Has(Env(fromFile.Options), "DRELAY_DODGE_ACT"),
                   "the child must not be told to act, by name or by value");
            ExpectEqual("hold:90:8000", Get(Env(fromFile.Options), "DRELAY_MOVE_STEER"), "the pilot's own value");

            // --dodge-act on its own puts the pilot in dodge mode, which is the implication the
            // config file has to get too (section 10.6).
            Options acting = Resolve(Empty(), Cli("--game", @"C:\g", "--dodge-act"), new FakeEnv());
            Expect(acting.DodgeAct && acting.Dodge, "--dodge-act implies --dodge");
            ExpectEqual("dodge", acting.MoveSteer, "--dodge-act puts the pilot in dodge mode");
            var actingVars = Env(acting);
            ExpectEqual("1", Get(actingVars, "DRELAY_DODGE_ACT"), "--dodge-act reaches the adapter");
            ExpectEqual("dodge", Get(actingVars, "DRELAY_MOVE_STEER"), "and the pilot");

            // --escape-test implies the frame replacement, which is where the call is made from.
            Options escape = Resolve(Empty(), Cli("--game", @"C:\g", "--escape-test", "25"), new FakeEnv());
            Expect(escape.FrameReplacement, "--escape-test implies --frame-replacement");
            ExpectEqual("25", Get(Env(escape), "DRELAY_ESCAPE_TEST_SEC"), "--escape-test reaches the hook");

            // --help is not an error.
            Expect(CommandLine.TryParse(new[] { "--help" }, out settings, out gameArguments, out help,
                                        out error) && help,
                   "--help must parse as a help request");
        }

        // ------------------------------------------------------------------
        // the fake environment discovery is driven through
        // ------------------------------------------------------------------

        /// <summary>
        /// A machine with no registry, no files and no directories until one is added.
        ///
        /// PATHS ARE NORMALISED THE SAME WAY ON BOTH SIDES, so a test cannot pass because the fake
        /// happened to store a path with the separator the probe used.
        /// </summary>
        private sealed class FakeEnv : IDiscoveryEnv
        {
            private readonly Dictionary<string, string> _registry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, long> _files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            public string LauncherDirectory { get; set; }

            private static string Norm(string path)
            {
                if (path == null) { return string.Empty; }
                return path.Replace('/', '\\').TrimEnd('\\');
            }

            public FakeEnv Registry(string keyPath, string valueName, string value)
            {
                _registry[Norm(keyPath) + "|" + valueName] = value;
                return this;
            }

            public FakeEnv Dir(string path)
            {
                _directories.Add(Norm(path));
                return this;
            }

            public FakeEnv File(string path, long size)
            {
                _files[Norm(path)] = size;
                return this;
            }

            public FakeEnv Text(string path, string text)
            {
                _texts[Norm(path)] = text;
                return this;
            }

            public bool TryReadRegistryString(string keyPath, string valueName, out string value)
            {
                return _registry.TryGetValue(Norm(keyPath) + "|" + valueName, out value);
            }

            public string ReadTextOrNull(string path)
            {
                return _texts.TryGetValue(Norm(path), out string text) ? text : null;
            }

            public bool DirectoryExists(string path)
            {
                return _directories.Contains(Norm(path));
            }

            public bool FileExists(string path)
            {
                return _files.ContainsKey(Norm(path));
            }

            public long FileSize(string path)
            {
                return _files.TryGetValue(Norm(path), out long size) ? size : -1;
            }
        }
    }
}
