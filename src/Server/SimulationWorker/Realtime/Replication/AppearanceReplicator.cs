using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Items;

namespace Mmorpg.SimulationWorker.Realtime;

// Gives every spawn the look of its player, and sends a new look to every connection that sees
// the player in detail and to its owner when it changes: its visible equipment, its lowered
// headwear, the tool it uses, such as its pickaxe while it gathers, or in Development its body
// model and look. A connection that sees the player out of detail gets the newest look later,
// as EntityPresenceReplicator describes. The owner gets its own look when it joins, since it
// never spawns its own player. A player whose look fails to build leaves the Shard alone, and a
// peer that a look fails to reach is disconnected alone. Loop-confined.
internal sealed class AppearanceReplicator(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    EntityPresenceReplicator presence,
    SimulationEntityRegistry entityRegistry,
    ConnectionEntityBindingRegistry connectionBindings,
    CharacterItemStateStore itemStates,
    CharacterLookStore looks,
    AppearanceReplicationSettings settings,
    RealtimeFailureIsolation isolation)
{
    public const string PhaseName = "appearances";

    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly HashSet<ulong> seen = [];
    private readonly List<ulong> gone = [];
    private readonly Dictionary<ulong, Sent> sent = [];
    private readonly List<int> recipients = [];

    // The look each player's spawns carry, built once and kept until the player's session, look
    // version, item-state revision, or used equipment slot moves, so the many spawns of an
    // interest refresh build no look.
    private readonly Dictionary<ulong, SpawnLook> spawnLooks = [];

    // The look a spawn of the player carries. A spawn can come before the first broadcast that
    // sees the player, so the look of its first spawn counts as sent: a change before that
    // broadcast still reaches the viewers the spawn reached.
    public RealtimeCharacterAppearance SpawnAppearanceOf(PlayerSimulationEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var (lookVersion, itemRevision) = VersionsOf(entity);
        if (!spawnLooks.TryGetValue(entity.NetworkEntityId, out var look)
            || !look.Matches(entity, lookVersion, itemRevision))
        {
            look = SpawnLook.Of(entity, lookVersion, itemRevision, AppearanceOf(entity));
            spawnLooks[entity.NetworkEntityId] = look;
        }

        if (!sent.ContainsKey(entity.NetworkEntityId))
        {
            sent[entity.NetworkEntityId] = new Sent(lookVersion, itemRevision, -1, entity.UsedEquipmentSlot, KeyOf(look.Appearance));
        }

        return look.Appearance;
    }

    // The player's look now, encoded for a viewer that missed it, or null for no player.
    public byte[]? EncodeLook(ulong entityId)
    {
        return entityRegistry.TryGetPlayer(entityId, out var entity)
            ? Encode(entityId, SpawnAppearanceOf(entity!))
            : null;
    }

    private RealtimeCharacterAppearance AppearanceOf(PlayerSimulationEntity entity)
    {
        var session = entity.Session;
        if (!looks.TryGet(session.CharacterId, session.SimulationSessionId, out var look, out _))
        {
            return new RealtimeCharacterAppearance(settings.DefaultBodyModel, [], [], false, string.Empty);
        }

        var worn = (look!.Outfit
            ?? (itemStates.TryGetItemState(session.CharacterId, session.SimulationSessionId, out var state)
                ? state!.Equipment.Select(item => new CharacterLookItem(item.EquipmentSlotId, item.DefinitionId))
                : [])).ToList();
        var toolInUse = entity.UsedEquipmentSlot.Length == 0
            ? string.Empty
            : worn.FirstOrDefault(item => string.Equals(item.EquipmentSlotId, entity.UsedEquipmentSlot, StringComparison.Ordinal))
                ?.DefinitionId ?? string.Empty;
        var visible = worn
            .Where(item => settings.VisibleEquipmentSlots.Contains(item.EquipmentSlotId, StringComparer.Ordinal))
            .OrderBy(item => settings.SlotOrder(item.EquipmentSlotId))
            .Take(RealtimeProtocolLimits.MaximumVisibleEquipment)
            .Select(item => new RealtimeVisibleEquipment(item.EquipmentSlotId, item.DefinitionId))
            .ToArray();
        var appearance = look.Appearance;
        return new RealtimeCharacterAppearance(
            appearance.BodyModel,
            appearance.Choices
                .Take(RealtimeProtocolLimits.MaximumAppearanceChoices)
                .Select(choice => new RealtimeAppearanceChoice(choice.Kind, choice.Slot, choice.Option))
                .ToArray(),
            visible,
            appearance.HeadwearLowered,
            toolInUse);
    }

    // Sends the looks that changed on the tick before each snapshot tick, ahead of the interest
    // refresh of that tick, so they reach the peers just before the next snapshot.
    public void AddTickPhase(SimulationTickPhases phases, SimulationTickCadence cadence)
    {
        ArgumentNullException.ThrowIfNull(cadence);
        phases.AddPeriodic(
            SimulationTickOrder.Appearances,
            PhaseName,
            (in SimulationTick tick) =>
            {
                if (!cadence.IsBeforeSnapshotTick(tick.ServerTick))
                {
                    return false;
                }

                Broadcast();
                return true;
            });
    }

    // Sends the looks that changed since the last call. A look is built again only when the
    // player's look version, its item-state revision, its used equipment slot, or its owner's
    // connection moved.
    public void Broadcast()
    {
        entityRegistry.CopyPlayersTo(playerBuffer);
        seen.Clear();
        foreach (var entity in playerBuffer)
        {
            seen.Add(entity.NetworkEntityId);
            try
            {
                BroadcastIfChanged(entity);
            }
            catch (Exception exception)
            {
                isolation.RemovePlayer(PhaseName, entity, exception);
            }
        }

        Forget(sent);
        Forget(spawnLooks);
    }

    // Drops what the replicator keeps of the players that are gone.
    private void Forget<T>(Dictionary<ulong, T> byEntity)
    {
        if (byEntity.Count <= seen.Count)
        {
            return;
        }

        gone.Clear();
        foreach (var entityId in byEntity.Keys)
        {
            if (!seen.Contains(entityId))
            {
                gone.Add(entityId);
            }
        }

        foreach (var entityId in gone)
        {
            byEntity.Remove(entityId);
        }
    }

    // Sends one player's look where it has to go when it changed.
    private void BroadcastIfChanged(PlayerSimulationEntity entity)
    {
        var (lookVersion, itemRevision) = VersionsOf(entity);
        var owner = connectionBindings.TryGetConnectionId(entity.NetworkEntityId, out var connectionId)
            ? connectionId
            : -1;
        var known = sent.TryGetValue(entity.NetworkEntityId, out var previous);
        if (known && previous.LookVersion == lookVersion && previous.ItemRevision == itemRevision && previous.Owner == owner
            && string.Equals(previous.UsedSlot, entity.UsedEquipmentSlot, StringComparison.Ordinal))
        {
            return;
        }

        var appearance = AppearanceOf(entity);
        spawnLooks[entity.NetworkEntityId] = SpawnLook.Of(entity, lookVersion, itemRevision, appearance);
        var key = KeyOf(appearance);
        var changed = known && !string.Equals(previous.Key, key, StringComparison.Ordinal);
        byte[]? packet = null;
        if (changed)
        {
            packet = Encode(entity.NetworkEntityId, appearance);
            SendToViewers(entity.NetworkEntityId, packet, owner);
        }

        if (owner >= 0 && (!known || changed || previous.Owner != owner))
        {
            SendTo(owner, packet ?? Encode(entity.NetworkEntityId, appearance));
        }

        sent[entity.NetworkEntityId] = new Sent(lookVersion, itemRevision, owner, entity.UsedEquipmentSlot, key);
    }

    public void Clear()
    {
        sent.Clear();
        spawnLooks.Clear();
    }

    // The player's look version and item-state revision; a look changes only when one of them moves.
    private (long LookVersion, long ItemRevision) VersionsOf(PlayerSimulationEntity entity)
    {
        var session = entity.Session;
        looks.TryGet(session.CharacterId, session.SimulationSessionId, out _, out var lookVersion);
        var itemRevision = itemStates.TryGetItemState(session.CharacterId, session.SimulationSessionId, out var state)
            ? state!.ItemStateRevision
            : -1;
        return (lookVersion, itemRevision);
    }

    // The viewers that see the player in detail get the look at once, and the others missed it.
    private void SendToViewers(ulong entityId, byte[] packet, int owner)
    {
        presence.CollectChangeRecipients(entityId, MissedStates.Look, false, owner, recipients);
        foreach (var connectionId in recipients)
        {
            if (peers.TryGetAuthenticatedPeer(connectionId, out var context, out var peer))
            {
                Send(peer, context, packet);
            }
        }
    }

    private void SendTo(int connectionId, byte[] packet)
    {
        if (peers.TryGetContext(connectionId, out var context)
            && context.Session is not null
            && peers.TryGetCurrentPeer(context, out var peer))
        {
            Send(peer, context, packet);
        }
    }

    private void Send(IRealtimePeerConnection peer, RealtimePeerContext context, byte[] packet)
    {
        try
        {
            sender.SendControl(peer, packet);
        }
        catch (Exception exception)
        {
            isolation.DisconnectPeer(PhaseName, peer, context, exception);
        }
    }

    private static byte[] Encode(ulong entityId, RealtimeCharacterAppearance appearance)
    {
        return RealtimeProtocol.EncodeEntityAppearanceChanged(new RealtimeEntityAppearanceChanged(entityId, appearance));
    }

    private static string KeyOf(RealtimeCharacterAppearance appearance)
    {
        return string.Join(
            "|",
            appearance.BodyModel,
            string.Join(";", appearance.Choices.Select(choice => $"{choice.Kind}/{choice.Slot}={choice.Option}")),
            string.Join(";", appearance.Equipment.Select(item => $"{item.EquipmentSlotId}={item.DefinitionId}")),
            appearance.HeadwearLowered ? "lowered" : "raised",
            appearance.ToolInUse);
    }

    private readonly record struct Sent(long LookVersion, long ItemRevision, int Owner, string UsedSlot, string Key);

    // A built look with what it was built from.
    private readonly record struct SpawnLook(
        Guid SessionId,
        long LookVersion,
        long ItemRevision,
        string UsedSlot,
        RealtimeCharacterAppearance Appearance)
    {
        public static SpawnLook Of(PlayerSimulationEntity entity, long lookVersion, long itemRevision, RealtimeCharacterAppearance appearance) =>
            new(entity.Session.SimulationSessionId, lookVersion, itemRevision, entity.UsedEquipmentSlot, appearance);

        public bool Matches(PlayerSimulationEntity entity, long lookVersion, long itemRevision) =>
            SessionId == entity.Session.SimulationSessionId
            && LookVersion == lookVersion
            && ItemRevision == itemRevision
            && string.Equals(UsedSlot, entity.UsedEquipmentSlot, StringComparison.Ordinal);
    }
}

// What the appearance replicator takes from content: the body model of a player whose look is not
// known yet, and the equipment slots whose items others see, in order.
internal sealed record AppearanceReplicationSettings(string DefaultBodyModel, IReadOnlyList<string> VisibleEquipmentSlots)
{
    public static AppearanceReplicationSettings Default { get; } =
        new("Human_Male", ["head", "body", "legs", "hands", "feet", "back"]);

    // The place of a visible equipment slot in content order.
    public int SlotOrder(string equipmentSlotId)
    {
        for (var index = 0; index < VisibleEquipmentSlots.Count; index++)
        {
            if (string.Equals(VisibleEquipmentSlots[index], equipmentSlotId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return VisibleEquipmentSlots.Count;
    }
}
