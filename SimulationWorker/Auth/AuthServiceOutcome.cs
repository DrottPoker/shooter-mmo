namespace SimulationWorker.Auth;

public static class AuthServiceOutcome
{
    // A timeout or lost connection can happen after AuthService committed the
    // operation, so the client must refresh instead of trusting a rejection.
    public static bool IsUncertain(string code)
    {
        return code is "auth_service_timeout" or "auth_service_unavailable";
    }
}
