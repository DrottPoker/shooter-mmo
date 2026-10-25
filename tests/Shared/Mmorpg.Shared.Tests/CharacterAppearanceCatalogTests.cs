using Mmorpg.WorldData.Characters;

namespace Mmorpg.Shared.Tests;

// The rules of a look, on a small catalog of their own, so content changes never break them.
public sealed class CharacterAppearanceCatalogTests
{
    [Fact]
    public void TheFirstBodyModelIsTheDefaultAndItsDefaultLookTakesTheFirstOptionOfEveryRequiredKind()
    {
        var catalog = Catalog();

        Assert.Equal("Body_A", catalog.DefaultBodyModel);
        Assert.Equal(["Body_A", "Body_B"], catalog.BodyModels);
        Assert.Equal(["head", "back"], catalog.VisibleEquipmentSlots);
        Assert.Equal(
            ["face=Plain", "underwear/legs=Briefs"],
            catalog.DefaultChoices("Body_A").Select(Describe));
        Assert.Equal(
            ["face=Freckles", "underwear/body=Bra", "underwear/legs=Briefs"],
            catalog.DefaultChoices("Body_B").Select(Describe));
        Assert.Empty(catalog.DefaultChoices("Body_C"));
    }

    [Fact]
    public void CreationAcceptsADefaultLookAndOptionalKinds()
    {
        var catalog = Catalog();

        Assert.Null(catalog.CreationError("Body_A", catalog.DefaultChoices("Body_A")));
        Assert.Null(catalog.CreationError(
            "Body_A",
            [Choice("face", "Stubble"), Choice("underwear", "Briefs", "legs"), Choice("hair", "Braid")]));
    }

    [Theory]
    [InlineData("Body_C", "The body model 'Body_C' does not exist.")]
    [InlineData("", "The body model '' does not exist.")]
    public void CreationRejectsAnUnknownBodyModel(string bodyModel, string error)
    {
        Assert.Equal(error, Catalog().CreationError(bodyModel, []));
    }

    [Fact]
    public void CreationRejectsWhatTheBodyModelDoesNotOffer()
    {
        var catalog = Catalog();

        // Another body's face.
        Assert.Contains(
            "does not offer",
            catalog.CreationError("Body_A", [Choice("face", "Freckles"), Choice("underwear", "Briefs", "legs")]));
        // A slot the body does not offer for the kind.
        Assert.Contains(
            "does not offer",
            catalog.CreationError("Body_A", [Choice("face", "Plain"), Choice("underwear", "Bra", "body"), Choice("underwear", "Briefs", "legs")]));
        // A kind content does not list.
        Assert.Equal(
            "The look kind 'scar' does not exist.",
            catalog.CreationError("Body_A", [Choice("scar", "Scar")]));
    }

    [Fact]
    public void CreationNeedsEveryRequiredKindAndSlotOnceAtMost()
    {
        var catalog = Catalog();

        Assert.Equal(
            "The look needs a underwear in legs.",
            catalog.CreationError("Body_A", [Choice("face", "Plain")]));
        Assert.Equal(
            "The look chooses more than one face.",
            catalog.CreationError("Body_A", [Choice("face", "Plain"), Choice("face", "Stubble"), Choice("underwear", "Briefs", "legs")]));
        Assert.Equal(
            "The look has no choices.",
            catalog.CreationError("Body_A", null!));
    }

    [Fact]
    public void StorageKeepsAChoiceAnotherBodyOffersSoOneLookServesEveryBody()
    {
        var catalog = Catalog();

        // Body_A has no freckles and no bra, but Body_B offers them.
        Assert.Null(catalog.StorageError("Body_A", [Choice("face", "Freckles"), Choice("underwear", "Bra", "body")]));
        // A bald character: optional and required kinds may both be left out when stored.
        Assert.Null(catalog.StorageError("Body_B", []));
        Assert.Equal(
            "No body model offers face=Scarred.",
            catalog.StorageError("Body_A", [Choice("face", "Scarred")]));
        Assert.Equal(
            "The body model 'Body_C' does not exist.",
            catalog.StorageError("Body_C", []));
        Assert.Equal(
            "The look chooses more than one underwear in legs.",
            catalog.StorageError("Body_A", [Choice("underwear", "Briefs", "legs"), Choice("underwear", "Briefs", "legs")]));
    }

