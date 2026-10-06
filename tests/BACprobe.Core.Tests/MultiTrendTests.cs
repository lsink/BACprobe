using System.IO.BACnet;
using BACprobe.Core.Trends;

namespace BACprobe.Core.Tests;

public class MultiTrendTests
{
    private static ChartSeries Temp(string name) => new(name, "°F", BacnetObjectTypes.OBJECT_ANALOG_INPUT);

    [Fact]
    public void Same_units_share_an_axis_and_mixed_units_do_not()
    {
        Assert.True(new MultiTrend([Temp("SAT"), Temp("Setpoint")]).SharedAxis);
        Assert.False(new MultiTrend([Temp("SAT"), new ChartSeries("Valve", "%", BacnetObjectTypes.OBJECT_ANALOG_OUTPUT)]).SharedAxis);
    }

    [Fact]
    public void Ranges_leave_room_and_on_off_is_always_zero_to_one()
    {
        var (min, max) = MultiTrend.RangeOf([50, 60]);
        Assert.True(min < 50 && max > 60);
        Assert.Equal((0, 1), MultiTrend.RangeOf([0, 1, 1, 0]));
        var (flatMin, flatMax) = MultiTrend.RangeOf([72, 72]);
        Assert.True(flatMin < 72 && flatMax > 72); // a flat line still gets a band to sit in
        Assert.Equal(0.5, MultiTrend.Normalise(5, 0, 10));
        Assert.Equal(0.5, MultiTrend.Normalise(5, 5, 5));
    }

    [Fact]
    public void Samples_line_up_by_moment_in_the_csv()
    {
        var trend = new MultiTrend([Temp("SAT"), new ChartSeries("Fan", "", BacnetObjectTypes.OBJECT_BINARY_VALUE)]);
        var t = new DateTime(2026, 10, 5, 14, 0, 0);
        trend.Sample(t, ["55.2", "Active"]);
        trend.Sample(t.AddSeconds(2), ["55.6", null]);

        var lines = trend.CsvLines().ToList();
        Assert.Equal("Time (this PC),SAT (°F),Fan", lines[0]);
        Assert.Equal("2026-10-05 14:00:00,55.2,1", lines[1]);
        Assert.StartsWith("2026-10-05 14:00:02,55.6,no answer", lines[2]);
        Assert.Equal(2, trend.Series[0].Trend.Series.Count);
        Assert.Single(trend.Series[1].Trend.Series); // a missing value is a gap, not a zero
    }

    [Fact]
    public void Too_many_lines_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new MultiTrend([.. Enumerable.Range(0, MultiTrend.MaxSeries + 1).Select(i => Temp($"T{i}"))]));
    }
}
