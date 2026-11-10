using System.Collections.Generic;
using Mmorpg.Client.Api;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.Client.Core
{
    // The text the player sees for a code that a server sent. The servers send a stable code
    // and, where a text needs them, named values; the client owns the sentence. The English
    // explanation that a server sends along is written to the log and never shown.
    public static class ClientMessages
    {
        public static string Text(
            TextCatalog texts,
            string code,
            string diagnostic = null,
            IReadOnlyList<MessageArgument> arguments = null)
        {
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                Debug.Log("Server message [" + code + "]: " + diagnostic);
            }

            return MessageText.Resolve(texts ?? TextCatalog.Empty(TextTableFormat.SourceLocale), code, arguments);
        }

        // A request that GameService refused shows the text of its code. A request that never
        // got an answer has no server code, so it keeps the client's own explanation.
        public static string Text(TextCatalog texts, ApiError error)
        {
            if (error == null)
            {
                return string.Empty;
            }

            return error.Kind == ApiErrorKind.Http
                ? Text(texts, error.Code, error.ToDisplayMessage())
                : error.Message;
        }
    }
}
