using BACprobe.Core.Bbmd;
using BACprobe.Core.Discovery;
using BACprobe.Core.Networking;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>Reads --bbmd and --ttl. Returns null when no BBMD was asked for; throws a readable error when invalid.</summary>
    private static BbmdTarget? ParseBbmd(Dictionary<string, string?> opts)
    {
        if (!opts.TryGetValue("bbmd", out var text)) return null;
        if (text is null) throw new ArgumentException("--bbmd needs the BBMD's IP address, e.g. --bbmd 10.20.30.40 (or 10.20.30.40:47809).");
        var ttl = IntOpt(opts, "ttl", BbmdTarget.DefaultTtlSeconds);
        return BbmdTarget.TryParse(text, ttl, out var target, out var error) ? target : throw new ArgumentException(error);
    }

    /// <summary>Register and print the plain-English result. Returns false if the BBMD did not accept us.</summary>
    private static async Task<bool> RegisterWithBbmdAsync(DiscoveryService svc, AdapterInfo adapter, BbmdTarget target)
    {
        var advice = target.CheckAgainst(adapter);
        if (advice.Severity != PreflightSeverity.Pass)
            Console.WriteLine($"  [NOTE] {advice.Message}\n         {advice.LikelyCause}");

        Console.WriteLine($"Registering with BBMD {target} as a foreign device (TTL {target.TtlSeconds}s)...");
        var registration = await svc.RegisterWithBbmdAsync(target);
        Console.WriteLine("  " + registration.Message);
        if (!registration.IsRegistered)
            Console.WriteLine("  Continuing with local discovery only; devices on other subnets will not answer.");
        return registration.IsRegistered;
    }
}
