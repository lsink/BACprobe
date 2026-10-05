using System.Collections.ObjectModel;
using System.IO.BACnet;
using BACprobe.Core.Browsing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

/// <summary>
/// Compare two devices point by point, and one point's properties on both: what is set differently, what is missing on one.
/// Reads only; nothing is written to either device.
/// </summary>
public sealed partial class CompareViewModel : ObservableObject
{
    private readonly Func<DeviceRow, IProgress<string>, Task<IReadOnlyList<ObjectSummary>>> _getPoints;
    private readonly Func<DeviceRow, BacnetObjectId, Task<IReadOnlyList<PropertyRow>>>? _getProps;
    private IReadOnlyList<PointCompareRow> _all = [];

    public CompareViewModel(IReadOnlyList<DeviceRow> devices, DeviceRow? first,
        Func<DeviceRow, IProgress<string>, Task<IReadOnlyList<ObjectSummary>>> getPoints,
        Func<DeviceRow, BacnetObjectId, Task<IReadOnlyList<PropertyRow>>>? getProps)
    {
        Devices = devices;
        _getPoints = getPoints;
        _getProps = getProps;
        _selectedA = first ?? devices.FirstOrDefault();
        _selectedB = devices.FirstOrDefault(d => !ReferenceEquals(d, _selectedA));
    }

    public IReadOnlyList<DeviceRow> Devices { get; }
    public ObservableCollection<PointCompareRow> Rows { get; } = [];
    public ObservableCollection<PropertyCompareRow> PropertyRows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    private DeviceRow? _selectedA;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    private DeviceRow? _selectedB;

    [ObservableProperty] private bool _differencesOnly = true;
    [ObservableProperty] private bool _ignoreLiveInputs = true;
    [ObservableProperty] private string _summary = "Pick two devices and press Compare. Nothing is written to either.";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _propertyHeader = "Select a point to compare its properties on both devices.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    private bool _isBusy;

    [ObservableProperty] private PointCompareRow? _selectedRow;

    private bool CanCompare() => !IsBusy && SelectedA is not null && SelectedB is not null && !ReferenceEquals(SelectedA, SelectedB);

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareAsync()
    {
        var a = SelectedA!;
        var b = SelectedB!;
        IsBusy = true;
        Rows.Clear();
        PropertyRows.Clear();
        Summary = "";
        try
        {
            var progress = new Progress<string>(s => Status = s);
            Status = $"Reading {a.Label} and {b.Label}...";
            var both = await Task.WhenAll(_getPoints(a, progress), _getPoints(b, progress)); // two devices, read at the same time
            _all = Comparison.ComparePoints(both[0], both[1]);
            Status = $"Compared {a.Label} (A) with {b.Label} (B).";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _all = [];
            Summary = "";
            Status = $"Could not compare: {ex.Message} Likely cause: a device stopped answering, or you are viewing a saved job with no connection. " +
                     "Next step: close this window, Scan again, and retry.";
        }
        finally { IsBusy = false; }
    }

    partial void OnDifferencesOnlyChanged(bool value) => ApplyFilter();
    partial void OnIgnoreLiveInputsChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var r in Comparison.Visible(_all, IgnoreLiveInputs, DifferencesOnly)) Rows.Add(r);
        Summary = _all.Count == 0 ? "" : Comparison.Summarise(_all, IgnoreLiveInputs);
    }

    partial void OnSelectedRowChanged(PointCompareRow? value) => _ = LoadPropertiesAsync(value);

    private async Task LoadPropertiesAsync(PointCompareRow? row)
    {
        PropertyRows.Clear();
        if (row is null || SelectedA is null || SelectedB is null) return;
        if (_getProps is null)
        {
            PropertyHeader = "Properties are only read while connected. Scan to connect, then compare again.";
            return;
        }
        if (row.Kind is CompareKind.OnlyInA or CompareKind.OnlyInB)
        {
            PropertyHeader = $"{row.Object} exists only on {(row.Kind == CompareKind.OnlyInA ? "A" : "B")}, so there is nothing to line up.";
            return;
        }

        PropertyHeader = $"Reading {row.Object} ({row.Name}) on both devices...";
        try
        {
            // The device object is a different instance on each device; the point is the same on both.
            BacnetObjectId IdFor(DeviceRow d) => row.Id.type == BacnetObjectTypes.OBJECT_DEVICE ? new BacnetObjectId(row.Id.type, d.Instance) : row.Id;
            var both = await Task.WhenAll(_getProps(SelectedA, IdFor(SelectedA)), _getProps(SelectedB, IdFor(SelectedB)));
            if (!ReferenceEquals(SelectedRow, row)) return; // the selection moved on while reading
            var rows = Comparison.CompareProperties(both[0], both[1]);
            foreach (var r in rows) PropertyRows.Add(r);
            PropertyHeader = $"{row.Object} ({row.Name}): {rows.Count(r => r.Kind != CompareKind.Same)} of {rows.Count} properties differ.";
        }
        catch (Exception ex)
        {
            PropertyHeader = $"Could not read {row.Object}: {ex.Message} Likely cause: a device stopped answering. Next step: select the row again.";
        }
    }
}
