using System.Globalization;
using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Writing;

/// <summary>
/// Which properties can be changed from the properties panel, and turning what the tech typed into the property's own BACnet type.
/// Present Value and Out Of Service have their own controls (with priorities and tracking), so they are not edited here. Pure.
/// </summary>
public static class PropertyEdit
{
    /// <summary>Properties no device lets you write, or that are only reports of the device's state.</summary>
    private static readonly HashSet<BacnetPropertyIds> NeverWritable =
    [
        BacnetPropertyIds.PROP_OBJECT_IDENTIFIER, BacnetPropertyIds.PROP_OBJECT_TYPE, BacnetPropertyIds.PROP_STATUS_FLAGS,
        BacnetPropertyIds.PROP_EVENT_STATE, BacnetPropertyIds.PROP_ACKED_TRANSITIONS, BacnetPropertyIds.PROP_EVENT_TIME_STAMPS,
        BacnetPropertyIds.PROP_PROPERTY_LIST, BacnetPropertyIds.PROP_OBJECT_LIST, BacnetPropertyIds.PROP_PRIORITY_ARRAY,
        BacnetPropertyIds.PROP_PROTOCOL_VERSION, BacnetPropertyIds.PROP_PROTOCOL_REVISION, BacnetPropertyIds.PROP_PROTOCOL_SERVICES_SUPPORTED,
        BacnetPropertyIds.PROP_PROTOCOL_OBJECT_TYPES_SUPPORTED, BacnetPropertyIds.PROP_VENDOR_IDENTIFIER, BacnetPropertyIds.PROP_VENDOR_NAME,
        BacnetPropertyIds.PROP_MODEL_NAME, BacnetPropertyIds.PROP_FIRMWARE_REVISION, BacnetPropertyIds.PROP_APPLICATION_SOFTWARE_VERSION,
        BacnetPropertyIds.PROP_SYSTEM_STATUS, BacnetPropertyIds.PROP_LOCAL_DATE, BacnetPropertyIds.PROP_LOCAL_TIME,
        BacnetPropertyIds.PROP_MAX_APDU_LENGTH_ACCEPTED, BacnetPropertyIds.PROP_SEGMENTATION_SUPPORTED, BacnetPropertyIds.PROP_DATABASE_REVISION,
        BacnetPropertyIds.PROP_LOG_BUFFER, BacnetPropertyIds.PROP_RECORD_COUNT, BacnetPropertyIds.PROP_TOTAL_RECORD_COUNT,
        BacnetPropertyIds.PROP_STRUCTURED_OBJECT_LIST, BacnetPropertyIds.PROP_SUBORDINATE_LIST,
    ];

    /// <summary>Have their own controls: overriding with a priority, and Out of service with tracking.</summary>
    private static readonly HashSet<BacnetPropertyIds> OwnControl =
        [BacnetPropertyIds.PROP_PRESENT_VALUE, BacnetPropertyIds.PROP_OUT_OF_SERVICE];

    private static readonly HashSet<BacnetApplicationTags> EditableTags =
    [
        BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE,
        BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, BacnetApplicationTags.BACNET_APPLICATION_TAG_SIGNED_INT,
        BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING,
        BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED,
    ];

    /// <summary>Whether the panel should offer to change this property, and if not, why (shown as a hint).</summary>
    public static bool CanEdit(PropertyRow row, out string reason)
    {
        var p = (BacnetPropertyIds)row.PropertyId;
        reason = "";
        if (OwnControl.Contains(p))
        {
            reason = p == BacnetPropertyIds.PROP_PRESENT_VALUE
                ? "Change the value with Override this point (it needs a priority, and can be released)."
                : "Use the Out of service button above.";
            return false;
        }
        if (NeverWritable.Contains(p) || row.IsError)
        {
            reason = "This property is read-only.";
            return false;
        }
        if (row.IsVendorSpecific)
        {
            reason = "Vendor-specific properties are shown raw and cannot be changed here.";
            return false;
        }
        if (row.ValueCount != 1 || row.ValueTag is not { } tag || !EditableTags.Contains(tag))
        {
            reason = "BACprobe cannot edit this kind of value yet (lists, arrays, dates and other structured values).";
            return false;
        }
        return true;
    }

