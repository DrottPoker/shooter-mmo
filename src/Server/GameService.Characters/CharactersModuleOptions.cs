using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.Server.Common.Characters;
using Mmorpg.Server.Common.Progression;

namespace Mmorpg.GameService.Characters;

public sealed record CharactersModuleOptions(int MaxCharactersPerAccount)
{
    // The character appearance content, appearance.json, from Characters:AppearancePath, by default
    // the copy that the build places next to GameService.
    public string AppearancePath { get; init; } = CharacterAppearanceFileLoader.ResolvePath(null);

    // The progression rules, progression-rules.json, from Characters:ProgressionRulesPath, by
    // default the copy that the build places next to GameService.
    public string ProgressionRulesPath { get; init; } = ProgressionRulesFileLoader.ResolvePath(null);

    public static CharactersModuleOptions Read(
        IConfiguration configuration,
        ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(errors);

        return new CharactersModuleOptions(ConfigurationValidation.ValidatePositiveInt(
            configuration,
            "Game:MaxCharactersPerAccount",
            errors))
        {
            AppearancePath = CharacterAppearanceFileLoader.ResolvePath(configuration["Characters:AppearancePath"]),
            ProgressionRulesPath = ProgressionRulesFileLoader.ResolvePath(configuration["Characters:ProgressionRulesPath"])
        };
    }
}
