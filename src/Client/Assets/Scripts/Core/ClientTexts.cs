using System;
using System.Collections.Generic;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.Client.Core
{
    // Reads the bundled text tables into the one text catalog that the client shows content
    // text from. The composition root loads it once and passes it on.
    public static class ClientTexts
    {
        public static bool TryLoad(
            IReadOnlyList<TextAsset> tableAssets,
            out TextCatalog catalog,
            out string error)
        {
            catalog = null;
            if (tableAssets == null || tableAssets.Count == 0)
            {
                error = "The client configuration lists no text tables.";
                return false;
            }

            var tables = new List<TextTableDocument>(tableAssets.Count);
            for (var index = 0; index < tableAssets.Count; index++)
            {
                var asset = tableAssets[index];
                if (asset == null)
                {
                    error = "Text table " + index + " of the client configuration is missing.";
                    return false;
                }

                if (!TryParse(asset.text, out var table, out error))
                {
                    error = "Text table '" + asset.name + "' is invalid: " + error;
                    return false;
                }

                tables.Add(table);
            }

            try
            {
                catalog = TextCatalog.Create(tables);
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
    }
}
