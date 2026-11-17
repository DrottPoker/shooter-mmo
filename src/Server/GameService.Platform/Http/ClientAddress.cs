using System.Net;
using System.Net.Sockets;

namespace Mmorpg.GameService.Platform.Http;

// The one place that says who a request comes from. The forwarded headers middleware runs first
// and, only for a request from a trusted proxy, replaces the connection's address with the
// client's, as docs/SERVICE_FEATURES.md describes under The Client's Address. The rate limits,
// the log scope, and the loopback checks of the development routes all read it here.
public static class ClientAddress
{
    public const string UnknownPartition = "unknown";

    // The client's address, with an IPv4 address that arrived mapped into IPv6 as plain IPv4.
    public static IPAddress? Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var address = context.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    public static bool IsLoopback(HttpContext context)
    {
        return Of(context) is { } address && IPAddress.IsLoopback(address);
    }

    // The key of the per-address rate limits. One IPv6 host usually holds a whole /64, so an
    // IPv6 client is limited by its /64 network instead of by each of its addresses.
    public static string PartitionKey(HttpContext context)
    {
        var address = Of(context);
        if (address is null)
        {
            return UnknownPartition;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IsLoopback(address))
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
