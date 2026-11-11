using System;
using System.Collections.Generic;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.Client.Core
{
    // Reads the resolved text tables into the one text catalog that the client shows content
    // text from. The composition root loads it once and passes it on.
    public static class ClientTexts
    {
        // Set to 1, the client shows the pseudo-locale from its next start.
        public const string PseudoLocalePreference = "Mmorpg.Debug.PseudoLocale";

        public static bool TryLoad(
            IReadOnlyList<string> tables,
            out TextCatalog catalog,
            out string error)
        {
            catalog = null;
            if (tables == null || tables.Count == 0)
            {
                error = "The client has no text tables.";
                return false;
            }

            var documents = new List<TextTableDocument>(tables.Count);
            for (var index = 0; index < tables.Count; index++)
            {
                if (tables[index] == null)
                {
                    error = "Text table " + index + " is missing.";
                    return false;
                }

                if (!TryParse(tables[index], out var table, out error))
                {
                    error = "Text table " + index + " is invalid: " + error;
                    return false;
                }

                documents.Add(table);
            }

            try
            {
                catalog = TextCatalog.Create(documents);
                error = string.Empty;
                return true;
            }
            catch (TextTableValidationException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static bool TryParse(string json, out TextTableDocument table, out string error)
        {
            table = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "The text table is empty.";
                return false;
            }

            try
            {
                var document = JsonUtility.FromJson<TextTableJsonDocument>(json);
                if (document == null)
                {
                    error = "The text table has no root object.";
                    return false;
                }

                table = document.ToRuntime();
                TextTable.Validate(table);
                error = string.Empty;
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is TextTableValidationException)
            {
                table = null;
                error = exception.Message;
                return false;
            }
        }

        // One request per bundled table of the source locale, named by the table id the bundled
        // file states. A table whose header cannot be read keeps an empty id, which no manifest
        // lists, so the client keeps its bundled set and the loader reports the table.
        public static IReadOnlyList<ClientContentRequest> ContentRequests(IReadOnlyList<string> bundledTables)
        {
            var requests = new List<ClientContentRequest>();
            foreach (var bundled in bundledTables ?? Array.Empty<string>())
            {
                var id = ClientContentHeader.TryRead(bundled, out var header) ? header.Id : string.Empty;
                requests.Add(new ClientContentRequest(
                    ClientContentKinds.TextTable,
                    id,
                    TextTableFormat.SourceLocale,
                    TextTableFormat.Version,
                    bundled,
                    TryValidateContent));
            }

            return requests;
        }

        public static bool TryValidateContent(string json, out ClientContentHeader header, out string error)
        {
            header = null;
            if (!TryParse(json, out var table, out error))
            {
                return false;
            }

            header = new ClientContentHeader(table.TableId, table.Locale, table.FormatVersion, table.Revision);
            return true;
        }
    }
}
