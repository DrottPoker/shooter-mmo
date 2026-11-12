using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Npgsql;

namespace Mmorpg.GameService.Quests.Tests;

// Pins the persisted operation kind of every quest command, as ItemTransactionRoutingTests does
// for the item commands. An account actor with a derived (version 5) operation id is rejected
// before any database access, so the complete routing table runs without PostgreSQL.
public sealed class QuestTransactionRoutingTests
{
    private static readonly Guid DerivedOperationId = Guid.Parse("8f14e45f-ceea-567a-9a6f-b3f0a2c4d1e8");
    private static readonly Guid Id = Guid.Parse("0f0e0d0c-0b0a-4908-8706-050403020100");

    public static TheoryData<string> OperationKinds()
    {
        return new TheoryData<string>(Routes.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(OperationKinds))]
    public async Task EveryCommandRoutesToItsPersistedOperationKind(string operationKind)
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        await using var services = GameServiceTestComposition.BuildServiceProvider(dataSource);
        await using var scope = services.CreateAsyncScope();
        var transactions = scope.ServiceProvider.GetRequiredService<IItemTransactions>();

        var result = await Routes[operationKind](
            transactions,
            ItemTransactionActor.ForAccount(Guid.NewGuid()));

        Assert.False(result.Succeeded);
        Assert.Equal(DerivedOperationId, result.OperationId);
        Assert.Equal(operationKind, result.OperationKind);
        Assert.Equal(ItemTransactionErrorCodes.ItemOperationIdInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task RoutingTableCoversEveryQuestRegistration()
    {
        await using var composition = new OfflineItemComposition();
        var registrations = composition.RegistrationsOf(GameServiceAssemblies.Quests);

        Assert.Equal(15, registrations.Count);
        Assert.Equal(15, registrations.Select(registration => registration.CommandType).Distinct().Count());
        Assert.Equal(15, Routes.Count);
        Assert.Equal(
            registrations.Select(registration => registration.OperationKind).Order(StringComparer.Ordinal),
            Routes.Keys.Order(StringComparer.Ordinal));
    }

    private static Task<ItemTransactionResult> Run<TCommand>(
        IItemTransactions service,
        ItemTransactionActor actor,
        TCommand command,
        Func<IItemTransactions, ItemTransactionRequest<TCommand>, Task<ItemTransactionResult>> execute)
        where TCommand : notnull
    {
        return execute(service, new ItemTransactionRequest<TCommand>(DerivedOperationId, actor, command));
    }

    private static readonly IReadOnlyDictionary<
        string,
        Func<IItemTransactions, ItemTransactionActor, Task<ItemTransactionResult>>> Routes =
        new Dictionary<string, Func<IItemTransactions, ItemTransactionActor, Task<ItemTransactionResult>>>(
            StringComparer.Ordinal)
        {
            [QuestOperationKinds.AbandonQuestItems] = (service, actor) => Run(
                service,
                actor,
                new AbandonQuestItemsCommand(Id, 1, "routing"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.AcceptQuest] = (service, actor) => Run(
                service,
                actor,
                new AcceptQuestCommand(Id, "quest.fixture.wolves"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.AbandonQuest] = (service, actor) => Run(
                service,
                actor,
                new AbandonQuestCommand(Id, "quest.fixture.wolves"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.RegrantQuestItems] = (service, actor) => Run(
                service,
                actor,
                new RegrantQuestItemsCommand(Id, "quest.fixture.errand"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.TalkToQuestNpc] = (service, actor) => Run(
                service,
                actor,
                new TalkToQuestNpcCommand(Id, "quest.fixture.errand", "report"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.DeliverQuestItems] = (service, actor) => Run(
                service,
                actor,
                new DeliverQuestItemsCommand(Id, "quest.fixture.errand", "hand_over"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.CreditQuestKill] = (service, actor) => Run(
                service,
                actor,
                new CreditQuestKillCommand(Id, "mob.feral_wolf", []),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.TurnInQuest] = (service, actor) => Run(
                service,
                actor,
                new TurnInQuestCommand(Id, "quest.fixture.wolves", "fixture.sword"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.AdminAcceptQuest] = (service, actor) => Run(
                service,
                actor,
                new AdminAcceptQuestCommand(Id, "quest.fixture.wolves"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.AdminCompleteQuestObjective] = (service, actor) => Run(
                service,
                actor,
                new AdminCompleteQuestObjectiveCommand(Id, "quest.fixture.wolves", "wolves"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.AdminCompleteQuestStep] = (service, actor) => Run(
                service,
                actor,
                new AdminCompleteQuestStepCommand(Id, "quest.fixture.wolves"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.AdminMakeQuestReady] = (service, actor) => Run(
                service,
                actor,
                new AdminMakeQuestReadyCommand(Id, "quest.fixture.wolves"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.AdminResetQuest] = (service, actor) => Run(
                service,
                actor,
                new AdminResetQuestCommand(Id, "quest.fixture.wolves"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.AdminResetQuestChain] = (service, actor) => Run(
                service,
                actor,
                new AdminResetQuestChainCommand(Id, "quest.fixture.wolves"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [QuestOperationKinds.AdminResetQuests] = (service, actor) => Run(
                service,
                actor,
                new AdminResetQuestsCommand(Id),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None))
        };
}
