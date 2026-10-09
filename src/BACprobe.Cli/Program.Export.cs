using BACprobe.Core.Discovery;
using BACprobe.Core.Export;
using BACprobe.Core.Jobs;

namespace BACprobe.Cli;

internal static partial class Program
{
    private sealed record Collected(List<ExportDevice> Devices, List<string> Failed);

    private static async Task<int> ExportAsync(Dictionary<string, string?> opts)
    {
        var fromJob = opts.TryGetValue("job", out var jobPath) && jobPath is not null;
        if (!fromJob && opts.ContainsKey("all") == opts.ContainsKey("device"))
            throw new ArgumentException("Choose what to export: --device <instance> for one device, --all for every device that answers, " +
                                        "or --job <file> to export from a saved job without touching the network.");

        var (format, path) = ResolveOutput(opts);
        if (File.Exists(path) && !opts.ContainsKey("force"))
            return Fail($"{path} already exists.\n  Next step: pick another name with --out, or add --force to overwrite it.");

        Collected collected;
        string project = opts.TryGetValue("project", out var p) && p is not null ? p : "BACprobe export";
        if (fromJob)
        {
            var job = LoadJob(jobPath!);
            if (opts.TryGetValue("project", out _) is false && job.Info.Name.Length > 0) project = job.Info.Name;
            var wanted = opts.TryGetValue("device", out var dev) && int.TryParse(dev, out var inst) ? (uint?)inst : null;
            var devices = job.Devices.Where(d => wanted is null || d.Instance == wanted).ToList();
            if (devices.Count == 0)
                return Fail(wanted is null ? "That job has no devices." : $"Device {wanted} is not in that job.\n  Next step: run 'bacprobe job show {jobPath}' to see what it holds.");
            var unread = devices.Where(d => !d.PointsRead).Select(d => $"device {d.Instance} ({d.Name}): points were not read when the job was saved").ToList();
            collected = new Collected([.. devices.Where(d => d.PointsRead).Select(d => d.ToExportDevice())], unread);
            if (collected.Devices.Count == 0)
                return Fail("None of those devices have saved points.\n  Next step: save the job again with 'bacprobe job save --all'.");
        }
        else
        {
            var (result, error) = await CollectFromNetworkAsync(opts);
            if (result is null) return Fail(error!);
            collected = result;
        }

        try
        {
            PointExporter.Write(path, format, collected.Devices, project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"Could not write {path}: {ex.Message}\n" +
                        "  Likely cause: the file is open in Excel, or the folder is read-only.\n" +
                        "  Next step:    close the file or choose another place with --out.");
        }

        var points = collected.Devices.Sum(c => c.Points.Count());
        Console.WriteLine($"Exported {points} point(s) from {collected.Devices.Count} device(s) to {Path.GetFullPath(path)} ({format}).");
        if (collected.Failed.Count > 0)
        {
            Console.WriteLine($"NOT included ({collected.Failed.Count}):");
            foreach (var f in collected.Failed) Console.WriteLine("  " + f);
            return 4;
        }
        return 0;
    }

    /// <summary>
    /// Connect, find the device(s) named by --device or --all, and read every point list.
    /// Returns the readable devices plus a note for each that failed.
    /// </summary>
    private static async Task<(Collected?, string?)> CollectFromNetworkAsync(Dictionary<string, string?> opts)
    {
        var all = opts.ContainsKey("all");
        var (svc, error) = await OpenSessionAsync(opts);
        if (svc is null) return (null, error);
        using var _ = svc;

        List<DiscoveredDevice> devices;
        if (all)
        {
            var wait = IntOpt(opts, "wait", 5);
            Console.WriteLine($"Sending Who-Is, listening {wait}s...");
            devices = [.. await svc.WhoIsAsync(-1, -1, TimeSpan.FromSeconds(wait))];
            if (devices.Count == 0)
                return (null, "No devices answered, so there is nothing to read.\n" +
                              "  Likely cause: wrong adapter/subnet, a firewall blocking UDP 47808, or devices on another subnet (try --bbmd).\n" +
                              "  Next step:    run 'bacprobe discover' to check what is reachable.");
            Console.WriteLine($"{devices.Count} device(s) found; reading names...");
            await svc.EnrichAsync(devices);
        }
        else
        {
            if (!int.TryParse(opts["device"], out var instance) || instance is < 0 or > 4194302)
                throw new ArgumentException("--device needs an instance number. 'bacprobe discover' lists them.");
            var found = await svc.WhoIsAsync(instance, instance, TimeSpan.FromSeconds(IntOpt(opts, "wait", 3)));
            var device = found.FirstOrDefault(d => d.InstanceId == instance);
            if (device is null)
                return (null, NoWhoIsAnswer(instance));
            await svc.EnrichAsync([device]);
            devices = [device];
        }

        var collected = new List<ExportDevice>();
        var failed = new List<string>();
        // A few devices at a time on the IP network; one at a time behind each router network (an MS/TP trunk) or over MS/TP.
        var how = ReadLanes.Describe(ReadLanes.Plan(devices, svc.IsSharedMedium));
        if (devices.Count > 1) Console.WriteLine($"Reading {devices.Count} devices, {how}...");
        var done = 0;
        var results = await ReadLanes.RunAsync(devices, svc.IsSharedMedium, async d =>
        {
            var read = await PointExporter.CollectAsync(svc.OpenDevice(d), d,
                new Progress<string>(m => Console.Write($"\r{$"[{Volatile.Read(ref done)}/{devices.Count}] {m}",-78}")));
            Interlocked.Increment(ref done);
            return read;
        });
        foreach (var r in results)
        {
            if (r.Value is { } read) collected.Add(read);
            else failed.Add($"device {r.Device.InstanceId} ({r.Device.ObjectName ?? r.Device.AddressText}): {ReadFailure(r.Error!).Split('\n')[0]}");
        }
        Console.WriteLine();

        return collected.Count == 0
            ? (null, "Could not read any device, so nothing was done.\n  " + string.Join("\n  ", failed) +
                     "\n  Next step: check the connection and run the command again.")
            : (new Collected(collected, failed), null);
    }

    private static JobSnapshot LoadJob(string path)
    {
        try { return JobFile.Load(path); }
        catch (JobFileException ex) { throw new ArgumentException(ex.Message, ex); }
    }

    private static (ExportFormat, string) ResolveOutput(Dictionary<string, string?> opts)
    {
        ExportFormat? format = null;
        if (opts.TryGetValue("format", out var f))
        {
            format = f?.ToLowerInvariant() switch
            {
                "csv" => ExportFormat.Csv,
                "xlsx" or "excel" => ExportFormat.Xlsx,
                "ede" => ExportFormat.Ede,
                _ => throw new ArgumentException("--format must be csv, xlsx or ede."),
            };
        }

        if (opts.TryGetValue("out", out var path) && path is not null)
        {
            format ??= PointExporter.FormatFromPath(path)
                       ?? throw new ArgumentException("Cannot tell the format from that file name. Use .csv or .xlsx, or add --format csv|xlsx|ede.");
            if (format == ExportFormat.Xlsx && PointExporter.FormatFromPath(path) != ExportFormat.Xlsx)
                throw new ArgumentException("An Excel export must be named .xlsx.");
            return (format.Value, path);
        }

        format ??= ExportFormat.Csv;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var kind = format == ExportFormat.Ede ? "ede" : "points";
        return (format.Value, $"bacprobe-{kind}-{stamp}{PointExporter.Extension(format.Value)}");
    }
}
