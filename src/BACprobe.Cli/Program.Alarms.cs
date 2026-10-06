using System.IO.BACnet;
using BACprobe.Core.Alarms;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Writing;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>
    /// List the active and unacknowledged alarms of every device (or one), each with a likely cause and next step.
    /// With --ack, acknowledge one point's alarm after a plain-English confirmation; that is logged like a write.
    /// </summary>
    private static async Task<int> AlarmsAsync(Dictionary<string, string?> opts)
    {
        BacnetObjectId? ack = null;
        if (opts.TryGetValue("ack", out var spec))
        {
            if (spec is null || !BacnetNames.TryParseObject(spec, out var id))
                throw new ArgumentException("Say which point's alarm to acknowledge, e.g. --ack ai:1 --device 1001.");
            if (!opts.ContainsKey("device")) throw new ArgumentException("--ack needs --device <instance> too.");
            ack = id;
        }

        int low = -1, high = -1;
        if (opts.TryGetValue("device", out var text))
        {
            if (!int.TryParse(text, out low) || low is < 0 or > 4194302)
                throw new ArgumentException("--device needs a device instance number. 'bacprobe discover' lists them.");
            high = low;
        }

        var (svc, error) = await OpenSessionAsync(opts);
        if (svc is null) return Fail(error!);
        using var _ = svc;

        var wait = IntOpt(opts, "wait", low >= 0 ? 3 : 5);
        var devices = await svc.WhoIsAsync(low, high, TimeSpan.FromSeconds(wait));
        if (devices.Count == 0)
            return Fail((low >= 0 ? $"Device {low} did not answer Who-Is." : "No devices answered Who-Is.") + "\n" +
                        "  Likely cause: wrong instance number, wrong adapter/subnet, or the devices are behind a router/BBMD.\n" +
                        "  Next step:    run 'bacprobe discover' to list the devices that do answer, or try a longer --wait.");
        await svc.EnrichAsync(devices);

        var reader = svc.CreateAlarmReader();
        if (ack is { } point) return await AcknowledgeAsync(svc, reader, devices[0], point, opts);
        if (opts.ContainsKey("listen")) return await ListenAsync(svc, devices, opts);

        Console.WriteLine($"Asking {devices.Count} device(s) for their alarms...");
        var results = await reader.ReadAllAsync(devices);
        var events = EventText.Sort(results.SelectMany(r => r.Events));

        Console.WriteLine();
        Console.WriteLine(AlarmCheck.Summarise(results));
        if (events.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{"Device",-8} {"Object",-8} {"Name",-24} {"State",-18} {"Since",-20} Not acknowledged");
            foreach (var e in events)
            {
                Console.WriteLine($"{e.Device.InstanceId,-8} {e.ShortId,-8} {Truncate(e.DisplayName, 24),-24} {e.StateText,-18} " +
                                  $"{(e.SinceText.Length > 0 ? e.SinceText : "-"),-20} {(e.NeedsAck ? e.UnackedText : "-")}");
                var why = EventText.Explain(e);
                Console.WriteLine($"         {why.Text}");
                Console.WriteLine($"         Likely cause: {why.Cause}");
                Console.WriteLine($"         Next step:    {why.NextStep}");
            }
            if (events.Any(e => e.NeedsAck))
                Console.WriteLine("\nTo acknowledge one: bacprobe alarms --device <n> --ack <type:n> (asks you to confirm; logged like a write).");
        }

        var findings = AlarmCheck.Findings(results);
        if (findings.Count > 0)
        {
            Console.WriteLine();
            PrintFindings(findings);
        }
        return events.Count > 0 ? 5 : 0;
    }

    /// <summary>
    /// Add BACprobe to each device's alarm recipient lists (after confirming), print alarms as they arrive, and take BACprobe off again
    /// when done (after --minutes, default 10, or Ctrl+C).
    /// </summary>
    private static async Task<int> ListenAsync(DiscoveryService svc, IReadOnlyList<DiscoveredDevice> devices, Dictionary<string, string?> opts)
    {
        if (svc.OwnRecipient is not { } me)
            return Fail("Live alarms work over BACnet/IP only for now.\n  Next step:    use 'bacprobe alarms' to read them by hand.");
        var minutes = IntOpt(opts, "minutes", 10);
        if (minutes is < 1 or > 240) throw new ArgumentException("--minutes must be between 1 and 240.");

        var requests = new List<AlarmListenRequest>();
        foreach (var d in devices)
        {
            try
            {
                var classes = await svc.ReadNotificationClassesAsync(d);
                if (classes.Count > 0) requests.Add(new AlarmListenRequest(d, d.ObjectName ?? $"device {d.InstanceId}", classes, me));
                else Console.WriteLine($"Device {d.InstanceId}: no notification classes, so it does not send alarms itself. Skipped.");
            }
            catch (Exception ex) { Console.WriteLine($"Device {d.InstanceId}: could not read its objects ({ex.Message}). Skipped."); }
        }
        if (requests.Count == 0) return Fail("No device here can send its alarms to BACprobe.\n  Next step:    use 'bacprobe alarms' to read them by hand.");

        Console.WriteLine();
        foreach (var r in requests) PrintExplanation(Prompts.ForAlarmListen(r));
        if (!opts.ContainsKey("yes"))
        {
            if (Console.IsInputRedirected)
                return Fail("This needs a person to confirm. Run it in a terminal, or add --yes if you are scripting it.");
            Console.Write($"Type y to add BACprobe to {requests.Sum(r => r.NotificationClasses.Count)} recipient list(s) on {requests.Count} device(s): ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Cancelled. Nothing was changed.");
                return 0;
            }
        }

        var writer = svc.CreateWriter(new WriteLog(WriteLog.DefaultPath), new OverrideTracker());
        svc.AlarmNotified += n => Console.WriteLine(n.Text);
        var listening = new List<AlarmListenRequest>();
        foreach (var r in requests)
        {
            var outcome = await writer.ListenForAlarmsAsync(r);
            if (outcome.Success) listening.Add(r);
            else Console.WriteLine($"Device {r.Device.InstanceId}: {outcome.Message}");
        }
        if (listening.Count == 0) return Fail("No device accepted BACprobe as a recipient.");

        Console.WriteLine($"\nListening for alarms for {minutes} minute(s). Ctrl+C stops early; BACprobe then takes itself off the lists.");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
        ConsoleCancelEventHandler stop = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += stop;
        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }
        Console.CancelKeyPress -= stop;

        Console.WriteLine("\nTaking BACprobe off the recipient lists...");
        var failed = 0;
        foreach (var r in listening)
        {
            var outcome = await writer.ListenForAlarmsAsync(r with { Stop = true });
            if (!outcome.Success)
            {
                failed++;
                Console.WriteLine($"Device {r.Device.InstanceId}: {outcome.Message}");
            }
        }
        Console.WriteLine(failed == 0 ? "Done. Logged to " + WriteLog.DefaultPath : $"{failed} device(s) still list BACprobe: remove it in the controller's tool.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> AcknowledgeAsync(DiscoveryService svc, AlarmReader reader, DiscoveredDevice device, BacnetObjectId point,
        Dictionary<string, string?> opts)
    {
        var result = await reader.ReadAsync(device);
        if (result.Error is not null) return Fail($"Could not read device {device.InstanceId}'s alarms: {result.Error}\n" +
                                                  "  Likely cause: the device stopped answering, or refuses alarm requests.\n" +
                                                  "  Next step:    run 'bacprobe alarms --device <n>' to see what it says.");
        var e = result.Events.FirstOrDefault(x => x.Point.Equals(point));
        if (e is null || !e.NeedsAck)
        {
            Console.WriteLine(e is null
                ? $"{BacnetNames.ObjectLabel(point)} on device {device.InstanceId} has no active or unacknowledged alarm. Nothing to do."
                : $"{e.DisplayName} ({e.ObjectLabel}) is {e.StateText.ToLowerInvariant()}, and already acknowledged. Nothing to do.");
            return 0;
        }

        var request = new AlarmAckRequest(e, device.ObjectName ?? $"device {device.InstanceId}", AlarmAckRequest.DefaultSource);
        Console.WriteLine();
        PrintExplanation(Prompts.ForAck(request));
        if (!opts.ContainsKey("yes"))
        {
            if (Console.IsInputRedirected)
                return Fail("This needs a person to confirm. Run it in a terminal, or add --yes if you are scripting it.");
            Console.Write("Type y to acknowledge, anything else cancels: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Cancelled. Nothing was sent.");
                return 0;
            }
        }

        var writer = svc.CreateWriter(new WriteLog(WriteLog.DefaultPath), new OverrideTracker());
        var outcome = await writer.AcknowledgeAsync(request);
        if (!outcome.Success) return Fail(outcome.Message);
        Console.WriteLine("Acknowledged.");
        try
        {
            var after = await reader.RereadAsync(e);
            Console.WriteLine(after is null
                ? "  The device no longer lists this alarm."
                : $"  Now: {after.StateText}{(after.NeedsAck ? $", still not acknowledged: {after.UnackedText}" : ", acknowledged")}.");
        }
        catch (Exception) { Console.WriteLine("  (Could not read the alarm back to check.)"); }
        Console.WriteLine($"Logged to {WriteLog.DefaultPath}");
        return 0;
    }
}
