using System.Buffers.Binary;
using System.Net;

namespace BACprobe.Core.Bbmd;

/// <summary>
/// One line of a BBMD's Broadcast Distribution Table: a BBMD that broadcasts are forwarded to, and how.
/// A mask of 255.255.255.255 is "two-hop" (send to that BBMD, which rebroadcasts locally); anything else is "one-hop"
/// (a directed broadcast straight onto that subnet, which routers usually block).
/// </summary>
public sealed record BdtEntry(IPEndPoint Bbmd, IPAddress Mask)
{
    public bool IsTwoHop => Mask.Equals(IPAddress.Broadcast);
    public string Distribution => IsTwoHop ? "two-hop" : "one-hop (directed broadcast)";
}

/// <summary>One line of a BBMD's Foreign Device Table: a PC or device registered from another subnet.</summary>
public sealed record FdtEntry(IPEndPoint Device, ushort TtlSeconds, ushort RemainingSeconds);

public enum TableReadStatus
{
    /// <summary>The table came back (it may be empty).</summary>
    Ok,
    /// <summary>The device answered with a NAK: it will not show this table (usually not a BBMD, or the feature is off).</summary>
    Refused,
    /// <summary>Nothing came back in time.</summary>
    NoAnswer,
    /// <summary>The request could not be sent from this PC.</summary>
    SendFailed,
}

/// <summary>A table read from one BBMD: what came back, or why nothing did.</summary>
public sealed record TableRead<T>(TableReadStatus Status, IReadOnlyList<T> Entries, string? Error = null)
{
    public bool Ok => Status == TableReadStatus.Ok;
}

public static class TableRead
{
    /// <summary>A read that brought no table back, and why.</summary>
    public static TableRead<T> Failed<T>(TableReadStatus status, string? error = null) => new(status, [], error);
}

/// <summary>
/// The BVLC messages for reading a BBMD's tables (ASHRAE 135 Annex J), encoded and decoded by hand: they are short,
/// and doing it here keeps the parsing testable with plain byte arrays.
/// </summary>
public static class BvlcTables
{
    public const byte BvlcType = 0x81;
    public const byte FnResult = 0x00, FnReadBdt = 0x02, FnReadBdtAck = 0x03, FnReadFdt = 0x06, FnReadFdtAck = 0x07;
    public const ushort ReadBdtNak = 0x0020, ReadFdtNak = 0x0040;
    private const int EntrySize = 10;

    public enum ReplyKind { Other, Bdt, Fdt, BdtNak, FdtNak }

    public static byte[] ReadBdtRequest() => [BvlcType, FnReadBdt, 0x00, 0x04];
    public static byte[] ReadFdtRequest() => [BvlcType, FnReadFdt, 0x00, 0x04];

    /// <summary>What a received datagram is. Anything malformed or unrelated is <see cref="ReplyKind.Other"/>.</summary>
    public static ReplyKind Classify(ReadOnlySpan<byte> msg)
    {
        if (msg.Length < 4 || msg[0] != BvlcType) return ReplyKind.Other;
        var length = BinaryPrimitives.ReadUInt16BigEndian(msg[2..]);
        if (length < 4 || length > msg.Length) return ReplyKind.Other;
        return msg[1] switch
        {
            FnReadBdtAck => ReplyKind.Bdt,
            FnReadFdtAck => ReplyKind.Fdt,
            FnResult when length >= 6 => BinaryPrimitives.ReadUInt16BigEndian(msg[4..]) switch
            {
                ReadBdtNak => ReplyKind.BdtNak,
                ReadFdtNak => ReplyKind.FdtNak,
                _ => ReplyKind.Other,
            },
            _ => ReplyKind.Other,
        };
    }

    /// <summary>The entries of a Read-Broadcast-Distribution-Table-Ack: address, port and mask, 10 bytes each. A stray partial entry is ignored.</summary>
    public static IReadOnlyList<BdtEntry> ParseBdt(ReadOnlySpan<byte> msg) =>
        Entries(msg, e => new BdtEntry(EndPoint(e), new IPAddress(e.Slice(6, 4))));

    /// <summary>The entries of a Read-Foreign-Device-Table-Ack: address, port, time-to-live and seconds remaining.</summary>
    public static IReadOnlyList<FdtEntry> ParseFdt(ReadOnlySpan<byte> msg) =>
        Entries(msg, e => new FdtEntry(EndPoint(e), BinaryPrimitives.ReadUInt16BigEndian(e[6..]), BinaryPrimitives.ReadUInt16BigEndian(e[8..])));

    private delegate T EntryReader<out T>(ReadOnlySpan<byte> entry);

    private static List<T> Entries<T>(ReadOnlySpan<byte> msg, EntryReader<T> read)
    {
        var list = new List<T>();
        if (msg.Length < 4) return list;
        var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(msg[2..]), msg.Length);
        for (var i = 4; i + EntrySize <= length; i += EntrySize) list.Add(read(msg.Slice(i, EntrySize)));
        return list;
    }

    private static IPEndPoint EndPoint(ReadOnlySpan<byte> e) => new(new IPAddress(e[..4]), BinaryPrimitives.ReadUInt16BigEndian(e[4..]));

    public static byte[] EncodeBdtAck(IEnumerable<BdtEntry> entries) => Encode(FnReadBdtAck, entries, (e, span) =>
    {
        Write(e.Bbmd, span);
        e.Mask.GetAddressBytes().CopyTo(span[6..]);
    });

    public static byte[] EncodeFdtAck(IEnumerable<FdtEntry> entries) => Encode(FnReadFdtAck, entries, (e, span) =>
    {
        Write(e.Device, span);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], e.TtlSeconds);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], e.RemainingSeconds);
    });

    public static byte[] EncodeResult(ushort result)
    {
        var frame = new byte[6];
        frame[0] = BvlcType;
        frame[1] = FnResult;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), result);
        return frame;
    }

    private delegate void EntryWriter<in T>(T entry, Span<byte> span);

    private static byte[] Encode<T>(byte function, IEnumerable<T> entries, EntryWriter<T> write)
    {
        var list = entries.ToList();
        var frame = new byte[4 + EntrySize * list.Count];
        frame[0] = BvlcType;
        frame[1] = function;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)frame.Length);
        for (var i = 0; i < list.Count; i++) write(list[i], frame.AsSpan(4 + i * EntrySize, EntrySize));
        return frame;
    }

    private static void Write(IPEndPoint ep, Span<byte> span)
    {
        ep.Address.MapToIPv4().GetAddressBytes().CopyTo(span);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)ep.Port);
    }
}
