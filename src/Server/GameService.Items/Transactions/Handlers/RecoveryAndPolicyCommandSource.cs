using Mmorpg.GameService.Characters.Contracts;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// The Recovery and item policy commands: adding, claiming, and expiring Recovery deliveries,
// and applying policies and removing insurance.
internal sealed class RecoveryAndPolicyCommandSource : IItemCommandSource
{
    public RecoveryAndPolicyCommandSource(ICharacterWallet characterWallet)
    {
        var recoveryDelivery = new RecoveryDeliveryCommandHandler();
        var itemPolicy = new ItemPolicyCommandHandler(characterWallet);
        Registrations =
        [
            new ItemCommandRegistration<AddRecoveryDeliveryCommand>(
                ItemOperationKinds.AddRecoveryDelivery,
                static command => command.CharacterId,
                recoveryDelivery),
            new ItemCommandRegistration<ClaimRecoveryDeliveryCommand>(
                ItemOperationKinds.ClaimRecoveryDelivery,
                static command => command.CharacterId,
                recoveryDelivery)
            {
                // The claimed items are stored in a stable order, so a retry in another
                // order replays.
                Canonicalize = static claim => claim with
                {
                    Items = claim.Items
                        .OrderBy(item => item.ItemInstanceId)
                        .ThenBy(item => item.Revision)
                        .ToArray()
                }
            },
            new ItemCommandRegistration<ExpireRecoveryDeliveryCommand>(
                ItemOperationKinds.ExpireRecoveryDelivery,
                static _ => null,
                new RecoveryExpiryCommandHandler())
            {
                RetriedSystemOperation = true
            },
            new ItemCommandRegistration<ApplyItemPolicyCommand>(
                ItemOperationKinds.ApplyItemPolicy,
                static command => command.CharacterId,
                itemPolicy)
            {
                OperationallySignificant = true,
                PolicyAction = true
            },
            new ItemCommandRegistration<RemoveInsurancePolicyCommand>(
                ItemOperationKinds.RemoveInsurancePolicy,
                static command => command.CharacterId,
                itemPolicy)
            {
                OperationallySignificant = true,
                PolicyAction = true
            }
        ];
    }

    public IReadOnlyList<ItemCommandRegistration> Registrations { get; }
}
