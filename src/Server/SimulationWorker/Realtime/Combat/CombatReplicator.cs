using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Entities;

namespace Mmorpg.SimulationWorker.Realtime;

// The live arrow count the owner sees: the committed stack minus uncommitted shots.
internal interface IOwnerAmmunitionSource
{
    (Guid ItemInstanceId, int Quantity) GetLiveAmmunition(Combatant player);
}

// Sends combat state to the peers that may see it: a combatant's state to every peer
// that sees the combatant, combat events to every peer that sees the attacker or the
// target or controls one of them, and the owner state to its owner only. Changes made
// during a tick are sent once at its end. Loop-confined.
internal sealed class CombatReplicator(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    ConnectionEntityBindingRegistry connectionBindings,
    EntityPresenceReplicator presence,
    CombatantRegistry combatants) : IEntitySpawnObserver
{
    private readonly List<RealtimeCombatEvent> events = [];
    private readonly List<RealtimeCombatEvent> recipientEvents = [];
    private readonly List<Combatant> combatantBuffer = [];

    public IOwnerAmmunitionSource? Ammunition { get; set; }

    public int PendingEventCount => events.Count;

    public void AttackReleased(ulong attackerEntityId, ulong targetEntityId, uint impactTick)
    {
        events.Add(new RealtimeCombatEvent(
            RealtimeCombatEventKind.AttackReleased,
            attackerEntityId,
            targetEntityId,
            impactTick,
            0,
            RealtimeCombatDamageType.Normal,
            0));
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
            healthAfter));
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
            0));
    }

    // Sends everything that changed in this tick.
    public void Flush(uint serverTick)
    {
        combatants.CopyTo(combatantBuffer);
        foreach (var combatant in combatantBuffer)
        {
            if (combatant.StateChanged)
            {
                BroadcastState(combatant);
            }

            if (combatant.OwnerStateChanged)
            {
                SendOwnerState(combatant, serverTick);
            }

            combatant.ClearChanges();
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
        if (combatants.TryGet(entityId, out var combatant))
        {
            sender.SendControl(peer, RealtimeProtocol.EncodeCombatantState(combatant.ToState()));
        }
    }

    public void Clear()
    {
        events.Clear();
    }

    private void BroadcastState(Combatant combatant)
    {
        byte[]? packet = null;
        foreach (var context in peers.Contexts)
        {
            if (context.Session is null
                || !presence.IsVisibleTo(context.PeerId, combatant.EntityId)
                || !peers.TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            packet ??= RealtimeProtocol.EncodeCombatantState(combatant.ToState());
            sender.SendControl(peer, packet);
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

    private void BroadcastEvents(uint serverTick)
    {
        foreach (var context in peers.Contexts)
        {
            if (context.Session is null
                || !connectionBindings.TryGetEntityId(context.PeerId, out var controlledEntityId)
                || !peers.TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            recipientEvents.Clear();
            foreach (var combatEvent in events)
            {
                if (Concerns(context.PeerId, controlledEntityId, combatEvent))
                {
                    recipientEvents.Add(combatEvent);
                }
            }

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
    }

    private bool Concerns(int connectionId, ulong controlledEntityId, RealtimeCombatEvent combatEvent)
    {
        return combatEvent.TargetEntityId == controlledEntityId
            || combatEvent.SourceEntityId == controlledEntityId
            || presence.IsVisibleTo(connectionId, combatEvent.TargetEntityId)
            || (combatEvent.SourceEntityId != 0
                && presence.IsVisibleTo(connectionId, combatEvent.SourceEntityId));
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
}
