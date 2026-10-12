// DrelayLog.cpp -- ring-buffered logging with an asynchronous flusher.

#include "DrelayLog.h"

#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>

namespace {

DrelayShared* g_shared = nullptr;
wchar_t       g_runDir[MAX_PATH] = {};
wchar_t       g_logPath[MAX_PATH] = {};
HANDLE        g_flusher = nullptr;
HANDLE        g_stopEvent = nullptr;
SRWLOCK       g_writeLock = SRWLOCK_INIT;

// The reader's cursor.  A ring can lose rows when it wraps; the log records the
// sequence number of every line so a consumer can tell a gap from a quiet
// period, and `logOverflow` counts how many lines went unwritten.
uint32_t g_readSeq = 0;

void FormatLine(char* out, size_t cap, int level, const char* fmt, va_list args) {
    static const char* kLevel[] = { "INFO ", "WARN ", "ERROR" };
    int prefix = _snprintf_s(out, cap, _TRUNCATE, "[%s] ", kLevel[level & 3]);
    if (prefix < 0) {
        return;
    }
    size_t rest = cap - static_cast<size_t>(prefix);
    _vsnprintf_s(out + prefix, rest, _TRUNCATE, fmt, args);
}

void Emit(int level, const char* fmt, va_list args) {
    if (g_shared == nullptr) {
        return;
    }

    char text[DRELAY_LOG_LINE_LEN];
    FormatLine(text, sizeof(text), level, fmt, args);

    // A single writer would be enough for correctness of the ring index, but
    // three threads can log concurrently, so the index is guarded.  An SRW lock
    // in shared mode is cheap and, critically, is not a heap allocation.
    AcquireSRWLockExclusive(&g_writeLock);
    uint32_t slot = g_shared->logWrite % DRELAY_LOG_RING_LINES;
    DrelayLogLine* line = &g_shared->log[slot];
    // Publish the payload before the sequence number, so a reader that sees the
    // new sequence is guaranteed to see the text that belongs to it.
    memcpy(line->text, text, sizeof(line->text));
    line->level = static_cast<uint32_t>(level);
    line->qpc = static_cast<uint64_t>(__rdtsc());
    line->seq = g_shared->logWrite + 1;
    g_shared->logWrite = g_shared->logWrite + 1;
    ReleaseSRWLockExclusive(&g_writeLock);
}

// The flusher exists so that the game's frame never touches a file handle.  It
// rewrites the whole visible window each pass rather than appending, because the
// ring is small, the pass is rare, and an append would need bookkeeping that a
// crash could leave inconsistent.
DWORD WINAPI FlusherThread(LPVOID) {
    for (;;) {
        DWORD wait = WaitForSingleObject(g_stopEvent, 250);
        DrLogFlushTo(g_logPath);
        // The near-miss table rides the flusher rather than the detour, for the reason
        // the whole logger exists: the JIT thread must not touch a file.  This is a
        // no-op unless the detour has recorded something new.
        DrLogFlushObserved();
        if (wait == WAIT_OBJECT_0) {
            break;
        }
    }
    return 0;
}

} // namespace

void DrLogInit(DrelayShared* shared, const wchar_t* runDir) {
    g_shared = shared;
    if (runDir != nullptr) {
        wcsncpy_s(g_runDir, runDir, _TRUNCATE);
        // The native side's own file.  It deliberately does NOT share `hook.log`
        // with the bootstrap: two writers on one path, opened by two different
        // runtimes with different sharing modes, produced a file that contained
        // one writer's lines and not the other's, and no amount of reasoning about
        // open modes settled it.  Separate files is the answer that cannot fail
        // that way, and the launcher prints both paths so a reader still has one
        // place to look.
        _snwprintf_s(g_logPath, MAX_PATH, _TRUNCATE, L"%s\\hook.log", runDir);
    }
}

void DrLogStartFlusher(void) {
    if (g_flusher != nullptr || g_logPath[0] == L'\0') {
        return;
    }
    g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_flusher = CreateThread(nullptr, 0, FlusherThread, nullptr, 0, nullptr);
    if (g_flusher != nullptr) {
        SetThreadDescription(g_flusher, L"drelay-hook-log");
    }
}

