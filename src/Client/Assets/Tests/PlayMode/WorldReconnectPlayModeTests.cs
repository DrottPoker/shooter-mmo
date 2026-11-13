using System;
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Ui;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.WorldData.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Utils;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Tests.PlayMode
{
    // The World scene goes on across a lost connection and the join that restores it, without a
    // scene load: a context of Development World 1 enters the World of a hub on a scripted
    // transport, the line drops, and the reconnect joins again with a ticket that the test grants.
    public sealed class WorldReconnectPlayModeTests
    {
        private const string WorldId = "development-world-1";
        private const string ShardId = "local-shard-1";
        private const string CharacterName = "Reconnect Hero";
        private static readonly string CharacterId = "00000000-0000-4000-8000-000000001003";
        private static readonly string LostSessionId = "00000000-0000-4000-8000-000000001001";
        private static readonly string RestoredSessionId = "00000000-0000-4000-8000-000000001101";
        private static readonly ClientSettings Settings = new ClientSettings("http://127.0.0.1:9", 10, 10, 5);

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator TheWorldSceneKeepsItsPlayerAcrossALostConnectionAndItsReconnect()
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
            var hubObject = new GameObject("Reconnect test hub");
            hubObject.SetActive(false);
            var hub = hubObject.AddComponent<RealtimeSimulationClient>();
            hub.Initialize(transport, () => Time.realtimeSinceStartup, Settings);
            hubObject.SetActive(true);
            var texts = BundledTexts();
            var theme = UiTheme.Load(out var themeHandle);
            WorldReconnect reconnect = null;
            GameObject noticeObject = null;
            try
            {
                // The client enters the World of its first join, as CharacterSelect leaves it.
                var session = new ClientSession(Settings);
                session.SignIn(new AuthResponse { accountId = "account-1", username = "reconnect_hero", sessionToken = "token" });
                var collisionRevision = CollisionRevision();
                transport.NextJoinAccepted = Accepted(LostSessionId, collisionRevision, false, new RealtimePlayerState(0f, 0f, -1f, 0f, 0f, 0f, 0f, true));
                ActiveSimulationSessionResponse joined = null;
                yield return hub.Session.Join(Placement("ticket-1", collisionRevision), value => joined = value, error => Assert.Fail(error.ToDiagnostic()));
                session.EnterWorld(joined);

                var players = new LocalPlayerSource();
                var playerChanges = 0;
                players.Changed += () => playerChanges++;
                context.Initialize(session, hub.Session, hub.Movement, hub.Players, hub.Looks, hub.Admin, players, new WorldPointerFocus(), null, null);
                context.enabled = true;
                Assert.That(context.TryEnterWorld(), Is.True);
                var player = context.LocalPlayer;
                Assert.That(player, Is.Not.Null);
                Assert.That(player.IsServerAuthoritative, Is.True);

                var notices = new ClientNotices(() => Time.realtimeSinceStartup);
                var recovery = new ClientSessionRecovery(session, hub.Session, notices);
                var exit = new WorldExit(session, hub.Session, recovery, notices);
                var tickets = new GrantedTickets();
                reconnect = new WorldReconnect(
                    session,
                    hub.Session,
                    new WorldJoin(session, tickets, hub.Session),
                    exit,
                    recovery,
                    () => Time.realtimeSinceStartup,
                    () => true,
                    routine => hub.StartCoroutine(routine));
                noticeObject = new GameObject("Reconnect notice");
                noticeObject.SetActive(false);
                var notice = noticeObject.AddComponent<ReconnectNotice>();
                notice.Initialize(theme, reconnect, texts);
                noticeObject.SetActive(true);

                // The joined player predicts and sends its input every tick.
                yield return Frames(reconnect, 10);
                Assert.That(transport.MovementInputBatches, Is.GreaterThan(0));
                Assert.That(notice.IsVisible, Is.False);

                // The line drops: the World and its player stay, the notice shows the seconds
                // left, and input neither moves the player nor sends.
                LogAssert.Expect(LogType.Error, new Regex("connection_lost"));
                transport.LoseLine();
                yield return Frames(reconnect, 3);
                Assert.That(reconnect.IsReconnecting, Is.True);
                Assert.That(tickets.Pending, Is.Not.Null, "The first attempt asks for a ticket.");
                Assert.That(notice.IsVisible, Is.True);
                Assert.That(notice.LineText, Is.EqualTo(ReconnectNotice.Line(texts, reconnect.SecondsLeft)));
                Assert.That(context.LocalPlayer, Is.SameAs(player));
                Assert.That(player.IsServerAuthoritative, Is.False);
                Assert.That(player.MoveTo(player.transform.position + new Vector3(3f, 0f, 0f)), Is.Zero);
                var standing = player.transform.position;
                var sent = transport.MovementInputBatches;
                yield return Frames(reconnect, 10);
                Assert.That(transport.MovementInputBatches, Is.EqualTo(sent), "No input is sent while reconnecting.");
                Assert.That(player.transform.position, Is.EqualTo(standing), "The player stands where it was.");
                Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo(SceneNames.DevelopmentWorld1));

                // The worker accepts the join with the body where it stands now.
                transport.NextJoinAccepted = Accepted(RestoredSessionId, collisionRevision, true, new RealtimePlayerState(3f, 0f, 2f, 0f, 0f, 0f, 90f, true));
                tickets.Pending.Grant(Placement("ticket-2", collisionRevision));
                var deadline = Time.realtimeSinceStartup + 10f;
                while (reconnect.IsReconnecting)
                {
                    Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), "The reconnect did not complete.");
                    yield return Frames(reconnect, 1);
                }

                yield return Frames(reconnect, 10);
                Assert.That(session.ActiveSimulationSession.simulationSessionId, Is.EqualTo(RestoredSessionId));
                Assert.That(context.LocalPlayer, Is.SameAs(player), "The same player goes on.");
                Assert.That(playerChanges, Is.EqualTo(1), "The player appeared once and never went.");
                Assert.That(player.IsServerAuthoritative, Is.True);
                Assert.That(
                    new Vector2(player.transform.position.x, player.transform.position.z),
                    Is.EqualTo(new Vector2(3f, 2f)).Using(new Vector2EqualityComparer(0.25f)),
                    "The player stands where the worker placed it.");
                Assert.That(transport.MovementInputBatches, Is.GreaterThan(sent), "Input is sent again.");
                Assert.That(notice.IsVisible, Is.False);
                Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo(SceneNames.DevelopmentWorld1));
            }
            finally
            {
                reconnect?.Dispose();
                if (noticeObject != null)
                {
                    Object.Destroy(noticeObject);
                }

                // The player of the scene stops with the session it was bound to.
                if (hub.Session.State != RealtimeConnectionState.Disconnected)
                {
                    hub.Session.Abort();
                }

                Object.Destroy(hubObject);
                ClientAssets.Release(themeHandle);
            }
        }

        // Frames in which the reconnect ends its window and starts its attempts, as the bootstrap's
        // Update does.
        private static IEnumerator Frames(WorldReconnect reconnect, int count)
        {
            for (var frame = 0; frame < count; frame++)
            {
                yield return null;
                reconnect?.Tick();
            }
        }

        private static string CollisionRevision()
        {
            Assert.That(UnityWorldCollisionLoader.TryCreateStream(WorldId, out var stream, out var error), Is.True, error);
            return stream.CollisionWorld.Revision;
        }

        private static JoinShardResponse Placement(string ticket, string collisionRevision)
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
                joinTicket = ticket,
                expiresAt = DateTime.UtcNow.AddMinutes(5).ToString("O")
            };
        }

        private static RealtimeJoinAccepted Accepted(
            string simulationSessionId,
            string collisionRevision,
            bool isReconnect,
            RealtimePlayerState state)
        {
            return new RealtimeJoinAccepted(
                simulationSessionId,
                "00000000-0000-4000-8000-000000001002",
                CharacterId,
                CharacterName,
                ShardId,
                WorldId,
                7,
                GameSimulationCompatibility.Revision,
                collisionRevision,
                "2026-11-13T10:00:00.0000000Z",
                "2026-11-13T22:00:00.0000000Z",
                isReconnect,
                new RealtimeCarryState(0, 0, 200),
                new RealtimeMovementSettings(30, 15, 5f, 720f, -24f, 55f, -2f, 0f, -14f, 14f, -14f, 14f, 0.35f, 2f, 0.35f, 45f, 0.4f, 0.1f, 6),
                state,
                new RealtimeShardRules(false));
        }

        private static TextCatalog BundledTexts()
        {
            var config = ClientConfig.Load(out var handle);
            try
            {
                Assert.That(ClientTexts.TryLoad(config.TextTables.Select(asset => asset.text).ToArray(), out var texts, out var error), Is.True, error);
                return texts;
            }
            finally
            {
                ClientAssets.Release(handle);
            }
        }

        // GameService's ticket call, whose one pending request the test grants.
        private sealed class GrantedTickets : IJoinTicketApi
        {
            public Request Pending { get; private set; }

            public IEnumerator CreateJoinTicket(
                string gameServiceBaseUrl,
                string sessionToken,
                string shardId,
                JoinShardRequest request,
                Action<JoinShardResponse> onSuccess,
                Action<ApiError> onError)
            {
                var pending = new Request();
                Pending = pending;
                while (pending.Ticket == null)
                {
                    yield return null;
                }

                Pending = null;
                onSuccess(pending.Ticket);
            }

            public sealed class Request
            {
                public JoinShardResponse Ticket { get; private set; }

                public void Grant(JoinShardResponse ticket)
                {
                    Ticket = ticket;
                }
            }
        }
    }
}
