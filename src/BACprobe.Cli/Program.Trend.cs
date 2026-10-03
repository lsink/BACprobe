using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Export;
using BACprobe.Core.Trends;

namespace BACprobe.Cli;

internal static partial class Program
{
    private const int TrendRowsShown = 40;

    /// <summary>Show a trend log's settings and recorded history, and optionally save it as CSV or Excel.</summary>
    private static async Task<int> TrendAsync(Dictionary<string, string?> opts)
    {
        if (!opts.TryGetValue("object", out var spec) || spec is null || !BacnetNames.TryParseObject(spec, out var id))
            throw new ArgumentException("Say which trend log with --object tl:<instance>, e.g. --object tl:1. 'bacprobe objects' shows which ones a device has.");
        if (id.type != BacnetObjectTypes.OBJECT_TRENDLOG)
            throw new ArgumentException($"{BacnetNames.ObjectLabel(id)} is not a trend log. Use --object tl:<instance>.");

        int? last = opts.ContainsKey("all") ? null : opts.ContainsKey("last") ? IntOpt(opts, "last", 20) : opts.ContainsKey("out") ? null : 20;
        if (last is 0) throw new ArgumentException("--last needs a number of records, 1 or more (or use --all).");

        string? outPath = null;
        ExportFormat format = ExportFormat.Csv;
        if (opts.TryGetValue("out", out outPath) && outPath is not null)
        {
            format = PointExporter.FormatFromPath(outPath)
                     ?? throw new ArgumentException("Trend data can be saved as .csv or .xlsx. Name the file accordingly.");
            if (File.Exists(outPath) && !opts.ContainsKey("force"))
                return Fail($"{outPath} already exists.\n  Next step: pick another name with --out, or add --force to overwrite it.");
        }

        var (svc, device, error) = await ConnectToDeviceAsync(opts);
        if (svc is null || device is null) return Fail(error!);
        using var _ = svc;

        var reader = svc.OpenTrendLogs(device);
        TrendLogInfo info;
        IReadOnlyList<TrendRecord> records;
        try
        {
            info = await reader.ReadInfoAsync(id);
            if (info.RecordCount is null)
                return Fail($"The device did not say how many records {BacnetNames.ObjectLabel(id)} holds.\n" +
                            "  Likely cause: that trend log does not exist on this device, or it is not a standard trend log.\n" +
                            $"  Next step:    run 'bacprobe objects --device {device.InstanceId}' to see what exists.");

            Console.WriteLine($"Device {device.InstanceId} / {BacnetNames.ObjectLabel(id)}: {info.Name ?? "(unnamed)"}");
            Console.WriteLine($"  Logging:      {info.Source ?? "unknown"}{(info.Units is null ? "" : $" in {info.Units}")}");
            Console.WriteLine($"  Enabled:      {(info.Enabled is { } e ? (e ? "yes" : "NO - the log is switched off, so no new records are being added") : "unknown")}");
            Console.WriteLine($"  Interval:     {(info.LogInterval is { } i ? $"every {i.TotalSeconds:0.##} s" : "on change, or unknown")}");
            Console.WriteLine($"  Records:      {info.RecordCount} in the device{(info.BufferSize is { } b ? $" (room for {b})" : "")}");
            if (info.Overwritten is { } lost and > 0)
                Console.WriteLine($"  Note:         {lost} older record(s) were overwritten as the buffer filled; they are gone.");

            var progress = new Progress<int>(n => Console.Write($"\rReading records: {n} of {last ?? (int)info.RecordCount.Value}   "));
            records = await reader.ReadRecordsAsync(id, info.RecordCount.Value, last, progress);
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            return Fail(ReadFailure(ex));
        }

        if (records.Count == 0)
        {
            Console.WriteLine("The log has no records to show.");
            return 0;
        }

        var stats = TrendStats.Summarize(records);
        if (stats is not null)
            Console.WriteLine($"  Summary:      {stats.Count} values from {stats.First:yyyy-MM-dd HH:mm:ss} to {stats.Last:yyyy-MM-dd HH:mm:ss} (device clock); " +
                              $"min {stats.Min:0.##}, max {stats.Max:0.##}, average {stats.Average:0.##}{(info.Units is null ? "" : " " + info.Units)}");
        Console.WriteLine();

        var shown = records.Count > TrendRowsShown ? records.Skip(records.Count - TrendRowsShown).ToList() : records.ToList();
        if (records.Count > shown.Count) Console.WriteLine($"(showing the latest {shown.Count} of {records.Count} records read)");
        Console.WriteLine($"{"Time",-20} {"Value",-12} Notes");
        foreach (var r in shown)
            Console.WriteLine($"{r.Time:yyyy-MM-dd HH:mm:ss}  {(r.IsValue ? r.Text : "-"),-12} {(r.IsValue ? "" : r.Text)}{(r.Flags.Length > 0 ? $" [{r.Flags}]" : "")}");

        if (outPath is not null)
        {
            try
            {
                TrendExporter.Write(outPath, format, info.Name ?? $"Device {device.InstanceId}", device.InstanceId,
                    info.Name ?? BacnetNames.ObjectLabel(id), info, records);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail($"Could not write {outPath}: {ex.Message}\n" +
                            "  Likely cause: the file is open in Excel, or the folder is read-only.\n" +
                            "  Next step:    close the file or choose another place with --out.");
            }
            Console.WriteLine($"\nSaved {records.Count} record(s) to {Path.GetFullPath(outPath)} ({format}).");
        }
        return 0;
    }
}
