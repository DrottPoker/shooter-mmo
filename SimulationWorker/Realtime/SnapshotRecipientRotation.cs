namespace SimulationWorker.Realtime;

internal sealed class SnapshotRecipientRotation
{
    private int nextStart;

    public int Begin(int recipientCount)
    {
        if (recipientCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recipientCount));
        }

        return nextStart % recipientCount;
    }

    public void Complete(int recipientCount, int lastAdmittedRecipientOffset)
    {
        if (recipientCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recipientCount));
        }

        if (lastAdmittedRecipientOffset >= recipientCount)
        {
            throw new ArgumentOutOfRangeException(nameof(lastAdmittedRecipientOffset));
        }

        var advance = lastAdmittedRecipientOffset >= 0
            ? lastAdmittedRecipientOffset + 1
            : 1;
        nextStart = (nextStart + advance) % recipientCount;
    }
}
