using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Writing;

namespace BACprobe.Core.Tests;

public class OverrideTests
{
    private static BacnetValue Null() => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);
    private static BacnetValue Real(float f) => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, f);

    private static List<BacnetValue> Array16(params (int Priority, float Value)[] occupied)
    {
        var a = Enumerable.Repeat(Null(), 16).ToList();
        foreach (var (p, v) in occupied) a[p - 1] = Real(v);
        return a;
    }

    private static readonly BacnetObjectId Ao1 = new(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1);

    [Fact]
    public void Occupied_slots_report_priority_and_value()
    {
        var slots = PriorityArrayInfo.Occupied(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, Array16((8, 25f), (16, 50f)));
        Assert.Equal([8, 16], slots.Select(s => s.Priority));
        Assert.Equal("25", slots[0].ValueText);
        Assert.Equal("8 (Manual Operator) = 25", slots[0].Description);
    }

    [Fact]
    public void Empty_array_has_no_slots_and_no_override()
    {
        var slots = PriorityArrayInfo.Occupied(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, Array16());
        Assert.Empty(slots);
        Assert.False(PriorityArrayInfo.IsOverride(slots));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(8, true)]
    [InlineData(9, false)]
    [InlineData(16, false)]
    public void Only_manual_operator_and_higher_count_as_an_override(int priority, bool expected) =>
        Assert.Equal(expected, PriorityArrayInfo.IsOverride(PriorityArrayInfo.Occupied(
            BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, Array16((priority, 1f)))));

    [Fact]
    public void Binary_slots_read_active_inactive()
    {
        var values = Enumerable.Repeat(Null(), 16).ToList();
        values[7] = new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, 1u);
        var slot = Assert.Single(PriorityArrayInfo.Occupied(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, values));
        Assert.Equal("Active", slot.ValueText);
    }

    [Fact]
    public void Missing_or_oversized_arrays_are_handled()
    {
        Assert.Empty(PriorityArrayInfo.Occupied(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, null));
        var tooLong = Enumerable.Repeat(Real(1), 40).ToList(); // a misbehaving device
        Assert.Equal(16, PriorityArrayInfo.Occupied(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, tooLong).Count);
    }

    [Theory]
    [InlineData(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, true)]
    [InlineData(BacnetObjectTypes.OBJECT_BINARY_VALUE, true)]
    [InlineData(BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT, true)]
    [InlineData(BacnetObjectTypes.OBJECT_ANALOG_INPUT, false)]
    [InlineData(BacnetObjectTypes.OBJECT_SCHEDULE, false)]
    [InlineData(BacnetObjectTypes.OBJECT_DEVICE, false)]
    public void Only_commandable_types_are_asked_for_a_priority_array(BacnetObjectTypes type, bool expected) =>
        Assert.Equal(expected, PriorityArrayInfo.MayHavePriorityArray(type));

    [Fact]
    public void Summary_shows_the_controlling_priority_in_plain_english()
    {
        var s = new ObjectSummary
        {
            Id = Ao1,
            PrioritySlots = PriorityArrayInfo.Occupied(Ao1.type, Array16((8, 25f), (10, 30f), (16, 50f))),
        };
        Assert.True(s.IsOverridden);
        Assert.Equal("P8 Manual Operator", s.OverrideText);
        Assert.Contains("8 (Manual Operator) = 25", s.OverrideTooltip);
        Assert.Contains("16 (Default (lowest)) = 50", s.OverrideTooltip);
    }

    [Fact]
    public void Point_driven_only_at_program_priority_is_not_flagged()
    {
        var s = new ObjectSummary { Id = Ao1, PrioritySlots = PriorityArrayInfo.Occupied(Ao1.type, Array16((16, 50f))) };
        Assert.False(s.IsOverridden);
        Assert.Equal("", s.OverrideText);
        Assert.NotEmpty(s.OverrideTooltip);
    }

    [Fact]
    public void Summary_without_a_priority_array_has_nothing_to_show()
    {
        var s = new ObjectSummary { Id = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1) };
        Assert.False(s.IsOverridden);
        Assert.Equal("", s.OverrideText);
        Assert.Equal("", s.OverrideTooltip);
    }

    // --- confirmation dialog content ---

    private static DiscoveredDevice Dev() => new()
    {
        InstanceId = 1003, Address = null!, MaxApdu = 480,
        Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 999,
    };

    [Fact]
    public void Confirmation_facts_name_device_point_values_and_priority()
    {
        var req = new WriteRequest(Dev(), "SIM-VAV-1003", Ao1, "Damper Position", Real(25f), "25", 8, "50 %");
        Assert.Equal("Write 25 to Damper Position?", req.Headline);
        var facts = req.Facts.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("SIM-VAV-1003 (device 1003)", facts["Device"]);
        Assert.Equal("Damper Position (Analog Output 1)", facts["Point"]);
        Assert.Equal("50 %", facts["Now"]);
        Assert.Equal("25", facts["New value"]);
        Assert.Equal("8 - Manual Operator", facts["Priority"]);
        Assert.Contains("until you release it", req.Consequence);
        Assert.Null(req.Warning);
    }

    [Fact]
    public void Release_confirmation_has_no_new_value_row()
    {
        var req = new WriteRequest(Dev(), "SIM-VAV-1003", Ao1, "Damper Position", null, "release", 8);
        Assert.Equal("Release your override of Damper Position?", req.Headline);
        Assert.DoesNotContain(req.Facts, f => f.Label == "New value");
        Assert.Contains(req.Facts, f => f is { Label: "Priority", Value: "8 - Manual Operator" });
        Assert.Contains("next-highest priority", req.Consequence);
        Assert.Null(req.Warning);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(8, false)]
    public void Only_life_safety_and_critical_priorities_get_a_warning(int priority, bool warns)
    {
        var req = new WriteRequest(Dev(), "D", Ao1, "P", Real(1), "1", priority);
        Assert.Equal(warns, req.Warning is not null);
    }
}
