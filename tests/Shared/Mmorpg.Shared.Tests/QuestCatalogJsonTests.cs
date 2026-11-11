using System.Text.Json;
using System.Text.Json.Serialization;
using Mmorpg.Testing;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Shared.Tests;

// The Unity client reads the compiled quest catalog through the camelCase JSON shape, so that
// shape must name every field the compiler writes and convert to a catalog that validates.
public sealed class QuestCatalogJsonTests
{
    // The field names Unity's JsonUtility matches exactly, with nothing in the JSON left over.
    private static readonly JsonSerializerOptions ExactFields = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [Theory]
    [InlineData("src/Shared/WorldData/Generated/Quests/core.quest-catalog.json")]
    [InlineData("tests/Fixtures/Quests/generated/Quests/core.quest-catalog.json")]
    public void TheJsonShapeReadsTheCompiledCatalogIntoAValidCatalog(string path)
    {
        var json = File.ReadAllText(Path.Combine(RepositoryPaths.Root, path));

        var runtime = JsonSerializer.Deserialize<QuestCatalogJsonDocument>(json, ExactFields)!.ToRuntime();

        QuestCatalogCompiler.ValidateRuntime(runtime);
        Assert.NotEmpty(runtime.Quests);
        // The compiled catalog carries no text, so the shape reads every text as empty.
        Assert.All(runtime.Quests, quest => Assert.True(string.IsNullOrEmpty(quest.OfferText)));
        Assert.DoesNotContain("offerText", json, StringComparison.Ordinal);
    }
}
