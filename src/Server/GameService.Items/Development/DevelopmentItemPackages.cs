using System.Text.Json;
using System.Text.Json.Serialization;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Development;

// The item states that the development item tool can give an empty offline character,
// authored in content/development/item-packages.json so the tool never names content.
internal sealed record DevelopmentItemPackageDocument(
    int FormatVersion,
    string CorpseSeedPackageId,
    IReadOnlyList<DevelopmentItemPackage> Packages);

// A package with an expected load must reach exactly that carried percentage of the
// character's carry capacity, or the tool rejects it as stale against the catalog.
internal sealed record DevelopmentItemPackage(
    string Id,
    string DisplayName,
    string Description,
    int? ExpectedLoadPercent,
    IReadOnlyList<DevelopmentItemPackageGrant> Grants);

// One grant, applied in order. An equipment grant enters Permanent Inventory and is then
// equipped; a bag grant enters the Bag that an earlier grant equipped; recovery grants
// enter the Bank and become one Recovery delivery after the last grant.
internal sealed record DevelopmentItemPackageGrant(
    string DefinitionId,
    int Quantity,
    string Destination,
    int? SlotIndex,
    string? EquipmentSlotId);

internal static class DevelopmentItemPackageDestinations
{
    public const string Inventory = "inventory";
    public const string Bank = "bank";
    public const string SecureContainer = "secure_container";
    public const string Equipment = "equipment";
    public const string Bag = "bag";
    public const string Recovery = "recovery";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Inventory,
        Bank,
        SecureContainer,
        Equipment,
        Bag,
        Recovery
    };

    // Carried destinations count toward carry weight; equipped items do not.
    public static bool IsCarried(string destination)
    {
        return destination is Inventory or SecureContainer or Bag;
    }
}

public sealed record DevelopmentItemPackageSource(string PackagesPath)
{
    private const string DefaultRelativePath = "Content/Development/item-packages.json";

    public static DevelopmentItemPackageSource FromConfiguration(IConfiguration configuration)
    {
        var configuredPath = configuration["Items:Development:ItemPackagesPath"];
        var path = string.IsNullOrWhiteSpace(configuredPath) ? DefaultRelativePath : configuredPath;
        var resolvedPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
        return new DevelopmentItemPackageSource(Path.GetFullPath(resolvedPath));
    }
}

internal static class DevelopmentItemPackageLoader
{
    public const int FormatVersion = 1;
    private const int MaximumGrantsPerPackage = 64;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static DevelopmentItemPackageDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The development item packages file '{path}' does not exist.");
        }

        return Parse(File.ReadAllText(path));
    }

    public static DevelopmentItemPackageDocument Parse(string json)
    {
        DevelopmentItemPackageDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<DevelopmentItemPackageDocument>(
                json,
                SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"The development item packages are malformed: {exception.Message}",
                exception);
        }

        if (document is null)
        {
            throw new InvalidOperationException("The development item packages have no root object.");
        }

        Validate(document);
        return document;
    }

    private static void Validate(DevelopmentItemPackageDocument document)
    {
        var errors = new List<string>();
        if (document.FormatVersion != FormatVersion)
        {
            errors.Add("formatVersion is unsupported.");
        }

        var packageIds = new HashSet<string>(StringComparer.Ordinal);
        var packages = document.Packages ?? [];
        if (packages.Count == 0)
        {
            errors.Add("packages must contain at least one package.");
        }

        for (var packageIndex = 0; packageIndex < packages.Count; packageIndex++)
        {
            var package = packages[packageIndex];
            var path = $"packages[{packageIndex}]";
            if (package is null)
            {
                errors.Add($"{path} must not be null.");
                continue;
            }

            if (!IsIdentifier(package.Id))
            {
                errors.Add($"{path}.id must be a lowercase identifier.");
            }
            else if (!packageIds.Add(package.Id))
            {
                errors.Add($"packages contains duplicate id '{package.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(package.DisplayName)
                || string.IsNullOrWhiteSpace(package.Description))
            {
                errors.Add($"{path} requires a displayName and a description.");
            }

            if (package.ExpectedLoadPercent is <= 0 or > 1000)
            {
                errors.Add($"{path}.expectedLoadPercent must be between 1 and 1000.");
            }

            ValidateGrants(package.Grants ?? [], path, errors);
        }

        if (!packageIds.Contains(document.CorpseSeedPackageId ?? string.Empty))
        {
            errors.Add("corpseSeedPackageId must name a package.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "The development item packages are invalid: " + string.Join(" ", errors));
        }
    }

    private static void ValidateGrants(
        IReadOnlyList<DevelopmentItemPackageGrant> grants,
        string packagePath,
        ICollection<string> errors)
    {
        if (grants.Count is 0 or > MaximumGrantsPerPackage)
        {
            errors.Add($"{packagePath}.grants must contain between 1 and {MaximumGrantsPerPackage} grants.");
        }

        var equipsBag = false;
        for (var grantIndex = 0; grantIndex < grants.Count; grantIndex++)
        {
            var grant = grants[grantIndex];
            var path = $"{packagePath}.grants[{grantIndex}]";
            if (grant is null)
            {
                errors.Add($"{path} must not be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(grant.DefinitionId) || grant.Quantity <= 0)
            {
                errors.Add($"{path} requires a definitionId and a positive quantity.");
            }

            if (!DevelopmentItemPackageDestinations.All.Contains(grant.Destination ?? string.Empty))
            {
                errors.Add($"{path}.destination is unsupported.");
            }

            if (grant.SlotIndex is < 0)
            {
                errors.Add($"{path}.slotIndex must not be negative.");
            }

            var isEquipment = grant.Destination == DevelopmentItemPackageDestinations.Equipment;
            if (isEquipment == string.IsNullOrWhiteSpace(grant.EquipmentSlotId))
            {
                errors.Add($"{path}.equipmentSlotId is required exactly for an equipment grant.");
            }

            if (isEquipment
                && string.Equals(grant.EquipmentSlotId, ItemEquipmentSlotIds.Bag, StringComparison.Ordinal))
            {
                equipsBag = true;
            }

            if (grant.Destination == DevelopmentItemPackageDestinations.Bag && !equipsBag)
            {
                errors.Add($"{path} grants into a Bag that no earlier grant equips.");
            }
        }
    }

    private static bool IsIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 64
            && value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
    }
}
