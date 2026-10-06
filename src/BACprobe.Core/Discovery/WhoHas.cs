using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using BACprobe.Core.Browsing;

namespace BACprobe.Core.Discovery;

/// <summary>One answer to Who-Has: this device has that object.</summary>
public sealed record IHaveReply(uint DeviceInstance, BacnetObjectId Point, string ObjectName, string AddressText)
{
    public string ObjectLabel => BacnetNames.ObjectLabel(Point);
}

/// <summary>
/// Decodes I-Have (the library sends it but has no decoder for it): device identifier, object identifier and object name, each
/// application-tagged. Pure, so it is tested against the library's own encoder.
/// </summary>
public static class IHaveCodec
{
    public static bool TryDecode(byte[] buffer, int offset, int length, out uint device, out BacnetObjectId obj, out string name)
    {
        device = 0;
        obj = default;
        name = "";
        try
        {
            var end = offset + length;
            var pos = offset;
            if (!ReadObjectId(buffer, ref pos, end, out var dev) || dev.type != BacnetObjectTypes.OBJECT_DEVICE) return false;
            if (!ReadObjectId(buffer, ref pos, end, out obj)) return false;
            pos += ASN1.decode_tag_number_and_value(buffer, pos, out byte tag, out uint len);
            if (tag != (byte)BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING || pos + len > end) return false;
            ASN1.decode_character_string(buffer, pos, end, len, out name);
            device = dev.instance;
            return true;
        }
        catch (IndexOutOfRangeException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool ReadObjectId(byte[] buffer, ref int pos, int end, out BacnetObjectId id)
    {
        id = default;
        if (pos >= end) return false;
        pos += ASN1.decode_tag_number_and_value(buffer, pos, out byte tag, out uint len);
        if (tag != (byte)BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID || len != 4 || pos + 4 > end) return false;
        pos += ASN1.decode_object_id(buffer, pos, out ushort type, out uint instance);
        id = new BacnetObjectId((BacnetObjectTypes)type, instance);
        return true;
    }

    /// <summary>"AI 1", "ai:1", "analog-input:1" → an object to look for; anything else is a name.</summary>
    public static (BacnetObjectId? Id, string? Name) ParseQuery(string text)
    {
        var t = text.Trim();
        return BacnetNames.TryParseObject(t.Replace(' ', ':'), out var id) ? (id, null) : (null, t);
    }
}
