using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// One player's state in a checkpoint: its entity and session when the loop collected it, the
// sequence within the session, and the state.
internal readonly record struct CharacterCheckpointEntry(
    ulong EntityId,
    ActiveSimulationSession Session,
    long Sequence,
    CharacterStateSnapshot State);

// A checkpoint call ended; GameService answered the first AnsweredCount entries, saved or skipped.
internal sealed record CharacterCheckpointCompleted(
    IReadOnlyList<CharacterCheckpointEntry> Entries,
    int AnsweredCount)
    : RealtimeOperationResult(RealtimePeerContext.System);

// The checkpoint call, off the realtime loop. It only calls GameService, posts the completion,
// and logs, so it never touches loop state.
internal sealed class CharacterStateAuthorityOperations(
    GameServiceClient gameServiceClient,
    string workerId,
    RealtimeCompletionQueue completions,
    ILogger logger)
{
    // GameService takes at most this many states per request.
    public const int MaximumStatesPerRequest = 500;

    public async Task SendCheckpointAsync(
        IRealtimeCompletionHandler completionHandler,
        IReadOnlyList<CharacterCheckpointEntry> entries)
    {
        var answered = await SendAsync(entries);
        completions.Post(completionHandler, new CharacterCheckpointCompleted(entries, answered));
    }

    // Sends the entries in order, at most MaximumStatesPerRequest per request, and stops at the
    // first failure. Returns how many leading entries GameService answered.
    public async Task<int> SendAsync(IReadOnlyList<CharacterCheckpointEntry> entries)
    {
        var answered = 0;
        try
        {
            while (answered < entries.Count)
            {
                var count = Math.Min(MaximumStatesPerRequest, entries.Count - answered);
                var states = new SimulationCharacterCheckpointEntry[count];
                for (var index = 0; index < count; index++)
                {
                    var entry = entries[answered + index];
                    states[index] = new SimulationCharacterCheckpointEntry(
                        entry.Session.SimulationSessionId,
                        entry.Sequence,
                        entry.State.PositionX,
                        entry.State.PositionY,
                        entry.State.PositionZ,
                        entry.State.YawDegrees,
                        entry.State.Health,
                        entry.State.Mana);
                }

                var result = await gameServiceClient.CheckpointCharacterStatesAsync(
                    workerId,
                    new SimulationCharacterCheckpointRequest(entries[answered].Session.WorkerRuntimeId, states),
                    CancellationToken.None);
                if (!result.Succeeded)
                {
                    logger.LogWarning(
                        "[SIMULATION] Could not save {Count} character states: {Code}. They are sent again with the next checkpoint.",
                        entries.Count - answered,
                        result.Error!.Code);
                    break;
                }

                if (result.Value!.Skipped > 0)
                {
                    logger.LogDebug(
                        "[SIMULATION] GameService skipped {Skipped} of {Count} character states whose session ended or that were not newer.",
                        result.Value.Skipped,
                        count);
                }

                answered += count;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "[SIMULATION] Could not save {Count} character states. They are sent again with the next checkpoint.",
                entries.Count - answered);
        }

        return answered;
    }
}
