// Settings.cs -- the per-field provenance record, and its resolution into `Options`.
//
// WHY THIS TYPE EXISTS AT ALL (HANDOFF-LAUNCH-CONFIG.md section 4.1)
// ------------------------------------------------------------------
// The launcher now reads four sources -- compiled defaults, auto-discovery, a JSON
// configuration file and the command line -- and the precedence is PER FIELD, not per
// source.  `--autoaim highesthp` on the command line must not discard a `GamePath` that
// only the config file knows, and a config file that sets two fields must not discard the
// compiled defaults for the rest.
//
// A parser that fills one struct in argv order cannot express that: "the struct already
// has a value" and "the user supplied a value" are different facts, and only the second
// one may overwrite another source's value.  So every field here is nullable -- `null`
// means "this source said nothing about this field" -- and `Options.Apply` copies only
// the non-null ones.
//
// The single most error-prone part of the implementation is exactly this, and it is the
// reason HANDOFF-LAUNCH-CONFIG.md section 13 C1 is a named invariant with a test.

using System;
using System.Collections.Generic;

namespace Drelay.Launcher
{
    /// <summary>
    /// One source's contribution, with `null` meaning "not set by this source".
    /// The command line and the configuration file both fill one of these, which is what
    /// makes "the same code path applies the implications" (section 10.6) true rather
    /// than aspirational.
    /// </summary>
    internal sealed class Settings
    {
        // Paths.
        public string GamePath;
        public string ExecutablePath;
        public string AssemblyPath;
        public string HookDll;
        public string BootstrapDll;
        public string RunRoot;

        // Numbers.  A null here is what keeps the hook's own default in charge: see
        // `Options.Apply` and section 13 C2.
        public int? TimeoutMs;
        public int? CollectDelayMs;
        public int? DiscoveryWindowMs;
        public int? ResolveDelayMs;
        public int? LiveSeconds;
        public int? Cores;
        public double? DodgeDisc;
        public double? DodgeWindowSeconds;
        public double? EscapeTestSeconds;

        // Switches.
        public bool? Off;
        public bool? Act;
        public bool? FullTrace;
        public bool? Verbose;
        public bool? NoWait;
        public bool? PauseAfterRun;
        public bool? DbgDump;
        public bool? FrameHelper;
        public bool? FrameReplacement;
        public bool? Trace;
        public bool? Dodge;
        public bool? DodgeAct;

        // Values that are not switches.
        public string AutoAim;
        public string MoveSteer;
        public string TestTargets;

        /// <summary>Whether this source said anything at all. Used only for reporting.</summary>
        public bool IsEmpty
        {
            get
            {
                return GamePath == null && ExecutablePath == null && AssemblyPath == null
                    && HookDll == null && BootstrapDll == null && RunRoot == null
                    && TimeoutMs == null && CollectDelayMs == null && DiscoveryWindowMs == null
                    && ResolveDelayMs == null && LiveSeconds == null && Cores == null && DodgeDisc == null
                    && DodgeWindowSeconds == null && EscapeTestSeconds == null
                    && Off == null && Act == null && FullTrace == null && Verbose == null
                    && NoWait == null && PauseAfterRun == null && DbgDump == null
                    && FrameHelper == null && FrameReplacement == null && Trace == null
                    && Dodge == null && DodgeAct == null
                    && AutoAim == null && MoveSteer == null && TestTargets == null;
            }
        }
    }

    /// <summary>
    /// The resolved configuration: every default materialised, every source merged, every
    /// implication applied.  This is the type the rest of the launcher reads, and it is
    /// deliberately the same shape it was before the config file existed -- the only
    /// additions are the `Set*` provenance flags, which exist so that Section 13 C2
    /// ("an unset field reaches nothing") is checkable rather than asserted.
    /// </summary>
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

        // WHETHER THE USER SUPPLIED THE TIMING KNOBS, WHICH IS NOT THE SAME AS THEIR VALUE.
        //
        // The launcher's defaults for these five are the same numbers the hook falls back
        // to (DrelayInstall.cpp: kDefaultCollectDelayMs 0, kDefaultDiscoveryWindowMs 10000,
        // kDefaultResolveDelayMs 100, kDefaultLiveWindowMs 5000; DrelayMain.cpp: 60000 ms),
        // so a run that supplies none of them behaves identically whether the launcher
        // writes them or not.  Writing only what was supplied is what keeps the hook's own
        // default authoritative (section 10.5's rule, applied to the timing knobs) and it is
        // what makes C2 a test rather than a claim.
        public bool SetTimeoutMs;
        public bool SetCollectDelayMs;
        public bool SetDiscoveryWindowMs;
        public bool SetResolveDelayMs;
        public bool SetLiveSeconds;