    /// <summary>What to type, for the hint under the box: "a number such as 72.5", "text", "true or false"…</summary>
    public static string KindHint(BacnetApplicationTags tag) => tag switch
    {
        BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL or BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE => "a number, such as 72.5",
        BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT => "a whole number, 0 or more",
        BacnetApplicationTags.BACNET_APPLICATION_TAG_SIGNED_INT => "a whole number",
        BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN => "true or false",
        BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED => "the number of the choice (for example 64 for degrees Fahrenheit)",
        _ => "text",
    };

    /// <summary>Turn the typed text into a value of the same BACnet type as the property's current value.</summary>
    public static bool TryParse(BacnetApplicationTags tag, string text, out BacnetValue value, out string error)
    {
        value = default;
        error = "";
        var t = tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING ? text : text.Trim();
        var inv = CultureInfo.InvariantCulture;
        switch (tag)
        {
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL:
                if (float.TryParse(t, NumberStyles.Float, inv, out var f) && float.IsFinite(f)) { value = new(tag, f); return true; }
                break;
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE:
                if (double.TryParse(t, NumberStyles.Float, inv, out var d) && double.IsFinite(d)) { value = new(tag, d); return true; }
                break;
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT:
                if (uint.TryParse(t, NumberStyles.None, inv, out var u)) { value = new(tag, u); return true; }
                break;
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_SIGNED_INT:
                if (int.TryParse(t, NumberStyles.AllowLeadingSign, inv, out var i)) { value = new(tag, i); return true; }
                break;
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN:
                var b = t.ToLowerInvariant() switch { "true" or "yes" or "on" or "1" => true, "false" or "no" or "off" or "0" => (bool?)false, _ => null };
                if (b is { } yes) { value = new(tag, yes); return true; }
                break;
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED:
                // Display shows enumerations as "name (n)": accept the bare number, or that whole text.
                var number = t.EndsWith(')') && t.LastIndexOf('(') is var open and >= 0 ? t[(open + 1)..^1] : t;
                if (uint.TryParse(number.Trim(), NumberStyles.None, inv, out var e)) { value = new(tag, e); return true; }
                break;
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING:
                if (t.Length > 255) { error = "That is too long: devices take at most a few hundred characters, often far fewer."; return false; }
                value = new(tag, t);
                return true;
            default:
                error = "BACprobe cannot edit this kind of value yet.";
                return false;
        }
        error = $"'{t}' is not {KindHint(tag)}.";
        return false;
    }
}

/// <summary>Changing one configuration property of a point (a limit, a description, a COV increment…). Not an override: nothing to release.</summary>
public sealed record PropertyWriteRequest(
    DiscoveredDevice Device,
    string DeviceName,
    BacnetObjectId Point,
    string ObjectName,
    BacnetPropertyIds Property,
    BacnetValue Value,
    string ValueText,
    string CurrentText)
{
    public string PropertyName => BacnetNames.PropertyName((uint)Property);
    public string ObjectLabel => $"{ObjectName} ({BacnetNames.ObjectLabel(Point)})";
    public string Headline => $"Change {ObjectName}'s {PropertyName} to {ValueText}?";

    public IReadOnlyList<ConfirmFact> Facts =>
    [
        new("Device", $"{DeviceName} (device {Device.InstanceId})"),
        new("Point", ObjectLabel),
        new("Property", PropertyName),
        new("Now", CurrentText.Length == 0 ? "(empty)" : CurrentText),
        new("New value", ValueText.Length == 0 ? "(empty)" : ValueText),
    ];

    public string ConfirmLabel => $"Change {PropertyName}";

    public string Consequence =>
        $"This changes how {ObjectName} is set up in the controller. It is not an override: there is nothing to release later, and it stays " +
        "after a restart. The old value is kept in the write log in case you need to put it back.";

    /// <summary>Limits, names and alarm settings are often used by other programs or the front-end.</summary>
    public string? Warning => Property == BacnetPropertyIds.PROP_OBJECT_NAME
        ? "Front-ends and other controllers may find this point by its name. Renaming it can break their links."
        : null;

    /// <summary>The write log's action: "set High Limit: 75 -> 80".</summary>
    public string LogAction => $"set {PropertyName}: {(CurrentText.Length == 0 ? "(empty)" : CurrentText)} -> {(ValueText.Length == 0 ? "(empty)" : ValueText)}";
}
