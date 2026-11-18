namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The join handshake timeout covers the wait for the join request, not a join that already started:
// that one waits for GameService, whose calls can take longer than the timeout together, and each of
// them is bounded by its own call timeout.
public sealed class RealtimeSlowJoinTests
{
    [Fact]
    public async Task AJoinThatWaitsForGameServiceLongerThanTheHandshakeTimeoutStillCompletes()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            JoinHandshakeTimeout = TimeSpan.FromMilliseconds(500)
        });
        var character = harness.AddCharacter("Slow Hero");
        // The consume takes three times the timeout, as a GameService behind a busy database can.
        harness.GameService.OnConsume = async (request, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1_500), cancellationToken);
            return harness.GameService.DefaultConsume(request);
        };
        await harness.StartAsync();

        var client = await harness.JoinAsync(character);

        Assert.NotNull(client.JoinAccepted);
        Assert.Null(client.ServerDisconnect);
        Assert.Null(client.JoinRejected);
        Assert.Single(harness.SessionStore.ListActiveSessions());
    }

    // A peer that sends no join request is still timed out, join started or not decided by its silence.
    [Fact]
    public async Task APeerThatSendsNoJoinRequestIsStillTimedOut()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            JoinHandshakeTimeout = TimeSpan.FromMilliseconds(500)
        });
        await harness.StartAsync();

        var client = harness.Connect("silent");

        await RealtimeProtocolRejectionTests.AssertRejectedAsync(harness, client, "join_timeout", beforeKeys: true);
    }
}
