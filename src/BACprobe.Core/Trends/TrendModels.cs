using System.Globalization;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using BACprobe.Core.Browsing;

namespace BACprobe.Core.Trends;

public enum TrendRecordKind
{
    /// <summary>A sampled value (real, binary, enumerated, integer).</summary>
    Value,
    /// <summary>The log's own state changed (started, stopped, buffer purged...).</summary>
    Status,
    /// <summary>The device could not read the source point when it logged.</summary>
    Error,
    /// <summary>The device's clock was changed.</summary>
    TimeChange,
    Other,
}

/// <summary>One entry in a trend log. <see cref="Time"/> is the device's own clock (BACnet logs carry no time zone).</summary>
public sealed record TrendRecord(DateTime Time, TrendRecordKind Kind, string Text, double? Number, string Flags)
{
    public bool IsValue => Kind == TrendRecordKind.Value;
}

/// <summary>What a trend log says about itself. Anything the device would not tell us is null.</summary>
public sealed record TrendLogInfo(
    string? Name,
    uint? RecordCount,
    uint? TotalRecordCount,
    uint? BufferSize,
    bool? Enabled,
    TimeSpan? LogInterval,
    string? Source,
    string? Units,
    bool? StopWhenFull)
{
    /// <summary>Records that have scrolled out of a full buffer and are gone for good.</summary>
    public uint? Overwritten => TotalRecordCount is { } t && RecordCount is { } c && t > c ? t - c : null;
}

public static class TrendRecordDecoder
{
    private const int MaxRecordsPerAnswer = 100_000;

    /// <summary>Turn the bytes of a ReadRange answer into records. Never throws on odd device data: undecodable bytes give fewer records.</summary>
    public static IReadOnlyList<TrendRecord> Decode(byte[] range)
    {
        if (range is null || range.Length == 0) return [];
        var result = new List<TrendRecord>();
        try
        {
            // The library decodes one record per call and says how many bytes it used.
            var offset = 0;
            while (offset < range.Length && result.Count < MaxRecordsPerAnswer)
            {
                var used = Services.DecodeLogRecord(range, offset, range.Length - offset, 1, out var records);
                if (used <= 0) break; // no progress: stop rather than loop forever on bad data
                if (records is not null) result.AddRange(records.Select(From));
                offset += used;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidCastException or NullReferenceException or OverflowException)
        {
            // keep whatever decoded cleanly before the bad bytes
        }
        return result;
    }

    /// <summary>Convert one library record into display form.</summary>
    public static TrendRecord From(BacnetLogRecord r)
    {
        var flags = FlagsText(r.statusFlags);
        switch (r.type)
        {
            case BacnetTrendLogValueType.TL_TYPE_REAL:
                var f = Convert.ToSingle(r.Value, CultureInfo.InvariantCulture);
                return new(r.timestamp, TrendRecordKind.Value, f.ToString("0.##", CultureInfo.InvariantCulture), f, flags);
            case BacnetTrendLogValueType.TL_TYPE_BOOL:
                var on = r.Value is true or 1 or 1u;
                return new(r.timestamp, TrendRecordKind.Value, on ? "Active" : "Inactive", on ? 1 : 0, flags);
            case BacnetTrendLogValueType.TL_TYPE_ENUM or BacnetTrendLogValueType.TL_TYPE_UNSIGN or BacnetTrendLogValueType.TL_TYPE_SIGN:
                var n = Convert.ToDouble(r.Value, CultureInfo.InvariantCulture);
                return new(r.timestamp, TrendRecordKind.Value, n.ToString("0.##", CultureInfo.InvariantCulture), n, flags);
            case BacnetTrendLogValueType.TL_TYPE_STATUS:
                return new(r.timestamp, TrendRecordKind.Status, "log status changed", null, flags);
            case BacnetTrendLogValueType.TL_TYPE_ERROR:
                return new(r.timestamp, TrendRecordKind.Error, "the device could not read the point", null, flags);
            case BacnetTrendLogValueType.TL_TYPE_DELTA:
                return new(r.timestamp, TrendRecordKind.TimeChange, $"device clock changed by {Convert.ToString(r.Value, CultureInfo.InvariantCulture)} s", null, flags);
            default:
                return new(r.timestamp, TrendRecordKind.Other, "(unsupported record type)", null, flags);
        }
    }

    public static string FlagsText(BacnetStatusFlags f)
    {
        var parts = new List<string>();
        if (f.HasFlag(BacnetStatusFlags.STATUS_FLAG_IN_ALARM)) parts.Add("in alarm");
        if (f.HasFlag(BacnetStatusFlags.STATUS_FLAG_FAULT)) parts.Add("fault");
        if (f.HasFlag(BacnetStatusFlags.STATUS_FLAG_OVERRIDDEN)) parts.Add("overridden");
        if (f.HasFlag(BacnetStatusFlags.STATUS_FLAG_OUT_OF_SERVICE)) parts.Add("out of service");
        return string.Join(", ", parts);
    }
}

/// <summary>A little arithmetic for the chart and the summary line.</summary>
public static class TrendStats
{
    public sealed record Summary(int Count, double Min, double Max, double Average, DateTime First, DateTime Last);

