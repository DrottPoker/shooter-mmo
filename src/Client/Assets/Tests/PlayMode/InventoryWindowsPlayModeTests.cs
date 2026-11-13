using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.Client.Ui;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Mmorpg.Client.Tests.PlayMode
{
    // Draws a committed snapshot in the inventory windows without a GameService. Without a
    // simulation session every operation stops at the controller, which says why.
    public sealed class InventoryWindowsPlayModeTests
    {
        // Port 9 refuses connections, so no request can reach a real GameService.
        private const string UnreachableGameServiceUrl = "http://127.0.0.1:9";
        private const string AmmunitionSlotId = "ammunition";

        [UnityTest]
        public IEnumerator WindowsDrawPaperDollBlockedSlotCarryMeterSearchAndTooltip()
        {
            using var probe = new WindowProbe();
            yield return probe.Load("Window Probe");
            var windows = probe.Windows;
            var catalog = probe.Catalog;
            var snapshot = probe.Snapshot;

            windows.SetEquipmentOpen(true);
            windows.SetInventoryOpen(true);
            yield return null;
            Canvas.ForceUpdateCanvases();

            // Every equipment slot shows except the hidden Back slot of the cloaks, and the
            // permanent inventory shows every slot of the catalog, 20 in rows of five, without a
            // heading.
            var views = windows.GetComponentsInChildren<InventorySlotView>();
            Assert.That(
                views.Count(view => view.SlotKey.StartsWith("equipment:", StringComparison.Ordinal)),
                Is.EqualTo(catalog.Gameplay.EquipmentSlots.Count(slot => slot.Id != "back")));
            Assert.That(views.Any(view => view.SlotKey == "equipment:back"), Is.False);
            Assert.That(
                views.Count(view => view.SlotKey.StartsWith("inventory:", StringComparison.Ordinal)),
                Is.EqualTo(catalog.Gameplay.Character.InventorySlotCount));
            var grid = View(views, "inventory:0").transform.parent.GetComponent<GridLayoutGroup>();
            Assert.That(grid.constraintCount, Is.EqualTo(5));
            Assert.That(
                Window(windows, InventoryWindows.InventoryWindowName).GetComponentsInChildren<TMP_Text>()
                    .Any(text => text.text.IndexOf("permanent", StringComparison.OrdinalIgnoreCase) >= 0),
                Is.False);
            Assert.That(View(views, "equipment:" + probe.EquippedSlotId).VisualState, Is.EqualTo(InventorySlotVisualState.Occupied));
            Assert.That(View(views, "inventory:0").VisualState, Is.EqualTo(InventorySlotVisualState.Occupied));
            Assert.That(View(views, "inventory:1").VisualState, Is.EqualTo(InventorySlotVisualState.Empty));
            if (probe.BlockedSlotId != null)
            {
                var blocked = View(views, "equipment:" + probe.BlockedSlotId);
                Assert.That(blocked.VisualState, Is.EqualTo(InventorySlotVisualState.Blocked));
                Assert.That(blocked.transform.Find("Content/Lock").gameObject.activeSelf, Is.True);
            }

            // Every slot is a square of one size.
            var sizes = views
                .Select(view => ((RectTransform)view.transform).rect.size)
                .Distinct()
                .ToArray();
            Assert.That(sizes.Length, Is.EqualTo(1), string.Join(", ", sizes));
            Assert.That(sizes[0].x, Is.EqualTo(windows.Theme.SlotSize).Within(0.01f));
            Assert.That(sizes[0].y, Is.EqualTo(sizes[0].x));

            // An occupied slot shows the item's icon in its rarity frame; an empty equipment
            // slot shows its silhouette.
            var occupied = View(views, "inventory:0");
            var icon = occupied.transform.Find("Content/Icon");
            Assert.That(icon.gameObject.activeSelf, Is.True);
            Assert.That(icon.GetComponent<Image>().sprite, Is.SameAs(catalog.LoadIcon(occupied.ItemDefinitionId)));
            Assert.That(occupied.transform.Find("Content/Frame").GetComponent<Image>().color, Is.EqualTo(windows.Theme.RarityCommon));
            Assert.That(occupied.transform.Find("Content/Name").gameObject.activeSelf, Is.False);
            Assert.That(View(views, "inventory:1").transform.Find("Content/Icon").gameObject.activeSelf, Is.False);
            var emptyHead = views.FirstOrDefault(view => view.SlotKey == "equipment:head" && view.VisualState == InventorySlotVisualState.Empty);
            if (emptyHead != null)
            {
                Assert.That(emptyHead.transform.Find("Content/Silhouette").gameObject.activeSelf, Is.True);
            }

            // The equipment slots show no names. The side columns stand as far from both edges
            // of the window, and the row below stands in the middle with its slots as close as
            // those of a column.
            var equipmentWindow = Window(windows, InventoryWindows.EquipmentWindowName);
            Assert.That(
                equipmentWindow.GetComponentsInChildren<TMP_Text>().Select(text => text.text),
                Has.None.EqualTo(InventorySlotPresentation.EquipmentSlotName(catalog, "neck")));
            var frame = WorldRect(equipmentWindow);
            var head = WorldRect(View(views, "equipment:head").transform);
            var shoulders = WorldRect(View(views, "equipment:shoulders").transform);
            var neck = WorldRect(View(views, "equipment:neck").transform);
            Assert.That(head.xMin - frame.xMin, Is.EqualTo(frame.xMax - neck.xMax).Within(0.5f));
            var columnGap = head.yMin - shoulders.yMax;
            var row = new[] { "main_hand", "off_hand", "ammunition", "tool" }
                .Select(slotId => WorldRect(View(views, "equipment:" + slotId).transform))
                .ToArray();
            for (var index = 1; index < row.Length; index++)
            {
                Assert.That(row[index].xMin - row[index - 1].xMax, Is.EqualTo(columnGap).Within(0.5f));
                Assert.That(row[index].y, Is.EqualTo(row[0].y).Within(0.5f));
            }

            Assert.That(row[0].xMin - frame.xMin, Is.EqualTo(frame.xMax - row[row.Length - 1].xMax).Within(0.5f));

            // An empty slot's tooltip names it.
            var neckTrigger = View(views, "equipment:neck").GetComponent<InventoryTooltipTrigger>();
            var neckPointer = new PointerEventData(EventSystem.current) { position = new Vector2(200f, 200f) };
            neckTrigger.OnPointerEnter(neckPointer);
            Assert.That(windows.Tooltip.Text, Does.StartWith("<b>" + InventorySlotPresentation.EquipmentSlotName(catalog, "neck") + "</b>"));
            neckTrigger.OnPointerExit(neckPointer);

            var fill = windows.GetComponentsInChildren<RectTransform>()
                .Single(rect => rect.name == "Fill" && rect.parent.name == "Meter");
            Assert.That(
                fill.anchorMax.x,
                Is.EqualTo(CarryLoadPresentation.From(snapshot).FillFraction).Within(0.0001f));

            // The search dims items whose name and category do not match.
            windows.SetSearch("no item is called this");
            Assert.That(View(windows.GetComponentsInChildren<InventorySlotView>(), "inventory:0").Dimmed, Is.True);
            windows.SetSearch(catalog.GetDisplayName(occupied.ItemDefinitionId).Substring(0, 3).ToUpperInvariant());
            Assert.That(View(windows.GetComponentsInChildren<InventorySlotView>(), "inventory:0").Dimmed, Is.False);
            windows.SetSearch(string.Empty);

            var tooltip = windows.Tooltip;
            Assert.That(tooltip.IsVisible, Is.False);
            var trigger = View(views, "equipment:" + probe.EquippedSlotId).GetComponent<InventoryTooltipTrigger>();
            var pointer = new PointerEventData(EventSystem.current) { position = new Vector2(200f, 200f) };
            trigger.OnPointerEnter(pointer);
            Assert.That(tooltip.IsVisible, Is.True);
            Assert.That(
                tooltip.Text,
                Does.Contain(catalog.GetDisplayName(
                    snapshot.Equipment.Single(slot => slot.SlotId == probe.EquippedSlotId).Item.DefinitionId)));

            // On the first hover the tooltip is as wide as its widest line, up to its limit.
            var tooltipLabel = tooltip.GetComponentInChildren<TMP_Text>();
            var widestLine = tooltipLabel.GetPreferredValues(tooltip.Text, float.PositiveInfinity, float.PositiveInfinity).x;
            Assert.That(widestLine, Is.GreaterThan(100f));
            Assert.That(
                tooltipLabel.rectTransform.rect.width,
                Is.GreaterThanOrEqualTo(Mathf.Min(320f, widestLine) - 1f));
            trigger.OnPointerExit(pointer);
            Assert.That(tooltip.IsVisible, Is.False);
        }

        // The Equipment window shows the World scene's character preview on its pedestal, lets it
        // draw only while the window is open, and a drag across the picture turns the copy. The
        // preview draws only with a graphics device, so the headless run leaves this test out; see
        // tests/README.md.
        [UnityTest]
        [Category("Graphics")]
        public IEnumerator TheEquipmentWindowShowsTheCharacterPreviewWhileOpen()
        {
            using var probe = new WindowProbe();
            yield return probe.Load("Preview Probe");
            var windows = probe.Windows;
            var preview = windows.CharacterPreview;
            Assert.That(preview, Is.Not.Null);
            Assert.That(preview, Is.SameAs(UnityEngine.Object.FindAnyObjectByType<WorldSceneContext>(FindObjectsInactive.Include).Preview));
            Assert.That(preview.Visible, Is.False);

            windows.SetEquipmentOpen(true);
            yield return null;
            Canvas.ForceUpdateCanvases();
            var figure = windows.GetComponentsInChildren<RawImage>().Single(image => image.name == "Figure");
            Assert.That(figure.texture, Is.SameAs(preview.Texture));
            Assert.That(preview.Visible, Is.True);
            var size = figure.rectTransform.rect.size;
            Assert.That(size.x / size.y, Is.EqualTo((float)CharacterPreview.TextureWidth / CharacterPreview.TextureHeight).Within(0.01f));

            var before = preview.Yaw;
            figure.GetComponent<CharacterPreviewDrag>().OnDrag(new PointerEventData(EventSystem.current)
            {
                delta = new Vector2(-50f, 0f),
                button = PointerEventData.InputButton.Left
            });
            Assert.That(
                Mathf.DeltaAngle(before, preview.Yaw),
                Is.EqualTo(50f * CharacterPreviewDrag.DegreesPerPixel).Within(0.01f));

            // Another window leaves it drawing; closing Equipment stops it.
            windows.SetInventoryOpen(true);
            Assert.That(preview.Visible, Is.True);
            windows.SetEquipmentOpen(false);
            Assert.That(preview.Visible, Is.False);
        }

        // Each window opens and closes on its own and keeps its place; its close button closes
        // only it.
        [UnityTest]
        public IEnumerator WindowsOpenAndCloseOnTheirOwnAndKeepTheirPlaces()
        {
            using var probe = new WindowProbe();
            yield return probe.Load("Window Toggle Probe");
            var windows = probe.Windows;

            windows.SetInventoryOpen(true);
            yield return null;
            Canvas.ForceUpdateCanvases();
            var inventoryWindow = Window(windows, InventoryWindows.InventoryWindowName);
            var alone = inventoryWindow.position;
            Assert.That(windows.IsEquipmentOpen, Is.False);
            Assert.That(windows.IsStorageOpen, Is.False);

            windows.SetEquipmentOpen(true);
            yield return null;
            Canvas.ForceUpdateCanvases();
            Assert.That(inventoryWindow.position, Is.EqualTo(alone));

            windows.OpenStorage(InventoryContextKind.Bank, InventoryStorageSource.Development);
            yield return null;
            Canvas.ForceUpdateCanvases();
            Assert.That(windows.IsStorageOpen, Is.True);
            Assert.That(windows.StorageContext, Is.EqualTo(InventoryContextKind.Bank));
            Assert.That(inventoryWindow.position, Is.EqualTo(alone));
            var storageWindow = Window(windows, InventoryWindows.StorageWindowName);
            Assert.That(storageWindow.position.x, Is.LessThan(inventoryWindow.position.x));
            Assert.That(
                windows.GetComponentsInChildren<InventorySlotView>().Count(view => view.SlotKey.StartsWith("bank:", StringComparison.Ordinal)),
                Is.EqualTo(probe.Snapshot.Bank.Slots.Count));

            // The close button of a window closes only that window.
            Window(windows, InventoryWindows.EquipmentWindowName)
                .Find("Header/Close").GetComponent<Button>().onClick.Invoke();
            yield return null;
            Assert.That(windows.IsEquipmentOpen, Is.False);
            Assert.That(windows.IsInventoryOpen, Is.True);
            Assert.That(windows.IsStorageOpen, Is.True);
            storageWindow.Find("Header/Close").GetComponent<Button>().onClick.Invoke();
            yield return null;
            Assert.That(windows.IsStorageOpen, Is.False);
            Assert.That(windows.IsInventoryOpen, Is.True);
        }

        // A right-click on an equipped item runs its move: without a simulation session the
        // controller refuses it, and the game message says so. The right button never drags.
        [UnityTest]
        public IEnumerator RightClickRunsTheSlotMoveAndTheRightButtonNeverDrags()
        {
            using var probe = new WindowProbe();
            yield return probe.Load("Right Click Probe");
            var windows = probe.Windows;
            windows.SetEquipmentOpen(true);
            yield return null;

            var slot = View(windows.GetComponentsInChildren<InventorySlotView>(), "equipment:" + probe.EquippedSlotId);
            var rightPress = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Right,
                position = new Vector2(200f, 200f)
            };
            slot.GetComponent<InventoryDragSource>().OnBeginDrag(rightPress);
            Assert.That(windows.DragCoordinator.IsDragging, Is.False);

            slot.GetComponent<InventorySlotClicks>().OnPointerClick(rightPress);
            yield return null;

            Assert.That(GameMessageShows(NoSessionMessage(probe)), Is.True, "The game message names why the move was not sent.");
        }

        // Shift-dragging a stack splits half of it off without asking: onto an empty slot, and
        // for arrows into the empty ammunition slot, which glows. Shift+right-click sends half
        // where right-click sends the stack: arrows into the ammunition slot, anything into the
        // open Bank. Without a session the controller refuses each after its checks passed.
        [UnityTest]
        public IEnumerator ShiftMovesHalfOfAStackWithoutAsking()
        {
            using var probe = new WindowProbe();
            yield return probe.Load("Half Probe", extraCarried: catalog => CreateItem(
                catalog.Definitions.First(definition =>
                    definition.Ammunition != null
                    && definition.EquipmentSlots != null
                    && definition.EquipmentSlots.Contains(AmmunitionSlotId)).Id,
                10));
            var windows = probe.Windows;
            windows.SetEquipmentOpen(true);
            windows.SetInventoryOpen(true);
            yield return null;

            var views = windows.GetComponentsInChildren<InventorySlotView>();
            var stack = View(views, "inventory:0");
            var source = stack.GetComponent<InventoryDragSource>();
            source.PartialOverride = true;
            var pointer = new PointerEventData(EventSystem.current) { position = new Vector2(300f, 300f) };
            source.OnBeginDrag(pointer);
            Assert.That(windows.DragCoordinator.ActivePayload.Quantity, Is.EqualTo(1), "half of a stack of two");
            var target = View(views, "inventory:1").GetComponent<InventoryDropTarget>();
            target.OnPointerEnter(pointer);
            Assert.That(target.ShowsValidDrop, Is.True);
            target.OnDrop(pointer);
            source.OnEndDrag(pointer);
            yield return null;
            Assert.That(windows.Dialog.IsOpen, Is.False);
            Assert.That(NewestGameMessage(), Does.Contain(NoSessionMessage(probe)));

            var arrows = View(windows.GetComponentsInChildren<InventorySlotView>(), "inventory:2").GetComponent<InventoryDragSource>();
            arrows.PartialOverride = true;
            arrows.OnBeginDrag(pointer);
            Assert.That(windows.DragCoordinator.ActivePayload.Quantity, Is.EqualTo(5));
            var ammunition = windows.GetComponentsInChildren<ItemSlotView>()
                .Single(view => view.Spec.Key == "equipment:" + AmmunitionSlotId);
            Assert.That(ammunition.ShowsDropTargetGlow, Is.True);
            var ammunitionTarget = ammunition.GetComponent<InventoryDropTarget>();
            ammunitionTarget.OnPointerEnter(pointer);
            Assert.That(ammunitionTarget.ShowsValidDrop, Is.True);
            ammunitionTarget.OnDrop(pointer);
            arrows.OnEndDrag(pointer);
            yield return null;
            Assert.That(NewestGameMessage(), Does.Contain(NoSessionMessage(probe)));

            var right = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Right };
            var arrowClicks = View(windows.GetComponentsInChildren<InventorySlotView>(), "inventory:2").GetComponent<InventorySlotClicks>();
            arrowClicks.ShiftOverride = true;
            arrowClicks.OnPointerClick(right);
            yield return null;
            Assert.That(NewestGameMessage(), Does.Contain(NoSessionMessage(probe)), "half the arrows go to the ammunition slot");

            windows.OpenStorage(InventoryContextKind.Bank, InventoryStorageSource.Development);
            yield return null;
            var clicks = View(windows.GetComponentsInChildren<InventorySlotView>(), "inventory:0").GetComponent<InventorySlotClicks>();
            clicks.ShiftOverride = true;
            clicks.OnPointerClick(right);
            yield return null;
            Assert.That(NewestGameMessage(), Does.Contain(NoSessionMessage(probe)));
        }

        // Ctrl+click asks how many items to take and carries them on the pointer, where the
        // world cannot take the click; a click on a slot puts them down and a right-click puts
        // them back. Dropping an item outside every window asks to destroy it.
        [UnityTest]
        public IEnumerator CtrlClickCarriesTheChosenPartAndADropOutsideAsksToDestroy()
        {
            using var probe = new WindowProbe();
            yield return probe.Load("Split Probe");
            var windows = probe.Windows;
            windows.SetInventoryOpen(true);
            yield return null;

            var views = windows.GetComponentsInChildren<InventorySlotView>();
            var stack = View(views, "inventory:0");
            var stackClicks = stack.GetComponent<InventorySlotClicks>();
            stackClicks.ControlOverride = true;
            var left = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
            stackClicks.OnPointerClick(left);

            var dialog = windows.Dialog;
            Assert.That(dialog.IsOpen, Is.True);
            var splitHeading = probe.Catalog.Texts.Get("ui/inventory/split-heading");
            Assert.That(dialog.Title, Is.EqualTo(splitHeading));
            Assert.That(dialog.GetComponentInChildren<TMP_Text>().text, Is.EqualTo(splitHeading.ToUpperInvariant()));
            // A stack of two splits one item off.
            Assert.That(dialog.Quantity, Is.EqualTo(1));
            dialog.Confirm();
            Assert.That(dialog.IsOpen, Is.False);
            var coordinator = windows.DragCoordinator;
            Assert.That(coordinator.IsCarrying, Is.True);
            Assert.That(coordinator.ActivePayload.Quantity, Is.EqualTo(1));
            var localPlayer = UnityEngine.Object.FindAnyObjectByType<LocalPlayerController>();
            if (localPlayer != null && localPlayer.PlayerInput != null && localPlayer.PlayerInput.IsConfigured)
            {
                Assert.That(localPlayer.TryGetPointerRay(out _), Is.False, "the world leaves a carrying pointer alone");
            }

            // The windows redraw under the carried part.
            windows.SetEquipmentOpen(true);
            yield return null;
            Assert.That(coordinator.IsCarrying, Is.True);

            var emptyClicks = View(windows.GetComponentsInChildren<InventorySlotView>(), "inventory:1").GetComponent<InventorySlotClicks>();
            emptyClicks.ControlOverride = false;
            emptyClicks.OnPointerClick(new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Right });
            Assert.That(coordinator.IsCarrying, Is.False, "a right-click puts the part back");

            stackClicks.OnPointerClick(left);
            dialog.Confirm();
            Assert.That(coordinator.IsCarrying, Is.True);
            emptyClicks.OnPointerClick(left);
            yield return null;
            Assert.That(coordinator.IsCarrying, Is.False);
            Assert.That(GameMessageShows(NoSessionMessage(probe)), Is.True);

            // Released over nothing of the interface, the item asks to be destroyed.
            var source = View(windows.GetComponentsInChildren<InventorySlotView>(), "inventory:0").GetComponent<InventoryDragSource>();
            var pointer = new PointerEventData(EventSystem.current) { position = new Vector2(300f, 300f) };
            source.PartialOverride = false;
            source.OnBeginDrag(pointer);
            source.OnEndDrag(new PointerEventData(EventSystem.current) { position = new Vector2(960f, 540f) });
            Assert.That(dialog.IsOpen, Is.True);
            Assert.That(dialog.Title, Is.EqualTo(probe.Catalog.Texts.Get("ui/inventory/destroy-heading")));
            Assert.That(dialog.Message, Does.Contain(probe.Catalog.GetDisplayName(stack.ItemDefinitionId)));
            dialog.Close();
            Assert.That(dialog.IsOpen, Is.False);
        }

        // While an equippable item is dragged, exactly the equipment slots that take it glow,
        // such as both ring slots for a ring, until the drag ends.
        [UnityTest]
        public IEnumerator TheEquipmentSlotsThatTakeADraggedItemGlow()
        {
            using var probe = new WindowProbe();
            string[] ringSlots = null;
            yield return probe.Load("Glow Probe", extraCarried: catalog =>
            {
                var ring = catalog.Definitions.First(definition =>
                    definition.EquipmentSlots != null
                    && definition.EquipmentSlots.Length > 1
                    && definition.MaximumStackSize == 1
                    && definition.RequiredCharacterLevel <= 1
                    && (definition.BlockedEquipmentSlots == null || definition.BlockedEquipmentSlots.Length == 0));
                ringSlots = ring.EquipmentSlots;
                return CreateItem(ring.Id, 1);
            });
            var windows = probe.Windows;
            windows.SetEquipmentOpen(true);
            windows.SetInventoryOpen(true);
            yield return null;

            var slots = windows.GetComponentsInChildren<ItemSlotView>()
                .Where(view => view.Spec.Key.StartsWith("equipment:", StringComparison.Ordinal))
                .ToArray();
            Assert.That(slots.Any(view => view.ShowsDropTargetGlow), Is.False);
            var source = View(windows.GetComponentsInChildren<InventorySlotView>(), "inventory:2").GetComponent<InventoryDragSource>();
            source.OnBeginDrag(new PointerEventData(EventSystem.current) { position = new Vector2(300f, 300f) });
            Assert.That(windows.DragCoordinator.IsDragging, Is.True);

            var glowing = slots
                .Where(view => view.ShowsDropTargetGlow)
                .Select(view => view.Spec.Key.Substring("equipment:".Length))
                .OrderBy(slotId => slotId, StringComparer.Ordinal);
            Assert.That(glowing, Is.EqualTo(ringSlots.OrderBy(slotId => slotId, StringComparer.Ordinal)));

            windows.DragCoordinator.Cancel();
            Assert.That(slots.Any(view => view.ShowsDropTargetGlow), Is.False);
        }

        // Each window takes the pointer only above itself, so a click beside or between the
        // windows reaches the world.
        [UnityTest]
        public IEnumerator WindowsTakeThePointerOnlyAboveThemselves()
        {
            using var probe = new WindowProbe();
            yield return probe.Load("Pointer Probe");
            var windows = probe.Windows;
            Assert.That(EventSystem.current, Is.Not.Null);
            var steps = new Action[]
            {
                () => windows.SetInventoryOpen(true),
                () => windows.SetEquipmentOpen(true),
                () => windows.OpenStorage(InventoryContextKind.RecoveryStorage, InventoryStorageSource.Development)
            };
            foreach (var step in steps)
            {
                step();
                yield return null;
                Canvas.ForceUpdateCanvases();
                var shown = new[]
                    {
                        InventoryWindows.EquipmentWindowName,
                        InventoryWindows.InventoryWindowName,
                        InventoryWindows.StorageWindowName
                    }
                    .Select(name => Window(windows, name))
                    .Where(window => window.gameObject.activeInHierarchy)
                    .ToArray();

                // Cell centers never lie on a window edge, so both checks agree exactly.
                const int cells = 20;
                var overWindow = 0;
                var overWorld = 0;
                for (var column = 0; column < cells; column++)
                {
                    for (var row = 0; row < cells; row++)
                    {
                        var point = new Vector2(
                            (column + 0.5f) / cells * Screen.width,
                            (row + 0.5f) / cells * Screen.height);
                        var aboveShownWindow = shown.Any(window =>
                            RectTransformUtility.RectangleContainsScreenPoint(window, point, null));
                        Assert.That(TakesPointer(windows, point), Is.EqualTo(aboveShownWindow), "at " + point);
                        if (aboveShownWindow)
                        {
                            overWindow++;
                        }
                        else
                        {
                            overWorld++;
                        }
                    }
                }

                Assert.That(overWindow, Is.GreaterThan(0));
                Assert.That(overWorld, Is.GreaterThan(0));
            }
        }

        [UnityTest]
        public IEnumerator EquippedArrowSlotFollowsTheLiveCountOfTheOwnerCombatState()
        {
            using var probe = new WindowProbe();
            var combat = UnityEngine.Object.FindAnyObjectByType<CombatClientController>();
            yield return probe.Load("Arrow Probe", catalog =>
            {
                var arrows = catalog.Definitions.First(definition =>
                    definition.Ammunition != null
                    && definition.EquipmentSlots != null
                    && definition.EquipmentSlots.Contains(AmmunitionSlotId));
                return (AmmunitionSlotId, CreateItem(arrows.Id, 500));
            });
            combat = UnityEngine.Object.FindAnyObjectByType<CombatClientController>();
            Assert.That(combat, Is.Not.Null);
            var windows = probe.Windows;
            var stack = probe.Snapshot.Equipment.Single(slot => slot.SlotId == AmmunitionSlotId).Item;
            try
            {
                windows.SetEquipmentOpen(true);
                yield return null;
                Assert.That(ArrowSlotQuantity(windows), Is.EqualTo("500"));

                // The worker's live count already subtracts shots that are not committed yet.
                Assert.That(combat.State.TryApplyOwner(Owner(1, stack.ItemInstanceId, 482), out var error), Is.True, error);
                Assert.That(ArrowSlotQuantity(windows), Is.EqualTo("482"));
                Assert.That(combat.State.TryApplyOwner(Owner(2, stack.ItemInstanceId, 0), out error), Is.True, error);
                Assert.That(ArrowSlotQuantity(windows), Is.EqualTo("0"));

                // A live count of another stack leaves the committed quantity.
                Assert.That(combat.State.TryApplyOwner(Owner(3, Guid.NewGuid(), 7), out error), Is.True, error);
                Assert.That(ArrowSlotQuantity(windows), Is.EqualTo("500"));

                // Drawing the window again keeps the live count.
                Assert.That(combat.State.TryApplyOwner(Owner(4, stack.ItemInstanceId, 481), out error), Is.True, error);
                windows.SetEquipmentOpen(false);
                windows.SetEquipmentOpen(true);
                yield return null;
                Assert.That(ArrowSlotQuantity(windows), Is.EqualTo("481"));
            }
            finally
            {
                combat.State.Clear();
            }
        }

        private static bool TakesPointer(InventoryWindows windows, Vector2 point)
        {
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            return hits.Any(hit => hit.gameObject.transform.IsChildOf(windows.transform));
        }

        // Without a simulation session the controller refuses every operation with this text.
        private static string NoSessionMessage(WindowProbe probe)
        {
            return probe.Catalog.Texts.Get("ui/inventory/session-required");
        }

        private static bool GameMessageShows(string text)
        {
            return UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Exclude)
                .Any(label => label.text.Contains(text));
        }

        private static string NewestGameMessage()
        {
            var panel = UnityEngine.Object.FindAnyObjectByType<TemporaryGameMessagePanel>();
            Assert.That(panel, Is.Not.Null);
            return panel.VisibleTexts().LastOrDefault() ?? string.Empty;
        }

        private static RectTransform Window(InventoryWindows windows, string name)
        {
            return windows.GetComponentsInChildren<RectTransform>(true).Single(rect => rect.name == name);
        }

        // The screen rectangle a UI element covers.
        private static Rect WorldRect(Transform element)
        {
            var corners = new Vector3[4];
            ((RectTransform)element).GetWorldCorners(corners);
            return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
        }

        private static string ArrowSlotQuantity(InventoryWindows windows)
        {
            var slot = View(windows.GetComponentsInChildren<InventorySlotView>(), "equipment:" + AmmunitionSlotId);
            var label = slot.transform.Find("Content/" + ItemSlotView.QuantityLabelName);
            Assert.That(label, Is.Not.Null);
            return label.GetComponent<TMP_Text>().text;
        }

        private static RealtimeOwnerCombatState Owner(long revision, Guid ammunition, int quantity)
        {
            return new RealtimeOwnerCombatState(
                revision,
                100,
                100,
                100,
                100,
                100,
                RealtimeCombatLifeState.Alive,
                0,
                0,
                false,
                0,
                RealtimeCombatAttackStatus.Idle,
                0,
                0,
                ammunition,
                quantity);
        }

        private static InventoryItem CreateItem(string definitionId, int quantity)
        {
            return new InventoryItem(
                Guid.NewGuid(),
                definitionId,
                quantity,
                1,
                Array.Empty<InventoryPolicy>());
        }

        private static InventorySlotView View(IEnumerable<InventorySlotView> views, string slotKey)
        {
            return views.Single(view => view.SlotKey == slotKey);
        }

        // Loads Development World 1 against an unreachable GameService, selects a character,
        // and applies a committed snapshot; disposing closes the windows and the session.
        private sealed class WindowProbe : IDisposable
        {
            private IDisposable gameService;

            public InventoryWindows Windows { get; private set; }

            public ClientItemCatalog Catalog { get; private set; }

            public CharacterInventorySnapshot Snapshot { get; private set; }

            public string EquippedSlotId { get; private set; }

            public string BlockedSlotId { get; private set; }

            // The extra carried item lies at Permanent Inventory slot index 2.
            public IEnumerator Load(
                string characterName,
                Func<ClientItemCatalog, (string SlotId, InventoryItem Item)> extraEquipment = null,
                Func<ClientItemCatalog, InventoryItem> extraCarried = null)
            {
                gameService = PlayModeClient.UseGameService(UnreachableGameServiceUrl);
                PlayModeClient.Session.SignOut();
                yield return PlayModeClient.WaitForContent();
                var worldLoad = ClientSceneLoader.LoadWorldScene(SceneNames.DevelopmentWorld1);
                yield return worldLoad;
                Assert.That(worldLoad.Status, Is.EqualTo(AsyncOperationStatus.Succeeded));
                yield return null;

                var controller = UnityEngine.Object.FindAnyObjectByType<InventoryClientController>();
                Windows = UnityEngine.Object.FindAnyObjectByType<InventoryWindows>();
                Assert.That(controller, Is.Not.Null);
                Assert.That(Windows, Is.Not.Null);
                Catalog = controller.State.Catalog;
                Assert.That(Catalog, Is.Not.Null);
                var characterId = Guid.NewGuid();
                PlayModeClient.Session.SignIn(new AuthResponse { accountId = Guid.NewGuid().ToString() });
                PlayModeClient.Session.Select(
                    new CharacterResponse
                    {
                        id = characterId.ToString(),
                        name = characterName
                    },
                    null);
                yield return null;
                Snapshot = CreateSnapshot(Catalog, characterId, extraEquipment?.Invoke(Catalog), extraCarried?.Invoke(Catalog));
                Assert.That(controller.State.ApplyFull(Snapshot), Is.EqualTo(InventorySnapshotApplyResult.Applied));
            }

            public void Dispose()
            {
                if (Windows != null)
                {
                    Windows.CloseAll();
                }

                PlayModeClient.Session.SignOut();
                gameService?.Dispose();
                gameService = null;
            }

            // Picks items by their rules instead of their ids, so content changes keep the test
            // valid.
            private CharacterInventorySnapshot CreateSnapshot(
                ClientItemCatalog catalog,
                Guid characterId,
                (string SlotId, InventoryItem Item)? extra,
                InventoryItem extraCarried)
            {
                var slotIds = catalog.Gameplay.EquipmentSlots.Select(slot => slot.Id).ToArray();
                var blocker = catalog.Definitions.FirstOrDefault(definition =>
                    definition.EquipmentSlots != null
                    && definition.EquipmentSlots.Length > 0
                    && definition.BlockedEquipmentSlots != null
                    && definition.BlockedEquipmentSlots.Any(slotId =>
                        slotIds.Contains(slotId) && slotId != definition.EquipmentSlots[0]));
                var equipped = blocker ?? catalog.Definitions.First(definition =>
                    definition.EquipmentSlots != null && definition.EquipmentSlots.Length > 0);
                EquippedSlotId = equipped.EquipmentSlots[0];
                BlockedSlotId = blocker?.BlockedEquipmentSlots.First(slotId =>
                    slotIds.Contains(slotId) && slotId != blocker.EquipmentSlots[0]);
                var equippedSlot = EquippedSlotId;
                var equipment = slotIds
                    .Select((slotId, index) => new InventoryEquipmentSlot(
                        slotId,
                        index,
                        slotId == equippedSlot
                            ? CreateItem(equipped.Id, 1)
                            : extra != null && slotId == extra.Value.SlotId ? extra.Value.Item : null))
                    .ToArray();

                var stackable = catalog.Definitions.First(definition =>
                    definition.MaximumStackSize > 1 && definition.PlayerDestroyable);
                var settings = catalog.Gameplay.Character;
                var carried = settings.BaseCarryCapacity / 2;
                var carriedItems = new Dictionary<int, InventoryItem> { [0] = CreateItem(stackable.Id, 2) };
                if (extraCarried != null)
                {
                    carriedItems[2] = extraCarried;
                }

                return new CharacterInventorySnapshot(
                    characterId,
                    catalog.Revision,
                    3,
                    CreateContainer("permanent_inventory", settings.InventorySlotCount, carriedItems),
                    equipment,
                    null,
                    CreateContainer("bank", settings.BankSlotCount, new Dictionary<int, InventoryItem>()),
                    new RecoveryStorageInventory(Guid.NewGuid(), 1, Array.Empty<RecoveryDelivery>()),
                    carried,
                    settings.BaseCarryCapacity,
                    (int)(carried * 10_000 / settings.BaseCarryCapacity),
                    10_000);
            }

            private static InventoryContainer CreateContainer(
                string containerType,
                int capacity,
                IReadOnlyDictionary<int, InventoryItem> items)
            {
                var slots = new InventorySlot[capacity];
                for (var index = 0; index < capacity; index++)
                {
                    items.TryGetValue(index, out var item);
                    slots[index] = new InventorySlot(index, "general", Array.Empty<string>(), item);
                }

                return new InventoryContainer(Guid.NewGuid(), containerType, 1, capacity, slots);
            }
        }
    }
}
