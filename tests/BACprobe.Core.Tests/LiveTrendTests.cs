using System.IO.BACnet;
using BACprobe.Core.Trends;

namespace BACprobe.Core.Tests;

public class LiveTrendTests
{
    private static readonly DateTime T0 = new(2026, 10, 2, 22, 0, 0);

    [Theory]
    [InlineData(BacnetObjectTypes.OBJECT_ANALOG_INPUT, "72.4", 72.4)]
    [InlineData(BacnetObjectTypes.OBJECT_ANALOG_INPUT, "-3", -3.0)]
    [InlineData(BacnetObjectTypes.OBJECT_BINARY_INPUT, "Active", 1.0)]
    [InlineData(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, "inactive", 0.0)]
    [InlineData(BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE, "3", 3.0)]
    public void Present_values_become_chartable_numbers(BacnetObjectTypes type, string pv, double expected) =>
        Assert.Equal(expected, LiveTrend.ToNumber(type, pv)!.Value, 6);

    [Theory]
    [InlineData(BacnetObjectTypes.OBJECT_ANALOG_INPUT, "")]
    [InlineData(BacnetObjectTypes.OBJECT_ANALOG_INPUT, "NaN")]
    [InlineData(BacnetObjectTypes.OBJECT_BINARY_INPUT, "2")]
    [InlineData(BacnetObjectTypes.OBJECT_ANALOG_INPUT, null)]
    public void Values_that_cannot_be_charted_give_no_number(BacnetObjectTypes type, string? pv) =>
        Assert.Null(LiveTrend.ToNumber(type, pv));

    [Fact]
    public void Samples_are_kept_in_order_with_state_names_and_numbers()
    {
        var t = new LiveTrend(BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE, ["Occupied", "Unoccupied", "Standby"]);
        t.AddValue(T0, "1");
        t.AddValue(T0.AddSeconds(2), "3");
        Assert.Equal(["Occupied (1)", "Standby (3)"], t.Records.Select(r => r.Text));
        Assert.Equal([(T0, 1.0), (T0.AddSeconds(2), 3.0)], t.Series);
    }

    [Fact]
    public void A_missed_reading_is_recorded_but_left_out_of_the_chart()
    {
        var t = new LiveTrend(BacnetObjectTypes.OBJECT_ANALOG_INPUT);
        t.AddValue(T0, "70");
        var gap = t.AddNoAnswer(T0.AddSeconds(2), "timeout");
        t.AddValue(T0.AddSeconds(4), "71");
        Assert.Equal(3, t.Count);
        Assert.Equal(TrendRecordKind.Error, gap.Kind);
        Assert.Contains("timeout", gap.Text);
        Assert.Equal(2, t.Series.Count);
        Assert.Equal(2, TrendStats.Summarize(t.Records)!.Count);
    }

    [Fact]
    public void Past_the_limit_the_oldest_samples_are_dropped_and_counted()
    {
        var t = new LiveTrend(BacnetObjectTypes.OBJECT_ANALOG_INPUT, maxSamples: 3);
        for (var i = 0; i < 5; i++) t.AddValue(T0.AddSeconds(i), i.ToString());
        Assert.Equal(3, t.Count);
        Assert.Equal(2, t.Dropped);
        Assert.Equal(2.0, t.Series[0].Value); // samples 0 and 1 went first
    }

    [Fact]
    public void Clear_starts_again()
    {
        var t = new LiveTrend(BacnetObjectTypes.OBJECT_ANALOG_INPUT, maxSamples: 1);
        t.AddValue(T0, "1");
        t.AddValue(T0, "2");
        t.Clear();
        Assert.Equal(0, t.Count);
        Assert.Equal(0, t.Dropped);
        Assert.Empty(t.Series);
    }

    [Fact]
    public void A_live_trend_export_says_its_times_are_this_pcs_clock()
    {
        var t = new LiveTrend(BacnetObjectTypes.OBJECT_ANALOG_INPUT);
        t.AddValue(T0, "70");
        Assert.StartsWith("Time (this PC)", TrendExporter.CsvLines(t.Records, "°F", pcClock: true).First());
        Assert.StartsWith("Time (device clock)", TrendExporter.CsvLines(t.Records, "°F").First());
    }
}
