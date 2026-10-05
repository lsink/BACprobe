using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using BACprobe.Core.Alarms;
using BACprobe.Core.Discovery;
using BACprobe.Core.Simulation;

namespace BACprobe.Core.Tests;

public class AlarmTests
{
    private static readonly BacnetObjectId Ai1 = new(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1);
    private static readonly BacnetObjectId Ai2 = new(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 2);
    private static readonly BacnetObjectId Av1 = new(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1);
    private static readonly BacnetObjectId Msi1 = new(BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT, 1);

    private static DiscoveredDevice Device(uint instance = 1001, string? name = "AHU-1") => new()
    {
        InstanceId = instance, Address = new BacnetAddress(BacnetAddressTypes.IP, "10.0.0.5:47808"), MaxApdu = 1476,
        Segmentation = BacnetSegmentations.SEGMENTATION_BOTH, VendorId = 5, ObjectName = name,
    };

    private static BacnetGenericTime At(int hour, int minute) =>
        new(new DateTime(2026, 10, 5, hour, minute, 0), BacnetTimestampTags.TIME_STAMP_DATETIME);

    private static ActiveEvent Event(BacnetEventStates state, bool[]? acked = null, BacnetGenericTime[]? stamps = null,
        BacnetObjectId? point = null, DiscoveredDevice? device = null) => new()
    {
        Device = device ?? Device(),
        Point = point ?? Ai1,
        State = state,
        Acked = acked ?? [true, true, true],
        TimeStamps = stamps ?? [At(14, 2), EventText.Never, EventText.Never],
    };

    // --- reading what the device sent ---

    [Fact]
    public void Acked_transitions_map_bit_by_bit_and_a_short_bit_string_counts_as_acknowledged()
    {
        Assert.Equal([true, false, true], EventText.AckedFrom(BacnetBitString.ConvertFromInt(0b101, 3)));
        Assert.Equal([false, true, true], EventText.AckedFrom(BacnetBitString.ConvertFromInt(0b10, 2)));
    }

    [Fact]
    public void A_transition_that_never_happened_is_recognised_however_the_device_says_it()
    {
        Assert.True(EventText.IsNever(EventText.Never));
        Assert.True(EventText.IsNever(new BacnetGenericTime(default, BacnetTimestampTags.TIME_STAMP_SEQUENCE, 0)));
        Assert.True(EventText.IsNever(default)); // a Null the library left empty
        Assert.False(EventText.IsNever(At(14, 2)));
        Assert.False(EventText.IsNever(new BacnetGenericTime(default, BacnetTimestampTags.TIME_STAMP_SEQUENCE, 12)));
    }

    [Fact]
    public void Time_stamps_read_the_way_the_device_kept_them()
    {
        Assert.Equal("2026-10-05 14:02:00", EventText.FormatStamp(At(14, 2)));
        Assert.Equal("event #12", EventText.FormatStamp(new BacnetGenericTime(default, BacnetTimestampTags.TIME_STAMP_SEQUENCE, 12)));
        Assert.Equal("09:30:00", EventText.FormatStamp(new BacnetGenericTime(new DateTime(1, 1, 1, 9, 30, 0), BacnetTimestampTags.TIME_STAMP_TIME)));
        Assert.Equal("", EventText.FormatStamp(EventText.Never));
    }

    [Fact]
    public void Never_happened_stamps_survive_the_wire_both_ways()
    {
        // The encoder writes nothing at all for an unspecified date unless the time is carried as a partial time:
        // check our "never" goes out as FF FF FF FF / FF FF FF FF and comes back as never, with the "more events" flag.
        var data = new BacnetGetEventInformationData
        {
            objectIdentifier = Ai1,
            eventState = BacnetEventStates.EVENT_STATE_HIGH_LIMIT,
            acknowledgedTransitions = BacnetBitString.ConvertFromInt(0b110, 3),
            eventTimeStamps = [At(14, 2), EventText.Never, EventText.Never],
            notifyType = BacnetNotifyTypes.NOTIFY_ALARM,
            eventEnable = BacnetBitString.ConvertFromInt(7, 3),
            eventPriorities = [100, 50, 200],
        };
        var buffer = new EncodeBuffer();
        Services.EncodeGetEventInformationAcknowledge(buffer, [data, data with { objectIdentifier = Ai2 }], moreEvents: true);

        IList<BacnetGetEventInformationData> back = [];
        Assert.True(Services.DecodeAlarmSummaryOrEvent(buffer.buffer, 0, buffer.offset, true, ref back, out var more) > 0);
        Assert.True(more);
        Assert.Equal(2, back.Count);
        Assert.Equal(BacnetEventStates.EVENT_STATE_HIGH_LIMIT, back[0].eventState);
        Assert.Equal("2026-10-05 14:02:00", EventText.FormatStamp(back[0].eventTimeStamps[0]));
        Assert.True(EventText.IsNever(back[0].eventTimeStamps[1]));
        Assert.True(EventText.IsNever(back[0].eventTimeStamps[2]));
        Assert.Equal([false, true, true], EventText.AckedFrom(back[0].acknowledgedTransitions));
        Assert.Equal(Ai2, back[1].objectIdentifier);
    }

