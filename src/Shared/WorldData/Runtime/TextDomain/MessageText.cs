using System;
using System.Collections.Generic;
using System.Text;

namespace Mmorpg.WorldData.Text
{
    // One named value of a message. A server sends an id or a number, never a sentence: a text
    // key is looked up in the client's text tables, and a plain text is shown as it is.
    public readonly struct MessageArgument
    {
        private MessageArgument(string name, string value, bool isTextKey)
        {
            Name = name ?? string.Empty;
            Value = value ?? string.Empty;
            IsTextKey = isTextKey;
        }

        public string Name { get; }

        public string Value { get; }

        public bool IsTextKey { get; }

        public static MessageArgument Text(string name, string value)
        {
            return new MessageArgument(name, value, false);
        }

        public static MessageArgument TextKey(string name, string key)
        {
            return new MessageArgument(name, key, true);
        }
    }

    // A text with named placeholders, such as "{toolKind} required in the Tool slot.". A
    // placeholder is a name in braces; a text has no other use of braces.
    public static class TextTemplate
    {
        public const int MaximumPlaceholderNameLength = 32;

        // A placeholder without a value stays in the text, so a missing argument is visible.
        public static string Format(
            string template,
            IReadOnlyList<MessageArgument> arguments,
            TextCatalog texts)
        {
            if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0)
            {
                return template ?? string.Empty;
            }

            var builder = new StringBuilder(template.Length + 32);
            var index = 0;
            while (index < template.Length)
            {
                var open = template.IndexOf('{', index);
                var close = open < 0 ? -1 : template.IndexOf('}', open + 1);
                if (close < 0)
                {
                    builder.Append(template, index, template.Length - index);
                    break;
                }

                builder.Append(template, index, open - index);
                var name = template.Substring(open + 1, close - open - 1);
                if (TryFindValue(name, arguments, texts, out var value))
                {
                    builder.Append(value);
                }
                else
                {
                    builder.Append(template, open, close - open + 1);
                }

                index = close + 1;
            }

            return builder.ToString();
        }

        // The placeholder names of a text, or false when its braces do not form placeholders.
        public static bool TryReadPlaceholders(string template, out IReadOnlyList<string> names)
        {
            var found = new List<string>();
            names = found;
            if (string.IsNullOrEmpty(template))
            {
                return true;
            }

            var index = 0;
            while (index < template.Length)
            {
                var character = template[index];
                if (character == '}')
                {
                    return false;
                }

                if (character != '{')
                {
                    index++;
                    continue;
                }

                var close = template.IndexOf('}', index + 1);
                if (close < 0)
                {
                    return false;
                }

                var name = template.Substring(index + 1, close - index - 1);
                if (!IsValidPlaceholderName(name))
                {
                    return false;
                }

                if (!found.Contains(name))
                {
                    found.Add(name);
                }

                index = close + 1;
            }

            return true;
        }

        // A placeholder name is a letter followed by letters and digits, such as "toolKind".
        public static bool IsValidPlaceholderName(string name)
        {
            if (string.IsNullOrEmpty(name)
                || name.Length > MaximumPlaceholderNameLength
                || !IsAsciiLetter(name[0]))
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

        private static bool TryFindValue(
            string name,
            IReadOnlyList<MessageArgument> arguments,
            TextCatalog texts,
            out string value)
        {
            value = string.Empty;
            if (arguments == null)
            {
                return false;
            }

            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index];
                if (!string.Equals(argument.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                value = argument.IsTextKey
                    ? (texts != null ? texts.Get(argument.Value) : TextCatalog.Missing(argument.Value))
                    : argument.Value;
                return true;
            }

            return false;
        }

        private static bool IsAsciiLetter(char character)
        {
            return (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z');
        }
    }

    // The text a player sees for a code that a server sent: an error, a closure, or a notice.
    // The servers send a stable code and named arguments, and the sentence they send along is
    // English for logs only. A code without a text of its own shows the unlisted text with
    // the code in it, so a new or purely technical code never shows an empty line.
    public static class MessageText
    {
        public const string CodeArgumentName = "code";

        public static bool IsListed(TextCatalog texts, string code)
        {
            return texts != null
                && TextKeys.IsValidId(code)
                && texts.TryGet(TextKeys.Message(code), out _);
        }

        public static string Resolve(
            TextCatalog texts,
            string code,
            IReadOnlyList<MessageArgument> arguments = null)
        {
            if (texts == null)
            {
                throw new ArgumentNullException(nameof(texts));
            }

            if (TextKeys.IsValidId(code) && texts.TryGet(TextKeys.Message(code), out var template))
            {
                return TextTemplate.Format(template, arguments, texts);
            }

            return TextTemplate.Format(
                texts.Get(TextKeys.UnlistedMessage()),
                new[] { MessageArgument.Text(CodeArgumentName, code ?? string.Empty) },
                texts);
        }
    }
}
