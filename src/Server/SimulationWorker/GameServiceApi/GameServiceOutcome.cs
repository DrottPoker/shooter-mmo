namespace Mmorpg.SimulationWorker.GameServiceApi;

public static class GameServiceOutcome
{
    // A timeout or lost connection can happen after GameService committed the
    // operation, so the client must refresh instead of trusting a rejection.
    public static bool IsUncertain(string code)
    {
        return code is "auth_service_timeout" or "auth_service_unavailable";
    }

    // The worker no longer holds a valid session or runtime for the player, who must
    // reconnect through a fresh join.
    public static bool EndsSession(string code)
    {
        return code is "worker_runtime_changed"
            or "simulation_session_invalid"
            or "auth_service_authentication_failed"
            or "invalid_auth_response";
    }

    // Item and NPC lifecycle operations act for the session, so a worker mismatch also
    // ends it. A corpse operation reports the same code when the corpse belongs to another
    // Shard, which closes only the corpse view.
    public static bool EndsItemSession(string code)
    {
        return EndsSession(code) || code is "wrong_simulation_worker";
    }

    // An item transaction may have committed before its answer was lost or failed: a timeout, a
    // lost connection, or a server error such as a failed read of the committed state. An answer
    // that ends the session is final.
    public static bool MayHaveCommitted(string code, int statusCode)
    {
        return IsTransient(code, statusCode);
    }

    // A failure that the same request may get past: a timeout, a lost connection, or a server
    // error. An answer that ends the session is final.
    public static bool IsTransient(string code, int statusCode)
    {
        return !EndsItemSession(code) && (IsUncertain(code) || statusCode >= 500);
    }
}
