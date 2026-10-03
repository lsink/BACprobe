using System.Collections.ObjectModel;
using System.IO;
using BACprobe.Core.Browsing;
using BACprobe.Core.Export;
using BACprobe.Core.Trends;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

/// <summary>
/// A temporary trend of one point: BACprobe reads it on a timer while the window is open and charts what it saw.
/// Nothing is stored on the device; closing the window throws the samples away (Export keeps them).
/// </summary>
public sealed partial class LiveTrendViewModel : ObservableObject, IDisposable
{
    /// <summary>The table shows only the newest samples, so it stays quick after hours of sampling. The chart and export have everything.</summary>
    private const int MaxRowsShown = 500;

    private readonly LiveTrend _trend;
    private readonly LiveTrendSampler _sampler;
    private readonly ObjectSummary _point;
    private readonly string _deviceName;
    private readonly uint _deviceInstance;
    private readonly Func<string, (string Path, ExportFormat Format)?> _pickFile;
    private CancellationTokenSource? _cts;
    private DateTime? _startedAt;
    private int _noAnswer;

    public LiveTrendViewModel(DeviceBrowser browser, ObjectSummary point, string deviceName, uint deviceInstance,
        Func<string, (string Path, ExportFormat Format)?> pickFile)
    {
        _point = point;
        _deviceName = deviceName;
        _deviceInstance = deviceInstance;
        _pickFile = pickFile;
        _trend = new LiveTrend(point.Id.type, point.StateNames);
        _sampler = new LiveTrendSampler(browser, point, _trend);
        Units = point.Units ?? "";
    }

    private string PointName => $"{_point.Name ?? _point.Label} ({BacnetNames.ObjectLabel(_point.Id)})";

    public string Title => $"Live trend - {PointName} - {_deviceName}";

    public string Heading =>
        $"{PointName} on {_deviceName} (device {_deviceInstance}). Sampled by BACprobe while this window is open; " +
        "nothing is saved on the controller, and closing the window discards the samples (Export keeps them).";

    public IReadOnlyList<int> Intervals { get; } = [1, 2, 5, 10, 30, 60];

    public ObservableCollection<TrendRecordRow> Rows { get; } = [];

    [ObservableProperty] private int _intervalSeconds = 2;
    [ObservableProperty] private string _units;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _status = "Starting...";
    [ObservableProperty] private IReadOnlyList<(DateTime Time, double Value)> _series = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel))]
    private bool _isRunning;

    public string PauseLabel => IsRunning ? "Pause" : "Resume";

    partial void OnIntervalSecondsChanged(int value)
    {
        if (IsRunning) Start(); // the new interval takes effect straight away
    }

    /// <summary>Begin (or restart) sampling: one reading now, then one every interval.</summary>
    public void Start()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _startedAt ??= DateTime.Now;
        IsRunning = true;
        _ = RunAsync(TimeSpan.FromSeconds(IntervalSeconds), cts.Token);
    }

    private async Task RunAsync(TimeSpan every, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(every);
            do
            {
                var sample = await _sampler.SampleAsync(ct);
                if (ct.IsCancellationRequested) return;
                Show(sample);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private void Show(TrendRecord sample)
    {
        if (sample.IsValue) _noAnswer = 0;
        else _noAnswer++;

        Rows.Insert(0, new TrendRecordRow(sample, Units));
        while (Rows.Count > MaxRowsShown) Rows.RemoveAt(Rows.Count - 1);
        Series = _trend.Series;

        var s = TrendStats.Summarize(_trend.Records);
        SummaryText = s is null ? "" : $"{s.Count} values   min {s.Min:0.##}   max {s.Max:0.##}   average {s.Average:0.##} {Units}".TrimEnd();

        var since = _startedAt is { } t ? $" since {t:HH:mm:ss}" : "";
        var dropped = _trend.Dropped > 0 ? $" (the oldest {_trend.Dropped} were dropped to save memory)" : "";
        Status = _noAnswer >= 3
            ? $"The device has not answered {_noAnswer} times in a row. Likely cause: network drop, or the controller is busy or restarting. " +
              "Next step: check the connection; sampling keeps trying."
            : $"Sampling every {IntervalSeconds} s{since}: {_trend.Count} sample(s){dropped}. Times are this PC's clock.";
    }

    [RelayCommand]
    private void PauseOrResume()
    {
        if (IsRunning)
        {
            _cts?.Cancel();
            IsRunning = false;
            Status = $"Paused with {_trend.Count} sample(s). Resume to carry on; the gap will show in the chart.";
        }
        else Start();
    }

    [RelayCommand]
    private void Clear()
    {
        _trend.Clear();
        Rows.Clear();
        Series = [];
        SummaryText = "";
        _startedAt = IsRunning ? DateTime.Now : null;
        Status = IsRunning ? $"Cleared. Sampling every {IntervalSeconds} s." : "Cleared.";
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var records = _trend.Records;
        if (records.Count == 0)
        {
            Status = "There is nothing to export yet.";
            return;
        }
        var choice = _pickFile(SafeFileName($"live-{_point.Name ?? _point.Label}"));
        if (choice is null) return;

        var info = new TrendLogInfo($"Live trend of {PointName}", (uint)records.Count, null, null, true,
            TimeSpan.FromSeconds(IntervalSeconds), PointName, string.IsNullOrEmpty(Units) ? null : Units, null);
        try
        {
            var (path, format) = choice.Value;
            await Task.Run(() => TrendExporter.Write(path, format, _deviceName, _deviceInstance, $"Live trend of {PointName}", info, records,
                pcClock: true));
            Status = $"Saved {records.Count} sample(s) to {path}. Sampling carries on.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save the file: {ex.Message} Likely cause: it is open in Excel, or the folder is read-only. " +
                     "Next step: close the file or choose another location.";
        }
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public void Dispose()
    {
        _cts?.Cancel();
        IsRunning = false;
    }
}