        // HOW MANY PROCESSORS THE GAME'S RUNTIME IS TOLD IT HAS, OR 0 FOR "LET IT DECIDE".
        //
        // Passed to the child as `DOTNET_PROCESSOR_COUNT`, which .NET reads before `Main` and
        // which changes `Environment.ProcessorCount` and the thread pool's worker count. It is
        // the only lever found so far that changes how many threads the game's own startup
        // `Parallel.For` (DataManager.ParseCachedXmls) runs at once, and that matters because
        // the runtime's `TS_Hijacked` assertion is a thread-suspension race: fewer threads in
        // cooperative mode while the GC asks them to stop is less of a race, on the same game
        // and the same runtime.
        //
        // 0 means "not set" rather than "one core", and the launcher only writes the variable
        // when it was asked for - so a machine whose runtime gets this right keeps its own
        // default.
        public int Cores;
        public bool SetCores;

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
        // default if the variable is absent or unparseable, so this is a
        // convenience rather than the only way to set it.
        //
        // IT DEFAULTS TO ZERO, AND IT USED TO DEFAULT TO THIRTY SECONDS.  The wait was
        // there because two of the pinned targets are compiled on first use during
        // gameplay, so a window that opened ~1.3 s into the process could close before
        // the operator had cleared the menu -- but the hook's own comment always
        // conceded that nothing was lost by looking during the delay.  Since the patch
        // no longer waits for the window at all (`DrelayPatchTargetsDuringDiscovery`
        // hooks a target half a second after the JIT reports it), the delay only
        // postponed the hook being useful.  The knob stays for a run whose subject is
        // the window's per-target verdicts.
        //
        // It has to be plumbed through the launcher because the launcher *strips every
        // `DRELAY_HOOK_*` variable from the child's environment* before adding its own
        // -- a deliberate safety measure that removes the `DOTNET_TieredCompilation`
        // hazard namespace, and the reason a flag set from the operator's shell in that
        // namespace would never reach the hook.  See PHASE1-HANDOFF.md section 4 trap 1.
        public int CollectDelayMs;

        // How long the discovery window stays open, in milliseconds.  Passed to the hook
        // as `DRELAY_HOOK_DISCOVERY_WINDOW_MS`.
        //
        // THIS IS A REPORTING KNOB, NOT A FUNCTIONAL ONE, and the distinction is the
        // whole reason it could drop from thirty seconds to ten.  The window bounds the
        // per-target verdicts (`FOUND`/`MISSING`, the native entry, the token, the target
        // size table) and closes early the moment every target is in.  Nothing on the
        // patch path waits for it, so a machine that needs longer to reach gameplay is
        // not being held back by this number.
        public int DiscoveryWindowMs = 10000;

        // How long the hook waits for the runtime to leave the startup-hook phase before
        // it calls into managed code, in milliseconds.  Passed as
        // `DRELAY_HOOK_RESOLVE_DELAY_MS`.
        //
        // IT IS A MARGIN AND NOT A SIGNAL, and the hook's own call site says why one
        // cannot exist: a startup hook cannot JIT-compile anything, so it cannot execute
        // a single statement after it returns, so nothing it writes can prove that it has
        // returned.  100 ms replaced a hardcoded 500 and is still two orders of magnitude
        // more than the epilogue it covers.  `0` is the diagnostic value that asks
        // whether the margin matters at all.
        public int ResolveDelayMs = 100;

        // Whether the frame read stops when the evidence window closes.  Passed as
        // `DRELAY_KEEP_READING=0`, which is the inverse of the old switch on purpose.
        //
        // OFF BY DEFAULT, BECAUSE IT WAS ON AND IT BROKE THE RELEASE CASE.  Pausing after
        // a five-second recording window was right for a run whose subject was the
        // recording; the frame read is also the sensor the dodge runs on, so a session
        // that paused there would stop dodging five seconds in, with its evidence already
        // written and nothing saying the hook had gone quiet.
        public bool PauseAfterRun;

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
        //
        // THE CONFIG FILE CANNOT SET IT (HANDOFF section 5 rule 3): a user-facing
        // configuration that offers this switch is offering a way to patch a method that
        // is not in the pinned build.
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

