// DrelayFrame.h -- target patching and the live frame sample.

#ifndef DRELAY_FRAME_H
#define DRELAY_FRAME_H

#include <stdint.h>

namespace drelay {

// Creates and enables a detour for every *found* target whose signature the hook
// can forward correctly.
//
// Not every found target is patched, and that is deliberate rather than
// incomplete: a detour body must match the target's calling convention and
// parameter list exactly, and three of Phase 1's four targets (the 16-argument
// spawn, the bool-returning collision, the argument-less escape) have no Phase 1
// behaviour to run.  Installing a forwarder with the wrong shape for a method
// that is called every frame would be a way to corrupt the game in exchange for
// nothing.  The patches that *are* needed get applied; the rest are recorded as
// found-and-verified and reported as deferred by name, so "three of four detours
// never fired" can never be mistaken for silence.
uint32_t DrelayPatchTargets();

// True once the frame-tick detour is live, which is what the self-check reports.
bool DrelayIsFrameTargetPatched();

bool DrelayDetoursInit();
void DrelayDetoursUninit();

} // namespace drelay

#endif // DRELAY_FRAME_H
