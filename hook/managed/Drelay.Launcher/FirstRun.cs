// FirstRun.cs -- the two rules that decide whether a refusal is allowed to hold the window open.
//
// HANDOFF-LAUNCH-CONFIG.md section 6, and both rules are about WHO IS WATCHING:
//
//   * "Pause only when there is nobody to read the output otherwise.  A first run from a script
//     or from the future proxy must not block on a keypress.  Detection: `args.Length == 0` AND
//     `!Console.IsInputRedirected` and the failure is a user-fixable one.  Scripted runs always
//     pass at least one argument, so this only affects the double-click path."
//   * "Never pause on a refusal the user cannot fix by editing the file (missing DLLs beside the
//     launcher, a game build whose fingerprint does not match).  Print and exit."
//
// IT IS A FUNCTION RATHER THAN AN `if` IN `Main` because section 13 C5 is a named invariant
// ("a zero-argument run with redirected input never pauses") and a check that has to start a
// process to see an `if` is a check nobody runs.  `LauncherHarness --check c5` calls this, and
// also asserts -- by reading the source -- that `Main` has exactly one `Console.ReadKey` and
// that it is guarded by this function, so the helper cannot drift away from its only caller.

namespace Drelay.Launcher
{
    internal static class FirstRun
    {
        /// <summary>
        /// Whether a refusal may wait for a keypress.
        /// </summary>
        /// <param name="noArguments">
        /// `args.Length == 0`: the double-click path.  A scripted run always passes at least one
        /// argument, and a refusal it cannot read is a refusal it cannot act on.
        /// </param>
        /// <param name="inputRedirected">
        /// `Console.IsInputRedirected`.  A redirected stdin is a machine on the other end of a
        /// pipe; `Console.ReadKey` would either throw or block it forever.
        /// </param>
        /// <param name="userFixable">
        /// Whether editing the configuration file, or starting the launcher differently, could
        /// change the outcome.  An OpenGL install or a game build whose fingerprint does not match
        /// is not something a file this program wrote can fix.
        /// </param>
        public static bool ShouldPause(bool noArguments, bool inputRedirected, bool userFixable)
        {
            return noArguments && !inputRedirected && userFixable;
        }

        /// <summary>
        /// Whether this run may create the configuration file.
        ///
        /// §6 HAS THE TEMPLATE WRITTEN ONLY WHEN DISCOVERY FAILS, AND THAT IS TOO NARROW.  On a Steam
        /// machine discovery *succeeds*, which is the common case and the whole point of §7 -- so the
        /// operator whose game was found is the one user who never gets the file, and the only way to
        /// change what the hook does is to know the schema by heart.  The file is written whenever a
        /// no-argument run finds it missing, and never by a scripted run (which must not create files
        /// it did not ask for) and never over a file that exists (which may be the operator's edits).
        ///
        /// IT IS WRITTEN BEFORE THE CONFIGURATION IS RESOLVED, so that the file describes the run
        /// that is about to happen rather than the one after it -- a written default that the run
        /// then ignores would be a lie about what just ran.
        /// </summary>
        public static bool ShouldWriteTemplate(bool noArguments, bool configExists)
        {
            return noArguments && !configExists;
        }
    }
}
