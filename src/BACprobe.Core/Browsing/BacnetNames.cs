using System.Globalization;
using System.IO.BACnet;

namespace BACprobe.Core.Browsing;

/// <summary>Pure helpers that turn BACnet enums and values into plain-English text.</summary>
public static class BacnetNames
{
    private const int ProprietaryObjectTypeMin = 128;
    private const uint ProprietaryPropertyMin = 512;

    private static readonly Dictionary<string, BacnetObjectTypes> ObjectAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ai"] = BacnetObjectTypes.OBJECT_ANALOG_INPUT, ["ao"] = BacnetObjectTypes.OBJECT_ANALOG_OUTPUT,
        ["av"] = BacnetObjectTypes.OBJECT_ANALOG_VALUE, ["bi"] = BacnetObjectTypes.OBJECT_BINARY_INPUT,
        ["bo"] = BacnetObjectTypes.OBJECT_BINARY_OUTPUT, ["bv"] = BacnetObjectTypes.OBJECT_BINARY_VALUE,
        ["msi"] = BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT, ["mso"] = BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT,
        ["msv"] = BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE, ["dev"] = BacnetObjectTypes.OBJECT_DEVICE,
        ["sch"] = BacnetObjectTypes.OBJECT_SCHEDULE, ["cal"] = BacnetObjectTypes.OBJECT_CALENDAR,
        ["tl"] = BacnetObjectTypes.OBJECT_TRENDLOG, ["nc"] = BacnetObjectTypes.OBJECT_NOTIFICATION_CLASS,
        ["loop"] = BacnetObjectTypes.OBJECT_LOOP, ["file"] = BacnetObjectTypes.OBJECT_FILE,
    };

    private static readonly Dictionary<BacnetUnitsId, string> UnitSymbols = new()
    {
        [BacnetUnitsId.UNITS_DEGREES_FAHRENHEIT] = "°F", [BacnetUnitsId.UNITS_DEGREES_CELSIUS] = "°C",
        [BacnetUnitsId.UNITS_PERCENT] = "%", [BacnetUnitsId.UNITS_PERCENT_RELATIVE_HUMIDITY] = "%RH",
        [BacnetUnitsId.UNITS_PASCALS] = "Pa", [BacnetUnitsId.UNITS_KILOPASCALS] = "kPa",
        [BacnetUnitsId.UNITS_INCHES_OF_WATER] = "inH2O", [BacnetUnitsId.UNITS_POUNDS_FORCE_PER_SQUARE_INCH] = "psi",
        [BacnetUnitsId.UNITS_CUBIC_FEET_PER_MINUTE] = "cfm", [BacnetUnitsId.UNITS_LITERS_PER_SECOND] = "L/s",
        [BacnetUnitsId.UNITS_VOLTS] = "V", [BacnetUnitsId.UNITS_AMPERES] = "A", [BacnetUnitsId.UNITS_WATTS] = "W",
        [BacnetUnitsId.UNITS_KILOWATTS] = "kW", [BacnetUnitsId.UNITS_KILOWATT_HOURS] = "kWh",
        [BacnetUnitsId.UNITS_HERTZ] = "Hz", [BacnetUnitsId.UNITS_PARTS_PER_MILLION] = "ppm",
        [BacnetUnitsId.UNITS_REVOLUTIONS_PER_MINUTE] = "rpm", [BacnetUnitsId.UNITS_DEGREES_ANGULAR] = "°",
        [BacnetUnitsId.UNITS_NO_UNITS] = "",
    };

    private static readonly string[] PriorityNames =
    [
        "Manual Life Safety", "Automatic Life Safety", "Available", "Available", "Critical Equipment Control",
        "Minimum On/Off", "Available", "Manual Operator", "Available", "Available", "Available", "Available",
        "Available", "Available", "Available", "Default (lowest)",
    ];

    /// <summary>Plain-English name for BACnet priority 1-16, e.g. 8 = "Manual Operator".</summary>
    public static string PriorityName(int priority) =>
        priority is >= 1 and <= 16 ? PriorityNames[priority - 1] : $"priority {priority}";

    public static string ObjectTypeName(BacnetObjectTypes type)
    {
        if ((int)type >= ProprietaryObjectTypeMin) return $"Vendor-specific type {(int)type}";
        return Enum.IsDefined(type) ? Prettify(type.ToString(), "OBJECT_") : $"Unknown type {(int)type}";
    }

    public static string ObjectTypeShort(BacnetObjectTypes type) =>
        ObjectAliases.FirstOrDefault(kv => kv.Value == type).Key?.ToUpperInvariant() ?? ObjectTypeName(type);

    /// <summary>"Analog Input 3", or "AI3" style short form via <see cref="ObjectTypeShort"/>.</summary>
    public static string ObjectLabel(BacnetObjectId id) => $"{ObjectTypeName(id.type)} {id.instance}";

    /// <summary>Accepts "ai:1", "AV:12", or "analog-input:1".</summary>
    public static bool TryParseObject(string spec, out BacnetObjectId id)
    {
        id = default;
        var parts = spec.Split(':', 2);
        if (parts.Length != 2 || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var instance)
            || instance > 4194303) return false;

        var key = parts[0].Trim();
        if (ObjectAliases.TryGetValue(key, out var type))
        {
            id = new BacnetObjectId(type, instance);
            return true;
        }
        var wanted = "OBJECT_" + key.Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
        if (Enum.TryParse<BacnetObjectTypes>(wanted, out var named) && Enum.IsDefined(named) && (int)named < ProprietaryObjectTypeMin)
        {
            id = new BacnetObjectId(named, instance);
            return true;
        }
        return false;
    }

    public static string PropertyName(uint propertyId)
    {
        if (propertyId >= ProprietaryPropertyMin) return $"vendor-specific ({propertyId})";
        var p = (BacnetPropertyIds)propertyId;
        return Enum.IsDefined(p) ? Prettify(p.ToString(), "PROP_") : $"unknown ({propertyId})";
    }

    /// <summary>Accepts "present-value", "Present Value", "PROP_PRESENT_VALUE" or a plain number.</summary>
    public static bool TryParseProperty(string text, out BacnetPropertyIds property)
    {
        property = default;
        var t = text.Trim();
        if (uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            property = (BacnetPropertyIds)n;
            return true;
        }
        var key = t.Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
        if (!key.StartsWith("PROP_", StringComparison.Ordinal)) key = "PROP_" + key;
        return Enum.TryParse(key, out property) && Enum.IsDefined(property);
    }

    /// <summary>Object types whose Present Value is worth refreshing live (not devices, files, calendars and the like).</summary>
    public static bool HasLivePresentValue(BacnetObjectTypes type) => type is
        BacnetObjectTypes.OBJECT_ANALOG_INPUT or BacnetObjectTypes.OBJECT_ANALOG_OUTPUT or BacnetObjectTypes.OBJECT_ANALOG_VALUE or
        BacnetObjectTypes.OBJECT_BINARY_INPUT or BacnetObjectTypes.OBJECT_BINARY_OUTPUT or BacnetObjectTypes.OBJECT_BINARY_VALUE or
        BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT or BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT or BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE or
        BacnetObjectTypes.OBJECT_INTEGER_VALUE or BacnetObjectTypes.OBJECT_POSITIVE_INTEGER_VALUE or
        BacnetObjectTypes.OBJECT_LARGE_ANALOG_VALUE or BacnetObjectTypes.OBJECT_ACCUMULATOR;

    public static bool IsVendorProperty(uint propertyId) => propertyId >= ProprietaryPropertyMin;

    public static string UnitsName(uint units)
    {
        var u = (BacnetUnitsId)units;
        if (UnitSymbols.TryGetValue(u, out var s)) return s;
        return Enum.IsDefined(u) ? Prettify(u.ToString(), "UNITS_").ToLowerInvariant() : $"units {units}";
    }

    /// <summary>Formats one value for display. Errors, binary states and units get friendly text.</summary>
    public static string FormatValue(BacnetObjectTypes objectType, BacnetPropertyIds property, BacnetValue v)
    {
        switch (v.Tag)
        {
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR when v.Value is BacnetError e:
                return $"(error: {Prettify(e.error_code.ToString(), "ERROR_CODE_").ToLowerInvariant()})";
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL:
                return "(null)";
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL:
                return Convert.ToSingle(v.Value, CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture);
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE:
                return Convert.ToDouble(v.Value, CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture);
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN:
                return v.Value is true ? "true" : "false";
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID when v.Value is BacnetObjectId oid:
                return ObjectLabel(oid);
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED:
                var n = Convert.ToUInt32(v.Value, CultureInfo.InvariantCulture);
                if (property == BacnetPropertyIds.PROP_UNITS) return UnitsName(n);
                if (property == BacnetPropertyIds.PROP_OBJECT_TYPE) return ObjectTypeName((BacnetObjectTypes)n);
                if (property is BacnetPropertyIds.PROP_PRESENT_VALUE or BacnetPropertyIds.PROP_RELINQUISH_DEFAULT
                    && objectType is BacnetObjectTypes.OBJECT_BINARY_INPUT or BacnetObjectTypes.OBJECT_BINARY_OUTPUT
                        or BacnetObjectTypes.OBJECT_BINARY_VALUE)
                    return n == 0 ? "Inactive" : "Active";
                return n.ToString(CultureInfo.InvariantCulture);
            default:
                return Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? "";
        }
    }

    /// <summary>Formats a property's whole value list. Priority arrays show only the occupied slots.</summary>
    public static string FormatValues(BacnetObjectTypes objectType, BacnetPropertyIds property, IList<BacnetValue>? values)
    {
        if (values is null || values.Count == 0) return "";
        if (property == BacnetPropertyIds.PROP_PRIORITY_ARRAY)
        {
            var slots = values.Select((v, i) => (v, p: i + 1))
                .Where(x => x.v.Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL)
                .Select(x => $"{x.p} ({PriorityName(x.p)}) = {FormatValue(objectType, BacnetPropertyIds.PROP_PRESENT_VALUE, x.v)}")
                .ToList();
            return slots.Count == 0 ? "no overrides (all 16 slots empty)" : string.Join("; ", slots);
        }
        if (property == BacnetPropertyIds.PROP_OBJECT_LIST)
            return $"{values.Count} objects";
        return values.Count == 1
            ? FormatValue(objectType, property, values[0])
            : string.Join(", ", values.Select(v => FormatValue(objectType, property, v)));
    }

    private static string Prettify(string enumName, string prefix)
    {
        var s = enumName.StartsWith(prefix, StringComparison.Ordinal) ? enumName[prefix.Length..] : enumName;
        var words = s.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
        return string.Join(' ', words);
    }
}
