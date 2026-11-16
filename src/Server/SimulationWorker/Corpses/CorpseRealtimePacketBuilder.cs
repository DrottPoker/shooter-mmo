using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Corpses;

public static class CorpseRealtimePacketBuilder
{
    private const int PresencePerChunk = 6;
    private const int SlotsPerChunk = 4;
    private const int MaximumTargetedSlots = 8;

    // The encoded length of a presence chunk without entries: the packet header and the chunk's
    // own fields.
    private static readonly int PresenceChunkFixedLength = RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(
        new RealtimeCorpsePresenceSnapshotChunk(0, 0, 1, [])).Length;

    // A player corpse carries its look with the items of its visible equipment slots, in their
    // order; without visible slots it wears nothing. Every corpse says whether the viewer may
    // loot it. A corpse whose look would not fit one packet shows the default body. The corpses go
    // in corpse id order into chunks of at most PresencePerChunk that each fit one packet. A chunk
    // is as long as its fixed part plus the length of each of its entries, so every entry is
    // encoded once alone to learn its length, and the chunks are cut by adding lengths instead of
    // encoding every candidate chunk again.
    public static IReadOnlyList<RealtimeCorpsePresenceSnapshotChunk> BuildPresence(
        uint sequence,
        IReadOnlyList<ICorpseRuntimePresence> corpses,
        Guid viewerCharacterId,
        IReadOnlyList<string>? visibleEquipmentSlots = null)
    {
        ArgumentNullException.ThrowIfNull(corpses);
        var visible = visibleEquipmentSlots ?? [];
        var batches = new List<RealtimeCorpsePresence[]>();
        var current = new List<RealtimeCorpsePresence>(PresencePerChunk);
        var currentLength = PresenceChunkFixedLength;
        foreach (var corpse in corpses.OrderBy(corpse => corpse.CorpseId))
        {
            var entry = Presence(
                corpse,
                AppearanceOf(corpse.Look, visible),
                CorpseLootRights.CanLoot(corpse, viewerCharacterId));
            if (!TryMeasurePresence(sequence, entry, out var entryLength))
            {
                entry = WithoutAppearance(entry);
                if (!TryMeasurePresence(sequence, entry, out entryLength))
                {
                    throw new InvalidOperationException(
                        "One corpse presence entry exceeds the realtime packet limit.");
                }
            }

            if (current.Count > 0
                && (current.Count + 1 > PresencePerChunk
                    || currentLength + entryLength > RealtimeProtocol.MaximumPacketSize))
            {
                batches.Add([.. current]);
                current.Clear();
                currentLength = PresenceChunkFixedLength;
            }

            current.Add(entry);
            currentLength += entryLength;
        }

        if (current.Count > 0 || batches.Count == 0)
        {
            batches.Add([.. current]);
        }

        var chunkCount = batches.Count;
        var chunks = new RealtimeCorpsePresenceSnapshotChunk[chunkCount];
        for (var index = 0; index < chunkCount; index++)
        {
            chunks[index] = new RealtimeCorpsePresenceSnapshotChunk(
                sequence,
                checked((ushort)index),
                checked((ushort)chunkCount),
                batches[index]);
        }

        return chunks;
    }

    // How many bytes the entry adds to a presence chunk, from one chunk that holds it alone;
    // false when that chunk exceeds the packet limit.
    private static bool TryMeasurePresence(uint sequence, RealtimeCorpsePresence entry, out int length)
    {
        try
        {
            length = RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(
                new RealtimeCorpsePresenceSnapshotChunk(sequence, 0, 1, [entry])).Length - PresenceChunkFixedLength;
            return true;
        }
        catch (InvalidOperationException)
        {
            length = 0;
            return false;
        }
    }

    private static RealtimeCorpsePresence Presence(
        ICorpseRuntimePresence corpse,
        RealtimeCharacterAppearance? appearance,
        bool isLootable)
    {
        return new RealtimeCorpsePresence(
            corpse.CorpseId,
            checked((float)corpse.PositionX),
            checked((float)corpse.PositionY),
            checked((float)corpse.PositionZ),
            corpse.YawDegrees,
            ToUnixMilliseconds(corpse.ExpiresAt),
            corpse.IsEmpty,
            isLootable,
            corpse.Source,
            corpse.PresentationKey,
            appearance);
    }

    // A corpse whose look would not fit one packet shows the default body instead.
    private static RealtimeCorpsePresence WithoutAppearance(RealtimeCorpsePresence entry)
    {
        return new RealtimeCorpsePresence(
            entry.CorpseId,
            entry.PositionX,
            entry.PositionY,
            entry.PositionZ,
            entry.YawDegrees,
            entry.ExpiresAtUnixMilliseconds,
            entry.IsEmpty,
            entry.IsLootable,
            entry.Source,
            entry.PresentationKey,
            null);
    }

