using Mmorpg.WorldData.Editor.ContentTools;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.WorldData.Editor.Actors
{
    // Hosts the Actor Studio tool. The undo state is serialized with the window, so unsaved
    // edits and their undo history survive script reloads; closing the window asks to save.
    public sealed class WorldActorStudioWindow : EditorWindow
    {
        public const string MenuPath = "MMORPG/Tools/Content/Actor Studio";

        [SerializeField]
        private ContentUndoState undoState;

        private WorldActorStudioView view;

        [MenuItem(MenuPath)]
        public static void Open()
        {
            var window = GetWindow<WorldActorStudioWindow>();
            window.titleContent = new GUIContent("Actor Studio");
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
                undoState = ContentUndoState.Create("Actor Studio Edits");
            }

            saveChangesMessage = "Actor Studio has unsaved changes. Save and compile them before closing?";
            view = new WorldActorStudioView(
                new WorldActorEditorService(WorldActorEditorPaths.CreateDefault()),
                undoState);
            view.UnsavedChangesChanged += () => hasUnsavedChanges = view != null && view.HasUnsavedChanges;
            rootVisualElement.Add(view);

            // The window paints before the content loads.
            rootVisualElement.schedule.Execute(() => view?.Load(true));
        }

        private void OnDisable()
        {
            view?.Dispose();
            view = null;
        }

        private void OnDestroy()
        {
            if (undoState)
            {
                Undo.ClearUndo(undoState);
                DestroyImmediate(undoState);
            }
        }
    }
}
