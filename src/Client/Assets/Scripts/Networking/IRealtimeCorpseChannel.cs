using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The corpse messages of a realtime shard session. RealtimeSimulationClient carries
    // them, and EditMode tests drive the corpse controller through a fake.
    public interface IRealtimeCorpseChannel
    {
        event Action<RealtimeCorpsePresenceSnapshotChunk> CorpsePresenceChunkReceived;

        event Action<RealtimeCorpseInteractionResult> CorpseInteractionCompleted;

        event Action<RealtimeCorpseViewStateChunk> CorpseViewStateChunkReceived;

        event Action<RealtimeCorpseViewClosed> CorpseViewClosed;

        event Action<RealtimeClientError> SessionEnded;

        bool IsJoined { get; }

        bool TrySendCorpseInteraction(RealtimeCorpseInteractionIntent intent);

        void DisconnectForClientFailure(string code, string message);
    }
}
