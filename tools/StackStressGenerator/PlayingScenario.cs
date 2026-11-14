using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mmorpg.Tools.StackStressGenerator;

public enum PlayingRole
{
    Fighter,
    Gatherer,
    Walker,
    Trader
}

public enum PlayingGroundKind
{
    // Where fighters hunt Mobs.
    Hunting,

    // Where gatherers work resource nodes.
    Nodes,

    // The town, where traders stay.
    Town,

    // A place where walkers start before they walk over the whole World.
    Walking
}

// An axis-aligned rectangle of a World, in meters.
public readonly record struct PlayingRectangle(float MinimumX, float MinimumZ, float MaximumX, float MaximumZ)
{
    public float CenterX => (MinimumX + MaximumX) * 0.5f;

    public float CenterZ => (MinimumZ + MaximumZ) * 0.5f;

    public bool Contains(float x, float z, float margin = 0f)
    {
        return x >= MinimumX - margin
            && x <= MaximumX + margin
            && z >= MinimumZ - margin
            && z <= MaximumZ + margin;
    }
}

// A home ground of the playing workload: the rectangle a bot of the ground's kind is
// teleported into and works in. A node field names the kit its gatherers need for its nodes.
public sealed record PlayingGround(string Name, PlayingGroundKind Kind, PlayingRectangle Area, string? Kit = null);

