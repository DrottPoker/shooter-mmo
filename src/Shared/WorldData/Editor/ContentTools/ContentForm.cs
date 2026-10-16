using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Mmorpg.WorldData.Editor.ContentTools
{
    // Fields that edit every selected entry at once. A field shows the value the selection
    // shares, or a mixed state when the entries differ, and an edit applies to every entry
    // it is valid for as one undoable step. Fields refresh in place after edits, undo, and
    // redo, and show the validation findings of the fields they edit.
    public sealed class ContentForm<T>
        where T : class
    {
        private readonly IContentEditor editor;
        private readonly Func<T, string> key;
        private readonly List<Action> refreshers = new List<Action>();
        private readonly List<FieldEntry> fields = new List<FieldEntry>();
        private IReadOnlyList<T> targets = Array.Empty<T>();

        public ContentForm(IContentEditor editor, Func<T, string> key)
        {
            this.editor = editor ?? throw new ArgumentNullException(nameof(editor));
            this.key = key ?? throw new ArgumentNullException(nameof(key));
        }

        public IReadOnlyList<T> Targets => targets;

        public IContentEditor Editor => editor;

        public void SetTargets(IReadOnlyList<T> value)
        {
            targets = value ?? Array.Empty<T>();
            Refresh();
        }

        public void Refresh()
        {
            foreach (var refresh in refreshers)
            {
                refresh();
            }
        }

        // Forgets every field, before the panel that holds them is rebuilt.
        public void Clear()
        {
            refreshers.Clear();
            fields.Clear();
        }

        // Runs after every refresh, for labels and visibility that follow the content.
        public void Watch(Action refresh)
        {
            refreshers.Add(refresh ?? throw new ArgumentNullException(nameof(refresh)));
        }

        public TextField Text(
            string label,
            string field,
            Func<T, string> get,
            Action<T, string> set,
            Func<T, bool> editable = null)
        {
            var control = new TextField { isDelayed = true };
            return Bind(control, label, field, entry => get(entry) ?? string.Empty, set, editable);
        }

        public Toggle Toggle(
            string label,
            string field,
            Func<T, bool> get,
            Action<T, bool> set,
            Func<T, bool> editable = null)
        {
            return Bind(new Toggle(), label, field, get, set, editable);
        }

        public IntegerField Integer(
            string label,
            string field,
            Func<T, int> get,
            Action<T, int> set,
            Func<T, bool> editable = null)
        {
            return Bind(new IntegerField { isDelayed = true }, label, field, get, set, editable);
        }

        public LongField Long(
            string label,
            string field,
            Func<T, long> get,
            Action<T, long> set,
            Func<T, bool> editable = null)
        {
            return Bind(new LongField { isDelayed = true }, label, field, get, set, editable);
        }

        public FloatField Float(
            string label,
            string field,
            Func<T, float> get,
            Action<T, float> set,
            Func<T, bool> editable = null)
        {
            return Bind(new FloatField { isDelayed = true }, label, field, get, set, editable);
        }

        public Vector3Field Vector3(
            string label,
            string field,
            Func<T, Vector3> get,
            Action<T, Vector3> set,
            Func<T, bool> editable = null)
        {
            return Bind(new Vector3Field(), label, field, get, set, editable);
        }

        public Vector2Field Vector2(
            string label,
            string field,
            Func<T, Vector2> get,
            Action<T, Vector2> set,
            Func<T, bool> editable = null)
        {
            return Bind(new Vector2Field(), label, field, get, set, editable);
        }

        // A dropdown over values with display labels. A value that is not among the options,
        // such as a reference to a removed entry, stays visible and marked as unknown.
        public DropdownField Choice(
            string label,
            string field,
            Func<IEnumerable<(string Value, string Label)>> options,
            Func<T, string> get,
            Action<T, string> set,
            Func<T, bool> editable = null)
        {
            var current = new List<(string Value, string Label)>();
            var dropdown = new DropdownField(label);
            Action<object> apply = value => Apply(label + " changed", editable, entry => set(entry, (string)value));
            dropdown.RegisterValueChangedCallback(_ =>
            {
                var index = dropdown.index;
                if (index >= 0 && index < current.Count)
                {
                    apply(current[index].Value);
                }
            });
            refreshers.Add(() =>
            {
                current = options().ToList();
                var values = Relevant(editable).Select(entry => get(entry) ?? string.Empty).ToArray();
                var first = values.Length > 0 ? values[0] : string.Empty;
                var mixed = values.Any(value => !string.Equals(value, first, StringComparison.Ordinal));
                if (!mixed && !current.Any(option => string.Equals(option.Value, first, StringComparison.Ordinal)))
                {
                    current.Add((first, first.Length == 0 ? "(none)" : first + " (unknown)"));
                }

                dropdown.choices = current.Select(option => option.Label).ToList();
                var selected = current.FindIndex(option => string.Equals(option.Value, first, StringComparison.Ordinal));
                dropdown.SetValueWithoutNotify(selected >= 0 && !mixed ? current[selected].Label : string.Empty);
                dropdown.showMixedValue = mixed;
                dropdown.SetEnabled(IsEditable(editable));
            });
            Register(field, dropdown, apply);
            return dropdown;
        }

        // One toggle per option for a set of string values, such as tags. A toggle is on when
        // every selected entry has the value and mixed when only some do; turning it on or off
        // adds or removes the value for every selected entry.
        public VisualElement Flags(
            string label,
            string field,
            Func<IEnumerable<(string Value, string Label)>> options,
            Func<T, string[]> get,
            Action<T, string[]> set,
            string emptyText,
            Func<T, bool> editable = null)
        {
            var container = new VisualElement();
            var title = new Label(label);
            title.AddToClassList(ContentToolStyles.SectionTitle);
            var toggles = new VisualElement();
            toggles.AddToClassList("content-tag-set");
            var empty = ContentToolStyles.CreateHint(emptyText);
            container.Add(title);
            container.Add(toggles);
            container.Add(empty);
            var shown = new List<string>();
            Action<string, bool> setFlag = (value, add) => Apply(
                value + (add ? " added" : " removed"),
                editable,
                entry => set(entry, Normalize(add
                    ? (get(entry) ?? Array.Empty<string>()).Concat(new[] { value })
                    : (get(entry) ?? Array.Empty<string>()).Where(existing =>
                        !string.Equals(existing, value, StringComparison.Ordinal)))));
            refreshers.Add(() =>
            {
                var available = options().ToArray();
                var values = available.Select(option => option.Value).ToList();
                if (!values.SequenceEqual(shown))
                {
                    shown = values;
                    toggles.Clear();
                    foreach (var option in available)
                    {
                        var value = option.Value;
                        var toggle = new Toggle { text = option.Label, tooltip = value, userData = value };
                        toggle.RegisterValueChangedCallback(change => setFlag(value, change.newValue));
                        toggles.Add(toggle);
                    }
                }

                empty.style.display = available.Length == 0 ? DisplayStyle.Flex : DisplayStyle.None;
                var relevant = Relevant(editable);
                foreach (var toggle in toggles.Children().OfType<Toggle>())
                {
                    var value = (string)toggle.userData;
                    var count = relevant.Count(entry => (get(entry) ?? Array.Empty<string>())
                        .Contains(value, StringComparer.Ordinal));
                    toggle.SetValueWithoutNotify(relevant.Count > 0 && count == relevant.Count);
                    toggle.showMixedValue = count > 0 && count < relevant.Count;
                    toggle.SetEnabled(IsEditable(editable));
                }
            });
            Register(field, container, null, setFlag);
            return container;
        }

        // Marks every field that a finding about a selected entry concerns, and shows the
        // findings as its tooltip.
        public void ApplyIssues(IEnumerable<ContentIssue> issues)
        {
            var keys = new HashSet<string>(targets.Select(key), StringComparer.Ordinal);
            var relevant = (issues ?? Array.Empty<ContentIssue>())
                .Where(issue => keys.Contains(issue.EntityKey))
                .ToArray();
            foreach (var entry in fields)
            {
                var related = relevant.Where(issue => entry.Paths.Any(issue.Concerns)).ToArray();
                var hasError = related.Any(issue => issue.Severity == ContentIssueSeverity.Error);
                entry.Element.EnableInClassList(ContentToolStyles.FieldError, hasError);
                entry.Element.EnableInClassList(
                    ContentToolStyles.FieldWarning,
                    !hasError && related.Length > 0);
                entry.ShowIssues(related);
            }
        }

        // The field that edits the path, or the closest field that contains it.
        public VisualElement FindField(string field)
        {
            if (string.IsNullOrEmpty(field))
            {
                return null;
            }

            var issue = new ContentIssue(string.Empty, field, string.Empty);
            return fields
                .Select(entry => (entry.Element, Length: entry.Paths.Where(issue.Concerns).Select(path => path.Length).DefaultIfEmpty(-1).Max()))
                .Where(match => match.Length >= 0)
                .OrderByDescending(match => match.Length)
                .Select(match => match.Element)
                .FirstOrDefault();
        }

        public void Register(string field, VisualElement element)
        {
            Register(field, element, null);
        }

        // Lets the field also show findings about other paths, such as the separate X, Y, and
        // Z values that one vector field edits.
        public void Covers(string field, params string[] paths)
        {
            foreach (var entry in fields.Where(candidate => string.Equals(candidate.Field, field, StringComparison.Ordinal)))
            {
                entry.Paths.AddRange((paths ?? Array.Empty<string>()).Where(path => !string.IsNullOrEmpty(path)));
            }
        }

        // Edits the field the way its control does, as one undoable step for every selected entry.
        public bool TrySetValue(string field, object value)
        {
            var entry = fields.FirstOrDefault(candidate => string.Equals(candidate.Field, field, StringComparison.Ordinal)
                && candidate.SetValue != null);
            entry?.SetValue(value);
            return entry != null;
        }

        // Turns one value of a set field on or off for every selected entry, as its toggle does.
        public bool TrySetFlag(string field, string value, bool enabled)
        {
            var entry = fields.FirstOrDefault(candidate => string.Equals(candidate.Field, field, StringComparison.Ordinal)
                && candidate.SetFlag != null);
            entry?.SetFlag(value, enabled);
            return entry != null;
        }

        private void Register(
            string field,
            VisualElement element,
            Action<object> setValue,
            Action<string, bool> setFlag = null)
        {
            if (!string.IsNullOrEmpty(field) && element != null)
            {
                fields.Add(new FieldEntry(field, element, setValue, setFlag));
            }
        }

        private TField Bind<TField, TValue>(
            TField control,
            string label,
            string field,
            Func<T, TValue> get,
            Action<T, TValue> set,
            Func<T, bool> editable)
            where TField : BaseField<TValue>
        {
            control.label = label;
            Action<object> apply = value => Apply(label + " changed", editable, entry => set(entry, (TValue)value));
            control.RegisterValueChangedCallback(change => apply(change.newValue));
            refreshers.Add(() =>
            {
                var comparer = EqualityComparer<TValue>.Default;
                var values = Relevant(editable).Select(get).ToArray();
                var first = values.Length > 0 ? values[0] : default;
                control.SetValueWithoutNotify(first);
                control.showMixedValue = values.Any(value => !comparer.Equals(value, first));
                control.SetEnabled(IsEditable(editable));
            });
            Register(field, control, apply);
            return control;
        }

        private void Apply(string label, Func<T, bool> editable, Action<T> edit)
        {
            var applicable = targets.Where(entry => editable == null || editable(entry)).ToArray();
            if (applicable.Length == 0)
            {
                return;
            }

            var description = applicable.Length == 1
                ? label
                : label + " for " + applicable.Length + " entries";
            editor.Change(description, () =>
            {
                foreach (var entry in applicable)
                {
                    edit(entry);
                }
            });
        }

        private bool IsEditable(Func<T, bool> editable)
        {
            return targets.Count > 0 && (editable == null || targets.Any(editable));
        }

        // The selected entries a field shows: the ones it can edit, or all when it edits none,
        // so a field that only applies to some entries shows their shared value.
        private IReadOnlyList<T> Relevant(Func<T, bool> editable)
        {
            if (editable == null)
            {
                return targets;
            }

            var applicable = targets.Where(editable).ToArray();
            return applicable.Length > 0 ? applicable : targets;
        }

        private static string[] Normalize(IEnumerable<string> values)
        {
            return values
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        private sealed class FieldEntry
        {
            private string tooltip;
            private bool showingIssues;

            public FieldEntry(
                string field,
                VisualElement element,
                Action<object> setValue,
                Action<string, bool> setFlag)
            {
                Field = field;
                Paths = new List<string> { field };
                Element = element;
                SetValue = setValue;
                SetFlag = setFlag;
            }

            public string Field { get; }

            // The field itself and the further paths it covers.
            public List<string> Paths { get; }

            public VisualElement Element { get; }

            public Action<object> SetValue { get; }

            public Action<string, bool> SetFlag { get; }

            // Shows the findings as the tooltip, and the field's own tooltip again without them.
            public void ShowIssues(IReadOnlyCollection<ContentIssue> related)
            {
                if (related.Count > 0)
                {
                    if (!showingIssues)
                    {
                        tooltip = Element.tooltip;
                        showingIssues = true;
                    }

                    Element.tooltip = string.Join("\n", related.Select(issue => issue.Message));
                }
                else if (showingIssues)
                {
                    Element.tooltip = tooltip;
                    showingIssues = false;
                }
            }
        }
    }
}
