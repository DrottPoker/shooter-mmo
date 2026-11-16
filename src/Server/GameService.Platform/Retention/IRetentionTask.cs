using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Platform.Retention;

// How long a class of rows stays after it ended, as RetentionOptions sets it.
public enum RetentionClass
{
    ItemHistory,
    SessionHistory
}

// The rows of one table, with the rows that go with them, that the module owning the table trims.
// The retention host calls it again and again within a pass while it deletes a full batch.
public interface IRetentionTask
{
    // A short, stable snake case name, the task label of the retention metrics.
    string Name { get; }

    RetentionClass Class { get; }

    // Deletes up to the batch's size of the rows that ended before the batch's window, oldest
    // first, in the scope's transaction. It locks only the rows it deletes, with skip locked, so it
    // never waits for play, and never deletes a row that something still needs.
    Task<RetentionBatchResult> DeleteDueAsync(
        PostgresTransactionScope scope,
        RetentionBatch batch,
        CancellationToken cancellationToken);
}

// Rows are due once they ended longer ago than the window.
public sealed record RetentionBatch(TimeSpan Window, int Size);

// How many rows a batch deleted and the age of the oldest of them that tells the lag: the time
// since that row ended, which the host turns into the lag. Null when the batch deleted none, or
// only rows whose age tells no lag because they became due later than their window says.
public readonly record struct RetentionBatchResult(int Deleted, TimeSpan? OldestDueAge)
{
    public static RetentionBatchResult None { get; } = new(0, null);
}

// The row of a retention delete: count(*) as "Deleted" and the age in seconds of the oldest
// deleted row that tells the lag, measured with the database clock, as "OldestDueAgeSeconds".
public sealed class RetentionBatchRow
{
    public int Deleted { get; set; }

    public double? OldestDueAgeSeconds { get; set; }

    public RetentionBatchResult ToResult()
    {
        return new RetentionBatchResult(
            Deleted,
            Deleted > 0 && OldestDueAgeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
    }
}
