using System.Collections.Concurrent;
using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Tests;

/// <summary>One end of an in-memory RS-485 wire: what one end writes the other end reads. No echo, like a normal adapter.</summary>
internal sealed class LoopbackSerial : IBacnetSerialTransport
{
    private readonly BlockingCollection<byte> _incoming = [];
    private LoopbackSerial? _peer;

    public static (LoopbackSerial A, LoopbackSerial B) Pair()
    {
        var a = new LoopbackSerial();
        var b = new LoopbackSerial();
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    public int BytesToRead => _incoming.Count;
    public void Dispose() { }
    public void Open() { }
    public void Close() { }

    public void Write(byte[] buffer, int offset, int length)
    {
        for (var i = 0; i < length; i++) _peer!._incoming.Add(buffer[offset + i]);
    }

    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
    {
        if (!_incoming.TryTake(out var first, Math.Max(timeoutMs, 0))) return -BacnetMstpProtocolTransport.ETIMEDOUT;
        buffer[offset] = first;
        var n = 1;
        while (n < length && _incoming.TryTake(out var next)) buffer[offset + n++] = next;
        return n;
    }
}

/// <summary>
/// Two of the library's MS/TP masters on an in-memory wire: BACprobe's connection (through <see cref="DiscoveryService.ForTransport"/>)
/// and a stand-in device. This exercises everything above the serial port with no hardware: the token ring forming, discovery,
/// and a read. It does NOT test the real serial driver, adapter latency, or a real trunk.
/// </summary>
public class MstpLoopbackTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Discovery_and_a_read_work_over_a_two_master_mstp_ring()
    {
        var (wireA, wireB) = LoopbackSerial.Pair();
        using var bacprobeSide = new BacnetMstpProtocolTransport(wireA, sourceAddress: 5, maxMaster: 127, maxInfoFrames: 1);
        using var deviceSide = new BacnetMstpProtocolTransport(wireB, sourceAddress: 1, maxMaster: 127, maxInfoFrames: 1);

        // The stand-in device: answers Who-Is, and a read of its object name.
        using var device = new BacnetClient(deviceSide, 3000, 1);
        device.OnWhoIs += (sender, adr, low, high) =>
        {
            if ((low < 0 || 1001 >= low) && (high < 0 || 1001 <= high))
                sender.Iam(1001, BacnetSegmentations.SEGMENTATION_NONE);
        };
        device.OnReadPropertyRequest += (sender, adr, invokeId, objectId, property, maxSegments) =>
        {
            if (objectId.type == BacnetObjectTypes.OBJECT_DEVICE && property.propertyIdentifier == (uint)BacnetPropertyIds.PROP_OBJECT_NAME)
                sender.ReadPropertyResponse(adr, invokeId, sender.GetSegmentBuffer(maxSegments), objectId, property,
                    [new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING, "Loopback VAV")]);
            else
                sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, invokeId,
                    BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY);
        };
        device.Start();

        using var svc = DiscoveryService.ForTransport(bacprobeSide, timeoutMs: 3000, retries: 1);
        svc.Start();

        // The ring takes a moment to form (token generation, poll for master), so ask until it answers.
        DiscoveredDevice? found = null;
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (found is null && DateTime.UtcNow < deadline)
            found = (await svc.WhoIsAsync(-1, -1, TimeSpan.FromSeconds(2))).FirstOrDefault(d => d.InstanceId == 1001);

        Assert.NotNull(found);
        Assert.Equal(BacnetAddressTypes.MSTP, found!.Address.type);
        Assert.Equal(1, found.Address.adr![0]); // MS/TP MAC of the stand-in

        var name = await svc.OpenDevice(found).ReadPropertyAsync(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, 1001), BacnetPropertyIds.PROP_OBJECT_NAME);
        Assert.Contains("Loopback VAV", name.Display);
    }

    [Fact]
    public void A_bbmd_is_refused_on_mstp_rather_than_crashing()
    {
        var (wireA, _) = LoopbackSerial.Pair();
        using var transport = new BacnetMstpProtocolTransport(wireA, 5, 127, 1);
        using var svc = DiscoveryService.ForTransport(transport);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => svc.RegisterWithBbmdAsync(new Core.Bbmd.BbmdTarget(System.Net.IPAddress.Loopback, 47808, 60)));
        Assert.Contains("MS/TP", ex.Result.Message);
    }
}
