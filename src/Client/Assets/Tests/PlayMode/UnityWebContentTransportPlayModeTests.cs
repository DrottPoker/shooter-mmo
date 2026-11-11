using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Mmorpg.Client.Core;
using Mmorpg.WorldData.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.PlayMode
{
    // The real content transport over HTTP against a loopback server, and the resolver with it.
    public sealed class UnityWebContentTransportPlayModeTests
    {
        private LoopbackHttpServer server;
        private UnityWebContentTransport transport;
        private string cacheRoot;

        [SetUp]
        public void StartServer()
        {
            server = new LoopbackHttpServer();
            transport = new UnityWebContentTransport();
            cacheRoot = Path.Combine(Path.GetTempPath(), "mmorpg-transport-tests-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void StopServer()
        {
            transport.Dispose();
            server.Dispose();
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, true);
            }
        }

        [UnityTest]
        public IEnumerator ABodyArrivesByteForByte()
        {
            var body = new byte[100 * 1024 + 17];
            new System.Random(7).NextBytes(body);
            server.Serve("/file", body);

            ClientContentResponse response = null;
            yield return transport.Get(server.BaseUrl + "/file", 5, ClientContentResolver.MaximumFileBytes, value => response = value);

            Assert.That(response.Succeeded, Is.True, response.Error);
            Assert.That(response.Body, Is.EqualTo(body));
        }

        [UnityTest]
        public IEnumerator ANotFoundAnswerFails()
        {
            server.ServeStatus("/missing", 404);

            ClientContentResponse response = null;
            yield return transport.Get(server.BaseUrl + "/missing", 5, ClientContentResolver.MaximumFileBytes, value => response = value);

            Assert.That(response.Succeeded, Is.False);
            Assert.That(response.Body, Is.Null);
            Assert.That(response.Error, Does.Contain("404"));
        }

        // The transport stops reading at the cap, so the server cannot send the whole body.
        [UnityTest]
        public IEnumerator ABodyAboveTheCapIsAbortedAndFails()
        {
            const long announced = 256L * 1024L * 1024L;
            const long cap = 64L * 1024L;
            server.ServeStream("/huge", announced);

            ClientContentResponse response = null;
            yield return transport.Get(server.BaseUrl + "/huge", 10, cap, value => response = value);

            Assert.That(response.Succeeded, Is.False);
            Assert.That(response.Error, Does.Contain("larger than " + cap));
            Assert.That(server.StreamedBytes, Is.LessThan(announced / 2), "The client read on after the cap.");
        }

        [UnityTest]
        public IEnumerator AServerThatNeverAnswersFailsAtTheTimeLimit()
        {
            server.ServeSilence("/silent");
            var clock = Stopwatch.StartNew();

            ClientContentResponse response = null;
            yield return transport.Get(server.BaseUrl + "/silent", 1, ClientContentResolver.MaximumFileBytes, value => response = value);

            Assert.That(response.Succeeded, Is.False);
            Assert.That(clock.Elapsed.TotalSeconds, Is.GreaterThan(0.5).And.LessThan(5));
        }

        // The manifest URL is joined to the base URL, one file has a relative URL and one an
        // absolute URL, and both are downloaded, checked, and cached over real HTTP.
        [UnityTest]
        public IEnumerator TheResolverDownloadsAManifestAndItsFilesOverHttp()
        {
            var bundledActors = Table(TextTableIds.Actors, (TextKeys.ActorName("npc.test"), "Mira"));
            var servedActors = Table(TextTableIds.Actors, (TextKeys.ActorName("npc.test"), "Mira the Trader"));
            var bundledItems = Table(TextTableIds.Items, (TextKeys.ItemName("test.sword"), "Sword"));
            var servedItems = Table(TextTableIds.Items, (TextKeys.ItemName("test.sword"), "Longsword"));
            var actorsPath = "/api/content/text-table/en/actors/" + servedActors.Revision;
            var itemsPath = "/mirror/text-table/en/items/" + servedItems.Revision;
            server.Serve(actorsPath, servedActors.Bytes);
            server.Serve(itemsPath, servedItems.Bytes);
            server.Serve(ClientContentResolver.ManifestPath, Encoding.UTF8.GetBytes(
                "{\"formatVersion\":1,\"files\":["
                + Entry(servedActors, actorsPath) + ","
                + Entry(servedItems, server.BaseUrl + itemsPath) + "]}"));
            var requests = ClientTexts.ContentRequests(new[] { bundledActors.Json, bundledItems.Json });
            var resolver = new ClientContentResolver(server.BaseUrl + "/", transport, new ClientContentCache(cacheRoot), requests, false);

            ClientContentReport report = null;
            yield return resolver.Resolve(value => report = value);

            Assert.That(report.FellBackToBundled, Is.False, report.FallbackDiagnostic);
            Assert.That(report.Files.Select(file => file.Source), Has.All.EqualTo(ClientContentSource.Downloaded));
            Assert.That(report.Files.Select(file => file.Text), Is.EqualTo(new[] { servedActors.Json, servedItems.Json }));
            Assert.That(
                File.ReadAllBytes(Path.Combine(cacheRoot, "text-table", "en", "items", servedItems.Revision + ".json")),
                Is.EqualTo(servedItems.Bytes));
        }

        private static string Entry(TableFile table, string url)
        {
            return "{\"kind\":\"text-table\",\"id\":\"" + table.Id + "\",\"locale\":\"en\",\"formatVersion\":"
                + TextTableFormat.Version + ",\"revision\":\"" + table.Revision + "\",\"sizeBytes\":"
                + table.Bytes.Length + ",\"url\":\"" + url + "\"}";
        }

        private static TableFile Table(string tableId, params (string Key, string Text)[] entries)
        {
            var table = TextTable.Create(
                tableId,
                TextTableFormat.SourceLocale,
                entries.Select(entry => new TextTableEntry { Key = entry.Key, Text = entry.Text }));
            var json = JsonUtility.ToJson(new TextTableJsonDocument
            {
                formatVersion = table.FormatVersion,
                tableId = table.TableId,
                locale = table.Locale,
                revision = table.Revision,
                entries = table.Entries.Select(entry => new TextTableJsonEntry { key = entry.Key, text = entry.Text }).ToArray()
            });
            return new TableFile(table.TableId, table.Revision, json);
        }

        private sealed class TableFile
        {
            public TableFile(string id, string revision, string json)
            {
                Id = id;
                Revision = revision;
                Json = json;
                Bytes = Encoding.UTF8.GetBytes(json);
            }

            public string Id { get; }

            public string Revision { get; }

            public string Json { get; }

            public byte[] Bytes { get; }
        }
    }
}
