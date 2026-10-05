namespace BACprobe.Core.Mstp;

/// <summary>
/// Writes MS/TP frames as a classic pcap file Wireshark can open (link type 165, BACNET_MS_TP). Each packet is the whole
/// frame as it was on the wire: 0x55 0xFF preamble, header, header CRC, data and data CRC. The wire bytes are rebuilt from the
/// decoded frame, which is exact because only frames whose CRCs checked out are kept.
/// </summary>
public static class MstpPcap
{
    public const uint LinkTypeBacnetMstp = 165;

    public static void Write(Stream output, IEnumerable<(double Seconds, MstpFrame Frame)> frames, DateTimeOffset start)
    {
        using var w = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write(0xA1B2C3D4u);   // magic: microsecond timestamps, written in this machine's (little-endian) byte order
        w.Write((ushort)2);     // version 2.4
        w.Write((ushort)4);
        w.Write(0);             // time zone correction
        w.Write(0u);            // timestamp accuracy
        w.Write(65535u);        // snapshot length
        w.Write(LinkTypeBacnetMstp);

        var startMicros = start.ToUnixTimeMilliseconds() * 1000L;
        foreach (var (seconds, frame) in frames)
        {
            var bytes = MstpCrc.Encode(frame.Type, frame.Destination, frame.Source, frame.Data);
            var micros = startMicros + (long)(seconds * 1_000_000);
            w.Write((uint)(micros / 1_000_000));
            w.Write((uint)(micros % 1_000_000));
            w.Write((uint)bytes.Length);
            w.Write((uint)bytes.Length);
            w.Write(bytes);
        }
    }
}
