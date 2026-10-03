using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using BACprobe.Core.Browsing;
using BACprobe.Core.Export;
using BACprobe.Core.Simulation;
using BACprobe.Core.Trends;
using ClosedXML.Excel;

namespace BACprobe.Core.Tests;

public class TrendSimTests
{
    private static readonly BacnetObjectId Tl1 = new(BacnetObjectTypes.OBJECT_TRENDLOG, 1);
    private static readonly BacnetObjectId Tl2 = new(BacnetObjectTypes.OBJECT_TRENDLOG, 2);

    private static SimulatedDeviceModel Model() => SimulatedDeviceModel.CreateSample(1001);

    private static List<BacnetLogRecord> Read(SimulatedDeviceModel m, BacnetReadRangeRequestTypes type, uint position, int count,
        DateTime? time = null)
    {
        Assert.True(m.TryReadLog(Tl1, type, position, time ?? default, count, out var slice, out _, out _, out _, out _));
        return slice;
    }

    [Fact]
    public void Sample_device_has_two_trend_logs_with_history()
    {
        var m = Model();
        Assert.True(m.TryRead(Tl1, BacnetPropertyIds.PROP_RECORD_COUNT, uint.MaxValue, out var c1, out _));
        Assert.Equal(300u, c1[0].Value);
        Assert.True(m.TryRead(Tl2, BacnetPropertyIds.PROP_RECORD_COUNT, uint.MaxValue, out var c2, out _));
        Assert.Equal(120u, c2[0].Value);
        Assert.True(m.TryRead(Tl1, BacnetPropertyIds.PROP_BUFFER_SIZE, uint.MaxValue, out var size, out _));
        Assert.Equal(1000u, size[0].Value);
    }

