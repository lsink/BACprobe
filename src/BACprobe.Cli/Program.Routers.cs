using BACprobe.Core.Discovery;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>List the routers that answer and the networks each says it can reach.</summary>
    private static async Task<int> RoutersAsync(Dictionary<string, string?> opts)
    {
        var (svc, error) = await OpenSessionAsync(opts);
        if (svc is null) return Fail(error!);
        using var _ = svc;

        var wait = IntOpt(opts, "wait", 3);
        Console.WriteLine($"Asking for routers, listening {wait}s...");
        svc.AskForRouters();
        await Task.Delay(TimeSpan.FromSeconds(wait));

        var routers = svc.Routers;
        if (routers.Count == 0)
        {
            Console.WriteLine("No routers answered.");
            Console.WriteLine("  Likely cause: there is no router on this network, the router does not answer \"who is a router\" requests, or UDP 47808 is blocked.");
            Console.WriteLine("  Next step:    if you expect an MS/TP trunk, run 'bacprobe discover' and check whether devices appear with a network number.");
            return 3;
        }
        foreach (var r in routers)
            Console.WriteLine($"  Router {r.Address}: network(s) {string.Join(", ", r.Networks)}");
        return 0;
    }

    /// <summary>Print which devices are on which network.</summary>
    private static void PrintNetworkMap(NetworkMap map, string localLabel)
    {
        Console.WriteLine();
        Console.WriteLine("Network map:");
        foreach (var line in map.Lines(localLabel)) Console.WriteLine("  " + line);
    }
}
