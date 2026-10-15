using System.Globalization;

namespace Mmorpg.Tools.ProtocolGenerator;

// Emits model classes, codecs, and validation for structs and generated messages. The
// codecs use the BinaryWriter and BinaryReader helpers of the hand-written RealtimeProtocol
// core, and struct helpers keep the Write, TryRead, and IsValid names that hand-written
// family codecs call.
internal static class CodecEmitter
{
    public static string TypeName(WireType type)
    {
        return type switch
        {
            PrimitiveType primitive => primitive.Kind switch
            {
                PrimitiveKind.U8 => "byte",
                PrimitiveKind.U16 => "ushort",
                PrimitiveKind.U32 => "uint",
                PrimitiveKind.U64 => "ulong",
                PrimitiveKind.I32 => "int",
                PrimitiveKind.I64 => "long",
                PrimitiveKind.F32 => "float",
                PrimitiveKind.Bool => "bool",
                PrimitiveKind.Guid => "Guid",
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            },
            EnumWireType enumType => enumType.Definition.ClassName,
            StructWireType structType => structType.Definition.ClassName,
            StringWireType => "string",
            ArrayWireType array => TypeName(array.Element.Type) + "[]",
            _ => throw new InvalidOperationException($"Type {type} has no C# type.")
        };
    }

    public static void EmitModel(
        CSharpWriter writer,
        string className,
        string description,
        IReadOnlyList<FieldDefinition> fields)
    {
        var members = Members(fields).ToArray();
        writer.Summary(description);
        writer.Open($"public sealed partial class {className}");
        EmitConstructor(writer, "public", className, members);
        EmitProperties(writer, members);
        writer.Close();
    }

    // A message with variants: an internal constructor for the decoder and one factory per
    // discriminator value.
    public static void EmitUnionModel(CSharpWriter writer, MessageDefinition message)
    {
        var union = message.Union!;
        var members = Members(message.Fields)
            .Concat(union.Pool.Select(pool => new Member(pool.Field.Name, TypeName(pool.Field.Type), pool.Field.Description)))
            .ToArray();
        writer.Summary(message.Description);
        writer.Open($"public sealed partial class {message.ClassName}");
        EmitConstructor(writer, "internal", message.ClassName, members);
        EmitProperties(writer, members);
        foreach (var unionCase in union.Cases)
        {
            foreach (var kind in unionCase.Kinds)
            {
                writer.BlankLine();
                EmitFactory(writer, message, unionCase, kind);
            }
        }

        writer.Close();
    }

    // A lenient struct is decoded somewhere with decodeBlank, so its reader and validation
    // take acceptBlankText, which lets its text fields be empty or whitespace.
    public static void EmitStructCodec(CSharpWriter writer, StructDefinition definition, bool lenient)
    {
        var display = CSharpNames.Display(definition.Name);
        var leniency = lenient ? new Leniency(StructParameter, NestedStructs: false, AllText: true) : null;
        writer.Open($"private static void Write{definition.Name}(BinaryWriter writer, {definition.ClassName} value)");
        writer.Open($"if (!IsValid{definition.Name}(value))");
        writer.Line($"throw new ArgumentException({CSharpWriter.Literal(display + " is invalid.")}, nameof(value));");
        writer.Close();
        writer.BlankLine();
        EmitWrites(writer, definition.Fields, unwrap: false, union: null, display);
        writer.Close();
        writer.BlankLine();
        EmitTryRead(writer, definition.Name, definition.ClassName, definition.Fields, unwrap: false, validate: true, union: null, leniency);
        writer.BlankLine();
        EmitIsValid(writer, definition.Name, definition.ClassName, definition.Fields, unwrap: false, definition.Rules, union: null, leniency);
    }

