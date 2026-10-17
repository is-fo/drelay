using System.Buffers.Binary;

namespace Drelay.Redirect;

/// <summary>Why a captured frame is not a TCP/IPv4 packet this program can act on.</summary>
internal enum ParseResult
{
    Ok,
    NotIpv4,
    NotTcp,
    /// <summary>An IP fragment other than the first: it carries no TCP header to read.</summary>
    Fragmented,
    Malformed,
}

/// <summary>Header facts about one captured packet, in the offsets needed to rewrite it in place.</summary>
internal readonly struct Packet
{
    internal readonly int IpHeaderLength;
    internal readonly int TcpOffset;
    internal readonly int TcpSegmentLength;
    internal readonly ushort SourcePort;
    internal readonly ushort DestinationPort;

    /// <summary>
    /// The source address as the four bytes in the IP header read as a little-endian word.
    /// </summary>
    /// <remarks>
    /// This looks like a byte-order mistake and is not. The Windows TCP table
    /// (<c>MIB_TCPROW_OWNER_PID.dwLocalAddr</c>) stores the address in network order, and it is read
    /// here by the same little-endian reader, so both sides compare raw bytes and a comparison
    /// between them is an address comparison. Nothing else uses these fields; the checksums are
    /// computed from the bytes in the packet.
    /// </remarks>
    internal readonly uint SourceAddress;

    /// <summary>The destination address, in the same representation as <see cref="SourceAddress"/>.</summary>
    internal readonly uint DestinationAddress;

    internal readonly bool Syn;
    internal readonly bool Ack;

    internal Packet(int ipHeaderLength, int tcpOffset, int tcpSegmentLength, ushort sourcePort,
        ushort destinationPort, uint sourceAddress, uint destinationAddress, bool syn, bool ack)
    {
        IpHeaderLength = ipHeaderLength;
        TcpOffset = tcpOffset;
        TcpSegmentLength = tcpSegmentLength;
        SourcePort = sourcePort;
        DestinationPort = destinationPort;
        SourceAddress = sourceAddress;
        DestinationAddress = destinationAddress;
        Syn = syn;
        Ack = ack;
    }
}

/// <summary>
/// Reads and rewrites a captured IPv4/TCP packet.
/// </summary>
/// <remarks>
/// <para>
/// This is the only part of the redirector that touches bytes, and it is the part that can be tested
/// without a driver: <c>--selftest</c> runs it over synthetic packets and checks the checksums by
/// verifying that they sum to zero afterwards. That matters because a packet with a bad TCP checksum
/// is silently discarded by the receiving stack, which looks exactly like "the redirect did nothing".
/// </para>
/// <para>
/// THE TRANSFORMATION IS A REFLECTION, NOT A DESTINATION REWRITE. An outbound packet re-injected with
/// a new destination is not re-routed - it has already been routed when WinDivert sees it - so
/// rewriting <c>DstAddr</c> to 127.0.0.1, or to this machine's own LAN address, sends it out of the
/// card and nowhere near a local listener. That is the failure recorded in docs/PROTOCOL.md §5
/// (<c>seen=21 redirected=21</c> and zero relay sessions). Swapping the two addresses and clearing
/// the outbound flag instead hands the packet back to the stack as *received traffic*, which the stack
/// delivers to a local listener on the destination port - WinDivert's own <c>streamdump</c> sample
/// does exactly this to divert a stream into a local proxy. The ports are never changed: the swap
/// carries the client's ephemeral port to the relay and the relay's listen port back to the client,
/// which is what makes the two legs a single connection as far as each end is concerned.
/// </para>
/// </remarks>
internal static class Packets
{
    private const int IpV4MinimumHeader = 20;
    private const int TcpMinimumHeader = 20;
    private const byte ProtocolTcp = 6;

