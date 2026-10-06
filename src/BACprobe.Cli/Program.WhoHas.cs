using BACprobe.Core.Discovery;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>
    /// Ask the network which device has an object, by exact name or by object identifier: one broadcast instead of reading every device.
    /// </summary>
    private static async Task<int> WhoHasAsync(Dictionary<string, string?> opts)
    {
        if (!opts.TryGetValue("_0", out var query) || string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Say what to look for: bacprobe who-has \"Zone Temp\" (an exact point name) or bacprobe who-has ai:1.");
        var (id, name) = IHaveCodec.ParseQuery(query);
        var low = IntOpt(opts, "low", -1);
        var high = IntOpt(opts, "high", -1);
        if ((low < 0) != (high < 0)) throw new ArgumentException("Give both --low and --high, or neither.");

        var (svc, error) = await OpenSessionAsync(opts);
        if (svc is null) return Fail(error!);
        using var _ = svc;

        var wait = IntOpt(opts, "wait", 3);
        Console.WriteLine($"Asking who has {(id is { } o ? Core.Browsing.BacnetNames.ObjectLabel(o) : $"\"{name}\"")}, listening {wait}s...");
        var replies = await svc.WhoHasAsync(name, id, TimeSpan.FromSeconds(wait), low, high);
        if (replies.Count == 0)
        {
            Console.WriteLine("No device said it has it.");
            Console.WriteLine("  Likely cause: names must match exactly (case and spaces), the device is on another subnet (Who-Has does not go");
            Console.WriteLine("                through a BBMD), or the device does not answer Who-Has.");
            Console.WriteLine("  Next step:    try the object identifier (e.g. ai:1), or 'bacprobe find' to search the names of devices it can read.");
            return 3;
        }
        Console.WriteLine();
        Console.WriteLine($"{"Device",-9} {"Object",-22} {"Name",-30} Address");
        foreach (var r in replies)
            Console.WriteLine($"{r.DeviceInstance,-9} {r.ObjectLabel,-22} {Truncate(r.ObjectName, 30),-30} {r.AddressText}");
        if (replies.Select(r => r.DeviceInstance).Distinct().Count() > 1 && name is not null)
            Console.WriteLine("\nMore than one device has a point with this name. That is normal (every VAV has a Zone Temp); add --low/--high to narrow it.");
        return 0;
    }
}
