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

    public IReadOnlyList<MessageDefinition> Messages { get; }

    // Families in the order their first message appears, which is the order of the
    // generated files.
    public IReadOnlyList<string> Families =>
        Messages.Select(message => message.Family)
            .Concat(Enums.Select(definition => definition.Family))
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

public sealed record EnumValueDefinition(string Name, byte Value, string? Description);

public sealed record EnumDefinition(
    string Name,
    string ClassName,
    string Family,
    bool Flags,
    string Description,
    IReadOnlyList<EnumValueDefinition> Values);

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

public sealed record MessageDefinition(
    string Name,
    byte Id,
    string Family,
    MessageDirection Direction,
    ChannelDefinition Channel,
    MessageDelivery Delivery,
    MessageCodec Codec,
    string Description);