        // PHASE 3'S ACTUATOR REQUEST, AND IT IS THE ONE SWITCH THAT MOVES THE PLAYER.
        //
        // The value is passed to the hook verbatim as `DRELAY_MOVE_STEER`, and the
        // hook decides what it means (see `SteeringPilot`): a mode name, optionally
        // with parameters.  Three properties matter and each is deliberate:
        //
        //   * EMPTY MEANS NOTHING HAPPENS.  The movement replacement is still
        //     published and still calls through, so the plumbing is exercised by
        //     every run, but the player is only ever moved when an operator asked;
        //   * IT IS A *VALUE*, NOT A FLAG, because the interesting experiments are
        //     "hold this heading", "sweep these headings and calibrate", "walk the
        //     speed boundary" -- and a boolean cannot carry which one;
        //   * IT IS PASSED THROUGH RATHER THAN INVENTED HERE.  The launcher owns the
        //     `DRELAY_HOOK_*` namespace and deletes inherited names from it; this one
        //     is the operator's, so the launcher's job is to place it in the child's
        //     environment unchanged and let the hook read it.
        //
        // IT IS NOT IN THE CONFIG FILE (section 5.1, rule 3): it is an experiment that
        // moves the player, not a setting.
        public string MoveSteer;

        // THE AUTO-AIM REQUEST: `--autoaim closest` or `--autoaim highesthp`.
        //
        // WHY IT IS A VALUE AND NOT A FLAG.  The two modes are the same mechanism with a
        // different score, and a boolean could not say which one was asked for -- the same
        // argument `--move-steer` carries.  The value is passed to the hook verbatim as
        // `DRELAY_AIM_MODE`; the launcher validates that it is one of the two names it
        // knows so a typo is refused at the command line rather than silently selecting
        // nothing once the game is already running.
        //
        // IT IS ALSO A REAL SETTING CHANGE, AND THE HELP TEXT SAYS SO.  The replacement
        // arms the game's own `Prefs.enable_aimbot`, because `Player.Shoot` checks that
        // property before it ever asks for a target -- see `AutoAim` in the bootstrap.
        //
        // AND IT IMPLIES `--frame-replacement`.  That is not a preference: the flag has to be
        // armed from the game's own thread before `AimbotSystem.FindTarget` is ever called,
        // and the frame replacement is the only game-thread entry point every run has.  The
        // reason the write cannot wait for the aim target itself is circular -- the target is
        // compiled only once the flag is set -- and `AutoAim.OnFrame` carries the full account.
        public string AutoAim;

        // SPIKE 3.2'S RECORDING REQUEST.
        //
        // IT SETS THREE THINGS, AND EACH ONE IS A DEPENDENCY THE OPERATOR SHOULD NOT HAVE TO KNOW
        // ABOUT. The trace recorder reads `DRELAY_TRACE`; it is called from the MANAGED frame
        // replacement and not from the native frame detour, so it needs
        // `DRELAY_HOOK_FRAME_REPLACEMENT=1`; and its input is the Phase 2 sensor's threat set, so it
        // needs `DRELAY_FRAME_SENSOR=1`. A run that gets any of them wrong writes a valid header and
        // zero frames -- an empty file that looks exactly like a session in which no bullet came
        // near, which is why the offline harness now fails on an empty recording rather than
        // reporting every invariant vacuously held. The run of `20261014-164314-p12060` is that
        // file, and it cost a session.
        public bool Trace;

