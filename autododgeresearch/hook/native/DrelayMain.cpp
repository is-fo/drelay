// DrelayMain.cpp -- the injected DLL's entry points and the watcher thread.
//
// LIFECYCLE, AND WHY IT IS SHAPED THIS WAY
// ----------------------------------------
// The launcher injects this DLL into a suspended process via a remote
// CreateRemoteThread(LoadLibraryW) call, and then waits on that thread.  Two
// rules follow, and both are Microsoft's rather than this project's invention:
//
//   1. DllMain does nothing but DisableThreadLibraryCalls and return.  The
//      loader lock is held during DllMain, CoreCLR registers its own DllMain,
//      and LoadLibrary/CreateThread inside the loader lock is a documented way
//      to deadlock a process before it has printed anything.
//   2. The real work happens in DrelayHook_Init, an exported function the
//      injector calls after LoadLibraryW has returned.  It must not block,
//      because the launcher's remote thread is the thing waiting on it -- so it
//      spawns a watcher and returns immediately.
//
// The watcher's whole job is to answer "when is it safe to touch the JIT?", and
// the answer is "not until the bootstrap says so".  The bootstrap runs from
// DOTNET_STARTUP_HOOKS, which the host invokes before the game's entry point,
// which means the signal it sends is a guarantee that CoreCLR is up, the game
// assembly is loaded, and the game's own Main has not run.  Waiting on that
// signal instead of polling for clrjit.dll is what makes the ordering a fact
// rather than a hope: the JIT detour is installed after the runtime exists and
// before the first game method is compiled.

#include "DrelayGlobals.h"
#include "DrelayCrash.h"
#include "DrelayEvidence.h"
#include "DrelayFrame.h"
#include "DrelayInstall.h"
#include "DrelayLog.h"
#include "DrelayPe.h"
#include "DrelaySelfCheck.h"

#include <windows.h>
#include <tlhelp32.h>   // the process census in DrLogProcessIdentity
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>      // _wcsicmp

