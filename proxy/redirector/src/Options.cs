using System.Globalization;

namespace Drelay.Redirect;

/// <summary>
/// Builds the WinDivert filter from the ports to intercept.
/// </summary>
/// <remarks>
/// <para>
/// The filter matches a port in BOTH directions and never an address. The address must not be pinned:
/// the realm address is invented by the server per transition (<c>GmReconnect</c>), so a filter that
/// names the current game server loses the session at the first realm change.
/// </para>
/// <para>
/// <c>not loopback</c> is what keeps the hosts-captured routes working: the queue route is captured by
/// resolving its name to <c>127.0.0.1</c>, and those packets are already going to a local listener.
/// Reflecting them would be a no-op at best and a loop at worst.
/// </para>
/// </remarks>
internal static class Filter
{
    internal static string Build(IReadOnlyCollection<ushort> ports)
    {
        var clauses = new List<string>();
        foreach (ushort port in ports.OrderBy(value => value))
        {
            clauses.Add("tcp.DstPort == " + port);
            clauses.Add("tcp.SrcPort == " + port);
        }
        return "outbound and ip and tcp and (" + string.Join(" or ", clauses) + ") and not loopback";
    }
}

/// <summary>Everything the command line can say.</summary>
internal sealed class Options
{
    private Options()
    {
    }

    /// <summary>The ports to intercept: the game's, by default.</summary>
    /// <remarks>
    /// 6410 is the game service and 6411 is the realm the server names in a reconnect. 6412 (the
    /// queue) is deliberately absent: it is captured by the hosts file, so its packets are loopback
    /// and the filter would not match them anyway.
    /// </remarks>
    internal SortedSet<ushort> Ports { get; } = new() { 6410, 6411 };

    internal List<int> ExcludedPids { get; } = new();

    /// <summary>
    /// Executables whose packets are never reflected: the relay and the launcher are both
    /// <c>java.exe</c>, and the relay's upstream dials are the loop the guard exists for.
    /// </summary>
    internal List<string> ExcludedImages { get; } = new() { "java.exe", "javaw.exe" };

    /// <summary>New client connections reflected per second before the fuse trips. 0 disables it.</summary>
    internal int FusePerSecond { get; private set; } = 20;

    /// <summary>Seconds between [stats] lines. 0 prints only at exit.</summary>
    internal int StatsInterval { get; private set; } = 5;

    /// <summary>How many reflected packets are described in full. 0 silences the samples.</summary>
    internal int Samples { get; private set; } = 10;

    /// <summary>Log the decision for every packet, without reflecting anything.</summary>
    internal bool DryRun { get; private set; }

    /// <summary>Where the launcher waits for "the driver is open", or null.</summary>
    internal string? ReadyPath { get; private set; }

    internal bool Help { get; private set; }

    internal bool SelfTest { get; private set; }

    /// <summary>Parses the command line, or throws <see cref="ArgumentException"/>.</summary>
    internal static Options Parse(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "--help" or "-h" or "/?":
                    options.Help = true;
                    break;
                case "--selftest":
                    options.SelfTest = true;
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--ports":
                    options.Ports.Clear();
                    foreach (string part in Next(args, ref i, argument).Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!ushort.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort port)
                            || port == 0)
                        {
                            throw new ArgumentException("'" + part + "' is not a port number");
                        }
                        options.Ports.Add(port);
                    }
                    if (options.Ports.Count == 0)
                    {
                        throw new ArgumentException("--ports needs at least one port");
                    }
                    break;
                case "--exclude-pid":
                    options.ExcludedPids.Add(ParseInt(Next(args, ref i, argument), argument));
                    break;
                case "--exclude-image":
                    options.ExcludedImages.Add(Next(args, ref i, argument));
                    break;
                case "--no-default-excludes":
                    options.ExcludedImages.Clear();
                    break;
                case "--fuse":
                    options.FusePerSecond = ParseInt(Next(args, ref i, argument), argument);
                    break;
                case "--stats-interval":
                    options.StatsInterval = ParseInt(Next(args, ref i, argument), argument);
                    break;
                case "--samples":
                    options.Samples = ParseInt(Next(args, ref i, argument), argument);
                    break;
                case "--ready":
                    options.ReadyPath = Next(args, ref i, argument);
                    break;
                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("unknown option '" + argument + "'");
                    }
                    break;
            }
        }
        return options;
    }

    /// <summary>
    /// Records whether the driver came up, for the launcher to read.
    /// </summary>
    /// <remarks>
    /// The launcher is already copying this process's output to a file, but it must not start the
    /// relay on a machine where the driver failed to install: the relay would be listening for
    /// traffic nothing is sending it. A one-line file is how the launcher learns the verdict without
    /// parsing a stream, and it is the same "write the fact where the other process can read it"
    /// pattern the relay uses for its dashboard URL.
    /// </remarks>
    internal void WriteReady(string text)
    {
        if (ReadyPath is null)
        {
            return;
        }
        try
        {
            File.WriteAllText(ReadyPath, text + Environment.NewLine);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("could not write " + ReadyPath + ": " + error.Message);
        }
    }

    internal static void PrintHelp()
    {
        Console.WriteLine("""
            drelay-redirect - divert the game's outbound TCP into the local relay, without a claim.

            The launcher starts this; it is not normally run by hand. It needs administrator rights
            (WinDivert installs a signed driver on first use) and WinDivert.dll + WinDivert64.sys
            beside it.

            Usage:
              drelay-redirect.exe [options]

            Options:
              --ports <a,b,...>       ports to intercept (default: 6410,6411)
              --exclude-pid <n>       never reflect this process's packets (repeatable)
              --exclude-image <name>  never reflect this executable's packets
                                      (default: java.exe, javaw.exe - the relay is a java process)
              --no-default-excludes   do not exclude java.exe/javaw.exe
              --fuse <n>              stop reflecting after n new connections in one second
                                      (default: 20, 0 disables the fuse)
              --stats-interval <s>    seconds between stats lines (default: 5, 0 = only at exit)
              --samples <n>           describe the first n reflected packets (default: 10)
              --dry-run               log decisions but re-inject every packet unchanged
              --ready <path>          write the driver's verdict to this file
              --selftest              run the offline checks (no driver, no elevation) and exit
              --help                  this text

            How it works, and why it is a reflection rather than a destination rewrite: see
            docs/proxy/HANDOFF-WINDIVERT.md and docs/proxy/PROTOCOL.md.
            """);
    }

    private static string Next(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException(name + " needs a value");
        }
        index++;
        return args[index];
    }

    private static int ParseInt(string text, string name)
    {
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
        {
            throw new ArgumentException("'" + text + "' is not a number for " + name);
        }
        return value;
    }
}