    public static void EmitMessageCodec(CSharpWriter writer, MessageDefinition message)
    {
        if (message.Api == MessageApi.Empty)
        {
            EmitEmptyMessageCodec(writer, message);
            return;
        }

        var unwrap = message.Api == MessageApi.Unwrap;
        var modelType = unwrap ? TypeName(message.Fields[0].Type) : message.ClassName;
        var parameter = message.Parameter;
        var display = CSharpNames.Display(message.Name);
        var invalid = CSharpWriter.Literal(display + " is invalid.");
        var leniency = message.Fields.Any(field => field.AcceptBlankOnDecode)
            ? new Leniency(MessageParameter, NestedStructs: true, AllText: false)
            : null;
        writer.Summary(message.Description);
        if (message.ExpandedEncode)
        {
            var structDefinition = ((StructWireType)message.Fields[0].Type).Definition;
            var members = Members(structDefinition.Fields).ToArray();
            var parameters = members.Select(member => $"{member.Type} {CSharpNames.Parameter(member.Name)}");
            writer.Open($"public static byte[] Encode{message.Name}({string.Join(", ", parameters)})");
            writer.Line($"var {parameter} = new {modelType}({string.Join(", ", members.Select(member => CSharpNames.Parameter(member.Name)))});");
        }
        else
        {
            writer.Open($"public static byte[] Encode{message.Name}({modelType} {parameter})");
        }

        writer.Open($"if (!IsValid{message.Name}({parameter}))");
        writer.Line($"throw new ArgumentException({invalid}, nameof({parameter}));");
        writer.Close();
        writer.BlankLine();
        writer.Line($"return Encode(RealtimeMessageType.{message.Name}, writer => Write{message.Name}(writer, {parameter}));");
        writer.Close();
        writer.BlankLine();
        writer.Open($"public static bool TryDecode{message.Name}(byte[] data, out {modelType} {parameter}, out string error)");
        writer.Line($"{parameter} = {DefaultValue(modelType)};");
        writer.Open($"if (!TryCreateReader(data, RealtimeMessageType.{message.Name}, out var stream, out var reader, out error))");
        writer.Line("return false;");
        writer.Close();
        writer.BlankLine();
        writer.Line("using (stream)");
        writer.Open("using (reader)");
        writer.Open($"if (!TryRead{message.Name}(reader, out var decoded, out error) || !TryFinish(stream, out error))");
        writer.Line("return false;");
        writer.Close();
        writer.BlankLine();
        writer.Open($"if (!IsValid{message.Name}(decoded{(leniency is null ? string.Empty : $", {MessageParameter}: true")}))");
        writer.Line($"error = {invalid};");
        writer.Line("return false;");
        writer.Close();
        writer.BlankLine();
        writer.Line($"{parameter} = decoded;");
        writer.Line("return true;");
        writer.Close();
        writer.Close();
        writer.BlankLine();
        writer.Open($"private static void Write{message.Name}(BinaryWriter writer, {modelType} value)");
        EmitWrites(writer, message.Fields, unwrap, message.Union, display);
        writer.Close();
        writer.BlankLine();
        EmitTryRead(writer, message.Name, modelType, message.Fields, unwrap, validate: false, message.Union, leniency);
        writer.BlankLine();
        EmitIsValid(writer, message.Name, modelType, message.Fields, unwrap, message.Rules, message.Union, leniency);
    }

    private static void EmitEmptyMessageCodec(CSharpWriter writer, MessageDefinition message)
    {
        writer.Summary(message.Description);
        writer.Open($"public static byte[] Encode{message.Name}()");
        writer.Line($"return Encode(RealtimeMessageType.{message.Name}, _ => {{ }});");
        writer.Close();
        writer.BlankLine();
        writer.Open($"public static bool TryDecode{message.Name}(byte[] data, out string error)");
        writer.Open($"if (!TryCreateReader(data, RealtimeMessageType.{message.Name}, out var stream, out var reader, out error))");
        writer.Line("return false;");
        writer.Close();
        writer.BlankLine();
        writer.Line("using (stream)");
        writer.Open("using (reader)");
        writer.Line("return TryFinish(stream, out error);");
        writer.Close();
        writer.Close();
    }

    private const string StructParameter = "acceptBlankText";

    private const string MessageParameter = "decoding";

    // How validation relaxes text checks while decoding. Parameter names the bool that
    // allows empty or whitespace text; NestedStructs passes it on to lenient struct fields,
    // and AllText applies it to every text field instead of only decodeBlank fields.
    private sealed record Leniency(string Parameter, bool NestedStructs, bool AllText);

    // The structs that some decodeBlank field reads leniently.
    public static ISet<StructDefinition> LenientStructs(ProtocolModel model)
    {
        var fields = model.Structs.SelectMany(definition => definition.Fields)
            .Concat(model.Messages.Where(message => message.Codec == MessageCodec.Generated).SelectMany(message => message.Fields));
        return fields.Where(field => field.AcceptBlankOnDecode && field.Type is StructWireType)
            .Select(field => ((StructWireType)field.Type).Definition)
            .ToHashSet();
    }

    public static void EmitIsDefined(CSharpWriter writer, EnumDefinition definition)
    {
        writer.Open($"private static bool IsDefined({definition.ClassName} value)");
        if (definition.Flags)
        {
            var mask = definition.Values.Aggregate(0, (total, value) => total | value.Value);
            writer.Line($"return ((byte)value & ~0x{mask.ToString("X2", CultureInfo.InvariantCulture)}) == 0;");
        }
        else
        {
            var values = definition.Values.Select(value => value.Value).OrderBy(value => value).ToArray();
            var contiguous = values[^1] - values[0] == values.Length - 1;
            if (contiguous)
            {
                writer.Line($"return (byte)value >= {values[0]} && (byte)value <= {values[^1]};");
            }
            else
            {
                writer.Line($"return {string.Join(" || ", values.Select(value => $"(byte)value == {value}"))};");
            }
        }

        writer.Close();
    }

