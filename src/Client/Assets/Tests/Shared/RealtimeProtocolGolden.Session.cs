#nullable disable
using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // Join, leave, and disconnect messages.
    public static partial class RealtimeProtocolGolden
    {
        private static void RegisterSession(Registry r)
        {
            const RealtimeMessageType joinRequest = RealtimeMessageType.JoinRequest;
            const RealtimeMessageType joinAccepted = RealtimeMessageType.JoinAccepted;
            const RealtimeMessageType joinRejected = RealtimeMessageType.JoinRejected;
            const RealtimeMessageType leaveRequest = RealtimeMessageType.LeaveRequest;
            const RealtimeMessageType leaveAccepted = RealtimeMessageType.LeaveAccepted;
            const RealtimeMessageType leaveRejected = RealtimeMessageType.LeaveRejected;
            const RealtimeMessageType serverDisconnect = RealtimeMessageType.ServerDisconnect;
            const RealtimeMessageType availabilityChanged = RealtimeMessageType.GameServiceAvailabilityChanged;

            r.Vector("Session/JoinRequest/typical", joinRequest, () => RealtimeProtocol.EncodeJoinRequest(JoinRequestModel()));
            r.Vector(
                "Session/JoinRequest/empty ticket id",
                joinRequest,
                () => RealtimeProtocol.EncodeJoinRequest(new RealtimeJoinRequest(Guid.Empty, Sequence(16, 0x40), Sequence(32, 0x80))));
            r.Vector("Session/JoinAccepted/typical", joinAccepted, () => RealtimeProtocol.EncodeJoinAccepted(JoinAcceptedModel()));
            r.Vector("Session/JoinAccepted/boundary", joinAccepted, () => RealtimeProtocol.EncodeJoinAccepted(JoinAcceptedBoundaryModel()));
            r.Vector(
                "Session/JoinRejected/basic",
                joinRejected,
                () => RealtimeProtocol.EncodeJoinRejected("join_ticket_expired", "The join ticket expired."));
            r.Vector(
                "Session/JoinRejected/exact-packet-limit",
                joinRejected,
                () => RealtimeProtocol.EncodeJoinRejected(Repeat("c", 64), Repeat("✓", 375)));
            r.Vector(
                "Session/LeaveRequest/basic",
                leaveRequest,
                () => RealtimeProtocol.EncodeLeaveRequest("6f0d1c9a-0000-4000-8000-000000000001"));
            r.Vector("Session/LeaveAccepted/lingering body", leaveAccepted, () => RealtimeProtocol.EncodeLeaveAccepted(20));
            r.Vector("Session/LeaveAccepted/left at once", leaveAccepted, () => RealtimeProtocol.EncodeLeaveAccepted(0));
            r.Vector("Session/LeaveAccepted/longest stay", leaveAccepted, () => RealtimeProtocol.EncodeLeaveAccepted(3600));
            r.Vector(
                "Session/LeaveRejected/basic",
                leaveRejected,
                () => RealtimeProtocol.EncodeLeaveRejected("leave_not_joined", "No simulation session is active."));
            r.Vector(
                "Session/ServerDisconnect/multibyte",
                serverDisconnect,
                () => RealtimeProtocol.EncodeServerDisconnect("server_shutdown", "Servern stängs för underhåll ✓"));
            r.Vector(
                "Session/GameServiceAvailabilityChanged/unavailable",
                availabilityChanged,
                () => RealtimeProtocol.EncodeGameServiceAvailabilityChanged(false));
            r.Vector(
                "Session/GameServiceAvailabilityChanged/available",
                availabilityChanged,
                () => RealtimeProtocol.EncodeGameServiceAvailabilityChanged(true));

            // The join request carries exactly 16 nonce and 32 proof bytes, each after its count.
            r.Accepts("Session/JoinRequest/16 nonce and 32 proof bytes", JoinRequestWire(16, 32));
            r.Accepts("Session/JoinRequest/empty ticket id", JoinRequestWire(16, 32, Guid.Empty));
            r.Rejects("Session/JoinRequest/15 nonce bytes", JoinRequestWire(15, 32));
            r.Rejects("Session/JoinRequest/17 nonce bytes", JoinRequestWire(17, 32));
            r.Rejects("Session/JoinRequest/31 proof bytes", JoinRequestWire(16, 31));
            r.Rejects("Session/JoinRequest/33 proof bytes", JoinRequestWire(16, 33));
            r.Rejects(
                "Session/JoinRequest/proof count beyond the packet",
                Wire.Message(joinRequest).Guid(G(1)).U8(16).Bytes(Sequence(16, 0x40)).U8(32).Bytes(Sequence(31, 0x80)));

            // Text fields share one reader: a declared UTF-8 byte length of at least one, at
            // most four bytes per allowed character, valid UTF-8, and at most the allowed number
            // of UTF-16 characters. The decoder accepts whitespace that the encoder refuses.
            r.Accepts("Session/LeaveRequest/one character", LeaveRequestWire("t"));
            r.Rejects("Session/LeaveRequest/empty text", LeaveRequestWire(""));
            r.AcceptsUnencodable("Session/LeaveRequest/tabs and spaces", LeaveRequestWire(" \t "));
            r.Rejects("Session/LeaveRequest/invalid UTF-8", LeaveRequestWire(WireText.Raw(0x74, 0xC3, 0x28)));
            r.Rejects("Session/LeaveRequest/overlong UTF-8", LeaveRequestWire(WireText.Raw(0xC0, 0x80)));
            r.Rejects("Session/LeaveRequest/encoded surrogate", LeaveRequestWire(WireText.Raw(0xED, 0xA0, 0x80)));
            r.Rejects("Session/LeaveRequest/declared length beyond the packet", LeaveRequestWire(WireText.Declared(10, 0x74, 0x74)));
            r.Accepts("Session/LeaveRequest/leading byte order mark", LeaveRequestWire(WireText.Raw(0xEF, 0xBB, 0xBF, 0x74)));
            r.Accepts("Session/LeaveRequest/128 characters", LeaveRequestWire(Repeat("s", 128)));
            r.Rejects("Session/LeaveRequest/129 characters", LeaveRequestWire(Repeat("s", 129)));
            r.Accepts("Session/LeaveRequest/128 three-byte characters", LeaveRequestWire(Repeat("✓", 128)));
            r.Accepts("Session/LeaveRequest/64 surrogate pairs count as 128 characters", LeaveRequestWire(Repeat("😀", 64)));
            r.Rejects("Session/LeaveRequest/65 surrogate pairs count as 130 characters", LeaveRequestWire(Repeat("😀", 65)));
            r.Rejects(
                "Session/LeaveRequest/declared length above four bytes per character",
                LeaveRequestWire(WireText.Raw(new byte[513])));
            r.AcceptsUnencodable("Session/LeaveRequest/whitespace text", LeaveRequestWire(" "));
            r.Accepts("Session/LeaveAccepted/3600 seconds", Wire.Message(leaveAccepted).U16(3600));
            r.Rejects("Session/LeaveAccepted/3601 seconds", Wire.Message(leaveAccepted).U16(3601));
            r.Rejects("Session/LeaveAccepted/without seconds", Wire.Message(leaveAccepted));
            r.AcceptsAs(
                "Session/GameServiceAvailabilityChanged/byte 2 reads as available",
                Wire.Message(availabilityChanged).U8(2),
                Wire.Message(availabilityChanged).Bool(true));
            r.Rejects("Session/GameServiceAvailabilityChanged/without the state", Wire.Message(availabilityChanged));
            r.Rejects(
                "Session/GameServiceAvailabilityChanged/trailing byte",
                Wire.Message(availabilityChanged).Bool(false).U8(0));

            foreach (var type in new[] { joinRejected, leaveRejected, serverDisconnect })
            {
                var prefix = "Session/" + type + "/";
                r.Accepts(prefix + "64 character code", ErrorWire(type, Repeat("c", 64), "message"));
                r.Rejects(prefix + "65 character code", ErrorWire(type, Repeat("c", 65), "message"));
                r.Accepts(prefix + "512 character message", ErrorWire(type, "code", Repeat("m", 512)));
                r.Rejects(prefix + "513 character message", ErrorWire(type, "code", Repeat("m", 513)));
                r.Rejects(prefix + "empty code", ErrorWire(type, "", "message"));
                r.Rejects(prefix + "empty message", ErrorWire(type, "code", ""));
                r.AcceptsUnencodable(prefix + "whitespace code", ErrorWire(type, " ", "message"));
                r.AcceptsUnencodable(prefix + "whitespace message", ErrorWire(type, "code", " "));
            }

            r.Accepts("Session/JoinAccepted/baseline", JoinAcceptedWire());
            r.Rejects("Session/JoinAccepted/controlled entity zero", JoinAcceptedWire(controlledEntityId: 0));
            r.Accepts("Session/JoinAccepted/controlled entity maximum", JoinAcceptedWire(controlledEntityId: ulong.MaxValue));
            r.AcceptsAs(
                "Session/JoinAccepted/reconnect byte 2 reads as true",
                JoinAcceptedWire(isReconnect: 2),
                JoinAcceptedWire(isReconnect: 1));
            r.Accepts("Session/JoinAccepted/not a reconnect", JoinAcceptedWire(isReconnect: 0));
            r.Accepts("Session/JoinAccepted/without player versus player", JoinAcceptedWire(playerVersusPlayer: 0));
            r.AcceptsAs(
                "Session/JoinAccepted/player versus player byte 3 reads as true",
                JoinAcceptedWire(playerVersusPlayer: 3),
                JoinAcceptedWire());
            r.Rejects(
                "Session/JoinAccepted/without shard rules",
                JoinAcceptedWire(includeShardRules: false));
            r.Rejects("Session/JoinAccepted/empty session id", JoinAcceptedWire(simulationSessionId: ""));
            r.Accepts("Session/JoinAccepted/128 character session id", JoinAcceptedWire(simulationSessionId: Repeat("s", 128)));
            r.Rejects("Session/JoinAccepted/129 character session id", JoinAcceptedWire(simulationSessionId: Repeat("s", 129)));
            r.AcceptsUnencodable("Session/JoinAccepted/whitespace session id", JoinAcceptedWire(simulationSessionId: " "));
            r.Accepts("Session/JoinAccepted/128 character name", JoinAcceptedWire(characterName: Repeat("å", 128)));
            r.Rejects("Session/JoinAccepted/129 character name", JoinAcceptedWire(characterName: Repeat("å", 129)));
            r.AcceptsUnencodable("Session/JoinAccepted/whitespace name", JoinAcceptedWire(characterName: "  "));
            r.Accepts("Session/JoinAccepted/64 character timestamp", JoinAcceptedWire(joinedAt: Repeat("9", 64)));
            r.Rejects("Session/JoinAccepted/65 character timestamp", JoinAcceptedWire(joinedAt: Repeat("9", 65)));
            r.Rejects("Session/JoinAccepted/invalid carry state", JoinAcceptedWire(carryState: CarryStateWire(carryCapacity: 0)));
            r.Rejects(
                "Session/JoinAccepted/invalid player state",
                JoinAcceptedWire(playerState: PlayerStateWire(positionY: float.NaN)));
            RegisterMovementSettingsCases(r);

            r.RejectsEncode("Session/JoinRequest/null request", () => RealtimeProtocol.EncodeJoinRequest(null));
            r.RejectsEncode(
                "Session/JoinRequest/null nonce",
                () => RealtimeProtocol.EncodeJoinRequest(new RealtimeJoinRequest(G(1), null, Sequence(32, 0x80))));
            r.RejectsEncode(
                "Session/JoinRequest/15 nonce bytes",
                () => RealtimeProtocol.EncodeJoinRequest(new RealtimeJoinRequest(G(1), Sequence(15, 0x40), Sequence(32, 0x80))));
            r.RejectsEncode(
                "Session/JoinRequest/null proof",
                () => RealtimeProtocol.EncodeJoinRequest(new RealtimeJoinRequest(G(1), Sequence(16, 0x40), null)));
            r.RejectsEncode(
                "Session/JoinRequest/33 proof bytes",
                () => RealtimeProtocol.EncodeJoinRequest(new RealtimeJoinRequest(G(1), Sequence(16, 0x40), Sequence(33, 0x80))));
            r.RejectsEncode("Session/LeaveRequest/whitespace session", () => RealtimeProtocol.EncodeLeaveRequest(" "));
            r.RejectsEncode("Session/LeaveRequest/129 character session", () => RealtimeProtocol.EncodeLeaveRequest(Repeat("s", 129)));
            r.RejectsEncode("Session/LeaveAccepted/3601 seconds", () => RealtimeProtocol.EncodeLeaveAccepted(3601));
            r.RejectsEncode("Session/JoinRejected/null code", () => RealtimeProtocol.EncodeJoinRejected(null, "message"));
            r.RejectsEncode("Session/JoinRejected/whitespace code", () => RealtimeProtocol.EncodeJoinRejected(" ", "message"));
            r.RejectsEncode("Session/JoinRejected/65 character code", () => RealtimeProtocol.EncodeJoinRejected(Repeat("c", 65), "message"));
            r.RejectsEncode("Session/LeaveRejected/empty message", () => RealtimeProtocol.EncodeLeaveRejected("code", ""));
            r.RejectsEncode(
                "Session/ServerDisconnect/513 character message",
                () => RealtimeProtocol.EncodeServerDisconnect("code", Repeat("m", 513)));
            r.RejectsOversize(
                "Session/JoinRejected/one byte above the packet limit",
                () => RealtimeProtocol.EncodeJoinRejected(Repeat("c", 64), Repeat("✓", 375) + "m"));
            r.RejectsEncode(
                "Session/JoinAccepted/controlled entity zero",
                () => RealtimeProtocol.EncodeJoinAccepted(JoinAcceptedModel(controlledEntityId: 0)));
            r.RejectsEncode(
                "Session/JoinAccepted/whitespace name",
                () => RealtimeProtocol.EncodeJoinAccepted(JoinAcceptedModel(characterName: " ")));
            r.RejectsEncode(
                "Session/JoinAccepted/null carry state",
                () => RealtimeProtocol.EncodeJoinAccepted(JoinAcceptedModel(carryState: Null<RealtimeCarryState>())));
            r.RejectsEncode(
                "Session/JoinAccepted/invalid movement settings",
                () => RealtimeProtocol.EncodeJoinAccepted(JoinAcceptedModel(movementSettings: MovementSettingsModel(tickRateHz: 0))));
            r.RejectsEncode(
                "Session/JoinAccepted/null movement settings",
                () => RealtimeProtocol.EncodeJoinAccepted(JoinAcceptedModel(movementSettings: Null<RealtimeMovementSettings>())));
            r.RejectsEncode(
                "Session/JoinAccepted/null player state",
                () => RealtimeProtocol.EncodeJoinAccepted(JoinAcceptedModel(initialPlayerState: Null<RealtimePlayerState>())));
            r.RejectsOversize(
                "Session/JoinAccepted/long identifiers above the packet limit",
                () => RealtimeProtocol.EncodeJoinAccepted(JoinAcceptedModel(
                    simulationSessionId: Repeat("a", 128),
                    characterName: Repeat("b", 128),
                    shardId: Repeat("c", 128),
                    worldId: Repeat("d", 128),
                    simulationRevision: Repeat("e", 128),
                    collisionRevision: Repeat("f", 128),
                    joinedAt: Repeat("g", 64),
                    accountId: Repeat("h", 128),
                    characterId: Repeat("i", 128),
                    sessionExpiresAt: Repeat("j", 64))));
        }

        private static RealtimeJoinAccepted JoinAcceptedModel(
            string simulationSessionId = "session-0001",
            string accountId = "account-0002",
            string characterId = "character-0003",
            string characterName = "Grön Ödla",
            string shardId = "local-shard-1",
            string worldId = "development-world-1",
            ulong controlledEntityId = 0x0102030405060708,
            string simulationRevision = "movement-simulation-v4",
            string collisionRevision = "collision-revision-123",
            string joinedAt = "2026-10-15T12:00:00.0000000Z",
            string sessionExpiresAt = "2026-10-15T12:00:30.0000000Z",
            bool isReconnect = true,
            Optional<RealtimeCarryState> carryState = default,
            Optional<RealtimeMovementSettings> movementSettings = default,
            Optional<RealtimePlayerState> initialPlayerState = default,
            bool playerVersusPlayer = true)
        {
            return new RealtimeJoinAccepted(
                simulationSessionId: simulationSessionId,
                accountId: accountId,
                characterId: characterId,
                characterName: characterName,
                shardId: shardId,
                worldId: worldId,
                controlledEntityId: controlledEntityId,
                simulationRevision: simulationRevision,
                collisionRevision: collisionRevision,
                joinedAt: joinedAt,
                sessionExpiresAt: sessionExpiresAt,
                isReconnect: isReconnect,
                carryState: carryState.Or(CarryStateModel()),
                movementSettings: movementSettings.Or(MovementSettingsModel()),
                initialPlayerState: initialPlayerState.Or(PlayerStateModel()),
                shardRules: new RealtimeShardRules(playerVersusPlayer));
        }

        private static RealtimeJoinAccepted JoinAcceptedBoundaryModel()
        {
            return JoinAcceptedModel(
                simulationSessionId: Repeat("s", 128),
                accountId: "a",
                characterId: "c",
                characterName: Repeat("å", 128),
                shardId: "s",
                worldId: "w",
                controlledEntityId: ulong.MaxValue,
                simulationRevision: "r",
                collisionRevision: "k",
                joinedAt: Repeat("9", 64),
                sessionExpiresAt: "e",
                isReconnect: false,
                carryState: new RealtimeCarryState(itemStateRevision: 0, carriedWeight: 0, carryCapacity: 1),
                movementSettings: MovementSettingsBoundaryModel(),
                initialPlayerState: PlayerStateBoundaryModel(),
                playerVersusPlayer: false);
        }

        private static RealtimeJoinRequest JoinRequestModel()
        {
            return new RealtimeJoinRequest(G(1), Sequence(16, 0x40), Sequence(32, 0x80));
        }

        private static Wire JoinRequestWire(int nonceBytes, int proofBytes, Guid? ticketId = null)
        {
            return Wire.Message(RealtimeMessageType.JoinRequest)
                .Guid(ticketId ?? G(1))
                .U8((byte)nonceBytes)
                .Bytes(Sequence(nonceBytes, 0x40))
                .U8((byte)proofBytes)
                .Bytes(Sequence(proofBytes, 0x80));
        }

        // count bytes that count up from first.
        private static byte[] Sequence(int count, byte first)
        {
            var bytes = new byte[count];
            for (var index = 0; index < count; index++)
            {
                bytes[index] = (byte)(first + index);
            }

            return bytes;
        }

        private static Wire LeaveRequestWire(WireText simulationSessionId)
        {
            return Wire.Message(RealtimeMessageType.LeaveRequest).Text(simulationSessionId);
        }

        private static Wire ErrorWire(RealtimeMessageType type, WireText code, WireText message)
        {
            return Wire.Message(type).Text(code).Text(message);
        }

        private static Wire JoinAcceptedWire(
            WireText simulationSessionId = null,
            WireText characterName = null,
            ulong controlledEntityId = 0x0102030405060708,
            WireText joinedAt = null,
            byte isReconnect = 1,
            Wire carryState = null,
            Wire movementSettings = null,
            Wire playerState = null,
            byte playerVersusPlayer = 1,
            bool includeShardRules = true)
        {
            var wire = Wire.Message(RealtimeMessageType.JoinAccepted)
                .Text(simulationSessionId ?? "session-0001")
                .Text("account-0002")
                .Text("character-0003")
                .Text(characterName ?? "Grön Ödla")
                .Text("local-shard-1")
                .Text("development-world-1")
                .U64(controlledEntityId)
                .Text("movement-simulation-v4")
                .Text("collision-revision-123")
                .Text(joinedAt ?? "2026-10-15T12:00:00.0000000Z")
                .Text("2026-10-15T12:00:30.0000000Z")
                .U8(isReconnect)
                .Append(carryState ?? CarryStateWire())
                .Append(movementSettings ?? MovementSettingsWire())
                .Append(playerState ?? PlayerStateWire());
            return includeShardRules ? wire.U8(playerVersusPlayer) : wire;
        }

        private static Optional<T> Null<T>()
            where T : class
        {
            return new Optional<T>(null);
        }

        // An optional model argument that can also carry an explicit null.
        private readonly struct Optional<T>
            where T : class
        {
            private readonly bool hasValue;
            private readonly T value;

            public Optional(T value)
            {
                hasValue = true;
                this.value = value;
            }

            public static implicit operator Optional<T>(T value)
            {
                return new Optional<T>(value);
            }

            public T Or(T fallback)
            {
                return hasValue ? value : fallback;
            }
        }
    }
}
