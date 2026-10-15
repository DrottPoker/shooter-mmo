using System;
using System.IO;
using System.Text;

namespace Mmorpg.GameProtocol
{
    // Framing and the primitive readers and writers shared by every message family. The
    // version, limits, and message types are generated from the schema in Schema~, and
    // each family keeps its messages and its codec in its own folder.
    public static partial class RealtimeProtocol
    {
        public static bool TryReadMessageType(byte[] data, out RealtimeMessageType messageType)
        {
            messageType = default;
            if (data == null || data.Length < sizeof(uint) + sizeof(ushort) + sizeof(byte))
            {
                return false;
            }

            using (var stream = new MemoryStream(data, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                return reader.ReadUInt32() == Magic
                    && reader.ReadUInt16() == Version
                    && Enum.IsDefined(typeof(RealtimeMessageType), messageType = (RealtimeMessageType)reader.ReadByte());
            }
        }

        private static byte[] Encode(RealtimeMessageType messageType, Action<BinaryWriter> writePayload)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write((byte)messageType);
                writePayload(writer);
                writer.Flush();

                if (stream.Length > MaximumPacketSize)
                {
                    throw new InvalidOperationException(
                        $"Realtime packet exceeds the {MaximumPacketSize} byte limit.");
                }

                return stream.ToArray();
            }
        }

        private static byte[] EncodeError(RealtimeMessageType messageType, string code, string message)
        {
            return Encode(messageType, writer =>
            {
                WriteString(writer, code, MaximumErrorCodeLength, nameof(code));
                WriteString(writer, message, MaximumErrorMessageLength, nameof(message));
            });
        }

        private static bool TryDecodeError(
            byte[] data,
            RealtimeMessageType expectedType,
            out RealtimeError realtimeError,
            out string error)
        {
            realtimeError = null;
            if (!TryCreateReader(data, expectedType, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadString(reader, MaximumErrorCodeLength, out var code, out error)
                    || !TryReadString(reader, MaximumErrorMessageLength, out var message, out error)
                    || !TryFinish(stream, out error))
                {
                    return false;
                }

                realtimeError = new RealtimeError(code, message);
                return true;
            }
        }

        private static bool TryCreateReader(
            byte[] data,
            RealtimeMessageType expectedType,
            out MemoryStream stream,
            out BinaryReader reader,
            out string error)
        {
            stream = null;
            reader = null;
            error = string.Empty;

            if (data == null || data.Length > MaximumPacketSize)
            {
                error = "Packet length is invalid.";
                return false;
            }

            try
            {
                stream = new MemoryStream(data, writable: false);
                reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                if (reader.ReadUInt32() != Magic)
                {
                    error = "Packet magic is invalid.";
                    DisposeReader(stream, reader);
                    return false;
                }

                if (reader.ReadUInt16() != Version)
                {
                    error = "Protocol version is unsupported.";
                    DisposeReader(stream, reader);
                    return false;
                }

                if ((RealtimeMessageType)reader.ReadByte() != expectedType)
                {
                    error = "Packet message type is invalid.";
                    DisposeReader(stream, reader);
                    return false;
                }

                return true;
            }
            catch (EndOfStreamException)
            {
                error = "Packet header is incomplete.";
                DisposeReader(stream, reader);
                return false;
            }
        }

        private static bool TryReadString(
            BinaryReader reader,
            int maximumLength,
            out string value,
            out string error)
        {
            value = string.Empty;
            error = string.Empty;

            try
            {
                var byteLength = reader.ReadUInt16();
                if (byteLength == 0 || byteLength > maximumLength * 4)
                {
                    error = "Packet string length is invalid.";
                    return false;
                }

                var bytes = reader.ReadBytes(byteLength);
                if (bytes.Length != byteLength)
                {
                    error = "Packet string is incomplete.";
                    return false;
                }

                value = new UTF8Encoding(false, true).GetString(bytes);
                if (value.Length > maximumLength)
                {
                    error = "Packet string exceeds its character limit.";
                    return false;
                }

                return true;
            }
            catch (Exception exception) when (
                exception is EndOfStreamException || exception is DecoderFallbackException)
            {
                error = "Packet string is invalid.";
                return false;
            }
        }

        private static bool TryReadBoolean(BinaryReader reader, out bool value, out string error)
        {
            value = false;
            error = string.Empty;

            try
            {
                value = reader.ReadBoolean();
                return true;
            }
            catch (EndOfStreamException)
            {
                error = "Packet boolean is incomplete.";
                return false;
            }
        }

        private static void WriteGuid(BinaryWriter writer, Guid value)
        {
            writer.Write(value.ToByteArray());
        }

        private static bool TryReadGuid(BinaryReader reader, out Guid value, out string error)
        {
            try
            {
                var bytes = reader.ReadBytes(16);
                if (bytes.Length != 16)
                {
                    value = Guid.Empty;
                    error = "Packet identifier is incomplete.";
                    return false;
                }

                value = new Guid(bytes);
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = Guid.Empty;
                error = "Packet identifier is incomplete.";
                return false;
            }
        }

        private static bool TryReadByte(BinaryReader reader, out byte value, out string error)
        {
            try
            {
                value = reader.ReadByte();
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0;
                error = "Packet byte is incomplete.";
                return false;
            }
        }

        private static bool TryReadUInt16(BinaryReader reader, out ushort value, out string error)
        {
            try
            {
                value = reader.ReadUInt16();
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0;
                error = "Packet unsigned integer is incomplete.";
                return false;
            }
        }

        private static bool TryReadUInt32(BinaryReader reader, out uint value, out string error)
        {
            try
            {
                value = reader.ReadUInt32();
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0;
                error = "Packet unsigned integer is incomplete.";
                return false;
            }
        }

        private static bool TryReadUInt64(BinaryReader reader, out ulong value, out string error)
        {
            try
            {
                value = reader.ReadUInt64();
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0;
                error = "Packet unsigned integer is incomplete.";
                return false;
            }
        }

        private static bool TryReadInt32(BinaryReader reader, out int value, out string error)
        {
            try
            {
                value = reader.ReadInt32();
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0;
                error = "Packet integer is incomplete.";
                return false;
            }
        }

        private static bool TryReadInt64(BinaryReader reader, out long value, out string error)
        {
            try
            {
                value = reader.ReadInt64();
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0;
                error = "Packet integer is incomplete.";
                return false;
            }
        }

        private static bool TryReadSingle(BinaryReader reader, out float value, out string error)
        {
            try
            {
                value = reader.ReadSingle();
                if (!IsFinite(value))
                {
                    error = "Packet number is not finite.";
                    return false;
                }

                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0f;
                error = "Packet number is incomplete.";
                return false;
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool TryFinish(MemoryStream stream, out string error)
        {
            if (stream.Position == stream.Length)
            {
                error = string.Empty;
                return true;
            }

            error = "Packet contains trailing data.";
            return false;
        }

        private static void WriteString(
            BinaryWriter writer,
            string value,
            int maximumLength,
            string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            {
                throw new ArgumentException(
                    $"Value must contain between 1 and {maximumLength} characters.",
                    parameterName);
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException("Value is too large to encode.", parameterName);
            }

            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static void DisposeReader(MemoryStream stream, BinaryReader reader)
        {
            reader?.Dispose();
            stream?.Dispose();
        }
    }
}
