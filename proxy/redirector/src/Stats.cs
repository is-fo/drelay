using System.Text;

namespace Drelay.Redirect;

/// <summary>
/// The counters the redirector prints, and the reason each one exists.
/// </summary>
/// <remarks>
/// These are the numbers that diagnosed the previous redirector's failures, and they are reproduced
/// because the same questions are asked of any packet-level redirect (docs/PROTOCOL.md §5, "Why the
/// packet redirect was abandoned"):
/// <list type="bullet">
///   <item><c>seen</c> at zero means the filter is not matching at all - check the port list;</item>
///   <item><c>seen</c> rising with <c>reflected</c> at zero means the loop guard is refusing every
///       packet, which is a process-ownership problem, not a filter problem;</item>
///   <item><c>reflected</c> rising with no session in the relay means the reflected packet is not
///       being delivered locally - a checksum or interface problem;</item>
///   <item><c>selfMatch</c> rising is the loop guard doing its job: the relay's own upstream dials;</item>
///   <item><c>unknownOwner</c> rising means packets are being passed through because Windows would
///       not name their process, so the proxy is not in the path for them;</item>
///   <item><c>reinjectFailed</c> is a WinDivert refusal to take a packet back, which loses traffic.</item>
/// </list>
/// </remarks>
internal sealed class Stats
{
    private long _seen;
    private long _reflected;
    private long _returned;
    private long _selfMatch;
    private long _unknownOwner;
    private long _noMatch;
    private long _notTcp;
    private long _fragmented;
    private long _malformed;
    private long _passThrough;
    private long _reinjectFailed;
    private long _fuseTripped;
    private long _receiveError;

    internal void SawPacket() => Interlocked.Increment(ref _seen);

    internal void Reflected() => Interlocked.Increment(ref _reflected);

    internal void Returned() => Interlocked.Increment(ref _returned);

    internal void SelfMatch() => Interlocked.Increment(ref _selfMatch);

    internal void UnknownOwner() => Interlocked.Increment(ref _unknownOwner);

    internal void NoMatch() => Interlocked.Increment(ref _noMatch);

    internal void PassedThrough() => Interlocked.Increment(ref _passThrough);

    internal void ReinjectFailed() => Interlocked.Increment(ref _reinjectFailed);

    internal void FuseTripped() => Interlocked.Increment(ref _fuseTripped);

    internal void ReceiveError() => Interlocked.Increment(ref _receiveError);

    internal void Rejected(ParseResult result)
    {
        switch (result)
        {
            case ParseResult.NotTcp:
            case ParseResult.NotIpv4:
                Interlocked.Increment(ref _notTcp);
                break;
            case ParseResult.Fragmented:
                Interlocked.Increment(ref _fragmented);
                break;
            default:
                Interlocked.Increment(ref _malformed);
                break;
        }
    }

    /// <summary>The counters as one line, in the shape the previous redirector's log used.</summary>
    internal string Format()
    {
        var text = new StringBuilder("[stats]");
        text.Append(" seen=").Append(Interlocked.Read(ref _seen));
        text.Append(" reflected=").Append(Interlocked.Read(ref _reflected));
        text.Append(" returned=").Append(Interlocked.Read(ref _returned));
        text.Append(" selfMatch=").Append(Interlocked.Read(ref _selfMatch));
        text.Append(" unknownOwner=").Append(Interlocked.Read(ref _unknownOwner));
        text.Append(" noMatch=").Append(Interlocked.Read(ref _noMatch));
        text.Append(" passThrough=").Append(Interlocked.Read(ref _passThrough));
        text.Append(" notTcp=").Append(Interlocked.Read(ref _notTcp));
        text.Append(" fragmented=").Append(Interlocked.Read(ref _fragmented));
        text.Append(" malformed=").Append(Interlocked.Read(ref _malformed));
        text.Append(" fuseTripped=").Append(Interlocked.Read(ref _fuseTripped));
        text.Append(" reinjectFailed=").Append(Interlocked.Read(ref _reinjectFailed));
        text.Append(" recvError=").Append(Interlocked.Read(ref _receiveError));
        return text.ToString();
    }

    /// <summary>Everything zero: used to decide whether the interval produced anything to print.</summary>
    internal bool IsEmpty()
    {
        return Interlocked.Read(ref _seen) == 0
               && Interlocked.Read(ref _reflected) == 0
               && Interlocked.Read(ref _returned) == 0
               && Interlocked.Read(ref _selfMatch) == 0
               && Interlocked.Read(ref _unknownOwner) == 0
               && Interlocked.Read(ref _noMatch) == 0
               && Interlocked.Read(ref _passThrough) == 0
               && Interlocked.Read(ref _notTcp) == 0
               && Interlocked.Read(ref _fragmented) == 0
               && Interlocked.Read(ref _malformed) == 0
               && Interlocked.Read(ref _fuseTripped) == 0
               && Interlocked.Read(ref _reinjectFailed) == 0
               && Interlocked.Read(ref _receiveError) == 0;
    }
}
