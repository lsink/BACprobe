namespace BACprobe.Core.Networking;

/// <summary>How Windows classifies a network. Windows Firewall keeps separate settings for each.</summary>
public enum NetworkCategory { Public = 0, Private = 1, Domain = 2 }

/// <summary>Windows Firewall profile bits (NET_FW_PROFILE_TYPE2). A rule applies to every profile whose bit it has.</summary>
[Flags]
public enum FirewallProfiles { None = 0, Domain = 1, Private = 2, Public = 4, All = 0x7FFFFFFF }

/// <summary>One Windows Firewall rule, reduced to what decides whether BACnet traffic gets in.</summary>
/// <param name="Protocol">IANA protocol number: 17 is UDP, 256 means any protocol.</param>
/// <param name="LocalPorts">"*" or empty for any; otherwise numbers, ranges ("47808-47823") and keywords, comma-separated.</param>
/// <param name="Application">Full path of the program the rule is limited to; empty for every program.</param>
/// <param name="Service">Windows service the rule is limited to; empty for none. Such a rule never applies to BACprobe.</param>
public sealed record FirewallRule(
    string Name, bool Enabled, bool Inbound, bool Allow, int Protocol, string? LocalPorts, string? Application,
    FirewallProfiles Profiles, string? Service = null);

/// <summary>
/// What Windows Firewall will do with BACnet traffic arriving on the chosen adapter. Gathered by <see cref="FirewallInspector"/>;
/// judged by <see cref="PreflightRules.CheckFirewall"/>.
/// </summary>
/// <param name="Readable">False when the settings could not be read (not Windows, policy, a third-party firewall); see <paramref name="ReadError"/>.</param>
/// <param name="Profile">The one profile that applies to the chosen adapter.</param>
/// <param name="Category">The adapter's network category, when Windows said; null if the profile was inferred.</param>
/// <param name="ProgramPath">This program's own path, so rules Windows made for it (its "allow this app?" prompt) can be recognised.</param>
public sealed record FirewallFacts(
    bool Readable,
    string? ReadError,
    FirewallProfiles Profile,
    NetworkCategory? Category,
    bool FirewallOn,
    bool BlockAllInbound,
    bool DefaultInboundAllow,
    IReadOnlyList<FirewallRule> Rules,
    string? ProgramPath)
{
    public static FirewallFacts Unreadable(string error) =>
        new(false, error, FirewallProfiles.None, null, false, false, false, [], null);

    public string ProfileName => Profile switch
    {
        FirewallProfiles.Domain => "Domain",
        FirewallProfiles.Private => "Private",
        FirewallProfiles.Public => "Public",
        _ => "this",
    };
}

/// <summary>Pure matching of firewall rules against "UDP to this port, for this program, on this profile".</summary>
public static class FirewallMatch
{
    public const int Udp = 17;
    public const int AnyProtocol = 256;

    /// <summary>True if a rule's local-port list includes <paramref name="port"/>. Keywords such as "RPC" never match a plain port.</summary>
    public static bool CoversPort(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts)) return true;
        foreach (var raw in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*") return true;
            var dash = raw.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(raw[..dash], out var lo) && int.TryParse(raw[(dash + 1)..], out var hi) && port >= lo && port <= hi)
                    return true;
            }
            else if (int.TryParse(raw, out var single) && single == port) return true;
        }
        return false;
    }

    /// <summary>
    /// True if the rule decides incoming UDP to <paramref name="port"/> for this program on <paramref name="profile"/>.
    /// Address, interface-type and edge-traversal limits are not considered: BACnet devices are normally on the local subnet.
    /// </summary>
    public static bool Applies(FirewallRule r, FirewallProfiles profile, int port, string? programPath) =>
        r.Enabled && r.Inbound
        && r.Protocol is Udp or AnyProtocol
        && (r.Profiles & profile) != 0
        && string.IsNullOrEmpty(r.Service)
        && CoversPort(r.LocalPorts, port)
        && (string.IsNullOrEmpty(r.Application) || SamePath(r.Application, programPath));

    /// <summary>Rules store paths with environment variables and in any case.</summary>
    public static bool SamePath(string rulePath, string? programPath)
    {
        if (string.IsNullOrEmpty(programPath)) return false;
        try
        {
            var a = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rulePath.Trim()));
            var b = Path.GetFullPath(programPath);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
