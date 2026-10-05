using BACprobe.Core.Discovery;
using BACprobe.Core.Mstp;

namespace BACprobe.Core.Tests;

public class MstpHealthTests
{
    private const long Tps = 1000; // 1 tick = 1 ms

    private static MstpFrame Token(long ts, byte from, byte to) => new(ts, (byte)MstpFrameType.Token, to, from, [], 8);

    /// <summary>A ring of 1 -> 2 -> 1 -> 2 ... where MAC 2 starts using the token <paramref name="pickupMs"/> after being passed it.</summary>
    private static MstpBusAnalyzer SlowRing(long pickupMs, int laps = 8)
    {
        var a = new MstpBusAnalyzer(38400, Tps);
        long t = 0;
        for (var i = 0; i < laps; i++)
        {
            a.AddFrame(Token(t, 1, 2));
            t += pickupMs;
            a.AddFrame(Token(t, 2, 1));
            t += 2;
        }
        return a;
    }

    [Fact]
    public void A_node_that_waits_most_of_the_timeout_is_called_slow()
    {
        var f = Assert.Single(SlowRing(18).Findings(), x => x.Title.Contains("slow to use the token"));
        Assert.Contains("MAC 2", f.Title);
        Assert.Contains("20 ms", f.Detail);
        Assert.NotEmpty(f.NextStep);
    }

    [Fact]
    public void A_quick_node_is_not_flagged()
        => Assert.DoesNotContain(SlowRing(3).Findings(), f => f.Title.Contains("slow to use"));

    [Fact]
    public void Pickup_time_is_recorded_per_node()
    {
        var n = SlowRing(12).Nodes.Single(x => x.Mac == 2);
        Assert.Equal(8, n.PickupSamples);
        Assert.Equal(12, n.PickupTicksMax);
    }

    [Fact]
    public void A_replay_does_not_invent_pickup_findings()
    {
        var a = SlowRing(18);
        a.TimingKnown = false;
        Assert.DoesNotContain(a.Findings(), f => f.Title.Contains("slow to use"));
    }

    [Fact]
    public void Damage_that_follows_one_nodes_turn_points_at_that_node()
    {
        var a = new MstpBusAnalyzer(38400, Tps);
        long t = 0;
        for (var i = 0; i < 10; i++)
        {
            a.AddFrame(Token(t++, 1, 5));
            if (i % 2 == 0) a.AddError(new MstpError(t++, MstpErrorKind.HeaderCrc)); // MAC 5's answer is damaged half the time
            a.AddFrame(Token(t++, 5, 1));
            a.AddFrame(Token(t++, 1, 2));
            a.AddFrame(Token(t++, 2, 1));
        }
        var f = Assert.Single(a.Findings(), x => x.Title.Contains("MAC 5's transmissions arrive damaged"));
        Assert.Equal(FindingSeverity.Problem, f.Severity); // 5 of 10 turns
        Assert.Contains("same MAC", f.LikelyCause);
        Assert.Contains("Unplug MAC 5", f.NextStep);
    }

    [Fact]
    public void Damage_spread_over_the_whole_trunk_is_not_pinned_on_a_node()
    {
        var a = new MstpBusAnalyzer(38400, Tps);
        long t = 0;
        for (var i = 0; i < 60; i++)
        {
            a.AddFrame(Token(t++, 1, (byte)(2 + i % 6)));
            if (i % 13 == 0) a.AddError(new MstpError(t++, MstpErrorKind.HeaderCrc)); // 5 errors landing on 5 different nodes
        }
        Assert.DoesNotContain(a.Findings(), f => f.Title.Contains("transmissions arrive damaged"));
    }

    [Fact]
    public void One_burst_of_noise_counts_once()
    {
        var a = new MstpBusAnalyzer(38400, Tps);
        a.AddFrame(Token(0, 1, 5));
        for (var i = 0; i < 4; i++) a.AddError(new MstpError(1 + i, MstpErrorKind.HeaderCrc));
        Assert.Equal(1, a.Nodes.Single(n => n.Mac == 5).TurnErrors);
    }

    [Fact]
    public void The_sample_capture_shows_the_damaged_node()
    {
        var m = MstpMonitor.FromRecording(MstpSampleCapture.Build(), 38400);
        var s = m.Snapshot();
        Assert.Contains(s.Findings, f => f.Title.Contains("MAC 5's transmissions arrive damaged"));
        Assert.True(s.Nodes.Single(n => n.Mac == 5).BadTurns >= 20);
        Assert.Contains(s.Findings, f => f.Title.Contains("MAC 7 never takes"));
    }

    [Fact]
    public void Pcap_has_the_wireshark_header_and_one_record_per_frame_with_exact_wire_bytes()
    {
        var frames = new List<(double, MstpFrame)>
        {
            (0.0, new MstpFrame(0, (byte)MstpFrameType.Token, 2, 1, [], 8)),
            (0.5, new MstpFrame(0, (byte)MstpFrameType.BacnetDataNotExpectingReply, 3, 2, [1, 2, 3], 13)),
        };
        using var ms = new MemoryStream();
        MstpPcap.Write(ms, frames, new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var b = ms.ToArray();

        Assert.Equal(0xA1B2C3D4u, BitConverter.ToUInt32(b, 0));
        Assert.Equal(165u, BitConverter.ToUInt32(b, 20)); // LINKTYPE_BACNET_MS_TP

        var first = MstpCrc.Encode((byte)MstpFrameType.Token, 2, 1, []);
        var second = MstpCrc.Encode((byte)MstpFrameType.BacnetDataNotExpectingReply, 3, 2, [1, 2, 3]);
        Assert.Equal(24 + 16 + first.Length + 16 + second.Length, b.Length);
        Assert.Equal((uint)first.Length, BitConverter.ToUInt32(b, 24 + 8));
        Assert.Equal(first, b[(24 + 16)..(24 + 16 + first.Length)]);
        var secondHeader = 24 + 16 + first.Length;
        Assert.Equal(0.5, (BitConverter.ToUInt32(b, secondHeader) - BitConverter.ToUInt32(b, 24)) + BitConverter.ToUInt32(b, secondHeader + 4) / 1e6, 3);
        Assert.Equal(second, b[(secondHeader + 16)..]);
    }

    [Fact]
    public void Monitor_exports_what_it_heard_and_the_file_parses_back()
    {
        var m = MstpMonitor.FromRecording(MstpSampleCapture.Build(40), 38400);
        var path = Path.Combine(Path.GetTempPath(), $"bacprobe-{Guid.NewGuid():N}.pcap");
        try
        {
            var n = m.ExportPcap(path);
            Assert.Equal(m.ExportableFrames, n);
            Assert.True(n > 100);
            // Every packet in the file must decode as a valid MS/TP frame again.
            var bytes = File.ReadAllBytes(path);
            var parser = new MstpFrameParser();
            var pos = 24;
            var good = 0;
            while (pos < bytes.Length)
            {
                var len = (int)BitConverter.ToUInt32(bytes, pos + 8);
                good += parser.Feed(bytes.AsSpan(pos + 16, len), 0).OfType<MstpFrame>().Count();
                pos += 16 + len;
            }
            Assert.Equal(n, good);
        }
        finally { File.Delete(path); }
    }
}
