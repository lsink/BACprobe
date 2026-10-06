using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Writing;

namespace BACprobe.Core.Alarms;

/// <summary>
/// Adding BACprobe to (or taking it off) the alarm recipient lists of a device's Notification Class objects, so the device sends its
/// alarms here as they happen. It changes the device's setup, so it is confirmed, logged, and undone before leaving.
/// </summary>
public sealed record AlarmListenRequest(DiscoveredDevice Device, string DeviceName, IReadOnlyList<BacnetObjectId> NotificationClasses,
    AlarmRecipient Me, bool Stop = false)
{
    public string ClassesText => string.Join(", ", NotificationClasses.Select(BacnetNames.ObjectLabel));

    public string Headline => Stop ? $"Stop getting {DeviceName}'s alarms live?" : $"Get {DeviceName}'s alarms live?";

    public IReadOnlyList<ConfirmFact> Facts =>
    [
        new("Device", $"{DeviceName} (device {Device.InstanceId})"),
        new("Alarm classes", ClassesText),
        new(Stop ? "Remove" : "Add", $"BACprobe at {Me.AddressText} (process {Me.ProcessId})"),
    ];

    public string ConfirmLabel => Stop ? "Remove BACprobe" : "Add BACprobe";

    public string Consequence => Stop
        ? "BACprobe takes itself off these recipient lists; the device's other recipients are not touched."
        : "BACprobe adds itself to these notification classes' recipient lists, so the device sends its alarms here the moment they happen. " +
          "This changes the device's setup: BACprobe takes itself off again when you stop or leave (it will ask). The front-end and other " +
          "recipients are not touched.";

    public string? Warning => Stop ? null
        : "Some controllers only have room for a few recipients. If BACprobe closes unexpectedly, its entry stays: remove it in the controller's tool " +
          $"(it is the one at {Me.AddressText}, process {Me.ProcessId}).";

    public string LogAction(BacnetObjectId nc) =>
        $"{(Stop ? "remove BACprobe from" : "add BACprobe to")} {BacnetNames.ObjectLabel(nc)} recipient list ({Me.AddressText})";
}

/// <summary>
/// An alarm notification as it arrived: which device and point, and its new state. From/To are null when the notification used an event
/// type the library cannot fully read (vendor or newer types): the device and point are still known, so its alarms are read again.
/// </summary>
public sealed record AlarmNotification(uint DeviceInstance, BacnetObjectId Point, BacnetEventStates? From, BacnetEventStates? To, string? Message,
    DateTime ReceivedAt)
{
    public string Text => $"{ReceivedAt:HH:mm:ss}  device {DeviceInstance} {BacnetNames.ObjectLabel(Point)}: " +
                          (From is { } f && To is { } t ? $"{EventText.StateName(f)} -> {EventText.StateName(t)}" : "alarm changed") +
                          (string.IsNullOrWhiteSpace(Message) ? "" : $" ({Message})");

    /// <summary>
    /// Just the start of an event notification (process id [0], initiating device [1], event object [2]): enough to know whose alarms to
    /// read again when the rest is in a form the library cannot decode.
    /// </summary>
    public static AlarmNotification? FromHeader(byte[] buffer, int offset, int length, DateTime receivedAt)
    {
        try
        {
            var end = offset + length;
            var pos = offset;
            if (!System.IO.BACnet.Serialize.ASN1.decode_is_context_tag(buffer, pos, 0)) return null;
            pos += System.IO.BACnet.Serialize.ASN1.decode_tag_number_and_value(buffer, pos, out byte _, out uint len) + (int)len;
            if (pos >= end || !System.IO.BACnet.Serialize.ASN1.decode_is_context_tag(buffer, pos, 1)) return null;
            pos += System.IO.BACnet.Serialize.ASN1.decode_context_object_id(buffer, pos, 1, out ushort _, out uint device);
            if (pos >= end || !System.IO.BACnet.Serialize.ASN1.decode_is_context_tag(buffer, pos, 2)) return null;
            System.IO.BACnet.Serialize.ASN1.decode_context_object_id(buffer, pos, 2, out ushort type, out uint instance);
            return new AlarmNotification(device, new BacnetObjectId((BacnetObjectTypes)type, instance), null, null, null, receivedAt);
        }
        catch (IndexOutOfRangeException) { return null; }
    }
}
