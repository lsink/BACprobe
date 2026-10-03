using System.Globalization;
using System.IO.BACnet;
using System.Text.RegularExpressions;
using BACprobe.Core.Browsing;

namespace BACprobe.Core.Writing;

/// <summary>
/// Turns "the write failed" or "the write did nothing" into an explanation a tech can act on, using what was found when
/// the point was investigated. Pure: no network.
/// </summary>
public static partial class WriteExplainer
{
    /// <summary>The device's own words, made readable: "...ERROR_CODE_WRITE_ACCESS_DENIED" becomes "write access denied".</summary>
    public static string DeviceSaid(string message)
    {
        var m = ErrorCode().Match(message);
        return m.Success ? m.Groups[1].Value.Replace('_', ' ').ToLowerInvariant() : message;
    }

    [GeneratedRegex(@"(?:ERROR_CODE|ABORT|REJECT)_([A-Z_]+)")]
    private static partial Regex ErrorCode();

    private static bool Has(string message, string text) => message.Contains(text, StringComparison.OrdinalIgnoreCase);

    // ---------- the write was refused ----------

    /// <summary>Explain a refused write. <paramref name="probe"/> may be null or unreachable; the explanation says what it could not check.</summary>
    public static PromptContent ExplainFailure(WriteRequest req, string message, PointProbe? probe)
    {
        var facts = BaseFacts(req);
        facts.Add(new("Device said", DeviceSaid(message)));
        AddProbeFacts(facts, probe);

        string headline, cause, next;
        string? warning = null;

        if (Has(message, "timeout"))
        {
            headline = "The device did not answer the write";
            cause = "The network dropped, or the controller is busy. The write may or may not have happened.";
            next = "Read the point again to see its real state before trying once more.";
        }
        else if (Has(message, "WRITE_ACCESS_DENIED"))
        {
            (headline, cause, next) = ExplainAccessDenied(req, probe);
        }
        else if (Has(message, "VALUE_OUT_OF_RANGE") || Has(message, "PARAMETER_OUT_OF_RANGE"))
        {
            headline = "The value is outside what this point accepts";
            cause = probe is { Min: not null, Max: not null }
                ? $"This point only accepts values from {Num(probe.Min!.Value)} to {Num(probe.Max!.Value)}{Units(probe)}, and {req.ValueText} is outside that."
                : "The controller has limits for this point and the value is outside them.";
            next = probe is { Min: not null, Max: not null }
                ? $"Try a value between {Num(probe.Min!.Value)} and {Num(probe.Max!.Value)}."
                : "Try a value closer to what the point normally holds.";
        }
        else if (Has(message, "INVALID_DATA_TYPE") || Has(message, "DATATYPE_NOT_SUPPORTED") || Has(message, "INCONSISTENT_PARAMETERS"))
        {
            headline = "The device does not accept that kind of value";
            cause = $"This is {Article(BacnetNames.ObjectTypeName(req.Point.type))} point, and it expects a different kind of value than the one sent " +
                    "(for example a number where it wants on/off, or the reverse).";
            next = "Read the point to see what it holds now, then write a matching value.";
        }
        else if (Has(message, "UNKNOWN_OBJECT"))
        {
            headline = "The device says that point does not exist";
            cause = "The point was deleted, or the object number is wrong.";
            next = "Select the device again to refresh its object list.";
        }
        else if (Has(message, "UNKNOWN_PROPERTY") || Has(message, "NOT_COV_PROPERTY"))
        {
            headline = "This point has no value that can be written";
            cause = "This kind of object does not have a writable Present Value.";
            next = "Choose a different point.";
        }
        else if (Has(message, "PASSWORD") || Has(message, "SECURITY") || Has(message, "AUTHORIZATION") || Has(message, "NO_ACCESS"))
        {
            headline = "The device wants permission before it accepts writes";
            cause = "A password or access level is required, and BACprobe does not send one.";
            next = "Ask whoever manages the controller for write access, or use the vendor's tool for this change.";
        }
        else if (Has(message, "SERVICE_REQUEST_DENIED") || Has(message, "OPTIONAL_FUNCTIONALITY_NOT_SUPPORTED") || Has(message, "REJECT")
                 || Has(message, "ABORT") || Has(message, "BUSY") || Has(message, "NOT_CONFIGURED"))
        {
            headline = "The controller will not take writes right now";
            cause = "It is busy, still starting up, in a mode where it refuses writes, or does not support writing this way. " +
                    "(For an output, check any hand-off-auto switch on the equipment.)";
            next = "Wait a moment and try again. If it keeps refusing, use the controller's own tool.";
        }
        else if (Has(message, "NO_SPACE") || Has(message, "OUT_OF_MEMORY"))
        {
            headline = "The controller has no room to store the write";
            cause = "Its memory for overrides or properties is full.";
            next = "Release other overrides on this controller and try again.";
        }
        else
        {
            headline = "The controller refused the write";
            cause = "The reason is not one BACprobe recognises.";
            next = "Read the point to check its state, then look up the device's error in its documentation.";
        }

        if (req.Priority <= 5)
            warning = "You tried priority " + req.Priority + ", which is reserved for life-safety or critical equipment. Do not retry at a high priority just to force it through.";

        return new PromptContent("Write refused", headline, facts, $"Likely cause: {cause}\n\nNext step: {next}", warning);
    }

