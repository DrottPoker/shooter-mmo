using UnityEditor;
using UnityEngine.UIElements;

namespace Mmorpg.WorldData.Editor.ContentTools
{
    // The stylesheet and class names that every content tool shares.
    public static class ContentToolStyles
    {
        public const string StyleSheetPath = "Packages/com.mmorpg.world-data/Editor/ContentTools/ContentTools.uss";

        public const string Root = "content-tool";
        public const string Heading = "content-heading";
        public const string Section = "content-section";
        public const string SectionTitle = "content-section__title";
        public const string Hint = "content-hint";
        public const string Row = "content-row";
        public const string FieldError = "content-field--error";
        public const string FieldWarning = "content-field--warning";
        public const string MarkerError = "content-marker--error";
        public const string MarkerWarning = "content-marker--warning";
        public const string MarkerChanged = "content-marker--changed";

        public static void Apply(VisualElement root)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet != null && !root.styleSheets.Contains(sheet))
            {
                root.styleSheets.Add(sheet);
            }

            root.AddToClassList(Root);
        }

        // A titled group of fields in a detail panel.
        public static VisualElement CreateSection(string title)
        {
            var section = new VisualElement();
            section.AddToClassList(Section);
            var label = new Label(title);
            label.AddToClassList(SectionTitle);
            section.Add(label);
            return section;
        }

        public static Label CreateHint(string text)
        {
            var hint = new Label(text);
            hint.AddToClassList(Hint);
            return hint;
        }
    }
}
