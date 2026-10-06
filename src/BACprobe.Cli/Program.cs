using System.IO.BACnet;
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
          bacprobe discover  [--adapter <ip>] [--low <n> --high <n>] [--wait <seconds>] [--no-details] [--job <site.bacprobe>] [--bbmd <ip[:port]> [--ttl <s>]]
          bacprobe simulate  [--adapter <ip>] [--devices <n>] [--first <instance>] [--no-rpm] [--no-cov] [--cov-limit <n>] [--router <net:devices,...;...>] [--dup] [--unassigned] [--stuck] [--protected] [--still] [--objects <n>] [--outage <after,seconds>] [--faults] [--no-events] [--skew <minutes>] [--password <p>] [--differ] [--bbmd [--bbmd-refuse] [--bbmd-port <n>] [--bbmd-peer]]
          bacprobe objects   --device <instance> [--adapter <ip>]
          bacprobe read      --device <instance> --object <type:n> [--property <name>] [--adapter <ip>]
          bacprobe job save  --out <site.bacprobe> (--all | --device <n>) [--name <text>] [--notes <text>] [--bbmd <ip>] [--force]
          bacprobe job show  <site.bacprobe> [--device <n>] [--log]
          bacprobe job note  <site.bacprobe> --device <n> [--object <type:n>] --text "..."
          bacprobe export    (--device <instance> | --all) [--format csv|xlsx|ede] [--out <file>] [--force] [--bbmd <ip>]
          bacprobe routers   [--adapter <ip>] [--wait <seconds>]
          bacprobe bbmd      <ip[:port]> [--adapter <ip>] [--no-peers]
          bacprobe lesson    [<name>]
          bacprobe mstp-monitor (--port <COMn> | --list | --replay <capture.bin>) [--baud 38400|auto] [--seconds 30] [--frames] [--record <capture.bin>] [--pcap <frames.pcap> [--force]]
          bacprobe mstp-monitor --make-sample <capture.bin> [--force]
          bacprobe mstp-discover --port <COMn> [--baud 38400|auto] [--mac <0-127>] [--survey 15] [--wait 10] [--low <n> --high <n>] [--yes]
          (objects, read, write, release, watch, trend, compare, routers and export also accept --mstp <COMn> [--baud ..] [--mac ..] [--yes] to connect through an MS/TP trunk)
          bacprobe compare   --device <a> --with <b> [--object <type:n>] [--all] [--inputs]
          bacprobe alarms    [--device <instance>] [--wait <seconds>]
          bacprobe alarms    --device <instance> --ack <type:n> [--yes]
          bacprobe find      <words...> [--device <n> | --job <file>] [--max <n>]
          bacprobe who-has   ("<exact point name>" | <type:n>) [--low <n> --high <n>] [--wait <seconds>]
          bacprobe trend     --device <instance> --object tl:<n> [--last <n> | --all] [--out <file.csv|xlsx>] [--force]
          bacprobe watch     --device <instance> [--object <type:n>] [--interval <seconds>] [--poll] [--cov-lifetime <seconds>]
          bacprobe write     --device <instance> --object <type:n> --value <v> [--priority 8] [--yes]
          bacprobe write     --device <instance> --object <type:n> --property <name> --value <v> [--yes]
          bacprobe release   --device <instance> --object <type:n> [--priority 8] [--yes]
          bacprobe clock     --device <instance> [--utc] [--yes]
          bacprobe restart   --device <instance> (--warm | --cold) [--password <p>] [--yes]
          bacprobe mute      --device <instance> [--minutes 10] [--initiation] [--password <p>] [--yes]
          bacprobe unmute    --device <instance> [--address <ip[:port]>] [--password <p>] [--yes]

        --adapter  IPv4 address of the NIC to use (default: the only usable adapter, else you must choose).
        --low/--high  Limit Who-Is to a device instance range.
        --wait     Seconds to listen for I-Am replies (default 5).
        objects    List a device's objects with name, value and units. read: all properties of one object, e.g. --object ai:1
                   (types: ai ao av bi bo bv msi mso msv, or names like analog-input). --property reads just one.
        export     Save a point list: csv, xlsx (Excel, with a Devices sheet) or ede. Default file: bacprobe-points-<time>.csv
                   in the current folder; an existing file is never overwritten without --force.
        routers    Ask for routers and list the networks each one says it can reach. 'discover' also prints a network map
                   (which devices are on which network) and flags networks that look wrong.
        bbmd       Read a BBMD's broadcast and foreign device tables (and each listed peer's broadcast table) and flag what looks
                   wrong: one-way peers, missing or duplicate entries, one-hop masks, two BBMDs on one subnet. Read-only.
        mstp-monitor  Listen to an MS/TP trunk through a USB-RS485 adapter WITHOUT transmitting. Reports the masters seen, how busy the
                   trunk is, the token loop time, damaged frames, nodes that do not take the token, and Max Master set too low, each with
                   a likely cause and next step. --frames prints every frame in plain English; --record saves the raw bytes and --replay
                   analyses a saved capture later (no adapter needed). --pcap writes the good frames as a Wireshark file. Common baud rates here: 38400 and 76800.
        compare    Line two devices up point by point and print what differs (a setpoint someone changed, a point missing on one, a point
                   named differently). Live inputs differ by nature and are left out unless --inputs. --object av:1 compares that one object's
                   properties on both. --all also lists what is the same. Exit code 5 means differences were found. Reads only.
        alarms     List every device's active and unacknowledged alarms (GetEventInformation; for a device without it, the points
                   whose status says in alarm or fault), worst first, each with when it started, what is not acknowledged, and a
                   likely cause and next step. Exit code 5 means alarms were found. --ack acknowledges one point's alarm after you
                   confirm; it changes nothing on the point, and is logged like a write.
        mstp-discover  JOIN an MS/TP trunk as a master (this TRANSMITS) and list the devices on it. It listens first and refuses if the trunk is
                   too noisy or too quiet, the adapter's latency timer is over 2 ms, or the MAC is taken; it picks the lowest free master address
                   (or use --mac), uses Max_Master 127, and asks you to type JOIN (or --yes). Built on the library's own MS/TP master; only tested
                   against a simulated trunk, never a real one.
        lesson     Short plain-English lessons (BBMDs, the MS/TP token, wiring, priorities ...). A finding that has one prints
                   "Lesson: bacprobe lesson <name>" under its next step. No name lists them all.
        find       Search every device for points by words in the name, description, type, units or value (all words must match).
                   Quote a phrase, e.g. "supply fan". Filters, alone or with words: is:overridden, is:fault, is:alarm, is:oos
                   (out of service), is:problem (fault, alarm or out of service). --job searches a saved job offline.
        who-has    Ask the network which device has a point, by exact name or object (e.g. ai:1): one broadcast, no need to read every
                   device. Reaches this subnet and networks behind its routers, not other subnets through a BBMD.
        trend      Show a trend log's settings and recorded history (latest 20 records by default); --out saves all of it as CSV or Excel.
        watch      Print a line whenever a point value or override changes. Uses COV where the device supports it (--poll forces polling;
                   --interval is the polling interval, default 2 s). Ctrl+C to stop.
        write      Overrides a point (asks you to confirm in plain English; default priority 8 = Manual Operator).
                   With --property it changes a setting instead (high-limit, description, cov-increment...): no priority, nothing to
                   release, and the old value goes in the write log.
                   release gives it back. Every write is logged to %LOCALAPPDATA%BACprobewrite-log.txt.
        clock      Set a device's clock to this PC's time (TimeSynchronization; --utc sends UTC for devices with their own UTC offset),
                   then read it back to check.
        restart    ReinitializeDevice: --warm restarts the program keeping its settings, --cold is like a power cycle. You type the device
                   number to confirm. Some devices need --password.
        mute       DeviceCommunicationControl: the device stops talking on the network for --minutes (1-60, default 10; never "forever"),
                   or with --initiation it still answers but sends nothing on its own. unmute undoes it. You type the device number to confirm.
        --bbmd     Register as a foreign device with a BBMD so Who-Is reaches other subnets (objects/read/write accept it too).
                   --ttl is how long the BBMD keeps you (default 300 s); BACprobe renews automatically.
        job        Keep a site visit in one file. 'job save' reads the devices and points and stores them (plus any
                   --name/--notes); 'job show <file> [--device n] [--log]' browses it offline; 'export --job <file>'
                   exports from it. Saved values are a snapshot, not live. Existing files need --force.
        discover   also runs a network check: duplicate device numbers, duplicate addresses, never-commissioned devices, and with
                   --job <file> what is missing, new, moved or changed compared with a saved job.
        simulate   Run fake BACnet devices on this PC (Ctrl+C to stop) so you can test without hardware.
                   --devices n (default 2), --first instance (default 1001),
                   --no-rpm makes the last device refuse ReadPropertyMultiple, like older devices.
                   --no-cov makes the last device refuse COV; --cov-limit n makes every device accept only n subscriptions.
                   --router "1001:3" adds a router to network 1001 with 3 virtual devices (visible, not readable); commas add networks,
                   semicolons add routers, e.g. --router "1001:3,1002:0;1001:1".
                   --stuck holds Occupied (bv:1) at priority 5, so writes at 8 are accepted but ignored; --protected makes Zone Setpoint
                   (av:1) refuse writes. (The damper always limits itself to 0-100.)
                   --dup adds an impostor with the first device's number; --unassigned adds a device with the reserved number 4194303.
                   --objects n adds n spare points to each device, so its object list needs segmented replies (a big controller).
                   --outage 20,25 makes the first device go silent after 20 s for 25 s and forget its COV subscriptions (a restart).
                   --faults gives the first device problems to find: AI 2 open loop (reads -40, a fault alarm), AI 1 above its high
                   alarm limit, BV 1 out of service, and MSI 1 with an earlier alarm that cleared but was never acknowledged.
                   --no-events makes the first device refuse GetEventInformation, so 'alarms' falls back to the points' status.
                   --differ makes the second device differ from the first (Zone Setpoint 68, an extra point), to try 'bacprobe compare'.
                   --skew 47 sets the first device's clock 47 minutes ahead of this PC, for the device clock check.
                   --password p makes every device ask for that password before a restart or mute.
                   --still stops the sensors drifting (by default analog inputs wander and Fan Status follows Fan Command).
                   --bbmd also runs a fake BBMD (port 47809); --bbmd-refuse makes it refuse registrations. --bbmd-peer adds a second
                   BBMD (next port) with table mistakes for 'bacprobe bbmd' to find: listed one-hop, and it does not list the first back.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } // so degree signs survive pipes and redirects
        catch (IOException) { /* no console attached */ }

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
                "watch" => await WatchAsync(opts),
                "trend" => await TrendAsync(opts),
                "find" => await FindAsync(opts),
                "who-has" => await WhoHasAsync(opts),
                "routers" => await RoutersAsync(opts),
                "bbmd" => await BbmdCheckAsync(opts),
                "compare" => await CompareAsync(opts),
                "alarms" => await AlarmsAsync(opts),
                "lesson" => Lesson(args.Skip(1).ToArray()),
                "mstp-monitor" => await MstpMonitorAsync(opts),
                "mstp-discover" => await MstpDiscoverAsync(opts),
                "export" => await ExportAsync(opts),
                "job" => await JobAsync(opts),
                "write" => await WriteAsync(opts),
                "release" => await ReleaseAsync(opts),
                "clock" => await ClockAsync(opts),
                "restart" => await RestartAsync(opts),
                "mute" => await MuteAsync(opts),
                "unmute" => await UnmuteAsync(opts),
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
            var firewall = PreflightRules.CheckFirewall(FirewallInspector.Inspect(adapter)); // read again: a Windows prompt may have been answered since
            if (firewall.Severity != PreflightSeverity.Pass)
            {
                Console.WriteLine($"  Likely cause: Windows Firewall. {firewall.Message}");
                Console.WriteLine($"  Next step:    {firewall.NextStep} Then run discover again.");
                return 3;
            }
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
        var addressWidth = Math.Max(22, devices.Max(d => d.AddressText.Length)); // routed devices have longer addresses
        Console.WriteLine($"{"Instance",-9} {"Address".PadRight(addressWidth)} {"Vendor",-24} {"Model",-18} {"Firmware",-12} {"Reply",7} Name");
        foreach (var d in devices)
        {
            Console.WriteLine($"{d.InstanceId,-9} {d.AddressText.PadRight(addressWidth)} {d.VendorName ?? $"vendor {d.VendorId}",-24} " +
                              $"{d.ModelName ?? "-",-18} {d.FirmwareRevision ?? "-",-12} {(d.ResponseTime is { } rt ? $"{rt.TotalMilliseconds:0} ms" : "-"),7} {d.ObjectName ?? "-"}");
            if (d.EnrichError is not null) Console.WriteLine($"          ! {d.EnrichError}");
        }
        var map = svc.BuildNetworkMap(devices, scanWasFiltered: low >= 0);
        PrintNetworkMap(map, adapter.Cidr);
        PrintNetworkCheck([.. svc.CheckNetwork(), .. map.Findings], opts, devices);
        return 0;
    }

    private static async Task<int> SimulateAsync(Dictionary<string, string?> opts)
    {
        var adapter = PickAdapter(opts);
        var count = IntOpt(opts, "devices", 2);
        var first = IntOpt(opts, "first", 1001);
        if (count < 1 || count > 50) throw new ArgumentException("--devices must be between 1 and 50.");
        var padding = IntOpt(opts, "objects", 0);
        if (padding is < 0 or > 5000) throw new ArgumentException("--objects must be between 0 and 5000.");

        var sims = new List<SimulatedDevice>();
        var routers = new List<SimulatedRouter>();
        SimulatedBbmd? bbmdSim = null, peerSim = null;
        Timer? outageTimer = null;
        try
        {
            if (opts.ContainsKey("bbmd") || opts.ContainsKey("bbmd-refuse"))
            {
                var bbmdPort = IntOpt(opts, "bbmd-port", 47809);
                IReadOnlyList<BACprobe.Core.Bbmd.BdtEntry>? table = null;
                if (opts.ContainsKey("bbmd-peer"))
                {
                    // Two classic mistakes for 'bacprobe bbmd' to find: the peer is listed one-hop (a directed broadcast,
                    // here with a /24 mask), and the peer's own table does not list this BBMD back.
                    var self = new IPEndPoint(adapter.Address, bbmdPort);
                    var peer = new IPEndPoint(adapter.Address, bbmdPort + 1);
                    table = [new(self, IPAddress.Broadcast), new(peer, IPAddress.Parse("255.255.255.0"))];
                    peerSim = new SimulatedBbmd(adapter, bbmdPort + 1) { Log = line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} (peer) {line}") };
                    peerSim.Start();
                    Console.WriteLine($"Simulating a peer BBMD on {peer} whose table lists only itself.");
                }
                bbmdSim = new SimulatedBbmd(adapter, bbmdPort, refuseRegistrations: opts.ContainsKey("bbmd-refuse"), broadcastTable: table)
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
                var model = SimulatedDeviceModel.CreateSample((uint)(first + i), null, stuck: opts.ContainsKey("stuck"), protectedSetpoint: opts.ContainsKey("protected"));
                if (i == 0 && opts.ContainsKey("faults")) model.AddSampleProblems();
                if (i == 0 && opts.ContainsKey("skew")) model.ClockSkew = TimeSpan.FromMinutes(IntOpt(opts, "skew", 0));
                if (i == 1 && opts.ContainsKey("differ")) // for 'bacprobe compare': the second device was set up differently
                {
                    model.Write(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1), BacnetPropertyIds.PROP_PRESENT_VALUE,
                        new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 68f), 16, out _);
                    model.AddAnalogValue(900, "Night Setback", "Unoccupied setback offset", 4, BacnetUnitsId.UNITS_DEGREES_FAHRENHEIT, commandable: false);
                }
                for (var k = 1; k <= padding; k++) // a big controller: its object list no longer fits in one packet
                    model.AddAnalogValue((uint)(1000 + k), $"Spare Value {k}", "Padding point", 0, BacnetUnitsId.UNITS_NO_UNITS, commandable: false);
                var sim = new SimulatedDevice(adapter, model, supportRpm: !legacy, drift: !opts.ContainsKey("still"),
                    supportCov: !(opts.ContainsKey("no-cov") && i == count - 1), covLimit: IntOpt(opts, "cov-limit", 0),
                    supportEvents: !(opts.ContainsKey("no-events") && i == 0))
                {
                    Password = opts.GetValueOrDefault("password"),
                    Log = line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"),
                };
                sim.Start();
                sims.Add(sim);
                Console.WriteLine($"Simulating device {first + i}{(legacy ? " (refuses ReadPropertyMultiple)" : "")}" +
                                  $"{(opts.ContainsKey("no-events") && i == 0 ? " (refuses GetEventInformation)" : "")} on {adapter.Address}");
            }

            if (opts.TryGetValue("router", out var routerSpec))
            {
                // "1001:3" = one router to network 1001 with 3 devices. Commas add networks to a router; semicolons add routers.
                var index = 0;
                foreach (var one in (routerSpec ?? "1001:3").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var nets = one.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(x => x.Split(':')).Select(a => new SimNetwork(ushort.Parse(a[0]), a.Length > 1 ? int.Parse(a[1]) : 3)).ToList();
                    var router = new SimulatedRouter(adapter, nets, index++) { Log = line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}") };
                    router.Start();
                    routers.Add(router);
                    Console.WriteLine($"Simulating a router to network(s) {string.Join(", ", nets.Select(n => $"{n.Number} ({n.DeviceCount} devices)"))}");
                }
            }

            if (opts.TryGetValue("outage", out var outageSpec))
            {
                // "20,25": after 20 s the first device goes silent for 25 s and forgets its COV subscriptions, like a restart.
                var parts = (outageSpec ?? "20,25").Split(',');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var after) || !int.TryParse(parts[1], out var length) || after < 0 || length < 1)
                    throw new ArgumentException("--outage takes \"after,seconds\", e.g. --outage 20,25.");
                var target = sims[0];
                outageTimer = new Timer(_ => target.GoSilent(TimeSpan.FromSeconds(length)), null, TimeSpan.FromSeconds(after), Timeout.InfiniteTimeSpan);
                Console.WriteLine($"Device {first} will go silent after {after} s for {length} s.");
            }

            // Problems to find: an impostor reusing the first device's number, and a never-commissioned device.
            if (opts.ContainsKey("dup"))
            {
                var impostor = new SimulatedDevice(adapter, SimulatedDeviceModel.CreateSample((uint)first, $"SIM-IMPOSTOR-{first}"),
                    drift: !opts.ContainsKey("still")) { Log = line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}") };
                impostor.Start();
                sims.Add(impostor);
                Console.WriteLine($"Simulating an impostor that also claims device number {first}");
            }
            if (opts.ContainsKey("unassigned"))
            {
                var fresh = new SimulatedDevice(adapter, SimulatedDeviceModel.CreateSample(4194303, "SIM-UNCOMMISSIONED"),
                    drift: !opts.ContainsKey("still")) { Log = line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}") };
                fresh.Start();
                sims.Add(fresh);
                Console.WriteLine("Simulating a device that was never given a device number (4194303)");
            }
        }
        catch (Exception ex)
        {
            foreach (var s in sims) s.Dispose();
            foreach (var r in routers) r.Dispose();
            bbmdSim?.Dispose();
            peerSim?.Dispose();
            return Fail($"Could not start the simulator: {ex.Message}\n" +
                        "  Likely cause: another program holds UDP 47808 exclusively.\n" +
                        "  Next step:    run 'bacprobe preflight' and close the program it names.");
        }

        Console.WriteLine("Running. Run 'bacprobe discover' (or the app) from another window. Ctrl+C to stop.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }
        outageTimer?.Dispose();
        foreach (var s in sims) s.Dispose();
        foreach (var r in routers) r.Dispose();
        bbmdSim?.Dispose();
        peerSim?.Dispose();
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
        var positional = 0;
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                opts[$"_{positional++}"] = args[i]; // bare words, e.g. the sub-command and file in 'job show site.bacprobe'
                continue;
            }
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