    private static (string Headline, string Cause, string Next) ExplainAccessDenied(WriteRequest req, PointProbe? probe)
    {
        if (probe is { Reachable: true, HasPriorityArray: false })
            return ("This point cannot be overridden",
                    "It has no priority array, so it is not commandable: it is an input or a read-only value. Only outputs and commandable values accept writes.",
                    "Write to the output or value point that drives it. To test an input you would have to put it out of service in the controller's own tool.");

        if (req.Priority == 6)
            return ("Priority 6 is kept for the controller itself",
                    "Priority 6 (Minimum On/Off) is used by the controller's own minimum run and off timers, and many devices refuse outside writes to it.",
                    "Write at priority 8 (Manual Operator) instead.");

        if (probe is { Reachable: true, HasPriorityArray: true })
            return ("The controller protects this point from writes",
                    "The point is commandable but the device still refuses: it is write-protected, locked by a program or setting, or your PC is not allowed to write to it.",
                    "Check the point's protection or lock setting and any write permission for this PC, or ask whoever manages the controller. A hand-off-auto switch on the equipment can also block it.");

        return ("The device refused the write: access denied",
                "The point is read-only (an input, or not commandable), or the device is protected against writes. BACprobe could not check the point afterwards to say which.",
                "Pick an output or value point that has a priority array, or check the device's write protection.");
    }

    // ---------- the write was accepted but did nothing ----------

    /// <summary>True when the point now holds what was written (allowing for rounding). A release is never "ineffective".</summary>
    public static bool IsEffective(WriteRequest req, PointProbe probe)
    {
        if (req.Value is not { } v || !probe.Reachable || probe.PresentValue is not { } now) return true; // nothing to compare: do not cry wolf
        if (PointProber.ToNumber(v) is not { } target) return true;
        var tolerance = req.Point.type is BacnetObjectTypes.OBJECT_ANALOG_OUTPUT or BacnetObjectTypes.OBJECT_ANALOG_VALUE or BacnetObjectTypes.OBJECT_ANALOG_INPUT
            ? Math.Max(0.1, Math.Abs(target) * 0.005) // devices round, and display rounds
            : 0.0001;
        return Math.Abs(now - target) <= tolerance;
    }