        // PHASE 3'S ESCALATION TEST: call `World.RequestEscape()` once, under hook control.
        //
        // WHY IT IS A SWITCH AND NOT PART OF THE DODGE.  The escape is Phase 3's floor --
        // reached only when the projected damage is lethal and no admissible move avoids it
        // -- but the *call itself* has never been measured, and the 2026-10-13 run is the
        // receipt: `escape MISSING never compiled`, because nothing ever called it, so the
        // JIT never compiled it and the detour never saw it. A floor nobody has stood on is
        // not a floor. This switch tests the actuator alone, with no threat set and no
        // solver behind it, which is why it can be tested before the dodge exists.
        //
        // SECONDS, NOT FRAMES, AND MEASURED FROM THE FIRST FRAME WITH A WORLD.  Frames are
        // the client's variable-rate loop and would make the delay depend on the frame rate;
        // wall clock from run start would make it depend on how long the operator took to get
        // into a zone. From "the world exists" it is the one origin the operator controls.
        //
        // IT IMPLIES `--frame-replacement`, because that is where the call is made from, and
        // it is a fatal combination to get wrong silently: a run that asked for the escape
        // test and did not install the frame replacement would report "the test never armed"
        // and read as a client refusal. `ApplyImplications` adds the flag and says so.
        public double EscapeTestSeconds = -1.0;

        // DELIVERABLE 3.3: THE DODGE'S OWN TWO SWITCHES, AND THEY ARE TWO ON PURPOSE.
        //
        // `--dodge` runs the solver on live frames and LOGS what it would have done. That is the
        // plan's requirement ("ship it behind its own switch, defaulting to a mode that logs
        // decisions without acting"), and it is also the only shape in which the decision can be
        // compared against a session's health trace before anything moves.
        //
        // `--dodge-act` additionally lets the decision reach the input -- and even then only
        // because it also puts the pilot in `dodge` mode, where the pilot writes nothing at all on
        // any frame the adapter publishes no command. So moving the player takes three separate
        // decisions: the dodge on, the acting on, and the actuator in dodge mode.
        //
        // BOTH IMPLY THE FRAME REPLACEMENT AND THE SENSOR, for the reason `--trace` does: the
        // adapter runs on the sensor's frame path and its input is the sensor's projectile list, so
        // `--dodge` without either is a solver that decides about an empty world on every frame and
        // reports `NoThreat` -- an empty recording's twin, and that one already cost a session.
        public bool Dodge;
        public bool DodgeAct;

        // THE RULE 6 DECISION WINDOW, PASSED AT STARTUP RATHER THAN REBUILT.
        //
        // `--dodge-disc K` sets the window as a MULTIPLE of the time a projectile takes to cross the
        // collision disc (`player.radius + projectile.radius`). It replaced a constant 50 ms, which
        // was a DISTANCE of `0.05 * speed` -- 1.95x the collision disc on a 16 u/s projectile and
        // 0.53x of it on the 6 u/s ones -- so one number could not be right for both ends of the
        // game. `--dodge-window S` is an absolute floor in seconds under it and is 0 by default.
        //
        // THE TWO PASS -1 FOR "NOT SET" rather than carrying a copy of the solver's default, for the
        // same reason `EscapeTestSeconds` does: a default duplicated in two assemblies is a default
        // that will disagree with itself, and this one already exists in `DodgeConfig.Default()`.
        // Only a value the operator actually typed travels.  THE CONFIG FILE USES `null` FOR THE
        // SAME THING (section 5.1) and lands here as -1.0.
        public double DodgeDisc = -1.0;
        public double DodgeWindowSeconds = -1.0;

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

        /// <summary>
        /// The directory auto-discovery produced, when nobody said where the game was.  Reported
        /// once in the transcript, because it is the one input the launcher chose for itself.
        /// </summary>
        public string DiscoveredFrom;

        /// <summary>
        /// Normalise and check an `--autoaim` mode.
        ///
        /// REFUSED AT THE COMMAND LINE RATHER THAN AT THE HOOK, because the two failures look
        /// nothing alike from the outside.  A typo that reached the hook would resolve to
        /// "off", the run would start, and the operator would spend a session watching the
        /// game not aim while `bootstrap.log` said the module had never been asked for
        /// anything -- the class of mistake this project has paid for repeatedly (see the
        /// empty-trace note on `Options.Trace`).  Here it costs one line of console text.
        ///
        /// THE REASON IS RETURNED WITHOUT THE FLAG NAME, because two callers report it: the
        /// command-line parser prefixes `--autoaim`, and the configuration reader prefixes
        /// `drelay.config.json -> AutoAim`.  A typo names the field it was typed in either
        /// way (section 13 C3).
        /// </summary>
        public static bool TryParseAutoAim(string raw, out string mode, out string reason)
        {
            mode = null;
            reason = null;
            string value = (raw ?? "").Trim().ToLowerInvariant();
            switch (value)
            {
                case "closest":
                case "close":
                case "nearest":
                    mode = "closest";
                    return true;
                case "highesthp":
                case "highest-hp":
                case "highest":
                case "maxhp":
                case "max-hp":
                    mode = "highesthp";
                    return true;
                default:
                    reason = "needs a mode: 'closest' (the nearest damageable enemy) "
                             + "or 'highesthp' (the damageable enemy with the most health), got '"
                             + raw + "'";
                    return false;
            }
        }

