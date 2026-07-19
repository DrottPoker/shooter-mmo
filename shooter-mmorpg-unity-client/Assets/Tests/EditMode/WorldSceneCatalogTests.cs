using System;
using System.Linq;
using NUnit.Framework;
using ShooterMmo.WorldData.Worlds;
using ShooterMmo.Worlds;
using UnityEditor;
using UnityEngine;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class WorldSceneCatalogTests
    {
        [Test]
        public void CheckedInCatalogResolvesDevelopmentWorldOne()
        {
            var resolved = WorldSceneCatalog.TryResolveScene(
                "development-world-1",
                out var sceneName,
                out var error);

            Assert.That(resolved, Is.True, error);
            Assert.That(sceneName, Is.EqualTo("DevelopmentWorld1"));
            Assert.That(WorldSceneCatalog.IsWorldScene(sceneName), Is.True);
        }

        [Test]
        public void CheckedInCatalogResolvesDevelopmentWorldTwo()
        {
            var resolved = WorldSceneCatalog.TryResolveScene(
                "development-world-2",
                out var sceneName,
                out var error);

            Assert.That(resolved, Is.True, error);
            Assert.That(sceneName, Is.EqualTo("DevelopmentWorld2"));
            Assert.That(WorldSceneCatalog.IsWorldScene(sceneName), Is.True);
        }

        [Test]
        public void CheckedInCatalogMatchesEveryCanonicalWorldManifest()
        {
            const string worldsRoot = "Packages/com.shootermmo.world-data/Worlds";
            var manifests = AssetDatabase
                .FindAssets("t:TextAsset", new[] { worldsRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith("/world.json", StringComparison.Ordinal))
                .Select(path => AssetDatabase.LoadAssetAtPath<TextAsset>(path))
                .Select(asset => JsonUtility.FromJson<WorldManifestDocument>(asset.text))
                .OrderBy(manifest => manifest.worldId, StringComparer.Ordinal)
                .ToArray();
            var catalogAsset = Resources.Load<TextAsset>(WorldSceneCatalog.ResourcePath);

            Assert.That(manifests, Is.Not.Empty);
            Assert.That(catalogAsset, Is.Not.Null);
            Assert.That(
                WorldSceneCatalog.TryParse(catalogAsset.text, out var catalog, out var error),
                Is.True,
                error);
            Assert.That(catalog.entries, Has.Length.EqualTo(manifests.Length));
            foreach (var manifest in manifests)
            {
                Assert.DoesNotThrow(() => WorldManifestValidator.Validate(manifest));
                var entry = catalog.entries.SingleOrDefault(
                    candidate => candidate.worldId == manifest.worldId);
                Assert.That(entry, Is.Not.Null, "Missing client mapping for " + manifest.worldId);
                Assert.That(entry.sceneName, Is.EqualTo(manifest.clientSceneName));
            }
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
