using System.Text;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Content.Tests;

// The manifest is a contract with the Unity client, whose EditMode tests read the same sample
// files in tests/Fixtures/ContentManifest. A changed field name, order, or URL shape here
// fails until both sides and the samples change together.
public sealed class ContentManifestSampleTests
{
    public const string BaseUrl = "https://cdn.example.com/content";

    [Theory]
    [InlineData(null, "manifest.json")]
    [InlineData(BaseUrl, "manifest-base-url.json")]
    public void TheManifestOfAFixedSetOfFilesIsTheCheckedInSample(string? baseUrl, string sampleName)
    {
        var store = new ContentFileStore(SampleFiles(), baseUrl);

        // The sample ends with the line feed every checked-in text file has.
        var sample = File.ReadAllText(Path.Combine(RepositoryPaths.ContentManifestFixtureDirectory, sampleName));
        Assert.EndsWith("\n", sample, StringComparison.Ordinal);
        Assert.Equal(sample[..^1], Encoding.UTF8.GetString(store.ManifestBytes));
    }

    // Given out of order, so the sample also pins the order of the manifest.
    private static ContentFile[] SampleFiles()
    {
        return
        [
            new ContentFile(ContentFileKinds.TextTable, "ui", "en", 1, new string('d', 64), new byte[45938]),
            new ContentFile(ContentFileKinds.QuestCatalog, "core", null, 2, new string('b', 64), new byte[8123]),
            new ContentFile(ContentFileKinds.TextTable, "items", "pt-br", 1, new string('c', 64), new byte[2048]),
            new ContentFile(ContentFileKinds.ItemCatalog, "core", null, 9, new string('a', 64), new byte[23779])
        ];
    }
}