namespace drelay {

// Shutdown is requested from the exported DrelayHook_Shutdown, which runs on the
// injector's thread, and observed by the watcher.  It is an interlocked flag
// rather than an event, because the watcher can be blocked in Sleep and a flag
// needs no owner to reset.
//
// The definition lives here; the declaration is in DrelayGlobals.h because the
// collection delay in DrelayInstall.cpp polls it too.
volatile LONG g_shutdownRequested = 0;

// Handles the exported entry points also need, so they are at namespace scope
// rather than file-static.
HANDLE g_watcher = nullptr;
HANDLE g_nativeReadyEvent = nullptr;
HANDLE g_bootReadyEvent = nullptr;

namespace {

// Bounded because the mapping is created by the launcher before either side
// knows the other's struct size.  Both sides compute the same constant, and a
// mismatch is a refusal rather than a reinterpretation of a foreign layout.
uint64_t ExpectedSharedSize() {
    return static_cast<uint64_t>(sizeof(DrelayShared));
}

bool ReadEnvU32(const char* name, uint32_t* out) {
    char buf[32] = {};
    const DWORD n = GetEnvironmentVariableA(name, buf, sizeof(buf));
    if (n == 0 || n >= sizeof(buf)) {
        return false;
    }
    *out = static_cast<uint32_t>(strtoul(buf, nullptr, 10));
    return true;
}

bool ReadEnvW(const char* name, wchar_t* out, size_t capChars) {
    wchar_t wide[64] = {};
    if (MultiByteToWideChar(CP_ACP, 0, name, -1, wide, 64) == 0) {
        return false;
    }
    const DWORD n = GetEnvironmentVariableW(wide, out, static_cast<DWORD>(capChars));
    return n != 0 && n < capChars;
}

// ---------------------------------------------------------------------------
// PROCESS IDENTITY: is the process being watched the process being played?
//
// WHY THIS EXISTS.  The most expensive wrong turn in section 11 was the conclusion
// that "the monitored process is not the one the operator plays, so the frame loop
// runs somewhere else".  It is a reasonable hypothesis -- Steam relaunches games,
// launchers spawn children, and a game that calls SteamAPI_RestartAppIfNecessary
// does exactly that -- and it was never measured, only argued from a flat
// compilation count.  It was also wrong: the game's own log for that run shows the
// hooked pid loading assets, entering GameScene and connecting to a server.
//
// The lesson is the one section 8 keeps recording: an unmeasured hypothesis that
// explains the evidence gets adopted.  So the hook now measures it every run, from
// inside the process under test, where the answer is cheap:
//
//   * the process's own pid and image path, so the pid in hook.log can be tied to
//     an executable rather than to a number;
//   * every other process on the machine with the same image NAME, with its pid and
//     parent pid, so a relaunch or a child is visible as a line rather than inferred;
//   * which process owns the FOREGROUND WINDOW, sampled while the run is live.  If
//     the operator is playing, this is the process receiving the input, and if it is
//     not ours then the two processes really have diverged -- which is the one fact
//     the hypothesis needed and did not have.
//
// Toolhelp32 rather than WMI: this runs before the runtime exists and must not load
// anything, and PROCESSENTRY32 carries th32ParentProcessID directly.
// ---------------------------------------------------------------------------

struct SameNameProcess {
    DWORD pid;
    DWORD parentPid;
};

// Returns how many processes share our image name (excluding ourselves) and fills
// up to `cap` of them.  Never fails loudly: a snapshot that cannot be taken is a
// missing line, not a failed run.
uint32_t FindSameNameProcesses(wchar_t* ownName, size_t cap, SameNameProcess* out, uint32_t capOut) {
    if (GetModuleFileNameW(nullptr, ownName, static_cast<DWORD>(cap)) == 0) {
        ownName[0] = L'\0';
        return 0;
    }
    const wchar_t* base = wcsrchr(ownName, L'\\');
    base = (base != nullptr) ? base + 1 : ownName;

    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) {
        return 0;
    }
    PROCESSENTRY32W entry = {};
    entry.dwSize = sizeof(entry);
    const DWORD self = GetCurrentProcessId();
    uint32_t count = 0;
    if (Process32FirstW(snap, &entry)) {
        do {
            if (entry.th32ProcessID == self) {
                continue;
            }
            if (_wcsicmp(entry.szExeFile, base) != 0) {
                continue;
            }
            if (count < capOut) {
                out[count].pid = entry.th32ProcessID;
                out[count].parentPid = entry.th32ParentProcessID;
            }
            ++count;
        } while (Process32NextW(snap, &entry));
    }
    CloseHandle(snap);
    return count;
}

// The title of the window that currently has focus, and the pid that owns it, or
// pid 0 when there is none.  A hook that cannot see the foreground window is a hook
// that cannot say whether its own window is the one being driven.
DWORD ForegroundWindowPid(wchar_t* title, size_t cap) {
    if (title != nullptr && cap > 0) {
        title[0] = L'\0';
    }
    HWND window = GetForegroundWindow();
    if (window == nullptr) {
        return 0;
    }
    DWORD pid = 0;
    GetWindowThreadProcessId(window, &pid);
    if (title != nullptr && cap > 0) {
        GetWindowTextW(window, title, static_cast<int>(cap));
    }
    return pid;
}

void DrLogProcessIdentity(const char* when) {
    wchar_t own[MAX_PATH] = {};
    SameNameProcess others[8] = {};
    const uint32_t othersCount = FindSameNameProcesses(own, MAX_PATH, others, 8);

    wchar_t title[128] = {};
    const DWORD fgPid = ForegroundWindowPid(title, 128);
    const DWORD self = GetCurrentProcessId();

    DrLog(DRLOG_INFO, "process identity (%s): pid %lu, image %ls, foreground window pid %lu%s, "
                      "%u other process(es) named %ls",
          when, self, own[0] != L'\0' ? own : L"(unknown)",
          fgPid, (fgPid == self) ? " (ours)" : (fgPid == 0 ? " (none)" : ""),
          othersCount, own[0] != L'\0' ? (wcsrchr(own, L'\\') != nullptr ? wcsrchr(own, L'\\') + 1 : own) : L"(unknown)");

    // A second instance with the same image name is the exact shape of the
    // "the operator is playing somewhere else" hypothesis, so it is spelled out
    // rather than left as a count.
    for (uint32_t i = 0; i < othersCount && i < 8; ++i) {
        const bool child = (others[i].parentPid == self);
        DrLog(DRLOG_WARN, "  another %ls is running: pid %lu, parent pid %lu%s -- a second "
                          "instance means the frame loop may be in that one, not in pid %lu",
              wcsrchr(own, L'\\') != nullptr ? wcsrchr(own, L'\\') + 1 : own,
              others[i].pid, others[i].parentPid,
              child ? " (a CHILD of this process)" : "", self);
    }
}

// The watcher.  Every step logs before it acts, so the last line of hook.log
// names the step a crash happened in -- which is worth more than a stack trace
// for code that runs before any debugger is attached.
DWORD WINAPI WatcherThread(LPVOID) {
    // STDOUT DEBUG TRACE FIRST, BEFORE ANYTHING CAN FAIL.
    //
    // The watcher was dying before its first log line, and the log is the only
    // evidence this code produces -- so the failure was invisible by construction.
    // OutputDebugString needs no shared block, no file and no path, so it survives
    // every failure mode this thread has; it is written to first, unconditionally,
    // before any other statement runs.
    OutputDebugStringA("drelay-hook: watcher thread entered\r\n");

    // THE BISECT, AND IT IS THE FIRST THING TO RUN BECAUSE IT COSTS NOTHING.
    //
    // Several rounds of elimination have proved individually that each step the
    // watcher performs is harmless, and the process still dies while the thread is
    // idle. This switch does nothing at all and then sleeps forever, which bisects
    // that: survive means the fault is in one of the steps that each passed its own
    // bracket; die means the fault is the injection or the thread's mere existence,
    // and no amount of work inside the watcher can fix it.
    //
    // THE NAME DOES NOT START WITH `DRELAY_HOOK_`, AND THAT MATTERS. The launcher
    // builds the child's environment by deleting every variable with that prefix, so
    // a flag named `DRELAY_HOOK_SKIP_EVERYTHING` is stripped before the child sees
    // it -- which is exactly how an earlier control silently became a no-op and
    // nearly produced a wrong conclusion. See PHASE1-HANDOFF.md trap 1.
    char skipAll[8] = {};
    if (GetEnvironmentVariableA("DRELAY_SKIP_EVERYTHING", skipAll, sizeof(skipAll)) != 0 &&
        skipAll[0] == '1') {
        OutputDebugStringA("drelay-hook: DRELAY_SKIP_EVERYTHING=1 -- idle loop, nothing else\r\n");
        for (;;) {
            Sleep(200);
        }
    }

    // 1. Open the shared block.
    //
    //    The block is a FILE in the run directory, mapped by three processes, and
    //    that is a correction rather than a preference: a named pagefile-backed
    //    section is charged against the commit limit when it is created, and on
    //    the machine this was developed on `CreateFileMapping(INVALID_HANDLE_VALUE,
    //    ..., "Local\\name")` failed with ERROR_NOT_ENOUGH_MEMORY for a section of
    //    a few hundred kilobytes while gigabytes were free.  A file-backed mapping
    //    is a window onto a file that already exists and has no such charge
    //    (tests/MapProbe is the measurement).  It also means a run that crashed
    //    leaves its block readable in the run directory.
    //
    //    The path comes from the launcher through the environment, which is the
    //    only channel this DLL shares with anything before a runtime exists.
    char blockPathUtf8[MAX_PATH * 3] = {};
    wchar_t blockPath[MAX_PATH] = {};
    bool haveBlockPath = false;
    if (GetEnvironmentVariableA(DRELAY_ENV_BLOCK, blockPathUtf8, sizeof(blockPathUtf8)) != 0 &&
        MultiByteToWideChar(CP_ACP, 0, blockPathUtf8, -1, blockPath, MAX_PATH) != 0) {
        haveBlockPath = true;
        HANDLE file = CreateFileW(blockPath, GENERIC_READ | GENERIC_WRITE,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                                  FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file != INVALID_HANDLE_VALUE) {
            g_mapHandle = CreateFileMappingW(file, nullptr, PAGE_READWRITE, 0,
                                             DRELAY_SHARED_SIZE, nullptr);
            CloseHandle(file);
        }
    }
    OutputDebugStringA(haveBlockPath ? "drelay-hook: block path resolved\r\n"
                                     : "drelay-hook: DRELAY_HOOK_BLOCK missing or bad\r\n");

    if (g_mapHandle == nullptr) {
        // No shared block means no log destination either.  OutputDebugString is
        // the only channel left, and the environment is named in the message
        // because the usual cause is a launcher that did not set it.
        OutputDebugStringA("drelay-hook: FATAL could not open or map the shared block file "
                           "(DRELAY_HOOK_BLOCK)\r\n");
        return 1;
    }
    g_shared = static_cast<DrelayShared*>(MapViewOfFile(g_mapHandle, FILE_MAP_ALL_ACCESS, 0, 0, 0));
    if (g_shared == nullptr) {
        OutputDebugStringA("drelay-hook: MapViewOfFile failed\r\n");
        return 1;
    }

    // LOGGING IS INITIALISED HERE, IMMEDIATELY, BEFORE ANYTHING ELSE.
    //
    // It used to be initialised further down, after the layout check -- so a
    // failure anywhere in between produced no log at all, and the evidence for the
    // failure was precisely what was missing.  The first line is now written the
    // instant there is somewhere to write it, and flushed at once, because from
    // here on the thread may die at any instruction.
    DrLogInit(g_shared, g_runDir[0] != L'\0' ? g_runDir : nullptr);
    DrLog(DRLOG_INFO, "watcher thread started (pid %lu, session %u); block mapped at %p",
          GetCurrentProcessId(), g_sessionId, static_cast<void*>(g_shared));
    DrLogFlushNow();
    // WHO AND WHERE THIS PROCESS IS, RECORDED BEFORE ANYTHING CAN GO WRONG.
    // This is the line that makes "the operator was playing somewhere else" a
    // measured question rather than a hypothesis; see the note above the helper.
    DrLogProcessIdentity("at start");
    DrLogFlushNow();
    OutputDebugStringA("drelay-hook: block mapped and logger initialised\r\n");

    // The flusher thread is created HERE and bracketed on both sides, because it is
    // a thread creation from an injected DLL and the last several failures in this
    // hook have all been thread creation.  It used to sit between two checkpoints,
    // which meant a fault inside it was attributed to whatever came next.
    OutputDebugStringA("drelay-hook: about to start the flusher thread\r\n");
    DrLog(DRLOG_INFO, "checkpoint 1a: starting the log flusher thread");
    DrLogFlushNow();
    // A BISECT SWITCH FOR THE FLUSHER, ADDED AFTER THE WATCHER SURVIVED ONLY
    // WITH `DRELAY_SKIP_EVERYTHING`.  That control removes too much to say which
    // step is at fault, so each remaining step gets a switch of its own and they
    // are run one at a time.
    char skipFlusher[8] = {};
    if (GetEnvironmentVariableA("DRELAY_SKIP_FLUSHER", skipFlusher, sizeof(skipFlusher)) != 0 &&
        skipFlusher[0] == '1') {
        DrLog(DRLOG_WARN, "DRELAY_SKIP_FLUSHER=1: the periodic log flusher thread is not started "
                          "(diagnostic mode)");
        DrLogFlushNow();
    } else {
        DrLogStartFlusher();
    }
    OutputDebugStringA("drelay-hook: flusher thread started\r\n");
    DrLog(DRLOG_INFO, "checkpoint 1b: flusher thread started");
    DrLogFlushNow();

    g_shared->nativePid = GetCurrentProcessId();
    DrLog(DRLOG_INFO, "shared block: struct %llu bytes, version %u reported",
          static_cast<unsigned long long>(ExpectedSharedSize()), DRELAY_SHARED_VERSION);
    DrLogFlushNow();

    if (g_shared->version != DRELAY_SHARED_VERSION ||
        g_shared->structSize != static_cast<uint32_t>(ExpectedSharedSize())) {
        DrLog(DRLOG_ERR, "shared block layout mismatch: writer version %u size %u, "
                         "reader version %u size %llu -- refusing",
              g_shared->version, g_shared->structSize,
              DRELAY_SHARED_VERSION, static_cast<unsigned long long>(ExpectedSharedSize()));
        g_shared->nativeStatus = DRELAY_ST_REFUSED;
        g_shared->magicStatus = DRELAY_MAGIC_STATUS;
        SetEvent(g_nativeReadyEvent);
        DrLogFlushNow();
        return 1;
    }
    DrLog(DRLOG_INFO, "checkpoint 2: layout check passed");
    DrLogFlushNow();

    // 2. The off switch.  Honoured before anything is read, installed or
    //    patched -- "off" has to mean the process is untouched, not merely
    //    quiet.
    char off[16] = {};
    if (GetEnvironmentVariableA(DRELAY_ENV_OFF, off, sizeof(off)) != 0 && off[0] == '1') {
        DrLog(DRLOG_WARN, "DRELAY_HOOK_OFF=1: observing nothing and patching nothing");
        g_shared->nativeStatus = DRELAY_ST_OFF;
        g_shared->magicStatus = DRELAY_MAGIC_STATUS;
        g_shared->nativeTick = 1;
        SetEvent(g_nativeReadyEvent);
        DrLogFlushNow();
        return 0;
    }
    DrLog(DRLOG_INFO, "checkpoint 3: off switch checked (not off)");
    DrLogFlushNow();

    // 3. Wait for the bootstrap.  This is the ordering guarantee described at
    //    the top of the file.
    //
    //    The timeout is generous by default -- a cold start of a 50 MB
    //    self-contained assembly on a slow disk is not instant, and a false
    //    timeout would report "nothing is installed" for a hook that was merely
    //    early -- but it is overridable, because the timeout path is itself
    //    something that has to be testable.  A watchdog nobody has ever seen fire
    //    is a watchdog that does not work, and this one has to: it is what keeps a
    //    bootstrap that never runs from being a game that never starts.
    uint32_t timeoutMs = 60000;
    ReadEnvU32(DRELAY_ENV_TIMEOUT_MS, &timeoutMs);
    if (timeoutMs == 0) {
        timeoutMs = 60000;
    }
    DrLog(DRLOG_INFO, "checkpoint 4: timeout read as %u ms", timeoutMs);
    DrLogFlushNow();

    // A PROBE BEFORE THE REAL WAIT, BECAUSE THE TWO POSSIBILITIES ARE OPPOSITE.
    //
    // The fault is `WaitForSingleObject(g_bootReadyEvent, …)` and nothing else. That
    // leaves exactly two explanations, and they need different fixes:
    //
    //   (a) the handle is not a valid event, in which case *any* wait on it faults,
    //       including a zero-timeout one; or
    //   (b) the handle is fine and the call faults only when it blocks, which would
    //       be about this thread rather than about the object.
    //
    // A zero-timeout wait distinguishes them in one call and cannot block, so it
    // cannot hang the process while answering. `WAIT_OBJECT_0` here means the event
    // is *already* signalled (the bootstrap sets it before this thread reaches the
    // wait, because the bootstrap is released by something else); `WAIT_TIMEOUT`
    // means it is a valid, unsignalled event.
    const DWORD probe = WaitForSingleObject(g_bootReadyEvent, 0);
    DrLog(DRLOG_INFO, "checkpoint 5: zero-timeout probe on the boot event returned 0x%08X "
                      "(%s); handle 0x%llX",
          probe,
          probe == WAIT_OBJECT_0 ? "signalled" : (probe == WAIT_TIMEOUT ? "not signalled"
                                                                        : "unexpected"),
          static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(g_bootReadyEvent)));
    DrLogFlushNow();

