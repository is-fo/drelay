using System.Runtime.InteropServices;
using System.Text;

namespace Drelay.Redirect;

/// <summary>
/// Answers "which process owns the socket that sent this packet?", and whether that process is one
/// this redirector must leave alone.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS THE LOOP GUARD, AND IT IS THE PART THAT MUST NOT BE WRONG.
/// </para>
/// <para>
/// The relay's own upstream dials are outbound TCP to the game's ports, so they match the packet
/// filter exactly as the client's dials do. Reflecting one of them back into the relay's listener
/// makes the relay a client of itself; each reflected connection makes another dial, and the cascade
/// recorded in docs/PROTOCOL.md §5 (16 000 sessions in five minutes) is the result. The right
/// discriminator is the process that owns the socket, not the source port: the previous redirector
/// pinned the relay's upstream dials to one source port (<c>upstreamBindPort</c>) and excluded that
/// port, which both serialised upstream dials - Windows allows one connection per local port - and
/// failed as soon as two sessions overlapped.
/// </para>
/// <para>
/// The owner comes from <c>GetExtendedTcpTable</c>, i.e. the table <c>netstat -o</c> prints. It is
/// queried when a flow is first seen (a SYN, or the first packet of a connection already in progress)
/// and cached for a couple of minutes; steady-state traffic costs a dictionary lookup. The answer is
/// authoritative at the moment it is asked because the socket exists before its first SYN is sent.
/// </para>
/// <para>
/// The caller treats "no answer" as "do not reflect". A packet that cannot be attributed is passed
/// through untouched: the client then reaches the real server directly and the proxy is simply not in
/// the path, which is a visible degradation rather than a self-connection storm.
/// </para>
/// </remarks>
internal sealed class LoopGuard
{
    private const int TableOwnerPidAll = 5;
    private const int AddressFamilyInet = 2;
    private const int RowSize = 24;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>TCP states whose rows should not decide ownership: the socket is already gone.</summary>
    private const uint StateTimeWait = 8;
    private const uint StateDeleteTcb = 12;

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);

    private readonly HashSet<int> _excludedPids = new();
    private readonly HashSet<string> _excludedImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, CachedOwner> _byLocalEndpoint = new();
    private readonly Dictionary<int, string> _imageByPid = new();

    internal LoopGuard(IEnumerable<int> excludedPids, IEnumerable<string> excludedImages)
    {
        _excludedPids.Add(Environment.ProcessId);
        foreach (int pid in excludedPids)
        {
            _excludedPids.Add(pid);
        }
        foreach (string image in excludedImages)
        {
            string name = Path.GetFileName(image.Trim());
            if (name.Length > 0)
            {
                _excludedImages.Add(name);
            }
        }
    }

    /// <summary>The exclusions, for the startup banner.</summary>
    internal string Describe()
    {
        var parts = new List<string>();
        if (_excludedImages.Count > 0)
        {
            parts.Add(string.Join(", ", _excludedImages.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)));
        }
        parts.Add("pid " + Environment.ProcessId);
        if (_excludedPids.Count > 1)
        {
            parts.Add("plus " + (_excludedPids.Count - 1) + " pid(s) given on the command line");
        }
        return string.Join(" and ", parts);
    }

    /// <summary>
    /// The process that owns the socket <paramref name="address"/>:<paramref name="port"/>, or 0 when
    /// Windows will not say.
    /// </summary>
    /// <param name="refresh">
    /// Re-read the TCP table even when the endpoint is cached. Set for a SYN, because a reused local
    /// port is a different connection owned by a different process.
    /// </param>
    internal int Owner(uint address, ushort port, bool refresh)
    {
        long key = ((long)address << 16) | port;
        if (!refresh && _byLocalEndpoint.TryGetValue(key, out CachedOwner cached)
            && DateTime.UtcNow - cached.ReadAt < CacheLifetime)
        {
            return cached.Pid;
        }

        if (!TryReadOwner(address, port, out int pid))
        {
            // Do not cache "unknown": the table is momentarily racy only for a socket being created,
            // and caching a zero would keep a real client unproxied for two minutes.
            return 0;
        }
        _byLocalEndpoint[key] = new CachedOwner(pid, DateTime.UtcNow);
        return pid;
    }

    /// <summary>Whether this redirector must pass packets owned by <paramref name="pid"/> through.</summary>
    internal bool IsExcluded(int pid)
    {
        if (pid <= 0 || _excludedPids.Contains(pid))
        {
            return true;
        }
        if (_excludedImages.Count == 0)
        {
            return false;
        }
        string? image = ImageName(pid);
        return image is null ? false : _excludedImages.Contains(image);
    }

    /// <summary>
    /// The executable name of a process, exposed for the offline selftest.
    /// </summary>
    /// <remarks>
    /// The exclusion check short-circuits on this process's own pid, so the image-name path - a
    /// P/Invoke into a function with a `W` suffix - would otherwise never be exercised without the
    /// driver. This process crashed on its first packet once because of exactly that, so it is now
    /// asked directly by <c>--selftest</c>.
    /// </remarks>
    internal string? ImageNameOf(int pid)
    {
        return ImageName(pid);
    }

    /// <summary>The executable's file name for <paramref name="pid"/>, or null when it is not readable.</summary>
    private string? ImageName(int pid)
    {
        if (_imageByPid.TryGetValue(pid, out string? cached))
        {
            return cached;
        }

        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var builder = new StringBuilder(1024);
            int size = builder.Capacity;
            // The W is not decoration: kernel32 exports QueryFullProcessImageNameW and A, never the
            // bare name, and this import is declared ExactSpelling so a missing suffix is a crash at
            // the first packet rather than a load-time error.
            if (!QueryFullProcessImageNameW(process, 0, builder, ref size))
            {
                return null;
            }
            string image = Path.GetFileName(builder.ToString(0, size));
            // Exact-name matching would miss the case Windows actually stores, so the answer is
            // cached per pid and compared case-insensitively by the caller's set.
            _imageByPid[pid] = image;
            return image;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>One pass over the IPv4 TCP table, looking for the socket's owning process.</summary>
    private static bool TryReadOwner(uint address, ushort port, out int pid)
    {
        pid = 0;
        int size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AddressFamilyInet, TableOwnerPidAll, 0);
        if (result != ErrorInsufficientBuffer || size <= 0)
        {
            return false;
        }

        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(table, ref size, false, AddressFamilyInet, TableOwnerPidAll, 0);
            if (result != 0)
            {
                return false;
            }

            int count = Marshal.ReadInt32(table);
            IntPtr row = table + 4;
            int fallback = 0;
            for (int i = 0; i < count; i++, row += RowSize)
            {
                uint localAddress = (uint)Marshal.ReadInt32(row, 4);
                uint localPort = (uint)Marshal.ReadInt32(row, 8);
                if (localAddress != address || Swap16((ushort)(localPort & 0xFFFF)) != port)
                {
                    continue;
                }
                uint state = (uint)Marshal.ReadInt32(row, 0);
                int owner = Marshal.ReadInt32(row, 20);
                if (state is not StateTimeWait and not StateDeleteTcb)
                {
                    pid = owner;
                    return true;
                }
                fallback = owner;
            }

            if (fallback != 0)
            {
                pid = fallback;
                return true;
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    private static ushort Swap16(ushort value)
    {
        return (ushort)((value >> 8) | (value << 8));
    }

    private const uint ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private readonly record struct CachedOwner(int Pid, DateTime ReadAt);
}
