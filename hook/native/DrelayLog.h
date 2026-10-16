// DrelayLog.h -- the hook's own log, and why it is a ring rather than a file.
//
// The native side writes log lines from three places that must not block and
// must not allocate: the JIT callback (which runs under the JIT's own lock, on
// whichever thread happened to compile something), the frame-tick detour (which
// runs inside the game's frame), and the watcher thread.  A file handle in any
// of those is a way to make the game stutter or deadlock.
//
// So the hook writes into the shared block's log ring, and a background flusher
// thread drains the ring to disk.  If the flusher never runs, the ring still
// holds the last 512 lines, so a crash still leaves evidence.  The reader
// detects overflow by watching the sequence numbers rather than by trusting the
// row count -- an overflowed log says so instead of looking complete.

#ifndef DRELAY_LOG_H
#define DRELAY_LOG_H

#include "DrelayHookShared.h"

enum {
    DRLOG_INFO = 0,
    DRLOG_WARN = 1,
    DRLOG_ERR  = 2
};

// Configure the run directory.  Called once from the watcher thread; if it is
// never called, logging stays in-ring only, which is the correct degradation.
void DrLogInit(DrelayShared* shared, const wchar_t* runDir);

// Start the flusher.  Separate from Init because Init must happen before the
// bootstrap is running but the flusher must not start until the run directory
// is known to exist.
void DrLogStartFlusher(void);
void DrLogStopFlusher(void);

void DrLog(int level, const char* fmt, ...);

// Emit a line only if `once` has not been set.  Used for the per-frame paths,
// where "this failed" would otherwise be 60 lines a second.
void DrLogOnce(unsigned int* once, int level, const char* fmt, ...);

// Drain the ring to `path` and close.  Called on shutdown and on unload.
void DrLogFlushTo(const wchar_t* path);

// Drain the ring to the configured run directory's log, synchronously.  Called at
// each decision point in the watcher rather than waiting for the flusher thread:
// the flusher runs every 250 ms, a decision point may be the last thing the
// process does, and a log that only exists if the process survives long enough is
// not evidence.  Cheap enough to call a dozen times per run -- it writes only what
// is new.
void DrLogFlushNow(void);

// Record that a method of a target's IL size was compiled and its hash did NOT
// match.  Called from inside the JIT detour, so it must not allocate, must not
// take a lock the JIT could already hold, and must not touch a file: it writes to a
// fixed table in this DLL's data section and returns.
//
// WHY THIS EXISTS AT ALL, GIVEN THE TRACE RING.
//
// The ring holds the last 1024 compilations, and a gameplay session produces
// thousands, so a target that compiled early is *evicted* -- after which "not in
// the trace" is indistinguishable from "never compiled".  That ambiguity has already
// cost one wrong conclusion in this phase: `0 of 4 targets found` was read as "the
// operator never reached gameplay" when the trace simply could not have shown a
// first-use compilation from thirty seconds earlier.
//
// This table keeps up to 16 *distinct* size-matching hashes for the whole run, so
// the memory of a near-miss survives the ring.  Sixteen is generous: the same size
// recurring across thousands of compilations is almost always one or two methods.
void DrRecordObservedHash(uint32_t targetIndex, const uint8_t hash[32]);

// Count every compilation by IL size, for the whole run, in a bounded table.
//
// THE QUESTION THIS ANSWERS, AND WHY THE RING CANNOT.
//
// A ring of 1024 rows over a stream of ~3600 compilations retains about eight
// seconds, so "the target is not in the trace" says nothing about the other fifty.
// Counting sizes instead of retaining rows makes the *whole* stream observable in a
// few hundred bytes, and it is additive: two runs' histograms can be compared.  A
// target whose IL size appears with a count of zero was never compiled; one that
// appears without a match was compiled and did not match -- and those two readings
// have completely different causes, which is the distinction three live runs have
// failed to make from rows alone.
//
// `size` is `ILCodeSize`; entries are keyed by size and counted up to
// `kMaxHistogramEntries` distinct sizes, after which the count of unrecorded sizes is
// tracked as a single overflow number rather than silently dropped.
void DrRecordCompileSize(uint32_t ilSize);

// Write the observed table to `<runDir>\targets-observed.csv` if it has changed.
// Called from the flusher thread; never from the JIT thread.
void DrLogFlushObserved(void);

// Log one line per target saying whether a method of that target's IL size was compiled
// anywhere in the run, and how many times.
//
// The distinction it draws is the one three live runs could not:
//
//   "ZERO compiled"        -> the method never reached the JIT. The game did not run
//                             that code, or it ran precompiled code. Operator coverage.
//   "compiled N times"     -> the method ran and its IL does not match the pinned hash.
//                             The pinned build record is wrong. Phase 0's problem.
//
// Printed through `DrLog`, so it reaches `hook.log` and the launcher's live transcript.
void DrLogTargetSizeSummary(void);

#endif // DRELAY_LOG_H