void DrLogStopFlusher(void) {
    if (g_stopEvent != nullptr) {
        SetEvent(g_stopEvent);
    }
    if (g_flusher != nullptr) {
        WaitForSingleObject(g_flusher, 2000);
        CloseHandle(g_flusher);
        g_flusher = nullptr;
    }
    if (g_stopEvent != nullptr) {
        CloseHandle(g_stopEvent);
        g_stopEvent = nullptr;
    }
}

void DrLog(int level, const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    Emit(level, fmt, args);
    va_end(args);
}

void DrLogOnce(unsigned int* once, int level, const char* fmt, ...) {
    if (once == nullptr || InterlockedCompareExchange(once, 1, 0) != 0) {
        return;
    }
    va_list args;
    va_start(args, fmt);
    Emit(level, fmt, args);
    va_end(args);
}

void DrLogFlushNow(void) {
    if (g_logPath[0] != L'\0') {
        DrLogFlushTo(g_logPath);
    }
}

void DrLogFlushTo(const wchar_t* path) {
    if (g_shared == nullptr || path == nullptr || path[0] == L'\0') {
        return;
    }

    // Snapshot what is new, then write.  Taking the lock only for the snapshot
    // keeps a slow disk away from the frame thread's log calls.
    char lines[64][DRELAY_LOG_LINE_LEN];
    uint32_t seqs[64];
    uint32_t count = 0;
    uint32_t skipped = 0;

    AcquireSRWLockExclusive(&g_writeLock);
    uint32_t written = g_shared->logWrite;
    uint32_t oldest = (written > DRELAY_LOG_RING_LINES) ? written - DRELAY_LOG_RING_LINES + 1 : 1;
    if (g_readSeq + 1 < oldest) {
        skipped = oldest - (g_readSeq + 1);
        g_readSeq = oldest - 1;
    }
    for (uint32_t seq = g_readSeq + 1; seq <= written && count < 64; ++seq, ++count) {
        const DrelayLogLine* line = &g_shared->log[(seq - 1) % DRELAY_LOG_RING_LINES];
        memcpy(lines[count], line->text, DRELAY_LOG_LINE_LEN);
        seqs[count] = seq;
    }
    uint32_t consumed = count;
    ReleaseSRWLockExclusive(&g_writeLock);

    // ALWAYS APPEND.  The previous version of this decided the open mode from its
    // own reader cursor -- "wb" on the first pass, "ab" afterwards -- and on the
    // first pass it truncated the file, destroying every line the bootstrap had
    // already written into it.  The evidence for that is exactly the symptom it
    // produced: hook.log contained the bootstrap's seven lines and not one of the
    // native side's, because the native flusher opened the same path for "wb"
    // moments after the bootstrap had closed it.
    //
    // The two writers share that file, and no ordering is imposed between them, so
    // truncating is never correct: the file is a transcript in arrival order.
    FILE* f = nullptr;
    // CAPTURED IMMEDIATELY, BEFORE ANYTHING ELSE CAN CHANGE IT.
    //
    // The first version read `GetLastError()` *inside* the `fwprintf` argument list,
    // which is after `_vsnprintf_s` and the stream machinery have run -- so it
    // reported 0 for a failure that certainly had an error behind it.  A wrong
    // number in a diagnostic is worse than a missing one: it rules out the real
    // cause.  These three reads are the only ones taken anywhere near the failure.
    const errno_t openError = _wfopen_s(&f, path, L"ab");
    const DWORD openLastError = GetLastError();
    if (openError != 0 || f == nullptr) {
        // A FAILED APPEND IS A LOST LINE, AND IT USED TO BE INVISIBLE.
        //
        // This path was a bare `return`.  Every failure of it -- a sharing violation
        // from a scanner or another handle, a missing directory, a full disk, a
        // revoked ACL mid-run -- produced exactly the same evidence as a run in which
        // the hook had nothing left to say: a log that simply stops.  That ambiguity
        // is why two live runs could not be told apart from "the collection delay
        // never finished", and it is the same failure mode as a log that only survives
        // a clean exit.
        //
        // IT COUNTS IN `logAppendFailures`, NOT IN `logOverflow`, AND SHARING ONE WAS
        // A MISTAKE.  `logOverflow` means "lines the ring discarded before the flusher
        // read them"; this is "flush passes that could not write the file".  The two
        // lose lines at different rates per pass, so a shared counter produced a
        // number that could not be interpreted: a live run reported `748 overflowed`
        // while the file had actually been unopenable, and I spent a round trying to
        // reconcile arithmetic that had no single meaning.  A counter that cannot be
        // read is worse than no counter, because it produces a confident wrong answer.
        //
        // The lines themselves are still in the ring, and `DrLogFlushTo` retries them
        // on the next pass because `g_readSeq` only advances on a successful write --
        // so a transient failure costs nothing, and the count is what makes a
        // persistent one visible.
        //
        // THE FIRST FAILURE ALSO WRITES A SIDE FILE, AND THAT IS NOT REDUNDANT.
        // The counter says how many passes failed; it cannot say why, and the reason
        // is not recoverable after the fact.  A sibling file named
        // `hook.log.append-failed` carries the errno and the path the hook was actually
        // trying to open -- which is the difference between "another process held the
        // file" and "the path was never what we thought".
        if (g_shared != nullptr) {
            g_shared->logAppendFailures = g_shared->logAppendFailures + 1;

            static volatile LONG reported = 0;
            if (InterlockedCompareExchange(&reported, 1, 0) == 0) {
                wchar_t sidePath[MAX_PATH] = {};
                _snwprintf_s(sidePath, MAX_PATH, _TRUNCATE, L"%s.append-failed", path);
                FILE* side = nullptr;
                if (_wfopen_s(&side, sidePath, L"wb") == 0 && side != nullptr) {
                    // WRITTEN IN UTF-8, NOT WITH `fwprintf` ON A WIDE STREAM.
                    //
                    // The first version used `_wfopen_s` with `fwprintf` and `%ls`,
                    // which emits UTF-16: every byte was followed by a NUL, so the file
                    // read as `e r r n o : 1 3` in any ordinary viewer.  The content was
                    // right and the presentation made it look corrupt -- the same defect
                    // as a wrong number, in that it costs a reader time to notice it is
                    // not one.
                    fprintf(side,
                            "drelay hook: could not append to the log\n"
                            "  path          : %ls\n"
                            "  errno         : %d\n"
                            "  win32 error   : %lu\n"
                            "  lines pending : %u\n"
                            "  failed passes : %u\n",
                            path, static_cast<int>(openError),
                            static_cast<unsigned long>(openLastError),
                            static_cast<unsigned>(consumed),
                            static_cast<unsigned>(g_shared->logAppendFailures));
                    fflush(side);
                    fclose(side);
                }
            }
        }
        return;
    }
    if (skipped > 0) {
        fprintf(f, "---- log ring overflowed: %u line(s) lost ----\r\n", skipped);
    }
    for (uint32_t i = 0; i < consumed; ++i) {
        fprintf(f, "%s\r\n", lines[i]);
        g_readSeq = seqs[i];
    }
    // FLUSHED BEFORE CLOSE, AND THE FLUSH IS THE POINT.
    //
    // `fclose` flushes, so this changes nothing about the bytes that reach the
    // disk in a clean exit.  It changes everything about a process that is killed:
    // stdio buffers a few kilobytes, and when a crash killed the host the entire
    // log was lost -- every diagnostic line Phase 1 depended on, including the one
    // that would have said whether the JIT detour had ever been entered.  A log
    // that only survives a clean exit is not evidence, and this cost several
    // rounds of investigation before the cause was noticed.
    fflush(f);
    fclose(f);
}

