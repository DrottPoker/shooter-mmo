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
                    fixture.Highlight,
                    Color.green,
                    Color.red,
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
                Assert.That(target.ShowsValidDrop, Is.True);
                Assert.That(fixture.Highlight.enabled, Is.True);
                Assert.That(fixture.Highlight.color, Is.EqualTo(Color.green));
                target.OnDrop(eventData);
                source.OnEndDrag(eventData);

                Assert.That(received, Is.SameAs(payload));
                Assert.That(received.ItemInstanceId, Is.EqualTo(itemId));
                Assert.That(fixture.Coordinator.IsDragging, Is.False);
                Assert.That(fixture.Coordinator.ActivePayload, Is.Null);
                Assert.That(fixture.Highlight.enabled, Is.False);
                Assert.That(target.ShowsValidDrop, Is.Null);
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
                    fixture.Highlight,
                    Color.green,
                    Color.red,
                    (InventoryDragPayload candidate, out string reason) =>
                    {
                        reason = "Recovery can be claimed only to Permanent Inventory or Bank.";
                        return false;
                    },
                    _ => operationInvoked = true,
                    reason => rejection = reason);
                var eventData = new PointerEventData(fixture.EventSystem);

                source.OnBeginDrag(eventData);
                target.OnPointerEnter(eventData);
                Assert.That(target.ShowsValidDrop, Is.False);
                Assert.That(fixture.Highlight.color, Is.EqualTo(Color.red));
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

                source.OnEndDrag(OverInterface(fixture));

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
                source.OnEndDrag(OverInterface(fixture));
            }
            finally
            {
                fixture.Dispose();
            }
        }

        // Shift drags half of a stack, rounded down: the payload carries the half, and the ghost
        // shows it.
        [Test]
        public void ShiftDragCarriesHalfOfTheStack()
        {
            var fixture = CreateFixture();
            var texture = new Texture2D(4, 4);
            var icon = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f));
            try
            {
                var itemId = Guid.NewGuid();
                var source = fixture.Source.AddComponent<InventoryDragSource>();
                source.Configure(
                    fixture.Coordinator,
                    InventoryDragPayload.ForItem(itemId),
                    "Iron Ore  x21",
                    icon,
                    "21",
                    true,
                    10);
                source.PartialOverride = true;

                source.OnBeginDrag(new PointerEventData(fixture.EventSystem));

                var payload = fixture.Coordinator.ActivePayload;
                Assert.That(payload.IsPartial, Is.True);
                Assert.That(payload.Quantity, Is.EqualTo(10));
                Assert.That(payload.ItemInstanceId, Is.EqualTo(itemId));
                var ghost = fixture.Coordinator.transform.Find(InventoryDragCoordinator.GhostName);
                Assert.That(ghost.Find("Quantity").GetComponent<TMP_Text>().text, Is.EqualTo("10"));
                source.OnEndDrag(OverInterface(fixture));

                // Without Shift the whole stack moves.
                source.PartialOverride = false;
                source.OnBeginDrag(new PointerEventData(fixture.EventSystem));
                Assert.That(fixture.Coordinator.ActivePayload.Quantity, Is.Null);
                source.OnEndDrag(OverInterface(fixture));

                // A slot without a half drags it whole even with Shift.
                source.PartialOverride = true;
                source.Configure(fixture.Coordinator, InventoryDragPayload.ForItem(itemId), "Ring", icon, string.Empty, true);
                source.OnBeginDrag(new PointerEventData(fixture.EventSystem));
                Assert.That(fixture.Coordinator.ActivePayload.IsPartial, Is.False);
                source.OnEndDrag(OverInterface(fixture));
                Assert.That(InventoryDragPayload.ForRecoveryDelivery(Guid.NewGuid()).WithQuantity(3).IsPartial, Is.False);
                Assert.Throws<ArgumentOutOfRangeException>(() => InventoryDragPayload.ForItem(itemId).WithQuantity(0));
            }
            finally
            {
                fixture.Dispose();
                UnityEngine.Object.DestroyImmediate(icon);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        // A carried part stays on the pointer: a slot that refuses it says why and keeps it
        // carried, a slot that takes it ends the carry, and the pointer stays captured for the
        // frame the carry ended in.
        [Test]
        public void ACarriedPartStaysUntilASlotTakesIt()
        {
            var fixture = CreateFixture();
            try
            {
                var started = 0;
                var ended = 0;
                fixture.Coordinator.DragStarted += _ => started++;
                fixture.Coordinator.DragEnded += () => ended++;
                var accepts = false;
                InventoryDragPayload received = null;
                string rejection = null;
                var target = fixture.Target.AddComponent<InventoryDropTarget>();
                target.Configure(
                    fixture.Coordinator,
                    fixture.Highlight,
                    Color.green,
                    Color.red,
                    (InventoryDragPayload candidate, out string reason) =>
                    {
                        reason = "The slot is taken.";
                        return accepts;
                    },
                    candidate => received = candidate,
                    reason => rejection = reason);
                var part = InventoryDragPayload.ForItem(Guid.NewGuid()).WithQuantity(4);

                Assert.That(fixture.Coordinator.BeginCarry(part, "Iron Ore", null, "4", Vector2.zero), Is.True);
                Assert.That(fixture.Coordinator.IsDragging, Is.True);
                Assert.That(fixture.Coordinator.IsCarrying, Is.True);
                Assert.That(fixture.Coordinator.CapturesPointer, Is.True);
                Assert.That(started, Is.EqualTo(1));
                Assert.That(fixture.Coordinator.BeginCarry(part, "Iron Ore", null, "4", Vector2.zero), Is.False);

                target.DropCarried();
                Assert.That(rejection, Is.EqualTo("The slot is taken."));
                Assert.That(received, Is.Null);
                Assert.That(fixture.Coordinator.IsCarrying, Is.True);

                accepts = true;
                target.DropCarried();
                Assert.That(received, Is.SameAs(part));
                Assert.That(fixture.Coordinator.IsDragging, Is.False);
                Assert.That(fixture.Coordinator.IsCarrying, Is.False);
                Assert.That(fixture.Coordinator.CapturesPointer, Is.True, "the click that ended the carry");
                Assert.That(ended, Is.EqualTo(1));

                // Without a carried part a click puts nothing down.
                received = null;
                target.DropCarried();
                Assert.That(received, Is.Null);
            }
            finally
            {
                fixture.Dispose();
            }
        }

        // Right-click moves, Shift+right-click moves half, and Ctrl+click splits. While a part
        // is carried, a click puts it on the slot and a right-click puts it back.
        [Test]
        public void SlotClicksChooseTheirMoveAndPlaceACarriedPart()
        {
            var fixture = CreateFixture();
            try
            {
                var calls = string.Empty;
                var target = fixture.Target.AddComponent<InventoryDropTarget>();
                target.Configure(
                    fixture.Coordinator,
                    fixture.Highlight,
                    Color.green,
                    Color.red,
                    (InventoryDragPayload candidate, out string reason) =>
                    {
                        reason = string.Empty;
                        return true;
                    },
                    _ => calls += "drop ",
                    _ => Assert.Fail("The carried part was refused."));
                var clicks = fixture.Target.AddComponent<InventorySlotClicks>();
                clicks.Configure(
                    fixture.Coordinator,
                    target,
                    () => calls += "right ",
                    () => calls += "half ",
                    () => calls += "split ");
                clicks.ShiftOverride = false;
                clicks.ControlOverride = false;
                var right = new PointerEventData(fixture.EventSystem) { button = PointerEventData.InputButton.Right };
                var left = new PointerEventData(fixture.EventSystem) { button = PointerEventData.InputButton.Left };

                clicks.OnPointerClick(right);
                clicks.OnPointerClick(left);
                clicks.ShiftOverride = true;
                clicks.OnPointerClick(right);
                clicks.ControlOverride = true;
                clicks.OnPointerClick(left);
                Assert.That(calls, Is.EqualTo("right half split "));

                // Without its own half move, Shift+right-click does what right-click does.
                clicks.Configure(fixture.Coordinator, target, () => calls += "right ", null, null);
                calls = string.Empty;
                clicks.OnPointerClick(right);
                Assert.That(calls, Is.EqualTo("right "));

                var part = InventoryDragPayload.ForItem(Guid.NewGuid()).WithQuantity(2);
                calls = string.Empty;
                fixture.Coordinator.BeginCarry(part, "Ore", null, "2", Vector2.zero);
                clicks.OnPointerClick(right);
                Assert.That(fixture.Coordinator.IsCarrying, Is.False);
                fixture.Coordinator.BeginCarry(part, "Ore", null, "2", Vector2.zero);
                clicks.OnPointerClick(left);
                Assert.That(calls, Is.EqualTo("drop "));
                Assert.That(fixture.Coordinator.IsCarrying, Is.False);
            }
            finally
            {
                fixture.Dispose();
            }
        }

        // A drag released over nothing of the interface drops outside every window; released
        // over a window it only ends.
        [Test]
        public void ADragReleasedOverNoInterfaceDropsOutside()
        {
            var fixture = CreateFixture();
            try
            {
                InventoryDragPayload outside = null;
                fixture.Coordinator.DroppedOutside += payload => outside = payload;
                var payload = InventoryDragPayload.ForItem(Guid.NewGuid());
                var source = fixture.Source.AddComponent<InventoryDragSource>();
                source.Configure(fixture.Coordinator, payload, "Ore", null, string.Empty, true);

                source.OnBeginDrag(new PointerEventData(fixture.EventSystem));
                source.OnEndDrag(OverInterface(fixture));
                Assert.That(outside, Is.Null);

                source.OnBeginDrag(new PointerEventData(fixture.EventSystem));
                source.OnEndDrag(new PointerEventData(fixture.EventSystem));
                Assert.That(outside, Is.SameAs(payload));
                Assert.That(fixture.Coordinator.IsDragging, Is.False);
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
            Assert.That(payload.WithQuantity(2).CorpseId, Is.EqualTo(corpseId));
            Assert.Throws<ArgumentException>(() =>
                InventoryDragPayload.ForCorpseItem(Guid.Empty, itemId));
        }

        // A release over a window: the event system's raycast found an interface object.
        private static PointerEventData OverInterface(DragFixture fixture)
        {
            return new PointerEventData(fixture.EventSystem)
            {
                pointerCurrentRaycast = new RaycastResult { gameObject = fixture.Target }
            };
        }

        private static DragFixture CreateFixture()
        {
            var eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));
            var canvasObject = new GameObject(
                "Canvas",
                typeof(RectTransform),
                typeof(Canvas));
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var theme = ScriptableObject.CreateInstance<UiTheme>();
            theme.TitleFont = TMP_Settings.defaultFontAsset;
            theme.BodyFont = TMP_Settings.defaultFontAsset;
            var coordinator = canvasObject.AddComponent<InventoryDragCoordinator>();
            coordinator.Configure(canvasObject.GetComponent<RectTransform>(), theme);
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
            var highlight = new GameObject("Highlight", typeof(RectTransform), typeof(Image));
            highlight.transform.SetParent(target.transform, false);
            return new DragFixture(
                eventSystemObject.GetComponent<EventSystem>(),
                canvasObject,
                source,
                target,
                highlight.GetComponent<Image>(),
                coordinator,
                theme);
        }

        private sealed class DragFixture : IDisposable
        {
            private readonly GameObject eventSystemObject;
            private readonly GameObject canvasObject;
            private readonly UiTheme theme;

            public DragFixture(
                EventSystem eventSystem,
                GameObject canvas,
                GameObject source,
                GameObject target,
                Image highlight,
                InventoryDragCoordinator coordinator,
                UiTheme dragTheme)
            {
                EventSystem = eventSystem;
                eventSystemObject = eventSystem.gameObject;
                canvasObject = canvas;
                Source = source;
                Target = target;
                Highlight = highlight;
                Coordinator = coordinator;
                theme = dragTheme;
            }

            public EventSystem EventSystem { get; }

            public GameObject Source { get; }

            public GameObject Target { get; }

            public Image Highlight { get; }

            public InventoryDragCoordinator Coordinator { get; }

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(canvasObject);
                UnityEngine.Object.DestroyImmediate(eventSystemObject);
                UnityEngine.Object.DestroyImmediate(theme);
            }
        }
    }
}
