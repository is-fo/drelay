using System.Runtime.InteropServices;

namespace Drelay.Redirect;

/// <summary>
/// The packet loop: read, decide, reflect or pass through.
/// </summary>
/// <remarks>
/// <para>
/// The decision is a two-line rule followed by one guard:
/// </para>
/// <list type="number">
///   <item>a packet whose DESTINATION port is one of ours is the client talking to the game service,
///       and is reflected to the relay's listener;</item>
///   <item>a packet whose SOURCE port is one of ours is the relay's listening socket answering, and is
///       reflected back to the client;</item>
///   <item>the guard applies only to the first case, because the relay's own upstream dials are
///       outbound packets addressed to the same destination ports. They are told apart by owning
///       process, not by port (see <see cref="LoopGuard"/>), and cannot be confused in the second case
///       because no other socket can have the relay's listen port as its source.</item>
/// </list>
/// <para>
/// Everything else - a broken header, a fragment, a port that is not ours, a packet whose owner
/// Windows will not name - is re-injected unchanged. Passing traffic through on doubt is deliberate:
/// it can only cost the proxy, never the connection.
/// </para>
/// </remarks>
internal sealed class Redirector : IDisposable
{
    /// <summary>WinDivert's own maximum packet size, with room to spare.</summary>
    private const int BufferSize = 40 + 0xFFFF;

    private const int ErrorOperationAborted = 995;
    private const int ErrorNoData = 232;

    private readonly Options _options;
    private readonly Stats _stats = new();
    private readonly LoopGuard _guard;
    private readonly Fuse _fuse;
    private readonly byte[] _buffer = new byte[BufferSize];
    private readonly string _filter;

    private IntPtr _handle = WinDivert.InvalidHandle;
    private Timer? _statsTimer;
    private volatile bool _stopping;
    private int _samples;
    private string _lastStats = "";

    internal Redirector(Options options)
    {
        _options = options;
        _filter = Filter.Build(options.Ports);
        _guard = new LoopGuard(options.ExcludedPids, options.ExcludedImages);
        _fuse = new Fuse(options.FusePerSecond);
    }

    /// <summary>Opens the driver, runs until interrupted, and reports what happened.</summary>
    internal int Run()
    {
        IntPtr handle;
        try
        {
            handle = WinDivert.WinDivertOpen(_filter, WindivertLayer.Network, 0, 0);
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException
                                          or EntryPointNotFoundException)
        {
            string message = "WinDivert.dll is not usable beside this exe (" + error.Message + ")";
            Fail(message);
            return 3;
        }

        if (handle == WinDivert.InvalidHandle || handle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Fail(ExplainOpenFailure(error), error);
            return 3;
        }
        _handle = handle;

        string version = WinDivert.Version(handle) ?? "unknown";
        PrintBanner(version);
        _options.WriteReady("ready pid=" + Environment.ProcessId + " winDivert=" + version);

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // Ctrl+C is the documented way to stop this; the launcher's Stop-Process is the other.
            eventArgs.Cancel = true;
            Stop();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();

        if (_options.StatsInterval > 0)
        {
            _statsTimer = new Timer(_ => PrintStats(), null,
                TimeSpan.FromSeconds(_options.StatsInterval), TimeSpan.FromSeconds(_options.StatsInterval));
        }

        try
        {
            Pump();
        }
        finally
        {
            _statsTimer?.Dispose();
            PrintStats(force: true);
            if (_handle != WinDivert.InvalidHandle)
            {
                WinDivert.WinDivertClose(_handle);
                _handle = WinDivert.InvalidHandle;
            }
        }

        Console.WriteLine("stopped.");
        return 0;
    }

    /// <summary>The receive loop. Returns when the handle is shut down or the driver fails.</summary>
    private void Pump()
    {
        int consecutiveErrors = 0;
        while (!_stopping)
        {
            var address = default(WindivertAddress);
            if (!WinDivert.WinDivertRecv(_handle, _buffer, BufferSize, out uint received, ref address))
            {
                int error = Marshal.GetLastWin32Error();
                if (_stopping || error is ErrorOperationAborted or ErrorNoData)
                {
                    break;
                }
                _stats.ReceiveError();
                if (++consecutiveErrors >= 100)
                {
                    Console.Error.WriteLine("[redirect] giving up after " + consecutiveErrors
                                            + " receive failures; last Win32 error " + error);
                    break;
                }
                Thread.Sleep(10);
                continue;
            }

            consecutiveErrors = 0;
            _stats.SawPacket();
            Handle((int)received, ref address);
        }
    }

