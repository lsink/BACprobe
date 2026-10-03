using System.Net;
using System.Net.Sockets;

namespace BACprobe.Core.Bbmd;

/// <summary>
/// Reads a BBMD's tables over its own short-lived UDP socket on the chosen adapter. It needs no BACnet connection and does
/// not disturb one: the BBMD answers to the port the request came from. Only reads; never changes a table.
/// </summary>
public sealed class BbmdTableReader(IPAddress localAddress, TimeSpan? timeout = null, int attempts = 2)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(2);

    public Task<TableRead<BdtEntry>> ReadBdtAsync(IPEndPoint bbmd, CancellationToken ct = default) =>
        ReadAsync(bbmd, BvlcTables.ReadBdtRequest(), BvlcTables.ReplyKind.Bdt, BvlcTables.ReplyKind.BdtNak, BvlcTables.ParseBdt, ct);

    public Task<TableRead<FdtEntry>> ReadFdtAsync(IPEndPoint bbmd, CancellationToken ct = default) =>
        ReadAsync(bbmd, BvlcTables.ReadFdtRequest(), BvlcTables.ReplyKind.Fdt, BvlcTables.ReplyKind.FdtNak, BvlcTables.ParseFdt, ct);

    private delegate IReadOnlyList<T> Parser<T>(ReadOnlySpan<byte> msg);

    private async Task<TableRead<T>> ReadAsync<T>(IPEndPoint bbmd, byte[] request, BvlcTables.ReplyKind ack, BvlcTables.ReplyKind nak,
        Parser<T> parse, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(localAddress, 0));
        }
        catch (SocketException ex)
        {
            return TableRead.Failed<T>(TableReadStatus.SendFailed, ex.Message);
        }

        var buffer = new byte[1500];
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                await socket.SendToAsync(request, SocketFlags.None, bbmd, ct);
            }
            catch (SocketException ex)
            {
                return TableRead.Failed<T>(TableReadStatus.SendFailed, ex.Message);
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(_timeout);
            try
            {
                while (true)
                {
                    var r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), wait.Token);
                    if (!((IPEndPoint)r.RemoteEndPoint).Address.Equals(bbmd.Address)) continue; // someone else's traffic
                    var msg = buffer.AsSpan(0, r.ReceivedBytes);
                    var kind = BvlcTables.Classify(msg);
                    if (kind == ack) return new TableRead<T>(TableReadStatus.Ok, parse(msg));
                    if (kind == nak) return TableRead.Failed<T>(TableReadStatus.Refused);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* no answer in time: try again */ }
            catch (SocketException) { /* ICMP port unreachable from the far end: treat as no answer */ }
        }
        return TableRead.Failed<T>(TableReadStatus.NoAnswer);
    }
}
