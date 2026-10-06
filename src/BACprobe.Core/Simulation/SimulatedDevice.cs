using System.Globalization;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using BACprobe.Core.Networking;

namespace BACprobe.Core.Simulation;

/// <summary>
/// A fake BACnet/IP device for testing without hardware. Answers Who-Is, ReadProperty, ReadPropertyMultiple,
/// WriteProperty, SubscribeCOV, ReadRange, GetEventInformation and AcknowledgeAlarm from a <see cref="SimulatedDeviceModel"/>.
/// </summary>
public sealed class SimulatedDevice : IDisposable
{
    private readonly BacnetClient _client;
    private readonly bool _supportRpm;
    private readonly bool _drift;
    private readonly Random _rng = new();
    private Timer? _driftTimer;
    private Timer? _covTimer;
    private readonly bool _supportCov;
    private readonly bool _supportEvents;
    private readonly int _covLimit;
    private readonly List<CovSub> _subs = [];
    private readonly Lock _subsLock = new();

    /// <summary>Analog points notify when they move by at least this much (the COV increment).</summary>
    private const double CovIncrement = 0.2;

    private sealed class CovSub(BacnetAddress subscriber, uint process, BacnetObjectId obj)
    {
        public BacnetAddress Subscriber { get; } = subscriber;
        public uint Process { get; } = process;
        public BacnetObjectId Object { get; } = obj;
        public bool Confirmed { get; set; }
        public DateTime? Expiry { get; set; }
        public string? LastText { get; set; }
        public double? LastNumber { get; set; }
        public BacnetStatusFlags? LastFlags { get; set; }
    }

    private DateTime _silentUntil = DateTime.MinValue;
    private DateTime _mutedUntil = DateTime.MinValue;
    private bool _muteInitiationOnly;

    /// <summary>True while restarting (see <see cref="GoSilent"/>) or muted by DeviceCommunicationControl: it answers nothing.</summary>
    private bool Silent => DateTime.UtcNow < _silentUntil || (DateTime.UtcNow < _mutedUntil && !_muteInitiationOnly);

    /// <summary>True while it may not send anything of its own (COV notifications), muted fully or for initiation only.</summary>
    private bool InitiationMuted => DateTime.UtcNow < _mutedUntil;

    /// <summary>If set, restarts and communication control must carry this password, like a protected controller.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Stop answering anything for a while, then come back having forgotten every COV subscription: like a controller
    /// that loses power and restarts, or a network drop. Its address stays the same, as a real controller's does.
    /// </summary>
    public void GoSilent(TimeSpan duration)
    {
        lock (_subsLock) _subs.Clear();
        _silentUntil = DateTime.UtcNow + duration;
        Log?.Invoke($"[{Model.Instance}] going silent for {duration.TotalSeconds:0} s (subscriptions forgotten, like a restart)");
    }

    public SimulatedDeviceModel Model { get; }
    public Action<string>? Log { get; set; }

