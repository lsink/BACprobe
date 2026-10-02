using System.Globalization;
using System.Net;
using BACprobe.Core.Networking;

namespace BACprobe.Core.Bbmd;

/// <summary>Where to register as a foreign device, and for how long.</summary>
public sealed record BbmdTarget(IPAddress Address, int Port, int TtlSeconds)
{
    public const int DefaultTtlSeconds = 300;
    /// <summary>The library takes a signed 16-bit TTL.</summary>
    public const int MaxTtlSeconds = short.MaxValue;

    public IPEndPoint EndPoint => new(Address, Port);
    public override string ToString() => Port == PreflightRules.BacnetPort ? Address.ToString() : $"{Address}:{Port}";

    /// <summary>Re-register at half the TTL so a single lost packet does not drop us.</summary>
    public TimeSpan RenewInterval => TimeSpan.FromSeconds(Math.Max(5, TtlSeconds / 2));

    /// <summary>Accepts "10.1.2.3" or "10.1.2.3:47809". Hostnames are not accepted: field sites give IPs.</summary>
    public static bool TryParse(string text, int ttlSeconds, out BbmdTarget? target, out string error)
    {
        target = null;
        error = "";
        var t = text.Trim();
        var port = PreflightRules.BacnetPort;

        var colon = t.LastIndexOf(':');
        if (colon > 0)
        {
            if (!int.TryParse(t[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            {
                error = $"'{t[(colon + 1)..]}' is not a valid UDP port. Use a number from 1 to 65535, or leave the port off for the standard 47808.";
                return false;
            }
            t = t[..colon];
        }

        if (!IPAddress.TryParse(t, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || t.Count(c => c == '.') != 3)
        {
            error = $"'{t}' is not an IPv4 address. Enter the BBMD's IP address, for example 10.20.30.40 (ask the site contact).";
            return false;
        }
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast))
        {
            error = $"{ip} cannot be a BBMD address. Enter the BBMD's real IP address.";
            return false;
        }
        if (ttlSeconds is < 1 or > MaxTtlSeconds)
        {
            error = $"The registration time must be between 1 and {MaxTtlSeconds} seconds (default {DefaultTtlSeconds}).";
            return false;
        }

        target = new BbmdTarget(ip, port, ttlSeconds);
        return true;
    }

    /// <summary>A heads-up when the BBMD is on the PC's own subnet, where registering is normally unnecessary.</summary>
    public PreflightResult CheckAgainst(AdapterInfo adapter) =>
        Subnet.Contains(adapter.Address, adapter.Mask, Address)
            ? new PreflightResult("BBMD address", PreflightSeverity.Warning,
                $"The BBMD {this} is on the same subnet as {adapter.Name} ({adapter.Cidr}).",
                "Devices on your own subnet already answer a normal broadcast; BBMD registration is meant for reaching other subnets.",
                "Registering is harmless. If you expected a different subnet, double-check the address.")
            : new PreflightResult("BBMD address", PreflightSeverity.Pass, $"BBMD {this} is on another subnet, as expected.");
}
