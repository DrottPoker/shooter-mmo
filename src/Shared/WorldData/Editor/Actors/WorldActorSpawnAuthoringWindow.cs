using Mmorpg.WorldData.Editor.ContentTools;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.WorldData.Editor.Actors
{
    // Hosts the Spawn Authoring tool and draws its scene handles. The undo state is serialized
    // with the window, so unsaved edits and their undo history survive script reloads;
    // closing the window asks to save.
    public sealed class WorldActorSpawnAuthoringWindow : EditorWindow
    {
        public const string MenuPath = "MMORPG/Tools/Content/Spawn Authoring";

        [SerializeField]
        private ContentUndoState undoState;

        private WorldActorSpawnAuthoringView view;

        [MenuItem(MenuPath)]
        public static void Open()
        {
            var window = GetWindow<WorldActorSpawnAuthoringWindow>();
            window.titleContent = new GUIContent("Spawn Authoring");
            window.minSize = new Vector2(900f, 600f);
            window.Show();
        }

        public override void SaveChanges()
        {
            if (view != null && view.Save())
            {
                base.SaveChanges();
            }
        }

        private void CreateGUI()
        {
            if (!undoState)
            {
                undoState = ContentUndoState.Create("Spawn Authoring Edits");
            }

            saveChangesMessage = "Spawn Authoring has unsaved changes. Save and compile them before closing?";
            view = new WorldActorSpawnAuthoringView(
                new WorldActorEditorService(WorldActorEditorPaths.CreateDefault()),
                undoState);
            view.UnsavedChangesChanged += () => hasUnsavedChanges = view != null && view.HasUnsavedChanges;
            rootVisualElement.Add(view);

            // The window paints before the content loads.
            rootVisualElement.schedule.Execute(() => view?.Load(true));
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGui;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGui;
            view?.Dispose();
            view = null;
            SceneView.RepaintAll();
        }

        private void OnDestroy()
        {
            if (undoState)
            {
                Undo.ClearUndo(undoState);
                DestroyImmediate(undoState);
            }
        }

        private void OnSceneGui(SceneView sceneView)
        {
            view?.DrawSceneGui();
        }
    }
}
