using System.Text;

namespace Mmorpg.Tools.ProtocolGenerator;

// Builds C# source with four-space indentation and LF line endings.
internal sealed class CSharpWriter
{
    private readonly StringBuilder builder = new();
    private int indentation;
    private bool pendingBlankLine;
    private bool atBlockStart;

    public CSharpWriter Line(string text)
    {
        if (pendingBlankLine)
        {
            builder.Append('\n');
            pendingBlankLine = false;
        }

        if (text.Length > 0)
        {
            builder.Append(' ', indentation * 4).Append(text);
        }

        builder.Append('\n');
        atBlockStart = false;
        return this;
    }

    // Requests one blank line before the next line: never two, and never right after an
    // opening brace or before a closing one.
    public CSharpWriter BlankLine()
    {
        pendingBlankLine = builder.Length > 0 && !atBlockStart;
        return this;
    }

    public CSharpWriter Open(string header)
    {
        Line(header);
        Line("{");
        indentation++;
        pendingBlankLine = false;
        atBlockStart = true;
        return this;
    }

    public CSharpWriter Close(string suffix = "")
    {
        pendingBlankLine = false;
        indentation--;
        return Line("}" + suffix);
    }

    public CSharpWriter Summary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return this;
        }

        Line("/// <summary>");
        Line("/// " + EscapeXml(text));
        return Line("/// </summary>");
    }

    public override string ToString()
    {
        return builder.ToString();
    }

    public static string Literal(string value)
    {
        var escaped = new StringBuilder("\"");
        foreach (var character in value)
        {
            escaped.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(character) => $"\\u{(int)character:x4}",
                _ => character.ToString()
            });
        }

        return escaped.Append('"').ToString();
    }

    private static string EscapeXml(string text)
    {
        return text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
