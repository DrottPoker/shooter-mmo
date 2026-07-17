using SimulationWorker.Auth;

namespace SimulationWorker.Corpses;

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
    IReadOnlyList<CorpseSectionState> Sections);

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
                    .ToArray());
            if (!replacement.TryAdd(state.CorpseId, state))
            {
                throw new ArgumentException(
                    "The corpse snapshot contains a duplicate corpse id.",
                    nameof(response));
            }
        }

        lock (sync)
        {
            corpses = replacement;
            databaseTimeAtSnapshot = response.DatabaseTime;
            timestampAtSnapshot = timeProvider.GetTimestamp();
        }
    }

    public IReadOnlyList<DurableCorpseState> ListActive()
    {
        lock (sync)
        {
            if (databaseTimeAtSnapshot == default)
            {
                return [];
            }

            var elapsed = timeProvider.GetElapsedTime(timestampAtSnapshot);
            var estimatedDatabaseTime = databaseTimeAtSnapshot + elapsed;
            return corpses.Values
                .Where(corpse => corpse.ExpiresAt > estimatedDatabaseTime)
                .OrderBy(corpse => corpse.CreatedAt)
                .ThenBy(corpse => corpse.CorpseId)
                .ToArray();
        }
    }
}
