using BACprobe.Core.Browsing;
using BACprobe.Core.Live;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>Print a line whenever a point's value or override changes. Uses COV where the device supports it. Ctrl+C to stop.</summary>
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
        var useCov = !opts.ContainsKey("poll");
        Console.WriteLine($"Watching {watched.Count} point(s) on device {device.InstanceId}{(useCov ? " (COV where supported)" : " (polling)")}. Ctrl+C to stop.");

        var last = watched.ToDictionary(p => p.Id, p => (Value: p.ValueText, Override: p.OverrideText, Status: p.ProblemText));
        foreach (var p in watched)
            Console.WriteLine($"  {BacnetNames.ObjectTypeShort(p.Id.type),-3} {p.Id.instance,-3} {p.Name,-22} {p.ValueText}" +
                              $"{(p.IsOverridden ? $"   [override {p.OverrideText}]" : "")}{(p.HasProblem ? $"   [{p.ProblemText}]" : "")}");

        var watcher = svc.CreateLiveWatcher(device, watched, new LiveOptions(TimeSpan.FromSeconds(interval), useCov) { CovLifetimeSeconds = (uint)IntOpt(opts, "cov-lifetime", 300) });
        var lastMode = "";
        var modeLock = new object();
        watcher.StatusChanged += text =>
        {
            var mode = text.Split("  (updated")[0]; // only print when the way points are being kept current changes
            lock (modeLock)
            {
                if (mode == lastMode) return;
                lastMode = mode;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {mode}");
            }
        };
        watcher.PointChanged += p =>
        {
            (string Value, string Override, string Status) before;
            lock (last) before = last[p.Id];
            var now = (Value: p.ValueText, Override: p.OverrideText, Status: p.ProblemText);
            if (now == before) return;
            lock (last) last[p.Id] = now;
            var note = now.Override != before.Override
                ? (p.IsOverridden ? $"   [override now {p.OverrideText}]" : "   [override released]")
                : "";
            if (now.Status != before.Status) note += p.HasProblem ? $"   [now {p.ProblemText}]" : "   [back to normal]";
            var change = before.Value == p.ValueText ? p.ValueText : $"{before.Value} -> {p.ValueText}";
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {BacnetNames.ObjectTypeShort(p.Id.type),-3} {p.Id.instance,-3} {p.Name,-22} {change}{note}");
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var failure = await watcher.RunAsync(cts.Token);
        return failure is null ? 0 : Fail(failure);
    }
}
