# The Java bridge — keeping the proxy in Java

This document specifies how the in-process C++ hook talks to the existing Java `drelay` process.
It answers the concrete question "do we need Java Native Access?" and it fixes the one architectural
decision that everything else follows from.

The authoritative, source-cited research behind the numbers here is
[BRIDGE-RESEARCH.md](BRIDGE-RESEARCH.md) (693 lines, produced alongside this document); where a
figure is quoted below it comes from there.

---

## 1. The decision that shapes everything: the decision cannot cross the bridge

There is a hard latency argument, and it does not depend on any measured transport cost.

The hook must act **inside the game's frame**, in the window between the client's collision decision
and the server's damage. The bridge cannot be on that path, because the best case is a full
round trip through another process and the worst case is a full round trip plus the Java side's poll
quantum. Measured, the loop is:

| segment | cost |
|---|---|
| shared-memory write + read | sub-microsecond in principle, **not yet measured on this platform** |
| Java-side poll quantum | up to the poll interval — bounded by design, but real |
| the game's frame quantum | 0 ms if the decision is made in the hook; ~16.7 ms at 60 fps if it must wait for the next frame |

The frame quantum is the expensive term, and it is only avoidable one way: **the decision is made in
C++, and the bridge carries observation and configuration only.**

This is not a compromise, it is the correct architecture, and the existing project already contains
the same principle: the relay's own auto-nexus takes its decision on the thread that read the packet
and writes the escape *before* forwarding that packet, precisely so that no scheduling hop sits
between the observation and the act (`docs/INJECTION.md` §1.4). The hook inherits that principle and
extends it into the game process.

So:

| what | where it lives | why |
|---|---|---|
| threat model, solver, escape decision | **C++, in the game process** | must run inside the frame |
| packet reading, decoding, logging, dashboard, and the escape packet itself | **Java, the existing relay** | already built, already verified, already the only writer of the socket |
| configuration, telemetry, health | **across the bridge** | latency-tolerant by definition |

**The relay does not become a native application, and nothing about the existing packet path
changes.** The hook is additive: a new producer of events and a new consumer of settings.

---

## 2. Does JNA suffice? Yes — and here is the precise reason

The user's instinct ("consider if you need JNA") is the right question, and the answer is that JNA is
sufficient *because the recommended design puts it off the hot path entirely*.

### 2.1 What JNA costs, and why it does not matter here

| binding | measured per-call cost (Windows x64, JMH) |
|---|---|
| **JNA** (interface mapping) | **1346 ± 72 ns** |
| JNA direct mapping | 1373 ± 70 ns |
| JNI | 56.6 ± 8.1 ns |
| **FFM (`java.lang.foreign`)** | **49.7 ± 6.7 ns** |

*(with a struct argument the gap widens to JNA 4037 ns vs JNI 260 ns / FFM 238 ns.)*

Note in passing: JNA's own FAQ claims interface mapping costs "hundreds of **microseconds**" rather
than "tens of nanoseconds" relative to JNI. That is a units error of roughly 1000×, and it should not
be repeated. Also note that **JNA direct mapping was not faster** than interface mapping in the
benchmark, contrary to the usual advice — the struct marshalling dominates, not the call mechanism.

At ~1.35 µs per call, JNA costs about **13 µs for a whole session** if it is called ten times. It is
used **only at attach**: `OpenFileMapping`, `MapViewOfFile`, `CloseHandle`. After that, all traffic
is through a `ByteBuffer` view over the mapped region and a `VarHandle`, which are ordinary Java with
**no native call at all**.

### 2.2 Why not FFM, given it is faster and stable

The Foreign Function & Memory API is final since Java 22 (JEP 454) and is *not* preview on Java 25,
so it is genuinely available. It is 25× faster per call and it supports upcalls. It is not chosen
because:

- it buys nothing when the binding is off the hot path, and this is a dependency-free project by
  design ("the released jar has no dependencies", `README.md`);
- its restricted methods (`Linker::downcallHandle`, `MemorySegment::reinterpret`,
  `SymbolLookup::libraryLookup`) need `--enable-native-access=ALL-UNNAMED` to avoid warnings, which
  is one more thing that can fail in the field;
- **JNA is still needed anyway for the JNI route**, and mixing both bindings in one small project is
  a worse outcome than picking the more forgiving one.

*The one place FFM would win outright is upcalls* (native→Java callbacks), where it is 3.5–4× JNI.
**This design has no upcalls**, because the decision must be in C++ anyway (§1). If a future feature
ever needs the hook to call back into Java synchronously, that is the moment to revisit FFM — and
that is exactly the kind of requirement that would mean the architecture had gone wrong.

