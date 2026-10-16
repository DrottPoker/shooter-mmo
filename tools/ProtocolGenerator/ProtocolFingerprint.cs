using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mmorpg.Tools.ProtocolGenerator;

// The SHA-256 fingerprint of the wire contract: everything that decides which bytes a peer
// writes and which packets it accepts. Names, descriptions, and the shape of the C# API are
// left out, so a rename needs no new version, while a changed type, order, rule, limit, id,
// or delivery does. Every limit counts, because hand-written rules may use any of them.
public static class ProtocolFingerprint
{
    public static string Compute(ProtocolModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Describe(model))));
    }

    // The canonical text the fingerprint hashes, one line per protocol element.
    public static string Describe(ProtocolModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"protocol magic={model.Magic:X8} key={model.ConnectionKey}")
            .Append(CultureInfo.InvariantCulture, $" packet={model.MaximumPacketSize} unreliable={model.MaximumUnreliablePacketSize}")
            .Append(CultureInfo.InvariantCulture, $" channels={model.ChannelCount}\n");
        foreach (var limit in model.Limits.OrderBy(limit => limit.Name, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"limit {limit.Name}={limit.Value}\n");
        }

        foreach (var message in model.Messages.OrderBy(message => message.Id))
        {
            text.Append(CultureInfo.InvariantCulture, $"message {message.Id} {message.Direction} channel={message.Channel.Id} {message.Delivery}")
                .Append(message.Rules ? " rules" : string.Empty)
                .Append(' ')
                .Append(Fields(message.Fields));
            if (message.Union is not null)
            {
                text.Append(' ').Append(Union(message.Union, message.Fields));
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static string Fields(IReadOnlyList<FieldDefinition> fields)
    {
        return "[" + string.Join("; ", fields.Select(field => Field(field, fields))) + "]";
    }

    private static string Field(FieldDefinition field, IReadOnlyList<FieldDefinition> siblings)
    {
        var text = new StringBuilder(Type(field.Type));
        text.Append(Rules(field.Rules, siblings, field.Type));
        if (field.Presence == FieldPresence.Conditional)
        {
            text.Append(CultureInfo.InvariantCulture, $" when={(field.ConditionValue ? string.Empty : "!")}{IndexOf(siblings, field.ConditionField!)}");
        }
        else if (field.Presence == FieldPresence.Optional)
        {
            text.Append(" optional");
        }

        if (field.AcceptBlankOnDecode)
        {
            text.Append(" acceptBlankOnDecode");
        }

        return text.ToString();
    }

    private static string Type(WireType type)
    {
        return type switch
        {
            PrimitiveType primitive => primitive.Kind.ToString().ToLowerInvariant(),
            StringWireType text => $"string({text.MaximumLength.Value}{(text.AllowBlank ? ",blank" : string.Empty)})",
            EnumWireType enumType => (enumType.Definition.Flags ? "flags(" : "enum(")
                + string.Join(",", enumType.Definition.Values.Select(value => value.Value).OrderBy(value => value)) + ")",
            StructWireType structType => "struct" + (structType.Definition.Rules ? " rules" : string.Empty)
                + Fields(structType.Definition.Fields),
            ArrayWireType array => $"array({array.MinimumCount}..{array.MaximumCount.Value} "
                + Type(array.Element.Type) + Rules(array.Element.Rules, [], array.Element.Type) + ")",
            BitsWireType bits => $"bits8({bits.Bits.Count})",
            _ => throw new InvalidOperationException($"Type {type} has no fingerprint.")
        };
    }

    private static string Rules(ValueRules rules, IReadOnlyList<FieldDefinition> siblings, WireType type)
    {
        var text = new StringBuilder();
        Bound(text, "min", rules.Minimum);
        Bound(text, "max", rules.Maximum);
        Bound(text, "exclusiveMin", rules.ExclusiveMinimum);
        Bound(text, "exclusiveMax", rules.ExclusiveMaximum);
        if (rules.NonEmpty)
        {
            text.Append(" nonEmpty");
        }

        if (rules.LessThan is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $" lessThan={IndexOf(siblings, rules.LessThan)}");
        }

        if (rules.Distinct)
        {
            text.Append(" distinct");
        }

        if (rules.DistinctBy is not null)
        {
            var elementFields = ((StructWireType)((ArrayWireType)type).Element.Type).Definition.Fields;
            text.Append(CultureInfo.InvariantCulture, $" distinctBy={IndexOf(elementFields, rules.DistinctBy)}");
        }

        if (rules.Format != StringFormat.Any)
        {
            text.Append(" format=").Append(rules.Format);
        }

        if (rules.OneOf is not null)
        {
            text.Append(" oneOf=").Append(string.Join("|", rules.OneOf));
        }

        return text.ToString();
    }

    private static void Bound(StringBuilder text, string name, NumericBound? bound)
    {
        if (bound is not null)
        {
            text.Append(' ').Append(name).Append('=').Append(bound.Value.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    private static string Union(UnionDefinition union, IReadOnlyList<FieldDefinition> fields)
    {
        var pool = union.Pool.Select(entry => entry.Field).ToArray();
        var properties = fields.Concat(pool).ToArray();
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"variants discriminator={IndexOf(fields, union.Discriminator)} pool[");
        text.Append(string.Join("; ", union.Pool.Select(entry => Type(entry.Field.Type) + " absent=" + (entry.Field.Type is ArrayWireType ? "empty" : entry.AbsentValue))))
            .Append(']');
        foreach (var unionCase in union.Cases)
        {
            text.Append(" case(")
                .Append(string.Join(",", unionCase.Kinds.Select(kind => kind.Value.Value).OrderBy(value => value)))
                .Append(") common[")
                .Append(string.Join("; ", unionCase.CommonOverrides.Select(entry => Entry(entry, fields, properties))))
                .Append("] entries[")
                .Append(string.Join("; ", unionCase.Entries.Select(entry => entry switch
                {
                    UnionGroupEntry group => $"group presence={IndexOf(pool, group.PresenceField)} ["
                        + string.Join("; ", group.Fields.Select(member => Entry(member, pool, properties))) + "]",
                    UnionFieldEntry field => Entry(field, pool, properties),
                    _ => throw new InvalidOperationException($"Entry {entry} has no fingerprint.")
                })))
                .Append(']');
        }

        return text.ToString();
    }

    // A case entry names its field by index among its owners, and a lessThan by index among
    // the message fields followed by the variant fields.
    private static string Entry(UnionFieldEntry entry, IReadOnlyList<FieldDefinition> owners, IReadOnlyList<FieldDefinition> properties)
    {
        var text = new StringBuilder(IndexOf(owners, entry.Field.Name).ToString(CultureInfo.InvariantCulture));
        text.Append(Rules(entry.Field.Rules, properties, entry.Field.Type));
        if (entry.FixedValue is not null)
        {
            text.Append(" fixed=").Append(entry.FixedValue);
        }

        return text.ToString();
    }

    private static int IndexOf(IReadOnlyList<FieldDefinition> fields, string name)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            if (fields[index].Name == name)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Field '{name}' is missing.");
    }
}
