// MapProbe.cs -- reproduces the launcher's CreateFileMapping call in isolation.
//
// WHY THIS EXISTS
// ---------------
// The launcher failed `CreateFileMapping(Local\DrelayHook-<pid>)` with error 8
// (ERROR_NOT_ENOUGH_MEMORY) for a 431 KB pagefile-backed section, on a machine
// with gigabytes free, while an identical call from another process succeeded.
// "Not enough memory" for 431 KB is not a plausible diagnosis, so the difference
// had to be measured rather than reasoned about.
//
// This program is that measurement: the same call, the same parameters, with the
// process's own limits printed alongside the result.  It is kept because the
// answer -- a per-process *commit* limit imposed by the job object the harness
// runs under, which is charged in full at section-creation time -- is the kind of
// thing that is rediscovered expensively.
//
// Usage: MapProbe [size] [name] [view]
//   size  bytes for the section's maximum (default 440864)
//   name  the section name (default Local\DrelayMapProbe)
//   view  pass "view" to also MapViewOfFile, which is where a commit charge is
//         actually paid for an uncommitted section

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

internal static class MapProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x100;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x200;
    private const uint JOB_OBJECT_LIMIT_JOB_TIME = 0x4;
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attributes, uint protect,
                                                   uint maximumSizeHigh, uint maximumSizeLow, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint high, uint low, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr address);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info,
                                                         uint length, out uint returnedLength);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX counters,
                                                    uint size);

    private static void Main(string[] args)
    {
        uint size = args.Length > 0 && uint.TryParse(args[0], out uint parsed) ? parsed : 440864u;
        string name = args.Length > 1 ? args[1] : "Local\\DrelayMapProbe";
        bool mapView = args.Length > 2 && args[2] == "view";

        Console.WriteLine($"requested size : {size} bytes");
        Console.WriteLine($"name           : {name}");

        var counters = new PROCESS_MEMORY_COUNTERS_EX();
        counters.cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>();
        if (GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb))
        {
            Console.WriteLine($"pagefile usage : {counters.PagefileUsage.ToUInt64()} bytes " +
                              $"(peak {counters.PeakPagefileUsage.ToUInt64()})");
        }

        bool inJob = false;
        if (IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob))
        {
            Console.WriteLine($"in a job object: {inJob}");
            if (inJob)
            {
                int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (QueryInformationJobObject(IntPtr.Zero, JobObjectExtendedLimitInformation,
                                                  buffer, (uint)length, out uint returned))
                    {
                        var info = Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(buffer);
                        Console.WriteLine($"  limits flags  : 0x{info.LimitFlags:X8}" +
                                          $"{(info.LimitFlags == 0 ? "  (no limits set)" : "")}");
                        if ((info.LimitFlags & JOB_OBJECT_LIMIT_PROCESS_MEMORY) != 0)
                        {
                            Console.WriteLine($"  process limit : {info.ProcessMemoryLimit.ToUInt64()} bytes");
                        }
                        if ((info.LimitFlags & JOB_OBJECT_LIMIT_JOB_MEMORY) != 0)
                        {
                            Console.WriteLine($"  job limit     : {info.JobMemoryLimit.ToUInt64()} bytes");
                        }
                        Console.WriteLine($"  process used  : {info.PeakProcessMemoryUsed.ToUInt64()} bytes (peak)");
                        Console.WriteLine($"  job used      : {info.PeakJobMemoryUsed.ToUInt64()} bytes (peak)");
                        Console.WriteLine($"  active procs  : {info.ActiveProcessLimit}");
                    }
                    else
                    {
                        Console.WriteLine($"  QueryInformationJobObject failed: " +
                                          $"{Marshal.GetLastWin32Error()}");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, size, name);
        int err = Marshal.GetLastWin32Error();
        if (mapping == IntPtr.Zero)
        {
            Console.WriteLine($"CreateFileMapping(size={size}) FAILED, win32 error {err} " +
                              $"({new Win32Exception(err).Message})");

            // Fall back to a section with no declared maximum.  A pagefile-backed
            // section with a zero maximum is valid: it grows as pages are touched,
            // and it is charged the same way.  If this succeeds where the sized
            // form failed, the failure is about the declared maximum and not about
            // available memory, which is the distinction worth recording.
            Console.WriteLine("retrying with maximum size 0 (grows on demand)...");
            mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, 0, name);
            err = Marshal.GetLastWin32Error();
            if (mapping == IntPtr.Zero)
            {
                Console.WriteLine($"CreateFileMapping(size=0) FAILED, win32 error {err} " +
                                  $"({new Win32Exception(err).Message})");
                return;
            }
            Console.WriteLine($"CreateFileMapping(size=0) OK, handle 0x{mapping.ToInt64():X}");
            size = 0;
        }
        else
        {
            Console.WriteLine($"CreateFileMapping OK, handle 0x{mapping.ToInt64():X}");
        }

        if (mapView)
        {
            IntPtr view = MapViewOfFile(mapping, 0x000F001F, 0, 0, UIntPtr.Zero);
            err = Marshal.GetLastWin32Error();
            if (view == IntPtr.Zero)
            {
                Console.WriteLine($"MapViewOfFile FAILED, win32 error {err} " +
                                  $"({new Win32Exception(err).Message})");
            }
            else
            {
                Console.WriteLine($"MapViewOfFile OK, view 0x{view.ToInt64():X} " +
                                  "(the commit charge is paid here for a pagefile section)");
                UnmapViewOfFile(view);
            }
        }
        CloseHandle(mapping);
    }
}
