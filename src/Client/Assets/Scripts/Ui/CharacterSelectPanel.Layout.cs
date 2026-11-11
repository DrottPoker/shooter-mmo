using System.Collections.Generic;
using Mmorpg.Client.Core;
using Mmorpg.WorldData.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The widgets of CharacterSelect: a top bar with the account, Refresh, and Log Out; a card
    // with the character list and New Character; a card that shows the chosen character with the
    // shards and Enter World, or the creation of a character; and a status line below. A list is
    // built again only when its rows change, so a double click lands on the row it started on.
    public sealed partial class CharacterSelectPanel
    {
        private readonly List<Button> characterRows = new List<Button>();
        private readonly List<string> characterRowIds = new List<string>();
        private readonly List<Button> shardRows = new List<Button>();
        private readonly List<string> shardRowIds = new List<string>();
        private readonly List<Button> bodyButtons = new List<Button>();
        private readonly List<TextMeshProUGUI> lookValues = new List<TextMeshProUGUI>();

        private TemporaryEntryScreen screen;
        private CanvasGroup controls;
        private TextMeshProUGUI accountText;
        private RectTransform characterList;
        private RectTransform enterPage;
        private RectTransform createPage;
        private TextMeshProUGUI characterTitle;
        private TextMeshProUGUI characterDetail;
        private RectTransform shardList;
        private TextMeshProUGUI enterWorldBlocker;
        private Button enterWorldButton;
        private TextMeshProUGUI enterWorldLabel;
        private TMP_InputField nameField;
        private RectTransform bodyRow;
        private RectTransform lookList;
        private TextMeshProUGUI lookNote;
        private Button cancelCreationButton;
        private TextMeshProUGUI statusText;
        private int shownCharactersVersion = -1;
        private int shownShardsVersion = -1;
        private CharacterCreationLook shownLook;
        private int shownBodyIndex = -1;
        private bool wasCreating;
        private bool focusNameWhenIdle;

        public string StatusText
        {
            get { return statusText != null ? statusText.text : string.Empty; }
        }

        private string CharacterNameInput
        {
            get { return nameField != null ? nameField.text.Trim() : string.Empty; }
        }

        private void ClearCharacterNameInput()
        {
            if (nameField != null)
            {
                nameField.text = string.Empty;
            }
        }

        private void BuildUi()
        {
            screen = TemporaryEntryScreen.Create(transform, ScreenName);
            var controlsRect = TemporaryUiKit.CreateUiObject("Controls", screen.Root).GetComponent<RectTransform>();
            TemporaryUiKit.Stretch(controlsRect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            controls = controlsRect.gameObject.AddComponent<CanvasGroup>();
            BuildTopBar(controlsRect);
            BuildCharacterCard(controlsRect);
            BuildWorldCard(controlsRect);

            statusText = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Status", screen.Root),
                string.Empty,
                15,
                FontStyles.Normal,
                TextAlignmentOptions.Center,
                TemporaryUiKit.TextSecondary);
            statusText.textWrappingMode = TextWrappingModes.Normal;
            TemporaryUiKit.Stretch(
                statusText.rectTransform,
                new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(-500f, 28f),
                new Vector2(500f, 84f));
        }

        private void BuildTopBar(RectTransform parent)
        {
            var bar = TemporaryUiKit.CreateUiObject("TopBar", parent).GetComponent<RectTransform>();
            TemporaryUiKit.Stretch(bar, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -60f), Vector2.zero);
            bar.gameObject.AddComponent<Image>().color = TemporaryUiKit.HeaderColor;
            var title = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Title", bar),
                TemporaryEntryScreen.GameTitle + "   <size=15><color=" + TemporaryUiKit.ColorTag(TemporaryUiKit.TextMuted)
                    + ">" + texts.Get("ui/characters/title") + "</color></size>",
                22,
                FontStyles.Bold,
                TextAlignmentOptions.Left,
                TemporaryUiKit.TextPrimary);
            TemporaryUiKit.Stretch(title.rectTransform, Vector2.zero, Vector2.one, new Vector2(24f, 0f), new Vector2(-24f, 0f));

            var actions = TemporaryUiKit.CreateRow(bar, 34f, 10f);
            actions.anchorMin = new Vector2(1f, 0.5f);
            actions.anchorMax = new Vector2(1f, 0.5f);
            actions.pivot = new Vector2(1f, 0.5f);
            actions.anchoredPosition = new Vector2(-20f, 0f);
            actions.sizeDelta = new Vector2(560f, 34f);
            actions.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleRight;
            accountText = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Account", actions),
                string.Empty,
                14,
                FontStyles.Normal,
                TextAlignmentOptions.Right,
                TemporaryUiKit.TextSecondary);
            accountText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            TemporaryUiKit.CreateButton(actions, texts.Get("ui/characters/refresh"), Refresh, true, TemporaryButtonTone.Normal, 110f);
            TemporaryUiKit.CreateButton(actions, texts.Get("ui/characters/log-out"), LogOut, true, TemporaryButtonTone.Quiet, 110f);
        }

        private void BuildCharacterCard(RectTransform parent)
        {
            var card = TemporaryEntryScreen.AddCard(parent, "CharactersCard", new Vector2(-320f, -4f), new Vector2(340f, 660f), false);
            TemporaryMenuWindow.AddSection(card, texts.Get("ui/characters/heading-characters"));
            characterList = TemporaryEntryScreen.AddScrollList(card, "CharacterList");
            var row = TemporaryEntryScreen.AddButtonRow(card, 40f);
            TemporaryUiKit.CreateButton(row, texts.Get("ui/characters/new-character"), () => OpenCreation(), true, TemporaryButtonTone.Normal);
        }

        private void BuildWorldCard(RectTransform parent)
        {
            var card = TemporaryEntryScreen.AddCard(parent, "WorldCard", new Vector2(180f, -4f), new Vector2(620f, 660f), false);

            enterPage = TemporaryEntryScreen.AddPage(card, "EnterPage");
            characterTitle = TemporaryEntryScreen.AddHeading(enterPage, string.Empty, 28);
            characterDetail = TemporaryEntryScreen.AddText(enterPage, string.Empty, 14, TemporaryUiKit.TextSecondary);
            TemporaryEntryScreen.AddSpace(enterPage, 8f, false);
            TemporaryMenuWindow.AddSection(enterPage, texts.Get("ui/characters/heading-shard"));
            shardList = TemporaryEntryScreen.AddScrollList(enterPage, "ShardList");
            enterWorldBlocker = TemporaryEntryScreen.AddText(enterPage, string.Empty, 13, TemporaryUiKit.WarningColor);
            enterWorldButton = TemporaryEntryScreen.AddMainButton(enterPage, texts.Get("ui/characters/enter-world"), BeginJoin);
            enterWorldLabel = enterWorldButton.GetComponentInChildren<TextMeshProUGUI>();
            TemporaryEntryScreen.AddText(
                enterPage,
                texts.Get("ui/characters/enter-hint"),
                12,
                TemporaryUiKit.TextMuted);

            createPage = TemporaryEntryScreen.AddPage(card, "CreatePage");
            TemporaryEntryScreen.AddHeading(createPage, texts.Get("ui/characters/new-character"), 24);
            nameField = screen.AddField(
                createPage,
                texts.Get("ui/characters/name"),
                string.Empty,
                texts.Get("ui/characters/name-placeholder"),
                TMP_InputField.ContentType.Standard,
                AccountEntryPresentation.CharacterNameLimit,
                texts.Get("ui/characters/name-hint"));
            TemporaryMenuWindow.AddSection(createPage, texts.Get("ui/characters/heading-body"));
            bodyRow = TemporaryEntryScreen.AddButtonRow(createPage, 34f);
            TemporaryMenuWindow.AddSection(createPage, texts.Get("ui/characters/heading-look"));
            lookNote = TemporaryEntryScreen.AddText(createPage, string.Empty, 13, TemporaryUiKit.TextMuted);
            lookList = TemporaryEntryScreen.AddScrollList(createPage, "LookList");
            var buttons = TemporaryEntryScreen.AddButtonRow(createPage, 44f);
            cancelCreationButton = TemporaryUiKit.CreateButton(
                buttons,
                texts.Get("ui/common/cancel"),
                () => state.SetCreating(false),
                true,
                TemporaryButtonTone.Quiet,
                140f);
            var create = TemporaryUiKit.CreateButton(buttons, texts.Get("ui/characters/create-character"), CreateCharacter, true, TemporaryButtonTone.Active);
            create.GetComponentInChildren<TextMeshProUGUI>().fontSize = 17;
        }

        private void OpenCreation()
        {
            state.SetCreating(true);
            TemporaryEntryScreen.Focus(nameField);
        }

        private void RefreshView()
        {
            if (screen == null)
            {
                return;
            }

            accountText.text = ClientSession.Auth != null
                ? texts.Format("ui/characters/signed-in", MessageArgument.Text("username", ClientSession.Auth.username))
                : string.Empty;
            RefreshCharacterRows();
            RefreshShardRows();

            var creating = state.IsCreating;
            enterPage.gameObject.SetActive(!creating);
            createPage.gameObject.SetActive(creating);
            if (creating)
            {
                RefreshCreation();
                focusNameWhenIdle |= !wasCreating;
            }
            else
            {
                RefreshEnterPage();
            }

            wasCreating = creating;
            RefreshBusy();
        }

        private void RefreshBusy()
        {
            if (controls == null)
            {
                return;
            }

            var busy = operationState.IsBusy;
            controls.interactable = !busy;
            enterWorldButton.interactable = state.CanEnterWorld;
            enterWorldLabel.text = texts.Get(operationState.Current == ClientOperation.JoinShard
                ? "ui/characters/entering-world"
                : "ui/characters/enter-world");
            cancelCreationButton.gameObject.SetActive(state.CanCancelCreation);

            // A field takes the caret only while the controls are enabled.
            if (!busy && focusNameWhenIdle && state.IsCreating)
            {
                focusNameWhenIdle = false;
                TemporaryEntryScreen.Focus(nameField);
            }
        }

        private void RefreshCharacterRows()
        {
            var characters = state.Characters;
            if (state.CharactersVersion != shownCharactersVersion)
            {
                shownCharactersVersion = state.CharactersVersion;
                ClearRows(characterList, characterRows, characterRowIds);
                foreach (var character in characters)
                {
                    var id = character.id;
                    var row = TemporaryEntryScreen.AddListRow(
                        characterList,
                        character.name,
                        AccountEntryPresentation.CharacterDetail(character, texts),
                        null,
                        TemporaryUiKit.TextSecondary,
                        () => state.SelectCharacter(id));
                    row.gameObject.AddComponent<TemporaryDoubleClick>().DoubleClicked += () =>
                    {
                        if (state.SelectedCharacter != null && state.SelectedCharacter.id == id)
                        {
                            BeginJoin();
                        }
                    };
                    characterRows.Add(row);
                    characterRowIds.Add(id);
                }

                if (characters.Count == 0)
                {
                    TemporaryEntryScreen.AddText(
                        characterList,
                        texts.Get(state.CharactersLoaded ? "ui/characters/no-characters" : "ui/characters/loading"),
                        14,
                        TemporaryUiKit.TextMuted);
                }
            }

            var chosen = !state.IsCreating && state.SelectedCharacter != null ? state.SelectedCharacter.id : null;
            ToneRows(characterRows, characterRowIds, chosen);
        }

        private void RefreshShardRows()
        {
            var shards = state.Shards;
            if (state.ShardsVersion != shownShardsVersion)
            {
                shownShardsVersion = state.ShardsVersion;
                ClearRows(shardList, shardRows, shardRowIds);
                foreach (var shard in shards)
                {
                    var id = shard.id;
                    var row = TemporaryEntryScreen.AddListRow(
                        shardList,
                        shard.displayName,
                        AccountEntryPresentation.ShardDetail(shard),
                        AccountEntryPresentation.ShardAvailability(shard, texts),
                        shard.isOnline ? TemporaryUiKit.SuccessColor : TemporaryUiKit.TextMuted,
                        () => state.SelectShard(id));
                    row.interactable = shard.isOnline;
                    row.gameObject.AddComponent<TemporaryDoubleClick>().DoubleClicked += () =>
                    {
                        if (state.SelectedShard != null && state.SelectedShard.id == id)
                        {
                            BeginJoin();
                        }
                    };
                    shardRows.Add(row);
                    shardRowIds.Add(id);
                }
            }

            ToneRows(shardRows, shardRowIds, state.SelectedShard != null ? state.SelectedShard.id : null);
        }

        private void RefreshEnterPage()
        {
            var character = state.SelectedCharacter;
            characterTitle.text = character != null
                ? character.name
                : texts.Get(state.CharactersLoaded ? "ui/characters/no-character-chosen" : "ui/characters/loading");
            characterDetail.text = AccountEntryPresentation.CharacterDetail(character, texts);
            var blocker = state.CharactersLoaded ? AccountEntryPresentation.EnterWorldBlocker(state, texts) : null;
            enterWorldBlocker.text = blocker ?? string.Empty;
            enterWorldBlocker.gameObject.SetActive(blocker != null);
        }

        // The body buttons and look rows are built again for another catalog or body model; a
        // stepped option only changes its value.
        private void RefreshCreation()
        {
            var look = state.CreationLook;
            var available = look != null && look.IsAvailable;
            lookNote.text = available
                ? string.Empty
                : texts.Get("ui/characters/look-not-loaded");
            lookNote.gameObject.SetActive(!available);
            if (look != shownLook || (available && look.BodyModelIndex != shownBodyIndex))
            {
                shownLook = look;
                shownBodyIndex = available ? look.BodyModelIndex : -1;
                BuildCreationRows(look, available);
            }

            if (!available)
            {
                return;
            }

            for (var index = 0; index < bodyButtons.Count; index++)
            {
                TemporaryUiKit.SetButtonTone(
                    bodyButtons[index],
                    index == look.BodyModelIndex ? TemporaryButtonTone.Active : TemporaryButtonTone.Normal);
            }

            for (var offer = 0; offer < lookValues.Count; offer++)
            {
                lookValues[offer].text = look.OptionLabel(offer, texts);
            }
        }

        private void BuildCreationRows(CharacterCreationLook look, bool available)
        {
            foreach (var button in bodyButtons)
            {
                Destroy(button.gameObject);
            }

            bodyButtons.Clear();
            lookValues.Clear();
            for (var index = lookList.childCount - 1; index >= 0; index--)
            {
                Destroy(lookList.GetChild(index).gameObject);
            }

            if (!available)
            {
                return;
            }

            for (var index = 0; index < look.BodyModelCount; index++)
            {
                var chosen = index;
                bodyButtons.Add(TemporaryUiKit.CreateButton(
                    bodyRow,
                    AccountEntryPresentation.Words(look.BodyModelAt(index)),
                    () =>
                    {
                        look.SelectBodyModel(chosen);
                        RefreshCreation();
                    },
                    true,
                    TemporaryButtonTone.Normal));
            }

            for (var offer = 0; offer < look.OfferCount; offer++)
            {
                var stepped = offer;
                lookValues.Add(TemporaryMenuWindow.AddStepper(
                    lookList,
                    AccountEntryPresentation.OfferLabel(look.OfferKind(offer), look.OfferSlot(offer)),
                    step =>
                    {
                        look.StepOption(stepped, step);
                        RefreshCreation();
                    }));
            }
        }

        private void SetStatus(string text, Color color)
        {
            if (statusText == null)
            {
                return;
            }

            statusText.text = text ?? string.Empty;
            statusText.color = color;
        }

        private static void ClearRows(RectTransform list, List<Button> rows, List<string> ids)
        {
            rows.Clear();
            ids.Clear();
            for (var index = list.childCount - 1; index >= 0; index--)
            {
                Destroy(list.GetChild(index).gameObject);
            }
        }

        private static void ToneRows(List<Button> rows, List<string> ids, string chosenId)
        {
            for (var index = 0; index < rows.Count; index++)
            {
                TemporaryUiKit.SetButtonTone(
                    rows[index],
                    ids[index] == chosenId ? TemporaryButtonTone.Active : TemporaryButtonTone.Normal);
            }
        }
    }
}
