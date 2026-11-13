using System.Text.RegularExpressions;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Items.StackStress;

// The definitions the stress fixtures grant come from configuration, so the fixture never
// names game content.
public sealed record StackStressFixtureOptions(bool Enabled, string AuthoritySecret)
{
    public const int MaximumKitGrants = 8;
    public const int MaximumKitQuantity = 10_000;

    private static readonly Regex KitName = new("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);

    public string InventoryDefinitionId { get; init; } = string.Empty;

    public string LootDefinitionId { get; init; } = string.Empty;

    // The kits the playing load bots receive, by name: the items of a role, such as a weapon
    // and its arrows, that the bot then equips over UDP like a player.
    public IReadOnlyDictionary<string, IReadOnlyList<KitGrant>> Kits { get; init; } =
        new Dictionary<string, IReadOnlyList<KitGrant>>(StringComparer.Ordinal);

    public const string SectionName = "StackStressFixtures";
    public const string SecretEnvironmentVariable = "STACK_STRESS_FIXTURE_SECRET";
    public const string AuthorityHeaderName = "X-Stack-Stress-Fixture-Key";

    public static StackStressFixtureOptions FromConfiguration(
        IConfiguration configuration,
        bool isDevelopment,
        string postgresConnectionString)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = configuration.GetValue($"{SectionName}:Enabled", false);
        var secret = configuration[SecretEnvironmentVariable]?.Trim() ?? string.Empty;
        var inventoryDefinitionId =
            configuration[$"{SectionName}:InventoryDefinitionId"]?.Trim() ?? string.Empty;
        var lootDefinitionId =
            configuration[$"{SectionName}:LootDefinitionId"]?.Trim() ?? string.Empty;
        var errors = new List<string>();
        if (enabled && !isDevelopment)
        {
            errors.Add($"{SectionName}:Enabled can only be true in the Development environment.");
        }

        if (enabled
            && (secret.Length < 32
                || secret.StartsWith("replace-with-", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(
                $"{SecretEnvironmentVariable} must contain a non-placeholder secret of at least 32 characters when stack stress fixtures are enabled.");
        }

        if (enabled)
        {
            ValidateDisposableDatabase(postgresConnectionString, errors);
            if (inventoryDefinitionId.Length == 0 || lootDefinitionId.Length == 0)
            {
                errors.Add(
                    $"{SectionName}:InventoryDefinitionId and {SectionName}:LootDefinitionId must name item definitions when stack stress fixtures are enabled.");
            }
        }

        var kits = ReadKits(configuration.GetSection($"{SectionName}:Kits"), errors);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Stack stress fixture configuration is invalid:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return new StackStressFixtureOptions(enabled, secret)
        {
            InventoryDefinitionId = inventoryDefinitionId,
            LootDefinitionId = lootDefinitionId,
            Kits = kits
        };
    }

    // StackStressFixtures:Kits:<name>:<index>:DefinitionId and :Quantity.
    private static Dictionary<string, IReadOnlyList<KitGrant>> ReadKits(
        IConfigurationSection section,
        ICollection<string> errors)
    {
        var kits = new Dictionary<string, IReadOnlyList<KitGrant>>(StringComparer.Ordinal);
        foreach (var kit in section.GetChildren())
        {
            if (!KitName.IsMatch(kit.Key))
            {
                errors.Add($"{SectionName}:Kits:{kit.Key} is not a kit name of lowercase letters, digits, and dashes.");
                continue;
            }

            var grants = new List<KitGrant>();
            foreach (var grant in kit.GetChildren())
            {
                var definitionId = grant["DefinitionId"]?.Trim() ?? string.Empty;
                var quantity = grant.GetValue("Quantity", 0);
                if (definitionId.Length == 0 || quantity is < 1 or > MaximumKitQuantity)
                {
                    errors.Add(
                        $"{SectionName}:Kits:{kit.Key}:{grant.Key} needs a DefinitionId and a Quantity between 1 and {MaximumKitQuantity}.");
                    continue;
                }

                grants.Add(new KitGrant(definitionId, quantity));
            }

            if (grants.Count is 0 or > MaximumKitGrants)
            {
                errors.Add($"{SectionName}:Kits:{kit.Key} must grant between 1 and {MaximumKitGrants} items.");
                continue;
            }

            kits[kit.Key] = grants;
        }

        return kits;
    }

    private static void ValidateDisposableDatabase(
        string connectionString,
        ICollection<string> errors)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!PostgresHost.IsLoopback(builder))
            {
                errors.Add("Stack stress fixtures require a loopback PostgreSQL host.");
            }

            var database = builder.Database?.Trim() ?? string.Empty;
            if (!database.Contains("stress", StringComparison.OrdinalIgnoreCase)
                && !database.Contains("test", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Stack stress fixtures require a database name containing 'stress' or 'test'.");
            }
        }
        catch (ArgumentException exception)
        {
            errors.Add($"Stack stress fixtures received an invalid PostgreSQL connection string: {exception.Message}");
        }
    }

    // One stack of a kit.
    public sealed record KitGrant(string DefinitionId, int Quantity);
}