        /// <summary>
        /// Copy every field <paramref name="from"/> actually supplied over this one.
        ///
        /// THIS IS WHERE PER-FIELD PRECEDENCE LIVES.  Called once for the configuration file
        /// and then once for the command line, in that order, which is section 4's table
        /// read top to bottom.  `Settings` fields that are null are absent, never zero.
        /// </summary>
        public void Apply(Settings from)
        {
            if (from == null) { return; }

            if (from.GamePath != null) { GamePath = from.GamePath; }
            if (from.ExecutablePath != null) { ExecutablePath = from.ExecutablePath; }
            if (from.AssemblyPath != null) { AssemblyPath = from.AssemblyPath; }
            if (from.HookDll != null) { HookDll = from.HookDll; }
            if (from.BootstrapDll != null) { BootstrapDll = from.BootstrapDll; }
            if (from.RunRoot != null) { RunRoot = from.RunRoot; }

            if (from.TimeoutMs.HasValue) { TimeoutMs = from.TimeoutMs.Value; SetTimeoutMs = true; }
            if (from.CollectDelayMs.HasValue) { CollectDelayMs = from.CollectDelayMs.Value; SetCollectDelayMs = true; }
            if (from.DiscoveryWindowMs.HasValue) { DiscoveryWindowMs = from.DiscoveryWindowMs.Value; SetDiscoveryWindowMs = true; }
            if (from.ResolveDelayMs.HasValue) { ResolveDelayMs = from.ResolveDelayMs.Value; SetResolveDelayMs = true; }
            if (from.LiveSeconds.HasValue) { LiveSeconds = from.LiveSeconds.Value; SetLiveSeconds = true; }
            if (from.Cores.HasValue) { Cores = from.Cores.Value; SetCores = true; }

            if (from.DodgeDisc.HasValue) { DodgeDisc = from.DodgeDisc.Value; }
            if (from.DodgeWindowSeconds.HasValue) { DodgeWindowSeconds = from.DodgeWindowSeconds.Value; }
            if (from.EscapeTestSeconds.HasValue) { EscapeTestSeconds = from.EscapeTestSeconds.Value; }

            if (from.Off.HasValue) { Off = from.Off.Value; }
            if (from.Act.HasValue) { DryRun = !from.Act.Value; }
            if (from.FullTrace.HasValue) { FullTrace = from.FullTrace.Value; }
            if (from.Verbose.HasValue) { Verbose = from.Verbose.Value; }
            if (from.NoWait.HasValue) { NoWait = from.NoWait.Value; }
            if (from.PauseAfterRun.HasValue) { PauseAfterRun = from.PauseAfterRun.Value; }
            if (from.DbgDump.HasValue) { DbgDump = from.DbgDump.Value; }
            if (from.FrameHelper.HasValue) { FrameHelper = from.FrameHelper.Value; }
            if (from.FrameReplacement.HasValue) { FrameReplacement = from.FrameReplacement.Value; }
            if (from.Trace.HasValue) { Trace = from.Trace.Value; }
            if (from.Dodge.HasValue) { Dodge = from.Dodge.Value; }
            if (from.DodgeAct.HasValue) { DodgeAct = from.DodgeAct.Value; }

            if (from.AutoAim != null) { AutoAim = from.AutoAim; }
            if (from.MoveSteer != null) { MoveSteer = from.MoveSteer; }
            if (from.TestTargets != null) { TestTargets = from.TestTargets; }
        }

