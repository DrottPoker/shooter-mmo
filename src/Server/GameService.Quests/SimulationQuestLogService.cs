using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.GameService.Quests.Contracts;
using Npgsql;

namespace Mmorpg.GameService.Quests;

// The worker's read of a live character's quest log when the character joins its Shard. The
// credentials must name the active simulation session, as for every other worker read.
internal sealed record SimulationQuestLogRequest(
    Guid AccountId,
    Guid CharacterId,
    string? WorkerId,
    string? WorkerRuntimeId,
    string? ShardId,
    string? SessionToken);

internal sealed class SimulationQuestLogService(
    NpgsqlDataSource dataSource,
    ISimulationSessionAuthority sessionAuthority,
    ICharacterQuestLog questLog,
    IQuestDefinitions questDefinitions)
{
    // The codes of the other worker reads, so the worker handles every read alike.
    private const string WrongSimulationWorker = "wrong_simulation_worker";
    private const string WorkerRuntimeChanged = "worker_runtime_changed";
    private const string SessionInvalid = "simulation_session_invalid";

    public async Task<ServiceResult<CharacterQuestLogResponse>> ReadAsync(
        string authenticatedWorkerId,
        Guid simulationSessionId,
        SimulationQuestLogRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(authenticatedWorkerId, request.WorkerId, StringComparison.Ordinal))
        {
            return ServiceResult<CharacterQuestLogResponse>.Forbidden(
                WrongSimulationWorker,
                "The authenticated simulation worker cannot read another worker's quest log.");
        }

        if (simulationSessionId == Guid.Empty
            || request.AccountId == Guid.Empty
            || request.CharacterId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.WorkerRuntimeId)
            || string.IsNullOrWhiteSpace(request.ShardId)
            || string.IsNullOrWhiteSpace(request.SessionToken)
            || request.SessionToken.Length > 1024)
        {
            return ServiceResult<CharacterQuestLogResponse>.BadRequest(
                "invalid_simulation_session_request",
                "The quest log request is incomplete or invalid.");
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

            var state = await questLog.ReadAsync(scope, request.CharacterId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<CharacterQuestLogResponse>.Ok(
                QuestLogResponses.From(request.CharacterId, questDefinitions.Revision, state));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // The same checks in the same order as the worker's corpse view read.
    private static ServiceResult<CharacterQuestLogResponse>? Validate(
        SimulationSessionReadAuthority? session,
        SimulationQuestLogRequest request)
    {
        if (session is null || !session.SessionIsActive)
        {
            return ServiceResult<CharacterQuestLogResponse>.Unauthorized(
                SessionInvalid,
                "The simulation session is invalid, released, or expired.");
        }

        if (session.AccountId != request.AccountId
            || session.CharacterId != request.CharacterId
            || !string.Equals(session.ShardId, request.ShardId, StringComparison.Ordinal)
            || !string.Equals(session.WorkerId, request.WorkerId, StringComparison.Ordinal)
            || !HashesMatch(session.SessionTokenHash, TokenGenerator.HashToken(request.SessionToken!.Trim())))
        {
            return ServiceResult<CharacterQuestLogResponse>.Unauthorized(
                SessionInvalid,
                "The quest log credentials do not match the active simulation session.");
        }

        if (!string.Equals(session.WorkerRuntimeId, request.WorkerRuntimeId, StringComparison.Ordinal)
            || !session.WorkerHasFreshLease)
        {
            return ServiceResult<CharacterQuestLogResponse>.Conflict(
                WorkerRuntimeChanged,
                "The simulation worker runtime no longer owns a fresh registry lease.");
        }

        return session.WorkerHasAssignment
            ? null
            : ServiceResult<CharacterQuestLogResponse>.Forbidden(
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
