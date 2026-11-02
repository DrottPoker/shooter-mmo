using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mmorpg.Client.Items;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The storage window beside the Inventory window: the Bank, Recovery Storage, an opened
    // corpse, or a vendor, with a tab for each one the open NPC offers. Its sections are built
    // again only when their shape changes, such as another corpse or a new Recovery delivery;
    // otherwise the slots are bound again in place.
    internal sealed partial class StorageWindow
    {
        private readonly InventoryWindows owner;
        private readonly UiTheme theme;
        private readonly RectTransform tabs;
        private readonly RectTransform content;
        private readonly TextMeshProUGUI message;
        private readonly List<SectionView> sections = new List<SectionView>();
        private readonly Dictionary<InventoryContextKind, Button> tabButtons = new Dictionary<InventoryContextKind, Button>();
        private string shapeKey = string.Empty;
        private string tabsKey = string.Empty;
        private string footerKey = string.Empty;
        private Button lootAllButton;

        public StorageWindow(InventoryWindows owner, RectTransform parent)
        {
            this.owner = owner;
            theme = owner.Theme;
            Window = UiWindow.Create(
                theme,
                parent,
                InventoryWindows.StorageWindowName,
                theme.StorageIcon,
                "Storage",
                ItemSlotGrid.Width(theme, CharacterInventoryWindow.Columns) + 2f * theme.WindowPadding);
            tabs = UiFactory.Row(Window.Body, "Tabs", theme.ButtonHeight, 6f).GetComponent<RectTransform>();
            message = UiFactory.BodyText(theme, Window.Body, "Message", theme.BodySize, theme.TextColor, TextAlignmentOptions.TopLeft);
            message.textWrappingMode = TextWrappingModes.Normal;
            content = UiFactory.Column(Window.Body, "Content", theme.SectionSpacing).GetComponent<RectTransform>();
        }

        public UiWindow Window { get; }

        // The slot grids of the shown sections, in order.
        public IEnumerable<ItemSlotGrid> Grids
        {
            get { return sections.Select(section => section.Grid).Where(grid => grid != null); }
        }

        public void Refresh()
        {
            var context = owner.StorageContext;
            RefreshTabs(context);
            var state = owner.State;
            if (state?.Catalog == null)
            {
                ShowMessage(state?.Error?.ToDisplayMessage() ?? "Inventory catalog is unavailable.", true);
                return;
            }

            switch (context)
            {
                case InventoryContextKind.Bank:
                    RefreshBank(state);
                    break;
                case InventoryContextKind.RecoveryStorage:
                    RefreshRecovery(state);
                    break;
                case InventoryContextKind.Corpse:
                    RefreshCorpse(state);
                    break;
                case InventoryContextKind.Vendor:
                    RefreshVendor(state);
                    break;
                default:
                    Window.SetTitle("Storage");
                    Window.SetDetail(string.Empty);
                    SetFooter(string.Empty, null);
                    ShowMessage("Talk to an NPC who keeps storage or trades, or open a corpse.", true);
                    break;
            }

            Window.FitBody();
        }

        private void RefreshTabs(InventoryContextKind current)
        {
            var contexts = owner.StorageContexts();
            var key = string.Join(",", contexts);
            if (!string.Equals(key, tabsKey, StringComparison.Ordinal))
            {
                tabsKey = key;
                UiFactory.ClearChildren(tabs);
                tabButtons.Clear();
                foreach (var context in contexts)
                {
                    var tabContext = context;
                    tabButtons[context] = UiFactory.Button(
                        theme,
                        tabs,
                        TabLabel(context),
                        () => owner.ShowStorageContext(tabContext));
                }
            }

            tabs.gameObject.SetActive(contexts.Count > 1);
            foreach (var pair in tabButtons)
            {
                pair.Value.targetGraphic.color = pair.Key == current
                    ? new Color(1f, 0.92f, 0.72f, 1f)
                    : new Color(0.75f, 0.78f, 0.78f, 1f);
            }
        }

        private void RefreshBank(InventoryClientState state)
        {
            var ownerName = owner.StorageOwnerName();
            Window.SetTitle(ownerName.Length > 0 ? ownerName : "Bank");
            SetFooter(string.Empty, null);
            var bank = state.Bank;
            if (bank == null)
            {
                ShowMessage(state.Error?.ToDisplayMessage() ?? CharacterInventoryWindow.StatusText(state), true);
                Window.SetDetail(string.Empty);
                return;
            }

            ShowMessage(state.Error?.ToDisplayMessage(), false);
            Window.SetDetail(CharacterInventoryWindow.UsedSlotsText(bank));
            // The window's title already names the Bank, so its grid has no heading.
            EnsureSections("bank:" + bank.ContainerId + ":" + bank.Slots.Count, () =>
                AddSection(null, null, true));
            sections[0].Grid.Bind(bank.Slots.Select(slot => owner.ContainerSlot(bank, slot, "bank")).ToArray());
        }

        private void RefreshRecovery(InventoryClientState state)
        {
            Window.SetTitle("Recovery Storage");
            Window.SetDetail(string.Empty);
            SetFooter(string.Empty, null);
            var recovery = state.RecoveryStorage;
            if (recovery == null)
            {
                ShowMessage("Recovery Storage is unavailable.", true);
                return;
            }

            if (recovery.Deliveries.Count == 0)
            {
                ShowMessage("Recovery Storage has no deliveries.", true);
                return;
            }

            ShowMessage("Drag any item of a delivery to your inventory or the Bank, or right-click it, to claim the whole delivery.", false);
            var key = "recovery:" + string.Join(",", recovery.Deliveries.Select(delivery =>
                delivery.DeliveryId + "/" + delivery.Items.Count.ToString(CultureInfo.InvariantCulture)));
            EnsureSections(key, () =>
            {
                foreach (var delivery in recovery.Deliveries)
                {
                    AddSection(InventorySlotPresentation.RecoverySourceLabel(delivery.SourceKind), null, true);
                }
            });
            for (var index = 0; index < recovery.Deliveries.Count; index++)
            {
                var delivery = recovery.Deliveries[index];
                sections[index].Detail.text = delivery.Items.Count.ToString(CultureInfo.InvariantCulture)
                    + (delivery.Items.Count == 1 ? " item" : " items");
                sections[index].Grid.Bind(delivery.Items
                    .Select(item => owner.RecoverySlot(delivery, item))
                    .ToArray());
            }
        }

        private void RefreshCorpse(InventoryClientState state)
        {
            var corpseState = owner.CorpseState;
            if (corpseState == null)
            {
                Window.SetTitle("Corpse");
                ShowMessage("Corpse interaction state is unavailable.", true);
                return;
            }

            var view = corpseState.ShownView;
            if (corpseState.ActiveView == null || view == null)
            {
                Window.SetTitle("Corpse");
                Window.SetDetail(string.Empty);
                SetFooter(string.Empty, null);
                ShowMessage(corpseState.Error?.ToDisplayMessage() ?? "Click a corpse in the world to walk to it and open it.", true);
                return;
            }

            Window.SetTitle(string.IsNullOrWhiteSpace(view.SourceDisplayName) ? "Corpse" : view.SourceDisplayName);
            Window.SetDetail("Expires " + DateTimeOffset.FromUnixTimeMilliseconds(view.ExpiresAtUnixMilliseconds)
                .ToLocalTime()
                .ToString("HH:mm", CultureInfo.InvariantCulture));
            var notice = corpseState.Error != null
                ? corpseState.Error.ToDisplayMessage()
                : corpseState.Status == CorpseClientStatus.Busy
                    ? "Waiting for the corpse..."
                    : corpseState.Notice;
            ShowMessage(notice, false);
            var key = "corpse:" + view.CorpseId + ":" + string.Join(",", view.Sections.Select(section =>
                section.SectionKind + "/" + section.Slots.Count.ToString(CultureInfo.InvariantCulture)));
            EnsureSections(key, () =>
            {
                foreach (var section in view.Sections)
                {
                    AddSection(InventorySlotPresentation.CorpseSectionTitle(section.SectionKind), null, true);
                }
            });
            for (var index = 0; index < view.Sections.Count; index++)
            {
                var section = view.Sections[index];
                sections[index].Root.gameObject.SetActive(section.Slots.Count > 0);
                sections[index].Detail.text = section.Slots.Count(slot => slot.Item != null).ToString(CultureInfo.InvariantCulture)
                    + " / " + section.Slots.Count.ToString(CultureInfo.InvariantCulture);
                sections[index].Grid.Bind(section.Slots.Select(slot => owner.CorpseSlot(view, section, slot)).ToArray());
            }

            var canLoot = corpseState.CanShowOperations
                && InventoryDropPlanner.CanMoveItem(state, null)
                && view.Sections.Any(section => section.Slots.Any(slot => slot.Item != null));
            SetFooter("corpse", footer =>
            {
                UiFactory.Button(theme, footer, "Close", () => owner.Report(owner.Interactions.CloseCorpse()));
                lootAllButton = UiFactory.Button(theme, footer, "Loot All", () => owner.Report(owner.Interactions.LootAll()), UiButtonTone.Primary);
            });
            if (lootAllButton != null)
            {
                lootAllButton.interactable = canLoot;
            }
        }

        private void ShowMessage(string text, bool only)
        {
            message.text = text ?? string.Empty;
            message.gameObject.SetActive(message.text.Length > 0);
            if (only)
            {
                EnsureSections(string.Empty, () => { });
            }
        }

        // Builds the sections again when their shape changed.
        private void EnsureSections(string key, Action build)
        {
            if (string.Equals(key, shapeKey, StringComparison.Ordinal))
            {
                return;
            }

            shapeKey = key;
            sections.Clear();
            UiFactory.ClearChildren(content);
            build();
        }

        // A section with a heading, or without one for a null title.
        private SectionView AddSection(string title, Sprite icon, bool withGrid)
        {
            var root = UiFactory.Column(content, "Section " + (title ?? "Slots"), 8f).GetComponent<RectTransform>();
            TextMeshProUGUI detail = null;
            var heading = title != null ? UiFactory.SectionHeading(theme, root, title, icon, out detail) : null;
            var section = new SectionView
            {
                Root = root,
                Title = heading,
                Detail = detail,
                Grid = withGrid
                    ? new ItemSlotGrid(theme, root, CharacterInventoryWindow.Columns, owner.DragCoordinator, owner.Tooltip)
                    : null
            };
            sections.Add(section);
            return section;
        }

        private void SetFooter(string key, Action<RectTransform> build)
        {
            if (string.Equals(key, footerKey, StringComparison.Ordinal))
            {
                return;
            }

            footerKey = key;
            UiFactory.ClearChildren(Window.Footer);
            build?.Invoke(Window.Footer);
        }

        private static string TabLabel(InventoryContextKind context)
        {
            return context switch
            {
                InventoryContextKind.Bank => "Bank",
                InventoryContextKind.RecoveryStorage => "Recovery",
                InventoryContextKind.Corpse => "Corpse",
                InventoryContextKind.Vendor => "Trade",
                _ => context.ToString()
            };
        }

        private sealed class SectionView
        {
            public RectTransform Root;
            public TextMeshProUGUI Title;
            public TextMeshProUGUI Detail;
            public ItemSlotGrid Grid;
        }
    }
}
