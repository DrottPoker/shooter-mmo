using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.App;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.Client.Ui;
using Mmorpg.Client.Worlds;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.PlayMode
{
    // The window manager of a loaded World without a GameService: the real windows on their
    // canvases, Escape in order, a click that focuses a window, and the typing guard in the
    // Inventory window's search field.
    public sealed class UiWindowManagerPlayModeTests
    {
        // Port 9 refuses connections, so no request can reach a real GameService.
        private const string UnreachableGameServiceUrl = "http://127.0.0.1:9";

        private System.IDisposable gameService;
        private UiWindowManager manager;
        private InventoryWindows inventory;
        private QuestWindows quests;
        private TemporaryDebugMenu debugMenu;
        private TemporaryAdminMenu adminMenu;

        [UnitySetUp]
        public IEnumerator LoadWorld()
        {
            gameService = PlayModeClient.UseGameService(UnreachableGameServiceUrl);
            PlayModeClient.Session.SignOut();
            yield return PlayModeClient.WaitForContent();
            var worldLoad = ClientSceneLoader.LoadWorldScene(SceneNames.DevelopmentWorld1);
            yield return worldLoad;
            Assert.That(worldLoad.Status, Is.EqualTo(AsyncOperationStatus.Succeeded));
            yield return null;

            manager = Object.FindAnyObjectByType<UiWindowManager>();
            inventory = Object.FindAnyObjectByType<InventoryWindows>();
            quests = Object.FindAnyObjectByType<QuestWindows>();
            debugMenu = Object.FindAnyObjectByType<TemporaryDebugMenu>();
            adminMenu = Object.FindAnyObjectByType<TemporaryAdminMenu>();
            Assert.That(manager, Is.Not.Null);
            Assert.That(inventory, Is.Not.Null);
            Assert.That(quests, Is.Not.Null);
            Assert.That(debugMenu, Is.Not.Null);
            Assert.That(manager.OpenEntries, Is.Empty);
        }

        [UnityTearDown]
        public IEnumerator CloseEverything()
        {
            if (manager != null)
            {
                manager.Dialog.Close();
            }

            if (inventory != null)
            {
                inventory.CloseAll();
            }

            if (quests != null)
            {
                quests.CloseDialog();
                quests.SetLogOpen(false);
            }

            if (debugMenu != null)
            {
                debugMenu.SetOpen(false);
            }

            if (adminMenu != null)
            {
                adminMenu.SetOpen(false);
            }

            yield return null;
            PlayModeClient.Session.SignOut();
            gameService?.Dispose();
            gameService = null;
        }

        // The inventory, the quest log, and a vendor open in that order, a dialog over them, and
        // each Escape closes one: the dialog, then the newest window first. The newest window
        // draws on top of its layer, and the dialog over every window.
        [UnityTest]
        public IEnumerator EscapeClosesTheDialogAndThenTheNewestWindowFirst()
        {
            Assert.That(inventory.Dialog, Is.SameAs(manager.Dialog));
            Assert.That(quests.Confirmation, Is.SameAs(manager.Dialog));
            Assert.That(Object.FindObjectsByType<UiDialog>(FindObjectsInactive.Include), Has.Length.EqualTo(1), "One dialog host.");

            manager.Press(UiCommand.ToggleInventory);
            yield return null;
            manager.Press(UiCommand.ToggleQuestLog);
            yield return null;
            inventory.OpenStorage(InventoryContextKind.Vendor, InventoryStorageSource.Npc);
            yield return null;
            Assert.That(inventory.IsInventoryOpen, Is.True);
            Assert.That(quests.IsLogOpen, Is.True);
            Assert.That(inventory.IsStorageOpen, Is.True);
            Assert.That(inventory.StorageContext, Is.EqualTo(InventoryContextKind.Vendor));

            var inventoryCanvas = WindowCanvas(InventoryWindows.InventoryWindowName);
            var logCanvas = quests.Log.GetComponent<Canvas>();
            var storageCanvas = WindowCanvas(InventoryWindows.StorageWindowName);
            Assert.That(logCanvas.sortingOrder, Is.GreaterThan(inventoryCanvas.sortingOrder));
            Assert.That(storageCanvas.sortingOrder, Is.GreaterThan(logCanvas.sortingOrder));

            var confirmed = false;
            manager.Dialog.AskConfirmation(inventory, "Destroy Item", "Destroy it?", "Destroy", () => confirmed = true);
            yield return null;
            Assert.That(
                manager.Dialog.GetComponentInParent<Canvas>().sortingOrder,
                Is.GreaterThan(storageCanvas.sortingOrder),
                "The dialog draws over every window.");

            manager.Press(UiCommand.Cancel);
            yield return null;
            Assert.That(manager.Dialog.IsOpen, Is.False);
            Assert.That(confirmed, Is.False);
            Assert.That(inventory.IsStorageOpen && quests.IsLogOpen && inventory.IsInventoryOpen, Is.True, "The dialog closes alone.");

            manager.Press(UiCommand.Cancel);
            yield return null;
            Assert.That(inventory.IsStorageOpen, Is.False);
            Assert.That(quests.IsLogOpen && inventory.IsInventoryOpen, Is.True);

            manager.Press(UiCommand.Cancel);
            yield return null;
            Assert.That(quests.IsLogOpen, Is.False);
            Assert.That(inventory.IsInventoryOpen, Is.True);

            manager.Press(UiCommand.Cancel);
            yield return null;
            Assert.That(inventory.IsInventoryOpen, Is.False);
            Assert.That(manager.OpenEntries, Is.Empty);
        }

        // A click on an older window brings it to the top of its layer, and Escape closes it
        // first.
        [UnityTest]
        public IEnumerator AClickOnAnOlderWindowBringsItToTheTop()
        {
            manager.Press(UiCommand.ToggleInventory);
            manager.Press(UiCommand.ToggleQuestLog);
            yield return null;
            Canvas.ForceUpdateCanvases();
            var inventoryCanvas = WindowCanvas(InventoryWindows.InventoryWindowName);
            var logCanvas = quests.Log.GetComponent<Canvas>();
            Assert.That(manager.Top.Canvas, Is.SameAs(logCanvas));

            manager.FocusAt(ScreenCenter((RectTransform)inventoryCanvas.transform));
            Assert.That(manager.Top.Canvas, Is.SameAs(inventoryCanvas));
            Assert.That(inventoryCanvas.sortingOrder, Is.GreaterThan(logCanvas.sortingOrder));

            manager.Press(UiCommand.Cancel);
            yield return null;
            Assert.That(inventory.IsInventoryOpen, Is.False);
            Assert.That(quests.IsLogOpen, Is.True);
        }

        // While the search field has focus every key of the interface is text: B, I, C, L, F2,
        // and F3 open nothing, and world input that reads keys, the camera's arrow keys, rests.
        // Escape leaves the field and closes nothing; the next one closes the window. Without
        // focus every key works again.
        [UnityTest]
        public IEnumerator EveryHotkeyIsTextWhileTheSearchFieldHasFocus()
        {
            manager.Press(UiCommand.ToggleInventory);
            yield return null;
            var search = inventory.GetComponentsInChildren<RectTransform>(true)
                .Single(rect => rect.name == InventoryWindows.InventoryWindowName)
                .GetComponentInChildren<TMP_InputField>();
            Assert.That(search, Is.Not.Null);
            EventSystem.current.SetSelectedGameObject(search.gameObject);
            search.ActivateInputField();
            yield return null;
            yield return null;
            Assert.That(search.isFocused, Is.True);
            Assert.That(manager.IsTyping, Is.True);
            var focus = Object.FindAnyObjectByType<ClientBootstrap>().InputFocus;
            Assert.That(focus.IsKeyboardHeldByInterface, Is.True, "The arrow keys move the caret, not the camera.");

            foreach (var command in new[]
                     {
                         UiCommand.ToggleInventory,
                         UiCommand.ToggleEquipment,
                         UiCommand.ToggleQuestLog,
                         UiCommand.ToggleDebugMenu,
                         UiCommand.ToggleAdminMenu
                     })
            {
                manager.Press(command);
            }

            yield return null;
            Assert.That(inventory.IsInventoryOpen, Is.True);
            Assert.That(inventory.IsEquipmentOpen, Is.False);
            Assert.That(quests.IsLogOpen, Is.False);
            Assert.That(debugMenu.IsOpen, Is.False);
            Assert.That(adminMenu == null || !adminMenu.IsOpen, Is.True);

            manager.Press(UiCommand.Cancel);
            Assert.That(manager.IsTyping, Is.False, "Escape leaves the field.");
            Assert.That(focus.IsKeyboardHeldByInterface, Is.False, "The arrow keys turn the camera again.");
            yield return null;
            Assert.That(search.isFocused, Is.False);
            Assert.That(inventory.IsInventoryOpen, Is.True, "Escape left the field and closed nothing.");

            manager.Press(UiCommand.ToggleEquipment);
            manager.Press(UiCommand.ToggleQuestLog);
            manager.Press(UiCommand.ToggleDebugMenu);
            yield return null;
            Assert.That(inventory.IsEquipmentOpen && quests.IsLogOpen && debugMenu.IsOpen, Is.True, "Without focus the keys work.");
            if (adminMenu != null)
            {
                manager.Press(UiCommand.ToggleAdminMenu);
                Assert.That(adminMenu.IsOpen, Is.True);
                manager.Press(UiCommand.Cancel);
                Assert.That(adminMenu.IsOpen, Is.False, "Escape closes the newest, the Admin menu.");
            }

            manager.Press(UiCommand.Cancel);
            Assert.That(debugMenu.IsOpen, Is.False, "Escape closes the Debug menu.");
        }

        // Every screen-space canvas of the World takes its order from the layer table: the order
        // of its layer, or a place in a stacked layer's band, and no two shown canvases tie.
        [UnityTest]
        public IEnumerator EveryCanvasOfTheWorldTakesItsOrderFromTheLayerTable()
        {
            manager.Press(UiCommand.ToggleInventory);
            manager.Press(UiCommand.ToggleEquipment);
            manager.Press(UiCommand.ToggleQuestLog);
            manager.Press(UiCommand.ToggleDebugMenu);
            manager.Press(UiCommand.ToggleAdminMenu);
            inventory.OpenStorage(InventoryContextKind.Bank, InventoryStorageSource.Development);
            manager.Dialog.AskConfirmation(inventory, "Destroy Item", "Destroy it?", "Destroy", () => { });
            yield return null;

            var shown = new List<(string Name, int Order)>();
            foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include))
            {
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay || !(canvas.isRootCanvas || canvas.overrideSorting))
                {
                    continue;
                }

                Assert.That(LayerOf(canvas.sortingOrder), Is.Not.Null, canvas.name + " at " + canvas.sortingOrder);
                if (canvas.isActiveAndEnabled)
                {
                    shown.Add((canvas.name, canvas.sortingOrder));
                }
            }

            Assert.That(shown.Count, Is.GreaterThan(10));
            var ties = shown.GroupBy(canvas => canvas.Order).Where(group => group.Count() > 1)
                .Select(group => string.Join(", ", group.Select(canvas => canvas.Name)))
                .ToArray();
            Assert.That(ties, Is.Empty);
        }

        private Canvas WindowCanvas(string name)
        {
            return inventory.GetComponentsInChildren<RectTransform>(true)
                .Single(rect => rect.name == name)
                .GetComponent<Canvas>();
        }

        // The layer whose order or stacked band holds an order, or null.
        private static UiLayer? LayerOf(int order)
        {
            foreach (var layer in UiLayers.All)
            {
                var layerOrder = UiLayers.SortingOrder(layer);
                if (order == layerOrder
                    || (UiLayers.IsStacked(layer) && order > layerOrder && order < layerOrder + UiLayers.StackBand))
                {
                    return layer;
                }
            }

            return null;
        }

        private static Vector2 ScreenCenter(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return (corners[0] + corners[2]) / 2f;
        }
    }
}
