using System.IO.BACnet;
using BACprobe.Core.Browsing;

namespace BACprobe.Core.Tests;

public class ComparisonTests
{
    private static ObjectSummary P(BacnetObjectTypes t, uint i, string name, string value) =>
        new() { Id = new BacnetObjectId(t, i), Name = name, PresentValue = value };

    [Fact]
    public void Matching_points_with_equal_values_are_the_same()
    {
        var a = new[] { P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "Setpoint", "72") };
        var b = new[] { P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "Setpoint", "72") };
        var r = Assert.Single(Comparison.ComparePoints(a, b));
        Assert.Equal(CompareKind.Same, r.Kind);
        Assert.False(r.NamesDiffer);
    }

    [Fact]
    public void A_changed_setpoint_is_a_difference()
    {
        var a = new[] { P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "Cooling Setpoint", "72") };
        var b = new[] { P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "Cooling Setpoint", "68") };
        var r = Assert.Single(Comparison.ComparePoints(a, b));
        Assert.Equal(CompareKind.Different, r.Kind);
        Assert.Equal("72", r.ValueA);
        Assert.Equal("68", r.ValueB);
        Assert.Equal("different", r.KindText);
    }

    [Fact]
    public void A_point_on_only_one_device_is_reported_for_that_side()
    {
        var a = new[] { P(BacnetObjectTypes.OBJECT_BINARY_VALUE, 5, "Night Mode", "Inactive") };
        var b = new[] { P(BacnetObjectTypes.OBJECT_BINARY_VALUE, 6, "Boost", "Active") };
        var rows = Comparison.ComparePoints(a, b);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Kind == CompareKind.OnlyInA && r.Name == "Night Mode");
        Assert.Contains(rows, r => r.Kind == CompareKind.OnlyInB && r.Name == "Boost");
    }

    [Fact]
    public void A_renamed_point_is_flagged_even_when_the_value_matches()
    {
        var a = new[] { P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "Zone SP", "72") };
        var b = new[] { P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "Room Setpoint", "72") };
        var r = Assert.Single(Comparison.ComparePoints(a, b));
        Assert.Equal(CompareKind.Same, r.Kind);
        Assert.True(r.NamesDiffer);
    }

    [Fact]
    public void Live_input_differences_are_marked_and_can_be_set_aside()
    {
        var a = new[] { P(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1, "Zone Temp", "70.1"), P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "SP", "72") };
        var b = new[] { P(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1, "Zone Temp", "74.3"), P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "SP", "68") };
        var rows = Comparison.ComparePoints(a, b);
        Assert.Equal("different (live input)", rows.Single(r => r.Id.type == BacnetObjectTypes.OBJECT_ANALOG_INPUT).KindText);
        Assert.Contains("1 differ", Comparison.Summarise(rows, ignoreLiveInputs: true));
        Assert.Contains("1 live input difference(s) set aside", Comparison.Summarise(rows, ignoreLiveInputs: true));
        Assert.Contains("2 differ", Comparison.Summarise(rows, ignoreLiveInputs: false));
    }

    [Fact]
    public void Visible_rows_and_the_difference_check_share_one_rule()
    {
        var a = new[]
        {
            P(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1, "Zone Temp", "70"), P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "SP", "72"),
            P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 2, "Same", "5"),
        };
        var b = new[]
        {
            P(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1, "Zone Temp", "74"), P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "SP", "72"),
            P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 2, "Same", "5"),
        };
        var rows = Comparison.ComparePoints(a, b);
        Assert.Equal(3, Comparison.Visible(rows, ignoreLiveInputs: false, differencesOnly: false).Count);
        Assert.Equal(2, Comparison.Visible(rows, ignoreLiveInputs: true, differencesOnly: false).Count);  // the live input difference is set aside
        Assert.Single(Comparison.Visible(rows, ignoreLiveInputs: false, differencesOnly: true));          // only the input differs
        Assert.Empty(Comparison.Visible(rows, ignoreLiveInputs: true, differencesOnly: true));
        Assert.True(Comparison.HasDifferences(rows, ignoreLiveInputs: false));
        Assert.False(Comparison.HasDifferences(rows, ignoreLiveInputs: true));
    }

    [Fact]
    public void The_two_devices_own_objects_are_matched_as_the_device()
    {
        var a = new[] { P(BacnetObjectTypes.OBJECT_DEVICE, 1001, "AHU-1", ""), P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "SP", "72") };
        var b = new[] { P(BacnetObjectTypes.OBJECT_DEVICE, 1002, "AHU-2", ""), P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "SP", "72") };
        var rows = Comparison.ComparePoints(a, b);
        Assert.Equal(2, rows.Count);
        var dev = rows.Single(r => r.Id.type == BacnetObjectTypes.OBJECT_DEVICE);
        Assert.Equal(CompareKind.Same, dev.Kind);
        Assert.False(dev.NamesDiffer);
    }

    [Fact]
    public void Rows_come_out_in_object_order()
    {
        var a = new[] { P(BacnetObjectTypes.OBJECT_BINARY_VALUE, 2, "x", "1"), P(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 9, "y", "1"),
                        P(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 2, "z", "1") };
        var ids = Comparison.ComparePoints(a, a).Select(r => (r.Id.type, r.Id.instance)).ToList();
        Assert.Equal([(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 2u), (BacnetObjectTypes.OBJECT_ANALOG_INPUT, 9u), (BacnetObjectTypes.OBJECT_BINARY_VALUE, 2u)], ids);
    }

    private static PropertyRow R(string name, string value) => new(1, name, value, false, false);

    [Fact]
    public void Properties_are_matched_by_name_and_extras_are_kept()
    {
        var a = new[] { R("Object Name", "Setpoint"), R("Present Value", "72"), R("Min Pres Value", "50") };
        var b = new[] { R("Object Name", "Setpoint"), R("Present Value", "68"), R("Max Pres Value", "90") };
        var rows = Comparison.CompareProperties(a, b);
        Assert.Equal(CompareKind.Same, rows.Single(r => r.Property == "Object Name").Kind);
        Assert.Equal(CompareKind.Different, rows.Single(r => r.Property == "Present Value").Kind);
        Assert.Equal(CompareKind.OnlyInA, rows.Single(r => r.Property == "Min Pres Value").Kind);
        Assert.Equal(CompareKind.OnlyInB, rows.Single(r => r.Property == "Max Pres Value").Kind);
    }
}
