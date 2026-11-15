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
        Assert.Equal(16d / 4d, summary.AllEntitiesUpdatesPerSecond!.Value, 6);
        Assert.Null(summary.UpdatesPerSecond);
    }

    // A player that walks near the bot counts its moving records over the time between them; a
    // record that does not move, the first one after a stand, and one beyond the near radius do
    // not.
    [Fact]
    public void ThePlayersThatWalkNearTheBotAreJudgedByTheGapsBetweenTheirRecords()
    {
        var cadence = new SimulationBotRemoteCadence();
        var own = (0f, 0f);
        cadence.OnSpawn(1, 0);
        cadence.OnSpawn(2, 0);
        cadence.BeginWindow(0);
        var frame = 0u;
        long At(uint value) => value * Second / 15;

        // Player 1 stands for a second, then walks for a second at full rate, missing one record.
        cadence.OnRecord(1, ++frame, At(frame), (10f, 0f), own);
        for (var standing = 0; standing < 15; standing++)
        {
            frame++;
        }

        cadence.OnRecord(1, frame, At(frame), (10f, 0f), own);
        var x = 10f;
        for (var walking = 0; walking < 15; walking++)
        {
            x += 0.33f;
            frame++;
            if (walking != 7)
            {
                cadence.OnRecord(1, frame, At(frame), (x, 0f), own);
            }
        }

        // Player 2 walks 60 m away, beyond the near radius.
        for (uint far = 1; far <= 15; far++)
        {
            cadence.OnRecord(2, far, At(far), (60f + (far * 0.33f), 0f), own);
        }

        cadence.EndWindow(4 * Second);

        var summary = cadence.Capture()!;
        var walker = summary.PerEntity.Single(entity => entity.NearMovingUpdates > 0);
        // 14 records walked; the first followed the stand, so 13 count over 14 frames.
        Assert.Equal(13, walker.NearMovingUpdates);
        Assert.Equal(14d / 15d, walker.NearMovingSeconds, 6);
        Assert.Equal(13d / (14d / 15d), summary.UpdatesPerSecond!.Value, 3);
        Assert.Equal(13, summary.NearMovingUpdates);
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