    // The enums that generated validation checks, so their IsDefined helpers are emitted.
    public static IEnumerable<EnumDefinition> CheckedEnums(ProtocolModel model)
    {
        var messages = model.Messages.Where(message => message.Codec == MessageCodec.Generated).ToArray();
        var fields = model.Structs.SelectMany(definition => definition.Fields)
            .Concat(messages.SelectMany(message => message.Fields))
            .Concat(messages.Where(message => message.Union is not null)
                .SelectMany(message => message.Union!.Pool.Select(pool => pool.Field)));
        return fields.SelectMany(field => EnumsOf(field.Type)).Distinct();
    }

    private static IEnumerable<EnumDefinition> EnumsOf(WireType type)
    {
        return type switch
        {
            EnumWireType enumType => [enumType.Definition],
            ArrayWireType array => EnumsOf(array.Element.Type),
            _ => []
        };
    }

    private static string DefaultValue(string typeName)
    {
        if (typeName == "string")
        {
            return "string.Empty";
        }

        return typeName is "byte" or "ushort" or "uint" or "ulong" or "int" or "long" or "float" or "bool" or "Guid"
            ? "default"
            : "null";
    }

    // A model member: one per field, and one bool per bit of a bits8 field.
    private sealed record Member(string Name, string Type, string? Description);

    private static IEnumerable<Member> Members(IEnumerable<FieldDefinition> fields)
    {
        foreach (var field in fields)
        {
            if (field.Type is BitsWireType bits)
            {
                foreach (var bit in bits.Bits)
                {
                    yield return new Member(bit.Name, "bool", bit.Description);
                }

                continue;
            }

            yield return new Member(field.Name, TypeName(field.Type), field.Description);
        }
    }

    private static void EmitConstructor(CSharpWriter writer, string access, string className, IReadOnlyList<Member> members)
    {
        writer.Line($"{access} {className}(");
        for (var index = 0; index < members.Count - 1; index++)
        {
            writer.Line($"    {members[index].Type} {CSharpNames.Parameter(members[index].Name)},");
        }

        writer.Open($"    {members[^1].Type} {CSharpNames.Parameter(members[^1].Name)})");
        foreach (var member in members)
        {
            writer.Line($"{member.Name} = {CSharpNames.Parameter(member.Name)};");
        }

        writer.Close();
    }

    private static void EmitProperties(CSharpWriter writer, IEnumerable<Member> members)
    {
        foreach (var member in members)
        {
            writer.BlankLine();
            writer.Summary(member.Description);
            writer.Line($"public {member.Type} {member.Name} {{ get; }}");
        }
    }

    private static void EmitFactory(CSharpWriter writer, MessageDefinition message, UnionCase unionCase, UnionKind kind)
    {
        var union = message.Union!;
        var entries = CaseFields(unionCase).ToDictionary(entry => entry.Field.Name);
        var overrides = unionCase.CommonOverrides.ToDictionary(entry => entry.Field.Name);
        var parameters = new List<string>();
        var arguments = new List<string>();
        foreach (var field in message.Fields)
        {
            if (field.Name == union.Discriminator)
            {
                arguments.Add($"{TypeName(field.Type)}.{kind.Value.Name}");
            }
            else if (overrides.TryGetValue(field.Name, out var fixedField) && fixedField.FixedValue is not null)
            {
                arguments.Add(fixedField.FixedValue);
            }
            else
            {
                parameters.Add($"{TypeName(field.Type)} {CSharpNames.Parameter(field.Name)}");
                arguments.Add(CSharpNames.Parameter(field.Name));
            }
        }

        foreach (var entry in CaseFields(unionCase).Where(entry => entry.FixedValue is null))
        {
            var name = entry.ParameterName ?? CSharpNames.Parameter(entry.Field.Name);
            var defaultValue = entry.ParameterDefault is null ? string.Empty : " = " + entry.ParameterDefault;
            parameters.Add($"{TypeName(entry.Field.Type)} {name}{defaultValue}");
        }

        foreach (var pool in union.Pool)
        {
            arguments.Add(entries.TryGetValue(pool.Field.Name, out var entry)
                ? entry.FixedValue ?? entry.ParameterName ?? CSharpNames.Parameter(pool.Field.Name)
                : pool.AbsentValue);
        }

        writer.Line($"public static {message.ClassName} {kind.Factory}(");
        for (var index = 0; index < parameters.Count - 1; index++)
        {
            writer.Line($"    {parameters[index]},");
        }

        writer.Open($"    {parameters[^1]})");
        writer.Line($"return new {message.ClassName}(");
        for (var index = 0; index < arguments.Count; index++)
        {
            writer.Line($"    {arguments[index]}{(index + 1 < arguments.Count ? "," : ");")}");
        }

