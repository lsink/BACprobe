using BACprobe.Core.Browsing;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>Print a line whenever a point's value or override changes. Ctrl+C to stop.</summary>
    private static async Task<int> WatchAsync(Dictionary<string, string?> opts)
    {
        var interval = IntOpt(opts, "interval", 2);
        if (interval < 1) throw new ArgumentException("--interval is in seconds, 1 or more.");

        var (svc, device, error) = await ConnectToDeviceAsync(opts);
        if (svc is null || device is null) return Fail(error!);
        using var _ = svc;

        var browser = svc.OpenDevice(device);
        IReadOnlyList<ObjectSummary> points;
        try
        {
            var ids = await browser.ReadObjectListAsync();
            points = await browser.ReadSummariesAsync(ids);
        }
        catch (Exception ex)
        {
            return Fail(ReadFailure(ex));
        }

        // Optional filter, e.g. --object ai:1
        if (opts.TryGetValue("object", out var spec) && spec is not null)
        {
            if (!BacnetNames.TryParseObject(spec, out var id))
                throw new ArgumentException($"'{spec}' is not an object. Use type:instance, e.g. ai:1.");
            points = [.. points.Where(p => p.Id == id)];
            if (points.Count == 0) return Fail($"Device {device.InstanceId} has no {spec}.\n  Next step: run 'bacprobe objects --device {device.InstanceId}'.");
        }

        var watched = points.Where(p => BacnetNames.HasLivePresentValue(p.Id.type)).ToList();
        Console.WriteLine($"Watching {watched.Count} point(s) on device {device.InstanceId} every {interval}s. Ctrl+C to stop.");
        foreach (var p in watched)
            Console.WriteLine($"  {BacnetNames.ObjectTypeShort(p.Id.type)} {p.Id.instance,-3} {p.Name,-22} {p.ValueText}{(p.IsOverridden ? $"   [override {p.OverrideText}]" : "")}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        var failures = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cts.Token))
            {
                try
                {
                    var before = watched.ToDictionary(p => p.Id, p => (p.ValueText, p.OverrideText));
                    var changed = await browser.RefreshValuesAsync(watched, cts.Token);
                    failures = 0;
                    foreach (var p in changed)
                    {
                        var (oldValue, oldOverride) = before[p.Id];
                        var note = p.OverrideText != oldOverride
                            ? (p.IsOverridden ? $"   [override now {p.OverrideText}]" : "   [override released]")
                            : "";
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {BacnetNames.ObjectTypeShort(p.Id.type)} {p.Id.instance,-3} {p.Name,-22} {oldValue} -> {p.ValueText}{note}");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (++failures >= 3)
                        return Fail("Lost the device (3 failed refreshes in a row).\n" + ReadFailure(ex));
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  (no answer, will retry)");
                }
            }
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}
