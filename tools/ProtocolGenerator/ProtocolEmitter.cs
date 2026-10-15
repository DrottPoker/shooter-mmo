using System.Globalization;

namespace Mmorpg.Tools.ProtocolGenerator;

// Turns the protocol model into the C# files below Runtime/Generated.
public static class ProtocolEmitter
{
    public const string CoreFileName = "RealtimeProtocol.Core.g.cs";

    private const string ProtocolClass = "RealtimeProtocol";

    // Generated file name to content, ordered by name.
    public static IReadOnlyList<(string FileName, string Content)> Emit(ProtocolModel model)
    {
        var files = new List<(string FileName, string Content)>
        {
            (CoreFileName, EmitCore(model))
        };
        foreach (var family in model.Families)
        {
            files.Add(($"RealtimeProtocol.{family}.g.cs", EmitFamily(model, family)));
        }

        return files.OrderBy(file => file.FileName, StringComparer.Ordinal).ToArray();
    }

    private static string EmitCore(ProtocolModel model)
    {
        var writer = StartFile(model, usesSystem: false);
        writer.Summary("The type byte of every realtime message.");
        writer.Open($"public enum RealtimeMessageType : byte");
        var messages = model.Messages.OrderBy(message => message.Id).ToArray();
        for (var index = 0; index < messages.Length; index++)
        {
            writer.Summary(messages[index].Description);
            writer.Line($"{messages[index].Name} = {messages[index].Id}{(index + 1 < messages.Length ? "," : string.Empty)}");
            if (index + 1 < messages.Length)
            {
                writer.BlankLine();
            }
        }

        writer.Close();
        writer.BlankLine();
        writer.Summary("The direction in which a realtime message travels.");
        writer.Open("public enum RealtimeMessageDirection : byte");
        writer.Line("ClientToServer = 1,");
        writer.Line("ServerToClient = 2");
        writer.Close();
        writer.BlankLine();
        writer.Summary("How the transport delivers a realtime message.");
        writer.Open("public enum RealtimeDelivery : byte");
        writer.Line("ReliableOrdered = 1,");
        writer.Line("Sequenced = 2,");
        writer.Line("Unreliable = 3");
        writer.Close();
        writer.BlankLine();
        EmitDescriptorClass(writer);
        writer.BlankLine();
        writer.Line("// Wire limits that authored content must fit. Content compilers keep their own");
        writer.Line("// limits, and a compatibility test checks that they never exceed these.");
        writer.Open("public static class RealtimeProtocolLimits");
        EmitLimits(writer, model.Limits.Where(limit => limit.Visibility == LimitVisibility.Public), "public", useAlias: false);
        writer.Close();
        writer.BlankLine();
        writer.Line("// Framing, versioning, channels, and limits of the realtime protocol.");
        writer.Open($"public static partial class {ProtocolClass}");
        EmitProtocolConstants(writer, model);
        writer.BlankLine();
        EmitDescriptorTable(writer, messages);
        writer.Close();
        return FinishFile(writer);
    }

    private static string EmitFamily(ProtocolModel model, string family)
    {
        var enums = model.Enums.Where(definition => definition.Family == family).ToArray();
        var structs = model.Structs.Where(definition => definition.Family == family).ToArray();
        var messages = model.Messages.Where(message => message.Family == family).ToArray();
        var checkedEnums = CodecEmitter.CheckedEnums(model).Where(definition => definition.Family == family).ToArray();
        var hasCodec = structs.Length > 0 || messages.Length > 0 || checkedEnums.Length > 0;
        var writer = StartFile(model, usesSystem: hasCodec || enums.Any(definition => definition.Flags));
        foreach (var definition in enums)
        {
            writer.BlankLine();
            EmitEnum(writer, definition);
        }

        foreach (var definition in structs)
        {
            writer.BlankLine();
            CodecEmitter.EmitModel(writer, definition.ClassName, definition.Description, definition.Fields);
        }

        foreach (var message in messages.Where(message => message.Api == MessageApi.Class))
        {
            writer.BlankLine();
            if (message.Union is null)
            {
                CodecEmitter.EmitModel(writer, message.ClassName, message.Description, message.Fields);
            }
            else
            {
                CodecEmitter.EmitUnionModel(writer, message);
            }
        }

        if (hasCodec)
        {
            writer.BlankLine();
            writer.Line($"// Encoding, decoding, and validation of the {family} family.");
            writer.Open($"public static partial class {ProtocolClass}");
            EmitSizeConstants(writer, structs, messages);
            foreach (var message in messages)
            {
                writer.BlankLine();
                CodecEmitter.EmitMessageCodec(writer, message);
            }

            var lenientStructs = CodecEmitter.LenientStructs(model);
            foreach (var definition in structs)
            {
                writer.BlankLine();
                CodecEmitter.EmitStructCodec(writer, definition, lenientStructs.Contains(definition));
            }

            foreach (var definition in checkedEnums)
            {
                writer.BlankLine();
                CodecEmitter.EmitIsDefined(writer, definition);
            }

            writer.Close();
        }

        return FinishFile(writer);
    }

