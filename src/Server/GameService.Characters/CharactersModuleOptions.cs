using Mmorpg.GameService.Platform.Configuration;

namespace Mmorpg.GameService.Characters;

public sealed record CharactersModuleOptions(int MaxCharactersPerAccount)
{
    public static CharactersModuleOptions Read(
        IConfiguration configuration,
        ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(errors);

        return new CharactersModuleOptions(ConfigurationValidation.ValidatePositiveInt(
            configuration,
            "Game:MaxCharactersPerAccount",
            errors));
    }
}