    /// <param name="supportRpm">False mimics older devices that refuse ReadPropertyMultiple, to exercise the fallback.</param>
    /// <param name="supportCov">False mimics a device with no change-of-value support, to exercise the polling fallback.</param>
    /// <param name="covLimit">Maximum number of subscriptions the device will accept (0 = no limit), like a real controller running out of slots.</param>
    /// <param name="supportEvents">False mimics a device with no GetEventInformation, to exercise the Status_Flags fallback.</param>
    public SimulatedDevice(AdapterInfo adapter, SimulatedDeviceModel model, bool supportRpm = true, int port = PreflightRules.BacnetPort,
        bool drift = true, bool supportCov = true, int covLimit = 0, bool supportEvents = true)
    {
        Model = model;
        _drift = drift;
        _supportCov = supportCov;
        _supportEvents = supportEvents;
        _covLimit = covLimit;
        _supportRpm = supportRpm;
        var transport = new BacnetIpUdpProtocolTransport(port, useExclusivePort: false,
            localEndpointIp: adapter.Address.ToString());
        _client = new BacnetClient(transport) { VendorId = model.VendorId };
        _client.OnWhoIs += OnWhoIs;
        _client.OnReadPropertyRequest += OnReadProperty;
        _client.OnReadPropertyMultipleRequest += OnReadPropertyMultiple;
        _client.OnWritePropertyRequest += OnWriteProperty;
        _client.OnSubscribeCOV += OnSubscribeCov;
        _client.OnReadRange += OnReadRange;
        _client.OnWhoHas += OnWhoHas;
        _client.OnTimeSynchronize += OnTimeSynchronize;
        _client.OnDeviceCommunicationControl += OnCommunicationControl;
        _client.OnReinitializedDevice += OnReinitialize;
        _client.OnGetAlarmSummaryOrEventInformation += OnGetEventInformation;
        _client.OnAlarmAcknowledge += OnAlarmAcknowledge;
    }

