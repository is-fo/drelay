// DrelayInstall.h -- the public surface of the JIT half of the hook.

#ifndef DRELAY_INSTALL_H
#define DRELAY_INSTALL_H

#include "DrelayHookShared.h"
#include "DrelayJit.h"

#include <windows.h>
#include <stdint.h>

namespace drelay {

// The tally the self-check accumulates.  `gaps` counts checks that were
// deliberately not made -- they are reported separately from passes so that
// "not checked" can never be read as "checked and fine", which is the
// distinction Phase 0 established for the fingerprint gate.
struct DrelayInstallReport {
    uint32_t passed = 0;
    uint32_t failed = 0;
    uint32_t gaps = 0;
};

// Proves which runtime is loaded and that the assembly is the pinned build.
// Returns false on any refusal; every refusal has already been logged with the
// expected and actual value, because a refusal that does not say why is
// indistinguishable from a crash.
bool DrelayRuntimeValidate(DrelayInstallReport* report);

// Replaces vtable slot 0.  Called only after DrelayRuntimeValidate returned true.
bool DrelayInstallJitDetour(DrelayInstallReport* report);

// Discovery state machine, driven by the watcher thread.
void     DrelayBeginDiscovery();
bool     DrelayDiscoveryWindowClosed();
DWORD    DrelayDiscoveryPollMs();
void     DrelayCloseDiscovery();
void     DrelayLogDiscoveryStatus();

// Waits out the operator's head start and then OPENS the discovery window.
//
// This is the only thing that opens the window, so "collection started" has one
// timestamp.  The head start now defaults to ZERO (`--collect-delay` /
// `DRELAY_HOOK_COLLECT_DELAY_MS` still honour a value), because the patch no longer
// waits for the window: a target is hooked a settle interval after it is first seen,
// from the moment the detour is installed.  See `ReadCollectDelayMs` in
// DrelayInstall.cpp for the full argument, and `DrelayPatchTargetsDuringDiscovery`.
//
// Returns false when a shutdown was requested during the wait, so the caller leaves
// its discovery loop at once and falls through to the evidence writer.  The window is
// opened even in that case, so the elapsed-time figures in the evidence are measured
// from a real timestamp.
bool     DrelayWaitForCollectDelay();

// How long the discovery window stays open -- ten seconds by default, and closed early
// the moment every target is in.  `ReadDiscoveryWindowMs` in DrelayInstall.cpp argues
// why the cap is a reporting bound and not a functional one.
DWORD    ReadDiscoveryWindowMs();

// How long the resolve callback waits for the runtime to leave the startup-hook phase
// before it enters managed code.  `--resolve-delay` / `DRELAY_HOOK_RESOLVE_DELAY_MS`,
// 100 ms by default; why it is a margin and not a signal is at the call site.
DWORD    ReadResolveDelayMs();

// How long the harness records the patched frame after the patch goes live, and
// whether the frame read is paused when that window closes.  Both are described at
// their definitions in DrelayInstall.cpp: the window is `DRELAY_HOOK_LIVE_MS` (the
// launcher's `--live-seconds`, default 5000 ms, unchanged) and the pause is now OFF by
// default -- `DRELAY_KEEP_READING=0`, or the launcher's `--pause-after-run`, asks for it.
DWORD    ReadLiveWindowMs();
bool     ReadKeepReadingAfterRun();

// Invokes the managed resolve callback, once, after the detour is installed.
//
// The bootstrap cannot do this work itself: a startup hook cannot JIT-compile
// anything (PHASE1-SCOPE.md §6.1), so loading the assembly, reflecting over it and
// building dynamic methods all have to happen after startup has completed.  This is
// called from the watcher thread at exactly that point.
void DrelayInvokeResolveCallback();

// Calls the managed arming probe, if one was published, and reports a change in
// its verdict once.
//
// Spike 1.3's success test -- read one float from the live world -- cannot run
// until the game has a world, and `World.Shared` is null until a scene loads.  The
// probe is the managed side's answer to "is there a world yet, and do the measured
// offsets describe it"; this function is the watcher thread's, and it is what makes
// the question get asked at all.
//
// IT MUST BE CALLED FROM EVERY WAIT THE WATCHER DOES.  Four call sites -- the
// collection delay, the discovery loop, the live-check wait and the idle loop --
// and the reason is that the moment a world appears is not known in advance:
// catching an operator who reaches gameplay in ten seconds and one who takes three
// minutes are the same requirement.  Once the verdict is in, each call is one
// volatile load and a return.
void DrelayPollArmProbe();

// Count of targets the bootstrap published (not the count found).
uint32_t DrelayTargetCount();

// Reads the target table out of the shared block.  Separate from validation so
// that the target list is loaded only once the block is known to be current.
void DrelayLoadTargets();

// Compares the assembly the runtime actually loaded -- which the bootstrap can
// only publish from the managed resolve callback -- against the MVID of the file
// on disk, which validation read earlier.  Returns false on a mismatch.
//
// IT HAS TO RUN AFTER `DrelayInvokeResolveCallback` AND BEFORE ANY PATCH.  The
// comparison is the one claim the fingerprint gate cannot make for itself: the
// gate proves the file is the pinned build, this proves the process is running
// that file, and only the pair rules out a swap between them.  Neither half has a
// value at the time the other does.
bool DrelayVerifyLoadedAssemblyMvid(DrelayInstallReport* report);
} // namespace drelay

#endif // DRELAY_INSTALL_H
