using System.Globalization;
using System.Text;

namespace Mmorpg.Tools.ProtocolGenerator;

// Names and literals in generated C#.
public static class CSharpNames
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
        "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while"
    };

    // Names the generated methods already use for their parameters and locals.
    private static readonly HashSet<string> GeneratedNames = new(StringComparer.Ordinal)
    {
        "data", "decoded", "element", "error", "index", "item", "reader", "stream", "value", "writer",
        "first", "second"
    };

    public static string Camel(string pascalName)
    {
        return char.ToLowerInvariant(pascalName[0]) + pascalName[1..];
    }

    // A parameter name for a constructor or factory.
    public static string Parameter(string pascalName)
    {
        var name = Camel(pascalName);
        return Keywords.Contains(name) ? "@" + name : name;
    }

    public static bool IsParameterName(string name)
    {
        return name.Length > 0
            && char.IsLower(name[0])
            && name.All(char.IsAsciiLetterOrDigit)
            && !Keywords.Contains(name);
    }

    // A local variable that never collides with a generated name.
    public static string Local(string pascalName)
    {
        var name = Camel(pascalName);
        return Keywords.Contains(name) || GeneratedNames.Contains(name) ? name + "Value" : name;
    }

    // "OwnerSimulationSnapshot" becomes "Owner simulation snapshot".
    public static string Display(string pascalName)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < pascalName.Length; index++)
        {
            var character = pascalName[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append(' ').Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    public static string FloatLiteral(double value)
    {
        return ((float)value).ToString("R", CultureInfo.InvariantCulture) + "f";
    }

    public static string IntegerLiteral(double value)
    {
        return ((long)value).ToString(CultureInfo.InvariantCulture);
    }
}
