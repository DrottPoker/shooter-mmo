using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.Server.Common.Identity;
using Mmorpg.WorldData.Actors;
using Npgsql;

namespace Mmorpg.GameService.Items;

// The quest drops that a dying Mob rolled for one credited character of a live corpse, with the
// revision of the quest catalog the worker rolled them by.
internal sealed record SimulationQuestDropRequest(
    Guid AccountId,
    Guid CharacterId,
    string? WorkerId,
    string? WorkerRuntimeId,
    string? ShardId,
    string? SessionToken,
    string? SourceActorDefinitionId,
    IReadOnlyList<SimulationQuestDrop>? Drops,
    string? QuestCatalogRevision = null);

internal sealed record SimulationQuestDrop(string? QuestId, string? DefinitionId, int Quantity);

// The drops that the character's quests still need, each at most what they need, in request
// order; a drop that no quest needs is left out.
internal sealed record SimulationQuestDropResponse(IReadOnlyList<SimulationQuestDrop> Drops);

// Before it lays out a live Mob corpse, the worker asks how much of the quest drops it rolled for
// the corpse's owner the owner's quests still need, so a quest drop never lies in a corpse for a
// player who has enough. A live corpse is worker state, so this only reads; the grant checks the
// need again. The credentials must name the active simulation session, as for every other
// worker read. What a quest asks for comes from the quest side, and what the owner already has
// and how drops of one item share it from the item primitives.
internal sealed class SimulationQuestDropService(
    NpgsqlDataSource dataSource,
    ISimulationSessionAuthority sessionAuthority,
    IQuestDefinitions questDefinitions,
    IQuestDropDemand questDropDemand)
{
    // The codes of the other worker reads, so the worker handles every read alike.
    private const string WrongSimulationWorker = "wrong_simulation_worker";
    private const string WorkerRuntimeChanged = "worker_runtime_changed";
    private const string SessionInvalid = "simulation_session_invalid";

    public async Task<ServiceResult<SimulationQuestDropResponse>> ResolveAsync(
        string authenticatedWorkerId,
        Guid simulationSessionId,
        SimulationQuestDropRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(authenticatedWorkerId, request.WorkerId, StringComparison.Ordinal))
        {
            return ServiceResult<SimulationQuestDropResponse>.Forbidden(
                WrongSimulationWorker,
                "The authenticated simulation worker cannot resolve another worker's quest drops.");
        }

        if (!IsValid(simulationSessionId, request))
        {
            return ServiceResult<SimulationQuestDropResponse>.BadRequest(
                ItemApiErrorCodes.SimulationOperationInvalid,
                "The quest drop request is incomplete or invalid.");
        }

        if (!QuestCatalogFence.Admits(questDefinitions, request.QuestCatalogRevision))
        {
            return ServiceResult<SimulationQuestDropResponse>.Conflict(
                QuestCatalogFence.MismatchCode,
                QuestCatalogFence.MismatchMessage);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "set transaction read only;",
                transaction: transaction,
                cancellationToken: cancellationToken));
            var scope = new PostgresTransactionScope(connection, transaction);
            var session = await sessionAuthority.GetReadAuthorityAsync(scope, simulationSessionId, cancellationToken);
            var rejection = Validate(session, request);
            if (rejection is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return rejection;
            }

            var plan = new LineageNeedPlan();
            var needed = new List<SimulationQuestDrop>(request.Drops!.Count);
            foreach (var drop in request.Drops)
            {
                var questId = drop.QuestId!.Trim();
                var sourceActorDefinitionId = request.SourceActorDefinitionId!.Trim();
                var definitionId = drop.DefinitionId!.Trim();
                var quantity = await plan.TakeAsync(
                    scope,
                    request.CharacterId,
                    QuestLineage.Of(questId),
                    definitionId,
                    drop.Quantity,
                    token => questDropDemand.WantedAsync(
                        scope,
                        request.CharacterId,
                        questId,
                        sourceActorDefinitionId,
                        definitionId,
                        token),
                    cancellationToken);
                if (quantity > 0)
                {
                    needed.Add(new SimulationQuestDrop(drop.QuestId.Trim(), drop.DefinitionId.Trim(), quantity));
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<SimulationQuestDropResponse>.Ok(new SimulationQuestDropResponse(needed));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static bool IsValid(Guid simulationSessionId, SimulationQuestDropRequest request)
    {
        return simulationSessionId != Guid.Empty
            && request.AccountId != Guid.Empty
            && request.CharacterId != Guid.Empty
            && !string.IsNullOrWhiteSpace(request.WorkerRuntimeId)
            && !string.IsNullOrWhiteSpace(request.ShardId)
            && !string.IsNullOrWhiteSpace(request.SessionToken)
            && request.SessionToken.Length <= 1024
            && Identifiers.IsContentIdentifier(request.SourceActorDefinitionId)
            && request.Drops is { Count: >= 1 and <= WorldActorCorpseRules.MaximumQuestDropCandidates }
            && request.Drops.All(drop =>
                drop is not null
                && Identifiers.IsContentIdentifier(drop.QuestId)
                && Identifiers.IsContentIdentifier(drop.DefinitionId)
                && drop.Quantity > 0);
    }

    // The same checks in the same order as the worker's corpse view read.
    private static ServiceResult<SimulationQuestDropResponse>? Validate(
        SimulationSessionReadAuthority? session,
        SimulationQuestDropRequest request)
    {
        if (session is null || !session.SessionIsActive)
        {
            return ServiceResult<SimulationQuestDropResponse>.Unauthorized(
                SessionInvalid,
                "The simulation session is invalid, released, or expired.");
        }

        if (session.AccountId != request.AccountId
            || session.CharacterId != request.CharacterId
            || !string.Equals(session.ShardId, request.ShardId, StringComparison.Ordinal)
            || !string.Equals(session.WorkerId, request.WorkerId, StringComparison.Ordinal)
            || !HashesMatch(session.SessionTokenHash, TokenGenerator.HashToken(request.SessionToken!.Trim())))
        {
            return ServiceResult<SimulationQuestDropResponse>.Unauthorized(
                SessionInvalid,
                "The quest drop credentials do not match the active simulation session.");
        }

        if (!string.Equals(session.WorkerRuntimeId, request.WorkerRuntimeId, StringComparison.Ordinal)
            || !session.WorkerHasFreshLease)
        {
            return ServiceResult<SimulationQuestDropResponse>.Conflict(
                WorkerRuntimeChanged,
                "The simulation worker runtime no longer owns a fresh registry lease.");
        }

        return session.WorkerHasAssignment
            ? null
            : ServiceResult<SimulationQuestDropResponse>.Forbidden(
                WrongSimulationWorker,
                "The simulation worker no longer owns the requested Shard assignment.");
    }

    private static bool HashesMatch(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
