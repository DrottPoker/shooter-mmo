using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Quests;
using Mmorpg.GameService.Testing;

namespace Mmorpg.GameService.Economy.Tests;

// Pins the economy part of the item command catalog, as ItemCommandCatalogTests does for Items'
// part: one registration per economy operation kind, no flags, and one source per handler.
// Economy is the highest module with item commands, so it also pins the whole catalog: exactly the
// operation kinds of Items, Quests, and Economy, from sources of those three modules.
public sealed class EconomyCommandCatalogTests
{
    [Fact]
    public async Task TheEconomySourceRegistersEveryEconomyOperationKindExactlyOnce()
    {
        await using var composition = new OfflineItemComposition();
        var kinds = composition.RegistrationsOf(GameServiceAssemblies.Economy)
            .Select(registration => registration.OperationKind)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(3, Constants(typeof(EconomyOperationKinds)).Length);
        Assert.Equal(Constants(typeof(EconomyOperationKinds)), kinds);
    }

    [Fact]
    public async Task TheCatalogHoldsExactlyTheItemQuestAndEconomyOperationKinds()
    {
        await using var composition = new OfflineItemComposition();
        var sourceModules = composition.Services.GetServices<IItemCommandSource>()
            .Select(source => source.GetType().Assembly)
            .Distinct()
            .ToArray();
        var kinds = composition.Catalog.Registrations
            .Select(registration => registration.OperationKind)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { GameServiceAssemblies.Items, GameServiceAssemblies.Quests, GameServiceAssemblies.Economy },
            sourceModules);
        Assert.Equal(48, kinds.Length);
        Assert.Equal(
            Constants(typeof(ItemOperationKinds))
                .Concat(Constants(typeof(QuestOperationKinds)))
                .Concat(Constants(typeof(EconomyOperationKinds)))
                .Order(StringComparer.Ordinal),
            kinds);
    }

    [Fact]
    public async Task NoEconomyKindCarriesAFlag()
    {
        await using var composition = new OfflineItemComposition();
        var registrations = composition.RegistrationsOf(GameServiceAssemblies.Economy);

        Assert.Equal(3, registrations.Count);
        Assert.DoesNotContain(registrations, registration => registration.RetriedSystemOperation);
        Assert.DoesNotContain(registrations, registration => registration.OperationallySignificant);
        Assert.DoesNotContain(registrations, registration => registration.PolicyAction);
        Assert.DoesNotContain(registrations, registration => registration.MayRunAfterAccountSessionEnd);
    }

    [Fact]
    public async Task EveryEconomyHandlerIsRegisteredByTheEconomySource()
    {
        await using var composition = new OfflineItemComposition();
        var sources = composition.Services.GetServices<IItemCommandSource>()
            .Where(source => source.GetType().Assembly == GameServiceAssemblies.Economy)
            .ToArray();
        var registeredHandlers = sources
            .SelectMany(source => source.Registrations.Select(HandlerType))
            .Distinct()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var handlerTypes = GameServiceAssemblies.Economy
            .GetTypes()
            .Where(type => !type.IsAbstract
                && type.GetInterfaces().Any(contract => contract.IsGenericType
                    && contract.GetGenericTypeDefinition() == typeof(IItemCommandHandler<>)))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal("EconomyCommandSource", Assert.Single(sources).GetType().Name);
        Assert.Equal(2, handlerTypes.Length);
        Assert.Equal(handlerTypes, registeredHandlers);
    }

    private static string[] Constants(Type kinds)
    {
        return kinds
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static Type HandlerType(ItemCommandRegistration registration)
    {
        return registration.GetType()
            .GetProperty("Handler", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(registration)!
            .GetType();
    }
}