        /// <summary>
        /// The flags that imply other flags, applied ONCE to the merged result.
        ///
        /// THIS IS A SEPARATE PASS ON PURPOSE (section 10.6).  The command line used to apply
        /// these inside the parser, which is the one place a configuration file cannot reach:
        /// a config that sets `AutoAim` without the frame replacement produces a module that is
        /// published, verified and never entered, and nothing in the log says why.  Applying
        /// them here means both sources go through the same code, and neither can re-derive the
        /// dependency differently.
        /// </summary>
        public void ApplyImplications()
        {
            // `--escape-test` calls `World.RequestEscape()` from the frame replacement, so a run
            // without it reports "the test never armed" -- indistinguishable from a client that
            // refused to escape.
            if (EscapeTestSeconds >= 0.0) { FrameReplacement = true; }

            // Auto-aim must arm `Prefs.enable_aimbot` from the game's own thread before the
            // target method is ever compiled, and the frame replacement is that thread.
            if (!string.IsNullOrEmpty(AutoAim)) { FrameReplacement = true; }

            // `--dodge-act` puts the pilot in `dodge` mode, which is a value the actuator reads
            // by name.  Set here rather than in the parser for the same reason as the two above.
            if (DodgeAct)
            {
                Dodge = true;
                MoveSteer = "dodge";
            }
        }

        /// <summary>
        /// The one contradiction the launcher refuses rather than resolves, checked after the
        /// merge and BEFORE <see cref="ApplyImplications"/> -- because the implication above is
        /// what makes the two fields agree, and a check that ran after it could never fire.
        /// </summary>
        public bool Validate(out string error)
        {
            error = null;
            // A CONTRADICTION IS REFUSED RATHER THAN RESOLVED. `--dodge-act` puts the pilot in
            // `dodge` mode; an explicit `--move-steer` says something else. Quietly preferring one
            // would be a run that walks where the operator did not ask -- the exact class of
            // surprise `--move-steer`'s own comment says the mode-value design exists to prevent.
            //
            // IT IS NOW REACHABLE FROM THE CONFIG FILE TOO, because the written template ships
            // `"Act": true`, so the message names both spellings and says which edit resolves it.
            // A refusal that only mentions `--dodge-act` would send an operator who passed
            // `--move-steer` looking for a flag they never typed.
            if (DodgeAct && !string.IsNullOrEmpty(MoveSteer))
            {
                error = "--dodge-act (or \"Dodge\": {\"Act\": true} in drelay.config.json) drives the "
                        + "actuator itself and cannot be combined with --move-steer '" + MoveSteer
                        + "'. Set \"Act\": false in the config file, or drop --move-steer.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Make every relative path absolute and fill in the three paths that default to
        /// "beside the launcher".
        ///
        /// WHY ABSOLUTE: resolving a relative path against the *current* directory is a way to be
        /// surprised when a script runs from elsewhere, so the default is derived from the
        /// launcher's own location and anything else is resolved against the working directory at
        /// startup, once, where the operator can see it.
        /// </summary>
        public void ResolvePaths(string launcherDirectory, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(GamePath))
            {
                return;
            }
            GamePath = System.IO.Path.GetFullPath(GamePath);
            if (!string.IsNullOrEmpty(ExecutablePath))
            {
                ExecutablePath = System.IO.Path.GetFullPath(ExecutablePath);
            }
            if (!string.IsNullOrEmpty(AssemblyPath))
            {
                AssemblyPath = System.IO.Path.GetFullPath(AssemblyPath);
                // When an assembly is named explicitly, its own directory is the
                // one the hook should treat as "the game directory": probing a
                // different one would be measuring the wrong tree.  This is what
                // makes the same launcher usable against a test host.
                if (System.IO.File.Exists(AssemblyPath))
                {
                    GamePath = System.IO.Path.GetDirectoryName(AssemblyPath);
                }
            }
            if (string.IsNullOrEmpty(HookDll))
            {
                HookDll = System.IO.Path.Combine(launcherDirectory, "Drelay.Hook.dll");
            }
            if (string.IsNullOrEmpty(BootstrapDll))
            {
                BootstrapDll = System.IO.Path.Combine(launcherDirectory, "Drelay.Bootstrap.dll");
            }
            if (string.IsNullOrEmpty(RunRoot))
            {
                RunRoot = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "drelay", "hook");
            }
            else
            {
                // SECTION 5'S NOTE, ENFORCED RATHER THAN DOCUMENTED: "if it is ever exposed, it is
                // exposed as an absolute path".  A relative `RunRoot` in the config file would
                // otherwise be resolved against whatever directory the launcher happened to be
                // started from, which is the surprise every other path here is made absolute to
                // avoid.
                RunRoot = System.IO.Path.GetFullPath(RunRoot);
            }
        }
    }
}
