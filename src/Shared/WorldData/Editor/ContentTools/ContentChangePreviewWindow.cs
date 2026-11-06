using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Mmorpg.WorldData.Editor.ContentTools
{
    // One canonical file that a successful validation would write.
    public readonly struct ContentChangePreviewFile
    {
        public ContentChangePreviewFile(string displayPath, string text, bool changed)
        {
            DisplayPath = displayPath ?? string.Empty;
            Text = text ?? string.Empty;
            Changed = changed;
        }

        public string DisplayPath { get; }
        public string Text { get; }
        public bool Changed { get; }
    }

    // Shows the canonical files a successful validation would write: the changes, the list of
    // files with the changed ones marked, and the text of the selected file.
    public sealed class ContentChangePreviewWindow : EditorWindow
    {
        private ContentChangePreviewFile[] files = Array.Empty<ContentChangePreviewFile>();
        private string summary = string.Empty;
        private HelpBox summaryBox;
        private ListView fileList;
        private TextField content;

        // The summary lists the changes and then the files that a save deletes.
        public static void ShowPreview(
            string title,
            IEnumerable<string> changes,
            IEnumerable<string> removedFiles,
            IEnumerable<ContentChangePreviewFile> files)
        {
            // GetWindow names only a new window, and the open one may show another tool's files.
            var window = GetWindow<ContentChangePreviewWindow>(true, title);
            window.titleContent = new GUIContent(title);
            window.minSize = new Vector2(720f, 480f);
            window.files = (files ?? Array.Empty<ContentChangePreviewFile>()).ToArray();
            var lines = (changes ?? Array.Empty<string>())
                .Concat((removedFiles ?? Array.Empty<string>()).Select(path => "Deletes " + path))
                .ToArray();
            window.summary = lines.Length == 0 ? "No canonical changes detected." : string.Join("\n", lines);
            window.Refresh();
            window.Show();
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
