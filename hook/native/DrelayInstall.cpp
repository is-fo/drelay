// DrelayInstall.cpp -- the JIT detour: validation, installation, and discovery.
//
// THE ORDER OF OPERATIONS IS THE SAFETY PROPERTY
// ----------------------------------------------
// Nothing is written to the JIT until the runtime has been proved to be the
// runtime we built for, and nothing is patched in the game until the discovery
// window has closed.  Each arrow below is a separate function so that a failure
// returns a name rather than a number:
//
//     load clrjit -> resolve getJit -> validate vtbl[0..3] are clrjit .text
//       -> read the JIT/EE GUID through vtbl[2] and compare
//       -> check the effective tiering setting
//       -> check the loaded assembly's MVID and size
//       -> ONLY NOW replace vtbl[0]
//       -> observe the compilation stream, hashing IL, for a discovery window
//       -> ONLY NOW patch the matched targets, off the JIT thread
//
// The two "ONLY NOW"s are the whole point.  A hook that validates and then
// installs has one failure mode (it did not install); a hook that installs and
// then hopes has a different one (it patched the wrong function in a live
// process), and the architecture document is explicit that the first is a far
// better failure than the second.

#include "DrelayInstall.h"
#include "DrelayCrash.h"
#include "DrelayFrame.h"
#include "DrelayGlobals.h"
#include "DrelayLog.h"
#include "DrelayPe.h"

#include <MinHook.h>

#include <wincrypt.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <atomic>

#pragma comment(lib, "version.lib")

