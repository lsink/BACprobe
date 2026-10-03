using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BACprobe.Core.Discovery;
using BACprobe.Core.Mstp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

/// <summary>
/// The MS/TP monitor window: listens to a trunk through a USB-RS485 adapter WITHOUT transmitting, and shows who is on it,
/// how healthy it is and what is going by. Can also open a saved capture or a made-up demo, so it works with no adapter.
/// </summary>
public sealed partial class MstpViewModel : ObservableObject, IDisposable
{
    private const string AutoBaud = "Auto-detect";
    private static readonly string[] BaudNames = [AutoBaud, "9600", "19200", "38400", "76800", "115200"];

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private MstpMonitor? _monitor;
    private MstpPortCapture? _capture;
    private CancellationTokenSource? _detectCts;
    private string? _recordPath;

    public MstpViewModel()
    {
        _timer.Tick += (_, _) => Refresh();
        RefreshPorts();
        SelectedBaud = BaudNames[0];
    }

    public ObservableCollection<string> Ports { get; } = [];
    public IReadOnlyList<string> Bauds => BaudNames;
    public ObservableCollection<MstpNodeRow> Nodes { get; } = [];
    public ObservableCollection<FindingRow> Findings { get; } = [];
    public ObservableCollection<MstpLogLine> LogLines { get; } = [];

    /// <summary>Set by the window: Open dialog for a capture file; null if cancelled.</summary>
    public Func<string?> PickOpenPath { get; set; } = () => null;

    /// <summary>Set by the window: Save dialog for a capture file (suggested name in); null if cancelled.</summary>
    public Func<string, string?> PickSavePath { get; set; } = _ => null;

    /// <summary>Raised after the log changed, so the window can scroll to the newest line.</summary>
    public event Action? LogChanged;

