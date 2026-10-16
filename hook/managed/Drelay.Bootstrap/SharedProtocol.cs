// SharedProtocol.cs -- the constants and accessors that are *not* generated from
// the native header.
//
// The layout itself lives in GeneratedSharedLayout.cs, which
// tools/gen_shared_layout.py writes from `native/DrelayHookShared.h`.  This file
// holds only what a generator cannot know:
//
//   * the protocol constants (magics, flag bits, status bits) which are written
//     in the header as #defines rather than as struct members;
//   * the helpers that read a struct field by index, because the generated
//     struct declares the eight targets and eight offsets as eight separate
//     fields -- which is what pins their offsets -- and something has to map an
//     index onto them.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Drelay.Bootstrap
{
    public static class SharedProtocol
    {
        /// <summary>
        /// Mirrors DRELAY_SHARED_VERSION.  A mismatch between this and the native
        /// side's value is a refusal, never an interpretation.
        /// </summary>
        public const uint Version = GeneratedLayout.SharedVersion;

        public const uint MagicConfig = 0x44485243; // 'DHRC'
        public const uint MagicStatus = 0x44485253; // 'DHRS'
        public const uint MagicBoot = 0x44485242;   // 'DHRB'

        public const int MaxTargets = GeneratedLayout.DRELAY_MAX_TARGETS;
        public const int MaxSteps = GeneratedLayout.DRELAY_MAX_STEPS;
        public const int MaxReads = GeneratedLayout.DRELAY_MAX_READS;
        public const int TargetNameLen = GeneratedLayout.DRELAY_TARGET_NAME_LEN;
        public const int FieldNameLen = GeneratedLayout.DRELAY_FIELD_NAME_LEN;
        public const int LogLineLen = GeneratedLayout.DRELAY_LOG_LINE_LEN;
        public const int LogRingLines = GeneratedLayout.DRELAY_LOG_RING_LINES;
        public const int TraceRingRows = GeneratedLayout.DRELAY_TRACE_RING_ROWS;
        public const int FrameRingRows = GeneratedLayout.DRELAY_FRAME_RING_ROWS;

        public const uint CfgDryRun = 0x00000001;
        public const uint CfgFrameSample = 0x00000002;
        public const uint CfgFullTrace = 0x00000004;
        public const uint CfgSlotProbe = 0x00000008;
        // 0x00000010 (`CfgManagedRead`) is RETIRED, not merely unused: the managed frame
        // callback it asked for cannot work at all, because entering managed code from
        // the frame detour is an illegal reverse P/Invoke.  See DrelayHookShared.h.

        public const uint StJitDetoured = 0x00000001;
        public const uint StDiscoveryDone = 0x00000002;
        public const uint StPatched = 0x00000004;
        public const uint StRefused = 0x00000008;
        public const uint StOff = 0x00000010;

        public const uint SlotNone = 0xFFFFFFFF;

        /// <summary>
        /// The frame callback's arming gate; mirrors the <c>DRELAY_ARM_*</c> defines
        /// in <c>native/DrelayHookShared.h</c>.
        ///
        /// Publishing the arming decision rather than a boolean is what lets
        /// <c>summary.txt</c> tell "the offsets were checked and are wrong" apart
        /// from "there were no offsets to check".  The first is a finding about the
        /// build, the second is a missing prerequisite, and a single bit would make
        /// both read as "not armed".
        /// </summary>
        public const uint ArmWaiting = 0;
        public const uint ArmArmed = 1;
        public const uint ArmRefused = 2;
        public const uint ArmUnconfigured = 3;

        public const uint ReadF32 = 0;
        public const uint ReadF64 = 1;
        public const uint StepAdd = 0;
        public const uint StepLoadPtr = 1;

        public static string MapName(uint session) => "Local\\DrelayHook-" + session;
        public static string NativeReadyEventName(uint session) => "Local\\DrelayHook-NativeReady-" + session;
        public static string BootReadyEventName(uint session) => "Local\\DrelayHook-BootReady-" + session;
    }

    /// <summary>
    /// Index-to-field mapping and the small string helpers.  The eight-by-eight
    /// shape is written out rather than looped because the generated struct's
    /// eight fields are what pins the offsets; a computed offset here would
    /// reintroduce exactly the arithmetic the generator removed.
    /// </summary>
    public static class SharedLayout
    {
        public static DrelayTargetHash Target(ref DrelayShared block, int index)
        {
            switch (index)
            {
                case 0: return block.target0;
                case 1: return block.target1;
                case 2: return block.target2;
                case 3: return block.target3;
                case 4: return block.target4;
                case 5: return block.target5;
                case 6: return block.target6;
                case 7: return block.target7;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public static void SetTarget(ref DrelayShared block, int index, DrelayTargetHash value)
        {
            switch (index)
            {
                case 0: block.target0 = value; break;
                case 1: block.target1 = value; break;
                case 2: block.target2 = value; break;
                case 3: block.target3 = value; break;
                case 4: block.target4 = value; break;
                case 5: block.target5 = value; break;
                case 6: block.target6 = value; break;
                case 7: block.target7 = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public static DrelayFieldOffset FieldOffset(ref DrelayShared block, int index)
        {
            switch (index)
            {
                case 0: return block.offset0;
                case 1: return block.offset1;
                case 2: return block.offset2;
                case 3: return block.offset3;
                case 4: return block.offset4;
                case 5: return block.offset5;
                case 6: return block.offset6;
                case 7: return block.offset7;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public static void SetFieldOffset(ref DrelayShared block, int index, DrelayFieldOffset value)
        {
            switch (index)
            {
                case 0: block.offset0 = value; break;
                case 1: block.offset1 = value; break;
                case 2: block.offset2 = value; break;
                case 3: block.offset3 = value; break;
                case 4: block.offset4 = value; break;
                case 5: block.offset5 = value; break;
                case 6: block.offset6 = value; break;
                case 7: block.offset7 = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        /// <summary>Copies a fixed byte buffer out of a struct field.</summary>
        public static unsafe byte[] ReadFixed(byte* source, int count)
        {
            var result = new byte[count];
            Marshal.Copy((IntPtr)source, result, 0, count);
            return result;
        }

        /// <summary>
        /// Reads a NUL-terminated ASCII string out of a fixed buffer.  Decoding a
        /// copied array rather than a pointer into the struct keeps this
        /// independent of the field's marshalling attributes.
        /// </summary>
        public static unsafe string ReadFixedString(byte* source, int count)
        {
            byte[] raw = ReadFixed(source, count);
            int len = Array.IndexOf(raw, (byte)0);
            if (len < 0)
            {
                len = count;
            }
            return Encoding.ASCII.GetString(raw, 0, len);
        }

        /// <summary>
        /// Copies a string into a fixed-width buffer as Latin-1, truncating rather
        /// than throwing.  Latin-1 rather than UTF-8 on purpose: the native side
        /// reads these with `%s` and every character that matters here is ASCII,
        /// so a multi-byte sequence would be split at the buffer boundary and read
        /// as two wrong characters.
        /// </summary>
        public static void WriteAscii(byte[] destination, string text)
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

        /// <summary>
        /// The one check that the generated offsets describe the type the compiler
        /// actually built.  A generator bug and a compiler surprise both land
        /// here, named, before anything is patched.
        /// </summary>
        public static bool VerifyOffsets(out string complaint)
        {
            complaint = null;
            int size = Marshal.SizeOf<DrelayShared>();
            if (size != GeneratedLayout.DrelaySharedSize)
            {
                complaint = string.Format("DrelayShared is {0} bytes, generated as {1}",
                    size, GeneratedLayout.DrelaySharedSize);
                return false;
            }
            if (Marshal.OffsetOf<DrelayShared>(nameof(DrelayShared.targetCount)) != (IntPtr)GeneratedLayout.Off_targetCount)
            {
                complaint = "DrelayShared.targetCount is not at its generated offset";
                return false;
            }
            if (Marshal.OffsetOf<DrelayShared>(nameof(DrelayShared.mvid)) != (IntPtr)GeneratedLayout.Off_mvid)
            {
                complaint = "DrelayShared.mvid is not at its generated offset";
                return false;
            }
            if (Marshal.OffsetOf<DrelayShared>(nameof(DrelayShared.managedPlayerTypeHandle)) != (IntPtr)GeneratedLayout.Off_managedPlayerTypeHandle)
            {
                complaint = "DrelayShared.managedPlayerTypeHandle is not at its generated offset";
                return false;
            }
            if (Marshal.OffsetOf<DrelayShared>(nameof(DrelayShared.nativeStatus)) != (IntPtr)GeneratedLayout.Off_nativeStatus)
            {
                complaint = "DrelayShared.nativeStatus is not at its generated offset";
                return false;
            }
            if (Marshal.OffsetOf<DrelayShared>(nameof(DrelayShared.jitVtableInText)) != (IntPtr)GeneratedLayout.Off_jitVtableInText)
            {
                complaint = "DrelayShared.jitVtableInText is not at its generated offset";
                return false;
            }
            return true;
        }
    }
}
