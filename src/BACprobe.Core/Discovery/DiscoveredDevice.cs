using System.IO.BACnet;

namespace BACprobe.Core.Discovery;

public sealed class DiscoveredDevice
{
    public required uint InstanceId { get; init; }
    public required BacnetAddress Address { get; init; }
    public required uint MaxApdu { get; init; }
    public required BacnetSegmentations Segmentation { get; init; }
    public required ushort VendorId { get; init; }

    public string? ObjectName { get; set; }
    public string? VendorName { get; set; }
    public string? ModelName { get; set; }
    public string? FirmwareRevision { get; set; }
    /// <summary>Set when enrichment failed entirely; text is meant for the user.</summary>
    public string? EnrichError { get; set; }

    /// <summary>Address as shown to the user: "ip:port", or "network 1001, MAC 5 (via ip:port)" for a device behind a router.</summary>
    public string AddressText => AddressInfo.Describe(Address);

    /// <summary>The BACnet network the device is on: 0 for this network, otherwise the number behind a router.</summary>
    public ushort Network => AddressInfo.NetworkOf(Address);
}
