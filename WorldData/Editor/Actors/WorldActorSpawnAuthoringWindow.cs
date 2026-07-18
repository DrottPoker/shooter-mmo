using System;
using System.Linq;
using ShooterMmo.WorldData.Actors;
using UnityEditor;
using UnityEngine;

namespace ShooterMmo.WorldData.Editor.Actors
{
    public sealed class WorldActorSpawnAuthoringWindow : EditorWindow
    {
        public const string MenuPath = "Shooter MMO/Tools/Content/Spawn Authoring";

        private enum SpawnTab
        {
            Points,
            Groups,
            Areas,
            Patrols
        }

        private WorldActorEditorService service;
        private WorldActorEditorWorkspace workspace;
        private SpawnTab tab;
        private int selectedIndex = -1;
        private Vector2 scroll;
        private string search = string.Empty;
        private string status = string.Empty;
        private MessageType statusType = MessageType.Info;
        private bool showScenePreview = true;

        [MenuItem(MenuPath)]
        public static void Open()
        {
            GetWindow<WorldActorSpawnAuthoringWindow>("Spawn Authoring");
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGui;
            Load();
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGui;
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
            tab = (SpawnTab)GUILayout.Toolbar(
                (int)tab,
                new[] { "Points", "Groups", "Areas", "Patrol Paths" });
            ClampSelection();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            switch (tab)
            {
                case SpawnTab.Points:
                    DrawPoints();
                    break;
                case SpawnTab.Groups:
                    DrawGroups();
                    break;
                case SpawnTab.Areas:
                    DrawAreas();
                    break;
                case SpawnTab.Patrols:
                    DrawPatrols();
                    break;
            }

            DrawPrefabPreview();

            EditorGUILayout.EndScrollView();
            DrawSelectionActions();
            if (EditorGUI.EndChangeCheck())
            {
                ValidateInline();
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                EditorGUILayout.HelpBox(status, statusType);
            }

            DrawFileActions();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            search = GUILayout.TextField(
                search,
                GUI.skin.FindStyle("ToolbarSearchTextField"),
                GUILayout.MinWidth(180f));
            showScenePreview = GUILayout.Toggle(
                showScenePreview,
                "Scene Preview",
                EditorStyles.toolbarButton,
                GUILayout.Width(100f));
            if (GUILayout.Button("Import", EditorStyles.toolbarButton, GUILayout.Width(60f)))
            {
                Load();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawPoints()
        {
            var values = workspace.Spawns.spawnPoints
                ?? Array.Empty<WorldActorEditorSpawnPoint>();
            DrawSelectionList(values.Select(value => value.id).ToArray());
            if (selectedIndex < 0 || selectedIndex >= values.Length)
            {
                return;
            }

            var value = values[selectedIndex];
            value.id = EditorGUILayout.TextField("Stable spawn ID", value.id);
            value.actorId = ActorPopup(value.actorId);
            DrawActorProfileSelection(value.actorId);
            var position = EditorGUILayout.Vector3Field(
                "Position", new Vector3(value.x, value.y, value.z));
            value.x = position.x;
            value.y = position.y;
            value.z = position.z;
            value.yawDegrees = EditorGUILayout.FloatField("Yaw", value.yawDegrees);
            value.patrolPathId = PatrolPopup(value.patrolPathId);
        }

        private void DrawGroups()
        {
            var values = workspace.Spawns.spawnGroups
                ?? Array.Empty<WorldActorEditorSpawnGroup>();
            DrawSelectionList(values.Select(value => value.id).ToArray());
            if (selectedIndex < 0 || selectedIndex >= values.Length)
            {
                return;
            }

            var value = values[selectedIndex];
            value.id = EditorGUILayout.TextField("Stable group ID", value.id);
            value.actorId = ActorPopup(value.actorId);
            DrawActorProfileSelection(value.actorId);
            var center = EditorGUILayout.Vector3Field(
                "Center", new Vector3(value.centerX, value.centerY, value.centerZ));
            value.centerX = center.x;
            value.centerY = center.y;
            value.centerZ = center.z;
            value.yawDegrees = EditorGUILayout.FloatField("Yaw", value.yawDegrees);
            value.rows = EditorGUILayout.IntField("Rows", value.rows);
            value.columns = EditorGUILayout.IntField("Columns", value.columns);
            value.spacing = EditorGUILayout.FloatField("Spacing", value.spacing);
            EditorGUILayout.LabelField(
                "Initial population",
                Math.Max(0L, (long)value.rows * value.columns).ToString());
            value.patrolPathId = PatrolPopup(value.patrolPathId);
        }

        private void DrawAreas()
        {
            var values = workspace.Spawns.spawnAreas
                ?? Array.Empty<WorldActorEditorSpawnArea>();
            DrawSelectionList(values.Select(value => value.id).ToArray());
            if (selectedIndex < 0 || selectedIndex >= values.Length)
            {
                return;
            }

            var value = values[selectedIndex];
            value.id = EditorGUILayout.TextField("Stable area ID", value.id);
            value.actorId = ActorPopup(value.actorId);
            DrawActorProfileSelection(value.actorId);
            var minimum = EditorGUILayout.Vector2Field(
                "Minimum XZ", new Vector2(value.minimumX, value.minimumZ));
            var maximum = EditorGUILayout.Vector2Field(
                "Maximum XZ", new Vector2(value.maximumX, value.maximumZ));
            value.minimumX = minimum.x;
            value.minimumZ = minimum.y;
            value.maximumX = maximum.x;
            value.maximumZ = maximum.y;
            value.y = EditorGUILayout.FloatField("Y", value.y);
            value.yawDegrees = EditorGUILayout.FloatField("Yaw", value.yawDegrees);
            value.count = EditorGUILayout.IntField("Deterministic count", value.count);
            value.seed = EditorGUILayout.IntField("Deterministic seed", value.seed);
            value.patrolPathId = PatrolPopup(value.patrolPathId);
        }

        private void DrawPatrols()
        {
            var values = workspace.Spawns.patrolPaths
                ?? Array.Empty<WorldActorEditorPatrolPath>();
            DrawSelectionList(values.Select(value => value.id).ToArray());
            if (selectedIndex < 0 || selectedIndex >= values.Length)
            {
                return;
            }

            var value = values[selectedIndex];
            value.id = EditorGUILayout.TextField("Stable path ID", value.id);
            value.points ??= Array.Empty<WorldActorEditorPatrolPoint>();
            for (var index = 0; index < value.points.Length; index++)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                var point = value.points[index];
                var position = EditorGUILayout.Vector3Field(
                    "Point " + (index + 1),
                    new Vector3(point.x, point.y, point.z));
                point.x = position.x;
                point.y = position.y;
                point.z = position.z;
                point.waitSeconds = EditorGUILayout.FloatField(
                    "Wait seconds", point.waitSeconds);
                if (GUILayout.Button("Remove Point"))
                {
                    value.points = value.points
                        .Where((_, valueIndex) => valueIndex != index).ToArray();
                    EditorGUILayout.EndVertical();
                    break;
                }

                EditorGUILayout.EndVertical();
            }

            if (GUILayout.Button("Add Patrol Point"))
            {
                ArrayUtility.Add(
                    ref value.points,
                    new WorldActorEditorPatrolPoint { waitSeconds = 1f });
            }
        }

        private void DrawSelectionList(string[] labels)
        {
            EditorGUILayout.LabelField("Authoring Entries", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            for (var index = 0; index < labels.Length; index++)
            {
                if (!MatchesSearch(labels[index]))
                {
                    continue;
                }

                if (GUILayout.Toggle(
                        selectedIndex == index,
                        labels[index],
                        "Button",
                        GUILayout.MinWidth(120f)))
                {
                    selectedIndex = index;
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();
        }

        private void DrawSelectionActions()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add " + SingularTabName()))
            {
                AddSelectedType();
            }

            GUI.enabled = SelectedCount() > 0 && selectedIndex >= 0;
            if (GUILayout.Button("Duplicate Selected"))
            {
                DuplicateSelected();
            }

            if (GUILayout.Button("Ground Snap"))
            {
                GroundSnapSelected();
            }

            if (GUILayout.Button("Delete Selected"))
            {
                DeleteSelected();
            }

            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawFileActions()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Preview Changes"))
            {
                Validate(false);
            }

            if (GUILayout.Button("Export + Compile"))
            {
                Validate(true);
            }

            if (GUILayout.Button("Verify Canonical"))
            {
                var result = service.VerifyCanonical();
                status = result.Output;
                statusType = result.Success ? MessageType.Info : MessageType.Error;
            }

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
                    "Compile actor spawns",
                    string.Join("\n", validation.Changes)
                        + "\n\nWrite deterministic authoring and runtime files?",
                    "Write files",
                    "Cancel"))
            {
                return;
            }

            service.SaveAndCompile(workspace, validation);
            AssetDatabase.Refresh();
            status = "World actor spawn authoring and canonical runtime files were written.\n"
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
                selectedIndex = SelectedCount() > 0 ? 0 : -1;
                status = "Imported deterministic actor spawn authoring.";
                statusType = MessageType.Info;
                SceneView.RepaintAll();
            }
            catch (Exception exception)
            {
                workspace = null;
                status = exception.Message;
                statusType = MessageType.Error;
            }
        }

