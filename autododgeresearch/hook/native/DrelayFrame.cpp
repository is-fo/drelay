// DrelayFrame.cpp -- patching the targets, and Spike 1.3's live sample.
//
// WHY THE PATCHES ARE APPLIED HERE AND NOT IN THE JIT CALLBACK
// -----------------------------------------------------------
// The EE copies the JIT's writable output into executable memory after
// compileMethod returns, so the `nativeEntry` the detour captured is the address
// we *expect* to execute but is not guaranteed to be the address that will
// (HOOK-ARCHITECTURE.md section 4.4).  Two consequences follow, and both are
// design decisions rather than caution:
//
//   * patching from inside compileMethod would be a write to code the JIT is
//     still finishing, under the JIT's own lock, on whichever thread happened to
//     compile the method;
//   * so the address is *queued* and applied later, by one thread, in one pass,
//     after the process is past startup.
//
// That also gives the spike its stability test for free: the address we captured
// is recorded in the shared block, and if the address we patch differs from it,
// the "nativeEntry is the executing address" assumption is wrong and the log
// says so -- which is exactly the third unverified assumption Spike 1.2 exists
// to settle.

#include "DrelayFrame.h"
#include "DrelayGlobals.h"
#include "DrelayInstall.h"
#include "DrelayLog.h"

#include <MinHook.h>

#include <stdio.h>
#include <string.h>

// `std::isfinite` for the helper's argument check: the native side and the managed
// side must agree on what "a sane float" means, because a disagreement shows up as a
// mismatch and would be read as a broken ABI.
#include <cmath>

