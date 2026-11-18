using System.Globalization;

namespace Mmorpg.Server.Common.Contracts;

// The version of the call between SimulationWorker and GameService: the routes that the worker
// calls, their request and response shapes, and the rules for what the answers mean. Both sides
// compile this one constant. The worker sends it with its registration heartbeat, and GameService
// refuses a heartbeat of another version, or of none, with ServiceContract.MismatchCode, so the two
// never run side by side with different ideas of the call.
//
// Bump it by one in the same change that does any of these, and in no other:
//   - removes or renames a route, a field, or an error code that the other side reads;
//   - changes the meaning of a field or an error code, or the rule that a status carries;
//   - makes the other side send or accept something that the version before it refused.
// Adding an optional field or a new route that the other side may ignore needs no bump. A bump
// needs the update order of docs/DEPLOYMENT.md under Update A Deployment: stop the workers, update GameService,
// start the workers of the new version.
public static class ServiceContract
{
    public const int Version = 1;

    // The code of the refusal, a 409, so neither side mistakes it for a credential failure.
    public const string MismatchCode = "service_contract_mismatch";

    // What GameService says when it refuses a heartbeat. Both versions are in the text, in one
    // form that TryReadGameServiceVersion reads back, so the worker can say which side is older.
    // The worker's version is null when its heartbeat carried none, as one of a worker older than
    // this check does.
    public static string MismatchMessage(int? workerVersion)
    {
        var worker = workerVersion is { } version
            ? version.ToString(CultureInfo.InvariantCulture)
            : "none";
        return $"The worker runs service contract version {worker} and GameService runs version "
            + $"{Version.ToString(CultureInfo.InvariantCulture)}. Update the older side so both run the same version.";
    }

    // The GameService version that a MismatchMessage names, or null for any other text.
    public static int? TryReadGameServiceVersion(string? message)
    {
        const string marker = "GameService runs version ";
        if (message is null)
        {
            return null;
        }

        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var digits = message.AsSpan(start + marker.Length);
        var length = 0;
        while (length < digits.Length && char.IsAsciiDigit(digits[length]))
        {
            length++;
        }

        return length > 0
            && int.TryParse(digits[..length], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
    }
}
