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
    /// <summary>The device's names for this point's states (see <see cref="StateText"/>); null if it gave none.</summary>
    public IReadOnlyList<string?>? StateNames { get; set; }

    /// <summary>Present value with its state name when the device gave one, e.g. "Occupied (2)" or "On (Active)".</summary>
    public string DisplayValue => StateText.Label(Id.type, PresentValue, StateNames);

    /// <summary>One slot as shown to the user, with the state name: "8 (Manual Operator) = Standby (3)".</summary>
    public string SlotText(PrioritySlot slot) =>
        $"{slot.Priority} ({BacnetNames.PriorityName(slot.Priority)}) = {StateText.Label(Id.type, slot.ValueText, StateNames)}";

    /// <summary>The whole priority array as shown in the properties panel.</summary>
    public string PriorityArrayText =>
        PrioritySlots.Count == 0 ? "no overrides (all 16 slots empty)" : string.Join("; ", PrioritySlots.Select(SlotText));

    /// <summary>All occupied slots, for a tooltip.</summary>
    public string OverrideTooltip =>
        PrioritySlots.Count == 0 ? "" : "Priority array: " + string.Join("; ", PrioritySlots.Select(SlotText));

    /// <summary>Status_Flags as last read; null if not read (or the object has none).</summary>
    public BacnetStatusFlags? StatusFlags { get; set; }

    /// <summary>The Reliability code, read only for points in fault (0 = no fault detected); null if not known.</summary>
    public uint? Reliability { get; set; }

    public bool IsFault => PointHealth.IsFault(StatusFlags, Reliability);
    public bool IsInAlarm => StatusFlags?.HasFlag(BacnetStatusFlags.STATUS_FLAG_IN_ALARM) == true;
    public bool IsOutOfService => StatusFlags?.HasFlag(BacnetStatusFlags.STATUS_FLAG_OUT_OF_SERVICE) == true;

    /// <summary>In fault, in alarm or out of service: something a tech should look at.</summary>
    public bool HasProblem => IsFault || IsInAlarm || IsOutOfService;

    /// <summary>For the Status column: "Fault: open loop; Out of service". Empty for a healthy point.</summary>
    public string ProblemText => PointHealth.Summary(StatusFlags, Reliability);

    /// <summary>Each problem with its likely cause and next step, for a tooltip.</summary>
    public string ProblemTooltip => PointHealth.Explanation(StatusFlags, Reliability);

    /// <summary>
    /// Store freshly read live fields. Returns true only if something the user can see changed,
    /// so a refresh that finds the same values does not make the screen flicker.
    /// </summary>
    public bool ApplyLive(string? presentValue, IReadOnlyList<PrioritySlot>? slots, BacnetStatusFlags? flags = null)
    {
        var changed = false;
        if (flags is { } f && f != StatusFlags)
        {
            StatusFlags = f;
            if (!f.HasFlag(BacnetStatusFlags.STATUS_FLAG_FAULT)) Reliability = null; // the fault cleared, so its reason no longer applies
            changed = true;
        }
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

    /// <summary>
    /// Take every field from a fresh read of the same object, in place. Other views (the Find index, a live watch) hold
    /// this same instance, so updating it keeps them all current; swapping in the new object would leave them stale.
    /// </summary>
    public void UpdateFrom(ObjectSummary fresh)
    {
        Name = fresh.Name ?? Name;
        Description = fresh.Description ?? Description;
        PresentValue = fresh.PresentValue ?? PresentValue;
        Units = fresh.Units ?? Units;
        UnitsCode = fresh.UnitsCode ?? UnitsCode;
        StateNames = fresh.StateNames ?? StateNames;
        StatusFlags = fresh.StatusFlags ?? StatusFlags;
        Reliability = fresh.StatusFlags is null ? Reliability : fresh.Reliability;
        // A missing or timed-out priority-array read leaves the fresh slots empty, which must not wipe a known override.
        if (fresh.PriorityArrayRead) PrioritySlots = fresh.PrioritySlots;
    }

    /// <summary>True when the device actually answered the Priority_Array read, so an empty list means "nothing is set".</summary>
    public bool PriorityArrayRead { get; set; }

    private static bool SameSlots(IReadOnlyList<PrioritySlot> a, IReadOnlyList<PrioritySlot> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Priority == p.Second.Priority && p.First.ValueText == p.Second.ValueText);

    public string TypeName => BacnetNames.ObjectTypeName(Id.type);
    public string Label => BacnetNames.ObjectLabel(Id);
    /// <summary>Present value with units or state name, e.g. "72.4 °F" or "Occupied (2)".</summary>
    public string ValueText => string.IsNullOrEmpty(Units) ? DisplayValue : $"{DisplayValue} {Units}";
}

