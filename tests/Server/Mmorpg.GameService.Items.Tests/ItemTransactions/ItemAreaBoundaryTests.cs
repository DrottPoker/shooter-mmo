using System.Reflection;
using System.Text.RegularExpressions;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Testing;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// Guards the corpse area inside GameService.Items, as docs/GAME_SERVICE_MODULES.md describes
// under Areas: the corpse area learns what a quest asks for only through IQuestDropDemand, which
// no other Items code uses, and the kernel names no quest, vendor, or gathering feature. The quest
// and economy code live in the modules above Items, whose boundaries keep them apart.
public sealed partial class ItemAreaBoundaryTests
{
    private const string ItemsPath = "GameService.Items/";

    // The SPI through which the corpse area asks the quest code, and its declaration.
    private const string QuestDropDemand = "IQuestDropDemand";
    private const string QuestDropDemandFile = ItemsPath + "Contracts/IQuestDropDemand.cs";

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
    public void TheAreaListNamesExistingFilesOnce()
    {
        var listed = CorpseAreaFiles;
        var existing = ItemsFiles.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(listed.Length, listed.Distinct(StringComparer.Ordinal).Count());
        Assert.All(listed, path => Assert.Contains(ItemsPath + path, existing));
        Assert.Equal(
            ItemsFiles
                .Where(file => file.RelativePath.StartsWith(ItemsPath + "Transactions/Handlers/PlayerDeath/", StringComparison.Ordinal))
                .Select(file => file.RelativePath.Substring(ItemsPath.Length)),
            CorpseAreaFiles.Where(path => path.StartsWith("Transactions/Handlers/PlayerDeath/", StringComparison.Ordinal)));
    }

    // The corpse area asks the quest code only through IQuestDropDemand, and no other Items code
    // asks it what a quest needs. The other SPI that the quest code implements, the item catalog
    // check of the seeder, stays out of the corpse area.
    [Fact]
    public void OnlyTheCorpseAreaAsksQuestsAndOnlyThroughIQuestDropDemand()
    {
        var askers = ItemsFiles
            .Where(file => file.RelativePath != QuestDropDemandFile
                && Regex.IsMatch(CodeOf(file.Text), $@"\b{QuestDropDemand}\b"))
            .Select(file => file.RelativePath)
            .ToArray();
        var otherSpis = ItemsFiles
            .Where(file => InArea(file, CorpseAreaFiles)
                && Regex.IsMatch(CodeOf(file.Text), @"\bIItemCatalogReferences\b"))
            .Select(file => file.RelativePath)
            .ToArray();

        Assert.Contains(ItemsPath + "Transactions/Domain/CorpseQuestDrops.cs", askers);
        Assert.True(
            askers.All(path => InArea(path, CorpseAreaFiles)),
            "Only the corpse area asks the quest code what a quest needs:"
            + Environment.NewLine + string.Join(Environment.NewLine, askers.Where(path => !InArea(path, CorpseAreaFiles))));
        Assert.Empty(otherSpis);
    }

    // The kernel's code names no quest, vendor, or gathering feature, except the vendor profile
    // load of the mirrored item catalog.
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
        return InArea(file.RelativePath, area);
    }

    private static bool InArea(string relativePath, IEnumerable<string> area)
    {
        return area.Any(path => string.Equals(relativePath, ItemsPath + path, StringComparison.Ordinal));
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

    [GeneratedRegex(@"(?m)^\s*//.*$")]
    private static partial Regex LineCommentPattern();

    [GeneratedRegex(@"\$*""""""[\s\S]*?""""""|\$*@?""(?:\\.|[^""\\\n])*""")]
    private static partial Regex StringLiteralPattern();

    // A word part Quest, Vendor, or Gather, or an identifier that starts with one in lower case.
    [GeneratedRegex(@"\b\w*(?:Quest|Vendor|Gather)\w*|\b(?:quest|vendor|gather)\w*")]
    private static partial Regex FeatureIdentifierPattern();
}
