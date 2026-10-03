using System.Globalization;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Trends;

/// <summary>Reads a trend log's details and its recorded history from one device.</summary>
public sealed class TrendLogReader(BacnetClient client, DiscoveredDevice device)
{
    /// <summary>First request size. Devices with small packets answer with fewer records; the reader copes either way.</summary>
    private const uint FirstPageSize = 40;
    private const uint SmallestPageSize = 5;

    private static readonly BacnetPropertyIds[] InfoProps =
    [
        BacnetPropertyIds.PROP_OBJECT_NAME, BacnetPropertyIds.PROP_ENABLE, BacnetPropertyIds.PROP_LOG_INTERVAL,
        BacnetPropertyIds.PROP_RECORD_COUNT, BacnetPropertyIds.PROP_TOTAL_RECORD_COUNT, BacnetPropertyIds.PROP_BUFFER_SIZE,
        BacnetPropertyIds.PROP_LOG_DEVICE_OBJECT_PROPERTY, BacnetPropertyIds.PROP_STOP_WHEN_FULL,
    ];

    /// <summary>The log's own settings and counters. Each one the device will not give us comes back null; this never throws for that.</summary>
    public async Task<TrendLogInfo> ReadInfoAsync(BacnetObjectId log, CancellationToken ct = default)
    {
        var values = new Dictionary<BacnetPropertyIds, BacnetValue>();

        try
        {
            var refs = InfoProps.Select(p => new BacnetPropertyReference(p, ASN1.BACNET_ARRAY_ALL)).ToList();
            foreach (var result in await client.ReadPropertyMultipleAsync(device.Address, log, refs, cancellationToken: ct))
                foreach (var pv in result.values)
                    Keep(values, (BacnetPropertyIds)pv.property.propertyIdentifier, pv.value);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            foreach (var p in InfoProps) // no RPM: one at a time
            {
                try { Keep(values, p, await client.ReadPropertyAsync(device.Address, log, p, cancellationToken: ct)); }
                catch (Exception) when (!ct.IsCancellationRequested) { /* this property is not offered */ }
            }
        }

        string? source = null, units = null;
        if (values.TryGetValue(BacnetPropertyIds.PROP_LOG_DEVICE_OBJECT_PROPERTY, out var src) && src.Value is BacnetDeviceObjectPropertyReference r)
        {
            source = BacnetNames.ObjectLabel(r.objectIdentifier);
            (source, units) = await DescribeSourceAsync(r, source, ct);
        }

        var intervalCs = UInt(values, BacnetPropertyIds.PROP_LOG_INTERVAL);
        return new TrendLogInfo(
            Text(values, BacnetPropertyIds.PROP_OBJECT_NAME),
            UInt(values, BacnetPropertyIds.PROP_RECORD_COUNT),
            UInt(values, BacnetPropertyIds.PROP_TOTAL_RECORD_COUNT),
            UInt(values, BacnetPropertyIds.PROP_BUFFER_SIZE),
            Bool(values, BacnetPropertyIds.PROP_ENABLE),
            intervalCs is > 0 ? TimeSpan.FromMilliseconds(intervalCs.Value * 10.0) : null, // BACnet counts hundredths of a second; 0 means "logs on change"
            source, units,
            Bool(values, BacnetPropertyIds.PROP_STOP_WHEN_FULL));
    }

    /// <summary>Name and units of the point being logged, when it lives on this same device. Best effort.</summary>
    private async Task<(string Source, string? Units)> DescribeSourceAsync(BacnetDeviceObjectPropertyReference r, string fallback, CancellationToken ct)
    {
        var elsewhere = r.deviceIdentifier.type == BacnetObjectTypes.OBJECT_DEVICE && r.deviceIdentifier.instance != device.InstanceId
                        && r.deviceIdentifier.instance != 0;
        if (elsewhere) return ($"{fallback} on device {r.deviceIdentifier.instance}", null);

        string? name = null, units = null;
        try
        {
            var n = await client.ReadPropertyAsync(device.Address, r.objectIdentifier, BacnetPropertyIds.PROP_OBJECT_NAME, cancellationToken: ct);
            name = n.FirstOrDefault().Value as string;
            var u = await client.ReadPropertyAsync(device.Address, r.objectIdentifier, BacnetPropertyIds.PROP_UNITS, cancellationToken: ct);
            if (u.Count > 0 && u[0].Value is IConvertible code)
                units = BacnetNames.UnitsName(Convert.ToUInt32(code, CultureInfo.InvariantCulture));
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* a binary point has no units; fine */ }
        return (name is null ? fallback : $"{name} ({fallback})", string.IsNullOrEmpty(units) ? null : units);
    }

    /// <summary>
    /// Read recorded history by position: the most recent <paramref name="last"/> records, or all of them when null.
    /// Reads in pages, and shrinks the page if the device cannot send that many at once.
    /// </summary>
    public async Task<IReadOnlyList<TrendRecord>> ReadRecordsAsync(BacnetObjectId log, uint recordCount, int? last = null,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (recordCount == 0) return [];
        var wanted = last is > 0 ? Math.Min((uint)last.Value, recordCount) : recordCount;
        var position = recordCount - wanted + 1; // positions are 1-based
        var page = FirstPageSize;
        var all = new List<TrendRecord>((int)Math.Min(wanted, 100_000u));

        while (all.Count < wanted)
        {
            ct.ThrowIfCancellationRequested();
            var ask = Math.Min(page, wanted - (uint)all.Count);
            BacnetReadRangeResult answer;
            try
            {
                answer = await client.ReadRangeAsync(device.Address, log, position, ask, cancellationToken: ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested && page > SmallestPageSize)
            {
                page = Math.Max(SmallestPageSize, page / 2); // too much for this device (or a dropped packet): try smaller
                continue;
            }

            var records = TrendRecordDecoder.Decode(answer.Range);
            if (records.Count == 0) break; // device has nothing more for us
            all.AddRange(records);
            position += (uint)records.Count;
            progress?.Report(all.Count);
        }
        return all;
    }

    private static void Keep(Dictionary<BacnetPropertyIds, BacnetValue> into, BacnetPropertyIds p, IList<BacnetValue>? v)
    {
        if (v is { Count: > 0 } && v[0].Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR) into[p] = v[0];
    }

    private static uint? UInt(Dictionary<BacnetPropertyIds, BacnetValue> v, BacnetPropertyIds p) =>
        v.TryGetValue(p, out var x) && x.Value is IConvertible c ? Convert.ToUInt32(c, CultureInfo.InvariantCulture) : null;

    private static bool? Bool(Dictionary<BacnetPropertyIds, BacnetValue> v, BacnetPropertyIds p) =>
        v.TryGetValue(p, out var x) ? x.Value switch { bool b => b, uint u => u != 0, _ => null } : null;

    private static string? Text(Dictionary<BacnetPropertyIds, BacnetValue> v, BacnetPropertyIds p) =>
        v.TryGetValue(p, out var x) ? x.Value as string : null;
}
