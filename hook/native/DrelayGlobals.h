// DrelayGlobals.h -- the process-wide state the hook keeps.
//
// There is exactly one hook per process, so this is a namespace of globals
// rather than an object graph.  It is a header rather than a .cpp because two
// translation units legitimately share every field: the JIT detour writes
// `compileCalls`/`targetsFound`, the patcher reads them, the frame hooks write
// timing, and the self-check reads all of it.

#ifndef DRELAY_GLOBALS_H
#define DRELAY_GLOBALS_H

#include "DrelayHookShared.h"
#include "DrelayJit.h"

#include <windows.h>
#include <stdint.h>
#include <atomic>

namespace drelay {

// Shutdown is requested from the exported `DrelayHook_Shutdown`, which runs on the
// injector's thread, and observed by the watcher.  It is an interlocked flag rather
// than an event, because the watcher can be blocked in Sleep and a flag needs no
// owner to reset.
//
// DECLARED HERE RATHER THAN FILE-STATIC IN DrelayMain.cpp, because the collection
// delay waits in slices precisely so it can notice this: a `Sleep(30000)` would make
// the operator's Ctrl+C wait out the whole delay before anything reacted to it.
extern volatile LONG g_shutdownRequested;

// The logger's sequence numbers assume a single shared block; nothing works
// without it, so it is checked once before anything else is attempted.
extern DrelayShared* g_shared;
extern HANDLE        g_mapHandle;
extern uint32_t      g_sessionId;
extern wchar_t       g_runDir[MAX_PATH];
extern wchar_t       g_gameDir[MAX_PATH];
extern wchar_t       g_assemblyPath[MAX_PATH];

// The JIT detour's own state.  `realCompileMethod` is the address the vtable
// slot held before we replaced it -- calling anything else here would be the
// classic recursion-into-ourselves bug.
extern DrelayICorJitCompilerVtbl* g_realCompiler;
extern void*                      g_jitObject;
extern void**                     g_jitVtable;
extern std::atomic<uint32_t>      g_compileCalls;
extern std::atomic<uint32_t>      g_compileFailed;
extern std::atomic<uint32_t>      g_targetsFound;
extern std::atomic<uint32_t>      g_patchCount;

// HOW MANY TIMES THE DETOUR WAS ENTERED, VERSUS HOW MANY OF THOSE IT ACTUALLY
// LOOKED AT, AND WHY THOSE ARE DIFFERENT NUMBERS.
//
// `g_compileCalls` counts only the entries that got past the re-entrancy guard, so
// it measures the hook's *instrumentation*, not the compilation stream.  Every
// conclusion in PHASE1-HANDOFF.md section 11 -- "the game must JIT the frame tick,
// the hook sees ~3,690 compilations, therefore the frame tick never reached the
// JIT" -- silently assumes those two are the same number.  If the guard ever
// discards an entry, the method compiled and the hook has no record of it, and
// that is indistinguishable from the method never compiling.
//
// `g_detourInvocations` is incremented at the top of the detour, BEFORE the guard.
// `g_reentrantSkips` is incremented on the guard's early-return path.  A run in
// which `invocations - calls` is non-trivial has an instrument problem, not a
// coverage problem, and the per-target verdicts from that run must not be read as
// facts about the game.
extern std::atomic<uint32_t>      g_detourInvocations;
extern std::atomic<uint32_t>      g_reentrantSkips;

// Set once the detour is installed and the discovery window has closed; the
// patcher waits on it, so no patch can be applied from inside a JIT callback.
extern HANDLE        g_discoveryDoneEvent;
extern std::atomic<int> g_discoveryClosed;

// The MVID read from the assembly file on disk, and whether that read succeeded.
//
// DECLARED HERE RATHER THAN FILE-STATIC IN DrelayInstall.cpp, BECAUSE THE SELF-CHECK
// IS THE THING THAT HAS TO REPORT IT.  The claim that matters most in this phase is
// the *comparison* between this value and the MVID the runtime reports for the
// assembly it actually loaded -- "the file is the pinned build" is Phase 0's gate,
// and "the loaded assembly is that file" is this one.  That comparison was made and
// only counted: it adjusted the report totals and wrote a log line, so a reader of
// `selfcheck.txt` alone could not see the result at all, and a refusal left a
// failure count with no line explaining it.  The values now live where the
// self-check can print them.
extern uint8_t g_fileMvid[16];
extern bool    g_fileMvidRead;

// THE MANAGED FRAME CALLBACK IS GONE, AND THIS COMMENT IS THE RECORD OF WHY.
//
// It used to be a `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]`
// managed method whose transition stub the frame detour called, on the argument
// that entering the stub puts the thread into cooperative GC mode and so makes a
// managed read legal from a native detour.
//
// That argument is backwards and coreclr enforces the opposite.  The detour runs on
// the game's frame thread while that thread is INSIDE managed code, so the thread is
// already in cooperative mode with live managed frames below the detour -- and a
// reverse P/Invoke asserts that its caller is unmanaged.  The runtime refuses it:
// `JIT_ReversePInvokeEnterRare` calls `ReversePInvokeBadTransition`, which fail-fasts
// the process with `COR_E_EXECUTIONENGINE` (0x80131506) through `TerminateProcess`,
// raising nothing an in-process handler can see.  Six dumps across three builds put
// the exception at `coreclr+0x244839` every time, with `FrameTickDetour` on the
// faulting stack and the game's own managed frame below it.
//
// The frame read is therefore NATIVE: the detour walks `World.Shared -> +Player ->
// +Position` itself, guarded by the `Player` MethodTable that the bootstrap publishes
// in `managedPlayerTypeHandle`.  See DrelayFrame.cpp for the read and
// HOOK-ARCHITECTURE.md for the rule that needs rewriting.

// Spike 1.3 liveness.  `frameCalls` is the count the self-check reports; the
// per-call cost is a rolling maximum so a single hitch is not averaged away.
extern std::atomic<uint64_t> g_frameCalls;
extern std::atomic<uint32_t> g_frameMaxUs;
extern std::atomic<uint32_t> g_frameReadFailures;
extern DWORD                 g_frameFirstTick;
extern std::atomic<int>      g_framePaused;

// State the self-check fills in and `DrelayHook_Init` reports.
struct SelfCheckReport {
    uint32_t passed;
    uint32_t failed;
    uint32_t gaps;
};
extern SelfCheckReport g_selfCheck;

// The field-offset cross-check that Phase 2 will run from inside the JIT detour.
//
// NOT IMPLEMENTED IN PHASE 1, and declared here rather than deleted so the gap is
// visible in the header a reader consults.  Phase 1 publishes offsets measured by
// managed reflection and verifies them by reading the same state through a raw
// pointer chain and requiring the two to agree; the JIT's own `getFieldOffset`
// oracle is the independent second source that Phase 2 adds, and until it exists
// the self-check reports check 7 as a GAP rather than as a pass.  A declared and
// unimplemented function would normally be a smell; here it is the record that a
// planned check has not been made.
void DrelayResolveOffsets(void* jitInfo, void* classHandle);

} // namespace drelay

#endif // DRELAY_GLOBALS_H
