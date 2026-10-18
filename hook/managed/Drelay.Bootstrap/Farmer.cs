// Farmer.cs -- the PUBLIC HALF of the auto-farmer: the surface, and no decision in it.
//
// =====================================================================================
// WHAT THIS FILE IS, AND WHY THE REAL ONE IS NOT NEXT TO IT
// =====================================================================================
//
// `PLAN-AUTOFARM.md` section 13 records the operator's constraint: the auto-farmer's decision code
// is not shared. Everything else in this component is public -- the solver, the planner, the
// point-goal plumbing the farmer and the routes share, the world log, the harness -- but the code
// that decides WHICH ENEMY TO WALK AT is a private module. The public tree carries this file: the
// same class name, the same public surface, and no behaviour. `ChooseTarget` publishes nothing, so
// every downstream frame is the frame the dodge would have produced with the farmer switched off.
//
// The private file lives at `hook/private/Farmer.cs`, which `hook/.gitignore` ignores. It defines
// `Drelay.Bootstrap.Farmer` with exactly this surface plus one line this file cannot have --
// `public const bool Installed = true`. The build (`build.ps1`, and both `.csproj` files, which
// agree by construction because they apply the same rule) removes this file and compiles
// `hook/private/**/*.cs` instead whenever the private directory is present. The public stub and the
// private module are therefore never both in one assembly, and neither build has to know the other
// exists at run time.
//
// =====================================================================================
// WHY A STUB AND NOT A MISSING TYPE OR A REFLECTIVE LOOKUP
// =====================================================================================
//
// A missing type would mean every caller of the farmer -- `DodgeAdapter`'s frame path, its warm-up
// (`Prepare`), the harness -- had to be compiled twice, and "the public build" would be a different
// program rather than the same program with one module declined. A runtime `Assembly.LoadFrom`
// plugin would put a first call to managed code that has not been JITted inside a patched game
// frame, which is exactly what `DodgeAdapter.Prepare` exists to prevent, and it would make the
// private artifact's ABI a thing that can drift from the bootstrap's.
//
// One file, swapped at build time, is the whole mechanism. `Farmer.Installed` is the one fact the
// two versions may disagree on, and it is a `const`: a caller that asks is compiled against the
// build it is in, not against a value read at run time.
//
// =====================================================================================
// HOW TO TELL THE TWO BUILDS APART
// =====================================================================================
//
//   * `Farmer.Installed` is `false` here and `true` in the private module;
//   * `build.ps1` prints which of the two it compiled and warns that a private build is not for
//     distribution;
//   * the harness prints "the private farmer module is not installed" and skips the `farmer*`
//     fixtures instead of failing their assertions;
//   * `Test-HookOffline.ps1` asserts that the build on disk agrees with the presence of
//     `hook/private`, so a stale `out\` cannot masquerade as the other build.
//
// =====================================================================================
// THE SURFACE IS DELIBERATELY IDENTICAL
// =====================================================================================
//
// The counters, properties and methods below are the private module's own public surface, kept in
// step by hand. They exist so this file is a drop-in replacement in EITHER direction and so the
// report and the harness compile against one type. A member added to the private module but not
// here breaks the public build loudly, which is the right failure: the private module is allowed to
// hide its decisions, not its shape.

namespace Drelay.Bootstrap
{
    /// <summary>
    /// The farmer's target selection, as the PUBLIC BUILD declares it. This implementation does
    /// nothing: it publishes no goal, holds no target, and counts nothing. The private module
    /// (`hook/private/Farmer.cs`) is the one that walks.
    /// </summary>
    public sealed class Farmer
    {
        /// <summary>
        /// False in the public build. `PLAN-AUTOFARM.md` section 13. The private module declares
        /// the same member as `true`, and it is a `const` on purpose -- see this file's header.
        /// </summary>
        public const bool Installed = false;

        // --- The report's counters ----------------------------------------------------------
        //
        // THE SAME FIELDS, ALL ZERO. The report prints them, the harness reads them, and neither
        // has to ask whether the module is installed before touching a field.

        /// <summary>Frames the farmer ran with its switch on.</summary>
        public long Frames;

        /// <summary>Frames on which a target was chosen and a goal point published.</summary>
        public long FramesWithTarget;

        /// <summary>Frames on which the farmer had a target and chose NOT to switch away from it.</summary>
        public long FramesHeld;

        /// <summary>Transitions from no-target to a target.</summary>
        public long Acquired;

        /// <summary>Transitions where a held target was abandoned for a closer one.</summary>
        public long Switches;

        /// <summary>Transitions where the held target was lost rather than beaten.</summary>
        public long ForcedReleases;

        /// <summary>Frames the farmer would have wanted a target and had none.</summary>
        public long FramesNoTarget;

        /// <summary>True when the last decision held a target. Always false here.</summary>
        public bool HasTarget => false;

        /// <summary>The held target's id, or zero when there is none. Always zero here.</summary>
        public int TargetGameId => 0;

        /// <summary>The held target's position, for the report. Always zero here.</summary>
        public float TargetX => 0.0f;

        /// <summary>The held target's position, for the report. Always zero here.</summary>
        public float TargetY => 0.0f;

        /// <summary>Player-to-target distance on the last frame. Always zero here.</summary>
        public float TargetDistance => 0.0f;

        /// <summary>Targets skipped by `FarmIgnore`. Always zero here.</summary>
        public long Ignored;

        /// <summary>Targets skipped by the client's `Unhittable()` rule. Always zero here.</summary>
        public long UnhittableSkipped;

        /// <summary>The lobby pilot's counters. Always zero here, and named so the two builds agree.</summary>
        public long LobbyFrames;
        public long PortalAttempts;
        public long PortalActivations;
        public long PortalFailures;
        public string PortalFailure;
        public string PortalTitle;

        /// <summary>The travel fallback's counters. Always zero here, and named so the two builds agree.</summary>
        public long TravelFrames;
        public long RoamWaypoints;

        /// <summary>False here: the public stub never travels.</summary>
        public bool Traveling => false;

        /// <summary>
        /// Drops the hold. A no-op here, and named anyway so the world-change path compiles
        /// without a conditional.
        /// </summary>
        public void Reset()
        {
        }

        /// <summary>Zeroes the report's counters, which are already zero.</summary>
        public void ResetCounters()
        {
            Frames = 0;
            FramesWithTarget = 0;
            FramesHeld = 0;
            Acquired = 0;
            Switches = 0;
            ForcedReleases = 0;
            FramesNoTarget = 0;
        }

        /// <summary>
        /// Chooses this frame's target and publishes it as the snapshot's point goal.
        ///
        /// IT PUBLISHES NOTHING, AND THAT IS THE COMPLETE BEHAVIOUR OF THIS BUILD. The snapshot
        /// leaves `GoalHasPoint` false (`DodgeSnapshot.Clear`), so the solver's goal ladder answers
        /// exactly as it did before the farmer existed. The private module is the only thing that
        /// ever sets the three goal fields.
        /// </summary>
        public void ChooseTarget(DodgeSnapshot s, DodgeConfig cfg)
        {
        }

        /// <summary>
        /// The farmer's line in the run log. This build's line says what it is rather than printing
        /// a row of zeroes that reads like a farmer that found nothing: a run with the switch on
        /// and no module installed is a deployment fact, not a measurement.
        /// </summary>
        public string Describe()
        {
            return "farm not installed (private module; PLAN-AUTOFARM.md section 13)";
        }

        /// <summary>The lobby/travel line. Empty here, so the public build writes no second line.</summary>
        public string DescribePilot()
        {
            return "pilot not installed";
        }
    }
}
