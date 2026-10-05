namespace BACprobe.Core.Mstp;

/// <summary>MS/TP frame types (ASHRAE 135 clause 9.3). 8-127 are reserved, 128-255 are vendor-defined.</summary>
public enum MstpFrameType : byte
{
    Token = 0,
    PollForMaster = 1,
    ReplyToPollForMaster = 2,
    TestRequest = 3,
    TestResponse = 4,
    BacnetDataExpectingReply = 5,
    BacnetDataNotExpectingReply = 6,
    ReplyPostponed = 7,
}

/// <summary>One valid MS/TP frame as seen on the wire.</summary>
/// <param name="Timestamp">When the last byte of the frame arrived (stopwatch ticks, see <see cref="System.Diagnostics.Stopwatch"/>).</param>
/// <param name="WireBytes">Preamble, header, data and CRCs: what the frame occupied on the bus.</param>
public sealed record MstpFrame(long Timestamp, byte Type, byte Destination, byte Source, byte[] Data, int WireBytes)
{
    public const byte Broadcast = 255;

    public bool IsKnownType => Type <= (byte)MstpFrameType.ReplyPostponed;
    public bool IsVendorType => Type >= 128;
    public bool IsTokenOrPoll => Type is (byte)MstpFrameType.Token or (byte)MstpFrameType.PollForMaster;

    /// <summary>The frame type in words, for a tech: "Token", "Poll For Master", "BACnet data (expects reply)".</summary>
    public string TypeName => Type switch
    {
        (byte)MstpFrameType.Token => "Token",
        (byte)MstpFrameType.PollForMaster => "Poll For Master",
        (byte)MstpFrameType.ReplyToPollForMaster => "Reply To Poll For Master",
        (byte)MstpFrameType.TestRequest => "Test Request",
        (byte)MstpFrameType.TestResponse => "Test Response",
        (byte)MstpFrameType.BacnetDataExpectingReply => "BACnet data (expects reply)",
        (byte)MstpFrameType.BacnetDataNotExpectingReply => "BACnet data",
        (byte)MstpFrameType.ReplyPostponed => "Reply Postponed",
        >= 128 => $"Vendor frame {Type}",
        _ => $"Reserved type {Type}",
    };

    /// <summary>One line of plain English: "MAC 3 passes the token to MAC 4".</summary>
    public string Meaning
    {
        get
        {
            var to = Destination == Broadcast ? "everyone" : $"MAC {Destination}";
            return Type switch
            {
                (byte)MstpFrameType.Token => $"MAC {Source} passes the token to {to}",
                (byte)MstpFrameType.PollForMaster => $"MAC {Source} asks whether {to} is a master that wants the token",
                (byte)MstpFrameType.ReplyToPollForMaster => $"MAC {Source} answers {to}: yes, I am a master",
                (byte)MstpFrameType.TestRequest => $"MAC {Source} sends a link test to {to}",
                (byte)MstpFrameType.TestResponse => $"MAC {Source} answers the link test from {to}",
                (byte)MstpFrameType.BacnetDataExpectingReply => $"MAC {Source} sends {Data.Length} bytes of BACnet to {to} and wants an answer",
                (byte)MstpFrameType.BacnetDataNotExpectingReply => $"MAC {Source} sends {Data.Length} bytes of BACnet to {to}",
                (byte)MstpFrameType.ReplyPostponed => $"MAC {Source} tells {to}: I need longer to answer",
                _ => $"MAC {Source} sends a {TypeName} to {to}",
            };
        }
    }
}

public enum MstpErrorKind
{
    /// <summary>The header looked like a frame but its CRC did not match: noise, bad termination, wrong baud rate, or a collision.</summary>
    HeaderCrc,
    /// <summary>The header was fine but the data CRC did not match.</summary>
    DataCrc,
    /// <summary>The header claimed more data than any MS/TP frame carries.</summary>
    BadLength,
}

public sealed record MstpError(long Timestamp, MstpErrorKind Kind);

/// <summary>The two checksums MS/TP uses (ASHRAE 135 Annex G). Pure, so they can be tested against the spec's own check values.</summary>
public static class MstpCrc
{
    public const byte HeaderResidual = 0x55;
    public const ushort DataResidual = 0xF0B8;

    public static byte Header(byte crc, byte data)
    {
        int c = crc ^ data;
        c = c ^ (c << 1) ^ (c << 2) ^ (c << 3) ^ (c << 4) ^ (c << 5) ^ (c << 6) ^ (c << 7);
        return (byte)((c & 0xFE) ^ ((c >> 8) & 1));
    }

    public static ushort Data(ushort crc, byte data)
    {
        var low = (crc & 0xFF) ^ data;
        return (ushort)((crc >> 8) ^ (low << 8) ^ (low << 3) ^ (low << 12) ^ (low >> 4) ^ (low & 0x0F) ^ ((low & 0x0F) << 7));
    }

