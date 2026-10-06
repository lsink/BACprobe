using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Writing;

namespace BACprobe.Core.Tests;

public class PropertyEditTests
{
    private const BacnetApplicationTags Real = BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL;
    private const BacnetApplicationTags Text = BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING;
    private const BacnetApplicationTags Enumerated = BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED;

    private static PropertyRow Row(BacnetPropertyIds p, BacnetApplicationTags? tag, int count = 1, bool vendor = false, bool error = false) =>
        new((uint)p, BacnetNames.PropertyName((uint)p), "x", vendor, error, tag, count);

    [Fact]
    public void Settings_are_editable_and_reports_are_not()
    {
        Assert.True(PropertyEdit.CanEdit(Row(BacnetPropertyIds.PROP_HIGH_LIMIT, Real), out _));
        Assert.True(PropertyEdit.CanEdit(Row(BacnetPropertyIds.PROP_DESCRIPTION, Text), out _));
        Assert.False(PropertyEdit.CanEdit(Row(BacnetPropertyIds.PROP_STATUS_FLAGS, BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING), out var ro));
        Assert.Contains("read-only", ro);
    }

    [Fact]
    public void Present_value_and_out_of_service_point_to_their_own_controls()
    {
        Assert.False(PropertyEdit.CanEdit(Row(BacnetPropertyIds.PROP_PRESENT_VALUE, Real), out var pv));
        Assert.Contains("Override", pv);
        Assert.False(PropertyEdit.CanEdit(Row(BacnetPropertyIds.PROP_OUT_OF_SERVICE, BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN), out var oos));
        Assert.Contains("Out of service", oos);
    }

    [Fact]
    public void Lists_vendor_values_and_errors_are_left_alone()
    {
        Assert.False(PropertyEdit.CanEdit(Row(BacnetPropertyIds.PROP_STATE_TEXT, null, count: 3), out var list));
        Assert.Contains("cannot edit this kind", list);
        Assert.False(PropertyEdit.CanEdit(Row((BacnetPropertyIds)600, Real, vendor: true), out var vendor));
        Assert.Contains("Vendor-specific", vendor);
        Assert.False(PropertyEdit.CanEdit(Row(BacnetPropertyIds.PROP_HIGH_LIMIT, null, error: true), out _));
    }

    [Theory]
    [InlineData(Real, "80", 80f)]
    [InlineData(Real, " 72.5 ", 72.5f)]
    [InlineData(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, "5", 5u)]
    [InlineData(BacnetApplicationTags.BACNET_APPLICATION_TAG_SIGNED_INT, "-3", -3)]
    [InlineData(BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, "yes", true)]
    [InlineData(Enumerated, "64", 64u)]
    [InlineData(Enumerated, "Degrees Fahrenheit (64)", 64u)]
    [InlineData(Text, "  Supply air  ", "  Supply air  ")]
    public void Typed_text_becomes_the_property_s_own_type(BacnetApplicationTags tag, string text, object expected)
    {
        Assert.True(PropertyEdit.TryParse(tag, text, out var v, out var error), error);
        Assert.Equal(tag, v.Tag);
        Assert.Equal(expected, v.Value);
    }

    [Fact]
    public void Wrong_input_says_what_was_expected()
    {
        Assert.False(PropertyEdit.TryParse(Real, "hot", out _, out var e1));
        Assert.Contains("a number", e1);
        Assert.False(PropertyEdit.TryParse(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, "-1", out _, out var e2));
        Assert.Contains("whole number", e2);
        Assert.False(PropertyEdit.TryParse(Real, "NaN", out _, out _));
    }

    [Fact]
    public void The_confirmation_shows_old_and_new_and_warns_about_renaming()
    {
        var device = new DiscoveredDevice
        {
            InstanceId = 1001, Address = new BacnetAddress(BacnetAddressTypes.IP, "10.0.0.5:47808"), MaxApdu = 1476,
            Segmentation = BacnetSegmentations.SEGMENTATION_BOTH, VendorId = 5,
        };
        var ai1 = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1);
        var limit = new PropertyWriteRequest(device, "VAV-1", ai1, "Zone Temp", BacnetPropertyIds.PROP_HIGH_LIMIT,
            new BacnetValue(Real, 80f), "80", "75");
        Assert.Equal("Change Zone Temp's High Limit to 80?", limit.Headline);
        Assert.Contains(limit.Facts, f => f.Label == "Now" && f.Value == "75");
        Assert.Equal("set High Limit: 75 -> 80", limit.LogAction);
        Assert.Contains("not an override", limit.Consequence);
        Assert.Null(limit.Warning);

        var rename = limit with { Property = BacnetPropertyIds.PROP_OBJECT_NAME, Value = new BacnetValue(Text, "ZN-T"), ValueText = "ZN-T" };
        Assert.NotNull(rename.Warning);
    }
}
