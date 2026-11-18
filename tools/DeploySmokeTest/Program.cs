using System.Globalization;
using Mmorpg.Tools.DeploySmokeTest;

// The smoke test of the production-like stack, in three commands that tools/Test-DeployStack.ps1
// runs around the Docker steps it owns:
//
//   play         a player's whole path through the proxy, and the checks of what a stranger meets
//   hold         one player who stays in the world until the worker ends the connection
//   watch-ready  the readiness answers through the proxy while GameService stops
//
// The exit code is 0 only when every check held. docs/DEPLOYMENT.md describes the test.
return await Run(args);

static async Task<int> Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help")
    {
        Console.Error.WriteLine(SmokeOptions.Usage);
        return 2;
    }

    SmokeOptions options;
    try
    {
        options = SmokeOptions.Parse(args[1..]);
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }

    // The same numbers in every line, on every machine.
    CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    var report = new SmokeReport(Console.Out);
    using var root = StackTrust.LoadRoot(options.CaPath);
    using var http = StackTrust.CreateClient(options.BaseUrl, root);
    var api = new StackApi(http);
    try
    {
        switch (args[0])
        {
            case "play":
                await new PlayScenario(options, api, report).RunAsync(cancellation.Token);
                break;
            case "hold":
                await new HoldScenario(options, api, report, Console.Out).RunAsync(cancellation.Token);
                break;
            case "watch-ready":
                await new ReadyWatcher(options, api, report, Console.Out).RunAsync(cancellation.Token);
                break;
            default:
                Console.Error.WriteLine($"Unknown command {args[0]}. {SmokeOptions.Usage}");
                return 2;
        }
    }
    catch (OperationCanceledException)
    {
        report.Fail("cancelled", "the run was cancelled before it finished");
    }

    Console.WriteLine($"{args[0]}: {report.Passed} checks held, {report.Failed} failed.");
    return report.ExitCode;
}
