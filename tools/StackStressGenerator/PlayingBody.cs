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

// What a role's state machine can see and do: the bot's world view, one intent at a time with
// its outcome, and walking. The real body sends the intents over UDP like a Unity client and
// measures them; tests drive a role with a fake body and a fake clock.
public interface IPlayingBotBody
{
    int BotIndex { get; }

    SimulationBotWorldView View { get; }

    PlayingKit Kit { get; }

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

    // Plans a route toward the destination and walks it; a far destination gets the part of
    // the route the planner reaches, and the role walks on from there.
    void WalkTo(SimulationVector3 destination);

    void Stand();

    void Count(PlayingCounter counter, long amount = 1);
}
