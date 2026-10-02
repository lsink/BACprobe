namespace BACprobe.Core.Networking;

public static class Preflight
{
    /// <summary>Probe the machine and apply the pre-flight rules for the chosen adapter.</summary>
    public static IReadOnlyList<PreflightResult> Run(AdapterInfo adapter, IReadOnlyList<AdapterInfo>? all = null) =>
        PreflightRules.Evaluate(adapter, all ?? AdapterEnumerator.GetAdapters(), UdpPortInspector.Probe());
}
