using Mmorpg.Server.Common.Characters;
using Mmorpg.Testing;

namespace Mmorpg.Server.Common.Tests;

public sealed class CharacterAppearanceFileLoaderTests
{
    [Fact]
    public void CheckedInContentOffersBothHumanBodiesAndTheSlotsOthersSee()
    {
        var catalog = CharacterAppearanceFileLoader.Load(RepositoryPaths.CharacterAppearancePath);

        Assert.Equal(["Human_Male", "Human_Female"], catalog.BodyModels);
        Assert.Equal(["head", "body", "legs", "hands", "feet", "back", "main_hand", "off_hand"], catalog.VisibleEquipmentSlots);
        Assert.Equal(
            ["face", "eyes", "scar", "tattoo", "underwear", "hair"],
            catalog.Kinds.Select(kind => kind.Id));
        // Every body model starts with a look it may be created with.
        foreach (var bodyModel in catalog.BodyModels)
        {
            Assert.Null(catalog.CreationError(bodyModel, catalog.DefaultChoices(bodyModel)));
        }
    }

    [Fact]
    public void ARelativePathResolvesNextToTheRunningAssembly()
    {
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, CharacterAppearanceFileLoader.DefaultRelativePath)),
            CharacterAppearanceFileLoader.ResolvePath(null));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("{", "malformed")]
    [InlineData("""{ "formatVersion": 1, "formatVersion": 1 }""", "duplicate property")]
    [InlineData("""{ "formatVersion": 1, "colors": [] }""", "malformed")]
    [InlineData("""{ "formatVersion": 1, "kinds": [], "bodyModels": [], "visibleEquipmentSlots": [] }""", "invalid")]
    public void BrokenContentIsRejected(string json, string reason)
    {
        var exception = Assert.Throws<InvalidDataException>(() => CharacterAppearanceFileLoader.Parse(json));
        Assert.Contains(reason, exception.Message);
    }

    [Fact]
    public void MissingContentIsRejected()
    {
        Assert.Throws<FileNotFoundException>(() => CharacterAppearanceFileLoader.Load(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "appearance.json")));
    }
}