    private static RealtimeCharacterAppearance? AppearanceOf(CorpseLook? look, IReadOnlyList<string> visibleEquipmentSlots)
    {
        if (look is null)
        {
            return null;
        }

        return new RealtimeCharacterAppearance(
            look.BodyModel,
            look.Choices
                .Take(RealtimeProtocolLimits.MaximumAppearanceChoices)
                .Select(choice => new RealtimeAppearanceChoice(choice.Kind, choice.Slot, choice.Option))
                .ToArray(),
            visibleEquipmentSlots
                .Select(slot => look.Equipment.FirstOrDefault(item => string.Equals(item.EquipmentSlotId, slot, StringComparison.Ordinal)))
                .Where(item => item is not null)
                .Take(RealtimeProtocolLimits.MaximumVisibleEquipment)
                .Select(item => new RealtimeVisibleEquipment(item!.EquipmentSlotId, item.DefinitionId))
                .ToArray(),
            look.HeadwearLowered,
            // A corpse uses no tool.
            string.Empty);
    }


    public static IReadOnlyList<RealtimeCorpseViewStateChunk> BuildSnapshot(
        CorpseViewSnapshotResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return BuildState(
            Guid.NewGuid(),
            RealtimeCorpseViewUpdateKind.Snapshot,
            -1,
            snapshot,
            snapshot.Sections.Select(section => new SectionUpdate(section, true, section.Slots))
                .ToArray());
    }

    public static IReadOnlyList<RealtimeCorpseViewStateChunk> BuildDelta(
        CorpseViewSnapshotResponse previous,
        CorpseViewSnapshotResponse current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (previous.CorpseId != current.CorpseId || current.Revision < previous.Revision)
        {
            throw new ArgumentException("Corpse snapshots cannot form an ordered delta.");
        }

        var updates = new List<SectionUpdate>();
        foreach (var currentSection in current.Sections)
        {
            var previousSection = previous.Sections.SingleOrDefault(section => string.Equals(
                section.SectionKind,
                currentSection.SectionKind,
                StringComparison.Ordinal));
            if (previousSection is null
                || previousSection.ContainerId != currentSection.ContainerId
                || previousSection.SlotCapacity != currentSection.SlotCapacity)
            {
                updates.Add(new SectionUpdate(currentSection, true, currentSection.Slots));
                continue;
            }

            var changedSlots = currentSection.Slots
                .Where(slot =>
                {
                    var previousSlot = previousSection.Slots.SingleOrDefault(
                        candidate => candidate.SlotIndex == slot.SlotIndex);
                    return previousSlot is null || !SlotsMatch(previousSlot, slot);
                })
                .ToArray();
            if (changedSlots.Length == 0
                && previousSection.ContainerRevision == currentSection.ContainerRevision)
            {
                continue;
            }

            updates.Add(changedSlots.Length > MaximumTargetedSlots
                ? new SectionUpdate(currentSection, true, currentSection.Slots)
                : new SectionUpdate(currentSection, false, changedSlots));
        }

        if (updates.Count == 0)
        {
            updates.AddRange(current.Sections.Select(
                section => new SectionUpdate(section, true, section.Slots)));
        }

        return BuildState(
            Guid.NewGuid(),
            RealtimeCorpseViewUpdateKind.Delta,
            previous.Revision,
            current,
            updates);
    }

