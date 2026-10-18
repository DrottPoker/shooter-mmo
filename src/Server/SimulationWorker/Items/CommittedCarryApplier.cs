using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Items;

// The one path that applies an item state GameService committed: item, corpse, NPC
// lifecycle, gathering, ammunition, and death results and session heartbeats. Each caller
// decides what a conflict means.
public sealed class CommittedCarryApplier(
    CharacterItemStateStore itemStateStore,
    ActiveSimulationSessionStore sessionStore)
{
    // Applies the committed item state and mirrors the resulting carry state into the
    // active session, unless the store rejected it as conflicting or foreign.
    public ItemStateApplyResult Apply(
        ActiveSimulationSession session,
        CharacterItemState committed,
        out CharacterItemState current)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(committed);
        var result = itemStateStore.ApplyCommitted(
            session.CharacterId,
            session.SimulationSessionId,
            committed,
            out current);
        if (result is ItemStateApplyResult.Applied
            or ItemStateApplyResult.Unchanged
            or ItemStateApplyResult.Stale)
        {
            sessionStore.RefreshCarryState(
                session.CharacterId,
                session.SimulationSessionId,
                session.SimulationSessionToken,
                current.Carry);
        }

        return result;
    }

    // A committed transaction answers with the acting character's state after the commit,
    // which the GameService client already validated against the committed revision.
    public static bool TryGetCommittedState(
        SimulationItemTransactionResponse transaction,
        out CharacterItemState committed)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.CharacterState is null)
        {
            committed = null!;
            return false;
        }

        committed = CharacterItemState.From(transaction.CharacterState);
        return true;
    }

    public static bool IsDiverged(ItemStateApplyResult result)
    {
        return result is ItemStateApplyResult.Conflict or ItemStateApplyResult.SessionMismatch;
    }
}
