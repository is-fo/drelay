// SharedBlock.cs -- the launcher's side of the handshake.
//
// The launcher creates the mapping and both events *before* the game process
// exists, so neither the hook nor the bootstrap has to race to be the creator.
// That ownership is what makes the ordering deterministic: by the time the first
// remote thread runs, every kernel object it will need is already there and
// already has the right name.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Drelay.Bootstrap;

namespace Drelay.Launcher
{
    internal sealed class SharedBlock : IDisposable
    {
        private readonly IntPtr _mapping;
        private readonly IntPtr _view;
        private readonly IntPtr _nativeReady;
        private readonly IntPtr _bootReady;
        private readonly uint _session;
        private static IntPtr _backingFile = IntPtr.Zero;

        private SharedBlock(uint session, IntPtr mapping, IntPtr view, IntPtr nativeReady, IntPtr bootReady)
        {
            _session = session;
            _mapping = mapping;
            _view = view;
            _nativeReady = nativeReady;
            _bootReady = bootReady;
        }

        public static SharedBlock Create(uint session, string runDirectory)
        {
            // A layout bug should be caught here, by the process that creates the
            // mapping, rather than by whoever reads it first -- and the launcher is
            // the only participant that can run this check before anything else
            // exists.
            string complaint;
            if (!SharedLayout.VerifyOffsets(out complaint))
            {
                throw new InvalidOperationException(
                    "the C# view of the shared block is internally inconsistent: " + complaint);
            }

            int size = GeneratedLayout.DrelaySharedSize;

            // The block is backed by a FILE in the run directory, not by a named
            // pagefile section, and that is a correction rather than a preference.
            //
            // A named pagefile-backed section is charged against the system commit
            // limit when it is created, and on this machine
            // `CreateFileMapping(INVALID_HANDLE_VALUE, ..., "Local\\name")` for
            // 431 KB fails with error 8 (ERROR_NOT_ENOUGH_MEMORY) from inside the
            // launcher while the identical call from another process succeeds --
            // see tests/MapProbe, which is what established that.  Deliberately
            // resisting that diagnosis, a file-backed section has no commit charge
            // at all: it is a window onto a file that already exists, which is the
            // whole of what this block is.
            //
            // It also makes a crashed run's block readable afterwards, because the
            // bytes are in the run directory rather than in a pagefile.
            string path = Path.Combine(runDirectory, "shared.block");

            // Create or truncate, then set the size explicitly.  A file-backed
            // mapping cannot exceed its file, so the size is not optional.
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite,
                                               FileShare.ReadWrite))
            {
                stream.SetLength(size);
            }

            IntPtr file = NativeMethods.CreateFile(
                path, NativeMethods.GenericRead | NativeMethods.GenericWrite,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero, NativeMethods.OpenExisting, NativeMethods.FileAttributeNormal, IntPtr.Zero);
            if (file == IntPtr.Zero || file == new IntPtr(-1))
            {
                throw new InvalidOperationException(
                    "CreateFile(" + path + "): " + NativeMethods.LastErrorText());
            }

            _backingFile = file;

