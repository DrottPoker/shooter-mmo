using System.Data;
using AuthService.Auth;
using AuthService.Config;
using AuthService.Http;
using Dapper;
using Npgsql;

namespace AuthService.Items;

public sealed class CorpseService(
    NpgsqlDataSource dataSource,
    ItemTransactionService transactionService,
    AuthServiceConfig authServiceConfig,
    ILogger<CorpseService> logger)
{
    public async Task<ServiceResult<PlayerDeathPartitionResponse>> ProcessSimulationDeathAsync(
        string authenticatedWorkerId,
        Guid simulationSessionId,
        SimulationPlayerDeathRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                authenticatedWorkerId,
                request.WorkerId,
                StringComparison.Ordinal))
        {
            return ServiceResult<PlayerDeathPartitionResponse>.Forbidden(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The authenticated simulation worker cannot submit another worker's death event.");
        }

        if (!IsValidSimulationDeathRequest(simulationSessionId, request))
        {
            return ServiceResult<PlayerDeathPartitionResponse>.BadRequest(
                ItemApiErrorCodes.SimulationOperationInvalid,
                "The simulation player-death request is incomplete or invalid.");
        }

        var actor = ItemTransactionActor.ForSimulationWorker(
            request.AccountId,
            request.CharacterId,
            simulationSessionId,
            request.WorkerId.Trim(),
            request.WorkerRuntimeId.Trim(),
            request.ShardId.Trim(),
            TokenGenerator.HashToken(request.SessionToken.Trim()),
            ItemTransactionLiveAccess.None);
        return await ProcessDeathAsync(
            request.OperationId,
            actor,
            new ProcessPlayerDeathCommand(
                request.DeathEventId,
                request.CharacterId,
                request.ExpectedCharacterRevision,
                request.ShardId.Trim(),
                request.PositionX,
                request.PositionY,
                request.PositionZ,
                request.RotationX,
                request.RotationY,
                request.RotationZ,
                request.RotationW,
                request.PresentationKey.Trim()),
            cancellationToken);
    }

    public Task<ServiceResult<PlayerDeathPartitionResponse>> ProcessSystemDeathAsync(
        Guid operationId,
        ProcessPlayerDeathCommand command,
        CancellationToken cancellationToken)
    {
        return ProcessDeathAsync(
            operationId,
            ItemTransactionActor.ForSystem(),
            command,
            cancellationToken);
    }

    public async Task<ServiceResult<CorpseRestoreResponse>> ListForWorkerAsync(
        string workerId,
        string workerRuntimeId,
        string shardId,
        CancellationToken cancellationToken)
    {
        if (!IsValidIdentifier(workerId)
            || !IsValidIdentifier(workerRuntimeId)
            || !IsValidIdentifier(shardId))
        {
            return ServiceResult<CorpseRestoreResponse>.BadRequest(
                ItemApiErrorCodes.SimulationOperationInvalid,
                "The corpse restoration identity is incomplete or invalid.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "set transaction read only;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        try
        {
            var authority = await connection.QuerySingleOrDefaultAsync<CorpseWorkerAuthorityRow>(
                new CommandDefinition(
                    """
                    select
                        worker.runtime_id as "RuntimeId",
                        worker.is_online
                            and worker.last_heartbeat_at is not null
                            and worker.last_heartbeat_at > now() - make_interval(secs => @HeartbeatTimeoutSeconds)
                            as "HasFreshLease",
                        exists (
                            select 1
                            from simulation_assignments assignment
                            where assignment.worker_id = worker.id
                              and assignment.shard_id = @ShardId
                              and assignment.released_at is null) as "HasAssignment"
                    from simulation_workers worker
                    where worker.id = @WorkerId;
                    """,
                    new
                    {
                        WorkerId = workerId,
                        ShardId = shardId,
                        HeartbeatTimeoutSeconds = authServiceConfig.SimulationWorkerHeartbeatTimeout.TotalSeconds
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            if (authority is null
                || !authority.HasFreshLease
                || !string.Equals(
                    authority.RuntimeId,
                    workerRuntimeId,
                    StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                return ServiceResult<CorpseRestoreResponse>.Conflict(
                    ItemTransactionErrorCodes.WorkerRuntimeChanged,
                    "The simulation worker runtime no longer owns a fresh registry lease.");
            }

            if (!authority.HasAssignment)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ServiceResult<CorpseRestoreResponse>.Forbidden(
                    ItemTransactionErrorCodes.WrongSimulationWorker,
                    "The simulation worker does not own the requested Shard assignment.");
            }

            var databaseTime = await connection.QuerySingleAsync<DateTime>(new CommandDefinition(
                "select now();",
                transaction: transaction,
                cancellationToken: cancellationToken));
            var corpses = await LoadOpenCorpsesAsync(
                connection,
                transaction,
                shardId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<CorpseRestoreResponse>.Ok(
                new CorpseRestoreResponse(databaseTime, corpses));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ServiceResult<ItemTransactionResult>> ExpireCorpseAsync(
        Guid corpseId,
        CancellationToken cancellationToken)
    {
        var candidate = await LoadExpiryCandidateAsync(corpseId, cancellationToken);
        if (candidate is null)
        {
            return ServiceResult<ItemTransactionResult>.NotFound(
                ItemTransactionErrorCodes.CorpseNotFound,
                "The corpse was not found.");
        }

        if (!candidate.IsDue && !candidate.IsClosed)
        {
            return ServiceResult<ItemTransactionResult>.Conflict(
                ItemTransactionErrorCodes.CorpseNotExpired,
                "The corpse has not reached its absolute expiry time.");
        }

        var result = await transactionService.ExecuteAsync(
            new ItemTransactionRequest<ExpireCorpseCommand>(
                candidate.ExpiryOperationId,
                ItemTransactionActor.ForSystem(),
                new ExpireCorpseCommand(corpseId)),
            cancellationToken);
        return MapTransactionResult<ItemTransactionResult>(result, result);
    }

    public async Task<int> ExpireDueCorpsesAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var corpseIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            select id
            from corpses
            where closed_at is null
              and expires_at <= now()
            order by expires_at, id
            limit @MaximumCount;
            """,
            new { MaximumCount = maximumCount },
            cancellationToken: cancellationToken))).ToArray();
        var expiredCount = 0;
        foreach (var corpseId in corpseIds)
        {
            var result = await ExpireCorpseAsync(corpseId, cancellationToken);
            if (result.Succeeded)
            {
                expiredCount++;
                continue;
            }

            logger.LogWarning(
                "Corpse {CorpseId} expiry was rejected with {Code}.",
                corpseId,
                result.Error!.Code);
        }

        return expiredCount;
    }

    private async Task<ServiceResult<PlayerDeathPartitionResponse>> ProcessDeathAsync(
        Guid operationId,
        ItemTransactionActor actor,
        ProcessPlayerDeathCommand command,
        CancellationToken cancellationToken)
    {
        var result = await transactionService.ExecuteAsync(
            new ItemTransactionRequest<ProcessPlayerDeathCommand>(
                operationId,
                actor,
                command),
            cancellationToken);
        if (!result.Succeeded)
        {
            return MapTransactionResult<PlayerDeathPartitionResponse>(result, null);
        }

        var partition = await LoadPartitionRecordAsync(
            command.DeathEventId,
            cancellationToken);
        if (partition is null)
        {
            throw new InvalidOperationException(
                $"Committed death event '{command.DeathEventId}' has no durable partition record.");
        }

        return ServiceResult<PlayerDeathPartitionResponse>.Ok(
            new PlayerDeathPartitionResponse(
                operationId,
                partition.DeathEventId,
                partition.Corpse,
                partition.CharacterRevision,
                partition.RecoveryDeliveryIds));
    }

    private async Task<CorpsePartitionRecord?> LoadPartitionRecordAsync(
        Guid deathEventId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "set transaction read only;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        try
        {
            var row = await connection.QuerySingleOrDefaultAsync<DeathPartitionRow>(
                new CommandDefinition(
                    """
                    select
                        event.id as "DeathEventId",
                        event.operation_id as "OriginalOperationId",
                        coalesce(event.character_id, corpse.source_character_id) as "CharacterId",
                        corpse.id as "CorpseId",
                        corpse.source_character_id as "SourceCharacterId",
                        corpse.source_display_name as "SourceDisplayName",
                        corpse.shard_id as "ShardId",
                        corpse.position_x as "PositionX",
                        corpse.position_y as "PositionY",
                        corpse.position_z as "PositionZ",
                        corpse.rotation_x as "RotationX",
                        corpse.rotation_y as "RotationY",
                        corpse.rotation_z as "RotationZ",
                        corpse.rotation_w as "RotationW",
                        corpse.presentation_key as "PresentationKey",
                        corpse.revision as "CorpseRevision",
                        corpse.created_at as "CreatedAt",
                        corpse.expires_at as "ExpiresAt",
                        state.revision as "CharacterRevision",
                        state.carried_weight as "CarriedWeight",
                        state.carry_capacity as "CarryCapacity"
                    from death_events event
                    join corpses corpse on corpse.id = event.corpse_id
                    join character_item_states state
                      on state.character_id = coalesce(event.character_id, corpse.source_character_id)
                    where event.id = @DeathEventId;
                    """,
                    new { DeathEventId = deathEventId },
                    transaction,
                    cancellationToken: cancellationToken));
            if (row is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            var sections = await LoadCorpseSectionsAsync(
                connection,
                transaction,
                [row.CorpseId],
                cancellationToken);
            var recoveryDeliveryIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from recovery_deliveries
                where character_id = @CharacterId
                  and source_event_id = @SourceEventId
                order by source_kind, id;
                """,
                new
                {
                    row.CharacterId,
                    SourceEventId = deathEventId.ToString("D")
                },
                transaction,
                cancellationToken: cancellationToken))).ToArray();
            await transaction.CommitAsync(cancellationToken);
            var corpse = CreateCorpseResponse(row, sections[row.CorpseId]);
            return new CorpsePartitionRecord(
                row.DeathEventId,
                row.OriginalOperationId,
                row.CharacterId,
                corpse,
                new ItemTransactionCharacterRevision(
                    row.CharacterId,
                    row.CharacterRevision,
                    row.CarriedWeight,
                    row.CarryCapacity),
                recoveryDeliveryIds);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<IReadOnlyList<DurableCorpseResponse>> LoadOpenCorpsesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string shardId,
        CancellationToken cancellationToken)
    {
        var rows = (await connection.QueryAsync<OpenCorpseRow>(new CommandDefinition(
            """
            select
                corpse.id as "CorpseId",
                corpse.source_character_id as "SourceCharacterId",
                corpse.source_display_name as "SourceDisplayName",
                corpse.shard_id as "ShardId",
                corpse.position_x as "PositionX",
                corpse.position_y as "PositionY",
                corpse.position_z as "PositionZ",
                corpse.rotation_x as "RotationX",
                corpse.rotation_y as "RotationY",
                corpse.rotation_z as "RotationZ",
                corpse.rotation_w as "RotationW",
                corpse.presentation_key as "PresentationKey",
                corpse.revision as "CorpseRevision",
                corpse.created_at as "CreatedAt",
                corpse.expires_at as "ExpiresAt"
            from corpses corpse
            where corpse.shard_id = @ShardId
              and corpse.closed_at is null
              and corpse.expires_at > now()
            order by corpse.created_at, corpse.id;
            """,
            new { ShardId = shardId },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        if (rows.Length == 0)
        {
            return [];
        }

        var sections = await LoadCorpseSectionsAsync(
            connection,
            transaction,
            rows.Select(row => row.CorpseId).ToArray(),
            cancellationToken);
        return rows
            .Select(row => CreateCorpseResponse(row, sections[row.CorpseId]))
            .ToArray();
    }

    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CorpseSectionResponse>>>
        LoadCorpseSectionsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid[] corpseIds,
            CancellationToken cancellationToken)
    {
        var rows = (await connection.QueryAsync<CorpseSectionRow>(new CommandDefinition(
            """
            select
                section.corpse_id as "CorpseId",
                section.section_kind as "SectionKind",
                section.container_id as "ContainerId",
                container.revision as "ContainerRevision",
                count(item.id)::integer as "ItemCount"
            from corpse_sections section
            join item_containers container on container.id = section.container_id
            left join item_instances item on item.container_id = section.container_id
            where section.corpse_id = any(@CorpseIds)
            group by
                section.corpse_id,
                section.section_kind,
                section.container_id,
                container.revision
            order by section.corpse_id, section.section_kind;
            """,
            new { CorpseIds = corpseIds },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        return corpseIds.ToDictionary(
            corpseId => corpseId,
            corpseId => (IReadOnlyList<CorpseSectionResponse>)rows
                .Where(row => row.CorpseId == corpseId)
                .Select(row => new CorpseSectionResponse(
                    row.SectionKind,
                    row.ContainerId,
                    row.ContainerRevision,
                    row.ItemCount))
                .ToArray());
    }

    private async Task<CorpseExpiryCandidate?> LoadExpiryCandidateAsync(
        Guid corpseId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<CorpseExpiryCandidate>(new CommandDefinition(
            """
            select
                expiry_operation_id as "ExpiryOperationId",
                expires_at <= now() as "IsDue",
                closed_at is not null as "IsClosed"
            from corpses
            where id = @CorpseId;
            """,
            new { CorpseId = corpseId },
            cancellationToken: cancellationToken));
    }

    private static DurableCorpseResponse CreateCorpseResponse(
        ICorpseRow row,
        IReadOnlyList<CorpseSectionResponse> sections)
    {
        return new DurableCorpseResponse(
            row.CorpseId,
            row.SourceCharacterId,
            row.SourceDisplayName,
            row.ShardId,
            row.PositionX,
            row.PositionY,
            row.PositionZ,
            row.RotationX,
            row.RotationY,
            row.RotationZ,
            row.RotationW,
            row.PresentationKey,
            row.CorpseRevision,
            row.CreatedAt,
            row.ExpiresAt,
            sections.Sum(section => section.ItemCount) == 0,
            sections);
    }

    private static ServiceResult<T> MapTransactionResult<T>(
        ItemTransactionResult result,
        T? successValue)
    {
        if (result.Succeeded)
        {
            return ServiceResult<T>.Ok(successValue!);
        }

        var error = result.Error
            ?? throw new InvalidOperationException("A rejected item operation has no error.");
        return error.Code switch
        {
            ItemTransactionErrorCodes.SimulationSessionInvalid =>
                ServiceResult<T>.Unauthorized(error.Code, error.Message),
            ItemTransactionErrorCodes.AuthorityRequired or
            ItemTransactionErrorCodes.WrongSimulationWorker =>
                ServiceResult<T>.Forbidden(error.Code, error.Message),
            ItemTransactionErrorCodes.ItemNotFound or
            ItemTransactionErrorCodes.ItemNotOwned or
            ItemTransactionErrorCodes.CorpseNotFound =>
                ServiceResult<T>.NotFound(error.Code, error.Message),
            ItemTransactionErrorCodes.ItemStateConflict or
            ItemTransactionErrorCodes.ItemOperationConflict or
            ItemTransactionErrorCodes.DeathEventConflict or
            ItemTransactionErrorCodes.CorpseNotExpired or
            ItemTransactionErrorCodes.CorpseStateChanged or
            ItemTransactionErrorCodes.WorkerRuntimeChanged =>
                ServiceResult<T>.Conflict(error.Code, error.Message),
            _ => ServiceResult<T>.UnprocessableEntity(error.Code, error.Message)
        };
    }

    private static bool IsValidSimulationDeathRequest(
        Guid simulationSessionId,
        SimulationPlayerDeathRequest request)
    {
        return simulationSessionId != Guid.Empty
            && request.OperationId != Guid.Empty
            && request.DeathEventId != Guid.Empty
            && request.AccountId != Guid.Empty
            && request.CharacterId != Guid.Empty
            && request.ExpectedCharacterRevision >= 0
            && IsValidIdentifier(request.WorkerId)
            && IsValidIdentifier(request.WorkerRuntimeId)
            && IsValidIdentifier(request.ShardId)
            && !string.IsNullOrWhiteSpace(request.SessionToken)
            && request.SessionToken.Length <= 1024
            && string.Equals(
                request.PresentationKey,
                ItemTransactionService.DefaultPlayerCorpsePresentationKey,
                StringComparison.Ordinal);
    }

    private static bool IsValidIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private interface ICorpseRow
    {
        Guid CorpseId { get; }

        Guid? SourceCharacterId { get; }

        string SourceDisplayName { get; }

        string ShardId { get; }

        double PositionX { get; }

        double PositionY { get; }

        double PositionZ { get; }

        double RotationX { get; }

        double RotationY { get; }

        double RotationZ { get; }

        double RotationW { get; }

        string PresentationKey { get; }

        long CorpseRevision { get; }

        DateTime CreatedAt { get; }

        DateTime ExpiresAt { get; }
    }

    private class OpenCorpseRow : ICorpseRow
    {
        public Guid CorpseId { get; set; }

        public Guid? SourceCharacterId { get; set; }

        public string SourceDisplayName { get; set; } = string.Empty;

        public string ShardId { get; set; } = string.Empty;

        public double PositionX { get; set; }

        public double PositionY { get; set; }

        public double PositionZ { get; set; }

        public double RotationX { get; set; }

        public double RotationY { get; set; }

        public double RotationZ { get; set; }

        public double RotationW { get; set; }

        public string PresentationKey { get; set; } = string.Empty;

        public long CorpseRevision { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime ExpiresAt { get; set; }
    }

    private sealed class DeathPartitionRow : OpenCorpseRow
    {
        public Guid DeathEventId { get; set; }

        public Guid OriginalOperationId { get; set; }

        public Guid CharacterId { get; set; }

        public long CharacterRevision { get; set; }

        public long CarriedWeight { get; set; }

        public long CarryCapacity { get; set; }
    }

    private sealed class CorpseSectionRow
    {
        public Guid CorpseId { get; set; }

        public string SectionKind { get; set; } = string.Empty;

        public Guid ContainerId { get; set; }

        public long ContainerRevision { get; set; }

        public int ItemCount { get; set; }
    }

    private sealed class CorpseWorkerAuthorityRow
    {
        public string? RuntimeId { get; set; }

        public bool HasFreshLease { get; set; }

        public bool HasAssignment { get; set; }
    }

    private sealed class CorpseExpiryCandidate
    {
        public Guid ExpiryOperationId { get; set; }

        public bool IsDue { get; set; }

        public bool IsClosed { get; set; }
    }
}
