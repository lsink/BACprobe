using System.Globalization;
using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Writing;

/// <summary>A priority the user can pick, with plain-English wording.</summary>
public sealed record PriorityChoice(int Number, string Name, string Hint, bool Advanced)
{
    public string Label => $"{Number} - {Name}";

    /// <summary>The short list a field tech normally needs, with Manual Operator (8) first. Life-safety slots are advanced.</summary>
    public static IReadOnlyList<PriorityChoice> All { get; } =
    [
        new(8, "Manual Operator", "Normal choice for a technician override. Stays until someone releases it.", false),
        new(16, "Default (lowest)", "Same slot most automatic programs use; the program may overwrite it.", false),
        new(10, "Available", "A free slot below Manual Operator.", false),
        new(6, "Minimum On/Off", "Used by minimum run/off timers; normally left to the controller.", true),
        new(5, "Critical Equipment Control", "Used for equipment protection; only use if you know why.", true),
        new(2, "Automatic Life Safety", "Reserved for life-safety systems. Do not use for normal work.", true),
        new(1, "Manual Life Safety", "Reserved for life-safety systems. Do not use for normal work.", true),
    ];

    public static PriorityChoice Default => All[0];
}

public static class WriteValueParser
{
    /// <summary>Turn what the user typed into the right BACnet type for the object.</summary>
    public static bool TryParse(BacnetObjectTypes type, string text, out BacnetValue value, out string error)
    {
        value = default;
        error = "";
        var t = text.Trim();
        if (t.Length == 0)
        {
            error = "Type a value to write.";
            return false;
        }

        switch (type)
        {
            case BacnetObjectTypes.OBJECT_ANALOG_OUTPUT or BacnetObjectTypes.OBJECT_ANALOG_VALUE or BacnetObjectTypes.OBJECT_ANALOG_INPUT:
                if (!float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || !float.IsFinite(f))
                {
                    error = $"'{t}' is not a number. Analog points take a number such as 72.5.";
                    return false;
                }
                value = new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, f);
                return true;

            case BacnetObjectTypes.OBJECT_BINARY_OUTPUT or BacnetObjectTypes.OBJECT_BINARY_VALUE or BacnetObjectTypes.OBJECT_BINARY_INPUT:
                var state = t.ToLowerInvariant() switch
                {
                    "active" or "on" or "1" or "true" or "start" => 1u,
                    "inactive" or "off" or "0" or "false" or "stop" => 0u,
                    _ => (uint?)null,
                };
                if (state is null)
                {
                    error = $"'{t}' is not a binary state. Use on/off, active/inactive, or 1/0.";
                    return false;
                }
                value = new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, state.Value);
                return true;

            case BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT or BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE or BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT:
                if (!uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
                {
                    error = $"'{t}' is not a state number. Multi-state points take 1, 2, 3, ...";
                    return false;
                }
                value = new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, n);
                return true;

            default:
                error = $"BACprobe cannot write {BacnetNames.ObjectTypeName(type)} points yet.";
                return false;
        }
    }
}

/// <summary>One labelled line in the confirmation dialog, e.g. "Priority" / "8 - Manual Operator".</summary>
public sealed record ConfirmFact(string Label, string Value);