// ---------------------------------------------------------------------------
// The near-miss table.  See DrelayLog.h for why it is not in the shared block.

namespace {

constexpr uint32_t kMaxObserved = 16;

struct ObservedHash {
    uint32_t targetIndex;
    uint8_t  hash[32];
};

ObservedHash     g_observed[kMaxObserved] = {};
volatile LONG    g_observedCount = 0;
volatile LONG    g_observedDirty = 0;
uint32_t         g_observedWritten = 0;

// The size histogram.  Open-addressed by `size % kMaxHistogramEntries`, linear
// probe, no allocation and no lock: the detour calls this on the JIT thread and the
// flusher reads it from another, so a torn read costs a slightly wrong count while a
// lock here would be a lock taken under the JIT's own.
//
// THE CAPACITY WAS WRONG BY A FACTOR OF TWO AND THE COMMENT SAID SO CONFIDENTLY.
// It read "512 distinct IL sizes is far more than a session produces (a few hundred
// at most)".  A measured session produces MORE than 512: the run that found three
// targets filled all 512 slots and reported `61 compilation(s) could not be
// recorded`.  The probe is no longer capped (see DrRecordCompileSize), so an entry
// is only lost when the table is genuinely full -- which it was.  4096 slots is
// 32 KB of the DLL's data section and makes "full" a state a session does not reach.
constexpr uint32_t kMaxHistogramEntries = 4096;

struct SizeCount {
    uint32_t size;
    uint32_t count;
};

SizeCount     g_sizeHistogram[kMaxHistogramEntries] = {};
volatile LONG g_sizeHistogramOverflow = 0;

// The exact per-target hit counters.  Indexed by the target's position in the
// shared block, incremented by a direct IL-size comparison and never by a table
// lookup, so these are the only numbers in this file that can be read as facts
// about whether a target was compiled.  See the note in DrRecordCompileSize.
uint32_t g_exactTargetHits[DRELAY_MAX_TARGETS] = {};

// Emits `hook.log.sizes`, the whole-run record of which IL sizes were compiled.
//
// REWRITTEN ON EVERY FLUSH PASS, WITH NO "NOTHING CHANGED" GUARD, AND THAT IS
// DELIBERATE.  A guard keyed on the near-miss table's count suppressed this file
// entirely on exactly the runs it exists for -- the ones where nothing matched.  A few
// hundred bytes, four times a second, on the thread whose whole job is file I/O, is
// not worth a guard that can be wrong.
//
// It is defined before `DrLogFlushObserved` rather than after, because the first
// attempt declared it and then defined it later, which the compiler rejects as
// "symbol involving type with internal linkage not defined" -- the declaration alone
// does not satisfy /W4 /WX when the definition has not been seen.
void WriteSizeHistogram() {
    wchar_t histPath[MAX_PATH] = {};
    _snwprintf_s(histPath, MAX_PATH, _TRUNCATE, L"%s.sizes", g_logPath);
    FILE* h = nullptr;
    if (_wfopen_s(&h, histPath, L"wb") != 0 || h == nullptr) {
        return;
    }
    fprintf(h, "# every distinct IL size compiled in this run, with its count.\n");
    fprintf(h, "# A target's IL size absent from this file was NEVER COMPILED -- that is a fact\n");
    fprintf(h, "# about the game's execution, not about the hook.  A size present without a match\n");
    fprintf(h, "# was compiled and its IL differs from the pinned build.\n");
    fprintf(h, "il_size,count,target\n");
    for (uint32_t i = 0; i < kMaxHistogramEntries; ++i) {
        const SizeCount& entry = g_sizeHistogram[i];
        if (entry.count == 0) {
            continue;
        }
        // Name the target whose size this is, if any, so the file reads without a
        // lookup table beside it.
        const char* which = "";
        if (g_shared != nullptr) {
            for (uint32_t t = 0; t < g_shared->targetCount && t < DRELAY_MAX_TARGETS; ++t) {
                if (g_shared->targets[t].ilSizeLow == entry.size) {
                    which = g_shared->targets[t].name;
                    break;
                }
            }
        }
        fprintf(h, "%u,%u,%s\n", entry.size, entry.count, which);
    }
    if (g_sizeHistogramOverflow != 0) {
        fprintf(h, "# %ld compilation(s) could not be recorded: the table was full\n",
                static_cast<long>(g_sizeHistogramOverflow));
    }
    fflush(h);
    fclose(h);
}

// A one-line-per-target verdict: was a method of this target's IL size compiled at all?
//
// Printed through `DrLog` so it lands in `hook.log` and on the launcher's live console
// transcript, because it is the answer to the only question the last three runs left
// open and the operator should not have to open a CSV to get it.
void WriteTargetSizeSummary() {
    if (g_shared == nullptr) {
        return;
    }
    for (uint32_t t = 0; t < g_shared->targetCount && t < DRELAY_MAX_TARGETS; ++t) {
        const uint16_t want = g_shared->targets[t].ilSizeLow;
        // READ FROM THE EXACT COUNTER, NOT FROM THE HISTOGRAM.
        //
        // This loop used to scan g_sizeHistogram and report zero when no slot held
        // this size -- which is a correct answer only while every compilation of
        // that size found a slot.  It does not: the table is lossy, and a run that
        // dropped compilations would have printed "ZERO compiled -- this method
        // never reached the JIT" about a method that had in fact been compiled.  A
        // verdict that can be produced by an instrument fault is worse than no
        // verdict, because it is read as a fact about the game.
        const uint32_t count = static_cast<uint32_t>(g_exactTargetHits[t]);
        const bool found = (g_shared->targets[t].found != 0);
        char name[DRELAY_TARGET_NAME_LEN] = {};
        memcpy(name, g_shared->targets[t].name, sizeof(name) - 1);
        if (count == 0) {
            DrLog(DRLOG_ERR, "target-size %-12s IL size %u: ZERO compiled -- this method never "
                             "reached the JIT in this run", name, static_cast<unsigned>(want));
        } else if (found) {
            // THE MATCHED CASE NEEDS ITS OWN WORDING.  It used to fall into the
            // branch below unconditionally, so the run that finally found a target
            // printed "compiled 1 time(s) but did not match" about a target that had
            // just been matched, patched and entered 297 times.  That line was
            // accurate only while no target had ever matched -- i.e. it was written
            // for the failure case and silently wrong the moment the fix landed.
            DrLog(DRLOG_INFO, "target-size %-12s IL size %u: compiled %u time(s) AND MATCHED "
                              "-- the target is found", name, static_cast<unsigned>(want), count);
        } else {
            DrLog(DRLOG_INFO, "target-size %-12s IL size %u: compiled %u time(s) but did not "
                              "match -- the pinned IL differs from this build's",
                  name, static_cast<unsigned>(want), count);
        }
    }
}

} // namespace

