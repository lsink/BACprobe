using System.Globalization;
using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Alarms;

/// <summary>The three transitions BACnet tracks for every event: into alarm, into fault, back to normal (the bit order of Acked_Transitions).</summary>
public enum EventTransition { ToOffnormal = 0, ToFault = 1, ToNormal = 2 }

/// <summary>How a device's alarms were found.</summary>
public enum AlarmSource
{
    /// <summary>The device answered GetEventInformation: its own list of active and unacknowledged events.</summary>
    EventInformation,

    /// <summary>
    /// The device refused GetEventInformation, so BACprobe listed the points whose Status_Flags say in alarm or fault and read
    /// their event properties. Events that already went back to normal without being acknowledged cannot be seen this way.
    /// </summary>
    StatusFlags,
}

/// <summary>
/// One active or unacknowledged event on one point, as the device reports it (GetEventInformation, or the point's own
/// Event_State / Acked_Transitions / Event_Time_Stamps), plus what was read about the point to explain it.
/// </summary>
public sealed class ActiveEvent
{
    public required DiscoveredDevice Device { get; init; }
    public required BacnetObjectId Point { get; init; }
    public required BacnetEventStates State { get; set; }

    /// <summary>Acknowledged flags, indexed by <see cref="EventTransition"/>. A transition that never needed acknowledging counts as acknowledged.</summary>
    public required bool[] Acked { get; set; }

    /// <summary>When each transition last happened, indexed by <see cref="EventTransition"/>, exactly as the device sent them (an acknowledgement must echo them).</summary>
    public required BacnetGenericTime[] TimeStamps { get; set; }

    /// <summary>Alarm (needs attention) or event (informational); null when the device did not say.</summary>
    public BacnetNotifyTypes? NotifyType { get; init; }

    /// <summary>Notification priorities per transition (lower = more urgent), when the device gave them.</summary>
    public IReadOnlyList<uint>? Priorities { get; init; }

    public AlarmSource Source { get; init; }

    // What was read about the point, to name it and explain the event. Missing when the device would not say.
    public string? Name { get; set; }
    public string? ValueText { get; set; }
    public BacnetStatusFlags? StatusFlags { get; set; }
    public uint? Reliability { get; set; }
    public float? HighLimit { get; set; }
    public float? LowLimit { get; set; }
    public float? Deadband { get; set; }
    public string? Units { get; set; }

    public string ObjectLabel => BacnetNames.ObjectLabel(Point);

    /// <summary>"AI 1", for narrow columns.</summary>
    public string ShortId => $"{BacnetNames.ObjectTypeShort(Point.type)} {Point.instance}";
    public string DisplayName => Name ?? ObjectLabel;
    public string StateText => EventText.StateName(State);

    public bool NeedsAck => Acked.Any(a => !a);

    /// <summary>"to alarm, to normal": the transitions still waiting for someone to acknowledge them. Empty when all are acknowledged.</summary>
    public string UnackedText => string.Join(", ", EventText.Unacked(this).Select(EventText.TransitionName));

    /// <summary>When the point went into its current state, if the device recorded it.</summary>
    public string SinceText => EventText.FormatStamp(TimeStamps[(int)EventText.TransitionInto(State)]);

    /// <summary>The worst priority (lowest number) the device gave this event's transitions, for sorting. 255 if unknown.</summary>
    public uint UrgentPriority => Priorities is { Count: > 0 } p ? p.Min() : 255;
}

/// <summary>Everything one device said about its alarms, or why it could not be asked.</summary>
/// <param name="Error">Set when the device could not be asked at all (it did not answer, or refused everything).</param>
public sealed record DeviceAlarms(DiscoveredDevice Device, IReadOnlyList<ActiveEvent> Events, AlarmSource? Source, string? Error = null,
    bool TimedOut = false);

/// <summary>Wording and rules for events: state names, which transition is unacknowledged, what to acknowledge, and why it happened. Pure.</summary>
public static class EventText
{
    public static string StateName(BacnetEventStates state) => state switch
    {
        BacnetEventStates.EVENT_STATE_NORMAL => "Normal",
        BacnetEventStates.EVENT_STATE_FAULT => "Fault",
        BacnetEventStates.EVENT_STATE_OFFNORMAL => "Alarm (off-normal)",
        BacnetEventStates.EVENT_STATE_HIGH_LIMIT => "High limit",
        BacnetEventStates.EVENT_STATE_LOW_LIMIT => "Low limit",
        BacnetEventStates.EVENT_STATE_LIFE_SAFETY_ALARM => "Life-safety alarm",
        _ => $"event state {(uint)state}",
    };

    public static string TransitionName(EventTransition t) => t switch
    {
        EventTransition.ToOffnormal => "to alarm",
        EventTransition.ToFault => "to fault",
        _ => "to normal",
    };

