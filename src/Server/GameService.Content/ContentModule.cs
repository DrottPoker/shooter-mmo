using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;

namespace Mmorpg.GameService.Content;

// Content: serves the compiled item catalog, quest catalog, and text tables by revision,
// exactly as the other modules load them, so a content change needs no client build.
// GameService serves the text tables as they are and reads no text from them.
public sealed class ContentModule(ContentModuleOptions options) : IGameServiceModule
{
    public string Name => "Content";

    public IReadOnlyList<DatabaseMigration> Migrations { get; } = [];

    public IReadOnlyList<string> MeterNames { get; } = [];

    public void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The files are read once; a missing or broken file stops the start.
        var store = ContentFileLoader.Load(options);
        services.AddSingleton(options);
        services.AddSingleton(store);
        services.AddHostedService<ContentRevisionCheck>();
        services.AddRateLimiter(rateLimiterOptions => rateLimiterOptions.AddPolicy(
            ContentRateLimiterPolicy.PolicyName,
            new ContentRateLimiterPolicy(options)));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapContentEndpoints();
    }
}
