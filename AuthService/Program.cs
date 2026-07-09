using AuthService.Auth;
using AuthService.Characters;
using AuthService.Database;
using AuthService.Worlds;
using Npgsql;
using ShooterMmo.Shared.Health;
using ShooterMmo.Shared.Networking;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddSingleton(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Postgres connection string is not configured.");

    return NpgsqlDataSource.Create(connectionString);
});

builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<CharacterService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<WorldService>();

var app = builder.Build();

if (app.Configuration.GetValue("Database:RunMigrationsOnStartup", true))
{
    var initializer = app.Services.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
}

app.MapGet("/", () => Results.Redirect("/health"));

app.MapGet("/health", async (IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var timeout = TimeSpan.FromMilliseconds(
        configuration.GetValue("HealthChecks:TcpTimeoutMilliseconds", 1_000));

    var postgresEndpoint = PostgresConnectionString.ParseEndpoint(
        configuration.GetConnectionString("Postgres"));

    var redisEndpoint = RedisConnectionString.ParseEndpoint(
        configuration.GetConnectionString("Redis"));

    var dependencies = await Task.WhenAll(
        TcpHealthProbe.CheckAsync("postgres", postgresEndpoint, timeout, cancellationToken),
        TcpHealthProbe.CheckAsync("redis", redisEndpoint, timeout, cancellationToken));

    var status = dependencies.All(dependency => dependency.IsReachable)
        ? "healthy"
        : "degraded";

    return Results.Ok(new ServiceHealth(
        "AuthService",
        status,
        DateTimeOffset.UtcNow,
        dependencies));
});

app.MapAccountEndpoints();
app.MapCharacterEndpoints();
app.MapWorldEndpoints();

app.Run();
