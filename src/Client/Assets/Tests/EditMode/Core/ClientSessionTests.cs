using System;
using System.Collections.Generic;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Quests;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The client session changes only through its transitions, and each transition raises its
    // one event.
    public sealed class ClientSessionTests
    {
        private static readonly ClientSettings Settings = new ClientSettings("http://127.0.0.1:9", 10, 10, 5);

        // The environment gives the address and the configuration the timeouts.
        [Test]
        public void TheSettingsComeFromTheEnvironmentAndTheConfiguration()
        {
            var config = ScriptableObject.CreateInstance<ClientConfig>();
            var build = new ClientBuildInfo(
                new ClientEnvironment("staging", " http://game.test:5000/ "),
                ClientBuildStamp.Editor);
            try
            {
                // The reconnect window mirrors the worker's 20 s body time by default.
                Assert.That(ClientSettings.From(config, build, string.Empty).ReconnectWindowSeconds, Is.EqualTo(20));

                JsonUtility.FromJsonOverwrite(
                    "{\"requestTimeoutSeconds\":7,"
                    + "\"realtimeTimeoutSeconds\":500,\"sessionValidationIntervalSeconds\":1,\"reconnectWindowSeconds\":0}",
                    config);

                var settings = ClientSettings.From(config, build, string.Empty);

                // The environment trims the URL and the configuration clamps the values.
                Assert.That(
                    (settings.GameServiceBaseUrl, settings.RequestTimeoutSeconds, settings.RealtimeTimeoutSeconds,
                        settings.SessionValidationIntervalSeconds, settings.ReconnectWindowSeconds),
                    Is.EqualTo(("http://game.test:5000", 7, 120, 2, 1)));
                Assert.That(settings.Environment.Name, Is.EqualTo("staging"));
                Assert.That(
                    settings.WithGameServiceBaseUrl("http://127.0.0.1:9").GameServiceBaseUrl,
                    Is.EqualTo("http://127.0.0.1:9"));
                Assert.That(settings.WithGameServiceBaseUrl("http://127.0.0.1:9").ReconnectWindowSeconds, Is.EqualTo(1));
                Assert.Throws<ArgumentException>(() => new ClientSettings(" ", 1, 1, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() => new ClientSettings("http://game.test", 1, 0, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() => new ClientSettings("http://game.test", 1, 1, 1, 0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void SigningInStartsWithoutASelectionOrAWorld()
        {
            var session = new ClientSession(Settings);
            var events = Record(session);
            session.SignIn(Auth("first-token"));
            session.Select(Character(), Shard());
            session.EnterWorld(World("session-1"));
            events.Clear();

            session.SignIn(Auth("second-token"));

            Assert.That(events, Is.EqualTo(new[] { "SignedIn" }));
            Assert.That(session.IsAuthenticated, Is.True);
            Assert.That(session.SessionToken, Is.EqualTo("second-token"));
            Assert.That(session.SelectedCharacter, Is.Null);
            Assert.That(session.SelectedShard, Is.Null);
            Assert.That(session.ActiveSimulationSession, Is.Null);
        }

        [Test]
        public void SigningOutClearsTheAccountTheSelectionAndTheWorldOnce()
        {
            var session = new ClientSession(Settings);
            var events = Record(session);
            session.SignIn(Auth("session-token"));
            session.Select(Character(), Shard());
            session.EnterWorld(World("session-1"));
            events.Clear();

            session.SignOut();
            session.SignOut();

            Assert.That(events, Is.EqualTo(new[] { "SignedOut" }), "A session that is already empty raises nothing.");
            Assert.That(session.IsAuthenticated, Is.False);
            Assert.That(session.Auth, Is.Null);
            Assert.That(session.SessionToken, Is.Empty);
            Assert.That(session.SelectedCharacter, Is.Null);
            Assert.That(session.SelectedShard, Is.Null);
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(session.Settings, Is.SameAs(Settings), "The settings outlive the account session.");
        }

        [Test]
        public void SelectingRaisesOnlyForAnotherCharacterOrShard()
        {
            var session = new ClientSession(Settings);
            var events = Record(session);
            var character = Character();
            var shard = Shard();

            session.Select(character, shard);
            session.Select(character, shard);
            var ticketShard = Shard();
            session.Select(character, ticketShard);

            Assert.That(events, Is.EqualTo(new[] { "SelectionChanged", "SelectionChanged" }));
            Assert.That(session.SelectedCharacter, Is.SameAs(character));
            Assert.That(session.SelectedShard, Is.SameAs(ticketShard));
        }

        // The rule of every way out of a World: an exit that names an older simulation session
        // never clears a newer World, such as the World that a join after a lost connection keeps.
        [Test]
        public void LeavingAWorldNeedsTheExactSimulationSession()
        {
            var session = new ClientSession(Settings);
            var events = Record(session);
            var first = World("session-1");
            var second = World("session-2");

            session.EnterWorld(first);
            Assert.That(session.LeaveWorld("session-2"), Is.False);
            Assert.That(session.ActiveSimulationSession, Is.SameAs(first));

            session.EnterWorld(second);
            Assert.That(session.LeaveWorld("session-1"), Is.False);
            Assert.That(session.ActiveSimulationSession, Is.SameAs(second));

            Assert.That(session.LeaveWorld("session-2"), Is.True);
            Assert.That(session.LeaveWorld("session-2"), Is.False);
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(events, Is.EqualTo(new[] { "WorldEntered", "WorldEntered", "WorldLeft" }));
        }

        // The inventory and quest controllers listen to the session, so they follow the account
        // and the character at once, without a frame.
        [Test]
        public void TheInventoryAndQuestControllersFollowTheAccountAndTheCharacterAtOnce()
        {
            var owner = new GameObject("Controllers");
            var api = new ApiClient(5);
            try
            {
                var realtime = owner.AddComponent<RealtimeSimulationClient>();
                realtime.Initialize(Settings);
                var session = new ClientSession(Settings);
                var recovery = new ClientSessionRecovery(session, realtime.Session, new ClientNotices(() => 0f));
                var texts = FixtureTexts.Messages();
                var inventory = owner.AddComponent<InventoryClientController>();
                inventory.Initialize(api, realtime.Items, realtime.Session, session, recovery, null, texts);
                var quests = owner.AddComponent<QuestClientController>();
                quests.Initialize(api, realtime.Quests, realtime.Session, session, recovery, null, texts);
                var first = Guid.NewGuid();
                var second = Guid.NewGuid();

                session.SignIn(Auth("session-token"));
                session.Select(new CharacterResponse { id = first.ToString(), name = "First" }, null);
                Assert.That((inventory.State.CharacterId, quests.State.CharacterId), Is.EqualTo((first, first)));

                session.Select(new CharacterResponse { id = second.ToString(), name = "Second" }, null);
                Assert.That((inventory.State.CharacterId, quests.State.CharacterId), Is.EqualTo((second, second)));

                session.SignOut();
                Assert.That((inventory.State.CharacterId, quests.State.CharacterId), Is.EqualTo((Guid.Empty, Guid.Empty)));
            }
            finally
            {
                api.Dispose();
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void DisposingTheApiClientAbortsAndReleasesRequestsInFlight()
        {
            var client = new ApiClient(5);
            var routine = client.GetShards("http://127.0.0.1:9", _ => { }, _ => { });

            Assert.That(routine.MoveNext(), Is.True);
            Assert.That(client.ActiveRequestCount, Is.EqualTo(1));

            client.Dispose();

            Assert.That(client.ActiveRequestCount, Is.EqualTo(0));
            Assert.Throws<ObjectDisposedException>(
                () => client.GetShards("http://127.0.0.1:9", _ => { }, _ => { }).MoveNext());
        }

        private static List<string> Record(IClientSession session)
        {
            var events = new List<string>();
            session.SignedIn += () => events.Add("SignedIn");
            session.SignedOut += () => events.Add("SignedOut");
            session.SelectionChanged += () => events.Add("SelectionChanged");
            session.WorldEntered += () => events.Add("WorldEntered");
            session.WorldLeft += () => events.Add("WorldLeft");
            return events;
        }

        private static AuthResponse Auth(string token)
        {
            return new AuthResponse
            {
                accountId = "account-1",
                username = "player_one",
                sessionId = "session-1",
                sessionToken = token,
                expiresAt = "2026-07-10T12:00:00Z"
            };
        }

        private static CharacterResponse Character()
        {
            return new CharacterResponse { id = "character-1", name = "Hero One" };
        }

        private static ShardResponse Shard()
        {
            return new ShardResponse { id = "local-shard-1", displayName = "Local Shard 1", worldId = "development-world-1" };
        }

        private static ActiveSimulationSessionResponse World(string simulationSessionId)
        {
            return new ActiveSimulationSessionResponse
            {
                simulationSessionId = simulationSessionId,
                characterId = "character-1",
                shardId = "local-shard-1",
                worldId = "development-world-1"
            };
        }
    }
}
