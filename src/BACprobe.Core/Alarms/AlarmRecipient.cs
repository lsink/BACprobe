using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using System.Net;

namespace BACprobe.Core.Alarms;

/// <summary>
/// One entry of a Notification Class's Recipient_List (a BACnetDestination): who gets the alarms, on which days and hours, and for which
/// transitions. BACprobe adds itself by address (it has no device object), every day, all day, all transitions, unconfirmed.
/// </summary>
/// <param name="Network">0 = the device's own network.</param>
/// <param name="Mac">The recipient's address on that network: for BACnet/IP, 4 bytes of IP then 2 bytes of port.</param>
/// <param name="DeviceInstance">Set instead of an address when the recipient is named by its device object.</param>
public sealed record AlarmRecipient(ushort Network, byte[] Mac, uint ProcessId, bool Confirmed, uint? DeviceInstance = null)
{
    /// <summary>The process identifier BACprobe uses in its recipient entries ("BP" in hex, then 1), so they can be told apart from others.</summary>
    public const uint BacprobeProcessId = 0x4250_0001;

    /// <summary>BACprobe's entry for a BACnet/IP endpoint: notifications go to exactly the socket it sends from.</summary>
    public static AlarmRecipient ForEndPoint(IPEndPoint endPoint) =>
        new(0, [.. endPoint.Address.MapToIPv4().GetAddressBytes(), (byte)(endPoint.Port >> 8), (byte)endPoint.Port], BacprobeProcessId, Confirmed: false);

    /// <summary>"172.27.32.1:55368" for a BACnet/IP address; hex otherwise; "device 1001" for a device recipient.</summary>
    public string AddressText => DeviceInstance is { } d ? $"device {d}"
        : Mac.Length == 6 ? $"{new IPAddress(Mac[..4])}:{(Mac[4] << 8) | Mac[5]}" + (Network == 0 ? "" : $" on network {Network}")
        : $"{Convert.ToHexString(Mac)}{(Network == 0 ? "" : $" on network {Network}")}";

    /// <summary>The IP endpoint, for a BACnet/IP address on the local network; null otherwise.</summary>
    public IPEndPoint? EndPoint => DeviceInstance is null && Network == 0 && Mac.Length == 6
        ? new IPEndPoint(new IPAddress(Mac[..4]), (Mac[4] << 8) | Mac[5])
        : null;

    public bool SameAs(AlarmRecipient other) =>
        Network == other.Network && Mac.AsSpan().SequenceEqual(other.Mac) && ProcessId == other.ProcessId && DeviceInstance == other.DeviceInstance;

    /// <summary>The BACnetDestination, encoded: valid days, from/to time, recipient, process id, confirmed, transitions.</summary>
    public byte[] Encode()
    {
        var b = new EncodeBuffer();
        ASN1.encode_application_bitstring(b, BacnetBitString.ConvertFromInt(0x7F, 7)); // every day of the week
        ASN1.encode_application_time(b, new BacnetTime(0, 0, 0, 0));
        ASN1.encode_application_time(b, new BacnetTime(23, 59, 59, 99));
        if (DeviceInstance is { } device)
        {
            ASN1.encode_context_object_id(b, 0, BacnetObjectTypes.OBJECT_DEVICE, device);
        }
        else
        {
            ASN1.encode_opening_tag(b, 1);
            ASN1.encode_application_unsigned(b, Network);
            ASN1.encode_application_octet_string(b, Mac, 0, Mac.Length);
            ASN1.encode_closing_tag(b, 1);
        }
        ASN1.encode_application_unsigned(b, ProcessId);
        ASN1.encode_application_boolean(b, Confirmed);
        ASN1.encode_application_bitstring(b, BacnetBitString.ConvertFromInt(0x07, 3)); // to-offnormal, to-fault, to-normal
        return b.buffer[..b.offset];
    }

    /// <summary>As a value for AddListElement / RemoveListElement: the encoded bytes go out exactly as they are.</summary>
    public BacnetValue ToValue() => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_CONTEXT_SPECIFIC_ENCODED, Encode());

    /// <summary>
    /// Read a list of BACnetDestinations (the inside of a Recipient_List, or of AddListElement's list). Stops at the first thing it does
    /// not understand and returns what it read so far.
    /// </summary>
    public static IReadOnlyList<AlarmRecipient> DecodeList(byte[] buffer, int offset, int end)
    {
        var list = new List<AlarmRecipient>();
        var pos = offset;
        try
        {
            while (pos < end && !ASN1.decode_is_closing_tag_number(buffer, pos, 3))
            {
                pos = Skip(buffer, pos); // valid days
                pos = Skip(buffer, pos); // from time
                pos = Skip(buffer, pos); // to time
                ushort network = 0;
                byte[] mac = [];
                uint? device = null;
                if (ASN1.decode_is_context_tag(buffer, pos, 0))
                {
                    pos += ASN1.decode_context_object_id(buffer, pos, 0, out ushort _, out uint instance);
                    device = instance;
                }
                else if (ASN1.decode_is_opening_tag_number(buffer, pos, 1))
                {
                    pos++;
                    pos += ASN1.decode_tag_number_and_value(buffer, pos, out byte _, out uint len);
                    pos += ASN1.decode_unsigned(buffer, pos, len, out var net);
                    network = (ushort)net;
                    pos += ASN1.decode_tag_number_and_value(buffer, pos, out byte _, out len);
                    mac = new byte[len];
                    pos += ASN1.decode_octet_string(buffer, pos, end, mac, 0, len);
                    if (!ASN1.decode_is_closing_tag_number(buffer, pos, 1)) return list;
                    pos++;
                }
                else return list;
                pos += ASN1.decode_tag_number_and_value(buffer, pos, out byte _, out uint plen);
                pos += ASN1.decode_unsigned(buffer, pos, plen, out var process);
                pos += ASN1.decode_tag_number_and_value(buffer, pos, out byte _, out uint confirmed); // a boolean's value is in its length
                pos = Skip(buffer, pos); // transitions
                list.Add(new AlarmRecipient(network, mac, process, confirmed != 0, device));
            }
        }
        catch (IndexOutOfRangeException) { }
        catch (ArgumentException) { }
        return list;
    }

    /// <summary>Step over one application-tagged value.</summary>
    private static int Skip(byte[] buffer, int pos)
    {
        var tagLen = ASN1.decode_tag_number_and_value(buffer, pos, out byte _, out uint len);
        return pos + tagLen + (int)len;
    }
}