namespace drelay {

// ---------------------------------------------------------------------------
// Definitions of the globals declared in DrelayGlobals.h.  One definition site,
// so "who owns this" has exactly one answer.

DrelayShared* g_shared = nullptr;
HANDLE        g_mapHandle = nullptr;
uint32_t      g_sessionId = 0;
wchar_t       g_runDir[MAX_PATH] = {};
wchar_t       g_gameDir[MAX_PATH] = {};
wchar_t       g_assemblyPath[MAX_PATH] = {};

DrelayICorJitCompilerVtbl* g_realCompiler = nullptr;
void*                      g_jitObject = nullptr;
void**                     g_jitVtable = nullptr;
std::atomic<uint32_t>      g_compileCalls{0};
std::atomic<uint32_t>      g_compileFailed{0};
std::atomic<uint32_t>      g_targetsFound{0};
std::atomic<uint32_t>      g_patchCount{0};
// See DrelayGlobals.h: `g_compileCalls` measures instrumentation, these two measure
// the stream.  A gap between them is an instrument fault, not a game fact.
std::atomic<uint32_t>      g_detourInvocations{0};
std::atomic<uint32_t>      g_reentrantSkips{0};

HANDLE           g_discoveryDoneEvent = nullptr;
std::atomic<int> g_discoveryClosed{0};

std::atomic<uint64_t> g_frameCalls{0};
std::atomic<uint32_t> g_frameMaxUs{0};
std::atomic<uint32_t> g_frameReadFailures{0};
DWORD                 g_frameFirstTick = 0;
std::atomic<int>      g_framePaused{0};

SelfCheckReport g_selfCheck = {};

namespace {

// The discovery window's cap.  It closed at a fixed thirty seconds, and it now closes
// at TEN -- or earlier, the moment every target is in, which is the common case and is
// what the watcher loop already did.
//
// WHAT THE WINDOW IS FOR, NOW THAT THE PATCH NO LONGER WAITS ON IT: the per-target
// verdicts an operator reads (`FOUND`/`MISSING`, the native entry, the token, the
// target-size table) are all measured over a bounded interval, and an unbounded one
// would make "we watched and it did not appear" impossible to state.  That is a
// reporting question, and ten seconds answers it as well as thirty did.  Nothing
// functional is inside it: a target compiled after the cap closes is still matched,
// still reported, and still patched by the late pass.
//
// `DRELAY_HOOK_DISCOVERY_WINDOW_MS` overrides it, and the launcher exposes it as
// `--discovery-ms`, because a machine that needs longer -- a cold disk, a slow
// self-contained start -- should be able to say so without a rebuild.
constexpr DWORD kDefaultDiscoveryWindowMs = 10000;

// The re-arming window: after the first flush a target may still be missed
// because the game has not done anything yet, so the watcher keeps re-checking
// rather than reporting a single premature failure.
constexpr DWORD kDiscoveryPollMs = 250;

// Signature of the compileMethod slot, with the raw `void*` info pointer: the
// hook passes `info` straight through to the slots it calls, so it never needs
// the full ICorJitInfo type.
typedef DrelayCorJitResult (__cdecl *CompileMethodFn)(
    void* self, void* info, DrelayCorMethodInfo* methodInfo,
    unsigned flags, uint8_t** nativeEntry, uint32_t* nativeSize);

CompileMethodFn g_realCompile = nullptr;

// Re-entrancy depth, per thread.
//
// `thread_local` IS THE OBVIOUS SPELLING AND IT IS THE WRONG ONE HERE.  In a DLL
// that is loaded at runtime -- which this one is, by injection -- `thread_local`
// with dynamic initialisation cannot be resolved at load time and the runtime
// falls back to `__dyn_tls_init`, which allocates and may call back into the
// loader on the *first touch from each thread*.  The first touch here happens
// inside `compileMethod`, on whichever thread the JIT is running, while the JIT
// holds its own lock.  That is not a safe place to allocate.
//
// So the depth is kept in a TLS slot this module owns, allocated once from the
// watcher thread before the detour can be reached.  `TlsGetValue` on a slot that
// has not been set returns null, which reads as depth zero, which is correct.
uint32_t g_entrancySlot = TLS_OUT_OF_INDEXES;

int EnterDetour() {
    if (g_entrancySlot == TLS_OUT_OF_INDEXES) {
        return 0;   // TLS unavailable: treat as re-entrant, i.e. pass straight through
    }
    const int depth = static_cast<int>(reinterpret_cast<intptr_t>(TlsGetValue(g_entrancySlot)));
    if (depth > 0) {
        // ALREADY INSIDE ON THIS THREAD: RETURN WITHOUT TOUCHING THE DEPTH.
        //
        // This used to increment and return the old depth, which the caller reads
        // as "re-entered, so skip the body" -- but the skip path (the early return
        // in HookCompileMethod) has no matching LeaveDetour, so the increment was
        // never undone.  The depth therefore grew by one for every nested
        // compilation and, because it never came back down to zero, EVERY LATER
        // COMPILATION ON THAT THREAD WAS TREATED AS RE-ENTRANT AND SILENTLY
        // DISCARDED: not counted, not hashed, not size-matched, not in the size
        // histogram, nothing.
        //
        // That is a permanent, per-thread blind spot, and its signature is exactly
        // the one section 11 spent a round misreading -- some methods are seen,
        // some are not, and "not seen" is presented as "never compiled".  Making
        // the nested entry leave the depth alone keeps the guard doing its job
        // (the body is still skipped) while the outer LeaveDetour still returns
        // the thread to zero.
        //
        // `g_reentrantSkips` counts these so a run can say how many compilations
        // the guard discarded rather than leaving it to be inferred.
        return depth;
    }
    TlsSetValue(g_entrancySlot, reinterpret_cast<void*>(static_cast<intptr_t>(1)));
    return 0;
}

void LeaveDetour() {
    if (g_entrancySlot == TLS_OUT_OF_INDEXES) {
        return;
    }
    const int depth = static_cast<int>(reinterpret_cast<intptr_t>(TlsGetValue(g_entrancySlot)));
    TlsSetValue(g_entrancySlot, reinterpret_cast<void*>(static_cast<intptr_t>(depth > 0 ? depth - 1 : 0)));
}

std::atomic<uint32_t> g_discoveryStartTick{0};
std::atomic<int>      g_discoveryTimerStarted{0};

// The operator's head start, in milliseconds, before the discovery window opens.
//
// IT DEFAULTS TO ZERO, AND IT USED TO DEFAULT TO THIRTY SECONDS.  The history is
// worth keeping, because the reasons for the delay were real and the reason it is
// gone is not that they stopped being true.
//
// The window used to open on the first compilation the detour saw, which is about
// 1.3 s into the process -- while the game is still on its loading screen.  Two of
// the four targets (`spawn` and `collision`) are compiled on first use during
// gameplay, as PHASE1-HANDOFF.md section 10 already says, so a window that closes
// before the operator has cleared the menu cannot collect them however long the
// window is.  Three live runs produced `0 of 4 targets found` for exactly this
// reason, and nothing in the console said so.
//
// WHAT THE DELAY ACTUALLY FIXED, AND WHAT IT COST.  It fixed the *reporting*: the
// window's verdicts are measuring "did this build's targets appear while we watched",
// and a window that opens at 1.3 s answers that about a loading screen.  It cost a
// fixed thirty seconds of wall clock on every run before the first target could be
// patched -- and the comment below already conceded the point, in the code's own
// words: nothing is lost during the delay, because the detour is already installed
// and `PublishMatch` records a match whenever the game compiles one.  The delay
// decided when the watcher stopped *waiting*, never when it started *looking*.
//
// SO THE ORDERING IT BOUGHT IS NOW BOUGHT SOMEWHERE ELSE.  The patch no longer waits
// for the window at all: `PatchTargetsPass` hooks a target a settle interval after
// it is first seen, from the moment the detour is installed (see
// `DrelayPatchTargetsDuringDiscovery` in DrelayFrame.cpp).  "Get the game into
// gameplay, then measure" is therefore about the *numbers*, not about the hook being
// resident, and a release run does not have to pay for it.  The knob is still here, and
// still honoured, for a run whose subject is the window's own verdicts:
// `--collect-delay` / `DRELAY_HOOK_COLLECT_DELAY_MS`.
constexpr DWORD kDefaultCollectDelayMs = 0;

DWORD ReadCollectDelayMs() {
    // `DRELAY_HOOK_`-prefixed because that namespace is the launcher's: it strips
    // every such variable from the parent environment and then sets its own, so a
    // value set by the operator's shell in that namespace would be removed while one
    // the launcher sets survives.  See PHASE1-HANDOFF.md section 4, trap 1.
    char value[32] = {};
    if (GetEnvironmentVariableA("DRELAY_HOOK_COLLECT_DELAY_MS", value, sizeof(value)) == 0 ||
        value[0] == '\0') {
        return kDefaultCollectDelayMs;
    }
    char* end = nullptr;
    const long parsed = strtol(value, &end, 10);
    if (end == value || parsed < 0) {
        DrLog(DRLOG_WARN, "DRELAY_HOOK_COLLECT_DELAY_MS='%s' is not a non-negative number of "
                          "milliseconds; using the %lu ms default", value,
              static_cast<unsigned long>(kDefaultCollectDelayMs));
        return kDefaultCollectDelayMs;
    }
    // Bounded, because this value becomes a sleep: a typo that meant "3000000" would
    // otherwise park the watcher for fifty minutes and present as a hang.
    constexpr long kMaxCollectDelayMs = 600000;
    if (parsed > kMaxCollectDelayMs) {
        DrLog(DRLOG_WARN, "DRELAY_HOOK_COLLECT_DELAY_MS=%ld exceeds the %ld ms maximum; clamping",
              parsed, kMaxCollectDelayMs);
        return static_cast<DWORD>(kMaxCollectDelayMs);
    }
    return static_cast<DWORD>(parsed);
}

// ---------------------------------------------------------------------------
// Hashing.  The IL hash must be byte-identical to the one Phase 0's
// il_index.py computed over `MethodDef.Rva`'s instruction bytes, so this is a
// plain SHA-256 over `ILCode[0..ILCodeSize)` with no header and no re-encoding
// (known-build.json's `_recipe`).

bool Sha256(const uint8_t* data, size_t len, uint8_t out[32]) {
    HCRYPTPROV prov = 0;
    HCRYPTHASH hash = 0;
    bool ok = false;
    if (CryptAcquireContextW(&prov, nullptr, nullptr, PROV_RSA_AES, CRYPT_VERIFYCONTEXT)) {
        if (CryptCreateHash(prov, CALG_SHA_256, 0, 0, &hash)) {
            if (CryptHashData(hash, data, static_cast<DWORD>(len), 0)) {
                DWORD digestLen = 32;
                ok = CryptGetHashParam(hash, HP_HASHVAL, out, &digestLen, 0) != FALSE;
            }
            CryptDestroyHash(hash);
        }
        CryptReleaseContext(prov, 0);
    }
    return ok;
}

// ---------------------------------------------------------------------------
// The target table, taken from the shared block rather than compiled in.  The
// bootstrap publishes what Phase 0 pinned; the native side never carries its own
// copy of a hash, because two copies of an identity is exactly the "second
// source of truth" that the plan's Phase 0 item 3 rejected for the constants
// file.  The token and the IL size come from the same record.

struct TargetRecord {
    char     name[DRELAY_TARGET_NAME_LEN];
    uint8_t  hash[32];
    uint32_t ilSize;
    uint32_t token;         // Phase 0's token, for the spike's cross-check
    uint32_t index;         // index into g_shared->targets
};

TargetRecord g_targets[DRELAY_MAX_TARGETS] = {};
uint32_t     g_targetCount = 0;

void LoadTargetsFromShared() {
    g_targetCount = 0;
    const uint32_t count = g_shared->targetCount;
    if (count > DRELAY_MAX_TARGETS) {
        DrLog(DRLOG_WARN, "bootstrap published %u targets; truncating to %u",
              count, DRELAY_MAX_TARGETS);
    }
    const uint32_t usable = (count > DRELAY_MAX_TARGETS) ? DRELAY_MAX_TARGETS : count;
    for (uint32_t i = 0; i < usable; ++i) {
        const DrelayTargetHash& src = g_shared->targets[i];
        TargetRecord& dst = g_targets[g_targetCount];
        memcpy(dst.name, src.name, sizeof(dst.name));
        dst.name[sizeof(dst.name) - 1] = '\0';
        memcpy(dst.hash, src.hash, sizeof(dst.hash));
        dst.ilSize = src.ilSizeLow;
        dst.token = src.metadataToken;
        dst.index = i;
        if (dst.name[0] == '\0') {
            _snprintf_s(dst.name, sizeof(dst.name), _TRUNCATE, "target%u", i);
        }
        ++g_targetCount;
    }
    DrLog(DRLOG_INFO, "target table: %u entr%s from the bootstrap",
          g_targetCount, (g_targetCount == 1) ? "y" : "ies");
}

// Pre-filter.  A hash comparison is 32 bytes of constant-time work and the
// compilation stream is ~24,000 methods, so the low 16 bits of ILCodeSize is
// used to reject the overwhelming majority first.  This is a filter, never a
// match: a size hit still has to hash.
int FindTargetBySize(uint32_t ilSize) {
    const uint32_t low = ilSize & 0xFFFFu;
    for (uint32_t i = 0; i < g_targetCount; ++i) {
        if (g_targets[i].ilSize == low) {
            return static_cast<int>(i);
        }
    }
    return -1;
}

// Thread-safe publish of a match.  The JIT serialises compilation per thread but
// not across threads, and two apphosts' worth of background compilation can be
// in flight at once, so the record is claimed with an exchange rather than with
// a read-modify-write.
void PublishMatch(uint32_t targetIndex, uint64_t nativeEntry, uint32_t nativeSize,
                  const uint8_t hash[32], uint32_t ilSize, uint32_t tokenRead) {
    DrelayTargetHash* dst = &g_shared->targets[targetIndex];
    if (InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(&dst->found), 1, 0) != 0) {
        // Already claimed -- for example a method compiled on two threads, which
        // is itself worth knowing about: with tiering off it should not happen.
        DrLog(DRLOG_WARN, "target '%s' compiled a second time (nativeEntry=%p) -- "
                          "with TieredCompilation off this should not occur",
              g_targets[targetIndex].name, reinterpret_cast<void*>(nativeEntry));
        return;
    }
    dst->firstSeenQpc = static_cast<uint64_t>(__rdtsc());
    dst->nativeEntry = nativeEntry;
    dst->nativeSize = nativeSize;
    dst->tokenRead = tokenRead;
    (void)hash;
    (void)ilSize;
    const uint32_t found = g_targetsFound.fetch_add(1) + 1;
    DrLog(DRLOG_INFO, "target '%s' found: IL %u bytes, native %u bytes at %p (token 0x%08X, %u/%u)",
          g_targets[targetIndex].name, ilSize, nativeSize,
          reinterpret_cast<void*>(nativeEntry), tokenRead, found, g_targetCount);
}

// ---------------------------------------------------------------------------
// The token probe.  `getMethodDefFromMethod` is declared at 0x7A by the
// runtime's generated header, but a slot index is a derived fact and this build
// is the authority, so the hook tries the candidates and keeps the one whose
// answer is a real MethodDef token.  A plausible token is 0x06xxxxxx; the
// expected value from known-build.json is stronger still and is used when
// available.

bool LooksLikeMethodDef(uint32_t token) {
    return (token & 0xFF000000u) == 0x06000000u && (token & 0x00FFFFFFu) != 0;
}

uint32_t ProbeSlot(void* jitInfo, void* methodHandle, uint32_t slot, uint32_t expectedToken) {
    auto fn = reinterpret_cast<DrelayGetMethodDefFn>(DrelayVtblSlot(jitInfo, slot));
    if (fn == nullptr) {
        return 0;
    }
    uint32_t token = 0;
    __try {
        token = fn(jitInfo, methodHandle, &token);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
    if (!LooksLikeMethodDef(token)) {
        return 0;
    }
    if (expectedToken != 0 && token != expectedToken) {
        DrLog(DRLOG_WARN, "slot 0x%02X returned token 0x%08X, expected 0x%08X -- treating as wrong",
              slot, token, expectedToken);
        return 0;
    }
    return token;
}

// ---------------------------------------------------------------------------
// Version reading, for the self-check's "the runtime is the one we pinned".

bool ReadFileVersion(const wchar_t* path, char* out, size_t cap, uint32_t* outBuild) {
    DWORD ignored = 0;
    DWORD size = GetFileVersionInfoSizeW(path, &ignored);
    if (size == 0) {
        return false;
    }
    void* buffer = HeapAlloc(GetProcessHeap(), 0, size);
    if (buffer == nullptr) {
        return false;
    }
    bool ok = false;
    if (GetFileVersionInfoW(path, 0, size, buffer)) {
        VS_FIXEDFILEINFO* info = nullptr;
        UINT len = 0;
        if (VerQueryValueW(buffer, L"\\", reinterpret_cast<LPVOID*>(&info), &len) && len >= sizeof(*info)) {
            _snprintf_s(out, cap, _TRUNCATE, "%u.%u.%u.%u",
                        HIWORD(info->dwFileVersionMS), LOWORD(info->dwFileVersionMS),
                        HIWORD(info->dwFileVersionLS), LOWORD(info->dwFileVersionLS));
            if (outBuild != nullptr) {
                *outBuild = info->dwFileVersionLS;
            }
            ok = true;
        }
    }
    HeapFree(GetProcessHeap(), 0, buffer);
    return ok;
}

// ---------------------------------------------------------------------------
// The detour body.  Four disciplines, each with a named failure it prevents
// (HOOK-ARCHITECTURE.md section 4.2):
//   call the real JIT first ....... prevents recursion/deadlock in our own code
//   re-entrancy guard ............. prevents the same on the same thread
//   save/restore LastError ....... prevents breaking the runtime's P/Invoke state
//   __try/__except ................ prevents a native exception reaching the JIT

void OnCompiled(void* jitInfo, DrelayCorMethodInfo* info, uint8_t* nativeEntry, uint32_t nativeSize) {
    if (info == nullptr || info->ILCode == nullptr || info->ILCodeSize == 0) {
        return;
    }

    // THE SIZE PRE-FILTER COMES FIRST, BEFORE ANY HASHING, AND THAT ORDER IS A
    // CORRECTNESS DECISION RATHER THAN AN OPTIMISATION.
    //
    // The first version hashed every method with CryptoAPI inside the JIT callback
    // -- six calls per compilation, on every one of the ~24,000 methods a
    // self-contained .NET process compiles, on whichever thread the JIT happened
    // to be running.  The process died with
    // `Fatal error. Internal CLR error. (0x80131506)` once the detour was
    // installed, and with the patch disabled as well, which isolated the fault to
    // this callback.
    //
    // Two things about the old shape are indefensible regardless of which of them
    // caused the crash: acquiring and releasing a CSP handle thousands of times
    // from inside the JIT's own lock can *load a provider DLL* under the loader
    // lock, and the ILCodeSize comparison that rejects almost every method costs
    // nothing and needs no runtime call at all.  So the cheap, pure test runs
    // first and only the handful of methods whose IL size matches a target's are
    // hashed.
    //
    // A size collision is not a match: a candidate still has to hash equal.  What
    // the filter changes is that a non-candidate is never hashed, so the run with
    // no targets found does no crypto work whatsoever.
    //
    // NO COUNTER IS TOUCHED HERE, DELIBERATELY.  `g_compileCalls` belongs to the
    // detour, which is the only place where "one compileMethod call" is a fact; this
    // function used to increment it as well, which doubled every reported
    // compilation count.  See the note in `HookCompileMethod`.

    int candidate = -1;
    int matched = -1;
    uint8_t digest[32] = {};
    bool hashed = false;

    for (uint32_t i = 0; i < g_targetCount; ++i) {
        if (g_targets[i].ilSize != (info->ILCodeSize & 0xFFFFu)) {
            continue;
        }
        if (!hashed) {
            if (!Sha256(info->ILCode, info->ILCodeSize, digest)) {
                return;
            }
            hashed = true;
        }
        if (memcmp(digest, g_targets[i].hash, 32) == 0) {
            matched = static_cast<int>(i);
            break;
        }
        candidate = static_cast<int>(i);
    }

    // A target whose IL size is not shared by anything is never hashed, so the
    // trace below would have no digest to carry.  That is deliberate: a row
    // without a hash is a row about the *stream*, and only a row about a target
    // needs one.
    const bool fullTrace = (g_shared->cfgFlags & DRELAY_CFG_FULL_TRACE) != 0;
    if (fullTrace || matched >= 0 || candidate >= 0) {
        uint32_t slot = g_shared->traceWrite % DRELAY_TRACE_RING_ROWS;
        DrelayTraceRow* row = &g_shared->trace[slot];
        if (hashed) {
            memcpy(row->ilHash, digest, 32);
        } else {
            memset(row->ilHash, 0, 32);
        }
        row->ilSize = info->ILCodeSize;
        row->nativeSize = nativeSize;
        row->nativeEntry = reinterpret_cast<uint64_t>(nativeEntry);
        row->qpc = static_cast<uint64_t>(__rdtsc());
        row->matchedTarget = (matched >= 0) ? static_cast<uint8_t>(matched) : 0xFFu;
        row->isTargetCandidate = (candidate >= 0) ? 1u : 0u;
        row->tokenRead = 0;
        if (g_shared->traceWrite >= DRELAY_TRACE_RING_ROWS) {
            g_shared->traceOverflow = g_shared->traceOverflow + 1;
        }
        row->seq = g_shared->traceWrite + 1;
        g_shared->traceWrite = g_shared->traceWrite + 1;

        if (candidate >= 0 && matched < 0) {
            // THE HASH GOES INTO A TABLE THAT OUTLIVES THE TRACE RING, not only into a
            // log line.  The ring keeps the last 1024 compilations; a session produces
            // thousands, so a near-miss from thirty seconds earlier is evicted and then
            // reads exactly like "the method never compiled".  That is the difference
            // between "the operator never reached gameplay" and "the target is not this
            // build's method", and it is not a difference a log line can preserve.
            if (hashed) {
                DrRecordObservedHash(static_cast<uint32_t>(candidate), digest);
            }
            DrLog(DRLOG_WARN,
                  "IL size %u matches target '%s' but the hash does not -- "
                  "a same-sized neighbour, or a changed method",
                  info->ILCodeSize, g_targets[candidate].name);
        }
    }

    if (matched < 0) {
        return;
    }

    // The token is diagnostic and is read here, from inside the JIT, because
    // this is the only place a method handle for this method is in hand.  The
    // slot is chosen on the first match and then reused: the probe compares what
    // each candidate slot returns for the *same* handle, and only the slot whose
    // answer is a real MethodDef token for a method we already identified by IL
    // hash is kept.  That is what makes this a demonstration that the index is
    // right rather than a restatement of the header.
    uint32_t tokenRead = 0;
    if (g_shared->methodDefSlotUsed == DRELAY_SLOT_NONE) {
        static const uint32_t kCandidates[] = {
            DRELAY_VTBL_GET_METHOD_DEF,           // 0x7A: the runtime's own ordering
            DRELAY_VTBL_GET_METHOD_DEF_MONOMOD,   // 0x76: expected to be runWithErrorTrap
            DRELAY_VTBL_GET_METHOD_DEF_MANAGEDJIT // 105: expected to be unrelated
        };
        for (uint32_t candidateSlot : kCandidates) {
            const uint32_t got = ProbeSlot(jitInfo, info->ftn, candidateSlot,
                                           g_targets[matched].token);
            DrLog(DRLOG_INFO, "slot probe 0x%02X -> token 0x%08X (target '%s' expects 0x%08X)",
                  candidateSlot, got, g_targets[matched].name, g_targets[matched].token);
            if (got != 0) {
                g_shared->methodDefSlotUsed = candidateSlot;
                tokenRead = got;
                break;
            }
        }
        if (g_shared->methodDefSlotUsed == DRELAY_SLOT_NONE) {
            DrLog(DRLOG_WARN, "no candidate slot returned a matching MethodDef token; the "
                              "target match stands (it is by IL hash) but the token "
                              "cross-check is a GAP");
            g_shared->methodDefSlotUsed = DRELAY_SLOT_NONE;
        }
    } else {
        auto fn = reinterpret_cast<DrelayGetMethodDefFn>(
            DrelayVtblSlot(jitInfo, g_shared->methodDefSlotUsed));
        if (fn != nullptr) {
            __try {
                fn(jitInfo, info->ftn, &tokenRead);
            } __except (EXCEPTION_EXECUTE_HANDLER) {
                tokenRead = 0;
            }
        }
    }

    PublishMatch(g_targets[matched].index, reinterpret_cast<uint64_t>(nativeEntry),
                 nativeSize, digest, info->ILCodeSize, tokenRead);

    // One match is enough to answer the slot question, so the probe is paid for
    // once per process rather than once per target.
    if (g_shared->methodDefSlotUsed != DRELAY_SLOT_NONE) {
        static unsigned int once = 0;
        DrLogOnce(&once, DRLOG_INFO,
                  "getMethodDefFromMethod slot confirmed as 0x%02X (the runtime's generated "
                  "header says 0x7A; MonoMod documents 0x76 and ManagedJit 105)",
                  g_shared->methodDefSlotUsed);
    }
}

DrelayCorJitResult __cdecl HookCompileMethod(void* self, void* info, DrelayCorMethodInfo* methodInfo,
                                            unsigned flags, uint8_t** nativeEntry, uint32_t* nativeSize) {
    // COUNTED FIRST, BEFORE THE RE-ENTRANCY GUARD, AND THAT ORDERING IS THE POINT.
    //
    // This is the only measurement that can tell "the game never compiled this
    // method" apart from "the hook looked away while it did".  `g_compileCalls`
    // below is incremented after the guard, so it answers the first question only
    // if the guard never discards anything -- an assumption that was never checked
    // and that the re-entrancy guard's own arithmetic did not satisfy.  See
    // DrelayGlobals.h for what the difference means.
    g_detourInvocations.fetch_add(1);
    if (EnterDetour() > 0) {
        // Re-entered: our own bookkeeping is being compiled on this thread.
        // Pass straight through without touching anything of ours -- in
        // particular without logging, because the logger takes a lock this
        // thread may already hold.
        g_reentrantSkips.fetch_add(1);
        return g_realCompile(self, info, methodInfo, flags, nativeEntry, nativeSize);
    }
    const DWORD lastError = GetLastError();

    // NO SEH FRAME HERE, AND THAT IS A CORRECTNESS DECISION.
    //
    // The architecture document's four disciplines include a `__try`/`__except`
    // around the detour body.  This detour deliberately does not have one, and the
    // reason is measured rather than theoretical: with the handler present the
    // process died with `Fatal error. Internal CLR error. (0x80131506)` once the
    // detour was installed -- with the target patch disabled, with the IL hashing
    // removed, and with the re-entrancy guard moved off `thread_local`.  Removing
    // the frame did not fix it either, but it removed a suspect.
    //
    // What replaces it: the body is a call, one comparison loop that cannot fault
    // on anything it dereferences, and a return.  If a later phase adds faulting
    // work here, the frame has to come back and this note is why it was removed.
    DrelayCorJitResult result = g_realCompile(self, info, methodInfo, flags, nativeEntry, nativeSize);

    // Every entry is counted, and the FIRST entry is logged, before anything else
    // is attempted.
    //
    // This ordering is the point.  The process was dying with the detour installed
    // and no evidence in any log about whether the detour had ever been *called* --
    // which left two very different possibilities indistinguishable: a detour that
    // is being entered and failing, and a detour that is never entered at all,
    // because the runtime does not route `compileMethod` through this vtable slot.
    // The second would mean the whole hook point is wrong, so it has to be
    // excluded before anything else is investigated.  One line, once, on the first
    // compilation the detour sees.
    //
    // AND IT COUNTS ONCE.  This is the only place `g_compileCalls` is incremented,
    // and that is why.  `OnCompiled` -- the only caller -- used to increment it too,
    // so every run reported exactly double the real number of compilations: 3,663
    // real compilations arrived as "7326 compilation(s) seen", and the self-check's
    // "the detour was entered N time(s)" was wrong by the same factor.  The count is
    // a reported *result*; a result that is quietly 2x is worse than no result at
    // all, and it was invisible because both sites looked reasonable in isolation.
    const uint32_t entries = g_compileCalls.fetch_add(1) + 1;

    // EVERY compilation is counted by size, before any filtering, and that is the
    // point: the trace ring keeps only what passes a filter and only for the last
    // 1024 rows, so it can never answer "was a method of this size compiled at all,
    // anywhere in the run".  Three live runs failed to answer it and produced a wrong
    // conclusion.  This costs one hashed lookup and no allocation.
    if (methodInfo != nullptr) {
        DrRecordCompileSize(methodInfo->ILCodeSize);
    }

    if (entries == 1) {
        DrLog(DRLOG_INFO, "compileMethod detour ENTERED for the first time "
                          "(IL size %u, result %d) -- the vtable is the hook point",
              (methodInfo != nullptr) ? methodInfo->ILCodeSize : 0u, result.value);
        DrLogFlushNow();
    }

    if (result.value == DRELAY_CORJIT_OK && methodInfo != nullptr && nativeEntry != nullptr &&
        nativeSize != nullptr) {
        // `DRELAY_FORWARD_ONLY=1` reduces this detour to a pure forwarder:
        // no shared-block read, no hash, no log, no counter.  It exists to answer
        // one question with certainty -- is the detour's *presence* fatal, or is
        // it something the detour does?  A crash that survives pure forwarding is
        // an ABI or installation problem and no amount of work inside the body can
        // fix it; a crash that stops is in the body.  Both are useful answers and
        // they are indistinguishable without this.
        //
        // RENAMED FROM `DRELAY_HOOK_FORWARD_ONLY`, WHICH COULD NEVER BE SET.  The
        // launcher removes every `DRELAY_HOOK_*` variable from the child's
        // environment before adding its own, so the old name was stripped before
        // the hook could read it and this switch was dead code to anyone using the
        // supported launcher.  See PHASE1-HANDOFF.md section 4 trap 1.
        static int forwardOnly = -1;
        if (forwardOnly < 0) {
            char value[8] = {};
            forwardOnly = (GetEnvironmentVariableA("DRELAY_FORWARD_ONLY", value, sizeof(value)) != 0 &&
                           value[0] == '1') ? 1 : 0;
        }
        if (forwardOnly == 0) {
            OnCompiled(info, methodInfo, *nativeEntry, *nativeSize);
        }
    }

    SetLastError(lastError);
    LeaveDetour();
    return result;
}

} // namespace

// HOW LONG THE RESOLVE CALLBACK WAITS FOR THE RUNTIME BEFORE IT RUNS.
//
// A MARGIN, NOT A SIGNAL, AND THE ARGUMENT FOR WHY NO SIGNAL EXISTS IS AT THE CALL SITE
// (DrelayInvokeResolveCallback). The short version: a startup hook cannot JIT-compile
// anything, so it cannot execute a single statement after it returns, so nothing it
// writes can prove that it has returned; and the game's entry-point compilation, which
// does follow the return, happens before this detour is installed.
//
// 100 ms replaced a hardcoded 500. It is still two orders of magnitude more than the
// startup hook's epilogue takes, and the work it defers -- measuring field offsets for
// the frame read -- cannot be needed until the game draws a frame.
constexpr DWORD kDefaultResolveDelayMs = 100;

DWORD ReadResolveDelayMs() {
    char value[32] = {};
    if (GetEnvironmentVariableA("DRELAY_HOOK_RESOLVE_DELAY_MS", value, sizeof(value)) == 0 ||
        value[0] == '\0') {
        return kDefaultResolveDelayMs;
    }
    char* end = nullptr;
    const long parsed = strtol(value, &end, 10);
    if (end == value || parsed < 0) {
        DrLog(DRLOG_WARN, "DRELAY_HOOK_RESOLVE_DELAY_MS='%s' is not a non-negative number of "
                          "milliseconds; using the %lu ms default", value,
              static_cast<unsigned long>(kDefaultResolveDelayMs));
        return kDefaultResolveDelayMs;
    }
    // Bounded at five seconds, which is well past the point where waiting longer is
    // doing anything other than postponing the frame read; `0` is accepted and is the
    // diagnostic value that asks the question "does this margin matter at all".
    constexpr long kMaxResolveDelayMs = 5000;
    if (parsed > kMaxResolveDelayMs) {
        DrLog(DRLOG_WARN, "DRELAY_HOOK_RESOLVE_DELAY_MS=%ld exceeds the %ld ms maximum; clamping",
              parsed, kMaxResolveDelayMs);
        return static_cast<DWORD>(kMaxResolveDelayMs);
    }
    return static_cast<DWORD>(parsed);
}

// HOW LONG THE HARNESS KEEPS RECORDING AFTER THE PATCH, AND WHY IT IS A KNOB NOW.
//
// AT `drelay` SCOPE RATHER THAN IN THE ANONYMOUS NAMESPACE ABOVE, for the same
// reason the MVID below is: `DrelayMain.cpp` calls it.  It started inside the
// anonymous namespace next to `ReadCollectDelayMs`, which was fine for a helper only
// this file uses and a link error for one the watcher needs -- and the link error is
// the good outcome, because the alternative is a second copy of the parse.
//
// Five seconds was hard-coded, and it was the right length for its original job:
// long enough for the detour to be entered on a title screen, short enough that a
// wrong-address patch is reported at the start of a session rather than at the end
// of one.  It is the wrong length for the endurance criterion -- an hour of play
// with no crash and no frame regression -- because a run whose recording window is
// five seconds cannot record an hour of anything: the frame detour keeps running
// afterwards and there is nobody left to read its counters.
//
// `DRELAY_HOOK_LIVE_MS`, set by the launcher's `--live-seconds`, makes it a knob.
// The default is unchanged, so runs before this change stay comparable with runs
// after it -- which is the reason it is not simply a bigger number.
constexpr DWORD kDefaultLiveWindowMs = 5000;

DWORD ReadLiveWindowMs() {
    char value[32] = {};
    if (GetEnvironmentVariableA("DRELAY_HOOK_LIVE_MS", value, sizeof(value)) == 0 ||
        value[0] == '\0') {
        return kDefaultLiveWindowMs;
    }
    char* end = nullptr;
    const long parsed = strtol(value, &end, 10);
    if (end == value || parsed < 0) {
        DrLog(DRLOG_WARN, "DRELAY_HOOK_LIVE_MS='%s' is not a non-negative number of "
                          "milliseconds; using the %lu ms default", value,
              static_cast<unsigned long>(kDefaultLiveWindowMs));
        return kDefaultLiveWindowMs;
    }
    // Bounded by the launcher's own six-hour wait, so a live window longer than the
    // launcher will wait is refused here rather than discovered as a session that
    // never writes its evidence.
    constexpr long kMaxLiveWindowMs = 6L * 60 * 60 * 1000;
    if (parsed > kMaxLiveWindowMs) {
        DrLog(DRLOG_WARN, "DRELAY_HOOK_LIVE_MS=%ld exceeds the %ld ms maximum; clamping",
              parsed, kMaxLiveWindowMs);
        return static_cast<DWORD>(kMaxLiveWindowMs);
    }
    return static_cast<DWORD>(parsed);
}

// WHETHER THE FRAME READ STAYS OPEN AFTER THE RECORDING WINDOW.
//
// `g_framePaused` has been the first branch of `FrameTickDetour` since the
// native-read round, and nothing ever set it: the detour dereferenced
// `World.Shared` on every frame for the whole life of the process, including after
// the evidence was written and during teardown (PHASE1-VERIFICATION.md section 5).
// The evidence round is over when the window closes, and the read is the only part of
// the hook that touches the game's heap.
//
// THE DEFAULT IS NOW "STAY OPEN", AND THAT IS A RELEASE DECISION RATHER THAN A
// REFINEMENT.  Pausing at the end of a five-second recording window was correct for a
// run whose subject was the recording, and it is fatal for one whose subject is the
// player: the frame read is the sensor the dodge runs on, so a release run that
// stopped reading five seconds after the patch would stop dodging five seconds into
// the session -- silently, with the evidence files already written and nothing saying
// the hook had gone quiet.  `DRELAY_KEEP_READING=0` asks for the old behaviour and is
// the inverse of the old switch on purpose: the value that turns something off is the
// one a reader of a log has to find, not the one a release has to remember to set.
//
// It is a plain `DRELAY_` name rather than `DRELAY_HOOK_` for the reason trap 1 gives:
// the launcher deletes every `DRELAY_HOOK_*` variable from the child's environment, so
// a switch the operator sets in their own shell must live outside that namespace.
// `Drelay.Launcher.exe --pause-after-run` is the same switch, plumbed so it survives
// the launcher's own filter.
bool ReadKeepReadingAfterRun() {
    char value[8] = {};
    if (GetEnvironmentVariableA("DRELAY_KEEP_READING", value, sizeof(value)) == 0 ||
        value[0] == '\0') {
        return true;
    }
    return value[0] != '0';
}

// The file's own MVID, read during validation and compared with the runtime's
// answer once the managed callback has published it.
//
// IT IS AT `drelay` SCOPE, NOT IN THE ANONYMOUS NAMESPACE ABOVE, BECAUSE THE
// SELF-CHECK PRINTS IT.  It started file-static, which was fine while the only
// consumer was the comparison in this file -- but the comparison then adjusted a
// counter and wrote a log line instead of recording a check, so the strongest
// identity claim in the phase was invisible to anyone reading `selfcheck.txt`, and
// a refusal produced a failure count with nothing naming the MVID that differed.
uint8_t g_fileMvid[16] = {};
bool    g_fileMvidRead = false;

// ---------------------------------------------------------------------------
// Public entry points used by DrelayMain.cpp.

bool DrelayRuntimeValidate(DrelayInstallReport* report) {
    HMODULE jit = GetModuleHandleW(L"clrjit.dll");
    HMODULE coreclr = GetModuleHandleW(L"coreclr.dll");
    if (jit == nullptr || coreclr == nullptr) {
        DrLog(DRLOG_ERR, "clrjit.dll=%p coreclr.dll=%p -- the runtime is not loaded",
              jit, coreclr);
        return false;
    }

    auto getJit = reinterpret_cast<void* (__cdecl *)()>(GetProcAddress(jit, "getJit"));
    if (getJit == nullptr) {
        DrLog(DRLOG_ERR, "clrjit!getJit is not exported -- the JIT interface changed");
        return false;
    }
    g_jitObject = getJit();
    if (g_jitObject == nullptr) {
        DrLog(DRLOG_ERR, "getJit() returned null");
        return false;
    }
    g_jitVtable = *static_cast<void***>(g_jitObject);

    // Check 1: the four ICorJitCompiler slots must all be code inside clrjit's
    // own .text.  Reading a slot that points anywhere else means we are not
    // looking at the object we think we are, and every later check is then
    // meaningless -- so this is the first one.
    uint32_t inText = 0;
    for (uint32_t i = 0; i < DRELAY_VTBL_ICOMPILER_SLOTS; ++i) {
        if (DrPeAddressInSection(g_jitVtable[i], ".text")) {
            ++inText;
        } else {
            wchar_t module[MAX_PATH] = {};
            DrPeModuleNameOf(g_jitVtable[i], module, MAX_PATH);
            DrLog(DRLOG_ERR, "vtbl[%u] = %p is not in clrjit .text (it is in %ls)",
                  i, g_jitVtable[i], module[0] ? module : L"an unknown module");
        }
    }
    g_shared->jitVtableInText = (inText == DRELAY_VTBL_ICOMPILER_SLOTS) ? 1u : 0u;
    g_shared->jitVtableSlots = DRELAY_VTBL_ICOMPILER_SLOTS;
    if (inText != DRELAY_VTBL_ICOMPILER_SLOTS) {
        report->failed++;
        return false;
    }
    report->passed++;

    // Check 2: ask the object who it is.  This is the check that turns "the game
    // crashed after an update" into "the hook printed a line and did nothing"
    // (HOOK-ARCHITECTURE.md section 4.1).
    //
    // THE SIGNATURE WAS GUESSED WRONG FOR THE WHOLE OF PHASE 1, AND THAT WAS A
    // CRASH, NOT A WRONG NUMBER.  Three attempts were made at this call and all
    // three assumed the GUID came back *by value*: first as `int`, then as a
    // by-value struct, then as a by-value union.  The runtime's own header says
    // otherwise, and it is unambiguous:
    //
    //     // src/coreclr/inc/corjit.h at release/9.0
    //     virtual void getVersionIdentifier(
    //             GUID*   versionIdentifier   /* OUT */
    //             ) = 0;
    //
    // It returns `void` and writes through a pointer.  A by-value return type of a
    // 16-byte aggregate makes MSVC put a *hidden return pointer in RCX*, which
    // pushes the real arguments along by one register -- so the callee received
    // `&returnBuffer` where it expected `this`, and wrote 16 bytes of GUID over
    // something else entirely.  That is the fault the in-process crash reporter
    // named: with `DRELAY_SKIP_GUID_CALL=1` the process survives to a clean exit,
    // and with the call present it dies at shutdown with an access violation
    // reading 0xFFFFFFFFFFFFFFFF inside coreclr.
    //
    // WHY IT LOOKED LIKE AN ABI PUZZLE RATHER THAN A WRONG DECLARATION.  The
    // observed bytes were usually garbage and occasionally *looked* right, which
    // reads as "the call works but the convention is unclear".  It never worked:
    // a garbage GUID is what a write to the wrong address looks like from the
    // caller's side.  The lesson is the same one PHASE1-HANDOFF.md section 8
    // records for environment-variable controls -- a control that cannot be
    // verified to have done what it claims produces a confident wrong answer, and
    // "the GUID is sometimes right" was that answer for three rounds.
    typedef struct DrelayGuid {
        uint32_t data1;
        uint16_t data2;
        uint16_t data3;
        uint8_t  data4[8];
    } DrelayGuid;

    auto getVersion = reinterpret_cast<void (__cdecl *)(void*, DrelayGuid*)>(
        DrelayVtblSlot(g_jitObject, DRELAY_VTBL_GET_VERSION_ID));

    // The expected GUID: d6218a78-9a34-4c6f-8db5-077a06022fae, from
    // `src/coreclr/inc/jiteeversionguid.h` at release/9.0.
    static const DrelayGuid kExpected = { 0xd6218a78, 0x9a34, 0x4c6f,
                                          { 0x8d, 0xb5, 0x07, 0x7a, 0x06, 0x02, 0x2f, 0xae } };

    DrelayGuid actual = {};
    char skipGuid[8] = {};
    if (GetEnvironmentVariableA("DRELAY_SKIP_GUID_CALL", skipGuid, sizeof(skipGuid)) != 0 &&
        skipGuid[0] == '1') {
        // Kept as a switch, because it is what isolated this fault and because a
        // validation step that can be removed in one run is worth keeping.
        DrLog(DRLOG_WARN, "DRELAY_SKIP_GUID_CALL=1: getVersionIdentifier is not called at all "
                          "(diagnostic mode); the JIT/EE GUID is reported as a gap");
        report->gaps++;
    } else {
        getVersion(g_jitObject, &actual);
        static_assert(sizeof(DrelayGuid) == 16, "GUID is 16 bytes");

        const bool guidOk = memcmp(&actual, &kExpected, 16) == 0;
        memcpy(g_shared->jitGuid, &actual, 16);
        if (!guidOk) {
            DrLog(DRLOG_WARN,
                  "JIT/EE GUID read did not match: got {%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
                  actual.data1, actual.data2, actual.data3,
                  actual.data4[0], actual.data4[1], actual.data4[2], actual.data4[3],
                  actual.data4[4], actual.data4[5], actual.data4[6], actual.data4[7]);
            // A GAP rather than a refusal, and that remains the right call for the
            // same reason it always was: the load-bearing validations are the
            // vtable-slots-in-.text check above and the IL-hash target match, and
            // refusing to install because a *diagnostic* disagreed would trade a
            // working hook for a guarantee it does not need.  What changed is that
            // a wrong GUID is now evidence about the runtime rather than about our
            // calling convention.
            report->gaps++;
        } else {
            DrLog(DRLOG_INFO, "JIT/EE GUID verified: %08X-%04X-%04X (vtbl[2])",
                  actual.data1, actual.data2, actual.data3);
            report->passed++;
        }
    }

    // Check 3: runtime file versions.
    wchar_t jitPath[MAX_PATH] = {};
    wchar_t coreclrPath[MAX_PATH] = {};
    GetModuleFileNameW(jit, jitPath, MAX_PATH);
    GetModuleFileNameW(coreclr, coreclrPath, MAX_PATH);
    uint32_t jitBuild = 0;
    uint32_t coreclrBuild = 0;
    ReadFileVersion(jitPath, g_shared->clrjitVersion, sizeof(g_shared->clrjitVersion), &jitBuild);
    ReadFileVersion(coreclrPath, g_shared->coreclrVersion, sizeof(g_shared->coreclrVersion), &coreclrBuild);
    DrLog(DRLOG_INFO, "coreclr %s / clrjit %s",
          g_shared->coreclrVersion, g_shared->clrjitVersion);
    report->passed++;

    // Check 4: the effective tiering setting, from the environment.  A script
    // cannot read this and it is the assumption the entire design rests on, so
    // it is reported as an explicit fact rather than an implicit one.
    wchar_t tiered[64] = {};
    const DWORD tieredLen = GetEnvironmentVariableW(L"DOTNET_TieredCompilation", tiered, 64);
    if (tieredLen == 0) {
        g_shared->effectiveTieredCompilation = 0;   // unset: runtimeconfig.json governs
        DrLog(DRLOG_INFO, "DOTNET_TieredCompilation is unset, so runtimeconfig.json "
                          "(TieredCompilation=false) governs");
        report->passed++;
    } else {
        const int on = (wcscmp(tiered, L"0") != 0);
        g_shared->effectiveTieredCompilation = on ? 1u : 0u;
        if (on) {
            DrLog(DRLOG_ERR, "DOTNET_TieredCompilation=%ls is set, which OVERRIDES "
                             "runtimeconfig.json and defeats the single-compilation "
                             "assumption -- refusing to install", tiered);
            report->failed++;
            return false;
        }
        DrLog(DRLOG_WARN, "DOTNET_TieredCompilation=%ls is set; it is off, but the "
                          "launcher should have removed it", tiered);
        report->passed++;
    }

    // Check 5: the file on disk carries the MVID the loaded assembly should have.
    //
    // THE COMPARISON CANNOT BE MADE HERE, AND PRETENDING OTHERWISE WAS A HARD
    // REFUSAL AGAINST THE REAL GAME.  The check used to compare `g_shared->mvid`
    // -- which the *bootstrap* publishes -- against the file, at a point in the
    // sequence that is *before* the bootstrap has published anything: the
    // publication happens inside `CheckGameAssembly`, which runs in the managed
    // resolve callback, which the native side invokes two steps later.  So the
    // comparison was always `00000000-...` against the file, and against the real
    // game it produced
    //
    //     loaded assembly MVID mismatch: bootstrap reported 00000000-... and the
    //     file says 96000000-...
    //
    // and refused to install on a build that is in fact the pinned one.  Worse,
    // the second half of that line was itself wrong: `DrPeReadMvid` was reading
    // the Module row's MVID *index* rather than the GUID it points at (see the
    // long note in DrelayPe.cpp).
    //
    // So the two halves are separated to the places where each is answerable.
    // Here: the file's MVID is read and recorded, and a file that cannot be read
    // is a GAP.  After the callback: `DrelayVerifyLoadedAssemblyMvid` compares the
    // runtime's answer against this one, and refuses before anything in the game
    // is patched.
    if (!DrPeReadMvid(g_assemblyPath, g_fileMvid)) {
        DrLog(DRLOG_WARN, "could not read the assembly's MVID from %ls -- the identity "
                          "check is a GAP, not a pass", g_assemblyPath);
        report->gaps++;
    } else {
        g_fileMvidRead = true;
        DrLog(DRLOG_INFO, "the file's assembly MVID is %02X%02X%02X%02X-%02X%02X-%02X%02X-...; "
                          "the comparison against the loaded assembly follows the managed "
                          "callback, which is where the runtime's answer exists",
              g_fileMvid[0], g_fileMvid[1], g_fileMvid[2], g_fileMvid[3],
              g_fileMvid[4], g_fileMvid[5], g_fileMvid[6], g_fileMvid[7]);
        report->passed++;
    }

    return true;
}

bool DrelayInstallJitDetour(DrelayInstallReport* report) {
    if (g_jitVtable == nullptr) {
        return false;
    }

    // A SWITCH THAT REMOVES EXACTLY ONE STEP, ADDED LATE BUT DECISIVELY.
    //
    // Every earlier control removed *more* than the detour -- `DRELAY_HOOK_NO_MINHOOK`
    // and `DRELAY_HOOK_NO_DETOUR` both return before the handshake, so neither of them
    // ever reached the point where the process now dies. That made them useless as
    // controls for the question "is the installed detour fatal", which is the question
    // that matters at this point in the phase. This flag installs nothing and
    // everything else runs exactly as normal.
    //
    // THE NAME DOES NOT START WITH `DRELAY_HOOK_`, AND THAT IS DELIBERATE. The
    // launcher builds the child's environment by DELETING every variable with that
    // prefix and then setting the ones it wants -- so a flag named
    // `DRELAY_HOOK_SKIP_JIT_DETOUR` is removed before the child ever sees it, and the
    // first attempt at this control silently did nothing for exactly that reason. It
    // looked like evidence that the detour was not the cause. It was evidence that the
    // flag never arrived.
    char skip[8] = {};
    if (GetEnvironmentVariableA("DRELAY_SKIP_JIT_DETOUR", skip, sizeof(skip)) != 0 &&
        skip[0] == '1') {
        DrLog(DRLOG_WARN, "DRELAY_SKIP_JIT_DETOUR=1: no detour is installed, but everything "
                          "else -- the handshake, the callback, discovery -- runs as normal");
        DrLogFlushNow();
        return true;
    }

    // The TLS slot the detour's re-entrancy guard uses, allocated here -- on the
    // watcher thread, before the detour exists -- rather than lazily on first use
    // from a JIT thread.  See EnterDetour for why that matters.
    if (g_entrancySlot == TLS_OUT_OF_INDEXES) {
        g_entrancySlot = TlsAlloc();
        if (g_entrancySlot == TLS_OUT_OF_INDEXES) {
            DrLog(DRLOG_ERR, "TlsAlloc failed (%lu) -- refusing to install, because the "
                             "re-entrancy guard has nowhere to live", GetLastError());
            report->failed++;
            return false;
        }
    }

    // MinHook initialises before the vtable write rather than after, so that a
    // failure here leaves the JIT untouched.
    if (!DrelayDetoursInit()) {
        DrLog(DRLOG_ERR, "MH_Initialize failed -- refusing to install");
        report->failed++;
        return false;
    }

    g_realCompiler = reinterpret_cast<DrelayICorJitCompilerVtbl*>(g_jitVtable);
    g_realCompile = reinterpret_cast<CompileMethodFn>(g_jitVtable[DRELAY_VTBL_COMPILE_METHOD]);

    // PATCH THE CODE, NOT THE VTABLE SLOT.  THIS IS THE RESOLUTION OF PHASE 1'S
    // HARDEST FAILURE, AND THE REASONING IS WORTH MORE THAN THE CODE.
    //
    // HOOK-ARCHITECTURE.md section 4.1 says to overwrite vtable slot 0.  Doing
    // exactly that killed the process -- `Fatal error. Internal CLR error.
    // (0x80131506)` within a few hundred milliseconds -- and the elimination run
    // that pinned it down is recorded in PHASE1-SCOPE.md.  The decisive observation
    // was this: with the slot overwritten, the detour was **never entered**.  A
    // function that is never called cannot be the cause of a crash, so the write
    // itself was the fault, and the only remaining explanation for a detour that
    // is never entered is that the runtime does not call `compileMethod` through
    // that slot at all -- it resolves the address once and keeps it, which is
    // ordinary for a runtime that wants to avoid an indirect call per method.
    //
    // Patching the resolved address instead has both properties that are needed:
    // every call reaches the detour whether it came through the vtable or through
    // a cached pointer, and the vtable is left byte-identical to what the runtime
    // built, so nothing that inspects it can see a change.
    //
    // `DRELAY_HOOK_VTABLE_PATCH=1` restores the documented strategy so the two can
    // be compared on one build rather than argued about.
    char useVtable[8] = {};
    const bool patchVtable =
        GetEnvironmentVariableA("DRELAY_HOOK_VTABLE_PATCH", useVtable, sizeof(useVtable)) != 0 &&
        useVtable[0] == '1';

    if (patchVtable) {
        DWORD oldProtect = 0;
        if (!VirtualProtect(&g_jitVtable[DRELAY_VTBL_COMPILE_METHOD], sizeof(void*),
                            PAGE_READWRITE, &oldProtect)) {
            DrLog(DRLOG_ERR, "VirtualProtect on the JIT vtable failed (%lu)", GetLastError());
            report->failed++;
            return false;
        }
        InterlockedExchangePointer(reinterpret_cast<PVOID volatile*>(&g_jitVtable[DRELAY_VTBL_COMPILE_METHOD]),
                                  reinterpret_cast<PVOID>(&HookCompileMethod));
        DWORD ignored = 0;
        VirtualProtect(&g_jitVtable[DRELAY_VTBL_COMPILE_METHOD], sizeof(void*), oldProtect, &ignored);
        DrLog(DRLOG_WARN, "the JIT vtable slot was overwritten (DRELAY_HOOK_VTABLE_PATCH=1): this "
                          "is the strategy that crashed the process, and the detour was never "
                          "entered under it");
    } else {
        if (g_realCompile == nullptr) {
            DrLog(DRLOG_ERR, "vtable slot %u is null; nothing to patch", DRELAY_VTBL_COMPILE_METHOD);
            report->failed++;
            return false;
        }
        void* const target = reinterpret_cast<void*>(g_realCompile);
        void* trampoline = nullptr;

        // Asking Windows which module owns the slot is worth one call: if the
        // address is not inside clrjit.dll, the detour is being placed on
        // indirection rather than on the JIT itself, and every later conclusion
        // would be built on a wrong premise.
        wchar_t owner[MAX_PATH] = {};
        DrPeModuleNameOf(target, owner, MAX_PATH);
        DrLog(DRLOG_INFO, "compileMethod resolves to %p in %ls", target,
              owner[0] != L'\0' ? owner : L"an unknown module");

        const MH_STATUS created = MH_CreateHook(target, reinterpret_cast<void*>(&HookCompileMethod),
                                                &trampoline);
        if (created != MH_OK) {
            DrLog(DRLOG_ERR, "MH_CreateHook on compileMethod (%p) failed: status %d "
                             "(4=NOT_CREATED 5=ENABLED 7=NOT_EXECUTABLE 8=UNSUPPORTED_FUNCTION "
                             "9=MEMORY_ALLOC 10=MEMORY_PROTECT 12=FUNCTION_NOT_FOUND)",
                  target, static_cast<int>(created));
            report->failed++;
            return false;
        }
        // The trampoline is the real entry point now, and it is what the detour
        // calls through -- calling the original address would run the patched
        // bytes and recurse.
        g_realCompile = reinterpret_cast<CompileMethodFn>(trampoline);
        // MH_EnableHook takes the *target*, the address MH_CreateHook was given.
        const MH_STATUS enabled = MH_EnableHook(target);
        if (enabled != MH_OK && enabled != MH_ERROR_ENABLED) {
            DrLog(DRLOG_ERR, "MH_EnableHook on compileMethod (%p) failed: status %d",
                  target, static_cast<int>(enabled));
            report->failed++;
            return false;
        }
        DrLog(DRLOG_INFO, "compileMethod code patched via MinHook (target %p, trampoline %p)",
              target, trampoline);
    }

    g_shared->nativeStatus |= DRELAY_ST_JIT_DETOURED;
    DrLog(DRLOG_INFO, "compileMethod detoured: real=%p hook=%p",
          reinterpret_cast<void*>(g_realCompile), reinterpret_cast<void*>(&HookCompileMethod));
    // Written to disk *now*, not on the next flusher pass.
    //
    // Everything after this line happens on a JIT thread and may end in a hard
    // process kill; the 250 ms flusher never gets its turn, and the whole log --
    // including the entry counter that says whether the detour works at all --
    // was being lost.  A diagnostic that only survives success is not a diagnostic.
    DrLogFlushNow();
    report->passed++;
    return true;
}

void DrelayInvokeResolveCallback() {
    // CALLED ONCE, HERE, BECAUSE HERE IS WHERE COMPILATION WORKS AGAIN.
    //
    // PHASE1-SCOPE.md §6.1: a startup hook cannot JIT-compile anything, so the
    // bootstrap cannot load the game assembly, reflect over it, or build dynamic
    // methods.  All of that was moved behind this callback, which the native side
    // invokes from its own thread after the runtime has finished starting.
    //
    // The callback is an `[UnmanagedCallersOnly]` transition stub, so entering it
    // puts this thread into cooperative GC mode and the managed side may do
    // ordinary work.  It is called *before* discovery is allowed to close, so the
    // offsets are in place before anything reads them.
    if (g_shared == nullptr || g_shared->managedResolveHook == 0) {
        DrLog(DRLOG_WARN, "no resolve callback was published; the hook is installed but the "
                          "the frame read will have no offsets");
        return;
    }

    // THE OTHER HALF OF THE BISECT, AND THE PRIME SUSPECT.
    //
    // The process survives when the watcher does nothing at all, and dies right
    // before this call. Everything else in the watcher has been bracketed and passes
    // individually, so the callback -- the only step that enters managed code from a
    // native thread -- is where the remaining probability sits.
    //
    // The name avoids the `DRELAY_HOOK_` prefix the launcher strips; see the note on
    // the skip switch in DrelayInstallJitDetour.
    char skip[8] = {};
    if (GetEnvironmentVariableA("DRELAY_SKIP_RESOLVE_CALLBACK", skip, sizeof(skip)) != 0 &&
        skip[0] == '1') {
        DrLog(DRLOG_WARN, "DRELAY_SKIP_RESOLVE_CALLBACK=1: the hook is installed but the managed "
                          "callback is never invoked (diagnostic mode)");
        DrLogFlushNow();
        return;
    }

    DrLog(DRLOG_INFO, "invoking the managed resolve callback at %p",
          reinterpret_cast<void*>(static_cast<uintptr_t>(g_shared->managedResolveHook)));
    DrLogFlushNow();

    // RE-ARMED AGAIN IMMEDIATELY BEFORE THE CALLBACK, because this is the single
    // most likely place for the process to die and the runtime may have
    // registered further handlers since the last arming.
    DrelayArmCrashReporter();

    // WAIT, BEFORE CALLING BACK, AND THE REASON IS THE SAME ONE THAT MADE EVERYTHING
    // ELSE IN THIS PHASE FAIL.
    //
    // The bootstrap's last act before returning is to signal this side, so the
    // callback used to arrive while the runtime was still finishing the
    // startup-hook phase -- the exact window in which managed work has been fatal
    // all through this investigation. The callback enters managed code through an
    // `[UnmanagedCallersOnly]` stub, which needs the runtime to be in a state that
    // the startup-hook window is not.
    //
    // THIS IS STILL A MARGIN RATHER THAN A SIGNAL, AND IT IS WORTH SAYING WHY ONE CANNOT
    // BE HAD.  The only thing that proves "the startup hook has returned" is an
    // observation made *after* the return -- and a startup hook cannot make one: it
    // cannot JIT-compile a method (PHASE1-SCOPE.md section 6.1, where every attempt
    // faulted the process), so a new call, a thread or a callback registered from
    // inside it all die before the return they were meant to announce.  The one
    // observable that does follow the return -- the game's entry point being compiled --
    // is compiled before this detour is even installed, microseconds after the
    // bootstrap signals.  So the margin stays, and what changed is its size and its
    // name: 100 ms rather than a hardcoded 500, and `DRELAY_HOOK_RESOLVE_DELAY_MS`
    // ('--resolve-delay' at the launcher) rather than a constant in the middle of a
    // function.  The work being deferred has no deadline: the offsets it computes are
    // needed by the frame read, which cannot happen until the game draws a frame.
    const DWORD resolveDelayMs = ReadResolveDelayMs();
    if (resolveDelayMs > 0) {
        DrLog(DRLOG_INFO, "waiting %lu ms for the runtime to finish the startup-hook phase "
                          "before the callback", resolveDelayMs);
        DrLogFlushNow();
        Sleep(resolveDelayMs);
    }

    auto resolve = reinterpret_cast<void (__cdecl *)(void)>(
        static_cast<uintptr_t>(g_shared->managedResolveHook));
    __try {
        resolve();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // A fault in the callback must not take the process with it; the hook is
        // already installed and the game is already running.
        DrLog(DRLOG_ERR, "the resolve callback raised a native exception; the hook stays "
                         "installed and the frame read stays closed");
    }

    DrLog(DRLOG_INFO, "resolve callback returned; %u offset(s) published",
          g_shared->offsetCount);
    DrLogFlushNow();
}

// ---------------------------------------------------------------------------
// THE ARMING PROBE: the watcher thread's half of Spike 1.3's sensor.
//
// WHY THIS EXISTS, IN ONE PARAGRAPH.  Phase 1's blocking gap was that the frame
// callback could never read anything: `World.Shared` is null until the game loads
// a scene, the hook attaches long before that, and so the callback was switched
// off at attach and "read exactly one float from the live world" never ran.  The
// fix is to decide when to arm it *later*, once a world exists -- and this is the
// loop that provides the "later", because the watcher thread is the only thing in
// this DLL that is awake for the whole session.
//
// IT IS CALLED FROM EVERY WAIT THE WATCHER DOES, AND THAT IS THE DESIGN RATHER
// THAN AN ACCIDENT OF PLACEMENT.  The collection delay (30 s by default), the
// discovery window, the live-check wait and the final idle loop are four separate
// sleeps, and the moment a world appears is not known in advance: an operator who
// reaches a dungeon in ten seconds and one who takes three minutes must both be
// caught.  Once the decision is made the call is one volatile load and a return,
// so calling it forever costs nothing.
//
// WHAT IT DOES NOT DO: it does not decide anything.  The decision needs
// reflection, which belongs on the managed side and not in a native loop that
// also has to keep the discovery window on time.  This function calls, then reads
// the verdict and reports it -- once, with the transition flushed, so the line is
// in `hook.log` (and on the operator's console) at the moment it happens rather
// than only in the evidence written minutes later.

namespace {

uint32_t g_armStateLogged = 0xFFFFFFFFu;   // impossible state: forces the first read
bool     g_armProbeFaulted = false;

const char* ArmStateName(uint32_t state) {
    switch (state) {
        case DRELAY_ARM_WAITING:      return "WAITING";
        case DRELAY_ARM_ARMED:        return "ARMED";
        case DRELAY_ARM_REFUSED:      return "REFUSED";
        case DRELAY_ARM_UNCONFIGURED: return "UNCONFIGURED";
        default:                      return "unknown";
    }
}

// A coordinate pair, or "(not read)" when the chain never got that far.
//
// TWO USES, AND THE SECOND IS WHY IT DOES NOT SIMPLY PRINT THE NUMBERS.  A refusal
// caused by the MethodTable guard never reads a float, so there is no pair to print
// and a NaN rendered as `-nan(ind)` reads like a wrong number rather than an absent
// one.  A refusal caused by the cross-check *throwing* never publishes a pair either,
// so it falls into the same case.
void FormatCrossCheck(char* out, size_t cap, float x, float y) {
    if (x != x || y != y) {
        _snprintf_s(out, cap, _TRUNCATE, "(not read)");
    } else {
        _snprintf_s(out, cap, _TRUNCATE, "(%.4f, %.4f)", x, y);
    }
}

// Whether the confirmation rested on two routes that both reported exactly zero.
//
// A ZERO PAIR AGREES FOR ANY TWO ADDRESSES, INCLUDING A WRONG ONE.  The game creates
// its world while a scene is still loading, long before a character is placed in it,
// so `Player.Position` can legitimately be the default (0, 0) at the moment the
// cross-check runs.  Reporting that as a plain confirmation would take more from the
// comparison than it earned; the MethodTable identity check is what carries the
// verification in that case, and a reader is told so.
bool CrossCheckWasTrivial() {
    return g_shared != nullptr &&
           g_shared->managedCrossCheckRefX == 0.0f && g_shared->managedCrossCheckRefY == 0.0f &&
           g_shared->managedCrossCheckPtrX == 0.0f && g_shared->managedCrossCheckPtrY == 0.0f;
}

} // namespace

void DrelayPollArmProbe() {
    if (g_shared == nullptr || g_armProbeFaulted) {
        return;
    }
    const uint64_t published = g_shared->managedProbeHook;
    if (published == 0) {
        return;
    }

    auto probe = reinterpret_cast<void (__cdecl *)(void)>(static_cast<uintptr_t>(published));
    __try {
        probe();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // A faulting probe must not take the watcher with it, and it must not be
        // called again: this is the same discipline the resolve callback's own
        // `__try` applies, and the same reason -- the hook is installed and the
        // game is running, so the only acceptable outcome is a missed reading.
        g_armProbeFaulted = true;
        DrLog(DRLOG_ERR, "the managed arming probe raised a native exception; it will not be "
                         "called again and the frame read path stays closed");
        DrLogFlushNow();
        return;
    }

    const uint32_t state = g_shared->managedArmState;
    if (state == g_armStateLogged) {
        return;
    }
    g_armStateLogged = state;

    // `WAITING` is the state the run starts in, so the first observation of it is
    // not a transition and gets no line.  Reporting it would put a line in every
    // log saying that nothing has happened yet.
    if (state == DRELAY_ARM_WAITING) {
        return;
    }

    char reflected[64] = {};
    char pointer[64] = {};
    FormatCrossCheck(reflected, sizeof(reflected),
                     g_shared->managedCrossCheckRefX, g_shared->managedCrossCheckRefY);
    FormatCrossCheck(pointer, sizeof(pointer),
                     g_shared->managedCrossCheckPtrX, g_shared->managedCrossCheckPtrY);

    switch (state) {
        case DRELAY_ARM_ARMED:
            // "no live player", not "no world": the counter is incremented for both
            // "the world is null" and "the world exists with no character in it yet",
            // and the second is the commoner of the two on a live run -- the world is
            // created while a scene loads, the player some seconds later.
            DrLog(DRLOG_INFO, "frame read path ARMED (t+%u ms, after %u probe(s) found no live "
                              "player): reflection and the pointer chain agree on "
                              "Player.Position = %s%s; live reads begin at the next frame tick",
                  g_shared->managedArmTick, g_shared->managedProbeWaits, reflected,
                  CrossCheckWasTrivial()
                      ? " -- but both routes returned exactly zero, so the MethodTable identity "
                        "check is what carries this verification"
                      : "");
            break;
        case DRELAY_ARM_REFUSED:
            DrLog(DRLOG_ERR, "frame read path REFUSED: reflection says %s and the pointer chain "
                             "built from the measured offsets says %s -- the offsets do not "
                             "describe this build, so the callback will never read through them",
                  reflected, pointer);
            break;
        case DRELAY_ARM_UNCONFIGURED:
            DrLog(DRLOG_ERR, "frame read path can never open: the resolve callback published "
                             "no field offsets, so there is no pointer chain to check against the "
                             "world and none to read through");
            break;
        default:
            DrLog(DRLOG_WARN, "the managed side published arm state %u (%s), which this build "
                              "does not know", state, ArmStateName(state));
            break;
    }
    DrLogFlushNow();
}

void DrelayBeginDiscovery() {
    // The window is measured from the moment it is *opened*, and closing is what
    // opens it.  It used to be opened by the first compilation the detour saw, which
    // put the measurement at ~1.3 s into the process; see `ReadCollectDelayMs` for why
    // that measured the wrong interval and what replaced it.
    //
    // `DrelayWaitForCollectDelay` calls this after the delay, so a caller that reaches
    // here directly still gets a sane window -- it just starts now.
    if (g_discoveryTimerStarted.exchange(1) == 0) {
        g_discoveryStartTick.store(GetTickCount());
    }
}

bool DrelayWaitForCollectDelay() {
    const DWORD delayMs = ReadCollectDelayMs();
    if (delayMs > 0) {
        DrLog(DRLOG_INFO, "waiting %lu ms before collection starts, so the operator can get "
                          "past the menu and into gameplay; nothing compiled in this interval "
                          "is lost, only unwatched", static_cast<unsigned long>(delayMs));
        DrLogFlushNow();

        // Sliced rather than one long Sleep, so `DrelayHook_Shutdown` from the
        // operator's console is honoured during the delay instead of after it.  A
        // single Sleep(30000) would make the launcher's shutdown path wait on a
        // watcher that is not listening.
        const DWORD sliceMs = 250;
        // A HEARTBEAT, BECAUSE "THE LOG STOPS" AND "THE WAIT IS STILL RUNNING" LOOKED
        // IDENTICAL.  One live run with a 30 s delay produced a `hook.log` that ended
        // at the `waiting` line and never grew again, while the process lived on and
        // wrote its other evidence files 65 s later.  Nothing in that run could
        // distinguish a watcher still sleeping from a watcher that had died, and the
        // flusher's own failures were invisible too.
        //
        // One line per ~5 s turns that into a fact: a log that stops mid-delay now has
        // a heartbeat after the last one it printed, or it does not, and the two cases
        // mean different things.  It is logged through the same ring as everything
        // else, so it also exercises the flush path during the wait -- which is the
        // path the missing lines would have taken.
        const DWORD heartbeatEvery = (sliceMs > 0) ? (5000 / sliceMs) : 20;
        DWORD slices = 0;
        DWORD waited = 0;
        while (waited < delayMs) {
            if (InterlockedCompareExchange(&g_shutdownRequested, 0, 0) != 0) {
                // NO EVIDENCE IS WRITTEN HERE, AND THAT IS NOT A LOSS.  The caller
                // treats this return as "leave the discovery loop now" and falls
                // through to the same evidence writer the normal path uses, so
                // `summary.txt`, `events.jsonl`, `selfcheck.txt` and the rest are still
                // produced from whatever was collected during the delay.  Writing them
                // here as well would mean two writers for one run.
                //
                // The window is opened first anyway so that the elapsed-time figures
                // those files report are measured from a real timestamp rather than
                // from zero.
                DrelayBeginDiscovery();
                DrLog(DRLOG_WARN, "shutdown requested %lu ms into the collection delay; ending "
                                  "collection now and writing the evidence gathered so far",
                      static_cast<unsigned long>(waited));
                DrLogFlushNow();
                return false;
            }
            const DWORD slice = (delayMs - waited < sliceMs) ? (delayMs - waited) : sliceMs;
            Sleep(slice);
            waited += slice;
            ++slices;
            // THE ARMING PROBE RUNS DURING THE DELAY, AND THIS IS THE MOST
            // IMPORTANT OF ITS FOUR CALL SITES.
            //
            // The delay exists so the operator can clear the menu and get into
            // gameplay, which is exactly when `World.Shared` stops being null -- so
            // a probe that only ran after the delay would miss the one moment it is
            // waiting for and the frame read path would stay closed for as long as
            // it takes the game to load another scene.
            DrelayPollArmProbe();
            if (heartbeatEvery > 0 && (slices % heartbeatEvery) == 0 && waited < delayMs) {
                DrLog(DRLOG_INFO, "still waiting: %lu of %lu ms before collection starts",
                      static_cast<unsigned long>(waited), static_cast<unsigned long>(delayMs));
                DrLogFlushNow();
            }
        }
    }

    // Opened here and nowhere else, so "collection starts" is one event with one
    // timestamp rather than a property inferred from whatever compiled first.
    DrelayBeginDiscovery();
    DrLog(DRLOG_INFO, "collection started: watching the compilation stream for up to %lu ms "
                      "(the window closes early the moment every target is in)",
          static_cast<unsigned long>(ReadDiscoveryWindowMs()));
    DrLogFlushNow();
    return true;
}

DWORD ReadDiscoveryWindowMs() {
    char value[32] = {};
    if (GetEnvironmentVariableA("DRELAY_HOOK_DISCOVERY_WINDOW_MS", value, sizeof(value)) == 0 ||
        value[0] == '\0') {
        return kDefaultDiscoveryWindowMs;
    }
    char* end = nullptr;
    const long parsed = strtol(value, &end, 10);
    if (end == value || parsed < 0) {
        DrLog(DRLOG_WARN, "DRELAY_HOOK_DISCOVERY_WINDOW_MS='%s' is not a non-negative number of "
                          "milliseconds; using the %lu ms default", value,
              static_cast<unsigned long>(kDefaultDiscoveryWindowMs));
        return kDefaultDiscoveryWindowMs;
    }
    // Bounded by the same ceiling the collection delay uses, and for the same reason:
    // this value becomes the length of the watcher's reporting loop, and a typo that
    // meant minutes would present as a hang rather than as a long window.
    constexpr long kMaxDiscoveryWindowMs = 600000;
    if (parsed > kMaxDiscoveryWindowMs) {
        DrLog(DRLOG_WARN, "DRELAY_HOOK_DISCOVERY_WINDOW_MS=%ld exceeds the %ld ms maximum; clamping",
              parsed, kMaxDiscoveryWindowMs);
        return static_cast<DWORD>(kMaxDiscoveryWindowMs);
    }
    return static_cast<DWORD>(parsed);
}

bool DrelayDiscoveryWindowClosed() {
    if (g_discoveryTimerStarted.load() == 0) {
        return false;
    }
    return (GetTickCount() - g_discoveryStartTick.load()) >= ReadDiscoveryWindowMs();
}

DWORD DrelayDiscoveryPollMs() {
    return kDiscoveryPollMs;
}

void DrelayCloseDiscovery() {
    if (g_discoveryClosed.exchange(1) == 0) {
        DrLog(DRLOG_INFO, "discovery window closed after %lu ms: %u compilation(s) seen, "
                          "%u/%u targets matched",
              GetTickCount() - g_discoveryStartTick.load(),
              g_compileCalls.load(), g_targetsFound.load(), g_targetCount);

        // THE TWO NUMBERS THAT DECIDE HOW TO READ THIS RUN.
        //
        // `instrumented` is what `g_compileCalls` counts and what every per-target
        // verdict is derived from.  `invocations` is how many times the detour was
        // actually reached.  When they differ, some compilations were executed and
        // never examined, and a target reported MISSING may simply be one of them.
        const uint32_t invocations = g_detourInvocations.load();
        const uint32_t instrumented = g_compileCalls.load();
        const uint32_t skipped = g_reentrantSkips.load();
        DrLog(DRLOG_INFO, "detour accounting: %u invocation(s), %u instrumented, "
                          "%u skipped as re-entrant",
              invocations, instrumented, skipped);
        if (invocations > instrumented) {
            const uint32_t unaccounted = invocations - instrumented - skipped;
            DrLog(DRLOG_WARN, "the detour was entered %u more time(s) than it inspected "
                              "(%u skipped by the re-entrancy guard, %u by no accounted path). "
                              "A target reported MISSING in this run is NOT proof that the game "
                              "never compiled it -- see DrelayGlobals.h.",
                  invocations - instrumented, skipped, unaccounted);
        }
        if (g_discoveryDoneEvent != nullptr) {
            SetEvent(g_discoveryDoneEvent);
        }

        // FLUSHED HERE RATHER THAN LEFT TO THE FLUSHER'S 250 ms CADENCE, BECAUSE
        // THE THINGS THAT HAPPEN NEXT ARE EXACTLY WHAT KILLS THE PROCESS.  The
        // live run that first found a target was patched and then died on the first
        // frame, and the two lines above -- the target-size verdicts' supporting
        // accounting and the window close -- were still sitting in the ring, so the
        // one run that most needed a record of what the detour had seen left none.
        // A decision point is worth a flush; see PHASE1-HANDOFF.md section 4 trap 3.
        DrLogFlushNow();
    }
}

void DrelayLogDiscoveryStatus() {
    for (uint32_t i = 0; i < g_targetCount; ++i) {
        const DrelayTargetHash& t = g_shared->targets[g_targets[i].index];
        if (t.found != 0) {
            DrLog(DRLOG_INFO, "  %-12s FOUND    IL size key %5u  native %5u  entry %p  "
                              "token 0x%08X  (Phase 0 says 0x%08X)",
                  g_targets[i].name, t.ilSizeLow, t.nativeSize,
                  reinterpret_cast<void*>(t.nativeEntry), t.tokenRead, t.metadataToken);
        } else {
            DrLog(DRLOG_ERR,
                  "  %-12s MISSING  never compiled in the discovery window. Either it was "
                  "inlined, or the game never reached the code path that calls it. "
                  "This target is NOT patched.", g_targets[i].name);
        }
    }
}

uint32_t DrelayTargetCount() {
    return g_targetCount;
}

void DrelayLoadTargets() {
    LoadTargetsFromShared();
}

bool DrelayVerifyLoadedAssemblyMvid(DrelayInstallReport* report) {
    // THE HALF OF THE IDENTITY CHECK THE NATIVE SIDE COULD NOT MAKE EARLIER.
    //
    // "The file on disk is the pinned build" is Phase 0's fingerprint gate.
    // "The assembly the runtime actually loaded is the file on disk" is this, and
    // it is the only version of the claim that a swap between the two cannot
    // defeat.  It needs the runtime's own answer, which the managed side can only
    // obtain after the startup hook has returned -- so it runs here, after the
    // callback, and before the discovery window can close and patch anything.
    //
    // THE REPORT IS NOT TOUCHED HERE, DELIBERATELY.  This function used to adjust
    // `report->passed`/`failed`/`gaps` directly, which meant the strongest identity
    // check in the phase existed only as a counter and a log line -- invisible in
    // `selfcheck.txt`, and on a refusal the totals moved while no line named the two
    // MVIDs that disagreed.  `DrelayRunSelfChecks` now emits it as check 5a, from
    // exactly the values compared below, and the counts are recomputed from the
    // lines.  `report` is accepted and unused for signature compatibility; keep it
    // that way rather than reintroducing a second accounting path.
    (void)report;
    if (!g_fileMvidRead) {
        DrLog(DRLOG_WARN, "the file's MVID could not be read, so the loaded assembly could not "
                          "be checked against it -- reported as a GAP, not as a pass");
        return true;
    }

    uint8_t loaded[16] = {};
    memcpy(loaded, g_shared->mvid, sizeof(loaded));

    bool published = false;
    for (size_t i = 0; i < sizeof(loaded); ++i) {
        if (loaded[i] != 0) {
            published = true;
            break;
        }
    }
    if (!published) {
        // An all-zero MVID is never a real assembly's, so this is "the bootstrap
        // never published" rather than "the assemblies differ".  Reported as a
        // GAP for the same reason every other unmakeable check is: silence here
        // would read as a pass.
        DrLog(DRLOG_WARN, "the bootstrap published no MVID (the block still holds zeros), so the "
                          "loaded assembly could not be checked against the file -- GAP");
        return true;
    }

    if (memcmp(loaded, g_fileMvid, 16) != 0) {
        DrLog(DRLOG_ERR, "loaded assembly MVID mismatch: the runtime loaded "
                         "%02X%02X%02X%02X-%02X%02X-%02X%02X-... and %ls holds "
                         "%02X%02X%02X%02X-%02X%02X-%02X%02X-... -- refusing to patch anything",
              loaded[0], loaded[1], loaded[2], loaded[3], loaded[4], loaded[5], loaded[6], loaded[7],
              g_assemblyPath,
              g_fileMvid[0], g_fileMvid[1], g_fileMvid[2], g_fileMvid[3],
              g_fileMvid[4], g_fileMvid[5], g_fileMvid[6], g_fileMvid[7]);
        return false;
    }

    DrLog(DRLOG_INFO, "loaded assembly MVID verified against the file: "
                      "%02X%02X%02X%02X-%02X%02X-%02X%02X-...",
          loaded[0], loaded[1], loaded[2], loaded[3], loaded[4], loaded[5], loaded[6], loaded[7]);
    return true;
}

} // namespace drelay
