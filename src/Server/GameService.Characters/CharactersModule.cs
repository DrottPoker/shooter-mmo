using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Characters.Persistence;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;

namespace Mmorpg.GameService.Characters;

// Characters: characters, ownership, and character currency.
public sealed class CharactersModule(CharactersModuleOptions options) : IGameServiceModule
{
    public string Name => "Characters";

    public IReadOnlyList<DatabaseMigration> Migrations => CharactersSchemaMigrations.All;

    public IReadOnlyList<string> MeterNames { get; } = [];

    public void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(options);
        services.AddSingleton<ICharacterLocks, CharacterLocks>();
        services.AddSingleton<ICharacterWallet, CharacterWallet>();
        services.AddScoped<CharacterService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCharacterEndpoints();
    }
}
