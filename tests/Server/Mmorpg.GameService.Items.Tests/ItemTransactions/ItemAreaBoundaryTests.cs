using System.Reflection;
using System.Text.RegularExpressions;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Testing;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// Guards the areas inside GameService.Items that let the quest code and the economy code
// (vendors and gathering) live in modules above Items, as docs/GAME_SERVICE_MODULES.md describes
// under Items Transaction Kernel:
// R-a: no kernel, core, or corpse code names a quest or an economy type, or references
//      Mmorpg.GameService.Quests;
// R-b: the quest and economy areas run no SQL of their own and reach item tables through the
//      item primitives;
// R-c: the quest and economy areas use nothing of the corpse area, and the corpse area reaches
//      the quest area only through IQuestDropDemand.
// The area lists below stand in for module boundaries until the quest and economy code move
// into modules of their own (hardening plan step 3.1c), which replaces them.
public sealed partial class ItemAreaBoundaryTests
{
    private const string ItemsPath = "GameService.Items/";

    // Composition registers the commands, routes, and services of every area.
    private const string CompositionFile = ItemsPath + "ItemsModule.cs";

    private static readonly string[] QuestAreaFiles =
    [
        "QuestItemCatalogReferences.cs",
        "SimulationQuestDropService.cs",
        "SimulationQuestEndpoints.cs",
        "SimulationQuestMutationService.cs",
        "SimulationQuestOperationContracts.cs",
        "Transactions/QuestTransactionContracts.cs",
        "Transactions/Domain/QuestDropDemand.cs",
        "Transactions/Domain/QuestGatherCredit.cs",
        "Transactions/Domain/QuestLineage.cs",
        "Transactions/Domain/QuestLogChanges.cs",
        "Transactions/Domain/QuestProgression.cs",
        "Transactions/Handlers/QuestCommandHandler.Admin.cs",
        "Transactions/Handlers/QuestCommandHandler.cs",
        "Transactions/Handlers/QuestCommandSource.cs",
        "Transactions/Handlers/QuestItemCommandHandler.cs"
    ];

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

    // The quest code's gathering entry is the one quest type that the economy area names.
    private const string QuestGatheringEntry = "QuestGatherCredit";

    // The one interface through which the corpse area learns what a quest asks for.
    private const string QuestDropInterface = "IQuestDropDemand";

    // The quest drop service opens its own read-only transaction, as the Quests module's reads
    // do; that statement touches no table.
    private const string QuestDropServiceFile = "SimulationQuestDropService.cs";
    private const string QuestDropServiceReadOnlyTransaction =
        """
                    await connection.ExecuteAsync(new CommandDefinition(
                        "set transaction read only;",
                        transaction: transaction,
                        cancellationToken: cancellationToken));

        """;

    private static IReadOnlyList<SourceFile> ItemsFiles { get; } = GameServiceSourceMap
        .LoadSourceFiles()
        .Where(file => file.RelativePath.StartsWith(ItemsPath, StringComparison.Ordinal))
        .Select(file => file with { Text = file.Text.Replace("\r\n", "\n", StringComparison.Ordinal) })
        .ToArray();

    [Fact]
    public void TheAreaListsNameExistingFilesOnce()
    {
        var listed = QuestAreaFiles.Concat(EconomyAreaFiles).Concat(CorpseAreaFiles).ToArray();
        var existing = ItemsFiles.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(listed.Length, listed.Distinct(StringComparer.Ordinal).Count());
        Assert.All(listed, path => Assert.Contains(ItemsPath + path, existing));
        Assert.Equal(
            ItemsFiles
                .Where(file => file.RelativePath.StartsWith(ItemsPath + "Transactions/Handlers/PlayerDeath/", StringComparison.Ordinal))
                .Select(file => file.RelativePath.Substring(ItemsPath.Length)),
            CorpseAreaFiles.Where(path => path.StartsWith("Transactions/Handlers/PlayerDeath/", StringComparison.Ordinal)));
    }