/// <summary>One thing the user is about to do to one point. A null <see cref="Value"/> means release (relinquish).</summary>
public sealed record WriteRequest(
    DiscoveredDevice Device,
    string DeviceName,
    BacnetObjectId Point,
    string ObjectName,
    BacnetValue? Value,
    string ValueText,
    int Priority,
    string? CurrentValueText = null)
{
    public bool IsRelease => Value is null;
    public string ObjectLabel => $"{ObjectName} ({BacnetNames.ObjectLabel(Point)})";
    public string PriorityLabel => $"priority {Priority} ({BacnetNames.PriorityName(Priority)})";

    /// <summary>The question, in one line: "Write 25 to Damper Position?"</summary>
    public string Headline => IsRelease ? $"Release your override of {ObjectName}?" : $"Write {ValueText} to {ObjectName}?";

    /// <summary>The details a tech should check before saying yes. The priority is always listed, in plain English.</summary>
    public IReadOnlyList<ConfirmFact> Facts
    {
        get
        {
            var facts = new List<ConfirmFact>
            {
                new("Device", $"{DeviceName} (device {Device.InstanceId})"),
                new("Point", ObjectLabel),
            };
            if (CurrentValueText is { Length: > 0 }) facts.Add(new("Now", CurrentValueText));
            if (!IsRelease) facts.Add(new("New value", ValueText));
            facts.Add(new("Priority", $"{Priority} - {BacnetNames.PriorityName(Priority)}"));
            return facts;
        }
    }

    public string Consequence => IsRelease
        ? "The point goes back to whatever the next-highest priority (or the controller's own program) says."
        : "This overrides the controller's automatic control of this point until you release it.";

    /// <summary>Extra warning for life-safety and critical-equipment priorities; null otherwise.</summary>
    public string? Warning => !IsRelease && Priority <= 5
        ? "This priority is reserved for life-safety or critical equipment. Only continue if you are sure."
        : null;

    /// <summary>Plain-English text for the confirmation dialog. Always names the priority.</summary>
    public string ConfirmationText()
    {
        var target = $"{ObjectLabel} on device {Device.InstanceId} \"{DeviceName}\"";
        if (IsRelease)
            return $"Release your override of {target} at {PriorityLabel}?\n\n" +
                   "The point goes back to whatever the next-highest priority (or the controller's own program) says.";

        var current = CurrentValueText is { Length: > 0 } ? $"It is currently {CurrentValueText}.\n\n" : "";
        var warn = Priority <= 5
            ? "\n\nWARNING: this priority is reserved for life-safety or critical equipment. Only continue if you are sure."
            : "";
        return $"Write {ValueText} to {target} at {PriorityLabel}?\n\n{current}" +
               "This overrides the controller's automatic control of this point until you release it." + warn;
    }
}

public sealed record WriteLogEntry(DateTimeOffset Time, uint DeviceInstance, string DeviceName, string Point,
    string Action, int Priority, bool Success, string Result)
{
    public string Text =>
        $"{Time:yyyy-MM-dd HH:mm:ss} | device {DeviceInstance} \"{DeviceName}\" | {Point} | {Action} @ {Priority} ({BacnetNames.PriorityName(Priority)}) | {(Success ? "OK" : "FAILED")}: {Result}";
}

/// <summary>Every write attempt, success or failure. Optionally mirrored to a local text file (no cloud, no telemetry).</summary>
public sealed class WriteLog(string? filePath = null)
{
    private readonly List<WriteLogEntry> _entries = [];
    private readonly Lock _lock = new();

    public event Action<WriteLogEntry>? Added;

    public IReadOnlyList<WriteLogEntry> Entries
    {
        get { lock (_lock) return [.. _entries]; }
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BACprobe", "write-log.txt");

    public void Add(WriteLogEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            if (filePath is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    File.AppendAllText(filePath, entry.Text + Environment.NewLine);
                }
                catch (IOException) { /* the in-memory log still has it */ }
                catch (UnauthorizedAccessException) { }
            }
        }
        Added?.Invoke(entry);
    }
}

public sealed record TrackedOverride(DiscoveredDevice Device, string DeviceName, BacnetObjectId Point, string ObjectName,
    int Priority, string ValueText)
{
    public string Description =>
        $"{ObjectName} ({BacnetNames.ObjectLabel(Point)}) on device {Device.InstanceId} \"{DeviceName}\" - {ValueText} at priority {Priority} ({BacnetNames.PriorityName(Priority)})";
}

/// <summary>Overrides this session has left in place, so they can be offered for release before disconnecting.</summary>
public sealed class OverrideTracker
{
    private readonly Dictionary<(uint Device, BacnetObjectId Point, int Priority), TrackedOverride> _active = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<TrackedOverride> Active
    {
        get
        {
            lock (_lock) return _active.Values.OrderBy(o => o.Device.InstanceId).ThenBy(o => o.Point.type).ThenBy(o => o.Point.instance)
                .ThenBy(o => o.Priority).ToList();
        }
    }

    public void Record(TrackedOverride o)
    {
        lock (_lock) _active[(o.Device.InstanceId, o.Point, o.Priority)] = o;
    }

    public void Remove(uint device, BacnetObjectId obj, int priority)
    {
        lock (_lock) _active.Remove((device, obj, priority));
    }
}
