using BACprobe.Core.Discovery;
using BACprobe.Core.Writing;

namespace BACprobe.Cli;

internal static partial class Program
{
    private static Task<int> ClockAsync(Dictionary<string, string?> opts) =>
        DeviceActionAsync(opts, opts.ContainsKey("utc") ? DeviceActionKind.SyncTimeUtc : DeviceActionKind.SyncTime);

    private static Task<int> RestartAsync(Dictionary<string, string?> opts)
    {
        var warm = opts.ContainsKey("warm");
        var cold = opts.ContainsKey("cold");
        if (warm == cold) throw new ArgumentException("Say which kind of restart: --warm (keeps its settings) or --cold (like a power cycle).");
        return DeviceActionAsync(opts, cold ? DeviceActionKind.ColdStart : DeviceActionKind.WarmStart);
    }

    private static Task<int> MuteAsync(Dictionary<string, string?> opts) =>
        DeviceActionAsync(opts, opts.ContainsKey("initiation") ? DeviceActionKind.MuteInitiation : DeviceActionKind.Mute);

    private static Task<int> UnmuteAsync(Dictionary<string, string?> opts) => DeviceActionAsync(opts, DeviceActionKind.Unmute);

    /// <summary>Open a session and take the device at this address on trust (no Who-Is): a muted device will not answer one.</summary>
    private static async Task<(DiscoveryService?, DiscoveredDevice?, string?)> ConnectByAddressAsync(Dictionary<string, string?> opts, string addressText)
    {
        if (!opts.TryGetValue("device", out var text) || !uint.TryParse(text, out var instance))
            throw new ArgumentException("Say which device with --device <instance number> as well as --address.");
        var endpoint = addressText.Contains(':') ? addressText : $"{addressText}:{Core.Networking.PreflightRules.BacnetPort}";
        if (!System.Net.IPEndPoint.TryParse(endpoint, out _))
            throw new ArgumentException($"'{addressText}' is not an IP address (or IP:port), e.g. 192.168.1.20 or 192.168.1.20:47808.");
        var (svc, error) = await OpenSessionAsync(opts);
        if (svc is null) return (null, null, error);
        return (svc, new DiscoveredDevice
        {
            InstanceId = instance,
            Address = new System.IO.BACnet.BacnetAddress(System.IO.BACnet.BacnetAddressTypes.IP, endpoint),
            MaxApdu = 480,
            Segmentation = System.IO.BACnet.BacnetSegmentations.SEGMENTATION_NONE,
            VendorId = 0,
        }, null);
    }

    /// <summary>
    /// Clock sync, restart, mute and un-mute: confirm in plain English (restarts and mutes need the device number typed), send, log.
    /// </summary>
    private static async Task<int> DeviceActionAsync(Dictionary<string, string?> opts, DeviceActionKind kind)
    {
        var minutes = (uint)IntOpt(opts, "minutes", (int)DeviceActionRequest.DefaultMuteMinutes);
        opts.TryGetValue("password", out var password);
        if (kind is DeviceActionKind.Mute or DeviceActionKind.MuteInitiation && (minutes < 1 || minutes > DeviceActionRequest.MaxMuteMinutes))
            return Fail($"Mute for 1 to {DeviceActionRequest.MaxMuteMinutes} minutes. BACprobe never mutes a device with no time limit.");

        // A muted device does not answer Who-Is, so un-muting may need its address.
        var (svc, device, error) = opts.TryGetValue("address", out var addressText) && addressText is not null
            ? await ConnectByAddressAsync(opts, addressText)
            : await ConnectToDeviceAsync(opts);
        if (svc is null || device is null)
            return Fail(error! + (kind == DeviceActionKind.Unmute
                ? "\n  A muted device does not answer Who-Is: give its address too, e.g. --address 192.168.1.20 (BACprobe printed it when muting)."
                : ""));
        using var _ = svc;
        if (kind != DeviceActionKind.Unmute) await svc.EnrichAsync([device]); // its name, and how far off its clock is (a muted device would only time out)

        var request = new DeviceActionRequest(device, device.ObjectName ?? $"device {device.InstanceId}", kind, minutes, password);
        if (request.Problem is { } problem) return Fail(problem);
        Console.WriteLine();
        PrintExplanation(Prompts.ForDeviceAction(request));

        if (!opts.ContainsKey("yes"))
        {
            if (Console.IsInputRedirected)
                return Fail("This needs a person to confirm. Run it in a terminal, or add --yes if you are scripting it.");
            if (request.TypeToConfirm is { } expected)
            {
                Console.Write($"Type the device number ({expected}) to go ahead, anything else cancels: ");
                if (Console.ReadLine()?.Trim() != expected) return Cancelled();
            }
            else
            {
                Console.Write("Type y to go ahead, anything else cancels: ");
                if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return Cancelled();
            }
        }

        var writer = svc.CreateWriter(new WriteLog(WriteLog.DefaultPath), new OverrideTracker());
        var outcome = await writer.RunDeviceActionAsync(request);
        if (!outcome.Success) return Fail(outcome.Message);
        Console.WriteLine($"Done: {outcome.Message.TrimEnd('.')}.");
        if (request.IsMute)
            Console.WriteLine($"It un-mutes by itself after {minutes} minutes, or now with:\n  bacprobe unmute --device {device.InstanceId} --address {device.AddressText}" +
                              (password is null ? "" : " --password <password>"));
        Console.WriteLine($"Logged to {WriteLog.DefaultPath}");
        return 0;

        static int Cancelled()
        {
            Console.WriteLine("Cancelled. Nothing was sent.");
            return 0;
        }
    }
}
