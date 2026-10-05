using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Live;

/// <summary>One watch-list entry after the first read: its summary, or why it could not be read.</summary>
public sealed record WatchReading(WatchEntry Entry, ObjectSummary? Summary, string? Problem);

/// <summary>
/// Keeps a watch list current across devices: one live watcher per device (COV where the device supports it, polling for the rest),
/// all running together. Reports through events from background threads; the UI must marshal.
/// </summary>
public sealed class WatchSession(DiscoveryService service, IReadOnlyDictionary<uint, DiscoveredDevice> devices, LiveOptions options)
{
    /// <summary>A watched point's value, status or override changed.</summary>
    public event Action<uint, ObjectSummary>? PointChanged;

    /// <summary>A device's one-line status ("Live: 3 point(s) by COV"), or the reason it stopped answering.</summary>
    public event Action<uint, string>? DeviceStatus;

    /// <summary>Read every entry once (a device at a time, in parallel across devices) so the list can show real values straight away.</summary>
    public async Task<IReadOnlyList<WatchReading>> ReadAsync(IReadOnlyList<WatchEntry> entries, CancellationToken ct = default)
    {
        var results = new WatchReading[entries.Count];
        var byDevice = entries.Select((e, i) => (e, i)).GroupBy(x => x.e.Device).ToList();

        await Parallel.ForEachAsync(byDevice, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (group, token) =>
        {
            if (!devices.TryGetValue(group.Key, out var device))
            {
                foreach (var (e, i) in group)
                    results[i] = new WatchReading(e, null, $"Device {group.Key} did not answer the scan. Likely cause: it is off, moved, or on another network. Next step: Scan again.");
                return;
            }
            try
            {
                var summaries = await service.OpenDevice(device).ReadSummariesAsync([.. group.Select(x => x.e.Point)], ct: token);
                var byId = summaries.ToDictionary(s => s.Id);
                foreach (var (e, i) in group)
                    results[i] = byId.TryGetValue(e.Point, out var s)
                        ? new WatchReading(e, s, null)
                        : new WatchReading(e, null, "The device did not return this point. Likely cause: it was deleted or renumbered.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                foreach (var (e, i) in group)
                    results[i] = new WatchReading(e, null, $"Could not read device {group.Key}: {ex.Message} Likely cause: it stopped answering. Next step: check the network and Scan again.");
            }
        });
        return results;
    }

    /// <summary>Run live updates for the readings that have summaries until cancelled. Returns when every device's watcher has ended.</summary>
    public async Task RunAsync(IReadOnlyList<WatchReading> readings, CancellationToken ct)
    {
        var tasks = new List<Task>();
        foreach (var group in readings.Where(r => r.Summary is not null).GroupBy(r => r.Entry.Device))
        {
            if (!devices.TryGetValue(group.Key, out var device)) continue;
            var instance = group.Key;
            var watcher = service.CreateLiveWatcher(device, [.. group.Select(r => r.Summary!)], options);
            watcher.PointChanged += s => PointChanged?.Invoke(instance, s);
            watcher.StatusChanged += text => DeviceStatus?.Invoke(instance, text);
            tasks.Add(Task.Run(async () =>
            {
                var failure = await watcher.RunAsync(ct);
                if (failure is not null && !ct.IsCancellationRequested) DeviceStatus?.Invoke(instance, failure);
            }, CancellationToken.None));
        }
        await Task.WhenAll(tasks);
    }
}
