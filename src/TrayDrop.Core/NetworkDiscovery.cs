using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TrayDrop.Core;

public sealed record LanAddress(string AdapterName, IPAddress Address, IPAddress SubnetMask)
{
    public string ConnectionUrl(int port, string code) => $"http://{Address}:{port}/#pair={code}";
    public override string ToString() => $"{AdapterName} ({Address})";
}

public static class NetworkDiscovery
{
    public static IReadOnlyList<LanAddress> GetAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet
                            or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx
                            or NetworkInterfaceType.FastEthernetT)
            .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(a.Address) &&
                            !(a.Address.GetAddressBytes()[0] == 169 && a.Address.GetAddressBytes()[1] == 254))
                .Select(a => new LanAddress(n.Name, a.Address, a.IPv4Mask)))
            .ToArray();

    public static bool IsAllowedClient(IPAddress? address, IReadOnlyList<LanAddress> networks)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var remote = address.GetAddressBytes();
        return networks.Any(network =>
        {
            var local = network.Address.GetAddressBytes();
            var mask = network.SubnetMask.GetAddressBytes();
            return local.Length == 4 && mask.Length == 4 && mask.Any(b => b != 0) &&
                   Enumerable.Range(0, 4).All(i => (remote[i] & mask[i]) == (local[i] & mask[i]));
        });
    }

    public static bool IsAllowedHost(string host, IReadOnlyList<LanAddress> networks)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out var address) &&
               (IPAddress.IsLoopback(address) || networks.Any(n => n.Address.Equals(address)));
    }
}
