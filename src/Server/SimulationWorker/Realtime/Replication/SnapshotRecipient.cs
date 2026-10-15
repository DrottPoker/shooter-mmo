using LiteNetLib;

namespace Mmorpg.SimulationWorker.Realtime;

internal readonly record struct SnapshotRecipient(
    RealtimePeerContext Context,
    NetPeer Peer,
    ulong ControlledEntityId);
