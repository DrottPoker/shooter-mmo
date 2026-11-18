using Mmorpg.GameService;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Database;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.Server.Common.Configuration;

// `migrate`, `migrate --plan`, and `audit` run instead of the web host; the other arguments configure.
// A command line that names one of them but is not one it understands is refused here, before
// the configuration or the database is touched.
GameServiceDatabaseCommand? databaseCommand;
string[] configurationArgs;
try
{
    (databaseCommand, configurationArgs) = GameServiceDatabaseCommandLine.Parse(args);
}
catch (GameServiceDatabaseCommandLineException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    Environment.ExitCode = 1;
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = configurationArgs,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration
    .AddJsonFile(
        Path.Combine("Config", "appsettings.json"),
        optional: false,
        reloadOnChange: false)
    .AddJsonFile(
        Path.Combine(
            "Config",
            $"appsettings.{builder.Environment.EnvironmentName}.json"),
        optional: true,
        reloadOnChange: false);
builder.Configuration.AddOptionalDotEnvFile(
    Directory.GetCurrentDirectory(),
    AppContext.BaseDirectory);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(configurationArgs);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var configuration = GameServiceConfiguration.Load(builder.Configuration, builder.Environment);
builder.AddGameServiceTelemetry(configuration);
builder.ConfigureGameServiceHost(configuration);

builder.Services.AddGameService(configuration);

var app = builder.Build();
var databaseInitializer = app.Services.GetRequiredService<GameServiceDatabaseInitializer>();

if (databaseCommand is { } command)
{
    Environment.ExitCode = await GameServiceDatabaseCommandLine.RunAsync(
        command,
        app.Services,
        Console.Out,
        Console.Error,
        CancellationToken.None);
    // Flushes the console log before the process ends.
    await app.DisposeAsync();
    return;
}

app.UseGameServicePipeline();

// Without migrations at startup, a separate migrate run prepares the database, and a start
// refuses a database that it would still change.
var unknownMigrations = await databaseInitializer.PrepareForStartAsync(
    configuration.Platform.RunMigrationsOnStartup,
    app.Logger,
    CancellationToken.None);
app.Services.GetRequiredService<DatabaseAheadState>().Record(unknownMigrations);

var commandExitCode = await ItemsCommandLine.TryRunAsync(app.Services, args, app.Logger);
if (commandExitCode is not null)
{
    Environment.ExitCode = commandExitCode.Value;
    return;
}

app.MapGameService(configuration);

if (configuration.Platform.Http.TrustsProxies)
{
    app.Logger.LogInformation(
        "Client addresses come from X-Forwarded-For of the trusted proxies {TrustedProxies}.",
        app.Configuration[HttpServerOptions.TrustedProxiesKey]);
}
else
{
    app.Logger.LogInformation(
        "No trusted proxies are configured: the connection's address is the client's, and forwarded headers are ignored.");
}

if (configuration.Telemetry.IsEnabled)
{
    app.Logger.LogInformation(
        "Exporting metrics, traces, and logs over OTLP to {OtlpEndpoint}.",
        configuration.Telemetry.OtlpEndpoint);
}
else
{
    app.Logger.LogInformation(
        "Telemetry export is disabled. Set Telemetry:OtlpEndpoint to export metrics, traces, and logs.");
}

app.Run();