/// <param name="ValueTag">The BACnet type of the value the device sent (null for an error, nothing, or a list), so the value can be edited as that type.</param>
/// <param name="ValueCount">How many values came back: more than one is an array or list.</param>
public sealed record PropertyRow(uint PropertyId, string Name, string Display, bool IsVendorSpecific, bool IsError,
    BacnetApplicationTags? ValueTag = null, int ValueCount = 0);

/// <summary>Reads the object list and properties of one device over an existing client.</summary>
public sealed class DeviceBrowser(BacnetClient client, DiscoveredDevice device)
{
    private const int SummaryBatchSize = 8;

    private static readonly BacnetPropertyIds[] SummaryProps =
    [
        BacnetPropertyIds.PROP_OBJECT_NAME, BacnetPropertyIds.PROP_DESCRIPTION,
        BacnetPropertyIds.PROP_PRESENT_VALUE, BacnetPropertyIds.PROP_UNITS,
    ];

    /// <summary>
    /// The four basics for every object, plus Status_Flags for points with a live value, the priority array for points that
    /// can be commanded, and the state names of binary and multi-state points.
    /// </summary>
    private static BacnetPropertyIds[] SummaryPropsFor(BacnetObjectTypes type) =>
    [
        .. SummaryProps,
        .. BacnetNames.HasLivePresentValue(type) ? [BacnetPropertyIds.PROP_STATUS_FLAGS] : Array.Empty<BacnetPropertyIds>(),
        .. PriorityArrayInfo.MayHavePriorityArray(type) ? [BacnetPropertyIds.PROP_PRIORITY_ARRAY] : Array.Empty<BacnetPropertyIds>(),
        .. StateText.NameProperties(type),
    ];

