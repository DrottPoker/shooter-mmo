using ShooterMmo.Tools.StackStressGenerator;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class StressGeneratorOptionsTests
{
    [Fact]
    public void DefaultsUseLoopbackAuthorityAndBoundedRamp()
    {
        var options = StressGeneratorOptions.Parse([], Directory.GetCurrentDirectory());

        Assert.Equal(StressRunMode.WorkerOnly, options.Mode);
        Assert.True(options.AuthorityUrl.IsLoopback);
        Assert.Equal(100, options.BotCount);
        Assert.Equal(25, options.RampStep);
        Assert.Equal("development-world-2", options.WorldId);
        Assert.Equal("Stress Bot 1", $"Stress Bot {options.BotStartIndex}");
        Assert.NotNull(options.WorkerSecret);
        Assert.True(options.WorkerSecret.Length >= 32);
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
                "--run-id", "smoke01"
            ],
            Directory.GetCurrentDirectory());

        Assert.Equal(StressRunMode.FullStack, options.Mode);
        Assert.Equal(new Uri("http://127.0.0.1:5000"), options.AuthorityUrl);
        Assert.Null(options.WorkerSecret);
        Assert.Equal("smoke01", options.RunId);
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
