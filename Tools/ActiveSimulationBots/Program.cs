using ShooterMmo.Shared.Configuration;
using ShooterMmo.Tools.ActiveSimulationBots;

var configuration = new ConfigurationManager();
configuration
    .AddJsonFile(
        Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
        optional: false,
        reloadOnChange: false)
    .AddOptionalDotEnvFile(Directory.GetCurrentDirectory())
    .AddEnvironmentVariables()
    .AddCommandLine(args);

ActiveSimulationBotOptions options;
try
{
    options = ActiveSimulationBotOptions.FromConfiguration(configuration);
}
catch (ActiveSimulationBotConfigurationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

using var httpClient = new HttpClient
{
    BaseAddress = options.AuthorityUrl,
    Timeout = options.JoinAndLeaveTimeout
};
var authorityClient = new ActiveSimulationBotAuthorityClient(httpClient, options);
var coordinator = new ActiveSimulationBotCoordinator(options, authorityClient);

try
{
    await coordinator.RunAsync(shutdown.Token);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"ActiveSimulationBots failed: {exception.Message}");
    return 1;
}
