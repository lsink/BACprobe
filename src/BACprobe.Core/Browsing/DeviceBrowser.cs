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
    /// <summary>Occupied priority-array slots (empty for points without a priority array).</summary>
    public IReadOnlyList<PrioritySlot> PrioritySlots { get; set; } = [];
    /// <summary>True when a slot at manual-operator priority (8) or higher is occupied: someone has overridden this point.</summary>
    public bool IsOverridden => PriorityArrayInfo.IsOverride(PrioritySlots);
    /// <summary>Short text for the Override column, e.g. "P8 Manual Operator"; empty when not overridden.</summary>
    public string OverrideText
    {
        get
        {
            if (!IsOverridden) return "";
            var p = PrioritySlots.Min(s => s.Priority);
            return $"P{p} {BacnetNames.PriorityName(p)}";
        }
    }
    /// <summary>All occupied slots, for a tooltip.</summary>
    public string OverrideTooltip =>
        PrioritySlots.Count == 0 ? "" : "Priority array: " + string.Join("; ", PrioritySlots.Select(s => s.Description));
    /// <summary>
    /// Store freshly read live fields. Returns true only if something the user can see changed,
    /// so a refresh that finds the same values does not make the screen flicker.
    /// </summary>
    public bool ApplyLive(string? presentValue, IReadOnlyList<PrioritySlot>? slots)
    {
        var changed = false;
        if (presentValue is not null && presentValue != PresentValue)
        {
            PresentValue = presentValue;
            changed = true;
        }
        if (slots is not null && !SameSlots(slots, PrioritySlots))
        {
            PrioritySlots = slots;
            changed = true;
        }
        return changed;
    }

    private static bool SameSlots(IReadOnlyList<PrioritySlot> a, IReadOnlyList<PrioritySlot> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Priority == p.Second.Priority && p.First.ValueText == p.Second.ValueText);

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

    /// <summary>The four basics for every object, plus the priority array for points that can be commanded.</summary>
    private static BacnetPropertyIds[] SummaryPropsFor(BacnetObjectTypes type) =>
        PriorityArrayInfo.MayHavePriorityArray(type) ? [.. SummaryProps, BacnetPropertyIds.PROP_PRIORITY_ARRAY] : SummaryProps;

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
                    SummaryPropsFor(id.type).Select(p => new BacnetPropertyReference(p, ASN1.BACNET_ARRAY_ALL)).ToList())).ToList();
                var results = await client.ReadPropertyMultipleAsync(device.Address, specs, cancellationToken: ct);
                foreach (var r in results)
                    foreach (var pv in r.values)
                        ApplySummary(byId[r.objectIdentifier], (BacnetPropertyIds)pv.property.propertyIdentifier, pv.value);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                foreach (var id in batch) // device refused RPM: one property at a time
                    foreach (var p in SummaryPropsFor(id.type))
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

    // Set once a device has shown it will not do ReadPropertyMultiple, so later refreshes skip the doomed first attempt.
    private bool _preferSingleReads;

    private static bool IsTimeout(Exception ex) =>
        ex is TimeoutException || ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Re-read present values (and priority arrays for commandable points) of the given objects, updating them in place.
    /// Returns the objects whose displayed value or override changed. Throws only if nothing at all could be read,
    /// so the caller can count consecutive failures.
    /// </summary>
    public async Task<IReadOnlyList<ObjectSummary>> RefreshValuesAsync(IReadOnlyList<ObjectSummary> targets, CancellationToken ct = default)
    {
        var live = targets.Where(t => BacnetNames.HasLivePresentValue(t.Id.type)).ToList();
        var byId = live.ToDictionary(t => t.Id);
        var changed = new List<ObjectSummary>();
        Exception? lastError = null;
        var reads = 0;

        foreach (var batch in live.Chunk(SummaryBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var seen = new Dictionary<BacnetObjectId, (string? Value, IReadOnlyList<PrioritySlot>? Slots)>();

            void Collect(BacnetObjectId id, BacnetPropertyIds prop, IList<BacnetValue>? values)
            {
                if (values is null || values.Count == 0 || values[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR) return;
                seen.TryGetValue(id, out var cur);
                if (prop == BacnetPropertyIds.PROP_PRESENT_VALUE) cur.Value = BacnetNames.FormatValues(id.type, prop, values);
                else if (prop == BacnetPropertyIds.PROP_PRIORITY_ARRAY) cur.Slots = PriorityArrayInfo.Occupied(id.type, values);
                seen[id] = cur;
            }

            var ok = false;
            if (!_preferSingleReads)
            {
                try
                {
                    var specs = batch.Select(t => new BacnetReadAccessSpecification(t.Id, LiveProps(t.Id.type)
                        .Select(p => new BacnetPropertyReference(p, ASN1.BACNET_ARRAY_ALL)).ToList())).ToList();
                    foreach (var r in await client.ReadPropertyMultipleAsync(device.Address, specs, cancellationToken: ct))
                        foreach (var pv in r.values)
                            Collect(r.objectIdentifier, (BacnetPropertyIds)pv.property.propertyIdentifier, pv.value);
                    ok = true;
                    reads++;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    lastError = ex;
                    if (!IsTimeout(ex)) _preferSingleReads = true; // refused, not just slow: stop asking
                }
            }

            if (!ok)
            {
                foreach (var t in batch)
                    foreach (var p in LiveProps(t.Id.type))
                    {
                        try
                        {
                            Collect(t.Id, p, await client.ReadPropertyAsync(device.Address, t.Id, p, cancellationToken: ct));
                            reads++;
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested) { lastError = ex; }
                    }
            }

            foreach (var (id, got) in seen)
                if (byId[id].ApplyLive(got.Value, got.Slots)) changed.Add(byId[id]);
        }

        if (reads == 0 && lastError is not null) throw lastError;
        return changed;
    }

    private static BacnetPropertyIds[] LiveProps(BacnetObjectTypes type) =>
        PriorityArrayInfo.MayHavePriorityArray(type)
            ? [BacnetPropertyIds.PROP_PRESENT_VALUE, BacnetPropertyIds.PROP_PRIORITY_ARRAY]
            : [BacnetPropertyIds.PROP_PRESENT_VALUE];

    private static void ApplySummary(ObjectSummary s, BacnetPropertyIds prop, IList<BacnetValue>? values)
    {
        if (values is null || values.Count == 0 || values[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR) return;
        var text = BacnetNames.FormatValues(s.Id.type, prop, values);
        switch (prop)
        {
            case BacnetPropertyIds.PROP_OBJECT_NAME: s.Name = text; break;
            case BacnetPropertyIds.PROP_DESCRIPTION: s.Description = text; break;
            case BacnetPropertyIds.PROP_PRESENT_VALUE: s.PresentValue = text; break;
            case BacnetPropertyIds.PROP_PRIORITY_ARRAY:
                s.PrioritySlots = PriorityArrayInfo.Occupied(s.Id.type, values);
                break;
            case BacnetPropertyIds.PROP_UNITS:
                s.Units = text;
                if (values[0].Value is IConvertible code) s.UnitsCode = Convert.ToUInt32(code, System.Globalization.CultureInfo.InvariantCulture);
                break;
        }
    }

    /// <summary>Every property the device will give us for one object. Vendor-specific ones are flagged, never hidden.</summary>
    // Log objects hold their history in Log_Buffer. Asking for "all" properties of one risks a huge or refused answer,
    // so for these we name the properties we want. (The history itself is read with ReadRange.)
    private static readonly BacnetPropertyIds[] LogObjectProps =
    [
        BacnetPropertyIds.PROP_OBJECT_IDENTIFIER, BacnetPropertyIds.PROP_OBJECT_NAME, BacnetPropertyIds.PROP_OBJECT_TYPE,
        BacnetPropertyIds.PROP_DESCRIPTION, BacnetPropertyIds.PROP_ENABLE, BacnetPropertyIds.PROP_LOG_INTERVAL,
        BacnetPropertyIds.PROP_RECORD_COUNT, BacnetPropertyIds.PROP_TOTAL_RECORD_COUNT, BacnetPropertyIds.PROP_BUFFER_SIZE,
        BacnetPropertyIds.PROP_STOP_WHEN_FULL, BacnetPropertyIds.PROP_START_TIME, BacnetPropertyIds.PROP_STOP_TIME,
    ];

    // The device object's "all" includes Object_List, which on a big controller is thousands of entries. Name what a tech needs.
    private static readonly BacnetPropertyIds[] DeviceObjectProps =
    [
        BacnetPropertyIds.PROP_OBJECT_IDENTIFIER, BacnetPropertyIds.PROP_OBJECT_NAME, BacnetPropertyIds.PROP_OBJECT_TYPE,
        BacnetPropertyIds.PROP_DESCRIPTION, BacnetPropertyIds.PROP_LOCATION, BacnetPropertyIds.PROP_SYSTEM_STATUS,
        BacnetPropertyIds.PROP_VENDOR_NAME, BacnetPropertyIds.PROP_VENDOR_IDENTIFIER, BacnetPropertyIds.PROP_MODEL_NAME,
        BacnetPropertyIds.PROP_FIRMWARE_REVISION, BacnetPropertyIds.PROP_APPLICATION_SOFTWARE_VERSION,
        BacnetPropertyIds.PROP_PROTOCOL_VERSION, BacnetPropertyIds.PROP_PROTOCOL_REVISION,
        BacnetPropertyIds.PROP_MAX_APDU_LENGTH_ACCEPTED, BacnetPropertyIds.PROP_SEGMENTATION_SUPPORTED,
        BacnetPropertyIds.PROP_APDU_TIMEOUT, BacnetPropertyIds.PROP_NUMBER_OF_APDU_RETRIES,
        BacnetPropertyIds.PROP_MAX_MASTER, BacnetPropertyIds.PROP_MAX_INFO_FRAMES, BacnetPropertyIds.PROP_DATABASE_REVISION,
        BacnetPropertyIds.PROP_LOCAL_DATE, BacnetPropertyIds.PROP_LOCAL_TIME, BacnetPropertyIds.PROP_UTC_OFFSET,
        BacnetPropertyIds.PROP_DAYLIGHT_SAVINGS_STATUS, BacnetPropertyIds.PROP_LAST_RESTART_REASON,
        BacnetPropertyIds.PROP_TIME_OF_DEVICE_RESTART,
    ];

    /// <summary>Named property lists for objects where "all" would drag in something huge; null means ask for all.</summary>
    private static BacnetPropertyIds[]? NamedProps(BacnetObjectTypes type) =>
        IsLogObject(type) ? LogObjectProps : type == BacnetObjectTypes.OBJECT_DEVICE ? DeviceObjectProps : null;

    public static bool IsLogObject(BacnetObjectTypes type) =>
        type is BacnetObjectTypes.OBJECT_TRENDLOG or BacnetObjectTypes.OBJECT_TREND_LOG_MULTIPLE or BacnetObjectTypes.OBJECT_EVENT_LOG;

    public async Task<IReadOnlyList<PropertyRow>> ReadAllPropertiesAsync(BacnetObjectId id, CancellationToken ct = default)
    {
        var rows = new List<PropertyRow>();
        var named = NamedProps(id.type);
        try
        {
            var refs = named is not null
                ? named.Select(p => new BacnetPropertyReference(p, ASN1.BACNET_ARRAY_ALL)).ToList()
                : [new((uint)BacnetPropertyIds.PROP_ALL, ASN1.BACNET_ARRAY_ALL)];
            var results = await client.ReadPropertyMultipleAsync(device.Address, id, refs, cancellationToken: ct);
            foreach (var pv in results.SelectMany(r => r.values))
            {
                // We asked for these by name; an optional one the device does not have is not worth a row.
                if (named is not null && IsUnknownProperty(pv.value)) continue;
                rows.Add(ToRow(id.type, pv.property.propertyIdentifier, pv.value));
            }
            return rows;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            rows.Clear(); // no RPM / no PROP_ALL: read the common ones one by one
        }

        foreach (var p in named ?? CommonProps)
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

    private static bool IsUnknownProperty(IList<BacnetValue>? values) =>
        values is { Count: > 0 } && values[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR
        && values[0].Value is BacnetError { error_code: BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY };

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
