using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// The level and experience of every character in the world, as GameService last reported them,
// and who hears of them. The owner gets its progression after every join and after every committed
// change; the viewers that see the character in detail hear when it reached a higher level, with
// no catch-up for the others. GameService computes every value; the worker keeps the latest only to
// send it and to skip the kill credits that cannot give the character experience. A character's
// progression lasts as long as its entity, also as a lingering body. Loop-confined.
internal sealed class ProgressionReplicator(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    EntityPresenceReplicator presence,
    SimulationEntityRegistry entityRegistry,
    ConnectionEntityBindingRegistry connectionBindings)
{
    private readonly Dictionary<Guid, StoredProgression> byCharacter = [];
    private readonly List<int> recipients = [];

    public int Count => byCharacter.Count;

    // A joined player, also one that reconnected, learns the progression that its join ticket read
    // from GameService. A development bot has none.
    public void OnPlayerJoined(RealtimePeerContext context, PlayerSimulationEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var session = entity.Session;
        if (session.IsSyntheticBot || session.Progression is not { } progression)
        {
            return;
        }

        byCharacter[session.CharacterId] = new StoredProgression(session.SimulationSessionId, progression);
        if (peers.TryGetCurrentPeer(context, out var peer))
        {
            sender.SendControl(peer, EncodeOwner(progression, 0, 0, RealtimeProgressionChangeReason.Snapshot));
        }
    }

    // The entity went, so its progression goes with it; a newer session of the character keeps
    // its own.
    public void OnPlayerRemoved(RealtimePeerContext context, ActiveSimulationSession session, ulong entityId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (byCharacter.TryGetValue(session.CharacterId, out var stored)
            && stored.SimulationSessionId == session.SimulationSessionId)
        {
            byCharacter.Remove(session.CharacterId);
        }
    }

    // A committed change of the session's character, with the peer that plays it now or none. It
    // is kept while the character's entity is in the world. The owner hears of it when it gained
    // experience, changed the level, or differs from what was kept, so a stale view heals; a higher
    // level reaches the viewers that see the character in detail.
    public void Apply(
        ActiveSimulationSession session,
        IRealtimePeerConnection? owner,
        SimulationProgressionChange change,
        RealtimeProgressionChangeReason reason)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(change);
        if (!entityRegistry.TryGetPlayerByCharacterId(session.CharacterId, out var entity)
            || entity!.Session.SimulationSessionId != session.SimulationSessionId)
        {
            return;
        }

        var progression = new SimulationProgression(
            change.Level,
            change.Experience,
            change.ExperienceToNextLevel,
            change.HighestMobLevelWithoutExperience);
        var differs = !byCharacter.TryGetValue(session.CharacterId, out var stored)
            || stored.SimulationSessionId != session.SimulationSessionId
            || stored.Progression != progression;
        byCharacter[session.CharacterId] = new StoredProgression(session.SimulationSessionId, progression);
        if (owner is not null && (change.ExperienceGained > 0 || change.LevelsGained != 0 || differs))
        {
            sender.SendControl(owner, EncodeOwner(progression, change.ExperienceGained, change.LevelsGained, reason));
        }

        if (change.LevelsGained > 0)
        {
            SendLevelUp(entity, (ushort)change.Level);
        }
    }

    // Whether a Mob of the level may give the character experience: not at the highest level, and
    // not at or below the highest Mob level that gives it none. A character without a kept
    // progression is asked about, since GameService decides.
    public bool GivesExperience(Guid characterId, int mobLevel)
    {
        if (!byCharacter.TryGetValue(characterId, out var stored))
        {
            return true;
        }

        var progression = stored.Progression;
        return progression.ExperienceToNextLevel > 0
            && mobLevel > progression.HighestMobLevelWithoutExperience;
    }

    public void Clear()
    {
        byCharacter.Clear();
    }

    private void SendLevelUp(PlayerSimulationEntity entity, ushort level)
    {
        var entityId = entity.NetworkEntityId;
        var owner = connectionBindings.TryGetConnectionId(entityId, out var connectionId) ? connectionId : -1;
        presence.CollectChangeRecipients(entityId, MissedStates.None, false, owner, recipients);
        byte[]? packet = null;
        foreach (var recipient in recipients)
        {
            if (peers.TryGetAuthenticatedPeer(recipient, out _, out var peer))
            {
                packet ??= RealtimeProtocol.EncodeEntityLeveledUp(new RealtimeEntityLeveledUp(entityId, level));
                sender.SendControl(peer, packet);
            }
        }
    }

    private static byte[] EncodeOwner(
        SimulationProgression progression,
        long experienceGained,
        int levelsGained,
        RealtimeProgressionChangeReason reason)
    {
        return RealtimeProtocol.EncodeOwnerProgressionChanged(new RealtimeOwnerProgressionChanged(
            (ushort)progression.Level,
            progression.Experience,
            progression.ExperienceToNextLevel,
            progression.HighestMobLevelWithoutExperience,
            experienceGained,
            levelsGained,
            reason));
    }

    private readonly record struct StoredProgression(Guid SimulationSessionId, SimulationProgression Progression);
}
