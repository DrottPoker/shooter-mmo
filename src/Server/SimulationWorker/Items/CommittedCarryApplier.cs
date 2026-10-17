using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Items;

// The one path that applies a carry state GameService committed: item, corpse, and NPC
// lifecycle results and session heartbeats. Each caller decides what a conflict means.
public sealed class CommittedCarryApplier(
    CarryStateStore carryStateStore,
    ActiveSimulationSessionStore sessionStore)
{
    // Applies the committed carry state and mirrors the resulting current state into the
    // active session, unless the carry store rejected it as conflicting or foreign.
    public CarryStateApplyResult Apply(
        ActiveSimulationSession session,
        PlayerCarryState committed,
        out PlayerCarryState current)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(committed);
        var result = carryStateStore.ApplyCommitted(
            session.CharacterId,
            session.SimulationSessionId,
            committed,
            out current);
        if (result is CarryStateApplyResult.Applied
            or CarryStateApplyResult.Unchanged
            or CarryStateApplyResult.Stale)
        {
            sessionStore.RefreshCarryState(
                session.CharacterId,
                session.SimulationSessionId,
                session.SimulationSessionToken,
                current);
        }

        return result;
    }

    // An item transaction for one character commits exactly one character revision.
    public static bool TryGetCommittedCarry(
        SimulationItemTransactionResponse transaction,
        out PlayerCarryState committed)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.CharacterRevisions.Count != 1)
        {
            committed = PlayerCarryState.Default;
            return false;
        }

        var revision = transaction.CharacterRevisions[0];
        committed = new PlayerCarryState(
            revision.Revision,
            revision.CarriedWeight,
            revision.CarryCapacity);
        return true;
    }

    public static bool IsDiverged(CarryStateApplyResult result)
    {
        return result is CarryStateApplyResult.Conflict or CarryStateApplyResult.SessionMismatch;
    }
}
