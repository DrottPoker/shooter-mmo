using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Corpses;

// Decides which corpse view chunks reach the requesting peer and the other viewers after
// an authoritative corpse result, so a peer that opens a corpse always starts from a full
// snapshot instead of a delta against state it never received.
public sealed record CorpseStateDistribution(
    IReadOnlyList<RealtimeCorpseViewStateChunk> RequesterChunks,
    IReadOnlyList<RealtimeCorpseViewStateChunk> ViewerChunks,
    bool RequesterReceivesViewerChunks,
    bool CachesResult)
{
    public static CorpseStateDistribution Plan(
        CorpseViewSnapshotResponse? cached,
        CorpseViewSnapshotResponse result,
        bool hasMutation)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (cached is not null && cached.Revision >= result.Revision)
        {
            // An equal or older result changes nothing the current viewers hold.
            return new CorpseStateDistribution(
                hasMutation ? [] : CorpseRealtimePacketBuilder.BuildSnapshot(cached),
                [],
                false,
                false);
        }

        var viewerChunks = cached is null
            ? CorpseRealtimePacketBuilder.BuildSnapshot(result)
            : CorpseRealtimePacketBuilder.BuildDelta(cached, result);
        if (hasMutation)
        {
            // A mutation comes from a peer that already views the corpse.
            return new CorpseStateDistribution([], viewerChunks, true, true);
        }

        // An open or refresh can come from a peer without any corpse state, so it always
        // receives a complete snapshot while the existing viewers receive the delta.
        return new CorpseStateDistribution(
            CorpseRealtimePacketBuilder.BuildSnapshot(result),
            cached is null ? [] : viewerChunks,
            false,
            true);
    }
}
