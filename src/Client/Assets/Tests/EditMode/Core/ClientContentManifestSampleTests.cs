using System.IO;
using System.Linq;
using Mmorpg.Client.Core;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The client reads the sample manifests that GameService's Content tests require it to
    // write, in tests/Fixtures/ContentManifest, so a field renamed on either side fails a test.
    public sealed class ClientContentManifestSampleTests
    {
        [TestCase("manifest.json", "/api/content/")]
        [TestCase("manifest-base-url.json", "https://cdn.example.com/content/")]
        public void TheClientReadsEveryFieldOfTheSampleManifest(string sampleName, string urlPrefix)
        {
            var path = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "..", "..", "tests", "Fixtures", "ContentManifest", sampleName));

            Assert.That(ClientContentManifest.TryParse(File.ReadAllText(path), out var manifest, out var error), Is.True, error);

            Assert.That(manifest.FormatVersion, Is.EqualTo(ClientContentManifest.SupportedFormatVersion));
            var expected = new[]
            {
                ("item-catalog", "core", "", 9, new string('a', 64), 23779L, "item-catalog/core/" + new string('a', 64)),
                ("quest-catalog", "core", "", 2, new string('b', 64), 8123L, "quest-catalog/core/" + new string('b', 64)),
                ("text-table", "ui", "en", 1, new string('d', 64), 45938L, "text-table/en/ui/" + new string('d', 64)),
                ("text-table", "items", "pt-br", 1, new string('c', 64), 2048L, "text-table/pt-br/items/" + new string('c', 64))
            };
            Assert.That(
                manifest.Files.Select(file => (file.Kind, file.Id, file.Locale, file.FormatVersion, file.Revision, file.SizeBytes, file.Url)),
                Is.EqualTo(expected.Select(entry => (entry.Item1, entry.Item2, entry.Item3, entry.Item4, entry.Item5, entry.Item6, urlPrefix + entry.Item7))));
            Assert.That(manifest.Find(ClientContentKinds.TextTable, "items", "pt-br"), Is.SameAs(manifest.Files[3]));
            Assert.That(manifest.Find(ClientContentKinds.ItemCatalog, ClientContentKinds.CoreCatalogId, null), Is.SameAs(manifest.Files[0]));
        }
    }
}
