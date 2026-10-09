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
        if (!release && opts.TryGetValue("property", out var propText) && propText is not null)
        {
            if (!BacnetNames.TryParseProperty(propText, out var prop))
                throw new ArgumentException($"'{propText}' is not a property name. Use names like high-limit, description or cov-increment.");
            if (prop != BacnetPropertyIds.PROP_PRESENT_VALUE) return await WritePropertyAsync(opts, id, prop);
        }

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
                throw new ArgumentException("Say what to write with --value, e.g. --value 72.5 (analog), --value on (binary) or --value Standby (multi-state).");
            // A state name ("Standby") can only be checked once the device has said what its states are called.
            if (!WriteValueParser.TryParse(id.type, valueText, out var parsed, out var parseError) && StateText.NameProperties(id.type).Length == 0)
                throw new ArgumentException(parseError);
        }

        var (svc, device, error) = await ConnectToDeviceAsync(opts);
        if (svc is null || device is null) return Fail(error!);
        using var _ = svc;

        var browser = svc.OpenDevice(device);
        string deviceName, pointName, current;
        IReadOnlyList<string?>? stateNames;
        try
        {
            stateNames = await browser.ReadStateNamesAsync(id);
            deviceName = (await browser.ReadPropertyAsync(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, device.InstanceId),
                BacnetPropertyIds.PROP_OBJECT_NAME)).Display;
            pointName = (await browser.ReadPropertyAsync(id, BacnetPropertyIds.PROP_OBJECT_NAME)).Display;
            var pv = await browser.ReadPropertyAsync(id, BacnetPropertyIds.PROP_PRESENT_VALUE);
            current = BuildCurrent(pv with { Display = StateText.Label(id.type, pv.Display, stateNames) },
                await TryReadAsync(browser, id, BacnetPropertyIds.PROP_UNITS));
        }
        catch (Exception ex)
        {
            return Fail(ReadFailure(ex));
        }

        if (!release)
        {
            if (!WriteValueParser.TryParse(id.type, valueText!, stateNames, out var parsed, out var parseError))
                return Fail(parseError + StateHint(id.type, stateNames));
            value = parsed;
        }

        // What will be written, as the device will show it: "Standby (3)", "On (Active)", "72.5".
        var request = new WriteRequest(device, deviceName, id, pointName, value,
            value is { } v ? StateText.Describe(id.type, v, stateNames) : "release", priority, current);
        Console.WriteLine();
        Console.WriteLine(request.ConfirmationText());
        Console.WriteLine();

        if (ConfirmOrExit(opts, "Type y to go ahead, anything else cancels: ", "Cancelled. Nothing was written.") is { } exit) return exit;

        var writer = svc.CreateWriter(new WriteLog(WriteLog.DefaultPath), new OverrideTracker());
        var outcome = await writer.ExecuteAsync(request);
        if (!outcome.Success)
        {
            if (outcome.Explanation is { } why) PrintExplanation(why);
            return Fail("The write did not go through.");
        }

        if (outcome.Explanation is { } ineffective)
        {
            Console.WriteLine("The device accepted the write, but it did not take effect.\n");
            PrintExplanation(ineffective);
        }
        else Console.WriteLine(release ? "Released." : "Done - the device accepted the write.");
        try
        {
            // As a summary, so it comes with the state names: "Standby (3)" rather than "3".
            var after = (await browser.ReadSummariesAsync([id]))[0];
            Console.WriteLine($"  Present value now: {after.ValueText}");
            if (PriorityArrayInfo.MayHavePriorityArray(id.type)) Console.WriteLine($"  Priority array:    {after.PriorityArrayText}");
        }
        catch (Exception) { Console.WriteLine("  (Could not read the point back to verify; check it before you leave.)"); }

        if (!release)
            Console.WriteLine($"\nThis override is still in place. Release it when you are done:\n" +
                              $"  bacprobe release --device {device.InstanceId} --object {spec} --priority {priority}");
        Console.WriteLine($"Logged to {WriteLog.DefaultPath}");
        return 0;
    }

    /// <summary>
    /// Change one configuration property (a limit, the description, the COV increment…): read it to learn its type, confirm in plain
    /// English, write without a priority, and read it back. Logged like a write; there is nothing to release afterwards.
    /// </summary>
    private static async Task<int> WritePropertyAsync(Dictionary<string, string?> opts, BacnetObjectId id, BacnetPropertyIds prop)
    {
        if (!opts.TryGetValue("value", out var valueText) || valueText is null)
            throw new ArgumentException("Say what to set it to with --value, e.g. --value 80 or --value \"Supply air temp\".");

        var (svc, device, error) = await ConnectToDeviceAsync(opts);
        if (svc is null || device is null) return Fail(error!);
        using var _ = svc;

        var browser = svc.OpenDevice(device);
        PropertyRow current;
        string deviceName, pointName;
        try
        {
            current = await browser.ReadPropertyAsync(id, prop);
            deviceName = (await browser.ReadPropertyAsync(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, device.InstanceId),
                BacnetPropertyIds.PROP_OBJECT_NAME)).Display;
            pointName = (await browser.ReadPropertyAsync(id, BacnetPropertyIds.PROP_OBJECT_NAME)).Display;
        }
        catch (Exception ex)
        {
            return Fail(ReadFailure(ex));
        }

        if (!PropertyEdit.CanEdit(current, out var why)) return Fail($"{current.Name} cannot be changed here. {why}");
        if (!PropertyEdit.TryParse(current.ValueTag!.Value, valueText, out var value, out var parseError))
            return Fail($"{parseError} Nothing was written.");

        var shown = BacnetNames.FormatValues(id.type, prop, [value]);
        var request = new PropertyWriteRequest(device, deviceName, id, pointName, prop, value, shown, current.Display);
        Console.WriteLine();
        PrintExplanation(Prompts.ForPropertyWrite(request));
        if (ConfirmOrExit(opts, "Type y to go ahead, anything else cancels: ", "Cancelled. Nothing was written.") is { } exit) return exit;

        var writer = svc.CreateWriter(new WriteLog(WriteLog.DefaultPath), new OverrideTracker());
        var outcome = await writer.WritePropertyAsync(request);
        if (!outcome.Success) return Fail(outcome.Message);
        Console.WriteLine($"Done: the {outcome.Message}.");
        Console.WriteLine($"Logged to {WriteLog.DefaultPath}");
        return 0;
    }

    private static void PrintExplanation(PromptContent c)
    {
        Console.WriteLine(c.Headline);
        foreach (var f in c.Facts) Console.WriteLine($"  {f.Label + ":",-20}{f.Value}");
        Console.WriteLine("\n" + c.Body);
        if (c.Warning is not null) Console.WriteLine("\nWARNING: " + c.Warning);
        Console.WriteLine();
    }

    private static async Task<PropertyRow?> TryReadAsync(DeviceBrowser browser, BacnetObjectId id, BacnetPropertyIds prop)
    {
        try { return await browser.ReadPropertyAsync(id, prop); }
        catch (Exception) { return null; }
    }

    /// <summary>A line listing the states, e.g. "This point's states: 1 = Occupied, 2 = Unoccupied", when the device named them.</summary>
    private static string StateHint(BacnetObjectTypes type, IReadOnlyList<string?>? names) =>
        StateText.IsMultiState(type) && names is { Count: > 0 }
            ? Environment.NewLine + "  This point's states: " + string.Join(", ", names.Select((n, i) => $"{i + 1} = {n}"))
            : "";

    private static string BuildCurrent(PropertyRow pv, PropertyRow? units) =>
        string.IsNullOrEmpty(units?.Display) ? pv.Display : $"{pv.Display} {units.Display}";
}
