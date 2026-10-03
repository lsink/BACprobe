using BACprobe.Core.Browsing;
using BACprobe.Core.Search;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>Find points by name, description, type or value across every device (or one device, or a saved job).</summary>
    private static async Task<int> FindAsync(Dictionary<string, string?> opts)
    {
        // Bare words are the query. A word with spaces in it was quoted by the user, so keep it as a phrase.
        var words = opts.Where(kv => kv.Key.StartsWith('_') && kv.Value is not null)
            .OrderBy(kv => int.Parse(kv.Key[1..], System.Globalization.CultureInfo.InvariantCulture))
            .Select(kv => kv.Value!).ToList();
        if (opts.TryGetValue("query", out var q) && q is not null) words.Add(q);
        var query = string.Join(' ', words.Select(w => w.Any(char.IsWhiteSpace) && !w.Contains('"') ? $"\"{w}\"" : w));
        if (PointSearch.Tokens(query).Count == 0)
            throw new ArgumentException("Say what to look for, e.g. 'bacprobe find zone temp', or quote a phrase: \"supply fan\". " +
                                        "Filters: is:overridden, is:fault, is:alarm, is:oos (out of service), is:problem (any of those three).");
        var max = IntOpt(opts, "max", 50);
        if (max < 1) throw new ArgumentException("--max must be 1 or more.");

        Collected collected;
        if (opts.TryGetValue("job", out var jobPath) && jobPath is not null)
        {
            var job = LoadJob(jobPath);
            var withPoints = job.Devices.Where(d => d.PointsRead).Select(d => d.ToExportDevice()).ToList();
            if (withPoints.Count == 0)
                return Fail("That job has no saved points to search.\n  Next step: save the job again with 'bacprobe job save --all'.");
            collected = new Collected(withPoints, []);
            Console.WriteLine($"Searching the saved job \"{job.Info.Name}\" ({withPoints.Count} device(s)); values are from when it was saved.");
        }
        else
        {
            var netOpts = new Dictionary<string, string?>(opts, StringComparer.OrdinalIgnoreCase);
            if (!netOpts.ContainsKey("device")) netOpts["all"] = null; // search every device that answers unless one was named
            var (result, error) = await CollectFromNetworkAsync(netOpts);
            if (result is null) return Fail(error!);
            collected = result;
        }

        var hits = PointSearch.Search(collected.Devices, query, 5000);
        var searched = collected.Devices.Sum(d => d.Points.Count());
        Console.WriteLine();
        if (hits.Count == 0)
        {
            Console.WriteLine($"No points match \"{query}\" ({searched} points on {collected.Devices.Count} device(s) searched).");
            foreach (var f in collected.Failed) Console.WriteLine("  NOT searched: " + f);
            Console.WriteLine("  Next step: try fewer or shorter words, e.g. 'temp' instead of 'temperature sensor'.");
            return 0;
        }

        Console.WriteLine($"{hits.Count} point(s) match \"{query}\" ({searched} points on {collected.Devices.Count} device(s) searched):");
        Console.WriteLine($"{"Device",-24} {"Object",-8} {"Name",-28} {"Value",-14} {"Override",-20} {"Status",-22} Description");
        foreach (var h in hits.Take(max))
        {
            var p = h.Point;
            Console.WriteLine($"{h.DeviceInstance + " " + Clip(h.DeviceName, 18),-24} {BacnetNames.ObjectTypeShort(p.Id.type) + " " + p.Id.instance,-8} " +
                              $"{Clip(p.Name ?? "-", 27),-28} {p.ValueText,-14} {p.OverrideText,-20} {Clip(p.ProblemText, 21),-22} {p.Description}");
        }
        if (hits.Count > max) Console.WriteLine($"...and {hits.Count - max} more. Narrow the search, or raise --max.");
        foreach (var f in collected.Failed) Console.WriteLine("  NOT searched: " + f);
        return collected.Failed.Count > 0 ? 4 : 0;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