void DrLogTargetSizeSummary(void) {
    WriteTargetSizeSummary();
}

void DrRecordCompileSize(uint32_t ilSize) {
    // THE EXACT PER-TARGET COUNT, AND IT DOES NOT USE THE TABLE AT ALL.
    //
    // The table below is a *histogram*: it is lossy by construction, because the
    // probe is bounded to keep the JIT thread's work bounded.  A live run filled
    // 426 of its 512 slots and then dropped 101 compilations, and the reviewer's
    // question -- "were the missing targets among the dropped ones?" -- is exactly
    // the question the table cannot answer, because it records how many
    // compilations were lost and never which sizes they were.
    //
    // So the per-target verdict no longer comes from the table.  There are at most
    // DRELAY_MAX_TARGETS targets, so a direct comparison against each target's IL
    // size is a handful of instructions and is EXACT: no hashing, no probing, no
    // eviction, no overflow.  A target whose counter is zero here was not compiled,
    // full stop.  The histogram is kept as the whole-stream record it is good at
    // being (which sizes existed, and how many of each), and nothing that decides a
    // question reads it any more.
    if (g_shared != nullptr) {
        const uint32_t count = g_shared->targetCount;
        for (uint32_t t = 0; t < count && t < DRELAY_MAX_TARGETS; ++t) {
            if (g_shared->targets[t].ilSizeLow == ilSize) {
                InterlockedIncrement(reinterpret_cast<volatile LONG*>(&g_exactTargetHits[t]));
                break;
            }
        }
    }

    // 0 and 1 are not real IL sizes but a zero-sized body is possible for an empty
    // method; they are allowed through rather than special-cased, because filtering
    // here would make the histogram disagree with the compilation count.
    const uint32_t start = ilSize % kMaxHistogramEntries;
    for (uint32_t probe = 0; probe < kMaxHistogramEntries; ++probe) {
        const uint32_t slot = (start + probe) % kMaxHistogramEntries;
        SizeCount& entry = g_sizeHistogram[slot];
        if (entry.size == ilSize) {
            entry.count = entry.count + 1;
            return;
        }
        if (entry.count == 0) {
            // Claimed by this write.  `size` before `count`, because the reader uses
            // `count != 0` as "this slot is live" and a slot whose count is set before
            // its size would be read as a different size.
            entry.size = ilSize;
            entry.count = 1;
            return;
        }
    }
    // Reached only when the table is genuinely full -- the probe now covers every
    // slot, so it is a capacity, not a work bound.  The old bound of 64 probes made
    // this fire at 83% load, which is the regime a real session lands in: 426
    // distinct sizes in a 512-slot table, 16.8% of slots empty, and linear probing's
    // cluster-length variance is exactly what a 64-step cap truncates.  The cost of
    // a full scan is one pass over the table on a compilation whose size is new,
    // which is nothing next to hashing 32 bytes of IL -- and with 4096 slots the
    // scan is a state a session does not reach either.
    InterlockedIncrement(&g_sizeHistogramOverflow);
}