    // --- ordering and wording ---

    [Fact]
    public void Worst_first_then_unacknowledged_then_newest()
    {
        var normal = Event(BacnetEventStates.EVENT_STATE_NORMAL, [false, true, false], point: Msi1);
        var oldAlarm = Event(BacnetEventStates.EVENT_STATE_HIGH_LIMIT, [true, true, true], [At(9, 0), EventText.Never, EventText.Never], Av1);
        var newAlarm = Event(BacnetEventStates.EVENT_STATE_HIGH_LIMIT, [true, true, true], [At(15, 0), EventText.Never, EventText.Never], Ai1);
        var unackedAlarm = Event(BacnetEventStates.EVENT_STATE_LOW_LIMIT, [false, true, true], [At(8, 0), EventText.Never, EventText.Never], Ai1,
            Device(1002));
        var fault = Event(BacnetEventStates.EVENT_STATE_FAULT, [true, false, true], [EventText.Never, At(7, 0), EventText.Never], Ai2);
        var lifeSafety = Event(BacnetEventStates.EVENT_STATE_LIFE_SAFETY_ALARM, point: new BacnetObjectId(BacnetObjectTypes.OBJECT_LIFE_SAFETY_POINT, 1));

        var sorted = EventText.Sort([normal, oldAlarm, newAlarm, unackedAlarm, fault, lifeSafety]);

        Assert.Equal([lifeSafety, fault, unackedAlarm, newAlarm, oldAlarm, normal], sorted);
    }

    [Fact]
    public void A_cleared_alarm_waiting_for_acknowledgement_names_both_transitions()
    {
        var e = Event(BacnetEventStates.EVENT_STATE_NORMAL, [false, true, false], [At(12, 0), EventText.Never, At(12, 20)], Msi1);
        Assert.True(e.NeedsAck);
        Assert.Equal("to alarm, to normal", e.UnackedText);
        Assert.Equal("2026-10-05 12:20:00", e.SinceText);
        Assert.Contains("Back to normal, not yet acknowledged", EventText.Explanation(e));
        Assert.Contains("Not acknowledged: to alarm, to normal.", EventText.Explanation(e));
    }

    [Fact]
    public void A_limit_alarm_shows_the_value_against_its_limit_and_where_it_clears()
    {
        var e = Event(BacnetEventStates.EVENT_STATE_HIGH_LIMIT);
        e.ValueText = "85.2 °F";
        e.Units = "°F";
        e.HighLimit = 80;
        e.LowLimit = 55;
        e.Deadband = 2;
        Assert.Equal("Reads 85.2 °F; high limit 80 °F, clears below 78 °F (deadband 2).", EventText.LimitDetail(e));
        Assert.StartsWith("Above its high alarm limit. Reads 85.2", EventText.Explain(e).Text);

        var low = Event(BacnetEventStates.EVENT_STATE_LOW_LIMIT);
        low.LowLimit = 40.5f;
        Assert.Equal("Low limit 40.5.", EventText.LimitDetail(low)); // nothing else known: still the limit
    }

    [Fact]
    public void A_fault_alarm_uses_the_same_words_as_the_status_column()
    {
        var e = Event(BacnetEventStates.EVENT_STATE_FAULT, [true, false, true]);
        e.Reliability = (uint)BacnetReliability.RELIABILITY_OPEN_LOOP;
        var p = EventText.Explain(e);
        Assert.Equal("Fault: open loop", p.Text);
        Assert.Contains("broken wire", p.Cause);
        Assert.Equal("Fault", EventText.Explain(Event(BacnetEventStates.EVENT_STATE_FAULT)).Text); // no reason given
    }

    // --- acknowledging ---

    [Fact]
    public void An_alarm_still_active_is_acknowledged_with_its_own_state_and_time_stamp()
    {
        var stamp = At(14, 2);
        var e = Event(BacnetEventStates.EVENT_STATE_HIGH_LIMIT, [false, true, true], [stamp, EventText.Never, EventText.Never]);
        var t = Assert.Single(EventText.AckTargets(e));
        Assert.Equal(EventTransition.ToOffnormal, t.Transition);
        Assert.Equal(BacnetEventStates.EVENT_STATE_HIGH_LIMIT, t.StateAcked);
        Assert.Equal(stamp, t.Stamp);
        Assert.Equal("to alarm (high limit), 2026-10-05 14:02:00", t.Text);
    }