### 2.3 The zero-dependency fallback

Worth recording because it is real: **pure Java can map shared memory without any native binding at
all**, via `FileChannel.map(MapMode.READ_WRITE, 0, size, Arena.ofShared())`, which returns a
`MemorySegment` and is not a restricted method. Both processes map the same file. This removes JNA
from the project entirely at the cost of a file on disk (and therefore of the pagefile-backed
`Local\` section that never touches the disk). Keep it in the back pocket: if vendoring a 1.86 MiB
jar ever becomes unacceptable, this is the escape hatch, and it changes only the attach code.

---

## 3. Transport

### 3.1 What was considered

| transport | measured | verdict |
|---|---|---|
| **pagefile-backed named section + ring** | not measured on this platform | **chosen.** No syscall on the data path, no admin, survives either side restarting independently |
| loopback TCP | ~10 µs per small exchange | the **control** channel's job; the relay already speaks TCP |
| named pipe, message mode | ~13 µs per small exchange | rejected: pure Java **cannot** use message mode (a `CreateFile` pipe client is always in byte-read mode initially), and Java's `FileChannel` cannot do overlapped I/O on a pipe, so a blocking read is uninterruptible except by `close()`. Using JNA for `CreateNamedPipe`+`SetNamedPipeHandleState` would fix it, but then the data path depends on JNA being correct rather than on a `ByteBuffer` |
| `WM_COPYDATA` | — | rejected: `SendMessage` is synchronous and the payload is only valid during the call; it would block the game thread |
| driver + IOCTL | — | unnecessary; it needs a signed driver, installation and admin |

**No elevation is required for any of these** — a default-descriptor named section with a `Local\`
prefix, a named pipe with a default descriptor, and a loopback socket are all unprivileged. That
matters because the existing launcher already elevates for address claiming and the hook should not
add to that requirement.

### 3.2 The shared-memory layout

Named section `Local\DrelayHookShm`, pagefile-backed, 2–4 MiB:

```
header (one 64-byte cache line, read-mostly):
    u32 magic            'DRHK'
    u32 version          layout version; mismatch => refuse
    u32 capacity         slots per ring
    u32 slotSize         bytes per slot
    u64 epoch            bumped by the hook on EVERY start
    u32 hookPid
    u64 heartbeatSeq     advanced by the hook every frame
    u64 producerDropped  count of dropped publications
    u64 consumerSeq      written by Java, the newest slot it has consumed

ring A  hook -> Java   events   (telemetry, decisions, threat snapshots)
ring B  Java -> hook   commands (settings, enabled/disabled, dry-run)
```

**Ring discipline: sequence-stamped slots, not a head/tail ring.**

```
producer: write payload into slot[n % capacity]
          slot.seq.store(n, release)          // the sequence stamp is written LAST
consumer: read slot[lastSeq+1].seq with acquire
          if it equals lastSeq+1 -> consume, lastSeq++
          if it is greater       -> an earlier slot was overwritten: count an overrun, jump forward
```

This shape is chosen over a head/tail ring for reasons that each remove a class of bug: there is no
head/tail to tear, no CAS, no ABA problem, no false sharing between two indices, and overrun
detection is a single comparison rather than a heuristic. **The producer never blocks**: if the
consumer is behind, the producer overwrites and increments `producerDropped`, which is the correct
behaviour for telemetry and for a game thread that must never wait.

### 3.3 Memory ordering, stated explicitly

This is the part that is easy to get wrong and hard to notice:

- the sequence stamp is written with a **release** store and read with an **acquire** load;
- in Java that means `VarHandle.setRelease` / `getAcquire` over a `ByteBuffer` view created once via
  `MethodHandles.byteBufferViewVarHandle`;
- **the specific hazard to avoid**: a *plain* read of a shared index inside a spin loop can be
  hoisted by the JIT into an infinite loop. Always use an acquire/volatile access mode or an
  explicit fence. The JDK documents `getAcquire`/`setRelease` as compatible with C++
  `memory_order_acquire`/`release`, and `fullFence()`/`acquireFence()`/`releaseFence()` are available
  directly.
- the C++ side uses `std::atomic<uint64_t>` with `memory_order_release` / `memory_order_acquire`, and
  both 8-byte-stamped slots and the 64-byte-aligned header must be aligned so that a single
  store is atomic (they are: naturally aligned 8-byte and cache-line-aligned respectively).

### 3.4 Timestamps, and why not a shared clock

Two processes do not share a clock, and an event timestamped by the hook in `QueryPerformanceCounter`
ticks is not comparable with a Java `System.nanoTime()` value. Three options, in order of preference:

1. **The hook stamps with its own monotonic frame counter** (`frameIndex`, plus `World.ClientTime`,
   which is the game's own clock and is therefore the *right* clock for anything game-related).
   Java records its own arrival time when it reads the event. The delay is then measured in Java's
   own clock, which removes cross-process clock comparison entirely.
2. Correlation by QPC offset, calibrated at handshake and re-calibrated on drift. Workable, more
   machinery.
3. A shared QPC read. Do not — it is a syscall-ish dependency on a path that should have none.

**Take option 1.** The interesting question is always "how long between the game deciding and this
line being written", and that is answerable with one clock.

---

## 4. What crosses the bridge

### 4.1 Hook → Java (events)

| event | when | payload |
|---|---|---|
| `hello` | on attach | epoch, version, build fingerprint, flags |
| `frame` | at most every N frames (default 10) | frameIndex, `World.ClientTime`, player x/y, hp/max/shield/barrier, threat count, solver cost µs |
| `threat` | on spawn, subject to a cap | gameId, startTime, x, y, angle, damage, hitsPlayer, owner |
| `decision` | on every *decision that matters* — chosen move, egress, escape, refusals | the kind, the reason, the inputs that produced it, the projected damage |
| `collision` | on every `HitCharacter` | gameId, predicted-vs-actual impact time, the damage the game computed |
| `escape` | when the hook calls `RequestEscape` | the reason, the projected lethal damage, the HP at the moment of the decision |
| `error` | on any internal failure | a reason string |

**The rate limit is deliberate.** `frame` at 10 Hz and `threat` capped are what keep the game
thread's cost bounded; a per-frame per-bullet feed would be the tail wagging the dog.

`collision` is the single most valuable event and should be emitted **always**, not sampled: it is
what makes the offline test of "did the model predict the hits that happened" possible
([DODGE-ALGORITHM.md](DODGE-ALGORITHM.md) §10, invariant 4).

### 4.2 Java → hook (commands)

Sparse, low-rate, and idempotent:

| command | payload |
|---|---|
| `setConfig` | the whole settings block (enabled, dryRun, aggressive thresholds, budgets) |
| `ping` | nothing; the hook answers in the heartbeat |
| `shutdown` | detach cleanly |

The hook applies `setConfig` at the next frame boundary and never mid-frame. **A command that never
arrives must be harmless**: the hook starts with conservative defaults compiled in, so a relay that
is not running leaves it in a defined state.

---

## 5. Threading and failure

### 5.1 Threads

| side | thread | behaviour |
|---|---|---|
| hook | the game's main thread | writes events inline; bounded memcpy, no allocation, no locks, no syscalls; on contention, overwrite and count |
| hook | a heartbeat thread (optional) | only needed if the frame loop can stall for long periods; the frame counter is normally the heartbeat |
| Java | **one dedicated platform thread** | **not** a virtual thread: a spin/poll loop on a virtual thread pins its carrier. Poll with `Thread.onSpinWait()` for a bounded spin, then `LockSupport.parkNanos(...)` backoff |

### 5.2 Detecting that the other side died

A file mapping does not close while any handle references it, so **Java's view stays valid but
frozen** when the hook dies — a silent failure that looks like "the game stopped sending events"
rather than "the game crashed". Combinations that distinguish the cases:

| observation | meaning | response |
|---|---|---|
| `heartbeatSeq` unchanged for > 250 ms, process alive | the game is stalled or hung | report "stalled", do not act |
| `heartbeatSeq` unchanged, process gone | the game exited or crashed | detach cleanly, report |
| `epoch` changed | the hook restarted | reset `lastSeq`, re-read the header, expect a `hello` |
| `seq > lastSeq + 1` | the ring overran | count it, jump forward, report the drop rate |
| `magic` or `version` wrong | not our mapping, or a layout change | refuse to attach, log loudly |

The heartbeat is deliberately **a counter, not a timestamp**, for the reason in §3.4: comparing a
counter against a local clock is exact and needs no shared time base.

### 5.3 Lifecycle and ordering

```
Java relay starts
  └─ is the game running and is the section present?
       no  -> run exactly as today; the auto-nexus is the whole feature
       yes -> OpenFileMapping("Local\\DrelayHookShm"); MapViewOfFile; build ByteBuffers;
              read the header; validate magic/version; record epoch; start the poll thread
                       ↓
              events flow; the dashboard gains a "hook" panel
                       ↓
Game exits or hook detaches
  └─ heartbeat stops -> Java reports "hook gone" and keeps running
```

The intended operating modes, all of which must work:

1. **relay only** (today): no hook present, nothing changes.
2. **relay + hook, dry run**: the hook decides and reports, calls `RequestEscape` never.
3. **relay + hook, live**: the hook may escape; the relay sees the `escape` event and can corroborate
   it against the `EscapeAck` it observes on the wire.

Mode 3 is the one that makes the relay's existing ack-tracking (`docs/AUTONEXUS.md` §5) directly
useful: **the hook's `escape` event and the relay's `EscapeAck` are the two halves of the same
story**, and joining them on `World.ClientTime` gives an end-to-end latency measurement for the
escape path that neither side could produce alone. That join is the reason the bridge is worth
building at all.

---

## 6. Build and deployment

| concern | decision |
|---|---|
| artifact | vendor `jna-5.19.1.jar` (1.86 MiB, dual LGPL-2.1+/Apache-2.0) in the repo for the plain-`javac` build; `net.java.dev.jna:jna:5.19.1` for Maven |
| `jna-platform` | **not needed** — declaring our own three-method interface avoids the extra 1.35 MiB |
| single jar | `maven-shade-plugin`; **do not relocate `com.sun.jna`** — JNA's stub extraction uses a hard-coded `/com/sun/jna/` resource path and whether shade rewrites that literal is unverified |
| manifest | add `Enable-Native-Access: ALL-UNNAMED` (the one accepted value, and the documented way to silence the native-access warnings from an executable jar) |
| the DLL | ship `drelay_hook.dll` next to the jar and set `-Djna.library.path`; loading a DLL out of `%TEMP%` (JNA's default extraction) is the path most likely to be blocked by application control or antivirus |
| no-JNA fallback | `FileChannel.map(..., Arena.ofShared())` over a file both sides map; changes only the attach code |

---

## 7. The smallest possible spike

Roughly 120 lines, no game, no .NET, no JNA in the first pass. Its entire purpose is to replace the
one number nobody could find a measurement for — **the actual shared-memory round-trip latency on
this platform** — with a measured one.

```
spike/shm_writer.cpp   CreateFileMappingW(INVALID_HANDLE_VALUE, ..., PAGE_READWRITE, 0, 1<<20,
                                           L"Local\\DrelaySpike")
                       publish 64-byte payloads at 1 kHz on a sequence-stamped ring
spike/ShmReader.java   ZERO DEPENDENCY: FileChannel.open + map(READ_WRITE, 0, 1<<20, Arena.ofShared())
                       read seq with getAcquire, verify payload integrity, print p50/p99/max
```

**Success criteria**, all of which are cheap to check and none of which is a judgement call:

1. **zero torn reads over 10 million messages** (the payload carries a checksum and a monotonic
   counter);
2. **p99 end-to-end < 1 ms**;
3. **< 1 % CPU on the Java side** with the spin-then-park backoff;
4. the writer killed mid-run produces a **"stalled" report within 250 ms**, with no crash and no
   100 % spin;
5. the writer restarted with a bumped `epoch` produces a **clean resync** with no lost-slot
   confusion.

Then, and only then, re-run the same spike against the pagefile-backed `Local\` section through JNA
to prove the attach path, and add the second half of the spike in parallel: a four-method JNA named
pipe interface with a C++ client at 200 Hz, verifying that client death surfaces as
`ERROR_BROKEN_PIPE` and that the disconnect/connect cycle recovers. The pipe is the candidate for the
control channel if TCP ever proves awkward.

---

## 8. What the bridge is *not*

- **Not a control path.** No decision crosses it (§1).
- **Not a reason to move any existing Java code.** The relay's packet path, its logging, its
  dashboard and its escape injection are unchanged and correct.
- **Not load-bearing for safety.** The hook never blocks on the bridge, never waits for a reply, and
  behaves identically whether Java is present or not. If the relay is not running, the hook still
  dodges and still escapes — it simply has nowhere to report.
- **Not a place for the hook to write packets.** The relay owns the socket and its single-writer
  discipline is what keeps frames whole (`docs/INJECTION.md` §1.4). Two writers would reintroduce
  exactly the interleaving bug that discipline exists to prevent.
