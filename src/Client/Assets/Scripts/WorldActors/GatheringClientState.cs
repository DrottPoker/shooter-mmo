using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.WorldActors
{
    public enum GatheringClientStatus
    {
        Idle,
        Gathering,
        Stopped
    }

    // Tracks the worker's GatheringState for the one node this player gathers from. The
    // worker owns cycles, yields, and stopping; this only mirrors them for presentation.
    public sealed class GatheringClientState
    {
        public event Action Changed;

        public GatheringClientStatus Status { get; private set; }

        public Guid InteractionSessionId { get; private set; }

        public ulong TargetEntityId { get; private set; }

        public int CycleMilliseconds { get; private set; }

        // Unscaled time in seconds when the current cycle began.
        public float CycleStartedAt { get; private set; }

        public int YieldedQuantity { get; private set; }

        public string YieldDefinitionId { get; private set; } = string.Empty;

        public string StopCode { get; private set; } = string.Empty;

        public string StopMessage { get; private set; } = string.Empty;

        // Applies one GatheringState. Started must belong to the open interaction; Yielded
        // and Stopped may arrive after the interaction closed, because a cycle that was in
        // flight still completes. A message for another gathering session is ignored.
        public bool TryApply(
            RealtimeGatheringState state,
            Guid activeInteractionSessionId,
            float now,
            out string error)
        {
            error = string.Empty;
            if (state == null)
            {
                error = "Gathering state is missing.";
                return false;
            }

            switch (state.Kind)
            {
                case RealtimeGatheringStateKind.Started:
                    if (state.InteractionSessionId != activeInteractionSessionId)
                    {
                        error = "Gathering started for an interaction that is not open.";
                        return false;
                    }

                    Status = GatheringClientStatus.Gathering;
                    InteractionSessionId = state.InteractionSessionId;
                    TargetEntityId = state.TargetEntityId;
                    CycleMilliseconds = state.CycleMilliseconds;
                    CycleStartedAt = now;
                    YieldedQuantity = 0;
                    YieldDefinitionId = string.Empty;
                    StopCode = string.Empty;
                    StopMessage = string.Empty;
                    break;
                case RealtimeGatheringStateKind.Yielded:
                    if (!IsCurrent(state))
                    {
                        error = "Gathering yield belongs to another gathering session.";
                        return false;
                    }

                    CycleMilliseconds = state.CycleMilliseconds;
                    CycleStartedAt = now;
                    YieldedQuantity += state.YieldQuantity;
                    YieldDefinitionId = state.YieldDefinitionId;
                    break;
                case RealtimeGatheringStateKind.Stopped:
                    if (!IsCurrent(state))
                    {
                        error = "Gathering stop belongs to another gathering session.";
                        return false;
                    }

                    Status = GatheringClientStatus.Stopped;
                    StopCode = state.Code;
                    StopMessage = state.Message;
                    break;
                default:
                    error = "Gathering state has an unknown kind.";
                    return false;
            }

            Changed?.Invoke();
            return true;
        }

        // The share of the current cycle that has passed, from 0 to 1.
        public float CycleProgress(float now)
        {
            if (Status != GatheringClientStatus.Gathering || CycleMilliseconds <= 0)
            {
                return 0f;
            }

            var progress = (now - CycleStartedAt) * 1000f / CycleMilliseconds;
            return progress < 0f ? 0f : progress > 1f ? 1f : progress;
        }

        public void Clear()
        {
            if (Status == GatheringClientStatus.Idle && InteractionSessionId == Guid.Empty)
            {
                return;
            }

            Status = GatheringClientStatus.Idle;
            InteractionSessionId = Guid.Empty;
            TargetEntityId = 0;
            CycleMilliseconds = 0;
            CycleStartedAt = 0f;
            YieldedQuantity = 0;
            YieldDefinitionId = string.Empty;
            StopCode = string.Empty;
            StopMessage = string.Empty;
            Changed?.Invoke();
        }

        private bool IsCurrent(RealtimeGatheringState state)
        {
            return Status == GatheringClientStatus.Gathering
                && state.InteractionSessionId == InteractionSessionId
                && state.TargetEntityId == TargetEntityId;
        }
    }
}
