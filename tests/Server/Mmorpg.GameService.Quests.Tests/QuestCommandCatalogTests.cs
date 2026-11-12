using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;

namespace Mmorpg.GameService.Quests.Tests;

// Pins the quest part of the item command catalog, as ItemCommandCatalogTests does for Items'
// part: one registration per quest operation kind, the quest kinds that carry each flag, and one
// source per handler. Quests is the highest module with item commands, so it also pins the whole
// catalog: exactly the operation kinds of Items and Quests, from sources of those two modules.
public sealed class QuestCommandCatalogTests
{
    [Fact]
    public async Task TheQuestSourceRegistersEveryQuestOperationKindExactlyOnce()
    {
        await using var composition = new OfflineItemComposition();
        var kinds = composition.RegistrationsOf(GameServiceAssemblies.Quests)
            .Select(registration => registration.OperationKind)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(15, Constants(typeof(QuestOperationKinds)).Length);
        Assert.Equal(Constants(typeof(QuestOperationKinds)), kinds);
    }

    [Fact]
    public async Task TheCatalogHoldsExactlyTheItemAndQuestOperationKinds()
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

        Assert.Equal(new[] { GameServiceAssemblies.Items, GameServiceAssemblies.Quests }, sourceModules);
        Assert.Equal(48, kinds.Length);
        Assert.Equal(
            Constants(typeof(ItemOperationKinds)).Concat(Constants(typeof(QuestOperationKinds))).Order(StringComparer.Ordinal),
            kinds);
    }

    [Fact]
    public async Task TheFlagsOfTheQuestKindsArePinned()
    {
        await using var composition = new OfflineItemComposition();
        var registrations = composition.RegistrationsOf(GameServiceAssemblies.Quests);

        Assert.DoesNotContain(registrations, registration => registration.RetriedSystemOperation);
        Assert.Equal(
            new[] { QuestOperationKinds.AbandonQuestItems },
            registrations.Where(registration => registration.OperationallySignificant)
                .Select(registration => registration.OperationKind));
        Assert.Equal(
            new[] { QuestOperationKinds.AbandonQuestItems },
            registrations.Where(registration => registration.PolicyAction)
                .Select(registration => registration.OperationKind));
    }

    [Fact]
    public async Task EveryQuestHandlerIsRegisteredByTheQuestSource()
    {
        await using var composition = new OfflineItemComposition();
        var sources = composition.Services.GetServices<IItemCommandSource>()
            .Where(source => source.GetType().Assembly == GameServiceAssemblies.Quests)
            .ToArray();
        var registeredHandlers = sources
            .SelectMany(source => source.Registrations.Select(HandlerType))
            .Distinct()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var handlerTypes = GameServiceAssemblies.Quests
            .GetTypes()
            .Where(type => !type.IsAbstract
                && type.GetInterfaces().Any(contract => contract.IsGenericType
                    && contract.GetGenericTypeDefinition() == typeof(IItemCommandHandler<>)))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal("QuestCommandSource", Assert.Single(sources).GetType().Name);
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
