using BACprobe.Core.Jobs;

namespace BACprobe.Core.Discovery;

/// <summary>One I-Am reply, as heard. Discovery keeps every one (not just the first per device number) so conflicts are visible.</summary>
public sealed record IAmObservation(uint Instance, string AddressText, ushort Network, string Mac, ushort VendorId);

public enum FindingSeverity { Info, Warning, Problem }

/// <summary>Something worth a tech's attention. Every finding says the likely cause and what to do next.</summary>
public sealed record NetworkFinding(FindingSeverity Severity, string Title, string Detail, string LikelyCause, string NextStep);

/// <summary>Spots conflicts in what answered a Who-Is, and differences from a saved job.</summary>
public static class NetworkCheck
{
    /// <summary>The device number BACnet reserves to mean "any device". A real device should never use it.</summary>
    public const uint UnassignedInstance = 4194303;

    /// <summary>Look for duplicate device numbers, duplicate addresses and unassigned devices in what answered.</summary>
    public static IReadOnlyList<NetworkFinding> Analyze(IEnumerable<IAmObservation> heard)
    {
        // The same device announcing itself twice is normal; what matters is distinct (device, address) pairs.
        var seen = heard.DistinctBy(o => (o.Instance, o.AddressText)).ToList();
        var findings = new List<NetworkFinding>();

        foreach (var g in seen.GroupBy(o => o.Instance).Where(g => g.Count() > 1).OrderBy(g => g.Key))
        {
            var where = string.Join(" and ", g.Select(o => o.AddressText));
            findings.Add(new(FindingSeverity.Problem,
                $"Device number {g.Key} is used by {g.Count()} devices",
                $"Device {g.Key} answered from {where}.",
                "Two controllers were given the same device number: usually a replaced or cloned controller, or a copied database. " +
                "(Less often, one device is reachable by two paths and is being heard twice.)",
                "Find which controller is the odd one out and give it a unique device number. Until then, tools may talk to either one."));
        }

        foreach (var o in seen.Where(o => o.Instance == UnassignedInstance))
            findings.Add(new(FindingSeverity.Problem,
                $"A device at {o.AddressText} still has the unassigned device number {UnassignedInstance}",
                $"The device at {o.AddressText} answered with {UnassignedInstance}, the number BACnet reserves to mean \"any device\".",
                "The controller has not been commissioned: it never had a device number set.",
                "Set a unique device number on that controller before using it on the network."));

        // Behind a router, two devices with the same MAC on one network is a specific, serious problem. Work it out first
        // so the general "one address, several devices" note below does not repeat it.
        var macConflicts = seen.Where(o => o.Network != 0 && o.Mac.Length > 0).GroupBy(o => (o.Network, o.Mac))
            .Where(g => g.Select(o => o.Instance).Distinct().Count() > 1).OrderBy(g => g.Key.Network).ToList();
        var coveredByMacConflict = macConflicts.SelectMany(g => g.Select(o => o.AddressText)).ToHashSet();

        // Several device numbers from one address: normal for a gateway, a mistake otherwise.
        foreach (var g in seen.Where(o => o.Instance != UnassignedInstance && !coveredByMacConflict.Contains(o.AddressText))
                     .GroupBy(o => o.AddressText)
                     .Where(g => g.Select(o => o.Instance).Distinct().Count() > 1).OrderBy(g => g.Key))
        {
            var numbers = string.Join(", ", g.Select(o => o.Instance).Distinct().Order());
            findings.Add(new(FindingSeverity.Info,
                $"{g.Key} answered as several devices ({numbers})",
                $"One address announced {g.Select(o => o.Instance).Distinct().Count()} different device numbers.",
                "That is normal for a gateway or router that presents other equipment as BACnet devices. If it is an ordinary controller, two devices may share an address.",
                "If this is not a gateway, check for two controllers with the same IP address."));
        }

        foreach (var g in macConflicts)
        {
            var numbers = string.Join(" and ", g.Select(o => o.Instance).Distinct().Order());
            findings.Add(new(FindingSeverity.Problem,
                $"Network {g.Key.Network}: devices {numbers} share the same MAC address {g.Key.Mac}",
                $"On network {g.Key.Network}, devices {numbers} both answer from MAC {g.Key.Mac}.",
                "Two controllers on the same MS/TP trunk have the same MAC address (a duplicated address switch or setting).",
                "Give one of them a unique MAC address. Duplicate MACs on an MS/TP trunk cause dropped and garbled traffic."));
        }

        return findings;
    }

