using System.Reflection;
using Mmorpg.GameService.Testing;

namespace Mmorpg.GameService.Tests.Architecture;

// A module publishes only its module class, its options, and its Contracts namespace.
// Everything else is internal, so other modules cannot reach past the contracts.
public sealed class ModulePublicSurfaceTests
{
    private static readonly IReadOnlyDictionary<string, string[]> PublishedTypes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Mmorpg.GameService.Accounts"] =
            [
                "Mmorpg.GameService.Accounts.AccountsModule",
                "Mmorpg.GameService.Accounts.AccountsModuleOptions",
                "Mmorpg.GameService.Accounts.Redis.RedisAccelerationOptions",
                "Mmorpg.GameService.Accounts.SimulationWorkerCredentials"
            ],
            ["Mmorpg.GameService.Characters"] =
            [
                "Mmorpg.GameService.Characters.CharactersModule",
                "Mmorpg.GameService.Characters.CharactersModuleOptions"
            ],
            ["Mmorpg.GameService.Placement"] =
            [
                "Mmorpg.GameService.Placement.Development.DevelopmentSimulationBotOptions",
                "Mmorpg.GameService.Placement.FleetBootstrapConfig",
                "Mmorpg.GameService.Placement.PlacementModule",
                "Mmorpg.GameService.Placement.PlacementModuleOptions",
                "Mmorpg.GameService.Placement.ShardBootstrapConfig",
                "Mmorpg.GameService.Placement.SimulationNodeBootstrapConfig",
                "Mmorpg.GameService.Placement.SimulationTopologyConfig",
                "Mmorpg.GameService.Placement.WorldManifestCatalog"
            ],
            ["Mmorpg.GameService.Items"] =
            [
                "Mmorpg.GameService.Items.Development.DevelopmentItemPackageSource",
                "Mmorpg.GameService.Items.ItemCatalogSource",
                "Mmorpg.GameService.Items.ItemOperationsOptions",
                "Mmorpg.GameService.Items.ItemsCommandLine",
                "Mmorpg.GameService.Items.ItemsModule",
                "Mmorpg.GameService.Items.ItemsModuleOptions",
                "Mmorpg.GameService.Items.NpcItemLifecycleOptions",
                "Mmorpg.GameService.Items.StackStress.StackStressFixtureOptions"
            ]
        };

    public static TheoryData<string> Modules => ["Accounts", "Characters", "Placement", "Items"];

    [Theory]
    [MemberData(nameof(Modules))]
    public void ModulesExportOnlyTheirModuleOptionsAndContracts(string module)
    {
        var assembly = Assembly.Load($"Mmorpg.GameService.{module}");
        var contractsNamespace = $"Mmorpg.GameService.{module}.Contracts";

        var exported = assembly.GetExportedTypes()
            .Where(type => !type.IsNested)
            .Where(type => !string.Equals(type.Namespace, contractsNamespace, StringComparison.Ordinal))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(PublishedTypes[assembly.GetName().Name!], exported);
    }

    [Fact]
    public void PlatformKeepsItsMiddlewareInternal()
    {
        var exported = GameServiceAssemblies.Platform.GetExportedTypes()
            .Select(type => type.Name)
            .ToArray();

        Assert.DoesNotContain("ApiExceptionHandler", exported);
        Assert.DoesNotContain("CorrelationIdMiddleware", exported);
        Assert.DoesNotContain("SensitiveResponseCacheMiddleware", exported);
    }
}
