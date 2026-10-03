using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Export;
using BACprobe.Core.Writing;

namespace BACprobe.Core.Jobs;

/// <summary>Job-level details. Everything here is typed by the user or taken from this PC, never from a device.</summary>
public sealed record JobInfo(
    string Name,
    string Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset SavedAt,
    string? BbmdText = null,
    string? AdapterCidr = null,
    string AppVersion = "");

public sealed record SavedObject(BacnetObjectTypes Type, uint Instance, string? Name, string? Description,
    string? PresentValue, string? Units, uint? UnitsCode, IReadOnlyList<PrioritySlot>? Slots = null,
    IReadOnlyList<string?>? StateNames = null, BacnetStatusFlags? StatusFlags = null, uint? Reliability = null)
{
    public ObjectSummary ToSummary() => new()
    {
        Id = new BacnetObjectId(Type, Instance), Name = Name, Description = Description,
        PresentValue = PresentValue, Units = Units, UnitsCode = UnitsCode, PrioritySlots = Slots ?? [], StateNames = StateNames,
        StatusFlags = StatusFlags, Reliability = Reliability,
    };

    public static SavedObject From(ObjectSummary s) =>
        new(s.Id.type, s.Id.instance, s.Name, s.Description, s.PresentValue, s.Units, s.UnitsCode, s.PrioritySlots, s.StateNames,
            s.StatusFlags, s.Reliability);
}

/// <summary>A device as saved: enough to show it offline and, if it is still where it was, to talk to it again.</summary>
public sealed record SavedDevice(
    uint Instance,
    string Name,
    string AddressText,
    BacnetAddressTypes AddressType,
    ushort Network,
    byte[] AddressBytes,
    ushort VendorId,
    string? VendorName,
    string? ModelName,
    string? Firmware,
    uint MaxApdu,
    BacnetSegmentations Segmentation,
    bool PointsRead,
    IReadOnlyList<SavedObject> Objects)
{
    /// <summary>Snapshot a live device. Pass null objects if its points have not been read.</summary>
    public static SavedDevice From(DiscoveredDevice d, string name, IReadOnlyList<ObjectSummary>? objects) => new(
        d.InstanceId, name, d.AddressText, d.Address.type, d.Address.net, d.Address.adr ?? [],
        d.VendorId, d.VendorName, d.ModelName, d.FirmwareRevision, d.MaxApdu, d.Segmentation,
        objects is not null, objects?.Select(SavedObject.From).ToList() ?? []);

    public DiscoveredDevice ToDiscovered() => new()
    {
        InstanceId = Instance,
        Address = new BacnetAddress(AddressType, Network, AddressBytes),
        MaxApdu = MaxApdu, Segmentation = Segmentation, VendorId = VendorId,
        ObjectName = Name, VendorName = VendorName, ModelName = ModelName, FirmwareRevision = Firmware,
    };

    public ExportDevice ToExportDevice() => new(ToDiscovered(), Name, [.. Objects.Select(o => o.ToSummary())]);
}

/// <summary>Everything in a job file.</summary>
public sealed record JobSnapshot(JobInfo Info, IReadOnlyList<SavedDevice> Devices, IReadOnlyList<WriteLogEntry> WriteLog);

/// <summary>A job file that cannot be used. The message already says what is likely wrong and what to do.</summary>
public sealed class JobFileException(string message, Exception? inner = null) : Exception(message, inner);