    /// <summary>
    /// Compare a saved job with what answers now: missing devices, new ones, moved ones and changed firmware.
    /// Names and versions are only compared when both sides know them.
    /// </summary>
    public static IReadOnlyList<NetworkFinding> Compare(IReadOnlyList<SavedDevice> saved, IReadOnlyList<DiscoveredDevice> now)
    {
        var findings = new List<NetworkFinding>();
        var nowById = now.GroupBy(d => d.InstanceId).ToDictionary(g => g.Key, g => g.First());
        var savedIds = saved.Select(s => s.Instance).ToHashSet();

        foreach (var s in saved.OrderBy(s => s.Instance))
        {
            if (!nowById.TryGetValue(s.Instance, out var d))
            {
                findings.Add(new(FindingSeverity.Warning,
                    $"Device {s.Instance} \"{s.Name}\" is in the job but did not answer",
                    $"It was at {s.AddressText} when the job was saved.",
                    "It is powered off, unplugged, moved to another subnet or network, or its device number changed.",
                    "Check power and cabling. If it is on another subnet, register with a BBMD and scan again."));
                continue;
            }

            if (HostOf(d.AddressText) != HostOf(s.AddressText)) // a changed port alone (a restarted software device) is not a move
                findings.Add(new(FindingSeverity.Warning,
                    $"Device {s.Instance} \"{s.Name}\" has moved",
                    $"It was at {s.AddressText}; it now answers from {d.AddressText}.",
                    "Its IP address changed (DHCP, or someone re-addressed it), or the controller was replaced.",
                    "If that was not planned, check the controller's address settings. Save the job again to record the new address."));

            if (!string.IsNullOrEmpty(d.ObjectName) && !string.IsNullOrEmpty(s.Name) && d.ObjectName != s.Name)
                findings.Add(new(FindingSeverity.Info,
                    $"Device {s.Instance} was renamed",
                    $"It was \"{s.Name}\"; it is now \"{d.ObjectName}\".",
                    "Someone changed the device name.",
                    "Nothing to fix unless it was unexpected."));

            if (Differs(s.Firmware, d.FirmwareRevision) || Differs(s.ModelName, d.ModelName))
                findings.Add(new(FindingSeverity.Info,
                    $"Device {s.Instance} \"{s.Name}\" has different firmware or model",
                    $"Saved: {s.ModelName ?? "?"} {s.Firmware ?? "?"}. Now: {d.ModelName ?? "?"} {d.FirmwareRevision ?? "?"}.",
                    "The controller was updated or replaced.",
                    "Check the change was intended."));
        }

        foreach (var d in now.OrderBy(d => d.InstanceId).Where(d => !savedIds.Contains(d.InstanceId)))
            findings.Add(new(FindingSeverity.Info,
                $"Device {d.InstanceId}{(string.IsNullOrEmpty(d.ObjectName) ? "" : $" \"{d.ObjectName}\"")} is new",
                $"It answers from {d.AddressText} but was not in the job.",
                "It was added since the job was saved, or it was not reachable then.",
                "Save the job again to include it."));

        return findings;
    }

    /// <summary>"10.1.2.3:47808" gives "10.1.2.3"; anything that is not an IPv4 address and port is returned unchanged.</summary>
    public static string HostOf(string address)
    {
        var colon = address.LastIndexOf(':');
        return colon > 0 && address[..colon].Count(c => c == '.') == 3 ? address[..colon] : address;
    }

    private static bool Differs(string? a, string? b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a != b;

    /// <summary>One line for a summary badge, e.g. "1 problem, 2 warnings" or "No conflicts found".</summary>
    public static string Summarize(IReadOnlyList<NetworkFinding> findings)
    {
        if (findings.Count == 0) return "No conflicts found";
        var parts = new List<string>();
        var problems = findings.Count(f => f.Severity == FindingSeverity.Problem);
        var warnings = findings.Count(f => f.Severity == FindingSeverity.Warning);
        var infos = findings.Count(f => f.Severity == FindingSeverity.Info);
        if (problems > 0) parts.Add(problems == 1 ? "1 problem" : $"{problems} problems");
        if (warnings > 0) parts.Add(warnings == 1 ? "1 warning" : $"{warnings} warnings");
        if (infos > 0) parts.Add(infos == 1 ? "1 note" : $"{infos} notes");
        return string.Join(", ", parts);
    }
}
