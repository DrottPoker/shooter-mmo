using Mmorpg.GameService;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Database;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Placement;
using Mmorpg.Server.Common.Configuration;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
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
builder.Configuration.AddCommandLine(args);

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

app.UseGameServicePipeline();

if (configuration.Platform.RunMigrationsOnStartup)
{
    var initializer = app.Services.GetRequiredService<GameServiceDatabaseInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
    await PlacementModule.SeedTopologyAsync(app.Services, CancellationToken.None);
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
