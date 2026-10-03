using System.Globalization;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Writing;

/// <summary>
/// What a point looks like right now, gathered to explain a write: its value, whether it is commandable, who is holding it,
/// and its limits. Anything the device would not tell us is null.
/// </summary>
public sealed record PointProbe(
    BacnetObjectTypes Type,
    bool Reachable,
    string? PresentValueText,
    double? PresentValue,
    bool? HasPriorityArray,
    IReadOnlyList<PrioritySlot> Slots,
    bool? OutOfService,
    double? Min,
    double? Max,
    string? Units)
{
    /// <summary>The occupied slot with the lowest number: the one that decides the point's value.</summary>
    public PrioritySlot? Controlling => Slots.Count == 0 ? null : Slots.MinBy(s => s.Priority);

    public static PointProbe Unreachable(BacnetObjectTypes type) => new(type, false, null, null, null, [], null, null, null, null);
}

/// <summary>Reads the handful of properties needed to explain what happened to a write.</summary>
public sealed class PointProber(BacnetClient client, DiscoveredDevice device)
{
    private static readonly BacnetPropertyIds[] Props =
    [
        BacnetPropertyIds.PROP_PRESENT_VALUE, BacnetPropertyIds.PROP_PRIORITY_ARRAY, BacnetPropertyIds.PROP_OUT_OF_SERVICE,
        BacnetPropertyIds.PROP_MIN_PRES_VALUE, BacnetPropertyIds.PROP_MAX_PRES_VALUE, BacnetPropertyIds.PROP_UNITS,
    ];

    public async Task<PointProbe> ProbeAsync(BacnetObjectId point, CancellationToken ct = default)
    {
        var got = new Dictionary<BacnetPropertyIds, IList<BacnetValue>>();
        var errors = new Dictionary<BacnetPropertyIds, BacnetErrorCodes>();
        var anyAnswer = false;

        void Keep(BacnetPropertyIds p, IList<BacnetValue>? values)
        {
            if (values is not { Count: > 0 }) return;
            anyAnswer = true;
            if (values[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR)
            {
                if (values[0].Value is BacnetError e) errors[p] = e.error_code;
            }
            else got[p] = values;
        }

        try
        {
            var refs = Props.Select(p => new BacnetPropertyReference(p, ASN1.BACNET_ARRAY_ALL)).ToList();
            foreach (var r in await client.ReadPropertyMultipleAsync(device.Address, point, refs, cancellationToken: ct))
                foreach (var pv in r.values)
                    Keep((BacnetPropertyIds)pv.property.propertyIdentifier, pv.value);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            foreach (var p in Props) // no RPM: one at a time
            {
                try
                {
                    Keep(p, await client.ReadPropertyAsync(device.Address, point, p, cancellationToken: ct));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (ex.Message.Contains("UNKNOWN_PROPERTY", StringComparison.OrdinalIgnoreCase)) { anyAnswer = true; errors[p] = BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY; }
                    else if (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase) || ex is TimeoutException) { /* silence */ }
                    else anyAnswer = true; // the device answered, just not usefully
                }
            }
        }

        if (!anyAnswer) return PointProbe.Unreachable(point.type);

        got.TryGetValue(BacnetPropertyIds.PROP_PRESENT_VALUE, out var pvValues);
        got.TryGetValue(BacnetPropertyIds.PROP_PRIORITY_ARRAY, out var paValues);
        got.TryGetValue(BacnetPropertyIds.PROP_UNITS, out var unitValues);

        bool? hasArray = paValues is not null ? true
            : errors.TryGetValue(BacnetPropertyIds.PROP_PRIORITY_ARRAY, out var code) && code == BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY ? false
            : null;

        var units = unitValues is { Count: > 0 } && unitValues[0].Value is IConvertible u
            ? BacnetNames.UnitsName(Convert.ToUInt32(u, CultureInfo.InvariantCulture))
            : null;

        return new PointProbe(
            point.type, true,
            pvValues is null ? null : BacnetNames.FormatValues(point.type, BacnetPropertyIds.PROP_PRESENT_VALUE, pvValues),
            pvValues is { Count: > 0 } ? ToNumber(pvValues[0]) : null,
            hasArray,
            paValues is null ? [] : PriorityArrayInfo.Occupied(point.type, paValues),
            got.TryGetValue(BacnetPropertyIds.PROP_OUT_OF_SERVICE, out var oos) && oos[0].Value is bool b ? b : null,
            got.TryGetValue(BacnetPropertyIds.PROP_MIN_PRES_VALUE, out var min) ? ToNumber(min[0]) : null,
            got.TryGetValue(BacnetPropertyIds.PROP_MAX_PRES_VALUE, out var max) ? ToNumber(max[0]) : null,
            string.IsNullOrEmpty(units) ? null : units);
    }

    internal static double? ToNumber(BacnetValue v) => v.Value switch
    {
        float f => f,
        double d => d,
        uint u => u,
        int i => i,
        bool b => b ? 1 : 0,
        _ => null,
    };
}
