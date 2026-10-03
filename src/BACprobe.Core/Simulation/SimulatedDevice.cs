using System.IO.BACnet;
using BACprobe.Core.Networking;

namespace BACprobe.Core.Simulation;

/// <summary>
/// A fake BACnet/IP device for testing without hardware. Answers Who-Is, ReadProperty, ReadPropertyMultiple
/// and WriteProperty from a <see cref="SimulatedDeviceModel"/>.
/// </summary>
public sealed class SimulatedDevice : IDisposable
{
    private readonly BacnetClient _client;
    private readonly bool _supportRpm;
    private readonly bool _drift;
    private readonly Random _rng = new();
    private Timer? _driftTimer;

    public SimulatedDeviceModel Model { get; }
    public Action<string>? Log { get; set; }

    /// <param name="supportRpm">False mimics older devices that refuse ReadPropertyMultiple, to exercise the fallback.</param>
    public SimulatedDevice(AdapterInfo adapter, SimulatedDeviceModel model, bool supportRpm = true, int port = PreflightRules.BacnetPort,
        bool drift = true)
    {
        Model = model;
        _drift = drift;
        _supportRpm = supportRpm;
        var transport = new BacnetIpUdpProtocolTransport(port, useExclusivePort: false,
            localEndpointIp: adapter.Address.ToString());
        _client = new BacnetClient(transport) { VendorId = model.VendorId };
        _client.OnWhoIs += OnWhoIs;
        _client.OnReadPropertyRequest += OnReadProperty;
        _client.OnReadPropertyMultipleRequest += OnReadPropertyMultiple;
        _client.OnWritePropertyRequest += OnWriteProperty;
    }

    public void Start()
    {
        _client.Start();
        _client.Iam(Model.Instance, BacnetSegmentations.SEGMENTATION_NONE); // announce, like a device powering up
        if (_drift) _driftTimer = new Timer(_ => Model.Tick(_rng), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void OnWhoIs(BacnetClient sender, BacnetAddress adr, int low, int high)
    {
        if ((low >= 0 && Model.Instance < low) || (high >= 0 && Model.Instance > high)) return;
        Log?.Invoke($"[{Model.Instance}] Who-Is from {adr} -> I-Am");
        sender.Iam(Model.Instance, BacnetSegmentations.SEGMENTATION_NONE);
    }

    private void OnReadProperty(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId,
        BacnetPropertyReference property, BacnetMaxSegments maxSegments)
    {
        var prop = (BacnetPropertyIds)property.propertyIdentifier;
        if (Model.TryRead(objectId, prop, property.propertyArrayIndex, out var values, out var err))
        {
            Log?.Invoke($"[{Model.Instance}] ReadProperty {objectId} {prop} from {adr}");
            sender.ReadPropertyResponse(adr, invokeId, null, objectId, property, values);
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
        sender.ReadPropertyMultipleResponse(adr, invokeId, null, results);
    }

    private void OnWriteProperty(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId,
        BacnetPropertyValue value, BacnetMaxSegments maxSegments)
    {
        var prop = (BacnetPropertyIds)value.property.propertyIdentifier;
        var first = value.value?.FirstOrDefault() ?? new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);
        var err = Model.Write(objectId, prop, first, value.priority, out var summary);
        if (err is null)
        {
            Log?.Invoke($"[{Model.Instance}] WriteProperty {objectId} {prop} from {adr}: {summary}");
            sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invokeId);
        }
        else
        {
            Log?.Invoke($"[{Model.Instance}] WriteProperty {objectId} {prop} from {adr} -> {err.Value.Code}");
            sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invokeId, err.Value.Class, err.Value.Code);
        }
    }

    public void Dispose()
    {
        _driftTimer?.Dispose();
        _client.Dispose();
    }
}
