using BACprobe.Core.Discovery;
using BACprobe.Core.Export;

namespace BACprobe.Cli;

internal static partial class Program
{
    private static async Task<int> ExportAsync(Dictionary<string, string?> opts)
    {
        var all = opts.ContainsKey("all");
        if (all == opts.ContainsKey("device"))
            throw new ArgumentException("Choose what to export: --device <instance> for one device, or --all for every device that answers.");

        var (format, path) = ResolveOutput(opts);
        if (File.Exists(path) && !opts.ContainsKey("force"))
            return Fail($"{path} already exists.\n  Next step: pick another name with --out, or add --force to overwrite it.");

        var (svc, error) = await OpenSessionAsync(opts);
        if (svc is null) return Fail(error!);
        using var _ = svc;

        List<DiscoveredDevice> devices;
        if (all)
        {
            var wait = IntOpt(opts, "wait", 5);
            Console.WriteLine($"Sending Who-Is, listening {wait}s...");
            devices = [.. await svc.WhoIsAsync(-1, -1, TimeSpan.FromSeconds(wait))];
            if (devices.Count == 0)
                return Fail("No devices answered, so there is nothing to export.\n" +
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
                return Fail($"Device {instance} did not answer Who-Is.\n" +
                            "  Likely cause: wrong instance number, wrong adapter/subnet, or the device is behind a router/BBMD.\n" +
                            "  Next step:    run 'bacprobe discover' to list the devices that do answer, or try a longer --wait.");
            await svc.EnrichAsync([device]);
            devices = [device];
        }

        var collected = new List<ExportDevice>();
        var failed = new List<string>();
        var progress = new Progress<string>(m => Console.Write($"\r{m,-70}"));
        foreach (var d in devices)
        {
            try
            {
                collected.Add(await PointExporter.CollectAsync(svc.OpenDevice(d), d, progress));
            }
            catch (Exception ex)
            {
                failed.Add($"device {d.InstanceId} ({d.ObjectName ?? d.AddressText}): {ReadFailure(ex).Split('\n')[0]}");
            }
        }
        Console.WriteLine();

        if (collected.Count == 0)
            return Fail("Could not read any device, so nothing was exported.\n  " + string.Join("\n  ", failed) +
                        "\n  Next step: check the connection and run the command again.");

        try
        {
            PointExporter.Write(path, format, collected, opts.TryGetValue("project", out var p) && p is not null ? p : "BACprobe export");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"Could not write {path}: {ex.Message}\n" +
                        "  Likely cause: the file is open in Excel, or the folder is read-only.\n" +
                        "  Next step:    close the file or choose another place with --out.");
        }

        var points = collected.Sum(c => c.Points.Count());
        Console.WriteLine($"Exported {points} point(s) from {collected.Count} device(s) to {Path.GetFullPath(path)} ({format}).");
        if (failed.Count > 0)
        {
            Console.WriteLine($"NOT included ({failed.Count}):");
            foreach (var f in failed) Console.WriteLine("  " + f);
            return 4;
        }
        return 0;
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
