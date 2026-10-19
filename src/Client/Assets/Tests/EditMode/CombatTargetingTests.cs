using System;
using System.Collections.Generic;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // Combat click priority and classification, the PvP rule, dead-state click blocking,
    // target selection, and the approach to weapon range.
    public sealed class CombatTargetingTests
    {
        private const ulong MobId = 11;
        private const ulong NpcId = 12;
        private const ulong PlayerId = 21;

        [Test]
        public void CombatSeesEveryClickBeforeWorldInteractionWhateverRegisteredFirst()
        {
            var calls = new List<string>();
            var handlers = new WorldClickHandlerList();
            var interaction = new RecordingHandler("interaction", calls, false);
            var combat = new RecordingHandler("combat", calls, false);
            var otherInteraction = new RecordingHandler("other", calls, false);

            handlers.Add(interaction, WorldClickOrder.WorldInteraction);
            handlers.Add(combat, WorldClickOrder.Combat);
            handlers.Add(otherInteraction, WorldClickOrder.WorldInteraction);

            Assert.That(handlers.TryHandle(default(Ray)), Is.False);
            Assert.That(calls, Is.EqualTo(new[] { "combat", "interaction", "other" }));

            // The first handler that takes the click ends it.
            calls.Clear();
            combat.Handles = true;
            Assert.That(handlers.TryHandle(default(Ray)), Is.True);
            Assert.That(calls, Is.EqualTo(new[] { "combat" }));

            // Adding a handler again moves it, and a removed handler sees nothing.
            handlers.Add(combat, WorldClickOrder.WorldInteraction + 1);
            Assert.That(handlers.Count, Is.EqualTo(3));
            Assert.That(handlers.HandlerAt(2), Is.SameAs(combat));
            Assert.That(handlers.Remove(interaction), Is.True);
            Assert.That(handlers.Remove(interaction), Is.False);
            calls.Clear();
            combat.Handles = false;
            handlers.TryHandle(default(Ray));
            Assert.That(calls, Is.EqualTo(new[] { "other", "combat" }));
        }

        [Test]
        public void DeadCharacterIgnoresEveryClick()
        {
            var dead = Owner(2, RealtimeCombatLifeState.Dead);
            var targets = new[]
            {
                new CombatClickTarget(CombatClickTargetKind.None, 0),
                new CombatClickTarget(CombatClickTargetKind.Other, 0),
                new CombatClickTarget(CombatClickTargetKind.Mob, MobId),
                new CombatClickTarget(CombatClickTargetKind.Player, PlayerId)
            };

            foreach (var target in targets)
            {
                Assert.That(
                    CombatClickRules.Classify(target, dead, true, Combatant(target.EntityId)),
                    Is.EqualTo(CombatClickAction.Ignore),
                    target.Kind.ToString());
            }

            Assert.That(CombatClickRules.IsDead(dead), Is.True);
            Assert.That(CombatClickRules.IsDead(Owner(3, RealtimeCombatLifeState.Alive)), Is.False);
            Assert.That(CombatClickRules.IsDead(null), Is.False);
        }

        [Test]
        public void LivingMobIsAttackedAndAMobThatCannotBeAttackedTakesTheClick()
        {
            var owner = Owner(1, RealtimeCombatLifeState.Alive);
            var mob = new CombatClickTarget(CombatClickTargetKind.Mob, MobId);

            Assert.That(
                CombatClickRules.Classify(mob, owner, false, Combatant(MobId)),
                Is.EqualTo(CombatClickAction.Attack));
            Assert.That(
                CombatClickRules.Classify(mob, null, false, Combatant(MobId)),
                Is.EqualTo(CombatClickAction.Attack));
            Assert.That(
                CombatClickRules.Classify(mob, owner, false, Combatant(MobId, RealtimeCombatLifeState.Evading)),
                Is.EqualTo(CombatClickAction.Ignore));
            Assert.That(
                CombatClickRules.Classify(mob, owner, false, Combatant(MobId, RealtimeCombatLifeState.Dead)),
                Is.EqualTo(CombatClickAction.Ignore));
            Assert.That(
                CombatClickRules.Classify(mob, owner, false, null),
                Is.EqualTo(CombatClickAction.Ignore));

            // The state of another combatant says nothing about this one.
            Assert.That(
                CombatClickRules.Classify(mob, owner, false, Combatant(MobId + 1)),
                Is.EqualTo(CombatClickAction.Ignore));
        }

        [Test]
        public void PlayerIsAttackedOnlyWhenTheShardAllowsPlayerVersusPlayer()
        {
            var owner = Owner(1, RealtimeCombatLifeState.Alive);
            var player = new CombatClickTarget(CombatClickTargetKind.Player, PlayerId);

            Assert.That(
                CombatClickRules.Classify(player, owner, true, Combatant(PlayerId)),
                Is.EqualTo(CombatClickAction.Attack));
            Assert.That(
                CombatClickRules.Classify(player, owner, false, Combatant(PlayerId)),
                Is.EqualTo(CombatClickAction.Pass));
            Assert.That(
                CombatClickRules.Classify(player, owner, true, Combatant(PlayerId, RealtimeCombatLifeState.Dead)),
                Is.EqualTo(CombatClickAction.Pass));

            // A player without combat state, such as a development bot, is no target.
            Assert.That(
                CombatClickRules.Classify(player, owner, true, null),
                Is.EqualTo(CombatClickAction.Pass));
        }

        [Test]
        public void GroundAndOtherTargetsPassTheClickOn()
        {
            var owner = Owner(1, RealtimeCombatLifeState.Alive);

            Assert.That(
                CombatClickRules.Classify(new CombatClickTarget(CombatClickTargetKind.None, 0), owner, true, null),
                Is.EqualTo(CombatClickAction.Pass));
            Assert.That(
                CombatClickRules.Classify(new CombatClickTarget(CombatClickTargetKind.Other, NpcId), owner, true, Combatant(0)),
                Is.EqualTo(CombatClickAction.Pass));
            Assert.That(new CombatClickTarget(CombatClickTargetKind.Other, NpcId).EntityId, Is.Zero);
        }

        [Test]
        public void ClickTargetsAreDescribedByTheirAuthoritativeKind()
        {
            var actors = new WorldActorClientState();
            Assert.That(actors.TryApplySpawn(Spawn(MobId, RealtimeWorldActorKind.Mob), out var error), Is.True, error);
            Assert.That(actors.TryApplySpawn(Spawn(NpcId, RealtimeWorldActorKind.Npc), out error), Is.True, error);
            Assert.That(actors.TryGet(MobId, out var mobEntry), Is.True);
            Assert.That(actors.TryGet(NpcId, out var npcEntry), Is.True);

            var mobObject = new GameObject("Mob");
            var npcObject = new GameObject("Npc");
            var corpseObject = new GameObject("Corpse");
            var playerObject = new GameObject("Player");
            try
            {
                var mob = mobObject.AddComponent<MobView>();
                mob.Initialize(mobEntry);
                var npc = npcObject.AddComponent<NpcView>();
                npc.Initialize(npcEntry);
                var corpse = corpseObject.AddComponent<CorpseInteractionTarget>();
                corpse.Apply(new Mmorpg.Client.Items.CorpsePresenceEntry(new RealtimeCorpsePresence(
                    Guid.NewGuid(),
                    1f,
                    0f,
                    2f,
                    2_000_000_000_000L,
                    false,
                    "Feral Wolf",
                    "corpse.remains")));
                var player = playerObject.AddComponent<RemotePlayerView>();
                player.Initialize(PlayerId, "character", "Ada", RealtimePlayerArchetypeIds.Player, 30, 3, null, CollisionSettings());

                AssertDescribed(new WorldPointerCandidate(mob, 4f), actors, CombatClickTargetKind.Mob, MobId);
                AssertDescribed(new WorldPointerCandidate(npc, 4f), actors, CombatClickTargetKind.Other, 0);
                AssertDescribed(new WorldPointerCandidate(corpse, 4f), actors, CombatClickTargetKind.Other, 0);
                AssertDescribed(new WorldPointerCandidate(player, 4f), actors, CombatClickTargetKind.Player, PlayerId);
                AssertDescribed(default(WorldPointerCandidate), actors, CombatClickTargetKind.None, 0);

                // Without the authoritative actor kind a view is no Mob.
                AssertDescribed(new WorldPointerCandidate(mob, 4f), null, CombatClickTargetKind.Other, 0);

                // An inactive Mob, such as a dead one, and a hidden player are nothing to click.
                Assert.That(
                    actors.TryApplyState(
                        new RealtimeWorldActorState(
                            MobId,
                            2,
                            1,
                            false,
                            2.5f,
                            0f,
                            -1f,
                            270f,
                            RealtimeWorldActorActivityTier.EventDriven),
                        out error),
                    Is.True,
                    error);
                mob.Apply(mobEntry);
                AssertDescribed(new WorldPointerCandidate(mob, 4f), actors, CombatClickTargetKind.None, 0);
                playerObject.SetActive(false);
                AssertDescribed(new WorldPointerCandidate(player, 4f), actors, CombatClickTargetKind.None, 0);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mobObject);
                UnityEngine.Object.DestroyImmediate(npcObject);
                UnityEngine.Object.DestroyImmediate(corpseObject);
                UnityEngine.Object.DestroyImmediate(playerObject);
            }
        }

        [Test]
        public void RemotePlayersGetATriggerPickedAsThePlayer()
        {
            var playerObject = new GameObject("Player");
            try
            {
                var player = playerObject.AddComponent<RemotePlayerView>();
                player.Initialize(PlayerId, "character", "Ada", RealtimePlayerArchetypeIds.Player, 30, 3, null, CollisionSettings());

                var capsule = playerObject.GetComponentInChildren<CapsuleCollider>();
                Assert.That(capsule, Is.Not.Null);
                Assert.That(capsule.isTrigger, Is.True);
                Assert.That(capsule.height, Is.EqualTo(1.8f));
                Assert.That(capsule.radius, Is.EqualTo(0.4f));
                Assert.That(player.TargetHeight, Is.EqualTo(1.8f));
                var candidate = WorldPointerPicker.FindCandidate(capsule, 3f);
                Assert.That(candidate.Player, Is.SameAs(player));
                Assert.That(candidate.Target, Is.Null);
                Assert.That(candidate.Distance, Is.EqualTo(3f));

                // Initializing again keeps one trigger.
                player.Initialize(PlayerId, "character", "Ada", RealtimePlayerArchetypeIds.Player, 30, 3, null, CollisionSettings());
                Assert.That(playerObject.GetComponentsInChildren<Collider>().Length, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(playerObject);
            }
        }

        [Test]
        public void TheNearestPointerCandidateWinsWhateverItsKind()
        {
            var playerObject = new GameObject("Player");
            try
            {
                var player = playerObject.AddComponent<RemotePlayerView>();
                var npc = new FakeTarget(true);
                var inactive = new FakeTarget(false);

                var selected = WorldPointerPicker.SelectCandidate(
                    new[]
                    {
                        new WorldPointerCandidate(npc, 6f),
                        new WorldPointerCandidate(player, 5f),
                        new WorldPointerCandidate(inactive, 1f)
                    },
                    Array.Empty<WorldPointerCandidate>());
                Assert.That(selected.Player, Is.SameAs(player));

                selected = WorldPointerPicker.SelectCandidate(
                    new[] { new WorldPointerCandidate(inactive, 1f) },
                    new[] { new WorldPointerCandidate(npc, 2f) });
                Assert.That(selected.Target, Is.SameAs(npc));
                Assert.That(
                    WorldPointerPicker.SelectCandidate(
                        Array.Empty<WorldPointerCandidate>(),
                        Array.Empty<WorldPointerCandidate>()).IsEmpty,
                    Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(playerObject);
            }
        }

        [Test]
        public void WorldInteractionNoLongerTreatsMobsAsInteractionTargets()
        {
            Assert.That(
                WorldInteractionTargetingController.OffersInteraction(
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    RealtimeWorldActorKind.Mob),
                Is.False);
            Assert.That(
                WorldInteractionTargetingController.OffersInteraction(
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    RealtimeWorldActorKind.Npc),
                Is.True);
            Assert.That(
                WorldInteractionTargetingController.OffersInteraction(
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    RealtimeWorldActorKind.ResourceNode),
                Is.True);
            Assert.That(
                WorldInteractionTargetingController.OffersInteraction(
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    null),
                Is.False);
            Assert.That(
                WorldInteractionTargetingController.OffersInteraction(
                    RealtimeWorldInteractionTargetKind.Corpse,
                    null),
                Is.True);
        }

        [Test]
        public void SelectionShowsTheRequestThenFollowsTheOwnerState()
        {
            var selection = new CombatTargetSelection();
            var idle = Owner(4, RealtimeCombatLifeState.Alive);
            Assert.That(selection.EffectiveTarget(idle), Is.Zero);
            Assert.That(selection.EffectiveTarget(null), Is.Zero);

            var attack = Guid.NewGuid();
            selection.BeginAttack(attack, MobId);
            Assert.That(selection.PendingOperationId, Is.EqualTo(attack));
            Assert.That(selection.EffectiveTarget(idle), Is.EqualTo(MobId));

            // The result can arrive before the owner state that shows the target.
            Assert.That(selection.ApplyResult(Result(attack, RealtimeCombatIntentKind.AttackTarget, true, MobId), idle), Is.True);
            Assert.That(selection.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(selection.EffectiveTarget(idle), Is.EqualTo(MobId));
            Assert.That(selection.EffectiveTarget(Owner(5, RealtimeCombatLifeState.Alive, MobId)), Is.EqualTo(MobId));

            // A newer owner state is authoritative, such as after the Mob died.
            Assert.That(selection.EffectiveTarget(Owner(6, RealtimeCombatLifeState.Alive)), Is.Zero);
        }

        [Test]
        public void RejectedRequestFallsBackToTheOwnerTargetAndKeepsTheError()
        {
            var selection = new CombatTargetSelection();
            var attacking = Owner(7, RealtimeCombatLifeState.Alive, MobId);
            var request = Guid.NewGuid();
            selection.BeginAttack(request, PlayerId);
            Assert.That(selection.EffectiveTarget(attacking), Is.EqualTo(PlayerId));

            var error = new RealtimeError(
                RealtimeCombatErrorCodes.PlayerVersusPlayerDisabled,
                "This Shard does not allow attacking other characters.");
            Assert.That(
                selection.ApplyResult(
                    Result(request, RealtimeCombatIntentKind.AttackTarget, false, MobId, error),
                    attacking),
                Is.True);
            Assert.That(selection.EffectiveTarget(attacking), Is.EqualTo(MobId));
            Assert.That(selection.LastRejection, Is.SameAs(error));

            // A clear shows no target at once, and a stale result changes nothing.
            var clear = Guid.NewGuid();
            selection.BeginClear(clear);
            Assert.That(selection.LastRejection, Is.Null);
            Assert.That(selection.EffectiveTarget(attacking), Is.Zero);
            Assert.That(
                selection.ApplyResult(Result(request, RealtimeCombatIntentKind.AttackTarget, true, MobId), attacking),
                Is.False);
            Assert.That(selection.EffectiveTarget(attacking), Is.Zero);
            Assert.That(
                selection.ApplyResult(Result(clear, RealtimeCombatIntentKind.ClearTarget, true, 0), attacking),
                Is.True);
            Assert.That(selection.EffectiveTarget(attacking), Is.Zero);
            Assert.That(selection.EffectiveTarget(Owner(8, RealtimeCombatLifeState.Alive)), Is.Zero);

            selection.BeginAttack(Guid.NewGuid(), MobId);
            selection.Reset();
            Assert.That(selection.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(selection.EffectiveTarget(Owner(9, RealtimeCombatLifeState.Alive)), Is.Zero);
        }

        [Test]
        public void ApproachStopsAMarginInsideTheWeaponRange()
        {
            Assert.That(CombatApproachPlanner.ApproachRange(10f), Is.EqualTo(9.25f).Within(0.0001f));
            Assert.That(CombatApproachPlanner.ApproachRange(1.5f), Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(CombatApproachPlanner.ApproachRange(0.4f), Is.EqualTo(0.4f).Within(0.0001f));
            Assert.That(CombatApproachPlanner.ApproachRange(1f), Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(
                CombatApproachPlanner.PlanarDistance(new Vector3(0f, 5f, 0f), new Vector3(3f, -2f, 4f)),
                Is.EqualTo(5f).Within(0.0001f));
        }

        [Test]
        public void NoWeaponMeansNoApproach()
        {
            var planner = new CombatApproachPlanner();

            var decision = planner.Update(Input(MobId, null, 20f, false, false, null, 0f));

            Assert.That(decision.Action, Is.EqualTo(CombatApproachAction.None));
            Assert.That(planner.TargetEntityId, Is.EqualTo(MobId));

            // Once the bow is known, the approach starts.
            decision = planner.Update(Input(MobId, 10f, 20f, false, false, null, 0.1f));
            Assert.That(decision.Action, Is.EqualTo(CombatApproachAction.Approach));
            Assert.That(decision.Range, Is.EqualTo(9.25f).Within(0.0001f));
        }

        [Test]
        public void ApproachWalksAgainWhenTheTargetMovesOrTheWorkerReportsOutOfRange()
        {
            var planner = new CombatApproachPlanner();
            Assert.That(
                planner.Update(Input(MobId, 10f, 20f, false, false, null, 0f)).Action,
                Is.EqualTo(CombatApproachAction.Approach));

            // While walking, a target close to the planned goal changes nothing.
            Assert.That(
                planner.Update(Input(MobId, 10f, 15f, true, true, RealtimeCombatAttackStatus.Moving, 2f, targetX: 21f)).Action,
                Is.EqualTo(CombatApproachAction.None));

            // A target that moved away from the goal gets a new route.
            Assert.That(
                planner.Update(Input(MobId, 10f, 15f, true, true, RealtimeCombatAttackStatus.Moving, 2.5f, targetX: 23f)).Action,
                Is.EqualTo(CombatApproachAction.Approach));

            // Standing and attacking never walks, even beyond the planar range.
            Assert.That(
                planner.Update(Input(MobId, 10f, 10.4f, false, true, RealtimeCombatAttackStatus.Attacking, 9f)).Action,
                Is.EqualTo(CombatApproachAction.None));

            // A target out of range walks again, but not more than once per retry interval.
            var decision = planner.Update(Input(MobId, 10f, 12f, false, true, RealtimeCombatAttackStatus.OutOfRange, 10f));
            Assert.That(decision.Action, Is.EqualTo(CombatApproachAction.Approach));
            Assert.That(decision.Range, Is.EqualTo(9.25f).Within(0.0001f));
            Assert.That(
                planner.Update(Input(MobId, 10f, 12f, false, true, RealtimeCombatAttackStatus.OutOfRange, 10.5f)).Action,
                Is.EqualTo(CombatApproachAction.None));

            // Within the planar range the worker still measures out of range: walk a margin closer.
            decision = planner.Update(Input(MobId, 10f, 9.5f, false, true, RealtimeCombatAttackStatus.OutOfRange, 12f));
            Assert.That(decision.Action, Is.EqualTo(CombatApproachAction.Approach));
            Assert.That(decision.Range, Is.EqualTo(8.75f).Within(0.0001f));
        }

        [Test]
        public void BlockedLineOfSightWalksCloserEachTime()
        {
            var planner = new CombatApproachPlanner();
            planner.Update(Input(MobId, 10f, 20f, false, false, null, 0f));

            var first = planner.Update(Input(MobId, 10f, 9f, false, true, RealtimeCombatAttackStatus.LineOfSightBlocked, 5f));
            Assert.That(first.Action, Is.EqualTo(CombatApproachAction.Approach));
            Assert.That(first.Range, Is.EqualTo(7f).Within(0.0001f));

            var second = planner.Update(Input(MobId, 10f, 7f, false, true, RealtimeCombatAttackStatus.LineOfSightBlocked, 7f));
            Assert.That(second.Range, Is.EqualTo(5f).Within(0.0001f));

            var closest = planner.Update(Input(MobId, 10f, 1f, false, true, RealtimeCombatAttackStatus.LineOfSightBlocked, 9f));
            Assert.That(closest.Range, Is.EqualTo(CombatApproachPlanner.MinimumRange));

            // A clear line of sight restores the weapon range for the next approach.
            planner.Update(Input(MobId, 10f, 1f, false, true, RealtimeCombatAttackStatus.Attacking, 11f));
            Assert.That(planner.RequestedRange, Is.EqualTo(9.25f).Within(0.0001f));
        }

        [Test]
        public void LosingTheTargetStopsOnlyTheApproachItself()
        {
            var planner = new CombatApproachPlanner();
            planner.Update(Input(MobId, 10f, 20f, false, false, null, 0f));

            Assert.That(
                planner.Update(Input(0, 10f, 20f, true, true, null, 1f)).Action,
                Is.EqualTo(CombatApproachAction.Stop));

            // A walk that a newer click started is not the approach's to stop.
            planner.Update(Input(MobId, 10f, 20f, false, false, null, 2f));
            Assert.That(
                planner.Update(Input(0, 10f, 20f, true, false, null, 3f)).Action,
                Is.EqualTo(CombatApproachAction.None));

            // A new target, or a reset of the same one, starts a fresh approach.
            planner.Update(Input(MobId, 10f, 20f, false, false, null, 4f));
            Assert.That(
                planner.Update(Input(PlayerId, 10f, 20f, false, false, null, 4.1f)).Action,
                Is.EqualTo(CombatApproachAction.Approach));
            planner.Reset();
            Assert.That(
                planner.Update(Input(PlayerId, 10f, 20f, true, true, null, 4.2f)).Action,
                Is.EqualTo(CombatApproachAction.Approach));
        }

        [Test]
        public void ApproachWaitsForTheTargetPresentation()
        {
            var planner = new CombatApproachPlanner();
            var input = new CombatApproachInput(MobId, 10f, Vector3.zero, null, false, false, null, 0f);

            Assert.That(planner.Update(input).Action, Is.EqualTo(CombatApproachAction.None));
            Assert.That(
                planner.Update(Input(MobId, 10f, 20f, false, false, null, 0.1f)).Action,
                Is.EqualTo(CombatApproachAction.Approach));
        }

        private static void AssertDescribed(
            WorldPointerCandidate candidate,
            WorldActorClientState actors,
            CombatClickTargetKind kind,
            ulong entityId)
        {
            var described = CombatClickRules.Describe(candidate, actors);
            Assert.That(described.Kind, Is.EqualTo(kind));
            Assert.That(described.EntityId, Is.EqualTo(entityId));
        }

        // The character stands at the origin and the target at (distance, 0, 0) unless
        // targetX moves it.
        private static CombatApproachInput Input(
            ulong target,
            float? weaponRange,
            float distance,
            bool isMoving,
            bool ownsNavigation,
            RealtimeCombatAttackStatus? status,
            float time,
            float? targetX = null)
        {
            return new CombatApproachInput(
                target,
                weaponRange,
                Vector3.zero,
                new Vector3(targetX ?? distance, 0f, 0f),
                isMoving,
                ownsNavigation,
                status,
                time);
        }

        private static CharacterCollisionSettings CollisionSettings()
        {
            return new CharacterCollisionSettings(0.4f, 1.8f, 0.35f, 45f, 0.2f, 0.25f, 4);
        }

        private static RealtimeCombatIntentResult Result(
            Guid operationId,
            RealtimeCombatIntentKind kind,
            bool succeeded,
            ulong target,
            RealtimeError error = null)
        {
            return new RealtimeCombatIntentResult(operationId, kind, succeeded, target, error);
        }

        private static RealtimeCombatantState Combatant(
            ulong entityId,
            RealtimeCombatLifeState lifeState = RealtimeCombatLifeState.Alive)
        {
            var dead = lifeState == RealtimeCombatLifeState.Dead;
            return new RealtimeCombatantState(entityId, 1, dead ? 0 : 40, 50, lifeState, false, 0);
        }

        private static RealtimeOwnerCombatState Owner(
            long revision,
            RealtimeCombatLifeState lifeState,
            ulong target = 0)
        {
            var dead = lifeState == RealtimeCombatLifeState.Dead;
            return new RealtimeOwnerCombatState(
                revision,
                100,
                dead ? 0 : 100,
                100,
                lifeState,
                1,
                dead ? 250u : 0u,
                false,
                dead ? 0 : target,
                dead
                    ? RealtimeCombatAttackStatus.Dead
                    : target == 0 ? RealtimeCombatAttackStatus.Idle : RealtimeCombatAttackStatus.Attacking,
                0,
                0,
                Guid.Empty,
                0);
        }

        private static RealtimeWorldActorSpawn Spawn(ulong entityId, RealtimeWorldActorKind kind)
        {
            var mob = kind == RealtimeWorldActorKind.Mob;
            return new RealtimeWorldActorSpawn(
                entityId,
                Guid.NewGuid(),
                mob ? "mob.feral_wolf" : "npc.city_services",
                mob ? "local.feral_wolf" : "local.city_services",
                mob ? "Feral Wolf" : "Mira",
                kind,
                mob ? "wild" : "city",
                mob ? RealtimeWorldActorDisposition.Hostile : RealtimeWorldActorDisposition.Friendly,
                mob ? "mob.feral_wolf" : "npc.city_services",
                1,
                1,
                true,
                2.5f,
                0f,
                -1f,
                270f,
                RealtimeWorldActorActivityTier.EventDriven,
                0f,
                0.5f,
                0f,
                0.8f,
                1f,
                1.4f);
        }

        private sealed class RecordingHandler : IWorldClickHandler
        {
            private readonly string name;
            private readonly List<string> calls;

            public RecordingHandler(string name, List<string> calls, bool handles)
            {
                this.name = name;
                this.calls = calls;
                Handles = handles;
            }

            public bool Handles { get; set; }

            public bool TryHandleWorldClick(Ray ray)
            {
                calls.Add(name);
                return Handles;
            }
        }

        private sealed class FakeTarget : IWorldInteractionTarget
        {
            public FakeTarget(bool active)
            {
                IsTargetActive = active;
                TargetRuntimeId = Guid.NewGuid();
            }

            public RealtimeWorldInteractionTargetKind TargetKind =>
                RealtimeWorldInteractionTargetKind.WorldActor;
            public ulong TargetEntityId => NpcId;
            public Guid TargetRuntimeId { get; }
            public long TargetRevision => 1;
            public bool IsTargetActive { get; }
            public string TargetDisplayName => "Npc";
            public Vector3 TargetPosition => Vector3.zero;
        }
    }
}
