using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.App;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.WorldActors;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Tests.PlayMode
{
    // Plays the running client on the network that tools/Test-LiveStack.ps1 puts between it and
    // the worker: the network part of the live stack test. It enters the default World as a
    // player does, through the client's own hub, World scene, local player, and presentation,
    // records with the client measurements, walks a route with turns, stops, walks back, opens
    // and closes an NPC, and leaves and comes back several times, while a second connection on
    // the worker's own port walks beside it as another player to watch. It asserts only what
    // must hold on any network and writes everything it measured to a result file per profile.
    [Explicit("Needs the local stack; tools/Test-LiveStack.ps1 starts it and runs this test.")]
    [Category("LiveStack")]
    [Category("LiveStackNetwork")]
    public sealed class LiveStackNetworkPlayModeTests
    {
        private const string ShardId = "local-shard-1";
        private const string NetworkVariable = "MMORPG_LIVE_STACK_NETWORK";
        private const string WalkSecondsVariable = "MMORPG_LIVE_STACK_WALK_SECONDS";
        private const string LeavesVariable = "MMORPG_LIVE_STACK_LEAVES";
        private const string ResultsVariable = "MMORPG_LIVE_STACK_RESULTS";
        private const string BoundsVariable = "MMORPG_LIVE_STACK_BOUNDS";
        private const string WorkerRuntimeVariable = "MMORPG_LIVE_STACK_WORKER_RUNTIME_ID";
        private const string DirectPortVariable = "MMORPG_LIVE_STACK_DIRECT_UDP_PORT";

        // The NPC with the town's services, Mira, whose interaction is the reliable intent.
        private const string ServicesActorDefinition = "npc.city_services";
        private const int InteractionCycles = 5;

        // The development teleport of the snap check, from where the player joined, and how
        // much of it a frame's walk may explain.
        private static readonly Vector3 SnapOffset = new Vector3(6f, 0f, -6f);
        private const float SnapAllowance = 0.5f;

        // After standing still this long, the presented player stands within this of the worker's.
        private const float StandingSeconds = 2f;
        private const float StandingTolerance = 0.5f;

        // The corners of the route around the spawn, which the planner walks around anything.
        private static readonly Vector2[] RouteCorners =
        {
            new Vector2(8f, 0f),
            new Vector2(8f, -8f),
            new Vector2(-8f, -8f),
            new Vector2(-8f, 0f)
        };

        private readonly List<string> exceptions = new List<string>();
        private readonly List<string> errors = new List<string>();
        private ApiClient api;
        private ClientBootstrap bootstrap;
        private RealtimeSimulationClient hub;
        private LiveStackProbe probe;
        private LiveStackWalker walker;
        private CharacterResponse character;
        private ShardResponse shard;
        private ActiveSimulationSessionResponse world;
        private string profile;
        private int unexpectedEnds;

        private static string BaseUrl
        {
            get { return PlayModeClient.Session.Settings.GameServiceBaseUrl; }
        }

        [UnityTest]
        [Timeout(1800000)]
        public IEnumerator TheClientPlaysOnTheNetworkOfItsProfile()
        {
            profile = Variable(NetworkVariable, "none");
            var walkSeconds = float.Parse(Variable(WalkSecondsVariable, "30"), CultureInfo.InvariantCulture);
            var leaves = int.Parse(Variable(LeavesVariable, "3"), CultureInfo.InvariantCulture);
            var results = Variable(ResultsVariable, Application.temporaryCachePath);
            var result = new LiveNetworkResult { profile = profile, walkSeconds = walkSeconds };

            yield return PlayModeClient.WaitForContent();
            bootstrap = Object.FindAnyObjectByType<ClientBootstrap>();
            hub = bootstrap.GetComponent<RealtimeSimulationClient>();
            var measurements = bootstrap.Measurements;
            api = new ApiClient(10);
            probe = new LiveStackProbe(hub);
            hub.Session.UnexpectedlyDisconnected += OnUnexpectedlyDisconnected;

            // A bad network makes the client log errors, which are measured here, not failures.
            LogAssert.ignoreFailingMessages = true;
            Application.logMessageReceived += OnLog;
            try
            {
                yield return CreateAccountAndCharacter();
                walker = new LiveStackWalker(api, BaseUrl, PlayModeClient.Session.Settings);
                yield return walker.JoinAndWalk(
                    ShardId,
                    Environment.GetEnvironmentVariable(WorkerRuntimeVariable),
                    int.Parse(Variable(DirectPortVariable, "0"), CultureInfo.InvariantCulture));
                yield return EnterWorld(result);
                yield return CheckThatASnapIsSeen(measurements, result);
                result.recording = measurements.StartRecording("live-" + profile, 5f);
                Report("network " + profile + ": joined " + world.characterName + "; recording the play to " + result.recording);

                // The play: no lost connection is expected on good, normal, and poor; on terrible
                // the client may lose it and must restore it by itself within its window.
                var player = LocalPlayer();
                var start = player.transform.position;
                yield return Walk(player, start, walkSeconds, result);
                yield return StandStill(player, result);
                yield return WalkTo(player, start, 20f);
                yield return OpenAndCloseTheServicesNpc(result);
                result.unexpectedEndsInPlay = unexpectedEnds;
                yield return AssertStillInWorld(result, "the play");
                result.summary = measurements.StopRecording();

                // Leaves: each ends at CharacterSelect, cleanly or not, and the character comes
                // back. Their own recording keeps the moments after each join apart from the play.
                result.leavesRecording = measurements.StartRecording("leaves-" + profile, 5f);
                for (var leave = 1; leave <= leaves; leave++)
                {
                    yield return LeaveAndReturn(leave, leave < leaves, result);
                }

                result.leavesSummary = measurements.StopRecording();
                result.walkerLegs = walker.Legs;
                result.walkerFailure = walker.Failure;
                yield return walker.Leave();
                result.unexpectedEnds = unexpectedEnds;
                result.exceptions = exceptions.Count;
                result.errors = errors.Count;
                result.distinctErrors = errors.Distinct().Take(5).ToList();
                WriteResult(results, result);
                ReportTable(result);

                Assert.That(exceptions, Is.Empty, "The client threw: " + string.Join(" | ", exceptions));
                Assert.That(result.walkerFailure, Is.Null.Or.Empty, "The other player stopped walking.");
                Assert.That(
                    result.snapLargestJumpMeters,
                    Is.GreaterThanOrEqualTo(result.snapMeters - SnapAllowance),
                    "The measurements saw the snap of the teleport as a jump.");
                Assert.That(
                    result.standingOffsetMeters,
                    Is.LessThanOrEqualTo(StandingTolerance),
                    "After standing " + StandingSeconds + " s the player stands where the worker has it.");
                if (profile != "terrible")
                {
                    Assert.That(result.unexpectedEndsInPlay, Is.Zero, "The client left its World by itself under " + profile + ".");
                }

                AssertBounds(result.summary);
            }
            finally
            {
                if (measurements.IsRecording)
                {
                    measurements.StopRecording();
                }

                Application.logMessageReceived -= OnLog;
                LogAssert.ignoreFailingMessages = false;
                hub.Session.UnexpectedlyDisconnected -= OnUnexpectedlyDisconnected;
                probe.Dispose();
                walker?.Dispose();
                if (hub.Session.State != RealtimeConnectionState.Disconnected)
                {
                    hub.Session.Abort();
                }

                if (PlayModeClient.Session.ActiveSimulationSession != null)
                {
                    PlayModeClient.Session.LeaveWorld(PlayModeClient.Session.ActiveSimulationSession.simulationSessionId);
                }

                api.Dispose();
            }
        }

        private void OnLog(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Exception)
            {
                exceptions.Add(message);
            }
            else if (type == LogType.Error)
            {
                errors.Add(message);
            }
        }

        private void OnUnexpectedlyDisconnected(RealtimeClientError error)
        {
            unexpectedEnds++;
        }

        private IEnumerator CreateAccountAndCharacter()
        {
            AuthResponse account = null;
            yield return LiveStackWalker.CreateAccountAndCharacter(api, BaseUrl, "Net", (value, created) =>
            {
                account = value;
                character = created;
            });

            ApiError error = null;
            ShardResponse[] shards = null;
            yield return api.GetShards(BaseUrl, value => shards = value, value => error = value);
            AssertNoApiError(error, "listing the shards");
            shard = Array.Find(shards, candidate => candidate.id == ShardId);
            Assert.That(shard, Is.Not.Null, "GameService lists no " + ShardId + ".");
            Assert.That(shard.isOnline, Is.True, ShardId + " is not online.");

            PlayModeClient.Session.SignIn(account);
        }

        // Joins as CharacterSelect does, through the client's own hub, and loads the World scene.
        // A join that a bad network breaks is tried again, as a player clicks Enter World again.
        private IEnumerator EnterWorld(LiveNetworkResult result)
        {
            var session = PlayModeClient.Session;
            var expectedRuntime = Environment.GetEnvironmentVariable(WorkerRuntimeVariable);
            Assert.That(WorldSceneCatalog.TryResolveScene(shard.worldId, out var sceneName, out var sceneError), Is.True, sceneError);
            WorldJoinResult join = null;
            for (var attempt = 1; ; attempt++)
            {
                session.Select(character, shard);
                result.joinAttempts++;
                join = null;
                yield return new WorldJoin(session, api, hub.Session).Run(
                    character.id,
                    character.name,
                    shard.id,
                    ticket => string.IsNullOrEmpty(expectedRuntime) || ticket.endpoint.runtimeId == expectedRuntime,
                    value => join = value);
                if (join.Status == WorldJoinStatus.Joined)
                {
                    break;
                }

                result.joinFailures++;
                Report("join attempt " + attempt + " failed: " + join.Status + " "
                    + (join.JoinError != null ? join.JoinError.Code : join.TicketError != null ? join.TicketError.Code : string.Empty));
                Assert.That(attempt, Is.LessThan(5), "The character could not join in five attempts.");
                yield return new WaitForSecondsRealtime(2f);
            }

            world = join.Session;
            session.EnterWorld(world);
            var load = ClientSceneLoader.LoadWorldScene(sceneName);
            yield return load;
            Assert.That(load.Status, Is.EqualTo(AsyncOperationStatus.Succeeded), "The World scene did not load.");
            yield return WaitFor(
                () => LocalPlayerOrNull() != null && LocalPlayerOrNull().IsServerAuthoritative,
                15f,
                "the local player of the joined session");
        }

        private static LocalPlayerController LocalPlayerOrNull()
        {
            var context = Object.FindAnyObjectByType<WorldSceneContext>();
            return context != null ? context.LocalPlayer : null;
        }

        private static LocalPlayerController LocalPlayer()
        {
            var player = LocalPlayerOrNull();
            Assert.That(player, Is.Not.Null, "The World scene has no local player.");
            return player;
        }

        // Shows that the jump detector sees what a player sees: a development teleport moves the
        // character by SnapOffset at once, the presentation shows it as a snap, and a recording
        // of its own finds that snap in the presented position.
        private IEnumerator CheckThatASnapIsSeen(ClientMeasurements measurements, LiveNetworkResult result)
        {
            yield return WaitFor(() => hub.Admin.AdminState != null, 15f, "the development worker's admin tools");
            var player = LocalPlayer();
            var from = player.transform.position;
            var destination = from + SnapOffset;
            measurements.StartRecording("snap-" + profile, 60f);
            var sent = hub.Admin.TrySendAdminIntent(
                RealtimeAdminIntent.CreateTeleport(Guid.NewGuid(), destination.x, destination.y + 0.05f, destination.z));
            Assert.That(sent, Is.True, "The teleport could not be sent.");
            yield return WaitFor(() => HorizontalDistance(player.transform.position, destination) <= 0.5f, 30f, "the teleport to show");
            yield return new WaitForSecondsRealtime(0.5f);
            var summary = measurements.StopRecording();
            result.snapMeters = HorizontalDistance(from, destination);
            result.snapLargestJumpMeters = summary.largestUnexplainedJumpMeters;
            result.snapHardSnaps = summary.hardSnaps;
            Report(string.Format(
                CultureInfo.InvariantCulture,
                "snap check: a teleport of {0:0.00} m showed as a jump of {1:0.00} m in one frame, hard snaps {2}",
                result.snapMeters,
                result.snapLargestJumpMeters,
                result.snapHardSnaps));
        }

        private static float HorizontalDistance(Vector3 first, Vector3 second)
        {
            return new Vector2(first.x - second.x, first.z - second.z).magnitude;
        }

        // Walks the corners of the route in turn until the time is up, a turn at every corner.
        private IEnumerator Walk(LocalPlayerController player, Vector3 start, float seconds, LiveNetworkResult result)
        {
            var end = Time.realtimeSinceStartup + seconds;
            var corner = 0;
            while (Time.realtimeSinceStartup < end)
            {
                var offset = RouteCorners[corner % RouteCorners.Length];
                yield return WalkTo(LocalPlayer(), start + new Vector3(offset.x, 0f, offset.y), 15f);
                corner++;
                result.legs++;
            }
        }

        // Walks until the player stands within a meter of the destination, or followed its route
        // to the end, or the time is up. The prediction takes the route on its next tick, so
        // the end of the route counts only after the player followed it.
        private static IEnumerator WalkTo(LocalPlayerController player, Vector3 destination, float timeoutSeconds)
        {
            var intent = player.MoveTo(destination);
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            var followed = false;
            yield return null;
            while (Time.realtimeSinceStartup < deadline && player != null)
            {
                var position = player.transform.position;
                var offset = new Vector2(position.x - destination.x, position.z - destination.z);
                if (offset.magnitude <= 1f)
                {
                    yield break;
                }

                var planned = player.AppliedNavigationIntentId == intent;
                if (planned && player.LatestNavigationResult == NavigationResult.NoRoute)
                {
                    yield break;
                }

                followed |= planned && player.IsFollowingPath;
                if (followed && !player.IsFollowingPath)
                {
                    yield break;
                }

                yield return null;
            }
        }

        // Stops, stands, and compares where the player is drawn with where the worker has it.
        private static IEnumerator StandStill(LocalPlayerController player, LiveNetworkResult result)
        {
            player.StopMoving();
            yield return WaitFor(() => !player.IsFollowingPath, 10f, "the player to stop");
            yield return new WaitForSecondsRealtime(StandingSeconds);
            Assert.That(player.TryGetAuthoritativePosition(out var authoritative), Is.True);
            result.standingOffsetMeters = Vector3.Distance(player.transform.position, authoritative);
            Report("stood " + StandingSeconds + " s: drawn " + Format(player.transform.position) + ", the worker's "
                + Format(authoritative) + ", " + result.standingOffsetMeters.ToString("0.000", CultureInfo.InvariantCulture) + " m apart");
        }

        // Walks to Mira and opens and closes her interaction, as the world menu does: reliable
        // intents with a result each, which the measurements time.
        private IEnumerator OpenAndCloseTheServicesNpc(LiveNetworkResult result)
        {
            var actors = bootstrap.GetComponent<WorldActorClientController>();
            var interactions = bootstrap.GetComponent<WorldInteractionClientController>();
            var player = LocalPlayer();
            var mira = actors.State.Actors
                .Where(actor => actor.ActorDefinitionId == ServicesActorDefinition && actor.IsActive)
                .OrderBy(actor => Vector3.Distance(player.transform.position, new Vector3(actor.PositionX, actor.PositionY, actor.PositionZ)))
                .FirstOrDefault();
            Assert.That(mira, Is.Not.Null, "The World shows no NPC with the town's services.");
            var target = new Vector3(mira.PositionX, mira.PositionY, mira.PositionZ);
            player.Approach(target, 1.5f);
            yield return WaitFor(() => Vector3.Distance(player.transform.position, target) <= 2.5f, 30f, "the player to reach Mira");
            yield return WaitFor(() => !player.IsFollowingPath, 10f, "the player to stop at Mira");
            for (var cycle = 0; cycle < InteractionCycles; cycle++)
            {
                result.interactionIntents++;
                if (!interactions.TryOpenActor(mira, string.Empty, out var openError))
                {
                    Report("open " + cycle + " not sent: " + openError);
                    yield return new WaitForSecondsRealtime(1f);
                    continue;
                }

                yield return WaitFor(() => interactions.State.PendingOperationId == Guid.Empty, 30f, "the open's result");
                if (interactions.State.ActiveInteraction == null)
                {
                    result.interactionRefusals++;
                    continue;
                }

                result.interactionIntents++;
                Assert.That(interactions.TryClose(out var closeError), Is.True, closeError);
                yield return WaitFor(
                    () => interactions.State.PendingOperationId == Guid.Empty && interactions.State.ActiveInteraction == null,
                    30f,
                    "the close's result and the closed interaction");
            }
        }

        private IEnumerator AssertStillInWorld(LiveNetworkResult result, string what)
        {
            if (profile == "terrible" && unexpectedEnds > 0)
            {
                // The reconnect has its window to restore the line by itself.
                var settings = PlayModeClient.Session.Settings;
                var deadline = Time.realtimeSinceStartup + settings.ReconnectWindowSeconds + 5f;
                while (!hub.Session.IsJoined && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                result.restoredAfterLoss = hub.Session.IsJoined;
            }

            Assert.That(
                WorldSceneCatalog.IsWorldScene(SceneManager.GetActiveScene().name) && hub.Session.IsJoined,
                Is.True,
                "After " + what + " the client is in its World and joined.");
        }

        // Leaves as the Debug menu's Leave Shard does, waits for the entry screen, and joins again.
        private IEnumerator LeaveAndReturn(int number, bool comeBack, LiveNetworkResult result)
        {
            RealtimeClientError error = null;
            var left = false;
            var started = Time.realtimeSinceStartup;
            yield return bootstrap.WorldExit.Leave(value => error = value, () => left = true);
            yield return WaitFor(
                () => SceneManager.GetActiveScene().name == SceneNames.CharacterSelect
                    || SceneManager.GetActiveScene().name == SceneNames.LoginMenu,
                30f,
                "leave " + number + " to end at an entry screen");
            result.leaves++;
            if (error == null && left)
            {
                result.cleanLeaves++;
            }
            else
            {
                result.leaveErrors.Add(error != null ? error.Code : "not_left");
            }

            Report("leave " + number + ": " + (error == null ? "clean" : error.Code) + " in "
                + (Time.realtimeSinceStartup - started).ToString("0.00", CultureInfo.InvariantCulture) + " s, now at "
                + SceneManager.GetActiveScene().name);
            Assert.That(PlayModeClient.Session.ActiveSimulationSession, Is.Null, "Leave " + number + " left the World.");
            if (!comeBack)
            {
                yield break;
            }

            yield return new WaitForSecondsRealtime(1f);
            yield return EnterWorld(result);
        }

        private static void AssertBounds(ClientMeasurementRecord summary)
        {
            var path = Environment.GetEnvironmentVariable(BoundsVariable);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            var bounds = JsonUtility.FromJson<LiveNetworkBoundsFile>(File.ReadAllText(path));
            var profileName = Variable(NetworkVariable, "none");
            var bound = bounds.profiles?.FirstOrDefault(entry => entry.profile == profileName);
            if (bound == null)
            {
                return;
            }

            Assert.That(summary.hardSnapsPerMinute, Is.LessThanOrEqualTo(bound.hardSnapsPerMinute), "Hard snaps per minute under " + profileName + ".");
            Assert.That(summary.correctionMeters.p95, Is.LessThanOrEqualTo(bound.correctionP95Meters), "The p95 correction under " + profileName + ".");
            Assert.That(summary.largestUnexplainedJumpMeters, Is.LessThanOrEqualTo(bound.largestUnexplainedJumpMeters), "The largest unexplained jump under " + profileName + ".");
        }

        private static void WriteResult(string folder, LiveNetworkResult result)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "live-network-" + result.profile + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(result, true));
            Report("result file " + path);
        }

        private static void ReportTable(LiveNetworkResult result)
        {
            var summary = result.summary;
            var invariant = CultureInfo.InvariantCulture;
            Report(string.Format(invariant, "profile {0}, the play: {1:0} s in the World, {2} legs in {3:0} s, standing offset {4:0.000} m", result.profile, summary.worldSeconds, result.legs, result.walkSeconds, result.standingOffsetMeters));
            Report(string.Format(invariant, "  round trip p50/p95 {0:0}/{1:0} ms, frames p95 {2:0.0} ms, owner snapshots lost {3:0.00} %, silences {4}, longest {5:0.00} s",
                summary.roundTripMilliseconds.p50, summary.roundTripMilliseconds.p95, summary.frameMilliseconds.p95, summary.ownerSnapshotLossPercent, summary.silences, summary.longestSilenceSeconds));
            Report(string.Format(invariant, "  corrections {0}, p50/p95/max {1:0.000}/{2:0.000}/{3:0.000} m, hard snaps {4} ({5:0.00} per minute)",
                summary.corrections, summary.correctionMeters.p50, summary.correctionMeters.p95, summary.correctionMeters.maximum, summary.hardSnaps, summary.hardSnapsPerMinute));
            Report(string.Format(invariant, "  largest unexplained jump {0:0.000} m, frames with a jump {1}; after a silence: correction {2:0.000} m, jump {3:0.000} m",
                summary.largestUnexplainedJumpMeters, summary.framesWithUnexplainedJump, summary.largestCorrectionAfterSilenceMeters, summary.largestJumpAfterSilenceMeters));
            Report(string.Format(invariant, "  remote: {0:0.0} entity-seconds seen, {1:0.0} starved ({2:0.00} %) in {3} starvations, longest {4:0.00} s; the other player walked {5} legs",
                summary.remoteObservedEntitySeconds, summary.remoteStarvedEntitySeconds, summary.remoteStarvedPercent, summary.remoteStarvations, summary.longestRemoteStarvationSeconds, result.walkerLegs));
            foreach (var intent in summary.intents)
            {
                Report(string.Format(invariant, "  intents {0}: {1} answered of {2}, p50/p95/max {3:0}/{4:0}/{5:0} ms",
                    intent.kind, intent.answered, intent.sent, intent.seconds.p50 * 1000f, intent.seconds.p95 * 1000f, intent.seconds.maximum * 1000f));
            }

            var leaves = result.leavesSummary;
            Report(string.Format(invariant, "  leaves {0}, clean {1}, ended otherwise: {2}; joins {3} with {4} failed; lost connections {5}; exceptions {6}; errors logged {7}{8}",
                result.leaves, result.cleanLeaves, result.leaveErrors.Count == 0 ? "none" : string.Join(", ", result.leaveErrors), result.joinAttempts, result.joinFailures, result.unexpectedEnds, result.exceptions, result.errors,
                result.errors == 0 ? string.Empty : ": " + string.Join(" | ", result.distinctErrors)));
            Report(string.Format(invariant, "  between the leaves: {0:0.0} s in the World, the other player starved {1:0.0} of {2:0.0} entity-seconds ({3:0.00} %), longest {4:0.00} s; largest jump {5:0.000} m, hard snaps {6}",
                leaves.worldSeconds, leaves.remoteStarvedEntitySeconds, leaves.remoteObservedEntitySeconds, leaves.remoteStarvedPercent, leaves.longestRemoteStarvationSeconds, leaves.largestUnexplainedJumpMeters, leaves.hardSnaps));
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

        private static string Variable(string name, string fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static void AssertNoApiError(ApiError error, string what)
        {
            Assert.That(error, Is.Null, error != null ? what + " failed: " + error.ToDiagnostic() : null);
        }

        private static string Format(Vector3 position)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:0.00}, {1:0.00}, {2:0.00})", position.x, position.y, position.z);
        }

        // The live stack script prints these lines as its result.
        private static void Report(string line)
        {
            Debug.Log("[LIVE] " + line);
        }

        [Serializable]
        private sealed class LiveNetworkResult
        {
            public string profile;
            public float walkSeconds;
            public int legs;
            public float standingOffsetMeters;
            public int interactionIntents;
            public int interactionRefusals;
            public int unexpectedEndsInPlay;
            public bool restoredAfterLoss;
            public int leaves;
            public int cleanLeaves;
            public List<string> leaveErrors = new List<string>();
            public int joinAttempts;
            public int joinFailures;
            public int unexpectedEnds;
            public int exceptions;
            public int errors;
            public List<string> distinctErrors = new List<string>();
            public float snapMeters;
            public float snapLargestJumpMeters;
            public int snapHardSnaps;
            public int walkerLegs;
            public string walkerFailure;
            public string recording;
            public ClientMeasurementRecord summary;
            public string leavesRecording;
            public ClientMeasurementRecord leavesSummary;
        }

        [Serializable]
        private sealed class LiveNetworkBoundsFile
        {
            public LiveNetworkBound[] profiles;
        }

        [Serializable]
        private sealed class LiveNetworkBound
        {
            public string profile;
            public float hardSnapsPerMinute;
            public float correctionP95Meters;
            public float largestUnexplainedJumpMeters;
        }
    }
}