void DrRecordObservedHash(uint32_t targetIndex, const uint8_t hash[32]) {
    if (hash == nullptr) {
        return;
    }
    // Bounded and lock-free, because this runs under the JIT's lock on whichever
    // thread is compiling.  A duplicate is the common case -- the same same-sized
    // method compiles once, but the size pre-filter fires for every compilation of it
    // -- so the scan is what keeps the table from filling with one hash.
    const LONG count = g_observedCount;
    for (LONG i = 0; i < count && i < static_cast<LONG>(kMaxObserved); ++i) {
        const ObservedHash& seen = g_observed[i];
        if (seen.targetIndex == targetIndex && memcmp(seen.hash, hash, 32) == 0) {
            return;
        }
    }
    const LONG slot = InterlockedIncrement(&g_observedCount) - 1;
    if (slot >= static_cast<LONG>(kMaxObserved)) {
        // Full.  The count still grows so the report can say how many distinct hashes
        // there were, but no new hash is stored -- overwriting one would lose the
        // earlier near-miss, which is the more interesting one.
        InterlockedExchange(&g_observedDirty, 1);
        return;
    }
    g_observed[slot].targetIndex = targetIndex;
    memcpy(g_observed[slot].hash, hash, 32);
    InterlockedExchange(&g_observedDirty, 1);
}

