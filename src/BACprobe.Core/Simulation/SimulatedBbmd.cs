using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BACprobe.Core.Bbmd;
using BACprobe.Core.Networking;

namespace BACprobe.Core.Simulation;

/// <summary>
/// A minimal BBMD for testing foreign-device registration without hardware.
/// Control port (default 47809, so it does not fight the BACnet port): Register-Foreign-Device is answered with a
/// BVLC-Result, and Distribute-Broadcast-To-Network is re-broadcast on the local subnet.
/// It also listens on 47808 and relays broadcasts it hears (such as I-Am from local devices) to every registered
/// foreign device as Forwarded-NPDU, which is what a real BBMD does. It answers Read-Broadcast-Distribution-Table with the
/// table it was given (by default just itself) and Read-Foreign-Device-Table with who is registered.
/// </summary>
public sealed class SimulatedBbmd : IDisposable
{
    private const byte BvlcType = 0x81;
    private const byte FnResult = 0x00, FnForwarded = 0x04, FnRegister = 0x05, FnDistribute = 0x09, FnOriginalBroadcast = 0x0B;
    private const ushort ResultSuccess = 0x0000, ResultRegisterNak = 0x0030;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30); // spec: FDT entries live TTL + 30 s

    private readonly AdapterInfo _adapter;
    private readonly int _controlPort;
    private readonly int _bacnetPort;
    private readonly bool _refuse;
    private readonly Dictionary<IPEndPoint, (DateTime Expiry, ushort Ttl)> _foreignDevices = [];
    private readonly IReadOnlyList<BdtEntry> _broadcastTable;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private Socket? _control;
    private Socket? _listen;

    public Action<string>? Log { get; set; }

    /// <param name="refuseRegistrations">Answer every registration (and foreign device table read) with a NAK, like a BBMD with foreign devices disabled.</param>
    /// <param name="broadcastTable">What Read-Broadcast-Distribution-Table returns; null for just this BBMD, two-hop.</param>
    public SimulatedBbmd(AdapterInfo adapter, int controlPort = 47809, bool refuseRegistrations = false,
        int bacnetPort = PreflightRules.BacnetPort, IReadOnlyList<BdtEntry>? broadcastTable = null)
    {
        _adapter = adapter;
        _controlPort = controlPort;
        _bacnetPort = bacnetPort;
        _refuse = refuseRegistrations;
        _broadcastTable = broadcastTable ?? [new BdtEntry(new IPEndPoint(adapter.Address, controlPort), IPAddress.Broadcast)];
    }

    /// <summary>Where this BBMD answers: the address to give 'bacprobe bbmd' or the app's BBMD box.</summary>
    public IPEndPoint EndPoint => new(_adapter.Address, _controlPort);

    public int RegisteredCount
    {
        get { lock (_lock) return _foreignDevices.Count(kv => kv.Value.Expiry > DateTime.UtcNow); }
    }

    public void Start()
    {
        _control = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _control.Bind(new IPEndPoint(_adapter.Address, _controlPort));

        _listen = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _listen.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listen.Bind(new IPEndPoint(IPAddress.Any, _bacnetPort));

        _ = Task.Run(() => LoopAsync(_control, control: true));
        _ = Task.Run(() => LoopAsync(_listen, control: false));
    }

    private async Task LoopAsync(Socket socket, bool control)
    {
        var buffer = new byte[2048];
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _cts.Token);
                var from = (IPEndPoint)r.RemoteEndPoint;
                if (r.ReceivedBytes < 4 || buffer[0] != BvlcType) continue;
                var data = buffer.AsSpan(0, r.ReceivedBytes).ToArray();
                if (control) HandleControl(from, data);
                else HandleBroadcast(from, data);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { /* ICMP unreachable from a vanished foreign device; keep going */ }
        }
    }

    private void HandleControl(IPEndPoint from, byte[] msg)
    {
        switch (msg[1])
        {
            case FnRegister when msg.Length >= 6:
                var ttl = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(4, 2));
                if (_refuse)
                {
                    Log?.Invoke($"[BBMD] Register-Foreign-Device from {from} (ttl {ttl}s) -> NAK (refusing)");
                    SendResult(_control!, from, ResultRegisterNak);
                    return;
                }
                lock (_lock) _foreignDevices[from] = (DateTime.UtcNow + TimeSpan.FromSeconds(ttl) + Grace, ttl);
                Log?.Invoke($"[BBMD] Register-Foreign-Device from {from} (ttl {ttl}s) -> OK ({RegisteredCount} registered)");
                SendResult(_control!, from, ResultSuccess);
                break;

            case FnDistribute when msg.Length > 4 && IsRegistered(from):
                // Re-broadcast on our subnet as Forwarded-NPDU, with the foreign device as the originator.
                var npdu = msg.AsSpan(4).ToArray();
                Log?.Invoke($"[BBMD] Distribute-Broadcast-To-Network from {from} ({npdu.Length} byte NPDU) -> local broadcast");
                _control!.SendTo(Forwarded(from, npdu), new IPEndPoint(_adapter.Broadcast, _bacnetPort));
                break;

            case FnDistribute:
                Log?.Invoke($"[BBMD] Distribute-Broadcast-To-Network from unregistered {from} -> ignored");
                break;

            case BvlcTables.FnReadBdt:
                Log?.Invoke($"[BBMD] Read-Broadcast-Distribution-Table from {from} -> {_broadcastTable.Count} entr(ies)");
                _control!.SendTo(BvlcTables.EncodeBdtAck(_broadcastTable), from);
                break;

            case BvlcTables.FnReadFdt when _refuse:
                Log?.Invoke($"[BBMD] Read-Foreign-Device-Table from {from} -> NAK (foreign devices refused)");
                SendResult(_control!, from, BvlcTables.ReadFdtNak);
                break;

            case BvlcTables.FnReadFdt:
                List<FdtEntry> table;
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    table = [.. _foreignDevices.Where(kv => kv.Value.Expiry > now)
                        .Select(kv => new FdtEntry(kv.Key, kv.Value.Ttl, (ushort)Math.Min(ushort.MaxValue, (kv.Value.Expiry - now).TotalSeconds)))];
                }
                Log?.Invoke($"[BBMD] Read-Foreign-Device-Table from {from} -> {table.Count} entr(ies)");
                _control!.SendTo(BvlcTables.EncodeFdtAck(table), from);
                break;
        }
    }

    /// <summary>Broadcasts heard on the BACnet port are relayed to all registered foreign devices.</summary>
    private void HandleBroadcast(IPEndPoint from, byte[] msg)
    {
        if (msg[1] != FnOriginalBroadcast || msg.Length <= 4) return; // ignore our own Forwarded-NPDU and unicasts
        var npdu = msg.AsSpan(4).ToArray();
        List<IPEndPoint> targets;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            foreach (var dead in _foreignDevices.Where(kv => kv.Value.Expiry <= now).Select(kv => kv.Key).ToList())
                _foreignDevices.Remove(dead);
            targets = [.. _foreignDevices.Keys];
        }
        if (targets.Count == 0) return;

        Log?.Invoke($"[BBMD] Broadcast from {from} -> forwarding to {targets.Count} foreign device(s)");
        var frame = Forwarded(from, npdu);
        foreach (var t in targets) _control!.SendTo(frame, t);
    }

    private bool IsRegistered(IPEndPoint ep)
    {
        lock (_lock) return _foreignDevices.TryGetValue(ep, out var entry) && entry.Expiry > DateTime.UtcNow;
    }

    private static void SendResult(Socket s, IPEndPoint to, ushort result)
    {
        var frame = new byte[6];
        frame[0] = BvlcType;
        frame[1] = FnResult;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), result);
        s.SendTo(frame, to);
    }

    private static byte[] Forwarded(IPEndPoint origin, byte[] npdu)
    {
        var frame = new byte[10 + npdu.Length];
        frame[0] = BvlcType;
        frame[1] = FnForwarded;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)frame.Length);
        origin.Address.GetAddressBytes().CopyTo(frame, 4);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), (ushort)origin.Port);
        npdu.CopyTo(frame, 10);
        return frame;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _control?.Dispose();
        _listen?.Dispose();
        _cts.Dispose();
    }
}
