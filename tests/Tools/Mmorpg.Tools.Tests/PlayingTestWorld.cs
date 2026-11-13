using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;
using Mmorpg.Tools.StackStressGenerator;

namespace Mmorpg.Tools.Tests;

// Builders of the protocol messages a playing bot's world view receives, shared by the world
// view and role tests.
internal static class PlayingTestWorld
{
    public const ulong OwnEntity = 7;
    public const string CharacterName = "Stack Test 1";
    public static readonly Guid PermanentInventory = Guid.Parse("a1f7c0de-0000-4000-8000-000000000001");

    public static readonly RealtimeMovementSettings Movement = new(
        30, 15, 5f, 720f, -24f, 55f, -2f, 0f, -190f, 190f, -190f, 190f, 0.35f, 2f, 0.35f, 45f, 0.4f, 0.1f, 6);

    public static RealtimeJoinAccepted Join(long itemStateRevision = 3, float x = 0f, float z = -12f)
    {
        return new RealtimeJoinAccepted(
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            CharacterName,
            "local-shard-2",
            "development-world-2",
            OwnEntity,
            "movement-simulation",
            "collision-revision",
            "2026-11-13T00:00:00Z",
            "2026-11-14T00:00:00Z",
            false,
            new RealtimeCarryState(itemStateRevision, 100, 3000),
            Movement,
            new RealtimePlayerState(x, 0f, z, 0f, 0f, 0f, 0f, true),
            new RealtimeShardRules(true));
    }

    public static RealtimeOwnerSimulationSnapshot OwnerAt(uint sequence, float x, float z, uint pathId = 0, byte nextWaypoint = 0)
    {
        return new RealtimeOwnerSimulationSnapshot(
            sequence,
            sequence * 2,
            sequence,
            new RealtimePlayerState(x, 0f, z, 0f, 0f, 0f, 0f, true),
            new RealtimeMovementPathProgress(pathId, nextWaypoint, 0));
    }

    public static RealtimeOwnerCombatState OwnCombat(
        long revision,
        int health = 100,
        RealtimeCombatLifeState lifeState = RealtimeCombatLifeState.Alive,
        uint lifeSequence = 1,
        ulong target = 0,
        RealtimeCombatAttackStatus status = RealtimeCombatAttackStatus.Idle)
    {
        return new RealtimeOwnerCombatState(
            revision,
            (uint)revision,
            health,
            100,
            100,
            100,
            lifeState,
            lifeSequence,
            0,
            false,
            target,
            lifeState == RealtimeCombatLifeState.Dead ? RealtimeCombatAttackStatus.Dead : status,
            0,
            0,
            Guid.Empty,
            0);
    }

    public static RealtimeWorldActorSpawn Mob(ulong entityId, float x, float z, string definitionId = "mob.grey_wolf")
    {
        return new RealtimeWorldActorSpawn(
            entityId,
            Guid.NewGuid(),
            definitionId,
            "development_world_2.load_test",
            RealtimeWorldActorKind.Mob,
            "wild",
            RealtimeWorldActorDisposition.Neutral,
            1,
            RealtimeWorldActorAggression.Defensive,
            "mob.feral_wolf",
            1,
            1,
            true,
            x,
            0f,
            z,
            0f,
            RealtimeWorldActorActivityTier.Active,
            0f,
            0.55f,
            0f,
            1.2f,
            1.1f,
            1.8f,
            []);
    }

    public static RealtimeCombatantState Combatant(
        ulong entityId,
        long revision,
        int health = 50,
        RealtimeCombatLifeState lifeState = RealtimeCombatLifeState.Alive,
        ulong target = 0,
        ulong taggedBy = 0)
    {
        return new RealtimeCombatantState(entityId, revision, health, 50, lifeState, target != 0, target, taggedBy);
    }

    public static RealtimeCorpsePresenceSnapshotChunk Corpses(
        uint sequence,
        params RealtimeCorpsePresence[] corpses)
    {
        return new RealtimeCorpsePresenceSnapshotChunk(sequence, 0, 1, corpses);
    }

    public static RealtimeCorpsePresence MobCorpse(Guid id, float x, float z, bool lootable = true, bool empty = false)
    {
        return new RealtimeCorpsePresence(
            id, x, 0f, z, 0f, 0, empty, lootable, new RealtimeCorpseSource(null, "mob.grey_wolf"), "corpse.mob", null);
    }

    public static RealtimeCorpsePresence PlayerCorpse(Guid id, float x, float z)
    {
        return new RealtimeCorpsePresence(
            id, x, 0f, z, 0f, 0, false, true, new RealtimeCorpseSource(CharacterName, null), "corpse.remains", null);
    }

    public static RealtimeCorpseViewStateChunk View(Guid corpseId, long revision)
    {
        return new RealtimeCorpseViewStateChunk(
            Guid.NewGuid(),
            RealtimeCorpseViewUpdateKind.Snapshot,
            corpseId,
            -1,
            revision,
            0,
            new RealtimeCorpseSource(null, "mob.grey_wolf"),
            "corpse.mob",
            0,
            1,
            "general_inventory",
            Guid.NewGuid(),
            1,
            true,
            4,
            0,
            []);
    }