    /// <summary>The transition that leads into a state: any alarm state is reached "to off-normal".</summary>
    public static EventTransition TransitionInto(BacnetEventStates state) => state switch
    {
        BacnetEventStates.EVENT_STATE_NORMAL => EventTransition.ToNormal,
        BacnetEventStates.EVENT_STATE_FAULT => EventTransition.ToFault,
        _ => EventTransition.ToOffnormal,
    };

    public static bool IsAlarmState(BacnetEventStates state) =>
        state is not (BacnetEventStates.EVENT_STATE_NORMAL or BacnetEventStates.EVENT_STATE_FAULT);

    /// <summary>The transitions waiting for an acknowledgement, in BACnet order.</summary>
    public static IReadOnlyList<EventTransition> Unacked(ActiveEvent e) =>
        Enum.GetValues<EventTransition>().Where(t => (int)t < e.Acked.Length && !e.Acked[(int)t]).ToList();

    /// <summary>Acked_Transitions as three flags; a missing or short bit string counts as acknowledged (nothing to do).</summary>
    public static bool[] AckedFrom(BacnetBitString bits) =>
        [.. Enumerable.Range(0, 3).Select(i => i >= bits.Length || bits.GetBit((byte)i))];

    /// <summary>"This transition never happened", the way BACnet devices usually say it: an all-unspecified date and time.</summary>
    public static BacnetGenericTime Never => new(new DateTime(1, 1, 1), BacnetTimestampTags.TIME_STAMP_DATETIME) { PartialTime = BacnetTime.Any };

    /// <summary>
    /// True when the device never recorded this transition: an all-unspecified date and time, sequence 0, or a Null (which the
    /// library leaves as an empty time stamp).
    /// </summary>
    public static bool IsNever(BacnetGenericTime stamp) => stamp.Tag switch
    {
        BacnetTimestampTags.TIME_STAMP_NONE => true,
        BacnetTimestampTags.TIME_STAMP_SEQUENCE => stamp.Sequence == 0,
        BacnetTimestampTags.TIME_STAMP_DATETIME => stamp.Time.Year <= 1,
        _ => stamp.PartialTime is { IsFullyUnspecified: true } || (stamp.PartialTime is null && stamp.Time == default),
    };

