using System.Globalization;
using System.Text.Json;

namespace Mmorpg.Tools.ProtocolGenerator;

// A strict view of one JSON object in the schema. Every property must be read or the
// object is rejected, so a misspelled property never passes silently.
internal sealed class SchemaNode
{
    private readonly JsonElement element;
    private readonly HashSet<string> readProperties = new(StringComparer.Ordinal);

    private SchemaNode(JsonElement element, string path)
    {
        this.element = element;
        Path = path;
    }

    public string Path { get; }

    public static SchemaNode Object(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ProtocolSchemaException(path, "must be an object.");
        }

        return new SchemaNode(element, path);
    }

    public bool Has(string name)
    {
        return element.TryGetProperty(name, out _);
    }

    public string RequiredString(string name)
    {
        return OptionalString(name) ?? throw Missing(name);
    }

    public string? OptionalString(string name)
    {
        if (!TryRead(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ProtocolSchemaException(Child(name), "must be a non-empty string.");
        }

        return value.GetString()!;
    }

    public long RequiredInteger(string name)
    {
        return OptionalInteger(name) ?? throw Missing(name);
    }

    public long? OptionalInteger(string name)
    {
        if (!TryRead(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw new ProtocolSchemaException(Child(name), "must be an integer.");
        }

        return number;
    }

    public bool OptionalBoolean(string name, bool fallback)
    {
        if (!TryRead(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ProtocolSchemaException(Child(name), "must be true or false.")
        };
    }

    // A hexadecimal string such as "0x4F4D4D53".
    public uint RequiredHexUInt32(string name)
    {
        var text = RequiredString(name);
        if (!text.StartsWith("0x", StringComparison.Ordinal)
            || !uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            throw new ProtocolSchemaException(Child(name), "must be a hexadecimal 32-bit value such as 0x4F4D4D53.");
        }

        return value;
    }

    public IReadOnlyList<SchemaNode> RequiredObjects(string name)
    {
        return OptionalObjects(name) ?? throw Missing(name);
    }

    public IReadOnlyList<SchemaNode>? OptionalObjects(string name)
    {
        if (!TryRead(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new ProtocolSchemaException(Child(name), "must be an array.");
        }

        return value.EnumerateArray()
            .Select((item, index) => Object(item, $"{Child(name)}[{index}]"))
            .ToArray();
    }

    public SchemaNode RequiredObject(string name)
    {
        if (!TryRead(name, out var value))
        {
            throw Missing(name);
        }

        return Object(value, Child(name));
    }

    public double? OptionalNumber(string name)
    {
        if (!TryRead(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            throw new ProtocolSchemaException(Child(name), "must be a finite number.");
        }

        return number;
    }

    // A property that is either a string, returned, or an integer, returned in number.
    public string? OptionalStringOrNull(string name, out long? number)
    {
        number = null;
        if (!TryRead(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer))
        {
            number = integer;
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }

        throw new ProtocolSchemaException(Child(name), "must be an integer or the name of a limit.");
    }

    public IReadOnlyList<string>? OptionalStrings(string name)
    {
        if (!TryRead(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array
            || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            throw new ProtocolSchemaException(Child(name), "must be an array of strings.");
        }

        return value.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    // Reads the property as an object when it is one; other kinds leave it for another reader.
    public bool TryReadObject(string name, out SchemaNode node)
    {
        node = null!;
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        readProperties.Add(name);
        node = Object(value, Child(name));
        return true;
    }

    // Marks a property as known without interpreting it, such as "$schema".
    public void Ignore(string name)
    {
        readProperties.Add(name);
    }

    // Rejects every property that no reader asked for.
    public void EnsureFullyRead()
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!readProperties.Contains(property.Name))
            {
                throw new ProtocolSchemaException(Child(property.Name), "is not a known schema property.");
            }
        }
    }

    public ProtocolSchemaException Error(string message)
    {
        return new ProtocolSchemaException(Path, message);
    }

    public ProtocolSchemaException Error(string property, string message)
    {
        return new ProtocolSchemaException(Child(property), message);
    }

    private bool TryRead(string name, out JsonElement value)
    {
        readProperties.Add(name);
        if (!element.TryGetProperty(name, out value) || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return true;
    }

    private ProtocolSchemaException Missing(string name)
    {
        return new ProtocolSchemaException(Child(name), "is required.");
    }

    private string Child(string name)
    {
        return Path + "." + name;
    }
}
