using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;

namespace Mmorpg.Tools.StackStressGenerator;

public enum PlayingMoveState
{
    // No walk: the bot stands.
    Standing,

    // The route is being planned.
    Planning,

    // The bot follows its route.
    Walking,

    // The route ended; RouteReachedDestination says whether it ended at the destination.
    Arrived,

    // No route was found, the planners were busy, or the walk was blocked or too slow.
    Failed
}

// The outcome of a bot's intent, once its result arrived. Expected says that the rejection is
// one of the conflicts PlayingExpectedConflicts explains.
public sealed record PlayingIntentOutcome(string Operation, bool Succeeded, string? Code, bool Expected);

// An item of a bot's kit and the equipment slot a player equips it into, from the item catalog.
public sealed record PlayingKitItem(string DefinitionId, string EquipmentSlotId);

// The kit a bot received for its role: the items it equips and the range of its weapon.
public sealed record PlayingKit(string? Name, IReadOnlyList<PlayingKitItem> Items, float WeaponRange)
{
    public static PlayingKit None { get; } = new(null, [], 0f);
}

// An action of a capability of the open world interaction: a gathering start, or a vendor sale
// or purchase with the total price the bot expects, as a Unity client computes it from the
// catalog.
public sealed record PlayingCapabilityAction(
    RealtimeWorldActorCapabilityKind CapabilityKind,
    RealtimeCapabilityActionKind Action,
    SimulationBotOwnedItem? Item = null,
    string? OfferDefinitionId = null,
    int Quantity = 0,
    long ExpectedPrice = 0);

// What a role's state machine can see and do: the bot's world view, one intent at a time with
// its outcome, and walking. The real body sends the intents over UDP like a Unity client and
// measures them; tests drive a role with a fake body and a fake clock.
public interface IPlayingBotBody
{
    int BotIndex { get; }

    SimulationBotWorldView View { get; }

    PlayingKit Kit { get; }

    // The item and actor catalogs a Unity client reads too.
    PlayingGameData GameData { get; }

    // Joined, and the bot's own items are known and current.
    bool IsReady { get; }

    bool HasPendingIntent { get; }

    // The combat events that arrived since the previous think, oldest first.
    IReadOnlyList<RealtimeCombatEvent> RecentCombatEvents { get; }

    PlayingMoveState MoveState { get; }

    bool RouteReachedDestination { get; }

    SimulationVector3 Position { get; }

    // The outcome of the latest intent, once; null while it is pending or after it was taken.
    PlayingIntentOutcome? TakeOutcome();

    // The walkable ground under a point, as the worker places a teleported character.
    bool TryFindGround(float x, float z, out SimulationVector3 ground);

    bool Equip(SimulationBotOwnedItem item, string equipmentSlotId);

    bool Teleport(SimulationVector3 destination);

    bool Attack(ulong targetEntityId);

    bool ClearTarget();

    bool OpenCorpse(Guid corpseId);

    bool LootAll(Guid corpseId, long expectedRevision);

    bool CloseCorpse(Guid corpseId, long expectedRevision);

    // Opens a world actor's interaction; the outcome succeeds once the worker opened it, and
    // the view then holds the open interaction.
    bool OpenInteraction(SimulationBotWorldActor actor, string operation);

    // Sends an action of a capability of the open interaction.
    bool CapabilityAction(string operation, PlayingCapabilityAction action);

    bool CloseInteraction(string operation);

    // Moves one of the bot's carried items to a slot of one of its containers.
    bool MoveItem(SimulationBotOwnedItem item, Guid containerId, int slotIndex);

    // Whether nothing of the worker's collision blocks the line from the eye of a character
    // standing at a point to a target point, by the rule the worker checks interactions with.
    bool HasLineOfSight(SimulationVector3 standPoint, SimulationVector3 target);

    // Plans a route toward the destination and walks it; a far destination gets the part of
    // the route the planner reaches, and the role walks on from there.
    void WalkTo(SimulationVector3 destination);

    void Stand();

    void Count(PlayingCounter counter, long amount = 1);
}
