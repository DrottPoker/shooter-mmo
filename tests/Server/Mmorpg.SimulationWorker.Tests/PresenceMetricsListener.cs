using System.Diagnostics.Metrics;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Tests;

// Reads what a PresenceMetrics counted: its counters by the value of their one label, and its
// gauges when asked. Listens from construction to disposal.
internal sealed class PresenceMetricsListener : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<(string Instrument, string Label), long> counts = [];
    private readonly Dictionary<string, double> gauges = [];
    private readonly MeterListener listener;

    public PresenceMetricsListener()
    {
        listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PresenceMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var label = tags.Length == 0 ? string.Empty : (string)tags[0].Value!;
            lock (sync)
            {
                counts[(instrument.Name, label)] = counts.GetValueOrDefault((instrument.Name, label)) + value;
            }
        });
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
        {
            lock (sync)
            {
                gauges[instrument.Name] = value;
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            lock (sync)
            {
                gauges[instrument.Name] = value;
            }
        });
        listener.Start();
    }

    // The sum of a counter's measurements that carry the label value.
    public long Of(string instrument, string label)
    {
        lock (sync)
        {
            return counts.GetValueOrDefault((instrument, label));
        }
    }

    // The latest value of a gauge, or null when it reported nothing.
    public double? Gauge(string instrument)
    {
        lock (sync)
        {
            gauges.Remove(instrument);
        }

        listener.RecordObservableInstruments();
        lock (sync)
        {
            return gauges.TryGetValue(instrument, out var value) ? value : null;
        }
    }

    public void Dispose()
    {
        listener.Dispose();
    }
}
