using System.Net;
using BACprobe.Core.Discovery;
using BACprobe.Core.Networking;
using BACprobe.Core.Simulation;

namespace BACprobe.Cli;

internal static partial class Program
{
    private const string Usage = """
        bacprobe - BACnet/IP test harness

        Usage:
          bacprobe adapters
          bacprobe preflight [--adapter <ip>]
          bacprobe discover  [--adapter <ip>] [--low <n> --high <n>] [--wait <seconds>] [--no-details] [--bbmd <ip[:port]> [--ttl <s>]]
          bacprobe simulate  [--adapter <ip>] [--devices <n>] [--first <instance>] [--no-rpm] [--bbmd [--bbmd-refuse] [--bbmd-port <n>]]
          bacprobe objects   --device <instance> [--adapter <ip>]
          bacprobe read      --device <instance> --object <type:n> [--property <name>] [--adapter <ip>]
          bacprobe export    (--device <instance> | --all) [--format csv|xlsx|ede] [--out <file>] [--force] [--bbmd <ip>]
          bacprobe write     --device <instance> --object <type:n> --value <v> [--priority 8] [--yes]
          bacprobe release   --device <instance> --object <type:n> [--priority 8] [--yes]

        --adapter  IPv4 address of the NIC to use (default: the only usable adapter, else you must choose).
        --low/--high  Limit Who-Is to a device instance range.
        --wait     Seconds to listen for I-Am replies (default 5).
        objects    List a device's objects with name, value and units. read: all properties of one object, e.g. --object ai:1
                   (types: ai ao av bi bo bv msi mso msv, or names like analog-input). --property reads just one.
        export     Save a point list: csv, xlsx (Excel, with a Devices sheet) or ede. Default file: bacprobe-points-<time>.csv
                   in the current folder; an existing file is never overwritten without --force.
        write      Overrides a point (asks you to confirm in plain English; default priority 8 = Manual Operator).
                   release gives it back. Every write is logged to %LOCALAPPDATA%BACprobewrite-log.txt.
        --bbmd     Register as a foreign device with a BBMD so Who-Is reaches other subnets (objects/read/write accept it too).
                   --ttl is how long the BBMD keeps you (default 300 s); BACprobe renews automatically.
        simulate   Run fake BACnet devices on this PC (Ctrl+C to stop) so you can test without hardware.
                   --devices n (default 2), --first instance (default 1001),
                   --no-rpm makes the last device refuse ReadPropertyMultiple, like older devices.
                   --bbmd also runs a fake BBMD (port 47809); --bbmd-refuse makes it refuse registrations.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var opts = ParseOptions(args.Skip(1).ToArray());
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "adapters" => Adapters(),
                "preflight" => Preflight(opts),
                "discover" => await DiscoverAsync(opts),
                "simulate" => await SimulateAsync(opts),
                "objects" => await ObjectsAsync(opts),
                "read" => await ReadAsync(opts),
                "export" => await ExportAsync(opts),
                "write" => await WriteAsync(opts),
                "release" => await ReleaseAsync(opts),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int Adapters()
    {
        var all = AdapterEnumerator.GetAdapters();
        Console.WriteLine($"{"Address",-16} {"Network",-18} {"State",-5} {"Type",-8} Name");
        foreach (var a in all)
            Console.WriteLine($"{a.Address,-16} {a.Cidr,-18} {(a.IsUp ? "up" : "down"),-5} {(a.IsVirtual ? "virtual" : "physical"),-8} {a.Name} - {a.Description}");
        return 0;
    }

    private static int Preflight(Dictionary<string, string?> opts)
    {
        var adapter = PickAdapter(opts);
        return PrintPreflight(adapter) ? 0 : 2;
    }

    private static bool PrintPreflight(AdapterInfo adapter)
    {
        Console.WriteLine($"Pre-flight for {adapter.Name} ({adapter.Address}, {adapter.Cidr})");
        var results = Core.Networking.Preflight.Run(adapter);
        foreach (var r in results)
        {
            var tag = r.Severity switch { PreflightSeverity.Pass => "PASS", PreflightSeverity.Warning => "WARN", _ => "FAIL" };
            Console.WriteLine($"  [{tag}] {r.Check}: {r.Message}");
            if (r.LikelyCause is not null) Console.WriteLine($"         Likely cause: {r.LikelyCause}");
            if (r.NextStep is not null) Console.WriteLine($"         Next step:    {r.NextStep}");
        }
        return PreflightRules.CanProceed(results);
    }

    private static async Task<int> DiscoverAsync(Dictionary<string, string?> opts)
    {
        var adapter = PickAdapter(opts);
        if (!PrintPreflight(adapter))
        {
            Console.WriteLine("\nFix the failures above, then run discover again.");
            return 2;
        }

        var low = IntOpt(opts, "low", -1);
        var high = IntOpt(opts, "high", -1);
        if ((low < 0) != (high < 0)) throw new ArgumentException("Give both --low and --high, or neither.");
        var wait = IntOpt(opts, "wait", 5);
        var bbmd = ParseBbmd(opts);

        Console.WriteLine();
        using var svc = new DiscoveryService(adapter);
        try { svc.Start(); }
        catch (Exception ex)
        {
            return Fail($"Could not open the BACnet socket: {ex.Message}\n" +
                        "  Likely cause: another program holds UDP 47808, or the adapter address changed.\n" +
                        "  Next step:    close other BACnet tools and re-run 'bacprobe preflight'.");
        }

        if (bbmd is not null) await RegisterWithBbmdAsync(svc, adapter, bbmd);
        Console.WriteLine($"Sending Who-Is{(low >= 0 ? $" {low}-{high}" : "")} to {adapter.Broadcast}" +
                          $"{(svc.BbmdRegistration is { IsRegistered: true } ? $" and via BBMD {bbmd}" : "")}, listening {wait}s...");

        var devices = await svc.WhoIsAsync(low, high, TimeSpan.FromSeconds(wait));
        if (devices.Count == 0)
        {
            Console.WriteLine("No devices answered.");
            Console.WriteLine("  Likely cause: wrong adapter/subnet, a firewall blocking UDP 47808, or devices on another subnet behind a BBMD.");
            Console.WriteLine("  Next step:    check the adapter with 'bacprobe adapters', allow bacprobe through Windows Firewall, or try a longer --wait.");
            return 3;
        }

        if (!opts.ContainsKey("no-details"))
        {
            Console.WriteLine($"{devices.Count} device(s) found; reading details...");
            await svc.EnrichAsync(devices);
        }

        Console.WriteLine();
        Console.WriteLine($"{"Instance",-9} {"Address",-22} {"Vendor",-24} {"Model",-18} {"Firmware",-12} Name");
        foreach (var d in devices)
        {
            Console.WriteLine($"{d.InstanceId,-9} {d.AddressText,-22} {d.VendorName ?? $"vendor {d.VendorId}",-24} " +
                              $"{d.ModelName ?? "-",-18} {d.FirmwareRevision ?? "-",-12} {d.ObjectName ?? "-"}");
            if (d.EnrichError is not null) Console.WriteLine($"          ! {d.EnrichError}");
        }
        return 0;
    }

    private static async Task<int> SimulateAsync(Dictionary<string, string?> opts)
    {
        var adapter = PickAdapter(opts);
        var count = IntOpt(opts, "devices", 2);
        var first = IntOpt(opts, "first", 1001);
        if (count < 1 || count > 50) throw new ArgumentException("--devices must be between 1 and 50.");

        var sims = new List<SimulatedDevice>();
        SimulatedBbmd? bbmdSim = null;
        try
        {
            if (opts.ContainsKey("bbmd") || opts.ContainsKey("bbmd-refuse"))
            {
                var bbmdPort = IntOpt(opts, "bbmd-port", 47809);
                bbmdSim = new SimulatedBbmd(adapter, bbmdPort, refuseRegistrations: opts.ContainsKey("bbmd-refuse"))
                {
                    Log = line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"),
                };
                bbmdSim.Start();
                Console.WriteLine($"Simulating a BBMD on {adapter.Address}:{bbmdPort}" +
                                  $"{(opts.ContainsKey("bbmd-refuse") ? " (refuses foreign devices)" : "")}." +
                                  $" Try: bacprobe discover --bbmd {adapter.Address}:{bbmdPort}");
            }

            for (var i = 0; i < count; i++)
            {
                var legacy = opts.ContainsKey("no-rpm") && i == count - 1;
                var sim = new SimulatedDevice(adapter, SimulatedDeviceModel.CreateSample((uint)(first + i)), supportRpm: !legacy)
                {
                    Log = line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"),
                };
                sim.Start();
                sims.Add(sim);
                Console.WriteLine($"Simulating device {first + i}{(legacy ? " (refuses ReadPropertyMultiple)" : "")} on {adapter.Address}");
            }
        }
        catch (Exception ex)
        {
            foreach (var s in sims) s.Dispose();
            bbmdSim?.Dispose();
            return Fail($"Could not start the simulator: {ex.Message}\n" +
                        "  Likely cause: another program holds UDP 47808 exclusively.\n" +
                        "  Next step:    run 'bacprobe preflight' and close the program it names.");
        }

        Console.WriteLine("Running. Run 'bacprobe discover' (or the app) from another window. Ctrl+C to stop.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }
        foreach (var s in sims) s.Dispose();
        bbmdSim?.Dispose();
        return 0;
    }

    private static AdapterInfo PickAdapter(Dictionary<string, string?> opts)
    {
        var all = AdapterEnumerator.GetAdapters();
        if (opts.TryGetValue("adapter", out var ip) && ip is not null)
        {
            if (!IPAddress.TryParse(ip, out var addr))
                throw new ArgumentException($"'{ip}' is not an IPv4 address. Run 'bacprobe adapters' to see the choices.");
            return all.FirstOrDefault(a => a.Address.Equals(addr))
                   ?? throw new ArgumentException($"No adapter has address {ip}. Run 'bacprobe adapters' to see the choices.");
        }

        var usable = all.Where(a => a.IsUp && !a.IsLoopback && !Subnet.IsLinkLocal(a.Address)).ToList();
        var physical = usable.Where(a => !a.IsVirtual).ToList();
        var pool = physical.Count > 0 ? physical : usable;
        if (pool.Count == 1) return pool[0];
        throw new ArgumentException(pool.Count == 0
            ? "No usable network adapter found. Connect to the building network, or run 'bacprobe adapters'."
            : "More than one adapter could be used. Choose one with --adapter <ip>:\n  " +
              string.Join("\n  ", pool.Select(a => $"{a.Address}  {a.Name}")));
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var opts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            opts[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
        }
        return opts;
    }

    private static int IntOpt(Dictionary<string, string?> opts, string key, int fallback)
    {
        if (!opts.TryGetValue(key, out var v)) return fallback;
        return int.TryParse(v, out var n) && n >= -1 ? n : throw new ArgumentException($"--{key} needs a number.");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
