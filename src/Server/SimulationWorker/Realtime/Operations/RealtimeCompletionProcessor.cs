using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Mmorpg.SimulationWorker.Realtime;

// Drains completed authority operations on the realtime loop within a time budget. At
// least one queued completion is processed per call; the rest wait for the next pass.
internal sealed class RealtimeCompletionProcessor(
    RealtimeCompletionQueue completions,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    ILogger logger)
{
    public void ProcessCompleted(TimeSpan budget)
    {
        var started = Stopwatch.GetTimestamp();
        while (completions.TryDequeue(out var completion))
        {
            try
            {
                completion.Handler.Complete(completion.Operation);
            }
            catch (Exception exception)
            {
                IsolateFailedOperation(completion, exception);
            }

            if (Stopwatch.GetElapsedTime(started) >= budget)
            {
                break;
            }
        }
    }

    // One failing operation must not stop the loop and drop every player, so only the
    // affected peer is disconnected. Its normal disconnect path releases the session.
    private void IsolateFailedOperation(RealtimeCompletion completion, Exception exception)
    {
        var operation = completion.Operation;
        logger.LogError(
            exception,
            "[SIMULATION] Could not process a completed {OperationType} for UDP peer {PeerId}.",
            operation.GetType().Name,
            operation.Context.PeerId);
        completion.Handler.OnCompletionFailed(operation);
        if (peers.TryGetCurrentPeer(operation.Context, out var peer))
        {
            sender.RejectProtocol(
                peer,
                operation.Context,
                "internal_error",
                "SimulationWorker could not process the request.");
        }
    }
}
