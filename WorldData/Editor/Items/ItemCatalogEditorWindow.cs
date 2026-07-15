using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShooterMmo.WorldData.Items;
using ShooterMmo.WorldData.Items.Presentation;
using UnityEditor;
using UnityEngine;

namespace ShooterMmo.WorldData.Editor.Items
{
    public sealed class ItemCatalogEditorWindow : EditorWindow
    {
        private const float CatalogListWidth = 290f;

        private readonly Dictionary<string, Sprite> iconCache =
            new Dictionary<string, Sprite>(StringComparer.Ordinal);

        private ItemCatalogEditorService service;
        private ItemCatalogEditorWorkspace workspace;
        private ItemCatalogEditorDefinition selectedDefinition;
        private ItemCatalogEditorValidationResult lastValidation;
        private Vector2 catalogScroll;
        private Vector2 detailScroll;
        private Vector2 statusScroll;
        private string searchText = string.Empty;
        private string categoryFilter = string.Empty;
        private string statusMessage = string.Empty;
        private MessageType statusType = MessageType.Info;
        private bool loadScheduled;

        [MenuItem("Tools/Shooter MMO/Item Catalog")]
        public static void Open()
        {
            var window = GetWindow<ItemCatalogEditorWindow>();
            window.titleContent = new GUIContent("Item Catalog");
            window.minSize = new Vector2(900f, 600f);
            window.Show();
        }

        private void OnEnable()
        {
            service = new ItemCatalogEditorService(ItemCatalogEditorPaths.CreateDefault());
            if (loadScheduled)
            {
                return;
            }

            loadScheduled = true;
            EditorApplication.delayCall += LoadCatalog;
        }

        private void OnDisable()
        {
            EditorApplication.delayCall -= LoadCatalog;
            loadScheduled = false;
        }

        private void OnGUI()
        {
            DrawToolbar();
            if (workspace == null)
            {
                EditorGUILayout.HelpBox(
                    statusMessage.Length == 0
                        ? "Loading the item catalog..."
                        : statusMessage,
                    statusType);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            DrawCatalogList();
            DrawSelectedDefinition();
            EditorGUILayout.EndHorizontal();
            DrawActions();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(65f)))
            {
                LoadCatalog();
            }

