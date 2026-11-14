using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Accounts.PasswordHashing;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Accounts.Tests;

public sealed class PasswordHashingBusyIntegrationTests
{
    // With the lane full, a login of a known account, a login of an unknown one, and a
    // registration are all refused with auth_busy and a retry time, and nothing is written.
    [PostgresIntegrationFact]
    public async Task AFullLaneRefusesLoginsAndRegistrationsWithAuthBusy()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        using var algorithm = new PasswordHashingLaneTests.GatedAlgorithm();
        using var metrics = new PasswordHashingMetrics();
        // A hash estimated at two seconds fits no wait of one second, so with the one thread
        // busy the lane refuses every further request.
        using var lane = PasswordHashingLaneTests.CreateLane(
            algorithm,
            metrics,
            1,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));
        var service = ActivatorUtilities.CreateInstance<AccountService>(context.Services, lane);
        var cancellationToken = TestContext.Current.CancellationToken;
        var running = lane.HashAsync("wait-running", cancellationToken);
        await PasswordHashingLaneTests.WaitUntilAsync(() => metrics.Capture().InFlight == 1);

        var known = await service.LoginAsync(
            new LoginAccountRequest("integration_player", "TestPass123!"),
            cancellationToken);
        var unknown = await service.LoginAsync(
            new LoginAccountRequest("nobody_here", "TestPass123!"),
            cancellationToken);
        var registration = await service.RegisterAsync(
            new RegisterAccountRequest("second@example.com", "second_player", "TestPass123!"),
            cancellationToken);

        foreach (var refused in new[] { known.StatusCode, unknown.StatusCode, registration.StatusCode })
        {
            Assert.Equal(503, refused);
        }

        Assert.Equal(AccountService.AuthBusyCode, known.Error?.Code);
        Assert.Equal(AccountService.AuthBusyCode, unknown.Error?.Code);
        Assert.Equal(AccountService.AuthBusyCode, registration.Error?.Code);
        Assert.Equal(TimeSpan.FromSeconds(1), known.RetryAfter);
        Assert.Equal(known.RetryAfter, unknown.RetryAfter);
        Assert.Equal(1, await context.ExecuteScalarIntAsync("select count(*) from accounts"));
        Assert.NotNull(await context.SessionService.AuthenticateTokenAsync(
            player.Registration.SessionToken,
            cancellationToken));
        Assert.Equal(3, metrics.Capture().Refusals);

        algorithm.Release();
        await running;
    }
}
