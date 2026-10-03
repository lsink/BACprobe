using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using BACprobe.Core.Trends;

namespace BACprobe.Core.Tests;

public class TrendTests
{
    private static readonly DateTime T0 = new(2026, 10, 2, 8, 0, 0);

    /// <summary>Encode with the library exactly as a device would, so the decoder is tested against real wire bytes.</summary>
    private static byte[] Encode(params BacnetLogRecord[] records)
    {
        var buffer = new EncodeBuffer();
        foreach (var r in records) Services.EncodeLogRecord(buffer, r);
        return buffer.buffer.AsSpan(0, buffer.offset).ToArray();
    }

    private static BacnetLogRecord Real(int minute, float v, BacnetStatusFlags flags = 0) =>
        new(BacnetTrendLogValueType.TL_TYPE_REAL, v, T0.AddMinutes(minute), flags);

    // --- decoding ---

    [Fact]
    public void Real_records_round_trip_with_their_timestamps()
    {
        var records = TrendRecordDecoder.Decode(Encode(Real(0, 70.5f), Real(1, 70.75f), Real(2, 71f)));
        Assert.Equal(3, records.Count);
        Assert.Equal(T0, records[0].Time);
        Assert.Equal(T0.AddMinutes(2), records[2].Time);
        Assert.Equal([70.5, 70.75, 71.0], records.Select(r => r.Number!.Value));
        Assert.Equal("70.75", records[1].Text);
        Assert.All(records, r => Assert.True(r.IsValue));
    }

    [Fact]
    public void Binary_records_read_active_inactive_and_plot_as_one_and_zero()
    {
        var records = TrendRecordDecoder.Decode(Encode(
            new BacnetLogRecord(BacnetTrendLogValueType.TL_TYPE_BOOL, true, T0, 0),
            new BacnetLogRecord(BacnetTrendLogValueType.TL_TYPE_BOOL, false, T0.AddMinutes(1), 0)));
        Assert.Equal(["Active", "Inactive"], records.Select(r => r.Text));
        Assert.Equal([1.0, 0.0], records.Select(r => r.Number!.Value));
    }

    [Fact]
    public void Status_flags_are_shown_in_plain_words()
    {
        var records = TrendRecordDecoder.Decode(Encode(Real(0, 1f, BacnetStatusFlags.STATUS_FLAG_FAULT | BacnetStatusFlags.STATUS_FLAG_OUT_OF_SERVICE)));
        Assert.Equal("fault, out of service", records[0].Flags);
        Assert.Equal("", TrendRecordDecoder.FlagsText(0));
    }

    [Fact]
    public void Error_and_clock_change_records_are_not_values()
    {
        var records = TrendRecordDecoder.Decode(Encode(
            new BacnetLogRecord(BacnetTrendLogValueType.TL_TYPE_ERROR, new BacnetError(BacnetErrorClasses.ERROR_CLASS_DEVICE, BacnetErrorCodes.ERROR_CODE_OTHER), T0, 0),
            Real(1, 5f)));
        Assert.Equal(TrendRecordKind.Error, records[0].Kind);
        Assert.False(records[0].IsValue);
        Assert.Null(records[0].Number);
        Assert.Contains("could not read", records[0].Text);
        Assert.Equal(TrendRecordKind.Value, records[1].Kind);
    }

    [Fact]
    public void Empty_or_garbage_bytes_give_no_records_instead_of_an_exception()
    {
        Assert.Empty(TrendRecordDecoder.Decode([]));
        Assert.Empty(TrendRecordDecoder.Decode(null!));
        var garbage = new byte[] { 0xFF, 0x00, 0x13, 0x37, 0xAB, 0xCD };
        var ex = Record.Exception(() => TrendRecordDecoder.Decode(garbage));
        Assert.Null(ex);
    }

    // --- summary and chart maths ---

    private static TrendRecord V(int minute, double v) => new(T0.AddMinutes(minute), TrendRecordKind.Value, v.ToString(), v, "");