    private static void EmitSizeConstants(
        CSharpWriter writer,
        IEnumerable<StructDefinition> structs,
        IEnumerable<MessageDefinition> messages)
    {
        foreach (var message in messages)
        {
            if (message.SizeConstant is not null)
            {
                writer.BlankLine();
                writer.Summary($"The size in bytes of every {message.Name} packet, including the frame header.");
                writer.Line($"public const int {message.SizeConstant} = {WireSizes.OfMessage(message).Minimum};");
            }

            if (message.EmptySizeConstant is not null)
            {
                writer.BlankLine();
                writer.Summary($"The size in bytes of a {message.Name} packet whose arrays are empty, including the frame header.");
                writer.Line($"public const int {message.EmptySizeConstant} = {WireSizes.EmptyMessageSize(message)};");
            }
        }

        foreach (var definition in structs.Where(definition => definition.SizeConstant is not null))
        {
            writer.BlankLine();
            writer.Summary($"The encoded size in bytes of one {definition.Name}.");
            writer.Line($"public const int {definition.SizeConstant} = {WireSizes.OfFields(definition.Fields).Minimum};");
        }
    }

    private static CSharpWriter StartFile(ProtocolModel model, bool usesSystem)
    {
        var writer = new CSharpWriter();
        writer.Line("// <auto-generated>");
        writer.Line($"// Generated by tools/ProtocolGenerator from {ProtocolLayout.SchemaPath}.");
        writer.Line("// Change the schema and run the generator instead of editing this file.");
        writer.Line("// </auto-generated>");
        if (usesSystem)
        {
            writer.Line("using System;");
        }

        writer.BlankLine();
        writer.Open($"namespace {model.Namespace}");
        return writer;
    }

    private static string FinishFile(CSharpWriter writer)
    {
        writer.Close();
        return writer.ToString();
    }

    private static void EmitEnum(CSharpWriter writer, EnumDefinition definition)
    {
        writer.Summary(definition.Description);
        if (definition.Flags)
        {
            writer.Line("[Flags]");
        }

        writer.Open($"public enum {definition.ClassName} : byte");
        for (var index = 0; index < definition.Values.Count; index++)
        {
            var value = definition.Values[index];
            writer.Summary(value.Description);
            writer.Line($"{value.Name} = {value.Value}{(index + 1 < definition.Values.Count ? "," : string.Empty)}");
        }

        writer.Close();
    }

    private static void EmitDescriptorClass(CSharpWriter writer)
    {
        writer.Summary("How one message type travels: its direction, channel, and delivery.");
        writer.Open("public sealed class RealtimeMessageDescriptor");
        writer.Line("public RealtimeMessageDescriptor(");
        writer.Line("    RealtimeMessageType type,");
        writer.Line("    RealtimeMessageDirection direction,");
        writer.Line("    byte channel,");
        writer.Open("    RealtimeDelivery delivery)");
        writer.Line("Type = type;");
        writer.Line("Direction = direction;");
        writer.Line("Channel = channel;");
        writer.Line("Delivery = delivery;");
        writer.Close();
        writer.BlankLine();
        writer.Line("public RealtimeMessageType Type { get; }");
        writer.BlankLine();
        writer.Line("public RealtimeMessageDirection Direction { get; }");
        writer.BlankLine();
        writer.Line("public byte Channel { get; }");
        writer.BlankLine();
        writer.Line("public RealtimeDelivery Delivery { get; }");
        writer.Close();
    }