        private void OnSceneGui(SceneView sceneView)
        {
            if (!showScenePreview || workspace == null)
            {
                return;
            }

            DrawAllPreviews();
            if (tab == SpawnTab.Patrols)
            {
                DrawSelectedPatrolHandles();
                return;
            }

            EditorGUI.BeginChangeCheck();
            var position = SelectedPosition();
            if (position.HasValue)
            {
                var changed = Handles.PositionHandle(position.Value, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    SetSelectedPosition(changed);
                    Repaint();
                }
            }
            else
            {
                EditorGUI.EndChangeCheck();
            }
        }

        private void DrawSelectedPatrolHandles()
        {
            var patrols = workspace.Spawns.patrolPaths
                ?? Array.Empty<WorldActorEditorPatrolPath>();
            if (selectedIndex < 0 || selectedIndex >= patrols.Length)
            {
                return;
            }

            var points = patrols[selectedIndex].points
                ?? Array.Empty<WorldActorEditorPatrolPoint>();
            for (var index = 0; index < points.Length; index++)
            {
                var point = points[index];
                EditorGUI.BeginChangeCheck();
                var position = Handles.PositionHandle(
                    new Vector3(point.x, point.y, point.z),
                    Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    point.x = position.x;
                    point.y = position.y;
                    point.z = position.z;
                    Repaint();
                }
            }
        }

