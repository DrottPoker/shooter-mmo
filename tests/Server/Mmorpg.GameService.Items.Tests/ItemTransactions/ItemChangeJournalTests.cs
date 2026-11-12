using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// White-box tests of the change journal rules and the finalizer's audit requirement.
public sealed class ItemChangeJournalTests
{
    [PostgresIntegrationFact]
    public async Task JournalRejectsEmptyAuditsAndLimitsOverflowToPlayerDeath()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(initializeDatabase: false);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var grant = Journal(connection, transaction, ItemOperationKinds.Grant);

        Assert.Equal(
            "An item audit change requires before or after state.",
            Assert.Throws<InvalidOperationException>(
                () => grant.AddItemAudit("item_granted", Guid.NewGuid(), null, null)).Message);
        Assert.Equal(
            "A metadata audit change requires before or after state.",
            Assert.Throws<InvalidOperationException>(
                () => grant.AddMetadataAudit("metadata", null, null)).Message);
        Assert.Equal(
            "Only player death may permit involuntary carry overflow.",
            Assert.Throws<InvalidOperationException>(grant.AllowInvoluntaryDeathHardCapOverflow).Message);
        Assert.False(grant.AllowsInvoluntaryHardCapOverflow);

        var removedItemId = Guid.NewGuid();
        grant.AddItemAudit("item_destroyed", removedItemId, "{}", null);
        grant.AddMetadataAudit("metadata", new { Value = 1 }, null);
        grant.TouchContainer(null);
        grant.TouchContainer(removedItemId);
        grant.TouchContainer(removedItemId);
        Assert.Null(grant.AuditChanges[0].ItemInstanceId);
        Assert.Equal("""{"value":1}""", grant.AuditChanges[1].BeforeState);
        Assert.Single(grant.TouchedContainerIds);

        var death = Journal(connection, transaction, ItemOperationKinds.ProcessPlayerDeath);
        death.AllowInvoluntaryDeathHardCapOverflow();
        Assert.True(death.AllowsInvoluntaryHardCapOverflow);
        await transaction.RollbackAsync();
    }

    [PostgresIntegrationFact]
    public async Task FinalizerRejectsAJournalWithoutAuditChanges()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(initializeDatabase: false);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var session = Session(connection, transaction, ItemOperationKinds.Grant);
        var journal = new ItemChangeJournal(
            session,
            new ItemStateReader(session, new ItemActorAuthority(session.Actor)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ItemTransactionFinalizer(session, journal).FinalizeAsync(CancellationToken.None));

        Assert.Equal("Item operation 'grant' produced no audit changes.", exception.Message);
        await transaction.RollbackAsync();
    }

    // A section is a top-level key of the stored result, and stored results are read without
    // regard to case, so no section may take the name of a result property.
    [PostgresIntegrationFact]
    public async Task JournalRefusesResultPropertyNamesAndKeepsSectionsInTheirOrder()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(initializeDatabase: false);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var journal = Journal(connection, transaction, ItemOperationKinds.Grant);

        foreach (var name in new[] { "operationId", "OperationKind", "ITEMCHANGES", "recoveryDeliveryIds", "sections" })
        {
            var refused = Assert.Throws<ArgumentException>(() => journal.SetSection(name, 1));
            Assert.Equal(
                $"The result section name '{name}' is a property of the item transaction result. (Parameter 'name')",
                refused.Message);
        }

        Assert.Throws<ArgumentException>(() => journal.SetSection(" ", 1));
        Assert.Null(journal.BuildSections());

        journal.SetSection("first", new { Value = 1 });
        journal.SetSection("second", new[] { "a" });
        journal.SetSection("first", new { Value = 3, Name = "later" });
        var sections = journal.BuildSections()!;

        Assert.Equal(["first", "second"], sections.Keys);
        Assert.Equal("""{"value":3,"name":"later"}""", sections["first"].GetRawText());
        Assert.Equal("""["a"]""", sections["second"].GetRawText());
        await transaction.RollbackAsync();
    }

    private static ItemChangeJournal Journal(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        string operationKind)
    {
        var session = Session(connection, transaction, operationKind);
        return new ItemChangeJournal(
            session,
            new ItemStateReader(session, new ItemActorAuthority(session.Actor)));
    }

    private static ItemTransactionSession Session(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        string operationKind)
    {
        return new ItemTransactionSession(
            connection,
            transaction,
            Guid.NewGuid(),
            operationKind,
            ItemTransactionActor.ForSystem());
    }
}
