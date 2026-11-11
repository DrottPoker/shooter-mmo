using TMPro;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // The entry screen while the client checks its content at start: the title and one status
    // line, and no form. The composition root removes it once the content is resolved.
    public sealed class ContentCheckPanel : MonoBehaviour
    {
        public const string ScreenName = "ContentCheck";

        private string statusText = string.Empty;
        private TemporaryEntryScreen screen;
        private TextMeshProUGUI status;

        public string StatusText
        {
            get { return status != null ? status.text : statusText; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(string checkingText)
        {
            statusText = checkingText ?? string.Empty;
        }

        private void Start()
        {
            screen = TemporaryEntryScreen.Create(transform, ScreenName);
            var card = TemporaryEntryScreen.AddCard(screen.Root, "ContentCheckCard", new Vector2(0f, 260f), new Vector2(440f, 0f), true);
            TemporaryEntryScreen.AddHeading(card, TemporaryEntryScreen.GameTitle, 30);
            status = TemporaryEntryScreen.AddStatus(card);
            status.text = statusText;
        }

        private void OnDestroy()
        {
            screen?.Destroy();
        }
    }
}
