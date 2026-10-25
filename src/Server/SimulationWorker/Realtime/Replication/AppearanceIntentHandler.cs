using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

internal sealed record AppearanceChangeCompleted(
    RealtimePeerContext PeerContext,
    ActiveSimulationSession Session,
    RealtimeAppearanceIntent Intent,
    GameServiceResult<SimulationAppearance>? Result)
    : RealtimeOperationResult(PeerContext);

// Lowers or raises a player's headwear, or in Development gives it another body model and look:
// GameService stores the change, the result answers the intent, and the appearance replicator
// sends the new look to everyone who sees the player. One change per peer runs at a time.
// Loop-confined; the GameService call runs off the loop.
internal sealed class AppearanceIntentHandler(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    CharacterLookStore looks,
    RealtimeOperationTracker operationTracker,
    RealtimeCompletionQueue completions,
    GameServiceClient? gameServiceClient,
    ILogger logger) : IRealtimeCompletionHandler
{
    public const string HeadwearChange = "headwear";
    public const string DevelopmentLookChange = "development_look";

    private readonly HashSet<int> inFlight = [];

    public void HandleIntent(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        if (!peers.TryGetBoundPlayer(context, out var entity))
        {
            sender.RejectProtocol(peer, context, "appearance_unavailable", "Appearance changes are not active for this peer.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeAppearanceIntent(message.Packet, out var intent, out var error))
        {
            sender.RejectProtocol(peer, context, "invalid_appearance_intent", error);
            return;
        }

        var session = entity.Session;
        if (gameServiceClient is null || session.IsSyntheticBot)
        {
            SendResult(peer, intent, "appearance_unavailable", "This worker cannot change looks.");
            return;
        }

        if (!inFlight.Add(context.PeerId))
        {
            SendResult(peer, intent, "appearance_change_in_flight", "Another change of the look is waiting for GameService.");
            return;
        }

        operationTracker.Track(RunAsync(context, session, intent, ToRequest(session, intent)));
    }

    public void Complete(RealtimeOperationResult completion)
    {
        var completed = (AppearanceChangeCompleted)completion;
        inFlight.Remove(completed.Context.PeerId);
        var session = completed.Session;
        var stored = completed.Result is { Succeeded: true, Value: { } appearance }
            && looks.TryApply(session.CharacterId, session.SimulationSessionId, appearance);
        if (completed.Context.Session?.SimulationSessionId != session.SimulationSessionId
            || !peers.TryGetCurrentPeer(completed.Context, out var peer))
        {
            return;
        }

        if (stored)
        {
            sender.SendControl(peer, RealtimeProtocol.EncodeAppearanceIntentResult(new RealtimeAppearanceIntentResult(
                completed.Intent.OperationId,
                completed.Intent.Kind,
                true,
                null)));
            return;
        }

        var failure = completed.Result?.Error;
        SendResult(
            peer,
            completed.Intent,
            failure?.Code ?? "appearance_change_failed",
            failure?.Message ?? "SimulationWorker could not change the look.");
    }

    public void OnCompletionFailed(RealtimeOperationResult completion)
    {
        inFlight.Remove(completion.Context.PeerId);
    }

    public void ForgetPeer(int peerId)
    {
        inFlight.Remove(peerId);
    }

    private async Task RunAsync(
        RealtimePeerContext context,
        ActiveSimulationSession session,
        RealtimeAppearanceIntent intent,
        SimulationAppearanceChangeRequest request)
    {
        try
        {
            var result = await gameServiceClient!.ChangeAppearanceAsync(
                session.SimulationSessionId,
                request,
                CancellationToken.None);
            completions.Post(this, new AppearanceChangeCompleted(context, session, intent, result));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while changing the look of simulation session {SimulationSessionId}.",
                session.SimulationSessionId);
            completions.Post(this, new AppearanceChangeCompleted(context, session, intent, null));
        }
    }

    private static SimulationAppearanceChangeRequest ToRequest(ActiveSimulationSession session, RealtimeAppearanceIntent intent)
    {
        return intent.Kind == RealtimeAppearanceIntentKind.SetDevelopmentLook
            ? new SimulationAppearanceChangeRequest(
                session.WorkerRuntimeId,
                session.SimulationSessionToken,
                DevelopmentLookChange,
                null,
                intent.BodyModel,
                intent.Choices.Select(choice => new SimulationAppearanceChoice(choice.Kind, choice.Slot, choice.Option)).ToArray())
            : new SimulationAppearanceChangeRequest(
                session.WorkerRuntimeId,
                session.SimulationSessionToken,
                HeadwearChange,
                intent.Kind == RealtimeAppearanceIntentKind.LowerHeadwear,
                null,
                null);
    }

    private void SendResult(IRealtimePeerConnection peer, RealtimeAppearanceIntent intent, string code, string message)
    {
        sender.SendControl(peer, RealtimeProtocol.EncodeAppearanceIntentResult(new RealtimeAppearanceIntentResult(
            intent.OperationId,
            intent.Kind,
            false,
            new RealtimeError(code, message))));
    }
}
