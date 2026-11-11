using System.Collections.Generic;
using Mmorpg.Client.Api;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.Client.Core
{
    // The text the player sees for a code: one that a server sent, or one that the client gave
    // a failure it found itself. The servers send a stable code and, where a text needs them,
    // named values; the client owns the sentence. The English diagnostic of a failure, a
    // server's or the client's own, is written to the log and never shown.
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
                Debug.Log("Message [" + code + "]: " + diagnostic);
            }

            return MessageText.Resolve(texts ?? TextCatalog.Empty(TextTableFormat.SourceLocale), code, arguments);
        }

        // A request shows the text of its code: the one GameService refused it with, or the
        // client's own code for a request that never got a usable answer.
        public static string Text(TextCatalog texts, ApiError error)
        {
            if (error == null)
            {
                return string.Empty;
            }

            return Text(texts, error.Code, error.ToDiagnostic());
        }
    }
}
