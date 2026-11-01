using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.WorldData.Combat;
using Mmorpg.WorldData.Editor.ContentTools;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Items.Presentation;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Mmorpg.WorldData.Editor.Items
{
    // The Item Catalog tool: a filterable list of item definitions beside an editor for the
    // selected ones, live validation with the shared compiler, and the save and bake actions.
    // Several selected definitions are edited together; identity, bag layout, combat data, and
    // client presentation are edited one definition at a time. The Vendors mode edits vendor
    // profiles with the same history and save.
    public sealed partial class ItemCatalogEditorView : VisualElement, IDisposable
    {
        public const long LiveValidationDelayMilliseconds = 300;

        private readonly ItemCatalogEditorService service;
        private readonly ContentUndoState undoState;
        private readonly Func<ItemCatalogEditorWorkspace> loadWorkspace;
        private readonly ContentToolLayout toolLayout;
        private readonly ContentListPane<ItemCatalogJsonDefinition> list;
        private readonly Label revisionLabel;
        private readonly Label validationInfo;
        private readonly Button removeButton;
        private readonly Button duplicateButton;
        private readonly IVisualElementScheduledItem liveValidation;
        // The client content root and its item icon folder. The folder is one Addressables folder
        // entry, so an icon's address is its path below the content root.
        private const string ContentRoot = "Assets/Content/";
        private const string IconFolder = ContentRoot + "Items/Icons";

        private readonly Dictionary<string, Sprite> iconCache = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private ContentEditSession<ItemCatalogEditorSnapshot> session;
        private ContentForm<ItemCatalogJsonDefinition> form;
        private ItemCatalogEditorWorkspace workspace;
        private ItemCatalogEditorValidationResult lastValidation;
        private IReadOnlyList<ContentIssue> issues = Array.Empty<ContentIssue>();
        private string detailShape = string.Empty;

        public ItemCatalogEditorView(
            ItemCatalogEditorService service,
            ContentUndoState undoState,
            Func<ItemCatalogEditorWorkspace> loadWorkspace = null)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.undoState = undoState ? undoState : throw new ArgumentNullException(nameof(undoState));
            this.loadWorkspace = loadWorkspace ?? service.Load;
            style.flexGrow = 1f;

            toolLayout = new ContentToolLayout(300f);
            Add(toolLayout);
            var reload = new ToolbarButton(Reload) { text = "Reload", tooltip = "Load the item catalog from disk again." };
            revisionLabel = new Label();
            revisionLabel.style.marginLeft = 8f;
            revisionLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            toolLayout.Toolbar.Add(reload);
            toolLayout.Toolbar.Add(new ToolbarSpacer { flex = true });
            toolLayout.Toolbar.Add(revisionLabel);

            list = new ContentListPane<ItemCatalogJsonDefinition>(
                "Item Definitions",
                definition => definition.id,
                definition => definition.displayName,
                Subtitle,
                definition => string.Join(" ", definition.tags ?? Array.Empty<string>()));
            list.AddChoiceFilter("All categories", CategoryOptions, (definition, value) =>
                string.Equals(definition.category, value, StringComparison.Ordinal));
            list.AddChoiceFilter("All tags", TagOptions, (definition, value) =>
                (definition.tags ?? Array.Empty<string>()).Contains(value, StringComparer.Ordinal));
            list.AddToggleFilter("Problems", definition => IssueState(definition) != null);
            list.SelectionChanged += OnSelectionChanged;
            toolLayout.ListSlot.Add(list);
            definitionActions = new VisualElement();
            definitionActions.AddToClassList(ContentToolStyles.Row);
            definitionActions.Add(new Button(CreateDefinition) { text = "New", tooltip = "Create a draft item definition." });
            duplicateButton = new Button(DuplicateSelected) { text = "Duplicate", tooltip = "Create a draft copy of every selected definition." };
            removeButton = new Button(RemoveSelectedDrafts) { text = "Remove Draft", tooltip = "Remove the selected definitions that were never baked." };
            definitionActions.Add(duplicateButton);
            definitionActions.Add(removeButton);
            toolLayout.ListSlot.Add(definitionActions);
            CreateVendorMode();

            toolLayout.Actions.Add(new Button(() => ValidateWithCompiler()) { text = "Validate", tooltip = "Compile the catalog with the content compiler." });
            toolLayout.Actions.Add(new Button(() => Save(false)) { text = "Save", tooltip = "Write the authoring and presentation files." });
            toolLayout.Actions.Add(new Button(() => Save(true)) { text = "Save And Bake", tooltip = "Also replace the deterministic WorldData runtime catalog." });
            validationInfo = new Label(
                "Save writes the editable authoring and presentation files. Save And Bake also replaces the "
                    + "runtime catalog. Neither writes item instances or persistence data.");
            validationInfo.AddToClassList("content-actions__info");
            toolLayout.Actions.Add(validationInfo);
            toolLayout.Issues.IssueActivated += ShowIssue;

            liveValidation = schedule.Execute(RunLiveValidation);
            liveValidation.Pause();
            ShowDetailHint("Loading the item catalog...");
        }

        public event Action UnsavedChangesChanged;

        public bool HasUnsavedChanges => session != null && session.HasUnsavedChanges;

        public ItemCatalogEditorWorkspace Workspace => workspace;

        public ContentListPane<ItemCatalogJsonDefinition> List => list;

        public ContentForm<ItemCatalogJsonDefinition> Form => form;

        public IReadOnlyList<ContentIssue> Issues => issues;

        public string StatusText => toolLayout.StatusText;

        public HelpBoxMessageType StatusType => toolLayout.StatusType;

        // Loads the catalog from disk. With keepPendingEdits, edits that were not saved before
        // a script reload are applied again.
        public void Load(bool keepPendingEdits)
        {
            try
            {
                session?.Dispose();
                workspace = loadWorkspace();
                iconCache.Clear();
                lastValidation = null;
                session = new ContentEditSession<ItemCatalogEditorSnapshot>(
                    undoState,
                    () => new ItemCatalogEditorSnapshot
                    {
                        authoring = workspace.Authoring,
                        presentation = workspace.Presentation
                    },
                    snapshot => workspace.RestoreContent(snapshot.authoring, snapshot.presentation));
                form = new ContentForm<ItemCatalogJsonDefinition>(session, definition => definition.id);
                detailShape = string.Empty;
                session.Changed += OnContentChanged;
                session.Load(keepPendingEdits);
                LoadVendors();
                if (list.Selection.Count == 0 && workspace.Authoring.definitions.Length > 0)
                {
                    list.Select(new[] { workspace.Authoring.definitions.OrderBy(definition => definition.id, StringComparer.Ordinal).First().id });
                }

                toolLayout.SetStatus(
                    "Loaded " + workspace.Authoring.definitions.Length
                        + " item definitions. Canonical authoring passed strict validation."
                        + (HasUnsavedChanges ? " Unsaved edits from before the script reload were restored." : string.Empty),
                    HelpBoxMessageType.Info);
                RunLiveValidation();
            }
            catch (Exception exception)
            {
                workspace = null;
                session?.Dispose();
                session = null;
                list.SetItems(Array.Empty<ItemCatalogJsonDefinition>());
                vendorList.SetItems(Array.Empty<ItemCatalogJsonVendor>());
                ShowDetailHint("The item catalog could not be loaded.");
                toolLayout.SetStatus(exception.Message, HelpBoxMessageType.Error);
            }

            UpdateRevisionLabel();
            UnsavedChangesChanged?.Invoke();
        }

        public void Reload()
        {
            if (HasUnsavedChanges
                && !EditorUtility.DisplayDialog(
                    "Discard Unsaved Item Changes",
                    "The item catalog has unsaved changes. Reloading discards them.",
                    "Discard and reload",
                    "Cancel"))
            {
                return;
            }

            Load(false);
        }

        // Checks the catalog with the shared compiler inside Unity and maps every finding to
        // the definition and field it concerns.
        public IReadOnlyList<ContentIssue> ValidateLive()
        {
            if (workspace == null)
            {
                return Array.Empty<ContentIssue>();
            }

            var found = new List<ContentIssue>();
            var definitions = workspace.Authoring.definitions;
            try
            {
                ItemCatalogCompiler.Compile(workspace.Authoring.ToAuthoringDocument());
            }
            catch (ItemCatalogValidationException exception)
            {
                found.AddRange(exception.Errors.Select(error => error.StartsWith("vendors", StringComparison.Ordinal)
                    ? ToVendorIssue(error)
                    : ContentIssuePath.ToIssue(
                        error,
                        "definitions",
                        index => index < definitions.Length ? definitions[index].id : null)));
            }
            catch (Exception exception)
            {
                found.Add(new ContentIssue(string.Empty, string.Empty, exception.Message));
            }

            var entries = workspace.Presentation.entries;
            try
            {
                ItemPresentationCatalogCompiler.Compile(
                    workspace.CurrentRuntime.revision,
                    definitions.Select(definition => definition.id),
                    entries);
            }
            catch (ItemPresentationCatalogValidationException exception)
            {
                found.AddRange(exception.Errors.Select(error => ContentIssuePath.ToIssue(
                    error,
                    "entries",
                    index => index < entries.Length ? entries[index]?.definitionId : null,
                    "presentation.")));
            }
            catch (Exception exception)
            {
                found.Add(new ContentIssue(string.Empty, string.Empty, exception.Message));
            }

            return found;
        }

        // Compiles the catalog with the content compiler process, the same check that saving
        // runs.
        public bool ValidateWithCompiler()
        {
            if (workspace == null)
            {
                return false;
            }

            lastValidation = service.Validate(workspace);
            toolLayout.SetStatus(
                lastValidation.Message,
                lastValidation.Success ? HelpBoxMessageType.Info : HelpBoxMessageType.Error);
            UpdateValidationInfo();
            RunLiveValidation();
            return lastValidation.Success;
        }

        public bool Save(bool bake, Func<IEnumerable<ItemCatalogDefinitionChange>, bool, bool> confirmStructuralChanges = null)
        {
            if (!ValidateWithCompiler())
            {
                return false;
            }

            var validation = lastValidation;
            var confirm = confirmStructuralChanges ?? ConfirmStructuralChanges;
            if (validation.RequiresStructuralConfirmation && !confirm(validation.Changes, bake))
            {
                toolLayout.SetStatus("The write was cancelled. No catalog files were changed.", HelpBoxMessageType.Warning);
                return false;
            }

            try
            {
                if (bake)
                {
                    service.SaveAndBake(workspace, validation);
                }
                else
                {
                    service.Save(workspace, validation);
                }

                AssetDatabase.Refresh();
                session.MarkSaved();
                UpdateRevisionLabel();
                toolLayout.SetStatus(
                    "Item catalog " + (bake ? "saved and baked" : "saved") + " successfully. " + validation.Message,
                    HelpBoxMessageType.Info);
                return true;
            }
            catch (Exception exception)
            {
                toolLayout.SetStatus("Item catalog write failed: " + exception.Message, HelpBoxMessageType.Error);
                return false;
            }
        }

        public void CreateDefinition()
        {
            if (workspace == null)
            {
                return;
            }

            ItemCatalogJsonDefinition created = null;
            session.Change("New item definition", () => created = workspace.CreateDefinition());
            list.Select(new[] { created.id });
            toolLayout.SetStatus("Created a draft definition. Its id can be edited until it is baked.", HelpBoxMessageType.Info);
        }

        public void DuplicateSelected()
        {
            var selection = list.Selection;
            if (workspace == null || selection.Count == 0)
            {
                return;
            }

            var copies = new List<string>();
            session.Change(
                selection.Count == 1 ? "Duplicate item definition" : "Duplicate " + selection.Count + " item definitions",
                () => copies.AddRange(selection.Select(definition => workspace.DuplicateDefinition(definition).id)));
            list.Select(copies);
            toolLayout.SetStatus(
                "Created " + copies.Count + (copies.Count == 1 ? " draft copy." : " draft copies.")
                    + " Their ids can be edited until they are baked.",
                HelpBoxMessageType.Info);
        }

        public void RemoveSelectedDrafts()
        {
            var selection = list.Selection;
            if (workspace == null || selection.Count == 0)
            {
                return;
            }

            var drafts = selection.Where(definition => !workspace.IsDefinitionIdLocked(definition.id)).ToArray();
            var removed = 0;
            session.Change(
                drafts.Length == 1 ? "Remove draft item definition" : "Remove " + drafts.Length + " draft item definitions",
                () =>
                {
                    foreach (var definition in drafts)
                    {
                        if (workspace.TryRemoveDraftDefinition(definition, out _))
                        {
                            removed++;
                        }
                    }
                });
            var kept = selection.Count - removed;
            toolLayout.SetStatus(
                "Removed " + removed + (removed == 1 ? " draft definition." : " draft definitions.")
                    + (kept > 0 ? " Baked definitions cannot be removed, so " + kept + " stayed." : string.Empty),
                kept > 0 ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info);
        }

        public void Dispose()
        {
            liveValidation.Pause();
            session?.Dispose();
            session = null;
        }

        private void OnContentChanged()
        {
            lastValidation = null;
            UpdateValidationInfo();
            list.SetItems(workspace.Authoring.definitions);
            OnVendorContentChanged();
            UnsavedChangesChanged?.Invoke();
            liveValidation.ExecuteLater(LiveValidationDelayMilliseconds);
        }

        private void RunLiveValidation()
        {
            issues = ValidateLive();
            list.SetIssueState(IssueState);
            vendorList.SetIssueState(VendorIssueState);
            toolLayout.Issues.SetIssues(issues, DescribeIssue);
            form?.ApplyIssues(issues);
            vendorForm?.ApplyIssues(VendorIssues());
        }

        private void OnSelectionChanged()
        {
            if (workspace == null || form == null || showVendors)
            {
                return;
            }

            var selection = list.Selection;
            var shape = DetailShape(selection);
            if (!string.Equals(shape, detailShape, StringComparison.Ordinal))
            {
                detailShape = shape;
                BuildDetail(selection);
            }
            else
            {
                form.SetTargets(selection);
            }

            form.ApplyIssues(issues);
            duplicateButton.SetEnabled(selection.Count > 0);
            removeButton.SetEnabled(selection.Any(definition => !workspace.IsDefinitionIdLocked(definition.id)));
        }

        // The detail panel is rebuilt only when its toolLayout changes, so typing keeps focus.
        private static string DetailShape(IReadOnlyList<ItemCatalogJsonDefinition> selection)
        {
            if (selection.Count == 0)
            {
                return "none";
            }

            if (selection.Count > 1)
            {
                return "many";
            }

            var definition = selection[0];
            var slots = definition.bag?.slots ?? Array.Empty<ItemCatalogJsonBagSlot>();
            return "one:" + definition.id
                + ":" + (definition.bag == null ? "no-bag" : string.Join(",", slots.Select(slot => slot.kind)))
                + ":" + (definition.weapon == null ? "no-weapon" : "weapon")
                + ":" + (definition.ammunition == null ? "no-ammunition" : "ammunition");
        }

        private void BuildDetail(IReadOnlyList<ItemCatalogJsonDefinition> selection)
        {
            form.Clear();
            vendorForm?.Clear();
            toolLayout.Detail.Clear();
            if (selection.Count == 0)
            {
                ShowDetailHint("Select one or more item definitions. Hold Ctrl or Shift to select several.");
                form.SetTargets(selection);
                return;
            }

            var single = selection.Count == 1;
            var identity = ContentToolStyles.CreateSection("Definition");
            if (single)
            {
                AddIdentityFields(identity, selection[0]);
            }
            else
            {
                identity.Add(ContentToolStyles.CreateHint(
                    selection.Count + " definitions selected. Changes apply to every selected definition. "
                        + "Id, display name, bag layout, combat data, and presentation are edited one definition at a time."));
            }

            identity.Add(form.Choice("Category", "category", CategoryOptions, definition => definition.category, (definition, value) => definition.category = value));
            var weight = form.Long("Weight", "unitWeight", definition => definition.unitWeight, (definition, value) => definition.unitWeight = value);
            weight.tooltip = "Neutral whole-number gameplay units with no physical measurement.";
            identity.Add(weight);
            identity.Add(form.Integer("Maximum Stack Size", "maximumStackSize", definition => definition.maximumStackSize, (definition, value) => definition.maximumStackSize = value));
            var value = form.Long("Value", "value", definition => definition.value, (definition, newValue) => definition.value = newValue);
            value.tooltip = "The base price in currency units. Vendors sell and buy at a share of it; 0 means vendors never trade it.";
            identity.Add(value);
            var level = form.Integer("Required Character Level", "requiredCharacterLevel", definition => definition.requiredCharacterLevel, (definition, value) => definition.requiredCharacterLevel = value);
            level.tooltip = "The character level needed to equip the item. 1 means every character.";
            identity.Add(level);
            toolLayout.Detail.Add(identity);

            var compatibility = ContentToolStyles.CreateSection("Tags And Compatibility");
            compatibility.Add(form.Flags(
                "Tags",
                "tags",
                TagNames,
                definition => definition.tags,
                (definition, value) => definition.tags = value,
                "No item tags are defined in the catalog."));
            compatibility.Add(form.Flags(
                "Equipment Compatibility",
                "equipmentSlots",
                EquipmentSlotNames,
                definition => definition.equipmentSlots,
                (definition, value) => definition.equipmentSlots = value,
                "No equipment slots are defined in the catalog."));
            var blocked = form.Flags(
                "Blocked Equipment Slots",
                "blockedEquipmentSlots",
                EquipmentSlotNames,
                definition => definition.blockedEquipmentSlots,
                (definition, value) => definition.blockedEquipmentSlots = value,
                "No equipment slots are defined in the catalog.");
            blocked.tooltip = "Slots that stay empty while this item is equipped, such as the off hand for a two-handed weapon.";
            compatibility.Add(blocked);
            var toolKinds = form.Flags(
                "Tool Kinds",
                "toolKinds",
                ToolKindNames,
                definition => definition.toolKinds,
                (definition, newValue) => definition.toolKinds = newValue,
                "No tool kinds are defined in the catalog.");
            toolKinds.tooltip = "Gathering kinds this item serves as a tool for while equipped in the Tool slot.";
            compatibility.Add(toolKinds);
            toolLayout.Detail.Add(compatibility);

            var rules = ContentToolStyles.CreateSection("Policies");
            rules.Add(form.Flags(
                "Default Policies",
                "defaultPolicies",
                PolicyOptions,
                definition => definition.defaultPolicies,
                (definition, value) => definition.defaultPolicies = value,
                string.Empty));
            rules.Add(form.Toggle(
                "Player Destroyable",
                "playerDestroyable",
                definition => definition.playerDestroyable,
                (definition, value) => definition.playerDestroyable = value));
            toolLayout.Detail.Add(rules);

            if (single)
            {
                toolLayout.Detail.Add(BuildBagSection(selection[0]));
                toolLayout.Detail.Add(BuildCombatSection(selection[0]));
                toolLayout.Detail.Add(BuildPresentationSection());
            }

            form.SetTargets(selection);
        }

        private void AddIdentityFields(VisualElement section, ItemCatalogJsonDefinition definition)
        {
            var locked = workspace.IsDefinitionIdLocked(definition.id);
            var idField = new TextField("Definition Id") { isDelayed = true };
            idField.SetEnabled(!locked);
            idField.RegisterValueChangedCallback(change => RenameDefinition(change.previousValue, change.newValue));
            form.Watch(() =>
            {
                if (form.Targets.Count == 1)
                {
                    idField.SetValueWithoutNotify(form.Targets[0].id);
                }
            });
            form.Register("id", idField);
            section.Add(idField);
            if (locked)
            {
                section.Add(ContentToolStyles.CreateHint("The id is locked because this item has been baked."));
            }

            section.Add(form.Text("Display Name", "displayName", definition => definition.displayName, SetDisplayName));
        }

        private VisualElement BuildBagSection(ItemCatalogJsonDefinition definition)
        {
            var section = ContentToolStyles.CreateSection("Bag");
            section.Add(form.Toggle(
                "Has Bag Definition",
                "bag",
                entry => entry.bag != null,
                (entry, value) => entry.bag = value
                    ? entry.bag ?? new ItemCatalogJsonBagDefinition
                    {
                        carryCapacityBonus = 0,
                        slots = Array.Empty<ItemCatalogJsonBagSlot>()
                    }
                    : null));
            if (definition.bag == null)
            {
                return section;
            }

            section.Add(form.Long(
                "Carry Capacity Bonus",
                "bag.carryCapacityBonus",
                entry => entry.bag?.carryCapacityBonus ?? 0,
                (entry, value) =>
                {
                    if (entry.bag != null)
                    {
                        entry.bag.carryCapacityBonus = value;
                    }
                }));
            var slots = definition.bag.slots ?? Array.Empty<ItemCatalogJsonBagSlot>();
            for (var position = 0; position < slots.Length; position++)
            {
                section.Add(BuildBagSlot(slots[position], position));
            }

            section.Add(new Button(AddBagSlot) { text = "Add Bag Slot" });
            return section;
        }

        private VisualElement BuildBagSlot(ItemCatalogJsonBagSlot slot, int position)
        {
            var box = new VisualElement();
            box.AddToClassList(ContentToolStyles.Section);
            var header = new VisualElement();
            header.AddToClassList(ContentToolStyles.Row);
            var title = new Label("Slot " + slot.index);
            title.AddToClassList(ContentToolStyles.SectionTitle);
            title.style.flexGrow = 1f;
            header.Add(title);
            header.Add(new Button(() => RemoveBagSlot(position)) { text = "Remove" });
            box.Add(header);
            var path = "bag.slots[" + position + "]";
            box.Add(form.Choice(
                "Kind",
                path + ".kind",
                () => new[]
                {
                    (BagSlotKindIds.General, "General"),
                    (BagSlotKindIds.Specialized, "Specialized")
                },
                entry => SlotAt(entry, position)?.kind,
                (entry, value) =>
                {
                    var target = SlotAt(entry, position);
                    if (target == null)
                    {
                        return;
                    }

                    target.kind = value;
                    if (string.Equals(value, BagSlotKindIds.General, StringComparison.Ordinal))
                    {
                        target.acceptedTags = Array.Empty<string>();
                    }
                }));
            if (string.Equals(slot.kind, BagSlotKindIds.Specialized, StringComparison.Ordinal))
            {
                box.Add(form.Flags(
                    "Accepted Tags",
                    path + ".acceptedTags",
                    TagNames,
                    entry => SlotAt(entry, position)?.acceptedTags,
                    (entry, value) =>
                    {
                        var target = SlotAt(entry, position);
                        if (target != null)
                        {
                            target.acceptedTags = value;
                        }
                    },
                    "No tags are available for specialized slots."));
            }
            else
            {
                box.Add(ContentToolStyles.CreateHint("A general slot accepts every item tag."));
            }

            return box;
        }

        // Weapons carry weapon data and ammunition carries ammunition data; the families and
        // their combat styles are edited in catalog.json.
        private VisualElement BuildCombatSection(ItemCatalogJsonDefinition definition)
        {
            var section = ContentToolStyles.CreateSection("Combat");
            section.Add(form.Toggle(
                "Has Weapon Data",
                "weapon",
                entry => entry.weapon != null,
                (entry, value) => entry.weapon = value ? entry.weapon ?? CreateWeapon() : null));
            if (definition.weapon != null)
            {
                section.Add(form.Choice("Weapon Family", "weapon.weaponFamily", WeaponFamilyOptions, entry => entry.weapon?.weaponFamily, (entry, value) => EditWeapon(entry, weapon => weapon.weaponFamily = value)));
                section.Add(form.Choice("Damage Type", "weapon.damageType", DamageTypeOptions, entry => entry.weapon?.damageType, (entry, value) => EditWeapon(entry, weapon => weapon.damageType = value)));
                section.Add(form.Integer("Minimum Damage", "weapon.minimumDamage", entry => entry.weapon?.minimumDamage ?? 0, (entry, value) => EditWeapon(entry, weapon => weapon.minimumDamage = value)));
                section.Add(form.Integer("Maximum Damage", "weapon.maximumDamage", entry => entry.weapon?.maximumDamage ?? 0, (entry, value) => EditWeapon(entry, weapon => weapon.maximumDamage = value)));
                section.Add(form.Float("Attack Interval Seconds", "weapon.attackIntervalSeconds", entry => entry.weapon?.attackIntervalSeconds ?? 0f, (entry, value) => EditWeapon(entry, weapon => weapon.attackIntervalSeconds = value)));
                var range = form.Float("Range", "weapon.range", entry => entry.weapon?.range ?? 0f, (entry, value) => EditWeapon(entry, weapon => weapon.range = value));
                range.tooltip = "Metres from the attacker to the closest point of the target.";
                section.Add(range);
                var manaCost = form.Integer("Mana Cost", "weapon.manaCost", entry => entry.weapon?.manaCost ?? 0, (entry, value) => EditWeapon(entry, weapon => weapon.manaCost = value));
                manaCost.tooltip = "The mana every attack costs; above 0 for a magic weapon and 0 otherwise.";
                section.Add(manaCost);
            }

            section.Add(form.Toggle(
                "Has Ammunition Data",
                "ammunition",
                entry => entry.ammunition != null,
                (entry, value) => entry.ammunition = value ? entry.ammunition ?? CreateAmmunition() : null));
            if (definition.ammunition != null)
            {
                section.Add(form.Choice("Ammunition Family", "ammunition.ammunitionFamily", AmmunitionFamilyOptions, entry => entry.ammunition?.ammunitionFamily, (entry, value) => EditAmmunition(entry, ammunition => ammunition.ammunitionFamily = value)));
                var bonus = form.Integer("Damage Bonus Percent", "ammunition.damageBonusPercent", entry => entry.ammunition?.damageBonusPercent ?? 0, (entry, value) => EditAmmunition(entry, ammunition => ammunition.damageBonusPercent = value));
                bonus.tooltip = "The bonus in percent of the weapon's damage roll.";
                section.Add(bonus);
            }

            return section;
        }

        private ItemCatalogJsonWeapon CreateWeapon()
        {
            return new ItemCatalogJsonWeapon
            {
                weaponFamily = workspace?.Authoring.weaponFamilies?.FirstOrDefault()?.id ?? string.Empty,
                damageType = DamageTypeIds.Normal,
                minimumDamage = 1,
                maximumDamage = 1,
                attackIntervalSeconds = 2.4f,
                range = 1.5f,
                manaCost = 0
            };
        }

        private ItemCatalogJsonAmmunition CreateAmmunition()
        {
            return new ItemCatalogJsonAmmunition
            {
                ammunitionFamily = workspace?.Authoring.ammunitionFamilies?.FirstOrDefault()?.id ?? string.Empty,
                damageBonusPercent = 0
            };
        }

        private static void EditWeapon(ItemCatalogJsonDefinition entry, Action<ItemCatalogJsonWeapon> edit)
        {
            if (entry.weapon != null)
            {
                edit(entry.weapon);
            }
        }

        private static void EditAmmunition(ItemCatalogJsonDefinition entry, Action<ItemCatalogJsonAmmunition> edit)
        {
            if (entry.ammunition != null)
            {
                edit(entry.ammunition);
            }
        }

        private VisualElement BuildPresentationSection()
        {
            var section = ContentToolStyles.CreateSection("Client Presentation");
            var missing = new HelpBox(
                "The presentation entry is missing. Reload to synchronize entries.",
                HelpBoxMessageType.Error);
            section.Add(missing);
            section.Add(form.Text("Localization Key", "presentation.localizationKey", entry => PresentationOf(entry)?.localizationKey, (entry, value) => SetPresentation(entry, presentation => presentation.localizationKey = value)));
            section.Add(form.Text("Fallback Display Name", "presentation.fallbackDisplayName", entry => PresentationOf(entry)?.fallbackDisplayName, (entry, value) => SetPresentation(entry, presentation => presentation.fallbackDisplayName = value)));
            section.Add(form.Text("Prefab Presentation Key", "presentation.prefabPresentationKey", entry => PresentationOf(entry)?.prefabPresentationKey, (entry, value) => SetPresentation(entry, presentation => presentation.prefabPresentationKey = value)));
            // Lists separated by commas: the garment textures the item shows, those it shows
            // lowered, and the colors of their dye channels.
            section.Add(form.Text("Garments", "presentation.garments", entry => JoinList(PresentationOf(entry)?.garments), (entry, value) => SetPresentation(entry, presentation => presentation.garments = SplitList(value))));
            section.Add(form.Text("Lowered Garments", "presentation.loweredGarments", entry => JoinList(PresentationOf(entry)?.loweredGarments), (entry, value) => SetPresentation(entry, presentation => presentation.loweredGarments = SplitList(value))));
            section.Add(form.Text("Dye Colors", "presentation.dyes", entry => JoinList(PresentationOf(entry)?.dyes), (entry, value) => SetPresentation(entry, presentation => presentation.dyes = SplitList(value))));
            // The attachment the item holds on the socket of its equipment slot, such as IronSword.
            section.Add(form.Text("Attachment", "presentation.attachment", entry => PresentationOf(entry)?.attachment, (entry, value) => SetPresentation(entry, presentation => presentation.attachment = value)));

            var icon = new ObjectField("Icon") { objectType = typeof(Sprite), allowSceneObjects = false };
            icon.RegisterValueChangedCallback(change => SetIcon(change.newValue as Sprite));
            var path = new TextField("Icon Address") { isReadOnly = true };
            var clear = new Button(() => SetIcon(null)) { text = "Clear" };
            var pathRow = new VisualElement();
            pathRow.AddToClassList(ContentToolStyles.Row);
            path.style.flexGrow = 1f;
            pathRow.Add(path);
            pathRow.Add(clear);
            form.Watch(() =>
            {
                var presentation = form.Targets.Count == 1 ? PresentationOf(form.Targets[0]) : null;
                missing.style.display = form.Targets.Count == 1 && presentation == null ? DisplayStyle.Flex : DisplayStyle.None;
                icon.SetValueWithoutNotify(ResolveIcon(presentation?.iconAddress));
                path.SetValueWithoutNotify(presentation?.iconAddress ?? string.Empty);
                clear.SetEnabled(!string.IsNullOrWhiteSpace(presentation?.iconAddress));
            });
            form.Register("presentation.iconAddress", icon);
            section.Add(icon);
            section.Add(pathRow);
            section.Add(ContentToolStyles.CreateHint(
                "Icons are client-owned Sprites below " + IconFolder + ", an Addressables folder entry. "
                    + "Only the icon's Addressables address is baked into the presentation catalog."));
            return section;
        }

        private void RenameDefinition(string currentId, string newId)
        {
            var definition = workspace.Authoring.definitions.FirstOrDefault(entry =>
                string.Equals(entry.id, currentId, StringComparison.Ordinal));
            if (definition == null || string.Equals(currentId, newId, StringComparison.Ordinal))
            {
                return;
            }

            var error = string.Empty;
            session.Change("Definition id changed", () => workspace.TryRenameDraftDefinition(definition, newId, out error));
            if (error.Length > 0)
            {
                toolLayout.SetStatus(error, HelpBoxMessageType.Error);
                form.Refresh();
                return;
            }

            list.Select(new[] { newId });
        }

        // The fallback display name follows the display name until it is edited on its own.
        private void SetDisplayName(ItemCatalogJsonDefinition definition, string value)
        {
            var previous = definition.displayName;
            definition.displayName = value;
            var presentation = workspace.FindPresentation(definition.id);
            if (presentation != null
                && (string.IsNullOrWhiteSpace(presentation.fallbackDisplayName)
                    || string.Equals(presentation.fallbackDisplayName, previous, StringComparison.Ordinal)))
            {
                presentation.fallbackDisplayName = value;
            }
        }

        private void AddBagSlot()
        {
            var definition = form.Targets.Count == 1 ? form.Targets[0] : null;
            if (definition?.bag == null)
            {
                return;
            }

            session.Change("Bag slot added", () =>
            {
                var slots = (definition.bag.slots ?? Array.Empty<ItemCatalogJsonBagSlot>()).ToList();
                slots.Add(new ItemCatalogJsonBagSlot
                {
                    index = slots.Count,
                    kind = BagSlotKindIds.General,
                    acceptedTags = Array.Empty<string>()
                });
                definition.bag.slots = slots.ToArray();
            });
        }

        private void RemoveBagSlot(int position)
        {
            var definition = form.Targets.Count == 1 ? form.Targets[0] : null;
            if (definition?.bag?.slots == null || position >= definition.bag.slots.Length)
            {
                return;
            }

            session.Change("Bag slot removed", () =>
            {
                var slots = definition.bag.slots.Where((_, index) => index != position).ToArray();
                for (var index = 0; index < slots.Length; index++)
                {
                    slots[index].index = index;
                }

                definition.bag.slots = slots;
            });
        }

        private void SetIcon(Sprite icon)
        {
            var definition = form.Targets.Count == 1 ? form.Targets[0] : null;
            if (definition == null)
            {
                return;
            }

            if (!TryGetIconAddress(icon, out var iconAddress, out var error))
            {
                toolLayout.SetStatus(error, HelpBoxMessageType.Error);
                form.Refresh();
                return;
            }

            if (icon != null)
            {
                iconCache[iconAddress] = icon;
            }

            session.Change(icon == null ? "Presentation icon cleared" : "Presentation icon changed", () =>
                SetPresentation(definition, presentation => presentation.iconAddress = iconAddress));
        }

        private void SetPresentation(ItemCatalogJsonDefinition definition, Action<ItemPresentationEntry> edit)
        {
            var presentation = PresentationOf(definition);
            if (presentation != null)
            {
                edit(presentation);
            }
        }

        private ItemPresentationEntry PresentationOf(ItemCatalogJsonDefinition definition)
        {
            return definition == null ? null : workspace.FindPresentation(definition.id);
        }

        private static ItemCatalogJsonBagSlot SlotAt(ItemCatalogJsonDefinition definition, int position)
        {
            var slots = definition.bag?.slots;
            return slots != null && position < slots.Length ? slots[position] : null;
        }

        private void ShowIssue(ContentIssue issue)
        {
            if (string.IsNullOrEmpty(issue.EntityKey) || TryShowVendorIssue(issue))
            {
                return;
            }

            if (showVendors)
            {
                ShowVendors(false);
            }

            list.Select(new[] { issue.EntityKey });
            var field = form.FindField(issue.Field);
            if (field != null)
            {
                toolLayout.Detail.ScrollTo(field);
                field.Focus();
            }
        }

        private void ShowDetailHint(string text)
        {
            toolLayout.Detail.Clear();
            toolLayout.Detail.Add(ContentToolStyles.CreateHint(text));
        }

        private ContentIssueSeverity? IssueState(ItemCatalogJsonDefinition definition)
        {
            var related = issues.Where(issue => string.Equals(issue.EntityKey, definition.id, StringComparison.Ordinal)).ToArray();
            if (related.Length == 0)
            {
                return null;
            }

            return related.Any(issue => issue.Severity == ContentIssueSeverity.Error)
                ? ContentIssueSeverity.Error
                : ContentIssueSeverity.Warning;
        }

        private string DescribeIssue(ContentIssue issue)
        {
            if (string.IsNullOrEmpty(issue.EntityKey))
            {
                return issue.Message;
            }

            if (issue.EntityKey.StartsWith(VendorIssueKeyPrefix, StringComparison.Ordinal))
            {
                return DescribeVendorIssue(issue);
            }

            var definition = workspace?.Authoring.definitions.FirstOrDefault(entry =>
                string.Equals(entry.id, issue.EntityKey, StringComparison.Ordinal));
            var name = definition == null ? issue.EntityKey : definition.displayName + " (" + definition.id + ")";
            return name + ": " + issue.Message;
        }

        private string Subtitle(ItemCatalogJsonDefinition definition)
        {
            var category = workspace?.Authoring.categories?.FirstOrDefault(entry =>
                string.Equals(entry.id, definition.category, StringComparison.Ordinal));
            var draft = workspace != null && !workspace.IsDefinitionIdLocked(definition.id) ? " | draft" : string.Empty;
            return definition.id + " | " + (category?.displayName ?? definition.category) + draft;
        }

        private IEnumerable<(string Value, string Label)> CategoryOptions()
        {
            return IdentityOptions(workspace?.Authoring.categories);
        }

        private IEnumerable<(string Value, string Label)> TagOptions()
        {
            return IdentityOptions(workspace?.Authoring.tags);
        }

        // Toggles show the display name and the id as their tooltip.
        private IEnumerable<(string Value, string Label)> TagNames()
        {
            return IdentityNames(workspace?.Authoring.tags);
        }

        private IEnumerable<(string Value, string Label)> ToolKindNames()
        {
            return IdentityNames(workspace?.Authoring.toolKinds);
        }

        private IEnumerable<(string Value, string Label)> EquipmentSlotNames()
        {
            return (workspace?.Authoring.equipmentSlots ?? Array.Empty<ItemCatalogJsonEquipmentSlot>())
                .Select(slot => (slot.id, slot.displayName));
        }

        private IEnumerable<(string Value, string Label)> WeaponFamilyOptions()
        {
            return (workspace?.Authoring.weaponFamilies ?? Array.Empty<ItemCatalogJsonWeaponFamily>())
                .Select(family => (family.id, family.displayName + " (" + family.combatStyle + ")"));
        }

        private IEnumerable<(string Value, string Label)> AmmunitionFamilyOptions()
        {
            return IdentityOptions(workspace?.Authoring.ammunitionFamilies);
        }

        private static IEnumerable<(string Value, string Label)> DamageTypeOptions()
        {
            return DamageTypeIds.All.Select(id => (id, char.ToUpperInvariant(id[0]) + id.Substring(1)));
        }

        private static IEnumerable<(string Value, string Label)> PolicyOptions()
        {
            return new[]
            {
                (ItemPolicyIds.ProtectedOnDeath, "Protected On Death"),
                (ItemPolicyIds.Insured, "Insured")
            };
        }

        private static IEnumerable<(string Value, string Label)> IdentityOptions(ItemCatalogJsonIdentity[] identities)
        {
            return (identities ?? Array.Empty<ItemCatalogJsonIdentity>())
                .Select(identity => (identity.id, identity.displayName + " (" + identity.id + ")"));
        }

        private static IEnumerable<(string Value, string Label)> IdentityNames(ItemCatalogJsonIdentity[] identities)
        {
            return (identities ?? Array.Empty<ItemCatalogJsonIdentity>())
                .Select(identity => (identity.id, identity.displayName));
        }

        private void UpdateRevisionLabel()
        {
            revisionLabel.text = workspace == null
                ? string.Empty
                : "Gameplay revision " + ShortRevision(workspace.CurrentRuntime.revision);
        }

        private void UpdateValidationInfo()
        {
            if (lastValidation == null || !lastValidation.Success)
            {
                validationInfo.text = "Save writes the editable authoring and presentation files. Save And Bake also "
                    + "replaces the runtime catalog. Neither writes item instances or persistence data.";
                return;
            }

            validationInfo.text = "Gameplay revision " + ShortRevision(workspace.CurrentRuntime.revision)
                + " becomes " + ShortRevision(lastValidation.CandidateRuntime.revision)
                + ". Presentation revision " + ShortRevision(workspace.Presentation.presentationRevision)
                + " becomes " + ShortRevision(lastValidation.CandidatePresentation.presentationRevision) + ".";
        }

        private static bool ConfirmStructuralChanges(IEnumerable<ItemCatalogDefinitionChange> changes, bool bake)
        {
            var structuralChanges = changes
                .Where(change => change.Kind == ItemCatalogDefinitionChangeKind.Structural
                    || change.Kind == ItemCatalogDefinitionChangeKind.Removed)
                .Select(change => change.DefinitionId + ": " + change.Kind)
                .ToArray();
            var action = bake ? "save and bake" : "save";
            return EditorUtility.DisplayDialog(
                "Confirm Structural Item Changes",
                "The following changes can affect future persistent item state:\n\n"
                    + string.Join("\n", structuralChanges)
                    + "\n\nGameService now rejects startup when live item instances depend "
                    + "on an incompatible structural change. Such changes require an "
                    + "explicit data migration. Confirm that these changes are intentional before "
                    + action + ".",
                "Confirm " + action,
                "Cancel");
        }

        private static string JoinList(string[] values)
        {
            return values == null ? string.Empty : string.Join(", ", values);
        }

        private static string[] SplitList(string value)
        {
            return (value ?? string.Empty)
                .Split(',')
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToArray();
        }

        private Sprite ResolveIcon(string iconAddress)
        {
            if (string.IsNullOrWhiteSpace(iconAddress))
            {
                return null;
            }

            if (iconCache.TryGetValue(iconAddress, out var icon))
            {
                return icon;
            }

            // The icon folder entry gives every asset the address of its path below the
            // client content root.
            icon = iconAddress.StartsWith(ItemPresentationCatalogFormat.IconAddressPrefix, StringComparison.Ordinal)
                ? AssetDatabase.LoadAssetAtPath<Sprite>(ContentRoot + iconAddress)
                : null;
            iconCache[iconAddress] = icon;
            return icon;
        }

        private static bool TryGetIconAddress(Sprite icon, out string iconAddress, out string error)
        {
            iconAddress = string.Empty;
            error = string.Empty;
            if (icon == null)
            {
                return true;
            }

            var assetPath = AssetDatabase.GetAssetPath(icon).Replace('\\', '/');
            if (!assetPath.StartsWith(IconFolder + "/", StringComparison.Ordinal))
            {
                error = "Item icons must be stored below " + IconFolder + ".";
                return false;
            }

            if (AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Sprite>().Count() != 1)
            {
                error = "Item icons must use one Sprite per asset file.";
                return false;
            }

            iconAddress = assetPath.Substring(ContentRoot.Length);
            return true;
        }

        private static string ShortRevision(string revision)
        {
            return string.IsNullOrWhiteSpace(revision) || revision.Length <= 12
                ? revision ?? string.Empty
                : revision.Substring(0, 12);
        }
    }
}
