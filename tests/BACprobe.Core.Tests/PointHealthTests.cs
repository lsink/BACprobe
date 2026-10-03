using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Export;
using BACprobe.Core.Live;
using BACprobe.Core.Search;
using BACprobe.Core.Simulation;

namespace BACprobe.Core.Tests;

public class PointHealthTests
{
    private const BacnetStatusFlags Alarm = BacnetStatusFlags.STATUS_FLAG_IN_ALARM;
    private const BacnetStatusFlags Fault = BacnetStatusFlags.STATUS_FLAG_FAULT;
    private const BacnetStatusFlags Oos = BacnetStatusFlags.STATUS_FLAG_OUT_OF_SERVICE;
    private const uint OpenLoop = (uint)BacnetReliability.RELIABILITY_OPEN_LOOP;

    private static BacnetValue Bits(BacnetStatusFlags f) =>
        new(BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING, BacnetBitString.ConvertFromInt((uint)f, 4));

    private static BacnetValue Enumerated(uint n) => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, n);

    // --- decoding ---

    [Fact]
    public void Status_flags_and_reliability_decode()
    {
        Assert.Equal(Fault | Oos, PointHealth.FlagsFrom([Bits(Fault | Oos)]));
        Assert.Equal((BacnetStatusFlags)0, PointHealth.FlagsFrom([Bits(0)]));
        Assert.Null(PointHealth.FlagsFrom([Enumerated(1)])); // not a bit string
        Assert.Null(PointHealth.FlagsFrom(null));
        Assert.Equal(OpenLoop, PointHealth.ReliabilityFrom([Enumerated(OpenLoop)]));
        Assert.Null(PointHealth.ReliabilityFrom([new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 1f)]));
    }

    [Theory]
    [InlineData(4u, "open loop")]
    [InlineData(5u, "shorted loop")]
    [InlineData(12u, "communication failure")]
    [InlineData(70u, "vendor-specific reliability 70")]
    public void Reliability_codes_have_plain_names(uint code, string expected) =>
        Assert.Equal(expected, PointHealth.ReliabilityName(code));

    // --- wording ---

    [Fact]
    public void A_healthy_point_has_no_problems()
    {
        Assert.Empty(PointHealth.Problems(0, null));
        Assert.Empty(PointHealth.Problems(null, null));
        Assert.Equal("", PointHealth.Summary(0, 0));
        Assert.Equal("normal", PointHealth.FlagsText(0));
    }

    [Fact]
    public void Problems_are_listed_worst_first_with_the_reason_for_a_fault()
    {
        Assert.Equal("Fault: open loop; In alarm; Out of service", PointHealth.Summary(Fault | Alarm | Oos, OpenLoop));
        Assert.Equal("Fault", PointHealth.Summary(Fault, null)); // reason not known (yet)
    }

    [Fact]
    public void A_reliability_problem_counts_as_a_fault_even_without_the_flag() =>
        Assert.True(PointHealth.IsFault(0, OpenLoop));

    [Fact]
    public void Every_problem_comes_with_a_likely_cause_and_a_next_step()
    {
        foreach (var code in Enum.GetValues<BacnetReliability>().Select(r => (uint)r).Where(c => c is > 0 and < 64).Append(70u))
            foreach (var p in PointHealth.Problems(Fault | Alarm | Oos, code))
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Cause), $"{code}: {p.Text}");
                Assert.False(string.IsNullOrWhiteSpace(p.NextStep), $"{code}: {p.Text}");
            }
        var tip = PointHealth.Explanation(Fault, OpenLoop);
        Assert.Contains("broken wire", tip);
        Assert.Contains("Next step: Check the wiring", tip);
    }

    [Fact]
    public void The_properties_panel_shows_flags_and_reliability_in_words()
    {
        Assert.Equal("fault, out of service",
            BacnetNames.FormatValue(BacnetObjectTypes.OBJECT_ANALOG_INPUT, BacnetPropertyIds.PROP_STATUS_FLAGS, Bits(Fault | Oos)));
        Assert.Equal("open loop",
            BacnetNames.FormatValue(BacnetObjectTypes.OBJECT_ANALOG_INPUT, BacnetPropertyIds.PROP_RELIABILITY, Enumerated(OpenLoop)));
        Assert.Equal("no fault detected",
            BacnetNames.FormatValue(BacnetObjectTypes.OBJECT_ANALOG_INPUT, BacnetPropertyIds.PROP_RELIABILITY, Enumerated(0)));
    }

    // --- live changes ---

    private static ObjectSummary Point(BacnetObjectTypes type = BacnetObjectTypes.OBJECT_ANALOG_INPUT, uint instance = 1, string name = "Zone Temp",
        BacnetStatusFlags? flags = 0, uint? reliability = null, bool overridden = false) => new()
    {
        Id = new BacnetObjectId(type, instance), Name = name, PresentValue = "70", StatusFlags = flags, Reliability = reliability,
        PrioritySlots = overridden ? [new PrioritySlot(8, "70")] : [],
    };

    [Fact]
    public void A_new_fault_is_a_change_and_clearing_it_forgets_the_reason()
    {
        var s = Point();
        Assert.False(s.ApplyLive("70", null, 0));
        Assert.True(s.ApplyLive("70", null, Fault));
        Assert.True(s.IsFault);
        s.Reliability = OpenLoop;
        Assert.Equal("Fault: open loop", s.ProblemText);
        Assert.True(s.ApplyLive("70", null, 0));
        Assert.False(s.HasProblem);
        Assert.Null(s.Reliability);
    }

    [Fact]
    public void A_notification_without_flags_leaves_them_alone()
    {
        var s = Point(flags: Oos);
        Assert.False(s.ApplyLive("70", null, null));
        Assert.True(s.IsOutOfService);
    }

    [Fact]
    public void Cov_notifications_carry_status_flags()
    {
        var values = new[]
        {
            new BacnetPropertyValue { property = new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_PRESENT_VALUE, ASN1.BACNET_ARRAY_ALL),
                value = [new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 70f)] },
            new BacnetPropertyValue { property = new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_STATUS_FLAGS, ASN1.BACNET_ARRAY_ALL),
                value = [Bits(Alarm)] },
        };
        Assert.Equal(Alarm, CovNotification.StatusFlags(values));
        Assert.Null(CovNotification.StatusFlags(values.Take(1)));
    }

    // --- finding them ---

    private static ExportDevice Site()
    {
        var d = new DiscoveredDevice
        {
            InstanceId = 1001, Address = new BacnetAddress(BacnetAddressTypes.IP, "10.0.0.1:47808", 0), MaxApdu = 480,
            Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 999, ObjectName = "AHU-1",
        };
        return new ExportDevice(d, "AHU-1",
        [
            Point(instance: 1, name: "Zone Temp", flags: Alarm),
            Point(instance: 2, name: "Discharge Temp", flags: Fault, reliability: OpenLoop),
            Point(BacnetObjectTypes.OBJECT_BINARY_VALUE, 1, "Occupied", flags: Oos),
            Point(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1, "Damper", overridden: true),
            Point(instance: 3, name: "Outside Temp"),
        ]);
    }

    private static string[] Names(string query) => [.. PointSearch.Search([Site()], query).Select(h => h.Point.Name!).Order()];

    [Theory]
    [InlineData("is:fault", new[] { "Discharge Temp" })]
    [InlineData("is:faults", new[] { "Discharge Temp" })]
    [InlineData("is:alarm", new[] { "Zone Temp" })]
    [InlineData("is:oos", new[] { "Occupied" })]
    [InlineData("is:out-of-service", new[] { "Occupied" })]
    [InlineData("is:problem", new[] { "Discharge Temp", "Occupied", "Zone Temp" })]
    [InlineData("is:overridden", new[] { "Damper" })]
    [InlineData("temp is:problem", new[] { "Discharge Temp", "Zone Temp" })]
    [InlineData("is:problem is:fault", new[] { "Discharge Temp" })]
    public void Find_filters_narrow_to_the_points_with_that_problem(string query, string[] expected) =>
        Assert.Equal(expected, Names(query));

    [Fact]
    public void An_unknown_filter_is_treated_as_a_word() => Assert.Empty(Names("is:broken"));

    [Fact]
    public void Exports_carry_the_status()
    {
        var lines = PointExporter.CsvLines([Site()]).ToList();
        Assert.EndsWith(",Status", lines[0]);
        Assert.Contains(lines, l => l.Contains("Discharge Temp") && l.EndsWith("Fault: open loop"));
    }

    // --- the simulator's troubles, for testing without hardware ---

    [Fact]
    public void Simulated_problems_report_the_right_flags()
    {
        var m = SimulatedDeviceModel.CreateSample(1001);
        m.AddSampleProblems();
        Assert.Equal(Fault, m.StatusFlags(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 2)));
        Assert.Equal(Alarm, m.StatusFlags(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1)));
        Assert.Equal(Oos, m.StatusFlags(new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_VALUE, 1)));
        Assert.Equal((BacnetStatusFlags)0, m.StatusFlags(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1)));

        Assert.True(m.TryRead(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 2), BacnetPropertyIds.PROP_STATUS_FLAGS, uint.MaxValue,
            out var flags, out _));
        Assert.Equal(Fault, PointHealth.FlagsFrom(flags));
        Assert.True(m.TryRead(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 2), BacnetPropertyIds.PROP_RELIABILITY, uint.MaxValue,
            out var rel, out _));
        Assert.Equal(OpenLoop, PointHealth.ReliabilityFrom(rel));
    }

    [Fact]
    public void A_broken_sensor_stays_stuck_while_the_others_drift()
    {
        var m = SimulatedDeviceModel.CreateSample(1001);
        m.AddSampleProblems();
        var ai2 = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 2);
        for (var i = 0; i < 20; i++) m.Tick(new Random(i));
        Assert.True(m.TryRead(ai2, BacnetPropertyIds.PROP_PRESENT_VALUE, uint.MaxValue, out var pv, out _));
        Assert.Equal(-40f, pv[0].Value);
    }
}
