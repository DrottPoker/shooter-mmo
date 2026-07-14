namespace AuthService.Auth;

public static class AuthenticationConstants
{
    public const string AccountSessionScheme = "AccountSession";
    public const string SimulationWorkerScheme = "SimulationWorker";
    public const string AccountSessionPolicy = "AccountSession";
    public const string SimulationWorkerPolicy = "SimulationWorker";
    public const string AccountIdClaim = "account_id";
    public const string SessionIdClaim = "session_id";
    public const string SimulationWorkerIdClaim = "simulation_worker_id";
    public const string SimulationWorkerIdHeader = "X-Simulation-Worker-ID";
    public const string SimulationWorkerSecretHeader = "X-Simulation-Worker-Secret";
}
