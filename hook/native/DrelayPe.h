// DrelayPe.h -- the PE/CLI reads the hook does for itself.
//
// Two things need file-format knowledge that no Win32 API exposes:
//
//   1. the MVID of the game assembly *as loaded*, which is the second of the two
//      gaps Phase 0 could not close from a script (VERIFICATION.md section 2).
//      The gate hashes the file on disk; this reads the module table the runtime
//      would read, so "the file is the build we built for" and "the process is
//      running the build we built for" become two statements.
//
//   2. whether a pointer lies inside a named section of a module, which is how
//      the JIT vtable is validated (HOOK-ARCHITECTURE.md section 4.1 step 5a).
//      "The first four slots all point into clrjit's .text" is a much stronger
//      statement than "the vtable has four entries".
//
// Both are read-only and neither allocates: the assembly is mapped, read, and
// unmapped.  No managed reader is used because at this point in startup the
// native side is the only side that exists.

#ifndef DRELAY_PE_H
#define DRELAY_PE_H

#include <windows.h>
#include <stdint.h>

// Reads the module table of a managed PE and copies its MVID out.
// `mvidOut` must point at 16 bytes.  Returns true only on a complete, coherent
// read; every partial condition returns false and the caller treats that as a
// refusal rather than as a pass.
bool DrPeReadMvid(const wchar_t* path, uint8_t* mvidOut);

// Reads the file's size and its SHA-256 in one pass.  Used to compare the
// loaded assembly against the fingerprint gate's expectation without relying on
// the gate having run.
bool DrPeHashFile(const wchar_t* path, uint8_t* sha256Out, int64_t* sizeOut);

// True when `address` lies inside the section named `sectionName` of the module
// that contains it.  Resolves the containing module itself, so a pointer from an
// unexpected module is reported as false rather than as "in some .text".
bool DrPeAddressInSection(const void* address, const char* sectionName);

// The image's preferred base, and the module-relative name of the module that
// contains `address`.  Both are diagnostics for hook.log.
uint32_t DrPeImageBaseOf(const void* address);
bool     DrPeModuleNameOf(const void* address, wchar_t* out, size_t capChars);

// Metadata token of the assembly's entry point, from the CLI header.  Recorded
// because it is a second, independent number that a game patch would move.
uint32_t DrPeEntryPointToken(const wchar_t* path);

#endif // DRELAY_PE_H
