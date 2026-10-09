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

    /// <summary>Its name, or "device 1001" when it has none: for sentences.</summary>
    public string DisplayName => ObjectName ?? $"device {InstanceId}";

    /// <summary>Its name, or "Device 1001" when it has none: as a device's name in exports and job files.</summary>
    public string ExportName => ObjectName ?? $"Device {InstanceId}";
    public string? VendorName { get; set; }
    public string? ModelName { get; set; }
    public string? FirmwareRevision { get; set; }
    /// <summary>How long the first read of this device took (includes any retry waits). Null if it never answered.</summary>
    public TimeSpan? ResponseTime { get; set; }

    /// <summary>The device's clock minus this PC's clock at the moment it was read; null when the device has no readable clock.</summary>
    public TimeSpan? ClockSkew { get; set; }

    /// <summary>Set when enrichment failed entirely; text is meant for the user.</summary>
    public string? EnrichError { get; set; }

    /// <summary>Address as shown to the user: "ip:port", or "network 1001, MAC 5 (via ip:port)" for a device behind a router.</summary>
    public string AddressText => AddressInfo.Describe(Address);

    /// <summary>The BACnet network the device is on: 0 for this network, otherwise the number behind a router.</summary>
    public ushort Network => AddressInfo.NetworkOf(Address);
}
