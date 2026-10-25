#nullable disable
using System;
using System.Collections.Generic;
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // The public encode and decode entry points of every message type.
    public static partial class RealtimeProtocolGolden
    {
        private delegate bool TypedDecoder<T>(byte[] data, out T value, out string error);

        private static Dictionary<RealtimeMessageType, Codec> CreateCodecs()
        {
            return new Dictionary<RealtimeMessageType, Codec>
            {
                [RealtimeMessageType.JoinRequest] = Typed<string>(
                    RealtimeProtocol.TryDecodeJoinRequest,
                    RealtimeProtocol.EncodeJoinRequest),
                [RealtimeMessageType.JoinAccepted] = Typed<RealtimeJoinAccepted>(
                    RealtimeProtocol.TryDecodeJoinAccepted,
                    RealtimeProtocol.EncodeJoinAccepted),
                [RealtimeMessageType.JoinRejected] = Typed<RealtimeError>(
                    RealtimeProtocol.TryDecodeJoinRejected,
                    value => RealtimeProtocol.EncodeJoinRejected(value.Code, value.Message)),
                [RealtimeMessageType.LeaveRequest] = Typed<string>(
                    RealtimeProtocol.TryDecodeLeaveRequest,
                    RealtimeProtocol.EncodeLeaveRequest),
                [RealtimeMessageType.LeaveAccepted] = new Codec(
                    data => RealtimeProtocol.TryDecodeLeaveAccepted(data, out _)
                        ? new Decoded(true, null)
                        : Decoded.Rejected,
                    _ => RealtimeProtocol.EncodeLeaveAccepted()),
                [RealtimeMessageType.LeaveRejected] = Typed<RealtimeError>(
                    RealtimeProtocol.TryDecodeLeaveRejected,
                    value => RealtimeProtocol.EncodeLeaveRejected(value.Code, value.Message)),
                [RealtimeMessageType.ServerDisconnect] = Typed<RealtimeError>(
                    RealtimeProtocol.TryDecodeServerDisconnect,
                    value => RealtimeProtocol.EncodeServerDisconnect(value.Code, value.Message)),
                [RealtimeMessageType.MovementInputBatch] = Typed<RealtimeMovementInputBatch>(
                    RealtimeProtocol.TryDecodeMovementInputBatch,
                    RealtimeProtocol.EncodeMovementInputBatch),
                [RealtimeMessageType.SimulationSnapshot] = Typed<RealtimeSimulationSnapshot>(
                    RealtimeProtocol.TryDecodeSimulationSnapshot,
                    RealtimeProtocol.EncodeSimulationSnapshot),
                [RealtimeMessageType.EntitySpawn] = Typed<RealtimeEntitySpawn>(
                    RealtimeProtocol.TryDecodeEntitySpawn,
                    RealtimeProtocol.EncodeEntitySpawn),
                [RealtimeMessageType.EntityDespawn] = Typed<RealtimeEntityDespawn>(
                    RealtimeProtocol.TryDecodeEntityDespawn,
                    RealtimeProtocol.EncodeEntityDespawn),
                [RealtimeMessageType.CarryStateChanged] = Typed<RealtimeCarryState>(
                    RealtimeProtocol.TryDecodeCarryStateChanged,
                    RealtimeProtocol.EncodeCarryStateChanged),
                [RealtimeMessageType.ItemOperationIntent] = Typed<RealtimeItemOperationIntent>(
                    RealtimeProtocol.TryDecodeItemOperationIntent,
                    RealtimeProtocol.EncodeItemOperationIntent),
                [RealtimeMessageType.ItemOperationResult] = Typed<RealtimeItemOperationResult>(
                    RealtimeProtocol.TryDecodeItemOperationResult,
                    RealtimeProtocol.EncodeItemOperationResult),
                [RealtimeMessageType.CorpsePresenceSnapshotChunk] = Typed<RealtimeCorpsePresenceSnapshotChunk>(
                    RealtimeProtocol.TryDecodeCorpsePresenceSnapshotChunk,
                    RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk),
                [RealtimeMessageType.CorpseInteractionIntent] = Typed<RealtimeCorpseInteractionIntent>(
                    RealtimeProtocol.TryDecodeCorpseInteractionIntent,
                    RealtimeProtocol.EncodeCorpseInteractionIntent),
                [RealtimeMessageType.CorpseInteractionResult] = Typed<RealtimeCorpseInteractionResult>(
                    RealtimeProtocol.TryDecodeCorpseInteractionResult,
                    RealtimeProtocol.EncodeCorpseInteractionResult),
                [RealtimeMessageType.CorpseViewStateChunk] = Typed<RealtimeCorpseViewStateChunk>(
                    RealtimeProtocol.TryDecodeCorpseViewStateChunk,
                    RealtimeProtocol.EncodeCorpseViewStateChunk),
                [RealtimeMessageType.CorpseViewClosed] = Typed<RealtimeCorpseViewClosed>(
                    RealtimeProtocol.TryDecodeCorpseViewClosed,
                    RealtimeProtocol.EncodeCorpseViewClosed),
                [RealtimeMessageType.WorldActorSpawn] = Typed<RealtimeWorldActorSpawn>(
                    RealtimeProtocol.TryDecodeWorldActorSpawn,
                    RealtimeProtocol.EncodeWorldActorSpawn),
                [RealtimeMessageType.WorldActorState] = Typed<RealtimeWorldActorState>(
                    RealtimeProtocol.TryDecodeWorldActorState,
                    RealtimeProtocol.EncodeWorldActorState),
                [RealtimeMessageType.WorldActorDespawn] = Typed<RealtimeWorldActorDespawn>(
                    RealtimeProtocol.TryDecodeWorldActorDespawn,
                    RealtimeProtocol.EncodeWorldActorDespawn),
                [RealtimeMessageType.WorldInteractionIntent] = Typed<RealtimeWorldInteractionIntent>(
                    RealtimeProtocol.TryDecodeWorldInteractionIntent,
                    RealtimeProtocol.EncodeWorldInteractionIntent),
                [RealtimeMessageType.WorldInteractionOpened] = Typed<RealtimeWorldInteractionOpened>(
                    RealtimeProtocol.TryDecodeWorldInteractionOpened,
                    RealtimeProtocol.EncodeWorldInteractionOpened),
                [RealtimeMessageType.WorldInteractionResult] = Typed<RealtimeWorldInteractionResult>(
                    RealtimeProtocol.TryDecodeWorldInteractionResult,
                    RealtimeProtocol.EncodeWorldInteractionResult),
                [RealtimeMessageType.WorldInteractionClosed] = Typed<RealtimeWorldInteractionClosed>(
                    RealtimeProtocol.TryDecodeWorldInteractionClosed,
                    RealtimeProtocol.EncodeWorldInteractionClosed),
                [RealtimeMessageType.OwnerSimulationSnapshot] = Typed<RealtimeOwnerSimulationSnapshot>(
                    RealtimeProtocol.TryDecodeOwnerSimulationSnapshot,
                    RealtimeProtocol.EncodeOwnerSimulationSnapshot),
                [RealtimeMessageType.CombatIntent] = Typed<RealtimeCombatIntent>(
                    RealtimeProtocol.TryDecodeCombatIntent,
                    RealtimeProtocol.EncodeCombatIntent),
                [RealtimeMessageType.CombatIntentResult] = Typed<RealtimeCombatIntentResult>(
                    RealtimeProtocol.TryDecodeCombatIntentResult,
                    RealtimeProtocol.EncodeCombatIntentResult),
                [RealtimeMessageType.CombatantState] = Typed<RealtimeCombatantState>(
                    RealtimeProtocol.TryDecodeCombatantState,
                    RealtimeProtocol.EncodeCombatantState),
                [RealtimeMessageType.CombatEventBatch] = Typed<RealtimeCombatEventBatch>(
                    RealtimeProtocol.TryDecodeCombatEventBatch,
                    RealtimeProtocol.EncodeCombatEventBatch),
                [RealtimeMessageType.OwnerCombatState] = Typed<RealtimeOwnerCombatState>(
                    RealtimeProtocol.TryDecodeOwnerCombatState,
                    RealtimeProtocol.EncodeOwnerCombatState),
                [RealtimeMessageType.OwnerItemQuantityChanged] = Typed<RealtimeOwnerItemQuantityChanged>(
                    RealtimeProtocol.TryDecodeOwnerItemQuantityChanged,
                    RealtimeProtocol.EncodeOwnerItemQuantityChanged),
                [RealtimeMessageType.OwnerItemStateChanged] = Typed<RealtimeOwnerItemStateChanged>(
                    RealtimeProtocol.TryDecodeOwnerItemStateChanged,
                    RealtimeProtocol.EncodeOwnerItemStateChanged),
                [RealtimeMessageType.GatheringState] = Typed<RealtimeGatheringState>(
                    RealtimeProtocol.TryDecodeGatheringState,
                    RealtimeProtocol.EncodeGatheringState),
                [RealtimeMessageType.EntityAppearanceChanged] = Typed<RealtimeEntityAppearanceChanged>(
                    RealtimeProtocol.TryDecodeEntityAppearanceChanged,
                    RealtimeProtocol.EncodeEntityAppearanceChanged),
                [RealtimeMessageType.AppearanceIntent] = Typed<RealtimeAppearanceIntent>(
                    RealtimeProtocol.TryDecodeAppearanceIntent,
                    RealtimeProtocol.EncodeAppearanceIntent),
                [RealtimeMessageType.AppearanceIntentResult] = Typed<RealtimeAppearanceIntentResult>(
                    RealtimeProtocol.TryDecodeAppearanceIntentResult,
                    RealtimeProtocol.EncodeAppearanceIntentResult)
            };
        }

        private static Codec Typed<T>(TypedDecoder<T> decode, Func<T, byte[]> encode)
        {
            return new Codec(
                data => decode(data, out var value, out _) ? new Decoded(true, value) : Decoded.Rejected,
                value => encode((T)value));
        }
    }
}