    const DWORD installStart = GetTickCount();
    DrLog(DRLOG_INFO, "waiting for the managed bootstrap to signal readiness (timeout %u ms)",
          timeoutMs);
    DrLogFlushNow();

    // A POLL LOOP INSTEAD OF A BLOCKING WAIT, AND IT IS A WORKAROUND WITH A MEASURED
    // JUSTIFICATION.
    //
    // The blocking `WaitForSingleObject(g_bootReadyEvent, timeoutMs)` killed the
    // process, and the zero-timeout probe above proved the handle is valid -- it
    // returned `WAIT_TIMEOUT`, which an invalid handle cannot. So the same thread
    // could perform a non-blocking wait on that handle and could not perform a
    // blocking one. The call, not the object, was the fault.
    //
    // Polling removes the blocking form entirely: each iteration waits with a zero
    // timeout (proven to work) and then sleeps. `Sleep` is also a kernel wait, so if
    // this works the difference is specifically about waiting *on a synchronisation
    // object*, which is a narrower and more interesting finding than "blocking is
    // broken"; if it does not work, the blocking call was not the cause after all and
    // that is worth knowing too.
    //
    // The cost is latency and a wake every 5 ms for at most `timeoutMs`, which for a
    // startup handshake is nothing.
    DWORD wait = WAIT_TIMEOUT;
    DWORD pollStart = GetTickCount();
    for (;;) {
        wait = WaitForSingleObject(g_bootReadyEvent, 0);
        if (wait != WAIT_TIMEOUT) {
            break;
        }
        if (GetTickCount() - pollStart >= timeoutMs) {
            break;
        }
        Sleep(5);
    }
    DrLog(DRLOG_INFO, "poll loop finished after %lu ms with 0x%08X",
          GetTickCount() - pollStart, wait);
    DrLogFlushNow();

