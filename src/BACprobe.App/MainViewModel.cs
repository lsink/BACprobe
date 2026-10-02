using System.Collections.ObjectModel;
using System.IO;
using BACprobe.Core.Bbmd;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Export;
using BACprobe.Core.Jobs;
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

public sealed class DeviceRow(DiscoveredDevice d)
{
    public DiscoveredDevice Device => d;
    public uint Instance => d.InstanceId;
    public string Address => d.AddressText;
    public string Vendor => d.VendorName ?? $"vendor {d.VendorId}";
    public string Model => d.ModelName ?? "-";
    public string Firmware => d.FirmwareRevision ?? "-";
    public string Name => d.ObjectName ?? "-";
}

public sealed class ObjectRow(ObjectSummary s) : ObservableObject
{
    private ObjectSummary _s = s;
    public ObjectSummary Summary => _s;
    public string Id => $"{BacnetNames.ObjectTypeShort(_s.Id.type)} {_s.Id.instance}";
    public string Type => _s.TypeName;
    public string Name => _s.Name ?? "-";
    public string Value => _s.ValueText;
    public string Description => _s.Description ?? "";

    /// <summary>Swap in freshly read values (e.g. after a write) without losing the grid selection.</summary>
    public void Refresh(ObjectSummary fresh)
    {
        _s = fresh;
        OnPropertyChanged(string.Empty);
    }
}

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

    /// <summary>Set by the window: shows a yes/no box, returns true for yes.</summary>
    public Func<string, string, bool> Confirm { get; set; } = (_, _) => false;

    /// <summary>Set by the window: shows a Save dialog (suggested file name in); null if the user cancels.</summary>
    public Func<string, (string Path, ExportFormat Format)?> PickExportFile { get; set; } = _ => null;

    /// <summary>Set by the window: Save dialog for a job file (suggested name in); null if cancelled.</summary>
    public Func<string, string?> PickJobSavePath { get; set; } = _ => null;

    /// <summary>Set by the window: Open dialog for a job file; null if cancelled.</summary>
    public Func<string?> PickJobOpenPath { get; set; } = () => null;

    /// <summary>Set by the window: shows a yes/no/cancel box.</summary>
    public Func<string, string, MessageBoxResult> AskYesNoCancel { get; set; } = (_, _) => MessageBoxResult.Cancel;

    public IReadOnlyList<PriorityChoice> PriorityChoices { get; } = PriorityChoice.All;

    /// <summary>True when this session has left overrides on a device that we can still talk to.</summary>
    public bool HasOverrides => _writer is not null && _overrides.Active.Count > 0;
    public ObservableCollection<string> WriteLogLines { get; } = [];

    public ObservableCollection<AdapterChoice> Adapters { get; } = [];
    public ObservableCollection<PreflightRow> Preflight { get; } = [];
    public ObservableCollection<DeviceRow> Devices { get; } = [];
    public ObservableCollection<ObjectRow> Objects { get; } = [];
    public ObservableCollection<PropertyRow> Properties { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
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
    [ObservableProperty] private string _overrideSummary = "No overrides in place.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteSelectedCommand), nameof(ReleaseSelectedCommand))]
    private bool _canWriteSelected;

    [ObservableProperty] private DeviceRow? _selectedDevice;
    [ObservableProperty] private ObjectRow? _selectedObject;
    [ObservableProperty] private string _propertiesHeader = "Properties";
    [ObservableProperty] private string _jobName = "";
    [ObservableProperty] private string _jobNotes = "";
    [ObservableProperty] private string _offlineBanner = "";
    [ObservableProperty] private string _bbmdText = "";
    [ObservableProperty] private string _ttlText = BbmdTarget.DefaultTtlSeconds.ToString();
    [ObservableProperty] private string _bbmdStatus = "";
    [ObservableProperty] private string _lowText = "";
    [ObservableProperty] private string _highText = "";
    [ObservableProperty] private string _status = "Pick the network adapter that is plugged into the building network.";

    public MainViewModel()
    {
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

    partial void OnSelectedAdapterChanged(AdapterChoice? value) => RunPreflight();

    [RelayCommand]
    private void RunPreflight()
    {
        Preflight.Clear();
        PreflightPassed = false;
        if (SelectedAdapter is null) return;
        var results = Core.Networking.Preflight.Run(SelectedAdapter.Info);
        foreach (var r in results) Preflight.Add(new PreflightRow(r));
        PreflightPassed = PreflightRules.CanProceed(results);
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
                Status = "No devices answered. Likely cause: wrong adapter or subnet, a firewall blocking UDP 47808, " +
                         "or devices on another subnet behind a BBMD. Next step: try another adapter or allow BACprobe through Windows Firewall.";
                return;
            }
            Status = $"{found.Count} device(s) found. Reading details...";
            await svc.EnrichAsync(found);
            foreach (var d in found) Devices.Add(new DeviceRow(d));
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
        Objects.Clear();
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
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                Status = $"Could not read {name}: {ex.Message}. Likely cause: network drop, a busy controller, or a router dropping the request. " +
                         "Next step: check the connection and select the device again.";
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
        ExportSelectedCommand.NotifyCanExecuteChanged();
        Status = $"{saved.Objects.Count} saved objects in {saved.Name}. These are the values from when the job was saved; Scan to read live.";
    }

    partial void OnSelectedObjectChanged(ObjectRow? value) => _ = LoadPropertiesAsync(value);

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
        _browseCts?.Cancel();
        _svc?.Dispose();
        _svc = null;
        _writer = null;
        ResetBrowsing();

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
                        "Click Scan to connect and read live values.";
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save the file: {ex.Message} Likely cause: it is open in Excel, or the folder is read-only. Next step: close the file or choose another location.";
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
            if (!WriteValueParser.TryParse(obj.Id.type, WriteValueText, out var parsed, out var error))
            {
                Status = error;
                return;
            }
            value = parsed;
        }

        var deviceName = deviceRow.Name == "-" ? $"device {deviceRow.Instance}" : deviceRow.Name;
        var request = new WriteRequest(deviceRow.Device, deviceName, obj.Id, obj.Name ?? obj.Label, value,
            release ? "release" : WriteValueText.Trim(), SelectedPriority.Number, obj.ValueText);

        if (!Confirm(release ? "Release override" : "Confirm write", request.ConfirmationText()))
        {
            Status = "Cancelled. Nothing was written.";
            return;
        }

        var outcome = await _writer.ExecuteAsync(request);
        Status = outcome.Success
            ? (release ? "Released." : "Written. The override stays in place until you release it.")
            : outcome.Message.Replace("\n", " ");
        UpdateOverrideSummary();

        try
        {
            var fresh = (await _svc.OpenDevice(deviceRow.Device).ReadSummariesAsync([obj.Id]))[0];
            row.Refresh(fresh);
            await LoadPropertiesAsync(row);
        }
        catch (Exception) { Status += " (Could not read the point back; check it before you leave.)"; }
    }

    private void UpdateOverrideSummary()
    {
        var n = _overrides.Active.Count;
        OverrideSummary = n == 0 ? "No overrides in place." : $"{n} override(s) in place from this session.";
    }

    /// <summary>
    /// Before disconnecting: list overrides this session left and offer to release them.
    /// Returns false if the user wants to go back.
    /// </summary>
    public async Task<bool> ResolveOverridesAsync()
    {
        var active = _overrides.Active;
        if (active.Count == 0 || _writer is null) return true;

        var text = $"This session left {active.Count} override(s) in place:\n\n" +
                   string.Join("\n", active.Select(o => "  - " + o.Description)) +
                   "\n\nRelease them now?\nYes = release them, No = leave them in place, Cancel = go back.";
        switch (AskYesNoCancel("Overrides still in place", text))
        {
            case MessageBoxResult.Cancel:
                return false;
            case MessageBoxResult.Yes:
                var (_, failed) = await _writer.ReleaseAllAsync();
                UpdateOverrideSummary();
                return failed == 0 || Confirm("Some overrides could not be released",
                    $"{failed} override(s) could not be released (see the write log). They are still in place on the device.\n\nContinue anyway?");
            default:
                return true;
        }
    }

    public void Dispose()
    {
        _browseCts?.Cancel();
        _svc?.Dispose();
    }
}
