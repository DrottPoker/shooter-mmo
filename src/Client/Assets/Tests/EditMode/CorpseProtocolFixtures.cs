using System;
using System.Linq;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Tests.EditMode
{
    // Realtime corpse messages shared by the corpse state and controller tests.
    internal static class CorpseProtocolFixtures
    {
        internal const long ExpiresAtUnixMilliseconds = 2_000_000_000_000L;

        internal static RealtimeCorpseInteractionResult Succeeded(
            Guid operationId,
            RealtimeCorpseInteractionKind operationKind,
            Guid corpseId,
            bool requiresInventoryRefresh = false)
        {
            return new RealtimeCorpseInteractionResult(
                operationId,
                operationKind,
                corpseId,
                true,
                false,
                requiresInventoryRefresh,
                new RealtimeCarryState(3, 0, 100),
                null);
        }

        internal static RealtimeCorpseInteractionResult Rejected(
            Guid operationId,
            RealtimeCorpseInteractionKind operationKind,
            Guid corpseId,
            string code,
            string message,
            bool requiresCorpseRefresh = false)
        {
            return new RealtimeCorpseInteractionResult(
                operationId,
                operationKind,
                corpseId,
                false,
                requiresCorpseRefresh,
                false,
                new RealtimeCarryState(3, 0, 100),
                new RealtimeError(code, message));
        }

        internal static RealtimeCorpsePresence CreatePresence(Guid corpseId, string name)
        {
            return new RealtimeCorpsePresence(
                corpseId,
                1f,
                2f,
                3f,
                45f,
                ExpiresAtUnixMilliseconds,
                false,
                name,
                "corpse.remains");
        }

        internal static RealtimeCorpseViewStateChunk[] CreateSnapshot(
            Guid corpseId,
            Guid itemId,
            Guid[] containers,
            int quantity,
            long revision,
            int generalCapacity)
        {
            var updateId = Guid.NewGuid();
            var generalSlots = Enumerable.Range(0, generalCapacity)
                .Select(index => index == 0
                    ? CreateSlot(index, itemId, quantity, 1)
                    : new RealtimeCorpseSlot(index, "general", null))
                .ToArray();
            return new[]
            {
                CreateChunk(
                    updateId,
                    RealtimeCorpseViewUpdateKind.Snapshot,
                    corpseId,
                    -1,
                    revision,
                    0,
                    3,
                    "general_inventory",
                    containers[0],
                    1,
                    true,
                    generalCapacity,
                    generalSlots),
                CreateChunk(
                    updateId,
                    RealtimeCorpseViewUpdateKind.Snapshot,
                    corpseId,
                    -1,
                    revision,
                    1,
                    3,
                    "equipment",
                    containers[1],
                    1,
                    true,
                    1,
                    new[]
                    {
                        new RealtimeCorpseSlot(
                            0,
                            "general",
                            Array.Empty<string>(),
                            "head",
                            null)
                    }),
                CreateChunk(
                    updateId,
                    RealtimeCorpseViewUpdateKind.Snapshot,
                    corpseId,
                    -1,
                    revision,
                    2,
                    3,
                    "bag",
                    containers[2],
                    1,
                    true,
                    1,
                    new[] { new RealtimeCorpseSlot(0, "general", null) })
            };
        }

        internal static RealtimeCorpseViewStateChunk CreateChunk(
            Guid updateId,
            RealtimeCorpseViewUpdateKind updateKind,
            Guid corpseId,
            long baseRevision,
            long revision,
            ushort chunkIndex,
            ushort chunkCount,
            string sectionKind,
            Guid containerId,
            long containerRevision,
            bool replaceSection,
            int slotCapacity,
            RealtimeCorpseSlot[] slots)
        {
            return new RealtimeCorpseViewStateChunk(
                updateId,
                updateKind,
                corpseId,
                baseRevision,
                revision,
                ExpiresAtUnixMilliseconds,
                "Fallen Hero",
                "corpse.remains",
                chunkIndex,
                chunkCount,
                sectionKind,
                containerId,
                containerRevision,
                replaceSection,
                slotCapacity,
                0,
                slots);
        }

        internal static RealtimeCorpseSlot CreateSlot(
            int slotIndex,
            Guid itemId,
            int quantity,
            long revision)
        {
            return new RealtimeCorpseSlot(
                slotIndex,
                "general",
                new RealtimeCorpseItem(
                    itemId,
                    "fixture.potion",
                    quantity,
                    revision,
                    Guid.Empty,
                    0));
        }
    }
}