    if (wait != WAIT_OBJECT_0) {
        DrLog(DRLOG_ERR, "the bootstrap did not signal within %u ms (wait=%lu). Either "
                         "DOTNET_STARTUP_HOOKS was not honoured or the bootstrap threw. "
                         "Nothing is installed; the game runs normally.",
              timeoutMs, wait);
        g_shared->nativeStatus = DRELAY_ST_REFUSED;
        g_shared->magicStatus = DRELAY_MAGIC_STATUS;
        g_shared->selfCheckFailed = 1;
        SetEvent(g_nativeReadyEvent);
        DrLogFlushNow();
        return 1;
    }

    DrLog(DRLOG_INFO, "bootstrap signalled after %lu ms; config flags 0x%08X, %u target(s)",
          GetTickCount() - installStart, g_shared->cfgFlags, g_shared->targetCount);

    // 4. Validate the runtime and the assembly before writing anything.
    DrelayInstallReport report;
    // A BISECT SWITCH FOR VALIDATION, AND THE REASON IT EXISTS IS THAT THE
    // OBVIOUS CONTROLS DID NOT REACH THIS QUESTION.
    //
    // Once `DRELAY_SKIP_JIT_DETOUR` and `DRELAY_SKIP_RESOLVE_CALLBACK` were both
    // set -- no detour, nothing entering managed code -- the process still died
    // at shutdown with the same access violation.  So there is a third fault, it
    // is upstream of both of those, and the only control that had ever hidden it
    // was `DRELAY_SKIP_EVERYTHING`, which removes the whole watcher and therefore
    // cannot name a step.  This one removes exactly the runtime validation.
    char skipValidate[8] = {};
    const bool skipValidation =
        GetEnvironmentVariableA("DRELAY_SKIP_RUNTIME_VALIDATE", skipValidate,
                                sizeof(skipValidate)) != 0 &&
        skipValidate[0] == '1';

    if (skipValidation) {
        DrLog(DRLOG_WARN, "DRELAY_SKIP_RUNTIME_VALIDATE=1: runtime and assembly validation is "
                          "SKIPPED (diagnostic mode); nothing is proved about this build");
        DrLogFlushNow();
    } else if (!DrelayRuntimeValidate(&report)) {
        DrLog(DRLOG_ERR, "runtime/assembly validation REFUSED installation "
                         "(passed=%u failed=%u gaps=%u) -- the game runs normally",
              report.passed, report.failed, report.gaps);
        g_shared->nativeStatus = DRELAY_ST_REFUSED;
        g_shared->selfCheckPassed = report.passed;
        g_shared->selfCheckFailed = report.failed;
        g_shared->selfCheckGaps = report.gaps;
        g_shared->magicStatus = DRELAY_MAGIC_STATUS;
        SetEvent(g_nativeReadyEvent);
        DrelayWriteSelfCheckFile(report);
        // Flushed here specifically: this is the refusal path, which is the one a
        // wrong build takes, and its log is the only thing that explains the
        // refusal.  Waiting for the 250 ms flusher thread would mean the
        // explanation exists only if the process happens to survive that long.
        DrLogFlushNow();
        DrLogFlushNow();
        return 1;
    }

