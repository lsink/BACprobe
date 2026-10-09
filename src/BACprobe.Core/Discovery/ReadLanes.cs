using System.IO.BACnet;

namespace BACprobe.Core.Discovery;

/// <summary>One device's result from <see cref="ReadLanes.RunAsync{T}"/>: what was read, or why not.</summary>
public sealed record LaneResult<T>(DiscoveredDevice Device, T? Value, Exception? Error);

/// <summary>
/// Reading every device of a big site without hammering a slow link. Devices on the IP network itself are read a few at a time in
/// parallel; devices behind the same router network (usually one MS/TP trunk) share a lane and are read one after another, because
/// a trunk at 38.4k is busy enough with one reader plus the front-end. Over an MS/TP connection everything is one lane.
/// </summary>
public static class ReadLanes
{
    /// <summary>Lanes read at the same time. Enough to cut a big IP site's read time a lot, few enough not to swamp a busy network.</summary>
    public const int DefaultParallel = 4;

    /// <summary>
    /// The lanes, each read in order. <paramref name="sharedMedium"/>: BACprobe itself is on one trunk (MS/TP), so everything shares it.
    /// Lanes keep the devices' original order inside them, and come out in the order of their first device.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<DiscoveredDevice>> Plan(IReadOnlyList<DiscoveredDevice> devices, bool sharedMedium)
    {
        if (sharedMedium) return devices.Count == 0 ? [] : [devices];
        return devices
            .Select((d, i) => (Device: d, Index: i, Key: d.Network == 0 ? $"ip:{d.InstanceId}:{i}" : $"net:{d.Network}"))
            .GroupBy(x => x.Key)
            .OrderBy(g => g.Min(x => x.Index))
            .Select(g => (IReadOnlyList<DiscoveredDevice>)[.. g.OrderBy(x => x.Index).Select(x => x.Device)])
            .ToList();
    }

    /// <summary>
    /// Read every device with <paramref name="read"/>, lane by lane, up to <paramref name="maxParallel"/> lanes at once. A device that fails
    /// does not stop the others. Results come back in the order of <paramref name="devices"/>.
    /// </summary>
    public static async Task<IReadOnlyList<LaneResult<T>>> RunAsync<T>(IReadOnlyList<DiscoveredDevice> devices, bool sharedMedium,
        Func<DiscoveredDevice, Task<T>> read, int maxParallel = DefaultParallel, CancellationToken ct = default)
    {
        var results = new LaneResult<T>?[devices.Count];
        var index = new Dictionary<DiscoveredDevice, int>(ReferenceEqualityComparer.Instance); // by reference: the same device object
        for (var i = 0; i < devices.Count; i++) index[devices[i]] = i;
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));
        var lanes = Plan(devices, sharedMedium).Select(async lane =>
        {
            await gate.WaitAsync(ct);
            try
            {
                foreach (var d in lane)
                {
                    ct.ThrowIfCancellationRequested();
                    LaneResult<T> r;
                    try { r = new LaneResult<T>(d, await read(d), null); }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { r = new LaneResult<T>(d, default, ex); }
                    results[index[d]] = r;
                }
            }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(lanes);
        return [.. results.Select(r => r!)];
    }

    /// <summary>"Reading 4 devices at a time" or "Reading one device at a time", for status lines.</summary>
    public static string Describe(IReadOnlyList<IReadOnlyList<DiscoveredDevice>> lanes, int maxParallel = DefaultParallel) =>
        Math.Min(lanes.Count, maxParallel) <= 1 ? "one device at a time" : $"{Math.Min(lanes.Count, maxParallel)} devices at a time";
}
