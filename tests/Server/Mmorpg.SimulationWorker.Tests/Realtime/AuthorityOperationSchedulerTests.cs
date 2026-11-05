using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A reconnect hands a session's lane to the new peer while an operation still runs on the old
// peer's lane: the new lane opens when that operation ends, also across a second reconnect.
public sealed class AuthorityOperationSchedulerTests
{
    [Fact]
    public void ALaneThatAReconnectHandedOnOpensWhenTheOldOperationEnds()
    {
        using var fixture = new RealtimeLoopFixture();
        var old = fixture.ConnectPlayer(1, "Old Client").Context;
        var middle = fixture.ConnectPlayer(2, "Middle Client").Context;
        var latest = fixture.ConnectPlayer(3, "Latest Client").Context;
        var started = new List<string>();
        fixture.Scheduler.Enqueue(old, new RecordingOperation("old", started));
        Assert.True(old.AuthorityOperationInFlight);

        AuthorityOperationScheduler.HandOver(old, middle);
        AuthorityOperationScheduler.HandOver(middle, latest);
        fixture.Scheduler.Enqueue(latest, new RecordingOperation("waiting", started));
        Assert.Equal(["old"], started);

        fixture.Scheduler.Release(old);

        Assert.Equal(["old", "waiting"], started);
        Assert.Null(old.LaneSuccessor);
        Assert.Null(middle.LaneSuccessor);
        Assert.False(middle.AuthorityOperationInFlight);
        Assert.True(latest.AuthorityOperationInFlight);
    }

    private sealed record RecordingOperation(string Name, List<string> Started) : IRealtimeAuthorityOperation
    {
        public void Start(RealtimePeerContext context, PlayerSimulationEntity entity, PlayerMovementState state)
        {
            Started.Add(Name);
        }
    }
}
