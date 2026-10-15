using System.Text.Json;

namespace Mmorpg.GameService.Items.Transactions;

// The in-memory change journal of one item transaction: audit changes, touched
// characters and containers, result items, Recovery deliveries, the Secure Container
// entitlement revision, and the death-only carry overflow permission. The kernel's
// finalizer turns it into revisions, audit rows, and the result.
internal sealed class ItemChangeJournal(
    ItemTransactionSession session,
    ItemStateReader reader)
{
    private readonly HashSet<Guid> touchedCharacterIds = [];
    private readonly HashSet<Guid> touchedContainerIds = [];
    private readonly HashSet<Guid> resultItemIds = [];
    private readonly List<ItemAuditChange> auditChanges = [];
    private readonly List<Guid> recoveryDeliveryIds = [];
    private long? secureContainerEntitlementRevision;
    private bool allowInvoluntaryHardCapOverflow;

    private string OperationKind => session.OperationKind;

    // Read-only views for the kernel's finalizer.
    internal IReadOnlyCollection<Guid> TouchedCharacterIds => touchedCharacterIds;

    internal IReadOnlyCollection<Guid> TouchedContainerIds => touchedContainerIds;

    internal IReadOnlyCollection<Guid> ResultItemIds => resultItemIds;

    internal IReadOnlyList<ItemAuditChange> AuditChanges => auditChanges;

    internal IReadOnlyList<Guid> RecoveryDeliveryIds => recoveryDeliveryIds;

    internal long? SecureContainerEntitlementRevision => secureContainerEntitlementRevision;

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

    public void SetSecureContainerEntitlementRevision(long revision)
    {
        secureContainerEntitlementRevision = revision;
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
}

internal sealed record ItemAuditChange(
    string ChangeKind,
    Guid? ItemInstanceId,
    string? BeforeState,
    string? AfterState);
