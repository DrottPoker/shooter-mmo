using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Mmorpg.WorldData.Editor.ContentTools
{
    // The layout every content tool shares: a toolbar, a list beside a scrollable detail
    // panel, the problems panel, a status line, and the row of file actions.
    public sealed class ContentToolLayout : VisualElement
    {
        private readonly HelpBox status;

        public ContentToolLayout(float listWidth = 280f)
        {
            ContentToolStyles.Apply(this);
            Toolbar = new Toolbar();
            var body = new TwoPaneSplitView(0, listWidth, TwoPaneSplitViewOrientation.Horizontal);
            body.AddToClassList("content-tool__body");
            ListSlot = new VisualElement();
            ListSlot.style.flexGrow = 1f;
            Detail = new ScrollView(ScrollViewMode.Vertical);
            Detail.AddToClassList("content-detail");
            body.Add(ListSlot);
            body.Add(Detail);
            Issues = new ContentIssuesPanel();
            status = new HelpBox();
            status.AddToClassList("content-status");
            status.style.display = DisplayStyle.None;
            Actions = new VisualElement();
            Actions.AddToClassList("content-actions");

            Add(Toolbar);
            Add(body);
            Add(Issues);
            Add(status);
            Add(Actions);
        }

        public Toolbar Toolbar { get; }

        public VisualElement ListSlot { get; }

        public ScrollView Detail { get; }

        public ContentIssuesPanel Issues { get; }

        public VisualElement Actions { get; }

        public string StatusText => status.text;

        public HelpBoxMessageType StatusType => status.messageType;

        public void SetStatus(string message, HelpBoxMessageType type)
        {
            status.text = message ?? string.Empty;
            status.messageType = type;
            status.style.display = string.IsNullOrEmpty(status.text) ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
