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

    /// <summary>Source address as shown to the user (IP:port, or network:MAC when routed).</summary>
    public string AddressText => Address.ToString();
}
