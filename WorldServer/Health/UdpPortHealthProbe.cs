using System.Net;
using System.Net.Sockets;
using ShooterMmo.Shared.Health;

namespace WorldServer.Health;

public static class UdpPortHealthProbe
{
    public static DependencyHealth CheckAvailable(int port)
    {
        var target = $"0.0.0.0:{port}/udp";

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ExclusiveAddressUse = true
            };
            socket.Bind(new IPEndPoint(IPAddress.Any, port));

            return new DependencyHealth("udp-port", target, true, null);
        }
        catch (SocketException exception)
        {
            return new DependencyHealth("udp-port", target, false, exception.Message);
        }
    }
}
