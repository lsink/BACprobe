using System.IO.BACnet;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Live;

/// <param name="Retryable">Refused only because the device did not answer: worth asking again once it does.</param>
public sealed record CovSubscribeResult(
    IReadOnlyList<BacnetObjectId> Subscribed,
    IReadOnlyDictionary<BacnetObjectId, string> Refused,
    string? DeviceWideReason,
    IReadOnlyList<BacnetObjectId> Retryable);

/// <summary>
/// Change-of-value subscriptions to one device: the device pushes a notification when a point changes, instead of us polling.
/// Subscriptions expire, so they are renewed at half their lifetime. Notifications arrive on library threads.
/// </summary>
public sealed class CovSession : IDisposable
{
    private readonly BacnetClient _client;
    private readonly DiscoveredDevice _device;
    private readonly uint _lifetimeSeconds;
    private readonly Dictionary<BacnetObjectId, uint> _processByObject = [];
    private readonly Dictionary<uint, BacnetObjectId> _objectByProcess = [];
    private readonly Lock _lock = new();

    // Process-wide, not per session: the device keys a subscription on (subscriber, process id, object), so a new session
    // that reused id 1 would have its subscription deleted by an old session's cancel when the user changes the interval.
    private static uint s_nextProcess;
    private Timer? _renewTimer;
    private bool _disposed;
    private int _renewing; // 1 while a renewal pass runs: a slow pass must not overlap the next one

    /// <summary>After this many timeouts in a row the device is treated as not answering, instead of waiting out every point.</summary>
    public const int MaxTimeoutsInARow = 3;

    public const string StoppedAnswering = "the device stopped answering subscriptions";

    public CovSession(BacnetClient client, DiscoveredDevice device, uint lifetimeSeconds = 300)
    {
        _client = client;
        _device = device;
        _lifetimeSeconds = lifetimeSeconds;
        _client.OnCOVNotification += OnNotification;
    }

    /// <summary>A subscribed point changed. Raised on a background thread.</summary>
    public event Action<BacnetObjectId, IList<BacnetPropertyValue>>? Notified;

    /// <summary>A renewal failed, so this point is no longer being pushed to us and should be polled.</summary>
    public event Action<BacnetObjectId, string>? Lost;

    public IReadOnlyCollection<BacnetObjectId> Subscribed
    {
        get { lock (_lock) return [.. _processByObject.Keys]; }
    }

    /// <summary>
    /// Subscribe to each point (unconfirmed notifications, so a flaky network never leaves the device waiting on acknowledgements).
    /// Stops early if the device refuses for a reason that applies to all points.
    /// </summary>
    public async Task<CovSubscribeResult> SubscribeAsync(IEnumerable<BacnetObjectId> ids, CancellationToken ct = default)
    {
        var ok = new List<BacnetObjectId>();
        var refused = new Dictionary<BacnetObjectId, string>();
        var retryable = new List<BacnetObjectId>();
        string? deviceWide = null;
        var timeouts = 0;

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (deviceWide is not null)
            {
                refused[id] = deviceWide;
                if (deviceWide == StoppedAnswering) retryable.Add(id);
                continue;
            }

            var process = Interlocked.Increment(ref s_nextProcess);
            try
            {
                await _client.SubscribeCOVAsync(_device.Address, id, process, cancel: false, issueConfirmedNotifications: false,
                    lifetime: _lifetimeSeconds, cancellationToken: ct);
                lock (_lock)
                {
                    _processByObject[id] = process;
                    _objectByProcess[process] = id;
                }
                ok.Add(id);
                timeouts = 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var r = CovRefusal.Explain(ex);
                refused[id] = r.Reason;
                if (CovRefusal.IsTimeout(ex))
                {
                    retryable.Add(id);
                    if (++timeouts >= MaxTimeoutsInARow) deviceWide = StoppedAnswering; // do not wait out every remaining point
                }
                else
                {
                    timeouts = 0;
                    if (r.StopTrying) deviceWide = r.Reason;
                }
            }
        }

