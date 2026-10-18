using System.Reflection;
using System.Text.RegularExpressions;
using Mmorpg.GameService.Testing;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// Guards the item transaction kernel boundary described in docs/GAME_SERVICE_MODULES.md:
// only the kernel ends transactions or touches the operation ledger, handlers are
// stateless and sequential, take locks in the documented order, reject only through
// ItemTransactionRejection, and only the listed types cross module boundaries.
public sealed partial class ItemTransactionKernelBoundaryTests
{
    private const string TransactionsPath = "GameService.Items/Transactions/";
    private const string KernelPath = "GameService.Items/Transactions/Kernel/";
    private const string HandlersPath = "GameService.Items/Transactions/Handlers/";
    private const string HandlersNamespace = "Mmorpg.GameService.Items.Transactions.Handlers";

    // Files outside the kernel that run inside an item transaction.
    private static IReadOnlyList<SourceFile> TransactionFiles { get; } = GameServiceSourceMap
        .LoadSourceFiles()
        .Where(file => file.RelativePath.StartsWith(TransactionsPath, StringComparison.Ordinal)
            && !file.RelativePath.StartsWith(KernelPath, StringComparison.Ordinal))
        .ToArray();

    [Fact]
    public void OnlyTheKernelEndsTransactionsAndTouchesTheOperationLedger()
    {
        var violations = TransactionFiles
            .SelectMany(file => KernelOnlyPattern().Matches(file.Text)
                .Select(match => $"{file.RelativePath} -> {match.Value}"))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Only Transactions/Kernel may begin, commit, or roll back a transaction, use the mutation savepoint, set timeouts, or touch item_operations:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
        Assert.NotEmpty(TransactionFiles);
    }

    [Fact]
    public void HandlersHoldOnlyReadonlyInjectedState()
    {
        var handlerTypes = GameServiceAssemblies.Items
            .GetTypes()
            .Where(type => string.Equals(type.Namespace, HandlersNamespace, StringComparison.Ordinal)
                && !type.IsNested
                && type.GetInterfaces().Any(contract => contract.IsGenericType
                    && string.Equals(contract.GetGenericTypeDefinition().Name, "IItemCommandHandler`1", StringComparison.Ordinal)))
            .ToArray();
        var mutableFields = handlerTypes
            .SelectMany(type => type
                .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => !field.IsInitOnly && !field.IsLiteral)
                .Select(field => $"{type.Name}.{field.Name}"))
            .ToArray();

