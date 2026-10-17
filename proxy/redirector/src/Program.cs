namespace Drelay.Redirect;

/// <summary>The redirector's entry point: parse the command line, then run one of two things.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine("error: " + error.Message);
            Console.Error.WriteLine("run with --help for the options");
            return 2;
        }

        if (options.Help)
        {
            Options.PrintHelp();
            return 0;
        }
        if (options.SelfTest)
        {
            return SelfTest.Run();
        }

        using var redirector = new Redirector(options);
        return redirector.Run();
    }
}
