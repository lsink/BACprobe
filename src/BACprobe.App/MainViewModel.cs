using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using BACprobe.Core.Bbmd;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Export;
using BACprobe.Core.Jobs;
using BACprobe.Core.Live;
using BACprobe.Core.Networking;
using BACprobe.Core.Writing;
using System.IO.BACnet;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

public sealed class AdapterChoice(AdapterInfo info)
{
    public AdapterInfo Info { get; } = info;
    public string Display => $"{Info.Address}  -  {Info.Name} ({Info.Cidr}){(Info.IsUp ? "" : " [down]")}{(Info.IsVirtual ? " [virtual]" : "")}";
}

public sealed class PreflightRow(PreflightResult r)
{
    public string Icon { get; } = r.Severity switch { PreflightSeverity.Pass => "✔", PreflightSeverity.Warning => "⚠", _ => "✖" };
    public string Check { get; } = r.Check;
    public string Message { get; } = r.Message;
    public string? Help { get; } = r.LikelyCause is null ? null : $"Likely cause: {r.LikelyCause}\nNext step: {r.NextStep}";
    public bool HasHelp => Help is not null;
    public PreflightSeverity Severity { get; } = r.Severity;
}

/// <summary>One line of the network check: what is wrong, why it is probably wrong, and what to do.</summary>
public sealed class FindingRow(NetworkFinding f)
{
    public string Icon { get; } = f.Severity switch { FindingSeverity.Problem => "✖", FindingSeverity.Warning => "⚠", _ => "ℹ" };
    public FindingSeverity Severity { get; } = f.Severity;
    public string Title { get; } = f.Title;
    public string Detail { get; } = f.Detail;
    public string Help { get; } = $"Likely cause: {f.LikelyCause}{Environment.NewLine}Next step: {f.NextStep}";
}

public sealed class DeviceRow(DiscoveredDevice d) : ObservableObject
{
    public DiscoveredDevice Device => d;
    public uint Instance => d.InstanceId;
    public string Address => d.AddressText;
    public string Vendor => d.VendorName ?? $"vendor {d.VendorId}";
    public string Model => d.ModelName ?? "-";
    public string Firmware => d.FirmwareRevision ?? "-";
    public string Name => d.ObjectName ?? "-";

    /// <summary>The device's details were read: show them.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One entry in the state dropdown ("On (Active)", "Standby (3)"): what the tech sees, and the text the write parser understands.</summary>
public sealed record StateChoice(string Label, string Text);

public sealed class ObjectRow(ObjectSummary s) : ObservableObject
{
    private ObjectSummary _s = s;
    public ObjectSummary Summary => _s;
    public string Id => $"{BacnetNames.ObjectTypeShort(_s.Id.type)} {_s.Id.instance}";
    public string Type => _s.TypeName;
    public string Name => _s.Name ?? "-";
    public string Value => _s.ValueText;
    public string Description => _s.Description ?? "";
    public bool IsOverridden => _s.IsOverridden;
    public string Override => _s.OverrideText;
    public string OverrideTooltip => _s.OverrideTooltip;
    public bool HasProblem => _s.HasProblem;
    /// <summary>"Fault: open loop", "In alarm", "Out of service"; empty for a healthy point.</summary>
    public string Status => _s.ProblemText;
    /// <summary>Each problem with its likely cause and next step.</summary>
    public string StatusTooltip => _s.ProblemTooltip;

    private bool _recentlyChanged;

    /// <summary>True for a moment after a live refresh changed this point, so the eye can find it.</summary>
    public bool RecentlyChanged
    {
        get => _recentlyChanged;
        private set => SetProperty(ref _recentlyChanged, value);
    }

    public void MarkChanged() => _ = FlashAsync();

    private async Task FlashAsync()
    {
        RecentlyChanged = true;
        await Task.Delay(1500);
        RecentlyChanged = false;
    }

