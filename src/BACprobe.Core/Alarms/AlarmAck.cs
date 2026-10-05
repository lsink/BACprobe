using System.IO.BACnet;
using BACprobe.Core.Writing;

namespace BACprobe.Core.Alarms;

/// <summary>
/// One transition to acknowledge, as the device must be told it: the transition's state and its exact time stamp.
/// </summary>
public sealed record AckTarget(EventTransition Transition, BacnetEventStates StateAcked, BacnetGenericTime Stamp)
{
    /// <summary>"to alarm (high limit), 2026-10-05 14:02:11"</summary>
    public string Text => EventText.Describe(Transition, StateAcked) + (EventText.FormatStamp(Stamp) is { Length: > 0 } when ? $", {when}" : "");
}

/// <summary>
/// Acknowledging one point's alarm: every transition still waiting (a cleared alarm usually has two, into alarm and back to
/// normal). It changes nothing about the point, but the site's front-end will show the alarm as seen, by <see cref="AckSource"/>,
/// so it gets the same confirmation and log as a write.
/// </summary>
public sealed record AlarmAckRequest(ActiveEvent Event, string DeviceName, string AckSource)
{
    /// <summary>"BACprobe (larry)": shown in the front-end's alarm history as who acknowledged it.</summary>
    public static string DefaultSource => $"BACprobe ({Environment.UserName})";

    /// <summary>What will be acknowledged; empty when nothing is waiting (callers should not offer the action then).</summary>
    public IReadOnlyList<AckTarget> Targets => EventText.AckTargets(Event);

    public string ObjectLabel => $"{Event.DisplayName} ({Event.ObjectLabel})";

    public string TargetText => Targets.Count == 0 ? "nothing (already acknowledged)" : string.Join("; ", Targets.Select(t => t.Text));

    public string Headline => $"Acknowledge the alarm on {Event.DisplayName}?";

    public IReadOnlyList<ConfirmFact> Facts =>
    [
        new("Device", $"{DeviceName} (device {Event.Device.InstanceId})"),
        new("Point", ObjectLabel),
        new("State now", Event.StateText + (Event.ValueText is { Length: > 0 } v ? $", reads {v}" : "")),
        new("Acknowledging", TargetText),
        new("Acknowledged as", AckSource),
    ];

    public string ConfirmLabel => Warning is null ? "Acknowledge" : "Acknowledge life-safety alarm";

    public string Consequence =>
        "This tells the controller that someone has seen the alarm. It does not fix anything or change the point: an alarm that is still " +
        "active stays active until its cause is fixed. The site's front-end will show it as acknowledged by " + AckSource + ".";

    public string? Warning => Event.State == BacnetEventStates.EVENT_STATE_LIFE_SAFETY_ALARM
        ? "This is a life-safety alarm. Only acknowledge it if the site's procedure says you may."
        : null;

    /// <summary>The write log's action column for one transition: "acknowledge alarm: to alarm (high limit)".</summary>
    public static string LogAction(AckTarget t) => $"acknowledge alarm: {EventText.Describe(t.Transition, t.StateAcked)}";
}

/// <summary>Why an acknowledgement failed, with a likely cause and next step.</summary>
public static class AckErrors
{
    public static WriteErrorText Explain(Exception ex)
    {
        var m = ex.Message;
        bool Has(string s) => m.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (BacnetFailure.IsTimeout(ex))
            return new("The device did not answer the acknowledgement (timeout).",
                "Network drop or a busy controller. The acknowledgement may or may not have reached it.",
                "Press Refresh to see whether the alarm still needs acknowledging.");
        if (Has("INVALID_TIME_STAMP"))
            return new("The device refused: the alarm's time stamp no longer matches.",
                "The alarm happened again (or cleared) after BACprobe read the list, so this acknowledgement was for an older one.",
                "Press Refresh, then acknowledge the alarm as it is now.");
        if (Has("INVALID_EVENT_STATE"))
            return new("The device refused: the alarm is in a different state now.",
                "The point changed state after BACprobe read the list.",
                "Press Refresh, then try again.");
        if (Has("UNKNOWN_OBJECT"))
            return new("The device says that point does not exist.",
                "The object was deleted, or the device was reprogrammed since the list was read.",
                "Press Refresh.");
        if (Has("SERVICE_REQUEST_DENIED") || Has("REJECT") || Has("UNRECOGNIZED_SERVICE") || Has("PASSWORD") || Has("SECURITY"))
            return new($"The device refused the acknowledgement ({m}).",
                "Some controllers only take acknowledgements from their own front-end, or need a password.",
                "Acknowledge it from the site's front-end or the controller's own tool.");
        return new($"The device returned an error: {m}",
            "The controller refused the acknowledgement for a reason BACprobe does not recognise.",
            "Press Refresh, and look the error up in the controller's documentation if it happens again.");
    }
}
