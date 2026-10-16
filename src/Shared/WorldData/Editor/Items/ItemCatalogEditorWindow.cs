using Mmorpg.WorldData.Editor.ContentTools;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.WorldData.Editor.Items
{
    // Hosts the Item Catalog tool. The undo state is serialized with the window, so unsaved
    // edits and their undo history survive script reloads; closing the window asks to save.
    public sealed class ItemCatalogEditorWindow : EditorWindow
    {
        public const string MenuPath = "MMORPG/Tools/Item Catalog";

        [SerializeField]
        private ContentUndoState undoState;

        private ItemCatalogEditorView view;

        [MenuItem(MenuPath)]
        public static void Open()
        {
            var window = GetWindow<ItemCatalogEditorWindow>();
            window.titleContent = new GUIContent("Item Catalog");
            window.minSize = new Vector2(900f, 600f);
            window.Show();
        }

        public override void SaveChanges()
        {
            if (view != null && view.Save(false))
            {
                base.SaveChanges();
            }
        }

        private void CreateGUI()
        {
            if (!undoState)
            {
                undoState = ContentUndoState.Create("Item Catalog Edits");
            }

            saveChangesMessage = "The item catalog has unsaved changes. Save them before closing? "
                + "Saving does not bake the runtime catalog.";
            view = new ItemCatalogEditorView(
                new ItemCatalogEditorService(ItemCatalogEditorPaths.CreateDefault()),
                undoState);
            view.UnsavedChangesChanged += () => hasUnsavedChanges = view != null && view.HasUnsavedChanges;
            rootVisualElement.Add(view);

            // Loading runs the content compiler, so the window paints before it starts.
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
