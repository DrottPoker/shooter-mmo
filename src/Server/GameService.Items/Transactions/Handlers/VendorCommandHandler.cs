using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// NPC vendor purchases and sales. The vendor profile and item values come from the mirrored
// catalog, the price from the shared VendorPricing rule, and the currency moves through the
// Characters wallet in the same transaction as the items.
internal sealed class VendorCommandHandler(ICharacterWallet characterWallet)
    : IItemCommandHandler<PurchaseFromVendorCommand>,
    IItemCommandHandler<SellToVendorCommand>
{
    private readonly ICharacterWallet characterWallet = characterWallet;

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        PurchaseFromVendorCommand command,
        CancellationToken cancellationToken)
    {
        context.Authority.EnsureVendorAccess(command.VendorId);
        var states = await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        var vendor = await context.State.LoadVendorAsync(command.VendorId, cancellationToken);
        if (!VendorPricing.Offers(vendor, command.DefinitionId))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.VendorOfferUnavailable,
                "The vendor does not offer this item.");
        }

        var definition = await context.State.LoadDefinitionAsync(
            command.DefinitionId,
            cancellationToken);
        if (command.Quantity <= 0 || command.Quantity > definition.RuntimeDefinition.MaximumStackSize)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStackLimitExceeded,
                "A purchase quantity must be positive and fit one stack.");
        }

        if (!VendorPricing.TryGetPurchasePrice(
                definition.RuntimeDefinition,
                vendor,
                command.Quantity,
                out var price)
            || price <= 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.VendorOfferUnavailable,
                "The vendor cannot price this purchase.");
        }

        if (price != command.ExpectedPrice)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.VendorPriceChanged,
                "The vendor price changed before the purchase committed.");
        }

        var debit = await characterWallet.DebitAsync(
            context.Scope,
            command.CharacterId,
            price,
            new CurrencyChangeSource(CurrencySourceKinds.VendorPurchase, vendor.Id, context.OperationId),
            cancellationToken);
        if (!debit.Succeeded)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CurrencyInsufficient,
                "The character does not have enough currency for this purchase.");
        }

        await CarriedAcquisition.AcquireAsync(
            context,
            states[command.CharacterId],
            definition,
            command.Quantity,
            "vendor_purchase",
            cancellationToken);
        context.Journal.AddMetadataAudit(
            "vendor_purchase",
            new { command.CharacterId, Currency = debit.Before },
            new
            {
                command.CharacterId,
                Currency = debit.After,
                VendorId = vendor.Id,
                DefinitionId = definition.RuntimeDefinition.Id,
                command.Quantity,
                Price = price
            });
    }

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        SellToVendorCommand command,
        CancellationToken cancellationToken)
    {
        context.Authority.EnsureVendorAccess(command.VendorId);
        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            [command.ItemInstanceId],
            [],
            cancellationToken);
        var item = await context.State.LoadOwnedItemAsync(
            command.ItemInstanceId,
            command.CharacterId,
            command.ExpectedItemRevision,
            cancellationToken);
        if (!IsCarried(item))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.VendorWillNotBuy,
                "Only items in the inventory or the equipped Bag can be sold.");
        }

        var definition = await context.State.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
        var policies = await context.State.LoadPoliciesAsync(item.ItemInstanceId, cancellationToken);
        if (!ItemPolicyRules.Evaluate(
                definition.RuntimeDefinition,
                ItemPolicyStates.ToPolicyStates(policies)).CanSellToVendor)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "Protected and insured items cannot be sold to a vendor.");
        }

        if (await context.State.BagHasContentsAsync(item, cancellationToken))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagNotEmpty,
                "A non-empty Bag cannot be sold.");
        }

        var vendor = await context.State.LoadVendorAsync(command.VendorId, cancellationToken);
        if (!VendorPricing.AcceptsSale(vendor, definition.RuntimeDefinition)
            || string.Equals(
                definition.RuntimeDefinition.Category,
                ItemCategoryIds.QuestItem,
                StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.VendorWillNotBuy,
                "The vendor does not buy this item.");
        }

        if (command.Quantity <= 0 || command.Quantity > item.Quantity)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemQuantityChanged,
                "The sale quantity exceeds the current item quantity.");
        }

        if (!VendorPricing.TryGetSalePrice(
                definition.RuntimeDefinition,
                vendor,
                command.Quantity,
                out var price))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.VendorWillNotBuy,
                "The vendor cannot price this sale.");
        }

        // A sale that rounds down to nothing would destroy the item for no payment.
        if (price == 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.VendorWillNotBuy,
                "The vendor pays nothing for this quantity. Sell more at once.");
        }

        if (price != command.ExpectedPrice)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.VendorPriceChanged,
                "The vendor price changed before the sale committed.");
        }

        var beforeState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        if (command.Quantity == item.Quantity)
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_destructions (
                    item_instance_id,
                    definition_id,
                    source_operation_id,
                    quantity,
                    reason)
                values (
                    @ItemInstanceId,
                    @DefinitionId,
                    @OperationId,
                    @Quantity,
                    'vendor_sale');

                delete from item_instances
                where id = @ItemInstanceId;
                """,
                new
                {
                    item.ItemInstanceId,
                    item.DefinitionId,
                    context.OperationId,
                    item.Quantity
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.Journal.AddItemAudit(
                "item_sold_to_vendor",
                item.ItemInstanceId,
                beforeState,
                null);
        }
        else
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                update item_instances
                set quantity = quantity - @Quantity,
                    revision = revision + 1,
                    updated_at = now()
                where id = @ItemInstanceId;
                """,
                new { command.Quantity, item.ItemInstanceId },
                context.Transaction,
                cancellationToken: cancellationToken));
            var afterState = await context.Journal.CaptureItemStateJsonAsync(
                item.ItemInstanceId,
                cancellationToken);
            context.Journal.AddItemAudit(
                "item_quantity_sold_to_vendor",
                item.ItemInstanceId,
                beforeState,
                afterState);
            context.Journal.IncludeResultItem(item.ItemInstanceId);
        }

        context.Journal.TouchContainer(item.ContainerId!.Value);
        context.Journal.TouchCharacter(command.CharacterId);

        var currencyBefore = 0L;
        var currencyAfter = 0L;
        if (price > 0)
        {
            var credit = await characterWallet.CreditAsync(
                context.Scope,
                command.CharacterId,
                price,
                new CurrencyChangeSource(CurrencySourceKinds.VendorSale, vendor.Id, context.OperationId),
                cancellationToken);
            if (!credit.Succeeded)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.CurrencyLimitExceeded,
                    "The sale would exceed the maximum currency balance.");
            }

            currencyBefore = credit.Before;
            currencyAfter = credit.After;
        }

        context.Journal.AddMetadataAudit(
            "vendor_sale",
            new { command.CharacterId, Currency = currencyBefore },
            new
            {
                command.CharacterId,
                Currency = currencyAfter,
                VendorId = vendor.Id,
                item.DefinitionId,
                command.Quantity,
                Price = price
            });
    }

    // Equipped, banked, and Recovery Storage items must first move into carried storage.
    private static bool IsCarried(LockedItem item)
    {
        return item.ContainerId is not null
            && item.EquipmentSlotId is null
            && (string.Equals(
                    item.SourceContainerType,
                    ItemContainerTypeIds.PermanentInventory,
                    StringComparison.Ordinal)
                || (string.Equals(
                        item.SourceContainerType,
                        ItemContainerTypeIds.BagContents,
                        StringComparison.Ordinal)
                    && string.Equals(
                        item.SourceContainerLifecycle,
                        "active",
                        StringComparison.Ordinal)));
    }
}
