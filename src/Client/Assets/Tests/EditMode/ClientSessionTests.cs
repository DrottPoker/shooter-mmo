using System;
using System.Linq;
using System.Reflection;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Worlds;
using Mmorpg.WorldData.Items.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class ClientSessionTests
    {
        [TearDown]
        public void TearDown()
        {
            ClientSession.Clear();
        }

        [Test]
        public void ClearRemovesAuthenticationAndShardSelection()
        {
            ClientSession.Auth = new AuthResponse
            {
                accountId = "account-1",
                username = "player_one",
                sessionId = "session-1",
                sessionToken = "session-token",
                expiresAt = "2026-07-10T12:00:00Z"
            };
            ClientSession.SelectedCharacter = new CharacterResponse
            {
                id = "character-1",
                name = "Hero One"
            };
            ClientSession.SelectedShard = new ShardResponse
            {
                id = "local-shard-1",
                displayName = "Local Shard 1",
                worldId = "development-world-1"
            };
            ClientSession.ActiveSimulationSession = new ActiveSimulationSessionResponse
            {
                characterId = "character-1",
                shardId = "local-shard-1",
                worldId = "development-world-1"
            };

            ClientSession.Clear();

            Assert.That(ClientSession.IsAuthenticated, Is.False);
            Assert.That(ClientSession.Auth, Is.Null);
            Assert.That(ClientSession.SelectedCharacter, Is.Null);
            Assert.That(ClientSession.SelectedShard, Is.Null);
            Assert.That(ClientSession.ActiveSimulationSession, Is.Null);
        }

        [Test]
        public void EveryStaticCacheResetsBeforeTheFirstSceneLoads()
        {
            // Entering Play Mode without a domain reload keeps static state, so each type
            // that holds it must reset at subsystem registration.
            var types = new[]
            {
                typeof(ClientSession),
                typeof(WorldSceneCatalog),
                typeof(ItemPresentationCatalogLoader)
            };
            foreach (var type in types)
            {
                var reset = type
                    .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .SingleOrDefault(method => method
                        .GetCustomAttributes<RuntimeInitializeOnLoadMethodAttribute>()
                        .Any(attribute =>
                            attribute.loadType == RuntimeInitializeLoadType.SubsystemRegistration));
                Assert.That(reset, Is.Not.Null, type.Name);
            }

            ClientSession.Auth = new AuthResponse { sessionToken = "session-token" };
            typeof(ClientSession)
                .GetMethod("ResetStaticState", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, null);

            Assert.That(ClientSession.IsAuthenticated, Is.False);
            Assert.That(ClientSession.GameServiceBaseUrl, Is.EqualTo("http://localhost:5000"));
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
    }
}
