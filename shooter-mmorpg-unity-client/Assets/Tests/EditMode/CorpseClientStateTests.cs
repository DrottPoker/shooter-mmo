using System;
using System.Linq;
using NUnit.Framework;
using ShooterMmo.GameProtocol;
using ShooterMmo.Items;

namespace ShooterMmo.Tests.EditMode
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

            Assert.That(state.ApplyViewChunk(snapshot[0]), Is.EqualTo(CorpseStateApplyResult.Waiting));
            Assert.That(state.ActiveView, Is.Null);
            Assert.That(state.ApplyViewChunk(snapshot[1]), Is.EqualTo(CorpseStateApplyResult.Waiting));
            Assert.That(state.ActiveView, Is.Null);
            Assert.That(state.ApplyViewChunk(snapshot[2]), Is.EqualTo(CorpseStateApplyResult.Applied));
            Assert.That(state.ActiveView.Revision, Is.EqualTo(5));
            Assert.That(FindItem(state, itemId).Quantity, Is.EqualTo(5));

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
                new[] { new RealtimeCorpseSlot(0, "general", null) });

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
                "corpse.generic_loot_crate");
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
                    new[] { new RealtimeCorpseSlot(0, "general", null) }),
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
                "corpse.generic_loot_crate",
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
                    "medical.field_dressing",
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
