using System;
using System.Collections.Generic;
using System.Globalization;
using Mmorpg.WorldData.Text;

namespace Mmorpg.Client.Core
{
    // A text that client code names instead of writing: the key of a ui text with its named
    // values. Rule code returns it, and the window that shows it resolves it against the text
    // catalog, so rule code never holds a sentence and a test compares keys.
    public readonly struct ClientText : IEquatable<ClientText>
    {
        private static readonly ClientTextArgument[] NoArguments = Array.Empty<ClientTextArgument>();

        private readonly string key;
        private readonly string literal;
        private readonly ClientTextArgument[] arguments;

        private ClientText(string key, string literal, ClientTextArgument[] arguments)
        {
            this.key = key;
            this.literal = literal;
            this.arguments = arguments;
        }

        // No text, which is also the default value.
        public static ClientText None => default;

        public string Key => key ?? string.Empty;

        public bool IsEmpty => string.IsNullOrEmpty(key) && string.IsNullOrEmpty(literal);

        public IReadOnlyList<ClientTextArgument> Arguments => arguments ?? NoArguments;

        public static ClientText Of(string key, params ClientTextArgument[] arguments)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("A client text needs a key.", nameof(key));
            }

            return new ClientText(key, null, arguments == null || arguments.Length == 0 ? null : arguments);
        }

        // A text that is a sentence already: the text of a server code, or a text of a
        // Temporary panel that stays English.
        public static ClientText Literal(string text)
        {
            return string.IsNullOrEmpty(text) ? default : new ClientText(null, text, null);
        }

        public string Resolve(TextCatalog texts)
        {
            if (!string.IsNullOrEmpty(literal))
            {
                return literal;
            }

            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            var catalog = texts ?? TextCatalog.Empty(TextTableFormat.SourceLocale);
            if (arguments == null)
            {
                return catalog.Get(key);
            }

            var resolved = new MessageArgument[arguments.Length];
            for (var index = 0; index < arguments.Length; index++)
            {
                resolved[index] = arguments[index].Resolve(catalog);
            }

            return catalog.Format(key, resolved);
        }

        public bool Equals(ClientText other)
        {
            if (!string.Equals(key ?? string.Empty, other.key ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(literal ?? string.Empty, other.literal ?? string.Empty, StringComparison.Ordinal))
            {
                return false;
            }

            var mine = arguments ?? NoArguments;
            var theirs = other.arguments ?? NoArguments;
            if (mine.Length != theirs.Length)
            {
                return false;
            }

            for (var index = 0; index < mine.Length; index++)
            {
                if (!mine[index].Equals(theirs[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            return obj is ClientText other && Equals(other);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(key ?? literal ?? string.Empty);
        }

        // The key or the literal, for logs and test reports. Never shown to a player.
        public override string ToString()
        {
            return !string.IsNullOrEmpty(literal) ? literal : Key;
        }

        public static bool operator ==(ClientText left, ClientText right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ClientText left, ClientText right)
        {
            return !left.Equals(right);
        }
    }

    // One named value of a client text: a plain text, the key of another text, or a whole
    // client text, such as the reason inside "The swap is not possible: {reason}".
    public readonly struct ClientTextArgument : IEquatable<ClientTextArgument>
    {
        private readonly MessageArgument value;

        // One element. An array, because a struct cannot hold a field of the type that holds it.
        private readonly ClientText[] nested;

        private ClientTextArgument(MessageArgument value, ClientText[] nested)
        {
            this.value = value;
            this.nested = nested;
        }

        public string Name => value.Name;

        public static ClientTextArgument Text(string name, string text)
        {
            return new ClientTextArgument(MessageArgument.Text(name, text), null);
        }

        public static ClientTextArgument Number(string name, long number)
        {
            return Text(name, number.ToString(CultureInfo.InvariantCulture));
        }

        public static ClientTextArgument TextKey(string name, string key)
        {
            return new ClientTextArgument(MessageArgument.TextKey(name, key), null);
        }

        public static ClientTextArgument Nested(string name, ClientText text)
        {
            return new ClientTextArgument(MessageArgument.Text(name, string.Empty), new[] { text });
        }

        public static implicit operator ClientTextArgument(MessageArgument argument)
        {
            return new ClientTextArgument(argument, null);
        }

        public MessageArgument Resolve(TextCatalog texts)
        {
            return nested == null ? value : MessageArgument.Text(value.Name, nested[0].Resolve(texts));
        }

        public bool Equals(ClientTextArgument other)
        {
            if (!string.Equals(value.Name, other.value.Name, StringComparison.Ordinal)
                || (nested == null) != (other.nested == null))
            {
                return false;
            }

            return nested != null
                ? nested[0].Equals(other.nested[0])
                : value.IsTextKey == other.value.IsTextKey
                    && string.Equals(value.Value, other.value.Value, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is ClientTextArgument other && Equals(other);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(value.Name ?? string.Empty);
        }
    }
}
