using System.Collections.ObjectModel;
using System.IO.BACnet;
using BACprobe.Core.Alarms;
using BACprobe.Core.Discovery;
using BACprobe.Core.Writing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

/// <summary>One alarm in the list, worded for a tech.</summary>
public sealed class AlarmRow(ActiveEvent e)
{
    public ActiveEvent Event => e;
    public string Device => $"{e.Device.InstanceId} {e.Device.ObjectName ?? ""}".Trim();
    public string PointId => e.ShortId;
    public string Name => e.DisplayName;
    public string State => e.StateText;
    public string Value => e.ValueText ?? "";
    public string Since => e.SinceText;
    public string Unacked => e.NeedsAck ? e.UnackedText : "";
    public string Explanation => EventText.Explanation(e);

    /// <summary>For colouring the row: LifeSafety, Fault, Alarm or Normal.</summary>
    public string Kind => e.State switch
    {
        BacnetEventStates.EVENT_STATE_LIFE_SAFETY_ALARM => "LifeSafety",
        BacnetEventStates.EVENT_STATE_FAULT => "Fault",
        BacnetEventStates.EVENT_STATE_NORMAL => "Normal",
        _ => "Alarm",
    };
}

/// <summary>
/// Every device's active and unacknowledged alarms, worst first, each explained. Acknowledging goes through the main window's
/// confirmation and the write log, and is refused in read-only mode. Reads alarms live; a saved job does not hold them.
/// </summary>
public sealed partial class AlarmsViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<DiscoveredDevice> _devices;
    private readonly AlarmReader _reader;
    private readonly Func<AlarmAckRequest, Task<WriteOutcome>> _acknowledge;
    private readonly Func<uint, BacnetObjectId, Task> _goTo;
    private readonly Func<bool> _readOnly;
    private List<DeviceAlarms> _results = [];
    private CancellationTokenSource? _cts;

    public AlarmsViewModel(IReadOnlyList<DiscoveredDevice> devices, AlarmReader reader, Func<AlarmAckRequest, Task<WriteOutcome>> acknowledge,
        Func<uint, BacnetObjectId, Task> goTo, Func<bool> readOnly)
    {
        _devices = devices;
        _reader = reader;
        _acknowledge = acknowledge;
        _goTo = goTo;
        _readOnly = readOnly;
    }

    public ObservableCollection<AlarmRow> Rows { get; } = [];
    public ObservableCollection<FindingRow> Findings { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _unackedOnly;
    [ObservableProperty] private string _detail = "Select an alarm to see what it means and what to do.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(AcknowledgeCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeCommand), nameof(GoToPointCommand))]
    private AlarmRow? _selectedRow;

    public bool HasFindings => Findings.Count > 0;

    /// <summary>Shown beside the Acknowledge button so a disabled button explains itself.</summary>
    public string AckHint => _readOnly() ? "Read-only mode is on: untick Read-only in the main window to acknowledge." : "";

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBusy = true;
        _results = [];
        Rows.Clear();
        Findings.Clear();
        OnPropertyChanged(nameof(HasFindings));
        Summary = "";
        Status = $"Asking {_devices.Count} device(s) for their alarms...";
        try
        {
            // Show each device's alarms as they arrive, so a slow device does not hold up the rest. A report can land after
            // the final list below, so merging is by device (idempotent).
            var progress = new Progress<DeviceAlarms>(r =>
            {
                if (ct.IsCancellationRequested) return;
                Merge(r);
                Status = $"Asked {_results.Count} of {_devices.Count} device(s)...";
            });
            var all = await _reader.ReadAllAsync(_devices, progress, ct);
            if (ct.IsCancellationRequested) return;
            foreach (var r in all) Merge(r);
            Status = $"Read at {DateTime.Now:HH:mm:ss}. Alarms are not updated live: press Refresh to look again.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = $"Could not read the alarms: {ex.Message} Likely cause: the connection was closed. Next step: Scan again in the main window, then Refresh.";
        }
        finally { IsBusy = false; }
    }

    private bool CanRefresh() => !IsBusy;

    partial void OnUnackedOnlyChanged(bool value) => Apply();

    partial void OnSelectedRowChanged(AlarmRow? value)
    {
        Detail = value is null ? "Select an alarm to see what it means and what to do." : value.Explanation;
        OnPropertyChanged(nameof(AckHint));
    }

    /// <summary>Put one device's latest answer in place of its previous one, then redraw.</summary>
    private void Merge(DeviceAlarms r)
    {
        var i = _results.FindIndex(x => x.Device.InstanceId == r.Device.InstanceId);
        if (i >= 0) _results[i] = r; else _results.Add(r);
        Apply();
    }

    /// <summary>Rebuild the list from the results so far, keeping the selection on the same point if it is still there.</summary>
    private void Apply()
    {
        var keep = SelectedRow?.Event;
        Rows.Clear();
        foreach (var e in EventText.Sort(_results.SelectMany(r => r.Events)).Where(e => !UnackedOnly || e.NeedsAck))
            Rows.Add(new AlarmRow(e));
        SelectedRow = keep is null ? null
            : Rows.FirstOrDefault(r => r.Event.Device.InstanceId == keep.Device.InstanceId && r.Event.Point.Equals(keep.Point));
        Summary = AlarmCheck.Summarise(_results);
        Findings.Clear();
        foreach (var f in AlarmCheck.Findings(_results)) Findings.Add(new FindingRow(f));
        OnPropertyChanged(nameof(HasFindings));
    }

    // Disabled in read-only mode (the hint beside it says why); the main window and the writer refuse it too.
    private bool CanAcknowledge() => !IsBusy && !_readOnly() && SelectedRow is { Event.NeedsAck: true };

    [RelayCommand(CanExecute = nameof(CanAcknowledge))]
    private async Task AcknowledgeAsync()
    {
        var row = SelectedRow!;
        var e = row.Event;
        var request = new AlarmAckRequest(e, e.Device.ObjectName ?? $"device {e.Device.InstanceId}", AlarmAckRequest.DefaultSource);
        IsBusy = true;
        try
        {
            var outcome = await _acknowledge(request);
            Status = outcome.Success ? $"Acknowledged {e.DisplayName} on device {e.Device.InstanceId}." : outcome.Message.Replace("\n", " ");
            if (!outcome.Success) return;

            // Read that device again, so the list shows what the device now says (still in alarm, but acknowledged; or gone).
            Merge(await _reader.ReadAsync(e.Device));
        }
        catch (Exception ex)
        {
            Status = $"Could not read the device back after acknowledging: {ex.Message} Next step: press Refresh.";
        }
        finally { IsBusy = false; }
    }

    private bool CanGoToPoint() => SelectedRow is not null;

    /// <summary>Select the point in the main window, to see its properties, priority array and trend.</summary>
    [RelayCommand(CanExecute = nameof(CanGoToPoint))]
    private Task GoToPointAsync() => _goTo(SelectedRow!.Event.Device.InstanceId, SelectedRow.Event.Point);

    /// <summary>The window closed: stop asking devices.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