    /// <summary>"2026-10-05 14:02:11" (the device's own clock), "14:02:11" for a time-only stamp, "event #12" for a sequence number, "" when it never happened.</summary>
    public static string FormatStamp(BacnetGenericTime stamp)
    {
        if (IsNever(stamp)) return "";
        return stamp.Tag switch
        {
            BacnetTimestampTags.TIME_STAMP_SEQUENCE => $"event #{stamp.Sequence}",
            BacnetTimestampTags.TIME_STAMP_TIME => stamp.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            _ => stamp.PartialTime is null
                ? stamp.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : stamp.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), // the device did not say the time of day
        };
    }

    /// <summary>For sorting newest first: the date and time the point went into its current state, or null if the device did not give a full date.</summary>
    public static DateTime? SinceDate(ActiveEvent e)
    {
        var stamp = e.TimeStamps[(int)TransitionInto(e.State)];
        return stamp.Tag == BacnetTimestampTags.TIME_STAMP_DATETIME && !IsNever(stamp) ? stamp.Time : null;
    }

    /// <summary>Worst first: life safety, fault, alarm, then events already back to normal; unacknowledged before acknowledged; most urgent priority, newest.</summary>
    public static IReadOnlyList<ActiveEvent> Sort(IEnumerable<ActiveEvent> events) =>
        events.OrderBy(e => Rank(e.State))
            .ThenBy(e => e.NeedsAck ? 0 : 1)
            .ThenBy(e => e.UrgentPriority)
            .ThenByDescending(e => SinceDate(e) ?? DateTime.MinValue)
            .ThenBy(e => e.Device.InstanceId).ThenBy(e => (int)e.Point.type).ThenBy(e => e.Point.instance)
            .ToList();

    private static int Rank(BacnetEventStates s) => s switch
    {
        BacnetEventStates.EVENT_STATE_LIFE_SAFETY_ALARM => 0,
        BacnetEventStates.EVENT_STATE_FAULT => 1,
        BacnetEventStates.EVENT_STATE_NORMAL => 3,
        _ => 2,
    };

    /// <summary>
    /// Every transition waiting for an acknowledgement, in BACnet order. The state sent with each has to fit its transition:
    /// an alarm state for "to alarm" (the current one while the point is still in alarm; off-normal once it has cleared, since
    /// the device no longer says which), Fault, or Normal. The time stamp is echoed exactly as the device sent it.
    /// </summary>
    public static IReadOnlyList<AckTarget> AckTargets(ActiveEvent e) =>
        Unacked(e).Select(t => new AckTarget(t, t switch
        {
            EventTransition.ToFault => BacnetEventStates.EVENT_STATE_FAULT,
            EventTransition.ToNormal => BacnetEventStates.EVENT_STATE_NORMAL,
            _ => IsAlarmState(e.State) ? e.State : BacnetEventStates.EVENT_STATE_OFFNORMAL,
        }, e.TimeStamps[(int)t])).ToList();

    /// <summary>"to alarm", "to alarm (high limit)", "to fault", "to normal".</summary>
    public static string Describe(EventTransition t, BacnetEventStates state) =>
        t == EventTransition.ToOffnormal && state is not BacnetEventStates.EVENT_STATE_OFFNORMAL
            ? $"{TransitionName(t)} ({StateName(state).ToLowerInvariant()})"
            : TransitionName(t);

    /// <summary>The value against the limits, when they are known: "Reads 85.2 °F; high limit 80, clears below 78 (deadband 2)."</summary>
    public static string LimitDetail(ActiveEvent e)
    {
        static string N(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
        var units = string.IsNullOrEmpty(e.Units) ? "" : " " + e.Units;
        var reads = e.ValueText is { Length: > 0 } v ? $"Reads {v}" : "";
        var band = e.Deadband is { } d && d > 0 ? d : (float?)null;
        string? limit = e.State switch
        {
            BacnetEventStates.EVENT_STATE_HIGH_LIMIT when e.HighLimit is { } h =>
                $"high limit {N(h)}{units}" + (band is { } b ? $", clears below {N(h - b)}{units} (deadband {N(b)})" : ""),
            BacnetEventStates.EVENT_STATE_LOW_LIMIT when e.LowLimit is { } l =>
                $"low limit {N(l)}{units}" + (band is { } b ? $", clears above {N(l + b)}{units} (deadband {N(b)})" : ""),
            _ => null,
        };
        if (limit is null) return reads.Length == 0 ? "" : reads + ".";
        return reads.Length == 0 ? char.ToUpperInvariant(limit[0]) + limit[1..] + "." : $"{reads}; {limit}.";
    }

    /// <summary>What the event means, its likely cause and the next step, in a tech's words.</summary>
    public static PointProblem Explain(ActiveEvent e)
    {
        var detail = LimitDetail(e);
        var p = e.State switch
        {
            BacnetEventStates.EVENT_STATE_HIGH_LIMIT => new PointProblem("Above its high alarm limit",
                "The equipment is not keeping up (or is driving too hard), the sensor reads wrong, or the limit is set too tight.",
                "Check the equipment serving this point and compare the sensor with your own meter. If both are right, review the limit with whoever owns the alarm setup."),
            BacnetEventStates.EVENT_STATE_LOW_LIMIT => new PointProblem("Below its low alarm limit",
                "The equipment is not keeping up (or is driving too hard), the sensor reads wrong, or the limit is set too tight.",
                "Check the equipment serving this point and compare the sensor with your own meter. If both are right, review the limit with whoever owns the alarm setup."),
            BacnetEventStates.EVENT_STATE_OFFNORMAL => new PointProblem("In its alarm state",
                "The point is in a state the controller is set to alarm on: for example a fan proof lost, a filter dirty, or a command that did not take.",
                "Check the equipment this point watches. The point's Alarm_Value (or the controller's alarm setup) says which state counts as an alarm."),
            BacnetEventStates.EVENT_STATE_FAULT => FaultProblem(e),
            BacnetEventStates.EVENT_STATE_LIFE_SAFETY_ALARM => new PointProblem("Life-safety alarm",
                "A life-safety device (smoke, fire, gas) is reporting an alarm.",
                "Follow the site's life-safety procedure. Do not acknowledge it from BACprobe unless you are authorised to."),
            _ => new PointProblem("Back to normal, not yet acknowledged",
                "The point went into alarm or fault earlier and has recovered since. The controller keeps it listed until someone acknowledges it.",
                "Find out what happened at the time shown, then acknowledge it if the site's procedure allows."),
        };
        return detail.Length == 0 ? p : p with { Text = $"{p.Text}. {detail}" };
    }

    private static PointProblem FaultProblem(ActiveEvent e)
    {
        // Same wording as the Status column, so the two never disagree about a broken sensor.
        var fault = PointHealth.Problems(BacnetStatusFlags.STATUS_FLAG_FAULT, e.Reliability is > 0 ? e.Reliability : null)[0];
        return fault;
    }

    /// <summary>The tooltip and detail panel: the explanation, plus what is waiting to be acknowledged.</summary>
    public static string Explanation(ActiveEvent e)
    {
        var p = Explain(e);
        var nl = Environment.NewLine;
        var text = $"{p.Text}{nl}Likely cause: {p.Cause}{nl}Next step: {p.NextStep}";
        if (e.NeedsAck) text += $"{nl}{nl}Not acknowledged: {e.UnackedText}.";
        return text;
    }
}
