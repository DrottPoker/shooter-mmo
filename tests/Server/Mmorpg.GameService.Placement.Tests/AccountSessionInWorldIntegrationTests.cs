using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Placement.Tests;

// A player in the World makes no requests of its own, so the session heartbeat counts the play as
// activity of the account session: it moves the idle end at most once an hour. And a heartbeat that
// waited for a new login reads what the login committed, so it names the replacement.
public sealed class AccountSessionInWorldIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";

    private static readonly TimeSpan MustFinishWithin = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MustStillWaitAfter = TimeSpan.FromSeconds(1.5);

    [PostgresIntegrationFact]
    public async Task TheHeartbeatMovesTheIdleEndOfAPlayerInTheWorldAtMostOnceAnHour()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = await JoinAsync(context, player);
        var writes = new WriteCounter(context, player.Account.SessionId);

        // Thirteen days without a request, but in the World: past its idle end.
        await writes.SetAsync("created_at = now() - interval '20 days', expires_at = now() - interval '1 hour'");
        await HeartbeatAsync(context, session);
        await writes.CountAsync();
        Assert.InRange(await IdleShortfallMinutesAsync(context, player.Account.SessionId), -1, 1);
        for (var index = 0; index < 5; index++)
        {
            await HeartbeatAsync(context, session);
            await writes.CountAsync();
        }

        Assert.Equal(1, writes.Count);

        await writes.SetAsync("expires_at = now() + interval '14 days' - interval '59 minutes'");
        await HeartbeatAsync(context, session);
        await writes.CountAsync();
        Assert.Equal(1, writes.Count);

        await writes.SetAsync("expires_at = now() + interval '14 days' - interval '61 minutes'");
        await HeartbeatAsync(context, session);
        await writes.CountAsync();
        await HeartbeatAsync(context, session);
        await writes.CountAsync();
        Assert.Equal(2, writes.Count);
    }

    // The new login holds the account, its account sessions, and the simulation session it marked
    // while the heartbeat waits. The heartbeat reads the end after its lock, so it names the
    // replacement, not just an end.
    [PostgresIntegrationFact]
    public async Task AHeartbeatThatWaitedForANewLoginNamesTheReplacement()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = await JoinAsync(context, player);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        Task<SimulationSessionHeartbeatResult> heartbeat;
        await using (var login = await connection.BeginTransactionAsync())
        {
            await context.SessionService.ReplaceSessionAsync(
                connection,
                login,
                player.Registration.AccountId,
                CancellationToken.None);
            heartbeat = context.HeartbeatSessionAsync(
                session.SimulationSessionId,
                session.SimulationSessionToken,
                WorkerId,
                RuntimeId);
            var first = await Task.WhenAny(heartbeat, Task.Delay(MustStillWaitAfter));
            Assert.NotSame(heartbeat, first);
            await login.CommitAsync();
        }

        var lease = (await heartbeat.WaitAsync(MustFinishWithin)).Lease;

        Assert.NotNull(lease);
        Assert.Equal(AccountSessionErrorCode.SessionReplaced, lease.AccountSessionEnded);
    }

    private static async Task HeartbeatAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session)
    {
        var heartbeat = await context.HeartbeatSessionAsync(
            session.SimulationSessionId,
            session.SimulationSessionToken,
            WorkerId,
            RuntimeId);
        Assert.NotNull(heartbeat.Lease);
        Assert.Null(heartbeat.Lease.AccountSessionEnded);
    }

    // How many minutes the stored end lies short of the idle lifetime from now.
    private static async Task<int> IdleShortfallMinutesAsync(PostgresIntegrationTestContext context, Guid accountSessionId)
    {
        return await context.ExecuteScalarIntAsync(
            $"""
            select round(extract(epoch from now() + interval '14 days' - expires_at) / 60)::integer
            from account_sessions
            where id = '{accountSessionId}';
            """);
    }

    private static async Task<ConsumedSimulationJoinTicketResponse> JoinAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            JoinTicketProofs.Consume(join.Value!, WorkerId, RuntimeId, ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }

    // Counts the writes of an account session row by its row version, which every update changes.
    private sealed class WriteCounter(PostgresIntegrationTestContext context, Guid accountSessionId)
    {
        private string? version;

        public int Count { get; private set; }

        // Changes the row as a test step, which is not counted.
        public async Task SetAsync(string assignments)
        {
            await context.ExecuteAsync($"update account_sessions set {assignments} where id = '{accountSessionId}';");
            version = await ReadAsync();
        }

        public async Task CountAsync()
        {
            var current = await ReadAsync();
            if (!string.Equals(current, version, StringComparison.Ordinal))
            {
                Count++;
                version = current;
            }
        }

        private Task<string?> ReadAsync()
        {
            return context.ExecuteScalarStringAsync(
                $"select xmin::text from account_sessions where id = '{accountSessionId}';");
        }
    }
}
