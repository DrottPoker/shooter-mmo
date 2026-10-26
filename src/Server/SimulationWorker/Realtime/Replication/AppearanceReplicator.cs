using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Items;

namespace Mmorpg.SimulationWorker.Realtime;

// Gives every spawn the look of its player, and sends a new look to every connection that sees
// the player and to its owner when it changes: its visible equipment, its lowered headwear, the
// tool it uses, such as its pickaxe while it gathers, or in Development its body model and look.
// The owner gets its own look when it joins, since it never spawns its own player. Loop-confined.
internal sealed class AppearanceReplicator(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    EntityPresenceReplicator presence,
    SimulationEntityRegistry entityRegistry,
    ConnectionEntityBindingRegistry connectionBindings,
    CharacterItemStateStore itemStates,
    CharacterLookStore looks,
    AppearanceReplicationSettings settings)
{
    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly HashSet<ulong> seen = [];
    private readonly Dictionary<ulong, Sent> sent = [];

    // The look a spawn of the player carries. A spawn can come before the first broadcast that
    // sees the player, so the look of its first spawn counts as sent: a change before that
    // broadcast still reaches the viewers the spawn reached.
    public RealtimeCharacterAppearance SpawnAppearanceOf(PlayerSimulationEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var appearance = AppearanceOf(entity);
        if (!sent.ContainsKey(entity.NetworkEntityId))
        {
            var (lookVersion, itemRevision) = VersionsOf(entity);
            sent[entity.NetworkEntityId] = new Sent(lookVersion, itemRevision, -1, entity.UsedEquipmentSlot, KeyOf(appearance));
        }

        return appearance;
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
            var (lookVersion, itemRevision) = VersionsOf(entity);
            var owner = connectionBindings.TryGetConnectionId(entity.NetworkEntityId, out var connectionId)
                ? connectionId
                : -1;
            var known = sent.TryGetValue(entity.NetworkEntityId, out var previous);
            if (known && previous.LookVersion == lookVersion && previous.ItemRevision == itemRevision && previous.Owner == owner
                && string.Equals(previous.UsedSlot, entity.UsedEquipmentSlot, StringComparison.Ordinal))
            {
                continue;
            }

            var appearance = AppearanceOf(entity);
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

        if (sent.Count > seen.Count)
        {
            foreach (var entityId in sent.Keys.Where(entityId => !seen.Contains(entityId)).ToArray())
            {
                sent.Remove(entityId);
            }
        }
    }

    public void Clear()
    {
        sent.Clear();
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

    private void SendToViewers(ulong entityId, byte[] packet, int owner)
    {
        foreach (var context in peers.Contexts)
        {
            if (context.PeerId != owner
                && context.Session is not null
                && presence.IsVisibleTo(context.PeerId, entityId)
                && peers.TryGetCurrentPeer(context, out var peer))
            {
                sender.SendControl(peer, packet);
            }
        }
    }

    private void SendTo(int connectionId, byte[] packet)
    {
        if (peers.TryGetContext(connectionId, out var context)
            && context.Session is not null
            && peers.TryGetCurrentPeer(context, out var peer))
        {
            sender.SendControl(peer, packet);
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
