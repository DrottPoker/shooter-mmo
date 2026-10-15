using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mmorpg.Tools.ProtocolGenerator;

// Reads realtime-protocol.json strictly and resolves it into a validated ProtocolModel.
public static partial class ProtocolSchemaLoader
{
    private const int FrameHeaderSize = 7;
    private const string ClassPrefix = "Realtime";

    public static ProtocolModel Load(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException exception)
        {
            throw new ProtocolSchemaException("$", "is not valid JSON: " + exception.Message);
        }

        using (document)
        {
            var root = SchemaNode.Object(document.RootElement, "$");
            root.Ignore("$schema");
            var description = root.RequiredString("description");
            var protocol = root.RequiredObject("protocol");
            var @namespace = protocol.RequiredString("namespace");
            if (!NamespacePattern().IsMatch(@namespace))
            {
                throw protocol.Error("namespace", "must be a dotted C# namespace.");
            }

            var version = protocol.RequiredInteger("version");
            if (version is < 1 or > ushort.MaxValue)
            {
                throw protocol.Error("version", "must be between 1 and 65535.");
            }

            var magic = protocol.RequiredHexUInt32("magic");
            var connectionKey = protocol.RequiredString("connectionKey");
            var maximumPacketSize = ReadSize(protocol, "maximumPacketSize", FrameHeaderSize + 1, int.MaxValue);
            var maximumUnreliablePacketSize = ReadSize(
                protocol,
                "maximumUnreliablePacketSize",
                FrameHeaderSize + 1,
                maximumPacketSize);
            var channelCount = (byte)ReadSize(protocol, "channelCount", 1, byte.MaxValue);
            var channels = ReadChannels(protocol, channelCount);
            protocol.EnsureFullyRead();

            var limits = ReadLimits(root);
            var enums = ReadEnums(root);
            var messages = ReadMessages(root, channels);
            root.EnsureFullyRead();

            EnsureUniqueTypeNames(enums, messages);
            return new ProtocolModel(
                description,
                @namespace,
                (ushort)version,
                magic,
                connectionKey,
                maximumPacketSize,
                maximumUnreliablePacketSize,
                channelCount,
                channels,
                limits,
                enums,
                messages);
        }
    }

    private static int ReadSize(SchemaNode node, string name, int minimum, int maximum)
    {
        var value = node.RequiredInteger(name);
        if (value < minimum || value > maximum)
        {
            throw node.Error(name, $"must be between {minimum} and {maximum}.");
        }

        return (int)value;
    }

    private static IReadOnlyList<ChannelDefinition> ReadChannels(SchemaNode protocol, byte channelCount)
    {
        var channels = new List<ChannelDefinition>();
        foreach (var node in protocol.RequiredObjects("channels"))
        {
            var name = RequireMemberName(node, "name");
            var id = node.RequiredInteger("id");
            if (id < 0 || id >= channelCount)
            {
                throw node.Error("id", $"must be below the channel count {channelCount}.");
            }

            channels.Add(new ChannelDefinition(name, (byte)id, node.RequiredString("description")));
            node.EnsureFullyRead();
        }

        EnsureUnique(protocol, "channels", channels.Select(channel => channel.Name));
        return channels;
    }

    private static IReadOnlyList<LimitDefinition> ReadLimits(SchemaNode root)
    {
        var limits = new List<LimitDefinition>();
        foreach (var node in root.RequiredObjects("limits"))
        {
            var name = RequireMemberName(node, "name");
            var value = node.RequiredInteger("value");
            if (value is < 1 or > ushort.MaxValue)
            {
                throw node.Error("value", "must be between 1 and 65535.");
            }

            var visibility = node.RequiredString("visibility") switch
            {
                "public" => LimitVisibility.Public,
                "protocol" => LimitVisibility.Protocol,
                "private" => LimitVisibility.Private,
                _ => throw node.Error("visibility", "must be public, protocol, or private.")
            };
            limits.Add(new LimitDefinition(name, (int)value, visibility, node.RequiredString("description")));
            node.EnsureFullyRead();
        }

        EnsureUnique(root, "limits", limits.Select(limit => limit.Name));
        return limits;
    }

    private static IReadOnlyList<EnumDefinition> ReadEnums(SchemaNode root)
    {
        var enums = new List<EnumDefinition>();
        foreach (var node in root.RequiredObjects("enums"))
        {
            var name = RequireMemberName(node, "name");
            var family = RequireMemberName(node, "family");
            var flags = node.OptionalBoolean("flags", false);
            var description = node.RequiredString("description");
            var values = new List<EnumValueDefinition>();
            foreach (var valueNode in node.RequiredObjects("values"))
            {
                var valueName = RequireMemberName(valueNode, "name");
                var value = valueNode.RequiredInteger("value");
                if (value is < 0 or > byte.MaxValue)
                {
                    throw valueNode.Error("value", "must fit in one byte.");
                }

                if (flags && value != 0 && (value & (value - 1)) != 0)
                {
                    throw valueNode.Error("value", "must be zero or a single bit in a flags enum.");
                }

                values.Add(new EnumValueDefinition(valueName, (byte)value, valueNode.OptionalString("description")));
                valueNode.EnsureFullyRead();
            }

            if (values.Count == 0)
            {
                throw node.Error("values", "must contain at least one value.");
            }

            EnsureUnique(node, "values", values.Select(value => value.Name));
            EnsureUnique(node, "values", values.Select(value => value.Value.ToString()));
            enums.Add(new EnumDefinition(name, ClassPrefix + name, family, flags, description, values));
            node.EnsureFullyRead();
        }

        EnsureUnique(root, "enums", enums.Select(definition => definition.Name));
        return enums;
    }

    private static IReadOnlyList<MessageDefinition> ReadMessages(
        SchemaNode root,
        IReadOnlyList<ChannelDefinition> channels)
    {
        var messages = new List<MessageDefinition>();
        foreach (var node in root.RequiredObjects("messages"))
        {
            var name = RequireMemberName(node, "name");
            var id = node.RequiredInteger("id");
            if (id is < 1 or > byte.MaxValue)
            {
                throw node.Error("id", "must be between 1 and 255.");
            }

            var family = RequireMemberName(node, "family");
            var direction = node.RequiredString("direction") switch
            {
                "clientToServer" => MessageDirection.ClientToServer,
                "serverToClient" => MessageDirection.ServerToClient,
                _ => throw node.Error("direction", "must be clientToServer or serverToClient.")
            };
            var channelName = node.RequiredString("channel");
            var channel = channels.FirstOrDefault(candidate => candidate.Name == channelName)
                ?? throw node.Error("channel", $"names no channel in protocol.channels: '{channelName}'.");
            var delivery = node.RequiredString("delivery") switch
            {
                "reliableOrdered" => MessageDelivery.ReliableOrdered,
                "sequenced" => MessageDelivery.Sequenced,
                "unreliable" => MessageDelivery.Unreliable,
                _ => throw node.Error("delivery", "must be reliableOrdered, sequenced, or unreliable.")
            };
            var codec = node.RequiredString("codec") switch
            {
                "handWritten" => MessageCodec.HandWritten,
                _ => throw node.Error("codec", "must be handWritten.")
            };
            messages.Add(new MessageDefinition(
                name,
                (byte)id,
                family,
                direction,
                channel,
                delivery,
                codec,
                node.RequiredString("description")));
            node.EnsureFullyRead();
        }

        EnsureUnique(root, "messages", messages.Select(message => message.Name));
        EnsureUnique(root, "messages", messages.Select(message => "id " + message.Id));
        return messages;
    }

    private static void EnsureUniqueTypeNames(
        IReadOnlyList<EnumDefinition> enums,
        IReadOnlyList<MessageDefinition> messages)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { "MessageType" };
        foreach (var name in enums.Select(definition => definition.Name).Concat(messages.Select(message => message.Name)))
        {
            if (!names.Add(name))
            {
                throw new ProtocolSchemaException("$", $"type name '{name}' is used more than once.");
            }
        }
    }

    private static string RequireMemberName(SchemaNode node, string property)
    {
        var name = node.RequiredString(property);
        if (!MemberNamePattern().IsMatch(name))
        {
            throw node.Error(property, $"'{name}' must be a PascalCase C# identifier.");
        }

        return name;
    }

    private static void EnsureUnique(SchemaNode node, string property, IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                throw node.Error(property, $"'{value}' appears more than once.");
            }
        }
    }

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$")]
    private static partial Regex MemberNamePattern();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*(\\.[A-Z][A-Za-z0-9]*)*$")]
    private static partial Regex NamespacePattern();
}
