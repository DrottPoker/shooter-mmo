using System.Text.Json;

namespace Mmorpg.GameService.Items.Transactions;

// The persisted JSON format of item operations. It shapes the canonical request and its
// hash, stored results, death event fingerprints, and corpse snapshots, so it must never
// change.
internal static class ItemOperationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
