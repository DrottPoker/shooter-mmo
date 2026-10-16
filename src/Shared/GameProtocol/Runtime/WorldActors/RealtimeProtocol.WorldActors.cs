using System;

namespace Mmorpg.GameProtocol
{
    // Cross-field rules of the WorldActors family. The schema in Schema~ describes the
    // messages, and the generator writes their models and codecs.
    public static partial class RealtimeProtocol
    {
        // A capability action carries the payload its lifecycle action needs, and nothing else.
        private static bool SatisfiesRules(RealtimeWorldInteractionIntent intent)
        {
            if (intent.OperationKind != RealtimeWorldInteractionOperationKind.CapabilityAction)
            {
                return true;
            }

            switch (intent.LifecycleAction)
            {
                case RealtimeNpcLifecycleActionKind.None:
                    return HasEmptyLifecyclePayload(intent);
                case RealtimeNpcLifecycleActionKind.ApplyInsurance:
                case RealtimeNpcLifecycleActionKind.RemoveInsurance:
                    return intent.CapabilityKind == RealtimeWorldActorCapabilityKind.Insurance
                        && intent.ExpectedCharacterRevision >= 0
                        && intent.ItemInstanceId != Guid.Empty
                        && intent.ExpectedItemRevision >= 0
                        && intent.DestinationContainerId == Guid.Empty
                        && intent.DestinationSlotIndex == -1;
                case RealtimeNpcLifecycleActionKind.AcceptQuest:
                    return intent.CapabilityKind == RealtimeWorldActorCapabilityKind.QuestOffer
                        && intent.ExpectedCharacterRevision >= 0
                        && intent.ItemInstanceId == Guid.Empty
                        && intent.ExpectedItemRevision == 0
                        && intent.DestinationContainerId != Guid.Empty
                        && intent.DestinationSlotIndex >= -1;
                case RealtimeNpcLifecycleActionKind.AbandonQuest:
                    return intent.CapabilityKind == RealtimeWorldActorCapabilityKind.QuestOffer
                        && intent.ExpectedCharacterRevision >= 0
                        && intent.ItemInstanceId == Guid.Empty
                        && intent.ExpectedItemRevision == 0
                        && intent.DestinationContainerId == Guid.Empty
                        && intent.DestinationSlotIndex == -1;
                default:
                    return false;
            }
        }

        private static bool HasEmptyLifecyclePayload(RealtimeWorldInteractionIntent intent)
        {
            return intent.ExpectedCharacterRevision == 0
                && intent.ItemInstanceId == Guid.Empty
                && intent.ExpectedItemRevision == 0
                && intent.DestinationContainerId == Guid.Empty
                && intent.DestinationSlotIndex == -1;
        }

        // A failed Open has no interaction yet; every other result names its interaction.
        // A success reports the committed target revision.
        private static bool SatisfiesRules(RealtimeWorldInteractionResult result)
        {
            var hasExpectedSession = result.OperationKind == RealtimeWorldInteractionOperationKind.Open
                ? result.InteractionSessionId == Guid.Empty && !result.Succeeded
                : result.InteractionSessionId != Guid.Empty;
            return hasExpectedSession && (!result.Succeeded || result.TargetRevision > 0);
        }
    }
}
