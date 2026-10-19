using System;
using System.Collections.Generic;
using Mmorpg.Client.Items;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;
using static Mmorpg.Client.Tests.EditMode.CorpseProtocolFixtures;

namespace Mmorpg.Client.Tests.EditMode
{
    // Drives CorpseClientController through a fake realtime channel to pin which refreshes
    // it starts on its own and what they leave visible.
    public sealed class CorpseClientControllerTests
    {
        private const long ViewRevision = 5;

        private GameObject host;
        private FakeCorpseChannel channel;
        private CorpseClientController controller;
        private Guid corpseId;
        private Guid itemId;
        private Guid[] containers;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject(nameof(CorpseClientControllerTests));
            channel = new FakeCorpseChannel();
            controller = host.AddComponent<CorpseClientController>();
            controller.Initialize(channel, null);
            corpseId = Guid.NewGuid();
            itemId = Guid.NewGuid();
            containers = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(host);
        }

        [Test]
        public void RejectionThatRequiresARefreshStaysVisibleThroughTheAutomaticRefresh()
        {
            OpenView();
            var move = MoveItemToSecondSlot();

            channel.Complete(
                Rejected(
                    move.OperationId,
                    RealtimeCorpseInteractionKind.MoveItem,
                    corpseId,
                    "item_state_conflict",
                    "The item changed.",
                    requiresCorpseRefresh: true));

            var refresh = channel.LastSent;
            Assert.That(channel.Sent.Count, Is.EqualTo(3));
            Assert.That(refresh.OperationKind, Is.EqualTo(RealtimeCorpseInteractionKind.Refresh));
            Assert.That(refresh.CorpseId, Is.EqualTo(corpseId));
            Assert.That(refresh.ExpectedCorpseRevision, Is.EqualTo(ViewRevision));
            Assert.That(controller.State.PendingOperationId, Is.EqualTo(refresh.OperationId));
            Assert.That(controller.State.IsAutomaticRefreshPending, Is.True);
            Assert.That(controller.State.Status, Is.EqualTo(CorpseClientStatus.Busy));
            Assert.That(controller.State.Error.Code, Is.EqualTo("item_state_conflict"));

            // The worker sends the refreshed snapshot after the result.
            channel.Complete(
                Succeeded(refresh.OperationId, RealtimeCorpseInteractionKind.Refresh, corpseId));
            channel.Deliver(CreateSnapshot(corpseId, itemId, containers, 5, ViewRevision + 1, 2));

            Assert.That(channel.Sent.Count, Is.EqualTo(3));
            Assert.That(controller.State.ActiveView.Revision, Is.EqualTo(ViewRevision + 1));
            Assert.That(controller.State.CanMutate, Is.True);
            Assert.That(controller.State.Error.Code, Is.EqualTo("item_state_conflict"));

            Assert.That(controller.TryRefresh(out var error), Is.True, error);

            Assert.That(controller.State.IsAutomaticRefreshPending, Is.False);
            Assert.That(controller.State.Error, Is.Null);
            Assert.That(channel.Disconnects, Is.Empty);
        }

