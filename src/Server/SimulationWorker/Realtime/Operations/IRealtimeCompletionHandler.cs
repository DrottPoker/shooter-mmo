namespace Mmorpg.SimulationWorker.Realtime;

// Finalizes completed authority operations. Both members run on the realtime loop.
internal interface IRealtimeCompletionHandler
{
    void Complete(RealtimeOperationResult completion);

    // Runs after Complete threw and before the peer is disconnected with internal_error.
    void OnCompletionFailed(RealtimeOperationResult completion)
    {
    }
}
