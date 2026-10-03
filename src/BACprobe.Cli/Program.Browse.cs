using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Networking;

namespace BACprobe.Cli;

internal static partial class Program
{
    private static async Task<int> ObjectsAsync(Dictionary<string, string?> opts)
    {
        var (svc, device, error) = await ConnectToDeviceAsync(opts);
        if (svc is null || device is null) return Fail(error!);
        using var _ = svc;

        var browser = svc.OpenDevice(device);
        try
        {
            Console.WriteLine($"Device {device.InstanceId} at {device.AddressText}: reading object list...");
            var ids = await browser.ReadObjectListAsync();
            Console.WriteLine($"{ids.Count} object(s). Reading names and values...");
            var summaries = await browser.ReadSummariesAsync(ids);

            Console.WriteLine();
            Console.WriteLine($"{"Object",-8} {"Type",-22} {"Name",-26} {"Value",-14} Description");
            foreach (var s in summaries)
                Console.WriteLine($"{BacnetNames.ObjectTypeShort(s.Id.type),-3} {s.Id.instance,-4} {s.TypeName,-22} {s.Name ?? "-",-26} {s.ValueText,-14} {s.Description}{(s.HasProblem ? $"  [{s.ProblemText}]" : "")}");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ReadFailure(ex));
        }
    }

    private static async Task<int> ReadAsync(Dictionary<string, string?> opts)
    {
        if (!opts.TryGetValue("object", out var spec) || spec is null)
            throw new ArgumentException("Say which object to read, e.g. --object ai:1 (types: ai ao av bi bo bv msi mso msv, or analog-input etc.).");
        if (!BacnetNames.TryParseObject(spec, out var id))
            throw new ArgumentException($"'{spec}' is not an object. Use type:instance, e.g. ai:1, av:12 or binary-output:3.");

        BacnetPropertyIds? single = null;
        if (opts.TryGetValue("property", out var propText) && propText is not null)
        {
            if (!BacnetNames.TryParseProperty(propText, out var p))
                throw new ArgumentException($"'{propText}' is not a known property. Try present-value, object-name, priority-array, or a number.");
            single = p;
        }

        var (svc, device, error) = await ConnectToDeviceAsync(opts);
        if (svc is null || device is null) return Fail(error!);
        using var _ = svc;

        var browser = svc.OpenDevice(device);
        try
        {
            Console.WriteLine($"Device {device.InstanceId} / {BacnetNames.ObjectLabel(id)}");
            var rows = single is { } one
                ? [await browser.ReadPropertyAsync(id, one)]
                : await browser.ReadAllPropertiesAsync(id);
            if (rows.Count == 0)
                return Fail("The device returned no properties for that object.\n" +
                            "  Likely cause: the object does not exist on this device.\n" +
                            $"  Next step:    run 'bacprobe objects --device {device.InstanceId}' to see what it has.");
            foreach (var r in rows)
                Console.WriteLine($"  {r.Name,-30} {r.Display}{(r.IsVendorSpecific ? "  [vendor-specific]" : "")}");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ReadFailure(ex));
        }
    }

    /// <summary>Who-Is for exactly one device instance, then return it with a live session.</summary>
    private static async Task<(DiscoveryService?, DiscoveredDevice?, string?)> ConnectToDeviceAsync(Dictionary<string, string?> opts)
    {
        if (!opts.TryGetValue("device", out var text) || !int.TryParse(text, out var instance) || instance is < 0 or > 4194302)
            throw new ArgumentException("Say which device with --device <instance number>. 'bacprobe discover' lists them.");

        var (svc, error) = await OpenSessionAsync(opts);
        if (svc is null) return (null, null, error);

        var wait = IntOpt(opts, "wait", 3);
        var found = await svc.WhoIsAsync(instance, instance, TimeSpan.FromSeconds(wait));
        var device = found.FirstOrDefault(d => d.InstanceId == instance);
        if (device is null)
        {
            svc.Dispose();
            return (null, null, $"Device {instance} did not answer Who-Is.\n" +
                                "  Likely cause: wrong instance number, wrong adapter/subnet, or the device is behind a router/BBMD.\n" +
                                "  Next step:    run 'bacprobe discover' to list the devices that do answer, or try a longer --wait.");
        }
        return (svc, device, null);
    }

    /// <summary>Pick the adapter, run pre-flight, open the BACnet socket and (if asked) register with a BBMD.</summary>
    private static async Task<(DiscoveryService?, string?)> OpenSessionAsync(Dictionary<string, string?> opts)
    {
        var adapter = PickAdapter(opts);
        var bbmd = ParseBbmd(opts);
        var results = Core.Networking.Preflight.Run(adapter);
        if (!PreflightRules.CanProceed(results))
        {
            PrintPreflight(adapter);
            return (null, "Fix the failures above, then try again.");
        }

        var svc = new DiscoveryService(adapter);
        try
        {
            svc.Start();
        }
        catch (Exception ex)
        {
            svc.Dispose();
            return (null, $"Could not open the BACnet socket: {ex.Message}\n" +
                          "  Likely cause: another program holds UDP 47808 exclusively.\n" +
                          "  Next step:    run 'bacprobe preflight' and close the program it names.");
        }

        if (bbmd is not null) await RegisterWithBbmdAsync(svc, adapter, bbmd);
        return (svc, null);
    }

    private static string ReadFailure(Exception ex) =>
        ex is TimeoutException || ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            ? "The device stopped answering (timeout).\n" +
              "  Likely cause: network drop, a busy controller, or a router dropping the request.\n" +
              "  Next step:    check the cable/Wi-Fi and run the command again."
            : $"The device returned an error: {ex.Message}\n" +
              "  Likely cause: the object or property does not exist, or the device refused the request.\n" +
              "  Next step:    run 'bacprobe objects --device <n>' to see what exists.";
}
