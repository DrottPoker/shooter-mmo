using NUnit.Framework;
using ShooterMmo.Api;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class JsonArrayUtilityTests
    {
        [Test]
        public void FromJsonReturnsAnEmptyArrayForMissingContent()
        {
            var result = JsonArrayUtility.FromJson<CharacterResponse>(string.Empty);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void FromJsonParsesAnApiArray()
        {
            const string json =
                "[{\"id\":\"character-1\",\"name\":\"Hero One\",\"currency\":25,\"createdAt\":\"2026-07-09T12:00:00Z\"}]";

            var result = JsonArrayUtility.FromJson<CharacterResponse>(json);

            Assert.That(result, Has.Length.EqualTo(1));
            Assert.That(result[0].id, Is.EqualTo("character-1"));
            Assert.That(result[0].name, Is.EqualTo("Hero One"));
            Assert.That(result[0].currency, Is.EqualTo(25));
        }
    }
}
