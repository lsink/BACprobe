using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Writing;

namespace BACprobe.Core.Tests;

public class WriteExplainerTests
{
    private static DiscoveredDevice Device() => new()
    {
        InstanceId = 1001, Address = null!, MaxApdu = 480,
        Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 999,
    };

    private static WriteRequest Request(BacnetObjectTypes type = BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, float value = 75f, int priority = 8)
    {
        BacnetValue v = type is BacnetObjectTypes.OBJECT_BINARY_VALUE or BacnetObjectTypes.OBJECT_BINARY_OUTPUT
            ? new(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, (uint)value)
            : new(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, value);
        return new(Device(), "AHU-1", new BacnetObjectId(type, 1), "Damper", v, value.ToString(), priority);
    }

    private static PointProbe Probe(double? now = 50, bool? array = true, bool? oos = false, double? min = null, double? max = null,
        params PrioritySlot[] slots) =>
        new(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, true, now?.ToString(), now, array, slots, oos, min, max, "%");

    private static string Text(PromptContent c) => c.Headline + " " + c.Body + " " + string.Join(" ", c.Facts.Select(f => f.Value));

    [Fact]
    public void Device_words_are_made_readable()
    {
        Assert.Equal("write access denied", WriteExplainer.DeviceSaid("Error from device: ERROR_CLASS_PROPERTY - ERROR_CODE_WRITE_ACCESS_DENIED"));
        Assert.Equal("Wait Timeout", WriteExplainer.DeviceSaid("Wait Timeout"));
    }

    [Fact]
    public void Timeout_says_the_write_may_or_may_not_have_happened()
    {
        var c = WriteExplainer.ExplainFailure(Request(), "Wait Timeout", null);
        Assert.Contains("did not answer", c.Headline);
        Assert.Contains("may or may not", c.Body);
    }

    [Fact]
    public void Out_of_range_quotes_the_limits()
    {
        var c = WriteExplainer.ExplainFailure(Request(value: 150), "ERROR_CODE_VALUE_OUT_OF_RANGE", Probe(min: 0, max: 100));
        Assert.Contains("0 to 100", c.Body);
        Assert.Contains("150", c.Body);
    }

    [Fact]
    public void Out_of_range_without_limits_still_helps()
    {
        var c = WriteExplainer.ExplainFailure(Request(), "ERROR_CODE_VALUE_OUT_OF_RANGE", null);
        Assert.Contains("limits", c.Body);
    }

    [Fact]
    public void Wrong_type_is_explained()
    {
        var c = WriteExplainer.ExplainFailure(Request(), "ERROR_CODE_INVALID_DATA_TYPE", Probe());
        Assert.Contains("kind of value", c.Headline);
    }

    [Fact]
    public void Access_denied_on_a_point_without_priority_array_says_not_commandable()
    {
        var c = WriteExplainer.ExplainFailure(Request(), "ERROR_CODE_WRITE_ACCESS_DENIED", Probe(array: false));
        Assert.Contains("cannot be overridden", c.Headline);
    }

    [Fact]
    public void Access_denied_at_priority_6_blames_priority_6()
    {
        var c = WriteExplainer.ExplainFailure(Request(priority: 6), "ERROR_CODE_WRITE_ACCESS_DENIED", Probe(array: true));
        Assert.Contains("Priority 6", c.Headline);
        Assert.Contains("priority 8", c.Body);
    }

    [Fact]
    public void Access_denied_on_a_commandable_point_says_protected()
    {
        var c = WriteExplainer.ExplainFailure(Request(), "ERROR_CODE_WRITE_ACCESS_DENIED", Probe(array: true));
        Assert.Contains("protects", c.Headline);
    }

    [Fact]
    public void Access_denied_with_unreachable_probe_admits_it_could_not_check()
    {
        var c = WriteExplainer.ExplainFailure(Request(), "ERROR_CODE_WRITE_ACCESS_DENIED", PointProbe.Unreachable(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT));
        Assert.Contains("could not check", c.Body);
        Assert.Contains("did not answer", Text(c));
    }

    [Theory]
    [InlineData("ERROR_CODE_UNKNOWN_OBJECT", "does not exist")]
    [InlineData("ERROR_CODE_UNKNOWN_PROPERTY", "no value that can be written")]
    [InlineData("ERROR_CODE_PASSWORD_FAILURE", "permission")]
    [InlineData("ABORT_BUFFER_OVERFLOW", "will not take writes")]
    [InlineData("ERROR_CODE_NO_SPACE_TO_WRITE_PROPERTY", "no room")]
    [InlineData("ERROR_CODE_SOMETHING_NEW", "refused the write")]
    public void Other_refusals_each_get_a_cause_and_next_step(string message, string headlinePart)
    {
        var c = WriteExplainer.ExplainFailure(Request(), message, Probe());
        Assert.Contains(headlinePart, c.Headline);
        Assert.Contains("Likely cause:", c.Body);
        Assert.Contains("Next step:", c.Body);
    }

