using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BACprobe.Core.Networking;

/// <summary>Windows-only: reads Windows Firewall settings and the adapter's network category, for the pre-flight firewall check.</summary>
public static class FirewallInspector
{
    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    /// <summary>Never throws: anything it cannot read comes back as <see cref="FirewallFacts.Unreadable"/>.</summary>
    public static FirewallFacts Inspect(AdapterInfo adapter, string? programPath = null)
    {
        if (!OperatingSystem.IsWindows()) return FirewallFacts.Unreadable("this is not Windows");
        try
        {
            return InspectWindows(adapter, programPath ?? Environment.ProcessPath);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentException)
        {
            return FirewallFacts.Unreadable(ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static FirewallFacts InspectWindows(AdapterInfo adapter, string? programPath)
    {
        var category = CategoryOf(adapter);
        // Windows files a network it cannot identify (a building LAN with a static IP and no gateway) as Public.
        var profile = category switch
        {
            NetworkCategory.Domain => FirewallProfiles.Domain,
            NetworkCategory.Private => FirewallProfiles.Private,
            _ => FirewallProfiles.Public,
        };

        // Late-bound COM (HNetCfg.FwPolicy2): the same object Windows Security uses. Reading needs no administrator rights.
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
        var p = (int)profile;
        bool on = policy.FirewallEnabled[p];
        bool blockAll = policy.BlockAllInboundTraffic[p];
        int defaultInbound = policy.DefaultInboundAction[p]; // 0 = block, 1 = allow

        var rules = new List<FirewallRule>();
        foreach (dynamic r in policy.Rules)
        {
            if ((int)r.Direction != 1) continue; // 1 = inbound: replies and notifications coming to us
            int protocol = r.Protocol;
            if (protocol is not (FirewallMatch.Udp or FirewallMatch.AnyProtocol)) continue;
            rules.Add(new FirewallRule((string)r.Name ?? "", (bool)r.Enabled, Inbound: true, Allow: (int)r.Action == 1, protocol,
                (string?)r.LocalPorts, (string?)r.ApplicationName, (FirewallProfiles)(int)r.Profiles, (string?)r.ServiceName));
        }

        return new FirewallFacts(true, null, profile, category, on, blockAll, defaultInbound == 1, rules, programPath);
    }

    /// <summary>The network category Windows gave the adapter, or null if it is not listed (for example, unplugged).</summary>
    [SupportedOSPlatform("windows")]
    private static NetworkCategory? CategoryOf(AdapterInfo adapter)
    {
        if (!Guid.TryParse(adapter.Id, out var adapterId)) return null;
        dynamic manager = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManagerClsid, throwOnError: true)!)!;
        foreach (dynamic connection in manager.GetNetworkConnections())
        {
            // GetAdapterId returns a GUID, which late binding cannot carry: ask through the typed interface instead.
            if (((INetworkConnection)connection).GetAdapterId() != adapterId) continue;
            int category = connection.GetNetwork().GetCategory();
            return Enum.IsDefined((NetworkCategory)category) ? (NetworkCategory)category : null;
        }
        return null;
    }

    /// <summary>netlistmgr.h INetworkConnection. Only the method order matters; the rest is reached late-bound.</summary>
    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [return: MarshalAs(UnmanagedType.IDispatch)] object GetNetwork();
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
        int GetDomainType();
    }
}
