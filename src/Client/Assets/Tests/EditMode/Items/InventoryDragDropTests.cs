using System;
using Mmorpg.Client.Ui;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class InventoryDragDropTests
    {
        [Test]
        public void ValidDropTransfersTypedPayloadAndClearsCoordinator()
        {
            var fixture = CreateFixture();
            try
            {
                var itemId = Guid.NewGuid();
                var payload = InventoryDragPayload.ForItem(itemId);
                InventoryDragPayload received = null;
                var source = fixture.Source.AddComponent<InventoryDragSource>();
                source.Configure(
                    fixture.Coordinator,
                    payload,
                    "Iron Ore x20",
                    null,
                    "20",
                    true);
                var target = fixture.Target.AddComponent<InventoryDropTarget>();
                target.Configure(
                    fixture.Coordinator,
                    fixture.Target.GetComponent<Image>(),
                    Color.gray,
                    (InventoryDragPayload candidate, out string reason) =>
                    {
                        reason = string.Empty;
                        return candidate.Kind == InventoryDragPayloadKind.Item;
                    },
                    candidate => received = candidate,
                    _ => Assert.Fail("The valid drop was rejected."));
                var eventData = new PointerEventData(fixture.EventSystem)
                {
                    position = new Vector2(25f, 25f)
                };

                source.OnBeginDrag(eventData);
                Assert.That(source.IsDragging, Is.True);
                Assert.That(fixture.Coordinator.IsDragging, Is.True);
                Assert.That(fixture.Coordinator.ActivePayload, Is.SameAs(payload));

                target.OnPointerEnter(eventData);
                target.OnDrop(eventData);
                source.OnEndDrag(eventData);

                Assert.That(received, Is.SameAs(payload));
                Assert.That(received.ItemInstanceId, Is.EqualTo(itemId));
                Assert.That(fixture.Coordinator.IsDragging, Is.False);
                Assert.That(fixture.Coordinator.ActivePayload, Is.Null);
            }
            finally
            {
                fixture.Dispose();
            }
        }

        [Test]
        public void InvalidDropReportsAuthorityReasonWithoutInvokingOperation()
        {
            var fixture = CreateFixture();
            try
            {
                var payload = InventoryDragPayload.ForRecoveryDelivery(Guid.NewGuid());
                var operationInvoked = false;
                string rejection = null;
                var source = fixture.Source.AddComponent<InventoryDragSource>();
                source.Configure(
                    fixture.Coordinator,
                    payload,
                    "Recovery delivery",
                    null,
                    string.Empty,
                    true);
                var target = fixture.Target.AddComponent<InventoryDropTarget>();
                target.Configure(
                    fixture.Coordinator,
                    fixture.Target.GetComponent<Image>(),
                    Color.gray,
                    (InventoryDragPayload candidate, out string reason) =>
                    {
                        reason = "Recovery can be claimed only to Permanent Inventory or Bank.";
                        return false;
                    },
                    _ => operationInvoked = true,
                    reason => rejection = reason);
                var eventData = new PointerEventData(fixture.EventSystem);

                source.OnBeginDrag(eventData);
                target.OnDrop(eventData);

                Assert.That(operationInvoked, Is.False);
                Assert.That(rejection, Does.Contain("Permanent Inventory or Bank"));
                Assert.That(fixture.Coordinator.IsDragging, Is.False);
            }
            finally
            {
                fixture.Dispose();
            }
        }

        // The picked-up item moves as its icon with its quantity, and its slot fades until the
        // drag ends.
        [Test]
        public void IconDragMovesTheIconAndFadesItsSlot()
        {
            var fixture = CreateFixture();
            var texture = new Texture2D(4, 4);
            var icon = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f));
            try
            {
                var source = fixture.Source.AddComponent<InventoryDragSource>();
                source.Configure(
                    fixture.Coordinator,
                    InventoryDragPayload.ForItem(Guid.NewGuid()),
                    "Iron Ore  x20",
                    icon,
                    "20",
                    true);
                var group = fixture.Source.GetComponent<CanvasGroup>();
                Assert.That(group.alpha, Is.EqualTo(1f));

                source.OnBeginDrag(new PointerEventData(fixture.EventSystem) { position = new Vector2(25f, 25f) });

                var ghost = fixture.Coordinator.transform.Find(InventoryDragCoordinator.GhostName);
                Assert.That(ghost, Is.Not.Null);
                Assert.That(ghost.GetComponent<Image>().sprite, Is.SameAs(icon));
                Assert.That(ghost.Find("Quantity").GetComponent<TMP_Text>().text, Is.EqualTo("20"));
                Assert.That(ghost.Find("Label"), Is.Null);
                Assert.That(group.alpha, Is.EqualTo(InventoryDragSource.DraggedAlpha));
                Assert.That(group.blocksRaycasts, Is.False);

                source.OnEndDrag(new PointerEventData(fixture.EventSystem));

                Assert.That(fixture.Coordinator.IsDragging, Is.False);
                Assert.That(fixture.Coordinator.transform.Find(InventoryDragCoordinator.GhostName), Is.Null);
                Assert.That(group.alpha, Is.EqualTo(1f));
                Assert.That(group.blocksRaycasts, Is.True);
            }
            finally
            {
                fixture.Dispose();
                UnityEngine.Object.DestroyImmediate(icon);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        // Without an icon the item moves as its name.
        [Test]
        public void DragWithoutIconMovesTheLabel()
        {
            var fixture = CreateFixture();
            try
            {
                var source = fixture.Source.AddComponent<InventoryDragSource>();
                source.Configure(
                    fixture.Coordinator,
                    InventoryDragPayload.ForItem(Guid.NewGuid()),
                    "Iron Ore  x20",
                    null,
                    "20",
                    true);

                source.OnBeginDrag(new PointerEventData(fixture.EventSystem));

                var ghost = fixture.Coordinator.transform.Find(InventoryDragCoordinator.GhostName);
                Assert.That(ghost, Is.Not.Null);
                Assert.That(ghost.Find("Label").GetComponent<TMP_Text>().text, Is.EqualTo("Iron Ore  x20"));
                Assert.That(ghost.Find("Quantity"), Is.Null);
                source.OnEndDrag(new PointerEventData(fixture.EventSystem));
            }
            finally
            {
                fixture.Dispose();
            }
        }

        [Test]
        public void CorpsePayloadKeepsCorpseAndItemAuthorityIdentity()
        {
            var corpseId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            var payload = InventoryDragPayload.ForCorpseItem(corpseId, itemId);

            Assert.That(payload.Kind, Is.EqualTo(InventoryDragPayloadKind.CorpseItem));
            Assert.That(payload.CorpseId, Is.EqualTo(corpseId));
            Assert.That(payload.ItemInstanceId, Is.EqualTo(itemId));
            Assert.That(payload.RecoveryDeliveryId, Is.EqualTo(Guid.Empty));
            Assert.Throws<ArgumentException>(() =>
                InventoryDragPayload.ForCorpseItem(Guid.Empty, itemId));
        }

        private static DragFixture CreateFixture()
        {
            var eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));
            var canvasObject = new GameObject(
                "Canvas",
                typeof(RectTransform),
                typeof(Canvas));
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var coordinator = canvasObject.AddComponent<InventoryDragCoordinator>();
            coordinator.Configure(
                canvasObject.GetComponent<RectTransform>(),
                TemporaryUiKit.Font);
            var source = new GameObject(
                "Source",
                typeof(RectTransform),
                typeof(Image));
            source.transform.SetParent(canvasObject.transform, false);
            var target = new GameObject(
                "Target",
                typeof(RectTransform),
                typeof(Image));
            target.transform.SetParent(canvasObject.transform, false);
            return new DragFixture(
                eventSystemObject.GetComponent<EventSystem>(),
                canvasObject,
                source,
                target,
                coordinator);
        }

        private sealed class DragFixture : IDisposable
        {
            private readonly GameObject eventSystemObject;
            private readonly GameObject canvasObject;

            public DragFixture(
                EventSystem eventSystem,
                GameObject canvas,
                GameObject source,
                GameObject target,
                InventoryDragCoordinator coordinator)
            {
                EventSystem = eventSystem;
                eventSystemObject = eventSystem.gameObject;
                canvasObject = canvas;
                Source = source;
                Target = target;
                Coordinator = coordinator;
            }

            public EventSystem EventSystem { get; }

            public GameObject Source { get; }

            public GameObject Target { get; }

            public InventoryDragCoordinator Coordinator { get; }

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(canvasObject);
                UnityEngine.Object.DestroyImmediate(eventSystemObject);
            }
        }
    }
}
