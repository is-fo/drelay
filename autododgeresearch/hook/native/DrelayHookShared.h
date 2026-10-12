// DrelayHookShared.h -- the contract between the native hook and the managed bootstrap.
//
// WHY THIS FILE IS THE MOST IMPORTANT ONE IN PHASE 1
// --------------------------------------------------
// The hook is injected before CoreCLR exists, so at injection time there is no
// managed assembly, no metadata and no way for the native side to discover
// anything.  The bootstrap runs later, from inside the runtime, and is the only
// code in the process that can ask the runtime questions about itself (what is
// my MVID, is tiered compilation on, where does the runtime think this field
// lives).  So the division of labour is:
//
//     bootstrap  answers questions about the runtime, and owns all managed reads
//     hook       owns the JIT detour and the patches, and reads nothing managed
//                that the bootstrap has not described to it
//
// This header is the shared vocabulary.  It is included by three compilers --
// MSVC for the native DLL, the C# compiler for the bootstrap and the launcher --
// so it must stay layout-trivial: fixed-width types, no pointers, no bitfields,
// and explicit padding.  Every field is read by at least one side and written by
// the other; nothing here is decorative.
//
// A layout change MUST bump DRELAY_SHARED_VERSION.  The native side refuses to
// use a block whose version it does not know, because the alternative is
// interpreting a stale layout as if it were current -- which presents as
// garbage offsets and a hook that reads the wrong field, not as a crash.
//
// DRELAY_SHARED_SIZE pins `sizeof(DrelayShared)` so the managed side has a number
// to compare against without anyone counting bytes.  It is the output of
// `tools/gen_shared_layout.py --print`, which models MSVC's packing rules over
// this very file, and the static_assert below makes a drift between the model and
// the compiler a build failure rather than a runtime misread.  Hand-counting this
// was tried first and produced three wrong strides; the generator exists because
// of that, not for tidiness.

#ifndef DRELAY_HOOK_SHARED_H
#define DRELAY_HOOK_SHARED_H

// <stdint.h> comes first and on its own, before anything that might pull in
// <windows.h>.  The shared block is compiled as C++17 by cl.exe as well as by
// the C# compiler's P/Invoke view, and MSVC's <windows.h> does not define the
// fixed-width types: without this include the first `uint32_t` in a struct
// produces C3646 ("unknown override specifier") at the first member and then a
// hundred cascading errors, which reads like a broken struct rather than a
// missing header.
#include <stdint.h>

// The launcher builds these names and the bootstrap is given the run directory
// by the launcher, but the *names* of the kernel objects are fixed because both
// sides must agree on them without a filesystem round trip.
#define DRELAY_SHARED_VERSION   5u
#define DRELAY_MAGIC_CONFIG     0x44485243u  /* 'DHRC' -- section holds a valid config */
#define DRELAY_MAGIC_STATUS     0x44485253u  /* 'DHRS' -- native has written its status */
#define DRELAY_MAGIC_BOOT       0x44485242u  /* 'DHRB' -- bootstrap has started and is alive */

// Kernel object names.  {0} is the numeric session id the launcher picks, so two
// sessions on one machine (the root apphost and the OpenGL one, say) cannot
// collide.  The launcher passes the id to the bootstrap in the environment.
#define DRELAY_MAP_FMT          L"Local\\DrelayHook-%u"
#define DRELAY_EVT_NATIVE_FMT   L"Local\\DrelayHook-NativeReady-%u"
#define DRELAY_EVT_BOOT_FMT     L"Local\\DrelayHook-BootReady-%u"

// Environment variables the launcher sets.  Named rather than positional so a
// run can be reconstructed from `hook.log` alone.
#define DRELAY_ENV_SESSION      "DRELAY_HOOK_SESSION"     // numeric session id
#define DRELAY_ENV_RUNDIR       "DRELAY_HOOK_RUNDIR"      // where the CSVs go
#define DRELAY_ENV_GAMEDIR      "DRELAY_HOOK_GAMEDIR"     // game install directory
#define DRELAY_ENV_ASSEMBLY     "DRELAY_HOOK_ASSEMBLY"    // the assembly to identify
#define DRELAY_ENV_BLOCK        "DRELAY_HOOK_BLOCK"       // file backing the shared block
#define DRELAY_ENV_OFF          "DRELAY_HOOK_OFF"         // "1" = observe nothing, patch nothing
#define DRELAY_ENV_DRYRUN       "DRELAY_HOOK_DRYRUN"      // "1" = decide, do not act
#define DRELAY_ENV_TIMEOUT_MS   "DRELAY_HOOK_TIMEOUT_MS"  // native wait for the bootstrap
#define DRELAY_ENV_STARTUPHOOK  "DOTNET_STARTUP_HOOKS"

