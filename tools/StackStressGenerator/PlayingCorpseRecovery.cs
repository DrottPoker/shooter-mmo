using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;

namespace Mmorpg.Tools.StackStressGenerator;

// What a gatherer or trader does after a death, as a player does it: wait for the respawn, walk
// back to the death spot, find its own corpse in the presence snapshots, open it, loot
// everything back, and close it. It fails when the corpse is gone or out of reach; the role
// then goes on as a walker and the report counts the lost corpse.
public sealed class PlayingCorpseRecovery
{
    public static readonly TimeSpan RecoveryLimit = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan OwnCorpseSearch = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ViewWait = TimeSpan.FromSeconds(6);
    private const float CorpseReach = 2f;
    private const float DeathSpotReach = 4f;

    private readonly SimulationVector3 deathPosition;
    private readonly uint deathLifeSequence;
    private RecoveryState state = RecoveryState.AwaitRespawn;
    private TimeSpan respawnedAt;
    private TimeSpan? atDeathSpotSince;
    private TimeSpan openedAt;
    private Guid corpse;
    private bool awaiting;
    private bool looted;
    private bool closeSent;

    public PlayingCorpseRecovery(SimulationVector3 deathPosition, uint deathLifeSequence)
    {
        this.deathPosition = deathPosition;
        this.deathLifeSequence = deathLifeSequence;
    }

    private enum RecoveryState
    {
        AwaitRespawn,
        Travel,
        Opening,
        Looting,
        Closing
    }

    public string State => state.ToString().ToLowerInvariant();

    public static bool Owns(string operation)
    {
        return operation is PlayingOperations.LootOpen or PlayingOperations.LootAll or PlayingOperations.LootClose;
    }

    public void OnOutcome(IPlayingBotBody body, PlayingIntentOutcome outcome, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(outcome);
        awaiting = false;
        switch (outcome.Operation)
        {
            case PlayingOperations.LootOpen:
                state = outcome.Succeeded ? RecoveryState.Looting : RecoveryState.Travel;
                openedAt = now;
                break;
            case PlayingOperations.LootAll:
                if (outcome.Succeeded)
                {
                    body.Count(PlayingCounter.CorpsesRecovered);
                }

                looted = true;
                state = RecoveryState.Closing;
                break;
            case PlayingOperations.LootClose:
                state = RecoveryState.Closing;
                looted = true;
                break;
        }
    }

    public PlayingVisitStatus Think(IPlayingBotBody body, TimeSpan now, Func<SimulationVector3, float, bool?> travel)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(travel);
        if (awaiting)
        {
            return PlayingVisitStatus.Running;
        }

        switch (state)
        {
            case RecoveryState.AwaitRespawn:
                if (body.View.OwnCombat is { LifeState: RealtimeCombatLifeState.Alive } own
                    && own.LifeSequence != deathLifeSequence)
                {
                    respawnedAt = now;
                    state = RecoveryState.Travel;
                }

                return PlayingVisitStatus.Running;
            case RecoveryState.Travel:
                return Travel(body, now, travel);
            case RecoveryState.Opening:
                return PlayingVisitStatus.Running;
            case RecoveryState.Looting:
                return Loot(body, now);
            default:
                return Close(body);
        }
    }

    private PlayingVisitStatus Travel(IPlayingBotBody body, TimeSpan now, Func<SimulationVector3, float, bool?> travel)
    {
        if (now - respawnedAt > RecoveryLimit)
        {
            return PlayingVisitStatus.Failed;
        }

        if (FindOwnCorpse(body) is { } own)
        {
            corpse = own.CorpseId;
            var reached = travel(new SimulationVector3(own.PositionX, own.PositionY, own.PositionZ), CorpseReach);
            if (reached is null)
            {
                return PlayingVisitStatus.Failed;
            }

            if (reached == true)
            {
                body.Stand();
                if (body.OpenCorpse(corpse))
                {
                    awaiting = true;
                    state = RecoveryState.Opening;
                }
            }

            return PlayingVisitStatus.Running;
        }

        switch (travel(deathPosition, DeathSpotReach))
        {
            case null:
                return PlayingVisitStatus.Failed;
            case true:
                // At the death spot, the corpse must show in a presence snapshot within a few seconds.
                atDeathSpotSince ??= now;
                return now - atDeathSpotSince.Value > OwnCorpseSearch
                    ? PlayingVisitStatus.Failed
                    : PlayingVisitStatus.Running;
            default:
                return PlayingVisitStatus.Running;
        }
    }

    private PlayingVisitStatus Loot(IPlayingBotBody body, TimeSpan now)
    {
        if (body.View.OpenCorpseId != corpse)
        {
            // The worker closed the view, as it does once the corpse is empty.
            return looted ? PlayingVisitStatus.Done : PlayingVisitStatus.Failed;
        }

        if (!body.View.OpenCorpseReady)
        {
            if (now - openedAt > ViewWait)
            {
                state = RecoveryState.Closing;
            }

            return PlayingVisitStatus.Running;
        }

        if (!looted && body.View.Corpses.GetValueOrDefault(corpse) is { IsEmpty: false })
        {
            if (body.LootAll(corpse, body.View.OpenCorpseRevision))
            {
                awaiting = true;
            }

            return PlayingVisitStatus.Running;
        }

        state = RecoveryState.Closing;
        return Close(body);
    }

    private PlayingVisitStatus Close(IPlayingBotBody body)
    {
        if (closeSent || body.View.OpenCorpseId != corpse || corpse == Guid.Empty)
        {
            return looted ? PlayingVisitStatus.Done : PlayingVisitStatus.Failed;
        }

        if (body.CloseCorpse(corpse, Math.Max(0, body.View.OpenCorpseRevision)))
        {
            awaiting = true;
            closeSent = true;
        }

        return PlayingVisitStatus.Running;
    }

    private RealtimeCorpsePresence? FindOwnCorpse(IPlayingBotBody body)
    {
        RealtimeCorpsePresence? best = null;
        var bestDistance = float.MaxValue;
        foreach (var candidate in body.View.Corpses.Values)
        {
            if (candidate.Source.CharacterName != body.View.OwnCharacterName || candidate.IsEmpty)
            {
                continue;
            }

            var dx = candidate.PositionX - deathPosition.X;
            var dz = candidate.PositionZ - deathPosition.Z;
            var distance = (dx * dx) + (dz * dz);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }
}