        private void DrawPrefabPreview()
        {
            var actorId = SelectedActorId();
            if (string.IsNullOrWhiteSpace(actorId))
            {
                return;
            }

            var actor = workspace.Catalog.actors?.FirstOrDefault(value =>
                string.Equals(value.id, actorId, StringComparison.Ordinal));
            if (actor == null)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                "Presentation Mapping Preview",
                EditorStyles.boldLabel);
            var prefab = ResolvePresentationPrefab(actor.presentationArchetypeId);
            if (prefab == null)
            {
                EditorGUILayout.HelpBox(
                    "No prefab mapping was found for presentation archetype '"
                        + actor.presentationArchetypeId
                        + "'. Runtime will use the explicit development fallback.",
                    MessageType.Warning);
                return;
            }

            var preview = AssetPreview.GetAssetPreview(prefab)
                ?? AssetPreview.GetMiniThumbnail(prefab);
            EditorGUILayout.ObjectField("Mapped prefab", prefab, typeof(GameObject), false);
            if (preview != null)
            {
                var rectangle = GUILayoutUtility.GetRect(128f, 128f, GUILayout.ExpandWidth(false));
                GUI.DrawTexture(rectangle, preview, ScaleMode.ScaleToFit, true);
            }
        }

        private static GameObject ResolvePresentationPrefab(string presentationArchetypeId)
        {
            const string canonicalPath =
                "Assets/Resources/ShooterMmo/WorldActors/WorldActorPresentationRegistry.asset";
            var registryPaths = new[] { canonicalPath }
                .Concat(AssetDatabase.FindAssets("t:WorldActorPresentationRegistry")
                    .Select(AssetDatabase.GUIDToAssetPath))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            for (var assetIndex = 0; assetIndex < registryPaths.Length; assetIndex++)
            {
                var registry = AssetDatabase.LoadAssetAtPath<ScriptableObject>(
                    registryPaths[assetIndex]);
                if (registry == null)
                {
                    continue;
                }

                var serialized = new SerializedObject(registry);
                var entries = serialized.FindProperty("entries");
                if (entries == null || !entries.isArray)
                {
                    continue;
                }

                for (var index = 0; index < entries.arraySize; index++)
                {
                    var entry = entries.GetArrayElementAtIndex(index);
                    var id = entry.FindPropertyRelative("presentationArchetypeId");
                    var prefab = entry.FindPropertyRelative("prefab");
                    if (id != null
                        && prefab != null
                        && string.Equals(
                            id.stringValue,
                            presentationArchetypeId,
                            StringComparison.Ordinal))
                    {
                        return prefab.objectReferenceValue as GameObject;
                    }
                }
            }

            return null;
        }

