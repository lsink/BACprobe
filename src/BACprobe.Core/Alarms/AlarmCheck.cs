using System.IO.BACnet;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Alarms;

/// <summary>Sums up an alarm scan and words what could not be checked. Pure.</summary>
public static class AlarmCheck
{
    /// <summary>"4 events on 2 devices: 1 fault, 2 in alarm, 1 back to normal; 3 not acknowledged." or "No active or unacknowledged alarms on 5 devices."</summary>
    public static string Summarise(IReadOnlyList<DeviceAlarms> results)
    {
        var events = results.SelectMany(r => r.Events).ToList();
        var asked = results.Count(r => r.Error is null);
        var devicesWord = asked == 1 ? "device" : "devices";
        if (events.Count == 0)
            return asked == 0 ? "No device could be asked for its alarms." : $"No active or unacknowledged alarms on {asked} {devicesWord}.";

        var withEvents = events.Select(e => e.Device.InstanceId).Distinct().Count();
        var parts = new List<string>();
        void Count(Func<BacnetEventStates, bool> match, string one, string many)
        {
            var n = events.Count(e => match(e.State));
            if (n > 0) parts.Add($"{n} {(n == 1 ? one : many)}");
        }
        Count(s => s == BacnetEventStates.EVENT_STATE_LIFE_SAFETY_ALARM, "life-safety alarm", "life-safety alarms");
        Count(s => s == BacnetEventStates.EVENT_STATE_FAULT, "fault", "faults");
        Count(s => EventText.IsAlarmState(s) && s != BacnetEventStates.EVENT_STATE_LIFE_SAFETY_ALARM, "in alarm", "in alarm");
        Count(s => s == BacnetEventStates.EVENT_STATE_NORMAL, "back to normal", "back to normal");
        var unacked = events.Count(e => e.NeedsAck);
        return $"{events.Count} {(events.Count == 1 ? "event" : "events")} on {withEvents} {(withEvents == 1 ? "device" : "devices")}: {string.Join(", ", parts)}" +
               (unacked > 0 ? $"; {unacked} not acknowledged." : "; all acknowledged.");
    }

    /// <summary>Devices that could not be asked, or could only be partly asked, each with a likely cause and next step.</summary>
    public static IReadOnlyList<NetworkFinding> Findings(IReadOnlyList<DeviceAlarms> results)
    {
        var list = new List<NetworkFinding>();
        foreach (var r in results.OrderBy(r => r.Device.InstanceId))
        {
            var who = $"Device {r.Device.InstanceId}{(r.Device.ObjectName is { Length: > 0 } n ? $" \"{n}\"" : "")}";
            if (r.TimedOut)
                list.Add(new(FindingSeverity.Problem, $"{who} did not answer",
                    "Its alarms could not be read, so it may have alarms that are not listed here.",
                    "The device is offline or busy, the network dropped the request, or a router is not passing it on.",
                    "Check the device is powered and on the network (Scan again), then Refresh."));
            else if (r.Error is not null)
                list.Add(new(FindingSeverity.Warning, $"{who}: alarms could not be read",
                    r.Error,
                    "The device refused both the alarm list request and reading its points' status.",
                    "Check the alarms in the controller's own tool or the site's front-end."));
            else if (r.Source == AlarmSource.StatusFlags)
                list.Add(new(FindingSeverity.Info, $"{who} has no alarm list service",
                    "It refused GetEventInformation, so BACprobe listed the points that say they are in alarm or fault instead.",
                    "Older or smaller controllers often leave this service out.",
                    "An alarm that already went back to normal without being acknowledged will not show here: check the front-end's alarm list for those."));
        }
        return list;
    }
}