        if (ok.Count > 0) ScheduleRenewal();
        return new CovSubscribeResult(ok, refused, deviceWide, retryable);
    }

    private void ScheduleRenewal()
    {
        lock (_lock)
        {
            if (_disposed || _renewTimer is not null) return;
            var every = TimeSpan.FromSeconds(Math.Max(5, _lifetimeSeconds / 2.0));
            _renewTimer = new Timer(_ => _ = RenewAllAsync(), null, every, every);
        }
    }

    private async Task RenewAllAsync()
    {
        if (Interlocked.Exchange(ref _renewing, 1) == 1) return; // the previous pass is still waiting on a slow device
        try
        {
            KeyValuePair<BacnetObjectId, uint>[] active;
            lock (_lock)
            {
                if (_disposed) return;
                active = [.. _processByObject];
            }

            var timeouts = 0;
            string? gaveUp = null;
            foreach (var (id, process) in active)
            {
                if (_disposed) return;
                string? reason = gaveUp;
                if (reason is null)
                {
                    try
                    {
                        await _client.SubscribeCOVAsync(_device.Address, id, process, cancel: false, issueConfirmedNotifications: false,
                            lifetime: _lifetimeSeconds);
                        timeouts = 0;
                    }
                    catch (Exception ex)
                    {
                        reason = CovRefusal.Explain(ex).Reason;
                        if (!CovRefusal.IsTimeout(ex)) timeouts = 0;
                        else if (++timeouts >= MaxTimeoutsInARow) gaveUp = StoppedAnswering; // the rest go to polling without waiting
                    }
                }
                if (reason is null) continue;

                lock (_lock)
                {
                    _processByObject.Remove(id);
                    _objectByProcess.Remove(process);
                }
                Lost?.Invoke(id, reason);
            }
        }
        finally { Interlocked.Exchange(ref _renewing, 0); }
    }

    private void OnNotification(BacnetClient sender, BacnetAddress adr, byte invokeId, uint subscriberProcess,
        BacnetObjectId initiatingDevice, BacnetObjectId monitored, uint timeRemaining, bool needConfirm,
        ICollection<BacnetPropertyValue> values, BacnetMaxSegments maxSegments)
    {
        // Only notifications from this device, for a subscription we made.
        if (initiatingDevice.type != BacnetObjectTypes.OBJECT_DEVICE || initiatingDevice.instance != _device.InstanceId) return;
        lock (_lock)
        {
            if (!_objectByProcess.TryGetValue(subscriberProcess, out var expected) || expected != monitored) return;
        }

        // A confirmed notification must be acknowledged or the device keeps retrying.
        if (needConfirm)
            sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_COV_NOTIFICATION, invokeId);

        Notified?.Invoke(monitored, [.. values]);
    }

    /// <summary>Stop renewing and tell the device we are leaving, so it stops sending. Best effort: gives up after a few seconds.</summary>
    public async Task CloseAsync()
    {
        KeyValuePair<BacnetObjectId, uint>[] active;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _renewTimer?.Dispose();
            _renewTimer = null;
            active = [.. _processByObject];
            _processByObject.Clear();
            _objectByProcess.Clear();
        }
        _client.OnCOVNotification -= OnNotification;

        var cancels = active.Select(async kv =>
        {
            try
            {
                await _client.SubscribeCOVAsync(_device.Address, kv.Key, kv.Value, cancel: true, issueConfirmedNotifications: false, lifetime: 0);
            }
            catch (Exception) { /* the subscription simply expires on its own */ }
        });
        await Task.WhenAny(Task.WhenAll(cancels), Task.Delay(TimeSpan.FromSeconds(3)));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _renewTimer?.Dispose();
            _renewTimer = null;
        }
        _client.OnCOVNotification -= OnNotification;
    }
}
