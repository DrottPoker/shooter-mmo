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
    // reconnect through a fresh join. A worker that runs another quest catalog than GameService
    // can host no quest progress until it runs the same content.
    public static bool EndsSession(string code)
    {
        return code is "worker_runtime_changed"
            or "simulation_session_invalid"
            or "auth_service_authentication_failed"
            or "invalid_auth_response"
            or "simulation_worker_quest_catalog_mismatch";
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

    // The item kernel rolled the transaction back because of concurrent database work, such as a
    // lock timeout or a deadlock, and released the operation, so the same operation may run again.
    public static bool IsRolledBack(string code)
    {
        return code is "item_transaction_timeout";
    }

    // A failure that asking again with the same operation id may get past: one that may have
    // committed, and one that rolled back. A retry of a committed operation replays its result.
    public static bool IsRetryable(string code, int statusCode)
    {
        return IsTransient(code, statusCode) || IsRolledBack(code);
    }
}
