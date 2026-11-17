using System;
using System.Collections;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.WorldData.Characters;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Tests.PlayMode
{
    // A body shows only once the content of its whole look is loaded, never in underwear and never
    // half dressed: a context of Development World 1 enters the World of a scripted worker, which
    // then sends a player with hair into view and changes its look. The content loads through
    // Addressables, which in the Editor's asset database mode takes a simulated 0.1 s a load, so the
    // frames in between show what a body does while its content loads.
    public sealed class WorldBodyContentPlayModeTests
    {
        private const string WorldId = "development-world-1";
        private const string ShardId = "local-shard-1";
        private const string CharacterId = "00000000-0000-4000-8000-000000002003";
        private const string Hair = "DarkBrownHair";
        private const string Tattoo = "Tattoo";
        private static readonly ClientSettings Settings = new ClientSettings("http://127.0.0.1:9", 10, 10, 5);

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator ABodyShowsOnlyOnceTheContentOfItsWholeLookIsLoaded()
        {
            PlayModeClient.Session.SignOut();
            yield return PlayModeClient.WaitForContent();
            var sceneLoad = ClientSceneLoader.LoadWorldScene(SceneNames.DevelopmentWorld1);
            yield return sceneLoad;
            Assert.That(sceneLoad.Status, Is.EqualTo(AsyncOperationStatus.Succeeded));
            yield return null;
            var context = Object.FindAnyObjectByType<WorldSceneContext>(FindObjectsInactive.Include);
            Assert.That(context, Is.Not.Null);

            var transport = new ScriptedRealtimeTransport();
            var hubObject = new GameObject("Body content test hub");
            hubObject.SetActive(false);
            var hub = hubObject.AddComponent<RealtimeSimulationClient>();
            hub.Initialize(transport, () => Time.realtimeSinceStartup, Settings);
            hubObject.SetActive(true);
            try
            {
                var session = new ClientSession(Settings);
                session.SignIn(new AuthResponse { accountId = "account-2", username = "content_hero", sessionToken = "token" });
                var collisionRevision = CollisionRevision();
                transport.NextJoinAccepted = Accepted(collisionRevision);
                ActiveSimulationSessionResponse joined = null;
                yield return hub.Session.Join(Placement(collisionRevision), value => joined = value, error => Assert.Fail(error.ToDiagnostic()));
                session.EnterWorld(joined);
                context.Initialize(session, hub.Session, hub.Movement, hub.Players, hub.Looks, hub.Admin, new LocalPlayerSource(),
                    new WorldPointerFocus(), null, null);
                context.enabled = true;

                // The World is entered at once but shown only once the content of the default looks,
                // their underwear, is loaded; a cover hides it meanwhile.
                Assert.That(context.TryEnterWorld(), Is.True);
                Assert.That(context.LocalPlayer, Is.Not.Null);
                Assert.That(context.IsWorldShown, Is.False, "the World shows before the content of its looks loaded");
                Assert.That(GameObject.Find("WorldEntryCover"), Is.Not.Null);
                yield return Until(() => context.IsWorldShown, "the World was never shown");
                Assert.That(GameObject.Find("WorldEntryCover") == null || !GameObject.Find("WorldEntryCover").activeInHierarchy, Is.True);

                // A player with hair comes into view: no body until the hair loaded, then the body
                // with its hair.
                var withHair = Appearance(Choice(CharacterAppearanceFormat.Hair, Hair));
                var cold = Time.realtimeSinceStartup;
                Spawn(transport, 77, withHair);
                var waited = 0;
                var longest = 0f;
                RemotePlayerView view = null;
                while (view == null || view.Body == null)
                {
                    Assert.That(Time.realtimeSinceStartup - cold, Is.LessThan(10f), "the player's body never showed");
                    yield return null;
                    longest = Mathf.Max(longest, Time.unscaledDeltaTime);
                    if (view == null)
                    {
                        context.TryGetRemotePlayer(77, out view);
                    }

                    waited += view != null && view.Body == null ? 1 : 0;
                }

                var coldSeconds = Time.realtimeSinceStartup - cold;
                // The frames in which the first garment of its shader variant draws, after the
                // warm-up drew that variant once.
                for (var frame = 0; frame < 5; frame++)
                {
                    yield return null;
                    longest = Mathf.Max(longest, Time.unscaledDeltaTime);
                }

                Assert.That(waited, Is.GreaterThan(0), "the body did not wait for the content of its look");
                var body = view.GetComponentInChildren<PlayerBodyAppearance>();
                AssertWearsItsLook(body);
                Assert.That(body.Worn.Select(garment => garment.Name), Does.Contain(Hair));

                // The look changes to one with a tattoo and another face: the body keeps its old look
                // until the new one's content is loaded.
                var tattooed = Appearance(
                    Choice(CharacterAppearanceFormat.Hair, Hair),
                    Choice(CharacterAppearanceFormat.Tattoo, Tattoo),
                    Choice(CharacterAppearanceFormat.Face, "Clean"));
                var oldLook = body.Look;
                transport.Deliver(RealtimeProtocol.EncodeEntityAppearanceChanged(new RealtimeEntityAppearanceChanged(77, tattooed)),
                    RealtimeProtocol.WorldChannel);
                var kept = 0;
                var started = Time.realtimeSinceStartup;
                while (!body.Look.HasPaint(Tattoo))
                {
                    Assert.That(Time.realtimeSinceStartup - started, Is.LessThan(10f), "the new look never showed");
                    Assert.That(body.Look.Key, Is.EqualTo(oldLook.Key), "the body changed its look before the content loaded");
                    AssertWearsItsLook(body);
                    kept++;
                    yield return null;
                }

                Assert.That(kept, Is.GreaterThan(0), "the body did not keep its old look while the new one loaded");
                AssertWearsItsLook(body);
                var painted = body.CombinedRenderer.sharedMaterials.Any(material =>
                    material.HasProperty("_PaintMap1") && material.GetTexture("_PaintMap1") != null
                    && material.GetTexture("_PaintMap1").name.StartsWith("T_Paint_Tattoo", StringComparison.Ordinal));
                Assert.That(painted, Is.True, "the new look shows its tattoo");

                // A second player in a look whose content is loaded shows as soon as its body is
                // built, without a load.
                var warm = Time.realtimeSinceStartup;
                Spawn(transport, 78, withHair);
                RemotePlayerView second = null;
                while (second == null || second.Body == null)
                {
                    Assert.That(Time.realtimeSinceStartup - warm, Is.LessThan(10f), "the second body never showed");
                    yield return null;
                    if (second == null)
                    {
                        context.TryGetRemotePlayer(78, out second);
                    }
                }

                var warmSeconds = Time.realtimeSinceStartup - warm;
                AssertWearsItsLook(second.GetComponentInChildren<PlayerBodyAppearance>());
                Debug.Log("[BODY CONTENT] A body came into view and showed after "
                    + (coldSeconds * 1000f).ToString("0") + " ms with its content cold and "
                    + (warmSeconds * 1000f).ToString("0") + " ms with it loaded; the longest frame from the cold spawn until 5 frames after it showed took "
                    + (longest * 1000f).ToString("0.0") + " ms; garments "
                    + context.BodyGarmentCount + ", attachments " + context.BodyAttachmentCount + ".");
                Assert.That(context.BodyGarmentCount, Is.GreaterThan(0));
            }
            finally
            {
                if (hub.Session.State != RealtimeConnectionState.Disconnected)
                {
                    hub.Session.Abort();
                }

                Object.Destroy(hubObject);
            }
        }

        // A body that shows wears every garment its look names that its catalog has, so no frame
        // shows it half dressed.
        private static void AssertWearsItsLook(PlayerBodyAppearance body)
        {
            Assert.That(body, Is.Not.Null);
            Assert.That(body.CombinedRenderer, Is.Not.Null);
            Assert.That(body.CombinedRenderer.sharedMesh, Is.Not.Null);
            var named = PlayerBodyAppearance.WornBy(body.Catalog, body.Look).Select(garment => garment.Name).ToArray();
            Assert.That(body.Worn.Select(garment => garment.Name), Is.EqualTo(named), "the body shows without a garment of its look");
        }

        private static IEnumerator Until(Func<bool> condition, string message)
        {
            var deadline = Time.realtimeSinceStartup + 10f;
            while (!condition())
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), message);
                yield return null;
            }
        }

        private static void Spawn(ScriptedRealtimeTransport transport, ulong entityId, RealtimeCharacterAppearance appearance)
        {
            var spawn = new RealtimeEntitySpawn(
                entityId,
                RealtimeEntityKind.Player,
                "00000000-0000-4000-8000-0000000020" + entityId.ToString("00"),
                "Visitor " + entityId,
                RealtimePlayerArchetypeIds.Player,
                10,
                new RealtimePlayerState(2f, 0f, 2f, 0f, 0f, 0f, 0f, true),
                appearance);
            transport.Deliver(RealtimeProtocol.EncodeEntitySpawn(spawn), RealtimeProtocol.WorldChannel);
        }

        private static RealtimeCharacterAppearance Appearance(params RealtimeAppearanceChoice[] choices)
        {
            return new RealtimeCharacterAppearance("Human_Male", choices, null, false, null);
        }

        private static RealtimeAppearanceChoice Choice(string kind, string option)
        {
            return new RealtimeAppearanceChoice(kind, string.Empty, option);
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
