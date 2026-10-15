using System.Text.RegularExpressions;

namespace Mmorpg.Backend.Tests.Architecture;

// Modules may only reference modules below them in the dependency order
// Platform, Accounts, Characters, Placement, Items, host.
public sealed partial class NamespaceBoundaryTests
{
    // Namespaces mapped to the module that owns them.
    private static readonly (string Namespace, string Module)[] NamespaceRules =
    [
        ("Mmorpg.GameService.Auth", GameServiceSourceMap.Accounts),
        ("Mmorpg.GameService.Redis", GameServiceSourceMap.Accounts),
        ("Mmorpg.GameService.Http", GameServiceSourceMap.Platform),
        ("Mmorpg.GameService.Characters", GameServiceSourceMap.Characters),
        ("Mmorpg.GameService.Simulation", GameServiceSourceMap.Placement),
        ("Mmorpg.GameService.Items", GameServiceSourceMap.Items),
        ("Mmorpg.GameService.Config", GameServiceSourceMap.Host),
        ("Mmorpg.GameService.Database", GameServiceSourceMap.Host),
        ("Mmorpg.GameService.Health", GameServiceSourceMap.Host)
    ];

    // References that still point up the dependency order. The cycle ids refer to
    // docs/GAME_SERVICE_MODULES.md. Every entry must still exist, so the list can only
    // shrink.
    private static readonly IReadOnlySet<string> PendingUpwardReferences = new HashSet<string>(StringComparer.Ordinal)
    {
        // C2: character creation bootstraps item state directly.
        "GameService/Characters/CharacterService.cs -> Mmorpg.GameService.Items",
        // C6: the shared configuration object and module options live in the host.
        "GameService/Auth/AccountsModuleRegistration.cs -> Mmorpg.GameService.Config",
        "GameService/Items/CorpseService.cs -> Mmorpg.GameService.Config",
        "GameService/Items/ItemsModuleRegistration.cs -> Mmorpg.GameService.Config",
        "GameService/Items/StackStressFixtureEndpoints.cs -> Mmorpg.GameService.Config",
        "GameService/Items/StackStressFixtureService.cs -> Mmorpg.GameService.Config",
        "GameService/Redis/RedisAuthenticationRateLimitMiddleware.cs -> Mmorpg.GameService.Config",
        "GameService/Redis/RedisConnectionProvider.cs -> Mmorpg.GameService.Config.GameServiceConfig",
        "GameService/Simulation/DevelopmentSimulationBotAuthority.cs -> Mmorpg.GameService.Config",
        "GameService/Simulation/DevelopmentSimulationBotEndpoints.cs -> Mmorpg.GameService.Config",
        "GameService/Simulation/DevelopmentSimulationBotPlacementService.cs -> Mmorpg.GameService.Config",
        "GameService/Simulation/PlacementModuleRegistration.cs -> Mmorpg.GameService.Config",
        "GameService/Simulation/ShardService.cs -> Mmorpg.GameService.Config",
        "GameService/Simulation/SimulationTopologySeeder.cs -> Mmorpg.GameService.Config",
        "GameService/Simulation/SimulationWorkerRegistryService.cs -> Mmorpg.GameService.Config"
    };

    [Fact]
    public void ModulesOnlyReferenceLowerModules()
    {
        var found = FindUpwardReferences();
        var unexpected = found.Where(reference => !PendingUpwardReferences.Contains(reference)).ToArray();
        var resolved = PendingUpwardReferences.Where(reference => !found.Contains(reference)).ToArray();

        Assert.True(
            unexpected.Length == 0,
            "A module references a module above it in the dependency order:"
            + Environment.NewLine + string.Join(Environment.NewLine, unexpected));
        Assert.True(
            resolved.Length == 0,
            "Remove resolved entries from the pending upward reference list:"
            + Environment.NewLine + string.Join(Environment.NewLine, resolved));
    }

    private static HashSet<string> FindUpwardReferences()
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in GameServiceSourceMap.LoadSourceFiles())
        {
            var sourceRank = GameServiceSourceMap.RankOf(file.Module);
            // A file's own namespace declaration is not a reference.
            var text = NamespaceDeclarationPattern().Replace(file.Text, string.Empty);
            foreach (Match match in NamespaceReferencePattern().Matches(text))
            {
                var referencedModule = ModuleOfNamespace(match.Groups["namespace"].Value);
                if (referencedModule is null
                    || string.Equals(referencedModule, file.Module, StringComparison.Ordinal)
                    || GameServiceSourceMap.RankOf(referencedModule) < sourceRank)
                {
                    continue;
                }

                references.Add($"{file.RelativePath} -> {match.Groups["namespace"].Value}");
            }
        }

        return references;
    }

    private static string? ModuleOfNamespace(string value)
    {
        foreach (var (prefix, module) in NamespaceRules)
        {
            if (string.Equals(value, prefix, StringComparison.Ordinal)
                || value.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return module;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\b(?<namespace>Mmorpg\.GameService(?:\.[A-Z][A-Za-z]*)+)")]
    private static partial Regex NamespaceReferencePattern();

    [GeneratedRegex(@"^namespace\s+[A-Za-z.]+;", RegexOptions.Multiline)]
    private static partial Regex NamespaceDeclarationPattern();
}
