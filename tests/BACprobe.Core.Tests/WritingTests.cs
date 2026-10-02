using System.IO.BACnet;
using BACprobe.Core.Discovery;
using BACprobe.Core.Writing;

namespace BACprobe.Core.Tests;

public class WritingTests
{
    private static DiscoveredDevice Device(uint instance = 1001) => new()
    {
        InstanceId = instance, Address = null!, MaxApdu = 480,
        Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 999,
    };

    private static readonly BacnetObjectId Ao1 = new(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1);

    [Theory]
    [InlineData(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, "72.5", BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL)]
    [InlineData(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, "on", BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED)]
    [InlineData(BacnetObjectTypes.OBJECT_BINARY_VALUE, "Inactive", BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED)]
    [InlineData(BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT, "3", BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT)]
    public void Values_get_the_right_bacnet_type(BacnetObjectTypes type, string text, BacnetApplicationTags tag)
    {
        Assert.True(WriteValueParser.TryParse(type, text, out var v, out _));
        Assert.Equal(tag, v.Tag);
    }

    [Theory]
    [InlineData(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, "abc")]
    [InlineData(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, "NaN")]
    [InlineData(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, "")]
    [InlineData(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, "maybe")]
    [InlineData(BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE, "0")]
    [InlineData(BacnetObjectTypes.OBJECT_SCHEDULE, "1")]
    public void Bad_values_are_rejected_with_a_message(BacnetObjectTypes type, string text)
    {
        Assert.False(WriteValueParser.TryParse(type, text, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Binary_on_maps_to_1_and_off_to_0()
    {
        WriteValueParser.TryParse(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, "on", out var on, out _);
        WriteValueParser.TryParse(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, "off", out var off, out _);
        Assert.Equal(1u, on.Value);
        Assert.Equal(0u, off.Value);
    }

    [Fact]
    public void Manual_operator_is_the_default_choice()
    {
        Assert.Equal(8, PriorityChoice.Default.Number);
        Assert.Equal("Manual Operator", PriorityChoice.Default.Name);
        Assert.False(PriorityChoice.Default.Advanced);
        Assert.All(PriorityChoice.All.Where(p => p.Number <= 6), p => Assert.True(p.Advanced));
    }

    [Fact]
    public void Confirmation_names_the_priority_in_plain_english()
    {
        var req = new WriteRequest(Device(), "AHU-1", Ao1, "Damper Position",
            new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 25f), "25", 8, "50");
        var text = req.ConfirmationText();
        Assert.Contains("priority 8 (Manual Operator)", text);
        Assert.Contains("Damper Position", text);
        Assert.Contains("1001", text);
        Assert.Contains("currently 50", text);
        Assert.Contains("until you release", text);
        Assert.DoesNotContain("WARNING", text);
    }

    [Fact]
    public void Life_safety_priorities_get_a_warning()
    {
        var req = new WriteRequest(Device(), "AHU-1", Ao1, "Damper Position",
            new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 25f), "25", 1);
        Assert.Contains("WARNING", req.ConfirmationText());
    }

    [Fact]
    public void Release_confirmation_says_release_and_names_the_priority()
    {
        var req = new WriteRequest(Device(), "AHU-1", Ao1, "Damper Position", null, "release", 8);
        Assert.True(req.IsRelease);
        var text = req.ConfirmationText();
        Assert.StartsWith("Release your override", text);
        Assert.Contains("priority 8 (Manual Operator)", text);
    }

    [Fact]
    public void Tracker_keeps_one_entry_per_point_and_priority()
    {
        var t = new OverrideTracker();
        var d = Device();
        t.Record(new TrackedOverride(d, "AHU-1", Ao1, "Damper", 8, "25"));
        t.Record(new TrackedOverride(d, "AHU-1", Ao1, "Damper", 8, "30")); // same slot: replaced
        t.Record(new TrackedOverride(d, "AHU-1", Ao1, "Damper", 16, "40"));
        Assert.Equal(2, t.Active.Count);
        Assert.Equal("30", t.Active.Single(o => o.Priority == 8).ValueText);

        t.Remove(1001, Ao1, 8);
        Assert.Single(t.Active);
        t.Remove(1001, Ao1, 8); // removing twice is harmless
        Assert.Single(t.Active);
    }

    [Fact]
    public void Log_records_entries_and_mirrors_to_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bacprobe-test-{Guid.NewGuid():N}", "log.txt");
        try
        {
            var log = new WriteLog(path);
            WriteLogEntry? seen = null;
            log.Added += e => seen = e;
            log.Add(new WriteLogEntry(DateTimeOffset.Now, 1001, "AHU-1", "Damper (Analog Output 1)", "write 25", 8, true, "ok"));
            log.Add(new WriteLogEntry(DateTimeOffset.Now, 1001, "AHU-1", "Damper (Analog Output 1)", "release", 8, false, "timeout"));

            Assert.Equal(2, log.Entries.Count);
            Assert.NotNull(seen);
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Contains("write 25 @ 8 (Manual Operator) | OK", lines[0]);
            Assert.Contains("FAILED: timeout", lines[1]);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Error from device: ERROR_CLASS_PROPERTY - ERROR_CODE_WRITE_ACCESS_DENIED", "access denied")]
    [InlineData("Error from device: ERROR_CODE_VALUE_OUT_OF_RANGE", "out of range")]
    [InlineData("Wait Timeout", "timeout")]
    [InlineData("something odd", "returned an error")]
    public void Write_errors_always_carry_cause_and_next_step(string message, string expectedSummaryPart)
    {
        var e = WriteErrors.Explain(new InvalidOperationException(message));
        Assert.Contains(expectedSummaryPart, e.Summary);
        Assert.NotEmpty(e.Cause);
        Assert.NotEmpty(e.NextStep);
    }
}
