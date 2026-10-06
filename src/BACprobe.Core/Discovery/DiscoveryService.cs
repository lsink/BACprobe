using System.Collections.Concurrent;
using System.IO.BACnet;
using BACprobe.Core.Networking;

namespace BACprobe.Core.Discovery;

public sealed class DiscoveryService : IDisposable
{
    private readonly BacnetClient _client;
    private readonly IBacnetTransport _transport;
    private readonly ConcurrentDictionary<uint, DiscoveredDevice> _devices = new();
    // One entry per (device number, address): devices re-announce themselves all day (every Who-Is from a front end),
    // and keeping each repeat would grow without limit over a long session. Repeats add nothing to the conflict check.
    private readonly ConcurrentDictionary<(uint Instance, string Address), IAmObservation> _heard = new();

    public DiscoveryService(AdapterInfo adapter, int port = PreflightRules.BacnetPort, int timeoutMs = 3000, int retries = 1)
        : this(new BacnetIpUdpProtocolTransport(port, useExclusivePort: false, localEndpointIp: adapter.Address.ToString()), timeoutMs, retries)
    {
    }

    /// <summary>
    /// Run on any transport, such as an MS/TP master (see <c>MstpActive.Join</c>). Everything that works over IP (discovery, browsing,
    /// reads, writes, live values, trends) works the same way; only BBMD registration is IP-specific.
    /// </summary>
    public static DiscoveryService ForTransport(IBacnetTransport transport, int timeoutMs = 3000, int retries = 1) => new(transport, timeoutMs, retries);

    private DiscoveryService(IBacnetTransport transport, int timeoutMs, int retries)
    {
        _transport = transport;
        // The library defaults to MAX_SEG0, which tells every device "send me nothing bigger than one packet": big object
        // lists and property reads then abort and fall back to slow one-at-a-time reads. Accept segmented replies.
        _client = new BacnetClient(transport, timeoutMs, retries) { MaxSegments = BacnetMaxSegments.MAX_SEG65 };
        _client.OnIam += OnIam;
        _client.OnIAmRouterToNetworkMessage += OnIAmRouter;
        _client.OnEventNotify += OnEventNotify;
        _client.OnUnconfirmedServiceRequest += OnUnconfirmedEventNotify;
    }

    /// <summary>An alarm or event notification arrived (only once BACprobe is on a recipient list; see <see cref="Alarms.AlarmListenRequest"/>).</summary>
    public event Action<Alarms.AlarmNotification>? AlarmNotified;

