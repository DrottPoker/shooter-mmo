#nullable disable
using System;
using System.Linq;
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // Carry state, item operation intents, and item operation results.
    public static partial class RealtimeProtocolGolden
    {
        private static void RegisterItems(Registry r)
        {
            const RealtimeMessageType carry = RealtimeMessageType.CarryStateChanged;
            const RealtimeMessageType intent = RealtimeMessageType.ItemOperationIntent;
            const RealtimeMessageType result = RealtimeMessageType.ItemOperationResult;

            r.Vector("Items/CarryStateChanged/overflow", carry, () => RealtimeProtocol.EncodeCarryStateChanged(CarryStateModel()));
            RegisterOwnerItemQuantityChanged(r);
            r.Vector(
                "Items/CarryStateChanged/empty",
                carry,
                () => RealtimeProtocol.EncodeCarryStateChanged(new RealtimeCarryState(0, 0, 1)));
            r.Vector(
                "Items/CarryStateChanged/maximum",
                carry,
                () => RealtimeProtocol.EncodeCarryStateChanged(new RealtimeCarryState(long.MaxValue, long.MaxValue, long.MaxValue)));

            r.Vector(
                "Items/ItemOperationIntent/relocate to any slot",
                intent,
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateRelocate(G(1), 5, G(2), 6, G(3))));
            r.Vector(
                "Items/ItemOperationIntent/relocate to slot",
                intent,
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateRelocate(G(1), 5, G(2), 6, G(3), 7)));
            r.Vector(
                "Items/ItemOperationIntent/equip",
                intent,
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateEquip(G(1), 5, G(2), 6, "main_hand")));
            r.Vector(
                "Items/ItemOperationIntent/unequip",
                intent,
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateUnequip(G(1), 5, G(2), 6, G(3), int.MaxValue)));
            r.Vector(
                "Items/ItemOperationIntent/split stack",
                intent,
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateSplitStack(G(1), 5, G(2), 6, 3, G(3), 0)));
            r.Vector(
                "Items/ItemOperationIntent/merge stacks",
                intent,
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateMergeStacks(G(1), 5, G(2), 6, G(4), 0)));
            r.Vector(
                "Items/ItemOperationIntent/destroy",
                intent,
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateDestroy(G(1), 0, G(2), long.MaxValue)));
            r.Vector(
                "Items/ItemOperationIntent/swap container items",
                intent,
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateSwapContainerItems(G(1), 5, G(2), 6, G(4), 7)));
            r.Vector(
                "Items/ItemOperationIntent/claim one recovery item",
                intent,
                () => RealtimeProtocol.EncodeItemOperationIntent(RealtimeItemOperationIntent.CreateClaimRecoveryDelivery(
                    G(1),
                    5,
                    G(5),
                    8,
                    G(3),
                    new[] { new RealtimeItemRevisionExpectation(itemInstanceId: G(2), revision: 6) })));
            r.Vector(
                "Items/ItemOperationIntent/claim 24 recovery items",
                intent,
                () => RealtimeProtocol.EncodeItemOperationIntent(RealtimeItemOperationIntent.CreateClaimRecoveryDelivery(
                    G(1),
                    long.MaxValue,
                    G(5),
                    0,
                    G(3),
                    Enumerable.Range(0, 24)
                        .Select(index => new RealtimeItemRevisionExpectation(G(6 + (index % 9)), index))
                        .ToArray())));

            r.Vector(
                "Items/ItemOperationResult/success with revisions",
                result,
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel()));
            r.Vector(
                "Items/ItemOperationResult/success needing refresh",
                result,
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel(
                    operationKind: RealtimeItemOperationKind.SwapContainerItems,
                    requiresInventoryRefresh: true,
                    itemRevisions: Array.Empty<RealtimeItemRevision>(),
                    containerRevisions: Array.Empty<RealtimeContainerRevision>(),
                    recoveryDeliveryIds: Array.Empty<Guid>())));
            r.Vector(
                "Items/ItemOperationResult/failure",
                result,
                () => RealtimeProtocol.EncodeItemOperationResult(ItemFailureModel()));
            r.Vector(
                "Items/ItemOperationResult/maximum lists",
                result,
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel(
                    operationKind: RealtimeItemOperationKind.ClaimRecoveryDelivery,
                    itemRevisions: Enumerable.Range(0, 32)
                        .Select(index => new RealtimeItemRevision(G(1 + (index % 15)), index))
                        .ToArray(),
                    containerRevisions: Enumerable.Range(0, 8)
                        .Select(index => new RealtimeContainerRevision(G(8 + index), long.MaxValue - index))
                        .ToArray(),
                    recoveryDeliveryIds: Enumerable.Range(0, 8).Select(index => G(15 - index)).ToArray())));

            r.Accepts("Items/CarryStateChanged/baseline", CarryWire(CarryStateWire()));
            r.Accepts("Items/CarryStateChanged/zero revision and weight", CarryWire(CarryStateWire(0, 0, 1)));
            r.Rejects("Items/CarryStateChanged/negative revision", CarryWire(CarryStateWire(itemStateRevision: -1)));
            r.Rejects("Items/CarryStateChanged/negative weight", CarryWire(CarryStateWire(carriedWeight: -1)));
            r.Rejects("Items/CarryStateChanged/capacity 0", CarryWire(CarryStateWire(carryCapacity: 0)));
            r.Rejects("Items/CarryStateChanged/negative capacity", CarryWire(CarryStateWire(carryCapacity: long.MinValue)));

            RegisterItemIntentCases(r);
            RegisterItemResultCases(r);

            r.RejectsEncode("Items/CarryStateChanged/null", () => RealtimeProtocol.EncodeCarryStateChanged(null));
            r.RejectsEncode(
                "Items/CarryStateChanged/negative weight",
                () => RealtimeProtocol.EncodeCarryStateChanged(new RealtimeCarryState(1, -1, 1)));
            r.RejectsEncode(
                "Items/CarryStateChanged/capacity 0",
                () => RealtimeProtocol.EncodeCarryStateChanged(new RealtimeCarryState(1, 1, 0)));
        }

        private static void RegisterItemIntentCases(Registry r)
        {
            const byte relocate = 1;
            const byte equip = 2;
            const byte unequip = 3;
            const byte merge = 5;
            const byte destroy = 6;
            const byte swap = 8;

            r.Accepts("Items/ItemOperationIntent/relocate baseline", DestinationIntentWire(relocate));
            r.Accepts("Items/ItemOperationIntent/unequip baseline", DestinationIntentWire(unequip));
            r.Accepts("Items/ItemOperationIntent/relocate to slot 0", DestinationIntentWire(relocate, slot: 0));
            r.Accepts("Items/ItemOperationIntent/relocate to the largest slot", DestinationIntentWire(relocate, slot: int.MaxValue));
            r.Rejects("Items/ItemOperationIntent/relocate to slot -2", DestinationIntentWire(relocate, slot: -2));
            r.Rejects("Items/ItemOperationIntent/relocate without destination", DestinationIntentWire(relocate, destination: Guid.Empty));
            r.Rejects("Items/ItemOperationIntent/relocate without item", DestinationIntentWire(relocate, item: Guid.Empty));
            r.Rejects("Items/ItemOperationIntent/relocate with negative item revision", DestinationIntentWire(relocate, itemRevision: -1));
            r.Accepts("Items/ItemOperationIntent/relocate with item revision 0", DestinationIntentWire(relocate, itemRevision: 0));
            r.Rejects("Items/ItemOperationIntent/empty operation id", DestinationIntentWire(relocate, operationId: Guid.Empty));
            r.Rejects("Items/ItemOperationIntent/negative character revision", DestinationIntentWire(relocate, characterRevision: -1));
            r.Accepts("Items/ItemOperationIntent/character revision 0", DestinationIntentWire(relocate, characterRevision: 0));
            r.Rejects("Items/ItemOperationIntent/kind 0 with a relocate payload", DestinationIntentWire(0));
            r.Rejects("Items/ItemOperationIntent/kind 9 with a relocate payload", DestinationIntentWire(9));
            r.Rejects("Items/ItemOperationIntent/kind 9 without payload", ItemIntentWire(9));
            r.Rejects("Items/ItemOperationIntent/equip kind with a relocate payload", DestinationIntentWire(equip));

            r.Accepts("Items/ItemOperationIntent/equip baseline", EquipIntentWire("main_hand"));
            r.Accepts("Items/ItemOperationIntent/equip 64 character slot", EquipIntentWire(Repeat("e", 64)));
            r.Rejects("Items/ItemOperationIntent/equip 65 character slot", EquipIntentWire(Repeat("e", 65)));
            r.Rejects("Items/ItemOperationIntent/equip whitespace slot", EquipIntentWire(" "));
            r.Rejects("Items/ItemOperationIntent/equip empty slot", EquipIntentWire(""));

            r.Accepts("Items/ItemOperationIntent/split baseline", SplitIntentWire(3));
            r.Accepts("Items/ItemOperationIntent/split to any slot", SplitIntentWire(1, slot: -1));
            r.Rejects("Items/ItemOperationIntent/split quantity 0", SplitIntentWire(0));
            r.Rejects("Items/ItemOperationIntent/split negative quantity", SplitIntentWire(int.MinValue));
            r.Rejects("Items/ItemOperationIntent/split without destination", SplitIntentWire(3, destination: Guid.Empty));

            foreach (var kind in new[] { merge, swap })
            {
                var name = kind == merge ? "merge" : "swap";
                r.Accepts($"Items/ItemOperationIntent/{name} baseline", PairIntentWire(kind, G(2), G(4)));
                r.Rejects($"Items/ItemOperationIntent/{name} same item twice", PairIntentWire(kind, G(2), G(2)));
                r.Rejects($"Items/ItemOperationIntent/{name} without target", PairIntentWire(kind, G(2), Guid.Empty));
                r.Rejects(
                    $"Items/ItemOperationIntent/{name} with negative target revision",
                    PairIntentWire(kind, G(2), G(4), targetRevision: -1));
            }

            r.Accepts("Items/ItemOperationIntent/destroy baseline", ItemIntentWire(destroy).Append(ItemExpectationWire(G(2), 6)));
            r.Rejects("Items/ItemOperationIntent/destroy without item", ItemIntentWire(destroy).Append(ItemExpectationWire(Guid.Empty, 6)));

            r.Accepts("Items/ItemOperationIntent/claim baseline", ClaimIntentWire(ItemExpectationWire(G(2), 6)));
            r.Accepts(
                "Items/ItemOperationIntent/claim 24 items",
                ClaimIntentWire(Enumerable.Range(0, 24).Select(index => ItemExpectationWire(G(2), index)).ToArray()));
            r.Rejects(
                "Items/ItemOperationIntent/claim 25 items",
                ClaimIntentWire(Enumerable.Range(0, 25).Select(index => ItemExpectationWire(G(2), index)).ToArray()));
            r.Rejects("Items/ItemOperationIntent/claim no items", ClaimIntentWire());
            r.Accepts(
                "Items/ItemOperationIntent/claim the same item twice",
                ClaimIntentWire(ItemExpectationWire(G(2), 6), ItemExpectationWire(G(2), 6)));
            r.Rejects("Items/ItemOperationIntent/claim item without id", ClaimIntentWire(ItemExpectationWire(Guid.Empty, 6)));
            r.Rejects("Items/ItemOperationIntent/claim item with negative revision", ClaimIntentWire(ItemExpectationWire(G(2), -1)));
            r.Rejects(
                "Items/ItemOperationIntent/claim without delivery",
                ClaimIntentWire(new[] { ItemExpectationWire(G(2), 6) }, delivery: Guid.Empty));
            r.Rejects(
                "Items/ItemOperationIntent/claim with negative delivery revision",
                ClaimIntentWire(new[] { ItemExpectationWire(G(2), 6) }, deliveryRevision: -1));
            r.Rejects(
                "Items/ItemOperationIntent/claim without destination",
                ClaimIntentWire(new[] { ItemExpectationWire(G(2), 6) }, destination: Guid.Empty));

            r.RejectsEncode("Items/ItemOperationIntent/null", () => RealtimeProtocol.EncodeItemOperationIntent(null));
            r.RejectsEncode(
                "Items/ItemOperationIntent/empty operation id",
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateRelocate(Guid.Empty, 5, G(2), 6, G(3))));
            r.RejectsEncode(
                "Items/ItemOperationIntent/negative character revision",
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateDestroy(G(1), -1, G(2), 6)));
            r.RejectsEncode(
                "Items/ItemOperationIntent/relocate to slot -2",
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateRelocate(G(1), 5, G(2), 6, G(3), -2)));
            r.RejectsEncode(
                "Items/ItemOperationIntent/unequip without destination",
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateUnequip(G(1), 5, G(2), 6, Guid.Empty)));
            r.RejectsEncode(
                "Items/ItemOperationIntent/equip whitespace slot",
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateEquip(G(1), 5, G(2), 6, " ")));
            r.RejectsEncode(
                "Items/ItemOperationIntent/equip 65 character slot",
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateEquip(G(1), 5, G(2), 6, Repeat("e", 65))));
            r.RejectsEncode(
                "Items/ItemOperationIntent/split quantity 0",
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateSplitStack(G(1), 5, G(2), 6, 0, G(3))));
            r.RejectsEncode(
                "Items/ItemOperationIntent/merge same item",
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateMergeStacks(G(1), 5, G(2), 6, G(2), 6)));
            r.RejectsEncode(
                "Items/ItemOperationIntent/destroy negative item revision",
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateDestroy(G(1), 5, G(2), -1)));
            r.RejectsEncode(
                "Items/ItemOperationIntent/claim null items",
                () => RealtimeProtocol.EncodeItemOperationIntent(
                    RealtimeItemOperationIntent.CreateClaimRecoveryDelivery(G(1), 5, G(5), 8, G(3), null)));
            r.RejectsEncode(
                "Items/ItemOperationIntent/claim no items",
                () => RealtimeProtocol.EncodeItemOperationIntent(RealtimeItemOperationIntent.CreateClaimRecoveryDelivery(
                    G(1),
                    5,
                    G(5),
                    8,
                    G(3),
                    Array.Empty<RealtimeItemRevisionExpectation>())));
            r.RejectsEncode(
                "Items/ItemOperationIntent/claim 25 items",
                () => RealtimeProtocol.EncodeItemOperationIntent(RealtimeItemOperationIntent.CreateClaimRecoveryDelivery(
                    G(1),
                    5,
                    G(5),
                    8,
                    G(3),
                    Enumerable.Range(0, 25).Select(index => new RealtimeItemRevisionExpectation(G(2), index)).ToArray())));
            r.RejectsEncode(
                "Items/ItemOperationIntent/claim null item",
                () => RealtimeProtocol.EncodeItemOperationIntent(RealtimeItemOperationIntent.CreateClaimRecoveryDelivery(
                    G(1),
                    5,
                    G(5),
                    8,
                    G(3),
                    new RealtimeItemRevisionExpectation[] { null })));
            r.RejectsEncode(
                "Items/ItemOperationIntent/claim item without id",
                () => RealtimeProtocol.EncodeItemOperationIntent(RealtimeItemOperationIntent.CreateClaimRecoveryDelivery(
                    G(1),
                    5,
                    G(5),
                    8,
                    G(3),
                    new[] { new RealtimeItemRevisionExpectation(Guid.Empty, 1) })));
        }

        private static void RegisterItemResultCases(Registry r)
        {
            r.Accepts("Items/ItemOperationResult/success baseline", ItemResultWire());
            r.Accepts("Items/ItemOperationResult/success without lists", ItemResultWire(items: new Wire[0], containers: new Wire[0], deliveries: new Wire[0]));
            r.Accepts("Items/ItemOperationResult/failure baseline", ItemFailureWire(ErrorFragment("item_conflict", "The item changed.")));
            r.AcceptsAs(
                "Items/ItemOperationResult/succeeded byte 2 reads as true",
                ItemResultWire(succeeded: 2),
                ItemResultWire());
            r.AcceptsAs(
                "Items/ItemOperationResult/refresh byte 255 reads as true",
                ItemResultWire(requiresRefresh: 255),
                ItemResultWire(requiresRefresh: 1));
            r.Rejects("Items/ItemOperationResult/empty operation id", ItemResultWire(operationId: Guid.Empty));
            r.Rejects("Items/ItemOperationResult/kind 0", ItemResultWire(kind: 0));
            r.Rejects("Items/ItemOperationResult/kind 9", ItemResultWire(kind: 9));
            r.Rejects("Items/ItemOperationResult/invalid carry state", ItemResultWire(carry: CarryStateWire(carriedWeight: -5)));
            r.Rejects(
                "Items/ItemOperationResult/failure with item revisions",
                ItemFailureWire(ErrorFragment("code", "message"), items: new[] { RevisionWire(G(2), 7) }));
            r.Rejects(
                "Items/ItemOperationResult/failure with container revisions",
                ItemFailureWire(ErrorFragment("code", "message"), containers: new[] { RevisionWire(G(3), 9) }));
            r.Rejects(
                "Items/ItemOperationResult/failure with deliveries",
                ItemFailureWire(ErrorFragment("code", "message"), deliveries: new[] { Wire.Fragment().Guid(G(5)) }));
            r.Rejects("Items/ItemOperationResult/failure with whitespace code", ItemFailureWire(ErrorFragment(" ", "message")));
            r.Rejects("Items/ItemOperationResult/failure with whitespace message", ItemFailureWire(ErrorFragment("code", " ")));
            r.Accepts(
                "Items/ItemOperationResult/32 item revisions",
                ItemResultWire(items: Enumerable.Range(0, 32).Select(index => RevisionWire(G(2), index)).ToArray()));
            r.Rejects(
                "Items/ItemOperationResult/33 item revisions",
                ItemResultWire(items: Enumerable.Range(0, 33).Select(index => RevisionWire(G(2), index)).ToArray()));
            r.Accepts(
                "Items/ItemOperationResult/8 container revisions",
                ItemResultWire(containers: Enumerable.Range(0, 8).Select(index => RevisionWire(G(3), index)).ToArray()));
            r.Rejects(
                "Items/ItemOperationResult/9 container revisions",
                ItemResultWire(containers: Enumerable.Range(0, 9).Select(index => RevisionWire(G(3), index)).ToArray()));
            r.Accepts(
                "Items/ItemOperationResult/8 deliveries",
                ItemResultWire(deliveries: Enumerable.Range(0, 8).Select(index => Wire.Fragment().Guid(G(5))).ToArray()));
            r.Rejects(
                "Items/ItemOperationResult/9 deliveries",
                ItemResultWire(deliveries: Enumerable.Range(0, 9).Select(index => Wire.Fragment().Guid(G(5))).ToArray()));
            r.Rejects("Items/ItemOperationResult/item revision without id", ItemResultWire(items: new[] { RevisionWire(Guid.Empty, 7) }));
            r.Rejects("Items/ItemOperationResult/negative item revision", ItemResultWire(items: new[] { RevisionWire(G(2), -1) }));
            r.Rejects(
                "Items/ItemOperationResult/container revision without id",
                ItemResultWire(containers: new[] { RevisionWire(Guid.Empty, 9) }));
            r.Rejects(
                "Items/ItemOperationResult/negative container revision",
                ItemResultWire(containers: new[] { RevisionWire(G(3), -1) }));
            r.Rejects(
                "Items/ItemOperationResult/empty delivery id",
                ItemResultWire(deliveries: new[] { Wire.Fragment().Guid(Guid.Empty) }));

            r.RejectsEncode("Items/ItemOperationResult/null", () => RealtimeProtocol.EncodeItemOperationResult(null));
            r.RejectsEncode(
                "Items/ItemOperationResult/empty operation id",
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel(operationId: Guid.Empty)));
            r.RejectsEncode(
                "Items/ItemOperationResult/undefined kind",
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel(operationKind: 0)));
            r.RejectsEncode(
                "Items/ItemOperationResult/success with error",
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel(error: new RealtimeError("code", "message"))));
            r.RejectsEncode(
                "Items/ItemOperationResult/failure without error",
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel(succeeded: false)));
            r.RejectsEncode(
                "Items/ItemOperationResult/failure with revisions",
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel(
                    succeeded: false,
                    error: new RealtimeError("code", "message"))));
            r.RejectsEncode(
                "Items/ItemOperationResult/failure with whitespace code",
                () => RealtimeProtocol.EncodeItemOperationResult(ItemFailureModel(code: " ")));
            r.RejectsEncode(
                "Items/ItemOperationResult/null item revisions",
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel(itemRevisions: Null<RealtimeItemRevision[]>())));
            r.RejectsEncode(
                "Items/ItemOperationResult/33 item revisions",
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel(
                    itemRevisions: Enumerable.Range(0, 33).Select(index => new RealtimeItemRevision(G(2), index)).ToArray())));
            r.RejectsEncode(
                "Items/ItemOperationResult/null item revision",
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel(itemRevisions: new RealtimeItemRevision[] { null })));
            r.RejectsEncode(
                "Items/ItemOperationResult/negative container revision",
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel(
                    containerRevisions: new[] { new RealtimeContainerRevision(G(3), -1) })));
            r.RejectsEncode(
                "Items/ItemOperationResult/empty delivery id",
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel(recoveryDeliveryIds: new[] { Guid.Empty })));
            r.RejectsEncode(
                "Items/ItemOperationResult/null carry state",
                () => RealtimeProtocol.EncodeItemOperationResult(ItemResultModel(carryState: Null<RealtimeCarryState>())));
            r.RejectsOversize(
                "Items/ItemOperationResult/failure message above the packet limit",
                () => RealtimeProtocol.EncodeItemOperationResult(ItemFailureModel(message: Repeat("✓", 512))));
        }

        private static void RegisterOwnerItemQuantityChanged(Registry r)
        {
            const RealtimeMessageType change = RealtimeMessageType.OwnerItemQuantityChanged;
            r.Vector(
                "Items/OwnerItemQuantityChanged/arrows spent",
                change,
                () => RealtimeProtocol.EncodeOwnerItemQuantityChanged(OwnerItemQuantityChangedModel()));
            r.Vector(
                "Items/OwnerItemQuantityChanged/last arrows spent",
                change,
                () => RealtimeProtocol.EncodeOwnerItemQuantityChanged(OwnerItemQuantityChangedModel(quantity: 0, itemRevision: 0)));

            r.Accepts("Items/OwnerItemQuantityChanged/baseline", OwnerItemQuantityChangedWire());
            r.Accepts(
                "Items/OwnerItemQuantityChanged/used up",
                OwnerItemQuantityChangedWire(quantity: 0, itemRevision: 0));
            r.Rejects("Items/OwnerItemQuantityChanged/reason 0", OwnerItemQuantityChangedWire(reason: 0));
            r.Rejects("Items/OwnerItemQuantityChanged/reason 2", OwnerItemQuantityChangedWire(reason: 2));
            r.Rejects(
                "Items/OwnerItemQuantityChanged/negative previous revision",
                OwnerItemQuantityChangedWire(previousRevision: -1));
            r.Rejects(
                "Items/OwnerItemQuantityChanged/revision did not advance",
                OwnerItemQuantityChangedWire(previousRevision: 12));
            r.Rejects("Items/OwnerItemQuantityChanged/empty item", OwnerItemQuantityChangedWire(itemInstanceId: Guid.Empty));
            r.Rejects("Items/OwnerItemQuantityChanged/negative quantity", OwnerItemQuantityChangedWire(quantity: -1));
            r.Rejects(
                "Items/OwnerItemQuantityChanged/used up with a revision",
                OwnerItemQuantityChangedWire(quantity: 0));
            r.Rejects(
                "Items/OwnerItemQuantityChanged/invalid carry state",
                OwnerItemQuantityChangedWire(carry: CarryStateWire(carryCapacity: 0)));
            r.RejectsEncode(
                "Items/OwnerItemQuantityChanged/revision did not advance",
                () => RealtimeProtocol.EncodeOwnerItemQuantityChanged(OwnerItemQuantityChangedModel(previousRevision: 12)));
        }

        private static RealtimeOwnerItemQuantityChanged OwnerItemQuantityChangedModel(
            long previousRevision = 11,
            int quantity = 995,
            long itemRevision = 41)
        {
            return new RealtimeOwnerItemQuantityChanged(
                RealtimeItemQuantityChangeReason.AmmunitionConsumed,
                previousRevision,
                CarryStateModel(),
                G(5),
                quantity,
                itemRevision);
        }

        private static Wire OwnerItemQuantityChangedWire(
            byte reason = 1,
            long previousRevision = 11,
            Wire carry = null,
            Guid? itemInstanceId = null,
            int quantity = 995,
            long itemRevision = 41)
        {
            return Wire.Message(RealtimeMessageType.OwnerItemQuantityChanged)
                .U8(reason)
                .I64(previousRevision)
                .Append(carry ?? CarryStateWire())
                .Guid(itemInstanceId ?? G(5))
                .I32(quantity)
                .I64(itemRevision);
        }

        private static RealtimeCarryState CarryStateModel()
        {
            return new RealtimeCarryState(itemStateRevision: 12, carriedWeight: 210, carryCapacity: 200);
        }

        private static RealtimeItemOperationResult ItemResultModel(
            Guid? operationId = null,
            RealtimeItemOperationKind operationKind = RealtimeItemOperationKind.Relocate,
            bool succeeded = true,
            bool requiresInventoryRefresh = false,
            RealtimeError error = null,
            Optional<RealtimeCarryState> carryState = default,
            Optional<RealtimeItemRevision[]> itemRevisions = default,
            Optional<RealtimeContainerRevision[]> containerRevisions = default,
            Optional<Guid[]> recoveryDeliveryIds = default)
        {
            return new RealtimeItemOperationResult(
                operationId: operationId ?? G(1),
                operationKind: operationKind,
                succeeded: succeeded,
                requiresInventoryRefresh: requiresInventoryRefresh,
                error: error,
                carryState: carryState.Or(CarryStateModel()),
                itemRevisions: itemRevisions.Or(new[] { new RealtimeItemRevision(G(2), 7), new RealtimeItemRevision(G(4), 8) }),
                containerRevisions: containerRevisions.Or(new[] { new RealtimeContainerRevision(G(3), 9) }),
                recoveryDeliveryIds: recoveryDeliveryIds.Or(new[] { G(5) }));
        }

        private static RealtimeItemOperationResult ItemFailureModel(
            string code = "item_revision_conflict",
            string message = "The item changed before the operation.")
        {
            return ItemResultModel(
                operationKind: RealtimeItemOperationKind.Equip,
                succeeded: false,
                requiresInventoryRefresh: true,
                error: new RealtimeError(code, message),
                itemRevisions: Array.Empty<RealtimeItemRevision>(),
                containerRevisions: Array.Empty<RealtimeContainerRevision>(),
                recoveryDeliveryIds: Array.Empty<Guid>());
        }

        private static Wire CarryStateWire(long itemStateRevision = 12, long carriedWeight = 210, long carryCapacity = 200)
        {
            return Wire.Fragment().I64(itemStateRevision).I64(carriedWeight).I64(carryCapacity);
        }

        private static Wire CarryWire(Wire carryState)
        {
            return Wire.Message(RealtimeMessageType.CarryStateChanged).Append(carryState);
        }

        private static Wire ItemExpectationWire(Guid itemInstanceId, long revision)
        {
            return Wire.Fragment().Guid(itemInstanceId).I64(revision);
        }

        private static Wire RevisionWire(Guid id, long revision)
        {
            return Wire.Fragment().Guid(id).I64(revision);
        }

        private static Wire ErrorFragment(WireText code, WireText message)
        {
            return Wire.Fragment().Text(code).Text(message);
        }

        private static Wire ItemIntentWire(byte kind, Guid? operationId = null, long characterRevision = 5)
        {
            return Wire.Message(RealtimeMessageType.ItemOperationIntent)
                .Guid(operationId ?? G(1))
                .U8(kind)
                .I64(characterRevision);
        }

        private static Wire DestinationIntentWire(
            byte kind,
            Guid? operationId = null,
            long characterRevision = 5,
            Guid? item = null,
            long itemRevision = 6,
            Guid? destination = null,
            int slot = -1)
        {
            return ItemIntentWire(kind, operationId, characterRevision)
                .Append(ItemExpectationWire(item ?? G(2), itemRevision))
                .Guid(destination ?? G(3))
                .I32(slot);
        }

        private static Wire EquipIntentWire(WireText slot)
        {
            return ItemIntentWire(2).Append(ItemExpectationWire(G(2), 6)).Text(slot);
        }

        private static Wire SplitIntentWire(int quantity, Guid? destination = null, int slot = 0)
        {
            return ItemIntentWire(4)
                .Append(ItemExpectationWire(G(2), 6))
                .I32(quantity)
                .Guid(destination ?? G(3))
                .I32(slot);
        }

        private static Wire PairIntentWire(byte kind, Guid item, Guid target, long targetRevision = 0)
        {
            return ItemIntentWire(kind).Append(ItemExpectationWire(item, 6)).Append(ItemExpectationWire(target, targetRevision));
        }

        private static Wire ClaimIntentWire(params Wire[] items)
        {
            return ClaimIntentWire(items, null);
        }

        private static Wire ClaimIntentWire(
            Wire[] items,
            Guid? delivery = null,
            long deliveryRevision = 8,
            Guid? destination = null)
        {
            var wire = ItemIntentWire(7)
                .Guid(delivery ?? G(5))
                .I64(deliveryRevision)
                .Guid(destination ?? G(3))
                .U8((byte)items.Length);
            foreach (var item in items)
            {
                wire.Append(item);
            }

            return wire;
        }

        private static Wire ItemResultWire(
            Guid? operationId = null,
            byte kind = 1,
            byte succeeded = 1,
            byte requiresRefresh = 0,
            Wire carry = null,
            Wire[] items = null,
            Wire[] containers = null,
            Wire[] deliveries = null)
        {
            return ItemResultLists(
                Wire.Message(RealtimeMessageType.ItemOperationResult)
                    .Guid(operationId ?? G(1))
                    .U8(kind)
                    .U8(succeeded)
                    .U8(requiresRefresh)
                    .Append(carry ?? CarryStateWire()),
                items ?? new[] { RevisionWire(G(2), 7), RevisionWire(G(4), 8) },
                containers ?? new[] { RevisionWire(G(3), 9) },
                deliveries ?? new[] { Wire.Fragment().Guid(G(5)) });
        }

        private static Wire ItemFailureWire(
            Wire error,
            Wire[] items = null,
            Wire[] containers = null,
            Wire[] deliveries = null)
        {
            return ItemResultLists(
                Wire.Message(RealtimeMessageType.ItemOperationResult)
                    .Guid(G(1))
                    .U8(2)
                    .U8(0)
                    .U8(1)
                    .Append(CarryStateWire())
                    .Append(error),
                items ?? new Wire[0],
                containers ?? new Wire[0],
                deliveries ?? new Wire[0]);
        }

        private static Wire ItemResultLists(Wire wire, Wire[] items, Wire[] containers, Wire[] deliveries)
        {
            foreach (var list in new[] { items, containers, deliveries })
            {
                wire.U8((byte)list.Length);
                foreach (var entry in list)
                {
                    wire.Append(entry);
                }
            }

            return wire;
        }
    }
}
