using System.IO.BACnet;

namespace BACprobe.Core.Browsing;

/// <summary>One thing wrong with a point, in plain English, with the likely cause and what to do next.</summary>
public sealed record PointProblem(string Text, string Cause, string NextStep);

/// <summary>
/// Reads a point's health from Status_Flags (in alarm, fault, overridden, out of service) and Reliability (why it is in
/// fault: open loop, shorted loop...), and says what is wrong in words a tech can act on. Pure, so it can be tested.
/// </summary>
public static class PointHealth
{
    private const uint ProprietaryReliabilityMin = 64;

    /// <summary>The flags in a Status_Flags value; null if the device sent something else (an error, or nothing).</summary>
    public static BacnetStatusFlags? FlagsFrom(IList<BacnetValue>? values) =>
        values is { Count: > 0 } && values[0].Value is BacnetBitString bits ? (BacnetStatusFlags)bits.ConvertToInt() : null;

    /// <summary>The code in a Reliability value; null if the device sent something else.</summary>
    public static uint? ReliabilityFrom(IList<BacnetValue>? values) =>
        values is { Count: > 0 } && values[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED && values[0].Value is IConvertible c
            ? Convert.ToUInt32(c, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    /// <summary>"open loop", "shorted loop"; vendor codes are named as such.</summary>
    public static string ReliabilityName(uint code)
    {
        if (code >= ProprietaryReliabilityMin) return $"vendor-specific reliability {code}";
        var r = (BacnetReliability)code;
        if (!Enum.IsDefined(r)) return $"reliability {code}";
        return r.ToString()["RELIABILITY_".Length..].Replace('_', ' ').ToLowerInvariant();
    }

    /// <summary>Status_Flags as words for the properties panel: "fault, out of service", or "normal".</summary>
    public static string FlagsText(BacnetStatusFlags flags)
    {
        var parts = new List<string>();
        if (flags.HasFlag(BacnetStatusFlags.STATUS_FLAG_IN_ALARM)) parts.Add("in alarm");
        if (flags.HasFlag(BacnetStatusFlags.STATUS_FLAG_FAULT)) parts.Add("fault");
        if (flags.HasFlag(BacnetStatusFlags.STATUS_FLAG_OVERRIDDEN)) parts.Add("overridden (hand/off/auto at the controller)");
        if (flags.HasFlag(BacnetStatusFlags.STATUS_FLAG_OUT_OF_SERVICE)) parts.Add("out of service");
        return parts.Count == 0 ? "normal" : string.Join(", ", parts);
    }

    /// <summary>A fault: the flag is set, or the device names a reliability problem without setting it.</summary>
    public static bool IsFault(BacnetStatusFlags? flags, uint? reliability) =>
        flags?.HasFlag(BacnetStatusFlags.STATUS_FLAG_FAULT) == true || reliability is > 0;

    /// <summary>Everything wrong with the point, worst first: fault, alarm, out of service. Empty for a healthy point.</summary>
    public static IReadOnlyList<PointProblem> Problems(BacnetStatusFlags? flags, uint? reliability)
    {
        var list = new List<PointProblem>();
        if (IsFault(flags, reliability)) list.Add(Fault(reliability));
        if (flags?.HasFlag(BacnetStatusFlags.STATUS_FLAG_IN_ALARM) == true)
            list.Add(new("In alarm",
                "The value is outside the alarm limits set in the controller (or in its alarm state, for an on/off or multi-state point).",
                "Compare the value with the point's alarm limits, and check the controller's alarm list for when it started."));
        if (flags?.HasFlag(BacnetStatusFlags.STATUS_FLAG_OUT_OF_SERVICE) == true)
            list.Add(new("Out of service",
                "Someone took the point out of service: the controller ignores the real sensor or output, and the value can be set by hand.",
                "If that was not meant to stay, put it back in service (Out Of Service = false) in the controller's own tool."));
        return list;
    }

    private static PointProblem Fault(uint? reliability)
    {
        var name = reliability is { } code && code > 0 ? ReliabilityName(code) : null;
        var text = name is null ? "Fault" : $"Fault: {name}";
        var (cause, next) = (BacnetReliability?)reliability switch
        {
            BacnetReliability.RELIABILITY_OPEN_LOOP => (
                "The input reads an open circuit: a broken wire, a loose terminal, or the sensor is unplugged or failed open.",
                "Check the wiring and terminals at the controller, then measure the sensor."),
            BacnetReliability.RELIABILITY_SHORTED_LOOP => (
                "The input reads a short circuit: the sensor wires are touching, or the sensor has failed short.",
                "Look for pinched or crossed wires, then measure the sensor."),
            BacnetReliability.RELIABILITY_NO_SENSOR => (
                "The controller sees no sensor on this input.",
                "Check a sensor is fitted and wired to the input this point uses."),
            BacnetReliability.RELIABILITY_OVER_RANGE or BacnetReliability.RELIABILITY_UNDER_RANGE => (
                "The reading is outside what the sensor can measure: usually a wiring fault, or the input is set for a different sensor type.",
                "Check the wiring, and that the input's sensor type matches the sensor fitted."),
            BacnetReliability.RELIABILITY_NO_OUTPUT => (
                "The controller cannot drive this output: nothing is connected, or the output circuit has failed.",
                "Check the output wiring and the device it drives (actuator, relay, VFD)."),
            BacnetReliability.RELIABILITY_COMMUNICATION_FAILURE => (
                "This point gets its value over a network (another controller, a gateway or a field bus), and that link is down.",
                "Check the device it reads from, and the trunk or link to it."),
            BacnetReliability.RELIABILITY_CONFIGURATION_ERROR => (
                "The point's setup in the controller is invalid.",
                "Check the point's configuration in the controller's programming tool."),
            BacnetReliability.RELIABILITY_PROCESS_ERROR => (
                "The controller's program flagged a problem with the equipment (for example a failed proof).",
                "Check the equipment, then the program's alarm logic."),
            BacnetReliability.RELIABILITY_TRIPPED => (
                "A safety or overload has tripped.",
                "Find the cause, then reset the trip at the equipment."),
            BacnetReliability.RELIABILITY_MULTI_STATE_FAULT or BacnetReliability.RELIABILITY_MULTI_STATE_OUT_OF_RANGE => (
                "The value is not one of the point's allowed states.",
                "Check the inputs or program that set it."),
            _ => (
                "The controller reports this point as unreliable" + (name is null ? "." : $" ({name})."),
                "Look the reliability up in the controller's documentation, and check the sensor or link the point depends on."),
        };
        return new(text, cause, next);
    }

    /// <summary>One line for a column: "Fault: open loop; Out of service". Empty for a healthy point.</summary>
    public static string Summary(BacnetStatusFlags? flags, uint? reliability) =>
        string.Join("; ", Problems(flags, reliability).Select(p => p.Text));

    /// <summary>The tooltip: each problem with its likely cause and next step.</summary>
    public static string Explanation(BacnetStatusFlags? flags, uint? reliability) =>
        string.Join(Environment.NewLine + Environment.NewLine,
            Problems(flags, reliability).Select(p => $"{p.Text}{Environment.NewLine}Likely cause: {p.Cause}{Environment.NewLine}Next step: {p.NextStep}"));
}
