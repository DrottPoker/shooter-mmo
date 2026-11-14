using Mmorpg.GameService;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Database;
using Mmorpg.GameService.Items;
using Mmorpg.Server.Common.Configuration;

// `migrate` and `migrate --plan` run instead of the web host; the other arguments configure.
var (databaseCommand, configurationArgs) = GameServiceDatabaseCommandLine.Parse(args);

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
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = configuration.Items.Operations.MaximumHttpRequestBodyBytes;
});

builder.Services.AddGameService(configuration);

var app = builder.Build();
var databaseInitializer = app.Services.GetRequiredService<GameServiceDatabaseInitializer>();

if (databaseCommand is { } command)
{
    Environment.ExitCode = await GameServiceDatabaseCommandLine.RunAsync(
        command,
        databaseInitializer,
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
if (configuration.Platform.RunMigrationsOnStartup)
{
    var changes = await databaseInitializer.InitializeAsync(CancellationToken.None);
    foreach (var change in changes.SeedChanges)
    {
        app.Logger.LogInformation("Database seed change: {SeedChange}", change);
    }
}
else
{
    await databaseInitializer.EnsureCurrentAsync(CancellationToken.None);
}

var commandExitCode = await ItemsCommandLine.TryRunAsync(app.Services, args, app.Logger);
if (commandExitCode is not null)
{
    Environment.ExitCode = commandExitCode.Value;
    return;
}

app.MapGameService(configuration);

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
