using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using System.Net;
using BACprobe.Core.Alarms;
using BACprobe.Core.Discovery;
using BACprobe.Core.Simulation;
using BACprobe.Core.Writing;

namespace BACprobe.Core.Tests;

public class AlarmListenTests
{
    private static readonly BacnetObjectId Nc1 = new(BacnetObjectTypes.OBJECT_NOTIFICATION_CLASS, 1);
    private static readonly BacnetObjectId Ai1 = new(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1);
    private static readonly AlarmRecipient Me = AlarmRecipient.ForEndPoint(new IPEndPoint(IPAddress.Parse("192.168.1.50"), 55368));

    [Fact]
    public void BACprobe_is_named_by_its_ip_and_port()
    {
        Assert.Equal([192, 168, 1, 50, 0xD8, 0x48], Me.Mac);
        Assert.Equal("192.168.1.50:55368", Me.AddressText);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.50"), 55368), Me.EndPoint);
        Assert.False(Me.Confirmed);
        Assert.Equal(AlarmRecipient.BacprobeProcessId, Me.ProcessId);
    }

    [Fact]
    public void A_recipient_list_round_trips_including_a_device_recipient()
    {
        var other = new AlarmRecipient(0, [], 7, Confirmed: true, DeviceInstance: 9000);
        var bytes = Me.Encode().Concat(other.Encode()).ToArray();
        var list = AlarmRecipient.DecodeList(bytes, 0, bytes.Length);
        Assert.Equal(2, list.Count);
        Assert.True(list[0].SameAs(Me));
        Assert.False(list[0].Confirmed);
        Assert.Equal(9000u, list[1].DeviceInstance);
        Assert.Equal(7u, list[1].ProcessId);
        Assert.True(list[1].Confirmed);
        Assert.Equal("device 9000", list[1].AddressText);
    }

    [Fact]
    public void Decoding_stops_at_the_closing_tag_and_at_junk()
    {
        var b = new EncodeBuffer();
        ASN1.encode_opening_tag(b, 3);
        var inner = Me.Encode();
        b.Add(inner, inner.Length);
        ASN1.encode_closing_tag(b, 3);
        Assert.Single(AlarmRecipient.DecodeList(b.buffer, 1, b.offset)); // starts after the opening tag, as AddListElement has it
        Assert.Empty(AlarmRecipient.DecodeList([0xFF, 0xFF], 0, 2));
    }

