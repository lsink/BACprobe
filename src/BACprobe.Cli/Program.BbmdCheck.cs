using BACprobe.Core.Bbmd;
using BACprobe.Core.Discovery;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>'bacprobe bbmd &lt;ip[:port]&gt;': read a BBMD's tables (and its peers'), and say what looks wrong. Read-only.</summary>
    private static async Task<int> BbmdCheckAsync(Dictionary<string, string?> opts)
    {
        var text = opts.GetValueOrDefault("_0") ?? opts.GetValueOrDefault("bbmd");
        if (text is null)
            throw new ArgumentException("Say which BBMD to check, e.g. 'bacprobe bbmd 10.20.30.40' (or 10.20.30.40:47809).");
        if (!BbmdTarget.TryParse(text, BbmdTarget.DefaultTtlSeconds, out var target, out var error)) throw new ArgumentException(error);

        var adapter = PickAdapter(opts);
        var readPeers = !opts.ContainsKey("no-peers");
        Console.WriteLine($"Checking BBMD {target} from {adapter.Address} ({adapter.Name}){(readPeers ? ", and the peers it lists" : "")}...");
        var report = await BbmdChecker.CheckAsync(target!.EndPoint, adapter, readPeers);

        Console.WriteLine();
        if (report.Bdt.Ok)
        {
            Console.WriteLine($"Broadcast table ({report.BdtRows.Count} entr{(report.BdtRows.Count == 1 ? "y" : "ies")}):");
            if (report.BdtRows.Count > 0) Console.WriteLine($"  {"BBMD",-22} {"Mask",-16} {"Distribution",-28} Note");
            foreach (var r in report.BdtRows) Console.WriteLine($"  {r.Address,-22} {r.Mask,-16} {r.Distribution,-28} {r.Note}");
        }
        else Console.WriteLine($"Broadcast table: not read ({Describe(report.Bdt.Status)}).");

        Console.WriteLine();
        if (report.Fdt.Ok)
        {
            Console.WriteLine($"Foreign device table ({report.FdtRows.Count} entr{(report.FdtRows.Count == 1 ? "y" : "ies")}):");
            if (report.FdtRows.Count > 0) Console.WriteLine($"  {"Device",-22} {"TTL",-8} {"Left",-8} Note");
            foreach (var r in report.FdtRows) Console.WriteLine($"  {r.Device,-22} {r.Ttl,-8} {r.Remaining,-8} {r.Note}");
        }
        else Console.WriteLine($"Foreign device table: not read ({Describe(report.Fdt.Status)}).");

        Console.WriteLine();
        Console.WriteLine("Findings:");
        foreach (var f in report.Findings.OrderByDescending(f => f.Severity))
        {
            var tag = f.Severity switch { FindingSeverity.Problem => "PROBLEM", FindingSeverity.Warning => "WARN", _ => "NOTE" };
            Console.WriteLine($"  [{tag}] {f.Title}");
            Console.WriteLine($"         {f.Detail}");
            if (f.Severity != FindingSeverity.Info)
            {
                Console.WriteLine($"         Likely cause: {f.LikelyCause}");
                Console.WriteLine($"         Next step:    {f.NextStep}");
            }
        }
        return report.Findings.Any(f => f.Severity == FindingSeverity.Problem) ? 3 : 0;
    }

    private static string Describe(TableReadStatus s) => s switch
    {
        TableReadStatus.Refused => "the BBMD refused",
        TableReadStatus.NoAnswer => "no answer",
        TableReadStatus.SendFailed => "could not send from this PC",
        _ => "ok",
    };
}
