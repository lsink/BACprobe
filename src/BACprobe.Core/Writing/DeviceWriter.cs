using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Writing;

/// <param name="Explanation">
/// Set when something deserves a proper explanation: the device refused, or accepted the write but the point did not change.
/// Null for a write that worked.
/// </param>
public sealed record WriteOutcome(bool Success, string Message, PromptContent? Explanation = null);

/// <summary>
/// Sends writes and releases, logs every attempt, and tracks the overrides left in place.
/// Callers must have shown the user <see cref="WriteRequest.ConfirmationText"/> before calling.
/// </summary>
public sealed class DeviceWriter(BacnetClient client, WriteLog log, OverrideTracker tracker)
{
    public WriteLog Log => log;
    public OverrideTracker Overrides => tracker;

    /// <summary>
    /// Read-only mode, enforced here and not only by hiding buttons: no new write and no Out_Of_Service on goes out, whoever asks.
    /// Releasing an override and putting a point back in service still work, because giving control back is cleanup, not a change.
    /// </summary>
    public bool ReadOnly { get; set; }

    private static readonly WriteOutcome ReadOnlyOutcome = new(false,
        "Read-only mode is on, so nothing was written.\n  Likely cause: the Read-only box at the top of the app is ticked (it starts ticked).\n  Next step:    untick Read-only if you mean to make a change.");