    /// <summary>The CRC byte to send after the five header bytes (type, destination, source, length hi, length lo).</summary>
    public static byte HeaderCrc(ReadOnlySpan<byte> header)
    {
        byte crc = 0xFF;
        foreach (var b in header) crc = Header(crc, b);
        return (byte)~crc;
    }

    /// <summary>The two CRC bytes to send after the data: low byte first.</summary>
    public static (byte Low, byte High) DataCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data) crc = Data(crc, b);
        crc = (ushort)~crc;
        return ((byte)(crc & 0xFF), (byte)(crc >> 8));
    }

    /// <summary>Build a complete frame (preamble to last CRC byte). Used by tests and the recorder's self-checks.</summary>
    public static byte[] Encode(byte type, byte destination, byte source, ReadOnlySpan<byte> data)
    {
        var header = new byte[] { type, destination, source, (byte)(data.Length >> 8), (byte)(data.Length & 0xFF) };
        var frame = new List<byte> { 0x55, 0xFF };
        frame.AddRange(header);
        frame.Add(HeaderCrc(header));
        if (data.Length > 0)
        {
            frame.AddRange(data.ToArray());
            var (lo, hi) = DataCrc(data);
            frame.Add(lo);
            frame.Add(hi);
        }
        return [.. frame];
    }
}

/// <summary>
/// Turns a stream of bytes from a passive listener into frames and errors. Feed it whatever the serial port returns;
/// it hunts for the 0x55 0xFF preamble, checks both CRCs, and after a bad frame rescans from the next byte so one
/// burst of noise costs one frame, not the rest of the capture.
/// </summary>
public sealed class MstpFrameParser
{
    /// <summary>The longest data field a frame may carry (clause 9.3: 501 bytes for MS/TP; some devices send up to 1497).</summary>
    public const int MaxData = 1497;

    private const int HeaderLength = 8; // 55 FF type dst src lenHi lenLo crc
    // One reusable buffer holding the bytes not yet consumed: no per-read copy, and the leftover is moved down once per Feed.
    private byte[] _buf = new byte[4096];
    private int _count;
    private readonly byte[] _header = new byte[5];

    /// <summary>Bytes thrown away while hunting for a preamble: line noise or a sender that is not MS/TP at this baud rate.</summary>
    public long DiscardedBytes { get; private set; }

    /// <summary>Add bytes and return the frames and errors they completed, in order.</summary>
    public IReadOnlyList<object> Feed(ReadOnlySpan<byte> bytes, long timestamp)
    {
        if (_count + bytes.Length > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _count + bytes.Length));
        bytes.CopyTo(_buf.AsSpan(_count));
        _count += bytes.Length;

        var found = new List<object>();
        var pos = 0;

        while (true)
        {
            // Find the preamble.
            var window = _buf.AsSpan(pos, _count - pos);
            var at = -1;
            for (var i = 0; i + 1 < window.Length; i++)
                if (window[i] == 0x55 && window[i + 1] == 0xFF) { at = i; break; }

            if (at < 0)
            {
                // Keep a trailing 0x55: its 0xFF may be in the next read.
                var keep = window.Length > 0 && window[^1] == 0x55 ? 1 : 0;
                DiscardedBytes += window.Length - keep;
                pos = _count - keep;
                break;
            }
            DiscardedBytes += at;
            pos += at;

            if (_count - pos < HeaderLength) break; // wait for the rest of the header

            Array.Copy(_buf, pos + 2, _header, 0, 5);
            if (MstpCrc.HeaderCrc(_header) != _buf[pos + 7])
            {
                found.Add(new MstpError(timestamp, MstpErrorKind.HeaderCrc));
                pos++; // rescan from the next byte
                continue;
            }

            var length = (_header[3] << 8) | _header[4];
            if (length > MaxData)
            {
                found.Add(new MstpError(timestamp, MstpErrorKind.BadLength));
                pos++;
                continue;
            }

            var total = HeaderLength + (length == 0 ? 0 : length + 2);
            if (_count - pos < total) break; // wait for the data

            var data = length == 0 ? [] : _buf.AsSpan(pos + HeaderLength, length).ToArray();
            if (length > 0)
            {
                var (lo, hi) = MstpCrc.DataCrc(data);
                if (lo != _buf[pos + HeaderLength + length] || hi != _buf[pos + HeaderLength + length + 1])
                {
                    found.Add(new MstpError(timestamp, MstpErrorKind.DataCrc));
                    pos += total; // the header CRC vouches for the length, so skip the whole damaged frame
                    continue;
                }
            }

            found.Add(new MstpFrame(timestamp, _header[0], _header[1], _header[2], data, total));
            pos += total;
        }

        if (pos > 0)
        {
            Buffer.BlockCopy(_buf, pos, _buf, 0, _count - pos);
            _count -= pos;
        }
        return found;
    }
}
