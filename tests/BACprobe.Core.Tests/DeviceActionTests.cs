using System.IO.BACnet;
using BACprobe.Core.Discovery;
using BACprobe.Core.Writing;

namespace BACprobe.Core.Tests;

public class DeviceActionTests
{
    private static DiscoveredDevice Device() => new()
    {
        InstanceId = 1001, Address = new BacnetAddress(BacnetAddressTypes.IP, "10.0.0.5:47808"), MaxApdu = 1476,
        Segmentation = BacnetSegmentations.SEGMENTATION_BOTH, VendorId = 5, ClockSkew = TimeSpan.FromMinutes(47),
    };

    private static DeviceActionRequest Req(DeviceActionKind kind, uint minutes = 10) => new(Device(), "VAV-1", kind, minutes);

    [Fact]
    public void Restarts_and_mutes_need_the_device_number_typed_and_the_clock_does_not()
    {
        Assert.Equal("1001", Req(DeviceActionKind.WarmStart).TypeToConfirm);
        Assert.Equal("1001", Req(DeviceActionKind.ColdStart).TypeToConfirm);
        Assert.Equal("1001", Req(DeviceActionKind.Mute).TypeToConfirm);
        Assert.Null(Req(DeviceActionKind.SyncTime).TypeToConfirm);
        Assert.Null(Req(DeviceActionKind.Unmute).TypeToConfirm); // giving control back is never made harder
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(60u, true)]
    [InlineData(61u, false)]
    public void A_mute_always_has_a_time_limit(uint minutes, bool ok)
    {
        Assert.Equal(ok, Req(DeviceActionKind.Mute, minutes).Problem is null);
        Assert.Null(Req(DeviceActionKind.Unmute, 0).Problem); // un-muting takes no time
    }

    [Fact]
    public void The_wording_says_what_happens_and_warns_about_cold_starts()
    {
        var cold = Req(DeviceActionKind.ColdStart);
        Assert.Equal("Cold start VAV-1?", cold.Headline);
        Assert.Contains("switching it off and on", cold.Consequence);
        Assert.NotNull(cold.Warning);
        Assert.True(cold.MayNeedPassword);

        var clock = Req(DeviceActionKind.SyncTime);
        Assert.Contains(clock.Facts, f => f.Label == "Device clock now" && f.Value.Contains("47"));
        Assert.False(clock.MayNeedPassword);

        var mute = Req(DeviceActionKind.Mute, 15);
        Assert.Equal("Mute VAV-1 for 15 minutes?", mute.Headline);
        Assert.Equal("communication control: disable for 15 min", mute.LogAction);
        Assert.Contains("un-mute", mute.Consequence);
    }

    [Fact]
    public void A_muted_device_is_listed_with_the_overrides_left_in_place()
    {
        var held = new TrackedOverride(Device(), "VAV-1", new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, 1001), "VAV-1",
            TrackedOverride.MutedPriority, "for 10 min");
        Assert.True(held.IsMuted);
        Assert.False(held.IsOutOfService);
        Assert.Equal("muted (for 10 min)", held.HeldAs);
    }

    [Fact]
    public void Device_errors_get_a_cause_and_next_step()
    {
        var pw = DeviceActionErrors.Explain(new InvalidOperationException("Error from device: ERROR_CLASS_SECURITY - ERROR_CODE_PASSWORD_FAILURE"));
        Assert.Contains("password", pw.Summary);
        Assert.Contains("password", pw.NextStep);
        var timeout = DeviceActionErrors.Explain(new TimeoutException("Failed"));
        Assert.Contains("Scan", timeout.NextStep);
    }
}
