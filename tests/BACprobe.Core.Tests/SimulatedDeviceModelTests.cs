using System.IO.BACnet;
using BACprobe.Core.Simulation;

namespace BACprobe.Core.Tests;

public class SimulatedDeviceModelTests
{
    private static readonly BacnetObjectId Dev = new(BacnetObjectTypes.OBJECT_DEVICE, 1001);
    private static readonly BacnetObjectId Ao1 = new(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1);
    private static readonly BacnetObjectId Ai1 = new(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1);
    private const uint All = uint.MaxValue;

    private static SimulatedDeviceModel Model() => SimulatedDeviceModel.CreateSample(1001);

    private static BacnetValue Real(float f) => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, f);
    private static BacnetValue Relinquish() => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);

    [Fact]
    public void Device_name_and_vendor_are_readable()
    {
        var m = Model();
        Assert.True(m.TryRead(Dev, BacnetPropertyIds.PROP_OBJECT_NAME, All, out var v, out _));
        Assert.Equal("SIM-VAV-1001", v[0].Value);
        Assert.True(m.TryRead(Dev, BacnetPropertyIds.PROP_VENDOR_NAME, All, out v, out _));
        Assert.Equal("BACprobe Simulator", v[0].Value);
    }

    [Fact]
    public void Wildcard_device_instance_resolves_to_this_device()
    {
        var wildcard = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, SimulatedDeviceModel.WildcardInstance);
        Assert.True(Model().TryRead(wildcard, BacnetPropertyIds.PROP_OBJECT_NAME, All, out _, out _));
    }

    [Fact]
    public void Object_list_supports_count_and_index()
    {
        var m = Model();
        Assert.True(m.TryRead(Dev, BacnetPropertyIds.PROP_OBJECT_LIST, All, out var all, out _));
        Assert.True(m.TryRead(Dev, BacnetPropertyIds.PROP_OBJECT_LIST, 0, out var count, out _));
        Assert.Equal((uint)all.Count, count[0].Value);
        Assert.True(m.TryRead(Dev, BacnetPropertyIds.PROP_OBJECT_LIST, 1, out var first, out _));
        Assert.Equal(Dev, first[0].Value);
        Assert.False(m.TryRead(Dev, BacnetPropertyIds.PROP_OBJECT_LIST, (uint)all.Count + 1, out _, out var err));
        Assert.Equal(BacnetErrorCodes.ERROR_CODE_INVALID_ARRAY_INDEX, err.Code);
    }

    [Fact]
    public void Unknown_object_and_property_return_distinct_errors()
    {
        var m = Model();
        Assert.False(m.TryRead(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 99), BacnetPropertyIds.PROP_PRESENT_VALUE, All, out _, out var e1));
        Assert.Equal(BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT, e1.Code);
        Assert.False(m.TryRead(Ai1, BacnetPropertyIds.PROP_PRIORITY_ARRAY, All, out _, out var e2));
        Assert.Equal(BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY, e2.Code);
    }

    [Fact]
    public void Indexing_a_non_array_property_is_an_error()
    {
        Assert.False(Model().TryRead(Ai1, BacnetPropertyIds.PROP_PRESENT_VALUE, 1, out _, out var err));
        Assert.Equal(BacnetErrorCodes.ERROR_CODE_PROPERTY_IS_NOT_AN_ARRAY, err.Code);
    }

    [Fact]
    public void Highest_priority_wins_and_relinquish_falls_back()
    {
        var m = Model();
        Assert.Null(m.Write(Ao1, BacnetPropertyIds.PROP_PRESENT_VALUE, Real(80), 16, out _));
        Assert.Null(m.Write(Ao1, BacnetPropertyIds.PROP_PRESENT_VALUE, Real(10), 8, out _));
        m.TryRead(Ao1, BacnetPropertyIds.PROP_PRESENT_VALUE, All, out var pv, out _);
        Assert.Equal(10f, pv[0].Value);

        Assert.Null(m.Write(Ao1, BacnetPropertyIds.PROP_PRESENT_VALUE, Relinquish(), 8, out _));
        m.TryRead(Ao1, BacnetPropertyIds.PROP_PRESENT_VALUE, All, out pv, out _);
        Assert.Equal(80f, pv[0].Value);

        Assert.Null(m.Write(Ao1, BacnetPropertyIds.PROP_PRESENT_VALUE, Relinquish(), 16, out _));
        m.TryRead(Ao1, BacnetPropertyIds.PROP_PRESENT_VALUE, All, out pv, out _);
        Assert.Equal(50f, pv[0].Value); // relinquish default
    }

    [Fact]
    public void Priority_array_shows_the_override_at_the_right_slot()
    {
        var m = Model();
        m.Write(Ao1, BacnetPropertyIds.PROP_PRESENT_VALUE, Real(25), 8, out _);
        m.TryRead(Ao1, BacnetPropertyIds.PROP_PRIORITY_ARRAY, All, out var arr, out _);
        Assert.Equal(16, arr.Count);
        Assert.Equal(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, arr[7].Tag);
        Assert.All(arr.Where((_, i) => i != 7), v => Assert.Equal(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, v.Tag));
    }

    [Fact]
    public void Missing_priority_defaults_to_16()
    {
        var m = Model();
        Assert.Null(m.Write(Ao1, BacnetPropertyIds.PROP_PRESENT_VALUE, Real(33), 0, out var summary));
        Assert.Contains("priority 16", summary);
    }

    [Fact]
    public void Writes_to_inputs_and_non_present_value_are_denied()
    {
        var m = Model();
        var e1 = m.Write(Ai1, BacnetPropertyIds.PROP_PRESENT_VALUE, Real(1), 8, out _);
        Assert.Equal(BacnetErrorCodes.ERROR_CODE_WRITE_ACCESS_DENIED, e1!.Value.Code);
        var e2 = m.Write(Ao1, BacnetPropertyIds.PROP_OBJECT_NAME, Real(1), 8, out _);
        Assert.Equal(BacnetErrorCodes.ERROR_CODE_WRITE_ACCESS_DENIED, e2!.Value.Code);
    }

    [Fact]
    public void Out_of_range_priority_is_rejected()
    {
        var e = Model().Write(Ao1, BacnetPropertyIds.PROP_PRESENT_VALUE, Real(1), 17, out _);
        Assert.Equal(BacnetErrorCodes.ERROR_CODE_VALUE_OUT_OF_RANGE, e!.Value.Code);
    }
}
