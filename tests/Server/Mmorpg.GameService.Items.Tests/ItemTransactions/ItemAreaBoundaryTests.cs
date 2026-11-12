using System.Reflection;
using System.Text.RegularExpressions;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Testing;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// Guards the areas inside GameService.Items that let the economy code (vendors and gathering)
// live in a module above Items, as docs/GAME_SERVICE_MODULES.md describes under Items
// Transaction Kernel:
// R-a: no kernel, core, or corpse code names an economy type, and the kernel names no quest,
//      vendor, or gathering feature;
// R-b: the economy area runs no SQL of its own and reaches item tables through the item
//      primitives;
// R-c: the economy area uses nothing of the corpse area.
// The quest code lives in GameService.Quests, above Items, where the module boundaries keep it
// apart. The area lists below stand in for module boundaries until the economy code moves into
// a module of its own (hardening plan step 3.1d), which replaces them.
public sealed partial class ItemAreaBoundaryTests
{
    private const string ItemsPath = "GameService.Items/";

    // Composition registers the commands, routes, and services of every area.
    private const string CompositionFile = ItemsPath + "ItemsModule.cs";

    private static readonly string[] EconomyAreaFiles =
    [
        "SimulationItemMutationService.Economy.cs",
        "Transactions/EconomyTransactionContracts.cs",
        "Transactions/Handlers/EconomyCommandSource.cs",
        "Transactions/Handlers/GatherResourceCommandHandler.cs",
        "Transactions/Handlers/VendorCommandHandler.cs"
    ];

    // Corpses, death, and Mob loot stay in Items: a corpse is an item container.
    private static readonly string[] CorpseAreaFiles =
    [
        "CorpseContracts.cs",
        "CorpseEndpoints.cs",
        "CorpseExpiryHostedService.cs",
        "CorpseService.cs",
        "CorpseService.Interactions.cs",
        "Integration/OpenCorpseShardStateProbe.cs",
        "Transactions/Domain/CorpseBagCustody.cs",
        "Transactions/Domain/CorpseCustody.cs",
        "Transactions/Domain/CorpseCustodyWrites.cs",
        "Transactions/Domain/CorpseItemTransfers.cs",
        "Transactions/Domain/CorpseQuestDrops.cs",
        "Transactions/Domain/CorpseTransformRules.cs",
        "Transactions/Domain/DurableCorpseRecords.cs",
        "Transactions/Domain/LootAllState.cs",
        "Transactions/Domain/MobLootRules.cs",
        "Transactions/Domain/MobLootWrites.cs",
        "Transactions/Handlers/CorpseBagSwapCommandHandler.cs",
        "Transactions/Handlers/CorpseCommandSource.cs",
        "Transactions/Handlers/CorpseDepositCommandHandler.cs",
        "Transactions/Handlers/CorpseExpiryCommandHandler.cs",
        "Transactions/Handlers/CorpseInternalMoveCommandHandler.cs",
        "Transactions/Handlers/CorpseLootAllCommandHandler.cs",
        "Transactions/Handlers/CorpseLootCommandHandler.cs",
        "Transactions/Handlers/MobCorpseCommandHandler.cs",
        "Transactions/Handlers/MobLootAllGrantCommandHandler.cs",
        "Transactions/Handlers/MobLootGrantCommandHandler.cs",
        "Transactions/Handlers/PlayerDeath/DeathCustodyTransfers.cs",
        "Transactions/Handlers/PlayerDeath/DeathPartitionModels.cs",
        "Transactions/Handlers/PlayerDeath/PlayerCorpseLayoutWriter.cs",
        "Transactions/Handlers/PlayerDeath/PlayerDeathCommandHandler.cs"
    ];

    private static IReadOnlyList<SourceFile> ItemsFiles { get; } = GameServiceSourceMap
        .LoadSourceFiles()
        .Where(file => file.RelativePath.StartsWith(ItemsPath, StringComparison.Ordinal))
        .Select(file => file with { Text = file.Text.Replace("\r\n", "\n", StringComparison.Ordinal) })
        .ToArray();