    private void OnTimeSynchronize(BacnetClient sender, BacnetAddress adr, DateTime dateTime, bool utc)
    {
        if (Silent) return;
        var local = utc ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc).ToLocalTime() : dateTime;
        Model.ClockSkew = local - DateTime.Now; // its clock now reads what it was sent, as a synced device would
        Log?.Invoke($"[{Model.Instance}] {(utc ? "UTC time" : "Time")} synchronization from {adr}: clock set to {local:HH:mm:ss}");
    }

    // Communication control and restarts are answered even while muted: that is how a muted device is un-muted.
    private void OnCommunicationControl(BacnetClient sender, BacnetAddress adr, byte invokeId, uint minutes, uint enableDisable, string password,
        BacnetMaxSegments maxSegments)
    {
        if (DateTime.UtcNow < _silentUntil) return; // restarting
        if (!PasswordOk(sender, adr, invokeId, BacnetConfirmedServices.SERVICE_CONFIRMED_DEVICE_COMMUNICATION_CONTROL, password)) return;
        sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_DEVICE_COMMUNICATION_CONTROL, invokeId);
        if (enableDisable == 0) _mutedUntil = DateTime.MinValue;
        else
        {
            _muteInitiationOnly = enableDisable == 2;
            _mutedUntil = minutes == 0 ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(minutes);
        }
        Log?.Invoke($"[{Model.Instance}] DeviceCommunicationControl from {adr}: " +
                    (enableDisable == 0 ? "enabled" : $"{(enableDisable == 2 ? "initiation disabled" : "disabled")} for {(minutes == 0 ? "ever" : $"{minutes} min")}"));
    }

    private void OnReinitialize(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetReinitializedStates state, string password,
        BacnetMaxSegments maxSegments)
    {
        if (DateTime.UtcNow < _silentUntil) return;
        if (!PasswordOk(sender, adr, invokeId, BacnetConfirmedServices.SERVICE_CONFIRMED_REINITIALIZE_DEVICE, password)) return;
        sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_REINITIALIZE_DEVICE, invokeId);
        Log?.Invoke($"[{Model.Instance}] ReinitializeDevice ({state}) from {adr}");
        _mutedUntil = DateTime.MinValue; // a restart clears communication control
        GoSilent(TimeSpan.FromSeconds(state == BacnetReinitializedStates.BACNET_REINIT_COLDSTART ? 8 : 4));
    }

    private bool PasswordOk(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetConfirmedServices service, string? password)
    {
        if (Password is null || Password == password) return true;
        Log?.Invoke($"[{Model.Instance}] {service} from {adr} -> wrong password");
        sender.ErrorResponse(adr, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SECURITY, BacnetErrorCodes.ERROR_CODE_PASSWORD_FAILURE);
        return false;
    }

    private void OnWhoHas(BacnetClient sender, BacnetAddress adr, int low, int high, BacnetObjectId? objId, string objName)
    {
        if (Silent) return; // pretending to be off: say nothing
        if ((low >= 0 && Model.Instance < low) || (high >= 0 && Model.Instance > high)) return;
        var device = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, Model.Instance);
        foreach (var (id, name) in Model.FindObjects(objId, objId is null ? objName : null))
        {
            Log?.Invoke($"[{Model.Instance}] Who-Has {(objId is { } o ? o.ToString() : $"\"{objName}\"")} from {adr} -> I-Have {id} \"{name}\"");
            sender.IHave(device, id, name); // broadcast, as the standard says
        }
    }

    /// <summary>Events per GetEventInformation answer: small, so the client has to follow the "more events" chain like it would on a busy controller.</summary>
    private const int EventsPerAnswer = 2;

    private void OnGetEventInformation(BacnetClient sender, BacnetAddress adr, byte invokeId, bool getEvent, BacnetObjectId lastReceived,
        BacnetMaxAdpu maxApdu, BacnetMaxSegments maxSegments)
    {
        if (Silent) return; // pretending to be off: say nothing, so the client times out
        if (!_supportEvents)
        {
            Log?.Invoke($"[{Model.Instance}] GetEventInformation from {adr} -> refused (no alarm list service)");
            sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_GET_EVENT_INFORMATION, invokeId,
                BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_SERVICE_REQUEST_DENIED);
            return;
        }

        // "Last received" is absent on the first request (the library marks that with MAX_BACNET_OBJECT_TYPE); later ones continue after it.
        var all = Model.ActiveEvents();
        var start = lastReceived.type == BacnetObjectTypes.MAX_BACNET_OBJECT_TYPE
            ? 0
            : all.ToList().FindIndex(e => e.objectIdentifier.Equals(lastReceived)) + 1;
        var page = all.Skip(start).Take(EventsPerAnswer).ToArray();
        var more = start + page.Length < all.Count;
        Log?.Invoke($"[{Model.Instance}] GetEventInformation from {adr} -> {page.Length} event(s){(more ? ", more to come" : "")}");
        sender.GetAlarmSummaryOrEventInformationResponse(adr, getEvent, invokeId, sender.GetSegmentBuffer(maxSegments), page, more);
    }

    private void OnAlarmAcknowledge(BacnetClient sender, BacnetAddress adr, byte invokeId, uint ackProcessIdentifier,
        BacnetObjectId eventObject, uint eventStateAcked, string ackSource, BacnetGenericTime eventTimeStamp, BacnetGenericTime ackTimeStamp)
    {
        if (Silent) return; // pretending to be off: say nothing, so the client times out
        var err = Model.Acknowledge(eventObject, (BacnetEventStates)eventStateAcked, eventTimeStamp);
        if (err is null)
        {
            Log?.Invoke($"[{Model.Instance}] AcknowledgeAlarm {eventObject} {(BacnetEventStates)eventStateAcked} by \"{ackSource}\" from {adr}");
            sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_ACKNOWLEDGE_ALARM, invokeId);
        }
        else
        {
            Log?.Invoke($"[{Model.Instance}] AcknowledgeAlarm {eventObject} from {adr} -> {err.Value.Code}");
            sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_ACKNOWLEDGE_ALARM, invokeId, err.Value.Class, err.Value.Code);
        }
    }

    public void Start()
    {
        _client.Start();
        _client.Iam(Model.Instance, BacnetSegmentations.SEGMENTATION_TRANSMIT); // announce, like a device powering up
        if (_drift) _driftTimer = new Timer(_ => Model.Tick(_rng), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _covTimer = new Timer(_ => CheckCov(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    private void OnWhoIs(BacnetClient sender, BacnetAddress adr, int low, int high)
    {
        if (Silent) return; // pretending to be off: say nothing, so the client times out
        if ((low >= 0 && Model.Instance < low) || (high >= 0 && Model.Instance > high)) return;
        Log?.Invoke($"[{Model.Instance}] Who-Is from {adr} -> I-Am");
        sender.Iam(Model.Instance, BacnetSegmentations.SEGMENTATION_TRANSMIT);
    }

    private void OnReadProperty(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId,
        BacnetPropertyReference property, BacnetMaxSegments maxSegments)
    {
        if (Silent) return; // pretending to be off: say nothing, so the client times out
        var prop = (BacnetPropertyIds)property.propertyIdentifier;
        if (Model.TryRead(objectId, prop, property.propertyArrayIndex, out var values, out var err))
        {
            Log?.Invoke($"[{Model.Instance}] ReadProperty {objectId} {prop} from {adr}");
            sender.ReadPropertyResponse(adr, invokeId, sender.GetSegmentBuffer(maxSegments), objectId, property, values);
        }
        else
        {
            Log?.Invoke($"[{Model.Instance}] ReadProperty {objectId} {prop} from {adr} -> {err.Code}");
            sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, invokeId, err.Class, err.Code);
        }
    }

    private void OnReadPropertyMultiple(BacnetClient sender, BacnetAddress adr, byte invokeId,
        IList<BacnetReadAccessSpecification> specs, BacnetMaxSegments maxSegments)
    {
        if (Silent) return; // pretending to be off: say nothing, so the client times out
        if (!_supportRpm)
        {
            Log?.Invoke($"[{Model.Instance}] ReadPropertyMultiple from {adr} -> refused (legacy mode)");
            sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROP_MULTIPLE, invokeId,
                BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_SERVICE_REQUEST_DENIED);
            return;
        }

        Log?.Invoke($"[{Model.Instance}] ReadPropertyMultiple ({specs.Count} object(s)) from {adr}");
        var results = new List<BacnetReadAccessResult>();
        foreach (var spec in specs)
        {
            var props = new List<BacnetPropertyValue>();
            foreach (var pref in spec.propertyReferences)
            {
                var id = (BacnetPropertyIds)pref.propertyIdentifier;
                var refs = id is BacnetPropertyIds.PROP_ALL or BacnetPropertyIds.PROP_REQUIRED or BacnetPropertyIds.PROP_OPTIONAL
                    ? Model.AllProperties(spec.objectIdentifier).Select(p => new BacnetPropertyReference(p)).ToList()
                    : [pref];
                foreach (var r in refs)
                {
                    var ok = Model.TryRead(spec.objectIdentifier, (BacnetPropertyIds)r.propertyIdentifier, r.propertyArrayIndex,
                        out var values, out var err);
                    props.Add(new BacnetPropertyValue
                    {
                        property = r,
                        value = ok
                            ? values
                            : [new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR, new BacnetError(err.Class, err.Code))],
                    });
                }
            }
            results.Add(new BacnetReadAccessResult(spec.objectIdentifier, props));
        }
        sender.ReadPropertyMultipleResponse(adr, invokeId, sender.GetSegmentBuffer(maxSegments), results); // segments a big answer if the client allows it
    }

    private void OnWriteProperty(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId,
        BacnetPropertyValue value, BacnetMaxSegments maxSegments)
    {
        if (Silent) return; // pretending to be off: say nothing, so the client times out
        var prop = (BacnetPropertyIds)value.property.propertyIdentifier;
        var first = value.value?.FirstOrDefault() ?? new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);
        var err = Model.Write(objectId, prop, first, value.priority, out var summary);
        if (err is null)
        {
            Log?.Invoke($"[{Model.Instance}] WriteProperty {objectId} {prop} from {adr}: {summary}");
            sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invokeId);
            Model.Tick(_rng); // let followers (e.g. Fan Status) catch up with the new command
            CheckCov();
        }
        else
        {
            Log?.Invoke($"[{Model.Instance}] WriteProperty {objectId} {prop} from {adr} -> {err.Value.Code}");
            sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invokeId, err.Value.Class, err.Value.Code);
        }
    }

    /// <summary>How many bytes of records one answer may carry, like the packet-size limit of a real controller. Forces paging.</summary>
    private const int MaxAnswerBytes = 380;

    private void OnReadRange(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId,
        BacnetPropertyReference property, BacnetReadRangeRequestTypes requestType, uint position, DateTime time, int count,
        BacnetMaxSegments maxSegments)
    {
        if (Silent) return; // pretending to be off: say nothing, so the client times out
        List<BacnetLogRecord> slice = [];
        uint firstSequence = 0;
        bool isFirst = false, isLast = false;
        SimError err = default;
        var asked = property.propertyIdentifier == (uint)BacnetPropertyIds.PROP_LOG_BUFFER;
        if (!asked || !Model.TryReadLog(objectId, requestType, position, time, count, out slice, out firstSequence, out isFirst,
                out isLast, out err))
        {
            var code = err.Code == 0 ? BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY : err.Code;
            var cls = err.Code == 0 ? BacnetErrorClasses.ERROR_CLASS_PROPERTY : err.Class;
            Log?.Invoke($"[{Model.Instance}] ReadRange {objectId} from {adr} -> {code}");
            sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_RANGE, invokeId, cls, code);
            return;
        }

        // Pack as many records as fit in one answer; flag MORE_ITEMS if some were left out.
        var buffer = new EncodeBuffer();
        var sent = 0;
        foreach (var record in slice)
        {
            var before = buffer.offset;
            Services.EncodeLogRecord(buffer, record);
            if (buffer.offset > MaxAnswerBytes && sent > 0)
            {
                buffer.offset = before;
                break;
            }
            sent++;
        }

        var flags = BacnetResultFlags.NONE;
        if (isFirst) flags |= BacnetResultFlags.FIRST_ITEM;
        if (isLast && sent == slice.Count) flags |= BacnetResultFlags.LAST_ITEM;
        if (sent < slice.Count || !isLast) flags |= BacnetResultFlags.MORE_ITEMS;

        Log?.Invoke($"[{Model.Instance}] ReadRange {objectId} {requestType} pos {position} count {count} from {adr} -> {sent} record(s)");
        sender.ReadRangeResponse(adr, invokeId, null, objectId, property, flags, (uint)sent,
            buffer.buffer.AsSpan(0, buffer.offset).ToArray(), requestType, firstSequence);
    }

    private void OnSubscribeCov(BacnetClient sender, BacnetAddress adr, byte invokeId, uint process, BacnetObjectId objectId,
        bool cancel, bool confirmed, uint lifetime, BacnetMaxSegments maxSegments)
    {
        if (Silent) return; // pretending to be off: say nothing, so the client times out
        if (!_supportCov)
        {
            Log?.Invoke($"[{Model.Instance}] SubscribeCOV {objectId} from {adr} -> refused (no COV support)");
            sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_SUBSCRIBE_COV, invokeId,
                BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_SERVICE_REQUEST_DENIED);
            return;
        }

        CovSub? sub;
        lock (_subsLock) sub = _subs.FirstOrDefault(x => x.Process == process && x.Object == objectId && x.Subscriber.ToString() == adr.ToString());

        if (cancel)
        {
            lock (_subsLock) if (sub is not null) _subs.Remove(sub);
            Log?.Invoke($"[{Model.Instance}] SubscribeCOV {objectId} from {adr} -> cancelled");
            sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_SUBSCRIBE_COV, invokeId);
            return;
        }

        if (!Model.TryRead(objectId, BacnetPropertyIds.PROP_PRESENT_VALUE, uint.MaxValue, out _, out var err))
        {
            // Unknown object, or an object (like the device itself) with no Present Value to report.
            var code = err.Code == BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT
                ? BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT
                : BacnetErrorCodes.ERROR_CODE_NOT_COV_PROPERTY;
            Log?.Invoke($"[{Model.Instance}] SubscribeCOV {objectId} from {adr} -> {code}");
            sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_SUBSCRIBE_COV, invokeId, err.Class, code);
            return;
        }

        var isNew = sub is null;
        lock (_subsLock)
        {
            if (isNew && _covLimit > 0 && _subs.Count >= _covLimit)
            {
                Log?.Invoke($"[{Model.Instance}] SubscribeCOV {objectId} from {adr} -> refused (out of slots, limit {_covLimit})");
                sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_SUBSCRIBE_COV, invokeId,
                    BacnetErrorClasses.ERROR_CLASS_RESOURCES, BacnetErrorCodes.ERROR_CODE_NO_SPACE_TO_ADD_LIST_ELEMENT);
                return;
            }
            sub ??= new CovSub(adr, process, objectId);
            sub.Confirmed = confirmed;
            sub.Expiry = lifetime == 0 ? null : DateTime.UtcNow.AddSeconds(lifetime);
            if (isNew) _subs.Add(sub);
        }

        Log?.Invoke($"[{Model.Instance}] SubscribeCOV {objectId} from {adr} -> {(isNew ? "subscribed" : "renewed")} (lifetime {lifetime}s)");
        sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_SUBSCRIBE_COV, invokeId);
        if (isNew) SendNotification(sub, force: true); // the spec says: report the current value on subscribing
    }

    /// <summary>Send a notification for every subscription whose point moved enough since it was last reported.</summary>
    private void CheckCov()
    {
        if (InitiationMuted || Silent) return; // told not to send anything on its own
        List<CovSub> subs;
        lock (_subsLock)
        {
            _subs.RemoveAll(x => x.Expiry is { } e && e < DateTime.UtcNow);
            subs = [.. _subs];
        }
        foreach (var sub in subs) SendNotification(sub, force: false);
    }

    private void SendNotification(CovSub sub, bool force)
    {
        if (!Model.TryRead(sub.Object, BacnetPropertyIds.PROP_PRESENT_VALUE, uint.MaxValue, out var pv, out _) || pv.Count == 0) return;

        var text = Convert.ToString(pv[0].Value, CultureInfo.InvariantCulture);
        double? number = pv[0].Value is float f ? f : null;
        var flags = Model.StatusFlags(sub.Object);
        lock (_subsLock)
        {
            var changed = force || flags != sub.LastFlags // a change of status (fault, alarm, out of service) is reported too
                || (number is { } n && sub.LastNumber is { } last ? Math.Abs(n - last) >= CovIncrement : text != sub.LastText);
            if (!changed) return;
            sub.LastText = text;
            sub.LastNumber = number;
            sub.LastFlags = flags;
        }

        var remaining = sub.Expiry is { } e ? (uint)Math.Max(0, (e - DateTime.UtcNow).TotalSeconds) : 0;
        var values = new List<BacnetPropertyValue>
        {
            new() { property = new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_PRESENT_VALUE, ASN1.BACNET_ARRAY_ALL), value = pv },
            new()
            {
                property = new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_STATUS_FLAGS, ASN1.BACNET_ARRAY_ALL),
                value = [new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING, BacnetBitString.ConvertFromInt((uint)flags, 4))],
            },
        };
        Log?.Invoke($"[{Model.Instance}] COV {sub.Object} = {text} -> {sub.Subscriber}");
        _ = NotifyAsync(sub, remaining, values);
    }

    private async Task NotifyAsync(CovSub sub, uint remaining, List<BacnetPropertyValue> values)
    {
        try
        {
            await _client.NotifyAsync(sub.Subscriber, sub.Process, Model.Instance, sub.Object, remaining, sub.Confirmed, values);
        }
        catch (Exception) { /* a subscriber that has gone away; it will time out */ }
    }

    public void Dispose()
    {
        _driftTimer?.Dispose();
        _covTimer?.Dispose();
        _client.Dispose();
    }
}