    [Fact]
    public void Summary_reports_count_range_and_average_of_values_only()
    {
        var s = TrendStats.Summarize([V(0, 10), V(1, 20), V(2, 30),
            new TrendRecord(T0.AddMinutes(3), TrendRecordKind.Error, "x", null, "")]);
        Assert.NotNull(s);
        Assert.Equal(3, s.Count);
        Assert.Equal(10, s.Min);
        Assert.Equal(30, s.Max);
        Assert.Equal(20, s.Average);
        Assert.Equal(T0, s.First);
        Assert.Equal(T0.AddMinutes(2), s.Last);
    }

    [Fact]
    public void Summary_of_nothing_numeric_is_null()
    {
        Assert.Null(TrendStats.Summarize([]));
        Assert.Null(TrendStats.Summarize([new TrendRecord(T0, TrendRecordKind.Status, "s", null, "")]));
    }

    [Fact]
    public void Small_series_are_left_alone()
    {
        var pts = Enumerable.Range(0, 50).Select(i => (T0.AddMinutes(i), (double)i)).ToList();
        Assert.Same(pts, TrendStats.Downsample(pts, 200));
    }

    [Fact]
    public void Downsampling_keeps_the_endpoints_and_a_single_sample_spike()
    {
        var pts = Enumerable.Range(0, 10_000).Select(i => (T0.AddSeconds(i), 70.0)).ToList();
        pts[4321] = (pts[4321].Item1, 500.0); // one bad sample
        pts[7777] = (pts[7777].Item1, -40.0);
        var small = TrendStats.Downsample(pts, 400);

        Assert.InRange(small.Count, 100, 410);
        Assert.Equal(pts[0], small[0]);
        Assert.Equal(pts[^1], small[^1]);
        Assert.Contains(small, p => p.Value == 500.0);
        Assert.Contains(small, p => p.Value == -40.0);
        for (var i = 1; i < small.Count; i++) Assert.True(small[i].Time >= small[i - 1].Time, "time must stay in order");
    }

    [Fact]
    public void Gridlines_land_on_round_numbers_inside_the_range()
    {
        var ticks = TrendStats.NiceTicks(67.32, 71.38);
        Assert.Equal([68.0, 69.0, 70.0, 71.0], ticks);
        Assert.Equal([0.0, 20.0, 40.0, 60.0, 80.0, 100.0], TrendStats.NiceTicks(0, 100));
        Assert.Equal([0.5, 1.0, 1.5, 2.0], TrendStats.NiceTicks(0.4, 2.1));
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(double.NaN, 1)]
    [InlineData(1, double.PositiveInfinity)]
    public void Degenerate_ranges_give_a_single_tick_and_never_hang(double min, double max) =>
        Assert.Single(TrendStats.NiceTicks(min, max));

    [Fact]
    public void Tick_counts_stay_sensible_for_very_large_and_very_small_ranges()
    {
        Assert.InRange(TrendStats.NiceTicks(0, 1_000_000).Count, 3, 10);
        Assert.InRange(TrendStats.NiceTicks(0.00001, 0.00009).Count, 3, 10);
        Assert.InRange(TrendStats.NiceTicks(-40, 140).Count, 3, 10);
    }

    [Fact]
    public void Log_interval_is_shown_in_seconds_not_hundredths()
    {
        string Show(uint cs) => BACprobe.Core.Browsing.BacnetNames.FormatValue(BacnetObjectTypes.OBJECT_TRENDLOG, BacnetPropertyIds.PROP_LOG_INTERVAL,
            new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, cs));
        Assert.Equal("10 s", Show(1000));
        Assert.Equal("0.5 s", Show(50));
        Assert.Equal("900 s", Show(90000));
        Assert.Equal("on change", Show(0));
    }

    [Fact]
    public void Overwritten_records_are_the_gap_between_total_and_stored()
    {
        Assert.Equal(1500u, new TrendLogInfo("T", 1000, 2500, 1000, true, null, null, null, false).Overwritten);
        Assert.Null(new TrendLogInfo("T", 400, 400, 1000, true, null, null, null, false).Overwritten);
        Assert.Null(new TrendLogInfo("T", null, null, null, null, null, null, null, null).Overwritten);
    }
}