// Maximum sizes, fixed so the block is a fixed-size mapping rather than a
// negotiated one.  Four targets is Phase 1's whole target table; the array is
// oversized relative to four only because Phase 2 adds the spawn and collision
// roles as first-class, separate detours.
#define DRELAY_MAX_TARGETS      8
#define DRELAY_MAX_STEPS        8
#define DRELAY_MAX_READS        8
#define DRELAY_MAX_SLOT_PROBE   12
#define DRELAY_TARGET_NAME_LEN  32
#define DRELAY_TYPE_NAME_LEN    96
#define DRELAY_FIELD_NAME_LEN   64
#define DRELAY_LOG_LINE_LEN     256
// Ring sizes.
//
// THESE ARE SIZED FOR LAUNCH, NOT FOR A SESSION, AND THE SECURITY-SECTION LIMIT
// IS WHY.  A named pagefile-backed section is charged against the process's
// commit *and* against a per-section security limit, and `CreateFileMapping` with
// a maximum size above roughly 1 MB on this machine fails with error 8
// (ERROR_NOT_ENOUGH_MEMORY) even with 6 GB of RAM free -- the first version asked
// for 1.9 MB of rings and could not be created at all.  That number is not
// guessable, so it is recorded here rather than rediscovered.
//
// What the sizes have to cover is one launch: a bootstrap handshake, the
// compilation of four target methods, and a few thousand frames of Spike 1.3's
// sample.  They are deliberately not sized for a multi-hour session; Phase 2's
// sensor will need its own transport, and growing these is the wrong answer to
// that because the mapping is created before either side can negotiate a size.
#define DRELAY_LOG_RING_LINES   256
#define DRELAY_TRACE_RING_ROWS  1024
#define DRELAY_FRAME_RING_ROWS  1024

// The reserved tail's size, and the reason it is a constant rather than
// `sizeof(DrelayShared)` in the two places that need it: the launcher creates the
// section before either side can measure anything, so the size must be known from
// a number both can compute.  Total `sizeof(DrelayShared)` is pinned by
// DRELAY_SHARED_SIZE below and asserted at compile time.
//
// EIGHT BYTES HAVE BEEN TAKEN OUT OF IT FOR `managedFrameHelper`, AND SIXTEEN MORE FOR
// `managedFrameReplacement` AND `managedFrameTrampoline`, WHICH IS WHAT THE RESERVE WAS
// KEPT FOR.  Taking them is what keeps those fields from moving any other offset and
// from changing `sizeof(DrelayShared)`: the whole point of the reserve is that a new
// field is an addition inside an existing mapping, and a field that grew the struct
// would force a `DRELAY_SHARED_VERSION` bump and a new mapping size for no benefit.
#define DRELAY_SHARED_RESERVE   262104u

/* ------------------------------------------------------------------------- */
/* Status codes.  Ordered by severity: the hook installs only when every check
 * is DRELAY_STATUS_OK or DRELAY_STATUS_GAP, and DRELAY_STATUS_GAP is the one
 * that is explicitly "not checked", never "checked and fine".                  */

typedef enum DrelayStatus {
    DRELAY_STATUS_OK            = 0,
    DRELAY_STATUS_GAP           = 1,   /* deliberately not checkable here */
    DRELAY_STATUS_REFUSED       = 2,   /* a fingerprint mismatch: never patch */
    DRELAY_STATUS_FAILED        = 3,   /* a check that should pass did not */
    DRELAY_STATUS_TIMEOUT       = 4,
    DRELAY_STATUS_DISABLED      = 5,   /* the off switch: nothing was installed */
    DRELAY_STATUS_UNSUPPORTED   = 6
} DrelayStatus;

/* Config flags the launcher/bootstrap may set. */
#define DRELAY_CFG_DRYRUN        0x00000001u  /* decide, never act (Phase 3 default) */
#define DRELAY_CFG_FRAME_SAMPLE  0x00000002u  /* Spike 1.3: sample the frame tick */
#define DRELAY_CFG_FULL_TRACE    0x00000004u  /* log every compilation, not just hits */
#define DRELAY_CFG_SLOT_PROBE    0x00000008u  /* try candidate getMethodDefFromMethod slots */
// 0x00000010 (DRELAY_CFG_MANAGED_READ, "call back into managed for live state") was
// REMOVED rather than left defined.  There is no callback: entering managed code from
// the frame detour is an illegal reverse P/Invoke and the runtime fail-fasts the
// process for it, so the flag could never have been honoured.  The bit is retired and
// must not be reused without a new name, because a saved `shared.block` or a launcher
// from an older build still means the old thing by it.

/* Native status flags. */
#define DRELAY_ST_JIT_DETOURED   0x00000001u
#define DRELAY_ST_DISCOVERY_DONE 0x00000002u
#define DRELAY_ST_PATCHED        0x00000004u
#define DRELAY_ST_REFUSED        0x00000008u
#define DRELAY_ST_OFF            0x00000010u

