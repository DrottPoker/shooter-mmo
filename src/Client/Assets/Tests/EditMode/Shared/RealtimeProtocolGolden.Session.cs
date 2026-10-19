#nullable disable
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
            const RealtimeMessageType leaveRejected = RealtimeMessageType.LeaveRejected;
            const RealtimeMessageType serverDisconnect = RealtimeMessageType.ServerDisconnect;

            r.Vector("Session/JoinRequest/ascii", joinRequest, () => RealtimeProtocol.EncodeJoinRequest("ticket.v1.abc-123"));
            r.Vector("Session/JoinRequest/multibyte", joinRequest, () => RealtimeProtocol.EncodeJoinRequest("biljett-åäö-✓-😀"));
            r.Vector("Session/JoinRequest/maximum-length", joinRequest, () => RealtimeProtocol.EncodeJoinRequest(Repeat("t", 1024)));
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
            r.Vector("Session/LeaveAccepted/empty", RealtimeMessageType.LeaveAccepted, RealtimeProtocol.EncodeLeaveAccepted);
            r.Vector(
                "Session/LeaveRejected/basic",
                leaveRejected,
                () => RealtimeProtocol.EncodeLeaveRejected("leave_not_joined", "No simulation session is active."));
            r.Vector(
                "Session/ServerDisconnect/multibyte",
                serverDisconnect,
                () => RealtimeProtocol.EncodeServerDisconnect("server_shutdown", "Servern stängs för underhåll ✓"));

            // Text fields share one reader: a declared UTF-8 byte length of at least one, at
            // most four bytes per allowed character, valid UTF-8, and at most the allowed number
            // of UTF-16 characters. The decoder accepts whitespace that the encoder refuses.
            r.Accepts("Session/JoinRequest/one character", JoinRequestWire("t"));
            r.Accepts("Session/JoinRequest/1024 characters", JoinRequestWire(Repeat("t", 1024)));
            r.Rejects("Session/JoinRequest/1025 characters", JoinRequestWire(Repeat("t", 1025)));
            r.Rejects("Session/JoinRequest/empty text", JoinRequestWire(""));
            r.AcceptsUnencodable("Session/JoinRequest/whitespace text", JoinRequestWire(" \t "));
            r.Rejects("Session/JoinRequest/invalid UTF-8", JoinRequestWire(WireText.Raw(0x74, 0xC3, 0x28)));
            r.Rejects("Session/JoinRequest/overlong UTF-8", JoinRequestWire(WireText.Raw(0xC0, 0x80)));
            r.Rejects("Session/JoinRequest/encoded surrogate", JoinRequestWire(WireText.Raw(0xED, 0xA0, 0x80)));
            r.Rejects("Session/JoinRequest/declared length beyond the packet", JoinRequestWire(WireText.Declared(10, 0x74, 0x74)));
            r.Accepts("Session/JoinRequest/leading byte order mark", JoinRequestWire(WireText.Raw(0xEF, 0xBB, 0xBF, 0x74)));
            r.Accepts("Session/LeaveRequest/128 characters", LeaveRequestWire(Repeat("s", 128)));
            r.Rejects("Session/LeaveRequest/129 characters", LeaveRequestWire(Repeat("s", 129)));
            r.Accepts("Session/LeaveRequest/128 three-byte characters", LeaveRequestWire(Repeat("✓", 128)));
            r.Accepts("Session/LeaveRequest/64 surrogate pairs count as 128 characters", LeaveRequestWire(Repeat("😀", 64)));
            r.Rejects("Session/LeaveRequest/65 surrogate pairs count as 130 characters", LeaveRequestWire(Repeat("😀", 65)));
            r.Rejects(
                "Session/LeaveRequest/declared length above four bytes per character",
                LeaveRequestWire(WireText.Raw(new byte[513])));
            r.AcceptsUnencodable("Session/LeaveRequest/whitespace text", LeaveRequestWire(" "));

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

            r.RejectsEncode("Session/JoinRequest/null ticket", () => RealtimeProtocol.EncodeJoinRequest(null));
            r.RejectsEncode("Session/JoinRequest/empty ticket", () => RealtimeProtocol.EncodeJoinRequest(""));
            r.RejectsEncode("Session/JoinRequest/whitespace ticket", () => RealtimeProtocol.EncodeJoinRequest(" "));
            r.RejectsEncode("Session/JoinRequest/1025 character ticket", () => RealtimeProtocol.EncodeJoinRequest(Repeat("t", 1025)));
            r.RejectsEncode("Session/LeaveRequest/whitespace session", () => RealtimeProtocol.EncodeLeaveRequest(" "));
            r.RejectsEncode("Session/LeaveRequest/129 character session", () => RealtimeProtocol.EncodeLeaveRequest(Repeat("s", 129)));
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

        private static Wire JoinRequestWire(WireText ticket)
        {
            return Wire.Message(RealtimeMessageType.JoinRequest).Text(ticket);
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