    // Confirmed notifications (a device may send them although BACprobe asks for unconfirmed): answer, or it keeps resending.
    private void OnEventNotify(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetEventNotificationData data, bool needConfirm)
    {
        if (!needConfirm) return; // unconfirmed ones are read in OnUnconfirmedEventNotify, which also copes with types the library cannot decode
        sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_EVENT_NOTIFICATION, invokeId);
        AlarmNotified?.Invoke(new Alarms.AlarmNotification(data.initiatingObjectIdentifier.instance, data.eventObjectIdentifier,
            data.fromState, data.toState, data.messageText, DateTime.Now));
    }

    private void OnUnconfirmedEventNotify(BacnetClient sender, BacnetAddress adr, BacnetPduTypes type, BacnetUnconfirmedServices service,
        byte[] buffer, int offset, int length)
    {
        if (service != BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_EVENT_NOTIFICATION || AlarmNotified is null) return;
        var now = DateTime.Now;
        var n = System.IO.BACnet.Serialize.Services.DecodeEventNotifyData(buffer, offset, length, out var data) >= 0
            ? new Alarms.AlarmNotification(data.initiatingObjectIdentifier.instance, data.eventObjectIdentifier, data.fromState, data.toState,
                data.messageText, now)
            : Alarms.AlarmNotification.FromHeader(buffer, offset, length, now); // a vendor or newer event type: still know whose alarms changed
        if (n is not null) AlarmNotified?.Invoke(n);
    }

    /// <summary>BACprobe's own recipient entry, for alarm recipient lists: only on BACnet/IP (null on MS/TP for now).</summary>
    public Alarms.AlarmRecipient? OwnRecipient => _transport is BacnetIpUdpProtocolTransport ip ? Alarms.AlarmRecipient.ForEndPoint(ip.LocalEndPoint) : null;

    /// <summary>A device's Notification Class objects (where alarm recipients are kept). Throws if the object list cannot be read.</summary>
    public async Task<IReadOnlyList<BacnetObjectId>> ReadNotificationClassesAsync(DiscoveredDevice device, CancellationToken ct = default) =>
        [.. (await OpenDevice(device).ReadObjectListAsync(ct)).Where(id => id.type == BacnetObjectTypes.OBJECT_NOTIFICATION_CLASS)];

    /// <summary>Set after <see cref="RegisterWithBbmdAsync"/>. While registered, Who-Is also goes through the BBMD.</summary>
    public Bbmd.ForeignDeviceRegistration? BbmdRegistration { get; private set; }

    public void Start() => _client.Start();

    /// <summary>A BBMD is a BACnet/IP thing: only an IP connection can register with one.</summary>
    public bool SupportsBbmd => _transport is BacnetIpUdpProtocolTransport;

    /// <summary>Register as a foreign device so broadcasts reach other subnets. Call after <see cref="Start"/>.</summary>
    public async Task<Bbmd.ForeignDeviceRegistration> RegisterWithBbmdAsync(Bbmd.BbmdTarget target, CancellationToken ct = default)
    {
        if (_transport is not BacnetIpUdpProtocolTransport ip)
            throw new InvalidOperationException("A BBMD only exists on BACnet/IP, and this connection is MS/TP.\n" +
                                                "  Likely cause: a BBMD address was given together with an MS/TP port.\n" +
                                                "  Next step:    leave the BBMD out when connecting through MS/TP.");
        BbmdRegistration?.Dispose();
        var registration = new Bbmd.ForeignDeviceRegistration(new Bbmd.LibraryBvlcLink(ip, _client), target);
        BbmdRegistration = registration;
        await registration.RegisterAsync(ct: ct);
        return registration;
    }

    /// <summary>Object-level reads for one discovered device, sharing this service's connection.</summary>
    public Browsing.DeviceBrowser OpenDevice(DiscoveredDevice device) => new(_client, device);

    /// <summary>Read a device's trend logs (settings and recorded history).</summary>
    public Trends.TrendLogReader OpenTrendLogs(DiscoveredDevice device) => new(_client, device);

    /// <summary>Keep a device's points up to date using COV where the device supports it and polling for the rest.</summary>
    public Live.LiveWatcher CreateLiveWatcher(DiscoveredDevice device, IReadOnlyList<Browsing.ObjectSummary> points, Live.LiveOptions options) =>
        new(_client, device, new Browsing.DeviceBrowser(_client, device), points, options);

    /// <summary>Writes through this service's connection; every attempt is logged and overrides are tracked.</summary>
    public Alarms.AlarmReader CreateAlarmReader() => new(_client);

    public Writing.DeviceWriter CreateWriter(Writing.WriteLog log, Writing.OverrideTracker tracker) => new(_client, log, tracker);

    private void OnIam(BacnetClient sender, BacnetAddress adr, uint deviceId, uint maxApdu,
        BacnetSegmentations segmentation, ushort vendorId)
    {
        // Keep every reply, not just the first per device number: a second device using the same number would otherwise be invisible.
        var address = AddressInfo.Describe(adr);
        _heard.TryAdd((deviceId, address), new IAmObservation(deviceId, address, AddressInfo.NetworkOf(adr), AddressInfo.MacOf(adr), vendorId));
        _devices.TryAdd(deviceId, new DiscoveredDevice
        {
            InstanceId = deviceId, Address = adr, MaxApdu = maxApdu, Segmentation = segmentation, VendorId = vendorId,
        });
    }

    private readonly ConcurrentDictionary<string, HashSet<ushort>> _routers = new();

    private void OnIAmRouter(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, byte[] buffer, int offset, int messageLength)
    {
        var body = buffer.AsSpan(offset, Math.Max(0, Math.Min(messageLength, buffer.Length - offset)));
        var networks = RouterAnnouncement.ParseNetworks(body);
        var set = _routers.GetOrAdd(adr.ToString(), _ => []);
        lock (set) foreach (var n in networks) set.Add(n);
    }

    /// <summary>Routers that have announced themselves, with the networks each one says it can reach.</summary>
    public IReadOnlyList<RouterObservation> Routers =>
        [.. _routers.OrderBy(kv => kv.Key).Select(kv => { lock (kv.Value) return new RouterObservation(kv.Key, [.. kv.Value.Order()]); })];

    /// <summary>Broadcast "who is a router?" so routers announce which networks they serve.</summary>
    public void AskForRouters() =>
        _client.SendNetworkMessage(_transport.GetBroadcastAddress(), [], 0, BacnetNetworkMessageTypes.NETWORK_MESSAGE_WHO_IS_ROUTER_TO_NETWORK);

    /// <summary>
    /// The network map: devices grouped by network, matched to the routers that announced them, with findings for
    /// things that look wrong. Pass true for <paramref name="scanWasFiltered"/> if the scan used a device-number range.
    /// </summary>
    public NetworkMap BuildNetworkMap(IReadOnlyList<DiscoveredDevice> devices, bool scanWasFiltered = false) =>
        NetworkMapBuilder.Build(devices, Routers, scanWasFiltered);

    /// <summary>Every distinct (device number, address) heard so far, including conflicting duplicates.</summary>
    public IReadOnlyList<IAmObservation> Heard =>
        [.. _heard.Values.OrderBy(o => o.Instance).ThenBy(o => o.AddressText, StringComparer.Ordinal)];

    /// <summary>Conflicts in what has answered so far: duplicate device numbers, duplicate addresses, unassigned devices.</summary>
    public IReadOnlyList<NetworkFinding> CheckNetwork() => NetworkCheck.Analyze(Heard);

    /// <summary>Broadcast Who-Is and collect I-Am replies for <paramref name="wait"/>. -1 = no limit.</summary>
    public async Task<IReadOnlyList<DiscoveredDevice>> WhoIsAsync(int low, int high, TimeSpan wait, CancellationToken ct = default)
    {
        _client.WhoIs(low, high);
        try { AskForRouters(); }
        catch (Exception) { /* a transport that cannot send network messages: devices on this network are still found */ }
        if (BbmdRegistration is { IsRegistered: true } bbmd) bbmd.RemoteWhoIs(low, high); // BBMD forwards it to its other subnets
        try { await Task.Delay(wait, ct); }
        catch (OperationCanceledException) { }
        // Devices also announce themselves unasked (at power-up, or answering another tool's Who-Is): keep to the range asked for.
        return _devices.Values.Where(d => InRange(d.InstanceId, low, high)).OrderBy(d => d.InstanceId).ToList();
    }

    /// <summary>
    /// Ask "who has this object?" by name or by object identifier, and collect the I-Have answers for <paramref name="wait"/>.
    /// Goes to this subnet (and networks behind its routers); it does not go through a BBMD. -1 = no device-number limit.
    /// </summary>
    public async Task<IReadOnlyList<IHaveReply>> WhoHasAsync(string? name, BacnetObjectId? id, TimeSpan wait, int low = -1, int high = -1,
        CancellationToken ct = default)
    {
        if (name is null == id is null) throw new ArgumentException("Look for a name or an object, not both.");
        var replies = new ConcurrentDictionary<(uint, BacnetObjectId, string), IHaveReply>();
        void OnRequest(BacnetClient sender, BacnetAddress adr, BacnetPduTypes type, BacnetUnconfirmedServices service, byte[] buffer, int offset, int length)
        {
            if (service != BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_I_HAVE) return;
            if (!IHaveCodec.TryDecode(buffer, offset, length, out var device, out var obj, out var objName)) return;
            if (!InRange(device, low, high)) return;
            var where = AddressInfo.Describe(adr);
            replies.TryAdd((device, obj, where), new IHaveReply(device, obj, objName, where));
        }

        _client.OnUnconfirmedServiceRequest += OnRequest;
        try
        {
            if (id is { } oid) _client.WhoHas(oid, low, high);
            else _client.WhoHas(name!, low, high);
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { }
        }
        finally { _client.OnUnconfirmedServiceRequest -= OnRequest; }
        return [.. replies.Values.OrderBy(r => r.DeviceInstance).ThenBy(r => (int)r.Point.type).ThenBy(r => r.Point.instance)];
    }

    /// <summary>True when a device number falls in a Who-Is range; -1 means no limit on that side.</summary>
    public static bool InRange(uint instance, int low, int high) =>
        (low < 0 || instance >= (uint)low) && (high < 0 || instance <= (uint)high);

    /// <summary>
    /// Read name/vendor/model/firmware. ReadPropertyMultiple first, per-property ReadProperty as fallback.
    /// <paramref name="progress"/> hears about each device as soon as it is done, so a slow one does not hold up the rest.
    /// </summary>
    public async Task EnrichAsync(IEnumerable<DiscoveredDevice> devices, IProgress<DiscoveredDevice>? progress = null,
        int parallelism = 8, CancellationToken ct = default)
    {
        await Parallel.ForEachAsync(devices, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (d, token) =>
            {
                await EnrichOneAsync(d, token);
                progress?.Report(d);
            });
    }

    private static readonly BacnetPropertyIds[] Props =
    [
        BacnetPropertyIds.PROP_OBJECT_NAME, BacnetPropertyIds.PROP_VENDOR_NAME,
        BacnetPropertyIds.PROP_MODEL_NAME, BacnetPropertyIds.PROP_FIRMWARE_REVISION,
    ];

    private async Task EnrichOneAsync(DiscoveredDevice d, CancellationToken ct)
    {
        var oid = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, d.InstanceId);
        string? lastError = null;
        try
        {
            // The clock rides along with the first read: a device without one just answers those two properties with an error.
            var refs = Props.Append(BacnetPropertyIds.PROP_LOCAL_DATE).Append(BacnetPropertyIds.PROP_LOCAL_TIME)
                .Select(p => new BacnetPropertyReference((uint)p, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL)).ToList();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = await _client.ReadPropertyMultipleAsync(d.Address, oid, refs, cancellationToken: ct);
            clock.Stop();
            var pcNow = DateTime.Now;
            d.ResponseTime = clock.Elapsed;
            DateTime? date = null, time = null;
            foreach (var pv in result.SelectMany(r => r.values))
            {
                var prop = (BacnetPropertyIds)pv.property.propertyIdentifier;
                var first = pv.value?.FirstOrDefault();
                if (prop == BacnetPropertyIds.PROP_LOCAL_DATE) date = AsDateTime(first);
                else if (prop == BacnetPropertyIds.PROP_LOCAL_TIME) time = AsDateTime(first);
                else Apply(d, prop, first);
            }
            if (DeviceHealth.CombineClock(date, time) is { } deviceClock) d.ClockSkew = deviceClock - pcNow;
            return;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && BacnetFailure.IsTimeout(ex))
        {
            lastError = ex.Message; // not answering at all: four more reads would only wait out four more timeouts
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* device may not support RPM; fall through to per-property reads */ }

        if (lastError is null)
            foreach (var p in Props)
            {
                try
                {
                    var values = await _client.ReadPropertyAsync(d.Address, oid, p, cancellationToken: ct);
                    Apply(d, p, values.FirstOrDefault());
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { lastError = ex.Message; }
            }
        if (d.ObjectName is null && lastError is not null)
            d.EnrichError = $"Could not read device properties ({lastError}). " +
                            "The device may be busy, or reachable only through a router that is dropping the request; try again.";
    }

    private static DateTime? AsDateTime(BacnetValue? v) =>
        v is { Value: DateTime dt, Tag: not BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR } ? dt : null;

    private static void Apply(DiscoveredDevice d, BacnetPropertyIds prop, BacnetValue? v)
    {
        if (v is not { } val || val.Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR) return;
        var text = val.Value?.ToString();
        switch (prop)
        {
            case BacnetPropertyIds.PROP_OBJECT_NAME: d.ObjectName = text; break;
            case BacnetPropertyIds.PROP_VENDOR_NAME: d.VendorName = text; break;
            case BacnetPropertyIds.PROP_MODEL_NAME: d.ModelName = text; break;
            case BacnetPropertyIds.PROP_FIRMWARE_REVISION: d.FirmwareRevision = text; break;
        }
    }

    public void Dispose()
    {
        BbmdRegistration?.Dispose();
        _client.Dispose();
    }
}
