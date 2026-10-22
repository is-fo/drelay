// DrelayCrash.h -- the in-process crash reporter.
//
// WHY THIS EXISTS INSTEAD OF A DEBUGGER
// -------------------------------------
// PHASE1-HANDOFF.md section 3 says the highest-value thing left in Phase 1 is to
// attach a native debugger and break on the fault.  There is no debugger on the
// machine this is built on: no cdb, no windbg, no Visual Studio debugger -- only
// `dbghelp.dll` next to the Windows Kits headers.  So the hook carries its own.
//
// What a debugger would have answered, and what this answers instead:
//
//   * "where did it fault"           -> the exception address, resolved to
//                                       `module+0xRVA` against a module list
//                                       snapshotted before the fault.
//   * "what did it touch"            -> `ExceptionInformation[1]` for an access
//                                       violation, which says whether the read
//                                       was a null, a wild pointer, or a
//                                       plausible-looking small integer.
//   * "who called it"                -> a real stack walk plus a raw stack scan,
//                                       because the faulting thread may have no
//                                       unwind data at the top frame (a MinHook
//                                       trampoline has none).
//
// It is installed from `DrelayHook_Init`, i.e. while the target process is still
// suspended by `CreateProcess(CREATE_SUSPENDED)`, so it is in place before a
// single managed instruction runs and before the watcher thread exists.
//
// THE ONE THING IT MUST NOT DO IS CHANGE THE OUTCOME.  Every handler returns
// `EXCEPTION_CONTINUE_SEARCH`: the runtime's own handling still runs, the fatal
// error is still printed, and the process still dies.  This observes; it does
// not intervene.  A diagnostic that swallows the fault it is measuring would be
// worth less than no diagnostic at all.
//
// AND THE SECOND THING: IT MUST NOT RAISE AN EXCEPTION OF ITS OWN.  `OutputDebugStringA`
// raises `0x40010006`, and raising it from inside another exception's dispatch
// re-enters the handler chain -- which, on a thread the runtime has just hijacked
// for a collection, used to turn the hijack trip into a CLR assert and a `0xC0000602`
// death.  That is why the debugger channel is now last-chance only; the argument is
// above STATUS_RETURN_ADDRESS_HIJACK_ATTEMPT in DrelayCrash.cpp.

#ifndef DRELAY_CRASH_H
#define DRELAY_CRASH_H

namespace drelay {

// Installs the vectored handler and the last-chance filter, and takes the first
// module-list snapshot.  Idempotent.
void DrelayInstallCrashReporter();

// Re-reads the module list AND moves the handler back to the front of the
// vectored chain.  Called from the watcher thread at each point where the
// process is about to do something new, and both halves are necessary:
//
//   * THE MODULE LIST, because the snapshot taken at injection time cannot
//     contain clrjit.dll, coreclr.dll or anything else the host loads later --
//     which is every module a fault here would be in.  It is refreshed from an
//     ordinary thread context on purpose: the loader lock is taken here, and an
//     exception handler is the wrong place to take a lock on the off chance the
//     fault happened while holding it.
//
//   * THE CHAIN POSITION, because `AddVectoredExceptionHandler(1, ...)` inserts
//     at the HEAD, so handlers registered with `First=1` run in reverse order of
//     registration.  This DLL registers before CoreCLR exists; the runtime then
//     registers its own, which therefore runs *first* and consumes a
//     managed-context access violation by redirecting it into a managed
//     exception.  The first version of this reporter wrote no report at all for
//     exactly that reason -- the fault was real, reproducible and completely
//     invisible to a handler that had been silently overtaken.  Removing and
//     re-adding puts this one back in front.
void DrelayArmCrashReporter();

// How many `STATUS_RETURN_ADDRESS_HIJACK_ATTEMPT` (0x80000033) exceptions the
// reporter has seen and passed through.  A trip is the runtime suspending a
// managed thread for a collection under CET -- routine, not a fault, and no
// longer written to `crash.txt` (see DrelayCrash.cpp: writing a report for one
// used to kill the process).  The number is the evidence that the pass-through
// is live and a measure of how hard the run is collecting.  Safe to call from any
// thread; the handler only ever increments it.
unsigned int DrelayHijackTripCount();

// Removes both handlers and restores the filter that `DrelayInstallCrashReporter`
// replaced.  Called from `DllMain(DLL_PROCESS_DETACH)` on an explicit unload --
// the handler addresses live in this image, and a process that frees the DLL
// without this dispatches its next exception into unmapped memory (measured: the
// process then dies with `0xC0000409`).  The game never unloads the DLL; the ABI
// self-test does.  Not for use while a fault is being handled.
void DrelayDetachCrashReporter();

} // namespace drelay

#endif // DRELAY_CRASH_H
