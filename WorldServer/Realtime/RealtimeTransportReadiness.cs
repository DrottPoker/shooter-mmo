namespace WorldServer.Realtime;

public sealed class RealtimeTransportReadiness
{
    private readonly TaskCompletionSource<int> listeningPort = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsListening => listeningPort.Task.IsCompletedSuccessfully;

    public void MarkListening(int port)
    {
        listeningPort.TrySetResult(port);
    }

    public Task<int> WaitUntilListeningAsync(CancellationToken cancellationToken)
    {
        return listeningPort.Task.WaitAsync(cancellationToken);
    }
}