        private void DrawAllPreviews()
        {
            Handles.color = new Color(0.2f, 0.8f, 1f, 1f);
            foreach (var point in workspace.Spawns.spawnPoints
                         ?? Array.Empty<WorldActorEditorSpawnPoint>())
            {
                var position = new Vector3(point.x, point.y, point.z);
                Handles.DrawWireDisc(position, Vector3.up, 0.5f);
                Handles.Label(position + Vector3.up, point.id + "\n" + point.actorId);
            }

            Handles.color = new Color(0.2f, 1f, 0.4f, 1f);
            foreach (var group in workspace.Spawns.spawnGroups
                         ?? Array.Empty<WorldActorEditorSpawnGroup>())
            {
                for (var row = 0; row < Math.Max(1, group.rows); row++)
                {
                    for (var column = 0; column < Math.Max(1, group.columns); column++)
                    {
                        var offsetX = (column - ((Math.Max(1, group.columns) - 1) * 0.5f))
                            * group.spacing;
                        var offsetZ = (row - ((Math.Max(1, group.rows) - 1) * 0.5f))
                            * group.spacing;
                        Handles.DrawWireDisc(
                            new Vector3(
                                group.centerX + offsetX,
                                group.centerY,
                                group.centerZ + offsetZ),
                            Vector3.up,
                            0.4f);
                    }
                }

                Handles.Label(
                    new Vector3(group.centerX, group.centerY + 1f, group.centerZ),
                    group.id + "\n" + group.actorId);
            }

            Handles.color = new Color(1f, 0.7f, 0.15f, 1f);
            foreach (var area in workspace.Spawns.spawnAreas
                         ?? Array.Empty<WorldActorEditorSpawnArea>())
            {
                var center = new Vector3(
                    (area.minimumX + area.maximumX) * 0.5f,
                    area.y,
                    (area.minimumZ + area.maximumZ) * 0.5f);
                var size = new Vector3(
                    area.maximumX - area.minimumX,
                    0.1f,
                    area.maximumZ - area.minimumZ);
                Handles.DrawWireCube(center, size);
                Handles.Label(center + Vector3.up, area.id + " x" + area.count);
            }

            Handles.color = Color.magenta;
            foreach (var path in workspace.Spawns.patrolPaths
                         ?? Array.Empty<WorldActorEditorPatrolPath>())
            {
                var points = path.points ?? Array.Empty<WorldActorEditorPatrolPoint>();
                for (var index = 0; index < points.Length; index++)
                {
                    var current = new Vector3(points[index].x, points[index].y, points[index].z);
                    Handles.SphereHandleCap(0, current, Quaternion.identity, 0.25f,
                        EventType.Repaint);
                    if (index > 0)
                    {
                        var previous = new Vector3(
                            points[index - 1].x,
                            points[index - 1].y,
                            points[index - 1].z);
                        Handles.DrawLine(previous, current);
                    }
                }
            }
        }

