using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// What Keep took of a committed change for Announce to send: the character's entity, the kept
// progression with what the change gained and why, whether its owner hears of it, whether the
// replicator finds that owner itself, and whether the viewers hear of a higher level.
internal sealed record KeptProgression(
    PlayerSimulationEntity Entity,
    SimulationProgression Progression,
    long ExperienceGained,
    int LevelsGained,
    RealtimeProgressionChangeReason Reason,
    bool TellsOwner,
    bool FindsOwner,
    bool LevelUp);

// The level and experience of every character in the world, as GameService last reported them,
// and who hears of them. The owner gets its progression after every join and after every committed
// change; the viewers that see the character in detail hear when it reached a higher level, with
// no catch-up for the others. GameService computes every value; the worker keeps the latest only to
// send it and to skip the kill credits that cannot give the character experience. A character's
// progression lasts as long as its entity, also as a lingering body, and never goes back to an
// older value of the same session. A committed change is kept first, before the operation that
// committed it releases its lane, so the next operation of the lane sees it, and announced at its
// place among the owner's messages. Loop-confined.
internal sealed class ProgressionReplicator(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    EntityPresenceReplicator presence,
    SimulationEntityRegistry entityRegistry,
    ConnectionEntityBindingRegistry connectionBindings,
    RealtimeSimulationClock clock,
    int tickRateHz)
{
    // How long a kept answer that a Mob gives the character nothing is trusted. GameService
    // derives it from progression rules that can change while the worker runs, and a skipped
    // credit never asks again, so after this time one credit goes to GameService, whose answer
    // is kept again, also when it gained nothing.
    public static readonly TimeSpan NoExperienceTrust = TimeSpan.FromMinutes(5);

    private readonly Dictionary<Guid, StoredProgression> byCharacter = [];
    private readonly List<int> recipients = [];
    private readonly uint trustTicks = CombatTicks.FromSeconds((float)NoExperienceTrust.TotalSeconds, tickRateHz);

    public int Count => byCharacter.Count;

    // A joined player, also one that reconnected, learns its progression. While a session lives on
    // this worker every change of its character goes through this worker, so what the worker
    // kept for the same session is never older than the join ticket, which may have been read
    // before a change committed: the kept value wins. A new session takes the ticket's. A
    // development bot has none.
    public void OnPlayerJoined(RealtimePeerContext context, PlayerSimulationEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var session = entity.Session;
        if (session.IsSyntheticBot)
        {
            return;
        }

        SimulationProgression progression;
        if (byCharacter.TryGetValue(session.CharacterId, out var stored)
            && stored.SimulationSessionId == session.SimulationSessionId)
        {
            // Kept as it was, also its time: it is no newer answer of GameService.
            progression = stored.Progression;
        }
        else if (session.Progression is { } ticket)
        {
            progression = ticket;
            Store(session.CharacterId, session.SimulationSessionId, progression);
        }
        else
        {
            return;
        }

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

    // Keeps and announces a committed change at once, for a caller that releases no lane between.
    public void Apply(
        ActiveSimulationSession session,
        IRealtimePeerConnection? owner,
        SimulationProgressionChange change,
        RealtimeProgressionChangeReason reason)
    {
        Announce(Keep(session, change, reason), owner);
    }

    // Keeps a committed change of the session's character while the character's entity is in the
    // world, and returns what Announce sends, or null when nothing was kept. The owner hears of it
    // when it gained experience, changed the level, or differs from what was kept, so a stale view
    // heals. A late change of an older session of the character, whose body went while its commit
    // was on its way, is kept only when it is ahead of what the entity's session holds: a higher
    // level, or the same level with more experience. That is safe because play only raises
    // progression and the admin tools, which can lower it, run on the current session.
    public KeptProgression? Keep(
        ActiveSimulationSession session,
        SimulationProgressionChange change,
        RealtimeProgressionChangeReason reason)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(change);
        if (!entityRegistry.TryGetPlayerByCharacterId(session.CharacterId, out var entity) || entity is null)
        {
            return null;
        }

        var progression = new SimulationProgression(
            change.Level,
            change.Experience,
            change.ExperienceToNextLevel,
            change.HighestMobLevelWithoutExperience);
        var currentSessionId = entity.Session.SimulationSessionId;
        var kept = byCharacter.TryGetValue(session.CharacterId, out var stored)
            && stored.SimulationSessionId == currentSessionId;
        if (currentSessionId != session.SimulationSessionId)
        {
            if (kept && !IsAhead(progression, stored.Progression))
            {
                return null;
            }

            Store(session.CharacterId, currentSessionId, progression);
            var levelsGained = kept ? progression.Level - stored.Progression.Level : change.LevelsGained;
            return new KeptProgression(
                entity,
                progression,
                change.ExperienceGained,
                levelsGained,
                reason,
                TellsOwner: true,
                FindsOwner: true,
                LevelUp: levelsGained > 0);
        }

        var differs = !kept || stored.Progression != progression;
        Store(session.CharacterId, currentSessionId, progression);
        return new KeptProgression(
            entity,
            progression,
            change.ExperienceGained,
            change.LevelsGained,
            reason,
            TellsOwner: change.ExperienceGained > 0 || change.LevelsGained != 0 || differs,
            FindsOwner: false,
            LevelUp: change.LevelsGained > 0);
    }

    // Sends what Keep took: to the owner, the peer that plays the character now or none, and a
    // higher level to the viewers that see the character in detail. A late change of an older
    // session goes to the peer that plays the entity's session, which the caller does not know.
    public void Announce(KeptProgression? kept, IRealtimePeerConnection? owner)
    {
        if (kept is null)
        {
            return;
        }

        var recipient = kept.FindsOwner ? CurrentPeerOf(kept.Entity) : owner;
        if (recipient is not null && kept.TellsOwner)
        {
            sender.SendControl(recipient, EncodeOwner(kept.Progression, kept.ExperienceGained, kept.LevelsGained, kept.Reason));
        }

        if (kept.LevelUp)
        {
            SendLevelUp(kept.Entity, (ushort)kept.Progression.Level);
        }
    }

    // Whether a Mob of the level may give the character experience: not at the highest level, and
    // not at or below the highest Mob level that gives it none. A character without a kept
    // progression is asked about, since GameService decides, and so is one whose progression was
    // kept longer ago than NoExperienceTrust, since the rules behind it may have changed.
    public bool GivesExperience(Guid characterId, int mobLevel)
    {
        if (!byCharacter.TryGetValue(characterId, out var stored)
            || CombatTicks.IsReached(clock.ServerTick, unchecked(stored.StoredAtTick + trustTicks)))
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

    private void Store(Guid characterId, Guid simulationSessionId, SimulationProgression progression)
    {
        byCharacter[characterId] = new StoredProgression(simulationSessionId, progression, clock.ServerTick);
    }

    private static bool IsAhead(SimulationProgression change, SimulationProgression kept)
    {
        return change.Level > kept.Level
            || (change.Level == kept.Level && change.Experience > kept.Experience);
    }

    // The peer that plays the entity's session now, or null.
    private IRealtimePeerConnection? CurrentPeerOf(PlayerSimulationEntity entity)
    {
        return connectionBindings.TryGetConnectionId(entity.NetworkEntityId, out var connectionId)
            && peers.TryGetContext(connectionId, out var context)
            && context.Session?.SimulationSessionId == entity.Session.SimulationSessionId
            && peers.TryGetCurrentPeer(context, out var peer)
                ? peer
                : null;
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

    private readonly record struct StoredProgression(
        Guid SimulationSessionId,
        SimulationProgression Progression,
        uint StoredAtTick);
}
