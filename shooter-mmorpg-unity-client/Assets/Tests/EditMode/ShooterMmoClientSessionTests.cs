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
        public void ClearRemovesAuthenticationAndWorldSelection()
        {
            ShooterMmoClientSession.Auth = new AuthResponse
            {
                accountId = "account-1",
                username = "player_one",
                sessionToken = "session-token",
                expiresAt = "2026-07-10T12:00:00Z"
            };
            ShooterMmoClientSession.SelectedCharacter = new CharacterResponse
            {
                id = "character-1",
                name = "Hero One"
            };
            ShooterMmoClientSession.SelectedWorld = new WorldResponse
            {
                id = "local-world-1",
                displayName = "Local World 1"
            };
            ShooterMmoClientSession.ActiveWorldSession = new ActivePlayerSessionResponse
            {
                characterId = "character-1",
                worldId = "local-world-1"
            };

            ShooterMmoClientSession.Clear();

            Assert.That(ShooterMmoClientSession.IsAuthenticated, Is.False);
            Assert.That(ShooterMmoClientSession.Auth, Is.Null);
            Assert.That(ShooterMmoClientSession.SelectedCharacter, Is.Null);
            Assert.That(ShooterMmoClientSession.SelectedWorld, Is.Null);
            Assert.That(ShooterMmoClientSession.ActiveWorldSession, Is.Null);
        }
    }
}
