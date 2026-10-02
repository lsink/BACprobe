using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Browsing;

public sealed class ObjectSummary
{
    public required BacnetObjectId Id { get; init; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? PresentValue { get; set; }
    public string? Units { get; set; }
    /// <summary>The raw BACnet engineering-units code (used by EDE export).</summary>
    public uint? UnitsCode { get; set; }
    public string TypeName => BacnetNames.ObjectTypeName(Id.type);
    public string Label => BacnetNames.ObjectLabel(Id);
    /// <summary>Present value with units, e.g. "72.4 °F".</summary>
    public string ValueText => string.IsNullOrEmpty(Units) ? PresentValue ?? "" : $"{PresentValue} {Units}";
}

public sealed record PropertyRow(uint PropertyId, string Name, string Display, bool IsVendorSpecific, bool IsError);

/// <summary>Reads the object list and properties of one device over an existing client.</summary>
public sealed class DeviceBrowser(BacnetClient client, DiscoveredDevice device)
{
    private const int SummaryBatchSize = 8;

    private static readonly BacnetPropertyIds[] SummaryProps =
    [
        BacnetPropertyIds.PROP_OBJECT_NAME, BacnetPropertyIds.PROP_DESCRIPTION,
        BacnetPropertyIds.PROP_PRESENT_VALUE, BacnetPropertyIds.PROP_UNITS,
    ];

    // Used when a device will not do ReadPropertyMultiple with PROP_ALL.
    private static readonly BacnetPropertyIds[] CommonProps =
    [
        BacnetPropertyIds.PROP_OBJECT_NAME, BacnetPropertyIds.PROP_OBJECT_TYPE, BacnetPropertyIds.PROP_DESCRIPTION,
        BacnetPropertyIds.PROP_PRESENT_VALUE, BacnetPropertyIds.PROP_UNITS, BacnetPropertyIds.PROP_STATUS_FLAGS,
        BacnetPropertyIds.PROP_OUT_OF_SERVICE, BacnetPropertyIds.PROP_PRIORITY_ARRAY, BacnetPropertyIds.PROP_RELINQUISH_DEFAULT,
        BacnetPropertyIds.PROP_INACTIVE_TEXT, BacnetPropertyIds.PROP_ACTIVE_TEXT,
    ];

    private BacnetObjectId DeviceObject => new(BacnetObjectTypes.OBJECT_DEVICE, device.InstanceId);

    /// <summary>Whole array first; if the device cannot send it in one piece, one index at a time.</summary>
    public async Task<IReadOnlyList<BacnetObjectId>> ReadObjectListAsync(CancellationToken ct = default)
    {
        try
        {
            var all = await client.ReadPropertyAsync(device.Address, DeviceObject, BacnetPropertyIds.PROP_OBJECT_LIST,
                cancellationToken: ct);
            return ToIds(all);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Likely a segmentation problem on a big list; fall through to per-item reads.
        }

        var countValues = await client.ReadPropertyAsync(device.Address, DeviceObject, BacnetPropertyIds.PROP_OBJECT_LIST,
            arrayIndex: 0, cancellationToken: ct);
        var count = Convert.ToUInt32(countValues[0].Value);
        var ids = new List<BacnetObjectId>((int)count);
        for (uint i = 1; i <= count; i++)
        {
            var one = await client.ReadPropertyAsync(device.Address, DeviceObject, BacnetPropertyIds.PROP_OBJECT_LIST,
                arrayIndex: i, cancellationToken: ct);
            ids.AddRange(ToIds(one));
        }
        return ids;
    }

    private static List<BacnetObjectId> ToIds(IEnumerable<BacnetValue> values) =>
        values.Where(v => v.Value is BacnetObjectId).Select(v => (BacnetObjectId)v.Value).ToList();