            IntPtr mapping = NativeMethods.CreateFileMapping(
                file, IntPtr.Zero, NativeMethods.PAGE_READWRITE, 0, (uint)size, null);
            if (mapping == IntPtr.Zero)
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    "CreateFileMapping(" + path + ", " + size + " bytes): " + error + " " +
                    new System.ComponentModel.Win32Exception(error).Message);
            }

            IntPtr view = NativeMethods.MapViewOfFile(mapping, NativeMethods.FILE_MAP_ALL_ACCESS, 0, 0,
                                                      UIntPtr.Zero);
            if (view == IntPtr.Zero)
            {
                throw new InvalidOperationException("MapViewOfFile: " + NativeMethods.LastErrorText());
            }

            IntPtr nativeReady = NativeMethods.CreateEvent(IntPtr.Zero, true, false,
                SharedProtocol.NativeReadyEventName(session));
            IntPtr bootReady = NativeMethods.CreateEvent(IntPtr.Zero, true, false,
                SharedProtocol.BootReadyEventName(session));
            if (nativeReady == IntPtr.Zero || bootReady == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateEvent: " + NativeMethods.LastErrorText());
            }

            return new SharedBlock(session, mapping, view, nativeReady, bootReady);
        }

        /// <summary>
        /// Stamps the header.  The magic is `MagicBoot` rather than `MagicConfig`
        /// until the configuration is complete, so a reader that races the
        /// launcher sees an obviously incomplete block instead of a half-written
        /// one that happens to pass its checks.
        /// </summary>
        public unsafe void WriteHeader()
        {
            DrelayShared* block = (DrelayShared*)_view;
            block->version = SharedProtocol.Version;
            block->structSize = (uint)GeneratedLayout.DrelaySharedSize;
            block->magic = SharedProtocol.MagicBoot;
            block->nativeTimeoutMs = 60000;
            // Cheap liveness without a clock call in a hot path: the bootstrap
            // bumps this, and a reader can tell "never ran" from "ran".
            block->bootTick = 1;
        }

        public unsafe void SetConfigReady()
        {
            DrelayShared* block = (DrelayShared*)_view;
            block->magicConfig = SharedProtocol.MagicConfig;
        }

        public bool WaitForNative(int timeoutMs)
        {
            return NativeMethods.WaitForSingleObject(_nativeReady, (uint)timeoutMs) == NativeMethods.WAIT_OBJECT_0;
        }

        public bool WaitForBootstrap(int timeoutMs)
        {
            return NativeMethods.WaitForSingleObject(_bootReady, (uint)timeoutMs) == NativeMethods.WAIT_OBJECT_0;
        }

        /// <summary>
        /// Prints what the native side published.  This is the launcher's
        /// contribution to the evidence: it is the only participant that sees the
        /// handshake from outside, so it is the only one that can report "the
        /// bootstrap never answered" as distinct from "the hook refused".
        /// </summary>
        public unsafe void Report(Action<string> write, bool verbose)
        {
            DrelayShared* block = (DrelayShared*)_view;
            write("");
            write("---- shared block ----");
            write(string.Format("  magic/version   : 0x{0:X8} / {1}", block->magic, block->version));
            write(string.Format("  native status   : 0x{0:X8}{1}{2}{3}{4}{5}",
                block->nativeStatus,
                (block->nativeStatus & SharedProtocol.StJitDetoured) != 0 ? " jitDetoured" : "",
                (block->nativeStatus & SharedProtocol.StDiscoveryDone) != 0 ? " discoveryDone" : "",
                (block->nativeStatus & SharedProtocol.StPatched) != 0 ? " patched" : "",
                (block->nativeStatus & SharedProtocol.StRefused) != 0 ? " REFUSED" : "",
                (block->nativeStatus & SharedProtocol.StOff) != 0 ? " OFF" : ""));
            write(string.Format("  self-check      : {0} passed / {1} failed / {2} gap(s)",
                block->selfCheckPassed, block->selfCheckFailed, block->selfCheckGaps));
            write(string.Format("  targets         : {0} found, {1} patched, of {2} pinned",
                block->targetsFound, block->targetsPatched, block->targetCount));
            write(string.Format("  methodDef slot  : {0}", block->methodDefSlotUsed == SharedProtocol.SlotNone
                ? "not proven"
                : "0x" + block->methodDefSlotUsed.ToString("X2")));
            write(string.Format("  compile calls   : {0}", block->traceWrite));
            write(string.Format("  frame calls     : {0} (frame reads {1} ok / {2} failed)",
                block->frameCalls, block->managedReadCount, block->managedReadFailures));
            // Printed next to the read counts because it decides how to read them:
            // two zeroes mean "there was no world to read" when the read path was
            // never opened and something else entirely when it was.
            write(string.Format("  frame read path : {0}{1}",
                ArmStateName(block->managedArmState),
                block->managedArmState == SharedProtocol.ArmArmed
                    ? string.Format(" at t+{0} ms", block->managedArmTick) : ""));
            write(string.Format("  clrjit/coreclr  : {0} / {1}",
                ReadFixedBuffer(&block->clrjitVersion[0], 32), ReadFixedBuffer(&block->coreclrVersion[0], 32)));
            write(string.Format("  runtime tiering : {0}",
                block->effectiveTieredCompilation == 0 ? "off"
                : block->effectiveTieredCompilation == 1 ? "ON (should have refused)" : "unknown"));
            write("");

            if (verbose)
            {
                // A stack local, addressed directly rather than through the
                // index accessor.  A fixed-size buffer field cannot be passed to
                // a `byte*` parameter as `local.name` -- the compiler answers
                // "you can only take the address of an unfixed expression inside
                // of a fixed statement initializer", because the local is a copy
                // and the buffer would move with it.  Taking `&local.name[0]`
                // works because the local is a stack value and never moves.
                for (int i = 0; i < block->targetCount && i < SharedProtocol.MaxTargets; i++)
                {
                    DrelayTargetHash target = SharedLayout.Target(ref *block, i);
                    write(string.Format("  target {0,-12} {1}  entry 0x{2:X}  native {3} bytes  token 0x{4:X8}",
                        ReadFixedBuffer(&target.name[0], SharedProtocol.TargetNameLen),
                        target.found != 0 ? "FOUND" : "MISSING",
                        target.nativeEntry, target.nativeSize, target.tokenRead));
                }
                for (int i = 0; i < block->offsetCount && i < SharedProtocol.MaxReads; i++)
                {
                    DrelayFieldOffset offset = SharedLayout.FieldOffset(ref *block, i);
                    // A STATIC FIELD'S VALUE IS AN ADDRESS IN TWO HALVES, and printing
                    // only `managedOffset` prints half a pointer that looks like a
                    // byte offset.  See DrelayFieldOffset in DrelayHookShared.h.
                    string where = offset.isStatic != 0
                        ? string.Format("0x{0:X8}{1:X8}", offset.managedStaticBase, offset.managedOffset)
                        : "+" + offset.managedOffset;
                    write(string.Format("  offset {0,-32} {1}{2}",
                        ReadFixedBuffer(&offset.name[0], SharedProtocol.FieldNameLen),
                        where, offset.agree != 0 ? "  confirmed" : ""));
                }
            }
        }

        private static unsafe string ReadFixedBuffer(byte* source, int count)
        {
            int len = 0;
            while (len < count && source[len] != 0)
            {
                len++;
            }
            return System.Text.Encoding.ASCII.GetString(source, len);
        }

        /// <summary>
        /// The arming gate's state, spelled the way the hook's own logs spell it, so
        /// the launcher's summary and <c>summary.txt</c> cannot disagree about what
        /// "0" means.
        /// </summary>
        private static string ArmStateName(uint state)
        {
            switch (state)
            {
                case SharedProtocol.ArmWaiting: return "waiting for a world";
                case SharedProtocol.ArmArmed: return "ARMED";
                case SharedProtocol.ArmRefused: return "REFUSED (the cross-check disagreed)";
                case SharedProtocol.ArmUnconfigured: return "UNCONFIGURED (no offsets measured)";
                default: return "unknown (" + state + ")";
            }
        }

        public void Dispose()
        {
            if (_nativeReady != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_nativeReady);
            }
            if (_bootReady != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_bootReady);
            }
            if (_view != IntPtr.Zero)
            {
                NativeMethods.UnmapViewOfFile(_view);
            }
            if (_mapping != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_mapping);
            }
            if (_backingFile != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_backingFile);
                _backingFile = IntPtr.Zero;
            }
        }
    }
}
