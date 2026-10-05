using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Mstp;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>
    /// Connect through an MS/TP trunk instead of an IP adapter (<c>--mstp COM5</c>, with <c>--baud</c>): listen first, show what was heard and
    /// the plan, and only transmit after a typed confirmation (or <c>--yes</c>). Every command that opens a session accepts this.
    /// </summary>
    private static async Task<(DiscoveryService?, string?)> OpenMstpSessionAsync(Dictionary<string, string?> opts)
    {
        var port = opts.GetValueOrDefault("mstp") ?? opts.GetValueOrDefault("port");
        if (string.IsNullOrWhiteSpace(port))
            return (null, "Say which serial port: --mstp COM5 --baud 38400. 'bacprobe mstp-monitor --list' shows the ports.");

        int baud;
        if (string.Equals(opts.GetValueOrDefault("baud"), "auto", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Finding the trunk's baud rate on {port} (listening only)...");
            try
            {
                var (picked, _, explanation) = await Task.Run(() => MstpPortCapture.DetectBaud(port, 3, null));
                Console.WriteLine(explanation);
                if (picked is null) return (null, "Cannot join without knowing the baud rate. Give it with --baud 38400.");
                baud = picked.Value;
            }
            catch (IOException ex) { return (null, ex.Message); }
        }
        else baud = IntOpt(opts, "baud", 38400);

        byte? mac = null;
        if (opts.ContainsKey("mac"))
        {
            var m = IntOpt(opts, "mac", -1);
            if (m is < 0 or > 255) return (null, "--mac needs a number from 0 to 127 (a master address).");
            mac = (byte)m;
        }

        MstpSurveyResult result;
        try
        {
            result = await MstpActive.SurveyAsync(port, baud, mac, IntOpt(opts, "survey", 15), new Progress<string>(Console.WriteLine));
        }
        catch (IOException ex) { return (null, ex.Message); }

        var s = result.Survey;
        Console.WriteLine($"Heard {s.Frames} good frames ({s.Errors} damaged); masters: {string.Join(", ", s.Nodes.Where(n => n.Role == "master").Select(n => n.Mac))}; " +
                          $"all addresses seen: {string.Join(", ", s.Nodes.Select(n => n.Mac))}.");
        PrintFindings(result.Plan.Notes, problemTag: "REFUSED", infoTag: "plan");
        if (!result.Plan.CanJoin) return (null, "Not joining the trunk. Nothing was transmitted.");

        Console.WriteLine();
        Console.WriteLine(MstpActive.ConfirmationText(port, baud, result.Plan));
        if (!opts.ContainsKey("yes"))
        {
            if (Console.IsInputRedirected) return (null, "Confirmation needed: add --yes to join the trunk (nothing was transmitted).");
            Console.Write("Type JOIN to continue, anything else to stop: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "JOIN", StringComparison.OrdinalIgnoreCase))
                return (null, "Cancelled. Nothing was transmitted.");
        }

        try
        {
            var svc = MstpActive.Join(port, baud, result.Plan);
            Console.WriteLine($"Joined as master MAC {result.Plan.Mac}. Waiting for the token...");
            return (svc, null);
        }
        catch (Exception ex)
        {
            return (null, $"Could not join the trunk: {ex.Message}\n  Likely cause: another program has {port} open, or the adapter was unplugged.\n" +
                          "  Next step:    close the other program and try again.");
        }
    }

    /// <summary>Join an MS/TP trunk, ask every device to identify itself, and list them. Transmits (after a confirmation).</summary>
    private static async Task<int> MstpDiscoverAsync(Dictionary<string, string?> opts)
    {
        if (!opts.ContainsKey("mstp") && opts.TryGetValue("port", out var p) && p is not null) opts["mstp"] = p;
        var (svc, error) = await OpenMstpSessionAsync(opts);
        if (svc is null) return Fail(error!);
        using var _ = svc;

        var wait = IntOpt(opts, "wait", 10);
        var low = IntOpt(opts, "low", -1);
        var high = IntOpt(opts, "high", -1);
        if ((low < 0) != (high < 0)) throw new ArgumentException("Give both --low and --high, or neither.");

        // The ring needs a moment to hand BACprobe its first token; keep asking until something answers or the wait runs out.
        Console.WriteLine($"Sending Who-Is{(low >= 0 ? $" {low}-{high}" : "")}, for up to {wait}s...");
        var devices = (IReadOnlyList<DiscoveredDevice>)[];
        var deadline = DateTime.UtcNow.AddSeconds(wait);
        while (devices.Count == 0 && DateTime.UtcNow < deadline)
            devices = await svc.WhoIsAsync(low, high, TimeSpan.FromSeconds(Math.Min(3, wait)));

        if (devices.Count == 0)
        {
            Console.WriteLine("No devices answered.");
            Console.WriteLine("  Likely cause: BACprobe has not been given the token yet (the masters only poll for new nodes now and then), or the devices on this trunk are slaves that need a longer wait.");
            Console.WriteLine("  Next step:    try a longer --wait 30, and check the trunk with 'bacprobe mstp-monitor'.");
            return 3;
        }

        await svc.EnrichAsync(devices);
        Console.WriteLine();
        Console.WriteLine($"{"Instance",-9} {"Address",-12} {"Vendor",-24} {"Model",-18} Name");
        foreach (var d in devices)
            Console.WriteLine($"{d.InstanceId,-9} {d.AddressText,-12} {d.VendorName ?? $"vendor {d.VendorId}",-24} {d.ModelName ?? "-",-18} {d.ObjectName ?? "-"}");
        Console.WriteLine("\nDisconnecting: the ring pauses for a moment while the other masters notice BACprobe has gone.");
        return 0;
    }
}
