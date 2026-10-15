namespace Mmorpg.GameService.Characters;

internal sealed record CreateCharacterRequest(string? Name);

internal sealed record CharacterResponse(
    Guid Id,
    string Name,
    long Currency,
    DateTime CreatedAt);
