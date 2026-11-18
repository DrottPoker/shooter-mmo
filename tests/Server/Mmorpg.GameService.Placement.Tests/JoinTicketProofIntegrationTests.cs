using Microsoft.AspNetCore.Http;
using Mmorpg.GameProtocol;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Placement.Tests;

// A join ticket is a public id and a transport key. The id travels over UDP in clear; the key
// travels only in the HTTPS answers. GameService consumes a ticket only for a proof that the key
// makes, checked before anything else happens, so whoever saw the id can neither join with it nor
// burn it, and it hands the key to the worker once.
public sealed class JoinTicketProofIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";

    [PostgresIntegrationFact]
    public async Task AProofMadeWithAnotherKeyIsRefusedAndLeavesTheTicket()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var ticket = await TicketAsync(context, player.Account, player.Character.Id);

        var forged = await ConsumeAsync(context, JoinTicketProofs.Consume(
            JoinTicketProofs.JoinRequest(ticket.JoinTicketId, RealtimeTransportKeys.CreateTransportKey(), ShardId),
            WorkerId,
            RuntimeId,
            ShardId));

        Assert.Equal(StatusCodes.Status401Unauthorized, forged.StatusCode);
        Assert.Equal("invalid_ticket", forged.Error!.Code);
        Assert.Equal(1, await PendingTicketsAsync(context, ticket.JoinTicketId));
        var consumed = await ConsumeAsync(context, JoinTicketProofs.Consume(ticket, WorkerId, RuntimeId, ShardId));
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
    }

    [PostgresIntegrationFact]
    public async Task ATicketIdAloneJoinsNobody()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var ticket = await TicketAsync(context, player.Account, player.Character.Id);
        var request = JoinTicketProofs.Consume(ticket, WorkerId, RuntimeId, ShardId);

        var withoutProof = await ConsumeAsync(context, request with { Proof = null });
        var withAZeroProof = await ConsumeAsync(context, request with { Proof = new byte[RealtimeJoinProof.ProofSize] });
        var withAnotherNonce = await ConsumeAsync(context, request with { ClientNonce = RealtimeJoinProof.CreateNonce() });
        var forAnotherVersion = await ConsumeAsync(context, request with { ProtocolVersion = RealtimeProtocol.Version + 1 });

        Assert.Equal(StatusCodes.Status400BadRequest, withoutProof.StatusCode);
        Assert.Equal("invalid_ticket", withoutProof.Error!.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, withAZeroProof.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, withAnotherNonce.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, forAnotherVersion.StatusCode);
        Assert.Equal(1, await PendingTicketsAsync(context, ticket.JoinTicketId));
        Assert.True((await ConsumeAsync(context, request)).Succeeded);
    }

    [PostgresIntegrationFact]
    public async Task TheRightProofJoinsOnceAndHandsTheWorkerTheClientsKey()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var ticket = await TicketAsync(context, player.Account, player.Character.Id);
        Assert.Equal(RealtimeTransportKeys.TransportKeySize, ticket.TransportKey.Length);
        Assert.Equal(1, await StoredKeysAsync(context, ticket.JoinTicketId));
        var request = JoinTicketProofs.Consume(ticket, WorkerId, RuntimeId, ShardId);

        var consumed = await ConsumeAsync(context, request);
        var again = await ConsumeAsync(context, request);
        var freshProof = await ConsumeAsync(context, JoinTicketProofs.Consume(ticket, WorkerId, RuntimeId, ShardId));

        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        Assert.Equal(ticket.TransportKey, consumed.Value!.TransportKey);
        Assert.Equal(StatusCodes.Status401Unauthorized, again.StatusCode);
        Assert.Equal("invalid_ticket", again.Error!.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, freshProof.StatusCode);

        // A consumed ticket keeps no key.
        Assert.Equal(0, await StoredKeysAsync(context, ticket.JoinTicketId));
    }

    [PostgresIntegrationFact]
    public async Task AnExpiredTicketIsRefused()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var ticket = await TicketAsync(context, player.Account, player.Character.Id);
        await context.ExecuteAsync(
            $"update simulation_join_tickets set expires_at = clock_timestamp() - interval '1 second' where id = '{ticket.JoinTicketId}';");

        var consumed = await ConsumeAsync(context, JoinTicketProofs.Consume(ticket, WorkerId, RuntimeId, ShardId));

        Assert.Equal(StatusCodes.Status401Unauthorized, consumed.StatusCode);
        Assert.Equal("invalid_ticket", consumed.Error!.Code);
    }

    // A ticket from before transport keys has no key, so nothing proves it; tickets live 30
    // seconds, so none is left once the new version runs.
    [PostgresIntegrationFact]
    public async Task ATicketWithoutAKeyIsRefused()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var ticket = await TicketAsync(context, player.Account, player.Character.Id);
        await context.ExecuteAsync(
            $"update simulation_join_tickets set transport_key = null, ticket_hash = 'legacy' where id = '{ticket.JoinTicketId}';");

        var consumed = await ConsumeAsync(context, JoinTicketProofs.Consume(ticket, WorkerId, RuntimeId, ShardId));

        Assert.Equal(StatusCodes.Status401Unauthorized, consumed.StatusCode);
    }

    // A new ticket supersedes the account's pending one, whose key goes with it.
    [PostgresIntegrationFact]
    public async Task ASupersededTicketKeepsNoKey()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var first = await TicketAsync(context, player.Account, player.Character.Id);

        var second = await TicketAsync(context, player.Account, player.Character.Id);

        Assert.NotEqual(first.JoinTicketId, second.JoinTicketId);
        Assert.NotEqual(first.TransportKey, second.TransportKey);
        Assert.Equal(0, await StoredKeysAsync(context, first.JoinTicketId));
        Assert.Equal(StatusCodes.Status401Unauthorized, (await ConsumeAsync(context, JoinTicketProofs.Consume(first, WorkerId, RuntimeId, ShardId))).StatusCode);
        Assert.True((await ConsumeAsync(context, JoinTicketProofs.Consume(second, WorkerId, RuntimeId, ShardId))).Succeeded);
    }

    private static async Task<JoinShardResponse> TicketAsync(
        PostgresIntegrationTestContext context,
        AuthenticatedAccount account,
        Guid characterId)
    {
        var ticket = await context.ShardService.CreateJoinTicketAsync(
            account,
            ShardId,
            PostgresIntegrationTestContext.CreateJoinRequest(characterId),
            CancellationToken.None);
        Assert.True(ticket.Succeeded, ticket.Error?.Message);
        return ticket.Value!;
    }

    private static Task<ServiceResult<ConsumedSimulationJoinTicketResponse>> ConsumeAsync(
        PostgresIntegrationTestContext context,
        ConsumeSimulationJoinTicketRequest request)
    {
        return context.ShardService.ConsumeJoinTicketAsync(request, CancellationToken.None);
    }

    private static Task<int> PendingTicketsAsync(PostgresIntegrationTestContext context, Guid ticketId)
    {
        return context.ExecuteScalarIntAsync(
            $"select count(*)::integer from simulation_join_tickets where id = '{ticketId}' and consumed_at is null;");
    }

    private static Task<int> StoredKeysAsync(PostgresIntegrationTestContext context, Guid ticketId)
    {
        return context.ExecuteScalarIntAsync(
            $"select count(*)::integer from simulation_join_tickets where id = '{ticketId}' and octet_length(transport_key) = 32 and ticket_hash is null;");
    }
}
