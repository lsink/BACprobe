using System.Collections.ObjectModel;
using BACprobe.Core.Bbmd;
using BACprobe.Core.Discovery;
using BACprobe.Core.Networking;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

/// <summary>
/// The BBMD check: a BBMD's broadcast and foreign device tables, what each listed peer says back, and what looks wrong.
/// Read-only: nothing on the BBMD is changed. Needs no scan first; it uses its own socket on the chosen adapter.
/// </summary>
public sealed partial class BbmdCheckViewModel(AdapterInfo adapter, BbmdTarget target) : ObservableObject
{
    public string Title => $"BBMD check - {target}";

    public string Heading =>
        $"Reading the tables of BBMD {target} from {adapter.Address} ({adapter.Name}), and the broadcast table of every peer it lists. " +
        "Nothing on the BBMDs is changed.";

    public ObservableCollection<FindingRow> Findings { get; } = [];
    public ObservableCollection<BdtRow> BroadcastTable { get; } = [];
    public ObservableCollection<FdtRow> ForeignDevices { get; } = [];

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _broadcastHeader = "Broadcast distribution table";
    [ObservableProperty] private string _foreignHeader = "Foreign device table";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private bool _isBusy;

    private bool CanCheck() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    public async Task CheckAsync()
    {
        IsBusy = true;
        Status = $"Reading the tables of {target}...";
        Findings.Clear();
        BroadcastTable.Clear();
        ForeignDevices.Clear();
        try
        {
            var report = await BbmdChecker.CheckAsync(target.EndPoint, adapter);
            foreach (var f in report.Findings.OrderByDescending(f => f.Severity)) Findings.Add(new FindingRow(f));
            foreach (var r in report.BdtRows) BroadcastTable.Add(r);
            foreach (var r in report.FdtRows) ForeignDevices.Add(r);

            BroadcastHeader = report.Bdt.Ok
                ? $"Broadcast distribution table: {report.BdtRows.Count} entr{(report.BdtRows.Count == 1 ? "y" : "ies")}"
                : $"Broadcast distribution table: not read ({Describe(report.Bdt.Status)})";
            ForeignHeader = report.Fdt.Ok
                ? $"Foreign device table: {report.FdtRows.Count} registered"
                : $"Foreign device table: not read ({Describe(report.Fdt.Status)})";
            Status = $"{NetworkCheck.Summarize(report.Findings.Where(f => f.Severity != FindingSeverity.Info).ToList())}. Checked at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            Status = $"The check failed: {ex.Message}. Likely cause: the adapter's address changed or the network dropped. " +
                     "Next step: click Re-check in the main window, then try again.";
        }
        finally { IsBusy = false; }
    }

    private static string Describe(TableReadStatus s) => s switch
    {
        TableReadStatus.Refused => "the BBMD refused",
        TableReadStatus.NoAnswer => "no answer",
        TableReadStatus.SendFailed => "could not send from this PC",
        _ => "",
    };
}