    [ObservableProperty] private string? _selectedPort;
    [ObservableProperty] private string _selectedBaud = AutoBaud;
    [ObservableProperty] private string _status = "Pick the adapter's COM port and press Start. BACprobe only listens; it never transmits.";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLatencyWarning))]
    private string _latencyWarning = "";

    public bool HasLatencyWarning => LatencyWarning.Length > 0;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _hideTokenAndPolls;
    [ObservableProperty] private bool _paused;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(OpenRecordingCommand), nameof(LoadDemoCommand), nameof(SaveCaptureCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isListening;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(OpenRecordingCommand), nameof(LoadDemoCommand))]
    private bool _isDetecting;

    public bool IsIdle => !IsListening;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCaptureCommand))]
    private bool _hasCapture;

    partial void OnSelectedPortChanged(string? value)
    {
        LatencyWarning = "";
        if (value is null || !OperatingSystem.IsWindows()) return;
        var bad = FtdiLatency.ReadAll().FirstOrDefault(l => string.Equals(l.PortName, value, StringComparison.OrdinalIgnoreCase) && l.IsTooHigh);
        if (bad is not null) LatencyWarning = bad.Verdict;
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        var keep = SelectedPort;
        Ports.Clear();
        foreach (var p in MstpPortCapture.PortNames()) Ports.Add(p);
        SelectedPort = keep is not null && Ports.Contains(keep) ? keep : Ports.FirstOrDefault();
        if (Ports.Count == 0)
            Status = "No serial ports found. Plug in the USB-RS485 adapter and install its driver, then press Refresh. You can still open a saved capture or the demo.";
    }

    private bool CanStart() => !IsListening && !IsDetecting && SelectedPort is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var port = SelectedPort!;
        int baud;
        if (SelectedBaud == AutoBaud)
        {
            IsDetecting = true;
            _detectCts = new CancellationTokenSource();
            try
            {
                Status = $"Finding the trunk's baud rate on {port}: listening about 3 s at each common rate...";
                var progress = new Progress<string>(s => Status = $"Finding the baud rate on {port}. {s}");
                var (picked, trials, explanation) = await Task.Run(() =>
                    MstpPortCapture.DetectBaud(port, 3, s => ((IProgress<string>)progress).Report(s), _detectCts.Token));
                if (picked is null)
                {
                    Status = explanation + " Pick a baud rate yourself to listen anyway.";
                    return;
                }
                baud = picked.Value;
                SelectedBaud = baud.ToString();
                Status = explanation;
            }
            catch (OperationCanceledException) { Status = "Cancelled."; return; }
            catch (IOException ex) { Status = ex.Message; return; }
            finally { IsDetecting = false; }
        }
        else baud = int.Parse(SelectedBaud);

        BeginSession(new MstpMonitor(baud));
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BACprobe", "mstp");
            Directory.CreateDirectory(dir);
            _recordPath = Path.Combine(dir, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.bin");
            _capture = new MstpPortCapture(port, _monitor!, _recordPath);
            _capture.Failed += msg => Application.Current?.Dispatcher.Invoke(() => { Status = msg; StopListening(); });
            _capture.Start();
        }
        catch (IOException ex)
        {
            Status = ex.Message;
            _capture?.Dispose();
            _capture = null;
            EndSession();
            return;
        }
        IsListening = true;
        HasCapture = true;
        Status = $"Listening on {port} at {baud} baud. Nothing is transmitted.";
        _timer.Start();
    }

    private bool CanStop() => IsListening || IsDetecting;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (IsDetecting) { _detectCts?.Cancel(); return; }
        StopListening();
        Status = "Stopped. What was heard is still shown; save it or press Start to listen again.";
    }

    private void StopListening()
    {
        _timer.Stop();
        _capture?.Dispose();
        _capture = null;
        IsListening = false;
        Refresh(force: true);
    }

    private bool CanOpenOther() => !IsListening && !IsDetecting;

    [RelayCommand(CanExecute = nameof(CanOpenOther))]
    private void OpenRecording()
    {
        if (PickOpenPath() is not { } path) return;
        try
        {
            var baud = SelectedBaud == AutoBaud ? 38400 : int.Parse(SelectedBaud);
            ShowRecording(File.ReadAllBytes(path), baud, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not read the capture: {ex.Message} Likely cause: the file is in use or was moved. Next step: pick it again.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenOther))]
    private void LoadDemo() => ShowRecording(MstpSampleCapture.Build(), 38400, "a made-up demo trunk");

    private void ShowRecording(byte[] bytes, int baud, string label)
    {
        _recordPath = null;
        BeginSession(MstpMonitor.FromRecording(bytes, baud));
        HasCapture = false;
        Refresh(force: true);
        Status = $"Showing {label} ({bytes.Length} bytes at {baud} baud).";
    }

    private bool CanSave() => HasCapture && !IsListening && _recordPath is not null && File.Exists(_recordPath);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveCapture()
    {
        if (PickSavePath(Path.GetFileName(_recordPath!)) is not { } dest) return;
        try
        {
            File.Copy(_recordPath!, dest, overwrite: true);
            Status = $"Saved the capture to {dest}. Open it later here, or with: bacprobe mstp-monitor --replay \"{dest}\"";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save: {ex.Message} Likely cause: the folder is read-only or the file is open. Next step: pick another place.";
        }
    }

    private void BeginSession(MstpMonitor monitor)
    {
        _monitor = monitor;
        Nodes.Clear();
        Findings.Clear();
        LogLines.Clear();
        Summary = "";
    }

    private void EndSession() => _monitor = null;

    partial void OnFilterTextChanged(string value) => Refresh(force: true);
    partial void OnHideTokenAndPollsChanged(bool value) => Refresh(force: true);
    partial void OnPausedChanged(bool value) { if (!value) Refresh(force: true); }

    /// <summary>The MAC typed in the filter box, or null for everyone. Anything that is not 0-255 is ignored (shows all).</summary>
    private byte? FilterMac => byte.TryParse(FilterText.Trim(), out var m) ? m : null;

    private void Refresh(bool force = false)
    {
        if (_monitor is null) return;
        var snap = _monitor.Snapshot();
        Summary = snap.Summary;

        SyncNodes(snap.Nodes);

        var findings = snap.Findings;
        if (Findings.Count != findings.Count || !Findings.Select(f => f.Title).SequenceEqual(findings.Select(f => f.Title)))
        {
            Findings.Clear();
            foreach (var f in findings) Findings.Add(new FindingRow(f));
        }

        if (Paused && !force) return;
        var lines = _monitor.Log(FilterMac, tokenAndPollsToo: !HideTokenAndPolls, max: 500);
        var lastShown = LogLines.Count > 0 ? LogLines[^1].Sequence : -1;
        var lastNew = lines.Count > 0 ? lines[^1].Sequence : -1;
        if (lastShown == lastNew && LogLines.Count == lines.Count && !force) return;
        LogLines.Clear();
        foreach (var l in lines) LogLines.Add(l);
        LogChanged?.Invoke();
    }

    private void SyncNodes(IReadOnlyList<MstpNodeRow> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (i >= Nodes.Count) Nodes.Add(rows[i]);
            else if (!Equals(Nodes[i], rows[i])) Nodes[i] = rows[i];
        }
        while (Nodes.Count > rows.Count) Nodes.RemoveAt(Nodes.Count - 1);
    }

    /// <summary>Show only this node's traffic (double-click a node).</summary>
    [RelayCommand]
    private void FilterToNode(MstpNodeRow? row)
    {
        if (row is not null) FilterText = row.Mac.ToString();
    }

    [RelayCommand]
    private void ClearFilter() => FilterText = "";

    public void Dispose()
    {
        _timer.Stop();
        _detectCts?.Cancel();
        _capture?.Dispose();
    }
}
