using Mmorpg.Testing;
using Mmorpg.Testing.Content;
using Mmorpg.Tools.ContentPipeline;
using Mmorpg.Tools.ItemCatalogCompiler;
using Mmorpg.Tools.WorldActorCompiler;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Worlds;

namespace Mmorpg.Tools.Tests;

public sealed class ContentPipelineTests
{
    [Theory]
    [InlineData("weapon.training_rifle")]
    [InlineData("mob.feral_wolf")]
    [InlineData("a")]
    [InlineData("quest_item.signal-transponder2")]
    [InlineData("console.panel")]
    public void PortableDefinitionIdsNameContentFiles(string definitionId)
    {
        Assert.True(ContentLayout.IsValidDefinitionId(definitionId));
        Assert.Equal(definitionId + ".json", ContentLayout.DefinitionFileName(definitionId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData("-leading")]
    [InlineData("Weapon.rifle")]
    [InlineData("weapon/rifle")]
    [InlineData("weapon\\rifle")]
    [InlineData("weapon rifle")]
    [InlineData("con")]
    [InlineData("nul.item")]
    [InlineData("lpt1.tool")]
    public void UnsafeDefinitionIdsCannotNameContentFiles(string definitionId)
    {
        Assert.False(ContentLayout.IsValidDefinitionId(definitionId));
        Assert.Throws<InvalidDataException>(() => ContentLayout.DefinitionFileName(definitionId));
    }

    [Fact]
    public void CheckedInDefinitionFilesAreListedInIdOrder()
    {
        var items = ContentLayout.ListDefinitionFiles(ItemCatalogTestData.ItemsDirectory);
        var actors = ContentLayout.ListDefinitionFiles(WorldActorTestData.ActorsDirectory);

        Assert.Equal(9, items.Length);
        Assert.Equal(items.Select(file => file.Id).Order(StringComparer.Ordinal), items.Select(file => file.Id));
        Assert.Equal(
            ["mob.feral_alpha", "mob.feral_wolf", "npc.city_guard", "npc.city_services"],
            actors.Select(file => file.Id));
    }

    [Fact]
    public void DefinitionFileMustContainItsOwnId()
    {
        using var content = TemporaryContent.CopyOf(ItemCatalogTestData.ItemsDirectory);
        var source = ContentLayout.DefinitionPath(content.Path, "armor.starter_vest");
        File.Move(source, ContentLayout.DefinitionPath(content.Path, "armor.renamed_vest"));

        var exception = Assert.Throws<InvalidDataException>(
            () => ItemCatalogContent.Load(content.Path));

        Assert.Contains("must contain id 'armor.renamed_vest'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnexpectedFilesInDefinitionsDirectoryFail()
    {
        using var content = TemporaryContent.CopyOf(WorldActorTestData.ActorsDirectory);
        File.WriteAllText(
            Path.Combine(ContentLayout.DefinitionsDirectory(content.Path), "notes.txt"),
            "not a definition");

        Assert.Throws<InvalidDataException>(() => WorldActorContent.LoadCatalog(content.Path));
    }

    [Fact]
    public void HiddenFilesInDefinitionsDirectoryAreIgnored()
    {
        using var content = TemporaryContent.CopyOf(ItemCatalogTestData.ItemsDirectory);
        File.WriteAllText(
            Path.Combine(ContentLayout.DefinitionsDirectory(content.Path), ".DS_Store"),
            "operating system metadata");

        Assert.Equal(9, ItemCatalogContent.Load(content.Path).Definitions.Length);
    }

    [Fact]
    public void SpawnFileMustDeclareItsWorldDirectory()
    {
        using var content = TemporaryContent.CopyOf(RepositoryPaths.Content);
        var path = ContentLayout.ActorSpawnsPath(content.Path, "development-world-2");
        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace(
                "\"worldId\": \"development-world-2\"",
                "\"worldId\": \"development-world-1\"",
                StringComparison.Ordinal));

        var exception = Assert.Throws<InvalidDataException>(
            () => WorldActorContent.LoadSpawns(content.Path, "development-world-2"));

        Assert.Contains("must declare worldId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorldsAreListedInIdOrderAndHiddenDirectoriesAreIgnored()
    {
        using var worlds = TemporaryContent.CopyOf(RepositoryPaths.Worlds);
        Directory.CreateDirectory(Path.Combine(worlds.Path, ".staging"));

        Assert.Equal(
            ["development-world-1", "development-world-2"],
            ContentLayout.ListWorldIds(worlds.Path));
        Assert.Equal(
            ContentLayout.ListWorldIds(RepositoryPaths.Worlds),
            WorldActorContent.ListWorldIds(RepositoryPaths.Content));
    }

    // Both World compilers list Worlds this way, so neither can skip an incomplete World
    // that the SimulationWorker would then fail to load.
    [Theory]
    [InlineData(WorldManifestFormat.FileName)]
    [InlineData(ContentLayout.ActorSpawnsFileName)]
    [InlineData(ContentLayout.CollisionSourceFileName)]
    public void AWorldWithoutARequiredFileFailsTheListing(string fileName)
    {
        Assert.Contains(fileName, ContentLayout.RequiredWorldFileNames);
        using var content = TemporaryContent.CopyOf(RepositoryPaths.Content);
        File.Delete(Path.Combine(
            ContentLayout.WorldDirectory(content.Path, "development-world-2"),
            fileName));

        var exception = Assert.Throws<InvalidDataException>(
            () => WorldActorContent.ListWorldIds(content.Path));

        Assert.Contains("'development-world-2'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(fileName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingWorldRootListsNoWorlds()
    {
        using var content = TemporaryContent.Empty();

        Assert.Empty(ContentLayout.ListWorldIds(ContentLayout.WorldsDirectory(content.Path)));
    }

    [Fact]
    public void ToolOptionsDefaultToRepositoryContentAndGeneratedData()
    {
        Assert.True(ContentToolOptions.TryParse([], out var options, out var error), error);

        Assert.Equal(RepositoryPaths.Content, options!.ContentRoot);
        Assert.Equal(RepositoryPaths.Generated, options.GeneratedRoot);
        Assert.False(options.VerifyOnly);
    }

    [Fact]
    public void ToolOptionsAcceptExplicitRootsAndVerify()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "content-root");
        var generatedRoot = Path.Combine(Path.GetTempPath(), "generated-root");

        Assert.True(
            ContentToolOptions.TryParse(
                ["--verify", "--content-root", contentRoot, "--generated-root", generatedRoot],
                out var options,
                out var error),
            error);

        Assert.Equal(contentRoot, options!.ContentRoot);
        Assert.Equal(generatedRoot, options.GeneratedRoot);
        Assert.True(options.VerifyOnly);
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--verify", "--verify")]
    [InlineData("--content-root")]
    [InlineData("--content-root", "--verify")]
    [InlineData("--generated-root", "one", "--generated-root", "two")]
    public void ToolOptionsRejectInvalidArguments(params string[] arguments)
    {
        Assert.False(ContentToolOptions.TryParse(arguments, out var options, out var error));
        Assert.Null(options);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void VerifyRejectsStaleOrMissingGeneratedFiles()
    {
        using var output = TemporaryContent.Empty();
        var path = Path.Combine(output.Path, "Items", "catalog.json");

        Assert.Throws<InvalidDataException>(
            () => GeneratedOutput.WriteOrVerifyText(path, "{}\n", true, "Generated test file"));
        GeneratedOutput.WriteOrVerifyText(path, "{}\n", false, "Generated test file");
        GeneratedOutput.WriteOrVerifyText(path, "{}\n", true, "Generated test file");
        Assert.Throws<InvalidDataException>(
            () => GeneratedOutput.WriteOrVerifyText(path, "[]\n", true, "Generated test file"));
    }

    [Fact]
    public void StaleGeneratedOutputIsRejectedInVerifyAndDeletedWithMetaInCompile()
    {
        using var output = TemporaryContent.Empty();
        var worldDirectory = Path.Combine(output.Path, "Worlds", "removed-world");
        var stale = Path.Combine(worldDirectory, ContentLayout.WorldActorsFileName);
        Directory.CreateDirectory(worldDirectory);
        File.WriteAllText(stale, "{}\n");
        File.WriteAllText(stale + ".meta", "guid");
        File.WriteAllText(worldDirectory + ".meta", "guid");

        Assert.Throws<InvalidDataException>(
            () => GeneratedOutput.RemoveOrRejectStale([stale], true, "Compiled world actors"));
        Assert.True(File.Exists(stale));

        GeneratedOutput.RemoveOrRejectStale([stale], false, "Compiled world actors");

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(stale + ".meta"));
        Assert.False(Directory.Exists(worldDirectory));
        Assert.False(File.Exists(worldDirectory + ".meta"));
    }

    private sealed class TemporaryContent : IDisposable
    {
        private TemporaryContent(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryContent Empty()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "MmorpgContentPipelineTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryContent(path);
        }

        public static TemporaryContent CopyOf(string sourceDirectory)
        {
            var content = Empty();
            foreach (var source in Directory.EnumerateFiles(
                         sourceDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                var target = System.IO.Path.Combine(
                    content.Path,
                    System.IO.Path.GetRelativePath(sourceDirectory, source));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }

            return content;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