namespace drelay {

namespace {

constexpr uint32_t kMaxPatched = DRELAY_MAX_TARGETS;

struct PatchRecord {
    char     name[DRELAY_TARGET_NAME_LEN];
    void*    target = nullptr;      // the address the JIT reported
    void*    trampoline = nullptr;  // MinHook's passthrough
    uint8_t* original = nullptr;    // first bytes, captured before the patch
    uint32_t originalLen = 0;
    bool     enabled = false;
};

PatchRecord g_patches[kMaxPatched] = {};
uint32_t    g_patchRecords = 0;

// MinHook hands back a trampoline whose type depends on the target's signature,
// so each signature keeps its own typed pointer rather than one `void*` that
// every detour would have to reinterpret.  One typed slot per detour body is the
// whole cost of not having a cast that is wrong in a way the compiler cannot see.
//
// THE FIRST PARAMETER IS `this`, AND IT IS NOT OPTIONAL.
//
// `DarzaWindow.Update` is an INSTANCE method with one float parameter, so on Win64
// the callee receives `this` in RCX and `total` in XMM0.  The detour and the
// trampoline were both declared `void(float)`, which reads XMM0 correctly and drops
// RCX on the floor -- and because the trampoline is the method's own prologue
// followed by a jump back into its body, that body then runs with whatever RCX the
// detour happened to leave behind.
//
// Measured, twice, on two builds and two pids: the process dies at exactly
// `nativeEntry + 0xB7` with `ACCESS_VIOLATION, read of 0x3F` -- a fixed offset and
// a fixed bogus base of zero, which is a deterministic wrong `this` and not a torn
// instruction.  It is the same class of defect as section 10's
// `getVersionIdentifier`: a hook whose declared signature does not match the
// callee's ABI.  Carrying `this` explicitly is what makes the trampoline call the
// real method with the real receiver.
typedef void (__cdecl *FrameTickFn)(void* self, float total);
FrameTickFn g_frameTickTrampoline = nullptr;
int g_frameFirstTickSummaryDone = 0;

// ---------------------------------------------------------------------------
// THE FRAME READ PATH, AND WHY IT IS NATIVE.
//
// THE DETOUR USED TO CALL MANAGED CODE, AND THE RUNTIME KILLS THE PROCESS FOR IT.
// An `[UnmanagedCallersOnly]` managed callback was called from here through its
// reverse-P/Invoke transition stub.  That is refused, deliberately and at a fixed
// address: the frame thread is INSIDE managed code when the detour intercepts it --
// between the managed caller of `DarzaWindow.Update` and its body -- so it is
// already in cooperative GC mode with live managed frames below the detour, while a
// reverse P/Invoke asserts that its caller is unmanaged.  coreclr's
// `JIT_ReversePInvokeEnterRare` therefore calls `ReversePInvokeBadTransition`, which
// fail-fasts with `COR_E_EXECUTIONENGINE` (0x80131506) via `TerminateProcess` -- no
// exception an in-process handler can see, and no managed body ever entered.
// Measured on six dumps across three builds: `coreclr+0x244839` every time, with
// `FrameTickDetour` on the faulting stack and the game's own `DarzasDominion.dll`
// frame below it.
//
// So the read is done here, natively, and it is SAFE FOR THE REASON THE CALLBACK WAS
// SUPPOSED TO BE: this thread is in cooperative GC mode, so it cannot be suspended
// mid-read and the GC cannot move the objects the read walks.  It is also the same
// read the arming cross-check performs as its production path, so the path that is
// exercised against a live world is the path that runs every frame.
//
// THE TWO GUARDS ARE NOT OPTIONAL, and they are the whole reason a wrong offset
// costs a counted failure instead of a wild read:
//
//   * the object at `World + playerOffset` must carry the `Player` MethodTable the
//     bootstrap published, masked because the low three bits are GC mark bits;
//   * both coordinates must pass the plausibility bound, which catches a wrong
//     offset that still lands inside a real object, where the type check cannot help.
//
// Nothing here allocates, locks, formats or throws.  The one thing it can do that a
// managed read could not is fault, so it runs inside a `__try`: a mistake costs a
// reading, not the game.

struct FrameReadInputs {
    uintptr_t worldSharedStaticAddress = 0;  // `World.Shared`, taken as an address
    uint32_t  worldPlayerOffset = 0;         // `World.Player`, an instance offset
    uint32_t  playerPositionOffset = 0;      // `Player.Position`, an instance offset
    uintptr_t playerTypeHandle = 0;          // the `Player` MethodTable
    bool      attempted = false;             // resolution has been tried, once
    bool      ready = false;                 // all four above are real
};

FrameReadInputs g_frameRead = {};

// ---------------------------------------------------------------------------
// THE MANAGED FRAME HELPER -- Phase 2's mechanism test.
//
// THE DETOUR ALREADY CALLS MANAGED CODE, AND THE COMMENT ABOVE IS ABOUT A DIFFERENT
// THING.  What was refused was the `[UnmanagedCallersOnly]` TRANSITION STUB -- a
// reverse P/Invoke.  What the detour has done on every frame since is call
// `g_frameTickTrampoline`, which is the target's own prologue followed by a `jmp`
// into its JIT-compiled body: a plain call into managed code from this thread, which
// coreclr accepts.  Measured at 209 897 calls in one hour, across the collections the
// game's own log records, with no fault.
//
// So the question Phase 2 has to answer is narrower, and it is the one this code
// exists to ask: is a helper of OURS -- one that allocates, and therefore triggers
// collections while it is on the stack -- safe on this thread?  The hypothesis is
// PHASE2-SCOPE.md section 3: when the GC walks that stack it finds the helper's JIT
// information from its IP, unwinds to a return address that belongs to no managed
// code range, finds no transition frame there, and stops -- leaving every managed
// frame *outside* this detour unscanned.  The synthetic host's cadence loop holds an
// object that only its own caller frame references, and reports whether it survived.
//
// IT IS OFF BY DEFAULT, AND THAT IS NOT CAUTION FOR ITS OWN SAKE.  The helper
// allocates 64 KB per frame on purpose; leaving it on would put that on the game's
// frame budget for a measurement the game does not need.  `DRELAY_HOOK_FRAME_HELPER=1`
// -- set by the launcher's `--frame-helper` -- is the only thing that turns it on.
//
// THE SIGNATURE IS ALL-PRIMITIVE AND OURS.  `int32 (int64, float)` is the platform
// convention on x64, so the managed side and this side agree without either decoding a
// game method's signature out of metadata -- which is the ABI hazard that has cost
// this project three faults.  The helper is NOT `[UnmanagedCallersOnly]`; the
// bootstrap refuses to publish one that is, because that would be the refused route.
typedef int32_t (__cdecl *FrameHelperFn)(int64_t frameIndex, float total);

FrameHelperFn g_frameHelper = nullptr;
bool      g_frameHelperEnabled = false;
bool      g_frameHelperEnabledResolved = false;
bool      g_frameHelperResolved = false;
uint64_t  g_frameHelperCalls = 0;
uint64_t  g_frameHelperMismatches = 0;
uint32_t  g_frameHelperFaults = 0;

bool ReadFrameHelperEnabled() {
    if (g_frameHelperEnabledResolved) {
        return g_frameHelperEnabled;
    }
    g_frameHelperEnabledResolved = true;
    char value[8] = {};
    g_frameHelperEnabled =
        GetEnvironmentVariableA("DRELAY_HOOK_FRAME_HELPER", value, sizeof(value)) != 0 &&
        value[0] == '1';
    return g_frameHelperEnabled;
}

// Phase 2's other mechanism test: patch `frameTick` with a managed replacement instead
// of with this file's detour.  A plain `DRELAY_` name would not do -- the launcher
// deletes inherited `DRELAY_HOOK_*` and sets its own, which is what makes an
// operator's shell unable to change the patch -- so this is read the same way the
// helper switch is, from a name the launcher owns.
bool ReadFrameReplacementEnabled() {
    static int resolved = -1;
    static bool enabled = false;
    if (resolved >= 0) {
        return enabled;
    }
    resolved = 1;
    char value[8] = {};
    enabled = GetEnvironmentVariableA("DRELAY_HOOK_FRAME_REPLACEMENT", value, sizeof(value)) != 0 &&
              value[0] == '1';
    return enabled;
}

void ResolveFrameHelperInputs() {
    g_frameHelperResolved = true;
    if (g_shared == nullptr) {
        return;
    }
    const uint64_t published = g_shared->managedFrameHelper;
    if (published == 0) {
        DrLog(DRLOG_WARN, "the frame helper is enabled but the bootstrap published no address; "
                          "no managed code will be called from the frame detour");
        return;
    }
    g_frameHelper = reinterpret_cast<FrameHelperFn>(static_cast<uintptr_t>(published));
    DrLog(DRLOG_INFO, "managed frame helper published at %p; the frame detour will call it "
                      "every frame (this is Phase 2's mechanism test)",
          reinterpret_cast<void*>(static_cast<uintptr_t>(published)));
}

// A fault inside the helper must cost a frame, not the game, exactly as the native
// read does.  The `__try` is here rather than around the whole detour so that the
// trampoline call and the ring store stay outside it -- a mistake in this call is the
// only thing that should be caught.
int32_t FrameHelperCallGuarded(int64_t frameIndex, float total) {
    __try {
        return g_frameHelper(frameIndex, total);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        g_frameHelperFaults += 1;
        static unsigned int once = 0;
        DrLogOnce(&once, DRLOG_ERR, "the managed frame helper raised; the frame continued");
        return 0;
    }
}


// One entry of the bootstrap's offset table, by name prefix.  A static field's pair
// is an ADDRESS, split across `managedStaticBase` (high) and `managedOffset` (low);
// an instance field's pair is an ordinary offset.  See DrelayFieldOffset.
bool ReadPublishedOffset(const char* name, uint64_t* value) {
    if (g_shared == nullptr) {
        return false;
    }
    const size_t wanted = strlen(name);
    const uint32_t count = g_shared->offsetCount;
    for (uint32_t i = 0; i < count && i < DRELAY_MAX_READS; ++i) {
        const DrelayFieldOffset& o = g_shared->offsets[i];
        if (o.managedValid == 0) {
            continue;
        }
        // A PREFIX COMPARISON, because the published name carries its provenance:
        // the static field is `World.Shared (static address)`, so that a reader of
        // `offsets.csv` can see which of the two encodings applies without having to
        // consult `is_static` first.
        if (strncmp(o.name, name, wanted) != 0) {
            continue;
        }
        *value = (static_cast<uint64_t>(o.managedStaticBase) << 32) | o.managedOffset;
        return true;
    }
    return false;
}

// Resolved once, on the first frame that finds the gate open, and never re-read:
// the offsets and the type identity are properties of the BUILD.  The WORLD is not,
// which is why the static field is re-dereferenced on every frame (rule 1: never
// cache a raw object pointer across anything).
bool ResolveFrameReadInputs() {
    uint64_t worldAddress = 0;
    uint64_t playerOffset = 0;
    uint64_t positionOffset = 0;

    if (!ReadPublishedOffset("World.Shared", &worldAddress) ||
        !ReadPublishedOffset("World.Player", &playerOffset) ||
        !ReadPublishedOffset("Player.Position", &positionOffset) ||
        g_shared->managedPlayerTypeHandle == 0) {
        DrLog(DRLOG_ERR, "the frame read path cannot resolve its inputs (World.Shared, World.Player, "
                         "Player.Position, or the Player MethodTable is unpublished), so it will not "
                         "read anything");
        DrLogFlushNow();
        return false;
    }

    g_frameRead.worldSharedStaticAddress = static_cast<uintptr_t>(worldAddress);
    g_frameRead.worldPlayerOffset = static_cast<uint32_t>(playerOffset);
    g_frameRead.playerPositionOffset = static_cast<uint32_t>(positionOffset);
    g_frameRead.playerTypeHandle = static_cast<uintptr_t>(g_shared->managedPlayerTypeHandle);
    g_frameRead.ready = true;
    DrLog(DRLOG_INFO, "frame read path armed: World.Shared @ %p, World.Player +%u, Player.Position "
                      "+%u, Player MethodTable %p",
          reinterpret_cast<void*>(g_frameRead.worldSharedStaticAddress),
          g_frameRead.worldPlayerOffset, g_frameRead.playerPositionOffset,
          reinterpret_cast<void*>(g_frameRead.playerTypeHandle));
    DrLogFlushNow();
    return true;
}

// The `Player` MethodTable comparison, with the GC's mark bits masked off.
bool IsInstanceOfNative(uintptr_t reference, uintptr_t expectedTypeHandle) {
    if (reference == 0 || expectedTypeHandle == 0) {
        return false;
    }
    const uintptr_t methodTable = *reinterpret_cast<const uintptr_t*>(reference);
    if (methodTable == 0) {
        return false;
    }
    return (methodTable & ~static_cast<uintptr_t>(0x7)) == expectedTypeHandle;
}

// The two coordinates are tile indices and the client's own grid is a few hundred
// tiles a side.  Deliberately loose: it exists to reject a misread pointer, not to
// validate gameplay, and a bound tight enough to matter would reject a legitimate
// teleport or a large map.
//
// THE RANGE TEST IS ALSO THE NaN AND INFINITY TEST, which is why there is no
// `isnan`/`isinf` here: every comparison against NaN is false, so a NaN fails both
// bounds, and +/-infinity fails one of them.  This still mirrors the managed
// `IsPlausibleCoordinate` it replaces -- the cross-check and the frame read must
// agree about what "plausible" means, or the production path would be reading
// through values the check never approved.
bool IsPlausibleCoordinate(float value) {
    return value > -100000.0f && value < 100000.0f;
}

void NativeFrameRead() {
    const uintptr_t world =
        *reinterpret_cast<const uintptr_t*>(g_frameRead.worldSharedStaticAddress);
    if (world == 0) {
        return;
    }

    const uintptr_t player =
        *reinterpret_cast<const uintptr_t*>(world + g_frameRead.worldPlayerOffset);
    if (!IsInstanceOfNative(player, g_frameRead.playerTypeHandle)) {
        g_shared->managedReadFailures = g_shared->managedReadFailures + 1;
        return;
    }

    const float* position =
        reinterpret_cast<const float*>(player + g_frameRead.playerPositionOffset);
    const float x = position[0];
    const float y = position[1];

    if (IsPlausibleCoordinate(x) && IsPlausibleCoordinate(y)) {
        g_shared->managedReadValue = x;
        g_shared->managedReadValid = 1;
        g_shared->managedReadCount = g_shared->managedReadCount + 1;
    } else {
        g_shared->managedReadFailures = g_shared->managedReadFailures + 1;
    }
}

// A `__try` OF ITS OWN, AND NOT BECAUSE THE READ IS EXPECTED TO FAULT.
//
// The read dereferences three pointers that belong to a running game, and the one
// thing a native read can do that a managed read could not is fault: a scene change
// that nulls `World.Shared` between the load and the dereference is a torn read, not
// a wrong number.  SEH belongs around exactly that and nothing else, which also
// keeps `FrameTickDetour` itself free of it -- MinHook's trampoline call, the QPC
// reads and the ring store have no business inside a `__try`.
void NativeFrameReadGuarded() {
    __try {
        NativeFrameRead();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // Counted, not logged per occurrence: a faulting read would otherwise
        // write 60 log lines a second into a ring of 256.
        g_frameReadFailures.fetch_add(1, std::memory_order_relaxed);
        g_shared->managedReadFailures = g_shared->managedReadFailures + 1;
        static unsigned int once = 0;
        DrLogOnce(&once, DRLOG_ERR, "the native frame read raised; the frame continued");
    }
}

// The frame-tick detour.  It is deliberately tiny: a QPC read, a counter, the read
// above when the gate is open, and a store into the ring.  No allocation, no locks,
// no formatting, and no call into managed code -- everything that would cost frame
// time is deferred to the flusher.
//
// It CALLS THE TRAMPOLINE FIRST AND UNCONDITIONALLY.  "Observing only" means
// observing the real method, not replacing it: a detour that returned without
// calling through would silently disable the game's own frame update, which is
// the single most destructive thing this whole file could do.  The trampoline is
// MinHook's copy of the overwritten prologue followed by a jump back into the
// original body, so calling it runs the real method exactly once.
//
// The `float total` argument is DarzaWindow::Update's frame delta.  It is used as
// a liveness signal on its own: a plausible ~1/60 s value, once per frame, is
// evidence that the detour is entering the real method and not, say, a
// same-shaped function that happened to be next in memory.
//
// `self` is the receiver and must be forwarded unchanged -- see the note on
// FrameTickFn.  It is not used for the read either: `World.Shared` is a STATIC
// field, so the read path reaches the world without it, which is why the read can
// live here rather than needing the receiver to be managed again.
void __cdecl FrameTickDetour(void* self, float total) {
    if (g_framePaused.load(std::memory_order_relaxed) != 0) {
        if (g_frameTickTrampoline != nullptr) {
            g_frameTickTrampoline(self, total);
        }
        return;
    }

    LARGE_INTEGER t0 = {};
    QueryPerformanceCounter(&t0);

    const uint64_t calls = g_frameCalls.fetch_add(1, std::memory_order_relaxed) + 1;
    if (g_frameFirstTick == 0) {
        g_frameFirstTick = GetTickCount();
    }

    const bool sampling = (g_shared->cfgFlags & DRELAY_CFG_FRAME_SAMPLE) != 0;

    // PUBLISHED BEFORE THE READ, AND THAT ORDER IS ITSELF A FIX.
    //
    // This counter used to be written after the read -- after the managed call that
    // used to be here -- so every failure inside the read reported `frame calls: 0`,
    // which reads as "the detour never ran".  In the run that produced this fix the
    // detour had been running for about a second before it died, and the block said
    // zero.  An instrument whose blind spot is exactly the failure it is consulted
    // about is the defect PHASE1-HANDOFF.md section 8 is about.
    if (sampling) {
        g_shared->frameCalls = static_cast<uint32_t>(calls);
    }

    // THE GATE, READ FROM THE BLOCK THE ARMING PROBE PUBLISHES.
    //
    // The detour is entered from the first frame after the target is patched.  Until
    // the probe has cross-checked the measured offsets against a live world, walking
    // the pointer chain could dereference a null `World.Shared`, so the read stays
    // closed and the entry is counted instead.
    if (g_shared->managedArmState == DRELAY_ARM_ARMED) {
        if (!g_frameRead.attempted) {
            g_frameRead.attempted = true;
            ResolveFrameReadInputs();
        }
        if (g_frameRead.ready) {
            NativeFrameReadGuarded();
        }
    } else {
        g_shared->managedFrameWaits = g_shared->managedFrameWaits + 1;
    }

    // THE MECHANISM TEST, WHEN IT IS ASKED FOR.  It is deliberately NOT inside the
    // arming gate: the question is whether managed code can run here at all, and a
    // run whose world never appears would otherwise say nothing about it.
    if (ReadFrameHelperEnabled()) {
        if (!g_frameHelperResolved) {
            ResolveFrameHelperInputs();
        }
        if (g_frameHelper != nullptr) {
            const int64_t index = static_cast<int64_t>(calls);
            const int32_t returned = FrameHelperCallGuarded(index, total);

            // The helper returns a value this side can predict from the arguments it
            // passed, so a correct return proves the ARGUMENTS crossed the boundary --
            // not merely that some call happened.  The sign carries the managed side's
            // own verdict on the float it received, so a NaN or a negative argument is
            // reported as a mismatch rather than as a plausible-looking number.
            const bool sane = std::isfinite(total) && total >= 0.0f;
            const int64_t mixed = (index ^ 0x5A5A5A5ALL) & 0x7FFFFFFFLL;
            const int32_t expected = sane ? static_cast<int32_t>(mixed)
                                          : -static_cast<int32_t>(mixed);
            g_frameHelperCalls += 1;
            if (returned != expected) {
                g_frameHelperMismatches += 1;
                static unsigned int mismatchOnce = 0;
                DrLogOnce(&mismatchOnce, DRLOG_ERR,
                          "the managed frame helper returned a value this side did not predict "
                          "from the arguments it passed -- the arguments did not survive the "
                          "boundary");
            }
            if (g_frameHelperCalls == 1) {
                DrLog(DRLOG_INFO,
                      "frame helper entered: frameIndex %lld, total %.4f, returned %d (expected %d)",
                      static_cast<long long>(index), static_cast<double>(total),
                      static_cast<int>(returned), static_cast<int>(expected));
            } else if ((g_frameHelperCalls % 256) == 0) {
                DrLog(DRLOG_INFO, "frame helper: %llu call(s), %llu mismatch(es), %u native fault(s)",
                      static_cast<unsigned long long>(g_frameHelperCalls),
                      static_cast<unsigned long long>(g_frameHelperMismatches),
                      g_frameHelperFaults);
            }
        }
    }

    LARGE_INTEGER freq = {};
    LARGE_INTEGER t1 = {};
    QueryPerformanceCounter(&t1);
    QueryPerformanceFrequency(&freq);
    const uint32_t us = (freq.QuadPart > 0)
        ? static_cast<uint32_t>(((t1.QuadPart - t0.QuadPart) * 1000000) / freq.QuadPart)
        : 0;

    // Rolling maximum, so one hitch is not averaged out of existence.
    uint32_t best = g_frameMaxUs.load(std::memory_order_relaxed);
    while (us > best && !g_frameMaxUs.compare_exchange_weak(best, us)) {
    }

    if (sampling) {
        const uint32_t slot = g_shared->frameWrite % DRELAY_FRAME_RING_ROWS;
        DrelayFrameRow* row = &g_shared->frames[slot];
        row->argTotal = total;
        row->elapsedUs = us;
        row->qpc = static_cast<uint64_t>(t0.QuadPart);
        row->readFailures = g_shared->managedReadFailures;
        row->readOk = g_shared->managedReadValid;
        row->value = static_cast<float>(g_shared->managedReadValue);
        row->seq = g_shared->frameWrite + 1;
        if (g_shared->frameWrite >= DRELAY_FRAME_RING_ROWS) {
            g_shared->frameOverflow = g_shared->frameOverflow + 1;
        }
        g_shared->frameWrite = g_shared->frameWrite + 1;
    }

    // Observation is finished; now let the game's own update run.  Last, so the
    // measurement is of our overhead and not of the game's frame.  `self` goes back
    // in RCX exactly as the callee expects it.
    if (g_frameTickTrampoline != nullptr) {
        g_frameTickTrampoline(self, total);
    }

    // One summary line per run, after enough frames that the numbers mean
    // something.  The argument is `TotalGameTime.TotalMilliseconds`, so the evidence
    // that this is really DarzaWindow::Update is the *increment* between calls --
    // ~16.7 ms at 60 Hz -- and not the raw value, which is a running total and was
    // previously printed as though it were a per-frame delta in seconds.
    static float lastTotal = 0.0f;
    if (calls == 120) {
        const float deltaMs = (lastTotal > 0.0f) ? (total - lastTotal) : 0.0f;
        DrLog(DRLOG_INFO,
              "frame tick at call 120: argTotal=%.1f ms, frame delta=%.3f ms (%.1f Hz), "
              "worst call so far %u us, frame reads %u ok / %u failed",
              total, deltaMs, (deltaMs > 0.0f) ? (1000.0f / deltaMs) : 0.0f,
              g_frameMaxUs.load(),
              g_shared->managedReadCount, g_shared->managedReadFailures);
        g_frameFirstTickSummaryDone = 1;
    }

    // One line for the helper's own accounting, at the same point in the run and for
    // the same reason: "it was enabled" and "it ran" are different claims, and this is
    // the one that says which happened.
    if (calls == 120 && ReadFrameHelperEnabled()) {
        DrLog(DRLOG_INFO,
              "frame helper accounting at call 120: %llu call(s), %llu mismatch(es), %u native fault(s), "
              "address %p",
              static_cast<unsigned long long>(g_frameHelperCalls),
              static_cast<unsigned long long>(g_frameHelperMismatches),
              g_frameHelperFaults, reinterpret_cast<void*>(g_frameHelper));
    }
    lastTotal = total;
}

} // namespace

bool DrelayDetoursInit() {
    const MH_STATUS st = MH_Initialize();
    if (st != MH_OK && st != MH_ERROR_ALREADY_INITIALIZED) {
        DrLog(DRLOG_ERR, "MH_Initialize returned %d", static_cast<int>(st));
        return false;
    }
    DrLog(DRLOG_INFO, "MinHook initialised");
    return true;
}

void DrelayDetoursUninit() {
    for (uint32_t i = 0; i < g_patchRecords; ++i) {
        if (g_patches[i].enabled && g_patches[i].target != nullptr) {
            MH_DisableHook(g_patches[i].target);
        }
    }
    if (g_patchRecords > 0) {
        MH_Uninitialize();
        DrLog(DRLOG_INFO, "MinHook uninitialised (%u patch(es) disabled)", g_patchRecords);
    }
}

// The record for an address, or nullptr when it has not been patched.  This is what
// makes a second pass safe: a target that is already hooked is skipped rather than
// hooked twice, and the record list the uninstall walks is left intact.
static PatchRecord* FindPatch(void* target) {
    for (uint32_t i = 0; i < g_patchRecords; ++i) {
        if (g_patches[i].target == target) {
            return &g_patches[i];
        }
    }
    return nullptr;
}

// ONE PATCH PASS.  `initialPass` selects the pass at the close of the discovery window
// -- the one that polls the arming probe and prints the "patching may go live" line --
// from a later pass, which must leave the record list alone.  Everything else is shared
// deliberately: the detour selection, the trampoline publication that has to precede
// the enable, and the enable itself are the parts that must not differ between a patch
// at t+5 s and one at t+55 s.
static uint32_t PatchTargetsPass(bool initialPass) {
    uint32_t patched = 0;

    // THE FRAME READ PATH NEEDS NOTHING PREPARED BEFORE A PATCH GOES LIVE, AND THAT
    // IS THE POINT OF THIS ROUND RATHER THAN AN OMISSION.
    //
    // Enabling a hook on `DarzaWindow.Update` makes the very next frame reach
    // `FrameTickDetour`.  The read it performs is native, guarded by the offset table
    // and the `Player` MethodTable that the resolve callback published seconds
    // earlier, and its gate is a field the arming probe writes.  So there is no
    // compiled code to pre-warm, no address to publish first, and no ordering between
    // the probe's cadence and this loop that anything depends on.  The previous round
    // needed both a pre-compile and an explicit probe call here because the detour
    // called managed code; the detour does not any more.
    //
    // The probe is still called, and still worth calling: it is what publishes the
    // arm-state transition, so running it here means the first frames after the patch
    // see the freshest verdict rather than one up to a poll interval old.
    if (initialPass) {
        DrelayPollArmProbe();
        DrLog(DRLOG_INFO, "the frame read is native and needs no managed entry point; patching may "
                          "go live");
        DrLogFlushNow();
    }

    // A diagnostic switch, and it exists because it is the only cheap way to tell
    // two very different failures apart.  With `DRELAY_NO_PATCH=1` the JIT detour
    // is installed exactly as normal but no target is patched, so a crash that
    // persists is in the detour and a crash that disappears was in the patch.
    // Without this the two are indistinguishable from a log that simply stops.
    //
    // IT IS `DRELAY_NO_PATCH`, NOT `DRELAY_HOOK_NO_PATCH`, AND THAT IS THE WHOLE
    // POINT.  The launcher builds the child's environment by deleting every
    // variable whose name begins with `DRELAY_HOOK_`, so this switch was
    // UNREACHABLE through the only supported way to start the game: setting it in
    // the operator's shell silently did nothing, and the control that was supposed
    // to separate "the patch" from "the detour" could never run.  That is
    // PHASE1-HANDOFF.md section 4 trap 1 for the third time in this project; the
    // convention is `DRELAY_*` for anything the operator sets.
    char noPatch[8] = {};
    if (GetEnvironmentVariableA("DRELAY_NO_PATCH", noPatch, sizeof(noPatch)) != 0 && noPatch[0] == '1') {
        DrLog(DRLOG_WARN, "DRELAY_NO_PATCH=1: the JIT detour is installed but no target is "
                          "patched (diagnostic mode)");
        return 0;
    }

    // Walk the shared target table directly: it is the single record of what the
    // JIT reported and what the bootstrap pinned, so joining on it rather than on
    // a second local list keeps "which target is this" to one answer.  Discovery
    // order is not table order, so the table is walked by index and only the
    // entries marked found are patched.
    for (uint32_t i = 0; i < g_shared->targetCount && g_patchRecords < kMaxPatched; ++i) {
        DrelayTargetHash* t = &g_shared->targets[i];
        if (t->found == 0 || t->nativeEntry == 0) {
            continue;
        }

        // ALREADY HOOKED, SO THERE IS NOTHING TO DO.  Only a second pass can find this,
        // and without it the late pass would try to re-create a hook on a live target:
        // `MH_CreateHook` refuses, the error would be logged every second, and the record
        // list the uninstall walks would be corrupted by a reset.
        if (FindPatch(reinterpret_cast<void*>(t->nativeEntry)) != nullptr) {
            continue;
        }

        // The dispatch is by name, and a target with no detour body is reported
        // rather than guessed at.  `frameTick` is the only signature Phase 1
        // implements, because Spike 1.3 asks for exactly one target patched and
        // a wrong-shaped forwarder on a per-frame method is a way to corrupt the
        // game for no gain.
        //
        // AND `frameTick` HAS A SECOND WAY TO BE PATCHED, WHICH IS PHASE 2'S
        // MECHANISM TEST.  With `DRELAY_HOOK_FRAME_REPLACEMENT=1` the destination of
        // MinHook's `jmp` is not this file at all: it is a managed method with the
        // target's own signature, so the game's `call` becomes a managed-to-managed
        // call and no native frame ever interrupts the managed stack.  See the note
        // on `managedFrameReplacement` in DrelayHookShared.h for why that was worth
        // building after the helper route measured as flaky.
        bool managedReplacement = false;
        void* detour = nullptr;
        volatile uint64_t* trampolineSlot = nullptr;
        if (strcmp(t->name, "frameTick") == 0) {
            const uint64_t replacement = (g_shared != nullptr) ? g_shared->managedFrameReplacement : 0;
            if (ReadFrameReplacementEnabled() && replacement != 0) {
                detour = reinterpret_cast<void*>(static_cast<uintptr_t>(replacement));
                managedReplacement = true;
                trampolineSlot = &g_shared->managedFrameTrampoline;
            } else {
                detour = reinterpret_cast<void*>(&FrameTickDetour);
            }
        } else if (strcmp(t->name, "collision") == 0) {
            // `Projectile.HitCharacter` HAS NO NATIVE DETOUR BODY, and now it has a reason
            // to be patched anyway: the managed replacement is the observation point for
            // Deliverable 2.3, and there is nothing for a native body to do there -- the
            // comparison needs the projectile's fields and the sensor's own state, which
            // are managed things.
            const uint64_t replacement = (g_shared != nullptr) ? g_shared->managedCollisionReplacement : 0;
            if (ReadFrameReplacementEnabled() && replacement != 0) {
                detour = reinterpret_cast<void*>(static_cast<uintptr_t>(replacement));
                managedReplacement = true;
                trampolineSlot = &g_shared->managedCollisionTrampoline;
            }
        }
        if (detour == nullptr) {
            // ONLY THE FIRST PASS SAYS SO.  A target with no body for this phase is
            // reported once, at the window's close, where a reader is looking for it;
            // the late pass runs every second and would otherwise repeat this line for
            // the rest of the session.
            if (initialPass) {
                DrLog(DRLOG_INFO,
                      "target '%s' verified at %p and NOT patched: its detour body is Phase 2's "
                      "(this spike patches one target on purpose)", t->name,
                      reinterpret_cast<void*>(t->nativeEntry));
            }
            continue;
        }

        PatchRecord& rec = g_patches[g_patchRecords];
        memcpy(rec.name, t->name, sizeof(rec.name));
        rec.name[sizeof(rec.name) - 1] = '\0';
        rec.target = reinterpret_cast<void*>(t->nativeEntry);

        const MH_STATUS st = MH_CreateHook(rec.target, detour, &rec.trampoline);
        if (st != MH_OK) {
            DrLog(DRLOG_ERR, "MH_CreateHook failed for '%s' at %p (status %d) -- not patched",
                  rec.name, rec.target, static_cast<int>(st));
            continue;
        }

        // PUBLISHED BEFORE THE HOOK IS ENABLED, AND THAT ORDER IS LOAD-BEARING.  The
        // very first `call` to the target has to find a trampoline, because the
        // replacement's only way to run the original is to call it.  A replacement that
        // saw zero would have to skip the original, and skipping it does not fail
        // loudly -- it stops the game's frame update, which is the most destructive
        // thing this file could do.
        if (managedReplacement && trampolineSlot != nullptr) {
            *trampolineSlot = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(rec.trampoline));
            DrLog(DRLOG_INFO,
                  "'%s' will be patched with the MANAGED REPLACEMENT at %p; its way back into the "
                  "original is the trampoline at %p, published before the hook is enabled",
                  rec.name, detour, rec.trampoline);
        }

        const MH_STATUS en = MH_EnableHook(rec.target);
        if (en != MH_OK && en != MH_ERROR_ENABLED) {
            DrLog(DRLOG_ERR, "MH_EnableHook failed for '%s' at %p (status %d) -- not patched",
                  rec.name, rec.target, static_cast<int>(en));
            MH_RemoveHook(rec.target);
            continue;
        }

        rec.enabled = true;
        ++g_patchRecords;
        ++patched;
        g_shared->targetsPatched = patched;
        if (strcmp(rec.name, "frameTick") == 0) {
            g_frameTickTrampoline = reinterpret_cast<FrameTickFn>(rec.trampoline);
        }
        DrLog(DRLOG_INFO, "PATCHED '%s' at %p (trampoline %p)", rec.name, rec.target, rec.trampoline);
    }

