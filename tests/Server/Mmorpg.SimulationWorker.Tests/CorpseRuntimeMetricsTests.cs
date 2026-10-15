namespace Mmorpg.SimulationWorker.Tests;

public sealed class CorpseRuntimeMetricsTests
{
    [Fact]
    public void CorpseMetricsSeparateLiveAndDurableWithoutIdentityLabels()
    {
        using var metrics = new SimulationWorker.Corpses.CorpseRuntimeMetrics();

        metrics.Observe(7, 19);

        var snapshot = metrics.Capture();
        Assert.Equal(7, snapshot.DurableCorpses);
        Assert.Equal(19, snapshot.LiveCorpses);
    }
}
