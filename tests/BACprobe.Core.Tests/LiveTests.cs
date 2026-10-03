using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Simulation;

namespace BACprobe.Core.Tests;

public class LiveTests
{
    private static readonly BacnetObjectId Ao1 = new(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1);
    private static readonly BacnetObjectId Ai1 = new(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1);
    private static readonly BacnetObjectId Bi1 = new(BacnetObjectTypes.OBJECT_BINARY_INPUT, 1);
    private static readonly BacnetObjectId Bo1 = new(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, 1);
    private const uint All = uint.MaxValue;

    // --- change detection ---

    [Fact]
    public void Same_values_are_not_a_change()
    {
        var s = new ObjectSummary { Id = Ao1, PresentValue = "50", PrioritySlots = [new PrioritySlot(16, "50")] };
        Assert.False(s.ApplyLive("50", [new PrioritySlot(16, "50")]));
    }

    [Fact]
    public void A_new_present_value_is_a_change_and_is_stored()
    {
        var s = new ObjectSummary { Id = Ai1, PresentValue = "70.1" };
        Assert.True(s.ApplyLive("70.4", null));
        Assert.Equal("70.4", s.PresentValue);
    }

    [Fact]
    public void A_new_override_is_a_change_and_flags_the_point()
    {
        var s = new ObjectSummary { Id = Ao1, PresentValue = "50" };
        Assert.False(s.IsOverridden);
        Assert.True(s.ApplyLive("25", [new PrioritySlot(8, "25")]));
        Assert.True(s.IsOverridden);
        Assert.Equal("P8 Manual Operator", s.OverrideText);
    }

    [Fact]
    public void A_released_override_is_a_change_and_clears_the_flag()
    {
        var s = new ObjectSummary { Id = Ao1, PresentValue = "25", PrioritySlots = [new PrioritySlot(8, "25")] };
        Assert.True(s.ApplyLive("50", []));
        Assert.False(s.IsOverridden);
    }

    [Fact]
    public void A_fresh_read_updates_the_same_instance_and_keeps_what_it_lacks()
    {
        var s = new ObjectSummary { Id = Ao1, Name = "Damper", Description = "Supply damper", PresentValue = "50", Units = "%" };
        s.UpdateFrom(new ObjectSummary { Id = Ao1, PresentValue = "25", PrioritySlots = [new PrioritySlot(8, "25")] });
        Assert.Equal("25", s.PresentValue);
        Assert.True(s.IsOverridden);
        Assert.Equal("Damper", s.Name);   // the fresh read did not have it: kept
        Assert.Equal("%", s.Units);
    }

    [Fact]
    public void Missing_data_leaves_the_old_value_alone()
    {
        // A read that failed for one property must not blank what we already know.
        var s = new ObjectSummary { Id = Ao1, PresentValue = "25", PrioritySlots = [new PrioritySlot(8, "25")] };
        Assert.False(s.ApplyLive(null, null));
        Assert.Equal("25", s.PresentValue);
        Assert.True(s.IsOverridden);
    }

    [Theory]
    [InlineData(BacnetObjectTypes.OBJECT_ANALOG_INPUT, true)]
    [InlineData(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, true)]
    [InlineData(BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE, true)]
    [InlineData(BacnetObjectTypes.OBJECT_DEVICE, false)]
    [InlineData(BacnetObjectTypes.OBJECT_FILE, false)]
    [InlineData(BacnetObjectTypes.OBJECT_CALENDAR, false)]
    public void Only_value_bearing_types_are_refreshed(BacnetObjectTypes type, bool expected) =>
        Assert.Equal(expected, BacnetNames.HasLivePresentValue(type));

    // --- the simulator's drifting world ---

    private static float Real(SimulatedDeviceModel m, BacnetObjectId id)
    {
        Assert.True(m.TryRead(id, BacnetPropertyIds.PROP_PRESENT_VALUE, All, out var v, out _));
        return (float)v[0].Value;
    }

    [Fact]
    public void Analog_inputs_wander_but_stay_in_range_and_outputs_do_not_move()
    {
        var m = SimulatedDeviceModel.CreateSample(1001);
        var rng = new Random(42);
        var start = Real(m, Ai1);
        var seen = new HashSet<float> { start };
        var damper = Real(m, Ao1);
        for (var i = 0; i < 500; i++)
        {
            m.Tick(rng);
            var v = Real(m, Ai1);
            Assert.InRange(v, 40f, 100f);
            seen.Add(v);
        }
        Assert.True(seen.Count > 5, "the sensor should actually move");
        Assert.Equal(damper, Real(m, Ao1));
    }

    [Fact]
    public void Fan_status_follows_fan_command_including_overrides_and_releases()
    {
        var m = SimulatedDeviceModel.CreateSample(1001);
        var rng = new Random(1);
        string Status()
        {
            m.TryRead(Bi1, BacnetPropertyIds.PROP_PRESENT_VALUE, All, out var v, out _);
            return Convert.ToUInt32(v[0].Value) == 1 ? "on" : "off";
        }

        m.Tick(rng);
        Assert.Equal("off", Status()); // command defaults to off

        m.Write(Bo1, BacnetPropertyIds.PROP_PRESENT_VALUE,
            new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, 1u), 8, out _);
        m.Tick(rng);
        Assert.Equal("on", Status());

        m.Write(Bo1, BacnetPropertyIds.PROP_PRESENT_VALUE,
            new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null), 8, out _);
        m.Tick(rng);
        Assert.Equal("off", Status());
    }

    [Theory]
    [InlineData(1476u, BacnetSegmentations.SEGMENTATION_NONE, 8)]
    [InlineData(480u, BacnetSegmentations.SEGMENTATION_NONE, 3)]   // a typical MS/TP controller
    [InlineData(206u, BacnetSegmentations.SEGMENTATION_RECEIVE, 1)] // can receive segments but not send them
    [InlineData(50u, BacnetSegmentations.SEGMENTATION_NONE, 1)]     // never zero
    [InlineData(480u, BacnetSegmentations.SEGMENTATION_BOTH, 8)]    // segments its answers: full batch
    [InlineData(480u, BacnetSegmentations.SEGMENTATION_TRANSMIT, 8)]
    public void Summary_batches_fit_what_the_device_can_send(uint maxApdu, BacnetSegmentations seg, int expected) =>
        Assert.Equal(expected, DeviceBrowser.BatchSizeFor(maxApdu, seg));
}
