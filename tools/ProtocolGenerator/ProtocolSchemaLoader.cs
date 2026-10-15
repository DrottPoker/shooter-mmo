using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mmorpg.Tools.ProtocolGenerator;

// Reads realtime-protocol.json strictly and resolves it into a validated ProtocolModel.
public static partial class ProtocolSchemaLoader
{
    private const string ClassPrefix = "Realtime";

    private static readonly Dictionary<string, PrimitiveKind> Primitives = new(StringComparer.Ordinal)
    {
        ["u8"] = PrimitiveKind.U8,
        ["u16"] = PrimitiveKind.U16,
        ["u32"] = PrimitiveKind.U32,
        ["u64"] = PrimitiveKind.U64,
        ["i32"] = PrimitiveKind.I32,
        ["i64"] = PrimitiveKind.I64,
        ["f32"] = PrimitiveKind.F32,
        ["bool"] = PrimitiveKind.Bool,
        ["guid"] = PrimitiveKind.Guid
    };

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
            var maximumPacketSize = ReadSize(protocol, "maximumPacketSize", WireSizes.FrameHeaderSize + 1, int.MaxValue);
            var maximumUnreliablePacketSize = ReadSize(
                protocol,
                "maximumUnreliablePacketSize",
                WireSizes.FrameHeaderSize + 1,
                maximumPacketSize);
            var channelCount = (byte)ReadSize(protocol, "channelCount", 1, byte.MaxValue);
            var channels = ReadChannels(protocol, channelCount);
            protocol.EnsureFullyRead();

            var limits = ReadLimits(root);
            var enums = ReadEnums(root);
            var scope = new TypeScope(limits, enums)
            {
                MaximumPacketSize = maximumPacketSize,
                MaximumUnreliablePacketSize = maximumUnreliablePacketSize
            };
            var structs = ReadStructs(root, scope);
            var messages = ReadMessages(root, channels, scope);
            root.EnsureFullyRead();

            var model = new ProtocolModel(
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
                structs,
                messages);
            Validate(model);
            return model;
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

    // Structs may reference structs defined later, so every struct is declared before any
    // field is read.
    private static IReadOnlyList<StructDefinition> ReadStructs(SchemaNode root, TypeScope scope)
    {
        var nodes = root.OptionalObjects("structs") ?? [];
        var structs = new List<StructDefinition>();
        foreach (var node in nodes)
        {
            var name = RequireMemberName(node, "name");
            var definition = new StructDefinition(
                name,
                ClassPrefix + name,
                RequireMemberName(node, "family"),
                node.RequiredString("description"),
                node.OptionalBoolean("rules", false),
                OptionalMemberName(node, "sizeConstant"));
            if (!scope.Structs.TryAdd(name, definition))
            {
                throw node.Error("name", $"'{name}' appears more than once.");
            }

            structs.Add(definition);
        }

        for (var index = 0; index < nodes.Count; index++)
        {
            structs[index].Fields.AddRange(ReadFields(nodes[index], scope));
            var blank = structs[index].Fields.FirstOrDefault(field => field.AcceptBlankOnDecode);
            if (blank is not null)
            {
                throw nodes[index].Error("fields", $"'{blank.Name}' cannot use blank acceptOnDecode, which applies to message fields only.");
            }

            nodes[index].EnsureFullyRead();
        }

        // Sizes recurse into structs, so a cycle must fail before any size is computed.
        foreach (var definition in structs)
        {
            EnsureAcyclic(definition, []);
        }

        return structs;
    }

