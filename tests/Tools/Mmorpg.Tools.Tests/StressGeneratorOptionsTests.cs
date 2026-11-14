using Mmorpg.Tools.StackStressGenerator;

namespace Mmorpg.Tools.Tests;

public sealed class StressGeneratorOptionsTests
{
    [Fact]
    public void DefaultsUseLoopbackAuthorityAndBoundedRamp()
    {
        var options = StressGeneratorOptions.Parse([], Directory.GetCurrentDirectory());

        Assert.Equal(StressRunMode.WorkerOnly, options.Mode);
        Assert.Equal(StressWorkloadProfile.Lifecycle, options.Workload);
        Assert.True(options.AuthorityUrl.IsLoopback);
        Assert.Equal(100, options.BotCount);
        Assert.Equal(25, options.RampStep);
        Assert.Equal("development-world-1", options.WorldId);
        Assert.Equal("local-shard-2", options.ShardId);
        Assert.Equal("local-simulation-worker-2", options.WorkerId);
        Assert.Equal(27016, options.WorkerUdpPort);
        Assert.Equal((0, 0, 0), (options.LootHotspotX, options.LootHotspotY, options.LootHotspotZ));
        Assert.Equal("Stress Bot 1", $"Stress Bot {options.BotStartIndex}");
        Assert.NotNull(options.WorkerSecret);
        Assert.True(options.WorkerSecret.Length >= 32);
    }

    [Fact]
    public void WorkerOnlyRejectsDurableGameplayWorkload()
    {
        var exception = Assert.Throws<StressGeneratorOptionException>(() =>
            StressGeneratorOptions.Parse(
                ["--mode", "worker-only", "--workload", "inventory"],
                Directory.GetCurrentDirectory()));

        Assert.Contains("full-stack", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsNonLoopbackAuthority()
    {
        var exception = Assert.Throws<StressGeneratorOptionException>(() =>
            StressGeneratorOptions.Parse(
                ["--authority-url", "http://192.0.2.1:5099"],
                Directory.GetCurrentDirectory()));

        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParsesExplicitStressShape()
    {
        var options = StressGeneratorOptions.Parse(
            [
                "--bots", "40",
                "--bot-start-index", "101",
                "--ramp-step", "10",
                "--duration-seconds", "30",
                "--worker-secret", "0123456789abcdef0123456789abcdef"
            ],
            Directory.GetCurrentDirectory());

        Assert.Equal(40, options.BotCount);
        Assert.Equal(101, options.BotStartIndex);
        Assert.Equal(10, options.RampStep);
        Assert.Equal(TimeSpan.FromSeconds(30), options.SteadyDuration);
    }

    // A burst is a login or a logout burst of the full stack, and it and its deadline fit the
    // steady interval.
    [Fact]
    public void ABurstNeedsTheFullStackAndFitsTheSteadyInterval()
    {
        string[] fullStack =
        [
            "--mode", "full-stack",
            "--postgres-connection-string", "Host=127.0.0.1;Database=shooter_mmo_stack_stress_test;Username=test;Password=test",
            "--confirm-disposable-database", "shooter_mmo_stack_stress_test",
            "--duration-seconds", "90"
        ];

        var login = StressGeneratorOptions.Parse(
            [.. fullStack, "--login-burst-accounts", "500", "--burst-at-seconds", "10", "--burst-deadline-seconds", "60"],
            Directory.GetCurrentDirectory());
        var logout = StressGeneratorOptions.Parse(
            [.. fullStack, "--logout-burst-accounts", "250"],
            Directory.GetCurrentDirectory());
        var none = StressGeneratorOptions.Parse(fullStack, Directory.GetCurrentDirectory());

        Assert.Equal(new StressBurstOptions(StressBurstKind.Login, 500, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)), login.Burst);
        Assert.Equal(new StressBurstOptions(StressBurstKind.Logout, 250, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)), logout.Burst);
        Assert.Null(none.Burst);
        AssertRefused([.. fullStack, "--login-burst-accounts", "5", "--logout-burst-accounts", "5"], "not both");
        AssertRefused([.. fullStack, "--login-burst-accounts", "5", "--burst-at-seconds", "40", "--burst-deadline-seconds", "60"], "does not fit");
        AssertRefused([.. fullStack, "--burst-at-seconds", "5"], "need --login-burst-accounts");
        AssertRefused(["--login-burst-accounts", "5"], "full-stack");

        static void AssertRefused(string[] arguments, string expected)
        {
            var exception = Assert.Throws<StressGeneratorOptionException>(() =>
                StressGeneratorOptions.Parse(arguments, Directory.GetCurrentDirectory()));
            Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FullStackRequiresDisposableDatabaseConfirmation()
    {
        var exception = Assert.Throws<StressGeneratorOptionException>(() =>
            StressGeneratorOptions.Parse(
                [
                    "--mode", "full-stack",
                    "--postgres-connection-string",
                    "Host=127.0.0.1;Database=shooter_mmo_stack_stress_test;Username=test;Password=test"
                ],
                Directory.GetCurrentDirectory()));

        Assert.Contains(
            "confirm-disposable-database",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParsesSafeFullStackMode()
    {
        var options = StressGeneratorOptions.Parse(
            [
                "--mode", "full-stack",
                "--postgres-connection-string",
                "Host=127.0.0.1;Database=shooter_mmo_stack_stress_test;Username=test;Password=test",
                "--confirm-disposable-database", "shooter_mmo_stack_stress_test",
                "--run-id", "smoke01",
                "--session-validation-requests-per-bot", "20"
            ],
            Directory.GetCurrentDirectory());

        Assert.Equal(StressRunMode.FullStack, options.Mode);
        Assert.Equal(new Uri("http://127.0.0.1:5000"), options.AuthorityUrl);
        Assert.Null(options.WorkerSecret);
        Assert.Equal("smoke01", options.RunId);
        Assert.Equal(20, options.SessionValidationRequestsPerBot);
    }

    [Fact]
    public void WorkerOnlyRejectsSessionValidationRequests()
    {
        var exception = Assert.Throws<StressGeneratorOptionException>(() =>
            StressGeneratorOptions.Parse(
                ["--session-validation-requests-per-bot", "1"],
                Directory.GetCurrentDirectory()));

        Assert.Contains("full-stack", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "Host=database.example;Database=shooter_mmo_stack_stress_test;Username=test;Password=test",
        "shooter_mmo_stack_stress_test",
        "loopback")]
    [InlineData(
        "Host=127.0.0.1;Database=shooter_mmo;Username=test;Password=test",
        "shooter_mmo",
        "stress")]
    [InlineData(
        "Host=127.0.0.1;Database=shooter_mmo_stack_stress_test;Username=test;Password=test",
        "different_database",
        "exactly match")]
    public void RejectsUnsafeFullStackDatabase(
        string connectionString,
        string confirmation,
        string expectedMessage)
    {
        var exception = Assert.Throws<StressGeneratorOptionException>(() =>
            StressPostgresSampler.ValidateConnectionString(
                connectionString,
                confirmation));

        Assert.Contains(
            expectedMessage,
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}
