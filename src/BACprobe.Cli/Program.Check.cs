using BACprobe.Core.Discovery;
using BACprobe.Core.Jobs;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>Print the network check: conflicts among the devices that answered, and (with --job) differences from a saved job.</summary>
    private static void PrintNetworkCheck(IReadOnlyList<NetworkFinding> conflicts, Dictionary<string, string?> opts,
        IReadOnlyList<DiscoveredDevice> devices)
    {
        var findings = new List<NetworkFinding>(conflicts);
        findings.AddRange(DeviceHealth.Check(devices));
        string? jobName = null;
        if (opts.TryGetValue("job", out var jobPath) && jobPath is not null)
        {
            var job = LoadJob(jobPath);
            jobName = job.Info.Name;
            findings.AddRange(NetworkCheck.Compare(job.Devices, devices));
        }

        Console.WriteLine();
        Console.WriteLine($"Network check{(jobName is null ? "" : $" (compared with job \"{jobName}\")")}: {NetworkCheck.Summarize(findings)}");
        foreach (var f in findings.OrderByDescending(f => f.Severity))
        {
            var tag = f.Severity switch { FindingSeverity.Problem => "PROBLEM", FindingSeverity.Warning => "WARNING", _ => "NOTE   " };
            Console.WriteLine($"  [{tag}] {f.Title}");
            Console.WriteLine($"            {f.Detail}");
            Console.WriteLine($"            Likely cause: {f.LikelyCause}");
            Console.WriteLine($"            Next step:    {f.NextStep}");
        }
    }
}
