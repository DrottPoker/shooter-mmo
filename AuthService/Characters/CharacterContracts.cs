namespace AuthService.Characters;

public sealed record CreateCharacterRequest(string? Name);

public sealed record CharacterResponse(
    Guid Id,
    string Name,
    long Currency,
    DateTime CreatedAt);