    // R-a for the quest area.
    [Fact]
    public void OnlyTheQuestAreaNamesQuestTypesOrTheQuestsModule()
    {
        var questNames = AreaNames(QuestAreaFiles);
        var violations = new List<string>();
        foreach (var file in ItemsFiles.Where(file => !InArea(file, QuestAreaFiles) && file.RelativePath != CompositionFile))
        {
            var code = CodeOf(file.Text);
            if (code.Contains("Mmorpg.GameService.Quests", StringComparison.Ordinal))
            {
                violations.Add($"{file.RelativePath} -> Mmorpg.GameService.Quests");
            }

            var allowed = InArea(file, EconomyAreaFiles) ? [QuestGatheringEntry] : Array.Empty<string>();
            violations.AddRange(NamesUsedIn(code, questNames)
                .Where(name => !allowed.Contains(name, StringComparer.Ordinal))
                .Select(name => $"{file.RelativePath} -> {name}"));
        }

        Assert.Contains("AcceptQuestCommand", questNames.Types);
        Assert.Contains("QuestDropDemand", questNames.Types);
        Assert.Contains("AcceptQuest", questNames.KindConstants);
        Assert.True(
            violations.Count == 0,
            "Only the quest area, and the composition that registers it, may name quest types, quest operation kinds, or the Quests module; the economy area may name only the quest gathering entry:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
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
    public void TheQuestAndEconomyAreasRunNoSqlOfTheirOwn()
    {
        var violations = new List<string>();
        foreach (var file in ItemsFiles.Where(file => InArea(file, QuestAreaFiles) || InArea(file, EconomyAreaFiles)))
        {
            var code = CodeOf(file.Text);
            if (file.RelativePath == ItemsPath + QuestDropServiceFile)
            {
                Assert.Equal(1, Occurrences(file.Text, QuestDropServiceReadOnlyTransaction));
                code = CodeOf(file.Text.Replace(QuestDropServiceReadOnlyTransaction, string.Empty, StringComparison.Ordinal))
                    .Replace("using Dapper;", string.Empty, StringComparison.Ordinal);
            }

            violations.AddRange(SqlAccessPattern().Matches(code)
                .Select(match => $"{file.RelativePath} -> {match.Value}"));
        }

        Assert.True(
            violations.Count == 0,
            "The quest and economy areas reach item tables only through the item primitives and use neither Dapper nor the transaction's connection:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // R-c.
    [Fact]
    public void TheQuestAndEconomyAreasUseNothingOfTheCorpseArea()
    {
        var corpseNames = AreaNames(CorpseAreaFiles);
        var violations = ItemsFiles
            .Where(file => InArea(file, QuestAreaFiles) || InArea(file, EconomyAreaFiles))
            .SelectMany(file => NamesUsedIn(CodeOf(file.Text), corpseNames)
                .Select(name => $"{file.RelativePath} -> {name}"))
            .ToArray();

        Assert.Contains("CorpseQuestDrops", corpseNames.Types);
        Assert.Contains("ItemTransactionQuestDrop", corpseNames.Types);
        Assert.True(
            violations.Length == 0,
            "The quest and economy areas use no corpse type:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // R-c.
    [Fact]
    public void TheCorpseAreaReachesTheQuestAreaOnlyThroughTheQuestDropDemand()
    {
        var questNames = AreaNames(QuestAreaFiles);
        var corpseFiles = ItemsFiles.Where(file => InArea(file, CorpseAreaFiles)).ToArray();
        var violations = corpseFiles
            .SelectMany(file =>
            {
                var code = CodeOf(file.Text);
                var names = NamesUsedIn(code, questNames).ToList();
                if (code.Contains("Mmorpg.GameService.Quests", StringComparison.Ordinal))
                {
                    names.Add("Mmorpg.GameService.Quests");
                }

                return names.Select(name => $"{file.RelativePath} -> {name}");
            })
            .ToArray();
        var demandUsers = corpseFiles
            .Where(file => Regex.IsMatch(CodeOf(file.Text), $@"\b{QuestDropInterface}\b"))
            .Select(file => file.RelativePath.Substring(ItemsPath.Length))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "The corpse area learns what a quest asks for through IQuestDropDemand only:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
        Assert.Equal(
            ["Transactions/Domain/CorpseQuestDrops.cs", "Transactions/Handlers/CorpseCommandSource.cs"],
            demandUsers);
        Assert.DoesNotContain(QuestDropInterface, questNames.Types);
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

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
