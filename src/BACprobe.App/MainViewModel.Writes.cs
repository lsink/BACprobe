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

// Everything that changes a device: overrides, Out of service, settings, device actions, and what is left in place when leaving.
public sealed partial class MainViewModel
{
    [ObservableProperty] private StateChoice? _selectedStateChoice;

    partial void OnSelectedStateChoiceChanged(StateChoice? value)
    {
        if (value is not null) WriteValueText = value.Text;
    }
    [ObservableProperty] private string _overrideSummary = "No overrides or problems.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteSelectedCommand), nameof(ReleaseSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(ShowOverridePanel), nameof(ShowReadOnlyHint))]
    private bool _canWriteSelected;

    /// <summary>Read-only mode: browsing and reading only. The override panel is hidden and writes are refused.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverridePanel), nameof(ShowReadOnlyHint), nameof(WindowTitle), nameof(ShowPropertyEditor), nameof(ShowDeviceActions))]
    [NotifyCanExecuteChangedFor(nameof(WriteSelectedCommand), nameof(ReleaseSelectedCommand))]
    private bool _readOnlyMode = AppSettings.Current.ReadOnly;

    public bool ShowOverridePanel => CanWriteSelected && !ReadOnlyMode;

    /// <summary>A writable point while read-only is on: say why the override controls are not there.</summary>
    public bool ShowReadOnlyHint => CanWriteSelected && ReadOnlyMode;

    public string WindowTitle => ReadOnlyMode ? "BACprobe - READ-ONLY" : "BACprobe";

