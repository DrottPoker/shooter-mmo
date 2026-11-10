namespace Mmorpg.GameProtocol
{
    public static partial class RealtimeProtocol
    {
        // The name is the placeholder of the client's text that the value fills: a letter
        // followed by letters and digits, such as "toolKind".
        private static bool SatisfiesRules(RealtimeMessageArgument argument)
        {
            var name = argument.Name;
            if (string.IsNullOrEmpty(name) || !IsAsciiLetter(name[0]))
            {
                return false;
            }

            foreach (var character in name)
            {
                if (!IsAsciiLetter(character) && (character < '0' || character > '9'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAsciiLetter(char character)
        {
            return (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z');
        }
    }

    public sealed partial class RealtimeMessageArgument
    {
        public static readonly RealtimeMessageArgument[] None = new RealtimeMessageArgument[0];

        // A value that the client shows as it is, such as a number.
        public static RealtimeMessageArgument Text(string name, string value)
        {
            return new RealtimeMessageArgument(name, RealtimeMessageArgumentKind.Text, value);
        }

        // A key of the client's text tables, such as the name of a tool kind.
        public static RealtimeMessageArgument TextKey(string name, string key)
        {
            return new RealtimeMessageArgument(name, RealtimeMessageArgumentKind.TextKey, key);
        }
    }
}
