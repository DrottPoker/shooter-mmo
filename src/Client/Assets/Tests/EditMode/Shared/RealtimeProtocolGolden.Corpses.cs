#nullable disable
using System;
using System.Linq;
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // Corpse presence, corpse interaction, and the corpse view.
    public static partial class RealtimeProtocolGolden
    {
        private const long CorpseExpiresAt = 1_760_000_000_000;

        private static void RegisterCorpses(Registry r)
        {
            const RealtimeMessageType presence = RealtimeMessageType.CorpsePresenceSnapshotChunk;
            const RealtimeMessageType result = RealtimeMessageType.CorpseInteractionResult;
            const RealtimeMessageType view = RealtimeMessageType.CorpseViewStateChunk;
            const RealtimeMessageType closed = RealtimeMessageType.CorpseViewClosed;

            r.Vector(
                "Corpses/CorpsePresenceSnapshotChunk/empty",
                presence,
                () => RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(new RealtimeCorpsePresenceSnapshotChunk(
                    snapshotSequence: 1,
                    chunkIndex: 0,
                    chunkCount: 1,
                    corpses: Array.Empty<RealtimeCorpsePresence>())));
            r.Vector(
                "Corpses/CorpsePresenceSnapshotChunk/one corpse",
                presence,
                () => RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(new RealtimeCorpsePresenceSnapshotChunk(
                    snapshotSequence: 0xA1B2C3D4,
                    chunkIndex: 0,
                    chunkCount: 1,
                    corpses: new[] { CorpsePresenceModel(G(10)) })));
            r.Vector(
                "Corpses/CorpsePresenceSnapshotChunk/six corpses",
                presence,
                () => RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(new RealtimeCorpsePresenceSnapshotChunk(
                    snapshotSequence: uint.MaxValue,
                    chunkIndex: 255,
                    chunkCount: 256,
                    corpses: Enumerable.Range(10, 6)
                        .Select(seed => CorpsePresenceModel(G(seed), isEmpty: seed % 2 == 0, expiresAt: long.MaxValue - seed))
                        .ToArray())));

            RegisterCorpseIntentVectors(r);

            r.Vector(
                "Corpses/CorpseInteractionResult/success",
                result,
                () => RealtimeProtocol.EncodeCorpseInteractionResult(CorpseResultModel()));
            r.Vector(
                "Corpses/CorpseInteractionResult/failure",
                result,
                () => RealtimeProtocol.EncodeCorpseInteractionResult(CorpseResultModel(
                    operationKind: RealtimeCorpseInteractionKind.SwapBag,
                    succeeded: false,
                    requiresCorpseRefresh: true,
                    requiresInventoryRefresh: false,
                    error: new RealtimeError("corpse_revision_conflict", "The corpse changed."))));
            r.Vector(
                "Corpses/CorpseViewStateChunk/general snapshot",
                view,
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel()));
            r.Vector(
                "Corpses/CorpseViewStateChunk/equipment snapshot",
                view,
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel(
                    sectionKind: "equipment",
                    slots: new[]
                    {
                        new RealtimeCorpseSlot(
                            slotIndex: 0,
                            slotKind: "equipment",
                            acceptedTags: new[] { "weapon" },
                            equipmentSlotId: "main_hand",
                            item: CorpseItemModel()),
                        new RealtimeCorpseSlot(
                            slotIndex: 1,
                            slotKind: "equipment",
                            acceptedTags: new[] { "armor" },
                            equipmentSlotId: "chest",
                            item: null)
                    })));
            r.Vector(
                "Corpses/CorpseViewStateChunk/bag delta",
                view,
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel(
                    updateKind: RealtimeCorpseViewUpdateKind.Delta,
                    baseRevision: 3,
                    revision: long.MaxValue,
                    chunkIndex: 1,
                    chunkCount: 2,
                    sectionKind: "bag",
                    replaceSection: false,
                    sectionSlotCapacity: 512,
                    slotOffset: 508,
                    slots: new[]
                    {
                        new RealtimeCorpseSlot(
                            slotIndex: 511,
                            slotKind: "bag",
                            acceptedTags: Array.Empty<string>(),
                            equipmentSlotId: string.Empty,
                            item: CorpseItemModel(quantity: int.MaxValue))
                    })));
            r.Vector(
                "Corpses/CorpseViewClosed/typical",
                closed,
                () => RealtimeProtocol.EncodeCorpseViewClosed(new RealtimeCorpseViewClosed(
                    corpseId: G(10),
                    code: "corpse_expired",
                    message: "The corpse decayed.")));

            RegisterCorpsePresenceCases(r);
            RegisterCorpseIntentCases(r);
            RegisterCorpseResultCases(r);
            RegisterCorpseViewCases(r);
        }

        private static void RegisterCorpseIntentVectors(Registry r)
        {
            const RealtimeMessageType intent = RealtimeMessageType.CorpseInteractionIntent;

            void Vector(string name, Func<RealtimeCorpseInteractionIntent> create)
            {
                r.Vector("Corpses/CorpseInteractionIntent/" + name, intent, () => RealtimeProtocol.EncodeCorpseInteractionIntent(create()));
            }

            Vector("open", () => RealtimeCorpseInteractionIntent.CreateOpen(G(1), G(10)));
            Vector("close", () => RealtimeCorpseInteractionIntent.CreateClose(G(1), G(10), 3));
            Vector("refresh", () => RealtimeCorpseInteractionIntent.CreateRefresh(G(1), G(10), 0));
            Vector(
                "loot item",
                () => RealtimeCorpseInteractionIntent.CreateLootItem(G(1), G(10), 3, G(2), 6, G(3), 9, 4, Guid.Empty, 0));
            Vector(
                "loot item onto a target",
                () => RealtimeCorpseInteractionIntent.CreateLootItem(G(1), G(10), 3, G(2), 6, G(3), 9, 4, G(4), 8));
            Vector(
                "loot partial stack",
                () => RealtimeCorpseInteractionIntent.CreateLootPartialStack(G(1), G(10), 3, G(2), 6, 5, G(3), 9, 0, Guid.Empty, 0));
            Vector(
                "deposit item",
                () => RealtimeCorpseInteractionIntent.CreateDepositItem(G(1), G(10), 3, G(2), 6, G(12), 7, 2, Guid.Empty, 0));
            Vector(
                "deposit partial stack onto a target",
                () => RealtimeCorpseInteractionIntent.CreateDepositPartialStack(G(1), G(10), 3, G(2), 6, 2, G(12), 7, 1, G(4), 0));
            Vector(
                "move item",
                () => RealtimeCorpseInteractionIntent.CreateMoveItem(
                    G(1),
                    G(10),
                    long.MaxValue,
                    G(2),
                    0,
                    G(12),
                    0,
                    int.MaxValue,
                    Guid.Empty,
                    0));
            Vector(
                "move partial stack",
                () => RealtimeCorpseInteractionIntent.CreateMovePartialStack(
                    G(1),
                    G(10),
                    0,
                    G(2),
                    long.MaxValue,
                    int.MaxValue,
                    G(12),
                    long.MaxValue,
                    0,
                    G(4),
                    long.MaxValue));
            Vector(
                "swap bag",
                () => RealtimeCorpseInteractionIntent.CreateSwapBag(G(1), G(10), 3, G(13), 6, G(14), 7, G(4), 8, G(15), 9));
        }

        private static void RegisterCorpsePresenceCases(Registry r)
        {
            r.Accepts("Corpses/CorpsePresenceSnapshotChunk/baseline", PresenceWire());
            r.Accepts("Corpses/CorpsePresenceSnapshotChunk/no corpses", PresenceWire(corpses: new Wire[0]));
            r.Accepts(
                "Corpses/CorpsePresenceSnapshotChunk/six corpses",
                PresenceWire(corpses: Enumerable.Range(10, 6).Select(seed => PresenceEntryWire(G(seed))).ToArray()));
            r.Rejects(
                "Corpses/CorpsePresenceSnapshotChunk/seven corpses",
                PresenceWire(corpses: Enumerable.Range(9, 7).Select(seed => PresenceEntryWire(G(seed))).ToArray()));
            r.Rejects("Corpses/CorpsePresenceSnapshotChunk/chunk count 0", PresenceWire(chunkCount: 0));
            r.Accepts("Corpses/CorpsePresenceSnapshotChunk/last of 256 chunks", PresenceWire(chunkIndex: 255, chunkCount: 256));
            r.Rejects("Corpses/CorpsePresenceSnapshotChunk/257 chunks", PresenceWire(chunkIndex: 0, chunkCount: 257));
            r.Rejects("Corpses/CorpsePresenceSnapshotChunk/chunk index equal to count", PresenceWire(chunkIndex: 1, chunkCount: 1));
            r.Rejects("Corpses/CorpsePresenceSnapshotChunk/empty corpse id", PresenceWire(corpses: new[] { PresenceEntryWire(Guid.Empty) }));
            r.Rejects(
                "Corpses/CorpsePresenceSnapshotChunk/duplicate corpse ids",
                PresenceWire(corpses: new[] { PresenceEntryWire(G(10)), PresenceEntryWire(G(10)) }));
            r.Rejects("Corpses/CorpsePresenceSnapshotChunk/expiry 0", PresenceWire(corpses: new[] { PresenceEntryWire(expiresAt: 0) }));
            r.Rejects(
                "Corpses/CorpsePresenceSnapshotChunk/NaN position",
                PresenceWire(corpses: new[] { PresenceEntryWire(positionXBits: QuietNaNBits) }));
            r.Rejects(
                "Corpses/CorpsePresenceSnapshotChunk/whitespace source name",
                PresenceWire(corpses: new[] { PresenceEntryWire(sourceDisplayName: " ") }));
            r.Rejects(
                "Corpses/CorpsePresenceSnapshotChunk/129 character source name",
                PresenceWire(corpses: new[] { PresenceEntryWire(sourceDisplayName: Repeat("n", 129)) }));
            r.Rejects(
                "Corpses/CorpsePresenceSnapshotChunk/whitespace presentation key",
                PresenceWire(corpses: new[] { PresenceEntryWire(presentationKey: " ") }));
            r.AcceptsAs(
                "Corpses/CorpsePresenceSnapshotChunk/empty byte 2 reads as true",
                PresenceWire(corpses: new[] { PresenceEntryWire(isEmpty: 2) }),
                PresenceWire(corpses: new[] { PresenceEntryWire(isEmpty: 1) }));

            r.RejectsEncode("Corpses/CorpsePresenceSnapshotChunk/null", () => RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(null));
            r.RejectsEncode(
                "Corpses/CorpsePresenceSnapshotChunk/chunk count 0",
                () => RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(new RealtimeCorpsePresenceSnapshotChunk(1, 0, 0, null)));
            r.RejectsEncode(
                "Corpses/CorpsePresenceSnapshotChunk/seven corpses",
                () => RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(new RealtimeCorpsePresenceSnapshotChunk(
                    1,
                    0,
                    1,
                    Enumerable.Range(1, 7).Select(seed => CorpsePresenceModel(G(seed))).ToArray())));
            r.RejectsEncode(
                "Corpses/CorpsePresenceSnapshotChunk/duplicate corpse ids",
                () => RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(new RealtimeCorpsePresenceSnapshotChunk(
                    1,
                    0,
                    1,
                    new[] { CorpsePresenceModel(G(10)), CorpsePresenceModel(G(10)) })));
            r.RejectsEncode(
                "Corpses/CorpsePresenceSnapshotChunk/null corpse",
                () => RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(new RealtimeCorpsePresenceSnapshotChunk(
                    1,
                    0,
                    1,
                    new RealtimeCorpsePresence[] { null })));
            r.RejectsEncode(
                "Corpses/CorpsePresenceSnapshotChunk/whitespace source name",
                () => RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(new RealtimeCorpsePresenceSnapshotChunk(
                    1,
                    0,
                    1,
                    new[] { CorpsePresenceModel(G(10), sourceDisplayName: " ") })));
            r.RejectsEncode(
                "Corpses/CorpsePresenceSnapshotChunk/expiry 0",
                () => RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(new RealtimeCorpsePresenceSnapshotChunk(
                    1,
                    0,
                    1,
                    new[] { CorpsePresenceModel(G(10), expiresAt: 0) })));
        }

        private static void RegisterCorpseIntentCases(Registry r)
        {
            const byte open = 1;
            const byte close = 2;
            const byte refresh = 3;
            const byte lootItem = 4;
            const byte lootPartial = 5;
            const byte swapBag = 6;

            r.Accepts("Corpses/CorpseInteractionIntent/open baseline", CorpseIntentWire(open, corpseRevision: -1));
            r.AcceptsAs(
                "Corpses/CorpseInteractionIntent/open ignores the corpse revision",
                CorpseIntentWire(open, corpseRevision: 5),
                CorpseIntentWire(open, corpseRevision: -1));
            r.Rejects(
                "Corpses/CorpseInteractionIntent/open with a transfer payload",
                CorpseIntentWire(open, corpseRevision: -1).Append(TransferPayloadWire()));
            r.Accepts("Corpses/CorpseInteractionIntent/close baseline", CorpseIntentWire(close));
            r.Rejects("Corpses/CorpseInteractionIntent/close revision -1", CorpseIntentWire(close, corpseRevision: -1));
            r.Accepts("Corpses/CorpseInteractionIntent/refresh revision 0", CorpseIntentWire(refresh, corpseRevision: 0));
            r.Rejects("Corpses/CorpseInteractionIntent/kind 0", CorpseIntentWire(0));
            r.Rejects("Corpses/CorpseInteractionIntent/kind 11", CorpseIntentWire(11));
            r.Rejects("Corpses/CorpseInteractionIntent/empty operation id", CorpseIntentWire(close, operationId: Guid.Empty));
            r.Rejects("Corpses/CorpseInteractionIntent/empty corpse id", CorpseIntentWire(close, corpseId: Guid.Empty));
            r.Rejects("Corpses/CorpseInteractionIntent/open with empty corpse id", CorpseIntentWire(open, corpseId: Guid.Empty, corpseRevision: -1));

            Wire Transfer(byte kind, Wire payload, long corpseRevision = 3)
            {
                return CorpseIntentWire(kind, corpseRevision: corpseRevision).Append(payload);
            }

            foreach (var kind in new byte[] { 4, 7, 9 })
            {
                var prefix = "Corpses/CorpseInteractionIntent/whole transfer " + kind + " ";
                r.Accepts(prefix + "baseline", Transfer(kind, TransferPayloadWire()));
                r.AcceptsAs(prefix + "ignores the quantity", Transfer(kind, TransferPayloadWire(quantity: 7)), Transfer(kind, TransferPayloadWire()));
            }

            foreach (var kind in new byte[] { 5, 8, 10 })
            {
                var prefix = "Corpses/CorpseInteractionIntent/partial transfer " + kind + " ";
                r.Accepts(prefix + "baseline", Transfer(kind, TransferPayloadWire(quantity: 5)));
                r.Rejects(prefix + "quantity 0", Transfer(kind, TransferPayloadWire(quantity: 0)));
                r.Rejects(prefix + "negative quantity", Transfer(kind, TransferPayloadWire(quantity: -1)));
            }

            const string transfer = "Corpses/CorpseInteractionIntent/transfer ";
            r.Accepts(transfer + "onto a target", Transfer(lootItem, TransferPayloadWire(hasTarget: 1, target: ItemExpectationWire(G(4), 8))));
            r.AcceptsAs(
                transfer + "empty target reads as no target",
                Transfer(lootItem, TransferPayloadWire(hasTarget: 1, target: ItemExpectationWire(Guid.Empty, 0))),
                Transfer(lootItem, TransferPayloadWire()));
            r.Rejects(
                transfer + "empty target with a revision",
                Transfer(lootItem, TransferPayloadWire(hasTarget: 1, target: ItemExpectationWire(Guid.Empty, 3))));
            r.Rejects(
                transfer + "target with negative revision",
                Transfer(lootItem, TransferPayloadWire(hasTarget: 1, target: ItemExpectationWire(G(4), -1))));
            r.AcceptsAs(
                transfer + "target byte 2 reads as true",
                Transfer(lootItem, TransferPayloadWire(hasTarget: 2, target: ItemExpectationWire(G(4), 8))),
                Transfer(lootItem, TransferPayloadWire(hasTarget: 1, target: ItemExpectationWire(G(4), 8))));
            r.Rejects(transfer + "target flag without target", Transfer(lootItem, TransferPayloadWire(hasTarget: 1)));
            r.Accepts(transfer + "slot 0", Transfer(lootItem, TransferPayloadWire(slot: 0)));
            r.Rejects(transfer + "slot -1", Transfer(lootItem, TransferPayloadWire(slot: -1)));
            r.Rejects(transfer + "without item", Transfer(lootItem, TransferPayloadWire(item: Guid.Empty)));
            r.Rejects(transfer + "negative item revision", Transfer(lootItem, TransferPayloadWire(itemRevision: -1)));
            r.Rejects(transfer + "without destination", Transfer(lootItem, TransferPayloadWire(destination: Guid.Empty)));
            r.Rejects(transfer + "negative destination revision", Transfer(lootItem, TransferPayloadWire(destinationRevision: -1)));
            r.Rejects(transfer + "negative corpse revision", Transfer(lootPartial, TransferPayloadWire(quantity: 1), corpseRevision: -1));

            r.Accepts("Corpses/CorpseInteractionIntent/swap bag baseline", Transfer(swapBag, SwapPayloadWire()));
            r.Rejects("Corpses/CorpseInteractionIntent/swap bag same bag", Transfer(swapBag, SwapPayloadWire(playerBag: G(13))));
            r.Rejects(
                "Corpses/CorpseInteractionIntent/swap bag same contents container",
                Transfer(swapBag, SwapPayloadWire(playerBagContainer: G(14))));
            r.Rejects("Corpses/CorpseInteractionIntent/swap bag without corpse bag", Transfer(swapBag, SwapPayloadWire(corpseBag: Guid.Empty)));
            r.Rejects(
                "Corpses/CorpseInteractionIntent/swap bag without corpse bag contents",
                Transfer(swapBag, SwapPayloadWire(corpseBagContainer: Guid.Empty)));
            r.Rejects("Corpses/CorpseInteractionIntent/swap bag without player bag", Transfer(swapBag, SwapPayloadWire(playerBag: Guid.Empty)));
            r.Rejects(
                "Corpses/CorpseInteractionIntent/swap bag without player bag contents",
                Transfer(swapBag, SwapPayloadWire(playerBagContainer: Guid.Empty)));
            r.Rejects(
                "Corpses/CorpseInteractionIntent/swap bag negative corpse bag revision",
                Transfer(swapBag, SwapPayloadWire(corpseBagRevision: -1)));
            r.Rejects(
                "Corpses/CorpseInteractionIntent/swap bag negative player contents revision",
                Transfer(swapBag, SwapPayloadWire(playerBagContainerRevision: -1)));

            r.RejectsEncode("Corpses/CorpseInteractionIntent/null", () => RealtimeProtocol.EncodeCorpseInteractionIntent(null));
            r.RejectsEncode(
                "Corpses/CorpseInteractionIntent/open without corpse",
                () => RealtimeProtocol.EncodeCorpseInteractionIntent(RealtimeCorpseInteractionIntent.CreateOpen(G(1), Guid.Empty)));
            r.RejectsEncode(
                "Corpses/CorpseInteractionIntent/close negative revision",
                () => RealtimeProtocol.EncodeCorpseInteractionIntent(RealtimeCorpseInteractionIntent.CreateClose(G(1), G(10), -1)));
            r.RejectsEncode(
                "Corpses/CorpseInteractionIntent/loot into slot -1",
                () => RealtimeProtocol.EncodeCorpseInteractionIntent(
                    RealtimeCorpseInteractionIntent.CreateLootItem(G(1), G(10), 3, G(2), 6, G(3), 9, -1, Guid.Empty, 0)));
            r.RejectsEncode(
                "Corpses/CorpseInteractionIntent/partial loot quantity 0",
                () => RealtimeProtocol.EncodeCorpseInteractionIntent(
                    RealtimeCorpseInteractionIntent.CreateLootPartialStack(G(1), G(10), 3, G(2), 6, 0, G(3), 9, 0, Guid.Empty, 0)));
            r.RejectsEncode(
                "Corpses/CorpseInteractionIntent/no target with a target revision",
                () => RealtimeProtocol.EncodeCorpseInteractionIntent(
                    RealtimeCorpseInteractionIntent.CreateDepositItem(G(1), G(10), 3, G(2), 6, G(3), 9, 0, Guid.Empty, 4)));
            r.RejectsEncode(
                "Corpses/CorpseInteractionIntent/swap the same bag",
                () => RealtimeProtocol.EncodeCorpseInteractionIntent(
                    RealtimeCorpseInteractionIntent.CreateSwapBag(G(1), G(10), 3, G(13), 6, G(14), 7, G(13), 8, G(15), 9)));
            r.RejectsEncode(
                "Corpses/CorpseInteractionIntent/swap into the same container",
                () => RealtimeProtocol.EncodeCorpseInteractionIntent(
                    RealtimeCorpseInteractionIntent.CreateSwapBag(G(1), G(10), 3, G(13), 6, G(14), 7, G(4), 8, G(14), 9)));
        }

        private static void RegisterCorpseResultCases(Registry r)
        {
            r.Accepts("Corpses/CorpseInteractionResult/success baseline", CorpseResultWire());
            r.Accepts("Corpses/CorpseInteractionResult/success without refresh", CorpseResultWire(refreshInventory: 0));
            r.Rejects("Corpses/CorpseInteractionResult/success with corpse refresh", CorpseResultWire(refreshCorpse: 1));
            r.Accepts(
                "Corpses/CorpseInteractionResult/failure baseline",
                CorpseResultWire(succeeded: 0, refreshCorpse: 1, error: ErrorFragment("corpse_conflict", "The corpse changed.")));
            r.Rejects(
                "Corpses/CorpseInteractionResult/failure with whitespace message",
                CorpseResultWire(succeeded: 0, error: ErrorFragment("corpse_conflict", " ")));
            r.Rejects("Corpses/CorpseInteractionResult/failure without error", CorpseResultWire(succeeded: 0));
            r.Rejects("Corpses/CorpseInteractionResult/kind 0", CorpseResultWire(kind: 0));
            r.Rejects("Corpses/CorpseInteractionResult/empty operation id", CorpseResultWire(operationId: Guid.Empty));
            r.Rejects("Corpses/CorpseInteractionResult/empty corpse id", CorpseResultWire(corpseId: Guid.Empty));
            r.Rejects("Corpses/CorpseInteractionResult/invalid carry state", CorpseResultWire(carry: CarryStateWire(itemStateRevision: -1)));
            r.AcceptsAs(
                "Corpses/CorpseInteractionResult/succeeded byte 2 reads as true",
                CorpseResultWire(succeeded: 2),
                CorpseResultWire());

            r.RejectsEncode("Corpses/CorpseInteractionResult/null", () => RealtimeProtocol.EncodeCorpseInteractionResult(null));
            r.RejectsEncode(
                "Corpses/CorpseInteractionResult/success with corpse refresh",
                () => RealtimeProtocol.EncodeCorpseInteractionResult(CorpseResultModel(requiresCorpseRefresh: true)));
            r.RejectsEncode(
                "Corpses/CorpseInteractionResult/success with error",
                () => RealtimeProtocol.EncodeCorpseInteractionResult(CorpseResultModel(error: new RealtimeError("code", "message"))));
            r.RejectsEncode(
                "Corpses/CorpseInteractionResult/failure without error",
                () => RealtimeProtocol.EncodeCorpseInteractionResult(CorpseResultModel(succeeded: false)));
            r.RejectsEncode(
                "Corpses/CorpseInteractionResult/failure with whitespace code",
                () => RealtimeProtocol.EncodeCorpseInteractionResult(CorpseResultModel(
                    succeeded: false,
                    error: new RealtimeError(" ", "message"))));
            r.RejectsEncode(
                "Corpses/CorpseInteractionResult/undefined kind",
                () => RealtimeProtocol.EncodeCorpseInteractionResult(CorpseResultModel(operationKind: 0)));
            r.RejectsEncode(
                "Corpses/CorpseInteractionResult/null carry state",
                () => RealtimeProtocol.EncodeCorpseInteractionResult(CorpseResultModel(carryState: Null<RealtimeCarryState>())));
        }

        private static void RegisterCorpseViewCases(Registry r)
        {
            const string prefix = "Corpses/CorpseViewStateChunk/";
            r.Accepts(prefix + "baseline", CorpseViewWire());
            r.Accepts(prefix + "delta", CorpseViewWire(updateKind: 2, baseRevision: 3, replaceSection: 0));
            r.Accepts(prefix + "delta from no base", CorpseViewWire(updateKind: 2, baseRevision: -1, replaceSection: 0));
            r.Rejects(prefix + "snapshot with a base revision", CorpseViewWire(baseRevision: 3));
            r.Rejects(prefix + "snapshot without replacing", CorpseViewWire(replaceSection: 0));
            r.AcceptsAs(prefix + "replace byte 2 reads as true", CorpseViewWire(replaceSection: 2), CorpseViewWire());
            r.Rejects(prefix + "empty update id", CorpseViewWire(updateId: Guid.Empty));
            r.Rejects(prefix + "update kind 0", CorpseViewWire(updateKind: 0));
            r.Rejects(prefix + "update kind 3", CorpseViewWire(updateKind: 3));
            r.Rejects(prefix + "empty corpse id", CorpseViewWire(corpseId: Guid.Empty));
            r.Rejects(prefix + "base revision -2", CorpseViewWire(updateKind: 2, baseRevision: -2, replaceSection: 0));
            r.Rejects(prefix + "revision -1", CorpseViewWire(revision: -1));
            r.Rejects(prefix + "revision below base", CorpseViewWire(updateKind: 2, baseRevision: 5, revision: 4, replaceSection: 0));
            r.Rejects(prefix + "expiry 0", CorpseViewWire(expiresAt: 0));
            r.Rejects(prefix + "whitespace source name", CorpseViewWire(sourceDisplayName: " "));
            r.Rejects(prefix + "whitespace presentation key", CorpseViewWire(presentationKey: " "));
            r.Rejects(prefix + "chunk count 0", CorpseViewWire(chunkCount: 0));
            r.Rejects(prefix + "257 chunks", CorpseViewWire(chunkCount: 257));
            r.Accepts(prefix + "last of 256 chunks", CorpseViewWire(chunkIndex: 255, chunkCount: 256));
            r.Rejects(prefix + "chunk index equal to count", CorpseViewWire(chunkIndex: 1, chunkCount: 1));
            r.Rejects(prefix + "unknown section", CorpseViewWire(sectionKind: "inventory"));
            r.Rejects(prefix + "whitespace section", CorpseViewWire(sectionKind: " "));
            r.Rejects(prefix + "33 character section", CorpseViewWire(sectionKind: Repeat("s", 33)));
            r.Accepts(prefix + "bag section", CorpseViewWire(sectionKind: "bag"));
            r.Rejects(prefix + "empty container", CorpseViewWire(containerId: Guid.Empty));
            r.Rejects(prefix + "negative container revision", CorpseViewWire(containerRevision: -1));
            r.Accepts(prefix + "empty section", CorpseViewWire(sectionSlotCapacity: 0, slots: new Wire[0]));
            r.Rejects(prefix + "slot in empty section", CorpseViewWire(sectionSlotCapacity: 0));
            r.Accepts(prefix + "capacity 512", CorpseViewWire(sectionSlotCapacity: 512));
            r.Rejects(prefix + "capacity 513", CorpseViewWire(sectionSlotCapacity: 513));
            r.Rejects(prefix + "negative slot offset", CorpseViewWire(slotOffset: -1));
            r.Accepts(prefix + "no slots", CorpseViewWire(slots: new Wire[0]));
            r.Accepts(prefix + "four slots", CorpseViewWire(slots: Enumerable.Range(0, 4).Select(index => CorpseSlotWire(index)).ToArray()));
            r.Rejects(prefix + "five slots", CorpseViewWire(slots: Enumerable.Range(0, 5).Select(index => CorpseSlotWire(index)).ToArray()));
            r.Rejects(prefix + "negative slot index", CorpseViewWire(slots: new[] { CorpseSlotWire(-1) }));
            r.Rejects(prefix + "slot index equal to capacity", CorpseViewWire(slots: new[] { CorpseSlotWire(20) }));
            r.Rejects(prefix + "duplicate slot index", CorpseViewWire(slots: new[] { CorpseSlotWire(3), CorpseSlotWire(3) }));
            r.Rejects(prefix + "whitespace slot kind", CorpseViewWire(slots: new[] { CorpseSlotWire(0, slotKind: " ") }));
            r.Accepts(prefix + "32 character slot kind", CorpseViewWire(slots: new[] { CorpseSlotWire(0, slotKind: Repeat("k", 32)) }));
            r.Rejects(prefix + "33 character slot kind", CorpseViewWire(slots: new[] { CorpseSlotWire(0, slotKind: Repeat("k", 33)) }));
            r.Accepts(
                prefix + "16 tags",
                CorpseViewWire(slots: new[] { CorpseSlotWire(0, tags: Enumerable.Range(0, 16).Select(index => (WireText)("t" + index)).ToArray()) }));
            r.Rejects(
                prefix + "17 tags",
                CorpseViewWire(slots: new[] { CorpseSlotWire(0, tags: Enumerable.Range(0, 17).Select(index => (WireText)("t" + index)).ToArray()) }));
            r.Rejects(prefix + "whitespace tag", CorpseViewWire(slots: new[] { CorpseSlotWire(0, tags: Tags("ammo", " ")) }));
            r.Accepts(prefix + "64 character tag", CorpseViewWire(slots: new[] { CorpseSlotWire(0, tags: Tags(Repeat("t", 64))) }));
            r.Rejects(prefix + "65 character tag", CorpseViewWire(slots: new[] { CorpseSlotWire(0, tags: Tags(Repeat("t", 65))) }));
            r.Rejects(prefix + "duplicate tags", CorpseViewWire(slots: new[] { CorpseSlotWire(0, tags: Tags("ammo", "ammo")) }));
            r.Accepts(prefix + "tags that differ by case", CorpseViewWire(slots: new[] { CorpseSlotWire(0, tags: Tags("ammo", "Ammo")) }));
            r.Accepts(
                prefix + "equipment slots",
                CorpseViewWire(sectionKind: "equipment", slots: new[] { CorpseSlotWire(0, equipmentSlotId: "main_hand") }));
            r.Rejects(
                prefix + "equipment slot without id",
                CorpseViewWire(sectionKind: "equipment", slots: new[] { CorpseSlotWire(0) }));
            r.Rejects(
                prefix + "equipment slot with a 65 character id",
                CorpseViewWire(sectionKind: "equipment", slots: new[] { CorpseSlotWire(0, equipmentSlotId: Repeat("e", 65)) }));
            r.Rejects(
                prefix + "general slot with an equipment id",
                CorpseViewWire(slots: new[] { CorpseSlotWire(0, equipmentSlotId: "main_hand") }));
            r.AcceptsAs(
                prefix + "general slot with a whitespace equipment id",
                CorpseViewWire(slots: new[] { CorpseSlotWire(0, equipmentSlotId: " ") }),
                CorpseViewWire(slots: new[] { CorpseSlotWire(0) }));
            r.AcceptsAs(
                prefix + "item byte 2 reads as true",
                CorpseViewWire(slots: new[] { CorpseSlotWire(0, item: CorpseItemWire(), itemFlag: 2) }),
                CorpseViewWire(slots: new[] { CorpseSlotWire(0, item: CorpseItemWire()) }));
            r.Rejects(prefix + "item without id", CorpseViewWire(slots: new[] { CorpseSlotWire(0, item: CorpseItemWire(itemInstanceId: Guid.Empty)) }));
            r.Rejects(prefix + "whitespace definition", CorpseViewWire(slots: new[] { CorpseSlotWire(0, item: CorpseItemWire(definitionId: " ")) }));
            r.Rejects(
                prefix + "129 character definition",
                CorpseViewWire(slots: new[] { CorpseSlotWire(0, item: CorpseItemWire(definitionId: Repeat("d", 129))) }));
            r.Rejects(prefix + "item quantity 0", CorpseViewWire(slots: new[] { CorpseSlotWire(0, item: CorpseItemWire(quantity: 0)) }));
            r.Rejects(prefix + "negative item revision", CorpseViewWire(slots: new[] { CorpseSlotWire(0, item: CorpseItemWire(revision: -1)) }));
            r.Rejects(
                prefix + "no bag with a bag revision",
                CorpseViewWire(slots: new[] { CorpseSlotWire(0, item: CorpseItemWire(bagContentsRevision: 2)) }));
            r.Accepts(
                prefix + "bag with revision 0",
                CorpseViewWire(slots: new[] { CorpseSlotWire(0, item: CorpseItemWire(bagContentsContainerId: G(14), bagContentsRevision: 0)) }));
            r.Rejects(
                prefix + "bag with negative revision",
                CorpseViewWire(slots: new[] { CorpseSlotWire(0, item: CorpseItemWire(bagContentsContainerId: G(14), bagContentsRevision: -1)) }));

            r.Accepts("Corpses/CorpseViewClosed/baseline", CorpseClosedWire());
            r.Rejects("Corpses/CorpseViewClosed/empty corpse id", CorpseClosedWire(corpseId: Guid.Empty));
            r.AcceptsUnencodable("Corpses/CorpseViewClosed/whitespace code", CorpseClosedWire(code: " "));
            r.AcceptsUnencodable("Corpses/CorpseViewClosed/whitespace message", CorpseClosedWire(message: " "));
            r.Rejects("Corpses/CorpseViewClosed/65 character code", CorpseClosedWire(code: Repeat("c", 65)));
            r.Rejects("Corpses/CorpseViewClosed/empty message", CorpseClosedWire(message: ""));

            r.RejectsEncode("Corpses/CorpseViewStateChunk/null", () => RealtimeProtocol.EncodeCorpseViewStateChunk(null));
            r.RejectsEncode(
                "Corpses/CorpseViewStateChunk/snapshot with a base revision",
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel(baseRevision: 3)));
            r.RejectsEncode(
                "Corpses/CorpseViewStateChunk/unknown section",
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel(sectionKind: "inventory")));
            r.RejectsEncode(
                "Corpses/CorpseViewStateChunk/five slots",
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel(
                    slots: Enumerable.Range(0, 5).Select(index => CorpseSlotModel(index)).ToArray())));
            r.RejectsEncode(
                "Corpses/CorpseViewStateChunk/null slot",
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel(slots: new RealtimeCorpseSlot[] { null })));
            r.RejectsEncode(
                "Corpses/CorpseViewStateChunk/duplicate slot index",
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel(slots: new[] { CorpseSlotModel(1), CorpseSlotModel(1) })));
            r.RejectsEncode(
                "Corpses/CorpseViewStateChunk/slot index equal to capacity",
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel(slots: new[] { CorpseSlotModel(20) })));
            r.RejectsEncode(
                "Corpses/CorpseViewStateChunk/equipment slot without id",
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel(sectionKind: "equipment")));
            r.RejectsEncode(
                "Corpses/CorpseViewStateChunk/general slot with an equipment id",
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel(slots: new[]
                {
                    new RealtimeCorpseSlot(0, "general", Array.Empty<string>(), "main_hand", null)
                })));
            r.RejectsEncode(
                "Corpses/CorpseViewStateChunk/duplicate tags",
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel(slots: new[]
                {
                    new RealtimeCorpseSlot(0, "general", new[] { "ammo", "ammo" }, string.Empty, null)
                })));
            r.RejectsEncode(
                "Corpses/CorpseViewStateChunk/item quantity 0",
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel(slots: new[]
                {
                    new RealtimeCorpseSlot(0, "general", Array.Empty<string>(), string.Empty, CorpseItemModel(quantity: 0))
                })));
            r.RejectsOversize(
                "Corpses/CorpseViewStateChunk/long tags above the packet limit",
                () => RealtimeProtocol.EncodeCorpseViewStateChunk(CorpseViewModel(slots: Enumerable.Range(0, 4)
                    .Select(index => new RealtimeCorpseSlot(
                        index,
                        "general",
                        Enumerable.Range(0, 16).Select(tag => tag.ToString("00") + Repeat("t", 62)).ToArray(),
                        string.Empty,
                        null))
                    .ToArray())));
            r.RejectsEncode("Corpses/CorpseViewClosed/null", () => RealtimeProtocol.EncodeCorpseViewClosed(null));
            r.RejectsEncode(
                "Corpses/CorpseViewClosed/empty corpse id",
                () => RealtimeProtocol.EncodeCorpseViewClosed(new RealtimeCorpseViewClosed(Guid.Empty, "code", "message")));
            r.RejectsEncode(
                "Corpses/CorpseViewClosed/whitespace code",
                () => RealtimeProtocol.EncodeCorpseViewClosed(new RealtimeCorpseViewClosed(G(10), " ", "message")));
            r.RejectsEncode(
                "Corpses/CorpseViewClosed/513 character message",
                () => RealtimeProtocol.EncodeCorpseViewClosed(new RealtimeCorpseViewClosed(G(10), "code", Repeat("m", 513))));
        }

        private static RealtimeCorpsePresence CorpsePresenceModel(
            Guid corpseId,
            bool isEmpty = false,
            long expiresAt = CorpseExpiresAt,
            string sourceDisplayName = "Grön Ödla")
        {
            return new RealtimeCorpsePresence(
                corpseId: corpseId,
                positionX: 5.5f,
                positionY: 0f,
                positionZ: -7.25f,
                expiresAtUnixMilliseconds: expiresAt,
                isEmpty: isEmpty,
                sourceDisplayName: sourceDisplayName,
                presentationKey: "corpse.player");
        }

        private static RealtimeCorpseInteractionResult CorpseResultModel(
            RealtimeCorpseInteractionKind operationKind = RealtimeCorpseInteractionKind.LootItem,
            bool succeeded = true,
            bool requiresCorpseRefresh = false,
            bool requiresInventoryRefresh = true,
            RealtimeError error = null,
            Optional<RealtimeCarryState> carryState = default)
        {
            return new RealtimeCorpseInteractionResult(
                operationId: G(1),
                operationKind: operationKind,
                corpseId: G(10),
                succeeded: succeeded,
                requiresCorpseRefresh: requiresCorpseRefresh,
                requiresInventoryRefresh: requiresInventoryRefresh,
                error: error,
                carryState: carryState.Or(CarryStateModel()));
        }

        private static RealtimeCorpseItem CorpseItemModel(int quantity = 3)
        {
            return new RealtimeCorpseItem(
                itemInstanceId: G(2),
                definitionId: "item.bandage",
                quantity: quantity,
                revision: 6,
                bagContentsContainerId: Guid.Empty,
                bagContentsRevision: 0);
        }

        private static RealtimeCorpseSlot CorpseSlotModel(int slotIndex)
        {
            return new RealtimeCorpseSlot(
                slotIndex: slotIndex,
                slotKind: "general",
                acceptedTags: Array.Empty<string>(),
                equipmentSlotId: string.Empty,
                item: null);
        }

        private static RealtimeCorpseViewStateChunk CorpseViewModel(
            RealtimeCorpseViewUpdateKind updateKind = RealtimeCorpseViewUpdateKind.Snapshot,
            long baseRevision = -1,
            long revision = 4,
            ushort chunkIndex = 0,
            ushort chunkCount = 1,
            string sectionKind = "general_inventory",
            bool replaceSection = true,
            int sectionSlotCapacity = 20,
            int slotOffset = 0,
            RealtimeCorpseSlot[] slots = null)
        {
            return new RealtimeCorpseViewStateChunk(
                updateId: G(11),
                updateKind: updateKind,
                corpseId: G(10),
                baseRevision: baseRevision,
                revision: revision,
                expiresAtUnixMilliseconds: CorpseExpiresAt,
                sourceDisplayName: "Grön Ödla",
                presentationKey: "corpse.player",
                chunkIndex: chunkIndex,
                chunkCount: chunkCount,
                sectionKind: sectionKind,
                containerId: G(12),
                containerRevision: 6,
                replaceSection: replaceSection,
                sectionSlotCapacity: sectionSlotCapacity,
                slotOffset: slotOffset,
                slots: slots ?? new[]
                {
                    new RealtimeCorpseSlot(
                        slotIndex: 0,
                        slotKind: "general",
                        acceptedTags: new[] { "consumable" },
                        equipmentSlotId: string.Empty,
                        item: CorpseItemModel()),
                    new RealtimeCorpseSlot(
                        slotIndex: 1,
                        slotKind: "general",
                        acceptedTags: Array.Empty<string>(),
                        equipmentSlotId: string.Empty,
                        item: null),
                    new RealtimeCorpseSlot(
                        slotIndex: 2,
                        slotKind: "bag",
                        acceptedTags: new[] { "bag", "container" },
                        equipmentSlotId: string.Empty,
                        item: new RealtimeCorpseItem(G(13), "item.backpack", 1, 2, G(14), 5)),
                    new RealtimeCorpseSlot(
                        slotIndex: 19,
                        slotKind: "general",
                        acceptedTags: new[] { "ammo" },
                        equipmentSlotId: string.Empty,
                        item: null)
                });
        }

        private static WireText[] Tags(params string[] values)
        {
            return values.Select(value => (WireText)value).ToArray();
        }

        private static Wire PresenceEntryWire(
            Guid? corpseId = null,
            uint? positionXBits = null,
            long expiresAt = CorpseExpiresAt,
            byte isEmpty = 0,
            WireText sourceDisplayName = null,
            WireText presentationKey = null)
        {
            return Wire.Fragment()
                .Guid(corpseId ?? G(10))
                .F32Or(5.5f, positionXBits)
                .F32(0f)
                .F32(-7.25f)
                .I64(expiresAt)
                .U8(isEmpty)
                .Text(sourceDisplayName ?? "Grön Ödla")
                .Text(presentationKey ?? "corpse.player");
        }

        private static Wire PresenceWire(ushort chunkIndex = 0, ushort chunkCount = 1, Wire[] corpses = null)
        {
            var list = corpses ?? new[] { PresenceEntryWire() };
            var wire = Wire.Message(RealtimeMessageType.CorpsePresenceSnapshotChunk)
                .U32(0xA1B2C3D4)
                .U16(chunkIndex)
                .U16(chunkCount)
                .U8((byte)list.Length);
            foreach (var corpse in list)
            {
                wire.Append(corpse);
            }

            return wire;
        }

        private static Wire CorpseIntentWire(byte kind, Guid? operationId = null, Guid? corpseId = null, long corpseRevision = 3)
        {
            return Wire.Message(RealtimeMessageType.CorpseInteractionIntent)
                .Guid(operationId ?? G(1))
                .U8(kind)
                .Guid(corpseId ?? G(10))
                .I64(corpseRevision);
        }

        private static Wire TransferPayloadWire(
            Guid? item = null,
            long itemRevision = 6,
            int quantity = 0,
            Guid? destination = null,
            long destinationRevision = 9,
            int slot = 4,
            byte hasTarget = 0,
            Wire target = null)
        {
            return Wire.Fragment()
                .Append(ItemExpectationWire(item ?? G(2), itemRevision))
                .I32(quantity)
                .Guid(destination ?? G(3))
                .I64(destinationRevision)
                .I32(slot)
                .U8(hasTarget)
                .Append(target ?? Wire.Fragment());
        }

        private static Wire SwapPayloadWire(
            Guid? corpseBag = null,
            long corpseBagRevision = 6,
            Guid? corpseBagContainer = null,
            long corpseBagContainerRevision = 7,
            Guid? playerBag = null,
            long playerBagRevision = 8,
            Guid? playerBagContainer = null,
            long playerBagContainerRevision = 9)
        {
            return Wire.Fragment()
                .Append(ItemExpectationWire(corpseBag ?? G(13), corpseBagRevision))
                .Guid(corpseBagContainer ?? G(14))
                .I64(corpseBagContainerRevision)
                .Append(ItemExpectationWire(playerBag ?? G(4), playerBagRevision))
                .Guid(playerBagContainer ?? G(15))
                .I64(playerBagContainerRevision);
        }

        private static Wire CorpseResultWire(
            Guid? operationId = null,
            byte kind = 4,
            Guid? corpseId = null,
            byte succeeded = 1,
            byte refreshCorpse = 0,
            byte refreshInventory = 1,
            Wire carry = null,
            Wire error = null)
        {
            return Wire.Message(RealtimeMessageType.CorpseInteractionResult)
                .Guid(operationId ?? G(1))
                .U8(kind)
                .Guid(corpseId ?? G(10))
                .U8(succeeded)
                .U8(refreshCorpse)
                .U8(refreshInventory)
                .Append(carry ?? CarryStateWire())
                .Append(error ?? Wire.Fragment());
        }

        private static Wire CorpseItemWire(
            Guid? itemInstanceId = null,
            WireText definitionId = null,
            int quantity = 3,
            long revision = 6,
            Guid? bagContentsContainerId = null,
            long bagContentsRevision = 0)
        {
            return Wire.Fragment()
                .Guid(itemInstanceId ?? G(2))
                .Text(definitionId ?? "item.bandage")
                .I32(quantity)
                .I64(revision)
                .Guid(bagContentsContainerId ?? Guid.Empty)
                .I64(bagContentsRevision);
        }

        private static Wire CorpseSlotWire(
            int slotIndex,
            WireText slotKind = null,
            WireText[] tags = null,
            WireText equipmentSlotId = null,
            Wire item = null,
            byte itemFlag = 1)
        {
            var list = tags ?? new WireText[0];
            var wire = Wire.Fragment().I32(slotIndex).Text(slotKind ?? "general").U8((byte)list.Length);
            foreach (var tag in list)
            {
                wire.Text(tag);
            }

            wire = equipmentSlotId == null ? wire.U8(0) : wire.U8(1).Text(equipmentSlotId);
            return item == null ? wire.U8(0) : wire.U8(itemFlag).Append(item);
        }

        private static Wire CorpseViewWire(
            Guid? updateId = null,
            byte updateKind = 1,
            Guid? corpseId = null,
            long baseRevision = -1,
            long revision = 4,
            long expiresAt = CorpseExpiresAt,
            WireText sourceDisplayName = null,
            WireText presentationKey = null,
            ushort chunkIndex = 0,
            ushort chunkCount = 1,
            WireText sectionKind = null,
            Guid? containerId = null,
            long containerRevision = 6,
            byte replaceSection = 1,
            int sectionSlotCapacity = 20,
            int slotOffset = 0,
            Wire[] slots = null)
        {
            var list = slots ?? new[]
            {
                CorpseSlotWire(0, tags: Tags("consumable"), item: CorpseItemWire()),
                CorpseSlotWire(1)
            };
            var wire = Wire.Message(RealtimeMessageType.CorpseViewStateChunk)
                .Guid(updateId ?? G(11))
                .U8(updateKind)
                .Guid(corpseId ?? G(10))
                .I64(baseRevision)
                .I64(revision)
                .I64(expiresAt)
                .Text(sourceDisplayName ?? "Grön Ödla")
                .Text(presentationKey ?? "corpse.player")
                .U16(chunkIndex)
                .U16(chunkCount)
                .Text(sectionKind ?? "general_inventory")
                .Guid(containerId ?? G(12))
                .I64(containerRevision)
                .U8(replaceSection)
                .I32(sectionSlotCapacity)
                .I32(slotOffset)
                .U8((byte)list.Length);
            foreach (var slot in list)
            {
                wire.Append(slot);
            }

            return wire;
        }

        private static Wire CorpseClosedWire(Guid? corpseId = null, WireText code = null, WireText message = null)
        {
            return Wire.Message(RealtimeMessageType.CorpseViewClosed)
                .Guid(corpseId ?? G(10))
                .Text(code ?? "corpse_expired")
                .Text(message ?? "The corpse decayed.");
        }
    }
}
