using Microsoft.AspNetCore.Http.Json;

namespace Mmorpg.Tools.StackStressGenerator;

// Hosts the in-memory stress authority that stands in for GameService in worker-only
// mode. Tests use the same host so the worker's response validation sees exactly what
// the tool serves.
public static class StressAuthorityHost
{
    public static WebApplication Create(
        StressAuthorityState authority,
        string url,
        string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls(url);
        builder.Services.AddSingleton(authority);
        builder.Services.Configure<JsonOptions>(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy =
                System.Text.Json.JsonNamingPolicy.CamelCase;
        });

        var app = builder.Build();
        app.MapStressAuthority();
        return app;
    }
}