    [Fact]
    public void Trend_log_appears_in_the_object_list_and_its_interval_is_in_hundredths_of_a_second()
    {
        var m = Model();
        m.TryRead(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, 1001), BacnetPropertyIds.PROP_OBJECT_LIST, uint.MaxValue, out var list, out _);
        Assert.Contains(list, v => v.Value is BacnetObjectId id && id == Tl1);
        m.TryRead(Tl1, BacnetPropertyIds.PROP_LOG_INTERVAL, uint.MaxValue, out var interval, out _);
        Assert.Equal(1000u, interval[0].Value); // 10 s
    }

    [Fact]
    public void By_position_returns_the_requested_window_in_time_order()
    {
        var m = Model();
        var first10 = Read(m, BacnetReadRangeRequestTypes.RR_BY_POSITION, 1, 10);
        Assert.Equal(10, first10.Count);
        for (var i = 1; i < first10.Count; i++) Assert.True(first10[i].timestamp > first10[i - 1].timestamp);

        var next = Read(m, BacnetReadRangeRequestTypes.RR_BY_POSITION, 11, 5);
        Assert.Equal(5, next.Count);
        Assert.True(next[0].timestamp > first10[^1].timestamp, "page 2 continues where page 1 stopped");
    }

    [Fact]
    public void A_request_past_the_end_is_clamped_and_one_wholly_past_the_end_is_empty()
    {
        var m = Model();
        Assert.Equal(5, Read(m, BacnetReadRangeRequestTypes.RR_BY_POSITION, 296, 50).Count);
        Assert.Empty(Read(m, BacnetReadRangeRequestTypes.RR_BY_POSITION, 301, 10));
        Assert.Empty(Read(m, BacnetReadRangeRequestTypes.RR_BY_POSITION, 9999, 10));
    }

    [Fact]
    public void A_negative_count_reads_backwards_from_the_position()
    {
        var m = Model();
        var all = Read(m, BacnetReadRangeRequestTypes.RR_READ_ALL, 0, 0);
        var back = Read(m, BacnetReadRangeRequestTypes.RR_BY_POSITION, 10, -3);
        Assert.Equal(3, back.Count);
        Assert.Equal(all[7].timestamp, back[0].timestamp); // positions 8, 9, 10
        Assert.Equal(all[9].timestamp, back[2].timestamp);
    }

    [Fact]
    public void Read_all_returns_everything_and_by_time_starts_at_or_after_the_time()
    {
        var m = Model();
        var all = Read(m, BacnetReadRangeRequestTypes.RR_READ_ALL, 0, 0);
        Assert.Equal(300, all.Count);
        var fromTime = Read(m, BacnetReadRangeRequestTypes.RR_BY_TIME, 0, 4, all[100].timestamp);
        Assert.Equal(4, fromTime.Count);
        Assert.Equal(all[100].timestamp, fromTime[0].timestamp);
    }

    [Fact]
    public void Asking_a_non_log_object_or_a_missing_object_is_an_error_not_an_empty_answer()
    {
        var m = Model();
        Assert.False(m.TryReadLog(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1), BacnetReadRangeRequestTypes.RR_READ_ALL,
            0, default, 0, out _, out _, out _, out _, out var e1));
        Assert.Equal(BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY, e1.Code);
        Assert.False(m.TryReadLog(new BacnetObjectId(BacnetObjectTypes.OBJECT_TRENDLOG, 99), BacnetReadRangeRequestTypes.RR_READ_ALL,
            0, default, 0, out _, out _, out _, out _, out var e2));
        Assert.Equal(BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT, e2.Code);
    }

    [Fact]
    public void Ticking_appends_records_only_when_the_interval_has_passed_and_totals_keep_counting()
    {
        var m = SimulatedDeviceModel.CreateSample(1001);
        var rng = new Random(1);
        m.TryRead(Tl1, BacnetPropertyIds.PROP_TOTAL_RECORD_COUNT, uint.MaxValue, out var before, out _);
        m.Tick(rng); // immediately after creation: not due yet
        m.TryRead(Tl1, BacnetPropertyIds.PROP_TOTAL_RECORD_COUNT, uint.MaxValue, out var same, out _);
        Assert.Equal(before[0].Value, same[0].Value);
    }

    [Fact]
    public void Time_ordered_history_ends_just_before_now()
    {
        var m = Model();
        var all = Read(m, BacnetReadRangeRequestTypes.RR_READ_ALL, 0, 0);
        Assert.InRange((DateTime.Now - all[^1].timestamp).TotalSeconds, 5, 20); // newest is one interval ago
        Assert.InRange((all[^1].timestamp - all[0].timestamp).TotalSeconds, 2900, 3000); // 299 intervals of 10 s
    }

    [Fact]
    public void Binary_history_uses_boolean_records_and_numeric_history_uses_real_records()
    {
        var m = Model();
        Assert.All(Read(m, BacnetReadRangeRequestTypes.RR_READ_ALL, 0, 0), r => Assert.Equal(BacnetTrendLogValueType.TL_TYPE_REAL, r.type));
        Assert.True(m.TryReadLog(Tl2, BacnetReadRangeRequestTypes.RR_READ_ALL, 0, default, 0, out var fan, out _, out _, out _, out _));
        Assert.All(fan, r => Assert.Equal(BacnetTrendLogValueType.TL_TYPE_BOOL, r.type));
    }

    [Fact]
    public void Records_the_simulator_sends_decode_back_to_the_same_values()
    {
        var m = Model();
        var slice = Read(m, BacnetReadRangeRequestTypes.RR_BY_POSITION, 1, 25);
        var buffer = new EncodeBuffer();
        foreach (var r in slice) Services.EncodeLogRecord(buffer, r);
        var decoded = TrendRecordDecoder.Decode(buffer.buffer.AsSpan(0, buffer.offset).ToArray());
        Assert.Equal(25, decoded.Count);
        // BACnet timestamps have 1/100 s precision, so the round trip drops anything finer.
        Assert.All(slice.Zip(decoded), p => Assert.InRange(Math.Abs((p.First.timestamp - p.Second.Time).TotalMilliseconds), 0, 10));
        Assert.Equal(slice.Select(r => (double)(float)r.Value), decoded.Select(r => r.Number!.Value));
    }
}

