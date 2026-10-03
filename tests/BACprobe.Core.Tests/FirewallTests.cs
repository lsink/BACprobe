using BACprobe.Core.Networking;

namespace BACprobe.Core.Tests;

public class FirewallTests
{
    private const string Me = @"C:\Tools\BACprobe\BACprobe.App.exe";
    private const int Port = 47808;

    private static FirewallRule Rule(string name = "rule", bool allow = true, int protocol = FirewallMatch.Udp, string? ports = "*",
        string? app = null, FirewallProfiles profiles = FirewallProfiles.All, bool enabled = true, string? service = null) =>
        new(name, enabled, true, allow, protocol, ports, app, profiles, service);

    private static FirewallFacts Facts(params FirewallRule[] rules) =>
        new(true, null, FirewallProfiles.Public, NetworkCategory.Public, FirewallOn: true, BlockAllInbound: false,
            DefaultInboundAllow: false, rules, Me);

    // --- ports ---

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("*", true)]
    [InlineData("47808", true)]
    [InlineData("80,47808,502", true)]
    [InlineData("47800-47823", true)]
    [InlineData("80, 47808-47810", true)]
    [InlineData("47809", false)]
    [InlineData("47809-47823", false)]
    [InlineData("RPC", false)]
    [InlineData("RPC-EPMap", false)]
    public void Port_lists_are_understood(string? ports, bool covers) =>
        Assert.Equal(covers, FirewallMatch.CoversPort(ports, Port));

    // --- which rules apply ---

    [Fact]
    public void A_rule_for_this_program_applies_and_one_for_another_program_does_not()
    {
        Assert.True(FirewallMatch.Applies(Rule(app: Me), FirewallProfiles.Public, Port, Me));
        Assert.True(FirewallMatch.Applies(Rule(app: Me.ToLowerInvariant()), FirewallProfiles.Public, Port, Me)); // Windows stores paths in any case
        Assert.False(FirewallMatch.Applies(Rule(app: @"C:\Other\yabe.exe"), FirewallProfiles.Public, Port, Me));
        Assert.True(FirewallMatch.Applies(Rule(app: null), FirewallProfiles.Public, Port, Me)); // every program
    }

    [Fact]
    public void Environment_variables_in_rule_paths_are_expanded()
    {
        var windir = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        Assert.True(FirewallMatch.SamePath(@"%SystemRoot%\notepad.exe", Path.Combine(windir, "notepad.exe")));
    }

    [Fact]
    public void Rules_that_do_not_cover_this_traffic_are_ignored()
    {
        Assert.False(FirewallMatch.Applies(Rule(profiles: FirewallProfiles.Private), FirewallProfiles.Public, Port, Me));
        Assert.False(FirewallMatch.Applies(Rule(protocol: 6), FirewallProfiles.Public, Port, Me)); // TCP
        Assert.False(FirewallMatch.Applies(Rule(enabled: false), FirewallProfiles.Public, Port, Me));
        Assert.False(FirewallMatch.Applies(Rule(ports: "53"), FirewallProfiles.Public, Port, Me));
        Assert.False(FirewallMatch.Applies(Rule(service: "Dnscache"), FirewallProfiles.Public, Port, Me));
        Assert.True(FirewallMatch.Applies(Rule(protocol: FirewallMatch.AnyProtocol), FirewallProfiles.Public, Port, Me));
    }

    [Fact]
    public void An_unknown_program_path_matches_only_rules_for_every_program()
    {
        Assert.False(FirewallMatch.Applies(Rule(app: Me), FirewallProfiles.Public, Port, null));
        Assert.True(FirewallMatch.Applies(Rule(app: null), FirewallProfiles.Public, Port, null));
    }

    // --- the check ---

    [Fact]
    public void An_allow_rule_passes_and_names_the_rule()
    {
        var r = PreflightRules.CheckFirewall(Facts(Rule("BACprobe.App", app: Me)));
        Assert.Equal(PreflightSeverity.Pass, r.Severity);
        Assert.Contains("BACprobe.App", r.Message);
    }

    [Fact]
    public void A_block_rule_wins_over_an_allow_rule_and_explains_the_windows_prompt()
    {
        var r = PreflightRules.CheckFirewall(Facts(Rule("BACnet", ports: "47808"), Rule("BACprobe.App", allow: false, app: Me)));
        Assert.Equal(PreflightSeverity.Warning, r.Severity);
        Assert.Contains("\"BACprobe.App\" blocks", r.Message);
        Assert.Contains("Don't allow", r.LikelyCause);
        Assert.Contains("still waiting", r.LikelyCause); // the rule Windows adds while its question is on screen looks the same
        Assert.Contains("Allow an app through firewall", r.NextStep);
        Assert.Contains("tick Public", r.NextStep);
    }

    [Fact]
    public void A_block_rule_for_every_program_says_to_ask_it()
    {
        var r = PreflightRules.CheckFirewall(Facts(Rule("Block BACnet", allow: false, ports: "47808")));
        Assert.Equal(PreflightSeverity.Warning, r.Severity);
        Assert.Contains("every program", r.LikelyCause);
        Assert.Contains("\"Block BACnet\"", r.NextStep);
    }

    [Fact]
    public void No_rule_and_default_block_warns_with_the_netsh_command_and_the_public_tip()
    {
        var r = PreflightRules.CheckFirewall(Facts());
        Assert.Equal(PreflightSeverity.Warning, r.Severity);
        Assert.Contains("broadcast their replies", r.LikelyCause);
        Assert.Contains("netsh advfirewall firewall add rule", r.NextStep);
        Assert.Contains("localport=47808", r.NextStep);
        Assert.Contains("Private", r.NextStep);
    }

    [Fact]
    public void A_private_network_gets_no_public_tip()
    {
        var r = PreflightRules.CheckFirewall(Facts() with { Profile = FirewallProfiles.Private, Category = NetworkCategory.Private });
        Assert.Equal(PreflightSeverity.Warning, r.Severity);
        Assert.Contains("Private networks", r.Message);
        Assert.DoesNotContain("setting it to Private", r.NextStep);
    }

    [Fact]
    public void An_unidentified_network_is_explained_as_treated_as_public()
    {
        var r = PreflightRules.CheckFirewall(Facts() with { Category = null });
        Assert.Contains("has not identified this network", r.Message);
        Assert.DoesNotContain("has not identified", PreflightRules.CheckFirewall(Facts()).Message);
    }

    [Fact]
    public void Default_allow_passes()
    {
        var r = PreflightRules.CheckFirewall(Facts() with { DefaultInboundAllow = true });
        Assert.Equal(PreflightSeverity.Pass, r.Severity);
    }

    [Fact]
    public void Firewall_off_passes_with_a_note_about_other_security_software()
    {
        var r = PreflightRules.CheckFirewall(Facts(Rule(allow: false)) with { FirewallOn = false });
        Assert.Equal(PreflightSeverity.Pass, r.Severity);
        Assert.Contains("Another security program", r.Message);
    }

    [Fact]
    public void Block_all_incoming_overrides_allow_rules()
    {
        var r = PreflightRules.CheckFirewall(Facts(Rule(app: Me)) with { BlockAllInbound = true });
        Assert.Equal(PreflightSeverity.Warning, r.Severity);
        Assert.Contains("ALL incoming", r.Message);
        Assert.NotNull(r.NextStep);
    }

    [Fact]
    public void Unreadable_settings_warn_but_never_block_scanning()
    {
        var r = PreflightRules.CheckFirewall(FirewallFacts.Unreadable("access denied"));
        Assert.Equal(PreflightSeverity.Warning, r.Severity);
        Assert.Contains("access denied", r.Message);
        Assert.NotNull(r.NextStep);
    }

    [Fact]
    public void The_firewall_check_is_never_a_failure_so_it_cannot_stop_a_scan()
    {
        FirewallFacts[] cases =
        [
            Facts(), Facts(Rule(allow: false, app: Me)), Facts() with { BlockAllInbound = true }, FirewallFacts.Unreadable("x"),
        ];
        Assert.All(cases, f => Assert.NotEqual(PreflightSeverity.Fail, PreflightRules.CheckFirewall(f).Severity));
    }
}
