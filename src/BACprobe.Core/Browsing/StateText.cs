using System.Globalization;
using System.IO.BACnet;

namespace BACprobe.Core.Browsing;

/// <summary>
/// The names a device gives a point's states, so a tech sees "Occupied (2)" instead of "2". Multi-state points carry
/// State_Text (state 1 is the first name); binary points carry Inactive_Text and Active_Text, kept here as [inactive, active].
/// Missing names fall back to the raw value, which is always kept so nothing is hidden.
/// </summary>
public static class StateText
{
    public static bool IsMultiState(BacnetObjectTypes type) => type is
        BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT or BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT or BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE;

    public static bool IsBinary(BacnetObjectTypes type) => type is
        BacnetObjectTypes.OBJECT_BINARY_INPUT or BacnetObjectTypes.OBJECT_BINARY_OUTPUT or BacnetObjectTypes.OBJECT_BINARY_VALUE;

    /// <summary>The properties that hold this type's state names; empty for types without any.</summary>
    public static BacnetPropertyIds[] NameProperties(BacnetObjectTypes type) =>
        IsMultiState(type) ? [BacnetPropertyIds.PROP_STATE_TEXT]
        : IsBinary(type) ? [BacnetPropertyIds.PROP_INACTIVE_TEXT, BacnetPropertyIds.PROP_ACTIVE_TEXT]
        : [];

    /// <summary>
    /// Fold one property read into the names known so far. Returns the names unchanged for any other property,
    /// or when the device sent an error. Names are device text: control characters are not stripped here (export does).
    /// </summary>
    public static IReadOnlyList<string?>? Merge(IReadOnlyList<string?>? names, BacnetObjectTypes type, BacnetPropertyIds property,
        IList<BacnetValue>? values)
    {
        if (values is not { Count: > 0 } || values[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR) return names;
        if (property == BacnetPropertyIds.PROP_STATE_TEXT && IsMultiState(type))
            return [.. values.Select(v => v.Value as string)];
        if (IsBinary(type) && property is BacnetPropertyIds.PROP_INACTIVE_TEXT or BacnetPropertyIds.PROP_ACTIVE_TEXT)
        {
            var pair = new string?[2];
            if (names is { Count: 2 }) { pair[0] = names[0]; pair[1] = names[1]; }
            pair[property == BacnetPropertyIds.PROP_ACTIVE_TEXT ? 1 : 0] = values[0].Value as string;
            return pair;
        }
        return names;
    }

    /// <summary>
    /// A formatted value ("2", "Active") with its name: "Occupied (2)", "On (Active)". Unchanged when the device gave no
    /// usable name, or the name only repeats the value (an Active_Text of "Active").
    /// </summary>
    public static string Label(BacnetObjectTypes type, string? raw, IReadOnlyList<string?>? names)
    {
        if (string.IsNullOrEmpty(raw) || names is not { Count: > 0 }) return raw ?? "";
        string? name = null;
        if (IsMultiState(type) && uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n >= 1 && n <= names.Count)
            name = names[(int)n - 1];
        else if (IsBinary(type) && names.Count == 2)
            name = raw.Equals("Active", StringComparison.OrdinalIgnoreCase) ? names[1]
                : raw.Equals("Inactive", StringComparison.OrdinalIgnoreCase) ? names[0] : null;

        name = name?.Trim();
        return string.IsNullOrEmpty(name) || name.Equals(raw, StringComparison.OrdinalIgnoreCase) ? raw : $"{name} ({raw})";
    }

    /// <summary>
    /// The states a tech can pick when writing, as (label, text the write parser takes). Binary: active first, since most
    /// overrides turn something on. Multi-state: every state in order. Empty when the type has no states.
    /// </summary>
    public static IReadOnlyList<(string Label, string Text)> Choices(BacnetObjectTypes type, IReadOnlyList<string?>? names)
    {
        if (IsBinary(type))
            return [(Label(type, "Active", names) is var on && on != "Active" ? on : "On (Active)", "active"),
                    (Label(type, "Inactive", names) is var off && off != "Inactive" ? off : "Off (Inactive)", "inactive")];
        if (IsMultiState(type) && names is { Count: > 0 })
            return [.. names.Select((_, i) => (Label(type, (i + 1).ToString(CultureInfo.InvariantCulture), names), (i + 1).ToString(CultureInfo.InvariantCulture)))];
        return [];
    }

    /// <summary>A value about to be written, labelled the same way: "Standby (3)", "On (Active)".</summary>
    public static string Describe(BacnetObjectTypes type, BacnetValue value, IReadOnlyList<string?>? names) =>
        Label(type, BacnetNames.FormatValue(type, BacnetPropertyIds.PROP_PRESENT_VALUE, value), names);

    /// <summary>The state number for a typed state name ("standby" for "Standby"), or null if no state has that name.</summary>
    public static uint? NumberOf(string text, IReadOnlyList<string?>? names)
    {
        if (names is null) return null;
        var t = text.Trim();
        for (var i = 0; i < names.Count; i++)
            if (!string.IsNullOrWhiteSpace(names[i]) && string.Equals(names[i]!.Trim(), t, StringComparison.OrdinalIgnoreCase))
                return (uint)(i + 1);
        return null;
    }
}
