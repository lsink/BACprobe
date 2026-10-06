using System.Collections.ObjectModel;
using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Export;
using BACprobe.Core.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

/// <summary>One result: a point from the devices read so far, or a device's answer to Who-Has.</summary>
public sealed class FindRow
{
    public FindRow(PointHit hit)
    {
        DeviceInstance = hit.DeviceInstance;
        Point = hit.Point.Id;
        Device = $"{hit.DeviceInstance}  {hit.DeviceName}";
        Name = hit.Point.Name ?? "-";
        Value = hit.Point.ValueText;
        Override = hit.Point.OverrideText;
        Description = hit.Point.Description ?? "";
        Status = hit.Point.ProblemText;
        StatusTooltip = hit.Point.ProblemTooltip;
    }

    /// <summary>An I-Have: the device says it has the point; nothing else is known until it is read.</summary>
    public FindRow(IHaveReply reply)
    {
        DeviceInstance = reply.DeviceInstance;
        Point = reply.Point;
        Device = $"{reply.DeviceInstance}  ({reply.AddressText})";
        Name = reply.ObjectName;
        Description = "answered Who-Has";
    }

    public uint DeviceInstance { get; }
    public BacnetObjectId Point { get; }
    public string Device { get; }
    public string PointId => $"{BacnetNames.ObjectTypeShort(Point.type)} {Point.instance}";
    public string Name { get; }
    public string Value { get; } = "";
    public string Override { get; } = "";
    public string Description { get; }
    public string Status { get; } = "";
    public string StatusTooltip { get; } = "";
}

/// <summary>The Find window: type words, see matching points across every device read so far, jump to one.</summary>
public sealed partial class FindViewModel(
    Func<IReadOnlyList<ExportDevice>> getIndex,
    Func<int> deviceCount,
    Func<IProgress<string>, Task<int>> readAllDevices,
    Func<uint, BacnetObjectId, Task> goTo,
    Func<string?, BacnetObjectId?, Task<IReadOnlyList<IHaveReply>>>? askNetwork = null) : ObservableObject
{
    private const int MaxShown = 500;

    public ObservableCollection<FindRow> Results { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private FindRow? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadAllCommand), nameof(AskNetworkCommand))]
    private bool _isBusy;

    partial void OnQueryChanged(string value) => Search();

    /// <summary>Re-run the search against whatever has been read so far.</summary>
    public void Search()
    {
        var index = getIndex();
        var points = index.Sum(d => d.Points.Count());
        var unread = Math.Max(0, deviceCount() - index.Count);

        Results.Clear();
        var hits = PointSearch.Search(index, Query, MaxShown + 1);
        foreach (var h in hits.Take(MaxShown)) Results.Add(new FindRow(h));

        var scope = $"{points} points on {index.Count} device(s)";
        var hint = unread > 0 ? $" {unread} device(s) have not been read yet: click \"Read all devices\" to include them." : "";
        Status = string.IsNullOrWhiteSpace(Query)
            ? $"Type words to find points. Searching {scope}.{hint}"
            : hits.Count == 0
                ? $"No points match. Searched {scope}.{hint}"
                : $"{(hits.Count > MaxShown ? $"{MaxShown}+" : hits.Count.ToString())} match(es). Searched {scope}.{hint}";
    }

    private bool CanReadAll() => !IsBusy;

    private bool CanAskNetwork() => !IsBusy && askNetwork is not null;

    /// <summary>
    /// Who-Has: ask every device whether it has a point with exactly this name (or this object, e.g. ai:1), without reading them.
    /// Their answers replace the list.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAskNetwork))]
    private async Task AskNetworkAsync()
    {
        var text = Query.Trim().Trim('"');
        if (text.Length == 0 || text.Contains("is:", StringComparison.OrdinalIgnoreCase))
        {
            Status = "Type an exact point name (e.g. Zone Temp) or an object (e.g. ai:1) to ask the network.";
            return;
        }
        var (id, name) = IHaveCodec.ParseQuery(text);
        IsBusy = true;
        Status = $"Asking the network who has {(id is { } o ? BacnetNames.ObjectLabel(o) : $"\"{name}\"")}...";
        try
        {
            var replies = await askNetwork!(name, id);
            Results.Clear();
            foreach (var r in replies) Results.Add(new FindRow(r));
            Status = replies.Count == 0
                ? "No device said it has it. Likely cause: the name must match exactly (case and spaces), or the device is on another subnet " +
                  "(Who-Has does not go through a BBMD). Next step: try the object (e.g. ai:1), or Read all devices and search."
                : $"{replies.Count} answer(s) to Who-Has from {replies.Select(r => r.DeviceInstance).Distinct().Count()} device(s). Double-click one to go to it.";
        }
        catch (Exception ex)
        {
            Status = $"Could not ask the network: {ex.Message} Likely cause: the connection was closed. Next step: Scan again.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanReadAll))]
    private async Task ReadAllAsync()
    {
        IsBusy = true;
        try
        {
            var read = await readAllDevices(new Progress<string>(m => Status = m));
            Search();
            if (read == 0) Status = "Could not read any device. Likely cause: the connection dropped. Next step: scan again.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task GoToAsync()
    {
        if (Selected is null) return;
        await goTo(Selected.DeviceInstance, Selected.Point);
    }
}
