using BACprobe.Core.Discovery;
using BACprobe.Core.Mstp;

namespace BACprobe.Core.Tests;

public class MstpTests
{
    private const long Tps = 1000; // test clock: 1 tick = 1 ms

    private static byte[] Token(byte from, byte to) => MstpCrc.Encode((byte)MstpFrameType.Token, to, from, []);
    private static byte[] Poll(byte from, byte to) => MstpCrc.Encode((byte)MstpFrameType.PollForMaster, to, from, []);
    private static byte[] Data(byte from, byte to, params byte[] data) =>
        MstpCrc.Encode((byte)MstpFrameType.BacnetDataNotExpectingReply, to, from, data);

    private static List<object> Parse(byte[] bytes, long ts = 0)
    {
        var p = new MstpFrameParser();
        return [.. p.Feed(bytes, ts)];
    }

    [Fact]
    public void Header_crc_of_a_good_header_ends_on_the_spec_residual()
    {
        byte[] header = [0x00, 0x04, 0x03, 0x00, 0x00];
        var crc = MstpCrc.HeaderCrc(header);
        byte run = 0xFF;
        foreach (var b in header) run = MstpCrc.Header(run, b);
        run = MstpCrc.Header(run, crc);
        Assert.Equal(MstpCrc.HeaderResidual, run);
    }

    [Fact]
    public void Data_crc_of_good_data_ends_on_the_spec_residual()
    {
        byte[] data = [0x01, 0x02, 0x03, 0xFE, 0x80];
        var (lo, hi) = MstpCrc.DataCrc(data);
        ushort run = 0xFFFF;
        foreach (var b in data) run = MstpCrc.Data(run, b);
        run = MstpCrc.Data(run, lo);
        run = MstpCrc.Data(run, hi);
        Assert.Equal(MstpCrc.DataResidual, run);
    }

    [Fact]
    public void Token_frame_round_trips()
    {
        var found = Parse(Token(3, 4));
        var f = Assert.IsType<MstpFrame>(Assert.Single(found));
        Assert.Equal((byte)MstpFrameType.Token, f.Type);
        Assert.Equal(3, f.Source);
        Assert.Equal(4, f.Destination);
        Assert.Equal(8, f.WireBytes);
        Assert.Equal("MAC 3 passes the token to MAC 4", f.Meaning);
    }

    [Fact]
    public void Data_frame_round_trips_with_its_payload()
    {
        var f = Assert.IsType<MstpFrame>(Assert.Single(Parse(Data(5, 9, 1, 2, 3, 4))));
        Assert.Equal([1, 2, 3, 4], f.Data);
        Assert.Equal(8 + 4 + 2, f.WireBytes);
    }

    [Fact]
    public void Frames_split_across_reads_are_still_found()
    {
        var bytes = Data(5, 9, 10, 20, 30).Concat(Token(1, 2)).ToArray();
        var p = new MstpFrameParser();
        var found = new List<object>();
        foreach (var b in bytes) found.AddRange(p.Feed([b], 0)); // one byte at a time
        Assert.Equal(2, found.OfType<MstpFrame>().Count());
        Assert.Empty(found.OfType<MstpError>());
    }

    [Fact]
    public void Noise_before_a_frame_is_discarded_without_losing_the_frame()
    {
        var bytes = new byte[] { 0x12, 0x55, 0x34, 0xAA }.Concat(Token(1, 2)).ToArray();
        var p = new MstpFrameParser();
        var found = p.Feed(bytes, 0);
        Assert.Single(found.OfType<MstpFrame>());
        Assert.True(p.DiscardedBytes >= 4);
    }

    [Fact]
    public void A_corrupted_header_is_counted_and_the_next_frame_is_still_read()
    {
        var bad = Token(1, 2);
        bad[3] ^= 0x10; // damage the destination
        var found = Parse(bad.Concat(Token(2, 3)).ToArray());
        Assert.Contains(found.OfType<MstpError>(), e => e.Kind == MstpErrorKind.HeaderCrc);
        var good = Assert.Single(found.OfType<MstpFrame>());
        Assert.Equal(2, good.Source);
    }

    [Fact]
    public void A_corrupted_data_field_is_reported_as_a_data_crc_error_once()
    {
        var bad = Data(1, 2, 9, 8, 7);
        bad[8] ^= 0xFF; // damage the first data byte
        var found = Parse(bad.Concat(Token(2, 3)).ToArray());
        var err = Assert.Single(found.OfType<MstpError>());
        Assert.Equal(MstpErrorKind.DataCrc, err.Kind);
        Assert.Single(found.OfType<MstpFrame>());
    }

    [Fact]
    public void An_impossible_length_is_flagged()
    {
        var header = new byte[] { 6, 1, 2, 0xFF, 0xFF };
        var bytes = new byte[] { 0x55, 0xFF }.Concat(header).Append(MstpCrc.HeaderCrc(header)).ToArray();
        var found = Parse(bytes);
        Assert.Contains(found.OfType<MstpError>(), e => e.Kind == MstpErrorKind.BadLength);
    }