        writer.Close();
    }

    private static IEnumerable<UnionFieldEntry> CaseFields(UnionCase unionCase)
    {
        return unionCase.Entries.SelectMany(entry => entry is UnionGroupEntry group ? group.Fields : [(UnionFieldEntry)entry]);
    }

    private static string Condition(FieldDefinition field, string prefix)
    {
        return (field.ConditionValue ? string.Empty : "!") + prefix + field.ConditionField;
    }

    // True when a group's presence field holds a value other than its absent value.
    private static string Presence(WireType type, string expression)
    {
        return type switch
        {
            PrimitiveType { Kind: PrimitiveKind.Guid } => $"{expression} != Guid.Empty",
            PrimitiveType { Kind: PrimitiveKind.Bool } => expression,
            PrimitiveType => $"{expression} != 0",
            StringWireType => $"!string.IsNullOrWhiteSpace({expression})",
            _ => $"{expression} != null"
        };
    }

    private static void OpenCaseLabels(CSharpWriter writer, UnionCase unionCase, string enumClass)
    {
        for (var index = 0; index < unionCase.Kinds.Count - 1; index++)
        {
            writer.Line($"case {enumClass}.{unionCase.Kinds[index].Value.Name}:");
        }

        writer.Open($"case {enumClass}.{unionCase.Kinds[^1].Value.Name}:");
    }

    private static void EmitWrites(
        CSharpWriter writer,
        IReadOnlyList<FieldDefinition> fields,
        bool unwrap,
        UnionDefinition? union,
        string display)
    {
        foreach (var field in fields)
        {
            if (field.Type is BitsWireType bits)
            {
                var parts = bits.Bits.Select((bit, index) => $"(value.{bit.Name} ? {1 << index} : 0)");
                writer.Line($"writer.Write((byte)({string.Join(" | ", parts)}));");
                continue;
            }

            if (field.Presence == FieldPresence.Conditional)
            {
                writer.Open($"if ({Condition(field, "value.")})");
                EmitWrite(writer, field.Type, "value." + field.Name);
                writer.Close();
                continue;
            }

            EmitWrite(writer, field.Type, unwrap ? "value" : "value." + field.Name);
        }

        if (union is null)
        {
            return;
        }

        var enumClass = TypeName(fields.Single(field => field.Name == union.Discriminator).Type);
        writer.BlankLine();
        writer.Open($"switch (value.{union.Discriminator})");
        foreach (var unionCase in union.Cases)
        {
            OpenCaseLabels(writer, unionCase, enumClass);
            foreach (var entry in unionCase.Entries)
            {
                if (entry is UnionGroupEntry group)
                {
                    var presenceType = group.Fields.Single(member => member.Field.Name == group.PresenceField).Field.Type;
                    var flag = "has" + group.PresenceField;
                    writer.Line($"var {flag} = {Presence(presenceType, "value." + group.PresenceField)};");
                    writer.Line($"writer.Write({flag});");
                    writer.Open($"if ({flag})");
                    foreach (var member in group.Fields)
                    {
                        EmitWrite(writer, member.Field.Type, "value." + member.Field.Name);
                    }

                    writer.Close();
                    continue;
                }

                var field = (UnionFieldEntry)entry;
                EmitWrite(writer, field.Field.Type, "value." + field.Field.Name);
            }

            writer.Line("break;");
            writer.Close();
        }

        writer.Open("default:");
        writer.Line($"throw new ArgumentException({CSharpWriter.Literal(display + " is invalid.")}, nameof(value));");
        writer.Close();
        writer.Close();
    }

    private static void EmitWrite(CSharpWriter writer, WireType type, string expression)
    {
        switch (type)
        {
            case PrimitiveType { Kind: PrimitiveKind.Guid }:
                writer.Line($"WriteGuid(writer, {expression});");
                break;
            case PrimitiveType:
                writer.Line($"writer.Write({expression});");
                break;
            case EnumWireType:
                writer.Line($"writer.Write((byte){expression});");
                break;
            case StringWireType text:
                writer.Line($"WriteString(writer, {expression}, {text.MaximumLength.Expression}, {CSharpWriter.Literal(expression)});");
                break;
            case StructWireType structType:
                writer.Line($"Write{structType.Definition.Name}(writer, {expression});");
                break;
            case ArrayWireType array:
                writer.Line($"writer.Write((byte){expression}.Length);");
                writer.Open($"foreach (var item in {expression})");
                EmitWrite(writer, array.Element.Type, "item");
                writer.Close();
                break;
            default:
                throw new InvalidOperationException($"Type {type} cannot be written.");
        }
    }

    private static void EmitTryRead(
        CSharpWriter writer,
        string name,
        string modelType,
        IReadOnlyList<FieldDefinition> fields,
        bool unwrap,
        bool validate,
        UnionDefinition? union,
        Leniency? leniency)
    {
        // A lenient struct reader takes the flag that its validation needs.
        var structLeniency = validate && leniency is not null;
        writer.Open($"private static bool TryRead{name}(BinaryReader reader, out {modelType} value, out string error"
            + (structLeniency ? $", bool {leniency!.Parameter} = false)" : ")"));
        writer.Line($"value = {DefaultValue(modelType)};");
        var arguments = new List<string>();
        foreach (var field in fields)
        {
            var local = CSharpNames.Local(field.Name);
            var fieldDisplay = FieldDisplay(name, field.Name);
            if (field.Type is BitsWireType bits)
            {
                EmitPrimitiveRead(writer, "Byte", local);
                var mask = (1 << bits.Bits.Count) - 1;
                writer.Open($"if (({local} & ~0x{mask.ToString("X2", CultureInfo.InvariantCulture)}) != 0)");
                writer.Line($"error = {CSharpWriter.Literal(fieldDisplay + " has unknown bits.")};");
                writer.Line("return false;");
                writer.Close();
                writer.BlankLine();
                arguments.AddRange(bits.Bits.Select((_, index) =>
                    $"({local} & 0x{(1 << index).ToString("X2", CultureInfo.InvariantCulture)}) != 0"));
                continue;
            }

            if (field.Presence == FieldPresence.Conditional)
            {
                writer.Line($"{TypeName(field.Type)} {local} = null;");
                var conditionLocal = CSharpNames.Local(field.ConditionField!);
                writer.Open($"if ({(field.ConditionValue ? string.Empty : "!")}{conditionLocal})");
                EmitReadInto(writer, field.Type, local, fieldDisplay);
                writer.Close();
                writer.BlankLine();
                arguments.Add(local);
                continue;
            }

            arguments.Add(EmitRead(writer, field.Type, local, fieldDisplay, lenient: field.AcceptBlankOnDecode));
        }

        if (union is not null)
        {
            EmitUnionReads(writer, name, fields, union);
            arguments.AddRange(union.Pool.Select(pool => CSharpNames.Local(pool.Field.Name)));
        }

        if (fields.Count == 0)
        {
            writer.Line("error = string.Empty;");
        }

        if (unwrap)
        {
            writer.Line($"value = {arguments[0]};");
            writer.Line("return true;");
            writer.Close();
            return;
        }

        var construction = $"new {modelType}({string.Join(", ", arguments)})";
        if (!validate)
        {
            writer.Line($"value = {construction};");
            writer.Line("return true;");
            writer.Close();
            return;
        }

        writer.Line($"var decoded = {construction};");
        writer.Open($"if (!IsValid{name}(decoded{(structLeniency ? ", " + leniency!.Parameter : string.Empty)}))");
        writer.Line($"error = {CSharpWriter.Literal(CSharpNames.Display(name) + " is invalid.")};");
        writer.Line("return false;");
        writer.Close();
        writer.BlankLine();
        writer.Line("value = decoded;");
        writer.Line("return true;");
        writer.Close();
    }

    private static void EmitUnionReads(CSharpWriter writer, string name, IReadOnlyList<FieldDefinition> fields, UnionDefinition union)
    {
        foreach (var pool in union.Pool)
        {
            writer.Line($"{TypeName(pool.Field.Type)} {CSharpNames.Local(pool.Field.Name)} = {pool.AbsentValue};");
        }

        writer.BlankLine();
        var enumClass = TypeName(fields.Single(field => field.Name == union.Discriminator).Type);
        writer.Open($"switch ({CSharpNames.Local(union.Discriminator)})");
        foreach (var unionCase in union.Cases)
        {
            OpenCaseLabels(writer, unionCase, enumClass);
            foreach (var fixedField in unionCase.CommonOverrides.Where(entry => entry.FixedValue is not null))
            {
                writer.Line($"{CSharpNames.Local(fixedField.Field.Name)} = {fixedField.FixedValue};");
                writer.BlankLine();
            }

            foreach (var entry in unionCase.Entries)
            {
                if (entry is UnionGroupEntry group)
                {
                    var flag = "has" + group.PresenceField;
                    EmitPrimitiveRead(writer, "Boolean", flag);
                    writer.Open($"if ({flag})");
                    foreach (var member in group.Fields)
                    {
                        EmitReadInto(writer, member.Field.Type, CSharpNames.Local(member.Field.Name), FieldDisplay(name, member.Field.Name));
                    }

                    writer.Close();
                    writer.BlankLine();
                    continue;
                }

                var field = (UnionFieldEntry)entry;
                if (field.FixedValue is not null)
                {
                    EmitDiscardRead(writer, field.Field.Type);
                    writer.Line($"{CSharpNames.Local(field.Field.Name)} = {field.FixedValue};");
                    writer.BlankLine();
                    continue;
                }

                EmitReadInto(writer, field.Field.Type, CSharpNames.Local(field.Field.Name), FieldDisplay(name, field.Field.Name));
            }

            writer.Line("break;");
            writer.Close();
        }

        writer.Open("default:");
        writer.Line($"error = {CSharpWriter.Literal(FieldDisplay(name, union.Discriminator) + " is invalid.")};");
        writer.Line("return false;");
        writer.Close();
        writer.Close();
        writer.BlankLine();
    }

    private static string FieldDisplay(string owner, string field)
    {
        return CSharpNames.Display(owner) + " " + CSharpNames.Display(field).ToLowerInvariant();
    }

    // Reads one value into a new local and returns the expression that holds it.
    private static string EmitRead(CSharpWriter writer, WireType type, string local, string display, bool lenient = false)
    {
        switch (type)
        {
            case PrimitiveType primitive:
                EmitPrimitiveRead(writer, ReaderSuffix(primitive.Kind), local);
                return local;
            case StringWireType text:
                writer.Open($"if (!TryReadString(reader, {text.MaximumLength.Expression}, out var {local}, out error))");
                writer.Line("return false;");
                writer.Close();
                writer.BlankLine();
                return local;
            case EnumWireType enumType:
                EmitPrimitiveRead(writer, "Byte", local + "Byte");
                writer.Line($"var {local} = ({enumType.Definition.ClassName}){local}Byte;");
                writer.BlankLine();
                return local;
            case StructWireType structType:
                writer.Open($"if (!TryRead{structType.Definition.Name}(reader, out var {local}, out error{(lenient ? ", " + StructParameter + ": true" : string.Empty)}))");
                writer.Line("return false;");
                writer.Close();
                writer.BlankLine();
                return local;
            case ArrayWireType array:
                EmitArrayCount(writer, array, local, display);
                writer.Line($"var {local} = new {ArrayAllocation(array, local + "Count")};");
                EmitArrayElements(writer, array, local, display);
                return local;
            default:
                throw new InvalidOperationException($"Type {type} cannot be read.");
        }
    }

    // Reads one value into an existing local.
    private static void EmitReadInto(CSharpWriter writer, WireType type, string target, string display)
    {
        switch (type)
        {
            case PrimitiveType primitive:
                writer.Open($"if (!TryRead{ReaderSuffix(primitive.Kind)}(reader, out {target}, out error))");
                break;
            case StringWireType text:
                writer.Open($"if (!TryReadString(reader, {text.MaximumLength.Expression}, out {target}, out error))");
                break;
            case EnumWireType enumType:
                EmitPrimitiveRead(writer, "Byte", target + "Byte");
                writer.Line($"{target} = ({enumType.Definition.ClassName}){target}Byte;");
                writer.BlankLine();
                return;
            case StructWireType structType:
                writer.Open($"if (!TryRead{structType.Definition.Name}(reader, out {target}, out error))");
                break;
            case ArrayWireType array:
                EmitArrayCount(writer, array, target, display);
                writer.Line($"{target} = new {ArrayAllocation(array, target + "Count")};");
                EmitArrayElements(writer, array, target, display);
                return;
            default:
                throw new InvalidOperationException($"Type {type} cannot be read.");
        }

        writer.Line("return false;");
        writer.Close();
        writer.BlankLine();
    }

    // Reads a fixed variant field whose value the decoder replaces.
    private static void EmitDiscardRead(CSharpWriter writer, WireType type)
    {
        var call = type switch
        {
            PrimitiveType primitive => $"TryRead{ReaderSuffix(primitive.Kind)}(reader, out _, out error)",
            _ => throw new InvalidOperationException($"Fixed fields of type {type} are not supported.")
        };
        writer.Open($"if (!{call})");
        writer.Line("return false;");
        writer.Close();
        writer.BlankLine();
    }

    private static void EmitArrayCount(CSharpWriter writer, ArrayWireType array, string local, string display)
    {
        EmitPrimitiveRead(writer, "Byte", local + "Count");
        var conditions = new List<string>();
        if (array.MinimumCount > 0)
        {
            conditions.Add($"{local}Count < {array.MinimumCount}");
        }

        conditions.Add($"{local}Count > {array.MaximumCount.Expression}");
        writer.Open($"if ({string.Join(" || ", conditions)})");
        writer.Line($"error = {CSharpWriter.Literal(display + " count is invalid.")};");
        writer.Line("return false;");
        writer.Close();
        writer.BlankLine();
    }

    private static void EmitArrayElements(CSharpWriter writer, ArrayWireType array, string local, string display)
    {
        writer.Open($"for (var index = 0; index < {local}.Length; index++)");
        var element = EmitRead(writer, array.Element.Type, "element", display);
        writer.Line($"{local}[index] = {element};");
        writer.Close();
        writer.BlankLine();
    }

    private static string ArrayAllocation(ArrayWireType array, string count)
    {
        var elementType = TypeName(array.Element.Type);
        return $"{elementType}[{count}]";
    }

    private static void EmitPrimitiveRead(CSharpWriter writer, string suffix, string local)
    {
        writer.Open($"if (!TryRead{suffix}(reader, out var {local}, out error))");
        writer.Line("return false;");
        writer.Close();
        writer.BlankLine();
    }

    private static string ReaderSuffix(PrimitiveKind kind)
    {
        return kind switch
        {
            PrimitiveKind.U8 => "Byte",
            PrimitiveKind.U16 => "UInt16",
            PrimitiveKind.U32 => "UInt32",
            PrimitiveKind.U64 => "UInt64",
            PrimitiveKind.I32 => "Int32",
            PrimitiveKind.I64 => "Int64",
            PrimitiveKind.F32 => "Single",
            PrimitiveKind.Bool => "Boolean",
            PrimitiveKind.Guid => "Guid",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static void EmitIsValid(
        CSharpWriter writer,
        string name,
        string modelType,
        IReadOnlyList<FieldDefinition> fields,
        bool unwrap,
        bool rules,
        UnionDefinition? union,
        Leniency? leniency)
    {
        writer.Open($"private static bool IsValid{name}({modelType} value"
            + (leniency is null ? ")" : $", bool {leniency.Parameter} = false)"));
        var conditions = new List<string>();
        if (!unwrap)
        {
            conditions.Add("value == null");
        }

        var loops = new List<Action>();
        foreach (var field in fields)
        {
            var expression = unwrap ? "value" : "value." + field.Name;
            if (field.Presence == FieldPresence.Conditional)
            {
                var structName = ((StructWireType)field.Type).Definition.Name;
                conditions.Add($"{Condition(field, "value.")} != ({expression} != null)");
                conditions.Add($"({expression} != null && !IsValid{structName}({expression}))");
                continue;
            }

            var lenient = leniency is not null && (leniency.AllText || field.AcceptBlankOnDecode);
            AddChecks(conditions, loops, writer, field.Type, field.Rules, expression, lenient ? leniency : null);
        }

        EmitChecks(writer, conditions, loops);
        if (union is not null)
        {
            var enumClass = TypeName(fields.Single(field => field.Name == union.Discriminator).Type);
            writer.Open($"switch (value.{union.Discriminator})");
            foreach (var unionCase in union.Cases)
            {
                OpenCaseLabels(writer, unionCase, enumClass);
                var caseConditions = new List<string>();
                var caseLoops = new List<Action>();
                foreach (var entry in unionCase.CommonOverrides.Concat(CaseFields(unionCase)))
                {
                    var expression = "value." + entry.Field.Name;
                    if (entry.FixedValue is not null)
                    {
                        caseConditions.Add($"{expression} != {entry.FixedValue}");
                        continue;
                    }

                    AddChecks(caseConditions, caseLoops, writer, entry.Field.Type, entry.Field.Rules, expression);
                }

                EmitChecks(writer, caseConditions, caseLoops);
                writer.Line("break;");
                writer.Close();
            }

            writer.Open("default:");
            writer.Line("return false;");
            writer.Close();
            writer.Close();
            writer.BlankLine();
        }

        writer.Line(rules ? "return SatisfiesRules(value);" : "return true;");
        writer.Close();
    }

    private static void AddChecks(
        List<string> conditions,
        List<Action> loops,
        CSharpWriter writer,
        WireType type,
        ValueRules rules,
        string expression,
        Leniency? leniency = null)
    {
        conditions.AddRange(Failures(type, rules, expression, leniency));
        if (type is ArrayWireType array)
        {
            AddArrayLoops(loops, writer, array, rules, expression);
        }
    }

    private static void EmitChecks(CSharpWriter writer, IReadOnlyList<string> conditions, IReadOnlyList<Action> loops)
    {
        if (conditions.Count > 0)
        {
            OpenCondition(writer, conditions);
            writer.Line("return false;");
            writer.Close();
            writer.BlankLine();
        }

        foreach (var loop in loops)
        {
            loop();
            writer.BlankLine();
        }
    }

    // Expressions that are true when the value breaks a rule of its type.
    private static IEnumerable<string> Failures(WireType type, ValueRules valueRules, string expression, Leniency? leniency = null)
    {
        switch (type)
        {
            case PrimitiveType { Kind: PrimitiveKind.F32 }:
                yield return $"!IsFinite({expression})";
                if (valueRules.Minimum is not null)
                {
                    yield return $"{expression} < {CSharpNames.FloatLiteral(valueRules.Minimum.Value)}";
                }

                if (valueRules.Maximum is not null)
                {
                    yield return $"{expression} > {CSharpNames.FloatLiteral(valueRules.Maximum.Value)}";
                }

                if (valueRules.ExclusiveMinimum is not null)
                {
                    yield return $"{expression} <= {CSharpNames.FloatLiteral(valueRules.ExclusiveMinimum.Value)}";
                }

                if (valueRules.ExclusiveMaximum is not null)
                {
                    yield return $"{expression} >= {CSharpNames.FloatLiteral(valueRules.ExclusiveMaximum.Value)}";
                }

                break;
            case PrimitiveType { Kind: PrimitiveKind.Guid }:
                if (valueRules.NonEmpty)
                {
                    yield return $"{expression} == Guid.Empty";
                }

                break;
            case PrimitiveType { Kind: not PrimitiveKind.Bool }:
                if (valueRules.Minimum is not null)
                {
                    yield return $"{expression} < {IntegerBound(valueRules.Minimum)}";
                }

                if (valueRules.Maximum is not null)
                {
                    yield return $"{expression} > {IntegerBound(valueRules.Maximum)}";
                }

                if (valueRules.LessThan is not null)
                {
                    yield return $"{expression} >= value.{valueRules.LessThan}";
                }

                break;
            case EnumWireType:
                yield return $"!IsDefined({expression})";
                break;
            case StringWireType text:
                if (leniency is null)
                {
                    yield return $"string.IsNullOrWhiteSpace({expression})";
                }
                else
                {
                    yield return $"{expression} == null";
                    yield return $"(!{leniency.Parameter} && string.IsNullOrWhiteSpace({expression}))";
                }

                yield return $"{expression}.Length > {text.MaximumLength.Expression}";
                if (valueRules.Format == StringFormat.Guid)
                {
                    yield return $"!Guid.TryParse({expression}, out _)";
                }

                if (valueRules.OneOf is not null)
                {
                    yield return "(" + string.Join(" && ", valueRules.OneOf.Select(option => $"{expression} != {CSharpWriter.Literal(option)}")) + ")";
                }

                break;
            case StructWireType structType:
                var argument = leniency is { NestedStructs: true } ? $", {StructParameter}: {leniency.Parameter}" : string.Empty;
                yield return $"!IsValid{structType.Definition.Name}({expression}{argument})";
                break;
            case ArrayWireType array:
                yield return $"{expression} == null";
                if (array.MinimumCount > 0)
                {
                    yield return $"{expression}.Length < {array.MinimumCount}";
                }

                yield return $"{expression}.Length > {array.MaximumCount.Expression}";
                break;
        }
    }

    // Opens an if block whose conditions are joined by ||, one per line.
    private static void OpenCondition(CSharpWriter writer, IReadOnlyList<string> conditions)
    {
        if (conditions.Count == 1)
        {
            writer.Open($"if ({conditions[0]})");
            return;
        }

        writer.Line($"if ({conditions[0]}");
        for (var index = 1; index < conditions.Count - 1; index++)
        {
            writer.Line($"    || {conditions[index]}");
        }

        writer.Open($"    || {conditions[^1]})");
    }

    private static string IntegerBound(NumericBound bound)
    {
        return bound.LimitName ?? CSharpNames.IntegerLiteral(bound.Value);
    }

    private static void AddArrayLoops(
        List<Action> loops,
        CSharpWriter writer,
        ArrayWireType array,
        ValueRules rules,
        string expression)
    {
        var elementFailures = Failures(array.Element.Type, array.Element.Rules, "item").ToArray();
        if (elementFailures.Length > 0)
        {
            loops.Add(() =>
            {
                writer.Open($"foreach (var item in {expression})");
                writer.Open($"if ({string.Join(" || ", elementFailures)})");
                writer.Line("return false;");
                writer.Close();
                writer.Close();
            });
        }

        if (rules.Distinct || rules.DistinctBy is not null)
        {
            var key = rules.DistinctBy is null ? string.Empty : "." + rules.DistinctBy;
            loops.Add(() =>
            {
                writer.Open($"for (var first = 0; first < {expression}.Length; first++)");
                writer.Open($"for (var second = first + 1; second < {expression}.Length; second++)");
                writer.Open($"if ({expression}[first]{key} == {expression}[second]{key})");
                writer.Line("return false;");
                writer.Close();
                writer.Close();
                writer.Close();
            });
        }
    }
}