        private void AddSelectedType()
        {
            switch (tab)
            {
                case SpawnTab.Points:
                    var points = workspace.Spawns.spawnPoints
                        ?? Array.Empty<WorldActorEditorSpawnPoint>();
                    ArrayUtility.Add(ref points, new WorldActorEditorSpawnPoint
                    {
                        id = CreateUniqueSpawnId(
                            "local.new_point_" + (points.Length + 1)),
                        actorId = FirstActorId(),
                        patrolPathId = string.Empty
                    });
                    workspace.Spawns.spawnPoints = points;
                    selectedIndex = points.Length - 1;
                    break;
                case SpawnTab.Groups:
                    var groups = workspace.Spawns.spawnGroups
                        ?? Array.Empty<WorldActorEditorSpawnGroup>();
                    ArrayUtility.Add(ref groups, new WorldActorEditorSpawnGroup
                    {
                        id = CreateUniqueSpawnId(
                            "local.new_group_" + (groups.Length + 1)),
                        actorId = FirstActorId(),
                        patrolPathId = string.Empty
                    });
                    workspace.Spawns.spawnGroups = groups;
                    selectedIndex = groups.Length - 1;
                    break;
                case SpawnTab.Areas:
                    var areas = workspace.Spawns.spawnAreas
                        ?? Array.Empty<WorldActorEditorSpawnArea>();
                    ArrayUtility.Add(ref areas, new WorldActorEditorSpawnArea
                    {
                        id = CreateUniqueSpawnId(
                            "local.new_area_" + (areas.Length + 1)),
                        actorId = FirstActorId(),
                        maximumX = 4f,
                        maximumZ = 4f,
                        patrolPathId = string.Empty
                    });
                    workspace.Spawns.spawnAreas = areas;
                    selectedIndex = areas.Length - 1;
                    break;
                case SpawnTab.Patrols:
                    var patrols = workspace.Spawns.patrolPaths
                        ?? Array.Empty<WorldActorEditorPatrolPath>();
                    ArrayUtility.Add(ref patrols, new WorldActorEditorPatrolPath
                    {
                        id = CreateUniqueSpawnId(
                            "local.new_patrol_" + (patrols.Length + 1)),
                        points = new[]
                        {
                            new WorldActorEditorPatrolPoint(),
                            new WorldActorEditorPatrolPoint { x = 2f }
                        }
                    });
                    workspace.Spawns.patrolPaths = patrols;
                    selectedIndex = patrols.Length - 1;
                    break;
            }

            SceneView.RepaintAll();
        }

        private void DuplicateSelected()
        {
            switch (tab)
            {
                case SpawnTab.Points:
                    var points = workspace.Spawns.spawnPoints;
                    var point = WorldActorEditorJson.Clone(points[selectedIndex]);
                    point.id = CreateUniqueSpawnId(point.id + ".copy");
                    point.x += 1f;
                    ArrayUtility.Add(ref points, point);
                    workspace.Spawns.spawnPoints = points;
                    selectedIndex = points.Length - 1;
                    break;
                case SpawnTab.Groups:
                    var groups = workspace.Spawns.spawnGroups;
                    var group = WorldActorEditorJson.Clone(groups[selectedIndex]);
                    group.id = CreateUniqueSpawnId(group.id + ".copy");
                    group.centerX += 1f;
                    ArrayUtility.Add(ref groups, group);
                    workspace.Spawns.spawnGroups = groups;
                    selectedIndex = groups.Length - 1;
                    break;
                case SpawnTab.Areas:
                    var areas = workspace.Spawns.spawnAreas;
                    var area = WorldActorEditorJson.Clone(areas[selectedIndex]);
                    area.id = CreateUniqueSpawnId(area.id + ".copy");
                    area.minimumX += 1f;
                    area.maximumX += 1f;
                    ArrayUtility.Add(ref areas, area);
                    workspace.Spawns.spawnAreas = areas;
                    selectedIndex = areas.Length - 1;
                    break;
                case SpawnTab.Patrols:
                    var patrols = workspace.Spawns.patrolPaths;
                    var patrol = WorldActorEditorJson.Clone(patrols[selectedIndex]);
                    patrol.id = CreateUniqueSpawnId(patrol.id + ".copy");
                    ArrayUtility.Add(ref patrols, patrol);
                    workspace.Spawns.patrolPaths = patrols;
                    selectedIndex = patrols.Length - 1;
                    break;
            }

            SceneView.RepaintAll();
        }

