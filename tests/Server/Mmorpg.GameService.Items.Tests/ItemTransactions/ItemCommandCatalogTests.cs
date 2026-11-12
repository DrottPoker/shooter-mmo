using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Testing;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// Pins the item command catalog of the module composition: one registration per operation
// kind, the kinds that carry each flag, one source per handler, and the failures for a
// duplicate or missing registration.
public sealed class ItemCommandCatalogTests
{
    [Fact]
    public async Task ModuleCatalogRegistersEveryOperationKindExactlyOnce()
    {
        await using var composition = new OfflineItemComposition();
        var constants = typeof(ItemOperationKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var kinds = composition.Catalog.Registrations
            .Select(registration => registration.OperationKind)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(48, constants.Length);
        Assert.Equal(constants, kinds);
    }

    [Fact]
    public async Task RetriedSystemOperationsArePinned()
    {
        await using var composition = new OfflineItemComposition();

        Assert.Equal(
            new[]
            {
                ItemOperationKinds.ProcessPlayerDeath,
                ItemOperationKinds.CreatePersistentMobCorpse,
                ItemOperationKinds.GrantMobLoot,
                ItemOperationKinds.ExpireCorpse,
                ItemOperationKinds.ExpireRecoveryDelivery
            }.Order(StringComparer.Ordinal),
            KindsWhere(composition, registration => registration.RetriedSystemOperation));
    }

    [Fact]
    public async Task OperationallySignificantKindsArePinned()
    {
        await using var composition = new OfflineItemComposition();

        Assert.Equal(
            new[]
            {
                ItemOperationKinds.ProcessPlayerDeath,
                ItemOperationKinds.CreatePersistentMobCorpse,
                ItemOperationKinds.ExpireCorpse,
                ItemOperationKinds.ApplyItemPolicy,
                ItemOperationKinds.RemoveInsurancePolicy,
                ItemOperationKinds.AbandonQuestItems
            }.Order(StringComparer.Ordinal),
            KindsWhere(composition, registration => registration.OperationallySignificant));
    }

    [Fact]
    public async Task EveryHandlerIsRegisteredByExactlyOneSource()
    {
        await using var composition = new OfflineItemComposition();
        var sources = composition.Services.GetServices<IItemCommandSource>().ToArray();
        var handlerSources = sources
            .SelectMany(source => source.Registrations
                .Select(registration => (Handler: HandlerType(registration), Source: source.GetType())))
            .Distinct()
            .GroupBy(pair => pair.Handler)
            .ToArray();
        var handlerTypes = GameServiceAssemblies.All
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract
                && type.GetInterfaces().Any(contract => contract.IsGenericType
                    && contract.GetGenericTypeDefinition() == typeof(IItemCommandHandler<>)))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(5, sources.Length);
        Assert.All(handlerSources, group => Assert.Single(group));
        Assert.Equal(24, handlerTypes.Length);
        Assert.Equal(
            handlerTypes,
            handlerSources.Select(group => group.Key.FullName!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DuplicateCommandTypeFailsNamingBothRegistrations()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new ItemCommandCatalog(
        [
            new FirstSource(Registration<GrantItemCommand>("test_first")),
            new SecondSource(Registration<GrantItemCommand>("test_second"))
        ]));

        Assert.Equal(
            "Two item command registrations share the command type: "
            + "GrantItemCommand as 'test_first' from FirstSource and "
            + "GrantItemCommand as 'test_second' from SecondSource.",
            exception.Message);
    }

    [Fact]
    public void DuplicateOperationKindFailsNamingBothRegistrations()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new ItemCommandCatalog(
        [
            new FirstSource(Registration<GrantItemCommand>("test_kind")),
            new SecondSource(Registration<RelocateItemCommand>("test_kind"))
        ]));

        Assert.Equal(
            "Two item command registrations share the operation kind: "
            + "GrantItemCommand as 'test_kind' from FirstSource and "
            + "RelocateItemCommand as 'test_kind' from SecondSource.",
            exception.Message);
    }

    [Fact]
    public async Task CommandWithoutRegistrationFailsNamingItsType()
    {
        await using var composition = new OfflineItemComposition();
        var service = ActivatorUtilities.CreateInstance<ItemTransactionService>(composition.Services);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(
            new ItemTransactionRequest<UnregisteredCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new UnregisteredCommand(Guid.NewGuid())),
            CancellationToken.None));

        Assert.Equal(
            $"The item command type '{typeof(UnregisteredCommand).FullName}' has no registration in the item command catalog.",
            exception.Message);
    }

    private static string[] KindsWhere(
        OfflineItemComposition composition,
        Func<ItemCommandRegistration, bool> predicate)
    {
        return composition.Catalog.Registrations
            .Where(predicate)
            .Select(registration => registration.OperationKind)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static Type HandlerType(ItemCommandRegistration registration)
    {
        return registration.GetType()
            .GetProperty(nameof(ItemCommandRegistration<GrantItemCommand>.Handler))!
            .GetValue(registration)!
            .GetType();
    }

    private static ItemCommandRegistration<TCommand> Registration<TCommand>(string operationKind)
        where TCommand : notnull
    {
        return new ItemCommandRegistration<TCommand>(operationKind, static _ => null, new NoHandler<TCommand>());
    }

    private sealed record UnregisteredCommand(Guid CharacterId);

    private sealed class NoHandler<TCommand> : IItemCommandHandler<TCommand>
        where TCommand : notnull
    {
        public Task ExecuteAsync(
            ItemTransactionContext context,
            TCommand command,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The catalog tests never run a handler.");
        }
    }

    private sealed class FirstSource(params ItemCommandRegistration[] registrations) : IItemCommandSource
    {
        public IReadOnlyList<ItemCommandRegistration> Registrations { get; } = registrations;
    }

    private sealed class SecondSource(params ItemCommandRegistration[] registrations) : IItemCommandSource
    {
        public IReadOnlyList<ItemCommandRegistration> Registrations { get; } = registrations;
    }
}
