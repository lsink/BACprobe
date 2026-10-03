using System.IO.BACnet;
using BACprobe.Core.Discovery;
using BACprobe.Core.Simulation;
using BACprobe.Core.Writing;

namespace BACprobe.Core.Tests;

public class OutOfServiceTests
{
    private static readonly BacnetObjectId Ai1 = new(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1);

    private static DiscoveredDevice Device() => new()
    {
        InstanceId = 1001, Address = new BacnetAddress(BacnetAddressTypes.IP, "10.0.0.5:47808"), MaxApdu = 1476,
        Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 0,
    };

    private static BacnetValue Bool(bool b) => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, b);

    [Fact]
    public void Simulator_accepts_out_of_service_and_reports_it_in_status_flags()
    {
        var m = SimulatedDeviceModel.CreateSample(1001);
        Assert.Null(m.Write(Ai1, BacnetPropertyIds.PROP_OUT_OF_SERVICE, Bool(true), 0, out _));
        Assert.True(m.StatusFlags(Ai1).HasFlag(BacnetStatusFlags.STATUS_FLAG_OUT_OF_SERVICE));
        Assert.Null(m.Write(Ai1, BacnetPropertyIds.PROP_OUT_OF_SERVICE, Bool(false), 0, out _));
        Assert.False(m.StatusFlags(Ai1).HasFlag(BacnetStatusFlags.STATUS_FLAG_OUT_OF_SERVICE));
    }

    [Fact]
    public void Simulator_refuses_a_wrong_type_for_out_of_service()
    {
        var m = SimulatedDeviceModel.CreateSample(1001);
        var err = m.Write(Ai1, BacnetPropertyIds.PROP_OUT_OF_SERVICE,
            new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 1f), 0, out _);
        Assert.NotNull(err);
    }

    [Fact]
    public void Confirmation_names_the_point_and_the_consequence()
    {
        var r = new OutOfServiceRequest(Device(), "AHU-1", Ai1, "Supply Temp", TurnOn: true, "55.2");
        var p = Prompts.ForOutOfService(r);
        Assert.Contains("Supply Temp", p.Headline);
        Assert.Equal("Take out of service", r.ConfirmLabel);
        Assert.Contains(p.Facts, f => f.Label == "Now" && f.Value == "55.2");
        Assert.Contains("frozen", p.Body);
    }

    [Fact]
    public void Putting_back_in_service_has_its_own_wording()
    {
        var r = new OutOfServiceRequest(Device(), "AHU-1", Ai1, "Supply Temp", TurnOn: false);
        Assert.Equal("Put back in service", r.ConfirmLabel);
        Assert.Contains("back in service", r.Headline);
    }

    [Fact]
    public void Held_point_reads_as_out_of_service_not_as_a_priority()
    {
        var held = new TrackedOverride(Device(), "AHU-1", Ai1, "Supply Temp", TrackedOverride.OutOfServicePriority, "out of service");
        Assert.True(held.IsOutOfService);
        Assert.Contains("out of service", held.Description);
        Assert.DoesNotContain("priority", held.Description);

        var normal = held with { Priority = 8, ValueText = "25" };
        Assert.Contains("25 at priority 8 (Manual Operator)", normal.Description);
    }

    [Fact]
    public void Log_line_leaves_out_the_priority_for_out_of_service()
    {
        var e = new WriteLogEntry(DateTimeOffset.Now, 1001, "AHU-1", "Supply Temp (Analog Input 1)", "out of service: on", 0, true, "ok");
        Assert.DoesNotContain("@", e.Text);
        var w = e with { Action = "write 5", Priority = 8 };
        Assert.Contains("@ 8 (Manual Operator)", w.Text);
    }
}
