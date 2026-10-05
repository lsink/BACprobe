using System.Text.RegularExpressions;

namespace BACprobe.Core.Browsing;

/// <summary>Text a tech pastes into a ticket, a chat or Wireshark. Pure, so it can be tested.</summary>
public static partial class QuickCopy
{
    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")]
    private static partial Regex Ipv4();

    /// <summary>
    /// A Wireshark display filter for traffic with this device. The first IPv4 address in the text is used, which for a
    /// device behind a router is the router's (that is where the packets go). Null when the text holds no IPv4 address.
    /// </summary>
    public static string? WiresharkFilterForAddress(string addressText)
    {
        var m = Ipv4().Match(addressText);
        return m.Success ? $"bacnet && ip.addr == {m.Value}" : null;
    }

    /// <summary>The same filter narrowed to one object: its type number and instance.</summary>
    public static string? WiresharkFilterForObject(string addressText, uint objectType, uint instance) =>
        WiresharkFilterForAddress(addressText) is { } device
            ? $"{device} && bacapp.objectType == {objectType} && bacapp.instance_number == {instance}"
            : null;
}