    DrelayLoadTargets();

    // THE CRASH REPORTER IS RE-ARMED HERE, ON THIS THREAD, AND THAT PLACE IS
    // DELIBERATE.  See DrelayArmCrashReporter: the runtime registers its own
    // vectored handler after this DLL is injected, so this one has to be moved
    // back to the front of the chain once the runtime exists.  This is the last
    // ordinary thread context before the detour can be entered and before the
    // managed callback runs, so it is where the arming belongs.
    DrelayArmCrashReporter();
    DrLog(DRLOG_INFO, "crash reporter armed: module list refreshed, handler moved to "
                      "the front of the vectored chain");
    DrLogFlushNow();

    // A DIAGNOSTIC THAT SEPARATES "MINHOOK IS LOADED" FROM "A DETOUR IS INSTALLED".
    //
    // Every test so far has changed what the detour *does* while MinHook has been
    // initialised throughout, so MinHook's own presence has never been excluded.
    // It matters because `MH_Initialize` is not inert: it allocates the trampoline
    // pool and installs its own machinery.  If the process dies with MinHook merely
    // initialised and no detour anywhere, then the fault is in that machinery and
    // not in hooking `compileMethod` at all -- a completely different problem with
    // a completely different answer.
    //
    //   DRELAY_HOOK_NO_MINHOOK=1   do not initialise MinHook and do not detour
    //   DRELAY_HOOK_NO_DETOUR=1    initialise MinHook, install no detour
    //   (neither)                  the normal path
    char noMinHook[8] = {};
    char noDetour[8] = {};
    const bool skipMinHook =
        GetEnvironmentVariableA("DRELAY_HOOK_NO_MINHOOK", noMinHook, sizeof(noMinHook)) != 0 &&
        noMinHook[0] == '1';
    const bool skipDetour =
        GetEnvironmentVariableA("DRELAY_HOOK_NO_DETOUR", noDetour, sizeof(noDetour)) != 0 &&
        noDetour[0] == '1';

    if (skipMinHook) {
        DrLog(DRLOG_WARN, "DRELAY_HOOK_NO_MINHOOK=1: MinHook is not initialised and no detour is "
                          "installed (diagnostic mode)");
        g_shared->magicStatus = DRELAY_MAGIC_STATUS;
        g_shared->nativeTick = 1;
        SetEvent(g_nativeReadyEvent);
        DrLogFlushNow();
        return 1;
    }

    if (skipDetour) {
        DrLog(DRLOG_WARN, "DRELAY_HOOK_NO_DETOUR=1: MinHook is initialised and no detour is "
                          "installed (diagnostic mode)");
        // Initialise it for real, so the only difference from the normal path is
        // the absence of a detour.
        DrelayDetoursInit();
        g_shared->magicStatus = DRELAY_MAGIC_STATUS;
        g_shared->nativeTick = 1;
        SetEvent(g_nativeReadyEvent);
        DrLogFlushNow();
        return 1;
    }

    // 5. Install the JIT detour.  From this instant the compilation stream is
    //    visible, which is what Spike 1.2 is about.
    if (!DrelayInstallJitDetour(&report)) {
        DrLog(DRLOG_ERR, "could not install the JIT detour -- nothing is patched");
        g_shared->nativeStatus = DRELAY_ST_REFUSED;
        g_shared->magicStatus = DRELAY_MAGIC_STATUS;
        SetEvent(g_nativeReadyEvent);
        DrLogFlushNow();
        return 1;
    }

    // A BISECT SWITCH FOR THE SELF-CHECKS, for the same reason as the one above.
    char skipSelfCheck[8] = {};
    if (GetEnvironmentVariableA("DRELAY_SKIP_SELFCHECKS", skipSelfCheck,
                                sizeof(skipSelfCheck)) != 0 &&
        skipSelfCheck[0] == '1') {
        DrLog(DRLOG_WARN, "DRELAY_SKIP_SELFCHECKS=1: the self-check pass is skipped "
                          "(diagnostic mode)");
        DrLogFlushNow();
    } else {
        DrelayRunSelfChecks(&report);
    }
    g_shared->selfCheckPassed = report.passed;
    g_shared->selfCheckFailed = report.failed;
    g_shared->selfCheckGaps = report.gaps;
    g_shared->methodDefSlotUsed = DRELAY_SLOT_NONE;
    g_shared->installMs = GetTickCount() - installStart;

    // Publish before the event, so the bootstrap's own self-check sees a
    // complete status rather than a half-written one.
    g_shared->magicStatus = DRELAY_MAGIC_STATUS;
    g_shared->nativeTick = 1;
    SetEvent(g_nativeReadyEvent);
    DrLog(DRLOG_INFO, "hook installed in %u ms; the bootstrap may proceed",
          g_shared->installMs);
    // Flushed before the bootstrap is released: if the host exits the instant
    // Main returns, a log that only exists when the process survives long enough
    // is not evidence.
    DrLogFlushNow();

    // The managed side's post-startup work, now that the runtime is up.  This is
    // where the field offsets are resolved, the Player MethodTable is published for
    // the native frame read, and the arming gate is opened --
    // everything the bootstrap could not do for itself.
    DrelayInvokeResolveCallback();

    // The identity check's second half, and it is placed here for a reason: the
    // runtime's own MVID only exists once the managed callback has published it,
    // and this is the last moment before the discovery window can close and patch
    // anything in the game.  A refusal here costs the hook and leaves the game
    // untouched, which is the trade this architecture always makes.
    const bool mvidVerified = DrelayVerifyLoadedAssemblyMvid(&report);

