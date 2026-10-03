using System.Collections.ObjectModel;
using System.IO;
using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Export;
using BACprobe.Core.Trends;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

public sealed record RangeChoice(string Label, int? Count);

public sealed class TrendRecordRow(TrendRecord r, string? units)
{
    public string Time => r.Time.ToString("yyyy-MM-dd HH:mm:ss");
    public string Value => r.IsValue ? (string.IsNullOrEmpty(units) ? r.Text : $"{r.Text} {units}") : "-";
    public string Notes => r.IsValue ? r.Flags : (r.Flags.Length > 0 ? $"{r.Text} [{r.Flags}]" : r.Text);
}

/// <summary>Everything the Trend window shows: the log's settings, the chart data, and the table of records.</summary>
public sealed partial class TrendViewModel(TrendLogReader reader, BacnetObjectId log, string deviceName, uint deviceInstance,
    Func<string, (string Path, ExportFormat Format)?> pickFile) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cts;
    private IReadOnlyList<TrendRecord> _records = [];
    private TrendLogInfo? _info;
    private string _logName = BacnetNames.ObjectLabel(log);

    public string Title => $"Trend - {_logName} - {deviceName}";

    public IReadOnlyList<RangeChoice> Ranges { get; } =
        [new("Latest 100", 100), new("Latest 500", 500), new("Latest 2000", 2000), new("All records", null)];

    public ObservableCollection<TrendRecordRow> Rows { get; } = [];

    [ObservableProperty] private RangeChoice _selectedRange = new("Latest 500", 500);
    [ObservableProperty] private string _infoText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _status = "Reading the trend log...";
    [ObservableProperty] private string _units = "";
    [ObservableProperty] private IReadOnlyList<(DateTime Time, double Value)> _series = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(ExportCommand), nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;

    public bool NotBusy => !IsBusy;

    partial void OnSelectedRangeChanged(RangeChoice value)
    {
        if (!IsBusy) _ = LoadAsync();
    }

    private bool CanLoad() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    public async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        Status = "Reading the trend log...";
        try
        {
            var info = _info = await reader.ReadInfoAsync(log, cts.Token);
            _logName = info.Name ?? _logName;
            Units = info.Units ?? "";
            InfoText = Describe(info);
            OnPropertyChanged(nameof(Title));

            var count = info.RecordCount ?? 0;
            if (count == 0)
            {
                _records = [];
                Show([]);
                Status = info.RecordCount is null
                    ? "The device did not say how many records this log holds. Likely cause: it is not a standard trend log. Next step: check the device's own tool."
                    : "The log is empty: it has not recorded anything yet.";
                return;
            }

            var progress = new Progress<int>(n => Status = $"Reading records: {n} of {Math.Min(count, (uint)(SelectedRange.Count ?? int.MaxValue))}...");
            _records = await reader.ReadRecordsAsync(log, count, SelectedRange.Count, progress, cts.Token);
            Show(_records);
            Status = _records.Count == 0
                ? "The device returned no records."
                : $"Showing {_records.Count} record(s). Times are the device's own clock.";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Could not read the trend log: {ex.Message}. Likely cause: network drop, a busy controller, or the connection was closed (scan again). " +
                     "Next step: check the connection and click Refresh.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(IReadOnlyList<TrendRecord> records)
    {
        Rows.Clear();
        foreach (var r in records.Reverse()) Rows.Add(new TrendRecordRow(r, _info?.Units)); // newest first
        Series = [.. records.Where(r => r.IsValue && r.Number.HasValue).Select(r => (r.Time, r.Number!.Value))];
        var s = TrendStats.Summarize(records);
        SummaryText = s is null
            ? ""
            : $"{s.Count} values   min {s.Min:0.##}   max {s.Max:0.##}   average {s.Average:0.##} {_info?.Units}".TrimEnd();
    }

    private static string Describe(TrendLogInfo i)
    {
        var lines = new List<string>
        {
            $"Logging: {i.Source ?? "unknown"}{(i.Units is null ? "" : $" in {i.Units}")}",
            $"Interval: {(i.LogInterval is { } v ? $"every {v.TotalSeconds:0.##} s" : "on change, or unknown")}    " +
            $"Records: {i.RecordCount?.ToString() ?? "?"}{(i.BufferSize is { } b ? $" of {b}" : "")}    " +
            $"Enabled: {(i.Enabled is { } e ? (e ? "yes" : "NO") : "unknown")}",
        };
        if (i.Enabled == false) lines.Add("This log is switched off, so it is not adding new records.");
        if (i.Overwritten is { } lost and > 0) lines.Add($"{lost} older record(s) were overwritten when the buffer filled; they are gone.");
        return string.Join(Environment.NewLine, lines);
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task ExportAsync()
    {
        if (_records.Count == 0)
        {
            Status = "There is nothing to export yet.";
            return;
        }
        var choice = pickFile(SafeFileName(_logName));
        if (choice is null) return;

        try
        {
            var (path, format) = choice.Value;
            await Task.Run(() => TrendExporter.Write(path, format, deviceName, deviceInstance, _logName, _info, _records));
            Status = $"Saved {_records.Count} record(s) to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save the file: {ex.Message} Likely cause: it is open in Excel, or the folder is read-only. Next step: close the file or choose another location.";
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public void Dispose() => _cts?.Cancel();
}
