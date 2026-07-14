namespace SimulationWorker.Registry;

public sealed record SimulationWorkerIdentity(string RuntimeId, DateTime StartedAt)
{
    public static SimulationWorkerIdentity Create()
    {
        return new SimulationWorkerIdentity(
            Guid.NewGuid().ToString("N"),
            DateTime.UtcNow);
    }
}