    private static void EmitProtocolConstants(CSharpWriter writer, ProtocolModel model)
    {
        writer.Summary("The protocol version. Peers with another version never exchange messages.");
        writer.Line($"public const ushort Version = {model.Version};");
        writer.BlankLine();
        writer.Summary("The LiteNetLib connection key.");
        writer.Line($"public const string ConnectionKey = {CSharpWriter.Literal(model.ConnectionKey)};");
        writer.BlankLine();
        writer.Summary("The largest packet in bytes, including the seven-byte frame header.");
        writer.Line($"public const int MaximumPacketSize = {model.MaximumPacketSize};");
        writer.BlankLine();
        writer.Summary("The largest unreliable packet in bytes, including the frame header.");
        writer.Line($"public const int MaximumUnreliablePacketSize = {model.MaximumUnreliablePacketSize};");
        writer.BlankLine();
        writer.Summary("The number of LiteNetLib channels both peers configure.");
        writer.Line($"public const byte ChannelCount = {model.ChannelCount};");
        foreach (var channel in model.Channels)
        {
            writer.BlankLine();
            writer.Summary(channel.Description);
            writer.Line($"public const byte {channel.Name} = {channel.Id};");
        }

        EmitLimits(writer, model.Limits.Where(limit => limit.Visibility == LimitVisibility.Protocol), "public", useAlias: false);
        writer.BlankLine();
        writer.Line($"private const uint Magic = 0x{model.Magic.ToString("X8", CultureInfo.InvariantCulture)};");
        EmitLimits(writer, model.Limits.Where(limit => limit.Visibility == LimitVisibility.Public), "private", useAlias: true);
        EmitLimits(writer, model.Limits.Where(limit => limit.Visibility == LimitVisibility.Private), "private", useAlias: false);
    }

    private static void EmitLimits(CSharpWriter writer, IEnumerable<LimitDefinition> limits, string access, bool useAlias)
    {
        foreach (var limit in limits)
        {
            if (access == "public")
            {
                writer.BlankLine();
                writer.Summary(limit.Description);
                writer.Line($"public const int {limit.Name} = {limit.Value};");
            }
            else
            {
                var value = useAlias ? $"RealtimeProtocolLimits.{limit.Name}" : limit.Value.ToString(CultureInfo.InvariantCulture);
                writer.Line($"private const int {limit.Name} = {value};");
            }
        }
    }

    private static void EmitDescriptorTable(CSharpWriter writer, IReadOnlyList<MessageDefinition> messages)
    {
        writer.Line("// Indexed by message type; types without a message are null.");
        writer.Open("private static readonly RealtimeMessageDescriptor[] Descriptors =");
        var byId = messages.ToDictionary(message => message.Id);
        var highest = messages.Max(message => message.Id);
        for (var id = 0; id <= highest; id++)
        {
            var separator = id < highest ? "," : string.Empty;
            if (!byId.TryGetValue((byte)id, out var message))
            {
                writer.Line("null" + separator);
                continue;
            }

            writer.Line(
                $"new RealtimeMessageDescriptor(RealtimeMessageType.{message.Name}, RealtimeMessageDirection.{message.Direction}, "
                + $"{message.Channel.Name}, RealtimeDelivery.{message.Delivery}){separator}");
        }

        writer.Close(";");
        writer.BlankLine();
        writer.Summary("Returns how a defined message type travels.");
        writer.Open("public static bool TryGetDescriptor(RealtimeMessageType type, out RealtimeMessageDescriptor descriptor)");
        writer.Line("var index = (int)type;");
        writer.Line("descriptor = index < Descriptors.Length ? Descriptors[index] : null;");
        writer.Line("return descriptor != null;");
        writer.Close();
    }
}
