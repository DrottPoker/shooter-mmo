using System;
using System.Linq;
using ShooterMmo.WorldData.Actors;
using UnityEditor;
using UnityEngine;

namespace ShooterMmo.WorldData.Editor.Actors
{
    public sealed class WorldActorStudioWindow : EditorWindow
    {
        public const string MenuPath = "Shooter MMO/Tools/Content/Actor Studio";

        private static readonly string[] ActorKinds =
        {
            WorldActorKindIds.Npc,
            WorldActorKindIds.Mob
        };

        private static readonly string[] Dispositions =
        {
            WorldActorDispositionIds.Friendly,
            WorldActorDispositionIds.Neutral,
            WorldActorDispositionIds.Hostile
        };

        private static readonly string[] CapabilityKinds =
        {
            WorldActorCapabilityKindIds.Dialogue,
            WorldActorCapabilityKindIds.Vendor,
            WorldActorCapabilityKindIds.QuestOffer,
            WorldActorCapabilityKindIds.QuestTurnIn,
            WorldActorCapabilityKindIds.Crafting,
            WorldActorCapabilityKindIds.Insurance,
            WorldActorCapabilityKindIds.Trainer,
            WorldActorCapabilityKindIds.Bank,
            WorldActorCapabilityKindIds.RecoveryStorage
        };

        private WorldActorEditorService service;
        private WorldActorEditorWorkspace workspace;
        private Vector2 listScroll;
        private Vector2 detailScroll;
        private string search = string.Empty;
        private int kindFilter;
        private string factionFilter = string.Empty;
        private string capabilityFilter = string.Empty;
        private string tagFilter = string.Empty;
        private int selectedIndex = -1;
        private string status = string.Empty;
        private MessageType statusType = MessageType.Info;

        [MenuItem(MenuPath)]
        public static void Open()
        {
            GetWindow<WorldActorStudioWindow>("Actor Studio");
        }

        private void OnEnable()
        {
            Load();
        }

        private void OnGUI()
        {
            DrawToolbar();
            if (workspace == null)
            {
                EditorGUILayout.HelpBox(status, MessageType.Error);
                return;
            }

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            DrawActorList();
            DrawActorDetails();
            EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck())
            {
                ValidateInline();
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                EditorGUILayout.HelpBox(status, statusType);
            }

