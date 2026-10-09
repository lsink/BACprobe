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

// The tool windows the main window opens: Find, trends, quick-copy, BBMD check, watch list, compare, alarms, MS/TP monitor.
public sealed partial class MainViewModel
{
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
        goTo: GoToPointAsync,
        askNetwork: _svc is null ? null : (name, id) => _svc.WhoHasAsync(name, id, TimeSpan.FromSeconds(3))));

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
        var name = device.ExportName;
        ShowLiveTrend(new LiveTrendViewModel(_svc.OpenDevice(device.Device), SelectedObject.Summary, name, device.Instance, PickTrendFile));
    }

    [RelayCommand(CanExecute = nameof(IsTrendSelected))]
    private void ViewTrend()
    {
        if (_svc is null || SelectedObject is null || SelectedDevice is null) return;
        var device = SelectedDevice;
        var name = device.ExportName;
        ShowTrend(new TrendViewModel(_svc.OpenTrendLogs(device.Device), SelectedObject.Summary.Id, name, device.Instance, PickTrendFile));
    }
    // Quick-copy (right-click a device or point): things a tech pastes into a ticket, a chat or Wireshark.
    private void CopyText(string? text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            Status = $"Nothing to copy for {what}. Likely cause: this device has no IP address (it is behind a router or on MS/TP). Next step: copy the router's address instead.";
            return;
        }
        try
        {
            Clipboard.SetText(text);
            Status = $"Copied {what}: {text}";
        }
        catch (Exception ex)
        {
            Status = $"Could not copy: {ex.Message} Likely cause: another program is holding the clipboard. Next step: try again.";
        }
    }

    [RelayCommand] private void CopyDeviceAddress() => CopyText(SelectedDevice?.Address, "the device address");

    [RelayCommand]
    private void CopyDeviceFilter() =>
        CopyText(SelectedDevice is { } d ? QuickCopy.WiresharkFilterForAddress(d.Address) : null, "a Wireshark filter for the device");

    [RelayCommand]
    private void CopyObjectId() =>
        CopyText(SelectedObject is { } o ? $"{BacnetNames.ObjectTypeShort(o.Summary.Id.type)}:{o.Summary.Id.instance}" : null, "the object ID");

    [RelayCommand] private void CopyObjectName() => CopyText(SelectedObject?.Summary.Name, "the point name");

    [RelayCommand] private void CopyObjectValue() => CopyText(SelectedObject?.Value, "the value");

    [RelayCommand]
    private void CopyObjectFilter() =>
        CopyText(SelectedDevice is { } d && SelectedObject is { } o
            ? QuickCopy.WiresharkFilterForObject(d.Address, (uint)o.Summary.Id.type, o.Summary.Id.instance) : null,
            "a Wireshark filter for the point");


    /// <summary>Set by the window: opens the BBMD check.</summary>
    public Action<BbmdCheckViewModel> ShowBbmdCheck { get; set; } = _ => { };

    // Watch list: points to keep an eye on across devices, saved with the job.
    public WatchList Watch { get; } = new();
    public string WatchButtonText => Watch.Count > 0 ? $"Watch list ({Watch.Count})..." : "Watch list...";

    /// <summary>Set by the window: opens the watch list.</summary>
    public Action<WatchViewModel> ShowWatch { get; set; } = _ => { };

    private bool CanWatchSelected() => SelectedDevice is not null && SelectedObject is not null;

    /// <summary>Put the selected point on the watch list (or take it off if it is already there).</summary>
    [RelayCommand(CanExecute = nameof(CanWatchSelected))]
    private void WatchSelected()
    {
        if (SelectedDevice is not { } d || SelectedObject is not { } o) return;
        var id = o.Summary.Id;
        var label = o.Summary.Name ?? o.Summary.Label;
        if (Watch.Contains(d.Instance, id))
        {
            Watch.Remove(d.Instance, id);
            Status = $"Took {label} off the watch list.";
        }
        else if (Watch.Add(d.Instance, id, label))
            Status = $"Added {label} on device {d.Instance} to the watch list ({Watch.Count} point(s)). Open Watch list... to see them live.";
        else
            Status = $"The watch list is full ({WatchList.MaxEntries} points). Remove some first.";
        OnPropertyChanged(nameof(WatchButtonText));
    }

    [RelayCommand]
    private void OpenWatch()
    {
        ShowWatch(new WatchViewModel(Watch, _svc, Devices.ToDictionary(r => r.Instance, r => r.Device), Devices.ToDictionary(r => r.Instance, r => r.Name),
            saved: (device, id) => _pointCache.TryGetValue(device, out var saved) ? saved.Objects.FirstOrDefault(s => s.Id == id) : null,
            new LiveOptions(TimeSpan.FromSeconds(LiveIntervalSeconds), UseCov),
            changed: () => OnPropertyChanged(nameof(WatchButtonText))));
    }

    /// <summary>Set by the window: opens the device comparison.</summary>
    public Action<CompareViewModel> ShowCompare { get; set; } = _ => { };

    /// <summary>Line two devices up point by point to find what is set differently. Reads only.</summary>
    [RelayCommand]
    private void OpenCompare()
    {
        if (Devices.Count < 2)
        {
            Status = "Comparing needs two devices. Scan first (or open a saved job with two or more devices).";
            return;
        }
        ShowCompare(new CompareViewModel(Devices.ToList(), SelectedDevice,
            getPoints: async (row, progress) =>
            {
                if (_svc is null)
                {
                    // Offline (a saved job): use what was saved.
                    if (_pointCache.TryGetValue(row.Instance, out var saved)) return saved.Objects;
                    throw new InvalidOperationException($"{row.Label}'s points were not read when the job was saved.");
                }
                var read = await PointExporter.CollectAsync(_svc.OpenDevice(row.Device), row.Device, progress);
                _pointCache[row.Instance] = read;
                return read.Objects;
            },
            getProps: _svc is null ? null : (row, id) => _svc.OpenDevice(row.Device).ReadAllPropertiesAsync(id)));
    }

    /// <summary>Set by the window: opens the alarm list.</summary>
    public Action<AlarmsViewModel> ShowAlarms { get; set; } = _ => { };

    /// <summary>Set by the window: confirm acknowledging an alarm.</summary>
    public Func<AlarmAckRequest, bool> ConfirmAck { get; set; } = _ => false;

    /// <summary>Every device's active and unacknowledged alarms, read live (a saved job does not hold alarms).</summary>
    [RelayCommand]
    private void OpenAlarms()
    {
        if (_svc is null || Devices.Count == 0)
        {
            Status = "Alarms are read live from the devices. Scan first (a saved job does not store alarms).";
            return;
        }
        ShowAlarms(new AlarmsViewModel([.. Devices.Select(d => d.Device)], _svc.CreateAlarmReader(), AcknowledgeAlarmAsync, GoToPointAsync,
            readOnly: () => ReadOnlyMode, live: _writer is null ? null : new AlarmLiveSession(_svc, _writer, r => ConfirmAlarmListen(r))));
    }

    /// <summary>Set by the window: confirm adding BACprobe to these devices' alarm recipient lists.</summary>
    public Func<IReadOnlyList<AlarmListenRequest>, bool> ConfirmAlarmListen { get; set; } = _ => false;


    /// <summary>Acknowledge one alarm: refused in read-only mode, confirmed in plain English, sent and logged by the writer.</summary>
    private async Task<WriteOutcome> AcknowledgeAlarmAsync(AlarmAckRequest request)
    {
        if (_writer is null)
            return new WriteOutcome(false, "Not connected, so nothing was sent. Next step: Scan again, then Refresh the alarm list.");
        if (ReadOnlyMode)
            return new WriteOutcome(false, "Read-only mode is on, so nothing was sent. Next step: untick Read-only at the top of the main window to acknowledge alarms.");
        if (!ConfirmAck(request)) return new WriteOutcome(false, "Cancelled. Nothing was sent.");
        return await _writer.AcknowledgeAsync(request);
    }

    /// <summary>Set by the window: opens the MS/TP monitor.</summary>
    public Action<MstpViewModel> ShowMstp { get; set; } = _ => { };

    /// <summary>Listen to an MS/TP trunk through a USB-RS485 adapter (receive-only). Needs no scan and no IP network.</summary>
    [RelayCommand]
    private void OpenMstp() => ShowMstp(new MstpViewModel());

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
}