    /// <summary>Explain a write the device accepted but that did not change the point. Null if it did take effect (or cannot be checked).</summary>
    public static PromptContent? ExplainIneffective(WriteRequest req, PointProbe probe)
    {
        if (IsEffective(req, probe)) return null;

        var facts = BaseFacts(req);
        facts.Add(new("Value now", $"{probe.PresentValueText}{Units(probe)}"));
        AddProbeFacts(facts, probe, skipValue: true);

        var control = probe.Controlling;
        string headline, cause, next;
        string? warning = null;

        if (control is not null && control.Priority < req.Priority)
        {
            headline = "The device accepted the write, but a higher priority is holding the point";
            cause = $"Priority {control.Priority} ({BacnetNames.PriorityName(control.Priority)}) is holding {req.ObjectName} at {control.ValueText}. " +
                    $"The lowest-numbered priority always wins, so your write at priority {req.Priority} is stored but ignored until that one lets go.";
            next = $"Find what is using priority {control.Priority}: a technician's override, a program, or a front-end command. Release it there, " +
                   "or (only if appropriate) write at a higher-ranking priority.";
            if (control.Priority <= 5)
                warning = $"Priority {control.Priority} is reserved for life-safety or critical equipment. Do not take it over unless you know why it is set.";
        }
        else if (control is not null && control.Priority == req.Priority)
        {
            headline = "The device accepted the write, but changed the value";
            cause = $"Your write is the one in control, yet the point holds {probe.PresentValueText}{Units(probe)} instead of {req.ValueText}. " +
                    "The controller rounded it, limited it to a range, or a program is adjusting it.";
            next = "Read the point's limits and resolution. If it keeps changing back, look for a program writing to it.";
        }
        else if (control is not null)
        {
            headline = "The device accepted the write, but then took it back";
            cause = $"Your priority-{req.Priority} write is no longer in the priority array, and the point is being held at priority {control.Priority} ({BacnetNames.PriorityName(control.Priority)}). " +
                    "A program or sequence is clearing and rewriting its commands.";
            next = "Look for a program in the controller that commands this point; a manual override will keep losing to it.";
        }
        else if (probe.OutOfService == true)
        {
            headline = "The device accepted the write, but the point is out of service";
            cause = "While a point is Out Of Service, its value comes from the field, not from the priority array, so writes have no effect.";
            next = "Put the point back in service in the controller's own tool, or write to the point that really drives it.";
        }
        else
        {
            headline = "The device accepted the write, but the value did not change";
            cause = "No priority is holding a value, yet the point stays at its old value. The controller may update it on its next program cycle, " +
                    "ignore writes to this point, or drive it from something else.";
            next = "Read the point again in a few seconds. If it never changes, check how the controller drives this point.";
        }

        return new PromptContent("Write had no effect", headline, facts, $"Likely cause: {cause}\n\nNext step: {next}", warning);
    }

    // ---------- shared pieces ----------

    private static List<ConfirmFact> BaseFacts(WriteRequest req) =>
    [
        new("Point", $"{req.ObjectLabel} on device {req.Device.InstanceId}"),
        new("You tried", $"{req.ValueText} at priority {req.Priority} ({BacnetNames.PriorityName(req.Priority)})"),
    ];

    private static void AddProbeFacts(List<ConfirmFact> facts, PointProbe? probe, bool skipValue = false)
    {
        if (probe is null) return;
        if (!probe.Reachable)
        {
            facts.Add(new("Checked afterwards", "the device did not answer, so the point could not be checked"));
            return;
        }
        if (!skipValue && probe.PresentValueText is not null) facts.Add(new("Value now", $"{probe.PresentValueText}{Units(probe)}"));
        if (probe.HasPriorityArray == false) facts.Add(new("Commandable", "no (it has no priority array)"));
        else if (probe.HasPriorityArray == true)
            facts.Add(new("Priority array", probe.Slots.Count == 0 ? "empty: nothing is overriding it" : string.Join("; ", probe.Slots.Select(s => s.Description))));
        if (probe.Min is not null && probe.Max is not null) facts.Add(new("Allowed range", $"{Num(probe.Min.Value)} to {Num(probe.Max.Value)}{Units(probe)}"));
        if (probe.OutOfService == true) facts.Add(new("Out of service", "yes"));
    }

    private static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Units(PointProbe p) => string.IsNullOrEmpty(p.Units) ? "" : " " + p.Units;

    private static string Article(string noun) => noun.Length > 0 && "AEIOUaeiou".Contains(noun[0]) ? "an " + noun : "a " + noun;
}
