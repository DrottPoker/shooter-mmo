using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Mmorpg.WorldData.Editor.ContentTools
{
    // A searchable, filterable list of content entries with multi-selection. Entries are
    // identified by key, so the selection survives undo, redo, and reloads, which replace the
    // entry objects. Only shown entries are selected: what you see is what you edit.
    public sealed class ContentListPane<T> : VisualElement
        where T : class
    {
        private readonly Func<T, string> key;
        private readonly Func<T, string> title;
        private readonly Func<T, string> subtitle;
        private readonly Func<T, string> searchText;
        private readonly ToolbarSearchField search;
        private readonly VisualElement filterBar;
        private readonly ListView list;
        private readonly Label summary;
        private readonly List<Filter> filters = new List<Filter>();
        private readonly List<T> shown = new List<T>();
        private readonly HashSet<string> selectedKeys = new HashSet<string>(StringComparer.Ordinal);
        private IReadOnlyList<T> source = Array.Empty<T>();
        private Func<T, ContentIssueSeverity?> issueState = _ => null;
        private bool suppressSelectionEvents;

        public ContentListPane(
            string heading,
            Func<T, string> key,
            Func<T, string> title,
            Func<T, string> subtitle,
            Func<T, string> searchText = null)
        {
            this.key = key ?? throw new ArgumentNullException(nameof(key));
            this.title = title ?? throw new ArgumentNullException(nameof(title));
            this.subtitle = subtitle ?? (_ => string.Empty);
            this.searchText = searchText ?? (_ => string.Empty);
            AddToClassList("content-list-pane");

            var header = new Label(heading);
            header.AddToClassList(ContentToolStyles.Heading);
            search = new ToolbarSearchField();
            search.RegisterValueChangedCallback(_ => ApplyFilters());
            filterBar = new VisualElement();
            filterBar.AddToClassList("content-list-filters");
            list = new ListView
            {
                selectionType = SelectionType.Multiple,
                fixedItemHeight = 34f,
                makeItem = MakeRow,
                bindItem = BindRow,
                itemsSource = shown
            };
            list.style.flexGrow = 1f;
            list.selectionChanged += _ => OnListSelectionChanged();
            summary = new Label();
            summary.AddToClassList("content-list-summary");

            Add(header);
            Add(search);
            Add(filterBar);
            Add(list);
            Add(summary);
        }

        // Raised when the selected entries change, including when undo replaces their objects.
        public event Action SelectionChanged;

        public IReadOnlyList<T> Selection => shown
            .Where(entry => selectedKeys.Contains(key(entry)))
            .ToArray();

        public IReadOnlyList<T> Shown => shown;

        public string SearchText
        {
            get => search.value;
            set
            {
                search.SetValueWithoutNotify(value ?? string.Empty);
                ApplyFilters();
            }
        }

        public void SetItems(IReadOnlyList<T> items)
        {
            source = items ?? Array.Empty<T>();
            foreach (var filter in filters)
            {
                filter.RefreshChoices?.Invoke();
            }

            ApplyFilters(forceSelectionEvent: true);
        }

        // A dropdown that keeps entries matching the chosen value. The first choice keeps all.
        public DropdownField AddChoiceFilter(
            string allLabel,
            Func<IEnumerable<(string Value, string Label)>> choices,
            Func<T, string, bool> matches)
        {
            var values = new List<string>();
            var dropdown = new DropdownField();
            dropdown.tooltip = allLabel;
            var filter = new Filter();
            filter.RefreshChoices = () =>
            {
                var current = filter.Value;
                var options = choices().ToArray();
                values.Clear();
                values.Add(string.Empty);
                values.AddRange(options.Select(option => option.Value));
                dropdown.choices = new[] { allLabel }
                    .Concat(options.Select(option => option.Label))
                    .ToList();
                var index = Math.Max(0, values.IndexOf(current));
                filter.Value = values[index];
                dropdown.SetValueWithoutNotify(dropdown.choices[index]);
            };
            filter.Matches = entry => filter.Value.Length == 0 || matches(entry, filter.Value);
            dropdown.RegisterValueChangedCallback(_ =>
            {
                filter.Value = values[Math.Max(0, dropdown.index)];
                ApplyFilters();
            });
            filter.RefreshChoices();
            filters.Add(filter);
            filterBar.Add(dropdown);
            return dropdown;
        }

        public Toggle AddToggleFilter(string label, Func<T, bool> matches)
        {
            var toggle = new Toggle { text = label };
            var filter = new Filter
            {
                Matches = entry => !toggle.value || matches(entry)
            };
            toggle.RegisterValueChangedCallback(_ => ApplyFilters());
            filters.Add(filter);
            filterBar.Add(toggle);
            return toggle;
        }

        // Marks entries that have validation findings.
        public void SetIssueState(Func<T, ContentIssueSeverity?> state)
        {
            issueState = state ?? (_ => null);
            list.RefreshItems();
        }

        public void Select(IEnumerable<string> keys)
        {
            selectedKeys.Clear();
            foreach (var value in keys ?? Array.Empty<string>())
            {
                selectedKeys.Add(value);
            }

            // An entry the filters hide becomes shown again when it is selected on purpose.
            if (selectedKeys.Count > 0 && !shown.Any(entry => selectedKeys.Contains(key(entry))))
            {
                SearchText = string.Empty;
            }

            ApplyFilters(forceSelectionEvent: true);
            var first = shown.FindIndex(entry => selectedKeys.Contains(key(entry)));
            if (first >= 0)
            {
                list.ScrollToItem(first);
            }
        }

        public void RefreshRows()
        {
            list.RefreshItems();
        }

        // Applies the search and the filters again, after a filter control changed.
        public void Refresh()
        {
            ApplyFilters();
        }

        private void ApplyFilters(bool forceSelectionEvent = false)
        {
            var before = string.Join("\n", Selection.Select(key));
            var text = search.value ?? string.Empty;
            shown.Clear();
            shown.AddRange(source.Where(entry =>
                filters.All(filter => filter.Matches(entry))
                && MatchesSearch(entry, text)));
            list.itemsSource = shown;
            list.RefreshItems();

            var indices = new List<int>();
            for (var index = 0; index < shown.Count; index++)
            {
                if (selectedKeys.Contains(key(shown[index])))
                {
                    indices.Add(index);
                }
            }

            suppressSelectionEvents = true;
            list.SetSelectionWithoutNotify(indices);
            suppressSelectionEvents = false;
            selectedKeys.IntersectWith(indices.Select(index => key(shown[index])));
            summary.text = shown.Count == source.Count
                ? source.Count + " entries"
                : shown.Count + " of " + source.Count + " entries";

            var after = string.Join("\n", Selection.Select(key));
            if (forceSelectionEvent || !string.Equals(before, after, StringComparison.Ordinal))
            {
                SelectionChanged?.Invoke();
            }
        }

        private bool MatchesSearch(T entry, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            return Contains(key(entry), text)
                || Contains(title(entry), text)
                || Contains(subtitle(entry), text)
                || Contains(searchText(entry), text);
        }

        private void OnListSelectionChanged()
        {
            if (suppressSelectionEvents)
            {
                return;
            }

            selectedKeys.Clear();
            foreach (var index in list.selectedIndices)
            {
                if (index >= 0 && index < shown.Count)
                {
                    selectedKeys.Add(key(shown[index]));
                }
            }

            SelectionChanged?.Invoke();
        }

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("content-list-row");
            var text = new VisualElement();
            text.AddToClassList("content-list-row__text");
            var titleLabel = new Label { name = "title" };
            titleLabel.AddToClassList("content-list-row__title");
            var subtitleLabel = new Label { name = "subtitle" };
            subtitleLabel.AddToClassList("content-list-row__subtitle");
            text.Add(titleLabel);
            text.Add(subtitleLabel);
            var marker = new VisualElement { name = "marker" };
            marker.AddToClassList("content-list-row__marker");
            row.Add(text);
            row.Add(marker);
            return row;
        }

        private void BindRow(VisualElement row, int index)
        {
            if (index < 0 || index >= shown.Count)
            {
                return;
            }

            var entry = shown[index];
            row.Q<Label>("title").text = title(entry);
            row.Q<Label>("subtitle").text = subtitle(entry);
            var marker = row.Q("marker");
            var state = issueState(entry);
            marker.EnableInClassList(ContentToolStyles.MarkerError, state == ContentIssueSeverity.Error);
            marker.EnableInClassList(ContentToolStyles.MarkerWarning, state == ContentIssueSeverity.Warning);
            marker.tooltip = state == null ? string.Empty : "This entry has validation findings.";
        }

        private static bool Contains(string value, string text)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class Filter
        {
            public string Value = string.Empty;

            public Func<T, bool> Matches = _ => true;

            public Action RefreshChoices;
        }
    }
}
