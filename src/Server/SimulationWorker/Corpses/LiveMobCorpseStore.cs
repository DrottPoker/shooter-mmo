using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Corpses;

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
    float YawDegrees,
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

    // A Mob corpse has no character look.
    public CorpseLook? Look => null;
}

public sealed class LiveMobCorpseStore(TimeProvider timeProvider)
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, LiveMobCorpseState> corpses = [];
    private readonly Dictionary<Guid, string> creationFingerprints = [];

    public LiveMobCorpseState CreateOrGet(LiveMobCorpseState corpse)
    {
        Validate(corpse);
        lock (sync)
        {
            PurgeExpiredLocked();
            var fingerprint = CreateFingerprint(corpse);
            if (corpses.TryGetValue(corpse.CorpseId, out var existing))
            {
                if (!creationFingerprints.TryGetValue(corpse.CorpseId, out var existingFingerprint)
                    || !string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The live Mob corpse id was reused with different authoritative data.");
                }

                return existing;
            }

            corpses.Add(corpse.CorpseId, corpse);
            creationFingerprints.Add(corpse.CorpseId, fingerprint);
            return corpse;
        }
    }

    public IReadOnlyList<LiveMobCorpseState> ListActive()
    {
        lock (sync)
        {
            PurgeExpiredLocked();
            return corpses.Values
                .Where(corpse => !corpse.IsEmpty)
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
            if (corpses.TryGetValue(corpseId, out corpse) && !corpse.IsEmpty)
            {
                return true;
            }

            corpse = null;
            return false;
        }
    }

    // An emptied corpse stays until its deadline, so a replayed creation cannot restore
    // its claimed loot; it is no longer active meanwhile.
    public bool IsEmptied(Guid corpseId)
    {
        lock (sync)
        {
            PurgeExpiredLocked();
            return corpses.TryGetValue(corpseId, out var corpse) && corpse.IsEmpty;
        }
    }

    public bool TryGetForReplay(Guid corpseId, out LiveMobCorpseState? corpse)
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
                corpses[corpseId] = corpse with
                {
                    Revision = checked(corpse.Revision + 1),
                    Loot = []
                };
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

    // Takes every granted entry out of the corpse at once, as one revision. Returns null once
    // the corpse is empty.
    public CorpseViewSnapshotResponse? CompleteClaims(
        Guid corpseId,
        IReadOnlyCollection<Guid> grantIds)
    {
        lock (sync)
        {
            PurgeExpiredLocked();
            if (!corpses.TryGetValue(corpseId, out var corpse))
            {
                return null;
            }

            var remaining = corpse.Loot
                .Where(candidate => !grantIds.Contains(candidate.GrantId))
                .OrderBy(candidate => candidate.SlotIndex)
                .Select((candidate, index) => candidate with { SlotIndex = index })
                .ToArray();
            if (remaining.Length == corpse.Loot.Count)
            {
                return ToSnapshot(corpse);
            }

            var updated = corpse with
            {
                Revision = checked(corpse.Revision + 1),
                Loot = remaining
            };
            corpses[corpseId] = updated;
            return updated.IsEmpty ? null : ToSnapshot(updated);
        }
    }

    public CorpseViewSnapshotResponse? CreateSnapshot(Guid corpseId)
    {
        lock (sync)
        {
            PurgeExpiredLocked();
            return corpses.TryGetValue(corpseId, out var corpse)
                && !corpse.IsEmpty
                ? ToSnapshot(corpse)
                : null;
        }
    }

    public bool Remove(Guid corpseId)
    {
        lock (sync)
        {
            if (corpses.TryGetValue(corpseId, out var corpse) && corpse.IsEmpty)
            {
                return false;
            }

            creationFingerprints.Remove(corpseId);
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
                    generalSlots.Length,
                    generalSlots),
                EmptySection("equipment", corpse.EquipmentContainerId, "corpse_equipment"),
                EmptySection("bag", corpse.BagContainerId, "corpse_bag_contents")
            ],
            []);
    }

    // A live Mob corpse carries only general loot, so its other sections have no slots.
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
            0,
            []);
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
            creationFingerprints.Remove(corpseId);
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
            || !float.IsFinite(corpse.YawDegrees)
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

    private static string CreateFingerprint(LiveMobCorpseState corpse)
    {
        var components = new List<string>(15 + (corpse.Loot.Count * 5))
        {
            corpse.CorpseId.ToString("N"),
            corpse.SourceActorRuntimeId.ToString("N"),
            corpse.SourceActorDefinitionId,
            corpse.SourceDisplayName,
            corpse.ShardId,
            corpse.PositionX.ToString("R", CultureInfo.InvariantCulture),
            corpse.PositionY.ToString("R", CultureInfo.InvariantCulture),
            corpse.PositionZ.ToString("R", CultureInfo.InvariantCulture),
            corpse.YawDegrees.ToString("R", CultureInfo.InvariantCulture),
            corpse.PresentationKey,
            corpse.GeneralContainerId.ToString("N"),
            corpse.EquipmentContainerId.ToString("N"),
            corpse.BagContainerId.ToString("N"),
            corpse.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture),
            corpse.ExpiresAt.Ticks.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var loot in corpse.Loot.OrderBy(entry => entry.SlotIndex))
        {
            components.Add(loot.LootEntryId.ToString("N"));
            components.Add(loot.GrantId.ToString("N"));
            components.Add(loot.DefinitionId);
            components.Add(loot.Quantity.ToString(CultureInfo.InvariantCulture));
            components.Add(loot.SlotIndex.ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', components))));
    }
}
