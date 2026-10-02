using System.IO.BACnet;
using BACprobe.Core.Browsing;

namespace BACprobe.Core.Tests;

public class BacnetNamesTests
{
    [Theory]
    [InlineData("ai:1", BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1u)]
    [InlineData("AV:12", BacnetObjectTypes.OBJECT_ANALOG_VALUE, 12u)]
    [InlineData("binary-output:3", BacnetObjectTypes.OBJECT_BINARY_OUTPUT, 3u)]
    [InlineData("multi-state-value:7", BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE, 7u)]
    public void Object_specs_parse(string spec, BacnetObjectTypes type, uint instance)
    {
        Assert.True(BacnetNames.TryParseObject(spec, out var id));
        Assert.Equal(type, id.type);
        Assert.Equal(instance, id.instance);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ai")]
    [InlineData("ai:")]
    [InlineData("ai:-1")]
    [InlineData("ai:4194304")]
    [InlineData("nonsense:1")]
    [InlineData("proprietary-min:1")]
    public void Bad_object_specs_are_rejected(string spec) =>
        Assert.False(BacnetNames.TryParseObject(spec, out _));

    [Theory]
    [InlineData("present-value")]
    [InlineData("Present Value")]
    [InlineData("PROP_PRESENT_VALUE")]
    [InlineData("85")]
    public void Property_names_parse(string text)
    {
        Assert.True(BacnetNames.TryParseProperty(text, out var p));
        Assert.Equal(BacnetPropertyIds.PROP_PRESENT_VALUE, p);
    }

    [Fact]
    public void Unknown_property_name_is_rejected() =>
        Assert.False(BacnetNames.TryParseProperty("not-a-property", out _));

    [Fact]
    public void Priority_8_is_manual_operator() =>
        Assert.Equal("Manual Operator", BacnetNames.PriorityName(8));

    [Fact]
    public void Priority_names_cover_1_to_16_only()
    {
        Assert.Equal("Manual Life Safety", BacnetNames.PriorityName(1));
        Assert.Equal("Default (lowest)", BacnetNames.PriorityName(16));
        Assert.Equal("priority 17", BacnetNames.PriorityName(17));
    }

    [Fact]
    public void Vendor_types_and_properties_are_labelled_vendor_specific()
    {
        Assert.Equal("Vendor-specific type 200", BacnetNames.ObjectTypeName((BacnetObjectTypes)200));
        Assert.Equal("vendor-specific (1234)", BacnetNames.PropertyName(1234));
        Assert.True(BacnetNames.IsVendorProperty(1234));
        Assert.False(BacnetNames.IsVendorProperty((uint)BacnetPropertyIds.PROP_PRESENT_VALUE));
    }

    [Fact]
    public void Standard_names_are_prettified()
    {
        Assert.Equal("Analog Input", BacnetNames.ObjectTypeName(BacnetObjectTypes.OBJECT_ANALOG_INPUT));
        Assert.Equal("Present Value", BacnetNames.PropertyName((uint)BacnetPropertyIds.PROP_PRESENT_VALUE));
    }

    [Fact]
    public void Units_use_symbols_when_known()
    {
        Assert.Equal("°F", BacnetNames.UnitsName((uint)BacnetUnitsId.UNITS_DEGREES_FAHRENHEIT));
        Assert.Equal("", BacnetNames.UnitsName((uint)BacnetUnitsId.UNITS_NO_UNITS));
        Assert.Equal("units 9999", BacnetNames.UnitsName(9999));
    }

    private static BacnetValue Tagged(BacnetApplicationTags tag, object? v) => new(tag, v);

    [Fact]
    public void Binary_present_value_reads_active_inactive()
    {
        var one = Tagged(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, 1u);
        var zero = Tagged(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, 0u);
        Assert.Equal("Active", BacnetNames.FormatValue(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, BacnetPropertyIds.PROP_PRESENT_VALUE, one));
        Assert.Equal("Inactive", BacnetNames.FormatValue(BacnetObjectTypes.OBJECT_BINARY_VALUE, BacnetPropertyIds.PROP_PRESENT_VALUE, zero));
        // Multi-state shares the tag but must stay numeric.
        Assert.Equal("1", BacnetNames.FormatValue(BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE, BacnetPropertyIds.PROP_PRESENT_VALUE, one));
    }

    [Fact]
    public void Reals_are_trimmed_to_two_decimals() =>
        Assert.Equal("72.4", BacnetNames.FormatValue(BacnetObjectTypes.OBJECT_ANALOG_INPUT, BacnetPropertyIds.PROP_PRESENT_VALUE,
            Tagged(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 72.4000015f)));

    [Fact]
    public void Per_property_errors_show_the_error_code()
    {
        var err = Tagged(BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR,
            new BacnetError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY));
        Assert.Equal("(error: unknown property)",
            BacnetNames.FormatValue(BacnetObjectTypes.OBJECT_ANALOG_INPUT, BacnetPropertyIds.PROP_PRIORITY_ARRAY, err));
    }

    [Fact]
    public void Priority_array_lists_only_occupied_slots_with_plain_names()
    {
        var empty = Tagged(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);
        var slots = Enumerable.Repeat(empty, 16).ToList();
        Assert.Equal("no overrides (all 16 slots empty)",
            BacnetNames.FormatValues(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, BacnetPropertyIds.PROP_PRIORITY_ARRAY, slots));

        slots[7] = Tagged(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 25f);
        Assert.Equal("8 (Manual Operator) = 25",
            BacnetNames.FormatValues(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, BacnetPropertyIds.PROP_PRIORITY_ARRAY, slots));
    }
}
