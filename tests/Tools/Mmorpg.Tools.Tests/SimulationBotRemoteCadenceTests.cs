using System.Diagnostics;
using Mmorpg.Tools.SimulationBotClient;

namespace Mmorpg.Tools.Tests;

public sealed class SimulationBotRemoteCadenceTests
{
    private static readonly long Second = Stopwatch.Frequency;

    [Fact]
    public void APlayerCountsOnlyForTheTimeItWasSpawnedInsideTheWindow()
    {
        var cadence = new SimulationBotRemoteCadence();
        cadence.OnSpawn(1, 0);
        cadence.OnSpawn(2, 0);
        cadence.OnDespawn(2, Second / 2);
        cadence.BeginWindow(Second);
        cadence.OnSpawn(3, 2 * Second);
        for (uint frame = 1; frame <= 15; frame++)
        {
            var at = Second + (frame * Second / 15);
            cadence.OnRecord(1, frame, at);
            if (at >= 2 * Second)
            {
                cadence.OnRecord(3, frame, at);
            }
        }

        cadence.OnDespawn(3, 3 * Second);
        cadence.EndWindow(4 * Second);
        cadence.OnDespawn(1, 5 * Second);

        var summary = cadence.Capture()!;
        Assert.Equal(2, summary.Entities);
        var first = summary.PerEntity.Single(entity => entity.VisibleSeconds > 2);
        Assert.Equal(15, first.Updates);
        Assert.Equal(3d, first.VisibleSeconds, 6);
        var third = summary.PerEntity.Single(entity => entity.VisibleSeconds < 2);
        Assert.Equal(1d, third.VisibleSeconds, 6);
        Assert.Equal(1, third.Updates);
        Assert.Equal(16d / 4d, summary.UpdatesPerSecond!.Value, 6);
    }

    [Fact]
    public void TheBotsOwnEntityNeverCounts()
    {
        var cadence = new SimulationBotRemoteCadence();
        cadence.SetOwnEntity(7);
        cadence.OnSpawn(7, 0);
        cadence.BeginWindow(0);
        cadence.OnRecord(7, 1, 1);

        cadence.EndWindow(Second);

        var summary = cadence.Capture()!;
        Assert.Equal(0, summary.Entities);
        Assert.Equal(0, summary.UntrackedRecords);
    }

    [Fact]
    public void NothingIsMeasuredBeforeTheWindowEndsOrWithoutAnyRemotePlayer()
    {
        var cadence = new SimulationBotRemoteCadence();
        cadence.BeginWindow(0);
        Assert.Null(cadence.Capture());

        cadence.EndWindow(Second);

        var summary = cadence.Capture()!;
        Assert.Equal(0, summary.Entities);
        Assert.Null(summary.UpdatesPerSecond);
    }
}
