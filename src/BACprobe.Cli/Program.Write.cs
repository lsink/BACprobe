using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Writing;

namespace BACprobe.Cli;

internal static partial class Program
{
    private static Task<int> WriteAsync(Dictionary<string, string?> opts) => WriteOrReleaseAsync(opts, release: false);

    private static Task<int> ReleaseAsync(Dictionary<string, string?> opts) => WriteOrReleaseAsync(opts, release: true);

    private static async Task<int> WriteOrReleaseAsync(Dictionary<string, string?> opts, bool release)
    {
        if (!opts.TryGetValue("object", out var spec) || spec is null || !BacnetNames.TryParseObject(spec, out var id))
            throw new ArgumentException("Say which point with --object type:instance, e.g. --object ao:1 (types: ai ao av bi bo bv msi mso msv).");

        var priority = PriorityChoice.Default.Number;
        if (opts.TryGetValue("priority", out var pText))
        {
            if (!int.TryParse(pText, out priority) || priority is < 1 or > 16)
                throw new ArgumentException("--priority must be a number from 1 to 16 (8 = Manual Operator, the normal choice for a technician).");
        }

        string? valueText = null;
        BacnetValue? value = null;
        if (!release)
        {
            if (!opts.TryGetValue("value", out valueText) || valueText is null)
                throw new ArgumentException("Say what to write with --value, e.g. --value 72.5 (analog) or --value on (binary).");
            if (!WriteValueParser.TryParse(id.type, valueText, out var parsed, out var parseError))
                throw new ArgumentException(parseError);
            value = parsed;
        }

        var (svc, device, error) = await ConnectToDeviceAsync(opts);
        if (svc is null || device is null) return Fail(error!);
        using var _ = svc;

        var browser = svc.OpenDevice(device);
        string deviceName, pointName, current;
        try
        {
            deviceName = (await browser.ReadPropertyAsync(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, device.InstanceId),
                BacnetPropertyIds.PROP_OBJECT_NAME)).Display;
            pointName = (await browser.ReadPropertyAsync(id, BacnetPropertyIds.PROP_OBJECT_NAME)).Display;
            current = BuildCurrent(await browser.ReadPropertyAsync(id, BacnetPropertyIds.PROP_PRESENT_VALUE),
                await TryReadAsync(browser, id, BacnetPropertyIds.PROP_UNITS));
        }
        catch (Exception ex)
        {
            return Fail(ReadFailure(ex));
        }

        var request = new WriteRequest(device, deviceName, id, pointName, value, release ? "release" : valueText!, priority, current);
        Console.WriteLine();
        Console.WriteLine(request.ConfirmationText());
        Console.WriteLine();

        if (!opts.ContainsKey("yes"))
        {
            if (Console.IsInputRedirected)
                return Fail("This needs a person to confirm. Run it in a terminal, or add --yes if you are scripting it.");
            Console.Write("Type y to go ahead, anything else cancels: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Cancelled. Nothing was written.");
                return 0;
            }
        }

        var writer = svc.CreateWriter(new WriteLog(WriteLog.DefaultPath), new OverrideTracker());
        var outcome = await writer.ExecuteAsync(request);
        if (!outcome.Success) return Fail(outcome.Message);

        Console.WriteLine(release ? "Released." : "Done - the device accepted the write.");
        try
        {
            var after = await browser.ReadPropertyAsync(id, BacnetPropertyIds.PROP_PRESENT_VALUE);
            var arr = await TryReadAsync(browser, id, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
            Console.WriteLine($"  Present value now: {after.Display}");
            if (arr is not null) Console.WriteLine($"  Priority array:    {arr.Display}");
        }
        catch (Exception) { Console.WriteLine("  (Could not read the point back to verify; check it before you leave.)"); }

        if (!release)
            Console.WriteLine($"\nThis override is still in place. Release it when you are done:\n" +
                              $"  bacprobe release --device {device.InstanceId} --object {spec} --priority {priority}");
        Console.WriteLine($"Logged to {WriteLog.DefaultPath}");
        return 0;
    }

    private static async Task<PropertyRow?> TryReadAsync(DeviceBrowser browser, BacnetObjectId id, BacnetPropertyIds prop)
    {
        try { return await browser.ReadPropertyAsync(id, prop); }
        catch (Exception) { return null; }
    }

    private static string BuildCurrent(PropertyRow pv, PropertyRow? units) =>
        string.IsNullOrEmpty(units?.Display) ? pv.Display : $"{pv.Display} {units.Display}";
}
