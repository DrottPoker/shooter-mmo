using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Corpses;

public interface ICorpseRuntimePresence
{
    Guid CorpseId { get; }
    string SourceDisplayName { get; }
    double PositionX { get; }
    double PositionY { get; }
    double PositionZ { get; }
    // The facing of whoever left the corpse around the vertical axis, so the remains lie as the
    // body fell.
    float YawDegrees { get; }
    string PresentationKey { get; }
    long Revision { get; }
    DateTime CreatedAt { get; }
    DateTime ExpiresAt { get; }
    bool IsEmpty { get; }
    // How a player corpse looks; null for a Mob corpse.
    CorpseLook? Look { get; }
}

// The dead character's body model, look, and headwear, and the items still in the corpse's
// equipment section, which it wears until someone loots them.
public sealed record CorpseLook(
    string BodyModel,
    IReadOnlyList<SimulationAppearanceChoice> Choices,
    IReadOnlyList<CharacterLookItem> Equipment,
    bool HeadwearLowered)
{
    public static CorpseLook? From(CorpseLookResponse? look) => look is null
        ? null
        : new CorpseLook(
            look.BodyModel,
            look.Choices,
            look.Equipment.Select(item => new CharacterLookItem(item.EquipmentSlotId, item.DefinitionId)).ToArray(),
            look.HeadwearLowered);
}

public sealed record DurableCorpseState(
    Guid CorpseId,
    Guid? SourceCharacterId,
    string SourceDisplayName,
    string ShardId,
    double PositionX,
    double PositionY,
    double PositionZ,
    double RotationX,
    double RotationY,
    double RotationZ,
    double RotationW,
    string PresentationKey,
    long Revision,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    bool IsEmpty,
    IReadOnlyList<CorpseSectionState> Sections) : ICorpseRuntimePresence
{
    public float YawDegrees => CorpseFacing.YawDegrees(RotationX, RotationY, RotationZ, RotationW);

    public CorpseLook? Look { get; init; }
}

public static class CorpseFacing
{
    // The turn around the vertical axis (Y) of a rotation, in degrees from -180 to 180. A player
    // corpse stores the facing of its character as a turn around Y only.
    public static float YawDegrees(double x, double y, double z, double w)
    {
        var radians = Math.Atan2(2d * ((w * y) + (x * z)), 1d - (2d * ((x * x) + (y * y))));
        return (float)(radians * 180d / Math.PI);
    }
}

public sealed record CorpseSectionState(
    string SectionKind,
    Guid ContainerId,
    long ContainerRevision,
    int ItemCount);

public sealed class DurableCorpseStore(TimeProvider timeProvider)
{
    private readonly object sync = new();
    private IReadOnlyDictionary<Guid, DurableCorpseState> corpses =
        new Dictionary<Guid, DurableCorpseState>();
    private DateTime databaseTimeAtSnapshot;
    private long timestampAtSnapshot;

