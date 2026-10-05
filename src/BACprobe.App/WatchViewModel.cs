using System.Collections.ObjectModel;
using System.IO.BACnet;
using System.Windows;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Live;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

/// <summary>One watched point as the list shows it.</summary>
public sealed partial class WatchRow(WatchEntry entry, string deviceName) : ObservableObject
{
    public WatchEntry Entry { get; } = entry;
    public ObjectSummary? Summary { get; private set; }

    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _updated = "";

    public string Device => $"{Entry.Device} - {deviceName}";
    public string Object => $"{BacnetNames.ObjectTypeShort(Entry.Point.type)} {Entry.Point.instance}";
    public string Name => Summary?.Name ?? Entry.Label;
    public string Value => Summary is null ? "-" : Summary.ValueText;
    public string Status => Problem.Length > 0 ? Problem : Summary is null ? "" : Summary.IsOverridden ? Summary.OverrideText : Summary.HasProblem ? Summary.ProblemText : "";

    public void Set(ObjectSummary? summary, string? problem, bool stamp)
    {
        Summary = summary;
        Problem = problem ?? "";
        if (stamp) Updated = DateTime.Now.ToString("HH:mm:ss");
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// The watch list window: the points a tech is keeping an eye on, across devices, kept current with COV where the device
/// supports it and polling for the rest. Reads only. Offline (a saved job) it shows the saved list with the saved values.
/// </summary>
public sealed partial class WatchViewModel : ObservableObject, IDisposable
{
    private readonly WatchList _list;
    private readonly DiscoveryService? _svc;
    private readonly IReadOnlyDictionary<uint, DiscoveredDevice> _devices;
    private readonly IReadOnlyDictionary<uint, string> _names;
    private readonly Func<uint, BacnetObjectId, ObjectSummary?> _saved;
    private readonly LiveOptions _options;
    private readonly Action _changed;
    private CancellationTokenSource? _cts;

    public WatchViewModel(WatchList list, DiscoveryService? svc, IReadOnlyDictionary<uint, DiscoveredDevice> devices,
        IReadOnlyDictionary<uint, string> names, Func<uint, BacnetObjectId, ObjectSummary?> saved, LiveOptions options, Action changed)
    {
        _list = list;
        _svc = svc;
        _devices = devices;
        _names = names;
        _saved = saved;
        _options = options;
        _changed = changed;
    }

    public ObservableCollection<WatchRow> Rows { get; } = [];
    public ObservableCollection<string> DeviceStatuses { get; } = [];

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private WatchRow? _selectedRow;

    private string NameOf(uint device) => _names.TryGetValue(device, out var n) && n != "-" ? n : $"device {device}";

    /// <summary>Build the rows and, when connected, read them once and keep them live.</summary>
    public async Task StartAsync()
    {
        Stop();
        Rows.Clear();
        DeviceStatuses.Clear();
        foreach (var e in _list.Items) Rows.Add(new WatchRow(e, NameOf(e.Device)));
        if (Rows.Count == 0)
        {
            Status = "The watch list is empty. Select a point in the main window and press Watch (or right-click it) to add it.";
            return;
        }

        if (_svc is null)
        {
            foreach (var row in Rows) row.Set(_saved(row.Entry.Device, row.Entry.Point), null, stamp: false);
            Status = $"{Rows.Count} point(s) from the saved job. These are the saved values, not live; Scan to connect and watch them live.";
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        Status = $"Reading {Rows.Count} point(s) across {_list.ByDevice().Count} device(s)...";
        var session = new WatchSession(_svc, _devices, _options);
        try
        {
            var readings = await session.ReadAsync(_list.Items, cts.Token);
            if (cts.IsCancellationRequested) return;
            var byEntry = Rows.ToDictionary(r => r.Entry);
            foreach (var r in readings)
                if (byEntry.TryGetValue(r.Entry, out var row)) row.Set(r.Summary, r.Problem, stamp: r.Summary is not null);

            var summaries = Rows.Where(r => r.Summary is not null).ToDictionary(r => r.Summary!);
            var bad = readings.Count(r => r.Summary is null);
            Status = bad == 0 ? $"Watching {summaries.Count} point(s) live." : $"Watching {summaries.Count} point(s); {bad} could not be read (see the Status column).";

            session.PointChanged += (device, s) => Post(() =>
            {
                if (cts.IsCancellationRequested || !summaries.TryGetValue(s, out var row)) return;
                row.Set(s, null, stamp: true);
            });
            session.DeviceStatus += (device, text) => Post(() => SetDeviceStatus(cts, device, text));
            _ = Task.Run(() => session.RunAsync(readings, cts.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = $"Could not start the watch: {ex.Message} Likely cause: the network dropped. Next step: Scan again, then reopen the list.";
        }
    }

    private void SetDeviceStatus(CancellationTokenSource cts, uint device, string text)
    {
        if (cts.IsCancellationRequested) return;
        var prefix = $"{device} {NameOf(device)}: ";
        for (var i = 0; i < DeviceStatuses.Count; i++)
            if (DeviceStatuses[i].StartsWith(prefix, StringComparison.Ordinal)) { DeviceStatuses[i] = prefix + text; return; }
        DeviceStatuses.Add(prefix + text);
    }

    private static void Post(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) a(); else d.BeginInvoke(a);
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        if (SelectedRow is not { } row) return;
        _list.Remove(row.Entry.Device, row.Entry.Point);
        _changed();
        await StartAsync();
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        _list.Clear();
        _changed();
        await StartAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => StartAsync();

    public void Dispose() => Stop();
}
