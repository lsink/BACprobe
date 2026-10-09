using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using BACprobe.Core.Alarms;
using BACprobe.Core.Bbmd;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Export;
using BACprobe.Core.Jobs;
using BACprobe.Core.Learning;
using BACprobe.Core.Live;
using BACprobe.Core.Networking;
using BACprobe.Core.Writing;
using System.IO.BACnet;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

/// <summary>
/// Live alarms over this connection: find each device's notification classes, confirm once, add BACprobe to them, pass notifications
/// on, and take BACprobe off again. The additions are tracked like overrides, so leaving the app offers to undo any left behind.
/// </summary>
internal sealed class AlarmLiveSession(DiscoveryService svc, DeviceWriter writer, Func<IReadOnlyList<AlarmListenRequest>, bool> confirm)
    : IAlarmLiveLink
{
    private readonly List<AlarmListenRequest> _active = [];

    public event Action<AlarmNotification>? Notified;

    private void Relay(AlarmNotification n) => Notified?.Invoke(n);

    public async Task<(bool Listening, string Message)> StartAsync(IReadOnlyList<DiscoveredDevice> devices)
    {
        if (svc.OwnRecipient is not { } me) return (false, "Live alarms work over BACnet/IP only for now. Use Refresh instead.");
        var requests = new List<AlarmListenRequest>();
        foreach (var d in devices)
        {
            try
            {
                var classes = await svc.ReadNotificationClassesAsync(d);
                if (classes.Count > 0) requests.Add(new AlarmListenRequest(d, d.DisplayName, classes, me));
            }
            catch (Exception) { /* a device that will not list its objects cannot be listened to; Refresh still covers it */ }
        }
        if (requests.Count == 0)
            return (false, "No device here has notification classes, so none can send its alarms to BACprobe. Likely cause: they do not " +
                           "report alarms themselves. Next step: use Refresh.");
        if (!confirm(requests)) return (false, "Cancelled. Nothing was changed.");

        svc.AlarmNotified += Relay;
        var problems = new List<string>();
        foreach (var r in requests)
        {
            var outcome = await writer.ListenForAlarmsAsync(r);
            if (outcome.Success) _active.Add(r);
            else problems.Add($"device {r.Device.InstanceId}: {outcome.Message.Split('\n')[0]}");
        }
        if (_active.Count == 0)
        {
            svc.AlarmNotified -= Relay;
            return (false, "No device accepted BACprobe as a recipient: " + string.Join("; ", problems));
        }
        return (true, $"Live: {_active.Count} device(s) send their alarms here as they happen." +
                      (problems.Count == 0 ? "" : $" Not live: {string.Join("; ", problems)}"));
    }

    public async Task<string> StopAsync()
    {
        svc.AlarmNotified -= Relay;
        var failed = 0;
        foreach (var r in _active.ToList())
        {
            var outcome = await writer.ListenForAlarmsAsync(r with { Stop = true });
            if (outcome.Success) _active.Remove(r); else failed++;
        }
        return failed == 0 ? "Live alarms stopped; BACprobe took itself off the recipient lists."
            : $"{failed} device(s) still list BACprobe. Likely cause: they stopped answering. Next step: leaving the app offers to remove it again.";
    }
}
