using System.IO.BACnet;

namespace BACprobe.Core.Discovery;

/// <summary>Things worth knowing about a device that has nothing to do with its points. Pure, so it can be tested.</summary>
public static class DeviceHealth
{
    public static readonly TimeSpan SlowResponse = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ClockSkewLimit = TimeSpan.FromMinutes(5);

    /// <summary>A device's own clock, from its Local_Date and Local_Time. Null when either is missing or unspecified.</summary>
    public static DateTime? CombineClock(DateTime? date, DateTime? time)
    {
        if (date is not { } d || time is not { } t) return null;
        // BACnet marks an unspecified date or time field with 255 (decoded as year 1 or an all-zero part); a real date has a year.
        if (d.Year < 1990) return null;
        return d.Date + t.TimeOfDay;
    }

    /// <summary>"2 min 5 s ahead" / "3 h 10 min behind", for the PC's clock as the reference.</summary>
    public static string DescribeSkew(TimeSpan skew)
    {
        var abs = skew.Duration();
        var text = abs.TotalDays >= 1 ? $"{(int)abs.TotalDays} d {abs.Hours} h"
            : abs.TotalHours >= 1 ? $"{(int)abs.TotalHours} h {abs.Minutes} min"
            : abs.TotalMinutes >= 1 ? $"{(int)abs.TotalMinutes} min {abs.Seconds} s"
            : $"{abs.Seconds} s";
        return $"{text} {(skew < TimeSpan.Zero ? "behind" : "ahead of")} this PC";
    }

    private static string Name(DiscoveredDevice d) => d.ObjectName is { Length: > 0 } n ? $"{n} (device {d.InstanceId})" : $"device {d.InstanceId}";

    public static IReadOnlyList<NetworkFinding> Check(IReadOnlyList<DiscoveredDevice> devices)
    {
        var list = new List<NetworkFinding>();

        foreach (var d in devices.Where(d => d.ResponseTime is { } r && r >= SlowResponse))
            list.Add(new(FindingSeverity.Warning, $"{Name(d)} is slow to answer",
                $"It took {d.ResponseTime!.Value.TotalSeconds:0.0} s to answer a simple read.",
                "A busy or overloaded controller, a weak link (Wi-Fi, a long router path, an MS/TP trunk that is busy), or many devices being read at once.",
                "Read it again on its own. If it stays slow, check the network path to it and how busy it or its trunk is."));

        var cannotSegment = devices.Where(d => d.Segmentation is BacnetSegmentations.SEGMENTATION_NONE or BacnetSegmentations.SEGMENTATION_TRANSMIT).ToList();
        if (cannotSegment.Count > 0)
            list.Add(new(FindingSeverity.Info,
                cannotSegment.Count == 1 ? $"{Name(cannotSegment[0])} cannot receive segmented requests" : $"{cannotSegment.Count} devices cannot receive segmented requests",
                $"Devices: {string.Join(", ", cannotSegment.Take(12).Select(d => d.InstanceId))}{(cannotSegment.Count > 12 ? ", ..." : "")}. Their largest message is {string.Join(" / ", cannotSegment.Select(d => d.MaxApdu).Distinct().Order())} bytes.",
                "Normal for small controllers. It means BACprobe has to ask them for a few points at a time, so reading a big device is slower.",
                "Nothing to fix. If reads of these devices time out, they are probably busy rather than broken."));

        foreach (var d in devices.Where(d => d.MaxApdu < 206))
            list.Add(new(FindingSeverity.Warning, $"{Name(d)} reports a tiny maximum message size ({d.MaxApdu} bytes)",
                "Most devices accept at least 206 bytes (MS/TP devices 480, IP devices 1476).",
                "An old or very small controller, or a misconfigured Max_APDU_Length_Accepted.",
                "Expect slow, one-point-at-a-time reads from it; check its configuration if that is not what you expect."));

        foreach (var d in devices.Where(d => d.ClockSkew is { } s && s.Duration() >= ClockSkewLimit))
            list.Add(new(FindingSeverity.Warning, $"{Name(d)}'s clock is off",
                $"Its clock is {DescribeSkew(d.ClockSkew!.Value)}.",
                d.ClockSkew!.Value.Duration() >= TimeSpan.FromHours(1)
                    ? "No time sync, a wrong time zone or daylight saving setting, or a dead clock battery."
                    : "It has drifted since it was last synchronised, or nothing synchronises it.",
                "Fix it at the controller or with the site's time-sync master. Trend log timestamps and alarm times are wrong until then."));

        foreach (var g in devices.Where(d => d.ObjectName is { Length: > 0 }).GroupBy(d => d.ObjectName!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Select(d => d.InstanceId).Distinct().Count() > 1))
            list.Add(new(FindingSeverity.Warning, $"Two devices are both named \"{g.Key}\"",
                $"Device numbers {string.Join(", ", g.Select(d => d.InstanceId).Distinct().Order())} share that name.",
                "A copied controller program, or a device that was never renamed after commissioning.",
                "Rename one so the operator workstation and alarm messages can tell them apart."));

        return list;
    }
}