        private void DeleteSelected()
        {
            switch (tab)
            {
                case SpawnTab.Points:
                    workspace.Spawns.spawnPoints = workspace.Spawns.spawnPoints
                        .Where((_, index) => index != selectedIndex).ToArray();
                    break;
                case SpawnTab.Groups:
                    workspace.Spawns.spawnGroups = workspace.Spawns.spawnGroups
                        .Where((_, index) => index != selectedIndex).ToArray();
                    break;
                case SpawnTab.Areas:
                    workspace.Spawns.spawnAreas = workspace.Spawns.spawnAreas
                        .Where((_, index) => index != selectedIndex).ToArray();
                    break;
                case SpawnTab.Patrols:
                    workspace.Spawns.patrolPaths = workspace.Spawns.patrolPaths
                        .Where((_, index) => index != selectedIndex).ToArray();
                    break;
            }

            ClampSelection();
            SceneView.RepaintAll();
        }

        private void GroundSnapSelected()
        {
            var position = SelectedPosition();
            if (!position.HasValue)
            {
                status = "Select an authoring entry before ground snapping.";
                statusType = MessageType.Warning;
                return;
            }

            var origin = position.Value + (Vector3.up * 1000f);
            if (!Physics.Raycast(origin, Vector3.down, out var hit, 2000f,
                    Physics.AllLayers, QueryTriggerInteraction.Ignore))
            {
                status = "Ground snap found no collider below the selected entry.";
                statusType = MessageType.Warning;
                return;
            }

            SetSelectedPosition(new Vector3(position.Value.x, hit.point.y, position.Value.z));
            status = "Selected authoring entry snapped to " + hit.collider.name + ".";
            statusType = MessageType.Info;
        }

        private Vector3? SelectedPosition()
        {
            if (selectedIndex < 0 || selectedIndex >= SelectedCount())
            {
                return null;
            }

            switch (tab)
            {
                case SpawnTab.Points:
                    var point = workspace.Spawns.spawnPoints[selectedIndex];
                    return new Vector3(point.x, point.y, point.z);
                case SpawnTab.Groups:
                    var group = workspace.Spawns.spawnGroups[selectedIndex];
                    return new Vector3(group.centerX, group.centerY, group.centerZ);
                case SpawnTab.Areas:
                    var area = workspace.Spawns.spawnAreas[selectedIndex];
                    return new Vector3(
                        (area.minimumX + area.maximumX) * 0.5f,
                        area.y,
                        (area.minimumZ + area.maximumZ) * 0.5f);
                default:
                    return null;
            }
        }

        private void SetSelectedPosition(Vector3 position)
        {
            switch (tab)
            {
                case SpawnTab.Points:
                    var point = workspace.Spawns.spawnPoints[selectedIndex];
                    point.x = position.x;
                    point.y = position.y;
                    point.z = position.z;
                    break;
                case SpawnTab.Groups:
                    var group = workspace.Spawns.spawnGroups[selectedIndex];
                    group.centerX = position.x;
                    group.centerY = position.y;
                    group.centerZ = position.z;
                    break;
                case SpawnTab.Areas:
                    var area = workspace.Spawns.spawnAreas[selectedIndex];
                    var halfX = (area.maximumX - area.minimumX) * 0.5f;
                    var halfZ = (area.maximumZ - area.minimumZ) * 0.5f;
                    area.minimumX = position.x - halfX;
                    area.maximumX = position.x + halfX;
                    area.minimumZ = position.z - halfZ;
                    area.maximumZ = position.z + halfZ;
                    area.y = position.y;
                    break;
            }

            SceneView.RepaintAll();
        }

        private string ActorPopup(string current)
        {
            return PopupReference(
                "Actor definition",
                current,
                workspace.Catalog.actors?.Select(value => value.id).ToArray());
        }