            GUILayout.Space(8f);
            GUILayout.Label("Search", GUILayout.Width(45f));
            searchText = GUILayout.TextField(
                searchText,
                EditorStyles.toolbarSearchField,
                GUILayout.MinWidth(160f));
            GUILayout.FlexibleSpace();
            if (workspace != null)
            {
                GUILayout.Label(
                    "Gameplay revision " + ShortRevision(workspace.CurrentRuntime.revision),
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawCatalogList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(CatalogListWidth));
            GUILayout.Label("Definitions", EditorStyles.boldLabel);
            DrawCategoryFilter();
            catalogScroll = EditorGUILayout.BeginScrollView(catalogScroll);
            var definitions = (workspace.Authoring.definitions
                    ?? Array.Empty<ItemCatalogEditorDefinition>())
                .Where(MatchesSearch)
                .OrderBy(definition => definition.id, StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                var selected = ReferenceEquals(definition, selectedDefinition);
                var label = definition.displayName + "\n" + definition.id;
                var style = selected ? "OL SelectedRow" : "OL Box";
                if (GUILayout.Button(label, style, GUILayout.Height(38f)))
                {
                    selectedDefinition = definition;
                    lastValidation = null;
                    GUI.FocusControl(null);
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("New"))
            {
                selectedDefinition = workspace.CreateDefinition();
                MarkChanged("Created a draft definition. Validate before saving.");
            }

            EditorGUI.BeginDisabledGroup(selectedDefinition == null);
            if (GUILayout.Button("Duplicate"))
            {
                selectedDefinition = workspace.DuplicateDefinition(selectedDefinition);
                MarkChanged("Created a draft copy. Its id can be edited until it is baked.");
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            var canRemove = selectedDefinition != null
                && !workspace.IsDefinitionIdLocked(selectedDefinition.id);
            EditorGUI.BeginDisabledGroup(!canRemove);
            if (GUILayout.Button("Remove Draft"))
            {
                string error;
                if (workspace.TryRemoveDraftDefinition(selectedDefinition, out error))
                {
                    selectedDefinition = workspace.Authoring.definitions.FirstOrDefault();
                    MarkChanged("Removed the unbaked draft definition.");
                }
                else
                {
                    SetStatus(error, MessageType.Error);
                }
            }

            EditorGUI.EndDisabledGroup();
            if (selectedDefinition != null
                && workspace.IsDefinitionIdLocked(selectedDefinition.id))
            {
                EditorGUILayout.HelpBox(
                    "The definition id is locked because this item has been baked.",
                    MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSelectedDefinition()
        {
            EditorGUILayout.BeginVertical();
            if (selectedDefinition == null)
            {
                EditorGUILayout.HelpBox("Select an item definition to edit.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            detailScroll = EditorGUILayout.BeginScrollView(detailScroll);
            DrawIdentityAndStacking();
            GUILayout.Space(8f);
            DrawTagsAndCompatibility();
            GUILayout.Space(8f);
            DrawEligibilityAndPolicies();
            GUILayout.Space(8f);
            DrawBagDefinition();
            GUILayout.Space(8f);
            DrawPresentation();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawIdentityAndStacking()
        {
            GUILayout.Label("Definition", EditorStyles.boldLabel);
            var idLocked = workspace.IsDefinitionIdLocked(selectedDefinition.id);
            EditorGUI.BeginDisabledGroup(idLocked);
            var newId = EditorGUILayout.TextField("Definition Id", selectedDefinition.id);
            EditorGUI.EndDisabledGroup();
            if (!idLocked && !string.Equals(newId, selectedDefinition.id, StringComparison.Ordinal))
            {
                string error;
                if (workspace.TryRenameDraftDefinition(selectedDefinition, newId, out error))
                {
                    MarkChanged("Draft definition id changed.");
                }
                else
                {
                    SetStatus(error, MessageType.Error);
                }
            }

            var previousDisplayName = selectedDefinition.displayName;
            var displayName = EditorGUILayout.TextField(
                "Display Name",
                selectedDefinition.displayName);
            if (!string.Equals(displayName, selectedDefinition.displayName, StringComparison.Ordinal))
            {
                selectedDefinition.displayName = displayName;
                var presentation = workspace.FindPresentation(selectedDefinition.id);
                if (presentation != null
                    && (string.IsNullOrWhiteSpace(presentation.fallbackDisplayName)
                        || string.Equals(
                            presentation.fallbackDisplayName,
                            previousDisplayName,
                            StringComparison.Ordinal)))
                {
                    presentation.fallbackDisplayName = displayName;
                }

                MarkChanged("Display name changed.");
            }

            DrawIdentityPopup(
                "Category",
                workspace.Authoring.categories,
                selectedDefinition.category,
                value => selectedDefinition.category = value);

            var weight = EditorGUILayout.LongField(
                new GUIContent(
                    "Weight",
                    "Neutral whole-number gameplay units with no physical measurement."),
                selectedDefinition.unitWeight);
            if (weight != selectedDefinition.unitWeight)
            {
                selectedDefinition.unitWeight = weight;
                MarkChanged("Weight changed.");
            }

            var maximumStackSize = EditorGUILayout.IntField(
                "Maximum Stack Size",
                selectedDefinition.maximumStackSize);
            if (maximumStackSize != selectedDefinition.maximumStackSize)
            {
                selectedDefinition.maximumStackSize = maximumStackSize;
                MarkChanged("Maximum stack size changed.");
            }
        }

        private void DrawTagsAndCompatibility()
        {
            GUILayout.Label("Tags", EditorStyles.boldLabel);
            selectedDefinition.tags = DrawIdentityToggles(
                workspace.Authoring.tags,
                selectedDefinition.tags,
                "No item tags are defined in the catalog.");

            GUILayout.Space(5f);
            GUILayout.Label("Equipment Compatibility", EditorStyles.boldLabel);
            selectedDefinition.equipmentSlots = DrawIdentityToggles(
                workspace.Authoring.equipmentSlots,
                selectedDefinition.equipmentSlots,
                "No equipment slots are defined in the catalog.");
        }

        private void DrawEligibilityAndPolicies()
        {
            GUILayout.Label("Location Eligibility", EditorStyles.boldLabel);
            if (selectedDefinition.locationEligibility == null)
            {
                selectedDefinition.locationEligibility =
                    new ItemCatalogEditorLocationEligibility();
            }

            DrawToggle(
                "Secure Container",
                selectedDefinition.locationEligibility.secureContainer,
                value => selectedDefinition.locationEligibility.secureContainer = value);

            GUILayout.Space(5f);
            GUILayout.Label("Default Policies", EditorStyles.boldLabel);
            selectedDefinition.defaultPolicies = DrawStringToggle(
                "Protected On Death",
                ItemPolicyIds.ProtectedOnDeath,
                selectedDefinition.defaultPolicies);
            selectedDefinition.defaultPolicies = DrawStringToggle(
                "Insured",
                ItemPolicyIds.Insured,
                selectedDefinition.defaultPolicies);

            DrawToggle(
                "Player Destroyable",
                selectedDefinition.playerDestroyable,
                value => selectedDefinition.playerDestroyable = value);
        }

        private void DrawBagDefinition()
        {
            GUILayout.Label("Bag", EditorStyles.boldLabel);
            var hasBag = selectedDefinition.bag != null;
            var newHasBag = EditorGUILayout.ToggleLeft("Has Bag Definition", hasBag);
            if (newHasBag != hasBag)
            {
                selectedDefinition.bag = newHasBag
                    ? new ItemCatalogEditorBagDefinition
                    {
                        carryCapacityBonus = 0,
                        slots = Array.Empty<ItemCatalogEditorBagSlot>()
                    }
                    : null;
                MarkChanged("Bag definition changed.");
            }

            if (selectedDefinition.bag == null)
            {
                return;
            }

            var carryCapacityBonus = EditorGUILayout.LongField(
                "Carry Capacity Bonus",
                selectedDefinition.bag.carryCapacityBonus);
            if (carryCapacityBonus != selectedDefinition.bag.carryCapacityBonus)
            {
                selectedDefinition.bag.carryCapacityBonus = carryCapacityBonus;
                MarkChanged("Bag carry capacity changed.");
            }

            selectedDefinition.bag.slots = selectedDefinition.bag.slots
                ?? Array.Empty<ItemCatalogEditorBagSlot>();
            var slots = selectedDefinition.bag.slots
                .OrderBy(slot => slot.index)
                .ToList();
            var removeIndex = -1;
            for (var slotIndex = 0; slotIndex < slots.Count; slotIndex++)
            {
                var slot = slots[slotIndex];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Slot " + slot.index, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Remove", GUILayout.Width(70f)))
                {
                    removeIndex = slotIndex;
                }

                EditorGUILayout.EndHorizontal();
                var kindOptions = new[] { BagSlotKindIds.General, BagSlotKindIds.Specialized };
                var kindIndex = Math.Max(0, Array.IndexOf(kindOptions, slot.kind));
                var selectedKindIndex = EditorGUILayout.Popup("Kind", kindIndex, kindOptions);
                var selectedKind = kindOptions[selectedKindIndex];
                if (!string.Equals(selectedKind, slot.kind, StringComparison.Ordinal))
                {
                    slot.kind = selectedKind;
                    if (string.Equals(slot.kind, BagSlotKindIds.General, StringComparison.Ordinal))
                    {
                        slot.acceptedTags = Array.Empty<string>();
                    }

                    MarkChanged("Bag slot kind changed.");
                }

                if (string.Equals(slot.kind, BagSlotKindIds.Specialized, StringComparison.Ordinal))
                {
                    GUILayout.Label("Accepted Tags", EditorStyles.miniBoldLabel);
                    slot.acceptedTags = DrawIdentityToggles(
                        workspace.Authoring.tags,
                        slot.acceptedTags,
                        "No tags are available for specialized slots.");
                }
                else
                {
                    EditorGUILayout.LabelField("Accepted Tags", "All item tags");
                }

                EditorGUILayout.EndVertical();
            }

            if (removeIndex >= 0)
            {
                slots.RemoveAt(removeIndex);
                ReindexSlots(slots);
                selectedDefinition.bag.slots = slots.ToArray();
                MarkChanged("Bag slot removed.");
            }

            if (GUILayout.Button("Add Bag Slot"))
            {
                slots.Add(new ItemCatalogEditorBagSlot
                {
                    index = slots.Count,
                    kind = BagSlotKindIds.General,
                    acceptedTags = Array.Empty<string>()
                });
                selectedDefinition.bag.slots = slots.ToArray();
                MarkChanged("Bag slot added.");
            }
        }

        private void DrawPresentation()
        {
            GUILayout.Label("Client Presentation", EditorStyles.boldLabel);
            var presentation = workspace.FindPresentation(selectedDefinition.id);
            if (presentation == null)
            {
                EditorGUILayout.HelpBox(
                    "The presentation entry is missing. Reload to synchronize entries.",
                    MessageType.Error);
                return;
            }

            DrawTextField(
                "Localization Key",
                presentation.localizationKey,
                value => presentation.localizationKey = value);
            DrawTextField(
                "Fallback Display Name",
                presentation.fallbackDisplayName,
                value => presentation.fallbackDisplayName = value);
            DrawTextField(
                "Prefab Presentation Key",
                presentation.prefabPresentationKey,
                value => presentation.prefabPresentationKey = value);

            var currentIcon = ResolveIcon(presentation.iconResourcePath);
            var selectedIcon = (Sprite)EditorGUILayout.ObjectField(
                "Icon",
                currentIcon,
                typeof(Sprite),
                false);
            if (!ReferenceEquals(selectedIcon, currentIcon))
            {
                string resourcePath;
                string error;
                if (TryGetResourcePath(selectedIcon, out resourcePath, out error))
                {
                    presentation.iconResourcePath = resourcePath;
                    iconCache[resourcePath] = selectedIcon;
                    MarkChanged("Presentation icon changed.");
                }
                else
                {
                    SetStatus(error, MessageType.Error);
                }
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Resource Path");
            EditorGUILayout.SelectableLabel(
                presentation.iconResourcePath ?? string.Empty,
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(
                presentation.iconResourcePath));
            if (GUILayout.Button("Clear", GUILayout.Width(55f)))
            {
                presentation.iconResourcePath = string.Empty;
                MarkChanged("Presentation icon cleared.");
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(
                "Icons are client-owned assets under an Assets/Resources folder. "
                    + "Only the extension-free Resources path is baked into the presentation catalog.",
                MessageType.Info);
        }

        private void DrawActions()
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Validate", GUILayout.Height(28f)))
            {
                ValidateWorkspace();
            }

            if (GUILayout.Button("Save", GUILayout.Height(28f)))
            {
                SaveWorkspace(false);
            }

            if (GUILayout.Button("Save And Bake", GUILayout.Height(28f)))
            {
                SaveWorkspace(true);
            }

            EditorGUILayout.EndHorizontal();
            if (lastValidation != null && lastValidation.Success)
            {
                EditorGUILayout.LabelField(
                    "Current gameplay revision",
                    workspace.CurrentRuntime.revision);
                EditorGUILayout.LabelField(
                    "Current presentation revision",
                    workspace.Presentation.presentationRevision);
                EditorGUILayout.LabelField(
                    "Candidate gameplay revision",
                    lastValidation.CandidateRuntime.revision);
                EditorGUILayout.LabelField(
                    "Candidate presentation revision",
                    lastValidation.CandidatePresentation.presentationRevision);
            }

            if (!string.IsNullOrWhiteSpace(statusMessage))
            {
                statusScroll = EditorGUILayout.BeginScrollView(
                    statusScroll,
                    GUILayout.MinHeight(45f),
                    GUILayout.MaxHeight(150f));
                EditorGUILayout.HelpBox(statusMessage, statusType);
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.HelpBox(
                "Save writes the editable authoring and presentation files. "
                    + "Save And Bake also replaces the deterministic WorldData runtime catalog. "
                    + "Neither action writes item instances or persistence data.",
                MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private void LoadCatalog()
        {
            loadScheduled = false;
            try
            {
                workspace = service.Load();
                selectedDefinition = workspace.Authoring.definitions
                    .OrderBy(definition => definition.id, StringComparer.Ordinal)
                    .FirstOrDefault();
                lastValidation = null;
                iconCache.Clear();
                SetStatus(
                    "Loaded " + workspace.Authoring.definitions.Length
                        + " item definitions. Canonical authoring passed strict validation.",
                    MessageType.Info);
            }
            catch (Exception exception)
            {
                workspace = null;
                selectedDefinition = null;
                SetStatus(exception.Message, MessageType.Error);
            }

            Repaint();
        }

        private void ValidateWorkspace()
        {
            lastValidation = service.Validate(workspace);
            SetStatus(
                lastValidation.Message,
                lastValidation.Success ? MessageType.Info : MessageType.Error);
        }

        private void SaveWorkspace(bool bake)
        {
            ValidateWorkspace();
            if (lastValidation == null || !lastValidation.Success)
            {
                return;
            }

            if (lastValidation.RequiresStructuralConfirmation
                && !ConfirmStructuralChanges(lastValidation.Changes, bake))
            {
                SetStatus("The write was cancelled. No catalog files were changed.", MessageType.Warning);
                return;
            }

            try
            {
                if (bake)
                {
                    service.SaveAndBake(workspace, lastValidation);
                }
                else
                {
                    service.Save(workspace, lastValidation);
                }

                AssetDatabase.Refresh();
                var operation = bake ? "saved and baked" : "saved";
                SetStatus(
                    "Item catalog " + operation + " successfully. "
                        + lastValidation.Message,
                    MessageType.Info);
            }
            catch (Exception exception)
            {
                SetStatus("Item catalog write failed: " + exception.Message, MessageType.Error);
            }
        }

        private static bool ConfirmStructuralChanges(
            IEnumerable<ItemCatalogDefinitionChange> changes,
            bool bake)
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
                    + "\n\nAfter Phase 3 introduces persistent item state, structural changes "
                    + "require explicit migration review. Confirm that these changes are "
                    + "intentional before "
                    + action + ".",
                "Confirm " + action,
                "Cancel");
        }

        private bool MatchesSearch(ItemCatalogEditorDefinition definition)
        {
            if (!string.IsNullOrWhiteSpace(categoryFilter)
                && !string.Equals(
                    definition.category,
                    categoryFilter,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(searchText))
            {
                return true;
            }

            return ContainsIgnoreCase(definition.id, searchText)
                || ContainsIgnoreCase(definition.displayName, searchText)
                || ContainsIgnoreCase(definition.category, searchText);
        }

        private void DrawCategoryFilter()
        {
            var categories = workspace.Authoring.categories
                ?? Array.Empty<ItemCatalogEditorIdentity>();
            var values = new[] { string.Empty }
                .Concat(categories.Select(category => category.id))
                .ToArray();
            var labels = new[] { "All Categories" }
                .Concat(categories.Select(category => category.displayName))
                .ToArray();
            var selectedIndex = Math.Max(0, Array.IndexOf(values, categoryFilter));
            categoryFilter = values[EditorGUILayout.Popup(selectedIndex, labels)];
        }

        private void DrawIdentityPopup(
            string label,
            ItemCatalogEditorIdentity[] identities,
            string currentValue,
            Action<string> apply)
        {
            identities = identities ?? Array.Empty<ItemCatalogEditorIdentity>();
            if (identities.Length == 0)
            {
                EditorGUILayout.LabelField(label, "No values defined");
                return;
            }

            var options = identities
                .Select(identity => new GUIContent(identity.displayName + " (" + identity.id + ")"))
                .ToArray();
            var currentIndex = Array.FindIndex(identities, identity => string.Equals(
                identity.id,
                currentValue,
                StringComparison.Ordinal));
            currentIndex = Math.Max(0, currentIndex);
            var selectedIndex = EditorGUILayout.Popup(
                new GUIContent(label),
                currentIndex,
                options);
            if (!string.Equals(
                identities[selectedIndex].id,
                currentValue,
                StringComparison.Ordinal))
            {
                apply(identities[selectedIndex].id);
                MarkChanged(label + " changed.");
            }
        }

        private string[] DrawIdentityToggles(
            ItemCatalogEditorIdentity[] identities,
            string[] values,
            string emptyMessage)
        {
            identities = identities ?? Array.Empty<ItemCatalogEditorIdentity>();
            values = values ?? Array.Empty<string>();
            if (identities.Length == 0)
            {
                EditorGUILayout.LabelField(emptyMessage, EditorStyles.miniLabel);
                return values;
            }

            foreach (var identity in identities)
            {
                values = DrawStringToggle(
                    identity.displayName + " (" + identity.id + ")",
                    identity.id,
                    values);
            }

            return values;
        }

        private string[] DrawStringToggle(string label, string value, string[] values)
        {
            values = values ?? Array.Empty<string>();
            var enabled = values.Contains(value, StringComparer.Ordinal);
            var newEnabled = EditorGUILayout.ToggleLeft(label, enabled);
            if (newEnabled == enabled)
            {
                return values;
            }

            MarkChanged(label + " changed.");
            return newEnabled
                ? values.Concat(new[] { value }).Distinct(StringComparer.Ordinal).ToArray()
                : values.Where(candidate => !string.Equals(
                    candidate,
                    value,
                    StringComparison.Ordinal)).ToArray();
        }

        private void DrawToggle(string label, bool value, Action<bool> apply)
        {
            var newValue = EditorGUILayout.ToggleLeft(label, value);
            if (newValue != value)
            {
                apply(newValue);
                MarkChanged(label + " changed.");
            }
        }

        private void DrawTextField(string label, string value, Action<string> apply)
        {
            var newValue = EditorGUILayout.TextField(label, value ?? string.Empty);
            if (!string.Equals(newValue, value, StringComparison.Ordinal))
            {
                apply(newValue);
                MarkChanged(label + " changed.");
            }
        }

        private Sprite ResolveIcon(string resourcePath)
        {
            if (string.IsNullOrWhiteSpace(resourcePath))
            {
                return null;
            }

            Sprite icon;
            if (iconCache.TryGetValue(resourcePath, out icon))
            {
                return icon;
            }

            foreach (var guid in AssetDatabase.FindAssets("t:Sprite", new[] { "Assets/Resources" }))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Sprite>())
                {
                    string candidatePath;
                    string error;
                    if (TryGetResourcePath(asset, out candidatePath, out error)
                        && string.Equals(candidatePath, resourcePath, StringComparison.Ordinal))
                    {
                        iconCache[resourcePath] = asset;
                        return asset;
                    }
                }
            }

            iconCache[resourcePath] = null;
            return null;
        }

        private static bool TryGetResourcePath(
            Sprite icon,
            out string resourcePath,
            out string error)
        {
            resourcePath = string.Empty;
            error = string.Empty;
            if (icon == null)
            {
                return true;
            }

            var assetPath = AssetDatabase.GetAssetPath(icon).Replace('\\', '/');
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                error = "Item icons must be owned by the client Assets folder.";
                return false;
            }

            var spritesAtPath = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                .OfType<Sprite>()
                .ToArray();
            if (spritesAtPath.Length != 1)
            {
                error = "Item icons must use one Sprite per asset file.";
                return false;
            }

            var marker = "/Resources/";
            var markerIndex = assetPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                error = "Item icons must be stored below an Assets/Resources folder.";
                return false;
            }

            var relativePath = assetPath.Substring(markerIndex + marker.Length);
            resourcePath = Path.ChangeExtension(relativePath, null).Replace('\\', '/');
            return !string.IsNullOrWhiteSpace(resourcePath);
        }

        private static void ReindexSlots(IList<ItemCatalogEditorBagSlot> slots)
        {
            for (var index = 0; index < slots.Count; index++)
            {
                slots[index].index = index;
            }
        }

        private void MarkChanged(string message)
        {
            lastValidation = null;
            SetStatus(message, MessageType.Warning);
        }

        private void SetStatus(string message, MessageType type)
        {
            statusMessage = message ?? string.Empty;
            statusType = type;
            Repaint();
        }

        private static bool ContainsIgnoreCase(string value, string search)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ShortRevision(string revision)
        {
            return string.IsNullOrWhiteSpace(revision) || revision.Length <= 12
                ? revision ?? string.Empty
                : revision.Substring(0, 12);
        }
    }
}
