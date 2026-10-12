// DrelayEvidence.cpp -- the run's evidence, written once at the end.

#include "DrelayEvidence.h"
#include "DrelayGlobals.h"
#include "DrelayLog.h"

#include <stdio.h>
#include <string.h>

namespace drelay {

namespace {

FILE* OpenIn(const wchar_t* runDir, const wchar_t* name) {
    wchar_t path[MAX_PATH] = {};
    _snwprintf_s(path, MAX_PATH, _TRUNCATE, L"%s\\%s", runDir, name);
    FILE* f = nullptr;
    if (_wfopen_s(&f, path, L"wb") != 0) {
        return nullptr;
    }
    return f;
}

void CopyFixedName(const char* source, size_t cap, char* out, size_t outCap) {
    size_t n = 0;
    while (n + 1 < cap && n + 1 < outCap && source[n] != '\0') {
        out[n] = source[n];
        ++n;
    }
    out[n] = '\0';
}

const char* StatusName(uint32_t status) {
    switch (status) {
        case DRELAY_STATUS_OK:          return "ok";
        case DRELAY_STATUS_GAP:         return "gap";
        case DRELAY_STATUS_REFUSED:     return "refused";
        case DRELAY_STATUS_FAILED:      return "failed";
        case DRELAY_STATUS_TIMEOUT:     return "timeout";
        case DRELAY_STATUS_DISABLED:    return "disabled";
        case DRELAY_STATUS_UNSUPPORTED: return "unsupported";
        default:                        return "unknown";
    }
}

void WriteHex(FILE* f, const uint8_t* bytes, size_t count) {
    for (size_t i = 0; i < count; ++i) {
        fprintf(f, "%02X", bytes[i]);
    }
}

// The arming gate's state, in the words `hook.log` and `selfcheck.txt` use.
const char* ArmStateName(uint32_t state) {
    switch (state) {
        case DRELAY_ARM_WAITING:      return "WAITING (no world has existed yet)";
        case DRELAY_ARM_ARMED:        return "ARMED (the cross-check passed)";
        case DRELAY_ARM_REFUSED:      return "REFUSED (the cross-check disagreed)";
        case DRELAY_ARM_UNCONFIGURED: return "UNCONFIGURED (no offsets were measured)";
        default:                      return "unknown";
    }
}

// A coordinate pair, or "(not read)" when the chain was refused before reading.
void FormatPoint(char* out, size_t cap, float x, float y) {
    if (x != x || y != y) {
        _snprintf_s(out, cap, _TRUNCATE, "(not read)");
    } else {
        _snprintf_s(out, cap, _TRUNCATE, "(%.4f, %.4f)", x, y);
    }
}

// `summary.txt` is the file a person reads first, so it leads with the answer
// (did it install, what was found) and puts the raw numbers underneath.
void WriteSummary(FILE* f) {
    fprintf(f, "drelay hook -- Phase 1 run summary\r\n");
    fprintf(f, "=================================\r\n\r\n");

    const bool installed = (g_shared->nativeStatus & DRELAY_ST_JIT_DETOURED) != 0;
    const bool refused = (g_shared->nativeStatus & DRELAY_ST_REFUSED) != 0;
    const bool off = (g_shared->nativeStatus & DRELAY_ST_OFF) != 0;

    fprintf(f, "result            : %s\r\n",
            off ? "OFF (the off switch was honoured; nothing was installed)"
                : refused ? "REFUSED (a check failed; the game ran unmodified)"
                          : installed ? "installed" : "not installed (see hook.log)");
    fprintf(f, "native status     : 0x%08X\r\n", g_shared->nativeStatus);
    fprintf(f, "self-check        : %u passed, %u failed, %u gap(s)\r\n",
            g_shared->selfCheckPassed, g_shared->selfCheckFailed, g_shared->selfCheckGaps);
    fprintf(f, "install time      : %u ms\r\n", g_shared->installMs);
    fprintf(f, "methodDef slot    : ");
    if (g_shared->methodDefSlotUsed == DRELAY_SLOT_NONE) {
        fprintf(f, "not proven (no candidate slot returned a matching token)\r\n");
    } else {
        fprintf(f, "0x%02X\r\n", g_shared->methodDefSlotUsed);
    }
    fprintf(f, "\r\n");

    fprintf(f, "runtime\r\n");
    fprintf(f, "  coreclr.dll     : %s\r\n", g_shared->coreclrVersion);
    fprintf(f, "  clrjit.dll      : %s\r\n", g_shared->clrjitVersion);
    fprintf(f, "  JIT/EE GUID     : ");
    WriteHex(f, g_shared->jitGuid, 16);
    fprintf(f, "\r\n");
    fprintf(f, "  vtable in .text : %s\r\n", g_shared->jitVtableInText ? "yes (4/4 slots)" : "NO");
    fprintf(f, "  tiering         : %s\r\n",
            g_shared->effectiveTieredCompilation == 0 ? "off"
            : g_shared->effectiveTieredCompilation == 1 ? "ON -- should have refused" : "unknown");
    fprintf(f, "\r\n");

    fprintf(f, "assembly\r\n");
    fprintf(f, "  MVID            : ");
    // Printed in the same byte order as MVID.ToString("D") in the bootstrap and
    // known-build.json, so the three can be compared by eye rather than by
    // reversing the first three fields in one's head.
    fprintf(f, "%02X%02X%02X%02X-%02X%02X-%02X%02X-%02X%02X-%02X%02X%02X%02X%02X%02X\r\n",
            g_shared->mvid[3], g_shared->mvid[2], g_shared->mvid[1], g_shared->mvid[0],
            g_shared->mvid[5], g_shared->mvid[4], g_shared->mvid[7], g_shared->mvid[6],
            g_shared->mvid[8], g_shared->mvid[9], g_shared->mvid[10], g_shared->mvid[11],
            g_shared->mvid[12], g_shared->mvid[13], g_shared->mvid[14], g_shared->mvid[15]);
    fprintf(f, "  size            : %lld bytes\r\n", static_cast<long long>(g_shared->managedAssemblySize));
    fprintf(f, "  entry point tok : 0x%08X\r\n", g_shared->entryPointToken);
    fprintf(f, "\r\n");

    // Spike 1.2's headline: which of the four targets the compilation stream
    // produced, and whether the token agrees with what Phase 0 pinned.
    fprintf(f, "targets (%u pinned, %u found, %u patched)\r\n",
            g_shared->targetCount, g_shared->targetsFound, g_shared->targetsPatched);
    for (uint32_t i = 0; i < g_shared->targetCount && i < DRELAY_MAX_TARGETS; ++i) {
        const DrelayTargetHash& t = g_shared->targets[i];
        char name[DRELAY_TARGET_NAME_LEN] = {};
        CopyFixedName(t.name, sizeof(t.name), name, sizeof(name));
        fprintf(f, "  %-12s %-8s", name, t.found ? "FOUND" : "MISSING");
        if (t.found) {
            const bool tokenAgrees = (t.tokenRead != 0) && (t.tokenRead == t.metadataToken);
            const bool tokenRead = (t.tokenRead != 0);
            fprintf(f, " native %5u bytes at 0x%llX  token 0x%08X (%s; Phase 0 says 0x%08X)",
                    t.nativeSize, static_cast<unsigned long long>(t.nativeEntry), t.tokenRead,
                    tokenRead ? (tokenAgrees ? "agrees" : "DISAGREES") : "not read",
                    t.metadataToken);
        }
        fprintf(f, "\r\n");
    }
    fprintf(f, "\r\n");

    fprintf(f, "frame tick (Spike 1.3)\r\n");
    // WHICH READ PATH THIS RUN USED, PRINTED BEFORE ITS COUNTERS BECAUSE IT DECIDES HOW
    // TO READ THEM.  Phase 1's frame read is the native detour; Phase 2's is a MANAGED
    // replacement, and in that mode the native detour is not installed at all -- so
    // `calls: 0` and `frame reads: 0 ok` describe a path that does not exist on this run
    // rather than a read that failed.  The 2026-10-12 20:42 session read 19 349 frames
    // through the managed replacement and printed those zeroes here, and the self-check
    // needed the same rule (DrelaySelfCheck.cpp checks 9 and 9c).  A reader who compares
    // two runs' frame costs needs this line before they compare the counters.
    fprintf(f, "  read path       : %s\r\n",
            (g_shared->managedFrameReplacement != 0)
                ? "the MANAGED REPLACEMENT -- the native detour is not installed, so the "
                  "counters below are 0 by construction; the entries are in the bootstrap "
                  "log ('MANAGED REPLACEMENT entered', and the sensor's frame lines)"
                : "the native frame detour");
    fprintf(f, "  calls           : %u\r\n", g_shared->frameCalls);
    fprintf(f, "  worst call      : %u us\r\n", g_frameMaxUs.load());
    // THE ARMING GATE, PRINTED BEFORE THE READ NUMBERS BECAUSE IT DECIDES HOW TO
    // READ THEM.  `frame reads: 0 ok, 0 failed` means one thing when the read path
    // was never armed -- there was no world to read -- and a different thing when it
    // was armed and the read failed.  Until this line existed the two were the same
    // two zeroes.
    {
        char reflected[64] = {};
        char pointer[64] = {};
        FormatPoint(reflected, sizeof(reflected),
                    g_shared->managedCrossCheckRefX, g_shared->managedCrossCheckRefY);
        FormatPoint(pointer, sizeof(pointer),
                    g_shared->managedCrossCheckPtrX, g_shared->managedCrossCheckPtrY);
        fprintf(f, "  arm state       : %s\r\n", ArmStateName(g_shared->managedArmState));
        if (g_shared->managedArmState == DRELAY_ARM_ARMED) {
            fprintf(f, "  armed at        : t+%u ms\r\n", g_shared->managedArmTick);
        }
        fprintf(f, "  cross-check     : reflection %s vs pointer chain %s\r\n",
                reflected, pointer);
        fprintf(f, "  waited for player: %u probe(s), %u frame(s) found none\r\n",
                g_shared->managedProbeWaits, g_shared->managedFrameWaits);
    }
    fprintf(f, "  frame reads     : %u ok, %u failed\r\n",
            g_shared->managedReadCount, g_shared->managedReadFailures);
    fprintf(f, "  read value      : %.4f%s\r\n", g_shared->managedReadValue,
            g_shared->managedReadValid ? "" : "  (not currently valid)");
    // WHETHER THE READ IS STILL RUNNING, REPORTED AS THE STATE IT IS IN RATHER THAN
    // AS THE INTENT IT WAS LAUNCHED WITH.  A run whose window has closed has the
    // read paused, and a reader comparing two runs needs to know that before
    // comparing their read counts or their frame costs.
    fprintf(f, "  frame read      : %s\r\n",
            g_framePaused.load() != 0
                ? "PAUSED (the live window has closed; DRELAY_KEEP_READING=1 keeps it open)"
                : "open (still reading on every frame)");
    fprintf(f, "\r\n");

    // THE NUMBER THAT DECIDES HOW EVERY OTHER NUMBER IN THIS FILE IS READ.
    //
    // `targets (4 pinned, 0 found)` above is only a statement about the game while
    // the detour inspected every compilation it was entered for.  When it did not,
    // "0 found" means "the hook was not looking", and the two are indistinguishable
    // in `hook.log` unless this line is printed.  `invocations` is counted before
    // the re-entrancy guard; `compilations seen` is counted after it.
    fprintf(f, "compileMethod detour\r\n");
    fprintf(f, "  invocations     : %u  (every entry, counted before the re-entrancy guard)\r\n",
            g_detourInvocations.load());
    fprintf(f, "  instrumented    : %u  (entries the hook actually inspected)\r\n",
            g_compileCalls.load());
    fprintf(f, "  re-entrant skip : %u\r\n", g_reentrantSkips.load());
    if (g_detourInvocations.load() > g_compileCalls.load()) {
        fprintf(f, "  WARNING         : %u compilation(s) were executed and NOT inspected. A target\r\n",
                g_detourInvocations.load() - g_compileCalls.load());
        fprintf(f, "                    reported MISSING in this run is not proof that the game never\r\n");
        fprintf(f, "                    compiled it. See DrelayGlobals.h.\r\n");
    }
    fprintf(f, "\r\n");

    fprintf(f, "rings\r\n");
    fprintf(f, "  log lines       : %u written, %u overflowed\r\n",
            g_shared->logWrite, g_shared->logOverflow);    // REPORTED SEPARATELY BECAUSE THEY ARE DIFFERENT FAILURES.  `overflowed` is the
    // ring discarding lines the flusher had not read yet; this is the flusher being
    // unable to OPEN the file, which loses every line behind it no matter how much
    // room the ring has.  A run whose log stops mid-session is explained by the second
    // and not by the first, and until they had separate counters the two produced one
    // uninterpretable number.
    if (g_shared->logAppendFailures != 0) {
        fprintf(f, "  log appends     : %u failed pass(es) -- see hook.log.append-failed\r\n",
                g_shared->logAppendFailures);
    }
    fprintf(f, "  trace rows      : %u written, %u overflowed\r\n",
            g_shared->traceWrite, g_shared->traceOverflow);
    fprintf(f, "  frame rows      : %u written, %u overflowed\r\n",
            g_shared->frameWrite, g_shared->frameOverflow);
    fprintf(f, "\r\n");

    fprintf(f,
        "what this run does and does not prove\r\n"
        "  Proves            : the runtime was validated, the compilation stream was observed,\r\n"
        "                      and the targets it produced were matched by IL hash.\r\n"
        "  Does not prove    : that a prediction agrees with the game's collision decision.\r\n"
        "                      That is Phase 2 (L2), and nothing here is evidence for it.\r\n"
        "  A GAP is a check that was not made. It is never a pass. See selfcheck.txt.\r\n");
}

void WriteOffsetsCsv(FILE* f) {
    fprintf(f, "name,managed_offset,managed_static_base,managed_valid,is_static,"
               "jit_offset,jit_valid,agree\n");
    for (uint32_t i = 0; i < g_shared->offsetCount && i < DRELAY_MAX_READS; ++i) {
        const DrelayFieldOffset& o = g_shared->offsets[i];
        char name[DRELAY_FIELD_NAME_LEN] = {};
        CopyFixedName(o.name, sizeof(o.name), name, sizeof(name));
        // A STATIC FIELD'S `managed_offset` COLUMN IS AN ADDRESS, NOT AN OFFSET.
        //
        // It is printed as one, because the two halves of a 64-bit address do not
        // fit in the 32-bit field that holds an instance offset and the previous
        // version printed the low half as if it were a number of bytes.  Read the
        // column together with `is_static`: for a static it is `0x<high><low>`, and
        // `managed_static_base` repeats the high half so a consumer that ignores
        // this convention still sees both halves rather than a plausible wrong one.
        fprintf(f, "\"%s\",", name);
        if (o.isStatic != 0) {
            fprintf(f, "0x%08X%08X", o.managedStaticBase, o.managedOffset);
        } else {
            fprintf(f, "%u", o.managedOffset);
        }
        fprintf(f, ",%u,%u,%u,", o.managedStaticBase, o.managedValid, o.isStatic);
        if (o.jitValid) {
            fprintf(f, "%u,1,%u\n", o.jitOffset, o.agree);
        } else {
            fprintf(f, ",0,%u\n", o.agree);
        }
    }
    // Said in the file rather than only in the docs: a reader who opens
    // offsets.csv alone must not conclude that the empty jit column means
    // "agreed by absence".
    fprintf(f, "# jit_offset is empty until Phase 2 wires ICorJitInfo::getFieldOffset.\n");
    // THE PHASE 1 ORACLE, NAMED, BECAUSE THE COLUMN ALONE DOES NOT SAY WHICH ONE IT IS.
    fprintf(f, "# agree is written by whichever route confirmed the value. In Phase 1 that is the\n");
    fprintf(f, "# pointer chain: the offsets are used to walk World.Shared -> Player -> Position and\n");
    fprintf(f, "# the result is compared with the same state read through reflection. See summary.txt\n");
    fprintf(f, "# for the arm state and the two coordinate pairs that were compared, and selfcheck.txt\n");
    fprintf(f, "# for the verdict as a check.\n");
    // A STATIC FIELD'S COLUMNS, STATED IN THE FILE ITSELF.  The encoding is not
    // guessable from the header line and a wrong reading produces a real-looking
    // number, which is the worst kind of wrong.
    fprintf(f, "# For a row with is_static=1, managed_offset is 0x<managed_static_base><managed_offset>:\n");
    fprintf(f, "# a static field has an address rather than an instance offset, and an x64 address does\n");
    fprintf(f, "# not fit the 32-bit column.\n");
}

void WriteFramesCsv(FILE* f) {
    // THE COLUMNS ARE NAMED FOR WHAT THE VALUES ACTUALLY ARE.
    //
    // This read `arg_total_s`/`implied_hz`, which treated the detour's argument as a
    // frame *delta in seconds* and printed `1/arg` as a frequency.  It is neither.
    // `DarzaWindow.Update`'s one parameter is `GameTime.TotalGameTime.TotalMilliseconds`
    // -- see the call site at IL_00fc of `WindowsGame::Update` in the game's IL dump --
    // so it is a monotonically increasing TOTAL, in milliseconds, since the game
    // started.  Printed as seconds it reads as an absurd 27,824 s frame and `1/arg`
    // as 0.00 Hz, and a reader had no way to tell that the instrument was fine.
    //
    // The frame rate is the *increment* between consecutive rows, which is what a
    // reader actually wants and what makes this file evidence: 16.7 ms per call is
    // 60 Hz and is the proof that the detour is on the real per-frame method.
    fprintf(f, "seq,qpc,arg_total_ms,frame_delta_ms,value,read_ok,read_failures,elapsed_us\n");
    const uint32_t written = g_shared->frameWrite;
    if (written == 0) {
        return;
    }
    // Oldest first.  The ring holds the last N rows; if it wrapped, the oldest
    // surviving row is `written - N + 1`, and starting anywhere else would emit
    // the rows out of order with a silent discontinuity in the middle.
    const uint32_t first = (written > DRELAY_FRAME_RING_ROWS)
        ? written - DRELAY_FRAME_RING_ROWS + 1 : 1;
    // The delta is derived here rather than stored, so the shared layout does not
    // change for a reporting detail -- a struct change costs a version bump and a
    // regenerated managed layout, which is not worth a subtraction.
    bool havePrevious = false;
    float previousTotal = 0.0f;
    for (uint32_t seq = first; seq <= written; ++seq) {
        const DrelayFrameRow& row = g_shared->frames[(seq - 1) % DRELAY_FRAME_RING_ROWS];
        const float delta = havePrevious ? (row.argTotal - previousTotal) : 0.0f;
        previousTotal = row.argTotal;
        havePrevious = true;
        fprintf(f, "%u,%llu,%.6f,%.3f,%.4f,%u,%u,%u\n", seq,
                static_cast<unsigned long long>(row.qpc), row.argTotal, delta,
                row.value, row.readOk, row.readFailures, row.elapsedUs);
    }
}

// One JSON object per line, so the file is appendable, greppable and readable
// without a streaming parser -- the same shape the relay's own event log uses.
void WriteEventsJsonl(FILE* f) {
    if (g_shared->traceOverflow > 0) {
        fprintf(f, "{\"kind\":\"ring-overflow\",\"ring\":\"trace\",\"lost\":%u}\n",
                g_shared->traceOverflow);
    }
    const uint32_t traceWritten = g_shared->traceWrite;
    const uint32_t traceFirst = (traceWritten > DRELAY_TRACE_RING_ROWS)
        ? traceWritten - DRELAY_TRACE_RING_ROWS + 1 : 1;
    for (uint32_t seq = traceFirst; seq <= traceWritten; ++seq) {
        const DrelayTraceRow& row = g_shared->trace[(seq - 1) % DRELAY_TRACE_RING_ROWS];
        fprintf(f, "{\"kind\":\"compile\",\"seq\":%u,\"ilSize\":%u,\"nativeSize\":%u,"
                   "\"nativeEntry\":\"0x%llX\",\"token\":%u,\"matched\":",
                seq, row.ilSize, row.nativeSize,
                static_cast<unsigned long long>(row.nativeEntry), row.tokenRead);
        if (row.matchedTarget == 0xFFu) {
            fprintf(f, "null");
        } else {
            char name[DRELAY_TARGET_NAME_LEN] = {};
            if (row.matchedTarget < DRELAY_MAX_TARGETS) {
                CopyFixedName(g_shared->targets[row.matchedTarget].name,
                              sizeof(g_shared->targets[row.matchedTarget].name),
                              name, sizeof(name));
            }
            fprintf(f, "\"%s\"", name);
        }
        fprintf(f, ",\"sizeMatchedTarget\":%s,\"ilHash\":\"",
                row.isTargetCandidate ? "true" : "false");
        WriteHex(f, row.ilHash, 32);
        fprintf(f, "\"}\n");
    }

    if (g_shared->logOverflow > 0) {
        fprintf(f, "{\"kind\":\"ring-overflow\",\"ring\":\"log\",\"lost\":%u}\n",
                g_shared->logOverflow);
    }
    const uint32_t logWritten = g_shared->logWrite;
    const uint32_t logFirst = (logWritten > DRELAY_LOG_RING_LINES)
        ? logWritten - DRELAY_LOG_RING_LINES + 1 : 1;
    for (uint32_t seq = logFirst; seq <= logWritten; ++seq) {
        const DrelayLogLine& line = g_shared->log[(seq - 1) % DRELAY_LOG_RING_LINES];
        // The text is escaped rather than trusted: a log line can contain a quote
        // (a path, an exception message) and an unescaped one breaks every
        // consumer of the file.
        fprintf(f, "{\"kind\":\"log\",\"seq\":%u,\"level\":%u,\"text\":\"", seq, line.level);
        for (size_t c = 0; c < DRELAY_LOG_LINE_LEN && line.text[c] != '\0'; ++c) {
            const unsigned char ch = static_cast<unsigned char>(line.text[c]);
            switch (ch) {
                case '"':  fputs("\\\"", f); break;
                case '\\': fputs("\\\\", f); break;
                case '\n': fputs("\\n", f);  break;
                case '\r': break;
                case '\t': fputs("\\t", f);  break;
                default:
                    if (ch < 0x20) {
                        fprintf(f, "\\u%04X", ch);
                    } else {
                        fputc(ch, f);
                    }
                    break;
            }
        }
        fprintf(f, "\"}\n");
    }
}

} // namespace

void DrelayWriteEvidence(const wchar_t* runDir) {
    if (runDir == nullptr || runDir[0] == L'\0' || g_shared == nullptr) {
        return;
    }

    if (FILE* f = OpenIn(runDir, L"summary.txt")) {
        WriteSummary(f);
        fclose(f);
        DrLog(DRLOG_INFO, "wrote summary.txt");
    } else {
        DrLog(DRLOG_WARN, "could not write summary.txt");
    }

    if (FILE* f = OpenIn(runDir, L"offsets.csv")) {
        WriteOffsetsCsv(f);
        fclose(f);
    } else {
        DrLog(DRLOG_WARN, "could not write offsets.csv");
    }

    if (FILE* f = OpenIn(runDir, L"frames.csv")) {
        WriteFramesCsv(f);
        fclose(f);
    } else {
        DrLog(DRLOG_WARN, "could not write frames.csv");
    }

    if (FILE* f = OpenIn(runDir, L"events.jsonl")) {
        WriteEventsJsonl(f);
        fclose(f);
        DrLog(DRLOG_INFO, "wrote events.jsonl (%u trace row(s), %u log line(s))",
              g_shared->traceWrite, g_shared->logWrite);
    } else {
        DrLog(DRLOG_WARN, "could not write events.jsonl");
    }
}

} // namespace drelay