    // THE SELF-CHECK IS RUN AGAIN, AFTER THE VERIFICATION, AND THE ORDER IS THE POINT.
    //
    // `DrelayRunSelfChecks` above runs before the resolve callback, so at that moment
    // `g_shared->mvid` still holds zeros -- it is the bootstrap that fills it, from
    // inside the callback.  The first version therefore recorded:
    //
    //   PASS 5  assembly identity: MVID 00000000-... size 0
    //
    // in `selfcheck.txt`, while the same run's own log said
    // `loaded assembly MVID verified against the file: C022F7CB-...` and
    // `summary.txt` printed the real `CBF722C0-56DB-...`.  A PASS whose detail line is
    // zeros is worse than no line: it is a check that reports the answer as absent
    // while the evidence for it sits one file away.
    //
    // Running it here -- rather than before `DrelayVerifyLoadedAssemblyMvid` -- is also
    // what lets check 5a report the identity *comparison*: it reads `g_fileMvid`,
    // which is only populated during validation, and it must see the concluded state
    // or it would report a comparison that had not been made.
    //
    // Re-running is safe and is what makes the file self-consistent: it resets
    // `g_lineCount` and restates every check from the state as it is *now*, and every
    // check it makes is a pure read of the block or of a counter.  The report counters
    // are recomputed from the lines, so the counts still agree with the file -- which
    // is why `DrelayVerifyLoadedAssemblyMvid` must not adjust them itself.
    DrelayRunSelfChecks(&report);

    if (!mvidVerified) {
        DrLog(DRLOG_ERR, "the loaded assembly is not the file on disk -- refusing to patch "
                         "anything; the game runs normally");
        g_shared->nativeStatus = DRELAY_ST_REFUSED;
        g_shared->selfCheckPassed = report.passed;
        g_shared->selfCheckFailed = report.failed;
        g_shared->selfCheckGaps = report.gaps;
        g_shared->magicStatus = DRELAY_MAGIC_STATUS;
        DrelayWriteSelfCheckFile(report);
        DrLogFlushNow();
        return 1;
    }

    // 6. The operator's head start, then discovery.
    //
    //    THE DELAY COMES BEFORE THE OPEN, NOT INSTEAD OF IT.  Opening on the first
    //    compilation put the measurement at ~1.3 s into the process -- loading screen
    //    -- and two of the four targets compile on first use during gameplay, so the
    //    window could close before the operator had cleared the menu however long it
    //    was.  Three live runs reported `0 of 4 targets found` for that reason and
    //    nothing said so.  Nothing is lost during the delay: the detour is already
    //    installed and `PublishMatch` records a match whenever it is seen, so a method
    //    compiled in this interval is found and counted, not unwatched.
    const bool collectDelayElapsed = DrelayWaitForCollectDelay();

    //    The window closes either when all targets are in or after the window
    //    expires.  Nothing is patched until it closes, so no patch happens on a JIT
    //    thread.
    //
    //    THE FOREGROUND WINDOW IS SAMPLED ACROSS THIS LOOP, AND ONLY WHEN IT
    //    CHANGES.  "The operator is playing in another process" is either true or
    //    false at every moment of the window, and the process that owns the
    //    foreground window is the process receiving that operator's input.  A change
    //    to a pid that is not ours during a window in which the operator says they
    //    are playing is the measurement; a log that only states it once at startup
    //    cannot make it, because the interesting moment is after the menu.
    DWORD lastForegroundPid = 0;
    bool  foregroundSampled = false;
    for (;;) {
        // A shutdown during the collection delay leaves this loop immediately, so the
        // evidence writer below still runs and a Ctrl+C during the head start produces
        // a full set of files rather than an empty run directory.
        if (!collectDelayElapsed) {
            DrLog(DRLOG_WARN, "collection ended during the delay at the operator's request");
            break;
        }
        if (InterlockedCompareExchange(&g_shutdownRequested, 0, 0) != 0) {
            DrLog(DRLOG_WARN, "shutdown requested during discovery");
            DrelayCloseDiscovery();
            DrLogFlushNow();
            return 0;
        }
        const uint32_t found = g_targetsFound.load();
        if (g_shared->targetCount > 0 && found >= g_shared->targetCount) {
            DrLog(DRLOG_INFO, "all %u targets seen after %lu ms; closing discovery early",
                  g_shared->targetCount, GetTickCount() - installStart);
            break;
        }
        if (DrelayDiscoveryWindowClosed()) {
            DrLog(DRLOG_WARN, "discovery window expired with %u of %u targets found",
                  found, g_shared->targetCount);
            break;
        }
        // The watcher must not spin: a busy loop in a game process is a frame
        // hitch with no diagnostic, and this loop can run for half a minute.
        {
            wchar_t fgTitle[128] = {};
            const DWORD fgPid = ForegroundWindowPid(fgTitle, 128);
            if (!foregroundSampled || fgPid != lastForegroundPid) {
                foregroundSampled = true;
                lastForegroundPid = fgPid;
                DrLogProcessIdentity(fgPid == GetCurrentProcessId()
                                         ? "discovery: foreground is THIS process"
                                         : "discovery: foreground changed");
                DrLogFlushNow();
            }
        }
        // The arming probe, on the same 250 ms cadence as the discovery poll.  It
        // has to be inside this loop and not only inside the collection delay: the
        // delay is where an operator *usually* reaches gameplay, but "usually" is
        // not "always", and the discovery window is the next 30 s of the run.
        DrelayPollArmProbe();
        Sleep(DrelayDiscoveryPollMs());
    }

    g_shared->nativeStatus |= DRELAY_ST_DISCOVERY_DONE;
    g_shared->targetsFound = g_targetsFound.load();
    DrelayLogDiscoveryStatus();
    DrelayCloseDiscovery();

    // 7. Patch.  One thread, one pass, after startup -- see DrelayFrame.cpp.
    const uint32_t patched = DrelayPatchTargets();
    g_shared->targetsPatched = patched;

