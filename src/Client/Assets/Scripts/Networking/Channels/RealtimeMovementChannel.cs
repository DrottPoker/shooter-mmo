using System;
using Mmorpg.Client.Core;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;

namespace Mmorpg.Client.Networking
{
    // The movement of the joined session: the snapshots, the carry state, the movement session,
    // and the movement input.
    public interface IRealtimeMovementChannel
    {
        // A chunk of the remote entity states, each one raised while joined.
        event Action<RealtimeSimulationSnapshot> SimulationSnapshotReceived;

        // The controlled entity's state, raised unless its server tick is older than the newest.
        event Action<RealtimeOwnerSimulationSnapshot> OwnerSimulationSnapshotReceived;

        // Raised after a newer carry state advanced the movement session, before the event of
        // the message that carried it.
        event Action<PlayerCarryState> CarryStateChanged;

        // The movement session of the joined session, or null outside it.
        NetworkMovementSession MovementSession { get; }

        // Sends the newest inputs, plus the path of the newest input when the worker has not
        // confirmed it yet. The path travels with the inputs that name it, so it can never
        // arrive after them.
        bool TrySendMovementInputs(PlayerMovementInput[] inputs, MovementPath unconfirmedPath);
    }

    internal sealed class RealtimeMovementChannel : IRealtimeMovementChannel
    {
        private readonly RealtimeSession session;
        private readonly RealtimeDiagnostics diagnostics;
        private readonly RealtimeSender sender;

        public RealtimeMovementChannel(
            RealtimeSession session,
            RealtimeDiagnostics diagnostics,
            RealtimeSender sender,
            RealtimeMessageTable messages)
        {
            this.session = session;
            this.diagnostics = diagnostics;
            this.sender = sender;
            // Snapshots that overtook the join or are in flight around a leave are dropped, as
            // LiteNetLib delivery methods do not order them against the reliable messages.
            messages.Register(
                RealtimeMessageType.SimulationSnapshot,
                HandleSimulationSnapshot,
                RealtimeStates.Joined,
                RealtimeStates.NotJoined,
                null,
                null);
            messages.Register(
                RealtimeMessageType.OwnerSimulationSnapshot,
                HandleOwnerSimulationSnapshot,
                RealtimeStates.Joined,
                RealtimeStates.NotJoined,
                null,
                null);
            // A carry state still advances the movement session while leaving.
            messages.Register(
                RealtimeMessageType.CarryStateChanged,
                HandleCarryStateChanged,
                RealtimeStates.Joined | RealtimeStates.Leaving,
                RealtimeStates.None,
                "unexpected_carry_state",
                "Carry state arrived without an active movement session.");
        }

        public event Action<RealtimeSimulationSnapshot> SimulationSnapshotReceived;

        public event Action<RealtimeOwnerSimulationSnapshot> OwnerSimulationSnapshotReceived;

        public event Action<PlayerCarryState> CarryStateChanged;

        public NetworkMovementSession MovementSession
        {
            get { return session.MovementSession; }
        }

        public bool TrySendMovementInputs(PlayerMovementInput[] inputs, MovementPath unconfirmedPath)
        {
            if (!sender.CanSend
                || inputs == null
                || inputs.Length == 0
                || inputs.Length > RealtimeProtocol.MaximumInputBatchSize)
            {
                return false;
            }

            var protocolInputs = new RealtimeMovementInput[inputs.Length];
            for (var index = 0; index < inputs.Length; index++)
            {
                var input = inputs[index];
                protocolInputs[index] = new RealtimeMovementInput(
                    input.InputSequence,
                    input.ClientTick,
                    input.PathId);
            }

            try
            {
                sender.SendMovementInput(RealtimeProtocol.EncodeMovementInputBatch(
                    new RealtimeMovementInputBatch(protocolInputs, ToRealtimePath(unconfirmedPath))));
                return true;
            }
            catch (ArgumentException exception)
            {
                ClientLog.Error(
                    ClientLogCategory.Client,
                    "Movement input could not be encoded: " + exception.Message);
                return false;
            }
        }

        // Applies the carry state of a message to the movement session and raises
        // CarryStateChanged when it is newer, so the message's own event comes after it.
        internal bool TryAdvanceCarryState(RealtimeCarryState carryState, out string error)
        {
            var movementSession = session.MovementSession;
            if (!movementSession.TryApplyCarryState(carryState, out var changed, out error))
            {
                return false;
            }

            if (changed)
            {
                CarryStateChanged?.Invoke(movementSession.CarryState);
            }

            return true;
        }

        private void HandleSimulationSnapshot(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeSimulationSnapshot(packet, out var snapshot, out var error))
            {
                session.FailProtocol("invalid_simulation_snapshot", error);
                return;
            }

            diagnostics.RecordRemoteSnapshot();
            SimulationSnapshotReceived?.Invoke(snapshot);
        }

        private void HandleOwnerSimulationSnapshot(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeOwnerSimulationSnapshot(
                    packet,
                    out var snapshot,
                    out var error))
            {
                session.FailProtocol("invalid_owner_simulation_snapshot", error);
                return;
            }

            if (diagnostics.RecordOwnerSnapshot(snapshot.SnapshotSequence, snapshot.ServerTick))
            {
                OwnerSimulationSnapshotReceived?.Invoke(snapshot);
            }
        }

        // While leaving, the carry state advances without its event.
        private void HandleCarryStateChanged(byte[] packet)
        {
            var movementSession = session.MovementSession;
            if (!RealtimeProtocol.TryDecodeCarryStateChanged(
                    packet,
                    out var carryState,
                    out var error)
                || !movementSession.TryApplyCarryState(
                    carryState,
                    out var changed,
                    out error))
            {
                session.FailProtocol("invalid_carry_state", error);
                return;
            }

            if (changed && session.State == RealtimeConnectionState.Joined)
            {
                CarryStateChanged?.Invoke(movementSession.CarryState);
            }
        }

        private static RealtimeMovementPath ToRealtimePath(MovementPath path)
        {
            if (path == null)
            {
                return null;
            }

            var waypoints = new RealtimeMovementPathWaypoint[path.Count];
            for (var index = 0; index < waypoints.Length; index++)
            {
                var waypoint = path[index];
                waypoints[index] = new RealtimeMovementPathWaypoint(waypoint.X, waypoint.Y, waypoint.Z);
            }

            return new RealtimeMovementPath(path.Id, waypoints);
        }
    }
}