    /// <summary>Reads the headers of <paramref name="frame"/> into <paramref name="packet"/>.</summary>
    internal static ParseResult TryRead(ReadOnlySpan<byte> frame, out Packet packet)
    {
        packet = default;
        if (frame.Length < IpV4MinimumHeader)
        {
            return ParseResult.Malformed;
        }
        if (frame[0] >> 4 != 4)
        {
            return ParseResult.NotIpv4;
        }

        int headerLength = (frame[0] & 0x0F) * 4;
        if (headerLength < IpV4MinimumHeader || frame.Length < headerLength)
        {
            return ParseResult.Malformed;
        }
        if (frame[9] != ProtocolTcp)
        {
            return ParseResult.NotTcp;
        }

        // The IP total length is the authority, not the captured buffer: WinDivert hands over the
        // packet as it is on the wire, but a caller of this class may pass a larger buffer.
        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(frame[2..]);
        if (totalLength < headerLength + TcpMinimumHeader || totalLength > frame.Length)
        {
            return ParseResult.Malformed;
        }

        // An offset or a "more fragments" bit means this is not a whole TCP header. Only the first
        // fragment carries one, and reassembly is not this program's job: such a packet is passed
        // through untouched rather than mangled.
        ushort fragment = BinaryPrimitives.ReadUInt16BigEndian(frame[6..]);
        if ((fragment & 0x3FFF) != 0)
        {
            return ParseResult.Fragmented;
        }

        int tcpOffset = headerLength;
        int dataOffset = (frame[tcpOffset + 12] >> 4) * 4;
        if (dataOffset < TcpMinimumHeader || tcpOffset + dataOffset > totalLength)
        {
            return ParseResult.Malformed;
        }

        ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(frame[(tcpOffset + 0)..]);
        ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(frame[(tcpOffset + 2)..]);
        byte flags = frame[tcpOffset + 13];

        packet = new Packet(
            headerLength,
            tcpOffset,
            totalLength - tcpOffset,
            sourcePort,
            destinationPort,
            BinaryPrimitives.ReadUInt32LittleEndian(frame[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(frame[16..]),
            (flags & 0x02) != 0,
            (flags & 0x10) != 0);
        return ParseResult.Ok;
    }

    /// <summary>
    /// Swaps the source and destination addresses and recomputes both checksums, in place.
    /// </summary>
    /// <remarks>
    /// The caller flips the packet's direction in the WinDivert address as well; the two changes go
    /// together, and neither is useful alone.
    /// </remarks>
    internal static void Reflect(Span<byte> frame, in Packet packet)
    {
        for (int i = 0; i < 4; i++)
        {
            (frame[12 + i], frame[16 + i]) = (frame[16 + i], frame[12 + i]);
        }

        // IPv4 header checksum: zero the field, sum the header, write the complement.
        frame[10] = 0;
        frame[11] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(frame[10..], Checksum(frame[..packet.IpHeaderLength]));

        // TCP checksum, over the pseudo header and the segment. The pseudo header is where the two
        // swapped addresses appear, so it cannot be computed incrementally by a constant delta the
        // way a port-only rewrite could.
        int checksumOffset = packet.TcpOffset + 16;
        frame[checksumOffset] = 0;
        frame[checksumOffset + 1] = 0;

        uint sum = 0;
        sum += Word(frame[12], frame[13]);
        sum += Word(frame[14], frame[15]);
        sum += Word(frame[16], frame[17]);
        sum += Word(frame[18], frame[19]);
        sum += ProtocolTcp;                                 // the zero byte contributes nothing
        sum += (uint)packet.TcpSegmentLength;
        sum += SumWords(frame.Slice(packet.TcpOffset, packet.TcpSegmentLength));
        BinaryPrimitives.WriteUInt16BigEndian(frame[checksumOffset..], (ushort)~Fold(sum));
    }

    /// <summary>The one's-complement checksum of <paramref name="data"/>.</summary>
    internal static ushort Checksum(ReadOnlySpan<byte> data)
    {
        return (ushort)~Fold(SumWords(data));
    }

    /// <summary>The 16-bit word formed by two bytes in network order.</summary>
    private static uint Word(byte high, byte low)
    {
        return (uint)((high << 8) | low);
    }

    private static uint SumWords(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 1 < data.Length; i += 2)
        {
            sum += Word(data[i], data[i + 1]);
        }
        if (i < data.Length)
        {
            // An odd trailing byte is padded with a zero on the right, as the RFCs require.
            sum += Word(data[i], 0);
        }
        return sum;
    }

    /// <summary>Folds the carries of a one's-complement sum into 16 bits.</summary>
    private static uint Fold(uint sum)
    {
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return sum;
    }
}
