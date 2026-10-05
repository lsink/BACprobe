using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Export;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>
    /// Line two devices up point by point (or, with --object, one object's properties on both) and print what is different.
    /// Reads only; changes nothing.
    /// </summary>
    private static async Task<int> CompareAsync(Dictionary<string, string?> opts)
    {
        if (!opts.TryGetValue("device", out var aText) || !uint.TryParse(aText, out var a) ||
            !opts.TryGetValue("with", out var bText) || !uint.TryParse(bText, out var b))
            throw new ArgumentException("Say which two devices: bacprobe compare --device 1001 --with 1002 [--object av:1] [--all] [--inputs]");
        if (a == b) throw new ArgumentException("Pick two different devices.");

        BacnetObjectId? single = null;
        if (opts.TryGetValue("object", out var spec) && spec is not null)
        {
            if (!BacnetNames.TryParseObject(spec, out var id)) throw new ArgumentException($"'{spec}' is not an object. Use something like av:1 or bv:7.");
            single = id;
        }

        var (svc, error) = await OpenSessionAsync(opts);
        if (svc is null) return Fail(error!);
        using var _ = svc;

        var wait = IntOpt(opts, "wait", 3);
        var found = await svc.WhoIsAsync((int)Math.Min(a, b), (int)Math.Max(a, b), TimeSpan.FromSeconds(wait));
        var devA = found.FirstOrDefault(d => d.InstanceId == a);
        var devB = found.FirstOrDefault(d => d.InstanceId == b);
        foreach (var (inst, dev) in new[] { (a, devA), (b, devB) })
            if (dev is null)
                return Fail($"Device {inst} did not answer Who-Is.\n  Likely cause: wrong instance number, wrong adapter/subnet, or the device is behind a router/BBMD.\n" +
                            "  Next step:    run 'bacprobe discover' to list the devices that do answer, or try a longer --wait.");
        await svc.EnrichAsync([devA!, devB!]);
        string Label(DiscoveredDevice d) => $"{d.InstanceId} \"{d.ObjectName ?? "?"}\"";
        Console.WriteLine($"A = device {Label(devA!)} at {devA!.AddressText}");
        Console.WriteLine($"B = device {Label(devB!)} at {devB!.AddressText}");
        Console.WriteLine();

        try
        {
            if (single is { } one)
            {
                var rows = Comparison.CompareProperties(await svc.OpenDevice(devA).ReadAllPropertiesAsync(one), await svc.OpenDevice(devB).ReadAllPropertiesAsync(one));
                var shown = rows.Where(r => opts.ContainsKey("all") || r.Kind != CompareKind.Same).ToList();
                Console.WriteLine($"{BacnetNames.ObjectLabel(one)}: {rows.Count(r => r.Kind != CompareKind.Same)} of {rows.Count} properties differ.");
                foreach (var r in shown) Console.WriteLine($"  {r.Property,-26} A: {r.ValueA ?? "-",-24} B: {r.ValueB ?? "-",-24} {r.KindText}");
                return rows.Any(r => r.Kind != CompareKind.Same) ? 5 : 0;
            }

            Console.WriteLine("Reading both devices' points...");
            var pa = await PointExporter.CollectAsync(svc.OpenDevice(devA), devA, null);
            var pb = await PointExporter.CollectAsync(svc.OpenDevice(devB), devB, null);
            var points = Comparison.ComparePoints(pa.Objects, pb.Objects);
            var ignoreInputs = !opts.ContainsKey("inputs");

            Console.WriteLine();
            Console.WriteLine(Comparison.Summarise(points, ignoreInputs));
            var list = points.Where(r => opts.ContainsKey("all") || r.Kind != CompareKind.Same || r.NamesDiffer)
                .Where(r => !(ignoreInputs && r.IsLiveInput && r.Kind == CompareKind.Different)).ToList();
            if (list.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"{"Object",-8} {"Name",-26} {"A",-18} {"B",-18} Result");
                foreach (var r in list)
                    Console.WriteLine($"{r.Object,-8} {Truncate(r.Name, 26),-26} {r.ValueA ?? "-",-18} {r.ValueB ?? "-",-18} {r.KindText}{(r.NamesDiffer ? $"  (named \"{r.NameB}\" on B)" : "")}");
            }
            if (ignoreInputs && points.Any(r => r.IsLiveInput && r.Kind == CompareKind.Different))
                Console.WriteLine("\nLive inputs (temperatures and so on) differ by nature and are left out; add --inputs to show them.");
            return points.Any(r => r.Kind != CompareKind.Same && !(ignoreInputs && r.IsLiveInput && r.Kind == CompareKind.Different)) ? 5 : 0;
        }
        catch (Exception ex)
        {
            return Fail(ReadFailure(ex));
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
