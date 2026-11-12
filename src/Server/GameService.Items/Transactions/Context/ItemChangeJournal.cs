using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;

namespace Mmorpg.GameService.Items.Transactions;

// The in-memory change journal of one item transaction: audit changes, touched
// characters and containers, result items, Recovery deliveries, the named result sections,
// and the death-only carry overflow permission. The kernel's finalizer turns it into
// revisions, audit rows, and the result.
internal sealed class ItemChangeJournal(
    ItemTransactionSession session,
    ItemStateReader reader)
{
    // A section is a top-level key of the stored result, so it may not take the name of a
    // result property. Stored results are read without regard to case.
    private static readonly FrozenSet<string> ReservedSectionNames = typeof(ItemTransactionResult)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(property => ItemOperationJson.Options.PropertyNamingPolicy!.ConvertName(property.Name))
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<Guid> touchedCharacterIds = [];
    private readonly HashSet<Guid> touchedContainerIds = [];
    private readonly HashSet<Guid> resultItemIds = [];
    private readonly HashSet<Guid> changedItemIds = [];
    private readonly List<ItemAuditChange> auditChanges = [];
    private readonly List<Guid> recoveryDeliveryIds = [];
    private readonly Dictionary<string, PendingSection> sections = new(StringComparer.Ordinal);
    private bool allowInvoluntaryHardCapOverflow;

    private string OperationKind => session.OperationKind;

    // Read-only views for the kernel's finalizer.
    internal IReadOnlyCollection<Guid> TouchedCharacterIds => touchedCharacterIds;

    internal IReadOnlyCollection<Guid> TouchedContainerIds => touchedContainerIds;

    internal IReadOnlyCollection<Guid> ResultItemIds => resultItemIds;

    // Every item an audit change recorded, so the result can report each item's final state.
    internal IReadOnlyCollection<Guid> ChangedItemIds => changedItemIds;

    internal IReadOnlyList<ItemAuditChange> AuditChanges => auditChanges;

    internal IReadOnlyList<Guid> RecoveryDeliveryIds => recoveryDeliveryIds;

    internal bool AllowsInvoluntaryHardCapOverflow => allowInvoluntaryHardCapOverflow;

    public async Task<string?> CaptureItemStateJsonAsync(
        Guid itemInstanceId,
        CancellationToken cancellationToken)
    {
        var item = await reader.LoadItemAsync(itemInstanceId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var policies = await reader.LoadPoliciesAsync(itemInstanceId, cancellationToken);
        var state = new ItemAuditState(
            item.ItemInstanceId,
            item.DefinitionId,
            item.Quantity,
            item.Revision,
            item.ContainerId,
            item.ContainerSlotIndex,
            item.EquippedCharacterId,
            item.EquipmentSlotId,
            policies.Select(policy => new ItemPolicyAuditState(
                policy.PolicyKind,
                policy.Status,
                policy.SourceKind,
                policy.SourceId,
                policy.Revision)).ToArray());
        return JsonSerializer.Serialize(state, ItemOperationJson.Options);
    }

    public void AddItemAudit(
        string changeKind,
        Guid itemInstanceId,
        string? beforeState,
        string? afterState)
    {
        if (beforeState is null && afterState is null)
        {
            throw new InvalidOperationException("An item audit change requires before or after state.");
        }

        changedItemIds.Add(itemInstanceId);
        auditChanges.Add(new ItemAuditChange(
            changeKind,
            afterState is null ? null : itemInstanceId,
            beforeState,
            afterState));
    }

    public void AddMetadataAudit(
        string changeKind,
        object? beforeState,
        object? afterState)
    {
        if (beforeState is null && afterState is null)
        {
            throw new InvalidOperationException("A metadata audit change requires before or after state.");
        }

        auditChanges.Add(new ItemAuditChange(
            changeKind,
            null,
            beforeState is null ? null : JsonSerializer.Serialize(beforeState, ItemOperationJson.Options),
            afterState is null ? null : JsonSerializer.Serialize(afterState, ItemOperationJson.Options)));
    }

    public void TouchCharacter(Guid characterId)
    {
        touchedCharacterIds.Add(characterId);
    }

    public void TouchContainer(Guid? containerId)
    {
        if (containerId is not null)
        {
            touchedContainerIds.Add(containerId.Value);
        }
    }

    public void IncludeResultItem(Guid itemInstanceId)
    {
        resultItemIds.Add(itemInstanceId);
    }

    public void AddRecoveryDelivery(Guid recoveryDeliveryId)
    {
        recoveryDeliveryIds.Add(recoveryDeliveryId);
    }

    // A named section of the result for a command above the kernel, which also reads it; a
    // later value of the same name replaces an earlier one. The value is serialized with
    // ItemOperationJson.Options when the result is built. The kernel never reads a section.
    public void SetSection<TValue>(string name, TValue value)
        where TValue : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        if (ReservedSectionNames.Contains(name))
        {
            throw new ArgumentException(
                $"The result section name '{name}' is a property of the item transaction result.",
                nameof(name));
        }

        sections[name] = new PendingSection(value, typeof(TValue));
    }

    // The sections in the order they were first set, or null without any.
    internal Dictionary<string, JsonElement>? BuildSections()
    {
        return sections.Count == 0
            ? null
            : sections.ToDictionary(
                pair => pair.Key,
                pair => JsonSerializer.SerializeToElement(
                    pair.Value.Value,
                    pair.Value.ValueType,
                    ItemOperationJson.Options),
                StringComparer.Ordinal);
    }

    public void AllowInvoluntaryDeathHardCapOverflow()
    {
        if (!string.Equals(
                OperationKind,
                ItemOperationKinds.ProcessPlayerDeath,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Only player death may permit involuntary carry overflow.");
        }

        allowInvoluntaryHardCapOverflow = true;
    }

    private sealed record ItemAuditState(
        Guid ItemInstanceId,
        string DefinitionId,
        int Quantity,
        long Revision,
        Guid? ContainerId,
        int? ContainerSlotIndex,
        Guid? EquippedCharacterId,
        string? EquipmentSlotId,
        IReadOnlyList<ItemPolicyAuditState> Policies);

    private sealed record ItemPolicyAuditState(
        string PolicyKind,
        string Status,
        string SourceKind,
        string SourceId,
        long Revision);

    private sealed record PendingSection(object Value, Type ValueType);
}

internal sealed record ItemAuditChange(
    string ChangeKind,
    Guid? ItemInstanceId,
    string? BeforeState,
    string? AfterState);
