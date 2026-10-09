using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Jobs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BACprobe.App;

/// <summary>
/// The Note box: free text for the selected point, or the device when no point is selected. Kept with the job file; never sent to a device,
/// so it works in read-only mode too.
/// </summary>
public sealed partial class NoteViewModel : ObservableObject
{
    private bool _loading;
    private (uint Device, BacnetObjectId? Point, string DeviceName, string? PointName)? _target;

    /// <summary>Every note this session (and from the job that was opened): what Save job stores.</summary>
    public SessionNotes Notes { get; } = new();

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _canNote;

    /// <summary>Point the box at another device or point (null device: nothing selected, no box).</summary>
    public void Target(DeviceRow? device, ObjectRow? point)
    {
        _loading = true; // moving to another note must not save the old text into it
        try
        {
            if (device is null)
            {
                _target = null;
                CanNote = false;
                Text = "";
                Title = "";
                return;
            }
            BacnetObjectId? id = point?.Summary.Id;
            _target = (device.Instance, id, device.DisplayName, point?.Name);
            CanNote = true;
            Text = Notes.Get(device.Instance, id);
            UpdateTitle();
        }
        finally { _loading = false; }
    }

    private void UpdateTitle()
    {
        if (_target is not { } t) return;
        var what = t.Point is { } p ? $"{t.PointName ?? BacnetNames.ObjectLabel(p)} ({BacnetNames.ObjectLabel(p)})" : t.DeviceName;
        var total = Notes.CountFor(t.Device);
        Title = $"Note for {what}" + (total > 0 ? $"  -  {total} note(s) on this device" : "");
    }

    partial void OnTextChanged(string value)
    {
        if (_loading || _target is not { } t) return;
        Notes.Set(t.Device, t.Point, value);
        UpdateTitle();
    }
}
