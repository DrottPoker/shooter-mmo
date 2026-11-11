using System.Data;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Content;

// Stops the start when the item or quest catalog that the content routes serve is not the
// revision that the Items and Quests modules mirrored and run with. It runs as a hosted
// service, so it sees the database after the startup migrations and seeds.
internal sealed class ContentRevisionCheck(
    ContentFileStore store,
    NpgsqlDataSource dataSource,
    IItemCatalogRevisionProvider itemCatalogRevisions,
    IQuestCatalogRevisionProvider questCatalogRevisions,
    ILogger<ContentRevisionCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await VerifyAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            // A database that cannot be read is reported by readiness, not by this check, so
            // the start goes on as it did before this check existed.
            logger.LogWarning(
                exception,
                "The content revision check could not read the mirrored catalog revisions and was skipped.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        string? itemRevision;
        string? questRevision;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(
                         IsolationLevel.RepeatableRead,
                         cancellationToken))
        {
            var scope = new PostgresTransactionScope(connection, transaction);
            itemRevision = await itemCatalogRevisions.GetCurrentRevisionAsync(scope, cancellationToken);
            questRevision = await questCatalogRevisions.GetCurrentRevisionAsync(scope, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var errors = new List<string>();
        Compare(ContentFileKinds.ItemCatalog, "Items", itemRevision, errors);
        Compare(ContentFileKinds.QuestCatalog, "Quests", questRevision, errors);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "GameService would serve content that its modules do not run with:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }
    }

    private void Compare(string kind, string module, string? moduleRevision, List<string> errors)
    {
        var served = store.FindCatalog(kind)?.Revision;
        if (!string.Equals(served, moduleRevision, StringComparison.Ordinal))
        {
            errors.Add(
                $"The {kind} served is revision '{served ?? "(none)"}', but the {module} module "
                + $"runs with revision '{moduleRevision ?? "(none)"}'.");
        }
    }
}
