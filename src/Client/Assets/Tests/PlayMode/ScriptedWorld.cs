using System;
using System.Collections;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Tests.PlayMode
{
    // Development World 1 with a context that a scripted worker joined, ready to enter, and the
    // players the worker sends into view. The content loads through Addressables, which in the
    // Editor's asset database mode takes a simulated 0.1 s a load.
    internal sealed class ScriptedWorld : IDisposable
    {
        private const string WorldId = "development-world-1";
        private const string ShardId = "local-shard-1";
        private const string CharacterId = "00000000-0000-4000-8000-000000002003";
        private static readonly ClientSettings Settings = new ClientSettings("http://127.0.0.1:9", 10, 10, 5);

        private GameObject hubObject;
        private RealtimeSimulationClient hub;

        public WorldSceneContext Context { get; private set; }

        public ScriptedRealtimeTransport Transport { get; private set; }

        public IEnumerator Load(PresentationBudget budget)
        {
            PlayModeClient.Session.SignOut();
            yield return PlayModeClient.WaitForContent();
            var sceneLoad = ClientSceneLoader.LoadWorldScene(SceneNames.DevelopmentWorld1);
            yield return sceneLoad;
            Assert.That(sceneLoad.Status, Is.EqualTo(AsyncOperationStatus.Succeeded));
            yield return null;
            Context = Object.FindAnyObjectByType<WorldSceneContext>(FindObjectsInactive.Include);
            Assert.That(Context, Is.Not.Null);

            Transport = new ScriptedRealtimeTransport();
            hubObject = new GameObject("Scripted world hub");
            hubObject.SetActive(false);
            hub = hubObject.AddComponent<RealtimeSimulationClient>();
            hub.Initialize(Transport, () => Time.realtimeSinceStartup, Settings);
            hubObject.SetActive(true);
            var session = new ClientSession(Settings);
            session.SignIn(new AuthResponse { accountId = "account-2", username = "content_hero", sessionToken = "token" });
            var collisionRevision = CollisionRevision();
            Transport.NextJoinAccepted = Accepted(collisionRevision);
            ActiveSimulationSessionResponse joined = null;
            yield return hub.Session.Join(Placement(collisionRevision), value => joined = value, error => Assert.Fail(error.ToDiagnostic()));
            session.EnterWorld(joined);
            Context.Initialize(session, hub.Session, hub.Movement, hub.Players, hub.Looks, hub.Admin, new LocalPlayerSource(),
                new WorldPointerFocus(), null, null, null, budget);
            Context.enabled = true;
        }

        // Enters the World and waits until it shows.
        public IEnumerator Enter()
        {
            Assert.That(Context.TryEnterWorld(), Is.True);
            yield return Until(() => Context.IsWorldShown, "the World was never shown");
        }

        // The worker sends a player into view, standing at the position.
        public void Spawn(ulong entityId, RealtimeCharacterAppearance appearance, Vector3 position = default)
        {
            if (position == default)
            {
                position = new Vector3(2f, 0f, 2f);
            }

            var spawn = new RealtimeEntitySpawn(
                entityId,
                RealtimeEntityKind.Player,
                "00000000-0000-4000-8000-0000000020" + (entityId % 100UL).ToString("00"),
                "Visitor " + entityId,
                RealtimePlayerArchetypeIds.Player,
                10,
                new RealtimePlayerState(position.x, position.y, position.z, 0f, 0f, 0f, 0f, true),
                appearance);
            Transport.Deliver(RealtimeProtocol.EncodeEntitySpawn(spawn), RealtimeProtocol.WorldChannel);
        }

        // Waits until the player has its body.
        public IEnumerator UntilBody(ulong entityId, Action<RemotePlayerView> shown)
        {
            RemotePlayerView view = null;
            yield return Until(() => Context.TryGetRemotePlayer(entityId, out view) && view.Body != null,
                "the body of player " + entityId + " never showed");
            shown(view);
        }

        public void Dispose()
        {
            if (hub != null && hub.Session.State != RealtimeConnectionState.Disconnected)
            {
                hub.Session.Abort();
            }

            if (hubObject != null)
            {
                Object.Destroy(hubObject);
            }
        }

        public static RealtimeCharacterAppearance Appearance(params RealtimeAppearanceChoice[] choices)
        {
            return new RealtimeCharacterAppearance("Human_Male", choices, null, false, null);
        }

        public static RealtimeAppearanceChoice Choice(string kind, string option)
        {
            return new RealtimeAppearanceChoice(kind, string.Empty, option);
        }

        public static IEnumerator Until(Func<bool> condition, string message)
        {
            var deadline = Time.realtimeSinceStartup + 10f;
            while (!condition())
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), message);
                yield return null;
            }
        }

        private static string CollisionRevision()
        {
            Assert.That(UnityWorldCollisionLoader.TryCreateStream(WorldId, out var stream, out var error), Is.True, error);
            return stream.CollisionWorld.Revision;
        }

        private static JoinShardResponse Placement(string collisionRevision)
        {
            return new JoinShardResponse
            {
                shard = new ShardResponse { id = ShardId, worldId = WorldId },
                endpoint = new SimulationEndpointResponse
                {
                    workerId = "worker-1",
                    runtimeId = "runtime-1",
                    host = "127.0.0.1",
                    udpPort = 27015,
                    protocolVersion = RealtimeProtocol.Version,
                    simulationRevision = GameSimulationCompatibility.Revision,
                    collisionRevision = collisionRevision
                },
                characterId = CharacterId,
                joinTicket = "ticket-content",
                expiresAt = DateTime.UtcNow.AddMinutes(5).ToString("O")
            };
        }

        private static RealtimeJoinAccepted Accepted(string collisionRevision)
        {
            return new RealtimeJoinAccepted(
                "00000000-0000-4000-8000-000000002101",
                "00000000-0000-4000-8000-000000002002",
                CharacterId,
                "Content Hero",
                ShardId,
                WorldId,
                7,
                GameSimulationCompatibility.Revision,
                collisionRevision,
                "2026-11-17T10:00:00.0000000Z",
                "2026-11-17T22:00:00.0000000Z",
                false,
                new RealtimeCarryState(0, 0, 200),
                new RealtimeMovementSettings(30, 15, 5f, 720f, -24f, 55f, -2f, 0f, -14f, 14f, -14f, 14f, 0.35f, 2f, 0.35f, 45f, 0.4f, 0.1f, 6),
                new RealtimePlayerState(0f, 0f, -1f, 0f, 0f, 0f, 0f, true),
                new RealtimeShardRules(false));
        }
    }
}
