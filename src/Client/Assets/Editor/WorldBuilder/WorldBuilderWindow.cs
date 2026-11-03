using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Worlds;
using Mmorpg.WorldData.Editor.ContentTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Mmorpg.Client.Editor
{
    // The World Builder window: adds lakes, rivers, seas, and waterfalls to the open scene, lists
    // the scene's shapes, shows the problems the World Builder finds, and rebuilds, validates, and
    // bakes. The shapes are shaped with their handles in the Scene view and their values in the
    // Inspector, and the water follows every change. See docs/DEVELOPMENT_WORLDS.md under World
    // Builder.
    public sealed class WorldBuilderWindow : EditorWindow
    {
        public const string MenuPath = "MMORPG/Tools/World Builder";

        private readonly List<WorldBuilderShape> shapes = new List<WorldBuilderShape>();
        private readonly List<WorldBuilderIssue> issues = new List<WorldBuilderIssue>();
        private Label sceneLabel;
        private Button waterfallButton;
        private Button bakeButton;
        private Button removeUnusedButton;
        private ListView shapeList;
        private ListView issueList;
        private Label issueSummary;
        private bool refreshScheduled;

        [MenuItem(MenuPath)]
        public static void Open()
        {
            var window = GetWindow<WorldBuilderWindow>();
            window.titleContent = new GUIContent("World Builder");
            window.minSize = new Vector2(360f, 480f);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += ScheduleRefresh;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            Selection.selectionChanged += UpdateButtons;
            WorldBuilderShape.Changed += OnShapeChanged;
            Undo.undoRedoPerformed += ScheduleRefresh;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= ScheduleRefresh;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            Selection.selectionChanged -= UpdateButtons;
            WorldBuilderShape.Changed -= OnShapeChanged;
            Undo.undoRedoPerformed -= ScheduleRefresh;
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            ContentToolStyles.Apply(root);
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1f;
            root.Add(scroll);

            var heading = new Label("World Builder");
            heading.AddToClassList(ContentToolStyles.Heading);
            scroll.Add(heading);
            sceneLabel = ContentToolStyles.CreateHint(string.Empty);
            scroll.Add(sceneLabel);
            scroll.Add(ContentToolStyles.CreateHint(
                "Add water below, then shape it with the handles in the Scene view and its values in the "
                + "Inspector. Lakes and seas lie at the height of their object; rivers fall from point to "
                + "point. Where water meets, the World Builder makes it agree, and the water follows every change."));

            var add = ContentToolStyles.CreateSection("Add");
            var addRow = Row();
            addRow.Add(new Button(() => Add(WorldBuilderCreate.AddLake)) { text = "Lake" });
            addRow.Add(new Button(() => Add(WorldBuilderCreate.AddRiver)) { text = "River" });
            addRow.Add(new Button(() => Add(WorldBuilderCreate.AddSea)) { text = "Sea" });
            add.Add(addRow);
            waterfallButton = new Button(AddWaterfall) { text = "Waterfall from the selected river" };
            add.Add(waterfallButton);
            add.Add(ContentToolStyles.CreateHint(
                "New water appears at the middle of the Scene view, at that height. A waterfall falls from the "
                + "last point of the selected river into the water below it."));
            scroll.Add(add);

            var water = ContentToolStyles.CreateSection("Shapes");
            shapeList = new ListView
            {
                selectionType = SelectionType.Single,
                makeItem = () => new Label(),
                bindItem = (element, index) => ((Label)element).text = Describe(shapes[index]),
                itemsSource = shapes,
                fixedItemHeight = 20f
            };
            shapeList.style.minHeight = 80f;
            shapeList.selectionChanged += selected => Select(selected.OfType<WorldBuilderShape>().FirstOrDefault());
            water.Add(shapeList);
            scroll.Add(water);

            var problems = ContentToolStyles.CreateSection("Problems");
            issueSummary = new Label();
            problems.Add(issueSummary);
            issueList = new ListView
            {
                selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
                makeItem = () =>
                {
                    var label = new Label();
                    label.style.whiteSpace = WhiteSpace.Normal;
                    return label;
                },
                bindItem = (element, index) =>
                {
                    var issue = issues[index];
                    var label = (Label)element;
                    label.text = (issue.Severity == WorldBuilderIssueSeverity.Error ? "Error: " : "Warning: ") + issue;
                    label.EnableInClassList(ContentToolStyles.FieldError, issue.Severity == WorldBuilderIssueSeverity.Error);
                    label.EnableInClassList(ContentToolStyles.FieldWarning, issue.Severity == WorldBuilderIssueSeverity.Warning);
                },
                itemsSource = issues
            };
            issueList.selectionChanged += selected =>
            {
                if (selected.OfType<WorldBuilderIssue>().FirstOrDefault() is WorldBuilderIssue issue && issue.Target != null)
                {
                    Select(issue.Target);
                }
            };
            problems.Add(issueList);
            removeUnusedButton = new Button(RemoveUnusedMeshes) { text = "Remove Unused Meshes" };
            problems.Add(removeUnusedButton);
            scroll.Add(problems);

            var actions = Row();
            actions.Add(new Button(Rebuild) { text = "Rebuild" });
            actions.Add(new Button(Refresh) { text = "Validate" });
            bakeButton = new Button(Bake) { text = "Bake Collision" };
            actions.Add(bakeButton);
            scroll.Add(actions);
            scroll.Add(ContentToolStyles.CreateHint(
                "Bake Collision bakes the open World scene, as World Collision > Bake Open Scene does, and stops at "
                + "any error above."));
            Refresh();
        }

        private static VisualElement Row()
        {
            var row = new VisualElement();
            row.AddToClassList(ContentToolStyles.Row);
            row.style.flexDirection = FlexDirection.Row;
            return row;
        }

        private static string Describe(WorldBuilderShape shape)
        {
            if (shape == null)
            {
                return string.Empty;
            }

            var kind = shape is LakeShape ? "Lake"
                : shape is RiverShape ? "River"
                : shape is SeaShape ? "Sea"
                : shape is WaterfallShape ? "Waterfall"
                : shape.GetType().Name;
            return shape is WaterSurfaceShape surface
                ? shape.name + "  (" + kind + ", " + surface.Body.BodyId + ")"
                : shape.name + "  (" + kind + ")";
        }

        private void Add(System.Func<Scene, Vector3, WaterSurfaceShape> add)
        {
            var view = SceneView.lastActiveSceneView;
            add(SceneManager.GetActiveScene(), view != null ? view.pivot : Vector3.zero);
            Refresh();
        }

        private void AddWaterfall()
        {
            if (Selection.activeGameObject != null && Selection.activeGameObject.TryGetComponent<RiverShape>(out var river))
            {
                WorldBuilderCreate.AddWaterfall(river);
                WorldBuilder.Rebuild(river.gameObject.scene);
                Refresh();
            }
        }

        private void Rebuild()
        {
            WorldBuilder.Rebuild(SceneManager.GetActiveScene());
            Refresh();
        }

        private void Bake()
        {
            WorldCollisionBaker.BakeOpenScene();
            Refresh();
        }

        private void RemoveUnusedMeshes()
        {
            var count = issues.Count(issue => issue.Target is Mesh);
            if (EditorUtility.DisplayDialog(
                    "Remove Unused Meshes",
                    "Move the " + count + (count == 1 ? " mesh" : " meshes") + " that no shape uses any more to the trash? "
                    + "They can be restored from there.",
                    "Move To Trash",
                    "Cancel"))
            {
                WorldBuilder.RemoveUnusedMeshes(SceneManager.GetActiveScene());
                Refresh();
            }
        }

        private void OnActiveSceneChanged(Scene previous, Scene next)
        {
            ScheduleRefresh();
        }

        private void OnShapeChanged(WorldBuilderShape shape)
        {
            ScheduleRefresh();
        }

        // Refreshes once after a burst of changes, such as while a handle is dragged.
        private void ScheduleRefresh()
        {
            if (refreshScheduled || rootVisualElement == null)
            {
                return;
            }

            refreshScheduled = true;
            rootVisualElement.schedule.Execute(() =>
            {
                refreshScheduled = false;
                Refresh();
            }).StartingIn(150);
        }

        private void Refresh()
        {
            if (shapeList == null)
            {
                return;
            }

            var scene = SceneManager.GetActiveScene();
            sceneLabel.text = "Scene: " + (string.IsNullOrEmpty(scene.path) ? scene.name + " (not saved)" : scene.path);
            shapes.Clear();
            shapes.AddRange(WorldBuilder.Shapes<WorldBuilderShape>(scene));
            shapeList.RefreshItems();
            issues.Clear();
            issues.AddRange(WorldBuilder.Validate(scene).OrderBy(issue => issue.Severity));
            var errors = issues.Count(issue => issue.Severity == WorldBuilderIssueSeverity.Error);
            issueSummary.text = issues.Count == 0
                ? "No problems found."
                : errors + (errors == 1 ? " error" : " errors") + ", " + (issues.Count - errors)
                    + (issues.Count - errors == 1 ? " warning" : " warnings") + ". Choose one to select its object.";
            issueList.style.display = issues.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            issueList.RefreshItems();
            removeUnusedButton.style.display = issues.Any(issue => issue.Target is Mesh) ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            if (waterfallButton == null)
            {
                return;
            }

            waterfallButton.SetEnabled(
                Selection.activeGameObject != null && Selection.activeGameObject.GetComponent<RiverShape>() != null);
            bakeButton.SetEnabled(Object.FindObjectsByType<WorldCollisionAuthoring>(FindObjectsInactive.Include).Length == 1);
        }

        private static void Select(Object target)
        {
            if (target == null)
            {
                return;
            }

            Selection.activeObject = target is Component component ? component.gameObject : target;
            EditorGUIUtility.PingObject(Selection.activeObject);
            if (SceneView.lastActiveSceneView != null && Selection.activeGameObject != null)
            {
                SceneView.lastActiveSceneView.FrameSelected();
            }
        }
    }
}
