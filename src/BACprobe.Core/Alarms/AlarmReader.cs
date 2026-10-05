using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Alarms;

/// <summary>
/// Reads the active and unacknowledged alarms of devices: GetEventInformation where the device supports it, otherwise the
/// points whose Status_Flags say in alarm or fault. Then reads each alarmed point's name, value and limits so the event can
/// be explained. Reads only.
/// </summary>
public sealed class AlarmReader(BacnetClient client)
{
    private const int Parallel = 8;

    private static readonly BacnetPropertyIds[] EventProps =
        [BacnetPropertyIds.PROP_EVENT_STATE, BacnetPropertyIds.PROP_ACKED_TRANSITIONS, BacnetPropertyIds.PROP_EVENT_TIME_STAMPS];

    private static readonly BacnetPropertyIds[] LimitProps =
        [BacnetPropertyIds.PROP_HIGH_LIMIT, BacnetPropertyIds.PROP_LOW_LIMIT, BacnetPropertyIds.PROP_DEADBAND];

    /// <summary>Every device, a few at a time. Each result is reported as it arrives; the list comes back in device order.</summary>
    public async Task<IReadOnlyList<DeviceAlarms>> ReadAllAsync(IReadOnlyList<DiscoveredDevice> devices, IProgress<DeviceAlarms>? progress = null,
        CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Parallel);
        var tasks = devices.Select(async d =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await ReadAsync(d, ct);
                progress?.Report(r);
                return r;
            }
            finally { gate.Release(); }
        }).ToList();
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// One device's alarms. Never throws for a device problem: the result says what went wrong. Always reads fresh, never from a
    /// saved point list: a stale Status_Flags would hide an alarm that started since.
    /// </summary>
    public async Task<DeviceAlarms> ReadAsync(DiscoveredDevice device, CancellationToken ct = default)
    {
        List<ActiveEvent> events;
        AlarmSource source;
        try
        {
            var list = await client.GetEventsAsync(device.Address, cancellationToken: ct);
            events = list.Select(e => FromEventInformation(device, e)).ToList();
            source = AlarmSource.EventInformation;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (BacnetFailure.IsTimeout(ex)) return new DeviceAlarms(device, [], null, "The device did not answer.", TimedOut: true);
            // Refused (the library reports any refusal as "Service not available"): look at the points instead.
            try
            {
                events = await FromStatusFlagsAsync(device, ct);
                source = AlarmSource.StatusFlags;
            }
            catch (Exception inner) when (!ct.IsCancellationRequested)
            {
                return BacnetFailure.IsTimeout(inner)
                    ? new DeviceAlarms(device, [], null, "The device stopped answering.", TimedOut: true)
                    : new DeviceAlarms(device, [], null, $"It refused the alarm list ({ex.Message}) and reading its points ({inner.Message}).");
            }
        }

        try { await DescribeAsync(device, events, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { /* the events still stand without names and limits */ }
        return new DeviceAlarms(device, EventText.Sort(events), source);
    }

    /// <summary>The same events read again (after an acknowledgement), or none if the device no longer lists it.</summary>
    public async Task<ActiveEvent?> RereadAsync(ActiveEvent e, CancellationToken ct = default)
    {
        var r = await ReadAsync(e.Device, ct);
        if (r.TimedOut) throw new TimeoutException(r.Error);
        return r.Events.FirstOrDefault(x => x.Point.Equals(e.Point));
    }

    private static ActiveEvent FromEventInformation(DiscoveredDevice device, BacnetGetEventInformationData e) => new()
    {
        Device = device,
        Point = e.objectIdentifier,
        State = e.eventState,
        Acked = EventText.AckedFrom(e.acknowledgedTransitions),
        TimeStamps = ThreeStamps(e.eventTimeStamps),
        NotifyType = e.notifyType,
        Priorities = e.eventPriorities is { Length: > 0 } p ? p : null,
        Source = AlarmSource.EventInformation,
    };

    /// <summary>Always three stamps; a missing one counts as "never happened" (all unspecified, as BACnet sends it).</summary>
    private static BacnetGenericTime[] ThreeStamps(IReadOnlyList<BacnetGenericTime>? stamps) =>
        [.. Enumerable.Range(0, 3).Select(i => stamps is not null && i < stamps.Count ? stamps[i] : EventText.Never)];

    /// <summary>The fallback: points that say they are in alarm or fault, with their event properties where the device has them.</summary>
    private async Task<List<ActiveEvent>> FromStatusFlagsAsync(DiscoveredDevice device, CancellationToken ct)
    {
        var browser = new DeviceBrowser(client, device);
        var ids = (await browser.ReadObjectListAsync(ct)).Where(id => BacnetNames.HasLivePresentValue(id.type)).ToList();
        var points = await browser.ReadSummariesAsync(ids, ct: ct);

        var events = new List<ActiveEvent>();
        foreach (var p in points.Where(p => p.IsInAlarm || p.IsFault))
        {
            var props = await ReadPropsAsync(device, p.Id, EventProps, ct);
            var state = props.TryGetValue(BacnetPropertyIds.PROP_EVENT_STATE, out var s) && s is [{ Value: IConvertible c }]
                && s[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED
                ? (BacnetEventStates)Convert.ToUInt32(c, System.Globalization.CultureInfo.InvariantCulture)
                : p.IsFault ? BacnetEventStates.EVENT_STATE_FAULT : BacnetEventStates.EVENT_STATE_OFFNORMAL;
            var acked = props.TryGetValue(BacnetPropertyIds.PROP_ACKED_TRANSITIONS, out var a) && a is [{ Value: BacnetBitString bits }]
                ? EventText.AckedFrom(bits)
                : [true, true, true]; // no event properties: nothing BACprobe can acknowledge
            var stamps = props.TryGetValue(BacnetPropertyIds.PROP_EVENT_TIME_STAMPS, out var t)
                ? t.Where(v => v.Value is BacnetGenericTime).Select(v => (BacnetGenericTime)v.Value).ToList()
                : null;
            events.Add(new ActiveEvent
            {
                Device = device,
                Point = p.Id,
                State = state,
                Acked = acked,
                TimeStamps = ThreeStamps(stamps),
                Source = AlarmSource.StatusFlags,
            });
        }
        return events;
    }

    /// <summary>Name, value, status and (for limit alarms) the limits of each event's point.</summary>
    private async Task DescribeAsync(DiscoveredDevice device, List<ActiveEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return;
        var ids = events.Select(e => e.Point).Distinct().ToList();
        var known = (await new DeviceBrowser(client, device).ReadSummariesAsync(ids, ct: ct)).ToDictionary(s => s.Id);

        foreach (var e in events)
        {
            if (known.TryGetValue(e.Point, out var s))
            {
                e.Name = s.Name;
                e.ValueText = s.ValueText;
                e.Units = s.Units;
                e.StatusFlags = s.StatusFlags;
                e.Reliability = s.Reliability;
            }
            if (e.State is BacnetEventStates.EVENT_STATE_HIGH_LIMIT or BacnetEventStates.EVENT_STATE_LOW_LIMIT)
            {
                var limits = await ReadPropsAsync(device, e.Point, LimitProps, ct);
                e.HighLimit = Real(limits, BacnetPropertyIds.PROP_HIGH_LIMIT);
                e.LowLimit = Real(limits, BacnetPropertyIds.PROP_LOW_LIMIT);
                e.Deadband = Real(limits, BacnetPropertyIds.PROP_DEADBAND);
            }
        }
    }

    private static float? Real(Dictionary<BacnetPropertyIds, IList<BacnetValue>> props, BacnetPropertyIds p) =>
        props.TryGetValue(p, out var v) && v is [{ Value: float f }] ? f : null;

    /// <summary>
    /// A few properties of one object: ReadPropertyMultiple, else one at a time. Properties the object does not have are
    /// left out; a device that stops answering throws.
    /// </summary>
    private async Task<Dictionary<BacnetPropertyIds, IList<BacnetValue>>> ReadPropsAsync(DiscoveredDevice device, BacnetObjectId id,
        IReadOnlyList<BacnetPropertyIds> props, CancellationToken ct)
    {
        var result = new Dictionary<BacnetPropertyIds, IList<BacnetValue>>();
        static bool IsError(IList<BacnetValue>? v) => v is null || v.Count == 0 || v[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR;
        try
        {
            var spec = new BacnetReadAccessSpecification(id, props.Select(p => new BacnetPropertyReference(p, ASN1.BACNET_ARRAY_ALL)).ToList());
            foreach (var r in await client.ReadPropertyMultipleAsync(device.Address, [spec], cancellationToken: ct))
                foreach (var pv in r.values)
                    if (!IsError(pv.value)) result[(BacnetPropertyIds)pv.property.propertyIdentifier] = pv.value;
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && !BacnetFailure.IsTimeout(ex)) { /* refused: one at a time */ }

        foreach (var p in props)
        {
            try
            {
                var v = await client.ReadPropertyAsync(device.Address, id, p, cancellationToken: ct);
                if (!IsError(v)) result[p] = v;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && !BacnetFailure.IsTimeout(ex)) { /* the object does not have it */ }
        }
        return result;
    }
}
