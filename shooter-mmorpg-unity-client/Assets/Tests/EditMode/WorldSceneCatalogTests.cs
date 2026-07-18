using NUnit.Framework;
using ShooterMmo.Worlds;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class WorldSceneCatalogTests
    {
        [Test]
        public void CheckedInCatalogResolvesTheLocalWorldScene()
        {
            var resolved = WorldSceneCatalog.TryResolveScene(
                "local-world-1",
                out var sceneName,
                out var error);

            Assert.That(resolved, Is.True, error);
            Assert.That(sceneName, Is.EqualTo("WorldScene"));
            Assert.That(WorldSceneCatalog.IsWorldScene(sceneName), Is.True);
        }

        [Test]
        public void ParserRejectsDuplicateWorldIds()
        {
            const string json = "{\"formatVersion\":1,\"entries\":["
                + "{\"worldId\":\"world-1\",\"sceneName\":\"WorldSceneOne\"},"
                + "{\"worldId\":\"world-1\",\"sceneName\":\"WorldSceneTwo\"}]}";

            var parsed = WorldSceneCatalog.TryParse(json, out _, out var error);

            Assert.That(parsed, Is.False);
            Assert.That(error, Does.Contain("duplicate world ids"));
        }

        [Test]
        public void ParserAllowsWorldsToShareAReusableScene()
        {
            const string json = "{\"formatVersion\":1,\"entries\":["
                + "{\"worldId\":\"world-1\",\"sceneName\":\"ReusableWorldScene\"},"
                + "{\"worldId\":\"world-2\",\"sceneName\":\"ReusableWorldScene\"}]}";

            var parsed = WorldSceneCatalog.TryParse(json, out var catalog, out var error);

            Assert.That(parsed, Is.True, error);
            Assert.That(catalog.entries, Has.Length.EqualTo(2));
        }

        [Test]
        public void ParserRejectsNonAsciiWorldIds()
        {
            const string json = "{\"formatVersion\":1,\"entries\":["
                + "{\"worldId\":\"world-\\u00e5\",\"sceneName\":\"WorldScene\"}]}";

            var parsed = WorldSceneCatalog.TryParse(json, out _, out var error);

            Assert.That(parsed, Is.False);
            Assert.That(error, Does.Contain("invalid world id"));
        }
    }
}
