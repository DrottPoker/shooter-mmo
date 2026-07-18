using System;
using UnityEditor;
using UnityEngine;

namespace ShooterMmo.WorldData.Editor.Actors
{
    public sealed class WorldActorChangePreviewWindow : EditorWindow
    {
        private string catalogJson = string.Empty;
        private string spawnJson = string.Empty;
        private string runtimeJson = string.Empty;
        private string changes = string.Empty;
        private int selectedDocument;
        private Vector2 scroll;

        public static void ShowPreview(WorldActorEditorValidationResult validation)
        {
            if (validation == null || !validation.Success)
            {
                throw new ArgumentException(
                    "A successful validation is required for change preview.",
                    nameof(validation));
            }

            var window = GetWindow<WorldActorChangePreviewWindow>(
                true,
                "World Actor Canonical Change Preview");
            window.catalogJson = validation.CatalogJson;
            window.spawnJson = validation.SpawnJson;
            window.runtimeJson = validation.RuntimeJson;
            window.changes = validation.Changes.Length == 0
                ? "No canonical changes detected."
                : string.Join("\n", validation.Changes);
            window.minSize = new Vector2(720f, 480f);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(changes, MessageType.Info);
            selectedDocument = GUILayout.Toolbar(
                selectedDocument,
                new[] { "Actor Source", "Spawn Source", "Compiled Runtime" });
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.TextArea(
                SelectedJson(),
                EditorStyles.textArea,
                GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private string SelectedJson()
        {
            switch (selectedDocument)
            {
                case 0:
                    return catalogJson;
                case 1:
                    return spawnJson;
                default:
                    return runtimeJson;
            }
        }
    }
}
