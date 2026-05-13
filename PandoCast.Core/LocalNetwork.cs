using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PandoCast.Core
{
    internal static class LocalNetwork
    {
        public static IPAddress GetLocalIPv4AddressFor(Uri? remoteDeviceUri = null)
        {
            IPAddress? remoteAddress = ResolveRemoteAddress(remoteDeviceUri);

            if (remoteAddress != null)
            {
                IPAddress? localAddress = GetLocalIPv4AddressForRemote(remoteAddress);
                if (localAddress != null) return localAddress;
            }

            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                ?? IPAddress.Loopback;
        }

        private static IPAddress? ResolveRemoteAddress(Uri? remoteDeviceUri)
        {
            if (remoteDeviceUri == null) return null;

            if (IPAddress.TryParse(remoteDeviceUri.Host, out var parsedAddress)) return parsedAddress;

            try
            {
                return Dns.GetHostAddresses(remoteDeviceUri.Host)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
            }
            catch
            {
                return null;
            }
        }

        private static IPAddress? GetLocalIPv4AddressForRemote(IPAddress remoteAddress)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(remoteAddress, 8009);

                if (socket.LocalEndPoint is IPEndPoint localEndPoint)
                {
                    return localEndPoint.Address;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }
    }
}
