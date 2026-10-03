using System.IO.BACnet;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Tests;

public class DeviceHealthTests
{
    private static DiscoveredDevice Dev(uint id, string? name = null, uint apdu = 1476,
        BacnetSegmentations seg = BacnetSegmentations.SEGMENTATION_BOTH, double? replyMs = null, TimeSpan? skew = null) => new()
    {
        InstanceId = id, Address = new BacnetAddress(BacnetAddressTypes.IP, $"10.0.0.{id % 250}:47808"), MaxApdu = apdu,
        Segmentation = seg, VendorId = 0, ObjectName = name,
        ResponseTime = replyMs is { } m ? TimeSpan.FromMilliseconds(m) : null, ClockSkew = skew,
    };

    [Fact]
    public void Healthy_devices_have_no_findings()
        => Assert.Empty(DeviceHealth.Check([Dev(1, "AHU-1", replyMs: 40, skew: TimeSpan.FromSeconds(20)), Dev(2, "AHU-2", replyMs: 80)]));

    [Fact]
    public void A_slow_answer_is_flagged_with_cause_and_next_step()
    {
        var f = Assert.Single(DeviceHealth.Check([Dev(7, "VAV-7", replyMs: 2400)]));
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Contains("2.4 s", f.Detail);
        Assert.NotEmpty(f.NextStep);
    }

    [Fact]
    public void Devices_that_cannot_segment_are_listed_together_as_info()
    {
        var f = Assert.Single(DeviceHealth.Check([
            Dev(1, "A", 480, BacnetSegmentations.SEGMENTATION_NONE), Dev(2, "B", 480, BacnetSegmentations.SEGMENTATION_NONE), Dev(3, "C")]));
        Assert.Equal(FindingSeverity.Info, f.Severity);
        Assert.Contains("2 devices", f.Title);
        Assert.Contains("1, 2", f.Detail);
    }

    [Fact]
    public void A_tiny_max_apdu_is_a_warning()
        => Assert.Contains(DeviceHealth.Check([Dev(1, "A", 128, BacnetSegmentations.SEGMENTATION_BOTH)]), f => f.Title.Contains("tiny"));

    [Theory]
    [InlineData(4, false)]
    [InlineData(6, true)]
    [InlineData(-47, true)]
    public void Clock_skew_over_five_minutes_is_flagged(int minutes, bool flagged)
    {
        var findings = DeviceHealth.Check([Dev(1, "A", skew: TimeSpan.FromMinutes(minutes))]);
        Assert.Equal(flagged, findings.Any(f => f.Title.Contains("clock")));
    }

    [Fact]
    public void An_hours_off_clock_blames_time_zone_or_battery()
    {
        var f = Assert.Single(DeviceHealth.Check([Dev(1, "A", skew: TimeSpan.FromHours(-5))]));
        Assert.Contains("behind this PC", f.Detail);
        Assert.Contains("time zone", f.LikelyCause);
    }

    [Theory]
    [InlineData(47, "+47 min")]
    [InlineData(-310, "-5 h 10 min")]
    [InlineData(3000, "+2 d")]
    public void Short_skew_fits_a_table_cell(int minutes, string expected)
        => Assert.Equal(expected, DeviceHealth.ShortSkew(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Two_devices_with_one_name_are_flagged()
        => Assert.Contains(DeviceHealth.Check([Dev(1, "AHU-1"), Dev(2, "ahu-1")]), f => f.Title.Contains("both named"));

    [Fact]
    public void The_same_device_heard_twice_is_not_a_duplicate_name()
        => Assert.DoesNotContain(DeviceHealth.Check([Dev(1, "AHU-1"), Dev(1, "AHU-1")]), f => f.Title.Contains("both named"));

    [Fact]
    public void Clock_needs_both_date_and_time_and_a_real_year()
    {
        Assert.Null(DeviceHealth.CombineClock(new DateTime(2026, 10, 2), null));
        Assert.Null(DeviceHealth.CombineClock(new DateTime(1, 1, 1), new DateTime(1, 1, 1, 10, 0, 0)));
        Assert.Equal(new DateTime(2026, 10, 2, 13, 5, 0),
            DeviceHealth.CombineClock(new DateTime(2026, 10, 2, 9, 9, 9), new DateTime(1, 1, 1, 13, 5, 0)));
    }
}
