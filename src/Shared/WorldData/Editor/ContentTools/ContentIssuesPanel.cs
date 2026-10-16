using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

namespace Mmorpg.WorldData.Editor.ContentTools
{
    // The validation findings of a tool. Choosing a finding selects the entry it concerns.
    public sealed class ContentIssuesPanel : VisualElement
    {
        private readonly Label title;
        private readonly ListView list;
        private readonly List<ContentIssue> issues = new List<ContentIssue>();
        private Func<ContentIssue, string> describe = issue => issue.Message;

        public ContentIssuesPanel()
        {
            AddToClassList("content-issues");
            var header = new VisualElement();
            header.AddToClassList("content-issues__header");
            title = new Label();
            title.AddToClassList("content-issues__title");
            header.Add(title);
            list = new ListView
            {
                selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
                makeItem = MakeRow,
                bindItem = BindRow,
                itemsSource = issues
            };
            list.style.flexGrow = 1f;
            list.selectionChanged += _ =>
            {
                if (list.selectedItem is ContentIssue issue)
                {
                    IssueActivated?.Invoke(issue);
                }
            };
            Add(header);
            Add(list);
            SetIssues(Array.Empty<ContentIssue>(), null, "Not validated yet.");
        }

        public event Action<ContentIssue> IssueActivated;

        public IReadOnlyList<ContentIssue> Issues => issues;

        public void SetIssues(
            IEnumerable<ContentIssue> values,
            Func<ContentIssue, string> describeIssue,
            string emptyText = "No problems found.")
        {
            describe = describeIssue ?? (issue => issue.Message);
            issues.Clear();
            issues.AddRange((values ?? Array.Empty<ContentIssue>())
                .OrderBy(issue => issue.Severity)
                .ThenBy(issue => issue.EntityKey, StringComparer.Ordinal));
            var errors = issues.Count(issue => issue.Severity == ContentIssueSeverity.Error);
            var warnings = issues.Count - errors;
            title.text = issues.Count == 0
                ? emptyText
                : "Problems: " + errors + (errors == 1 ? " error" : " errors")
                    + (warnings > 0 ? ", " + warnings + (warnings == 1 ? " warning" : " warnings") : string.Empty);
            list.style.display = issues.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            list.ClearSelection();
            list.RefreshItems();
        }

        private static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("content-issues__row");
            var marker = new VisualElement { name = "marker" };
            marker.AddToClassList("content-list-row__marker");
            row.Add(marker);
            row.Add(new Label { name = "text" });
            return row;
        }

        private void BindRow(VisualElement row, int index)
        {
            if (index < 0 || index >= issues.Count)
            {
                return;
            }

            var issue = issues[index];
            row.Q<Label>("text").text = describe(issue);
            row.tooltip = issue.Message;
            var marker = row.Q("marker");
            marker.EnableInClassList(ContentToolStyles.MarkerError, issue.Severity == ContentIssueSeverity.Error);
            marker.EnableInClassList(ContentToolStyles.MarkerWarning, issue.Severity == ContentIssueSeverity.Warning);
        }
    }
}