public sealed class TrendExportAndNamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bacprobe-trend-{Guid.NewGuid():N}");

    public TrendExportAndNamesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DateTime T0 = new(2026, 10, 2, 8, 0, 0);

    private static IReadOnlyList<TrendRecord> Sample() =>
    [
        new(T0, TrendRecordKind.Value, "70.5", 70.5, ""),
        new(T0.AddMinutes(1), TrendRecordKind.Value, "71", 71, "fault"),
        new(T0.AddMinutes(2), TrendRecordKind.Error, "the device could not read the point", null, ""),
    ];

    private static TrendLogInfo Info(string? units = "°F") => new("Zone Temp Trend", 3, 3, 1000, true, TimeSpan.FromMinutes(1),
        "Zone Temp (Analog Input 1)", units, false);

    [Fact]
    public void Csv_has_a_header_values_with_units_and_a_separate_meaning_column_for_non_values()
    {
        var lines = TrendExporter.CsvLines(Sample(), "°F").ToList();
        Assert.Equal("Time (device clock),Value,Units,Meaning,Status flags", lines[0]);
        Assert.Equal("2026-10-02 08:00:00,70.5,°F,,", lines[1]);
        Assert.Equal("2026-10-02 08:01:00,71,°F,,fault", lines[2]);
        Assert.Equal("2026-10-02 08:02:00,,,the device could not read the point,", lines[3]);
    }

    [Fact]
    public void Hostile_units_text_cannot_become_a_formula_in_csv()
    {
        var lines = TrendExporter.CsvLines(Sample(), "=cmd|' /C calc'!A0").ToList();
        Assert.Contains("'=cmd", lines[1]);
    }

    [Fact]
    public void Csv_file_is_written_atomically_with_a_bom()
    {
        var path = Path.Combine(_dir, "t.csv");
        TrendExporter.Write(path, ExportFormat.Csv, "AHU-1", 1001, "Zone Temp Trend", Info(), Sample());
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path)[..3]);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Xlsx_stores_real_dates_and_numbers_so_excel_can_chart_them()
    {
        var path = Path.Combine(_dir, "t.xlsx");
        TrendExporter.Write(path, ExportFormat.Xlsx, "AHU-1", 1001, "Zone Temp Trend", Info(), Sample());
        using var wb = new XLWorkbook(path);

        var trend = wb.Worksheet("Trend");
        Assert.Equal(T0, trend.Cell(2, 1).GetDateTime());
        Assert.Equal(70.5, trend.Cell(2, 2).GetDouble());
        Assert.Equal("°F", trend.Cell(2, 3).GetString());
        Assert.Equal("fault", trend.Cell(3, 5).GetString());
        Assert.Equal("the device could not read the point", trend.Cell(4, 4).GetString());
        Assert.True(trend.Cell(1, 1).Style.Font.Bold);

        var about = wb.Worksheet("Log");
        Assert.Contains("AHU-1", about.Cell(1, 2).GetString());
        Assert.Equal("Zone Temp (Analog Input 1)", about.Cell(3, 2).GetString());
        Assert.Equal("70.5", about.Cell(7, 2).GetString()); // minimum
        Assert.Equal("71", about.Cell(8, 2).GetString());   // maximum
    }

    [Fact]
    public void Only_csv_and_excel_are_offered_for_trend_data() =>
        Assert.Throws<ArgumentException>(() =>
            TrendExporter.Write(Path.Combine(_dir, "t.csv"), ExportFormat.Ede, "d", 1, "l", Info(), Sample()));

    [Fact]
    public void A_failed_write_leaves_no_temp_file()
    {
        var bad = Path.Combine(_dir, "no-such-folder", "t.csv");
        Assert.ThrowsAny<IOException>(() => TrendExporter.Write(bad, ExportFormat.Csv, "d", 1, "l", Info(), Sample()));
        Assert.False(File.Exists(bad + ".tmp"));
    }

    [Fact]
    public void Trend_log_has_a_readable_name_and_log_objects_are_recognised()
    {
        Assert.Equal("Trend Log", BacnetNames.ObjectTypeName(BacnetObjectTypes.OBJECT_TRENDLOG));
        Assert.True(BacnetNames.TryParseObject("tl:3", out var id));
        Assert.Equal(BacnetObjectTypes.OBJECT_TRENDLOG, id.type);
        Assert.True(DeviceBrowser.IsLogObject(BacnetObjectTypes.OBJECT_TRENDLOG));
        Assert.True(DeviceBrowser.IsLogObject(BacnetObjectTypes.OBJECT_EVENT_LOG));
        Assert.False(DeviceBrowser.IsLogObject(BacnetObjectTypes.OBJECT_ANALOG_INPUT));
    }
}
