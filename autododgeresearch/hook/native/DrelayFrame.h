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

// Patches any target that the game compiled *after* the discovery window closed.
//
// Whether a target lands inside that thirty-second window is a fact about how quickly the
// player reached the content that compiles it, not about the target:
// `Projectile.HitCharacter` is compiled the first time a projectile is fired at the
// player, so a session that spends longer in the menu than the window produces a run with
// no collision comparison at all unless this pass exists -- which is exactly what the
// 2026-10-12 21:22 session recorded, `collisions: 0 decision(s)`, with the target matched
// and reported as found but never hooked.
//
// Safe to call repeatedly: a target that is already hooked is skipped, and the record list
// the uninstall walks is left alone.
uint32_t DrelayPatchNewTargets();

// True once the frame-tick detour is live, which is what the self-check reports.
bool DrelayIsFrameTargetPatched();

bool DrelayDetoursInit();
void DrelayDetoursUninit();

} // namespace drelay

#endif // DRELAY_FRAME_H
