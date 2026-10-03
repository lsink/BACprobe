using BACprobe.Core.Discovery;
using BACprobe.Core.Mstp;

namespace BACprobe.Core.Tests;

public class MstpAdapterTests
{
    private static BaudTrial T(int baud, int good, int bad = 0, long junk = 0) => new(baud, good, bad, junk);

    [Fact]
    public void The_rate_with_clearly_the_most_good_frames_wins()
    {
        var trials = new[] { T(9600, 0, 0, 900), T(19200, 0, 1, 700), T(38400, 120, 1), T(76800, 2, 4, 500), T(115200, 0, 0, 300) };
        Assert.Equal(38400, MstpBaudDetector.Pick(trials));
    }

    [Fact]
    public void Silence_gives_no_answer_and_says_to_check_wiring()
    {
        var trials = MstpBaudDetector.Candidates.Select(b => T(b, 0)).ToList();
        Assert.Null(MstpBaudDetector.Pick(trials));
        Assert.Contains("wiring", MstpBaudDetector.Explain(trials, null));
    }

    [Fact]
    public void Two_rates_both_decoding_is_not_guessed()
    {
        var trials = new[] { T(38400, 60), T(76800, 50) };
        Assert.Null(MstpBaudDetector.Pick(trials));
        Assert.Contains("different rates", MstpBaudDetector.Explain(trials, null));
    }

    [Fact]
    public void Noisy_rates_with_more_errors_than_frames_do_not_win()
    {
        Assert.Null(MstpBaudDetector.Pick([T(38400, 5, 20)]));
    }

    [Fact]
    public void Junk_only_points_at_polarity()
    {
        var trials = new[] { T(38400, 0, 0, 500) };
        Assert.Contains("swap", MstpBaudDetector.Explain(trials, null), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(3, true)]
    [InlineData(2, false)]
    [InlineData(1, false)]
    public void Latency_over_2_ms_is_too_high(int ms, bool tooHigh)
    {
        var l = new FtdiLatency("COM5", ms);
        Assert.Equal(tooHigh, l.IsTooHigh);
        if (tooHigh) Assert.Contains("Latency Timer to 1 ms", l.Verdict);
    }

    [Fact]
    public void A_slow_trunk_gets_an_info_note_not_a_warning()
    {
        var a = new MstpBusAnalyzer(9600, 1000);
        a.AddFrame(new MstpFrame(0, 0, 2, 1, [], 8));
        var f = Assert.Single(a.Findings(), x => x.Title.Contains("slow"));
        Assert.Equal(FindingSeverity.Info, f.Severity);
    }
}
