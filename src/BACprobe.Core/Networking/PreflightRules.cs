using System.Net;

namespace BACprobe.Core.Networking;

/// <summary>Facts gathered from the machine; the rules below are pure functions of these.</summary>
public sealed record PortProbe(bool Bindable, string? BindError, IReadOnlyList<PortOwner> Owners);

public sealed record PortOwner(int ProcessId, string ProcessName, string LocalAddress);

/// <summary>Pure pre-flight rules, kept free of I/O so they can be unit tested.</summary>
public static class PreflightRules
{
    public const int BacnetPort = 47808;

    /// <param name="firewall">Windows Firewall facts; null skips the firewall check.</param>
    public static IReadOnlyList<PreflightResult> Evaluate(
        AdapterInfo adapter, IReadOnlyList<AdapterInfo> allAdapters, PortProbe port, FirewallFacts? firewall = null)
    {
        List<PreflightResult> results =
        [
            CheckAdapterUp(adapter),
            CheckAddress(adapter),
            CheckVirtual(adapter),
            CheckDuplicateSubnet(adapter, allAdapters),
            CheckPort(port),
        ];
        if (firewall is not null) results.Add(CheckFirewall(firewall));
        return results;
    }

    public static bool CanProceed(IEnumerable<PreflightResult> results) =>
        results.All(r => r.Severity != PreflightSeverity.Fail);

    public static PreflightResult CheckAdapterUp(AdapterInfo a) =>
        a.IsUp
            ? new("Adapter up", PreflightSeverity.Pass, $"{a.Name} is connected.")
            : new("Adapter up", PreflightSeverity.Fail, $"{a.Name} is not connected.",
                "The cable is unplugged, the switch port is down, or the adapter is disabled.",
                "Plug in the network cable (or enable the adapter in Windows network settings) and try again.");

    public static PreflightResult CheckAddress(AdapterInfo a)
    {
        if (Subnet.IsLinkLocal(a.Address))
            return new("IPv4 address", PreflightSeverity.Fail,
                $"{a.Name} has a self-assigned address ({a.Address}).",
                "The PC asked for an address via DHCP and nobody answered, so Windows gave itself a 169.254.x.x address.",
                "Set a static IP on the same subnet as the BACnet devices (ask the site contact for a free address), then retry.");
        if (a.IsLoopback || a.Address.Equals(IPAddress.Any))
            return new("IPv4 address", PreflightSeverity.Fail, $"{a.Name} is not a real network address ({a.Address}).",
                "This is the loopback adapter, which only talks to this PC.",
                "Pick the Ethernet or Wi-Fi adapter that is plugged into the building network.");
        return new("IPv4 address", PreflightSeverity.Pass, $"{a.Address}  ({a.Cidr})");
    }

    public static PreflightResult CheckVirtual(AdapterInfo a) =>
        a.IsVirtual
            ? new("Adapter type", PreflightSeverity.Warning, $"{a.Name} ({a.Description}) looks like a virtual adapter.",
                "VPN, VM, WSL and Bluetooth adapters usually are not on the BACnet network.",
                "If no devices show up, pick the physical Ethernet adapter instead.")
            : new("Adapter type", PreflightSeverity.Pass, "Physical adapter.");

    public static PreflightResult CheckDuplicateSubnet(AdapterInfo a, IReadOnlyList<AdapterInfo> all)
    {
        var clash = all.Where(o => o.IsUp && !o.IsLoopback && !o.Address.Equals(a.Address)
                                   && Subnet.SameSubnet(a.Address, a.Mask, o.Address, o.Mask)).ToList();
        return clash.Count == 0
            ? new("Duplicate subnets", PreflightSeverity.Pass, "No other adapter shares this subnet.")
            : new("Duplicate subnets", PreflightSeverity.Warning,
                $"{string.Join(", ", clash.Select(c => $"{c.Name} ({c.Address})"))} is on the same subnet as {a.Name}.",
                "Two adapters on one subnet make Windows pick either one for traffic, so replies can go to the wrong place.",
                "Disable or unplug the adapter you are not using (Wi-Fi is the usual one).");
    }

    public static PreflightResult CheckPort(PortProbe p)
    {
        // One process often holds several sockets on the port (shared + unicast); name it once.
        var owners = string.Join(", ", p.Owners.DistinctBy(o => o.ProcessId).Select(o => $"{o.ProcessName} (PID {o.ProcessId})"));
        if (!p.Bindable)
            return new("UDP 47808", PreflightSeverity.Fail,
                $"UDP port {BacnetPort} is held exclusively{(owners.Length > 0 ? $" by {owners}" : "")}.",
                "Another BACnet tool (YABE, a vendor workbench, a BBMD service) opened the port without sharing it.",
                "Close that program, then retry." + (p.BindError is null ? "" : $" (Windows said: {p.BindError})"));
        if (p.Owners.Count > 0)
            return new("UDP 47808", PreflightSeverity.Warning,
                $"Port {BacnetPort} is also open in {owners}. Sharing works, but replies may be split between programs.",
                "Another BACnet program is running on this PC.",
                "If discovery looks incomplete, close the other program and retry.");
        return new("UDP 47808", PreflightSeverity.Pass, $"Port {BacnetPort} is free.");
    }

