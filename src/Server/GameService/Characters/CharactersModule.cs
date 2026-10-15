using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;

namespace Mmorpg.GameService.Characters;

// Characters: characters, ownership, and character currency.
public sealed class CharactersModule(CharactersModuleOptions options) : IGameServiceModule
{
    public string Name => "Characters";

    public IReadOnlyList<DatabaseMigration> Migrations { get; } = [];

    public void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(options);
        services.AddScoped<CharacterService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCharacterEndpoints();
    }
}
