// DrelaySelfCheck.h -- the hook's own acceptance test.
//
// HOOK-ARCHITECTURE.md section 9 lists eight checks.  This is their
// implementation, plus the Phase 1 question the architecture document leaves
// open: whether the address the JIT returned is the address that executes.
//
// The report distinguishes three outcomes rather than two, which is the same
// distinction Phase 0's fingerprint gate made and for the same reason:
//
//   PASSED  the check was made and succeeded
//   FAILED  the check was made and failed -- with the expected/actual pair
//   GAP     the check could not be made here (no live game yet, no relay, ...)
//
// A gap is never counted as a pass, because "we did not look" and "we looked and
// it was fine" are exactly the two things a self-check exists to tell apart.

#ifndef DRELAY_SELFCHECK_H
#define DRELAY_SELFCHECK_H

#include "DrelayInstall.h"

#include <windows.h>

namespace drelay {

// Runs the checks that can be made before any patch.  Returns false when a check
// that must pass did not, which the caller treats as a refusal.
bool DrelayRunSelfChecks(DrelayInstallReport* report);

// Writes selfcheck.txt into the run directory, one line per check.  Separate
// from running them so that the file is a record of the run rather than a
// side effect of it.
void DrelayWriteSelfCheckFile(const DrelayInstallReport& report);

// Live checks, run after the discovery window: is the frame tick actually being
// entered, and are the reads succeeding.
void DrelayRunLiveChecks(DrelayInstallReport* report);

} // namespace drelay

#endif // DRELAY_SELFCHECK_H
