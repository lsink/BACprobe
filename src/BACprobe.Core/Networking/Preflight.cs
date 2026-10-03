namespace BACprobe.Core.Networking;

public static class Preflight
{
    /// <summary>Probe the machine and apply the pre-flight rules for the chosen adapter.</summary>
    public static IReadOnlyList<PreflightResult> Run(AdapterInfo adapter, IReadOnlyList<AdapterInfo>? all = null)
    {
        // Firewall first: the port probe opens UDP 47808, which can make Windows ask "allow this app?" and add a temporary
        // block rule while it waits. Reading afterwards would report that rule as if the tech had already said no.
        var firewall = FirewallInspector.Inspect(adapter);
        return PreflightRules.Evaluate(adapter, all ?? AdapterEnumerator.GetAdapters(), UdpPortInspector.Probe(), firewall);
    }
}