    [Fact]
    public void A_cleared_alarm_acknowledges_both_transitions_with_states_that_fit_them()
    {
        var e = Event(BacnetEventStates.EVENT_STATE_NORMAL, [false, true, false], [At(12, 0), EventText.Never, At(12, 20)], Msi1);
        var targets = EventText.AckTargets(e);
        Assert.Equal([EventTransition.ToOffnormal, EventTransition.ToNormal], targets.Select(t => t.Transition));
        Assert.Equal([BacnetEventStates.EVENT_STATE_OFFNORMAL, BacnetEventStates.EVENT_STATE_NORMAL], targets.Select(t => t.StateAcked));
        Assert.Equal("to alarm, 2026-10-05 12:00:00; to normal, 2026-10-05 12:20:00",
            new AlarmAckRequest(e, "AHU-1", "BACprobe (test)").TargetText);
        Assert.Empty(EventText.AckTargets(Event(BacnetEventStates.EVENT_STATE_HIGH_LIMIT))); // all acknowledged: nothing to send
    }

    [Fact]
    public void The_confirmation_says_what_is_acknowledged_by_whom_and_that_nothing_is_fixed()
    {
        var e = Event(BacnetEventStates.EVENT_STATE_HIGH_LIMIT, [false, true, true]);
        e.Name = "Zone Temp";
        e.ValueText = "85.2 °F";
        var r = new AlarmAckRequest(e, "AHU-1", "BACprobe (larry)");
        Assert.Equal("Acknowledge the alarm on Zone Temp?", r.Headline);
        Assert.Contains(r.Facts, f => f.Label == "Acknowledged as" && f.Value == "BACprobe (larry)");
        Assert.Contains(r.Facts, f => f.Label == "State now" && f.Value == "High limit, reads 85.2 °F");
        Assert.Contains("does not fix anything", r.Consequence);
        Assert.Null(r.Warning);
        Assert.Equal("Acknowledge", r.ConfirmLabel);

        var life = new AlarmAckRequest(Event(BacnetEventStates.EVENT_STATE_LIFE_SAFETY_ALARM, [false, true, true]), "FACP", "BACprobe (larry)");
        Assert.NotNull(life.Warning);
        Assert.Equal("Acknowledge life-safety alarm", life.ConfirmLabel);
    }

    [Fact]
    public void Ack_errors_each_get_a_cause_and_next_step()
    {
        var stale = AckErrors.Explain(new InvalidOperationException("Error from device: ERROR_CLASS_SERVICES - ERROR_CODE_INVALID_TIME_STAMP"));
        Assert.Contains("happened again", stale.Cause);
        Assert.Contains("Refresh", stale.NextStep);
        var timeout = AckErrors.Explain(new TimeoutException("Failed to acknowledge"));
        Assert.Contains("may or may not", timeout.Cause);
        var denied = AckErrors.Explain(new InvalidOperationException("Error from device: ERROR_CLASS_SERVICES - ERROR_CODE_SERVICE_REQUEST_DENIED"));
        Assert.Contains("front-end", denied.NextStep);
    }

    // --- summary and findings ---

    [Fact]
    public void The_summary_counts_by_kind_and_says_how_many_wait_for_acknowledgement()
    {
        var d = Device();
        var results = new[]
        {
            new DeviceAlarms(d, [Event(BacnetEventStates.EVENT_STATE_FAULT, [true, false, true]), Event(BacnetEventStates.EVENT_STATE_HIGH_LIMIT),
                Event(BacnetEventStates.EVENT_STATE_NORMAL, [false, true, false])], AlarmSource.EventInformation),
            new DeviceAlarms(Device(1002), [], AlarmSource.EventInformation),
        };
        Assert.Equal("3 events on 1 device: 1 fault, 1 in alarm, 1 back to normal; 2 not acknowledged.", AlarmCheck.Summarise(results));
        Assert.Equal("No active or unacknowledged alarms on 1 device.", AlarmCheck.Summarise([results[1]]));
    }

    [Fact]
    public void Devices_that_could_not_be_asked_are_findings_with_cause_and_next_step()
    {
        var results = new[]
        {
            new DeviceAlarms(Device(1001), [], null, "The device did not answer.", TimedOut: true),
            new DeviceAlarms(Device(1002, "VAV-2"), [], AlarmSource.StatusFlags),
            new DeviceAlarms(Device(1003), [], AlarmSource.EventInformation),
        };
        var findings = AlarmCheck.Findings(results);
        Assert.Equal(2, findings.Count);
        Assert.Equal(FindingSeverity.Problem, findings[0].Severity);
        Assert.Contains("1001", findings[0].Title);
        Assert.Equal(FindingSeverity.Info, findings[1].Severity);
        Assert.Contains("\"VAV-2\"", findings[1].Title);
        Assert.All(findings, f => { Assert.NotEmpty(f.LikelyCause); Assert.NotEmpty(f.NextStep); });
        Assert.Equal("No device could be asked for its alarms.", AlarmCheck.Summarise([results[0]]));
    }

