using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using BACprobe.Core.Export;
using BACprobe.Core.Trends;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

/// <summary>One entry in the chart's legend: its colour, name and the value now.</summary>
public sealed partial class LegendItem(Brush color, string title) : ObservableObject
{
    public Brush Color { get; } = color;
    public string Title { get; } = title;
    [ObservableProperty] private string _now = "-";
}

/// <summary>
/// Several watched points on one chart. It samples the watch list's own rows (kept current by COV or polling), so it adds no network
/// traffic. In memory only: closing the window (or the watch list) discards it; Export keeps it.
/// </summary>
public sealed partial class MultiTrendViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<WatchRow> _rows;
    private readonly Func<string, string?> _pickCsv;
    private readonly DispatcherTimer _timer = new();

    public MultiTrendViewModel(IReadOnlyList<WatchRow> rows, Func<string, string?> pickCsv)
    {
        _rows = rows;
        _pickCsv = pickCsv;
        Trend = new MultiTrend([.. rows.Select(r => new ChartSeries($"{r.Name} ({r.Entry.Device})", r.Summary?.Units ?? "", r.Entry.Point.type,
            r.Summary?.StateNames))]);
        for (var i = 0; i < rows.Count; i++)
        {
            var brush = new SolidColorBrush(MultiTrendChart.Palette[i % MultiTrendChart.Palette.Length]);
            brush.Freeze();
            Legend.Add(new LegendItem(brush, Trend.Series[i].Title));
        }
        _timer.Tick += (_, _) => Sample();
        _timer.Interval = TimeSpan.FromSeconds(IntervalSeconds);
        _timer.Start();
        Sample();
    }

    public MultiTrend Trend { get; }
    public ObservableCollection<LegendItem> Legend { get; } = [];
    public IReadOnlyList<int> Intervals { get; } = [1, 2, 5, 10, 30, 60];

    [ObservableProperty] private int _intervalSeconds = 2;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _status = "";

    public string Title => $"Chart - {_rows.Count} watched point(s)";
    public string Explanation => Trend.SharedAxis
        ? "All these points have the same units, so they share one scale."
        : "These points have different units, so each line is drawn across its own range (0-100 %). Hover to read the real values.";

    /// <summary>Raised after each sample, so the chart redraws.</summary>
    public event Action? Sampled;

    partial void OnIntervalSecondsChanged(int value) => _timer.Interval = TimeSpan.FromSeconds(value);

    partial void OnIsPausedChanged(bool value)
    {
        if (value) _timer.Stop(); else _timer.Start();
    }

    private void Sample()
    {
        var now = DateTime.Now;
        Trend.Sample(now, [.. _rows.Select(r => r.Summary?.PresentValue)]);
        for (var i = 0; i < _rows.Count; i++) Legend[i].Now = _rows[i].Value;
        var count = Trend.Series[0].Trend.Count;
        Status = $"Sampling the watch list every {IntervalSeconds} s: {count} sample(s) since {now.AddSeconds(-(count - 1) * IntervalSeconds):HH:mm:ss}. Times are this PC's clock.";
        Sampled?.Invoke();
    }

    [RelayCommand]
    private void Clear()
    {
        Trend.Clear();
        Sampled?.Invoke();
        Status = "Cleared.";
    }

    [RelayCommand]
    private void Export()
    {
        var path = _pickCsv($"bacprobe-chart-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (path is null) return;
        try
        {
            CsvWriter.WriteFile(path, Trend.CsvLines());
            Status = $"Saved {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save {path}: {ex.Message} Likely cause: the file is open in Excel or the folder is read-only. Next step: close it, or pick another folder.";
        }
    }

    public void Dispose() => _timer.Stop();
}
