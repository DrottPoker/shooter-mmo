using System;
using NUnit.Framework;
using ShooterMmo.Ui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ShooterMmo.Tests.EditMode
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
                Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
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