    // --- the simulator, so the app can be tried without hardware ---

    [Fact]
    public void The_simulated_troubles_appear_in_the_event_list()
    {
        var m = SimulatedDeviceModel.CreateSample(1001);
        m.AddSampleProblems();
        var list = m.ActiveEvents();
        Assert.Equal([Ai1, Ai2, Msi1], list.Select(e => e.objectIdentifier));
        Assert.Equal(BacnetEventStates.EVENT_STATE_HIGH_LIMIT, list[0].eventState);
        Assert.Equal(BacnetEventStates.EVENT_STATE_FAULT, list[1].eventState);
        Assert.Equal(BacnetEventStates.EVENT_STATE_NORMAL, list[2].eventState);
        Assert.Equal([false, true, false], EventText.AckedFrom(list[2].acknowledgedTransitions));

        // The same, read as properties (what the Status_Flags fallback reads).
        Assert.True(m.TryRead(Ai1, BacnetPropertyIds.PROP_EVENT_TIME_STAMPS, uint.MaxValue, out var stamps, out _));
        Assert.Equal(3, stamps.Count);
        Assert.All(stamps, s => Assert.Equal(BacnetApplicationTags.BACNET_APPLICATION_TAG_TIMESTAMP, s.Tag));
        Assert.True(m.TryRead(Ai1, BacnetPropertyIds.PROP_HIGH_LIMIT, uint.MaxValue, out _, out _));
    }

    [Fact]
    public void The_simulator_checks_an_acknowledgement_like_a_controller()
    {
        var m = SimulatedDeviceModel.CreateSample(1001);
        m.AddSampleProblems();
        var zone = m.ActiveEvents()[0];
        var stamp = zone.eventTimeStamps[0];

        var stale = new BacnetGenericTime(stamp.Time.AddMinutes(-5), BacnetTimestampTags.TIME_STAMP_DATETIME);
        Assert.Equal(BacnetErrorCodes.ERROR_CODE_INVALID_TIME_STAMP, m.Acknowledge(Ai1, BacnetEventStates.EVENT_STATE_HIGH_LIMIT, stale)?.Code);
        Assert.Null(m.Acknowledge(Ai1, BacnetEventStates.EVENT_STATE_HIGH_LIMIT, stamp));
        Assert.Equal([true, true, true], EventText.AckedFrom(m.ActiveEvents()[0].acknowledgedTransitions)); // still in alarm, but seen

        // The cleared filter alarm leaves the list once both of its transitions are acknowledged.
        var filter = m.ActiveEvents().Single(e => e.objectIdentifier.Equals(Msi1));
        Assert.Null(m.Acknowledge(Msi1, BacnetEventStates.EVENT_STATE_OFFNORMAL, filter.eventTimeStamps[0]));
        Assert.Null(m.Acknowledge(Msi1, BacnetEventStates.EVENT_STATE_NORMAL, filter.eventTimeStamps[2]));
        Assert.DoesNotContain(m.ActiveEvents(), e => e.objectIdentifier.Equals(Msi1));
    }

    [Fact]
    public void A_limit_alarm_clears_only_past_its_deadband()
    {
        var m = SimulatedDeviceModel.CreateSample(1001);
        m.SetLimits(Av1, high: 80, low: 60, deadband: 2);
        BacnetEventStates State() => m.ActiveEvents().SingleOrDefault(e => e.objectIdentifier.Equals(Av1)).eventState;
        void Write(float f) => m.Write(Av1, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, f), 8, out _);

        Assert.DoesNotContain(m.ActiveEvents(), e => e.objectIdentifier.Equals(Av1)); // 72: normal, nothing to list
        Write(85);
        Assert.Equal(BacnetEventStates.EVENT_STATE_HIGH_LIMIT, State());
        Assert.Equal(BacnetStatusFlags.STATUS_FLAG_IN_ALARM, m.StatusFlags(Av1));
        Write(79); // under the limit, but not by the deadband
        Assert.Equal(BacnetEventStates.EVENT_STATE_HIGH_LIMIT, State());
        Write(77);
        Assert.Equal(BacnetEventStates.EVENT_STATE_NORMAL, State()); // still listed: the alarm and the return are unacknowledged
        Assert.Equal((BacnetStatusFlags)0, m.StatusFlags(Av1));
    }
}
