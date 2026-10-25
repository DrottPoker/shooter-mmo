using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.WorldData.Characters;
using Npgsql;

namespace Mmorpg.GameService.Placement;

// Changes the look of the character of a live simulation session for the worker that holds the
// session: its headwear lowered or raised, or in Development another body model and look. It locks
// the character before the session, as item mutations do, and checks the session's token under
// both locks, so a reconnect that rotates the token either commits first and refuses the change,
// or waits for it and loads the changed look.
internal sealed class SimulationAppearanceService(
    NpgsqlDataSource dataSource,
    PlacementModuleOptions options,
    ICharacterLocks characterLocks,
    ICharacterAppearances characterAppearances)
{
    public const string HeadwearChange = "headwear";
    public const string DevelopmentLookChange = "development_look";

    public async Task<ServiceResult<SimulationAppearance>> ChangeAsync(
        Guid simulationSessionId,
        string workerId,
        SimulationAppearanceChangeRequest request,
        CancellationToken cancellationToken)
    {
        if (simulationSessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(workerId)
            || string.IsNullOrWhiteSpace(request.WorkerRuntimeId)
            || string.IsNullOrWhiteSpace(request.SessionToken))
        {
            return ServiceResult<SimulationAppearance>.BadRequest(
                "invalid_simulation_session_request",
                "Simulation session id, worker id, worker runtime id, and session token are required.");
        }

        var isHeadwear = string.Equals(request.Change, HeadwearChange, StringComparison.Ordinal);
        var isDevelopmentLook = string.Equals(request.Change, DevelopmentLookChange, StringComparison.Ordinal);
        if (!isHeadwear && !isDevelopmentLook)
        {
            return ServiceResult<SimulationAppearance>.BadRequest(
                "invalid_appearance_change",
                $"Change must be {HeadwearChange} or {DevelopmentLookChange}.");
        }

        if (isHeadwear && request.HeadwearLowered is null)
        {
            return ServiceResult<SimulationAppearance>.BadRequest(
                "invalid_appearance_change",
                "A headwear change needs HeadwearLowered.");
        }

        if (isDevelopmentLook && !options.DevelopmentLooks)
        {
            return ServiceResult<SimulationAppearance>.Forbidden(
                "development_look_disabled",
                "Only a Development GameService changes a body model or a look; character creation chooses them.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var scope = new PostgresTransactionScope(connection, transaction);
        var characterId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            select character_id
            from character_simulation_sessions
            where id = @SimulationSessionId;
            """,
            new { SimulationSessionId = simulationSessionId },
            transaction,
            cancellationToken: cancellationToken));
        if (characterId is null
            || await characterLocks.LockActiveForItemMutationAsync(scope, characterId.Value, cancellationToken) is null
            || !await LockLiveSessionAsync(scope, simulationSessionId, characterId.Value, workerId, request, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<SimulationAppearance>.Unauthorized(
                "invalid_simulation_session",
                "Simulation session is invalid, released, expired, or owned by another worker runtime.");
        }

        var change = isHeadwear
            ? await characterAppearances.SetHeadwearLoweredAsync(scope, characterId.Value, request.HeadwearLowered!.Value, cancellationToken)
            : await characterAppearances.SetDevelopmentLookAsync(
                scope,
                characterId.Value,
                request.BodyModel ?? string.Empty,
                (request.Choices ?? [])
                    .Select(choice => new CharacterAppearanceChoice(choice?.Kind, choice?.Slot, choice?.Option))
                    .ToArray(),
                cancellationToken);
        if (change.Appearance is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<SimulationAppearance>.BadRequest(change.ErrorCode!, change.ErrorMessage!);
        }

        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<SimulationAppearance>.Ok(SimulationAppearance.From(change.Appearance));
    }

    // Locks the session FOR UPDATE when it is live, still the character's, and held by this
    // worker runtime with this session token.
    private static async Task<bool> LockLiveSessionAsync(
        PostgresTransactionScope scope,
        Guid simulationSessionId,
        Guid characterId,
        string workerId,
        SimulationAppearanceChangeRequest request,
        CancellationToken cancellationToken)
    {
        var locked = await scope.Connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            $"""
            select id
            from character_simulation_sessions
            where id = @SimulationSessionId
              and character_id = @CharacterId
              and simulation_worker_id = @WorkerId
              and worker_runtime_id = @WorkerRuntimeId
              and session_token_hash = @SessionTokenHash
              and released_at is null
              and expires_at > now()
              and {AccountsSql.IsActiveOrUnboundSession("character_simulation_sessions.account_session_id")}
            for update of character_simulation_sessions;
            """,
            new
            {
                SimulationSessionId = simulationSessionId,
                CharacterId = characterId,
                WorkerId = workerId.Trim(),
                WorkerRuntimeId = request.WorkerRuntimeId!.Trim(),
                SessionTokenHash = TokenGenerator.HashToken(request.SessionToken!.Trim())
            },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return locked is not null;
    }
}