/* A slot index that means "not probed / not known". */
#define DRELAY_SLOT_NONE         0xFFFFFFFFu

/* The frame read path's arming gate.
 *
 * WHY THIS EXISTS AT ALL, AND WHY IT IS NOT A BOOLEAN.  The bootstrap publishes
 * the offsets and the type identity during the startup hook, when the runtime is up
 * but the game has no world yet: `World.Shared` is null until a scene loads, and
 * a callback that read through the pointer chain then would read through a null
 * base.  The first version of this simply refused to arm the callback at all in
 * that situation, which was honest and left Spike 1.3's success test -- read one
 * float from the live world -- never executed.  So arming became a decision made
 * LATER, by the managed side, the first time a world exists, and this field is
 * where that decision is published.
 *
 * `DRELAY_ARM_REFUSED` AND `DRELAY_ARM_UNCONFIGURED` ARE DIFFERENT FAILURES AND
 * MUST NOT BE COLLAPSED.  REFUSED means the pointer chain built from the measured
 * offsets did NOT reproduce what reflection says, i.e. the offsets are wrong for
 * this build and the hook must not read through them.  UNCONFIGURED means there
 * were never any offsets to check -- the assembly did not load, or its type names
 * moved -- so the cross-check could not be attempted at all.  One is a finding
 * about the layout, the other is a missing prerequisite; a single "not armed"
 * bit would make them indistinguishable in `summary.txt`, which is exactly the
 * class of defect PHASE1-HANDOFF.md section 8 is about. */
#define DRELAY_ARM_WAITING       0u  /* no world yet; the read path is closed and reads nothing */
#define DRELAY_ARM_ARMED         1u  /* the cross-check passed; the read path reads live state */
#define DRELAY_ARM_REFUSED       2u  /* the cross-check disagreed; the read path stays shut */
#define DRELAY_ARM_UNCONFIGURED  3u  /* no offsets were ever measured; nothing to arm */

/* ------------------------------------------------------------------------- */
/* Target identity.  The IL hash is the identity; everything else in this
 * struct is either a diagnostic or a cross-check the spike must report on.     */

typedef struct DrelayTargetHash {
    char     name[DRELAY_TARGET_NAME_LEN];  /* "frameTick", "spawn", ... */
    uint16_t ilSizeLow;                     /* ILCodeSize, low 16 bits -- a cheap pre-filter */
    uint8_t  hash[32];                      /* SHA-256 over ILCode[0..ILCodeSize) */
    uint8_t  found;                         /* native: 1 once seen in the compile stream */
    uint8_t  reserved0[3];
    uint64_t firstSeenQpc;                  /* native: when, in QPC ticks */
    uint64_t nativeEntry;                   /* native: the address the JIT returned */
    uint32_t nativeSize;                    /* native: the size the JIT returned */
    uint32_t tokenRead;                     /* native: what the probed slot said, if anything */
    uint32_t metadataToken;                 /* bootstrap: Phase 0's token, for the cross-check */
    uint32_t reserved1;
} DrelayTargetHash;

/* ------------------------------------------------------------------------- */
/* Offset resolution.  Two independent sources for the same fact, on purpose:
 * the bootstrap measures with managed reflection, and the hook asks the JIT via
 * ICorJitInfo::getFieldOffset.  Agreement is evidence; disagreement is a finding
 * (see PHASE1-SCOPE.md section 1.3).  Neither value is ever read from the IL.   */

/* HOW A STATIC FIELD'S ADDRESS FITS IN TWO 32-BIT FIELDS.
 *
 * `World.Shared` is static, so it has an ADDRESS rather than an instance offset,
 * and on x64 an address is 64 bits.  The first version wrote it with
 * `(uint)address`, which silently truncated it to its low half, and that
 * half-address was what `offsets.csv` and the launcher's console report printed.
 * Nothing read it, so nothing failed -- a wrong number that no check consumed,
 * which is the quietest kind.
 *
 * So for `isStatic != 0` the pair is the address and NOT an offset: the high 32
 * bits are in `managedStaticBase` and the low 32 in `managedOffset`, printed as
 * `0x%08X%08X`.  For every other field `managedStaticBase` is 0 and
 * `managedOffset` is an ordinary instance offset, which is why that column must
 * be read together with `is_static` in `offsets.csv` rather than on its own. */

typedef struct DrelayFieldOffset {
    char     name[DRELAY_FIELD_NAME_LEN];   /* "World.Shared", "World.Player", ... */
    uint32_t managedOffset;                 /* bootstrap: measured from the runtime */
    uint32_t managedStaticBase;             /* bootstrap: non-zero when it is a static field */
    uint8_t  managedValid;
    uint8_t  isStatic;
    uint8_t  reserved0[2];
    uint32_t jitOffset;                     /* native: ICorJitInfo::getFieldOffset, or DRELAY_SLOT_NONE */
    uint8_t  jitValid;
    uint8_t  agree;                         /* 1 when the value has been confirmed by a second route */
    uint8_t  reserved1[2];
} DrelayFieldOffset;