    /// <summary>Play a list of frames onto the analyzer, 10 ms apart.</summary>
    private static MstpBusAnalyzer Run(int baud, IEnumerable<byte[]> frames, long stepMs = 10, IEnumerable<MstpError>? errors = null)
    {
        var a = new MstpBusAnalyzer(baud, Tps);
        var p = new MstpFrameParser();
        long t = 0;
        foreach (var f in frames)
        {
            foreach (var item in p.Feed(f, t)) a.Add(item);
            t += stepMs;
        }
        foreach (var e in errors ?? []) a.AddError(e);
        return a;
    }

    private static IEnumerable<byte[]> Ring(int laps, params byte[] macs)
    {
        for (var i = 0; i < laps; i++)
            for (var j = 0; j < macs.Length; j++)
                yield return Token(macs[j], macs[(j + 1) % macs.Length]);
    }

    [Fact]
    public void A_healthy_ring_has_no_findings_and_a_token_loop_time()
    {
        var a = Run(38400, Ring(6, 1, 2, 3));
        Assert.Equal(3, a.Masters.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(30), a.AverageTokenLoop);
        Assert.DoesNotContain(a.Findings(), f => f.Severity != FindingSeverity.Info);
        Assert.All(a.Nodes, n => Assert.Equal(0, n.TokenNotTaken));
    }

    [Fact]
    public void A_node_that_never_takes_the_token_is_a_problem()
    {
        // 1 passes to 5, 5 stays silent, 1 tries again; repeated.
        var frames = new List<byte[]>();
        for (var i = 0; i < 4; i++) { frames.Add(Token(1, 5)); frames.Add(Token(1, 2)); frames.Add(Token(2, 1)); }
        var a = Run(38400, frames);
        var f = Assert.Single(a.Findings(), x => x.Title.Contains("MAC 5 never takes"));
        Assert.Equal(FindingSeverity.Problem, f.Severity);
        Assert.NotEmpty(f.LikelyCause);
        Assert.NotEmpty(f.NextStep);
    }

    [Fact]
    public void A_master_above_the_highest_poll_means_max_master_is_too_low()
    {
        var frames = new List<byte[]> { Poll(1, 2), Poll(1, 3), Poll(1, 4) };
        frames.AddRange(Ring(3, 1, 9));
        var a = Run(38400, frames);
        Assert.Equal(4, a.HighestPolled);
        Assert.Contains(a.Findings(), f => f.Title.Contains("Max Master"));
    }

    [Fact]
    public void Many_damaged_frames_are_a_problem()
    {
        var frames = Ring(5, 1, 2).ToList(); // 10 good frames
        var errors = Enumerable.Range(0, 3).Select(i => new MstpError(100 + i, MstpErrorKind.HeaderCrc));
        var a = Run(38400, frames, errors: errors);
        var f = Assert.Single(a.Findings(), x => x.Title.Contains("damaged frames"));
        Assert.Equal(FindingSeverity.Problem, f.Severity); // 3 of 13 is about 23 percent
    }

    [Fact]
    public void Utilisation_counts_ten_bits_per_wire_byte()
    {
        // 100 token frames of 8 bytes in 1 s at 9600 baud: 100*8*10/9600 = 83 percent.
        var a = Run(9600, Ring(50, 1, 2), stepMs: 10);
        Assert.InRange(a.Utilisation, 0.8, 0.9);
        Assert.Contains(a.Findings(), f => f.Title.Contains("busy"));
    }

    [Fact]
    public void Sample_capture_shows_the_dead_node_and_the_low_max_master_but_not_a_made_up_load()
    {
        var parser = new MstpFrameParser();
        var a = new MstpBusAnalyzer(38400, Tps) { TimingKnown = false };
        long t = 0;
        foreach (var item in parser.Feed(MstpSampleCapture.Build(), t++)) { a.Add(item); t += 5; }
        a.DiscardedBytes = parser.DiscardedBytes;

        var titles = a.Findings().Select(f => f.Title).ToList();
        Assert.Contains(titles, x => x.Contains("MAC 7 never takes"));
        Assert.Contains(titles, x => x.Contains("Max Master"));
        Assert.DoesNotContain(titles, x => x.Contains("busy"));
    }

    [Fact]
    public void Silence_says_what_to_check()
    {
        var f = Assert.Single(new MstpBusAnalyzer(38400, Tps).Findings());
        Assert.Equal(FindingSeverity.Problem, f.Severity);
        Assert.Contains("COM port", f.NextStep);
    }

    [Fact]
    public void Bytes_but_no_frames_points_at_baud_rate_and_polarity()
    {
        var a = new MstpBusAnalyzer(9600, Tps) { DiscardedBytes = 500 };
        var f = Assert.Single(a.Findings());
        Assert.Contains("baud", f.NextStep);
        Assert.Contains("swap A and B", f.NextStep);
    }
}
