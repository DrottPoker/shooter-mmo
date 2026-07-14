using NUnit.Framework;
using ShooterMmo.Api;

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
                worldId = "local-world-1"
            };
            ShooterMmoClientSession.ActiveSimulationSession = new ActiveSimulationSessionResponse
            {
                characterId = "character-1",
                shardId = "local-shard-1",
                worldId = "local-world-1"
            };

            ShooterMmoClientSession.Clear();

            Assert.That(ShooterMmoClientSession.IsAuthenticated, Is.False);
            Assert.That(ShooterMmoClientSession.Auth, Is.Null);
            Assert.That(ShooterMmoClientSession.SelectedCharacter, Is.Null);
            Assert.That(ShooterMmoClientSession.SelectedShard, Is.Null);
            Assert.That(ShooterMmoClientSession.ActiveSimulationSession, Is.Null);
        }
    }
}