/* `agree` MEANS "CONFIRMED BY AN INDEPENDENT SECOND ROUTE", AND IT HAS TWO.
 *
 * The plan gives this field one producer -- `ICorJitInfo::getFieldOffset`, the
 * JIT's own answer -- and that is Phase 2's.  In Phase 1 the second route is the
 * pointer chain: the offsets are used to walk `World.Shared -> Player ->
 * Position` and the result is compared against the same state read through
 * reflection.  Agreement is evidence the offsets describe this build's layout;
 * disagreement is a finding, and the reason `agree` is written by the managed
 * side rather than only by the JIT.
 *
 * THE TWO SOURCES ARE KEPT APART BY `jitValid`.  `agree` alone would not say
 * which route agreed, and a reader of `offsets.csv` has to be able to tell "the
 * JIT confirmed this" from "the pointer chain confirmed this" -- they are
 * different claims with different confidence, and Phase 2 replaces the second
 * with the first.  So: `jitValid` names the oracle, `agree` names the verdict. */

/* ------------------------------------------------------------------------- */
/* The read plan.  The managed side composes these once; the hook executes them
 * on every frame tick.  This split exists so that the hook's hot path contains
 * no knowledge of the game's layout at all -- it follows a plan it was handed,
 * which is what keeps a game patch from turning a read into a wild pointer.
 *
 * Each step either dereferences a pointer at `offset` from the running base, or
 * adds `offset` to the running base (for a value type such as GamePoint, whose
 * fields are inline rather than behind a reference).  A failed read (null base)
 * aborts the whole plan and the frame is counted as `readFailures`; the hook
 * never guesses.
 */
#define DRELAY_STEP_ADD      0u   /* base += offset (inline value type traversal) */
#define DRELAY_STEP_LOAD_PTR 1u   /* base = *(void**)(base + offset), null-checked */

typedef struct DrelayReadStep {
    uint32_t op;      /* DRELAY_STEP_* */
    uint32_t offset;
} DrelayReadStep;

#define DRELAY_READ_F32      0u  /* read a 4-byte float at base + offset */
#define DRELAY_READ_F64      1u  /* read an 8-byte double at base + offset */

typedef struct DrelayReadSpec {
    char     name[DRELAY_FIELD_NAME_LEN];   /* "Player.Position.X" */
    uint32_t stepFirst;                     /* index into steps[] */
    uint32_t stepCount;
    uint32_t readKind;                      /* DRELAY_READ_* */
    uint32_t offset;                        /* final offset from the resolved base */
    uint8_t  valid;
    uint8_t  reserved0[3];
} DrelayReadSpec;

/* ------------------------------------------------------------------------- */
/* Rings.  A ring rather than an unbounded file because the native side must
 * never allocate or block inside a JIT callback, and because the interesting
 * events are rare.  Each entry carries a sequence number, so the reader (a
 * script, or Phase 5's bridge) can detect both overflow and gaps -- an
 * overflowed ring reports a gap rather than silently dropping rows.            */

typedef struct DrelayLogLine {
    uint32_t seq;
    uint32_t level;                       /* 0=info 1=warn 2=error */
    uint64_t qpc;
    char     text[DRELAY_LOG_LINE_LEN];
} DrelayLogLine;

typedef struct DrelayTraceRow {
    uint32_t seq;
    uint32_t ilSize;
    uint32_t tokenRead;
    uint32_t nativeSize;
    uint8_t  ilHash[32];
    uint64_t nativeEntry;
    uint64_t qpc;
    uint8_t  matchedTarget;               /* 0xFF = not a target */
    uint8_t  isTargetCandidate;           /* IL size matched a target's, hash did not */
    uint8_t  reserved0[6];
} DrelayTraceRow;

typedef struct DrelayFrameRow {
    uint32_t seq;
    uint32_t readFailures;
    uint64_t qpc;
    float    argTotal;                    /* DarzaWindow::Update's float argument */
    float    value;                       /* the first successful managed read */
    uint32_t elapsedUs;                   /* per-call cost, microseconds */
    uint8_t  readOk;
    uint8_t  reserved0[3];
} DrelayFrameRow;

/* ------------------------------------------------------------------------- */
/* The block itself.  Sectioned so the two writers never share a cache line: the
 * bootstrap writes `cfg`, native writes `st`.  `bootTick`/`nativeTick` are the
 * liveness pair the plan's Phase 5 heartbeat is modelled on, prototyped here
 * because Spike 1.1 already needs a liveness signal.                          */