    public static RealtimeCorpseInteractionResult CorpseResult(
        Guid corpseId,
        RealtimeCorpseInteractionKind kind,
        bool succeeded = true)
    {
        return new RealtimeCorpseInteractionResult(
            Guid.NewGuid(),
            kind,
            corpseId,
            succeeded,
            false,
            false,
            new RealtimeCarryState(3, 100, 3000),
            succeeded ? null : new RealtimeError("corpse_not_found", "gone"));
    }

    public static SimulationBotItemSnapshot Inventory(long revision, params SimulationBotOwnedItem[] items)
    {
        return new SimulationBotItemSnapshot(
            revision,
            PermanentInventory,
            [new SimulationBotItemContainer(PermanentInventory, 1, 20)],
            items);
    }

    public static SimulationBotOwnedItem Carried(string definitionId, int slot, int quantity = 1)
    {
        return new SimulationBotOwnedItem(Guid.NewGuid(), definitionId, quantity, 1, PermanentInventory, slot, string.Empty);
    }

    public static SimulationBotOwnedItem Worn(string definitionId, string slot, int quantity = 1)
    {
        return new SimulationBotOwnedItem(Guid.NewGuid(), definitionId, quantity, 1, Guid.Empty, -1, slot);
    }

    public static PlayingKit SwordKit { get; } =
        new("sword", [new PlayingKitItem("weapon.iron_sword", "main_hand")], 2.5f);
}

// A body that records the intents a role sends and lets a test answer them, with a real world
// view that the test feeds protocol messages.
internal sealed class FakePlayingBody : IPlayingBotBody
{
    private uint ownerSequence;

    public FakePlayingBody(PlayingKit kit)
    {
        Kit = kit;
        View = new SimulationBotWorldView(PlayingTestWorld.CharacterName);
        View.ApplyJoin(PlayingTestWorld.Join());
    }

    public int BotIndex => 1;

    public SimulationBotWorldView View { get; }

    public PlayingKit Kit { get; }

    public bool IsReady { get; set; } = true;

    public bool HasPendingIntent => Pending is not null;

    public List<RealtimeCombatEvent> Events { get; } = [];

    public IReadOnlyList<RealtimeCombatEvent> RecentCombatEvents => Events;

    public PlayingMoveState MoveState { get; set; } = PlayingMoveState.Standing;

    public bool RouteReachedDestination { get; set; } = true;

    public SimulationVector3 Position => new(View.OwnPosition.X, View.OwnPosition.Y, View.OwnPosition.Z);

    public string? Pending { get; private set; }

    public List<string> Sent { get; } = [];

    public List<SimulationVector3> Walks { get; } = [];

    public SimulationVector3? Teleported { get; private set; }

    public ulong AttackedTarget { get; private set; }

    public Guid OpenedCorpse { get; private set; }

    public Dictionary<PlayingCounter, long> Counters { get; } = [];

    public PlayingIntentOutcome? Outcome { get; set; }

    public PlayingIntentOutcome? TakeOutcome()
    {
        var taken = Outcome;
        Outcome = null;
        return taken;
    }

    // Answers the pending intent.
    public void Answer(bool succeeded = true, string? code = null)
    {
        Assert.NotNull(Pending);
        Outcome = new PlayingIntentOutcome(
            Pending!,
            succeeded,
            code,
            !succeeded && PlayingExpectedConflicts.IsExpected(Pending!, code));
        Pending = null;
    }

    public void MoveTo(float x, float z)
    {
        View.ApplyOwnerSnapshot(PlayingTestWorld.OwnerAt(++ownerSequence, x, z));
    }

    public bool TryFindGround(float x, float z, out SimulationVector3 ground)
    {
        ground = new SimulationVector3(x, 0f, z);
        return Math.Abs(x) < 190f && Math.Abs(z) < 190f;
    }

    public bool Equip(SimulationBotOwnedItem item, string equipmentSlotId) => Send(PlayingOperations.Equip);

    public bool Teleport(SimulationVector3 destination)
    {
        Teleported = destination;
        return Send(PlayingOperations.Teleport);
    }

    public bool Attack(ulong targetEntityId)
    {
        AttackedTarget = targetEntityId;
        return Send(PlayingOperations.Attack);
    }

    public bool ClearTarget() => Send(PlayingOperations.ClearTarget);

    public bool OpenCorpse(Guid corpseId)
    {
        OpenedCorpse = corpseId;
        return Send(PlayingOperations.LootOpen);
    }

    public bool LootAll(Guid corpseId, long expectedRevision) => Send(PlayingOperations.LootAll);

    public bool CloseCorpse(Guid corpseId, long expectedRevision) => Send(PlayingOperations.LootClose);

    public void WalkTo(SimulationVector3 destination)
    {
        Walks.Add(destination);
        MoveState = PlayingMoveState.Walking;
    }

    public void Stand()
    {
        if (MoveState is PlayingMoveState.Walking or PlayingMoveState.Planning)
        {
            MoveState = PlayingMoveState.Standing;
        }
    }

    public void Count(PlayingCounter counter, long amount = 1)
    {
        Counters[counter] = Counters.GetValueOrDefault(counter) + amount;
    }

    private bool Send(string operation)
    {
        if (Pending is not null)
        {
            return false;
        }

        Pending = operation;
        Sent.Add(operation);
        return true;
    }
}
