using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using BACprobe.Core.Alarms;
using BACprobe.Core.Bbmd;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Export;
using BACprobe.Core.Jobs;
using BACprobe.Core.Learning;
using BACprobe.Core.Live;
using BACprobe.Core.Networking;
using BACprobe.Core.Writing;
using System.IO.BACnet;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private DiscoveryService? _svc;
    private CancellationTokenSource? _browseCts;
    private readonly WriteLog _log = new(WriteLog.DefaultPath);
    private readonly OverrideTracker _overrides = new();
    private DeviceWriter? _writer;

    // Point lists read this session (or loaded from a job), by device instance: what Save job stores and what offline browsing shows.
    private readonly Dictionary<uint, ExportDevice> _pointCache = [];
    private DateTimeOffset _jobCreated = DateTimeOffset.Now;
    private List<WriteLogEntry> _persistedLog = [];
    private int _sessionLogSaved;

    /// <summary>Set by the window: the write/release confirmation dialog; true if the user confirmed.</summary>
    public Func<WriteRequest, bool> ConfirmWrite { get; set; } = _ => false;

    /// <summary>Shows why a write was refused, or why it had no effect. Set by the window.</summary>
    public Action<PromptContent> ShowExplanation { get; set; } = _ => { };

    /// <summary>Set by the window: shows a Save dialog (suggested file name in); null if the user cancels.</summary>
    public Func<string, (string Path, ExportFormat Format)?> PickExportFile { get; set; } = _ => null;

    /// <summary>Set by the window: opens the Find window.</summary>
    public Action<FindViewModel> ShowFind { get; set; } = _ => { };

    /// <summary>Set by the window: opens the Trend window for a trend log.</summary>
    public Action<TrendViewModel> ShowTrend { get; set; } = _ => { };

    /// <summary>Set by the window: opens a live (temporary) trend of one point.</summary>
    public Action<LiveTrendViewModel> ShowLiveTrend { get; set; } = _ => { };

    /// <summary>Set by the window: Save dialog for trend data (Excel or CSV); null if cancelled.</summary>
    public Func<string, (string Path, ExportFormat Format)?> PickTrendFile { get; set; } = _ => null;

    /// <summary>Raised when the connection is replaced (a new scan, or a job was opened), so windows built on it can close.</summary>
    public event Action? ConnectionReset;

    /// <summary>Set by the window: Save dialog for a job file (suggested name in); null if cancelled.</summary>
    public Func<string, string?> PickJobSavePath { get; set; } = _ => null;

    /// <summary>Set by the window: Open dialog for a job file; null if cancelled.</summary>
    public Func<string?> PickJobOpenPath { get; set; } = () => null;

    /// <summary>Set by the window: asks what to do about overrides this session left in place.</summary>
    public Func<IReadOnlyList<TrackedOverride>, OverrideChoice> AskOverrides { get; set; } = _ => OverrideChoice.GoBack;

    /// <summary>Set by the window: some overrides could not be released; true to continue anyway.</summary>
    public Func<int, bool> ConfirmContinueAfterFailedRelease { get; set; } = _ => false;

    public IReadOnlyList<PriorityChoice> PriorityChoices { get; } = PriorityChoice.All;

    /// <summary>True when this session has left overrides on a device that we can still talk to.</summary>
    public bool HasOverrides => _writer is not null && _overrides.Active.Count > 0;
    public ObservableCollection<string> WriteLogLines { get; } = [];

    public ObservableCollection<AdapterChoice> Adapters { get; } = [];
    public ObservableCollection<PreflightRow> Preflight { get; } = [];
    public ObservableCollection<DeviceRow> Devices { get; } = [];
    public ObservableCollection<ObjectRow> Objects { get; } = [];
    public ObservableCollection<PropertyRow> Properties { get; } = [];

    // The object list's Show filter: everything, only points with a problem, or only overridden points.
    public IReadOnlyList<PointFilterChoice> PointFilters { get; } =
    [
        new("All points", null),
        new("Problems (fault, alarm, out of service)", p => p.HasProblem),
        new("Overridden", p => p.IsOverridden),
    ];

    [ObservableProperty] private PointFilterChoice? _selectedPointFilter;
    [ObservableProperty] private string _pointFilterStatus = "";

    partial void OnSelectedPointFilterChanged(PointFilterChoice? value)
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Objects);
        view.Filter = value?.Test is { } test ? o => o is ObjectRow r && test(r.Summary) : null;
        UpdateOverrideSummary();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CheckBbmdCommand))]
    private AdapterChoice? _selectedAdapter;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _preflightPassed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(ExportSelectedCommand), nameof(ExportAllCommand))]
    private bool _isExporting;

    [ObservableProperty] private PriorityChoice _selectedPriority = PriorityChoice.Default;
    [ObservableProperty] private string _writeValueText = "";

    // On/off points, and multi-state points whose states have names, get a dropdown instead of a text box,
    // so nobody has to remember what to type or which number means what.
    public ObservableCollection<StateChoice> StateChoices { get; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotStateSelected))]
    private bool _isStateSelected;

    public bool IsNotStateSelected => !IsStateSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeviceCard), nameof(ShowDeviceActions))]
    private DeviceRow? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeviceCard), nameof(ShowPointDetail))]
    private ObjectRow? _selectedObject;

    /// <summary>The right-hand pane shows the device's details while no point is selected, and the point once one is.</summary>
    public bool ShowDeviceCard => SelectedDevice is not null && SelectedObject is null;
    public bool ShowPointDetail => SelectedObject is not null;
    [ObservableProperty] private string _propertiesHeader = "Properties";
    // Result of the last export or job save: a visible confirmation with quick access to the file.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenResultFileCommand), nameof(ShowResultInFolderCommand))]
    private string _resultPath = "";

    [ObservableProperty] private string _resultMessage = "";

    [ObservableProperty] private string _preflightSummary = "";
    /// <summary>The worst pre-flight result, for colouring its label; null while checking or with no adapter.</summary>
    [ObservableProperty] private PreflightSeverity? _preflightState;
    // Live values: the object list refreshes itself on a timer.
    private CancellationTokenSource? _liveCts;
    public IReadOnlyList<int> LiveIntervals { get; } = [1, 2, 5, 10];
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private bool _canUseLive;
    [ObservableProperty] private int _liveIntervalSeconds = 2;
    [ObservableProperty] private bool _useCov = true;
    [ObservableProperty] private string _liveStatus = "";

    partial void OnIsLiveChanged(bool value)
    {
        if (value) StartLive();
        else StopLive();
    }

    partial void OnLiveIntervalSecondsChanged(int value)
    {
        if (IsLive) StartLive();
    }

    partial void OnUseCovChanged(bool value)
    {
        if (IsLive) StartLive();
    }

    // Network check: conflicts among what answered, and differences from a job opened earlier.
    public ObservableCollection<FindingRow> NetworkFindings { get; } = [];
    public ObservableCollection<string> NetworkMapLines { get; } = [];
    private IReadOnlyList<SavedDevice>? _baseline;
    private string? _baselineName;
    [ObservableProperty] private bool _hasNetworkCheck;
    [ObservableProperty] private string _networkCheckSummary = "";
    /// <summary>The worst finding, for colouring the network check's label; null when there is nothing to report.</summary>
    [ObservableProperty] private FindingSeverity? _networkCheckState;

    private void ShowNetworkCheck(IReadOnlyList<NetworkFinding> conflicts, IReadOnlyList<DiscoveredDevice> found, NetworkMap map)
    {
        var findings = new List<NetworkFinding>(conflicts);
        findings.AddRange(map.Findings);
        findings.AddRange(DeviceHealth.Check(found));

        NetworkMapLines.Clear();
        foreach (var line in map.Lines(SelectedAdapter?.Info.Cidr ?? "this subnet")) NetworkMapLines.Add(line);
        if (_baseline is not null) findings.AddRange(NetworkCheck.Compare(_baseline, found));

        NetworkFindings.Clear();
        foreach (var f in findings.OrderByDescending(f => f.Severity)) NetworkFindings.Add(new FindingRow(f));
        var worst = findings.Count == 0 ? (FindingSeverity?)null : findings.Max(f => f.Severity);
        var icon = worst switch { FindingSeverity.Problem => "✖", FindingSeverity.Warning => "⚠", FindingSeverity.Info => "ℹ", _ => "✔" };
        var routed = map.Networks.Count(n => !n.IsLocal);
        NetworkCheckSummary = $"{icon} Network check{(_baselineName is null ? "" : $" (compared with job \"{_baselineName}\")")}: {NetworkCheck.Summarize(findings)}" +
                              (routed > 0 ? $"  -  {routed} routed network(s)" : "");
        NetworkCheckState = worst;
        HasNetworkCheck = true;
    }

    private void ClearNetworkCheck()
    {
        NetworkFindings.Clear();
        NetworkMapLines.Clear();
        NetworkCheckSummary = "";
        HasNetworkCheck = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JobButtonText))]
    private string _jobName = "";

    public string JobButtonText => JobName.Trim().Length == 0 ? "Job" : $"Job: {JobName.Trim()}";
    [ObservableProperty] private string _jobNotes = "";
    [ObservableProperty] private string _offlineBanner = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckBbmdCommand))]
    private string _bbmdText = "";

    [ObservableProperty] private string _ttlText = BbmdTarget.DefaultTtlSeconds.ToString();
    [ObservableProperty] private string _bbmdStatus = "";
    [ObservableProperty] private string _lowText = "";
    [ObservableProperty] private string _highText = "";
    [ObservableProperty] private string _status = "Pick the network adapter that is plugged into the building network.";

    public MainViewModel()
    {
        _selectedPointFilter = PointFilters[0];
        _log.Added += e => UiThread.Invoke(() => WriteLogLines.Add(e.Text));
        RefreshAdapters();
    }

    [RelayCommand]
    private void RefreshAdapters()
    {
        Adapters.Clear();
        var all = AdapterEnumerator.GetAdapters()
            .Where(a => !a.IsLoopback && !Subnet.IsLinkLocal(a.Address))
            .OrderBy(a => a.IsVirtual).ThenByDescending(a => a.IsUp);
        foreach (var a in all) Adapters.Add(new AdapterChoice(a));
        SelectedAdapter = Adapters.FirstOrDefault(a => a.Info.IsUp && !a.Info.IsVirtual);
    }

    partial void OnSelectedAdapterChanged(AdapterChoice? value) => _ = RunPreflightAsync();

    // The firewall warning, if pre-flight gave one: "no devices answered" names it as the likely cause.
    private PreflightResult? _firewallWarning;

    [RelayCommand]
    private async Task RunPreflightAsync()
    {
        Preflight.Clear();
        PreflightPassed = false;
        _firewallWarning = null;
        var adapter = SelectedAdapter;
        if (adapter is null) return;
        PreflightSummary = "Checking...";
        PreflightState = null;
        // Off the UI thread: reading the firewall rules takes a moment.
        var results = await Task.Run(() => Core.Networking.Preflight.Run(adapter.Info));
        if (!ReferenceEquals(adapter, SelectedAdapter)) return; // the tech picked another adapter meanwhile
        foreach (var r in results) Preflight.Add(new PreflightRow(r));
        _firewallWarning = results.FirstOrDefault(r => r.Check == PreflightRules.FirewallCheckName && r.Severity != PreflightSeverity.Pass);
        PreflightPassed = PreflightRules.CanProceed(results);

        // One short label in the toolbar; the full list is in its drop-down, and on the start page until the first scan.
        var failures = results.Count(r => r.Severity == PreflightSeverity.Fail);
        var warnings = results.Count(r => r.Severity == PreflightSeverity.Warning);
        PreflightState = failures > 0 ? PreflightSeverity.Fail : warnings > 0 ? PreflightSeverity.Warning : PreflightSeverity.Pass;
        PreflightSummary = failures > 0 ? $"✖ Can't scan: {failures} problem(s)"
            : warnings > 0 ? $"⚠ Ready, {warnings} warning(s)" : "✔ Ready";
        Status = PreflightPassed ? "Ready. Click Scan to find devices." : "Fix the red items in the pre-flight list, then click Re-check.";
    }

    private bool CanScan() => SelectedAdapter is not null && PreflightPassed && !IsScanning && !IsExporting;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        int low = -1, high = -1;
        if (LowText.Length > 0 || HighText.Length > 0)
        {
            if (!int.TryParse(LowText, out low) || !int.TryParse(HighText, out high) || low < 0 || high < low)
            {
                Status = "Instance range needs two whole numbers, low then high (or leave both empty).";
                return;
            }
        }

        BbmdTarget? bbmd = null;
        if (BbmdText.Trim().Length > 0)
        {
            var ttl = int.TryParse(TtlText, out var t) ? t : -1;
            if (!BbmdTarget.TryParse(BbmdText, ttl, out bbmd, out var bbmdError))
            {
                Status = bbmdError;
                return;
            }
        }

        if (!await ResolveOverridesAsync()) return;

        IsScanning = true;
        ClearNetworkCheck();
        StopLive();
        CanUseLive = false;
        ConnectionReset?.Invoke();
        ResetBrowsing();
        OfflineBanner = "";
        Status = "Sending Who-Is...";
        try
        {
            _svc?.Dispose();
            _svc = null;
            _writer = null;
            var svc = new DiscoveryService(SelectedAdapter!.Info);
            svc.Start();
            _svc = svc;
            _writer = svc.CreateWriter(_log, _overrides);
            _writer.ReadOnly = ReadOnlyMode; // the app's switch is enforced where the write happens, not only by hiding buttons
            CanUseLive = true;
            BbmdStatus = "";
            if (bbmd is not null)
            {
                Status = $"Registering with BBMD {bbmd}...";
                var registration = await svc.RegisterWithBbmdAsync(bbmd);
                BbmdStatus = registration.Message;
                registration.Changed += () => UiThread.Invoke(() => BbmdStatus = registration.Message); // e.g. a renewal that failed later
                Status = "Sending Who-Is...";
            }
            var found = await svc.WhoIsAsync(low, high, TimeSpan.FromSeconds(5));
            if (found.Count == 0)
            {
                Status = _firewallWarning is { } fw
                    ? $"No devices answered. Likely cause: Windows Firewall ({fw.Message}) Next step: {fw.NextStep} Then scan again."
                    : "No devices answered. Likely cause: wrong adapter or subnet, a firewall blocking UDP 47808, " +
                      "or devices on another subnet behind a BBMD. Next step: try another adapter or allow BACprobe through Windows Firewall.";
                return;
            }
            // Show the devices now and fill in names as each one answers: one slow or unreachable device must not hold up the list.
            var rows = new Dictionary<DiscoveredDevice, DeviceRow>();
            foreach (var d in found) Devices.Add(rows[d] = new DeviceRow(d));
            ExportAllCommand.NotifyCanExecuteChanged();
            var detailed = 0;
            Status = $"{found.Count} device(s) found. Reading details (you can select one already)...";
            await svc.EnrichAsync(found, progress: new Progress<DiscoveredDevice>(d =>
            {
                rows[d].Refresh();
                if (++detailed < found.Count) Status = $"{found.Count} device(s) found. Read details of {detailed}...";
            }));
            ShowNetworkCheck(svc.CheckNetwork(), found, svc.BuildNetworkMap(found, scanWasFiltered: low >= 0));
            ExportAllCommand.NotifyCanExecuteChanged();
            Status = $"{found.Count} device(s) found. Select one to see its objects.";
        }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message}. Likely cause: another program holds UDP 47808 or the adapter address changed. " +
                     "Next step: close other BACnet tools and click Re-check.";
        }
        finally
        {
            IsScanning = false;
            ExportAllCommand.NotifyCanExecuteChanged();
        }
    }

    private void ResetBrowsing()
    {
        _browseCts?.Cancel();
        _pointCache.Clear();
        Devices.Clear();
        Objects.Clear();
        Properties.Clear();
        PropertiesHeader = "Properties";
    }

    partial void OnSelectedDeviceChanged(DeviceRow? value)
    {
        WatchSelectedCommand.NotifyCanExecuteChanged();
        Note.Target(SelectedDevice, SelectedObject);
        _ = LoadObjectsAsync(value);
    }

    // The Note box (see NoteViewModel). Notes is the same store, for Save job and Open job.
    public NoteViewModel Note { get; } = new();
    public SessionNotes Notes => Note.Notes;

    public void Dispose()
    {
        StopLive();
        _browseCts?.Cancel();
        _svc?.Dispose();
    }
}
