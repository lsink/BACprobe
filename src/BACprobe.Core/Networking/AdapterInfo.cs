using System.Net;

namespace BACprobe.Core.Networking;

/// <summary>One IPv4 address on one network adapter.</summary>
public sealed record AdapterInfo(
    string Id,
    string Name,
    string Description,
    IPAddress Address,
    IPAddress Mask,
    bool IsUp,
    bool IsVirtual,
    bool IsLoopback)
{
    public IPAddress Broadcast => Subnet.BroadcastAddress(Address, Mask);
    public string Cidr => Subnet.Cidr(Address, Mask);
}