    // 8. Live checks, once the frame has been running long enough to have
    //    entered the detour.  Five seconds of frames is enough for a title
    //    screen and short enough that a wrong-address patch is reported now
    //    rather than at the end of a session.  It is a knob now
    //    (`--live-seconds`), because the endurance criterion asks for an hour of
    //    play and a five-second recording window cannot show one.
    const DWORD liveWindowMs = ReadLiveWindowMs();
    const DWORD liveStart = GetTickCount();
    uint32_t latePatchPolls = 0;
    while (GetTickCount() - liveStart < liveWindowMs) {
        if (InterlockedCompareExchange(&g_shutdownRequested, 0, 0) != 0) {
            break;
        }
        // Still probing, because the target has only just been patched and a run
        // whose world appears during this window is a run that still gets a live
        // read -- and the evidence written below is what has to show it.
        DrelayPollArmProbe();

        // AND STILL WATCHING FOR TARGETS THAT ARRIVE LATE, WHICH IS A FIX AND NOT A
        // REFINEMENT.  The discovery window is thirty seconds long and the patch pass
        // runs once, at its close, so a method the game first compiles at t+31 s is
        // matched, reported as found -- and left unhooked.  The 2026-10-12 21:22 session
        // is the receipt: the player reached `GameScene` at 21:23:10 and the window closed
        // at 21:23:16, so `Projectile.HitCharacter` missed the pass and the run published
        // `collisions: 0 decision(s)` with a healthy sensor around it, which reads as "no
        // collisions happened" when the truth is "nothing was watching for them".
        //
        // Once a second is enough: the cost is one walk of a four-entry table, and the
        // alternative is a session whose comparison silently starts at whatever moment
        // the player happened to enter the game.
        if (++latePatchPolls >= 4) {
            latePatchPolls = 0;
            DrelayPatchNewTargets();
        }
        Sleep(250);
    }
    // One last look, because a target matched in the final second is still worth hooking
    // for the evidence that follows.
    DrelayPatchNewTargets();

    // THE ROUND IS OVER, SO THE READ STOPS HERE.
    //
    // `g_framePaused` is the detour's first branch and nothing used to set it, so
    // the frame read ran for the whole life of the process: past the evidence,
    // through the idle loop and into teardown, where the world it walks is being
    // taken apart.  The read is the only part of the hook that touches the game's
    // heap, and it has nothing left to learn once the window closes.
    // `DRELAY_KEEP_READING=1` keeps the old behaviour for a run whose subject is
    // that teardown.
    if (ReadKeepReadingAfterRun()) {
        DrLog(DRLOG_INFO, "DRELAY_KEEP_READING=1: the frame read stays open after the live window");
    } else {
        g_framePaused.store(1);
        DrLog(DRLOG_INFO, "live window closed after %lu ms; the frame read is paused "
                          "(%llu frame(s) recorded). Set DRELAY_KEEP_READING=1 to keep it open",
              static_cast<unsigned long>(liveWindowMs),
              static_cast<unsigned long long>(g_frameCalls.load()));
    }
    DrLogFlushNow();

    DrelayInstallReport liveReport;
    DrelayRunLiveChecks(&liveReport);
    g_shared->selfCheckPassed = liveReport.passed;
    g_shared->selfCheckFailed = liveReport.failed;
    g_shared->selfCheckGaps = liveReport.gaps;
    DrelayWriteSelfCheckFile(liveReport);

    // WHICH TARGET SIZES WERE COMPILED AT ALL, ON THE CONSOLE AS WELL AS IN THE FILE.
    //
    // This is the one line that separates "the game never ran that code" from "the code
    // ran and its IL differs", and the two have completely different owners: the first
    // is an operator coverage question, the second is a `known-build.json` question.
    // Reading it out of `hook.log.sizes` afterwards works, but the operator is holding
    // the answer at the moment the run ends and should not have to be told where to
    // look -- three live runs were spent on exactly that gap.
    DrLogFlushObserved();
    DrLogTargetSizeSummary();

    // The evidence files.  Written here, after the live checks, so every counter
    // they report is final -- and from this thread, which is in nobody's frame,
    // rather than from the detour.
    DrelayWriteEvidence(g_runDir);
    DrLogFlushNow();

    DrLogProcessIdentity("at end");
    // TWO COUNTS, BECAUSE THEY ARE TWO DIFFERENT FACTS, AND THE LINE USED TO REPORT ONE
    // AS IF IT WERE THE OTHER.  `g_shared->targetsFound` is what the fixed thirty-second
    // discovery window matched; a target that the game compiles later -- `escape` is the
    // standing example, because nothing calls it at a title screen or in a safe zone --
    // is matched after that window closes and shows up in the target-size table a few
    // lines above this one as "compiled N time(s) AND MATCHED".  The summary said
    // "3 target(s) found" about a run whose own table said all four were found, and
    // PHASE2-SCOPE.md section 6.2 recorded the gap; this is that section's fix.
    uint32_t matchedByEnd = 0;
    for (uint32_t t = 0; t < g_shared->targetCount && t < DRELAY_MAX_TARGETS; ++t) {
        if (g_shared->targets[t].found != 0) {
            matchedByEnd++;
        }
    }
    DrLog(DRLOG_INFO, "phase 1 complete: %u of %u target(s) matched inside the discovery window, "
                      "%u matched by the end of the run, %u patched, %llu frame(s), "
                      "worst frame call %u us, self-check %u passed / %u failed / %u gap(s)",
          g_shared->targetsFound, g_shared->targetCount, matchedByEnd, patched,
          static_cast<unsigned long long>(g_frameCalls.load()),
          g_frameMaxUs.load(),
          liveReport.passed, liveReport.failed, liveReport.gaps);
    if (g_runDir[0] != L'\0') {
        wchar_t logPath[MAX_PATH] = {};
        _snwprintf_s(logPath, MAX_PATH, _TRUNCATE, L"%s\\hook.log", g_runDir);
        DrLogFlushTo(logPath);
    }