typedef struct DrelayShared {
    /* --- header: written once by whoever creates the mapping --- */
    volatile uint32_t magic;
    volatile uint32_t version;
    volatile uint32_t structSize;         /* sizeof(DrelayShared) as the writer saw it */
    volatile uint32_t reserved0;

    /* --- bootstrap -> native --- */
    volatile uint32_t magicConfig;
    volatile uint32_t cfgFlags;
    volatile uint32_t targetCount;
    volatile uint32_t nativeTimeoutMs;
    volatile uint64_t bootTick;           /* bounded spin count; see ipc.cpp */
    volatile uint32_t bootPid;
    volatile uint32_t bootAssembliesReady;/* 1 once the bootstrap has resolved everything */

    DrelayTargetHash targets[DRELAY_MAX_TARGETS];

    /* identity of the loaded assembly, as the runtime sees it, not as the file does */
    uint8_t  mvid[16];
    uint8_t  assemblySha256[32];
    int64_t  managedAssemblySize;
    uint32_t imageBase;
    uint32_t entryPointToken;

    /* --- managed reads --- */
    volatile uint32_t readSpecCount;
    volatile uint32_t stepCount;
    DrelayReadStep  steps[DRELAY_MAX_STEPS];
    DrelayReadSpec  reads[DRELAY_MAX_READS];

    DrelayFieldOffset offsets[DRELAY_MAX_READS];
    volatile uint32_t offsetCount;

    /* THE `Player` METHODTABLE POINTER, PUBLISHED BY THE BOOTSTRAP, AND THE ONE
     * IDENTITY THE FRAME READ NEEDS.
     *
     * THIS FIELD REPLACES A MANAGED FRAME CALLBACK, AND THE REPLACEMENT IS A
     * MEASURED FINDING RATHER THAN A PREFERENCE.  The frame detour used to call an
     * `[UnmanagedCallersOnly]` managed method through its transition stub, on the
     * argument that entering the stub is what makes a managed read legal from a
     * native detour (HOOK-ARCHITECTURE.md section 5.2 rule 3).
     *
     * That argument is backwards, and coreclr says so at a fixed address.  The
     * detour runs on the game's frame thread while that thread is INSIDE managed
     * code -- between the managed caller of `DarzaWindow.Update` and its body --
     * so the thread is already in cooperative GC mode with live managed frames
     * below the detour.  A reverse P/Invoke asserts the opposite (its caller is
     * unmanaged, and its transition frame is the GC's boundary), so the runtime
     * refuses it: `JIT_ReversePInvokeEnterRare` calls `ReversePInvokeBadTransition`,
     * which fail-fasts the process with `COR_E_EXECUTIONENGINE` (0x80131506)
     * through `TerminateProcess` -- raising nothing an in-process handler can see.
     * Measured on six dumps spanning three builds, always at `coreclr+0x244839`,
     * always with `Drelay.Hook.dll!FrameTickDetour` on the faulting stack and the
     * game's own `DarzasDominion.dll` frame below it.
     *
     * So the frame read is NATIVE.  It is the same read the arming cross-check
     * already performs as its production path -- `World.Shared -> +Player ->
     * +Position` -- and it is safe for the reason the callback was supposed to be:
     * the thread is in cooperative mode, so it cannot be suspended mid-read and the
     * GC cannot move those objects while the read runs.  No transition, no compile,
     * no ABI boundary.  The MethodTable identity check comes along unchanged, and
     * this field is what supplies it: comparing the object's MethodTable against
     * the type handle turns a wrong offset into a counted failure instead of a wild
     * dereference.
     *
     * The low three bits are masked off before the comparison, because the GC uses
     * them as mark bits (HOOK-ARCHITECTURE.md section 5.1).                        */
    volatile uint64_t managedPlayerTypeHandle;

    /* The resolve callback, published by the bootstrap and called by the native
     * side exactly once, after the detour is installed.
     *
     * WHY THIS EXISTS IS PHASE 1'S ROOT CAUSE.  A startup hook cannot JIT-compile
     * anything: any call that forces a method to be compiled for the first time
     * faulters the process, and the stack blames the startup hook rather than the
     * compiler.  So everything the bootstrap used to do after signalling -- loading
     * the assembly, reflecting over it, building dynamic methods, starting a thread
     * -- had to move somewhere that runs *after* startup, and this is it.  The
     * native side calls it from its own thread, with the runtime fully up, where
     * all of that is ordinary work.                                          */
    volatile uint64_t managedResolveHook;

    /* The read path's result channel.  Written by the frame detour -- which now
     * performs the read itself, natively -- and read by the evidence writer on the
     * watcher thread.  It exists so the hot path can record one number without
     * allocating or formatting.
     *
     * THE `managed` PREFIX IS HISTORICAL.  These fields were named when the read was
     * a managed callback, and the names are kept because they are part of this
     * block's layout: renaming them would churn the generator, both sides and every
     * saved block for no gain.  What they hold is the frame read's result, whoever
     * performs it, and every human-readable label -- `summary.txt`, `hook.log`, the
     * launcher's report -- says "frame reads".                              */
    volatile uint32_t managedReadCount;    /* successful reads so far */
    volatile uint32_t managedReadFailures; /* reads that could not be made */
    volatile double   managedReadValue;    /* the first successful read, this frame */
    volatile uint8_t  managedReadValid;    /* 1 when managedReadValue is current */
    uint8_t           reservedManaged[7];

    /* ------------------------------------------------------------------ */
    /* The arming gate, and the evidence for the decision.                 */
    /*                                                                     */
    /* WHY A MANAGED PROBE ON THE WATCHER THREAD, AND NOT THE FRAME PATH.  */
    /*                                                                     */
    /* The arming decision needs reflection: the cross-check compares the  */
    /* pointer chain against `World.Shared.Player.Position` read through   */
    /* the runtime's own field accessors, and reflection boxes, allocates  */
    /* and can trigger a GC.  The frame path runs inside the game's frame  */
    /* and inside a native `__try`, and its whole contract is that it      */
    /* allocates nothing.  Putting the cross-check there would trade that  */
    /* contract for nothing, because the frame tick is not on the right    */
    /* thread for the question: the world reference comes from a STATIC    */
    /* field, not from the `DarzaWindow` receiver the detour is handed, so */
    /* the frame tick offers no information the watcher thread does not    */
    /* have.                                                               */
    /*                                                                     */
    /* So the probe below is called by the native watcher thread -- the    */
    /* one thread in this process that may legally enter managed code,      */
    /* because it is an unmanaged thread and its reverse P/Invoke is       */
    /* therefore what the runtime expects.  It is the same thread, through */
    /* the same mechanism, that already calls `managedResolveHook`, and it */
    /* is called every time that thread does anything: the collection      */
    /* delay, the discovery window, the live-check wait and the idle loop. */
    /* It makes the decision, publishes it here, and opens the gate that   */
    /* the frame detour's read path checks.                                */
    /*                                                                     */
    /* `managedProbeWaits` AND `managedFrameWaits` ARE SEPARATE ON PURPOSE.*/
    /* "The probe looked and there was no world" and "a frame was entered  */
    /* while the read path was still unarmed" are different statements     */
    /* about different threads; a shared counter would make the run's own  */
    /* report unable to say which of the two ever happened.                */
    volatile uint64_t managedProbeHook;    /* the arming probe, called from the watcher thread */
    volatile uint32_t managedArmState;     /* DRELAY_ARM_* */
    volatile uint32_t managedProbeWaits;   /* probe calls that found no world yet */
    volatile uint32_t managedFrameWaits;   /* frame entries that found the read path unarmed */
    volatile uint32_t managedArmTick;      /* GetTickCount when the decision was made */
    /* The two routes' answers, published so the run's evidence can state the
     * comparison rather than only its verdict.  A verdict without the numbers is
     * a claim a reader cannot check, and this is the one check in Phase 1 whose
     * whole content is a comparison. */
    volatile float    managedCrossCheckRefX;
    volatile float    managedCrossCheckRefY;
    volatile float    managedCrossCheckPtrX;
    volatile float    managedCrossCheckPtrY;

    /* --- native -> bootstrap --- */
    volatile uint32_t magicStatus;
    volatile uint32_t nativeStatus;
    volatile uint32_t selfCheckFailed;
    volatile uint32_t selfCheckGaps;
    volatile uint32_t selfCheckPassed;
    volatile uint32_t targetsFound;
    volatile uint32_t targetsPatched;
    volatile uint32_t methodDefSlotUsed;  /* DRELAY_SLOT_NONE until proven */
    volatile uint32_t installed;
    volatile uint32_t reserved1;
    volatile uint64_t nativeTick;
    volatile uint32_t nativePid;
    volatile uint32_t installMs;

    /* runtime facts the native side read for itself, for the L0/L1 comparison */
    uint8_t  jitGuid[16];
    char     clrjitVersion[32];
    char     coreclrVersion[32];
    uint32_t jitVtableSlots;
    uint32_t effectiveTieredCompilation;  /* native's own read; -1 if it could not */
    uint32_t jitVtableInText;             /* 1 when slots 0..3 all lie in clrjit .text */

    /* --- rings --- */
    volatile uint32_t logWrite;
    volatile uint32_t logOverflow;

    /* Flush passes that could not APPEND to `hook.log`.
     *
     * A RESERVED SLOT USED RATHER THAN A NEW FIELD, AND THAT IS DELIBERATE.  Adding a
     * field changes `sizeof(DrelayShared)`, which the `static_assert` below and
     * `SizeProbe.c` both check against the generator -- so a counter would have meant
     * bumping `DRELAY_SHARED_VERSION` and the block size for a diagnostic.  The slot
     * was already reserved for exactly this kind of growth.
     *
     * SEPARATE FROM `logOverflow`, WHICH IS THE POINT.  The first version counted an
     * append failure into `logOverflow`, which already meant "lines the ring discarded
     * before the flusher read them".  Two unrelated failures sharing one number made
     * that number unreadable: a live run reported `748 overflowed` while the flusher
     * had actually been unable to open the file, and the arithmetic would not close
     * because the two lose lines at different rates per pass.  A counter that cannot
     * be interpreted is worse than no counter, because it produces a confident wrong
     * diagnosis -- which it did, twice.
     *
     * `logOverflow`       = lines lost inside the ring (producer outran the flusher).
     * `logAppendFailures` = flush passes that could not write the file at all. */
    volatile uint32_t logAppendFailures;

    DrelayLogLine   log[DRELAY_LOG_RING_LINES];

    volatile uint32_t traceWrite;
    volatile uint32_t traceOverflow;
    uint32_t reserved3;
    DrelayTraceRow  trace[DRELAY_TRACE_RING_ROWS];

    volatile uint32_t frameWrite;
    volatile uint32_t frameOverflow;
    volatile uint32_t frameCalls;
    volatile uint32_t frameReadFailures;
    DrelayFrameRow  frames[DRELAY_FRAME_RING_ROWS];

    /* ------------------------------------------------------------------ */
    /* THE MANAGED FRAME HELPER -- Phase 2's mechanism test.                */
    /*                                                                     */
    /* A PLAIN MANAGED METHOD'S ENTRY POINT, NOT AN `[UnmanagedCallersOnly]` */
    /* STUB, AND THE DIFFERENCE IS THE WHOLE POINT.  Phase 1 measured that a  */
    /* reverse P/Invoke from the frame thread is refused and fail-fasts the   */
    /* process (`coreclr+0x244839`, six dumps).  It then shipped a frame      */
    /* detour that calls MinHook's trampoline, which `jmp`s into              */
    /* JIT-compiled managed code, 209 897 times in one hour without a fault  */
    /* -- so what is refused is the transition stub, not managed code.        */
    /*                                                                     */
    /* This field carries the address of a helper of our own, obtained with   */
    /* `RuntimeHelpers.PrepareMethod` + `MethodHandle.GetFunctionPointer()`.  */
    /* The detour calls it directly, the way it already calls the trampoline.  */
    /* The signature is all-primitive and fixed -- `int32 (int64, float)` --   */
    /* so the x64 managed convention is expressible, and therefore checkable,  */
    /* in C++ without having to read a game method's signature out of         */
    /* metadata.  That is the property Phase 2's two game hooks need.         */
    /*                                                                     */
    /* IT SITS LAST, IMMEDIATELY BEFORE THE RESERVE, AND THAT IS DELIBERATE.  */
    /* The block is the one part of this design whose layout both sides        */
    /* compute from the same generator, so a field added in the middle moves   */
    /* every offset after it -- and a stale `shared.block` of the same size    */
    /* would then pass the version and size checks in five places while being  */
    /* read at the wrong offsets.  Added here and taken out of the reserve,    */
    /* `sizeof(DrelayShared)` and every existing offset are unchanged, which   */
    /* is what the reserve is for.                                            */
    /*                                                                     */
    /* Written by the bootstrap on the resolve callback -- NOT during the      */
    /* startup hook itself, which cannot JIT anything -- and read by the frame */
    /* detour.  Zero means "no helper published; do not call".                 */
    volatile uint64_t managedFrameHelper;

    /* ------------------------------------------------------------------ */
    /* THE MANAGED REPLACEMENT, AND ITS WAY BACK INTO THE ORIGINAL.         */
    /*                                                                     */
    /* WHY THIS PAIR EXISTS.  The helper above is entered by a native `call`  */
    /* from `FrameTickDetour`, which leaves a native return address in the    */
    /* middle of the managed stack.  Measured over nine runs of the synthetic */
    /* host, that arrangement fails about one run in four -- always inside    */
    /* the GC's mark phase, and even when every managed method on the path is */
    /* pre-compiled and nothing allocates (PHASE2-SCOPE.md section 4.3).  The */
    /* diagnosis is that the runtime cannot unwind a managed stack that a     */
    /* native frame interrupts without a transition marker, and no discipline */
    /* inside the callee can supply the frame its caller does not have.       */
    /*                                                                     */
    /* So this pair removes the native frame instead.  `managedFrameReplacement`
    /* is a managed method with the target's own signature, and MinHook's     */
    /* `jmp` at the target's entry lands *directly* in it: the game's own     */
    /* `call` is then a managed-to-managed call, with no transition to       */
    /* refuse and no native frame to unwind through.  `managedFrameTrampoline`
    /* is MinHook's passthrough back into the real body, and the replacement  */
    /* calls it through a MANAGED function pointer -- `delegate* managed` --  */
    /* which the JIT emits as a plain `call`, so the original body's return   */
    /* address is inside the replacement, which is managed, and the whole     */
    /* chain stays walkable.  That is the one difference from the helper      */
    /* route, and it is the whole experiment.                                */
    /*                                                                     */
    /* Ordering, and it is load-bearing: the bootstrap publishes the          */
    /* replacement on the resolve callback; the native side creates the hook  */
    /* and publishes the trampoline from `MH_CreateHook`; only then may the   */
    /* hook be ENABLED, because the first `call` to the target arrives as     */
    /* soon as it is.  A replacement that read a zero trampoline would have   */
    /* to skip the original, which would silently stop the game updating.     */
    volatile uint64_t managedFrameReplacement;
    volatile uint64_t managedFrameTrampoline;

    /* ------------------------------------------------------------------ */
    /* THE COLLISION REPLACEMENT -- Deliverable 2.3's other half.           */
    /*                                                                     */
    /* `Projectile.HitCharacter()` is `private bool HitCharacter()`, so on  */
    /* x64 the receiver arrives in RCX and the answer leaves in AL.  The    */
    /* replacement takes the receiver as an `object` and returns `bool`,    */
    /* which is the same ABI: a managed reference is a pointer in RCX       */
    /* whether the compiler calls it an object or an IntPtr, and taking it  */
    /* as `object` means the hook can read the projectile's fields through  */
    /* reflection instead of reinterpreting an address.                     */
    /*                                                                     */
    /* IT IS THE COMPARISON'S ANCHOR.  This call is the instant the client   */
    /* decides a bullet is hitting the player and is about to tell the      */
    /* server -- `CLIENT-FINDINGS.md` section 4 calls it "the exact moment   */
    /* the server is about to be told".  Everything Phase 2 produces comes   */
    /* from asking, at this instant, whether the sensor had already         */
    /* predicted it and how long ago.                                       */
    /*                                                                     */
    /* `managedCollisionTrampoline` is MinHook's passthrough to the real     */
    /* method and is published before the hook is enabled, for the same      */
    /* reason the frame tick's is: the first call arrives immediately, and   */
    /* a replacement that could not reach the original would silently        */
    /* suppress the hit -- the game would stop claiming damage.              */
    volatile uint64_t managedCollisionReplacement;
    volatile uint64_t managedCollisionTrampoline;

    /* Reserved capacity.  The mapping is created at a fixed size by the launcher
     * and mapped by both sides before either knows the other's struct size, so
     * the size has to be a constant both can compute rather than
     * sizeof(DrelayShared) agreed on live.  Reserving it here means a Phase 2
     * field is an addition inside an existing mapping instead of a new mapping
     * size -- which matters because the mapping is created before the bootstrap
     * exists and cannot be resized afterwards.  A future addition that does not
     * fit here is a version bump, which is the correct outcome anyway.           */
    uint8_t         reserved[DRELAY_SHARED_RESERVE];
} DrelayShared;

