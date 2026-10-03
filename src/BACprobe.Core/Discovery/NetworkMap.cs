using System.IO.BACnet;

namespace BACprobe.Core.Discovery;

/// <summary>How a device's address looks, including devices behind a router (which the library reports as router address + routed source).</summary>
public static class AddressInfo
{
    public static bool IsRouted(BacnetAddress a) => a.RoutedSource is { net: not 0 };

    /// <summary>The BACnet network number the device is on: 0 for the local network, the router's far-side number otherwise.</summary>
    public static ushort NetworkOf(BacnetAddress a) => a.RoutedSource is { net: not 0 } rs ? rs.net : a.net;

    /// <summary>The device's MAC on its own network: one byte for MS/TP (shown as a plain number), otherwise hex. Empty for plain IP devices.</summary>
    public static string MacOf(BacnetAddress a)
    {
        var mac = a.RoutedSource is { net: not 0 } rs ? rs.adr : a.net != 0 ? a.adr : null;
        return mac is null or { Length: 0 } ? "" : mac.Length == 1 ? mac[0].ToString() : Convert.ToHexString(mac);
    }

    /// <summary>"192.168.1.5:47808" for a plain device; "network 1001, MAC 5 (via 192.168.1.9:47808)" for one behind a router.</summary>
    public static string Describe(BacnetAddress a) =>
        IsRouted(a) ? $"network {NetworkOf(a)}, MAC {MacOf(a)} (via {a})" : a.ToString();
}

/// <summary>A router's announcement: where it is, and which networks it says it can reach.</summary>
public sealed record RouterObservation(string Address, IReadOnlyList<ushort> Networks);

public static class RouterAnnouncement
{
    /// <summary>The body of an I-Am-Router-To-Network message: network numbers, two bytes each, big-endian. A stray odd byte is ignored.</summary>
    public static IReadOnlyList<ushort> ParseNetworks(ReadOnlySpan<byte> body)
    {
        var networks = new List<ushort>();
        for (var i = 0; i + 1 < body.Length; i += 2)
        {
            var n = (ushort)((body[i] << 8) | body[i + 1]);
            if (n != 0 && !networks.Contains(n)) networks.Add(n);
        }
        return networks;
    }
}

/// <summary>One network and the devices found on it. Number 0 is the network this PC is on.</summary>
public sealed record MapNetwork(ushort Number, IReadOnlyList<string> Routers, IReadOnlyList<DiscoveredDevice> Devices)
{
    public bool IsLocal => Number == 0;
}

public sealed record NetworkMap(IReadOnlyList<MapNetwork> Networks, IReadOnlyList<NetworkFinding> Findings)
{
    /// <summary>Plain lines for the screen or console: this network, then each routed network.</summary>
    public IReadOnlyList<string> Lines(string localLabel)
    {
        var lines = new List<string>();
        foreach (var n in Networks)
        {
            var count = n.Devices.Count == 1 ? "1 device" : $"{n.Devices.Count} devices";
            if (n.IsLocal)
                lines.Add($"This network ({localLabel}): {count}");
            else
            {
                var via = n.Routers.Count == 0 ? "no router announced it" : "via " + string.Join(" and ", n.Routers);
                lines.Add($"Network {n.Number} ({via}): {(n.Devices.Count == 0 ? "no devices answered" : count)}");
            }
        }
        if (Networks.All(n => n.IsLocal) && Networks.Count > 0)
            lines.Add("No routers answered, and no devices were found behind one: everything found is on your own network.");
        return lines;
    }
}

public static class NetworkMapBuilder
{
    /// <summary>
    /// Group devices by the network they are on and match them to the routers that announced those networks.
    /// <paramref name="scanWasFiltered"/> says the scan was limited to a range of device numbers, in which case an
    /// empty network proves nothing.
    /// </summary>
    public static NetworkMap Build(IReadOnlyList<DiscoveredDevice> devices, IReadOnlyList<RouterObservation> routers, bool scanWasFiltered = false)
    {
        var routersByNetwork = new Dictionary<ushort, List<string>>();
        foreach (var r in routers)
            foreach (var n in r.Networks)
            {
                if (!routersByNetwork.TryGetValue(n, out var list)) routersByNetwork[n] = list = [];
                if (!list.Contains(r.Address)) list.Add(r.Address);
            }

        var devicesByNetwork = devices.GroupBy(d => AddressInfo.NetworkOf(d.Address)).ToDictionary(g => g.Key, g => g.OrderBy(d => d.InstanceId).ToList());
        var numbers = devicesByNetwork.Keys.Concat(routersByNetwork.Keys).Append((ushort)0).Distinct().Order().ToList();

        var networks = numbers.Select(n => new MapNetwork(n,
            routersByNetwork.TryGetValue(n, out var rs) ? [.. rs] : [],
            devicesByNetwork.TryGetValue(n, out var ds) ? ds : [])).ToList();

        var findings = new List<NetworkFinding>();
        foreach (var n in networks.Where(n => !n.IsLocal))
        {
            if (n.Routers.Count > 1)
                findings.Add(new(FindingSeverity.Warning,
                    $"Network {n.Number} is announced by {n.Routers.Count} routers",
                    $"{string.Join(" and ", n.Routers)} both say they can reach network {n.Number}.",
                    "That is fine for a redundant path to one network, but it is a mistake if they are two separate trunks that were both given the same network number: devices on them would be confused.",
                    "Check each router's network number. Every separate trunk or subnet needs its own."));

            if (n.Routers.Count == 0 && n.Devices.Count > 0)
                findings.Add(new(FindingSeverity.Warning,
                    $"Devices were found on network {n.Number}, but no router announced it",
                    $"{n.Devices.Count} device(s) answered from network {n.Number} (for example device {n.Devices[0].InstanceId}).",
                    "The router does not answer \"who is a router\" requests (some do not), or its announcement was lost.",
                    "Scan again. If the devices read fine, nothing is wrong."));

            if (n.Routers.Count > 0 && n.Devices.Count == 0 && !scanWasFiltered)
                findings.Add(new(FindingSeverity.Warning,
                    $"Router {n.Routers[0]} announces network {n.Number}, but no devices on it answered",
                    $"Nothing on network {n.Number} replied to Who-Is.",
                    "The trunk is unplugged, wired wrong, or at the wrong baud rate; the devices are powered off; or the router's MS/TP address range (max master) does not include them.",
                    "Check the trunk wiring, termination and baud rate, and the router's own settings, then scan again."));
        }
        return new NetworkMap(networks, findings);
    }
}
