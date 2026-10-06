using System.Globalization;
using System.IO.BACnet;
using BACprobe.Core.Export;

namespace BACprobe.Core.Trends;

/// <summary>One line on a multi-point chart: a watched point, its units, and its samples.</summary>
public sealed class ChartSeries(string label, string units, BacnetObjectTypes type, IReadOnlyList<string?>? stateNames = null)
{
    public string Label { get; } = label;
    public string Units { get; } = units;
    public LiveTrend Trend { get; } = new(type, stateNames);

    /// <summary>"Zone Temp (°F)", for the legend and the CSV header.</summary>
    public string Title => Units.Length == 0 ? Label : $"{Label} ({Units})";
}

/// <summary>
/// Several watched points charted together, sampled at the same moments. Points with the same units share one real axis; mixed units
/// are each drawn across their own range (the legend and hover give the real values), so a 0-100 % valve and a 55 °F temperature can
/// still be compared by shape. Kept in memory only, like <see cref="LiveTrend"/>.
/// </summary>
public sealed class MultiTrend(IReadOnlyList<ChartSeries> series)
{
    /// <summary>More lines than this cannot be told apart on one chart.</summary>
    public const int MaxSeries = 8;

    public IReadOnlyList<ChartSeries> Series { get; } = series.Count <= MaxSeries ? series
        : throw new ArgumentException($"At most {MaxSeries} points can be charted together.", nameof(series));

    /// <summary>One sampling moment: each point's present value as read now (null for a point with no value yet).</summary>
    public void Sample(DateTime time, IReadOnlyList<string?> presentValues)
    {
        for (var i = 0; i < Series.Count && i < presentValues.Count; i++)
            if (presentValues[i] is { Length: > 0 } v) Series[i].Trend.AddValue(time, v);
            else Series[i].Trend.AddNoAnswer(time, "no value");
    }

    public void Clear()
    {
        foreach (var s in Series) s.Trend.Clear();
    }

    /// <summary>True when every line has the same units, so they share one real axis.</summary>
    public bool SharedAxis => Series.Select(s => s.Units).Distinct(StringComparer.Ordinal).Count() <= 1;

    /// <summary>The axis range for a line: its own min-max with a little room, or a fixed 0-1 for on/off.</summary>
    public static (double Min, double Max) RangeOf(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count == 0) return (0, 1);
        if (list.All(v => v is 0 or 1)) return (0, 1);
        double min = list.Min(), max = list.Max();
        var pad = (max - min) * 0.08;
        if (pad < 1e-9) pad = Math.Max(1, Math.Abs(max) * 0.05);
        return (min - pad, max + pad);
    }

    /// <summary>Where a value sits on a 0-1 scale of [min, max].</summary>
    public static double Normalise(double value, double min, double max) => max - min < 1e-12 ? 0.5 : (value - min) / (max - min);

    /// <summary>The CSV: a time column (this PC's clock) and one column per point, a row per sampling moment.</summary>
    public IEnumerable<string> CsvLines()
    {
        yield return CsvWriter.Line(["Time (this PC)", .. Series.Select(s => CsvWriter.SafeText(s.Title))]);
        var columns = Series.Select(s => s.Trend.Records.GroupBy(r => r.Time).ToDictionary(g => g.Key, g => g.Last())).ToList();
        var times = columns.SelectMany(c => c.Keys).Distinct().Order();
        foreach (var t in times)
            yield return CsvWriter.Line([t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                .. columns.Select(c => c.TryGetValue(t, out var r) ? CsvWriter.SafeText(r.IsValue && r.Number is { } n ? n.ToString(CultureInfo.InvariantCulture) : r.Text) : "")]);
    }
}