    [Fact]
    public void ContentWithAKindTheClientsDoNotKnowIsRejected()
    {
        var document = Document();
        document.kinds.Add(new CharacterAppearanceKindDocument { id = "beard", required = false });

        var exception = Assert.Throws<CharacterAppearanceValidationException>(() => CharacterAppearanceCatalog.Create(document));
        Assert.StartsWith("Every kind must be one of face, eyes, scar, tattoo, underwear, hair", exception.Message);
    }

    [Fact]
    public void ContentThatOffersAKindAndSlotTwiceIsRejected()
    {
        var document = Document();
        document.bodyModels[0].offers.Add(Offer("face", "", "Plain"));

        var exception = Assert.Throws<CharacterAppearanceValidationException>(() => CharacterAppearanceCatalog.Create(document));
        Assert.Equal("Body_A face: a kind and slot is offered twice.", exception.Message);
    }

    [Fact]
    public void ContentWithARepeatedOrNonIdentifierVisibleSlotIsRejected()
    {
        var repeated = Document();
        repeated.visibleEquipmentSlots.Add("head");
        var malformed = Document();
        malformed.visibleEquipmentSlots[0] = "Head";

        Assert.Throws<CharacterAppearanceValidationException>(() => CharacterAppearanceCatalog.Create(repeated));
        Assert.Throws<CharacterAppearanceValidationException>(() => CharacterAppearanceCatalog.Create(malformed));
    }

    [Fact]
    public void ContentOfAnotherFormatVersionIsRejected()
    {
        var document = Document();
        document.formatVersion = 2;

        var exception = Assert.Throws<CharacterAppearanceValidationException>(() => CharacterAppearanceCatalog.Create(document));
        Assert.Equal("formatVersion must be 1.", exception.Message);
    }

    private static CharacterAppearanceCatalog Catalog() => CharacterAppearanceCatalog.Create(Document());

    private static CharacterAppearanceDocument Document()
    {
        return new CharacterAppearanceDocument
        {
            formatVersion = 1,
            kinds =
            [
                new CharacterAppearanceKindDocument { id = "face", required = true },
                new CharacterAppearanceKindDocument { id = "underwear", required = true, changesInGame = true },
                new CharacterAppearanceKindDocument { id = "hair", changesInGame = true }
            ],
            bodyModels =
            [
                new CharacterBodyModelDocument
                {
                    id = "Body_A",
                    offers =
                    [
                        Offer("face", "", "Plain", "Stubble"),
                        Offer("underwear", "legs", "Briefs"),
                        Offer("hair", "", "Braid")
                    ]
                },
                new CharacterBodyModelDocument
                {
                    id = "Body_B",
                    offers =
                    [
                        Offer("face", "", "Freckles"),
                        Offer("underwear", "body", "Bra"),
                        Offer("underwear", "legs", "Briefs"),
                        Offer("hair", "", "Braid")
                    ]
                }
            ],
            visibleEquipmentSlots = ["head", "back"]
        };
    }

    private static CharacterAppearanceOfferDocument Offer(string kind, string slot, params string[] options)
    {
        return new CharacterAppearanceOfferDocument { kind = kind, slot = slot, options = [.. options] };
    }

    private static CharacterAppearanceChoice Choice(string kind, string option, string slot = "")
    {
        return new CharacterAppearanceChoice(kind, slot, option);
    }

    private static string Describe(CharacterAppearanceChoice choice)
    {
        return choice.Kind + (choice.Slot.Length > 0 ? "/" + choice.Slot : string.Empty) + "=" + choice.Option;
    }
}
