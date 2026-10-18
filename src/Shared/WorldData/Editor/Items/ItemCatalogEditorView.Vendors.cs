using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Editor.ContentTools;
using Mmorpg.WorldData.Items;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Mmorpg.WorldData.Editor.Items
{
    // The Vendors mode of the Item Catalog tool: vendor profiles with their price rates,
    // the categories they buy, and the offers they list in order. Vendor profiles share the
    // definitions' undo history, live validation, and save.
    public sealed partial class ItemCatalogEditorView
    {
        public const string VendorIssueKeyPrefix = "vendor:";

        private ContentListPane<ItemCatalogJsonVendor> vendorList;
        private VisualElement definitionActions;
        private VisualElement vendorActions;
        private ToolbarToggle definitionsMode;
        private ToolbarToggle vendorsMode;
        private ContentForm<ItemCatalogJsonVendor> vendorForm;
        private bool showVendors;
        private string vendorDetailShape = string.Empty;

        public bool IsShowingVendors => showVendors;

        public ContentListPane<ItemCatalogJsonVendor> VendorList => vendorList;

        public ContentForm<ItemCatalogJsonVendor> VendorForm => vendorForm;

        public void ShowVendors(bool value)
        {
            showVendors = value;
            definitionsMode.SetValueWithoutNotify(!value);
            vendorsMode.SetValueWithoutNotify(value);
            list.style.display = value ? DisplayStyle.None : DisplayStyle.Flex;
            definitionActions.style.display = value ? DisplayStyle.None : DisplayStyle.Flex;
            vendorList.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
            vendorActions.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
            detailShape = string.Empty;
            vendorDetailShape = string.Empty;
            if (value)
            {
                OnVendorSelectionChanged();
            }
            else
            {
                OnSelectionChanged();
            }
        }

        public void CreateVendor()
        {
            if (workspace == null)
            {
                return;
            }

            ItemCatalogJsonVendor created = null;
            session.Change("New vendor profile", () => created = workspace.CreateVendor());
            vendorList.Select(new[] { created.id });
            toolLayout.SetStatus(
                "Created a vendor profile. A vendor capability in Actor Studio names it by id.",
                HelpBoxMessageType.Info);
        }

        public void RemoveSelectedVendors()
        {
            var selection = vendorList.Selection;
            if (workspace == null || selection.Count == 0)
            {
                return;
            }

            session.Change(
                selection.Count == 1 ? "Remove vendor profile" : "Remove " + selection.Count + " vendor profiles",
                () =>
                {
                    foreach (var vendor in selection.ToArray())
                    {
                        workspace.RemoveVendor(vendor);
                    }
                });
            toolLayout.SetStatus(
                "Removed " + selection.Count + (selection.Count == 1 ? " vendor profile." : " vendor profiles.")
                    + " Actor content that names a removed vendor fails validation until it is updated.",
                HelpBoxMessageType.Warning);
        }

        private void CreateVendorMode()
        {
            definitionsMode = new ToolbarToggle { text = "Definitions", value = true, tooltip = "Edit item definitions." };
            vendorsMode = new ToolbarToggle { text = "Vendors", tooltip = "Edit vendor profiles: prices, offers, and the categories they buy." };
            definitionsMode.RegisterValueChangedCallback(change => ShowVendors(!change.newValue));
            vendorsMode.RegisterValueChangedCallback(change => ShowVendors(change.newValue));
            toolLayout.Toolbar.Insert(0, vendorsMode);
            toolLayout.Toolbar.Insert(0, definitionsMode);

            vendorList = new ContentListPane<ItemCatalogJsonVendor>(
                "Vendor Profiles",
                vendor => vendor.id,
                vendor => vendor.displayName,
                vendor => vendor.id + " | " + (vendor.offers?.Length ?? 0) + " offers",
                vendor => string.Join(" ", (vendor.offers ?? Array.Empty<ItemCatalogJsonVendorOffer>()).Select(offer => offer.definitionId)));
            vendorList.AddToggleFilter("Problems", vendor => VendorIssueState(vendor) != null);
            vendorList.SelectionChanged += OnVendorSelectionChanged;
            vendorList.style.display = DisplayStyle.None;
            toolLayout.ListSlot.Add(vendorList);
            vendorActions = new VisualElement();
            vendorActions.AddToClassList(ContentToolStyles.Row);
            vendorActions.Add(new Button(CreateVendor) { text = "New", tooltip = "Create a vendor profile." });
            vendorActions.Add(new Button(RemoveSelectedVendors) { text = "Remove", tooltip = "Remove the selected vendor profiles." });
            vendorActions.style.display = DisplayStyle.None;
            toolLayout.ListSlot.Add(vendorActions);
        }

        private void LoadVendors()
        {
            vendorForm = new ContentForm<ItemCatalogJsonVendor>(session, vendor => vendor.id);
            vendorDetailShape = string.Empty;
            vendorList.SetItems(workspace.Authoring.vendors);
            if (vendorList.Selection.Count == 0 && workspace.Authoring.vendors.Length > 0)
            {
                vendorList.Select(new[] { workspace.Authoring.vendors.OrderBy(vendor => vendor.id, StringComparer.Ordinal).First().id });
            }
        }

        private void OnVendorContentChanged()
        {
            vendorList.SetItems(workspace.Authoring.vendors);
            if (showVendors)
            {
                OnVendorSelectionChanged();
            }
        }

        private void OnVendorSelectionChanged()
        {
            if (!showVendors || workspace == null || vendorForm == null)
            {
                return;
            }

            var selection = vendorList.Selection;
            var shape = VendorDetailShape(selection);
            if (!string.Equals(shape, vendorDetailShape, StringComparison.Ordinal))
            {
                vendorDetailShape = shape;
                BuildVendorDetail(selection);
            }
            else
            {
                vendorForm.SetTargets(selection);
            }

            vendorForm.ApplyIssues(VendorIssues());
        }

        // The detail is rebuilt only when the selection or the offer list changes, so typing
        // keeps focus.
        private static string VendorDetailShape(IReadOnlyList<ItemCatalogJsonVendor> selection)
        {
            if (selection.Count != 1)
            {
                return selection.Count == 0 ? "none" : "many";
            }

            var vendor = selection[0];
            return "one:" + vendor.id + ":" + string.Join(
                ",",
                (vendor.offers ?? Array.Empty<ItemCatalogJsonVendorOffer>()).Select(offer => offer.definitionId));
        }

        private void BuildVendorDetail(IReadOnlyList<ItemCatalogJsonVendor> selection)
        {
            vendorForm.Clear();
            form?.Clear();
            toolLayout.Detail.Clear();
            if (selection.Count != 1)
            {
                ShowDetailHint(selection.Count == 0
                    ? "Select a vendor profile, or create one with New."
                    : "Select one vendor profile to edit it.");
                vendorForm.SetTargets(selection);
                return;
            }

            var vendor = selection[0];
            var profile = ContentToolStyles.CreateSection("Vendor Profile");
            var idField = new TextField("Vendor Id") { isDelayed = true };
            idField.SetValueWithoutNotify(vendor.id);
            idField.RegisterValueChangedCallback(change => RenameVendor(change.previousValue, change.newValue));
            vendorForm.Register("id", idField);
            profile.Add(idField);
            profile.Add(vendorForm.Text("Display Name", "displayName", entry => entry.displayName, (entry, value) => entry.displayName = value));
            var purchase = vendorForm.Integer("Selling Price (basis points)", "purchasePriceBasisPoints", entry => entry.purchasePriceBasisPoints, (entry, value) => entry.purchasePriceBasisPoints = value);
            purchase.tooltip = "What players pay, in basis points of the item value. 10000 is the value itself; totals round up.";
            profile.Add(purchase);
            var sale = vendorForm.Integer("Buying Price (basis points)", "salePriceBasisPoints", entry => entry.salePriceBasisPoints, (entry, value) => entry.salePriceBasisPoints = value);
            sale.tooltip = "What players receive, in basis points of the item value; totals round down. It must stay below the selling price, so buying and selling back never gains.";
            profile.Add(sale);
            profile.Add(vendorForm.Flags(
                "Buys Categories",
                "saleCategories",
                CategoryNamesExceptQuestItems,
                entry => entry.saleCategories,
                (entry, value) => entry.saleCategories = value,
                "No item categories are defined in the catalog."));
            toolLayout.Detail.Add(profile);
            toolLayout.Detail.Add(BuildOffersSection(vendor));
            vendorForm.SetTargets(selection);
        }

        private VisualElement BuildOffersSection(ItemCatalogJsonVendor vendor)
        {
            var section = ContentToolStyles.CreateSection("Offers");
            var offers = vendor.offers ?? Array.Empty<ItemCatalogJsonVendorOffer>();
            if (offers.Length == 0)
            {
                section.Add(ContentToolStyles.CreateHint("This vendor sells nothing yet. Add offers below."));
            }

            for (var position = 0; position < offers.Length; position++)
            {
                section.Add(BuildOfferRow(vendor, offers[position], position, offers.Length));
            }

            var addRow = new VisualElement();
            addRow.AddToClassList(ContentToolStyles.Row);
            var candidates = OfferCandidates(vendor).ToList();
            var choice = new DropdownField(
                "Add Offer",
                candidates.Select(candidate => candidate.Label).ToList(),
                candidates.Count > 0 ? 0 : -1);
            choice.style.flexGrow = 1f;
            var add = new Button(() =>
            {
                if (choice.index >= 0 && choice.index < candidates.Count)
                {
                    AddOffer(vendor, candidates[choice.index].Value);
                }
            })
            { text = "Add" };
            add.SetEnabled(candidates.Count > 0);
            addRow.Add(choice);
            addRow.Add(add);
            vendorForm.Register("offers", addRow);
            section.Add(addRow);
            section.Add(ContentToolStyles.CreateHint(
                "Offers need a positive value and no default policies, and quest items are never sold. "
                    + "The client lists them in this order."));
            return section;
        }

        private VisualElement BuildOfferRow(
            ItemCatalogJsonVendor vendor,
            ItemCatalogJsonVendorOffer offer,
            int position,
            int count)
        {
            var row = new VisualElement();
            row.AddToClassList(ContentToolStyles.Row);
            var definition = workspace.Authoring.definitions.FirstOrDefault(entry =>
                string.Equals(entry.id, offer.definitionId, StringComparison.Ordinal));
            var label = new Label(definition == null
                ? offer.definitionId + " (unknown definition)"
                : definition.displayName + " (" + definition.id + "), value " + definition.value);
            label.style.flexGrow = 1f;
            row.Add(label);
            var up = new Button(() => MoveOffer(vendor, position, -1)) { text = "Up" };
            up.SetEnabled(position > 0);
            var down = new Button(() => MoveOffer(vendor, position, 1)) { text = "Down" };
            down.SetEnabled(position < count - 1);
            row.Add(up);
            row.Add(down);
            row.Add(new Button(() => RemoveOffer(vendor, position)) { text = "Remove" });
            vendorForm.Register("offers[" + position + "]", row);
            return row;
        }

        // Definitions a vendor may offer and does not offer yet.
        private IEnumerable<(string Value, string Label)> OfferCandidates(ItemCatalogJsonVendor vendor)
        {
            var offered = new HashSet<string>(
                (vendor.offers ?? Array.Empty<ItemCatalogJsonVendorOffer>()).Select(offer => offer.definitionId),
                StringComparer.Ordinal);
            return workspace.Authoring.definitions
                .Where(definition => !offered.Contains(definition.id)
                    && definition.value > 0
                    && !string.Equals(definition.category, ItemCategoryIds.QuestItem, StringComparison.Ordinal)
                    && (definition.defaultPolicies == null || definition.defaultPolicies.Length == 0))
                .OrderBy(definition => definition.id, StringComparer.Ordinal)
                .Select(definition => (definition.id, definition.displayName + " (" + definition.id + ")"));
        }

        private void AddOffer(ItemCatalogJsonVendor vendor, string definitionId)
        {
            session.Change("Vendor offer added", () =>
                vendor.offers = (vendor.offers ?? Array.Empty<ItemCatalogJsonVendorOffer>())
                    .Concat(new[] { new ItemCatalogJsonVendorOffer { definitionId = definitionId } })
                    .ToArray());
        }

        private void RemoveOffer(ItemCatalogJsonVendor vendor, int position)
        {
            session.Change("Vendor offer removed", () =>
                vendor.offers = vendor.offers.Where((_, index) => index != position).ToArray());
        }

        private void MoveOffer(ItemCatalogJsonVendor vendor, int position, int step)
        {
            var target = position + step;
            if (target < 0 || target >= vendor.offers.Length)
            {
                return;
            }

            session.Change("Vendor offer moved", () =>
            {
                var offers = vendor.offers.ToArray();
                (offers[position], offers[target]) = (offers[target], offers[position]);
                vendor.offers = offers;
            });
        }

        private void RenameVendor(string currentId, string newId)
        {
            var vendor = workspace.Authoring.vendors.FirstOrDefault(entry =>
                string.Equals(entry.id, currentId, StringComparison.Ordinal));
            if (vendor == null || string.Equals(currentId, newId, StringComparison.Ordinal))
            {
                return;
            }

            var error = string.Empty;
            session.Change("Vendor id changed", () => workspace.TryRenameVendor(vendor, newId, out error));
            if (error.Length > 0)
            {
                toolLayout.SetStatus(error, HelpBoxMessageType.Error);
                vendorDetailShape = string.Empty;
                OnVendorSelectionChanged();
                return;
            }

            vendorList.Select(new[] { newId });
            toolLayout.SetStatus(
                "Renamed the vendor profile. Update every vendor capability that names '" + currentId + "'.",
                HelpBoxMessageType.Warning);
        }

        private IEnumerable<(string Value, string Label)> CategoryNamesExceptQuestItems()
        {
            return IdentityNames(workspace?.Authoring.categories)
                .Where(option => !string.Equals(option.Value, ItemCategoryIds.QuestItem, StringComparison.Ordinal));
        }

        // Vendor findings use the vendor id behind a prefix, so they never collide with
        // definition ids.
        private ContentIssue ToVendorIssue(string error)
        {
            var vendors = workspace.Authoring.vendors;
            var issue = ContentIssuePath.ToIssue(
                error,
                "vendors",
                index => index < vendors.Length ? vendors[index].id : null);
            return string.IsNullOrEmpty(issue.EntityKey)
                ? issue
                : new ContentIssue(VendorIssueKeyPrefix + issue.EntityKey, issue.Field, issue.Message, issue.Severity);
        }

        private IReadOnlyList<ContentIssue> VendorIssues()
        {
            return issues
                .Where(issue => issue.EntityKey.StartsWith(VendorIssueKeyPrefix, StringComparison.Ordinal))
                .Select(issue => new ContentIssue(
                    issue.EntityKey.Substring(VendorIssueKeyPrefix.Length),
                    issue.Field,
                    issue.Message,
                    issue.Severity))
                .ToArray();
        }

        private ContentIssueSeverity? VendorIssueState(ItemCatalogJsonVendor vendor)
        {
            var related = VendorIssues()
                .Where(issue => string.Equals(issue.EntityKey, vendor.id, StringComparison.Ordinal))
                .ToArray();
            if (related.Length == 0)
            {
                return null;
            }

            return related.Any(issue => issue.Severity == ContentIssueSeverity.Error)
                ? ContentIssueSeverity.Error
                : ContentIssueSeverity.Warning;
        }

        private bool TryShowVendorIssue(ContentIssue issue)
        {
            if (!issue.EntityKey.StartsWith(VendorIssueKeyPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (!showVendors)
            {
                ShowVendors(true);
            }

            vendorList.Select(new[] { issue.EntityKey.Substring(VendorIssueKeyPrefix.Length) });
            var field = vendorForm.FindField(issue.Field);
            if (field != null)
            {
                toolLayout.Detail.ScrollTo(field);
                field.Focus();
            }

            return true;
        }

        private string DescribeVendorIssue(ContentIssue issue)
        {
            var id = issue.EntityKey.Substring(VendorIssueKeyPrefix.Length);
            var vendor = workspace?.Authoring.vendors.FirstOrDefault(entry =>
                string.Equals(entry.id, id, StringComparison.Ordinal));
            return "Vendor " + (vendor == null ? id : vendor.displayName + " (" + vendor.id + ")") + ": " + issue.Message;
        }
    }
}