    /// <summary>Classifies one captured packet and either reflects it or puts it back.</summary>
    private void Handle(int received, ref WindivertAddress address)
    {
        Span<byte> frame = _buffer.AsSpan(0, received);
        ParseResult result = Packets.TryRead(frame, out Packet packet);
        if (result != ParseResult.Ok)
        {
            _stats.Rejected(result);
            Reinject(received, ref address);
            return;
        }

        bool towardRelay = _options.Ports.Contains(packet.DestinationPort);
        bool towardClient = _options.Ports.Contains(packet.SourcePort);
        if (!towardRelay && !towardClient)
        {
            _stats.NoMatch();
            Reinject(received, ref address);
            return;
        }

        if (towardRelay)
        {
            bool newConnection = packet.Syn && !packet.Ack;
            int owner = _guard.Owner(packet.SourceAddress, packet.SourcePort, newConnection);
            if (owner <= 0)
            {
                // Windows would not name the process. Fail closed: an unproxied connection is a
                // visible degradation, a self-connection is a storm.
                _stats.UnknownOwner();
                Reinject(received, ref address);
                return;
            }
            if (_guard.IsExcluded(owner))
            {
                _stats.SelfMatch();
                Reinject(received, ref address);
                return;
            }
            if (newConnection && !_fuse.Allow())
            {
                _stats.FuseTripped();
                Reinject(received, ref address);
                return;
            }
        }

        string? sample = _samples < _options.Samples ? Describe(frame, packet) : null;

        if (_options.DryRun)
        {
            // Count the decision even though nothing is reflected: a dry run that prints no counters
            // cannot answer the question it is run for ("would this filter have caught the client?").
            if (towardRelay)
            {
                _stats.Reflected();
            }
            else
            {
                _stats.Returned();
            }
            if (sample is not null)
            {
                _samples++;
                Console.WriteLine("[dry-run] would reflect " + sample
                                  + (towardRelay ? "  (client -> relay listener)" : "  (relay -> client)"));
            }
            Reinject(received, ref address);
            return;
        }

        Packets.Reflect(frame, packet);
        address.Outbound = false;

        if (WinDivert.WinDivertSend(_handle, _buffer, (uint)received, out _, ref address))
        {
            if (towardRelay)
            {
                _stats.Reflected();
            }
            else
            {
                _stats.Returned();
            }
            if (sample is not null)
            {
                _samples++;
                string direction = towardRelay ? "client -> relay listener" : "relay -> client";
                Console.WriteLine("[redirect] sample " + _samples + ": " + sample + "  (" + direction + ")");
            }
        }
        else
        {
            // The packet has already been modified, so it cannot be put back as it was. Counting is
            // all that can be done, and the counter is why it is not silent.
            _stats.ReinjectFailed();
        }
    }

    private void Reinject(int received, ref WindivertAddress address)
    {
        if (!WinDivert.WinDivertSend(_handle, _buffer, (uint)received, out _, ref address))
        {
            _stats.ReinjectFailed();
        }
        else
        {
            _stats.PassedThrough();
        }
    }

    private void Stop()
    {
        if (_stopping)
        {
            return;
        }
        _stopping = true;
        if (_handle != WinDivert.InvalidHandle)
        {
            // Unblocks WinDivertRecv from another thread; the handle is closed by Run's finally.
            WinDivert.WinDivertShutdown(_handle, WindivertShutdown.Both);
        }
    }

    private void PrintStats(bool force = false)
    {
        if (!force && _stats.IsEmpty())
        {
            return;
        }
        string line = _stats.Format();
        if (force || line != _lastStats)
        {
            _lastStats = line;
            Console.WriteLine(line);
        }
    }

    private void PrintBanner(string version)
    {
        Console.WriteLine("drelay redirector (reflection)");
        Console.WriteLine("  winDivert  : " + version);
        Console.WriteLine("  layer      : network");
        Console.WriteLine("  filter     : " + _filter);
        Console.WriteLine("  mode       : swap source and destination, re-inject inward"
                          + (_options.DryRun ? " (dry run: nothing is reflected)" : ""));
        Console.WriteLine("  ports      : " + string.Join(", ", _options.Ports));
        Console.WriteLine("  exclude    : " + _guard.Describe());
        Console.WriteLine("  fuse       : " + (_options.FusePerSecond <= 0
            ? "off"
            : _options.FusePerSecond + " new connections/s, then pass through"));
        Console.WriteLine("  ready. Ctrl+C to stop.");
    }

    /// <summary>The addresses and ports of a packet, before it is modified.</summary>
    private static string Describe(ReadOnlySpan<byte> frame, in Packet packet)
    {
        return Address(frame, 12) + ":" + packet.SourcePort
               + " -> " + Address(frame, 16) + ":" + packet.DestinationPort
               + "  flags=" + Flags(packet);
    }

    private static string Address(ReadOnlySpan<byte> frame, int offset)
    {
        return frame[offset] + "." + frame[offset + 1] + "." + frame[offset + 2] + "." + frame[offset + 3];
    }

    private static string Flags(in Packet packet)
    {
        return (packet.Syn ? "S" : "") + (packet.Ack ? "A" : "") + (packet.Syn || packet.Ack ? "" : "-");
    }

    private void Fail(string message, int error = 0)
    {
        Console.Error.WriteLine("redirector failed: " + message
                                + (error == 0 ? "" : " (Win32 error " + error + ")"));
        _options.WriteReady("failed: " + message);
    }

    private static string ExplainOpenFailure(int error)
    {
        return error switch
        {
            5 => "access denied - WinDivert needs administrator rights, so start drelay from an "
                 + "elevated prompt",
            2 => "the WinDivert driver or WinDivert.dll could not be found beside the exe",
            87 => "the packet filter was rejected by WinDivert",
            1275 or 577 => "the WinDivert driver could not be loaded - the machine may be blocking "
                           + "unsigned or new kernel drivers",
            _ => "WinDivertOpen failed",
        };
    }

    public void Dispose()
    {
        _statsTimer?.Dispose();
        if (_handle != WinDivert.InvalidHandle)
        {
            WinDivert.WinDivertClose(_handle);
            _handle = WinDivert.InvalidHandle;
        }
    }
}
