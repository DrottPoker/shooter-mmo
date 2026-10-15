namespace Mmorpg.SimulationWorker.Realtime;

internal readonly record struct SnapshotRecipient(
    RealtimePeerContext Context,
    IRealtimePeerConnection Peer,
    ulong ControlledEntityId);
