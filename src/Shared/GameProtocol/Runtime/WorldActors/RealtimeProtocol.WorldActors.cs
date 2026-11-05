using System;

namespace Mmorpg.GameProtocol
{
    // Cross-field rules of the WorldActors family. The schema in Schema~ describes the
    // messages, and the generator writes their models and codecs.
    public static partial class RealtimeProtocol
    {
        // A capability action carries the payload its action needs, and nothing else.
        private static bool SatisfiesRules(RealtimeWorldInteractionIntent intent)
        {
            if (intent.OperationKind != RealtimeWorldInteractionOperationKind.CapabilityAction)
            {
                return true;
            }

            switch (intent.CapabilityAction)
            {
                case RealtimeCapabilityActionKind.None:
                    return HasNoCharacterRevision(intent)
                        && HasNoItem(intent)
                        && HasNoDestination(intent)
                        && HasNoVendorTerms(intent)
                        && HasNoQuestTerms(intent);
                case RealtimeCapabilityActionKind.ApplyInsurance:
                case RealtimeCapabilityActionKind.RemoveInsurance:
                    return intent.CapabilityKind == RealtimeWorldActorCapabilityKind.Insurance
                        && intent.ExpectedCharacterRevision >= 0
                        && HasItem(intent)
                        && HasNoDestination(intent)
                        && HasNoVendorTerms(intent)
                        && HasNoQuestTerms(intent);
                case RealtimeCapabilityActionKind.AcceptQuest:
                case RealtimeCapabilityActionKind.RegrantQuestItems:
                    return IsQuestAction(intent)
                        && intent.QuestObjectiveId.Length == 0
                        && intent.RewardChoiceDefinitionId.Length == 0;
                case RealtimeCapabilityActionKind.TalkToQuestNpc:
                case RealtimeCapabilityActionKind.DeliverQuestItems:
                    return IsQuestAction(intent)
                        && !string.IsNullOrWhiteSpace(intent.QuestObjectiveId)
                        && intent.RewardChoiceDefinitionId.Length == 0;
                case RealtimeCapabilityActionKind.TurnInQuest:
                    return IsQuestAction(intent)
                        && intent.QuestObjectiveId.Length == 0
                        && (intent.RewardChoiceDefinitionId.Length == 0
                            || !string.IsNullOrWhiteSpace(intent.RewardChoiceDefinitionId));
                case RealtimeCapabilityActionKind.VendorPurchase:
                    return intent.CapabilityKind == RealtimeWorldActorCapabilityKind.Vendor
                        && intent.ExpectedCharacterRevision >= 0
                        && HasNoItem(intent)
                        && HasNoDestination(intent)
                        && !string.IsNullOrWhiteSpace(intent.OfferDefinitionId)
                        && intent.Quantity >= 1
                        && intent.ExpectedPrice >= 0
                        && HasNoQuestTerms(intent);
                case RealtimeCapabilityActionKind.VendorSale:
                    return intent.CapabilityKind == RealtimeWorldActorCapabilityKind.Vendor
                        && intent.ExpectedCharacterRevision >= 0
                        && HasItem(intent)
                        && HasNoDestination(intent)
                        && intent.OfferDefinitionId.Length == 0
                        && intent.Quantity >= 1
                        && intent.ExpectedPrice >= 0
                        && HasNoQuestTerms(intent);
                case RealtimeCapabilityActionKind.StartGathering:
                    return intent.CapabilityKind == RealtimeWorldActorCapabilityKind.Gathering
                        && HasNoCharacterRevision(intent)
                        && HasNoItem(intent)
                        && HasNoDestination(intent)
                        && HasNoVendorTerms(intent)
                        && HasNoQuestTerms(intent);
                default:
                    return false;
            }
        }

        // A quest action at a quest NPC names its quest. GameService places and takes the items,
        // so the action carries no item, destination, or expected item-state revision.
        private static bool IsQuestAction(RealtimeWorldInteractionIntent intent)
        {
            return intent.CapabilityKind == RealtimeWorldActorCapabilityKind.Quests
                && HasNoCharacterRevision(intent)
                && HasNoItem(intent)
                && HasNoDestination(intent)
                && HasNoVendorTerms(intent)
                && !string.IsNullOrWhiteSpace(intent.QuestId);
        }

        private static bool HasNoQuestTerms(RealtimeWorldInteractionIntent intent)
        {
            return intent.QuestId.Length == 0
                && intent.QuestObjectiveId.Length == 0
                && intent.RewardChoiceDefinitionId.Length == 0;
        }

        private static bool HasNoCharacterRevision(RealtimeWorldInteractionIntent intent)
        {
            return intent.ExpectedCharacterRevision == 0;
        }

        private static bool HasItem(RealtimeWorldInteractionIntent intent)
        {
            return intent.ItemInstanceId != Guid.Empty && intent.ExpectedItemRevision >= 0;
        }

        private static bool HasNoItem(RealtimeWorldInteractionIntent intent)
        {
            return intent.ItemInstanceId == Guid.Empty && intent.ExpectedItemRevision == 0;
        }

        private static bool HasNoDestination(RealtimeWorldInteractionIntent intent)
        {
            return intent.DestinationContainerId == Guid.Empty && intent.DestinationSlotIndex == -1;
        }

        private static bool HasNoVendorTerms(RealtimeWorldInteractionIntent intent)
        {
            return intent.OfferDefinitionId.Length == 0
                && intent.Quantity == 0
                && intent.ExpectedPrice == 0;
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
