using System;
using Mmorpg.Client.Items;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class WorldActorClientFoundationTests
    {
        [Test]
        public void ActorStateAcceptsOnlyAuthoritativeMonotonicRevisions()
        {
            var state = FixtureTexts.ActorState();
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
                spawnDefinitionId: "local.forged_spawn");
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
                new[]
                {
                    new RealtimeWorldActorCapability(
                        "services.vendor",
                        RealtimeWorldActorCapabilityKind.Vendor,
                        "vendor.city_general_store",
                        true,
                        4)
                });
            var mira = new WorldActorClientEntry(CreateSpawn(), FixtureTexts.DevelopmentActors());

            Assert.That(state.TryOpen(opened, mira, out var error), Is.True, error);
            Assert.That(state.ActiveInteraction.Capabilities[0].Revision, Is.EqualTo(4));

            // The open interaction names its actor and its capabilities from the text catalog.
            Assert.That(state.ActiveDisplayName, Is.EqualTo("Mira"));
            Assert.That(state.CapabilityName(state.ActiveInteraction.Capabilities[0]), Is.EqualTo("Trade"));
            Assert.That(
                state.TryComplete(
                    new RealtimeWorldInteractionResult(
                        Guid.NewGuid(),
                        interactionId,
                        RealtimeWorldInteractionOperationKind.CapabilityAction,
                        false,
                        3,
                        itemStateRevision: 0,
                        message: string.Empty,
                        error: new RealtimeError("invalid", "Wrong operation")),
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
                        Array.Empty<RealtimeWorldActorCapability>()),
                    null,
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
                        itemStateRevision: 0,
                        message: string.Empty,
                        error: new RealtimeError(
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
                42,
                "Insurance applied.",
                null);

            Assert.That(state.TryComplete(result, out var error), Is.True, error);
            Assert.That(state.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(state.LastErrorCode, Is.Empty);
            Assert.That(state.LastMessage, Is.EqualTo("Insurance applied."));
            Assert.That(result.ItemStateRevision, Is.EqualTo(42));
        }

        [Test]
        public void DirectPointerCandidatesWinAndSphereToleranceIsFallback()
        {
            var direct = new FakeTarget("Direct");
            var sphere = new FakeTarget("Sphere");
            var results = new System.Collections.Generic.List<WorldPointerCandidate>();

            WorldPointerPicker.SelectCandidates(
                new[] { new WorldPointerCandidate(direct, 5f) },
                new[] { new WorldPointerCandidate(sphere, 1f) },
                results);
            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(results[0].Target, Is.SameAs(direct));

            WorldPointerPicker.SelectCandidates(
                Array.Empty<WorldPointerCandidate>(),
                new[] { new WorldPointerCandidate(sphere, 2f) },
                results);
            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(results[0].Target, Is.SameAs(sphere));
        }

        [Test]
        public void AnActorIsNamedFromTheTextCatalogAndItsMenuOptionsCannotChangeWithoutADespawn()
        {
            var state = FixtureTexts.ActorState();
            var spawn = CreateSpawn();
            Assert.That(state.TryApplySpawn(spawn, out var error), Is.True, error);
            Assert.That(state.TryGet(spawn.EntityId, out var actor), Is.True);
            Assert.That(actor.DisplayName, Is.EqualTo("Mira"));
            Assert.That(actor.ExamineText, Is.EqualTo("She keeps the city's stores."));
            Assert.That(actor.MenuOptions.Count, Is.EqualTo(2));
            Assert.That(actor.MenuOptionLabel(actor.MenuOptions[0].CapabilityId), Is.EqualTo("Talk-to"));
            Assert.That(actor.CapabilityName(actor.MenuOptions[0].CapabilityId), Is.EqualTo("Talk"));
            Assert.That(actor.MenuOptions[1].CapabilityId, Is.EqualTo("services.vendor"));

            // A text that the catalog lacks shows its key, and an id that cannot be a key shows
            // as it is.
            Assert.That(
                actor.MenuOptionLabel("services.unknown"),
                Is.EqualTo("[actor/npc.city_services/capability/services.unknown/menu]"));
            Assert.That(actor.MenuOptionLabel("Not A Key"), Is.EqualTo("Not A Key"));
            var unnamed = new WorldActorClientState();
            Assert.That(unnamed.TryApplySpawn(CreateSpawn(), out error), Is.True, error);
            Assert.That(unnamed.TryGet(spawn.EntityId, out var unnamedActor), Is.True);
            Assert.That(unnamedActor.DisplayName, Is.EqualTo("[actor/npc.city_services/name]"));
            Assert.That(unnamedActor.ExamineText, Is.Empty);

            // The same presence again is fine.
            Assert.That(state.TryApplySpawn(CreateSpawn(spawn.RuntimeActorId), out error), Is.True, error);

            Assert.That(
                state.TryApplySpawn(
                    CreateSpawn(
                        spawn.RuntimeActorId,
                        menuOptions: new[]
                        {
                            new RealtimeWorldActorMenuOption(
                                "services.vendor",
                                RealtimeWorldActorCapabilityKind.Vendor)
                        }),
                    out error),
                Is.False);
            Assert.That(error, Does.Contain("identity"));
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
                    WorldPointerPicker.FindRegisteredTarget(
                        unrelatedCollider),
                    Is.Null);
                Assert.That(WorldPointerPicker.FindCandidate(unrelatedCollider, 1f).IsEmpty, Is.True);

                var view = actorObject.AddComponent<NpcView>();
                view.Initialize(new WorldActorClientEntry(CreateSpawn(), FixtureTexts.DevelopmentActors()));
                var actorCollider = actorObject.GetComponentInChildren<BoxCollider>();

                Assert.That(actorCollider, Is.Not.Null);
                Assert.That(
                    WorldPointerPicker.FindRegisteredTarget(
                        actorCollider),
                    Is.SameAs(view));
                Assert.That(WorldPointerPicker.FindCandidate(actorCollider, 1f).Target, Is.SameAs(view));
                Assert.That(view.TargetHeight, Is.EqualTo(2f));
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
            var actor = new WorldActorClientEntry(CreateSpawn(), FixtureTexts.DevelopmentActors());
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
                    90f,
                    2_000_000_000_000L,
                    false,
                    true,
                    RealtimeCorpseSource.Character("Fallen Hero"),
                    "corpse.remains",
                    null),
                    FixtureTexts.DevelopmentActors()));

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
            string spawnDefinitionId = "local.city_services",
            RealtimeWorldActorMenuOption[] menuOptions = null)
        {
            return new RealtimeWorldActorSpawn(
                7,
                runtimeActorId ?? Guid.NewGuid(),
                "npc.city_services",
                spawnDefinitionId,
                RealtimeWorldActorKind.Npc,
                "city",
                RealtimeWorldActorDisposition.Friendly,
                0,
                RealtimeWorldActorAggression.None,
                "npc.city_services",
                1,
                1,
                true,
                2.5f,
                0f,
                -1f,
                270f,
                RealtimeWorldActorActivityTier.EventDriven,
                0f,
                1f,
                0f,
                0.8f,
                2f,
                0.8f,
                menuOptions ?? new[]
                {
                    new RealtimeWorldActorMenuOption("services.dialogue", RealtimeWorldActorCapabilityKind.Dialogue),
                    new RealtimeWorldActorMenuOption("services.vendor", RealtimeWorldActorCapabilityKind.Vendor)
                });
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
            public Vector3 TargetPosition => Vector3.zero;
        }
    }
}
