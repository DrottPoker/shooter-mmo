using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.WorldData.Combat;

namespace Mmorpg.SimulationWorker.Realtime;

// The live arrow count the owner sees: the committed stack minus uncommitted shots.
internal interface IOwnerAmmunitionSource
{
    (Guid ItemInstanceId, int Quantity) GetLiveAmmunition(Combatant player);
}

// Sends combat state to the peers that may see it: a combatant's state to every peer
// that sees the combatant in detail, and to every peer that sees it when its life state
// changed; combat events to every peer that sees the attacker or the target in detail or
// controls one of them, a death to every peer that sees either; and the owner state to its
// owner only. A peer that sees a combatant out of detail gets its newest state later, as
// EntityPresenceReplicator describes. Changes made during a tick are sent once at its end,
// after the targets of the tick, so a combatant's target and whoever targets it see it in
// detail at once. A combatant's state names its health regeneration, which viewers follow
// without a new state. Loop-confined.
internal sealed class CombatReplicator(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    ConnectionEntityBindingRegistry connectionBindings,
    EntityPresenceReplicator presence,
    CombatantRegistry combatants,
    RealtimeSimulationClock clock,
    int healthRegenerationPerSecond)
{
    private readonly List<RealtimeCombatEvent> events = [];
    private readonly List<Combatant> combatantBuffer = [];
    private readonly List<Combatant> retargeted = [];
    private readonly List<int> recipients = [];

    // The events of a tick per recipient, and the recipients in the order of their first event.
    private readonly Dictionary<int, RecipientEvents> eventsByRecipient = [];
    private readonly List<int> eventRecipients = [];

    public IOwnerAmmunitionSource? Ammunition { get; set; }

    public int PendingEventCount => events.Count;

    // An attack started; every client plays its attacker's attack clip so it strikes or looses
    // at the release tick.
    public void AttackStarted(ulong attackerEntityId, ulong targetEntityId, uint releaseTick, string combatStyle)
    {
        AddAttackEvent(RealtimeCombatEventKind.AttackStarted, attackerEntityId, targetEntityId, releaseTick, combatStyle);
    }

    public void AttackReleased(ulong attackerEntityId, ulong targetEntityId, uint impactTick, string combatStyle)
    {
        AddAttackEvent(RealtimeCombatEventKind.AttackReleased, attackerEntityId, targetEntityId, impactTick, combatStyle);
    }

    // An attack ended in its windup and never released.
    public void AttackCancelled(ulong attackerEntityId, ulong targetEntityId, string combatStyle)
    {
        AddAttackEvent(RealtimeCombatEventKind.AttackCancelled, attackerEntityId, targetEntityId, 0, combatStyle);
    }

    private void AddAttackEvent(
        RealtimeCombatEventKind kind,
        ulong attackerEntityId,
        ulong targetEntityId,
        uint tick,
        string combatStyle)
    {
        events.Add(new RealtimeCombatEvent(
            kind,
            attackerEntityId,
            targetEntityId,
            tick,
            0,
            RealtimeCombatDamageType.Normal,
            0,
            ToAttackStyle(combatStyle)));
    }

    public void Hit(ulong attackerEntityId, ulong targetEntityId, int damage, string damageType, int healthAfter)
    {
        events.Add(new RealtimeCombatEvent(
            RealtimeCombatEventKind.Hit,
            attackerEntityId,
            targetEntityId,
            0,
            damage,
            ToDamageType(damageType),
            healthAfter,
            RealtimeCombatAttackStyle.Melee));
    }

    public void Died(ulong killerEntityId, ulong targetEntityId)
    {
        events.Add(new RealtimeCombatEvent(
            RealtimeCombatEventKind.Died,
            killerEntityId == targetEntityId ? 0 : killerEntityId,
            targetEntityId,
            0,
            0,
            RealtimeCombatDamageType.Normal,
            0,
            RealtimeCombatAttackStyle.Melee));
    }

    // Sends everything that changed in this tick.
    public void Flush(uint serverTick)
    {
        combatants.CopyTo(combatantBuffer);
        UpdateTargets();
        foreach (var combatant in combatantBuffer)
        {
            if (combatant.StateChanged)
            {
                BroadcastState(combatant, serverTick);
            }

            if (combatant.OwnerStateChanged)
            {
                SendOwnerState(combatant, serverTick);
            }

            combatant.ClearChanges();
        }

        // A new target, and a combatant that took an entity as its target, bring each side
        // whatever it missed of the other.
        foreach (var combatant in retargeted)
        {
            if (connectionBindings.TryGetConnectionId(combatant.EntityId, out var connectionId))
            {
                presence.CatchUp(connectionId, combatant.ReplicatedTargetEntityId);
            }

            if (connectionBindings.TryGetConnectionId(combatant.ReplicatedTargetEntityId, out var targetConnectionId))
            {
                presence.CatchUp(targetConnectionId, combatant.EntityId);
            }
        }

        if (events.Count > 0)
        {
            BroadcastEvents(serverTick);
            events.Clear();
        }
    }

    // A spawn is always followed by the combatant's state, so no client shows a
    // combatant without health.
    public void OnSpawnSent(IRealtimePeerConnection peer, ulong entityId)
    {
        var packet = EncodeState(entityId);
        if (packet is not null)
        {
            sender.SendControl(peer, packet);
        }
    }

    // The combatant's state now, encoded, or null for an entity that is no combatant.
    public byte[]? EncodeState(ulong entityId)
    {
        return combatants.TryGet(entityId, out var combatant)
            ? RealtimeProtocol.EncodeCombatantState(combatant.ToState(clock.ServerTick, healthRegenerationPerSecond))
            : null;
    }

    public void Clear()
    {
        events.Clear();
        eventsByRecipient.Clear();
        eventRecipients.Clear();
    }

    // Tells presence every target that changed since the last flush, so the states and events
    // of this tick reach a combatant's target and whoever targets it in detail.
    private void UpdateTargets()
    {
        retargeted.Clear();
        foreach (var combatant in combatantBuffer)
        {
            var target = combatant.IsAlive ? combatant.TargetEntityId : 0;
            if (target == combatant.ReplicatedTargetEntityId)
            {
                continue;
            }

            combatant.ReplicatedTargetEntityId = target;
            presence.SetTarget(combatant.EntityId, target);
            if (target != 0)
            {
                retargeted.Add(combatant);
            }
        }
    }

    // A change of the life state, a death, a respawn, or an evading Mob, reaches every viewer at
    // once; any other change only the viewers that see the combatant in detail.
    private void BroadcastState(Combatant combatant, uint serverTick)
    {
        var urgent = combatant.LifeState != combatant.ReplicatedLifeState;
        combatant.ReplicatedLifeState = combatant.LifeState;
        presence.CollectChangeRecipients(combatant.EntityId, MissedStates.Combatant, urgent, -1, recipients);
        byte[]? packet = null;
        foreach (var connectionId in recipients)
        {
            if (peers.TryGetAuthenticatedPeer(connectionId, out _, out var peer))
            {
                packet ??= RealtimeProtocol.EncodeCombatantState(combatant.ToState(serverTick, healthRegenerationPerSecond));
                sender.SendControl(peer, packet);
            }
        }
    }

    private void SendOwnerState(Combatant player, uint serverTick)
    {
        if (player.Kind != CombatantKind.Player
            || !connectionBindings.TryGetConnectionId(player.EntityId, out var connectionId)
            || !peers.TryGetContext(connectionId, out var context)
            || context.Session is null
            || !peers.TryGetCurrentPeer(context, out var peer))
        {
            return;
        }

        var (ammunitionItemId, ammunitionQuantity) = Ammunition?.GetLiveAmmunition(player)
            ?? (Guid.Empty, 0);
        var dead = !player.IsAlive;
        sender.SendControl(peer, RealtimeProtocol.EncodeOwnerCombatState(new RealtimeOwnerCombatState(
            player.OwnerStateRevision,
            serverTick,
            dead ? 0 : player.Health,
            player.MaximumHealth,
            player.Mana,
            player.MaximumMana,
            dead ? RealtimeCombatLifeState.Dead : RealtimeCombatLifeState.Alive,
            player.LifeSequence,
            dead ? player.RespawnTick : 0,
            player.InCombat,
            dead ? 0 : player.TargetEntityId,
            dead
                ? RealtimeCombatAttackStatus.Dead
                : player.TargetEntityId == 0
                    ? RealtimeCombatAttackStatus.Idle
                    : player.AttackStatus,
            player.NextAttackTick,
            player.LockoutEndTick,
            ammunitionQuantity > 0 ? ammunitionItemId : Guid.Empty,
            Math.Max(0, ammunitionQuantity))));
    }

    // Each event goes to whoever controls its source or target and to the viewers that see
    // either in detail, a death to every viewer of either; each recipient gets its events of the
    // tick in their order.
    private void BroadcastEvents(uint serverTick)
    {
        eventRecipients.Clear();
        for (var index = 0; index < events.Count; index++)
        {
            var combatEvent = events[index];
            var everyViewer = combatEvent.Kind == RealtimeCombatEventKind.Died;
            AddViewers(combatEvent.TargetEntityId, index, everyViewer);
            AddController(combatEvent.TargetEntityId, index);
            if (combatEvent.SourceEntityId != 0)
            {
                AddViewers(combatEvent.SourceEntityId, index, everyViewer);
                AddController(combatEvent.SourceEntityId, index);
            }
        }

        foreach (var connectionId in eventRecipients)
        {
            var recipient = eventsByRecipient[connectionId];
            if (peers.TryGetAuthenticatedPeer(connectionId, out _, out var peer))
            {
                var recipientEvents = recipient.Events;
                for (var offset = 0; offset < recipientEvents.Count; offset += RealtimeProtocolLimits.MaximumCombatEventsPerBatch)
                {
                    var count = Math.Min(
                        RealtimeProtocolLimits.MaximumCombatEventsPerBatch,
                        recipientEvents.Count - offset);
                    sender.SendControl(peer, RealtimeProtocol.EncodeCombatEventBatch(new RealtimeCombatEventBatch(
                        serverTick,
                        recipientEvents.GetRange(offset, count).ToArray())));
                }
            }

            recipient.Events.Clear();
            recipient.LastEvent = -1;
        }

        // The lists stay for the next tick, but not for connections that are gone for good.
        if (eventsByRecipient.Count > (2 * peers.Count) + 16)
        {
            eventsByRecipient.Clear();
        }
    }

    private void AddViewers(ulong entityId, int eventIndex, bool everyViewer)
    {
        presence.CollectChangeRecipients(entityId, MissedStates.None, everyViewer, -1, recipients);
        foreach (var connectionId in recipients)
        {
            AddRecipient(connectionId, eventIndex);
        }
    }

    // Whoever controls the source or the target gets the event, whether it sees the other or not.
    private void AddController(ulong entityId, int eventIndex)
    {
        if (connectionBindings.TryGetConnectionId(entityId, out var connectionId))
        {
            AddRecipient(connectionId, eventIndex);
        }
    }

    private void AddRecipient(int connectionId, int eventIndex)
    {
        if (!eventsByRecipient.TryGetValue(connectionId, out var recipient))
        {
            recipient = new RecipientEvents();
            eventsByRecipient.Add(connectionId, recipient);
        }

        if (recipient.LastEvent == eventIndex)
        {
            return;
        }

        if (recipient.Events.Count == 0)
        {
            eventRecipients.Add(connectionId);
        }

        recipient.LastEvent = eventIndex;
        recipient.Events.Add(events[eventIndex]);
    }

    private static RealtimeCombatDamageType ToDamageType(string damageType)
    {
        return damageType switch
        {
            "fire" => RealtimeCombatDamageType.Fire,
            "cold" => RealtimeCombatDamageType.Cold,
            "nature" => RealtimeCombatDamageType.Nature,
            "shadow" => RealtimeCombatDamageType.Shadow,
            _ => RealtimeCombatDamageType.Normal
        };
    }

    private static RealtimeCombatAttackStyle ToAttackStyle(string combatStyle)
    {
        return combatStyle switch
        {
            CombatStyleIds.Ranged => RealtimeCombatAttackStyle.Ranged,
            CombatStyleIds.Magic => RealtimeCombatAttackStyle.Magic,
            _ => RealtimeCombatAttackStyle.Melee
        };
    }

    // One recipient's events of a tick, and the index of the event it got last.
    private sealed class RecipientEvents
    {
        public List<RealtimeCombatEvent> Events { get; } = [];

        public int LastEvent { get; set; } = -1;
    }
}
