using System.Collections.ObjectModel;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Networking;
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

public sealed class ObjectRow(ObjectSummary s)
{
    public ObjectSummary Summary => s;
    public string Id => $"{BacnetNames.ObjectTypeShort(s.Id.type)} {s.Id.instance}";
    public string Type => s.TypeName;
    public string Name => s.Name ?? "-";
    public string Value => s.ValueText;
    public string Description => s.Description ?? "";
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private DiscoveryService? _svc;
    private CancellationTokenSource? _browseCts;

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

    [ObservableProperty] private DeviceRow? _selectedDevice;
    [ObservableProperty] private ObjectRow? _selectedObject;
    [ObservableProperty] private string _propertiesHeader = "Properties";
    [ObservableProperty] private string _lowText = "";
    [ObservableProperty] private string _highText = "";
    [ObservableProperty] private string _status = "Pick the network adapter that is plugged into the building network.";

    public MainViewModel() => RefreshAdapters();

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

    private bool CanScan() => SelectedAdapter is not null && PreflightPassed && !IsScanning;

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

        IsScanning = true;
        ResetBrowsing();
        Status = "Sending Who-Is...";
        try
        {
            _svc?.Dispose();
            _svc = null;
            var svc = new DiscoveryService(SelectedAdapter!.Info);
            svc.Start();
            _svc = svc;
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
            Status = $"{found.Count} device(s) found. Select one to see its objects.";
        }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message}. Likely cause: another program holds UDP 47808 or the adapter address changed. " +
                     "Next step: close other BACnet tools and click Re-check.";
        }
        finally { IsScanning = false; }
    }

    private void ResetBrowsing()
    {
        _browseCts?.Cancel();
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
        if (row is null || _svc is null) return;

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
            foreach (var s in summaries) Objects.Add(new ObjectRow(s));
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

    partial void OnSelectedObjectChanged(ObjectRow? value) => _ = LoadPropertiesAsync(value);

    private async Task LoadPropertiesAsync(ObjectRow? row)
    {
        Properties.Clear();
        if (row is null || SelectedDevice is null || _svc is null)
        {
            PropertiesHeader = "Properties";
            return;
        }

        var label = BacnetNames.ObjectLabel(row.Summary.Id);
        PropertiesHeader = $"Properties - {label}";
        try
        {
            var rows = await _svc.OpenDevice(SelectedDevice.Device).ReadAllPropertiesAsync(row.Summary.Id, _browseCts?.Token ?? default);
            if (!ReferenceEquals(SelectedObject, row)) return; // selection moved on while reading
            foreach (var r in rows) Properties.Add(r);
            if (rows.Count == 0)
                Status = $"The device returned no properties for {label}. Likely cause: the object was removed. Next step: select the device again.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = $"Could not read {label}: {ex.Message}. Likely cause: the device refused the request or stopped answering. Next step: try again.";
        }
    }

    public void Dispose()
    {
        _browseCts?.Cancel();
        _svc?.Dispose();
    }
}
