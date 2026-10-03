using System.IO.BACnet;
using BACprobe.Core.Browsing;

namespace BACprobe.Core.Live;

/// <summary>Why a device said no to a COV subscription, in plain English, and whether asking again for other points is pointless.</summary>
/// <param name="StopTrying">True when the answer applies to the whole device (no COV at all, or out of slots), so the remaining points are not worth asking about.</param>
public sealed record CovRefusal(string Reason, bool StopTrying)
{
    /// <summary>Map the library's plain exceptions to a short reason. Unrecognised errors keep their text so nothing is hidden.</summary>
    public static CovRefusal Explain(Exception ex)
    {
        var m = ex.Message;
        bool Has(string s) => m.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (ex is TimeoutException || Has("timeout"))
            return new("the device did not answer the subscription", StopTrying: false);
        if (Has("SERVICE_REQUEST_DENIED") || Has("OPTIONAL_FUNCTIONALITY_NOT_SUPPORTED") || Has("REJECT") || Has("UNRECOGNIZED_SERVICE"))
            return new("the device does not support COV", StopTrying: true);
        if (Has("NO_SPACE_TO_ADD_LIST_ELEMENT") || Has("COV_SUBSCRIPTION_FAILED") || Has("OUT_OF_MEMORY"))
            return new("the device has run out of COV subscription slots", StopTrying: true);
        if (Has("NOT_COV_PROPERTY") || Has("UNSUPPORTED_OBJECT_TYPE"))
            return new("this kind of point does not report COV", StopTrying: false);
        if (Has("UNKNOWN_OBJECT"))
            return new("the device says the point does not exist", StopTrying: false);
        return new($"the device refused ({m})", StopTrying: false);
    }
}

public static class CovNotification
{
    /// <summary>The Present Value carried in a COV notification, formatted like everywhere else; null if the notification has none.</summary>
    public static string? PresentValueText(BacnetObjectTypes type, IEnumerable<BacnetPropertyValue> values)
    {
        foreach (var v in values)
        {
            if (v.property.propertyIdentifier != (uint)BacnetPropertyIds.PROP_PRESENT_VALUE) continue;
            if (v.value is null || v.value.Count == 0) return null;
            if (v.value[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR) return null;
            return BacnetNames.FormatValues(type, BacnetPropertyIds.PROP_PRESENT_VALUE, v.value);
        }
        return null;
    }
}
