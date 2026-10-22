// DrelayCrash.cpp -- see DrelayCrash.h for why this exists rather than a
// debugger.

#include "DrelayCrash.h"
#include "DrelayGlobals.h"

#include <windows.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>

namespace drelay {
namespace {

// ---------------------------------------------------------------------------
// The module list.  Snapshotted from an ordinary thread context, then read from
// the handler; the handler itself calls nothing that could take a lock.

struct ModuleInfo {
    wchar_t   name[48];
    uintptr_t base;
    uintptr_t end;
};

constexpr uint32_t kMaxModules = 320;

ModuleInfo g_modules[kMaxModules] = {};
uint32_t   g_moduleCount = 0;
volatile LONG g_installed = 0;
volatile LONG g_inHandler = 0;
volatile LONG g_faults = 0;
// CET hijack trips seen and passed straight through.  Not faults: each one is the
// runtime suspending a managed thread for a collection.  See the comment above
// STATUS_RETURN_ADDRESS_HIJACK_ATTEMPT.
volatile LONG g_hijackTrips = 0;
PVOID         g_veh = nullptr;
// What `SetUnhandledExceptionFilter` replaced, so an explicit unload can put the
// process back the way it was.  See DrelayDetachCrashReporter.
LPTOP_LEVEL_EXCEPTION_FILTER g_previousFilter = nullptr;

// ---------------------------------------------------------------------------
// FATAL_JIT_EXCEPTION: THE EXCEPTION THE JIT RAISES WHEN IT GIVES UP ON A METHOD,
// AND THE ONE THIS REPORTER USED TO MISTAKE FOR A CRASH.
//
// `0x02345678` is not a Windows status code and it is not an accident.  It is the
// JIT's own channel for "I cannot compile this method", and it is raised in exactly
// one place -- `fatal(int errCode)` in `src/coreclr/jit/error.cpp`:
//
//     ULONG_PTR exceptArg = errCode;
//     RaiseException(FATAL_JIT_EXCEPTION, EXCEPTION_NONCONTINUABLE, 1, &exceptArg);
//
// and caught by the JIT's own filter in the same file:
//
//     LONG __JITfilter(PEXCEPTION_POINTERS p, LPVOID param) {
//         if (p->ExceptionRecord->ExceptionCode == FATAL_JIT_EXCEPTION) {
//             pParam->errc = (int)p->ExceptionRecord->ExceptionInformation[0];
//             jitInfo->reportFatalError((CorJitResult)pParam->errc);
//             return EXCEPTION_EXECUTE_HANDLER;
//         }
//         return EXCEPTION_CONTINUE_SEARCH;
//     }
//
// So it is a FIRST-CHANCE exception the runtime handles as a matter of routine --
// the EE retries the method with MinOpts -- and `param[0]` is a `CorJitResult`, not
// an address: `0x80000001` badCode, `0x80000003` noWay, `0x80000005`
// CORJIT_RECOVERABLEERROR (a `noway_assert`), `0x80000006` an implementation limit.
//
// WHY IT MATTERED HERE.  A run produced a `crash.txt` containing two records with
// `exception 0x02345678 unclassified` and `param[0] 0x80000005`, and it read as a
// crash in the JIT -- with our `compileMethod` detour one frame below, which made it
// look like the hook corrupting the compiler.  It is neither.  The process that
// produced one of those records went on to write its whole evidence set, and the
// same record appears in runs from before this hook had any of the code under
// suspicion.  Nothing about it is a fault.
//
// THE REAL COST.  A `crash.txt` that says "crash" for a handled JIT retry is the
// same defect as `frames.csv` calling a millisecond total a per-frame second: an
// instrument that is wrong about what it is measuring, and that a reader has no way
// to detect.  It cost an hour of looking for a crash that had not happened, while
// the run's ACTUAL death -- `COR_E_EXECUTIONENGINE` (0x80131506) from coreclr, which
// terminates the process through `TerminateProcess` and therefore raises nothing
// this handler can see -- left no report at all.
//
// It is still recorded, because "the JIT gave up on a method" is real evidence about
// a process with a JIT hook in it.  It goes to `jit-internal.txt`, named for what it
// is.
#define FATAL_JIT_EXCEPTION 0x02345678u

// ---------------------------------------------------------------------------
// STATUS_RETURN_ADDRESS_HIJACK_ATTEMPT: THE EXCEPTION THIS REPORTER MUST NEVER
// WRITE A REPORT FOR, AND THE ONE IT USED TO KILL THE PROCESS WITH.
//
// The game's runtime is CET (shadow-stack) enabled -- .NET 9 turns it on by
// default, and `coreclr!Thread::AreShadowStacksEnabled` is true in this process.
// Under it the runtime does not suspend a running managed thread by overwriting
// its instruction pointer.  It swaps the *return address* for a target the OS
// supplies (`RtlGetReturnAddressHijackTarget`), and when that frame returns the
// CPU's shadow-stack check fails and Windows raises `0x80000033`
// (`vm/excep.cpp:6514`).  The runtime's own handler then moves `rip` to the
// original return site and lets the thread reach a GC safe point.  In a healthy
// process this happens on ordinary collections and is completely routine.
//
// WHAT WENT WRONG.  This handler ran first in the vectored chain, did not know
// the code, and wrote a report -- which begins with `OutputDebugStringA`.  That
// call raises `0x40010006` of its own.  The nested exception is dispatched from
// the top of the chain again, so it reaches the runtime's handler *while the
// outer `0x80000033` is still in flight*, and that handler unhijacks the current
// thread for every exception it sees (`vm/excep.cpp:6561-6566` ->
// `Thread::UnhijackThreadNoAlloc`, which restores the return address and clears
// `TS_Hijacked`, `vm/threadsuspend.cpp:4659`).  When the outer exception resumed
// dispatch, the runtime found a hijack trip on a thread it no longer believed was
// hijacked:
//
//     _ASSERTE_ALL_BUILDS(pThread->HasThreadState(Thread::TS_Hijacked));   // excep.cpp:6524
//
// which fail-fasts the process through `RaiseFailFastException`, i.e.
// `0xC0000602` with `coreclr+0x321531` -- an exception that raises nothing this
// handler can see, so it left no report of its own.
//
// THE MEASUREMENT.  Across the runs in `%LOCALAPPDATA%\drelay\hook`, 60 wrote a
// `0x80000033` report and 55 of them died within a second or two of writing it;
// the other five kept running for another 16 to 249 seconds.  The runtime returns
// before the assert on a trip it declines as "not ours" (the thread is not in
// cooperative mode), which is the most likely difference between the two groups --
// this comment does not claim it is the only one.  The four dumps in `work\dumps`
// are the same fail-fast, and their fail-fast thread's stack points at
// `coreclr+0x137343` -- the instruction after the call that loads
// `excep.cpp:6524`.  Autofarming makes it quick only because it collects far more
// often: every collection is another trip.
//
// SO: PASS IT THROUGH, COUNT IT, NEVER REPORT IT.  The counter is the evidence;
// `DrelayHijackTripCount` is read from an ordinary thread by the watcher.
#ifndef STATUS_RETURN_ADDRESS_HIJACK_ATTEMPT
#define STATUS_RETURN_ADDRESS_HIJACK_ATTEMPT 0x80000033u
#endif

// ---------------------------------------------------------------------------
// The report is built in one static buffer, because allocating inside an
// exception handler is exactly the thing that turns a diagnosed fault into an
// undiagnosed one.

char   g_text[16384];
size_t g_len = 0;

void App(const char* text) {
    const size_t n = strlen(text);
    if (g_len + n + 1 >= sizeof(g_text)) {
        return;
    }
    memcpy(g_text + g_len, text, n);
    g_len += n;
    g_text[g_len] = '\0';
}

void AppF(const char* fmt, ...) {
    char line[512] = {};
    va_list args;
    va_start(args, fmt);
    _vsnprintf_s(line, sizeof(line), _TRUNCATE, fmt, args);
    va_end(args);
    App(line);
}

// ---------------------------------------------------------------------------

void SnapshotModules() {
    const uint32_t previous = g_moduleCount;
    uint32_t count = 0;

    HANDLE snapshot = CreateToolhelp32Snapshot(
        TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    if (snapshot != INVALID_HANDLE_VALUE) {
        MODULEENTRY32W entry = {};
        entry.dwSize = sizeof(entry);
        if (Module32FirstW(snapshot, &entry)) {
            do {
                if (count >= kMaxModules) {
                    break;
                }
                ModuleInfo& module = g_modules[count];
                // The name is truncated, not rejected: a diagnostic that fails
                // because a path was long would be a diagnostic that fails
                // exactly when it is needed.
                wcsncpy_s(module.name, entry.szModule, _TRUNCATE);
                module.base = reinterpret_cast<uintptr_t>(entry.modBaseAddr);
                module.end = module.base + entry.modBaseSize;
                ++count;
            } while (Module32NextW(snapshot, &entry));
        }
        CloseHandle(snapshot);
    }

    g_moduleCount = (count > 0) ? count : previous;
}

// `out` is written for every address, including one that belongs to no module,
// because "which module" being unknown is itself the finding.
void Describe(const void* address, char* out, size_t cap) {
    const uintptr_t value = reinterpret_cast<uintptr_t>(address);
    for (uint32_t i = 0; i < g_moduleCount; ++i) {
        if (value >= g_modules[i].base && value < g_modules[i].end) {
            _snprintf_s(out, cap, _TRUNCATE, "%ls+0x%llX", g_modules[i].name,
                        static_cast<unsigned long long>(value - g_modules[i].base));
            return;
        }
    }

    // WHERE THE LOADER-LOCK FALLBACK USED TO BE.  It asked the loader
    // (`GetModuleHandleExW`) for a module loaded after the last snapshot, which was
    // worth a name for a lazily loaded DLL and cost the loader lock inside an
    // exception handler.  `DrelayArmCrashReporter` re-snapshots at each checkpoint
    // precisely so this handler never has to, and the failures that matter here --
    // a JIT code-heap address such as the `ret` a hijack trip happens on -- belong
    // to no module at all, so the fallback bought nothing for them while making
    // "the handler takes no lock" untrue.  "(no module)" is the finding.
    _snprintf_s(out, cap, _TRUNCATE, "(no module)");
}

const char* ExceptionName(DWORD code) {
    switch (code) {
        case EXCEPTION_ACCESS_VIOLATION:      return "ACCESS_VIOLATION";
        case EXCEPTION_IN_PAGE_ERROR:         return "IN_PAGE_ERROR";
        case EXCEPTION_ILLEGAL_INSTRUCTION:   return "ILLEGAL_INSTRUCTION";
        case EXCEPTION_PRIV_INSTRUCTION:      return "PRIV_INSTRUCTION";
        case EXCEPTION_STACK_OVERFLOW:        return "STACK_OVERFLOW";
        case EXCEPTION_INT_DIVIDE_BY_ZERO:    return "INT_DIVIDE_BY_ZERO";
        case EXCEPTION_DATATYPE_MISALIGNMENT: return "DATATYPE_MISALIGNMENT";
        case EXCEPTION_ARRAY_BOUNDS_EXCEEDED: return "ARRAY_BOUNDS_EXCEEDED";
        case 0x80131506u:                     return "COR_E_EXECUTIONENGINE (the CLR fatal error)";
        case 0xC0000409u:                     return "STACK_BUFFER_OVERRUN / fail-fast";
        case 0xE0434352u:                     return "CLR managed exception";
        case STATUS_RETURN_ADDRESS_HIJACK_ATTEMPT:
                                              // Unreachable through the vectored
                                              // handler, which now passes it through
                                              // before building a report.  Named here
                                              // anyway, because a report that says
                                              // "unclassified" about the runtime's own
                                              // hijack trip is what made the old runs
                                              // look like a crash.
                                              return "RETURN_ADDRESS_HIJACK_TRIP (the CLR's CET hijack "
                                                     "trip: a GC suspending a managed thread, not a fault)";
        case FATAL_JIT_EXCEPTION:             return "FATAL_JIT_EXCEPTION (the JIT's own fatal path, "
                                                     "which the JIT catches itself)";
        default:                              return "unclassified";
    }
}

// A plain walk of the faulting thread's return addresses.  This is the frame
// list a debugger would print, and it is short for the same reason a debugger's
// is: the top frame is often a JIT-compiled or trampoline body with no unwind
// data, and the walk stops there.
void AppendStackWalk(const CONTEXT* context) {
    App("  return addresses (RtlCaptureStackBackTrace):\r\n");

    void* frames[62] = {};
    const USHORT captured = RtlCaptureStackBackTrace(0, 62, frames, nullptr);
    for (USHORT i = 0; i < captured; ++i) {
        char described[256] = {};
        Describe(frames[i], described, sizeof(described));
        AppF("    [%02u] %p  %s\r\n", static_cast<unsigned>(i), frames[i], described);
    }
    if (captured == 0) {
        App("    (the walk captured nothing -- no unwind data above the fault)\r\n");
    }

    // A RAW SCAN, BECAUSE THE WALK ABOVE IS EXPECTED TO FAIL HERE.
    //
    // `RtlCaptureStackBackTrace` needs unwind information for every frame it
    // steps over.  A MinHook trampoline, a hand-written thunk and the
    // `[UnmanagedCallersOnly]` transition stub all lack it, so the interesting
    // fault -- one reached through the hook -- produces an empty walk.  Scanning
    // the stack for values that land inside a known module needs no unwind data
    // and no cooperation from the code above the fault.
    App("  raw stack scan (module-shaped values, may include stale frames):\r\n");
    const uintptr_t* stack = reinterpret_cast<const uintptr_t*>(context->Rsp);
    uint32_t shown = 0;
    for (uint32_t i = 0; i < 768 && shown < 60; ++i) {
        uintptr_t value = 0;
        bool unreadable = false;
        __try {
            value = stack[i];
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            unreadable = true;
        }
        if (unreadable) {
            App("    (the stack itself became unreadable here)\r\n");
            break;
        }
        for (uint32_t m = 0; m < g_moduleCount; ++m) {
            if (value >= g_modules[m].base && value < g_modules[m].end) {
                char described[256] = {};
                Describe(reinterpret_cast<const void*>(value), described, sizeof(described));
                AppF("    [rsp+0x%03X] %p  %s\r\n", i * 8,
                     reinterpret_cast<const void*>(value), described);
                ++shown;
                break;
            }
        }
    }
    if (shown == 0) {
        App("    (nothing module-shaped within 768 slots of rsp)\r\n");
    }
}

// `secondChance` means the process is already dying (the last-chance filter ran),
// and it is the *only* case in which the debugger channel may be used.
//
// WHY THE MOVED LINE MATTERS, AND WHY IT IS NOT A STYLE CHOICE.  `OutputDebugStringA`
// raises `0x40010006` -- a real first-chance exception, raised whether or not a
// debugger is attached (measured: a probe process with nothing but a vectored
// handler installed saw exactly that code, and saw nothing from plain file I/O).
// Raising it from inside another exception's dispatch
// re-enters the handler chain, and the runtime's handler unhijacks the current
// thread for every exception it sees.  On a thread the runtime has just hijacked
// for a collection, that turns the hijack trip into the `excep.cpp:6524` assert --
// which is the whole story in the comment above
// STATUS_RETURN_ADDRESS_HIJACK_ATTEMPT.  A live process must not be handed a
// nested exception by its own diagnostic; a dying one can afford it, and that is
// where the debugger channel still goes.
void WriteReport(const wchar_t* fileName, bool secondChance) {
    if (secondChance) {
        OutputDebugStringA(g_text);
    }

    if (g_runDir[0] == L'\0') {
        return;
    }
    wchar_t path[MAX_PATH] = {};
    _snwprintf_s(path, MAX_PATH, _TRUNCATE, L"%s\\%s", g_runDir, fileName);
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return;
    }
    DWORD written = 0;
    WriteFile(file, g_text, static_cast<DWORD>(g_len), &written, nullptr);
    FlushFileBuffers(file);
    CloseHandle(file);
}

LONG ReportException(EXCEPTION_POINTERS* pointers, bool secondChance) {
    if (pointers == nullptr || pointers->ExceptionRecord == nullptr) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    const EXCEPTION_RECORD* record = pointers->ExceptionRecord;
    const DWORD code = record->ExceptionCode;

    // First-chance noise that is not a fault.  A vectored handler sees every
    // exception in the process, including the ones the runtime raises and
    // handles routinely, and treating one of those as the crash would produce a
    // confident wrong answer -- the exact failure mode PHASE1-HANDOFF.md
    // section 8 is about.
    switch (code) {
        case 0x406D1388u:            // SetThreadName, raised by every named thread
        case 0xE06D7363u:            // a C++ exception being thrown
        case 0x40010006u:            // OutputDebugString
        case 0x4001000Au:            // Debugger print
        case 0x4000001Fu:            // WOW64 breakpoint
        case EXCEPTION_BREAKPOINT:
        case EXCEPTION_SINGLE_STEP:
        case 0xE0434352u:            // a managed exception being thrown normally
        // 0x80000033 IS THE RUNTIME'S OWN RETURN-ADDRESS HIJACK SIGNAL, AND IT IS THE ONE
        // EXCEPTION THIS HANDLER MUST NOT TOUCH.  It is raised on a *suspended-for-GC*
        // thread when the hijacked return executes; the runtime's own vectored handler
        // then resumes it at a safe point. Recording it used to mean a stack walk and up to
        // four file writes inside that window, on the very thread the GC is waiting for -
        // which is exactly the kind of delay a thread-suspension race does not tolerate.
        // The report was also misleading (TROUBLESHOOTING.md section 2: it is not the
        // death), so the only thing that recording it ever bought was a wider window for
        // the real `TS_Hijacked` assertion.  Hand it straight back.
        case 0x80000033u:
            return EXCEPTION_CONTINUE_SEARCH;
        default:
            break;
    }

    // AND THE RUNTIME'S OWN HIJACK TRIP, WHICH IS NOT NOISE AND IS NOT A FAULT.
    // Passed through before any bookkeeping -- before the re-entrancy guard and
    // before the `g_faults` budget, so a run that trips often never spends the
    // budget that a real fault would need.  See the comment on the constant.
    if (code == STATUS_RETURN_ADDRESS_HIJACK_ATTEMPT) {
        InterlockedIncrement(&g_hijackTrips);
        return EXCEPTION_CONTINUE_SEARCH;
    }

    if (InterlockedCompareExchange(&g_inHandler, 1, 0) != 0) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    if (InterlockedIncrement(&g_faults) > 8) {
        InterlockedExchange(&g_inHandler, 0);
        return EXCEPTION_CONTINUE_SEARCH;
    }

    g_len = 0;
    g_text[0] = '\0';

    char described[256] = {};
    const bool jitInternal = (code == FATAL_JIT_EXCEPTION);
    Describe(record->ExceptionAddress, described, sizeof(described));
    App(jitInternal
            ? "\r\n=== drelay: the JIT gave up on a method (NOT a crash) ===\r\n"
            : "\r\n=== drelay crash report ===\r\n");
    AppF("  exception   0x%08X  %s\r\n", code, ExceptionName(code));
    if (jitInternal) {
        // Said before the stack, because the stack is the misleading part: it shows
        // clrjit and our own detour, which reads as "the hook broke the compiler".
        App("  what        the JIT raised its own fatal-exception channel and caught it\n"
            "              itself; the runtime retries the method. This is recorded as\n"
            "              evidence about the JIT, not as a process death. A real death\n"
            "              (COR_E_EXECUTIONENGINE, 0x80131506) terminates through\n"
            "              TerminateProcess and raises nothing this handler can see.\n");
    }
    AppF("  thread      %lu\r\n", GetCurrentThreadId());
    AppF("  at          %p  %s\r\n", record->ExceptionAddress, described);
    AppF("  parameters  %u\r\n", record->NumberParameters);

    if (code == EXCEPTION_ACCESS_VIOLATION && record->NumberParameters >= 2) {
        const ULONG_PTR operation = record->ExceptionInformation[0];
        const ULONG_PTR target = record->ExceptionInformation[1];
        const char* what = (operation == 0) ? "read" : (operation == 1) ? "write" : "execute";
        char targetDescribed[256] = {};
        Describe(reinterpret_cast<const void*>(target), targetDescribed, sizeof(targetDescribed));
        AppF("  access      %s of %p  %s\r\n", what,
             reinterpret_cast<const void*>(target), targetDescribed);
    } else {
        for (DWORD i = 0; i < record->NumberParameters && i < 4; ++i) {
            AppF("  param[%u]    0x%llX\r\n", i,
                 static_cast<unsigned long long>(record->ExceptionInformation[i]));
        }
    }

    if (pointers->ContextRecord != nullptr) {
        const CONTEXT* context = pointers->ContextRecord;
        AppF("  rip %p  rsp %p  rbp %p\r\n",
             reinterpret_cast<const void*>(context->Rip),
             reinterpret_cast<const void*>(context->Rsp),
             reinterpret_cast<const void*>(context->Rbp));
        AppendStackWalk(context);
    }

    AppF("  modules snapshotted: %u\r\n", g_moduleCount);
    App(jitInternal ? "=== end: the JIT's own retry, not a crash ===\r\n"
                    : "=== end crash report ===\r\n");

    // Last, because it is the only step that can fail without losing the report.
    WriteReport(jitInternal ? L"jit-internal.txt" : L"crash.txt", secondChance);

    InterlockedExchange(&g_inHandler, 0);
    return EXCEPTION_CONTINUE_SEARCH;
}

// The vectored entry point.  A plain one-argument function because that is the
// type `AddVectoredExceptionHandler` takes; the shared body above carries the
// `secondChance` distinction instead of a defaulted parameter, which would change
// this function's type and quietly stop it converting to PVECTORED_EXCEPTION_HANDLER.
LONG CALLBACK VehHandler(EXCEPTION_POINTERS* pointers) {
    return ReportException(pointers, /*secondChance=*/false);
}

LONG WINAPI UnhandledFilter(EXCEPTION_POINTERS* pointers) {
    // A vectored handler runs before this one, so reaching here means the fault
    // is one the vectored handler filtered as benign, or the filter itself
    // failed.  Reported anyway rather than returning EXCEPTION_CONTINUE_SEARCH
    // silently, because silence here is indistinguishable from "no crash".  This
    // is second chance: the process is going down, so the debugger channel is
    // safe here and only here.
    ReportException(pointers, /*secondChance=*/true);
    return EXCEPTION_CONTINUE_SEARCH;
}

} // namespace

void DrelayInstallCrashReporter() {
    if (InterlockedCompareExchange(&g_installed, 1, 0) != 0) {
        return;
    }
    SnapshotModules();
    g_veh = AddVectoredExceptionHandler(1, VehHandler);
    g_previousFilter = SetUnhandledExceptionFilter(UnhandledFilter);
    (void)g_veh;
}

// THE HANDLERS POINT INTO THIS IMAGE, SO THEY MUST GO BEFORE IT IS UNMAPPED.
//
// In the game the DLL is never unloaded, so this only matters to a host that calls
// `FreeLibrary` -- and `Test-HookAbi.ps1` is exactly that host: it loads the DLL,
// calls `DrelayHook_Init` (which arms both handlers) and frees it.  ntdll keeps the
// handler addresses regardless, so the next exception in that process dispatches
// into unmapped memory.  The measured symptom was the whole offline suite dying with
// `0xC0000409` the instant the ABI self-test finished, and it reproduced against the
// DLL built from the sources before the CET fix, so it is not a regression: it is an
// unload that was never safe.  `DllMain(DLL_PROCESS_DETACH)` with `lpReserved == NULL`
// is the one place that runs on an explicit unload, and it is where this is called.
//
// The one thing this cannot do is synchronise with an exception that is already being
// dispatched on another thread; `RemoveVectoredExceptionHandler` does not wait for it.
// That is a narrower race than a guaranteed dangling pointer, and it does not arise in
// the game, which never unloads.
void DrelayDetachCrashReporter() {
    if (g_veh != nullptr) {
        RemoveVectoredExceptionHandler(g_veh);
        g_veh = nullptr;
    }
    // Restoring the previous filter rather than clearing it keeps the promise the
    // header makes: this observes, and when it leaves it leaves nothing behind.
    SetUnhandledExceptionFilter(g_previousFilter);
    g_previousFilter = nullptr;
    InterlockedExchange(&g_installed, 0);
}

void DrelayArmCrashReporter() {
    SnapshotModules();
    if (g_veh != nullptr) {
        RemoveVectoredExceptionHandler(g_veh);
    }
    g_veh = AddVectoredExceptionHandler(1, VehHandler);
}

unsigned int DrelayHijackTripCount() {
    // Read from an ordinary thread (the watcher), never from the handler, which
    // only ever increments it.  The compare-exchange is a portable atomic read:
    // `InterlockedCompareExchange` with the same value both ways leaves the
    // counter untouched and returns what it was.
    return static_cast<unsigned int>(InterlockedCompareExchange(&g_hijackTrips, 0, 0));
}

} // namespace drelay
