using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Characters.Persistence;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;
using Mmorpg.Server.Common.Characters;

namespace Mmorpg.GameService.Characters;

// Characters: characters, ownership, character currency, character level, and character looks.
public sealed class CharactersModule(CharactersModuleOptions options) : IGameServiceModule
{
    public string Name => "Characters";

    public IReadOnlyList<DatabaseMigration> Migrations => CharactersSchemaMigrations.All;

    public IReadOnlyList<string> MeterNames { get; } = [];

    public void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The appearance content is loaded once; a broken file stops the start.
        var appearanceCatalog = CharacterAppearanceFileLoader.Load(options.AppearancePath);
        services.AddSingleton(options);
        services.AddSingleton(appearanceCatalog);
        services.AddSingleton<CharacterAppearances>();
        services.AddSingleton<ICharacterAppearances>(provider => provider.GetRequiredService<CharacterAppearances>());
        services.AddSingleton<IDatabaseSeeder, CharacterAppearanceVerifier>();
        services.AddSingleton<ICharacterLocks, CharacterLocks>();
        services.AddSingleton<ICharacterWallet, CharacterWallet>();
        services.AddSingleton<ICharacterProgression, CharacterProgression>();
        services.AddScoped<CharacterService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCharacterEndpoints();
    }
}
