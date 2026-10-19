using System;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.GameProtocol;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class CorpseClientStateTests
    {
        private const long ExpiresAtUnixMilliseconds = 2_000_000_000_000L;

        [Test]
        public void PresenceChunksApplyOnceAndRejectDuplicateOrStaleSequences()
        {
            var state = new CorpseClientState();
            var firstCorpseId = Guid.NewGuid();
            var secondCorpseId = Guid.NewGuid();
            var first = new RealtimeCorpsePresenceSnapshotChunk(
                10,
                0,
                2,
                new[] { CreatePresence(firstCorpseId, "First Hero") });
            var second = new RealtimeCorpsePresenceSnapshotChunk(
                10,
                1,
                2,
                new[] { CreatePresence(secondCorpseId, "Second Hero") });

            Assert.That(state.ApplyPresenceChunk(first), Is.EqualTo(CorpseStateApplyResult.Waiting));
            Assert.That(state.ApplyPresenceChunk(first), Is.EqualTo(CorpseStateApplyResult.Duplicate));
            Assert.That(state.ApplyPresenceChunk(second), Is.EqualTo(CorpseStateApplyResult.Applied));
            Assert.That(state.NearbyCorpses.Select(entry => entry.CorpseId), Is.EquivalentTo(
                new[] { firstCorpseId, secondCorpseId }));
            Assert.That(state.ApplyPresenceChunk(second), Is.EqualTo(CorpseStateApplyResult.Duplicate));
            Assert.That(
                state.ApplyPresenceChunk(new RealtimeCorpsePresenceSnapshotChunk(
                    9,
                    0,
                    1,
                    Array.Empty<RealtimeCorpsePresence>())),
                Is.EqualTo(CorpseStateApplyResult.Stale));
            Assert.That(
                state.ApplyPresenceChunk(new RealtimeCorpsePresenceSnapshotChunk(
                    11,
                    0,
                    1,
                    Array.Empty<RealtimeCorpsePresence>())),
                Is.EqualTo(CorpseStateApplyResult.Applied));
            Assert.That(state.NearbyCorpses, Is.Empty);
        }

        [Test]
        public void SnapshotAndTargetedDeltaApplyWithoutOptimisticMutation()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var containers = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var snapshot = CreateSnapshot(corpseId, itemId, containers, 5, 5, 1);
            CompleteOpen(state, corpseId);

            Assert.That(state.ApplyViewChunk(snapshot[0]), Is.EqualTo(CorpseStateApplyResult.Waiting));
            Assert.That(state.ActiveView, Is.Null);
            Assert.That(state.ApplyViewChunk(snapshot[1]), Is.EqualTo(CorpseStateApplyResult.Waiting));
            Assert.That(state.ActiveView, Is.Null);
            Assert.That(state.ApplyViewChunk(snapshot[2]), Is.EqualTo(CorpseStateApplyResult.Applied));
            Assert.That(state.ActiveView.Revision, Is.EqualTo(5));
            Assert.That(FindItem(state, itemId).Quantity, Is.EqualTo(5));
            Assert.That(
                state.ActiveView.Sections.Single(section => section.SectionKind == "equipment")
                    .Slots.Single().EquipmentSlotId,
                Is.EqualTo("head"));

            var delta = CreateChunk(
                Guid.NewGuid(),
                RealtimeCorpseViewUpdateKind.Delta,
                corpseId,
                5,
                6,
                0,
                1,
                "general_inventory",
                containers[0],
                2,
                false,
                1,
                new[] { CreateSlot(0, itemId, 3, 2) });

            Assert.That(state.ApplyViewChunk(delta), Is.EqualTo(CorpseStateApplyResult.Applied));
            Assert.That(state.ActiveView.Revision, Is.EqualTo(6));
            Assert.That(FindItem(state, itemId).Quantity, Is.EqualTo(3));

            var stale = CreateChunk(
                Guid.NewGuid(),
                RealtimeCorpseViewUpdateKind.Delta,
                corpseId,
                5,
                7,
                0,
                1,
                "general_inventory",
                containers[0],
                3,
                false,
                1,
                new[] { CreateSlot(0, itemId, 1, 3) });
            Assert.That(state.ApplyViewChunk(stale), Is.EqualTo(CorpseStateApplyResult.Stale));
            Assert.That(state.ActiveView.Revision, Is.EqualTo(6));
            Assert.That(FindItem(state, itemId).Quantity, Is.EqualTo(3));
        }

        [Test]
        public void DeltaRejectsInconsistentContainerRevisionsAcrossChunks()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var containers = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            CompleteOpen(state, corpseId);
            foreach (var chunk in CreateSnapshot(corpseId, itemId, containers, 2, 5, 2))
            {
                state.ApplyViewChunk(chunk);
            }

            var updateId = Guid.NewGuid();
            var first = CreateChunk(
                updateId,
                RealtimeCorpseViewUpdateKind.Delta,
                corpseId,
                5,
                6,
                0,
                2,
                "general_inventory",
                containers[0],
                2,
                false,
                2,
                new[] { CreateSlot(0, itemId, 1, 2) });
            var second = CreateChunk(
                updateId,
                RealtimeCorpseViewUpdateKind.Delta,
                corpseId,
                5,
                6,
                1,
                2,
                "general_inventory",
                containers[0],
                3,
                false,
                2,
                new[] { new RealtimeCorpseSlot(1, "general", null) });

            Assert.That(state.ApplyViewChunk(first), Is.EqualTo(CorpseStateApplyResult.Waiting));
            Assert.That(state.ApplyViewChunk(second), Is.EqualTo(CorpseStateApplyResult.Invalid));
            Assert.That(state.ActiveView.Revision, Is.EqualTo(5));
        }

        [Test]
        public void SnapshotRequiresAllCanonicalCorpseSections()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            var updateId = Guid.NewGuid();
            CompleteOpen(state, corpseId);
            var first = CreateChunk(
                updateId,
                RealtimeCorpseViewUpdateKind.Snapshot,
                corpseId,
                -1,
                1,
                0,
                2,
                "general_inventory",
                Guid.NewGuid(),
                1,
                true,
                1,
                new[] { new RealtimeCorpseSlot(0, "general", null) });
            var second = CreateChunk(
                updateId,
                RealtimeCorpseViewUpdateKind.Snapshot,
                corpseId,
                -1,
                1,
                1,
                2,
                "equipment",
                Guid.NewGuid(),
                1,
                true,
                1,
                new[]
                {
                    new RealtimeCorpseSlot(
                        0,
                        "general",
                        Array.Empty<string>(),
                        "head",
                        null)
                });

            Assert.That(state.ApplyViewChunk(first), Is.EqualTo(CorpseStateApplyResult.Waiting));
            Assert.That(state.ApplyViewChunk(second), Is.EqualTo(CorpseStateApplyResult.Invalid));
            Assert.That(state.ActiveView, Is.Null);
        }

        [Test]
        public void EveryCorpseIntentShapeRoundTripsInUnityAssembly()
        {
            var corpseId = Guid.NewGuid();
            var intents = new[]
            {
                RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpseId),
                RealtimeCorpseInteractionIntent.CreateClose(Guid.NewGuid(), corpseId, 4),
                RealtimeCorpseInteractionIntent.CreateRefresh(Guid.NewGuid(), corpseId, 4),
                RealtimeCorpseInteractionIntent.CreateLootItem(
                    Guid.NewGuid(), corpseId, 4, Guid.NewGuid(), 2, Guid.NewGuid(), 3, 1,
                    Guid.Empty, 0),
                RealtimeCorpseInteractionIntent.CreateLootPartialStack(
                    Guid.NewGuid(), corpseId, 4, Guid.NewGuid(), 2, 3, Guid.NewGuid(), 3, 1,
                    Guid.Empty, 0),
                RealtimeCorpseInteractionIntent.CreateDepositItem(
                    Guid.NewGuid(), corpseId, 4, Guid.NewGuid(), 2, Guid.NewGuid(), 3, 1,
                    Guid.Empty, 0),
                RealtimeCorpseInteractionIntent.CreateDepositPartialStack(
                    Guid.NewGuid(), corpseId, 4, Guid.NewGuid(), 2, 3, Guid.NewGuid(), 3, 1,
                    Guid.Empty, 0),
                RealtimeCorpseInteractionIntent.CreateMoveItem(
                    Guid.NewGuid(), corpseId, 4, Guid.NewGuid(), 2, Guid.NewGuid(), 3, 1,
                    Guid.Empty, 0),
                RealtimeCorpseInteractionIntent.CreateMovePartialStack(
                    Guid.NewGuid(), corpseId, 4, Guid.NewGuid(), 2, 3, Guid.NewGuid(), 3, 1,
                    Guid.Empty, 0),
                RealtimeCorpseInteractionIntent.CreateSwapBag(
                    Guid.NewGuid(), corpseId, 4, Guid.NewGuid(), 2, Guid.NewGuid(), 3,
                    Guid.NewGuid(), 4, Guid.NewGuid(), 5)
            };

            foreach (var expected in intents)
            {
                Assert.That(
                    RealtimeProtocol.TryDecodeCorpseInteractionIntent(
                        RealtimeProtocol.EncodeCorpseInteractionIntent(expected),
                        out var actual,
                        out var error),
                    Is.True,
                    error);
                Assert.That(actual.OperationId, Is.EqualTo(expected.OperationId));
                Assert.That(actual.OperationKind, Is.EqualTo(expected.OperationKind));
                Assert.That(actual.CorpseId, Is.EqualTo(corpseId));
            }
        }

        [Test]
        public void CorpseViewDistinguishesEmptyAndNonEmptyBagAggregates()
        {
            var bagContentsContainerId = Guid.NewGuid();
            var bag = new CorpseLootItem(new RealtimeCorpseItem(
                Guid.NewGuid(),
                "fixture.pack",
                1,
                2,
                bagContentsContainerId,
                3));
            var equipment = new CorpseLootSection(
                "equipment",
                Guid.NewGuid(),
                1,
                1,
                new[]
                {
                    new CorpseLootSlot(
                        0,
                        "general",
                        Array.Empty<string>(),
                        "bag",
                        bag)
                });
            var emptyBagSection = new CorpseLootSection(
                "bag",
                bagContentsContainerId,
                3,
                1,
                new[] { new CorpseLootSlot(0, "general", null) });
            var emptyView = new CorpseLootView(
                Guid.NewGuid(),
                1,
                ExpiresAtUnixMilliseconds,
                "Fallen Hero",
                "corpse.remains",
                new[] { equipment, emptyBagSection });

            Assert.That(emptyView.BagHasContents(bag), Is.False);

            var occupiedBagSection = new CorpseLootSection(
                "bag",
                bagContentsContainerId,
                4,
                1,
                new[]
                {
                    new CorpseLootSlot(
                        0,
                        "general",
                        new CorpseLootItem(new RealtimeCorpseItem(
                            Guid.NewGuid(),
                            "fixture.ore",
                            1,
                            1,
                            Guid.Empty,
                            0)))
                });
            var occupiedView = new CorpseLootView(
                Guid.NewGuid(),
                2,
                ExpiresAtUnixMilliseconds,
                "Fallen Hero",
                "corpse.remains",
                new[] { equipment, occupiedBagSection });

            Assert.That(occupiedView.BagHasContents(bag), Is.True);
        }

        [Test]
        public void EmptiedClosureAfterTheLastLootLeavesANoticeInsteadOfAnError()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            OpenView(state, corpseId);
            var loot = Begin(state, RealtimeCorpseInteractionKind.LootItem, corpseId);
            AssertCompletes(state, Succeeded(loot, RealtimeCorpseInteractionKind.LootItem, corpseId));

            Assert.That(state.ApplyClosure(corpseId, "corpse_emptied", "The corpse is empty."), Is.True);

            Assert.That(state.ActiveView, Is.Null);
            Assert.That(state.Error, Is.Null);
            Assert.That(state.Notice, Is.EqualTo("The corpse is empty."));
            Assert.That(state.Status, Is.EqualTo(CorpseClientStatus.Ready));
            Assert.That(state.PendingOperationId, Is.EqualTo(Guid.Empty));

            Begin(state, RealtimeCorpseInteractionKind.Open, Guid.NewGuid());

            Assert.That(state.Notice, Is.Empty);
        }

        [Test]
        public void InvalidationClosureEndsTheViewWithAnError()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            OpenView(state, corpseId);

            Assert.That(
                state.ApplyClosure(corpseId, "corpse_invalidated", "The corpse is no longer available."),
                Is.True);

            Assert.That(state.ActiveView, Is.Null);
            Assert.That(state.Notice, Is.Empty);
            Assert.That(
                state.Error.ToDisplayMessage(),
                Is.EqualTo("corpse_invalidated: The corpse is no longer available."));
        }

        [Test]
        public void OwnCloseAndItsTrailingClosureLeaveNoMessage()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            OpenView(state, corpseId);
            var loot = Begin(state, RealtimeCorpseInteractionKind.LootItem, corpseId);
            AssertCompletes(
                state,
                Rejected(
                    loot,
                    RealtimeCorpseInteractionKind.LootItem,
                    corpseId,
                    "item_slot_occupied",
                    "The slot is occupied."));
            Assert.That(state.Error.Code, Is.EqualTo("item_slot_occupied"));

            var close = Begin(state, RealtimeCorpseInteractionKind.Close, corpseId);
            AssertCompletes(state, Succeeded(close, RealtimeCorpseInteractionKind.Close, corpseId));

            Assert.That(
                state.ApplyClosure(corpseId, "corpse_view_closed", "The corpse view was closed."),
                Is.False);
            Assert.That(state.ActiveView, Is.Null);
            Assert.That(state.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(state.Status, Is.EqualTo(CorpseClientStatus.Ready));
            Assert.That(state.Error, Is.Null);
            Assert.That(state.Notice, Is.Empty);
        }

        // Another player takes the last item while this viewer's loot is in flight. The
        // worker closes the view first and answers the loot afterwards.
        [Test]
        public void EmptiedClosureKeepsThePendingLootCorrelatedUntilItsResultArrives()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            OpenView(state, corpseId);
            var loot = Begin(state, RealtimeCorpseInteractionKind.LootItem, corpseId);

            Assert.That(state.ApplyClosure(corpseId, "corpse_emptied", "The corpse is empty."), Is.True);

            Assert.That(state.ActiveView, Is.Null);
            Assert.That(state.PendingOperationId, Is.EqualTo(loot));
            Assert.That(state.Status, Is.EqualTo(CorpseClientStatus.Busy));
            Assert.That(state.CanMutate, Is.False);
            Assert.That(state.Error, Is.Null);
            Assert.That(state.Notice, Is.EqualTo("The corpse is empty."));

            AssertCompletes(
                state,
                Rejected(
                    loot,
                    RealtimeCorpseInteractionKind.LootItem,
                    corpseId,
                    "corpse_not_found",
                    "The corpse is no longer active on this Shard."));

            Assert.That(state.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(state.Status, Is.EqualTo(CorpseClientStatus.Ready));
            Assert.That(state.ActiveView, Is.Null);
            Assert.That(state.Error, Is.Null);
            Assert.That(state.Notice, Is.EqualTo("The corpse is empty."));
        }

        // The worker can notice an emptied live Mob corpse before it finalizes the loot that
        // emptied it, so the looter's own committed result arrives after the closure.
        [Test]
        public void LateCommittedLootAfterTheClosureKeepsTheNotice()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            OpenView(state, corpseId);
            var loot = Begin(state, RealtimeCorpseInteractionKind.LootItem, corpseId);
            state.ApplyClosure(corpseId, "corpse_emptied", "The corpse is empty.");

            AssertCompletes(
                state,
                Succeeded(
                    loot,
                    RealtimeCorpseInteractionKind.LootItem,
                    corpseId,
                    requiresInventoryRefresh: true));

            Assert.That(state.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(state.Status, Is.EqualTo(CorpseClientStatus.Ready));
            Assert.That(state.ActiveView, Is.Null);
            Assert.That(state.Error, Is.Null);
            Assert.That(state.Notice, Is.EqualTo("The corpse is empty."));
        }

        [TestCase(
            "corpse_expired",
            "The corpse expired and is no longer available.",
            RealtimeCorpseInteractionKind.Refresh,
            "corpse_not_found",
            "The corpse is no longer active on this Shard.")]
        [TestCase(
            "corpse_expired",
            "The corpse expired and is no longer available.",
            RealtimeCorpseInteractionKind.DepositItem,
            "corpse_view_not_open",
            "Open the corpse before refreshing or looting it.")]
        [TestCase(
            "combat_character_dead",
            "The character died.",
            RealtimeCorpseInteractionKind.MoveItem,
            "combat_character_dead",
            "The character is dead.")]
        public void LateRejectionAfterAnIntendedClosureKeepsTheNotice(
            string closureCode,
            string closureMessage,
            RealtimeCorpseInteractionKind operationKind,
            string rejectionCode,
            string rejectionMessage)
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            OpenView(state, corpseId);
            var operation = Begin(state, operationKind, corpseId);
            state.ApplyClosure(corpseId, closureCode, closureMessage);

            AssertCompletes(
                state,
                Rejected(operation, operationKind, corpseId, rejectionCode, rejectionMessage));

            Assert.That(state.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(state.Status, Is.EqualTo(CorpseClientStatus.Ready));
            Assert.That(state.Error, Is.Null);
            Assert.That(state.Notice, Is.EqualTo(closureMessage));
        }

        [Test]
        public void LateRejectionAfterAnInvalidationKeepsTheClosureError()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            OpenView(state, corpseId);
            var refresh = Begin(state, RealtimeCorpseInteractionKind.Refresh, corpseId);
            state.ApplyClosure(corpseId, "corpse_invalidated", "The corpse is no longer available.");

            AssertCompletes(
                state,
                Rejected(
                    refresh,
                    RealtimeCorpseInteractionKind.Refresh,
                    corpseId,
                    "corpse_not_found",
                    "The corpse is no longer active on this Shard."));

            Assert.That(state.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(state.Error.Code, Is.EqualTo("corpse_invalidated"));
            Assert.That(state.Notice, Is.Empty);
        }

        // The worker sends an open or refresh snapshot after its result, so a refresh that
        // committed before the closure is followed by a snapshot of the closed view.
        [Test]
        public void LateSnapshotOfAClosedViewDoesNotReopenIt()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            OpenView(state, corpseId);
            var refresh = Begin(state, RealtimeCorpseInteractionKind.Refresh, corpseId);
            state.ApplyClosure(corpseId, "corpse_expired", "The corpse expired and is no longer available.");
            AssertCompletes(state, Succeeded(refresh, RealtimeCorpseInteractionKind.Refresh, corpseId));

            var containers = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            foreach (var chunk in CreateSnapshot(corpseId, Guid.NewGuid(), containers, 1, 2, 1))
            {
                Assert.That(state.ApplyViewChunk(chunk), Is.EqualTo(CorpseStateApplyResult.Ignored));
            }

            Assert.That(state.ActiveView, Is.Null);
            Assert.That(state.Notice, Is.EqualTo("The corpse expired and is no longer available."));
        }

        [Test]
        public void ClosureDuringAPendingOpenKeepsTheOpenCorrelated()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            var open = Begin(state, RealtimeCorpseInteractionKind.Open, corpseId);

            Assert.That(
                state.ApplyClosure(corpseId, "corpse_expired", "The corpse expired and is no longer available."),
                Is.True);
            Assert.That(state.PendingOperationId, Is.EqualTo(open));

            AssertCompletes(state, Succeeded(open, RealtimeCorpseInteractionKind.Open, corpseId));
            var containers = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            foreach (var chunk in CreateSnapshot(corpseId, Guid.NewGuid(), containers, 1, 1, 1))
            {
                Assert.That(state.ApplyViewChunk(chunk), Is.EqualTo(CorpseStateApplyResult.Ignored));
            }

            Assert.That(state.ActiveView, Is.Null);
            Assert.That(state.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(state.Notice, Is.EqualTo("The corpse expired and is no longer available."));
        }

        [Test]
        public void LateOwnCloseAndItsTrailingClosureKeepTheEmptiedNotice()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            OpenView(state, corpseId);
            var close = Begin(state, RealtimeCorpseInteractionKind.Close, corpseId);
            state.ApplyClosure(corpseId, "corpse_emptied", "The corpse is empty.");

            AssertCompletes(state, Succeeded(close, RealtimeCorpseInteractionKind.Close, corpseId));
            Assert.That(
                state.ApplyClosure(corpseId, "corpse_view_closed", "The corpse view was closed."),
                Is.False);

            Assert.That(state.ActiveView, Is.Null);
            Assert.That(state.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(state.Error, Is.Null);
            Assert.That(state.Notice, Is.EqualTo("The corpse is empty."));
        }

        [Test]
        public void ClosureOfAnotherCorpseIsIgnored()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            OpenView(state, corpseId);

            Assert.That(
                state.ApplyClosure(Guid.NewGuid(), "corpse_expired", "The corpse expired."),
                Is.False);
            Assert.That(state.ApplyClosure(Guid.Empty, "corpse_expired", "The corpse expired."), Is.False);

            Assert.That(state.ActiveView.CorpseId, Is.EqualTo(corpseId));
            Assert.That(state.Notice, Is.Empty);
            Assert.That(state.Error, Is.Null);
        }

        [Test]
        public void RejectedOpenForgetsTheCorpse()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            var open = Begin(state, RealtimeCorpseInteractionKind.Open, corpseId);

            AssertCompletes(
                state,
                Rejected(
                    open,
                    RealtimeCorpseInteractionKind.Open,
                    corpseId,
                    "corpse_out_of_range",
                    "Move closer to the corpse before interacting with it."));

            Assert.That(state.Status, Is.EqualTo(CorpseClientStatus.Error));
            Assert.That(state.Error.Code, Is.EqualTo("corpse_out_of_range"));
            var containers = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            Assert.That(
                state.ApplyViewChunk(CreateSnapshot(corpseId, Guid.NewGuid(), containers, 1, 1, 1)[0]),
                Is.EqualTo(CorpseStateApplyResult.Ignored));
            Assert.That(state.ApplyClosure(corpseId, "corpse_expired", "The corpse expired."), Is.False);
            Assert.That(state.Error.Code, Is.EqualTo("corpse_out_of_range"));
        }

        [Test]
        public void ResultThatAnswersNoPendingOperationIsAConflict()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            OpenView(state, corpseId);

            Assert.That(state.TryComplete(null, out var error), Is.False);
            Assert.That(error, Is.Not.Empty);
            Assert.That(
                state.TryComplete(
                    Succeeded(Guid.NewGuid(), RealtimeCorpseInteractionKind.Refresh, corpseId),
                    out _),
                Is.False);

            var refresh = Begin(state, RealtimeCorpseInteractionKind.Refresh, corpseId);
            Assert.That(
                state.TryComplete(
                    Succeeded(Guid.NewGuid(), RealtimeCorpseInteractionKind.Refresh, corpseId),
                    out _),
                Is.False);
            Assert.That(
                state.TryComplete(
                    Succeeded(refresh, RealtimeCorpseInteractionKind.LootItem, corpseId),
                    out _),
                Is.False);
            Assert.That(
                state.TryComplete(
                    Succeeded(refresh, RealtimeCorpseInteractionKind.Refresh, Guid.NewGuid()),
                    out _),
                Is.False);
            Assert.That(state.PendingOperationId, Is.EqualTo(refresh));

            AssertCompletes(state, Succeeded(refresh, RealtimeCorpseInteractionKind.Refresh, corpseId));
            Assert.That(
                state.TryComplete(
                    Succeeded(refresh, RealtimeCorpseInteractionKind.Refresh, corpseId),
                    out _),
                Is.False);
        }

        [Test]
        public void ViewStateDuringAPendingOperationKeepsTheStateBusy()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var containers = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            CompleteOpen(state, corpseId);
            foreach (var chunk in CreateSnapshot(corpseId, itemId, containers, 5, 5, 1))
            {
                state.ApplyViewChunk(chunk);
            }

            var loot = Begin(state, RealtimeCorpseInteractionKind.LootItem, corpseId);
            var otherViewerDelta = CreateChunk(
                Guid.NewGuid(),
                RealtimeCorpseViewUpdateKind.Delta,
                corpseId,
                5,
                6,
                0,
                1,
                "general_inventory",
                containers[0],
                2,
                false,
                1,
                new[] { CreateSlot(0, itemId, 3, 2) });

            Assert.That(state.ApplyViewChunk(otherViewerDelta), Is.EqualTo(CorpseStateApplyResult.Applied));
            Assert.That(state.ActiveView.Revision, Is.EqualTo(6));
            Assert.That(state.PendingOperationId, Is.EqualTo(loot));
            Assert.That(state.Status, Is.EqualTo(CorpseClientStatus.Busy));
            Assert.That(state.CanMutate, Is.False);
        }

        [Test]
        public void SessionResetDropsAnOperationThatOutlivedItsView()
        {
            var state = new CorpseClientState();
            var corpseId = Guid.NewGuid();
            OpenView(state, corpseId);
            var loot = Begin(state, RealtimeCorpseInteractionKind.LootItem, corpseId);
            state.ApplyClosure(corpseId, "corpse_emptied", "The corpse is empty.");

            state.Reset();

            Assert.That(state.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(state.Status, Is.EqualTo(CorpseClientStatus.Unavailable));
            Assert.That(state.Notice, Is.Empty);
            Assert.That(
                state.TryComplete(
                    Succeeded(loot, RealtimeCorpseInteractionKind.LootItem, corpseId),
                    out _),
                Is.False);
        }

        // The worker answers an open before it sends the snapshot of the opened view.
        private static void CompleteOpen(CorpseClientState state, Guid corpseId)
        {
            var open = Begin(state, RealtimeCorpseInteractionKind.Open, corpseId);
            AssertCompletes(state, Succeeded(open, RealtimeCorpseInteractionKind.Open, corpseId));
        }

        private static void OpenView(CorpseClientState state, Guid corpseId)
        {
            CompleteOpen(state, corpseId);
            var containers = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            foreach (var chunk in CreateSnapshot(corpseId, Guid.NewGuid(), containers, 1, 1, 1))
            {
                state.ApplyViewChunk(chunk);
            }

            Assert.That(state.ActiveView, Is.Not.Null);
        }

        private static Guid Begin(
            CorpseClientState state,
            RealtimeCorpseInteractionKind operationKind,
            Guid corpseId)
        {
            var operationId = Guid.NewGuid();
            state.BeginOperation(operationId, operationKind, corpseId);
            return operationId;
        }

        private static void AssertCompletes(
            CorpseClientState state,
            RealtimeCorpseInteractionResult result)
        {
            Assert.That(state.TryComplete(result, out var error), Is.True, error);
        }

        private static RealtimeCorpseInteractionResult Succeeded(
            Guid operationId,
            RealtimeCorpseInteractionKind operationKind,
            Guid corpseId,
            bool requiresInventoryRefresh = false)
        {
            return new RealtimeCorpseInteractionResult(
                operationId,
                operationKind,
                corpseId,
                true,
                false,
                requiresInventoryRefresh,
                new RealtimeCarryState(3, 0, 100),
                null);
        }

        private static RealtimeCorpseInteractionResult Rejected(
            Guid operationId,
            RealtimeCorpseInteractionKind operationKind,
            Guid corpseId,
            string code,
            string message)
        {
            return new RealtimeCorpseInteractionResult(
                operationId,
                operationKind,
                corpseId,
                false,
                false,
                false,
                new RealtimeCarryState(3, 0, 100),
                new RealtimeError(code, message));
        }

        private static RealtimeCorpsePresence CreatePresence(Guid corpseId, string name)
        {
            return new RealtimeCorpsePresence(
                corpseId,
                1f,
                2f,
                3f,
                ExpiresAtUnixMilliseconds,
                false,
                name,
                "corpse.remains");
        }

        private static RealtimeCorpseViewStateChunk[] CreateSnapshot(
            Guid corpseId,
            Guid itemId,
            Guid[] containers,
            int quantity,
            long revision,
            int generalCapacity)
        {
            var updateId = Guid.NewGuid();
            var generalSlots = Enumerable.Range(0, generalCapacity)
                .Select(index => index == 0
                    ? CreateSlot(index, itemId, quantity, 1)
                    : new RealtimeCorpseSlot(index, "general", null))
                .ToArray();
            return new[]
            {
                CreateChunk(
                    updateId,
                    RealtimeCorpseViewUpdateKind.Snapshot,
                    corpseId,
                    -1,
                    revision,
                    0,
                    3,
                    "general_inventory",
                    containers[0],
                    1,
                    true,
                    generalCapacity,
                    generalSlots),
                CreateChunk(
                    updateId,
                    RealtimeCorpseViewUpdateKind.Snapshot,
                    corpseId,
                    -1,
                    revision,
                    1,
                    3,
                    "equipment",
                    containers[1],
                    1,
                    true,
                    1,
                    new[]
                    {
                        new RealtimeCorpseSlot(
                            0,
                            "general",
                            Array.Empty<string>(),
                            "head",
                            null)
                    }),
                CreateChunk(
                    updateId,
                    RealtimeCorpseViewUpdateKind.Snapshot,
                    corpseId,
                    -1,
                    revision,
                    2,
                    3,
                    "bag",
                    containers[2],
                    1,
                    true,
                    1,
                    new[] { new RealtimeCorpseSlot(0, "general", null) })
            };
        }

        private static RealtimeCorpseViewStateChunk CreateChunk(
            Guid updateId,
            RealtimeCorpseViewUpdateKind updateKind,
            Guid corpseId,
            long baseRevision,
            long revision,
            ushort chunkIndex,
            ushort chunkCount,
            string sectionKind,
            Guid containerId,
            long containerRevision,
            bool replaceSection,
            int slotCapacity,
            RealtimeCorpseSlot[] slots)
        {
            return new RealtimeCorpseViewStateChunk(
                updateId,
                updateKind,
                corpseId,
                baseRevision,
                revision,
                ExpiresAtUnixMilliseconds,
                "Fallen Hero",
                "corpse.remains",
                chunkIndex,
                chunkCount,
                sectionKind,
                containerId,
                containerRevision,
                replaceSection,
                slotCapacity,
                0,
                slots);
        }

        private static RealtimeCorpseSlot CreateSlot(
            int slotIndex,
            Guid itemId,
            int quantity,
            long revision)
        {
            return new RealtimeCorpseSlot(
                slotIndex,
                "general",
                new RealtimeCorpseItem(
                    itemId,
                    "fixture.potion",
                    quantity,
                    revision,
                    Guid.Empty,
                    0));
        }

        private static CorpseLootItem FindItem(CorpseClientState state, Guid itemId)
        {
            Assert.That(
                state.ActiveView.TryFindItem(itemId, out var item, out _, out _),
                Is.True);
            return item;
        }
    }
}
