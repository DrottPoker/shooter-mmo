using ShooterMmo.WorldData.Actors;
using SimulationWorker.Auth;

namespace SimulationWorker.Corpses;

public sealed record LiveMobCorpseLootEntry(
    Guid LootEntryId,
    Guid GrantId,
    string DefinitionId,
    int Quantity,
    int SlotIndex);

public sealed record LiveMobCorpseState(
    Guid CorpseId,
    Guid SourceActorRuntimeId,
    string SourceActorDefinitionId,
    string SourceDisplayName,
    string ShardId,
    double PositionX,
    double PositionY,
    double PositionZ,
    string PresentationKey,
    Guid GeneralContainerId,
    Guid EquipmentContainerId,
    Guid BagContainerId,
    long Revision,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    IReadOnlyList<LiveMobCorpseLootEntry> Loot) : ICorpseRuntimePresence
{
    public bool IsEmpty => Loot.Count == 0;
}

public sealed class LiveMobCorpseStore(TimeProvider timeProvider)
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, LiveMobCorpseState> corpses = [];

    public LiveMobCorpseState CreateOrGet(LiveMobCorpseState corpse)
    {
        Validate(corpse);
        lock (sync)
        {
            PurgeExpiredLocked();
            if (corpses.TryGetValue(corpse.CorpseId, out var existing))
            {
                if (!Matches(existing, corpse))
                {
                    throw new InvalidOperationException(
                        "The live Mob corpse id was reused with different authoritative data.");
                }

                return existing;
            }

            corpses.Add(corpse.CorpseId, corpse);
            return corpse;
        }
    }

    public IReadOnlyList<LiveMobCorpseState> ListActive()
    {
        lock (sync)
        {
            PurgeExpiredLocked();
            return corpses.Values
                .OrderBy(corpse => corpse.CreatedAt)
                .ThenBy(corpse => corpse.CorpseId)
                .ToArray();
        }
    }

    public bool TryGetActive(Guid corpseId, out LiveMobCorpseState? corpse)
    {
        lock (sync)
        {
            PurgeExpiredLocked();
            return corpses.TryGetValue(corpseId, out corpse);
        }
    }

    public bool TryGetLootEntry(
        Guid corpseId,
        Guid lootEntryId,
        out LiveMobCorpseState? corpse,
        out LiveMobCorpseLootEntry? lootEntry)
    {
        lock (sync)
        {
            PurgeExpiredLocked();
            if (!corpses.TryGetValue(corpseId, out corpse))
            {
                lootEntry = null;
                return false;
            }

            lootEntry = corpse.Loot.SingleOrDefault(entry =>
                entry.LootEntryId == lootEntryId);
            return lootEntry is not null;
        }
    }

    public CorpseViewSnapshotResponse? CompleteClaim(
        Guid corpseId,
        Guid lootEntryId,
        Guid grantId)
    {
        lock (sync)
        {
            PurgeExpiredLocked();
            if (!corpses.TryGetValue(corpseId, out var corpse))
            {
                return null;
            }

            var entry = corpse.Loot.SingleOrDefault(candidate =>
                candidate.LootEntryId == lootEntryId);
            if (entry is null)
            {
                return ToSnapshot(corpse);
            }

            if (entry.GrantId != grantId)
            {
                throw new InvalidOperationException(
                    "The committed Mob loot grant does not match the live corpse entry.");
            }

            var remaining = corpse.Loot
                .Where(candidate => candidate.LootEntryId != lootEntryId)
                .OrderBy(candidate => candidate.SlotIndex)
                .Select((candidate, index) => candidate with { SlotIndex = index })
                .ToArray();
            if (remaining.Length == 0)
            {
                corpses.Remove(corpseId);
                return null;
            }

            var updated = corpse with
            {
                Revision = checked(corpse.Revision + 1),
                Loot = remaining
            };
            corpses[corpseId] = updated;
            return ToSnapshot(updated);
        }
    }

    public CorpseViewSnapshotResponse? CreateSnapshot(Guid corpseId)
    {
        lock (sync)
        {
            PurgeExpiredLocked();
            return corpses.TryGetValue(corpseId, out var corpse)
                ? ToSnapshot(corpse)
                : null;
        }
    }

    public bool Remove(Guid corpseId)
    {
        lock (sync)
        {
            return corpses.Remove(corpseId);
        }
    }

    private static CorpseViewSnapshotResponse ToSnapshot(LiveMobCorpseState corpse)
    {
        var generalSlots = corpse.Loot
            .OrderBy(entry => entry.SlotIndex)
            .Select(entry => new CorpseViewSlotResponse(
                entry.SlotIndex,
                "general",
                [],
                new CorpseViewItemResponse(
                    entry.LootEntryId,
                    entry.DefinitionId,
                    entry.Quantity,
                    0,
                    null,
                    null)))
            .ToArray();
        return new CorpseViewSnapshotResponse(
            corpse.CorpseId,
            null,
            corpse.SourceDisplayName,
            corpse.ShardId,
            corpse.PositionX,
            corpse.PositionY,
            corpse.PositionZ,
            corpse.PresentationKey,
            corpse.Revision,
            corpse.CreatedAt,
            corpse.ExpiresAt,
            [
                new CorpseViewSectionResponse(
                    "general_inventory",
                    corpse.GeneralContainerId,
                    "corpse_inventory",
                    corpse.Revision,
                    Math.Max(1, generalSlots.Length),
                    generalSlots),
                EmptySection("equipment", corpse.EquipmentContainerId, "corpse_equipment"),
                EmptySection("bag", corpse.BagContainerId, "corpse_bag_contents")
            ],
            []);
    }

    private static CorpseViewSectionResponse EmptySection(
        string sectionKind,
        Guid containerId,
        string containerType)
    {
        return new CorpseViewSectionResponse(
            sectionKind,
            containerId,
            containerType,
            0,
            1,
            [new CorpseViewSlotResponse(
                0,
                sectionKind == "equipment" ? "equipment" : "general",
                [],
                null,
                sectionKind == "equipment" ? "head" : string.Empty)]);
    }

    private void PurgeExpiredLocked()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var corpseId in corpses
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            corpses.Remove(corpseId);
        }
    }

    private static void Validate(LiveMobCorpseState corpse)
    {
        ArgumentNullException.ThrowIfNull(corpse);
        if (corpse.CorpseId == Guid.Empty
            || corpse.SourceActorRuntimeId == Guid.Empty
            || string.IsNullOrWhiteSpace(corpse.SourceActorDefinitionId)
            || string.IsNullOrWhiteSpace(corpse.SourceDisplayName)
            || string.IsNullOrWhiteSpace(corpse.ShardId)
            || string.IsNullOrWhiteSpace(corpse.PresentationKey)
            || corpse.GeneralContainerId == Guid.Empty
            || corpse.EquipmentContainerId == Guid.Empty
            || corpse.BagContainerId == Guid.Empty
            || corpse.Revision < 0
            || corpse.CreatedAt == default
            || corpse.ExpiresAt <= corpse.CreatedAt
            || corpse.Loot is null
            || corpse.Loot.Count is < 1 or > WorldActorCorpseRules.MaximumLootEntries
            || corpse.Loot.Any(entry =>
                entry.LootEntryId == Guid.Empty
                || entry.GrantId == Guid.Empty
                || string.IsNullOrWhiteSpace(entry.DefinitionId)
                || entry.Quantity <= 0
                || entry.SlotIndex < 0)
            || corpse.Loot.Select(entry => entry.LootEntryId).Distinct().Count()
                != corpse.Loot.Count
            || corpse.Loot.Select(entry => entry.GrantId).Distinct().Count()
                != corpse.Loot.Count
            || corpse.Loot.Select(entry => entry.SlotIndex).Distinct().Count()
                != corpse.Loot.Count)
        {
            throw new ArgumentException(
                "The live Mob corpse state is incomplete or invalid.",
                nameof(corpse));
        }
    }

    private static bool Matches(
        LiveMobCorpseState first,
        LiveMobCorpseState second)
    {
        return first == second
            || first.CorpseId == second.CorpseId
                && first.SourceActorRuntimeId == second.SourceActorRuntimeId
                && string.Equals(
                    first.SourceActorDefinitionId,
                    second.SourceActorDefinitionId,
                    StringComparison.Ordinal)
                && string.Equals(
                    first.SourceDisplayName,
                    second.SourceDisplayName,
                    StringComparison.Ordinal)
                && first.CreatedAt == second.CreatedAt
                && first.ExpiresAt == second.ExpiresAt
                && string.Equals(first.ShardId, second.ShardId, StringComparison.Ordinal)
                && first.PositionX == second.PositionX
                && first.PositionY == second.PositionY
                && first.PositionZ == second.PositionZ
                && string.Equals(
                    first.PresentationKey,
                    second.PresentationKey,
                    StringComparison.Ordinal)
                && first.GeneralContainerId == second.GeneralContainerId
                && first.EquipmentContainerId == second.EquipmentContainerId
                && first.BagContainerId == second.BagContainerId
                && first.Revision == second.Revision
                && first.Loot.SequenceEqual(second.Loot);
    }
}
