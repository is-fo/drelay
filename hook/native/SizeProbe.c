// SizeProbe.c -- prints the sizes and member offsets of the shared block exactly
// as the compiler that builds the hook sees them.
//
// WHY THIS IS COMPILED AND RUN RATHER THAN COMPUTED
// -------------------------------------------------
// The layout is agreed between MSVC and the C# compiler, and stating it twice has
// been wrong twice: a hand-written `[FieldOffset]` set was wrong in three places at
// once, and the ring bases were nowhere near where the arithmetic suggested.
//
// NO SPECIFIC SIZE IS QUOTED HERE, DELIBERATELY.  This comment used to name
// `DrelayFieldOffset` as 88 and `DrelayTargetHash` as 112; both were true of a
// struct revision that no longer exists, and prose that repeats a measured number
// goes stale silently while the probe beside it keeps printing the right one.  The
// authority is this probe's own output on every build -- `build.ps1` prints it as
// `compiler-reported layout` and diffs it against the generator's prediction.
//
// `tools/gen_shared_layout.py` models the C packing rules and generates the
// managed side, and its model can still be wrong.  So it is checked against the
// real compiler: this probe prints the numbers, `build.ps1` diffs them against
// what the generator predicted, and the native header's `static_assert` pins the
// total.  A disagreement is a build failure naming the field.

#include "DrelayHookShared.h"

#include <stdio.h>
#include <stddef.h>

int main(void) {
    printf("DrelayTargetHash %zu\n", sizeof(DrelayTargetHash));
    printf("DrelayFieldOffset %zu\n", sizeof(DrelayFieldOffset));
    printf("DrelayReadStep %zu\n", sizeof(DrelayReadStep));
    printf("DrelayReadSpec %zu\n", sizeof(DrelayReadSpec));
    printf("DrelayLogLine %zu\n", sizeof(DrelayLogLine));
    printf("DrelayTraceRow %zu\n", sizeof(DrelayTraceRow));
    printf("DrelayFrameRow %zu\n", sizeof(DrelayFrameRow));
    printf("DrelayShared %zu\n", sizeof(DrelayShared));
    printf("DRELAY_SHARED_SIZE %u\n", (unsigned)DRELAY_SHARED_SIZE);

    /* The fields either side reads, by offset, so the generator's model can be
       diffed field by field rather than only in total. */
    printf("off.Targets %zu\n", offsetof(DrelayShared, targets));
    printf("off.Mvid %zu\n", offsetof(DrelayShared, mvid));
    printf("off.Steps %zu\n", offsetof(DrelayShared, steps));
    printf("off.Reads %zu\n", offsetof(DrelayShared, reads));
    printf("off.Offsets %zu\n", offsetof(DrelayShared, offsets));
    printf("off.OffsetCount %zu\n", offsetof(DrelayShared, offsetCount));
    printf("off.ManagedPlayerTypeHandle %zu\n", offsetof(DrelayShared, managedPlayerTypeHandle));
    printf("off.ManagedReadValue %zu\n", offsetof(DrelayShared, managedReadValue));
    printf("off.ManagedProbeHook %zu\n", offsetof(DrelayShared, managedProbeHook));
    printf("off.ManagedArmState %zu\n", offsetof(DrelayShared, managedArmState));
    printf("off.MagicStatus %zu\n", offsetof(DrelayShared, magicStatus));
    printf("off.NativeStatus %zu\n", offsetof(DrelayShared, nativeStatus));
    printf("off.JitVtableInText %zu\n", offsetof(DrelayShared, jitVtableInText));
    printf("off.Log %zu\n", offsetof(DrelayShared, log));
    printf("off.Trace %zu\n", offsetof(DrelayShared, trace));
    printf("off.Frames %zu\n", offsetof(DrelayShared, frames));
    printf("off.Reserved %zu\n", offsetof(DrelayShared, reserved));
    return 0;
}