    public async Task<WriteOutcome> ExecuteAsync(WriteRequest request, CancellationToken ct = default)
    {
        var action = request.IsRelease ? "release" : $"write {request.ValueText}";
        if (ReadOnly && !request.IsRelease)
        {
            Record(request, action, false, "blocked: read-only mode");
            return ReadOnlyOutcome;
        }
        try
        {
            if (request.Priority is < 1 or > 16)
                throw new ArgumentOutOfRangeException(nameof(request), "Priority must be 1 to 16.");

            var value = request.Value ?? new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);
            await client.WritePropertyAsync(request.Device.Address, request.Point, BacnetPropertyIds.PROP_PRESENT_VALUE,
                [value], priority: (byte)request.Priority, cancellationToken: ct);

            if (request.IsRelease)
                tracker.Remove(request.Device.InstanceId, request.Point, request.Priority);
            else
                tracker.Record(new TrackedOverride(request.Device, request.DeviceName, request.Point, request.ObjectName,
                    request.Priority, request.ValueText));

            if (request.IsRelease)
            {
                Record(request, action, true, "released");
                return new WriteOutcome(true, "released");
            }

            // The device said yes. That does not mean the point changed (a higher priority may be holding it): look.
            var probe = await VerifyAsync(request, ct);
            var explanation = WriteExplainer.ExplainIneffective(request, probe);
            var msg = explanation is not null ? $"accepted, but it had no effect: {explanation.Headline.ToLowerInvariant()}"
                : probe.Reachable ? "device accepted the write"
                : "device accepted the write (the point could not be read back to check)";
            Record(request, action, true, msg);
            return new WriteOutcome(true, msg, explanation);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var text = WriteErrors.Explain(ex);
            Record(request, action, false, text.Summary);

            // Find out why: look at the point, then explain what we found.
            PointProbe? probe = null;
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(4));
                probe = await new PointProber(client, request.Device).ProbeAsync(request.Point, limit.Token);
            }
            catch (Exception) { /* the explanation says it could not check */ }

            // A timed-out write may still have landed: track it, so leaving offers to release it.
            if (WriteExplainer.MayHaveLanded(request, WriteErrors.IsTimeout(ex), probe))
                tracker.Record(new TrackedOverride(request.Device, request.DeviceName, request.Point, request.ObjectName,
                    request.Priority, request.ValueText, Unconfirmed: true));
            return new WriteOutcome(false, text.Full, WriteExplainer.ExplainFailure(request, ex.Message, probe));
        }
    }

    /// <summary>
    /// Turn Out_Of_Service on or off. Turning it on is tracked like an override, so leaving offers to put the point back.
    /// Callers must have shown the user the confirmation first.
    /// </summary>
    public async Task<WriteOutcome> SetOutOfServiceAsync(OutOfServiceRequest request, CancellationToken ct = default)
    {
        var action = request.TurnOn ? "out of service: on" : "out of service: off";
        if (ReadOnly && request.TurnOn)
        {
            LogOutOfService(request, action, false, "blocked: read-only mode");
            return ReadOnlyOutcome;
        }
        var held =new TrackedOverride(request.Device, request.DeviceName, request.Point, request.ObjectName,
            TrackedOverride.OutOfServicePriority, "out of service");
        try
        {
            await client.WritePropertyAsync(request.Device.Address, request.Point, BacnetPropertyIds.PROP_OUT_OF_SERVICE,
                [new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, request.TurnOn)], cancellationToken: ct);

            if (request.TurnOn) tracker.Record(held);
            else tracker.Remove(request.Device.InstanceId, request.Point, TrackedOverride.OutOfServicePriority);
            var msg = request.TurnOn ? "point is out of service" : "point is back in service";
            LogOutOfService(request, action, true, msg);
            return new WriteOutcome(true, msg);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var text = WriteErrors.Explain(ex);
            LogOutOfService(request, action, false, text.Summary);
            // A timed-out "on" may still have landed: offer to put the point back when leaving.
            if (request.TurnOn && WriteErrors.IsTimeout(ex)) tracker.Record(held with { Unconfirmed = true });
            return new WriteOutcome(false, text.Full);
        }
    }

    /// <summary>
    /// Change one configuration property (no priority). Refused in read-only mode, logged with the old and new value, and read back.
    /// Callers must have shown the user the confirmation first.
    /// </summary>
    public async Task<WriteOutcome> WritePropertyAsync(PropertyWriteRequest request, CancellationToken ct = default)
    {
        if (ReadOnly)
        {
            LogProperty(request, false, "blocked: read-only mode");
            return ReadOnlyOutcome;
        }
        try
        {
            await client.WritePropertyAsync(request.Device.Address, request.Point, request.Property, [request.Value], cancellationToken: ct);
            string after;
            try
            {
                var back = await client.ReadPropertyAsync(request.Device.Address, request.Point, request.Property, cancellationToken: ct);
                after = BacnetNames.FormatValues(request.Point.type, request.Property, back);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { after = "(could not read it back)"; }
            var msg = $"device accepted it; it now reads {after}";
            LogProperty(request, true, msg);
            return new WriteOutcome(true, msg);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var text = WriteErrors.Explain(ex);
            LogProperty(request, false, text.Summary);
            return new WriteOutcome(false, text.Full);
        }
    }

    /// <summary>
    /// Set the clock, restart, mute or un-mute a device. Refused in read-only mode except un-muting (giving control back is cleanup).
    /// A mute is tracked like an override, so leaving offers to un-mute it. Callers must have shown the confirmation first.
    /// </summary>
    public async Task<WriteOutcome> RunDeviceActionAsync(DeviceActionRequest request, CancellationToken ct = default)
    {
        var device = request.Device;
        var asPoint = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, device.InstanceId);
        if (ReadOnly && request.Kind != DeviceActionKind.Unmute)
        {
            LogDevice(request, false, "blocked: read-only mode");
            return ReadOnlyOutcome;
        }
        if (request.Problem is { } problem) return new WriteOutcome(false, problem);

        try
        {
            switch (request.Kind)
            {
                case DeviceActionKind.SyncTime or DeviceActionKind.SyncTimeUtc:
                {
                    // Unconfirmed: the device never says whether it took it, so look at its clock afterwards.
                    client.SynchronizeTime(device.Address, request.Kind == DeviceActionKind.SyncTimeUtc ? DateTime.UtcNow : DateTime.Now);
                    await Task.Delay(VerifyEvery, ct);
                    var skew = await ReadClockSkewAsync(device, ct);
                    device.ClockSkew = skew ?? device.ClockSkew;
                    var msg = skew is null ? "sent (the device clock could not be read back to check)"
                        : skew.Value.Duration() < TimeSpan.FromMinutes(1) ? "device clock is now within a minute of this PC"
                        : $"sent, but the device clock is still {DeviceHealth.DescribeSkew(skew.Value)}: it may ignore time sync, or use another time zone";
                    var worked = skew is null || skew.Value.Duration() < TimeSpan.FromMinutes(1);
                    LogDevice(request, worked, msg);
                    return new WriteOutcome(worked, msg);
                }
                case DeviceActionKind.WarmStart or DeviceActionKind.ColdStart:
                    await client.ReinitializeAsync(device.Address,
                        request.Kind == DeviceActionKind.ColdStart ? BacnetReinitializedStates.BACNET_REINIT_COLDSTART : BacnetReinitializedStates.BACNET_REINIT_WARMSTART,
                        request.Password ?? "", cancellationToken: ct);
                    LogDevice(request, true, "device accepted; it is restarting");
                    return new WriteOutcome(true, "The device accepted and is restarting. Give it a minute, then Scan to see it again.");
                default:
                {
                    var enableDisable = request.Kind switch { DeviceActionKind.Mute => 1u, DeviceActionKind.MuteInitiation => 2u, _ => 0u };
                    await client.DeviceCommunicationControlAsync(device.Address, request.IsMute ? request.Minutes : 0, enableDisable,
                        request.Password ?? "", cancellationToken: ct);
                    if (request.IsMute)
                        tracker.Record(new TrackedOverride(device, request.DeviceName, asPoint, request.DeviceName, TrackedOverride.MutedPriority,
                            request.Kind == DeviceActionKind.Mute ? $"for {request.Minutes} min" : $"sends nothing on its own, for {request.Minutes} min",
                            Password: request.Password));
                    else tracker.Remove(device.InstanceId, asPoint, TrackedOverride.MutedPriority);
                    var msg = request.IsMute ? $"muted for {request.Minutes} minutes" : "talking again";
                    LogDevice(request, true, msg);
                    return new WriteOutcome(true, msg);
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var text = DeviceActionErrors.Explain(ex);
            LogDevice(request, false, text.Summary);
            // A timed-out mute may still have landed: offer to undo it when leaving.
            if (request.IsMute && WriteErrors.IsTimeout(ex))
                tracker.Record(new TrackedOverride(device, request.DeviceName, asPoint, request.DeviceName, TrackedOverride.MutedPriority,
                    $"for {request.Minutes} min", Unconfirmed: true, Password: request.Password));
            return new WriteOutcome(false, text.Full);
        }
    }

    /// <summary>BACprobe's own recipient entry (where alarms should be sent); null when the connection is not BACnet/IP.</summary>
    public Alarms.AlarmRecipient? Me => client.Transport is BacnetIpUdpProtocolTransport ip ? Alarms.AlarmRecipient.ForEndPoint(ip.LocalEndPoint) : null;

    /// <summary>
    /// Add BACprobe to (or take it off) the recipient lists of these notification classes. Adding is refused in read-only mode and tracked,
    /// so leaving offers to undo it; taking it off is cleanup and always allowed. Callers must have shown the confirmation first.
    /// </summary>
    public async Task<WriteOutcome> ListenForAlarmsAsync(Alarms.AlarmListenRequest request, CancellationToken ct = default)
    {
        if (ReadOnly && !request.Stop)
        {
            foreach (var nc in request.NotificationClasses) LogListen(request, nc, false, "blocked: read-only mode");
            return ReadOnlyOutcome;
        }
        if (request.NotificationClasses.Count == 0)
            return new WriteOutcome(false, "This device has no Notification Class objects, so it cannot send its alarms to BACprobe.\n" +
                                           "  Likely cause: it does not report alarms itself (a front-end may be watching its points instead).\n" +
                                           "  Next step:    use Refresh in the alarm list to look again by hand.");
        var reference = new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_RECIPIENT_LIST, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL);
        var done = 0;
        foreach (var nc in request.NotificationClasses)
        {
            try
            {
                if (request.Stop)
                {
                    await client.RemoveListElementAsync(request.Device.Address, nc, reference, [request.Me.ToValue()], cancellationToken: ct);
                    tracker.Remove(request.Device.InstanceId, nc, TrackedOverride.AlarmRecipientPriority);
                }
                else
                {
                    await client.AddListElementAsync(request.Device.Address, nc, reference, [request.Me.ToValue()], cancellationToken: ct);
                    tracker.Record(new TrackedOverride(request.Device, request.DeviceName, nc, BacnetNames.ObjectLabel(nc),
                        TrackedOverride.AlarmRecipientPriority, request.Me.AddressText));
                }
                LogListen(request, nc, true, "done");
                done++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var text = AlarmListenErrors.Explain(ex, request.Stop);
                LogListen(request, nc, false, text.Summary);
                // The device may have added us and only the ack was lost: remember it, so leaving still offers to take us off.
                if (!request.Stop && WriteErrors.IsTimeout(ex))
                    tracker.Record(new TrackedOverride(request.Device, request.DeviceName, nc, BacnetNames.ObjectLabel(nc),
                        TrackedOverride.AlarmRecipientPriority, request.Me.AddressText, Unconfirmed: true));
                // Taking BACprobe off a list it was never on (or that the device emptied) is not worth keeping a reminder for.
                if (request.Stop && ex.Message.Contains("LIST_ELEMENT_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
                    tracker.Remove(request.Device.InstanceId, nc, TrackedOverride.AlarmRecipientPriority);
                var partly = done > 0 ? $"{done} of {request.NotificationClasses.Count} done, then: " : "";
                return new WriteOutcome(false, partly + text.Full);
            }
        }
        return new WriteOutcome(true, request.Stop ? "BACprobe is off the recipient lists" : $"BACprobe is on {done} recipient list(s)");
    }

    private void LogListen(Alarms.AlarmListenRequest r, BacnetObjectId nc, bool success, string result) =>
        log.Add(new WriteLogEntry(DateTimeOffset.Now, r.Device.InstanceId, r.DeviceName, BacnetNames.ObjectLabel(nc), r.LogAction(nc),
            Priority: 0, success, result)); // a configuration change has no priority

    /// <summary>Device clock minus PC clock, or null if the device will not say.</summary>
    private async Task<TimeSpan?> ReadClockSkewAsync(DiscoveredDevice device, CancellationToken ct)
    {
        try
        {
            var oid = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, device.InstanceId);
            var date = await client.ReadPropertyAsync(device.Address, oid, BacnetPropertyIds.PROP_LOCAL_DATE, cancellationToken: ct);
            var time = await client.ReadPropertyAsync(device.Address, oid, BacnetPropertyIds.PROP_LOCAL_TIME, cancellationToken: ct);
            var pcNow = DateTime.Now;
            return DeviceHealth.CombineClock(date is [{ Value: DateTime d }] ? d : null, time is [{ Value: DateTime t }] ? t : null) is { } clock
                ? clock - pcNow
                : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    private void LogDevice(DeviceActionRequest r, bool success, string result) =>
        log.Add(new WriteLogEntry(DateTimeOffset.Now, r.Device.InstanceId, r.DeviceName, $"device {r.Device.InstanceId}", r.LogAction,
            Priority: 0, success, result)); // a device action has no priority

    private void LogProperty(PropertyWriteRequest r, bool success, string result) =>
        log.Add(new WriteLogEntry(DateTimeOffset.Now, r.Device.InstanceId, r.DeviceName, r.ObjectLabel, r.LogAction,
            Priority: 0, success, result)); // a configuration change has no priority

    /// <summary>
    /// Acknowledge one alarm. It changes nothing on the point, but it does change what the site's operators see, so it is
    /// refused in read-only mode and logged like a write. Callers must have shown the user the confirmation first.
    /// </summary>
    public async Task<WriteOutcome> AcknowledgeAsync(Alarms.AlarmAckRequest request, CancellationToken ct = default)
    {
        var e = request.Event;
        var targets = request.Targets;
        if (ReadOnly)
        {
            LogAck(request, "acknowledge alarm", false, "blocked: read-only mode");
            return ReadOnlyOutcome;
        }
        if (targets.Count == 0) return new WriteOutcome(false, "That alarm is already acknowledged.");

        // One request per waiting transition (into alarm, back to normal...): BACnet acknowledges them one at a time.
        var done = 0;
        foreach (var t in targets)
        {
            try
            {
                await client.AlarmAcknowledgementAsync(e.Device.Address, e.Point, t.StateAcked, request.AckSource, t.Stamp,
                    new BacnetGenericTime(DateTime.Now, BacnetTimestampTags.TIME_STAMP_DATETIME), cancellationToken: ct);
                LogAck(request, Alarms.AlarmAckRequest.LogAction(t), true, "acknowledged");
                done++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var text = Alarms.AckErrors.Explain(ex);
                LogAck(request, Alarms.AlarmAckRequest.LogAction(t), false, text.Summary);
                var partly = done > 0 ? $"{done} of {targets.Count} acknowledged, then: " : "";
                return new WriteOutcome(false, partly + text.Full);
            }
        }
        return new WriteOutcome(true, "acknowledged");
    }

    private void LogAck(Alarms.AlarmAckRequest r, string action, bool success, string result) =>
        log.Add(new WriteLogEntry(DateTimeOffset.Now, r.Event.Device.InstanceId, r.DeviceName, r.ObjectLabel, action,
            Priority: 0, success, result)); // an acknowledgement has no priority

    private void LogOutOfService(OutOfServiceRequest r, string action, bool success, string result) =>
        log.Add(new WriteLogEntry(DateTimeOffset.Now, r.Device.InstanceId, r.DeviceName, r.ObjectLabel, action,
            TrackedOverride.OutOfServicePriority, success, result));

    /// <summary>Read the point back until it shows the written value, for up to <see cref="VerifyFor"/> (controllers update on their own schedule).</summary>
    private async Task<PointProbe> VerifyAsync(WriteRequest request, CancellationToken ct)
    {
        var prober = new PointProber(client, request.Device);
        var deadline = DateTime.UtcNow + VerifyFor;
        PointProbe probe;
        do
        {
            probe = await prober.ProbeAsync(request.Point, ct);
            if (!probe.Reachable || WriteExplainer.IsEffective(request, probe)) return probe;
            await Task.Delay(VerifyEvery, ct);
        }
        while (DateTime.UtcNow < deadline);
        return await prober.ProbeAsync(request.Point, ct); // one last look
    }

    /// <summary>How long to wait for a slow controller to show the new value before calling the write ineffective.</summary>
    public TimeSpan VerifyFor { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan VerifyEvery { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Release every override this session left in place. Returns (released, failed).</summary>
    public async Task<(int Released, int Failed)> ReleaseAllAsync(CancellationToken ct = default)
    {
        int ok = 0, bad = 0;
        foreach (var o in tracker.Active)
        {
            var outcome = o.IsAlarmRecipient
                ? await ListenForAlarmsAsync(new Alarms.AlarmListenRequest(o.Device, o.DeviceName, [o.Point], Me ?? throw new InvalidOperationException(
                    "BACprobe's address is unknown, so it cannot take itself off the recipient list."), Stop: true), ct)
                : o.IsMuted
                ? await RunDeviceActionAsync(new DeviceActionRequest(o.Device, o.DeviceName, DeviceActionKind.Unmute, Password: o.Password), ct)
                : o.IsOutOfService
                ? await SetOutOfServiceAsync(new OutOfServiceRequest(o.Device, o.DeviceName, o.Point, o.ObjectName, TurnOn: false), ct)
                : await ExecuteAsync(new WriteRequest(o.Device, o.DeviceName, o.Point, o.ObjectName, null, "release", o.Priority), ct);
            if (outcome.Success) ok++; else bad++;
        }
        return (ok, bad);
    }

    private void Record(WriteRequest r, string action, bool success, string result) =>
        log.Add(new WriteLogEntry(DateTimeOffset.Now, r.Device.InstanceId, r.DeviceName, r.ObjectLabel, action, r.Priority, success, result));
}

public sealed record WriteErrorText(string Summary, string Cause, string NextStep)
{
    public string Full => $"{Summary}\n  Likely cause: {Cause}\n  Next step:    {NextStep}";
}

public static class WriteErrors
{
    public static bool IsTimeout(Exception ex) => BacnetFailure.IsTimeout(ex);

    /// <summary>Map the library's plain exceptions to a likely cause and a next step.</summary>
    public static WriteErrorText Explain(Exception ex)
    {
        var m = ex.Message;
        bool Has(string s) => m.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (IsTimeout(ex))
            return new("The device did not answer the write (timeout).",
                "Network drop, a busy controller, or a router dropping the request. The write may or may not have happened.",
                "Read the point again to see its real state before retrying.");
        if (Has("WRITE_ACCESS_DENIED"))
            return new("The device refused the write: access denied.",
                "The point is read-only (an input, or not commandable), or the device is protected against writes.",
                "Pick an output or value point that has a priority array, or check the point's Out Of Service setting.");
        if (Has("VALUE_OUT_OF_RANGE"))
            return new("The device refused the value: out of range.",
                "The number is outside the limits the controller allows for this point.",
                "Try a value inside the point's normal range.");
        if (Has("INVALID_DATA_TYPE") || Has("DATATYPE_NOT_SUPPORTED"))
            return new("The device refused the value: wrong data type.",
                "The point expects a different type (for example a number instead of on/off).",
                "Read the point to see what type it holds, then write a matching value.");
        if (Has("UNKNOWN_OBJECT"))
            return new("The device says that object does not exist.",
                "The object was deleted or the instance number is wrong.",
                "Select the device again to refresh its object list.");
        if (Has("UNKNOWN_PROPERTY"))
            return new("The device says that point has no Present Value to write.",
                "This object type is not writable this way.",
                "Choose a different point.");
        if (Has("SERVICE_REQUEST_DENIED") || Has("REJECT") || Has("ABORT"))
            return new($"The device rejected the request ({m}).",
                "The controller is busy, in a state where it will not take writes, or does not support WriteProperty here.",
                "Wait a moment and try again, or check the controller's mode.");
        return new($"The device returned an error: {m}",
            "The controller refused the write for a reason BACprobe does not recognise.",
            "Read the point to check its state, and look up the error text in the controller's documentation.");
    }
}