    private static IReadOnlyList<RealtimeCorpseViewStateChunk> BuildState(
        Guid updateId,
        RealtimeCorpseViewUpdateKind updateKind,
        long baseRevision,
        CorpseViewSnapshotResponse snapshot,
        IReadOnlyList<SectionUpdate> updates)
    {
        var parts = new List<SectionChunkPart>();
        var expiresAt = ToUnixMilliseconds(snapshot.ExpiresAt);
        foreach (var update in updates)
        {
            if (update.Slots.Count == 0)
            {
                parts.Add(new SectionChunkPart(
                    update.Section,
                    update.ReplaceSection,
                    0,
                    Array.Empty<CorpseViewSlotResponse>()));
                continue;
            }

            var slotOffset = 0;
            var currentSlots = new List<CorpseViewSlotResponse>();
            foreach (var slot in update.Slots)
            {
                var candidate = currentSlots.Append(slot).ToArray();
                if (currentSlots.Count > 0
                    && (candidate.Length > SlotsPerChunk
                        || !FitsStatePacket(
                            updateId,
                            updateKind,
                            baseRevision,
                            snapshot,
                            expiresAt,
                            update,
                            slotOffset,
                            candidate)))
                {
                    parts.Add(new SectionChunkPart(
                        update.Section,
                        update.ReplaceSection,
                        slotOffset,
                        currentSlots.ToArray()));
                    slotOffset += currentSlots.Count;
                    currentSlots.Clear();
                }

                currentSlots.Add(slot);
                if (!FitsStatePacket(
                        updateId,
                        updateKind,
                        baseRevision,
                        snapshot,
                        expiresAt,
                        update,
                        slotOffset,
                        currentSlots))
                {
                    throw new InvalidOperationException(
                        "One corpse state slot exceeds the realtime packet limit.");
                }
            }

            parts.Add(new SectionChunkPart(
                update.Section,
                update.ReplaceSection,
                slotOffset,
                currentSlots.ToArray()));
        }

        if (parts.Count > RealtimeProtocolLimits.MaximumCorpseChunkCount)
        {
            throw new InvalidOperationException("Corpse state requires too many realtime chunks.");
        }

        return parts.Select((part, index) => new RealtimeCorpseViewStateChunk(
                updateId,
                updateKind,
                snapshot.CorpseId,
                baseRevision,
                snapshot.Revision,
                expiresAt,
                CorpseSources.Create(snapshot.SourceCharacterName, snapshot.SourceActorDefinitionId),
                snapshot.PresentationKey,
                checked((ushort)index),
                checked((ushort)parts.Count),
                part.Section.SectionKind,
                part.Section.ContainerId,
                part.Section.ContainerRevision,
                part.ReplaceSection,
                part.Section.SlotCapacity,
                part.SlotOffset,
                part.Slots.Select(MapSlot).ToArray()))
            .ToArray();
    }

    private static bool FitsStatePacket(
        Guid updateId,
        RealtimeCorpseViewUpdateKind updateKind,
        long baseRevision,
        CorpseViewSnapshotResponse snapshot,
        long expiresAt,
        SectionUpdate update,
        int slotOffset,
        IReadOnlyCollection<CorpseViewSlotResponse> slots)
    {
        try
        {
            RealtimeProtocol.EncodeCorpseViewStateChunk(
                new RealtimeCorpseViewStateChunk(
                    updateId,
                    updateKind,
                    snapshot.CorpseId,
                    baseRevision,
                    snapshot.Revision,
                    expiresAt,
                    CorpseSources.Create(snapshot.SourceCharacterName, snapshot.SourceActorDefinitionId),
                    snapshot.PresentationKey,
                    0,
                    1,
                    update.Section.SectionKind,
                    update.Section.ContainerId,
                    update.Section.ContainerRevision,
                    update.ReplaceSection,
                    update.Section.SlotCapacity,
                    slotOffset,
                    slots.Select(MapSlot).ToArray()));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static RealtimeCorpseSlot MapSlot(CorpseViewSlotResponse slot)
    {
        return new RealtimeCorpseSlot(
            slot.SlotIndex,
            slot.SlotKind,
            slot.AcceptedTags.ToArray(),
            slot.EquipmentSlotId,
            slot.Item is null
                ? null!
                : new RealtimeCorpseItem(
                    slot.Item.ItemInstanceId,
                    slot.Item.DefinitionId,
                    slot.Item.Quantity,
                    slot.Item.Revision,
                    slot.Item.BagContentsContainerId ?? Guid.Empty,
                    slot.Item.BagContentsRevision ?? 0));
    }

    private static bool SlotsMatch(
        CorpseViewSlotResponse first,
        CorpseViewSlotResponse second)
    {
        if (first.SlotIndex != second.SlotIndex
            || !string.Equals(first.SlotKind, second.SlotKind, StringComparison.Ordinal)
            || !first.AcceptedTags.SequenceEqual(second.AcceptedTags, StringComparer.Ordinal)
            || !string.Equals(
                first.EquipmentSlotId,
                second.EquipmentSlotId,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (first.Item is null || second.Item is null)
        {
            return first.Item is null && second.Item is null;
        }

        return first.Item.ItemInstanceId == second.Item.ItemInstanceId
            && string.Equals(
                first.Item.DefinitionId,
                second.Item.DefinitionId,
                StringComparison.Ordinal)
            && first.Item.Quantity == second.Item.Quantity
            && first.Item.Revision == second.Item.Revision
            && first.Item.BagContentsContainerId == second.Item.BagContentsContainerId
            && first.Item.BagContentsRevision == second.Item.BagContentsRevision;
    }

    private static long ToUnixMilliseconds(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            .ToUnixTimeMilliseconds();
    }

    private sealed record SectionUpdate(
        CorpseViewSectionResponse Section,
        bool ReplaceSection,
        IReadOnlyList<CorpseViewSlotResponse> Slots);

    private sealed record SectionChunkPart(
        CorpseViewSectionResponse Section,
        bool ReplaceSection,
        int SlotOffset,
        IReadOnlyList<CorpseViewSlotResponse> Slots);
}
