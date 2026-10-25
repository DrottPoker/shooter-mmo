namespace Mmorpg.GameService.Characters;

// A new character with its look, or the default look of the default body model without one.
internal sealed record CreateCharacterRequest(string? Name, CharacterAppearanceRequest? Appearance = null);

internal sealed record CharacterAppearanceRequest(string? BodyModel, CharacterAppearanceChoiceRequest[]? Choices);

internal sealed record CharacterAppearanceChoiceRequest(string? Kind, string? Slot, string? Option);

internal sealed record CharacterResponse(
    Guid Id,
    string Name,
    long Currency,
    DateTime CreatedAt,
    string BodyModel);

// What character creation offers, from the appearance content: the kinds of a look with their
// rules, and every body model, the default first, with its options per kind and slot, the first
// option of each its default.
internal sealed record CharacterAppearanceCatalogResponse(
    CharacterAppearanceKindResponse[] Kinds,
    CharacterBodyModelResponse[] BodyModels);

internal sealed record CharacterAppearanceKindResponse(string Id, bool Required, bool ChangesInGame);

internal sealed record CharacterBodyModelResponse(string Id, CharacterAppearanceOfferResponse[] Offers);

internal sealed record CharacterAppearanceOfferResponse(string Kind, string Slot, string[] Options);
