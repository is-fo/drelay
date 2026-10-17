namespace Drelay.Redirect;

/// <summary>
/// A rate fuse on new reflected connections.
/// </summary>
/// <remarks>
/// The loop guard is supposed to make a self-connection impossible, and this is the belt to its
/// braces. The failure it exists for is not subtle: one misattributed packet makes the relay dial
/// itself, which makes another, and the previous redirector produced 16 000 sessions in five minutes
/// (docs/PROTOCOL.md §5). Legitimate traffic on these two ports is a handful of connections per
/// minute, so a burst of new connections within one second is a fault, not load. When the fuse trips
/// the packet is passed through untouched: the client reaches the real server directly, the run is
/// visibly unproxied, and nothing loops.
/// </remarks>
internal sealed class Fuse
{
    private readonly int _limit;
    private long _windowStart;
    private int _count;

    /// <param name="limit">New connections per second, or 0 for no limit.</param>
    internal Fuse(int limit)
    {
        _limit = limit;
    }

    /// <summary>Whether one more new connection may be reflected right now.</summary>
    internal bool Allow()
    {
        if (_limit <= 0)
        {
            return true;
        }
        long now = Environment.TickCount64;
        if (now - _windowStart >= 1000)
        {
            _windowStart = now;
            _count = 0;
        }
        _count++;
        return _count <= _limit;
    }
}
