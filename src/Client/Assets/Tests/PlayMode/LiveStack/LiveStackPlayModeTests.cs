using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Ui;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.PlayMode
{
    // Plays one character through the client's real network layer against the local stack:
    // ApiClient for the account, the character, the Shard list, and the join tickets, and
    // RealtimeSimulationClient on LiteNetLib to the SimulationWorker. tools/Test-LiveStack.ps1
    // starts the stack and runs it. Every other run leaves it out: a headless run by its
    // category, the Editor's runners because it is explicit.
    [Explicit("Needs the local stack; tools/Test-LiveStack.ps1 starts it and runs this test.")]
    [Category("LiveStack")]
    public sealed class LiveStackPlayModeTests
    {
        private const string ShardId = "local-shard-1";

        // The runtime of the worker that tools/Test-LiveStack.ps1 started; tickets must name it.
        private const string WorkerRuntimeVariable = "MMORPG_LIVE_STACK_WORKER_RUNTIME_ID";

        private const int ExpectedBodySeconds = 20;
        private const float WalkMeters = 6f;
        private const float MinimumWalkMeters = 2.5f;
        private const float WalkSeconds = 2f;

        // A reconnect resumes the entity as it stands; a saved place is restored from floats.
        private const float ResumedPlaceTolerance = 0.05f;
        private const float SavedPlaceTolerance = 0.1f;

        private ApiClient api;
        private LiveStackTransport transport;
        private RealtimeSimulationClient hub;
        private LiveStackProbe probe;
        private string sessionToken;
        private string characterId;
        private JoinShardResponse ticket;
        private ActiveSimulationSessionResponse joined;
        private ushort lastBodySeconds;
        private SimulationVector3 standing;

        // The GameService of the client's configuration, which the script's stack serves.
        private static string BaseUrl
        {
            get { return PlayModeClient.Session.Settings.GameServiceBaseUrl; }
        }

        private ulong ControlledEntityId
        {
            get { return hub.Movement.MovementSession.ControlledEntityId; }
        }

        private SimulationVector3 JoinedPosition
        {
            get { return hub.Movement.MovementSession.InitialState.Position; }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator ACharacterJoinsWalksLeavesAndComesBackOverTheRealNetwork()
        {
            yield return PlayModeClient.WaitForContent();
            yield return AssertStackReady();
            api = new ApiClient(10);
            transport = new LiveStackTransport();
            var hubObject = new GameObject("Live stack hub");
            hubObject.SetActive(false);
            hub = hubObject.AddComponent<RealtimeSimulationClient>();
            hub.Initialize(transport, () => Time.realtimeSinceStartup, PlayModeClient.Session.Settings);
            hubObject.SetActive(true);
            probe = new LiveStackProbe(hub);
            try
            {
                // a. A fresh account and character join over UDP, and the baseline arrives.
                yield return CreateAccountAndCharacter();
                yield return RequestTicket(false);
                Assert.That(ticket.isReconnect, Is.False, "a. A new character has no session to reconnect to.");
                yield return Join();
                Assert.That(joined.isReconnect, Is.False, "a. The first join is no reconnect.");
                var firstEntity = ControlledEntityId;
                var spawn = JoinedPosition;
                yield return WaitFor(
                    () => probe.OwnerSnapshotCount > 0
                        && probe.WorldActorSpawnCount > 0
                        && probe.LatestOwnerCombatState != null,
                    5f,
                    "a. the baseline: an owner snapshot, a world actor spawn, and the owner's combat state");
                var startedRuntime = Environment.GetEnvironmentVariable(WorkerRuntimeVariable);
                Report("a. joined " + joined.characterName + " on " + joined.shardId + " through runtime "
                    + joined.workerRuntimeId
                    + (string.IsNullOrEmpty(startedRuntime) ? " (no started runtime named)" : " (the started worker)")
                    + ": entity " + firstEntity + " at " + Format(spawn) + ", "
                    + probe.OwnerSnapshotCount + " owner snapshots, " + probe.WorldActorSpawnCount
                    + " world actor spawns, health " + probe.LatestOwnerCombatState.Health + "/"
                    + probe.LatestOwnerCombatState.MaximumHealth);

                // b. Movement input walks the character, as the worker's owner snapshots show.
                yield return WalkAway();
                var walked = Distance(spawn, standing);
                Assert.That(walked, Is.GreaterThanOrEqualTo(MinimumWalkMeters), "b. The owner snapshots show no walk.");
                Report("b. the owner snapshots moved entity " + firstEntity + " " + Meters(walked) + " to " + Format(standing));

                // c. A leave is accepted with the body's time and ends the session once, as expected.
                yield return Leave("c");
                Assert.That(lastBodySeconds, Is.EqualTo(ExpectedBodySeconds), "c. LeaveAccepted names the body's time.");
                Report("c. LeaveAccepted with BodySeconds " + lastBodySeconds
                    + ", one SessionEnded, no UnexpectedlyDisconnected");

                // d. A join right after the leave reconnects to the body where it stands.
                yield return RequestTicket(false);
                Assert.That(ticket.isReconnect, Is.True, "d. The ticket reconnects to the body's session.");
                yield return Join();
                AssertResumed("d", firstEntity);

                // e. A line dropped without a leave, then a join, reconnects to the same entity.
                var endedBeforeDrop = probe.SessionEndedCount;
                transport.DropLine();
                hub.Session.Abort();
                Assert.That(probe.SessionEndedCount, Is.EqualTo(endedBeforeDrop + 1), "e. The dropped session ends once.");
                yield return new WaitForSecondsRealtime(1f);
                yield return RequestTicket(false);
                Assert.That(ticket.isReconnect, Is.True, "e. The ticket reconnects to the session of the dropped line.");
                yield return Join();
                AssertResumed("e", firstEntity);
                yield return WaitFor(() => probe.LatestOwnerCombatState != null, 5f, "e. the owner's combat state");
                var vitals = probe.LatestOwnerCombatState;

                // f. Once the body is gone, a join is new: a new entity at the saved place and vitals.
                yield return Leave("f");
                Assert.That(lastBodySeconds, Is.EqualTo(ExpectedBodySeconds), "f. LeaveAccepted names the body's time.");
                yield return new WaitForSecondsRealtime(lastBodySeconds);
                yield return RequestTicket(true);
                yield return Join();
                Assert.That(joined.isReconnect, Is.False, "f. The body is gone, so the join is no reconnect.");
                var newEntity = ControlledEntityId;
                Assert.That(newEntity, Is.Not.EqualTo(firstEntity), "f. The join creates a new entity.");
                var savedPlaceOffset = Distance(JoinedPosition, standing);
                Assert.That(
                    savedPlaceOffset,
                    Is.LessThanOrEqualTo(SavedPlaceTolerance),
                    "f. The new entity starts at the saved place, " + Format(standing) + ", not at "
                    + Format(JoinedPosition) + ".");
                yield return WaitFor(() => probe.LatestOwnerCombatState != null, 5f, "f. the owner's combat state");
                var restored = probe.LatestOwnerCombatState;
                Assert.That(
                    (restored.Health, restored.Mana),
                    Is.EqualTo((vitals.Health, vitals.Mana)),
                    "f. The new entity has the saved vitals.");
                Report("f. after the body's " + lastBodySeconds + " s: no reconnect, new entity " + newEntity
                    + " at " + Format(JoinedPosition) + " (" + Meters(savedPlaceOffset) + " from where it stood), health "
                    + restored.Health + "/" + restored.MaximumHealth + ", mana " + restored.Mana + "/"
                    + restored.MaximumMana + " as before");

                // g. A clean leave ends the scenario.
                yield return Leave("g");
                Report("g. left cleanly with BodySeconds " + lastBodySeconds);
            }
            finally
            {
                probe.Dispose();
                if (hub.Session.State != RealtimeConnectionState.Disconnected)
                {
                    hub.Session.Abort();
                }

                UnityEngine.Object.Destroy(hubObject);
                api.Dispose();
            }
        }

        private static IEnumerator AssertStackReady()
        {
            using (var request = UnityWebRequest.Get(BaseUrl.TrimEnd('/') + "/health/ready"))
            {
                request.timeout = 5;
                yield return request.SendWebRequest();
                Assert.That(
                    request.result,
                    Is.EqualTo(UnityWebRequest.Result.Success),
                    "GameService at " + BaseUrl + " is not ready (" + request.error
                    + "). tools/Test-LiveStack.ps1 starts the local stack and runs this test.");
            }
        }

        private IEnumerator CreateAccountAndCharacter()
        {
            // A random name, so reruns against the same database never collide.
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            var username = "live_" + suffix;
            AuthResponse account = null;
            ApiError error = null;
            yield return api.Register(
                BaseUrl,
                new RegisterAccountRequest
                {
                    email = username + "@example.test",
                    username = username,
                    password = "live-" + suffix
                },
                value => account = value,
                value => error = value);
            AssertNoApiError(error, "registering the account");
            sessionToken = account.sessionToken;

            CharacterAppearanceCatalogResponse appearanceCatalog = null;
            yield return api.GetCharacterAppearanceCatalog(
                BaseUrl,
                sessionToken,
                value => appearanceCatalog = value,
                value => error = value);
            AssertNoApiError(error, "reading what character creation offers");
            var look = new CharacterCreationLook(appearanceCatalog);
            Assert.That(look.IsAvailable, Is.True, "GameService offers no body model.");

            CharacterResponse character = null;
            yield return api.CreateCharacter(
                BaseUrl,
                sessionToken,
                new CreateCharacterRequest { name = "Live " + suffix.Substring(0, 10), appearance = look.ToRequest() },
                value => character = value,
                value => error = value);
            AssertNoApiError(error, "creating the character");
            characterId = character.id;

            ShardResponse[] shards = null;
            yield return api.GetShards(BaseUrl, value => shards = value, value => error = value);
            AssertNoApiError(error, "listing the shards");
            var shard = Array.Find(shards, candidate => candidate.id == ShardId);
            Assert.That(shard, Is.Not.Null, "GameService lists no " + ShardId + ".");
            Assert.That(shard.isOnline, Is.True, ShardId + " is not online.");
        }

        // Asks for tickets until one names the worker that the script started, and with
        // untilReleased until the character's previous session is released, so no reconnect.
        private IEnumerator RequestTicket(bool untilReleased)
        {
            var expectedRuntime = Environment.GetEnvironmentVariable(WorkerRuntimeVariable);
            var deadline = Time.realtimeSinceStartup + (untilReleased ? 30f : 90f);
            while (true)
            {
                JoinShardResponse result = null;
                ApiError error = null;
                yield return api.CreateJoinTicket(
                    BaseUrl,
                    sessionToken,
                    ShardId,
                    new JoinShardRequest { characterId = characterId },
                    value => result = value,
                    value => error = value);
                AssertNoApiError(error, "asking for a join ticket");
                var onStartedWorker = string.IsNullOrEmpty(expectedRuntime)
                    || string.Equals(result.endpoint.runtimeId, expectedRuntime, StringComparison.Ordinal);
                if (onStartedWorker && (!untilReleased || !result.isReconnect))
                {
                    ticket = result;
                    yield break;
                }

                Assert.That(
                    Time.realtimeSinceStartup,
                    Is.LessThan(deadline),
                    untilReleased
                        ? "The body's session was not released in time."
                        : "GameService places the Shard on runtime " + result.endpoint.runtimeId
                          + ", not on the started worker's runtime " + expectedRuntime + ".");
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        private IEnumerator Join()
        {
            probe.Reset();
            joined = null;
            RealtimeClientError error = null;
            yield return hub.Session.Join(ticket, value => joined = value, value => error = value);
            Assert.That(error, Is.Null, error != null ? "The join failed: " + error.ToDiagnostic() : null);
            Assert.That(joined, Is.Not.Null);
            Assert.That(hub.Session.IsJoined, Is.True);
            Assert.That(probe.SessionJoinedCount, Is.EqualTo(1));
        }

        private IEnumerator Leave(string step)
        {
            var endedBefore = probe.SessionEndedCount;
            var unexpectedBefore = probe.UnexpectedlyDisconnectedCount;
            var answersBefore = transport.LeaveBodySeconds.Count;
            var left = false;
            RealtimeClientError error = null;
            yield return hub.Session.Leave(joined.simulationSessionId, () => left = true, value => error = value);
            Assert.That(error, Is.Null, error != null ? step + ". The leave failed: " + error.ToDiagnostic() : null);
            Assert.That(left, Is.True);

            // Late events of the ended session would arrive within this second.
            yield return new WaitForSecondsRealtime(1f);
            Assert.That(probe.SessionEndedCount - endedBefore, Is.EqualTo(1), step + ". One SessionEnded.");
            Assert.That(probe.UnexpectedlyDisconnectedCount - unexpectedBefore, Is.Zero, step + ". No UnexpectedlyDisconnected.");
            Assert.That(transport.LeaveBodySeconds.Count, Is.EqualTo(answersBefore + 1), step + ". One LeaveAccepted.");
            lastBodySeconds = transport.LeaveBodySeconds[transport.LeaveBodySeconds.Count - 1];
        }

        private void AssertResumed(string step, ulong firstEntity)
        {
            Assert.That(joined.isReconnect, Is.True, step + ". The join is a reconnect.");
            Assert.That(ControlledEntityId, Is.EqualTo(firstEntity), step + ". The reconnect keeps the entity.");
            var offset = Distance(JoinedPosition, standing);
            Assert.That(
                offset,
                Is.LessThanOrEqualTo(ResumedPlaceTolerance),
                step + ". The entity is where it stood, " + Format(standing) + ", not at " + Format(JoinedPosition) + ".");
            Report(step + ". reconnect (IsReconnect " + joined.isReconnect + ", ticket " + ticket.isReconnect + ") to entity "
                + ControlledEntityId + " at " + Format(JoinedPosition) + ", " + Meters(offset) + " from where it stood");
        }

        // Plans a route about six meters away with the shared planner and sends one input per
        // simulation tick for two seconds, as the local player does: the newest batch each time,
        // with the route until an owner snapshot confirms it. Without input the character
        // stands, so the walk ends where the owner snapshots settle.
        private IEnumerator WalkAway()
        {
            var session = hub.Movement.MovementSession;
            yield return WaitFor(() => probe.LatestOwnerSnapshot != null, 5f, "b. an owner snapshot");
            var start = PositionOf(probe.LatestOwnerSnapshot);
            var planner = new MovementPathPlanner(session.CollisionWorld, session.Settings, MovementPathPlannerSettings.Default);
            MovementPathPlan plan = null;
            for (var direction = 0; direction < 8 && plan == null; direction++)
            {
                var angle = direction * Mathf.PI / 4f;
                var destination = new SimulationVector3(
                    start.X + (Mathf.Cos(angle) * WalkMeters),
                    start.Y,
                    start.Z + (Mathf.Sin(angle) * WalkMeters));
                Assert.That(session.TryEnsureCollisionChunks(new[] { start, destination }, out var chunkError), Is.True, chunkError);
                var candidate = planner.Plan(start, destination);
                if (candidate.Status != MovementPathPlanStatus.NoPath
                    && candidate.Waypoints.Count > 0
                    && Distance(start, candidate.Waypoints[candidate.Waypoints.Count - 1]) >= MinimumWalkMeters)
                {
                    plan = candidate;
                }
            }

            Assert.That(plan, Is.Not.Null, "b. No walkable route leads away from the spawn.");
            var path = new MovementPath(1, plan.Waypoints);
            var pending = new List<PlayerMovementInput>();
            var confirmedPathId = 0u;
            Action<RealtimeOwnerSimulationSnapshot> onSnapshot = snapshot =>
            {
                pending.RemoveAll(input => !MovementSequence.IsNewer(input.InputSequence, snapshot.LastProcessedInputSequence));
                confirmedPathId = snapshot.PathProgress.PathId;
            };
            hub.Movement.OwnerSimulationSnapshotReceived += onSnapshot;
            try
            {
                var tickSeconds = session.Settings.FixedDeltaTime;
                var accumulator = tickSeconds;
                var sequence = 0u;
                var end = Time.realtimeSinceStartup + WalkSeconds;
                while (Time.realtimeSinceStartup < end)
                {
                    while (accumulator >= tickSeconds)
                    {
                        accumulator -= tickSeconds;
                        sequence++;
                        pending.Add(new PlayerMovementInput(sequence, sequence, path));
                        var count = Math.Min(pending.Count, RealtimeProtocol.MaximumInputBatchSize);
                        var batch = pending.GetRange(pending.Count - count, count).ToArray();
                        Assert.That(
                            hub.Movement.TrySendMovementInputs(batch, confirmedPathId == path.Id ? null : path),
                            Is.True,
                            "b. The movement input could not be sent.");
                    }

                    yield return null;
                    accumulator += Mathf.Min(Time.unscaledDeltaTime, 0.25f);
                }

                yield return WaitFor(() => confirmedPathId == path.Id, 2f, "b. the worker to confirm the route");
            }
            finally
            {
                hub.Movement.OwnerSimulationSnapshotReceived -= onSnapshot;
            }

            // Two owner snapshots half a second apart in one place mean the character stands.
            var settleDeadline = Time.realtimeSinceStartup + 5f;
            var previous = PositionOf(probe.LatestOwnerSnapshot);
            while (true)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                var current = PositionOf(probe.LatestOwnerSnapshot);
                if (Distance(previous, current) < 0.001f)
                {
                    standing = current;
                    yield break;
                }

                Assert.That(Time.realtimeSinceStartup, Is.LessThan(settleDeadline), "b. The character does not come to a stand.");
                previous = current;
            }
        }

        private static IEnumerator WaitFor(Func<bool> condition, float seconds, string what)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), "Waited " + seconds + " s for " + what + ".");
                yield return null;
            }
        }

        private static void AssertNoApiError(ApiError error, string what)
        {
            Assert.That(error, Is.Null, error != null ? what + " failed: " + error.ToDiagnostic() : null);
        }

        private static SimulationVector3 PositionOf(RealtimeOwnerSimulationSnapshot snapshot)
        {
            return new SimulationVector3(snapshot.State.PositionX, snapshot.State.PositionY, snapshot.State.PositionZ);
        }

        private static float Distance(SimulationVector3 first, SimulationVector3 second)
        {
            var x = first.X - second.X;
            var y = first.Y - second.Y;
            var z = first.Z - second.Z;
            return Mathf.Sqrt((x * x) + (y * y) + (z * z));
        }

        private static string Format(SimulationVector3 position)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:0.000}, {1:0.000}, {2:0.000})",
                position.X,
                position.Y,
                position.Z);
        }

        private static string Meters(float distance)
        {
            return distance.ToString("0.000", CultureInfo.InvariantCulture) + " m";
        }

        // The live stack script prints these lines as its result.
        private static void Report(string line)
        {
            Debug.Log("[LIVE] " + line);
        }
    }
}