    [Fact]
    public void The_simulators_notifications_decode_on_the_client()
    {
        foreach (var sample in new[]
        {
            new BacnetEventNotificationData
            {
                eventType = BacnetEventTypes.EVENT_OUT_OF_RANGE, outOfRange_exceedingValue = 70.5f, outOfRange_exceededLimit = 63,
                outOfRange_deadband = 1, outOfRange_statusFlags = BacnetBitString.ConvertFromInt(1, 4),
                fromState = BacnetEventStates.EVENT_STATE_NORMAL, toState = BacnetEventStates.EVENT_STATE_HIGH_LIMIT,
            },
            new BacnetEventNotificationData
            {
                eventType = BacnetEventTypes.EVENT_CHANGE_OF_STATE, changeOfState_statusFlags = BacnetBitString.ConvertFromInt(2, 4),
                changeOfState_newState = new BacnetPropertyState
                {
                    tag = BacnetPropertyState.BacnetPropertyStateTypes.STATE,
                    state = new BacnetPropertyState.State { state = BacnetEventStates.EVENT_STATE_FAULT },
                },
                fromState = BacnetEventStates.EVENT_STATE_NORMAL, toState = BacnetEventStates.EVENT_STATE_FAULT,
            },
        })
        {
            var data = sample; // a struct: fill in a copy
            data.processIdentifier = Me.ProcessId;
            data.initiatingObjectIdentifier = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, 1001);
            data.eventObjectIdentifier = Ai1;
            data.timeStamp = new BacnetGenericTime(new DateTime(2026, 10, 5, 14, 2, 0), BacnetTimestampTags.TIME_STAMP_DATETIME);
            data.notificationClass = 1;
            data.priority = 100;
            data.notifyType = BacnetNotifyTypes.NOTIFY_ALARM;
            data.ackRequired = true;
            var b = new EncodeBuffer();
            Services.EncodeEventNotifyUnconfirmed(b, data);
            Assert.True(Services.DecodeEventNotifyData(b.buffer, 0, b.offset, out var back) > 0, data.eventType.ToString());
            Assert.Equal(Ai1, back.eventObjectIdentifier);
            Assert.Equal(data.toState, back.toState);
            Assert.Equal(1001u, back.initiatingObjectIdentifier.instance);
        }
    }

    [Fact]
    public void A_notification_the_library_cannot_read_still_says_whose_alarms_changed()
    {
        var b = new EncodeBuffer();
        ASN1.encode_context_unsigned(b, 0, Me.ProcessId);
        ASN1.encode_context_object_id(b, 1, BacnetObjectTypes.OBJECT_DEVICE, 1001);
        ASN1.encode_context_object_id(b, 2, BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1);
        b.Add([0xFF, 0xFF, 0xFF], 3); // the rest in a form nobody can read
        var n = AlarmNotification.FromHeader(b.buffer, 0, b.offset, new DateTime(2026, 10, 5, 14, 2, 0));
        Assert.NotNull(n);
        Assert.Equal(1001u, n.DeviceInstance);
        Assert.Equal(Ai1, n.Point);
        Assert.Null(n.To);
        Assert.Equal("14:02:00  device 1001 Analog Input 1: alarm changed", n.Text);
        Assert.Null(AlarmNotification.FromHeader([0x01, 0x02], 0, 2, DateTime.Now));
    }

    [Fact]
    public void The_simulator_keeps_one_entry_per_recipient_and_queues_alarm_changes()
    {
        var m = SimulatedDeviceModel.CreateSample(1001);
        m.AddSampleProblems();
        m.DrainTransitions(); // the start-up alarms, before anyone listened

        Assert.Null(m.AddRecipient(Nc1, Me));
        Assert.Null(m.AddRecipient(Nc1, Me)); // the same entry twice is one entry
        Assert.Single(m.RecipientsOf(1));
        Assert.True(m.TryRead(Nc1, BacnetPropertyIds.PROP_RECIPIENT_LIST, uint.MaxValue, out var raw, out _));
        Assert.Single(raw);

        m.Write(Ai1, BacnetPropertyIds.PROP_HIGH_LIMIT, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 95f), 16, out _);
        var t = Assert.Single(m.DrainTransitions());
        Assert.Equal(BacnetEventStates.EVENT_STATE_HIGH_LIMIT, t.From);
        Assert.Equal(BacnetEventStates.EVENT_STATE_NORMAL, t.To);
        Assert.Equal(1u, t.NotificationClass);
        Assert.Empty(m.DrainTransitions());

        Assert.Null(m.RemoveRecipient(Nc1, Me));
        Assert.Equal(BacnetErrorCodes.ERROR_CODE_LIST_ELEMENT_NOT_FOUND, m.RemoveRecipient(Nc1, Me)?.Code);
        Assert.Empty(m.RecipientsOf(1));
    }

    [Fact]
    public void The_confirmation_names_the_classes_and_how_to_undo_it()
    {
        var device = new DiscoveredDevice
        {
            InstanceId = 1001, Address = new BacnetAddress(BacnetAddressTypes.IP, "10.0.0.5:47808"), MaxApdu = 1476,
            Segmentation = BacnetSegmentations.SEGMENTATION_BOTH, VendorId = 5,
        };
        var r = new AlarmListenRequest(device, "VAV-1", [Nc1], Me);
        Assert.Equal("Get VAV-1's alarms live?", r.Headline);
        Assert.Contains(r.Facts, f => f.Value == "Notification Class 1");
        Assert.Contains("takes itself off again", r.Consequence);
        Assert.Contains("192.168.1.50:55368", r.Warning);
        Assert.Null((r with { Stop = true }).Warning);
        Assert.Equal("Get alarms live from 2 devices?", Prompts.ForAlarmListen([r, r with { DeviceName = "VAV-2" }]).Headline);

        var held = new TrackedOverride(device, "VAV-1", Nc1, "Notification Class 1", TrackedOverride.AlarmRecipientPriority, Me.AddressText);
        Assert.True(held.IsAlarmRecipient);
        Assert.Contains("alarm recipient list", held.HeldAs);
    }
}