    /// <summary>Swap in freshly read values (e.g. after a write) without losing the grid selection.</summary>
    public void Refresh(ObjectSummary fresh)
    {
        _s = fresh;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>One choice in the object list's Show filter; a null test means every point.</summary>
public sealed record PointFilterChoice(string Label, Func<ObjectSummary, bool>? Test);

/// <summary>What the user chose when asked about overrides they are about to walk away from.</summary>
public enum OverrideChoice { Release, Leave, GoBack }

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

    /// <summary>Open the Find window: search every point on every device read so far.</summary>
    [RelayCommand]
    private void OpenFind() => ShowFind(new FindViewModel(
        getIndex: () => [.. _pointCache.Values],
        deviceCount: () => Devices.Count,
        readAllDevices: async progress =>
        {
            IsExporting = true; // blocks Scan while we read every device
            try
            {
                var (collected, _) = await CollectAllAsync(progress);
                return collected.Count;
            }
            finally { IsExporting = false; }
        },
        goTo: GoToPointAsync));

    /// <summary>Select a device and one of its points in the main window (used by Find). Waits for the device's objects to load.</summary>
    private async Task GoToPointAsync(uint instance, BacnetObjectId id)
    {
        var device = Devices.FirstOrDefault(d => d.Instance == instance);
        if (device is null)
        {
            Status = $"Device {instance} is no longer in the list. Scan again to find it.";
            return;
        }

        if (!ReferenceEquals(SelectedDevice, device)) SelectedDevice = device; // loads its objects
        for (var i = 0; i < 150; i++) // up to about 15 s for a slow device
        {
            var row = Objects.FirstOrDefault(o => o.Summary.Id == id);
            if (row is not null)
            {
                SelectedObject = row;
                return;
            }
            await Task.Delay(100);
        }
        Status = "That point did not appear in the device's object list. Likely cause: the device is slow or the object was removed. Next step: select the device again.";
    }

    // Trend logs have no value to write; instead the tech can open their history.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ViewTrendCommand))]
    private bool _isTrendSelected;

    // Any point with a live value can be trended on the spot while connected: BACprobe samples it while the window is open.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrendLiveCommand))]
    private bool _canTrendLive;

    [RelayCommand(CanExecute = nameof(CanTrendLive))]
    private void TrendLive()
    {
        if (_svc is null || SelectedObject is null || SelectedDevice is null) return;
        var device = SelectedDevice;
        var name = device.Name == "-" ? $"Device {device.Instance}" : device.Name;
        ShowLiveTrend(new LiveTrendViewModel(_svc.OpenDevice(device.Device), SelectedObject.Summary, name, device.Instance, PickTrendFile));
    }

    [RelayCommand(CanExecute = nameof(IsTrendSelected))]
    private void ViewTrend()
    {
        if (_svc is null || SelectedObject is null || SelectedDevice is null) return;
        var device = SelectedDevice;
        var name = device.Name == "-" ? $"Device {device.Instance}" : device.Name;
        ShowTrend(new TrendViewModel(_svc.OpenTrendLogs(device.Device), SelectedObject.Summary.Id, name, device.Instance, PickTrendFile));
    }
    [ObservableProperty] private StateChoice? _selectedStateChoice;

    partial void OnSelectedStateChoiceChanged(StateChoice? value)
    {
        if (value is not null) WriteValueText = value.Text;
    }
    [ObservableProperty] private string _overrideSummary = "No overrides or problems.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteSelectedCommand), nameof(ReleaseSelectedCommand))]
    private bool _canWriteSelected;

    [ObservableProperty] private DeviceRow? _selectedDevice;
    [ObservableProperty] private ObjectRow? _selectedObject;
    [ObservableProperty] private string _propertiesHeader = "Properties";
    // Result of the last export or job save: a visible confirmation with quick access to the file.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenResultFileCommand), nameof(ShowResultInFolderCommand))]
    private string _resultPath = "";

    [ObservableProperty] private string _resultMessage = "";

    [ObservableProperty] private string _preflightSummary = "";
    [ObservableProperty] private bool _preflightExpanded = true;
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
    [ObservableProperty] private bool _networkCheckExpanded;
    [ObservableProperty] private string _networkCheckSummary = "";

    private void ShowNetworkCheck(IReadOnlyList<NetworkFinding> conflicts, IReadOnlyList<DiscoveredDevice> found, NetworkMap map)
    {
        var findings = new List<NetworkFinding>(conflicts);
        findings.AddRange(map.Findings);

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
        NetworkCheckExpanded = worst == FindingSeverity.Problem; // open by itself only for something that needs fixing
        HasNetworkCheck = true;
    }

    private void ClearNetworkCheck()
    {
        NetworkFindings.Clear();
        NetworkMapLines.Clear();
        NetworkCheckSummary = "";
        HasNetworkCheck = false;
    }

    [ObservableProperty] private string _jobName = "";
    [ObservableProperty] private string _jobNotes = "";
    [ObservableProperty] private string _offlineBanner = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckBbmdCommand))]
    private string _bbmdText = "";

    /// <summary>Set by the window: opens the BBMD check.</summary>
    public Action<BbmdCheckViewModel> ShowBbmdCheck { get; set; } = _ => { };

    private bool CanCheckBbmd() => SelectedAdapter is not null && BbmdText.Trim().Length > 0;

    /// <summary>Read the BBMD's tables and its peers' and say what looks wrong. Needs no scan first; changes nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckBbmd))]
    private void CheckBbmd()
    {
        if (SelectedAdapter is null) return;
        if (!BbmdTarget.TryParse(BbmdText, BbmdTarget.DefaultTtlSeconds, out var target, out var error))
        {
            Status = error;
            return;
        }
        ShowBbmdCheck(new BbmdCheckViewModel(SelectedAdapter.Info, target!));
    }
    [ObservableProperty] private string _ttlText = BbmdTarget.DefaultTtlSeconds.ToString();
    [ObservableProperty] private string _bbmdStatus = "";
    [ObservableProperty] private string _lowText = "";
    [ObservableProperty] private string _highText = "";
    [ObservableProperty] private string _status = "Pick the network adapter that is plugged into the building network.";

    public MainViewModel()
    {
        _selectedPointFilter = PointFilters[0];
        _log.Added += e => OnUi(() => WriteLogLines.Add(e.Text));
        RefreshAdapters();
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.Invoke(a);
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
        // Off the UI thread: reading the firewall rules takes a moment.
        var results = await Task.Run(() => Core.Networking.Preflight.Run(adapter.Info));
        if (!ReferenceEquals(adapter, SelectedAdapter)) return; // the tech picked another adapter meanwhile
        foreach (var r in results) Preflight.Add(new PreflightRow(r));
        _firewallWarning = results.FirstOrDefault(r => r.Check == PreflightRules.FirewallCheckName && r.Severity != PreflightSeverity.Pass);
        PreflightPassed = PreflightRules.CanProceed(results);

        // Collapse the list when everything is fine; open it when the tech needs to read something.
        var failures = results.Count(r => r.Severity == PreflightSeverity.Fail);
        var warnings = results.Count(r => r.Severity == PreflightSeverity.Warning);
        var warned = string.Join(", ", results.Where(r => r.Severity == PreflightSeverity.Warning).Select(r => r.Check));
        PreflightSummary = failures > 0
            ? $"✖ {failures} problem(s) to fix before you can scan - details below"
            : warnings > 0 ? $"⚠ Ready, with {warnings} warning(s): {warned} - click to read" : "✔ All pre-flight checks passed";
        PreflightExpanded = failures > 0; // a warning does not block scanning, so it stays one line until the tech opens it
        Status = PreflightPassed ? "Ready. Click Scan to find devices." : "Fix the red items above, then click Re-check.";
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
            CanUseLive = true;
            BbmdStatus = "";
            if (bbmd is not null)
            {
                Status = $"Registering with BBMD {bbmd}...";
                var registration = await svc.RegisterWithBbmdAsync(bbmd);
                BbmdStatus = registration.Message;
                registration.Changed += () => OnUi(() => BbmdStatus = registration.Message); // e.g. a renewal that failed later
                Status = "Sending Who-Is...";
            }
            var found = await svc.WhoIsAsync(low, high, TimeSpan.FromSeconds(5));
            if (found.Count == 0)
            {
                Status = _firewallWarning is { } fw
                    ? $"No devices answered. Likely cause: Windows Firewall ({fw.Message}) Next step: {fw.NextStep} Then scan again."
                    : "No devices answered. Likely cause: wrong adapter or subnet, a firewall blocking UDP 47808, " +
                      "or devices on another subnet behind a BBMD. Next step: try another adapter or allow BACprobe through Windows Firewall.";
                if (_firewallWarning is not null) PreflightExpanded = true; // show the firewall details right away
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

    partial void OnSelectedDeviceChanged(DeviceRow? value) => _ = LoadObjectsAsync(value);

    private async Task LoadObjectsAsync(DeviceRow? row)
    {
        _browseCts?.Cancel();
        StopLive();
        Objects.Clear();
        UpdateOverrideSummary();
        Properties.Clear();
        PropertiesHeader = "Properties";
        ExportSelectedCommand.NotifyCanExecuteChanged();
        if (row is null) return;

        if (_svc is null)
        {
            ShowSavedPoints(row);
            return;
        }

        var cts = _browseCts = new CancellationTokenSource();
        var name = row.Name == "-" ? $"device {row.Instance}" : row.Name;
        try
        {
            var browser = _svc.OpenDevice(row.Device);
            Status = $"Reading the object list of {name}...";
            var ids = await browser.ReadObjectListAsync(cts.Token);
            Status = $"{ids.Count} objects in {name}. Reading names and values...";
            var progress = new Progress<int>(n =>
            {
                if (!cts.IsCancellationRequested) Status = $"Reading {name}: {n} of {ids.Count} objects...";
            });
            var summaries = await browser.ReadSummariesAsync(ids, progress, cts.Token);
            if (cts.IsCancellationRequested) return;
            _pointCache[row.Instance] = new ExportDevice(row.Device, row.Name == "-" ? $"Device {row.Instance}" : row.Name, summaries);
            foreach (var s in summaries) Objects.Add(new ObjectRow(s));
            ExportSelectedCommand.NotifyCanExecuteChanged();
            Status = $"{ids.Count} objects in {name}. Select one to see its properties.";
            UpdateOverrideSummary();
            if (IsLive) StartLive();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                Status = $"Could not read {name}: {ex.Message}. Likely cause: network drop, a busy controller, or a router dropping the request. " +
                         "Next step: check the connection and select the device again.";
        }
    }

    private void StopLive()
    {
        _liveCts?.Cancel();
        _liveCts = null;
        LiveStatus = "";
    }

    /// <summary>
    /// (Re)start keeping the selected device's points current: COV where the device supports it, polling for the rest.
    /// Does nothing until a device with objects is showing.
    /// </summary>
    private void StartLive()
    {
        StopLive();
        if (!IsLive || _svc is null || SelectedDevice is null || Objects.Count == 0) return;

        var cts = _liveCts = new CancellationTokenSource();
        var rows = Objects.ToDictionary(r => r.Summary); // keyed by the summary object itself
        var watcher = _svc.CreateLiveWatcher(SelectedDevice.Device, [.. rows.Keys],
            new LiveOptions(TimeSpan.FromSeconds(LiveIntervalSeconds), UseCov));

        // The watcher reports from background threads (COV notifications arrive on the network thread): hop to the UI thread.
        watcher.StatusChanged += text => PostUi(() =>
        {
            if (!cts.IsCancellationRequested) LiveStatus = text;
        });
        watcher.PointChanged += summary => PostUi(() =>
        {
            if (cts.IsCancellationRequested || !rows.TryGetValue(summary, out var row)) return;
            row.Refresh(summary);
            row.MarkChanged();
            if (ReferenceEquals(SelectedObject, row)) UpdateOpenProperties(summary);
            UpdateOverrideSummary();
        });
        _ = RunLiveAsync(watcher, cts.Token);
    }

    private async Task RunLiveAsync(LiveWatcher watcher, CancellationToken ct)
    {
        // Off the UI thread: the watch loop decodes every poll and notification. Its events already hop back via PostUi.
        var failure = await Task.Run(() => watcher.RunAsync(ct));
        if (failure is null || ct.IsCancellationRequested) return;
        // The device stopped answering: stop and say why, instead of showing stale numbers as if they were live.
        IsLive = false;
        Status = failure;
    }

    private static void PostUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) a(); else d.BeginInvoke(a);
    }

    /// <summary>Update the Present Value and Priority Array rows in place, so the properties panel does not flicker or lose its place.</summary>
    private void UpdateOpenProperties(ObjectSummary s)
    {
        for (var i = 0; i < Properties.Count; i++)
        {
            var r = Properties[i];
            if (r.PropertyId == (uint)BacnetPropertyIds.PROP_PRESENT_VALUE && s.PresentValue is not null)
                Properties[i] = r with { Display = s.DisplayValue };
            else if (r.PropertyId == (uint)BacnetPropertyIds.PROP_PRIORITY_ARRAY)
                Properties[i] = r with { Display = s.PriorityArrayText };
        }
    }

    /// <summary>Offline (a job was opened): show the points that were saved, with no network traffic.</summary>
    private void ShowSavedPoints(DeviceRow row)
    {
        if (!_pointCache.TryGetValue(row.Instance, out var saved))
        {
            Status = $"Device {row.Instance} was found when the job was saved, but its points were not read. Scan to read them live.";
            return;
        }
        foreach (var s in saved.Objects) Objects.Add(new ObjectRow(s));
        UpdateOverrideSummary();
        ExportSelectedCommand.NotifyCanExecuteChanged();
        Status = $"{saved.Objects.Count} saved objects in {saved.Name}. These are the values from when the job was saved; Scan to read live.";
    }

    partial void OnSelectedObjectChanged(ObjectRow? value)
    {
        // A value typed for one point must never carry over to the next.
        WriteValueText = "";
        SelectedStateChoice = null;
        StateChoices.Clear();
        if (value is not null)
            foreach (var (label, text) in StateText.Choices(value.Summary.Id.type, value.Summary.StateNames))
                StateChoices.Add(new StateChoice(label, text));
        IsStateSelected = StateChoices.Count > 0;
        IsTrendSelected = value?.Summary.Id.type == BacnetObjectTypes.OBJECT_TRENDLOG && _svc is not null;
        CanTrendLive = value is not null && _svc is not null && BacnetNames.HasLivePresentValue(value.Summary.Id.type);
        if (value is not null && StateText.IsBinary(value.Summary.Id.type))
        {
            // Most overrides flip the point, so start on the opposite of what it is now. The confirmation still shows the new value.
            var isOn = string.Equals(value.Summary.PresentValue, "Active", StringComparison.OrdinalIgnoreCase);
            SelectedStateChoice = StateChoices[isOn ? 1 : 0];
        }
        // A multi-state point starts with nothing picked: there is no obvious "other" state, and guessing could write the wrong one.
        _ = LoadPropertiesAsync(value);
    }

    private async Task LoadPropertiesAsync(ObjectRow? row)
    {
        Properties.Clear();
        CanWriteSelected = false;
        if (row is null || SelectedDevice is null || _svc is null)
        {
            PropertiesHeader = row is not null && _svc is null ? "Properties - offline (Scan to read live)" : "Properties";
            return;
        }

        var label = BacnetNames.ObjectLabel(row.Summary.Id);
        PropertiesHeader = $"Properties - {label}";
        try
        {
            var rows = await _svc.OpenDevice(SelectedDevice.Device).ReadAllPropertiesAsync(row.Summary.Id, _browseCts?.Token ?? default);
            if (!ReferenceEquals(SelectedObject, row)) return; // selection moved on while reading
            foreach (var r in rows) Properties.Add(r);
            CanWriteSelected = rows.Any(r => r.PropertyId == (uint)BacnetPropertyIds.PROP_PRIORITY_ARRAY);
            if (rows.Count == 0)
                Status = $"The device returned no properties for {label}. Likely cause: the object was removed. Next step: select the device again.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = $"Could not read {label}: {ex.Message}. Likely cause: the device refused the request or stopped answering. Next step: try again.";
        }
    }

    private bool CanExportSelected() => !IsExporting && SelectedDevice is not null && Objects.Count > 0;

    private bool CanExportAll() => !IsExporting && !IsScanning && Devices.Count > 0 && (_svc is not null || _pointCache.Count > 0);

    /// <summary>
    /// Every device's point list. Live: read now (so values are fresh). Offline (a job is open): the saved points.
    /// Devices that cannot be read are returned with a reason instead of silently dropped.
    /// </summary>
    private async Task<(List<ExportDevice> Devices, List<(DeviceRow Row, string Reason)> Failed)> CollectAllAsync(IProgress<string> progress)
    {
        var collected = new List<ExportDevice>();
        var failed = new List<(DeviceRow, string)>();
        foreach (var d in Devices.ToList())
        {
            if (_svc is null)
            {
                if (_pointCache.TryGetValue(d.Instance, out var saved)) collected.Add(saved);
                else failed.Add((d, "points were not read when the job was saved"));
                continue;
            }
            try
            {
                var read = await PointExporter.CollectAsync(_svc.OpenDevice(d.Device), d.Device, progress);
                _pointCache[d.Instance] = read;
                collected.Add(read);
            }
            catch (Exception ex) { failed.Add((d, ex.Message)); }
        }
        return (collected, failed);
    }

    [RelayCommand(CanExecute = nameof(CanExportSelected))]
    private async Task ExportSelectedAsync()
    {
        var row = SelectedDevice;
        if (row is null) return;
        var device = new ExportDevice(row.Device, row.Name == "-" ? $"Device {row.Instance}" : row.Name,
            Objects.Select(o => o.Summary).ToList());
        await ExportAsync([device], $"{SafeFileName(device.Name)}-points");
    }

    [RelayCommand(CanExecute = nameof(CanExportAll))]
    private async Task ExportAllAsync()
    {
        var choice = PickExportFile("all-devices-points");
        if (choice is null)
        {
            Status = "Export cancelled.";
            return;
        }

        IsExporting = true;
        try
        {
            var (collected, failed) = await CollectAllAsync(new Progress<string>(m => Status = m));
            if (collected.Count == 0)
            {
                Status = "Could not read any device, so nothing was exported. Likely cause: network drop or the devices stopped answering. Next step: check the connection and try again.";
                return;
            }
            await SaveExportAsync(collected, choice.Value, [.. failed.Select(f => $"device {f.Row.Instance} ({f.Reason})")]);
        }
        finally { IsExporting = false; }
    }

    [RelayCommand]
    private async Task SaveJobAsync()
    {
        if (Devices.Count == 0)
        {
            Status = "There is nothing to save yet. Scan for devices first (or open an existing job).";
            return;
        }

        var name = JobName.Trim().Length > 0 ? JobName.Trim() : "BACprobe job";
        var path = PickJobSavePath(SafeFileName(name));
        if (path is null)
        {
            Status = "Save cancelled.";
            return;
        }

        IsExporting = true; // blocks Scan/Export while we read every device
        try
        {
            var (collected, failed) = await CollectAllAsync(new Progress<string>(m => Status = m));
            var saved = collected.Select(c => SavedDevice.From(c.Device, c.Name, c.Objects))
                .Concat(failed.Select(f => SavedDevice.From(f.Row.Device, f.Row.Name == "-" ? $"Device {f.Row.Instance}" : f.Row.Name, null)))
                .OrderBy(d => d.Instance).ToList();

            var sessionLog = _log.Entries;
            var writeLog = _persistedLog.Concat(sessionLog.Skip(_sessionLogSaved)).ToList();
            var info = new JobInfo(name, JobNotes.Trim(), _jobCreated, DateTimeOffset.Now,
                BbmdText.Trim().Length > 0 ? BbmdText.Trim() : null, SelectedAdapter?.Info.Cidr,
                typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "");

            await Task.Run(() => JobFile.Save(path, new JobSnapshot(info, saved, writeLog)));
            _persistedLog = writeLog;
            _sessionLogSaved = sessionLog.Count;

            Status = $"Saved job \"{name}\": {saved.Count} device(s), {saved.Sum(d => d.Objects.Count)} object(s) to {path}.";
            if (failed.Count > 0)
                Status += $" Points NOT saved for: {string.Join(", ", failed.Select(f => $"{f.Row.Instance} ({f.Reason})"))}.";
            ShowResult(Status, path);
        }
        catch (JobFileException ex)
        {
            Status = ex.Message.Replace("\n", " ");
        }
        finally { IsExporting = false; }
    }

    [RelayCommand]
    private async Task OpenJobAsync()
    {
        if (!await ResolveOverridesAsync()) return;
        var path = PickJobOpenPath();
        if (path is null) return;

        JobSnapshot job;
        try { job = await Task.Run(() => JobFile.Load(path)); }
        catch (JobFileException ex)
        {
            Status = ex.Message.Replace("\n", " ");
            return;
        }

        // Opening a job leaves live mode: close the connection and show the saved snapshot.
        IsLive = false;
        CanUseLive = false;
        ConnectionReset?.Invoke();
        _browseCts?.Cancel();
        _svc?.Dispose();
        _svc = null;
        _writer = null;
        ResetBrowsing();

        _baseline = job.Devices;
        _baselineName = job.Info.Name;
        ClearNetworkCheck();
        JobName = job.Info.Name;
        JobNotes = job.Info.Notes;
        if (job.Info.BbmdText is not null) BbmdText = job.Info.BbmdText;
        _jobCreated = job.Info.CreatedAt;
        _persistedLog = [.. job.WriteLog];
        _sessionLogSaved = _log.Entries.Count;
        WriteLogLines.Clear();
        foreach (var e in job.WriteLog) WriteLogLines.Add(e.Text);

        foreach (var d in job.Devices)
        {
            Devices.Add(new DeviceRow(d.ToDiscovered()));
            if (d.PointsRead) _pointCache[d.Instance] = d.ToExportDevice();
        }
        UpdateOverrideSummary();
        OfflineBanner = $"Viewing saved job \"{job.Info.Name}\" from {job.Info.SavedAt.LocalDateTime:yyyy-MM-dd HH:mm}. These values are a snapshot, not live. " +
                        "Click Scan to connect and read live values; the scan will also compare what answers with this job.";
        Status = $"Opened {job.Devices.Count} device(s) from {path}. Select one to see its saved points.";
        ExportAllCommand.NotifyCanExecuteChanged();
    }

    private async Task ExportAsync(IReadOnlyList<ExportDevice> devices, string suggestedName)
    {
        var choice = PickExportFile(suggestedName);
        if (choice is null)
        {
            Status = "Export cancelled.";
            return;
        }

        IsExporting = true;
        try { await SaveExportAsync(devices, choice.Value, []); }
        finally { IsExporting = false; }
    }

    private async Task SaveExportAsync(IReadOnlyList<ExportDevice> devices, (string Path, ExportFormat Format) choice, List<string> failed)
    {
        try
        {
            await Task.Run(() => PointExporter.Write(choice.Path, choice.Format, devices));
            var points = devices.Sum(d => d.Points.Count());
            Status = $"Exported {points} point(s) from {devices.Count} device(s) to {choice.Path}.";
            if (failed.Count > 0) Status += $" NOT included: {string.Join("; ", failed)}.";
            ShowResult(Status, choice.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save the file: {ex.Message} Likely cause: it is open in Excel, or the folder is read-only. Next step: close the file or choose another location.";
        }
    }

    private void ShowResult(string message, string path)
    {
        ResultMessage = message;
        ResultPath = path;
    }

    [RelayCommand]
    private void DismissResult()
    {
        ResultMessage = "";
        ResultPath = "";
    }

    private bool HasResultFile() => ResultPath.Length > 0 && File.Exists(ResultPath);

    [RelayCommand(CanExecute = nameof(HasResultFile))]
    private void OpenResultFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ResultPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Status = $"Could not open the file: {ex.Message} Likely cause: no program is set to open this file type. " +
                     "Next step: use Show in folder and open it from there.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasResultFile))]
    private void ShowResultInFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ResultPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Status = $"Could not open the folder: {ex.Message} The file is at {ResultPath}.";
        }
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    [RelayCommand(CanExecute = nameof(CanWriteSelected))]
    private Task WriteSelectedAsync() => DoWriteAsync(release: false);

    [RelayCommand(CanExecute = nameof(CanWriteSelected))]
    private Task ReleaseSelectedAsync() => DoWriteAsync(release: true);


    private async Task DoWriteAsync(bool release)
    {
        var row = SelectedObject;
        var deviceRow = SelectedDevice;
        if (_writer is null || _svc is null || row is null || deviceRow is null) return;

        var obj = row.Summary;
        BacnetValue? value = null;
        if (!release)
        {
            if (IsStateSelected && SelectedStateChoice is null)
            {
                Status = "Pick the state to write from the Value list.";
                return;
            }
            if (!WriteValueParser.TryParse(obj.Id.type, WriteValueText, obj.StateNames, out var parsed, out var error))
            {
                Status = error;
                return;
            }
            value = parsed;
        }

        var deviceName = deviceRow.Name == "-" ? $"device {deviceRow.Instance}" : deviceRow.Name;
        var request = new WriteRequest(deviceRow.Device, deviceName, obj.Id, obj.Name ?? obj.Label, value,
            value is { } v ? StateText.Describe(obj.Id.type, v, obj.StateNames) : "release", // "Standby (3)", "On (Active)"
            SelectedPriority.Number, obj.ValueText);

        if (!ConfirmWrite(request))
        {
            Status = "Cancelled. Nothing was written.";
            return;
        }

        var outcome = await _writer.ExecuteAsync(request);
        Status = !outcome.Success ? outcome.Message.Replace("\n", " ")
            : outcome.Explanation is not null ? "The device accepted the write, but the point did not change."
            : release ? "Released." : "Written. The override stays in place until you release it.";
        if (outcome.Explanation is { } explanation) ShowExplanation(explanation);
        UpdateOverrideSummary();

        try
        {
            var fresh = (await _svc.OpenDevice(deviceRow.Device).ReadSummariesAsync([obj.Id]))[0];
            obj.UpdateFrom(fresh); // in place: Find and Live hold this same object
            row.Refresh(obj);
            UpdateOverrideSummary();
            await LoadPropertiesAsync(row);
        }
        catch (Exception) { Status += " (Could not read the point back; check it before you leave.)"; }
    }

    /// <summary>
    /// The footer: overrides this session made (the ones BACprobe will offer to release) plus any point on the
    /// device being viewed that is overridden by anyone, so the footer never disagrees with the orange rows.
    /// </summary>
    private void UpdateOverrideSummary()
    {
        var mine = _overrides.Active.Count;
        var onDevice = Objects.Count(o => o.IsOverridden);
        var problems = Objects.Count(o => o.HasProblem);
        var parts = new List<string>();
        if (mine > 0) parts.Add($"{mine} override(s) from this session");
        if (onDevice > 0) parts.Add($"{onDevice} point(s) overridden on this device");
        if (problems > 0) parts.Add($"{problems} point(s) with a problem");
        OverrideSummary = parts.Count == 0 ? "No overrides or problems." : string.Join("  |  ", parts);

        // A point that just went into fault (or got overridden) must join the filtered list, and one that recovered must leave it.
        if (SelectedPointFilter?.Test is { } test)
        {
            System.Windows.Data.CollectionViewSource.GetDefaultView(Objects).Refresh();
            var shown = Objects.Count(o => test(o.Summary));
            PointFilterStatus = shown == 0 && Objects.Count > 0 ? $"None of the {Objects.Count} points" : $"{shown} of {Objects.Count}";
        }
        else PointFilterStatus = "";
    }

    /// <summary>
    /// Before disconnecting: list overrides this session left and offer to release them.
    /// Returns false if the user wants to go back.
    /// </summary>
    public async Task<bool> ResolveOverridesAsync()
    {
        var active = _overrides.Active;
        if (active.Count == 0 || _writer is null) return true;

        switch (AskOverrides(active))
        {
            case OverrideChoice.GoBack:
                return false;
            case OverrideChoice.Release:
                var (_, failed) = await _writer.ReleaseAllAsync();
                UpdateOverrideSummary();
                return failed == 0 || ConfirmContinueAfterFailedRelease(failed);
            default:
                _overrides.Clear(); // left in place on purpose: do not ask about them again
                UpdateOverrideSummary();
                return true;
        }
    }

    public void Dispose()
    {
        StopLive();
        _browseCts?.Cancel();
        _svc?.Dispose();
    }
}
