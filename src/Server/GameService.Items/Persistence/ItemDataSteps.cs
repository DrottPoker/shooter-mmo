using Dapper;
using Npgsql;

namespace Mmorpg.GameService.Items.Persistence;

// The content revision each data step of Items last ran for to completion. A migration that
// changes what a step computes deletes the step's row, so the next seed runs it again.
internal static class ItemDataSteps
{
    // The carry weight and capacity of every character, which the item catalog's weights and
    // Bag capacities decide.
    public const string CarryStateRecompute = "carry_state_recompute";

    public const string Table = "item_data_steps";

    // The recorded revision, or null when the step never completed.
    public static Task<string?> ReadAsync(
        NpgsqlConnection connection,
        string stepId,
        CancellationToken cancellationToken)
    {
        return connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "select revision from item_data_steps where id = @StepId;",
            new { StepId = stepId },
            cancellationToken: cancellationToken));
    }

    public static Task RecordAsync(
        NpgsqlConnection connection,
        string stepId,
        string revision,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            """
            insert into item_data_steps (id, revision, completed_at)
            values (@StepId, @Revision, now())
            on conflict (id) do update
            set revision = excluded.revision,
                completed_at = excluded.completed_at;
            """,
            new { StepId = stepId, Revision = revision },
            cancellationToken: cancellationToken));
    }
}
