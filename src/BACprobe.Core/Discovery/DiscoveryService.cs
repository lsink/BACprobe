using System.Collections.Concurrent;
using System.IO.BACnet;
using BACprobe.Core.Networking;

namespace BACprobe.Core.Discovery;

public sealed class DiscoveryService : IDisposable
{
    private readonly BacnetClient _client;
    private readonly ConcurrentDictionary<uint, DiscoveredDevice> _devices = new();

    public DiscoveryService(AdapterInfo adapter, int port = PreflightRules.BacnetPort, int timeoutMs = 3000, int retries = 1)
    {
        var transport = new BacnetIpUdpProtocolTransport(port, useExclusivePort: false,
            localEndpointIp: adapter.Address.ToString());
        _client = new BacnetClient(transport, timeoutMs, retries);
        _client.OnIam += OnIam;
    }

    public void Start() => _client.Start();

    private void OnIam(BacnetClient sender, BacnetAddress adr, uint deviceId, uint maxApdu,
        BacnetSegmentations segmentation, ushort vendorId)
    {
        _devices.TryAdd(deviceId, new DiscoveredDevice
        {
            InstanceId = deviceId, Address = adr, MaxApdu = maxApdu, Segmentation = segmentation, VendorId = vendorId,
        });
    }

    /// <summary>Broadcast Who-Is and collect I-Am replies for <paramref name="wait"/>. -1 = no limit.</summary>
    public async Task<IReadOnlyList<DiscoveredDevice>> WhoIsAsync(int low, int high, TimeSpan wait, CancellationToken ct = default)
    {
        _client.WhoIs(low, high);
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

    public void Dispose() => _client.Dispose();
}
