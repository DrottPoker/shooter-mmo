using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;
using Mmorpg.Tools.StackStressGenerator;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;

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
            items)
        {
            Currency = 100,
            CarriedWeight = 200,
            CarryCapacity = 2000
        };
    }

    public const string Vendor = "vendor.test_store";
    public const string Arrow = "ammunition.bronze_arrow";
    public const string Pelt = "material.wolf_pelt";
    public const string Ore = "material.iron_ore";
    public const string Pickaxe = "tool.starter_pickaxe";
    public const string IronVein = "resource.iron_vein";
    public const string VendorNpc = "npc.test_services";

    // A small item and actor catalog of the test's own, so content changes never break the
    // role tests: a vendor that sells arrows at their value and buys materials at 40 percent,
    // an iron vein that needs a pickaxe, and aggressive feral and defensive grey wolves.
    public static PlayingGameData GameData { get; } = new(
        new ItemCatalogRuntimeDocument
        {
            Definitions =
            [
                new ItemDefinition
                {
                    Id = "weapon.iron_sword",
                    Category = "weapon",
                    Value = 120,
                    UnitWeight = 100,
                    MaximumStackSize = 1,
                    EquipmentSlots = ["main_hand"],
                    ToolKinds = [],
                    Weapon = new ItemWeaponEntry { Range = 2.5f }
                },
                new ItemDefinition
                {
                    Id = "weapon.training_shortbow",
                    Category = "weapon",
                    Value = 100,
                    UnitWeight = 80,
                    MaximumStackSize = 1,
                    EquipmentSlots = ["main_hand"],
                    ToolKinds = [],
                    Weapon = new ItemWeaponEntry { Range = 10f }
                },
                new ItemDefinition
                {
                    Id = Arrow,
                    Category = "ammunition",
                    Value = 1,
                    UnitWeight = 1,
                    MaximumStackSize = 1000,
                    EquipmentSlots = ["ammunition"],
                    ToolKinds = [],
                    Ammunition = new ItemAmmunitionEntry { AmmunitionFamily = "arrow" }
                },
                new ItemDefinition
                {
                    Id = Pelt,
                    Category = "material",
                    Value = 15,
                    UnitWeight = 20,
                    MaximumStackSize = 50,
                    EquipmentSlots = [],
                    ToolKinds = []
                },
                new ItemDefinition
                {
                    Id = Ore,
                    Category = "material",
                    Value = 10,
                    UnitWeight = 60,
                    MaximumStackSize = 100,
                    EquipmentSlots = [],
                    ToolKinds = []
                },
                new ItemDefinition
                {
                    Id = Pickaxe,
                    Category = "tool",
                    Value = 50,
                    UnitWeight = 150,
                    MaximumStackSize = 1,
                    EquipmentSlots = ["tool"],
                    ToolKinds = ["pickaxe"]
                }
            ],
            Vendors =
            [
                new VendorProfileDefinition
                {
                    Id = Vendor,
                    Offers = [new VendorOfferEntry { DefinitionId = Arrow }],
                    PurchasePriceBasisPoints = 10000,
                    SalePriceBasisPoints = 4000,
                    SaleCategories = ["material", "ammunition"]
                }
            ]
        },
        new WorldActorCatalogRuntimeDocument
        {
            Actors =
            [
                new WorldActorDefinition
                {
                    Id = "mob.feral_wolf",
                    Kind = "mob",
                    BrainProfileId = "mob.aggressive",
                    InteractionBounds = new WorldActorBoundsDefinition { CenterY = 0.55f, SizeX = 1.2f, SizeY = 1.1f, SizeZ = 1.8f },
                    Capabilities = []
                },
                new WorldActorDefinition
                {
                    Id = "mob.grey_wolf",
                    Kind = "mob",
                    BrainProfileId = "mob.defensive",
                    InteractionBounds = new WorldActorBoundsDefinition { CenterY = 0.55f, SizeX = 1.2f, SizeY = 1.1f, SizeZ = 1.8f },
                    Capabilities = []
                },
                new WorldActorDefinition
                {
                    Id = IronVein,
                    Kind = "resource_node",
                    InteractionBounds = new WorldActorBoundsDefinition { CenterY = 0.6f, SizeX = 1.6f, SizeY = 1.2f, SizeZ = 1.6f },
                    Capabilities =
                    [
                        new WorldActorCapabilityDefinition { Id = "resource.mine", Kind = "gathering", ProfileId = "resource.iron_vein" }
                    ]
                },
                new WorldActorDefinition
                {
                    Id = VendorNpc,
                    Kind = "npc",
                    InteractionBounds = new WorldActorBoundsDefinition { CenterY = 1f, SizeX = 0.8f, SizeY = 2f, SizeZ = 0.8f },
                    Capabilities =
                    [
                        new WorldActorCapabilityDefinition { Id = "services.vendor", Kind = "vendor", ProfileId = Vendor }
                    ]
                }
            ],
            ResourceProfiles =
            [
                new WorldActorResourceProfileDefinition { Id = "resource.iron_vein", RequiredToolKind = "pickaxe" }
            ],
            BrainProfiles =
            [
                new WorldActorBrainProfileDefinition { Id = "mob.aggressive", Aggression = "aggressive", DetectionRange = 8f },
                new WorldActorBrainProfileDefinition { Id = "mob.defensive", Aggression = "defensive", DetectionRange = 0f }
            ]
        });

    public static RealtimeWorldActorSpawn VendorAt(ulong entityId, float x, float z)
    {
        return new RealtimeWorldActorSpawn(
            entityId,
            Guid.NewGuid(),
            VendorNpc,
            "development_world_2.vendor",
            RealtimeWorldActorKind.Npc,
            "city",
            RealtimeWorldActorDisposition.Friendly,
            1,
            RealtimeWorldActorAggression.None,
            "npc.citizen",
            1,
            1,
            true,
            x,
            0f,
            z,
            0f,
            RealtimeWorldActorActivityTier.Active,
            0f,
            1f,
            0f,
            0.8f,
            2f,
            0.8f,
            [new RealtimeWorldActorMenuOption("services.vendor", RealtimeWorldActorCapabilityKind.Vendor)]);
    }

    public static RealtimeWorldActorSpawn NodeAt(ulong entityId, float x, float z, bool active = true)
    {
        return new RealtimeWorldActorSpawn(
            entityId,
            Guid.NewGuid(),
            IronVein,
            "development_world_2.load_field.node" + entityId,
            RealtimeWorldActorKind.ResourceNode,
            "neutral",
            RealtimeWorldActorDisposition.Neutral,
            1,
            RealtimeWorldActorAggression.None,
            "resource.iron_vein",
            1,
            1,
            active,
            x,
            0f,
            z,
            0f,
            RealtimeWorldActorActivityTier.Active,
            0f,
            0.6f,
            0f,
            1.6f,
            1.2f,
            1.6f,
            [new RealtimeWorldActorMenuOption("resource.mine", RealtimeWorldActorCapabilityKind.Gathering)]);
    }

    public static RealtimeWorldActorState ActorState(ulong entityId, long revision, float x, float z, bool active)
    {
        return new RealtimeWorldActorState(entityId, revision, revision, active, x, 0f, z, 0f, RealtimeWorldActorActivityTier.Active);
    }

    // The worker's answer to an Open of a vendor or a node.
    public static RealtimeWorldInteractionOpened Opened(
        RealtimeWorldActorSpawn target,
        RealtimeWorldActorCapabilityKind kind,
        string capabilityId,
        string profileId)
    {
        return new RealtimeWorldInteractionOpened(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RealtimeWorldInteractionTargetKind.WorldActor,
            target.EntityId,
            target.RuntimeActorId,
            target.InteractionRevision,
            1,
            [new RealtimeWorldActorCapability(capabilityId, kind, profileId, true, 1)]);
    }

    public static RealtimeWorldInteractionClosed Closed(RealtimeWorldInteractionOpened opened, string code)
    {
        return new RealtimeWorldInteractionClosed(
            opened.InteractionSessionId,
            opened.TargetKind,
            opened.TargetEntityId,
            opened.TargetRuntimeId,
            code,
            "closed");
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

    public static PlayingKit BowKit { get; } =
        new("bow", [new PlayingKitItem("weapon.training_shortbow", "main_hand"), new PlayingKitItem(Arrow, "ammunition")], 10f);

    public static PlayingKit PickaxeKit { get; } =
        new("pickaxe", [new PlayingKitItem(Pickaxe, "tool")], 0f);
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

    public PlayingGameData GameData => PlayingTestWorld.GameData;

    public bool IsReady { get; set; } = true;

    // Cover between every stand point and every target, when set.
    public bool SightBlocked { get; set; }

    public SimulationBotWorldActor? OpenedActor { get; private set; }

    public PlayingCapabilityAction? LastAction { get; private set; }

    public (SimulationBotOwnedItem Item, Guid ContainerId, int Slot)? LastMove { get; private set; }

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

    public bool OpenInteraction(SimulationBotWorldActor actor, string operation)
    {
        OpenedActor = actor;
        return Send(operation);
    }

    public bool CapabilityAction(string operation, PlayingCapabilityAction action)
    {
        if (View.OpenInteraction is null)
        {
            return false;
        }

        LastAction = action;
        return Send(operation);
    }

    public bool CloseInteraction(string operation)
    {
        return View.OpenInteraction is not null && Send(operation);
    }

    public bool MoveItem(SimulationBotOwnedItem item, Guid containerId, int slotIndex)
    {
        LastMove = (item, containerId, slotIndex);
        return Send(PlayingOperations.ItemMove);
    }

    public bool HasLineOfSight(SimulationVector3 standPoint, SimulationVector3 target) => !SightBlocked;

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
