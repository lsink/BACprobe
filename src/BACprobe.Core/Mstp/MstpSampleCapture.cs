namespace BACprobe.Core.Mstp;

/// <summary>
/// A made-up MS/TP capture for trying the monitor without hardware (the MS/TP counterpart of <c>bacprobe simulate</c>).
/// Masters 1, 2, 3 and 7 pass the token round; MAC 7 is dead (never takes it), a little noise is thrown in,
/// and Max Master is too low for MAC 9, which sits on the trunk but is never polled.
/// </summary>
public static class MstpSampleCapture
{
    public static byte[] Build(int laps = 40, int seed = 7)
    {
        var rng = new Random(seed);
        var bytes = new List<byte>();
        byte[] Token(byte from, byte to) => MstpCrc.Encode((byte)MstpFrameType.Token, to, from, []);

        // Polls stop at 8: Max_Master is too low for the master at 9.
        for (byte mac = 4; mac <= 8; mac++)
            bytes.AddRange(MstpCrc.Encode((byte)MstpFrameType.PollForMaster, mac, 1, []));
        bytes.AddRange(MstpCrc.Encode((byte)MstpFrameType.ReplyToPollForMaster, 1, 9, []));

        for (var i = 0; i < laps; i++)
        {
            bytes.AddRange(Token(1, 2));
            bytes.AddRange(MstpCrc.Encode((byte)MstpFrameType.BacnetDataExpectingReply, 3, 2, [0x01, 0x04, 0x00, 0x05, 0x01, 0x0C]));
            bytes.AddRange(MstpCrc.Encode((byte)MstpFrameType.BacnetDataNotExpectingReply, 2, 3, [0x01, 0x00, 0x30, 0x01, 0x3E, 0x44]));
            bytes.AddRange(Token(2, 3));
            bytes.AddRange(Token(3, 7));   // MAC 7 never answers...
            bytes.AddRange(Token(3, 1));   // ...so MAC 3 gives up and passes it on
            if (rng.Next(12) == 0)         // a burst of line noise now and then
                bytes.AddRange(Enumerable.Range(0, rng.Next(2, 6)).Select(_ => (byte)rng.Next(256)));
            if (rng.Next(25) == 0)         // and the odd damaged frame
            {
                var bad = Token(1, 2);
                bad[3] ^= 0x08;
                bytes.AddRange(bad);
            }
        }
        return [.. bytes];
    }
}
