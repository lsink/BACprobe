using System.IO.BACnet;
using System.IO.BACnet.Serialize;

namespace BACprobe.Core.Simulation;

public readonly record struct SimError(BacnetErrorClasses Class, BacnetErrorCodes Code);

/// <summary>
/// In-memory BACnet device: objects, properties, priority arrays. No networking, so it is fully unit-testable.
/// </summary>
public sealed class SimulatedDeviceModel
{
    public const uint WildcardInstance = 4194303;
    private const uint ArrayAll = uint.MaxValue;

    private sealed class SimObject(BacnetObjectId id)
    {
        public BacnetObjectId Id { get; } = id;
        public Dictionary<BacnetPropertyIds, List<BacnetValue>> Props { get; } = [];
        /// <summary>Non-null for commandable objects; index 0 = priority 1.</summary>
        public BacnetValue?[]? Priority { get; set; }
        public BacnetValue RelinquishDefault { get; set; }
        public SimTrend? Trend { get; set; }
        /// <summary>Set by <see cref="SetProblem"/>: the point reports itself in alarm.</summary>
        public bool InAlarm { get; set; }
        /// <summary>The value is pinned (a failed sensor reads the same rail value): the drift leaves it alone.</summary>
        public bool Stuck { get; set; }
    }

    /// <summary>
    /// Give a point a problem to find: a fault with its reason (Reliability), an alarm, and/or out of service.
    /// <paramref name="stuckAt"/> pins an analog value, like a broken sensor reading the bottom of its range.
    /// </summary>
    public void SetProblem(BacnetObjectId id, BacnetReliability? reliability = null, bool inAlarm = false, bool outOfService = false,
        float? stuckAt = null)
    {
        lock (_lock)
        {
            if (!_objects.TryGetValue(id, out var o)) return;
            if (reliability is { } r) Set(o, BacnetPropertyIds.PROP_RELIABILITY, Enum((uint)r));
            o.InAlarm = inAlarm;
            Set(o, BacnetPropertyIds.PROP_OUT_OF_SERVICE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, outOfService));
            if (stuckAt is { } v)
            {
                Set(o, BacnetPropertyIds.PROP_PRESENT_VALUE, Real(v));
                o.Stuck = true;
            }
        }
    }

    /// <summary>The sample device's troubles for <c>simulate --faults</c>: a broken discharge-air sensor, a zone in alarm, and a point left out of service.</summary>
    public void AddSampleProblems()
    {
        SetProblem(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 2), BacnetReliability.RELIABILITY_OPEN_LOOP, stuckAt: -40f);
        SetProblem(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1), inAlarm: true);
        SetProblem(new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_VALUE, 1), outOfService: true);
    }

    /// <summary>Status_Flags as the point would report them: alarm as set, fault from Reliability, out of service from its property.</summary>
    public BacnetStatusFlags StatusFlags(BacnetObjectId id)
    {
        lock (_lock) return Resolve(id, out var o) ? FlagsOf(o) : 0;
    }

    private static BacnetStatusFlags FlagsOf(SimObject o)
    {
        var flags = (BacnetStatusFlags)0;
        if (o.InAlarm) flags |= BacnetStatusFlags.STATUS_FLAG_IN_ALARM;
        if (o.Props.TryGetValue(BacnetPropertyIds.PROP_RELIABILITY, out var rel) && rel is [{ Value: uint code }] && code != 0)
            flags |= BacnetStatusFlags.STATUS_FLAG_FAULT;
        if (o.Props.TryGetValue(BacnetPropertyIds.PROP_OUT_OF_SERVICE, out var oos) && oos is [{ Value: true }])
            flags |= BacnetStatusFlags.STATUS_FLAG_OUT_OF_SERVICE;
        return flags;
    }

    /// <summary>The recorded history of one trend log, newest at the end.</summary>
    private sealed class SimTrend(BacnetObjectId source, TimeSpan interval, int bufferSize)
    {
        public BacnetObjectId Source { get; } = source;
        public TimeSpan Interval { get; } = interval;
        public int BufferSize { get; } = bufferSize;
        public List<BacnetLogRecord> Buffer { get; } = [];
        public uint Total { get; set; }
        public DateTime LastLogged { get; set; }
    }

    private readonly Dictionary<BacnetObjectId, SimObject> _objects = [];
    private readonly HashSet<BacnetObjectId> _locked = [];
    private readonly Lock _lock = new();
    private readonly SimObject _device;

    public uint Instance { get; }
    public ushort VendorId { get; }

    public SimulatedDeviceModel(uint instance, string name, string vendorName, ushort vendorId, string model, string firmware)
    {
        Instance = instance;
        VendorId = vendorId;
        _device = Add(BacnetObjectTypes.OBJECT_DEVICE, instance, name);
        Set(_device, BacnetPropertyIds.PROP_VENDOR_NAME, Str(vendorName));
        Set(_device, BacnetPropertyIds.PROP_VENDOR_IDENTIFIER, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, (uint)vendorId));
        Set(_device, BacnetPropertyIds.PROP_MODEL_NAME, Str(model));
        Set(_device, BacnetPropertyIds.PROP_FIRMWARE_REVISION, Str(firmware));
        Set(_device, BacnetPropertyIds.PROP_PROTOCOL_VERSION, Uint(1));
        Set(_device, BacnetPropertyIds.PROP_PROTOCOL_REVISION, Uint(14));
        Set(_device, BacnetPropertyIds.PROP_MAX_APDU_LENGTH_ACCEPTED, Uint(480));
    }

    private static BacnetValue Str(string s) => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING, s);
    private static BacnetValue Uint(uint u) => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, u);
    private static BacnetValue Real(float f) => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, f);
    private static BacnetValue Enum(uint e) => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, e);
    private static BacnetValue Null() => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);

    private static void Set(SimObject o, BacnetPropertyIds p, params BacnetValue[] values) => o.Props[p] = [.. values];

    private SimObject Add(BacnetObjectTypes type, uint instance, string name)
    {
        var id = new BacnetObjectId(type, instance);
        var o = new SimObject(id);
        Set(o, BacnetPropertyIds.PROP_OBJECT_IDENTIFIER, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID, id));
        Set(o, BacnetPropertyIds.PROP_OBJECT_NAME, Str(name));
        Set(o, BacnetPropertyIds.PROP_OBJECT_TYPE, Enum((uint)type));
        _objects[id] = o;
        return o;
    }

    public void AddAnalogInput(uint instance, string name, string description, float value, BacnetUnitsId units) =>
        AddAnalog(BacnetObjectTypes.OBJECT_ANALOG_INPUT, instance, name, description, value, units, commandable: false);

    public void AddAnalogValue(uint instance, string name, string description, float value, BacnetUnitsId units, bool commandable = true) =>
        AddAnalog(BacnetObjectTypes.OBJECT_ANALOG_VALUE, instance, name, description, value, units, commandable);

    public void AddAnalogOutput(uint instance, string name, string description, float value, BacnetUnitsId units) =>
        AddAnalog(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, instance, name, description, value, units, commandable: true);

    private void AddAnalog(BacnetObjectTypes type, uint instance, string name, string description, float value,
        BacnetUnitsId units, bool commandable)
    {
        var o = Add(type, instance, name);
        Set(o, BacnetPropertyIds.PROP_DESCRIPTION, Str(description));
        Set(o, BacnetPropertyIds.PROP_PRESENT_VALUE, Real(value));
        Set(o, BacnetPropertyIds.PROP_UNITS, Enum((uint)units));
        Set(o, BacnetPropertyIds.PROP_OUT_OF_SERVICE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, false));
        if (commandable) MakeCommandable(o, Real(value));
    }

    public void AddBinaryInput(uint instance, string name, string description, bool value) =>
        AddBinary(BacnetObjectTypes.OBJECT_BINARY_INPUT, instance, name, description, value, commandable: false);

    public void AddBinaryValue(uint instance, string name, string description, bool value, bool commandable = true) =>
        AddBinary(BacnetObjectTypes.OBJECT_BINARY_VALUE, instance, name, description, value, commandable);

    public void AddBinaryOutput(uint instance, string name, string description, bool value) =>
        AddBinary(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, instance, name, description, value, commandable: true);

    private void AddBinary(BacnetObjectTypes type, uint instance, string name, string description, bool value, bool commandable)
    {
        var o = Add(type, instance, name);
        Set(o, BacnetPropertyIds.PROP_DESCRIPTION, Str(description));
        Set(o, BacnetPropertyIds.PROP_PRESENT_VALUE, Enum(value ? 1u : 0u));
        Set(o, BacnetPropertyIds.PROP_INACTIVE_TEXT, Str("Off"));
        Set(o, BacnetPropertyIds.PROP_ACTIVE_TEXT, Str("On"));
        Set(o, BacnetPropertyIds.PROP_OUT_OF_SERVICE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, false));
        if (commandable) MakeCommandable(o, Enum(value ? 1u : 0u));
    }

    /// <summary>A multi-state point: states are numbered from 1, and <paramref name="states"/> are their names (State_Text).</summary>
    public void AddMultiState(BacnetObjectTypes type, uint instance, string name, string description, uint value, string[] states)
    {
        var o = Add(type, instance, name);
        Set(o, BacnetPropertyIds.PROP_DESCRIPTION, Str(description));
        Set(o, BacnetPropertyIds.PROP_PRESENT_VALUE, Uint(value));
        Set(o, BacnetPropertyIds.PROP_NUMBER_OF_STATES, Uint((uint)states.Length));
        Set(o, BacnetPropertyIds.PROP_STATE_TEXT, [.. states.Select(Str)]);
        Set(o, BacnetPropertyIds.PROP_OUT_OF_SERVICE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, false));
        if (type != BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT) MakeCommandable(o, Uint(value));
    }

    private static void MakeCommandable(SimObject o, BacnetValue relinquishDefault)
    {
        o.Priority = new BacnetValue?[16];
        o.RelinquishDefault = relinquishDefault;
        Set(o, BacnetPropertyIds.PROP_PRIORITY_ARRAY, Null()); // placeholder so the property is listed; computed on read
        Set(o, BacnetPropertyIds.PROP_RELINQUISH_DEFAULT, relinquishDefault);
    }

    private readonly List<(BacnetObjectId Input, BacnetObjectId Output)> _followers = [];

    /// <summary>Make a commandable point refuse every write, like one protected by a lock or permission.</summary>
    public void Lock(BacnetObjectId id) => _locked.Add(id);

    /// <summary>Pre-load an override into a priority slot, as if someone (or a program) had already commanded the point.</summary>
    public void PreOverride(BacnetObjectId id, int priority, BacnetValue value)
    {
        lock (_lock)
            if (_objects.TryGetValue(id, out var o) && o.Priority is not null) o.Priority[priority - 1] = value;
    }

    /// <summary>Make an input mirror an output, like a fan proof switch following the fan command.</summary>
    public void AddFollower(BacnetObjectId input, BacnetObjectId output) => _followers.Add((input, output));

    /// <summary>
    /// Move the simulated world one step so live views have something to show: analog inputs wander a little,
    /// and followers copy their output's present value.
    /// </summary>
    public void Tick(Random rng)
    {
        lock (_lock)
        {
            foreach (var o in _objects.Values)
            {
                if (o.Id.type != BacnetObjectTypes.OBJECT_ANALOG_INPUT || o.Stuck) continue;
                if (o.Props.TryGetValue(BacnetPropertyIds.PROP_PRESENT_VALUE, out var pv) && pv is [{ Value: float f }])
                    pv[0] = Real((float)Math.Round(Math.Clamp(f + (rng.NextDouble() - 0.5) * 0.6, 40, 100), 1));
            }
            foreach (var (input, output) in _followers)
            {
                if (!_objects.TryGetValue(input, out var i) || !_objects.TryGetValue(output, out var outObj)) continue;
                var value = Raw(outObj, BacnetPropertyIds.PROP_PRESENT_VALUE);
                if (value is { Count: 1 }) i.Props[BacnetPropertyIds.PROP_PRESENT_VALUE] = [value[0]];
            }
            LogDue(DateTime.Now);
        }
    }

    /// <summary>
    /// Add a trend log that records <paramref name="source"/> every <paramref name="interval"/>, starting with
    /// <paramref name="historyRecords"/> of believable past data so there is something to read on day one.
    /// </summary>
    public void AddTrendLog(uint instance, string name, string description, BacnetObjectId source, TimeSpan interval,
        int historyRecords, int bufferSize = 1000)
    {
        var o = Add(BacnetObjectTypes.OBJECT_TRENDLOG, instance, name);
        var trend = new SimTrend(source, interval, bufferSize);
        o.Trend = trend;
        Set(o, BacnetPropertyIds.PROP_DESCRIPTION, Str(description));
        Set(o, BacnetPropertyIds.PROP_ENABLE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, true));
        Set(o, BacnetPropertyIds.PROP_LOG_INTERVAL, Uint((uint)(interval.TotalMilliseconds / 10))); // hundredths of a second
        Set(o, BacnetPropertyIds.PROP_STOP_WHEN_FULL, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, false));
        Set(o, BacnetPropertyIds.PROP_BUFFER_SIZE, Uint((uint)bufferSize));
        Set(o, BacnetPropertyIds.PROP_RECORD_COUNT, Uint(0)); // placeholders: the real values come from Raw()
        Set(o, BacnetPropertyIds.PROP_TOTAL_RECORD_COUNT, Uint(0));
        Set(o, BacnetPropertyIds.PROP_LOG_DEVICE_OBJECT_PROPERTY,
            new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_DEVICE_OBJECT_PROPERTY_REFERENCE,
                new BacnetDeviceObjectPropertyReference(source, BacnetPropertyIds.PROP_PRESENT_VALUE, null, uint.MaxValue)));

        // History: a gentle wave plus noise for numbers, an occasional switch for on/off points. Deterministic per log.
        var rng = new Random((int)(Instance * 31 + instance));
        var now = DateTime.Now;
        lock (_lock)
        {
            var current = _objects.TryGetValue(source, out var src) ? Raw(src, BacnetPropertyIds.PROP_PRESENT_VALUE) : null;
            var isReal = current is [{ Value: float }];
            var baseValue = isReal ? (float)current![0].Value : 0f;
            var on = rng.Next(2) == 0;
            for (var i = historyRecords; i >= 1; i--)
            {
                var stamp = now - TimeSpan.FromTicks(interval.Ticks * i);
                if (isReal)
                {
                    var v = baseValue + 1.5 * Math.Sin(i / 25.0) + (rng.NextDouble() - 0.5) * 0.6;
                    trend.Buffer.Add(new BacnetLogRecord(BacnetTrendLogValueType.TL_TYPE_REAL, (float)Math.Round(v, 1), stamp, 0));
                }
                else
                {
                    if (rng.Next(25) == 0) on = !on;
                    trend.Buffer.Add(new BacnetLogRecord(BacnetTrendLogValueType.TL_TYPE_BOOL, on, stamp, 0));
                }
                trend.Total++;
            }
            trend.LastLogged = now;
        }
    }

    /// <summary>Append a record to every trend log whose interval has passed. Called with the lock held.</summary>
    private void LogDue(DateTime now)
    {
        foreach (var o in _objects.Values)
        {
            if (o.Trend is not { } t || now - t.LastLogged < t.Interval) continue;
            t.LastLogged = now;
            if (!_objects.TryGetValue(t.Source, out var src)) continue;
            var v = Raw(src, BacnetPropertyIds.PROP_PRESENT_VALUE);
            if (v is not [{ } first]) continue;

            t.Buffer.Add(first.Value is float f
                ? new BacnetLogRecord(BacnetTrendLogValueType.TL_TYPE_REAL, f, now, 0)
                : new BacnetLogRecord(BacnetTrendLogValueType.TL_TYPE_BOOL, Convert.ToUInt32(first.Value) != 0, now, 0));
            t.Total++;
            if (t.Buffer.Count > t.BufferSize) t.Buffer.RemoveAt(0); // full buffer: the oldest record is lost
        }
    }

    /// <summary>
    /// The slice of a trend log a ReadRange request asks for (by position, by time, or everything).
    /// Positions are 1-based; a negative count reads backwards from the position.
    /// </summary>
    public bool TryReadLog(BacnetObjectId id, BacnetReadRangeRequestTypes type, uint position, DateTime time, int count,
        out List<BacnetLogRecord> slice, out uint firstSequence, out bool isFirst, out bool isLast, out SimError error)
    {
        lock (_lock)
        {
            slice = [];
            firstSequence = 0;
            isFirst = isLast = false;
            if (!Resolve(id, out var o))
            {
                error = new(BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT);
                return false;
            }
            if (o.Trend is not { } t)
            {
                error = new(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY);
                return false;
            }

            var n = t.Buffer.Count;
            var bufferFirstSeq = (int)t.Total - n + 1;
            int start, take;
            switch (type)
            {
                case BacnetReadRangeRequestTypes.RR_READ_ALL:
                    start = 0; take = n;
                    break;
                case BacnetReadRangeRequestTypes.RR_BY_TIME:
                    var firstAtOrAfter = t.Buffer.FindIndex(r => r.timestamp >= time);
                    if (firstAtOrAfter < 0) firstAtOrAfter = n;
                    if (count >= 0) { start = firstAtOrAfter; take = count; }
                    else { take = -count; start = firstAtOrAfter - take; }
                    break;
                case BacnetReadRangeRequestTypes.RR_BY_SEQUENCE:
                    var seqIndex = (int)position - bufferFirstSeq;
                    if (count >= 0) { start = seqIndex; take = count; }
                    else { take = -count; start = seqIndex - take + 1; }
                    break;
                default: // by position
                    if (count >= 0) { start = (int)position - 1; take = count; }
                    else { take = -count; start = (int)position - take; }
                    break;
            }

            if (start < 0) { take += start; start = 0; }
            take = Math.Clamp(take, 0, Math.Max(0, n - start));
            slice = start < n ? t.Buffer.GetRange(start, take) : [];
            firstSequence = (uint)Math.Max(1, bufferFirstSeq + start);
            isFirst = start == 0;
            isLast = start + take >= n;
            error = default;
            return true;
        }
    }

    /// <summary>A small but realistic VAV-style point set. Values vary a little with the instance so devices are distinguishable.</summary>
    public static SimulatedDeviceModel CreateSample(uint instance, string? name = null, bool stuck = false, bool protectedSetpoint = false)
    {
        var m = new SimulatedDeviceModel(instance, name ?? $"SIM-VAV-{instance}", "BACprobe Simulator", 999,
            "SimVAV-100", "1.0.0");
        var n = instance % 10;
        m.AddAnalogInput(1, "Zone Temp", "Zone temperature", 68f + n + 0.4f, BacnetUnitsId.UNITS_DEGREES_FAHRENHEIT);
        m.AddAnalogInput(2, "Discharge Air Temp", "Discharge air temperature", 55.2f, BacnetUnitsId.UNITS_DEGREES_FAHRENHEIT);
        m.AddAnalogValue(1, "Zone Setpoint", "Occupied cooling setpoint", 72f, BacnetUnitsId.UNITS_DEGREES_FAHRENHEIT);
        m.AddAnalogOutput(1, "Damper Position", "Supply damper command", 50f, BacnetUnitsId.UNITS_PERCENT);
        var damper = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1);
        Set(m._objects[damper], BacnetPropertyIds.PROP_MIN_PRES_VALUE, Real(0f)); // a damper position is 0 to 100 %
        Set(m._objects[damper], BacnetPropertyIds.PROP_MAX_PRES_VALUE, Real(100f));
        m.AddBinaryInput(1, "Fan Status", "Supply fan proof", true);
        m.AddBinaryValue(1, "Occupied", "Occupancy mode", true);
        m.AddBinaryOutput(1, "Fan Command", "Supply fan start/stop", false);
        m.AddFollower(new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_INPUT, 1), new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, 1));
        m.AddMultiState(BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE, 1, "Occupancy Mode", "Occupied / unoccupied / standby", 1,
            ["Occupied", "Unoccupied", "Standby"]);
        m.AddMultiState(BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT, 1, "Fan Speed", "Supply fan speed command", 3, ["Off", "Low", "Medium", "High"]);
        m.AddMultiState(BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT, 1, "Filter Status", "Filter differential pressure switch", 1,
            ["Clean", "Dirty", "Missing"]);
        // Troublemakers for testing the write explainer: a point already held at a high priority, and one that refuses writes.
        if (stuck) m.PreOverride(new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_VALUE, 1), 5, Enum(1));
        if (protectedSetpoint) m.Lock(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1));
        // Trend logs: a new record every 10 s so they visibly grow while you watch (real logs are usually 1 to 15 minutes).
        m.AddTrendLog(1, "Zone Temp Trend", "Zone temperature history", new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1),
            TimeSpan.FromSeconds(10), historyRecords: 300);
        m.AddTrendLog(2, "Fan Status Trend", "Supply fan proof history", new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_INPUT, 1),
            TimeSpan.FromSeconds(10), historyRecords: 120);
        return m;
    }

    private bool Resolve(BacnetObjectId id, out SimObject obj)
    {
        if (id.type == BacnetObjectTypes.OBJECT_DEVICE && id.instance == WildcardInstance) id = _device.Id;
        return _objects.TryGetValue(id, out obj!);
    }

    private List<BacnetPropertyIds> ListedProperties(SimObject o)
    {
        var list = o.Props.Keys.ToList();
        if (ReferenceEquals(o, _device)) list.Add(BacnetPropertyIds.PROP_OBJECT_LIST);
        if (HasStatus(o)) list.Add(BacnetPropertyIds.PROP_STATUS_FLAGS);
        return list;
    }

    /// <summary>Points (anything with a present value, other than the device) carry Status_Flags.</summary>
    private bool HasStatus(SimObject o) => !ReferenceEquals(o, _device) && o.Trend is null && o.Props.ContainsKey(BacnetPropertyIds.PROP_PRESENT_VALUE);

    private List<BacnetValue> ObjectList() =>
        _objects.Keys.OrderBy(k => k.type != BacnetObjectTypes.OBJECT_DEVICE).ThenBy(k => (int)k.type).ThenBy(k => k.instance)
            .Select(k => new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID, k)).ToList();

    private static bool IsArray(BacnetPropertyIds p) =>
        p is BacnetPropertyIds.PROP_OBJECT_LIST or BacnetPropertyIds.PROP_PRIORITY_ARRAY or BacnetPropertyIds.PROP_STATE_TEXT;

    private List<BacnetValue>? Raw(SimObject o, BacnetPropertyIds p)
    {
        if (p == BacnetPropertyIds.PROP_OBJECT_LIST && ReferenceEquals(o, _device)) return ObjectList();
        if (p == BacnetPropertyIds.PROP_STATUS_FLAGS && HasStatus(o))
            return [new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING, BacnetBitString.ConvertFromInt((uint)FlagsOf(o), 4))];
        if (o.Trend is { } t)
        {
            if (p == BacnetPropertyIds.PROP_RECORD_COUNT) return [Uint((uint)t.Buffer.Count)];
            if (p == BacnetPropertyIds.PROP_TOTAL_RECORD_COUNT) return [Uint(t.Total)];
        }
        if (o.Priority is not null)
        {
            if (p == BacnetPropertyIds.PROP_PRIORITY_ARRAY)
                return o.Priority.Select(v => v ?? Null()).ToList();
            if (p == BacnetPropertyIds.PROP_PRESENT_VALUE)
                return [o.Priority.FirstOrDefault(v => v is not null) ?? o.RelinquishDefault];
        }
        return o.Props.TryGetValue(p, out var vals) ? vals : null;
    }

    /// <summary>Property ids to return for ALL/REQUIRED/OPTIONAL requests.</summary>
    public IList<BacnetPropertyIds> AllProperties(BacnetObjectId id)
    {
        lock (_lock) return Resolve(id, out var o) ? ListedProperties(o) : [];
    }

    public bool TryRead(BacnetObjectId id, BacnetPropertyIds prop, uint arrayIndex,
        out List<BacnetValue> values, out SimError error)
    {
        lock (_lock)
        {
            values = [];
            if (!Resolve(id, out var o))
            {
                error = new(BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT);
                return false;
            }
            var raw = Raw(o, prop);
            if (raw is null)
            {
                error = new(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY);
                return false;
            }
            if (arrayIndex == ArrayAll)
            {
                values = [.. raw];
            }
            else if (!IsArray(prop))
            {
                error = new(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_PROPERTY_IS_NOT_AN_ARRAY);
                return false;
            }
            else if (arrayIndex == 0)
            {
                values = [Uint((uint)raw.Count)];
            }
            else if (arrayIndex > raw.Count)
            {
                error = new(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_INVALID_ARRAY_INDEX);
                return false;
            }
            else
            {
                values = [raw[(int)arrayIndex - 1]];
            }
            error = default;
            return true;
        }
    }

    /// <summary>Returns null on success, otherwise the BACnet error to send. Priority 0 means "not given" (16).</summary>
    public SimError? Write(BacnetObjectId id, BacnetPropertyIds prop, BacnetValue value, int priority, out string summary)
    {
        lock (_lock)
        {
            summary = "";
            if (!Resolve(id, out var o))
                return new SimError(BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT);
            if (!o.Props.ContainsKey(prop))
                return new SimError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY);
            if (prop != BacnetPropertyIds.PROP_PRESENT_VALUE)
                return new SimError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_WRITE_ACCESS_DENIED);

            var p = priority == 0 ? 16 : priority;
            if (p is < 1 or > 16)
                return new SimError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_VALUE_OUT_OF_RANGE);

            if (o.Priority is null)
            {
                // Non-commandable (inputs, or values with no priority array): writable only when out of service in real
                // devices; the simulator simply refuses so the "write denied" path can be exercised.
                return new SimError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_WRITE_ACCESS_DENIED);
            }

            if (_locked.Contains(id))
                return new SimError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_WRITE_ACCESS_DENIED);

            var relinquish = value.Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL;
            if (!relinquish)
            {
                // A real controller checks the kind of value and its limits before accepting a write.
                var wantsReal = id.type is BacnetObjectTypes.OBJECT_ANALOG_OUTPUT or BacnetObjectTypes.OBJECT_ANALOG_VALUE;
                var wantsEnum = id.type is BacnetObjectTypes.OBJECT_BINARY_OUTPUT or BacnetObjectTypes.OBJECT_BINARY_VALUE;
                var wantsState = id.type is BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT or BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE;
                if ((wantsReal && value.Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL)
                    || (wantsEnum && value.Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED)
                    || (wantsState && value.Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT))
                    return new SimError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_INVALID_DATA_TYPE);
                if (wantsState && o.Props.TryGetValue(BacnetPropertyIds.PROP_NUMBER_OF_STATES, out var count) && count is [{ Value: uint states }]
                    && value.Value is uint state && (state < 1 || state > states))
                    return new SimError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_VALUE_OUT_OF_RANGE);
                if (wantsReal && value.Value is float f
                    && o.Props.TryGetValue(BacnetPropertyIds.PROP_MIN_PRES_VALUE, out var lo) && lo is [{ Value: float min }]
                    && o.Props.TryGetValue(BacnetPropertyIds.PROP_MAX_PRES_VALUE, out var hi) && hi is [{ Value: float max }]
                    && (f < min || f > max))
                    return new SimError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_VALUE_OUT_OF_RANGE);
            }
            o.Priority[p - 1] = relinquish ? null : value;
            summary = relinquish ? $"relinquished priority {p}" : $"wrote {value.Value} at priority {p}";
            return null;
        }
    }
}
