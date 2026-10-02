using System.Collections.ObjectModel;
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
    public uint Instance { get; } = d.InstanceId;
    public string Address { get; } = d.AddressText;
    public string Vendor => d.VendorName ?? $"vendor {d.VendorId}";
    public string Model => d.ModelName ?? "-";
    public string Firmware => d.FirmwareRevision ?? "-";
    public string Name => d.ObjectName ?? "-";
}

public sealed partial class MainViewModel : ObservableObject
{
    public ObservableCollection<AdapterChoice> Adapters { get; } = [];
    public ObservableCollection<PreflightRow> Preflight { get; } = [];
    public ObservableCollection<DeviceRow> Devices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private AdapterChoice? _selectedAdapter;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _preflightPassed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isScanning;

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
        Devices.Clear();
        Status = "Sending Who-Is...";
        try
        {
            using var svc = new DiscoveryService(SelectedAdapter!.Info);
            svc.Start();
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
            Status = $"{found.Count} device(s) found.";
        }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message}. Likely cause: another program holds UDP 47808 or the adapter address changed. " +
                     "Next step: close other BACnet tools and click Re-check.";
        }
        finally { IsScanning = false; }
    }
}
