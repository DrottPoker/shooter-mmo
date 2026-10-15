using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ShooterMmo.Api;
using ShooterMmo.Worlds;
using ShooterMmo.WorldData.Items.Presentation;
using UnityEngine;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class ShooterMmoClientSessionTests
    {
        [TearDown]
        public void TearDown()
        {
            ShooterMmoClientSession.Clear();
        }

        [Test]
        public void ClearRemovesAuthenticationAndShardSelection()
        {
            ShooterMmoClientSession.Auth = new AuthResponse
            {
                accountId = "account-1",
                username = "player_one",
                sessionId = "session-1",
                sessionToken = "session-token",
                expiresAt = "2026-07-10T12:00:00Z"
            };
            ShooterMmoClientSession.SelectedCharacter = new CharacterResponse
            {
                id = "character-1",
                name = "Hero One"
            };
            ShooterMmoClientSession.SelectedShard = new ShardResponse
            {
                id = "local-shard-1",
                displayName = "Local Shard 1",
                worldId = "development-world-1"
            };
            ShooterMmoClientSession.ActiveSimulationSession = new ActiveSimulationSessionResponse
            {
                characterId = "character-1",
                shardId = "local-shard-1",
                worldId = "development-world-1"
            };

            ShooterMmoClientSession.Clear();

            Assert.That(ShooterMmoClientSession.IsAuthenticated, Is.False);
            Assert.That(ShooterMmoClientSession.Auth, Is.Null);
            Assert.That(ShooterMmoClientSession.SelectedCharacter, Is.Null);
            Assert.That(ShooterMmoClientSession.SelectedShard, Is.Null);
            Assert.That(ShooterMmoClientSession.ActiveSimulationSession, Is.Null);
        }

        [Test]
        public void EveryStaticCacheResetsBeforeTheFirstSceneLoads()
        {
            // Entering Play Mode without a domain reload keeps static state, so each type
            // that holds it must reset at subsystem registration.
            var types = new[]
            {
                typeof(ShooterMmoClientSession),
                typeof(ShooterMmoClientBootstrap),
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

            ShooterMmoClientSession.Auth = new AuthResponse { sessionToken = "session-token" };
            typeof(ShooterMmoClientSession)
                .GetMethod("ResetStaticState", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, null);

            Assert.That(ShooterMmoClientSession.IsAuthenticated, Is.False);
            Assert.That(ShooterMmoClientSession.AuthServiceBaseUrl, Is.EqualTo("http://localhost:5000"));
        }

        [Test]
        public void DisposingTheApiClientAbortsAndReleasesRequestsInFlight()
        {
            var client = new ShooterMmoApiClient(5);
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
