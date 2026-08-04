using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Wollet.Client.Core;

public sealed class RouteDeviceInfoProvider : IDeviceInfoProvider
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public async Task<DeviceIdentity> GetAsync(Uri server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        var localAddress = await FindRoutedLocalAddressAsync(server, cancellationToken);
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var ownsAddress = adapter.GetIPProperties().UnicastAddresses
                .Any(address => address.Address.Equals(localAddress));
            if (!ownsAddress)
            {
                continue;
            }

            var bytes = adapter.GetPhysicalAddress().GetAddressBytes();
            if (bytes.Length != 6 || bytes.All(value => value == 0))
            {
                continue;
            }

            return new DeviceIdentity(Environment.MachineName, FormatMacAddress(bytes));
        }

        throw new InvalidOperationException("无法确定访问服务端所使用网卡的 MAC 地址");
    }

    public static string FormatMacAddress(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 6)
        {
            throw new ArgumentException("MAC 地址必须包含 6 个字节", nameof(bytes));
        }

        return string.Join(':', bytes.ToArray().Select(value => value.ToString("X2")));
    }

    private static async Task<IPAddress> FindRoutedLocalAddressAsync(
        Uri server,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(server.DnsSafeHost, cancellationToken);
        var port = server.IsDefaultPort
            ? server.Scheme == Uri.UriSchemeHttps ? 443 : 80
            : server.Port;
        Exception? lastError = null;

        foreach (var address in addresses.Where(address =>
                     address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(ProbeTimeout);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), attempt.Token);
                if (socket.LocalEndPoint is IPEndPoint local)
                {
                    return local.Address;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = exception;
            }
        }

        throw new InvalidOperationException("无法连接服务端以确定本地网卡", lastError);
    }
}
