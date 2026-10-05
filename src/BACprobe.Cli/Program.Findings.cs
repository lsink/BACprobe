using BACprobe.Core.Discovery;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>
    /// Print findings the way every command does: a tagged title, then the detail, the likely cause and the next step
    /// (empty parts are left out). Worst first is the caller's job.
    /// </summary>
    private static void PrintFindings(IEnumerable<NetworkFinding> findings, string problemTag = "PROBLEM", string infoTag = "NOTE")
    {
        foreach (var f in findings)
        {
            var tag = f.Severity switch { FindingSeverity.Problem => problemTag, FindingSeverity.Warning => "WARNING", _ => infoTag };
            Console.WriteLine($"  [{tag}] {f.Title}");
            if (f.Detail.Length > 0) Console.WriteLine($"            {f.Detail}");
            if (f.LikelyCause.Length > 0) Console.WriteLine($"            Likely cause: {f.LikelyCause}");
            if (f.NextStep.Length > 0) Console.WriteLine($"            Next step:    {f.NextStep}");
        }
    }
}
