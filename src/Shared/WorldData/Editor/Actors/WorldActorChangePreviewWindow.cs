using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.WorldData.Editor.Actors
{
    public sealed class WorldActorChangePreviewWindow : EditorWindow
    {
        private WorldActorEditorFile[] files = Array.Empty<WorldActorEditorFile>();
        private string[] fileLabels = Array.Empty<string>();
        private string summary = string.Empty;
        private int selectedFile;
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
            window.files = validation.Files;
            window.fileLabels = validation.Files
                .Select(file => file.DisplayPath + (file.Changed ? " (changed)" : string.Empty))
                .ToArray();
            window.summary = BuildSummary(validation);
            window.selectedFile = Math.Max(0, Array.FindIndex(validation.Files, file => file.Changed));
            window.minSize = new Vector2(720f, 480f);
            window.Show();
        }

        private static string BuildSummary(WorldActorEditorValidationResult validation)
        {
            var lines = validation.Changes.ToList();
            lines.AddRange(validation.RemovedFiles.Select(path => "Deletes " + path));
            return lines.Count == 0 ? "No canonical changes detected." : string.Join("\n", lines);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(summary, MessageType.Info);
            if (files == null || files.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "Validate again to preview the canonical files.",
                    MessageType.None);
                return;
            }

            selectedFile = EditorGUILayout.Popup(
                "File",
                Mathf.Clamp(selectedFile, 0, files.Length - 1),
                fileLabels);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.TextArea(
                files[selectedFile].Text,
                EditorStyles.textArea,
                GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }
    }
}
