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
    // pose, records where the Mob stands, the direct jump for a teleport, the cleanup on hide and
    // respawn, and the start of a Mob whose spawn says that it walked.
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

        // How far the Mob walks in one frame at 60 frames per second.
        private const float WalkingFrameStep = MetresPerTick * TickRateHz * FrameSeconds;

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
            var actors = FixtureTexts.ActorState();
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
        public void ARestPoseSettlesAfterTheSnapshotsPlayedOutAndOnlyAJumpIsTheCallers()
        {
            var movement = Movement();
            Assert.That(movement.TrySettle(new Vector3(1f, 0f, 0f), 0f), Is.False, "No snapshot moves the Mob.");

            // The snapshots end while the Mob walks: it walks on for a while and eases to a stop.
            var walk = new WalkSimulation(movement, Vector3.zero);
            walk.Snapshots(102, 110);
            walk.Frames(120);
            var extrapolated = (MetresPerTick * 10f)
                + (MetresPerTick * TickRateHz * (RemoteMovementPresenter.ExtrapolationSeconds + (RemoteMovementPresenter.ExtrapolationEaseSeconds / 2f)));
            Assert.That(movement.Position.x, Is.EqualTo(extrapolated).Within(0.01f));
            Assert.That(movement.IsStarved, Is.True);

            // The rest pose the worker reports brings it back without a jump.
            Assert.That(movement.TrySettle(new Vector3(1.65f, 0f, 0f), 90f), Is.True);
            Assert.That(movement.IsStarved, Is.False);
            var settle = new WalkSimulation(movement, movement.Position);
            settle.Frames(60);
            Assert.That(settle.MaximumStep, Is.LessThanOrEqualTo(RemoteMovementPresenter.CorrectionMetresPerSecond * FrameSeconds * 1.01f));
            Assert.That(movement.Position.x, Is.EqualTo(1.65f).Within(0.0001f));
            Assert.That(movement.IsDriven, Is.True);

            // A pose farther than a teleport from the newest state is a jump for the caller.
            Assert.That(movement.TrySettle(new Vector3(6f, 0f, 0f), 90f), Is.False);
            Assert.That(movement.IsDriven, Is.False);
        }

        [Test]
        public void ARecordOfTheMobWhereItStandsChangesNothing()
        {
            var rest = new Vector3(1.65f, 0f, 0f);
            var movement = Movement();

            // A refresh of a Mob that never moved.
            Assert.That(movement.PushSnapshot(102, State(1.65f), rest, 90f), Is.False);
            Assert.That(movement.IsDriven, Is.False);

            // The stop repeats after it came to rest.
            var walk = new WalkSimulation(movement, Vector3.zero);
            walk.Snapshots(102, 110);
            Assert.That(movement.TrySettle(rest, 90f), Is.True);
            walk.Frames(60);
            Assert.That(movement.PushSnapshot(116, State(1.65f), rest, 90f), Is.False);
            Assert.That(movement.PushSnapshot(146, State(1.65f), rest, 90f), Is.False);
            Assert.That(movement.Advance(FrameSeconds, FrameSeconds), Is.False, "A settled rest pose costs nothing.");
            Assert.That(movement.Position.x, Is.EqualTo(1.65f).Within(0.0001f));
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

            // It walks on, stops at its last snapshot, and the worker reports that pose.
            var resumed = new WalkSimulation(movement, movement.Position);
            for (var tick = 402u; tick <= 410u; tick += 2)
            {
                Assert.That(movement.PushSnapshot(tick, State(1.95f + (MetresPerTick * (tick - 400))), new Vector3(1.65f, 0f, 0f), 90f), Is.True);
                resumed.Frames(4);
            }

            Assert.That(movement.TrySettle(new Vector3(3.45f, 0f, 0f), 90f), Is.True);
            resumed.Frames(60);
            Assert.That(resumed.MaximumStep, Is.LessThanOrEqualTo(MaximumFrameStep));
            Assert.That(resumed.MovedBackwards, Is.False);
            Assert.That(movement.Position.x, Is.EqualTo(3.45f).Within(0.0001f));
        }

        // The first far-tier record of a walking Mob lies farther than a teleport from its reliable
        // pose, as when its view appeared only after its prefab loaded: that record says nothing
        // about standing, so the next one, 6 ticks later, walked the whole gap and the Mob is
        // shown at its walking speed.
        [Test]
        public void AWalkingMobFirstSeenFarFromItsReliablePoseIsShownAtItsWalkingSpeed()
        {
            var rest = Vector3.zero;
            var movement = Movement();
            var maximumStep = 0f;
            var movedBackwards = false;
            float? last = null;
            for (uint tick = 100; tick <= 160; tick += 6)
            {
                Assert.That(movement.PushSnapshot(tick, State(10f + (MetresPerTick * (tick - 100))), rest, 90f), Is.True);
                for (var frame = 0; frame < 12; frame++)
                {
                    movement.Advance(FrameSeconds, FrameSeconds);
                    var x = movement.Position.x;
                    if (last is { } previous)
                    {
                        maximumStep = Mathf.Max(maximumStep, Mathf.Abs(x - previous));
                        movedBackwards |= x < previous - 0.00001f;
                    }

                    last = x;
                }
            }

            Assert.That(maximumStep, Is.LessThanOrEqualTo(MetresPerTick * TickRateHz * FrameSeconds * 1.2f), "No dash.");
            Assert.That(movedBackwards, Is.False);
            Assert.That(movement.Position.x, Is.GreaterThan(13f), "It walks.");
            Assert.That(movement.Discontinuities, Is.EqualTo(1), "Only its appearance.");
        }

        // A Mob that walked when its spawn was sent starts from the spawn's pose as a remote
        // player does: its first record, however far behind the spawn within the longest render
        // delay, walked the whole gap, so the Mob walks on at its own speed without a dash.
        [TestCase(6u)]
        [TestCase(9u)]
        [TestCase(13u)]
        public void AMobThatComesIntoViewWalkingWalksOnAtItsOwnSpeed(uint firstRecordTicks)
        {
            var actors = FixtureTexts.ActorState();
            Assert.That(
                actors.TryApplySpawn(Spawn(MobId, RealtimeWorldActorKind.Mob, poseTick: 100, isMoving: true), out var error),
                Is.True,
                error);
            actors.TryGet(MobId, out var entry);
            var mobObject = new GameObject("Mob");
            try
            {
                var run = new SpawnRun(entry, 100 + firstRecordTicks);
                run.Present(mobObject.AddComponent<MobView>());
                Assert.That(run.Mob.Movement.IsDriven, Is.True, "It walks from its spawn.");
                Assert.That(run.Mob.transform.position, Is.EqualTo(new Vector3(2.5f, 0f, -1f)));

                run.Play(120);

                Assert.That(run.MaximumStep, Is.LessThanOrEqualTo(WalkingFrameStep * 1.2f), "No dash.");
                Assert.That(run.MovedBackwards, Is.False);
                Assert.That(run.Mob.Movement.Discontinuities, Is.EqualTo(1), "Only its appearance.");
                Assert.That(run.Mob.Movement.Position.x, Is.GreaterThan(8f), "It walks.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mobObject);
            }
        }

        // A Mob whose spawn names a rest pose starts from it as before, one render delay before
        // its first snapshot.
        [Test]
        public void AMobThatSpawnsRestingStartsFromItsRestPoseAsBefore()
        {
            var actors = FixtureTexts.ActorState();
            Assert.That(actors.TryApplySpawn(Spawn(MobId, RealtimeWorldActorKind.Mob, poseTick: 100), out var error), Is.True, error);
            actors.TryGet(MobId, out var entry);
            Assert.That(entry.IsMoving, Is.False);
            var mobObject = new GameObject("Mob");
            try
            {
                var mob = mobObject.AddComponent<MobView>();
                mob.Movement.Configure(TickRateHz, SnapshotRateHz, null, CollisionSettings());
                mob.Initialize(entry);
                Assert.That(mob.Movement.IsDriven, Is.False);
                Assert.That(mob.transform.position, Is.EqualTo(new Vector3(2.5f, 0f, -1f)));

                // Seconds later the Mob walks off; its first snapshot is already 0.3 m along.
                var views = new Dictionary<ulong, WorldActorView> { [MobId] = mob };
                Assert.That(WorldActorPresentationController.RouteSnapshot(Snapshot(160, Entity(MobId, 2.8f)), views), Is.EqualTo(1));
                Assert.That(mob.Movement.IsDriven, Is.True);
                Assert.That(mob.Movement.IsResting, Is.False);
                Assert.That(mob.transform.position, Is.EqualTo(new Vector3(2.5f, 0f, -1f)), "It stands where it rested.");
                var walk = new WalkSimulation(mob.Movement, mob.transform.position);
                walk.Frames(8);
                Assert.That(walk.MaximumStep, Is.LessThanOrEqualTo(MaximumFrameStep));
                Assert.That(walk.MovedBackwards, Is.False);
                Assert.That(mob.Movement.Position.x, Is.GreaterThan(2.6f), "It walks into its snapshot.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mobObject);
            }
        }

        // The view of a Mob appears only once its prefab loaded, and the snapshots before reach
        // nothing. A Mob that walked in its spawn still starts from that spawn and walks on at its
        // own speed; one whose rest pose arrived first rests there.
        [Test]
        public void AMobWhoseViewAppearsAfterItsPrefabLoadedStartsFromTheSpawnItGotEarlier()
        {
            var actors = FixtureTexts.ActorState();
            Assert.That(
                actors.TryApplySpawn(Spawn(MobId, RealtimeWorldActorKind.Mob, poseTick: 100, isMoving: true), out var error),
                Is.True,
                error);
            actors.TryGet(MobId, out var entry);
            var mobObject = new GameObject("Mob");
            var restingObject = new GameObject("RestingMob");
            try
            {
                // The prefab loads for a quarter of a second, while the record of tick 106 arrives.
                var run = new SpawnRun(entry, 106);
                run.Play(15);
                Assert.That(run.Routed, Is.Zero);
                run.Present(mobObject.AddComponent<MobView>());
                Assert.That(run.Mob.transform.position, Is.EqualTo(new Vector3(2.5f, 0f, -1f)), "It appears at its spawn.");

                run.Play(105);

                Assert.That(run.Routed, Is.GreaterThan(0));
                Assert.That(run.MaximumStep, Is.LessThanOrEqualTo(WalkingFrameStep * 1.2f), "No dash.");
                Assert.That(run.MovedBackwards, Is.False);
                Assert.That(run.Mob.Movement.Discontinuities, Is.EqualTo(1), "Only its appearance.");
                Assert.That(run.Mob.Movement.Position.x, Is.GreaterThan(6f), "It walks.");

                // The Mob came to rest before its view appeared: the newer state is a rest pose.
                var resting = FixtureTexts.ActorState();
                Assert.That(
                    resting.TryApplySpawn(Spawn(MobId, RealtimeWorldActorKind.Mob, poseTick: 100, isMoving: true), out error),
                    Is.True,
                    error);
                Assert.That(
                    resting.TryApplyState(
                        new RealtimeWorldActorState(MobId, 2, 1, true, 3.1f, 0f, -1f, 90f, RealtimeWorldActorActivityTier.Active),
                        out error),
                    Is.True,
                    error);
                resting.TryGet(MobId, out var restingEntry);
                Assert.That(restingEntry.IsMoving, Is.False);
                var restingMob = restingObject.AddComponent<MobView>();
                restingMob.Movement.Configure(TickRateHz, SnapshotRateHz, null, CollisionSettings());
                restingMob.Initialize(restingEntry);
                Assert.That(restingMob.Movement.IsDriven, Is.False);
                Assert.That(restingMob.transform.position, Is.EqualTo(new Vector3(3.1f, 0f, -1f)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mobObject);
                UnityEngine.Object.DestroyImmediate(restingObject);
            }
        }

        // The view of a Mob appears only long after its spawn, after a slow prefab load, so its
        // first snapshot comes later than a walk from the spawn's pose could have lasted, or shows
        // it away from the rest pose of a spawn that stood. The view shows the Mob at that
        // snapshot, as it does for a teleport, and the Mob walks on from there at its own speed
        // instead of sliding the gap many times too fast. The 2 s load is of a slow walker, so
        // the gap stays below the teleport distance and this rule, not the jump, places it.
        [TestCase(25, 4.5f, true)]
        [TestCase(120, 1.2f, true)]
        [TestCase(25, 4.5f, false)]
        [TestCase(120, 1.2f, false)]
        public void AMobWhoseViewAppearsLongAfterItsSpawnStartsAtItsFirstSnapshot(
            int loadFrames,
            float metresPerSecond,
            bool spawnWalked)
        {
            var metresPerTick = metresPerSecond / TickRateHz;
            var actors = FixtureTexts.ActorState();
            Assert.That(
                actors.TryApplySpawn(Spawn(MobId, RealtimeWorldActorKind.Mob, poseTick: 100, isMoving: spawnWalked), out var error),
                Is.True,
                error);
            actors.TryGet(MobId, out var entry);
            var mobObject = new GameObject("Mob");
            try
            {
                var run = new SpawnRun(entry, 106, metresPerTick);
                run.Play(loadFrames);
                Assert.That(run.Routed, Is.Zero);
                run.Present(mobObject.AddComponent<MobView>());
                Assert.That(run.Mob.transform.position, Is.EqualTo(new Vector3(2.5f, 0f, -1f)), "It appears at its spawn.");

                run.Play(120);

                // The first record after the load, of the first record tick past it.
                var firstRouted = 106u + (6u * (uint)((((loadFrames / 2) + 100 - 106) / 6) + 1));
                Assert.That(run.Jumps, Is.EqualTo(1), "It is shown at its first snapshot.");
                Assert.That(run.JumpedToX, Is.EqualTo(SpawnRun.WalkX(firstRouted, metresPerTick)).Within(0.01f));
                Assert.That(run.JumpedToX - 2.5f, Is.LessThan(RemoteActorMovement.TeleportDistance));
                Assert.That(run.MaximumStep, Is.LessThanOrEqualTo(metresPerTick * TickRateHz * FrameSeconds * 1.2f), "No slide.");
                Assert.That(run.MovedBackwards, Is.False);
                Assert.That(run.Mob.Movement.Position.x, Is.GreaterThan(run.JumpedToX + 0.5f), "It walks on.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mobObject);
            }
        }

        // A lost record matters only when it showed the Mob away from the pose its view starts
        // at: a Mob that stood while its prefab loaded starts from its rest pose as before, and so
        // does one whose newer rest pose arrived after the lost records.
        [Test]
        public void AMobThatRestedWhileItsPrefabLoadedStartsFromItsRestPoseAsBefore()
        {
            var actors = FixtureTexts.ActorState();
            Assert.That(actors.TryApplySpawn(Spawn(MobId, RealtimeWorldActorKind.Mob, poseTick: 100), out var error), Is.True, error);
            actors.TryGet(MobId, out var entry);
            var missed = new Dictionary<ulong, long>();
            Action<RealtimeEntitySnapshot> note = record =>
            {
                if (WorldActorPresentationController.ShowsAwayFromPose(entry, record))
                {
                    missed[record.EntityId] = entry.StateRevision;
                }
            };
            var noViews = new Dictionary<ulong, WorldActorView>();

            // A refresh where it rests is no move.
            WorldActorPresentationController.RouteSnapshot(Snapshot(130, Entity(MobId, 2.5f)), noViews, note);
            Assert.That(WorldActorPresentationController.MissedAMove(entry, missed), Is.False);

            // A move followed by its newer rest pose is no move from that pose.
            WorldActorPresentationController.RouteSnapshot(Snapshot(136, Entity(MobId, 3.4f)), noViews, note);
            Assert.That(WorldActorPresentationController.MissedAMove(entry, missed), Is.True);
            Assert.That(
                actors.TryApplyState(
                    new RealtimeWorldActorState(MobId, 2, 1, true, 3.4f, 0f, -1f, 90f, RealtimeWorldActorActivityTier.Active),
                    out error),
                Is.True,
                error);
            Assert.That(WorldActorPresentationController.MissedAMove(entry, missed), Is.False);

            var mobObject = new GameObject("Mob");
            try
            {
                var mob = mobObject.AddComponent<MobView>();
                mob.Movement.Configure(TickRateHz, SnapshotRateHz, null, CollisionSettings());
                mob.Initialize(entry);

                // Seconds later it walks off: it starts from its rest pose as before.
                var views = new Dictionary<ulong, WorldActorView> { [MobId] = mob };
                Assert.That(WorldActorPresentationController.RouteSnapshot(Snapshot(300, Entity(MobId, 3.7f)), views, note), Is.EqualTo(1));
                Assert.That(mob.Movement.IsDriven, Is.True);
                Assert.That(mob.transform.position, Is.EqualTo(new Vector3(3.4f, 0f, -1f)), "It stands where it rested.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mobObject);
            }
        }

        // A Mob that came into view walking keeps every Mob rule: the record of its spawn's own
        // snapshot changes nothing, a rest pose settles it without a jump and then costs nothing,
        // and a hidden Mob forgets its movement and returns at its reliable pose.
        [Test]
        public void AMobThatCameIntoViewWalkingSettlesAndHidesAsEveryMob()
        {
            var actors = FixtureTexts.ActorState();
            Assert.That(
                actors.TryApplySpawn(Spawn(MobId, RealtimeWorldActorKind.Mob, poseTick: 100, isMoving: true), out var error),
                Is.True,
                error);
            actors.TryGet(MobId, out var entry);
            var mobObject = new GameObject("Mob");
            try
            {
                var run = new SpawnRun(entry, 102);
                run.Present(mobObject.AddComponent<MobView>());
                var mob = run.Mob;

                // The record of the spawn's own snapshot is not newer than the spawn.
                var views = new Dictionary<ulong, WorldActorView> { [MobId] = mob };
                Assert.That(WorldActorPresentationController.RouteSnapshot(Snapshot(100, Entity(MobId, 2.5f)), views), Is.EqualTo(1));
                Assert.That(mob.Movement.Discontinuities, Is.EqualTo(1));

                // It walks through the records of ticks 102, 108, and 114 and stops a tick later.
                run.Play(30);
                ApplyState(actors, mob, entry, 2, true, SpawnRun.WalkX(115));
                Assert.That(entry.IsMoving, Is.False);
                Assert.That(mob.Movement.IsResting, Is.True);
                var settle = new WalkSimulation(mob.Movement, mob.Movement.Position);
                settle.Frames(90);
                Assert.That(settle.MaximumStep, Is.LessThanOrEqualTo(MaximumFrameStep));
                Assert.That(settle.MovedBackwards, Is.False);
                Assert.That(mob.Movement.Position.x, Is.EqualTo(SpawnRun.WalkX(115)).Within(0.01f));
                Assert.That(mob.Movement.Advance(FrameSeconds, FrameSeconds), Is.False, "A settled rest pose costs nothing.");

                // A hidden Mob forgets its movement and returns at its reliable pose.
                ApplyState(actors, mob, entry, 3, false, SpawnRun.WalkX(115));
                Assert.That(mob.Movement.IsDriven, Is.False);
                ApplyState(actors, mob, entry, 4, true, 2.5f);
                Assert.That(mob.Movement.IsDriven, Is.False);
                Assert.That(mob.transform.position, Is.EqualTo(new Vector3(2.5f, 0f, -1f)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mobObject);
            }
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
            var actors = FixtureTexts.ActorState();
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
                mob.PushSnapshot(102, Entity(MobId, 3.3f, -1f));
                mob.PushSnapshot(104, Entity(MobId, 3.6f, -1f));
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
                mob.PushSnapshot(106, Entity(MobId, 3.9f, -1f));
                Assert.That(mob.Movement.IsDriven, Is.False);

                // When it returns it stands at its reliable pose and walks on from there.
                ApplyState(actors, mob, entry, 5, true, 8f);
                Assert.That(mobObject.activeSelf, Is.True);
                Assert.That(mob.Movement.IsDriven, Is.False);
                Assert.That(mob.transform.position, Is.EqualTo(new Vector3(8f, 0f, -1f)));
                mob.PushSnapshot(300, Entity(MobId, 8.3f, -1f));
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

        private static PlayerMovementState State(float positionX, float positionZ = 0f)
        {
            return new PlayerMovementState(positionX, 0f, positionZ, 0f, 0f, 0f, 90f, true);
        }

        private static RealtimeEntitySnapshot Entity(ulong entityId, float positionX, float positionZ = -1f)
        {
            return RealtimeEntitySnapshot.Quantize((uint)entityId, positionX, 0f, positionZ, 90f, true);
        }

        private static RealtimeSimulationSnapshot Snapshot(uint serverTick, params RealtimeEntitySnapshot[] entities)
        {
            return new RealtimeSimulationSnapshot(1, serverTick, 0, 1, entities);
        }

        private static CharacterCollisionSettings CollisionSettings()
        {
            return new CharacterCollisionSettings(0.4f, 1.8f, 0.35f, 45f, 0.2f, 0.25f, 4);
        }

        private static RealtimeWorldActorSpawn Spawn(
            ulong entityId,
            RealtimeWorldActorKind kind,
            uint poseTick = 0,
            bool isMoving = false)
        {
            var mob = kind == RealtimeWorldActorKind.Mob;
            return new RealtimeWorldActorSpawn(
                entityId,
                Guid.NewGuid(),
                mob ? "mob.feral_wolf" : "npc.city_services",
                mob ? "local.feral_wolf" : "local.city_services",
                kind,
                mob ? "wild" : "city",
                mob ? RealtimeWorldActorDisposition.Hostile : RealtimeWorldActorDisposition.Friendly,
                (ushort)(mob ? 2 : 0),
                mob ? RealtimeWorldActorAggression.Aggressive : RealtimeWorldActorAggression.None,
                mob ? "mob.feral_wolf" : "npc.city_services",
                1,
                1,
                true,
                2.5f,
                0f,
                -1f,
                270f,
                RealtimeWorldActorActivityTier.Active,
                poseTick,
                isMoving,
                0f,
                0.5f,
                0f,
                0.8f,
                1f,
                1.4f,
                mob
                    ? new RealtimeWorldActorMenuOption[0]
                    : new[] { new RealtimeWorldActorMenuOption("services.dialogue", RealtimeWorldActorCapabilityKind.Dialogue) });
        }

        // Plays a Mob that walks along +x and passed x 2.5, the pose of its spawn, at tick 100, at
        // 60 frames per second from the moment its spawn arrived: every later tick arrives
        // (tick - 100) / 30 s after it. Its records start at the first record tick and come every
        // six ticks, as far away. One that arrives before the view exists, as while the prefab
        // loads, reaches no view, and the run remembers it as WorldActorPresentationController
        // does. Records how the presented pose moves once the view exists; a jump, a start at a
        // snapshot, is counted apart from the steps.
        private sealed class SpawnRun
        {
            private readonly Dictionary<ulong, WorldActorView> views = new Dictionary<ulong, WorldActorView>();
            private readonly Dictionary<ulong, long> missedMoves = new Dictionary<ulong, long>();
            private readonly WorldActorClientEntry entry;
            private readonly uint firstRecordTick;
            private readonly float metresPerTick;
            private readonly Action<RealtimeEntitySnapshot> noteUnrouted;
            private uint nextTick = 101;
            private int frame;
            private float? lastX;

            public SpawnRun(WorldActorClientEntry entry, uint firstRecordTick, float metresPerTick = MetresPerTick)
            {
                this.entry = entry;
                this.firstRecordTick = firstRecordTick;
                this.metresPerTick = metresPerTick;
                noteUnrouted = record =>
                {
                    if (WorldActorPresentationController.ShowsAwayFromPose(entry, record))
                    {
                        missedMoves[record.EntityId] = entry.StateRevision;
                    }
                };
            }

            public MobView Mob { get; private set; }

            public int Routed { get; private set; }

            public float MaximumStep { get; private set; }

            public bool MovedBackwards { get; private set; }

            public int Jumps { get; private set; }

            // Where the Mob was shown right after its last jump.
            public float JumpedToX { get; private set; }

            public static float WalkX(uint tick)
            {
                return WalkX(tick, MetresPerTick);
            }

            public static float WalkX(uint tick, float metresPerTick)
            {
                return 2.5f + (metresPerTick * (tick - 100));
            }

            // The view appears with the session's snapshot movement, as when its prefab is ready.
            public void Present(MobView mob)
            {
                mob.Movement.Configure(TickRateHz, SnapshotRateHz, null, CollisionSettings());
                mob.Initialize(entry);
                if (WorldActorPresentationController.MissedAMove(entry, missedMoves))
                {
                    mob.StartAtNextSnapshot();
                }

                missedMoves.Remove(entry.EntityId);
                views[MobId] = mob;
                Mob = mob;
                lastX = mob.transform.position.x;
            }

            public void Play(int frames)
            {
                for (var index = 0; index < frames; index++)
                {
                    frame++;
                    var jumpsBefore = Mob != null ? Mob.Movement.Discontinuities : 0;
                    while ((nextTick - 100) / (double)TickRateHz <= frame / 60d)
                    {
                        if (nextTick >= firstRecordTick && (nextTick - firstRecordTick) % 6 == 0)
                        {
                            Routed += WorldActorPresentationController.RouteSnapshot(
                                Snapshot(nextTick, Entity(MobId, WalkX(nextTick, metresPerTick))),
                                views,
                                noteUnrouted);
                        }

                        nextTick++;
                    }

                    if (Mob == null || !Mob.Movement.Advance(FrameSeconds, FrameSeconds))
                    {
                        continue;
                    }

                    var x = Mob.Movement.Position.x;
                    if (Mob.Movement.Discontinuities != jumpsBefore)
                    {
                        Jumps += Mob.Movement.Discontinuities - jumpsBefore;
                        JumpedToX = x;
                    }
                    else if (lastX is { } previous)
                    {
                        MaximumStep = Mathf.Max(MaximumStep, Mathf.Abs(x - previous));
                        MovedBackwards |= x < previous - 0.00001f;
                    }

                    lastX = x;
                }
            }
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
