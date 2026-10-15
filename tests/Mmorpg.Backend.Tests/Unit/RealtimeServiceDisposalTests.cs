using System.Diagnostics.Metrics;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.Backend.Tests.Unit;

public sealed class RealtimeServiceDisposalTests
{
    [Fact]
    public async Task DisposingTheServiceDisposesTheMetricsItCreated()
    {
        var capturing = false;
        var created = new List<Instrument>();
        var completed = new HashSet<Instrument>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (!capturing
                    || instrument.Meter.Name is not (RealtimeNetworkMetrics.MeterName
                        or RealtimePerformanceMetrics.MeterName))
                {
                    return;
                }

                created.Add(instrument);
                meterListener.EnableMeasurementEvents(instrument);
            },
            MeasurementsCompleted = (instrument, _) =>
            {
                lock (completed)
                {
                    completed.Add(instrument);
                }
            }
        };
        listener.Start();

        capturing = true;
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ProvideMetrics = false
        });
        capturing = false;
        Assert.Contains(created, instrument => instrument.Meter.Name == RealtimeNetworkMetrics.MeterName);
        Assert.Contains(created, instrument => instrument.Meter.Name == RealtimePerformanceMetrics.MeterName);

        harness.Service.Dispose();

        lock (completed)
        {
            Assert.All(created, instrument => Assert.Contains(instrument, completed));
        }
    }

    [Fact]
    public async Task DisposingTheServiceLeavesProvidedMetricsUsable()
    {
        await using var harness = RealtimeServerHarness.Create();

        harness.Service.Dispose();

        harness.NetworkMetrics!.RecordSent(10);
        Assert.Equal(1, harness.NetworkMetrics.Capture().SentPackets);
    }
}
