using BACprobe.Core.Browsing;
using BACprobe.Core.Jobs;

namespace BACprobe.Cli;

internal static partial class Program
{
    private static Task<int> JobAsync(Dictionary<string, string?> opts) =>
        opts.GetValueOrDefault("_0")?.ToLowerInvariant() switch
        {
            "save" => JobSaveAsync(opts),
            "show" => Task.FromResult(JobShow(opts)),
            _ => throw new ArgumentException("Use 'bacprobe job save --out site.bacprobe --all' or 'bacprobe job show site.bacprobe'."),
        };

    private static async Task<int> JobSaveAsync(Dictionary<string, string?> opts)
    {
        if (!opts.TryGetValue("out", out var path) || path is null)
            throw new ArgumentException("Say where to save with --out, e.g. --out city-hall.bacprobe.");
        if (!path.EndsWith(JobFile.Extension, StringComparison.OrdinalIgnoreCase)) path += JobFile.Extension;
        if (opts.ContainsKey("all") == opts.ContainsKey("device"))
            throw new ArgumentException("Choose what to save: --all for every device that answers, or --device <instance>.");

        // Re-saving over an existing job keeps its creation time and write history.
        JobSnapshot? previous = null;
        if (File.Exists(path))
        {
            if (!opts.ContainsKey("force"))
                return Fail($"{path} already exists.\n  Next step: pick another name with --out, or add --force to replace it (its write history is kept).");
            try { previous = JobFile.Load(path); }
            catch (JobFileException) { /* unreadable old file: replace it */ }
        }

        var (collected, error) = await CollectFromNetworkAsync(opts);
        if (collected is null) return Fail(error!);

        var now = DateTimeOffset.Now;
        var info = new JobInfo(
            opts.TryGetValue("name", out var name) && name is not null ? name : previous?.Info.Name ?? "BACprobe job",
            opts.TryGetValue("notes", out var notes) && notes is not null ? notes : previous?.Info.Notes ?? "",
            previous?.Info.CreatedAt ?? now, now,
            opts.TryGetValue("bbmd", out var bbmd) ? bbmd : previous?.Info.BbmdText,
            PickAdapter(opts).Cidr,
            typeof(Program).Assembly.GetName().Version?.ToString() ?? "");

        var devices = collected.Devices.Select(d => SavedDevice.From(d.Device, d.Name, d.Objects)).ToList();
        try
        {
            JobFile.Save(path, new JobSnapshot(info, devices, previous?.WriteLog ?? []));
        }
        catch (JobFileException ex)
        {
            return Fail(ex.Message);
        }

        Console.WriteLine($"Saved job \"{info.Name}\": {devices.Count} device(s), {devices.Sum(d => d.Objects.Count)} object(s) to {Path.GetFullPath(path)}.");
        foreach (var f in collected.Failed) Console.WriteLine("  NOT included: " + f);
        return collected.Failed.Count > 0 ? 4 : 0;
    }

    private static int JobShow(Dictionary<string, string?> opts)
    {
        var file = opts.GetValueOrDefault("_1") ?? throw new ArgumentException("Say which file: bacprobe job show site.bacprobe");
        var job = LoadJob(file);
        var i = job.Info;

        Console.WriteLine($"Job:     {i.Name}");
        if (i.Notes.Length > 0) Console.WriteLine($"Notes:   {i.Notes}");
        Console.WriteLine($"Saved:   {i.SavedAt.LocalDateTime:yyyy-MM-dd HH:mm} (created {i.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm})");
        if (i.BbmdText is not null) Console.WriteLine($"BBMD:    {i.BbmdText}");
        if (i.AdapterCidr is not null) Console.WriteLine($"Network: {i.AdapterCidr}");
        Console.WriteLine("These values are a snapshot from when the job was saved, not live.");
        Console.WriteLine();

        if (opts.TryGetValue("device", out var dev) && int.TryParse(dev, out var instance))
        {
            var d = job.Devices.FirstOrDefault(x => x.Instance == instance)
                    ?? throw new ArgumentException($"Device {instance} is not in that job. Run 'bacprobe job show {file}' to see what it holds.");
            if (!d.PointsRead)
            {
                Console.WriteLine($"Device {d.Instance} \"{d.Name}\" was found but its points were not read when the job was saved.");
                return 0;
            }
            Console.WriteLine($"Device {d.Instance} \"{d.Name}\" at {d.AddressText}");
            Console.WriteLine($"{"Object",-8} {"Type",-22} {"Name",-26} {"Value",-14} Description");
            foreach (var o in d.Objects)
            {
                var s = o.ToSummary();
                Console.WriteLine($"{BacnetNames.ObjectTypeShort(o.Type)} {o.Instance,-4} {s.TypeName,-22} {o.Name ?? "-",-26} {s.ValueText,-14} {o.Description}");
            }
        }
        else
        {
            Console.WriteLine($"{"Instance",-9} {"Address",-22} {"Vendor",-22} {"Model",-16} {"Points",-7} Name");
            foreach (var d in job.Devices)
                Console.WriteLine($"{d.Instance,-9} {d.AddressText,-22} {d.VendorName ?? $"vendor {d.VendorId}",-22} {d.ModelName ?? "-",-16} " +
                                  $"{(d.PointsRead ? d.Objects.Count.ToString() : "not read"),-7} {d.Name}");
        }

        if (opts.ContainsKey("log") || job.WriteLog.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Write history: {job.WriteLog.Count} entr{(job.WriteLog.Count == 1 ? "y" : "ies")}{(opts.ContainsKey("log") ? "" : " (add --log to list them)")}");
            if (opts.ContainsKey("log"))
                foreach (var e in job.WriteLog) Console.WriteLine("  " + e.Text);
        }
        return 0;
    }
}