    /// <summary>Min, max and average of the numeric values; null if there are none.</summary>
    public static Summary? Summarize(IEnumerable<TrendRecord> records)
    {
        var vals = records.Where(r => r.IsValue && r.Number.HasValue).ToList();
        if (vals.Count == 0) return null;
        var nums = vals.Select(r => r.Number!.Value).ToList();
        return new Summary(vals.Count, nums.Min(), nums.Max(), nums.Average(), vals.Min(r => r.Time), vals.Max(r => r.Time));
    }

    /// <summary>
    /// Round-number gridline values between <paramref name="min"/> and <paramref name="max"/>
    /// (steps of 1, 2 or 5 times a power of ten), so axes read 70, 71, 72 rather than 70.36, 71.38.
    /// </summary>
    public static IReadOnlyList<double> NiceTicks(double min, double max, int target = 5)
    {
        if (!(max > min) || double.IsNaN(min) || double.IsNaN(max) || double.IsInfinity(min) || double.IsInfinity(max)) return [min];
        var rough = (max - min) / Math.Max(1, target - 1);
        var pow = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var fraction = rough / pow;
        var step = (fraction < 1.5 ? 1 : fraction < 3 ? 2 : fraction < 7 ? 5 : 10) * pow; // the classic "nice number" thresholds

        var ticks = new List<double>();
        for (var t = Math.Ceiling(min / step) * step; t <= max + step * 1e-9 && ticks.Count < 50; t += step)
            ticks.Add(Math.Round(t, 10)); // no 70.00000000000001
        return ticks;
    }

    /// <summary>
    /// Reduce to at most about <paramref name="maxPoints"/> points for drawing, keeping each bucket's lowest and highest
    /// value so a one-sample spike never disappears from the chart.
    /// </summary>
    public static IReadOnlyList<(DateTime Time, double Value)> Downsample(IReadOnlyList<(DateTime Time, double Value)> points, int maxPoints)
    {
        if (maxPoints < 4) maxPoints = 4;
        if (points.Count <= maxPoints) return points;

        var buckets = maxPoints / 2;
        var result = new List<(DateTime, double)>(maxPoints + 2) { points[0] };
        var size = (double)points.Count / buckets;
        for (var b = 0; b < buckets; b++)
        {
            var from = (int)(b * size);
            var to = Math.Min(points.Count, (int)((b + 1) * size));
            if (to <= from) continue;
            var lo = from;
            var hi = from;
            for (var i = from; i < to; i++)
            {
                if (points[i].Value < points[lo].Value) lo = i;
                if (points[i].Value > points[hi].Value) hi = i;
            }
            // keep time order inside the bucket
            foreach (var i in lo <= hi ? new[] { lo, hi } : new[] { hi, lo })
                if (result[^1].Item1 != points[i].Time || result[^1].Item2 != points[i].Value) result.Add(points[i]);
        }
        if (result[^1].Item1 != points[^1].Time) result.Add(points[^1]);
        return result;
    }
}
