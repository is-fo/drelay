// StartupHook.cs -- the managed half of Phase 1.
//
// WHEN THIS RUNS, AND WHY THAT IS THE WHOLE POINT
// -----------------------------------------------
// The host invokes a startup hook after the runtime is initialised and before the
// application's entry point.  That window is the only place in the process where
// all three of these are true at once:
//
//   * CoreCLR exists, so `clrjit.dll` is loaded and the JIT vtable can be found;
//   * the game assembly is loaded, so its MVID can be read from the runtime
//     rather than from a file;
//   * the game's own code has not run, so no target method has been compiled yet
//     and the JIT detour will see every one of them.
//
// So the bootstrap does not install anything itself.  It *answers questions* --
// is this the build we pinned, where does the runtime think these fields live,
// what is the MVID of the assembly that is actually loaded -- publishes the
// answers into the shared block, and then waits for the native side to confirm
// that it has installed its detour.  Only then does it return and let `Main`
// run.  The waiting is the mechanism that makes "installed before the first
// compilation" a fact rather than a race.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Drelay.Bootstrap
{
    /// <summary>
    /// The arming decision and the offset measurement behind the live-state sensor:
    /// one read of <c>World.Shared.Player.Position</c> per game frame, using the
    /// offsets measured at attach.
    ///
    /// ONE MANAGED ENTRY POINT ON ONE THREAD, AND THAT IS THE POINT.
    ///
    /// <see cref="OnProbe"/> runs on the native watcher thread and decides whether
    /// the measured offsets describe a live world; it is where the reflection the
    /// cross-check needs can happen, because that thread is a genuine unmanaged
    /// thread -- so its reverse P/Invoke is exactly what the runtime expects -- and
    /// because it is not inside a game frame.
    ///
    /// THERE USED TO BE A SECOND ENTRY POINT ON THE GAME'S OWN THREAD, AND IT COULD
    /// NOT WORK.  `OnFrame` was called from the frame detour through its
    /// `[UnmanagedCallersOnly]` transition stub, on the argument that entering the
    /// stub is what makes a managed read legal from a native detour.  That is
    /// backwards: the frame thread is already inside managed code when the detour
    /// intercepts it, so the reverse P/Invoke is illegal and the runtime fail-fasts
    /// the process with `COR_E_EXECUTIONENGINE` (0x80131506).  The frame read is
    /// native now -- see DrelayFrame.cpp -- and the type identity and offsets
    /// measured here are published into the shared block for it.
    ///
    /// The first version of this armed at attach, discovered that
    /// <c>World.Shared</c> is always null that early, and disabled itself for the
    /// session -- which is how Spike 1.3's success test came to have never run.
    /// </summary>
    public static class ManagedArmingProbe
    {
        // The read plan, fixed at attach.  Static fields rather than instance
        // state because the probe is reached through a raw function pointer: there
        // is no object to hang it off, and the address must be stable for the life
        // of the process.
        private static IntPtr _worldSharedStaticAddress;
        private static int _worldPlayerOffset;
        private static int _playerPositionOffset;
        private static IntPtr _sharedBlockPointer;

        // The game's Player MethodTable, captured as a bare pointer at attach.
        // This side uses it for the cross-check, which compares the object the
        // pointer chain lands on against the type the runtime knows.  It is ALSO
        // published into the shared block, because the frame read is native now and
        // needs the same identity check; the two must be the same value, and taking
        // it once and publishing it is what makes that true by construction.
        private static IntPtr _playerTypeHandle;

        // THE REFLECTION ROUTE OF THE CROSS-CHECK, CAPTURED AT ATTACH.
        //
        // The cross-check reads `World.Shared.Player.Position` twice: once through
        // the runtime's own field accessors and once through the raw pointer chain
        // built from the measured offsets.  Holding the `FieldInfo` objects rather
        // than re-resolving them by name keeps the check from depending on
        // reflection lookups succeeding at a moment when the game is mid-frame.
        private static FieldInfo _worldSharedField;
        private static FieldInfo _playerField;
        private static FieldInfo _positionField;
        private static FieldInfo _positionXField;
        private static FieldInfo _positionYField;

        // The arming gate.  `Waiting` until a world exists; the probe below decides
        // and publishes; the native frame read consults it and reads nothing until it
        // says `Armed`.
        //
        // VOLATILE BECAUSE TWO THREADS READ IT: the watcher thread writes it from
        // inside `TryArm`, and the game's frame thread reads it at the top of
        // `FrameTickDetour`.  `Armed` is written LAST, after every field the read path
        // depends on -- so a frame that observes the new state also observes the
        // offsets and the published cross-check numbers.
        //
        // NOT `volatile`, AND THAT IS NOT AN OVERSIGHT: C# rejects `Volatile.Read`
        // on a `volatile` field (CS0420, "a reference to a volatile field will not
        // be treated as volatile") because the two mechanisms are alternatives.  The
        // explicit `Volatile.Read`/`Volatile.Write` calls name the barrier at the
        // access that needs it, which is exactly the ordering argument above.
        private static int _armState = (int)SharedProtocol.ArmWaiting;

        // Claimed with an interlocked exchange.  With the frame entry point gone, the
        // probe on the watcher thread is the only caller, so the guard is now
        // defensive rather than load-bearing -- but `TryArm` is reached through a raw
        // function pointer with no caller contract the compiler can enforce, and a
        // second caller running the reflection cross-check twice would double the cost
        // and make the published tick ambiguous.
        private static int _arming;

        // One line, once, for the state the first live run misdiagnosed: a world that
        // exists with no player in it yet.  See `ArmIfWorldExists`.
        private static int _worldWithoutPlayerLogged;

        /// <summary>
        /// THE ARMING PROBE.  Called by the native watcher thread, repeatedly, from
        /// the moment the resolve callback returns until the process exits.
        ///
        /// WHY THIS IS A SEPARATE ENTRY POINT AND NOT PART OF THE FRAME PATH.
        ///
        /// Spike 1.3's success test is "read exactly one float from the live world",
        /// and it could not run because the world reference (`World.Shared`) is null
        /// until the game loads a scene, while the hook attaches before the game has
        /// a world.  The sensor was therefore disabled at attach, and nothing was
        /// ever read.  Arming it correctly means answering "is there a world yet?"
        /// and then proving the measured offsets describe the live object graph --
        /// and both of those need reflection, which allocates.
        ///
        /// The frame read's contract is that it allocates nothing: it runs
        /// inside the game's frame and its cost is game frame time.  Moving the
        /// cross-check there would spend that contract for no benefit, because the
        /// frame detour's receiver is a `DarzaWindow`, not the world -- the world
        /// reference comes from a static field this method reads directly, so the
        /// frame thread knows nothing the watcher thread does not.
        ///
        /// AND THE FRAME THREAD CANNOT RUN MANAGED CODE AT ALL, which is the stronger
        /// reason and the one that was measured: any managed call from inside that
        /// detour is an illegal reverse P/Invoke and fail-fasts the process.  The
        /// watcher thread is an unmanaged thread, so entering managed code from it is
        /// exactly what the runtime expects.  That asymmetry is what makes THIS the
        /// only place in the process where reflection may happen mid-run.
        ///
        /// So the decision is made on the watcher thread, where the runtime is fully
        /// up and reflection is ordinary work, and it is made at every opportunity
        /// that thread has: it already polls during the collection delay, during the
        /// discovery window, during the live-check wait and while idling.
        ///
        /// It is idempotent and cheap once decided (one volatile read), so the
        /// native side calls it without a state machine of its own.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void OnProbe()
        {
            // No try/catch here: `TryArm` catches everything itself.  An exception
            // escaping this stub would unwind into the native caller's `__try`,
            // which cannot catch a managed exception, and that is a process-level
            // failure rather than a missed reading.
            TryArm();
        }

        /// <summary>
        /// The arming decision, made at most once per process.
        ///
        /// THREE OUTCOMES, AND THEY ARE NOT INTERCHANGEABLE:
        ///
        ///   * no world yet -- not a conclusion, so the state stays `Waiting` and
        ///     the next call tries again.  This is the normal state for the first
        ///     tens of seconds of a run;
        ///   * `Unconfigured` -- the resolve callback never measured the offsets, so
        ///     there is nothing to check.  Terminal;
        ///   * `Armed` or `Refused` -- the cross-check ran.  `Refused` means the
        ///     pointer chain built from the offsets did NOT reproduce what
        ///     reflection says, which is a finding about this build's layout and the
        ///     reason the callback must stay off rather than read through them.
        /// </summary>
        private static void TryArm()
        {
            if (_sharedBlockPointer == IntPtr.Zero)
            {
                return;
            }

            // NO PRE-AMBLE HERE ANY MORE, AND THE ROUND THAT REMOVED IT IS WORTH ONE
            // PARAGRAPH.
            //
            // This method used to begin by pre-compiling `OnFrame`, the
            // `[UnmanagedCallersOnly]` method the frame detour called.  The premise was
            // that the first managed call from inside the game's frame would otherwise
            // have to JIT-compile it there.  The live crash showed the premise was
            // wrong: the call never reached the method body at all.  Entering an
            // `[UnmanagedCallersOnly]` method from the frame detour is an illegal
            // reverse P/Invoke -- that thread is already inside managed code, with live
            // managed frames below the detour -- and coreclr fail-fasts the process for
            // it before a single managed instruction runs.  Pre-compiling could not have
            // helped, and `managedFrameWaits = 0` in the crashed runs' own blocks is the
            // proof that the body never started.
            //
            // There is nothing to pre-compile now because there is no managed frame
            // entry point: the read is native.  See DrelayFrame.cpp.
            if (Volatile.Read(ref _armState) != (int)SharedProtocol.ArmWaiting)
            {
                return;
            }
            if (_worldSharedStaticAddress == IntPtr.Zero || _positionXField == null
                || _positionYField == null)
            {
                // The resolve callback ran but did not get as far as configuring
                // this type: the assembly did not load, its names moved, or the
                // point type has no X/Y fields to compare.  There is nothing to
                // retry, and naming the state separately from a refusal is the
                // point -- see SharedProtocol.ArmUnconfigured.
                PublishArmState(SharedProtocol.ArmUnconfigured);
                return;
            }

            if (Interlocked.CompareExchange(ref _arming, 1, 0) != 0)
            {
                return;
            }
            try
            {
                ArmIfWorldExists();
            }
            catch (Exception ex)
            {
                // Reached from native code, so this must not escape.  A throw here
                // means the cross-check could not be made at all, which is a
                // refusal: reading through offsets that were never checked is the
                // one thing this gate exists to prevent.
                PublishArmState(SharedProtocol.ArmRefused);
                Volatile.Write(ref _armState, (int)SharedProtocol.ArmRefused);
                AppendArmLog("ERROR", "the arming cross-check threw, so the frame read path "
                                      + "stays off: " + ex);
            }
            finally
            {
                _arming = 0;
            }
        }

        /// <summary>
        /// Walks the pointer chain and the object graph and compares the two.
        ///
        /// The comparison is of coordinates rather than of addresses, deliberately.
        /// An address comparison would test that two routes reached the same object,
        /// which the runtime can answer without the offsets being right; a
        /// coordinate comparison tests that the bytes at `Player + positionOffset`
        /// really are the floats the runtime's own accessor reports, which is the
        /// claim the hook's read path depends on.
        /// </summary>
        private static unsafe void ArmIfWorldExists()
        {
            // THE PRODUCTION PATH FIRST, BECAUSE IT IS ALSO THE READINESS TEST.
            //
            // Hop 1, by the raw route.  This is the exact read the native frame path
            // will perform, so the check exercises the production path rather than
            // a parallel one.
            IntPtr world = *(IntPtr*)_worldSharedStaticAddress;
            if (world == IntPtr.Zero)
            {
                PublishProbeWait();
                return;
            }

            // A NON-NULL WORLD IS NOT A READY WORLD, AND THE FIRST LIVE RUN IS WHY
            // THIS CHECK EXISTS.
            //
            // `World.Shared` stops being null while the game is still loading a scene
            // -- about ten seconds into a run, well before a character exists -- and
            // `World.Player` stays null until one does.  The first version of this
            // method treated a non-null world as "ready", asked reflection for the
            // player, and got
            //
            //   System.Reflection.TargetException: Non-static field requires a target.
            //      at System.Reflection.FieldAccessor.GetValue(Object obj)
            //      at Drelay.Bootstrap.ManagedArmingProbe.ArmIfWorldExists()
            //
            // from `FieldInfo.GetValue(null)` on an instance field.  `TryArm` caught it
            // and refused, which is the right response to an exception and the wrong
            // response to this condition -- so the callback was disabled for the whole
            // session by a state that was merely "not yet".  That is the same defect
            // as the one this round exists to fix, one level down: **a not-yet-ready
            // state reported as a finding**.  It cost two live runs, and it is why the
            // probe now checks the graph it is about to read before it checks anything
            // with reflection.
            IntPtr player = *(IntPtr*)((byte*)world + _worldPlayerOffset);
            if (player == IntPtr.Zero)
            {
                // Logged once, because the distinction between "no world" and "a world
                // with nobody in it" is not visible in the single wait counter the
                // block publishes, and it is exactly the distinction that was
                // misdiagnosed.
                if (Interlocked.Exchange(ref _worldWithoutPlayerLogged, 1) == 0)
                {
                    AppendArmLog("INFO", "World.Shared is non-null but World.Player is still null: the "
                                         + "game has a world and no character in it yet, so the "
                                         + "cross-check waits for one instead of refusing");
                }
                PublishProbeWait();
                return;
            }

            if (!IsInstanceOf(player, _playerTypeHandle))
            {
                // The object at `World + playerOffset` is not a Player, so the
                // offset does not describe this build.  This is the failure the
                // MethodTable guard exists to catch, and it is a refusal rather
                // than a gap: the offset was checked and it is wrong.
                PublishCrossCheck(float.NaN, float.NaN, float.NaN, float.NaN);
                PublishArmState(SharedProtocol.ArmRefused);
                Volatile.Write(ref _armState, (int)SharedProtocol.ArmRefused);
                AppendArmLog("ERROR", "offset cross-check FAILED: the reference at "
                                      + "World+" + _worldPlayerOffset + " is not a Player "
                                      + "(its MethodTable does not match), so the offsets do "
                                      + "not describe this build; the frame read path stays closed");
                return;
            }

            // The reflection route, on the same two objects.  `GetValue(null)` on the
            // same static field yields the managed object, and every hop after it is
            // the runtime's own field resolution rather than an offset of ours.
            //
            // NULL IS RE-CHECKED HERE, AND IT IS NOT PARANOIA: the game can clear
            // either reference between the raw reads above and these calls -- leaving
            // a scene does exactly that -- and a null here means "gone again", which
            // is another wait, not a disagreement.
            object worldObject = _worldSharedField.GetValue(null);
            object playerObject = (worldObject != null) ? _playerField.GetValue(worldObject) : null;
            if (worldObject == null || playerObject == null)
            {
                PublishProbeWait();
                return;
            }

            object positionObject = _positionField.GetValue(playerObject);
            float reflectedX = (float)_positionXField.GetValue(positionObject);
            float reflectedY = (float)_positionYField.GetValue(positionObject);

            float* position = (float*)((byte*)player + _playerPositionOffset);
            float pointerX = position[0];
            float pointerY = position[1];

            PublishCrossCheck(reflectedX, reflectedY, pointerX, pointerY);

            if (Math.Abs(pointerX - reflectedX) < 0.0001f &&
                Math.Abs(pointerY - reflectedY) < 0.0001f)
            {
                // The offsets the chain used are now confirmed by a second route,
                // which is what makes self-check 7 a PASS rather than a GAP and
                // what `offsets.csv`'s `agree` column reports.
                MarkOffsetAgreed(0);
                MarkOffsetAgreed(1);
                MarkOffsetAgreed(2);
                NativeMethods.WriteBlockU32(_sharedBlockPointer, GeneratedLayout.Off_managedArmTick,
                                            unchecked((uint)Environment.TickCount));
                // LAST, so a frame that observes `Armed` also observes everything
                // written above it.  On x64 stores are not reordered with stores.
                PublishArmState(SharedProtocol.ArmArmed);
                Volatile.Write(ref _armState, (int)SharedProtocol.ArmArmed);
                // THE AGREEMENT IS WEAK WHEN BOTH ROUTES REPORT ZERO, AND SAYING SO IS
                // THE POINT.
                //
                // A live run showed that the world exists while a scene is still
                // loading, so this comparison can legitimately be made against a
                // default-initialised `GamePoint` of (0, 0) -- and two zeros agree for
                // any pair of addresses, including a wrong one.  In that case the
                // MethodTable check above is what carries the verification, and a
                // reader who sees `PASSED` without the caveat would take more from the
                // line than it earned.
                bool trivialAgreement = reflectedX == 0.0f && reflectedY == 0.0f &&
                                        pointerX == 0.0f && pointerY == 0.0f;
                AppendArmLog("INFO", HookEntry.Fmt(
                    "offset cross-check PASSED at t+{0} ms: reflection ({1:F4}, {2:F4}) and the "
                    + "pointer chain ({3:F4}, {4:F4}) agree, so the offsets describe this build; "
                    + "the frame read path is now ARMED{5}",
                    Environment.TickCount, reflectedX, reflectedY, pointerX, pointerY,
                    trivialAgreement
                        ? " (NOTE: both routes returned exactly zero, so this agreement is trivial "
                          + "and the MethodTable identity check is what carries the verification)"
                        : ""));
            }
            else
            {
                PublishArmState(SharedProtocol.ArmRefused);
                Volatile.Write(ref _armState, (int)SharedProtocol.ArmRefused);
                AppendArmLog("ERROR", HookEntry.Fmt(
                    "offset cross-check FAILED: reflection says ({0:F4}, {1:F4}) and the pointer "
                    + "chain says ({2:F4}, {3:F4}); the offsets do not describe this build's "
                    + "layout, so the frame read path stays CLOSED", reflectedX, reflectedY,
                    pointerX, pointerY));
            }
        }

        /// <summary>
        /// Records one successful read's worth of context without disturbing the
        /// counters the frame read path owns.
        /// </summary>
        private static void PublishArmState(uint state)
        {
            try
            {
                NativeMethods.WriteBlockU32(_sharedBlockPointer, GeneratedLayout.Off_managedArmState,
                                            state);
            }
            catch
            {
                // The block is memory another process writes; a failed publish
                // must never be the reason the hook fails.
            }
        }

        private static void PublishCrossCheck(float refX, float refY, float ptrX, float ptrY)
        {
            try
            {
                NativeMethods.WriteBlockF32(_sharedBlockPointer,
                                            GeneratedLayout.Off_managedCrossCheckRefX, refX);
                NativeMethods.WriteBlockF32(_sharedBlockPointer,
                                            GeneratedLayout.Off_managedCrossCheckRefY, refY);
                NativeMethods.WriteBlockF32(_sharedBlockPointer,
                                            GeneratedLayout.Off_managedCrossCheckPtrX, ptrX);
                NativeMethods.WriteBlockF32(_sharedBlockPointer,
                                            GeneratedLayout.Off_managedCrossCheckPtrY, ptrY);
            }
            catch
            {
            }
        }

        private static void PublishProbeWait()
        {
            try
            {
                uint waits = unchecked((uint)Marshal.ReadInt32(_sharedBlockPointer,
                                        GeneratedLayout.Off_managedProbeWaits)) + 1;
                NativeMethods.WriteBlockU32(_sharedBlockPointer,
                                            GeneratedLayout.Off_managedProbeWaits, waits);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Marks one published offset as confirmed by the pointer chain.
        ///
        /// Safe to call for indices 0..2 only because `Configure` is reached only
        /// after `ResolveOffsets` has written all three records; the guard is the
        /// one on the call path rather than a bounds check here.
        /// </summary>
        private static void MarkOffsetAgreed(int index)
        {
            try
            {
                int at = GeneratedLayout.Off_offsets + (index * GeneratedLayout.OffsetStride)
                       + GeneratedLayout.Offset_agree;
                Marshal.WriteByte(_sharedBlockPointer, at, 1);
            }
            catch
            {
            }
        }

        /// <summary>
        /// The arming decision is the one event in this file that a reader needs in
        /// order, so it is written with the same flush discipline as the bootstrap's
        /// own checkpoints -- and only from the arming path, never from the frame
        /// callback.
        /// </summary>
        private static void AppendArmLog(string level, string message)
        {
            HookEntry.AppendLog(level, message);
            HookEntry.FlushLog();
        }

        /// <summary>
        /// THE RESOLVE CALLBACK, AND WHY IT EXISTS AT ALL.
        ///
        /// Phase 1's root cause (PHASE1-SCOPE.md §6.1) is that **a startup hook
        /// cannot JIT-compile anything**.  Any call that forces a method to be
        /// compiled for the first time faulters the process, and the stack names the
        /// startup hook rather than the compiler -- which is why it presented as five
        /// different bugs over five rounds.
        ///
        /// The consequence is structural: everything that needs compiling has to
        /// happen *after* startup has completed.  So the bootstrap is reduced to
        /// publishing the target table and signalling, and the work that needs
        /// reflection, dynamic methods and assembly loading moves here -- to a
        /// managed method the **native** side calls, from its own thread, once the
        /// runtime is fully up and compiling normally.
        ///
        /// This is reached exactly once, by name, from `DrelayResolveOffsets` on the
        /// native side.  It may allocate, reflect and compile freely, because by the
        /// time it runs none of that is a first-time cost in a fragile window.  It
        /// still must not throw across the native frame, so it catches everything.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void OnResolve()
        {
            // The very first thing this method does, before any managed call at all:
            // a direct write of a sentinel into the block, so that "the callback was
            // entered" can be distinguished from "the callback was entered and then
            // died inside its first logging call".  A logging call is itself a lot of
            // machinery -- string formatting, a lock, file I/O -- and it is the last
            // thing that has been instrumented rather than the first.
            try
            {
                if (HookEntry.SharedBlockPointer != IntPtr.Zero)
                {
                    Marshal.WriteInt32(HookEntry.SharedBlockPointer,
                                       GeneratedLayout.Off_bootTick, 0x52455356 /* 'RESV' */);
                }
            }
            catch
            {
                // Nothing here is allowed to be the reason the callback fails.
            }

            try
            {
                HookEntry.AppendLog("INFO", "resolve[0] OnResolve entered");
                HookEntry.FlushLog();
                HookEntry.ResolveOffsetsAfterStartup();
            }
            catch (Exception ex)
            {
                HookEntry.AppendLog("ERROR", "the resolve callback threw: " + ex);
                HookEntry.FlushLog();
            }
        }

        /// <summary>
        /// Hands the measured layout to the arming probe.
        ///
        /// CALLED AT ATTACH, WHEN THE GAME HAS NO WORLD, AND THAT IS THE POINT.
        /// The offsets and the type identity are properties of the *build*, not of
        /// any particular world, so they are measured once and the world is looked
        /// up later -- every frame, in fact, because the reference is a static field
        /// that a scene change can null and re-create.  What was missing before was
        /// not the measurement but the moment: the gate was decided here, and here
        /// there is never a world to check against.
        ///
        /// The `FieldInfo` objects are carried alongside the offsets so the
        /// cross-check can compare the pointer chain against the runtime's own
        /// accessors without another reflection lookup while the game is running.
        ///
        /// THE `Player` METHODTABLE IS ALSO WRITTEN INTO THE SHARED BLOCK, in
        /// `ResolveOffsets`, because the native frame read needs the same identity
        /// check this side makes and the two must be the same value.  Taking it once
        /// and publishing it is what makes that true by construction rather than by
        /// two reflection calls that happen to agree.
        /// </summary>
        public static void Configure(IntPtr worldSharedStaticAddress, int worldPlayerOffset,
                                     int playerPositionOffset, IntPtr playerTypeHandle,
                                     FieldInfo worldSharedField, FieldInfo playerField,
                                     FieldInfo positionField, FieldInfo positionXField,
                                     FieldInfo positionYField)
        {
            _worldSharedStaticAddress = worldSharedStaticAddress;
            _worldPlayerOffset = worldPlayerOffset;
            _playerPositionOffset = playerPositionOffset;
            _playerTypeHandle = playerTypeHandle;
            _worldSharedField = worldSharedField;
            _playerField = playerField;
            _positionField = positionField;
            _positionXField = positionXField;
            _positionYField = positionYField;
        }

        public static void SetSharedBlock(IntPtr block)
        {
            _sharedBlockPointer = block;
        }

        // THE FRAME ENTRY POINT IS GONE, AND THIS IS WHERE IT WAS.
        //
        // `OnFrame` was the `[UnmanagedCallersOnly]` method the frame detour called once
        // per frame, and `IsPlausibleCoordinate` was its second guard.  Both are removed
        // rather than left dead, because leaving them would leave the next reader with a
        // mechanism that looks live and is not -- and because the reason they are gone is
        // the single most expensive finding of this phase:
        //
        //   ENTERING THIS METHOD FROM THE FRAME DETOUR FAIL-FASTS THE PROCESS.
        //
        // The frame detour runs on the game's own frame thread while that thread is inside
        // managed code -- between the managed caller of `DarzaWindow.Update` and its body
        // -- so the thread is already in cooperative GC mode with live managed frames
        // below the detour.  A reverse P/Invoke asserts the opposite: that its caller is
        // unmanaged, because the transition frame it pushes is where the GC stops walking
        // the managed stack.  coreclr therefore refuses it.  `JIT_ReversePInvokeEnterRare`
        // calls `ReversePInvokeBadTransition`, which fail-fasts with
        // `COR_E_EXECUTIONENGINE` (0x80131506) through `TerminateProcess`.
        //
        // That is why this death left no `crash.txt` and no managed stack, and why it
        // looked like a JIT-context problem for a round: it raises nothing an in-process
        // handler can see.  Six WER dumps across three builds put the exception at
        // `coreclr+0x244839` every time, with `Drelay.Hook.dll!FrameTickDetour` on the
        // faulting stack and the game's own `DarzasDominion.dll` frame below it -- and
        // `managedFrameWaits == 0` in every one of those runs' shared blocks, which is the
        // proof that the method body was never entered at all.
        //
        // `IsInstanceOf` BELOW IS NOT DEAD: `ArmIfWorldExists` uses it for the offset
        // cross-check, which still runs on the watcher thread, where entering managed code
        // from native code is exactly what the runtime expects.  The frame read itself is
        // native now -- it walks the same pointer chain and applies the same two guards --
        // so the type handle captured here is published into the shared block and the
        // native side compares against it.  See DrelayFrame.cpp and DrelayHookShared.h.

        // The game type names, resolved by reflection from names rather than
        // compiled against, because the bootstrap has no reference to the game
        // assembly and taking one would be a compile-time dependency on an
        // obfuscated build.
        internal const string WorldTypeName = "DarzasDominion.Darza.Game.World";
        internal const string PlayerTypeName = "DarzasDominion.Darza.Game.Objects.Entities.Player";
        internal const string PointTypeName = "DarzaCore.Geometry.GamePoint";

        /// <summary>
        /// A hand-written managed type check on a raw pointer.
        ///
        /// The MethodTable pointer is the first field of every managed object on
        /// x64, and during a GC marking phase its low three bits carry mark bits,
        /// so they are masked off before the comparison
        /// (HOOK-ARCHITECTURE.md section 5.1).  Comparing the masked pointer with
        /// <c>Type.TypeHandle.Value</c> is the same identity the runtime itself
        /// would use for an exact-type test.
        /// </summary>
        private static unsafe bool IsInstanceOf(IntPtr reference, IntPtr expectedTypeHandle)
        {
            if (reference == IntPtr.Zero || expectedTypeHandle == IntPtr.Zero)
            {
                return false;
            }
            IntPtr methodTable = *(IntPtr*)reference;
            if (methodTable == IntPtr.Zero)
            {
                return false;
            }
            IntPtr masked = (IntPtr)(methodTable.ToInt64() & ~0x7L);
            return masked == expectedTypeHandle;
        }
    }

    /// <summary>
    /// The startup hook.
    ///
    /// THE TYPE NAME IS FIXED BY THE RUNTIME, NOT BY THIS PROJECT.  It must be
    /// exactly `StartupHook` in the GLOBAL namespace, with a public static
    /// `Initialize()`.  The first version nested it inside `namespace
    /// Drelay.Bootstrap`, and the host refused to start with
    /// `TypeLoadException: Could not load type 'StartupHook' from assembly
    /// 'Drelay.Bootstrap'` -- the name it looks for is not the assembly-qualified
    /// one, so a namespace is not allowed.  `Initialize` catches everything, so
    /// the hook can never be a reason the game does not start.
    /// </summary>
    public static class HookEntry
    {
        private const string EnvSession = "DRELAY_HOOK_SESSION";
        private const string EnvRunDir = "DRELAY_HOOK_RUNDIR";
        private const string EnvGameDir = "DRELAY_HOOK_GAMEDIR";
        private const string EnvAssembly = "DRELAY_HOOK_ASSEMBLY";
        /// <summary>
        /// Path of the file backing the shared block. The bootstrap maps that file
        /// rather than opening a named section, because a file-backed mapping has
        /// no commit charge at creation and a named pagefile-backed one does; see
        /// the launcher's SharedBlock.Create for the measurement behind that.
        /// </summary>
        private const string EnvBlockFile = "DRELAY_HOOK_BLOCK";
        private const string EnvDryRun = "DRELAY_HOOK_DRYRUN";
        private const string EnvFullTrace = "DRELAY_HOOK_FULLTRACE";

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly List<string> Log = new List<string>();
        private static IntPtr _viewPointer;
        private static uint _session;

        /// <summary>
        /// <c>string.Format</c> under the invariant culture, and it is not a style
        /// preference.
        ///
        /// THE CULTURE ON THIS MACHINE PUTS A COMMA WHERE EVERY OTHER FILE IN THE RUN
        /// PUTS A FULL STOP.  `sv-SE` formats `12.5f` as `12,5`, so the bootstrap --
        /// the one participant in the run that is a managed process -- printed
        /// `at t+0,0 ms` and `reflection (12,5000, -7,2500)` while `hook.log`,
        /// `summary.txt`, `selfcheck.txt` and `offsets.csv` all printed `0.0` and
        /// `12.5000`, because the native side formats with `%.1f` and `%.4f` in the C
        /// locale.  The two halves of the ONE comparison that proves the offsets are
        /// real therefore read as different numbers to a reader -- and, worse, to a
        /// script: a parser that accepts `12.5000` and not `12,5000` silently fails to
        /// see the confirmation, which is exactly what the arming-gate test did on its
        /// first run.
        ///
        /// The culture is NOT changed process-wide: `CurrentCulture` is global state in
        /// a process that also contains a game, and a hook that reconfigures its host's
        /// formatting to fix its own log lines is a hook that changes the thing it is
        /// measuring.  The conversion is per call site instead, which is why every
        /// message that formats a number goes through here.
        /// </summary>
        internal static string Fmt(string format, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }

        /// <summary>
        /// The startup hook entry point.  The host calls this by reflection; the
        /// signature and the type name are fixed by the runtime, so neither may
        /// be renamed.
        /// </summary>
        public static void Initialize()
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                // The hook must never prevent the game from starting.  A failure
                // here means the native side times out, logs why, and installs
                // nothing -- so this is reported into the block if there is one,
                // and then swallowed.
                AppendLog("ERROR", "bootstrap threw: " + ex);
                FlushLog();
            }
        }

        private static void Run()
        {
            double t0 = Clock.Elapsed.TotalMilliseconds;

            string sessionText = Environment.GetEnvironmentVariable(EnvSession);
            if (!uint.TryParse(sessionText, out _session))
            {
                // Without the session id there is no mapping and no way to
                // report anything.  Nothing to do but leave.
                return;
            }

            string runDir = Environment.GetEnvironmentVariable(EnvRunDir);
            string gameDir = Environment.GetEnvironmentVariable(EnvGameDir);
            bool dryRun = Environment.GetEnvironmentVariable(EnvDryRun) == "1";
            bool fullTrace = Environment.GetEnvironmentVariable(EnvFullTrace) == "1";

            AppendLog("INFO", "bootstrap: environment read, opening the shared block");
            FlushLog();
            OpenSharedBlock();
            AppendLog("INFO", Fmt(
                "DOTNET_STARTUP_HOOKS is running: pid {0}, session {1}, at t+{2:F1} ms (this line "
                + "must predate the game's first log line)",
                Environment.ProcessId, _session, t0));
            // Flushed at every checkpoint, because a fatal CLR error bypasses every
            // catch and takes the buffered log with it.  An empty bootstrap.log
            // cannot distinguish "the crash was before the first line" from "the
            // crash was after the last one and the buffer was lost", and that
            // distinction is the whole of the current investigation.
            FlushLog();

            uint cfgFlags = SharedProtocol.CfgFrameSample;
            if (dryRun) cfgFlags |= SharedProtocol.CfgDryRun;
            if (fullTrace) cfgFlags |= SharedProtocol.CfgFullTrace;

            // --- the checks that need no game state -------------------------
            int passed = 0, failed = 0;
            CheckRuntimeConfig(ref passed, ref failed);
            AppendLog("INFO", "bootstrap: runtime config checked");
            FlushLog();

            // --- publish and hand over, BEFORE the field measurement --------
            //
            // ORDER MATTERS HERE AND IT IS NOT THE ORDER THE DESIGN FIRST USED.
            //
            // The identity checks are cheap and the field offset resolution is not;
            // more importantly, the native side is *blocked* on the signal below
            // and cannot install anything until it arrives.  Resolving offsets first
            // made a slow or throwing measurement a reason the hook never installed
            // at all -- which is exactly what the first live run did: the exception
            // unwound past the signal and the native side spent its full sixty
            // seconds waiting for a bootstrap that had already given up.
            //
            // So the target table -- the thing the native side needs to do its job
            // -- is published first, the signal is sent, and the offsets are
            // resolved afterwards.  The native side is released in milliseconds
            // instead of after the measurement, and a measurement that fails costs a
            // gap rather than the whole hook.
            // Published in stages, each flushed, because the crash that made this
            // necessary lands somewhere in here and the block writes are the only
            // thing between the last working checkpoint and the signal. Three
            // candidates and no way to tell them apart without splitting them:
            // the ordinary scalar writes, `PublishTargets` (which does the
            // marshalled string and hash copies), and `GetFrameCallbackAddress`,
            // which takes the native address of an `[UnmanagedCallersOnly]` method.
            AppendLog("INFO", "bootstrap: publishing the scalar fields");
            FlushLog();
            NativeMethods.WriteBlockU32(_viewPointer, GeneratedLayout.Off_structSize,
                                        (uint)GeneratedLayout.DrelaySharedSize);
            NativeMethods.WriteBlockU32(_viewPointer, GeneratedLayout.Off_cfgFlags, cfgFlags);
            NativeMethods.WriteBlockU32(_viewPointer, GeneratedLayout.Off_bootPid,
                                        (uint)Environment.ProcessId);
            NativeMethods.WriteBlockU32(_viewPointer, GeneratedLayout.Off_offsetCount, 0);
            NativeMethods.WriteBlockU32(_viewPointer, GeneratedLayout.Off_readSpecCount, 0);
            NativeMethods.WriteBlockU32(_viewPointer, GeneratedLayout.Off_stepCount, 0);
            NativeMethods.WriteBlockU32(_viewPointer, GeneratedLayout.Off_bootAssembliesReady, 1);
            AppendLog("INFO", "bootstrap: scalars published");
            FlushLog();

            AppendLog("INFO", "bootstrap: publishing the target table");
            FlushLog();
            NativeMethods.WriteBlockU32(_viewPointer, GeneratedLayout.Off_targetCount, PublishTargets());
            AppendLog("INFO", "bootstrap: target table published");
            FlushLog();

            // NOTHING IS PUBLISHED FOR THE FRAME PATH HERE, AND THAT IS A CHANGE WITH A
            // REASON.
            //
            // This is where the frame callback's `[UnmanagedCallersOnly]` stub address
            // used to be written, together with the `DRELAY_NO_FRAME_CB=1` control that
            // zeroed it.  Both are gone: the detour no longer calls into managed code at
            // all, so there is no address for this window to publish.  The switch is
            // removed rather than left accepted-and-ignored, because a control that no
            // longer controls anything is worse than no control -- it reads in a run's
            // environment as though it had done something, which is PHASE1-HANDOFF.md
            // trap 1 in a new costume.
            //
            // The frame read still needs ONE thing from this side, and it is not known
            // yet: the `Player` MethodTable, which the resolve callback publishes together
            // with the offsets, seconds from now.  Zeroed explicitly here rather than left
            // to the launcher's zeroing, for the reason the arm state is: `shared.block`
            // is a file a previous run may have written, and "the block starts zeroed" is
            // a property of a fresh file rather than of the protocol.  A stale handle from
            // a previous run would make the identity check pass for the wrong type.
            Marshal.WriteInt64(_viewPointer, GeneratedLayout.Off_managedPlayerTypeHandle, 0L);
            AppendLog("INFO", "bootstrap: no frame entry point to publish; the frame read is native");
            FlushLog();

            // The resolve callback's address, which the native side will call once
            // the runtime is fully up.  Taking an `[UnmanagedCallersOnly]` method's
            // address yields its already-emitted transition stub, so this does not
            // compile anything and is safe here -- which is exactly why the design
            // can move the compiling work behind a callback pointer this way.
            Marshal.WriteInt64(_viewPointer, GeneratedLayout.Off_managedResolveHook,
                               GetResolveCallbackAddress().ToInt64());
            AppendLog("INFO", "bootstrap: resolve callback published");
            FlushLog();

            // THE ARMING PROBE, PUBLISHED HERE BECAUSE IT IS THE ONE OTHER THING
            // THAT MUST EXIST BEFORE THE NATIVE SIDE STARTS ASKING.
            //
            // Same mechanics as the two above: the address of an
            // `[UnmanagedCallersOnly]` method is its already-emitted transition
            // stub, so taking it compiles nothing.  The native watcher thread calls
            // it repeatedly -- during the collection delay, the discovery window,
            // the live-check wait and its idle loop -- and it is what decides, once
            // the game has a world, whether the measured offsets describe that world
            // and therefore whether the frame read path may read through them.
            //
            // PUBLISHING IT BEFORE THE SIGNAL MATTERS.  The signal releases the
            // native side, and the first thing that thread does after the resolve
            // callback is wait out the collection delay -- calling this probe
            // throughout.  A probe published after the signal would be one the
            // watcher might look for before it exists.
            Marshal.WriteInt64(_viewPointer, GeneratedLayout.Off_managedProbeHook,
                               GetProbeCallbackAddress().ToInt64());
            // The gate starts closed and is opened by the probe alone.  Written
            // explicitly rather than left to the launcher's zeroing: `shared.block`
            // is a file that a previous run may have written, and "the block starts
            // zeroed" is a property of a fresh file rather than of the protocol.
            NativeMethods.WriteBlockU32(_viewPointer, GeneratedLayout.Off_managedArmState,
                                        SharedProtocol.ArmWaiting);
            AppendLog("INFO", "bootstrap: arming probe and resolve callback published");
            FlushLog();

            NativeMethods.WriteBlockU32(_viewPointer, GeneratedLayout.Off_magicConfig,
                                        SharedProtocol.MagicConfig);
            AppendLog("INFO", "bootstrap: magic written; creating the boot event");
            FlushLog();
            IntPtr bootEvent = GetOrCreateBootEvent();
            AppendLog("INFO", string.Format("bootstrap: boot event at 0x{0:X}; signalling",
                                            bootEvent.ToInt64()));
            FlushLog();
            NativeMethods.SetEvent(bootEvent);
            AppendLog("INFO", "bootstrap: signalled");
            FlushLog();

            AppendLog("INFO", string.Format(
                "bootstrap published: {0} target(s); signalling the native side now, offsets to "
                + "follow", GeneratedBuildTable.Targets.Length));

            // Signal here, before the measurement.  The report comes back on a
            // pooled thread; see the wait-and-report below for why it must not
            // block this one.
            NativeMethods.SetEvent(GetOrCreateBootEvent());
            // A DEDICATED THREAD, NOT THE THREAD POOL, AND THIS WAS THE CRASH.
            //
            // Localised by flush-checkpoints: the bootstrap published everything,
            // created the event, signalled it, and then died with
            // `Fatal error. Internal CLR error. (0x80131506)` before returning. The
            // only call between the successful signal and the return was
            // `ThreadPool.QueueUserWorkItem`.
            //
            // That call *initialises the thread pool*, and it was being made from
            // inside a startup hook -- a window in which the runtime is still
            // bringing itself up. Starting the pool there kills the process, and the
            // stack it produces names the startup hook rather than the pool, which is
            // why this took so long to find.
            //
            // A plain background thread does the same job without dragging the pool
            // into existence. (The original design blocked on this thread instead,
            // and the startup-hook thread does not tolerate a blocking P/Invoke
            // either -- so neither the blocking form nor the pooled form is
            // available, and a dedicated thread is what is left.)
            // No reporter thread.  Its whole purpose was to wait for the native
            // side's answer without blocking the startup-hook thread, and it is the
            // step that turned out to be impossible: starting a thread runs a method
            // body that has never been compiled, and compiling from a startup hook
            // faulters the process.  The native side now does the waiting itself and
            // calls back when it has an answer, which is strictly better -- it needs
            // no thread from the managed side at all.

            // STOP HERE.  THIS IS THE WHOLE POINT OF THE REDESIGN.
            //
            // The bootstrap has published the target table -- the only thing the
            // native side needs to install its detour -- and signalled.  Everything
            // else it used to do (loading an assembly, reflecting over it, building
            // dynamic methods, arriving at the frame callback address, starting a
            // reporter thread) has been moved to `ResolveOffsetsAfterStartup`, which
            // the native side calls from its own thread once the runtime is fully up.
            //
            // The reason is the root cause recorded in PHASE1-SCOPE.md §6.1: a
            // startup hook cannot JIT-compile anything.  Every one of those steps
            // forces a compilation of something not yet compiled, and the runtime
            // faulters the process -- while the stack blames the startup hook rather
            // than the compiler.  Reducing the bootstrap to "marshal some bytes, set
            // an event, return" is what keeps it inside the set of methods that are
            // already compiled by the time it runs.
            //
            // It is a real loss of ordering, and it is worth naming: the offsets are
            // no longer resolved before the detour is installed.  That is acceptable
            // because the offsets serve the *frame read*, which cannot happen until
            // the game draws a frame -- far later than the resolve callback runs.
            AppendLog("INFO", string.Format(
                "bootstrap finished: {0} target(s) published; signalling done. Offsets and the "
                + "field offsets are resolved by the native side after startup.",
                GeneratedBuildTable.Targets.Length));
            FlushLog();
        }

        /// <summary>
        /// Everything the bootstrap cannot do, done later.
        ///
        /// Called exactly once, by name, from the native side's
        /// `DrelayResolveOffsets`, after the detour is installed.  By then the
        /// runtime is fully initialised and this may reflect, allocate, load
        /// assemblies and compile methods freely -- all of which crashed the process
        /// when attempted from the startup hook.
        ///
        /// Public `static` with no arguments so the native side can reach it through
        /// a single `DrelayResolveFn` call, matching `ManagedArmingProbe.OnResolve`.
        /// </summary>
        public static void ResolveOffsetsAfterStartup()
        {
            int passed = 0, failed = 0, gaps = 0;
            int offsetCount = 0;

            // Checkpointed the same way the startup hook was, and for the same
            // reason: the native log stops at "invoking the managed resolve
            // callback", so the death is in here, and only flushed checkpoints can
            // say which line.
            AppendLog("INFO", "resolve[1] entered; about to read the environment");
            FlushLog();

            string gameDir = Environment.GetEnvironmentVariable(EnvGameDir);
            string assemblyPath = Environment.GetEnvironmentVariable(EnvAssembly);
            if (string.IsNullOrEmpty(assemblyPath))
            {
                assemblyPath = Path.Combine(gameDir ?? string.Empty, GeneratedBuildTable.AssemblyFileName);
            }
            AppendLog("INFO", "resolve[2] environment read; assembly path is " + assemblyPath);
            FlushLog();

            Assembly game = null;
            try
            {
                if (!File.Exists(assemblyPath))
                {
                    AppendLog("ERROR", "resolve[3] assembly not found at " + assemblyPath);
                    failed++;
                }
                else
                {
                    AppendLog("INFO", "resolve[3] loading the assembly");
                    FlushLog();
                    game = Assembly.LoadFrom(assemblyPath);
                    AppendLog("INFO", "resolve[4] assembly loaded; checking its identity");
                    FlushLog();
                    CheckGameAssembly(game, assemblyPath, ref passed, ref failed);
                    AppendLog("INFO", "resolve[5] identity checked");
                    FlushLog();
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "resolve[3] loading the assembly threw: " + ex);
                failed++;
            }

            if (game != null)
            {
                try
                {
                    AppendLog("INFO", "resolve[6] resolving field offsets");
                    FlushLog();
                    ResolveOffsets(game, ref offsetCount, ref passed, ref failed, ref gaps);
                    AppendLog("INFO", "resolve[7] offsets resolved; publishing the count");
                    FlushLog();
                    NativeMethods.WriteBlockU32(_viewPointer, GeneratedLayout.Off_offsetCount,
                                                (uint)offsetCount);
                }
                catch (Exception ex)
                {
                    AppendLog("ERROR", "resolve[6] offset resolution threw; the hook is installed, "
                                       + "so this costs the frame read and nothing else: " + ex);
                    gaps++;
                }
            }

            AppendLog("INFO", string.Format(
                "resolve[8] finished: {0} offset(s) resolved, {1} passed / {2} failed / {3} gap(s)",
                offsetCount, passed, failed, gaps));
            FlushLog();

            // LAST, AND DELIBERATELY SO.  Publishing the frame helper forces this
            // method to be JIT-compiled, and a failure here must not cost the offsets
            // that were just measured -- the frame read is Phase 1's, and this is
            // Phase 2's mechanism test, which is expendable by comparison.
            try
            {
                PublishFrameHelper();
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "publishing the managed frame helper threw; the detour will "
                                   + "not call one, and nothing else is affected: " + ex);
                FlushLog();
            }

            // The managed replacement, when the launcher asked for it.  Mutually
            // exclusive with the helper in practice -- the native side chooses one way
            // to patch `frameTick` -- and this one is opt-in because it replaces the
            // detour rather than adding to it.
            if (Environment.GetEnvironmentVariable("DRELAY_HOOK_FRAME_REPLACEMENT") == "1")
            {
                try
                {
                    PublishFrameReplacement();
                }
                catch (Exception ex)
                {
                    AppendLog("ERROR", "publishing the managed frame replacement threw; the native "
                                       + "side will fall back to its own detour: " + ex);
                    FlushLog();
                    Marshal.WriteInt64(_viewPointer, GeneratedLayout.Off_managedFrameReplacement, 0L);
                }

                // Deliverable 2.3's hook.  Published after the frame tick's, because the
                // comparison it makes is only meaningful once the sensor is reading the
                // world -- and because a collision observed before the first frame read
                // would be reported as "not tracked" for a reason that is about ordering
                // rather than about the model.
                try
                {
                    CollisionObservation.Publish();
                }
                catch (Exception ex)
                {
                    AppendLog("ERROR", "publishing the managed collision replacement threw; the "
                                       + "collision target will not be patched and no comparison "
                                       + "will be made: " + ex);
                    FlushLog();
                    Marshal.WriteInt64(_viewPointer, GeneratedLayout.Off_managedCollisionReplacement, 0L);
                }
            }

            // PHASE 3'S ACTUATOR, AND IT IS PUBLISHED ON EVERY RUN RATHER THAN ONLY WHEN
            // STEERING WAS ASKED FOR.  The address costs one JIT of a method that most runs
            // never enter, and in exchange the shared slot, the native dispatch by target
            // name and the trampoline handshake are exercised by every run -- instead of
            // first being exercised by the run whose mistake would move the player.  Whether
            // the `jmp` is aimed here at all is the launcher's decision, not this one.
            try
            {
                SteeringPilot.ResolveRotation(game);
                if (!SteeringPilot.Resolve(game))
                {
                    AppendLog("ERROR", "the steering pilot is NOT available: "
                                       + SteeringPilot.UnresolvedReason
                                       + " -- nothing else is affected, but no steering "
                                       + "experiment can run");
                }
                SteeringPilot.Publish();
                SteeringPilot.LogPlan();
                // THE WARM-UP RUNS AFTER PUBLISH AND BEFORE THE PATCH GOES LIVE. `Publish` is
                // called on the resolve callback, which is before the native side enables any
                // hook -- so a first-use failure here is a refusal, not a crash in a frame.
                SteeringPilot.Warm();
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "publishing the movement replacement threw; the movement "
                                   + "target will not be patched and the game is unaffected: " + ex);
                FlushLog();
                Marshal.WriteInt64(_viewPointer, GeneratedLayout.Off_managedMovementReplacement, 0L);
            }

            // PHASE 3'S ESCALATION TEST — the floor itself, made enterable on demand.
            //
            // `World.RequestEscape` was reported MISSING in the 2026-10-13 run for the one
            // reason a target can be missing without anything being wrong: nothing called it,
            // so the JIT never compiled it. That makes the floor the one part of the
            // deliverable that can be built, tested and observed *without* the decision that
            // eventually calls it -- so it is tested alone, before the solver exists to need
            // it. See EscapeTest.cs for what it does and what it refuses to do.
            try
            {
                if (!EscapeTest.Resolve(game))
                {
                    AppendLog("ERROR", "the escape test is not available: "
                                       + EscapeTest.UnresolvedReason
                                       + " -- nothing else is affected");
                }
                EscapeTest.LogPlan();
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "resolving the escape test threw; it will not run and nothing "
                                   + "else is affected: " + ex);
                FlushLog();
            }

            // Phase 2's bookkeeping rules, exercised where they run.  Last of all,
            // because nothing that follows depends on it -- and not optional, because a
            // rule that is only checked by hand is a rule that is only checked once.
            try
            {
                int sensorPassed;
                int sensorFailed;
                SensorSelfTest.Run(out sensorPassed, out sensorFailed);
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "the sensor self-test threw; its assertions are NOT proven in "
                                   + "this run: " + ex);
                FlushLog();
            }
        }

        // ===================================================================
        // The managed frame helper -- Phase 2's mechanism test.
        //
        // WHAT IT IS FOR.  Phase 1's frame detour calls MinHook's trampoline, which
        // `jmp`s into `DarzaWindow.Update`'s JIT-compiled body; that has run 209 897
        // times in an hour without a fault, so a plain call into managed code from
        // the game's thread demonstrably works.  What is NOT measured is a helper of
        // ours that allocates and calls other managed methods, and therefore
        // triggers collections *while it is on the stack* -- which is the case that
        // decides whether the GC can walk a stack whose frame below us belongs to
        // `Drelay.Hook.dll`.  PHASE2-SCOPE.md section 3 states the hypothesis and
        // section 4.6 the falsification test; this is the code that runs it.
        //
        // IT IS NOT `[UnmanagedCallersOnly]`, AND THAT IS THE ENTIRE POINT.  An
        // `[UnmanagedCallersOnly]` method compiles to a transition stub, and calling
        // one from the frame thread is a reverse P/Invoke, which coreclr refuses and
        // fail-fasts on (`ReversePInvokeBadTransition`, six dumps).  A plain method
        // has no such prologue, so the native side can call it the same way it
        // already calls the trampoline.  The signature is all-primitive on purpose:
        // the x64 managed convention for `int32 (int64, float)` is the platform
        // convention, so the C++ side can declare it exactly without decoding
        // anything out of metadata.
        // ===================================================================

        /// <summary>Body executions the helper has seen. Native comparison channel.</summary>
        private static long _frameHelperCalls;

        /// <summary>Bytes the helper has allocated, so "it allocated" is a number.</summary>
        private static long _frameHelperBytes;

        /// <summary>Full, blocking collections the helper forced. See the call site.</summary>
        private static long _frameHelperForcedCollections;

        /// <summary>Whether <see cref="FrameHelperProbe"/> forces full collections.</summary>
        private static int _frameHelperForceGc = -1;

        /// <summary>
        /// Whether the helper skips allocating and logging entirely.
        ///
        /// THE SECOND DISCRIMINATOR, AND IT DECIDES HOW MUCH OF PHASE 2 SURVIVES.
        /// "A managed helper called from the detour dies under a full compacting
        /// collection" is a finding; "because it allocates" and "because it is a
        /// managed frame under a native return address at all" are very different
        /// problems.  The first is avoidable -- `ProjDesc.GetPosition` need not
        /// allocate -- and the second is not.  With this set, the helper touches only a
        /// static buffer it creates once, formats nothing and logs nothing, so a crash
        /// here cannot be blamed on an allocation or on an unrooted reference in its
        /// frame.
        /// </summary>
        private static int _frameHelperNoAlloc = -1;

        private static bool FrameHelperSkipsAllocation()
        {
            if (_frameHelperNoAlloc < 0)
            {
                string value = Environment.GetEnvironmentVariable("DRELAY_FRAME_HELPER_NO_ALLOC");
                _frameHelperNoAlloc = (value == "1") ? 1 : 0;
            }
            return _frameHelperNoAlloc == 1;
        }

        private static byte[] _frameHelperStaticBuffer;

        // ===================================================================
        // The virtual-call rehearsal -- the SHAPE of what Phase 2 will do.
        //
        // Phase 2's sampler calls `ProjDesc.GetPosition(int, float, float, out float,
        // out float, int)`: a VIRTUAL method, with `out` parameters, on an object the
        // helper did not create.  That is a different shape from "call a static method
        // and return a number", and the differences are the ones that matter to a
        // collector -- a second managed frame, dispatch through a MethodTable, and two
        // stack slots written through by the callee.  Rehearsing it here, under the
        // gate's forced collections, is the cheapest way to find out that it is
        // survivable BEFORE the game is the thing being tested.
        //
        // The concrete type lives in this assembly rather than the host's, because a
        // startup hook cannot statically reference the game's assembly and reflection
        // would allocate.  What is being measured is the mechanism -- virtual dispatch
        // plus out parameters plus no allocation, on the detour's stack -- not which
        // assembly the type came from.
        // ===================================================================

        public abstract class TrajectoryProbe
        {
            public abstract void GetPosition(int time, float sin, float cos,
                                             out float x, out float y, int gameId);
        }

        public sealed class StraightTrajectoryProbe : TrajectoryProbe
        {
            public override void GetPosition(int time, float sin, float cos,
                                             out float x, out float y, int gameId)
            {
                // The same arithmetic a straight-line family does, and deliberately
                // arithmetic only: an allocation here would destroy the property the
                // rehearsal exists to test.
                float t = time * 0.001f;
                float speed = 120.0f + (gameId & 7);
                x = sin * speed * t;
                y = cos * speed * t;
            }
        }

        private static TrajectoryProbe _frameHelperTrajectory;
        private static float _frameHelperFold;
        private static int _frameHelperVirtualCount;
        private static int _frameHelperVirtualFlag = -1;
        private static int _frameHelperVirtualRepeat = -1;

        /// <summary>
        /// How many virtual calls the helper makes per frame.
        ///
        /// THE WIDTH OF THE WINDOW IS THE EXPERIMENT.  A forced collection lands inside
        /// the patched path only if the thread happens to be there when it is
        /// suspended, so a run whose managed region occupies 0.3 % of the frame will
        /// see roughly one hit in 400 collections -- which is enough to be a bug and
        /// not enough to be a measurement.  Repeating the call widens the region to a
        /// known fraction of the frame, so "the witness survived" and "no collection
        /// ever landed there" stop being the same result.  That distinction is the
        /// whole reason this switch exists.
        /// </summary>
        private static int FrameHelperVirtualRepeat()
        {
            if (_frameHelperVirtualRepeat < 0)
            {
                string value = Environment.GetEnvironmentVariable("DRELAY_FRAME_HELPER_VIRTUAL_REPEAT");
                _frameHelperVirtualRepeat =
                    (!string.IsNullOrEmpty(value) && int.TryParse(value, out int parsed) && parsed > 0)
                        ? parsed : 1;
            }
            return _frameHelperVirtualRepeat;
        }

        private static bool FrameHelperUsesVirtualCall()
        {
            if (_frameHelperVirtualFlag < 0)
            {
                string value = Environment.GetEnvironmentVariable("DRELAY_FRAME_HELPER_VIRTUAL");
                _frameHelperVirtualFlag = (value == "1") ? 1 : 0;
            }
            return _frameHelperVirtualFlag == 1;
        }

        /// <summary>
        /// Whether the helper forces a full compacting collection.
        ///
        /// A PLAIN `DRELAY_` NAME ON PURPOSE.  The launcher deletes every inherited
        /// `DRELAY_HOOK_*` variable before it adds its own, so a switch in that
        /// namespace could never be set from the operator's shell or from
        /// `Test-Injection.ps1` -- PHASE1-HANDOFF.md section 4 trap 1, which has cost
        /// this project three rounds.  `DRELAY_` is the convention for anything the
        /// caller sets.
        ///
        /// IT EXISTS BECAUSE THE TWO REGIMES ARE DIFFERENT MEASUREMENTS, and the first
        /// run of this probe showed they have different answers.  Ordinary gen0
        /// pressure and a forced full compacting collection are not the same test, and
        /// "which one kills it" decides what Phase 2 may do.
        /// </summary>
        private static bool FrameHelperForcesGc()
        {
            if (_frameHelperForceGc < 0)
            {
                string value = Environment.GetEnvironmentVariable("DRELAY_FRAME_HELPER_FORCE_GC");
                _frameHelperForceGc = (value == "1") ? 1 : 0;
            }
            return _frameHelperForceGc == 1;
        }

        /// <summary>
        /// The helper the native frame detour calls.  Returns a value the native side
        /// can predict from its own arguments, so a correct return proves the
        /// arguments crossed the boundary intact rather than merely that a call
        /// happened.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int FrameHelperProbe(long frameIndex, float total)
        {
            // ALLOCATE, AND KEEP IT LIVE.  A collection must be able to happen while
            // this method is on the stack; if the array were unused the JIT could
            // drop it and the run would prove nothing.  `Checksum` is the managed
            // call that keeps it live -- and it is also the proof that the helper is
            // not a leaf, which is the shape a real sensor would have.
            byte[] ballast;
            if (FrameHelperSkipsAllocation())
            {
                // One allocation, on the first call, into a static: after that this
                // method allocates nothing and its frame needs no reference tracking
                // for the buffer.
                if (_frameHelperStaticBuffer == null)
                {
                    _frameHelperStaticBuffer = new byte[65536];
                }
                ballast = _frameHelperStaticBuffer;
            }
            else
            {
                ballast = new byte[65536];
            }
            ballast[0] = unchecked((byte)frameIndex);
            ballast[ballast.Length - 1] = unchecked((byte)(frameIndex >> 8));
            int checksum = FrameHelperChecksum(ballast, unchecked((int)(frameIndex & 0xFFFF)));

            _frameHelperCalls = _frameHelperCalls + 1;
            _frameHelperBytes = _frameHelperBytes + (FrameHelperSkipsAllocation() ? 0 : ballast.Length);

            // THE REHEARSAL, ON THE DETOUR'S STACK, WITH NO ALLOCATION BEHIND IT.
            if (FrameHelperUsesVirtualCall())
            {
                float px = 0f;
                float py = 0f;
                TrajectoryProbe trajectory = _frameHelperTrajectory;
                int repeat = FrameHelperVirtualRepeat();
                float fold = 0f;
                for (int r = 0; r < repeat; r++)
                {
                    trajectory.GetPosition(unchecked((int)frameIndex) + r, 0.5f, 0.866f, out px, out py,
                                           unchecked((int)(frameIndex & 0xFFFF)));
                    fold = fold + px - py;
                }
                _frameHelperVirtualCount = _frameHelperVirtualCount + 1;
                // Into a static, so the loop cannot be optimised away and the return
                // value stays the predictable one the native side checks.
                _frameHelperFold = fold;
                if (_frameHelperVirtualCount == 1)
                {
                    AppendLog("INFO", "frame helper virtual call: x=" + px.ToString("R", CultureInfo.InvariantCulture)
                                      + " y=" + py.ToString("R", CultureInfo.InvariantCulture)
                                      + " after " + _frameHelperVirtualCount + " call(s), repeat " + repeat);
                    FlushLog();
                }
            }

            // A FULL, BLOCKING, COMPACTING COLLECTION WHILE THIS METHOD IS ON THE
            // STACK, AND WITHOUT IT THE TEST WOULD MEASURE NOTHING.
            //
            // The first version of this probe relied on gen0 pressure alone, and that
            // is not enough: the host's witness object is allocated once and read
            // every iteration, so ordinary collections promote it long before the
            // detour is patched -- and after that, gen0 collections never revisit a
            // promoted object, so "the witness survived" would have been true whether
            // or not the runtime can walk the frames outside this detour.  A forced
            // collection of every generation makes the question decidable, and it is
            // what MANAGED-ENTRY-HANDOFF.md section 4 step 3 asks for: a variant that
            // survives five minutes without a GC has measured nothing.
            if (FrameHelperForcesGc() && (_frameHelperCalls % 64) == 0)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                _frameHelperForcedCollections = _frameHelperForcedCollections + 1;
            }

            // The log is the mechanism's own evidence, and it doubles as proof that
            // managed calls (formatting, a lock, a list) work inside the helper.  One
            // line per 64 calls: enough to place the numbers, few enough not to swamp
            // the ring or the frame budget.
            if (!FrameHelperSkipsAllocation() && (_frameHelperCalls % 64) == 1)
            {
                AppendLog("INFO", "frame helper: call " + _frameHelperCalls + " of frameIndex "
                                  + frameIndex + ", total " + total.ToString("R", CultureInfo.InvariantCulture)
                                  + ", " + _frameHelperBytes + " byte(s) allocated, checksum " + checksum
                                  + ", gen0 collections " + GC.CollectionCount(0)
                                  + ", forced full collections " + _frameHelperForcedCollections);
                FlushLog();
            }

            // THE PREDICTABLE RETURN.  The native side computes the same expression
            // from the arguments it passed; a mismatch means the first argument did
            // not arrive, and a NaN/Infinity/garbage float is reported by the sign
            // bit rather than silently producing a plausible number.
            bool sane = !float.IsNaN(total) && !float.IsInfinity(total) && total >= 0f;
            long mixed = (frameIndex ^ 0x5A5A5A5AL) & 0x7FFFFFFFL;
            return sane ? (int)mixed : -(int)mixed;
        }

        /// <summary>
        /// A managed call that keeps the helper's allocation live.  Stepped rather
        /// than exhaustive: this runs inside a game frame on a path whose budget is
        /// measured, and the property under test is that managed calls work at all,
        /// not the checksum's quality.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int FrameHelperChecksum(byte[] data, int seed)
        {
            int h = seed;
            for (int i = 0; i < data.Length; i += 257)
            {
                h = unchecked((h * 31) + data[i]);
            }
            return h;
        }

        // ===================================================================
        // THE MANAGED REPLACEMENT -- MinHook's `jmp` lands here, and the whole
        // managed chain stays walkable.
        //
        // WHAT IS DIFFERENT FROM THE HELPER ABOVE, IN ONE SENTENCE.  There, a native
        // detour called into managed code and left a native return address inside the
        // managed stack; here, the game's own `call` lands on the target's entry, the
        // entry `jmp`s into this method, and everything from the game's caller down is
        // a managed call.  Nothing interrupts the chain, so the runtime can unwind it,
        // and the discipline the helper route needed -- pre-compile everything, allocate
        // nothing -- is not carrying the safety any more.
        //
        // THE WAY BACK INTO THE ORIGINAL IS A *MANAGED* FUNCTION POINTER, AND THAT IS
        // THE POINT OF THE WHOLE EXERCISE.  `delegate* unmanaged` here would be a
        // managed-to-native transition, which would put a transition frame and a
        // preemptive thread state under the original body -- reintroducing exactly the
        // interruption this design exists to remove.  `delegate* managed` is emitted as
        // a plain `call` with no transition at all, and because MinHook's trampoline is
        // the original prologue followed by a jump into the body, the stack at the
        // body's first instruction is indistinguishable from a direct managed call.
        // ===================================================================

        private static long _frameReplacementCalls;
        private static int _frameReplacementLogged;
        private static int _frameReplacementAllocFlag = -1;

        /// <summary>One line about the first exception the frame path swallowed, and never a second.</summary>
        private static int _frameSensorFailureLogged;

        /// <summary>One line about a report that threw, and never a second.</summary>
        private static int _reportFailureLogged;

        /// <summary>How often the phase's numbers are published, in frames.</summary>
        private const int ReportEveryFrames = 300;

        /// <summary>
        /// Frames until the next publication.
        ///
        /// A COUNTDOWN RATHER THAN A MODULO, AND THE REASON IS A MEASUREMENT.  The first
        /// version fired when `_frameReplacementCalls % 300 == 0`, and in the
        /// fifteen-minute session of 2026-10-12 the report stopped after three lines --
        /// frames 300, 601 and 901 of 54 600 -- while the sensor's own lines, which use a
        /// `>=` test against a counter nothing else touches, kept coming every 150 frames
        /// right to the end.  Whatever reset or misaligned that counter, a cadence that
        /// depends on another counter's exact value can stop without saying so; one that
        /// counts its own frames down cannot.
        /// </summary>
        private static int _framesUntilReport = ReportEveryFrames;

        /// <summary>Whether the replacement allocates on every frame.</summary>
        private static bool FrameReplacementAllocates()
        {
            if (_frameReplacementAllocFlag < 0)
            {
                string value = Environment.GetEnvironmentVariable("DRELAY_FRAME_REPLACEMENT_ALLOC");
                _frameReplacementAllocFlag = (value == "1") ? 1 : 0;
            }
            return _frameReplacementAllocFlag == 1;
        }

        /// <summary>
        /// The replacement itself: `DarzaWindow.Update`'s shape, as a static method
        /// taking the receiver explicitly.  On x64 the argument registers line up with
        /// the instance method's own ABI -- `this` in RCX, the float in XMM1 -- so the
        /// `jmp` from the target's entry arrives with the arguments where this method
        /// expects them.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FrameTickReplacement(IntPtr self, float total)
        {
            // The sensor work is allowed to fail; the game's frame update is not.
            try
            {
                FrameReplacementSensor(total);
            }
            catch (Exception ex)
            {
                // Swallowed deliberately: an exception escaping into the game's frame
                // loop would be far worse than a missed observation.  `catch` costs
                // nothing until something is thrown.
                //
                // LOGGED ONCE, THOUGH, AND THAT IS THE CORRECTION.  A swallow with no
                // record is how the 2026-10-12 evening session lost the phase's cost
                // numbers: the report stopped after three lines and every subsequent
                // frame threw inside this block with nothing written down, so the only
                // evidence was an absence -- the same shape as the exception log that was
                // keyed on the wrong condition in PHASE2-SCOPE.md section 4.12.  The
                // first failure names itself; the rest are counted, not printed, because
                // this is the frame path.
                if (System.Threading.Interlocked.Exchange(ref _frameSensorFailureLogged, 1) == 0)
                {
                    AppendLog("ERROR", "the frame replacement's sensor path threw; the game is "
                                       + "unaffected and continues into the original method, but "
                                       + "the sensor is not running to completion on this frame: "
                                       + ex.GetType().Name + ": " + ex.Message
                                       + " | " + FirstFrames(ex));
                    FlushLog();
                }
            }

            FrameReplacementCallOriginal(self, total);
        }

        /// <summary>
        /// What a Phase 2 sensor would do here.  Allocates only when asked, so the
        /// mechanism can be measured with and without that variable.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FrameReplacementSensor(float total)
        {
            _frameReplacementCalls = _frameReplacementCalls + 1;

            // THE ESCALATION TEST, AND IT IS OUTSIDE THE SENSOR'S SWITCH ON PURPOSE.  It is an
            // actuator test rather than a read: it calls `World.RequestEscape()` once, after a
            // delay, and it must be runnable without paying for the sensor's own read path --
            // otherwise the one experiment that establishes whether the escape floor can be
            // reached at all would depend on the cost work Phase 2 waived. `EscapeTest.OnFrame`
            // returns immediately when the switch is unset, which is every run but its own.
            EscapeTest.OnFrame();

            // THE SENSOR, WHEN ASKED FOR.  This is the read that Phase 2's numbers come
            // from: the live-object dictionary, filtered to projectiles, turned into the
            // threat set.  Gated because it is the expensive part and every other run --
            // including the mechanism test -- should not pay for it.
            if (_frameSensorEnabled)
            {
                SensorRead.OnFrame();

                // A TRACE, WHEN ASKED FOR, AND IT RIDES ON THE SENSOR RATHER THAN REPEATING IT.
                // The recorder needs the same live-object list the sensor has just built, and
                // walking the game's drawing layer a second time would double the frame cost of a
                // run whose whole purpose is to be an ordinary run. So it runs immediately after
                // the sensor, on the sensor's own lists, inside the sensor's own switch: a trace
                // with no sensor has no projectiles to record.
                TraceRecorder.OnFrame(SensorRead.LiveObjects, SensorRead.LiveKeys,
                                      (int)SensorRead.Frame, TrajectorySampler.LastFrameSeconds());

                // DELIVERABLE 3.3'S DECIDING END, AND IT RUNS HERE FOR THE REASON SECTION 4 OF
                // PHASE3-HANDOFF.md GIVES: the frame tick is where the world is freshest and where
                // the snapshot belongs. It borrows the sensor's own projectile list for the same
                // reason the recorder does -- walking the game's drawing layer twice would double
                // the frame cost of a run whose purpose is to be an ordinary run.
                //
                // IT RUNS BEFORE `ReleaseTrajectories` AND THEREFORE RENTS ITS OWN PATHS OUT OF THE
                // SAME POOL. The pool only grows to the number of bullets in a frame, once.
                DodgeAdapter.OnFrame(SensorRead.LiveObjects, SensorRead.LiveKeys,
                                     (int)SensorRead.Frame);

                TrajectorySampler.ReleaseTrajectories();

                // THE PHASE'S FOUR NUMBERS, PERIODICALLY RATHER THAN AT AN END THIS CODE
                // CANNOT SEE.  The replacement does not know when the recording window
                // closes -- the native side owns that -- so the report is emitted every few
                // hundred frames and the last one before the process exits is the run's.
                // Emitting it only at the end would mean emitting it never.
                if (--_framesUntilReport <= 0)
                {
                    _framesUntilReport = ReportEveryFrames;
                    try
                    {
                        Phase2Report.Log();
                    }
                    catch (Exception ex)
                    {
                        // THE REASON THIS CATCH IS HERE.  `FrameTickReplacement` swallows
                        // everything, so without this a report that threw would look
                        // exactly like a report that was never due -- which is what the
                        // 2026-10-12 evening run looked like: the published cost
                        // percentiles covered 901 frames of 54 600, and the verdict quoted
                        // them as the phase's cost.  A number whose provenance is a
                        // silence is worse than no number.
                        if (System.Threading.Interlocked.Exchange(ref _reportFailureLogged, 1) == 0)
                        {
                            AppendLog("ERROR", "the PHASE 2 NUMBERS report threw, so this run's "
                                               + "published numbers are stale or missing: "
                                               + ex.GetType().Name + ": " + ex.Message
                                               + " | " + FirstFrames(ex));
                            FlushLog();
                        }
                    }
                }
            }

            if (FrameReplacementAllocates())
            {
                byte[] ballast = new byte[65536];
                ballast[0] = unchecked((byte)_frameReplacementCalls);
                _frameReplacementFold = ballast[0];
            }

            if (_frameReplacementTrajectory == null)
            {
                _frameReplacementTrajectory = new StraightTrajectoryProbe();
            }
            float px;
            float py;
            _frameReplacementTrajectory.GetPosition(unchecked((int)_frameReplacementCalls), 0.5f, 0.866f,
                                                    out px, out py, 0);
            _frameReplacementFold = px - py;

            // ONE LINE, ONCE, AND LOGGING HERE IS ALLOWED.  The replacement's caller is
            // managed, so the stack the collector walks is ordinary; this is the
            // positive evidence that the replacement was entered at all, which the
            // host's own counters cannot give -- a hook that never took effect would
            // leave those counters agreeing just as happily.
            if (_frameReplacementLogged == 0)
            {
                _frameReplacementLogged = 1;
                AppendLog("INFO", "MANAGED REPLACEMENT entered (call 1): total="
                                  + total.ToString("R", CultureInfo.InvariantCulture));
                FlushLog();
            }
        }

        private static float _frameReplacementFold;
        private static TrajectoryProbe _frameReplacementTrajectory;

        /// <summary>
        /// Whether the replacement also runs the sensor's read path.
        ///
        /// `DRELAY_FRAME_SENSOR=1`, a plain `DRELAY_` name so the launcher's own filter
        /// cannot eat it (PHASE1-HANDOFF.md section 4 trap 1).  Off by default: the read
        /// path reflects over the live-object dictionary every frame, which is exactly
        /// the cost Phase 2 has to measure and exactly what no other run should pay.
        ///
        /// **AND THE TRACE TURNS IT ON FOR ITSELF**, which is the difference between a recording
        /// run and a run that writes an empty file. The recorder's input is this read path's
        /// projectile list; an operator who set `DRELAY_TRACE=1` and forgot `DRELAY_FRAME_SENSOR=1`
        /// would get a valid, empty trace -- and "the model predicted nothing" and "the recorder
        /// saw nothing" are the same four bytes on disk. A dependency that can be satisfied
        /// automatically is one the operator cannot get wrong.
        /// </summary>
        private static bool _frameSensorEnabled =
            Environment.GetEnvironmentVariable("DRELAY_FRAME_SENSOR") == "1"
            || Environment.GetEnvironmentVariable("DRELAY_TRACE") == "1"
            // AND THE DODGE TURNS IT ON FOR ITSELF, for exactly the reason above: the adapter's
            // input is this read path's projectile list, so `DRELAY_DODGE=1` without the sensor is
            // a solver that decides about an empty world and reports `NoThreat` on every frame --
            // which is an empty recording's twin, and that one already cost a session (defect 3.8).
            || Environment.GetEnvironmentVariable("DRELAY_DODGE") == "1";

        /// <summary>
        /// Continue into the real method.  Separate and marked NoInlining so it can be
        /// prepared explicitly before the patch goes live -- see PublishFrameReplacement.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FrameReplacementCallOriginal(IntPtr self, float total)
        {
            long trampoline = Marshal.ReadInt64(_viewPointer, GeneratedLayout.Off_managedFrameTrampoline);
            if (trampoline == 0)
            {
                // Must not happen: the native side publishes the trampoline before it
                // enables the hook.  Returning would stop the game's frame update for
                // ever, so it is logged loudly rather than passed over.
                if (System.Threading.Interlocked.Exchange(ref _frameReplacementLogged, 2) != 2)
                {
                    AppendLog("ERROR", "the managed replacement ran with no trampoline published; the "
                                       + "original frame tick cannot be called");
                    FlushLog();
                }
                return;
            }

            unsafe
            {
                ((delegate* managed<IntPtr, float, void>)(IntPtr)trampoline)(self, total);
            }
        }

        /// <summary>
        /// Prepare and publish the replacement, and warm everything it calls.
        ///
        /// WARMING MATTERS EVEN HERE, and for the same reason it mattered for the
        /// helper: a method compiled on its first call is compiled from wherever that
        /// call comes from, and here that is inside a patched game frame.  The chain is
        /// managed now, so it is not the hazard it was, but the rule is cheap to obey
        /// and a compile in the frame path is a stall in the frame path.
        /// </summary>
        public static void PublishFrameReplacement()
        {
            MethodInfo method = typeof(HookEntry).GetMethod(
                nameof(FrameTickReplacement), BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                AppendLog("ERROR", "the frame replacement method was not found by name; nothing published");
                FlushLog();
                return;
            }

            PreparePrivate("FrameReplacementSensor");
            PreparePrivate("FrameReplacementCallOriginal");
            PreparePrivate("FrameHelperChecksum");
            // The escape test is called from the frame replacement's sensor path, so its
            // frame-time method is warmed here for the same reason everything else on that
            // path is: a method compiled on its first call is compiled from inside a patched
            // game frame. `Resolve` deliberately is not warmed -- it runs on the resolve
            // callback, where a JIT is ordinary work.
            PrepareStatic(typeof(EscapeTest), "OnFrame");
            // The dodge's frame path, for the same reason: a decision compiled on its first call is
            // compiled from inside a patched game frame. `DodgeAdapter.Warm` names the callees --
            // including `DodgeSolver.Decide`, which is an INSTANCE method and therefore invisible to
            // `PrepareStatic` above. It was written as `PrepareStatic(typeof(DodgeSolver), "Decide")`
            // for one revision and prepared nothing at all: `PrepareStatic` searches for
            // `BindingFlags.Static`, finds no such method, and returns quietly. A warm-up that
            // silently prepares nothing is worse than none, because it is believed.
            DodgeAdapter.Warm();
            MethodInfo dispatch = typeof(StraightTrajectoryProbe).GetMethod(
                "GetPosition", BindingFlags.Public | BindingFlags.Instance);
            if (dispatch != null)
            {
                RuntimeHelpers.PrepareMethod(dispatch.MethodHandle);
            }
            RuntimeHelpers.PrepareMethod(method.MethodHandle);

            // The sensor's own path is exercised once, on this thread, so nothing in it
            // is compiled for the first time inside a game frame.
            _frameReplacementTrajectory = new StraightTrajectoryProbe();
            FrameReplacementSensor(0.0f);
            _frameReplacementCalls = 0;
            _frameReplacementLogged = 0;
            // The warm-up call above is a real call to the sensor path, so it spends one of
            // the report's frames; the countdown is reset with the counters it belongs to.
            _framesUntilReport = ReportEveryFrames;

            IntPtr entry = method.MethodHandle.GetFunctionPointer();
            Marshal.WriteInt64(_viewPointer, GeneratedLayout.Off_managedFrameReplacement, entry.ToInt64());
            AppendLog("INFO", "managed frame replacement published at 0x" + entry.ToInt64().ToString("X")
                              + "; the native side will jump straight into it");
            FlushLog();
        }

        private static void PreparePrivate(string name)
        {
            MethodInfo m = typeof(HookEntry).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            if (m != null)
            {
                RuntimeHelpers.PrepareMethod(m.MethodHandle);
            }
        }

        /// <summary>
        /// The same, for a frame-path method that lives on another class. `PreparePrivate`
        /// only ever looks at `HookEntry`, so a helper's public static entry point needs its
        /// own lookup rather than a widened version of that one -- widening it would let a
        /// typo in a name silently prepare nothing on a class it was not meant to search.
        /// </summary>
        private static void PrepareStatic(Type owner, string name)
        {
            MethodInfo m = owner.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic
                                                 | BindingFlags.Static);
            if (m != null)
            {
                RuntimeHelpers.PrepareMethod(m.MethodHandle);
            }
        }

        /// <summary>
        /// JIT the helper and publish its entry point, so the native detour can call
        /// it.  Runs on the resolve callback and never during the startup hook
        /// itself: a startup hook cannot compile anything, which is why the whole
        /// resolve-callback mechanism exists.
        /// </summary>
        public static void PublishFrameHelper()
        {
            MethodInfo method = typeof(HookEntry).GetMethod(
                nameof(FrameHelperProbe), BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                AppendLog("ERROR", "the frame helper method was not found by name; nothing published");
                FlushLog();
                return;
            }

            // PrepareMethod first: `GetFunctionPointer` on a method that has never
            // been compiled returns a prestub, and the detour would then be calling
            // something whose meaning changes under it.
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            IntPtr entry = method.MethodHandle.GetFunctionPointer();

            // A CALLER MUST BE ABLE TO TELL THIS FROM A TRANSITION STUB.  The address
            // published here belongs to a plain method whose entry the runtime will
            // execute directly; if the address ever resolved to an
            // `[UnmanagedCallersOnly]` stub, the detour's call would be the reverse
            // P/Invoke that killed six runs.  The attribute is checked rather than
            // assumed, because the failure it prevents is a fail-fast with no
            // in-process handler.
            if (method.GetCustomAttribute(typeof(System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute)) != null)
            {
                AppendLog("ERROR", "the frame helper is marked [UnmanagedCallersOnly]; refusing to "
                                   + "publish it, because the detour's call would be a reverse "
                                   + "P/Invoke from a thread inside managed code");
                FlushLog();
                Marshal.WriteInt64(_viewPointer, GeneratedLayout.Off_managedFrameHelper, 0L);
                return;
            }

            Marshal.WriteInt64(_viewPointer, GeneratedLayout.Off_managedFrameHelper, entry.ToInt64());
            AppendLog("INFO", "managed frame helper published at 0x" + entry.ToInt64().ToString("X"));

            // THE REHEARSAL'S OBJECT IS CREATED HERE, NOT IN THE HELPER, and that is
            // what makes the rehearsal about the detour's stack rather than about an
            // allocation.  This runs on the resolve callback, before the patch goes
            // live, so by the time the helper is called every frame its call path --
            // dispatch, `out` parameters, arithmetic -- allocates nothing at all.
            //
            // AND THE WHOLE CALL PATH IS WARMED HERE, WHICH IS NOT OPTIONAL.  A
            // managed method that has never been compiled is compiled the first time it
            // is called, and JIT compilation allocates, takes the runtime's JIT lock and
            // can collect -- all of it from inside the frame detour, which is the worst
            // possible place for it.  Phase 1 already learned this: its own comment
            // records that the detour "needed both a pre-compile and an explicit probe
            // call here".  `PrepareMethod` covers the helper, but NOT the methods the
            // helper calls, so the dispatch is also exercised once, on this thread,
            // before anything is patched.  The first version of the rehearsal skipped
            // that and produced an unexplained corruption of the host's own objects on
            // the very first helper call -- see PHASE2-SCOPE.md section 4.3.
            // THE HELPER'S OWN CALLEES ARE PREPARED WHETHER OR NOT THE REHEARSAL IS ON,
            // because the first thing that goes wrong without this is not the virtual
            // dispatch: `FrameHelperChecksum` is called on the helper's very first
            // frame, and compiling it there is compilation from the detour's stack.
            MethodInfo checksum = typeof(HookEntry).GetMethod(
                "FrameHelperChecksum", BindingFlags.NonPublic | BindingFlags.Static);
            if (checksum != null)
            {
                RuntimeHelpers.PrepareMethod(checksum.MethodHandle);
            }

            if (FrameHelperUsesVirtualCall() && _frameHelperTrajectory == null)
            {
                _frameHelperTrajectory = new StraightTrajectoryProbe();

                MethodInfo dispatch = typeof(StraightTrajectoryProbe).GetMethod(
                    "GetPosition", BindingFlags.Public | BindingFlags.Instance);
                if (dispatch != null)
                {
                    RuntimeHelpers.PrepareMethod(dispatch.MethodHandle);
                }

                float warmX;
                float warmY;
                _frameHelperTrajectory.GetPosition(0, 0.5f, 0.866f, out warmX, out warmY, 0);
                AppendLog("INFO", "frame helper virtual-call rehearsal armed: "
                                  + _frameHelperTrajectory.GetType().Name + " created and warmed before "
                                  + "the patch (x=" + warmX.ToString("R", CultureInfo.InvariantCulture) + ")");
            }
            FlushLog();
        }

        private static IntPtr _bootEvent = IntPtr.Zero;

        /// <summary>
        /// The mapped block's address, exposed so the resolve callback can write a
        /// sentinel into it before doing anything else.
        /// </summary>
        public static IntPtr SharedBlockPointer => _viewPointer;

        private static IntPtr GetOrCreateBootEvent()
        {
            if (_bootEvent == IntPtr.Zero)
            {
                _bootEvent = NativeMethods.CreateEvent(IntPtr.Zero, true, false,
                    SharedProtocol.BootReadyEventName(_session));
            }
            return _bootEvent;
        }

        // -------------------------------------------------------------------
        // The shared block.

        private static void OpenSharedBlock()
        {
            string blockFile = Environment.GetEnvironmentVariable(EnvBlockFile);
            if (string.IsNullOrEmpty(blockFile))
            {
                throw new InvalidOperationException(
                    EnvBlockFile + " is not set; the launcher must name the file that backs the block");
            }

            var handle = NativeMethods.CreateFile(
                blockFile, NativeMethods.GenericRead | NativeMethods.GenericWrite,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero, NativeMethods.OpenExisting, NativeMethods.FileAttributeNormal, IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                throw new InvalidOperationException(
                    "could not open the block file " + blockFile +
                    " (win32 error " + Marshal.GetLastWin32Error() + ")");
            }

            // A named mapping over the file's handle. The name is harmless here --
            // the file is what makes the mapping findable across processes -- and
            // passing one keeps a debugger's handle view readable.
            var mapping = NativeMethods.CreateFileMapping(
                handle, IntPtr.Zero, NativeMethods.PageReadWrite, 0,
                (uint)GeneratedLayout.DrelaySharedSize, null);
            if (mapping == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "could not map the block file " + blockFile +
                    " (win32 error " + Marshal.GetLastWin32Error() + ")");
            }

            _viewPointer = NativeMethods.MapViewOfFile(mapping, NativeMethods.FileMapAllAccess, 0, 0, UIntPtr.Zero);
            if (_viewPointer == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "could not map a view of the block (win32 error " + Marshal.GetLastWin32Error() + ")");
            }
            ManagedArmingProbe.SetSharedBlock(_viewPointer);

            // Read through `Marshal`, never through a pointer.  See ReadBlockU32.
            uint magic = ReadBlockU32(GeneratedLayout.Off_magic);
            uint version = ReadBlockU32(GeneratedLayout.Off_version);
            uint size = ReadBlockU32(GeneratedLayout.Off_structSize);

            if (magic != SharedProtocol.MagicBoot && magic != SharedProtocol.MagicConfig)
            {
                throw new InvalidOperationException(string.Format(
                    "shared block magic is 0x{0:X8}; expected 0x{1:X8} (the launcher writes it "
                    + "before ResumeThread)", magic, SharedProtocol.MagicBoot));
            }
            if (version != SharedProtocol.Version)
            {
                throw new InvalidOperationException(string.Format(
                    "shared block version is {0}; this bootstrap speaks {1}",
                    version, SharedProtocol.Version));
            }
            if (size != 0 && size != (uint)GeneratedLayout.DrelaySharedSize)
            {
                throw new InvalidOperationException(string.Format(
                    "shared block size is {0}; this bootstrap expects {1}",
                    size, GeneratedLayout.DrelaySharedSize));
            }
        }

        private static uint PublishTargets()
        {
            // THE PINNED TABLE, PLUS ANY EXPLICIT TEST TARGETS.  The pinned four come
            // from `known-build.json` at build time and describe *the game*; nothing
            // in this process can add to them, which is deliberate -- the Phase 0 gate
            // checks exactly those four IL hashes and a table that could be extended
            // at runtime would make the gate's claim ("this is the build we pinned")
            // untrue.  A synthetic host is not that build and has no entry there, so
            // without a second route the injection test can exercise the arming gate
            // against a host but never the patch, which is the state its `GAP 9c`
            // records.
            //
            // `DRELAY_HOOK_TEST_TARGETS` is that second route, and it is test-only by
            // construction: the launcher sets it only for `--test-target` (it deletes
            // every inherited `DRELAY_HOOK_*` first, so an operator's shell cannot
            // reach the hook with it), and every entry it contributes is logged as a
            // test target so no reader can mistake such a run for a run of the pinned
            // build.
            var specs = new List<GeneratedBuildTable.TargetSpec>(GeneratedBuildTable.Targets);
            List<GeneratedBuildTable.TargetSpec> testSpecs = ReadTestTargets();
            if (testSpecs.Count > 0)
            {
                specs.AddRange(testSpecs);
            }

            uint count = 0;
            for (int i = 0; i < specs.Count && i < SharedProtocol.MaxTargets; i++)
            {
                var spec = specs[i];
                byte[] hash = HexToBytes(spec.IlHash);

                // Written field by field through `Marshal`, at offsets the
                // generator computed from the native header, and now at the
                // *field* offsets it computed rather than at numbers restated
                // here.  A generic `Marshal.StructureToPtr` would be shorter and
                // is the wrong tool for the reason this comment always gave: it
                // would depend on this file's view of the struct matching the
                // native one.  But restating the numbers had exactly that
                // dependency and none of the safety -- `hash` was written at 40
                // against a native offset of 34, so every target's IL hash was
                // published with six zero bytes in front of it, `found` was
                // written at 68 against a native 66, and the result was that no
                // target could ever match and every target read back as FOUND.
                int at = GeneratedLayout.Off_targets + (i * GeneratedLayout.TargetStride);
                IntPtr record = IntPtr.Add(_viewPointer, at);
                byte[] nameBytes = new byte[SharedProtocol.TargetNameLen];
                WriteAscii(nameBytes, spec.Name);
                Marshal.Copy(nameBytes, 0, IntPtr.Add(record, GeneratedLayout.Target_name),
                             nameBytes.Length);
                Marshal.WriteInt16(record, GeneratedLayout.Target_ilSizeLow,
                                   (short)(spec.IlSize & 0xFFFF));
                byte[] hashBytes = new byte[32];
                for (int b = 0; b < 32; b++)
                {
                    hashBytes[b] = b < hash.Length ? hash[b] : (byte)0;
                }
                Marshal.Copy(hashBytes, 0, IntPtr.Add(record, GeneratedLayout.Target_hash), 32);
                Marshal.WriteByte(record, GeneratedLayout.Target_found, 0);
                Marshal.WriteInt64(record, GeneratedLayout.Target_firstSeenQpc, 0);
                Marshal.WriteInt64(record, GeneratedLayout.Target_nativeEntry, 0);
                Marshal.WriteInt32(record, GeneratedLayout.Target_nativeSize, 0);
                Marshal.WriteInt32(record, GeneratedLayout.Target_tokenRead, 0);
                Marshal.WriteInt32(record, GeneratedLayout.Target_metadataToken,
                                   unchecked((int)spec.MetadataToken));
                count++;
            }
            return count;
        }

        /// <summary>
        /// Parses `DRELAY_HOOK_TEST_TARGETS` into extra target specs, for the
        /// synthetic host and nothing else.
        ///
        /// The format is one or more specs separated by `;`, each
        /// `name|declaringType|methodName|ilSize|ilHash`, where `ilHash` is the
        /// 64-character SHA-256 that `tools/il_method_hash.py` prints from
        /// `il_index.py`'s own reader.  A malformed spec is refused and logged rather
        /// than skipped quietly: a test that silently published nothing would look
        /// exactly like a test whose target never compiled, and the whole point of
        /// this switch is to tell those two apart.
        /// </summary>
        private static List<GeneratedBuildTable.TargetSpec> ReadTestTargets()
        {
            var specs = new List<GeneratedBuildTable.TargetSpec>();
            string raw = Environment.GetEnvironmentVariable("DRELAY_HOOK_TEST_TARGETS");
            if (string.IsNullOrEmpty(raw))
            {
                return specs;
            }

            foreach (string entry in raw.Split(';'))
            {
                string spec = entry.Trim();
                if (spec.Length == 0)
                {
                    continue;
                }

                string[] fields = spec.Split('|');
                if (fields.Length != 5)
                {
                    HookEntry.AppendLog("ERROR", "test target refused (expected 5 fields, got " +
                                                 fields.Length + "): " + spec);
                    continue;
                }
                if (fields[0].Length == 0 || fields[0].Length >= SharedProtocol.TargetNameLen)
                {
                    HookEntry.AppendLog("ERROR", "test target refused (name empty or too long): " + spec);
                    continue;
                }
                if (!int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture,
                                  out int ilSize) || ilSize <= 0)
                {
                    HookEntry.AppendLog("ERROR", "test target refused (IL size '" + fields[3] +
                                                 "' is not a positive integer): " + spec);
                    continue;
                }
                if (fields[4].Length != 64)
                {
                    HookEntry.AppendLog("ERROR", "test target refused (IL hash is not 64 characters): " + spec);
                    continue;
                }

                specs.Add(new GeneratedBuildTable.TargetSpec(
                    fields[0], fields[1], fields[2], 0u, ilSize, fields[4].ToUpperInvariant(),
                    "test target (not the pinned build)"));
                HookEntry.AppendLog("INFO", "TEST TARGET appended: '" + fields[0] + "' " + fields[1] +
                                            "::" + fields[2] + " IL " + ilSize + " hash " +
                                            fields[4].ToUpperInvariant());
            }
            return specs;
        }

        /// <summary>
        /// Copies a string into a fixed-width buffer as Latin-1, truncating
        /// rather than throwing.  Latin-1 rather than UTF-8 on purpose: the native
        /// side reads these as narrow strings with `%s` and every character that
        /// matters here is ASCII, so a multi-byte sequence would be split at the
        /// buffer boundary and read as two wrong characters.
        /// </summary>
        private static void WriteAscii(byte[] destination, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            int n = Math.Min(text.Length, destination.Length - 1);
            for (int i = 0; i < n; i++)
            {
                char c = text[i];
                destination[i] = c <= 0xFF ? (byte)c : (byte)'?';
            }
        }

        // -------------------------------------------------------------------
        // Checks.

        private static void CheckRuntimeConfig(ref int passed, ref int failed)
        {
            // The load-bearing assumption.  `AppContext` is the runtime's own view
            // of the switch, which is the closest a managed caller can get to the
            // question "is tiered compilation on"; the native side separately
            // reads the environment, and the two together are what make the
            // assumption checkable from both directions.
            bool tieredKnown = AppContext.TryGetSwitch("System.Runtime.TieredCompilation", out bool tiered);
            AppendLog(tieredKnown && tiered ? "ERROR" : "INFO", string.Format(
                "AppContext System.Runtime.TieredCompilation: {0}",
                tieredKnown ? tiered.ToString() : "<not present>"));
            if (tieredKnown && tiered)
            {
                failed++;
            }
            else
            {
                passed++;
            }

            string framework = RuntimeInformation.FrameworkDescription;
            AppendLog("INFO", "runtime: " + framework + ", " + RuntimeInformation.ProcessArchitecture);
            if (!framework.Contains(".NET 9"))
            {
                // Not a failure -- the design does not depend on the exact
                // version -- but it is the kind of thing that should be visible
                // in the log rather than inferred from a crash later.
                AppendLog("WARN",
                    "the hook's reference values (JIT vtable slots, JIT/EE GUID) were derived for "
                    + ".NET 9; this process reports " + framework);
            }
        }

        private static void CheckGameAssembly(Assembly game, string path, ref int passed, ref int failed)
        {
            var mvid = game.ManifestModule.ModuleVersionId;
            AppendLog("INFO", "loaded assembly MVID (from the runtime): " + mvid.ToString("D").ToUpperInvariant());

            // PUBLISHED, NOT MERELY LOGGED, AND THE DISTINCTION IS A WHOLE CHECK.
            //
            // The native side compares this against the MVID it reads from the file
            // on disk, and that comparison is the one claim Phase 0's fingerprint
            // gate cannot make: the gate proves the file is the pinned build, this
            // proves the process is running that file.  The native side used to
            // read `DrelayShared.mvid` and find it zero, because nothing ever wrote
            // it -- so against the real game it refused to install on a build that
            // was in fact the pinned one.  A log line is not a publication.
            //
            // `Marshal.Copy` rather than a pointer dereference into the block, for
            // the reason ReadBlockU32 gives: the block is memory another process
            // writes, and the runtime cannot validate a raw dereference of it.
            byte[] mvidBytes = mvid.ToByteArray();
            Marshal.Copy(mvidBytes, 0, IntPtr.Add(_viewPointer, GeneratedLayout.Off_mvid), 16);

            var expected = Guid.Parse(GeneratedBuildTable.AssemblyMvid);
            if (mvid != expected)
            {
                AppendLog("ERROR", string.Format(
                    "MVID mismatch: the runtime loaded {0}, known-build.json pins {1}",
                    mvid.ToString("D").ToUpperInvariant(), expected.ToString("D").ToUpperInvariant()));
                failed++;
            }
            else
            {
                AppendLog("INFO", "MVID matches known-build.json");
                passed++;
            }

            long length = new FileInfo(path).Length;
            // PUBLISHED TOO, AND IT WAS NOT, WHICH MADE TWO REPORTS WRONG AT ONCE.
            //
            // `DrelayShared.managedAssemblySize` was declared, generated into the
            // managed layout, read by the self-check and written by nobody -- so a
            // verified run printed `PASS 5 assembly identity: MVID ... size 0` in
            // `selfcheck.txt` and `size : 0 bytes` in `summary.txt` while the
            // bootstrap's own log reported the real 50,216,960 bytes.  The field
            // existed precisely so those two files could state the size, and it is
            // the same class of defect as the MVID it sits beside: a value that is
            // measured, compared, and then not published.
            Marshal.WriteInt64(_viewPointer, GeneratedLayout.Off_managedAssemblySize, length);
            if (length != GeneratedBuildTable.AssemblySize)
            {
                AppendLog("ERROR", string.Format("assembly size {0} != pinned {1}", length,
                    GeneratedBuildTable.AssemblySize));
                failed++;
            }
            else
            {
                passed++;
            }

            // Is the game's Main still uncompiled?  If so, this hook really is
            // running before it.  This is a direct answer to Spike 1.1's question
            // rather than an inference from a timestamp comparison.
            try
            {
                MethodInfo entry = game.EntryPoint;
                if (entry == null)
                {
                    AppendLog("WARN", "the game assembly has no entry point; ordering cannot be confirmed");
                }
                else
                {
                    RuntimeHelpers.PrepareMethod(entry.MethodHandle);
                    AppendLog("INFO", string.Format(
                        "entry point is {0}.{1}; this hook ran before the host invoked it",
                        entry.DeclaringType?.FullName, entry.Name));
                }
                passed++;
            }
            catch (Exception ex)
            {
                AppendLog("WARN", "could not inspect the entry point: " + ex.Message);
            }
        }

        private static void ResolveOffsets(Assembly game, ref int offsetCount, ref int passed,
                                           ref int failed, ref int gaps)
        {
            Type worldType = game.GetType(ManagedArmingProbe.WorldTypeName);
            Type playerType = game.GetType(ManagedArmingProbe.PlayerTypeName);
            if (worldType == null || playerType == null)
            {
                AppendLog("ERROR", string.Format(
                    "could not resolve {0} / {1} by name; the assembly's names may have changed",
                    ManagedArmingProbe.WorldTypeName, ManagedArmingProbe.PlayerTypeName));
                failed++;
                return;
            }

            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static;

            FieldInfo sharedField = worldType.GetField("Shared", Any);
            FieldInfo playerField = worldType.GetField("Player", Any);
            FieldInfo positionField = playerType.GetField("Position", Any);
            if (sharedField == null || playerField == null || positionField == null)
            {
                AppendLog("ERROR", string.Format(
                    "field lookup failed: World.Shared={0} World.Player={1} Player.Position={2}",
                    sharedField != null, playerField != null, positionField != null));
                failed++;
                return;
            }

            // A static field has no instance offset; it has an address.  The
            // runtime gives us one through a one-instruction dynamic method
            // rather than through a documented API, because there is no
            // documented API for it -- see StaticFieldAddress for the caveats.
            IntPtr sharedAddress = StaticFieldAddress(sharedField);
            // NOT `Marshal.OffsetOf`, AND THE LIVE RUN IS WHY.
            //
            // The first version of this used `Marshal.OffsetOf(worldType, "Player")`
            // and `Marshal.OffsetOf(playerType, "Position")`, and against the real
            // game it threw before the bootstrap could signal the native side:
            //
            //   System.ArgumentException: Type 'DarzasDominion.Darza.Game.World'
            //   cannot be marshaled as an unmanaged structure; no meaningful size
            //   or offset can be computed.
            //
            // `Marshal.OffsetOf` is not a general field-offset API.  It answers for
            // a type the marshaller can lay out as an unmanaged structure, and a
            // managed class full of object references is not one -- which is every
            // interesting type in this game.  It happened to work against the
            // synthetic host only because that host has no such type, so the
            // failure needed the real assembly to appear at all.
            //
            // The replacement asks the same question of the runtime directly, one
            // `ldflda` at a time, which works for any field of any type including
            // inline value types and reference fields.
            int playerOffset = FieldOffsetOf(playerField);
            int positionOffset = FieldOffsetOf(positionField);

            WriteOffset(0, "World.Shared (static address)",
                        (uint)sharedAddress.ToInt64(), isStatic: true,
                        staticHigh: (uint)(sharedAddress.ToInt64() >> 32));
            WriteOffset(1, "World.Player", (uint)playerOffset, isStatic: false);
            WriteOffset(2, "Player.Position", (uint)positionOffset, isStatic: false);
            offsetCount = 3;

            // Phase 2's read path.  Resolved here because this is where the assembly and
            // the reflection already are, and NOT fatal if it fails: the frame read, the
            // patch and every piece of Phase 1 evidence work without it.
            try
            {
                if (!SensorRead.Resolve(game))
                {
                    AppendLog("ERROR", "the sensor's read path is NOT available: "
                                       + SensorRead.UnresolvedReason
                                       + " -- the frame read and the patch are unaffected, but "
                                       + "no threat set will be maintained");
                }
                else if (!TrajectorySampler.Resolve(game))
                {
                    AppendLog("ERROR", "the trajectory sampler is NOT available: "
                                       + TrajectorySampler.UnresolvedReason
                                       + " -- the threat set is unaffected, but no trajectory "
                                       + "will be sampled and no straight fraction measured");
                }
                else if (!CollisionObservation.Resolve(game))
                {
                    AppendLog("ERROR", "the collision observation is NOT available: "
                                       + CollisionObservation.UnresolvedReason
                                       + " -- nothing else is affected, but no predicted-versus-"
                                       + "actual comparison will be made");
                }
                else if (!TraceRecorder.Resolve(game, Environment.GetEnvironmentVariable("DRELAY_HOOK_RUNDIR")))
                {
                    // NOT AN ERROR WHEN THE SWITCH IS SIMPLY UNSET, which is every run but the
                    // recording ones. The recorder says which of the two it was, so a run where
                    // the operator asked for a trace and did not get one is distinguishable in
                    // the log from a run that never wanted one.
                    string why = TraceRecorder.UnresolvedReason;
                    if (why != null && why.IndexOf("DRELAY_TRACE", StringComparison.Ordinal) >= 0)
                    {
                        AppendLog("INFO", "trace recorder idle: " + why);
                    }
                    else
                    {
                        AppendLog("ERROR", "the trace recorder was asked for and is NOT available: "
                                           + why + " -- no trace will be written");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "resolving the sensor's read path threw: " + ex);
            }

            // DELIVERABLE 3.3, RESOLVED OUTSIDE THE CHAIN ABOVE AND DELIBERATELY.
            //
            // The chain above is a chain of `else if`s, so a failure anywhere in it silently skips
            // everything after it -- which is why this is not another `else if`. The dodge depends
            // on the sensor's *frame path* (it is called from it) but not on the recorder or on the
            // collision observation resolving, and a run that asked for a dodge and lost it because
            // the trace could not open its file would be a run whose report says "no threat" for
            // reasons that have nothing to do with the world.
            try
            {
                if (!DodgeAdapter.Resolve(game))
                {
                    string why = DodgeAdapter.UnresolvedReason;
                    if (why != null && why.IndexOf("DRELAY_DODGE", StringComparison.Ordinal) >= 0)
                    {
                        AppendLog("INFO", "dodge idle: " + why);
                    }
                    else
                    {
                        AppendLog("ERROR", "the dodge adapter was asked for and is NOT available: "
                                           + why + " -- the sensor, the trace and the actuator are "
                                           + "unaffected, but no live decision will be made");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "resolving the dodge adapter threw: " + ex);
            }

            AppendLog("INFO", string.Format(
                "offsets measured by managed reflection: World.Shared @ 0x{0:X} (static), "
                + "World.Player +{1}, Player.Position +{2}",
                sharedAddress.ToInt64(), playerOffset, positionOffset));
            passed++;

            // THE CROSS-CHECK IS NO LONGER ATTEMPTED HERE, AND THE REASON IS THE
            // WHOLE POINT OF THIS ROUND.
            //
            // It used to run at this moment -- which is during the startup hook's
            // aftermath, before the game has a world -- so `World.Shared` was always
            // null, the chain was never walked, and the check reported a GAP every
            // single run.  Worse, the GAP then disabled the sensor, so Spike
            // 1.3's success test ("read exactly one float from the live world") could
            // never execute: the sensor was switched off by a check that had not run.
            //
            // The offsets and the type identity are properties of the BUILD, and they
            // are measured here because here is when they can be measured.  Whether
            // they describe a live world is a question about the WORLD, and it is
            // asked by the arming probe (`ManagedArmingProbe.OnProbe`) the first time
            // a world exists -- which is tens of seconds later and on a thread where
            // reflection is ordinary work.
            //
            // So: publish the measurement, hand it over, and leave the gate CLOSED.
            // The probe opens it, and only if the pointer chain reproduces what
            // reflection says.
            FieldInfo pointX = null;
            FieldInfo pointY = null;
            try
            {
                pointX = positionField.FieldType.GetField("X", Any);
                pointY = positionField.FieldType.GetField("Y", Any);
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "could not read the point type's fields: " + ex.Message);
            }
            if (pointX == null || pointY == null)
            {
                // Without these the probe cannot make its comparison, and a probe
                // that silently cannot run is the failure this round exists to
                // remove.  Reported here rather than as an unexplained
                // `UNCONFIGURED` forty seconds later.
                AppendLog("ERROR", string.Format(
                    "the point type {0} has no X and Y fields, so the offset cross-check cannot "
                    + "be made; the frame read path will not open",
                    positionField.FieldType.FullName));
                failed++;
            }

            // Configured even when X/Y are missing: the probe checks them and
            // refuses with a reason rather than the run reporting nothing at all.
            ManagedArmingProbe.Configure(sharedAddress, playerOffset, positionOffset,
                                           playerType.TypeHandle.Value,
                                           sharedField, playerField, positionField, pointX, pointY);

            // THE METHODTABLE, PUBLISHED FOR THE NATIVE FRAME READ.
            //
            // This is the one thing about the frame path's inputs that managed code
            // owns, and it cannot be measured on the native side: it is the runtime's
            // own `TypeHandle`, which only reflection can name.  The native read
            // compares the object at `World + playerOffset` against it, so publishing
            // the wrong handle would be worse than publishing none -- which is why the
            // startup hook zeroes this field first and this is the only writer.
            Marshal.WriteInt64(_viewPointer, GeneratedLayout.Off_managedPlayerTypeHandle,
                               playerType.TypeHandle.Value.ToInt64());
            AppendLog("INFO", string.Format(
                "resolve[7] Player MethodTable {0} published for the native frame read",
                "0x" + playerType.TypeHandle.Value.ToInt64().ToString("X")));
            FlushLog();

            AppendLog("INFO", "resolve[7] layout published; the arming probe will cross-check "
                              + "it against the live world as soon as one exists");
        }

        /// <summary>
        /// Records one measured field offset in the block.
        ///
        /// Written through `Marshal` at the offsets the generator computed, rather
        /// than by building a `DrelayFieldOffset` and passing it by reference.  The
        /// by-reference form compiled and worked, but it required the managed side
        /// to hold a `DrelayShared*` -- and a raw dereference of memory another
        /// process writes is what the runtime cannot validate, with the whole
        /// process as the failure mode.  Field-by-field writes keep the managed side
        /// free of pointers into the block entirely.
        /// </summary>
        private static void WriteOffset(int index, string name, uint offset, bool isStatic,
                                        uint staticHigh = 0)
        {
            int at = GeneratedLayout.Off_offsets + (index * GeneratedLayout.OffsetStride);
            byte[] nameBytes = new byte[SharedProtocol.FieldNameLen];
            WriteAscii(nameBytes, name);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(_viewPointer, at), nameBytes.Length);
            // THE FIELD NAMES COME FROM THE GENERATOR, NOT FROM ARITHMETIC HERE.
            //
            // These were numbers -- 64, 68, 72, 73, 76, 80, 81 -- copied out of the
            // native header by hand.  That is the same defect as `PublishTargets`
            // writing `hash` at 40 against a native 34: a second copy of a layout
            // that already has an authority, which is right until the authority
            // moves.  `gen_shared_layout.py` emits one constant per field of every
            // sub-struct (see `Offset_*`), so there is no reason for a literal.
            Marshal.WriteInt32(_viewPointer, at + GeneratedLayout.Offset_managedOffset,
                               unchecked((int)offset));
            // For a static field this is the HIGH half of the address; see the note
            // on DrelayFieldOffset in DrelayHookShared.h.  Zero for every instance
            // offset, which is what makes `is_static` the flag that decides how the
            // pair is read.
            Marshal.WriteInt32(_viewPointer, at + GeneratedLayout.Offset_managedStaticBase,
                               unchecked((int)staticHigh));
            Marshal.WriteByte(_viewPointer, at + GeneratedLayout.Offset_managedValid, 1);
            Marshal.WriteByte(_viewPointer, at + GeneratedLayout.Offset_isStatic,
                              isStatic ? (byte)1 : (byte)0);
            Marshal.WriteInt32(_viewPointer, at + GeneratedLayout.Offset_jitOffset,
                               unchecked((int)SharedProtocol.SlotNone));
            Marshal.WriteByte(_viewPointer, at + GeneratedLayout.Offset_jitValid, 0);
            // Not yet: `agree` is written by whoever confirms the value by an
            // independent route, and at this moment nothing has.
            Marshal.WriteByte(_viewPointer, at + GeneratedLayout.Offset_agree, 0);
        }

        /// <summary>
        /// The byte offset of an instance field, asked of the runtime directly.
        ///
        /// `ldflda` yields the field's address for a given object, so the offset is
        /// `address - object`.  The object has to be REAL.
        ///
        /// THE NULL-REFERENCE VERSION OF THIS DID NOT WORK, AND THE FAILURE WAS A
        /// MEASURED ONE, NOT A SUSPICION.
        ///
        /// The version this replaces passed `null` and relied on `ldflda` computing
        /// `null + offset` without touching the object.  Against the synthetic host
        /// that is what happened, so the technique looked sound.  Against the real
        /// game the call threw:
        ///
        ///   System.NullReferenceException: Object reference not set to an instance
        ///   of an object.
        ///      at drelay_field_offset(Object)
        ///      at Drelay.Bootstrap.HookEntry.FieldOffsetOf(FieldInfo field)
        ///
        /// The managed frame in that stack is the dynamic method itself, so the
        /// fault is the emitted `ldflda` and nothing else.  Isolated outside the
        /// startup hook, against the same assembly, with the same runtime, four
        /// shapes were tried and all four threw on a null:
        ///
        ///   ldarg.0; ldflda; conv.i; ret                  -> NullReferenceException
        ///   ldarg.0; castclass; ldflda; conv.i; ret       -> NullReferenceException
        ///   ldflda with a real instance                   -> the offset
        ///   ldsflda for a static field                    -> the address
        ///
        /// So "`ldflda` never needs a null check" is false on this runtime, and the
        /// synthetic host was only ever passing because its types are trivial.  That
        /// is the same shape of trap as the `Marshal.OffsetOf` failure documented at
        /// the call site: a technique that passes against the stand-in and fails
        /// against the thing under test.
        ///
        /// The replacement allocates an *uninitialised* instance of the declaring
        /// type and measures against it.  `GetUninitializedObject` runs no
        /// constructor, so it cannot start game machinery or observe half-built
        /// state; it costs one zeroed allocation per field, once per session, at a
        /// point where the runtime is entirely happy to allocate.  Nothing reads
        /// through the returned offset here -- the cross-check does that separately,
        /// against the live object, and refuses to arm the callback if the two
        /// routes disagree.
        /// </summary>
        private static int FieldOffsetOf(FieldInfo field)
        {
            var method = new DynamicMethod(
                "drelay_field_offset", typeof(IntPtr), new[] { typeof(object) },
                field.DeclaringType, skipVisibility: true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldflda, field);
            il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Ret);

            // Rooted for the process: a collected DynamicMethod whose code is still
            // reachable is undefined behaviour, and these are called again by the
            // cross-check.
            _staticAddressKeepAlive.Add(method);
            var fn = (Func<object, IntPtr>)method.CreateDelegate(typeof(Func<object, IntPtr>));
            _staticAddressKeepAliveDelegates.Add(fn);

            object instance = RuntimeHelpers.GetUninitializedObject(field.DeclaringType);
            long fieldAddress = fn(instance).ToInt64();
            long objectAddress = ObjectAddress(instance);
            return (int)(fieldAddress - objectAddress);
        }

        /// <summary>
        /// The address of a managed object, through a one-instruction dynamic method.
        ///
        /// Used only to turn `ldflda`'s absolute address into the offset the native
        /// side needs.  `GCHandle.Alloc(obj, GCHandleType.Pinned)` would be the
        /// documented route and is deliberately not used: pinning a game object is a
        /// side effect on the object, and the offset does not need one.
        ///
        /// The instance is held by the caller for the duration of the measurement,
        /// so the address cannot move between the two calls.  A GC here would be
        /// harmless even so -- the two measurements are used only as `a - b`, and
        /// both operands would move together -- but the invariant is stated because
        /// it is what makes the subtraction meaningful.
        /// </summary>
        private static long ObjectAddress(object instance)
        {
            var method = new DynamicMethod(
                "drelay_object_address", typeof(IntPtr), new[] { typeof(object) },
                typeof(object), skipVisibility: true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ret);
            _staticAddressKeepAlive.Add(method);
            var fn = (Func<object, IntPtr>)method.CreateDelegate(typeof(Func<object, IntPtr>));
            _staticAddressKeepAliveDelegates.Add(fn);
            return fn(instance).ToInt64();
        }

        /// <summary>
        /// The address of a static field.
        ///
        /// WHY THIS IS A DYNAMIC METHOD AND NOT AN API CALL
        /// ------------------------------------------------
        /// There is no public way to get the address of a static field's storage.
        /// `Marshal.OffsetOf` refuses statics; `RuntimeHelpers` exposes no such
        /// thing; and the JIT's own `getStaticBaseAddress`/`getFieldOffset` pair
        /// is only callable while the JIT is compiling something.  What is
        /// available is `ldsflda`, so a one-instruction dynamic method returns
        /// exactly the address, with the runtime's own field resolution doing the
        /// work and the JITter having no reason to be clever about it.
        ///
        /// Two caveats, both real and both accepted for Phase 1 rather than
        /// papered over:
        ///
        ///   * the returned address is valid for the life of the class's statics
        ///     block.  If the runtime ever relocates it -- which for a static
        ///     reference field of a non-collectible class it does not -- the
        ///     cached value would go stale.  A static handle would be the robust
        ///     answer and needs the native side to resolve it every frame, which
        ///     is Phase 2's shape, not this one's;
        ///   * a static field in a collectible assembly could be collected.  The
        ///     game assembly is not collectible in this process.
        /// </summary>
        private static IntPtr StaticFieldAddress(FieldInfo field)
        {
            var method = new DynamicMethod(
                "drelay_static_address", typeof(IntPtr), Type.EmptyTypes,
                field.DeclaringType, skipVisibility: true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldsflda, field);
            il.Emit(OpCodes.Conv_U);
            il.Emit(OpCodes.Ret);
            // Keep the delegate alive for the process: the DynamicMethod itself is
            // not rooted by its delegate's existence, and a collected
            // DynamicMethod whose code is still being jumped into is undefined.
            _staticAddressKeepAlive.Add(method);
            var fn = (Func<IntPtr>)method.CreateDelegate(typeof(Func<IntPtr>));
            _staticAddressKeepAliveDelegates.Add(fn);
            return fn();
        }

        private static readonly List<DynamicMethod> _staticAddressKeepAlive = new List<DynamicMethod>();
        private static readonly List<object> _staticAddressKeepAliveDelegates =
            new List<object>();

        // -------------------------------------------------------------------
        // The handshake.

        /// <summary>
        /// The address of the resolve callback, which the native side invokes once
        /// the startup phase is over.  An `[UnmanagedCallersOnly]` method's address is
        /// its already-emitted transition stub, so obtaining it compiles nothing --
        /// which is what makes it safe to take from inside the startup hook.
        ///
        /// THE FRAME CALLBACK'S EQUIVALENT OF THIS IS GONE, AND IT IS NOT COMING BACK.
        /// An `[UnmanagedCallersOnly]` address is only usable from a thread the runtime
        /// considers unmanaged, which is true of the watcher thread that calls this and
        /// false of the game's frame thread, where the detour runs.  See the note where
        /// `OnFrame` used to be.
        /// </summary>
        private static IntPtr GetResolveCallbackAddress()
        {
            unsafe
            {
                delegate* unmanaged[Cdecl]<void> callback = &ManagedArmingProbe.OnResolve;
                return (IntPtr)callback;
            }
        }

        /// <summary>
        /// The address of the arming probe, which the native watcher thread calls
        /// repeatedly until the decision is made.  Same mechanics and same reasoning
        /// as the resolve callback above.
        /// </summary>
        private static IntPtr GetProbeCallbackAddress()
        {
            unsafe
            {
                delegate* unmanaged[Cdecl]<void> callback = &ManagedArmingProbe.OnProbe;
                return (IntPtr)callback;
            }
        }

        /// <summary>
        /// Reads one 32-bit field out of the mapped block.
        ///
        /// MARSHALLING, NOT POINTER DEREFERENCE, AND THAT IS A CORRECTNESS FIX.
        /// The previous version read the block through `DrelayShared*` inside an
        /// `unsafe` block, and the bootstrap died with
        /// `Fatal error. Internal CLR error. (0x80131506)` inside this method --
        /// with the call stack naming `StartupHookProvider.CallStartupHook`.
        /// Dereferencing a raw address that another process is concurrently writing
        /// is not something the runtime can validate, and the failure mode is the
        /// whole process, not a wrong number.  `Marshal.ReadInt32` is a runtime
        /// service: it validates the address and copies four bytes.
        ///
        /// The offsets come from the generator, so this reads the field the native
        /// header declares rather than one this file believes in.
        /// </summary>
        private static uint ReadBlockU32(int offset)
        {
            return unchecked((uint)Marshal.ReadInt32(_viewPointer, offset));
        }

        // -------------------------------------------------------------------
        // Logging.  The bootstrap's lines go into the same ring the native side
        // writes, so one file holds the whole startup story in order.

        private static readonly object LogLock = new object();
        private static int _logSeq;
        private static string _logPath;

        public static void AppendLog(string level, string message)
        {
            string line = string.Format("[{0}] boot: {1}", level, message);
            lock (LogLock)
            {
                Log.Add(line);
            }
        }

        /// <summary>
        /// The site of an exception, for a one-shot diagnostic.
        ///
        /// A TYPE AND A MESSAGE ARE NOT ENOUGH, AND THIS PROJECT HAS THE RECEIPT.
        /// `NullReferenceException` out of a dozen nested calls said nothing, and the
        /// emitted-accessor bug was found only once the log carried the stack
        /// (PHASE2-SCOPE.md section 4.12: "a diagnostic that names its site is worth the
        /// characters").  Four hundred characters is about six frames, which is where the
        /// answer has been every time it has been asked.
        /// </summary>
        internal static string FirstFrames(Exception ex)
        {
            string where = ex.StackTrace == null
                ? "(no stack)"
                : ex.StackTrace.Replace("\r", " ").Replace("\n", " ");
            return where.Length > 400 ? where.Substring(0, 400) : where;
        }

        public static void FlushLog()
        {
            string runDir = Environment.GetEnvironmentVariable(EnvRunDir);
            if (string.IsNullOrEmpty(runDir))
            {
                return;
            }
            // The bootstrap's own file, not the native side's.  Two writers on one path,
            // opened by two different runtimes with different sharing modes, produced a
            // file containing one writer's lines and not the other's; separate files
            // cannot fail that way.  DrLogInit on the native side says the same thing.
            _logPath = Path.Combine(runDir, "bootstrap.log");
            string[] lines;
            lock (LogLock)
            {
                lines = Log.ToArray();
                Log.Clear();
            }
            if (lines.Length == 0)
            {
                return;
            }
            try
            {
                using (var writer = new StreamWriter(_logPath, append: _logSeq > 0, Encoding.ASCII))
                {
                    foreach (string line in lines)
                    {
                        // The native flusher writes CRLF too; matching it keeps one
                        // file readable by one reader.
                        writer.Write(line);
                        writer.Write("\r\n");
                    }
                }
                _logSeq += lines.Length;
            }
            catch (IOException)
            {
                // A log write failing must never fail the hook.  The line is
                // lost, and the native side's ring still holds its own.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // -------------------------------------------------------------------

        private static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return Array.Empty<byte>();
            }
            var result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return result;
        }
    }

    internal static class NativeMethods
    {
        public const uint FileMapAllAccess = 0x000F001F;
        public const uint EventModifyState = 0x0002;
        public const uint WaitObject0 = 0;
        public const uint Infinite = 0xFFFFFFFF;
        public const uint PageReadWrite = 0x04;
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint OpenExisting = 3;
        public const uint FileAttributeNormal = 0x80;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string path, uint desiredAccess, uint shareMode,
                                               IntPtr attributes, uint creationDisposition,
                                               uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attributes, uint protect,
                                                      uint maximumSizeHigh, uint maximumSizeLow,
                                                      string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr mapping, uint desiredAccess, uint offsetHigh,
                                                  uint offsetLow, UIntPtr numberOfBytesToMap);

        /// <summary>
        /// Writes one 32-bit field into the mapped block.
        ///
        /// Marshalling rather than `DrelayShared*`: the managed side never
        /// dereferences the block as a pointer any more, because a raw dereference
        /// of memory another process is writing is not something the runtime can
        /// validate and its failure mode is the whole process.  See StartupHook's
        /// ReadBlockU32.
        /// </summary>
        public static void WriteBlockU32(IntPtr block, int offset, uint value)
        {
            Marshal.WriteInt32(block, offset, unchecked((int)value));
        }

        /// <summary>
        /// Writes one 32-bit float into the mapped block, for the cross-check's two
        /// coordinate pairs.  Through the bit pattern rather than through
        /// `Marshal.WriteInt32` on a reinterpreted float, because the block's field
        /// is a C `float` and `BitConverter.SingleToInt32Bits` is the runtime's own
        /// statement about how a float is stored.
        /// </summary>
        public static void WriteBlockF32(IntPtr block, int offset, float value)
        {
            Marshal.WriteInt32(block, offset, BitConverter.SingleToInt32Bits(value));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState,
                                                string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenEvent(uint desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}

/// <summary>
/// The global-scope entry point the runtime looks for.
///
/// StartupHookProvider loads DOTNET_STARTUP_HOOKS assemblies and looks for a
/// type named exactly StartupHook in the global namespace with a public static
/// Initialize().  A namespace is not permitted, which the first version of this
/// file found out the hard way: the type was Drelay.Bootstrap.StartupHook and
/// the host failed with TypeLoadException: Could not load type 'StartupHook'.
///
/// So this exists only to be findable, and forwards to the real implementation so
/// that the rest of the file can stay organised.  It adds no behaviour, and the
/// forwarding call is the whole of it.
/// </summary>
public static class StartupHook
{
    public static void Initialize()
    {
        Drelay.Bootstrap.HookEntry.Initialize();
    }
}
