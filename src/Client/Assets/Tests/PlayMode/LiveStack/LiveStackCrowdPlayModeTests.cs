using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
using UnityEngine.Profiling;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Tests.PlayMode
{
    // Measures how the running client copes with a crowd in the stress arena, where every player
    // stays inside one interest radius. It enters the arena as a player does, with the default
    // camera at the spawn, and measures one of three kinds that MMORPG_CROWD_MODE names:
    //
    // - crowd, the default: the frames from standing in the arena until the client shows the
    //   whole crowd of ActiveSimulationBots, the arrival, then the client measurements for a
    //   minute with the whole crowd shown, the standing recording, and the memory. With
    //   MMORPG_CROWD_RETURN it then asks the script to stop the bots, reads the memory again once
    //   their bodies are gone, asks for the same bots again, and records their arrival and the
    //   memory a third time.
    // - churn: the frames for a minute while bots come and go, and the spawns and despawns the
    //   client saw.
    // - mobs: the arrival and the standing recording of walking Mobs of a World variant, without
    //   bots.
    //
    // tools/Measure-ClientCrowd.ps1 starts the stack, the bots, or the variant, runs it in a test
    // player in a 1920 by 1080 window, and answers its requests, files below MMORPG_CROWD_RESULTS.
    // Without the script's variables it measures the crowd of bots it finds, so a person can run
    // it from the Test Runner of an open Editor while the script keeps the bots up. It asserts
    // only that the crowd appears, stays, and comes back as asked, and that the client stays
    // joined; no rule judges the numbers, which it writes to a result file and prints as [CROWD]
    // lines.
    //
    // The frame in which the World scene became active is its load, not its arrival: the arrival
    // records from the first frame that began after it, and the activation frame's own length is a
    // value of the result.
    //
    // Its category holds no other category's name, because the test runner matches a category
    // filter as a regular expression: LiveStack would select a LiveStackCrowd test.
    [Explicit("Needs the local stack with ActiveSimulationBots; tools/Measure-ClientCrowd.ps1 starts it.")]
    [Category("ClientCrowd")]
    public sealed class LiveStackCrowdPlayModeTests
    {
        private const string ShardId = "local-shard-2";
        private const string ModeVariable = "MMORPG_CROWD_MODE";
        private const string BotsVariable = "MMORPG_CROWD_BOTS";
        private const string SecondsVariable = "MMORPG_CROWD_SECONDS";
        private const string ResultsVariable = "MMORPG_CROWD_RESULTS";
        private const string LabelVariable = "MMORPG_CROWD_LABEL";
        private const string StandingVariable = "MMORPG_CROWD_STANDING";
        private const string ReturnVariable = "MMORPG_CROWD_RETURN";
        private const string MobsVariable = "MMORPG_CROWD_MOBS";
        private const string MobActorVariable = "MMORPG_CROWD_MOB_ACTOR";

        private const string CrowdMode = "crowd";
        private const string ChurnMode = "churn";
        private const string MobsMode = "mobs";
        private const string DefaultMobActor = "mob.grey_wolf";

        // The requests to the script, files named crowd-<label>-<request>.request, which it
        // answers with a .done file of the same name.
        private const string DepartRequest = "depart";
        private const string ReturnRequest = "return";

        private const int WindowWidth = 1920;
        private const int WindowHeight = 1080;

        // How long the crowd may take to appear, and how long a count without an expected size
        // must stay the same to count as the whole crowd.
        private const float CrowdTimeoutSeconds = 120f;
        private const float StableSeconds = 3f;

        // How long the bodies of stopped bots may take to go: the bots leave within 15 s, and a
        // body stays 20 s after its leave. How long the first bot of a crowd that comes back may
        // take, from the start of its process. How long the client settles before the memory of
        // a crowd that left or came back is read.
        private const float DepartTimeoutSeconds = 90f;
        private const float FirstReturnTimeoutSeconds = 60f;
        private const float SettleSeconds = 5f;

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
            var mode = Variable(ModeVariable, CrowdMode);
            Assert.That(new[] { CrowdMode, ChurnMode, MobsMode }, Does.Contain(mode), ModeVariable + " names no kind of measurement.");
            var expected = int.Parse(Variable(BotsVariable, "0"), CultureInfo.InvariantCulture);
            var expectedMobs = int.Parse(Variable(MobsVariable, "0"), CultureInfo.InvariantCulture);
            var seconds = float.Parse(Variable(SecondsVariable, "60"), CultureInfo.InvariantCulture);
            var results = Variable(ResultsVariable, Application.temporaryCachePath);
            var label = Variable(LabelVariable, Application.isEditor ? "editor" : "player");
            var standing = Variable(StandingVariable, "1") != "0";
            var comeBack = Variable(ReturnVariable, "0") == "1";
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
                // Every kind but the churn records the arrival from the frame after the activation.
                var activation = new Activation(mode != ChurnMode ? measurements : null, label);
                yield return EnterWorld(hub, shard, character, activation);
                var result = new CrowdResult
                {
                    label = label,
                    mode = mode,
                    runtime = Runtime,
                    platform = Application.isEditor ? "Editor Play Mode" : "player",
                    expectedBots = expected,
                    expectedMobs = expectedMobs,
                    mobActorId = Variable(MobActorVariable, DefaultMobActor),
                    graphicsDevice = SystemInfo.graphicsDeviceName,
                    graphicsApi = SystemInfo.graphicsDeviceVersion,
                    processor = SystemInfo.processorType,
                    processorCount = SystemInfo.processorCount,
                    systemMemoryMegabytes = SystemInfo.systemMemorySize,
                    qualityLevel = QualitySettings.names[QualitySettings.GetQualityLevel()],
                    vSyncCount = QualitySettings.vSyncCount,
                    targetFrameRate = Application.targetFrameRate,
                    developmentBuild = Debug.isDebugBuild,
                    arrival = new CrowdStretch(),
                    returnArrival = new CrowdStretch()
                };
                var run = new Run
                {
                    Context = Object.FindAnyObjectByType<WorldSceneContext>(),
                    Actors = Object.FindAnyObjectByType<WorldActorPresentationController>(),
                    Hub = hub,
                    Measurements = measurements,
                    Result = result,
                    Results = results,
                    Label = label,
                    Activation = activation
                };

                if (mode == ChurnMode)
                {
                    yield return MeasureChurn(run, expected, seconds);
                }
                else if (mode == MobsMode)
                {
                    yield return MeasureMobs(run, expectedMobs, seconds);
                }
                else
                {
                    yield return MeasureCrowd(run, expected, seconds, standing, comeBack);
                }

                result.screenWidth = Screen.width;
                result.screenHeight = Screen.height;
                result.memory = run.Memory.ToArray();
                WriteResult(results, result);
                ReportResult(result);

                Assert.That(hub.Session.IsJoined, Is.True, "The client stayed in the arena while it recorded.");
                if (mode == ChurnMode)
                {
                    Assert.That(
                        result.churnSpawns > 0 && result.churnDespawns > 0,
                        Is.True,
                        "No bot came or went while the client recorded: " + result.churnSpawns + " spawns, "
                        + result.churnDespawns + " despawns.");
                }
                else if (mode == MobsMode)
                {
                    Assert.That(
                        result.fewestShownMobs,
                        Is.GreaterThanOrEqualTo(result.shownMobs),
                        "Mobs left while the client recorded: " + result.fewestShownMobs + " of " + result.shownMobs
                        + " were shown at the least, so the frames are not those of every Mob.");
                }
                else if (standing)
                {
                    Assert.That(
                        result.fewestShownBots,
                        Is.GreaterThanOrEqualTo(result.shownBots),
                        "The crowd shrank while the client recorded: " + result.fewestShownBots + " of " + result.shownBots
                        + " bots were shown at the least, so the frames are not those of the whole crowd.");
                }
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

        // The arrival of the crowd that the client walks into, a minute of it standing, and with
        // comeBack its departure and its return.
        private static IEnumerator MeasureCrowd(Run run, int expected, float seconds, bool standing, bool comeBack)
        {
            var result = run.Result;
            var crowd = 0;
            yield return RecordFirstArrival(run, result.arrival, WaitForTheWholeCrowd(run.Context, expected, value => crowd = value));
            result.shownBots = crowd;
            result.crowdShownSeconds = result.arrival.shownSeconds;
            Report(run.Label + ": " + crowd + " bots shown " + Seconds(result.crowdShownSeconds) + " s after the World scene loaded.");

            if (standing)
            {
                var fewest = crowd;
                yield return RecordStanding(run, "crowd", seconds, () => ShownBots(run.Context, out _), value => fewest = value);
                result.shownBotsAtEnd = ShownBots(run.Context, out var knownAtEnd);
                result.knownBotsAtEnd = knownAtEnd;
                result.fewestShownBots = Mathf.Min(crowd, Mathf.Min(fewest, result.shownBotsAtEnd));
            }
            else
            {
                yield return new WaitForSecondsRealtime(SettleSeconds);
            }

            ReadMemory(run, "with the crowd", true);
            if (!comeBack)
            {
                yield break;
            }

            // The script stops the bots; they leave, and their bodies go 20 s later.
            Request(run, DepartRequest);
            var departed = Time.realtimeSinceStartup;
            var deadline = departed + DepartTimeoutSeconds;
            while (true)
            {
                ShownBots(run.Context, out var known);
                if (known == 0)
                {
                    break;
                }

                Assert.That(
                    Time.realtimeSinceStartup,
                    Is.LessThan(deadline),
                    "Waited " + DepartTimeoutSeconds + " s for the bodies of the stopped bots to go: " + known + " still known. "
                    + Answer(run, DepartRequest));
                yield return new WaitForSecondsRealtime(0.1f);
            }

            result.departedSeconds = Time.realtimeSinceStartup - departed;
            Report(run.Label + ": every body gone " + Seconds(result.departedSeconds) + " s after the bots were asked to stop.");
            yield return new WaitForSecondsRealtime(SettleSeconds);
            ReadMemory(run, "crowd gone", false);

            // The script starts the same number of bots again; the arrival counts from the first.
            Request(run, ReturnRequest);
            deadline = Time.realtimeSinceStartup + FirstReturnTimeoutSeconds;
            while (true)
            {
                ShownBots(run.Context, out var known);
                if (known > 0)
                {
                    break;
                }

                Assert.That(
                    Time.realtimeSinceStartup,
                    Is.LessThan(deadline),
                    "Waited " + FirstReturnTimeoutSeconds + " s for the first bot of the crowd that comes back. " + Answer(run, ReturnRequest));
                yield return null;
            }

            var returned = 0;
            yield return RecordArrival(run, "return", result.returnArrival, WaitForTheWholeCrowd(run.Context, expected, value => returned = value));
            Report(run.Label + ": " + returned + " bots shown again " + Seconds(result.returnArrival.shownSeconds) + " s after the first came back.");
            yield return new WaitForSecondsRealtime(SettleSeconds);
            ReadMemory(run, "crowd back", false);
        }

        // A minute of bots that come and go, with the spawns and despawns of bots the client saw.
        private static IEnumerator MeasureChurn(Run run, int minimum, float seconds)
        {
            var result = run.Result;
            var crowd = 0;
            yield return WaitForTheWholeCrowd(run.Context, minimum, value => crowd = value);
            result.shownBots = crowd;

            var bots = new HashSet<ulong>();
            foreach (var view in run.Context.RemotePlayers)
            {
                if (view != null && view.ArchetypeId == RealtimePlayerArchetypeIds.DevelopmentBot)
                {
                    bots.Add(view.NetworkEntityId);
                }
            }

            var spawns = 0;
            var despawns = 0;
            Action<RealtimeEntitySpawn> spawned = spawn =>
            {
                if (spawn.Kind == RealtimeEntityKind.Player
                    && spawn.ArchetypeId == RealtimePlayerArchetypeIds.DevelopmentBot
                    && bots.Add(spawn.EntityId))
                {
                    spawns++;
                }
            };
            Action<RealtimeEntityDespawn> despawned = despawn =>
            {
                if (bots.Remove(despawn.EntityId))
                {
                    despawns++;
                }
            };
            var fewest = int.MaxValue;
            var most = 0;
            run.Hub.Players.EntitySpawned += spawned;
            run.Hub.Players.EntityDespawned += despawned;
            try
            {
                yield return RecordStanding(
                    run,
                    "churn",
                    seconds,
                    () =>
                    {
                        ShownBots(run.Context, out var known);
                        most = Mathf.Max(most, known);
                        return known;
                    },
                    value => fewest = value);
            }
            finally
            {
                run.Hub.Players.EntitySpawned -= spawned;
                run.Hub.Players.EntityDespawned -= despawned;
            }

            var duration = Mathf.Max(0.001f, result.summary.durationSeconds);
            result.churnSpawns = spawns;
            result.churnDespawns = despawns;
            result.spawnsPerSecond = spawns / duration;
            result.despawnsPerSecond = despawns / duration;
            result.fewestKnownBots = fewest == int.MaxValue ? 0 : fewest;
            result.mostKnownBots = most;
            ReadMemory(run, "while bots come and go", true);
        }

        // The arrival of the walking Mobs of a World variant and a minute with all of them shown.
        private static IEnumerator MeasureMobs(Run run, int expected, float seconds)
        {
            var result = run.Result;
            Assert.That(run.Actors, Is.Not.Null, "The World scene has no world actor presentation.");
            var shown = 0;
            yield return RecordFirstArrival(run, result.arrival, WaitForTheMobs(run, expected, value => shown = value));
            result.shownMobs = shown;
            result.crowdShownSeconds = result.arrival.shownSeconds;
            Report(run.Label + ": " + shown + " Mobs shown " + Seconds(result.crowdShownSeconds) + " s after the World scene loaded.");

            var fewest = shown;
            yield return RecordStanding(run, "mobs", seconds, () => ShownMobs(run), value => fewest = value);
            result.fewestShownMobs = Mathf.Min(shown, Mathf.Min(fewest, ShownMobs(run)));
            ReadMemory(run, "with the Mobs", true);
        }

        // The first arrival records from the first frame that began after the World scene became
        // active, which the activation's watch started the recording at, until the wait ends.
        private static IEnumerator RecordFirstArrival(Run run, CrowdStretch stretch, IEnumerator wait)
        {
            var activation = run.Activation;
            var deadline = Time.realtimeSinceStartup + 15f;
            while (!activation.Recording)
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), "The arrival's recording never started after the World scene became active.");
                yield return null;
            }

            run.Result.activationFrameMilliseconds = activation.Milliseconds;
            stretch.recording = activation.RecordingPath;
            stretch.startUtc = activation.StartUtc;
            yield return wait;
            stretch.summary = run.Measurements.StopRecording();
            stretch.endUtc = Utc();
            stretch.shownSeconds = Time.realtimeSinceStartup - activation.StartTime;
            stretch.measured = true;
        }

        // Records from now until the wait ends.
        private static IEnumerator RecordArrival(Run run, string name, CrowdStretch stretch, IEnumerator wait)
        {
            stretch.recording = run.Measurements.StartRecording(name + "-" + run.Label, 5f);
            stretch.startUtc = Utc();
            var started = Time.realtimeSinceStartup;
            yield return wait;
            stretch.summary = run.Measurements.StopRecording();
            stretch.endUtc = Utc();
            stretch.shownSeconds = Time.realtimeSinceStartup - started;
            stretch.measured = true;
        }

        // Records for the given seconds into the result's main recording, and looks up the count
        // once a second, whose smallest value it reports.
        private static IEnumerator RecordStanding(Run run, string name, float seconds, Func<int> count, Action<int> fewest)
        {
            var result = run.Result;
            result.recording = run.Measurements.StartRecording(name + "-" + run.Label, 5f);
            result.recordingStartUtc = Utc();
            Report(run.Label + ": recording " + seconds.ToString("0", CultureInfo.InvariantCulture) + " s.");
            var smallest = int.MaxValue;
            var recordingEnds = Time.realtimeSinceStartup + seconds;
            var nextCount = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < recordingEnds)
            {
                if (Time.realtimeSinceStartup >= nextCount)
                {
                    nextCount += 1f;
                    smallest = Mathf.Min(smallest, count());
                }

                yield return null;
            }

            result.summary = run.Measurements.StopRecording();
            result.recordingEndUtc = Utc();
            result.standing = true;
            result.ownerSnapshotsPerSecond = result.summary.durationSeconds > 0f
                ? result.summary.ownerSnapshotFrames / result.summary.durationSeconds
                : 0f;
            fewest(Mathf.Min(smallest, count()));
        }

        // The memory now, the bots the client knows, and the entries of its body caches and body
        // content. The first row is also the result's memory.
        private static void ReadMemory(Run run, string moment, bool isResultMemory)
        {
            ShownBots(run.Context, out var known);
            var memory = new CrowdMemory
            {
                moment = moment,
                utc = Utc(),
                knownBots = known,
                allocatedMegabytes = Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f),
                reservedMegabytes = Profiler.GetTotalReservedMemoryLong() / (1024f * 1024f),
                managedHeapMegabytes = Profiler.GetMonoUsedSizeLong() / (1024f * 1024f),
                bodyMeshes = run.Context.BodyMeshCount,
                bodyMaterials = run.Context.BodyMaterialCount,
                bodyGarments = run.Context.BodyGarmentCount,
                bodyAttachments = run.Context.BodyAttachmentCount
            };
            run.Memory.Add(memory);
            if (isResultMemory)
            {
                run.Result.allocatedMegabytes = memory.allocatedMegabytes;
                run.Result.reservedMegabytes = memory.reservedMegabytes;
                run.Result.managedHeapMegabytes = memory.managedHeapMegabytes;
            }
        }

        private static void Request(Run run, string request)
        {
            Directory.CreateDirectory(run.Results);
            File.WriteAllText(RequestPath(run, request, ".request"), Utc());
            Report(run.Label + ": asked the script to " + (request == DepartRequest ? "stop the bots." : "start the bots again."));
        }

        // Whether the script answered a request, for the message of a wait that timed out.
        private static string Answer(Run run, string request)
        {
            return File.Exists(RequestPath(run, request, ".done"))
                ? "The script answered the request."
                : "The script never answered the request; tools/Measure-ClientCrowd.ps1 answers it.";
        }

        private static string RequestPath(Run run, string request, string extension)
        {
            return Path.Combine(run.Results, "crowd-" + run.Label + "-" + request + extension);
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

        // Joins as CharacterSelect does, through the client's own hub, and loads the World scene,
        // whose activation the watch measures.
        private IEnumerator EnterWorld(RealtimeSimulationClient hub, ShardResponse shard, CharacterResponse character, Activation activation)
        {
            var session = PlayModeClient.Session;
            Assert.That(WorldSceneCatalog.TryResolveScene(shard.worldId, out var sceneName, out var sceneError), Is.True, sceneError);
            UnityEngine.Events.UnityAction<Scene, LoadSceneMode> loaded = (scene, mode) =>
            {
                if (scene.name == sceneName && activation.Frame < 0)
                {
                    activation.Frame = Time.frameCount;
                    hub.StartCoroutine(activation.Watch());
                }
            };
            SceneManager.sceneLoaded += loaded;
            try
            {
                yield return Join(hub, shard, character, session, sceneName);
            }
            finally
            {
                SceneManager.sceneLoaded -= loaded;
            }

            Assert.That(activation.Frame, Is.GreaterThanOrEqualTo(0), "The World scene never became active.");
        }

        // The join through the client's own hub and the load of the World scene, until the local
        // player stands in it.
        private IEnumerator Join(RealtimeSimulationClient hub, ShardResponse shard, CharacterResponse character, ClientSession session, string sceneName)
        {
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

        // Waits until the client shows the expected number of the variant's Mobs, or, without
        // one, a number that stayed the same for a few seconds.
        private static IEnumerator WaitForTheMobs(Run run, int expected, Action<int> shown)
        {
            var deadline = Time.realtimeSinceStartup + CrowdTimeoutSeconds;
            var lastShown = -1;
            var stableSince = Time.realtimeSinceStartup;
            while (true)
            {
                var mobs = ShownMobs(run);
                if (mobs != lastShown)
                {
                    lastShown = mobs;
                    stableSince = Time.realtimeSinceStartup;
                }

                var whole = expected > 0
                    ? mobs >= expected
                    : mobs > 0 && Time.realtimeSinceStartup - stableSince >= StableSeconds;
                if (whole)
                {
                    shown(mobs);
                    yield break;
                }

                Assert.That(
                    Time.realtimeSinceStartup,
                    Is.LessThan(deadline),
                    "Waited " + CrowdTimeoutSeconds + " s for the Mobs: " + mobs + " " + run.Result.mobActorId + " shown, "
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

        // The Mobs of the variant's actor that the client presents; a view exists once the Mob's
        // prefab is ready.
        private static int ShownMobs(Run run)
        {
            var shown = 0;
            foreach (var view in run.Actors.Views)
            {
                if (view is MobView mob && mob != null
                    && string.Equals(mob.ActorDefinitionId, run.Result.mobActorId, StringComparison.Ordinal))
                {
                    shown++;
                }
            }

            return shown;
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
            var invariant = CultureInfo.InvariantCulture;
            Report(string.Format(invariant, "{0} {1}, {2} by {3}, {4}, {5}, {6}", result.runtime, result.platform, result.screenWidth, result.screenHeight, result.graphicsDevice, result.processor, result.mode));
            if (result.arrival.measured)
            {
                Report(string.Format(invariant, "  World activation frame {0:0.0} ms, the load before the arrival", result.activationFrameMilliseconds));
            }

            ReportStretch("arrival", result.arrival);
            if (result.standing)
            {
                var summary = result.summary;
                Report(string.Format(invariant, "  {0} bots and {1} Mobs shown, {2} bots and {3} Mobs at the least while it recorded; frames p50/p95/p99/max {4:0.0}/{5:0.0}/{6:0.0}/{7:0.0} ms over {8:0} s, {9} over 33.3 ms",
                    result.shownBots, result.shownMobs, result.fewestShownBots, result.fewestShownMobs, summary.frameMilliseconds.p50, summary.frameMilliseconds.p95, summary.frameMilliseconds.p99, summary.frameMilliseconds.maximum, summary.durationSeconds, summary.hitchFrames));
                Report(string.Format(invariant, "  owner snapshots {0:0.0} per s, lost {1:0.00} %; remote starved {2:0.00} % in {3} starvations, longest {4:0.00} s",
                    result.ownerSnapshotsPerSecond, summary.ownerSnapshotLossPercent, summary.remoteStarvedPercent, summary.remoteStarvations, summary.longestRemoteStarvationSeconds));
            }

            if (result.mode == ChurnMode)
            {
                Report(string.Format(invariant, "  churn: {0} spawns and {1} despawns, {2:0.00} and {3:0.00} per s; {4} to {5} bots known",
                    result.churnSpawns, result.churnDespawns, result.spawnsPerSecond, result.despawnsPerSecond, result.fewestKnownBots, result.mostKnownBots));
            }

            ReportStretch("return", result.returnArrival);
            foreach (var memory in result.memory)
            {
                Report(string.Format(invariant, "  memory {0}: allocated/reserved/managed {1:0}/{2:0}/{3:0} MB, {4} bots known, body caches {5} meshes and {6} materials, body content {7} garments and {8} attachments",
                    memory.moment, memory.allocatedMegabytes, memory.reservedMegabytes, memory.managedHeapMegabytes, memory.knownBots, memory.bodyMeshes, memory.bodyMaterials,
                    memory.bodyGarments, memory.bodyAttachments));
            }

            Report(string.Format(invariant, "  vsync {0}, target frame rate {1}, quality {2}", result.vSyncCount, result.targetFrameRate, result.qualityLevel));
        }

        private static void ReportStretch(string name, CrowdStretch stretch)
        {
            if (stretch == null || !stretch.measured)
            {
                return;
            }

            var frames = stretch.summary.frameMilliseconds;
            Report(string.Format(CultureInfo.InvariantCulture, "  {0}: shown after {1:0.0} s; longest frame {2:0.0} ms, {3} over 33.3 ms and {4} over 16.7 ms; frames p50/p95/p99 {5:0.0}/{6:0.0}/{7:0.0} ms",
                name, stretch.shownSeconds, frames.maximum, stretch.summary.hitchFrames, stretch.summary.slowFrames, frames.p50, frames.p95, frames.p99));
        }

        private static string Variable(string name, string fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string Utc()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        private static string Seconds(float seconds)
        {
            return seconds.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static void Report(string line)
        {
            Debug.Log("[CROWD] " + line);
        }

        // What one measurement works with and fills.
        private sealed class Run
        {
            public readonly List<CrowdMemory> Memory = new List<CrowdMemory>();
            public WorldSceneContext Context;
            public WorldActorPresentationController Actors;
            public RealtimeSimulationClient Hub;
            public ClientMeasurements Measurements;
            public CrowdResult Result;
            public string Results;
            public string Label;
            public Activation Activation;
        }

        // The frame in which the World scene became active: its length, the time that frame takes
        // as the next one begins, and the arrival's recording, which starts in the frame after
        // that, so its first frame is the first that began after the activation.
        private sealed class Activation
        {
            private readonly ClientMeasurements measurements;
            private readonly string label;

            public Activation(ClientMeasurements arrivalMeasurements, string runLabel)
            {
                measurements = arrivalMeasurements;
                label = runLabel;
            }

            public int Frame { get; set; } = -1;

            public float Milliseconds { get; private set; }

            public bool Recording { get; private set; }

            public string RecordingPath { get; private set; }

            public string StartUtc { get; private set; }

            public float StartTime { get; private set; }

            public IEnumerator Watch()
            {
                yield return null;
                Milliseconds = Time.unscaledDeltaTime * 1000f;
                yield return null;
                if (measurements == null)
                {
                    yield break;
                }

                RecordingPath = measurements.StartRecording("arrival-" + label, 5f);
                StartUtc = Utc();
                StartTime = Time.realtimeSinceStartup;
                Recording = true;
            }
        }

        // The result file. The fields of the standing recording, recording to summary, are those
        // of the main recording of every kind: a minute with the whole crowd, the churn, or the
        // Mobs.
        [Serializable]
        private sealed class CrowdResult
        {
            public string label;
            public string mode;
            public string runtime;
            public string platform;
            public bool developmentBuild;
            public int expectedBots;
            public int shownBots;
            public float crowdShownSeconds;
            // The length of the frame in which the World scene became active, its load, which the
            // arrival does not count.
            public float activationFrameMilliseconds;
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
            public bool standing;
            public string recording;
            public string recordingStartUtc;
            public string recordingEndUtc;
            public ClientMeasurementRecord summary;
            public CrowdStretch arrival;
            public float departedSeconds;
            public CrowdStretch returnArrival;
            public CrowdMemory[] memory;
            public int churnSpawns;
            public int churnDespawns;
            public float spawnsPerSecond;
            public float despawnsPerSecond;
            public int fewestKnownBots;
            public int mostKnownBots;
            public string mobActorId;
            public int expectedMobs;
            public int shownMobs;
            public int fewestShownMobs;
        }

        // A recording while a crowd comes into view, until it is shown.
        [Serializable]
        private sealed class CrowdStretch
        {
            public bool measured;
            public float shownSeconds;
            public string recording;
            public string startUtc;
            public string endUtc;
            public ClientMeasurementRecord summary;
        }

        // The memory at one moment, with the bots the client knew and its body caches.
        [Serializable]
        private sealed class CrowdMemory
        {
            public string moment;
            public string utc;
            public int knownBots;
            public float allocatedMegabytes;
            public float reservedMegabytes;
            public float managedHeapMegabytes;
            public int bodyMeshes;
            public int bodyMaterials;
            public int bodyGarments;
            public int bodyAttachments;
        }
    }
}
