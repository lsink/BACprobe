using System.Net;

namespace BACprobe.Core.Networking;

/// <summary>Pure IPv4 subnet math.</summary>
public static class Subnet
{
    public static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) throw new ArgumentException("IPv4 address required.", nameof(ip));
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static IPAddress FromUInt(uint v) =>
        new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    public static int PrefixLength(IPAddress mask)
    {
        var m = ToUInt(mask);
        var bits = 0;
        while (bits < 32 && (m & (0x80000000u >> bits)) != 0) bits++;
        // Reject non-contiguous masks (e.g. 255.0.255.0).
        var expected = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
        if (m != expected) throw new ArgumentException("Subnet mask is not contiguous.", nameof(mask));
        return bits;
    }

    public static IPAddress NetworkAddress(IPAddress ip, IPAddress mask) =>
        FromUInt(ToUInt(ip) & ToUInt(mask));

    public static IPAddress BroadcastAddress(IPAddress ip, IPAddress mask) =>
        FromUInt(ToUInt(ip) | ~ToUInt(mask));

    public static bool Contains(IPAddress network, IPAddress mask, IPAddress ip) =>
        (ToUInt(ip) & ToUInt(mask)) == (ToUInt(network) & ToUInt(mask));

    /// <summary>True when two interface addresses sit on overlapping IPv4 networks.</summary>
    public static bool SameSubnet(IPAddress ipA, IPAddress maskA, IPAddress ipB, IPAddress maskB)
    {
        // Overlap if either network contains the other's network address (covers differing prefix lengths).
        var netA = NetworkAddress(ipA, maskA);
        var netB = NetworkAddress(ipB, maskB);
        return Contains(netA, maskA, netB) || Contains(netB, maskB, netA);
    }

    public static bool IsLinkLocal(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }

    public static string Cidr(IPAddress ip, IPAddress mask) =>
        $"{NetworkAddress(ip, mask)}/{PrefixLength(mask)}";
}
