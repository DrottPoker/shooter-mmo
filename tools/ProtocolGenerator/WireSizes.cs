namespace Mmorpg.Tools.ProtocolGenerator;

// The smallest and largest encoded size of a value in bytes.
public readonly record struct SizeRange(long Minimum, long Maximum)
{
    public bool IsFixed => Minimum == Maximum;

    public static SizeRange Fixed(long size)
    {
        return new SizeRange(size, size);
    }

    public static SizeRange operator +(SizeRange left, SizeRange right)
    {
        return new SizeRange(left.Minimum + right.Minimum, left.Maximum + right.Maximum);
    }
}

// Encoded sizes, derived from the schema so size limits are proven before code runs.
public static class WireSizes
{
    public const int FrameHeaderSize = 7;

    public static SizeRange Of(WireType type)
    {
        return type switch
        {
            PrimitiveType primitive => SizeRange.Fixed(PrimitiveSize(primitive.Kind)),
            EnumWireType or BitsWireType => SizeRange.Fixed(1),
            // A UTF-16 character encodes to at most three UTF-8 bytes; a surrogate pair
            // encodes to four bytes for two characters.
            StringWireType text => new SizeRange(2 + 1, 2 + (3L * text.MaximumLength.Value)),
            StructWireType structType => OfFields(structType.Definition.Fields),
            ArrayWireType array => new SizeRange(
                1 + (array.MinimumCount * Of(array.Element.Type).Minimum),
                1 + (array.MaximumCount.Value * Of(array.Element.Type).Maximum)),
            _ => throw new InvalidOperationException($"Unknown wire type {type}.")
        };
    }

    public static SizeRange OfFields(IEnumerable<FieldDefinition> fields)
    {
        return fields.Aggregate(SizeRange.Fixed(0), (total, field) => total + OfField(field));
    }

    // A conditional field may be absent.
    public static SizeRange OfField(FieldDefinition field)
    {
        var size = Of(field.Type);
        return field.Presence == FieldPresence.Conditional ? new SizeRange(0, size.Maximum) : size;
    }

    // The whole packet, including the frame header and the smallest and largest case.
    public static SizeRange OfMessage(MessageDefinition message)
    {
        var size = SizeRange.Fixed(FrameHeaderSize) + OfFields(message.Fields);
        if (message.Union is null)
        {
            return size;
        }

        var cases = message.Union.Cases.Select(unionCase => OfEntries(unionCase.Entries)).ToArray();
        return size + new SizeRange(cases.Min(range => range.Minimum), cases.Max(range => range.Maximum));
    }

    // A group is a bool, then its fields when present.
    private static SizeRange OfEntries(IEnumerable<UnionEntry> entries)
    {
        return entries.Aggregate(SizeRange.Fixed(0), (total, entry) => total + entry switch
        {
            UnionFieldEntry field => Of(field.Field.Type),
            UnionGroupEntry group => new SizeRange(1, 1 + group.Fields.Sum(member => Of(member.Field.Type).Maximum)),
            _ => throw new InvalidOperationException($"Unknown entry {entry}.")
        });
    }

    // The packet size when every top-level array is empty.
    public static long EmptyMessageSize(MessageDefinition message)
    {
        return FrameHeaderSize + message.Fields.Sum(field => field.Type is ArrayWireType ? 1 : Of(field.Type).Minimum);
    }

    public static int PrimitiveSize(PrimitiveKind kind)
    {
        return kind switch
        {
            PrimitiveKind.U8 or PrimitiveKind.Bool => 1,
            PrimitiveKind.U16 => 2,
            PrimitiveKind.U32 or PrimitiveKind.I32 or PrimitiveKind.F32 => 4,
            PrimitiveKind.U64 or PrimitiveKind.I64 => 8,
            PrimitiveKind.Guid => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The primitive is unknown.")
        };
    }
}
