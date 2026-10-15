using System;
using System.Globalization;
using System.Text;

namespace Mmorpg.WorldData.Editor
{
    // JsonUtility writes a float through its double value, so 0.55f becomes
    // 0.550000011920929. This rewrites such numbers to the shortest text that parses back
    // to the same float. Only use it for documents whose fractional fields are all float:
    // a double field holding a float-representable value would change.
    public static class SinglePrecisionJson
    {
        public static string ShortenFloats(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var builder = new StringBuilder(json.Length);
            var index = 0;
            while (index < json.Length)
            {
                var character = json[index];
                if (character == '"')
                {
                    var end = SkipString(json, index);
                    builder.Append(json, index, end - index);
                    index = end;
                    continue;
                }

                if (character == '-' || (character >= '0' && character <= '9'))
                {
                    var end = SkipNumber(json, index);
                    builder.Append(Shorten(json.Substring(index, end - index)));
                    index = end;
                    continue;
                }

                builder.Append(character);
                index++;
            }

            return builder.ToString();
        }

        private static string Shorten(string token)
        {
            if (token.IndexOfAny(new[] { '.', 'e', 'E' }) < 0
                || !double.TryParse(
                    token,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                return token;
            }

            var single = (float)value;
            if (float.IsInfinity(single) || (double)single != value)
            {
                return token;
            }

            var shortened = single.ToString("R", CultureInfo.InvariantCulture);
            if (float.Parse(shortened, NumberStyles.Float, CultureInfo.InvariantCulture) != single)
            {
                return token;
            }

            return shortened.IndexOfAny(new[] { '.', 'E' }) < 0 ? shortened + ".0" : shortened;
        }

        private static int SkipString(string json, int start)
        {
            var index = start + 1;
            while (index < json.Length)
            {
                if (json[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                if (json[index] == '"')
                {
                    return index + 1;
                }

                index++;
            }

            return json.Length;
        }

        private static int SkipNumber(string json, int start)
        {
            var index = start;
            while (index < json.Length)
            {
                var character = json[index];
                if ((character >= '0' && character <= '9')
                    || character == '-'
                    || character == '+'
                    || character == '.'
                    || character == 'e'
                    || character == 'E')
                {
                    index++;
                    continue;
                }

                break;
            }

            return index;
        }
    }
}