            DrawActions();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            search = GUILayout.TextField(search, GUI.skin.FindStyle("ToolbarSearchTextField"),
                GUILayout.MinWidth(180f));
            kindFilter = EditorGUILayout.Popup(
                kindFilter,
                new[] { "All actors", "NPC", "Mob" },
                EditorStyles.toolbarPopup,
                GUILayout.Width(100f));
            factionFilter = ToolbarReferenceFilter(
                factionFilter,
                workspace?.Catalog.factions?.Select(value => value.id).ToArray(),
                "All factions");
            capabilityFilter = ToolbarReferenceFilter(
                capabilityFilter,
                CapabilityKinds,
                "All capabilities");
            tagFilter = ToolbarReferenceFilter(
                tagFilter,
                workspace?.Catalog.actors?
                    .SelectMany(actor => actor.tags ?? Array.Empty<string>())
                    .ToArray(),
                "All tags");
            if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(64f)))
            {
                Load();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawActorList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(260f));
            EditorGUILayout.LabelField("Definitions", EditorStyles.boldLabel);
            listScroll = EditorGUILayout.BeginScrollView(listScroll);
            var actors = workspace.Catalog.actors
                ?? Array.Empty<WorldActorEditorDefinition>();
            for (var index = 0; index < actors.Length; index++)
            {
                var actor = actors[index];
                if (!MatchesFilter(actor))
                {
                    continue;
                }

                var label = actor.displayName + "\n" + actor.id;
                if (GUILayout.Toggle(selectedIndex == index, label, "Button",
                        GUILayout.Height(42f)))
                {
                    selectedIndex = index;
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("New NPC"))
            {
                AddTemplate(WorldActorKindIds.Npc);
            }

            if (GUILayout.Button("New Mob"))
            {
                AddTemplate(WorldActorKindIds.Mob);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = SelectedActor != null;
            if (GUILayout.Button("Duplicate Selected"))
            {
                DuplicateSelected();
            }

            if (GUILayout.Button("Delete Selected"))
            {
                DeleteSelected();
            }

            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawActorDetails()
        {
            EditorGUILayout.BeginVertical();
            detailScroll = EditorGUILayout.BeginScrollView(detailScroll);
            var actor = SelectedActor;
            if (actor == null)
            {
                EditorGUILayout.HelpBox(
                    "Select an actor definition or create one from a template.",
                    MessageType.Info);
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.LabelField("Actor Definition", EditorStyles.boldLabel);
            actor.id = EditorGUILayout.TextField("Stable actor ID", actor.id);
            actor.displayName = EditorGUILayout.TextField("Display name", actor.displayName);
            actor.kind = PopupValue("Kind", actor.kind, ActorKinds);
            actor.factionId = PopupReference(
                "Faction",
                actor.factionId,
                workspace.Catalog.factions?.Select(value => value.id).ToArray());
            actor.disposition = PopupValue("Disposition", actor.disposition, Dispositions);
            actor.presentationArchetypeId = PopupReference(
                "Presentation",
                actor.presentationArchetypeId,
                workspace.Catalog.presentationArchetypes
                    ?.Where(value => value.actorKind == actor.kind)
                    .Select(value => value.id).ToArray());
            DrawPresentationMapping(actor.presentationArchetypeId);
            actor.tags ??= Array.Empty<string>();
            var tags = EditorGUILayout.TextField(
                "Content tags",
                string.Join(", ", actor.tags));
            actor.tags = tags.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            EditorGUILayout.HelpBox(
                "The presentation archetype is neutral WorldData. Its replaceable prefab mapping remains a Unity presentation asset.",
                MessageType.None);
            if (actor.kind == WorldActorKindIds.Npc)
            {
                actor.damagePolicy = WorldActorDamagePolicyIds.Invulnerable;
                EditorGUILayout.LabelField("Damage policy", "Invulnerable (required for NPCs)");
                actor.activityProfileId = string.Empty;
                actor.respawnProfileId = string.Empty;
            }
            else
            {
                actor.damagePolicy = PopupValue(
                    "Damage policy",
                    actor.damagePolicy,
                    new[]
                    {
                        WorldActorDamagePolicyIds.Damageable,
                        WorldActorDamagePolicyIds.Invulnerable
                    });
                actor.activityProfileId = PopupReference(
                    "Activity profile",
                    actor.activityProfileId,
                    workspace.Catalog.activityProfiles?.Select(value => value.id).ToArray());
                actor.respawnProfileId = PopupReference(
                    "Respawn profile",
                    actor.respawnProfileId,
                    workspace.Catalog.respawnProfiles?.Select(value => value.id).ToArray());
            }

            DrawBounds(actor);
            DrawCapabilities(actor);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private static void DrawBounds(WorldActorEditorDefinition actor)
        {
            actor.interactionBounds ??= new WorldActorEditorBounds();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Authoritative Interaction Bounds", EditorStyles.boldLabel);
            var center = EditorGUILayout.Vector3Field(
                "Local center",
                new Vector3(
                    actor.interactionBounds.centerX,
                    actor.interactionBounds.centerY,
                    actor.interactionBounds.centerZ));
            var size = EditorGUILayout.Vector3Field(
                "Size",
                new Vector3(
                    actor.interactionBounds.sizeX,
                    actor.interactionBounds.sizeY,
                    actor.interactionBounds.sizeZ));
            actor.interactionBounds.centerX = center.x;
            actor.interactionBounds.centerY = center.y;
            actor.interactionBounds.centerZ = center.z;
            actor.interactionBounds.sizeX = size.x;
            actor.interactionBounds.sizeY = size.y;
            actor.interactionBounds.sizeZ = size.z;
            var preview = GUILayoutUtility.GetRect(220f, 100f, GUILayout.ExpandWidth(false));
            EditorGUI.DrawRect(preview, new Color(0.10f, 0.12f, 0.15f, 1f));
            var width = Mathf.Clamp(size.x * 32f, 4f, preview.width - 12f);
            var height = Mathf.Clamp(size.y * 32f, 4f, preview.height - 12f);
            var boundsRectangle = new Rect(
                preview.center.x - (width * 0.5f),
                preview.center.y - (height * 0.5f),
                width,
                height);
            EditorGUI.DrawRect(boundsRectangle, new Color(0.2f, 0.7f, 1f, 0.35f));
        }

        private static void DrawCapabilities(WorldActorEditorDefinition actor)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Composable Capabilities", EditorStyles.boldLabel);
            actor.capabilities ??= Array.Empty<WorldActorEditorCapability>();
            for (var index = 0; index < actor.capabilities.Length; index++)
            {
                var capability = actor.capabilities[index];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                capability.id = EditorGUILayout.TextField("Stable capability ID", capability.id);
                capability.kind = PopupValue("Kind", capability.kind, CapabilityKinds);
                capability.displayName = EditorGUILayout.TextField(
                    "Display name", capability.displayName);
                EditorGUILayout.BeginHorizontal();
                GUI.enabled = index > 0;
                if (GUILayout.Button("Move Up"))
                {
                    var previous = actor.capabilities[index - 1];
                    actor.capabilities[index - 1] = capability;
                    actor.capabilities[index] = previous;
                }

                GUI.enabled = index < actor.capabilities.Length - 1;
                if (GUILayout.Button("Move Down"))
                {
                    var next = actor.capabilities[index + 1];
                    actor.capabilities[index + 1] = capability;
                    actor.capabilities[index] = next;
                }

                GUI.enabled = true;
                if (GUILayout.Button("Remove Capability"))
                {
                    actor.capabilities = actor.capabilities
                        .Where((_, valueIndex) => valueIndex != index).ToArray();
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    break;
                }

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.EndVertical();
            }

            if (actor.capabilities.Length < 16 && GUILayout.Button("Add Capability"))
            {
                var next = actor.capabilities.Length + 1;
                ArrayUtility.Add(
                    ref actor.capabilities,
                    new WorldActorEditorCapability
                    {
                        id = actor.id + ".capability_" + next,
                        kind = WorldActorCapabilityKindIds.Dialogue,
                        displayName = "Talk"
                    });
            }
        }

        private static void DrawPresentationMapping(string presentationArchetypeId)
        {
            EditorGUILayout.LabelField("Unity Presentation Mapping", EditorStyles.boldLabel);
            var registry = FindPresentationRegistry();
            if (registry == null)
            {
                EditorGUILayout.HelpBox(
                    "No runtime presentation registry asset exists at the canonical Resources path.",
                    MessageType.Warning);
                if (GUILayout.Button("Create Presentation Registry"))
                {
                    registry = CreatePresentationRegistry();
                }
            }

            if (registry == null)
            {
                return;
            }

            var serialized = new SerializedObject(registry);
            var entries = serialized.FindProperty("entries");
            if (entries == null || !entries.isArray)
            {
                EditorGUILayout.HelpBox(
                    "The presentation registry asset has an incompatible schema.",
                    MessageType.Error);
                return;
            }

            var entryIndex = -1;
            GameObject currentPrefab = null;
            for (var index = 0; index < entries.arraySize; index++)
            {
                var entry = entries.GetArrayElementAtIndex(index);
                var id = entry.FindPropertyRelative("presentationArchetypeId");
                if (id != null
                    && string.Equals(
                        id.stringValue,
                        presentationArchetypeId,
                        StringComparison.Ordinal))
                {
                    entryIndex = index;
                    currentPrefab = entry.FindPropertyRelative("prefab")
                        ?.objectReferenceValue as GameObject;
                    break;
                }
            }

            EditorGUI.BeginChangeCheck();
            var selectedPrefab = (GameObject)EditorGUILayout.ObjectField(
                "Replaceable prefab",
                currentPrefab,
                typeof(GameObject),
                false);
            if (!EditorGUI.EndChangeCheck())
            {
                return;
            }

            if (entryIndex < 0)
            {
                entryIndex = entries.arraySize;
                entries.InsertArrayElementAtIndex(entryIndex);
            }

            var selectedEntry = entries.GetArrayElementAtIndex(entryIndex);
            selectedEntry.FindPropertyRelative("presentationArchetypeId").stringValue =
                presentationArchetypeId;
            selectedEntry.FindPropertyRelative("prefab").objectReferenceValue =
                selectedPrefab;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(registry);
        }

        private static ScriptableObject FindPresentationRegistry()
        {
            const string registryPath =
                "Assets/Resources/ShooterMmo/WorldActors/WorldActorPresentationRegistry.asset";
            return AssetDatabase.LoadAssetAtPath<ScriptableObject>(registryPath);
        }

        private static ScriptableObject CreatePresentationRegistry()
        {
            var scriptGuid = AssetDatabase.FindAssets(
                    "WorldActorPresentationRegistry t:MonoScript")
                .FirstOrDefault();
            var script = string.IsNullOrEmpty(scriptGuid)
                ? null
                : AssetDatabase.LoadAssetAtPath<MonoScript>(
                    AssetDatabase.GUIDToAssetPath(scriptGuid));
            var registryType = script?.GetClass();
            if (registryType == null
                || !typeof(ScriptableObject).IsAssignableFrom(registryType))
            {
                Debug.LogError(
                    "WorldActorPresentationRegistry runtime type could not be resolved.");
                return null;
            }

            EnsureAssetFolder("Assets/Resources", "ShooterMmo");
            EnsureAssetFolder("Assets/Resources/ShooterMmo", "WorldActors");
            var registry = ScriptableObject.CreateInstance(registryType);
            AssetDatabase.CreateAsset(
                registry,
                "Assets/Resources/ShooterMmo/WorldActors/WorldActorPresentationRegistry.asset");
            AssetDatabase.SaveAssets();
            Selection.activeObject = registry;
            return registry;
        }

        private static void EnsureAssetFolder(string parent, string child)
        {
            var path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }

        private void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = workspace != null;
            if (GUILayout.Button("Preview Changes"))
            {
                Validate(false);
            }

            if (GUILayout.Button("Save + Compile"))
            {
                Validate(true);
            }

            if (GUILayout.Button("Verify Canonical"))
            {
                var result = service.VerifyCanonical();
                status = result.Output;
                statusType = result.Success ? MessageType.Info : MessageType.Error;
            }

            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        private void Validate(bool save)
        {
            var validation = service.Validate(workspace);
            status = validation.Message;
            statusType = validation.Success ? MessageType.Info : MessageType.Error;
            if (!validation.Success)
            {
                return;
            }

            if (!save)
            {
                WorldActorChangePreviewWindow.ShowPreview(validation);
                return;
            }

            if (validation.Changes.Length > 0
                && !EditorUtility.DisplayDialog(
                    "Compile world actors",
                    string.Join("\n", validation.Changes)
                        + "\n\nWrite authoring and canonical runtime files?",
                    "Write files",
                    "Cancel"))
            {
                return;
            }

            service.SaveAndCompile(workspace, validation);
            AssetDatabase.Refresh();
            status = "World actor authoring and canonical runtime files were written.\n"
                + validation.Message;
        }

        private void ValidateInline()
        {
            try
            {
                var runtime = WorldActorCompiler.Compile(
                    workspace.Catalog.ToDomain(),
                    workspace.Spawns.ToDomain());
                status = "Inline validation passed. Candidate revision "
                    + runtime.Revision + ".";
                statusType = MessageType.Info;
            }
            catch (Exception exception)
            {
                status = exception.Message;
                statusType = MessageType.Error;
            }
        }

        private void Load()
        {
            try
            {
                service = new WorldActorEditorService(WorldActorEditorPaths.CreateDefault());
                workspace = service.Load();
                selectedIndex = workspace.Catalog.actors?.Length > 0 ? 0 : -1;
                status = "Loaded deterministic world actor content.";
                statusType = MessageType.Info;
            }
            catch (Exception exception)
            {
                workspace = null;
                status = exception.Message;
                statusType = MessageType.Error;
            }
        }

        private void AddTemplate(string kind)
        {
            var actors = workspace.Catalog.actors
                ?? Array.Empty<WorldActorEditorDefinition>();
            var index = actors.Length + 1;
            var npc = kind == WorldActorKindIds.Npc;
            var actor = new WorldActorEditorDefinition
            {
                id = CreateUniqueActorId(
                    (npc ? "npc.new_actor_" : "mob.new_actor_") + index),
                displayName = npc ? "New NPC" : "New Mob",
                kind = kind,
                factionId = workspace.Catalog.factions?.FirstOrDefault()?.id ?? string.Empty,
                disposition = npc
                    ? WorldActorDispositionIds.Friendly
                    : WorldActorDispositionIds.Hostile,
                presentationArchetypeId = workspace.Catalog.presentationArchetypes
                    ?.FirstOrDefault(value => value.actorKind == kind)?.id ?? string.Empty,
                damagePolicy = npc
                    ? WorldActorDamagePolicyIds.Invulnerable
                    : WorldActorDamagePolicyIds.Damageable,
                interactionBounds = new WorldActorEditorBounds(),
                capabilities = Array.Empty<WorldActorEditorCapability>(),
                tags = npc ? new[] { "city", "service" } : new[] { "wild" },
                activityProfileId = npc
                    ? string.Empty
                    : workspace.Catalog.activityProfiles?.FirstOrDefault()?.id ?? string.Empty,
                respawnProfileId = npc
                    ? string.Empty
                    : workspace.Catalog.respawnProfiles?.FirstOrDefault()?.id ?? string.Empty
            };
            ArrayUtility.Add(ref actors, actor);
            workspace.Catalog.actors = actors;
            selectedIndex = actors.Length - 1;
        }

        private void DuplicateSelected()
        {
            var actors = workspace.Catalog.actors;
            var copy = WorldActorEditorJson.Clone(SelectedActor);
            copy.id = CreateUniqueActorId(copy.id + ".copy");
            copy.displayName += " Copy";
            ArrayUtility.Add(ref actors, copy);
            workspace.Catalog.actors = actors;
            selectedIndex = actors.Length - 1;
        }

        private void DeleteSelected()
        {
            var selected = SelectedActor;
            if (selected == null)
            {
                return;
            }

            var spawnReferenceCount = (workspace.Spawns.spawnPoints
                    ?? Array.Empty<WorldActorEditorSpawnPoint>())
                .Count(value => string.Equals(
                    value.actorId,
                    selected.id,
                    StringComparison.Ordinal))
                + (workspace.Spawns.spawnGroups
                    ?? Array.Empty<WorldActorEditorSpawnGroup>())
                .Count(value => string.Equals(
                    value.actorId,
                    selected.id,
                    StringComparison.Ordinal))
                + (workspace.Spawns.spawnAreas
                    ?? Array.Empty<WorldActorEditorSpawnArea>())
                .Count(value => string.Equals(
                    value.actorId,
                    selected.id,
                    StringComparison.Ordinal));
            if (spawnReferenceCount > 0)
            {
                status = "Actor '" + selected.id + "' is referenced by "
                    + spawnReferenceCount
                    + " spawn entries. Remove or reassign those references first.";
                statusType = MessageType.Warning;
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "Delete actor definition",
                    "Delete actor '" + selected.id
                        + "' from the current authoring workspace?",
                    "Delete",
                    "Cancel"))
            {
                return;
            }

            workspace.Catalog.actors = workspace.Catalog.actors
                .Where((_, index) => index != selectedIndex)
                .ToArray();
            selectedIndex = Math.Min(
                selectedIndex,
                workspace.Catalog.actors.Length - 1);
            ValidateInline();
        }

        private string CreateUniqueActorId(string candidate)
        {
            var existing = new System.Collections.Generic.HashSet<string>(
                (workspace.Catalog.actors ?? Array.Empty<WorldActorEditorDefinition>())
                    .Select(value => value.id),
                StringComparer.Ordinal);
            var value = candidate;
            var suffix = 2;
            while (existing.Contains(value))
            {
                value = candidate + "_" + suffix;
                suffix++;
            }

            return value;
        }

        private bool MatchesFilter(WorldActorEditorDefinition actor)
        {
            if (kindFilter > 0 && actor.kind != ActorKinds[kindFilter - 1])
            {
                return false;
            }

            if (!string.IsNullOrEmpty(factionFilter)
                && !string.Equals(actor.factionId, factionFilter, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(capabilityFilter)
                && !(actor.capabilities ?? Array.Empty<WorldActorEditorCapability>())
                    .Any(value => string.Equals(
                        value.kind,
                        capabilityFilter,
                        StringComparison.Ordinal)))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(tagFilter)
                && !(actor.tags ?? Array.Empty<string>()).Contains(
                    tagFilter,
                    StringComparer.Ordinal))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(search)
                || (actor.id ?? string.Empty).IndexOf(
                    search, StringComparison.OrdinalIgnoreCase) >= 0
                || (actor.displayName ?? string.Empty).IndexOf(
                    search, StringComparison.OrdinalIgnoreCase) >= 0
                || (actor.factionId ?? string.Empty).IndexOf(
                    search, StringComparison.OrdinalIgnoreCase) >= 0
                || (actor.tags ?? Array.Empty<string>()).Any(value =>
                    value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                || (actor.capabilities ?? Array.Empty<WorldActorEditorCapability>()).Any(value =>
                    (value.kind ?? string.Empty).IndexOf(
                        search, StringComparison.OrdinalIgnoreCase) >= 0
                    || (value.displayName ?? string.Empty).IndexOf(
                        search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private WorldActorEditorDefinition SelectedActor
        {
            get
            {
                var actors = workspace?.Catalog.actors;
                return actors != null && selectedIndex >= 0 && selectedIndex < actors.Length
                    ? actors[selectedIndex]
                    : null;
            }
        }

        private static string PopupReference(string label, string value, string[] values)
        {
            var options = (values ?? Array.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .Prepend(string.Empty)
                .ToArray();
            return PopupValue(label, value, options);
        }

        private static string PopupValue(string label, string value, string[] values)
        {
            var options = values ?? Array.Empty<string>();
            if (options.Length == 0)
            {
                return EditorGUILayout.TextField(label, value);
            }

            var index = Math.Max(0, Array.IndexOf(options, value));
            return options[EditorGUILayout.Popup(label, index, options)];
        }

        private static string ToolbarReferenceFilter(
            string current,
            string[] values,
            string allLabel)
        {
            var references = (values ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var labels = new[] { allLabel }.Concat(references).ToArray();
            var index = string.IsNullOrEmpty(current)
                ? 0
                : Math.Max(0, Array.IndexOf(references, current) + 1);
            var selected = EditorGUILayout.Popup(
                index,
                labels,
                EditorStyles.toolbarPopup,
                GUILayout.Width(130f));
            return selected == 0 ? string.Empty : references[selected - 1];
        }
    }
}