// The playing section of a load scenario: the share of every role in percent, the share of
// fighters that carry a bow, whether every bot is teleported to its home ground once after it
// joined, and the home grounds.
public sealed record PlayingScenario(
    IReadOnlyDictionary<PlayingRole, int> RoleShares,
    int FighterBowPercent,
    bool TeleportToGrounds,
    IReadOnlyList<PlayingGround> Grounds)
{
    // The roles a bot can play today; the others are refused until they exist.
    public static readonly IReadOnlySet<PlayingRole> ImplementedRoles =
        new HashSet<PlayingRole> { PlayingRole.Fighter, PlayingRole.Gatherer, PlayingRole.Walker, PlayingRole.Trader };

    private static readonly Regex KitName = new("^[a-z][a-z0-9-]{0,47}$", RegexOptions.CultureInvariant);

    private static readonly Regex Name = new("^[a-z][a-z0-9-]{0,47}$", RegexOptions.CultureInvariant);

    public static PlayingScenario Parse(JsonElement element, string source)
    {
        StrictJson.RequireObject(element, source, "'playing'");
        StrictJson.RequireOnly(element, source, "'playing'", "roles", "fighterBowPercent", "teleportToGrounds", "grounds", "churn");
        if (!element.TryGetProperty("roles", out var rolesElement) || rolesElement.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(source, "'playing' needs the object 'roles' with the percent of every role.");
        }

        var shares = new Dictionary<PlayingRole, int>();
        foreach (var property in rolesElement.EnumerateObject())
        {
            var role = ParseRole(property.Name, source);
            var share = ReadInt(property.Value, source, $"the share of role '{property.Name}'", 0, 100);
            if (share > 0 && !ImplementedRoles.Contains(role))
            {
                throw Invalid(source, $"the role '{property.Name}' is not implemented yet.");
            }

            shares[role] = share;
        }

        if (shares.Values.Sum() != 100)
        {
            throw Invalid(source, "the role shares in 'playing.roles' must add up to 100.");
        }

        var bowPercent = element.TryGetProperty("fighterBowPercent", out var bowElement)
            ? ReadInt(bowElement, source, "'fighterBowPercent'", 0, 100)
            : 0;
        var teleport = element.TryGetProperty("teleportToGrounds", out var teleportElement)
            && teleportElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw Invalid(source, "'teleportToGrounds' must be true or false.")
            };
        var grounds = new List<PlayingGround>();
        if (element.TryGetProperty("grounds", out var groundsElement))
        {
            if (groundsElement.ValueKind != JsonValueKind.Array)
            {
                throw Invalid(source, "'playing.grounds' must be an array.");
            }

            foreach (var ground in groundsElement.EnumerateArray())
            {
                StrictJson.RequireObject(ground, source, "a ground");
                StrictJson.RequireOnly(ground, source, "a ground", "name", "kind", "minimumX", "minimumZ", "maximumX", "maximumZ", "kit");
                var name = RequireName(ground, source, "a ground");
                if (grounds.Any(existing => existing.Name == name))
                {
                    throw Invalid(source, $"the ground '{name}' appears twice.");
                }

                var kind = StrictJson.RequireString(ground, "kind", source, $"the ground '{name}'") switch
                {
                    "hunting" => PlayingGroundKind.Hunting,
                    "nodes" => PlayingGroundKind.Nodes,
                    "town" => PlayingGroundKind.Town,
                    "walking" => PlayingGroundKind.Walking,
                    var other => throw Invalid(source, $"the ground '{name}' has the unknown kind '{other}'.")
                };
                string? kit = null;
                if (ground.TryGetProperty("kit", out _))
                {
                    if (kind != PlayingGroundKind.Nodes)
                    {
                        throw Invalid(source, $"only a ground of kind 'nodes' names a kit, not the ground '{name}'.");
                    }

                    kit = StrictJson.RequireString(ground, "kit", source, $"the ground '{name}'");
                    if (!KitName.IsMatch(kit))
                    {
                        throw Invalid(source, $"the kit '{kit}' of the ground '{name}' must be lowercase letters, digits, and dashes.");
                    }
                }
                else if (kind == PlayingGroundKind.Nodes)
                {
                    throw Invalid(source, $"the node field '{name}' needs the 'kit' its gatherers work its nodes with.");
                }

                grounds.Add(new PlayingGround(name, kind, ReadRectangle(ground, source, $"the ground '{name}'"), kit));
            }
        }

        if (shares.GetValueOrDefault(PlayingRole.Fighter) > 0
            && grounds.All(ground => ground.Kind != PlayingGroundKind.Hunting))
        {
            throw Invalid(source, "fighters need at least one ground of kind 'hunting'.");
        }

        if (shares.GetValueOrDefault(PlayingRole.Gatherer) > 0
            && grounds.All(ground => ground.Kind != PlayingGroundKind.Nodes))
        {
            throw Invalid(source, "gatherers need at least one ground of kind 'nodes'.");
        }

        // Fighters and gatherers sell what they carry at the town's vendor, and traders stay
        // there.
        if ((shares.GetValueOrDefault(PlayingRole.Fighter) > 0
                || shares.GetValueOrDefault(PlayingRole.Gatherer) > 0
                || shares.GetValueOrDefault(PlayingRole.Trader) > 0)
            && grounds.All(ground => ground.Kind != PlayingGroundKind.Town))
        {
            throw Invalid(source, "fighters, gatherers, and traders need a ground of kind 'town' with the vendor.");
        }

        return new PlayingScenario(shares, bowPercent, teleport, grounds)
        {
            Churn = element.TryGetProperty("churn", out var churn) ? PlayingChurnSettings.Parse(churn, source) : null
        };
    }

    // The share of the bots that leaves and comes back every minute of the steady interval, or
    // null for none.
    public PlayingChurnSettings? Churn { get; init; }

    public IReadOnlyList<PlayingGround> GroundsOf(PlayingGroundKind kind)
    {
        return Grounds.Where(ground => ground.Kind == kind).ToArray();
    }

    public static PlayingGroundKind? GroundKindOf(PlayingRole role)
    {
        return role switch
        {
            PlayingRole.Fighter => PlayingGroundKind.Hunting,
            PlayingRole.Gatherer => PlayingGroundKind.Nodes,
            PlayingRole.Trader => PlayingGroundKind.Town,
            PlayingRole.Walker => PlayingGroundKind.Walking,
            _ => null
        };
    }

    public static string FormatRole(PlayingRole role)
    {
        return role.ToString().ToLowerInvariant();
    }

    internal static PlayingRole ParseRole(string value, string source)
    {
        return value switch
        {
            "fighter" => PlayingRole.Fighter,
            "gatherer" => PlayingRole.Gatherer,
            "walker" => PlayingRole.Walker,
            "trader" => PlayingRole.Trader,
            _ => throw Invalid(source, $"the role '{value}' is unknown.")
        };
    }

    internal static string RequireName(JsonElement element, string source, string what)
    {
        var name = StrictJson.RequireString(element, "name", source, what);
        if (!Name.IsMatch(name))
        {
            throw Invalid(source, $"the name '{name}' must be lowercase letters, digits, and dashes.");
        }

        return name;
    }

    internal static PlayingRectangle ReadRectangle(JsonElement element, string source, string what)
    {
        var rectangle = new PlayingRectangle(
            ReadFloat(element, "minimumX", source, what),
            ReadFloat(element, "minimumZ", source, what),
            ReadFloat(element, "maximumX", source, what),
            ReadFloat(element, "maximumZ", source, what));
        if (rectangle.MaximumX <= rectangle.MinimumX || rectangle.MaximumZ <= rectangle.MinimumZ)
        {
            throw Invalid(source, $"{what} needs a positive X and Z size.");
        }

        return rectangle;
    }

    internal static float ReadFloat(JsonElement element, string name, string source, string what)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number)
            || Math.Abs(number) > 100_000d)
        {
            throw Invalid(source, $"{what} needs the number '{name}'.");
        }

        return (float)number;
    }

    internal static int ReadInt(JsonElement value, string source, string what, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number)
            || number < minimum
            || number > maximum)
        {
            throw Invalid(
                source,
                $"{what} must be an integer between {minimum.ToString(CultureInfo.InvariantCulture)} and {maximum.ToString(CultureInfo.InvariantCulture)}.");
        }

        return number;
    }

    internal static StressGeneratorOptionException Invalid(string source, string problem)
    {
        return new StressGeneratorOptionException($"'{source}': {problem}");
    }
}

