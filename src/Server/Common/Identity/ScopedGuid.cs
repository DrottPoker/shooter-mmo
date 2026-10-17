using System.Security.Cryptography;
using System.Text;

namespace Mmorpg.Server.Common.Identity;

// Deterministic version 5 style ids derived from a scope and a source id. GameService and
// SimulationWorker persist and compare them, so the derivation must never change.
public static class ScopedGuid
{
    public static Guid Create(string scope, Guid sourceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            scope + ":" + sourceId.ToString("N")));
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
}
