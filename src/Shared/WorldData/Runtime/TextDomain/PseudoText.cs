using System.Text;

namespace Mmorpg.WorldData.Text
{
    // The pseudo-locale: every text of a text table with its vowels accented, so it stays
    // readable and is plainly not the English that the code holds. A placeholder such as
    // {count} and a rich text tag such as <b> stay as they are, because code reads them.
    public static class PseudoText
    {
        public const string Locale = "qps-ploc";

        public static string Alter(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            var closing = '\0';
            foreach (var character in text)
            {
                if (closing != '\0')
                {
                    builder.Append(character);
                    if (character == closing)
                    {
                        closing = '\0';
                    }

                    continue;
                }

                if (character == '{')
                {
                    closing = '}';
                }
                else if (character == '<')
                {
                    closing = '>';
                }

                builder.Append(Accent(character));
            }

            return builder.ToString();
        }

        private static char Accent(char character)
        {
            switch (character)
            {
                case 'a': return 'á';
                case 'e': return 'é';
                case 'i': return 'í';
                case 'o': return 'ó';
                case 'u': return 'ú';
                case 'A': return 'Á';
                case 'E': return 'É';
                case 'I': return 'Í';
                case 'O': return 'Ó';
                case 'U': return 'Ú';
                default: return character;
            }
        }
    }
}