    // Out_Of_Service: offered for any point that has the property (inputs too), hidden in read-only mode.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOutOfServicePanel))]
    private bool _canSetOutOfService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutOfServiceButtonText))]
    private bool _isPointOutOfService;

    public bool ShowOutOfServicePanel => CanSetOutOfService && !ReadOnlyMode;
    public string OutOfServiceButtonText => IsPointOutOfService ? "Put back in service..." : "Take out of service...";

    partial void OnReadOnlyModeChanged(bool value)
    {
        AppSettings.Update(s => s with { ReadOnly = value });
        if (_writer is not null) _writer.ReadOnly = value;
        OnPropertyChanged(nameof(ShowOutOfServicePanel));
    }

    /// <summary>Set by the window: confirm taking a point out of, or putting it back in, service.</summary>
    public Func<OutOfServiceRequest, bool> ConfirmOutOfService { get; set; } = _ => false;

    [RelayCommand]
    private async Task ToggleOutOfServiceAsync()
    {
        var row = SelectedObject;
        var deviceRow = SelectedDevice;
        if (_writer is null || _svc is null || row is null || deviceRow is null) return;
        if (ReadOnlyMode)
        {
            Status = "Read-only mode is on, so nothing was changed. Untick Read-only at the top to make changes.";
            return;
        }

        var obj = row.Summary;
        var deviceName = deviceRow.DisplayName;
        var request = new OutOfServiceRequest(deviceRow.Device, deviceName, obj.Id, obj.Name ?? obj.Label,
            TurnOn: !IsPointOutOfService, obj.ValueText);
        if (!ConfirmOutOfService(request))
        {
            Status = "Cancelled. Nothing was changed.";
            return;
        }

        var outcome = await _writer.SetOutOfServiceAsync(request);
        Status = outcome.Success
            ? request.TurnOn ? "Out of service. Put it back in service before you leave." : "Back in service."
            : outcome.Message.Replace("\n", " ");
        UpdateOverrideSummary();

        try
        {
            var fresh = (await _svc.OpenDevice(deviceRow.Device).ReadSummariesAsync([obj.Id]))[0];
            obj.UpdateFrom(fresh);
            row.Refresh(obj);
            UpdateOverrideSummary();
            await LoadPropertiesAsync(row);
        }
        catch (Exception) { Status += " (Could not read the point back; check it before you leave.)"; }
    }


    [RelayCommand(CanExecute = nameof(ShowOverridePanel))]
    private Task WriteSelectedAsync() => DoWriteAsync(release: false);

    [RelayCommand(CanExecute = nameof(ShowOverridePanel))]
    private Task ReleaseSelectedAsync() => DoWriteAsync(release: true);


    private async Task DoWriteAsync(bool release)
    {
        var row = SelectedObject;
        var deviceRow = SelectedDevice;
        if (_writer is null || _svc is null || row is null || deviceRow is null) return;
        if (ReadOnlyMode)
        {
            Status = "Read-only mode is on, so nothing was written. Untick Read-only at the top to make changes.";
            return;
        }

        var obj = row.Summary;
        BacnetValue? value = null;
        if (!release)
        {
            if (IsStateSelected && SelectedStateChoice is null)
            {
                Status = "Pick the state to write from the Value list.";
                return;
            }
            if (!WriteValueParser.TryParse(obj.Id.type, WriteValueText, obj.StateNames, out var parsed, out var error))
            {
                Status = error;
                return;
            }
            value = parsed;
        }

        var deviceName = deviceRow.DisplayName;
        var request = new WriteRequest(deviceRow.Device, deviceName, obj.Id, obj.Name ?? obj.Label, value,
            value is { } v ? StateText.Describe(obj.Id.type, v, obj.StateNames) : "release", // "Standby (3)", "On (Active)"
            SelectedPriority.Number, obj.ValueText);

        if (!ConfirmWrite(request))
        {
            Status = "Cancelled. Nothing was written.";
            return;
        }

        var outcome = await _writer.ExecuteAsync(request);
        Status = !outcome.Success ? outcome.Message.Replace("\n", " ")
            : outcome.Explanation is not null ? "The device accepted the write, but the point did not change."
            : release ? "Released." : "Written. The override stays in place until you release it.";
        if (outcome.Explanation is { } explanation) ShowExplanation(explanation);
        UpdateOverrideSummary();

        try
        {
            var fresh = (await _svc.OpenDevice(deviceRow.Device).ReadSummariesAsync([obj.Id]))[0];
            obj.UpdateFrom(fresh); // in place: Find and Live hold this same object
            row.Refresh(obj);
            UpdateOverrideSummary();
            await LoadPropertiesAsync(row);
        }
        catch (Exception) { Status += " (Could not read the point back; check it before you leave.)"; }
    }

    // Changing a setting (a limit, the description, the COV increment...) of the selected point, from the properties list.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPropertyEditor), nameof(PropertyEditTitle))]
    [NotifyCanExecuteChangedFor(nameof(EditPropertyCommand))]
    private PropertyRow? _selectedProperty;

    [ObservableProperty] private string _propertyEditText = "";
    [ObservableProperty] private string _propertyEditHint = "";

    /// <summary>Set by the window: confirm changing a setting.</summary>
    public Func<PropertyWriteRequest, bool> ConfirmPropertyWrite { get; set; } = _ => false;

    /// <summary>The edit box under the properties: for a selected property, when connected and not read-only.</summary>
    public bool ShowPropertyEditor => SelectedProperty is not null && _svc is not null && !ReadOnlyMode;
    public string PropertyEditTitle => SelectedProperty is { } p ? $"Change {p.Name}" : "";

    partial void OnSelectedPropertyChanged(PropertyRow? value)
    {
        if (value is null) return;
        var editable = PropertyEdit.CanEdit(value, out var reason);
        // Start from the current value; for a choice ("Degrees Fahrenheit (64)") the number is what the device needs.
        PropertyEditText = editable ? value.Display : "";
        PropertyEditHint = editable ? $"Type {PropertyEdit.KindHint(value.ValueTag!.Value)}." : reason;
    }

    private bool CanEditProperty() => SelectedProperty is { } p && PropertyEdit.CanEdit(p, out _);

    [RelayCommand(CanExecute = nameof(CanEditProperty))]
    private async Task EditPropertyAsync()
    {
        var row = SelectedObject;
        var deviceRow = SelectedDevice;
        var prop = SelectedProperty;
        if (_writer is null || _svc is null || row is null || deviceRow is null || prop?.ValueTag is not { } tag) return;
        if (ReadOnlyMode)
        {
            Status = "Read-only mode is on, so nothing was written. Switch Read-only off in the toolbar to change settings.";
            return;
        }
        if (!PropertyEdit.TryParse(tag, PropertyEditText, out var value, out var error))
        {
            Status = $"{error} Nothing was written.";
            return;
        }

        var obj = row.Summary;
        var deviceName = deviceRow.DisplayName;
        var id = (BacnetPropertyIds)prop.PropertyId;
        var request = new PropertyWriteRequest(deviceRow.Device, deviceName, obj.Id, obj.Name ?? obj.Label, id, value,
            BacnetNames.FormatValues(obj.Id.type, id, [value]), prop.Display);
        if (!ConfirmPropertyWrite(request))
        {
            Status = "Cancelled. Nothing was written.";
            return;
        }

        var outcome = await _writer.WritePropertyAsync(request);
        Status = outcome.Success ? $"{prop.Name} changed: the {outcome.Message}." : outcome.Message.Replace("\n", " ");
        await LoadPropertiesAsync(row);
    }

    // Device actions on the selected device: set its clock, restart it, mute or un-mute it.
    /// <summary>Set by the window: confirm a device action; returns whether to go ahead, and the password typed (if any).</summary>
    public Func<DeviceActionRequest, (bool Go, string? Password)> ConfirmDeviceAction { get; set; } = _ => (false, null);

    public IReadOnlyList<int> MuteMinuteChoices { get; } = [5, 10, 15, 30, 60];
    [ObservableProperty] private int _muteMinutes = (int)DeviceActionRequest.DefaultMuteMinutes;

    /// <summary>The device card's actions: shown when connected and not read-only.</summary>
    public bool ShowDeviceActions => _svc is not null && !ReadOnlyMode;

    [RelayCommand] private Task SyncClockAsync() => DeviceActionAsync(DeviceActionKind.SyncTime);
    [RelayCommand] private Task WarmStartAsync() => DeviceActionAsync(DeviceActionKind.WarmStart);
    [RelayCommand] private Task ColdStartAsync() => DeviceActionAsync(DeviceActionKind.ColdStart);
    [RelayCommand] private Task MuteDeviceAsync() => DeviceActionAsync(DeviceActionKind.Mute);
    [RelayCommand] private Task UnmuteDeviceAsync() => DeviceActionAsync(DeviceActionKind.Unmute);

    private async Task DeviceActionAsync(DeviceActionKind kind)
    {
        var deviceRow = SelectedDevice;
        if (_writer is null || _svc is null || deviceRow is null) return;
        if (ReadOnlyMode && kind != DeviceActionKind.Unmute)
        {
            Status = "Read-only mode is on, so nothing was sent. Switch Read-only off in the toolbar first.";
            return;
        }
        var name = deviceRow.DisplayName;
        var request = new DeviceActionRequest(deviceRow.Device, name, kind, (uint)MuteMinutes);
        var (go, password) = ConfirmDeviceAction(request);
        if (!go)
        {
            Status = "Cancelled. Nothing was sent.";
            return;
        }
        var outcome = await _writer.RunDeviceActionAsync(request with { Password = password });
        Status = outcome.Success ? $"{name}: {outcome.Message.TrimEnd('.')}." : outcome.Message.Replace("\n", " ");
        deviceRow.Refresh(); // the clock column, after a time sync
        UpdateOverrideSummary(); // a mute counts as something left in place
    }

    /// <summary>
    /// The footer: overrides this session made (the ones BACprobe will offer to release) plus any point on the
    /// device being viewed that is overridden by anyone, so the footer never disagrees with the orange rows.
    /// </summary>
    private void UpdateOverrideSummary()
    {
        var mine = _overrides.Active.Count;
        var onDevice = Objects.Count(o => o.IsOverridden);
        var problems = Objects.Count(o => o.HasProblem);
        var parts = new List<string>();
        if (mine > 0) parts.Add($"{mine} override(s) from this session");
        if (onDevice > 0) parts.Add($"{onDevice} point(s) overridden on this device");
        if (problems > 0) parts.Add($"{problems} point(s) with a problem");
        OverrideSummary = parts.Count == 0 ? "No overrides or problems." : string.Join("  |  ", parts);

        // A point that just went into fault (or got overridden) must join the filtered list, and one that recovered must leave it.
        if (SelectedPointFilter?.Test is { } test)
        {
            System.Windows.Data.CollectionViewSource.GetDefaultView(Objects).Refresh();
            var shown = Objects.Count(o => test(o.Summary));
            PointFilterStatus = shown == 0 && Objects.Count > 0 ? $"None of the {Objects.Count} points" : $"{shown} of {Objects.Count}";
        }
        else PointFilterStatus = "";
    }

    /// <summary>
    /// Before disconnecting: list overrides this session left and offer to release them.
    /// Returns false if the user wants to go back.
    /// </summary>
    public async Task<bool> ResolveOverridesAsync()
    {
        var active = _overrides.Active;
        if (active.Count == 0 || _writer is null) return true;

        switch (AskOverrides(active))
        {
            case OverrideChoice.GoBack:
                return false;
            case OverrideChoice.Release:
                var (_, failed) = await _writer.ReleaseAllAsync();
                UpdateOverrideSummary();
                return failed == 0 || ConfirmContinueAfterFailedRelease(failed);
            default:
                _overrides.Clear(); // left in place on purpose: do not ask about them again
                UpdateOverrideSummary();
                return true;
        }
    }
}
