namespace AuthService.Characters;

public static class CharacterValidation
{
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Character name is required.";
        }

        var trimmedName = name.Trim();

        if (trimmedName.Length is < 3 or > 24)
        {
            return "Character name must be between 3 and 24 characters.";
        }

        if (trimmedName.Contains("  ", StringComparison.Ordinal))
        {
            return "Character name cannot contain repeated spaces.";
        }

        if (!trimmedName.All(IsAllowedCharacter))
        {
            return "Character name can only contain letters, numbers, spaces, hyphens, and underscores.";
        }

        return null;
    }

    private static bool IsAllowedCharacter(char character)
    {
        return char.IsAsciiLetterOrDigit(character)
               || character == ' '
               || character == '-'
               || character == '_';
    }
}

