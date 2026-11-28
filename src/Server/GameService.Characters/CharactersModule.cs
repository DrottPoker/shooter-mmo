using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Characters.Persistence;
using Mmorpg.GameService.Platform.Audit;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;
using Mmorpg.Server.Common.Characters;
using Mmorpg.Server.Common.Progression;

namespace Mmorpg.GameService.Characters;

// Characters: characters, ownership, character currency with the audit of its ledger, character
// level and experience, character looks, and every character's saved place and vitals.
public sealed class CharactersModule(CharactersModuleOptions options) : IGameServiceModule
{
    public string Name => "Characters";

    public IReadOnlyList<DatabaseMigration> Migrations => CharactersSchemaMigrations.All;

    public IReadOnlyList<string> MeterNames { get; } = [CharacterProgressionMetrics.MeterName];

    public void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The appearance content is loaded once; a broken file stops the start.
        var appearanceCatalog = CharacterAppearanceFileLoader.Load(options.AppearancePath);

        // So are the progression rules.
        var progressionRules = ProgressionRulesFileLoader.Load(options.ProgressionRulesPath);
        services.AddSingleton(options);
        services.AddSingleton(appearanceCatalog);
        services.AddSingleton(progressionRules);
        services.AddSingleton<CharacterAppearances>();
        services.AddSingleton<ICharacterAppearances>(provider => provider.GetRequiredService<CharacterAppearances>());
        services.AddSingleton<IDatabaseSeeder, CharacterAppearanceVerifier>();
        services.AddSingleton<ICharacterLocks, CharacterLocks>();
        services.AddSingleton<ICharacterWallet, CharacterWallet>();
        services.AddSingleton<CharacterProgressionMetrics>();
        services.AddSingleton<ICharacterProgression, CharacterProgression>();
        services.AddSingleton<ICharacterSimulationStates, CharacterSimulationStates>();
        services.AddScoped<CharacterService>();
        foreach (var check in CharactersAudit.Checks)
        {
            services.AddSingleton<IAuditCheck>(check);
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCharacterEndpoints();
    }
}
