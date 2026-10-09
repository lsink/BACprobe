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

// Browsing the selected device: its points, the device's own folders, live values and the properties panel.
public sealed partial class MainViewModel
{
    // The device's own folders (Structured Views), when it has any: shown instead of the flat list when Folders is on.
    public ObservableCollection<StructureNode> Structure { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    private bool _showTree;

    public bool ShowList => !ShowTree;
    public bool HasStructure => Structure.Count > 0;

    /// <summary>A point picked in the folder tree: select it, as if it had been picked in the list.</summary>
    public void SelectFromTree(StructureNode? node)
    {
        if (node?.Point is null) return;
        if (Objects.FirstOrDefault(o => o.Summary.Id.Equals(node.Id)) is { } row) SelectedObject = row;
    }

    private async Task LoadStructureAsync(DeviceBrowser browser, IReadOnlyList<BacnetObjectId> ids, IReadOnlyList<ObjectSummary> summaries,
        CancellationToken ct)
    {
        if (!ids.Any(i => i.type == BacnetObjectTypes.OBJECT_STRUCTURED_VIEW)) return;
        try
        {
            var tree = StructureTree.Build(summaries, await browser.ReadStructuredViewsAsync(ids, ct));
            if (ct.IsCancellationRequested) return;
            foreach (var node in tree) Structure.Add(node);
            OnPropertyChanged(nameof(HasStructure));
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* the flat list still has every point */ }
    }

    private async Task LoadObjectsAsync(DeviceRow? row)
    {
        _browseCts?.Cancel();
        StopLive();
        Objects.Clear();
        Structure.Clear();
        OnPropertyChanged(nameof(HasStructure));
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
        var name = row.DisplayName;
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
            _pointCache[row.Instance] = new ExportDevice(row.Device, row.ExportName, summaries);
            foreach (var s in summaries) Objects.Add(new ObjectRow(s));
            ExportSelectedCommand.NotifyCanExecuteChanged();
            Status = $"{ids.Count} objects in {name}. Select one to see its properties.";
            await LoadStructureAsync(browser, ids, summaries, cts.Token);
            if (!HasStructure) ShowTree = false;
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
        watcher.StatusChanged += text => UiThread.Post(() =>
        {
            if (!cts.IsCancellationRequested) LiveStatus = text;
        });
        watcher.PointChanged += summary => UiThread.Post(() =>
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
        // Off the UI thread: the watch loop decodes every poll and notification. Its events already hop back via UiThread.
        var failure = await Task.Run(() => watcher.RunAsync(ct));
        if (failure is null || ct.IsCancellationRequested) return;
        // The device stopped answering: stop and say why, instead of showing stale numbers as if they were live.
        IsLive = false;
        Status = failure;
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
        WatchSelectedCommand.NotifyCanExecuteChanged();
        Note.Target(SelectedDevice, SelectedObject);
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
        CanSetOutOfService = false;
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
            IsPointOutOfService = row.Summary.IsOutOfService;
            CanSetOutOfService = rows.Any(r => r.PropertyId == (uint)BacnetPropertyIds.PROP_OUT_OF_SERVICE);
            if (rows.Count == 0)
                Status = $"The device returned no properties for {label}. Likely cause: the object was removed. Next step: select the device again.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = $"Could not read {label}: {ex.Message}. Likely cause: the device refused the request or stopped answering. Next step: try again.";
        }
    }
}
