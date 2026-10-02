using System.IO.BACnet;

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
    }

    private readonly Dictionary<BacnetObjectId, SimObject> _objects = [];
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

    private static void MakeCommandable(SimObject o, BacnetValue relinquishDefault)
    {
        o.Priority = new BacnetValue?[16];
        o.RelinquishDefault = relinquishDefault;
        Set(o, BacnetPropertyIds.PROP_PRIORITY_ARRAY, Null()); // placeholder so the property is listed; computed on read
        Set(o, BacnetPropertyIds.PROP_RELINQUISH_DEFAULT, relinquishDefault);
    }

    /// <summary>A small but realistic VAV-style point set. Values vary a little with the instance so devices are distinguishable.</summary>
    public static SimulatedDeviceModel CreateSample(uint instance, string? name = null)
    {
        var m = new SimulatedDeviceModel(instance, name ?? $"SIM-VAV-{instance}", "BACprobe Simulator", 999,
            "SimVAV-100", "1.0.0");
        var n = instance % 10;
        m.AddAnalogInput(1, "Zone Temp", "Zone temperature", 68f + n + 0.4f, BacnetUnitsId.UNITS_DEGREES_FAHRENHEIT);
        m.AddAnalogInput(2, "Discharge Air Temp", "Discharge air temperature", 55.2f, BacnetUnitsId.UNITS_DEGREES_FAHRENHEIT);
        m.AddAnalogValue(1, "Zone Setpoint", "Occupied cooling setpoint", 72f, BacnetUnitsId.UNITS_DEGREES_FAHRENHEIT);
        m.AddAnalogOutput(1, "Damper Position", "Supply damper command", 50f, BacnetUnitsId.UNITS_PERCENT);
        m.AddBinaryInput(1, "Fan Status", "Supply fan proof", true);
        m.AddBinaryValue(1, "Occupied", "Occupancy mode", true);
        m.AddBinaryOutput(1, "Fan Command", "Supply fan start/stop", false);
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
        return list;
    }

    private List<BacnetValue> ObjectList() =>
        _objects.Keys.OrderBy(k => k.type != BacnetObjectTypes.OBJECT_DEVICE).ThenBy(k => (int)k.type).ThenBy(k => k.instance)
            .Select(k => new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID, k)).ToList();

    private static bool IsArray(BacnetPropertyIds p) =>
        p is BacnetPropertyIds.PROP_OBJECT_LIST or BacnetPropertyIds.PROP_PRIORITY_ARRAY;

    private List<BacnetValue>? Raw(SimObject o, BacnetPropertyIds p)
    {
        if (p == BacnetPropertyIds.PROP_OBJECT_LIST && ReferenceEquals(o, _device)) return ObjectList();
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

            var relinquish = value.Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL;
            o.Priority[p - 1] = relinquish ? null : value;
            summary = relinquish ? $"relinquished priority {p}" : $"wrote {value.Value} at priority {p}";
            return null;
        }
    }
}
