namespace ShooterMmo.Tools.StackStressGenerator;

public interface IStressAdmissionProvider : IAsyncDisposable
{
    Task<StressRunTarget> WaitForReadyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<StressAdmissionResult> AdmitAsync(
        int botIndex,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        IReadOnlyCollection<int> botIndexes,
        CancellationToken cancellationToken);

    Task SampleAsync(CancellationToken cancellationToken);

    void MarkSteadyState();

    StressProviderReport CaptureReport();
}

public sealed record StressProviderReport(
    StressWorkerRegistration? Worker,
    StressAuthorityCounters? Authority,
    StressFullStackSummary? FullStack,
    StressProcessSummary? AuthServiceProcess,
    StressPostgresSummary? Postgres);

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

    public void MarkSteadyState()
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