        [Test]
        public void RejectedAutomaticRefreshDoesNotRefreshAgain()
        {
            OpenView();
            var move = MoveItemToSecondSlot();
            channel.Complete(
                Rejected(
                    move.OperationId,
                    RealtimeCorpseInteractionKind.MoveItem,
                    corpseId,
                    "item_state_conflict",
                    "The item changed.",
                    requiresCorpseRefresh: true));
            var refresh = channel.LastSent;

            channel.Complete(
                Rejected(
                    refresh.OperationId,
                    RealtimeCorpseInteractionKind.Refresh,
                    corpseId,
                    "auth_service_unavailable",
                    "GameService is unavailable.",
                    requiresCorpseRefresh: true));

            Assert.That(channel.Sent.Count, Is.EqualTo(3));
            Assert.That(controller.State.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(controller.State.CanMutate, Is.True);
            Assert.That(controller.State.Error.Code, Is.EqualTo("auth_service_unavailable"));
            Assert.That(channel.Disconnects, Is.Empty);
        }

        [Test]
        public void StaleDeltaRefreshKeepsAnEarlierRejection()
        {
            OpenView();
            var move = MoveItemToSecondSlot();
            channel.Complete(
                Rejected(
                    move.OperationId,
                    RealtimeCorpseInteractionKind.MoveItem,
                    corpseId,
                    "item_stack_incompatible",
                    "The items cannot share a stack."));
            Assert.That(channel.Sent.Count, Is.EqualTo(2));

            channel.Deliver(CreateStaleDelta());

            Assert.That(channel.Sent.Count, Is.EqualTo(3));
            Assert.That(channel.LastSent.OperationKind, Is.EqualTo(RealtimeCorpseInteractionKind.Refresh));
            Assert.That(controller.State.IsAutomaticRefreshPending, Is.True);
            Assert.That(controller.State.Error.Code, Is.EqualTo("item_stack_incompatible"));
        }

        [Test]
        public void DeltaThatWentStaleDuringARejectedOperationRefreshesTheView()
        {
            OpenView();
            var move = MoveItemToSecondSlot();
            channel.Deliver(CreateStaleDelta());
            Assert.That(channel.Sent.Count, Is.EqualTo(2));

            channel.Complete(
                Rejected(
                    move.OperationId,
                    RealtimeCorpseInteractionKind.MoveItem,
                    corpseId,
                    "item_stack_incompatible",
                    "The items cannot share a stack."));

            Assert.That(channel.Sent.Count, Is.EqualTo(3));
            Assert.That(channel.LastSent.OperationKind, Is.EqualTo(RealtimeCorpseInteractionKind.Refresh));
            Assert.That(controller.State.IsAutomaticRefreshPending, Is.True);
            Assert.That(controller.State.Error.Code, Is.EqualTo("item_stack_incompatible"));
        }

        [Test]
        public void ClosureDuringTheAutomaticRefreshEndsItWithoutAnotherRefresh()
        {
            OpenView();
            var move = MoveItemToSecondSlot();
            channel.Complete(
                Rejected(
                    move.OperationId,
                    RealtimeCorpseInteractionKind.MoveItem,
                    corpseId,
                    "item_already_looted",
                    "The item was already looted.",
                    requiresCorpseRefresh: true));
            var refresh = channel.LastSent;

            channel.Close(corpseId, "corpse_emptied", "The corpse is empty.");
            channel.Complete(
                Rejected(
                    refresh.OperationId,
                    RealtimeCorpseInteractionKind.Refresh,
                    corpseId,
                    "corpse_not_found",
                    "The corpse is no longer active on this Shard."));

            Assert.That(channel.Sent.Count, Is.EqualTo(3));
            Assert.That(controller.State.ActiveView, Is.Null);
            Assert.That(controller.State.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(controller.State.Error, Is.Null);
            Assert.That(controller.State.Notice, Is.EqualTo("The corpse is empty."));
        }

        [Test]
        public void SessionEndClearsTheRejectionAndTheAutomaticRefresh()
        {
            OpenView();
            var move = MoveItemToSecondSlot();
            channel.Complete(
                Rejected(
                    move.OperationId,
                    RealtimeCorpseInteractionKind.MoveItem,
                    corpseId,
                    "item_state_conflict",
                    "The item changed.",
                    requiresCorpseRefresh: true));

            channel.EndSession();

            Assert.That(controller.State.Status, Is.EqualTo(CorpseClientStatus.Unavailable));
            Assert.That(controller.State.PendingOperationId, Is.EqualTo(Guid.Empty));
            Assert.That(controller.State.IsAutomaticRefreshPending, Is.False);
            Assert.That(controller.State.Error, Is.Null);
        }

        // Opens the corpse the way the worker answers: presence first, then the open result
        // and its snapshot.
        private void OpenView()
        {
            channel.DeliverPresence(CreatePresence(corpseId, "Fallen Hero"));
            Assert.That(controller.TryOpen(corpseId, out var error), Is.True, error);
            channel.Complete(
                Succeeded(
                    channel.LastSent.OperationId,
                    RealtimeCorpseInteractionKind.Open,
                    corpseId));
            channel.Deliver(CreateSnapshot(corpseId, itemId, containers, 5, ViewRevision, 2));
            Assert.That(controller.State.ActiveView.Revision, Is.EqualTo(ViewRevision));
            Assert.That(controller.State.CanMutate, Is.True);
        }

        private RealtimeCorpseInteractionIntent MoveItemToSecondSlot()
        {
            var view = controller.State.ActiveView;
            Assert.That(view.TryFindItem(itemId, out var item, out var section, out _), Is.True);
            Assert.That(section.TryGetSlot(1, out var destination), Is.True);
            Assert.That(
                controller.TryMoveWithinCorpse(item, section, destination, item.Quantity, out var error),
                Is.True,
                error);
            Assert.That(channel.LastSent.OperationKind, Is.EqualTo(RealtimeCorpseInteractionKind.MoveItem));
            return channel.LastSent;
        }

        // A delta on a revision the client never saw, so the view must be refreshed.
        private RealtimeCorpseViewStateChunk CreateStaleDelta()
        {
            return CreateChunk(
                Guid.NewGuid(),
                RealtimeCorpseViewUpdateKind.Delta,
                corpseId,
                ViewRevision + 2,
                ViewRevision + 3,
                0,
                1,
                "general_inventory",
                containers[0],
                2,
                false,
                2,
                new[] { CreateSlot(0, itemId, 3, 2) });
        }

        private sealed class FakeCorpseChannel : IRealtimeCorpseChannel
        {
            private uint presenceSequence;

            public event Action<RealtimeCorpsePresenceSnapshotChunk> CorpsePresenceChunkReceived;

            public event Action<RealtimeCorpseInteractionResult> CorpseInteractionCompleted;

            public event Action<RealtimeCorpseViewStateChunk> CorpseViewStateChunkReceived;

            public event Action<RealtimeCorpseViewClosed> CorpseViewClosed;

            public event Action<RealtimeClientError> SessionEnded;

            public bool IsJoined { get; private set; } = true;

            public List<RealtimeCorpseInteractionIntent> Sent { get; } =
                new List<RealtimeCorpseInteractionIntent>();

            public List<string> Disconnects { get; } = new List<string>();

            public RealtimeCorpseInteractionIntent LastSent
            {
                get { return Sent[Sent.Count - 1]; }
            }

            public bool TrySendCorpseInteraction(RealtimeCorpseInteractionIntent intent)
            {
                Sent.Add(intent);
                return true;
            }

            public void DisconnectForClientFailure(string code, string message)
            {
                Disconnects.Add(code);
            }

            public void DeliverPresence(params RealtimeCorpsePresence[] corpses)
            {
                CorpsePresenceChunkReceived?.Invoke(
                    new RealtimeCorpsePresenceSnapshotChunk(++presenceSequence, 0, 1, corpses));
            }

            public void Complete(RealtimeCorpseInteractionResult result)
            {
                CorpseInteractionCompleted?.Invoke(result);
            }

            public void Deliver(params RealtimeCorpseViewStateChunk[] chunks)
            {
                foreach (var chunk in chunks)
                {
                    CorpseViewStateChunkReceived?.Invoke(chunk);
                }
            }

            public void Close(Guid corpseId, string code, string message)
            {
                CorpseViewClosed?.Invoke(new RealtimeCorpseViewClosed(corpseId, code, message));
            }

            public void EndSession()
            {
                IsJoined = false;
                SessionEnded?.Invoke(
                    new RealtimeClientError(
                        RealtimeClientErrorKind.Network,
                        "session_ended",
                        "The realtime session ended."));
            }
        }
    }
}
