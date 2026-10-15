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

// A u16 UTF-8 byte length, then the bytes. At most MaximumLength UTF-16 characters.
// Text must not be empty or whitespace when encoded, unless AllowBlank lets it be
// empty or whitespace in both directions.
public sealed record StringWireType(IntBound MaximumLength, bool AllowBlank = false) : WireType;

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
    string? DistinctBy,
    StringFormat Format,
    IReadOnlyList<string>? OneOf)
{
    public static readonly ValueRules None = new(null, null, null, null, false, null, false, null, StringFormat.Any, null);
}

public enum StringFormat
{
    Any,

    // Text that Guid.TryParse accepts.
    Guid
}

public sealed record ElementDefinition(WireType Type, ValueRules Rules);

// How a field reaches the wire.
public enum FieldPresence
{
    // Always written.
    Always,

    // Written only while the bool field named by ConditionField has ConditionValue.
    Conditional,

    // Written after its own bool, which is true when the value is present: a struct that
    // is not null, or text that is not empty or whitespace. Absent text reads as empty.
    Optional
}

public sealed record FieldDefinition(string Name, string? Description, WireType Type, ValueRules Rules)
{
    public FieldPresence Presence { get; init; } = FieldPresence.Always;

    public string? ConditionField { get; init; }

    public bool ConditionValue { get; init; }

    // Decoding accepts empty or whitespace text in this string field, or in the string
    // fields of this struct field. Encoding still rejects it.
    public bool AcceptBlankOnDecode { get; init; }

    // The constructor turns null into an empty string or array.
    public bool NullAsEmpty { get; init; }
}

// A message whose discriminator field decides which variant fields follow the common
// fields on the wire. Each case has factory methods, one per discriminator value.
public sealed record UnionDefinition(
    string Discriminator,
    IReadOnlyList<UnionPoolField> Pool,
    IReadOnlyList<UnionCase> Cases);

// A variant field and the C# value it has in cases that do not carry it.
public sealed record UnionPoolField(FieldDefinition Field, string AbsentValue);

public sealed record UnionCase(
    IReadOnlyList<UnionKind> Kinds,
    IReadOnlyList<UnionEntry> Entries,
    IReadOnlyList<UnionFieldEntry> CommonOverrides);

public sealed record UnionKind(EnumValueDefinition Value, string Factory);

public abstract record UnionEntry;

// A field in one case: its rules there, and optionally a fixed value. A fixed field is
// written as the model value, which must equal it, and decoded as the fixed value
// whatever the wire carries. Factories leave fixed fields out.
public sealed record UnionFieldEntry(
    FieldDefinition Field,
    string? FixedValue,
    string? ParameterName,
    string? ParameterDefault) : UnionEntry;

// Fields behind a bool that is true when the presence field differs from its absent value.
public sealed record UnionGroupEntry(string PresenceField, IReadOnlyList<UnionFieldEntry> Fields) : UnionEntry;

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
    Unwrap,

    // The message has no fields; encode takes nothing and decode only reports errors.
    Empty
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
    IReadOnlyList<FieldDefinition> Fields,
    UnionDefinition? Union = null)
{
    // An unwrapped struct whose encode method takes the struct's fields as parameters.
    public bool ExpandedEncode { get; init; }
}
