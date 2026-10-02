using System.IO.BACnet;

namespace BACprobe.Core.Browsing;

/// <summary>One occupied slot of a priority array, e.g. priority 8 holding 25.</summary>
public sealed record PrioritySlot(int Priority, string ValueText)
{
    public string Description => $"{Priority} ({BacnetNames.PriorityName(Priority)}) = {ValueText}";
}

public static class PriorityArrayInfo
{
    /// <summary>
    /// Slots at this priority or higher (numerically lower) count as an override: life safety, critical equipment,
    /// minimum on/off and manual operator. Programs normally write at 9 to 16, so those do not raise the flag.
    /// </summary>
    public const int OverrideCeiling = 8;

    /// <summary>Object types that normally have a priority array. Others are never asked for it.</summary>
    public static bool MayHavePriorityArray(BacnetObjectTypes type) => type is
        BacnetObjectTypes.OBJECT_ANALOG_OUTPUT or BacnetObjectTypes.OBJECT_ANALOG_VALUE or
        BacnetObjectTypes.OBJECT_BINARY_OUTPUT or BacnetObjectTypes.OBJECT_BINARY_VALUE or
        BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT or BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE;

    /// <summary>The slots that hold a value. Empty slots (null) are skipped; position in the list is the priority.</summary>
    public static IReadOnlyList<PrioritySlot> Occupied(BacnetObjectTypes type, IList<BacnetValue>? values)
    {
        if (values is null) return [];
        var slots = new List<PrioritySlot>();
        for (var i = 0; i < values.Count && i < 16; i++)
        {
            if (values[i].Tag is BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL or BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR)
                continue;
            slots.Add(new PrioritySlot(i + 1, BacnetNames.FormatValue(type, BacnetPropertyIds.PROP_PRESENT_VALUE, values[i])));
        }
        return slots;
    }

    public static bool IsOverride(IReadOnlyList<PrioritySlot> slots) => slots.Any(s => s.Priority <= OverrideCeiling);
}
