using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.Client.Ui;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Mmorpg.Client.Tests.PlayMode
{
    // Draws a committed snapshot in the temporary inventory panel without a GameService.
    public sealed class TemporaryInventoryPanelPlayModeTests
    {
        // Port 9 refuses connections, so no request can reach a real GameService.
        private const string UnreachableGameServiceUrl = "http://127.0.0.1:9";

        [UnityTest]
        public IEnumerator CompleteViewDrawsPaperDollBlockedSlotCarryMeterAndTooltip()
        {
            var previousBaseUrl = ClientSession.GameServiceBaseUrl;
            ConfigureGameServiceUrl(UnreachableGameServiceUrl);
            ClientSession.Clear();
            var worldLoad = ClientSceneLoader.LoadWorldScene(SceneNames.DevelopmentWorld1);
            yield return worldLoad;
            Assert.That(worldLoad.Status, Is.EqualTo(AsyncOperationStatus.Succeeded));
            yield return null;

            var controller = UnityEngine.Object.FindAnyObjectByType<InventoryClientController>();
            var panel = UnityEngine.Object.FindAnyObjectByType<TemporaryInventoryPanel>();
            Assert.That(controller, Is.Not.Null);
            Assert.That(panel, Is.Not.Null);
            var catalog = controller.State.Catalog;
            Assert.That(catalog, Is.Not.Null);

            var characterId = Guid.NewGuid();
            ClientSession.Auth = new AuthResponse { accountId = Guid.NewGuid().ToString() };
            ClientSession.SelectedCharacter = new CharacterResponse
            {
                id = characterId.ToString(),
                name = "Panel Probe"
            };
            try
            {
                // The controller observes the selected character on its next update.
                yield return null;
                var snapshot = CreateSnapshot(catalog, characterId, out var equippedSlotId, out var blockedSlotId);
                Assert.That(
                    controller.State.ApplyFull(snapshot),
                    Is.EqualTo(InventorySnapshotApplyResult.Applied));

                panel.SetOpen(true, InventoryPanelMode.Complete);
                yield return null;
                Canvas.ForceUpdateCanvases();

                var views = panel.GetComponentsInChildren<InventorySlotView>();
                Assert.That(
                    views.Count(view => view.SlotKey.StartsWith("equipment:", StringComparison.Ordinal)),
                    Is.EqualTo(catalog.Gameplay.EquipmentSlots.Length));
                Assert.That(
                    views.Count(view => view.SlotKey.StartsWith("inventory:", StringComparison.Ordinal)),
                    Is.EqualTo(snapshot.PermanentInventory.Slots.Count));
                Assert.That(
                    views.Count(view => view.SlotKey.StartsWith("secure:", StringComparison.Ordinal)),
                    Is.EqualTo(snapshot.SecureContainer.Contents.Slots.Count));
                Assert.That(View(views, "equipment:" + equippedSlotId).VisualState, Is.EqualTo(InventorySlotVisualState.Occupied));
                Assert.That(View(views, "inventory:0").VisualState, Is.EqualTo(InventorySlotVisualState.Occupied));
                Assert.That(View(views, "inventory:1").VisualState, Is.EqualTo(InventorySlotVisualState.Empty));
                if (blockedSlotId != null)
                {
                    Assert.That(
                        View(views, "equipment:" + blockedSlotId).VisualState,
                        Is.EqualTo(InventorySlotVisualState.Blocked));
                }

                // Every slot in every module shares one size.
                var sizes = views
                    .Select(view => ((RectTransform)view.transform).rect.size)
                    .Distinct()
                    .ToArray();
                Assert.That(sizes.Length, Is.EqualTo(1), string.Join(", ", sizes));
                Assert.That(sizes[0].x, Is.GreaterThan(0f));

                var fill = panel.GetComponentsInChildren<RectTransform>()
                    .Single(rect => rect.name == "Fill" && rect.parent.name == "Track");
                Assert.That(
                    fill.anchorMax.x,
                    Is.EqualTo(CarryLoadPresentation.From(snapshot).FillFraction).Within(0.0001f));

                var tooltip = panel.GetComponentInChildren<InventoryTooltipView>(true);
                Assert.That(tooltip, Is.Not.Null);
                Assert.That(tooltip.IsVisible, Is.False);
                var trigger = View(views, "equipment:" + equippedSlotId)
                    .GetComponent<InventoryTooltipTrigger>();
                var pointer = new PointerEventData(EventSystem.current) { position = new Vector2(200f, 200f) };
                trigger.OnPointerEnter(pointer);
                Assert.That(tooltip.IsVisible, Is.True);
                Assert.That(
                    tooltip.Text,
                    Does.Contain(catalog.GetDisplayName(
                        snapshot.Equipment.Single(slot => slot.SlotId == equippedSlotId).Item.DefinitionId)));
                trigger.OnPointerExit(pointer);
                Assert.That(tooltip.IsVisible, Is.False);

                // The character module keeps its size and place in the character-only view.
                var characterModule = panel.GetComponentsInChildren<RectTransform>(true)
                    .Single(rect => rect.name == TemporaryInventoryPanel.CharacterModuleName);
                var completeRect = characterModule.rect;
                panel.SetOpen(true, InventoryPanelMode.CharacterOnly);
                yield return null;
                Assert.That(panel.IsEquipmentModuleVisible, Is.False);
                Assert.That(characterModule.rect, Is.EqualTo(completeRect));
            }
            finally
            {
                panel.SetOpen(false);
                ClientSession.Clear();
                ConfigureGameServiceUrl(previousBaseUrl);
            }
        }

        // A right-click on an equipped item runs its move: without a simulation session the
        // controller refuses it, and the status line says so. The right button never drags.
        [UnityTest]
        public IEnumerator RightClickRunsTheSlotMoveAndTheRightButtonNeverDrags()
        {
            var previousBaseUrl = ClientSession.GameServiceBaseUrl;
            ConfigureGameServiceUrl(UnreachableGameServiceUrl);
            ClientSession.Clear();
            var worldLoad = ClientSceneLoader.LoadWorldScene(SceneNames.DevelopmentWorld1);
            yield return worldLoad;
            Assert.That(worldLoad.Status, Is.EqualTo(AsyncOperationStatus.Succeeded));
            yield return null;

            var controller = UnityEngine.Object.FindAnyObjectByType<InventoryClientController>();
            var panel = UnityEngine.Object.FindAnyObjectByType<TemporaryInventoryPanel>();
            var characterId = Guid.NewGuid();
            ClientSession.Auth = new AuthResponse { accountId = Guid.NewGuid().ToString() };
            ClientSession.SelectedCharacter = new CharacterResponse
            {
                id = characterId.ToString(),
                name = "Right Click Probe"
            };
            try
            {
                yield return null;
                var snapshot = CreateSnapshot(controller.State.Catalog, characterId, out var equippedSlotId, out _);
                Assert.That(
                    controller.State.ApplyFull(snapshot),
                    Is.EqualTo(InventorySnapshotApplyResult.Applied));
                panel.SetOpen(true, InventoryPanelMode.Complete);
                yield return null;

                var slot = View(panel.GetComponentsInChildren<InventorySlotView>(), "equipment:" + equippedSlotId);
                var rightPress = new PointerEventData(EventSystem.current)
                {
                    button = PointerEventData.InputButton.Right,
                    position = new Vector2(200f, 200f)
                };
                slot.GetComponent<InventoryDragSource>().OnBeginDrag(rightPress);
                Assert.That(slot.GetComponentInParent<InventoryDragCoordinator>().IsDragging, Is.False);

                slot.GetComponent<InventorySlotRightClick>().OnPointerClick(rightPress);
                yield return null;

                Assert.That(
                    panel.GetComponentsInChildren<Text>().Any(text =>
                        text.text.Contains("active simulation session")),
                    Is.True,
                    "The status line names why the right-click move was not sent.");
            }
            finally
            {
                panel.SetOpen(false);
                ClientSession.Clear();
                ConfigureGameServiceUrl(previousBaseUrl);
            }
        }

        [UnityTest]
        public IEnumerator EquippedArrowSlotFollowsTheLiveCountOfTheOwnerCombatState()
        {
            var previousBaseUrl = ClientSession.GameServiceBaseUrl;
            ConfigureGameServiceUrl(UnreachableGameServiceUrl);
            ClientSession.Clear();
            var worldLoad = ClientSceneLoader.LoadWorldScene(SceneNames.DevelopmentWorld1);
            yield return worldLoad;
            Assert.That(worldLoad.Status, Is.EqualTo(AsyncOperationStatus.Succeeded));
            yield return null;

            var controller = UnityEngine.Object.FindAnyObjectByType<InventoryClientController>();
            var combat = UnityEngine.Object.FindAnyObjectByType<CombatClientController>();
            var panel = UnityEngine.Object.FindAnyObjectByType<TemporaryInventoryPanel>();
            Assert.That(controller, Is.Not.Null);
            Assert.That(combat, Is.Not.Null);
            Assert.That(panel, Is.Not.Null);
            var catalog = controller.State.Catalog;
            Assert.That(catalog, Is.Not.Null);

            var characterId = Guid.NewGuid();
            ClientSession.Auth = new AuthResponse { accountId = Guid.NewGuid().ToString() };
            ClientSession.SelectedCharacter = new CharacterResponse
            {
                id = characterId.ToString(),
                name = "Arrow Probe"
            };
            try
            {
                yield return null;
                var snapshot = CreateSnapshot(catalog, characterId, out _, out _);
                var arrows = catalog.Definitions.First(definition =>
                    definition.Ammunition != null
                    && definition.EquipmentSlots != null
                    && definition.EquipmentSlots.Contains(AmmunitionSlotId));
                var stack = CreateItem(arrows.Id, 500);
                snapshot = WithEquipped(snapshot, AmmunitionSlotId, stack);
                Assert.That(
                    controller.State.ApplyFull(snapshot),
                    Is.EqualTo(InventorySnapshotApplyResult.Applied));

                panel.SetOpen(true, InventoryPanelMode.CharacterAndEquipment);
                yield return null;
                Assert.That(ArrowSlotQuantity(panel), Is.EqualTo("500"));

                // The worker's live count already subtracts shots that are not committed yet.
                Assert.That(combat.State.TryApplyOwner(Owner(1, stack.ItemInstanceId, 482), out var error), Is.True, error);
                Assert.That(ArrowSlotQuantity(panel), Is.EqualTo("482"));
                Assert.That(combat.State.TryApplyOwner(Owner(2, stack.ItemInstanceId, 0), out error), Is.True, error);
                Assert.That(ArrowSlotQuantity(panel), Is.EqualTo("0"));

                // A live count of another stack leaves the committed quantity.
                Assert.That(combat.State.TryApplyOwner(Owner(3, Guid.NewGuid(), 7), out error), Is.True, error);
                Assert.That(ArrowSlotQuantity(panel), Is.EqualTo("500"));

                // Redrawing keeps the live count.
                Assert.That(combat.State.TryApplyOwner(Owner(4, stack.ItemInstanceId, 481), out error), Is.True, error);
                panel.SetOpen(false);
                panel.SetOpen(true, InventoryPanelMode.CharacterAndEquipment);
                yield return null;
                Assert.That(ArrowSlotQuantity(panel), Is.EqualTo("481"));
            }
            finally
            {
                combat.State.Clear();
                panel.SetOpen(false);
                ClientSession.Clear();
                ConfigureGameServiceUrl(previousBaseUrl);
            }
        }

        private const string AmmunitionSlotId = "ammunition";

        private static string ArrowSlotQuantity(TemporaryInventoryPanel panel)
        {
            var slot = View(panel.GetComponentsInChildren<InventorySlotView>(), "equipment:" + AmmunitionSlotId);
            var label = slot.transform.Find("Quantity");
            Assert.That(label, Is.Not.Null);
            return label.GetComponent<Text>().text;
        }

        private static CharacterInventorySnapshot WithEquipped(
            CharacterInventorySnapshot source,
            string slotId,
            InventoryItem item)
        {
            return new CharacterInventorySnapshot(
                source.CharacterId,
                source.CatalogRevision,
                source.ItemStateRevision,
                source.PermanentInventory,
                source.Equipment
                    .Select(slot => slot.SlotId == slotId
                        ? new InventoryEquipmentSlot(slot.SlotId, slot.SortOrder, item)
                        : slot)
                    .ToArray(),
                source.EquippedBag,
                source.Bank,
                source.SecureContainer,
                source.RecoveryStorage,
                source.CarriedWeight,
                source.CarryCapacity,
                source.LoadRatioBasisPoints,
                source.MovementMultiplierBasisPoints);
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

        // Picks items by their rules instead of their ids, so content changes keep the test valid.
        private static CharacterInventorySnapshot CreateSnapshot(
            ClientItemCatalog catalog,
            Guid characterId,
            out string equippedSlotId,
            out string blockedSlotId)
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
            equippedSlotId = equipped.EquipmentSlots[0];
            blockedSlotId = blocker?.BlockedEquipmentSlots.First(slotId =>
                slotIds.Contains(slotId) && slotId != blocker.EquipmentSlots[0]);
            var equippedSlot = equippedSlotId;
            var equipment = slotIds
                .Select((slotId, index) => new InventoryEquipmentSlot(
                    slotId,
                    index,
                    slotId == equippedSlot ? CreateItem(equipped.Id, 1) : null))
                .ToArray();

            var stackable = catalog.Definitions.First(definition => definition.MaximumStackSize > 1);
            var settings = catalog.Gameplay.Character;
            var secureTier = catalog.Gameplay.SecureContainerTiers.Single(tier =>
                tier.Id == catalog.Gameplay.BaseSecureContainerTierId);
            var carried = settings.BaseCarryCapacity / 2;
            return new CharacterInventorySnapshot(
                characterId,
                catalog.Revision,
                3,
                CreateContainer(
                    "permanent_inventory",
                    settings.InventorySlotCount,
                    new Dictionary<int, InventoryItem> { [0] = CreateItem(stackable.Id, 2) }),
                equipment,
                null,
                CreateContainer("bank", settings.BankSlotCount, new Dictionary<int, InventoryItem>()),
                new SecureContainerInventory(
                    secureTier.Id,
                    1,
                    CreateContainer("secure_container", secureTier.SlotCapacity, new Dictionary<int, InventoryItem>())),
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

        private static void ConfigureGameServiceUrl(string baseUrl)
        {
            var config = ScriptableObject.CreateInstance<ClientConfig>();
            JsonUtility.FromJsonOverwrite(
                "{\"gameServiceBaseUrl\":\"" + baseUrl + "\"}",
                config);
            ClientSession.Configure(config);
            UnityEngine.Object.Destroy(config);
        }
    }
}
