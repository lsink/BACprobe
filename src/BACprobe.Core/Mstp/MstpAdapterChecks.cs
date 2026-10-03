using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BACprobe.Core.Mstp;

/// <summary>How well one trial baud rate matched what was on the wire.</summary>
public sealed record BaudTrial(int Baud, int GoodFrames, int Errors, long DiscardedBytes);

/// <summary>Picks the trunk's baud rate from short listens at each candidate. Pure.</summary>
public static class MstpBaudDetector
{
    /// <summary>The rates MS/TP trunks use, slowest first. 38400 and 76800 are the common field choices.</summary>
    public static IReadOnlyList<int> Candidates { get; } = [9600, 19200, 38400, 76800, 115200];

    private const int MinGoodFrames = 3;

    /// <summary>
    /// The rate that produced clearly the most good frames, or null when nothing was heard or the answer is not clear
    /// (two rates both decoding frames means a mixed trunk or a very quiet one, which is worth a look, not a guess).
    /// </summary>
    public static int? Pick(IReadOnlyList<BaudTrial> trials)
    {
        var ranked = trials.Where(t => t.GoodFrames >= MinGoodFrames && t.GoodFrames > t.Errors * 2)
            .OrderByDescending(t => t.GoodFrames - t.Errors * 5).ToList();
        if (ranked.Count == 0) return null;
        if (ranked.Count > 1 && ranked[1].GoodFrames * 2 > ranked[0].GoodFrames) return null;
        return ranked[0].Baud;
    }

    /// <summary>What to tell the tech about the result of a detection run.</summary>
    public static string Explain(IReadOnlyList<BaudTrial> trials, int? picked)
    {
        if (picked is { } b) return $"The trunk is running at {b} baud.";
        if (trials.All(t => t.GoodFrames == 0 && t.Errors == 0 && t.DiscardedBytes == 0))
            return "Heard nothing at any baud rate. Likely cause: wrong COM port, A/B/shield not connected, or no master is powered. Next step: check the wiring and the port.";
        if (trials.Count(t => t.GoodFrames >= MinGoodFrames) > 1)
            return "More than one baud rate decoded frames. Likely cause: devices on the trunk are set to different rates, or it is very quiet. Next step: listen longer, or pick the rate most devices use and look for the odd one out.";
        return "Bytes arrived but no baud rate decoded a clean frame. Likely cause: A and B swapped, a very noisy bus, or a rate not on the list. Next step: swap A and B, check termination, then try again.";
    }
}

/// <summary>An FTDI adapter's USB latency timer, which delays every received byte by up to this long.</summary>
public sealed record FtdiLatency(string PortName, int LatencyMs)
{
    /// <summary>Over 2 ms makes the monitor see frames late and a master built on it miss its reply windows.</summary>
    public const int MaxGoodMs = 2;

    public bool IsTooHigh => LatencyMs > MaxGoodMs;

    /// <summary>Plain-English verdict with a next step.</summary>
    public string Verdict => IsTooHigh
        ? $"{PortName}: latency timer is {LatencyMs} ms (the FTDI default is 16). Likely cause: the driver default was never changed. Next step: in Device Manager open the port's Properties, Port Settings, Advanced, and set Latency Timer to 1 ms, then unplug and replug the adapter."
        : $"{PortName}: latency timer is {LatencyMs} ms, fine.";

    /// <summary>Latency timers of the FTDI adapters Windows knows about. Empty when there are none or the registry cannot be read.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<FtdiLatency> ReadAll()
    {
        var found = new List<FtdiLatency>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\FTDIBUS");
            if (root is null) return found;
            foreach (var device in root.GetSubKeyNames())
            {
                using var dev = root.OpenSubKey(device);
                if (dev is null) continue;
                foreach (var instance in dev.GetSubKeyNames())
                {
                    using var p = dev.OpenSubKey($@"{instance}\Device Parameters");
                    if (p?.GetValue("PortName") is string port && p.GetValue("LatencyTimer") is int ms)
                        found.Add(new FtdiLatency(port, ms));
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable: say nothing rather than guess.
        }
        return found;
    }
}
