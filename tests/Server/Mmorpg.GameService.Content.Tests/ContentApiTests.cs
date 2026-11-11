using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mmorpg.GameService.Content;
using Mmorpg.GameService.Testing;
using Mmorpg.Server.Common.Items;
using Mmorpg.Server.Common.Quests;
using Mmorpg.WorldData.Text;

namespace Mmorpg.GameService.Content.Tests;

public sealed class ContentApiTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManifestListsBothCatalogsAndEveryTextTableWithTheirRevisions()
    {
        var options = GameServiceTestComposition.CreateContentOptions();
        await using var host = await ContentApiTestHost.StartAsync(options);

        using var response = await host.Client.GetAsync("/api/content", Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag.IsWeak);
        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(1, manifest.RootElement.GetProperty("formatVersion").GetInt32());
        var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();

        var item = Assert.Single(files, file => Kind(file) == "item-catalog");
        Assert.Equal("core", item.GetProperty("id").GetString());
        Assert.False(item.TryGetProperty("locale", out _));
        Assert.Equal(ItemCatalogFileLoader.Load(options.ItemCatalogPath).Revision, Revision(item));
        Assert.Equal(new FileInfo(options.ItemCatalogPath).Length, item.GetProperty("sizeBytes").GetInt64());
        Assert.Equal($"/api/content/item-catalog/core/{Revision(item)}", Url(item));

        var quest = Assert.Single(files, file => Kind(file) == "quest-catalog");
        Assert.Equal("core", quest.GetProperty("id").GetString());
        Assert.Equal(QuestCatalogFileLoader.Load(options.QuestCatalogPath).Revision, Revision(quest));
        Assert.Equal($"/api/content/quest-catalog/core/{Revision(quest)}", Url(quest));

        var tablePaths = Directory.GetFiles(options.TextTablesPath, "*" + TextTableFormat.FileSuffix);
        var tables = files.Where(file => Kind(file) == "text-table").ToArray();
        Assert.Equal(tablePaths.Length, tables.Length);
        foreach (var path in tablePaths)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var tableId = document.RootElement.GetProperty("tableId").GetString();
            var table = Assert.Single(tables, file => file.GetProperty("id").GetString() == tableId);
            Assert.Equal("en", table.GetProperty("locale").GetString());
            Assert.Equal(document.RootElement.GetProperty("revision").GetString(), Revision(table));
            Assert.Equal(TextTableFormat.Version, table.GetProperty("formatVersion").GetInt32());
            Assert.Equal(new FileInfo(path).Length, table.GetProperty("sizeBytes").GetInt64());
            Assert.Equal($"/api/content/text-table/en/{tableId}/{Revision(table)}", Url(table));
        }
    }

    [Fact]
    public async Task EveryListedUrlReturnsTheExactFileBytesAsImmutable()
    {
        var options = GameServiceTestComposition.CreateContentOptions();
        await using var host = await ContentApiTestHost.StartAsync(options);
        var expectedBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["item-catalog"] = File.ReadAllBytes(options.ItemCatalogPath),
            ["quest-catalog"] = File.ReadAllBytes(options.QuestCatalogPath)
        };
        foreach (var path in Directory.GetFiles(options.TextTablesPath, "*" + TextTableFormat.FileSuffix))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            expectedBytes["text-table:" + document.RootElement.GetProperty("tableId").GetString()] =
                File.ReadAllBytes(path);
        }

        var files = await ReadManifestFilesAsync(host);
        Assert.Equal(expectedBytes.Count, files.Length);
        foreach (var file in files)
        {
            using var response = await host.Client.GetAsync(Url(file), Cancellation);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            var cacheControl = response.Headers.CacheControl;
            Assert.NotNull(cacheControl);
            Assert.True(cacheControl.Public);
            Assert.Equal(TimeSpan.FromSeconds(31_536_000), cacheControl.MaxAge);
            Assert.Contains(cacheControl.Extensions, extension => extension.Name == "immutable");
            Assert.Equal($"\"{Revision(file)}\"", response.Headers.ETag?.Tag);
            var key = Kind(file) == "text-table"
                ? "text-table:" + file.GetProperty("id").GetString()
                : Kind(file);
            Assert.Equal(expectedBytes[key], await response.Content.ReadAsByteArrayAsync(Cancellation));
        }
    }

    [Theory]
    [InlineData("/api/content/item-catalogue/core/{item}")]
    [InlineData("/api/content/item-catalog/other/{item}")]
    [InlineData("/api/content/item-catalog/core/{quest}")]
    [InlineData("/api/content/item-catalog/core/0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("/api/content/item-catalog/core/not-a-revision")]
    [InlineData("/api/content/item-catalog/Core/{item}")]
    [InlineData("/api/content/item-catalog/en/core/{item}")]
    [InlineData("/api/content/text-table/ui/{ui}")]
    [InlineData("/api/content/text-table/de/ui/{ui}")]
    [InlineData("/api/content/text-table/EN/ui/{ui}")]
    [InlineData("/api/content/text-table/en/missing/{ui}")]
    [InlineData("/api/content/text-table/en/ui/{item}")]
    public async Task AnUnknownKindIdLocaleOrRevisionIsNotFound(string pathTemplate)
    {
        await using var host = await ContentApiTestHost.StartAsync(GameServiceTestComposition.CreateContentOptions());
        var files = await ReadManifestFilesAsync(host);
        var path = pathTemplate
            .Replace("{item}", Revision(files.Single(file => Kind(file) == "item-catalog")), StringComparison.Ordinal)
            .Replace("{quest}", Revision(files.Single(file => Kind(file) == "quest-catalog")), StringComparison.Ordinal)
            .Replace(
                "{ui}",
                Revision(files.Single(file => file.GetProperty("id").GetString() == TextTableIds.Ui)),
                StringComparison.Ordinal);

        using var response = await host.Client.GetAsync(path, Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.ETag);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal("content_not_found", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ManifestAndFilesHonorIfNoneMatch()
    {
        await using var host = await ContentApiTestHost.StartAsync(GameServiceTestComposition.CreateContentOptions());
        using var manifest = await host.Client.GetAsync("/api/content", Cancellation);
        var etag = manifest.Headers.ETag!;

        using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/content");
        unchangedRequest.Headers.IfNoneMatch.Add(etag);
        using var unchanged = await host.Client.SendAsync(unchangedRequest, Cancellation);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Equal(etag, unchanged.Headers.ETag);
        Assert.True(unchanged.Headers.CacheControl?.NoCache);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync(Cancellation));

        using var staleRequest = new HttpRequestMessage(HttpMethod.Get, "/api/content");
        staleRequest.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"stale\""));
        using var stale = await host.Client.SendAsync(staleRequest, Cancellation);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Equal(
            await manifest.Content.ReadAsByteArrayAsync(Cancellation),
            await stale.Content.ReadAsByteArrayAsync(Cancellation));

        var file = (await ReadManifestFilesAsync(host)).First();
        using var fileRequest = new HttpRequestMessage(HttpMethod.Get, Url(file));
        fileRequest.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{Revision(file)}\""));
        using var unchangedFile = await host.Client.SendAsync(fileRequest, Cancellation);
        Assert.Equal(HttpStatusCode.NotModified, unchangedFile.StatusCode);
    }

    [Fact]
    public async Task ManifestUrlsUseTheConfiguredBaseUrl()
    {
        var options = GameServiceTestComposition.CreateContentOptions("https://cdn.example.test/game/content");
        await using var host = await ContentApiTestHost.StartAsync(options);

        var files = await ReadManifestFilesAsync(host);

        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var url = Url(file);
            Assert.StartsWith("https://cdn.example.test/game/content/", url, StringComparison.Ordinal);

            // The same relative path also resolves on GameService itself.
            var relativePath = url["https://cdn.example.test/game/content/".Length..];
            using var response = await host.Client.GetAsync("/api/content/" + relativePath, Cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task ContentRoutesAreAnonymousAndShareOnePerAddressRateLimit()
    {
        var options = GameServiceTestComposition.CreateContentOptions() with
        {
            RateLimitPermitLimit = 3,
            RateLimitWindow = TimeSpan.FromMinutes(10)
        };
        await using var host = await ContentApiTestHost.StartAsync(options);
        Assert.Null(host.Client.DefaultRequestHeaders.Authorization);

        var files = await ReadManifestFilesAsync(host);
        using (var first = await host.Client.GetAsync(Url(files[0]), Cancellation))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var second = await host.Client.GetAsync("/api/content/item-catalog/core/not-a-revision", Cancellation))
        {
            Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        }

        using var limited = await host.Client.GetAsync("/api/content", Cancellation);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter?.Delta);
        Assert.True(limited.Headers.RetryAfter.Delta >= TimeSpan.FromSeconds(1));
        using var problem = JsonDocument.Parse(await limited.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal("rate_limit_exceeded", problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<JsonElement[]> ReadManifestFilesAsync(ContentApiTestHost host)
    {
        using var response = await host.Client.GetAsync("/api/content", Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
        return manifest.RootElement.GetProperty("files").EnumerateArray()
            .Select(file => file.Clone())
            .ToArray();
    }

    private static string Kind(JsonElement file)
    {
        return file.GetProperty("kind").GetString()!;
    }

    private static string Revision(JsonElement file)
    {
        return file.GetProperty("revision").GetString()!;
    }

    private static string Url(JsonElement file)
    {
        return file.GetProperty("url").GetString()!;
    }
}
