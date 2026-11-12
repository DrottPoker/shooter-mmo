using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Testing;

namespace Mmorpg.GameService.Tests.Architecture;

// Every SPI runs inside the caller's transaction. A missing implementation would
// silently skip required work, so the composition requires exactly one, from the
// module that owns the data.
public sealed class ModuleCompositionTests
{
    private static readonly (Type Spi, string ImplementingModule)[] Spis =
    [
        (typeof(IAccountSessionLifecycleHandler), "Mmorpg.GameService.Placement"),
        (typeof(ICharacterLifecycleHandler), "Mmorpg.GameService.Items"),
        (typeof(ISimulationCharacterStateProvider), "Mmorpg.GameService.Items"),
        (typeof(IShardWorldLocalStateProbe), "Mmorpg.GameService.Items"),
        (typeof(IQuestCatalogRevisionProvider), "Mmorpg.GameService.Quests"),
        // Items' quest drop demand, item catalog check, and gathering credit, which Quests
        // implements.
        (typeof(IQuestDropDemand), "Mmorpg.GameService.Quests"),
        (typeof(IItemCatalogReferences), "Mmorpg.GameService.Quests"),
        (typeof(IQuestGatherCredit), "Mmorpg.GameService.Quests")
    ];

    private static readonly (Type Contract, string ImplementingModule)[] Contracts =
    [
        (typeof(IAccountLocks), "Mmorpg.GameService.Accounts"),
        (typeof(ICharacterLocks), "Mmorpg.GameService.Characters"),
        (typeof(ICharacterWallet), "Mmorpg.GameService.Characters"),
        (typeof(ISimulationSessionAuthority), "Mmorpg.GameService.Placement"),
        (typeof(ISimulationWorkerAuthority), "Mmorpg.GameService.Placement"),
        (typeof(ISimulationPresence), "Mmorpg.GameService.Placement"),
        (typeof(ICharacterQuestLog), "Mmorpg.GameService.Quests")
    ];

    [Fact]
    public void EachSpiAndContractHasExactlyOneImplementationFromItsModule()
    {
        var services = ComposeProductionServices();

        foreach (var (type, module) in Spis.Concat(Contracts))
        {
            var descriptor = Assert.Single(services, descriptor => descriptor.ServiceType == type);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.Equal(module, descriptor.ImplementationType?.Assembly.GetName().Name);
        }
    }

    [Fact]
    public void CompositionRejectsAMissingSpiImplementation()
    {
        var services = ComposeProductionServices();
        services.Remove(services.Single(descriptor =>
            descriptor.ServiceType == typeof(ICharacterLifecycleHandler)));

        var exception = Assert.Throws<InvalidOperationException>(
            () => GameServiceComposition.ValidateSpis(services));

        Assert.Contains(nameof(ICharacterLifecycleHandler), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositionRejectsADuplicateSpiImplementation()
    {
        var services = ComposeProductionServices();
        services.Add(services.Single(descriptor =>
            descriptor.ServiceType == typeof(IAccountSessionLifecycleHandler)));

        var exception = Assert.Throws<InvalidOperationException>(
            () => GameServiceComposition.ValidateSpis(services));

        Assert.Contains(nameof(IAccountSessionLifecycleHandler), exception.Message, StringComparison.Ordinal);
    }

    private static IServiceCollection ComposeProductionServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGameService(GameServiceTestComposition.CreateConfiguration(
            "Host=localhost;Database=module_composition_test;Username=test;Password=test"));
        return services;
    }
}
