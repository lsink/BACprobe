using System.Collections.Concurrent;
using System.IO.BACnet;
using BACprobe.Core.Networking;

namespace BACprobe.Core.Discovery;

public sealed class DiscoveryService : IDisposable
{
    private readonly BacnetClient _client;
    private readonly BacnetIpUdpProtocolTransport _transport;
    private readonly ConcurrentDictionary<uint, DiscoveredDevice> _devices = new();
    private readonly ConcurrentQueue<IAmObservation> _heard = new();

    public DiscoveryService(AdapterInfo adapter, int port = PreflightRules.BacnetPort, int timeoutMs = 3000, int retries = 1)
    {
        _transport = new BacnetIpUdpProtocolTransport(port, useExclusivePort: false,
            localEndpointIp: adapter.Address.ToString());
        // The library defaults to MAX_SEG0, which tells every device "send me nothing bigger than one packet": big object
        // lists and property reads then abort and fall back to slow one-at-a-time reads. Accept segmented replies.
        _client = new BacnetClient(_transport, timeoutMs, retries) { MaxSegments = BacnetMaxSegments.MAX_SEG65 };
        _client.OnIam += OnIam;
        _client.OnIAmRouterToNetworkMessage += OnIAmRouter;
    }

    /// <summary>Set after <see cref="RegisterWithBbmdAsync"/>. While registered, Who-Is also goes through the BBMD.</summary>
    public Bbmd.ForeignDeviceRegistration? BbmdRegistration { get; private set; }

    public void Start() => _client.Start();

    /// <summary>Register as a foreign device so broadcasts reach other subnets. Call after <see cref="Start"/>.</summary>
    public async Task<Bbmd.ForeignDeviceRegistration> RegisterWithBbmdAsync(Bbmd.BbmdTarget target, CancellationToken ct = default)
    {
        BbmdRegistration?.Dispose();
        var registration = new Bbmd.ForeignDeviceRegistration(new Bbmd.LibraryBvlcLink(_transport, _client), target);
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
    public Writing.DeviceWriter CreateWriter(Writing.WriteLog log, Writing.OverrideTracker tracker) => new(_client, log, tracker);

    private void OnIam(BacnetClient sender, BacnetAddress adr, uint deviceId, uint maxApdu,
        BacnetSegmentations segmentation, ushort vendorId)
    {
        // Keep every reply, not just the first per device number: a second device using the same number would otherwise be invisible.
        _heard.Enqueue(new IAmObservation(deviceId, AddressInfo.Describe(adr), AddressInfo.NetworkOf(adr), AddressInfo.MacOf(adr), vendorId));
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

    /// <summary>Every I-Am heard so far, including repeats and conflicting duplicates.</summary>
    public IReadOnlyList<IAmObservation> Heard => [.. _heard];

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
        return _devices.Values.OrderBy(d => d.InstanceId).ToList();
    }

    /// <summary>Read name/vendor/model/firmware. ReadPropertyMultiple first, per-property ReadProperty as fallback.</summary>
    public async Task EnrichAsync(IEnumerable<DiscoveredDevice> devices, int parallelism = 8, CancellationToken ct = default)
    {
        await Parallel.ForEachAsync(devices, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (d, _) => await EnrichOneAsync(d));
    }

    private static readonly BacnetPropertyIds[] Props =
    [
        BacnetPropertyIds.PROP_OBJECT_NAME, BacnetPropertyIds.PROP_VENDOR_NAME,
        BacnetPropertyIds.PROP_MODEL_NAME, BacnetPropertyIds.PROP_FIRMWARE_REVISION,
    ];

    private async Task EnrichOneAsync(DiscoveredDevice d)
    {
        var oid = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, d.InstanceId);
        try
        {
            var refs = Props.Select(p => new BacnetPropertyReference((uint)p, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL)).ToList();
            var result = await _client.ReadPropertyMultipleAsync(d.Address, oid, refs);
            foreach (var pv in result.SelectMany(r => r.values))
                Apply(d, (BacnetPropertyIds)pv.property.propertyIdentifier, pv.value?.FirstOrDefault());
            return;
        }
        catch (Exception) { /* device may not support RPM; fall through to per-property reads */ }

        string? lastError = null;
        foreach (var p in Props)
        {
            try
            {
                var values = await _client.ReadPropertyAsync(d.Address, oid, p);
                Apply(d, p, values.FirstOrDefault());
            }
            catch (Exception ex) { lastError = ex.Message; }
        }
        if (d.ObjectName is null && lastError is not null)
            d.EnrichError = $"Could not read device properties ({lastError}). " +
                            "The device may be busy, or reachable only through a router that is dropping the request; try again.";
    }

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
