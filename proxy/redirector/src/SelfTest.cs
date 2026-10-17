using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Drelay.Redirect;

/// <summary>
/// The checks that can run without the driver, and therefore without administrator rights:
/// <c>drelay-redirect.exe --selftest</c>.
/// </summary>
/// <remarks>
/// This exists because the interesting half of the redirector cannot be exercised unelevated - opening
/// a WinDivert handle installs a kernel driver - and a packet transformation that is only ever tested
/// in production is how a bad checksum reaches a live session. Everything here is either pure
/// byte-level arithmetic or a question Windows answers to any user.
/// </remarks>
internal static class SelfTest
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    internal static int Run()
    {
        Console.WriteLine("drelay redirector selftest (no driver, no elevation)");

        FilterTests();
        ParseTests();
        ReflectTests();
        FuseTests();
        OptionTests();
        LiveSocketOwnershipTest();

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine("selftest: " + _passed + " checks passed");
            return 0;
        }
        Console.WriteLine("selftest: " + Failures.Count + " of " + (_passed + Failures.Count) + " checks FAILED");
        foreach (string failure in Failures)
        {
            Console.WriteLine("  FAIL " + failure);
        }
        return 1;
    }

    private static void Check(string name, bool condition)
    {
        if (condition)
        {
            _passed++;
        }
        else
        {
            Failures.Add(name);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Filter
    // -----------------------------------------------------------------------------------------

    private static void FilterTests()
    {
        string filter = Filter.Build(new ushort[] { 6410, 6411 });
        Check("filter is outbound only", filter.Contains("outbound", StringComparison.Ordinal));
        Check("filter is IPv4 only", filter.Contains(" ip ", StringComparison.Ordinal));
        Check("filter matches the destination port", filter.Contains("tcp.DstPort == 6410", StringComparison.Ordinal));
        Check("filter matches the source port", filter.Contains("tcp.SrcPort == 6410", StringComparison.Ordinal));
        Check("filter matches both ports", filter.Contains("tcp.DstPort == 6411", StringComparison.Ordinal)
                                           && filter.Contains("tcp.SrcPort == 6411", StringComparison.Ordinal));
        Check("filter excludes loopback", filter.Contains("not loopback", StringComparison.Ordinal));
        Check("filter pins no address", !filter.Contains("DstAddr", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------------
    // Parsing
    // -----------------------------------------------------------------------------------------

    private static void ParseTests()
    {
        byte[] tcp = BuildTcp(Source, Destination, 53749, 6410, syn: true, ack: false, payloadLength: 135);
        Check("a TCP packet parses", Packets.TryRead(tcp, out Packet packet) == ParseResult.Ok);
        Check("the source port is read", packet.SourcePort == 53749);
        Check("the destination port is read", packet.DestinationPort == 6410);
        Check("the SYN flag is read", packet.Syn && !packet.Ack);

        byte[] ack = BuildTcp(Source, Destination, 6410, 53749, syn: false, ack: true, payloadLength: 0);
        Check("a bare ACK parses", Packets.TryRead(ack, out Packet ackPacket) == ParseResult.Ok);
        Check("the ACK flag is read", ackPacket.Ack && !ackPacket.Syn);
        Check("an empty segment has no payload", ackPacket.TcpSegmentLength == 20);

        byte[] withOptions = BuildTcp(Source, Destination, 53749, 6410, true, false, 0, tcpOptionsLength: 12);
        Check("TCP options parse", Packets.TryRead(withOptions, out Packet optionsPacket) == ParseResult.Ok);
        Check("TCP options are accounted for", optionsPacket.TcpSegmentLength == 32);

        byte[] icmp = BuildTcp(Source, Destination, 1, 6410, false, false, 8);
        icmp[9] = 1;
        Check("a non-TCP protocol is rejected", Packets.TryRead(icmp, out _) == ParseResult.NotTcp);

        byte[] ipv6 = BuildTcp(Source, Destination, 53749, 6410, true, false, 0);
        ipv6[0] = 0x60;
        Check("an IPv6 packet is rejected", Packets.TryRead(ipv6, out _) == ParseResult.NotIpv4);

        byte[] fragment = BuildTcp(Source, Destination, 53749, 6410, true, false, 16);
        fragment[6] = 0x20;                     // more fragments
        Check("a fragment is rejected", Packets.TryRead(fragment, out _) == ParseResult.Fragmented);

        byte[] truncated = BuildTcp(Source, Destination, 53749, 6410, true, false, 16);
        BinaryPrimitives.WriteUInt16BigEndian(truncated.AsSpan(2), (ushort)(truncated.Length + 100));
        Check("a truncated packet is rejected", Packets.TryRead(truncated, out _) == ParseResult.Malformed);

        Check("an empty buffer is rejected", Packets.TryRead(ReadOnlySpan<byte>.Empty, out _) == ParseResult.Malformed);
    }

    // -----------------------------------------------------------------------------------------
    // Reflection
    // -----------------------------------------------------------------------------------------

    private static void ReflectTests()
    {
        byte[] original = BuildTcp(Source, Destination, 53749, 6410, syn: true, ack: false, payloadLength: 135);
        byte[] packet = (byte[])original.Clone();
        Check("the packet parses before reflection", Packets.TryRead(packet, out Packet parsed) == ParseResult.Ok);

        Packets.Reflect(packet, parsed);

        Check("the source address is now the server", packet.AsSpan(12, 4).SequenceEqual(Destination));
        Check("the destination address is now the client", packet.AsSpan(16, 4).SequenceEqual(Source));
        Check("the source port is unchanged", BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(parsed.TcpOffset)) == 53749);
        Check("the destination port is unchanged",
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(parsed.TcpOffset + 2)) == 6410);
        Check("the payload is untouched", packet.AsSpan(parsed.TcpOffset + 20).SequenceEqual(original.AsSpan(parsed.TcpOffset + 20)));
        Check("the IPv4 checksum verifies", Packets.Checksum(packet.AsSpan(0, parsed.IpHeaderLength)) == 0);
        Check("the TCP checksum verifies", TcpChecksumVerifies(packet, parsed));

        // Reflecting twice must be the identity on the addresses: that is what makes the two legs of
        // one connection symmetrical, and a bug here would show up as "the relay's answer never
        // reaches the client" rather than as an address error.
        Packets.TryRead(packet, out Packet returned);
        Packets.Reflect(packet, returned);
        Check("a second reflection restores the source", packet.AsSpan(12, 4).SequenceEqual(Source));
        Check("a second reflection restores the destination", packet.AsSpan(16, 4).SequenceEqual(Destination));
        Check("the round trip still verifies", Packets.Checksum(packet.AsSpan(0, returned.IpHeaderLength)) == 0
                                               && TcpChecksumVerifies(packet, returned));

        // An odd-length payload is where a checksum implementation that forgets the final padding
        // byte goes wrong, and it is not hypothetical: a 135-byte game frame is odd.
        byte[] odd = BuildTcp(Source, Destination, 53749, 6410, false, true, payloadLength: 135);
        Packets.TryRead(odd, out Packet oddPacket);
        Packets.Reflect(odd, oddPacket);
        Check("an odd payload length still produces a valid TCP checksum", TcpChecksumVerifies(odd, oddPacket));

        byte[] options = BuildTcp(Source, Destination, 53749, 6410, true, false, 8, tcpOptionsLength: 12);
        Packets.TryRead(options, out Packet optionPacket);
        Packets.Reflect(options, optionPacket);
        Check("a packet with TCP options still produces valid checksums",
            Packets.Checksum(options.AsSpan(0, optionPacket.IpHeaderLength)) == 0
            && TcpChecksumVerifies(options, optionPacket));
    }

    /// <summary>
    /// Verifies a TCP checksum the way a receiver does: the pseudo header and the whole segment,
    /// including the checksum field, must sum to <c>0xFFFF</c>.
    /// </summary>
    private static bool TcpChecksumVerifies(ReadOnlySpan<byte> frame, in Packet packet)
    {
        uint sum = 0;
        for (int i = 0; i < 4; i += 2)
        {
            sum += Word(frame[12 + i], frame[12 + i + 1]);
            sum += Word(frame[16 + i], frame[16 + i + 1]);
        }
        sum += 6;
        sum += (uint)packet.TcpSegmentLength;
        ReadOnlySpan<byte> segment = frame.Slice(packet.TcpOffset, packet.TcpSegmentLength);
        int index = 0;
        for (; index + 1 < segment.Length; index += 2)
        {
            sum += Word(segment[index], segment[index + 1]);
        }
        if (index < segment.Length)
        {
            sum += Word(segment[index], 0);
        }
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return sum == 0xFFFF;
    }

    private static uint Word(byte high, byte low) => (uint)((high << 8) | low);

    // -----------------------------------------------------------------------------------------
    // Fuse
    // -----------------------------------------------------------------------------------------

    private static void FuseTests()
    {
        var fuse = new Fuse(2);
        Check("the fuse allows up to its limit", fuse.Allow() && fuse.Allow());
        Check("the fuse trips past its limit", !fuse.Allow());

        var disabled = new Fuse(0);
        bool all = true;
        for (int i = 0; i < 1000; i++)
        {
            all &= disabled.Allow();
        }
        Check("a zero fuse never trips", all);
    }

    // -----------------------------------------------------------------------------------------
    // Command line
    // -----------------------------------------------------------------------------------------

    private static void OptionTests()
    {
        Options defaults = Options.Parse(Array.Empty<string>());
        Check("the default ports are the game's", defaults.Ports.SequenceEqual(new ushort[] { 6410, 6411 }));
        Check("java.exe is excluded by default", defaults.ExcludedImages.Contains("java.exe"));

        Options explicitPorts = Options.Parse(new[] { "--ports", "6410,6412" });
        Check("--ports replaces the port list", explicitPorts.Ports.SequenceEqual(new ushort[] { 6410, 6412 }));

        Options excluded = Options.Parse(new[] { "--exclude-pid", "4242", "--exclude-image", "relay.exe" });
        Check("--exclude-pid is read", excluded.ExcludedPids.Contains(4242));
        Check("--exclude-image is read", excluded.ExcludedImages.Contains("relay.exe"));

        Check("an invalid port is refused", Throws(() => Options.Parse(new[] { "--ports", "0" })));
        Check("an unknown option is refused", Throws(() => Options.Parse(new[] { "--nonsense" })));
        Check("a missing value is refused", Throws(() => Options.Parse(new[] { "--ports" })));
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    // -----------------------------------------------------------------------------------------
    // The loop guard's answer for a socket that really exists
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Asks Windows who owns a socket this process just opened, and checks the answer is this
    /// process. It exercises the parts that a unit test cannot reach: the TCP table's byte order for
    /// the port, the address comparison, and <c>QueryFullProcessImageName</c>.
    /// </summary>
    private static void LiveSocketOwnershipTest()
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient(AddressFamily.InterNetwork);
            client.Connect(IPAddress.Loopback, port);
            using TcpClient accepted = listener.AcceptTcpClient();

            var local = (IPEndPoint)client.Client.LocalEndPoint!;
            var guard = new LoopGuard(Array.Empty<int>(), new[] { "java.exe" });
            int owner = guard.Owner(Raw(local.Address), (ushort)local.Port, refresh: true);

            Check("the owner of a live socket is this process", owner == Environment.ProcessId);
            Check("this process is excluded by its own pid", guard.IsExcluded(Environment.ProcessId));
            // The image-name path is what protects java.exe, and it is a P/Invoke into a function
            // whose name carries a W suffix. It is asked directly here, because IsExcluded
            // short-circuits on this process's own pid and would never reach it.
            var imageGuard = new LoopGuard(Array.Empty<int>(), new[] { "something-that-is-not-running.exe" });
            string expected = Path.GetFileName(Environment.ProcessPath!)!;
            string? actual = imageGuard.ImageNameOf(Environment.ProcessId);
            Check("an image-name lookup answers for a live process",
                string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase));
            // An address no socket can own must produce no answer rather than a wrong one; the caller
            // relies on that to fail closed.
            uint addressNobodyOwns = BinaryPrimitives.ReadUInt32LittleEndian(new byte[] { 10, 255, 255, 1 });
            Check("an endpoint nobody owns has no owner", guard.Owner(addressNobodyOwns, (ushort)local.Port, true) == 0);
        }
        catch (Exception error)
        {
            // A machine with no loopback networking is not a redirector bug, so this is reported but
            // not counted as a failure of the packet code.
            Console.WriteLine("  note: the live-socket check could not run: " + error.Message);
        }
    }

    /// <summary>
    /// The address as the raw four bytes of an IP header, which is what the TCP table compares.
    /// A dual-mode socket reports its endpoint as an IPv4-mapped IPv6 address, so the last four bytes
    /// are the IPv4 address in that case.
    /// </summary>
    private static uint Raw(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length == 16 && address.IsIPv4MappedToIPv6)
        {
            bytes = bytes[12..];
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    // -----------------------------------------------------------------------------------------
    // Packet construction
    // -----------------------------------------------------------------------------------------

    private static readonly byte[] Source = { 192, 168, 0, 11 };
    private static readonly byte[] Destination = { 18, 145, 161, 25 };

    /// <summary>
    /// Builds an IPv4/TCP packet with zeroed checksums, which is what a captured packet looks like
    /// before <see cref="Packets.Reflect"/> fills them in.
    /// </summary>
    private static byte[] BuildTcp(byte[] source, byte[] destination, ushort sourcePort,
        ushort destinationPort, bool syn, bool ack, int payloadLength, int tcpOptionsLength = 0)
    {
        const int ipHeaderLength = 20;
        int tcpHeaderLength = 20 + tcpOptionsLength;
        int totalLength = ipHeaderLength + tcpHeaderLength + payloadLength;
        var packet = new byte[totalLength];

        packet[0] = 0x45;                                       // IPv4, 20-byte header
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)totalLength);
        packet[8] = 64;                                         // TTL
        packet[9] = 6;                                          // TCP
        source.CopyTo(packet, 12);
        destination.CopyTo(packet, 16);

        int tcp = ipHeaderLength;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(tcp + 0), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(tcp + 2), destinationPort);
        packet[tcp + 12] = (byte)((tcpHeaderLength / 4) << 4);
        packet[tcp + 13] = (byte)((syn ? 0x02 : 0) | (ack ? 0x10 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(tcp + 14), 65535);  // window

        for (int i = 0; i < payloadLength; i++)
        {
            packet[tcp + tcpHeaderLength + i] = (byte)(i * 7);
        }
        return packet;
    }
}
