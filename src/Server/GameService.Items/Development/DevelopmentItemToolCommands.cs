using System.Globalization;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Items.Development;

internal enum DevelopmentItemToolCommandKind
{
    List = 1,
    Grant = 2,
    Package = 3,
    Corpse = 4
}

internal enum DevelopmentItemDestination
{
    PermanentInventory = 1,
    Bank = 2,
    SecureContainer = 3
}

internal sealed record DevelopmentItemToolCommand(
    DevelopmentItemToolCommandKind Kind,
    Guid CharacterId,
    string DefinitionId,
    int Quantity,
    DevelopmentItemDestination Destination,
    string PackageId,
    string ShardId = "",
    double PositionX = 0d,
    double PositionY = 0d,
    double PositionZ = 0d);

internal static class DevelopmentItemToolCommandParser
{
    public const string ListArgument = "--dev-items-list";
    public const string GrantArgument = "--dev-items-grant";
    public const string PackageArgument = "--dev-items-package";
    public const string CorpseArgument = "--dev-items-corpse";

    private static readonly string[] CommandArguments =
    [
        ListArgument,
        GrantArgument,
        PackageArgument,
        CorpseArgument
    ];

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out DevelopmentItemToolCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;
        if (arguments is null)
        {
            return false;
        }

        var commandIndexes = arguments
            .Select((argument, index) => new { argument, index })
            .Where(candidate => CommandArguments.Contains(
                candidate.argument,
                StringComparer.Ordinal))
            .ToArray();
        if (commandIndexes.Length == 0)
        {
            return false;
        }

        if (commandIndexes.Length != 1)
        {
            error = "Exactly one development item tool command may be supplied.";
            return true;
        }

        var selected = commandIndexes[0];
        if (string.Equals(selected.argument, ListArgument, StringComparison.Ordinal))
        {
            command = new DevelopmentItemToolCommand(
                DevelopmentItemToolCommandKind.List,
                Guid.Empty,
                string.Empty,
                0,
                DevelopmentItemDestination.PermanentInventory,
                string.Empty);
            return true;
        }

        if (string.Equals(selected.argument, GrantArgument, StringComparison.Ordinal))
        {
            return TryParseGrant(arguments, selected.index, out command, out error);
        }

        if (string.Equals(selected.argument, CorpseArgument, StringComparison.Ordinal))
        {
            return TryParseCorpse(arguments, selected.index, out command, out error);
        }

        return TryParsePackage(arguments, selected.index, out command, out error);
    }

    private static bool TryParseCorpse(
        IReadOnlyList<string> arguments,
        int commandIndex,
        out DevelopmentItemToolCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;
        if (commandIndex + 5 >= arguments.Count
            || !TryParseCharacterId(arguments[commandIndex + 1], out var characterId)
            || !Identifiers.IsServiceIdentifier(arguments[commandIndex + 2])
            || !TryParseFinite(arguments[commandIndex + 3], out var positionX)
            || !TryParseFinite(arguments[commandIndex + 4], out var positionY)
            || !TryParseFinite(arguments[commandIndex + 5], out var positionZ))
        {
            error = $"{CorpseArgument} requires <characterGuid> <shardId> <x> <y> <z>.";
            return true;
        }

        command = new DevelopmentItemToolCommand(
            DevelopmentItemToolCommandKind.Corpse,
            characterId,
            string.Empty,
            0,
            DevelopmentItemDestination.PermanentInventory,
            string.Empty,
            arguments[commandIndex + 2].Trim(),
            positionX,
            positionY,
            positionZ);
        return true;
    }

    private static bool TryParseGrant(
        IReadOnlyList<string> arguments,
        int commandIndex,
        out DevelopmentItemToolCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;
        if (commandIndex + 4 >= arguments.Count
            || !TryParseCharacterId(arguments[commandIndex + 1], out var characterId)
            || string.IsNullOrWhiteSpace(arguments[commandIndex + 2])
            || !int.TryParse(arguments[commandIndex + 3], out var quantity)
            || quantity <= 0
            || !TryParseDestination(arguments[commandIndex + 4], out var destination))
        {
            error = $"{GrantArgument} requires <characterGuid> <definitionId> "
                + "<positiveQuantity> <permanent|bank|secure>.";
            return true;
        }

        command = new DevelopmentItemToolCommand(
            DevelopmentItemToolCommandKind.Grant,
            characterId,
            arguments[commandIndex + 2].Trim(),
            quantity,
            destination,
            string.Empty);
        return true;
    }

    private static bool TryParsePackage(
        IReadOnlyList<string> arguments,
        int commandIndex,
        out DevelopmentItemToolCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;
        if (commandIndex + 2 >= arguments.Count
            || !TryParseCharacterId(arguments[commandIndex + 1], out var characterId)
            || string.IsNullOrWhiteSpace(arguments[commandIndex + 2]))
        {
            error = $"{PackageArgument} requires <characterGuid> <packageId>.";
            return true;
        }

        command = new DevelopmentItemToolCommand(
            DevelopmentItemToolCommandKind.Package,
            characterId,
            string.Empty,
            0,
            DevelopmentItemDestination.PermanentInventory,
            arguments[commandIndex + 2].Trim());
        return true;
    }

    private static bool TryParseCharacterId(string value, out Guid characterId)
    {
        return Guid.TryParse(value, out characterId) && characterId != Guid.Empty;
    }

    private static bool TryParseDestination(
        string value,
        out DevelopmentItemDestination destination)
    {
        destination = value switch
        {
            "permanent" => DevelopmentItemDestination.PermanentInventory,
            "bank" => DevelopmentItemDestination.Bank,
            "secure" => DevelopmentItemDestination.SecureContainer,
            _ => default
        };
        return destination != default;
    }

    private static bool TryParseFinite(string value, out double parsed)
    {
        return double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out parsed)
            && double.IsFinite(parsed)
            && parsed is >= -1_000_000d and <= 1_000_000d;
    }

}
