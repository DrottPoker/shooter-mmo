using System;
using System.Linq;
using Mmorpg.WorldData.Editor.ContentTools;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Mmorpg.WorldData.Editor.Actors
{
    // Shows the canonical files a successful validation would write: the changes, the list of
    // files with the changed ones marked, and the text of the selected file.
    public sealed class WorldActorChangePreviewWindow : EditorWindow
    {
        private WorldActorEditorFile[] files = Array.Empty<WorldActorEditorFile>();
        private string summary = string.Empty;
        private HelpBox summaryBox;
        private ListView fileList;
        private TextField content;

        public static void ShowPreview(WorldActorEditorValidationResult validation)
        {
            if (validation == null || !validation.Success)
            {
                throw new ArgumentException(
                    "A successful validation is required for change preview.",
                    nameof(validation));
            }

            var window = GetWindow<WorldActorChangePreviewWindow>(true, "World Actor Canonical Change Preview");
            window.minSize = new Vector2(720f, 480f);
            window.files = validation.Files;
            window.summary = BuildSummary(validation);
            window.Refresh();
            window.Show();
        }

        private static string BuildSummary(WorldActorEditorValidationResult validation)
        {
            var lines = validation.Changes.ToList();
            lines.AddRange(validation.RemovedFiles.Select(path => "Deletes " + path));
            return lines.Count == 0 ? "No canonical changes detected." : string.Join("\n", lines);
        }

        private void CreateGUI()
        {
            ContentToolStyles.Apply(rootVisualElement);
            summaryBox = new HelpBox(string.Empty, HelpBoxMessageType.Info);
            var body = new TwoPaneSplitView(0, 300f, TwoPaneSplitViewOrientation.Horizontal);
            body.style.flexGrow = 1f;
            fileList = new ListView
            {
                selectionType = SelectionType.Single,
                fixedItemHeight = 20f,
                makeItem = () => new Label(),
                bindItem = (element, index) =>
                {
                    var file = files[index];
                    var label = (Label)element;
                    label.text = file.DisplayPath + (file.Changed ? " (changed)" : string.Empty);
                    label.tooltip = label.text;
                    label.style.unityFontStyleAndWeight = file.Changed ? FontStyle.Bold : FontStyle.Normal;
                    label.style.paddingLeft = 4f;
                }
            };
            fileList.selectionChanged += _ => ShowSelectedFile();
            content = new TextField { multiline = true, isReadOnly = true };
            content.style.flexGrow = 1f;
            content.verticalScrollerVisibility = ScrollerVisibility.Auto;
            body.Add(fileList);
            body.Add(content);
            rootVisualElement.Add(summaryBox);
            rootVisualElement.Add(body);
            Refresh();
        }

        // The window may open before its user interface exists, so both paths refresh.
        private void Refresh()
        {
            if (summaryBox == null)
            {
                return;
            }

            summaryBox.text = files.Length == 0 ? "Validate again to preview the canonical files." : summary;
            fileList.itemsSource = files;
            fileList.RefreshItems();
            if (files.Length > 0)
            {
                fileList.SetSelection(Math.Max(0, Array.FindIndex(files, file => file.Changed)));
            }

            ShowSelectedFile();
        }

        private void ShowSelectedFile()
        {
            var index = fileList.selectedIndex;
            content.SetValueWithoutNotify(index >= 0 && index < files.Length ? files[index].Text : string.Empty);
        }
    }
}
