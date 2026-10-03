using System.Diagnostics;
using System.IO.BACnet;
using System.Threading.Channels;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Live;

public sealed record LiveOptions(TimeSpan PollInterval, bool UseCov = true)
{
    /// <summary>
    /// Even points that push COV are re-read this often: a notification can be lost (UDP), a device restart drops
    /// subscriptions without telling us, and priority arrays are not part of COV.
    /// </summary>
    public TimeSpan SafetyPoll { get; init; } = TimeSpan.FromSeconds(30);

    public uint CovLifetimeSeconds { get; init; } = 300;
}

/// <summary>
/// Keeps a device's points up to date: COV where the device supports it, polling for the rest, and a slow safety poll
/// for everything. Reports changes by updating the <see cref="ObjectSummary"/> objects in place and raising events
/// (on background threads: the UI must marshal).
/// </summary>
public sealed class LiveWatcher(BacnetClient client, DiscoveredDevice device, DeviceBrowser browser,
    IReadOnlyList<ObjectSummary> points, LiveOptions options)
{
    private const int MaxConsecutiveFailures = 3;

    private readonly Lock _lock = new();
    private readonly HashSet<BacnetObjectId> _covSet = [];
    private string? _deviceWideRefusal;
    private DateTime? _lastUpdate;
    private volatile bool _ready; // false while subscribing, so the status does not flash "polling" before COV is set up

    /// <summary>A point's value or override changed.</summary>
    public event Action<ObjectSummary>? PointChanged;

    /// <summary>The one-line status for the screen changed.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>The one-line description of how points are being kept current.</summary>
    public static string DescribeMode(bool useCov, int covCount, int polledCount, TimeSpan pollInterval, string? deviceWideRefusal)
    {
        var every = $"{pollInterval.TotalSeconds:0.#} s";
        if (!useCov) return $"Live: polling every {every}";
        if (covCount == 0)
            return deviceWideRefusal is null
                ? $"Live: polling every {every}"
                : $"Live: polling every {every} ({deviceWideRefusal})";
        return polledCount == 0
            ? $"Live: {covCount} point(s) by COV"
            : $"Live: {covCount} by COV, {polledCount} polled every {every}";
    }

    /// <summary>
    /// Run until cancelled. Returns null on a normal stop, or a message (with likely cause and next step)
    /// if the device stopped answering and the watch gave up.
    /// </summary>
    public async Task<string?> RunAsync(CancellationToken ct)
    {
        var live = points.Where(p => BacnetNames.HasLivePresentValue(p.Id.type)).ToList();
        var byId = live.ToDictionary(p => p.Id);
        var pending = Channel.CreateUnbounded<ObjectSummary>();
        CovSession? cov = null;
        Task? consumer = null;

        void Publish()
        {
            if (!_ready) return;
            int covCount, polled;
            lock (_lock)
            {
                covCount = _covSet.Count;
                polled = live.Count - covCount;
            }
            var text = DescribeMode(options.UseCov, covCount, polled, options.PollInterval, _deviceWideRefusal);
            if (_lastUpdate is { } t) text += $"  (updated {t:HH:mm:ss})";
            StatusChanged?.Invoke(text);
        }

        void Changed(ObjectSummary s)
        {
            _lastUpdate = DateTime.Now;
            PointChanged?.Invoke(s);
        }

        try
        {
            if (options.UseCov && live.Count > 0)
            {
                cov = new CovSession(client, device, options.CovLifetimeSeconds);
                cov.Notified += (id, values) =>
                {
                    if (!byId.TryGetValue(id, out var s)) return;
                    if (s.ApplyLive(CovNotification.PresentValueText(id.type, values), null)) Changed(s);
                    // A commandable point's priority array is not in the notification: fetch it so overrides show up too.
                    if (PriorityArrayInfo.MayHavePriorityArray(id.type)) pending.Writer.TryWrite(s);
                    Publish();
                };
                cov.Lost += (id, _) =>
                {
                    lock (_lock) _covSet.Remove(id); // falls back to polling
                    Publish();
                };

                StatusChanged?.Invoke("Live: subscribing to COV...");
                var result = await cov.SubscribeAsync(live.Select(p => p.Id), ct);
                lock (_lock)
                {
                    foreach (var id in result.Subscribed) _covSet.Add(id);
                    _deviceWideRefusal = result.DeviceWideReason;
                }
            }

            _ready = true;
            consumer = Task.Run(async () =>
            {
                // Priority-array refreshes triggered by notifications. Bursts are batched.
                await foreach (var first in pending.Reader.ReadAllAsync(ct))
                {
                    var batch = new HashSet<ObjectSummary> { first };
                    while (batch.Count < 16 && pending.Reader.TryRead(out var more)) batch.Add(more);
                    try
                    {
                        foreach (var s in await browser.RefreshValuesAsync([.. batch], ct)) Changed(s);
                        Publish();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { /* the safety poll will catch up */ }
                }
            }, ct);

            Publish();
            var failures = 0;
            var sinceSafety = Stopwatch.StartNew();
            using var timer = new PeriodicTimer(options.PollInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                List<ObjectSummary> due;
                lock (_lock) due = [.. live.Where(p => !_covSet.Contains(p.Id))];
                var safety = _covSet.Count > 0 && sinceSafety.Elapsed >= options.SafetyPoll;
                if (safety)
                {
                    due = live;
                    sinceSafety.Restart();
                }
                if (due.Count == 0) continue;

                try
                {
                    foreach (var s in await browser.RefreshValuesAsync(due, ct)) Changed(s);
                    failures = 0;
                    _lastUpdate = DateTime.Now;
                    Publish();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (++failures < MaxConsecutiveFailures)
                    {
                        StatusChanged?.Invoke("Live: no answer, retrying...");
                        continue;
                    }
                    return $"Live values stopped: the device did not answer {MaxConsecutiveFailures} times in a row ({ex.Message}). " +
                           "Likely cause: network drop, or the controller is busy or restarting. " +
                           "Next step: check the connection, then switch Live values back on.";
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            pending.Writer.TryComplete();
            if (cov is not null) await cov.CloseAsync();
            if (consumer is not null)
            {
                try { await consumer; }
                catch (OperationCanceledException) { }
            }
        }
        return null;
    }
}
