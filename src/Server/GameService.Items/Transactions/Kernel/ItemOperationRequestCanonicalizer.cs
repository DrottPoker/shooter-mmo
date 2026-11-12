using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mmorpg.GameService.Items.Transactions;

// Builds the canonical request payload of an item operation and its SHA-256 hash. Both
// are persisted for idempotent replay, so the property names, order, and sort rules are
// part of the stored format. The command's registration owns its sort rules.
internal static class ItemOperationRequestCanonicalizer
{
    public static CanonicalItemOperationRequest Canonicalize<TCommand>(
        ItemCommandRegistration<TCommand> registration,
        ItemTransactionActor actor,
        TCommand command)
        where TCommand : notnull
    {
        var requestPayload = JsonSerializer.Serialize(
            new CanonicalOperationRequest(
                CanonicalizeActor(actor),
                registration.Canonicalize(command)),
            ItemOperationJson.Options);
        var requestHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(requestPayload)));
        return new CanonicalItemOperationRequest(requestPayload, requestHash);
    }

    private static CanonicalOperationActor CanonicalizeActor(ItemTransactionActor actor)
    {
        return new CanonicalOperationActor(
            actor.Authority,
            actor.AccountId,
            actor.RequiresOfflineCharacter,
            actor.Simulation?.SimulationSessionId,
            actor.Simulation?.CharacterId,
            actor.Simulation?.WorkerId,
            actor.Simulation?.WorkerRuntimeId,
            actor.Simulation?.ShardId);
    }

    private sealed record CanonicalOperationRequest(
        CanonicalOperationActor Actor,
        object Command);

    private sealed record CanonicalOperationActor(
        ItemTransactionAuthority Authority,
        Guid? AccountId,
        bool RequiresOfflineCharacter,
        Guid? SimulationSessionId,
        Guid? CharacterId,
        string? WorkerId,
        string? WorkerRuntimeId,
        string? ShardId);
}

internal sealed record CanonicalItemOperationRequest(
    string Payload,
    string Hash);
