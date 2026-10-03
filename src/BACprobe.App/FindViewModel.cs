using System.Collections.ObjectModel;
using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Export;
using BACprobe.Core.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

public sealed class FindRow(PointHit hit)
{
    public PointHit Hit => hit;
    public string Device => $"{hit.DeviceInstance}  {hit.DeviceName}";
    public string PointId => $"{BacnetNames.ObjectTypeShort(hit.Point.Id.type)} {hit.Point.Id.instance}";
    public string Name => hit.Point.Name ?? "-";
    public string Value => hit.Point.ValueText;
    public string Override => hit.Point.OverrideText;
    public string Description => hit.Point.Description ?? "";
    public string Status => hit.Point.ProblemText;
    public string StatusTooltip => hit.Point.ProblemTooltip;
}

/// <summary>The Find window: type words, see matching points across every device read so far, jump to one.</summary>
public sealed partial class FindViewModel(
    Func<IReadOnlyList<ExportDevice>> getIndex,
    Func<int> deviceCount,
    Func<IProgress<string>, Task<int>> readAllDevices,
    Func<uint, BacnetObjectId, Task> goTo) : ObservableObject
{
    private const int MaxShown = 500;

    public ObservableCollection<FindRow> Results { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private FindRow? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadAllCommand))]
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
        await goTo(Selected.Hit.DeviceInstance, Selected.Hit.Point.Id);
    }
}
