using System.Reflection;
using System.Text.RegularExpressions;

namespace Mmorpg.Backend.Tests.Architecture;

// Guards the item transaction kernel boundary described in docs/GAME_SERVICE_MODULES.md:
// only the kernel ends transactions or touches the operation ledger, handlers are
// stateless and sequential, take locks in the documented order, and reject only through
// ItemTransactionContext.Reject.
public sealed partial class ItemTransactionKernelBoundaryTests
{
    private const string TransactionsPath = "GameService.Items/Transactions/";
    private const string KernelPath = "GameService.Items/Transactions/Kernel/";
    private const string HandlersPath = "GameService.Items/Transactions/Handlers/";
    private const string HandlersNamespace = "Mmorpg.GameService.Items.Transactions.Handlers";

    // Files outside the kernel that run inside an item transaction.
    private static IReadOnlyList<SourceFile> TransactionFiles { get; } = GameServiceSourceMap
        .LoadSourceFiles()
        .Where(file => (file.RelativePath.StartsWith(TransactionsPath, StringComparison.Ordinal)
                || string.Equals(
                    file.RelativePath,
                    "GameService.Items/ItemTransactionContext.cs",
                    StringComparison.Ordinal))
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

        Assert.Equal(19, handlerTypes.Length);
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
            "Item transaction code rejects through ItemTransactionContext.Reject only:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // The file that defines the single rejection throw.
    private const string RejectionOwner = "GameService.Items/ItemTransactionContext.cs";

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
}