// The extra actors of a load variant of a World: Mob areas, rectangles with an actor and a
// count, and node groups, grids with an actor, rows, columns, and spacing. The game content
// stays untouched; the variant is compiled into the run folder.
public sealed record WorldVariantScenario(
    IReadOnlyList<WorldVariantArea> Areas,
    IReadOnlyList<WorldVariantGroup> Groups)
{
    public static WorldVariantScenario Parse(JsonElement element, string source)
    {
        StrictJson.RequireObject(element, source, "'worldVariant'");
        StrictJson.RequireOnly(element, source, "'worldVariant'", "areas", "groups");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var areas = new List<WorldVariantArea>();
        foreach (var area in ReadArray(element, "areas", source))
        {
            StrictJson.RequireObject(area, source, "a variant area");
            StrictJson.RequireOnly(area, source, "a variant area", "name", "actorId", "minimumX", "minimumZ", "maximumX", "maximumZ", "count");
            var name = UniqueName(area, names, source);
            areas.Add(new WorldVariantArea(
                name,
                StrictJson.RequireString(area, "actorId", source, $"the area '{name}'"),
                PlayingScenario.ReadRectangle(area, source, $"the area '{name}'"),
                PlayingScenario.ReadInt(
                    area.TryGetProperty("count", out var count) ? count : default,
                    source,
                    $"the count of the area '{name}'",
                    1,
                    256)));
        }

        var groups = new List<WorldVariantGroup>();
        foreach (var group in ReadArray(element, "groups", source))
        {
            StrictJson.RequireObject(group, source, "a variant group");
            StrictJson.RequireOnly(group, source, "a variant group", "name", "actorId", "centerX", "centerZ", "rows", "columns", "spacing");
            var name = UniqueName(group, names, source);
            var spacing = PlayingScenario.ReadFloat(group, "spacing", source, $"the group '{name}'");
            if (spacing < 2f)
            {
                throw PlayingScenario.Invalid(source, $"the group '{name}' needs a spacing of at least 2 m.");
            }

            groups.Add(new WorldVariantGroup(
                name,
                StrictJson.RequireString(group, "actorId", source, $"the group '{name}'"),
                PlayingScenario.ReadFloat(group, "centerX", source, $"the group '{name}'"),
                PlayingScenario.ReadFloat(group, "centerZ", source, $"the group '{name}'"),
                PlayingScenario.ReadInt(group.TryGetProperty("rows", out var rows) ? rows : default, source, $"the rows of the group '{name}'", 1, 16),
                PlayingScenario.ReadInt(group.TryGetProperty("columns", out var columns) ? columns : default, source, $"the columns of the group '{name}'", 1, 16),
                spacing));
        }

        if (areas.Count + groups.Count == 0)
        {
            throw PlayingScenario.Invalid(source, "'worldVariant' needs at least one area or group.");
        }

        return new WorldVariantScenario(areas, groups);
    }

    private static string UniqueName(JsonElement element, ISet<string> names, string source)
    {
        var name = PlayingScenario.RequireName(element, source, "a variant area or group");
        if (!names.Add(name))
        {
            throw PlayingScenario.Invalid(source, $"the variant area or group '{name}' appears twice.");
        }

        return name;
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement element, string name, string source)
    {
        if (!element.TryGetProperty(name, out var array))
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw PlayingScenario.Invalid(source, $"'worldVariant.{name}' must be an array.");
        }

        return array.EnumerateArray().ToArray();
    }
}

public sealed record WorldVariantArea(string Name, string ActorId, PlayingRectangle Area, int Count);

public sealed record WorldVariantGroup(
    string Name,
    string ActorId,
    float CenterX,
    float CenterZ,
    int Rows,
    int Columns,
    float Spacing);
