using System.Data;
using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.Server.Common.Identity;
using Npgsql;

namespace Mmorpg.GameService.Items;

internal sealed partial class CorpseService(
    NpgsqlDataSource dataSource,
    ItemTransactionService transactionService,
    SimulationWorkerLeasePolicy workerLeasePolicy,
    ISimulationWorkerAuthority workerAuthority,
    ISimulationSessionAuthority sessionAuthority,
    SimulationCharacterStates characterStates,
    ICharacterAppearances characterAppearances,
    ILogger<CorpseService> logger)
{
    private NpgsqlDataSource DataSource => dataSource;

    private ItemTransactionService TransactionService => transactionService;

    private ISimulationSessionAuthority SessionAuthority => sessionAuthority;

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
                request.PresentationKey.Trim(),
                request.AmmunitionConsumption is { } consumption
                    ? new DeathAmmunitionConsumption(
                        consumption.ItemInstanceId,
                        consumption.ExpectedItemRevision,
                        consumption.Quantity)
                    : null,
                request.Killer is { } killer ? CanonicalKiller(killer) : null),
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

    public async Task<ServiceResult<PersistentMobCorpseResponse>>
        CreatePersistentMobCorpseAsync(
            string authenticatedWorkerId,
            CreatePersistentMobCorpseRequest request,
            CancellationToken cancellationToken)
    {
        if (!string.Equals(
                authenticatedWorkerId,
                request.WorkerId,
                StringComparison.Ordinal))
        {
            return ServiceResult<PersistentMobCorpseResponse>.Forbidden(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The authenticated simulation worker cannot create another worker's Mob corpse.");
        }

        if (!IsValidPersistentMobCorpseRequest(request))
        {
            return ServiceResult<PersistentMobCorpseResponse>.BadRequest(
                ItemApiErrorCodes.SimulationOperationInvalid,
                "The persistent Mob corpse request is incomplete or invalid.");
        }

        var result = await transactionService.ExecuteAsync(
            new ItemTransactionRequest<CreatePersistentMobCorpseCommand>(
                request.OperationId,
                ItemTransactionActor.ForSystem(),
                new CreatePersistentMobCorpseCommand(
                    request.CorpseId,
                    request.WorkerId.Trim(),
                    request.WorkerRuntimeId.Trim(),
                    request.ShardId.Trim(),
                    request.SourceActorDefinitionId.Trim(),
                    request.SourceDisplayName.Trim(),
                    request.PositionX,
                    request.PositionY,
                    request.PositionZ,
                    request.RotationX,
                    request.RotationY,
                    request.RotationZ,
                    request.RotationW,
                    request.PresentationKey.Trim(),
                    request.LifetimeSeconds,
                    checked((int)workerLeasePolicy.HeartbeatTimeout.TotalSeconds),
                    request.Loot)),
            cancellationToken);
        if (!result.Succeeded)
        {
            return MapTransactionResult<PersistentMobCorpseResponse>(result, null);
        }

        var corpse = await LoadCorpseByIdAsync(request.CorpseId, cancellationToken);
        if (corpse is null)
        {
            throw new InvalidOperationException(
                "A committed persistent Mob corpse has no durable corpse row.");
        }

        return ServiceResult<PersistentMobCorpseResponse>.Ok(
            new PersistentMobCorpseResponse(request.OperationId, corpse));
    }

    public async Task<ServiceResult<SimulationItemOperationResponse>> GrantLiveMobLootAsync(
        string authenticatedWorkerId,
        Guid simulationSessionId,
        SimulationMobLootGrantRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                authenticatedWorkerId,
                request.WorkerId,
                StringComparison.Ordinal))
        {
            return ServiceResult<SimulationItemOperationResponse>.Forbidden(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The authenticated simulation worker cannot submit another worker's Mob loot grant.");
        }

        if (!IsValidSimulationMobLootGrantRequest(simulationSessionId, request))
        {
            return ServiceResult<SimulationItemOperationResponse>.BadRequest(
                ItemApiErrorCodes.SimulationOperationInvalid,
                "The live Mob loot grant request is incomplete or invalid.");
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
        var result = await transactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantMobLootCommand>(
                request.GrantId,
                actor,
                new GrantMobLootCommand(
                    request.CharacterId,
                    request.ExpectedCharacterRevision,
                    request.GrantId,
                    request.SourceCorpseId,
                    request.SourceActorDefinitionId.Trim(),
                    request.DefinitionId.Trim(),
                    request.Quantity,
                    request.DestinationContainerId,
                    request.ExpectedDestinationContainerRevision,
                    request.DestinationSlotIndex,
                    string.IsNullOrWhiteSpace(request.EquipmentSlotId) ? null : request.EquipmentSlotId.Trim(),
                    request.TargetItemInstanceId,
                    request.ExpectedTargetItemRevision)),
            cancellationToken);
        return MapTransactionResult(
            result,
            result.Succeeded
                ? SimulationItemOperationResponse.From(
                    result,
                    await characterStates.ReadAsync(request.CharacterId, cancellationToken))
                : null);
    }

    public async Task<ServiceResult<SimulationMobLootAllResponse>> GrantLiveMobLootAllAsync(
        string authenticatedWorkerId,
        Guid simulationSessionId,
        SimulationMobLootAllRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                authenticatedWorkerId,
                request.WorkerId,
                StringComparison.Ordinal))
        {
            return ServiceResult<SimulationMobLootAllResponse>.Forbidden(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The authenticated simulation worker cannot submit another worker's Mob Loot all.");
        }

        if (!IsValidSimulationMobLootAllRequest(simulationSessionId, request))
        {
            return ServiceResult<SimulationMobLootAllResponse>.BadRequest(
                ItemApiErrorCodes.SimulationOperationInvalid,
                "The live Mob Loot all request is incomplete or invalid.");
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
        var result = await transactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantMobLootAllCommand>(
                request.OperationId,
                actor,
                new GrantMobLootAllCommand(
                    request.CharacterId,
                    request.ExpectedCharacterRevision,
                    request.SourceCorpseId,
                    request.SourceActorDefinitionId.Trim(),
                    request.Loot
                        .Select(loot => new MobLootGrantEntry(loot.GrantId, loot.DefinitionId.Trim(), loot.Quantity))
                        .ToArray())),
            cancellationToken);
        if (!result.Succeeded)
        {
            return MapTransactionResult<SimulationMobLootAllResponse>(result, null);
        }

        // The ledger answers for a replayed operation too, whose stored result names no grants.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var granted = (await connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select grant_id
                from live_mob_loot_grants
                where grant_id = any(@GrantIds)
                order by grant_id;
                """,
                new { GrantIds = request.Loot.Select(loot => loot.GrantId).ToArray() },
                cancellationToken: cancellationToken)))
            .ToArray();
        return MapTransactionResult(
            result,
            new SimulationMobLootAllResponse(
                SimulationItemOperationResponse.From(
                    result,
                    await characterStates.ReadAsync(request.CharacterId, cancellationToken)),
                granted));
    }

    public async Task<ServiceResult<CorpseRestoreResponse>> ListForWorkerAsync(
        string workerId,
        string workerRuntimeId,
        string shardId,
        CancellationToken cancellationToken)
    {
        if (!Identifiers.IsServiceIdentifier(workerId)
            || !Identifiers.IsServiceIdentifier(workerRuntimeId)
            || !Identifiers.IsServiceIdentifier(shardId))
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
            var authority = await workerAuthority.GetShardLeaseAsync(
                new PostgresTransactionScope(connection, transaction),
                workerId,
                shardId,
                cancellationToken);
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
                partition.RecoveryDeliveryIds,
                await characterStates.ReadAsync(command.CharacterId, cancellationToken)));
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
            var corpse = (await WithLooksAsync(
                connection,
                transaction,
                [CreateCorpseResponse(row, sections[row.CorpseId])],
                cancellationToken))[0];
            await transaction.CommitAsync(cancellationToken);
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

    private async Task<IReadOnlyList<DurableCorpseResponse>> LoadOpenCorpsesAsync(
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
        return await WithLooksAsync(
            connection,
            transaction,
            rows.Select(row => CreateCorpseResponse(row, sections[row.CorpseId])).ToArray(),
            cancellationToken);
    }

    private async Task<DurableCorpseResponse?> LoadCorpseByIdAsync(
        Guid corpseId,
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
            var row = await connection.QuerySingleOrDefaultAsync<OpenCorpseRow>(
                new CommandDefinition(
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
                    where corpse.id = @CorpseId;
                    """,
                    new { CorpseId = corpseId },
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
                [corpseId],
                cancellationToken);
            var corpse = (await WithLooksAsync(
                connection,
                transaction,
                [CreateCorpseResponse(row, sections[corpseId])],
                cancellationToken))[0];
            await transaction.CommitAsync(cancellationToken);
            return corpse;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
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

    // The look of every player corpse: its source character's body model and look, through the
    // Characters contract, and the definitions in the slots of its equipment section.
    private async Task<IReadOnlyList<DurableCorpseResponse>> WithLooksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<DurableCorpseResponse> corpses,
        CancellationToken cancellationToken)
    {
        var players = corpses.Where(corpse => corpse.SourceCharacterId is not null).ToArray();
        if (players.Length == 0)
        {
            return corpses;
        }

        var appearances = await characterAppearances.ReadManyAsync(
            new PostgresTransactionScope(connection, transaction),
            players.Select(corpse => corpse.SourceCharacterId!.Value).ToArray(),
            cancellationToken);
        var equipment = (await connection.QueryAsync<CorpseEquipmentRow>(new CommandDefinition(
            """
            select
                section.corpse_id as "CorpseId",
                slot.equipment_slot_id as "EquipmentSlotId",
                item.definition_id as "DefinitionId"
            from corpse_sections section
            join item_container_slots slot on slot.container_id = section.container_id
            join item_instances item
              on item.container_id = slot.container_id
             and item.container_slot_index = slot.slot_index
            where section.corpse_id = any(@CorpseIds)
              and section.section_kind = 'equipment'
              and slot.equipment_slot_id is not null
            order by section.corpse_id, slot.slot_index;
            """,
            new { CorpseIds = players.Select(corpse => corpse.CorpseId).ToArray() },
            transaction,
            cancellationToken: cancellationToken))).ToLookup(row => row.CorpseId);
        return corpses
            .Select(corpse => corpse.SourceCharacterId is { } characterId
                && appearances.TryGetValue(characterId, out var appearance)
                ? corpse with
                {
                    Look = new CorpseLookResponse(
                        appearance.BodyModel,
                        appearance.Choices.Select(choice => new CorpseLookChoiceResponse(choice.Kind, choice.Slot, choice.Option)).ToArray(),
                        equipment[corpse.CorpseId]
                            .Select(row => new CorpseLookEquipmentResponse(row.EquipmentSlotId, row.DefinitionId))
                            .ToArray(),
                        appearance.HeadwearLowered)
                }
                : corpse)
            .ToArray();
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
            ItemTransactionErrorCodes.ItemTransactionTimeout or
            ItemTransactionErrorCodes.DeathEventConflict or
            ItemTransactionErrorCodes.CorpseNotExpired or
            ItemTransactionErrorCodes.CorpseStateChanged or
            ItemTransactionErrorCodes.CorpseExpired or
            ItemTransactionErrorCodes.CorpseInvalidated or
            ItemTransactionErrorCodes.ItemAlreadyLooted or
            ItemTransactionErrorCodes.ItemQuantityChanged or
            ItemTransactionErrorCodes.BagStateChanged or
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
            && Identifiers.IsServiceIdentifier(request.WorkerId)
            && Identifiers.IsServiceIdentifier(request.WorkerRuntimeId)
            && Identifiers.IsServiceIdentifier(request.ShardId)
            && !string.IsNullOrWhiteSpace(request.SessionToken)
            && request.SessionToken.Length <= 1024
            && string.Equals(
                request.PresentationKey,
                ItemTransactionService.DefaultPlayerCorpsePresentationKey,
                StringComparison.Ordinal)
            && (request.AmmunitionConsumption is null
                || (request.AmmunitionConsumption.ItemInstanceId != Guid.Empty
                    && request.AmmunitionConsumption.ExpectedItemRevision >= 0
                    && request.AmmunitionConsumption.Quantity is >= 1
                        and <= Mmorpg.WorldData.Items.ItemCatalogFormat.MaximumQuantity))
            && (request.Killer is null || IsValidKiller(request.Killer));
    }

    // A character killer is stored as the canonical Guid text, so audit queries match it.
    private static DeathKiller CanonicalKiller(SimulationDeathKillerRequest killer)
    {
        var kind = killer.Kind.Trim();
        return new DeathKiller(
            kind,
            string.Equals(kind, DeathKillerKinds.Character, StringComparison.Ordinal)
                ? Guid.Parse(killer.Id).ToString("D")
                : killer.Id.Trim());
    }

    private static bool IsValidKiller(SimulationDeathKillerRequest killer)
    {
        return killer.Kind?.Trim() switch
        {
            DeathKillerKinds.Mob => Identifiers.IsContentIdentifier(killer.Id?.Trim()),
            DeathKillerKinds.Character => Guid.TryParse(killer.Id, out var id) && id != Guid.Empty,
            _ => false
        };
    }

    private static bool IsValidPersistentMobCorpseRequest(
        CreatePersistentMobCorpseRequest request)
    {
        return request.OperationId != Guid.Empty
            && request.CorpseId != Guid.Empty
            && Identifiers.IsServiceIdentifier(request.WorkerId)
            && Identifiers.IsServiceIdentifier(request.WorkerRuntimeId)
            && Identifiers.IsServiceIdentifier(request.ShardId)
            && Identifiers.IsContentIdentifier(request.SourceActorDefinitionId)
            && !string.IsNullOrWhiteSpace(request.SourceDisplayName)
            && request.SourceDisplayName.Length <= 128
            && CorpseTransformRules.IsMobCorpsePresentationKey(request.PresentationKey)
            && request.LifetimeSeconds
                is >= Mmorpg.WorldData.Actors.WorldActorCorpseRules.MinimumLifetimeSeconds
                and <= Mmorpg.WorldData.Actors.WorldActorCorpseRules.MaximumLifetimeSeconds
            && request.Loot is not null
            && request.Loot.Count
                is >= 1
                and <= Mmorpg.WorldData.Actors.WorldActorCorpseRules.MaximumLootEntries
            && request.Loot.All(loot =>
                loot.GrantId != Guid.Empty
                && Identifiers.IsContentIdentifier(loot.DefinitionId)
                && loot.Quantity > 0)
            && request.Loot.Select(loot => loot.GrantId).Distinct().Count()
                == request.Loot.Count;
    }

    private static bool IsValidSimulationMobLootAllRequest(
        Guid simulationSessionId,
        SimulationMobLootAllRequest request)
    {
        return simulationSessionId != Guid.Empty
            && request.OperationId != Guid.Empty
            && request.AccountId != Guid.Empty
            && request.CharacterId != Guid.Empty
            && request.ExpectedCharacterRevision >= 0
            && Identifiers.IsServiceIdentifier(request.WorkerId)
            && Identifiers.IsServiceIdentifier(request.WorkerRuntimeId)
            && Identifiers.IsServiceIdentifier(request.ShardId)
            && !string.IsNullOrWhiteSpace(request.SessionToken)
            && request.SessionToken.Length <= 1024
            && request.SourceCorpseId != Guid.Empty
            && Identifiers.IsContentIdentifier(request.SourceActorDefinitionId)
            && request.Loot is not null
            && request.Loot.Count
                is >= 1
                and <= Mmorpg.WorldData.Actors.WorldActorCorpseRules.MaximumLootEntries
            && request.Loot.All(loot =>
                loot.GrantId != Guid.Empty
                && Identifiers.IsContentIdentifier(loot.DefinitionId)
                && loot.Quantity > 0)
            && request.Loot.Select(loot => loot.GrantId).Distinct().Count()
                == request.Loot.Count;
    }

    private static bool IsValidSimulationMobLootGrantRequest(
        Guid simulationSessionId,
        SimulationMobLootGrantRequest request)
    {
        return simulationSessionId != Guid.Empty
            && request.GrantId != Guid.Empty
            && request.AccountId != Guid.Empty
            && request.CharacterId != Guid.Empty
            && request.ExpectedCharacterRevision >= 0
            && Identifiers.IsServiceIdentifier(request.WorkerId)
            && Identifiers.IsServiceIdentifier(request.WorkerRuntimeId)
            && Identifiers.IsServiceIdentifier(request.ShardId)
            && !string.IsNullOrWhiteSpace(request.SessionToken)
            && request.SessionToken.Length <= 1024
            && request.SourceCorpseId != Guid.Empty
            && Identifiers.IsContentIdentifier(request.SourceActorDefinitionId)
            && Identifiers.IsContentIdentifier(request.DefinitionId)
            && request.Quantity > 0
            && (request.TargetItemInstanceId is null) == (request.ExpectedTargetItemRevision is null)
            && request.TargetItemInstanceId != Guid.Empty
            && (request.ExpectedTargetItemRevision is null or >= 0)
            && (string.IsNullOrWhiteSpace(request.EquipmentSlotId)
                ? request.DestinationContainerId != Guid.Empty
                    && request.ExpectedDestinationContainerRevision >= 0
                    && request.DestinationSlotIndex >= 0
                : MobLootRules.NamesEquipmentDestinationOnly(
                    request.DestinationContainerId,
                    request.ExpectedDestinationContainerRevision,
                    request.DestinationSlotIndex));
    }


    private sealed class CorpseEquipmentRow
    {
        public Guid CorpseId { get; init; }

        public string EquipmentSlotId { get; init; } = string.Empty;

        public string DefinitionId { get; init; } = string.Empty;
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

    private sealed class CorpseExpiryCandidate
    {
        public Guid ExpiryOperationId { get; set; }

        public bool IsDue { get; set; }

        public bool IsClosed { get; set; }
    }
}
