using Mmorpg.Server.Common.Identity;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.SimulationWorker.Corpses;

public sealed record MobCorpseLootSeed(string DefinitionId, int Quantity);

// A quest drop that a dying Mob rolled for its corpse's owner, which GameService grants with the
// quest's lineage up to what the quest still needs.
public sealed record MobCorpseQuestDropSeed(string QuestId, string DefinitionId, int Quantity);

// One corpse of a Mob death. Every corpse of the death shares the loot group, so a viewer sees
// one body. The owner is the player the Mob credited, who alone may loot the corpse, or null
// when everyone may.
public sealed record CreateMobCorpseRequest(
    Guid DeathEventId,
    Guid CorpseId,
    Guid LootGroupId,
    Guid SourceActorRuntimeId,
    IReadOnlyList<MobCorpseLootSeed> Loot,
    Guid? OwnerCharacterId = null)
{
    // The quest drops rolled for the owner, which follow the Mob's own loot. A live corpse asks
    // GameService through the owner's session how much of them the owner still needs; a durable
    // corpse hands them to GameService, which keeps that much. The needed ones then fill the
    // entries that the Mob's own loot leaves.
    public IReadOnlyList<MobCorpseQuestDropSeed> QuestDrops { get; init; } = [];
}

public sealed record MobCorpseCreationResult(
    LiveMobCorpseState? LiveCorpse,
    DurableCorpseResponse? DurableCorpse,
    SimulationWorkerErrorResponse? Error)
{
    public bool Succeeded => Error is null;

    // The quest drops that the corpse holds for its owner, in corpse order, which the owner is
    // told about.
    public IReadOnlyList<MobCorpseQuestDropSeed> QuestDrops { get; init; } = [];

    // The Mob rolled only quest drops, and its owner needs none of them, so it leaves no corpse.
    public static MobCorpseCreationResult Empty { get; } = new(null, null, null);

    public static MobCorpseCreationResult Live(LiveMobCorpseState corpse)
    {
        return new MobCorpseCreationResult(corpse, null, null)
        {
            QuestDrops = corpse.Loot
                .Where(entry => entry.QuestId is not null)
                .OrderBy(entry => entry.SlotIndex)
                .Select(entry => new MobCorpseQuestDropSeed(entry.QuestId!, entry.DefinitionId, entry.Quantity))
                .ToArray()
        };
    }

    public static MobCorpseCreationResult Durable(
        DurableCorpseResponse corpse,
        IReadOnlyList<SimulationQuestDrop>? questDrops = null)
    {
        return new MobCorpseCreationResult(null, corpse, null)
        {
            QuestDrops = (questDrops ?? [])
                .Select(drop => new MobCorpseQuestDropSeed(drop.QuestId, drop.DefinitionId, drop.Quantity))
                .ToArray()
        };
    }

    public static MobCorpseCreationResult Rejected(SimulationWorkerErrorResponse error)
    {
        return new MobCorpseCreationResult(null, null, error);
    }
}