// The size `tools/gen_shared_layout.py` computes for the struct above, and the
// number the managed side carries.  If the compiler disagrees, this file does not
// build, which is the correct place to find out.
//
// The first version of this constant was 1,911,328 -- a shared block whose rings
// were sized for a multi-hour session.  It could not be created: a named
// pagefile-backed section of that size fails `CreateFileMapping` with error 8.
// The rings are now sized for a launch (see the DRELAY_*_RING_* comments) and the
// whole block is 431 KB.
#define DRELAY_SHARED_SIZE 440912u

// The assert is suppressed for the layout probe only.  SizeProbe.c must be able
// to compile in order to *report* the real size when the constant is wrong; with
// the assert active it would fail to build and the build log would name the
// disagreement without ever printing the number that explains it.
#if defined(__cplusplus) && !defined(DRELAY_LAYOUT_PROBE)
static_assert(sizeof(DrelayShared) == DRELAY_SHARED_SIZE,
              "DrelayShared's layout drifted from tools/gen_shared_layout.py; "
              "re-run the generator and bump DRELAY_SHARED_VERSION");
#endif

/* Exported entry points.  The launcher resolves Init; Init spawns its own
 * watcher thread and returns immediately, because it is called from a remote
 * thread that the launcher is waiting on. */
#define DRELAY_EXPORT_INIT      "DrelayHook_Init"
#define DRELAY_EXPORT_SHUTDOWN  "DrelayHook_Shutdown"
#define DRELAY_EXPORT_ABI       "DrelayHook_AbiVersion"

#define DRELAY_ABI_VERSION      1u

typedef int  (__cdecl *DrelayHookInitFn)(void);
typedef void (__cdecl *DrelayHookShutdownFn)(void);
typedef unsigned int (__cdecl *DrelayHookAbiFn)(void);

#endif /* DRELAY_HOOK_SHARED_H */
