using System;
using System.Buffers.Binary;
using System.Text;

namespace Mmorpg.GameProtocol
{
    // Framing and the primitive readers and writers that the generated codecs use. The
    // version, limits, messages, and codecs are generated from the schema in Schema~, and
    // each family folder keeps only its cross-field rules.
    public static partial class RealtimeProtocol
    {
        private const int HeaderSize = sizeof(uint) + sizeof(ushort) + sizeof(byte);

        private const int GuidSize = 16;

        // Decoding rejects invalid UTF-8 instead of replacing it.
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static bool TryReadMessageType(byte[] data, out RealtimeMessageType messageType)
        {
            messageType = default;
            if (data == null || data.Length < HeaderSize)
            {
                return false;
            }

            var header = new ReadOnlySpan<byte>(data);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
                || BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(sizeof(uint))) != Version)
            {
                return false;
            }

            messageType = (RealtimeMessageType)header[HeaderSize - 1];
            return TryGetDescriptor(messageType, out _);
        }

        // Allocates the packet at its exact size and writes the frame header.
        private static PacketWriter StartPacket(RealtimeMessageType messageType, int payloadSize)
        {
            var size = HeaderSize + payloadSize;
            if (size > MaximumPacketSize)
            {
                throw new InvalidOperationException($"Realtime packet exceeds the {MaximumPacketSize} byte limit.");
            }

            var writer = new PacketWriter(new byte[size]);
            writer.WriteUInt32(Magic);
            writer.WriteUInt16(Version);
            writer.WriteByte((byte)messageType);
            return writer;
        }

        private static bool TryStartPacket(
            byte[] data,
            RealtimeMessageType expectedType,
            out PacketReader reader,
            out string error)
        {
            reader = default;
            if (data == null || data.Length > MaximumPacketSize)
            {
                error = "Packet length is invalid.";
                return false;
            }

            reader = new PacketReader(data);
            if (!reader.TryReadUInt32(out var magic, out error))
            {
                error = "Packet header is incomplete.";
                return false;
            }

            if (magic != Magic)
            {
                error = "Packet magic is invalid.";
                return false;
            }

            if (!reader.TryReadUInt16(out var version, out error))
            {
                error = "Packet header is incomplete.";
                return false;
            }

            if (version != Version)
            {
                error = "Protocol version is unsupported.";
                return false;
            }

            if (!reader.TryReadByte(out var messageType, out error))
            {
                error = "Packet header is incomplete.";
                return false;
            }

            if ((RealtimeMessageType)messageType != expectedType)
            {
                error = "Packet message type is invalid.";
                return false;
            }

            return true;
        }

        // A u16 UTF-8 byte length, then the bytes.
        private static int StringSize(string value)
        {
            return sizeof(ushort) + Encoding.UTF8.GetByteCount(value);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        // Writes little-endian values into a packet that was allocated at its exact size.
        // Callers validate every value first, so the writer only copies bytes.
        private ref struct PacketWriter
        {
            private readonly byte[] buffer;
            private int position;

            public PacketWriter(byte[] buffer)
            {
                this.buffer = buffer;
                position = 0;
            }

            public void WriteByte(byte value)
            {
                buffer[position] = value;
                position++;
            }

            public void WriteBoolean(bool value)
            {
                WriteByte(value ? (byte)1 : (byte)0);
            }

            public void WriteUInt16(ushort value)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(position), value);
                position += sizeof(ushort);
            }

            public void WriteUInt32(uint value)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(position), value);
                position += sizeof(uint);
            }

            public void WriteUInt64(ulong value)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(position), value);
                position += sizeof(ulong);
            }

            public void WriteInt32(int value)
            {
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(position), value);
                position += sizeof(int);
            }

            public void WriteInt64(long value)
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(position), value);
                position += sizeof(long);
            }

            public void WriteSingle(float value)
            {
                WriteInt32(BitConverter.SingleToInt32Bits(value));
            }

            // The byte order of Guid.ToByteArray.
            public void WriteGuid(Guid value)
            {
                if (!value.TryWriteBytes(buffer.AsSpan(position)))
                {
                    throw new InvalidOperationException("Realtime packet is too small for its identifier.");
                }

                position += GuidSize;
            }

            public void WriteString(string value)
            {
                var length = Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, position + sizeof(ushort));
                WriteUInt16((ushort)length);
                position += length;
            }

            // Every packet is sized exactly, so a mismatch is a generator defect.
            public byte[] Finish()
            {
                if (position != buffer.Length)
                {
                    throw new InvalidOperationException("Realtime packet size does not match its encoded bytes.");
                }

                return buffer;
            }
        }

        // Reads little-endian values and reports the first truncated or malformed one.
        private ref struct PacketReader
        {
            private readonly ReadOnlySpan<byte> data;
            private int position;

            public PacketReader(ReadOnlySpan<byte> data)
            {
                this.data = data;
                position = 0;
            }

            public bool TryReadByte(out byte value, out string error)
            {
                if (!Has(sizeof(byte), "Packet byte is incomplete.", out error))
                {
                    value = 0;
                    return false;
                }

                value = data[position];
                position++;
                return true;
            }

            // Any non-zero byte reads as true.
            public bool TryReadBoolean(out bool value, out string error)
            {
                if (!Has(sizeof(byte), "Packet boolean is incomplete.", out error))
                {
                    value = false;
                    return false;
                }

                value = data[position] != 0;
                position++;
                return true;
            }

            public bool TryReadUInt16(out ushort value, out string error)
            {
                if (!Has(sizeof(ushort), "Packet unsigned integer is incomplete.", out error))
                {
                    value = 0;
                    return false;
                }

                value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(position));
                position += sizeof(ushort);
                return true;
            }

            public bool TryReadUInt32(out uint value, out string error)
            {
                if (!Has(sizeof(uint), "Packet unsigned integer is incomplete.", out error))
                {
                    value = 0;
                    return false;
                }

                value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position));
                position += sizeof(uint);
                return true;
            }

            public bool TryReadUInt64(out ulong value, out string error)
            {
                if (!Has(sizeof(ulong), "Packet unsigned integer is incomplete.", out error))
                {
                    value = 0;
                    return false;
                }

                value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(position));
                position += sizeof(ulong);
                return true;
            }

            public bool TryReadInt32(out int value, out string error)
            {
                if (!Has(sizeof(int), "Packet integer is incomplete.", out error))
                {
                    value = 0;
                    return false;
                }

                value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(position));
                position += sizeof(int);
                return true;
            }

            public bool TryReadInt64(out long value, out string error)
            {
                if (!Has(sizeof(long), "Packet integer is incomplete.", out error))
                {
                    value = 0;
                    return false;
                }

                value = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(position));
                position += sizeof(long);
                return true;
            }

            public bool TryReadSingle(out float value, out string error)
            {
                if (!Has(sizeof(float), "Packet number is incomplete.", out error))
                {
                    value = 0f;
                    return false;
                }

                value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(position)));
                position += sizeof(float);
                if (!IsFinite(value))
                {
                    error = "Packet number is not finite.";
                    return false;
                }

                return true;
            }

            public bool TryReadGuid(out Guid value, out string error)
            {
                if (!Has(GuidSize, "Packet identifier is incomplete.", out error))
                {
                    value = Guid.Empty;
                    return false;
                }

                value = new Guid(data.Slice(position, GuidSize));
                position += GuidSize;
                return true;
            }

            // Text has 1 to maximumLength UTF-16 characters, or 0 with allowBlank, and at
            // most four bytes per character. Whitespace is read as is: validation decides
            // whether blank text is valid.
            public bool TryReadString(int maximumLength, out string value, out string error, bool allowBlank = false)
            {
                value = string.Empty;
                if (!Has(sizeof(ushort), "Packet string is invalid.", out error))
                {
                    return false;
                }

                var byteLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(position));
                position += sizeof(ushort);
                if ((byteLength == 0 && !allowBlank) || byteLength > maximumLength * 4)
                {
                    error = "Packet string length is invalid.";
                    return false;
                }

                if (!Has(byteLength, "Packet string is incomplete.", out error))
                {
                    return false;
                }

                try
                {
                    value = StrictUtf8.GetString(data.Slice(position, byteLength));
                }
                catch (DecoderFallbackException)
                {
                    value = string.Empty;
                    error = "Packet string is invalid.";
                    return false;
                }

                position += byteLength;
                if (value.Length > maximumLength)
                {
                    error = "Packet string exceeds its character limit.";
                    return false;
                }

                return true;
            }

            public bool TryFinish(out string error)
            {
                if (position == data.Length)
                {
                    error = string.Empty;
                    return true;
                }

                error = "Packet contains trailing data.";
                return false;
            }

            private bool Has(int count, string incomplete, out string error)
            {
                if (data.Length - position >= count)
                {
                    error = string.Empty;
                    return true;
                }

                error = incomplete;
                return false;
            }
        }
    }
}