public sealed class MobCorpseLifecycleService(
    WorldActorStore actorStore,
    LiveMobCorpseStore liveCorpseStore,
    DurableCorpseStore durableCorpseStore,
    GameServiceClient gameServiceClient,
    ActiveSimulationSessionStore sessionStore,
    SimulationWorkerConfig config,
    SimulationWorkerIdentity identity,
    TimeProvider timeProvider,
    QuestCatalogRuntimeDocument questCatalog)
{
    // The remains of a Mob without its own corpse model. A Mob's corpse uses its own key, such as
    // corpse.mob.feral_wolf, and keeps the yaw it died at, so the client lays the Mob down as it
    // fell. Player corpses use corpse.remains, which the client lays down as a body.
    public const string DefaultPresentationKey = WorldActorCorpseRules.MobCorpsePresentationKey;

    private const string MobCorpseEmptyCode = "mob_corpse_empty";

    public async Task<MobCorpseCreationResult> CreateAsync(
        CreateMobCorpseRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        if (!actorStore.TryGetByRuntimeId(request.SourceActorRuntimeId, out var actor)
            || actor is null
            || !actor.IsActive
            || !string.Equals(actor.Definition.Kind, WorldActorKindIds.Mob, StringComparison.Ordinal))
        {
            return MobCorpseCreationResult.Rejected(new SimulationWorkerErrorResponse(
                "mob_corpse_source_invalid",
                "The Mob corpse source is not an active authoritative Mob actor."));
        }

        if (!string.Equals(actor.ShardId, config.ShardId, StringComparison.Ordinal)
            || !string.Equals(actor.WorkerRuntimeId, identity.RuntimeId, StringComparison.Ordinal))
        {
            return MobCorpseCreationResult.Rejected(new SimulationWorkerErrorResponse(
                "mob_corpse_authority_invalid",
                "The Mob corpse source is not owned by this worker runtime and Shard."));
        }

        var corpseId = request.CorpseId;
        var definition = actor.Definition;
        if (string.Equals(
                definition.CorpsePersistenceMode,
                WorldActorCorpsePersistenceModeIds.Live,
                StringComparison.Ordinal))
        {
            // A corpse is laid out once, so a replay finds it as it is and asks GameService
            // nothing.
            if (liveCorpseStore.TryGetForReplay(corpseId, out var existing))
            {
                return MobCorpseCreationResult.Live(existing!);
            }

            var neededQuestDrops = await ResolveQuestDropsAsync(request, definition.Id, cancellationToken);
            if (neededQuestDrops.Error is not null)
            {
                return MobCorpseCreationResult.Rejected(neededQuestDrops.Error);
            }

            var liveLoot = Resolve(
                corpseId,
                request.Loot,
                neededQuestDrops.Drops.Take(WorldActorCorpseRules.MaximumLootEntries - request.Loot.Count));
            if (liveLoot.Length == 0)
            {
                return MobCorpseCreationResult.Empty;
            }

            var createdAt = timeProvider.GetUtcNow().UtcDateTime;
            var liveCorpse = liveCorpseStore.CreateOrGet(new LiveMobCorpseState(
                corpseId,
                actor.RuntimeActorId,
                definition.Id,
                definition.DisplayName,
                actor.ShardId,
                actor.PositionX,
                actor.PositionY,
                actor.PositionZ,
                actor.YawDegrees,
                WorldActorCorpseRules.MobCorpsePresentationKeyFor(definition.PresentationArchetypeId),
                ScopedGuid.Create("mob-corpse-general", corpseId),
                ScopedGuid.Create("mob-corpse-equipment", corpseId),
                ScopedGuid.Create("mob-corpse-bag", corpseId),
                1,
                createdAt,
                createdAt.AddSeconds(definition.CorpseLifetimeSeconds),
                liveLoot.Select(value => new LiveMobCorpseLootEntry(
                    value.LootEntryId,
                    value.GrantId,
                    value.DefinitionId,
                    value.Quantity,
                    value.SlotIndex,
                    value.QuestId))
                    .ToArray())
            {
                OwnerCharacterId = request.OwnerCharacterId,
                LootGroupId = request.LootGroupId
            });
            return MobCorpseCreationResult.Live(liveCorpse);
        }

        if (!string.Equals(
                definition.CorpsePersistenceMode,
                WorldActorCorpsePersistenceModeIds.Durable,
                StringComparison.Ordinal))
        {
            return MobCorpseCreationResult.Rejected(new SimulationWorkerErrorResponse(
                "mob_corpse_persistence_invalid",
                "The Mob content does not define a supported corpse persistence mode."));
        }

        var loot = Resolve(
            corpseId,
            request.Loot,
            request.QuestDrops.Select(drop => new SimulationQuestDrop(drop.QuestId, drop.DefinitionId, drop.Quantity)));
        var halfYawRadians = actor.YawDegrees * Math.PI / 360d;
        var operationId = ScopedGuid.Create("mob-corpse-create", corpseId);
        var durableResult = await gameServiceClient.CreatePersistentMobCorpseAsync(
            new CreatePersistentMobCorpseRequest(
                operationId,
                corpseId,
                config.SimulationWorkerId,
                identity.RuntimeId,
                actor.ShardId,
                definition.Id,
                definition.DisplayName,
                actor.PositionX,
                actor.PositionY,
                actor.PositionZ,
                0d,
                Math.Sin(halfYawRadians),
                0d,
                Math.Cos(halfYawRadians),
                WorldActorCorpseRules.MobCorpsePresentationKeyFor(definition.PresentationArchetypeId),
                definition.CorpseLifetimeSeconds,
                loot.Select(value => new MobCorpseLootEntryRequest(
                    value.GrantId,
                    value.DefinitionId,
                    value.Quantity,
                    value.QuestId))
                    .ToArray())
            {
                OwnerCharacterId = request.OwnerCharacterId,
                LootGroupId = request.LootGroupId
            },
            cancellationToken);
        if (!durableResult.Succeeded)
        {
            return durableResult.Error!.Code == MobCorpseEmptyCode
                ? MobCorpseCreationResult.Empty
                : MobCorpseCreationResult.Rejected(durableResult.Error);
        }

        durableCorpseStore.Apply(durableResult.Value!.Corpse);
        return MobCorpseCreationResult.Durable(durableResult.Value.Corpse, durableResult.Value.QuestDrops);
    }

    // The quest drops that a live corpse's owner still needs, asked through the owner's current
    // session, so a reconnect's new token holds. A timeout or a server error fails the creation,
    // which the Mob retries, since the read changes nothing. An owner without a session on this
    // worker, or any other answer, such as a session that ended, leaves the quest drops out.
    private async Task<(IReadOnlyList<SimulationQuestDrop> Drops, SimulationWorkerErrorResponse? Error)>
        ResolveQuestDropsAsync(
            CreateMobCorpseRequest request,
            string sourceActorDefinitionId,
            CancellationToken cancellationToken)
    {
        if (request.QuestDrops.Count == 0
            || request.OwnerCharacterId is not { } ownerCharacterId
            || !sessionStore.TryGet(ownerCharacterId, out var owner)
            || owner is null)
        {
            return ([], null);
        }

        var result = await gameServiceClient.ResolveQuestDropsAsync(
            owner.SimulationSessionId,
            new SimulationQuestDropRequest(
                owner.AccountId,
                owner.CharacterId,
                owner.WorkerId,
                owner.WorkerRuntimeId,
                owner.ShardId,
                owner.SimulationSessionToken,
                questCatalog.Revision,
                sourceActorDefinitionId,
                request.QuestDrops
                    .Select(drop => new SimulationQuestDrop(drop.QuestId, drop.DefinitionId, drop.Quantity))
                    .ToArray()),
            cancellationToken);
        if (result.Succeeded)
        {
            return (result.Value!.Drops, null);
        }

        return GameServiceOutcome.IsTransient(result.Error!.Code, result.StatusCode)
            ? ([], result.Error)
            : ([], null);
    }

    // The Mob's own loot first and the quest drops after it, each with ids derived from the
    // corpse and its place in the corpse.
    private static ResolvedLoot[] Resolve(
        Guid corpseId,
        IEnumerable<MobCorpseLootSeed> loot,
        IEnumerable<SimulationQuestDrop> questDrops)
    {
        return loot
            .Select(seed => (DefinitionId: seed.DefinitionId.Trim(), seed.Quantity, QuestId: (string?)null))
            .Concat(questDrops.Select(drop => (drop.DefinitionId, drop.Quantity, QuestId: (string?)drop.QuestId)))
            .Select((value, index) => new ResolvedLoot(
                CreateIndexedScopedGuid("mob-loot-entry", corpseId, index),
                CreateIndexedScopedGuid("mob-loot-grant", corpseId, index),
                value.DefinitionId,
                value.Quantity,
                index,
                value.QuestId))
            .ToArray();
    }

    private static void ValidateRequest(CreateMobCorpseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeathEventId == Guid.Empty
            || request.CorpseId == Guid.Empty
            || request.LootGroupId == Guid.Empty
            || request.OwnerCharacterId == Guid.Empty
            || request.SourceActorRuntimeId == Guid.Empty
            || request.Loot is null
            || request.QuestDrops is null
            || request.Loot.Count > WorldActorCorpseRules.MaximumLootEntries
            || request.QuestDrops.Count > WorldActorCorpseRules.MaximumQuestDropCandidates
            || request.Loot.Count + request.QuestDrops.Count == 0
            || request.Loot.Any(seed =>
                !Identifiers.IsContentIdentifier(seed.DefinitionId)
                || seed.Quantity <= 0)
            || request.QuestDrops.Any(drop =>
                drop is null
                || !Identifiers.IsContentIdentifier(drop.QuestId)
                || !Identifiers.IsContentIdentifier(drop.DefinitionId)
                || drop.Quantity <= 0)
            || (request.QuestDrops.Count > 0 && request.OwnerCharacterId is null))
        {
            throw new ArgumentException(
                "The Mob corpse creation request is incomplete or invalid.",
                nameof(request));
        }
    }

    private static Guid CreateIndexedScopedGuid(string scope, Guid sourceId, int index)
    {
        return ScopedGuid.Create(scope + ":" + index, sourceId);
    }

    private sealed record ResolvedLoot(
        Guid LootEntryId,
        Guid GrantId,
        string DefinitionId,
        int Quantity,
        int SlotIndex,
        string? QuestId);
}