    public const string FirewallCheckName = "Windows Firewall";

    /// <summary>
    /// Will Windows Firewall let BACnet traffic in? Devices broadcast their I-Am replies and push COV notifications unasked,
    /// so unlike most programs BACprobe needs incoming UDP 47808, not just replies to what it sent. Never a Fail: a
    /// third-party firewall or a rule detail BACprobe does not model can change the outcome, so the tech can still scan.
    /// Windows applies block rules before allow rules, and this follows the same order.
    /// </summary>
    public static PreflightResult CheckFirewall(FirewallFacts f, int port = BacnetPort)
    {
        if (!f.Readable)
            return new(FirewallCheckName, PreflightSeverity.Warning, $"Could not read the Windows Firewall settings ({f.ReadError}).",
                "Windows did not let BACprobe look (a company policy, or another security program has replaced Windows Firewall).",
                $"If no devices answer, ask IT whether this PC allows incoming UDP {port}.");

        var where = $"{f.ProfileName} networks";
        // The profile already names the category; only an unidentified network needs saying.
        var category = f.Category is null ? " Windows has not identified this network, so it treats it as Public." : "";
        if (!f.FirewallOn)
            return new(FirewallCheckName, PreflightSeverity.Pass,
                $"Windows Firewall is off for {where}. (Another security program could still block BACnet.)");

        var program = string.IsNullOrEmpty(f.ProgramPath) ? "BACprobe" : Path.GetFileName(f.ProgramPath);
        var allowApp = $"Open Windows Security > Firewall & network protection > Allow an app through firewall, find {program} " +
                       $"and tick {f.ProfileName} (needs an administrator).";
        var publicTip = f.Profile == FirewallProfiles.Public
            ? " If this is the building's own network, setting it to Private in Windows network settings also works."
            : "";

        if (f.BlockAllInbound)
            return new(FirewallCheckName, PreflightSeverity.Warning,
                $"Windows Firewall blocks ALL incoming traffic on {where}, whatever the rules say. Devices' replies will not get in.{category}",
                "\"Block all incoming connections\" is switched on for this network type, often by company policy on Public networks.",
                "Switch it off for this network type in Windows Security > Firewall & network protection (needs an administrator)." + publicTip);

        var applying = f.Rules.Where(r => FirewallMatch.Applies(r, f.Profile, port, f.ProgramPath)).ToList();
        if (applying.FirstOrDefault(r => !r.Allow) is { } block)
            return new(FirewallCheckName, PreflightSeverity.Warning,
                $"Firewall rule \"{block.Name}\" blocks incoming BACnet (UDP {port}) on {where}.{category}",
                string.IsNullOrEmpty(block.Application)
                    ? "Someone added a rule that blocks this port for every program."
                    : "Windows asked whether this program may use the network and either the answer was Cancel or \"Don't allow\", " +
                      "or the question is still waiting (look for a Windows Security window, perhaps behind this one).",
                (string.IsNullOrEmpty(block.Application)
                    ? $"Ask IT (or an administrator) to remove or disable the rule \"{block.Name}\" in Windows Defender Firewall."
                    : allowApp) + publicTip);

        if (applying.FirstOrDefault(r => r.Allow) is { } allow)
            return new(FirewallCheckName, PreflightSeverity.Pass, $"Incoming BACnet is allowed on {where} (rule \"{allow.Name}\").");

        if (f.DefaultInboundAllow)
            return new(FirewallCheckName, PreflightSeverity.Pass, $"Windows Firewall lets incoming traffic in by default on {where}.");

        return new(FirewallCheckName, PreflightSeverity.Warning,
            $"No Windows Firewall rule lets BACnet replies in on {where}.{category}",
            "Windows blocks traffic nobody asked for, and BACnet devices broadcast their replies to Who-Is, so the replies can be dropped " +
            "even though the request went out.",
            $"If Windows asks whether {program} may use the network, choose Allow and tick {f.ProfileName}. Otherwise: " + allowApp + $" Or, as administrator: netsh advfirewall firewall add rule name=\"BACnet/IP\" dir=in action=allow protocol=UDP localport={port}" +
            publicTip);
    }
}
