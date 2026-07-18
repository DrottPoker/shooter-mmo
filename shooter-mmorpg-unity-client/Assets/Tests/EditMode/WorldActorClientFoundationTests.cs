using System;
using NUnit.Framework;
using ShooterMmo.GameProtocol;
using ShooterMmo.Items;
using ShooterMmo.WorldActors;
using UnityEngine;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class WorldActorClientFoundationTests
    {
        [Test]
        public void ActorStateAcceptsOnlyAuthoritativeMonotonicRevisions()
        {
            var state = new WorldActorClientState();
            var spawn = CreateSpawn();

            Assert.That(state.TryApplySpawn(spawn, out var error), Is.True, error);
            Assert.That(
                state.TryApplyState(
                    new RealtimeWorldActorState(
                        spawn.EntityId,
                        2,
                        1,
                        true,
                        3f,
                        0f,
                        -1f,
                        90f,
                        RealtimeWorldActorActivityTier.EventDriven),
                    out error),
                Is.True,
                error);
            Assert.That(state.TryGet(spawn.EntityId, out var actor), Is.True);
            Assert.That(actor.StateRevision, Is.EqualTo(2));
            Assert.That(actor.PositionX, Is.EqualTo(3f));

            Assert.That(
                state.TryApplyState(
                    new RealtimeWorldActorState(
                        spawn.EntityId,
                        1,
                        1,
                        true,
                        99f,
                        0f,
                        -1f,
                        90f,
                        RealtimeWorldActorActivityTier.EventDriven),
                    out error),
                Is.True,
                error);
            Assert.That(actor.PositionX, Is.EqualTo(3f));
            Assert.That(
                state.TryApplyState(
                    new RealtimeWorldActorState(
                        spawn.EntityId,
                        2,
                        1,
                        true,
                        4f,
                        0f,
                        -1f,
                        90f,
                        RealtimeWorldActorActivityTier.EventDriven),
                    out error),
                Is.False);
            Assert.That(error, Does.Contain("conflicts"));

            var conflictingPresence = CreateSpawn(
                spawn.RuntimeActorId,
                "Forged Name");
            Assert.That(
                state.TryApplySpawn(conflictingPresence, out error),
                Is.False);
            Assert.That(error, Does.Contain("identity"));
        }

        [Test]
        public void InteractionStateCorrelatesOperationsAndClearsOnReconnectBoundary()
        {
            var state = new WorldInteractionClientState();
            var operationId = Guid.NewGuid();
            var interactionId = Guid.NewGuid();
            state.Begin(operationId);
            var opened = new RealtimeWorldInteractionOpened(
                operationId,
                interactionId,
                RealtimeWorldInteractionTargetKind.WorldActor,
                7,
                Guid.NewGuid(),
                3,
                9,
                "Mira",
                new[]
                {
                    new RealtimeWorldActorCapability(
                        "services.vendor",
                        RealtimeWorldActorCapabilityKind.Vendor,
                        "Trade",
                        true,
                        4)
                });

            Assert.That(state.TryOpen(opened, out var error), Is.True, error);
            Assert.That(state.ActiveInteraction.Capabilities[0].Revision, Is.EqualTo(4));
            Assert.That(
                state.TryComplete(
                    new RealtimeWorldInteractionResult(
                        Guid.NewGuid(),
                        interactionId,
                        RealtimeWorldInteractionOperationKind.CapabilityAction,
                        false,
                        3,
                        new RealtimeError("invalid", "Wrong operation")),
                    out error),
                Is.False);

            state.Clear();
            state.Close(new RealtimeWorldInteractionClosed(
                interactionId,
                RealtimeWorldInteractionTargetKind.WorldActor,
                7,
                opened.TargetRuntimeId,
                "stale",
                "Stale closure"));

            Assert.That(state.ActiveInteraction, Is.Null);
            Assert.That(state.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(state.LastErrorCode, Is.Empty);
        }

        [Test]
        public void ServerClosurePreservesPendingOperationCorrelationUntilResultArrives()
        {
            var state = new WorldInteractionClientState();
            var openOperationId = Guid.NewGuid();
            var interactionId = Guid.NewGuid();
            var runtimeActorId = Guid.NewGuid();
            state.Begin(openOperationId);
            Assert.That(
                state.TryOpen(
                    new RealtimeWorldInteractionOpened(
                        openOperationId,
                        interactionId,
                        RealtimeWorldInteractionTargetKind.WorldActor,
                        7,
                        runtimeActorId,
                        1,
                        2,
                        "Mira",
                        Array.Empty<RealtimeWorldActorCapability>()),
                    out var error),
                Is.True,
                error);

            var actionOperationId = Guid.NewGuid();
            state.Begin(actionOperationId);
            state.Close(new RealtimeWorldInteractionClosed(
                interactionId,
                RealtimeWorldInteractionTargetKind.WorldActor,
                7,
                runtimeActorId,
                "world_interaction_out_of_range",
                "Move closer."));

            Assert.That(state.ActiveInteraction, Is.Null);
            Assert.That(state.PendingOperationId, Is.EqualTo(actionOperationId));
            Assert.That(
                state.TryComplete(
                    new RealtimeWorldInteractionResult(
                        actionOperationId,
                        interactionId,
                        RealtimeWorldInteractionOperationKind.CapabilityAction,
                        false,
                        1,
                        new RealtimeError(
                            "world_interaction_session_invalid",
                            "The interaction closed.")),
                    out error),
                Is.True,
                error);
            Assert.That(state.PendingOperationId, Is.EqualTo(Guid.Empty));
        }

        [Test]
        public void LifecycleCompletionPreservesCommittedMessageAndItemRevision()
        {
            var state = new WorldInteractionClientState();
            var operationId = Guid.NewGuid();
            var interactionId = Guid.NewGuid();
            state.Begin(operationId);
            var result = new RealtimeWorldInteractionResult(
                operationId,
                interactionId,
                RealtimeWorldInteractionOperationKind.CapabilityAction,
                true,
                3,
                null,
                "Insurance applied.",
                42);

            Assert.That(state.TryComplete(result, out var error), Is.True, error);
            Assert.That(state.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(state.LastErrorCode, Is.Empty);
            Assert.That(state.LastMessage, Is.EqualTo("Insurance applied."));
            Assert.That(result.ItemStateRevision, Is.EqualTo(42));
        }

        [Test]
        public void DirectCrosshairCandidateWinsAndSphereToleranceIsFallback()
        {
            var direct = new FakeTarget("Direct");
            var sphere = new FakeTarget("Sphere");

            Assert.That(
                WorldInteractionTargetingController.SelectCandidate(
                    new[] { new WorldInteractionTargetCandidate(direct, 5f) },
                    new[] { new WorldInteractionTargetCandidate(sphere, 1f) }),
                Is.SameAs(direct));
            Assert.That(
                WorldInteractionTargetingController.SelectCandidate(
                    Array.Empty<WorldInteractionTargetCandidate>(),
                    new[] { new WorldInteractionTargetCandidate(sphere, 2f) }),
                Is.SameAs(sphere));
        }

        [Test]
        public void UnregisteredCollidersAreIgnoredAndActorBoundsAreRegistered()
        {
            var unrelated = new GameObject("Unrelated");
            var unrelatedCollider = unrelated.AddComponent<BoxCollider>();
            var actorObject = new GameObject("ActorPresentation");
            try
            {
                Assert.That(
                    WorldInteractionTargetingController.FindRegisteredTarget(
                        unrelatedCollider),
                    Is.Null);

                var view = actorObject.AddComponent<NpcView>();
                view.Initialize(new WorldActorClientEntry(CreateSpawn()));
                var actorCollider = actorObject.GetComponentInChildren<BoxCollider>();

                Assert.That(actorCollider, Is.Not.Null);
                Assert.That(
                    WorldInteractionTargetingController.FindRegisteredTarget(
                        actorCollider),
                    Is.SameAs(view));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(unrelated);
                UnityEngine.Object.DestroyImmediate(actorObject);
            }
        }

        [Test]
        public void PresentationReplacementCannotMutateActorAuthorityState()
        {
            var actor = new WorldActorClientEntry(CreateSpawn());
            var firstPresentation = new GameObject("FirstPresentation");
            var replacementPresentation = new GameObject("ReplacementPresentation");
            try
            {
                firstPresentation.AddComponent<NpcView>().Initialize(actor);
                replacementPresentation.AddComponent<NpcView>().Initialize(actor);

                Assert.That(actor.ActorDefinitionId, Is.EqualTo("npc.city_services"));
                Assert.That(actor.FactionId, Is.EqualTo("city"));
                Assert.That(actor.InteractionRevision, Is.EqualTo(1));
                Assert.That(actor.SpawnDefinitionId, Is.EqualTo("local.city_services"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(firstPresentation);
                UnityEngine.Object.DestroyImmediate(replacementPresentation);
            }
        }

        [Test]
        public void CorpsePresentationUsesSharedInteractionTargetContract()
        {
            var corpseId = Guid.NewGuid();
            var targetObject = new GameObject("CorpseTarget");
            try
            {
                var target = targetObject.AddComponent<CorpseInteractionTarget>();
                target.Apply(new CorpsePresenceEntry(new RealtimeCorpsePresence(
                    corpseId,
                    1f,
                    0f,
                    2f,
                    2_000_000_000_000L,
                    false,
                    "Fallen Hero",
                    "corpse.generic_loot_crate")));

                Assert.That(
                    target.TargetKind,
                    Is.EqualTo(RealtimeWorldInteractionTargetKind.Corpse));
                Assert.That(target.TargetRuntimeId, Is.EqualTo(corpseId));
                Assert.That(target.IsTargetActive, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(targetObject);
            }
        }

        private static RealtimeWorldActorSpawn CreateSpawn(
            Guid? runtimeActorId = null,
            string displayName = "Mira")
        {
            return new RealtimeWorldActorSpawn(
                7,
                runtimeActorId ?? Guid.NewGuid(),
                "npc.city_services",
                "local.city_services",
                displayName,
                RealtimeWorldActorKind.Npc,
                "city",
                RealtimeWorldActorDisposition.Friendly,
                "npc.city_services",
                2.5f,
                0f,
                -1f,
                270f,
                1,
                1,
                true,
                0f,
                1f,
                0f,
                0.8f,
                2f,
                0.8f,
                RealtimeWorldActorActivityTier.EventDriven);
        }

        private sealed class FakeTarget : IWorldInteractionTarget
        {
            public FakeTarget(string name)
            {
                TargetDisplayName = name;
                TargetRuntimeId = Guid.NewGuid();
            }

            public RealtimeWorldInteractionTargetKind TargetKind =>
                RealtimeWorldInteractionTargetKind.WorldActor;
            public ulong TargetEntityId => 1;
            public Guid TargetRuntimeId { get; }
            public long TargetRevision => 1;
            public bool IsTargetActive => true;
            public string TargetDisplayName { get; }
        }
    }
}
