using System.Diagnostics;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Platform.Tests;

public sealed class DatabaseTracingTests
{
    [Theory]
    [InlineData("select 1;", "postgresql select")]
    [InlineData("\n    INSERT INTO item_operations (id) values (@Id);", "postgresql insert")]
    [InlineData("with recent as (select 1) select * from recent;", "postgresql with")]
    [InlineData("", "postgresql")]
    [InlineData(null, "postgresql")]
    public void CommandSpansAreNamedAfterTheSqlVerb(string? commandText, string expected)
    {
        Assert.Equal(expected, PlatformServiceCollectionExtensions.CommandSpanName(commandText));
    }

    // Background polling runs outside any request, and its commands must not become root
    // traces; a command inside a traced request or operation joins that trace.
    [PostgresIntegrationFact]
    public async Task CommandsBecomeSpansOnlyInsideATracedOperation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        using var operationSource = new ActivitySource("Mmorpg.Tests.DatabaseTracing");
        var commandSpans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Npgsql" or "Mmorpg.Tests.DatabaseTracing",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name == "Npgsql")
                {
                    lock (commandSpans)
                    {
                        commandSpans.Add(activity);
                    }
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        await using var dataSource = PlatformServiceCollectionExtensions.CreateDataSource(
            database.ConnectionString);

        await using (var background = dataSource.CreateCommand("select 1;"))
        {
            await background.ExecuteScalarAsync(cancellationToken);
        }

        ActivitySpanId operationSpanId;
        using (var operation = operationSource.StartActivity("operation"))
        {
            operationSpanId = operation!.SpanId;
            await using var traced = dataSource.CreateCommand("select 2;");
            await traced.ExecuteScalarAsync(cancellationToken);
        }

        Activity span;
        lock (commandSpans)
        {
            span = Assert.Single(commandSpans);
        }

        Assert.Equal("postgresql select", span.DisplayName);
        Assert.Equal(operationSpanId, span.ParentSpanId);
    }
}
