// DrelaySelfCheck.cpp -- the hook's acceptance test, as code rather than as a
// checklist someone is expected to remember.
//
// The point of running this on every start is the failure mode it converts:
// without it, a hook that installed against the wrong build, or that never saw a
// target because the method was inlined, presents as "the dodge is bad" -- a
// behavioural symptom with no diagnostic.  With it, each of those is one line
// naming the component that moved.

#include "DrelaySelfCheck.h"
#include "DrelayFrame.h"
#include "DrelayGlobals.h"
#include "DrelayLog.h"

#include <stdio.h>
#include <stdarg.h>
#include <string.h>

namespace drelay {

namespace {

struct CheckLine {
    DrelayStatus status;
    char         text[220];
};

CheckLine g_lines[64] = {};
uint32_t  g_lineCount = 0;

void AddCheck(DrelayStatus status, const char* fmt, ...) {
    if (g_lineCount >= 64) {
        return;
    }
    CheckLine& line = g_lines[g_lineCount];
    line.status = status;
    va_list args;
    va_start(args, fmt);
    _vsnprintf_s(line.text, sizeof(line.text), _TRUNCATE, fmt, args);
    va_end(args);
    ++g_lineCount;
}

const char* StatusWord(DrelayStatus status) {
    switch (status) {
        case DRELAY_STATUS_OK:          return "PASS";
        case DRELAY_STATUS_GAP:         return "GAP ";
        case DRELAY_STATUS_REFUSED:     return "FAIL";
        case DRELAY_STATUS_FAILED:      return "FAIL";
        case DRELAY_STATUS_TIMEOUT:     return "FAIL";
        case DRELAY_STATUS_DISABLED:    return "OFF ";
        case DRELAY_STATUS_UNSUPPORTED: return "FAIL";
        default:                        return "????";
    }
}

// A GUID's 16 bytes as `Guid.ToString("D")` prints them.
//
// The first three components are little-endian integers in the CLI's storage order
// and big-endian in the canonical text, so the byte order is reversed for each of
// them and preserved for the last two.  `DrelayEvidence.cpp` does the same
// reversal inline for `summary.txt`; doing it here as well is deliberate
// duplication of four index expressions in exchange for `selfcheck.txt` and
// `summary.txt` never being able to disagree about how to print an identity.
void FormatCanonicalGuid(const uint8_t* bytes, char* out, size_t cap) {
    _snprintf_s(out, cap, _TRUNCATE,
                "%02X%02X%02X%02X-%02X%02X-%02X%02X-%02X%02X-%02X%02X%02X%02X%02X%02X",
                bytes[3], bytes[2], bytes[1], bytes[0],
                bytes[5], bytes[4], bytes[7], bytes[6],
                bytes[8], bytes[9], bytes[10], bytes[11],
                bytes[12], bytes[13], bytes[14], bytes[15]);
}

// The arming gate's state as a word, so `selfcheck.txt` and `summary.txt` name it
// the same way `hook.log` does.
const char* ArmStateWord(uint32_t state) {
    switch (state) {
        case DRELAY_ARM_WAITING:      return "WAITING (no world yet)";
        case DRELAY_ARM_ARMED:        return "ARMED";
        case DRELAY_ARM_REFUSED:      return "REFUSED (the cross-check disagreed)";
        case DRELAY_ARM_UNCONFIGURED: return "UNCONFIGURED (no offsets were measured)";
        default:                      return "unknown";
    }
}

// A field offset as it should be printed, which depends on whether it is one.
//
// `World.Shared` is static, so what was measured is an ADDRESS: on x64 that does
// not fit in the 32-bit `managedOffset` field, and the pair carries it -- see the
// note on `DrelayFieldOffset` in DrelayHookShared.h.  Printing `managedOffset`
// alone for that row prints the low half of a pointer and calls it an offset,
// which is what the previous version did.
void FormatFieldValue(const DrelayFieldOffset& o, char* out, size_t cap) {
    if (o.isStatic != 0) {
        _snprintf_s(out, cap, _TRUNCATE, "0x%08X%08X (static address)",
                    o.managedStaticBase, o.managedOffset);
    } else {
        _snprintf_s(out, cap, _TRUNCATE, "+%u", o.managedOffset);
    }
}

// A coordinate pair, or "(not read)" when the chain was refused before it read
// anything.  Duplicated from DrelayInstall.cpp's helper rather than shared: the
// alternative is a new header for six lines, and the two callers must agree about
// "absent" rather than about formatting.
void FormatPoint(char* out, size_t cap, float x, float y) {
    if (x != x || y != y) {
        _snprintf_s(out, cap, _TRUNCATE, "(not read)");
    } else {
        _snprintf_s(out, cap, _TRUNCATE, "(%.4f, %.4f)", x, y);
    }
}

} // namespace

bool DrelayRunSelfChecks(DrelayInstallReport* report) {
    g_lineCount = 0;

    // 1. The runtime is loaded.  Nothing else can be checked without it.
    AddCheck(DRELAY_STATUS_OK, "1  clrjit.dll and coreclr.dll loaded");

    // 2. The JIT object and its vtable.  What has already been proved by
    //    DrelayRuntimeValidate is restated here so the file stands alone.
    if (g_jitObject != nullptr && g_jitVtable != nullptr) {
        AddCheck(DRELAY_STATUS_OK, "2  getJit resolved; vtable at %p; 4 ICorJitCompiler slots",
                 static_cast<void*>(g_jitVtable));
    } else {
        AddCheck(DRELAY_STATUS_FAILED, "2  getJit or its vtable could not be resolved");
    }

    // 3. The JIT/EE GUID.  Reported from the value that was actually read, so a
    //    reader can see the runtime's own answer rather than our expectation.
    AddCheck(DRELAY_STATUS_OK, "3  JIT/EE GUID read via vtbl[2]: %02X%02X%02X%02X-%02X%02X-...",
             g_shared->jitGuid[0], g_shared->jitGuid[1], g_shared->jitGuid[2], g_shared->jitGuid[3],
             g_shared->jitGuid[4], g_shared->jitGuid[5]);

    // 4. Tiering.  This is the assumption the design rests on, so it is stated
    //    as a fact about the environment rather than inferred from behaviour.
    if (g_shared->effectiveTieredCompilation == 0) {
        AddCheck(DRELAY_STATUS_OK, "4  effective TieredCompilation is off");

        // 4b. ...and the *behavioural* confirmation, which is the check that
        //     Phase 1 can actually make: with tiering off each method compiles
        //     exactly once.  A second compilation of a target is logged by the
        //     detour itself, and its absence is evidence, not proof -- so this is
        //     reported as what it is.
        AddCheck(DRELAY_STATUS_GAP, "4b a target compiled twice would prove tiering on; "
                                    "none observed so far (behavioural, not a proof)");
    } else if (g_shared->effectiveTieredCompilation == static_cast<uint32_t>(-1)) {
        AddCheck(DRELAY_STATUS_GAP, "4  the effective tiering setting could not be read");
    } else {
        AddCheck(DRELAY_STATUS_FAILED, "4  TieredCompilation is ON, which defeats the design");
    }

    // 5. The loaded assembly's identity.
    AddCheck(DRELAY_STATUS_OK, "5  assembly identity: MVID %02X%02X%02X%02X-... size %lld",
             g_shared->mvid[0], g_shared->mvid[1], g_shared->mvid[2], g_shared->mvid[3],
             static_cast<long long>(g_shared->managedAssemblySize));

    // 5a. THE COMPARISON THAT MAKES CHECK 5 MEAN SOMETHING, AND IT WAS MISSING HERE.
    //
    // Check 5 above states the MVID the runtime reported.  That is a fact about the
    // process; it says nothing about whether the process is running the build the
    // fingerprint gate pinned.  The comparison against the file on disk is what turns
    // it into an answer, and it was made, counted, logged -- and never recorded as a
    // check, so `selfcheck.txt` could not tell a reader that the loaded assembly had
    // been verified against the file.  A refusal was worse: the totals moved and no
    // line named the two values that differed.
    //
    // Printed in canonical GUID order -- the order `Guid.ToString("D")` and
    // `known-build.json` use -- rather than in the CLI's on-disk byte order, so this
    // line and `bootstrap.log`'s `loaded assembly MVID ... CBF722C0-...` can be
    // compared by eye.  A diagnostic that requires reversing the first three fields
    // in one's head is a diagnostic that gets misread.
    if (!g_fileMvidRead) {
        AddCheck(DRELAY_STATUS_GAP, "5a the file's MVID could not be read, so the loaded "
                                    "assembly was never checked against it (a GAP, not a pass)");
    } else {
        char loadedText[40] = {};
        char fileText[40] = {};
        FormatCanonicalGuid(g_shared->mvid, loadedText, sizeof(loadedText));
        FormatCanonicalGuid(g_fileMvid, fileText, sizeof(fileText));
        if (memcmp(g_shared->mvid, g_fileMvid, 16) == 0) {
            AddCheck(DRELAY_STATUS_OK, "5a loaded assembly verified against the file: %s", fileText);
        } else {
            AddCheck(DRELAY_STATUS_FAILED,
                     "5a loaded assembly is NOT the file on disk: runtime %s, file %s",
                     loadedText, fileText);
        }
    }

    // 6. The JIT detour.
    if ((g_shared->nativeStatus & DRELAY_ST_JIT_DETOURED) != 0) {
        AddCheck(DRELAY_STATUS_OK, "6  compileMethod detoured (slot 0)");
    } else {
        AddCheck(DRELAY_STATUS_FAILED, "6  compileMethod was not detoured");
    }

    if (report != nullptr) {
        report->passed = 0;
        report->failed = 0;
        report->gaps = 0;
        for (uint32_t i = 0; i < g_lineCount; ++i) {
            switch (g_lines[i].status) {
                case DRELAY_STATUS_OK:   report->passed++; break;
                case DRELAY_STATUS_GAP:  report->gaps++;   break;
                default:                 report->failed++; break;
            }
        }
    }
    return report == nullptr || report->failed == 0;
}

void DrelayRunLiveChecks(DrelayInstallReport* report) {
    // The single most important live check, and it is first because everything
    // else is conditional on it.
    //
    // The whole hook design assumes a `compileMethod` detour observes the
    // compilation stream.  Phase 1 measured that assumption instead of trusting
    // it: the detour was installed two different ways, and under the documented
    // way it was *never entered* before the process died -- so the check below
    // reports the count rather than a boolean, and a zero is the finding.
    if (g_compileCalls.load() == 0) {
        AddCheck(DRELAY_STATUS_FAILED, "6a the compileMethod detour was installed but NEVER "
                                       "ENTERED: the runtime does not compile through the patched "
                                       "entry point, so this hook point does not observe the "
                                       "compilation stream");
    } else {
        AddCheck(DRELAY_STATUS_OK, "6a the compileMethod detour was entered %u time(s)",
                 g_compileCalls.load());
    }

    // 6 (continued).  Which targets the compilation stream actually produced.
    for (uint32_t i = 0; i < g_shared->targetCount; ++i) {
        const DrelayTargetHash& t = g_shared->targets[i];
        char name[DRELAY_TARGET_NAME_LEN] = {};
        memcpy(name, t.name, sizeof(name) - 1);
        if (t.found != 0) {
            AddCheck(DRELAY_STATUS_OK,
                     "6  target '%s' found by IL hash: native %u bytes at %p, token 0x%08X",
                     name, t.nativeSize, reinterpret_cast<void*>(t.nativeEntry), t.tokenRead);
        } else {
            AddCheck(DRELAY_STATUS_FAILED,
                     "6  target '%s' was NEVER compiled in the discovery window", name);
        }
    }

    // 7. Offsets resolved, and -- which is the half that was missing -- confirmed
    //    by a route independent of the measurement that produced them.
    //
    //    `agree` is what the pointer chain writes when it reproduces what
    //    reflection says.  It is a different oracle from `jitValid` (Phase 2's),
    //    and the two are reported differently because they are different claims:
    //    "the JIT said so" and "a live walk through the offsets said so".  A row
    //    with neither is a GAP, not a pass.
    if (g_shared->offsetCount == 0) {
        AddCheck(DRELAY_STATUS_FAILED, "7  no field offsets were published");
    } else {
        for (uint32_t i = 0; i < g_shared->offsetCount && i < DRELAY_MAX_READS; ++i) {
            const DrelayFieldOffset& o = g_shared->offsets[i];
            char name[DRELAY_FIELD_NAME_LEN] = {};
            char value[64] = {};
            memcpy(name, o.name, sizeof(name) - 1);
            FormatFieldValue(o, value, sizeof(value));
            if (o.managedValid == 0) {
                AddCheck(DRELAY_STATUS_FAILED, "7  offset '%s' was not measured", name);
            } else if (o.jitValid != 0) {
                if (o.agree != 0) {
                    AddCheck(DRELAY_STATUS_OK, "7  offset '%s' = %s confirmed by both sources",
                             name, value);
                } else {
                    AddCheck(DRELAY_STATUS_FAILED, "7  offset '%s' DISAGREES: managed %s, JIT %u",
                             name, value, o.jitOffset);
                }
            } else if (o.agree != 0) {
                AddCheck(DRELAY_STATUS_OK, "7  offset '%s' = %s, confirmed by the pointer chain "
                                           "(the JIT-side oracle is Phase 2's)", name, value);
            } else {
                AddCheck(DRELAY_STATUS_GAP, "7  offset '%s' = %s (managed reflection); no second "
                                            "route has confirmed it yet", name, value);
            }
        }
    }

    // 7b. THE CROSS-CHECK ITSELF, AND ITS FOUR DIFFERENT OUTCOMES.
    //
    // This is the check that Phase 1 could not make for its whole first life: the
    // pointer chain needs a live world, the hook attaches before there is one, and
    // so the check was attempted once at attach, always found `World.Shared` null,
    // and reported a GAP -- while the GAP silently disabled the sensor and
    // so kept Spike 1.3's success test from ever running.  It is now made by the
    // arming probe the first time a world exists.
    //
    // The four states are reported as four different things on purpose.  "No world
    // yet" is a gap; "the offsets disagree" is a failure; "there were never any
    // offsets" is a failure with a different owner; and "armed" is a pass that
    // names the numbers it compared.
    {
        char reflected[64] = {};
        char pointer[64] = {};
        FormatPoint(reflected, sizeof(reflected),
                    g_shared->managedCrossCheckRefX, g_shared->managedCrossCheckRefY);
        FormatPoint(pointer, sizeof(pointer),
                    g_shared->managedCrossCheckPtrX, g_shared->managedCrossCheckPtrY);
        switch (g_shared->managedArmState) {
            case DRELAY_ARM_ARMED:
            {
                // THE STRENGTH OF THE AGREEMENT IS PART OF THE CHECK.
                //
                // A live run showed the world exists while a scene is still loading, so
                // this comparison can be made against a default `GamePoint` of (0, 0) --
                // and two zeros agree for any pair of addresses, including a wrong one.
                // Saying so is the difference between a check a reader can weigh and one
                // they will over-trust.
                const bool trivial = (g_shared->managedCrossCheckRefX == 0.0f &&
                                      g_shared->managedCrossCheckRefY == 0.0f &&
                                      g_shared->managedCrossCheckPtrX == 0.0f &&
                                      g_shared->managedCrossCheckPtrY == 0.0f);
                if (trivial) {
                    AddCheck(DRELAY_STATUS_OK,
                             "7b the pointer chain reproduces reflection: Player.Position %s by "
                             "both routes, so the offsets describe this build; armed at t+%u ms. "
                             "NOTE: both routes returned exactly zero, so this agreement is "
                             "trivial and the MethodTable identity of World.Player is what "
                             "carries the verification",
                             reflected, g_shared->managedArmTick);
                } else {
                    AddCheck(DRELAY_STATUS_OK,
                             "7b the pointer chain reproduces reflection: Player.Position %s by "
                             "both routes, so the offsets describe this build; the frame read path "
                             "was armed at t+%u ms", reflected, g_shared->managedArmTick);
                }
                break;
            }
            case DRELAY_ARM_REFUSED:
                AddCheck(DRELAY_STATUS_FAILED,
                         "7b the pointer chain DISAGREES with reflection: reflection %s, pointer "
                         "chain %s -- the offsets do not describe this build's layout and the "
                         "frame read path is NOT open", reflected, pointer);
                break;
            case DRELAY_ARM_UNCONFIGURED:
                AddCheck(DRELAY_STATUS_FAILED,
                         "7b no field offsets were ever published, so the pointer chain could not "
                         "be checked against a world and the frame read path cannot open");
                break;
            default:
                AddCheck(DRELAY_STATUS_GAP,
                         "7b no live player existed while the hook was watching (%u probe(s) and "
                         "%u frame(s) found none), so the pointer chain has never been "
                         "cross-checked against a live object graph; the frame read path is NOT "
                         "armed", g_shared->managedProbeWaits, g_shared->managedFrameWaits);
                break;
        }
    }

    // 8. The bridge.  Phase 4's, and reported as a gap rather than as a pass so
    //    that nobody reads this file as evidence of a working bridge.
    AddCheck(DRELAY_STATUS_GAP, "8  the relay bridge is Phase 4 and is not present");

    // The frame tick, which is Spike 1.3's whole subject.
    //
    // WHICH PATH THE FRAME READ TAKES IS NOW A MODE, AND THIS CHECK HAS TO KNOW IT.
    // Phase 1's frame read was a native detour.  Phase 2 replaced it for the sensor's
    // purposes with a *managed* replacement: the JIT-compiled body is patched to jump
    // straight into a managed method, and the native detour is not installed at all -- so
    // `g_frameCalls` is zero BY CONSTRUCTION on every such run.  Reporting that as "the
    // detour is installed at the wrong address" was a false alarm on every managed-
    // replacement run, including the sixteen-minute live session of 2026-10-12, where the
    // bootstrap logged 54 599 managed entries and this file reported FAIL 9 in the same
    // breath.  A check that fires when it is not being asked anything is worse than no
    // check: it teaches its reader to skim the file.
    //
    // A GAP AND NOT A PASS, because this file cannot see the managed side's counter.  The
    // native side does not own that number and must not report having measured it; the
    // evidence for that mode is the bootstrap log's "MANAGED REPLACEMENT entered" line and
    // the sensor's own frame count.
    const bool managedFramePath = (g_shared != nullptr &&
                                   g_shared->managedFrameReplacement != 0);
    const uint64_t frames = g_frameCalls.load();
    if (managedFramePath) {
        AddCheck(DRELAY_STATUS_GAP,
                 "9  the frame read is a managed replacement on this run, so the native "
                 "frame detour is not installed and its counter is 0 by construction; the "
                 "managed entry is evidenced by 'MANAGED REPLACEMENT entered' in the "
                 "bootstrap log, not here");
    } else if (!DrelayIsFrameTargetPatched()) {
        AddCheck(DRELAY_STATUS_FAILED, "9  the frame tick was not patched");
    } else if (frames == 0) {
        AddCheck(DRELAY_STATUS_FAILED, "9  the frame tick is patched but has never been entered "
                                       "-- the detour is installed at the wrong address, or the "
                                       "game has not rendered a frame");
    } else {
        AddCheck(DRELAY_STATUS_OK, "9  frame tick entered %llu times, worst call %u us",
                 static_cast<unsigned long long>(frames), g_frameMaxUs.load());
        // The third unverified assumption from Spike 1.2: is the address we
        // patched the address that executes?  Entering the detour at all is the
        // answer -- a patch on a dead copy would never be reached.
        AddCheck(DRELAY_STATUS_OK, "9b the JIT-reported address is an executing address "
                                   "(the detour is being entered)");
    }

    // 9c. SPIKE 1.3'S ACTUAL SUCCESS TEST, WHICH HAD NEVER RUN.
    //
    // The phase's exit criterion for this spike is "read exactly one float from the
    // live world".  Until this round the callback that would do the reading was
    // disabled at attach -- because `World.Shared` is null before the game loads a
    // scene -- so every run reported `frame reads: 0 ok, 0 failed` and the test
    // had never executed.  The arming probe is what makes a world a prerequisite
    // that gets waited for instead of a reason to give up.
    //
    // FOUR OUTCOMES, AND ONLY THE FIRST TWO ARE ABOUT A GAME.  "Not armed" and "no
    // frame has entered it yet" are gaps -- the check could not be made.  A failure
    // is reserved for the one state that cannot be explained by the game: armed,
    // frames entered, and neither a success nor a failure recorded, which means the
    // callback is returning before it reads and the counters are not describing the
    // read path at all.
    {
        const uint64_t framesEntered = g_frameCalls.load();
        const uint32_t readsOk = g_shared->managedReadCount;
        const uint32_t readsFailed = g_shared->managedReadFailures;
        if (managedFramePath) {
            // The same mode question as check 9, one level down: `managedReadCount` is
            // incremented by the NATIVE frame read, so on a managed-replacement run it is
            // zero for the same reason `g_frameCalls` is, and "no frame has entered the
            // detour" would be a statement about a detour that does not exist.  The
            // managed path's own evidence is in the bootstrap log.
            AddCheck(DRELAY_STATUS_GAP,
                     "9c the frame read is a managed replacement on this run, so the native "
                     "read counters (%u ok, %u failed) describe a path that is not in use; "
                     "Spike 1.3's success test is the bootstrap log's sensor lines",
                     readsOk, readsFailed);
        } else if (g_shared->managedArmState != DRELAY_ARM_ARMED) {
            AddCheck(DRELAY_STATUS_GAP,
                     "9c no live read was made: the frame read path is closed (arm state: %s); "
                     "%u probe(s) and %u frame(s) found no live player",
                     ArmStateWord(g_shared->managedArmState), g_shared->managedProbeWaits,
                     g_shared->managedFrameWaits);
        } else if (readsOk > 0) {
            AddCheck(DRELAY_STATUS_OK,
                     "9c live world read: %u successful frame read(s), %u failed, latest "
                     "Player.Position.X = %.4f -- this is Spike 1.3's success test",
                     readsOk, readsFailed, g_shared->managedReadValue);
        } else if (framesEntered == 0) {
            AddCheck(DRELAY_STATUS_GAP,
                     "9c the read path is open but no frame has entered the detour yet, so nothing has "
                     "been read");
        } else if (readsFailed > 0) {
            AddCheck(DRELAY_STATUS_GAP,
                     "9c the read path is open and frames ran, but every read found no usable "
                     "value (%u failure(s)) -- the world was gone again by the time a frame "
                     "reached the callback. This is NOT evidence the read path works",
                     readsFailed);
        } else {
            AddCheck(DRELAY_STATUS_FAILED,
                     "9c the read path is open and %llu frame(s) entered the detour, but neither a "
                     "success nor a failure was recorded: the callback returns before it reads, "
                     "which is an instrument fault and not a game fact",
                     static_cast<unsigned long long>(framesEntered));
        }
    }

    if (report != nullptr) {
        for (uint32_t i = 0; i < g_lineCount; ++i) {
            switch (g_lines[i].status) {
                case DRELAY_STATUS_OK:   report->passed++; break;
                case DRELAY_STATUS_GAP:  report->gaps++;   break;
                default:                 report->failed++; break;
            }
        }
    }
}

void DrelayWriteSelfCheckFile(const DrelayInstallReport& report) {
    if (g_runDir[0] == L'\0') {
        return;
    }
    wchar_t path[MAX_PATH] = {};
    _snwprintf_s(path, MAX_PATH, _TRUNCATE, L"%s\\selfcheck.txt", g_runDir);

    FILE* f = nullptr;
    if (_wfopen_s(&f, path, L"wb") != 0 || f == nullptr) {
        DrLog(DRLOG_WARN, "could not write %ls", path);
        return;
    }

    fprintf(f, "drelay hook self-check\r\n");
    fprintf(f, "PASSED %u  FAILED %u  GAP %u\r\n",
            report.passed, report.failed, report.gaps);
    fprintf(f, "A GAP is not a pass: it records a check that could not be made here.\r\n\r\n");
    for (uint32_t i = 0; i < g_lineCount; ++i) {
        fprintf(f, "%-4s %s\r\n", StatusWord(g_lines[i].status), g_lines[i].text);
    }
    fclose(f);
}

} // namespace drelay
