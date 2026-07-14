using ShooterMmo.Tools.SimulationStressGenerator;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class StressGeneratorOptionsTests
{
    [Fact]
    public void DefaultsUseLoopbackAuthorityAndBoundedRamp()
    {
        var options = StressGeneratorOptions.Parse([], Directory.GetCurrentDirectory());

        Assert.True(options.AuthorityUrl.IsLoopback);
        Assert.Equal(100, options.BotCount);
        Assert.Equal(25, options.RampStep);
        Assert.Equal("Stress Bot 1", $"Stress Bot {options.BotStartIndex}");
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
}
