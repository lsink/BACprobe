using System.Net;

namespace BACprobe.Core.Networking;

/// <summary>Facts gathered from the machine; the rules below are pure functions of these.</summary>
public sealed record PortProbe(bool Bindable, string? BindError, IReadOnlyList<PortOwner> Owners);

public sealed record PortOwner(int ProcessId, string ProcessName, string LocalAddress);

/// <summary>Pure pre-flight rules, kept free of I/O so they can be unit tested.</summary>
public static class PreflightRules
{
    public const int BacnetPort = 47808;

    public static IReadOnlyList<PreflightResult> Evaluate(
        AdapterInfo adapter, IReadOnlyList<AdapterInfo> allAdapters, PortProbe port) =>
    [
        CheckAdapterUp(adapter),
        CheckAddress(adapter),
        CheckVirtual(adapter),
        CheckDuplicateSubnet(adapter, allAdapters),
        CheckPort(port),
    ];

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
        var owners = string.Join(", ", p.Owners.Select(o => $"{o.ProcessName} (PID {o.ProcessId})"));
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
}
