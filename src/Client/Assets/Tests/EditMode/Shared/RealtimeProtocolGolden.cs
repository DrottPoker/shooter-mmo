#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // Golden wire cases for realtime protocol version 14. Compiled into both the Unity
    // EditMode tests and the .NET backend tests, so both runtimes must produce and accept
    // exactly these bytes. Change a vector only together with an intended protocol change
    // and a protocol version bump.
    //
    // Every Verify method returns one line per failure, so a test runner only asserts that
    // the list is empty and the message names every broken case.
    public static partial class RealtimeProtocolGolden
    {
        public const ushort PinnedVersion = 14;
        public const uint PinnedMagic = 0x4F4D4D53;
        private const int HeaderSize = 7;
        private const int PinnedMaximumPacketSize = 1200;

        // Vectors above this size are pinned by length and SHA-256 instead of full hex.
        private const int MaximumInlineVectorSize = 256;

        private static readonly Lazy<Registry> Cases = new Lazy<Registry>(BuildRegistry);

        private static readonly Lazy<Dictionary<RealtimeMessageType, Codec>> Codecs =
            new Lazy<Dictionary<RealtimeMessageType, Codec>>(CreateCodecs);

        // Every message type and variant encodes to its pinned bytes, and decoding those bytes
        // and encoding the result again reproduces them exactly.
        public static IReadOnlyList<string> VerifyVectors()
        {
            var failures = new List<string>();
            var registry = Cases.Value;
            foreach (var vector in registry.Vectors)
            {
                if (!PayloadVectors.TryGetValue(vector.Name, out var pinned))
                {
                    failures.Add($"{vector.Name}: no pinned payload exists.");
                    continue;
                }

                byte[] encoded;
                try
                {
                    encoded = vector.Encode();
                }
                catch (Exception exception)
                {
                    failures.Add($"{vector.Name}: encoding threw {exception.GetType().Name}: {exception.Message}");
                    continue;
                }

                if (!HasHeader(encoded, vector.Type))
                {
                    failures.Add($"{vector.Name}: header is {Hex(encoded.Take(HeaderSize).ToArray())}.");
                    continue;
                }

                var payload = encoded.Skip(HeaderSize).ToArray();
                if (!MatchesPinnedPayload(payload, pinned))
                {
                    failures.Add($"{vector.Name}: payload is {DescribePayload(payload)}, pinned {pinned}.");
                    continue;
                }

                var codec = Codecs.Value[vector.Type];
                var decoded = Decode(codec, encoded, out var decodeFailure);
                if (decodeFailure != null)
                {
                    failures.Add($"{vector.Name}: {decodeFailure}");
                    continue;
                }

                if (!decoded.Ok)
                {
                    failures.Add($"{vector.Name}: its own bytes were rejected.");
                    continue;
                }

                var reencoded = TryEncode(codec, decoded.Value, out var reencodeFailure);
                if (reencoded == null || !reencoded.SequenceEqual(encoded))
                {
                    failures.Add($"{vector.Name}: decoded value encodes as {reencodeFailure ?? Hex(reencoded)}.");
                }
            }

            foreach (var name in PayloadVectors.Keys)
            {
                if (registry.Vectors.All(vector => vector.Name != name))
                {
                    failures.Add($"{name}: pinned payload has no case.");
                }
            }

            return failures;
        }

        // Every valid vector is rejected when truncated at any byte, extended by one byte,
        // or given another magic, version, or message type, and the type reader agrees.
        public static IReadOnlyList<string> VerifyStructuralRejections()
        {
            var failures = new List<string>();
            foreach (var vector in Cases.Value.Vectors)
            {
                byte[] encoded;
                try
                {
                    encoded = vector.Encode();
                }
                catch (Exception exception)
                {
                    failures.Add($"{vector.Name}: encoding threw {exception.GetType().Name}.");
                    continue;
                }

                var codec = Codecs.Value[vector.Type];
                for (var length = 0; length < encoded.Length; length++)
                {
                    ExpectRejected(failures, codec, $"{vector.Name} truncated to {length} bytes", encoded.Take(length).ToArray());
                }

                ExpectRejected(failures, codec, $"{vector.Name} with a trailing byte", encoded.Concat(new byte[] { 0 }).ToArray());
                if (encoded.Length <= PinnedMaximumPacketSize)
                {
                    var oversized = new byte[PinnedMaximumPacketSize + 1];
                    Array.Copy(encoded, oversized, encoded.Length);
                    ExpectRejected(failures, codec, $"{vector.Name} padded above the packet limit", oversized);
                }

                for (var index = 0; index < 4; index++)
                {
                    var corrupted = (byte[])encoded.Clone();
                    corrupted[index] ^= 0xFF;
                    ExpectRejected(failures, codec, $"{vector.Name} with magic byte {index} flipped", corrupted);
                    ExpectTypeUnreadable(failures, $"{vector.Name} with magic byte {index} flipped", corrupted);
                }

                foreach (var version in new[] { PinnedVersion - 1, PinnedVersion + 1, 0, ushort.MaxValue })
                {
                    var corrupted = (byte[])encoded.Clone();
                    corrupted[4] = (byte)version;
                    corrupted[5] = (byte)(version >> 8);
                    ExpectRejected(failures, codec, $"{vector.Name} with version {version}", corrupted);
                    ExpectTypeUnreadable(failures, $"{vector.Name} with version {version}", corrupted);
                }

                for (var type = 0; type <= byte.MaxValue; type++)
                {
                    if (type == (byte)vector.Type)
                    {
                        continue;
                    }

                    var corrupted = (byte[])encoded.Clone();
                    corrupted[6] = (byte)type;
                    ExpectRejected(failures, codec, $"{vector.Name} with message type {type}", corrupted);
                }

                if (!RealtimeProtocol.TryReadMessageType(encoded, out var readType) || readType != vector.Type)
                {
                    failures.Add($"{vector.Name}: the message type reader did not return {vector.Type}.");
                }
            }

            return failures;
        }

        // Hand-built packets that probe every field rule: accepted canonical packets, accepted
        // packets that today's decoder normalizes, and packets that must be rejected.
        public static IReadOnlyList<string> VerifyDecodeCases()
        {
            var failures = new List<string>();
            foreach (var decodeCase in Cases.Value.DecodeCases)
            {
                var codec = Codecs.Value[decodeCase.Type];
                var data = decodeCase.Packet.ToArray();
                var decoded = Decode(codec, data, out var decodeFailure);
                if (decodeFailure != null)
                {
                    failures.Add($"{decodeCase.Name}: {decodeFailure}");
                    continue;
                }

                switch (decodeCase.Expectation)
                {
                    case DecodeExpectation.Rejected:
                        if (decoded.Ok)
                        {
                            failures.Add($"{decodeCase.Name}: was accepted.");
                        }

                        break;
                    case DecodeExpectation.Accepted:
                    case DecodeExpectation.AcceptedAs:
                        if (!decoded.Ok)
                        {
                            failures.Add($"{decodeCase.Name}: was rejected.");
                            break;
                        }

                        var expected = decodeCase.Expectation == DecodeExpectation.Accepted
                            ? data
                            : decodeCase.Canonical.ToArray();
                        var reencoded = TryEncode(codec, decoded.Value, out var reencodeFailure);
                        if (reencoded == null || !reencoded.SequenceEqual(expected))
                        {
                            failures.Add(
                                $"{decodeCase.Name}: decoded value encodes as {reencodeFailure ?? Hex(reencoded)}, expected {Hex(expected)}.");
                        }

                        break;
                    case DecodeExpectation.AcceptedUnencodable:
                        if (!decoded.Ok)
                        {
                            failures.Add($"{decodeCase.Name}: was rejected.");
                        }

                        break;
                }
            }

            return failures;
        }

        // Invalid values fail before anything is sent: ArgumentException for invalid values
        // and InvalidOperationException for packets above the packet limit.
        public static IReadOnlyList<string> VerifyEncodeRejections()
        {
            var failures = new List<string>();
            foreach (var rejection in Cases.Value.EncodeRejections)
            {
                try
                {
                    var bytes = rejection.Encode();
                    failures.Add($"{rejection.Name}: encoded {bytes.Length} bytes.");
                }
                catch (Exception exception)
                {
                    if (!rejection.ExpectedException.IsInstanceOfType(exception))
                    {
                        failures.Add(
                            $"{rejection.Name}: threw {exception.GetType().Name}, expected {rejection.ExpectedException.Name}.");
                    }
                }
            }

            return failures;
        }

        // Constants, enum values, and the message type reader that the wire depends on.
        public static IReadOnlyList<string> VerifyConstants()
        {
            var failures = new List<string>();
            Expect(failures, "Version", RealtimeProtocol.Version, PinnedVersion);
            Expect(failures, "ConnectionKey", RealtimeProtocol.ConnectionKey, "Mmorpg.Realtime.v8");
            Expect(failures, "MaximumPacketSize", RealtimeProtocol.MaximumPacketSize, PinnedMaximumPacketSize);
            Expect(failures, "MaximumUnreliablePacketSize", RealtimeProtocol.MaximumUnreliablePacketSize, 1023);
            Expect(failures, "ControlChannel", RealtimeProtocol.ControlChannel, (byte)0);
            Expect(failures, "MovementInputChannel", RealtimeProtocol.MovementInputChannel, (byte)1);
            Expect(failures, "UnreliableReceiveChannel", RealtimeProtocol.UnreliableReceiveChannel, (byte)0);
            Expect(failures, "ChannelCount", RealtimeProtocol.ChannelCount, (byte)2);
            Expect(failures, "MaximumInputBatchSize", RealtimeProtocol.MaximumInputBatchSize, 4);
            Expect(failures, "OwnerSimulationSnapshotPacketSize", RealtimeProtocol.OwnerSimulationSnapshotPacketSize, 48);
            Expect(failures, "MaximumSnapshotEntitiesPerChunk", RealtimeProtocol.MaximumSnapshotEntitiesPerChunk, 24);
            Expect(failures, "SimulationSnapshotPacketHeaderSize", RealtimeProtocol.SimulationSnapshotPacketHeaderSize, 20);
            Expect(failures, "SimulationSnapshotEntitySize", RealtimeProtocol.SimulationSnapshotEntitySize, 41);
            Expect(failures, "Limits.MaximumIdentifierLength", RealtimeProtocolLimits.MaximumIdentifierLength, 128);
            Expect(failures, "Limits.MaximumNameLength", RealtimeProtocolLimits.MaximumNameLength, 128);
            Expect(failures, "Limits.MaximumEquipmentSlotIdLength", RealtimeProtocolLimits.MaximumEquipmentSlotIdLength, 64);
            Expect(failures, "Limits.MaximumSlotTagLength", RealtimeProtocolLimits.MaximumSlotTagLength, 64);
            Expect(failures, "Limits.MaximumSlotTags", RealtimeProtocolLimits.MaximumSlotTags, 16);
            Expect(failures, "Limits.MaximumDefinitionIdLength", RealtimeProtocolLimits.MaximumDefinitionIdLength, 128);

            ExpectEnum<RealtimeMessageType>(
                failures,
                "JoinRequest=1 JoinAccepted=2 JoinRejected=3 LeaveRequest=4 LeaveAccepted=5 LeaveRejected=6 "
                + "ServerDisconnect=7 MovementInputBatch=8 SimulationSnapshot=9 EntitySpawn=10 EntityDespawn=11 "
                + "CarryStateChanged=12 ItemOperationIntent=13 ItemOperationResult=14 CorpsePresenceSnapshotChunk=15 "
                + "CorpseInteractionIntent=16 CorpseInteractionResult=17 CorpseViewStateChunk=18 CorpseViewClosed=19 "
                + "WorldActorSpawn=20 WorldActorState=21 WorldActorDespawn=22 WorldInteractionIntent=23 "
                + "WorldInteractionOpened=24 WorldInteractionResult=25 WorldInteractionClosed=26 OwnerSimulationSnapshot=27");
            ExpectEnum<RealtimeMovementButtons>(failures, "None=0 Sprint=1 Jump=2 Aim=4");
            ExpectEnum<RealtimeEntityKind>(failures, "Player=1");
            ExpectEnum<RealtimeItemOperationKind>(
                failures,
                "Relocate=1 Equip=2 Unequip=3 SplitStack=4 MergeStacks=5 Destroy=6 ClaimRecoveryDelivery=7 SwapContainerItems=8");
            ExpectEnum<RealtimeCorpseInteractionKind>(
                failures,
                "Open=1 Close=2 Refresh=3 LootItem=4 LootPartialStack=5 SwapBag=6 DepositItem=7 DepositPartialStack=8 "
                + "MoveItem=9 MovePartialStack=10");
            ExpectEnum<RealtimeCorpseViewUpdateKind>(failures, "Snapshot=1 Delta=2");
            ExpectEnum<RealtimeWorldActorKind>(failures, "Npc=1 Mob=2");
            ExpectEnum<RealtimeWorldActorDisposition>(failures, "Friendly=1 Neutral=2 Hostile=3");
            ExpectEnum<RealtimeWorldActorActivityTier>(failures, "EventDriven=1 Dormant=2 Active=3");
            ExpectEnum<RealtimeWorldInteractionTargetKind>(failures, "WorldActor=1 Corpse=2");
            ExpectEnum<RealtimeWorldInteractionOperationKind>(failures, "Open=1 Close=2 CapabilityAction=3");
            ExpectEnum<RealtimeWorldActorCapabilityKind>(
                failures,
                "Dialogue=1 Vendor=2 QuestOffer=3 QuestTurnIn=4 Crafting=5 Insurance=6 Trainer=7 Bank=8 RecoveryStorage=9");
            ExpectEnum<RealtimeNpcLifecycleActionKind>(
                failures,
                "None=0 ApplyInsurance=1 RemoveInsurance=2 AcceptQuest=3 AbandonQuest=4");

            var minimal = Wire.Message(RealtimeMessageType.LeaveAccepted).ToArray();
            Expect(failures, "LeaveAccepted header", Hex(minimal), "53 4d 4d 4f 0e 00 05");
            ExpectTypeReader(failures, "null packet", null, false, default);
            ExpectTypeReader(failures, "six bytes", minimal.Take(6).ToArray(), false, default);
            ExpectTypeReader(failures, "header only", minimal, true, RealtimeMessageType.LeaveAccepted);
            ExpectTypeReader(failures, "type 0", Wire.Header(0).ToArray(), false, default);
            ExpectTypeReader(failures, "type 28", Wire.Header(28).ToArray(), false, default);
            ExpectTypeReader(failures, "type 255", Wire.Header(255).ToArray(), false, default);
            ExpectTypeReader(failures, "type 27", Wire.Header(27).ToArray(), true, RealtimeMessageType.OwnerSimulationSnapshot);

            // The type reader looks at the header only, so any payload length is fine.
            ExpectTypeReader(
                failures,
                "header with an oversized payload",
                Wire.Header(1).Bytes(new byte[PinnedMaximumPacketSize]).ToArray(),
                true,
                RealtimeMessageType.JoinRequest);
            return failures;
        }

        // Prints the payload of every vector in the layout of RealtimeProtocolGoldenVectors.cs.
        // Use it only to pin an intended protocol change, never to make a failing test pass.
        public static string FormatPinnedPayloads()
        {
            var builder = new StringBuilder();
            foreach (var vector in Cases.Value.Vectors.OrderBy(vector => vector.Name, StringComparer.Ordinal))
            {
                var payload = vector.Encode().Skip(HeaderSize).ToArray();
                builder.Append("            [\"").Append(vector.Name).Append("\"] =");
                if (payload.Length > MaximumInlineVectorSize)
                {
                    builder.Append(" \"").Append(DescribePayload(payload)).Append("\",\n");
                    continue;
                }

                if (payload.Length == 0)
                {
                    builder.Append(" \"\",\n");
                    continue;
                }

                for (var offset = 0; offset < payload.Length; offset += 16)
                {
                    var line = Hex(payload.Skip(offset).Take(16).ToArray());
                    builder.Append(offset == 0 ? "\n                \"" : " +\n                \"").Append(line);
                    builder.Append(offset + 16 < payload.Length ? " \"" : "\"");
                }

                builder.Append(",\n");
            }

            return builder.ToString();
        }

        private static Registry BuildRegistry()
        {
            var registry = new Registry();
            RegisterSession(registry);
            RegisterMovement(registry);
            RegisterReplication(registry);
            RegisterItems(registry);
            RegisterWorldActors(registry);
            RegisterCorpses(registry);
            return registry;
        }

        private static bool HasHeader(byte[] packet, RealtimeMessageType type)
        {
            return packet.Length >= HeaderSize
                && packet.Take(HeaderSize).SequenceEqual(Wire.Message(type).ToArray());
        }

        private static bool MatchesPinnedPayload(byte[] payload, string pinned)
        {
            return string.Equals(DescribePayload(payload), NormalizePinned(pinned), StringComparison.Ordinal);
        }

        private static string NormalizePinned(string pinned)
        {
            return pinned.StartsWith("sha256:", StringComparison.Ordinal)
                ? pinned
                : string.Join(" ", pinned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string DescribePayload(byte[] payload)
        {
            if (payload.Length <= MaximumInlineVectorSize)
            {
                return Hex(payload);
            }

            using (var sha = SHA256.Create())
            {
                return "sha256:" + payload.Length + ":"
                    + string.Concat(sha.ComputeHash(payload).Select(value => value.ToString("x2")));
            }
        }

        private static string Hex(byte[] bytes)
        {
            return bytes == null
                ? "<null>"
                : string.Join(" ", bytes.Select(value => value.ToString("x2")));
        }

        private static Decoded Decode(Codec codec, byte[] data, out string failure)
        {
            failure = null;
            try
            {
                return codec.Decode(data);
            }
            catch (Exception exception)
            {
                failure = $"decoding threw {exception.GetType().Name}: {exception.Message}";
                return Decoded.Rejected;
            }
        }

        private static byte[] TryEncode(Codec codec, object value, out string failure)
        {
            failure = null;
            try
            {
                return codec.Encode(value);
            }
            catch (Exception exception)
            {
                failure = $"<encoding threw {exception.GetType().Name}>";
                return null;
            }
        }

        private static void ExpectRejected(List<string> failures, Codec codec, string name, byte[] data)
        {
            var decoded = Decode(codec, data, out var failure);
            if (failure != null)
            {
                failures.Add($"{name}: {failure}");
            }
            else if (decoded.Ok)
            {
                failures.Add($"{name}: was accepted.");
            }
        }

        private static void ExpectTypeUnreadable(List<string> failures, string name, byte[] data)
        {
            if (RealtimeProtocol.TryReadMessageType(data, out _))
            {
                failures.Add($"{name}: the message type reader accepted the header.");
            }
        }

        private static void ExpectTypeReader(
            List<string> failures,
            string name,
            byte[] data,
            bool expectedResult,
            RealtimeMessageType expectedType)
        {
            var result = RealtimeProtocol.TryReadMessageType(data, out var type);
            if (result != expectedResult || (result && type != expectedType))
            {
                failures.Add($"Type reader with {name}: returned {result} and {type}.");
            }
        }

        private static void Expect<T>(List<string> failures, string name, T actual, T expected)
        {
            if (!EqualityComparer<T>.Default.Equals(actual, expected))
            {
                failures.Add($"{name}: is {actual}, pinned {expected}.");
            }
        }

        private static void ExpectEnum<TEnum>(List<string> failures, string pinned)
            where TEnum : struct, Enum
        {
            var actual = string.Join(
                " ",
                Enum.GetValues(typeof(TEnum))
                    .Cast<TEnum>()
                    .Select(value => value + "=" + Convert.ToInt64(value)));
            Expect(failures, typeof(TEnum).Name, actual, pinned);
        }

        private static Guid G(int seed)
        {
            var bytes = new byte[16];
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = (byte)((seed * 16) + index);
            }

            return new Guid(bytes);
        }

        private static string Repeat(string value, int count)
        {
            return string.Concat(Enumerable.Repeat(value, count));
        }

        private enum DecodeExpectation
        {
            Accepted,
            AcceptedAs,
            AcceptedUnencodable,
            Rejected
        }

        private readonly struct Decoded
        {
            public static readonly Decoded Rejected = new Decoded(false, null);

            public Decoded(bool ok, object value)
            {
                Ok = ok;
                Value = value;
            }

            public bool Ok { get; }

            public object Value { get; }
        }

        private sealed class Codec
        {
            public Codec(Func<byte[], Decoded> decode, Func<object, byte[]> encode)
            {
                Decode = decode;
                Encode = encode;
            }

            public Func<byte[], Decoded> Decode { get; }

            public Func<object, byte[]> Encode { get; }
        }

        private sealed class VectorCase
        {
            public VectorCase(string name, RealtimeMessageType type, Func<byte[]> encode)
            {
                Name = name;
                Type = type;
                Encode = encode;
            }

            public string Name { get; }

            public RealtimeMessageType Type { get; }

            public Func<byte[]> Encode { get; }
        }

        private sealed class DecodeCase
        {
            public DecodeCase(string name, DecodeExpectation expectation, Wire packet, Wire canonical)
            {
                Name = name;
                Expectation = expectation;
                Packet = packet;
                Canonical = canonical;
            }

            public string Name { get; }

            public RealtimeMessageType Type => Packet.Type;

            public DecodeExpectation Expectation { get; }

            public Wire Packet { get; }

            public Wire Canonical { get; }
        }

        private sealed class EncodeRejection
        {
            public EncodeRejection(string name, Type expectedException, Func<byte[]> encode)
            {
                Name = name;
                ExpectedException = expectedException;
                Encode = encode;
            }

            public string Name { get; }

            public Type ExpectedException { get; }

            public Func<byte[]> Encode { get; }
        }

        private sealed class Registry
        {
            // Names are unique per kind of case, so a decode case and an encode rejection may share one.
            private readonly HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);

            public List<VectorCase> Vectors { get; } = new List<VectorCase>();

            public List<DecodeCase> DecodeCases { get; } = new List<DecodeCase>();

            public List<EncodeRejection> EncodeRejections { get; } = new List<EncodeRejection>();

            // A valid message whose encoding is pinned in RealtimeProtocolGoldenVectors.cs.
            public void Vector(string name, RealtimeMessageType type, Func<byte[]> encode)
            {
                Vectors.Add(new VectorCase(Unique("vector " + name, name), type, encode));
            }

            // A canonical packet that decodes and encodes back to the same bytes.
            public void Accepts(string name, Wire packet)
            {
                DecodeCases.Add(new DecodeCase(Unique("decode " + name, name), DecodeExpectation.Accepted, packet, null));
            }

            // A non-canonical packet that decodes and encodes back to the canonical bytes.
            public void AcceptsAs(string name, Wire packet, Wire canonical)
            {
                DecodeCases.Add(new DecodeCase(Unique("decode " + name, name), DecodeExpectation.AcceptedAs, packet, canonical));
            }

            // A packet that decodes although its value cannot be encoded again.
            public void AcceptsUnencodable(string name, Wire packet)
            {
                DecodeCases.Add(new DecodeCase(Unique("decode " + name, name), DecodeExpectation.AcceptedUnencodable, packet, null));
            }

            public void Rejects(string name, Wire packet)
            {
                DecodeCases.Add(new DecodeCase(Unique("decode " + name, name), DecodeExpectation.Rejected, packet, null));
            }

            public void RejectsEncode(string name, Func<byte[]> encode)
            {
                EncodeRejections.Add(new EncodeRejection(Unique("encode " + name, name), typeof(ArgumentException), encode));
            }

            public void RejectsOversize(string name, Func<byte[]> encode)
            {
                EncodeRejections.Add(new EncodeRejection(Unique("encode " + name, name), typeof(InvalidOperationException), encode));
            }

            private string Unique(string key, string name)
            {
                if (!names.Add(key))
                {
                    throw new InvalidOperationException($"Golden case '{key}' is registered twice.");
                }

                return name;
            }
        }

        // UTF-8 text as the wire carries it: a declared byte length followed by bytes.
        private sealed class WireText
        {
            private WireText(byte[] bytes, int declaredLength)
            {
                Bytes = bytes;
                DeclaredLength = declaredLength;
            }

            public byte[] Bytes { get; }

            public int DeclaredLength { get; }

            public static implicit operator WireText(string value)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                return new WireText(bytes, bytes.Length);
            }

            public static WireText Raw(params byte[] bytes)
            {
                return new WireText(bytes, bytes.Length);
            }

            public static WireText Declared(int declaredLength, params byte[] bytes)
            {
                return new WireText(bytes, declaredLength);
            }
        }

        // Builds packets byte by byte without the protocol implementation, so decode cases
        // describe the layout independently of the code under test. Little-endian throughout.
        private sealed class Wire
        {
            private readonly List<byte> bytes = new List<byte>();

            private Wire(RealtimeMessageType type)
            {
                Type = type;
            }

            public RealtimeMessageType Type { get; }

            public static Wire Message(RealtimeMessageType type)
            {
                return Header((byte)type);
            }

            public static Wire Header(byte type)
            {
                return new Wire((RealtimeMessageType)type).U32(PinnedMagic).U16(PinnedVersion).U8(type);
            }

            public static Wire Fragment()
            {
                return new Wire(default);
            }

            public Wire U8(byte value)
            {
                bytes.Add(value);
                return this;
            }

            public Wire Bool(bool value)
            {
                return U8(value ? (byte)1 : (byte)0);
            }

            public Wire U16(ushort value)
            {
                return Little(value, 2);
            }

            public Wire U32(uint value)
            {
                return Little(value, 4);
            }

            public Wire U64(ulong value)
            {
                return Little(value, 8);
            }

            public Wire I32(int value)
            {
                return Little(unchecked((uint)value), 4);
            }

            public Wire I64(long value)
            {
                return Little(unchecked((ulong)value), 8);
            }

            public Wire F32(float value)
            {
                var raw = BitConverter.GetBytes(value);
                if (!BitConverter.IsLittleEndian)
                {
                    Array.Reverse(raw);
                }

                return Bytes(raw);
            }

            public Wire F32Bits(uint bits)
            {
                return U32(bits);
            }

            // Raw bits when given, which is how NaN payloads and infinities are written.
            public Wire F32Or(float value, uint? bits)
            {
                return bits.HasValue ? U32(bits.Value) : F32(value);
            }

            public Wire Guid(Guid value)
            {
                return Bytes(value.ToByteArray());
            }

            public Wire Text(WireText value)
            {
                return U16((ushort)value.DeclaredLength).Bytes(value.Bytes);
            }

            public Wire Bytes(byte[] value)
            {
                bytes.AddRange(value);
                return this;
            }

            public Wire Append(Wire fragment)
            {
                bytes.AddRange(fragment.bytes);
                return this;
            }

            public byte[] ToArray()
            {
                return bytes.ToArray();
            }

            private Wire Little(ulong value, int size)
            {
                for (var index = 0; index < size; index++)
                {
                    bytes.Add((byte)(value >> (8 * index)));
                }

                return this;
            }
        }
    }
}