    if (patched > 0) {
        g_shared->nativeStatus |= DRELAY_ST_PATCHED;
    }
    if (initialPass) {
        DrLog(patched > 0 ? DRLOG_INFO : DRLOG_ERR,
              "%u of %u target(s) patched", patched, g_shared->targetCount);
    }
    return patched;
}

// The pass at the close of the discovery window.  It owns the reset of the record list,
// because it is the one that starts it.
uint32_t DrelayPatchTargets() {
    g_patchRecords = 0;
    return PatchTargetsPass(true);
}

// THE LATE PASS, AND THE SESSION THAT MADE IT NECESSARY.
//
// The patch decision used to be a single pass at the close of the fixed thirty-second
// discovery window, so a target the game compiled *after* that window was matched,
// reported as found by the target-size table -- and never patched.  On the
// 2026-10-12 21:22 session the operator reached `GameScene` at 21:23:10 and the window
// closed at 21:23:16, so `Projectile.HitCharacter` missed it by seconds: the run
// recorded `collisions: 0 decision(s)` with a perfectly healthy sensor around it,
// because the method was never hooked rather than because nothing collided.
//
// That is PHASE2-SCOPE.md section 6.2's defect one level down -- the window's count
// standing in for the run's -- and the fix has the same shape: keep watching, and act on
// what arrives.  The compileMethod detour is still installed and still matching, so a
// target that appears later sets `found` and this pass hooks it.
uint32_t DrelayPatchNewTargets() {
    if (g_shared == nullptr) {
        return 0;
    }
    const uint32_t added = PatchTargetsPass(false);
    if (added > 0) {
        g_shared->targetsPatched = g_patchRecords;
        DrLog(DRLOG_INFO, "%u target(s) that the game compiled after the discovery window closed "
                          "were patched late; %u of %u target(s) are now patched, and this run's "
                          "comparison starts from here rather than from the window",
              added, g_patchRecords, g_shared->targetCount);
        DrLogFlushNow();
    }
    return added;
}

bool DrelayIsFrameTargetPatched() {
    for (uint32_t i = 0; i < g_patchRecords; ++i) {
        if (g_patches[i].enabled && strcmp(g_patches[i].name, "frameTick") == 0) {
            return true;
        }
    }
    return false;
}

} // namespace drelay

