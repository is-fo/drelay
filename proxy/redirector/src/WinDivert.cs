using System.Runtime.InteropServices;

namespace Drelay.Redirect;

/// <summary>How a captured packet is to be handled, encoded in <see cref="WindivertAddress.Flags"/>.</summary>
internal static class WindivertLayer
{
    internal const int Network = 0;
    internal const int NetworkForward = 1;
    internal const int Flow = 2;
    internal const int Socket = 3;
    internal const int Reflect = 4;
}

/// <summary>WinDivert shutdown modes for <see cref="WinDivert.WinDivertShutdown"/>.</summary>
internal static class WindivertShutdown
{
    internal const int Recv = 0x1;
    internal const int Send = 0x2;
    internal const int Both = 0x3;
}

/// <summary>WinDivert handle parameters, for reading the driver version.</summary>
internal static class WindivertParam
{
    internal const int VersionMajor = 3;
    internal const int VersionMinor = 4;
}

/// <summary>
/// A hand-written mirror of WinDivert 2.2's <c>WINDIVERT_ADDRESS</c>.
/// </summary>
/// <remarks>
/// <para>
/// The C struct is a bitfield followed by a union, which C# cannot express directly, so the layout is
/// declared field by field with explicit offsets. The sizes and offsets are exactly section 5.3 of
/// the WinDivert documentation: a 64-bit timestamp, then a 32-bit word of bitfields, then a reserved
/// 32-bit word, then a 64-byte union whose first two words are the interface index for the NETWORK
/// layer. The whole structure is 80 bytes, and it must be at least that large: WinDivert writes the
/// full structure through the pointer it is given, so a smaller one would be silently overrun.
/// </para>
/// <para>
/// Only the NETWORK layer fields are used here. The redirector needs the direction bit and the
/// interface index and nothing else.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 80)]
internal struct WindivertAddress
{
    /// <summary>Bit 17 of <see cref="Flags"/>: 1 for a packet this machine sends, 0 for one it receives.</summary>
    private const int OutboundBit = 17;

    /// <summary>Bit 18: 1 when the packet originates and terminates on this machine.</summary>
    private const int LoopbackBit = 18;

    /// <summary>Bit 20: 1 for an IPv6 packet.</summary>
    private const int IpV6Bit = 20;

    [FieldOffset(0)]
    public long Timestamp;

    /// <summary>Layer:8, Event:8, Sniffed:1, Outbound:1, Loopback:1, Impostor:1, IPv6:1, checksums, Reserved1.</summary>
    [FieldOffset(8)]
    public uint Flags;

    [FieldOffset(12)]
    public uint Reserved;

    /// <summary>The interface the packet arrived on (inbound) or is to be sent on (outbound).</summary>
    [FieldOffset(16)]
    public uint InterfaceIndex;

    [FieldOffset(20)]
    public uint SubInterfaceIndex;

    public bool Outbound
    {
        readonly get => (Flags & (1u << OutboundBit)) != 0;
        set => Flags = value ? Flags | (1u << OutboundBit) : Flags & ~(1u << OutboundBit);
    }

    public readonly bool Loopback => (Flags & (1u << LoopbackBit)) != 0;

    public readonly bool IpV6 => (Flags & (1u << IpV6Bit)) != 0;

    public readonly int Layer => (int)(Flags & 0xFF);
}

/// <summary>
/// The four WinDivert 2.2 entry points this program uses, bound directly to <c>WinDivert.dll</c>.
/// </summary>
/// <remarks>
/// The signatures follow section 5 of the WinDivert documentation. <c>WinDivertRecv</c> and
/// <c>WinDivertSend</c> take the packet as a byte array because the address of a pinned array is what
/// the C API wants; the array is pinned by the marshaller for the duration of the call only, which is
/// all WinDivert needs.
/// </remarks>
internal static class WinDivert
{
    internal static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true,
        CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertRecv(IntPtr handle, byte[] packet, uint packetLength,
        out uint receivedLength, ref WindivertAddress address);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertSend(IntPtr handle, byte[] packet, uint packetLength,
        out uint sentLength, ref WindivertAddress address);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertShutdown(IntPtr handle, int how);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertClose(IntPtr handle);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertGetParam(IntPtr handle, int param, out ulong value);

    /// <summary>Reads the driver's version, or <c>null</c> when it cannot be asked.</summary>
    internal static string? Version(IntPtr handle)
    {
        if (WinDivertGetParam(handle, WindivertParam.VersionMajor, out ulong major)
            && WinDivertGetParam(handle, WindivertParam.VersionMinor, out ulong minor))
        {
            return major + "." + minor;
        }
        return null;
    }
}
