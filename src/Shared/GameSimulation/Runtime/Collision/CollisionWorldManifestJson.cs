using System;
using System.Globalization;
using System.Text;

namespace Mmorpg.GameSimulation
{
    // The one text form of a collision manifest. The collision compiler and the Unity collision
    // bake both write it, so the committed file never depends on which of them ran last, and
    // verification compares the text. Readers parse it with any JSON reader.
    public static class CollisionWorldManifestJson
    {
        private const string Indent = "    ";

        public static string Serialize(CollisionWorldManifest manifest)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            var chunks = manifest.Chunks ?? Array.Empty<CollisionChunkManifestEntry>();
            var text = new StringBuilder();
            text.Append("{\n");
            AppendMember(text, 1, "FormatVersion", Number(manifest.FormatVersion), true);
            AppendMember(text, 1, "WorldId", Text(manifest.WorldId), true);
            AppendMember(text, 1, "Revision", Text(manifest.Revision), true);
            AppendMember(text, 1, "ChunkSize", Number(manifest.ChunkSize), true);
            if (chunks.Length == 0)
            {
                AppendMember(text, 1, "Chunks", "[]", false);
            }
            else
            {
                text.Append(Indent).Append("\"Chunks\": [\n");
                for (var index = 0; index < chunks.Length; index++)
                {
                    var chunk = chunks[index];
                    text.Append(Indent).Append(Indent).Append("{\n");
                    AppendMember(text, 3, "X", Number(chunk.X), true);
                    AppendMember(text, 3, "Z", Number(chunk.Z), true);
                    AppendMember(text, 3, "ResourceName", Text(chunk.ResourceName), true);
                    AppendMember(text, 3, "Sha256", Text(chunk.Sha256), true);
                    AppendMember(text, 3, "MinimumX", Number(chunk.MinimumX), true);
                    AppendMember(text, 3, "MinimumZ", Number(chunk.MinimumZ), true);
                    AppendMember(text, 3, "MaximumX", Number(chunk.MaximumX), true);
                    AppendMember(text, 3, "MaximumZ", Number(chunk.MaximumZ), false);
                    text.Append(Indent).Append(Indent).Append(index + 1 < chunks.Length ? "},\n" : "}\n");
                }

                text.Append(Indent).Append("]\n");
            }

            text.Append("}\n");
            return text.ToString();
        }

        private static void AppendMember(StringBuilder text, int depth, string name, string value, bool more)
        {
            for (var level = 0; level < depth; level++)
            {
                text.Append(Indent);
            }

            text.Append('"').Append(name).Append("\": ").Append(value).Append(more ? ",\n" : "\n");
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        // A float always shows a decimal point, so a reader never takes it for an integer.
        private static string Number(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentException("A collision manifest holds finite numbers only.");
            }

            var text = value.ToString("R", CultureInfo.InvariantCulture);
            return text.IndexOf('.') < 0 && text.IndexOf('E') < 0 ? text + ".0" : text;
        }

        private static string Text(string value)
        {
            var text = new StringBuilder("\"");
            foreach (var character in value ?? string.Empty)
            {
                if (character == '"' || character == '\\')
                {
                    text.Append('\\').Append(character);
                }
                else if (character < ' ')
                {
                    text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    text.Append(character);
                }
            }

            return text.Append('"').ToString();
        }
    }
}
