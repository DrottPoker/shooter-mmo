using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    public enum InventoryPanelMode
    {
        Complete,
        CharacterAndEquipment,
        CharacterOnly
    }

    [DisallowMultipleComponent]
    public sealed partial class TemporaryInventoryPanel : MonoBehaviour
    {
        public const string EquipmentModuleName = "EquipmentPanel";
        public const string ContextModuleName = "ContextPanel";
        public const string CharacterModuleName = "Character InventoryPanel";

        // The inventory canvas draws above the world interaction panel and the combat HUD.
        public const int CanvasSortingOrder = 100;

        private const int StorageColumns = 5;
        private const float SlotHeight = 64f;
        private const float MinimumSlotWidth = 72f;
        private const float MaximumSlotWidth = 120f;
        private const float SlotSpacing = 6f;
        private const float PaperDollSpacing = 10f;
        private const float ContentPadding = 4f;
        private const float HeaderHeight = 46f;
        private const float FooterMargin = 10f;
        // How long an undone item move stays marked in red.
        private const float RollbackMarkSeconds = 1.5f;

        private InventoryClientController controller;
        private CorpseClientController corpseController;
        private WorldInteractionClientController worldInteractionController;
        private CombatClientController combatController;
        private WorldMenuController worldMenu;
        private LocalPlayerInput localPlayerInput;
        private GameObject canvasObject;
        private GameObject rootObject;
        private RectTransform equipmentPanel;
        private RectTransform contextPanel;
        private RectTransform characterPanel;
        private RectTransform equipmentContent;
        private RectTransform contextContent;
        private RectTransform characterContent;
        private RectTransform characterFooter;
        private RectTransform characterScroll;
        private Text equipmentHeader;
        private Text contextHeader;
        private Text characterHeader;
        private Text equipmentHeaderDetail;
        private Text contextHeaderDetail;
        private Text characterHeaderDetail;
        private InventoryDragCoordinator dragCoordinator;
        private InventoryTooltipView tooltip;
        private Vector2 slotSize = new Vector2(MaximumSlotWidth, SlotHeight);
        private bool isOpen;
        private InventoryPanelMode mode = InventoryPanelMode.Complete;
        private InventoryContextKind context = InventoryContextKind.None;
        private Guid selectedItemId;
        private bool splitMode;
        private bool destroyConfirmation;
        private string splitQuantityText = "1";
        private string corpseQuantityText = "1";
        private bool corpsePartialLoot;
        private string localStatus = "Drag an item onto a highlighted destination.";
        private LiveAmmunitionSlot liveAmmunition;
        private bool rebuildDeferred;
        private readonly HashSet<Guid> rolledBackItemIds = new HashSet<Guid>();
        private float rollbackMarkUntil;

        public bool IsOpen
        {
            get { return isOpen; }
        }

        public InventoryPanelMode Mode
        {
            get { return mode; }
        }

        public bool IsEquipmentModuleVisible
        {
            get { return isOpen && equipmentPanel != null && equipmentPanel.gameObject.activeSelf; }
        }

        public bool IsContextModuleVisible
        {
            get { return isOpen && contextPanel != null && contextPanel.gameObject.activeSelf; }
        }

        public bool IsCharacterModuleVisible
        {
            get { return isOpen && characterPanel != null && characterPanel.gameObject.activeSelf; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(
            InventoryClientController inventory,
            CorpseClientController corpses,
            WorldInteractionClientController worldInteraction,
            CombatClientController combat,
            WorldMenuController menu)
        {
            controller = inventory;
            corpseController = corpses;
            worldInteractionController = worldInteraction;
            combatController = combat;
            worldMenu = menu;
        }

        private void Start()
        {
            if (controller != null)
            {
                controller.State.Changed += Rebuild;
                controller.State.PredictionRolledBack += OnPredictionRolledBack;
            }

            if (corpseController != null)
            {
                corpseController.State.Changed += OnCorpseStateChanged;
            }

            if (worldInteractionController != null)
            {
                worldInteractionController.InventoryCapabilityOpened += OnInventoryCapabilityOpened;
                worldInteractionController.State.Changed += OnWorldInteractionStateChanged;
            }

            if (combatController != null)
            {
                combatController.State.Changed += OnCombatStateChanged;
            }

            FindPlayerPresentation();
            BuildUi();
            SetOpen(false);
        }

        private void Update()
        {
            if (localPlayerInput == null)
            {
                FindPlayerPresentation();
            }

            if (rolledBackItemIds.Count > 0 && Time.unscaledTime >= rollbackMarkUntil)
            {
                rolledBackItemIds.Clear();
                rebuildDeferred = true;
            }

            if (rebuildDeferred && (dragCoordinator == null || !dragCoordinator.IsDragging))
            {
                Rebuild();
            }

            if (localPlayerInput != null)
            {
                if (localPlayerInput.ToggleCharacterInventoryPressedThisFrame)
                {
                    ToggleMode(InventoryPanelMode.CharacterOnly);
                }
                else if (localPlayerInput.ToggleEquipmentInventoryPressedThisFrame)
                {
                    ToggleMode(InventoryPanelMode.CharacterAndEquipment);
                }
                else if (localPlayerInput.ToggleInventoryPressedThisFrame)
                {
                    ToggleMode(InventoryPanelMode.Complete);
                }
                else if (isOpen
                    && localPlayerInput.CancelPressedThisFrame
                    && (worldMenu == null || !worldMenu.CancelConsumedThisFrame))
                {
                    // The targeting controller closes the interaction on Cancel. An Escape
                    // that only closed the world menu leaves the panel open.
                    Hide(false);
                }
            }
        }

        private void OnDisable()
        {
            if (isOpen)
            {
                SetOpen(false);
            }
        }

        private void OnDestroy()
        {
            if (controller != null)
            {
                controller.State.Changed -= Rebuild;
                controller.State.PredictionRolledBack -= OnPredictionRolledBack;
            }

            if (corpseController != null)
            {
                corpseController.State.Changed -= OnCorpseStateChanged;
            }

            if (worldInteractionController != null)
            {
                worldInteractionController.InventoryCapabilityOpened -= OnInventoryCapabilityOpened;
                worldInteractionController.State.Changed -= OnWorldInteractionStateChanged;
            }

            if (combatController != null)
            {
                combatController.State.Changed -= OnCombatStateChanged;
            }
        }

        // Every shot changes the live arrow count. The drawn slot updates in place, so a
        // shot never redraws the panel or cancels a drag.
        private void OnCombatStateChanged()
        {
            if (!isOpen || liveAmmunition == null || liveAmmunition.QuantityText == null)
            {
                return;
            }

            var live = LiveAmmunitionQuantity(liveAmmunition.Item);
            if (live == liveAmmunition.DrawnQuantity)
            {
                return;
            }

            liveAmmunition.DrawnQuantity = live;
            liveAmmunition.QuantityText.text = InventorySlotPresentation.LiveQuantityText(
                liveAmmunition.Item.Quantity,
                live);
            if (liveAmmunition.Tooltip != null && controller != null && controller.State.Catalog != null)
            {
                liveAmmunition.Tooltip.Configure(
                    tooltip,
                    InventorySlotPresentation.BuildItemTooltip(
                        controller.State.Catalog,
                        liveAmmunition.Item,
                        InventorySlotPresentation.LiveQuantityDescription(
                            liveAmmunition.Description,
                            liveAmmunition.Item.Quantity,
                            live)));
            }
        }

        // The worker's live count while it names the equipped stack, else the committed one.
        private int LiveAmmunitionQuantity(InventoryItem item)
        {
            return CombatLoadoutRules.DisplayedAmmunitionQuantity(
                item.ItemInstanceId,
                item.Quantity,
                combatController != null ? combatController.State.Owner : null);
        }

        public void SetOpen(bool value)
        {
            SetOpen(value, value ? InventoryPanelMode.Complete : mode);
        }

        public void SetOpen(bool value, InventoryPanelMode requestedMode)
        {
            if (value)
            {
                Show(requestedMode);
                return;
            }

            Hide(true);
        }

        private void Show(InventoryPanelMode requestedMode)
        {
            isOpen = true;
            mode = requestedMode;
            ApplyModeVisibility();
            if (rootObject != null)
            {
                rootObject.SetActive(true);
            }

            controller?.EnsureFullState();
            Rebuild();
        }

        // An open corpse view belongs to the panel, so hiding the panel closes it unless
        // the caller already closes the interaction through its own path.
        private void Hide(bool closeCorpseView)
        {
            isOpen = false;
            if (rootObject != null)
            {
                rootObject.SetActive(false);
            }

            if (closeCorpseView
                && corpseController?.State.ActiveView != null
                && corpseController.State.PendingOperationId == Guid.Empty)
            {
                worldInteractionController?.TryCloseActive(out _);
            }

            dragCoordinator?.Cancel();
            ClearSelection();
        }

        private void ToggleMode(InventoryPanelMode requestedMode)
        {
            if (isOpen && mode == requestedMode)
            {
                Hide(true);
                return;
            }

            // A corpse view is presented only by the complete view's context module.
            if (requestedMode != InventoryPanelMode.Complete
                && corpseController?.State.ActiveView != null
                && corpseController.State.PendingOperationId == Guid.Empty)
            {
                worldInteractionController?.TryCloseActive(out _);
            }

            dragCoordinator?.Cancel();
            ClearSelection();
            Show(requestedMode);
        }

        // The undone items flash red where they returned to, and the status line says why.
        private void OnPredictionRolledBack(InventoryPredictionRollback rollback)
        {
            rolledBackItemIds.Clear();
            rolledBackItemIds.UnionWith(rollback.ItemInstanceIds);
            rollbackMarkUntil = Time.unscaledTime + RollbackMarkSeconds;
            localStatus = "Item move undone: " + rollback.Error.Message;
            Rebuild();
        }

        private void OnCorpseStateChanged()
        {
            if (corpseController?.State.ActiveView != null)
            {
                context = InventoryContextKind.Corpse;
                // Only the complete view shows the context module that presents the corpse.
                if (!isOpen || mode != InventoryPanelMode.Complete)
                {
                    Show(InventoryPanelMode.Complete);
                    return;
                }
            }

            Rebuild();
        }

        private void FindPlayerPresentation()
        {
            var localPlayer = FindAnyObjectByType<LocalPlayerController>();
            localPlayerInput = localPlayer != null
                ? localPlayer.PlayerInput
                : FindAnyObjectByType<LocalPlayerInput>();
        }

        private void BuildUi()
        {
            EnsureEventSystem();
            canvasObject = new GameObject(
                "TemporaryInventoryCanvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CanvasSortingOrder;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            dragCoordinator = canvasObject.AddComponent<InventoryDragCoordinator>();
            dragCoordinator.Configure(
                canvasObject.GetComponent<RectTransform>(),
                TemporaryUiKit.Font);

            rootObject = CreateUiObject("InventoryRoot", canvasObject.transform);
            var rootRect = rootObject.GetComponent<RectTransform>();
            Stretch(rootRect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            // Keeps world clicks from passing through while the inventory is open.
            var blocker = rootObject.AddComponent<Image>();
            blocker.color = new Color(0f, 0f, 0f, 0.22f);

            // The character module keeps its place in every mode, so B, C, and I only add
            // columns around it.
            equipmentContent = CreateModule(
                EquipmentModuleName,
                new Vector2(0.012f, 0.47f),
                new Vector2(0.292f, 0.96f),
                false,
                out equipmentPanel,
                out equipmentHeader,
                out equipmentHeaderDetail,
                out _,
                out _);
            contextContent = CreateModule(
                ContextModuleName,
                new Vector2(0.302f, 0.04f),
                new Vector2(0.648f, 0.96f),
                false,
                out contextPanel,
                out contextHeader,
                out contextHeaderDetail,
                out _,
                out _);
            characterContent = CreateModule(
                CharacterModuleName,
                new Vector2(0.658f, 0.04f),
                new Vector2(0.988f, 0.96f),
                true,
                out characterPanel,
                out characterHeader,
                out characterHeaderDetail,
                out characterFooter,
                out characterScroll);
            tooltip = InventoryTooltipView.Create(rootRect, dragCoordinator);
            ApplyModeVisibility();
        }

        private void ApplyModeVisibility()
        {
            if (equipmentPanel == null || contextPanel == null || characterPanel == null)
            {
                return;
            }

            equipmentPanel.gameObject.SetActive(
                mode != InventoryPanelMode.CharacterOnly);
            contextPanel.gameObject.SetActive(
                mode == InventoryPanelMode.Complete);
            characterPanel.gameObject.SetActive(true);
        }

        private void Rebuild()
        {
            if (!isOpen
                || equipmentContent == null
                || contextContent == null
                || characterContent == null)
            {
                return;
            }

            // A confirmation that arrives while the player drags the next item must not cancel
            // that drag, so the panel redraws when the drag ends.
            if (dragCoordinator != null && dragCoordinator.IsDragging)
            {
                rebuildDeferred = true;
                return;
            }

            rebuildDeferred = false;

            if (tooltip != null)
            {
                tooltip.Hide(null);
            }

            ClearChildren(equipmentContent);
            ClearChildren(contextContent);
            ClearChildren(characterContent);
            ClearChildren(characterFooter);
            equipmentHeader.text = "Equipment";
            equipmentHeaderDetail.text = string.Empty;
            contextHeader.text = ContextTitle(context);
            contextHeaderDetail.text = string.Empty;
            characterHeader.text = "Inventory";
            characterHeaderDetail.text = string.Empty;
            if (controller == null || controller.State.Catalog == null)
            {
                var message = controller?.State.Error?.ToDisplayMessage()
                    ?? "Inventory catalog is unavailable.";
                CreateMessage(characterContent, message, TemporaryUiKit.DangerColor);
                FitCharacterFooter();
                return;
            }

            var state = controller.State;
            dragCoordinator?.Cancel();
            if (selectedItemId != Guid.Empty
                && !state.TryFindItem(selectedItemId, out _, out _))
            {
                ClearSelection();
            }

            slotSize = ResolveSlotSize();
            liveAmmunition = null;
            if (state.FullSnapshot != null)
            {
                characterHeaderDetail.text = VendorClientRules.FormatAmount(
                        state.Catalog,
                        state.FullSnapshot.Currency)
                    + "    Revision "
                    + state.FullSnapshot.ItemStateRevision.ToString(CultureInfo.InvariantCulture);
                equipmentHeaderDetail.text = state.FullSnapshot.Equipment.Count(slot => slot.Item != null)
                    + " / " + state.FullSnapshot.Equipment.Count + " equipped";
            }

            contextHeaderDetail.text = ContextDetail(state);

            if (equipmentPanel.gameObject.activeSelf)
            {
                DrawEquipment(state);
            }

            if (contextPanel.gameObject.activeSelf)
            {
                DrawContext(state);
            }

            DrawCharacterInventory(state);
            DrawSelectionFooter(state);
            FitCharacterFooter();
        }

        private void DrawEquipment(InventoryClientState state)
        {
            if (state.FullSnapshot == null)
            {
                CreateMessage(equipmentContent, StatusText(state), TemporaryUiKit.TextPrimary);
                return;
            }

            DrawStatus(equipmentContent, state);
            var equipment = state.FullSnapshot.Equipment;
            var slotsById = new Dictionary<string, InventoryEquipmentSlot>(StringComparer.Ordinal);
            foreach (var equipmentSlot in equipment)
            {
                if (equipmentSlot != null && !slotsById.ContainsKey(equipmentSlot.SlotId))
                {
                    slotsById.Add(equipmentSlot.SlotId, equipmentSlot);
                }
            }

            var cells = EquipmentPaperDollLayout.Arrange(slotsById.Keys, out var rows);
            var cellsByIndex = cells.ToDictionary(cell =>
                cell.Row * EquipmentPaperDollLayout.ColumnCount + cell.Column);
            var blocked = InventorySlotPresentation.FindBlockedEquipmentSlots(
                state.Catalog,
                equipment);
            var grid = CreateGrid(
                equipmentContent,
                EquipmentPaperDollLayout.ColumnCount,
                rows * EquipmentPaperDollLayout.ColumnCount,
                PaperDollSpacing,
                TextAnchor.UpperCenter);
            for (var index = 0; index < rows * EquipmentPaperDollLayout.ColumnCount; index++)
            {
                if (cellsByIndex.TryGetValue(index, out var cell))
                {
                    DrawEquipmentSlot(grid, state, slotsById[cell.SlotId], blocked);
                }
                else
                {
                    CreateUiObject("Spacer", grid);
                }
            }

            CreateMessage(
                equipmentContent,
                "Drag an item onto a green slot to equip it. On an occupied slot the equipped item swaps into the slot the new item came from. Drag a corpse Bag onto the Bag slot to swap Bags together with their contents.",
                TemporaryUiKit.TextMuted);
        }

        private void DrawContext(InventoryClientState state)
        {
            var tabs = TemporaryUiKit.CreateRow(contextContent, 36f, 6f);
            CreateTab(tabs, "Bank", InventoryContextKind.Bank);
            CreateTab(tabs, "Recovery", InventoryContextKind.RecoveryStorage);
            CreateTab(tabs, "Corpse", InventoryContextKind.Corpse);
            CreateTab(tabs, "Vendor", InventoryContextKind.Vendor);
            TemporaryUiKit.CreateButton(
                tabs,
                "Refresh",
                RefreshCurrentContext,
                true,
                TemporaryButtonTone.Quiet,
                96f);

            if (context == InventoryContextKind.None)
            {
                CreateMessage(
                    contextContent,
                    "Choose Bank or Recovery above, or talk to an NPC who offers them or trades. Click a corpse in the world to walk to it and open it.",
                    TemporaryUiKit.TextSecondary);
                CreateMessage(
                    contextContent,
                    "Reads are account-owned; mutations use the active SimulationWorker access policy.",
                    TemporaryUiKit.TextMuted);
                return;
            }

            if (context == InventoryContextKind.Corpse)
            {
                DrawCorpse(contextContent, state);
                return;
            }

            DrawStatus(contextContent, state);
            if (context == InventoryContextKind.Bank)
            {
                DrawContainer(contextContent, state, state.Bank, null, "bank");
                return;
            }

            if (context == InventoryContextKind.RecoveryStorage)
            {
                DrawRecovery(contextContent, state);
                return;
            }

            if (context == InventoryContextKind.Vendor)
            {
                DrawVendor(contextContent, state);
            }
        }

        private void CreateTab(Transform parent, string label, InventoryContextKind tabContext)
        {
            TemporaryUiKit.CreateButton(
                parent,
                label,
                () => SetContext(tabContext),
                true,
                context == tabContext ? TemporaryButtonTone.Active : TemporaryButtonTone.Normal);
        }

        private void DrawCharacterInventory(InventoryClientState state)
        {
            DrawStatus(characterContent, state);
            if (state.FullSnapshot == null)
            {
                CreateMessage(characterContent, StatusText(state), TemporaryUiKit.TextPrimary);
                return;
            }

            var snapshot = state.FullSnapshot;
            DrawCarryMeter(characterContent, CarryLoadPresentation.From(snapshot));
            DrawContainer(
                characterContent,
                state,
                snapshot.PermanentInventory,
                "Permanent Inventory",
                "inventory");
            if (snapshot.EquippedBag == null)
            {
                CreateSubheader(characterContent, "Bag", "none equipped");
                CreateMessage(
                    characterContent,
                    "Equip a Bag in the Bag slot for more slots and carry capacity.",
                    TemporaryUiKit.TextMuted);
            }
            else
            {
                DrawContainer(
                    characterContent,
                    state,
                    snapshot.EquippedBag.Contents,
                    state.Catalog.GetDisplayName(snapshot.EquippedBag.Item.DefinitionId),
                    "bag");
            }

            DrawContainer(
                characterContent,
                state,
                snapshot.SecureContainer.Contents,
                InventorySlotPresentation.SecureContainerTitle(
                    state.Catalog,
                    snapshot.SecureContainer.TierId),
                "secure");
        }

        private void DrawContainer(
            Transform parent,
            InventoryClientState state,
            InventoryContainer container,
            string title,
            string keyPrefix)
        {
            if (container == null)
            {
                if (title != null)
                {
                    CreateSubheader(parent, title, string.Empty);
                }

                CreateMessage(parent, "Container state is unavailable.", TemporaryUiKit.DangerColor);
                return;
            }

            if (title != null)
            {
                CreateSubheader(parent, title, UsedSlotsText(container));
            }

            var grid = CreateGrid(
                parent,
                StorageColumns,
                container.Slots.Count,
                SlotSpacing,
                TextAnchor.UpperLeft);
            foreach (var slot in container.Slots)
            {
                DrawContainerSlot(grid, state, container, slot, keyPrefix);
            }
        }

        private void DrawContainerSlot(
            Transform parent,
            InventoryClientState state,
            InventoryContainer container,
            InventorySlot slot,
            string keyPrefix)
        {
            var item = slot.Item;
            var specialized = !string.Equals(
                slot.SlotKind,
                BagSlotKindIds.General,
                StringComparison.Ordinal);
            var tagLabel = specialized
                ? InventorySlotPresentation.AcceptedTagsLabel(state.Catalog, slot.AcceptedTags)
                : string.Empty;
            var description = ContainerName(state, container.ContainerType)
                + " slot " + (slot.SlotIndex + 1)
                + (specialized ? ", accepts " + tagLabel : string.Empty);
            CreateSlot(parent, new SlotSpec
            {
                Key = keyPrefix + ":" + slot.SlotIndex,
                Item = item,
                CornerLabel = tagLabel,
                EmptyLabel = tagLabel,
                Selected = item != null && item.ItemInstanceId == selectedItemId,
                Interactable = item != null,
                OnClick = () => OnContainerSlotClicked(slot),
                DragPayload = item == null
                    ? null
                    : InventoryDragPayload.ForItem(item.ItemInstanceId),
                CanDrag = CanMoveItem(state, item),
                DropValidator = (InventoryDragPayload candidate, out string reason) =>
                    CanDropOnContainer(state, container, slot, candidate, out reason),
                OnDrop = candidate => OnContainerDrop(container, slot, candidate),
                Tooltip = item != null
                    ? InventorySlotPresentation.BuildItemTooltip(state.Catalog, item, description)
                    : specialized ? "<b>Empty</b>\n" + description : string.Empty
            });
        }

        private void DrawEquipmentSlot(
            Transform parent,
            InventoryClientState state,
            InventoryEquipmentSlot equipmentSlot,
            IReadOnlyDictionary<string, InventoryEquipmentSlot> blocked)
        {
            var item = equipmentSlot.Item;
            var slotName = InventorySlotPresentation.EquipmentSlotName(
                state.Catalog,
                equipmentSlot.SlotId);
            var isBlocked = item == null
                && blocked.TryGetValue(equipmentSlot.SlotId, out _);
            // The equipped arrows show the worker's live count, which already subtracts
            // shots that are not committed yet.
            string quantityText = null;
            var baseDescription = "Equipped in " + slotName;
            var description = baseDescription;
            var live = 0;
            var showsLiveCount = item != null
                && string.Equals(equipmentSlot.SlotId, ItemEquipmentSlotIds.Ammunition, StringComparison.Ordinal);
            if (showsLiveCount)
            {
                live = LiveAmmunitionQuantity(item);
                quantityText = InventorySlotPresentation.LiveQuantityText(item.Quantity, live);
                description = InventorySlotPresentation.LiveQuantityDescription(
                    baseDescription,
                    item.Quantity,
                    live);
            }

            var tooltipText = item != null
                ? InventorySlotPresentation.BuildItemTooltip(
                    state.Catalog,
                    item,
                    description)
                : "<b>" + slotName + "</b>\n" + (isBlocked
                    ? BlockedSlotText(state, blocked[equipmentSlot.SlotId])
                    : EmptyEquipmentSlotText(state, equipmentSlot.SlotId));
            var slotObject = CreateSlot(parent, new SlotSpec
            {
                Key = "equipment:" + equipmentSlot.SlotId,
                Item = item,
                QuantityText = quantityText,
                CornerLabel = slotName,
                EmptyLabel = slotName,
                Blocked = isBlocked,
                Selected = item != null && item.ItemInstanceId == selectedItemId,
                Interactable = item != null,
                OnClick = () => OnEquipmentSlotClicked(equipmentSlot),
                DragPayload = item == null
                    ? null
                    : InventoryDragPayload.ForItem(item.ItemInstanceId),
                CanDrag = CanMoveItem(state, item),
                DropValidator = (InventoryDragPayload candidate, out string reason) =>
                    CanDropOnEquipment(state, equipmentSlot, candidate, out reason),
                OnDrop = candidate => OnEquipmentDrop(equipmentSlot, candidate),
                Tooltip = tooltipText
            });
            if (showsLiveCount)
            {
                var quantityLabel = slotObject.transform.Find(QuantityLabelName);
                liveAmmunition = new LiveAmmunitionSlot
                {
                    Item = item,
                    Description = baseDescription,
                    DrawnQuantity = live,
                    QuantityText = quantityLabel != null ? quantityLabel.GetComponent<Text>() : null,
                    Tooltip = slotObject.GetComponent<InventoryTooltipTrigger>()
                };
            }
        }

        private static string BlockedSlotText(
            InventoryClientState state,
            InventoryEquipmentSlot blocker)
        {
            return "Blocked by " + state.Catalog.GetDisplayName(blocker.Item.DefinitionId)
                + " in " + InventorySlotPresentation.EquipmentSlotName(state.Catalog, blocker.SlotId)
                + ". Unequip it to use this slot.";
        }

        private static string EmptyEquipmentSlotText(InventoryClientState state, string slotId)
        {
            return state.Catalog.TryGetEquipmentSlot(slotId, out var definition)
                && definition.MaximumQuantity > 1
                    ? "Empty. Holds a stack of up to "
                        + definition.MaximumQuantity.ToString(CultureInfo.InvariantCulture) + "."
                    : "Empty";
        }

        private void DrawRecovery(Transform parent, InventoryClientState state)
        {
            var recovery = state.RecoveryStorage;
            if (recovery == null)
            {
                CreateMessage(parent, "Recovery Storage is unavailable.", TemporaryUiKit.TextPrimary);
                return;
            }

            if (recovery.Deliveries.Count == 0)
            {
                CreateMessage(parent, "Recovery Storage has no deliveries.", TemporaryUiKit.TextSecondary);
                return;
            }

            foreach (var delivery in recovery.Deliveries)
            {
                CreateSubheader(
                    parent,
                    RecoverySourceLabel(delivery.SourceKind),
                    "Revision " + delivery.Revision.ToString(CultureInfo.InvariantCulture));
                var grid = CreateGrid(
                    parent,
                    StorageColumns,
                    delivery.Items.Count,
                    SlotSpacing,
                    TextAnchor.UpperLeft);
                foreach (var deliveryItem in delivery.Items)
                {
                    var item = deliveryItem.Item;
                    CreateSlot(grid, new SlotSpec
                    {
                        Key = "recovery:" + delivery.DeliveryId + ":" + deliveryItem.ItemOrder,
                        Item = item,
                        Interactable = true,
                        DragPayload = InventoryDragPayload.ForRecoveryDelivery(delivery.DeliveryId),
                        CanDrag = state.CanMutate,
                        DropValidator = RejectRecoveryDestination,
                        Tooltip = InventorySlotPresentation.BuildItemTooltip(
                            state.Catalog,
                            item,
                            "Recovery delivery, claimed as a whole")
                    });
                }
            }

            CreateMessage(
                parent,
                "Drag any item of a delivery to Permanent Inventory or Bank. The whole delivery is claimed at once.",
                TemporaryUiKit.TextMuted);
        }

        private void DrawCorpse(Transform parent, InventoryClientState inventoryState)
        {
            if (corpseController == null)
            {
                CreateMessage(parent, "Corpse interaction state is unavailable.", TemporaryUiKit.DangerColor);
                return;
            }

            var corpseState = corpseController.State;
            if (corpseState.Error != null)
            {
                CreateMessage(parent, corpseState.Error.ToDisplayMessage(), TemporaryUiKit.DangerColor);
            }
            else if (!string.IsNullOrEmpty(corpseState.Notice))
            {
                CreateMessage(parent, corpseState.Notice, TemporaryUiKit.TextSecondary);
            }

            if (corpseState.ActiveView == null)
            {
                CreateMessage(
                    parent,
                    "Click a corpse in the world to walk to it and open it.",
                    TemporaryUiKit.TextSecondary);
                if (corpseState.NearbyCorpses.Count == 0)
                {
                    CreateMessage(parent, "No durable corpses are nearby.", TemporaryUiKit.TextMuted);
                    return;
                }

                CreateSubheader(parent, "Nearby", corpseState.NearbyCorpses.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var corpse in corpseState.NearbyCorpses)
                {
                    var label = (string.IsNullOrWhiteSpace(corpse.SourceDisplayName)
                            ? "Corpse"
                            : corpse.SourceDisplayName)
                        + (corpse.IsEmpty ? " (empty)" : string.Empty);
                    CreateMessage(parent, label, TemporaryUiKit.TextPrimary);
                }

                return;
            }

            var view = corpseState.ActiveView;
            CreateMessage(
                parent,
                "<b>" + view.SourceDisplayName + "</b>"
                    + "    <color=" + TemporaryUiKit.ColorTag(TemporaryUiKit.TextMuted) + ">Expires "
                    + DateTimeOffset.FromUnixTimeMilliseconds(
                        view.ExpiresAtUnixMilliseconds).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    + "    Revision " + view.Revision.ToString(CultureInfo.InvariantCulture) + "</color>",
                TemporaryUiKit.TextPrimary);
            var idle = corpseState.PendingOperationId == Guid.Empty;
            var actions = TemporaryUiKit.CreateRow(parent, 34f, 6f);
            TemporaryUiKit.CreateButton(
                actions,
                "Close",
                () => ExecuteCorpseClose(),
                idle,
                TemporaryButtonTone.Normal);
            TemporaryUiKit.CreateButton(
                actions,
                "Refresh",
                () => ExecuteCorpseRefresh(),
                idle,
                TemporaryButtonTone.Normal);
            TemporaryUiKit.CreateButton(
                actions,
                corpsePartialLoot ? "Partial stack: on" : "Partial stack: off",
                () =>
                {
                    corpsePartialLoot = !corpsePartialLoot;
                    Rebuild();
                },
                idle,
                corpsePartialLoot ? TemporaryButtonTone.Active : TemporaryButtonTone.Normal);
            var quantityInput = TemporaryUiKit.CreateIntegerField(actions, corpseQuantityText, 80f);
            quantityInput.onValueChanged.AddListener(value => corpseQuantityText = value);
            if (corpseState.Status == CorpseClientStatus.Busy)
            {
                CreateMessage(parent, "Waiting for authoritative corpse state.", TemporaryUiKit.WarningColor);
            }

            foreach (var section in view.Sections)
            {
                CreateSubheader(
                    parent,
                    CorpseSectionTitle(section.SectionKind),
                    section.Slots.Count(slot => slot.Item != null) + " / " + section.Slots.Count
                        + "    Revision " + section.ContainerRevision.ToString(CultureInfo.InvariantCulture));
                var grid = CreateGrid(
                    parent,
                    StorageColumns,
                    section.Slots.Count,
                    SlotSpacing,
                    TextAnchor.UpperLeft);
                foreach (var slot in section.Slots)
                {
                    DrawCorpseSlot(grid, inventoryState, view, section, slot);
                }
            }

            CreateMessage(
                parent,
                "Drag items between the corpse and your carried storage, or within the corpse. Typed slots accept only their shown type, stacks merge, other items swap, and an occupied Bag slot swaps the whole Bag. Drag a corpse item onto an equipment slot to equip it; an occupied slot swaps the equipped item into the corpse.",
                TemporaryUiKit.TextMuted);
        }

        private void DrawCorpseSlot(
            Transform parent,
            InventoryClientState inventoryState,
            CorpseLootView view,
            CorpseLootSection section,
            CorpseLootSlot slot)
        {
            var item = slot.Item;
            var inventoryItem = item?.ToInventoryItem();
            var label = CorpseSlotLabel(inventoryState.Catalog, slot);
            var description = CorpseSectionTitle(section.SectionKind)
                + (!string.IsNullOrWhiteSpace(slot.EquipmentSlotId)
                    ? ", " + label
                    : " slot " + (slot.SlotIndex + 1)
                        + (label.Length > 0 ? ", accepts " + label : string.Empty));
            CreateSlot(parent, new SlotSpec
            {
                Key = "corpse:" + section.SectionKind + ":" + slot.SlotIndex,
                Item = inventoryItem,
                CornerLabel = label,
                EmptyLabel = label,
                Interactable = item != null,
                DragPayload = item == null
                    ? null
                    : InventoryDragPayload.ForCorpseItem(view.CorpseId, item.ItemInstanceId),
                CanDrag = corpseController.State.CanMutate,
                DropValidator = (InventoryDragPayload candidate, out string reason) =>
                    CanDropOnCorpse(
                        inventoryState,
                        view,
                        section,
                        slot,
                        candidate,
                        out reason),
                OnDrop = candidate => OnCorpseDrop(view, section, slot, candidate),
                Tooltip = inventoryItem != null
                    ? InventorySlotPresentation.BuildItemTooltip(
                        inventoryState.Catalog,
                        inventoryItem,
                        description)
                    : label.Length > 0 ? "<b>Empty</b>\n" + description : string.Empty
            });
        }

        private static string CorpseSectionTitle(string sectionKind)
        {
            return sectionKind switch
            {
                CorpseSectionKindIds.Equipment => "Corpse Equipment",
                CorpseSectionKindIds.GeneralInventory => "Corpse Inventory",
                CorpseSectionKindIds.Bag => "Corpse Bag Contents",
                _ => "Corpse " + sectionKind
            };
        }

        // The equipment slot name or the accepted item types; empty for general slots.
        private static string CorpseSlotLabel(
            ClientItemCatalog catalog,
            CorpseLootSlot slot)
        {
            if (!string.IsNullOrWhiteSpace(slot.EquipmentSlotId))
            {
                return InventorySlotPresentation.EquipmentSlotName(catalog, slot.EquipmentSlotId);
            }

            return string.Equals(slot.SlotKind, BagSlotKindIds.General, StringComparison.Ordinal)
                ? string.Empty
                : InventorySlotPresentation.AcceptedTagsLabel(catalog, slot.AcceptedTags);
        }

        // Selected-item actions and the status line sit below the character module, so they
        // stay reachable in every mode.
        private void DrawSelectionFooter(InventoryClientState state)
        {
            if (selectedItemId == Guid.Empty
                || !state.TryFindItem(selectedItemId, out var item, out var location))
            {
                TemporaryUiKit.CreateLabel(
                    characterFooter,
                    "Click an item to select it. Drag an item onto a slot to move it.",
                    13,
                    FontStyle.Normal,
                    TemporaryUiKit.TextMuted,
                    20f);
                CreateStatusLine();
                return;
            }

            TemporaryUiKit.CreateLabel(
                characterFooter,
                "<b>" + state.Catalog.GetDisplayName(item.DefinitionId) + "</b>"
                    + (item.Quantity > 1
                        ? "  x" + item.Quantity.ToString(CultureInfo.InvariantCulture)
                        : string.Empty)
                    + "    <color=" + TemporaryUiKit.ColorTag(TemporaryUiKit.TextMuted) + ">"
                    + LocationLabel(state, location)
                    + "    Revision " + item.Revision.ToString(CultureInfo.InvariantCulture) + "</color>",
                15,
                FontStyle.Normal,
                TemporaryUiKit.TextPrimary,
                22f);
            var row = TemporaryUiKit.CreateRow(characterFooter, 34f, 6f);
            var input = TemporaryUiKit.CreateIntegerField(row, splitQuantityText, 76f);
            input.onValueChanged.AddListener(value => splitQuantityText = value);
            TemporaryUiKit.CreateButton(
                row,
                splitMode ? "Cancel split" : "Split",
                () =>
                {
                    splitMode = !splitMode;
                    destroyConfirmation = false;
                    localStatus = splitMode
                        ? "Drag the selected stack onto an empty compatible slot."
                        : "Split cancelled.";
                    Rebuild();
                },
                item.Quantity > 1 && CanMoveItem(state, item),
                splitMode ? TemporaryButtonTone.Active : TemporaryButtonTone.Normal);
            var canDestroy = InventoryTargetAdvisor.CanDestroy(
                state.Catalog,
                item,
                out var destroyReason);
            TemporaryUiKit.CreateButton(
                row,
                destroyConfirmation ? "Confirm destroy" : "Destroy",
                () => DestroySelected(item),
                canDestroy && CanMoveItem(state, item),
                destroyConfirmation ? TemporaryButtonTone.Danger : TemporaryButtonTone.Normal);
            TemporaryUiKit.CreateButton(
                row,
                "Clear",
                () =>
                {
                    ClearSelection();
                    Rebuild();
                },
                true,
                TemporaryButtonTone.Quiet,
                80f);
            if (!canDestroy)
            {
                TemporaryUiKit.CreateLabel(
                    characterFooter,
                    destroyReason,
                    13,
                    FontStyle.Normal,
                    TemporaryUiKit.TextMuted,
                    18f);
            }

            CreateStatusLine();
        }

        private void CreateStatusLine()
        {
            TemporaryUiKit.CreateLabel(
                characterFooter,
                localStatus,
                14,
                FontStyle.Normal,
                TemporaryUiKit.TextSecondary,
                22f);
        }

        private bool CanDropOnContainer(
            InventoryClientState state,
            InventoryContainer container,
            InventorySlot slot,
            InventoryDragPayload payload,
            out string reason)
        {
            reason = string.Empty;
            if (!TryResolveCurrentSlot(state, container, slot, out container, out slot))
            {
                reason = "This slot is no longer available.";
                return false;
            }

            // Item moves show at once and may follow pending ones; corpse loot and Recovery
            // claims wait for a confirmed state.
            if (payload.Kind == InventoryDragPayloadKind.Item
                ? !CanMoveItem(state, null)
                : !state.CanMutate)
            {
                reason = state.HasPendingOperations
                    ? "Wait until your item moves are confirmed."
                    : "Inventory mutation is unavailable while state is loading or busy.";
                return false;
            }

            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                return CanDropCorpseItemOnContainer(
                    state,
                    container,
                    slot,
                    payload,
                    out reason);
            }

            if (payload.Kind == InventoryDragPayloadKind.RecoveryDelivery)
            {
                var delivery = state.RecoveryStorage?.Deliveries.FirstOrDefault(candidate =>
                    candidate.DeliveryId == payload.RecoveryDeliveryId);
                if (delivery == null)
                {
                    reason = "The Recovery delivery is no longer available.";
                    return false;
                }

                return InventoryTargetAdvisor.CanClaimRecovery(
                    state,
                    delivery,
                    container,
                    out reason);
            }

            if (payload.Kind != InventoryDragPayloadKind.Item
                || !state.TryFindItem(payload.ItemInstanceId, out var item, out var source))
            {
                reason = "The dragged item is no longer available.";
                return false;
            }

            var splitDrag = IsSplitDrag(payload);
            if (slot.Item != null)
            {
                if (slot.Item.ItemInstanceId == item.ItemInstanceId)
                {
                    reason = "The item is already in this slot.";
                    return false;
                }

                if (splitDrag)
                {
                    reason = "Split stacks must be dropped into an empty slot.";
                    return false;
                }

                var targetLocation = new InventoryItemLocation(
                    InventoryItemLocationKind.Container,
                    container.ContainerId,
                    container.ContainerType,
                    slot.SlotIndex,
                    string.Empty,
                    Guid.Empty);
                if (InventoryTargetAdvisor.CanMerge(
                    state,
                    item,
                    source,
                    slot.Item,
                    targetLocation,
                    out reason))
                {
                    return true;
                }

                return InventoryTargetAdvisor.CanSwap(
                    state,
                    item,
                    source,
                    slot.Item,
                    targetLocation,
                    out reason);
            }

            // A split drag from an equipped stack unequips part of it, such as some arrows.
            if (source.Kind == InventoryItemLocationKind.Equipment && !splitDrag)
            {
                return InventoryTargetAdvisor.CanUnequip(
                    state,
                    item,
                    container,
                    slot,
                    out reason);
            }

            var movedQuantity = item.Quantity;
            if (splitDrag
                && (!int.TryParse(splitQuantityText, out movedQuantity)
                    || !InventoryTargetAdvisor.CanSplit(
                        state.Catalog,
                        item,
                        movedQuantity,
                        out reason)))
            {
                return false;
            }

            return InventoryTargetAdvisor.CanPlaceInContainer(
                state,
                item,
                source,
                container,
                slot,
                movedQuantity,
                out reason);
        }

        // A shown item moves at once, also while earlier moves wait for the server, unless it
        // is a split stack that the server has not confirmed yet.
        private static bool CanMoveItem(InventoryClientState state, InventoryItem item)
        {
            return (state.CanPredict || state.CanMutate)
                && (item == null || !state.IsPredictedItem(item.ItemInstanceId));
        }

        // The drawn slot can be older than the state, when a confirmation arrived during a drag.
        private static bool TryResolveCurrentSlot(
            InventoryClientState state,
            InventoryContainer drawnContainer,
            InventorySlot drawnSlot,
            out InventoryContainer container,
            out InventorySlot slot)
        {
            slot = null;
            var snapshot = state.FullSnapshot;
            container = snapshot == null
                ? null
                : new[]
                    {
                        snapshot.PermanentInventory,
                        snapshot.EquippedBag?.Contents,
                        snapshot.SecureContainer?.Contents,
                        state.Bank
                    }
                    .FirstOrDefault(candidate =>
                        candidate != null && candidate.ContainerId == drawnContainer.ContainerId);
            return container != null && container.TryGetSlot(drawnSlot.SlotIndex, out slot);
        }

        private static bool TryResolveCurrentEquipmentSlot(
            InventoryClientState state,
            InventoryEquipmentSlot drawnSlot,
            out InventoryEquipmentSlot equipmentSlot)
        {
            equipmentSlot = state.FullSnapshot?.Equipment.FirstOrDefault(candidate =>
                string.Equals(candidate.SlotId, drawnSlot.SlotId, StringComparison.Ordinal));
            return equipmentSlot != null;
        }

        private bool CanDropCorpseItemOnContainer(
            InventoryClientState state,
            InventoryContainer container,
            InventorySlot slot,
            InventoryDragPayload payload,
            out string reason)
        {
            var view = corpseController?.State.ActiveView;
            if (view == null
                || !corpseController.State.CanMutate
                || view.CorpseId != payload.CorpseId
                || !view.TryFindItem(
                    payload.ItemInstanceId,
                    out var corpseItem,
                    out var sourceSection,
                    out var sourceSlot))
            {
                reason = "The corpse item is no longer available for mutation.";
                return false;
            }

            return CorpseTargetAdvisor.CanLoot(
                state,
                corpseItem,
                sourceSection,
                sourceSlot,
                container,
                slot,
                RequestedCorpseQuantity(),
                out reason);
        }

        private bool CanDropOnCorpse(
            InventoryClientState state,
            CorpseLootView view,
            CorpseLootSection section,
            CorpseLootSlot slot,
            InventoryDragPayload payload,
            out string reason)
        {
            reason = string.Empty;
            if (!state.CanMutate
                || corpseController?.State.ActiveView == null
                || !corpseController.State.CanMutate
                || corpseController.State.ActiveView.CorpseId != view.CorpseId)
            {
                reason = "Inventory or corpse mutation is unavailable while state is loading or busy.";
                return false;
            }

            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                if (payload.CorpseId != view.CorpseId
                    || !view.TryFindItem(
                        payload.ItemInstanceId,
                        out var sourceItem,
                        out var sourceSection,
                        out var sourceSlot))
                {
                    reason = "The corpse source item is no longer available.";
                    return false;
                }

                return CorpseTargetAdvisor.CanMoveWithinCorpse(
                    state,
                    view,
                    sourceItem,
                    sourceSection,
                    sourceSlot,
                    section,
                    slot,
                    RequestedCorpseQuantity(),
                    out reason);
            }

            if (payload.Kind != InventoryDragPayloadKind.Item
                || !state.TryFindItem(payload.ItemInstanceId, out var item, out var source))
            {
                reason = "Only a current carried inventory item can enter corpse custody.";
                return false;
            }

            return CorpseTargetAdvisor.CanDeposit(
                state,
                item,
                source,
                section,
                slot,
                RequestedCorpseQuantity(),
                out reason);
        }

        // The partial-stack control requests an explicit quantity. An unparsable value is
        // zero, which the corpse advisor rejects.
        private int? RequestedCorpseQuantity()
        {
            if (!corpsePartialLoot)
            {
                return null;
            }

            return int.TryParse(corpseQuantityText, out var quantity) ? quantity : 0;
        }

        private bool CanDropOnEquipment(
            InventoryClientState state,
            InventoryEquipmentSlot equipmentSlot,
            InventoryDragPayload payload,
            out string reason)
        {
            reason = string.Empty;
            if (!TryResolveCurrentEquipmentSlot(state, equipmentSlot, out equipmentSlot))
            {
                reason = "This equipment slot is no longer available.";
                return false;
            }

            if (payload.Kind == InventoryDragPayloadKind.Item
                ? !CanMoveItem(state, null)
                : !state.CanMutate)
            {
                reason = state.HasPendingOperations
                    ? "Wait until your item moves are confirmed."
                    : "Inventory mutation is unavailable while state is loading or busy.";
                return false;
            }

            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                var view = corpseController?.State.ActiveView;
                if (view == null
                    || !corpseController.State.CanMutate
                    || view.CorpseId != payload.CorpseId
                    || !view.TryFindItem(
                        payload.ItemInstanceId,
                        out var corpseItem,
                        out var sourceSection,
                        out var sourceSlot))
                {
                    reason = "The corpse item is no longer available.";
                    return false;
                }

                // A corpse Bag swaps with the equipped Bag; any other corpse item equips whole.
                if (corpseItem.HasBagContents)
                {
                    return CorpseTargetAdvisor.CanSwapCorpseBagIntoEquipment(
                        state,
                        corpseItem,
                        sourceSection,
                        equipmentSlot,
                        out reason);
                }

                if (RequestedCorpseQuantity() != null)
                {
                    reason = "A partial corpse stack cannot be equipped. Turn the partial-stack control off.";
                    return false;
                }

                return CorpseTargetAdvisor.CanLootIntoEquipment(
                    state,
                    corpseItem,
                    sourceSection,
                    sourceSlot,
                    equipmentSlot,
                    out reason);
            }

            if (payload.Kind != InventoryDragPayloadKind.Item
                || !state.TryFindItem(payload.ItemInstanceId, out var item, out var source))
            {
                reason = "Only a current inventory item can be equipped.";
                return false;
            }

            if (IsSplitDrag(payload))
            {
                reason = "A split stack cannot be equipped.";
                return false;
            }

            if (IsEquippedStackTopUp(state, item, equipmentSlot))
            {
                return CanTopUpEquippedStack(state, item, source, equipmentSlot, out reason);
            }

            if (source.Kind == InventoryItemLocationKind.Equipment)
            {
                reason = equipmentSlot.Item != null && equipmentSlot.Item.ItemInstanceId == item.ItemInstanceId
                    ? "The item is already equipped in this slot."
                    : "Move the equipped item to an inventory slot before changing equipment slots.";
                return false;
            }

            // An occupied slot swaps: the equipped item moves into the slot the new item came from.
            return InventoryTargetAdvisor.CanEquip(
                state,
                item,
                source,
                equipmentSlot,
                out reason);
        }

        private void OnContainerDrop(
            InventoryContainer container,
            InventorySlot slot,
            InventoryDragPayload payload)
        {
            var state = controller.State;
            if (!CanDropOnContainer(state, container, slot, payload, out var validationError))
            {
                RejectDrop(validationError);
                return;
            }

            TryResolveCurrentSlot(state, container, slot, out container, out slot);

            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                SubmitCorpseLoot(container, slot, payload);
                return;
            }

            if (payload.Kind == InventoryDragPayloadKind.RecoveryDelivery)
            {
                var delivery = state.RecoveryStorage.Deliveries.First(candidate =>
                    candidate.DeliveryId == payload.RecoveryDeliveryId);
                ClaimRecovery(delivery, container.ContainerId);
                return;
            }

            state.TryFindItem(payload.ItemInstanceId, out var item, out var source);
            if (slot.Item != null)
            {
                var targetLocation = new InventoryItemLocation(
                    InventoryItemLocationKind.Container,
                    container.ContainerId,
                    container.ContainerType,
                    slot.SlotIndex,
                    string.Empty,
                    Guid.Empty);
                if (!InventoryTargetAdvisor.CanMerge(
                    state,
                    item,
                    source,
                    slot.Item,
                    targetLocation,
                    out _))
                {
                    Execute(
                        controller.TrySwap(item, slot.Item, out var swapError),
                        swapError,
                        "Swap submitted.");
                    return;
                }

                Execute(
                    controller.TryMerge(item, slot.Item, out var mergeError),
                    mergeError,
                    "Merge submitted.");
                return;
            }

            bool sent;
            string operationError;
            if (IsSplitDrag(payload))
            {
                int.TryParse(splitQuantityText, out var quantity);
                sent = controller.TrySplit(
                    item,
                    quantity,
                    container.ContainerId,
                    slot.SlotIndex,
                    out operationError);
            }
            else if (source.Kind == InventoryItemLocationKind.Equipment)
            {
                sent = controller.TryUnequip(
                    item,
                    container.ContainerId,
                    slot.SlotIndex,
                    out operationError);
            }
            else
            {
                sent = controller.TryRelocate(
                    item,
                    container.ContainerId,
                    slot.SlotIndex,
                    out operationError);
            }

            Execute(
                sent,
                operationError,
                IsSplitDrag(payload) ? "Split submitted." : "Move submitted.");
        }

        private void OnEquipmentDrop(
            InventoryEquipmentSlot equipmentSlot,
            InventoryDragPayload payload)
        {
            var state = controller.State;
            if (!CanDropOnEquipment(state, equipmentSlot, payload, out var validationError))
            {
                RejectDrop(validationError);
                return;
            }

            TryResolveCurrentEquipmentSlot(state, equipmentSlot, out equipmentSlot);

            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                var view = corpseController.State.ActiveView;
                view.TryFindItem(
                    payload.ItemInstanceId,
                    out var corpseItem,
                    out _,
                    out _);
                if (corpseItem.HasBagContents)
                {
                    ExecuteCorpse(
                        corpseController.TrySwapBag(corpseItem, out var bagError),
                        bagError,
                        "Atomic Bag swap submitted.");
                    return;
                }

                ExecuteCorpse(
                    corpseController.TryLootToEquipment(corpseItem, equipmentSlot, out var corpseError),
                    corpseError,
                    equipmentSlot.Item == null ? "Corpse equip submitted." : "Corpse equip swap submitted.");
                return;
            }

            state.TryFindItem(payload.ItemInstanceId, out var item, out _);
            if (IsEquippedStackTopUp(state, item, equipmentSlot))
            {
                Execute(
                    controller.TryMerge(item, equipmentSlot.Item, out var mergeError),
                    mergeError,
                    "Merge submitted.");
                return;
            }

            Execute(
                controller.TryEquip(item, equipmentSlot.SlotId, out var error),
                error,
                equipmentSlot.Item != null ? "Swap submitted." : "Equip submitted.");
        }

        // A stack of the equipped definition tops up the equipped stack, such as arrows in the
        // ammunition slot. Any other item on an occupied slot swaps with the equipped item.
        private static bool IsEquippedStackTopUp(
            InventoryClientState state,
            InventoryItem item,
            InventoryEquipmentSlot equipmentSlot)
        {
            return InventoryTargetAdvisor.IsEquippedStackTopUp(state.Catalog, item, equipmentSlot.Item);
        }

        private static bool CanTopUpEquippedStack(
            InventoryClientState state,
            InventoryItem item,
            InventoryItemLocation source,
            InventoryEquipmentSlot equipmentSlot,
            out string reason)
        {
            return InventoryTargetAdvisor.CanMerge(
                state,
                item,
                source,
                equipmentSlot.Item,
                InventoryItemLocation.ForEquipment(equipmentSlot.SlotId),
                out reason);
        }

        private void OnContainerSlotClicked(InventorySlot slot)
        {
            ToggleActionSelection(slot.Item);
        }

        private void OnEquipmentSlotClicked(InventoryEquipmentSlot equipmentSlot)
        {
            ToggleActionSelection(equipmentSlot.Item);
        }

        private void ToggleActionSelection(InventoryItem item)
        {
            if (item == null)
            {
                localStatus = "Drag an item onto this slot to move it.";
                Rebuild();
                return;
            }

            if (selectedItemId == item.ItemInstanceId)
            {
                ClearSelection();
                localStatus = "Selection cleared. Drag an item to move it.";
                Rebuild();
                return;
            }

            SelectItem(item);
        }

        private bool IsSplitDrag(InventoryDragPayload payload)
        {
            return splitMode
                && payload.Kind == InventoryDragPayloadKind.Item
                && payload.ItemInstanceId == selectedItemId;
        }

        private static bool RejectRecoveryDestination(
            InventoryDragPayload payload,
            out string reason)
        {
            reason = "Recovery Storage accepts system deliveries only. "
                + "Drag a Recovery delivery to Permanent Inventory or Bank to claim it.";
            return false;
        }

        private void RejectDrop(string reason)
        {
            localStatus = string.IsNullOrWhiteSpace(reason)
                ? "This inventory destination is not valid."
                : reason;
            Rebuild();
        }

        private void ClaimRecovery(RecoveryDelivery delivery, Guid destinationContainerId)
        {
            Execute(
                controller.TryClaimRecovery(delivery, destinationContainerId, out var error),
                error,
                "Recovery claim submitted.");
        }

        private void SubmitCorpseLoot(
            InventoryContainer destination,
            InventorySlot destinationSlot,
            InventoryDragPayload payload)
        {
            var view = corpseController?.State.ActiveView;
            if (view == null
                || !view.TryFindItem(
                    payload.ItemInstanceId,
                    out var corpseItem,
                    out _,
                    out _))
            {
                RejectDrop("The corpse item is no longer available.");
                return;
            }

            var quantity = RequestedCorpseQuantity() ?? corpseItem.Quantity;

            ExecuteCorpse(
                corpseController.TryLoot(
                    corpseItem,
                    destination,
                    destinationSlot,
                    quantity,
                    out var error),
                error,
                quantity < corpseItem.Quantity
                    ? "Partial corpse loot submitted."
                    : "Corpse loot submitted.");
        }

        private void OnCorpseDrop(
            CorpseLootView view,
            CorpseLootSection section,
            CorpseLootSlot slot,
            InventoryDragPayload payload)
        {
            var state = controller.State;
            if (!CanDropOnCorpse(
                    state,
                    view,
                    section,
                    slot,
                    payload,
                    out var validationError))
            {
                RejectDrop(validationError);
                return;
            }

            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                view.TryFindItem(
                    payload.ItemInstanceId,
                    out var corpseItem,
                    out _,
                    out _);
                var corpseQuantity = RequestedCorpseQuantity() ?? corpseItem.Quantity;

                ExecuteCorpse(
                    corpseController.TryMoveWithinCorpse(
                        corpseItem,
                        section,
                        slot,
                        corpseQuantity,
                        out var moveError),
                    moveError,
                    corpseQuantity < corpseItem.Quantity
                        ? "Partial corpse rearrangement submitted."
                        : "Corpse rearrangement submitted.");
                return;
            }

            state.TryFindItem(payload.ItemInstanceId, out var item, out var source);
            if (CorpseTargetAdvisor.IsBagSwap(state, item, source, section, slot))
            {
                ExecuteCorpse(
                    corpseController.TrySwapBag(slot.Item, out var bagError),
                    bagError,
                    "Atomic Bag swap submitted.");
                return;
            }

            var quantity = RequestedCorpseQuantity() ?? item.Quantity;
            ExecuteCorpse(
                corpseController.TryDeposit(
                    item,
                    section,
                    slot,
                    quantity,
                    out var error),
                error,
                quantity < item.Quantity
                    ? "Partial corpse deposit submitted."
                    : "Corpse deposit submitted.");
        }

        private void DestroySelected(InventoryItem item)
        {
            if (!destroyConfirmation)
            {
                destroyConfirmation = true;
                localStatus = "Press Confirm Destroy to send the authoritative destruction request.";
                Rebuild();
                return;
            }

            Execute(
                controller.TryDestroy(item, out var error),
                error,
                "Destroy submitted.");
        }

        private void Execute(bool sent, string error, string success)
        {
            localStatus = sent ? success : error;
            if (sent)
            {
                ClearSelection();
            }

            Rebuild();
        }

        private void ExecuteCorpse(bool sent, string error, string success)
        {
            localStatus = sent ? success : error;
            Rebuild();
        }

        private void SelectItem(InventoryItem item)
        {
            selectedItemId = item.ItemInstanceId;
            splitMode = false;
            destroyConfirmation = false;
            splitQuantityText = "1";
            localStatus = "Drag this item to move it, or use the selected-item actions.";
            Rebuild();
        }

        private void ClearSelection()
        {
            selectedItemId = Guid.Empty;
            splitMode = false;
            destroyConfirmation = false;
            splitQuantityText = "1";
        }

        private void SetContext(InventoryContextKind next)
        {
            context = next;
            ClearSelection();
            if (next is InventoryContextKind.Bank or InventoryContextKind.RecoveryStorage)
            {
                controller.RefreshContext(next);
            }

            Rebuild();
        }

        private void RefreshCurrentContext()
        {
            if (context == InventoryContextKind.Corpse)
            {
                if (corpseController?.State.ActiveView != null)
                {
                    ExecuteCorpseRefresh();
                }

                return;
            }

            controller.RefreshContext(context);
        }

        private void ExecuteCorpseClose()
        {
            var error = "Corpse interaction state is unavailable.";
            if (worldInteractionController == null
                || !worldInteractionController.TryCloseActive(out error))
            {
                RejectDrop(error);
                return;
            }

            localStatus = "Corpse close submitted.";
        }

        private void ExecuteCorpseRefresh()
        {
            var error = "Corpse interaction state is unavailable.";
            if (corpseController == null
                || !corpseController.TryRefresh(out error))
            {
                RejectDrop(error);
                return;
            }

            localStatus = "Corpse refresh submitted.";
        }

        private void DrawStatus(Transform parent, InventoryClientState state)
        {
            if (state.Error != null)
            {
                CreateMessage(parent, state.Error.ToDisplayMessage(), TemporaryUiKit.DangerColor);
            }

            if (state.Status is InventoryClientStatus.Loading or InventoryClientStatus.Busy)
            {
                CreateMessage(parent, StatusText(state), TemporaryUiKit.WarningColor);
            }
        }

        private string ContextDetail(InventoryClientState state)
        {
            var parts = new List<string>();
            var access = StorageAccessLabel();
            if (!string.IsNullOrEmpty(access))
            {
                parts.Add(access);
            }

            if (context == InventoryContextKind.Bank && state.Bank != null)
            {
                parts.Add(UsedSlotsText(state.Bank) + " used");
            }

            return string.Join("    ", parts);
        }

        // Names the NPC whose open interaction offers the shown storage or trade.
        private string StorageAccessLabel()
        {
            var active = worldInteractionController?.State.ActiveInteraction;
            if (active == null
                || context is not (InventoryContextKind.Bank
                    or InventoryContextKind.RecoveryStorage
                    or InventoryContextKind.Vendor))
            {
                return string.Empty;
            }

            var kind = context switch
            {
                InventoryContextKind.Bank => RealtimeWorldActorCapabilityKind.Bank,
                InventoryContextKind.Vendor => RealtimeWorldActorCapabilityKind.Vendor,
                _ => RealtimeWorldActorCapabilityKind.RecoveryStorage
            };
            return active.Capabilities.Any(capability => capability.Kind == kind)
                ? "via " + active.DisplayName
                : string.Empty;
        }

        private static string UsedSlotsText(InventoryContainer container)
        {
            return container.Slots.Count(slot => slot.Item != null).ToString(CultureInfo.InvariantCulture)
                + " / " + container.Slots.Count.ToString(CultureInfo.InvariantCulture);
        }

        private static string ContextTitle(InventoryContextKind value)
        {
            return value switch
            {
                InventoryContextKind.Bank => "Character Bank",
                InventoryContextKind.RecoveryStorage => "Recovery Storage",
                InventoryContextKind.Corpse => "Corpse",
                InventoryContextKind.WorldLootPrepared => "World Loot (Prepared)",
                InventoryContextKind.Vendor => "Vendor",
                _ => "Storage"
            };
        }

        private static string StatusText(InventoryClientState state)
        {
            return state.Status switch
            {
                InventoryClientStatus.Loading => "Loading authoritative inventory...",
                InventoryClientStatus.Busy => "Waiting for the committed operation and refresh...",
                InventoryClientStatus.UpdateRequired => "Client update required.",
                InventoryClientStatus.Error => "Inventory refresh failed.",
                InventoryClientStatus.Ready => "Inventory ready.",
                _ => "Inventory has not loaded."
            };
        }

        private static string LocationLabel(InventoryClientState state, InventoryItemLocation location)
        {
            return location.Kind switch
            {
                InventoryItemLocationKind.Equipment => "Equipped in "
                    + InventorySlotPresentation.EquipmentSlotName(state.Catalog, location.EquipmentSlotId),
                InventoryItemLocationKind.RecoveryStorage => "Recovery Storage",
                _ => ContainerName(state, location.ContainerType) + " slot " + (location.SlotIndex + 1)
            };
        }

        private static string ContainerName(InventoryClientState state, string containerType)
        {
            return containerType switch
            {
                ItemContainerTypeIds.PermanentInventory => "Permanent Inventory",
                ItemContainerTypeIds.BagContents => state.FullSnapshot?.EquippedBag != null
                    ? state.Catalog.GetDisplayName(state.FullSnapshot.EquippedBag.Item.DefinitionId)
                    : "Bag",
                ItemContainerTypeIds.SecureContainer => InventorySlotPresentation.SecureContainerTitle(
                    state.Catalog,
                    state.FullSnapshot?.SecureContainer?.TierId),
                ItemContainerTypeIds.Bank => "Bank",
                _ => containerType
            };
        }

        private static string RecoverySourceLabel(string source)
        {
            return source switch
            {
                "death_recovered_insured" => "Recovered: consumed insurance",
                "death_recovered_protected" => "Recovered: protected on death",
                _ => "Recovered: " + source
            };
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            var eventSystem = new GameObject(
                "InventoryEventSystem",
                typeof(EventSystem),
                typeof(InputSystemUIInputModule));
            eventSystem.transform.SetAsFirstSibling();
        }
    }
}
