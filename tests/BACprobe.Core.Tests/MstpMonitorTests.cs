using BACprobe.Core.Mstp;

namespace BACprobe.Core.Tests;

public class MstpMonitorTests
{
    [Fact]
    public void A_recording_gives_nodes_findings_and_a_frame_log_without_made_up_timing()
    {
        var m = MstpMonitor.FromRecording(MstpSampleCapture.Build(), 38400);
        var s = m.Snapshot();
        Assert.False(s.TimingKnown);
        Assert.Contains(s.Nodes, n => n.Mac == 7 && n.TokenNotTaken > 0);
        Assert.Contains(s.Findings, f => f.Title.Contains("MAC 7"));
        Assert.DoesNotContain(s.Findings, f => f.Title.Contains("busy"));
        Assert.Contains("no clock", s.Summary);
        Assert.NotEmpty(m.Log());
    }

    [Fact]
    public void The_log_can_be_filtered_by_mac_and_hides_token_noise_on_request()
    {
        var m = MstpMonitor.FromRecording(MstpSampleCapture.Build(), 38400);
        Assert.All(m.Log(mac: 3), l => Assert.True(l.Source == 3 || l.Destination == 3));
        var dataOnly = m.Log(tokenAndPollsToo: false);
        Assert.DoesNotContain(dataOnly, l => l.Type == (byte)MstpFrameType.Token);
        Assert.Contains(dataOnly, l => l.Type == (byte)MstpFrameType.BacnetDataExpectingReply);
    }

    [Fact]
    public void The_log_keeps_only_the_most_recent_lines()
    {
        var m = new MstpMonitor(38400, keepFrames: 10);
        for (var i = 0; i < 50; i++) m.Feed(MstpCrc.Encode((byte)MstpFrameType.Token, 2, 1, []), i);
        var log = m.Log(max: 100);
        Assert.Equal(10, log.Count);
        Assert.Equal(50, log[^1].Sequence);
    }

    [Fact]
    public void Silence_is_reported_as_listening()
        => Assert.Contains("nothing heard", new MstpMonitor(38400).Snapshot().Summary);
}