void DrLogFlushObserved(void) {
    if (g_logPath[0] == L'\0') {
        return;
    }

    // THE HISTOGRAM IS WRITTEN FIRST, AND THE ORDER IS A FIX.
    //
    // The first version wrote it after the near-miss table's "nothing changed" guard.
    // With no near-misses recorded, that guard is true on every call, and the
    // histogram -- the instrument that exists precisely for the run where nothing
    // matches -- was never written at all.  A diagnostic that appears only when it is
    // not needed is not a diagnostic.
    WriteSizeHistogram();

    const uint32_t count = static_cast<uint32_t>(g_observedCount < 0 ? 0 : g_observedCount);
    const uint32_t stored = (count < kMaxObserved) ? count : kMaxObserved;

    // The near-miss table is rewritten only when something changed: the flusher runs
    // four times a second and the file is tiny, so rewriting it unconditionally would
    // be 240 writes a minute for no new information.  The histogram above has the same
    // guard inside it, keyed on its own total.
    if (g_observedDirty == 0 && g_observedWritten == count) {
        return;
    }
    g_observedDirty = 0;
    g_observedWritten = count;

    wchar_t path[MAX_PATH] = {};
    _snwprintf_s(path, MAX_PATH, _TRUNCATE, L"%s.observed", g_logPath);
    FILE* f = nullptr;
    if (_wfopen_s(&f, path, L"wb") != 0 || f == nullptr) {
        return;
    }
    fprintf(f, "# every distinct IL hash seen whose SIZE matched a target but whose hash did not.\n");
    fprintf(f, "# A target whose record is absent here never had a size match at all, which is a\n");
    fprintf(f, "# different finding from a near-miss: it means the method was not compiled.\n");
    fprintf(f, "target_index,target_name,il_size,observed_hash\n");
    for (uint32_t i = 0; i < stored; ++i) {
        char name[DRELAY_TARGET_NAME_LEN] = {};
        uint16_t ilSize = 0;
        const uint32_t ti = g_observed[i].targetIndex;
        if (ti < DRELAY_MAX_TARGETS && g_shared != nullptr) {
            memcpy(name, g_shared->targets[ti].name, sizeof(name) - 1);
            ilSize = g_shared->targets[ti].ilSizeLow;
        }
        fprintf(f, "%u,%s,%u,", ti, name, static_cast<unsigned>(ilSize));
        for (int b = 0; b < 32; ++b) {
            fprintf(f, "%02X", g_observed[i].hash[b]);
        }
        fprintf(f, "\n");
    }
    if (count > stored) {
        fprintf(f, "# (%u further distinct hashes were seen and not stored)\n", count - stored);
    }
    fflush(f);
    fclose(f);
}