        Assert.Equal(22, handlerTypes.Length);
        Assert.True(
            mutableFields.Length == 0,
            "Item command handlers are shared across concurrent transactions and may hold only readonly injected fields:"
            + Environment.NewLine + string.Join(Environment.NewLine, mutableFields));
    }

    [Fact]
    public void TransactionCodeRunsItsCommandsSequentially()
    {
        var violations = TransactionFiles
            .Where(file => file.Text.Contains("Task.WhenAll", StringComparison.Ordinal)
                || file.Text.Contains("Task.WhenAny", StringComparison.Ordinal)
                || file.Text.Contains("Parallel.", StringComparison.Ordinal))
            .Select(file => file.RelativePath)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "One transaction uses one connection, so its commands must run one after another:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void HandlersTakeLocksInTheDocumentedOrder()
    {
        var violations = new List<string>();
        var orderedPairs = 0;
        foreach (var file in TransactionFiles.Where(file =>
                     file.RelativePath.StartsWith(HandlersPath, StringComparison.Ordinal)))
        {
            foreach (var method in MethodPattern().Split(file.Text).Skip(1))
            {
                var ranks = LockCallPattern().Matches(method)
                    .Select(match => (match.Value, Rank: LockRank(match.Value)))
                    .ToArray();
                for (var index = 1; index < ranks.Length; index++)
                {
                    orderedPairs++;
                    if (ranks[index].Rank < ranks[index - 1].Rank)
                    {
                        violations.Add(
                            $"{file.RelativePath}: {ranks[index].Value} after {ranks[index - 1].Value}");
                    }
                }
            }
        }

        Assert.True(orderedPairs >= 20, $"Only {orderedPairs} lock call pairs were checked.");
        Assert.True(
            violations.Count == 0,
            "Handlers lock the advisory key, then character states, then the corpse row, then the mutation scope:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void HandlersRejectOnlyThroughReject()
    {
        var violations = GameServiceSourceMap.LoadSourceFiles()
            .Where(file => file.Text.Contains("new ItemTransactionRejectedException(", StringComparison.Ordinal)
                && !string.Equals(file.RelativePath, RejectionOwner, StringComparison.Ordinal))
            .Select(file => file.RelativePath)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Item transaction code rejects through ItemTransactionRejection.Reject only:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TransactionCodeRejectsOnlyWithTransactionErrorCodes()
    {
        var violations = GameServiceSourceMap.LoadSourceFiles()
            .Where(file => file.RelativePath.StartsWith(TransactionsPath, StringComparison.Ordinal)
                && file.Text.Contains("ItemApiErrorCodes", StringComparison.Ordinal))
            .Select(file => file.RelativePath)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "The transaction kernel owns its error codes and never uses the HTTP API codes:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void CrossModuleStatementsStayInTheirTransactionTypes()
    {
        var transactionFiles = GameServiceSourceMap.LoadSourceFiles()
            .Where(file => file.RelativePath.StartsWith(TransactionsPath, StringComparison.Ordinal))
            .ToArray();
        var users = transactionFiles
            .Where(file => CrossModulePattern().IsMatch(file.Text))
            .Select(file => file.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(CrossModuleOwners.Order(StringComparer.Ordinal), users);
    }

    // The only item transaction types that use another module's contracts or SQL
    // fragments, with the cross-module statements of docs/GAME_SERVICE_MODULES.md.
    private static readonly string[] CrossModuleOwners =
    [
        // Composition only: passes the contracts to the types below.
        "GameService.Items/Transactions/ItemTransactionService.cs",
        // S32: the operation claim references the actor account and character.
        "GameService.Items/Transactions/Kernel/ItemOperationLedger.cs",
        // S22 to S25 and S29: live session and worker lease authority.
        "GameService.Items/Transactions/Context/ItemSimulationAuthority.cs",
        // S26 to S28: character locks and offline presence.
        "GameService.Items/Transactions/Context/CharacterItemStateLocks.cs",
        // S30: insurance charges the character wallet.
        "GameService.Items/Transactions/Handlers/ItemPolicyCommandHandler.cs",
        // S41 and S42: vendor purchases debit and sales credit the character wallet.
        "GameService.Items/Transactions/Handlers/VendorCommandHandler.cs",
        // S31: the account's active characters for a tier change.
        "GameService.Items/Transactions/Handlers/SecureContainerTierCommandHandler.cs",
        // S33: the dead character's name and Shard.
        "GameService.Items/Transactions/Handlers/PlayerDeath/PlayerDeathCommandHandler.cs",
        // S41: equipment reads the character level for its level requirement.
        "GameService.Items/Transactions/Context/ItemSlotPlacement.cs"
    ];

    // The file that defines the single rejection throw, ItemTransactionRejection.
    private const string RejectionOwner = "GameService.Items/Transactions/ItemTransactionErrors.cs";

    private static int LockRank(string call)
    {
        return call switch
        {
            "LockDeathEventKeyAsync" or "pg_advisory_xact_lock" => 0,
            "LockCharacterStatesAsync" or "LockCharacterStateForIdempotentEventAsync" => 1,
            "LockOpenCorpseAsync" => 2,
            "LockMutationScopeAsync" => 3,
            _ => throw new InvalidOperationException($"Unknown lock call '{call}'.")
        };
    }

    [GeneratedRegex(@"\b(?:BeginTransactionAsync|CommitAsync|RollbackAsync|set_config|savepoint|item_operations)\b", RegexOptions.IgnoreCase)]
    private static partial Regex KernelOnlyPattern();

    [GeneratedRegex(@"\n    (?:public|internal|private)[^\n]*\(")]
    private static partial Regex MethodPattern();

    [GeneratedRegex(@"\b(?:LockDeathEventKeyAsync|pg_advisory_xact_lock|LockCharacterStatesAsync|LockCharacterStateForIdempotentEventAsync|LockOpenCorpseAsync|LockMutationScopeAsync)\b")]
    private static partial Regex LockCallPattern();

    [GeneratedRegex(@"\b(?:ICharacterLocks|ICharacterWallet|ICharacterProgression|ISimulationSessionAuthority|ISimulationWorkerAuthority|ISimulationPresence|AccountsSql|CharactersSql|PlacementSql)\b|Mmorpg\.GameService\.(?:Accounts|Characters|Placement)\.Contracts")]
    private static partial Regex CrossModulePattern();
}
