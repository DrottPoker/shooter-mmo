using System;
using System.Collections.Generic;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // Mob snapshot routing, the shared remote interpolation, the settle onto the reliable rest
    // pose, the direct jump without recent snapshots, and the cleanup on hide and respawn.
    public sealed class MobMovementPresentationTests
    {
        private const ulong MobId = 11;
        private const ulong NpcId = 12;
        private const ulong PlayerId = 21;
        private const int TickRateHz = 30;
        private const int SnapshotRateHz = 15;
        private const float FrameSeconds = 1f / 60f;

        // A Mob walks 4.5 m/s, 0.15 m per tick. One frame at 60 Hz covers at most three quarters
        // of a tick at the clock's catch-up speed, so no frame moves it a whole tick, while a
        // jump onto the rest pose would cover the four ticks of the render delay.
        private const float MetresPerTick = 0.15f;
        private const float MaximumFrameStep = MetresPerTick;

        [Test]
        public void MobsShareTheRemotePlayerDelayAndTheSnapshotInterval()
        {
            // Two snapshot intervals, and at least 100 ms.
            Assert.That(RemoteMovementPresenter.InterpolationDelayTicks(30, 15), Is.EqualTo(4));
            Assert.That(RemoteMovementPresenter.InterpolationDelayTicks(20, 20), Is.EqualTo(2));
            Assert.That(RemoteMovementPresenter.InterpolationDelayTicks(60, 15), Is.EqualTo(8));
            Assert.That(RemoteMovementPresenter.InterpolationDelayTicks(60, 60), Is.EqualTo(6));

            var movement = Movement();
            Assert.That(movement.DelayTicks, Is.EqualTo(4));
            Assert.That(movement.SnapshotIntervalTicks, Is.EqualTo(2));
            Assert.That(movement.IsDriven, Is.False);
        }

        [Test]
        public void SnapshotEntitiesReachOnlyThePresentedMobViews()
        {
            var actors = new WorldActorClientState();
            Assert.That(actors.TryApplySpawn(Spawn(MobId, RealtimeWorldActorKind.Mob), out var error), Is.True, error);
            Assert.That(actors.TryApplySpawn(Spawn(NpcId, RealtimeWorldActorKind.Npc), out error), Is.True, error);
            actors.TryGet(MobId, out var mobEntry);
            actors.TryGet(NpcId, out var npcEntry);
            var mobObject = new GameObject("Mob");
            var respawnedObject = new GameObject("RespawnedMob");
            var unconfiguredObject = new GameObject("UnconfiguredMob");
            var npcObject = new GameObject("Npc");
            try
            {
                var mob = mobObject.AddComponent<MobView>();
                mob.Initialize(mobEntry);
                mob.Movement.Configure(TickRateHz, SnapshotRateHz, null, CollisionSettings());
                var npc = npcObject.AddComponent<NpcView>();
                npc.Initialize(npcEntry);
                var views = new Dictionary<ulong, WorldActorView> { [MobId] = mob, [NpcId] = npc };

                var routed = WorldActorPresentationController.RouteSnapshot(
                    Snapshot(102, Entity(MobId, 2.8f), Entity(NpcId, 9f), Entity(PlayerId, 5f), Entity(99, 7f)),
                    views);

                Assert.That(routed, Is.EqualTo(1));
                Assert.That(mob.Movement.IsDriven, Is.True);
                // The movement starts where the Mob stands, one render delay behind.
                Assert.That(mob.transform.position, Is.EqualTo(new Vector3(2.5f, 0f, -1f)));
                Assert.That(npc.transform.position, Is.EqualTo(new Vector3(2.5f, 0f, -1f)));

                // A despawned Mob has no view, so a later snapshot reaches nothing.
                views.Remove(MobId);
                Assert.That(
                    WorldActorPresentationController.RouteSnapshot(Snapshot(104, Entity(MobId, 3.1f)), views),
                    Is.Zero);

                // Its respawn is a new view that starts from its own spawn pose.
                var respawned = respawnedObject.AddComponent<MobView>();
                respawned.Initialize(mobEntry);
                respawned.Movement.Configure(TickRateHz, SnapshotRateHz, null, CollisionSettings());
                Assert.That(respawned.Movement.IsDriven, Is.False);
                views[MobId] = respawned;
                Assert.That(
                    WorldActorPresentationController.RouteSnapshot(Snapshot(110, Entity(MobId, 2.8f)), views),
                    Is.EqualTo(1));
                Assert.That(respawned.transform.position, Is.EqualTo(new Vector3(2.5f, 0f, -1f)));

                // A view without snapshot movement, such as one outside a joined session,
                // keeps its reliable pose.
                var unconfigured = unconfiguredObject.AddComponent<MobView>();
                unconfigured.Initialize(mobEntry);
                views[MobId] = unconfigured;
                WorldActorPresentationController.RouteSnapshot(Snapshot(112, Entity(MobId, 2.8f)), views);
                Assert.That(unconfigured.Movement.IsDriven, Is.False);
                Assert.That(unconfigured.transform.position, Is.EqualTo(new Vector3(2.5f, 0f, -1f)));
                Assert.That(WorldActorPresentationController.RouteSnapshot(null, views), Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mobObject);
                UnityEngine.Object.DestroyImmediate(respawnedObject);
                UnityEngine.Object.DestroyImmediate(unconfiguredObject);
                UnityEngine.Object.DestroyImmediate(npcObject);
            }
        }

        [Test]
        public void AMobWalksFromItsRestPoseAndSettlesOnItsRestStateWithoutAJump()
        {
            var movement = Movement();
            var walk = new WalkSimulation(movement, Vector3.zero);

            walk.Snapshots(102, 130);
            var beforeRest = movement.Position;
            Assert.That(beforeRest.x, Is.LessThan(4.5f - (2f * MetresPerTick)), "The clock lags the newest snapshot.");

            // The worker reports the rest pose one tick after the last snapshot.
            Assert.That(movement.TrySettle(new Vector3(4.65f, 0f, 0f), 90f), Is.True);
            Assert.That(movement.Position, Is.EqualTo(beforeRest), "Settling never moves the Mob at once.");
            Assert.That(movement.IsResting, Is.True);
            walk.Frames(90);

            Assert.That(walk.FirstPosition, Is.EqualTo(Vector3.zero));
            Assert.That(walk.MaximumStep, Is.LessThanOrEqualTo(MaximumFrameStep));
            Assert.That(walk.MovedBackwards, Is.False);
            Assert.That(movement.Position.x, Is.EqualTo(4.65f).Within(0.0001f));
            Assert.That(movement.YawDegrees, Is.EqualTo(90f).Within(0.0001f));
            Assert.That(movement.IsDriven, Is.True);
            Assert.That(movement.Advance(FrameSeconds, FrameSeconds), Is.False, "A settled rest pose costs nothing.");

            // A late snapshot older than the rest pose cannot pull the Mob back.
            Assert.That(movement.PushSnapshot(132, State(4.5f), Vector3.zero, 0f), Is.False);
        }

        [Test]
        public void ARestStateWithoutRecentSnapshotsIsAppliedDirectly()
        {
            var movement = Movement();
            Assert.That(movement.TrySettle(new Vector3(1f, 0f, 0f), 0f), Is.False, "No snapshot moves the Mob.");

            var walk = new WalkSimulation(movement, Vector3.zero);
            walk.Snapshots(102, 110);
            walk.Frames(120);
            var playedOut = movement.Position;
            Assert.That(playedOut.x, Is.EqualTo(MetresPerTick * 10f).Within(0.0001f));

            // The same pose keeps the snapshot presentation.
            Assert.That(movement.TrySettle(playedOut, 90f), Is.True);
            Assert.That(movement.IsDriven, Is.True);

            // Another pose after the snapshots played out is a jump for the caller.
            Assert.That(movement.TrySettle(new Vector3(6f, 0f, 0f), 90f), Is.False);
            Assert.That(movement.IsDriven, Is.False);
        }

        [Test]
        public void AMobThatRestedStartsAgainFromItsRestPose()
        {
            var movement = Movement();
            var walk = new WalkSimulation(movement, Vector3.zero);
            walk.Snapshots(102, 110);
            Assert.That(movement.TrySettle(new Vector3(1.65f, 0f, 0f), 90f), Is.True);
            walk.Frames(120);
            Assert.That(movement.Position.x, Is.EqualTo(1.65f).Within(0.0001f));

            // Seconds later the Mob walks on; the first snapshot is already 0.3 m along.
            Assert.That(movement.PushSnapshot(400, State(1.95f), new Vector3(1.65f, 0f, 0f), 90f), Is.True);
            Assert.That(movement.Position.x, Is.EqualTo(1.65f).Within(0.0001f));
            Assert.That(movement.IsResting, Is.False);
            Assert.That(movement.Advance(0f, 0f), Is.True, "The movement runs again.");

            var resumed = new WalkSimulation(movement, movement.Position);
            resumed.Frames(30);
            Assert.That(resumed.MaximumStep, Is.LessThanOrEqualTo(MaximumFrameStep));
            Assert.That(resumed.MovedBackwards, Is.False);
            Assert.That(movement.Position.x, Is.EqualTo(1.95f).Within(0.0001f));
        }

        [Test]
        public void ATeleportIsShownAtOnce()
        {
            var movement = Movement();
            var walk = new WalkSimulation(movement, Vector3.zero);
            walk.Snapshots(102, 106);

            // A rest pose far from the snapshots, such as an evading Mob placed home, is a jump.
            Assert.That(movement.TrySettle(new Vector3(20f, 0f, 0f), 0f), Is.False);
            Assert.That(movement.IsDriven, Is.False);

            // So is a first movement or a snapshot far from where the Mob is presented.
            Assert.That(movement.PushSnapshot(200, State(30f), new Vector3(20f, 0f, 0f), 0f), Is.True);
            Assert.That(movement.Position.x, Is.EqualTo(30f));
            Assert.That(movement.PushSnapshot(202, State(40f), Vector3.zero, 0f), Is.True);
            Assert.That(movement.Position.x, Is.EqualTo(40f));
        }

        [Test]
        public void MobViewsSettleIgnoreUnchangedStatesAndStartAgainAfterHiding()
        {
            var actors = new WorldActorClientState();
            Assert.That(actors.TryApplySpawn(Spawn(MobId, RealtimeWorldActorKind.Mob), out var error), Is.True, error);
            actors.TryGet(MobId, out var entry);
            var mobObject = new GameObject("Mob");
            try
            {
                var mob = mobObject.AddComponent<MobView>();
                mob.Initialize(entry);

                // Without snapshot movement a newer state places the Mob at once, as before.
                ApplyState(actors, mob, entry, 2, true, 3f);
                Assert.That(mob.transform.position, Is.EqualTo(new Vector3(3f, 0f, -1f)));

                mob.Movement.Configure(TickRateHz, SnapshotRateHz, null, CollisionSettings());
                mob.PushSnapshot(102, Entity(MobId, 3.3f, -1f).State);
                mob.PushSnapshot(104, Entity(MobId, 3.6f, -1f).State);
                Assert.That(mob.Movement.IsDriven, Is.True);
                Assert.That(mob.transform.position, Is.EqualTo(new Vector3(3f, 0f, -1f)));

                // The rest state settles through the snapshots instead of jumping there.
                ApplyState(actors, mob, entry, 3, true, 3.75f);
                Assert.That(mob.Movement.IsResting, Is.True);
                Assert.That(mob.transform.position, Is.EqualTo(new Vector3(3f, 0f, -1f)));

                // Every change applies every actor again; an unchanged entry moves nothing.
                mob.Apply(entry);
                Assert.That(mob.Movement.IsDriven, Is.True);
                Assert.That(mob.transform.position, Is.EqualTo(new Vector3(3f, 0f, -1f)));

                // A hidden Mob, such as a dead one, forgets its movement and ignores snapshots.
                ApplyState(actors, mob, entry, 4, false, 3.75f);
                Assert.That(mob.Movement.IsDriven, Is.False);
                Assert.That(mobObject.activeSelf, Is.False);
                mob.PushSnapshot(106, Entity(MobId, 3.9f, -1f).State);
                Assert.That(mob.Movement.IsDriven, Is.False);

                // When it returns it stands at its reliable pose and walks on from there.
                ApplyState(actors, mob, entry, 5, true, 8f);
                Assert.That(mobObject.activeSelf, Is.True);
                Assert.That(mob.Movement.IsDriven, Is.False);
                Assert.That(mob.transform.position, Is.EqualTo(new Vector3(8f, 0f, -1f)));
                mob.PushSnapshot(300, Entity(MobId, 8.3f, -1f).State);
                Assert.That(mob.Movement.IsDriven, Is.True);
                Assert.That(mob.transform.position, Is.EqualTo(new Vector3(8f, 0f, -1f)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mobObject);
            }
        }

        private static RemoteActorMovement Movement()
        {
            var movement = new RemoteActorMovement();
            movement.Configure(TickRateHz, SnapshotRateHz, null, CollisionSettings());
            return movement;
        }

        private static void ApplyState(
            WorldActorClientState actors,
            MobView mob,
            WorldActorClientEntry entry,
            long revision,
            bool isActive,
            float positionX)
        {
            Assert.That(
                actors.TryApplyState(
                    new RealtimeWorldActorState(
                        MobId,
                        revision,
                        1,
                        isActive,
                        positionX,
                        0f,
                        -1f,
                        270f,
                        RealtimeWorldActorActivityTier.Active),
                    out var error),
                Is.True,
                error);
            mob.Apply(entry);
        }

        private static RealtimePlayerState State(float positionX, float positionZ = 0f)
        {
            return new RealtimePlayerState(positionX, 0f, positionZ, 4.5f, 0f, 0f, 90f, true);
        }

        private static RealtimeEntitySnapshot Entity(ulong entityId, float positionX, float positionZ = -1f)
        {
            return new RealtimeEntitySnapshot(entityId, 0, State(positionX, positionZ));
        }

        private static RealtimeSimulationSnapshot Snapshot(uint serverTick, params RealtimeEntitySnapshot[] entities)
        {
            return new RealtimeSimulationSnapshot(1, serverTick, 0, 1, entities);
        }

        private static CharacterCollisionSettings CollisionSettings()
        {
            return new CharacterCollisionSettings(0.4f, 1.8f, 0.35f, 45f, 0.2f, 0.25f, 4);
        }

        private static RealtimeWorldActorSpawn Spawn(ulong entityId, RealtimeWorldActorKind kind)
        {
            var mob = kind == RealtimeWorldActorKind.Mob;
            return new RealtimeWorldActorSpawn(
                entityId,
                Guid.NewGuid(),
                mob ? "mob.feral_wolf" : "npc.city_services",
                mob ? "local.feral_wolf" : "local.city_services",
                mob ? "Feral Wolf" : "Mira",
                kind,
                mob ? "wild" : "city",
                mob ? RealtimeWorldActorDisposition.Hostile : RealtimeWorldActorDisposition.Friendly,
                mob ? "mob.feral_wolf" : "npc.city_services",
                1,
                1,
                true,
                2.5f,
                0f,
                -1f,
                270f,
                RealtimeWorldActorActivityTier.Active,
                0f,
                0.5f,
                0f,
                0.8f,
                1f,
                1.4f);
        }

        // Plays a Mob that started walking along +x at tick 100 at 60 frames per second, with
        // one snapshot every two ticks, and records how the presented pose moves.
        private sealed class WalkSimulation
        {
            private readonly RemoteActorMovement movement;
            private readonly Vector3 restPosition;
            private Vector3 lastPosition;
            private bool started;

            public WalkSimulation(RemoteActorMovement movement, Vector3 restPosition)
            {
                this.movement = movement;
                this.restPosition = restPosition;
                lastPosition = movement.IsDriven ? movement.Position : restPosition;
                started = movement.IsDriven;
                FirstPosition = lastPosition;
            }

            public Vector3 FirstPosition { get; private set; }

            public float MaximumStep { get; private set; }

            public bool MovedBackwards { get; private set; }

            // Four frames pass between two snapshots, as at 15 snapshots per second.
            public void Snapshots(uint firstTick, uint lastTick)
            {
                for (var tick = firstTick; tick <= lastTick; tick += 2)
                {
                    Assert.That(
                        movement.PushSnapshot(tick, State(MetresPerTick * (tick - 100)), restPosition, 90f),
                        Is.True,
                        "Snapshot " + tick);
                    Record();
                    Frames(tick < lastTick ? 4 : 1);
                }
            }

            public void Frames(int count)
            {
                for (var frame = 0; frame < count; frame++)
                {
                    movement.Advance(FrameSeconds, FrameSeconds);
                    Record();
                }
            }

            private void Record()
            {
                var position = movement.Position;
                if (!started)
                {
                    started = true;
                    FirstPosition = position;
                    lastPosition = position;
                    return;
                }

                MaximumStep = Mathf.Max(MaximumStep, Vector3.Distance(lastPosition, position));
                MovedBackwards |= position.x < lastPosition.x - 0.00001f;
                lastPosition = position;
            }
        }
    }
}
