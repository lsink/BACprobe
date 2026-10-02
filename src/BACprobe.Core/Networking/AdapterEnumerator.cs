using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BACprobe.Core.Networking;

public static class AdapterEnumerator
{
    private static readonly string[] VirtualMarkers =
        ["virtual", "vmware", "vbox", "hyper-v", "vethernet", "wsl", "docker", "tap-", "tunnel", "vpn", "loopback", "bluetooth"];

    public static IReadOnlyList<AdapterInfo> GetAdapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            var isLoopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            var isUp = nic.OperationalStatus == OperationalStatus.Up;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                list.Add(new AdapterInfo(nic.Id, nic.Name, nic.Description, ua.Address, ua.IPv4Mask,
                    isUp, IsVirtualAdapter(nic.Name, nic.Description), isLoopback));
            }
        }
        return list;
    }

    public static bool IsVirtualAdapter(string name, string description)
    {
        var text = $"{name} {description}";
        return VirtualMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