    [Fact]
    public void High_priority_failures_carry_a_warning_and_normal_ones_do_not()
    {
        Assert.NotNull(WriteExplainer.ExplainFailure(Request(priority: 3), "ERROR_CODE_WRITE_ACCESS_DENIED", Probe()).Warning);
        Assert.Null(WriteExplainer.ExplainFailure(Request(priority: 8), "ERROR_CODE_WRITE_ACCESS_DENIED", Probe()).Warning);
    }

    // ---------- accepted but ineffective ----------

    [Fact]
    public void A_write_that_landed_is_effective_within_rounding()
    {
        Assert.True(WriteExplainer.IsEffective(Request(value: 75f), Probe(now: 75.04)));
        Assert.True(WriteExplainer.IsEffective(Request(value: 1000f), Probe(now: 1004))); // 0.5 % tolerance
        Assert.False(WriteExplainer.IsEffective(Request(value: 75f), Probe(now: 50)));
    }

    [Fact]
    public void Binary_values_must_match_exactly()
    {
        var probe = new PointProbe(BacnetObjectTypes.OBJECT_BINARY_VALUE, true, "active", 1, true, [], false, null, null, null);
        Assert.True(WriteExplainer.IsEffective(Request(BacnetObjectTypes.OBJECT_BINARY_VALUE, 1f), probe));
        Assert.False(WriteExplainer.IsEffective(Request(BacnetObjectTypes.OBJECT_BINARY_VALUE, 0f), probe));
    }

    [Fact]
    public void Unverifiable_writes_are_not_called_ineffective()
    {
        var unreachable = PointProbe.Unreachable(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT);
        Assert.True(WriteExplainer.IsEffective(Request(), unreachable));
        Assert.Null(WriteExplainer.ExplainIneffective(Request(), unreachable));
        Assert.True(WriteExplainer.IsEffective(Request(), Probe(now: null)));
    }

    [Fact]
    public void Releases_are_never_ineffective()
    {
        var release = Request() with { Value = null };
        Assert.Null(WriteExplainer.ExplainIneffective(release, Probe(now: 50)));
    }

    [Fact]
    public void A_higher_priority_holding_the_point_is_named()
    {
        var c = WriteExplainer.ExplainIneffective(Request(priority: 8), Probe(now: 50, slots: [new PrioritySlot(5, "50"), new PrioritySlot(8, "75")]))!;
        Assert.Contains("higher priority", c.Headline);
        Assert.Contains("Priority 5", c.Body);
        Assert.NotNull(c.Warning); // 5 is critical equipment
    }

    [Fact]
    public void A_higher_non_critical_priority_has_no_warning()
    {
        var c = WriteExplainer.ExplainIneffective(Request(priority: 12), Probe(now: 50, slots: [new PrioritySlot(8, "50")]))!;
        Assert.Contains("higher priority", c.Headline);
        Assert.Null(c.Warning);
    }

    [Fact]
    public void Our_priority_in_control_but_value_different_means_the_device_changed_it()
    {
        var c = WriteExplainer.ExplainIneffective(Request(priority: 8), Probe(now: 60, slots: [new PrioritySlot(8, "75")]))!;
        Assert.Contains("changed the value", c.Headline);
    }

    [Fact]
    public void Our_slot_gone_and_another_one_holding_means_a_program_took_it_back()
    {
        var c = WriteExplainer.ExplainIneffective(Request(priority: 8), Probe(now: 50, slots: [new PrioritySlot(10, "50")]))!;
        Assert.Contains("took it back", c.Headline);
    }

    [Fact]
    public void Out_of_service_is_named()
    {
        var c = WriteExplainer.ExplainIneffective(Request(), Probe(now: 50, oos: true))!;
        Assert.Contains("out of service", c.Headline);
    }

    [Fact]
    public void Nothing_found_still_gives_a_next_step()
    {
        var c = WriteExplainer.ExplainIneffective(Request(), Probe(now: 50))!;
        Assert.Contains("did not change", c.Headline);
        Assert.Contains("Next step:", c.Body);
        Assert.Equal("Write had no effect", c.Title);
    }
}