        private void DrawActorProfileSelection(string actorId)
        {
            var actor = workspace.Catalog.actors?.FirstOrDefault(value =>
                string.Equals(value.id, actorId, StringComparison.Ordinal));
            if (actor == null || actor.kind != WorldActorKindIds.Mob)
            {
                return;
            }

            EditorGUILayout.LabelField("Mob Lifecycle", EditorStyles.boldLabel);
            actor.activityProfileId = PopupReference(
                "Activation profile",
                actor.activityProfileId,
                workspace.Catalog.activityProfiles?.Select(value => value.id).ToArray());
            actor.respawnProfileId = PopupReference(
                "Respawn profile",
                actor.respawnProfileId,
                workspace.Catalog.respawnProfiles?.Select(value => value.id).ToArray());
            var respawn = workspace.Catalog.respawnProfiles?.FirstOrDefault(value =>
                string.Equals(value.id, actor.respawnProfileId, StringComparison.Ordinal));
            if (respawn != null)
            {
                respawn.populationLimit = EditorGUILayout.IntField(
                    "Population limit",
                    respawn.populationLimit);
                respawn.delaySeconds = EditorGUILayout.FloatField(
                    "Respawn delay seconds",
                    respawn.delaySeconds);
            }
        }

        private string SelectedActorId()
        {
            if (selectedIndex < 0 || selectedIndex >= SelectedCount())
            {
                return string.Empty;
            }

            switch (tab)
            {
                case SpawnTab.Points:
                    return workspace.Spawns.spawnPoints[selectedIndex].actorId;
                case SpawnTab.Groups:
                    return workspace.Spawns.spawnGroups[selectedIndex].actorId;
                case SpawnTab.Areas:
                    return workspace.Spawns.spawnAreas[selectedIndex].actorId;
                default:
                    return string.Empty;
            }
        }

        private string PatrolPopup(string current)
        {
            return PopupReference(
                "Patrol path",
                current,
                workspace.Spawns.patrolPaths?.Select(value => value.id).ToArray(),
                true);
        }

        private static string PopupReference(
            string label,
            string current,
            string[] values,
            bool allowEmpty = false)
        {
            var options = (values ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (allowEmpty)
            {
                options.Insert(0, string.Empty);
            }

            if (options.Count == 0)
            {
                return EditorGUILayout.TextField(label, current);
            }

            var index = Math.Max(0, options.IndexOf(current));
            return options[EditorGUILayout.Popup(label, index, options.ToArray())];
        }

        private int SelectedCount()
        {
            if (workspace == null)
            {
                return 0;
            }

            switch (tab)
            {
                case SpawnTab.Points:
                    return workspace.Spawns.spawnPoints?.Length ?? 0;
                case SpawnTab.Groups:
                    return workspace.Spawns.spawnGroups?.Length ?? 0;
                case SpawnTab.Areas:
                    return workspace.Spawns.spawnAreas?.Length ?? 0;
                default:
                    return workspace.Spawns.patrolPaths?.Length ?? 0;
            }
        }

        private void ClampSelection()
        {
            var count = SelectedCount();
            selectedIndex = count == 0 ? -1 : Mathf.Clamp(selectedIndex, 0, count - 1);
        }

        private bool MatchesSearch(string value)
        {
            return string.IsNullOrWhiteSpace(search)
                || (value ?? string.Empty).IndexOf(
                    search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string FirstActorId()
        {
            return workspace.Catalog.actors?.FirstOrDefault()?.id ?? string.Empty;
        }

        private string CreateUniqueSpawnId(string candidate)
        {
            var existing = new System.Collections.Generic.HashSet<string>(
                StringComparer.Ordinal);
            existing.UnionWith((workspace.Spawns.spawnPoints
                    ?? Array.Empty<WorldActorEditorSpawnPoint>())
                .Select(value => value.id));
            existing.UnionWith((workspace.Spawns.spawnGroups
                    ?? Array.Empty<WorldActorEditorSpawnGroup>())
                .Select(value => value.id));
            existing.UnionWith((workspace.Spawns.spawnAreas
                    ?? Array.Empty<WorldActorEditorSpawnArea>())
                .Select(value => value.id));
            existing.UnionWith((workspace.Spawns.patrolPaths
                    ?? Array.Empty<WorldActorEditorPatrolPath>())
                .Select(value => value.id));
            var value = candidate;
            var suffix = 2;
            while (existing.Contains(value))
            {
                value = candidate + "_" + suffix;
                suffix++;
            }

            return value;
        }

        private string SingularTabName()
        {
            switch (tab)
            {
                case SpawnTab.Points:
                    return "Point";
                case SpawnTab.Groups:
                    return "Group";
                case SpawnTab.Areas:
                    return "Area";
                default:
                    return "Patrol Path";
            }
        }
    }
}
