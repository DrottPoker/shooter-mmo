using SimulationWorker.Corpses;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class CorpseMutationCoordinatorTests
{
    [Fact]
    public async Task SameCorpseMutationsAreSerializedAndGateIsReleased()
    {
        var coordinator = new CorpseMutationCoordinator();
        var corpseId = Guid.NewGuid();
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await coordinator.AcquireAsync(corpseId, cancellationToken);

        var secondTask = coordinator
            .AcquireAsync(corpseId, cancellationToken)
            .AsTask();

        Assert.False(secondTask.IsCompleted);
        Assert.Equal(1, coordinator.ActiveCorpseCount);
        Assert.Equal(1, coordinator.WaitingMutationCount);

        first.Dispose();
        var second = await secondTask.WaitAsync(
            TimeSpan.FromSeconds(1),
            cancellationToken);
        second.Dispose();

        Assert.Equal(0, coordinator.ActiveCorpseCount);
        Assert.Equal(0, coordinator.WaitingMutationCount);
    }

    [Fact]
    public async Task DifferentCorpsesCanMutateConcurrently()
    {
        var coordinator = new CorpseMutationCoordinator();
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await coordinator.AcquireAsync(
            Guid.NewGuid(),
            cancellationToken);

        var secondTask = coordinator
            .AcquireAsync(Guid.NewGuid(), cancellationToken)
            .AsTask();

        Assert.True(secondTask.IsCompletedSuccessfully);
        var second = await secondTask;
        Assert.Equal(2, coordinator.ActiveCorpseCount);

        second.Dispose();
        first.Dispose();
        Assert.Equal(0, coordinator.ActiveCorpseCount);
    }
}
