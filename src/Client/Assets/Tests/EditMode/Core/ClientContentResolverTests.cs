using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Text;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The content set resolved at start, driven with a fake transport and a temporary cache folder.
    public sealed class ClientContentResolverTests
    {
        private const string BaseUrl = "http://content.test";
        private const string ManifestUrl = BaseUrl + ClientContentResolver.ManifestPath;

        private string cacheRoot;

        [SetUp]
        public void CreateCache()
        {
            cacheRoot = Path.Combine(Path.GetTempPath(), "mmorpg-content-tests-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void DeleteCache()
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, true);
            }
        }

        [Test]
        public void BundledFilesThatMatchTheManifestNeedNoDownload()
        {
            var actors = ActorsTable("Mira");
            var items = ItemsTable("Sword");
            var transport = new FakeTransport();
            transport.Serve(ManifestUrl, Manifest(Entry(actors), Entry(items)));

            var report = Resolve(transport, false, actors, items);

            Assert.That(report.FellBackToBundled, Is.False);
            Assert.That(report.Files.Select(file => file.Source), Has.All.EqualTo(ClientContentSource.Bundled));
            Assert.That(report.Files.Select(file => file.Revision), Is.EqualTo(new[] { actors.Revision, items.Revision }));
            Assert.That(transport.Requests, Is.EqualTo(new[] { ManifestUrl }));
        }

        [Test]
        public void AStaleBundledFileIsDownloadedCheckedAndCachedAndThenReadFromTheCache()
        {
            var actors = ActorsTable("Mira");
            var bundledItems = ItemsTable("Sword");
            var servedItems = ItemsTable("Longsword");
            var transport = new FakeTransport();
            transport.Serve(ManifestUrl, Manifest(Entry(actors), Entry(servedItems)));
            transport.Serve(Url(servedItems), servedItems.Bytes);

            var first = Resolve(transport, false, actors, bundledItems);

            Assert.That(first.FellBackToBundled, Is.False, first.FallbackDiagnostic);
            Assert.That(first.Files[0].Source, Is.EqualTo(ClientContentSource.Bundled));
            Assert.That(first.Files[1].Source, Is.EqualTo(ClientContentSource.Downloaded));
            Assert.That(first.Files[1].Text, Is.EqualTo(servedItems.Json));
            Assert.That(first.Files[1].Revision, Is.EqualTo(servedItems.Revision));
            Assert.That(File.ReadAllBytes(CachePath(servedItems)), Is.EqualTo(servedItems.Bytes));

            transport.Requests.Clear();
            var second = Resolve(transport, false, actors, bundledItems);

            Assert.That(second.FellBackToBundled, Is.False, second.FallbackDiagnostic);
            Assert.That(second.Files[1].Source, Is.EqualTo(ClientContentSource.Cache));
            Assert.That(second.Files[1].Text, Is.EqualTo(servedItems.Json));
            Assert.That(transport.Requests, Is.EqualTo(new[] { ManifestUrl }));
        }

        [Test]
        public void ACachedFileThatDoesNotHashToItsRevisionIsDeletedAndDownloadedAgain()
        {
            var bundled = ItemsTable("Sword");
            var served = ItemsTable("Longsword");
            var tampered = served.Json.Replace("Longsword", "Shortsword");
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath(served)));
            File.WriteAllText(CachePath(served), tampered);
            var transport = new FakeTransport();
            transport.Serve(ManifestUrl, Manifest(Entry(served)));
            transport.Serve(Url(served), served.Bytes);

            var report = Resolve(transport, false, bundled);

            Assert.That(report.FellBackToBundled, Is.False, report.FallbackDiagnostic);
            Assert.That(report.Files[0].Source, Is.EqualTo(ClientContentSource.Downloaded));
            Assert.That(report.Files[0].Text, Is.EqualTo(served.Json));
            Assert.That(report.Warnings.Any(warning => warning.Contains("downloaded again")), Is.True, string.Join(" ", report.Warnings));
            Assert.That(transport.Requests, Is.EqualTo(new[] { ManifestUrl, Url(served) }));
            Assert.That(File.ReadAllBytes(CachePath(served)), Is.EqualTo(served.Bytes));
        }

        public enum BadDownload
        {
            ContentDoesNotMatchRevision,
            WrongId,
            WrongLocale,
            TooLarge,
            NotHttp
        }

        // One file that cannot be used makes the client keep its whole bundled set.
        [TestCase(BadDownload.ContentDoesNotMatchRevision)]
        [TestCase(BadDownload.WrongId)]
        [TestCase(BadDownload.WrongLocale)]
        [TestCase(BadDownload.TooLarge)]
        [TestCase(BadDownload.NotHttp)]
        public void ADownloadThatCannotBeUsedFallsBackToTheWholeBundledSet(BadDownload bad)
        {
            var bundledActors = ActorsTable("Mira");
            var servedActors = ActorsTable("Mira the Trader");
            var bundledItems = ItemsTable("Sword");
            var servedItems = ItemsTable("Longsword");
            var transport = new FakeTransport();
            transport.Serve(Url(servedActors), servedActors.Bytes);
            string itemsEntry;
            switch (bad)
            {
                case BadDownload.ContentDoesNotMatchRevision:
                    var tampered = Encoding.UTF8.GetBytes(servedItems.Json.Replace("Longsword", "Broadsword"));
                    itemsEntry = Entry(servedItems, tampered.Length);
                    transport.Serve(Url(servedItems), tampered);
                    break;
                case BadDownload.WrongId:
                    // The actors table, listed as the items table under its own revision.
                    itemsEntry = Entry("text-table", "items", "en", TextTableFormat.Version, servedActors.Revision, servedActors.Bytes.Length, "/api/content/text-table/en/items/" + servedActors.Revision);
                    transport.Serve(BaseUrl + "/api/content/text-table/en/items/" + servedActors.Revision, servedActors.Bytes);
                    break;
                case BadDownload.WrongLocale:
                    var german = Table(TextTableIds.Items, "de", (TextKeys.ItemName("test.sword"), "Schwert"));
                    itemsEntry = Entry("text-table", "items", "en", TextTableFormat.Version, german.Revision, german.Bytes.Length, "/api/content/text-table/en/items/" + german.Revision);
                    transport.Serve(BaseUrl + "/api/content/text-table/en/items/" + german.Revision, german.Bytes);
                    break;
                case BadDownload.TooLarge:
                    itemsEntry = Entry(servedItems, ClientContentResolver.MaximumFileBytes + 1);
                    transport.Serve(Url(servedItems), servedItems.Bytes);
                    break;
                default:
                    itemsEntry = Entry("text-table", "items", "en", TextTableFormat.Version, servedItems.Revision, servedItems.Bytes.Length, "file:///etc/items.json");
                    break;
            }

            transport.Serve(ManifestUrl, Manifest(Entry(servedActors), itemsEntry));

            var report = Resolve(transport, false, bundledActors, bundledItems);

            Assert.That(report.FellBackToBundled, Is.True);
            Assert.That(report.UpdateRequired, Is.False);
            Assert.That(report.FallbackCode, Is.EqualTo(ClientContentReport.UnavailableCode));
            Assert.That(report.FallbackDiagnostic, Does.Contain("text-table en/items"));
            Assert.That(report.Files.Select(file => file.Source), Has.All.EqualTo(ClientContentSource.Bundled));
            Assert.That(report.Files.Select(file => file.Text), Is.EqualTo(new[] { bundledActors.Json, bundledItems.Json }));
            Assert.That(report.Files.Select(file => file.Revision), Is.EqualTo(new[] { bundledActors.Revision, bundledItems.Revision }));
            Assert.That(Directory.Exists(Path.Combine(cacheRoot, "text-table", "en", "items")), Is.False, "A file that failed its check is never cached.");
            if (bad == BadDownload.TooLarge || bad == BadDownload.NotHttp)
            {
                Assert.That(transport.Requests, Is.EqualTo(new[] { ManifestUrl, Url(servedActors) }));
            }
        }

        // A body larger than the manifest says is cut off at the byte limit the transport is given.
        [Test]
        public void ABodyAboveTheLimitIsRefused()
        {
            var bundled = ItemsTable("Sword");
            var served = ItemsTable("Longsword");
            var transport = new FakeTransport();
            transport.Serve(ManifestUrl, Manifest(Entry(served)));
            transport.Serve(Url(served), new byte[ClientContentResolver.MaximumFileBytes + 1]);

            var report = Resolve(transport, false, bundled);

            Assert.That(report.FellBackToBundled, Is.True);
            Assert.That(report.FallbackDiagnostic, Does.Contain("could not be downloaded"));
            Assert.That(
                transport.MaximumBytesAsked,
                Is.EqualTo(new[] { ClientContentResolver.MaximumManifestBytes, ClientContentResolver.MaximumFileBytes }));
        }

        [Test]
        public void AManifestThatCannotBeFetchedKeepsTheBundledSetWithoutAnotherRequest()
        {
            var actors = ActorsTable("Mira");
            var items = ItemsTable("Sword");
            var transport = new FakeTransport();

            var report = Resolve(transport, false, actors, items);

            Assert.That(report.FellBackToBundled, Is.True);
            Assert.That(report.FallbackCode, Is.EqualTo(ClientContentReport.UnavailableCode));
            Assert.That(report.FallbackDiagnostic, Does.Contain("manifest could not be fetched"));
            Assert.That(report.Files.Select(file => file.Source), Has.All.EqualTo(ClientContentSource.Bundled));
            Assert.That(transport.Requests, Is.EqualTo(new[] { ManifestUrl }));
            Assert.That(transport.TimeoutsAsked, Is.EqualTo(new[] { ClientContentResolver.ManifestTimeoutSeconds }));
            Assert.That(Directory.Exists(cacheRoot), Is.False);
        }

        // A served format newer than this build reads means the client is out of date.
        [TestCase(false)]
        [TestCase(true)]
        public void ANewerFormatKeepsTheBundledSetAndSaysTheClientIsOutOfDate(bool newerManifest)
        {
            var bundled = ItemsTable("Sword");
            var served = ItemsTable("Longsword");
            var transport = new FakeTransport();
            transport.Serve(Url(served), served.Bytes);
            transport.Serve(
                ManifestUrl,
                newerManifest
                    ? Manifest(ClientContentManifest.SupportedFormatVersion + 1, Entry(served))
                    : Manifest(Entry("text-table", "items", "en", TextTableFormat.Version + 1, served.Revision, served.Bytes.Length, RelativeUrl(served))));

            var report = Resolve(transport, false, bundled);

            Assert.That(report.FellBackToBundled, Is.True);
            Assert.That(report.UpdateRequired, Is.True);
            Assert.That(report.FallbackCode, Is.EqualTo("client_update_required"));
            Assert.That(report.Files.Single().Source, Is.EqualTo(ClientContentSource.Bundled));
            Assert.That(transport.Requests, Is.EqualTo(new[] { ManifestUrl }));
        }

        [Test]
        public void AFileTheManifestDoesNotListKeepsTheBundledSet()
        {
            var actors = ActorsTable("Mira");
            var items = ItemsTable("Sword");
            var transport = new FakeTransport();
            transport.Serve(ManifestUrl, Manifest(Entry(actors)));

            var report = Resolve(transport, false, actors, items);

            Assert.That(report.FellBackToBundled, Is.True);
            Assert.That(report.FallbackDiagnostic, Does.Contain("does not list text-table en/items"));
        }

        [Test]
        public void OldRevisionsOfAFileAreRemovedAfterASuccessfulResolution()
        {
            var actors = ActorsTable("Mira");
            var oldActors = ActorsTable("Old Mira");
            var bundledItems = ItemsTable("Sword");
            var oldItems = ItemsTable("Old Sword");
            var servedItems = ItemsTable("Longsword");
            foreach (var old in new[] { oldActors, oldItems })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath(old)));
                File.WriteAllBytes(CachePath(old), old.Bytes);
            }

            var leftover = Path.Combine(Path.GetDirectoryName(CachePath(oldItems)), "partial.json.abc.tmp");
            File.WriteAllText(leftover, "{");
            var transport = new FakeTransport();
            transport.Serve(ManifestUrl, Manifest(Entry(actors), Entry(servedItems)));
            transport.Serve(Url(servedItems), servedItems.Bytes);

            var report = Resolve(transport, false, actors, bundledItems);

            Assert.That(report.FellBackToBundled, Is.False, report.FallbackDiagnostic);
            Assert.That(
                Directory.GetFiles(Path.GetDirectoryName(CachePath(servedItems))).Select(path => Path.GetFileName(path)),
                Is.EqualTo(new[] { servedItems.Revision + ".json" }));
            Assert.That(File.Exists(CachePath(oldActors)), Is.False, "A bundled file keeps no cached revision.");
        }

        [Test]
        public void IgnoringBundledContentTakesEveryFileFromADownloadAndThenTheCache()
        {
            var actors = ActorsTable("Mira");
            var items = ItemsTable("Sword");
            var transport = new FakeTransport();
            transport.Serve(ManifestUrl, Manifest(Entry(actors), Entry(items)));
            transport.Serve(Url(actors), actors.Bytes);
            transport.Serve(Url(items), items.Bytes);

            var first = Resolve(transport, true, actors, items);
            var second = Resolve(transport, true, actors, items);

            Assert.That(first.FellBackToBundled, Is.False, first.FallbackDiagnostic);
            Assert.That(first.Files.Select(file => file.Source), Has.All.EqualTo(ClientContentSource.Downloaded));
            Assert.That(second.Files.Select(file => file.Source), Has.All.EqualTo(ClientContentSource.Cache));
            Assert.That(transport.Requests, Is.EqualTo(new[] { ManifestUrl, Url(actors), Url(items), ManifestUrl }));
        }

        // A client whose bundled item catalog and item names are older than the served ones runs
        // with the served values once the real loaders build them.
        [Test]
        public void TheServedItemCatalogAndItemNamesReplaceOlderBundledOnes()
        {
            var bundledCatalog = File.ReadAllText(FixtureItemCatalog.RuntimePath);
            var bundledNames = File.ReadAllText(FixtureItemCatalog.TextTablePath);
            Assert.That(ClientItemCatalog.TryParseGameplay(bundledCatalog, out var bundledGameplay, out var error), Is.True, error);
            var definition = bundledGameplay.Definitions.First();
            var servedCatalog = WithItemValue(bundledCatalog, bundledGameplay.Revision, definition.Id, definition.Value + 7);
            Assert.That(ClientItemCatalog.TryParseGameplay(servedCatalog, out var servedGameplay, out error), Is.True, error);
            Assert.That(ClientTexts.TryParse(bundledNames, out var bundledTable, out error), Is.True, error);
            var nameKey = TextKeys.ItemName(definition.Id);
            Assert.That(bundledTable.Entries.Select(entry => entry.Key), Has.Member(nameKey));
            var servedNames = Serialize(TextTable.Create(
                bundledTable.TableId,
                bundledTable.Locale,
                bundledTable.Entries.Select(entry => new TextTableEntry
                {
                    Key = entry.Key,
                    Text = entry.Key == nameKey ? "Served Name" : entry.Text
                })));

            var transport = new FakeTransport();
            var catalogUrl = "/api/content/item-catalog/core/" + servedGameplay.Revision;
            transport.Serve(BaseUrl + catalogUrl, Encoding.UTF8.GetBytes(servedCatalog));
            transport.Serve(Url(servedNames), servedNames.Bytes);
            transport.Serve(ManifestUrl, Manifest(
                Entry("item-catalog", "core", null, ItemCatalogFormat.Version, servedGameplay.Revision, Encoding.UTF8.GetByteCount(servedCatalog), catalogUrl),
                Entry(servedNames)));
            var requests = new List<ClientContentRequest> { ClientItemCatalog.ContentRequest(bundledCatalog) };
            requests.AddRange(ClientTexts.ContentRequests(new[] { bundledNames }));

            var report = Run(new ClientContentResolver(BaseUrl, transport, new ClientContentCache(cacheRoot), requests, false));

            Assert.That(report.FellBackToBundled, Is.False, report.FallbackDiagnostic);
            Assert.That(report.Files.Select(file => file.Source), Has.All.EqualTo(ClientContentSource.Downloaded));
            Assert.That(
                ClientTexts.TryLoad(
                    report.TextsOf(ClientContentKinds.TextTable).Concat(new[] { Serialize(FixtureTexts.UiTable()).Json }).ToArray(),
                    out var texts,
                    out error),
                Is.True,
                error);
            Assert.That(
                ClientItemCatalog.TryLoad(report.TextOf(ClientContentKinds.ItemCatalog, ClientContentKinds.CoreCatalogId), texts, out var catalog, out error),
                Is.True,
                error);
            Assert.That(catalog.Revision, Is.EqualTo(servedGameplay.Revision));
            Assert.That(catalog.TryGetDefinition(definition.Id, out var served), Is.True);
            Assert.That(served.Value, Is.EqualTo(definition.Value + 7));
            Assert.That(catalog.GetDisplayName(definition.Id), Is.EqualTo("Served Name"));
        }

        private ClientContentReport Resolve(FakeTransport transport, bool ignoreBundled, params TableFile[] bundled)
        {
            var requests = ClientTexts.ContentRequests(bundled.Select(table => table.Json).ToArray());
            return Run(new ClientContentResolver(BaseUrl, transport, new ClientContentCache(cacheRoot), requests, ignoreBundled));
        }

        private static ClientContentReport Run(ClientContentResolver resolver)
        {
            ClientContentReport report = null;
            var stack = new Stack<IEnumerator>();
            stack.Push(resolver.Resolve(value => report = value));
            var steps = 0;
            while (stack.Count > 0)
            {
                Assert.That(++steps, Is.LessThan(10000), "The resolver does not end.");
                var routine = stack.Peek();
                if (!routine.MoveNext())
                {
                    stack.Pop();
                }
                else if (routine.Current is IEnumerator nested)
                {
                    stack.Push(nested);
                }
            }

            Assert.That(report, Is.Not.Null);
            return report;
        }

        private string CachePath(TableFile table)
        {
            return Path.Combine(cacheRoot, "text-table", table.Locale, table.Id, table.Revision + ".json");
        }

        private static TableFile ActorsTable(string name)
        {
            return Table(TextTableIds.Actors, TextTableFormat.SourceLocale, (TextKeys.ActorName("npc.test"), name));
        }

        private static TableFile ItemsTable(string name)
        {
            return Table(TextTableIds.Items, TextTableFormat.SourceLocale, (TextKeys.ItemName("test.sword"), name));
        }

        private static TableFile Table(string tableId, string locale, params (string Key, string Text)[] entries)
        {
            return Serialize(TextTable.Create(
                tableId,
                locale,
                entries.Select(entry => new TextTableEntry { Key = entry.Key, Text = entry.Text })));
        }

        private static TableFile Serialize(TextTableDocument table)
        {
            var json = JsonUtility.ToJson(
                new TextTableJsonDocument
                {
                    formatVersion = table.FormatVersion,
                    tableId = table.TableId,
                    locale = table.Locale,
                    revision = table.Revision,
                    entries = table.Entries
                        .Select(entry => new TextTableJsonEntry { key = entry.Key, text = entry.Text })
                        .ToArray()
                },
                true);
            return new TableFile(table.TableId, table.Locale, table.Revision, json);
        }

        // Changes one item's value in compiled catalog JSON and states the revision of the result.
        private static string WithItemValue(string json, string revision, string definitionId, long value)
        {
            var definitionAt = json.IndexOf("\"id\": \"" + definitionId + "\"", StringComparison.Ordinal);
            Assert.That(definitionAt, Is.GreaterThan(0));
            const string valueProperty = "\"value\": ";
            var valueAt = json.IndexOf(valueProperty, definitionAt, StringComparison.Ordinal) + valueProperty.Length;
            var valueEnd = valueAt;
            while (char.IsDigit(json[valueEnd]))
            {
                valueEnd++;
            }

            var changed = json.Substring(0, valueAt) + value + json.Substring(valueEnd);
            var compiled = ItemCatalogCompiler.CompileCompiled(
                ClientItemCatalogJson.Deserialize(changed, out _).ToAuthoringDocument());
            return changed.Replace("\"revision\": \"" + revision + "\"", "\"revision\": \"" + compiled.Revision + "\"");
        }

        private static string RelativeUrl(TableFile table)
        {
            return "/api/content/text-table/" + table.Locale + "/" + table.Id + "/" + table.Revision;
        }

        private static string Url(TableFile table)
        {
            return BaseUrl + RelativeUrl(table);
        }

        private static string Entry(TableFile table)
        {
            return Entry(table, table.Bytes.Length);
        }

        private static string Entry(TableFile table, long sizeBytes)
        {
            return Entry("text-table", table.Id, table.Locale, TextTableFormat.Version, table.Revision, sizeBytes, RelativeUrl(table));
        }

        private static string Entry(string kind, string id, string locale, int formatVersion, string revision, long sizeBytes, string url)
        {
            return "{\"kind\":\"" + kind + "\",\"id\":\"" + id + "\""
                + (locale != null ? ",\"locale\":\"" + locale + "\"" : string.Empty)
                + ",\"formatVersion\":" + formatVersion
                + ",\"revision\":\"" + revision + "\""
                + ",\"sizeBytes\":" + sizeBytes
                + ",\"url\":\"" + url + "\"}";
        }

        private static byte[] Manifest(params string[] entries)
        {
            return Manifest(ClientContentManifest.SupportedFormatVersion, entries);
        }

        private static byte[] Manifest(int formatVersion, params string[] entries)
        {
            return Encoding.UTF8.GetBytes(
                "{\"formatVersion\":" + formatVersion + ",\"files\":[" + string.Join(",", entries) + "]}");
        }

        private sealed class TableFile
        {
            public TableFile(string id, string locale, string revision, string json)
            {
                Id = id;
                Locale = locale;
                Revision = revision;
                Json = json;
                Bytes = Encoding.UTF8.GetBytes(json);
            }

            public string Id { get; }

            public string Locale { get; }

            public string Revision { get; }

            public string Json { get; }

            public byte[] Bytes { get; }
        }

        // Answers at once from a fixed set of URLs; every other URL is refused, like a closed port.
        private sealed class FakeTransport : IClientContentTransport
        {
            private readonly Dictionary<string, byte[]> bodies = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            public List<string> Requests { get; } = new List<string>();

            public List<int> TimeoutsAsked { get; } = new List<int>();

            public List<long> MaximumBytesAsked { get; } = new List<long>();

            public void Serve(string url, byte[] body)
            {
                bodies[url] = body;
            }

            public IEnumerator Get(string url, int timeoutSeconds, long maximumBytes, Action<ClientContentResponse> onCompleted)
            {
                Requests.Add(url);
                TimeoutsAsked.Add(timeoutSeconds);
                MaximumBytesAsked.Add(maximumBytes);
                if (!bodies.TryGetValue(url, out var body))
                {
                    onCompleted(ClientContentResponse.Failure("Connection refused."));
                }
                else if (body.LongLength > maximumBytes)
                {
                    onCompleted(ClientContentResponse.Failure("The response is too large."));
                }
                else
                {
                    onCompleted(ClientContentResponse.Success(body));
                }

                yield break;
            }
        }
    }
}
