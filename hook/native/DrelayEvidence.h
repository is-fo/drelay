// DrelayEvidence.h -- draining the shared block to files at the end of a run.
//
// WHY THE EVIDENCE IS IN RINGS AND NOT IN FILES
// --------------------------------------------
// The native side writes from three places that must not block and must not
// allocate: the JIT callback, the frame-tick detour, and the watcher.  A file
// handle in any of them is a way to make the game stutter.  So the events go into
// fixed-size rings inside the shared block, and this module drains them once, at
// the end, from the watcher thread -- which is not in anyone's frame.
//
// The drain is also the place where "the ring overflowed" has to be said out
// loud.  A ring loses its oldest rows when it wraps, and a reader who is not told
// will read a partial trace as a complete one.  Every file this module writes
// carries its own overflow count, and a non-zero one is written as a first line
// rather than as a field at the end.

#ifndef DRELAY_EVIDENCE_H
#define DRELAY_EVIDENCE_H

#include "DrelayHookShared.h"

#include <windows.h>

namespace drelay {

// Writes the whole of a run's evidence into `runDir`:
//
//   events.jsonl   one JSON object per trace row, plus one per log line, in
//                  sequence order -- the machine-readable form
//   summary.txt    the human-readable form: target table, offsets, counters
//   offsets.csv    both offset sources side by side with their agreement
//   frames.csv     the frame-tick sample, with the per-call cost
//
// Called once, after the live checks, so the counters it reports are final.
void DrelayWriteEvidence(const wchar_t* runDir);

} // namespace drelay

#endif // DRELAY_EVIDENCE_H
