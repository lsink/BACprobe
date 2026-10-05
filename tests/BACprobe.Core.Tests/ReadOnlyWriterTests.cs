using System.IO.BACnet;
using BACprobe.Core.Discovery;
using BACprobe.Core.Writing;

namespace BACprobe.Core.Tests;

/// <summary>Read-only mode is enforced by the writer itself, so no caller can write around a hidden button.</summary>
public class ReadOnlyWriterTests
{
    private static readonly BacnetObjectId Ao1 = new(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1);

    private static DiscoveredDevice Device() => new()
    {
        InstanceId = 1001, Address = new BacnetAddress(BacnetAddressTypes.MSTP, 0, [1]), MaxApdu = 480,
        Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 0,
    };

    private static (DeviceWriter Writer, WriteLog Log, OverrideTracker Tracker, IDisposable Cleanup) Make()
    {
        var (wire, _) = LoopbackSerial.Pair();
        var transport = new BacnetMstpProtocolTransport(wire, 5, 127, 1); // never started: a blocked write must not need the network
        var client = new BacnetClient(transport, 100, 0);
        var log = new WriteLog();
        var tracker = new OverrideTracker();
        return (new DeviceWriter(client, log, tracker) { ReadOnly = true }, log, tracker, client);
    }

    [Fact]
    public async Task A_write_is_refused_with_cause_and_next_step_and_leaves_a_log_line()
    {
        var (writer, log, tracker, cleanup) = Make();
        using var _ = cleanup;
        var request = new WriteRequest(Device(), "AHU-1", Ao1, "Damper", new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 25f), "25", 8);

        var outcome = await writer.ExecuteAsync(request);

        Assert.False(outcome.Success);
        Assert.Contains("Read-only", outcome.Message);
        Assert.Contains("Likely cause", outcome.Message);
        Assert.Contains("Next step", outcome.Message);
        Assert.Empty(tracker.Active); // nothing was left in place
        var entry = Assert.Single(log.Entries);
        Assert.False(entry.Success);
        Assert.Contains("read-only", entry.Result);
    }

    [Fact]
    public async Task Taking_a_point_out_of_service_is_refused_too()
    {
        var (writer, log, tracker, cleanup) = Make();
        using var _ = cleanup;
        var outcome = await writer.SetOutOfServiceAsync(new OutOfServiceRequest(Device(), "AHU-1", Ao1, "Damper", TurnOn: true));

        Assert.False(outcome.Success);
        Assert.Empty(tracker.Active);
        Assert.Contains("read-only", Assert.Single(log.Entries).Result);
    }

    [Fact]
    public async Task Acknowledging_an_alarm_is_refused_too()
    {
        var (writer, log, _, cleanup) = Make();
        using var _ = cleanup;
        var e = new Alarms.ActiveEvent
        {
            Device = Device(), Point = Ao1, State = BacnetEventStates.EVENT_STATE_HIGH_LIMIT, Acked = [false, true, true],
            TimeStamps = [new BacnetGenericTime(new DateTime(2026, 10, 5, 14, 2, 11), BacnetTimestampTags.TIME_STAMP_DATETIME),
                Alarms.EventText.Never, Alarms.EventText.Never],
        };

        var outcome = await writer.AcknowledgeAsync(new Alarms.AlarmAckRequest(e, "AHU-1", "BACprobe (test)"));

        Assert.False(outcome.Success);
        Assert.Contains("Next step", outcome.Message);
        var entry = Assert.Single(log.Entries);
        Assert.Contains("acknowledge", entry.Action);
        Assert.Contains("read-only", entry.Result);
    }
}
