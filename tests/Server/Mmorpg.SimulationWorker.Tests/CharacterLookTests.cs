using Mmorpg.Server.Common.Characters;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.Testing;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Characters;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class CharacterLookTests
{
    private static readonly CharacterAppearanceCatalog Catalog =
        CharacterAppearanceFileLoader.Load(RepositoryPaths.CharacterAppearancePath);

    [Fact]
    public void EveryBotGetsALookCreationAllowsAndKeepsItOnEveryJoin()
    {
        var looks = new DevelopmentBotLooks(Catalog, ItemCatalogTestData.CompileFixture());

        foreach (var characterId in Enumerable.Range(1, 64).Select(Id))
        {
            var look = looks.For(characterId);
            var choices = look.Appearance.Choices
                .Select(choice => new CharacterAppearanceChoice(choice.Kind, choice.Slot, choice.Option))
                .ToArray();

            Assert.Null(Catalog.CreationError(look.Appearance.BodyModel, choices));
            Assert.False(look.Appearance.HeadwearLowered);
            Assert.Equal(Describe(look), Describe(looks.For(characterId)));
            // The fixture catalog has a tunic for the body and nothing for the legs or feet.
            Assert.Contains(look.Outfit!, item => item.EquipmentSlotId == "body" && item.DefinitionId == "fixture.tunic");
            Assert.All(look.Outfit!, item => Assert.Contains(item.EquipmentSlotId, Catalog.VisibleEquipmentSlots));
        }
    }

    [Fact]
    public void ACrowdOfBotsShowsEveryBodyAndBothBaldAndHairedHeads()
    {
        var looks = new DevelopmentBotLooks(Catalog, ItemCatalogTestData.CompileFixture());

        var crowd = Enumerable.Range(1, 64).Select(Id).Select(looks.For).ToArray();

        Assert.Equal(
            Catalog.BodyModels.Order(StringComparer.Ordinal),
            crowd.Select(look => look.Appearance.BodyModel).Distinct().Order(StringComparer.Ordinal));
        Assert.Contains(crowd, look => look.Appearance.Choices.Any(choice => choice.Kind == "hair"));
        Assert.Contains(crowd, look => look.Appearance.Choices.All(choice => choice.Kind != "hair"));
        Assert.Contains(crowd, look => look.Outfit!.Any(item => item.EquipmentSlotId == "head"));
        Assert.Contains(crowd, look => look.Outfit!.All(item => item.EquipmentSlotId != "head"));
    }

    [Fact]
    public void TheStoreKeepsEachLookFencedByItsSessionAndVersionsEveryChange()
    {
        var store = new CharacterLookStore();
        var characterId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var outfit = new[] { new CharacterLookItem("body", "fixture.tunic") };
        store.Register(characterId, sessionId, new CharacterLook(CharacterStateTestData.Appearance, outfit));
        Assert.True(store.TryGet(characterId, sessionId, out _, out var firstVersion));

        var lowered = CharacterStateTestData.Appearance with { HeadwearLowered = true };
        Assert.False(store.TryApply(characterId, Guid.NewGuid(), lowered));
        Assert.True(store.TryApply(characterId, sessionId, lowered));

        Assert.True(store.TryGet(characterId, sessionId, out var look, out var secondVersion));
        Assert.True(secondVersion > firstVersion);
        Assert.True(look!.Appearance.HeadwearLowered);
        // A bot keeps the outfit it was dressed in.
        Assert.Same(outfit, look.Outfit);
        Assert.False(store.TryGet(characterId, Guid.NewGuid(), out _, out _));

        store.Remove(characterId, Guid.NewGuid());
        Assert.True(store.TryGet(characterId, sessionId, out _, out _));
        store.Remove(characterId, sessionId);
        Assert.False(store.TryGet(characterId, sessionId, out _, out _));
    }

    private static Guid Id(int seed)
    {
        return new Guid(seed, 0x1234, 0x5678, [1, 2, 3, 4, 5, 6, 7, 8]);
    }

    private static string Describe(CharacterLook look)
    {
        return string.Join(
            "|",
            look.Appearance.BodyModel,
            string.Join(";", look.Appearance.Choices.Select(choice => $"{choice.Kind}/{choice.Slot}={choice.Option}")),
            string.Join(";", look.Outfit!.Select(item => $"{item.EquipmentSlotId}={item.DefinitionId}")));
    }
}