    public void Replace(CorpseRestoreResponse response, string expectedShardId)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.DatabaseTime == default)
        {
            throw new ArgumentException("The corpse snapshot has no database time.", nameof(response));
        }

        var replacement = new Dictionary<Guid, DurableCorpseState>();
        foreach (var corpse in response.Corpses)
        {
            if (!string.Equals(corpse.ShardId, expectedShardId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The corpse snapshot contains another Shard.",
                    nameof(response));
            }

            if (corpse.ExpiresAt <= response.DatabaseTime)
            {
                continue;
            }

            var state = new DurableCorpseState(
                corpse.CorpseId,
                corpse.SourceCharacterId,
                corpse.SourceDisplayName,
                corpse.ShardId,
                corpse.PositionX,
                corpse.PositionY,
                corpse.PositionZ,
                corpse.RotationX,
                corpse.RotationY,
                corpse.RotationZ,
                corpse.RotationW,
                corpse.PresentationKey,
                corpse.Revision,
                corpse.CreatedAt,
                corpse.ExpiresAt,
                corpse.IsEmpty,
                corpse.Sections
                    .Select(section => new CorpseSectionState(
                        section.SectionKind,
                        section.ContainerId,
                        section.ContainerRevision,
                        section.ItemCount))
                    .ToArray())
            {
                Look = CorpseLook.From(corpse.Look)
            };
            if (!replacement.TryAdd(state.CorpseId, state))
            {
                throw new ArgumentException(
                    "The corpse snapshot contains a duplicate corpse id.",
                    nameof(response));
            }
        }

        lock (sync)
        {
            foreach (var current in corpses.Values)
            {
                if (replacement.TryGetValue(current.CorpseId, out var restored))
                {
                    if (current.Revision > restored.Revision)
                    {
                        replacement[current.CorpseId] = current;
                    }

                    continue;
                }

                if (current.CreatedAt > response.DatabaseTime)
                {
                    replacement.Add(current.CorpseId, current);
                }
            }

            corpses = replacement;
            databaseTimeAtSnapshot = response.DatabaseTime;
            timestampAtSnapshot = timeProvider.GetTimestamp();
        }
    }

    public IReadOnlyList<DurableCorpseState> ListActive()
    {
        lock (sync)
        {
            var estimatedDatabaseTime = EstimateDatabaseTimeLocked();
            return corpses.Values
                .Where(corpse => corpse.ExpiresAt > estimatedDatabaseTime)
                .OrderBy(corpse => corpse.CreatedAt)
                .ThenBy(corpse => corpse.CorpseId)
                .ToArray();
        }
    }

    public bool TryGetActive(Guid corpseId, out DurableCorpseState? corpse)
    {
        lock (sync)
        {
            if (corpses.TryGetValue(corpseId, out var candidate)
                && candidate.ExpiresAt > EstimateDatabaseTimeLocked())
            {
                corpse = candidate;
                return true;
            }

            corpse = null;
            return false;
        }
    }

    // A view snapshot has no facing and no look of its own: the corpse keeps the facing and the
    // body model and look it had, and wears what is left in its equipment section now.
    public void ApplySnapshot(CorpseViewSnapshotResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DurableCorpseState? current;
        lock (sync)
        {
            corpses.TryGetValue(snapshot.CorpseId, out current);
        }

        var equipment = snapshot.Sections
            .Where(section => string.Equals(section.SectionKind, "equipment", StringComparison.Ordinal))
            .SelectMany(section => section.Slots)
            .Where(slot => slot.Item is not null && !string.IsNullOrEmpty(slot.EquipmentSlotId))
            .Select(slot => new CharacterLookItem(slot.EquipmentSlotId, slot.Item!.DefinitionId))
            .ToArray();
        var state = new DurableCorpseState(
            snapshot.CorpseId,
            snapshot.SourceCharacterId,
            snapshot.SourceDisplayName,
            snapshot.ShardId,
            snapshot.PositionX,
            snapshot.PositionY,
            snapshot.PositionZ,
            current?.RotationX ?? 0d,
            current?.RotationY ?? 0d,
            current?.RotationZ ?? 0d,
            current?.RotationW ?? 1d,
            snapshot.PresentationKey,
            snapshot.Revision,
            snapshot.CreatedAt,
            snapshot.ExpiresAt,
            snapshot.Sections.All(section => section.Slots.All(slot => slot.Item is null)),
            snapshot.Sections
                .Select(section => new CorpseSectionState(
                    section.SectionKind,
                    section.ContainerId,
                    section.ContainerRevision,
                    section.Slots.Count(slot => slot.Item is not null)))
                .ToArray())
        {
            Look = current?.Look is { } look ? look with { Equipment = equipment } : null
        };
        ApplyState(state);
    }

    public void Apply(DurableCorpseResponse corpse)
    {
        ArgumentNullException.ThrowIfNull(corpse);
        ApplyState(new DurableCorpseState(
            corpse.CorpseId,
            corpse.SourceCharacterId,
            corpse.SourceDisplayName,
            corpse.ShardId,
            corpse.PositionX,
            corpse.PositionY,
            corpse.PositionZ,
            corpse.RotationX,
            corpse.RotationY,
            corpse.RotationZ,
            corpse.RotationW,
            corpse.PresentationKey,
            corpse.Revision,
            corpse.CreatedAt,
            corpse.ExpiresAt,
            corpse.IsEmpty,
            corpse.Sections
                .Select(section => new CorpseSectionState(
                    section.SectionKind,
                    section.ContainerId,
                    section.ContainerRevision,
                    section.ItemCount))
                .ToArray())
        {
            Look = CorpseLook.From(corpse.Look)
        });
    }

    private void ApplyState(DurableCorpseState state)
    {
        lock (sync)
        {
            if (corpses.TryGetValue(state.CorpseId, out var current)
                && current.Revision > state.Revision)
            {
                return;
            }

            if (state.ExpiresAt <= EstimateDatabaseTimeLocked())
            {
                corpses = corpses
                    .Where(pair => pair.Key != state.CorpseId)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                return;
            }

            var replacement = corpses.ToDictionary(pair => pair.Key, pair => pair.Value);
            replacement[state.CorpseId] = state;
            corpses = replacement;
        }
    }

    public bool Remove(Guid corpseId)
    {
        lock (sync)
        {
            if (!corpses.ContainsKey(corpseId))
            {
                return false;
            }

            corpses = corpses
                .Where(pair => pair.Key != corpseId)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            return true;
        }
    }

    private DateTime EstimateDatabaseTimeLocked()
    {
        if (databaseTimeAtSnapshot == default)
        {
            return timeProvider.GetUtcNow().UtcDateTime;
        }

        return databaseTimeAtSnapshot + timeProvider.GetElapsedTime(timestampAtSnapshot);
    }
}