    private static IReadOnlyList<MessageDefinition> ReadMessages(
        SchemaNode root,
        IReadOnlyList<ChannelDefinition> channels,
        TypeScope scope)
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
                "generated" => MessageCodec.Generated,
                _ => throw node.Error("codec", "must be handWritten or generated.")
            };
            var messageDescription = node.RequiredString("description");
            var className = ClassPrefix + name;
            MessageDefinition message;
            if (codec == MessageCodec.HandWritten)
            {
                message = new MessageDefinition(
                    name,
                    (byte)id,
                    family,
                    direction,
                    channel,
                    delivery,
                    codec,
                    messageDescription,
                    MessageApi.Class,
                    className,
                    CSharpNames.Camel(name),
                    false,
                    null,
                    null,
                    false,
                    []);
            }
            else
            {
                var api = (node.OptionalString("api") ?? "class") switch
                {
                    "class" => MessageApi.Class,
                    "unwrap" => MessageApi.Unwrap,
                    "empty" => MessageApi.Empty,
                    _ => throw node.Error("api", "must be class, unwrap, or empty.")
                };
                var fields = api == MessageApi.Empty ? [] : ReadFields(node, scope);
                EnsureFlatBlankStructs(node, fields);
                if (api == MessageApi.Unwrap && (fields.Count != 1 || fields[0].Type is BitsWireType))
                {
                    throw node.Error("api", "unwrap requires exactly one field that is not bits8.");
                }

                var union = node.TryReadObject("variants", out var variantsNode) ? ReadUnion(variantsNode, fields, scope) : null;
                if (union is not null && api != MessageApi.Class)
                {
                    throw node.Error("api", "only a class message can have variants.");
                }

                var expandedEncode = (node.OptionalString("encodeParameters") ?? "value") switch
                {
                    "value" => false,
                    "expanded" => true,
                    _ => throw node.Error("encodeParameters", "must be value or expanded.")
                };
                if (expandedEncode && (api != MessageApi.Unwrap || fields[0].Type is not StructWireType))
                {
                    throw node.Error("encodeParameters", "expanded requires an unwrapped struct field.");
                }

                var rules = node.OptionalBoolean("rules", false);
                var explicitParameter = node.OptionalString("parameter");
                if (api == MessageApi.Empty && (rules || explicitParameter is not null))
                {
                    throw node.Error("api", "an empty message takes no rules or parameter.");
                }

                var parameter = explicitParameter
                    ?? CSharpNames.Camel(api == MessageApi.Unwrap ? fields[0].Name : name);
                message = new MessageDefinition(
                    name,
                    (byte)id,
                    family,
                    direction,
                    channel,
                    delivery,
                    codec,
                    messageDescription,
                    api,
                    className,
                    parameter,
                    rules,
                    OptionalMemberName(node, "sizeConstant"),
                    OptionalMemberName(node, "emptySizeConstant"),
                    node.OptionalBoolean("runtimeSizeCheck", false),
                    fields,
                    union)
                {
                    ExpandedEncode = expandedEncode
                };
                CheckMessageSizes(node, message, scope);
            }

            messages.Add(message);
            node.EnsureFullyRead();
        }

        EnsureUnique(root, "messages", messages.Select(message => message.Name));
        EnsureUnique(root, "messages", messages.Select(message => "id " + message.Id));
        return messages;
    }

    private static List<FieldDefinition> ReadFields(SchemaNode owner, TypeScope scope)
    {
        var fields = new List<FieldDefinition>();
        foreach (var node in owner.RequiredObjects("fields"))
        {
            fields.Add(ReadField(node, scope));
            node.EnsureFullyRead();
        }

        if (fields.Count == 0)
        {
            throw owner.Error("fields", "must contain at least one field.");
        }

        var memberNames = fields.SelectMany(field => field.Type is BitsWireType bits
            ? bits.Bits.Select(bit => bit.Name)
            : [field.Name]);
        EnsureUnique(owner, "fields", memberNames);
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            if (field.Presence == FieldPresence.Conditional
                && !fields.Take(index).Any(candidate => candidate.Name == field.ConditionField
                    && candidate.Type is PrimitiveType { Kind: PrimitiveKind.Bool }))
            {
                throw owner.Error("fields", $"'{field.Name}' must depend on an earlier bool field, not '{field.ConditionField}'.");
            }
        }

        foreach (var field in fields.Where(field => field.Rules.LessThan is not null))
        {
            var target = fields.FirstOrDefault(candidate => candidate.Name == field.Rules.LessThan);
            if (target is null || target.Type is not PrimitiveType { Kind: not (PrimitiveKind.F32 or PrimitiveKind.Bool or PrimitiveKind.Guid) })
            {
                throw owner.Error("fields", $"'{field.Name}' must be less than an integer field, not '{field.Rules.LessThan}'.");
            }
        }

        return fields;
    }

    // A struct read with blank acceptOnDecode relaxes its own text fields, so it must not nest
    // structs or arrays that would still validate strictly.
    private static void EnsureFlatBlankStructs(SchemaNode owner, IReadOnlyList<FieldDefinition> fields)
    {
        foreach (var field in fields.Where(field => field.AcceptBlankOnDecode && field.Type is StructWireType))
        {
            var definition = ((StructWireType)field.Type).Definition;
            if (definition.Fields.Any(member => member.Type is StructWireType or ArrayWireType))
            {
                throw owner.Error("fields", $"'{field.Name}' accepts blank text on decode, so struct '{definition.Name}' must not contain struct or array fields.");
            }
        }
    }

    private static UnionDefinition ReadUnion(SchemaNode node, IReadOnlyList<FieldDefinition> commonFields, TypeScope scope)
    {
        var discriminatorName = RequireMemberName(node, "discriminator");
        var discriminator = commonFields.FirstOrDefault(field => field.Name == discriminatorName);
        if (discriminator?.Type is not EnumWireType discriminatorType)
        {
            throw node.Error("discriminator", $"must name an enum field among the common fields, not '{discriminatorName}'.");
        }

        var pool = new List<UnionPoolField>();
        foreach (var fieldNode in node.RequiredObjects("fields"))
        {
            var absentText = fieldNode.OptionalStringOrNull("absent", out var absentNumber);
            var field = ReadField(fieldNode, scope);
            fieldNode.EnsureFullyRead();
            if (field.Rules != ValueRules.None || field.Presence != FieldPresence.Always)
            {
                throw fieldNode.Error("rules belong to the case entries, not to the variant field.");
            }

            if (field.AcceptBlankOnDecode)
            {
                throw fieldNode.Error("blank", "acceptOnDecode applies to common message fields only, not to variant fields.");
            }

            if (absentText is not null || (absentNumber is not null && !IsInteger(field.Type)))
            {
                throw fieldNode.Error("absent", "must be an integer for an integer field.");
            }

            pool.Add(new UnionPoolField(
                field,
                absentNumber?.ToString(CultureInfo.InvariantCulture) ?? AbsentValue(field.Type)));
        }

        EnsureUnique(node, "fields", pool.Select(field => field.Field.Name).Concat(commonFields.Select(field => field.Name)));
        var cases = new List<UnionCase>();
        var coveredKinds = new HashSet<string>(StringComparer.Ordinal);
        var factories = new HashSet<string>(StringComparer.Ordinal);
        var usedPoolFields = new HashSet<string>(StringComparer.Ordinal);
        var poolFields = pool.Select(field => field.Field).ToArray();
        var overridableFields = commonFields.Where(field => field.Name != discriminatorName).ToArray();
        foreach (var caseNode in node.RequiredObjects("cases"))
        {
            var kinds = new List<UnionKind>();
            foreach (var kindNode in caseNode.RequiredObjects("kinds"))
            {
                var kindName = RequireMemberName(kindNode, "kind");
                var value = discriminatorType.Definition.Values.FirstOrDefault(candidate => candidate.Name == kindName)
                    ?? throw kindNode.Error("kind", $"'{kindName}' is not a value of {discriminatorType.Definition.Name}.");
                if (!coveredKinds.Add(kindName))
                {
                    throw kindNode.Error("kind", $"'{kindName}' has more than one case.");
                }

                var factory = RequireMemberName(kindNode, "factory");
                if (!factories.Add(factory))
                {
                    throw kindNode.Error("factory", $"'{factory}' is used more than once.");
                }

                kinds.Add(new UnionKind(value, factory));
                kindNode.EnsureFullyRead();
            }

            var overrides = (caseNode.OptionalObjects("common") ?? [])
                .Select(entryNode => ReadUnionEntry(entryNode, overridableFields, scope))
                .ToArray();
            if (overrides.Any(entry => entry.ParameterDefault is not null || entry.ParameterName is not null))
            {
                throw caseNode.Error("common", "common fields keep their parameter names and have no defaults.");
            }

            var entries = new List<UnionEntry>();
            foreach (var entryNode in caseNode.RequiredObjects("fields"))
            {
                if (!entryNode.Has("group"))
                {
                    entries.Add(ReadUnionEntry(entryNode, poolFields, scope));
                    continue;
                }

                var presence = RequireMemberName(entryNode, "presence");
                var members = entryNode.RequiredObjects("group")
                    .Select(memberNode => ReadUnionEntry(memberNode, poolFields, scope))
                    .ToArray();
                if (members.All(member => member.Field.Name != presence) || members.Any(member => member.FixedValue is not null))
                {
                    throw entryNode.Error("presence", "must name a group member, and group members cannot be fixed.");
                }

                entries.Add(new UnionGroupEntry(presence, members));
                entryNode.EnsureFullyRead();
            }

            var caseFields = entries.SelectMany(entry => entry is UnionGroupEntry group ? group.Fields : [(UnionFieldEntry)entry])
                .ToArray();
            EnsureUnique(caseNode, "fields", caseFields.Select(entry => entry.Field.Name));
            EnsureUnique(caseNode, "common", overrides.Select(entry => entry.Field.Name));
            usedPoolFields.UnionWith(caseFields.Select(entry => entry.Field.Name));
            var parameterFields = caseFields.Where(entry => entry.FixedValue is null).ToArray();
            var firstDefault = Array.FindIndex(parameterFields, entry => entry.ParameterDefault is not null);
            if (firstDefault >= 0 && parameterFields.Skip(firstDefault).Any(entry => entry.ParameterDefault is null))
            {
                throw caseNode.Error("fields", "parameters with a default must come after every parameter without one.");
            }

            EnsureUnique(
                caseNode,
                "fields",
                overrides.Where(entry => entry.FixedValue is null)
                    .Concat(parameterFields)
                    .Select(entry => entry.ParameterName ?? CSharpNames.Parameter(entry.Field.Name)));
            cases.Add(new UnionCase(kinds, entries, overrides));
            caseNode.EnsureFullyRead();
        }

        var uncovered = discriminatorType.Definition.Values.FirstOrDefault(value => !coveredKinds.Contains(value.Name));
        if (uncovered is not null)
        {
            throw node.Error("cases", $"{discriminatorType.Definition.Name}.{uncovered.Name} has no case.");
        }

        var unused = pool.FirstOrDefault(field => !usedPoolFields.Contains(field.Field.Name));
        if (unused is not null)
        {
            throw node.Error("fields", $"'{unused.Field.Name}' is in no case.");
        }

        return new UnionDefinition(discriminatorName, pool, cases);
    }

    private static UnionFieldEntry ReadUnionEntry(SchemaNode node, IReadOnlyList<FieldDefinition> fields, TypeScope scope)
    {
        var name = RequireMemberName(node, "field");
        var field = fields.FirstOrDefault(candidate => candidate.Name == name)
            ?? throw node.Error("field", $"'{name}' is not a field that this case may use.");
        var rules = ReadRules(node, field.Type, scope);
        var fixedText = node.OptionalStringOrNull("fixed", out var fixedNumber);
        string? fixedValue = null;
        if (fixedNumber is not null && IsInteger(field.Type))
        {
            fixedValue = fixedNumber.Value.ToString(CultureInfo.InvariantCulture);
        }
        else if (fixedText == "empty" && field.Type is PrimitiveType { Kind: PrimitiveKind.Guid })
        {
            fixedValue = "Guid.Empty";
        }
        else if (fixedText is not null || fixedNumber is not null)
        {
            throw node.Error("fixed", "must be an integer for an integer field, or \"empty\" for a guid.");
        }

        var parameterName = node.OptionalString("parameter");
        if (parameterName is not null && !CSharpNames.IsParameterName(parameterName))
        {
            throw node.Error("parameter", "must be a camelCase C# identifier.");
        }

        var parameterDefault = ReadParameterDefault(node, field.Type);
        if (fixedValue is not null && (parameterName is not null || parameterDefault is not null || rules != ValueRules.None))
        {
            throw node.Error("fixed", "a fixed field has no rules, parameter, or default.");
        }

        node.EnsureFullyRead();
        return new UnionFieldEntry(field with { Rules = rules }, fixedValue, parameterName, parameterDefault);
    }

    // A factory parameter default: an integer, "default", or the name of an enum value.
    private static string? ReadParameterDefault(SchemaNode node, WireType type)
    {
        var text = node.OptionalStringOrNull("parameterDefault", out var number);
        if (number is not null && IsInteger(type))
        {
            return number.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (text == "default")
        {
            return "default";
        }

        if (text is not null && type is EnumWireType enumType && enumType.Definition.Values.Any(value => value.Name == text))
        {
            return enumType.Definition.ClassName + "." + text;
        }

        if (text is null && number is null)
        {
            return null;
        }

        throw node.Error("parameterDefault", "must be an integer, \"default\", or a value of the field's enum.");
    }

    private static bool IsInteger(WireType type)
    {
        return type is PrimitiveType { Kind: not (PrimitiveKind.F32 or PrimitiveKind.Bool or PrimitiveKind.Guid) };
    }

    // The C# value a variant field has in cases that do not carry it.
    private static string AbsentValue(WireType type)
    {
        return type switch
        {
            PrimitiveType { Kind: PrimitiveKind.Guid } => "Guid.Empty",
            PrimitiveType { Kind: PrimitiveKind.Bool } => "false",
            PrimitiveType => "0",
            EnumWireType => "default",
            StringWireType => "string.Empty",
            ArrayWireType array => $"Array.Empty<{CodecEmitter.TypeName(array.Element.Type)}>()",
            _ => "null"
        };
    }

    private static FieldDefinition ReadField(SchemaNode node, TypeScope scope)
    {
        var name = RequireMemberName(node, "name");
        var description = node.OptionalString("description");
        var typeName = node.RequiredString("type");
        WireType type = typeName switch
        {
            "array" => ReadArray(node, scope),
            "bits8" => ReadBits(node),
            _ => scope.Resolve(node, "type", typeName)
        };
        var field = new FieldDefinition(name, description, type, ReadRules(node, type, scope));
        switch (node.OptionalString("blank"))
        {
            case null:
                break;
            case "allow" when type is StringWireType text:
                field = field with { Type = text with { AllowBlank = true } };
                break;
            case "acceptOnDecode" when type is StringWireType or StructWireType:
                field = field with { AcceptBlankOnDecode = true };
                break;
            default:
                throw node.Error("blank", "must be allow on a string field, or acceptOnDecode on a string or struct field.");
        }

        if (node.OptionalBoolean("optional", false))
        {
            if (field.Type is not (StringWireType { AllowBlank: false } or StructWireType)
                || field.AcceptBlankOnDecode
                || field.Rules.Format != StringFormat.Any
                || field.Rules.OneOf is not null)
            {
                throw node.Error("optional", "applies to a struct, or to a string without blank, format, or oneOf.");
            }

            field = field with { Presence = FieldPresence.Optional };
        }

        if (node.OptionalBoolean("nullAsEmpty", false))
        {
            var optionalText = field.Type is StringWireType && field.Presence == FieldPresence.Optional;
            if (!optionalText && field.Type is not (StringWireType { AllowBlank: true } or ArrayWireType { MinimumCount: 0 }))
            {
                throw node.Error("nullAsEmpty", "applies to an optional string, a string that allows blank text, or an array without minCount.");
            }

            field = field with { NullAsEmpty = true };
        }

        var condition = node.OptionalString("when");
        if (condition is null)
        {
            return field;
        }

        if (type is not StructWireType || field.Presence == FieldPresence.Optional)
        {
            throw node.Error("when", "applies to struct fields that are not optional.");
        }

        var conditionValue = !condition.StartsWith('!');
        return field with
        {
            Presence = FieldPresence.Conditional,
            ConditionField = conditionValue ? condition : condition[1..],
            ConditionValue = conditionValue
        };
    }

    private static ArrayWireType ReadArray(SchemaNode node, TypeScope scope)
    {
        ElementDefinition element;
        if (node.Has("items") && node.TryReadObject("items", out var itemNode))
        {
            var itemType = scope.Resolve(itemNode, "type", itemNode.RequiredString("type"));
            element = new ElementDefinition(itemType, ReadRules(itemNode, itemType, scope));
            itemNode.EnsureFullyRead();
        }
        else
        {
            var itemTypeName = node.RequiredString("items");
            if (itemTypeName == "string")
            {
                throw node.Error("items", "string elements need an items object with their maxLength.");
            }

            var itemType = scope.Resolve(node, "items", itemTypeName);
            element = new ElementDefinition(itemType, ValueRules.None);
        }

        var minimumCount = (int)(node.OptionalInteger("minCount") ?? 0);
        var maximumCount = scope.ReadIntBound(node, "maxCount")
            ?? throw node.Error("maxCount", "is required.");
        if (minimumCount < 0 || minimumCount > maximumCount.Value || maximumCount.Value > byte.MaxValue)
        {
            throw node.Error("maxCount", "requires 0 <= minCount <= maxCount <= 255.");
        }

        return new ArrayWireType(element, minimumCount, maximumCount);
    }

    private static BitsWireType ReadBits(SchemaNode node)
    {
        var bits = new List<BitDefinition>();
        foreach (var bitNode in node.RequiredObjects("bits"))
        {
            bits.Add(new BitDefinition(RequireMemberName(bitNode, "name"), bitNode.OptionalString("description")));
            bitNode.EnsureFullyRead();
        }

        if (bits.Count is 0 or > 8)
        {
            throw node.Error("bits", "must contain between 1 and 8 bits.");
        }

        return new BitsWireType(bits);
    }

    // Reads the value rules that fit the type. Rules that do not fit stay unread, so the
    // strict reader rejects them as unknown properties.
    private static ValueRules ReadRules(SchemaNode node, WireType type, TypeScope scope)
    {
        switch (type)
        {
            case PrimitiveType { Kind: PrimitiveKind.F32 }:
                return ValueRules.None with
                {
                    Minimum = ReadFloatBound(node, "minimum"),
                    Maximum = ReadFloatBound(node, "maximum"),
                    ExclusiveMinimum = ReadFloatBound(node, "exclusiveMinimum"),
                    ExclusiveMaximum = ReadFloatBound(node, "exclusiveMaximum")
                };
            case PrimitiveType { Kind: PrimitiveKind.Guid }:
                return ValueRules.None with { NonEmpty = node.OptionalBoolean("nonEmpty", false) };
            case PrimitiveType { Kind: not PrimitiveKind.Bool } primitive:
                var minimum = ReadIntegerBound(node, "minimum", primitive.Kind, scope);
                var maximum = ReadIntegerBound(node, "maximum", primitive.Kind, scope);
                if (minimum is not null && maximum is not null && minimum.Value > maximum.Value)
                {
                    throw node.Error("minimum", "must not exceed the maximum.");
                }

                return ValueRules.None with
                {
                    Minimum = minimum,
                    Maximum = maximum,
                    LessThan = OptionalMemberName(node, "lessThan")
                };
            case StringWireType:
                var format = (node.OptionalString("format") ?? "any") switch
                {
                    "any" => StringFormat.Any,
                    "guid" => StringFormat.Guid,
                    _ => throw node.Error("format", "must be any or guid.")
                };
                var oneOf = node.OptionalStrings("oneOf");
                if (oneOf is { Count: 0 })
                {
                    throw node.Error("oneOf", "must list at least one value.");
                }

                return ValueRules.None with { Format = format, OneOf = oneOf };
            case ArrayWireType array:
                var distinct = node.OptionalBoolean("distinct", false);
                var distinctBy = OptionalMemberName(node, "distinctBy");
                if (distinct && array.Element.Type is not (PrimitiveType or EnumWireType or StringWireType))
                {
                    throw node.Error("distinct", "applies to arrays of primitives, enums, and strings; use distinctBy for structs.");
                }

                if (distinctBy is not null
                    && (array.Element.Type is not StructWireType elementStruct
                        || elementStruct.Definition.Fields.All(field => field.Name != distinctBy)))
                {
                    throw node.Error("distinctBy", $"must name a field of the array's struct, not '{distinctBy}'.");
                }

                return ValueRules.None with { Distinct = distinct, DistinctBy = distinctBy };
            default:
                return ValueRules.None;
        }
    }

    private static NumericBound? ReadFloatBound(SchemaNode node, string name)
    {
        var value = node.OptionalNumber(name);
        return value is null ? null : new NumericBound(value.Value, null);
    }

    private static NumericBound? ReadIntegerBound(SchemaNode node, string name, PrimitiveKind kind, TypeScope scope)
    {
        var bound = scope.ReadIntBound(node, name);
        if (bound is null)
        {
            return null;
        }

        var (minimum, maximum) = kind switch
        {
            PrimitiveKind.U8 => (0L, byte.MaxValue),
            PrimitiveKind.U16 => (0L, ushort.MaxValue),
            PrimitiveKind.U32 => (0L, uint.MaxValue),
            PrimitiveKind.U64 => (0L, long.MaxValue),
            PrimitiveKind.I32 => (int.MinValue, int.MaxValue),
            _ => (long.MinValue, long.MaxValue)
        };
        if (bound.Value < minimum || bound.Value > maximum)
        {
            throw node.Error(name, "does not fit the field type.");
        }

        return new NumericBound(bound.Value, bound.LimitName);
    }

    private static void CheckMessageSizes(SchemaNode node, MessageDefinition message, TypeScope scope)
    {
        var size = WireSizes.OfMessage(message);
        if (message.Delivery == MessageDelivery.Unreliable && size.Maximum > scope.MaximumUnreliablePacketSize)
        {
            throw node.Error(
                "fields",
                $"can reach {size.Maximum} bytes, above the unreliable limit of {scope.MaximumUnreliablePacketSize}.");
        }

        if (message.Delivery != MessageDelivery.Unreliable
            && size.Maximum > scope.MaximumPacketSize
            && !message.RuntimeSizeCheck)
        {
            throw node.Error(
                "runtimeSizeCheck",
                $"must be true: the message can reach {size.Maximum} bytes, above the packet limit of {scope.MaximumPacketSize}.");
        }

        if (message.RuntimeSizeCheck
            && (message.Delivery == MessageDelivery.Unreliable || size.Maximum <= scope.MaximumPacketSize))
        {
            throw node.Error(
                "runtimeSizeCheck",
                $"is only for a reliable message that can exceed the packet limit; this one reaches at most {size.Maximum} bytes.");
        }

        if (message.SizeConstant is not null && !size.IsFixed)
        {
            throw node.Error("sizeConstant", "requires a message with a fixed size.");
        }

        if (message.EmptySizeConstant is not null && message.Fields.All(field => field.Type is not ArrayWireType))
        {
            throw node.Error("emptySizeConstant", "requires a message with an array field.");
        }
    }

    private static void Validate(ProtocolModel model)
    {
        var typeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "MessageType",
            "MessageDescriptor",
            "Delivery",
            "MessageDirection",
            "Protocol",
            "ProtocolLimits"
        };
        foreach (var name in model.Enums.Select(definition => definition.Name)
            .Concat(model.Structs.Select(definition => definition.Name))
            .Concat(model.Messages.Select(message => message.Name)))
        {
            if (!typeNames.Add(name))
            {
                throw new ProtocolSchemaException("$", $"type name '{name}' is used more than once.");
            }
        }

        var constants = new HashSet<string>(model.Limits.Select(limit => limit.Name), StringComparer.Ordinal);
        foreach (var constant in model.Structs.Select(definition => definition.SizeConstant)
            .Concat(model.Messages.SelectMany(message => new[] { message.SizeConstant, message.EmptySizeConstant }))
            .OfType<string>())
        {
            if (!constants.Add(constant))
            {
                throw new ProtocolSchemaException("$", $"constant '{constant}' is defined more than once.");
            }
        }

        foreach (var definition in model.Structs)
        {
            if (definition.SizeConstant is not null && !WireSizes.OfFields(definition.Fields).IsFixed)
            {
                throw new ProtocolSchemaException("$", $"struct '{definition.Name}' has a sizeConstant but no fixed size.");
            }
        }

        // Hand-written codecs may use any struct, so unused structs are only known once every
        // message is generated.
        if (model.Messages.Any(message => message.Codec == MessageCodec.HandWritten))
        {
            return;
        }

        var used = new HashSet<StructDefinition>();
        foreach (var message in model.Messages)
        {
            var variantFields = message.Union?.Pool.Select(pool => pool.Field) ?? [];
            foreach (var field in message.Fields.Concat(variantFields))
            {
                CollectStructs(field.Type, used);
            }
        }

        var unused = model.Structs.FirstOrDefault(definition => !used.Contains(definition));
        if (unused is not null)
        {
            throw new ProtocolSchemaException("$", $"struct '{unused.Name}' is not used by any generated message.");
        }
    }

    private static void EnsureAcyclic(StructDefinition definition, HashSet<StructDefinition> path)
    {
        if (!path.Add(definition))
        {
            throw new ProtocolSchemaException("$", $"struct '{definition.Name}' contains itself.");
        }

        foreach (var field in definition.Fields)
        {
            foreach (var nested in DirectStructs(field.Type))
            {
                EnsureAcyclic(nested, path);
            }
        }

        path.Remove(definition);
    }

    private static void CollectStructs(WireType type, HashSet<StructDefinition> used)
    {
        foreach (var nested in DirectStructs(type))
        {
            if (used.Add(nested))
            {
                foreach (var field in nested.Fields)
                {
                    CollectStructs(field.Type, used);
                }
            }
        }
    }

    private static IEnumerable<StructDefinition> DirectStructs(WireType type)
    {
        return type switch
        {
            StructWireType structType => [structType.Definition],
            ArrayWireType array => DirectStructs(array.Element.Type),
            _ => []
        };
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

    private static string? OptionalMemberName(SchemaNode node, string property)
    {
        return node.Has(property) ? RequireMemberName(node, property) : null;
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

    // Names that field types and bounds may refer to.
    private sealed class TypeScope
    {
        private readonly IReadOnlyList<LimitDefinition> limits;
        private readonly IReadOnlyList<EnumDefinition> enums;

        public TypeScope(IReadOnlyList<LimitDefinition> limits, IReadOnlyList<EnumDefinition> enums)
        {
            this.limits = limits;
            this.enums = enums;
        }

        public int MaximumPacketSize { get; init; } = int.MaxValue;

        public int MaximumUnreliablePacketSize { get; init; } = int.MaxValue;

        public Dictionary<string, StructDefinition> Structs { get; } = new(StringComparer.Ordinal);

        public WireType Resolve(SchemaNode node, string property, string typeName)
        {
            if (Primitives.TryGetValue(typeName, out var primitive))
            {
                return new PrimitiveType(primitive);
            }

            if (typeName == "string")
            {
                var maximumLength = ReadIntBound(node, "maxLength")
                    ?? throw node.Error("maxLength", "is required for string.");
                if (maximumLength.Value is < 1 or > 4096)
                {
                    throw node.Error("maxLength", "must be between 1 and 4096.");
                }

                return new StringWireType(maximumLength);
            }

            var enumDefinition = enums.FirstOrDefault(definition => definition.Name == typeName);
            if (enumDefinition is not null)
            {
                return new EnumWireType(enumDefinition);
            }

            if (Structs.TryGetValue(typeName, out var structDefinition))
            {
                return new StructWireType(structDefinition);
            }

            throw node.Error(property, $"'{typeName}' is not a primitive, enum, or struct.");
        }

        // A number, or the name of a limit whose value it takes.
        public IntBound? ReadIntBound(SchemaNode node, string name)
        {
            var limitName = node.OptionalStringOrNull(name, out var number);
            if (limitName is null)
            {
                if (number is null)
                {
                    return null;
                }

                if (number < int.MinValue || number > int.MaxValue)
                {
                    throw node.Error(name, "is out of range.");
                }

                return new IntBound((int)number.Value, null);
            }

            var limit = limits.FirstOrDefault(candidate => candidate.Name == limitName)
                ?? throw node.Error(name, $"names no limit: '{limitName}'.");
            return new IntBound(limit.Value, limit.Name);
        }
    }
}
