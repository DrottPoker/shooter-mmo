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
        writer.Line($"public {className}(");
        for (var index = 0; index < members.Length - 1; index++)
        {
            writer.Line($"    {members[index].Type} {CSharpNames.Parameter(members[index].Name)},");
        }

        writer.Open($"    {members[^1].Type} {CSharpNames.Parameter(members[^1].Name)})");
        foreach (var member in members)
        {
            writer.Line($"{member.Name} = {CSharpNames.Parameter(member.Name)};");
        }

        writer.Close();
        foreach (var member in members)
        {
            writer.BlankLine();
            writer.Summary(member.Description);
            writer.Line($"public {member.Type} {member.Name} {{ get; }}");
        }

        writer.Close();
    }

    public static void EmitStructCodec(CSharpWriter writer, StructDefinition definition)
    {
        var display = CSharpNames.Display(definition.Name);
        writer.Open($"private static void Write{definition.Name}(BinaryWriter writer, {definition.ClassName} value)");
        writer.Open($"if (!IsValid{definition.Name}(value))");
        writer.Line($"throw new ArgumentException({CSharpWriter.Literal(display + " is invalid.")}, nameof(value));");
        writer.Close();
        writer.BlankLine();
        EmitWrites(writer, definition.Fields, unwrap: false);
        writer.Close();
        writer.BlankLine();
        EmitTryRead(writer, definition.Name, definition.ClassName, definition.Fields, unwrap: false, validate: true);
        writer.BlankLine();
        EmitIsValid(writer, definition.Name, definition.ClassName, definition.Fields, unwrap: false, definition.Rules);
    }

    public static void EmitMessageCodec(CSharpWriter writer, MessageDefinition message)
    {
        var unwrap = message.Api == MessageApi.Unwrap;
        var modelType = unwrap ? TypeName(message.Fields[0].Type) : message.ClassName;
        var parameter = message.Parameter;
        var invalid = CSharpWriter.Literal(CSharpNames.Display(message.Name) + " is invalid.");
        writer.Summary(message.Description);
        writer.Open($"public static byte[] Encode{message.Name}({modelType} {parameter})");
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
        writer.Open($"if (!IsValid{message.Name}(decoded))");
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
        EmitWrites(writer, message.Fields, unwrap);
        writer.Close();
        writer.BlankLine();
        EmitTryRead(writer, message.Name, modelType, message.Fields, unwrap, validate: false);
        writer.BlankLine();
        EmitIsValid(writer, message.Name, modelType, message.Fields, unwrap, message.Rules);
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
        var fields = model.Structs.SelectMany(definition => definition.Fields)
            .Concat(model.Messages.Where(message => message.Codec == MessageCodec.Generated)
                .SelectMany(message => message.Fields));
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

    private static void EmitWrites(CSharpWriter writer, IReadOnlyList<FieldDefinition> fields, bool unwrap)
    {
        foreach (var field in fields)
        {
            if (field.Type is BitsWireType bits)
            {
                var parts = bits.Bits.Select((bit, index) => $"(value.{bit.Name} ? {1 << index} : 0)");
                writer.Line($"writer.Write((byte)({string.Join(" | ", parts)}));");
                continue;
            }

            EmitWrite(writer, field.Type, unwrap ? "value" : "value." + field.Name);
        }
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
        bool validate)
    {
        writer.Open($"private static bool TryRead{name}(BinaryReader reader, out {modelType} value, out string error)");
        writer.Line($"value = {DefaultValue(modelType)};");
        var arguments = new List<string>();
        foreach (var field in fields)
        {
            var local = CSharpNames.Local(field.Name);
            var fieldDisplay = CSharpNames.Display(name) + " " + CSharpNames.Display(field.Name).ToLowerInvariant();
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

            arguments.Add(EmitRead(writer, field.Type, local, fieldDisplay));
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
        writer.Open($"if (!IsValid{name}(decoded))");
        writer.Line($"error = {CSharpWriter.Literal(CSharpNames.Display(name) + " is invalid.")};");
        writer.Line("return false;");
        writer.Close();
        writer.BlankLine();

        writer.Line("value = decoded;");
        writer.Line("return true;");
        writer.Close();
    }

    // Reads one value into a new local and returns the expression that holds it.
    private static string EmitRead(CSharpWriter writer, WireType type, string local, string display)
    {
        switch (type)
        {
            case PrimitiveType primitive:
                EmitPrimitiveRead(writer, ReaderSuffix(primitive.Kind), local);
                return local;
            case EnumWireType enumType:
                EmitPrimitiveRead(writer, "Byte", local + "Byte");
                writer.Line($"var {local} = ({enumType.Definition.ClassName}){local}Byte;");
                writer.BlankLine();
                return local;
            case StructWireType structType:
                writer.Open($"if (!TryRead{structType.Definition.Name}(reader, out var {local}, out error))");
                writer.Line("return false;");
                writer.Close();
                writer.BlankLine();
                return local;
            case ArrayWireType array:
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
                writer.Line($"var {local} = new {ArrayAllocation(array, local + "Count")};");
                writer.Open($"for (var index = 0; index < {local}.Length; index++)");
                var element = EmitRead(writer, array.Element.Type, "element", display);
                writer.Line($"{local}[index] = {element};");
                writer.Close();
                writer.BlankLine();
                return local;
            default:
                throw new InvalidOperationException($"Type {type} cannot be read.");
        }
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
        bool rules)
    {
        writer.Open($"private static bool IsValid{name}({modelType} value)");
        var conditions = new List<string>();
        if (!unwrap)
        {
            conditions.Add("value == null");
        }

        var loops = new List<Action>();
        foreach (var field in fields)
        {
            var expression = unwrap ? "value" : "value." + field.Name;
            conditions.AddRange(Failures(field.Type, field.Rules, expression));
            if (field.Type is ArrayWireType array)
            {
                AddArrayLoops(loops, writer, array, field.Rules, expression);
            }
        }

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

        writer.Line(rules ? "return SatisfiesRules(value);" : "return true;");
        writer.Close();
    }

    // Expressions that are true when the value breaks a rule of its type.
    private static IEnumerable<string> Failures(WireType type, ValueRules valueRules, string expression)
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
            case StructWireType structType:
                yield return $"!IsValid{structType.Definition.Name}({expression})";
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