    // 9. Idle.  The hook's work is done; it stays resident only so a later phase
    //    has somewhere to publish and so shutdown can flush.  It must not
    //    consume CPU: the bridge is Phase 5 and until then there is nothing to
    //    wait for but shutdown.
    for (;;) {
        if (InterlockedCompareExchange(&g_shutdownRequested, 0, 0) != 0) {
            break;
        }
        // THE PROBE STILL RUNS HERE, AFTER THE EVIDENCE HAS BEEN WRITTEN, AND THAT
        // IS NOT AN OVERSIGHT.
        //
        // The evidence files are written a fixed interval after the patch, and an
        // operator who spends five minutes in a menu is in gameplay long after that.
        // A world that appears late still arms the callback -- the hook's whole job
        // in this phase is to prove the read works, and refusing to arm because a
        // file had already been written would be a monitoring artefact deciding a
        // technical question.  The transition is logged when it happens, so the run
        // has a dated record of it even though `summary.txt` was written earlier.
        DrelayPollArmProbe();
        Sleep(500);
    }

    DrLog(DRLOG_INFO, "watcher thread exiting");
    DrLogFlushNow();
    return 0;
}

} // namespace

// Initialisation, called by the injector after LoadLibraryW returns.  Reading
// the environment and creating the events lives here rather than in the watcher
// so that a failure to set up is reported to the launcher (a zero return) rather
// than only to a log file nobody is reading yet.
bool DrelaySetupFromEnvironment() {
    if (!ReadEnvU32(DRELAY_ENV_SESSION, &g_sessionId)) {
        return false;
    }
    ReadEnvW(DRELAY_ENV_RUNDIR, g_runDir, MAX_PATH);
    ReadEnvW(DRELAY_ENV_GAMEDIR, g_gameDir, MAX_PATH);
    // THE EXPLICIT ASSEMBLY WINS, AND WITHOUT THIS THE SYNTHETIC TEST NEVER
    // EXERCISED THE IDENTITY CHECK AT ALL.
    //
    // The launcher publishes `DRELAY_HOOK_ASSEMBLY` whenever `--assembly` is
    // given -- which the injection test always does -- and the native side then
    // ignored it, deriving `DarzasDominion.dll` from the game directory instead.
    // In the staged test tree that file does not exist, so the MVID read failed
    // and the check was quietly downgraded to a GAP.  The one configuration that
    // would have caught the real-game refusal was the one configuration that
    // never ran the check.
    if (!ReadEnvW(DRELAY_ENV_ASSEMBLY, g_assemblyPath, MAX_PATH) && g_gameDir[0] != L'\0') {
        _snwprintf_s(g_assemblyPath, MAX_PATH, _TRUNCATE, L"%s\\DarzasDominion.dll", g_gameDir);
    }

    wchar_t name[128] = {};
    _snwprintf_s(name, 128, _TRUNCATE, DRELAY_EVT_NATIVE_FMT, g_sessionId);
    g_nativeReadyEvent = CreateEventW(nullptr, TRUE, FALSE, name);
    _snwprintf_s(name, 128, _TRUNCATE, DRELAY_EVT_BOOT_FMT, g_sessionId);
    g_bootReadyEvent = CreateEventW(nullptr, TRUE, FALSE, name);
    return g_nativeReadyEvent != nullptr && g_bootReadyEvent != nullptr;
}

bool DrelayStartWatcher() {
    g_watcher = CreateThread(nullptr, 0, WatcherThread, nullptr, 0, nullptr);
    if (g_watcher == nullptr) {
        return false;
    }
    SetThreadDescription(g_watcher, L"drelay-hook-watcher");
    return true;
}

} // namespace drelay

extern "C" {

__declspec(dllexport) unsigned int __cdecl DrelayHook_AbiVersion(void) {
    return DRELAY_ABI_VERSION;
}

__declspec(dllexport) int __cdecl DrelayHook_Init(void) {
    using namespace drelay;
    // INSTALLED FIRST, AND THE ORDER IS THE POINT.
    //
    // This is called while the target process is still suspended by
    // CreateProcess(CREATE_SUSPENDED), so the reporter is in place before a
    // single managed instruction runs, before the watcher exists, and before
    // anything has had a chance to fault.  There is no debugger on this machine
    // (PHASE1-HANDOFF.md section 3 asks for one and section "how to build" has no
    // way to get one), so this is the debugger.
    DrelayInstallCrashReporter();
    if (!DrelaySetupFromEnvironment()) {
        OutputDebugStringA("drelay-hook: FATAL environment or handshake setup failed\r\n");
        return 0;
    }
    return DrelayStartWatcher() ? 1 : 0;
}

__declspec(dllexport) void __cdecl DrelayHook_Shutdown(void) {
    using namespace drelay;
    InterlockedExchange(&g_shutdownRequested, 1);
    if (g_watcher != nullptr) {
        WaitForSingleObject(g_watcher, 3000);
    }
    if (g_runDir[0] != L'\0') {
        wchar_t path[MAX_PATH] = {};
        _snwprintf_s(path, MAX_PATH, _TRUNCATE, L"%s\\hook.log", g_runDir);
        DrLogFlushTo(path);
    }
    DrLogStopFlusher();
    DrelayDetoursUninit();
}

// A TEST-ONLY EXPORT OF THE MVID READER, AND WHY IT IS WORTH THE SURFACE AREA.
//
// `DrPeReadMvid` reported "PE read failed at DrelayPe.cpp:181" against the game
// while the runtime read the same assembly's MVID without complaint, and the
// identity check was therefore reported as a GAP on every live run.  The
// injection test could not have caught it: the synthetic host it injects into is
// a PE32 image, and the defect was in the PE32+ branch.  A test that cannot reach
// the branch it is testing is not a test of that branch.
//
// Exporting the reader lets `tools/Test-PeReader.ps1` call it in-process against
// the game's own assembly and against the synthetic host, with no game running and
// no injection, and compare the answer against an independent reader.  It is a
// pure read of a file the caller names; it installs nothing, allocates nothing
// that outlives the call, and touches no global state.
__declspec(dllexport) int __cdecl DrelayHook_PeReadMvid(const wchar_t* path,
                                                        unsigned char* mvidOut) {
    using namespace drelay;
    return DrPeReadMvid(path, mvidOut) ? 1 : 0;
}

} // extern "C"

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        // The loader lock is held here.  Everything DllMain is allowed to do is
        // the line below, and nothing else belongs here -- the work is in
        // DrelayHook_Init, which the injector calls afterwards.
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