    // Used when a device will not do ReadPropertyMultiple with PROP_ALL.
    private static readonly BacnetPropertyIds[] CommonProps =
    [
        BacnetPropertyIds.PROP_OBJECT_NAME, BacnetPropertyIds.PROP_OBJECT_TYPE, BacnetPropertyIds.PROP_DESCRIPTION,
        BacnetPropertyIds.PROP_PRESENT_VALUE, BacnetPropertyIds.PROP_UNITS, BacnetPropertyIds.PROP_STATUS_FLAGS,
        BacnetPropertyIds.PROP_OUT_OF_SERVICE, BacnetPropertyIds.PROP_PRIORITY_ARRAY, BacnetPropertyIds.PROP_RELINQUISH_DEFAULT,
        BacnetPropertyIds.PROP_INACTIVE_TEXT, BacnetPropertyIds.PROP_ACTIVE_TEXT, BacnetPropertyIds.PROP_NUMBER_OF_STATES,
        BacnetPropertyIds.PROP_STATE_TEXT, BacnetPropertyIds.PROP_RELIABILITY,
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
        catch (Exception ex) when (!ct.IsCancellationRequested && !BacnetFailure.IsTimeout(ex))
        {
            // Too big to send in one piece (the device cannot segment); fall through to per-item reads.
            // A timeout is rethrown instead: a device that is not answering would only time out on every item too.
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

    /// <summary>
    /// Objects per ReadPropertyMultiple. A device that can send segmented answers gets the full batch; one that cannot
    /// gets as many as fit in its max APDU (roughly 160 bytes per object with names, value, units and priority array),
    /// so a small MS/TP controller is not sent requests it can only abort.
    /// </summary>
    public static int BatchSizeFor(uint maxApdu, BacnetSegmentations segmentation) =>
        segmentation is BacnetSegmentations.SEGMENTATION_BOTH or BacnetSegmentations.SEGMENTATION_TRANSMIT
            ? SummaryBatchSize
            : Math.Clamp((int)(maxApdu / 160), 1, SummaryBatchSize);

    /// <summary>
    /// Name, description, value and units for each object, a few objects per request. A batch the device aborts is split
    /// in half; a device that refuses ReadPropertyMultiple is read one property at a time from then on. Throws a
    /// <see cref="TimeoutException"/> if the device stops answering, instead of waiting out every remaining property.
    /// </summary>
    public async Task<IReadOnlyList<ObjectSummary>> ReadSummariesAsync(IReadOnlyList<BacnetObjectId> ids,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var summaries = ids.Select(id => new ObjectSummary { Id = id }).ToList();
        var byId = summaries.ToDictionary(s => s.Id);
        var batchSize = BatchSizeFor(device.MaxApdu, device.Segmentation);
        var timeouts = 0;

        var next = 0;
        while (next < ids.Count)
        {
            ct.ThrowIfCancellationRequested();
            var batch = ids.Skip(next).Take(batchSize).ToArray();
            var viaRpm = false;
            if (!_preferSingleReads)
            {
                try
                {
                    var specs = batch.Select(id => new BacnetReadAccessSpecification(id,
                        SummaryPropsFor(id.type).Select(p => new BacnetPropertyReference(p, ASN1.BACNET_ARRAY_ALL)).ToList())).ToList();
                    var results = await client.ReadPropertyMultipleAsync(device.Address, specs, cancellationToken: ct);
                    foreach (var r in results)
                        if (byId.TryGetValue(r.objectIdentifier, out var s))
                            foreach (var pv in r.values)
                                ApplySummary(s, (BacnetPropertyIds)pv.property.propertyIdentifier, pv.value);
                    viaRpm = true;
                    timeouts = 0;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (BacnetFailure.IsAbort(ex) && batch.Length > 1)
                    {
                        batchSize = Math.Max(1, batch.Length / 2); // too big for this device: ask for less, same objects again
                        continue;
                    }
                    if (BacnetFailure.IsTimeout(ex)) CountTimeout(ref timeouts, ex);
                    else if (!BacnetFailure.IsAbort(ex)) _preferSingleReads = true; // refused, not just slow or too big: stop asking
                }
            }

            if (!viaRpm)
                foreach (var id in batch) // one property at a time
                    foreach (var p in SummaryPropsFor(id.type))
                    {
                        try
                        {
                            ApplySummary(byId[id], p, await client.ReadPropertyAsync(device.Address, id, p, cancellationToken: ct));
                            timeouts = 0;
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            if (BacnetFailure.IsTimeout(ex)) CountTimeout(ref timeouts, ex);
                            else timeouts = 0; // property not present on this object type
                        }
                    }

            next += batch.Length;
            progress?.Report(next);
        }
        await FillReliabilityAsync(summaries, ct);
        return summaries;
    }

    private static void CountTimeout(ref int timeouts, Exception ex)
    {
        if (++timeouts >= BacnetFailure.MaxTimeoutsInARow)
            throw new TimeoutException($"The device stopped answering ({ex.Message}).", ex);
    }

    // Set once a device has shown it will not do ReadPropertyMultiple, so later refreshes skip the doomed first attempt.
    private bool _preferSingleReads;

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

        foreach (var batch in live.Chunk(BatchSizeFor(device.MaxApdu, device.Segmentation)))
        {
            ct.ThrowIfCancellationRequested();
            var seen = new Dictionary<BacnetObjectId, (string? Value, IReadOnlyList<PrioritySlot>? Slots, BacnetStatusFlags? Flags)>();

            void Collect(BacnetObjectId id, BacnetPropertyIds prop, IList<BacnetValue>? values)
            {
                if (values is null || values.Count == 0 || values[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR) return;
                seen.TryGetValue(id, out var cur);
                if (prop == BacnetPropertyIds.PROP_PRESENT_VALUE) cur.Value = BacnetNames.FormatValues(id.type, prop, values);
                else if (prop == BacnetPropertyIds.PROP_PRIORITY_ARRAY) cur.Slots = PriorityArrayInfo.Occupied(id.type, values);
                else if (prop == BacnetPropertyIds.PROP_STATUS_FLAGS) cur.Flags = PointHealth.FlagsFrom(values);
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
                    if (BacnetFailure.IsTimeout(ex))
                    {
                        // No answer at all. Nothing read yet this pass: the device is down, say so now rather than
                        // waiting out a timeout per property. Otherwise skip this batch; the next pass gets it.
                        if (reads == 0) throw;
                        continue;
                    }
                    _preferSingleReads = true; // refused, not just slow: stop asking
                }
            }

            var silent = false;
            if (!ok)
            {
                var timeouts = 0;
                foreach (var t in batch.TakeWhile(_ => !silent))
                    foreach (var p in LiveProps(t.Id.type))
                    {
                        if (silent) break;
                        try
                        {
                            Collect(t.Id, p, await client.ReadPropertyAsync(device.Address, t.Id, p, cancellationToken: ct));
                            reads++;
                            timeouts = 0;
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            lastError = ex;
                            // The device went quiet: stop this pass (keeping what was read) instead of waiting out the rest.
                            if (BacnetFailure.IsTimeout(ex) && ++timeouts >= BacnetFailure.MaxTimeoutsInARow) silent = true;
                        }
                    }
            }

            foreach (var (id, got) in seen)
                if (byId.TryGetValue(id, out var target) && target.ApplyLive(got.Value, got.Slots, got.Flags)) changed.Add(target);
            if (silent) break;
        }

        // A point that has just gone into fault: find out why (Reliability is not polled, to keep requests down).
        foreach (var s in await FillReliabilityAsync(live, ct))
            if (!changed.Contains(s)) changed.Add(s);

        if (reads == 0 && lastError is not null) throw lastError;
        return changed;
    }

    private static BacnetPropertyIds[] LiveProps(BacnetObjectTypes type) =>
        PriorityArrayInfo.MayHavePriorityArray(type)
            ? [BacnetPropertyIds.PROP_PRESENT_VALUE, BacnetPropertyIds.PROP_PRIORITY_ARRAY, BacnetPropertyIds.PROP_STATUS_FLAGS]
            : [BacnetPropertyIds.PROP_PRESENT_VALUE, BacnetPropertyIds.PROP_STATUS_FLAGS];

    /// <summary>
    /// Read Reliability for points in fault whose reason is not known yet, so the Status column can say "open loop" rather
    /// than just "fault". Faults are few, so this is a handful of reads at most. Returns the points that gained a reason.
    /// </summary>
    private async Task<List<ObjectSummary>> FillReliabilityAsync(IEnumerable<ObjectSummary> points, CancellationToken ct)
    {
        var filled = new List<ObjectSummary>();
        foreach (var s in points.Where(p => p.IsFault && p.Reliability is null).ToList())
        {
            try
            {
                var code = PointHealth.ReliabilityFrom(await client.ReadPropertyAsync(device.Address, s.Id, BacnetPropertyIds.PROP_RELIABILITY,
                    cancellationToken: ct));
                if (code is null) continue;
                s.Reliability = code;
                filled.Add(s);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (BacnetFailure.IsTimeout(ex)) break; // not answering: the fault still shows, just without its reason
                s.Reliability = 0; // the device has no reason to give: remember that, so live polls do not keep asking
            }
        }
        return filled;
    }

    private static void ApplySummary(ObjectSummary s, BacnetPropertyIds prop, IList<BacnetValue>? values)
    {
        if (values is null || values.Count == 0 || values[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR) return;
        s.StateNames = StateText.Merge(s.StateNames, s.Id.type, prop, values);
        var text = BacnetNames.FormatValues(s.Id.type, prop, values);
        switch (prop)
        {
            case BacnetPropertyIds.PROP_OBJECT_NAME: s.Name = text; break;
            case BacnetPropertyIds.PROP_DESCRIPTION: s.Description = text; break;
            case BacnetPropertyIds.PROP_PRESENT_VALUE: s.PresentValue = text; break;
            case BacnetPropertyIds.PROP_PRIORITY_ARRAY:
                s.PrioritySlots = PriorityArrayInfo.Occupied(s.Id.type, values);
                s.PriorityArrayRead = true;
                break;
            case BacnetPropertyIds.PROP_STATUS_FLAGS:
                s.StatusFlags = PointHealth.FlagsFrom(values);
                break;
            case BacnetPropertyIds.PROP_RELIABILITY:
                s.Reliability = PointHealth.ReliabilityFrom(values);
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
        var named = NamedProps(id.type);
        try
        {
            var refs = named is not null
                ? named.Select(p => new BacnetPropertyReference(p, ASN1.BACNET_ARRAY_ALL)).ToList()
                : [new((uint)BacnetPropertyIds.PROP_ALL, ASN1.BACNET_ARRAY_ALL)];
            var results = await client.ReadPropertyMultipleAsync(device.Address, id, refs, cancellationToken: ct);
            var raw = new List<(uint, IList<BacnetValue>?)>();
            foreach (var pv in results.SelectMany(r => r.values))
            {
                // We asked for these by name; an optional one the device does not have is not worth a row.
                if (named is not null && IsUnknownProperty(pv.value)) continue;
                raw.Add((pv.property.propertyIdentifier, pv.value));
            }
            return ToRows(id.type, raw);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // no RPM / no PROP_ALL: read the common ones one by one
        }

        var oneByOne = new List<(uint, IList<BacnetValue>?)>();
        foreach (var p in named ?? CommonProps)
        {
            try
            {
                oneByOne.Add(((uint)p, await client.ReadPropertyAsync(device.Address, id, p, cancellationToken: ct)));
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* not present */ }
        }
        return ToRows(id.type, oneByOne);
    }

    private static bool IsUnknownProperty(IList<BacnetValue>? values) =>
        values is { Count: > 0 } && values[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR
        && values[0].Value is BacnetError { error_code: BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY };

    /// <summary>The point's state names (State_Text, or Inactive/Active_Text); null if it has none or will not say. Never throws for that.</summary>
    public async Task<IReadOnlyList<string?>?> ReadStateNamesAsync(BacnetObjectId id, CancellationToken ct = default)
    {
        IReadOnlyList<string?>? names = null;
        foreach (var p in StateText.NameProperties(id.type))
        {
            try { names = StateText.Merge(names, id.type, p, await client.ReadPropertyAsync(device.Address, id, p, cancellationToken: ct)); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* optional property */ }
        }
        return names;
    }

    /// <summary>
    /// Each Structured View's Subordinate_List (the device's own folders). A view that cannot be read is left empty rather than failing
    /// the lot; a device that stops answering throws.
    /// </summary>
    public async Task<IReadOnlyDictionary<BacnetObjectId, IReadOnlyList<SubordinateRef>>> ReadStructuredViewsAsync(IEnumerable<BacnetObjectId> ids,
        CancellationToken ct = default)
    {
        var views = new Dictionary<BacnetObjectId, IReadOnlyList<SubordinateRef>>();
        var timeouts = 0;
        foreach (var id in ids.Where(i => i.type == BacnetObjectTypes.OBJECT_STRUCTURED_VIEW))
        {
            try
            {
                views[id] = StructureTree.ParseSubordinates(
                    await client.ReadPropertyAsync(device.Address, id, BacnetPropertyIds.PROP_SUBORDINATE_LIST, cancellationToken: ct));
                timeouts = 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                views[id] = [];
                if (BacnetFailure.IsTimeout(ex)) CountTimeout(ref timeouts, ex);
            }
        }
        return views;
    }

    /// <summary>Read one named property; throws the library's exception on error or timeout.</summary>
    public async Task<PropertyRow> ReadPropertyAsync(BacnetObjectId id, BacnetPropertyIds property, CancellationToken ct = default)
    {
        var values = await client.ReadPropertyAsync(device.Address, id, property, cancellationToken: ct);
        return ToRow(id.type, (uint)property, values);
    }

    /// <summary>
    /// Rows for the properties panel. The state names are found first, so Present Value, Relinquish Default and the
    /// priority array can show "Occupied (2)" rather than a bare number, wherever they came in the answer.
    /// </summary>
    public static IReadOnlyList<PropertyRow> ToRows(BacnetObjectTypes type, IReadOnlyList<(uint Property, IList<BacnetValue>? Values)> raw)
    {
        IReadOnlyList<string?>? names = null;
        foreach (var (p, v) in raw) names = StateText.Merge(names, type, (BacnetPropertyIds)p, v);

        var rows = new List<PropertyRow>(raw.Count);
        foreach (var (p, v) in raw)
        {
            var row = ToRow(type, p, v);
            if (names is not null && !row.IsError && v is { Count: > 0 })
            {
                if (p is (uint)BacnetPropertyIds.PROP_PRESENT_VALUE or (uint)BacnetPropertyIds.PROP_RELINQUISH_DEFAULT)
                    row = row with { Display = StateText.Label(type, row.Display, names) };
                else if (p == (uint)BacnetPropertyIds.PROP_PRIORITY_ARRAY)
                    row = row with
                    {
                        Display = new ObjectSummary { Id = new BacnetObjectId(type, 0), StateNames = names, PrioritySlots = PriorityArrayInfo.Occupied(type, v) }
                            .PriorityArrayText,
                    };
            }
            rows.Add(row);
        }
        return rows;
    }

    private static PropertyRow ToRow(BacnetObjectTypes type, uint propertyId, IList<BacnetValue>? values)
    {
        var isError = values is { Count: > 0 } && values[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR;
        var display = BacnetNames.FormatValues(type, (BacnetPropertyIds)propertyId, values);
        if (BacnetNames.IsVendorProperty(propertyId) && values is { Count: > 0 } && !isError)
            display = "raw: " + string.Join(", ", values.Select(v => Convert.ToString(v.Value) ?? ""));
        return new PropertyRow(propertyId, BacnetNames.PropertyName(propertyId), display,
            BacnetNames.IsVendorProperty(propertyId), isError,
            !isError && values is { Count: 1 } ? values[0].Tag : null, values?.Count ?? 0);
    }
}
