using System.Collections.Generic;

namespace Mmorpg.Client.Gameplay
{
    // One remote entity in view in this frame, and whether its presentation ran out of
    // snapshots while it moved, so it stands where the next snapshot should have moved it on.
    public readonly struct RemoteMotionSample
    {
        public RemoteMotionSample(ulong entityId, bool isStarved)
        {
            EntityId = entityId;
            IsStarved = isStarved;
        }

        public ulong EntityId { get; }

        public bool IsStarved { get; }
    }

    // A presentation of remote entities that the client measurements read once a frame while
    // they record: the other players of the World scene and its Mobs.
    public interface IRemoteMotionSource
    {
        void CollectRemoteMotion(List<RemoteMotionSample> samples);
    }
}
