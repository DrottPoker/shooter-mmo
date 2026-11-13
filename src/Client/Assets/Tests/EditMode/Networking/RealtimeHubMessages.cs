using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Tests.EditMode
{
    // Valid server messages for the hub tests, built from the generated protocol models. The ids
    // are fixed so a test reads which entity a message names.
    internal static class RealtimeHubMessages
    {
        public const ulong ControlledEntityId = 7;
        public const ulong PlayerEntityId = 11;
        public const ulong OtherPlayerEntityId = 12;
        public const ulong ActorEntityId = 21;
        public const ulong OtherActorEntityId = 22;
        public const string ShardId = "local-shard-1";
        public const string WorldId = "development-world-1";
        public const string CharacterName = "Hub Hero";
        public const long CorpseExpiresAt = 1_760_000_000_000;

        public static readonly Guid SessionId = G(1001);
        public static readonly Guid AccountId = G(1002);
        public static readonly Guid CharacterId = G(1003);
        public static readonly Guid ActorRuntimeId = G(2101);
        public static readonly Guid OtherActorRuntimeId = G(2201);

        public static Guid G(int seed)
        {
            return new Guid("00000000-0000-4000-8000-" + seed.ToString("D12"));
        }

        public static RealtimeJoinAccepted JoinAccepted(
            string simulationRevision,
            string collisionRevision,
            bool isReconnect = false,
            string characterId = null,
            string simulationSessionId = null,
            bool playerVersusPlayer = false)
        {
            return new RealtimeJoinAccepted(
                simulationSessionId ?? SessionId.ToString("D"),
                AccountId.ToString("D"),
                characterId ?? CharacterId.ToString("D"),
                CharacterName,
                ShardId,
                WorldId,
                ControlledEntityId,
                simulationRevision,
                collisionRevision,
                "2026-11-13T10:00:00.0000000Z",
                "2026-11-13T22:00:00.0000000Z",
                isReconnect,
                Carry(0, 0),
                new RealtimeMovementSettings(
                    30,
                    15,
                    5f,
                    720f,
                    -24f,
                    55f,
                    -2f,
                    0f,
                    -14f,
                    14f,
                    -14f,
                    14f,
                    0.35f,
                    2f,
                    0.35f,
                    45f,
                    0.4f,
                    0.1f,
                    6),
                PlayerState(),
                new RealtimeShardRules(playerVersusPlayer));
        }

        public static RealtimePlayerState PlayerState()
        {
            return new RealtimePlayerState(0f, 0f, -1f, 0f, -2f, 0f, 0f, true);
        }

        public static RealtimeCarryState Carry(long itemStateRevision, long carriedWeight = 40)
        {
            return new RealtimeCarryState(itemStateRevision, carriedWeight, 200);
        }

        public static RealtimeCharacterAppearance Appearance(string bodyModel = "Human_Female")
        {
            return new RealtimeCharacterAppearance(
                bodyModel,
                new[] { new RealtimeAppearanceChoice("face", string.Empty, "Freckles") },
                new[] { new RealtimeVisibleEquipment("head", "clothing.blue_wool_hood") },
                false,
                null);
        }

        public static RealtimeEntitySpawn EntitySpawn(
            ulong entityId,
            string persistentId = "8c3d2e1f-0b4a-4c5d-9e6f-a1b2c3d4e5f6",
            string archetypeId = "player-default",
            string bodyModel = "Human_Female")
        {
            return new RealtimeEntitySpawn(
                entityId,
                RealtimeEntityKind.Player,
                persistentId,
                "Ödla",
                archetypeId,
                1200,
                PlayerState(),
                Appearance(bodyModel));
        }

        public static RealtimeEntityDespawn EntityDespawn(ulong entityId, string reason = "out_of_interest")
        {
            return new RealtimeEntityDespawn(entityId, reason);
        }

        public static RealtimeEntityAppearanceChanged AppearanceChanged(ulong entityId, string bodyModel = "Human_Male")
        {
            return new RealtimeEntityAppearanceChanged(entityId, Appearance(bodyModel));
        }

        public static RealtimeWorldActorSpawn WorldActorSpawn(
            ulong entityId = ActorEntityId,
            Guid? runtimeActorId = null,
            long stateRevision = 3,
            ushort level = 0)
        {
            return new RealtimeWorldActorSpawn(
                entityId,
                runtimeActorId ?? ActorRuntimeId,
                "npc.blacksmith",
                "spawn.blacksmith.1",
                RealtimeWorldActorKind.Npc,
                "faction.town",
                RealtimeWorldActorDisposition.Friendly,
                level,
                RealtimeWorldActorAggression.None,
                "presentation.blacksmith",
                stateRevision,
                4,
                true,
                10.5f,
                0.25f,
                -20.75f,
                90f,
                RealtimeWorldActorActivityTier.EventDriven,
                10.5f,
                1f,
                -20.75f,
                0.8f,
                2f,
                0.75f,
                new[] { new RealtimeWorldActorMenuOption("smith.talk", RealtimeWorldActorCapabilityKind.Dialogue) });
        }

        public static RealtimeWorldActorState WorldActorState(ulong entityId = ActorEntityId, long stateRevision = 4)
        {
            return new RealtimeWorldActorState(
                entityId,
                stateRevision,
                5,
                true,
                11f,
                0.25f,
                -20f,
                45f,
                RealtimeWorldActorActivityTier.Active);
        }

        public static RealtimeWorldActorDespawn WorldActorDespawn(
            ulong entityId = ActorEntityId,
            Guid? runtimeActorId = null,
            string reason = "despawned")
        {
            return new RealtimeWorldActorDespawn(entityId, runtimeActorId ?? ActorRuntimeId, reason);
        }

        public static RealtimeItemOperationResult ItemOperationResult(RealtimeCarryState carryState)
        {
            return new RealtimeItemOperationResult(
                G(1),
                RealtimeItemOperationKind.Relocate,
                true,
                false,
                carryState,
                null,
                new[] { new RealtimeItemRevision(G(2), 7) },
                new[] { new RealtimeContainerRevision(G(3), 9) },
                Array.Empty<Guid>());
        }

        public static RealtimeCorpseInteractionResult CorpseInteractionResult(RealtimeCarryState carryState)
        {
            return new RealtimeCorpseInteractionResult(
                G(1),
                RealtimeCorpseInteractionKind.LootItem,
                G(10),
                true,
                false,
                true,
                carryState,
                null);
        }

        public static RealtimeOwnerItemQuantityChanged OwnerItemQuantityChanged(
            long previousItemStateRevision,
            RealtimeCarryState carryState)
        {
            return new RealtimeOwnerItemQuantityChanged(
                RealtimeItemQuantityChangeReason.AmmunitionConsumed,
                previousItemStateRevision,
                carryState,
                G(5),
                995,
                41);
        }

        // One transaction advances the item-state revision by exactly one.
        public static RealtimeOwnerItemStateChanged OwnerItemStateChanged(long previousItemStateRevision)
        {
            return new RealtimeOwnerItemStateChanged(
                G(6),
                previousItemStateRevision,
                Carry(previousItemStateRevision + 1),
                250,
                new[] { new RealtimeContainerRevision(G(3), 9) },
                new[]
                {
                    new RealtimeItemStateChange(
                        G(2),
                        RealtimeItemStateLocationKind.Container,
                        "potion",
                        1,
                        7,
                        G(3),
                        4,
                        string.Empty,
                        Array.Empty<RealtimeItemPolicySummary>(),
                        Guid.Empty,
                        0)
                },
                Array.Empty<Guid>());
        }

        public static RealtimeCorpsePresenceSnapshotChunk CorpsePresence()
        {
            return new RealtimeCorpsePresenceSnapshotChunk(
                1,
                0,
                1,
                new[]
                {
                    new RealtimeCorpsePresence(
                        G(10),
                        5.5f,
                        0f,
                        -7.25f,
                        123.5f,
                        CorpseExpiresAt,
                        false,
                        true,
                        RealtimeCorpseSource.Character("Ödla"),
                        "corpse.player",
                        null)
                });
        }

        public static RealtimeCorpseViewStateChunk CorpseViewState()
        {
            return new RealtimeCorpseViewStateChunk(
                G(11),
                RealtimeCorpseViewUpdateKind.Snapshot,
                G(10),
                -1,
                4,
                CorpseExpiresAt,
                RealtimeCorpseSource.Character("Ödla"),
                "corpse.player",
                0,
                1,
                "general_inventory",
                G(12),
                6,
                true,
                20,
                0,
                new[]
                {
                    new RealtimeCorpseSlot(
                        0,
                        "general",
                        new[] { "consumable" },
                        string.Empty,
                        new RealtimeCorpseItem(G(2), "item.bandage", 3, 6, Guid.Empty, 0)),
                    new RealtimeCorpseSlot(1, "general", Array.Empty<string>(), string.Empty, null)
                });
        }

        public static RealtimeCorpseViewClosed CorpseViewClosed()
        {
            return new RealtimeCorpseViewClosed(G(10), "corpse_expired", "The corpse decayed.");
        }

        public static RealtimeWorldInteractionOpened InteractionOpened()
        {
            return new RealtimeWorldInteractionOpened(
                G(7),
                G(9),
                RealtimeWorldInteractionTargetKind.WorldActor,
                ActorEntityId,
                ActorRuntimeId,
                4,
                5,
                new[]
                {
                    new RealtimeWorldActorCapability(
                        "smith.talk",
                        RealtimeWorldActorCapabilityKind.Dialogue,
                        string.Empty,
                        true,
                        2)
                });
        }

        public static RealtimeWorldInteractionResult InteractionResult()
        {
            return new RealtimeWorldInteractionResult(
                G(7),
                G(9),
                RealtimeWorldInteractionOperationKind.Close,
                true,
                4,
                0,
                string.Empty,
                null,
                null);
        }

        public static RealtimeWorldInteractionClosed InteractionClosed()
        {
            return new RealtimeWorldInteractionClosed(
                G(9),
                RealtimeWorldInteractionTargetKind.WorldActor,
                ActorEntityId,
                ActorRuntimeId,
                "target_despawned",
                "The target is gone.");
        }

        public static RealtimeGatheringState GatheringStarted()
        {
            return RealtimeGatheringState.CreateStarted(G(9), ActorEntityId, ActorRuntimeId, 3000);
        }

        public static RealtimeCombatIntentResult CombatIntentResult()
        {
            return new RealtimeCombatIntentResult(
                G(1),
                RealtimeCombatIntentKind.AttackTarget,
                true,
                ActorEntityId,
                null);
        }

        public static RealtimeCombatantState CombatantState(ulong entityId, long stateRevision = 1)
        {
            return new RealtimeCombatantState(
                entityId,
                stateRevision,
                37,
                50,
                RealtimeCombatLifeState.Alive,
                true,
                entityId == ActorEntityId ? ControlledEntityId : ActorEntityId,
                0);
        }

        public static RealtimeCombatEventBatch CombatEvents()
        {
            return new RealtimeCombatEventBatch(
                1300,
                new[]
                {
                    new RealtimeCombatEvent(
                        RealtimeCombatEventKind.Hit,
                        ControlledEntityId,
                        ActorEntityId,
                        0,
                        17,
                        RealtimeCombatDamageType.Fire,
                        33,
                        RealtimeCombatAttackStyle.Melee)
                });
        }

        public static RealtimeOwnerCombatState OwnerCombatState()
        {
            return new RealtimeOwnerCombatState(
                11,
                1300,
                64,
                100,
                40,
                100,
                RealtimeCombatLifeState.Alive,
                2,
                0,
                true,
                ActorEntityId,
                RealtimeCombatAttackStatus.Attacking,
                1340,
                1320,
                G(5),
                997);
        }

        public static RealtimeAppearanceIntentResult AppearanceIntentResult()
        {
            return new RealtimeAppearanceIntentResult(G(1), RealtimeAppearanceIntentKind.LowerHeadwear, true, null);
        }

        public static RealtimeAdminIntentResult AdminIntentResult()
        {
            return new RealtimeAdminIntentResult(G(1), RealtimeAdminIntentKind.Teleport, true, null);
        }

        public static RealtimeAdminStateChanged AdminState(ushort speedPercent = 200, bool invulnerable = true)
        {
            return new RealtimeAdminStateChanged(speedPercent, invulnerable);
        }

        public static RealtimeQuestIntentResult QuestIntentResult()
        {
            return new RealtimeQuestIntentResult(G(1), RealtimeQuestIntentKind.Abandon, true, 42, null);
        }

        public static RealtimeQuestLogChanged QuestLogChanged()
        {
            return new RealtimeQuestLogChanged(
                G(1),
                3,
                4,
                false,
                new[]
                {
                    new RealtimeQuestChange(
                        "quest.wolves_at_the_gate",
                        "hunt",
                        new[] { new RealtimeQuestObjectiveProgress("wolves", 1) },
                        0)
                });
        }

        public static RealtimeQuestDropInCorpse QuestDrop()
        {
            return new RealtimeQuestDropInCorpse(
                G(10),
                "mob.feral_wolf",
                "quest.pelts_and_fangs",
                "quest_item.wolf_fang",
                2);
        }

        public static RealtimeOwnerSimulationSnapshot OwnerSnapshot(uint snapshotSequence, uint serverTick)
        {
            return new RealtimeOwnerSimulationSnapshot(
                snapshotSequence,
                serverTick,
                0,
                PlayerState(),
                new RealtimeMovementPathProgress(0, 0, 0));
        }

        public static RealtimeSimulationSnapshot RemoteSnapshot(uint snapshotSequence, uint serverTick)
        {
            return new RealtimeSimulationSnapshot(
                snapshotSequence,
                serverTick,
                0,
                1,
                new[] { new RealtimeEntitySnapshot(PlayerEntityId, 3, PlayerState()) });
        }

        // Client intents, which the hub sends and a server never sends back.
        public static RealtimeItemOperationIntent ItemIntent()
        {
            return RealtimeItemOperationIntent.CreateRelocate(G(1), 5, G(2), 6, G(3));
        }

        // A merge of an item with itself, which the encoder refuses.
        public static RealtimeItemOperationIntent UnencodableItemIntent()
        {
            return RealtimeItemOperationIntent.CreateMergeStacks(G(1), 5, G(2), 6, G(2), 0);
        }

        public static RealtimeCorpseInteractionIntent CorpseIntent()
        {
            return RealtimeCorpseInteractionIntent.CreateOpen(G(1), G(10));
        }

        public static RealtimeWorldInteractionIntent WorldIntent()
        {
            return RealtimeWorldInteractionIntent.CreateOpen(
                G(7),
                SessionId,
                RealtimeWorldInteractionTargetKind.WorldActor,
                ActorEntityId,
                ActorRuntimeId,
                4);
        }

        public static RealtimeAdminIntent AdminIntent()
        {
            return RealtimeAdminIntent.CreateRestoreHealth(G(1));
        }

        public static RealtimeAppearanceIntent AppearanceIntent()
        {
            return RealtimeAppearanceIntent.CreateLowerHeadwear(G(1));
        }

        public static RealtimeQuestIntent QuestIntent()
        {
            return RealtimeQuestIntent.CreateAbandon(G(1), "quest.wolves_at_the_gate");
        }

        public static RealtimeCombatIntent CombatIntent()
        {
            return RealtimeCombatIntent.CreateAttackTarget(G(1), ActorEntityId);
        }

        public static RealtimeMovementInputBatch MovementInputs()
        {
            return new RealtimeMovementInputBatch(
                new[] { new RealtimeMovementInput(1, 10, 3) },
                new RealtimeMovementPath(3, new[] { new RealtimeMovementPathWaypoint(1f, 0f, 2f) }));
        }
    }
}
