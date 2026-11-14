using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.App;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Tests.PlayMode
{
    // Measures how the running client copes with a crowd: it enters the stress arena, where every
    // player stays inside one interest radius, as a player does, waits until the client shows
    // the whole crowd of ActiveSimulationBots, and records the client measurements for a minute
    // with the default camera at the spawn. tools/Measure-ClientCrowd.ps1 starts the stack and
    // the bots and runs it, in a test player in a 1920 by 1080 window. Without the script's
    // variables it measures the crowd it finds, so a person can run it from the Test Runner of an
    // open Editor while the script keeps the bots up. It asserts only that the crowd appears and
    // that the whole crowd stays shown and the client stays joined while it records; no rule
    // judges the numbers, which it writes to a result file and prints as [CROWD] lines.
    //
    // Its category holds no other category's name, because the test runner matches a category
    // filter as a regular expression: LiveStack would select a LiveStackCrowd test.
    [Explicit("Needs the local stack with ActiveSimulationBots; tools/Measure-ClientCrowd.ps1 starts it.")]
    [Category("ClientCrowd")]
    public sealed class LiveStackCrowdPlayModeTests
    {
        private const string ShardId = "local-shard-2";
        private const string BotsVariable = "MMORPG_CROWD_BOTS";
        private const string SecondsVariable = "MMORPG_CROWD_SECONDS";
        private const string ResultsVariable = "MMORPG_CROWD_RESULTS";
        private const string LabelVariable = "MMORPG_CROWD_LABEL";

        private const int WindowWidth = 1920;
        private const int WindowHeight = 1080;

        // How long the crowd may take to appear, and how long a count without an expected size
        // must stay the same to count as the whole crowd.
        private const float CrowdTimeoutSeconds = 120f;
        private const float StableSeconds = 3f;

        private ApiClient api;

        private static string BaseUrl
        {
            get { return PlayModeClient.Session.Settings.GameServiceBaseUrl; }
        }

#if ENABLE_IL2CPP
        private const string Runtime = "IL2CPP";
#else
        private const string Runtime = "Mono";
#endif

        [UnityTest]
        [Timeout(1800000)]
        public IEnumerator TheClientShowsTheCrowdAndRecordsItsFrames()
        {
            var expected = int.Parse(Variable(BotsVariable, "0"), CultureInfo.InvariantCulture);
            var seconds = float.Parse(Variable(SecondsVariable, "60"), CultureInfo.InvariantCulture);
            var results = Variable(ResultsVariable, Application.temporaryCachePath);
            var label = Variable(LabelVariable, Application.isEditor ? "editor" : "player");
            if (!Application.isEditor)
            {
                Screen.SetResolution(WindowWidth, WindowHeight, FullScreenMode.Windowed);
                for (var frame = 0; frame < 10; frame++)
                {
                    yield return null;
                }
            }

            yield return PlayModeClient.WaitForContent();
            var bootstrap = Object.FindAnyObjectByType<ClientBootstrap>();
            var hub = bootstrap.GetComponent<RealtimeSimulationClient>();
            var measurements = bootstrap.Measurements;
            api = new ApiClient(10);

            // A crowd makes the client log what it measures, not failures.
            LogAssert.ignoreFailingMessages = true;
            try
            {
                var shard = default(ShardResponse);
                var character = default(CharacterResponse);
                yield return SignIn((joinedShard, created) =>
                {
                    shard = joinedShard;
                    character = created;
                });
                yield return EnterWorld(hub, shard, character);
                var shownStarted = Time.realtimeSinceStartup;
                var context = Object.FindAnyObjectByType<WorldSceneContext>();
                var crowd = 0;
                yield return WaitForTheWholeCrowd(context, expected, value => crowd = value);
                var shownSeconds = Time.realtimeSinceStartup - shownStarted;
                Report(label + ": " + crowd + " bots shown " + shownSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                    + " s after the World scene loaded; recording " + seconds.ToString("0", CultureInfo.InvariantCulture) + " s.");

                var result = new CrowdResult
                {
                    label = label,
                    runtime = Runtime,
                    platform = Application.isEditor ? "Editor Play Mode" : "player",
                    expectedBots = expected,
                    shownBots = crowd,
                    crowdShownSeconds = shownSeconds,
                    graphicsDevice = SystemInfo.graphicsDeviceName,
                    graphicsApi = SystemInfo.graphicsDeviceVersion,
                    processor = SystemInfo.processorType,
                    processorCount = SystemInfo.processorCount,
                    systemMemoryMegabytes = SystemInfo.systemMemorySize,
                    qualityLevel = QualitySettings.names[QualitySettings.GetQualityLevel()],
                    vSyncCount = QualitySettings.vSyncCount,
                    targetFrameRate = Application.targetFrameRate,
                    developmentBuild = Debug.isDebugBuild
                };

                result.recording = measurements.StartRecording("crowd-" + label, 5f);
                result.recordingStartUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                // The frames count for this crowd only while the whole crowd is shown, so the
                // smallest number of shown bots is looked up once a second.
                var fewestShown = crowd;
                var recordingEnds = Time.realtimeSinceStartup + seconds;
                var nextCount = Time.realtimeSinceStartup + 1f;
                while (Time.realtimeSinceStartup < recordingEnds)
                {
                    if (Time.realtimeSinceStartup >= nextCount)
                    {
                        nextCount += 1f;
                        fewestShown = Mathf.Min(fewestShown, ShownBots(context, out _));
                    }

                    yield return null;
                }

                result.summary = measurements.StopRecording();
                result.recordingEndUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                result.screenWidth = Screen.width;
                result.screenHeight = Screen.height;
                result.shownBotsAtEnd = ShownBots(context, out var knownAtEnd);
                result.knownBotsAtEnd = knownAtEnd;
                result.fewestShownBots = Mathf.Min(fewestShown, result.shownBotsAtEnd);
                result.ownerSnapshotsPerSecond = result.summary.durationSeconds > 0f
                    ? result.summary.ownerSnapshotFrames / result.summary.durationSeconds
                    : 0f;
                result.allocatedMegabytes = Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f);
                result.reservedMegabytes = Profiler.GetTotalReservedMemoryLong() / (1024f * 1024f);
                result.managedHeapMegabytes = Profiler.GetMonoUsedSizeLong() / (1024f * 1024f);
                WriteResult(results, result);
                ReportResult(result);

                Assert.That(hub.Session.IsJoined, Is.True, "The client stayed in the arena while it recorded.");
                Assert.That(
                    result.fewestShownBots,
                    Is.GreaterThanOrEqualTo(crowd),
                    "The crowd shrank while the client recorded: " + result.fewestShownBots + " of " + crowd
                    + " bots were shown at the least, so the frames are not those of the whole crowd.");
            }
            finally
            {
                if (measurements.IsRecording)
                {
                    measurements.StopRecording();
                }

                LogAssert.ignoreFailingMessages = false;
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

        private IEnumerator SignIn(Action<ShardResponse, CharacterResponse> signedIn)
        {
            AuthResponse account = null;
            CharacterResponse character = null;
            yield return LiveStackWalker.CreateAccountAndCharacter(api, BaseUrl, "Crowd", (value, created) =>
            {
                account = value;
                character = created;
            });

            ApiError error = null;
            ShardResponse[] shards = null;
            yield return api.GetShards(BaseUrl, value => shards = value, value => error = value);
            Assert.That(error, Is.Null, error != null ? "Listing the shards failed: " + error.ToDiagnostic() : null);
            var shard = Array.Find(shards, candidate => candidate.id == ShardId);
            Assert.That(shard, Is.Not.Null, "GameService lists no " + ShardId + ".");
            Assert.That(shard.isOnline, Is.True, ShardId + " is not online.");
            PlayModeClient.Session.SignIn(account);
            signedIn(shard, character);
        }

        // Joins as CharacterSelect does, through the client's own hub, and loads the World scene.
        private IEnumerator EnterWorld(RealtimeSimulationClient hub, ShardResponse shard, CharacterResponse character)
        {
            var session = PlayModeClient.Session;
            Assert.That(WorldSceneCatalog.TryResolveScene(shard.worldId, out var sceneName, out var sceneError), Is.True, sceneError);
            session.Select(character, shard);
            WorldJoinResult join = null;
            yield return new WorldJoin(session, api, hub.Session).Run(
                character.id,
                character.name,
                shard.id,
                ticket => true,
                value => join = value);
            Assert.That(join.Status, Is.EqualTo(WorldJoinStatus.Joined), "The join failed: " + join.Status);
            session.EnterWorld(join.Session);
            var load = ClientSceneLoader.LoadWorldScene(sceneName);
            yield return load;
            Assert.That(load.Status, Is.EqualTo(AsyncOperationStatus.Succeeded), "The World scene did not load.");
            var deadline = Time.realtimeSinceStartup + 15f;
            while (true)
            {
                var context = Object.FindAnyObjectByType<WorldSceneContext>();
                if (context != null && context.LocalPlayer != null && context.LocalPlayer.IsServerAuthoritative)
                {
                    yield break;
                }

                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), "Waited 15 s for the local player.");
                yield return null;
            }
        }

        // Waits until every bot the client knows has its body, and the client knows the expected
        // number of bots, or, without one, a number that stayed the same for a few seconds.
        private static IEnumerator WaitForTheWholeCrowd(WorldSceneContext context, int expected, Action<int> shown)
        {
            var deadline = Time.realtimeSinceStartup + CrowdTimeoutSeconds;
            var lastKnown = -1;
            var stableSince = Time.realtimeSinceStartup;
            while (true)
            {
                var withBody = ShownBots(context, out var known);
                if (known != lastKnown)
                {
                    lastKnown = known;
                    stableSince = Time.realtimeSinceStartup;
                }

                var whole = expected > 0
                    ? known >= expected
                    : known > 0 && Time.realtimeSinceStartup - stableSince >= StableSeconds;
                if (whole && withBody >= known)
                {
                    shown(withBody);
                    yield break;
                }

                Assert.That(
                    Time.realtimeSinceStartup,
                    Is.LessThan(deadline),
                    "Waited " + CrowdTimeoutSeconds + " s for the crowd: " + withBody + " shown of " + known + " known, "
                    + expected + " expected.");
                yield return new WaitForSecondsRealtime(0.1f);
            }
        }

        // The bots with a body, and in known every bot the client presents.
        private static int ShownBots(WorldSceneContext context, out int known)
        {
            known = 0;
            var withBody = 0;
            foreach (var view in context.RemotePlayers)
            {
                if (view == null || view.ArchetypeId != RealtimePlayerArchetypeIds.DevelopmentBot)
                {
                    continue;
                }

                known++;
                if (view.Body != null)
                {
                    withBody++;
                }
            }

            return withBody;
        }

        private static void WriteResult(string folder, CrowdResult result)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "crowd-" + result.label + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(result, true));
            Report("result file " + path);
        }

        private static void ReportResult(CrowdResult result)
        {
            var summary = result.summary;
            var invariant = CultureInfo.InvariantCulture;
            Report(string.Format(invariant, "{0} {1}, {2} by {3}, {4}, {5}", result.runtime, result.platform, result.screenWidth, result.screenHeight, result.graphicsDevice, result.processor));
            Report(string.Format(invariant, "  {0} bots shown in {1:0.0} s, {2} at the least while it recorded; frames p50/p95/p99/max {3:0.0}/{4:0.0}/{5:0.0}/{6:0.0} ms over {7:0} s",
                result.shownBots, result.crowdShownSeconds, result.fewestShownBots, summary.frameMilliseconds.p50, summary.frameMilliseconds.p95, summary.frameMilliseconds.p99, summary.frameMilliseconds.maximum, summary.durationSeconds));
            Report(string.Format(invariant, "  owner snapshots {0:0.0} per s, lost {1:0.00} %; remote starved {2:0.00} % in {3} starvations, longest {4:0.00} s",
                result.ownerSnapshotsPerSecond, summary.ownerSnapshotLossPercent, summary.remoteStarvedPercent, summary.remoteStarvations, summary.longestRemoteStarvationSeconds));
            Report(string.Format(invariant, "  memory allocated/reserved/managed {0:0}/{1:0}/{2:0} MB; vsync {3}, target frame rate {4}, quality {5}",
                result.allocatedMegabytes, result.reservedMegabytes, result.managedHeapMegabytes, result.vSyncCount, result.targetFrameRate, result.qualityLevel));
        }

        private static string Variable(string name, string fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static void Report(string line)
        {
            Debug.Log("[CROWD] " + line);
        }

        [Serializable]
        private sealed class CrowdResult
        {
            public string label;
            public string runtime;
            public string platform;
            public bool developmentBuild;
            public int expectedBots;
            public int shownBots;
            public float crowdShownSeconds;
            public int shownBotsAtEnd;
            public int knownBotsAtEnd;
            public int fewestShownBots;
            public int screenWidth;
            public int screenHeight;
            public string graphicsDevice;
            public string graphicsApi;
            public string processor;
            public int processorCount;
            public int systemMemoryMegabytes;
            public string qualityLevel;
            public int vSyncCount;
            public int targetFrameRate;
            public float ownerSnapshotsPerSecond;
            public float allocatedMegabytes;
            public float reservedMegabytes;
            public float managedHeapMegabytes;
            public string recording;
            public string recordingStartUtc;
            public string recordingEndUtc;
            public ClientMeasurementRecord summary;
        }
    }
}
