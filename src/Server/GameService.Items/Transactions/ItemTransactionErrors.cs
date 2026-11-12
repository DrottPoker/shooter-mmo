using System.Diagnostics.CodeAnalysis;

namespace Mmorpg.GameService.Items;

internal sealed class ItemTransactionRejectedException(
    string code,
    string message) : Exception(message)
{
    public string Code { get; } = code;
}

// The single place that throws an item transaction rejection. The kernel catches it and
// stores or releases the rejected result.
internal static class ItemTransactionRejection
{
    [DoesNotReturn]
    public static void Reject(string code, string message)
    {
        throw new ItemTransactionRejectedException(code, message);
    }
}
