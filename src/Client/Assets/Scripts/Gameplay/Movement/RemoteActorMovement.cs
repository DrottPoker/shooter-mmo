using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Presents a remote actor that travels in the snapshots only while its pose changes and
    // reports the pose it comes to rest at reliably, such as a Mob. Snapshots move it through
    // the remote player presentation, with the same render clock and delay. A movement starts
    // from the reliable pose the actor stands at. A rest pose that arrives while snapshots
    // still play becomes their final state, so the actor settles without a jump. A rest pose
    // without recent snapshots, or a teleport, is the caller's to apply directly.
    public sealed class RemoteActorMovement
    {
        // A pose this far from the newest state is a teleport, such as an evading Mob that the
        // worker placed home, and is shown at once instead of sliding there.
        public const float TeleportDistance = 3f;

        // A rest pose this close to the newest state is the same pose.
        public const float SamePositionTolerance = 0.01f;
        public const float SameYawToleranceDegrees = 0.5f;

        private readonly RemoteMovementPresenter presenter = new RemoteMovementPresenter();
        private int snapshotIntervalTicks = 1;
        private bool resting;

        public bool IsConfigured { get; private set; }

        // Whether snapshots drive the pose; otherwise the reliable pose applies as it is.
        public bool IsDriven
        {
            get { return presenter.HasPose; }
        }

        // Whether the newest state is the reliable rest pose.
        public bool IsResting
        {
            get { return resting && presenter.HasPose; }
        }

        public int DelayTicks
        {
            get { return presenter.DelayTicks; }
        }

        // How many times the presented pose jumped, as in a teleport or a start from the rest pose.
        public int Discontinuities
        {
            get { return presenter.Discontinuities; }
        }

        public int SnapshotIntervalTicks
        {
            get { return snapshotIntervalTicks; }
        }

        public Vector3 Position
        {
            get { return presenter.Position; }
        }

        public float YawDegrees
        {
            get { return presenter.YawDegrees; }
        }

        public Quaternion Rotation
        {
            get { return presenter.Rotation; }
        }

        // Uses the joined session's tick and snapshot rates, collision, and character capsule,
        // because the client does not receive an actor's own capsule. Without a session no
        // snapshot moves the actor.
        public void ConfigureForSession(NetworkMovementSession session)
        {
            if (session == null)
            {
                IsConfigured = false;
                Clear();
                return;
            }

            Configure(
                session.Settings.TickRateHz,
                session.SnapshotRateHz,
                session.CollisionWorld,
                session.Settings.CharacterCollision);
        }

        public void Configure(
            int tickRateHz,
            int snapshotRateHz,
            ICollisionWorld collisionWorld,
            CharacterCollisionSettings collisionSettings)
        {
            var ticks = Mathf.Max(1, tickRateHz);
            snapshotIntervalTicks = Mathf.Max(
                1,
                Mathf.RoundToInt(ticks / (float)Mathf.Max(1, snapshotRateHz)));
            presenter.Configure(
                ticks,
                RemoteMovementPresenter.InterpolationDelayTicks(ticks, snapshotRateHz),
                collisionWorld,
                collisionSettings);
            resting = false;
            IsConfigured = true;
        }

        public void Clear()
        {
            presenter.Clear();
            resting = false;
        }

        // Buffers a snapshot state of the actor. restPosition and restYawDegrees are its
        // reliable pose, where a movement starts. Returns whether the state was accepted.
        public bool PushSnapshot(
            uint serverTick,
            RealtimePlayerState state,
            Vector3 restPosition,
            float restYawDegrees)
        {
            if (!IsConfigured || state == null)
            {
                return false;
            }

            var sample = RemoteMovementPresenter.ToMovementState(state);
            if (!presenter.HasPose
                || !presenter.TryGetNewestState(out var newestTick, out var newest))
            {
                StartFrom(RestState(restPosition, restYawDegrees), serverTick, sample);
                return true;
            }

            if (!MovementSequence.IsNewer(serverTick, newestTick))
            {
                return false;
            }

            if (Distance(newest, sample) > TeleportDistance)
            {
                StartAt(serverTick, sample);
                return true;
            }

            // The actor stood at its rest pose since the newest state; it starts again there.
            if (resting
                && presenter.IsPlayedOut
                && MovementSequence.IsNewer(unchecked(serverTick - StartTicks()), newestTick))
            {
                StartFrom(newest, serverTick, sample);
                return true;
            }

            presenter.Push(serverTick, sample);
            resting = false;
            return true;
        }

        // Applies the reliable pose the actor came to rest at. Returns true when the snapshots
        // keep presenting the actor and settle on that pose, or false when the caller applies
        // it directly: no snapshot moves the actor, the snapshots already played out at another
        // pose, or the pose is a teleport.
        public bool TrySettle(Vector3 position, float yawDegrees)
        {
            if (!presenter.HasPose
                || !presenter.TryGetNewestState(out var newestTick, out var newest))
            {
                return false;
            }

            var rest = RestState(position, yawDegrees);
            if (IsSamePose(newest, rest))
            {
                resting = true;
                return true;
            }

            if (presenter.IsPlayedOut || Distance(newest, rest) > TeleportDistance)
            {
                Clear();
                return false;
            }

            // The rest pose is the next state after the newest snapshot.
            presenter.Push(unchecked(newestTick + (uint)snapshotIntervalTicks), rest);
            resting = true;
            return true;
        }

        // Advances the render clock by one frame and returns whether it presented a pose. A
        // settled rest pose no longer changes, so it costs nothing until the next snapshot.
        public bool Advance(float unscaledDeltaTime, float deltaTime)
        {
            if (resting && presenter.IsSettled)
            {
                return false;
            }

            return presenter.Advance(unscaledDeltaTime, deltaTime);
        }

        // Shows the actor standing at the anchor and then walking into the snapshot, with the
        // render clock one delay behind it as for every remote entity.
        private void StartFrom(PlayerMovementState anchor, uint serverTick, PlayerMovementState sample)
        {
            if (Distance(anchor, sample) > TeleportDistance)
            {
                StartAt(serverTick, sample);
                return;
            }

            presenter.Clear();
            presenter.Push(
                unchecked(serverTick - StartTicks()),
                RestState(new Vector3(anchor.PositionX, anchor.PositionY, anchor.PositionZ), anchor.YawDegrees));
            presenter.Push(serverTick, sample);
            presenter.RestartClock();
            resting = false;
        }

        private void StartAt(uint serverTick, PlayerMovementState sample)
        {
            presenter.Clear();
            presenter.Push(serverTick, sample);
            resting = false;
        }

        // The anchor lies one render delay before the first snapshot, where the restarted
        // clock begins.
        private uint StartTicks()
        {
            return (uint)presenter.DelayTicks;
        }

        private static PlayerMovementState RestState(Vector3 position, float yawDegrees)
        {
            return new PlayerMovementState(
                position.x,
                position.y,
                position.z,
                0f,
                0f,
                0f,
                yawDegrees,
                true);
        }

        private static bool IsSamePose(PlayerMovementState first, PlayerMovementState second)
        {
            return Distance(first, second) <= SamePositionTolerance
                && Mathf.Abs(MovementStateInterpolation.DeltaAngle(first.YawDegrees, second.YawDegrees))
                    <= SameYawToleranceDegrees;
        }

        private static float Distance(PlayerMovementState first, PlayerMovementState second)
        {
            return Vector3.Distance(
                new Vector3(first.PositionX, first.PositionY, first.PositionZ),
                new Vector3(second.PositionX, second.PositionY, second.PositionZ));
        }
    }
}
