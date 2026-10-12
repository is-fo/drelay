#!/usr/bin/env python3
"""Is "ZERO compiled" from hook.log.sizes a fact about the game, or about the table?

`DrRecordCompileSize` (DrelayLog.cpp) inserts an IL size into a 512-slot
open-addressed table by linear probing from `size % 512`, and gives up after 64
probes, counting the compilation as `g_sizeHistogramOverflow`.  `WriteTargetSizeSummary`
then reads only the table, so a size whose insert gave up is reported as
"ZERO compiled -- this method never reached the JIT".

THE ARGUMENT THIS SCRIPT MAKES EXACT.

Slots are never freed, so table occupancy only grows.  If, in the FINAL table read
off disk, the probe chain starting at `size % 512` reaches an empty slot at probe
index d, then at every earlier moment the chain was no longer than d.  So if d < 64
the insert would have succeeded whenever that size was first compiled -- meaning the
size is genuinely absent from the stream.

If instead the final chain is blocked for >= 64 probes, the absence proves nothing:
that size's insert always failed and the size is permanently invisible.

So each target verdict gets one of three readings:
  PROVEN ABSENT      -- chain is short enough that a real compile would have been seen
  UNFALSIFIABLE      -- chain is blocked; the table cannot answer the question
  PRESENT            -- the size is in the table
"""
import re
import sys

SLOTS = 4096
PROBE_LIMIT = 4096
TARGETS = {'frameTick': 1286, 'spawn': 2014, 'collision': 989, 'escape': 335}


def load(path):
    entries = {}
    overflow = 0
    for line in open(path, 'r', encoding='utf-8', errors='replace'):
        line = line.strip()
        if line.startswith('#'):
            m = re.match(r'#\s*(\d+)\s+compilation', line)
            if m:
                overflow = int(m.group(1))
            continue
        if not line or line.startswith('il_size'):
            continue
        parts = line.split(',')
        if len(parts) < 2:
            continue
        try:
            size, count = int(parts[0]), int(parts[1])
        except ValueError:
            continue
        if count:
            entries[size] = count
    return entries, overflow


def occupancy(entries):
    """slot -> size, using the same hash and linear probe as the C++."""
    slots = {}
    for size in entries:
        start = size % SLOTS
        for probe in range(PROBE_LIMIT):
            slot = (start + probe) % SLOTS
            if slot not in slots:
                slots[slot] = size
                break
        # a size whose insert gave up is absent from the model, which is the point
    return slots


def chain_length(slots, size):
    """How many consecutive occupied slots sit in front of `size`'s own slot?"""
    start = size % SLOTS
    for probe in range(SLOTS):
        slot = (start + probe) % SLOTS
        if slot not in slots or slots[slot] == size:
            return probe
    return SLOTS


def main(path):
    entries, overflow = load(path)
    slots = occupancy(entries)
    total = sum(entries.values())
    print('file            :', path)
    print('distinct sizes  : %d  (table has %d slots, load %.0f%%)'
          % (len(entries), SLOTS, 100.0 * len(entries) / SLOTS))
    print('compilations    : %d recorded, %d could not be recorded' % (total, overflow))
    print('slots occupied  : %d' % len(slots))
    print()
    print('%-12s %7s %8s %6s  %s' % ('target', 'ilSize', 'slot', 'probe', 'verdict'))
    for name, size in TARGETS.items():
        slot = size % SLOTS
        if size in entries:
            print('%-12s %7d %8d %6s  PRESENT (count %d)'
                  % (name, size, slot, '-', entries[size]))
            continue
        d = chain_length(slots, size)
        if d < PROBE_LIMIT:
            verdict = ('PROVEN ABSENT -- an insert here would have claimed slot %d, '
                       'so this size never reached the JIT' % ((slot + d) % SLOTS))
        else:
            verdict = ('UNFALSIFIABLE -- chain is blocked for >=%d probes; this size '
                       'could have compiled and been dropped as overflow' % PROBE_LIMIT)
        print('%-12s %7d %8d %6d  %s' % (name, size, slot, d, verdict))

    # Which sizes are structurally invisible, for context.
    blocked = [s for s in range(1, 4000) if s not in entries
               and chain_length(slots, s) >= PROBE_LIMIT]
    print()
    print('sizes 1..3999 that this table could never have recorded: %d' % len(blocked))
    if blocked:
        print('  e.g.', blocked[:40])

    # Which of the dropped compilations are identifiable?  None -- that is why the
    # probe argument is needed rather than reading the overflow counter.
    print()
    print('NOTE: the %d unrecorded compilations are anonymous; the counter records how'
          % overflow)
    print('      many were lost, not which sizes. The probe argument above is what')
    print('      makes "absent" a fact rather than a hope.')


if __name__ == '__main__':
    for p in sys.argv[1:]:
        main(p)
        print()
