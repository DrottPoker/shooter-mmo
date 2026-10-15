namespace Mmorpg.Tools.ProtocolGenerator;

// The resolved, validated protocol that the emitters turn into C#.
public sealed class ProtocolModel
{
    public ProtocolModel(
        string description,
        string @namespace,
        ushort version,
        uint magic,
        string connectionKey,
        int maximumPacketSize,
        int maximumUnreliablePacketSize,
        byte channelCount,
        IReadOnlyList<ChannelDefinition> channels,
        IReadOnlyList<LimitDefinition> limits,
        IReadOnlyList<EnumDefinition> enums,
        IReadOnlyList<StructDefinition> structs,
        IReadOnlyList<MessageDefinition> messages)
    {
        Description = description;
        Namespace = @namespace;
        Version = version;
        Magic = magic;
        ConnectionKey = connectionKey;
        MaximumPacketSize = maximumPacketSize;
        MaximumUnreliablePacketSize = maximumUnreliablePacketSize;
        ChannelCount = channelCount;
        Channels = channels;
        Limits = limits;
        Enums = enums;
        Structs = structs;
        Messages = messages;
    }

    public string Description { get; }

    public string Namespace { get; }

    public ushort Version { get; }

    public uint Magic { get; }

    public string ConnectionKey { get; }

    public int MaximumPacketSize { get; }

    public int MaximumUnreliablePacketSize { get; }

    public byte ChannelCount { get; }

    public IReadOnlyList<ChannelDefinition> Channels { get; }

    public IReadOnlyList<LimitDefinition> Limits { get; }

    public IReadOnlyList<EnumDefinition> Enums { get; }

    public IReadOnlyList<StructDefinition> Structs { get; }

    public IReadOnlyList<MessageDefinition> Messages { get; }

    // Families in the order their first message appears, which is the order of the
    // generated files.
    public IReadOnlyList<string> Families =>
        Messages.Select(message => message.Family)
            .Concat(Enums.Select(definition => definition.Family))
            .Concat(Structs.Select(definition => definition.Family))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

public sealed record ChannelDefinition(string Name, byte Id, string Description);

public enum LimitVisibility
{
    // A RealtimeProtocolLimits constant that content compilers may reference.
    Public,

    // A public RealtimeProtocol constant.
    Protocol,

    // A private RealtimeProtocol constant.
    Private
}

public sealed record LimitDefinition(string Name, int Value, LimitVisibility Visibility, string Description);

// An integer bound written either as a number or as the name of a limit.
public sealed record IntBound(int Value, string? LimitName)
{
    public string Expression => LimitName ?? Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

// A numeric bound of a value rule. Integer bounds may name a limit.
public sealed record NumericBound(double Value, string? LimitName);

public sealed record EnumValueDefinition(string Name, byte Value, string? Description);

public sealed record EnumDefinition(
    string Name,
    string ClassName,
    string Family,
    bool Flags,
    string Description,
    IReadOnlyList<EnumValueDefinition> Values);

public enum PrimitiveKind
{
    U8,
    U16,
    U32,
    U64,
    I32,
    I64,
    F32,
    Bool,
    Guid
}

// The wire type of a field or array element.
public abstract record WireType;

public sealed record PrimitiveType(PrimitiveKind Kind) : WireType;

public sealed record EnumWireType(EnumDefinition Definition) : WireType;

public sealed record StructWireType(StructDefinition Definition) : WireType;

// A u8 count followed by the elements.
public sealed record ArrayWireType(ElementDefinition Element, int MinimumCount, IntBound MaximumCount) : WireType;

// One byte whose bits are boolean properties. Unknown bits are rejected.
public sealed record BitsWireType(IReadOnlyList<BitDefinition> Bits) : WireType;

public sealed record BitDefinition(string Name, string? Description);

// Value rules that apply to one field or one array element.
public sealed record ValueRules(
    NumericBound? Minimum,
    NumericBound? Maximum,
    NumericBound? ExclusiveMinimum,
    NumericBound? ExclusiveMaximum,
    bool NonEmpty,
    string? LessThan,
    bool Distinct,
    string? DistinctBy)
{
    public static readonly ValueRules None = new(null, null, null, null, false, null, false, null);
}

public sealed record ElementDefinition(WireType Type, ValueRules Rules);

public sealed record FieldDefinition(string Name, string? Description, WireType Type, ValueRules Rules);

public sealed class StructDefinition
{
    public StructDefinition(string name, string className, string family, string description, bool rules, string? sizeConstant)
    {
        Name = name;
        ClassName = className;
        Family = family;
        Description = description;
        Rules = rules;
        SizeConstant = sizeConstant;
    }

    public string Name { get; }

    public string ClassName { get; }

    public string Family { get; }

    public string Description { get; }

    // Cross-field rules live in a hand-written SatisfiesRules overload in the family folder.
    public bool Rules { get; }

    public string? SizeConstant { get; }

    public List<FieldDefinition> Fields { get; } = [];
}

public enum MessageDirection
{
    ClientToServer = 1,
    ServerToClient = 2
}

public enum MessageDelivery
{
    ReliableOrdered = 1,
    Sequenced = 2,
    Unreliable = 3
}

public enum MessageCodec
{
    // The codec is still written by hand in the family folder.
    HandWritten,

    // The generator writes the model and the codec.
    Generated
}

public enum MessageApi
{
    // Encode and decode a generated message class.
    Class,

    // The message has one field, and encode and decode use that field's value directly.
    Unwrap
}

public sealed record MessageDefinition(
    string Name,
    byte Id,
    string Family,
    MessageDirection Direction,
    ChannelDefinition Channel,
    MessageDelivery Delivery,
    MessageCodec Codec,
    string Description,
    MessageApi Api,
    string ClassName,
    string Parameter,
    bool Rules,
    string? SizeConstant,
    string? EmptySizeConstant,
    bool RuntimeSizeCheck,
    IReadOnlyList<FieldDefinition> Fields);
