using System.Text.Json;
using System.Text.Json.Serialization;
using Mmorpg.WorldData.Combat;

namespace Mmorpg.SimulationWorker.Combat;

// What a Shard rule set allows. GameService names the rule set of the Shard; the worker's
// combat rules give it its meaning.
public sealed record ShardRuleSet(string Id, bool PlayerVersusPlayer);

// The validated player combat rules and Shard rule sets the worker enforces.
public sealed class CombatRules
{
    private readonly Dictionary<string, ShardRuleSet> ruleSets;

    private CombatRules(CombatRulesDocument document)
    {
        PlayerMaximumHealth = document.player.maximumHealth;
        RespawnDelaySeconds = document.player.respawnDelaySeconds;
        CombatTimeoutSeconds = document.player.combatTimeoutSeconds;
        RegenerationPercentPerSecond = document.player.regenerationPercentPerSecond;
        EquipmentLockoutSeconds = document.player.equipmentLockoutSeconds;
        AmmunitionBatchIntervalSeconds = document.ammunition.batchIntervalSeconds;
        ruleSets = document.ruleSets.ToDictionary(
            ruleSet => ruleSet.id,
            ruleSet => new ShardRuleSet(ruleSet.id, ruleSet.playerVersusPlayer),
            StringComparer.Ordinal);
    }

    public int PlayerMaximumHealth { get; }

    public float RespawnDelaySeconds { get; }

    public float CombatTimeoutSeconds { get; }

    public int RegenerationPercentPerSecond { get; }

    public float EquipmentLockoutSeconds { get; }

    public float AmmunitionBatchIntervalSeconds { get; }

    public IReadOnlyCollection<ShardRuleSet> RuleSets => ruleSets.Values;

    public bool TryGetRuleSet(string id, out ShardRuleSet ruleSet)
    {
        return ruleSets.TryGetValue(id, out ruleSet!);
    }

    // The rules a worker composed without content uses, as in focused tests.
    public static CombatRules Default { get; } = FromDocument(new CombatRulesDocument
    {
        formatVersion = CombatRulesFormat.Version,
        player = new PlayerCombatRulesDocument
        {
            maximumHealth = 100,
            respawnDelaySeconds = 5f,
            combatTimeoutSeconds = 10f,
            regenerationPercentPerSecond = 2,
            equipmentLockoutSeconds = 1f
        },
        ammunition = new AmmunitionRulesDocument { batchIntervalSeconds = 5f },
        ruleSets = [new ShardRuleSetDocument { id = "default", playerVersusPlayer = false }]
    });

    public static CombatRules FromDocument(CombatRulesDocument document)
    {
        CombatRulesValidator.Validate(document);
        return new CombatRules(document);
    }
}

// Loads the combat rules strictly: unknown and duplicate properties, comments, and
// trailing commas are rejected, so a typo never silently falls back to a default.
public static class CombatRulesLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        IncludeFields = true,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static CombatRules Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The combat rules file does not exist.", path);
        }

        return Parse(File.ReadAllText(path));
    }

    public static CombatRules Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The combat rules file is empty.");
        }

        CombatRulesDocument document;
        try
        {
            using (var parsed = JsonDocument.Parse(json))
            {
                RejectDuplicateProperties(parsed.RootElement, "$");
            }

            document = JsonSerializer.Deserialize<CombatRulesDocument>(json, SerializerOptions)
                ?? throw new InvalidDataException("The combat rules file has no root object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The combat rules file is malformed: {exception.Message}", exception);
        }

        try
        {
            return CombatRules.FromDocument(document);
        }
        catch (CombatRulesValidationException exception)
        {
            throw new InvalidDataException($"The combat rules are invalid: {exception.Message}", exception);
        }
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"The combat rules contain duplicate property '{property.Name}' at {path}.");
                }

                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index}]");
                index++;
            }
        }
    }
}

// The rule set of this worker's Shard, as the latest registry heartbeat reported it.
// The heartbeat service writes it; the realtime loop reads it.
public sealed class SimulationShardRules
{
    private static readonly ShardRuleSet Unknown = new(string.Empty, false);
    private volatile ShardRuleSet current = Unknown;

    public ShardRuleSet Current => current;

    public bool IsKnown => !ReferenceEquals(current, Unknown);

    public void Set(ShardRuleSet ruleSet)
    {
        current = ruleSet ?? throw new ArgumentNullException(nameof(ruleSet));
    }
}