    /// <summary>Name, description, value and units for each object, a few objects per request.</summary>
    public async Task<IReadOnlyList<ObjectSummary>> ReadSummariesAsync(IReadOnlyList<BacnetObjectId> ids,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var summaries = ids.Select(id => new ObjectSummary { Id = id }).ToList();
        var byId = summaries.ToDictionary(s => s.Id);
        var done = 0;

        foreach (var batch in ids.Chunk(SummaryBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var specs = batch.Select(id => new BacnetReadAccessSpecification(id,
                    SummaryProps.Select(p => new BacnetPropertyReference(p, ASN1.BACNET_ARRAY_ALL)).ToList())).ToList();
                var results = await client.ReadPropertyMultipleAsync(device.Address, specs, cancellationToken: ct);
                foreach (var r in results)
                    foreach (var pv in r.values)
                        ApplySummary(byId[r.objectIdentifier], (BacnetPropertyIds)pv.property.propertyIdentifier, pv.value);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                foreach (var id in batch) // device refused RPM: one property at a time
                    foreach (var p in SummaryProps)
                    {
                        try { ApplySummary(byId[id], p, await client.ReadPropertyAsync(device.Address, id, p, cancellationToken: ct)); }
                        catch (Exception) when (!ct.IsCancellationRequested) { /* property not present on this object type */ }
                    }
            }
            done += batch.Length;
            progress?.Report(done);
        }
        return summaries;
    }

    private static void ApplySummary(ObjectSummary s, BacnetPropertyIds prop, IList<BacnetValue>? values)
    {
        if (values is null || values.Count == 0 || values[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR) return;
        var text = BacnetNames.FormatValues(s.Id.type, prop, values);
        switch (prop)
        {
            case BacnetPropertyIds.PROP_OBJECT_NAME: s.Name = text; break;
            case BacnetPropertyIds.PROP_DESCRIPTION: s.Description = text; break;
            case BacnetPropertyIds.PROP_PRESENT_VALUE: s.PresentValue = text; break;
            case BacnetPropertyIds.PROP_UNITS:
                s.Units = text;
                if (values[0].Value is IConvertible code) s.UnitsCode = Convert.ToUInt32(code, System.Globalization.CultureInfo.InvariantCulture);
                break;
        }
    }

    /// <summary>Every property the device will give us for one object. Vendor-specific ones are flagged, never hidden.</summary>
    public async Task<IReadOnlyList<PropertyRow>> ReadAllPropertiesAsync(BacnetObjectId id, CancellationToken ct = default)
    {
        var rows = new List<PropertyRow>();
        try
        {
            var refs = new List<BacnetPropertyReference>
                { new((uint)BacnetPropertyIds.PROP_ALL, ASN1.BACNET_ARRAY_ALL) };
            var results = await client.ReadPropertyMultipleAsync(device.Address, id, refs, cancellationToken: ct);
            foreach (var pv in results.SelectMany(r => r.values))
                rows.Add(ToRow(id.type, pv.property.propertyIdentifier, pv.value));
            return rows;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            rows.Clear(); // no RPM / no PROP_ALL: read the common ones one by one
        }

        foreach (var p in CommonProps)
        {
            try
            {
                var values = await client.ReadPropertyAsync(device.Address, id, p, cancellationToken: ct);
                rows.Add(ToRow(id.type, (uint)p, values));
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* not present */ }
        }
        return rows;
    }

    /// <summary>Read one named property; throws the library's exception on error or timeout.</summary>
    public async Task<PropertyRow> ReadPropertyAsync(BacnetObjectId id, BacnetPropertyIds property, CancellationToken ct = default)
    {
        var values = await client.ReadPropertyAsync(device.Address, id, property, cancellationToken: ct);
        return ToRow(id.type, (uint)property, values);
    }

    private static PropertyRow ToRow(BacnetObjectTypes type, uint propertyId, IList<BacnetValue>? values)
    {
        var isError = values is { Count: > 0 } && values[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR;
        var display = BacnetNames.FormatValues(type, (BacnetPropertyIds)propertyId, values);
        if (BacnetNames.IsVendorProperty(propertyId) && values is { Count: > 0 } && !isError)
            display = "raw: " + string.Join(", ", values.Select(v => Convert.ToString(v.Value) ?? ""));
        return new PropertyRow(propertyId, BacnetNames.PropertyName(propertyId), display,
            BacnetNames.IsVendorProperty(propertyId), isError);
    }
}
