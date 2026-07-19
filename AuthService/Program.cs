using System.Threading.RateLimiting;
using AuthService.Auth;
using AuthService.Characters;
using AuthService.Config;
using AuthService.Database;
using AuthService.Health;
using AuthService.Http;
using AuthService.Items;
using AuthService.Simulation;
using Microsoft.AspNetCore.Authentication;
using Npgsql;
using ShooterMmo.Shared.Configuration;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration
    .AddJsonFile(
        Path.Combine("Config", "appsettings.json"),
        optional: false,
        reloadOnChange: true)
    .AddJsonFile(
        Path.Combine(
            "Config",
            $"appsettings.{builder.Environment.EnvironmentName}.json"),
        optional: true,
        reloadOnChange: true);
builder.Configuration.AddOptionalDotEnvFile(Directory.GetCurrentDirectory());
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var config = AuthServiceConfig.FromConfiguration(builder.Configuration);
var worldManifestCatalog = WorldManifestCatalog.FromConfiguration(builder.Configuration);
var developmentSimulationBotOptions = DevelopmentSimulationBotOptions.FromConfiguration(
    builder.Configuration,
    builder.Environment.IsDevelopment());
var stackStressFixtureOptions = StackStressFixtureOptions.FromConfiguration(
    builder.Configuration,
    builder.Environment.IsDevelopment(),
    config.PostgresConnectionString);
var npcItemLifecycleOptions = NpcItemLifecycleOptions.FromConfiguration(
    builder.Configuration);
var itemOperationsOptions = ItemOperationsOptions.FromConfiguration(builder.Configuration);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = itemOperationsOptions.MaximumHttpRequestBodyBytes;
});

builder.Services.AddSingleton(_ =>
{
    return NpgsqlDataSource.Create(config.PostgresConnectionString);
});

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(worldManifestCatalog);
builder.Services.AddSingleton(developmentSimulationBotOptions);
builder.Services.AddSingleton(stackStressFixtureOptions);
builder.Services.AddSingleton(npcItemLifecycleOptions);
builder.Services.AddSingleton(itemOperationsOptions);
builder.Services.AddSingleton<ItemOperationsMetrics>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(ItemCatalogSource.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<DevelopmentSimulationBotAuthority>();
builder.Services.AddSingleton<ItemCatalogSeeder>();
builder.Services.AddSingleton<CharacterItemStateBootstrapper>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<SimulationTopologySeeder>();
builder.Services.AddSingleton<PostgresHealthProbe>();
builder.Services.AddSingleton<AuthServiceHealthService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<CharacterService>();
builder.Services.AddScoped<ItemCatalogQueryService>();
builder.Services.AddScoped<ItemQueryService>();
builder.Services.AddScoped<ItemTransactionService>();
builder.Services.AddScoped<AccountItemMutationService>();
builder.Services.AddScoped<SimulationItemMutationService>();
builder.Services.AddScoped<ItemPolicyService>();
builder.Services.AddScoped<QuestItemService>();
builder.Services.AddScoped<CorpseService>();
builder.Services.AddScoped<ItemOperationsMaintenanceService>();
builder.Services.AddScoped<PhaseNineDevelopmentFixtureSeeder>();
builder.Services.AddScoped<DevelopmentItemToolService>();
builder.Services.AddScoped<StackStressFixtureService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<ShardService>();
builder.Services.AddScoped<SimulationSessionService>();
builder.Services.AddScoped<SimulationWorkerRegistryService>();
builder.Services.AddScoped<DevelopmentSimulationBotPlacementService>();
builder.Services.AddScoped<DevelopmentSimulationBotTicketService>();
builder.Services.AddHostedService<CorpseExpiryHostedService>();
builder.Services.AddHostedService<ItemOperationsMetricsReporterService>();
builder.Services.AddHostedService<ItemOperationsMaintenanceHostedService>();
builder.Services.AddApiProblemDetails();
builder.Services
    .AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, AccountSessionAuthenticationHandler>(
        AuthenticationConstants.AccountSessionScheme,
        _ => { })
    .AddScheme<AuthenticationSchemeOptions, SimulationWorkerAuthenticationHandler>(
        AuthenticationConstants.SimulationWorkerScheme,
        _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthenticationConstants.AccountSessionPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(AuthenticationConstants.AccountSessionScheme);
        policy.RequireAuthenticatedUser();
    });
    options.AddPolicy(AuthenticationConstants.SimulationWorkerPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(AuthenticationConstants.SimulationWorkerScheme);
        policy.RequireAuthenticatedUser();
    });
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = builder.Configuration
            .GetValue("RateLimiting:Authentication:WindowSeconds", 60)
            .ToString();

        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too Many Requests",
                detail: "Too many authentication attempts. Try again later.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "rate_limit_exceeded"
                })
            .ExecuteAsync(context.HttpContext);
    };

    AddAuthenticationRateLimitPolicy(options, "login", builder.Configuration);
    AddAuthenticationRateLimitPolicy(options, "register", builder.Configuration);
});

var app = builder.Build();

app.UseApiPipeline();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Configuration.GetValue("Database:RunMigrationsOnStartup", true))
{
    var initializer = app.Services.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
    var topologySeeder = app.Services.GetRequiredService<SimulationTopologySeeder>();
    await topologySeeder.SeedAsync(CancellationToken.None);
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

app.MapGet("/", () => Results.Redirect("/health/ready"));
app.MapGet("/health/live", () => Results.Ok(new
{
    service = "AuthService",
    status = "live",
    checkedAt = DateTimeOffset.UtcNow
}));
app.MapGet("/health/ready", async (
    AuthServiceHealthService healthService,
    CancellationToken cancellationToken) =>
{
    var health = await healthService.CheckReadinessAsync(cancellationToken);
    return Results.Json(
        health,
        statusCode: health.Status == "ready"
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable);
});

app.MapAccountEndpoints();
app.MapCharacterEndpoints();
app.MapItemEndpoints();
app.MapSimulationItemEndpoints();
app.MapCorpseEndpoints();
app.MapSimulationEndpoints();
app.MapDevelopmentSimulationBotEndpoints(developmentSimulationBotOptions);
app.MapStackStressFixtureEndpoints(stackStressFixtureOptions);

app.Run();

static void AddAuthenticationRateLimitPolicy(
    Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options,
    string policyName,
    IConfiguration configuration)
{
    options.AddPolicy(policyName, httpContext =>
    {
        var remoteAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{policyName}:{remoteAddress}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = configuration.GetValue("RateLimiting:Authentication:PermitLimit", 5),
                Window = TimeSpan.FromSeconds(
                    configuration.GetValue("RateLimiting:Authentication:WindowSeconds", 60)),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
}