    [Fact]
    public void TheAreaListsNameExistingFilesOnce()
    {
        var listed = EconomyAreaFiles.Concat(CorpseAreaFiles).ToArray();
        var existing = ItemsFiles.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(listed.Length, listed.Distinct(StringComparer.Ordinal).Count());
        Assert.All(listed, path => Assert.Contains(ItemsPath + path, existing));
        Assert.Equal(
            ItemsFiles
                .Where(file => file.RelativePath.StartsWith(ItemsPath + "Transactions/Handlers/PlayerDeath/", StringComparison.Ordinal))
                .Select(file => file.RelativePath.Substring(ItemsPath.Length)),
            CorpseAreaFiles.Where(path => path.StartsWith("Transactions/Handlers/PlayerDeath/", StringComparison.Ordinal)));
    }

    // R-a for the economy area. The vendor profiles are part of the mirrored item catalog, so
    // ItemStateReader.LoadVendorAsync and the catalog mirror in ItemCatalogSeeder stay Items code
    // and are not economy types.
    [Fact]
    public void OnlyTheEconomyAreaNamesEconomyTypes()
    {
        var economyNames = AreaNames(EconomyAreaFiles);
        var violations = ItemsFiles
            .Where(file => !InArea(file, EconomyAreaFiles) && file.RelativePath != CompositionFile)
            .SelectMany(file => NamesUsedIn(CodeOf(file.Text), economyNames)
                .Select(name => $"{file.RelativePath} -> {name}"))
            .ToArray();

        Assert.Contains("PurchaseFromVendorCommand", economyNames.Types);
        Assert.Contains("GatherResourceCommand", economyNames.Types);
        Assert.Contains("VendorPurchase", economyNames.KindConstants);
        Assert.DoesNotContain("SimulationItemMutationService", economyNames.Types);
        Assert.True(
            violations.Length == 0,
            "Only the economy area, and the composition that registers it, may name vendor and gathering types or operation kinds:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // R-a for the kernel: its code names no quest, vendor, or gathering feature, except the
    // vendor profile load of the mirrored item catalog.
    [Fact]
    public void TheKernelNamesNoQuestOrEconomyFeature()
    {
        var kernelFiles = ItemsFiles.Where(file =>
            file.RelativePath.StartsWith(ItemsPath + "Transactions/Kernel/", StringComparison.Ordinal)
            || file.RelativePath.StartsWith(ItemsPath + "Transactions/Context/", StringComparison.Ordinal)
            || file.RelativePath == ItemsPath + "ItemOperationsMetrics.cs");
        var violations = kernelFiles
            .SelectMany(file => FeatureIdentifierPattern().Matches(WithoutStrings(CodeOf(file.Text)))
                .Select(match => match.Value)
                .Where(identifier => !(file.RelativePath == ItemsPath + "Transactions/Context/ItemStateReader.cs"
                    && VendorCatalogLoad.Contains(identifier)))
                .Distinct(StringComparer.Ordinal)
                .Select(identifier => $"{file.RelativePath} -> {identifier}"))
            .ToList();
        violations.AddRange(KernelTypes
            .SelectMany(type => type
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(member => FeatureIdentifierPattern().IsMatch(member.Name)
                    && !(type == typeof(ItemStateReader)
                        && (VendorCatalogLoad.Contains(member.Name)
                            || member.Name.StartsWith("<LoadVendorAsync>", StringComparison.Ordinal))))
                .Select(member => $"{type.Name}.{member.Name}")));

        Assert.True(
            violations.Count == 0,
            "The item transaction kernel names no quest, vendor, or gathering feature:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // R-b.
    [Fact]
    public void TheEconomyAreaRunsNoSqlOfItsOwn()
    {
        var violations = ItemsFiles
            .Where(file => InArea(file, EconomyAreaFiles))
            .SelectMany(file => SqlAccessPattern().Matches(CodeOf(file.Text))
                .Select(match => $"{file.RelativePath} -> {match.Value}"))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "The economy area reaches item tables only through the item primitives and uses neither Dapper nor the transaction's connection:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // R-c.
    [Fact]
    public void TheEconomyAreaUsesNothingOfTheCorpseArea()
    {
        var corpseNames = AreaNames(CorpseAreaFiles);
        var violations = ItemsFiles
            .Where(file => InArea(file, EconomyAreaFiles))
            .SelectMany(file => NamesUsedIn(CodeOf(file.Text), corpseNames)
                .Select(name => $"{file.RelativePath} -> {name}"))
            .ToArray();

        Assert.Contains("CorpseQuestDrops", corpseNames.Types);
        Assert.Contains("ItemTransactionQuestDrop", corpseNames.Types);
        Assert.True(
            violations.Length == 0,
            "The economy area uses no corpse type:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // The kernel types whose members the kernel test checks by reflection, besides the source.
    private static readonly Type[] KernelTypes =
    [
        typeof(ItemTransactionResult),
        typeof(ItemTransactionLiveAccess),
        typeof(ItemActorAuthority),
        typeof(ItemChangeJournal),
        typeof(ItemStateReader),
        typeof(ItemTransactionKernel),
        typeof(ItemOperationLedger),
        typeof(ItemCommandRegistration),
        typeof(ItemOperationsMetrics)
    ];

    // The identifiers of ItemStateReader.LoadVendorAsync and its row.
    private static readonly HashSet<string> VendorCatalogLoad = new(StringComparer.Ordinal)
    {
        "LoadVendorAsync",
        "vendor",
        "vendorId",
        "VendorId",
        "VendorUnavailable",
        "VendorRow",
        "VendorProfileDefinition",
        "VendorOfferEntry"
    };

    private static bool InArea(SourceFile file, IEnumerable<string> area)
    {
        return area.Any(path => string.Equals(file.RelativePath, ItemsPath + path, StringComparison.Ordinal));
    }

    // The types that only the area declares, and the operation kinds that its part of
    // ItemOperationKinds declares. A partial type that other files declare too is shared.
    private static AreaNameSet AreaNames(IEnumerable<string> area)
    {
        var areaFiles = ItemsFiles.Where(file => InArea(file, area)).ToArray();
        var otherTypes = ItemsFiles.Where(file => !InArea(file, area))
            .SelectMany(file => DeclarationPattern().Matches(CodeOf(file.Text)).Select(match => match.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);
        var types = areaFiles
            .SelectMany(file => DeclarationPattern().Matches(CodeOf(file.Text)).Select(match => match.Groups["name"].Value))
            .Where(name => !otherTypes.Contains(name))
            .ToHashSet(StringComparer.Ordinal);
        var kinds = areaFiles
            .SelectMany(file => OperationKindsPartPattern().Matches(CodeOf(file.Text)))
            .SelectMany(match => ConstantPattern().Matches(match.Groups["body"].Value).Select(constant => constant.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);
        return new AreaNameSet(types, kinds);
    }

    private static IEnumerable<string> NamesUsedIn(string code, AreaNameSet names)
    {
        return names.Types
            .Where(name => Regex.IsMatch(code, $@"\b{name}\b"))
            .Concat(names.KindConstants
                .Where(name => Regex.IsMatch(code, $@"\bItemOperationKinds\.{name}\b"))
                .Select(name => "ItemOperationKinds." + name))
            .Order(StringComparer.Ordinal);
    }

    // The source without line comments, which may name other areas.
    private static string CodeOf(string text)
    {
        return LineCommentPattern().Replace(text, string.Empty);
    }

    private static string WithoutStrings(string code)
    {
        return StringLiteralPattern().Replace(code, "\"\"");
    }

    private sealed record AreaNameSet(IReadOnlySet<string> Types, IReadOnlySet<string> KindConstants);

    [GeneratedRegex(@"(?m)^\s*//.*$")]
    private static partial Regex LineCommentPattern();

    [GeneratedRegex(@"\$*""""""[\s\S]*?""""""|\$*@?""(?:\\.|[^""\\\n])*""")]
    private static partial Regex StringLiteralPattern();

    [GeneratedRegex(@"\b(?:class|record|interface|struct|enum)\s+(?<name>[A-Z]\w*)")]
    private static partial Regex DeclarationPattern();

    [GeneratedRegex(@"partial class ItemOperationKinds\s*\{(?<body>[^}]*)\}")]
    private static partial Regex OperationKindsPartPattern();

    [GeneratedRegex(@"const string (?<name>\w+)\s*=")]
    private static partial Regex ConstantPattern();

    // A word part Quest, Vendor, or Gather, or an identifier that starts with one in lower case.
    [GeneratedRegex(@"\b\w*(?:Quest|Vendor|Gather)\w*|\b(?:quest|vendor|gather)\w*")]
    private static partial Regex FeatureIdentifierPattern();

    [GeneratedRegex(@"\busing Dapper;|\bDapper\.|\bSqlMapper\b|\bCommandDefinition\b|\bNpgsqlCommand\b|\.Connection\b")]
    private static partial Regex SqlAccessPattern();
}
