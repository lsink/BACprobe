using System.Collections.Concurrent;
using System.IO.BACnet;

namespace BACprobe.Core.Simulation;

/// <summary>
/// The simulator's BACnet/IP transport. The library answers every confirmed service it has no handler for with "unrecognized service",
/// even after the simulator has answered it itself (AddListElement, RemoveListElement). That second answer races the first, so a
/// client can see a refusal for something that worked. This transport drops that reject for requests marked as answered.
/// </summary>
internal sealed class SimulatedIpTransport(int port, string localEndpointIp)
    : BacnetIpUdpProtocolTransport(port, useExclusivePort: false, localEndpointIp: localEndpointIp)
{
    private const byte RejectPdu = 0x60;
    private readonly ConcurrentDictionary<(string Address, byte InvokeId), DateTime> _answered = new();

    /// <summary>The simulator answered this request itself: swallow the library's reject that follows.</summary>
    public void MarkAnswered(BacnetAddress requester, byte invokeId) =>
        _answered[(requester.ToString(), invokeId)] = DateTime.UtcNow;

    public override int Send(byte[] buffer, int offset, int dataLength, BacnetAddress address, bool waitForTransmission, int timeout)
    {
        if (IsReject(buffer, offset, dataLength, out var invokeId) && _answered.TryRemove((address.ToString(), invokeId), out _))
            return dataLength; // already answered: pretend it went
        foreach (var stale in _answered.Where(kv => DateTime.UtcNow - kv.Value > TimeSpan.FromSeconds(10)).Select(kv => kv.Key).ToList())
            _answered.TryRemove(stale, out _);
        return base.Send(buffer, offset, dataLength, address, waitForTransmission, timeout);
    }

    /// <summary>Skips the NPDU (version, control, optional destination/source, hop count) to the APDU type and invoke id.</summary>
    private static bool IsReject(byte[] b, int offset, int length, out byte invokeId)
    {
        invokeId = 0;
        var end = offset + length;
        var pos = offset + 2;
        if (pos > end) return false;
        var control = b[offset + 1];
        var hasDestination = (control & 0x20) != 0;
        if (hasDestination) pos += 3 + b[pos + 2]; // DNET (2), DLEN (1), DADR
        if ((control & 0x08) != 0) pos += 3 + b[pos + 2]; // SNET, SLEN, SADR
        if (hasDestination) pos++; // hop count
        if ((control & 0x80) != 0 || pos + 1 >= end) return false; // a network message, not an APDU
        if ((b[pos] & 0xF0) != RejectPdu) return false;
        invokeId = b[pos + 1];
        return true;
    }
}
