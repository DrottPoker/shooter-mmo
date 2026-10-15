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
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = configuration.Items.Operations.MaximumHttpRequestBodyBytes;
});

builder.Services.AddGameService(configuration);

var app = builder.Build();

app.UseGameServicePipeline();

if (configuration.Platform.RunMigrationsOnStartup)
{
    var initializer = app.Services.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
    await PlacementModule.SeedTopologyAsync(app.Services, CancellationToken.None);
}

if (PhaseNineDevelopmentFixtureCommand.TryParse(
        args,
        out var phaseNineCharacterId,
        out var phaseNineFixtureError))
{
    if (!string.IsNullOrWhiteSpace(phaseNineFixtureError))
    {
        throw new InvalidOperationException(phaseNineFixtureError);
    }

    await using var scope = app.Services.CreateAsyncScope();
    var seeder = scope.ServiceProvider.GetRequiredService<PhaseNineDevelopmentFixtureSeeder>();
    var result = await seeder.SeedAsync(phaseNineCharacterId, CancellationToken.None);
    app.Logger.LogInformation(
        "Seeded Phase 9 fixture for character {CharacterId} at revision {ItemStateRevision} with {GrantedItemCount} items, {RecoveryDeliveryCount} Recovery delivery, and carry {CarriedWeight}/{CarryCapacity}.",
        result.CharacterId,
        result.ItemStateRevision,
        result.GrantedItemCount,
        result.RecoveryDeliveryCount,
        result.CarriedWeight,
        result.CarryCapacity);
    return;
}

if (DevelopmentItemToolCommandParser.TryParse(
        args,
        out var developmentItemToolCommand,
        out var developmentItemToolError))
{
    DevelopmentItemToolResponse response;
    if (!string.IsNullOrWhiteSpace(developmentItemToolError)
        || developmentItemToolCommand is null)
    {
        response = new DevelopmentItemToolResponse(
            false,
            developmentItemToolError.Length == 0
                ? "The development item tool command is invalid."
                : developmentItemToolError,
            null);
        Environment.ExitCode = 1;
    }
    else
    {
        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<DevelopmentItemToolService>();
            response = await service.ExecuteAsync(
                developmentItemToolCommand,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            response = new DevelopmentItemToolResponse(false, exception.Message, null);
            Environment.ExitCode = 1;
        }
    }

    Console.WriteLine(DevelopmentItemToolProtocol.Serialize(response));
    return;
}

app.MapGameService(configuration);

app.Run();
