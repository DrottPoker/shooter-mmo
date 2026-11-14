namespace Mmorpg.Tools.StackStressGenerator;

public interface IStressAdmissionProvider : IAsyncDisposable
{
    Task<StressRunTarget> WaitForReadyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task PrepareAsync(CancellationToken cancellationToken);

    Task<StressAdmissionResult> AdmitAsync(
        int botIndex,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        IReadOnlyCollection<int> botIndexes,
        CancellationToken cancellationToken);

    Task SampleAsync(CancellationToken cancellationToken);

    // The drift checks of one report interval, or null when the mode has no database.
    Task<StressDriftSample?> SampleDriftAsync(int joinedBots, CancellationToken cancellationToken);

    Task<FullStackCharacterInventoryResponse?> RefreshInventoryAsync(
        int botIndex,
        CancellationToken cancellationToken);

    void MarkSteadyState(TimeSpan plannedDuration);

    void MarkSteadyEnd();

    // Counts the database's tables and reads Redis at one end of the steady interval.
    Task CaptureSteadyBoundaryAsync(bool start, int players, CancellationToken cancellationToken);

    StressProviderReport CaptureReport();
}

public sealed record StressProviderReport(
    StressWorkerRegistration? Worker,
    StressAuthorityCounters? Authority,
    StressFullStackSummary? FullStack,
    StressProcessSummary? GameServiceProcess,
    StressPostgresSummary? Postgres)
{
    // How the database and Redis grew over the steady interval, and the worst drift values.
    public StressStorageGrowthSummary? Storage { get; init; }

    public StressDriftSummary? Drift { get; init; }
}

public sealed class WorkerOnlyStressAdmissionProvider(
    StressGeneratorOptions options,
    StressAuthorityState authority) : IStressAdmissionProvider
{
    public async Task<StressRunTarget> WaitForReadyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var worker = await authority.WaitForWorkerAsync(timeout, cancellationToken);
        return new StressRunTarget(
            worker.ShardId,
            worker.WorldId,
            worker.MaxConnections,
            worker);
    }

    public Task<StressAdmissionResult> AdmitAsync(
        int botIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ticket = authority.IssueTicket(botIndex);
        var admission = new StressBotAdmission(
            ticket.BotIndex,
            ticket.AccountId,
            ticket.CharacterId,
            ticket.CharacterName,
            ticket.Ticket,
            ticket.ExpiresAt,
            options.WorkerHost,
            options.WorkerUdpPort,
            options.ShardId,
            options.WorldId);
        return Task.FromResult(StressAdmissionResult.Success(admission));
    }

    public Task PrepareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task CompleteAsync(
        IReadOnlyCollection<int> botIndexes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<StressDriftSample?> SampleDriftAsync(int joinedBots, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<StressDriftSample?>(null);
    }

    public Task CaptureSteadyBoundaryAsync(bool start, int players, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void MarkSteadyEnd()
    {
    }

    public Task<FullStackCharacterInventoryResponse?> RefreshInventoryAsync(
        int botIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<FullStackCharacterInventoryResponse?>(null);
    }

    public void MarkSteadyState(TimeSpan plannedDuration)
    {
    }

    public StressProviderReport CaptureReport()
    {
        return new StressProviderReport(
            authority.CaptureWorkerRegistration(),
            authority.CaptureCounters(),
            null,
            null,
            null);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
