using Mmorpg.Client.Networking;
using Mmorpg.GameSimulation;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Presents a remote actor that reports the pose it comes to rest at reliably, such as a
    // Mob. Snapshots move it through the remote player presentation, with the same render clock
    // and delay. A movement starts from the reliable pose the actor stands at, and a snapshot
    // record at the pose it stands at changes nothing, so an actor that stands costs nothing.
    // An actor that walked when it came into view starts from that pose instead as a remote
    // player does, from a first state that says nothing about standing. An actor whose records
    // were lost before it was presented, such as while its prefab loaded, starts at its first
    // snapshot instead of sliding from a pose that it left long ago. A rest pose that arrives
    // while snapshots play, or after they played out, becomes their final state, so the actor
    // settles without a jump. A rest pose without any snapshot, or a teleport, is the caller's
    // to apply directly.
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

        // The only state is the pose of a spawn that walked, and no snapshot followed it yet.
        private bool walkingSpawnUnconfirmed;

        // Records were lost before the actor was presented, so its rest pose may be stale.
        private bool restPoseStale;

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

        // Whether snapshots moved the actor and it ran out of them while it still moved; an
        // actor at its rest pose is not starved.
        public bool IsStarved
        {
            get { return !resting && presenter.IsStarved; }
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
            ForgetStart();
            IsConfigured = true;
        }

        public void Clear()
        {
            presenter.Clear();
            resting = false;
            ForgetStart();
        }

        // Starts the actor at a pose it walked through at serverTick, such as the pose of a Mob
        // that came into view while it walked. The pose is a first state that says nothing about
        // standing, so the actor walked the whole gap to a moving snapshot that follows it
        // within the longest render delay, as a remote player that came into view does. A first
        // snapshot later than that, such as the first after a slow prefab load, starts the
        // movement at itself instead. Returns false, and starts nothing, without snapshot
        // movement.
        public bool StartMovingFrom(uint serverTick, Vector3 position, float yawDegrees)
        {
            if (!IsConfigured)
            {
                return false;
            }

            presenter.Clear();
            presenter.Push(serverTick, PoseState(position, yawDegrees));
            resting = false;
            restPoseStale = false;
            walkingSpawnUnconfirmed = true;
            return true;
        }

        // Records of the actor were lost before it was presented, such as while its prefab
        // loaded, so it may have walked off its rest pose meanwhile: the next snapshot away from
        // that pose starts the movement at itself, a first state that says nothing about
        // standing, instead of from the rest pose. A snapshot at the rest pose, or a newer rest
        // pose, makes the rest pose current again.
        public void StartAtNextSnapshot()
        {
            if (!presenter.HasPose)
            {
                restPoseStale = true;
            }
        }

        // Buffers a snapshot state of the actor. restPosition and restYawDegrees are its
        // reliable pose, where a movement starts. Returns whether the state was accepted.
        public bool PushSnapshot(
            uint serverTick,
            PlayerMovementState sample,
            Vector3 restPosition,
            float restYawDegrees)
        {
            if (!IsConfigured)
            {
                return false;
            }

            if (!presenter.HasPose
                || !presenter.TryGetNewestState(out var newestTick, out var newest))
            {
                // A record of the actor where it stands, such as a refresh, starts nothing.
                var rest = PoseState(restPosition, restYawDegrees);
                if (IsSamePose(rest, sample))
                {
                    restPoseStale = false;
                    return false;
                }

                // The actor left a rest pose whose records were lost: it is shown where it is.
                if (restPoseStale)
                {
                    StartAt(serverTick, sample);
                    return true;
                }

                StartFrom(rest, serverTick, sample);
                return true;
            }

            if (!MovementSequence.IsNewer(serverTick, newestTick))
            {
                return false;
            }

            // The first snapshot after a spawn that walked comes later than a walk from it could
            // have lasted, such as after a slow prefab load: the actor is shown where it is now
            // instead of sliding the gap.
            if (walkingSpawnUnconfirmed)
            {
                walkingSpawnUnconfirmed = false;
                if (unchecked(serverTick - newestTick) > presenter.LongestWalkGapTicks)
                {
                    StartAt(serverTick, sample);
                    return true;
                }
            }

            // The actor stands where it rested; the record says so again.
            if (resting && IsSamePose(newest, sample))
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
        // it directly: no snapshot moves the actor, or the pose is a teleport.
        public bool TrySettle(Vector3 position, float yawDegrees)
        {
            if (!presenter.HasPose
                || !presenter.TryGetNewestState(out var newestTick, out var newest))
            {
                // The caller applies the newer rest pose, which is current.
                restPoseStale = false;
                return false;
            }

            walkingSpawnUnconfirmed = false;

            var rest = PoseState(position, yawDegrees);
            if (Distance(newest, rest) > TeleportDistance)
            {
                Clear();
                return false;
            }

            // The rest pose is the next state after the newest snapshot, and the one after that
            // repeats it: the actor stands there, so nothing walks on past it.
            var restTick = unchecked(newestTick + (uint)snapshotIntervalTicks);
            if (!IsSamePose(newest, rest))
            {
                presenter.Push(restTick, rest);
                restTick = unchecked(restTick + (uint)snapshotIntervalTicks);
            }

            presenter.Push(restTick, rest);
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
            presenter.PushStanding(
                unchecked(serverTick - StartTicks()),
                PoseState(new Vector3(anchor.PositionX, anchor.PositionY, anchor.PositionZ), anchor.YawDegrees));
            presenter.Push(serverTick, sample);
            presenter.RestartClock();
            resting = false;
            ForgetStart();
        }

        private void StartAt(uint serverTick, PlayerMovementState sample)
        {
            presenter.Clear();
            presenter.Push(serverTick, sample);
            resting = false;
            ForgetStart();
        }

        private void ForgetStart()
        {
            walkingSpawnUnconfirmed = false;
            restPoseStale = false;
        }

        // The anchor lies one render delay before the first snapshot, where the restarted
        // clock begins.
        private uint StartTicks()
        {
            return (uint)presenter.DelayTicks;
        }

        // A grounded state at the pose; the presenter derives a velocity from the states.
        private static PlayerMovementState PoseState(Vector3 position, float yawDegrees)
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
