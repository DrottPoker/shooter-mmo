using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.Client.Quests;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Quests;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The quest windows: the quest dialog that a quest NPC opens, the quest log that L opens, and
    // the tracker at the right edge of the screen. The dialog shows what the NPC can do for the
    // player now, and for the chosen entry its text, objectives, and rewards with the action that
    // answers it; it closes with the interaction. They read the quest state, the inventory, and
    // the open interaction, send every action through the interaction or the quest controller,
    // and own no quest rule. A QuestNoticeFeed writes the notices of every committed quest change
    // into the game messages. They run after the inventory windows, so an Escape that a dialog
    // took does nothing else; the abandon confirmation takes its Escape before the inventory
    // windows.
    [DefaultExecutionOrder(InventoryWindows.ExecutionOrder + 1)]
    [DisallowMultipleComponent]
    public sealed class QuestWindows : MonoBehaviour
    {
        public const string DialogWindowName = "QuestDialogWindow";

        // Above the interaction panel and below the inventory, whose tooltips draw over it.
        public const int CanvasSortingOrder = 90;

        public const float DialogWidth = 460f;

        private UiTheme theme;
        private QuestClientController quests;
        private InventoryClientController inventory;
        private WorldInteractionClientController interaction;
        private GameMessageLog messages;
        private WorldMenuController worldMenu;
        private InventoryWindows inventoryWindows;
        private LocalPlayerInput localPlayerInput;
        private QuestNoticeFeed notices;
        private UiWindow dialog;
        private QuestLogWindow log;
        private QuestTracker tracker;
        private UiDialog confirmation;
        private bool placedBesideEquipment;
        private string npcActorId = string.Empty;
        private Guid dialogInteractionSessionId;
        private QuestDialogEntry selected;
        private string rewardChoice = string.Empty;

        public UiWindow Dialog
        {
            get { return dialog; }
        }

        public bool IsDialogOpen
        {
            get { return dialog != null && dialog.IsOpen; }
        }

        public UiWindow Log
        {
            get { return log?.Window; }
        }

        public bool IsLogOpen
        {
            get { return log != null && log.IsOpen; }
        }

        // The tracker's lines as it shows them, empty without active quests.
        public string TrackerText
        {
            get { return tracker != null && tracker.Panel.gameObject.activeSelf ? tracker.Text : string.Empty; }
        }

        public UiDialog Confirmation
        {
            get { return confirmation; }
        }

        // Called by the composition root before the component is activated. The inventory
        // windows tell where the Equipment window stands, which the dialog and the log keep clear.
        public void Initialize(
            UiTheme uiTheme,
            QuestClientController questController,
            InventoryClientController inventoryController,
            WorldInteractionClientController worldInteraction,
            GameMessageLog gameMessages,
            WorldMenuController menu = null,
            InventoryWindows inventoryWindowsBeside = null)
        {
            theme = uiTheme;
            quests = questController;
            inventory = inventoryController;
            interaction = worldInteraction;
            messages = gameMessages;
            worldMenu = menu;
            inventoryWindows = inventoryWindowsBeside;
        }

        public void SetLogOpen(bool open)
        {
            if (log == null)
            {
                return;
            }

            log.Window.SetOpen(open);
            if (open)
            {
                quests?.EnsureLoaded();
                RefreshLog();
            }
        }

        // Opens the dialog for the NPC of the open interaction.
        public void OpenDialog()
        {
            var active = interaction?.State.ActiveInteraction;
            if (dialog == null || active == null || !interaction.TryGetActiveActor(out var actor))
            {
                return;
            }

            npcActorId = actor.ActorDefinitionId;
            dialogInteractionSessionId = active.InteractionSessionId;
            selected = null;
            rewardChoice = string.Empty;

            // A message of an earlier action belongs to that action, not to this dialog.
            interaction.State.ClearMessage();
            dialog.SetTitle(actor.DisplayName);
            dialog.SetOpen(true);
            quests?.EnsureLoaded();
            Refresh();
        }

        public void CloseDialog()
        {
            selected = null;
            rewardChoice = string.Empty;
            if (dialog != null)
            {
                dialog.SetOpen(false);
            }
        }

        private void Start()
        {
            BuildUi();
            FindPlayerInput();
            if (quests != null)
            {
                // The quest log may have loaded before the windows, so the notices start from
                // it now.
                notices = new QuestNoticeFeed(quests.State, inventory?.State, Say);
                quests.State.Changed += OnQuestStateChanged;
                quests.IntentChanged += RefreshLog;
                quests.QuestDropped += OnQuestDropped;
            }

            if (inventory != null)
            {
                inventory.State.Changed += OnInventoryChanged;
            }

            if (interaction != null)
            {
                interaction.State.Changed += OnInteractionChanged;
                interaction.QuestCapabilityOpened += OnQuestCapabilityOpened;
            }
        }

        private void OnDestroy()
        {
            notices?.Dispose();
            if (quests != null)
            {
                quests.State.Changed -= OnQuestStateChanged;
                quests.IntentChanged -= RefreshLog;
                quests.QuestDropped -= OnQuestDropped;
            }

            if (inventory != null)
            {
                inventory.State.Changed -= OnInventoryChanged;
            }

            if (interaction != null)
            {
                interaction.State.Changed -= OnInteractionChanged;
                interaction.QuestCapabilityOpened -= OnQuestCapabilityOpened;
            }
        }

        private void Update()
        {
            if (dialog != null && IsEquipmentOpen != placedBesideEquipment)
            {
                PlaceWindows();
            }

            if (localPlayerInput == null)
            {
                FindPlayerInput();
                if (localPlayerInput == null)
                {
                    return;
                }
            }

            if (localPlayerInput.ToggleQuestLogPressedThisFrame && !IsTyping())
            {
                SetLogOpen(!IsLogOpen);
            }
            else if (localPlayerInput.CancelPressedThisFrame
                && (worldMenu == null || !worldMenu.CancelConsumedThisFrame)
                && IsLogOpen)
            {
                // The log closes with the other windows. A confirmation took its Escape earlier.
                SetLogOpen(false);
            }
        }

        // A key typed into a text field, such as the inventory search, is text.
        private static bool IsTyping()
        {
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            var selectedObject = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            return selectedObject != null
                && selectedObject.TryGetComponent<TMP_InputField>(out var field)
                && field.isFocused;
        }

        private void FindPlayerInput()
        {
            var localPlayer = FindAnyObjectByType<LocalPlayerController>();
            localPlayerInput = localPlayer != null
                ? localPlayer.PlayerInput
                : FindAnyObjectByType<LocalPlayerInput>();
        }

        private void Abandon(QuestLogActiveEntry entry)
        {
            if (entry == null || confirmation == null)
            {
                return;
            }

            var questId = entry.Quest.Id;
            confirmation.AskConfirmation(
                "Abandon Quest",
                "Abandon " + entry.Quest.Title + "? Its progress and its quest items are lost.",
                "Abandon",
                () =>
                {
                    if (!quests.TryAbandon(questId, out var error))
                    {
                        Say(new[] { error });
                    }
                });
        }

        private void RefreshLog()
        {
            if (log == null || quests == null)
            {
                return;
            }

            log.Refresh(
                quests.State,
                QuestCarriedItems.Counter(inventory?.State),
                quests.HasPendingIntent,
                quests.LastAbandonError);
        }

        private void RefreshTracker()
        {
            if (tracker != null && quests != null)
            {
                tracker.Refresh(quests.State, QuestCarriedItems.Counter(inventory?.State));
            }
        }

        private void OnQuestCapabilityOpened(RealtimeWorldActorCapability capability)
        {
            OpenDialog();
        }

        // The dialog belongs to one interaction and closes when it ends.
        private void OnInteractionChanged()
        {
            if (!IsDialogOpen)
            {
                return;
            }

            var active = interaction.State.ActiveInteraction;
            if (active == null || active.InteractionSessionId != dialogInteractionSessionId)
            {
                CloseDialog();
                return;
            }

            Refresh();
        }

        private void OnQuestStateChanged()
        {
            Refresh();
            RefreshLog();
            RefreshTracker();
        }

        private void OnQuestDropped(RealtimeQuestDropInCorpse drop)
        {
            Say(new[] { QuestNotices.ForDrop(drop, quests.State.Catalog, ItemName) });
        }

        // Carried items move collect objectives, and they can make a quest ready.
        private void OnInventoryChanged()
        {
            Refresh();
            RefreshLog();
            RefreshTracker();
        }

        private bool IsEquipmentOpen
        {
            get { return inventoryWindows != null && inventoryWindows.IsEquipmentOpen && inventoryWindows.Equipment != null; }
        }

        // The dialog stands at the left screen edge, or beside the Equipment window while that is
        // open, since the Equipment window stands there too and draws over this canvas; the log
        // stands beside the dialog.
        private void PlaceWindows()
        {
            placedBesideEquipment = IsEquipmentOpen;
            var left = theme.ScreenMargin
                + (placedBesideEquipment ? inventoryWindows.Equipment.Rect.sizeDelta.x + theme.WindowGap : 0f);
            dialog.Place(new Vector2(0f, 1f), new Vector2(left, -theme.WindowTop));
            log.Window.Place(new Vector2(0f, 1f), new Vector2(left + DialogWidth + theme.WindowGap, -theme.WindowTop));
        }

        private void Say(IReadOnlyList<string> notices)
        {
            if (messages == null)
            {
                return;
            }

            foreach (var notice in notices)
            {
                messages.Add(notice, Time.unscaledTime);
            }
        }

        private void Refresh()
        {
            if (!IsDialogOpen || quests == null || interaction == null)
            {
                return;
            }

            UiFactory.ClearChildren(dialog.Body);
            UiFactory.ClearChildren(dialog.Footer);
            var state = quests.State;
            if (!state.IsReady)
            {
                AddParagraph(QuestLogWindow.StatusText(state), theme.MutedTextColor);
                dialog.FitBody();
                return;
            }

            var carried = QuestCarriedItems.Counter(inventory?.State);
            var entries = QuestDialogEntries.For(
                npcActorId,
                state,
                carried,
                QuestCarriedItems.OwnedQuestGrantCounter(inventory?.State));
            if (selected != null)
            {
                selected = entries.FirstOrDefault(entry => IsSameEntry(entry, selected));
                if (selected == null)
                {
                    rewardChoice = string.Empty;
                }
            }

            if (selected == null)
            {
                BuildList(entries);
            }
            else
            {
                BuildDetail(selected, carried);
            }

            var interactionState = interaction.State;
            if (!string.IsNullOrEmpty(interactionState.LastErrorCode) && !string.IsNullOrWhiteSpace(interactionState.LastMessage))
            {
                AddParagraph(interactionState.LastMessage, theme.DangerColor);
            }

            dialog.FitBody();
        }

        private void BuildList(IReadOnlyList<QuestDialogEntry> entries)
        {
            if (entries.Count == 0)
            {
                AddParagraph("There is nothing more to do here for now.", theme.MutedTextColor);
                return;
            }

            foreach (var entry in entries)
            {
                var chosen = entry;
                UiFactory.Button(
                    theme,
                    dialog.Body,
                    MarkerOf(entry.Kind) + "  " + LabelOf(entry),
                    () => Select(chosen),
                    entry.Kind == QuestDialogEntryKind.InProgress ? UiButtonTone.Normal : UiButtonTone.Primary);
            }
        }

        private void BuildDetail(QuestDialogEntry entry, Func<string, int> carried)
        {
            var quest = entry.Quest;
            UiFactory.SectionHeading(theme, dialog.Body, quest.Title, null, out _);
            AddParagraph(TextOf(entry, carried), theme.TextColor);

            var objectives = entry.Kind == QuestDialogEntryKind.Offer
                ? QuestProgress.OfferedObjectives(quest, carried)
                : QuestProgress.CurrentObjectives(quest, entry.Active, carried);
            if (objectives.Count > 0)
            {
                UiFactory.SectionHeading(theme, dialog.Body, "Objectives", null, out _);
                foreach (var line in objectives)
                {
                    AddParagraph(line.Text, line.IsComplete ? theme.MutedTextColor : theme.TextColor);
                }
            }

            if (entry.Kind is QuestDialogEntryKind.Offer or QuestDialogEntryKind.TurnIn or QuestDialogEntryKind.InProgress)
            {
                BuildRewards(quest, entry.Kind == QuestDialogEntryKind.TurnIn);
            }

            var idle = interaction.State.PendingOperationId == Guid.Empty;
            UiFactory.Button(theme, dialog.Footer, "Back", Back);
            switch (entry.Kind)
            {
                case QuestDialogEntryKind.Offer:
                    AddAction("Accept", idle, () => Send(RealtimeCapabilityActionKind.AcceptQuest, string.Empty, string.Empty));
                    break;
                case QuestDialogEntryKind.TurnIn:
                    var needsChoice = quest.RewardChoiceItems != null && quest.RewardChoiceItems.Length > 0;
                    var rewardsFit = QuestRewardFit.Fits(
                        quest,
                        rewardChoice,
                        inventory?.State.ConfirmedSnapshot,
                        inventory?.State.Catalog,
                        out var noRoom);
                    if (!rewardsFit)
                    {
                        AddParagraph(noRoom, theme.DangerColor);
                    }

                    AddAction(
                        "Complete",
                        idle && rewardsFit && (!needsChoice || !string.IsNullOrEmpty(rewardChoice)),
                        () => Send(RealtimeCapabilityActionKind.TurnInQuest, string.Empty, needsChoice ? rewardChoice : string.Empty));
                    break;
                case QuestDialogEntryKind.TalkTo:
                    AddAction("Continue", idle, () => Send(RealtimeCapabilityActionKind.TalkToQuestNpc, entry.Objective.Id, string.Empty));
                    break;
                case QuestDialogEntryKind.Deliver:
                    AddAction(
                        "Hand over",
                        idle && QuestDialogEntries.CanHandOver(entry, carried),
                        () => Send(RealtimeCapabilityActionKind.DeliverQuestItems, entry.Objective.Id, string.Empty));
                    break;
                case QuestDialogEntryKind.LostStartItems:
                    AddAction("Ask again", idle, () => Send(RealtimeCapabilityActionKind.RegrantQuestItems, string.Empty, string.Empty));
                    break;
            }
        }

        private void BuildRewards(QuestDefinition quest, bool choosing)
        {
            var items = quest.RewardItems ?? Array.Empty<QuestItemQuantityDefinition>();
            var choices = quest.RewardChoiceItems ?? Array.Empty<QuestItemQuantityDefinition>();
            if (quest.RewardGold <= 0 && items.Length == 0 && choices.Length == 0)
            {
                return;
            }

            UiFactory.SectionHeading(theme, dialog.Body, "Rewards", null, out _);
            if (quest.RewardGold > 0)
            {
                AddParagraph(quest.RewardGold + " Gold", theme.GoldTextColor);
            }

            foreach (var item in items)
            {
                AddParagraph(ItemText(item), theme.TextColor);
            }

            if (choices.Length == 0)
            {
                return;
            }

            AddParagraph("Choose one of:", theme.MutedTextColor);
            foreach (var choice in choices)
            {
                var definitionId = choice.DefinitionId;
                if (!choosing)
                {
                    AddParagraph(ItemText(choice), theme.TextColor);
                    continue;
                }

                var chosen = string.Equals(rewardChoice, definitionId, StringComparison.Ordinal);
                UiFactory.Button(
                    theme,
                    dialog.Body,
                    (chosen ? "> " : string.Empty) + ItemText(choice),
                    () =>
                    {
                        rewardChoice = definitionId;
                        Refresh();
                    },
                    chosen ? UiButtonTone.Primary : UiButtonTone.Normal);
            }
        }

        private void Select(QuestDialogEntry entry)
        {
            selected = entry;
            rewardChoice = string.Empty;
            interaction.State.ClearMessage();
            Refresh();
        }

        private void Back()
        {
            selected = null;
            rewardChoice = string.Empty;
            interaction.State.ClearMessage();
            Refresh();
        }

        private void Send(RealtimeCapabilityActionKind action, string objectiveId, string choice)
        {
            if (selected == null)
            {
                return;
            }

            if (!interaction.TryExecuteQuestAction(action, selected.Quest.Id, objectiveId, choice, out var error))
            {
                interaction.State.SetLocalError("quest_action_unavailable", error);
            }
        }

        private void AddAction(string label, bool enabled, Action onClick)
        {
            var button = UiFactory.Button(theme, dialog.Footer, label, onClick, UiButtonTone.Primary);
            button.interactable = enabled;
        }

        private void AddParagraph(string text, Color color)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var paragraph = UiFactory.BodyText(theme, dialog.Body, "Text", theme.BodySize, color, TextAlignmentOptions.TopLeft, text);
            paragraph.textWrappingMode = TextWrappingModes.Normal;
        }

        private string TextOf(QuestDialogEntry entry, Func<string, int> carried)
        {
            switch (entry.Kind)
            {
                case QuestDialogEntryKind.Offer:
                    return entry.Quest.OfferText;
                case QuestDialogEntryKind.TurnIn:
                    return entry.Quest.CompletionText;
                case QuestDialogEntryKind.TalkTo:
                    return string.IsNullOrWhiteSpace(entry.Objective.Line) ? entry.Quest.ProgressText : entry.Objective.Line;
                case QuestDialogEntryKind.Deliver:
                    return "Hand over " + ItemText(new QuestItemQuantityDefinition
                    {
                        DefinitionId = entry.Objective.ItemId,
                        Quantity = QuestDialogEntries.RemainingDelivery(entry)
                    }) + ". You carry " + carried(entry.Objective.ItemId) + ".";
                case QuestDialogEntryKind.LostStartItems:
                    return "You no longer have what you were given for this quest: "
                        + string.Join(", ", entry.Quest.StartItems.Select(ItemText))
                        + ". Ask for it again.";
                default:
                    return entry.Quest.ProgressText;
            }
        }

        private string LabelOf(QuestDialogEntry entry)
        {
            switch (entry.Kind)
            {
                case QuestDialogEntryKind.TurnIn:
                    return entry.Quest.Title + " (Complete)";
                case QuestDialogEntryKind.TalkTo:
                case QuestDialogEntryKind.Deliver:
                    return entry.Quest.Title + ": " + entry.Objective.Text;
                case QuestDialogEntryKind.LostStartItems:
                    return entry.Quest.Title + ": lost items";
                case QuestDialogEntryKind.InProgress:
                    return entry.Quest.Title + " (in progress)";
                default:
                    return entry.Quest.Title;
            }
        }

        // The marker that the quest windows and the NPC markers share: ! for a quest to accept,
        // ? for something to finish here.
        private static string MarkerOf(QuestDialogEntryKind kind)
        {
            return kind is QuestDialogEntryKind.Offer or QuestDialogEntryKind.LostStartItems ? "!" : "?";
        }

        private string ItemText(QuestItemQuantityDefinition item)
        {
            var name = ItemName(item.DefinitionId);
            return item.Quantity > 1 ? item.Quantity + " x " + name : name;
        }

        private string ItemName(string definitionId)
        {
            var catalog = inventory?.State.Catalog;
            return catalog != null ? catalog.GetDisplayName(definitionId) : definitionId;
        }

        private static bool IsSameEntry(QuestDialogEntry entry, QuestDialogEntry other)
        {
            return entry.Kind == other.Kind
                && string.Equals(entry.Quest.Id, other.Quest.Id, StringComparison.Ordinal)
                && string.Equals(entry.Objective?.Id, other.Objective?.Id, StringComparison.Ordinal);
        }

        private void BuildUi()
        {
            if (theme == null)
            {
                theme = UiTheme.Load(out _);
            }

            TemporaryUiKit.EnsureEventSystem();
            var canvasObject = new GameObject(
                "QuestCanvas",
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

            // The root only groups the windows and draws nothing, so a click beside them still
            // reaches the world.
            var root = UiFactory.Create("QuestRoot", canvasObject.transform);
            UiFactory.Fill(root);
            tracker = new QuestTracker(theme, root);
            dialog = UiWindow.Create(theme, root, DialogWindowName, null, "Quests", DialogWidth);
            dialog.SetMaximumBodyHeight(1080f - theme.WindowTop - theme.ScreenMargin - 220f);
            dialog.CloseRequested += CloseDialog;
            dialog.SetOpen(false);
            // The log stands beside the dialog, so both show at once.
            log = new QuestLogWindow(theme, root, Abandon, RefreshLog);
            log.Window.CloseRequested += () => SetLogOpen(false);
            PlaceWindows();
            confirmation = UiDialog.Create(theme, root);
            gameObject.AddComponent<UiDialogEscape>().Initialize(confirmation, worldMenu, () => localPlayerInput);
            RefreshTracker();
        }
    }
}
