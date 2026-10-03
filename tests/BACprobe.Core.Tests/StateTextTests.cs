using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Export;
using BACprobe.Core.Search;
using BACprobe.Core.Writing;

namespace BACprobe.Core.Tests;

public class StateTextTests
{
    private const BacnetObjectTypes Msv = BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE;
    private const BacnetObjectTypes Bo = BacnetObjectTypes.OBJECT_BINARY_OUTPUT;
    private static readonly string?[] Modes = ["Occupied", "Unoccupied", "Standby"];

    private static BacnetValue Text(string s) => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING, s);
    private static BacnetValue Unsigned(uint n) => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, n);
    private static BacnetValue Enumerated(uint n) => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, n);
    private static BacnetValue Null() => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);
    private static BacnetValue Error() =>
        new(BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR, new BacnetError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY));

    // --- labels ---

    [Theory]
    [InlineData("1", "Occupied (1)")]
    [InlineData("3", "Standby (3)")]
    [InlineData("4", "4")]  // the device sent a state it has no name for: show the number, never guess
    [InlineData("0", "0")]
    [InlineData("", "")]
    public void Multi_state_values_get_their_names(string raw, string expected) =>
        Assert.Equal(expected, StateText.Label(Msv, raw, Modes));

    [Fact]
    public void Without_names_the_raw_value_is_shown()
    {
        Assert.Equal("2", StateText.Label(Msv, "2", null));
        Assert.Equal("Active", StateText.Label(Bo, "Active", null));
    }

    [Fact]
    public void Binary_values_get_the_devices_words()
    {
        string?[] names = ["Closed", "Open"];
        Assert.Equal("Open (Active)", StateText.Label(Bo, "Active", names));
        Assert.Equal("Closed (Inactive)", StateText.Label(Bo, "Inactive", names));
    }

    [Fact]
    public void A_name_that_only_repeats_the_value_or_is_blank_is_not_doubled()
    {
        Assert.Equal("Active", StateText.Label(Bo, "Active", ["inactive", "ACTIVE"]));
        Assert.Equal("Inactive", StateText.Label(Bo, "Inactive", ["  ", "On"]));
        Assert.Equal("2", StateText.Label(Msv, "2", ["Occupied", null, "Standby"]));
    }

    [Fact]
    public void Analog_points_are_never_labelled() =>
        Assert.Equal("2", StateText.Label(BacnetObjectTypes.OBJECT_ANALOG_VALUE, "2", Modes));

    // --- collecting names from reads ---

    [Fact]
    public void State_text_becomes_the_name_list()
    {
        var names = StateText.Merge(null, Msv, BacnetPropertyIds.PROP_STATE_TEXT, [Text("Off"), Text("Low"), Text("High")]);
        Assert.Equal(["Off", "Low", "High"], names);
    }

    [Fact]
    public void Binary_texts_fill_inactive_then_active_in_either_order()
    {
        var names = StateText.Merge(null, Bo, BacnetPropertyIds.PROP_ACTIVE_TEXT, [Text("Open")]);
        names = StateText.Merge(names, Bo, BacnetPropertyIds.PROP_INACTIVE_TEXT, [Text("Closed")]);
        Assert.Equal(["Closed", "Open"], names);
    }

    [Fact]
    public void Errors_and_other_properties_leave_the_names_alone()
    {
        IReadOnlyList<string?> before = ["Closed", "Open"];
        Assert.Same(before, StateText.Merge(before, Bo, BacnetPropertyIds.PROP_ACTIVE_TEXT, [Error()]));
        Assert.Same(before, StateText.Merge(before, Bo, BacnetPropertyIds.PROP_PRESENT_VALUE, [Enumerated(1)]));
        Assert.Null(StateText.Merge(null, BacnetObjectTypes.OBJECT_ANALOG_INPUT, BacnetPropertyIds.PROP_STATE_TEXT, [Text("x")]));
    }

    // --- choices for writing ---

    [Fact]
    public void Binary_choices_use_the_devices_words_and_fall_back_to_on_off()
    {
        Assert.Equal([("On (Active)", "active"), ("Off (Inactive)", "inactive")], StateText.Choices(Bo, null));
        Assert.Equal([("Open (Active)", "active"), ("Closed (Inactive)", "inactive")], StateText.Choices(Bo, ["Closed", "Open"]));
    }

    [Fact]
    public void Multi_state_choices_list_every_state_in_order_or_none_without_names()
    {
        Assert.Equal([("Occupied (1)", "1"), ("Unoccupied (2)", "2"), ("Standby (3)", "3")], StateText.Choices(Msv, Modes));
        Assert.Empty(StateText.Choices(Msv, null)); // no names: the tech types a number instead
        Assert.Empty(StateText.Choices(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, Modes));
    }

    [Fact]
    public void A_value_about_to_be_written_is_labelled()
    {
        Assert.Equal("Standby (3)", StateText.Describe(Msv, Unsigned(3), Modes));
        Assert.Equal("Open (Active)", StateText.Describe(Bo, Enumerated(1), ["Closed", "Open"]));
    }

    // --- parsing what the tech typed ---

    [Theory]
    [InlineData("standby", 3u)]
    [InlineData(" Unoccupied ", 2u)]
    [InlineData("1", 1u)]
    public void Multi_state_writes_accept_a_state_name_or_number(string typed, uint expected)
    {
        Assert.True(WriteValueParser.TryParse(Msv, typed, Modes, out var v, out _));
        Assert.Equal(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, v.Tag);
        Assert.Equal(expected, v.Value);
    }

    [Fact]
    public void A_state_the_point_does_not_have_is_refused_with_its_range()
    {
        Assert.False(WriteValueParser.TryParse(Msv, "7", Modes, out _, out var error));
        Assert.Contains("1 to 3", error);
        Assert.False(WriteValueParser.TryParse(Msv, "turbo", Modes, out _, out error));
        Assert.Contains("not one of this point's states", error);
    }

    [Fact]
    public void Without_names_any_state_number_is_left_for_the_device_to_judge() =>
        Assert.True(WriteValueParser.TryParse(Msv, "7", null, out _, out _));

    [Fact]
    public void Binary_writes_accept_the_devices_words()
    {
        Assert.True(WriteValueParser.TryParse(Bo, "open", ["Closed", "Open"], out var v, out _));
        Assert.Equal(1u, v.Value);
        Assert.True(WriteValueParser.TryParse(Bo, "Closed", ["Closed", "Open"], out v, out _));
        Assert.Equal(0u, v.Value);
        Assert.True(WriteValueParser.TryParse(Bo, "on", ["Closed", "Open"], out v, out _)); // the standard words still work
        Assert.Equal(1u, v.Value);
    }

    // --- where the labels show up ---

    private static ObjectSummary Mode(string pv, params PrioritySlot[] slots) =>
        new() { Id = new BacnetObjectId(Msv, 1), Name = "Occupancy Mode", PresentValue = pv, StateNames = Modes, PrioritySlots = slots };

    [Fact]
    public void A_summary_shows_names_in_its_value_and_priority_array()
    {
        var s = Mode("3", new PrioritySlot(8, "3"), new PrioritySlot(16, "1"));
        Assert.Equal("Standby (3)", s.ValueText);
        Assert.Equal("8 (Manual Operator) = Standby (3); 16 (Default (lowest)) = Occupied (1)", s.PriorityArrayText);
        Assert.Contains("Standby (3)", s.OverrideTooltip);
        Assert.Equal("3", s.PresentValue); // the raw value is kept: live updates compare it
    }

    [Fact]
    public void Properties_are_labelled_even_when_state_text_comes_after_present_value()
    {
        var pa = new List<BacnetValue>(Enumerable.Repeat(Null(), 16)) { [7] = Unsigned(2) };
        var rows = DeviceBrowser.ToRows(Msv,
        [
            ((uint)BacnetPropertyIds.PROP_PRESENT_VALUE, [Unsigned(2)]),
            ((uint)BacnetPropertyIds.PROP_PRIORITY_ARRAY, pa),
            ((uint)BacnetPropertyIds.PROP_RELINQUISH_DEFAULT, [Unsigned(1)]),
            ((uint)BacnetPropertyIds.PROP_STATE_TEXT, [Text("Occupied"), Text("Unoccupied"), Text("Standby")]),
        ]);
        Assert.Equal("Unoccupied (2)", rows[0].Display);
        Assert.Equal("8 (Manual Operator) = Unoccupied (2)", rows[1].Display);
        Assert.Equal("Occupied (1)", rows[2].Display);
        Assert.Equal("1 = Occupied; 2 = Unoccupied; 3 = Standby", rows[3].Display);
    }

    [Fact]
    public void Find_matches_a_state_name_and_export_shows_it()
    {
        var d = new DiscoveredDevice
        {
            InstanceId = 1001, Address = new BacnetAddress(BacnetAddressTypes.IP, "10.0.0.1:47808", 0), MaxApdu = 480,
            Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 999, ObjectName = "AHU-1",
        };
        var device = new ExportDevice(d, "AHU-1", [Mode("3")]);
        Assert.Single(PointSearch.Search([device], "standby"));
        Assert.Contains(PointExporter.CsvLines([device]), line => line.Contains("Standby (3)"));
    }
}
