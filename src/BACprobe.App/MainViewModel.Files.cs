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

// Exports, job files, and the result banner after a save.
public sealed partial class MainViewModel
{
    private bool CanExportSelected() => !IsExporting && SelectedDevice is not null && Objects.Count > 0;

    private bool CanExportAll() => !IsExporting && !IsScanning && Devices.Count > 0 && (_svc is not null || _pointCache.Count > 0);

    /// <summary>
    /// Every device's point list. Live: read now (so values are fresh). Offline (a job is open): the saved points.
    /// Devices that cannot be read are returned with a reason instead of silently dropped.
    /// </summary>
    private async Task<(List<ExportDevice> Devices, List<(DeviceRow Row, string Reason)> Failed)> CollectAllAsync(IProgress<string> progress)
    {
        var collected = new List<ExportDevice>();
        var failed = new List<(DeviceRow, string)>();
        var rows = Devices.ToList();
        if (_svc is not { } svc)
        {
            // Offline (a saved job): use what was saved.
            foreach (var d in rows)
            {
                if (_pointCache.TryGetValue(d.Instance, out var saved)) collected.Add(saved);
                else failed.Add((d, "points were not read when the job was saved"));
            }
            return (collected, failed);
        }

        // A few devices at a time on the IP network; one at a time behind each router network (an MS/TP trunk) or over MS/TP.
        var devices = rows.Select(r => r.Device).ToList();
        var how = ReadLanes.Describe(ReadLanes.Plan(devices, svc.IsSharedMedium));
        var done = 0;
        var results = await ReadLanes.RunAsync(devices, svc.IsSharedMedium, async d =>
        {
            var read = await PointExporter.CollectAsync(svc.OpenDevice(d), d,
                new Progress<string>(m => progress.Report($"{Volatile.Read(ref done)} of {rows.Count} devices read, {how}. {m}")));
            Interlocked.Increment(ref done);
            return read;
        });
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i] is { Value: { } read })
            {
                _pointCache[rows[i].Instance] = read;
                collected.Add(read);
            }
            else failed.Add((rows[i], results[i].Error?.Message ?? "not read"));
        }
        return (collected, failed);
    }

    [RelayCommand(CanExecute = nameof(CanExportSelected))]
    private async Task ExportSelectedAsync()
    {
        var row = SelectedDevice;
        if (row is null) return;
        var device = new ExportDevice(row.Device, row.ExportName,
            Objects.Select(o => o.Summary).ToList());
        await ExportAsync([device], $"{SafeFileName(device.Name)}-points");
    }

    [RelayCommand(CanExecute = nameof(CanExportAll))]
    private async Task ExportAllAsync()
    {
        var choice = PickExportFile("all-devices-points");
        if (choice is null)
        {
            Status = "Export cancelled.";
            return;
        }

        IsExporting = true;
        try
        {
            var (collected, failed) = await CollectAllAsync(new Progress<string>(m => Status = m));
            if (collected.Count == 0)
            {
                Status = "Could not read any device, so nothing was exported. Likely cause: network drop or the devices stopped answering. Next step: check the connection and try again.";
                return;
            }
            await SaveExportAsync(collected, choice.Value, [.. failed.Select(f => $"device {f.Row.Instance} ({f.Reason})")]);
        }
        finally { IsExporting = false; }
    }

    [RelayCommand]
    private async Task SaveJobAsync()
    {
        if (Devices.Count == 0)
        {
            Status = "There is nothing to save yet. Scan for devices first (or open an existing job).";
            return;
        }

        var name = JobName.Trim().Length > 0 ? JobName.Trim() : "BACprobe job";
        var path = PickJobSavePath(SafeFileName(name));
        if (path is null)
        {
            Status = "Save cancelled.";
            return;
        }

        IsExporting = true; // blocks Scan/Export while we read every device
        try
        {
            var (collected, failed) = await CollectAllAsync(new Progress<string>(m => Status = m));
            var saved = collected.Select(c => SavedDevice.From(c.Device, c.Name, c.Objects))
                .Concat(failed.Select(f => SavedDevice.From(f.Row.Device, f.Row.ExportName, null)))
                .OrderBy(d => d.Instance).ToList();

            var sessionLog = _log.Entries;
            var writeLog = _persistedLog.Concat(sessionLog.Skip(_sessionLogSaved)).ToList();
            var info = new JobInfo(name, JobNotes.Trim(), _jobCreated, DateTimeOffset.Now,
                BbmdText.Trim().Length > 0 ? BbmdText.Trim() : null, SelectedAdapter?.Info.Cidr,
                typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "");

            await Task.Run(() => JobFile.Save(path, new JobSnapshot(info, saved, writeLog, Notes.All, Watch.Items)));
            _persistedLog = writeLog;
            _sessionLogSaved = sessionLog.Count;

            Status = $"Saved job \"{name}\": {saved.Count} device(s), {saved.Sum(d => d.Objects.Count)} object(s) to {path}.";
            if (failed.Count > 0)
                Status += $" Points NOT saved for: {string.Join(", ", failed.Select(f => $"{f.Row.Instance} ({f.Reason})"))}.";
            ShowResult(Status, path);
        }
        catch (JobFileException ex)
        {
            Status = ex.Message.Replace("\n", " ");
        }
        finally { IsExporting = false; }
    }

    [RelayCommand]
    private async Task OpenJobAsync()
    {
        if (!await ResolveOverridesAsync()) return;
        var path = PickJobOpenPath();
        if (path is null) return;

        JobSnapshot job;
        try { job = await Task.Run(() => JobFile.Load(path)); }
        catch (JobFileException ex)
        {
            Status = ex.Message.Replace("\n", " ");
            return;
        }

        // Opening a job leaves live mode: close the connection and show the saved snapshot.
        IsLive = false;
        CanUseLive = false;
        ConnectionReset?.Invoke();
        _browseCts?.Cancel();
        _svc?.Dispose();
        _svc = null;
        _writer = null;
        ResetBrowsing();

        Notes.Load(job.AllNotes);
        Watch.Load(job.AllWatch);
        OnPropertyChanged(nameof(WatchButtonText));
        Note.Target(SelectedDevice, SelectedObject);
        _baseline = job.Devices;
        _baselineName = job.Info.Name;
        ClearNetworkCheck();
        JobName = job.Info.Name;
        JobNotes = job.Info.Notes;
        if (job.Info.BbmdText is not null) BbmdText = job.Info.BbmdText;
        _jobCreated = job.Info.CreatedAt;
        _persistedLog = [.. job.WriteLog];
        _sessionLogSaved = _log.Entries.Count;
        WriteLogLines.Clear();
        foreach (var e in job.WriteLog) WriteLogLines.Add(e.Text);

        foreach (var d in job.Devices)
        {
            Devices.Add(new DeviceRow(d.ToDiscovered()));
            if (d.PointsRead) _pointCache[d.Instance] = d.ToExportDevice();
        }
        UpdateOverrideSummary();
        OfflineBanner = $"Viewing saved job \"{job.Info.Name}\" from {job.Info.SavedAt.LocalDateTime:yyyy-MM-dd HH:mm}. These values are a snapshot, not live. " +
                        "Click Scan to connect and read live values; the scan will also compare what answers with this job.";
        Status = $"Opened {job.Devices.Count} device(s) from {path}. Select one to see its saved points.";
        ExportAllCommand.NotifyCanExecuteChanged();
    }

    private async Task ExportAsync(IReadOnlyList<ExportDevice> devices, string suggestedName)
    {
        var choice = PickExportFile(suggestedName);
        if (choice is null)
        {
            Status = "Export cancelled.";
            return;
        }

        IsExporting = true;
        try { await SaveExportAsync(devices, choice.Value, []); }
        finally { IsExporting = false; }
    }

    private async Task SaveExportAsync(IReadOnlyList<ExportDevice> devices, (string Path, ExportFormat Format) choice, List<string> failed)
    {
        try
        {
            await Task.Run(() => PointExporter.Write(choice.Path, choice.Format, devices));
            var points = devices.Sum(d => d.Points.Count());
            Status = $"Exported {points} point(s) from {devices.Count} device(s) to {choice.Path}.";
            if (failed.Count > 0) Status += $" NOT included: {string.Join("; ", failed)}.";
            ShowResult(Status, choice.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save the file: {ex.Message} Likely cause: it is open in Excel, or the folder is read-only. Next step: close the file or choose another location.";
        }
    }

    private void ShowResult(string message, string path)
    {
        ResultMessage = message;
        ResultPath = path;
    }

    [RelayCommand]
    private void DismissResult()
    {
        ResultMessage = "";
        ResultPath = "";
    }

    private bool HasResultFile() => ResultPath.Length > 0 && File.Exists(ResultPath);

    [RelayCommand(CanExecute = nameof(HasResultFile))]
    private void OpenResultFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ResultPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Status = $"Could not open the file: {ex.Message} Likely cause: no program is set to open this file type. " +
                     "Next step: use Show in folder and open it from there.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasResultFile))]
    private void ShowResultInFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ResultPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Status = $"Could not open the folder: {ex.Message} The file is at {ResultPath}.";
        }
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
